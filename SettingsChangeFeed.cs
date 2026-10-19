// Useful TOR Stuff - Copyright (C) 2026 DaUnknown-0
// Licensed under GPL-3.0-or-later. See LICENSE for details.
// Based on The Other Roles (https://github.com/TheOtherRolesAU/TheOtherRoles), GPL-3.0.

/*
 * SettingsChangeFeed - which modded setting just changed, from what to what, without a sound
 * (User 2026-10-18: "the settings changes of all TOR mods only show the number").
 *
 * TOR announces every change of a modded option through the game's lobby popup:
 *   Notifier.AddSettingsChangeMessage((StringNames)(id + 6000), value, false)   (CustomOptions.cs:194)
 * The game looks the name up under that made-up key, finds nothing and shows the bare value, "3" or "On".
 * Two repairs came before this one (TorLobbyFixes): sending the line again as a disconnect message beeped
 * on every click, and rewriting the finished popup lost to the game itself, which rewrites its popup back
 * to the bare value when the same setting is clicked again.
 *
 * So for modded keys the game's popup does not happen any more (prefix, false; vanilla settings keep it),
 * and a small card in the UTS look lists the changes instead:
 *     Sheriff · Mobile Emergency Button: Off -> On
 *  - The role (top of the parent chain) stands in its colour in front of a child option that does not
 *    name it, because "Number Of Sabotage Fixes" alone could belong to any role.
 *  - Old -> new: TOR calls the popup BEFORE it stores the new selection, so the option still holds the
 *    old one. Clicking the same option again updates its line and keeps the first old value
 *    (20 -> 22.5 -> 25 reads "20 -> 25"); back at the start the line goes.
 *  - Newest on top, at most five lines plus "+N more"; a line leaves 8 s after its last change, the card
 *    with the last line, and everything when the lobby ends.
 *  - Silent: no sound of its own (TOR passed playSound false to the game's popup as well).
 * Display only, on every client that receives a change. No option, no gate, nothing decided here.
 */

using System;
using System.Collections.Generic;
using System.Linq;
using HarmonyLib;
using TheOtherRoles;
using UnityEngine;
using UnityEngine.UI;

namespace UsefulTORStuff {

    public static class SettingsChangeFeed {

        /// TOR's offset from CustomOptions.cs:194. Read from there, not guessed.
        private const int TorStringNameOffset = 6000;
        private const int MaxLines = 5;
        private const float LineSeconds = 8f;

        private class Entry {
            public int Id;
            public string Label;
            public string Old, New;
            public float Until;
        }

        private static readonly List<Entry> entries = new List<Entry>();     // newest first
        private static GameObject root;
        private static TMPro.TextMeshProUGUI text;
        private static RectTransform cardRect;
        private static string shown = "";
        private static float nextTick;

        private static string Plain(string s) => System.Text.RegularExpressions.Regex.Replace(s ?? "", "<[^>]*>", "").Trim();

        private static CustomOption Find(int id) {
            foreach (var o in CustomOption.options) if (o != null && o.id == id) return o;
            return null;
        }

        private static string ValueOf(CustomOption o, int selection) {
            try {
                if (o.selections == null || selection < 0 || selection >= o.selections.Length) return "?";
                return o.selections[selection]?.ToString() ?? "?";
            } catch { return "?"; }
        }

        // "Role · Option" for a child option that does not already carry its role's name.
        private static string LabelOf(CustomOption o) {
            string name = Clean(o.name);
            var top = o;
            for (int guard = 0; top.parent != null && guard < 16; guard++) top = top.parent;
            if (top == o) return name;
            string role = Plain(top.name);
            if (role.Length == 0 || Plain(name).IndexOf(role, StringComparison.OrdinalIgnoreCase) >= 0) return name;
            return $"{Clean(top.name)} <color=#8C9AAD>{Sym('·', "/")}</color> {name}";
        }

        // One line per option: TOR breaks long names over two lines ("Max Amount Of Roles" / "To Choose
        // From") and marks child options with a leading "- " for its own indented list.
        private static string Clean(string name) {
            string n = System.Text.RegularExpressions.Regex.Replace(name ?? "?", @"\s*\n\s*", " ").Trim();
            string bare = Plain(n);
            if (bare.StartsWith("- ")) {
                int at = n.IndexOf("- ", StringComparison.Ordinal);
                if (at >= 0) n = (n.Substring(0, at) + n.Substring(at + 2)).Trim();
            }
            return n;
        }

        // Arrow and dot only where the card's font has them (VanillaUI.SetText would otherwise switch the
        // whole card to TMP's default face for one glyph).
        private static string Sym(char c, string fallback) {
            try {
                // the card's own face, without fallbacks: the same test VanillaUI.SetText makes
                var f = VanillaUI.Font();
                if (f != null && f.HasCharacters(c.ToString())) return c.ToString();
            } catch { }
            return fallback;
        }

