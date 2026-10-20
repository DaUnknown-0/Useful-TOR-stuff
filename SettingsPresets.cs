// Useful TOR Stuff - Copyright (C) 2026 DaUnknown-0
// Licensed under GPL-3.0-or-later. See LICENSE for details.
// Based on The Other Roles (https://github.com/TheOtherRolesAU/TheOtherRoles), GPL-3.0.

/*
 * SettingsPresets - as many named settings presets as the host wants (User 2026-10-20: "presets, easier to set
 * than now, no limits"). A preset holds EVERY mod option (CustomOption.options: TOR's and every loaded mod's) and
 * the Among Us settings; the host saves, loads, renames, overwrites and deletes them in the lobby menu
 * (SettingsPresetsUI).
 *
 * What TOR has, and why this is separate: two preset slots ("Preset 1/2", option id 0) plus five random-map slots,
 * all inside TOR's own config file, and the copy/paste string (SettingsShare). The slots are numbered, unnamed and
 * fixed in number; the string has to be kept somewhere by hand. Here every preset is one file:
 *     BepInEx/config/UTS-Presets/<name>.preset      (UTF-8 text)
 *         # UTS settings preset
 *         name=<display name>
 *         saved=<yyyy-MM-ddTHH:mm:ss, local time>
 *         tor=<TOR version that saved it>
 *         vanilla=<base64 of the game options, the same bytes TOR's saveVanillaOptions writes>
 *         <id>\t<selection>\t<value text>\t<option name>      one line per option
 * The value text lets a preset survive an option whose selection list changed (an option that got a new step):
 * the saved value is looked up first, the index is the fallback. Options a preset does not know (a mod installed
 * later) keep their current value; options of a mod that is missing now are skipped and counted.
 *
 * LOADING (host only, lobby): only the options whose selection really changes are touched: selection, TOR's
 * config entry of the current TOR slot (one file write at the end, not one per option), the open menu's value and
 * the option's onChange. Then just these options go out through PacedOptionSync.SendPaced (200 per packet, 0.15 s
 * apart: the server dropped players at big option bursts, 2026-10-16), the game options through the game's own
 * single SyncOptions. Nothing is sent when nothing changed.
 */

using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using TheOtherRoles;
using UnityEngine;

namespace UsefulTORStuff {

    public static class SettingsPresets {

        internal static string Folder => Path.Combine(BepInEx.Paths.ConfigPath, "UTS-Presets");
        private const string Ext = ".preset";
        internal const int MaxName = 40;

        private static void Log(string s) => UsefulTORStuffPlugin.Logger?.LogInfo("[Presets] " + s);
        private static void Warn(string s) => UsefulTORStuffPlugin.Logger?.LogWarning("[Presets] " + s);

        internal sealed class Entry {
            public int Id, Selection;
            public string Value, Name;
        }

        internal sealed class Preset {
            public string Path;
            public string Name;
            public DateTime Saved;
            public string Vanilla;
            public readonly List<Entry> Options = new List<Entry>();
        }

        internal sealed class LoadResult {
            public int Changed, Same, Unknown, Packets;
            public bool VanillaApplied;
            public string Error;
        }

        /// Name of the preset loaded or saved last in this session (the panel marks it).
        internal static string LastName { get; private set; }

        // ====================================================================
        // Files
        // ====================================================================
        internal static List<Preset> List() {
            var list = new List<Preset>();
            try {
                if (!Directory.Exists(Folder)) return list;
                foreach (var f in Directory.GetFiles(Folder, "*" + Ext)) {
                    var p = Read(f);
                    if (p != null) list.Add(p);
                }
            } catch (Exception e) { Warn("list failed: " + e.Message); }
            return list.OrderByDescending(p => p.Saved).ThenBy(p => p.Name, StringComparer.OrdinalIgnoreCase).ToList();
        }

