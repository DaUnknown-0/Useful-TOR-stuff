// Useful TOR Stuff - Copyright (C) 2026 DaUnknown-0
// Licensed under GPL-3.0-or-later. See LICENSE for details.
// Based on The Other Roles (https://github.com/TheOtherRolesAU/TheOtherRoles), GPL-3.0.

/*
 * TrapperLimp - new Trapper options "Trapped Players Limp" and "Trapper Can Self-Limp".
 *
 * TOR traps fully FREEZE a triggering player for Trapper.trapDuration (moveable=false + Halt,
 * Trap.triggerTrap). This adds a limp (movement slow) ON TOP of that freeze — it has no effect while
 * frozen (we gate on CanMove) and kicks in for a configurable duration AFTER the player is released.
 * The trapper can also toggle a self-limp on himself.
 *
 * The slow is applied the same way PropHunt does its speed effects: a velocity multiply in the
 * PlayerPhysics.FixedUpdate (AmOwner) and CustomNetworkTransform.FixedUpdate (!AmOwner) postfixes,
 * so it looks consistent locally and for remote viewers.
 *
 * Sync: Trap.triggerTrap already runs on every client, so the trapped-limp schedule (limpUntil) is
 * naturally consistent everywhere. The self-limp toggle is broadcast via a small custom RPC (248) so
 * remote clients slow the trapper's NetTransform too.
 */

using System;
using System.Collections.Generic;
using HarmonyLib;
using Hazel;
using UnityEngine;
using TheOtherRoles;
using TheOtherRoles.Objects;
using static TheOtherRoles.TheOtherRoles;
using Types = TheOtherRoles.CustomOption.CustomOptionType;

namespace UsefulTORStuff {
    public static class TrapperLimp {
        public const byte SelfLimpRpcId = 248;

        public static CustomOption TrappedOption;   // Off/On: trapped players limp after the freeze
        public static CustomOption SelfOption;       // Off/On: trapper gets a self-limp toggle
        public static CustomOption StrengthOption;   // speed multiplier while limping
        public static CustomOption DurationOption;   // limp seconds after the freeze

        // PlayerId → realtime (Time.time) until which that player limps (trapped path). Synced because
        // Trap.triggerTrap runs on every client.
        private static readonly Dictionary<byte, float> limpUntil = new Dictionary<byte, float>();
        // Trapper self-limp toggle, broadcast to all clients via SelfLimpRpc.
        private static bool selfLimping;

        private static CustomButton selfLimpButton;

        public static void CreateOptions() {
            // Receiver registration for the consolidated RPC channel (UTSRpc.CallId = 240).
            // CreateOptions is this feature's only load-time entry point, so it doubles as init.
            UTSRpc.Register(SelfLimpRpcId, HandleModuleRpc);

            try {
                TrappedOption = CustomOption.Create(
                    1270, Types.Crewmate, "Trapped Players Limp", false, CustomOptionHolder.trapperSpawnRate);
                UTSLocalization.BindOptionTitle(TrappedOption, "uts.trapperlimp.trapped_option");
                SelfOption = CustomOption.Create(
                    1271, Types.Crewmate, "Trapper Can Self-Limp", false, CustomOptionHolder.trapperSpawnRate);
                UTSLocalization.BindOptionTitle(SelfOption, "uts.trapperlimp.self_option");
                StrengthOption = CustomOption.Create(
                    1272, Types.Crewmate, "Limp Speed Multiplier", 0.5f, 0.25f, 0.9f, 0.05f, CustomOptionHolder.trapperSpawnRate);
                UTSLocalization.BindOptionTitle(StrengthOption, "uts.trapperlimp.strength_option");
                // CustomOption.Create builds the float selections by accumulating `+= 0.05f`, which
                // drifts (e.g. 0.7000000001) and shows up raw in the menu and getFloat(). The 0.05
                // step isn't binary-exact; round each entry to 2 decimals so display + value are clean.
                if (StrengthOption.selections != null)
                    for (int i = 0; i < StrengthOption.selections.Length; i++)
                        StrengthOption.selections[i] = Mathf.Round((float)StrengthOption.selections[i] * 100f) / 100f;
                DurationOption = CustomOption.Create(
                    1273, Types.Crewmate, "Limp Duration After Freeze", 5f, 1f, 20f, 1f, CustomOptionHolder.trapperSpawnRate);
                UTSLocalization.BindOptionTitle(DurationOption, "uts.trapperlimp.duration_option");

                var opts = CustomOption.options;
                foreach (var o in new[] { TrappedOption, SelfOption, StrengthOption, DurationOption }) opts.Remove(o);
                int idx = opts.IndexOf(CustomOptionHolder.trapperTrapDuration);
                if (idx < 0) idx = opts.Count - 1;
                opts.Insert(idx + 1, TrappedOption);
                opts.Insert(idx + 2, SelfOption);
                opts.Insert(idx + 3, StrengthOption);
                opts.Insert(idx + 4, DurationOption);

                UsefulTORStuffPlugin.Logger?.LogInfo("[TrapperLimp] Options created under Trapper.");
            } catch (Exception e) {
                UsefulTORStuffPlugin.Logger?.LogError($"[TrapperLimp] CreateOptions failed: {e}");
            }
        }

        private static float Ratio() => StrengthOption != null ? StrengthOption.getFloat() : 0.5f;

        // Dead players (ghosts) still have CanMove == true, so the limp would otherwise follow them
        // into death — permanently for the time-unbounded self-limp. Only the living limp.
        private static bool IsAlive(PlayerControl p) =>
            p != null && p.Data != null && !p.Data.IsDead;

