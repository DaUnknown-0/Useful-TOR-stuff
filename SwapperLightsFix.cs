// Useful TOR Stuff - Copyright (C) 2026 DaUnknown-0
// Licensed under GPL-3.0-or-later. See LICENSE for details.
// Based on The Other Roles (https://github.com/TheOtherRolesAU/TheOtherRoles), GPL-3.0.

/*
 * SwapperLightsFix - new Swapper options "Swapper Can Fix Lights" and "Swapper Can Fix Comms".
 *
 * TOR deliberately forbids the Swapper from interacting with the lights AND comms panels via three
 * patches in TheOtherRoles.Patches (UsablesPatch.cs):
 *   1) ConsoleCanUsePatch.Prefix forces Console.CanUse to canUse=couldUse=false for any
 *      FixLights/FixComms console while the local player is the Swapper.
 *   2) LightsMinigameBeginPatch.Postfix immediately Close()s SwitchMinigame (lights).
 *   3) CommsMinigameBeginPatch.Postfix immediately Close()s TuneRadioMinigame (comms).
 *
 * Each panel gets its own independent option (default OFF). When an option is ON we re-allow that
 * panel for the local Swapper, without touching TOR's source:
 *   - A Postfix on Console.CanUse re-computes a normal distance-based usability for the local
 *     Swapper at the matching console (TOR's prefix already set both flags to false).
 *   - To defeat TOR's auto-close we do NOT patch Minigame.Close/TuneRadioMinigame.Close: both are
 *     small parameterless Il2Cpp methods, and detouring them risks the Il2Cpp method-dedup crash
 *     documented for Minigame.Close(bool) elsewhere in this mod (identical native code for two
 *     managed methods gets folded into one, so a detour on one silently detours the other too).
 *     Instead our own high-priority prefix on each minigame's Begin flips Swapper.swapper to null
 *     for the duration of that one Begin call (out __state remembers the real value), so TOR's own
 *     Begin-postfix - which only Close()s the panel when Swapper.swapper == PlayerControl.LocalPlayer
 *     - reads null and does nothing. A Finalizer restores Swapper.swapper right after Begin returns,
 *     so nothing downstream (the rest of the frame, other Swapper checks) ever sees the flip.
 */

using System;
using HarmonyLib;
using UnityEngine;
using TheOtherRoles;
using static TheOtherRoles.TheOtherRoles;
using Types = TheOtherRoles.CustomOption.CustomOptionType;

namespace UsefulTORStuff {
    public static class SwapperLightsFix {
        public static CustomOption LightsOption;  // Off/On toggle (lights)
        public static CustomOption CommsOption;   // Off/On toggle (comms)

        public static void CreateOptions() {
            try {
                LightsOption = CustomOption.Create(
                    1220, Types.Crewmate, "Swapper Can Fix Lights",
                    false, CustomOptionHolder.swapperSpawnRate);
                UTSLocalization.BindOptionTitle(LightsOption, "uts.swapperlightsfix.lights_option");
                CommsOption = CustomOption.Create(
                    1221, Types.Crewmate, "Swapper Can Fix Comms",
                    false, CustomOptionHolder.swapperSpawnRate);
                UTSLocalization.BindOptionTitle(CommsOption, "uts.swapperlightsfix.comms_option");

                var opts = CustomOption.options;
                opts.Remove(LightsOption);
                opts.Remove(CommsOption);
                int idx = opts.IndexOf(CustomOptionHolder.swapperRechargeTasksNumber);
                if (idx < 0) idx = opts.Count - 1;
                opts.Insert(idx + 1, LightsOption);
                opts.Insert(idx + 2, CommsOption);

                UsefulTORStuffPlugin.Logger?.LogInfo("[SwapperLightsFix] Options created under Swapper.");
            } catch (Exception e) {
                UsefulTORStuffPlugin.Logger?.LogError($"[SwapperLightsFix] CreateOptions failed: {e}");
            }
        }

