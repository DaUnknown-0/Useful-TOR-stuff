// Useful TOR Stuff - Copyright (C) 2026 DaUnknown-0
// Licensed under GPL-3.0-or-later. See LICENSE for details.
// Based on The Other Roles (https://github.com/TheOtherRolesAU/TheOtherRoles), GPL-3.0.

/*
 * EarlyDeathShield - the pink shield: whoever keeps getting killed early gets the newcomer's
 * treatment, a round in which nobody can kill them before the first meeting.
 *
 * The newcomer shield answers "who has never played with us"; this one answers "who never gets to
 * play". Some players are simply the first body in round after round - a quiet player nobody
 * suspects, the one who always does electrical first - and spend most of the evening as a ghost.
 *
 * WHO GETS IT
 * DeathTimeHistory keeps, per player, the share of each round they survived before somebody else
 * killed them. A player with at least "Minimum Recorded Rounds" rounds counts as QUALIFIED. The
 * LOBBY AVERAGE is the mean of the qualified players currently in the lobby (at least three, or
 * there is no meaningful average and nobody is picked automatically). Whoever sits below
 * "Threshold" percent of that average is a candidate; the lowest ones are picked, at most
 * "Maximum Shielded Players" of them. Relative, not absolute: in a lobby with two aggressive
 * impostor mains everybody dies earlier, and the shield should still go to the one who dies
 * earliest of all.
 *
 * Shielded rounds are recorded like any other. The shield only buys the opening minutes, so a
 * player who really keeps dying early stays low, and one who only had a bad week climbs out of it
 * - the shield regulates itself instead of sticking forever to whoever once qualified.
 *
 * HOST OVERRIDE
 * The lobby panel (EarlyDeathShieldUI) lets the host force the shield on or off per player. Unlike
 * the newcomer's one-round marks these are persistent (stored in DeathTimeHistory's file), because
 * the rule they override is persistent too: "never shield X" should not need clicking every lobby.
 *
 * VISIBLE IN THE LOBBY
 * Exactly like the newcomer shield: the host broadcasts the list while the lobby is open and
 * refreshes it whenever it changes, and UTSShieldOutlines paints the pink outline for everyone.
 *
 * LIFETIME AND ENFORCEMENT
 * Deliberately the newcomer's, layer for layer (see NewcomerShield.cs for the reasoning behind each):
 *  - assigned at the end of the intro by the host, over module byte 239;
 *  - dropped when the first meeting opens, or after it when "Shield During The First Meeting" keeps
 *    it alive through that meeting (vote/guess blocks live in NewcomerMeetingProtection.cs). A
 *    FORCED shield can last up to the 2nd or 3rd meeting instead, set per player in the lobby
 *    panel and kept in the history file (User 2026-10-02); the meeting option then applies to
 *    every meeting it covers;
 *  - enforcement 0: TOR's setTarget untargetable list (peaceful abilities excepted, ShieldPeaceGate);
 *  - enforcement 1: vanilla CheckMurder on the host;
 *  - enforcement 2: Helpers.checkMuderAttempt postfix on the killer's client.
 * The lover cascade stays untouched for the same reason as there.
 *
 * Options 1383-1388, module byte 239 on UTSRpc.CallId = 240. See ID-Registry.md.
 */

using System;
using System.Collections.Generic;
using System.Linq;
using HarmonyLib;
using Hazel;
using UnityEngine;
using TheOtherRoles;
using Types = TheOtherRoles.CustomOption.CustomOptionType;

namespace UsefulTORStuff {

    public static class EarlyDeathShield {

        // ---- Options (1383-1388) ----
        public static CustomOption Enabled;
        public static CustomOption MeetingProtection;
        public static CustomOption Threshold;
        public static CustomOption MinRounds;
        public static CustomOption MaxShielded;

        private static readonly string[] ThresholdValues = { "40%", "50%", "60%", "70%", "80%" };

        // Fewer qualified players than this and there is no lobby average worth comparing against.
        private const int MinQualifiedForAverage = 3;

        // ---- Everyone: who is shielded in THIS round, and up to which meeting (1 unless forced longer) ----
        private static readonly HashSet<byte> shielded = new HashSet<byte>();
        private static readonly Dictionary<byte, int> lastsUntil = new Dictionary<byte, int>();

