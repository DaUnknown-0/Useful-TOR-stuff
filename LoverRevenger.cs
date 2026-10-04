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
 *         "Blind Rage Kills" (1298, 1-3, default 1) sets how many wrong kills they get before the
 *         button locks; until then a later hit on the real killer still wins.
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
        public static CustomOption RageKills;        // Blind Rage: wrong kills allowed before the button locks

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

        public static bool rageKillDone;             // Blind-Rage Revenger killed a wrong target -> dies next meeting
        public static int rageKills;                 // wrong Blind-Rage kills so far (synced via SubRageArmed)

        // Blind Rage: the button locks once the allowed number of wrong kills is used up (option 1298,
        // default 1). Until then the Revenger may keep trying, and hitting the real killer still wins.
        private static int RageKillLimit() =>
            RageKills != null ? Math.Max(1, (int)UTSGate.Num(RageKills)) : 1;
        private static bool RageLocked() => rageKillDone && rageKills >= RageKillLimit();
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
            revengerInfo ??= new RoleInfo(UTSLocalization.Tr("uts.loverrevenger.role_name"), Lovers.color,
                UTSLocalization.Tr("uts.loverrevenger.role_desc"), UTSLocalization.Tr("uts.loverrevenger.role_desc"),
                RoleId.Lover, true);

        // The RoleInfo above is built lazily once and then cached, so a later language switch would
        // otherwise leave its name/description stale (same pitfall as DrunkRename) - re-push the
        // translated texts into the already-created instance on every language (re)apply.
        private static void ReapplyRevengerInfoLanguage() {
            if (revengerInfo == null) return;
            revengerInfo.name = UTSLocalization.Tr("uts.loverrevenger.role_name");
            revengerInfo.introDescription = UTSLocalization.Tr("uts.loverrevenger.role_desc");
            revengerInfo.shortDescription = UTSLocalization.Tr("uts.loverrevenger.role_desc");
        }

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
        private static bool deadChatHintShown;       // the dead Lover was told once that the partner cannot read him
        private static PlayerControl currentTarget;  // Revenger's nearest target (for the button)
        private static CustomButton revengerButton;

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
        private const byte SubActive = 5;      // active (host -> everyone at intro end; the host's gate wins)
        private const byte SubDesync = 6;      // loverId (any client -> host: "he is already dead here")

        // Host: when the last Revenger decision went out, so a desync report only counts right after it.
        private static float decisionAt = -100f;
        private const float DesyncWindow = 20f;

        // The host's own gate decision for this round, once it arrived. Every client used to latch
        // `active` from its OWN handshake table; one gap there left the Lover-death suppression off on
        // that client only, so the surviving Lover died there and lived everywhere else (Opus audit
        // 2026-10-02). Now the host's decision is broadcast and overrides the local one.
        private static bool? hostActive;

        // Resolved once from TOR's internal CustomRPC enum (fallback 108 = UncheckedMurderPlayer).
        private static byte uncheckedMurderRpc = 108;

        // GameHistory is internal in TOR, so its death-reason override is called via reflection.
        private static MethodInfo overrideDeathMethod;

        // ---- Flavor texts (localization keys; resolved via UTSLocalization.Tr at use time) ----
        private static readonly string[] GriefTexts = {
            "uts.loverrevenger.grief_1",
            "uts.loverrevenger.grief_2",
            "uts.loverrevenger.grief_3",
            "uts.loverrevenger.grief_4",
            "uts.loverrevenger.grief_5"
        };
        private static readonly string[] AwakenTargeted = {
            "uts.loverrevenger.awaken_targeted_1",
            "uts.loverrevenger.awaken_targeted_2"
        };
        private static readonly string[] AwakenRage = {
            "uts.loverrevenger.awaken_rage_1",
            "uts.loverrevenger.awaken_rage_2"
        };
        private static readonly string[] RageDeathTexts = {
            "uts.loverrevenger.rage_death_1",
            "uts.loverrevenger.rage_death_2",
            "uts.loverrevenger.rage_death_3",
            "uts.loverrevenger.rage_death_4",
            "uts.loverrevenger.rage_death_5"
        };
        private static readonly string[] RevengeDeniedTexts = {
            "uts.loverrevenger.denied_1",
            "uts.loverrevenger.denied_2",
            "uts.loverrevenger.denied_3",
            "uts.loverrevenger.denied_4",
            "uts.loverrevenger.denied_5"
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
                    1294, Types.Modifier, "Delay Lover Death (Revenger, Needs Both Lovers Die)",
                    false, CustomOptionHolder.modifierLover);
                UTSLocalization.BindOptionTitle(DelayOption, "uts.loverrevenger.option_delay");
                RevengerChance = CustomOption.Create(
                    1295, Types.Modifier, "Chance Surviving Lover Becomes Revenger",
                    CustomOptionHolder.rates, DelayOption);
                UTSLocalization.BindOptionTitle(RevengerChance, "uts.loverrevenger.option_chance");
                RevengerMode = CustomOption.Create(
                    1296, Types.Modifier, "Revenger Mode",
                    new string[] { "Targeted Justice", "Blind Rage" }, DelayOption);
                UTSLocalization.BindOptionTitle(RevengerMode, "uts.loverrevenger.option_mode");
                UTSLocalization.BindOptionSelections(RevengerMode,
                    "uts.loverrevenger.mode_targeted", "uts.loverrevenger.mode_blindrage");
                RevengerCooldown = CustomOption.Create(
                    1297, Types.Modifier, "Revenger Kill Cooldown",
                    30f, 10f, 60f, 2.5f, DelayOption);
                UTSLocalization.BindOptionTitle(RevengerCooldown, "uts.loverrevenger.option_cooldown");
                RageKills = CustomOption.Create(
                    1298, Types.Modifier, "Blind Rage Kills (Blind Rage Mode Only)",
                    1f, 1f, 3f, 1f, DelayOption);
                UTSLocalization.BindOptionTitle(RageKills, "uts.loverrevenger.option_ragekills");

                // Reapply the Revenger's cached RoleInfo texts on every language switch (see
                // ReapplyRevengerInfoLanguage above); subscribed once here alongside option creation.
                UTSLocalization.LanguageApplied += ReapplyRevengerInfoLanguage;

                // Place directly under the existing Lover modifier options (same approach as
                // LawyerLoverTracker). Insert after "Enable Lover Chat" (or the tracker options).
                var opts = CustomOption.options;
                foreach (var o in new[] { DelayOption, RevengerChance, RevengerMode, RevengerCooldown, RageKills })
                    opts.Remove(o);
                int idx = opts.IndexOf(CustomOptionHolder.modifierLoverEnableChat);
                if (idx < 0) idx = opts.Count - 1;
                opts.Insert(idx + 1, DelayOption);
                opts.Insert(idx + 2, RevengerChance);
                opts.Insert(idx + 3, RevengerMode);
                opts.Insert(idx + 4, RevengerCooldown);
                opts.Insert(idx + 5, RageKills);

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
                var impWin = type.GetMethod("CheckAndEndGameForImpostorWin", BindingFlags.NonPublic | BindingFlags.Static);
                var jackalWin = type.GetMethod("CheckAndEndGameForJackalWin", BindingFlags.NonPublic | BindingFlags.Static);
                if (impWin != null) { harmony.Patch(impWin, prefix: new HarmonyMethod(typeof(LoverRevenger), nameof(ImpostorWinBlockPrefix))); UsefulTORStuffPlugin.Logger?.LogInfo("[LoverRevenger] Patched CheckAndEndGameForImpostorWin (parity block)."); }
                else UsefulTORStuffPlugin.Logger?.LogWarning("[LoverRevenger] CheckAndEndGameForImpostorWin not found — parity block disabled for impostors.");
                if (jackalWin != null) { harmony.Patch(jackalWin, prefix: new HarmonyMethod(typeof(LoverRevenger), nameof(JackalWinBlockPrefix))); UsefulTORStuffPlugin.Logger?.LogInfo("[LoverRevenger] Patched CheckAndEndGameForJackalWin (parity block)."); }
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
        private static bool RevengerAlive() => active && !IsGone(revenger);

        // Dead OR gone: a player who left keeps IsDead == false (audit 04.10.: a Revenger who left the
        // lobby held the impostors' and the Jackal's parity win back for good).
        private static bool IsGone(PlayerControl p) =>
            p == null || p.Data == null || p.Data.IsDead || p.Data.Disconnected;

        // The block only makes sense against a RIVAL team. A Revenger who is himself an Impostor (or
        // in the Jackal team) would otherwise block his own team's kill win; and when a teammate killed
        // his Lover he cannot even reach that teammate (impostor targeting skips impostors), so his
        // team could not win by kills at all (Opus audit 2026-10-02).
        public static bool ImpostorWinBlockPrefix(ref bool __result) {
            try {
                if (RevengerAlive() && !(revenger.Data.Role != null && revenger.Data.Role.IsImpostor)) { LogBlock("Impostor"); __result = false; return false; }
            } catch (Exception e) {
                UsefulTORStuffPlugin.Logger?.LogError($"[LoverRevenger] ImpostorWinBlockPrefix failed: {e}");
            }
            return true;
        }

        // One log line per Revenger when his parity block first holds a win back: makes a stalled
        // game provable from the host's log (playtest 2026-10-02, Jackal + Sidekick held back).
        private static byte blockLoggedFor = byte.MaxValue;
        private static void LogBlock(string team) {
            if (revenger == null || blockLoggedFor == revenger.PlayerId) return;
            blockLoggedFor = revenger.PlayerId;
            UsefulTORStuffPlugin.Logger?.LogInfo($"[LoverRevenger] {team} parity win held back: Revenger {revenger.Data?.PlayerName} is alive.");
        }

        public static bool JackalWinBlockPrefix(ref bool __result) {
            try {
                bool jackalTeam = revenger == Jackal.jackal || revenger == Sidekick.sidekick;
                if (RevengerAlive() && !jackalTeam) { LogBlock("Jackal"); __result = false; return false; }
            } catch (Exception e) {
                UsefulTORStuffPlugin.Logger?.LogError($"[LoverRevenger] JackalWinBlockPrefix failed: {e}");
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
        // instead of getting a second (Revenger) button: their normal kill on the Lover's killer
        // triggers the win. Uses TOR's canonical Helpers.isKiller (Impostor + neutral killers like
        // Jackal/Sidekick/Thief). NOT the Sheriff any more (User 04.10.): his shot on a crew killer is
        // a misfire that kills him, so he could not avenge a crew killer at all; he gets the Revenger
        // button like everyone else and keeps his own shot beside it.
        private static bool IsKiller(PlayerControl p) =>
            p != null && p.Data != null && Helpers.isKiller(p);

        // IsKiller of the Revenger, taken ONCE before he awakens (Opus review 2026-10-02). Asked later it
        // is wrong: from the awakening on, RoleInfoPatch below hands out only the Revenger's own RoleInfo,
        // which is neutral, and TOR's Helpers.isKiller counts every neutral (but Jester, Arsonist,
        // Vulture, Lawyer, Pursuer) as a killer. So every crew Revenger looked like a killer with his own
        // button and never got the Revenger button.
        private static bool revengerOwnKill;

        private static PlayerControl PartnerOf(PlayerControl p) {
            if (p == Lovers.lover1) return Lovers.lover2;
            if (p == Lovers.lover2) return Lovers.lover1;
            return null;
        }

        // The dead Lover's messages do not reach the living partner while the revenge runs (User
        // 2026-10-04, Fable review of audit 10.3): with TOR's Lover chat the dead one could simply
        // name the killer, which the Revenger is never meant to learn. Only that one direction is
        // dark; the ghost chat of the dead Lover works as usual, and he is told once why his partner
        // does not answer. Display only (each client filters what it shows), nothing is sent.
        [HarmonyPatch(typeof(ChatController), nameof(ChatController.AddChat))]
        static class DeadLoverChatPatch {
            [HarmonyPriority(Priority.First)]
            public static bool Prefix([HarmonyArgument(0)] PlayerControl sourcePlayer) {
                try {
                    if (!pendingArmed && revenger == null) return true;
                    var lp = PlayerControl.LocalPlayer;
                    if (lp == null || lp.Data == null || sourcePlayer == null || sourcePlayer.Data == null) return true;
                    var partner = revenger ?? pendingLover;
                    // living partner's screen: drop the dead Lover's lines
                    if (lp == partner && !lp.Data.IsDead && sourcePlayer != lp
                        && sourcePlayer.Data.IsDead && PartnerOf(lp) == sourcePlayer) return false;
                    // the dead Lover's own line: tell him once
                    if (sourcePlayer == lp && lp.Data.IsDead && partner != null && PartnerOf(partner) == lp
                        && !deadChatHintShown) {
                        deadChatHintShown = true;
                        var hud = HudManager.Instance;
                        if (hud != null && hud.Notifier != null)
                            hud.Notifier.AddDisconnectMessage(UTSLocalization.Tr("uts.loverrevenger.dead_chat_hint"));
                    }
                } catch { }
                return true;
            }
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

        // A death at a meeting's end (failed roll, Blind-Rage follow-up, revenge denied): exiled, NOT
        // murdered. uncheckedMurderPlayer left a body on the map the moment the round resumed, and that
        // body could be reported (User 2026-10-02: Revenger died after the second meeting, Ric reported
        // him). TOR's own meeting-end deaths take the same road: the Witch's spell
        // (ExileControllerPatch: lawyerPromotesToPursuer first, then uncheckedExilePlayer) and the
        // Lover who follows an exiled partner (ExilePlayerPatch: otherLover.Exiled()).
        // Runs locally on every client, like every other Apply* here.
        private const string NonWitchExileKey = "TORMods.NonWitchExile";

        private static void MeetingEndDeath(PlayerControl p) {
            if (p == null || p.Data == null || p.Data.IsDead) return;
            try {
                // An exiled Lawyer client would take the Lawyer down with him (ExilePlayerPatch); a
                // suicide promotes him to Pursuer instead, exactly as the Witch's exile does.
                if (Lawyer.lawyer != null && p == Lawyer.target) RPCProcedure.lawyerPromotesToPursuer();
            } catch (Exception e) {
                UsefulTORStuffPlugin.Logger?.LogWarning($"[LoverRevenger] lawyer promotion failed: {e.Message}");
            }
            // AppDomain contract with Unknown's Collection: its Witch kill-cutscene hooks
            // uncheckedExilePlayer and must not mistake this exile for a Witch spell.
            AppDomain.CurrentDomain.SetData(NonWitchExileKey, true);
            try { RPCProcedure.uncheckedExilePlayer(p.PlayerId); }
            finally { AppDomain.CurrentDomain.SetData(NonWitchExileKey, null); }
            OverrideLoverSuicide(p);
            UsefulTORStuffPlugin.Logger?.LogInfo($"[LoverRevenger] {p.Data.PlayerName} dies at the meeting end (exiled, no body).");
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
        //
        // NOT migrated to the consolidated channel (UTSRpc.CallId = 240) - deliberately. Classified
        // NOT IDEMPOTENT: the appliers kill players (ApplyDecision's failed-roll suicide,
        // ApplyRageDeath, ApplyDeniedDeath all call RPCProcedure.uncheckedMurderPlayer), post chat
        // lines, play the Revenger sting and latch the win flags. A dual-send would run all of that
        // twice on every new-build client - duplicated chat and a second murder call on an already
        // dead player. De-duplicating is impossible: a receiver cannot tell a dual-sending new build
        // from an old build that will never follow up on channel 240. Stays on the standalone callId
        // 247 until the legacy paths are dropped wholesale in a breaking release.
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
            // AUDIT-2026-09-11: the success paths below used to log nothing at all, so a report like
            // "I was Revenger and had no kill button" could not be diagnosed after the fact - the log
            // could not even show whether the roll succeeded, whether IsKiller() misclassified the
            // player, or whether EnsureRevengerButton ran. This one line makes every future occurrence
            // provable from the log instead of guessed at.
            UsefulTORStuffPlugin.Logger?.LogInfo(
                $"[LoverRevenger] ApplyDecision lover={lover?.Data?.PlayerName ?? loverId.ToString()} "
                + $"becomeRevenger={becomeRevenger} killerId={revKillerId} mode={mode} "
                + $"isKiller={(lover != null && IsKiller(lover))} isLocal={lover == PlayerControl.LocalPlayer}");
            if (lover == null) return;

            if (becomeRevenger && lover.Data != null && lover.Data.IsDead && !AmongUsClient.Instance.AmHost) {
                // The host still has him alive, this client does not: the partner suicide was NOT
                // suppressed here (a patch that silently stopped running on this client). Left alone
                // he stays a ghost here and an alive Revenger on the host, whose parity block then
                // holds back the Jackal/Impostor win (playtest 2026-10-02). Tell the host.
                UsefulTORStuffPlugin.Logger?.LogWarning(
                    $"[LoverRevenger] desync: {lover.Data.PlayerName} is already dead on this client - reporting to the host.");
                try {
                    var w = BeginRpc(SubDesync);
                    w.Write(loverId);
                    AmongUsClient.Instance.FinishRpcImmediately(w);
                } catch (Exception e) { UsefulTORStuffPlugin.Logger?.LogError($"[LoverRevenger] desync report failed: {e}"); }
            }

            if (becomeRevenger) {
                // The host only sends becomeRevenger=true when ITS gate was open, so the feature is
                // usable - full stop. Each client latches `active` on its own at intro end, and its
                // EveryoneHasMod() reads its OWN handshake table (exact version + build GUID for every
                // client). A single missing or late handshake on the Revenger's machine left `active`
                // false there, and every button check (LocalIsRevenger) starts with `active`: the
                // Revenger existed for everyone else but had no button on his own screen.
                active = true;
                revengerOwnKill = IsKiller(lover);   // BEFORE revenger is set, see the field
                revenger = lover;
                revengerMode = mode;
                killerId = revKillerId;
                if (lover == PlayerControl.LocalPlayer) {
                    string awaken = UTSLocalization.Tr(Pick(mode == ModeBlindRage ? AwakenRage : AwakenTargeted));
                    PostChat(lover, awaken);
                    // Detective-style colour hint (dark/light only), shown once at the awakening: the
                    // Revenger never learns WHO the killer is, this narrows it to a colour half. Local
                    // chat only - ApplyDecision runs on every client but PostChat is gated to the
                    // Revenger themselves above.
                    // The colour word comes from TOR's translated keys (audit 04.10.: the raw English
                    // "lighter"/"darker" sat inside every translated sentence).
                    var revKiller = Helpers.playerById(revKillerId);
                    string hint = null;
                    if (revKiller != null && revKiller.Data != null) {
                        hint = UTSLocalization.Tr("uts.loverrevenger.color_hint",
                            UTSLocalization.Tr(Helpers.isLighterColor(revKiller) ? "tor.ui.color.lighter" : "tor.ui.color.darker"));
                        PostChat(lover, hint);
                    }
                    // The round chat can be closed for the living right now (no Lover chat): the
                    // awakening and the hint also show on screen (audit 04.10.).
                    try { Helpers.showFlash(Lovers.color, 2.5f, hint != null ? awaken + "\n" + hint : awaken); } catch { }
                    UTSAssets.PlayRevenger(); // dark awakening sting, Revenger-only
                    // A non-killer Revenger awakens NOW (mid-game). Guarantee the kill button exists at
                    // this exact moment - the HudManager.Start creation can be long gone by here, which is
                    // what left non-killers with no button. Killers use their own kill button (no second).
                    if (!revengerOwnKill) {
                        EnsureRevengerButton(HudManager.Instance);
                        UsefulTORStuffPlugin.Logger?.LogInfo(
                            $"[LoverRevenger] EnsureRevengerButton done, button={(revengerButton != null)} "
                            + $"actionButton={(revengerButton?.actionButton != null)}");
                    } else {
                        UsefulTORStuffPlugin.Logger?.LogInfo(
                            "[LoverRevenger] Local player classified as IsKiller() - no dedicated button granted, expecting their own kill button to trigger the win.");
                    }
                }
            } else {
                // Roll failed (or the killer was already gone): the delayed Lover suicide happens now.
                MeetingEndDeath(lover);
            }
        }

        // Sent once per wrong Blind-Rage kill, so every client counts the same number.
        private static void ApplyRageArmed(byte revengerId) {
            rageKillDone = true;
            rageKills++;
        }

        private static void ApplyRageDeath(byte revengerId, byte msgIndex) {
            rageKillDone = false;
            rageKills = 0;
            var rev = Helpers.playerById(revengerId);
            if (rev == null) return;
            MeetingEndDeath(rev);
            if (msgIndex < RageDeathTexts.Length)
                PostChat(rev, UTSLocalization.Tr(RageDeathTexts[msgIndex]));
            revenger = null;
        }

        private static void ApplyDeniedDeath(byte revengerId, byte msgIndex) {
            var rev = Helpers.playerById(revengerId);
            if (rev == null) return;
            MeetingEndDeath(rev);
            if (msgIndex < RevengeDeniedTexts.Length)
                PostChat(rev, UTSLocalization.Tr(RevengeDeniedTexts[msgIndex]));
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
        // Own, standalone callId (247, not migrated to UTSRpc - see the header). Each subtype is
        // authored by a different party, so each gets its own guard (AUDIT-2026-08-15): the payload
        // is always read in full first (reader-cursor rule), the guard only gates applying it.
        [HarmonyPatch(typeof(PlayerControl), nameof(PlayerControl.HandleRpc))]
        static class HandleRpcPatch {
            [HarmonyPriority(Priority.High)]
            public static bool Prefix(byte callId, MessageReader reader, PlayerControl __instance) {
                if (callId != RpcId) return true;
                try {
                    byte subtype = reader.ReadByte();
                    switch (subtype) {
                        case SubDecision: {
                            byte loverId = reader.ReadByte();
                            bool become = reader.ReadByte() != 0;
                            byte k = reader.ReadByte();
                            byte mode = reader.ReadByte();
                            // Host-authoritative: only OnMeetingEnd (host-only) ever sends this.
                            if (UTSRpc.RequireHost(__instance, "LoverRevenger.Decision"))
                                ApplyDecision(loverId, become, k, mode);
                            break;
                        }
                        case SubRageArmed: {
                            byte revId = reader.ReadByte();
                            // Owner-authored: only the Revenger themselves (or the host) may arm the
                            // Blind-Rage follow-up death.
                            if (UTSRpc.RequireOwnerOrHost(__instance, revenger, "LoverRevenger.RageArmed"))
                                ApplyRageArmed(revId);
                            break;
                        }
                        case SubRageDeath: {
                            byte revId = reader.ReadByte();
                            byte idx = reader.ReadByte();
                            // Host-authoritative: only OnMeetingEnd (host-only) ever sends this.
                            if (UTSRpc.RequireHost(__instance, "LoverRevenger.RageDeath"))
                                ApplyRageDeath(revId, idx);
                            break;
                        }
                        case SubDeniedDeath: {
                            byte revId = reader.ReadByte();
                            byte idx = reader.ReadByte();
                            // Host-authoritative: only OnMeetingEnd (host-only) ever sends this.
                            if (UTSRpc.RequireHost(__instance, "LoverRevenger.DeniedDeath"))
                                ApplyDeniedDeath(revId, idx);
                            break;
                        }
                        case SubActive: {
                            bool on = reader.ReadByte() != 0;
                            if (UTSRpc.RequireHost(__instance, "LoverRevenger.Active")) {
                                hostActive = on;
                                active = on;
                                // The host sends this at ITS intro end. A client still in its intro
                                // may be in the middle of the Role Draft, which offers everything in
                                // allRoleInfos: the Revenger showed up there as a pick (User
                                // 2026-10-02). Our own IntroEndPatch lists him once the intro is over.
                                if (IntroCutscene.Instance == null) SetGuessable(on);
                            }
                            break;
                        }
                        case SubDesync: {
                            byte loverId = reader.ReadByte();
                            // Any client may report (the one where he is dead is arbitrary), but only
                            // the host acts, only right after a decision, and only for the Revenger
                            // it just made: he then dies everywhere (exile, no body) and the half-dead
                            // state is gone. Clients where he is already dead skip the kill.
                            if (AmongUsClient.Instance != null && AmongUsClient.Instance.AmHost
                                && revenger != null && revenger.PlayerId == loverId
                                && revenger.Data != null && !revenger.Data.IsDead
                                && Time.time - decisionAt <= DesyncWindow) {
                                UsefulTORStuffPlugin.Logger?.LogWarning(
                                    $"[LoverRevenger] desync reported by {__instance?.Data?.PlayerName}: {revenger.Data.PlayerName} dies everywhere.");
                                SendDeniedDeath(revenger.PlayerId, (byte)rnd.Next(RevengeDeniedTexts.Length));
                            }
                            break;
                        }
                        case SubWin: {
                            byte revId = reader.ReadByte();
                            // Owner-authored: only the Revenger themselves (or the host) may claim
                            // the win - without this any client could end the game as a Lovers win.
                            if (UTSRpc.RequireOwnerOrHost(__instance, revenger, "LoverRevenger.Win"))
                                ApplyWin(revId);
                            break;
                        }
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
                hostActive = null;
                revengerOwnKill = false;
                blockLoggedFor = byte.MaxValue;
                decisionAt = -100f;
                revenger = null;
                revengerMode = 0;
                killerId = byte.MaxValue;
                pendingArmed = false;
                pendingLover = null;
                pendingKillerId = byte.MaxValue;
                rageKillDone = false;
                rageKills = 0;
                triggerRevengerWin = false;
                griefChatShown = false;
                deadChatHintShown = false;
                currentTarget = null;
                // No gFlipArmed/gVictim/gPartner/gKiller reset here anymore: GuesserShootSuppressPatch's
                // flip bookkeeping now travels through Harmony's __state (see the patch below), so it
                // never outlives a single guesserShoot call and there is nothing left to clear here.
                SetGuessable(false); // keep the guess list clean between rounds; re-added on intro end
            }
        }

        // Latch whether the feature is usable for the whole game once the intro ends.
        [HarmonyPatch(typeof(IntroCutscene), nameof(IntroCutscene.OnDestroy))]
        static class IntroEndPatch {
            public static void Postfix() {
                active = DelayOption != null && UTSGate.Bool(DelayOption)
                         && Lovers.bothDie && EveryoneHasMod();
                if (AmongUsClient.Instance != null && AmongUsClient.Instance.AmHost) {
                    try {
                        var w = BeginRpc(SubActive);
                        w.Write((byte)(active ? 1 : 0));
                        AmongUsClient.Instance.FinishRpcImmediately(w);
                    } catch (Exception e) { UsefulTORStuffPlugin.Logger?.LogError($"[LoverRevenger] SendActive failed: {e}"); }
                } else if (hostActive.HasValue) {
                    active = hostActive.Value;   // the host's word arrived before our own intro ended
                }
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
        // Flip bookkeeping travels through Harmony's __state instead of a static field: MurderPlayer
        // can run reentrantly (e.g. a kill effect chain triggering another MurderPlayer call before
        // the outer one's Finalizer runs), and a static field would let the inner call's Prefix
        // overwrite the outer call's still-pending flip state.
        private struct FlipState {
            public bool armed;
            public PlayerControl victim;
            public PlayerControl partner;
            public byte killerId;
        }

        [HarmonyPatch(typeof(PlayerControl), nameof(PlayerControl.MurderPlayer))]
        static class MurderPlayerSuppressPatch {
            public static void Prefix(PlayerControl __instance, [HarmonyArgument(0)] PlayerControl target, out FlipState __state) {
                __state = default;
                try {
                    if (!active) return;
                    PlayerControl killer = __instance, victim = target;
                    if (killer == null || victim == null || killer == victim) return; // real kill only
                    if (!IsLover(victim) || !Lovers.bothDie) return;
                    PlayerControl partner = PartnerOf(victim);
                    if (IsGone(partner)) return; // partner must survive (and still be here)
                    if (pendingArmed || revenger != null) return; // already in a delay/revenger flow

                    // Skip TOR's suicide+override block (both guarded by Lovers.bothDie).
                    Lovers.bothDie = false;
                    __state.armed = true;
                    __state.victim = victim;
                    __state.partner = partner;
                    __state.killerId = killer.PlayerId;
                } catch (Exception e) {
                    __state = default;
                    UsefulTORStuffPlugin.Logger?.LogError($"[LoverRevenger] suppress prefix failed: {e}");
                }
            }

            // FINALIZER, not a postfix (AUDIT M-9). The restore has to happen no matter what, and a
            // postfix does not guarantee that: HarmonyX aborts the remaining postfixes as soon as an
            // earlier one throws, and MurderPlayer carries roughly fifteen Normal-priority postfixes
            // ahead of this one - TOR's own large, unguarded MurderPlayerPatch.Postfix included
            // (PlayerControlPatch.cs:1205-1299). One throw up there and Lovers.bothDie would have
            // stayed false for the rest of the round: Lovers would never again die together, and the
            // prefix above cannot re-arm because it only fires while bothDie is still true.
            // A finalizer runs after every postfix AND after a throw, which keeps the original
            // "restore last, so the flip stays effective" ordering intact. Returning void leaves any
            // exception to propagate exactly as before.
            public static void Finalizer(FlipState __state) {
                try {
                    if (!__state.armed) return;
                    Lovers.bothDie = true; // restore
                    bool victimDied = __state.victim != null && __state.victim.Data != null && __state.victim.Data.IsDead;
                    if (victimDied && __state.partner != null && !__state.partner.Data.IsDead) {
                        // Arm the delayed decision for the next meeting end.
                        pendingArmed = true;
                        pendingLover = __state.partner;
                        pendingKillerId = __state.killerId;
                        UsefulTORStuffPlugin.Logger?.LogInfo($"[LoverRevenger] Delayed Lover death armed (partner {__state.partner.Data?.PlayerName}, killer id {__state.killerId}).");
                    }
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
            // AUDIT: same reasoning as MurderPlayerSuppressPatch's FlipState above - travels through
            // Harmony's __state instead of a static field so a reentrant guesserShoot call cannot have
            // its Prefix overwrite an outer, still-pending call's flip state, and reuses the very same
            // struct shape (armed/victim/partner/killerId already match 1:1).
            public static void Prefix([HarmonyArgument(0)] byte killerId, [HarmonyArgument(1)] byte dyingTargetId, out FlipState __state) {
                __state = default;
                try {
                    if (!active || !Lovers.bothDie) return;
                    var victim = Helpers.playerById(dyingTargetId);
                    if (victim == null || !IsLover(victim)) return;
                    var partner = PartnerOf(victim);
                    if (IsGone(partner)) return; // partner must survive (and still be here)
                    if (partner.PlayerId == dyingTargetId) return;
                    if (pendingArmed || revenger != null) return; // already in a delay/revenger flow

                    Lovers.bothDie = false; // skip TOR's bothDie-gated partner suicide during Exiled()
                    __state.armed = true;
                    __state.victim = victim;
                    __state.partner = partner;
                    __state.killerId = killerId;
                } catch (Exception e) {
                    __state = default;
                    UsefulTORStuffPlugin.Logger?.LogError($"[LoverRevenger] guesserShoot prefix failed: {e}");
                }
            }

            // Finalizer for the same reason as MurderPlayerSuppressPatch above (AUDIT M-9): the
            // restore of Lovers.bothDie must survive a throw from any other patch on guesserShoot.
            public static void Finalizer(FlipState __state) {
                try {
                    if (!__state.armed) return;
                    Lovers.bothDie = true; // restore
                    bool victimDied = __state.victim != null && __state.victim.Data != null && __state.victim.Data.IsDead;
                    if (victimDied && __state.partner != null && !__state.partner.Data.IsDead) {
                        pendingArmed = true;
                        pendingLover = __state.partner;
                        pendingKillerId = __state.killerId;
                        UsefulTORStuffPlugin.Logger?.LogInfo($"[LoverRevenger] Delayed Lover death armed via guess (partner {__state.partner.Data?.PlayerName}, guesser id {__state.killerId}).");
                    }
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
                        PostChat(pendingLover, UTSLocalization.Tr(Pick(GriefTexts)));
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
                // AUDIT-2026-09-11: WrapUp/WrapUpAndSpawn (the only two callers of OnMeetingEnd) run on
                // EVERY meeting end including a skip/tie, but this whole method used to log nothing on
                // its success paths - a delayed decision that silently expired (pendingLover already
                // gone) looked identical in the log to one that never got a chance to run at all. Logged
                // here so "I was Revenger and had no kill button" can be traced to a concrete branch.
                bool justAwakened = false;
                if (pendingArmed) {
                    if (pendingLover == null || pendingLover.Data == null
                        || pendingLover.Data.IsDead || pendingLover.Data.Disconnected) {
                        // Surviving Lover already gone -> nothing to decide.
                        UsefulTORStuffPlugin.Logger?.LogInfo(
                            $"[LoverRevenger] OnMeetingEnd: pending decision for {pendingLover?.Data?.PlayerName} dropped (lover already dead/disconnected/gone).");
                        pendingArmed = false; pendingLover = null;
                    } else {
                        int sel = RevengerChance != null ? UTSGate.Sel(RevengerChance) : 0;
                        // Nobody left to avenge: the killer died before this meeting ended (killed in
                        // the round, or exiled right now). Awakening anyway made a Revenger with no
                        // possible target who only died a meeting later (User 2026-10-02: the Maniac
                        // bombed a Lover and died himself before the first meeting). The Lover follows
                        // his partner now instead, as without the delay.
                        // The surviving Lover killed the partner himself (mixed Impostor+Crew pair,
                        // Sheriff/Guesser Lover, Maniac bomb): there is nobody to avenge either, a
                        // Revenger with himself as the target could never win or be denied.
                        var pk = Helpers.playerById(pendingKillerId);
                        bool killerGone = pk == null || pk.Data == null || pk.Data.IsDead || pk.Data.Disconnected
                            || pendingKillerId == pendingLover.PlayerId;
                        bool become = active && !killerGone && rnd.Next(1, 101) <= sel * 10;
                        byte mode = (byte)(RevengerMode != null ? UTSGate.Sel(RevengerMode) : 0);
                        UsefulTORStuffPlugin.Logger?.LogInfo(
                            $"[LoverRevenger] OnMeetingEnd: resolving pending decision for {pendingLover.Data?.PlayerName} "
                            + $"(active={active}, chance selection={sel}, killerId={pendingKillerId}, killerGone={killerGone}) -> becomeRevenger={become}");
                        decisionAt = Time.time;
                        SendDecision(pendingLover.PlayerId, become, pendingKillerId, mode);
                        justAwakened = become;
                    }
                }

                // 2) Blind-Rage Revenger who killed a wrong target dies now (with a rage message).
                if (rageKillDone) {
                    if (revenger == null || revenger.Data == null || revenger.Data.IsDead) {
                        rageKillDone = false;
                        rageKills = 0;
                    } else {
                        SendRageDeath(revenger.PlayerId, (byte)rnd.Next(RageDeathTexts.Length));
                    }
                }

                // 3) Revenge denied: the Lover's killer died before the Revenger could strike (voted out
                //    or killed by someone else). With nothing left to avenge, the Revenger dies at this
                //    meeting's end. Skipped in the very meeting they awaken, and only once they exist.
                if (!justAwakened && !rageKillDone && killerId != byte.MaxValue && !IsGone(revenger)) {
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

        // Airship: the postfix runs before the exile (coroutine), so the killer would still count as
        // alive. Deferred until the cutscene is done, see AirshipWrapUpDefer.
        [HarmonyPatch(typeof(AirshipExileController), nameof(AirshipExileController.WrapUpAndSpawn))]
        static class AirshipExileWrapUpPatch {
            public static void Postfix(AirshipExileController __instance) =>
                AirshipWrapUpDefer.Arm(__instance, "LoverRevenger", OnMeetingEnd);
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
            LocalIsRevenger() && !revengerOwnKill;

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
                    // Blind Rage: once the allowed wrong kills (option 1298) are used up, the Revenger
                    // only waits for the meeting that ends him.
                    () => !RageLocked() && currentTarget != null && PlayerControl.LocalPlayer != null && PlayerControl.LocalPlayer.CanMove,
                    () => { if (revengerButton != null) revengerButton.Timer = revengerButton.MaxTimer; },
                    UTSAssets.RevengerIcon ?? hud.KillButton.graphic.sprite,
                    CustomButton.ButtonPositions.upperRowRight,
                    hud,
                    KeyCode.Q
                );
                revengerButton.MaxTimer = RevengerCooldown != null ? UTSGate.Num(RevengerCooldown) : 30f;
                revengerButton.Timer = revengerButton.MaxTimer;
            } catch (Exception e) {
                UsefulTORStuffPlugin.Logger?.LogError($"[LoverRevenger] button creation failed: {e}");
            }
        }

        // ====================================================================
        // Slot: never underneath another ability button.
        //
        // The button used to sit at upperRowRight unconditionally. A Revenger keeps his base role's
        // buttons (only the role INFO is replaced), and TOR puts the Engineer's repair and the
        // Hacker's button in that very slot (Buttons.cs), as do the UC Poisoner and Pelican. Two
        // clones of the kill button on the same spot: the Revenger button was drawn underneath and
        // could not be seen or clicked. Now it takes the first slot no other VISIBLE custom button
        // occupies, the same slot grid PlayerTuning/ChanceMod use for the granted vent button.
        // PositionOffset is read by CustomButton.Update every frame, so setting it is enough.
        // Priority.Low: after TOR's own HudManager.Update postfix has updated (and shown) the rest.
        // ====================================================================
        private static readonly Vector3[] CandidateSlots = {
            CustomButton.ButtonPositions.upperRowRight,
            CustomButton.ButtonPositions.upperRowCenter,
            CustomButton.ButtonPositions.upperRowLeft,
            CustomButton.ButtonPositions.upperRowFarLeft,
            CustomButton.ButtonPositions.lowerRowRight,
            CustomButton.ButtonPositions.lowerRowCenter,
            CustomButton.ButtonPositions.lowerRowLeft,
            CustomButton.ButtonPositions.highRowRight,
        };
        private const float SlotEps = 0.25f;
        private static readonly List<Vector3> occupiedSlots = new List<Vector3>();
        private static bool slotLogged;

        private static bool SlotTaken(Vector3 slot) {
            foreach (var o in occupiedSlots)
                if (Mathf.Abs(o.x - slot.x) < SlotEps && Mathf.Abs(o.y - slot.y) < SlotEps) return true;
            return false;
        }

        [HarmonyPatch(typeof(HudManager), nameof(HudManager.Update))]
        static class RevengerButtonSlotPatch {
            [HarmonyPriority(Priority.Low)]
            public static void Postfix() {
                try {
                    if (revengerButton == null || revengerButton.actionButton == null) return;
                    if (!LocalUsesRevengerButton()) { slotLogged = false; return; }
                    if (MeetingHud.Instance != null || ExileController.Instance != null) return;

                    occupiedSlots.Clear();
                    foreach (var b in CustomButton.buttons) {
                        if (b == null || b == revengerButton || b.mirror) continue;
                        if (b.actionButtonGameObject == null || !b.actionButtonGameObject.activeSelf) continue;
                        occupiedSlots.Add(b.PositionOffset);
                    }
                    if (!SlotTaken(revengerButton.PositionOffset) && slotLogged) return;

                    Vector3 before = revengerButton.PositionOffset;
                    foreach (var slot in CandidateSlots) {
                        if (SlotTaken(slot)) continue;
                        revengerButton.PositionOffset = slot;
                        break;
                    }
                    if (!slotLogged) {
                        slotLogged = true;
                        UsefulTORStuffPlugin.Logger?.LogInfo(
                            $"[LoverRevenger] Revenger button active at slot {revengerButton.PositionOffset} "
                            + $"(was {before}, {occupiedSlots.Count} other visible button(s)).");
                    }
                } catch { }
            }
        }

        [HarmonyPatch(typeof(HudManager), nameof(HudManager.Start))]
        static class HudStartButtonPatch {
            public static void Postfix(HudManager __instance) => EnsureRevengerButton(__instance);
        }

        private static void OnRevengerKill() {
            try {
                if (!LocalIsRevenger() || currentTarget == null || RageLocked()) return;
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
            // The HOST flags the win before the kill runs (audit 04.10.): the Revenger's own SendWin
            // below only arrives after the kill, and a kill that removes the last evil player could
            // end the round as a normal win first. Every client knows revenger/killerId/revengerOwnKill.
            [HarmonyPriority(Priority.First)]
            public static void Prefix(PlayerControl __instance, [HarmonyArgument(0)] PlayerControl target) {
                try {
                    if (!active || revengerWon || revenger == null || !revengerOwnKill) return;
                    if (AmongUsClient.Instance == null || !AmongUsClient.Instance.AmHost) return;
                    if (__instance != revenger || target == null || target.PlayerId != killerId) return;
                    SendWin(revenger.PlayerId);
                } catch (Exception e) {
                    UsefulTORStuffPlugin.Logger?.LogError($"[LoverRevenger] host revenger win failed: {e}");
                }
            }

            public static void Postfix(PlayerControl __instance, [HarmonyArgument(0)] PlayerControl target) {
                try {
                    if (!active || revengerWon || revenger == null) return;
                    if (__instance != revenger || __instance != PlayerControl.LocalPlayer) return; // own kill only, once
                    if (!revengerOwnKill) return;                  // non-killers use the Revenger button
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
        static class OnGameEndOverridePatch {
            [HarmonyPriority(Priority.Last)]  // after TOR's OnGameEndPatch.Postfix
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
                    // At the game's end the summary keeps what he was underneath (audit 04.10.: only
                    // "Revenger" was listed); the Lover entry itself is what Revenger replaces.
                    bool ended = AmongUsClient.Instance != null
                                 && AmongUsClient.Instance.GameState == InnerNet.InnerNetClient.GameStates.Ended;
                    var list = new List<RoleInfo> { RevengerInfo() };
                    if (ended && __result != null)
                        foreach (var r in __result)
                            if (r != null && r != RoleInfo.lover && !list.Contains(r)) list.Add(r);
                    __result = list;
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
        static class EndGameWinTextPatch {
            [HarmonyPriority(Priority.Last)]  // after TOR's EndGameManagerSetUpPatch
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
                    // the Revenger win counts as a Lovers win (just the two of them)
                    tr.text = UTSLocalization.Tr("uts.loverrevenger.endscreen_lovers_win");
                    tr.color = Lovers.color;
                } catch (Exception e) {
                    UsefulTORStuffPlugin.Logger?.LogError($"[LoverRevenger] win text failed: {e}");
                }
            }
        }
    }
}
