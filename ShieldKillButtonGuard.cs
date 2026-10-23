// Useful TOR Stuff - Copyright (C) 2026 DaUnknown-0
// Licensed under GPL-3.0-or-later. See LICENSE for details.
// Based on The Other Roles (https://github.com/TheOtherRolesAU/TheOtherRoles), GPL-3.0.

/*
 * ShieldKillButtonGuard (review 2026-10-02, UTS-5).
 *
 * Under TOR no kill reaches PlayerControl.CheckMurder, so the three UTS kill shields (newcomer,
 * spawn protection, early death) are enforced only by managed TOR hooks on the killer's client:
 * a setTarget prefix and a checkMuderAttempt postfix. .NET tiering can silently drop the detour of
 * a managed TOR method once it gets hot (see DetourWatchdog), and then a shield just stops working.
 *
 * The impostor kill button is the one kill path that starts in an Il2Cpp game method, whose detour
 * tiering does not touch: KillButton.DoClick, where TOR's own prefix (UsablesPatch.cs:176) kills only
 * if __instance.currentTarget is set. This prefix runs first and clears that target when it holds a
 * UTS shield, so TOR's prefix finds nothing to kill. HarmonyX runs every prefix anyway, which is why
 * the target is cleared rather than the original skipped. TOR's custom role buttons (Sheriff, Jackal
 * ...) never pass here and keep relying on the managed hooks.
 *
 * Since 23.10. the same for the extra Minis (MultiModifiers, Mini Quantity > 1): TOR protects only
 * its own Mini.mini inside checkMuderAttempt; the extra ones hung on a postfix that tiering had
 * dropped when an impostor killed a young extra Mini (the Spy) in a vent.
 *
 * Vetoing later, at MurderPlayer, is not an option: TOR's MurderPlayer postfix books the death and
 * triggers the Lover suicide without checking that anyone died.
 */

using System;
using HarmonyLib;
using UnityEngine;

namespace UsefulTORStuff {
    internal static class ShieldKillButtonGuard {
        private static float lastLogAt = -10f;

        [HarmonyPatch(typeof(KillButton), nameof(KillButton.DoClick))]
        static class DoClickPatch {
            [HarmonyPriority(Priority.First)]
            public static void Prefix(KillButton __instance) {
                try {
                    if (__instance == null) return;
                    var target = __instance.currentTarget;
                    if (target == null) return;
                    bool shield = ShieldPeaceGate.IsKillProtected(target.PlayerId);
                    bool mini = !shield && MultiModifiers.IsYoungExtraMini(target);
                    if (!shield && !mini) return;
                    __instance.currentTarget = null;
                    if (Time.realtimeSinceStartup - lastLogAt >= 2f) {
                        lastLogAt = Time.realtimeSinceStartup;
                        UsefulTORStuffPlugin.Logger?.LogInfo(
                            $"[ShieldKillButtonGuard] kill on {target.Data?.PlayerName} refused at the kill button ({(shield ? "UTS shield" : "young extra Mini")}).");
                    }
                } catch (Exception e) {
                    UsefulTORStuffPlugin.Logger?.LogWarning($"[ShieldKillButtonGuard] {e.Message}");
                }
            }
        }
    }
}
