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
 *   2) Re-enable the move arrows: TOR always calls Vent.SetButtons AFTER toggling inVent via
 *      RpcEnterVent/RpcExitVent (UsablesPatch.cs:130-134), so at the SetButtons call inVent already
 *      reflects the new state (== TOR's captured isEnter). A Vent.SetButtons prefix therefore forces
 *      the argument to true whenever the local Spy is inside a vent — showing arrows on enter and
 *      leaving exits untouched (inVent already false) so they hide when leaving. Reading inVent at
 *      SetButtons time is order-independent (no fragile cross-patch flag), which is why the previous
 *      Vent.Use-prefix approach showed the arrows inverted.
 */

using System;
using HarmonyLib;
using TheOtherRoles;
using static TheOtherRoles.TheOtherRoles;
using Types = TheOtherRoles.CustomOption.CustomOptionType;

namespace UsefulTORStuff {
    public static class SpyFullVent {
        public static CustomOption Option;  // Off/On toggle

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

        // Re-enable the directional move buttons for the Spy while inside a vent. TOR calls SetButtons
        // after RpcEnterVent/RpcExitVent has toggled inVent, so inVent here is the post-click state:
        // true right after entering (show arrows), false right after exiting (leave TOR's false → hide).
        [HarmonyPatch(typeof(Vent), nameof(Vent.SetButtons))]
        static class VentSetButtonsPatch {
            public static void Prefix(Vent __instance, ref bool __0) {
                try {
                    if (!LocalIsSpy()) return;
                    bool inVent = PlayerControl.LocalPlayer != null && PlayerControl.LocalPlayer.inVent;
                    bool incoming = __0;
                    if (inVent) __0 = true;
                    UsefulTORStuffPlugin.Logger?.LogInfo(
                        $"[SpyFullVent][DIAG] SetButtons vent={(__instance != null ? __instance.Id : -1)} " +
                        $"inVent={inVent} incoming={incoming} -> {__0}");
                } catch { }
            }
        }

        // DIAG: log the enter/exit decision exactly where TOR computes it, to compare against the
        // SetButtons call above (reveals the real ordering / inVent state at click time).
        [HarmonyPatch(typeof(Vent), nameof(Vent.Use))]
        static class VentUseDiagPatch {
            public static void Prefix() {
                try {
                    if (!LocalIsSpy()) return;
                    bool inVent = PlayerControl.LocalPlayer != null && PlayerControl.LocalPlayer.inVent;
                    UsefulTORStuffPlugin.Logger?.LogInfo($"[SpyFullVent][DIAG] Vent.Use click: inVent(before)={inVent}");
                } catch { }
            }
        }
    }
}
