// Useful TOR Stuff - Copyright (C) 2026 DaUnknown-0
// Licensed under GPL-3.0-or-later. See LICENSE for details.
// Based on The Other Roles (https://github.com/TheOtherRolesAU/TheOtherRoles), GPL-3.0.

/*
 * LoverRevenger - "Delay Lover Death" + a new "Revenger" path for the surviving Lover.
 *
 * Vanilla TOR: with "Both Lovers Die" ON, when one Lover dies the other dies INSTANTLY
 * (PlayerControlPatch.MurderPlayer postfix triggers otherLover.MurderPlayer; Exiled() for exile).
 *
 * With "Delay Lover Death" ON (and the first Lover was KILLED, not exiled) we suppress that instant
 * suicide and defer the decision to the END OF THE NEXT MEETING (ExileController.WrapUp):
 *   - A %-roll (RevengerChance, like the Lawyer->Prosecutor chance) decides whether the surviving
 *     Lover becomes a REVENGER (lives on) or dies now (delayed Lover suicide).
 *   - The Revenger gets a Sheriff-like kill button. A host option picks the mode:
 *       * "Targeted Justice": may only correctly kill the Lover's killer. Correct kill -> game ends
 *         immediately with a Lovers win. Wrong target -> misfire, the Revenger dies.
 *       * "Blind Rage": may kill anyone. If they happen to hit the real killer -> win (as above).
 *         Otherwise they die at the end of the next meeting, with a random rage chat message.
 *
 * Exile case (the first Lover was voted out -> no single killer): NO Revenger; the surviving Lover
 * dies at the end of the next meeting (vanilla suicide via Exiled(), which we never intercept).
 *
 * ARCHITECTURE: unlike SheriffParityWin (host-authoritative), this is inherently CLIENT-SIDE (new
 * button, local kills, chat), so it is GATED on "everyone has the mod" (UsefulVersionHandshake),
 * exactly like the Snitch fix. The host gets a lobby warning otherwise. State is synced via a small
 * custom RPC (252); the kills themselves reuse TOR's UncheckedMurderPlayer.
 *
 * The instant-suicide suppression flips Lovers.bothDie OFF for the duration of the triggering
 * MurderPlayer so TOR's own (bothDie-gated) suicide+death-reason block is skipped cleanly, then
 * restores it in a last-priority postfix. The immediate win reuses TOR's internal
 * CheckEndCriteriaPatch.CheckAndEndGameForLoverWin (patched via reflection, like SheriffParityWin).
 */

using System;
using System.Reflection;
using HarmonyLib;
using Hazel;
using UnityEngine;
using TheOtherRoles;
using TheOtherRoles.Objects;
using TheOtherRoles.Patches;
using TheOtherRoles.Utilities;
using static TheOtherRoles.TheOtherRoles;
using Types = TheOtherRoles.CustomOption.CustomOptionType;

namespace UsefulTORStuff {
    public static class LoverRevenger {
        // ---- Options ----
        public static CustomOption DelayOption;      // toggle, child of modifierLoverBothDie
        public static CustomOption RevengerChance;   // rates (0..100%)
        public static CustomOption RevengerMode;     // 0 = Targeted Justice, 1 = Blind Rage
        public static CustomOption RevengerCooldown; // kill cooldown

        // ---- Mode constants ----
        private const int ModeTargeted = 0;
        private const int ModeBlindRage = 1;

        // ---- Runtime state (reset each round) ----
        public static PlayerControl revenger;        // the surviving Lover turned Revenger (or null)
        public static int revengerMode;
        public static byte killerId = byte.MaxValue; // the Lover's killer = Revenger's "correct" target

        // Pending decision (surviving Lover whose suicide we suppressed, awaiting the next meeting end)
        public static bool pendingArmed;
        public static PlayerControl pendingLover;
        public static byte pendingKillerId = byte.MaxValue;

        public static bool rageKillDone;             // Blind-Rage Revenger used their kill -> dies next meeting
        public static bool triggerRevengerWin;       // host: tells CheckEndCriteria to end the game now
        public static bool revengerWinResolved;      // OnGameEnd: rebuild winners as Lovers + Revenger

