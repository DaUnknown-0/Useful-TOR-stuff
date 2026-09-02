// Useful TOR Stuff - Copyright (C) 2026 DaUnknown-0
// Licensed under GPL-3.0-or-later. See LICENSE for details.

/*
 * PerfHud - the measuring stick for everything this mod family does per frame.
 *
 * The 2026-09-01 performance pass removed work that ran every frame and produced the same result
 * every frame (strings, Il2Cpp wrappers, object scans). None of that raises peak framerate; it
 * lowers allocation pressure, and the payoff is fewer GC hitches. An fps counter cannot show that,
 * so this HUD reports what can:
 *
 *   - frame time as an average AND as a 99th percentile plus the share of frames over 20 ms,
 *     because a hitch is a tail event that an average hides,
 *   - managed bytes allocated per second and gen-0 collections per second, which is the quantity
 *     the pass actually changed,
 *   - the scene and the player count, because a comparison only holds between equal scenes.
 *
 * Two scopes: "now" is the last half second, "run" is everything since the HUD was switched on or
 * reset. To compare two builds, run each for the same minute in the same scene and read the run
 * line, or read the summary line written to the BepInEx log on every reset and switch-off.
 *
 * Nothing measures until the key is pressed; idle, this costs one bool test and one GetKeyDown per
 * frame. The HUD must not distort what it measures, so it recomputes and rewrites its text twice a
 * second rather than per frame, and skips the assignment when the text has not changed.
 */

using System;
using System.Globalization;
using System.Text;
using BepInEx.Configuration;
using UnityEngine;
using UnityEngine.UI;

namespace UsefulTORStuff {

    public static class PerfHud {

        // ------------------------------------------------------------------------------------
        // Config
        // ------------------------------------------------------------------------------------
        private static ConfigEntry<bool> available;
        private static ConfigEntry<bool> showOnStart;
        private static ConfigEntry<string> toggleKeyName;
        private static KeyCode toggleKey = KeyCode.F10;

        public static void Bind(ConfigFile config) {
            available = config.Bind("PerfHud", "Enabled", true,
                "Make the performance HUD available. It stays hidden and measures nothing until the " +
                "toggle key is pressed; this only decides whether that key does anything.");
            showOnStart = config.Bind("PerfHud", "ShowOnStart", false,
                "Show the HUD from launch instead of waiting for the toggle key.");
            toggleKeyName = config.Bind("PerfHud", "ToggleKey", "F10",
                "Key that shows/hides the HUD. Hold shift with it to reset the run statistics and " +
                "write a summary line to the log. Any UnityEngine.KeyCode name.");

            if (!Enum.TryParse(toggleKeyName.Value, true, out toggleKey)) toggleKey = KeyCode.F10;
        }

        // ------------------------------------------------------------------------------------
        // State
        // ------------------------------------------------------------------------------------
        private const float WindowSeconds = 0.5f;
        private const float StallMs = 200f;   // a load, not a frame - kept out of the statistics
        private const float BucketMs = 0.25f;
        private const int Buckets = 400;      // 0 .. 100 ms

        private static bool visible;
        private static bool started;

        // Current half-second window.
        private static float windowStart;
        private static int windowFrames;
        private static float windowSumMs, windowMaxMs;
        private static long windowAllocMark;
        private static int windowGen0Mark;

        // Last completed window - the "now" line.
        private static float nowFps, nowAvgMs, nowMaxMs, nowAllocMbs, nowGen0PerSec;

        // Since switch-on or reset - the "run" line.
        private static float runStart;
        private static long runFrames;
        private static double runSumMs;
        private static long runAllocMark;
        private static int runGen0Mark;
        private static int runStalls, runOver20;
        private static readonly int[] hist = new int[Buckets];
        private static int histOver;

        private static readonly StringBuilder sb = new StringBuilder(400);
        private static string lastText = "";

        // UI
        private static GameObject root;
        private static TMPro.TextMeshProUGUI label;
        private static Sprite backdropSprite;

