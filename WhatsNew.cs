// Useful TOR Stuff - Copyright (C) 2026 DaUnknown-0
// Licensed under GPL-3.0-or-later. See LICENSE for details.

/*
 * WhatsNew - after an update, the main menu shows what is new in each updated mod (User 2026-11-13).
 *
 * The versions of the mods in the UTS catalog (UTSModCatalog, own repositories only) are remembered in the
 * config ([Interface] Seen Versions). At the first main menu of a start, every mod whose loaded version
 * differs from the remembered one gets its release notes from the GitHub Atom feed (ReleaseFeed, the same
 * source the updaters read, not counted against the API limit) and a panel shows them, one mod after the
 * other: name, version, the notes (cut after about 40 lines) and a button to the release page.
 * A fresh install (nothing remembered yet) only remembers and shows nothing. A release without notes, a
 * private repository or no network: nothing is shown for that mod, the version is remembered anyway.
 * Switch: [Interface] Whats New.
 */

using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using Il2CppInterop.Runtime.Attributes;
using UnityEngine;
using UnityEngine.Networking;

namespace UsefulTORStuff {

    public class WhatsNew : MonoBehaviour {
        public static WhatsNew Instance { get; private set; }
        public WhatsNew(IntPtr ptr) : base(ptr) { }

        internal static BepInEx.Configuration.ConfigEntry<bool> Enabled;
        internal static BepInEx.Configuration.ConfigEntry<string> Seen;
        private const int MaxLines = 40, MaxChars = 2600;

        private sealed class Item { public string Name, Version, Notes, Url; }
        private readonly List<Item> queue = new List<Item>();
        private GameObject root;
        private static bool started;

        internal static void Bind(BepInEx.Configuration.ConfigFile config) {
            Enabled = config.Bind("Interface", "Whats New", true, "After an update, show the release notes of each updated mod in the main menu.");
            Seen = config.Bind("Interface", "Seen Versions", "", "Mod versions the what's-new panel has already handled (guid=version;...).");
        }

        public void Awake() {
            if (Instance) Destroy(Instance);
            Instance = this;
        }

        public void Update() {
            try {
                if (root != null) {
                    if (MainMenu() == null) { Close(); return; }
                    if (Input.GetKeyDown(KeyCode.Escape)) Next();
                    return;
                }
                if (started || Enabled == null || !Enabled.Value) return;
                if (MainMenu() == null) return;
                started = true;
                BepInEx.Unity.IL2CPP.Utils.MonoBehaviourExtensions.StartCoroutine(this, Collect());
            } catch { }
        }

        [HideFromIl2Cpp]
        private static MainMenuManager MainMenu() {
            try { return FindObjectOfType<MainMenuManager>(); } catch { return null; }
        }

        [HideFromIl2Cpp]
        private static Dictionary<string, string> ParseSeen() {
            var d = new Dictionary<string, string>();
            foreach (var part in (Seen?.Value ?? "").Split(';')) {
                int eq = part.IndexOf('=');
                if (eq > 0) d[part.Substring(0, eq)] = part.Substring(eq + 1);
            }
            return d;
        }

