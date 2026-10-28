// Useful TOR Stuff - Copyright (C) 2026 DaUnknown-0
// Licensed under GPL-3.0-or-later. See LICENSE for details.

/*
 * Hat search (2026-10-28, user idea): a search field above the hat list in the wardrobe. TOR builds the tab itself
 * (HatsTabPatches.OnEnable: one block per package, title text plus chips with the hat name). With many custom hats
 * the list gets long, so this adds a small field at the top: click it and type, every chip whose hat name, author or
 * package does not contain the text is hidden and the rest close up in the grid; the package titles hide while a
 * search is on. Backspace deletes, Escape or Enter leaves the field, an empty field shows the full list again.
 * Purely local UI, no game state. Keys typed into the field are not passed on (the wardrobe has no other key use).
 */

using System;
using System.Collections.Generic;
using HarmonyLib;
using TMPro;
using UnityEngine;
using Object = UnityEngine.Object;

namespace UsefulTORStuff {
    [HarmonyPatch]
    internal static class HatSearch {
        private static HatsTab tab;
        private static GameObject box;
        private static TextMeshPro label;
        private static SpriteRenderer back;
        private static string query = "";
        private static bool focused;
        private static float caretBlink;
        private static readonly Dictionary<IntPtr, Vector3> homes = new();
        private static readonly List<(GameObject Go, bool Active)> titles = new();
        private static float originalMax;
        private static Sprite white;

        [HarmonyPatch(typeof(HatsTab), nameof(HatsTab.OnEnable))]
        [HarmonyPostfix]
        [HarmonyPriority(Priority.Low)]
        private static void OnEnablePostfix(HatsTab __instance) {
            try {
                tab = __instance;
                query = "";
                focused = false;
                homes.Clear();
                titles.Clear();
                originalMax = __instance.scroller.ContentYBounds.max;
                foreach (var chip in __instance.ColorChips) if (chip != null) homes[chip.Pointer] = chip.transform.localPosition;
                // package titles: the TextMeshPro children of the list that are not part of a chip
                for (int i = 0; i < __instance.scroller.Inner.childCount; i++) {
                    var ch = __instance.scroller.Inner.GetChild(i);
                    if (ch.GetComponent<ColorChip>() == null && ch.GetComponent<TextMeshPro>() != null) titles.Add((ch.gameObject, ch.gameObject.activeSelf));
                }
                BuildBox(__instance);
            } catch (Exception e) { UsefulTORStuffPlugin.Logger?.LogWarning("[UTS/HatSearch] " + e.Message); }
        }

        [HarmonyPatch(typeof(HatsTab), nameof(HatsTab.Update))]
        [HarmonyPostfix]
        private static void UpdatePostfix(HatsTab __instance) {
            if (__instance != tab || box == null) return;
            try { Tick(); } catch { }
        }

        private static void BuildBox(HatsTab t) {
            if (box != null) Object.Destroy(box);
            var hit = t.scroller.Hitbox;
            if (hit == null) return;
            var b = hit.bounds;
            box = new GameObject("UTS_HatSearch") { layer = t.gameObject.layer };
            box.transform.SetParent(t.transform, true);
            box.transform.position = new Vector3(b.center.x, b.max.y + 0.32f, hit.transform.position.z - 2f);
            box.transform.localScale = Vector3.one;
            var bg = new GameObject("Back") { layer = box.layer };
            bg.transform.SetParent(box.transform, false);
            back = bg.AddComponent<SpriteRenderer>();
            if (white == null) {
                var tex = new Texture2D(4, 4);
                var px = new Color32[16];
                for (int i = 0; i < 16; i++) px[i] = new Color32(255, 255, 255, 255);
                tex.SetPixels32(px); tex.Apply();
                tex.hideFlags |= HideFlags.HideAndDontSave;
                white = Sprite.Create(tex, new Rect(0, 0, 4, 4), new Vector2(0.5f, 0.5f), 4f);
                white.hideFlags |= HideFlags.HideAndDontSave;
            }
            back.sprite = white;
            bg.transform.localScale = new Vector3(Mathf.Min(3.2f, b.size.x * 0.8f), 0.42f, 1f);
            back.color = new Color(0.12f, 0.12f, 0.16f, 0.9f);
            var col = bg.AddComponent<BoxCollider2D>();
            col.size = Vector2.one;
            var tx = new GameObject("Text") { layer = box.layer };
            tx.transform.SetParent(box.transform, false);
            tx.transform.localPosition = new Vector3(0f, 0f, -0.1f);
            label = tx.AddComponent<TextMeshPro>();
            label.fontSize = 2.2f;
            label.alignment = TextAlignmentOptions.Center;
            label.enableWordWrapping = false;
            label.rectTransform.sizeDelta = new Vector2(3.1f, 0.4f);
            Paint();
        }

