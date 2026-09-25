// Useful TOR Stuff - Copyright (C) 2026 DaUnknown-0
// Licensed under GPL-3.0-or-later. See LICENSE for details.
// Based on The Other Roles (https://github.com/TheOtherRolesAU/TheOtherRoles), GPL-3.0.

/*
 * DeathTimeHistory - how long each player survives a round, remembered across sessions.
 *
 * The Tracker export plugin already stamps every death with a wall-clock time, but only for the
 * external Python tracker, and only on the machine that runs it. This is the in-game half: a small
 * rolling history per player that the EarlyDeathShield reads to decide who keeps dying early.
 *
 * WHAT ONE ROUND CONTRIBUTES
 * One number per player: the SHARE of the round's gameplay time they survived before somebody
 * else killed them (0.0 = killed right after the intro, 1.0 = alive at the end). A share and not
 * seconds, because rounds differ wildly in length: dying after two minutes of a three minute round
 * is a normal round, after two minutes of a fifteen minute round it is an early death.
 *
 * Gameplay time excludes meetings. The clock starts when the intro ends, pauses while a MeetingHud
 * or an ExileController exists and stops at game end - the same split the tracker export draws
 * between "gameplaySeconds" and "meetingSeconds".
 *
 * WHICH DEATHS COUNT
 * Only kills by somebody else (the user's choice): TOR's GameHistory entry must name a killer who
 * is not the victim, with a reason of Kill, Guess, Bomb, Arson or WitchExile. A round that ended in
 * a way that says nothing about being hunted (voted out, a misfire, a failed guess, a lover or
 * lawyer suicide, a shift gone wrong, a disconnect, an unexplained death) is left out for that
 * player entirely - counting it as "survived" would be as wrong as counting it as "killed".
 *
 * GameHistory is read in an OnGameEnd PREFIX: TOR's own OnGameEnd postfix ends with
 * resetVariables(), which replaces the list (see project memory "TrackerExport-Schnappschuss").
 * Its timestamps are DateTime.UtcNow, so this file keeps its own clock in UtcNow as well.
 *
 * WHO RECORDS
 * Every client records, not only the host: every client applies every murder and sees every
 * meeting, so the numbers agree, and whoever hosts next already has a history. Nothing is sent
 * over the network; the file stays on the machine that wrote it.
 *
 * Identity is NewcomerShield.CodeOf: friend code, or "name:<PlayerName>" on auth-less servers.
 *
 * The file also carries the host's per-player override for the shield, so a "never shield this
 * one" survives a restart like the numbers it overrides.
 */

using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using HarmonyLib;
using TheOtherRoles;
using TheOtherRoles.CustomGameModes;
using UnityEngine;

namespace UsefulTORStuff {

    public static class DeathTimeHistory {

        // Rounds remembered per player. Twenty is long enough to average out a bad evening and
        // short enough that somebody who got better stops being "the one who dies early".
        public const int Window = 20;

        // Rounds shorter than this say nothing about anybody (an instant sabotage win, a host who
        // ended the game from the lobby menu).
        private const double MinRoundSeconds = 30;

        public const int OverrideAuto = 0;
        public const int OverrideOn = 1;
        public const int OverrideOff = -1;

        private sealed class Entry {
            public string Name = "";
            public int Override;
            public readonly List<float> Shares = new List<float>();
        }

        public struct Stat {
            public int Rounds;
            public float Mean;   // average survived share, 0..1; 0 when Rounds == 0
        }

        private static readonly Dictionary<string, Entry> entries = new Dictionary<string, Entry>();
        private static bool loaded;

        private static string FilePath =>
            Path.Combine(BepInEx.Paths.ConfigPath, "UTSDeathTimes.txt");

        // ---- queries (host side, for the shield and its lobby panel) ----

        public static Stat StatOf(string code) {
            EnsureLoaded();
            if (string.IsNullOrEmpty(code) || !entries.TryGetValue(code, out var e) || e.Shares.Count == 0)
                return new Stat();
            return new Stat { Rounds = e.Shares.Count, Mean = e.Shares.Average() };
        }

