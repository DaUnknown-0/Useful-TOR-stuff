// Useful TOR Stuff - Copyright (C) 2026 DaUnknown-0
// Licensed under GPL-3.0-or-later. See LICENSE for details.

/*
 * UTSModInventory - the wire layer of the mod sync feature (module byte 255 on UTSRpc.CallId 240).
 *
 * Every client that has this mod broadcasts, once per lobby and again whenever somebody joins, WHICH
 * catalogued mods it runs and in which version. Until now a missing mod was simply invisible: an
 * uninstalled mod sends no handshake and nothing publishes on its behalf, so nobody could tell the
 * difference between "player doesn't have Unknown's Collection" and "Unknown's Collection said
 * nothing yet". This message closes that gap, because it is sent by a mod that IS there.
 *
 * Wire format (after the module byte):
 *   packed int  count
 *   count x {  byte catalogId, byte state, packed int major, minor, build, revision (-1 = none)  }
 *   packed int  unknownCount        // catalogued-by-nobody plugins, counted only, never named
 *
 * Only catalog IDs travel, never names, repositories or file names - see UTSModCatalog's header.
 * The sender is identified by the TRANSPORT (UTSRpc.Sender), never by anything in the payload, so a
 * client cannot file its inventory under the host's id and fake a download suggestion for others.
 *
 * NO dual-send: this module is new, older builds never had a legacy callId for it and correctly
 * ignore unknown module bytes (UTSRpc.HandleRpcPatch). A host on an older build therefore simply
 * produces no suggestions, which is the intended graceful degradation.
 */

using System;
using System.Collections.Generic;
using HarmonyLib;
using Hazel;

namespace UsefulTORStuff {

    public sealed class InventoryEntry {
        public byte CatalogId;
        public LocalModState State;
        public Version Version;      // null when State == Missing
    }

    public sealed class ClientInventory {
        public readonly Dictionary<byte, InventoryEntry> Entries = new Dictionary<byte, InventoryEntry>();
        public int UnknownCount;

        public InventoryEntry Get(byte catalogId) =>
            Entries.TryGetValue(catalogId, out var e) ? e : null;
    }

    public static class UTSModInventory {

        // clientId -> what that client reported. The key comes from the transport, never the payload.
        public static readonly Dictionary<int, ClientInventory> inventories =
            new Dictionary<int, ClientInventory>();

        private static bool sentThisLobby;

        // The host's inventory, or null when the host never sent one (no mod / older build).
        // This is the ONLY inventory that may produce download suggestions (rule V2).
        public static ClientInventory HostInventory {
            get {
                var client = AmongUsClient.Instance;
                if (client == null) return null;
                return inventories.TryGetValue(client.HostId, out var inv) ? inv : null;
            }
        }

        public static void RegisterRpc() {
            UTSRpc.Register(UsefulTORStuffPlugin.ModInventoryRpcId, HandleModuleRpc);
        }

        private static void HandleModuleRpc(MessageReader reader) {
            // Capture the sender BEFORE parsing: UTSRpc clears it once the handler returns.
            var sender = UTSRpc.Sender;
            try { Receive(reader, sender); }
            catch (Exception ex) {
                UsefulTORStuffPlugin.Logger?.LogWarning($"[ModSync] malformed inventory ignored: {ex.Message}");
            }
        }

        // ---- send ----

        public static void Share() {
            if (AmongUsClient.Instance == null || PlayerControl.LocalPlayer == null) return;
            if (UsefulTORStuffPlugin.ModSyncEnabled != null && !UsefulTORStuffPlugin.ModSyncEnabled.Value) return;

            List<UTSModCatalog.LocalEntry> local;
            int unknownCount;
            try { local = UTSModCatalog.LocalInventory(out unknownCount); }
            catch (Exception ex) {
                UsefulTORStuffPlugin.Logger?.LogWarning($"[ModSync] local inventory failed: {ex.Message}");
                return;
            }

            try {
                MessageWriter w = UTSRpc.Begin(UsefulTORStuffPlugin.ModInventoryRpcId);
                WritePayload(w, local, unknownCount);
                AmongUsClient.Instance.FinishRpcImmediately(w);
            } catch (Exception ex) {
                UsefulTORStuffPlugin.Logger?.LogError($"[ModSync] inventory send failed: {ex}");
            }

            // Apply locally too - the sender never receives its own broadcast, and the lobby board
            // wants a row for every client including this one.
            StoreLocal(local, unknownCount);
        }