        private const byte RpcId = UsefulTORStuffPlugin.EarlyDeathShieldRpcId;
        private const byte SubSetShields = 0;   // count, ids...
        private const byte SubClear      = 1;
        private const byte SubStats      = 2;   // host -> all: the lobby statistics (read-only viewer)
        private const byte SubRequest    = 3;   // anyone -> host: please send the statistics now
        // host -> all, right after SubSetShields: count, (id, meetings)... A forced shield can last up
        // to the 2nd or 3rd meeting (User 2026-10-02). A separate sub so an older build, which knows
        // only Sub 0, still shields everyone until the first meeting as before.
        private const byte SubMeetings   = 4;

        private static int LastsUntil(byte id) => lastsUntil.TryGetValue(id, out int n) ? n : 1;

        public static bool Active => shielded.Count > 0;
        public static bool IsShielded(byte playerId) => shielded.Contains(playerId);

        // Pink, as asked. Not TOR's pink player colour (238,84,187) and brighter than the Lovers'
        // heart pink, so it reads as an outline and not as a cosmetic.
        public static readonly Color ShieldColor = new Color32(255, 90, 205, byte.MaxValue);

        // Selection 0 = off (shield ends at meeting start), 1 = votes, 2 = guesses, 3 = both.
        private static int MeetingMode {
            get { try { return MeetingProtection == null ? 0 : UTSGate.Sel(MeetingProtection); } catch { return 0; } }
        }
        public static bool SurvivesMeeting => MeetingMode > 0;
        public static bool BlocksVotes => MeetingMode == 1 || MeetingMode == 3;
        public static bool BlocksGuesses => MeetingMode == 2 || MeetingMode == 3;

        public static void CreateOptions() {
            try {
                Enabled = CustomOption.Create(1383, Types.General,
                    "Protect Players Who Often Die Early", false, null, true);
                // Explicit constructor for the selection lists, for the reason NewcomerShield gives
                // (the Create(string[]) overload hardcodes its default).
                MeetingProtection = new CustomOption(1385, Types.General,
                    "Shield During The First Meeting",
                    new string[] { "Ends Before The Meeting", "Blocks Votes", "Blocks Guesses", "Blocks Both" },
                    "Ends Before The Meeting", Enabled, false);
                Threshold = new CustomOption(1386, Types.General,
                    "Shield Below % Of The Lobby Average", ThresholdValues, "60%", Enabled, false);
                MinRounds = CustomOption.Create(1387, Types.General,
                    "Minimum Recorded Rounds", 5f, 3f, 20f, 1f, Enabled);
                MaxShielded = CustomOption.Create(1388, Types.General,
                    "Maximum Shielded Players", 1f, 1f, 3f, 1f, Enabled);
                UTSLocalization.BindOptionTitle(Enabled, "uts.earlydeath.option_name");
                UTSLocalization.BindOptionTitle(MeetingProtection, "uts.earlydeath.option_meeting");
                UTSLocalization.BindOptionSelections(MeetingProtection, "uts.earlydeath.option_meeting_values");
                UTSLocalization.BindOptionTitle(Threshold, "uts.earlydeath.option_threshold");
                UTSLocalization.BindOptionTitle(MinRounds, "uts.earlydeath.option_minrounds");
                UTSLocalization.BindOptionTitle(MaxShielded, "uts.earlydeath.option_max");
                UsefulTORStuffPlugin.Logger?.LogInfo("[EarlyDeathShield] Options created.");
            } catch (Exception e) {
                UsefulTORStuffPlugin.Logger?.LogError($"[EarlyDeathShield] CreateOptions failed: {e}");
            }
        }

        public static void RegisterRpc() => UTSRpc.Register(RpcId, HandleModuleRpc);

        private static bool AmHost() => AmongUsClient.Instance != null && AmongUsClient.Instance.AmHost;

        private static bool IsEnabled() {
            try { return Enabled != null && Enabled.getBool(); } catch { return false; }
        }

        // ====================================================================
        // The decision (host side). One evaluation covers the whole lobby: the average depends on
        // everybody present, so a single player's verdict cannot be computed in isolation.
        // ====================================================================
        public sealed class Verdict {
            public byte PlayerId;
            public string Code;
            public string Name;
            public DeathTimeHistory.Stat Stat;
            public int Override;
            public bool Qualified;
            public bool AutoPick;
            public bool Shield;
            public int Meetings = 1;   // up to which meeting the shield lasts (forced: 1-3)
        }

