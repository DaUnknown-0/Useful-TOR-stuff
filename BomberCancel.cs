// Useful TOR Stuff - Copyright (C) 2026 DaUnknown-0
// Licensed under GPL-3.0-or-later. See LICENSE for details.
// Based on The Other Roles (https://github.com/TheOtherRolesAU/TheOtherRoles), GPL-3.0.

/*
 * BomberCancel - new Bomber option "Bomber Can Cancel Bomb".
 *
 * TOR only lets OTHER players defuse a planted bomb, and only once it is active and they are
 * in range (Buttons.cs defuseButton). The Bomber himself has no way to abort his own bomb. This
 * adds a Bomber-only cancel button that destroys the live bomb at ANY time — whether it is still
 * in the pre-arm phase (only the bomber sees it) or already active/visible to everyone.
 *
 * The bomb GameObjects live on every client (TOR's Bomb ctor runs via the PlaceBomb RPC on all
 * clients), so the cancel must run everywhere: we broadcast a small custom RPC (id 252) that calls
 * the existing, state-independent Bomber.clearBomb() on each client, and apply it locally too.
 *
 * The button is a regular TOR CustomButton (it self-registers into CustomButton.buttons and is
 * driven by TOR's HudUpdate loop). It is created in a HudManager.Start postfix, mirroring how TOR
 * builds its own buttons.
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
    public static class BomberCancel {
        // Custom RPC id for the cross-client bomb cancel. Free range: TOR 100–~180, Chance
        // 200/201/250/251, Useful handshake 253. Useful feature RPCs use 240–252.
        public const byte CancelBombRpcId = 252;

        public static CustomOption Option;        // Off/On toggle
        private static CustomButton cancelButton;  // recreated each HudManager.Start

        // Create the option under Bomber. Called from UsefulTORStuffPlugin.Load() after TOR's
        // CustomOptionHolder.Load() (guaranteed by the hard dependency).
        public static void CreateOptions() {
            try {
                Option = CustomOption.Create(
                    1210, Types.Impostor, "Bomber Can Cancel Bomb",
                    false, CustomOptionHolder.bomberSpawnRate);

                // Move directly under the Bomber core options (late-created options otherwise land
                // at the bottom of the Impostor tab). Same approach as SheriffParityWin.
                var opts = CustomOption.options;
                opts.Remove(Option);
                int idx = opts.IndexOf(CustomOptionHolder.bomberBombActiveAfter);
                if (idx < 0) idx = opts.Count - 1;
                opts.Insert(idx + 1, Option);

                UsefulTORStuffPlugin.Logger?.LogInfo("[BomberCancel] Option created under Bomber.");
            } catch (Exception e) {
                UsefulTORStuffPlugin.Logger?.LogError($"[BomberCancel] CreateOptions failed: {e}");
            }
        }

        // Broadcast the cancel to all clients and apply it locally (the sender never receives its
        // own RPC). Bomber.clearBomb() is state-independent: it only checks Bomber.bomb != null.
        private static void SendCancel() {
            try {
                MessageWriter writer = AmongUsClient.Instance.StartRpcImmediately(
                    PlayerControl.LocalPlayer.NetId, CancelBombRpcId, SendOption.Reliable, -1);
                AmongUsClient.Instance.FinishRpcImmediately(writer);
            } catch (Exception e) {
                UsefulTORStuffPlugin.Logger?.LogError($"[BomberCancel] SendCancel failed: {e}");
            }
            try { Bomber.clearBomb(); } catch { }
        }

        // Receive RPC 252 (Prefix with high priority → before TOR's HandleRpc switch). Returns
        // false only for our id; everything else falls through to TOR untouched.
        [HarmonyPatch(typeof(PlayerControl), nameof(PlayerControl.HandleRpc))]
        [HarmonyPriority(Priority.High)]
        static class HandleRpcPatch {
            public static bool Prefix(byte callId, MessageReader reader) {
                if (callId == CancelBombRpcId) {
                    try { Bomber.clearBomb(); } catch { }
                    return false;
                }
                return true;
            }
        }

        // Build the cancel button after the HUD (and TOR's own buttons) are set up. TOR recreates
        // all CustomButtons on every HudManager.Start; the old button's actionButton is destroyed
        // with the old HUD and pruned by CustomButton.HudUpdate, so we simply create a fresh one.
        [HarmonyPatch(typeof(HudManager), nameof(HudManager.Start))]
        [HarmonyPriority(Priority.Low)]
        static class HudStartPatch {
            public static void Postfix(HudManager __instance) {
                try {
                    cancelButton = new CustomButton(
                        () => SendCancel(),
                        // HasButton: only the Bomber, only while alive, only when the option is on.
                        () => Option != null && Option.getBool()
                              && Bomber.bomber != null && Bomber.bomber == PlayerControl.LocalPlayer
                              && PlayerControl.LocalPlayer.Data != null && !PlayerControl.LocalPlayer.Data.IsDead,
                        // CouldUse: whenever a live bomb exists, regardless of arm state / visibility.
                        () => Bomber.bomb != null && PlayerControl.LocalPlayer.CanMove,
                        () => { },
                        Bomb.getDefuseSprite(),
                        // Bomber is an Impostor: the right-side slots (lowerRowRight/upperRowRight/
                        // upperRowCenter) overlap the kill button. The plant button uses upperRowLeft,
                        // so place cancel on lowerRowLeft to avoid overlapping either.
                        CustomButton.ButtonPositions.lowerRowLeft,
                        __instance,
                        KeyCode.G,
                        false,
                        ""
                    );
                    // No cooldown: usable the instant a bomb exists. Timer < 0 keeps onClickEvent
                    // enabled and Update() won't decrement it.
                    cancelButton.MaxTimer = 0f;
                    cancelButton.Timer = -1f;
                } catch (Exception e) {
                    UsefulTORStuffPlugin.Logger?.LogError($"[BomberCancel] Button creation failed: {e}");
                }
            }
        }
    }
}
