// Useful TOR Stuff - Copyright (C) 2026 DaUnknown-0
// Licensed under GPL-3.0-or-later. See LICENSE for details.
// Based on The Other Roles (https://github.com/TheOtherRolesAU/TheOtherRoles), GPL-3.0.

/*
 * SessionStats - the lobby statistics of one gaming evening, for everybody (User 2026-10-02).
 *
 * Per player: rounds, wins (overall and per team), roles played, kills, meetings survived, how much
 * of a round they survive, how their rounds ended, who killed them most and whom they killed most.
 * The last three are drawn as pie charts in SessionStatsUI.
 *
 * WHERE THE NUMBERS COME FROM
 * DeathTimeHistory already runs the round clock and reads TOR's GameHistory in the OnGameEnd prefix.
 * It hands every participant over (CaptureForSessionStats): team, main role, death reason, killer,
 * kills, meetings survived, survived share. The winners are not known yet at that point (TOR and
 * Unknown's Collection rewrite EndGameResult.CachedWinners in their OnGameEnd postfixes), so the
 * round waits as "pending" and is completed when the end screen is set up, where the winner list
 * is final. A round whose end screen never comes (disconnect mid end) is dropped.
 *
 * WHICH NUMBERS ARE SHOWN
 * Only the host's. Every client records, like DeathTimeHistory, so whoever hosts next already has
 * the evening; but the lobby view shows exactly what the host sends (module 238 on channel 240),
 * the same rule as the early-death statistics.
 *
 * SESSION
 * The rule of DeathTimeHistory (SessionGap 120 min, MaxAge 24 h), so both statistics mean the same
 * evening: a round that starts more than 120 minutes after the newest recorded one starts a new
 * session, and only rounds of the last 24 hours are kept.
 *
 * Option 1395 (General tab), module byte 238. See ID-Registry.md.
 */

using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using HarmonyLib;
using Hazel;
using TheOtherRoles;
using UnityEngine;
using Types = TheOtherRoles.CustomOption.CustomOptionType;

namespace UsefulTORStuff {

    public static class SessionStats {

        public static CustomOption Enabled;

        public const int ReasonSurvived = -1;
        public const byte OthersId = 255;          // killer/victim who is not in the lobby any more

        // How a round ended for a player, in the order the pie chart draws them.
        public const int CatSurvived = 0, CatKilled = 1, CatVoted = 2, CatGuessed = 3, CatOther = 4, CatSelf = 5;
        public const int CatCount = 6;

        private const byte RpcId = UsefulTORStuffPlugin.SessionStatsRpcId;
        private const byte SubRequest = 0;   // anyone -> host: send the table now
        private const byte SubBegin   = 1;   // host -> all: seq, row count, rounds in the session
        private const byte SubRow     = 2;   // host -> all: one player's numbers
        private const byte SubTitles  = 3;   // host -> all: titles of the evening (between Begin and the rows)
        private const byte SubCeremony = 4;  // host -> all: open the "end of the evening" titles panel now

        private const int MaxRoles = 3, MaxSlices = 7;

        // ---- one participant of one round ----
        public sealed class RoundEntry {
            public string Code = "";
            public string Name = "";
            public byte Team;              // 0 crew, 1 impostor, 2 neutral
            public string Role = "";
            public int Reason;             // ReasonSurvived or (int)DeadPlayer.CustomDeathReason
            public string KillerCode = "";
            public int Meetings;
            public int Kills;
            public float Share;            // survived share of the gameplay time, -1 unknown (disconnect)
            public bool Won;
        }

        public static bool IsKillReason(DeadPlayer.CustomDeathReason r) =>
            r == DeadPlayer.CustomDeathReason.Kill || r == DeadPlayer.CustomDeathReason.Guess
            || r == DeadPlayer.CustomDeathReason.Bomb || r == DeadPlayer.CustomDeathReason.Arson
            || r == DeadPlayer.CustomDeathReason.WitchExile;

