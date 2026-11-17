// Useful TOR Stuff - Copyright (C) 2026 DaUnknown-0
// Licensed under GPL-3.0-or-later. See LICENSE for details.
// Based on The Other Roles (https://github.com/TheOtherRolesAU/TheOtherRoles), GPL-3.0.

// Audit 16.11. UTS Teil 2 / TOR-026:
// Vollständige Optionsübertragungen teilen dieselbe nach Besitzern getrennte Queue.
// Join-Wellen werden nach 1 s Ruhe, spätestens nach 4 s zusammengefasst.
// Höchstens 200 Optionen und 900 Nutzbytes pro Paket, mindestens 0,15 s Abstand.
// BeginGame wartet auf die Queue, einschließlich eines beim Start gewählten Kartenpresets.

using System;
using System.Collections.Generic;
using System.Linq;
using BepInEx.Configuration;
using HarmonyLib;
using Hazel;
using TheOtherRoles;
using UnityEngine;

namespace UsefulTORStuff {
    public static class PacedOptionSync {
        private const float Quiet = 1.0f, MaxWait = 4.0f, ChunkGap = 0.15f;
        private const int ChunkSize = 200;

        private static ConfigEntry<bool> Enabled;
        private static bool installed;
        private static bool pending;
        private static int joins;
        private static float firstAt, lastAt, nextChunk;
        private static List<CustomOption> queue;
        private static int chunksSent, optionsSent;
        private static GameStartManager deferredStart;
        internal static bool StartDeferred => deferredStart != null;
        private static int queueGame = int.MinValue, queueHost = int.MinValue;

        // Audit 16.11. UTS Teil 2 / TOR-026: Ein Transportweg für jeden vollständigen Dump.
        internal static void QueueFullSync() {
            if (!CanSend()) return;
            EnsureLobby();
            if (shareOptionsId == 0 && !ResolveRpcId()) throw new InvalidOperationException("ShareOptions fehlt");
            queue = CustomOption.options.Where(o => o != null).ToList();
            GroupQueue();
        }

        private static void EnsureLobby() {
            var client = AmongUsClient.Instance;
            if (client != null && queueGame == client.GameId && queueHost == client.HostId) return;
            ResetQueue();
            if (client == null) return;
            queueGame = client.GameId;
            queueHost = client.HostId;
        }

        private static void ResetQueue() {
            pending = false;
            queue = null;
            deferredStart = null;
            chunksSent = optionsSent = joins = 0;
            nextChunk = 0f;
        }

