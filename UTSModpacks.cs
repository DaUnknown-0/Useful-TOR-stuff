// Useful TOR Stuff - Copyright (C) 2026 DaUnknown-0
// Licensed under GPL-3.0-or-later. See LICENSE for details.

/*
 * UTSModpacks - named selections of catalog mods, shareable as a short code.
 *
 * A modpack says which catalog mods are ON and, per mod, whether the newest prerelease or the
 * newest stable is wanted. Applying one
 *   - queues a download for every listed mod (UTSModDownloader, newest release of the wanted
 *     channel; nothing is fetched when that version is already on disk),
 *   - switches every listed mod on and every other INSTALLED catalog mod off, through the mods'
 *     own "[General] Enabled" config (the Mod Manager's toggle), or by renaming the DLL for a mod
 *     without such a switch (Submerged: Submerged.dll <-> Submerged.dll.disabled).
 * Everything takes effect at the next game start, like every other Mod Manager change.
 *
 * Code format, one line, safe for chat: "UTSPACK1:<name>:1,3*,4" - version 1, the URL-escaped name,
 * then the catalog ids; a trailing * means "newest prerelease", otherwise "newest stable". Only
 * catalog ids travel (UTSModCatalog is the trust anchor): an id the local catalog does not know is
 * dropped with a note, nothing a code says ever becomes a URL, a path or a file name.
 *
 * Storage: one code per line in BepInEx/config/UTSModpacks.txt.
 */

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using BepInEx;

namespace UsefulTORStuff {

    public sealed class ModpackEntry {
        public byte Id;
        public bool Prerelease;
    }

    public sealed class Modpack {
        public string Name = "";
        public List<ModpackEntry> Mods = new List<ModpackEntry>();

        public bool Contains(byte id) => Mods.Any(m => m.Id == id);
        public ModpackEntry Get(byte id) => Mods.FirstOrDefault(m => m.Id == id);
    }

    public sealed class ModpackApplyResult {
        public int Downloads, Enabled, Disabled, Unknown;
        public List<string> Errors = new List<string>();
    }

    public static class UTSModpacks {
        private const string CodePrefix = "UTSPACK1:";
        private static List<Modpack> packs;

        private static string FilePath => Path.Combine(Paths.ConfigPath, "UTSModpacks.txt");

        public static IReadOnlyList<Modpack> All {
            get { if (packs == null) Load(); return packs; }
        }

        // ---- storage ----

        public static void Load() {
            packs = new List<Modpack>();
            try {
                if (!File.Exists(FilePath)) return;
                foreach (var line in File.ReadAllLines(FilePath)) {
                    var p = FromCode(line, out _, out _);
                    if (p != null) packs.Add(p);
                }
            } catch (Exception ex) {
                UsefulTORStuffPlugin.Logger?.LogWarning($"[Modpacks] load failed: {ex.Message}");
            }
        }

        public static void Save() {
            try {
                File.WriteAllLines(FilePath, All.Select(ToCode));
            } catch (Exception ex) {
                UsefulTORStuffPlugin.Logger?.LogWarning($"[Modpacks] save failed: {ex.Message}");
            }
        }

        public static void Add(Modpack p) {
            if (p == null) return;
            if (packs == null) Load();
            packs.Add(p);
            Save();
        }

        public static void Delete(Modpack p) {
            if (packs == null || p == null) return;
            packs.Remove(p);
            Save();
        }

        /// <summary>A name no stored pack has yet: "Modpack 1", "Modpack 2", ...</summary>
        public static string FreeName(string stem) {
            for (int i = 1; i < 1000; i++) {
                string n = $"{stem} {i}";
                if (!All.Any(p => string.Equals(p.Name, n, StringComparison.OrdinalIgnoreCase))) return n;
            }
            return stem;
        }

        // ---- building ----

        /// <summary>
        /// The pack that describes this install: every catalog mod that is installed and switched on
        /// (config), with the prerelease flag from the shared "show test versions" switch.
        /// </summary>
        public static Modpack FromCurrent(string name) {
            var p = new Modpack { Name = name ?? "" };
            bool pre = false;
            try { pre = VersionDisplay.ShowTestVersions(); } catch { }
            foreach (var e in UTSModCatalog.Entries) {
                var state = UTSModCatalog.StateOf(e, out _);
                if (state == LocalModState.Missing) continue;
                if (!IsSwitchedOn(e)) continue;
                p.Mods.Add(new ModpackEntry { Id = e.Id, Prerelease = pre });
            }
            return p;
        }

