// Useful TOR Stuff - Copyright (C) 2026 DaUnknown-0
// Licensed under GPL-3.0-or-later. See LICENSE for details.

/*
 * UTSModCatalog - the ONLY trust anchor of the mod sync feature (UTSModInventory / UTSModSync).
 *
 * THE RULE THIS FILE EXISTS FOR: not a single byte that came in over the network may ever end up
 * in a URL, a file path or a file name. The host sends a catalog ID (one byte) and nothing else;
 * everything needed to actually fetch a DLL - repository owner, repository name, asset file name,
 * target path - is compiled into the table below and therefore lives on the CLIENT.
 *
 * Consequences that are features, not limitations:
 *   - A mod the local catalog does not know can never be downloaded, only counted (Id 0).
 *   - A newer mod becomes syncable only once a Useful TOR Stuff release ships a catalog that
 *     knows it. Since this mod is in its own catalog, that is self-healing: update this one first.
 *   - No mod NAME travels over the wire either. Names are looked up here, which also kills the
 *     rich-text injection angle (the lobby board renders through TMP with <color=...> markup, so a
 *     host-supplied name could otherwise forge "everything OK" rows).
 *
 * IDs ARE PERMANENT. Never re-use or re-number an entry: an older client resolves the ID with ITS
 * table, so a recycled ID would make it fetch a different mod than the host meant. Only append.
 */

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using BepInEx;
using BepInEx.Unity.IL2CPP;

namespace UsefulTORStuff {

    public sealed class CatalogEntry {
        public readonly byte Id;
        public readonly string Guid;
        public readonly string DisplayName;
        public readonly string RepositoryOwner;
        public readonly string RepositoryName;
        public readonly string AssetName;
        // Compact label for the host's Mod-Check lines (the display names are too long for a row).
        public readonly string ShortName;
        // Runs on the host only (HostFix): a guest never needs it, so it is never offered to one and
        // never counts as "missing" in the host view.
        public readonly bool HostOnly;
        // Only matters while the mod publishes its handshake column (Unknown's Atlas: only while an
        // Atlas map is chosen). The host view ignores it otherwise; the sync still offers it.
        public readonly bool BoardGated;
        // A mod from outside this family (Submerged). It never registers in ModManagerRegistry, so
        // "loaded by BepInEx" already means "running", with the chainloader's 3-part version.
        public readonly bool External;
        // Only this exact version may be synced (host must run it, download must be it). Null = any.
        public readonly Version PinnedVersion;
        // Plugin that must already be loaded locally before this mod is offered (Submerged needs
        // Reactor; a DLL that cannot load would only cost the guest a restart for nothing).
        public readonly string RequiresGuid;

        public CatalogEntry(byte id, string guid, string displayName,
                            string owner, string repo, string assetName,
                            string shortName = null, bool hostOnly = false, bool boardGated = false,
                            bool external = false, Version pinnedVersion = null, string requiresGuid = null) {
            Id = id; Guid = guid; DisplayName = displayName;
            RepositoryOwner = owner; RepositoryName = repo; AssetName = assetName;
            ShortName = shortName ?? displayName; HostOnly = hostOnly; BoardGated = boardGated;
            External = external; PinnedVersion = pinnedVersion; RequiresGuid = requiresGuid;
        }

        // True when a version may be synced for this entry (always, unless the entry is pinned).
        public bool AllowsVersion(Version v) =>
            PinnedVersion == null || (v != null && UsefulTORStuffUpdater.SemCompare(v, PinnedVersion) == 0);

        // Built from the compiled-in coordinates, never from anything received.
        public string ReleasesApiUrl =>
            $"https://api.github.com/repos/{RepositoryOwner}/{RepositoryName}/releases";

        // The download target. Deliberately NOT the GitHub asset's own name field: the file name
        // belongs to the catalog, so a compromised/odd release cannot steer where we write.
        public string TargetPath => Path.Combine(Paths.PluginPath, AssetName);
    }

    // What the local install looks like for one catalog entry.
    public enum LocalModState : byte {
        Missing = 0,     // not installed at all
        Active = 1,      // installed and loaded
        Disabled = 2     // installed but switched off (Mod Manager / config), so it is not running
    }

    public static class UTSModCatalog {

