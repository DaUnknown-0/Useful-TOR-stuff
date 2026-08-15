// Useful TOR Stuff - Copyright (C) 2026 DaUnknown-0
// Licensed under GPL-3.0-or-later. See LICENSE for details.
// Based on The Other Roles (https://github.com/TheOtherRolesAU/TheOtherRoles), GPL-3.0.

/*
 * MedicReshield - new Medic option "Medic Can Reshield" plus a unified shield-charge system.
 *
 * TOR's Medic shield is a one-shot: once used, Medic.usedShield latches true and the shield button
 * disappears (its couldUse checks !Medic.usedShield, Buttons.cs:511). This adds an "unshield" button
 * that removes the current shield and re-arms the medic so they can redistribute it to a new target
 * via the normal shield button.
 *
 * The shield state must stay consistent across clients (kill-suppression checks Medic.shielded on
 * the killer's client, Helpers.checkMuderAttempt). So the reset is broadcast via a small custom RPC
 * (id 249) that clears shielded/futureShielded/usedShield everywhere, then re-shielding goes through
 * TOR's existing MedicSetShielded RPC unchanged. Clearing usedShield automatically re-enables TOR's
 * shield button (couldUse) and removes the old shield visual (derived from Medic.shielded each frame).
 *
 * Shield charges (OptionShieldCharges, 0 = ∞): the medic has a pool of charges, shown as "X/Y" on
 * TOR's shield button. A charge is consumed only when a shield is PLACED; the unshield button and a
 * shielded death cost nothing. Placement is detected as a false→true transition of Medic.usedShield
 * (set in RPCProcedure.medicSetShielded/setFutureShielded, RPC.cs:579/824) on the medic's client.
 *
 * The shield blocks *murder*, so a blocked attack never sets usedShield or IsDead — only a real death
 * of Medic.shielded (lover, guesser, shifter, exile, …) does. On such a death we re-arm the medic
 * (broadcast SendReset) if charges remain so they can place a new shield; no charge is spent for the
 * death itself. Once the pool is empty we leave usedShield latched, so no further shield can be placed.
 * The remaining/max count is drawn on TOR's shield button (always visible to the medic) via a postfix
 * on CustomButton.HudUpdate — no TOR source is modified.
 */

using System;
using System.Linq;
using HarmonyLib;
using Hazel;
using UnityEngine;
using TheOtherRoles;
using TheOtherRoles.Objects;
using static TheOtherRoles.TheOtherRoles;
using Types = TheOtherRoles.CustomOption.CustomOptionType;

namespace UsefulTORStuff {
    public static class MedicReshield {
        // Free range: TOR 100–~180, Chance 200/201/250/251, Useful 252 (bomber)/253 (handshake).
        public const byte ReshieldRpcId = 249;

        public static CustomOption Option;             // Off/On toggle
        public static CustomOption OptionShieldCharges; // total shield placements per game (0 = ∞, shown as "∞")
        private static CustomButton unshieldButton;     // recreated each HudManager.Start

        // Index 0 = "∞" (unlimited), indices 1–10 map directly to the charge count.
        private static readonly string[] ShieldChargeSelections =
            { "∞", "1", "2", "3", "4", "5", "6", "7", "8", "9", "10" };

        // Shields placed this game, capped by OptionShieldCharges. A charge is spent only on placement;
        // remaining charges = max - placementsUsed. Reset on round start.
        private static int placementsUsed;

        // Tracks Medic.usedShield to detect a false→true transition (= a new placement) on the medic's
        // client. Reset on round start.
        private static bool prevUsedShield;

        // Guards the shielded-death detector so a single death only re-arms once (the dead reference
        // lingers when the pool is empty and we don't re-arm). Reset on round start.
        private static byte? lastDeadShieldedId;