        [HarmonyPatch(typeof(AmongUsClient), nameof(AmongUsClient.OnGameJoined))]
        private static class JoinedPatch {
            public static void Postfix() { ResetQueue(); queueGame = queueHost = int.MinValue; }
        }

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
                    EnsureLobby();
                    float now = Time.realtimeSinceStartup;
                    if (!pending) { firstAt = now; joins = 0; }
                    pending = true;
                    joins++;
                    lastAt = now;
                } catch { }
            }
        }

        /// <summary>Host: sends just these options (a loaded preset's changes), 200 per packet, 0.15 s apart.</summary>
        public static int SendPaced(IEnumerable<CustomOption> options) {
            var list = options?.Where(o => o != null && o.id != 0).Distinct().ToList() ?? new List<CustomOption>();
            if (list.Count == 0 || !CanSend()) return 0;
            EnsureLobby();
            if (shareOptionsId == 0 && !ResolveRpcId()) return 0;
            if (queue == null) { queue = new List<CustomOption>(); chunksSent = optionsSent = 0; nextChunk = Math.Max(nextChunk, Time.realtimeSinceStartup); }
            foreach (var o in list) if (!queue.Contains(o)) queue.Add(o);
            GroupQueue();
            int packets = CountPackets();
            Log($"{list.Count} changed option(s) queued, {packets} packet(s).");
            return packets;
        }

        /// <summary>True while option packets are still waiting to go out.</summary>
        internal static bool Sending => queue != null;

        [HarmonyPatch(typeof(HudManager), nameof(HudManager.Update))]
        static class TickPatch {
            public static void Postfix() {
                if (!installed && queue == null && deferredStart == null) return;
                try {
                    EnsureLobby();
                    if (!CanSend()) { ResetQueue(); return; }
                    float now = Time.realtimeSinceStartup;
                    if (pending && (now - lastAt >= Quiet || now - firstAt >= MaxWait)) StartSync();
                    if (queue != null && now >= nextChunk) { SendChunk(); nextChunk = now + ChunkGap; }
                    if (deferredStart != null && queue == null && !pending && now >= nextChunk) {
                        var start = deferredStart;
                        // BeginGame prüft alle Sperren erneut. Die Kartenwahl bleibt bis dahin stabil.
                        start.BeginGame();
                    }
                } catch (Exception e) {
                    ResetQueue();
                    UsefulTORStuffPlugin.Logger?.LogWarning($"[PacedOptionSync] send failed: {e.Message}");
                }
            }
        }

        [HarmonyPatch(typeof(GameStartManager), nameof(GameStartManager.ResetStartState))]
        private static class CancelStartPatch {
            public static void Postfix() => deferredStart = null;
        }

        // Audit 16.11. UTS Teil 2: Kein synchroner Queue-Flush beim Start.
        // HarmonyX führt auch bei abgelehntem Original weitere Prefixe aus. Deshalb erst nach
        // TORs Karten-/Presetwahl prüfen und nur einen sonst erlaubten Start erneut aufrufen.
        [HarmonyPatch(typeof(GameStartManager), nameof(GameStartManager.BeginGame))]
        static class StartFlushPatch {
            [HarmonyPriority(Priority.Last)]
            public static bool Prefix(GameStartManager __instance, bool __runOriginal) {
                if (!__runOriginal) { deferredStart = null; return false; }
                EnsureLobby();
                if (pending) StartSync();
                if (queue == null) { deferredStart = null; return true; }
                deferredStart = __instance;
                return false;
            }
        }

        private static void StartSync() {
            pending = false;
            if (!CanSend()) { queue = null; return; }
            try { GameManager.Instance.LogicOptions.SyncOptions(); } catch { }
            QueueFullSync();
            chunksSent = optionsSent = 0;
            nextChunk = Math.Max(nextChunk, Time.realtimeSinceStartup);
            Log($"{joins} spawn(s) bundled into one sync of {queue.Count} option(s).");
        }

        // Same order as OptionSyncFix's grouped send: TOR's own options first, then one run per mod. Stable, so the
        // order within a group stays TOR's.
        private static void GroupQueue() {
            if (queue == null || queue.Count < 2) return;
            queue = queue.OrderBy(o => OptionSyncFix.OwnerGroup(o)).ToList();
        }

        private static int CountPackets() {
            int packets = 0;
            for (int offset = 0; offset < queue.Count; packets++) offset += ChunkLength(offset);
            return packets;
        }

        private static int ChunkLength(int offset) {
            int group = OptionSyncFix.OwnerGroup(queue[offset]);
            int amount = 0, bytes = 1;
            while (offset + amount < queue.Count && amount < ChunkSize
                   && OptionSyncFix.OwnerGroup(queue[offset + amount]) == group) {
                var item = queue[offset + amount];
                int size = AuditFixB_OptionCodec.PackedSize((uint)item.id)
                    + AuditFixB_OptionCodec.PackedSize((uint)DraftLawyerConflict.SelectionForSync(item));
                if (bytes + size > 900) break;
                bytes += size;
                amount++;
            }
            return amount;
        }

        private static void SendChunk() {
            if (queue == null) return;
            if (queue.Count == 0 || !CanSend()) { queue = null; return; }
            int amount = ChunkLength(0);
            var writer = AmongUsClient.Instance.StartRpcImmediately(PlayerControl.LocalPlayer.NetId, shareOptionsId, SendOption.Reliable, -1);
            writer.Write((byte)amount);
            for (int i = 0; i < amount; i++) {
                var option = queue[i];
                writer.WritePacked((uint)option.id);
                // read now, not when the join happened; never the draft's locally held Prosecutor chance
                writer.WritePacked(Convert.ToUInt32(DraftLawyerConflict.SelectionForSync(option)));
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
