// Useful TOR Stuff - Copyright (C) 2026 DaUnknown-0
// Licensed under GPL-3.0-or-later. See LICENSE for details.
// Based on The Other Roles (https://github.com/TheOtherRolesAU/TheOtherRoles), GPL-3.0.

/*
 * LobbyReturnWatch - who is still on the end screen (User 2026-10-19).
 *
 * After a round the host is back in the lobby long before everyone else. The game keeps every client
 * connected, but a client that is still on the end screen has not loaded the lobby scene yet
 * (ClientData.InScene false) and has no player in the lobby (ClientData.Character null). The host only
 * sees the player count, not who is missing, and starting would leave them behind.
 *
 * For the host, in the lobby, a small card in the UTS look lists exactly those clients:
 *     Still on the end screen
 *     Lisa                0:42
 *     Marcel              0:17
 *  - a client shows up after 2 s, so a quick join that is just loading does not flash in and out
 *  - the time counts from when the host first saw the client missing
 *  - a client that comes back, or leaves the game, drops off; the card goes with the last one
 * Top left, where the settings card (SettingsChangeFeed) sits; that card moves down below this one.
 * Display only, host only, nothing decided here.
 */

using System;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;
using UnityEngine.UI;

namespace UsefulTORStuff {

    public static class LobbyReturnWatch {

        private const float ShowAfterSeconds = 2f;
        private const int MaxLines = 8;

        private static readonly Dictionary<int, float> missingSince = new Dictionary<int, float>();   // clientId -> time
        private static readonly Dictionary<int, string> names = new Dictionary<int, string>();
        private static GameObject root;
        private static RectTransform cardRect;
        private static TMPro.TextMeshProUGUI text;
        private static string shown = "";
        private static float nextTick;
        private static bool diagHold;

        /// <summary>Space the card takes at the top left (for SettingsChangeFeed), 0 while hidden.</summary>
        public static float VisibleHeight() =>
            root != null && root.activeSelf && cardRect != null ? cardRect.rect.height + 12f : 0f;

        private static bool HostInLobby() {
            var client = AmongUsClient.Instance;
            return client != null && client.AmHost && LobbyBehaviour.Instance != null && ShipStatus.Instance == null;
        }

        // Driven from SessionStatsUI.Update like the other overlays.
        public static void Tick() {
            if (diagHold || Time.realtimeSinceStartup < nextTick) return;
            nextTick = Time.realtimeSinceStartup + 0.5f;
            try {
                if (!HostInLobby()) {
                    missingSince.Clear();
                    names.Clear();
                    Render(new List<(string, float)>());
                    return;
                }
                var client = AmongUsClient.Instance;
                var present = new HashSet<int>();
                float now = Time.realtimeSinceStartup;
                var all = client.allClients;
                if (all != null) {
                    for (int i = 0; i < all.Count; i++) {
                        var c = all[i];
                        if (c == null || c.Id == client.ClientId) continue;
                        bool missing = !c.InScene || c.Character == null;
                        if (!missing) continue;
                        present.Add(c.Id);
                        if (!missingSince.ContainsKey(c.Id)) missingSince[c.Id] = now;
                        string n = null;
                        try { n = c.PlayerName; } catch { }
                        if (!string.IsNullOrWhiteSpace(n)) names[c.Id] = n;
                    }
                }
                foreach (int id in missingSince.Keys.Where(id => !present.Contains(id)).ToList()) {
                    missingSince.Remove(id);
                    names.Remove(id);
                }
                var list = missingSince.Where(kv => now - kv.Value >= ShowAfterSeconds)
                    .OrderBy(kv => kv.Value)
                    .Select(kv => (names.TryGetValue(kv.Key, out var n) ? n : $"#{kv.Key}", now - kv.Value))
                    .ToList();
                Render(list);
            } catch (Exception e) {
                UsefulTORStuffPlugin.Logger?.LogWarning($"[LobbyReturnWatch] tick failed: {e.Message}");
                nextTick = Time.realtimeSinceStartup + 5f;
            }
        }

        private static string Clock(float s) {
            int t = Mathf.Max(0, Mathf.FloorToInt(s));
            return $"{t / 60}:{t % 60:00}";
        }