        // ------------------------------------------------------------------------------------
        // Per-frame entry point (PerfHudTicker)
        // ------------------------------------------------------------------------------------
        public static void Tick() {
            if (available == null || !available.Value) return;

            try {
                if (!started) {
                    started = true;
                    if (showOnStart != null && showOnStart.Value) Show();
                }

                if (Input.GetKeyDown(toggleKey)) {
                    bool shift = Input.GetKey(KeyCode.LeftShift) || Input.GetKey(KeyCode.RightShift);
                    if (shift) {
                        if (visible) { LogSummary("reset"); ResetRun(); }
                        else Show();
                    } else if (visible) {
                        LogSummary("off");
                        Hide();
                    } else Show();
                }
            } catch { }

            if (!visible) return;

            try { Sample(); } catch { }
        }

        private static void Sample() {
            float dtMs = Time.unscaledDeltaTime * 1000f;

            if (dtMs >= StallMs) {
                // A scene load or an alt-tab. Counted, but never mixed into averages or the
                // percentile - one four-second load would otherwise own the whole tail.
                runStalls++;
            } else {
                windowFrames++;
                windowSumMs += dtMs;
                if (dtMs > windowMaxMs) windowMaxMs = dtMs;

                runFrames++;
                runSumMs += dtMs;
                if (dtMs > 20f) runOver20++;

                int b = (int)(dtMs / BucketMs);
                if (b < 0) b = 0;
                if (b >= Buckets) histOver++; else hist[b]++;
            }

            float now = Time.realtimeSinceStartup;
            float elapsed = now - windowStart;
            if (elapsed < WindowSeconds) return;

            if (windowFrames > 0) {
                nowAvgMs = windowSumMs / windowFrames;
                nowFps = 1000f / Mathf.Max(nowAvgMs, 0.0001f);
                nowMaxMs = windowMaxMs;
            }

            long alloc = AllocatedBytes();
            int gen0 = GC.CollectionCount(0);
            nowAllocMbs = (float)((alloc - windowAllocMark) / 1048576.0 / elapsed);
            nowGen0PerSec = (gen0 - windowGen0Mark) / elapsed;
            windowAllocMark = alloc;
            windowGen0Mark = gen0;

            windowStart = now;
            windowFrames = 0;
            windowSumMs = 0f;
            windowMaxMs = 0f;

            Redraw(now, alloc, gen0);
        }

        // GC.GetAllocatedBytesForCurrentThread is exact and cheap and, since everything here runs on
        // the main thread, it is exactly the managed garbage this mod family produces. It counts the
        // .NET heap, which is where mod strings and Il2Cpp wrapper objects live - the Il2Cpp heap of
        // the game's own objects is not in it, and is not what the perf pass touched.
        private static long AllocatedBytes() {
            try { return GC.GetAllocatedBytesForCurrentThread(); } catch { return 0L; }
        }

        // ------------------------------------------------------------------------------------
        // Statistics
        // ------------------------------------------------------------------------------------
        private static float RunAvgMs() => runFrames > 0 ? (float)(runSumMs / runFrames) : 0f;

        /// Frame time at the given quantile, from the histogram. Returns a negative number when the
        /// bucket is past the histogram's range (over 100 ms), which the caller prints as ">100".
        private static float RunPercentileMs(double q) {
            if (runFrames <= 0) return 0f;
            long target = (long)(runFrames * q);
            long seen = 0;
            for (int i = 0; i < Buckets; i++) {
                seen += hist[i];
                if (seen >= target) return (i + 1) * BucketMs;
            }
            return -1f;
        }

        private static void ResetRun() {
            runStart = Time.realtimeSinceStartup;
            runFrames = 0;
            runSumMs = 0d;
            runStalls = 0;
            runOver20 = 0;
            histOver = 0;
            Array.Clear(hist, 0, hist.Length);
            runAllocMark = AllocatedBytes();
            runGen0Mark = GC.CollectionCount(0);

            windowStart = runStart;
            windowFrames = 0;
            windowSumMs = 0f;
            windowMaxMs = 0f;
            windowAllocMark = runAllocMark;
            windowGen0Mark = runGen0Mark;
        }

