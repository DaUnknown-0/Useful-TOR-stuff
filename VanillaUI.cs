// Useful TOR Stuff - Copyright (C) 2026 DaUnknown-0
// Licensed under GPL-3.0-or-later. See LICENSE for details.

/*
 * VanillaUI - the one place that knows what Among Us menus look like.
 *
 * Every UTS screen-space canvas (lobby menu, shields, session stats, replay, mod sync, mod manager)
 * builds from these pieces so they read as part of the game and not as something a mod bolted on:
 *
 *   Panel   the lobby info pane: a thick grey frame around a dark body, rounded corners, drop shadow
 *   Title   light grey upper-case heading, centred, with a thin rule beneath it
 *   Box     a dark rounded field like the CAPACITY / GAME MODE pills; rows and cards are boxes
 *   Button  the EDIT / VIEW button: flat colour, two diagonal light stripes, upper-case label that is
 *           dark on light buttons and white on dark ones; hover lays a light veil over it, pressing a
 *           dark one
 *   Tile    the square grey corner button (chat, settings, map): its sprites are taken from the
 *           game's own map button at runtime, so the tile IS the vanilla one; only the glyph is ours
 *   Text    the game's font wherever it has every glyph of the text, TMP's default otherwise
 *
 * All shapes but the tile are sprites generated once at runtime, so no asset ships with the mod and
 * nothing depends on object names that an Among Us update could rename. Where the game's sprites or
 * font are not reachable (main menu, early frames) the generated shapes stand in.
 */

using System;
using System.Collections.Generic;
using Il2CppInterop.Runtime;
using UnityEngine;
using UnityEngine.UI;

namespace UsefulTORStuff {

    public static class VanillaUI {

        // ---- palette, sampled from the lobby screen ----
        // Our own look in the game's visual language (2026-10-07): deep space-blue cards with a thick
        // pale outline (the game draws everything with heavy outlines), slanted tabs like the settings
        // screen's section headers, capsule buttons with a hard drop shadow, Brook for anything loud.
        public static readonly Color Frame = new Color(0.72f, 0.78f, 0.84f);         // thick pale outline
        public static readonly Color Body = new Color(0.09f, 0.11f, 0.15f);
        public static readonly Color Field = new Color(0.14f, 0.17f, 0.22f);          // section boxes, rows
        public static readonly Color FieldLight = new Color(0.21f, 0.26f, 0.33f);     // selected / hovered row
        public static readonly Color TitleColor = Color.white;
        public static readonly Color TextColor = Color.white;
        public static readonly Color Muted = new Color(0.66f, 0.72f, 0.80f);
        public static readonly Color Rule = new Color(0.30f, 0.36f, 0.44f);
        public static readonly Color Shadow = new Color(0f, 0f, 0f, 0.55f);
        public static readonly Color BackdropColor = new Color(0f, 0f, 0f, 0.72f);
        // buttons
        public static readonly Color Teal = new Color(0.16f, 0.70f, 0.66f);
        public static readonly Color TealBright = new Color(0.36f, 0.88f, 0.82f);     // the selected tab
        public static readonly Color Green = new Color(0.30f, 0.74f, 0.42f);
        public static readonly Color Amber = new Color(0.94f, 0.66f, 0.18f);
        public static readonly Color Red = new Color(0.86f, 0.28f, 0.32f);
        public static readonly Color Grey = new Color(0.36f, 0.42f, 0.50f);           // neutral
        public static readonly Color Blue = new Color(0.30f, 0.56f, 0.90f);
        public static readonly Color Pink = new Color(0.90f, 0.42f, 0.74f);
        // status text
        public static readonly Color Good = new Color(0.62f, 1f, 0.63f);
        public static readonly Color Warn = new Color(1f, 0.82f, 0.5f);
        public static readonly Color Bad = new Color(1f, 0.5f, 0.5f);

        public const float FrameW = 5f;                                              // the outline's width
        public const int RadiusPanel = 22, RadiusBox = 10, RadiusButton = 16, RadiusTile = 12;