        private static bool active;                  // feature usable this game (gating + option ON)
        private static bool griefChatShown;          // first-meeting grief message guard
        private static PlayerControl currentTarget;  // Revenger's nearest target (for the button)
        private static CustomButton revengerButton;

        // bothDie-flip bookkeeping for the suppression prefix/postfix
        private static bool flipArmed;
        private static PlayerControl flipVictim;
        private static PlayerControl flipPartner;
        private static byte flipKiller = byte.MaxValue;

        // ---- Custom RPC (252) subtypes ----
        private const byte RpcId = 252;
        private const byte SubDecision = 0;  // loverId, becomeRevenger, killerId, mode
        private const byte SubRageDeath = 1; // revengerId, msgIndex
        private const byte SubWin = 2;       // revengerId
        private const byte SubRageArmed = 3; // revengerId

        // Resolved once from TOR's internal CustomRPC enum (fallback 108 = UncheckedMurderPlayer).
        private static byte uncheckedMurderRpc = 108;

        // GameHistory is internal in TOR, so its death-reason override is called via reflection.
        private static MethodInfo overrideDeathMethod;

        // ---- Flavor texts ----
        private static readonly string[] GriefTexts = {
            "A part of your soul was just ripped away. The silence where their heartbeat was is deafening.",
            "Your other half is gone. Grief claws at you — will it break you, or forge you into something darker?",
            "You can still feel their hand in yours, though it's growing cold. Something inside you is changing.",
            "The world tilts. Half of you is dead, and the other half is starting to burn.",
            "Their love kept you whole. Now only the wreckage remains... and a terrible, rising urge."
        };
        private static readonly string[] AwakenTargeted = {
            "You know exactly who did this. Hunt them down — and only them.",
            "Grief sharpens into purpose. Their killer will answer to you, and no one else."
        };
        private static readonly string[] AwakenRage = {
            "Rage takes hold. Someone is going to bleed for this.",
            "You can't think straight — only the killing will quiet the screaming inside."
        };
        private static readonly string[] RageDeathTexts = {
            "Your rage blinded you, and once you cooled down you saw an innocent's blood on your hands. The guilt finishes what grief started.",
            "When the red haze lifted, you understood what you'd done. You could not live with it.",
            "You struck out in fury and ended the wrong life. Shame swallows you whole.",
            "Vengeance demanded blood, but it was the wrong blood. Your heart simply gives out.",
            "The fog of wrath clears too late — an innocent lies dead, and you cannot bear to remain."
        };

        // ====================================================================
        // Options
        // ====================================================================
        public static void CreateOptions() {
            try {
                DelayOption = CustomOption.Create(
                    1290, Types.Modifier, "Delay Lover Death (Revenger)",
                    false, CustomOptionHolder.modifierLoverBothDie);
                RevengerChance = CustomOption.Create(
                    1291, Types.Modifier, "Chance Surviving Lover Becomes Revenger",
                    CustomOptionHolder.rates, DelayOption);
                RevengerMode = CustomOption.Create(
                    1292, Types.Modifier, "Revenger Mode",
                    new string[] { "Targeted Justice", "Blind Rage" }, RevengerChance);
                RevengerCooldown = CustomOption.Create(
                    1293, Types.Modifier, "Revenger Kill Cooldown",
                    30f, 10f, 60f, 2.5f, RevengerChance);

                // Place directly under the existing Lover modifier options (same approach as
                // LawyerLoverTracker). Insert after "Enable Lover Chat" (or the tracker options).
                var opts = CustomOption.options;
                foreach (var o in new[] { DelayOption, RevengerChance, RevengerMode, RevengerCooldown })
                    opts.Remove(o);
                int idx = opts.IndexOf(CustomOptionHolder.modifierLoverEnableChat);
                if (idx < 0) idx = opts.Count - 1;
                opts.Insert(idx + 1, DelayOption);
                opts.Insert(idx + 2, RevengerChance);
                opts.Insert(idx + 3, RevengerMode);
                opts.Insert(idx + 4, RevengerCooldown);

                UsefulTORStuffPlugin.Logger?.LogInfo("[LoverRevenger] Options created under Lovers.");
            } catch (Exception e) {
                UsefulTORStuffPlugin.Logger?.LogError($"[LoverRevenger] CreateOptions failed: {e}");
            }
        }