        // ------------------------------------------------------------------------------------
        // Scene label - a measurement is only comparable against the same scene and player count
        // ------------------------------------------------------------------------------------
        private static string Scene() {
            try {
                int players = 0;
                try { players = PlayerControl.AllPlayerControls.Count; } catch { }

                if (AmongUsClient.Instance != null
                    && AmongUsClient.Instance.GameState == InnerNet.InnerNetClient.GameStates.Started)
                    return "round " + players + "p";

                if (LobbyScreen.Exists) return "lobby " + players + "p";
            } catch { }
            return "menu";
        }

        private static string Clock(float seconds) {
            int s = Mathf.Max(0, (int)seconds);
            return (s / 60).ToString("00", CultureInfo.InvariantCulture) + ":"
                 + (s % 60).ToString("00", CultureInfo.InvariantCulture);
        }

        private static string F(float v, string fmt) => v.ToString(fmt, CultureInfo.InvariantCulture);

        // ------------------------------------------------------------------------------------
        // Text
        // ------------------------------------------------------------------------------------
        private static void Redraw(float now, long alloc, int gen0) {
            if (label == null) return;

            float runSec = Mathf.Max(now - runStart, 0.0001f);
            float runAvg = RunAvgMs();
            float runFps = runAvg > 0f ? 1000f / runAvg : 0f;
            float p99 = RunPercentileMs(0.99d);
            float runAllocMbs = (float)((alloc - runAllocMark) / 1048576.0 / runSec);
            float runGen0 = (gen0 - runGen0Mark) / runSec;
            float over20 = runFrames > 0 ? 100f * runOver20 / runFrames : 0f;

            sb.Clear();
            // mspace keeps the digits in fixed columns; without it every changing digit shifts the
            // rest of the line and the numbers are unreadable while they move.
            sb.Append("<mspace=0.55em>");
            sb.Append("UTS Perf ").Append(UsefulTORStuffPlugin.PluginVersion)
              .Append("  ").Append(Scene())
              .Append("  run ").Append(Clock(runSec)).Append('\n');

            sb.Append("now  ").Append(F(nowFps, "0.0")).Append(" fps  ")
              .Append(F(nowAvgMs, "0.0")).Append(" avg  ")
              .Append(F(nowMaxMs, "0.0")).Append(" worst ms  ")
              .Append(F(nowAllocMbs, "0.00")).Append(" MB/s  gen0 ")
              .Append(F(nowGen0PerSec, "0.0")).Append("/s\n");

            sb.Append("run  ").Append(F(runFps, "0.0")).Append(" fps  ")
              .Append(F(runAvg, "0.0")).Append(" avg  ");
            if (p99 < 0f) sb.Append(">100 p99 ms  ");
            else sb.Append(F(p99, "0.0")).Append(" p99 ms  ");
            sb.Append(F(runAllocMbs, "0.00")).Append(" MB/s  gen0 ")
              .Append(F(runGen0, "0.0")).Append("/s\n");

            sb.Append("     ").Append(F(over20, "0.0")).Append("% frames >20 ms   ")
              .Append(runStalls).Append(" stalls >").Append((int)StallMs).Append(" ms\n");

            sb.Append("     ").Append(toggleKey).Append(" hide   shift+").Append(toggleKey)
              .Append(" reset + log");

            string text = sb.ToString();
            if (text == lastText) return;
            lastText = text;
            label.text = text;
        }

        // The line to compare two builds with. Everything a run needs to be reproduced is on it:
        // build, scene, duration, and the two numbers the perf pass moves.
        private static void LogSummary(string reason) {
            try {
                float runSec = Mathf.Max(Time.realtimeSinceStartup - runStart, 0.0001f);
                if (runFrames < 30) return;   // too short to mean anything

                float runAvg = RunAvgMs();
                float p99 = RunPercentileMs(0.99d);
                float allocMbs = (float)((AllocatedBytes() - runAllocMark) / 1048576.0 / runSec);
                float gen0 = (GC.CollectionCount(0) - runGen0Mark) / runSec;
                float over20 = runFrames > 0 ? 100f * runOver20 / runFrames : 0f;

                UsefulTORStuffPlugin.Logger?.LogInfo(
                    $"[PerfHud] {reason} | UTS {UsefulTORStuffPlugin.PluginVersion} | {Scene()} | "
                    + $"{Clock(runSec)} | {F(runAvg > 0f ? 1000f / runAvg : 0f, "0.0")} fps avg | frame "
                    + $"{F(runAvg, "0.00")} ms avg, p99 {(p99 < 0f ? ">100" : F(p99, "0.00"))} ms, "
                    + $"{F(over20, "0.0")}% >20 ms | alloc {F(allocMbs, "0.00")} MB/s | "
                    + $"gen0 {F(gen0, "0.00")}/s | {runFrames} frames, {runStalls} stalls");
            } catch { }
        }

