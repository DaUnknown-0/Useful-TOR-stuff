// Useful TOR Stuff - Copyright (C) 2026 DaUnknown-0
// Licensed under GPL-3.0-or-later. See LICENSE for details.
// Based on The Other Roles (https://github.com/TheOtherRolesAU/TheOtherRoles), GPL-3.0.

/*
 * SpyFullVent - new Spy option "Spy Can Fully Vent".
 *
 * TOR's existing "Spy Can Enter Vents" (Spy.canEnterVents) only lets the Spy enter/exit a vent: in
 * VentUsePatch (UsablesPatch.cs:112,134) TOR computes canMoveInVents = LocalPlayer != Spy.spy and
 * calls Vent.SetButtons(isEnter && canMoveInVents), so the directional move arrows are never shown
 * to the Spy — it cannot travel between vents like an Impostor/Engineer.
 *
 * With this option ON the Spy gets FULL venting (enter + travel + exit):
 *   1) Force Spy.canEnterVents = true (postfix on Spy.clearAndReload) so the entry is allowed
 *      regardless of TOR's own option. roleCanUseVents() then also allows the connected vents in
 *      VentCanUsePatch, so movement is permitted.
 *   2) Re-enable the move arrows: a Vent.Use prefix records whether this click is an ENTER (computed
 *      from inVent exactly like TOR's isEnter), and a Vent.SetButtons prefix forces the argument to
 *      true on that enter — leaving exits untouched so the arrows still hide when leaving.
 */

using System;
using HarmonyLib;
using TheOtherRoles;
using static TheOtherRoles.TheOtherRoles;
using Types = TheOtherRoles.CustomOption.CustomOptionType;

namespace UsefulTORStuff {
    public static class SpyFullVent {
        public static CustomOption Option;  // Off/On toggle

        // Set at the Vent.Use click (before TOR's prefix calls SetButtons): true when the local Spy
        // is entering a vent, false when exiting. Drives the SetButtons override below.
        private static bool spyEntering;

        public static void CreateOptions() {
            try {
                Option = CustomOption.Create(
                    1250, Types.Crewmate, "Spy Can Fully Vent",
                    false, CustomOptionHolder.spySpawnRate);

                var opts = CustomOption.options;
                opts.Remove(Option);
                int idx = opts.IndexOf(CustomOptionHolder.spyHasImpostorVision);
                if (idx < 0) idx = opts.Count - 1;
                opts.Insert(idx + 1, Option);

                UsefulTORStuffPlugin.Logger?.LogInfo("[SpyFullVent] Option created under Spy.");
            } catch (Exception e) {
                UsefulTORStuffPlugin.Logger?.LogError($"[SpyFullVent] CreateOptions failed: {e}");
            }
        }

        private static bool LocalIsSpy() =>
            Option != null && Option.getBool()
            && Spy.spy != null && Spy.spy == PlayerControl.LocalPlayer;

        // Force the Spy's entry permission on each round-reload when the option is on (TOR's
        // clearAndReload otherwise overwrites canEnterVents from its own option).
        [HarmonyPatch(typeof(Spy), nameof(Spy.clearAndReload))]
        static class SpyClearAndReloadPatch {
            public static void Postfix() {
                try {
                    if (Option != null && Option.getBool()) Spy.canEnterVents = true;
                } catch { }
            }
        }

        // Record enter vs exit before TOR's VentUsePatch.Prefix runs its SetButtons call. isEnter is
        // computed the same way TOR does it (UsablesPatch.cs:115): isEnter = !inVent at click time.
        [HarmonyPatch(typeof(Vent), nameof(Vent.Use))]
        [HarmonyPriority(Priority.High)]
        static class VentUsePrefixPatch {
            public static void Prefix() {
                try {
                    spyEntering = LocalIsSpy() && PlayerControl.LocalPlayer != null
                                  && !PlayerControl.LocalPlayer.inVent;
                } catch { spyEntering = false; }
            }
        }

        // Re-enable the directional move buttons for the Spy on enter (TOR passed false). Exits leave
        // spyEntering false, so SetButtons(false) still hides the arrows when leaving.
        [HarmonyPatch(typeof(Vent), nameof(Vent.SetButtons))]
        static class VentSetButtonsPatch {
            public static void Prefix(ref bool __0) {
                try {
                    if (spyEntering && LocalIsSpy()) __0 = true;
                } catch { }
            }
        }
    }
}
