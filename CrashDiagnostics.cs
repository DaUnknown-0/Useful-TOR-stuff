// Useful TOR Stuff - Copyright (C) 2026 DaUnknown-0
// Licensed under GPL-3.0-or-later. See LICENSE for details.

/*
 * CrashDiagnostics - makes a hard crash on ANY client leave something behind to read.
 *
 * THE PROBLEM THIS SOLVES (2026-08-29)
 * ------------------------------------
 * Other players' games died that evening - in the lobby, walking around in a round - and there was
 * nothing to look at. Not because nothing was logged, but because of two defaults:
 *
 *   1. BepInEx overwrites LogOutput.log on every launch ([Logging.Disk] AppendLog = false). A
 *      player who crashes and restarts to rejoin has, by the time anyone asks, already deleted the
 *      only record of the crash.
 *   2. A native crash produces no dump unless Windows Error Reporting has been told to keep one.
 *      On this machine it has (AppData\Local\CrashDumps held seven of them, and they were what
 *      finally pointed at the DetourWatchdog); on everyone else's it has not.
 *
 * Both are settings, not code, and both can be put in place by the plugin on its own machine:
 *
 *   - AppendLog is flipped to true in BepInEx.cfg. That file is BepInEx's, not ours, so the edit is
 *     the smallest possible one: the single key inside the single section, everything else copied
 *     through byte for byte. It takes effect on the NEXT launch, which is fine - the crash worth
 *     reading is never the current session's. The log then grows across sessions (roughly a
 *     megabyte per hour of play on this install); a player can delete it whenever they like.
 *   - A LocalDumps entry for "Among Us.exe" is written under HKEY_CURRENT_USER. Per-user, no
 *     elevation, no effect on any other program: WER consults the per-process key only for that
 *     executable. DumpType 1 is a minidump (10-20 MB here), DumpCount 5 keeps it from piling up.
 *     Deleting the key restores the old behaviour; the plugin never deletes it itself.
 *
 * And a third thing, for the crash class no dump explains well: a 32-bit process running out of
 * address space. The plugin logs its private bytes on a slow timer and shouts once it passes a
 * threshold, so an OutOfMemory death reads as "memory climbed to 1,7 GB in the minutes before"
 * instead of as a bare exception from whichever allocation happened to lose.
 *
 * Everything here is opt-out via BepInEx config. None of it is gated on the host: it is about the
 * local machine's own crash, and it must keep working in a lobby whose host runs nothing at all.
 */

using System;
using System.Diagnostics;
using System.IO;
using System.Text;
using BepInEx;
using BepInEx.Configuration;
using Microsoft.Win32;
using UnityEngine;
using UnityEngine.Profiling;

namespace UsefulTORStuff {

    public static class CrashDiagnostics {
        private static ConfigEntry<bool> appendLog;
        private static ConfigEntry<bool> werDumps;
        private static ConfigEntry<float> memoryLogInterval;
        private static ConfigEntry<int> memoryWarnMb;
        private static ConfigEntry<bool> breakdown;
        private static ConfigEntry<int> breakdownGrowthMb;

        public static void Bind(ConfigFile config) {
            appendLog = config.Bind("CrashDiagnostics", "KeepBepInExLogAcrossSessions", true,
                "Set [Logging.Disk] AppendLog = true in BepInEx.cfg so a crash's log survives the " +
                "restart that follows it. Applied once, effective from the next launch.");
            werDumps = config.Bind("CrashDiagnostics", "WriteCrashDumps", true,
                "Register Among Us.exe for Windows Error Reporting LocalDumps under HKEY_CURRENT_USER " +
                "so a hard crash leaves a minidump in %LOCALAPPDATA%\\CrashDumps. Per-user, no admin.");
            memoryLogInterval = config.Bind("CrashDiagnostics", "MemoryLogIntervalSeconds", 30f,
                "How often to log the process's private bytes (0 = never). The game is a 32-bit " +
                "process; the trend before an OutOfMemory crash is the only evidence there is.");
            memoryWarnMb = config.Bind("CrashDiagnostics", "MemoryWarnMB", 1500,
                "Private bytes above which every memory log line becomes a warning.");
            breakdown = config.Bind("CrashDiagnostics", "MemoryBreakdown", true,
                "On every scene change (and whenever private bytes grew by BreakdownGrowthMB since the " +
                "last one) log Unity's own memory counters and the largest textures, audio clips and " +
                "meshes by name, so a memory jump can be attributed to whatever loaded it.");
            breakdownGrowthMb = config.Bind("CrashDiagnostics", "BreakdownGrowthMB", 200,
                "Growth of private bytes since the last breakdown that triggers another one.");
        }