        // ------------------------------------------------------------------------------------
        // Show / hide
        // ------------------------------------------------------------------------------------
        public static void Show() {
            try {
                if (root == null) Build();
                if (root == null) return;
                root.SetActive(true);
                visible = true;
                lastText = "";
                ResetRun();
            } catch { }
        }

        public static void Hide() {
            visible = false;
            try { if (root != null) root.SetActive(false); } catch { }
        }

        private static Sprite Backdrop() {
            if (backdropSprite != null) return backdropSprite;
            var tex = new Texture2D(1, 1);
            tex.SetPixel(0, 0, new Color(0f, 0f, 0f, 0.72f));
            tex.Apply();
            backdropSprite = Sprite.Create(tex, new Rect(0, 0, 1, 1), new Vector2(0.5f, 0.5f));
            UnityEngine.Object.DontDestroyOnLoad(tex);
            UnityEngine.Object.DontDestroyOnLoad(backdropSprite);
            return backdropSprite;
        }

        // A screen-space canvas like UTSModSyncUI: this has to be readable in the main menu, the
        // lobby and a round, and only one of those has a HudManager. No GraphicRaycaster - the HUD
        // is never clicked, and one here would swallow clicks meant for the game underneath.
        private static void Build() {
            try {
                root = new GameObject("UTSPerfHud");
                UnityEngine.Object.DontDestroyOnLoad(root);

                var canvas = root.AddComponent<Canvas>();
                canvas.renderMode = RenderMode.ScreenSpaceOverlay;
                canvas.sortingOrder = 9800;
                var scaler = root.AddComponent<CanvasScaler>();
                scaler.uiScaleMode = CanvasScaler.ScaleMode.ScaleWithScreenSize;
                scaler.referenceResolution = new Vector2(1920, 1080);
                scaler.matchWidthOrHeight = 0.5f;

                var panel = new GameObject("Panel");
                panel.transform.SetParent(root.transform, false);
                var prt = panel.AddComponent<RectTransform>();
                prt.anchorMin = new Vector2(0f, 1f);
                prt.anchorMax = new Vector2(0f, 1f);
                prt.pivot = new Vector2(0f, 1f);
                prt.anchoredPosition = new Vector2(24, -24);
                prt.sizeDelta = new Vector2(560, 132);
                panel.AddComponent<Image>().sprite = Backdrop();

                var to = new GameObject("T");
                to.transform.SetParent(panel.transform, false);
                var trt = to.AddComponent<RectTransform>();
                trt.anchorMin = Vector2.zero;
                trt.anchorMax = Vector2.one;
                trt.sizeDelta = Vector2.zero;
                trt.offsetMin = new Vector2(10, 8);
                trt.offsetMax = new Vector2(-10, -8);

                label = to.AddComponent<TMPro.TextMeshProUGUI>();
                label.fontSize = 15;
                label.alignment = TMPro.TextAlignmentOptions.TopLeft;
                label.color = new Color(0.75f, 1f, 0.85f);
                label.richText = true;
                label.text = "UTS Perf";
            } catch (Exception ex) {
                UsefulTORStuffPlugin.Logger?.LogWarning($"[PerfHud] build failed: {ex.Message}");
                root = null;
                label = null;
            }
        }
    }

    public class PerfHudTicker : MonoBehaviour {
        public PerfHudTicker(IntPtr ptr) : base(ptr) { }

        public void Update() => PerfHud.Tick();
    }
}
