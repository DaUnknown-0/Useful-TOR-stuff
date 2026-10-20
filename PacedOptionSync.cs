// Useful TOR Stuff - Copyright (C) 2026 DaUnknown-0
// Licensed under GPL-3.0-or-later. See LICENSE for details.
// Based on The Other Roles (https://github.com/TheOtherRolesAU/TheOtherRoles), GPL-3.0.

/*
 * PacedOptionSync - the host sends the mod options once per wave of joins, spread over a moment,
 * instead of once per joining player all at once.
 *
 * TOR's AmongUsClientOnPlayerJoinedPatch (CustomOptions.cs:946) runs on every PlayerPhysics.CoSpawnPlayer
 * on the host and calls LogicOptions.SyncOptions() plus CustomOption.ShareOptionSelections(), which sends
 * ALL ~700 options of TOR and the mods (chunks of 200, every chunk a reliable broadcast) in the same frame.
 * When a finished round returns 13 players to the lobby, that is 13 full dumps within a few seconds, about
 * 65 large reliable messages to every client. A player with a weak connection was dropped by the server
 * at exactly that moment (2026-10-16, "You disconnected from the server" right after the lobby rejoin).
 *
 * Here TOR's postfix is taken off CoSpawnPlayer (Harmony unpatch at load, so .NET tiering cannot bring it
 * back) and replaced: every spawn only marks a sync as wanted; the sync goes out once no further player
 * has appeared for 1 s (at most 4 s after the first), and its chunks leave 0.15 s apart. Pressing Start
 * flushes anything pending first, so nobody starts with stale options. The values are read at send time,
 * so a change made in between is never overwritten by an older one.
 *
 * Host only, nothing changes for anyone else; switch: [Network] PacedOptionSync in the config.
 *
 * SendPaced (2026-10-20, settings presets): sends only the given options through the same paced chunks, with or
 * without the per-spawn replacement above; a full sync that starts meanwhile simply takes them along.
 */

using System;
using System.Collections.Generic;
using System.Linq;
using BepInEx.Configuration;
using HarmonyLib;
using Hazel;
using TheOtherRoles;
using UnityEngine;

namespace UsefulTORStuff {
    internal static class PacedOptionSync {
        private const float Quiet = 1.0f, MaxWait = 4.0f, ChunkGap = 0.15f;
        private const int ChunkSize = 200;   // same as TOR: well under the 1200-byte packet limit

        private static ConfigEntry<bool> Enabled;
        private static bool installed;
        private static bool pending;
        private static int joins;
        private static float firstAt, lastAt, nextChunk;
        private static List<CustomOption> queue;
        private static int chunksSent, optionsSent;

        private static void Log(string s) => UsefulTORStuffPlugin.Logger?.LogInfo("[PacedOptionSync] " + s);

        internal static void Bind(ConfigFile config) {
            Enabled = config.Bind("Network", "PacedOptionSync", true,
                "As host, send the mod options once per wave of joining players and spread over a moment, instead of "
                + "all options again for every single player at once (helps players with weak connections; only the host's setting counts).");
        }

        /// Called once after all plugins patched: takes TOR's per-spawn sync off and enables ours.
        internal static void Install() {
            try {
                if (Enabled == null || !Enabled.Value) { Log("off by config - TOR syncs on every spawn as before."); return; }
                var original = AccessTools.Method(typeof(PlayerPhysics), nameof(PlayerPhysics.CoSpawnPlayer));
                var info = original != null ? Harmony.GetPatchInfo(original) : null;
                var tor = info?.Postfixes.FirstOrDefault(p => p.PatchMethod?.DeclaringType?.Name == "AmongUsClientOnPlayerJoinedPatch");
                if (tor == null) { Log("TOR's per-spawn sync not found - nothing replaced."); return; }
                if (!ResolveRpcId()) { Log("TOR's ShareOptions RPC id not found - nothing replaced."); return; }
                new Harmony(tor.owner).Unpatch(original, tor.PatchMethod);
                installed = true;
                Log("TOR's per-spawn option sync replaced by a bundled, paced one.");
            } catch (Exception e) {
                UsefulTORStuffPlugin.Logger?.LogError($"[PacedOptionSync] install failed, TOR's sync stays: {e.Message}");
            }
        }

        // TOR's CustomRPC enum is internal: its ShareOptions value is read once by name.
        private static byte shareOptionsId;

