// Copyright (C) 2026 DaUnknown-0. Licensed under GPL-3.0-or-later.
//
// THE "UNKNOWN'S COLLECTIVE" HUD LINE.
//
// Every one of DaUnknown's mods used to insert its own line into PingTracker's text
// unconditionally, every frame - fine with one or two mods installed, a wall of text with five.
// This collapses them: with exactly one of these mods loaded, the line looks exactly like it
// always has. With two or more, they fold into one clickable "Unknown's Collective (N)" line;
// clicking it expands to the full per-mod list (and collapses it again), and also drives the
// shared "Modded by DaUnknown" credit line the same click always has.
//
// Process-wide AppDomain state, no cross-assembly references - duplicated verbatim per mod,
// exactly like VersionDisplay.cs. Every mod calls Contribute() once per frame with its own
// display line, then Render() once; Render() is itself idempotent per frame (a marker inside the
// text, the same trick the old per-mod credit line already used), so every mod can call it
// unconditionally and only the first one to run each frame actually writes anything.

using System;
using System.Collections.Generic;
using System.Linq;
using TMPro;
using UnityEngine;

namespace UsefulTORStuff {
    public static class UnknownsCollective {
        private const string MembersKey = "TORMods.CollectiveMembers";
        private const string ExpandedKey = "TORMods.CollectiveExpanded";
        // Shared with every mod's OLD per-line credit toggle - same key, same meaning, so an
        // existing "Modded by DaUnknown" preference (still shown by an un-retrofitted mod) is not
        // reset just because this mod loaded too.
        private const string CreditKey = "TORMods.DaUnknownCreditVisible";
        // Both possible link IDs Render() ever writes share this prefix, so a single Contains()
        // check covers "did SOME mod already render the block this frame" without the two link
        // tags having to nest (TextMeshPro links do not nest).
        private const string MarkerPrefix = "unknownsCollective";
        private const string ToggleLinkId = "unknownsCollectiveToggle";
        private const string SoleLinkId = "unknownsCollectiveSole";
        // Click debounce, SHARED across every copy of this file via AppDomain like all other state
        // here. Render() runs once per frame PER LOADED MOD, and each copy sees the same
        // GetMouseButtonDown - so one click toggled the block five times in a row (open/shut
        // flicker, net effect depending on the mod count's parity). A per-assembly static would
        // only fix the copy that owns it; the shared timestamp makes the FIRST copy consume the
        // click and every other copy (and every further click for half a second) a no-op.
        private const string LastToggleKey = "TORMods.CollectiveLastToggle";
        private const float ToggleCooldown = 0.5f;

        private static bool ToggleReady() {
            float last = AppDomain.CurrentDomain.GetData(LastToggleKey) is float f ? f : -999f;
            return Time.realtimeSinceStartup - last >= ToggleCooldown;
        }

        private static void MarkToggled() =>
            AppDomain.CurrentDomain.SetData(LastToggleKey, Time.realtimeSinceStartup);

        private static Dictionary<string, string> Members() {
            var d = AppDomain.CurrentDomain.GetData(MembersKey) as Dictionary<string, string>;
            if (d == null) {
                d = new Dictionary<string, string>();
                AppDomain.CurrentDomain.SetData(MembersKey, d);
            }
            return d;
        }

        private static bool Expanded() =>
            AppDomain.CurrentDomain.GetData(ExpandedKey) is bool b && b;

        private static void SetExpanded(bool v) => AppDomain.CurrentDomain.SetData(ExpandedKey, v);

        private static bool CreditVisible() =>
            AppDomain.CurrentDomain.GetData(CreditKey) is bool b && b;

        /// Call once per frame from every mod's PingTracker patch, before Render(). `guid` is this
        /// mod's own BepInEx GUID (stable, unique - used as the dictionary key only, never shown).
        /// `coloredLine` is exactly the rich-text line the mod would have inserted on its own
        /// before ("<color=#xxxxxx>Name</color> vX.Y.Z"), with no <link> wrapper - Render() adds
        /// whichever wrapper the current view (single / collapsed / expanded) needs.
        public static void Contribute(string guid, string coloredLine) {
            try {
                var members = Members();
                // Only a CHANGED line bumps the shared version - every mod calls this each frame
                // with the same cached string, and the version is what lets Render() skip the
                // rebuild below on the (overwhelmingly common) unchanged frame.
                if (members.TryGetValue(guid, out var old)
                    && string.Equals(old, coloredLine, StringComparison.Ordinal)) return;
                members[guid] = coloredLine;
                AppDomain.CurrentDomain.SetData(VersionKey, MembersVersion() + 1);
            } catch { }
        }