        // ====================================================================
        // Reflection patches: CheckAndEndGameForLoverWin + resolve the UncheckedMurderPlayer RPC id.
        // (All other patches are attribute-based and picked up by PatchAll.)
        // ====================================================================
        public static void TryPatch(Harmony harmony) {
            try {
                var torAsm = typeof(CustomOption).Assembly;

                // Resolve the UncheckedMurderPlayer RPC byte from TOR's internal CustomRPC enum.
                try {
                    var rpcEnum = torAsm.GetType("TheOtherRoles.CustomRPC");
                    if (rpcEnum != null)
                        uncheckedMurderRpc = (byte)(int)Enum.Parse(rpcEnum, "UncheckedMurderPlayer");
                } catch (Exception ex) {
                    UsefulTORStuffPlugin.Logger?.LogWarning($"[LoverRevenger] Could not resolve UncheckedMurderPlayer RPC id, using {uncheckedMurderRpc}: {ex.Message}");
                }

                // Resolve GameHistory.overrideDeathReasonAndKiller (internal type).
                try {
                    var ghType = torAsm.GetType("TheOtherRoles.GameHistory");
                    overrideDeathMethod = ghType?.GetMethod("overrideDeathReasonAndKiller",
                        BindingFlags.Public | BindingFlags.Static);
                } catch (Exception ex) {
                    UsefulTORStuffPlugin.Logger?.LogWarning($"[LoverRevenger] Could not resolve overrideDeathReasonAndKiller: {ex.Message}");
                }

                var type = torAsm.GetType("TheOtherRoles.Patches.CheckEndCriteriaPatch");
                if (type == null) {
                    UsefulTORStuffPlugin.Logger?.LogWarning("[LoverRevenger] CheckEndCriteriaPatch not found — instant win disabled.");
                    return;
                }
                var loverWin = type.GetMethod("CheckAndEndGameForLoverWin",
                    BindingFlags.NonPublic | BindingFlags.Static);
                if (loverWin == null) {
                    UsefulTORStuffPlugin.Logger?.LogWarning("[LoverRevenger] CheckAndEndGameForLoverWin not found — instant win disabled.");
                    return;
                }
                harmony.Patch(loverWin, prefix: new HarmonyMethod(typeof(LoverRevenger), nameof(LoverWinPrefix)));
                UsefulTORStuffPlugin.Logger?.LogInfo("[LoverRevenger] Patched CheckAndEndGameForLoverWin for the Revenger win.");
            } catch (Exception e) {
                UsefulTORStuffPlugin.Logger?.LogError($"[LoverRevenger] TryPatch failed: {e}");
            }
        }

        // Prefix on TOR's host-only CheckAndEndGameForLoverWin: when a Revenger win is pending, end
        // the game immediately as a Lovers win. Runs host-side (CheckEndCriteria is host-only).
        public static bool LoverWinPrefix(ref bool __result) {
            try {
                if (triggerRevengerWin) {
                    triggerRevengerWin = false;
                    revengerWinResolved = true;
                    GameManager.Instance.RpcEndGame((GameOverReason)10 /* CustomGameOverReason.LoversWin */, false);
                    __result = true;
                    return false;
                }
            } catch (Exception e) {
                UsefulTORStuffPlugin.Logger?.LogError($"[LoverRevenger] LoverWinPrefix failed: {e}");
            }
            return true;
        }

        // ====================================================================
        // Helpers
        // ====================================================================
        private static bool EveryoneHasMod() {
            try { return UsefulVersionHandshake.BuildMismatchMessage() == ""; }
            catch { return false; }
        }

        private static bool IsLover(PlayerControl p) =>
            p != null && (p == Lovers.lover1 || p == Lovers.lover2);

        private static PlayerControl PartnerOf(PlayerControl p) {
            if (p == Lovers.lover1) return Lovers.lover2;
            if (p == Lovers.lover2) return Lovers.lover1;
            return null;
        }

        private static void PostChat(PlayerControl source, string text) {
            try {
                var hud = HudManager.Instance;
                if (hud != null && hud.Chat != null && source != null)
                    hud.Chat.AddChat(source, text);
            } catch { }
        }

        private static string Pick(string[] arr) => arr[rnd.Next(arr.Length)];

