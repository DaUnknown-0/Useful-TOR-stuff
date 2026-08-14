// Useful TOR Stuff - Copyright (C) 2026 DaUnknown-0
// Licensed under GPL-3.0-or-later. See LICENSE for details.

/*
 * UTSModSync - the decision layer: what differs between the HOST's mod set and this client's, and
 * what may be offered as a one-click action.
 *
 * Only the host's inventory is diffed (rule V2). Any other player's inventory is display material
 * for the lobby board; it must never produce an install prompt, otherwise every random lobby member
 * could make everyone else's client offer to install something.
 *
 * The rules, in one place because they are the whole point of the feature:
 *   Install    mod missing locally, host runs it                 -> bulk action
 *   Upgrade    host runs a newer version                          -> bulk action
 *   Downgrade  host runs an OLDER version                         -> single click only, never bulk.
 *              A host on an old build must not be able to drag every client back to an arbitrarily
 *              old release (which may contain bugs we already fixed, e.g. the Snitch reveal). The
 *              catalog whitelist does not protect against this; this rule does.
 *   TestBuild  target is a prerelease (vX.Y.Z.W) while this client has test versions hidden
 *              -> single click only, and the channel toggle is NOT flipped permanently.
 *   Enable     installed but switched off locally -> tell the user to enable it, never download.
 *   HostMissing this client runs something the host does not -> display only, no action exists.
 *
 * Version comparison always goes through UsefulTORStuffUpdater.SemCompare: plain Version.CompareTo
 * would rank the prerelease 1.0.0.4 above the finalized 1.0.0 and invert half of these decisions.
 */

using System;
using System.Collections.Generic;

namespace UsefulTORStuff {

    public enum SyncAction {
        None,          // in sync (or nothing sensible to do)
        Install,       // missing locally, host has it
        Upgrade,       // host is newer
        Downgrade,     // host is older
        Enable,        // present locally but disabled
        HostMissing    // we have it, the host does not - display only
    }

    public sealed class SyncRow {
        public CatalogEntry Catalog;
        public SyncAction Action;
        public LocalModState LocalState;
        public Version LocalVersion;    // null when not installed
        public Version HostVersion;     // null when the host does not run it
        // True when this row must not travel in the bulk action: downgrades and prerelease targets
        // on a client that has test versions hidden. Both need a deliberate, separate click.
        public bool NeedsConfirm;

        // Already downloaded in this session: the file is on disk but the running process still has
        // the old state, so the row stays visible as "restart required" instead of being offered again.
        public bool Fetched;

        public bool IsDownloadable => !Fetched
                                   && (Action == SyncAction.Install
                                    || Action == SyncAction.Upgrade
                                    || Action == SyncAction.Downgrade);
    }

    public static class UTSModSync {

        private static List<SyncRow> cache;
        private static bool cacheValid;
        private static float cacheTime;

        // Catalog ids fetched in this session. Deliberately NOT fed back into UTSModCatalog: the
        // inventory we broadcast must keep describing the RUNNING process, not the files on disk.
        // Survives a lobby change - only restarting the game actually changes what is loaded.
        private static readonly HashSet<byte> fetched = new HashSet<byte>();

        public static void MarkFetched(byte catalogId) {
            fetched.Add(catalogId);
            InvalidateCache();
        }

        public static bool WasFetched(byte catalogId) => fetched.Contains(catalogId);
        public static bool AnythingFetched => fetched.Count > 0;

        public static void InvalidateCache() { cacheValid = false; }

        public static void ResetOnGameJoined() {
            cache = null;
            cacheValid = false;
        }

        // True when the host published an inventory at all. False means "host has no mod sync"
        // (no Useful TOR Stuff, or a build older than module byte 255) - the feature stays silent
        // rather than guessing, because "the host sent nothing" is not evidence of anything.
        public static bool HostReported => UTSModInventory.HostInventory != null;

        // How many mods the host runs that this catalog cannot name. Purely informational: it tells
        // the player that updating Useful TOR Stuff itself may reveal more.
        public static int HostUnknownCount => UTSModInventory.HostInventory?.UnknownCount ?? 0;

