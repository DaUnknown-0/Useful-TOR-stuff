// Useful TOR Stuff - Copyright (C) 2026 DaUnknown-0
// Licensed under GPL-3.0-or-later. See LICENSE for details.

/*
 * GameUI - panels built from the game's OWN menu graphics (User 2026-11-13: "it has to really look like the Among Us
 * UI itself"). VanillaUI draws look-alike shapes; this takes the sprites and fonts the settings window uses
 * (read out with the DuoTest "uidump" plan, 13.11.):
 *
 *   window     IMAGE-PanelBG (the settings window: black body, grey frame) + IMAGE-SettingsHeader (gears, yellow
 *              crewmate) top right, title in Brook, IMAGE-TitleDivider under it
 *   tabs       CTAInactive / CTAActive (sliced 23), flat: the game lays a gloss sprite on top, the User wants them
 *              without (13.11.); Brook in dark teal #10584B, on the dark tab strip (#292929)
 *   section    IMAGE-Subheader (the slanted grey "IMPOSTORS" bar, sliced 280) + Barlow Italic upper case,
 *              IMAGE-TitleDivider running on to the right
 *   row        IMAGE-SettingHeaderBG (grey label, sliced 35) with Barlow SemiBold, right aligned, and the value box
 *              BUTTON-CheckboxInactive (black, white frame, sliced 45) with Barlow Black
 *   close      BUTTON-ExitStandard / BUTTON-ExitHover
 *
 * The sprites are looked up by name among the loaded ones and kept (DontUnloadUnusedAsset) once found: the lobby
 * has all of them (the lobby info pane carries the settings window). Where one is missing (main menu, a renamed
 * sprite after an Among Us update) the VanillaUI shape stands in, so a panel never fails to build.
 * Screen-space canvas like VanillaUI (same 1920x1080 reference), so the callers keep their layout code.
 */

using System;
using System.Collections.Generic;
using Il2CppInterop.Runtime;
using UnityEngine;
using UnityEngine.UI;

namespace UsefulTORStuff {

    public static class GameUI {
        // ---- the game's colours (read from the settings window) ----
        public static readonly Color TabText = new Color32(0x10, 0x58, 0x4B, 0xFF);
        public static readonly Color TabStrip = new Color32(0x29, 0x29, 0x29, 0xFF);
        public static readonly Color LabelGrey = new Color32(0x37, 0x36, 0x36, 0xFF);
        public static readonly Color Dimmed = new Color(1f, 1f, 1f, 0.45f);

        // ====================================================================
        // Sprites and fonts
        // ====================================================================
        private static readonly string[] Wanted = {
            "IMAGE-PanelBG", "IMAGE-SettingsHeader", "IMAGE-TitleDivider", "IMAGE-Subheader", "IMAGE-SettingHeaderBG",
            "BUTTON-CheckboxInactive", "BUTTON-CheckboxHighlight", "CTAInactive", "CTAActive", "BUTTON-CTAShine",
            "BUTTON-CTAActiveShine", "BUTTON-ExitStandard", "BUTTON-ExitHover", "IMAGE-LabelCube", "IMAGE-FieldCube",
        };
        private static readonly Dictionary<string, Sprite> sprites = new();
        private static float searchedAt = -100f;
        private static bool logged;

        /// <summary>The game sprite of that name, or null while it is not loaded.</summary>
        public static Sprite Sprite(string name) {
            if (sprites.TryGetValue(name, out var s) && s != null) return s;
            Search();
            return sprites.TryGetValue(name, out s) ? s : null;
        }

        /// <summary>True when the window sprites are there (else the panels fall back to VanillaUI shapes).</summary>
        public static bool Ready => Sprite("IMAGE-PanelBG") != null && Sprite("CTAInactive") != null;

        // The menu sprites belong to the lobby scene: they are looked up (and pinned) as soon as a lobby is up, so a
        // panel opened later in a round (vote history, meeting cards) still finds them after the scene change.
        [HarmonyLib.HarmonyPatch(typeof(LobbyBehaviour), nameof(LobbyBehaviour.Start))]
        private static class LobbyWarm {
            public static void Postfix() { try { searchedAt = -100f; Search(); } catch { } }
        }

