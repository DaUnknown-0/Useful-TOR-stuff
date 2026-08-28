// Useful TOR Stuff - Copyright (C) 2026 DaUnknown-0
// Licensed under GPL-3.0-or-later. See LICENSE for details.
// Based on The Other Roles (https://github.com/TheOtherRolesAU/TheOtherRoles), GPL-3.0.

/*
 * MedicShiftShield - bugfix: a Medic who dies with a shield still queued spends the charge without
 * ever delivering it, and hands the block on to whoever is shifted into the role.
 *
 * THE SEQUENCE, all of it in TOR
 *   1. With "Set Shield After Meeting" on, pressing the shield button calls setFutureShielded
 *      (RPC.cs:822-825), which parks the target in Medic.futureShielded and sets Medic.usedShield
 *      true IMMEDIATELY. The shield itself is placed later, at the exile screen.
 *   2. ExileControllerBeginPatch.Prefix (ExileControllerPatch.cs:19) only places it while the medic
 *      is alive: `... && Medic.futureShielded != null && !Medic.medic.Data.IsDead`. A medic killed
 *      in the meantime therefore never delivers the shield - and nothing clears futureShielded or
 *      gives the charge back, because medicSetShielded is the only thing that clears it (RPC.cs:581).
 *   3. The Shifter's shift runs a few lines further down in the SAME prefix. Shifter.shiftRole moves
 *      the Medic.medic pointer (TheOtherRoles.cs:2091-2095) and nothing else: usedShield is a static
 *      that belongs to the ROLE, not to a player, and it is only ever reset by Medic.clearAndReload
 *      at round start.
 *
 * So the player shifted into the Medic role inherits usedShield == true and the shield button's
 * CouldUse (`!Medic.usedShield`, Buttons.cs:511) refuses every placement, for a shield that nobody
 * ever received. There is a second, quieter half to it: futureShielded still points at the dead
 * medic's old target, and at the NEXT exile the medic pointer is a living player again, so TOR
 * happily places that stale shield - one the new medic never chose.
 *
 * WHAT THIS FIXES, AND WHAT IT DELIBERATELY DOES NOT
 * Only the charge that was consumed but never delivered. The discriminator is exact and provable
 * rather than a guess at intent: futureShielded surviving into the shift can ONLY mean the exile
 * handler just skipped it, and it skips for exactly one reason, a dead medic. A shield that was
 * really placed leaves futureShielded null (medicSetShielded clears it) and stays spent, so a
 * successor does NOT get a second one. Without "Set Shield After Meeting" the shield is placed the
 * instant the button is pressed, so the losing window does not exist and this never fires.
 *
 * EVERY CLIENT, NOT JUST THE NEW MEDIC. TrapperShiftCharges can repair Trapper.charges locally
 * because only the local trapper's own button reads it. This state is not like that: the stale
 * futureShielded is acted on by the HOST in ExileControllerBeginPatch, so leaving it standing
 * anywhere would place that ghost shield later. shifterShift is an RPC procedure that runs on every
 * client, so clearing it here keeps all of them identical.
 *
 * No option: a charge that was paid for and never delivered is a defect, not a balance knob.
 */

using System;
using HarmonyLib;
using TheOtherRoles;

namespace UsefulTORStuff {
    public static class MedicShiftShield {

        [HarmonyPatch(typeof(RPCProcedure), nameof(RPCProcedure.shifterShift))]
        static class ShifterShiftPatch {
            // Who held the role before the shift, so the postfix can tell "the Medic changed hands"
            // from "this shift had nothing to do with the Medic".
            public static void Prefix(out PlayerControl __state) {
                __state = Medic.medic;
            }

            public static void Postfix(PlayerControl __state) {
                try {
                    if (Medic.medic == null || Medic.medic == __state) return;   // role did not move
                    if (Medic.futureShielded == null) return;                    // shield was delivered

                    string lost = "?";
                    try { lost = Medic.futureShielded.Data?.PlayerName ?? "?"; } catch { }

                    Medic.futureShielded = null;
                    Medic.usedShield = false;

                    UsefulTORStuffPlugin.Logger?.LogInfo(
                        "[MedicShiftShield] The previous medic died with a shield still queued for "
                        + $"{lost}, so it was never placed. Charge returned to the new medic and the "
                        + "stale target cleared.");
                } catch (Exception e) {
                    UsefulTORStuffPlugin.Logger?.LogError($"[MedicShiftShield] shifterShift postfix failed: {e}");
                }
            }
        }
    }
}