        // --- The table. APPEND ONLY. See the header. ---
        private static readonly CatalogEntry[] entries = {
            new CatalogEntry(1, UsefulTORStuffPlugin.PluginGuid, UsefulTORStuffPlugin.PluginName,
                             "DaUnknown-0", "Useful-TOR-stuff", "UsefulTORStuff.dll", "Forgotten Fixes"),
            new CatalogEntry(2, "com.tormod.chancemodifier", "TOR - Unknown Chaos",
                             "DaUnknown-0", "TOR-Chance", "TOR-ChanceModifier.dll", "Chaos"),
            new CatalogEntry(3, "com.tormod.unknownscollection", "Unknown's Collection",
                             "DaUnknown-0", "UnknownsCollection", "UnknownsCollection.dll", "Unknown's"),
            new CatalogEntry(4, "com.trackerteam.hostfix", "TOR - Hostfix",
                             "DaUnknown-0", "TOR-Host-Fix", "HostFixPlugin.dll", "Hostfix", hostOnly: true),
            // Pulled from distribution for a few hours on 2026-08-29 while its memory was under
            // suspicion, and put back the same evening once measured: a Mira world build costs the
            // process +38 MB and is released at round end (919 MB peak in a round that started at
            // 714 MB). The OutOfMemoryException that had implicated it came from a failing
            // SortingLayer.layers interop call, not from a full heap; that call is gone.
            new CatalogEntry(5, "com.tormod.nightfall", "Nightfall",
                             "DaUnknown-0", "Nightfall", "Nightfall.dll", "Nightfall"),
            new CatalogEntry(6, "com.daunknown0.atlas", "Unknown's Atlas",
                             "DaUnknown-0", "UnknownsAtlas", "UnknownsAtlas.dll", "Atlas", boardGated: true),
            // The Submerged map (SubmergedAmongUs/Submerged, added 2026-09-24). Pinned to v2025.1.30, the
            // release built for Among Us 2024.11.26 that was tested with TOR and our mods (UTS
            // SubmergedSelfTest); newer releases target newer game versions. Its licence allows
            // redistribution of the unmodified DLL, which is all the sync does (from its own
            // GitHub release). Needs Reactor, which the TOR package ships.
            new CatalogEntry(7, "Submerged", "Submerged",
                             "SubmergedAmongUs", "Submerged", "Submerged.dll", "Submerged",
                             external: true, pinnedVersion: new Version(2025, 1, 30), requiresGuid: "gg.reactor.api"),
        };

        // Reserved: "a mod outside this catalog". Counted in the inventory so the local player can
        // see THAT the host runs something else, never named and never offered for download.
        public const byte UnknownId = 0;

        public static IReadOnlyList<CatalogEntry> Entries => entries;

        public static CatalogEntry ById(byte id) {
            foreach (var e in entries) if (e.Id == id) return e;
            return null;
        }

        public static CatalogEntry ByGuid(string guid) {
            if (string.IsNullOrEmpty(guid)) return null;
            foreach (var e in entries) if (e.Guid == guid) return e;
            return null;
        }

        // ---- local install probing ----

        // Installed mods by GUID, straight from BepInEx. Covers disabled mods too: a mod that
        // early-returns in Load() (our "Enabled = false" convention) is still a loaded plugin as far
        // as the chainloader is concerned, which is exactly the distinction we want to show.
        //
        // The value is only an APPROXIMATE version. BepInEx stores plugin metadata as a
        // SemanticVersioning.Version, which has no 4th component - and the 4th component is exactly
        // what tells our test builds apart (v1.3.3.8 vs v1.3.3). The precise System.Version comes
        // from the Mod Manager registry below; this is the fallback for mods that are loaded but
        // never registered themselves.
        private static Dictionary<string, Version> LoadedPlugins() {
            var map = new Dictionary<string, Version>();
            try {
                foreach (var kv in IL2CPPChainloader.Instance.Plugins) {
                    var sv = kv.Value?.Metadata?.Version;
                    if (sv == null) continue;
                    try { map[kv.Key] = new Version(sv.Major, sv.Minor, sv.Patch); }
                    catch { }
                }
            } catch (Exception ex) {
                UsefulTORStuffPlugin.Logger?.LogWarning($"[ModSync] plugin enumeration failed: {ex.Message}");
            }
            return map;
        }

        // GUIDs that reported themselves as actually RUNNING, with the exact version they published.
        // Registration happens at the end of each mod's Load(), so this must never be called during
        // load - the inventory is built in the lobby, long after every plugin has loaded, which
        // makes the order irrelevant.
        private static Dictionary<string, Version> RunningMods() {
            var map = new Dictionary<string, Version>();
            try {
                foreach (var m in ModManagerRegistry.GetAllMods())
                    if (m != null && m.RuntimeEnabled && !string.IsNullOrEmpty(m.Guid))
                        map[m.Guid] = m.Version;
            } catch { }
            return map;
        }

        public struct LocalEntry {
            public CatalogEntry Catalog;
            public LocalModState State;
            public Version Version;   // null when Missing
        }

        // The local install cannot change while the game runs (BepInEx loads plugins once, at
        // startup), so this is computed once and reused. That matters twice over: the lobby board
        // asks for it every frame, and ModManagerRegistry.GetAllMods() writes a log line on every
        // single call - without the cache that alone would flood the log.
        private static List<LocalEntry> cachedLocal;
        private static int cachedUnknownCount;

