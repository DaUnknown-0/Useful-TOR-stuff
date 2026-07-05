// Useful TOR Stuff - Copyright (C) 2026 DaUnknown-0
// Licensed under GPL-3.0-or-later. See LICENSE for details.
// Based on The Other Roles (https://github.com/TheOtherRolesAU/TheOtherRoles), GPL-3.0.

/*
 * BomberArmoredFix - an Armored Bomber loses his armor when PLANTING and the bomb never spawns.
 *
 * Root cause (vanilla TOR): the bomber button "attacks" the bomber HIMSELF to consume Pursuer
 * blanks (Buttons.cs: checkMuderAttempt(Bomber.bomber, Bomber.bomber, ignoreMedic: true)). If the
 * bomber wears the Armored modifier, checkArmored() eats that pseudo-attack: the armor breaks
 * (RPC to everyone) and the result is BlankKill, so the bomb is not placed - but the button
 * callback still sets Bomber.isPlanted = true, which locks the plant button until clearBomb()
 * runs. With MultiModifiers' extra Armored holders the same thing happens via our own armor block.
 *
 * Fix: while checkMuderAttempt runs with killer == target (a SELF-check, i.e. an ability probe,
 * not a real murder attempt), checkArmored is skipped entirely - armor is "protection from one
 * murder attempt", and nobody murder-attempts themselves. A bool prefix skips TOR's original
 * (TOR does not patch its own checkArmored, so there is no patch-order hazard), and
 * MultiModifiers' extra-armor postfix honors the same flag. Pursuer blanks still work on the
 * plant (TOR design: the blank consumes the attempt); Thief fail-kills keep their armor save
 * (that inner checkArmored(killer) call happens during a NON-self outer check).
 *
 * Runs on the acting player's client (the bomber's own button), so it works for every UTS user
 * regardless of what the rest of the lobby runs - no sync impact: with the fix no armor-break
 * RPC is sent at all.
 */

using HarmonyLib;
using TheOtherRoles;
using static TheOtherRoles.TheOtherRoles;

namespace UsefulTORStuff {
    public static class BomberArmoredFix {
        // True while checkMuderAttempt runs killer==target (ability self-probe, e.g. bomb plant).
        internal static bool InSelfCheck;

        [HarmonyPatch(typeof(Helpers), nameof(Helpers.checkMuderAttempt))]
        static class SelfCheckFlagPatch {
            public static void Prefix(PlayerControl killer, PlayerControl target) {
                InSelfCheck = killer != null && target != null && killer.PlayerId == target.PlayerId;
            }
            // Finalizer (not postfix): must reset even if the original throws, or a stale flag
            // would skip a real armor check later.
            public static void Finalizer() {
                InSelfCheck = false;
            }
        }

        [HarmonyPatch(typeof(Helpers), nameof(Helpers.checkArmored))]
        static class ArmoredSelfCheckSkipPatch {
            public static bool Prefix(ref bool __result) {
                if (!InSelfCheck) return true;
                __result = false;   // self-probes never hit armor (and never break it)
                return false;
            }
        }
    }
}