        // GameHistory.overrideDeathReasonAndKiller(player, LoverSuicide) via reflection (internal type).
        private static void OverrideLoverSuicide(PlayerControl p) {
            try {
                overrideDeathMethod?.Invoke(null,
                    new object[] { p, DeadPlayer.CustomDeathReason.LoverSuicide, null });
            } catch (Exception e) {
                UsefulTORStuffPlugin.Logger?.LogError($"[LoverRevenger] OverrideLoverSuicide failed: {e}");
            }
        }

        // Perform an unchecked murder on every client (local call + RPC), like the Sheriff.
        private static void RpcUncheckedMurder(byte sourceId, byte targetId) {
            try {
                MessageWriter w = AmongUsClient.Instance.StartRpcImmediately(
                    PlayerControl.LocalPlayer.NetId, uncheckedMurderRpc, SendOption.Reliable, -1);
                w.Write(sourceId);
                w.Write(targetId);
                w.Write(byte.MaxValue); // showAnimation
                AmongUsClient.Instance.FinishRpcImmediately(w);
                RPCProcedure.uncheckedMurderPlayer(sourceId, targetId, byte.MaxValue);
            } catch (Exception e) {
                UsefulTORStuffPlugin.Logger?.LogError($"[LoverRevenger] RpcUncheckedMurder failed: {e}");
            }
        }

        // ---- Custom RPC senders (each also applies locally; the sender never receives its own RPC) ----
        private static MessageWriter BeginRpc(byte subtype) {
            MessageWriter w = AmongUsClient.Instance.StartRpcImmediately(
                PlayerControl.LocalPlayer.NetId, RpcId, SendOption.Reliable, -1);
            w.Write(subtype);
            return w;
        }

        private static void SendDecision(byte loverId, bool becomeRevenger, byte revKillerId, byte mode) {
            try {
                var w = BeginRpc(SubDecision);
                w.Write(loverId);
                w.Write((byte)(becomeRevenger ? 1 : 0));
                w.Write(revKillerId);
                w.Write(mode);
                AmongUsClient.Instance.FinishRpcImmediately(w);
                ApplyDecision(loverId, becomeRevenger, revKillerId, mode);
            } catch (Exception e) {
                UsefulTORStuffPlugin.Logger?.LogError($"[LoverRevenger] SendDecision failed: {e}");
            }
        }

        private static void SendRageArmed(byte revengerId) {
            try {
                var w = BeginRpc(SubRageArmed);
                w.Write(revengerId);
                AmongUsClient.Instance.FinishRpcImmediately(w);
                ApplyRageArmed(revengerId);
            } catch (Exception e) { UsefulTORStuffPlugin.Logger?.LogError($"[LoverRevenger] SendRageArmed failed: {e}"); }
        }

        private static void SendRageDeath(byte revengerId, byte msgIndex) {
            try {
                var w = BeginRpc(SubRageDeath);
                w.Write(revengerId);
                w.Write(msgIndex);
                AmongUsClient.Instance.FinishRpcImmediately(w);
                ApplyRageDeath(revengerId, msgIndex);
            } catch (Exception e) { UsefulTORStuffPlugin.Logger?.LogError($"[LoverRevenger] SendRageDeath failed: {e}"); }
        }

        private static void SendWin(byte revengerId) {
            try {
                var w = BeginRpc(SubWin);
                w.Write(revengerId);
                AmongUsClient.Instance.FinishRpcImmediately(w);
                ApplyWin(revengerId);
            } catch (Exception e) { UsefulTORStuffPlugin.Logger?.LogError($"[LoverRevenger] SendWin failed: {e}"); }
        }

        // ---- Custom RPC appliers (run on every client) ----
        private static void ApplyDecision(byte loverId, bool becomeRevenger, byte revKillerId, byte mode) {
            pendingArmed = false;
            pendingLover = null;
            var lover = Helpers.playerById(loverId);
            if (lover == null) return;

            if (becomeRevenger) {
                revenger = lover;
                revengerMode = mode;
                killerId = revKillerId;
                if (lover == PlayerControl.LocalPlayer)
                    PostChat(lover, Pick(mode == ModeBlindRage ? AwakenRage : AwakenTargeted));
            } else if (!lover.Data.IsDead) {
                // Roll failed: the delayed Lover suicide happens now (every client kills locally).
                RPCProcedure.uncheckedMurderPlayer(loverId, loverId, byte.MaxValue);
                OverrideLoverSuicide(lover);
            }
        }