        private static bool ShouldLimp(byte id) {
            if (TrappedOption != null && TrappedOption.getBool()
                && limpUntil.TryGetValue(id, out float until) && Time.time < until) return true;
            if (SelfOption != null && SelfOption.getBool() && selfLimping
                && Trapper.trapper != null && Trapper.trapper.PlayerId == id) return true;
            return false;
        }

        // ---- Trapped-limp scheduling (runs on every client) -------------------------------------
        // Patch RPCProcedure.triggerTrap (public) rather than the internal Objects.Trap.triggerTrap;
        // it simply forwards to it with the same (playerId, trapId) args and runs on every client.
        [HarmonyPatch(typeof(RPCProcedure), nameof(RPCProcedure.triggerTrap))]
        static class TriggerTrapPatch {
            public static void Postfix(byte playerId, byte trapId) {
                try {
                    if (TrappedOption == null || !TrappedOption.getBool()) return;
                    float dur = DurationOption != null ? DurationOption.getFloat() : 5f;
                    // Limp window starts now and lasts through the freeze plus the configured tail, so
                    // the player keeps limping after being released.
                    limpUntil[playerId] = Time.time + Trapper.trapDuration + dur;
                } catch (Exception e) {
                    UsefulTORStuffPlugin.Logger?.LogError($"[TrapperLimp] triggerTrap postfix failed: {e}");
                }
            }
        }

        // Clear state each round (same reset hook the rest of this mod uses).
        [HarmonyPatch(typeof(RPCProcedure), nameof(RPCProcedure.resetVariables))]
        static class ResetPatch {
            public static void Postfix() {
                limpUntil.Clear();
                selfLimping = false;
            }
        }

        // ---- Velocity slow (mirrors PropHunt's speed effect) ------------------------------------
        [HarmonyPatch(typeof(PlayerPhysics), nameof(PlayerPhysics.FixedUpdate))]
        static class PlayerPhysicsPatch {
            public static void Postfix(PlayerPhysics __instance) {
                try {
                    if (!__instance.AmOwner || __instance.myPlayer == null) return;
                    if (GameData.Instance != null && IsAlive(__instance.myPlayer) && __instance.myPlayer.CanMove && ShouldLimp(__instance.myPlayer.PlayerId))
                        __instance.body.velocity *= Ratio();
                } catch { }
            }
        }
        [HarmonyPatch(typeof(CustomNetworkTransform), nameof(CustomNetworkTransform.FixedUpdate))]
        static class NetTransformPatch {
            public static void Postfix(CustomNetworkTransform __instance) {
                try {
                    if (__instance.AmOwner || __instance.myPlayer == null) return;
                    if (GameData.Instance != null && IsAlive(__instance.myPlayer) && __instance.myPlayer.CanMove && ShouldLimp(__instance.myPlayer.PlayerId))
                        __instance.body.velocity *= Ratio();
                } catch { }
            }
        }

        // ---- Self-limp toggle (button + synced RPC) ---------------------------------------------
        private static void ToggleSelfLimp() {
            selfLimping = !selfLimping;
            // LEGACY DUAL-SEND (see UTSRpc.cs): legacy callId 248 + consolidated channel 240.
            // Classified IDEMPOTENT: the TOGGLE happens locally on the sender, the wire carries the
            // resulting ABSOLUTE state (0/1) and the receiver just assigns it - so applying the same
            // payload twice yields the same flag. The legacy half can go in a breaking release.
            byte state = selfLimping ? (byte)1 : (byte)0;
            UTSRpc.SendDual(SelfLimpRpcId, SelfLimpRpcId, w => w.Write(state));
        }

        // Receiver on the consolidated channel (module byte 248). Registered from CreateOptions.
        private static void HandleModuleRpc(MessageReader reader) {
            try { selfLimping = reader.ReadByte() != 0; } catch { }
        }

        // LEGACY DUAL-SEND receiver: still accepts the old standalone callId 248 from pre-240 builds.
        [HarmonyPatch(typeof(PlayerControl), nameof(PlayerControl.HandleRpc))]
        [HarmonyPriority(Priority.High)]
        static class HandleRpcPatch {
            public static bool Prefix(byte callId, MessageReader reader) {
                if (callId == SelfLimpRpcId) {
                    HandleModuleRpc(reader);
                    return false;
                }
                return true;
            }
        }

        [HarmonyPatch(typeof(HudManager), nameof(HudManager.Start))]
        [HarmonyPriority(Priority.Low)]
        static class HudStartPatch {
            public static void Postfix(HudManager __instance) {
                try {
                    selfLimpButton = new CustomButton(
                        () => { ToggleSelfLimp(); UTSAssets.PlayLimpToggle(); },
                        () => SelfOption != null && SelfOption.getBool()
                              && Trapper.trapper != null && Trapper.trapper == PlayerControl.LocalPlayer
                              && PlayerControl.LocalPlayer.Data != null && !PlayerControl.LocalPlayer.Data.IsDead,
                        () => PlayerControl.LocalPlayer.CanMove,
                        () => { },
                        UTSAssets.SelfLimpIcon ?? Trapper.getButtonSprite(),
                        CustomButton.ButtonPositions.lowerRowCenter,
                        __instance,
                        KeyCode.H,
                        false,
                        UTSLocalization.Tr("uts.trapperlimp.button_label")
                    );
                    selfLimpButton.MaxTimer = 0f;
                    selfLimpButton.Timer = -1f;
                } catch (Exception e) {
                    UsefulTORStuffPlugin.Logger?.LogError($"[TrapperLimp] Button creation failed: {e}");
                }
            }
        }
    }
}
