// Useful TOR Stuff - Copyright (C) 2026 DaUnknown-0
// Licensed under GPL-3.0-or-later. See LICENSE for details.
// Based on The Other Roles (https://github.com/TheOtherRolesAU/TheOtherRoles), GPL-3.0.

/*
 * SpyExtras - two new Spy option groups:
 *
 * 1) Evil Flash on Death: when the Spy (who also has the VIP modifier) is killed, a red
 *    (impostor-coloured) flash is shown to all local players. The Seer can optionally
 *    receive the true crewmate-white flash instead (matching VIP's showColor scheme, where
 *    Crew = white), revealing the Spy's actual alignment. Only applies when VIP colours are
 *    active (Vip.showColor); otherwise the Seer sees the same red evil flash as everyone.
 *    Only activates when Spy also has VIP — layering on top of the VIP flash keeps the
 *    interaction consistent. Our postfix runs after TOR's VIP flash (yellow/white for
 *    crewmate) and overrides it visually.
 *
 * 2) Shifter Interaction: three-way option controlling what happens when the Shifter's
 *    target is the Spy.
 *      0 "Shift Succeeds"  — vanilla behaviour, Shifter takes the Spy role.
 *      1 "Shifter Dies"    — mirrors TOR's impostor-target path (RPC.cs:602-611): the
 *                            Shifter is exiled and the shift is cancelled.
 *      2 "Shift Cancelled" — shift silently fails; nobody dies. Sub-option: Shifter Gets
 *                            Shift Back → resets all button cooldowns for the Shifter so
 *                            they can immediately pick a new target.
 */

using System;
using System.Reflection;
using HarmonyLib;
using TheOtherRoles;
using TheOtherRoles.Objects;
using UnityEngine;
using static TheOtherRoles.TheOtherRoles;
using Types = TheOtherRoles.CustomOption.CustomOptionType;

namespace UsefulTORStuff {
    public static class SpyExtras {
        public static CustomOption OptionDeathFlash;
        public static CustomOption OptionSeerTrueFlash;
        public static CustomOption OptionShifterInteraction;
        public static CustomOption OptionShifterGetsShiftBack;

        private static readonly string[] ShifterModes = {
            "Shift Succeeds",   // 0 = vanilla
            "Shifter Dies",     // 1 = exiled, shift cancelled
            "Shift Cancelled"   // 2 = silent cancel, no death
        };

        public static void CreateOptions() {
            try {
                // IDs 1320-1323: must NOT collide with any other CustomOption id. The 1300-1303
                // range overlaps TricksterAvatarSabotage (1300/1301/1302); duplicate ids share a
                // config slot and TOR resolves options via options.First(id==...), so the colliding
                // options scramble each other's selections (serializeOptions' consecutive-id delta
                // scheme makes it worse). That made "Shifter Interaction" read the wrong mode and
                // exile the Shifter even when "Shift Cancelled" was selected.
                OptionDeathFlash = CustomOption.Create(
                    1320, Types.Crewmate, "Evil Flash on Death",
                    false, CustomOptionHolder.spySpawnRate);

                OptionSeerTrueFlash = CustomOption.Create(
                    1321, Types.Crewmate, "Seer Sees True Flash",
                    false, OptionDeathFlash);

                OptionShifterInteraction = CustomOption.Create(
                    1322, Types.Crewmate, "Shifter Interaction",
                    ShifterModes, CustomOptionHolder.spySpawnRate);

                // Shown when OptionShifterInteraction > 0 (any non-vanilla mode)
                OptionShifterGetsShiftBack = CustomOption.Create(
                    1323, Types.Crewmate, "Shifter Gets Shift Back",
                    false, OptionShifterInteraction);

                var opts = CustomOption.options;
                opts.Remove(OptionDeathFlash);
                opts.Remove(OptionSeerTrueFlash);
                opts.Remove(OptionShifterInteraction);
                opts.Remove(OptionShifterGetsShiftBack);

                int idx = opts.IndexOf(CustomOptionHolder.spyHasImpostorVision);
                if (idx < 0) idx = opts.Count - 1;

                // Insert in reverse so the final sequence is:
                // spyHasImpostorVision → DeathFlash → SeerTrueFlash → ShifterInteraction → ShifterGetsShiftBack
                opts.Insert(idx + 1, OptionShifterGetsShiftBack);
                opts.Insert(idx + 1, OptionShifterInteraction);
                opts.Insert(idx + 1, OptionSeerTrueFlash);
                opts.Insert(idx + 1, OptionDeathFlash);

                UsefulTORStuffPlugin.Logger?.LogInfo("[SpyExtras] Options created.");
            } catch (Exception e) {
                UsefulTORStuffPlugin.Logger?.LogError($"[SpyExtras] CreateOptions failed: {e}");
            }
        }

