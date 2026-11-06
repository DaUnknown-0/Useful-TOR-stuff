// Useful TOR Stuff - Copyright (C) 2026 DaUnknown-0
// Licensed under GPL-3.0-or-later. See LICENSE for details.

/*
 * ModManagerDelete - the Mod Manager's DELETE button (2026-11-05): removes a mod together with everything
 * it brought along.
 *
 * What goes:
 *   - the plugin DLL, from where the chainloader actually loaded it (inside the plugins folder only),
 *   - the mod's own config files (<guid>.cfg and <guid>.*.cfg),
 *   - the folders the mod downloads or writes its assets to. A mod names them itself through the registry
 *     key "DeletePaths" (string[], relative to the BepInEx folder); releases that predate the key are
 *     covered by the KnownFolders table below.
 *
 * A running mod keeps its DLL open and may hold asset packs open as well. The DLL is renamed to
 * "<name>.dll.deleted" first (renaming a loaded DLL works on Windows, the updaters' ".old" relies on the
 * same), so BepInEx no longer sees it at the next start, then deleted when Windows lets go of it.
 * Whatever cannot be removed right now is written to config/UTSPendingDelete.txt and removed at the next
 * start (ProcessPending, first thing in Load). The mod does not load then, so nothing holds the files.
 *
 * Every path must lie strictly inside the BepInEx folder and is never one of its main folders (plugins,
 * config, core, ...) itself. The Mod Manager never offers to delete Useful TOR Stuff, which owns it.
 */

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using BepInEx;
using BepInEx.Unity.IL2CPP;

namespace UsefulTORStuff
{
    public static class ModManagerDelete
    {
        private const string PendingFileName = "UTSPendingDelete.txt";
        private const string DeletedSuffix = ".deleted";

        // Asset folders of releases that do not publish "DeletePaths" yet, relative to the BepInEx folder.
        private static readonly Dictionary<string, string[]> KnownFolders = new Dictionary<string, string[]>
        {
            { "com.tormod.unknownscollection", new[] { "uc-assets" } },
            { "com.daunknown0.atlas", new[] { "atlas-assets", "plugins/AtlasMaps" } },
            { "com.daunknown.unknownshalloween", new[] { "halloween-assets", "config/UnknownsHalloween" } },
            { "com.tormod.nightfall", new[] { "nightfall-models", "nightfall-reports", "nightfall-diag" } },
        };

        // Main folders of the install that no mod may take with it.
        private static readonly string[] Protected = { "plugins", "config", "core", "interop", "patchers", "unity-libs", "cache" };

        private static readonly HashSet<string> deletedThisSession = new HashSet<string>();

        private static string PendingFile => Path.Combine(Paths.ConfigPath, PendingFileName);

        public static bool CanDelete(ModInfo mod) =>
            mod != null && !string.IsNullOrEmpty(mod.Guid) && mod.Guid != UsefulTORStuffPlugin.PluginGuid;

        public static bool WasDeleted(string guid) => guid != null && deletedThisSession.Contains(guid);

        /// <summary>Everything DELETE would remove for this mod: existing files and folders, full paths.</summary>
        public static List<string> Targets(ModInfo mod)
        {
            var list = new List<string>();
            if (!CanDelete(mod)) return list;
            string dll = PluginDll(mod.Guid);
            if (dll != null) list.Add(dll);
            try
            {
                foreach (var f in Directory.GetFiles(Paths.ConfigPath, mod.Guid + ".cfg")) list.Add(Path.GetFullPath(f));
                foreach (var f in Directory.GetFiles(Paths.ConfigPath, mod.Guid + ".*.cfg")) list.Add(Path.GetFullPath(f));
            }
            catch { }
            string[] folders = mod.DeletePaths ?? (KnownFolders.TryGetValue(mod.Guid, out var known) ? known : Array.Empty<string>());
            foreach (var rel in folders)
            {
                if (string.IsNullOrWhiteSpace(rel)) continue;
                string full;
                try { full = Path.GetFullPath(Path.Combine(Paths.BepInExRootPath, rel)); }
                catch { continue; }
                if (IsSafe(full) && (Directory.Exists(full) || File.Exists(full))) list.Add(full);
            }
            return list.Where(IsSafe).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        }