        /// <summary>Called for a modded key instead of the game's popup. False when no option is behind
        /// the key, so the caller lets the game's popup run as before.</summary>
        internal static bool Add(int key, string value) {
            if (key < TorStringNameOffset) return false;
            var o = Find(key - TorStringNameOffset);
            if (o == null) return false;
            try {
                string now = value ?? "?";
                var e = entries.FirstOrDefault(x => x.Id == o.id);
                if (e == null) {
                    e = new Entry { Id = o.id, Old = ValueOf(o, o.selection) };
                } else {
                    entries.Remove(e);
                }
                e.Label = LabelOf(o);
                e.New = now;
                e.Until = Time.realtimeSinceStartup + LineSeconds;
                if (e.New != e.Old) entries.Insert(0, e);   // clicked back to where it was: nothing changed
                Render();
            } catch (Exception ex) {
                UsefulTORStuffPlugin.Logger?.LogWarning($"[SettingsChangeFeed] add failed: {ex.Message}");
            }
            return true;
        }

        // Driven from SessionStatsUI.Update like the ghost kill feed.
        public static void Tick() {
            if (Time.realtimeSinceStartup < nextTick) return;
            nextTick = Time.realtimeSinceStartup + 0.25f;
            try {
                if (LobbyBehaviour.Instance == null) {
                    if (entries.Count > 0) entries.Clear();
                } else {
                    float now = Time.realtimeSinceStartup;
                    entries.RemoveAll(x => x.Until <= now);
                }
                Render();
            } catch (Exception e) {
                UsefulTORStuffPlugin.Logger?.LogWarning($"[SettingsChangeFeed] tick failed: {e.Message}");
                nextTick = Time.realtimeSinceStartup + 5f;
            }
        }

        private static void Render() {
            if (entries.Count == 0) {
                if (root != null && root.activeSelf) root.SetActive(false);
                return;
            }
            if (root == null) Build();
            if (root == null) return;
            if (!root.activeSelf) root.SetActive(true);
            // below the "still on the end screen" card when the host sees both
            if (cardRect != null) cardRect.anchoredPosition = new Vector2(24, -250 - LobbyReturnWatch.VisibleHeight());

            string arrow = Sym('→', "->");
            var lines = new List<string>();
            foreach (var e in entries.Take(MaxLines))
                lines.Add($"{e.Label}: <color=#8C9AAD>{e.Old}</color> {arrow} <color=#9EFFA1>{e.New}</color>");
            if (entries.Count > MaxLines)
                lines.Add($"<color=#8C9AAD>{UTSLocalization.Tr("uts.settingsfeed.more", entries.Count - MaxLines)}</color>");
            string body = string.Join("\n", lines);
            if (body != shown) { VanillaUI.SetText(text, body); shown = body; }
        }

        // One screen-space card in the UTS look (same family as the ghost kill feed).
        private static void Build() {
            try {
                root = new GameObject("UTSSettingsChangeFeed");
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
                var rt = bg.GetComponent<RectTransform>();
                cardRect = rt;
                // top left under the mod-check banner: the game's own notifications (joins, vanilla
                // settings) stack up from the bottom left corner
                rt.anchorMin = rt.anchorMax = rt.pivot = new Vector2(0, 1);
                rt.anchoredPosition = new Vector2(24, -250);
                rt.sizeDelta = new Vector2(600, 0);
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
                timg.sprite = VanillaUI.Slant(); timg.type = Image.Type.Sliced; timg.color = VanillaUI.Teal; timg.raycastTarget = false;
                var title = VanillaUI.Label(tab, UTSLocalization.Tr("uts.settingsfeed.title"), 24, Color.white, Vector2.zero, Vector2.one,
                    new Vector2(0.5f, 0.5f), Vector2.zero, Vector2.zero, TMPro.TextAlignmentOptions.Left, TMPro.FontStyles.UpperCase);
                title.rectTransform.offsetMin = new Vector2(14, 0); title.rectTransform.offsetMax = new Vector2(-150, 0);
                VanillaUI.Heading(title);

                var t = new GameObject("T");
                t.transform.SetParent(bg.transform, false);
                t.AddComponent<RectTransform>();
                text = t.AddComponent<TMPro.TextMeshProUGUI>();
                text.fontSize = 19;
                text.color = Color.white;
                text.alignment = TMPro.TextAlignmentOptions.TopLeft;
                text.enableWordWrapping = true;
                text.raycastTarget = false;
                var font = VanillaUI.Font();
                if (font != null) text.font = font;
                shown = "";
            } catch (Exception e) {
                UsefulTORStuffPlugin.Logger?.LogWarning($"[SettingsChangeFeed] UI failed: {e.Message}");
                if (root != null) UnityEngine.Object.Destroy(root);
                root = null;
            }
        }

        /*
         * POSITIONAL INJECTION (__0/__1), NOT PARAMETER NAMES: the vanilla signature is
         * AddSettingsChangeMessage(key, value, playSound, associatedRole), four parameters read from the
         * interop metadata, and Harmony binds injected parameters by name. The first two are all this needs.
         */
        [HarmonyPatch(typeof(NotificationPopper), nameof(NotificationPopper.AddSettingsChangeMessage))]
        internal static class PopupPatch {
            public static bool Prefix(StringNames __0, string __1) {
                try {
                    return !Add((int)__0, __1);
                } catch (Exception e) {
                    UsefulTORStuffPlugin.Logger?.LogWarning($"[SettingsChangeFeed] popup prefix failed: {e.Message}");
                    return true;
                }
            }
        }