        // The local inventory: one row per catalog entry, plus a count of loaded plugins this
        // catalog does not know (reported as Id 0, never named).
        public static List<LocalEntry> LocalInventory(out int unknownCount) {
            if (cachedLocal != null) {
                unknownCount = cachedUnknownCount;
                return cachedLocal;
            }
            var loaded = LoadedPlugins();
            var running = RunningMods();
            var list = new List<LocalEntry>();

            foreach (var e in entries) {
                var row = new LocalEntry { Catalog = e, State = LocalModState.Missing, Version = null };
                if (loaded.TryGetValue(e.Guid, out var approx)) {
                    // Registered = running, and its version is the exact one (4 components included).
                    if (e.External) {
                        row.State = LocalModState.Active;
                        row.Version = approx;
                    } else if (running.TryGetValue(e.Guid, out var exact) && exact != null) {
                        row.State = LocalModState.Active;
                        row.Version = exact;
                    } else {
                        row.State = LocalModState.Disabled;
                        row.Version = approx;
                    }
                }
                list.Add(row);
            }

            // Everything loaded that is not in the catalog. TOR itself and BepInEx' own plugins would
            // otherwise be counted as "unknown mods", which is noise: only count plugins that are
            // neither catalogued nor part of the base install.
            unknownCount = 0;
            foreach (var guid in loaded.Keys) {
                if (ByGuid(guid) != null) continue;
                if (IsNeverSyncable(guid)) continue;
                unknownCount++;
            }

            cachedLocal = list;
            cachedUnknownCount = unknownCount;
            // Once per process (the result is cached): what this client reports to the host.
            try {
                UsefulTORStuffPlugin.Logger?.LogInfo("[ModSync] local inventory: " + string.Join(", ",
                    list.Select(r => $"{r.Catalog.ShortName} {r.State}{(r.Version != null ? " " + r.Version : "")}"))
                    + $"; unknown plugins {unknownCount}");
            } catch { }
            return list;
        }

        // Plugins that must never be counted as "a mod the host runs that you are missing":
        //  - the base install everybody has anyway (TOR, Reactor, BepInEx itself), and
        //  - our own local tooling, which is deliberately not distributed and therefore can never
        //    appear in any catalog. Counting those made the lobby button permanent for anyone whose
        //    host runs Role Control or the Tracker export, because the number could never reach zero.
        private static bool IsNeverSyncable(string guid) {
            switch (guid) {
                case "me.eisbison.theotherroles":       // TOR itself
                case "gg.reactor.api":
                case "com.trackerteam.forceimpostor":   // Role Control (host tooling)
                case "tracker.export.plugin":           // Tracker export
                case "com.tormod.bypasstest":           // Bypass test mod
                case "com.tormod.debugunlock":
                case "com.tormod.credits":
                    return true;
            }
            return guid.StartsWith("com.bepinex", StringComparison.OrdinalIgnoreCase)
                || guid.StartsWith("gg.reactor", StringComparison.OrdinalIgnoreCase)
                // Mini.RegionInstall ships with practically every modded install and has nothing to
                // do with the round; whatever exact id it uses, it is not something to offer.
                || guid.IndexOf("regioninstall", StringComparison.OrdinalIgnoreCase) >= 0;
        }

        // Loaded by BepInEx at all (running or not). Cached with the inventory's lifetime in mind:
        // the plugin set cannot change while the game runs.
        private static Dictionary<string, Version> cachedLoaded;
        public static bool IsLoaded(string guid) {
            if (string.IsNullOrEmpty(guid)) return false;
            if (cachedLoaded == null) cachedLoaded = LoadedPlugins();
            return cachedLoaded.ContainsKey(guid);
        }

        public static LocalModState StateOf(CatalogEntry e, out Version version) {
            version = null;
            if (e == null) return LocalModState.Missing;
            int ignored;
            foreach (var row in LocalInventory(out ignored)) {
                if (row.Catalog.Id != e.Id) continue;
                version = row.Version;
                return row.State;
            }
            return LocalModState.Missing;
        }

        // ---- download URL validation (rule V5) ----

        // A release asset URL is only accepted when it is https AND lives on a GitHub host AND -
        // for github.com itself - points into THIS catalog entry's own release download path. The
        // check parses the URL instead of doing StartsWith on a string, so neither
        // "https://github.com.evil.tld/..." nor "https://evil.tld/?x=https://github.com/..." passes.
        public static bool IsTrustedAssetUrl(CatalogEntry entry, string url) {
            if (entry == null || string.IsNullOrEmpty(url)) return false;
            Uri uri;
            try { uri = new Uri(url); } catch { return false; }

            if (uri.Scheme != Uri.UriSchemeHttps) return false;

            string host = uri.Host.ToLowerInvariant();
            if (host == "github.com") {
                // Expected shape: /{owner}/{repo}/releases/download/{tag}/{asset}
                string expected = $"/{entry.RepositoryOwner}/{entry.RepositoryName}/releases/download/";
                return uri.AbsolutePath.StartsWith(expected, StringComparison.OrdinalIgnoreCase);
            }

            // GitHub redirects asset downloads to its object storage; accept those hosts as-is
            // (the path there is opaque and signed, there is nothing repo-shaped left to match).
            return host == "objects.githubusercontent.com"
                || host == "release-assets.githubusercontent.com";
        }
    }
}
