// Copyright (C) 2026 DaUnknown-0. Licensed under GPL-3.0-or-later.
//
// Shared version-string formatting for all DaUnknown TOR mods. Version scheme: vX.Y.Z (stable) or
// vX.Y.Z.W (TEST build, W = 4th component set by the CI release workflow from a vX.Y.Z.W tag). A build
// is "test" iff System.Version.Revision > 0 (plain vX.Y.Z parses to Revision == -1). The 4th component
// is shown ONLY on test builds AND only while the shared "show test versions" toggle is on (Mod
// Manager). The toggle is a process-wide AppDomain flag with an identical key across every mod, so
// flipping it once affects all mods - no cross-assembly references. Duplicated verbatim per mod.

using System;

namespace UsefulTORStuff {
    public static class VersionDisplay {
        // Shared across ALL DaUnknown mods - keep this string identical everywhere.
        public const string ShowTestVersionsKey = "TORMods.ShowTestVersions";

        // Default FALSE: test builds are opt-in - the test-version suffix only shows when explicitly enabled.
        public static bool ShowTestVersions() {
            try { return AppDomain.CurrentDomain.GetData(ShowTestVersionsKey) is bool b && b; }
            catch { return false; }
        }

        public static void SetShowTestVersions(bool value) {
            try { AppDomain.CurrentDomain.SetData(ShowTestVersionsKey, value); } catch { }
        }

        // Formats without a leading "v". Callers prepend "v" themselves.
        public static string Format(Version v) {
            if (v == null) return "?";
            bool isTest = v.Revision > 0;
            if (isTest && ShowTestVersions())
                return $"{v.Major}.{v.Minor}.{v.Build}.{v.Revision}";
            return $"{v.Major}.{v.Minor}.{v.Build}";
        }

        // Rich-text variant of Format(): on a shown test build, only the 4th (test-revision) component
        // is colour-highlighted, same TMPro <color> tag style already used at every version-line call
        // site in this project (e.g. UnknownsCollectionPlugin's credits line). Stable builds render
        // identically to Format() - no tags at all, so there is nothing to strip for plain-text
        // consumers that happen to call this instead. Callers still prepend "v" themselves.
        //
        // Intended for TMPro labels only (TMPro renders <color> tags by default). Plain-text consumers
        // (logs, non-rich UI) must keep using Format() - FormatRich() is additive, not a replacement.
        public const string TestVersionColor = "#FFA33F";

        public static string FormatRich(Version v) {
            if (v == null) return "?";
            bool isTest = v.Revision > 0;
            if (isTest && ShowTestVersions())
                return $"{v.Major}.{v.Minor}.{v.Build}<color={TestVersionColor}>.{v.Revision}</color>";
            return $"{v.Major}.{v.Minor}.{v.Build}";
        }
    }
}