        // ====================================================================
        // Canvas and backdrop
        // ====================================================================
        public static GameObject Canvas(string name, int order, bool blocking) {
            var go = new GameObject(name);
            UnityEngine.Object.DontDestroyOnLoad(go);
            var canvas = go.AddComponent<Canvas>();
            canvas.renderMode = RenderMode.ScreenSpaceOverlay;
            canvas.sortingOrder = order;
            var scaler = go.AddComponent<CanvasScaler>();
            scaler.uiScaleMode = CanvasScaler.ScaleMode.ScaleWithScreenSize;
            scaler.referenceResolution = new Vector2(1920, 1080);
            scaler.matchWidthOrHeight = 0.5f;
            var ray = go.AddComponent<GraphicRaycaster>();
            if (blocking) ray.blockingObjects = GraphicRaycaster.BlockingObjects.All;
            return go;
        }

        /// <summary>Dims the whole screen; a click on it runs onClick (usually: close).</summary>
        public static GameObject Backdrop(GameObject root, Action onClick) {
            var go = new GameObject("Backdrop");
            go.transform.SetParent(root.transform, false);
            Stretch(go);
            var img = go.AddComponent<Image>();
            img.sprite = Solid();
            img.color = BackdropColor;
            if (onClick != null) go.AddComponent<Button>().onClick.AddListener((UnityEngine.Events.UnityAction)(() => onClick()));
            return go;
        }

        // ====================================================================
        // Panel: frame + body + shadow. Children are placed on the returned object, so the frame
        // width has to be allowed for (FrameW on every side).
        // ====================================================================
        public static GameObject Panel(GameObject parent, Vector2 size, Vector2 anchor, Vector2 pivot, Vector2 pos) {
            var go = new GameObject("Panel");
            go.transform.SetParent(parent.transform, false);
            var rt = Rect(go, anchor, anchor, pivot);
            rt.sizeDelta = size; rt.anchoredPosition = pos;

            // hard offset shadow, dark body, thick pale outline on top (the game's heavy-outline look)
            Stretch(Named(Rounded(go, Shadow, RadiusPanel), "Shadow")).anchoredPosition = new Vector2(8, -10);
            var body = Rounded(go, Body, RadiusPanel);
            Stretch(body);
            body.GetComponent<Image>().raycastTarget = true;   // swallows clicks so the backdrop behind does not close
            Stretch(Ring(go, Frame, RadiusPanel, (int)FrameW));
            return go;
        }

        /// <summary>A panel centred on the screen.</summary>
        public static GameObject CenterPanel(GameObject root, Vector2 size) =>
            Panel(root, size, new Vector2(0.5f, 0.5f), new Vector2(0.5f, 0.5f), Vector2.zero);

        /// <summary>
        /// The heading of a panel like "GAME SETTINGS": the game's handwriting face, white, upper case,
        /// left-aligned at the top. Returns the y below it.
        /// </summary>
        public static float Title(GameObject panel, string text, float top = 16f, float size = 34f, Color? color = null) {
            // a slanted tab in the accent colour hanging from the top-left corner, the title on it
            float h = size * 1.2f + 14f;
            float w = Mathf.Clamp(text.Length * size * 0.62f + 90f, 260f, 760f);
            var tab = new GameObject("TitleTab");
            tab.transform.SetParent(panel.transform, false);
            var trt = Rect(tab, new Vector2(0, 1), new Vector2(0, 1), new Vector2(0, 1));
            trt.anchoredPosition = new Vector2(FrameW + 18f, -FrameW - top + 6f);
            trt.sizeDelta = new Vector2(w, h);
            var img = tab.AddComponent<Image>();
            img.sprite = Slant();
            img.type = Image.Type.Sliced;
            img.color = color ?? Teal;
            img.raycastTarget = false;

            var t = Label(tab, text, size * 1.15f, TitleColor, Vector2.zero, Vector2.one, new Vector2(0.5f, 0.5f),
                          Vector2.zero, new Vector2(-60f, 0f), TMPro.TextAlignmentOptions.Left, TMPro.FontStyles.UpperCase);
            t.rectTransform.offsetMin = new Vector2(22f, 0f);
            Heading(t);
            return -FrameW - top - h - 4f;
        }

        /// <summary>
        /// Gives a label the game's handwriting heading face (Brook) when it has the glyphs, and marks it
        /// so SetText keeps that face on later text changes.
        /// </summary>
        public static void Heading(TMPro.TextMeshProUGUI t) {
            if (t == null) return;
            t.gameObject.name = "H";
            var f = HeadingFont();
            if (f != null && GlyphsOk(f, t.text)) { t.font = f; t.fontStyle &= ~TMPro.FontStyles.Bold; }
            t.characterSpacing = 1f;
        }

