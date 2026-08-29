using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using BepInEx;
using BepInEx.Configuration;
using HarmonyLib;
using UnityEngine;

namespace FollowPlayer
{
    [BepInPlugin(PluginInfo.PLUGIN_GUID, PluginInfo.PLUGIN_NAME, PluginInfo.PLUGIN_VERSION)]
    public class FollowPlayerPlugin : BaseUnityPlugin
    {
        internal static ConfigEntry<float> StopDistance;
        internal static ConfigEntry<float> RunDistance;

        internal static bool FollowActive;
        internal static string TargetName;

        private Harmony _harmony;

        private void Awake()
        {
            StopDistance = Config.Bind("General", "StopDistance", 4f,
                new ConfigDescription("Stop this many meters short of the target.",
                    new AcceptableValueRange<float>(1f, 30f)));
            RunDistance = Config.Bind("General", "RunDistance", 8f,
                new ConfigDescription("Beyond this distance the follower runs.",
                    new AcceptableValueRange<float>(2f, 60f)));

            RegisterCommand("follow", "Toggle following the current target on or off.", args =>
            {
                var me = Player.m_localPlayer;
                if (me == null) return;

                FollowActive = !FollowActive;
                if (FollowActive && string.IsNullOrEmpty(TargetName))
                    TargetName = FacingName(me);
                me.Message(MessageHud.MessageType.Center,
                    FollowActive ? "Follow: ON " + (TargetName ?? "(none)") : "Follow: OFF");
            });

            RegisterCommand("follownext", "Cycle which nearby player to follow.", args =>
            {
                var me = Player.m_localPlayer;
                if (me == null) return;

                CycleTarget(me);
                me.Message(MessageHud.MessageType.Center, "Target: " + (TargetName ?? "(none)"));
            });

            _harmony = Harmony.CreateAndPatchAll(Assembly.GetExecutingAssembly(), PluginInfo.PLUGIN_GUID);
            Logger.LogInfo($"{PluginInfo.PLUGIN_NAME} {PluginInfo.PLUGIN_VERSION} loaded.");
        }

        private void OnDestroy() => _harmony?.UnpatchSelf();

        private GUIStyle _followLabelStyle;

        private void OnGUI()
        {
            if (!FollowActive) return;

            if (_followLabelStyle == null)
            {
                _followLabelStyle = new GUIStyle(GUI.skin.label)
                {
                    alignment = TextAnchor.UpperRight,
                    fontSize = 16,
                    fontStyle = FontStyle.Bold
                };
                _followLabelStyle.normal.textColor = Color.white;
            }

            GUI.Label(new Rect(Screen.width - 320, 10, 300, 30),
                "Following: " + (TargetName ?? "(none)"), _followLabelStyle);
        }

        // Terminal.ConsoleCommand's constructor has picked up extra optional parameters across
        // game updates, so a direct `new Terminal.ConsoleCommand(...)` call embeds an exact
        // compile-time signature that breaks (MissingMethodException) the moment the NuGet
        // reference package's snapshot drifts from the player's actual installed game version.
        // Finding the real constructor via reflection and padding trailing args with their
        // declared defaults avoids hard-binding to one exact parameter list.
        private static void RegisterCommand(string command, string description, Terminal.ConsoleEvent action)
        {
            var ctor = typeof(Terminal.ConsoleCommand).GetConstructors()
                .FirstOrDefault(c =>
                {
                    var p = c.GetParameters();
                    return p.Length >= 3
                        && p[0].ParameterType == typeof(string)
                        && p[1].ParameterType == typeof(string)
                        && p[2].ParameterType == typeof(Terminal.ConsoleEvent);
                });
            if (ctor == null)
            {
                Debug.LogError("FollowPlayer: no matching Terminal.ConsoleCommand constructor found.");
                return;
            }

            var parameters = ctor.GetParameters();
            var args = new object[parameters.Length];
            args[0] = command;
            args[1] = description;
            args[2] = action;
            for (int i = 3; i < parameters.Length; i++)
            {
                var p = parameters[i];
                args[i] = p.HasDefaultValue ? p.DefaultValue
                    : (p.ParameterType.IsValueType ? Activator.CreateInstance(p.ParameterType) : null);
            }

            ctor.Invoke(args);
        }