        private static void Search() {
            if (sprites.Count >= Wanted.Length || Time.realtimeSinceStartup - searchedAt < 2f) return;
            searchedAt = Time.realtimeSinceStartup;
            try {
                var want = new HashSet<string>(Wanted);
                foreach (var o in Resources.FindObjectsOfTypeAll(Il2CppType.Of<Sprite>())) {
                    var sp = o.TryCast<Sprite>();
                    if (sp == null || !want.Contains(sp.name) || sprites.ContainsKey(sp.name)) continue;
                    sp.hideFlags |= HideFlags.DontUnloadUnusedAsset;
                    if (sp.texture != null) sp.texture.hideFlags |= HideFlags.DontUnloadUnusedAsset;
                    sprites[sp.name] = sp;
                }
                if (!logged && sprites.Count > 0) {
                    logged = sprites.Count >= Wanted.Length;
                    UsefulTORStuffPlugin.Logger?.LogInfo($"[GameUI] {sprites.Count}/{Wanted.Length} menu sprites found.");
                }
            } catch (Exception e) { UsefulTORStuffPlugin.Logger?.LogWarning($"[GameUI] sprite search failed: {e.Message}"); }
        }

        private static readonly Dictionary<string, TMPro.TMP_FontAsset> fonts = new();
        private static float fontsAt = -100f;

        /// <summary>A loaded TMP font by exact name ("Brook SDF", "Barlow-Black SDF"...), null if not loaded.</summary>
        public static TMPro.TMP_FontAsset Font(string name) {
            if (fonts.TryGetValue(name, out var f) && f != null) return f;
            if (Time.realtimeSinceStartup - fontsAt < 2f) return null;
            fontsAt = Time.realtimeSinceStartup;
            try {
                foreach (var o in Resources.FindObjectsOfTypeAll(Il2CppType.Of<TMPro.TMP_FontAsset>())) {
                    var fa = o.TryCast<TMPro.TMP_FontAsset>();
                    if (fa != null && !fonts.ContainsKey(fa.name)) fonts[fa.name] = fa;
                }
            } catch { }
            return fonts.TryGetValue(name, out f) ? f : null;
        }

        public const string Brook = "Brook SDF", BarlowBlack = "Barlow-Black SDF", BarlowSemi = "Barlow-SemiBold SDF",
                            BarlowItalic = "Barlow-Italic SDF";

        // ====================================================================
        // Pieces
        // ====================================================================
        private static Image Img(GameObject parent, string name, string sprite, Color color, bool sliced, float ppuMul = 1f) {
            var go = new GameObject(name);
            go.transform.SetParent(parent.transform, false);
            go.AddComponent<RectTransform>();
            var img = go.AddComponent<Image>();
            var sp = Sprite(sprite);
            img.sprite = sp ?? VanillaUI.RoundedSprite(10);
            img.type = sliced || sp == null ? Image.Type.Sliced : Image.Type.Simple;
            img.pixelsPerUnitMultiplier = sp == null ? 1f : ppuMul;
            img.color = sp == null ? new Color(color.r * 0.3f, color.g * 0.3f, color.b * 0.3f, color.a) : color;
            img.raycastTarget = false;
            return img;
        }

        private static RectTransform Place(Component c, Vector2 anchor, Vector2 pivot, Vector2 pos, Vector2 size) {
            var rt = c.GetComponent<RectTransform>();
            rt.anchorMin = rt.anchorMax = anchor; rt.pivot = pivot;
            rt.anchoredPosition = pos; rt.sizeDelta = size;
            return rt;
        }

        /// <summary>Text in one of the game's faces (VanillaUI's font choice when that face lacks a glyph).</summary>
        public static TMPro.TextMeshProUGUI Text(GameObject parent, string text, string font, float size, Color color,
                                                 TMPro.TextAlignmentOptions align, TMPro.FontStyles style = TMPro.FontStyles.Normal) {
            var go = new GameObject("T");
            go.transform.SetParent(parent.transform, false);
            go.AddComponent<RectTransform>();
            var t = go.AddComponent<TMPro.TextMeshProUGUI>();
            t.fontSize = size; t.color = color; t.alignment = align; t.fontStyle = style;
            t.enableWordWrapping = false; t.overflowMode = TMPro.TextOverflowModes.Ellipsis;
            t.raycastTarget = false;
            SetText(t, text, font);
            return t;
        }

        public static void SetText(TMPro.TextMeshProUGUI t, string text, string font) {
            if (t == null) return;
            text ??= "";
            var f = Font(font);
            bool ok = false;
            try { ok = f != null && f.HasCharacters(System.Text.RegularExpressions.Regex.Replace(text, "<[^>]*>", "")); } catch { }
            if (ok) { if (t.font != f) t.font = f; t.text = text; }
            else VanillaUI.SetText(t, text);
        }

