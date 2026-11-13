// Useful TOR Stuff - Copyright (C) 2026 DaUnknown-0
// Licensed under GPL-3.0-or-later. See LICENSE for details.

/*
 * ErrorToast - a mod logs an error: a small card says so, and one click copies what a bug report needs
 * (User 2026-11-13). Before this, an error lived only in LogOutput.log, which most players never open and
 * which BepInEx overwrites at the next start.
 *
 * A BepInEx log listener keeps the last 150 log lines of every source. An Error or Fatal line from a mod
 * (any plugin's own log source, or a Unity/Il2CppInterop exception whose text names one of the mods) is written to
 * BepInEx/ErrorReports/<time>_<source>.txt together with those 150 lines, and a card bottom right says
 * "<mod> had an error" with [Copy] (the whole report into the clipboard) and [X]. The card leaves after 10 s.
 * At most one card per source per minute; errors in between are counted on the card. At most 20 reports
 * are kept, the oldest go.
 *
 * The listener can be called from any thread, so it only queues; the card is built on the main thread.
 * Nothing is sent anywhere. Switch: [Interface] Error Toast in the config.
 */

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using BepInEx.Logging;
using Il2CppInterop.Runtime.Attributes;
using UnityEngine;
using UnityEngine.UI;

namespace UsefulTORStuff {

    internal sealed class ErrorToastListener : ILogListener {
        internal static readonly object Gate = new object();
        internal static readonly Queue<string> Ring = new Queue<string>();
        internal static readonly Queue<(string Source, string Text)> Pending = new Queue<(string, string)>();
        private const int RingSize = 150;
        private static bool selfTest, selfTestDone;   // between ModSelfTest's first "[SelfTest]" line and its "boot checks:" summary

        // the game's and the loaders' own sources: their errors are not ours to report
        private static readonly HashSet<string> Foreign = new HashSet<string>(StringComparer.OrdinalIgnoreCase) {
            "BepInEx", "Preloader", "Detour", "Chainloader", "Mono", "Interop",
        };
        private static readonly HashSet<string> UnitySources = new HashSet<string>(StringComparer.OrdinalIgnoreCase) {
            // Il2CppInterop reports an exception thrown inside a Harmony patch ("During invoking native->managed
            // trampoline"), which is the usual way a mod error shows up
            "Unity", "Unity Log", "IL2CPP", "Il2Cpp", "Il2CppInterop", "HarmonyX",
        };
        // a Unity exception counts when its text names one of these (namespaces of the mods); the first match names the
        // source, so the generic "Unknowns" prefix (every other Unknown's mod) comes last
        private static readonly string[] ModNames = {
            "TheOtherRoles", "UsefulTORStuff", "UnknownsCollection", "HostFixPlugin", "UnknownsAtlas", "Nightfall", "ChanceMod",
            "TOR_ChanceModifier", "ForceImpostorMod", "TrackerExport", "TOR_BypassTest", "UnknownsFeedback", "Unknowns",
        };

        public LogLevel LogLevelFilter => LogLevel.All;

        public void LogEvent(object sender, LogEventArgs e) {
            try {
                if (ErrorToast.Enabled != null && !ErrorToast.Enabled.Value) return;
                string source = e.Source?.SourceName ?? "?";
                string text = e.Data?.ToString() ?? "";
                lock (Gate) {
                    Ring.Enqueue($"[{e.Level,-7}:{source,10}] {text}");
                    while (Ring.Count > RingSize) Ring.Dequeue();
                    // ModSelfTest's boot checks send deliberately broken RPCs and throw probes on purpose; their
                    // caught errors are expected (2026-11-13: the card popped up with "+17 more" during a test)
                    if (text.IndexOf("[SelfTest]", StringComparison.Ordinal) >= 0) {
                        // the window opens at the first line and closes for good at the summary; the lines that
                        // follow it ("mods found", "report written") must not open it again
                        if (text.IndexOf("boot checks:", StringComparison.Ordinal) >= 0) { selfTest = false; selfTestDone = true; }
                        else if (!selfTestDone) selfTest = true;
                        return;
                    }
                    if (selfTest || text.IndexOf("ModSelfTest probe", StringComparison.Ordinal) >= 0) return;
                    if ((e.Level & (LogLevel.Error | LogLevel.Fatal)) == 0) return;
                    if (source == ErrorToast.OwnSource || Foreign.Contains(source)) return;
                    if (UnitySources.Contains(source)) {
                        if (text.IndexOf("Exception", StringComparison.Ordinal) < 0) return;
                        if (!ModNames.Any(m => text.IndexOf(m, StringComparison.Ordinal) >= 0)) return;
                        source = ModNames.First(m => text.IndexOf(m, StringComparison.Ordinal) >= 0);
                    }
                    if (Pending.Count < 20) Pending.Enqueue((source, text));
                }
            } catch { }
        }

        public void Dispose() { }
    }

    public class ErrorToast : MonoBehaviour {
        public static ErrorToast Instance { get; private set; }
        public ErrorToast(IntPtr ptr) : base(ptr) { }

        internal static BepInEx.Configuration.ConfigEntry<bool> Enabled;
        internal const string OwnSource = "UTS-ErrorToast";
        private const int KeepReports = 20;
        private const long ShowMs = 10000, PerSourceMs = 60000;