        /// <summary>Total size of the targets in bytes (folders recursively).</summary>
        public static long Bytes(IEnumerable<string> targets)
        {
            long sum = 0;
            foreach (var t in targets)
            {
                try
                {
                    if (File.Exists(t)) sum += new FileInfo(t).Length;
                    else if (Directory.Exists(t))
                        foreach (var f in Directory.EnumerateFiles(t, "*", SearchOption.AllDirectories))
                            try { sum += new FileInfo(f).Length; } catch { }
                }
                catch { }
            }
            return sum;
        }

        /// <summary>Names of loaded plugins that hard-depend on this mod: they no longer load once it is gone.</summary>
        public static List<string> Dependents(string guid)
        {
            var names = new List<string>();
            try
            {
                foreach (var kv in IL2CPPChainloader.Instance.Plugins)
                {
                    var deps = kv.Value?.Dependencies;
                    if (deps == null) continue;
                    if (deps.Any(d => d.DependencyGUID == guid && (d.Flags & BepInDependency.DependencyFlags.HardDependency) != 0))
                        names.Add(kv.Value.Metadata?.Name ?? kv.Key);
                }
            }
            catch { }
            return names;
        }

        /// <summary>Removes the mod. Files still in use are queued for the next start.</summary>
        public static void Delete(ModInfo mod, out int removed, out int pending)
        {
            removed = 0; pending = 0;
            if (!CanDelete(mod)) return;
            var later = new List<string>();
            foreach (var target in Targets(mod))
            {
                string left = Remove(target);
                if (left == null) removed++;
                else { later.Add(left); pending++; }
            }
            if (later.Count > 0)
            {
                try { File.AppendAllLines(PendingFile, later); }
                catch (Exception ex) { UsefulTORStuffPlugin.Logger?.LogWarning($"[ModManager] could not queue {later.Count} path(s) for deletion: {ex.Message}"); }
            }
            deletedThisSession.Add(mod.Guid);
            UTSRestart.Mark();
            UsefulTORStuffPlugin.Logger?.LogInfo($"[ModManager] deleted {mod.Name} ({mod.Guid}): {removed} removed, {pending} queued for the next start");
        }

        /// <summary>Removes what an earlier session could not (call first thing in Load).</summary>
        public static void ProcessPending()
        {
            try
            {
                foreach (var f in Directory.GetFiles(Paths.PluginPath, "*.dll" + DeletedSuffix, SearchOption.AllDirectories))
                    try { File.Delete(f); } catch { }

                string file = PendingFile;
                if (!File.Exists(file)) return;
                DateTime queuedAt = File.GetLastWriteTimeUtc(file);
                var left = new List<string>();
                int removed = 0;
                foreach (var line in File.ReadAllLines(file))
                {
                    string path = line.Trim();
                    if (path.Length == 0) continue;
                    try { path = Path.GetFullPath(path); } catch { continue; }
                    if (!IsSafe(path)) continue;
                    // reinstalled since (Catalog tab, Mod Sync): a fresh DLL under the old name stays
                    if (path.EndsWith(".dll", StringComparison.OrdinalIgnoreCase) && File.Exists(path)
                        && File.GetLastWriteTimeUtc(path) > queuedAt) continue;
                    string rest = Remove(path);
                    if (rest == null) removed++; else left.Add(rest);
                }
                if (left.Count == 0) File.Delete(file);
                else File.WriteAllLines(file, left);
                UsefulTORStuffPlugin.Logger?.LogInfo($"[ModManager] pending deletions: {removed} removed, {left.Count} still in use");
            }
            catch (Exception ex)
            {
                UsefulTORStuffPlugin.Logger?.LogWarning($"[ModManager] pending deletions failed: {ex.Message}");
            }
        }