        public static void CreateOptions() {
            // Receiver registration for the consolidated RPC channel (UTSRpc.CallId = 240).
            // CreateOptions is this feature's only load-time entry point, so it doubles as init.
            UTSRpc.Register(ReshieldRpcId, HandleModuleRpc);

            try {
                Option = CustomOption.Create(
                    1230, Types.Crewmate, "Medic Can Reshield",
                    false, CustomOptionHolder.medicSpawnRate);
                UTSLocalization.BindOptionTitle(Option, "uts.medicreshield.option_name");

                // Sub-option: number of shield placements per game (0 = ∞). Only visible while Reshield is on.
                OptionShieldCharges = CustomOption.Create(
                    1231, Types.Crewmate, "Shield Charges",
                    ShieldChargeSelections, Option);
                UTSLocalization.BindOptionTitle(OptionShieldCharges, "uts.medicreshield.charges_option");

                var opts = CustomOption.options;
                opts.Remove(Option);
                opts.Remove(OptionShieldCharges);
                int idx = opts.IndexOf(CustomOptionHolder.medicShowAttemptToMedic);
                if (idx < 0) idx = opts.Count - 1;
                // Insert in reverse so the order is: Medic Can Reshield → Shield Charges
                opts.Insert(idx + 1, OptionShieldCharges);
                opts.Insert(idx + 1, Option);

                UsefulTORStuffPlugin.Logger?.LogInfo("[MedicReshield] Option created under Medic.");
            } catch (Exception e) {
                UsefulTORStuffPlugin.Logger?.LogError($"[MedicReshield] CreateOptions failed: {e}");
            }
        }

        // Broadcast + apply the shield reset (sender never receives its own RPC).
        // LEGACY DUAL-SEND (see UTSRpc.cs): legacy callId 249 + consolidated channel 240. Classified
        // IDEMPOTENT: ApplyReset only nulls Medic.shielded/futureShielded and clears usedShield, so
        // running it twice ends in exactly the same state. The legacy half can go in a breaking release.
        private static void SendReset() {
            UTSRpc.SendDual(ReshieldRpcId, ReshieldRpcId, null); // no payload
            ApplyReset();
        }

        private static void ApplyReset() {
            try {
                Medic.shielded = null;
                Medic.futureShielded = null;
                Medic.usedShield = false;
            } catch { }
        }

        // Receiver on the consolidated channel (module byte 249). Registered from CreateOptions.
        // Owner-authored: only the Medic (or the host) may reset the shield state - without this
        // guard any client could strip the medic's shield right before a kill (AUDIT-2026-08-15).
        private static void HandleModuleRpc(MessageReader reader) {
            if (UTSRpc.RequireOwnerOrHost(Medic.medic, "MedicReshield.Reset")) ApplyReset();
        }

        // LEGACY DUAL-SEND receiver: still accepts the old standalone callId 249 from pre-240 builds.
        [HarmonyPatch(typeof(PlayerControl), nameof(PlayerControl.HandleRpc))]
        [HarmonyPriority(Priority.High)]
        static class HandleRpcPatch {
            public static bool Prefix(byte callId, MessageReader reader, PlayerControl __instance) {
                if (callId == ReshieldRpcId) {
                    // Same owner-or-host guard on the LEGACY path: __instance is the sender here,
                    // UTSRpc.Sender is only set for the consolidated-channel dispatch (AUDIT-2026-08-15).
                    if (UTSRpc.RequireOwnerOrHost(__instance, Medic.medic, "MedicReshield.Reset(legacy)"))
                        ApplyReset();
                    return false;
                }
                return true;
            }
        }

        // Reset the charge state on round reset (new game / round start).
        [HarmonyPatch(typeof(RPCProcedure), nameof(RPCProcedure.resetVariables))]
        static class ResetPatch {
            public static void Postfix() { placementsUsed = 0; prevUsedShield = false; lastDeadShieldedId = null; }
        }

