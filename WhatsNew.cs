// Useful TOR Stuff - Copyright (C) 2026 DaUnknown-0
// Licensed under GPL-3.0-or-later. See LICENSE for details.

/*
 * WhatsNew - after an update, the main menu shows what is new in each updated mod (User 2026-11-13).
 *
 * The versions of the mods in the UTS catalog (UTSModCatalog, own repositories only) are remembered in the
 * config ([Interface] Seen Versions). At the first main menu of a start, every mod whose loaded version
 * differs from the remembered one gets its release notes from the GitHub Atom feed (ReleaseFeed, the same
 * source the updaters read, not counted against the API limit) and a panel shows them, one mod after the
 * other: name, version, the notes (split into pages that fit the box, arrows, wheel or arrow keys turn them) and a
 * button to the release page.
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
        // long notes are split into pages that fit the box (User 2026-11-20: pages instead of text running out of the panel)
        private const int MaxLines = 400, MaxChars = 30000;

        private sealed class Item { public string Name, Version, Notes, Url; }
        private readonly List<Item> queue = new List<Item>();
        private GameObject root;
        private static bool started;
        private readonly List<string> pages = new List<string>();
        private int page;
        private TMPro.TextMeshProUGUI body, pageLabel;
        private GameObject prevBtn, nextBtn;

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
                    else if (Input.GetKeyDown(KeyCode.RightArrow) || Input.GetKeyDown(KeyCode.PageDown)) Turn(1);
                    else if (Input.GetKeyDown(KeyCode.LeftArrow) || Input.GetKeyDown(KeyCode.PageUp)) Turn(-1);
                    else {
                        float wheel = Input.mouseScrollDelta.y;
                        if (wheel < -0.01f) Turn(1); else if (wheel > 0.01f) Turn(-1);
                    }
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
            body = pageLabel = null; prevBtn = nextBtn = null;
            pages.Clear(); page = 0;
        }

        /// <summary>Splits the notes line by line into pages whose rendered height fits the text box.</summary>
        [HideFromIl2Cpp]
        private static List<string> Paginate(TMPro.TextMeshProUGUI t, string notes, float width, float height) {
            var result = new List<string>();
            var cur = new System.Text.StringBuilder();
            foreach (var line in (notes ?? "").Split('\n')) {
                string candidate = cur.Length == 0 ? line : cur + "\n" + line;
                float h;
                try { h = t.GetPreferredValues(candidate, width, 0f).y; } catch { h = 0f; }
                if (cur.Length > 0 && h > height) {
                    result.Add(cur.ToString().TrimEnd('\n'));
                    cur.Clear();
                    if (line.Length == 0) continue; // no blank line at the top of a page
                    cur.Append(line);
                } else {
                    cur.Clear(); cur.Append(candidate);
                }
            }
            if (cur.Length > 0 || result.Count == 0) result.Add(cur.ToString());
            return result;
        }

        [HideFromIl2Cpp]
        private void Turn(int delta) {
            if (body == null || pages.Count < 2) return;
            int to = Mathf.Clamp(page + delta, 0, pages.Count - 1);
            if (to == page) return;
            page = to;
            ShowPage();
        }

        [HideFromIl2Cpp]
        private void ShowPage() {
            if (body == null || pages.Count == 0) return;
            body.text = pages[page];
            bool many = pages.Count > 1;
            if (pageLabel != null) { pageLabel.gameObject.SetActive(many); pageLabel.text = $"{page + 1} / {pages.Count}"; }
            if (prevBtn != null) prevBtn.SetActive(many && page > 0);
            if (nextBtn != null) nextBtn.SetActive(many && page < pages.Count - 1);
        }

        [HideFromIl2Cpp]
        private void Show() {
            try {
                var item = queue[0];
                root = VanillaUI.Canvas("UTSWhatsNew", 9600, true);
                VanillaUI.Backdrop(root, null);
                var panel = VanillaUI.CenterPanel(root, new Vector2(1300, 900));
                float y = VanillaUI.Title(panel, UTSLocalization.Tr("uts.whatsnew.title", item.Name, item.Version));
                // Title returns the (negative) offset below the title: the box starts there, not mirrored above the panel
                float top = y - 10f;
                float boxH = 900f + top - 120f;
                var box = VanillaUI.Box(panel, new Vector2(30, top), new Vector2(1240, boxH));
                // text area leaves a strip at the bottom of the box for the page arrows
                float textH = boxH - 28f - 56f;
                var t = VanillaUI.Text(box, item.Notes, 19, Color.white, new Vector2(18, -14), new Vector2(1204, textH));
                t.enableWordWrapping = true;
                t.overflowMode = TMPro.TextOverflowModes.Truncate;
                body = t;
                pages.Clear();
                pages.AddRange(Paginate(t, item.Notes, 1204f, textH));
                page = 0;
                pageLabel = VanillaUI.Label(box, "", 20, Color.white, new Vector2(0.5f, 0), new Vector2(0.5f, 0), new Vector2(0.5f, 0),
                    new Vector2(0, 12), new Vector2(200, 40), TMPro.TextAlignmentOptions.Center);
                prevBtn = VanillaUI.Button(box, "‹", new Vector2(-150, 8), new Vector2(80, 46), VanillaUI.Blue, () => Turn(-1));
                nextBtn = VanillaUI.Button(box, "›", new Vector2(150, 8), new Vector2(80, 46), VanillaUI.Blue, () => Turn(1));
                ShowPage();
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