        public static int OverrideOf(string code) {
            EnsureLoaded();
            return !string.IsNullOrEmpty(code) && entries.TryGetValue(code, out var e) ? e.Override : OverrideAuto;
        }

        public static void SetOverride(string code, string name, int value) {
            if (string.IsNullOrEmpty(code)) return;
            EnsureLoaded();
            var e = GetOrAdd(code, name);
            if (e.Override == value) return;
            e.Override = value;
            Save();
        }

        private static Entry GetOrAdd(string code, string name) {
            if (!entries.TryGetValue(code, out var e)) {
                e = new Entry();
                entries[code] = e;
            }
            // Keep the last seen name: on friend-code servers it is the only human-readable part.
            if (!string.IsNullOrEmpty(name)) e.Name = name.Replace('\t', ' ');
            return e;
        }

        // ---- persistence ----
        // One player per line: code, last name, override, then the shares oldest first.
        // Tab separated (names cannot contain tabs after the Replace above, codes never do).

        private static void EnsureLoaded() {
            if (loaded) return;
            loaded = true;
            try {
                if (!File.Exists(FilePath)) return;
                int n = 0;
                foreach (var line in File.ReadAllLines(FilePath)) {
                    var parts = line.Split('\t');
                    if (parts.Length < 3 || string.IsNullOrEmpty(parts[0])) continue;
                    var e = GetOrAdd(parts[0], parts[1]);
                    int.TryParse(parts[2], NumberStyles.Integer, CultureInfo.InvariantCulture, out e.Override);
                    if (e.Override < OverrideOff || e.Override > OverrideOn) e.Override = OverrideAuto;
                    if (parts.Length > 3 && parts[3].Length > 0) {
                        foreach (var s in parts[3].Split(',')) {
                            if (float.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out float f))
                                e.Shares.Add(Mathf.Clamp01(f));
                        }
                        while (e.Shares.Count > Window) e.Shares.RemoveAt(0);
                    }
                    n++;
                }
                UsefulTORStuffPlugin.Logger?.LogInfo($"[DeathTimeHistory] loaded {n} player(s).");
            } catch (Exception e) {
                UsefulTORStuffPlugin.Logger?.LogWarning($"[DeathTimeHistory] load failed: {e.Message}");
            }
        }

        private static void Save() {
            try {
                var lines = new List<string>(entries.Count);
                foreach (var kv in entries) {
                    string shares = string.Join(",",
                        kv.Value.Shares.Select(f => f.ToString("0.###", CultureInfo.InvariantCulture)));
                    lines.Add($"{kv.Key}\t{kv.Value.Name}\t{kv.Value.Override.ToString(CultureInfo.InvariantCulture)}\t{shares}");
                }
                // tmp + move: a crash mid-write must not cost the whole history.
                string tmp = FilePath + ".tmp";
                File.WriteAllLines(tmp, lines);
                File.Move(tmp, FilePath, true);
            } catch (Exception e) {
                UsefulTORStuffPlugin.Logger?.LogWarning($"[DeathTimeHistory] save failed: {e.Message}");
            }
        }

        // ====================================================================
        // The round clock. Driven every frame from EarlyDeathShieldUI.Update - a MonoBehaviour on the
        // plugin's own GameObject, for the reason NewcomerShield's header gives: no foreign patch
        // that throws can skip it.
        // ====================================================================

        private sealed class Participant {
            public string Code;
            public string Name;
        }

        private static DateTime? roundStart;
        private static readonly List<DateTime[]> meetings = new List<DateTime[]>();   // [start, end]
        private static DateTime? meetingOpen;
        private static readonly Dictionary<byte, Participant> participants = new Dictionary<byte, Participant>();

        private static bool roundSeen;
        private static float roundSeenAt;
        private static bool introOverSeen;
        private static DateTime introOverAt;
        private const float IntroFallbackSeconds = 10f;