        private static void Render(List<(string Name, float Seconds)> list) {
            if (list.Count == 0) {
                if (root != null && root.activeSelf) root.SetActive(false);
                shown = "";
                return;
            }
            if (root == null) Build();
            if (root == null) return;
            if (!root.activeSelf) root.SetActive(true);
            var lines = list.Take(MaxLines).Select(x =>
                $"{x.Name.Replace("<", "").Replace(">", "")}<pos=78%><color=#8C9AAD>{Clock(x.Seconds)}</color>").ToList();
            if (list.Count > MaxLines)
                lines.Add($"<color=#8C9AAD>{UTSLocalization.Tr("uts.settingsfeed.more", list.Count - MaxLines)}</color>");
            string body = string.Join("\n", lines);
            if (body != shown) { VanillaUI.SetText(text, body); shown = body; }
        }

        private static void Build() {
            try {
                root = new GameObject("UTSLobbyReturnWatch");
                UnityEngine.Object.DontDestroyOnLoad(root);
                var canvas = root.AddComponent<Canvas>();
                canvas.renderMode = RenderMode.ScreenSpaceOverlay;
                canvas.sortingOrder = 7900;
                var scaler = root.AddComponent<CanvasScaler>();
                scaler.uiScaleMode = CanvasScaler.ScaleMode.ScaleWithScreenSize;
                scaler.referenceResolution = new Vector2(1920, 1080);
                scaler.matchWidthOrHeight = 0.5f;

                var bg = VanillaUI.Rounded(root, new Color(VanillaUI.Body.r, VanillaUI.Body.g, VanillaUI.Body.b, 0.88f), VanillaUI.RadiusBox + 4);
                bg.name = "Bg";
                var ring = VanillaUI.Ring(bg, VanillaUI.Frame, VanillaUI.RadiusBox + 4, 3);
                VanillaUI.Stretch(ring);
                ring.AddComponent<LayoutElement>().ignoreLayout = true;
                cardRect = bg.GetComponent<RectTransform>();
                cardRect.anchorMin = cardRect.anchorMax = cardRect.pivot = new Vector2(0, 1);
                cardRect.anchoredPosition = new Vector2(24, -250);
                cardRect.sizeDelta = new Vector2(420, 0);
                var fit = bg.AddComponent<ContentSizeFitter>();
                fit.verticalFit = ContentSizeFitter.FitMode.PreferredSize;
                var layout = bg.AddComponent<VerticalLayoutGroup>();
                var pad = new RectOffset(); pad.left = 16; pad.right = 16; pad.top = 12; pad.bottom = 12;
                layout.padding = pad;
                layout.spacing = 6;
                layout.childControlHeight = true; layout.childControlWidth = true;
                layout.childForceExpandHeight = false;

                var tab = new GameObject("TitleTab");
                tab.transform.SetParent(bg.transform, false);
                tab.AddComponent<RectTransform>();
                var le = tab.AddComponent<LayoutElement>();
                le.preferredHeight = 34; le.minHeight = 34;
                var timg = tab.AddComponent<Image>();
                timg.sprite = VanillaUI.Slant(); timg.type = Image.Type.Sliced; timg.color = VanillaUI.Amber; timg.raycastTarget = false;
                var title = VanillaUI.Label(tab, UTSLocalization.Tr("uts.returnwatch.title"), 24, Color.white, Vector2.zero, Vector2.one,
                    new Vector2(0.5f, 0.5f), Vector2.zero, Vector2.zero, TMPro.TextAlignmentOptions.Left, TMPro.FontStyles.UpperCase);
                title.rectTransform.offsetMin = new Vector2(14, 0); title.rectTransform.offsetMax = new Vector2(-70, 0);
                VanillaUI.Heading(title);

                var t = new GameObject("T");
                t.transform.SetParent(bg.transform, false);
                t.AddComponent<RectTransform>();
                text = t.AddComponent<TMPro.TextMeshProUGUI>();
                text.fontSize = 20;
                text.color = Color.white;
                text.alignment = TMPro.TextAlignmentOptions.TopLeft;
                text.enableWordWrapping = false;
                text.raycastTarget = false;
                var font = VanillaUI.Font();
                if (font != null) text.font = font;
                shown = "";
            } catch (Exception e) {
                UsefulTORStuffPlugin.Logger?.LogWarning($"[LobbyReturnWatch] UI failed: {e.Message}");
                if (root != null) UnityEngine.Object.Destroy(root);
                root = null;
            }
        }

        /// <summary>Autotest: sample lines (no second client exists in a local test lobby).</summary>
        internal static void DiagSample() {
            diagHold = true;
            Render(new List<(string, float)> { ("Lisa", 42f), ("Marcel", 17f), ("TrollQueen", 3f) });
        }

        internal static void DiagRelease() {
            diagHold = false;
            Render(new List<(string, float)>());
        }
    }
}