        /// <summary>Smaller grey line under a title (hint, state).</summary>
        public static TMPro.TextMeshProUGUI Subtitle(GameObject panel, string text, float y, float height = 30f, float size = 15f) =>
            Label(panel, text, size, Muted, new Vector2(0, 1), new Vector2(1, 1), new Vector2(0.5f, 1),
                  new Vector2(0, y), new Vector2(-2 * (FrameW + 30f), height), TMPro.TextAlignmentOptions.Top);

        // ====================================================================
        // Boxes: dark rounded fields, positioned by their top-left corner
        // ====================================================================
        public static GameObject Box(GameObject parent, Vector2 topLeft, Vector2 size, Color? color = null, int radius = RadiusBox) {
            var go = Rounded(parent, color ?? Field, radius);
            var rt = Rect(go, new Vector2(0, 1), new Vector2(0, 1), new Vector2(0, 1));
            rt.anchoredPosition = topLeft; rt.sizeDelta = size;
            return go;
        }

        /// <summary>A box stretched across the parent's width (inset on both sides), by its top edge.</summary>
        public static GameObject Row(GameObject parent, float y, float height, float inset, Color? color = null) {
            var go = Rounded(parent, color ?? Field, RadiusBox);
            var rt = Rect(go, new Vector2(0, 1), new Vector2(1, 1), new Vector2(0.5f, 1));
            rt.anchoredPosition = new Vector2(0, y); rt.sizeDelta = new Vector2(-2 * inset, height);
            return go;
        }

        /// <summary>Makes a box clickable: a veil for hover and press, like the buttons.</summary>
        public static void Clickable(GameObject box, Action onClick, int radius = RadiusBox) => Hover(box, radius, onClick);

        // ====================================================================
        // Buttons
        // ====================================================================
        public static GameObject Button(GameObject parent, string label, Vector2 pos, Vector2 size, Color color, Action onClick,
                                        out TMPro.TextMeshProUGUI text, Vector2? anchorMin = null, Vector2? anchorMax = null,
                                        Vector2? pivot = null, float fontSize = 0f) {
            var go = new GameObject("Btn");
            go.transform.SetParent(parent.transform, false);
            var rt = Rect(go, anchorMin ?? new Vector2(0.5f, 0), anchorMax ?? new Vector2(0.5f, 0), pivot ?? new Vector2(0.5f, 0));
            rt.anchoredPosition = pos; rt.sizeDelta = size;

            // a capsule: flat colour, hard drop shadow, a darker outline of the same hue
            int radius = Mathf.Clamp(Mathf.RoundToInt(size.y / 2f) - 1, 4, 30);
            Stretch(Named(Rounded(go, Shadow, radius), "Shadow")).anchoredPosition = new Vector2(3, -5);
            var fill = Rounded(go, color, radius);
            Stretch(fill);
            var edge = Ring(go, Darker(color), radius, 3);
            edge.name = "Edge";
            Stretch(edge);

            if (fontSize <= 0f) fontSize = Mathf.Clamp(size.y * 0.6f, 15f, 34f);
            text = Label(go, label, fontSize, LabelColor(color), Vector2.zero, Vector2.one, new Vector2(0.5f, 0.5f),
                         Vector2.zero, new Vector2(-14, 0), TMPro.TextAlignmentOptions.Center, TMPro.FontStyles.UpperCase);
            Heading(text);
            text.enableAutoSizing = true; text.fontSizeMin = 11; text.fontSizeMax = fontSize;
            text.enableWordWrapping = false;

            Hover(go, RadiusButton, onClick);
            return go;
        }

        public static GameObject Button(GameObject parent, string label, Vector2 pos, Vector2 size, Color color, Action onClick,
                                        Vector2? anchorMin = null, Vector2? anchorMax = null, Vector2? pivot = null) =>
            Button(parent, label, pos, size, color, onClick, out _, anchorMin, anchorMax, pivot);

        /// <summary>
        /// The close control of every panel: the game's white square with a black X in the top-right
        /// corner (the label is kept for callers, the X needs none).
        /// </summary>
        public static GameObject CloseButton(GameObject panel, string label, Action onClose, float width = 240f) {
            var go = Button(panel, "X", new Vector2(-FrameW - 10f, -FrameW - 10f), new Vector2(54f, 54f), Red, onClose,
                            out var text, new Vector2(1, 1), new Vector2(1, 1), new Vector2(1, 1), 40f);
            go.name = "Close";
            text.fontStyle |= TMPro.FontStyles.Bold;
            return go;
        }

