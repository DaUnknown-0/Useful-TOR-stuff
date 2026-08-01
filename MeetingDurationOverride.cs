// Useful TOR Stuff - Copyright (C) 2026 DaUnknown-0
// Licensed under GPL-3.0-or-later. See LICENSE for details.
// Based on The Other Roles (https://github.com/TheOtherRolesAU/TheOtherRoles), GPL-3.0.

/*
 * MeetingDurationOverride - new "TOR Settings" option that overrides the meeting timer with a
 * value computed from the alive/dead player counts at meeting start.
 *
 * Two independent formulas (one for the discussion phase, one for the voting phase), each:
 *     seconds = Base + (aliveCount * PerAlivePlayer) - (deadCount * ReductionPerDeadPlayer)
 * clamped to a hard minimum of 0 (Among Us meeting times can't be negative).
 *
 * Host-authoritative (same philosophy as SheriffParityWin): only the host computes the durations,
 * writes them into the vanilla NormalGameOptions DiscussionTime/VotingTime and SyncOptions()s them
 * to everyone, so the override applies to all clients regardless of who has the mod. It is NOT
 * mod-gated.
 *
 * Because DiscussionTime/VotingTime live on the host's persistent lobby options (the same reason
 * TOR's ShipStatusPatch.resetVanillaSettings exists), the host's configured values are captured
 * once per game and restored on AmongUsClient.OnGameEnd so the lobby settings don't drift.
 */

using System;
using HarmonyLib;
using TheOtherRoles;
using TheOtherRoles.Utilities;
using UnityEngine;
using Types = TheOtherRoles.CustomOption.CustomOptionType;

namespace UsefulTORStuff {
    public static class MeetingDurationOverride {
        // Set in CreateOptions(); read by the MeetingHud.Start patch.
        public static CustomOption Enabled;            // master Off/On toggle (header)
        public static CustomOption DiscussionBase;     // seconds
        public static CustomOption DiscussionPerAlive; // seconds per alive player
        public static CustomOption DiscussionPerDead;  // seconds removed per dead player
        public static CustomOption VotingBase;         // seconds
        public static CustomOption VotingPerAlive;     // seconds per alive player
        public static CustomOption VotingPerDead;      // seconds removed per dead player

        // Host's configured discussion/voting time, snapshotted once per game so we can restore it
        // after we have overwritten the vanilla options (see RestorePatch / CaptureOriginalsOnce).
        private static int _originalDiscussionTime;
        private static int _originalVotingTime;
        private static bool _capturedThisGame;

        // Create the in-game options under the "TOR Settings" tab (CustomOptionType.General). Called
        // from UsefulTORStuffPlugin.Load() after TOR has already run CustomOptionHolder.Load()
        // (guaranteed by the hard dependency). IDs 1210-1216 (1200/1201 are SheriffParityWin's).
        public static void CreateOptions() {
            try {
                Enabled = CustomOption.Create(
                    1210, Types.General, "Override Meeting Duration", false, null, true);
                UTSLocalization.BindOptionTitle(Enabled, "uts.meetingdurationoverride.enabled");

                DiscussionBase     = CustomOption.Create(1211, Types.General, "Discussion Base Time", 15f, 0f, 120f, 2.5f, Enabled);
                UTSLocalization.BindOptionTitle(DiscussionBase, "uts.meetingdurationoverride.discussion_base");
                DiscussionPerAlive = CustomOption.Create(1212, Types.General, "Discussion Per Alive Player", 0f, 0f, 30f, 2.5f, Enabled);
                UTSLocalization.BindOptionTitle(DiscussionPerAlive, "uts.meetingdurationoverride.discussion_per_alive");
                DiscussionPerDead  = CustomOption.Create(1213, Types.General, "Discussion Reduction Per Dead Player", 0f, 0f, 30f, 2.5f, Enabled);
                UTSLocalization.BindOptionTitle(DiscussionPerDead, "uts.meetingdurationoverride.discussion_per_dead");
                VotingBase         = CustomOption.Create(1214, Types.General, "Voting Base Time", 30f, 0f, 120f, 2.5f, Enabled);
                UTSLocalization.BindOptionTitle(VotingBase, "uts.meetingdurationoverride.voting_base");
                VotingPerAlive     = CustomOption.Create(1215, Types.General, "Voting Per Alive Player", 0f, 0f, 30f, 2.5f, Enabled);
                UTSLocalization.BindOptionTitle(VotingPerAlive, "uts.meetingdurationoverride.voting_per_alive");
                VotingPerDead      = CustomOption.Create(1216, Types.General, "Voting Reduction Per Dead Player", 0f, 0f, 30f, 2.5f, Enabled);
                UTSLocalization.BindOptionTitle(VotingPerDead, "uts.meetingdurationoverride.voting_per_dead");

                UsefulTORStuffPlugin.Logger?.LogInfo("[MeetingDurationOverride] Options created under TOR Settings.");
            } catch (Exception e) {
                UsefulTORStuffPlugin.Logger?.LogError($"[MeetingDurationOverride] CreateOptions failed: {e}");
            }
        }