        // ====================================================================
        // Autotest: a local lobby, a few options changed the way TOR's menu does, one screenshot, every
        // option put back without a popup, quit.
        // ====================================================================
        public static BepInEx.Configuration.ConfigEntry<bool> DiagTest;
        private static bool diagFired;

        [HarmonyPatch(typeof(MainMenuManager), nameof(MainMenuManager.Start))]
        private static class DiagPatch {
            private static void Postfix() {
                if (diagFired || DiagTest == null || !DiagTest.Value || DraftMusicDriver.Instance == null) return;
                diagFired = true;
                BepInEx.Unity.IL2CPP.Utils.MonoBehaviourExtensions.StartCoroutine(DraftMusicDriver.Instance, DiagRun());
            }
        }

        private static void DiagLog(string s) => UsefulTORStuffPlugin.Logger?.LogInfo("[SettingsFeedTest] " + s);

        private static System.Collections.IEnumerator DiagRun() {
            yield return new WaitForSeconds(4f);
            try {
                var host = UnityEngine.Object.FindObjectOfType<HostLocalGameButton>(true);
                if (host == null) DiagLog("no HostLocalGameButton");
                else {
                    for (var tr = host.transform; tr != null; tr = tr.parent) if (!tr.gameObject.activeSelf) tr.gameObject.SetActive(true);
                    host.NetworkMode = NetworkModes.LocalGame;
                    host.OnClick();
                    DiagLog("hosting a local lobby");
                }
            } catch (Exception e) { DiagLog("host failed: " + e.Message); }
            float t0 = Time.realtimeSinceStartup;
            while (LobbyBehaviour.Instance == null && Time.realtimeSinceStartup - t0 < 45f) yield return null;
            yield return new WaitForSeconds(5f);
            DiagLog($"lobby {(LobbyBehaviour.Instance != null)}");

            var before = new Dictionary<CustomOption, int>();
            // never the preset selector (id 0): switching it rewrites every option
            var picks = CustomOption.options.Where(o => o != null && o.id != 0 && o.selections != null && o.selections.Length > 2).ToList();
            var chosen = picks.Where(o => o.parent != null && o.parent.parent == null).Take(2)
                .Concat(picks.Where(o => o.parent == null).Take(2)).ToList();
            var bools = CustomOption.options.Where(o => o != null && o.id != 0 && o.parent != null && o.selections != null && o.selections.Length == 2).Take(1);
            chosen.AddRange(bools);
            foreach (var o in chosen) before[o] = o.selection;
            try {
                for (int i = 0; i < chosen.Count; i++) {
                    var o = chosen[i];
                    o.updateSelection(o.selection + 1);
                    if (i == 0) o.updateSelection(o.selection + 1);   // a second click on the same option merges
                }
                if (chosen.Count > 1) { var o = chosen[1]; o.updateSelection(o.selection - 1); }   // and back: the line goes
            } catch (Exception e) { DiagLog("change failed: " + e.Message); }
            LobbyReturnWatch.DiagSample();
            yield return new WaitForSeconds(1.0f);
            DiagLog($"return watch card height: {LobbyReturnWatch.VisibleHeight():F0}");
            DiagLog($"card lines: {entries.Count}, text: {Plain(DiagText()).Replace("\n", " | ")}");
            yield return Shot("settings_feed");
            LobbyReturnWatch.DiagRelease();

            // put everything back without a popup
            foreach (var kv in before) { try { kv.Key.updateSelection(kv.Value, false); } catch { } }
            DiagLog("options restored: " + string.Join(", ", before.Select(kv => $"{kv.Key.id}={kv.Key.selection}")));
            yield return new WaitForSeconds(9f);
            DiagLog($"after 9 s: card {(root != null && root.activeSelf ? "still shown" : "gone")}, lines {entries.Count}");
            DiagLog("done");
            yield return new WaitForSeconds(0.5f);
            Application.Quit();
        }

        private static string DiagText() => text != null ? text.text : "";

        private static System.Collections.IEnumerator Shot(string tag) {
            yield return new WaitForEndOfFrame();
            try {
                string dir = System.IO.Path.Combine(BepInEx.Paths.GameRootPath, "UTSShots");
                System.IO.Directory.CreateDirectory(dir);
                string file = System.IO.Path.Combine(dir, $"{tag}.png");
                var tex = new Texture2D(Screen.width, Screen.height, TextureFormat.RGB24, false);
                tex.ReadPixels(new Rect(0, 0, Screen.width, Screen.height), 0, 0);
                tex.Apply();
                System.IO.File.WriteAllBytes(file, ImageConversion.EncodeToPNG(tex));
                UnityEngine.Object.Destroy(tex);
                DiagLog("screenshot " + file);
            } catch (Exception e) { DiagLog("screenshot failed: " + e.Message); }
        }
    }
}