        /// <summary>Recolours a button built by Button(): fill, outline and label.</summary>
        public static void Recolor(GameObject button, Color color) {
            if (button == null) return;
            var fill = button.transform.Find("Fill");
            if (fill != null) fill.GetComponent<Image>().color = color;
            var edge = button.transform.Find("Edge");
            if (edge != null) edge.GetComponent<Image>().color = Darker(color);
            var t = button.GetComponentInChildren<TMPro.TextMeshProUGUI>();
            if (t != null) t.color = LabelColor(color);
        }

        private static Color Darker(Color c) => new Color(c.r * 0.55f, c.g * 0.55f, c.b * 0.55f, c.a);

        // white labels like the game's buttons; dark only on a nearly white fill
        public static Color LabelColor(Color fill) {
            float lum = 0.299f * fill.r + 0.587f * fill.g + 0.114f * fill.b;
            return lum < 0.8f ? Color.white : new Color(0.1f, 0.1f, 0.1f);
        }

        // ====================================================================
        // Text
        // ====================================================================
        public static TMPro.TextMeshProUGUI Label(GameObject parent, string text, float size, Color color,
                Vector2 anchorMin, Vector2 anchorMax, Vector2 pivot, Vector2 pos, Vector2 sizeDelta,
                TMPro.TextAlignmentOptions align, TMPro.FontStyles style = TMPro.FontStyles.Normal) {
            var go = new GameObject("L");
            go.transform.SetParent(parent.transform, false);
            var rt = Rect(go, anchorMin, anchorMax, pivot);
            rt.anchoredPosition = pos; rt.sizeDelta = sizeDelta;
            var t = go.AddComponent<TMPro.TextMeshProUGUI>();
            t.fontSize = size; t.fontStyle = style; t.color = color;
            t.alignment = align; t.enableWordWrapping = true;
            t.raycastTarget = false;
            SetText(t, text);
            return t;
        }

        /// <summary>Text positioned by its top-left corner, top-left aligned.</summary>
        public static TMPro.TextMeshProUGUI Text(GameObject parent, string text, float size, Color color, Vector2 topLeft, Vector2 box,
                                                 TMPro.FontStyles style = TMPro.FontStyles.Normal,
                                                 TMPro.TextAlignmentOptions align = TMPro.TextAlignmentOptions.TopLeft) =>
            Label(parent, text, size, color, new Vector2(0, 1), new Vector2(0, 1), new Vector2(0, 1), topLeft, box, align, style);

        /// <summary>Sets the text and picks the font: the game's where it has every glyph, TMP's default otherwise.</summary>
        public static void SetText(TMPro.TextMeshProUGUI t, string text) {
            if (t == null) return;
            text = text ?? "";
            // labels marked by Heading() keep the handwriting face while it has the glyphs
            var game = t.gameObject.name == "H" ? HeadingFont() : null;
            if (game == null || !GlyphsOk(game, text)) game = Font();
            var target = game != null && GlyphsOk(game, text) ? game : DefaultFont();
            if (target != null && t.font != target) t.font = target;
            if (t.text != text) t.text = text;
        }

        private static TMPro.TMP_FontAsset gameFont, headingFont, defaultFont;
        private static float fontSearchedAt = -100f;
        private static bool fontsLogged;

        private static TMPro.TMP_FontAsset DefaultFont() {
            if (defaultFont == null) { try { defaultFont = TMPro.TMP_Settings.defaultFontAsset; } catch { } }
            return defaultFont;
        }

        /// <summary>The game's body face (Barlow, upright bold) for labels and values.</summary>
        public static TMPro.TMP_FontAsset Font() {
            if (gameFont == null) SearchFonts();
            return gameFont;
        }

        /// <summary>The game's handwriting heading face (Brook: GAME SETTINGS, PLAY, EDIT), null until found.</summary>
        public static TMPro.TMP_FontAsset HeadingFont() {
            if (headingFont == null) SearchFonts();
            return headingFont;
        }