        public static void Tick() {
            try {
                var client = AmongUsClient.Instance;
                bool inRound = client != null && ShipStatus.Instance != null && client.IsGameStarted;
                if (!inRound) {
                    // Only mark the round as left. The clock itself must survive: the game state flips to
                    // Ended BEFORE OnGameEnd runs, and clearing roundStart here made FinishRound bail out
                    // silently - not one round was ever recorded (playtest 2026-09-25, 6 players, Polus).
                    // The next round start clears it instead.
                    if (roundSeen) {
                        roundSeen = false;
                        UsefulTORStuffPlugin.Logger?.LogInfo(
                            $"[DeathTimeHistory] round left (state {client?.GameState}, clock {(roundStart != null ? "running" : "not started")}).");
                    }
                    return;
                }

                if (!roundSeen) {
                    ResetRound();
                    roundSeen = true;
                    roundSeenAt = Time.realtimeSinceStartup;
                }

                if (roundStart == null) {
                    if (introOverSeen) StartRound(introOverAt);
                    else if (Time.realtimeSinceStartup - roundSeenAt >= IntroFallbackSeconds) StartRound(DateTime.UtcNow);
                    return;
                }

                bool meetingRunning = MeetingHud.Instance != null || ExileController.Instance != null;
                if (meetingRunning && meetingOpen == null) {
                    meetingOpen = DateTime.UtcNow;
                } else if (!meetingRunning && meetingOpen != null) {
                    meetings.Add(new[] { meetingOpen.Value, DateTime.UtcNow });
                    meetingOpen = null;
                }
            } catch { }
        }

        private static void StartRound(DateTime at) {
            roundStart = at;
            participants.Clear();
            foreach (var p in PlayerControl.AllPlayerControls.ToArray()) {
                if (p == null || p.Data == null || p.Data.IsDead || p.Data.Disconnected) continue;
                string code = NewcomerShield.CodeOf(p);
                if (string.IsNullOrEmpty(code)) continue;
                participants[p.PlayerId] = new Participant { Code = code, Name = p.Data.PlayerName };
            }
            UsefulTORStuffPlugin.Logger?.LogInfo(
                $"[DeathTimeHistory] round clock started ({(introOverSeen ? "intro end" : "fallback")}), {participants.Count} participant(s).");
        }

        private static void ResetRound() {
            roundSeen = false;
            introOverSeen = false;
            roundStart = null;
            meetingOpen = null;
            meetings.Clear();
            participants.Clear();
        }

        // Gameplay seconds between the round start and `t`: wall time minus every meeting overlap.
        private static double GameplayAt(DateTime t) {
            if (roundStart == null) return 0;
            DateTime start = roundStart.Value;
            if (t <= start) return 0;
            double total = (t - start).TotalSeconds;
            foreach (var m in meetings) total -= Overlap(start, t, m[0], m[1]);
            if (meetingOpen != null) total -= Overlap(start, t, meetingOpen.Value, t);
            return Math.Max(0, total);
        }

        private static double Overlap(DateTime a0, DateTime a1, DateTime b0, DateTime b1) {
            DateTime s = a0 > b0 ? a0 : b0;
            DateTime e = a1 < b1 ? a1 : b1;
            return e > s ? (e - s).TotalSeconds : 0;
        }

        [HarmonyPatch(typeof(IntroCutscene), nameof(IntroCutscene.OnDestroy))]
        [HarmonyPriority(Priority.First)]
        static class IntroOverMarkerPatch {
            public static void Prefix() {
                if (introOverSeen) return;
                introOverSeen = true;
                introOverAt = DateTime.UtcNow;
            }
        }

        // ====================================================================
        // Round end: turn TOR's death list into one share per participant
        // ====================================================================

        private static System.Reflection.FieldInfo fiDeadPlayers;
        private static bool deadPlayersResolved;

