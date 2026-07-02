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
 *     Lover becomes a REVENGER (lives on) or dies now (delayed Lover suicide). This applies to any
 *     surviving Lover regardless of role, including Impostor/Jackal Lovers.
 *   - The Revenger shows as "Revenger" (own RoleInfo, keeping the Lovers color) in name tags from the
 *     awakening on, in the role tab and the end-game summary. The WIN, however, counts as a Lovers win
 *     for exactly the two Lovers (the fallen one + the Revenger) — end screen "Lovers Win".
 *   - A NON-killer (crew) Revenger gets a Sheriff-like kill button. A host option picks the mode:
 *       * "Targeted Justice": may only correctly kill the Lover's killer. Correct kill -> game ends
 *         immediately as a Lovers win. Wrong target -> misfire, the Revenger dies.
 *       * "Blind Rage": may kill anyone. If they happen to hit the real killer -> win (as above).
 *         Otherwise they die at the end of the next meeting, with a random rage chat message.
 *   - A Revenger with their OWN kill button (Impostor, neutral killers like Jackal/Sidekick/Thief, or
 *     the Sheriff) gets NO second button: their normal kill on the Lover's killer triggers the win
 *     (modes/misfire/rage don't apply to them). Detection via Helpers.isKiller + Sheriff.
 *
 * Guess case (a Lover is shot by a Guesser): ALSO arms the Revenger, with the Guesser as the target.
 * TOR kills a guessed Lover via Exiled(), so we intercept RPCProcedure.guesserShoot (same bothDie flip).
 *
 * Vote-exile case (the first Lover was voted out -> no single killer): NO Revenger; the surviving Lover
 * dies at the end of the next meeting (vanilla suicide via Exiled(), which we never intercept).
 *
 * ARCHITECTURE: unlike SheriffParityWin (host-authoritative), this is inherently CLIENT-SIDE (new
 * button, local kills, chat), so it is GATED on "everyone has the mod" (UsefulVersionHandshake),
 * exactly like the Snitch fix. The host gets a lobby warning otherwise. State is synced via a small
 * custom RPC (247); the kills themselves reuse TOR's UncheckedMurderPlayer.
 *
 * The instant-suicide suppression flips Lovers.bothDie OFF for the duration of the triggering
 * MurderPlayer so TOR's own (bothDie-gated) suicide+death-reason block is skipped cleanly, then
 * restores it in a last-priority postfix. The win uses TOR's internal
 * CheckEndCriteriaPatch.CheckAndEndGameForLoverWin only as a host-only entry point (patched via
 * reflection, like SheriffParityWin), but ends the game with a SEPARATE CustomGameOverReason (17), so
 * we control the winners (the two Lovers) and a "Lovers Win" end screen independently of TOR.
 */

using System;
using System.Collections.Generic;
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

        // Win-display snapshot. Set when the win is triggered; read at OnGameEnd and on the end screen.
        // Deliberately kept OUT of the per-round resetVariables reset: TOR calls resetVariables from its
        // OWN OnGameEnd postfix (EndGamePatch.cs) which runs BEFORE ours, so anything reset there would
        // already be gone. Instead this is cleared at game start (IntroEndPatch).
        public static bool revengerWon;
        // The win counts as a Lovers win for exactly the two Lovers (the fallen one + the Revenger),
        // so we snapshot both Lover infos at win time.
        private static NetworkedPlayerInfo loverData1, loverData2;

        // Separate CustomGameOverReason for the Revenger win. TOR's internal enum uses 10..16; 17 is ours.
        private const int RevengerWinReason = 17;

        // The Revenger's own neutral role identity (own name, keeps the Lovers color). Built lazily so
        // Lovers.color is initialised. RoleId.Lover is reused purely as a display tag (we never look the
        // Revenger up by RoleId; TryAdd in the RoleInfo ctor no-ops since Lover is already registered).
        private static RoleInfo revengerInfo;
        private static RoleInfo RevengerInfo() =>
            revengerInfo ??= new RoleInfo("Revenger", Lovers.color,
                "Avenge your fallen partner", "Avenge your fallen partner", RoleId.Lover, true);

        // Make the Revenger guessable by listing its (singleton) RoleInfo in allRoleInfos while the
        // feature is active. The Guesser UI builds its options from allRoleInfos, and correctness is a
        // reference compare against getRoleInfoForPlayer(target).First — both resolve to this instance.
        // Listed only when active (like a spawn-rate>0 role) and removed each round (resetVariables).
        private static void SetGuessable(bool on) {
            try {
                var list = RoleInfo.allRoleInfos;
                var info = RevengerInfo();
                if (on) { if (!list.Contains(info)) list.Add(info); }
                else list.Remove(info);
            } catch (Exception e) {
                UsefulTORStuffPlugin.Logger?.LogError($"[LoverRevenger] SetGuessable failed: {e}");
            }
        }

        private static bool active;                  // feature usable this game (gating + option ON)
        private static bool griefChatShown;          // first-meeting grief message guard
        private static PlayerControl currentTarget;  // Revenger's nearest target (for the button)
        private static CustomButton revengerButton;

        // bothDie-flip bookkeeping for the MurderPlayer suppression prefix/postfix
        private static bool flipArmed;
        private static PlayerControl flipVictim;
        private static PlayerControl flipPartner;
        private static byte flipKiller = byte.MaxValue;

        // same, for the Guesser path (guesserShoot -> Exiled-based partner suicide)
        private static bool gFlipArmed;
        private static PlayerControl gVictim;
        private static PlayerControl gPartner;
        private static byte gKiller = byte.MaxValue;

        // ---- Custom RPC (247) subtypes ----
        // NOTE: 247, NOT 252 — 252 is BomberCancel's CancelBombRpcId. Both live in this same plugin, so
        // sharing the id made each HandleRpc prefix mis-read the other's payload (a stray subtype byte
        // could fire uncheckedMurderPlayer -> a player dies for no reason). Keep these in-plugin unique.
        private const byte RpcId = 247;
        private const byte SubDecision = 0;  // loverId, becomeRevenger, killerId, mode
        private const byte SubRageDeath = 1; // revengerId, msgIndex
        private const byte SubWin = 2;       // revengerId
        private const byte SubRageArmed = 3; // revengerId
        private const byte SubDeniedDeath = 4; // revengerId, msgIndex (target died first -> revenge denied)

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
        private static readonly string[] RevengeDeniedTexts = {
            "Someone else got to your partner's killer first. With nothing left to avenge, your heart gives out.",
            "Your target is dead — by another hand. The revenge that kept you breathing is gone, and so are you.",
            "They died before you could make them pay. Robbed of your vengeance, you fade away.",
            "Justice found your enemy without you. Empty and purposeless, you follow your love into the dark.",
            "The one you swore to kill is already gone. There is nothing left to hold you here."
        };

        // ====================================================================
        // Options
        // ====================================================================
        public static void CreateOptions() {
            try {
                // IDs 1294-1297 (NOT 1290-1293): 1290 is InvertVision's "Inverted Vision". A shared
                // option id makes both options read the same stored selection, so DelayOption would
                // silently track Inverted Vision's value (feature looks "off" -> no suppression).
                // Parent directly under the Lovers modifier (not "Both Lovers Die"): TOR only checks an
                // option's parent + grandparent for visibility, so a deeper chain can't see the Lovers
                // rate. Keeping all sub-options as direct children of DelayOption (itself a child of
                // Lovers) means they ALL hide when Lovers = 0% or "Delay Lover Death" is Off - i.e. only
                // shown when a Revenger can actually exist. ("Both Lovers Die" is still required and is
                // enforced at runtime.)
                DelayOption = CustomOption.Create(
                    1294, Types.Modifier, "Delay Lover Death (Revenger)",
                    false, CustomOptionHolder.modifierLover);
                RevengerChance = CustomOption.Create(
                    1295, Types.Modifier, "Chance Surviving Lover Becomes Revenger",
                    CustomOptionHolder.rates, DelayOption);
                RevengerMode = CustomOption.Create(
                    1296, Types.Modifier, "Revenger Mode",
                    new string[] { "Targeted Justice", "Blind Rage" }, DelayOption);
                RevengerCooldown = CustomOption.Create(
                    1297, Types.Modifier, "Revenger Kill Cooldown",
                    30f, 10f, 60f, 2.5f, DelayOption);

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

                // Block the Impostor/Jackal parity win while a Revenger is alive.
                var blocker = new HarmonyMethod(typeof(LoverRevenger), nameof(ParityWinBlockPrefix));
                var impWin = type.GetMethod("CheckAndEndGameForImpostorWin", BindingFlags.NonPublic | BindingFlags.Static);
                var jackalWin = type.GetMethod("CheckAndEndGameForJackalWin", BindingFlags.NonPublic | BindingFlags.Static);
                if (impWin != null) { harmony.Patch(impWin, prefix: blocker); UsefulTORStuffPlugin.Logger?.LogInfo("[LoverRevenger] Patched CheckAndEndGameForImpostorWin (parity block)."); }
                else UsefulTORStuffPlugin.Logger?.LogWarning("[LoverRevenger] CheckAndEndGameForImpostorWin not found — parity block disabled for impostors.");
                if (jackalWin != null) { harmony.Patch(jackalWin, prefix: blocker); UsefulTORStuffPlugin.Logger?.LogInfo("[LoverRevenger] Patched CheckAndEndGameForJackalWin (parity block)."); }
                else UsefulTORStuffPlugin.Logger?.LogWarning("[LoverRevenger] CheckAndEndGameForJackalWin not found — parity block disabled for jackal.");
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
                    // Separate neutral win: ends with our own reason (17), NOT LoversWin (10).
                    GameManager.Instance.RpcEndGame((GameOverReason)RevengerWinReason, false);
                    __result = true;
                    return false;
                }
            } catch (Exception e) {
                UsefulTORStuffPlugin.Logger?.LogError($"[LoverRevenger] LoverWinPrefix failed: {e}");
            }
            return true;
        }

        // While a Revenger is alive they remain a lethal independent threat, so the Impostors/Jackal
        // cannot claim a numerical (parity) win — they must kill the Revenger first. Mirrors how TOR's
        // own impostor/jackal win checks block on the rival killer team still being alive.
        private static bool RevengerAlive() =>
            active && revenger != null && revenger.Data != null && !revenger.Data.IsDead;

        public static bool ParityWinBlockPrefix(ref bool __result) {
            try {
                if (RevengerAlive()) { __result = false; return false; }
            } catch (Exception e) {
                UsefulTORStuffPlugin.Logger?.LogError($"[LoverRevenger] ParityWinBlockPrefix failed: {e}");
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

        // A player who already has their OWN kill button. Such a Revenger keeps that single button
        // instead of getting a second (Revenger) button — their normal kill on the Lover's killer
        // triggers the win. Uses TOR's canonical Helpers.isKiller (Impostor + neutral killers like
        // Jackal/Sidekick/Thief) plus the Sheriff (a crew killer that isKiller does NOT count).
        private static bool IsKiller(PlayerControl p) =>
            p != null && p.Data != null && (Helpers.isKiller(p)
                || p == Sheriff.sheriff || p == Sheriff.formerSheriff);

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

        private static void SendDeniedDeath(byte revengerId, byte msgIndex) {
            try {
                var w = BeginRpc(SubDeniedDeath);
                w.Write(revengerId);
                w.Write(msgIndex);
                AmongUsClient.Instance.FinishRpcImmediately(w);
                ApplyDeniedDeath(revengerId, msgIndex);
            } catch (Exception e) { UsefulTORStuffPlugin.Logger?.LogError($"[LoverRevenger] SendDeniedDeath failed: {e}"); }
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
                if (lover == PlayerControl.LocalPlayer) {
                    PostChat(lover, Pick(mode == ModeBlindRage ? AwakenRage : AwakenTargeted));
                    UTSAssets.PlayRevenger(); // dark awakening sting, Revenger-only
                    // A non-killer Revenger awakens NOW (mid-game). Guarantee the kill button exists at
                    // this exact moment - the HudManager.Start creation can be long gone by here, which is
                    // what left non-killers with no button. Killers use their own kill button (no second).
                    if (!IsKiller(lover)) EnsureRevengerButton(HudManager.Instance);
                }
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

        private static void ApplyDeniedDeath(byte revengerId, byte msgIndex) {
            var rev = Helpers.playerById(revengerId);
            if (rev == null) return;
            if (!rev.Data.IsDead) {
                RPCProcedure.uncheckedMurderPlayer(revengerId, revengerId, byte.MaxValue);
                OverrideLoverSuicide(rev);
            }
            if (msgIndex < RevengeDeniedTexts.Length)
                PostChat(rev, RevengeDeniedTexts[msgIndex]);
            revenger = null;
        }

        private static void ApplyWin(byte revengerId) {
            triggerRevengerWin = true;
            revengerWon = true;
            // Snapshot BOTH Lovers now (the win is a Lovers win for just the two of them); the fields
            // survive TOR's end-of-game resetVariables (see above).
            loverData1 = Lovers.lover1 != null ? Lovers.lover1.Data : null;
            loverData2 = Lovers.lover2 != null ? Lovers.lover2.Data : null;
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
                        case SubDeniedDeath: {
                            byte revId = reader.ReadByte();
                            byte idx = reader.ReadByte();
                            ApplyDeniedDeath(revId, idx);
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
                griefChatShown = false;
                currentTarget = null;
                flipArmed = false; flipVictim = null; flipPartner = null; flipKiller = byte.MaxValue;
                gFlipArmed = false; gVictim = null; gPartner = null; gKiller = byte.MaxValue;
                SetGuessable(false); // keep the guess list clean between rounds; re-added on intro end
            }
        }

        // Latch whether the feature is usable for the whole game once the intro ends.
        [HarmonyPatch(typeof(IntroCutscene), nameof(IntroCutscene.OnDestroy))]
        static class IntroEndPatch {
            public static void Postfix() {
                active = DelayOption != null && DelayOption.getBool()
                         && Lovers.bothDie && EveryoneHasMod();
                // List the Revenger as a guessable role only while the feature is actually usable.
                SetGuessable(active);
                // Clear the win snapshot at game start (NOT in resetVariables — see field comment).
                revengerWon = false;
                loverData1 = null;
                loverData2 = null;
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
        // Guesser path: a Lover killed by a Guesser also arms the Revenger, with the GUESSER as the
        // target. TOR kills a guessed Lover via dyingTarget.Exiled(), whose postfix runs the
        // (bothDie-gated) partner suicide. We flip Lovers.bothDie OFF around guesserShoot so that partner
        // suicide (and the meeting-UI partner-death handling, also bothDie-gated) is skipped, restore it
        // after, and arm the pending decision for the meeting end. Runs on every client like guesserShoot.
        // ====================================================================
        [HarmonyPatch(typeof(RPCProcedure), nameof(RPCProcedure.guesserShoot))]
        static class GuesserShootSuppressPatch {
            public static void Prefix([HarmonyArgument(0)] byte killerId, [HarmonyArgument(1)] byte dyingTargetId) {
                try {
                    gFlipArmed = false;
                    if (!active || !Lovers.bothDie) return;
                    var victim = Helpers.playerById(dyingTargetId);
                    if (victim == null || !IsLover(victim)) return;
                    var partner = PartnerOf(victim);
                    if (partner == null || partner.Data == null || partner.Data.IsDead) return; // partner must survive
                    if (partner.PlayerId == dyingTargetId) return;
                    if (pendingArmed || revenger != null) return; // already in a delay/revenger flow

                    Lovers.bothDie = false; // skip TOR's bothDie-gated partner suicide during Exiled()
                    gFlipArmed = true;
                    gVictim = victim;
                    gPartner = partner;
                    gKiller = killerId;
                } catch (Exception e) {
                    UsefulTORStuffPlugin.Logger?.LogError($"[LoverRevenger] guesserShoot prefix failed: {e}");
                }
            }

            [HarmonyPriority(Priority.Last)]
            public static void Postfix() {
                try {
                    if (!gFlipArmed) return;
                    Lovers.bothDie = true; // restore
                    bool victimDied = gVictim != null && gVictim.Data != null && gVictim.Data.IsDead;
                    if (victimDied && gPartner != null && !gPartner.Data.IsDead) {
                        pendingArmed = true;
                        pendingLover = gPartner;
                        pendingKillerId = gKiller;
                        UsefulTORStuffPlugin.Logger?.LogInfo($"[LoverRevenger] Delayed Lover death armed via guess (partner {gPartner.Data?.PlayerName}, guesser id {gKiller}).");
                    }
                    gFlipArmed = false; gVictim = null; gPartner = null; gKiller = byte.MaxValue;
                } catch (Exception e) {
                    UsefulTORStuffPlugin.Logger?.LogError($"[LoverRevenger] guesserShoot postfix failed: {e}");
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
                bool justAwakened = false;
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
                        justAwakened = become;
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

                // 3) Revenge denied: the Lover's killer died before the Revenger could strike (voted out
                //    or killed by someone else). With nothing left to avenge, the Revenger dies at this
                //    meeting's end. Skipped in the very meeting they awaken, and only once they exist.
                if (!justAwakened && !rageKillDone && killerId != byte.MaxValue
                    && revenger != null && revenger.Data != null && !revenger.Data.IsDead) {
                    var killer = Helpers.playerById(killerId);
                    if (killer == null || killer.Data == null || killer.Data.IsDead || killer.Data.Disconnected)
                        SendDeniedDeath(revenger.PlayerId, (byte)rnd.Next(RevengeDeniedTexts.Length));
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

        // Killing-role Revengers keep their own single kill button (their normal kill on the Lover's
        // killer triggers the win), so ONLY non-killer Revengers get the dedicated Revenger button +
        // target outline. This is what guarantees "only one kill button".
        private static bool LocalUsesRevengerButton() =>
            LocalIsRevenger() && !IsKiller(PlayerControl.LocalPlayer);

        [HarmonyPatch(typeof(HudManager), nameof(HudManager.Update))]
        static class HudUpdateTargetPatch {
            public static void Postfix() {
                try {
                    if (!LocalUsesRevengerButton()) { currentTarget = null; return; }
                    if (MeetingHud.Instance != null || ExileController.Instance != null) return;
                    currentTarget = PlayerControlFixedUpdatePatch.setTarget();
                    PlayerControlFixedUpdatePatch.setPlayerOutline(currentTarget, Lovers.color);
                } catch { }
            }
        }

        // Create the Revenger kill button if it does not currently exist (or its backing ActionButton was
        // torn down, e.g. across a HUD rebuild). Idempotent - a live button is left untouched. Called from
        // BOTH HudManager.Start AND the moment a local non-killer player actually awakens as the Revenger
        // (ApplyDecision). The awaken happens MID-GAME, long after HudManager.Start, so relying on the Start
        // creation alone left the button missing whenever that early instance was gone by awaken time -
        // which is exactly the "button never appeared" bug. Recreating at awaken guarantees it exists then.
        private static void EnsureRevengerButton(HudManager hud) {
            try {
                if (hud == null || hud.KillButton == null) return;
                if (revengerButton != null && revengerButton.actionButton != null) return;
                revengerButton = new CustomButton(
                    OnRevengerKill,
                    LocalUsesRevengerButton,
                    () => currentTarget != null && PlayerControl.LocalPlayer != null && PlayerControl.LocalPlayer.CanMove,
                    () => { if (revengerButton != null) revengerButton.Timer = revengerButton.MaxTimer; },
                    UTSAssets.RevengerIcon ?? hud.KillButton.graphic.sprite,
                    CustomButton.ButtonPositions.upperRowRight,
                    hud,
                    KeyCode.Q
                );
                revengerButton.MaxTimer = RevengerCooldown != null ? RevengerCooldown.getFloat() : 30f;
                revengerButton.Timer = revengerButton.MaxTimer;
            } catch (Exception e) {
                UsefulTORStuffPlugin.Logger?.LogError($"[LoverRevenger] button creation failed: {e}");
            }
        }

        [HarmonyPatch(typeof(HudManager), nameof(HudManager.Start))]
        static class HudStartButtonPatch {
            public static void Postfix(HudManager __instance) => EnsureRevengerButton(__instance);
        }

        private static void OnRevengerKill() {
            try {
                if (!LocalIsRevenger() || currentTarget == null) return;
                var result = Helpers.checkMuderAttempt(revenger, currentTarget);
                if (result != MurderAttemptResult.PerformKill) return;

                byte targetId = currentTarget.PlayerId;
                bool correct = targetId == killerId;

                if (correct) {
                    // Right target in either mode -> flag the win BEFORE the kill, then kill. The kill
                    // may remove the last evil player; flagging first guarantees the host sees
                    // triggerRevengerWin when that kill's CheckEndCriteria runs, so it ends with our
                    // reason (17) instead of racing a Crew "No Evil Killers Left" win.
                    SendWin(revenger.PlayerId);
                    RpcUncheckedMurder(revenger.PlayerId, targetId);
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
        // Killing-role Revenger win: they use their OWN normal kill button (no second button). When such
        // a Revenger kills the Lover's killer, that is the revenge -> Lovers win. Only the initiator's own
        // kill fires it, exactly once (non-killers go through the Revenger button + RpcUncheckedMurder).
        // ====================================================================
        [HarmonyPatch(typeof(PlayerControl), nameof(PlayerControl.MurderPlayer))]
        static class KillerRevengerWinPatch {
            public static void Postfix(PlayerControl __instance, [HarmonyArgument(0)] PlayerControl target) {
                try {
                    if (!active || revengerWon || revenger == null) return;
                    if (__instance != revenger || __instance != PlayerControl.LocalPlayer) return; // own kill only, once
                    if (!IsKiller(revenger)) return;               // non-killers use the Revenger button
                    if (target == null || target.PlayerId != killerId) return; // only the Lover's killer wins
                    SendWin(revenger.PlayerId);
                } catch (Exception e) {
                    UsefulTORStuffPlugin.Logger?.LogError($"[LoverRevenger] killer-revenger win failed: {e}");
                }
            }
        }

        // ====================================================================
        // End-of-game winner override: a Revenger win counts as a Lovers win for exactly the two Lovers
        // (the fallen one + the Revenger). Uses the snapshots because TOR's own postfix already ran
        // resetVariables, nulling both Lovers.* and our revenger field, before this last-priority postfix.
        // ====================================================================
        [HarmonyPatch(typeof(AmongUsClient), nameof(AmongUsClient.OnGameEnd))]
        [HarmonyPriority(Priority.Last)] // after TOR's OnGameEndPatch.Postfix
        static class OnGameEndOverridePatch {
            public static void Postfix() {
                try {
                    // Gate on the REAL end reason, not just revengerWon: a Revenger kill that ends the
                    // round another way (e.g. a raced Crew win) must not rebuild the winners.
                    if (!revengerWon || (int)OnGameEndPatch.gameOverReason != RevengerWinReason) return;
                    var winners = EndGameResult.CachedWinners;
                    if (winners == null) return;
                    winners.Clear();
                    if (loverData1 != null) winners.Add(new CachedPlayerData(loverData1));
                    if (loverData2 != null) winners.Add(new CachedPlayerData(loverData2));
                } catch (Exception e) {
                    UsefulTORStuffPlugin.Logger?.LogError($"[LoverRevenger] OnGameEnd override failed: {e}");
                }
            }
        }

        // ====================================================================
        // Role identity: show the awakened Revenger as its own neutral role (own name, Lovers color)
        // instead of the "Lover" modifier — in name tags (from awakening on), the role tab and the
        // end-game role summary. Replaces the whole info list so no stale base role/modifier leaks.
        // ====================================================================
        [HarmonyPatch(typeof(RoleInfo), nameof(RoleInfo.getRoleInfoForPlayer))]
        static class RoleInfoPatch {
            public static void Postfix(PlayerControl p, ref List<RoleInfo> __result) {
                try {
                    if (!active || revenger == null || p == null || p != revenger) return;
                    __result = new List<RoleInfo> { RevengerInfo() };
                } catch (Exception e) {
                    UsefulTORStuffPlugin.Logger?.LogError($"[LoverRevenger] RoleInfo postfix failed: {e}");
                }
            }
        }

        // ====================================================================
        // End screen: render the separate "Revenger Wins" subtitle (Lovers color) and tint the bar.
        // TOR's reason 17 isn't recognised by its own EndGameManagerSetUpPatch, so its bonus line stays
        // empty and we add our own — mirroring how TOR builds its special-win bonus text.
        // ====================================================================
        [HarmonyPatch(typeof(EndGameManager), nameof(EndGameManager.SetEverythingUp))]
        [HarmonyPriority(Priority.Last)] // after TOR's EndGameManagerSetUpPatch
        static class EndGameWinTextPatch {
            public static void Postfix(EndGameManager __instance) {
                try {
                    // Only when the game actually ended via our reason (17) — otherwise TOR's own win
                    // text (Crew/Impostor/...) already covers it and we must not overlay on top.
                    if ((int)OnGameEndPatch.gameOverReason != RevengerWinReason) return;
                    __instance.BackgroundBar.material.SetColor("_Color", Lovers.color);
                    GameObject bonusText = UnityEngine.Object.Instantiate(__instance.WinText.gameObject);
                    bonusText.transform.position = new Vector3(
                        __instance.WinText.transform.position.x,
                        __instance.WinText.transform.position.y - 0.5f,
                        __instance.WinText.transform.position.z);
                    bonusText.transform.localScale = new Vector3(0.7f, 0.7f, 1f);
                    TMPro.TMP_Text tr = bonusText.GetComponent<TMPro.TMP_Text>();
                    tr.text = "Lovers Win"; // the Revenger win counts as a Lovers win (just the two of them)
                    tr.color = Lovers.color;
                } catch (Exception e) {
                    UsefulTORStuffPlugin.Logger?.LogError($"[LoverRevenger] win text failed: {e}");
                }
            }
        }
    }
}