        // Both faces are picked by name from the loaded font assets (once every few seconds until
        // found). Picking by name, not from a HUD label: the lobby's player counter turned out to be
        // the italic Barlow cut (2026-10-07), which made every panel look slanted.
        private static void SearchFonts() {
            if (Time.realtimeSinceStartup - fontSearchedAt < 3f) return;
            fontSearchedAt = Time.realtimeSinceStartup;
            try {
                var all = Resources.FindObjectsOfTypeAll(Il2CppType.Of<TMPro.TMP_FontAsset>());
                var names = new List<string>();
                int bodyScore = -1, headScore = -1;
                foreach (var o in all) {
                    var f = o.TryCast<TMPro.TMP_FontAsset>();
                    if (f == null) continue;
                    names.Add(f.name);
                    string n = f.name.ToLowerInvariant();
                    if (n.Contains("outline") || n.Contains("italic") || n.Contains("masked")) continue;
                    if (n.Contains("barlow")) {
                        // the plain bold cut first, then any upright Barlow
                        int score = n.Contains("bold") ? 3 : n.Contains("semibold") ? 2 : 1;
                        if (score > bodyScore) { bodyScore = score; gameFont = f; }
                    } else if (n.Contains("brook")) {
                        int score = n.Contains("sdf") ? 2 : 1;
                        if (score > headScore) { headScore = score; headingFont = f; }
                    }
                }
                if (!fontsLogged && names.Count > 0) {
                    fontsLogged = true;
                    UsefulTORStuffPlugin.Logger?.LogInfo($"[VanillaUI] fonts: body={(gameFont != null ? gameFont.name : "-")}, "
                        + $"heading={(headingFont != null ? headingFont.name : "-")}; loaded: {string.Join(", ", names)}");
                }
            } catch (Exception ex) {
                UsefulTORStuffPlugin.Logger?.LogWarning($"[VanillaUI] font search failed: {ex.Message}");
            }
        }

        private static bool GlyphsOk(TMPro.TMP_FontAsset font, string text) {
            try { return font.HasCharacters(text); } catch { return false; }
        }

        // ====================================================================
        // Tile: the vanilla square corner button, sprites borrowed from the map button
        // ====================================================================
        private static Sprite tileBack;

        /// <summary>The grey tile sprite of the chat / settings / map buttons, or null outside the HUD.</summary>
        public static Sprite TileSprite() {
            if (tileBack != null) return tileBack;
            try {
                if (!DestroyableSingleton<HudManager>.InstanceExists) return null;
                var hud = HudManager.Instance;
                if (hud == null || hud.MapButton == null) return null;
                var bg = hud.MapButton.transform.Find("Background");
                var sr = bg != null ? bg.GetComponent<SpriteRenderer>() : null;
                if (sr != null && sr.sprite != null) {
                    tileBack = sr.sprite;
                    tileBack.hideFlags |= HideFlags.DontUnloadUnusedAsset;
                }
            } catch (Exception ex) {
                UsefulTORStuffPlugin.Logger?.LogWarning($"[VanillaUI] tile sprite lookup failed: {ex.Message}");
            }
            return tileBack;
        }

        /// <summary>
        /// A corner tile with a glyph drawn by glyph(texture size) and a hover/press veil. Uses the game's own
        /// tile sprite when the HUD exists and a drawn stand-in otherwise. Bottom-left anchored.
        /// </summary>
        public static GameObject Tile(GameObject parent, float size, Sprite glyph, Color glyphColor, Action onClick) {
            var go = new GameObject("Tile");
            go.transform.SetParent(parent.transform, false);
            var rt = Rect(go, Vector2.zero, Vector2.zero, Vector2.zero);
            rt.sizeDelta = new Vector2(size, size);

            var vanilla = TileSprite();
            if (vanilla != null) {
                var back = new GameObject("Back");
                back.transform.SetParent(go.transform, false);
                Stretch(back);
                var img = back.AddComponent<Image>();
                img.sprite = vanilla;
                img.preserveAspect = true;
                img.raycastTarget = false;
            } else {
                // the stand-in: light frame, darker body
                Stretch(Named(Rounded(go, Shadow, RadiusTile), "Shadow")).anchoredPosition = new Vector2(0, -4);
                Stretch(Rounded(go, new Color(0.76f, 0.78f, 0.8f), RadiusTile));
                Inset(Stretch(Rounded(go, new Color(0.4f, 0.43f, 0.46f), RadiusTile - 4)), 5f);
            }

            var icon = new GameObject("Glyph");
            icon.transform.SetParent(go.transform, false);
            Inset(Stretch(icon), size * 0.22f);
            var gi = icon.AddComponent<Image>();
            gi.sprite = glyph;
            gi.color = glyphColor;
            gi.preserveAspect = true;
            gi.raycastTarget = false;

            Hover(go, RadiusTile, onClick);
            return go;
        }