        // Rebuilt on demand, with a one-second ceiling on top of the explicit invalidation: host
        // migration changes who "the host" is without any new message arriving, and the board asks
        // for this every frame.
        public static List<SyncRow> Rows() {
            float now = UnityEngine.Time.realtimeSinceStartup;
            if (cacheValid && cache != null && now - cacheTime < 1f) return cache;
            cache = Build();
            cacheValid = true;
            cacheTime = now;
            return cache;
        }

        private static List<SyncRow> Build() {
            var rows = new List<SyncRow>();
            var host = UTSModInventory.HostInventory;
            if (host == null) return rows;

            // Never suggest anything to the host about the host's own lobby.
            if (AmongUsClient.Instance != null && AmongUsClient.Instance.AmHost) return rows;

            List<UTSModCatalog.LocalEntry> local;
            int unknownCount;
            try { local = UTSModCatalog.LocalInventory(out unknownCount); }
            catch { return rows; }

            bool showTest = false;
            try { showTest = VersionDisplay.ShowTestVersions(); } catch { }

            foreach (var localRow in local) {
                var entry = localRow.Catalog;
                var hostEntry = host.Get(entry.Id);

                // A mod the host has installed but switched off does not shape the round, so it is
                // treated exactly like "host does not have it".
                bool hostRuns = hostEntry != null
                             && hostEntry.State == LocalModState.Active
                             && hostEntry.Version != null;

                var row = new SyncRow {
                    Catalog = entry,
                    LocalState = localRow.State,
                    LocalVersion = localRow.Version,
                    HostVersion = hostRuns ? hostEntry.Version : null,
                    Action = SyncAction.None,
                    Fetched = fetched.Contains(entry.Id)
                };

                if (!hostRuns) {
                    // We run something the host does not. Nothing to download; worth showing so the
                    // player understands why their roles/options do not appear.
                    if (localRow.State == LocalModState.Active) row.Action = SyncAction.HostMissing;
                } else if (localRow.State == LocalModState.Missing) {
                    row.Action = SyncAction.Install;
                } else if (localRow.State == LocalModState.Disabled) {
                    // Present on disk but off. Downloading would not change anything the player can
                    // see, so the only honest suggestion is "switch it back on".
                    row.Action = SyncAction.Enable;
                } else {
                    int diff = UsefulTORStuffUpdater.SemCompare(hostEntry.Version, localRow.Version);
                    if (diff > 0) row.Action = SyncAction.Upgrade;
                    else if (diff < 0) row.Action = SyncAction.Downgrade;
                    else row.Action = SyncAction.None;
                }

                row.NeedsConfirm = row.Action == SyncAction.Downgrade
                                || (row.IsDownloadable && IsTestBuild(row.HostVersion) && !showTest);

                rows.Add(row);
            }

            return rows;
        }

        // Prerelease by tag shape: vX.Y.Z.W has a 4th component, vX.Y.Z does not.
        public static bool IsTestBuild(Version v) => v != null && v.Revision > 0;

        // Rows the bulk button acts on: everything downloadable that does not need its own click.
        public static List<SyncRow> BulkRows() {
            var list = new List<SyncRow>();
            foreach (var r in Rows())
                if (r.IsDownloadable && !r.NeedsConfirm) list.Add(r);
            return list;
        }

        // Worth showing the player a button for. Deliberately ONLY things they can act on.
        //
        // This used to include "the host runs mods this catalog does not know" and the purely
        // informational HostMissing rows, which made the button permanent for anyone joining a host
        // who runs private tooling: Role Control, Tracker Export, Bypass, Credits and
        // Mini.RegionInstall are all real plugins that will never be in a download catalog, so the
        // count never drops to zero and the button never goes away. A button that is always there is
        // not a notification, it is furniture.
        public static bool HasAnythingToShow() => ActionableCount() > 0;

        // Count used on the lobby button label.
        public static int ActionableCount() {
            int n = 0;
            foreach (var r in Rows())
                if (r.IsDownloadable || r.Action == SyncAction.Enable) n++;
            return n;
        }
    }
}