        public sealed class Evaluation {
            public readonly List<Verdict> Rows = new List<Verdict>();
            public float LobbyMean;        // mean of the qualified players' means; 0 without an average
            public bool HasAverage;
            public int MinRounds;
            public int ThresholdPercent;
            public int ShieldCount => Rows.Count(r => r.Shield);
            public Verdict Of(byte playerId) => Rows.FirstOrDefault(r => r.PlayerId == playerId);
        }

        public static int ThresholdPercent {
            get {
                try { return 40 + 10 * Mathf.Clamp(Threshold?.selection ?? 2, 0, ThresholdValues.Length - 1); }
                catch { return 60; }
            }
        }

        public static Evaluation Evaluate() {
            var ev = new Evaluation();
            try {
                ev.MinRounds = MinRounds == null ? 5 : Mathf.RoundToInt(MinRounds.getFloat());
                ev.ThresholdPercent = ThresholdPercent;
                int max = MaxShielded == null ? 1 : Mathf.RoundToInt(MaxShielded.getFloat());

                foreach (var p in PlayerControl.AllPlayerControls.ToArray()) {
                    if (p == null || p.Data == null || p.Data.Disconnected) continue;
                    string code = NewcomerShield.CodeOf(p);
                    if (string.IsNullOrEmpty(code)) continue;
                    var stat = DeathTimeHistory.StatOf(code);
                    ev.Rows.Add(new Verdict {
                        PlayerId = p.PlayerId,
                        Code = code,
                        Name = p.Data.PlayerName ?? "?",
                        Stat = stat,
                        Override = DeathTimeHistory.OverrideOf(code),
                        Qualified = stat.Rounds >= ev.MinRounds
                    });
                }

                var qualified = ev.Rows.Where(r => r.Qualified).ToList();
                ev.HasAverage = qualified.Count >= MinQualifiedForAverage;
                if (ev.HasAverage) {
                    ev.LobbyMean = qualified.Average(r => r.Stat.Mean);
                    float limit = ev.LobbyMean * ev.ThresholdPercent / 100f;
                    foreach (var r in qualified
                                 .Where(r => r.Override == DeathTimeHistory.OverrideAuto && r.Stat.Mean < limit)
                                 .OrderBy(r => r.Stat.Mean)
                                 .Take(Math.Max(0, max)))
                        r.AutoPick = true;
                }

                foreach (var r in ev.Rows) {
                    r.Shield = r.Override == DeathTimeHistory.OverrideOn
                               || (r.Override == DeathTimeHistory.OverrideAuto && r.AutoPick);
                    r.Meetings = r.Override == DeathTimeHistory.OverrideOn ? DeathTimeHistory.ForcedMeetingsOf(r.Code) : 1;
                }
            } catch (Exception e) {
                UsefulTORStuffPlugin.Logger?.LogWarning($"[EarlyDeathShield] evaluation failed: {e.Message}");
            }
            return ev;
        }

        // ---- host overrides (lobby panel) ----

        // Flips whatever the player's CURRENT state is: shielded becomes forced off, unshielded
        // becomes forced on - the button always does what its label says (NewcomerShield's rule).
        public static void ToggleOverride(Verdict v) {
            if (v == null) return;
            DeathTimeHistory.SetOverride(v.Code, v.Name,
                v.Shield ? DeathTimeHistory.OverrideOff : DeathTimeHistory.OverrideOn);
        }

        public static void ClearOverride(Verdict v) {
            if (v == null) return;
            DeathTimeHistory.SetOverride(v.Code, v.Name, DeathTimeHistory.OverrideAuto);
        }

        /// <summary>Forced rows only: 1 -> 2 -> 3 -> 1 meetings.</summary>
        public static void CycleMeetings(Verdict v) {
            if (v == null || v.Override != DeathTimeHistory.OverrideOn) return;
            DeathTimeHistory.SetForcedMeetings(v.Code, v.Name, v.Meetings % DeathTimeHistory.MaxForcedMeetings + 1);
        }

        // ====================================================================
        // Statistics for everyone (User 25.09.: every player can look at everyone's numbers in the
        // lobby, their own and the average included). Only the host's records decide the shield,
        // so the viewer shows exactly those: the host sends its evaluation, nobody shows local data.
        // ====================================================================
        public sealed class StatsRow {
            public byte PlayerId;
            public int Rounds;
            public float Mean;
            public bool Qualified;
            public bool Shield;
            // host override, shown openly to everyone (User 25.09.): DeathTimeHistory.OverrideAuto/On/Off
            public int Override;
            public int Meetings = 1;
        }