        // Build the unshield button after the HUD is set up (recreated each HudManager.Start, like
        // TOR's own buttons).
        [HarmonyPatch(typeof(HudManager), nameof(HudManager.Start))]
        [HarmonyPriority(Priority.Low)]
        static class HudStartPatch {
            public static void Postfix(HudManager __instance) {
                try {
                    unshieldButton = new CustomButton(
                        // Unshield is free: just remove the current shield and re-arm. The next placement
                        // (via TOR's shield button) is what spends a charge.
                        () => { SendReset(); UTSAssets.PlayReshield(); },
                        // HasButton: medic, alive, option on, a shield is currently active (something to
                        // remove), and a charge remains to re-give (0 = unlimited).
                        // Clearing usedShield hides this button again and re-shows TOR's shield button.
                        () => Option != null && UTSGate.Bool(Option)
                              && Medic.medic != null && Medic.medic == PlayerControl.LocalPlayer
                              && PlayerControl.LocalPlayer.Data != null && !PlayerControl.LocalPlayer.Data.IsDead
                              && Medic.usedShield
                              && (OptionShieldCharges == null || UTSGate.Sel(OptionShieldCharges) == 0
                                  || placementsUsed < UTSGate.Sel(OptionShieldCharges)),
                        () => PlayerControl.LocalPlayer.CanMove,
                        () => { },
                        UTSAssets.UnshieldIcon ?? Medic.getButtonSprite(),
                        CustomButton.ButtonPositions.lowerRowLeft,
                        __instance,
                        KeyCode.G,
                        false,
                        UTSLocalization.Tr("uts.medicreshield.button_label")
                    );
                    unshieldButton.MaxTimer = 0f;
                    unshieldButton.Timer = -1f;
                } catch (Exception e) {
                    UsefulTORStuffPlugin.Logger?.LogError($"[MedicReshield] Button creation failed: {e}");
                }
            }
        }

        // Runs every frame after all CustomButton.Update calls (CustomButton.HudUpdate is invoked from
        // TOR's HudManager.Update postfix, UpdatePatch.cs:347). Three jobs, all medic-local:
        //   1) spend a charge whenever a shield is placed (usedShield false→true transition);
        //   2) re-arm (no charge) when the shielded player actually dies, while charges remain;
        //   3) draw the remaining/max charge count on TOR's shield button.
        [HarmonyPatch(typeof(CustomButton), nameof(CustomButton.HudUpdate))]
        static class ChargeTickPatch {
            public static void Postfix() {
                try {
                    if (Option == null || !UTSGate.Bool(Option)) return;
                    var lp = PlayerControl.LocalPlayer;
                    if (Medic.medic == null || lp == null || Medic.medic != lp
                        || lp.Data == null || lp.Data.IsDead) return;

                    int max = OptionShieldCharges == null ? 0 : UTSGate.Sel(OptionShieldCharges);
                    bool unlimited = max == 0;

                    // 1) Placement → spend one charge. usedShield is set true exactly when a shield is
                    //    placed (RPC.cs:579/824); SendReset clears it back to false on unshield / re-arm.
                    bool used = Medic.usedShield;
                    if (used && !prevUsedShield) placementsUsed++;
                    prevUsedShield = used;

                    // 2) Shielded death → re-arm (no charge), once per death. The shield blocks murder,
                    //    so any real death of Medic.shielded is a non-prevented one (lover/guess/shift/…).
                    var shielded = Medic.shielded;
                    if (shielded != null && shielded.Data != null && shielded.Data.IsDead
                        && lastDeadShieldedId != shielded.PlayerId) {
                        lastDeadShieldedId = shielded.PlayerId;
                        if (unlimited || placementsUsed < max)
                            SendReset();   // re-arm: clears the dead shield everywhere, usedShield = false
                        // else: out of charges — leave usedShield latched so no new shield can be placed.
                    }

                    // 3) Draw "remaining/max" (or "∞") on TOR's shield button, which is always visible to
                    //    the medic. Excludes our own unshield button (shares the same sprite).
                    var shieldBtn = CustomButton.buttons.FirstOrDefault(
                        b => b != null && b != unshieldButton && b.Sprite == Medic.getButtonSprite());
                    if (shieldBtn != null && shieldBtn.actionButton != null) {
                        string txt = unlimited
                            ? UTSLocalization.Tr("uts.medicreshield.charges_infinite")
                            : UTSLocalization.Tr("uts.medicreshield.charge_counter", Math.Max(0, max - placementsUsed), max);
                        shieldBtn.actionButton.OverrideText(txt);
                        if (shieldBtn.actionButtonLabelText != null)
                            shieldBtn.actionButtonLabelText.enabled = true;
                    }
                } catch (Exception e) {
                    UsefulTORStuffPlugin.Logger?.LogError($"[MedicReshield] Charge tick failed: {e}");
                }
            }
        }
    }
}