        private static Preset Read(string path) {
            try {
                var p = new Preset { Path = path, Name = System.IO.Path.GetFileNameWithoutExtension(path), Saved = File.GetLastWriteTime(path) };
                foreach (var line in File.ReadAllLines(path, Encoding.UTF8)) {
                    if (line.Length == 0 || line[0] == '#') continue;
                    if (line.StartsWith("name=")) { p.Name = line.Substring(5).Trim(); continue; }
                    if (line.StartsWith("saved=")) {
                        if (DateTime.TryParseExact(line.Substring(6).Trim(), "yyyy-MM-ddTHH:mm:ss", CultureInfo.InvariantCulture, DateTimeStyles.None, out var d))
                            p.Saved = d;
                        continue;
                    }
                    if (line.StartsWith("vanilla=")) { p.Vanilla = line.Substring(8).Trim(); continue; }
                    if (line.StartsWith("tor=")) continue;
                    var f = line.Split('\t');
                    if (f.Length < 2 || !int.TryParse(f[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out int id)
                        || !int.TryParse(f[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out int sel)) continue;
                    p.Options.Add(new Entry { Id = id, Selection = sel, Value = f.Length > 2 ? f[2] : null, Name = f.Length > 3 ? f[3] : "" });
                }
                if (string.IsNullOrWhiteSpace(p.Name)) p.Name = System.IO.Path.GetFileNameWithoutExtension(path);
                return p;
            } catch (Exception e) { Warn($"{System.IO.Path.GetFileName(path)} unreadable: {e.Message}"); return null; }
        }

        private static void Write(Preset p) {
            var sb = new StringBuilder();
            sb.Append("# UTS settings preset (TOR - Forgotten Fixes). One option per line: id, selection, value, name.\n");
            sb.Append("name=").Append(CleanName(p.Name)).Append('\n');
            sb.Append("saved=").Append(p.Saved.ToString("yyyy-MM-ddTHH:mm:ss", CultureInfo.InvariantCulture)).Append('\n');
            string tor = "";
            try { tor = TheOtherRolesPlugin.VersionString; } catch { }
            sb.Append("tor=").Append(tor).Append('\n');
            if (!string.IsNullOrEmpty(p.Vanilla)) sb.Append("vanilla=").Append(p.Vanilla).Append('\n');
            foreach (var e in p.Options)
                sb.Append(e.Id.ToString(CultureInfo.InvariantCulture)).Append('\t')
                  .Append(e.Selection.ToString(CultureInfo.InvariantCulture)).Append('\t')
                  .Append(Field(e.Value)).Append('\t').Append(Field(e.Name)).Append('\n');
            Directory.CreateDirectory(Folder);
            string tmp = p.Path + ".tmp";
            File.WriteAllText(tmp, sb.ToString(), new UTF8Encoding(false));
            File.Move(tmp, p.Path, true);
        }

        private static string Field(string s) => (s ?? "").Replace('\t', ' ').Replace('\n', ' ').Replace('\r', ' ');

        internal static string CleanName(string name) {
            name = Regex.Replace(name ?? "", @"\s+", " ").Trim();
            name = new string(name.Where(c => !char.IsControl(c)).ToArray());
            return name.Length > MaxName ? name.Substring(0, MaxName).Trim() : name;
        }

        private static string FileFor(string name, string keep = null) {
            var invalid = System.IO.Path.GetInvalidFileNameChars();
            string stem = new string(CleanName(name).Select(c => invalid.Contains(c) ? '_' : c).ToArray()).Trim(' ', '.');
            if (stem.Length == 0) stem = "preset";
            string path = System.IO.Path.Combine(Folder, stem + Ext);
            for (int i = 2; File.Exists(path) && !SamePath(path, keep); i++)
                path = System.IO.Path.Combine(Folder, $"{stem} ({i}){Ext}");
            return path;
        }

        private static bool SamePath(string a, string b) =>
            b != null && string.Equals(System.IO.Path.GetFullPath(a), System.IO.Path.GetFullPath(b), StringComparison.OrdinalIgnoreCase);

        internal static bool NameTaken(string name, Preset except = null) =>
            List().Any(p => p != except && (except == null || !SamePath(p.Path, except.Path))
                            && string.Equals(p.Name, CleanName(name), StringComparison.OrdinalIgnoreCase));

        // ====================================================================
        // Snapshot of the current settings
        // ====================================================================
        private static string Plain(string s) => Regex.Replace(s ?? "", "<[^>]*>", "").Trim();

        internal static string ValueText(CustomOption o, int sel) {
            try {
                if (o.selections == null || sel < 0 || sel >= o.selections.Length) return "";
                return Plain(Convert.ToString(o.selections[sel], CultureInfo.InvariantCulture));
            } catch { return ""; }
        }

        private static List<CustomOption> AllOptions() =>
            CustomOption.options.Where(o => o != null && o.id != 0 && o.selections != null && o.selections.Length > 0)
                                .GroupBy(o => o.id).Select(g => g.First()).OrderBy(o => o.id).ToList();

        private static string VanillaNow() {
            try {
                if (GameManager.Instance == null || GameManager.Instance.LogicOptions == null) return null;
                return Convert.ToBase64String(GameOptionsManager.Instance.gameOptionsFactory.ToBytes(
                    GameManager.Instance.LogicOptions.currentGameOptions, false));
            } catch (Exception e) { Warn("game options not readable: " + e.Message); return null; }
        }

        private static void Fill(Preset p) {
            p.Options.Clear();
            foreach (var o in AllOptions())
                p.Options.Add(new Entry { Id = o.id, Selection = o.selection, Value = ValueText(o, o.selection), Name = Plain(o.name) });
            p.Vanilla = VanillaNow();
            p.Saved = DateTime.Now;
        }

        /// <summary>Saves the current settings as a new preset. Null name or an empty one: refused.</summary>
        internal static Preset SaveNew(string name) {
            name = CleanName(name);
            if (name.Length == 0) return null;
            try {
                var p = new Preset { Name = name, Path = FileFor(name) };
                Fill(p);
                Write(p);
                LastName = p.Name;
                Log($"saved '{p.Name}': {p.Options.Count} option(s){(p.Vanilla != null ? " + game options" : "")} -> {System.IO.Path.GetFileName(p.Path)}");
                return p;
            } catch (Exception e) { Warn($"save '{name}' failed: {e.Message}"); return null; }
        }

        /// <summary>Puts the current settings into an existing preset (keeps its name).</summary>
        internal static bool Overwrite(Preset p) {
            try {
                Fill(p);
                Write(p);
                LastName = p.Name;
                Log($"overwrote '{p.Name}': {p.Options.Count} option(s)");
                return true;
            } catch (Exception e) { Warn($"overwrite '{p?.Name}' failed: {e.Message}"); return false; }
        }

        internal static bool Rename(Preset p, string newName) {
            newName = CleanName(newName);
            if (p == null || newName.Length == 0) return false;
            try {
                string old = p.Name, oldPath = p.Path;
                var full = Read(p.Path) ?? p;
                full.Name = newName;
                full.Path = FileFor(newName, oldPath);
                Write(full);
                if (!SamePath(full.Path, oldPath) && File.Exists(oldPath)) File.Delete(oldPath);
                if (LastName == old) LastName = newName;
                p.Name = newName; p.Path = full.Path;
                Log($"renamed '{old}' -> '{newName}'");
                return true;
            } catch (Exception e) { Warn($"rename '{p.Name}' failed: {e.Message}"); return false; }
        }

        internal static bool Delete(Preset p) {
            try {
                if (p == null || !File.Exists(p.Path)) return false;
                File.Delete(p.Path);
                if (LastName == p.Name) LastName = null;
                Log($"deleted '{p.Name}'");
                return true;
            } catch (Exception e) { Warn($"delete '{p?.Name}' failed: {e.Message}"); return false; }
        }

        internal static void OpenFolder() {
            try {
                Directory.CreateDirectory(Folder);
                System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo { FileName = Folder, UseShellExecute = true });
            } catch (Exception e) { Warn("open folder: " + e.Message); }
        }

        // ====================================================================
        // Loading
        // ====================================================================
        private static int Resolve(CustomOption o, Entry e) {
            int n = o.selections.Length;
            if (e.Value != null) {
                if (e.Selection >= 0 && e.Selection < n && ValueText(o, e.Selection) == e.Value) return e.Selection;
                for (int i = 0; i < n; i++) if (ValueText(o, i) == e.Value) return i;
                // a float option whose steps moved a little (TOR adds its steps up, 1.6000003)
                if (float.TryParse(e.Value, NumberStyles.Float, CultureInfo.InvariantCulture, out float want)) {
                    int best = -1; float bestD = float.MaxValue;
                    for (int i = 0; i < n; i++) {
                        if (o.selections[i] is float f && Math.Abs(f - want) < bestD) { bestD = Math.Abs(f - want); best = i; }
                    }
                    if (best >= 0 && bestD < 0.01f) return best;
                }
            }
            return Mathf.Clamp(e.Selection, 0, n - 1);
        }

        /// <summary>How many options of the preset differ from the current settings (unknown ones not counted).</summary>
        internal static int Differences(Preset p) {
            if (p == null) return 0;
            var byId = AllOptions().ToDictionary(o => o.id);
            int d = 0;
            foreach (var e in p.Options)
                if (byId.TryGetValue(e.Id, out var o) && o.selection != Resolve(o, e)) d++;
            return d;
        }

        internal static bool CanLoad() {
            try {
                return AmongUsClient.Instance != null && AmongUsClient.Instance.AmHost && GameManager.Instance != null
                       && (LobbyScreen.Exists || SettingsPresetsUI.DiagActive);
            } catch { return false; }
        }

        internal static LoadResult Load(Preset p) {
            var r = new LoadResult();
            if (p == null) { r.Error = "no preset"; return r; }
            if (!CanLoad()) { r.Error = "host only, in the lobby"; return r; }
            var full = Read(p.Path);
            if (full == null) { r.Error = "file unreadable"; return r; }

            var byId = AllOptions().ToDictionary(o => o.id);
            var changed = new List<CustomOption>();
            var cfg = TheOtherRolesPlugin.Instance?.Config;
            bool saveOnSet = cfg != null && cfg.SaveOnConfigSet;
            try {
                if (cfg != null) cfg.SaveOnConfigSet = false;     // one write of TOR's config at the end, not one per option
                foreach (var e in full.Options) {
                    if (e.Id == 0) continue;                       // TOR's preset slot selector stays as it is
                    if (!byId.TryGetValue(e.Id, out var o)) { r.Unknown++; continue; }
                    int target = Resolve(o, e);
                    if (o.selection == target) { r.Same++; continue; }
                    o.selection = target;
                    try { if (o.entry != null) o.entry.Value = target; } catch { }
                    try {
                        var so = o.optionBehaviour != null ? o.optionBehaviour.TryCast<StringOption>() : null;
                        if (so != null) { so.oldValue = so.Value = target; so.ValueText.text = o.selections[target].ToString(); }
                    } catch { }
                    try { o.onChange?.Invoke(); } catch { }
                    changed.Add(o);
                }
            } finally {
                if (cfg != null) {
                    cfg.SaveOnConfigSet = saveOnSet;
                    try { if (changed.Count > 0) cfg.Save(); } catch (Exception e) { Warn("TOR config save: " + e.Message); }
                }
            }
            r.Changed = changed.Count;

            // the game's own settings (map, speed, cooldowns, ...): TOR's loadVanillaOptions path with our bytes
            if (!string.IsNullOrEmpty(full.Vanilla)) {
                try {
                    var opts = GameOptionsManager.Instance.gameOptionsFactory.FromBytes(Convert.FromBase64String(full.Vanilla));
                    if (opts != null && opts.Version >= 8) {
                        GameOptionsManager.Instance.GameHostOptions = opts;
                        GameOptionsManager.Instance.CurrentGameOptions = GameOptionsManager.Instance.GameHostOptions;
                        GameManager.Instance.LogicOptions.SetGameOptions(GameOptionsManager.Instance.CurrentGameOptions);
                        GameManager.Instance.LogicOptions.SyncOptions();
                        try { CustomOption.saveVanillaOptions(); } catch { }   // into TOR's current slot as well
                        r.VanillaApplied = true;
                    }
                } catch (Exception e) { Warn("game options not applied: " + e.Message); }
            }

            RefreshMenus();
            if (changed.Count > 0) r.Packets = PacedOptionSync.SendPaced(changed);
            LastName = full.Name;
            Log($"loaded '{full.Name}': {r.Changed} changed, {r.Same} unchanged, {r.Unknown} unknown (mod not installed)"
                + $"{(r.VanillaApplied ? ", game options applied" : "")}, {r.Packets} packet(s) to send.");
            return r;
        }

        // The settings menu, if it is open: every tab redrawn, like TOR's switchPreset does. TOR's patch class is
        // internal, so through reflection.
        private static void RefreshMenus() {
            try {
                var t = TorType.Get("TheOtherRoles.GameOptionsMenuStartPatch");
                var dictField = t?.GetField("currentGOMs", System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic);
                var update = t?.GetMethod("updateGameOptionsMenu", System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic);
                if (dictField?.GetValue(null) is not Dictionary<byte, GameOptionsMenu> goms || update == null) return;
                foreach (var kv in goms.ToList()) {
                    if (kv.Value == null) continue;
                    try { update.Invoke(null, new object[] { (CustomOption.CustomOptionType)kv.Key, kv.Value }); } catch { }
                }
            } catch { }
        }
    }
}