        // UI gallery: a dummy mod with two config files and an asset folder is deleted again; "config" and a path
        // outside the BepInEx folder must be refused.
        internal static string DiagSelfTest()
        {
            const string guid = "com.usefultorstuff.deletetest";
            string folder = Path.Combine(Paths.BepInExRootPath, "uts-delete-test");
            string cfg = Path.Combine(Paths.ConfigPath, guid + ".cfg");
            string cfg2 = Path.Combine(Paths.ConfigPath, guid + ".extra.cfg");
            try
            {
                Directory.CreateDirectory(Path.Combine(folder, "sub"));
                File.WriteAllText(Path.Combine(folder, "sub", "pack.bin"), "x");
                File.WriteAllText(cfg, "[General]");
                File.WriteAllText(cfg2, "[General]");
                var mod = new ModInfo { Guid = guid, Name = "Delete test", DeletePaths = new[] { "uts-delete-test", "config", "../outside" } };
                int found = Targets(mod).Count;
                Delete(mod, out int removed, out int pending);
                deletedThisSession.Remove(guid);
                bool gone = !Directory.Exists(folder) && !File.Exists(cfg) && !File.Exists(cfg2) && Directory.Exists(Paths.ConfigPath);
                return $"targets {found} (expect 3), removed {removed}, pending {pending}, gone {gone}";
            }
            catch (Exception ex) { return "failed: " + ex.Message; }
        }

        // Removes one file or folder. Returns null when it is gone, otherwise the path that is left over
        // (a DLL that could only be renamed is left as "<name>.dll.deleted").
        private static string Remove(string path)
        {
            try
            {
                if (File.Exists(path))
                {
                    if (path.EndsWith(".dll", StringComparison.OrdinalIgnoreCase))
                    {
                        string parked = path + DeletedSuffix;
                        try
                        {
                            if (File.Exists(parked)) File.Delete(parked);
                            File.Move(path, parked);
                            path = parked;
                        }
                        catch (Exception ex) { UsefulTORStuffPlugin.Logger?.LogWarning($"[ModManager] could not park {Path.GetFileName(path)}: {ex.Message}"); }
                    }
                    try { File.Delete(path); } catch { }
                    return File.Exists(path) ? path : null;
                }
                if (Directory.Exists(path))
                {
                    try { Directory.Delete(path, true); }
                    catch
                    {
                        // something inside is open: take what can go, the rest waits for the next start
                        foreach (var f in Directory.EnumerateFiles(path, "*", SearchOption.AllDirectories).ToList())
                            try { File.Delete(f); } catch { }
                        try { Directory.Delete(path, true); } catch { }
                    }
                    return Directory.Exists(path) ? path : null;
                }
            }
            catch (Exception ex)
            {
                UsefulTORStuffPlugin.Logger?.LogWarning($"[ModManager] could not delete {path}: {ex.Message}");
                return File.Exists(path) || Directory.Exists(path) ? path : null;
            }
            return null;
        }

        // Strictly inside the BepInEx folder and not one of its main folders.
        private static bool IsSafe(string full)
        {
            try
            {
                string root = Path.GetFullPath(Paths.BepInExRootPath).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
                    + Path.DirectorySeparatorChar;
                full = Path.GetFullPath(full);
                if (!full.StartsWith(root, StringComparison.OrdinalIgnoreCase)) return false;
                string rel = full.Substring(root.Length).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
                if (rel.Length == 0) return false;
                foreach (var p in Protected)
                    if (string.Equals(rel, p, StringComparison.OrdinalIgnoreCase)) return false;
                return true;
            }
            catch { return false; }
        }

        private static string PluginDll(string guid)
        {
            try
            {
                if (IL2CPPChainloader.Instance.Plugins.TryGetValue(guid, out var info) && info != null && !string.IsNullOrEmpty(info.Location))
                {
                    string full = Path.GetFullPath(info.Location);
                    if (full.EndsWith(".dll", StringComparison.OrdinalIgnoreCase) && File.Exists(full)
                        && full.StartsWith(Path.GetFullPath(Paths.PluginPath), StringComparison.OrdinalIgnoreCase))
                        return full;
                }
            }
            catch { }
            var entry = UTSModCatalog.ByGuid(guid);
            if (entry != null && !entry.External && File.Exists(entry.TargetPath)) return Path.GetFullPath(entry.TargetPath);
            return null;
        }
    }
}