        private static void ApplyRageArmed(byte revengerId) {
            rageKillDone = true;
        }

        private static void ApplyRageDeath(byte revengerId, byte msgIndex) {
            rageKillDone = false;
            var rev = Helpers.playerById(revengerId);
            if (rev == null) return;
            if (!rev.Data.IsDead) {
                RPCProcedure.uncheckedMurderPlayer(revengerId, revengerId, byte.MaxValue);
                OverrideLoverSuicide(rev);
            }
            if (msgIndex < RageDeathTexts.Length)
                PostChat(rev, RageDeathTexts[msgIndex]);
            revenger = null;
        }

        private static void ApplyWin(byte revengerId) {
            triggerRevengerWin = true;
            revengerWinResolved = true;
        }

        // ====================================================================
        // RPC receiver
        // ====================================================================
        [HarmonyPatch(typeof(PlayerControl), nameof(PlayerControl.HandleRpc))]
        [HarmonyPriority(Priority.High)]
        static class HandleRpcPatch {
            public static bool Prefix(byte callId, MessageReader reader) {
                if (callId != RpcId) return true;
                try {
                    byte subtype = reader.ReadByte();
                    switch (subtype) {
                        case SubDecision: {
                            byte loverId = reader.ReadByte();
                            bool become = reader.ReadByte() != 0;
                            byte k = reader.ReadByte();
                            byte mode = reader.ReadByte();
                            ApplyDecision(loverId, become, k, mode);
                            break;
                        }
                        case SubRageArmed: ApplyRageArmed(reader.ReadByte()); break;
                        case SubRageDeath: {
                            byte revId = reader.ReadByte();
                            byte idx = reader.ReadByte();
                            ApplyRageDeath(revId, idx);
                            break;
                        }
                        case SubWin: ApplyWin(reader.ReadByte()); break;
                    }
                } catch (Exception e) {
                    UsefulTORStuffPlugin.Logger?.LogError($"[LoverRevenger] HandleRpc failed: {e}");
                }
                return false;
            }
        }

        // ====================================================================
        // Round reset + game-start gating
        // ====================================================================
        [HarmonyPatch(typeof(RPCProcedure), nameof(RPCProcedure.resetVariables))]
        static class ResetPatch {
            public static void Postfix() {
                revenger = null;
                revengerMode = 0;
                killerId = byte.MaxValue;
                pendingArmed = false;
                pendingLover = null;
                pendingKillerId = byte.MaxValue;
                rageKillDone = false;
                triggerRevengerWin = false;
                revengerWinResolved = false;
                griefChatShown = false;
                currentTarget = null;
                flipArmed = false; flipVictim = null; flipPartner = null; flipKiller = byte.MaxValue;
            }
        }

        // Latch whether the feature is usable for the whole game once the intro ends.
        [HarmonyPatch(typeof(IntroCutscene), nameof(IntroCutscene.OnDestroy))]
        static class IntroEndPatch {
            public static void Postfix() {
                active = DelayOption != null && DelayOption.getBool()
                         && Lovers.bothDie && EveryoneHasMod();
            }
        }

        // ====================================================================
        // Suppress the instant Lover suicide when the first Lover is KILLED (delay enabled).
        // We flip Lovers.bothDie OFF for the duration of the triggering MurderPlayer so TOR's
        // bothDie-gated suicide+death-reason block is skipped, then restore it last.
        // ====================================================================
        [HarmonyPatch(typeof(PlayerControl), nameof(PlayerControl.MurderPlayer))]
        static class MurderPlayerSuppressPatch {
            public static void Prefix(PlayerControl __instance, [HarmonyArgument(0)] PlayerControl target) {
                try {
                    flipArmed = false;
                    if (!active) return;
                    PlayerControl killer = __instance, victim = target;
                    if (killer == null || victim == null || killer == victim) return; // real kill only
                    if (!IsLover(victim) || !Lovers.bothDie) return;
                    PlayerControl partner = PartnerOf(victim);
                    if (partner == null || partner.Data == null || partner.Data.IsDead) return; // partner must survive
                    if (pendingArmed || revenger != null) return; // already in a delay/revenger flow

                    // Skip TOR's suicide+override block (both guarded by Lovers.bothDie).
                    Lovers.bothDie = false;
                    flipArmed = true;
                    flipVictim = victim;
                    flipPartner = partner;
                    flipKiller = killer.PlayerId;
                } catch (Exception e) {
                    UsefulTORStuffPlugin.Logger?.LogError($"[LoverRevenger] suppress prefix failed: {e}");
                }
            }

