// Useful TOR Stuff - Copyright (C) 2026 DaUnknown-0
// Licensed under GPL-3.0-or-later. See LICENSE for details.
// Based on The Other Roles (https://github.com/TheOtherRolesAU/TheOtherRoles), GPL-3.0.

/*
 * TrapperShiftCharges - bugfix: a player shifted into the Trapper role couldn't place traps.
 *
 * Trapper.charges and Trapper.rechargedTasks are static, role-bound fields. The Shifter only moves
 * the Trapper.trapper pointer to the new holder (TheOtherRoles.cs, Shifter.shiftRole); the charge
 * state stays in whatever state the OLD trapper left it. Two consequences for the new trapper:
 *   1. If the old trapper used up the charges, the new trapper inherits charges == 0, so the trap
 *      button's CouldUse (Buttons.cs, `Trapper.charges > 0`) blocks every placement.
 *   2. Recharge is broken: trapperUpdate (PlayerControlPatch.cs) only recharges on the EXACT match
 *      `playerCompleted == Trapper.rechargedTasks`. The new trapper has a different completed-task
 *      count than the threshold the old trapper advanced, so the match never happens again.
 *
 * Fix: when the local player newly becomes the Trapper through a shift, restore the normal starting
 * charge count (without discarding any it already had) and rebase the recharge threshold onto the
 * new trapper's current task progress so future recharges resume. Trapper.charges is only read on
 * the local trapper's own client (the button gate), so applying it on whichever client just became
 * the trapper is sufficient and correct; the host can verify it as the trapper.
 *
 * No option: this is a straight bugfix, always active.
 */

using System;
using HarmonyLib;
using UnityEngine;
using TheOtherRoles;
using static TheOtherRoles.TheOtherRoles;

namespace UsefulTORStuff {
    public static class TrapperShiftCharges {

        // Patch the public RPC entry (runs once per shift on every client). Capturing the local
        // trapper state in the prefix and comparing in the postfix detects exactly the transition
        // "local player was NOT the trapper -> now IS the trapper" without any game-start bookkeeping.
        [HarmonyPatch(typeof(RPCProcedure), nameof(RPCProcedure.shifterShift))]
        static class ShifterShiftPatch {
            public static void Prefix(out bool __state) {
                __state = Trapper.trapper != null && Trapper.trapper == PlayerControl.LocalPlayer;
            }

            public static void Postfix(bool __state) {
                try {
                    bool localIsTrapperNow = Trapper.trapper != null && Trapper.trapper == PlayerControl.LocalPlayer;
                    if (!localIsTrapperNow || __state) return; // not a fresh become-trapper transition

                    var trapper = Trapper.trapper;
                    if (trapper == null || trapper.Data == null) return;

                    // 1. Fresh starting charges (the TOR start value is maxCharges / 2), never lowering
                    //    a count the new holder already happens to have.
                    int start = Trapper.maxCharges / 2;
                    Trapper.charges = Mathf.Max(Trapper.charges, start);

                    // 2. Rebase the recharge threshold onto the new trapper's own task progress so the
                    //    exact-match recharge in trapperUpdate works again from here on.
                    var (playerCompleted, _) = TasksHandler.taskInfo(trapper.Data);
                    Trapper.rechargedTasks = playerCompleted + Trapper.rechargeTasksNumber;

                    UsefulTORStuffPlugin.Logger?.LogInfo(
                        $"[TrapperShiftCharges] Local player became Trapper via shift; charges set to {Trapper.charges}, recharge threshold rebased to {Trapper.rechargedTasks}.");
                } catch (Exception e) {
                    UsefulTORStuffPlugin.Logger?.LogError($"[TrapperShiftCharges] shifterShift postfix failed: {e}");
                }
            }
        }
    }
}