        // Evil Flash: runs after TOR's MurderPlayer postfix (including the VIP flash).
        // Our red/blue flash overlays TOR's yellow/white VIP flash.
        [HarmonyPatch(typeof(PlayerControl), nameof(PlayerControl.MurderPlayer))]
        static class SpyDeathFlashPatch {
            public static void Postfix(PlayerControl __instance, PlayerControl target) {
                try {
                    if (OptionDeathFlash == null || !OptionDeathFlash.getBool()) return;
                    if (target == null || target != Spy.spy) return;

                    // Only when the dying Spy also has the VIP modifier
                    bool spyHasVip = false;
                    for (int i = 0; i < Vip.vip.Count; i++) {
                        if (Vip.vip[i] != null && Vip.vip[i].PlayerId == target.PlayerId) {
                            spyHasVip = true;
                            break;
                        }
                    }
                    if (!spyHasVip) return;

                    var lp = PlayerControl.LocalPlayer;
                    if (lp == null || lp.Data == null || lp.Data.IsDead) return;

                    bool isSeer = Seer.seer != null
                                  && lp.PlayerId == Seer.seer.PlayerId
                                  && !Seer.seer.Data.IsDead;

                    if (isSeer && OptionSeerTrueFlash != null && OptionSeerTrueFlash.getBool() && Vip.showColor)
                        // White = true crewmate colour (Spy is a Crew role; matches VIP showColor scheme).
                        // Only when Vip.showColor is on — with colours off there is nothing to reveal,
                        // so the Seer falls through to the red evil flash like everyone else.
                        Helpers.showFlash(Color.white, 1.5f);
                    else
                        // Red = evil flash (Spy appeared impostor-like)
                        Helpers.showFlash(Spy.color, 1.5f);
                } catch (Exception e) {
                    UsefulTORStuffPlugin.Logger?.LogError($"[SpyExtras] DeathFlash postfix failed: {e}");
                }
            }
        }

        // TheOtherRoles.GameHistory is internal, so its overrideDeathReasonAndKiller
        // can't be called directly from this assembly. Resolve it once via reflection
        // (mirrors the rest of this mod's TOR-internal access pattern).
        private static MethodInfo _overrideDeathReason;
        private static bool _overrideDeathReasonResolved;

        private static void OverrideDeathReasonAndKiller(
            PlayerControl player, DeadPlayer.CustomDeathReason deathReason, PlayerControl killer) {
            if (!_overrideDeathReasonResolved) {
                _overrideDeathReasonResolved = true;
                var torAsm = typeof(CustomOption).Assembly;
                var type = torAsm.GetType("TheOtherRoles.GameHistory");
                _overrideDeathReason = type?.GetMethod(
                    "overrideDeathReasonAndKiller",
                    BindingFlags.Public | BindingFlags.Static, null,
                    new[] { typeof(PlayerControl), typeof(DeadPlayer.CustomDeathReason), typeof(PlayerControl) },
                    null);
                if (_overrideDeathReason == null)
                    UsefulTORStuffPlugin.Logger?.LogWarning(
                        "[SpyExtras] GameHistory.overrideDeathReasonAndKiller not found — death reason won't be tagged.");
            }
            _overrideDeathReason?.Invoke(null, new object[] { player, deathReason, killer });
        }

        // Shifter interaction: prefix intercepts before TOR processes the shift.
        // Mode 0 falls through to TOR (vanilla). Modes 1/2 cancel TOR's shift (return false).
        [HarmonyPatch(typeof(RPCProcedure), nameof(RPCProcedure.shifterShift))]
        static class ShifterSpyInteractionPatch {
            public static bool Prefix(byte targetId) {
                try {
                    if (OptionShifterInteraction == null) return true;
                    int mode = OptionShifterInteraction.getSelection();
                    if (mode == 0) return true; // Shift Succeeds = vanilla

                    var target = Helpers.playerById(targetId);
                    if (target == null || target != Spy.spy) return true;

                    PlayerControl oldShifter = Shifter.shifter;
                    Shifter.futureShift = null;
                    Shifter.clearAndReload();

                    if (mode == 1) {
                        // Shifter Dies — mirrors RPC.cs:602-611
                        if (oldShifter != null && !oldShifter.Data.IsDead) {
                            oldShifter.Exiled();
                            // GameHistory is internal in TOR, so call via reflection.
                            OverrideDeathReasonAndKiller(
                                oldShifter, DeadPlayer.CustomDeathReason.Shift, target);
                        }
                    } else {
                        // Shift Cancelled, No One Dies
                        bool giveback = OptionShifterGetsShiftBack != null
                                        && OptionShifterGetsShiftBack.getBool();
                        if (giveback && oldShifter != null
                            && PlayerControl.LocalPlayer != null
                            && PlayerControl.LocalPlayer.PlayerId == oldShifter.PlayerId) {
                            CustomButton.ResetAllCooldowns();
                        }
                    }

                    return false; // cancel TOR's shift in both non-vanilla modes
                } catch (Exception e) {
                    UsefulTORStuffPlugin.Logger?.LogError($"[SpyExtras] ShifterInteraction prefix failed: {e}");
                    return true;
                }
            }
        }
    }
}
