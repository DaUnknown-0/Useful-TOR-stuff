// Useful TOR Stuff - Copyright (C) 2026 DaUnknown-0
// Licensed under GPL-3.0-or-later. See LICENSE for details.
// Based on The Other Roles (https://github.com/TheOtherRolesAU/TheOtherRoles), GPL-3.0.

/*
 * ConnectionLight - a traffic light per player for the host (User 2026-11-11): who has a shaky connection BEFORE
 * he drops out of a round. Background: on 2026-10-16 one player kept getting disconnected by the server at data
 * bursts, and nobody saw it coming; the log only showed the gap afterwards (LeaveDiag).
 *
 * What the host can measure. Among Us relays everything through the server, so the host never sees another player's
 * ping. He can time a round trip himself though:
 *     host -> everyone   [240][229][0][seq]      every 5 s while in a lobby or a round
 *     client -> host     [240][229][1][seq]      the answer, addressed to the host only
 * The time between the two is host-to-server plus server-to-client, both ways. That is about the host's own ping
 * plus the player's, so the limits below are roughly twice what one would call a bad ping.
 * Per player the host keeps the last 24 answers (two minutes): the middle value, the worst one, and how many pings
 * were never answered. Added to that: how often this player (PlayerIdentity, so a rejoin counts as the same person)
 * left tonight for a reason other than leaving on his own.
 *
 *   red     2 of the last 6 pings unanswered, or a middle value from 700 ms, or dropped twice or more tonight
 *   yellow  any ping of the window unanswered, or a middle value from 350 ms, or a worst value from 900 ms, or
 *           dropped once tonight
 *   green   none of that
 *   grey    never answered at all: a client with an older version of this mod, which does not know the ping
 *
 * The measuring lives here because every client has to answer the ping. The host's panel (lobby menu entry
 * "Connections") is part of TOR - Host Tools and reads Rows(). Nothing here changes the game; a player whose
 * answers stop is not kicked.
 */

using System;
using System.Collections.Generic;
using System.Linq;
using HarmonyLib;
using Hazel;
using UnityEngine;

namespace UsefulTORStuff {

    public static class ConnectionLight {
        internal const byte ModuleId = 229;
        private const byte SubPing = 0, SubPong = 1;
        private const float PingEvery = 5f, LostAfter = 4f;
        private const int Window = 24, Recent = 6;

        public enum Level { Grey, Green, Yellow, Red }

        internal sealed class Stat {
            public readonly List<float> Samples = new List<float>();   // milliseconds, -1 = never answered
            public bool EverAnswered;
            public int Pings;
        }

        private static readonly Dictionary<int, Stat> stats = new Dictionary<int, Stat>();          // by client id
        private static readonly Dictionary<string, int> drops = new Dictionary<string, int>();      // by player identity
        private static readonly HashSet<int> waiting = new HashSet<int>();
        private static byte seq;
        private static float sentAt = -1f, nextPing;
        private static bool registered;

        internal static void RegisterRpc() {
            if (registered) return;
            registered = true;
            UTSRpc.Register(ModuleId, Handle);
        }

        private static bool AmHost() {
            try {
                var c = AmongUsClient.Instance;
                return c != null && c.AmHost && c.NetworkMode != NetworkModes.FreePlay;
            } catch { return false; }
        }

        // ====================================================================
        // Net
        // ====================================================================
        private static void Handle(MessageReader reader) {
            try {
                byte sub = reader.ReadByte();
                byte s = reader.ReadByte();
                var sender = UTSRpc.Sender;
                if (sub == SubPing) {
                    if (!UTSRpc.RequireHost("ConnectionLight.Ping") || !UTSRpc.CanSend) return;
                    var c = AmongUsClient.Instance;
                    if (c.AmHost) return;
                    MessageWriter w = c.StartRpcImmediately(PlayerControl.LocalPlayer.NetId, UTSRpc.CallId, SendOption.Reliable, c.HostId);
                    w.Write(ModuleId);
                    w.Write(SubPong);
                    w.Write(s);
                    c.FinishRpcImmediately(w);
                } else if (sub == SubPong) {
                    if (!AmHost() || sender == null || s != seq || sentAt < 0f) return;
                    int id = UnknownsNet.SenderClientId(sender);
                    if (!waiting.Remove(id)) return;                 // answered already, or was not asked
                    Add(id, (Time.realtimeSinceStartup - sentAt) * 1000f);
                }
            } catch (Exception e) {
                UsefulTORStuffPlugin.Logger?.LogWarning($"[ConnectionLight] rpc failed: {e.Message}");
            }
        }

        private static void Add(int clientId, float ms) {
            if (!stats.TryGetValue(clientId, out var st)) stats[clientId] = st = new Stat();
            st.Samples.Add(ms);
            if (ms >= 0f) st.EverAnswered = true;
            if (st.Samples.Count > Window) st.Samples.RemoveAt(0);
        }