            [HarmonyPriority(Priority.Last)] // run after TOR's suicide postfix so the flip stays effective
            public static void Postfix() {
                try {
                    if (!flipArmed) return;
                    Lovers.bothDie = true; // restore
                    bool victimDied = flipVictim != null && flipVictim.Data != null && flipVictim.Data.IsDead;
                    if (victimDied && flipPartner != null && !flipPartner.Data.IsDead) {
                        // Arm the delayed decision for the next meeting end.
                        pendingArmed = true;
                        pendingLover = flipPartner;
                        pendingKillerId = flipKiller;
                        UsefulTORStuffPlugin.Logger?.LogInfo($"[LoverRevenger] Delayed Lover death armed (partner {flipPartner.Data?.PlayerName}, killer id {flipKiller}).");
                    }
                    flipArmed = false; flipVictim = null; flipPartner = null; flipKiller = byte.MaxValue;
                } catch (Exception e) {
                    UsefulTORStuffPlugin.Logger?.LogError($"[LoverRevenger] suppress postfix failed: {e}");
                }
            }
        }

        // ====================================================================
        // First-meeting grief message (local, surviving Lover only).
        // ====================================================================
        [HarmonyPatch(typeof(MeetingHud), nameof(MeetingHud.Start))]
        static class MeetingStartPatch {
            public static void Postfix() {
                try {
                    if (!pendingArmed || griefChatShown) return;
                    if (pendingLover != null && pendingLover == PlayerControl.LocalPlayer) {
                        griefChatShown = true;
                        PostChat(pendingLover, Pick(GriefTexts));
                    }
                } catch { }
            }
        }

        // ====================================================================
        // Decision / rage death at the end of the meeting (host-driven), on all exile-controller paths.
        // ====================================================================
        public static void OnMeetingEnd() {
            try {
                if (AmongUsClient.Instance == null || !AmongUsClient.Instance.AmHost) return;

                // 1) Resolve a pending Revenger decision.
                if (pendingArmed) {
                    if (pendingLover == null || pendingLover.Data == null
                        || pendingLover.Data.IsDead || pendingLover.Data.Disconnected) {
                        // Surviving Lover already gone -> nothing to decide.
                        pendingArmed = false; pendingLover = null;
                    } else {
                        int sel = RevengerChance != null ? RevengerChance.getSelection() : 0;
                        bool become = active && rnd.Next(1, 101) <= sel * 10;
                        byte mode = (byte)(RevengerMode != null ? RevengerMode.getSelection() : 0);
                        SendDecision(pendingLover.PlayerId, become, pendingKillerId, mode);
                    }
                }

                // 2) Blind-Rage Revenger who used their kill dies now (with a rage message).
                if (rageKillDone) {
                    if (revenger == null || revenger.Data == null || revenger.Data.IsDead) {
                        rageKillDone = false;
                    } else {
                        SendRageDeath(revenger.PlayerId, (byte)rnd.Next(RageDeathTexts.Length));
                    }
                }
            } catch (Exception e) {
                UsefulTORStuffPlugin.Logger?.LogError($"[LoverRevenger] OnMeetingEnd failed: {e}");
            }
        }

        [HarmonyPatch(typeof(ExileController), nameof(ExileController.WrapUp))]
        static class ExileWrapUpPatch {
            public static void Postfix() => OnMeetingEnd();
        }

        [HarmonyPatch(typeof(AirshipExileController), nameof(AirshipExileController.WrapUpAndSpawn))]
        static class AirshipExileWrapUpPatch {
            public static void Postfix() => OnMeetingEnd();
        }

        // ====================================================================
        // Revenger kill button + target selection.
        // ====================================================================
        private static bool LocalIsRevenger() =>
            active && revenger != null && revenger == PlayerControl.LocalPlayer
            && PlayerControl.LocalPlayer != null && !PlayerControl.LocalPlayer.Data.IsDead;