        public sealed class StatsTable {
            public readonly List<StatsRow> Rows = new List<StatsRow>();
            public bool HasAverage;
            public float LobbyMean;
            public int ThresholdPercent;
            public int MinRounds;
            public float ReceivedAt;
        }

        /// <summary>Latest statistics from the host (the host itself fills it when sending).</summary>
        public static StatsTable LastStats { get; private set; }
        /// <summary>Autotest: Beispielzahlen fuer alle anwesenden Figuren (Freeplay-Dummies).</summary>
        public static void DiagSampleStats() {
            var t = new StatsTable { HasAverage = true, ThresholdPercent = 60, MinRounds = 5, ReceivedAt = Time.realtimeSinceStartup };
            float[] means = { 0.62f, 0.31f, 0.78f, 0.55f, 0.47f, 0.69f, 0.24f, 0.58f };
            int i = 0;
            foreach (var p in PlayerControl.AllPlayerControls.ToArray()) {
                if (p == null) continue;
                bool few = i == 5;
                int ov = i == 3 ? DeathTimeHistory.OverrideOn : i == 1 ? DeathTimeHistory.OverrideOff : DeathTimeHistory.OverrideAuto;
                bool auto = means[i % means.Length] < 0.3f && !few;
                t.Rows.Add(new StatsRow { PlayerId = p.PlayerId, Rounds = few ? 2 : 6 + i * 2, Mean = means[i % means.Length],
                                          Qualified = !few, Override = ov,
                                          Shield = ov == DeathTimeHistory.OverrideOn || (ov == DeathTimeHistory.OverrideAuto && auto) });
                i++;
            }
            var q = t.Rows.Where(r => r.Qualified).ToList();
            t.LobbyMean = q.Count > 0 ? q.Average(r => r.Mean) : 0f;
            LastStats = t;
        }

        /// <summary>New lobby: numbers of the last one are not valid any more.</summary>
        public static void ClearStats() { LastStats = null; lastStatsKey = "?"; }
        private static string lastStatsKey = "?";
        private static bool statsRequested;

        public static StatsTable TableOf(Evaluation ev) {
            var t = new StatsTable {
                HasAverage = ev.HasAverage, LobbyMean = ev.LobbyMean,
                ThresholdPercent = ev.ThresholdPercent, MinRounds = ev.MinRounds,
                ReceivedAt = Time.realtimeSinceStartup
            };
            foreach (var r in ev.Rows)
                t.Rows.Add(new StatsRow { PlayerId = r.PlayerId, Rounds = r.Stat.Rounds, Mean = r.Stat.Mean,
                                          Qualified = r.Qualified, Shield = r.Shield, Override = r.Override, Meetings = r.Meetings });
            return t;
        }

        private static string StatsKey(Evaluation ev) =>
            string.Join(";", ev.Rows.Select(r => $"{r.PlayerId}:{r.Stat.Rounds}:{Mathf.RoundToInt(r.Stat.Mean * 1000f)}:{(r.Qualified ? 1 : 0)}{(r.Shield ? 1 : 0)}{r.Override}{r.Meetings}"))
            + $"|{(ev.HasAverage ? Mathf.RoundToInt(ev.LobbyMean * 1000f) : -1)}|{ev.ThresholdPercent}|{ev.MinRounds}";

        private static void SendStats(Evaluation ev) {
            try {
                MessageWriter w = UTSRpc.Begin(RpcId);
                w.Write(SubStats);
                int n = Math.Min(ev.Rows.Count, 255);
                w.Write((byte)n);
                for (int i = 0; i < n; i++) {
                    var r = ev.Rows[i];
                    w.Write(r.PlayerId);
                    w.Write((byte)Math.Min(r.Stat.Rounds, 255));
                    w.Write((ushort)Mathf.Clamp(Mathf.RoundToInt(r.Stat.Mean * 1000f), 0, 1000));
                    w.Write((byte)((r.Qualified ? 1 : 0) | (r.Shield ? 2 : 0)
                                   | (r.Override == DeathTimeHistory.OverrideOn ? 4 : 0)
                                   | (r.Override == DeathTimeHistory.OverrideOff ? 8 : 0)
                                   | ((Mathf.Clamp(r.Meetings, 1, 3) - 1) << 4)));   // bits 4-5, older builds ignore them
                }
                w.Write(ev.HasAverage);
                w.Write((ushort)Mathf.Clamp(Mathf.RoundToInt(ev.LobbyMean * 1000f), 0, 1000));
                w.Write((byte)ev.ThresholdPercent);
                w.Write((byte)Math.Min(ev.MinRounds, 255));
                AmongUsClient.Instance.FinishRpcImmediately(w);
            } catch (Exception e) {
                UsefulTORStuffPlugin.Logger?.LogWarning($"[EarlyDeathShield] stats send failed: {e.Message}");
            }
            LastStats = TableOf(ev);
        }