        // Picks whichever nearby player is most in front of the camera, rather than just the
        // closest one, so the right person gets picked when several players are standing around.
        private static string FacingName(Player me)
        {
            var cam = Camera.main;
            Vector3 forward = cam != null ? cam.transform.forward : me.transform.forward;
            forward.y = 0f;
            if (forward.sqrMagnitude < 0.0001f) forward = me.transform.forward;
            forward.Normalize();

            Player best = null;
            float bestDot = float.NegativeInfinity;
            foreach (var p in Player.GetAllPlayers())
            {
                if (p == me) continue;
                Vector3 to = p.transform.position - me.transform.position;
                to.y = 0f;
                if (to.sqrMagnitude < 0.0001f) continue;
                float dot = Vector3.Dot(to.normalized, forward);
                if (dot > bestDot) { bestDot = dot; best = p; }
            }
            return best != null ? best.GetPlayerName() : null;
        }

        private static void CycleTarget(Player me)
        {
            var others = new List<Player>();
            foreach (var p in Player.GetAllPlayers())
                if (p != me) others.Add(p);
            if (others.Count == 0) { TargetName = null; return; }
            int idx = others.FindIndex(p => p.GetPlayerName() == TargetName);
            TargetName = others[(idx + 1) % others.Count].GetPlayerName();
        }

        internal static Player ResolveTarget(Player me)
        {
            if (string.IsNullOrEmpty(TargetName)) return null;
            foreach (var p in Player.GetAllPlayers())
                if (p != me && p.GetPlayerName() == TargetName) return p;
            return null;
        }
    }

    // Harmony matches prefix params by NAME, so only the ones we overwrite are declared.
    [HarmonyPatch(typeof(Player), nameof(Player.SetControls))]
    internal static class Player_SetControls_Patch
    {
        // m_lookDir is private at runtime even though the compile-time GameLibs stub exposes it
        // as public; a plain field access compiles but throws FieldAccessException in-game, so
        // it has to go through an accessor that explicitly skips the visibility check.
        private static readonly AccessTools.FieldRef<Character, Vector3> LookDirRef =
            AccessTools.FieldRefAccess<Character, Vector3>("m_lookDir");

        private static void Prefix(Player __instance, ref Vector3 movedir, bool attack, bool attackHold,
            bool secondaryAttack, bool secondaryAttackHold, ref bool run, ref bool autoRun)
        {
            if (!FollowPlayerPlugin.FollowActive) return;
            if (__instance != Player.m_localPlayer) return;

            // Manual movement or an attack/block click turns following off entirely; re-enable
            // with /follow. Jump, crouch, etc. aren't checked here, so they don't cancel it.
            if (movedir.sqrMagnitude > 0.0001f || attack || attackHold || secondaryAttack || secondaryAttackHold)
            {
                FollowPlayerPlugin.FollowActive = false;
                __instance.Message(MessageHud.MessageType.Center, "Follow: OFF");
                return;
            }

            var target = FollowPlayerPlugin.ResolveTarget(__instance);
            if (target == null) return;

            Vector3 to = target.transform.position - __instance.transform.position;
            to.y = 0f;
            float dist = to.magnitude;

            if (dist <= FollowPlayerPlugin.StopDistance.Value)
            {
                movedir = Vector3.zero;
                run = false;
                autoRun = false;
                return;
            }

            // SetControls treats movedir as local (z = forward, x = right) relative to the
            // character's current flat look direction, not a world-space vector, so the desired
            // world bearing has to be projected onto that basis first.
            Vector3 worldDir = to.normalized;
            Vector3 forward = LookDirRef(__instance);
            forward.y = 0f;
            forward.Normalize();
            Vector3 right = Vector3.Cross(Vector3.up, forward);

            movedir = new Vector3(Vector3.Dot(worldDir, right), 0f, Vector3.Dot(worldDir, forward));
            run = dist > FollowPlayerPlugin.RunDistance.Value;
            autoRun = false;
        }
    }
}