        private static readonly Dictionary<string, long> lastCard = new Dictionary<string, long>();
        private GameObject root;
        private TMPro.TextMeshProUGUI body;
        private string cardSource, cardReport;
        private int extra;
        private long hideAt;

        internal static void Install(BepInEx.Configuration.ConfigFile config) {
            Enabled = config.Bind("Interface", "Error Toast", true,
                "When a mod logs an error, show a small card with a button that copies an error report (also saved in BepInEx/ErrorReports).");
            try { BepInEx.Logging.Logger.Listeners.Add(new ErrorToastListener()); } catch { }
        }

        public void Awake() {
            if (Instance) Destroy(Instance);
            Instance = this;
        }

        public void Update() {
            try {
                long now = Environment.TickCount64;
                if (root != null && root.activeSelf && now >= hideAt) root.SetActive(false);
                (string Source, string Text) item;
                lock (ErrorToastListener.Gate) {
                    if (ErrorToastListener.Pending.Count == 0) return;
                    item = ErrorToastListener.Pending.Dequeue();
                }
                Handle(item.Source, item.Text, now);
            } catch { }
        }

        [HideFromIl2Cpp]
        private void Handle(string source, string text, long now) {
            // the same source again within a minute: count it on the card that is (or was) up
            if (lastCard.TryGetValue(source, out long at) && now - at < PerSourceMs) {
                if (source == cardSource && root != null && root.activeSelf) { extra++; Render(text); hideAt = now + ShowMs; }
                return;
            }
            lastCard[source] = now;
            cardSource = source;
            extra = 0;
            cardReport = SaveReport(source, text);
            if (root == null) Build();
            if (root == null) return;
            root.SetActive(true);
            hideAt = now + ShowMs;
            Render(text);
        }

        [HideFromIl2Cpp]
        private void Render(string text) {
            string first = (text ?? "").Split('\n')[0].Replace("<", "").Replace(">", "").Trim();
            if (first.Length > 140) first = first.Substring(0, 137) + "...";
            string more = extra > 0 ? $"\n<color=#8C9AAD>{UTSLocalization.Tr("uts.errortoast.more", extra)}</color>" : "";
            VanillaUI.SetText(body, $"<b>{UTSLocalization.Tr("uts.errortoast.title", cardSource)}</b>\n<size=80%><color=#C9D2DE>{first}</color></size>{more}");
        }

        [HideFromIl2Cpp]
        private static string SaveReport(string source, string text) {
            string report;
            lock (ErrorToastListener.Gate) {
                report = $"Error report {DateTime.Now:yyyy-MM-dd HH:mm:ss}, source: {source}\n"
                       + $"Mods: {ModList()}\n\n--- error ---\n{text}\n\n--- last {ErrorToastListener.Ring.Count} log lines ---\n"
                       + string.Join("\n", ErrorToastListener.Ring);
            }
            try {
                string dir = Path.Combine(BepInEx.Paths.BepInExRootPath, "ErrorReports");
                Directory.CreateDirectory(dir);
                string safe = new string(source.Select(c => char.IsLetterOrDigit(c) ? c : '_').ToArray());
                File.WriteAllText(Path.Combine(dir, $"{DateTime.Now:yyyyMMdd-HHmmss}_{safe}.txt"), report);
                foreach (var old in new DirectoryInfo(dir).GetFiles("*.txt").OrderByDescending(f => f.LastWriteTimeUtc).Skip(KeepReports))
                    try { old.Delete(); } catch { }
            } catch { }
            return report;
        }

        [HideFromIl2Cpp]
        private static string ModList() {
            try {
                return string.Join(", ", BepInEx.Unity.IL2CPP.IL2CPPChainloader.Instance.Plugins.Values
                    .Select(p => $"{p.Metadata.Name} {p.Metadata.Version}"));
            } catch { return "?"; }
        }

        [HideFromIl2Cpp]
        private void Copy() {
            try { GUIUtility.systemCopyBuffer = cardReport ?? ""; } catch { }
            VanillaUI.SetText(body, $"<b>{UTSLocalization.Tr("uts.errortoast.copied")}</b>\n<size=80%><color=#C9D2DE>{UTSLocalization.Tr("uts.errortoast.where")}</color></size>");
            hideAt = Environment.TickCount64 + 4000;
        }

        [HideFromIl2Cpp]
        private void Build() {
            try {
                root = VanillaUI.Canvas("UTSErrorToast", 9800, false);
                var card = VanillaUI.Panel(root, new Vector2(560, 150), new Vector2(1, 0), new Vector2(1, 0), new Vector2(-24, 24));
                body = VanillaUI.Text(card, "", 20, Color.white, new Vector2(20, -16), new Vector2(520, 80));
                body.enableWordWrapping = true;
                VanillaUI.Button(card, UTSLocalization.Tr("uts.errortoast.copy"), new Vector2(-84, 16), new Vector2(180, 44), VanillaUI.Teal, Copy,
                    new Vector2(1, 0), new Vector2(1, 0), new Vector2(1, 0));
                VanillaUI.Button(card, "X", new Vector2(-20, 16), new Vector2(52, 44), VanillaUI.Red, () => { if (root != null) root.SetActive(false); },
                    new Vector2(1, 0), new Vector2(1, 0), new Vector2(1, 0));
            } catch (Exception e) {
                // no logging here: an error from the toast itself would only call the toast again
                if (root != null) Destroy(root);
                root = null;
                _ = e;
            }
        }
    }
}