        // ---- code ----

        public static string ToCode(Modpack p) {
            if (p == null) return "";
            var ids = p.Mods.OrderBy(m => m.Id).Select(m => m.Id + (m.Prerelease ? "*" : ""));
            return CodePrefix + Uri.EscapeDataString(p.Name ?? "") + ":" + string.Join(",", ids);
        }

        /// <summary>Parses a code; null with an error key when it is not one. unknown = ids the local catalog lacks.</summary>
        public static Modpack FromCode(string code, out string errorKey, out int unknown) {
            errorKey = null; unknown = 0;
            if (string.IsNullOrWhiteSpace(code)) { errorKey = "uts.modpacks.err_empty"; return null; }
            code = code.Trim();
            if (!code.StartsWith(CodePrefix, StringComparison.Ordinal)) { errorKey = "uts.modpacks.err_format"; return null; }
            string rest = code.Substring(CodePrefix.Length);
            int sep = rest.IndexOf(':');
            if (sep < 0) { errorKey = "uts.modpacks.err_format"; return null; }
            string name;
            try { name = Uri.UnescapeDataString(rest.Substring(0, sep)); } catch { errorKey = "uts.modpacks.err_format"; return null; }
            // the name is shown in TMP labels: no tags, no control characters, bounded length
            name = Regex.Replace(name, @"[<>\p{C}]", "").Trim();
            if (name.Length > 40) name = name.Substring(0, 40);
            if (name.Length == 0) name = "Modpack";

            var p = new Modpack { Name = name };
            foreach (var tok in rest.Substring(sep + 1).Split(',', StringSplitOptions.RemoveEmptyEntries)) {
                string t = tok.Trim();
                bool pre = t.EndsWith("*");
                if (pre) t = t.Substring(0, t.Length - 1);
                if (!byte.TryParse(t, out byte id)) { errorKey = "uts.modpacks.err_format"; return null; }
                if (UTSModCatalog.ById(id) == null) { unknown++; continue; }
                if (p.Contains(id)) continue;
                p.Mods.Add(new ModpackEntry { Id = id, Prerelease = pre });
            }
            return p;
        }

        /// <summary>Short list of the pack's mods for a label: "Forgotten Fixes, Chaos*, Hostfix".</summary>
        public static string Describe(Modpack p) {
            if (p == null || p.Mods.Count == 0) return "-";
            return string.Join(", ", p.Mods.OrderBy(m => m.Id).Select(m => {
                var e = UTSModCatalog.ById(m.Id);
                return (e != null ? e.ShortName : "#" + m.Id) + (m.Prerelease ? "*" : "");
            }));
        }

        // ---- applying ----

        public static ModpackApplyResult Apply(Modpack p) {
            var res = new ModpackApplyResult();
            if (p == null) return res;
            var dl = UTSModDownloader.Instance;
            foreach (var e in UTSModCatalog.Entries) {
                var want = p.Get(e.Id);
                var state = UTSModCatalog.StateOf(e, out _);
                try {
                    if (want != null) {
                        // a DLL parked as .disabled comes back without a download
                        if (state == LocalModState.Missing && File.Exists(e.TargetPath + ".disabled") && !File.Exists(e.TargetPath)) {
                            File.Move(e.TargetPath + ".disabled", e.TargetPath);
                            res.Enabled++;
                        } else if (dl != null) {
                            dl.EnqueueLatest(e, want.Prerelease);
                            res.Downloads++;
                        }
                        if (SetSwitchedOn(e, true)) res.Enabled++;
                    } else if (state != LocalModState.Missing) {
                        if (SetSwitchedOn(e, false)) res.Disabled++;
                    }
                } catch (Exception ex) {
                    res.Errors.Add(e.ShortName);
                    UsefulTORStuffPlugin.Logger?.LogWarning($"[Modpacks] {e.ShortName}: {ex.Message}");
                }
            }
            UsefulTORStuffPlugin.Logger?.LogInfo(
                $"[Modpacks] applied '{p.Name}': {res.Downloads} download(s), {res.Enabled} on, {res.Disabled} off, {res.Errors.Count} error(s).");
            return res;
        }

        // ---- the on/off switch of one catalog mod ----