        public static void CreateOptions() {
            try {
                Enabled = CustomOption.Create(1395, Types.General, "Session Statistics In The Lobby", true, null, true);
                UTSLocalization.BindOptionTitle(Enabled, "uts.sessionstats.option_name");
            } catch (Exception e) {
                UsefulTORStuffPlugin.Logger?.LogError($"[SessionStats] CreateOptions failed: {e}");
            }
        }

        public static void RegisterRpc() => UTSRpc.Register(RpcId, HandleModuleRpc);

        private static bool AmHost() => AmongUsClient.Instance != null && AmongUsClient.Instance.AmHost;

        // ====================================================================
        // Store
        // ====================================================================
        private sealed class Round {
            public DateTime End;
            public List<RoundEntry> Entries;
        }

        private static readonly List<Round> rounds = new List<Round>();
        private static bool loaded;
        private static int dataVersion;
        private static List<RoundEntry> pending;
        private static DateTime pendingStart, pendingEnd;

        private static string FilePath => Path.Combine(BepInEx.Paths.ConfigPath, "UTSSessionStats.txt");

        private static DateTime Newest() => rounds.Count == 0 ? DateTime.MinValue : rounds.Max(r => r.End);

        private static bool SessionOver() {
            var n = Newest();
            return n == DateTime.MinValue || DateTime.UtcNow - n > DeathTimeHistory.SessionGap;
        }

        /// <summary>Rounds that count right now: the live session, at most 24 hours back.</summary>
        private static List<Round> Current() {
            EnsureLoaded();
            if (SessionOver()) return new List<Round>();
            DateTime cut = DateTime.UtcNow - DeathTimeHistory.MaxAge;
            return rounds.Where(r => r.End >= cut).ToList();
        }