        /// <summary>
        /// The settings window, centred: body + frame, header art top right, title top left, divider. Returns the
        /// window object; its content area starts about 150 px below the top (tabs) and keeps 40 px to every edge.
        /// </summary>
        public static GameObject Window(GameObject root, Vector2 size, string title, Action onClose) {
            var win = new GameObject("GameWindow");
            win.transform.SetParent(root.transform, false);
            var rt = win.AddComponent<RectTransform>();
            rt.anchorMin = rt.anchorMax = rt.pivot = new Vector2(0.5f, 0.5f);
            rt.sizeDelta = size;

            var bg = Img(win, "Background", "IMAGE-PanelBG", Color.white, false);
            VanillaUI.Stretch(bg.gameObject);
            bg.raycastTarget = true;   // a click on the window does not reach the backdrop behind it
            if (Sprite("IMAGE-PanelBG") == null) { bg.color = VanillaUI.Body; VanillaUI.Stretch(VanillaUI.Ring(win, VanillaUI.Frame, VanillaUI.RadiusPanel, 5)); }

            // the header art fills the title band at the right and ends on the tab strip, as on the settings window
            // (aspect 796:240)
            var head = Img(win, "HeaderArt", "IMAGE-SettingsHeader", Color.white, false);
            head.preserveAspect = true;
            const float hh = 112f;
            Place(head, new Vector2(1, 1), new Vector2(1, 1), new Vector2(-size.x * 0.05f, -6f), new Vector2(hh * 796f / 240f, hh));

            var t = Text(win, title, Brook, 64f, Color.white, TMPro.TextAlignmentOptions.Left, TMPro.FontStyles.UpperCase);
            Place(t, new Vector2(0, 1), new Vector2(0, 1), new Vector2(54f, -26f), new Vector2(size.x * 0.55f, 76f));

            var div = Img(win, "Divider", "IMAGE-TitleDivider", Color.white, false);
            Place(div, new Vector2(0.5f, 1), new Vector2(0.5f, 1), new Vector2(-size.x * 0.02f, -108f), new Vector2(size.x * 0.92f, 3f));

            if (onClose != null) Close(win, onClose);
            return win;
        }

        /// <summary>The white X of the settings menu on the window's top-right corner (where the game's sits).</summary>
        public static GameObject Close(GameObject window, Action onClose) {
            var img = Img(window, "Close", "BUTTON-ExitStandard", Color.white, false);
            img.preserveAspect = true;
            img.raycastTarget = true;
            Place(img, new Vector2(1, 1), new Vector2(0.5f, 0.5f), new Vector2(-6f, -6f), new Vector2(78f, 78f));
            var b = img.gameObject.AddComponent<Button>();
            b.targetGraphic = img;
            var hover = Sprite("BUTTON-ExitHover");
            if (hover != null) {
                b.transition = Selectable.Transition.SpriteSwap;
                var st = b.spriteState; st.highlightedSprite = hover; st.pressedSprite = hover; st.selectedSprite = null; b.spriteState = st;
            }
            if (Sprite("BUTTON-ExitStandard") == null) {
                var x = Text(img.gameObject, "X", BarlowBlack, 40f, Color.white, TMPro.TextAlignmentOptions.Center);
                VanillaUI.Stretch(x.gameObject);
            }
            b.onClick.AddListener((UnityEngine.Events.UnityAction)(() => onClose()));
            return img.gameObject;
        }

        /// <summary>The dark strip the tabs sit on, across the window under the divider.</summary>
        public static GameObject TabStripAt(GameObject window, float y, float height = 62f) {
            var go = new GameObject("TabStrip");
            go.transform.SetParent(window.transform, false);
            go.AddComponent<RectTransform>();
            var img = go.AddComponent<Image>();
            img.sprite = VanillaUI.Solid(); img.color = TabStrip; img.raycastTarget = false;
            var rt = go.GetComponent<RectTransform>();
            rt.anchorMin = new Vector2(0, 1); rt.anchorMax = new Vector2(1, 1); rt.pivot = new Vector2(0.5f, 1);
            rt.anchoredPosition = new Vector2(-6f, y); rt.sizeDelta = new Vector2(-104f, height);
            return go;
        }

        /// <summary>
        /// A tab / button of the settings window: the teal CTA plate with its gloss, Brook label in dark teal;
        /// selected = the light plate. Positioned by its top-left corner in the parent.
        /// </summary>
        public static GameObject Tab(GameObject parent, string label, Vector2 topLeft, Vector2 size, bool selected, Action onClick,
                                     float fontSize = 30f) {
            var plate = Img(parent, "Tab", selected ? "CTAActive" : "CTAInactive", Color.white, true, 0.9f);
            Place(plate, new Vector2(0, 1), new Vector2(0, 1), topLeft, size);
            plate.raycastTarget = true;
            // no gloss on top (User 13.11.: "without shine"): the flat plate only
            var t = Text(plate.gameObject, label, Brook, fontSize, TabText, TMPro.TextAlignmentOptions.Center, TMPro.FontStyles.UpperCase);
            t.enableAutoSizing = true; t.fontSizeMin = 12f; t.fontSizeMax = fontSize;
            var trt = VanillaUI.Stretch(t.gameObject); trt.offsetMin = new Vector2(12f, 2f); trt.offsetMax = new Vector2(-12f, -4f);

            var b = plate.gameObject.AddComponent<Button>();
            b.targetGraphic = plate;
            var active = Sprite("CTAActive");
            if (!selected && active != null) {
                b.transition = Selectable.Transition.SpriteSwap;
                var st = b.spriteState; st.highlightedSprite = active; st.pressedSprite = active; b.spriteState = st;
            } else b.transition = Selectable.Transition.None;
            if (onClick != null) b.onClick.AddListener((UnityEngine.Events.UnityAction)(() => onClick()));
            return plate.gameObject;
        }