        /// <summary>Viewer opened: ask the host for fresh numbers (the host answers within half a second).</summary>
        public static void RequestStats() {
            if (AmHost()) { statsRequested = true; nextTick = 0f; return; }
            try {
                MessageWriter w = UTSRpc.Begin(RpcId);
                w.Write(SubRequest);
                AmongUsClient.Instance.FinishRpcImmediately(w);
            } catch (Exception e) {
                UsefulTORStuffPlugin.Logger?.LogWarning($"[EarlyDeathShield] stats request failed: {e.Message}");
            }
        }

        private static StatsTable ReadStats(MessageReader reader) {
            var t = new StatsTable { ReceivedAt = Time.realtimeSinceStartup };
            int n = reader.ReadByte();
            for (int i = 0; i < n; i++) {
                var r = new StatsRow { PlayerId = reader.ReadByte(), Rounds = reader.ReadByte(), Mean = reader.ReadUInt16() / 1000f };
                byte f = reader.ReadByte();
                r.Qualified = (f & 1) != 0; r.Shield = (f & 2) != 0;
                r.Override = (f & 4) != 0 ? DeathTimeHistory.OverrideOn
                           : (f & 8) != 0 ? DeathTimeHistory.OverrideOff : DeathTimeHistory.OverrideAuto;
                r.Meetings = ((f >> 4) & 3) + 1;
                t.Rows.Add(r);
            }
            t.HasAverage = reader.ReadBoolean();
            t.LobbyMean = reader.ReadUInt16() / 1000f;
            t.ThresholdPercent = reader.ReadByte();
            t.MinRounds = reader.ReadByte();
            return t;
        }

        // ---- RPC ----
        private static void SendSetShields(List<(byte Id, int Meetings)> list) {
            try {
                var ids = list.Select(x => x.Id).ToList();
                MessageWriter w = UTSRpc.Begin(RpcId);
                w.Write(SubSetShields);
                w.Write((byte)Math.Min(ids.Count, 255));
                for (int i = 0; i < ids.Count && i < 255; i++) w.Write(ids[i]);
                AmongUsClient.Instance.FinishRpcImmediately(w);
                ApplySetShields(ids);

                // the meetings follow on the same reliable channel, so they always land after Sub 0
                w = UTSRpc.Begin(RpcId);
                w.Write(SubMeetings);
                w.Write((byte)Math.Min(list.Count, 255));
                for (int i = 0; i < list.Count && i < 255; i++) { w.Write(list[i].Id); w.Write((byte)list[i].Meetings); }
                AmongUsClient.Instance.FinishRpcImmediately(w);
                ApplyMeetings(list);
            } catch (Exception e) {
                UsefulTORStuffPlugin.Logger?.LogError($"[EarlyDeathShield] send failed: {e}");
            }
        }

        private static void SendClear() {
            try {
                MessageWriter w = UTSRpc.Begin(RpcId);
                w.Write(SubClear);
                AmongUsClient.Instance.FinishRpcImmediately(w);
                ApplyClear();
            } catch (Exception e) {
                UsefulTORStuffPlugin.Logger?.LogError($"[EarlyDeathShield] clear failed: {e}");
            }
        }