        // One player-round per line, tab separated:
        // unix end, code, name, team, role, won, reason, killer code, meetings, kills, share
        private static void EnsureLoaded() {
            if (loaded) return;
            loaded = true;
            try {
                if (!File.Exists(FilePath)) return;
                DateTime cut = DateTime.UtcNow - DeathTimeHistory.MaxAge;
                var byEnd = new Dictionary<long, Round>();
                int dropped = 0;
                foreach (var line in File.ReadAllLines(FilePath)) {
                    var f = line.Split('\t');
                    if (f.Length < 11 || !long.TryParse(f[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out long unix)) continue;
                    var end = DateTimeOffset.FromUnixTimeSeconds(unix).UtcDateTime;
                    if (end < cut) { dropped++; continue; }
                    var e = new RoundEntry {
                        Code = f[1], Name = f[2], Role = f[4], KillerCode = f[7],
                        Team = byte.TryParse(f[3], out var t) ? t : (byte)0,
                        Won = f[5] == "1",
                        Reason = int.TryParse(f[6], NumberStyles.Integer, CultureInfo.InvariantCulture, out var rs) ? rs : ReasonSurvived,
                        Meetings = int.TryParse(f[8], NumberStyles.Integer, CultureInfo.InvariantCulture, out var m) ? m : 0,
                        Kills = int.TryParse(f[9], NumberStyles.Integer, CultureInfo.InvariantCulture, out var k) ? k : 0,
                        Share = float.TryParse(f[10], NumberStyles.Float, CultureInfo.InvariantCulture, out var s) ? s : -1f
                    };
                    if (!byEnd.TryGetValue(unix, out var r)) byEnd[unix] = r = new Round { End = end, Entries = new List<RoundEntry>() };
                    r.Entries.Add(e);
                }
                rounds.AddRange(byEnd.Values.OrderBy(r => r.End));
                string session = "";
                if (rounds.Count > 0 && SessionOver()) {
                    session = $", last round {(DateTime.UtcNow - Newest()).TotalMinutes:F0} min ago - new session, {rounds.Count} round(s) dropped";
                    rounds.Clear();
                }
                dataVersion++;
                UsefulTORStuffPlugin.Logger?.LogInfo(
                    $"[SessionStats] loaded {rounds.Count} round(s), {dropped} player-round(s) older than 24 h left out{session}.");
            } catch (Exception e) {
                UsefulTORStuffPlugin.Logger?.LogWarning($"[SessionStats] load failed: {e.Message}");
            }
        }

        private static string Clean(string s) => (s ?? "").Replace('\t', ' ').Replace('\n', ' ').Replace('\r', ' ');

        private static void Save() {
            try {
                var lines = new List<string>();
                foreach (var r in rounds) {
                    string unix = new DateTimeOffset(r.End).ToUnixTimeSeconds().ToString(CultureInfo.InvariantCulture);
                    foreach (var e in r.Entries)
                        lines.Add(string.Join("\t", unix, Clean(e.Code), Clean(e.Name), e.Team.ToString(CultureInfo.InvariantCulture),
                            Clean(e.Role), e.Won ? "1" : "0", e.Reason.ToString(CultureInfo.InvariantCulture), Clean(e.KillerCode),
                            e.Meetings.ToString(CultureInfo.InvariantCulture), e.Kills.ToString(CultureInfo.InvariantCulture),
                            e.Share.ToString("0.###", CultureInfo.InvariantCulture)));
                }
                string tmp = FilePath + ".tmp";
                File.WriteAllLines(tmp, lines);
                File.Move(tmp, FilePath, true);
            } catch (Exception e) {
                UsefulTORStuffPlugin.Logger?.LogWarning($"[SessionStats] save failed: {e.Message}");
            }
        }

        // ====================================================================
        // Recording: DeathTimeHistory hands the round over, the end screen adds the winners
        // ====================================================================
        public static void RoundCaptured(List<RoundEntry> entries, DateTime start, DateTime end) {
            if (entries == null || entries.Count == 0) return;
            pending = entries;
            pendingStart = start;
            pendingEnd = end;
        }

        [HarmonyPatch(typeof(EndGameManager), nameof(EndGameManager.SetEverythingUp))]
        static class EndScreenPatch {
            [HarmonyPriority(Priority.First)]
            public static void Prefix() {
                try { FinishPending(); }
                catch (Exception e) { UsefulTORStuffPlugin.Logger?.LogError($"[SessionStats] round end failed: {e}"); }
            }
        }

        private static void FinishPending() {
            if (pending == null) return;
            var entries = pending;
            pending = null;

            var winners = new HashSet<string>();
            try {
                foreach (var w in EndGameResult.CachedWinners.ToArray())
                    if (w != null && !string.IsNullOrEmpty(w.PlayerName)) winners.Add(w.PlayerName);
            } catch (Exception e) {
                UsefulTORStuffPlugin.Logger?.LogWarning($"[SessionStats] winner list not readable ({e.Message}) - round not recorded.");
                return;
            }
            foreach (var e in entries) e.Won = winners.Contains(e.Name);

            EnsureLoaded();
            var newest = Newest();
            if (newest != DateTime.MinValue && pendingStart - newest > DeathTimeHistory.SessionGap) {
                UsefulTORStuffPlugin.Logger?.LogInfo(
                    $"[SessionStats] no round for over {DeathTimeHistory.SessionGap.TotalMinutes:F0} min - new session, {rounds.Count} old round(s) dropped.");
                rounds.Clear();
            }
            DateTime cut = DateTime.UtcNow - DeathTimeHistory.MaxAge;
            rounds.RemoveAll(r => r.End < cut);
            rounds.Add(new Round { End = pendingEnd, Entries = entries });
            dataVersion++;
            Save();
            UsefulTORStuffPlugin.Logger?.LogInfo(
                $"[SessionStats] round recorded: {entries.Count} player(s), {entries.Count(x => x.Won)} winner(s), "
                + $"{rounds.Count} round(s) in this session.");
        }

        // ====================================================================
        // Aggregation (host side)
        // ====================================================================
        public sealed class Row {
            public byte PlayerId;
            public int Rounds, Wins, Kills;
            public readonly int[] TeamRounds = new int[3], TeamWins = new int[3];
            public float MeetingsAvg;          // meetings survived per round
            public float ShareAvg = -1f;       // survived share of a round, -1 unknown
            public readonly int[] DeathCats = new int[CatCount];
            public readonly List<(string Role, int Count)> Roles = new List<(string, int)>();
            public readonly List<(byte Id, int Count)> KilledBy = new List<(byte, int)>();
            public readonly List<(byte Id, int Count)> Victims = new List<(byte, int)>();
        }

        // Titles of the evening (User 2026-10-02): computed by the host from the same rounds, shown in the
        // panel's second tab after every round. Order = display order.
        public const byte TitleSerialKiller = 0, TitleSurvivor = 1, TitleLucky = 2, TitleUnlucky = 3,
                          TitleTalker = 4, TitleSuspicious = 5, TitleNemesis = 6, TitleAllrounder = 7, TitleImpostor = 8;
        public const int TitleCount = 9;

        public sealed class Title {
            public byte Id;
            public int Value;                                   // kills, %, count ... (meetings x100)
            public readonly List<byte> Holders = new List<byte>();   // Nemesis: [killer, victim]
        }

        public sealed class Table {
            public byte Seq;
            public int SessionRounds;
            public int Expected;
            public readonly Dictionary<byte, Row> Rows = new Dictionary<byte, Row>();
            public readonly List<Title> Titles = new List<Title>();
            public float ReceivedAt;
            public bool Complete => Rows.Count >= Expected;
        }

        public static Table LastTable { get; private set; }
        private static Table incoming;

        public static int CategoryOf(RoundEntry e) {
            if (e.Reason == ReasonSurvived) return CatSurvived;
            var r = (DeadPlayer.CustomDeathReason)e.Reason;
            if (r == DeadPlayer.CustomDeathReason.Exile) return CatVoted;
            if (r == DeadPlayer.CustomDeathReason.LoverSuicide || r == DeadPlayer.CustomDeathReason.LawyerSuicide) return CatOther;
            if (r == DeadPlayer.CustomDeathReason.Shift) return CatSelf;
            if (!string.IsNullOrEmpty(e.KillerCode) && e.KillerCode == e.Code) return CatSelf;   // misfire, failed guess, own bomb
            if (r == DeadPlayer.CustomDeathReason.Kill) return CatKilled;
            if (r == DeadPlayer.CustomDeathReason.Guess) return CatGuessed;
            return CatOther;   // Bomb, Arson, Witch
        }

        private static bool IsKilledBySomeone(RoundEntry e) =>
            e.Reason != ReasonSurvived && IsKillReason((DeadPlayer.CustomDeathReason)e.Reason)
            && !string.IsNullOrEmpty(e.KillerCode) && e.KillerCode != e.Code;

        /// <summary>The table for the players in the lobby right now (host side).</summary>
        public static Table Build() {
            var t = new Table { ReceivedAt = Time.realtimeSinceStartup };
            var cur = Current();
            t.SessionRounds = cur.Count;
            var present = new Dictionary<string, byte>();
            foreach (var p in PlayerControl.AllPlayerControls.ToArray()) {
                if (p == null || p.Data == null || p.Data.Disconnected) continue;
                string code = NewcomerShield.CodeOf(p);
                if (!string.IsNullOrEmpty(code) && !present.ContainsKey(code)) present[code] = p.PlayerId;
            }
            var all = cur.SelectMany(r => r.Entries).ToList();
            foreach (var kv in present) {
                var row = new Row { PlayerId = kv.Value };
                var mine = all.Where(e => e.Code == kv.Key && e.Reason != (int)DeadPlayer.CustomDeathReason.Disconnect).ToList();
                row.Rounds = mine.Count;
                foreach (var e in mine) {
                    int team = Mathf.Clamp(e.Team, 0, 2);
                    row.TeamRounds[team]++;
                    if (e.Won) { row.Wins++; row.TeamWins[team]++; }
                    row.Kills += e.Kills;
                    row.DeathCats[CategoryOf(e)]++;
                }
                if (mine.Count > 0) {
                    row.MeetingsAvg = (float)mine.Average(e => e.Meetings);
                    var shares = mine.Where(e => e.Share >= 0f).ToList();
                    if (shares.Count > 0) row.ShareAvg = shares.Average(e => e.Share);
                }
                foreach (var g in mine.GroupBy(e => e.Role).OrderByDescending(g => g.Count()).ThenBy(g => g.Key).Take(MaxRoles))
                    row.Roles.Add((g.Key, g.Count()));
                row.KilledBy.AddRange(Slices(mine.Where(IsKilledBySomeone).Select(e => e.KillerCode), present));
                row.Victims.AddRange(Slices(all.Where(e => e.KillerCode == kv.Key && IsKilledBySomeone(e)).Select(e => e.Code), present));
                t.Rows[row.PlayerId] = row;
            }
            t.Expected = t.Rows.Count;
            BuildTitles(t, all, present);
            return t;
        }

        // Per title the best value among the players present; ties share the title (at most three
        // holders). A title nobody qualifies for is left out.
        private static void BuildTitles(Table t, List<RoundEntry> all, Dictionary<string, byte> present) {
            void Best(byte id, Func<Row, int> value, Func<Row, bool> qualifies) {
                var cand = t.Rows.Values.Where(r => qualifies(r)).Select(r => (r.PlayerId, V: value(r))).Where(x => x.V > 0).ToList();
                if (cand.Count == 0) return;
                int max = cand.Max(x => x.V);
                var title = new Title { Id = id, Value = max };
                foreach (var x in cand.Where(x => x.V == max).OrderBy(x => x.PlayerId).Take(3)) title.Holders.Add(x.PlayerId);
                t.Titles.Add(title);
            }
            Best(TitleSerialKiller, r => r.Kills, r => true);
            Best(TitleSurvivor, r => Mathf.RoundToInt(r.ShareAvg * 100f), r => r.Rounds >= 2 && r.ShareAvg >= 0f);
            Best(TitleLucky, r => Mathf.RoundToInt(100f * r.Wins / Mathf.Max(1, r.Rounds)), r => r.Rounds >= 3);
            Best(TitleUnlucky, r => r.DeathCats[CatKilled] + r.DeathCats[CatGuessed] + r.DeathCats[CatOther], r => true);
            Best(TitleTalker, r => Mathf.RoundToInt(r.MeetingsAvg * 100f), r => r.Rounds >= 2);
            Best(TitleSuspicious, r => r.DeathCats[CatVoted], r => true);

            // Nemesis: the killer/victim pair with the most kills (at least two)
            var pair = all.Where(e => IsKilledBySomeone(e) && present.ContainsKey(e.Code) && present.ContainsKey(e.KillerCode))
                          .GroupBy(e => (e.KillerCode, e.Code)).OrderByDescending(g => g.Count()).FirstOrDefault();
            if (pair != null && pair.Count() >= 2) {
                var n = new Title { Id = TitleNemesis, Value = pair.Count() };
                n.Holders.Add(present[pair.Key.KillerCode]);
                n.Holders.Add(present[pair.Key.Code]);
                t.Titles.Add(n);
            }

            var roleKinds = present.ToDictionary(kv => kv.Value,
                kv => all.Where(e => e.Code == kv.Key && e.Reason != (int)DeadPlayer.CustomDeathReason.Disconnect)
                         .Select(e => e.Role).Distinct().Count());
            Best(TitleAllrounder, r => roleKinds.TryGetValue(r.PlayerId, out var k) && k >= 2 ? k : 0, r => true);
            Best(TitleImpostor, r => r.TeamWins[1], r => true);
            t.Titles.Sort((a, b) => a.Id.CompareTo(b.Id));
        }

        // Counts per opponent, biggest first; opponents who left and everything past the first slices
        // fold into "others".
        private static IEnumerable<(byte, int)> Slices(IEnumerable<string> codes, Dictionary<string, byte> present) {
            var counts = new Dictionary<byte, int>();
            foreach (var c in codes) {
                byte id = present.TryGetValue(c, out var pid) ? pid : OthersId;
                counts[id] = (counts.TryGetValue(id, out var n) ? n : 0) + 1;
            }
            var list = counts.Where(x => x.Key != OthersId).OrderByDescending(x => x.Value).ThenBy(x => x.Key).ToList();
            int others = counts.TryGetValue(OthersId, out var o) ? o : 0;
            while (list.Count > MaxSlices - 1) { others += list[list.Count - 1].Value; list.RemoveAt(list.Count - 1); }
            foreach (var x in list) yield return (x.Key, x.Value);
            if (others > 0) yield return (OthersId, others);
        }

        // ====================================================================
        // Network
        // ====================================================================
        private static byte seq;

        private static void SendTable(Table t) {
            try {
                seq++;
                t.Seq = seq;
                MessageWriter w = UTSRpc.Begin(RpcId);
                w.Write(SubBegin);
                w.Write(t.Seq);
                w.Write((byte)Math.Min(t.Rows.Count, 255));
                w.Write((ushort)Math.Min(t.SessionRounds, ushort.MaxValue));
                AmongUsClient.Instance.FinishRpcImmediately(w);
                MessageWriter tw = UTSRpc.Begin(RpcId);
                tw.Write(SubTitles);
                tw.Write(t.Seq);
                tw.Write(B(t.Titles.Count));
                foreach (var ti in t.Titles) {
                    tw.Write(ti.Id);
                    tw.Write((ushort)Mathf.Clamp(ti.Value, 0, ushort.MaxValue));
                    tw.Write(B(ti.Holders.Count));
                    foreach (byte h in ti.Holders) tw.Write(h);
                }
                AmongUsClient.Instance.FinishRpcImmediately(tw);
                // one message per player keeps every packet far below the MTU
                foreach (var row in t.Rows.Values) {
                    MessageWriter r = UTSRpc.Begin(RpcId);
                    r.Write(SubRow);
                    r.Write(t.Seq);
                    WriteRow(r, row);
                    AmongUsClient.Instance.FinishRpcImmediately(r);
                }
            } catch (Exception e) {
                UsefulTORStuffPlugin.Logger?.LogWarning($"[SessionStats] send failed: {e.Message}");
            }
            LastTable = t;
        }

        private static byte B(int v) => (byte)Mathf.Clamp(v, 0, 255);

        private static void WriteRow(MessageWriter w, Row row) {
            w.Write(row.PlayerId);
            w.Write(B(row.Rounds));
            w.Write(B(row.Wins));
            for (int i = 0; i < 3; i++) { w.Write(B(row.TeamRounds[i])); w.Write(B(row.TeamWins[i])); }
            w.Write((ushort)Mathf.Clamp(row.Kills, 0, ushort.MaxValue));
            w.Write((ushort)Mathf.Clamp(Mathf.RoundToInt(row.MeetingsAvg * 100f), 0, ushort.MaxValue));
            w.Write(row.ShareAvg < 0f ? ushort.MaxValue : (ushort)Mathf.Clamp(Mathf.RoundToInt(row.ShareAvg * 1000f), 0, 1000));
            for (int i = 0; i < CatCount; i++) w.Write(B(row.DeathCats[i]));
            w.Write(B(row.Roles.Count));
            foreach (var (role, n) in row.Roles) {
                string s = role ?? "?";
                w.Write(s.Length > 32 ? s.Substring(0, 32) : s);
                w.Write(B(n));
            }
            WriteSlices(w, row.KilledBy);
            WriteSlices(w, row.Victims);
        }

        private static void WriteSlices(MessageWriter w, List<(byte Id, int Count)> list) {
            w.Write(B(list.Count));
            foreach (var (id, n) in list) { w.Write(id); w.Write(B(n)); }
        }

        private static Row ReadRow(MessageReader r) {
            var row = new Row { PlayerId = r.ReadByte(), Rounds = r.ReadByte(), Wins = r.ReadByte() };
            for (int i = 0; i < 3; i++) { row.TeamRounds[i] = r.ReadByte(); row.TeamWins[i] = r.ReadByte(); }
            row.Kills = r.ReadUInt16();
            row.MeetingsAvg = r.ReadUInt16() / 100f;
            ushort share = r.ReadUInt16();
            row.ShareAvg = share == ushort.MaxValue ? -1f : share / 1000f;
            for (int i = 0; i < CatCount; i++) row.DeathCats[i] = r.ReadByte();
            int k = r.ReadByte();
            for (int i = 0; i < k; i++) row.Roles.Add((r.ReadString(), r.ReadByte()));
            ReadSlices(r, row.KilledBy);
            ReadSlices(r, row.Victims);
            return row;
        }

        private static void ReadSlices(MessageReader r, List<(byte, int)> list) {
            int n = r.ReadByte();
            for (int i = 0; i < n; i++) list.Add((r.ReadByte(), r.ReadByte()));
        }

        /// <summary>Host: open the end-of-evening titles panel for everybody in the lobby (and here).</summary>
        public static void SendCeremony() {
            if (!AmHost()) return;
            try {
                MessageWriter w = UTSRpc.Begin(RpcId);
                w.Write(SubCeremony);
                AmongUsClient.Instance.FinishRpcImmediately(w);
            } catch (Exception e) {
                UsefulTORStuffPlugin.Logger?.LogWarning($"[SessionStats] ceremony send failed: {e.Message}");
            }
            requested = true; nextTick = 0f;   // fresh table with the titles right behind it
            SessionStatsUI.Instance?.OpenCeremony();
        }

        public static void RequestTable() {
            if (AmHost()) { requested = true; nextTick = 0f; return; }
            try {
                MessageWriter w = UTSRpc.Begin(RpcId);
                w.Write(SubRequest);
                AmongUsClient.Instance.FinishRpcImmediately(w);
            } catch (Exception e) {
                UsefulTORStuffPlugin.Logger?.LogWarning($"[SessionStats] request failed: {e.Message}");
            }
        }

        private static void HandleModuleRpc(MessageReader reader) {
            try {
                byte sub = reader.ReadByte();
                switch (sub) {
                    case SubRequest:
                        if (AmHost()) { requested = true; nextTick = 0f; }
                        break;
                    case SubCeremony:
                        if (UTSRpc.RequireHost("SessionStats.Ceremony")) SessionStatsUI.Instance?.OpenCeremony();
                        break;
                    case SubBegin: {
                        var t = new Table { Seq = reader.ReadByte(), Expected = reader.ReadByte(), SessionRounds = reader.ReadUInt16() };
                        if (!UTSRpc.RequireHost("SessionStats.Begin")) break;
                        incoming = t;
                        if (t.Expected == 0) Publish(t);
                        break;
                    }
                    case SubTitles: {
                        byte s = reader.ReadByte();
                        int n = reader.ReadByte();
                        var list = new List<Title>(n);
                        for (int i = 0; i < n; i++) {
                            var ti = new Title { Id = reader.ReadByte(), Value = reader.ReadUInt16() };
                            int k = reader.ReadByte();
                            for (int j = 0; j < k; j++) ti.Holders.Add(reader.ReadByte());
                            list.Add(ti);
                        }
                        if (!UTSRpc.RequireHost("SessionStats.Titles")) break;
                        if (incoming == null || incoming.Seq != s) break;
                        incoming.Titles.Clear();
                        incoming.Titles.AddRange(list);
                        break;
                    }
                    case SubRow: {
                        byte s = reader.ReadByte();
                        var row = ReadRow(reader);
                        if (!UTSRpc.RequireHost("SessionStats.Row")) break;
                        if (incoming == null || incoming.Seq != s) break;   // a row of an older send
                        incoming.Rows[row.PlayerId] = row;
                        if (incoming.Complete) Publish(incoming);
                        break;
                    }
                }
            } catch (Exception e) {
                UsefulTORStuffPlugin.Logger?.LogError($"[SessionStats] rpc failed: {e}");
            }
        }

        private static void Publish(Table t) {
            t.ReceivedAt = Time.realtimeSinceStartup;
            LastTable = t;
            incoming = null;
        }

        /// <summary>New lobby: the numbers of the last one are not valid any more.</summary>
        public static void ClearTable() { LastTable = null; incoming = null; lastKey = "?"; }

        // ====================================================================
        // Host driver: send on change (data or lobby members) and on request
        // ====================================================================
        private static float nextTick;
        private static bool requested;
        private static string lastKey = "?";

        public static bool IsEnabled() {
            try { return Enabled != null && Enabled.getBool(); } catch { return false; }
        }

        public static void Tick() {
            try {
                if (Time.realtimeSinceStartup < nextTick) return;
                nextTick = Time.realtimeSinceStartup + 1f;
                if (!AmHost() || !LobbyScreen.Exists || !IsEnabled()) return;
                EnsureLoaded();
                var ids = new List<string>();
                foreach (var p in PlayerControl.AllPlayerControls.ToArray())
                    if (p != null && p.Data != null && !p.Data.Disconnected) ids.Add(p.PlayerId + ":" + NewcomerShield.CodeOf(p));
                ids.Sort(StringComparer.Ordinal);
                string key = dataVersion + "|" + (SessionOver() ? 0 : 1) + "|" + string.Join(",", ids);
                if (key == lastKey && !requested) return;
                lastKey = key;
                requested = false;
                SendTable(Build());
            } catch (Exception e) {
                UsefulTORStuffPlugin.Logger?.LogWarning($"[SessionStats] tick failed: {e.Message}");
            }
        }

        /// <summary>Autotest: sample numbers for every figure present (freeplay dummies).</summary>
        public static void DiagSample() {
            var t = new Table { SessionRounds = 9, ReceivedAt = Time.realtimeSinceStartup };
            var ids = PlayerControl.AllPlayerControls.ToArray().Where(p => p != null).Select(p => p.PlayerId).ToList();
            string[] roles = { "Sheriff", "Medic", "Morphling", "Jester", "Engineer", "Vampire", "Lawyer", "Crewmate" };
            for (int i = 0; i < ids.Count; i++) {
                var row = new Row { PlayerId = ids[i], Rounds = 9, Wins = 3 + i % 4, Kills = (i * 3) % 7 };
                row.TeamRounds[0] = 5; row.TeamRounds[1] = 3; row.TeamRounds[2] = 1;
                row.TeamWins[0] = 2 + i % 3; row.TeamWins[1] = 1 + i % 2; row.TeamWins[2] = i % 2;
                row.MeetingsAvg = 1.4f + 0.3f * (i % 5);
                row.ShareAvg = 0.35f + 0.08f * (i % 6);
                int[] cats = { 3, 2, 2, 1, 0, 1 };
                for (int c = 0; c < CatCount; c++) row.DeathCats[c] = cats[(c + i) % CatCount];
                row.Roles.Add((roles[i % roles.Length], 3)); row.Roles.Add((roles[(i + 3) % roles.Length], 2)); row.Roles.Add(("Crewmate", 1));
                for (int j = 1; j <= 3 && j < ids.Count; j++) row.KilledBy.Add((ids[(i + j) % ids.Count], 4 - j));
                row.KilledBy.Add((OthersId, 1));
                for (int j = 2; j <= 4 && j < ids.Count; j++) row.Victims.Add((ids[(i + j) % ids.Count], 5 - j));
                t.Rows[row.PlayerId] = row;
            }
            t.Expected = t.Rows.Count;
            int[] values = { 7, 68, 67, 5, 210, 3, 3, 5, 4 };
            for (byte id = 0; id < TitleCount; id++) {
                if (ids.Count == 0) break;
                var ti = new Title { Id = id, Value = values[id] };
                ti.Holders.Add(ids[id % ids.Count]);
                if (id == TitleNemesis || id == TitleSuspicious) ti.Holders.Add(ids[(id + 2) % ids.Count]);
                t.Titles.Add(ti);
            }
            LastTable = t;
        }
    }
}
