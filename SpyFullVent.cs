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

        // true, während TORs Vent.Use läuft — nur dann überschreiben wir das SetButtons-Argument
        // (TORs gegateter Aufruf). Native SetButtons-Aufrufe (z.B. aus TryMoveToVent beim Vent-Wechsel)
        // bleiben unangetastet, damit die Traversal-Pfeile vom Spiel selbst korrekt gesetzt werden.
        private static bool _inVentUse;
        // Vorzustand beim Klick: true = Einsteigen (Pfeile zeigen), false = Aussteigen (Pfeile aus).
        // Aus dem inVent VOR dem Klick bestimmt — zuverlässig, anders als das während der Aussteige-
        // Animation noch kurz true bleibende Live-inVent.
        private static bool _pendingIsEnter;

        public static void CreateOptions() {
            try {
                // Child of TOR's "Spy Can Enter Vents": TOR's options menu hides a child whose parent
                // is at selection 0, so this option disappears when the Spy can't vent at all.
                Option = CustomOption.Create(
                    1250, Types.Crewmate, "Spy Can Fully Vent",
                    false, CustomOptionHolder.spyCanEnterVents);

                // Place it directly under "Spy Can Enter Vents" so it reads as nested beneath it.
                var opts = CustomOption.options;
                opts.Remove(Option);
                int idx = opts.IndexOf(CustomOptionHolder.spyCanEnterVents);
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

        // Bewegungspfeile für den Spy genau wie für einen Impostor schalten: SetButtons(isEnter).
        // TOR ruft in Vent.Use SetButtons(isEnter && canMoveInVents) auf, und canMoveInVents ist für
        // den Spy IMMER false → Pfeile aus. Wir überschreiben das Argument nur während Vent.Use
        // (_inVentUse) mit dem zuverlässigen _pendingIsEnter (Vorzustand des Klicks): true beim
        // Einsteigen (Pfeile an), false beim Aussteigen (Pfeile sofort aus — kein Nachflackern während
        // der Aussteige-Animation, weil wir NICHT das live noch kurz true bleibende inVent lesen).
        // Native SetButtons-Aufrufe (Vent-Wechsel via TryMoveToVent) lassen wir unangetastet.
        [HarmonyPatch(typeof(Vent), nameof(Vent.SetButtons))]
        static class VentSetButtonsPatch {
            public static void Prefix(Vent __instance, ref bool __0) {
                try {
                    if (!LocalIsSpy() || !_inVentUse) return;
                    bool incoming = __0;
                    __0 = _pendingIsEnter;
                    UsefulTORStuffPlugin.Logger?.LogInfo(
                        $"[SpyFullVent][DIAG] SetButtons vent={(__instance != null ? __instance.Id : -1)} " +
                        $"isEnter={_pendingIsEnter} incoming={incoming} -> {__0}");
                } catch { }
            }
        }

        // DIAG: feuert, sobald ein Bewegungspfeil-Klick die Traversal-Methode erreicht. Reines Logging
        // (kein Skip) — zeigt im nächsten Test, ob Klicks ankommen (dann sitzt die Sperre tiefer) oder
        // gar nicht (dann ist es die Pfeil-Sichtbarkeit/Interaktivität).
        [HarmonyPatch(typeof(Vent), nameof(Vent.TryMoveToVent))]
        static class VentTryMoveDiagPatch {
            public static void Prefix(Vent __instance, Vent otherVent) {
                try {
                    if (!LocalIsSpy()) return;
                    bool inVent = PlayerControl.LocalPlayer != null && PlayerControl.LocalPlayer.inVent;
                    UsefulTORStuffPlugin.Logger?.LogInfo(
                        $"[SpyFullVent][DIAG] TryMoveToVent from={(__instance != null ? __instance.Id : -1)} " +
                        $"to={(otherVent != null ? otherVent.Id : -1)} inVent={inVent}");
                } catch { }
            }
        }

        // Markiert das Zeitfenster von Vent.Use und erfasst den Vorzustand (isEnter) VOR dem Klick.
        // Priority.First, damit unser Prefix vor TORs VentUsePatch.Prefix läuft (das intern SetButtons
        // aufruft) — so steht _inVentUse/_pendingIsEnter, wenn unser SetButtons-Prefix greift. Der
        // Postfix räumt das Fenster wieder ab (läuft auch, wenn TORs Prefix das Original überspringt).
        [HarmonyPatch(typeof(Vent), nameof(Vent.Use))]
        static class VentUseScopePatch {
            [HarmonyPriority(Priority.First)]
            public static void Prefix() {
                try {
                    if (!LocalIsSpy()) return;
                    _pendingIsEnter = !(PlayerControl.LocalPlayer != null && PlayerControl.LocalPlayer.inVent);
                    _inVentUse = true;
                    UsefulTORStuffPlugin.Logger?.LogInfo($"[SpyFullVent][DIAG] Vent.Use click: isEnter={_pendingIsEnter}");
                } catch { }
            }
            public static void Postfix() { _inVentUse = false; }
        }
    }
}