        private static bool ResolveRpcId() {
            try {
                var t = TorType.Get("TheOtherRoles.CustomRPC");
                if (t == null) return false;
                shareOptionsId = Convert.ToByte(Enum.Parse(t, "ShareOptions"));
                return true;
            } catch { return false; }
        }

        private static bool CanSend() {
            try {
                return AmongUsClient.Instance != null && AmongUsClient.Instance.AmHost
                       && PlayerControl.LocalPlayer != null && PlayerControl.AllPlayerControls.Count > 1;
            } catch { return false; }
        }

        [HarmonyPatch(typeof(PlayerPhysics), nameof(PlayerPhysics.CoSpawnPlayer))]
        static class SpawnPatch {
            public static void Postfix() {
                if (!installed) return;
                try {
                    if (AmongUsClient.Instance == null || !AmongUsClient.Instance.AmHost || PlayerControl.LocalPlayer == null) return;
                    float now = Time.realtimeSinceStartup;
                    if (!pending) { firstAt = now; joins = 0; }
                    pending = true;
                    joins++;
                    lastAt = now;
                } catch { }
            }
        }

        /// <summary>Host: sends just these options (a loaded preset's changes), 200 per packet, 0.15 s apart.</summary>
        internal static int SendPaced(IEnumerable<CustomOption> options) {
            var list = options?.Where(o => o != null && o.id != 0).Distinct().ToList() ?? new List<CustomOption>();
            if (list.Count == 0 || !CanSend()) return 0;
            if (shareOptionsId == 0 && !ResolveRpcId()) {
                try { CustomOption.ShareOptionSelections(); } catch { }   // TOR's own path as the fallback
                return 1;
            }
            if (queue == null) { queue = new List<CustomOption>(); chunksSent = optionsSent = 0; nextChunk = 0f; }
            foreach (var o in list) if (!queue.Contains(o)) queue.Add(o);
            int packets = (queue.Count + ChunkSize - 1) / ChunkSize;
            Log($"{list.Count} changed option(s) queued, {packets} packet(s).");
            return packets;
        }

        /// <summary>True while option packets are still waiting to go out.</summary>
        internal static bool Sending => queue != null;

        [HarmonyPatch(typeof(HudManager), nameof(HudManager.Update))]
        static class TickPatch {
            public static void Postfix() {
                if (!installed && queue == null) return;
                try {
                    float now = Time.realtimeSinceStartup;
                    if (pending && (now - lastAt >= Quiet || now - firstAt >= MaxWait)) StartSync();
                    if (queue != null && now >= nextChunk) { SendChunk(); nextChunk = now + ChunkGap; }
                } catch (Exception e) {
                    queue = null;
                    UsefulTORStuffPlugin.Logger?.LogWarning($"[PacedOptionSync] send failed: {e.Message}");
                }
            }
        }

        // Start must never go out with options still waiting.
        [HarmonyPatch(typeof(GameStartManager), nameof(GameStartManager.BeginGame))]
        static class StartFlushPatch {
            [HarmonyPriority(Priority.First)]
            public static void Prefix() {
                if (!installed && queue == null) return;
                try {
                    if (pending) StartSync();
                    while (queue != null) SendChunk();
                } catch { queue = null; }
            }
        }

        private static void StartSync() {
            pending = false;
            if (!CanSend()) { queue = null; return; }
            try { GameManager.Instance.LogicOptions.SyncOptions(); } catch { }
            queue = new List<CustomOption>(CustomOption.options);
            chunksSent = optionsSent = 0;
            nextChunk = 0f;
            Log($"{joins} spawn(s) bundled into one sync of {queue.Count} option(s).");
        }

        private static void SendChunk() {
            if (queue == null) return;
            if (queue.Count == 0 || !CanSend()) { queue = null; return; }
            int amount = Math.Min(queue.Count, ChunkSize);
            var writer = AmongUsClient.Instance.StartRpcImmediately(PlayerControl.LocalPlayer.NetId, shareOptionsId, SendOption.Reliable, -1);
            writer.Write((byte)amount);
            for (int i = 0; i < amount; i++) {
                var option = queue[i];
                writer.WritePacked((uint)option.id);
                writer.WritePacked(Convert.ToUInt32(option.selection));   // read now, not when the join happened
            }
            AmongUsClient.Instance.FinishRpcImmediately(writer);
            queue.RemoveRange(0, amount);
            chunksSent++;
            optionsSent += amount;
            if (queue.Count == 0) {
                queue = null;
                Log($"sent {optionsSent} option(s) in {chunksSent} packet(s).");
            }
        }
    }
}