        private static void HandleModuleRpc(MessageReader reader) {
            try {
                byte sub = reader.ReadByte();
                switch (sub) {
                    case SubSetShields: {
                        int n = reader.ReadByte();
                        var ids = new List<byte>(n);
                        for (int i = 0; i < n; i++) ids.Add(reader.ReadByte());
                        if (UTSRpc.RequireHost("EarlyDeathShield.SetShields")) ApplySetShields(ids);
                        break;
                    }
                    case SubClear:
                        if (UTSRpc.RequireHost("EarlyDeathShield.Clear")) ApplyClear();
                        break;
                    case SubMeetings: {
                        int n = reader.ReadByte();
                        var list = new List<(byte, int)>(n);
                        for (int i = 0; i < n; i++) list.Add((reader.ReadByte(), reader.ReadByte()));
                        if (UTSRpc.RequireHost("EarlyDeathShield.Meetings")) ApplyMeetings(list);
                        break;
                    }
                    case SubStats: {
                        var table = ReadStats(reader);
                        if (UTSRpc.RequireHost("EarlyDeathShield.Stats")) LastStats = table;
                        break;
                    }
                    case SubRequest:
                        if (AmHost()) { statsRequested = true; nextTick = 0f; }
                        break;
                }
            } catch (Exception e) {
                UsefulTORStuffPlugin.Logger?.LogError($"[EarlyDeathShield] rpc failed: {e}");
            }
        }

        private static void ApplySetShields(List<byte> ids) {
            shielded.Clear();
            lastsUntil.Clear();      // every shield lasts one meeting until SubMeetings says more
            foreach (byte id in ids) shielded.Add(id);
            if (shielded.Count > 0)
                UsefulTORStuffPlugin.Logger?.LogInfo(
                    $"[EarlyDeathShield] {shielded.Count} player(s) shielded.");
        }

        private static void ApplyMeetings(List<(byte Id, int Meetings)> list) {
            foreach (var (id, m) in list) lastsUntil[id] = Mathf.Clamp(m, 1, DeathTimeHistory.MaxForcedMeetings);
            var longer = list.Where(x => x.Meetings > 1).ToList();
            if (longer.Count > 0)
                UsefulTORStuffPlugin.Logger?.LogInfo(
                    $"[EarlyDeathShield] longer shields: {string.Join(", ", longer.Select(x => $"{x.Id} until meeting {x.Meetings}"))}.");
        }

        private static void ApplyClear() {
            lastsUntil.Clear();
            if (shielded.Count == 0) return;
            shielded.Clear();
            UsefulTORStuffPlugin.Logger?.LogInfo("[EarlyDeathShield] shield list cleared by the host.");
        }

        // Drops the shields that run out at the current meeting count (every client on its own; the
        // host also tells everyone once the list is empty, as before).
        private static void DropExpired(bool meetingRunning) {
            if (shielded.Count == 0) return;
            // Ends before the meeting: gone the moment meeting N opens. Kept through it: gone once
            // meeting N is over.
            var gone = shielded.Where(id => meetingsSeen >= LastsUntil(id) && (!SurvivesMeeting || !meetingRunning)).ToList();
            if (gone.Count == 0) return;
            foreach (byte id in gone) { shielded.Remove(id); lastsUntil.Remove(id); }
            UsefulTORStuffPlugin.Logger?.LogInfo($"[EarlyDeathShield] shield ended for {gone.Count} player(s) after meeting {meetingsSeen}, {shielded.Count} left.");
            if (shielded.Count == 0 && AmHost()) SendClear();
        }

        // ====================================================================
        // The driver, called from EarlyDeathShieldUI.Update (never a Harmony postfix - see the
        // NewcomerShield header for why).
        // ====================================================================
        private static float nextTick;
        private static bool roundSeen;
        private static float roundSeenAt;
        private static bool assignedThisRound;
        private static bool introOverSeen;
        private static int meetingsSeen;      // meetings opened in this round
        private const float IntroFallbackSeconds = 10f;

        public static void Tick() {
            try {
                if (Time.realtimeSinceStartup < nextTick) return;
                nextTick = Time.realtimeSinceStartup + 0.5f;

                var client = AmongUsClient.Instance;
                if (client == null) return;

                bool inRound = ShipStatus.Instance != null && client.IsGameStarted;
                if (!inRound) {
                    roundSeen = false;
                    introOverSeen = false;
                    assignedThisRound = false;
                    // LobbyScreen.Exists, never GameStartManager.Instance (LobbyLeakGuard.cs).
                    if (client.AmHost && LobbyScreen.Exists) LobbyPreviewTick();
                    return;
                }

                if (!roundSeen) { roundSeen = true; roundSeenAt = Time.realtimeSinceStartup; }

                // Same two lifetimes as the newcomer shield (see its Tick for the long version), counted
                // per player: a forced shield may run up to the 2nd or 3rd meeting.
                bool meetingRunning = MeetingHud.Instance != null || ExileController.Instance != null;
                DropExpired(meetingRunning);

                if (assignedThisRound || !client.AmHost) return;
                if (!IsEnabled()) { assignedThisRound = true; return; }
                if (!introOverSeen
                    && Time.realtimeSinceStartup - roundSeenAt < IntroFallbackSeconds) return;

                assignedThisRound = true;
                AssignShields();
            } catch (Exception e) {
                UsefulTORStuffPlugin.Logger?.LogError($"[EarlyDeathShield] tick failed: {e}");
            }
        }