        // Count players that are alive vs dead at this moment (disconnected players count as neither),
        // mirroring SheriffParityWin.CountAlive / TOR's PlayerStatistics.
        private static void CountAliveDead(out int alive, out int dead) {
            alive = dead = 0;
            var gd = GameData.Instance;
            if (gd == null) return;
            foreach (var pi in gd.AllPlayers.GetFastEnumerator()) {
                if (pi == null || pi.Disconnected) continue;
                if (pi.IsDead) dead++;
                else alive++;
            }
        }

        private static int Compute(float baseValue, float perAlive, float perDead, int alive, int dead) {
            float seconds = baseValue + alive * perAlive - dead * perDead;
            return Mathf.Max(0, Mathf.RoundToInt(seconds)); // hard minimum 0
        }

        private static void CaptureOriginalsOnce() {
            if (_capturedThisGame) return;
            var opts = GameOptionsManager.Instance.currentNormalGameOptions;
            _originalDiscussionTime = opts.DiscussionTime;
            _originalVotingTime = opts.VotingTime;
            _capturedThisGame = true;
        }

        // Host-authoritative override: on the host, when a meeting opens, compute the durations from
        // the current alive/dead counts, write them into the vanilla options and sync to all clients.
        // MeetingHud.Update reads GetDiscussionTime()/GetVotingTime() live each frame, so the synced
        // values take effect immediately on every client (modded or not).
        [HarmonyPatch(typeof(MeetingHud), nameof(MeetingHud.Start))]
        public static class ApplyPatch {
            public static void Postfix() {
                try {
                    if (AmongUsClient.Instance == null || !AmongUsClient.Instance.AmHost) return;
                    if (Enabled == null || !UTSGate.Bool(Enabled)) return;

                    CaptureOriginalsOnce();
                    CountAliveDead(out int alive, out int dead);

                    int disc = Compute(UTSGate.Num(DiscussionBase), UTSGate.Num(DiscussionPerAlive), UTSGate.Num(DiscussionPerDead), alive, dead);
                    int vote = Compute(UTSGate.Num(VotingBase), UTSGate.Num(VotingPerAlive), UTSGate.Num(VotingPerDead), alive, dead);

                    var opts = GameOptionsManager.Instance.currentNormalGameOptions;
                    opts.DiscussionTime = disc;
                    opts.VotingTime = vote;
                    GameManager.Instance.LogicOptions.SyncOptions();

                    UsefulTORStuffPlugin.Logger?.LogInfo(
                        $"[MeetingDurationOverride] alive={alive} dead={dead} -> discussion={disc}s voting={vote}s (synced).");
                } catch (Exception e) {
                    UsefulTORStuffPlugin.Logger?.LogError($"[MeetingDurationOverride] Apply failed: {e}");
                }
            }
        }

        // Restore the host's configured discussion/voting time once the game ends so the lobby
        // settings don't keep the last computed values. Same hook TOR uses for resetVanillaSettings.
        [HarmonyPatch(typeof(AmongUsClient), nameof(AmongUsClient.OnGameEnd))]
        public static class RestorePatch {
            public static void Postfix() {
                try {
                    if (!_capturedThisGame) return;
                    var opts = GameOptionsManager.Instance.currentNormalGameOptions;
                    opts.DiscussionTime = _originalDiscussionTime;
                    opts.VotingTime = _originalVotingTime;
                    _capturedThisGame = false;
                    UsefulTORStuffPlugin.Logger?.LogInfo("[MeetingDurationOverride] Restored host's discussion/voting time.");
                } catch (Exception e) {
                    UsefulTORStuffPlugin.Logger?.LogError($"[MeetingDurationOverride] Restore failed: {e}");
                }
            }
        }
    }
}