        [HideFromIl2Cpp]
        private IEnumerator Collect() {
            var seen = ParseSeen();
            bool fresh = seen.Count == 0;
            var changed = new List<(CatalogEntry Entry, string Version)>();
            // the exact version (4 parts for test builds) from the Mod Manager registry; BepInEx keeps only 3
            var registry = new Dictionary<string, string>();
            try {
                foreach (var m in ModManagerRegistry.GetAllMods())
                    if (m != null && !string.IsNullOrEmpty(m.Guid) && m.Version != null) registry[m.Guid] = m.Version.ToString();
            } catch { }
            foreach (var e in UTSModCatalog.Entries) {
                if (e.External) continue;
                string ver = null;
                if (!registry.TryGetValue(e.Guid, out ver)) {
                    try {
                        if (BepInEx.Unity.IL2CPP.IL2CPPChainloader.Instance.Plugins.TryGetValue(e.Guid, out var info) && info?.Metadata != null)
                            ver = info.Metadata.Version.ToString();
                    } catch { }
                }
                if (ver == null) continue;
                if (seen.TryGetValue(e.Guid, out var old) && old == ver) continue;
                if (!fresh && seen.ContainsKey(e.Guid)) changed.Add((e, ver));
                seen[e.Guid] = ver;
            }
            // remembered right away: a crash or a closed game must not show the same notes forever
            Seen.Value = string.Join(";", seen.Select(kv => kv.Key + "=" + kv.Value));
            if (changed.Count == 0) yield break;

            foreach (var (entry, ver) in changed) {
                var req = new UnityWebRequest();
                req.timeout = UnknownsNet.RequestTimeout;
                req.SetMethod(UnityWebRequest.UnityWebRequestMethod.Get);
                req.SetUrl(ReleaseFeed.Url(entry.RepositoryOwner, entry.RepositoryName));
                req.SetRequestHeader("User-Agent", $"UsefulTORStuff/{UsefulTORStuffPlugin.PluginVersion}");
                req.downloadHandler = new DownloadHandlerBuffer();
                var op = req.SendWebRequest();
                while (!op.isDone) yield return null;
                try {
                    if (!req.isNetworkError && !req.isHttpError) {
                        var hit = ReleaseFeed.Parse(req.downloadHandler.text)
                            .FirstOrDefault(r => (r.Tag ?? "").TrimStart('v', 'V') == ver);
                        if (hit != null && !string.IsNullOrWhiteSpace(hit.Notes))
                            queue.Add(new Item {
                                Name = entry.DisplayName, Version = ver, Notes = Trim(hit.Notes),
                                Url = $"https://github.com/{entry.RepositoryOwner}/{entry.RepositoryName}/releases/tag/{Uri.EscapeDataString(hit.Tag)}",
                            });
                    }
                } catch (Exception ex) {
                    UsefulTORStuffPlugin.Logger?.LogInfo($"[WhatsNew] {entry.DisplayName}: {ex.Message}");
                } finally {
                    req.downloadHandler.Dispose();
                    req.Dispose();
                }
            }
            if (queue.Count > 0 && MainMenu() != null) Show();
        }

        [HideFromIl2Cpp]
        private static string Trim(string notes) {
            var lines = notes.Replace("\r", "").Split('\n').Select(l => l.TrimEnd()).ToList();
            // no blank runs, no leading blank lines
            var kept = new List<string>();
            foreach (var l in lines) {
                if (l.Length == 0 && (kept.Count == 0 || kept[kept.Count - 1].Length == 0)) continue;
                kept.Add(l.Replace("<", "‹").Replace(">", "›"));
            }
            var sb = new System.Text.StringBuilder();
            int n = 0;
            foreach (var l in kept) {
                if (n >= MaxLines || sb.Length + l.Length > MaxChars) { sb.Append("\n…"); break; }
                sb.Append(n == 0 ? "" : "\n").Append(l);
                n++;
            }
            return sb.ToString();
        }

        [HideFromIl2Cpp]
        private void Next() {
            if (queue.Count > 0) queue.RemoveAt(0);
            Close();
            if (queue.Count > 0) Show();
        }

        [HideFromIl2Cpp]
        private void Close() {
            if (root != null) { Destroy(root); root = null; }
        }

        [HideFromIl2Cpp]
        private void Show() {
            try {
                var item = queue[0];
                root = VanillaUI.Canvas("UTSWhatsNew", 9600, true);
                VanillaUI.Backdrop(root, null);
                var panel = VanillaUI.CenterPanel(root, new Vector2(1300, 900));
                float y = VanillaUI.Title(panel, UTSLocalization.Tr("uts.whatsnew.title", item.Name, item.Version));
                var box = VanillaUI.Box(panel, new Vector2(30, -y - 10f), new Vector2(1240, 900 - y - 130f));
                var t = VanillaUI.Text(box, item.Notes, 19, Color.white, new Vector2(18, -14), new Vector2(1204, 900 - y - 158f));
                t.enableWordWrapping = true;
                t.overflowMode = TMPro.TextOverflowModes.Ellipsis;
                string next = queue.Count > 1 ? UTSLocalization.Tr("uts.whatsnew.next", queue.Count - 1) : UTSLocalization.Tr("uts.whatsnew.close");
                VanillaUI.Button(panel, next, new Vector2(230, 28), new Vector2(380, 60), VanillaUI.Green, Next);
                string url = item.Url;
                VanillaUI.Button(panel, UTSLocalization.Tr("uts.whatsnew.open"), new Vector2(-230, 28), new Vector2(380, 60), VanillaUI.Blue,
                    () => { try { Application.OpenURL(url); } catch { } });
            } catch (Exception e) {
                UsefulTORStuffPlugin.Logger?.LogWarning($"[WhatsNew] panel failed: {e.Message}");
                Close();
            }
        }
    }
}
