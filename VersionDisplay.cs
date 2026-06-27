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
        public const string ShowTestVersionsKey = "TORMods.ShowTestVersions";

        public static bool ShowTestVersions() {
            try { return !(AppDomain.CurrentDomain.GetData(ShowTestVersionsKey) is bool b) || b; }
            catch { return true; }
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
    }
}