        private static bool IsLocalSwapper() =>
            Swapper.swapper != null && Swapper.swapper == PlayerControl.LocalPlayer;
        private static bool LightsActive() =>
            LightsOption != null && UTSGate.Bool(LightsOption) && IsLocalSwapper();
        private static bool CommsActive() =>
            CommsOption != null && UTSGate.Bool(CommsOption) && IsLocalSwapper();

        // Re-allow the lights/comms console for the local Swapper. Runs after TOR's
        // ConsoleCanUsePatch.Prefix (which forced canUse=couldUse=false), so we just recompute a
        // normal distance-based result for the enabled panel(s).
        [HarmonyPatch(typeof(Console), nameof(Console.CanUse))]
        static class ConsoleCanUsePostfix {
            public static void Postfix(ref float __result, Console __instance,
                                       [HarmonyArgument(0)] NetworkedPlayerInfo pc,
                                       [HarmonyArgument(1)] ref bool canUse,
                                       [HarmonyArgument(2)] ref bool couldUse) {
                try {
                    if (!IsLocalSwapper()) return;
                    if (pc == null || pc.Object == null || pc.Object != PlayerControl.LocalPlayer) return;

                    bool isLights = false, isComms = false;
                    var tasks = __instance.TaskTypes;
                    for (int i = 0; i < tasks.Count; i++) {
                        if (tasks[i] == TaskTypes.FixLights) isLights = true;
                        else if (tasks[i] == TaskTypes.FixComms) isComms = true;
                    }
                    bool allow = (isLights && LightsActive()) || (isComms && CommsActive());
                    if (!allow) return;

                    var po = pc.Object;
                    if (po.Data == null || po.Data.IsDead || !po.CanMove) return;

                    float dist = Vector2.Distance(po.GetTruePosition(), (Vector2)__instance.transform.position);
                    __result = dist;
                    couldUse = true;
                    canUse = dist <= __instance.UsableDistance;
                } catch (Exception e) {
                    UsefulTORStuffPlugin.Logger?.LogError($"[SwapperLightsFix] Console.CanUse postfix failed: {e}");
                }
            }
        }

        // Flip Swapper.swapper to null for the duration of TOR's Begin-postfix so its
        // "Close() the Swapper's own panel" check reads null and does nothing; the Finalizer
        // restores the real value right after Begin returns (__state carries it, so a reentrant
        // Begin call cannot clobber another call's in-flight flip).
        [HarmonyPatch(typeof(SwitchMinigame), nameof(SwitchMinigame.Begin))]
        [HarmonyPriority(Priority.High)]
        static class LightsBeginPatch {
            public static void Prefix(out PlayerControl __state) {
                __state = null;
                try {
                    if (LightsActive()) { __state = Swapper.swapper; Swapper.swapper = null; }
                } catch (Exception e) {
                    UsefulTORStuffPlugin.Logger?.LogError($"[SwapperLightsFix] Lights Begin prefix failed: {e}");
                    __state = null;
                }
            }
            public static void Finalizer(PlayerControl __state) {
                if (__state != null) Swapper.swapper = __state;
            }
        }

        [HarmonyPatch(typeof(TuneRadioMinigame), nameof(TuneRadioMinigame.Begin))]
        [HarmonyPriority(Priority.High)]
        static class CommsBeginPatch {
            public static void Prefix(out PlayerControl __state) {
                __state = null;
                try {
                    if (CommsActive()) { __state = Swapper.swapper; Swapper.swapper = null; }
                } catch (Exception e) {
                    UsefulTORStuffPlugin.Logger?.LogError($"[SwapperLightsFix] Comms Begin prefix failed: {e}");
                    __state = null;
                }
            }
            public static void Finalizer(PlayerControl __state) {
                if (__state != null) Swapper.swapper = __state;
            }
        }
    }
}