        /// One-shot, at load. Each step is independent and failure-tolerant: a locked config file
        /// or a registry policy must never stop the plugin from loading.
        public static void Install() {
            if (appendLog?.Value == true) EnsureAppendLog();
            if (werDumps?.Value == true) EnsureLocalDumps();
            LogAddressSpace();
        }

        // ------------------------------------------------------------------------------------
        // BepInEx.cfg: AppendLog = true
        // ------------------------------------------------------------------------------------
        private static void EnsureAppendLog() {
            try {
                string path = Path.Combine(Paths.ConfigPath, "BepInEx.cfg");
                if (!File.Exists(path)) return;

                var lines = File.ReadAllLines(path);
                bool inSection = false, changed = false, keySeen = false;
                int sectionEnd = -1;
                for (int i = 0; i < lines.Length; i++) {
                    string t = lines[i].Trim();
                    if (t.StartsWith("[")) {
                        if (inSection) { sectionEnd = i; break; }
                        inSection = t.Equals("[Logging.Disk]", StringComparison.OrdinalIgnoreCase);
                        continue;
                    }
                    if (!inSection || !t.StartsWith("AppendLog", StringComparison.OrdinalIgnoreCase)) continue;
                    keySeen = true;
                    int eq = t.IndexOf('=');
                    if (eq < 0) continue;
                    string value = t.Substring(eq + 1).Trim();
                    if (value.Equals("true", StringComparison.OrdinalIgnoreCase)) return;   // already set
                    lines[i] = "AppendLog = true";
                    changed = true;
                    break;
                }
                if (!keySeen) {
                    // The section exists (BepInEx writes every key on first run), but be safe: add the
                    // key at the end of the section rather than doing nothing silently.
                    if (!inSection && sectionEnd < 0) return;   // no [Logging.Disk] at all: not our file to invent
                    var list = new System.Collections.Generic.List<string>(lines);
                    int at = sectionEnd < 0 ? list.Count : sectionEnd;
                    list.Insert(at, "AppendLog = true");
                    lines = list.ToArray();
                    changed = true;
                }
                if (!changed) return;

                // Atomic replace: write beside, then swap, so a crash mid-write cannot leave BepInEx
                // with half a config.
                string tmp = path + ".uts-tmp";
                File.WriteAllLines(tmp, lines, new UTF8Encoding(false));
                File.Copy(tmp, path, true);
                File.Delete(tmp);
                UsefulTORStuffPlugin.Logger?.LogInfo(
                    "[CrashDiagnostics] BepInEx.cfg: AppendLog set to true - from the next launch on, " +
                    "LogOutput.log keeps earlier sessions (including the one that crashed).");
            } catch (Exception e) {
                UsefulTORStuffPlugin.Logger?.LogWarning($"[CrashDiagnostics] could not update BepInEx.cfg: {e.GetType().Name}: {e.Message}");
            }
        }