        // PERF: the block used to be rebuilt from scratch every frame by whichever mod rendered
        // first - an OrderBy, a Join and several interpolations, sixty times a second, for text
        // that changes only when a mod's line changes, the list is expanded/collapsed or the
        // lobby turns into a round. It is now cached per copy of this file, keyed on exactly those
        // inputs; the shared members version lives in AppDomain like the rest of the state.
        private const string VersionKey = "TORMods.CollectiveVersion";
        private static int MembersVersion() =>
            AppDomain.CurrentDomain.GetData(VersionKey) is int v ? v : 0;

        private static string cachedBlock;
        private static int cachedVersion = -1, cachedCount = -1;
        private static bool cachedExpanded, cachedLobby;

        private static string BlockFor(Dictionary<string, string> members, bool lobby, bool expanded) {
            int version = MembersVersion();
            if (cachedBlock != null && version == cachedVersion && members.Count == cachedCount
                && lobby == cachedLobby && expanded == cachedExpanded)
                return cachedBlock;

            string block;
            if (members.Count == 1) {
                // Exactly one of our mods loaded: behave exactly as every mod used to on
                // its own - one clickable line, no collective wrapper.
                block = $"<link=\"{SoleLinkId}\">{members.Values.First()}</link>";
            } else if (lobby) {
                // Lobby: the "Unknown's Collective" name is a round-time thing -
                // list every mod's own line here, same as each used to show on its own.
                var lines = members.Values.OrderBy(v => v, StringComparer.Ordinal);
                block = $"<link=\"{ToggleLinkId}\">" + string.Join("\n", lines) + "</link>";
            } else if (!expanded) {
                // ">" / "v" rather than a real arrow glyph: the HUD's TMP font is ASCII-only
                // (see the world-space overlay lesson - ▸▾ etc. render as a missing-glyph box).
                // A bare ">" (not "&gt;" - this TMP build does not decode the entity) is safe:
                // rich text only treats "<...>" pairs as tags.
                block = $"<link=\"{ToggleLinkId}\"><color=#B892FF>Unknown's Collective</color>"
                      + $" ({members.Count}) ></link>";
            } else {
                var lines = members.Values.OrderBy(v => v, StringComparer.Ordinal);
                string header = $"<link=\"{ToggleLinkId}\"><color=#B892FF>Unknown's Collective</color>"
                      + " v</link>";
                block = header + "\n" + string.Join("\n", lines);
            }

            cachedBlock = block;
            cachedVersion = version;
            cachedCount = members.Count;
            cachedLobby = lobby;
            cachedExpanded = expanded;
            return block;
        }

        /// Handles the click and writes the line(s) into `text`, once per frame across every mod
        /// that calls this. Safe to call unconditionally.
        public static string Render(TMP_Text tmp, string text) {
            try {
                if (tmp == null || string.IsNullOrEmpty(text)) return text;
                var members = Members();
                if (members.Count == 0) return text;

                if (Input.GetMouseButtonDown(0)) {
                    Camera cam = Camera.main;
                    var canvas = tmp.canvas;
                    if (canvas != null)
                        cam = canvas.renderMode == RenderMode.ScreenSpaceOverlay ? null
                            : (canvas.worldCamera != null ? canvas.worldCamera : Camera.main);
                    int link = TMP_TextUtilities.FindIntersectingLink(tmp, Input.mousePosition, cam);
                    if (link != -1) {
                        string id = tmp.textInfo.linkInfo[link].GetLinkID();
                        if ((id == ToggleLinkId || id == SoleLinkId) && ToggleReady()) {
                            MarkToggled();
                            // The click always drives the shared credit line, exactly like clicking
                            // any single mod's name always has.
                            AppDomain.CurrentDomain.SetData(CreditKey, !CreditVisible());
                            // With two or more mods contributing, it ALSO expands/collapses the list.
                            if (id == ToggleLinkId) SetExpanded(!Expanded());
                        }
                    }
                }

                // Idempotency: whichever mod's patch runs first this frame renders the block: every
                // later mod's Render() call this same frame sees the marker and leaves `text` alone.
                if (!text.Contains(MarkerPrefix)) {
                    string block = BlockFor(members, ShipStatus.Instance == null, Expanded());

                    int nl = text.IndexOf('\n');
                    text = nl >= 0
                        ? text.Substring(0, nl + 1) + block + "\n" + text.Substring(nl + 1)
                        : text + "\n" + block;
                }

                // The shared credit line itself - unchanged from every mod's old copy of this same
                // block, just centralised so it is written at most once regardless of how many
                // mods are contributing this frame.
                if (CreditVisible() && !text.Contains("DaUnknown")) {
                    string credit = "\n<size=70%>Modded by <color=#FCCE03FF>DaUnknown</color></size>";
                    int anchor = text.IndexOf("Bavari");
                    if (anchor >= 0) {
                        int lineEnd = text.IndexOf('\n', anchor);
                        text = lineEnd >= 0
                            ? text.Substring(0, lineEnd) + credit + text.Substring(lineEnd)
                            : text + credit;
                    } else {
                        text += credit;
                    }
                }

                return text;
            } catch {
                return text;
            }
        }
    }
}