        [HarmonyPatch(typeof(IntroCutscene), nameof(IntroCutscene.OnDestroy))]
        static class IntroOverMarkerPatch {
            [HarmonyPriority(Priority.First)]
            public static void Prefix() => introOverSeen = true;
        }

        private static void AssignShields() {
            try {
                // No shields in Hide'n'Seek or Prop Hunt (audit 04.10.): no meeting ever ends them there.
                if (AntiStartKill.ShieldFreeMode()) return;
                var ev = Evaluate();
                var list = new List<(byte Id, int Meetings)>();
                foreach (var r in ev.Rows) {
                    if (!r.Shield) continue;
                    var p = Helpers.playerById(r.PlayerId);
                    if (p == null || p.Data == null || p.Data.IsDead || p.Data.Disconnected) continue;
                    list.Add((r.PlayerId, r.Meetings));
                }
                var ids = list.Select(x => x.Id).ToList();

                // Always logged: the proof the decision ran, and the numbers behind it.
                UsefulTORStuffPlugin.Logger?.LogInfo(
                    $"[EarlyDeathShield] round start: {ev.Rows.Count(r => r.Qualified)} qualified player(s)"
                    + (ev.HasAverage ? $", lobby average {ev.LobbyMean * 100f:F0}% survived, limit {ev.ThresholdPercent}%" : ", no average yet")
                    + $", {ids.Count} to shield"
                    + (ids.Count > 0 ? ": " + string.Join(", ", ev.Rows.Where(r => ids.Contains(r.PlayerId))
                          .Select(r => $"{r.Name} ({r.Stat.Mean * 100f:F0}%, {r.Stat.Rounds} rounds"
                                       + (r.Override == DeathTimeHistory.OverrideOn ? $", forced until meeting {r.Meetings}" : "") + ")")) : "")
                    + ".");

                if (list.Count > 0) SendSetShields(list);
            } catch (Exception e) {
                UsefulTORStuffPlugin.Logger?.LogError($"[EarlyDeathShield] round start failed: {e}");
            }
        }

        [HarmonyPatch(typeof(MeetingHud), nameof(MeetingHud.Start))]
        static class MeetingStartPatch {
            public static void Postfix() {
                try {
                    meetingsSeen++;
                    DropExpired(true);
                } catch { }
            }
        }

        // ====================================================================
        // Lobby preview: broadcast whenever the list changes (join, leave, option, override).
        // ====================================================================
        private static string lastPreviewKey = "";

        // Forces the next preview tick to resend even an unchanged list - after an override click
        // the host wants to see the outline change at once, not half a second later.
        public static void RefreshPreview() { lastPreviewKey = "?"; lastStatsKey = "?"; nextTick = 0f; }

        private static void LobbyPreviewTick() {
            try {
                var ids = new List<(byte Id, int Meetings)>();
                if (IsEnabled()) {
                    var ev = Evaluate();
                    foreach (var r in ev.Rows)
                        if (r.Shield) ids.Add((r.PlayerId, r.Meetings));
                    // statistics for everyone's viewer: on change, and right away when somebody asks
                    string sk = StatsKey(ev);
                    if (sk != lastStatsKey || statsRequested) {
                        lastStatsKey = sk;
                        statsRequested = false;
                        SendStats(ev);
                    }
                }
                ids.Sort();
                // The player count is part of the key: a newcomer who is not shielded himself does not
                // change the list, but he cleared his own copy on join and needs it sent once.
                string key = $"{PlayerControl.AllPlayerControls.Count}|"
                    + string.Join(",", ids.Select(x => $"{x.Id}:{x.Meetings}"));
                if (key == lastPreviewKey) return;
                lastPreviewKey = key;

                if (ids.Count > 0) SendSetShields(ids);
                else SendClear();
            } catch { }
        }