        // ====================================================================
        // Glyphs: small white shapes for tiles and badges, tinted by Image.color
        // ====================================================================
        private static Sprite glyphMenu, glyphDownload, circle;

        /// <summary>Three rounded bars.</summary>
        public static Sprite GlyphMenu() {
            if (glyphMenu != null) return glyphMenu;
            glyphMenu = Draw(64, 64, (x, y) => {
                float a = 0f;
                for (int i = 0; i < 3; i++) a = Mathf.Max(a, RoundRect(x, y, 6, 10 + i * 19, 52, 11, 5));
                return a;
            });
            return glyphMenu;
        }

        /// <summary>An arrow down onto a tray.</summary>
        public static Sprite GlyphDownload() {
            if (glyphDownload != null) return glyphDownload;
            glyphDownload = Draw(64, 64, (x, y) => {
                float a = RoundRect(x, y, 8, 50, 48, 9, 4);                 // tray
                a = Mathf.Max(a, RoundRect(x, y, 27, 6, 10, 26, 4));         // shaft
                // head: a triangle from y 28 to 44, 32 wide at the top
                float ty = y - 28f;
                if (ty >= 0 && ty <= 16) {
                    float half = 16f * (1f - ty / 16f) + 1f;
                    float dx = Mathf.Abs(x - 32f);
                    a = Mathf.Max(a, Mathf.Clamp01(half - dx + 0.5f));
                }
                return a;
            });
            return glyphDownload;
        }

        /// <summary>White anti-aliased disc.</summary>
        public static Sprite Circle() {
            if (circle != null) return circle;
            circle = Draw(96, 96, (x, y) => {
                float dx = x - 48f, dy = y - 48f;
                return Mathf.Clamp01(47f - Mathf.Sqrt(dx * dx + dy * dy) + 0.5f);
            });
            return circle;
        }

        private static float RoundRect(float px, float py, float x, float y, float w, float h, float r) {
            float cx = x + w / 2f, cy = y + h / 2f;
            float qx = Mathf.Abs(px - cx) - (w / 2f - r), qy = Mathf.Abs(py - cy) - (h / 2f - r);
            float outside = new Vector2(Mathf.Max(qx, 0), Mathf.Max(qy, 0)).magnitude + Mathf.Min(Mathf.Max(qx, qy), 0) - r;
            return Mathf.Clamp01(0.5f - outside);
        }

        private static Sprite Draw(int w, int h, Func<float, float, float> alpha) {
            var tex = new Texture2D(w, h, TextureFormat.RGBA32, false);
            var px = new Color[w * h];
            for (int y = 0; y < h; y++)
                for (int x = 0; x < w; x++)
                    px[y * w + x] = new Color(1f, 1f, 1f, alpha(x + 0.5f, h - (y + 0.5f)));   // y down, like a drawing
            tex.SetPixels(px);
            tex.filterMode = FilterMode.Bilinear;
            tex.wrapMode = TextureWrapMode.Clamp;
            tex.Apply(false, true);
            var s = Sprite.Create(tex, new Rect(0, 0, w, h), new Vector2(0.5f, 0.5f), 100f);
            tex.hideFlags |= HideFlags.DontUnloadUnusedAsset;
            s.hideFlags |= HideFlags.DontUnloadUnusedAsset;
            return s;
        }

        // ====================================================================
        // Shapes
        // ====================================================================
        private static Sprite solid;

        public static Sprite Solid() {
            if (solid != null) return solid;
            var tex = new Texture2D(1, 1);
            tex.SetPixel(0, 0, Color.white);
            tex.Apply();
            solid = Sprite.Create(tex, new Rect(0, 0, 1, 1), new Vector2(0.5f, 0.5f));
            tex.hideFlags |= HideFlags.DontUnloadUnusedAsset;
            solid.hideFlags |= HideFlags.DontUnloadUnusedAsset;
            return solid;
        }