        /// <summary>A section header like "IMPOSTORS": the slanted grey bar with the italic label, a divider running on.</summary>
        public static GameObject Section(GameObject parent, string label, Vector2 topLeft, float width, float barWidth = 360f) {
            var go = new GameObject("Section");
            go.transform.SetParent(parent.transform, false);
            go.AddComponent<RectTransform>();
            Place(go.GetComponent<RectTransform>(), new Vector2(0, 1), new Vector2(0, 1), topLeft, new Vector2(width, 44f));
            var bar = Img(go, "Bar", "IMAGE-Subheader", Color.white, true, 1.6f);
            Place(bar, new Vector2(0, 0.5f), new Vector2(0, 0.5f), Vector2.zero, new Vector2(barWidth, 40f));
            var t = Text(bar.gameObject, label, BarlowItalic, 24f, Color.white, TMPro.TextAlignmentOptions.Left, TMPro.FontStyles.UpperCase);
            var trt = VanillaUI.Stretch(t.gameObject); trt.offsetMin = new Vector2(18f, 0f); trt.offsetMax = new Vector2(-30f, 0f);
            var div = Img(go, "Divider", "IMAGE-TitleDivider", Color.white, false);
            Place(div, new Vector2(0, 0.5f), new Vector2(0, 0.5f), new Vector2(barWidth + 6f, 0f), new Vector2(width - barWidth - 6f, 3f));
            return go;
        }

        /// <summary>The grey label plate of a setting row; the text right aligned like the game's.</summary>
        public static TMPro.TextMeshProUGUI Label(GameObject parent, string text, Vector2 topLeft, Vector2 size, Color? color = null,
                                                  TMPro.TextAlignmentOptions align = TMPro.TextAlignmentOptions.Right, float fontSize = 22f) {
            var plate = Img(parent, "Label", "IMAGE-SettingHeaderBG", Color.white, true, 1.4f);
            Place(plate, new Vector2(0, 1), new Vector2(0, 1), topLeft, size);
            var t = Text(plate.gameObject, text, BarlowSemi, fontSize, color ?? Color.white, align);
            t.enableAutoSizing = true; t.fontSizeMin = 12f; t.fontSizeMax = fontSize;
            var trt = VanillaUI.Stretch(t.gameObject); trt.offsetMin = new Vector2(16f, 2f); trt.offsetMax = new Vector2(-16f, -2f);
            return t;
        }

        /// <summary>The black value box with the white frame; Barlow Black, centred.</summary>
        public static TMPro.TextMeshProUGUI Value(GameObject parent, string text, Vector2 topLeft, Vector2 size, Color? color = null, float fontSize = 24f) {
            var box = Img(parent, "Value", "BUTTON-CheckboxInactive", Color.white, true, 2.2f);
            Place(box, new Vector2(0, 1), new Vector2(0, 1), topLeft, size);
            var t = Text(box.gameObject, text, BarlowBlack, fontSize, color ?? Color.white, TMPro.TextAlignmentOptions.Center);
            t.enableAutoSizing = true; t.fontSizeMin = 11f; t.fontSizeMax = fontSize;
            var trt = VanillaUI.Stretch(t.gameObject); trt.offsetMin = new Vector2(8f, 2f); trt.offsetMax = new Vector2(-8f, -2f);
            return t;
        }

        /// <summary>Plain text in the window (hints, legends): Barlow SemiBold, top-left.</summary>
        public static TMPro.TextMeshProUGUI Note(GameObject parent, string text, Vector2 topLeft, Vector2 size, float fontSize = 18f, Color? color = null) {
            var t = Text(parent, text, BarlowSemi, fontSize, color ?? new Color(0.8f, 0.8f, 0.8f), TMPro.TextAlignmentOptions.TopLeft);
            t.enableWordWrapping = true; t.overflowMode = TMPro.TextOverflowModes.Truncate;
            Place(t, new Vector2(0, 1), new Vector2(0, 1), topLeft, size);
            return t;
        }
    }
}