        // ====================================================================
        // Enforcement 1: vanilla kills, host-authoritative
        // ====================================================================
        [HarmonyPatch(typeof(PlayerControl), nameof(PlayerControl.CheckMurder))]
        static class CheckMurderPatch {
            public static bool Prefix(PlayerControl __instance, [HarmonyArgument(0)] PlayerControl target) {
                try {
                    if (shielded.Count == 0 || target == null) return true;
                    if (AntiStartKill.ShieldFreeMode()) return true;
                    if (!shielded.Contains(target.PlayerId)) return true;
                    UsefulTORStuffPlugin.Logger?.LogInfo(
                        $"[EarlyDeathShield] blocked a vanilla kill on {target.Data?.PlayerName} (shielded).");
                    return false;
                } catch { return true; }
            }
        }

        // ====================================================================
        // Enforcement 2: every TOR/UC role kill, on the killer's own client. Postfix, and only a
        // real kill result is rewritten (AntiStartKill's refinement): SuppressKill is refused
        // already, and a BlankKill kills nobody.
        // ====================================================================
        // (The "tell the killer why" line, option 1384, was removed on 2026-10-04 with AntiStartKill's.)
        [HarmonyPatch(typeof(Helpers), nameof(Helpers.checkMuderAttempt))]
        static class CheckMurderAttemptPatch {
            public static void Postfix(PlayerControl killer, PlayerControl target,
                                       ref MurderAttemptResult __result) {
                try {
                    if (shielded.Count == 0 || target == null) return;
                    if (AntiStartKill.ShieldFreeMode()) return;
                    // The Bomber's self-probe checkMuderAttempt(bomber, bomber) is no kill (04.10.).
                    if (killer != null && killer.PlayerId == target.PlayerId) return;
                    if (!shielded.Contains(target.PlayerId)) return;
                    if (__result != MurderAttemptResult.PerformKill
                        && __result != MurderAttemptResult.DelayVampireKill) return;

                    __result = MurderAttemptResult.SuppressKill;
                    UsefulTORStuffPlugin.Logger?.LogInfo(
                        $"[EarlyDeathShield] blocked a role kill on {target.Data?.PlayerName} (shielded).");
                } catch { }
            }
        }

        // ====================================================================
        // Enforcement 0: a shielded player cannot even be TARGETED (the Thief rationale in
        // NewcomerShield.cs), except by peaceful abilities and by a Jackal who can still recruit.
        // ====================================================================
        [HarmonyPatch(typeof(TheOtherRoles.Patches.PlayerControlFixedUpdatePatch),
                      nameof(TheOtherRoles.Patches.PlayerControlFixedUpdatePatch.setTarget))]
        static class SetTargetPatch {
            public static void Prefix(ref List<PlayerControl> untargetablePlayers) {
                try {
                    if (shielded.Count == 0) return;
                    if (AntiStartKill.ShieldFreeMode()) return;
                    if (ShieldPeaceGate.Peaceful) return;
                    if (NewcomerShield.JackalCanRecruitNow()) return;   // his KILL button is guarded in NewcomerShield
                    var list = untargetablePlayers != null
                        ? new List<PlayerControl>(untargetablePlayers) : new List<PlayerControl>();
                    foreach (byte id in shielded) {
                        var p = Helpers.playerById(id);
                        if (p != null && !list.Contains(p)) list.Add(p);
                    }
                    untargetablePlayers = list;
                } catch { }
            }
        }

        // ====================================================================
        // Resets
        // ====================================================================
        [HarmonyPatch(typeof(RPCProcedure), nameof(RPCProcedure.resetVariables))]
        static class ResetPatch {
            public static void Postfix() {
                shielded.Clear();
                lastsUntil.Clear();
                meetingsSeen = 0;
                lastPreviewKey = "";
            }
        }

        [HarmonyPatch(typeof(AmongUsClient), nameof(AmongUsClient.OnGameJoined))]
        static class LobbyResetPatch {
            // Player ids are per connection; the overrides live on in the history file by code.
            public static void Postfix() {
                shielded.Clear();
                lastsUntil.Clear();
                meetingsSeen = 0;
                lastPreviewKey = "";
            }
        }
    }
}
