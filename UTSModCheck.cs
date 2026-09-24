// Useful TOR Stuff - Copyright (C) 2026 DaUnknown-0
// Licensed under GPL-3.0-or-later. See LICENSE for details.

/*
 * UTSModCheck - the host's per-player answer to "who does not run my mods (correctly)?".
 *
 * Two sources, merged per player and per mod:
 *   - the mod inventory (UTSModInventory, module byte 255): exact 4-part versions and the on/off state
 *     of every catalogued mod, sent by every client that has this mod. This is the primary source,
 *     because it also knows mods that publish no handshake of their own (Nightfall) and tells test
 *     builds apart (1.2.6.1 vs 1.2.6.2), which the 3-byte handshakes cannot.
 *   - the cross-mod handshake board (TORMods.Handshake.*, see UsefulVersionHandshake): the per-mod
 *     verdicts published by this mod, Chance, Unknown's Collection and Unknown's Atlas. It covers
 *     players WITHOUT this mod (they send no inventory, but their other mods still shake hands) and
 *     adds "modified build" (same version, different assembly), which no inventory can see.
 *
 * What counts: every catalogued mod the HOST runs, minus host-only mods (HostFix: a guest never needs
 * it) and minus board-gated mods whose column is not published (Unknown's Atlas publishes only while
 * an Atlas map is chosen, so a vanilla lobby does not flag everyone without it). Every player is
 * compared with the host, never with each other, which is why this is a host view.
 *
 * Replaces the old board, which listed EVERY player with every mod as soon as one thing differed and
 * compared 3-part versions only. Now only players with a problem get a line, each with the exact
 * versions and whether the Mod-Sync can fix it for them.
 */

using System;
using System.Collections.Generic;

namespace UsefulTORStuff {

    public enum ModCheckFix {
        None,       // nothing the sync could do (for example a mod switched off on their side)
        Sync,       // they run the Mod-Sync and it offers the missing/different mods
        SyncOff,    // they have this mod, but the Mod-Sync is switched off in their config
        Manual      // no Forgotten Fixes at all: they have to install by hand
    }

    public sealed class ModCheckPlayer {
        public int ClientId;
        public string Name;
        public readonly List<string> Issues = new List<string>();
        public ModCheckFix Fix;
    }

    public static class UTSModCheck {
        private const string BoardRegistry = "TORMods.Handshake.Registry";
        private const string BoardPrefix = "TORMods.Handshake.";
        private const char StatusSep = '\u001f';
        // Same beat as the lobby board refresh; the name tint asks every frame.
        private const float CacheSeconds = 0.25f;

        private static List<ModCheckPlayer> cache = new List<ModCheckPlayer>();
        private static readonly HashSet<int> cacheIds = new HashSet<int>();
        private static int cacheChecked;
        private static float cacheAt = -1f;

        /// Players that differ from the host (empty = all fine); checkedPlayers = guests looked at.
        public static List<ModCheckPlayer> Current(out int checkedPlayers) {
            float now = UnityEngine.Time.realtimeSinceStartup;
            if (cacheAt < 0f || now - cacheAt >= CacheSeconds) {
                try { cache = Evaluate(out cacheChecked); }
                catch (Exception ex) {
                    UsefulTORStuffPlugin.Logger?.LogWarning($"[ModCheck] evaluation failed: {ex.Message}");
                    cache = new List<ModCheckPlayer>();
                    cacheChecked = 0;
                }
                cacheIds.Clear();
                foreach (var p in cache) cacheIds.Add(p.ClientId);
                cacheAt = now;
            }
            checkedPlayers = cacheChecked;
            return cache;
        }

        public static bool HasIssues(int clientId) {
            Current(out _);
            return cacheIds.Contains(clientId);
        }

        public static void Invalidate() { cacheAt = -1f; }