        /// <summary>A filled, sliced rounded rectangle with the given corner radius (canvas units).</summary>
        // the drop shadows are Rounded too; renamed so Find("Fill") gets the real fill
        private static GameObject Named(GameObject go, string name) { go.name = name; return go; }

        public static GameObject Rounded(GameObject parent, Color color, int radius) {
            var go = new GameObject(radius <= 1 ? "Rule" : "Fill");
            go.transform.SetParent(parent.transform, false);
            go.AddComponent<RectTransform>();
            var img = go.AddComponent<Image>();
            img.sprite = RoundedSprite(Mathf.Max(radius, 1));
            img.type = Image.Type.Sliced;
            img.color = color;
            img.raycastTarget = false;
            return go;
        }

        private static readonly Dictionary<int, Sprite> roundedCache = new Dictionary<int, Sprite>();

        public static Sprite RoundedSprite(int radius) {
            if (roundedCache.TryGetValue(radius, out var cached) && cached != null) return cached;
            int n = radius * 2 + 4;
            var tex = new Texture2D(n, n, TextureFormat.RGBA32, false);
            var px = new Color[n * n];
            float half = n / 2f;
            for (int y = 0; y < n; y++)
                for (int x = 0; x < n; x++) {
                    float qx = Mathf.Abs(x + 0.5f - half) - (half - radius);
                    float qy = Mathf.Abs(y + 0.5f - half) - (half - radius);
                    float outside = new Vector2(Mathf.Max(qx, 0), Mathf.Max(qy, 0)).magnitude + Mathf.Min(Mathf.Max(qx, qy), 0) - radius;
                    px[y * n + x] = new Color(1f, 1f, 1f, Mathf.Clamp01(0.5f - outside));
                }
            tex.SetPixels(px);
            tex.filterMode = FilterMode.Bilinear;
            tex.wrapMode = TextureWrapMode.Clamp;
            tex.Apply(false, true);
            float b = radius + 2;
            var sprite = Sprite.Create(tex, new Rect(0, 0, n, n), new Vector2(0.5f, 0.5f), 100f, 0,
                                       SpriteMeshType.FullRect, new Vector4(b, b, b, b));
            tex.hideFlags |= HideFlags.DontUnloadUnusedAsset;
            sprite.hideFlags |= HideFlags.DontUnloadUnusedAsset;
            roundedCache[radius] = sprite;
            return sprite;
        }

        /// <summary>A rounded ring (outline only) of the given radius and border width, sliced.</summary>
        public static GameObject Ring(GameObject parent, Color color, int radius, int border) {
            var go = new GameObject("Outline");
            go.transform.SetParent(parent.transform, false);
            go.AddComponent<RectTransform>();
            var img = go.AddComponent<Image>();
            img.sprite = RingSprite(Mathf.Max(radius, 2), Mathf.Clamp(border, 1, radius));
            img.type = Image.Type.Sliced;
            img.color = color;
            img.raycastTarget = false;
            return go;
        }

        private static readonly Dictionary<int, Sprite> ringCache = new Dictionary<int, Sprite>();

        private static Sprite RingSprite(int radius, int border) {
            int key = radius * 100 + border;
            if (ringCache.TryGetValue(key, out var cached) && cached != null) return cached;
            int n = radius * 2 + 4;
            var tex = new Texture2D(n, n, TextureFormat.RGBA32, false);
            var px = new Color[n * n];
            float half = n / 2f;
            for (int y = 0; y < n; y++)
                for (int x = 0; x < n; x++) {
                    float qx = Mathf.Abs(x + 0.5f - half) - (half - radius);
                    float qy = Mathf.Abs(y + 0.5f - half) - (half - radius);
                    float outside = new Vector2(Mathf.Max(qx, 0), Mathf.Max(qy, 0)).magnitude + Mathf.Min(Mathf.Max(qx, qy), 0) - radius;
                    float a = Mathf.Clamp01(0.5f - outside) * Mathf.Clamp01(outside + border + 0.5f);
                    px[y * n + x] = new Color(1f, 1f, 1f, a);
                }
            tex.SetPixels(px);
            tex.filterMode = FilterMode.Bilinear;
            tex.wrapMode = TextureWrapMode.Clamp;
            tex.Apply(false, true);
            float b = radius + 2;
            var sprite = Sprite.Create(tex, new Rect(0, 0, n, n), new Vector2(0.5f, 0.5f), 100f, 0,
                                       SpriteMeshType.FullRect, new Vector4(b, b, b, b));
            tex.hideFlags |= HideFlags.DontUnloadUnusedAsset;
            sprite.hideFlags |= HideFlags.DontUnloadUnusedAsset;
            ringCache[key] = sprite;
            return sprite;
        }