        /// <summary>Whether the mod is set to run at the next start (config), or for Submerged: its DLL is in place.</summary>
        public static bool IsSwitchedOn(CatalogEntry e) {
            if (e == null) return false;
            if (e.External) return File.Exists(e.TargetPath);
            var mod = RegistryEntry(e.Guid);
            if (mod != null && mod.Enabled != null) return mod.Enabled.Value;
            // not registered (never ran): the config file decides, default on
            return ReadConfigEnabled(e.Guid) ?? true;
        }

        /// <summary>Switches the mod on or off for the next start. Returns true when something changed.</summary>
        public static bool SetSwitchedOn(CatalogEntry e, bool on) {
            if (e == null) return false;
            if (e.External) {
                string path = e.TargetPath, parked = path + ".disabled";
                if (!on && File.Exists(path)) {
                    if (File.Exists(parked)) File.Delete(parked);
                    File.Move(path, parked);
                    return true;
                }
                if (on && !File.Exists(path) && File.Exists(parked)) { File.Move(parked, path); return true; }
                return false;
            }
            var mod = RegistryEntry(e.Guid);
            if (mod != null && mod.Enabled != null) {
                if (mod.Enabled.Value == on) return false;
                mod.Enabled.Value = on;
                mod.Enabled.ConfigFile?.Save();
                return true;
            }
            return WriteConfigEnabled(e.Guid, on);
        }

        private static ModInfo RegistryEntry(string guid) {
            try { return ModManagerRegistry.GetAllMods().FirstOrDefault(m => m != null && m.Guid == guid); }
            catch { return null; }
        }

        // The mods' config files follow BepInEx' layout: "[General]" then "Enabled = true". Read and
        // written by hand here because a mod that never ran has no ConfigFile object yet.
        private static string ConfigPathOf(string guid) => Path.Combine(Paths.ConfigPath, guid + ".cfg");

        private static readonly Regex EnabledLine = new Regex(@"^(?<key>Enabled\s*=\s*)(?<val>\S+)\s*$", RegexOptions.Multiline);

        private static bool? ReadConfigEnabled(string guid) {
            try {
                string path = ConfigPathOf(guid);
                if (!File.Exists(path)) return null;
                var m = FirstGeneralEnabled(File.ReadAllText(path), out _);
                if (m == null) return null;
                return !m.Groups["val"].Value.Equals("false", StringComparison.OrdinalIgnoreCase);
            } catch { return null; }
        }

        private static bool WriteConfigEnabled(string guid, bool on) {
            string path = ConfigPathOf(guid);
            string text = File.Exists(path) ? File.ReadAllText(path) : "";
            var m = FirstGeneralEnabled(text, out int generalAt);
            string val = on ? "true" : "false";
            if (m != null) {
                if (m.Groups["val"].Value.Equals(val, StringComparison.OrdinalIgnoreCase)) return false;
                text = text.Substring(0, m.Groups["val"].Index) + val + text.Substring(m.Groups["val"].Index + m.Groups["val"].Length);
            } else if (generalAt >= 0) {
                int lineEnd = text.IndexOf('\n', generalAt);
                lineEnd = lineEnd < 0 ? text.Length : lineEnd + 1;
                text = text.Substring(0, lineEnd) + $"Enabled = {val}\n" + text.Substring(lineEnd);
            } else {
                if (on) return false;   // no file and "on" is the default: nothing to write
                text = text.TrimEnd() + (text.Length > 0 ? "\n\n" : "") + $"[General]\nEnabled = {val}\n";
            }
            File.WriteAllText(path, text);
            return true;
        }

        // The "Enabled" line inside the [General] section (other sections have their own Enabled keys).
        private static Match FirstGeneralEnabled(string text, out int generalAt) {
            generalAt = -1;
            var sec = Regex.Match(text, @"^\[General\]\s*$", RegexOptions.Multiline);
            if (!sec.Success) return null;
            generalAt = sec.Index;
            int end = text.IndexOf("\n[", sec.Index + sec.Length, StringComparison.Ordinal);
            string body = end < 0 ? text.Substring(sec.Index) : text.Substring(sec.Index, end - sec.Index);
            var m = EnabledLine.Match(body);
            if (!m.Success) return null;
            // re-match on the full text at the right offset so the indices are absolute
            return EnabledLine.Match(text, sec.Index + m.Index);
        }
    }
}