        private static List<ModCheckPlayer> Evaluate(out int checkedPlayers) {
            var result = new List<ModCheckPlayer>();
            checkedPlayers = 0;
            var ac = AmongUsClient.Instance;
            if (ac == null || ac.allClients == null) return result;

            var board = ReadBoard();
            var mods = new List<(CatalogEntry Entry, Version Host)>();
            foreach (var row in UTSModCatalog.LocalInventory(out _)) {
                if (row.State != LocalModState.Active || row.Version == null) continue;
                if (row.Catalog.HostOnly) continue;
                if (row.Catalog.BoardGated && !board.ContainsKey(row.Catalog.Guid)) continue;
                mods.Add((row.Catalog, row.Version));
            }
            board.TryGetValue(UsefulTORStuffPlugin.PluginGuid, out var ownColumn);

            // Walked in place: ToArray() would copy the Il2Cpp list every refresh.
            for (int i = 0; i < ac.allClients.Count; i++) {
                var c = ac.allClients[i];
                if (c == null || c.Character == null || c.Character.Data == null) continue;
                if (c.Id == ac.ClientId || c.Character == PlayerControl.LocalPlayer) continue;
                checkedPlayers++;

                UTSModInventory.inventories.TryGetValue(c.Id, out var inv);
                var p = new ModCheckPlayer { ClientId = c.Id, Name = c.Character.Data.PlayerName };
                bool syncable = false;

                foreach (var (entry, hostVersion) in mods) {
                    board.TryGetValue(entry.Guid, out var column);
                    string code = Code(column, c.Id, out string boardVersion);
                    string label = entry.ShortName;
                    if (inv != null) {
                        var e = inv.Get(entry.Id);
                        if (e == null || e.State == LocalModState.Missing) {
                            p.Issues.Add(UTSLocalization.Tr("uts.modcheck.missing", label));
                            if (entry.AllowsVersion(hostVersion)) syncable = true;   // pinned (Submerged): only its version
                        } else if (e.State == LocalModState.Disabled) {
                            p.Issues.Add(UTSLocalization.Tr("uts.modcheck.off", label));
                        } else if (!Same(e.Version, hostVersion)) {
                            p.Issues.Add(UTSLocalization.Tr("uts.modcheck.version", label, Ver(e.Version), Ver(hostVersion)));
                            if (entry.AllowsVersion(hostVersion)) syncable = true;
                        } else if (code == "mod") {
                            p.Issues.Add(UTSLocalization.Tr("uts.modcheck.modified", label, Ver(e.Version)));
                        }
                    } else if (column != null) {
                        // No inventory: only the mod's own handshake can speak for this player.
                        if (code == null)
                            p.Issues.Add(UTSLocalization.Tr("uts.modcheck.missing", label));
                        else if (code == "old" || code == "new")
                            p.Issues.Add(UTSLocalization.Tr("uts.modcheck.version", label, boardVersion, Ver(hostVersion)));
                        else if (code == "mod")
                            p.Issues.Add(UTSLocalization.Tr("uts.modcheck.modified", label, boardVersion));
                    }
                    // No inventory and no column (Nightfall for a player without this mod): nothing is
                    // known, so nothing is claimed.
                }

                if (p.Issues.Count == 0) continue;
                if (inv != null) p.Fix = syncable ? ModCheckFix.Sync : ModCheckFix.None;
                else p.Fix = Code(ownColumn, c.Id, out _) != null ? ModCheckFix.SyncOff : ModCheckFix.Manual;
                result.Add(p);
            }
            return result;
        }

        private static Dictionary<string, Dictionary<int, string>> ReadBoard() {
            var board = new Dictionary<string, Dictionary<int, string>>();
            var reg = AppDomain.CurrentDomain.GetData(BoardRegistry) as string ?? "";
            foreach (var g in reg.Split(',')) {
                if (g.Length == 0 || board.ContainsKey(g)) continue;
                board[g] = AppDomain.CurrentDomain.GetData(BoardPrefix + g + ".status") as Dictionary<int, string>
                           ?? new Dictionary<int, string>();
            }
            return board;
        }

        // One player's verdict in one mod's column; null when the column has no entry for them.
        private static string Code(Dictionary<int, string> column, int clientId, out string version) {
            version = "?";
            if (column == null || !column.TryGetValue(clientId, out var token) || token == null) return null;
            int sep = token.IndexOf(StatusSep);
            if (sep < 0) return token;
            version = token.Substring(sep + 1);
            return token.Substring(0, sep);
        }

        private static Version Norm(Version v) =>
            new Version(Math.Max(0, v.Major), Math.Max(0, v.Minor), Math.Max(0, v.Build), Math.Max(0, v.Revision));

        private static bool Same(Version a, Version b) => a != null && b != null && Norm(a).Equals(Norm(b));

        // Always the full version (the 4th component is exactly what tells test builds apart).
        public static string Ver(Version v) {
            if (v == null) return "?";
            return v.Revision > 0 ? $"{v.Major}.{v.Minor}.{v.Build}.{v.Revision}" : $"{v.Major}.{v.Minor}.{v.Build}";
        }
    }
}