        private static void WritePayload(MessageWriter w, List<UTSModCatalog.LocalEntry> local, int unknownCount) {
            w.WritePacked(local.Count);
            foreach (var row in local) {
                w.Write(row.Catalog.Id);
                w.Write((byte)row.State);
                var v = row.Version;
                w.WritePacked(v?.Major ?? 0);
                w.WritePacked(v == null ? 0 : Math.Max(0, v.Minor));
                w.WritePacked(v == null ? 0 : Math.Max(0, v.Build));
                w.WritePacked(v == null ? -1 : v.Revision);   // -1 = stable build, no 4th component
            }
            w.WritePacked(unknownCount);
        }

        private static void StoreLocal(List<UTSModCatalog.LocalEntry> local, int unknownCount) {
            var inv = new ClientInventory { UnknownCount = unknownCount };
            foreach (var row in local) {
                inv.Entries[row.Catalog.Id] = new InventoryEntry {
                    CatalogId = row.Catalog.Id,
                    State = row.State,
                    Version = row.Version
                };
            }
            inventories[AmongUsClient.Instance.ClientId] = inv;
        }

        // ---- receive ----

        private static void Receive(MessageReader reader, PlayerControl sender) {
            if (sender == null) return;
            int clientId = sender.OwnerId;

            int count = reader.ReadPackedInt32();
            // A hostile/broken sender could claim an absurd count; the catalog bounds what can
            // possibly be meaningful, so refuse anything beyond it instead of allocating.
            if (count < 0 || count > 512) {
                UsefulTORStuffPlugin.Logger?.LogWarning(
                    $"[ModSync] inventory from client {clientId} claims {count} entries - ignored.");
                return;
            }

            var inv = new ClientInventory();
            for (int i = 0; i < count; i++) {
                byte catalogId = reader.ReadByte();
                byte rawState = reader.ReadByte();
                int major = reader.ReadPackedInt32();
                int minor = reader.ReadPackedInt32();
                int build = reader.ReadPackedInt32();
                int revision = reader.ReadPackedInt32();

                // Unknown catalog ids are dropped, not stored: this build cannot resolve them to
                // anything, and keeping them would only invite code that tries.
                if (UTSModCatalog.ById(catalogId) == null) continue;

                var state = rawState <= (byte)LocalModState.Disabled
                    ? (LocalModState)rawState : LocalModState.Missing;

                Version version = null;
                if (state != LocalModState.Missing) {
                    try {
                        version = revision < 0
                            ? new Version(Math.Max(0, major), Math.Max(0, minor), Math.Max(0, build))
                            : new Version(Math.Max(0, major), Math.Max(0, minor), Math.Max(0, build), revision);
                    } catch { version = null; }
                    if (version == null) state = LocalModState.Missing;
                }

                inv.Entries[catalogId] = new InventoryEntry {
                    CatalogId = catalogId, State = state, Version = version
                };
            }

            int unknown = 0;
            try { unknown = reader.ReadPackedInt32(); } catch { }
            inv.UnknownCount = Math.Max(0, Math.Min(unknown, 999));

            inventories[clientId] = inv;
            UTSModSync.InvalidateCache();
        }

        // ---- lifecycle ----

        // Client ids are per connection, so the table must never survive a lobby change.
        [HarmonyPatch(typeof(AmongUsClient), nameof(AmongUsClient.OnGameJoined))]
        static class OnGameJoinedPatch {
            public static void Postfix() {
                inventories.Clear();
                sentThisLobby = false;
                UTSModSync.ResetOnGameJoined();
            }
        }

        [HarmonyPatch(typeof(AmongUsClient), nameof(AmongUsClient.OnPlayerJoined))]
        static class OnPlayerJoinedPatch {
            public static void Postfix() {
                if (PlayerControl.LocalPlayer != null) Share();
            }
        }

        [HarmonyPatch(typeof(GameStartManager), nameof(GameStartManager.Start))]
        static class GameStartManagerStartPatch {
            public static void Postfix() { sentThisLobby = false; }
        }

        // One broadcast per lobby, mirroring UsefulVersionHandshake's own versionSent latch.
        [HarmonyPatch(typeof(GameStartManager), nameof(GameStartManager.Update))]
        [HarmonyPriority(Priority.Low)]
        static class GameStartManagerUpdatePatch {
            public static void Postfix() {
                if (PlayerControl.LocalPlayer == null || sentThisLobby) return;
                sentThisLobby = true;
                Share();
            }
        }
    }
}
