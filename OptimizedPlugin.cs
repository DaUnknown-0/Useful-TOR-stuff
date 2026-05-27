// TOR Optimized - Copyright (C) 2026 DaUnknown-0
// Licensed under GPL-3.0-or-later. See LICENSE for details.
// Based on The Other Roles (https://github.com/TheOtherRolesAU/TheOtherRoles), GPL-3.0.

/*
 * OptimizedPlugin - External performance fix for TOR 4.8.0.
 *
 * Bloody modifier lag: TOR's PlayerControlPatch.bloodyUpdate() spawns a brand-new
 * Bloodytrail (GameObject + SpriteRenderer + 10s coroutine) for every bloody player
 * on EVERY FixedUpdate (~50 Hz). With a 10s lifetime that's up to ~500 live blood
 * GameObjects per player, which tanks the framerate.
 *
 * Fix: patch the Bloodytrail constructor and skip it (return false) unless the player
 * has actually moved at least MinDropDistance since their last accepted blood drop.
 * This caps blood to roughly one drop per MinDropDistance travelled and also looks
 * more natural (blood only along the walked path). bloodyUpdate() itself is left
 * untouched, so TOR's per-tick Bloody.active countdown — and thus the effect duration —
 * is unchanged.
 *
 * Strategy: minimal, defensive patches via reflection so no compile-time TOR reference
 * is needed; if TOR changes its internals the patch simply becomes a no-op.
 */

global using Il2CppInterop.Runtime;
global using Il2CppInterop.Runtime.Attributes;
global using Il2CppInterop.Runtime.InteropTypes;
global using Il2CppInterop.Runtime.InteropTypes.Arrays;
global using Il2CppInterop.Runtime.Injection;

using BepInEx;
using BepInEx.Configuration;
using BepInEx.Logging;
using BepInEx.Unity.IL2CPP;
using HarmonyLib;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using UnityEngine;

namespace Optimized;

[BepInPlugin(PluginGuid, PluginName, PluginVersion)]
[BepInProcess("Among Us.exe")]
[BepInDependency("me.eisbison.theotherroles", BepInDependency.DependencyFlags.HardDependency)]
public class OptimizedPlugin : BasePlugin
{
    public const string PluginGuid = "com.tormod.optimized";
    public const string PluginName = "TOR Optimized";
    public const string PluginVersion = "1.0.0";
    public static readonly System.Version Version = System.Version.Parse(PluginVersion);

    public static ManualLogSource Logger { get; private set; }

    internal static Assembly TORAssembly;
    internal static ConfigEntry<float> MinDropDistance;

    public override void Load()
    {
        Logger = Log;
        Logger.LogInfo($"{PluginName} v{PluginVersion} loading...");

        MinDropDistance = Config.Bind(
            "Bloody", "MinDropDistance", 0.35f,
            "Minimum distance (in world units) a bloody player must travel before a new blood " +
            "trail drop is spawned. Higher = fewer blood objects = less lag. 0 disables throttling.");

        var harmony = new Harmony(PluginGuid);

        PatchBloodyThrottle(harmony);
        harmony.PatchAll(typeof(VersionDisplayPatch));

        // Self-updater: checks GitHub releases and offers an in-game update button.
        AddComponent<OptimizedUpdater>();

        Logger.LogInfo($"{PluginName} v{PluginVersion} loaded.");
    }

    // ========================================================================
    // Bloody throttle: patch the Bloodytrail constructor (resolved via reflection,
    // since the type is internal to TOR) and skip construction when the player hasn't
    // moved far enough since their last drop.
    // ========================================================================

    private void PatchBloodyThrottle(Harmony harmony)
    {
        try
        {
            TORAssembly = AppDomain.CurrentDomain.GetAssemblies()
                .FirstOrDefault(a => a.GetName().Name == "TheOtherRoles");
            if (TORAssembly == null)
            {
                Logger.LogError("TheOtherRoles assembly not found — Bloody throttle disabled.");
                return;
            }

            var bloodytrailType = TORAssembly.GetType("TheOtherRoles.Objects.Bloodytrail");
            if (bloodytrailType == null)
            {
                Logger.LogWarning("Bloodytrail type not found — Bloody throttle disabled.");
                return;
            }

            var ctor = bloodytrailType.GetConstructor(
                BindingFlags.Public | BindingFlags.Instance, null,
                new[] { typeof(PlayerControl), typeof(PlayerControl) }, null);
            if (ctor == null)
            {
                Logger.LogWarning("Bloodytrail(PlayerControl, PlayerControl) constructor not found — Bloody throttle disabled.");
                return;
            }

            harmony.Patch(ctor,
                prefix: new HarmonyMethod(typeof(BloodyThrottlePatch), nameof(BloodyThrottlePatch.Prefix)));
            Logger.LogInfo("Patched Bloodytrail constructor — blood drops are now distance-throttled.");
        }
        catch (Exception ex)
        {
            Logger.LogError($"Failed to patch Bloodytrail: {ex}");
        }
    }

    public static class BloodyThrottlePatch
    {
        // Last accepted blood-drop position per player id.
        private static readonly Dictionary<byte, Vector2> _lastDropPos = new Dictionary<byte, Vector2>();

        // __0 = the "player" argument of Bloodytrail(player, bloodyPlayer). Returning false skips the
        // original constructor body, so no blood GameObject is created this tick.
        public static bool Prefix(PlayerControl __0)
        {
            try
            {
                if (__0 == null) return true;
                float minDist = MinDropDistance != null ? MinDropDistance.Value : 0.35f;
                if (minDist <= 0f) return true; // throttling disabled

                byte id = __0.PlayerId;
                Vector3 p = __0.transform.position;
                Vector2 pos = new Vector2(p.x, p.y);

                if (_lastDropPos.TryGetValue(id, out Vector2 last)
                    && Vector2.Distance(pos, last) < minDist)
                {
                    return false; // not far enough — skip this drop
                }

                _lastDropPos[id] = pos;
                return true;
            }
            catch
            {
                return true; // never block TOR on our account
            }
        }
    }

    // ========================================================================
    // Version display: add an "Optimized vX.Y.Z" line to the top-corner PingTracker.
    // Inserted right after TOR's own "TheOtherRoles vX" line; the Chance and Host Fix
    // lines stack below it, so nothing overlaps.
    // ========================================================================

    [HarmonyPatch(typeof(PingTracker), nameof(PingTracker.Update))]
    [HarmonyPriority(Priority.Low)] // run after TOR's own PingTracker postfix
    public static class VersionDisplayPatch
    {
        public static void Postfix(PingTracker __instance)
        {
            if (__instance == null || __instance.text == null) return;
            string text = __instance.text.text;
            if (string.IsNullOrEmpty(text)) return;

            string line = $"<color=#3FCF4A>Optimized</color> v{PluginVersion}";
            int nl = text.IndexOf('\n');
            text = nl >= 0
                ? text.Substring(0, nl + 1) + line + "\n" + text.Substring(nl + 1)
                : text + "\n" + line;

            __instance.text.text = text;
        }
    }
}