        [HarmonyPatch(typeof(HudManager), nameof(HudManager.Update))]
        private static class TickPatch {
            public static void Postfix() {
                try {
                    float now = Time.realtimeSinceStartup;
                    if (now < nextPing && (sentAt < 0f || waiting.Count == 0 || now - sentAt < LostAfter)) return;
                    if (!AmHost() || !UTSRpc.CanSend) { nextPing = now + PingEvery; waiting.Clear(); sentAt = -1f; return; }
                    // whoever has not answered the last ping within the limit: that one counts as lost
                    if (sentAt >= 0f && waiting.Count > 0 && now - sentAt >= LostAfter) {
                        foreach (int id in waiting) Add(id, -1f);
                        waiting.Clear();
                    }
                    if (now < nextPing) return;
                    nextPing = now + PingEvery;
                    var c = AmongUsClient.Instance;
                    waiting.Clear();
                    for (int i = 0; i < c.allClients.Count; i++) {
                        var cl = c.allClients[i];
                        if (cl == null || cl.Id == c.ClientId || cl.Character == null) continue;
                        waiting.Add(cl.Id);
                        if (!stats.TryGetValue(cl.Id, out var st)) stats[cl.Id] = st = new Stat();
                        st.Pings++;
                    }
                    if (waiting.Count == 0) { sentAt = -1f; return; }
                    seq++;
                    sentAt = now;
                    MessageWriter w = UTSRpc.Begin(ModuleId);
                    w.Write(SubPing);
                    w.Write(seq);
                    c.FinishRpcImmediately(w);
                } catch (Exception e) {
                    nextPing = Time.realtimeSinceStartup + PingEvery;
                    UsefulTORStuffPlugin.Logger?.LogWarning($"[ConnectionLight] tick failed: {e.Message}");
                }
            }
        }

        [HarmonyPatch(typeof(AmongUsClient), nameof(AmongUsClient.OnGameJoined))]
        private static class JoinedPatch {
            public static void Postfix() { stats.Clear(); waiting.Clear(); sentAt = -1f; }
        }

        // A drop is a leave the player did not choose. Counted per person for the evening: after the rejoin the
        // client id is a new one, the identity is the same.
        [HarmonyPatch(typeof(GameData), nameof(GameData.HandleDisconnect), new[] { typeof(PlayerControl), typeof(DisconnectReasons) })]
        private static class DisconnectPatch {
            public static void Postfix(PlayerControl player, DisconnectReasons reason) {
                try {
                    if (player == null) return;
                    int id = player.OwnerId;
                    stats.Remove(id);
                    waiting.Remove(id);
                    if (!AmHost() || reason == DisconnectReasons.ExitGame) return;
                    string key = KeyOf(player);
                    if (key == null) return;
                    drops[key] = (drops.TryGetValue(key, out int n) ? n : 0) + 1;
                    UsefulTORStuffPlugin.Logger?.LogInfo($"[ConnectionLight] {player.Data?.PlayerName ?? "?"} dropped ({reason}), {drops[key]} time(s) tonight.");
                } catch { }
            }
        }

        private static string KeyOf(PlayerControl p) {
            try {
                string k = PlayerIdentity.KeyOf(p);
                if (!string.IsNullOrEmpty(k)) return k;
                return p.Data != null && !string.IsNullOrEmpty(p.Data.PlayerName) ? "name:" + p.Data.PlayerName : null;
            } catch { return null; }
        }

        // ====================================================================
        // Verdict
        // ====================================================================
        public sealed class Row {
            public string Name;
            public Level Level;
            public int Median = -1, Worst = -1, Lost, Asked, Drops;
        }

        private static Row Judge(InnerNet.ClientData cl) {
            var r = new Row { Name = "?" };
            try { r.Name = cl.Character.Data != null ? cl.Character.Data.PlayerName : cl.PlayerName ?? "?"; } catch { }
            string key = KeyOf(cl.Character);
            if (key != null) drops.TryGetValue(key, out r.Drops);
            stats.TryGetValue(cl.Id, out var st);
            if (st != null) {
                var ok = st.Samples.Where(x => x >= 0f).OrderBy(x => x).ToList();
                r.Asked = st.Samples.Count;
                r.Lost = st.Samples.Count - ok.Count;
                if (ok.Count > 0) { r.Median = Mathf.RoundToInt(ok[ok.Count / 2]); r.Worst = Mathf.RoundToInt(ok[ok.Count - 1]); }
            }
            if (st == null || !st.EverAnswered) {
                // no answer ever: an older version that does not know the ping. Drops still count.
                r.Level = r.Drops >= 2 ? Level.Red : r.Drops == 1 ? Level.Yellow : Level.Grey;
                return r;
            }
            int lostRecent = st.Samples.Skip(Math.Max(0, st.Samples.Count - Recent)).Count(x => x < 0f);
            if (lostRecent >= 2 || r.Median >= 700 || r.Drops >= 2) r.Level = Level.Red;
            else if (r.Lost >= 1 || r.Median >= 350 || r.Worst >= 900 || r.Drops == 1) r.Level = Level.Yellow;
            else r.Level = Level.Green;
            return r;
        }

        /// <summary>Every other player with his light, the worst first.</summary>
        public static List<Row> Rows() {
            var list = new List<Row>();
            try {
                var c = AmongUsClient.Instance;
                if (c == null || c.allClients == null) return list;
                for (int i = 0; i < c.allClients.Count; i++) {
                    var cl = c.allClients[i];
                    if (cl == null || cl.Id == c.ClientId || cl.Character == null) continue;
                    list.Add(Judge(cl));
                }
            } catch { }
            return list.OrderByDescending(r => r.Level == Level.Red ? 3 : r.Level == Level.Yellow ? 2 : r.Level == Level.Grey ? 1 : 0)
                       .ThenBy(r => r.Name, StringComparer.OrdinalIgnoreCase).ToList();
        }

        public static Color ColorOf(Level l) =>
            l == Level.Red ? VanillaUI.Red : l == Level.Yellow ? VanillaUI.Yellow : l == Level.Green ? VanillaUI.Green : VanillaUI.Grey;
    }
}