        // ------------------------------------------------------------------------------------
        // WER LocalDumps for Among Us.exe (HKCU)
        // ------------------------------------------------------------------------------------
        private static void EnsureLocalDumps() {
            try {
                string exe;
                try { exe = Path.GetFileName(Process.GetCurrentProcess().MainModule?.FileName ?? "Among Us.exe"); }
                catch { exe = "Among Us.exe"; }

                using var key = Registry.CurrentUser.CreateSubKey(
                    @"Software\Microsoft\Windows\Windows Error Reporting\LocalDumps\" + exe);
                if (key == null) return;

                // Idempotent: only touch what differs, and say so once.
                bool changed = false;
                if (!(key.GetValue("DumpType") is int dt) || dt != 1) { key.SetValue("DumpType", 1, RegistryValueKind.DWord); changed = true; }
                if (!(key.GetValue("DumpCount") is int dc) || dc < 5) { key.SetValue("DumpCount", 5, RegistryValueKind.DWord); changed = true; }
                if (key.GetValue("DumpFolder") == null) {
                    key.SetValue("DumpFolder", @"%LOCALAPPDATA%\CrashDumps", RegistryValueKind.ExpandString);
                    changed = true;
                }
                if (changed)
                    UsefulTORStuffPlugin.Logger?.LogInfo(
                        $"[CrashDiagnostics] Windows Error Reporting will keep a minidump of {exe} in " +
                        "%LOCALAPPDATA%\\CrashDumps on a hard crash (HKCU LocalDumps, per user).");
            } catch (Exception e) {
                UsefulTORStuffPlugin.Logger?.LogWarning($"[CrashDiagnostics] could not register LocalDumps: {e.GetType().Name}: {e.Message}");
            }
        }

        // ------------------------------------------------------------------------------------
        // Memory: one line at start, then a slow heartbeat
        // ------------------------------------------------------------------------------------
        private static void LogAddressSpace() {
            try {
                UsefulTORStuffPlugin.Logger?.LogInfo(
                    $"[CrashDiagnostics] {(Environment.Is64BitProcess ? "64-bit" : "32-bit")} process on a " +
                    $"{(Environment.Is64BitOperatingSystem ? "64-bit" : "32-bit")} OS; " +
                    $"private bytes at load: {PrivateMb():0} MB. Memory heartbeat every " +
                    $"{memoryLogInterval?.Value ?? 0:0}s, warning above {memoryWarnMb?.Value ?? 0} MB.");
            } catch { }
        }

        private static float PrivateMb() {
            using var p = Process.GetCurrentProcess();
            return p.PrivateMemorySize64 / (1024f * 1024f);
        }

        /// Called from the ticker component below. Time-based, not frame-based, so it costs the
        /// same at 30 and at 144 fps and is silent when the interval is 0.
        private static float nextMemoryLog;
        private static float peakMb;
        internal static void MemoryTick() {
            try {
                float interval = memoryLogInterval?.Value ?? 0f;
                if (interval <= 0f) return;
                if (Time.realtimeSinceStartup < nextMemoryLog) return;
                nextMemoryLog = Time.realtimeSinceStartup + Mathf.Max(5f, interval);

                float mb = PrivateMb();
                if (mb > peakMb) peakMb = mb;
                long managed = GC.GetTotalMemory(false) / (1024 * 1024);
                string unity = "";
                try {
                    // Unity's own view of the same process: what its allocators hold (native objects:
                    // textures, meshes, audio), what they have reserved from the OS, and what sits in
                    // the graphics driver. The gap between private bytes and these is everything else
                    // (the .NET runtime, JIT code, Il2Cpp, DLLs).
                    unity = $", unity alloc {Profiler.GetTotalAllocatedMemoryLong() >> 20} / reserved " +
                            $"{Profiler.GetTotalReservedMemoryLong() >> 20} MB, gfx driver " +
                            $"{Profiler.GetAllocatedMemoryForGraphicsDriver() >> 20} MB";
                } catch { }
                string line = $"[CrashDiagnostics] memory: {mb:0} MB private ({managed} MB managed heap{unity}), peak {peakMb:0} MB";
                int warn = memoryWarnMb?.Value ?? int.MaxValue;
                if (mb >= warn)
                    UsefulTORStuffPlugin.Logger?.LogWarning(line + $" - above {warn} MB in a 32-bit process; an OutOfMemory crash is close.");
                else
                    UsefulTORStuffPlugin.Logger?.LogInfo(line);

                MaybeBreakdown(mb);
            } catch { }
        }

        // ------------------------------------------------------------------------------------
        // Breakdown: who owns the memory? Largest Unity objects by name.
        // ------------------------------------------------------------------------------------
        // The heartbeat says HOW MUCH; this says WHAT. Measured 2026-08-29 on the host: joining a
        // lobby took the process from 323 to 1251 MB, a round to 1493 MB, with Nightfall off and no
        // watchdog churn - and two of the crash dumps sit exactly on such jumps. A sum cannot be
        // acted on; a list of the biggest textures, clips and meshes with their names can, because
        // every mod names its assets (UC animation frames, hats, TOR sprites, Nightfall's world).
        // Runs on a scene change or after BreakdownGrowthMB of growth, never per frame:
        // FindObjectsOfTypeAll walks every loaded object of the type.
        private static string lastScene = "";
        private static float lastBreakdownMb = -1f;

        private static void MaybeBreakdown(float mb) {
            if (breakdown?.Value != true) return;
            string scene;
            try { scene = UnityEngine.SceneManagement.SceneManager.GetActiveScene().name ?? ""; } catch { scene = "?"; }
            bool sceneChanged = scene != lastScene;
            bool grew = lastBreakdownMb >= 0 && mb - lastBreakdownMb >= (breakdownGrowthMb?.Value ?? 200);
            if (!sceneChanged && !grew) return;
            lastScene = scene;
            lastBreakdownMb = mb;
            try {
                UsefulTORStuffPlugin.Logger?.LogInfo($"[CrashDiagnostics] breakdown in scene '{scene}' at {mb:0} MB private" +
                                                     (sceneChanged ? " (scene change)" : " (growth)") + ":");
                // The group key is what makes the list actionable. Mod-loaded textures have NO name
                // (every LoadImage path in this family creates them nameless - measured: the twelve
                // biggest textures in a lobby were all unnamed), so those are grouped by their shape
                // and format instead: "unnamed 115x115 ARGB32 mips x368" points straight at a loader.
                Report<Texture2D>("Texture2D",
                    t => $"{t.width}x{t.height} {t.format}{(t.mipmapCount > 1 ? " mips" : "")}",
                    t => string.IsNullOrEmpty(t.name) ? $"unnamed {t.width}x{t.height} {t.format}{(t.mipmapCount > 1 ? " mips" : "")}" : t.name);
                Report<RenderTexture>("RenderTexture", t => $"{t.width}x{t.height}", t => t.name);
                Report<AudioClip>("AudioClip", c => $"{c.length:0.0}s {c.channels}ch {c.frequency}Hz {c.loadType}", c => c.name);
                Report<Mesh>("Mesh", m => $"{m.vertexCount} verts", m => m.name);
            } catch (Exception e) {
                UsefulTORStuffPlugin.Logger?.LogWarning($"[CrashDiagnostics] breakdown failed: {e.GetType().Name}: {e.Message}");
            }
        }

        private const int TopN = 12;

        private static void Report<T>(string label, Func<T, string> describe, Func<T, string> groupKey) where T : UnityEngine.Object {
            try {
                var all = Resources.FindObjectsOfTypeAll(Il2CppInterop.Runtime.Il2CppType.Of<T>());
                if (all == null) return;
                long total = 0; int count = 0;
                var top = new System.Collections.Generic.List<(long size, string text)>();
                var groups = new System.Collections.Generic.Dictionary<string, (long size, int n)>();
                foreach (var o in all) {
                    if (o == null) continue;
                    var t = o.TryCast<T>();
                    if (t == null) continue;
                    long size;
                    try { size = Profiler.GetRuntimeMemorySizeLong(t); } catch { continue; }
                    total += size; count++;
                    string key;
                    try { key = groupKey(t) ?? ""; } catch { key = "?"; }
                    groups[key] = groups.TryGetValue(key, out var g) ? (g.size + size, g.n + 1) : (size, 1);
                    if (size < (1 << 20) && top.Count >= TopN) continue;   // below 1 MB: only while the list is short
                    string desc;
                    try { desc = describe(t); } catch { desc = "?"; }
                    top.Add((size, $"{size >> 10,8} KB  {t.name} ({desc})"));
                }
                top.Sort((a, b) => b.size.CompareTo(a.size));
                var sb = new StringBuilder();
                sb.Append($"[CrashDiagnostics]   {label}: {count} object(s), {total >> 20} MB total");
                for (int i = 0; i < top.Count && i < TopN; i++) sb.Append("\n      ").Append(top[i].text);
                // The groups are the part that names a culprit: the sum of 368 small frames shows up
                // here and nowhere in the top-12 list.
                var byGroup = new System.Collections.Generic.List<(string key, long size, int n)>();
                foreach (var kv in groups) byGroup.Add((kv.Key, kv.Value.size, kv.Value.n));
                byGroup.Sort((a, b) => b.size.CompareTo(a.size));
                sb.Append("\n      -- largest groups (by name, or by shape for unnamed) --");
                for (int i = 0; i < byGroup.Count && i < 15; i++)
                    sb.Append($"\n      {byGroup[i].size >> 10,8} KB  {byGroup[i].key}  x{byGroup[i].n}");
                UsefulTORStuffPlugin.Logger?.LogInfo(sb.ToString());
            } catch (Exception e) {
                UsefulTORStuffPlugin.Logger?.LogWarning($"[CrashDiagnostics]   {label}: failed: {e.GetType().Name}: {e.Message}");
            }
        }
    }

    /// The heartbeat's driver. A component on the plugin's own GameObject rather than a Harmony
    /// postfix, for the reason NewcomerShield gives: a throwing patch elsewhere in a shared chain
    /// can silently switch a postfix off, and this one must keep ticking right up to the crash.
    public class CrashDiagnosticsTicker : MonoBehaviour {
        public CrashDiagnosticsTicker(IntPtr ptr) : base(ptr) { }

        public void Update() => CrashDiagnostics.MemoryTick();
    }
}
