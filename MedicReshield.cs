// Useful TOR Stuff - Copyright (C) 2026 DaUnknown-0
// Licensed under GPL-3.0-or-later. See LICENSE for details.
// Based on The Other Roles (https://github.com/TheOtherRolesAU/TheOtherRoles), GPL-3.0.

/*
 * MedicReshield - new Medic option "Medic Can Reshield".
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
 */

using System;
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

        public static CustomOption Option;          // Off/On toggle
        private static CustomButton unshieldButton;  // recreated each HudManager.Start

        // The reshield is limited to once per meeting cycle: set when used, cleared each time a
        // meeting ends (and on round reset).
        private static bool reshieldedThisRound;

        public static void CreateOptions() {
            try {
                Option = CustomOption.Create(
                    1230, Types.Crewmate, "Medic Can Reshield",
                    false, CustomOptionHolder.medicSpawnRate);

                var opts = CustomOption.options;
                opts.Remove(Option);
                int idx = opts.IndexOf(CustomOptionHolder.medicShowAttemptToMedic);
                if (idx < 0) idx = opts.Count - 1;
                opts.Insert(idx + 1, Option);

                UsefulTORStuffPlugin.Logger?.LogInfo("[MedicReshield] Option created under Medic.");
            } catch (Exception e) {
                UsefulTORStuffPlugin.Logger?.LogError($"[MedicReshield] CreateOptions failed: {e}");
            }
        }

        // Broadcast + apply the shield reset (sender never receives its own RPC).
        private static void SendReset() {
            try {
                MessageWriter writer = AmongUsClient.Instance.StartRpcImmediately(
                    PlayerControl.LocalPlayer.NetId, ReshieldRpcId, SendOption.Reliable, -1);
                AmongUsClient.Instance.FinishRpcImmediately(writer);
            } catch (Exception e) {
                UsefulTORStuffPlugin.Logger?.LogError($"[MedicReshield] SendReset failed: {e}");
            }
            ApplyReset();
        }

        private static void ApplyReset() {
            try {
                Medic.shielded = null;
                Medic.futureShielded = null;
                Medic.usedShield = false;
            } catch { }
        }

        // Receive RPC 249.
        [HarmonyPatch(typeof(PlayerControl), nameof(PlayerControl.HandleRpc))]
        [HarmonyPriority(Priority.High)]
        static class HandleRpcPatch {
            public static bool Prefix(byte callId, MessageReader reader) {
                if (callId == ReshieldRpcId) {
                    ApplyReset();
                    return false;
                }
                return true;
            }
        }

        // Clear the once-per-meeting limit on round reset (new game / round start).
        [HarmonyPatch(typeof(RPCProcedure), nameof(RPCProcedure.resetVariables))]
        static class ResetPatch {
            public static void Postfix() { reshieldedThisRound = false; }
        }

        // Build the unshield button after the HUD is set up (recreated each HudManager.Start, like
        // TOR's own buttons).
        [HarmonyPatch(typeof(HudManager), nameof(HudManager.Start))]
        [HarmonyPriority(Priority.Low)]
        static class HudStartPatch {
            public static void Postfix(HudManager __instance) {
                try {
                    unshieldButton = new CustomButton(
                        () => { reshieldedThisRound = true; SendReset(); },
                        // HasButton: medic, alive, option on, a shield is currently used (so there is
                        // something to redistribute), and not already reshielded this meeting cycle.
                        // Clearing usedShield hides this button again and re-shows TOR's shield button.
                        () => Option != null && Option.getBool()
                              && Medic.medic != null && Medic.medic == PlayerControl.LocalPlayer
                              && PlayerControl.LocalPlayer.Data != null && !PlayerControl.LocalPlayer.Data.IsDead
                              && Medic.usedShield && !reshieldedThisRound,
                        () => PlayerControl.LocalPlayer.CanMove,
                        // OnMeetingEnds: re-arm the once-per-meeting reshield for the next round.
                        () => { reshieldedThisRound = false; },
                        Medic.getButtonSprite(),
                        CustomButton.ButtonPositions.lowerRowLeft,
                        __instance,
                        KeyCode.G,
                        false,
                        "RESHIELD"
                    );
                    unshieldButton.MaxTimer = 0f;
                    unshieldButton.Timer = -1f;
                } catch (Exception e) {
                    UsefulTORStuffPlugin.Logger?.LogError($"[MedicReshield] Button creation failed: {e}");
                }
            }
        }
    }
}