        [HarmonyPatch(typeof(HudManager), nameof(HudManager.Update))]
        static class HudUpdateTargetPatch {
            public static void Postfix() {
                try {
                    if (!LocalIsRevenger()) { currentTarget = null; return; }
                    if (MeetingHud.Instance != null || ExileController.Instance != null) return;
                    currentTarget = PlayerControlFixedUpdatePatch.setTarget();
                    PlayerControlFixedUpdatePatch.setPlayerOutline(currentTarget, Lovers.color);
                } catch { }
            }
        }

        [HarmonyPatch(typeof(HudManager), nameof(HudManager.Start))]
        static class HudStartButtonPatch {
            public static void Postfix(HudManager __instance) {
                try {
                    if (revengerButton != null && revengerButton.actionButton != null) return;
                    revengerButton = new CustomButton(
                        OnRevengerKill,
                        LocalIsRevenger,
                        () => currentTarget != null && PlayerControl.LocalPlayer != null && PlayerControl.LocalPlayer.CanMove,
                        () => { if (revengerButton != null) revengerButton.Timer = revengerButton.MaxTimer; },
                        __instance.KillButton.graphic.sprite,
                        CustomButton.ButtonPositions.upperRowRight,
                        __instance,
                        KeyCode.Q
                    );
                    revengerButton.MaxTimer = RevengerCooldown != null ? RevengerCooldown.getFloat() : 30f;
                    revengerButton.Timer = revengerButton.MaxTimer;
                } catch (Exception e) {
                    UsefulTORStuffPlugin.Logger?.LogError($"[LoverRevenger] button creation failed: {e}");
                }
            }
        }

        private static void OnRevengerKill() {
            try {
                if (!LocalIsRevenger() || currentTarget == null) return;
                var result = Helpers.checkMuderAttempt(revenger, currentTarget);
                if (result != MurderAttemptResult.PerformKill) return;

                byte targetId = currentTarget.PlayerId;
                bool correct = targetId == killerId;

                if (correct) {
                    // Right target in either mode -> kill and win instantly.
                    RpcUncheckedMurder(revenger.PlayerId, targetId);
                    SendWin(revenger.PlayerId);
                } else if (revengerMode == ModeTargeted) {
                    // Wrong target -> misfire, the Revenger dies.
                    RpcUncheckedMurder(revenger.PlayerId, revenger.PlayerId);
                } else {
                    // Blind Rage, wrong target -> kill them, then die at the next meeting end.
                    RpcUncheckedMurder(revenger.PlayerId, targetId);
                    SendRageArmed(revenger.PlayerId);
                }

                if (revengerButton != null) revengerButton.Timer = revengerButton.MaxTimer;
                currentTarget = null;
            } catch (Exception e) {
                UsefulTORStuffPlugin.Logger?.LogError($"[LoverRevenger] OnRevengerKill failed: {e}");
            }
        }

        // ====================================================================
        // End-of-game winner override: when a Revenger won, the winners are the (dead) Lovers + Revenger.
        // ====================================================================
        [HarmonyPatch(typeof(AmongUsClient), nameof(AmongUsClient.OnGameEnd))]
        [HarmonyPriority(Priority.Last)] // after TOR's OnGameEndPatch.Postfix
        static class OnGameEndOverridePatch {
            public static void Postfix() {
                try {
                    if (!revengerWinResolved) return;
                    var winners = EndGameResult.CachedWinners;
                    if (winners == null) return;
                    winners.Clear();
                    if (Lovers.lover1 != null) winners.Add(new CachedPlayerData(Lovers.lover1.Data));
                    if (Lovers.lover2 != null) winners.Add(new CachedPlayerData(Lovers.lover2.Data));
                    if (revenger != null
                        && (Lovers.lover1 == null || revenger.PlayerId != Lovers.lover1.PlayerId)
                        && (Lovers.lover2 == null || revenger.PlayerId != Lovers.lover2.PlayerId))
                        winners.Add(new CachedPlayerData(revenger.Data));
                } catch (Exception e) {
                    UsefulTORStuffPlugin.Logger?.LogError($"[LoverRevenger] OnGameEnd override failed: {e}");
                }
            }
        }
    }
}