        private static void Paint() {
            if (label == null) return;
            bool caret = focused && (Time.time - caretBlink) % 1f < 0.55f;
            label.text = query.Length == 0 && !focused
                ? "<color=#9a9aa8>Search hats...</color>"
                : query + (caret ? "|" : " ");
            if (back != null) back.color = focused ? new Color(0.18f, 0.18f, 0.26f, 0.95f) : new Color(0.12f, 0.12f, 0.16f, 0.9f);
        }

        private static void Tick() {
            if (Input.GetMouseButtonDown(0)) {
                var cam = Camera.main;
                bool over = false;
                if (cam != null && back != null) {
                    var wp = (Vector2)cam.ScreenToWorldPoint(Input.mousePosition);
                    var c = back.GetComponent<BoxCollider2D>();
                    over = c != null && c.OverlapPoint(wp);
                }
                if (over != focused) { focused = over; caretBlink = Time.time; }
            }
            if (focused) {
                string before = query;
                foreach (char ch in Input.inputString) {
                    if (ch == '\b') { if (query.Length > 0) query = query.Substring(0, query.Length - 1); }
                    else if (ch == '\n' || ch == '\r') focused = false;
                    else if (!char.IsControl(ch) && query.Length < 24) query += ch;
                }
                if (Input.GetKeyDown(KeyCode.Escape)) focused = false;
                if (query != before) Apply();
            }
            Paint();
        }

        private static void Apply() {
            if (tab == null) return;
            string q = query.Trim().ToLowerInvariant();
            foreach (var (go, active) in titles) if (go != null) go.SetActive(q.Length == 0 && active);
            int n = 0;
            float x0 = tab.XRange.min, x1 = tab.XRange.max;
            foreach (var chip in tab.ColorChips) {
                if (chip == null) continue;
                bool show = q.Length == 0 || Matches(chip, q);
                chip.gameObject.SetActive(show);
                if (!show) continue;
                if (q.Length == 0) {
                    if (homes.TryGetValue(chip.Pointer, out var home)) chip.transform.localPosition = home;
                    continue;
                }
                float xPos = Mathf.Lerp(x0, x1, n % tab.NumPerRow / (tab.NumPerRow - 1f));
                float yPos = tab.YStart - n / tab.NumPerRow * 1.5f * tab.YOffset;
                chip.transform.localPosition = new Vector3(xPos, yPos, -1f);
                n++;
            }
            tab.scroller.ContentYBounds.max = q.Length == 0 ? originalMax : Mathf.Max(0f, -(tab.YStart - (n - 1) / Math.Max(1, tab.NumPerRow) * 1.5f * tab.YOffset - 2.5f));
            tab.scroller.ScrollToTop();
        }

        private static bool Matches(ColorChip chip, string q) {
            try {
                var hat = chip.Tag?.TryCast<HatData>();
                if (hat != null && hat.name != null && hat.name.ToLowerInvariant().Contains(q)) return true;
                // TOR writes "<name>\nby <author>" under the custom chips
                foreach (var t in chip.GetComponentsInChildren<TextMeshPro>())
                    if (t != null && t.text != null && t.text.ToLowerInvariant().Contains(q)) return true;
            } catch { }
            return false;
        }
    }
}