        // GameHistory is internal to TOR; DeadPlayer itself is public, so the list casts cleanly
        // (Pelican.cs in Unknown's Collection reads it the same way).
        private static List<DeadPlayer> TorDeadPlayers() {
            try {
                if (!deadPlayersResolved) {
                    deadPlayersResolved = true;
                    fiDeadPlayers = typeof(CustomOption).Assembly.GetType("TheOtherRoles.GameHistory")
                        ?.GetField("deadPlayers", System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static);
                    if (fiDeadPlayers == null)
                        UsefulTORStuffPlugin.Logger?.LogWarning(
                            "[DeathTimeHistory] GameHistory.deadPlayers not found - death times are not recorded.");
                }
                return fiDeadPlayers?.GetValue(null) as List<DeadPlayer>;
            } catch { return null; }
        }

        private static bool IsForeignKill(DeadPlayer dp) {
            if (dp.killerIfExisting == null || dp.player == null) return false;
            if (dp.killerIfExisting.PlayerId == dp.player.PlayerId) return false;   // misfire, failed guess
            switch (dp.deathReason) {
                case DeadPlayer.CustomDeathReason.Kill:
                case DeadPlayer.CustomDeathReason.Guess:
                case DeadPlayer.CustomDeathReason.Bomb:
                case DeadPlayer.CustomDeathReason.Arson:
                case DeadPlayer.CustomDeathReason.WitchExile:
                    return true;
                default:
                    return false;   // Exile, Shift, Lover/Lawyer suicide, Disconnect
            }
        }

        [HarmonyPatch(typeof(AmongUsClient), nameof(AmongUsClient.OnGameEnd))]
        [HarmonyPriority(Priority.First)]
        static class GameEndPatch {
            public static void Prefix() {
                try { FinishRound(); }
                catch (Exception e) { UsefulTORStuffPlugin.Logger?.LogError($"[DeathTimeHistory] round end failed: {e}"); }
            }
        }

        private static void FinishRound() {
            if (roundStart == null || participants.Count == 0) {
                UsefulTORStuffPlugin.Logger?.LogInfo(
                    $"[DeathTimeHistory] round not recorded: clock {(roundStart != null ? "running" : "not started")}, {participants.Count} participant(s).");
                return;
            }
            if (HideNSeek.isHideNSeekGM || AntiStartKill.IsPropHuntGM()) return;   // deaths are the game there

            DateTime end = DateTime.UtcNow;
            double length = GameplayAt(end);
            if (length < MinRoundSeconds) {
                UsefulTORStuffPlugin.Logger?.LogInfo(
                    $"[DeathTimeHistory] round too short ({length:F0}s gameplay) - not recorded.");
                roundStart = null;
                return;
            }

            var deaths = new Dictionary<byte, DeadPlayer>();
            var list = TorDeadPlayers();
            if (list != null)
                foreach (var dp in list)
                    if (dp?.player != null) deaths[dp.player.PlayerId] = dp;

            EnsureLoaded();
            int killed = 0, survived = 0, skipped = 0;
            foreach (var kv in participants) {
                float share;
                if (deaths.TryGetValue(kv.Key, out var dp)) {
                    if (!IsForeignKill(dp)) { skipped++; continue; }
                    share = (float)Math.Clamp(GameplayAt(dp.timeOfDeath) / length, 0.0, 1.0);
                    killed++;
                } else {
                    var p = Helpers.playerById(kv.Key);
                    // Dead without a TOR entry (a revive undone, a foreign mod's kill) or gone:
                    // nothing reliable to say about this round.
                    if (p == null || p.Data == null || p.Data.Disconnected || p.Data.IsDead) { skipped++; continue; }
                    share = 1f;
                    survived++;
                }
                var e = GetOrAdd(kv.Value.Code, kv.Value.Name);
                e.Shares.Add(share);
                while (e.Shares.Count > Window) e.Shares.RemoveAt(0);
            }
            Save();
            roundStart = null;   // one record per round, even if OnGameEnd fires twice

            UsefulTORStuffPlugin.Logger?.LogInfo(
                $"[DeathTimeHistory] round recorded ({length:F0}s gameplay): {killed} killed, "
                + $"{survived} survived, {skipped} left out.");
        }
    }
}