        // The slanted tab of the settings screen's section headers ("IMPOSTORS"): square on the
        // left, the right edge leaning. Sliced so the slant keeps its angle at any width.
        private static Sprite slant;

        public static Sprite Slant() {
            if (slant != null) return slant;
            const int h = 48, lean = 22, w = 40 + lean + 8;
            slant = Draw(w, h, (x, y) => {
                // left and bottom edges straight, top-left corner rounded (r 8), right edge leaning
                float r = 8f;
                float cx = Mathf.Max(x, r), cy = Mathf.Min(y, h - r);
                float corner = (x < r && y > h - r) ? Mathf.Clamp01(r - Mathf.Sqrt((x - r) * (x - r) + (y - (h - r)) * (y - (h - r))) + 0.5f) : 1f;
                float rightEdge = w - 1f - lean * (1f - y / h);   // leans: wide at the bottom, narrow at the top
                float a = Mathf.Clamp01(rightEdge - x + 0.5f);
                return corner * a;
            });
            slant = Sprite.Create(slant.texture, new Rect(0, 0, w, h), new Vector2(0.5f, 0.5f), 100f, 0,
                                  SpriteMeshType.FullRect, new Vector4(12, 2, lean + 8, 2));
            slant.hideFlags |= HideFlags.DontUnloadUnusedAsset;
            return slant;
        }

        // A translucent veil over the whole target: invisible at rest, white on hover, dark while pressed.
        public static void Hover(GameObject target, int radius, Action onClick) {
            // the Button tints the image's own colour, so that stays white and the states carry the alpha
            var veil = Rounded(target, Color.white, radius);
            Stretch(veil);
            var vimg = veil.GetComponent<Image>();
            vimg.raycastTarget = true;
            var b = veil.AddComponent<Button>();
            b.targetGraphic = vimg;
            var c = b.colors;
            c.normalColor = new Color(1f, 1f, 1f, 0f);
            c.highlightedColor = new Color(1f, 1f, 1f, 0.2f);
            c.pressedColor = new Color(0f, 0f, 0f, 0.25f);
            c.selectedColor = new Color(1f, 1f, 1f, 0f);
            c.colorMultiplier = 1f;
            c.fadeDuration = 0.06f;
            b.colors = c;
            // the Button applies its normal colour in Start, one frame later: without this the veil is
            // solid white for that frame (seen as a white flash and bleached screenshots, 2026-10-07)
            vimg.CrossFadeColor(c.normalColor, 0f, true, true);
            if (onClick != null) b.onClick.AddListener((UnityEngine.Events.UnityAction)(() => onClick()));
        }

        // ====================================================================
        // Rect helpers
        // ====================================================================
        public static RectTransform Rect(GameObject go, Vector2 anchorMin, Vector2 anchorMax, Vector2 pivot) {
            var rt = go.GetComponent<RectTransform>();
            if (rt == null) rt = go.AddComponent<RectTransform>();
            rt.anchorMin = anchorMin; rt.anchorMax = anchorMax; rt.pivot = pivot;
            return rt;
        }

        public static RectTransform Stretch(GameObject go) {
            var rt = Rect(go, Vector2.zero, Vector2.one, new Vector2(0.5f, 0.5f));
            rt.sizeDelta = Vector2.zero; rt.anchoredPosition = Vector2.zero;
            return rt;
        }

        public static RectTransform Inset(RectTransform rt, float margin) {
            rt.offsetMin = new Vector2(margin, margin);
            rt.offsetMax = new Vector2(-margin, -margin);
            return rt;
        }

        /// <summary>Canvas scale factor of a screen-space canvas (for screen-to-canvas conversions).</summary>
        public static float Scale(GameObject canvasRoot) {
            var c = canvasRoot != null ? canvasRoot.GetComponent<Canvas>() : null;
            return c != null && c.scaleFactor > 0f ? c.scaleFactor : 1f;
        }
    }
}
