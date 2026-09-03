// Useful TOR Stuff - Copyright (C) 2026 DaUnknown-0
// Licensed under GPL-3.0-or-later. See LICENSE for details.
// Based on The Other Roles (https://github.com/TheOtherRolesAU/TheOtherRoles), GPL-3.0.

/*
 * MapLanguageToggle - a "[ Language: xxx ]" button in the bottom-right corner of the
 * MEETING minimap that opens a 3-column dropdown grid to switch the mod language on the
 * fly (deliberately meeting-only, same surface as the map ping - the round map stays
 * untouched).
 *
 * Clicking the button opens a panel with auto + all 26 languages (3 x 9 grid, row-major,
 * current selection highlighted). Clicking a cell writes UTSLocalization.ModLanguage -
 * its SettingChanged handler re-applies every table live, so the settings visible on the
 * very map you are looking at re-title immediately. While the panel is open EVERY click
 * is consumed (select or close), so MeetingMapPing never turns a menu click into a ping
 * (it asks IsPointerOverToggle() first).
 *
 * Rendering: world-space TMP + 1x1 white sprites parented to the map object (hidden with
 * it). IMPORTANT: the map background draws with its own sorting - anything without an
 * explicit sortingLayer/Order renders BEHIND it and is invisible (first version's bug).
 * All renderers therefore copy HerePoint's sorting layer and sit above its order. Per the
 * world-space overlay rules the button is re-fitted to the camera's bottom-right viewport
 * corner every frame and all hit tests run in LOCAL space. Display names avoid glyphs the
 * default TMP font lacks (no CJK, no Latin Extended Additional).
 */

using HarmonyLib;
using System;
using UnityEngine;

namespace UsefulTORStuff {
    public static class MapLanguageToggle {
        private const int Columns = 3;
        private const float CellW = 1.62f, CellH = 0.31f;

        // parallel arrays: config code <-> glyph-safe display name
        private static readonly string[] Codes = {
            "auto", "en", "german", "french", "spanish", "latam", "italian", "dutch",
            "portuguese", "brazilian", "russian", "japanese", "korean", "schinese",
            "tchinese", "filipino", "irish", "tr", "pl", "cs", "hu", "ro", "sv", "fi",
            "uk", "id", "vi"
        };
        // NOTE: the map TMP font misses some Latin-Ext-A/Cyrillic-Ext glyphs (playtest:
        // "Čeština" -> "□e□tina", "Українська" -> "Укра□нська") - those entries use their
        // English names instead.
        private static readonly string[] Names = {
            "auto", "English", "Deutsch", "Français", "Español", "Español (LA)", "Italiano",
            "Nederlands", "Português", "Português (BR)", "Русский", "Japanese", "Korean",
            "Chinese (S)", "Chinese (T)", "Filipino", "Gaeilge", "Türkçe", "Polski",
            "Czech", "Magyar", "Română", "Svenska", "Suomi", "Ukrainian", "Indonesia",
            "Tieng Viet"
        };

        // Among Us renders the HUD/UI layer through a SEPARATE "UI Camera", not Camera.main -
        // mouse hit-tests and corner fits through the wrong camera land offset (established
        // overlay rule; same resolver pattern as SettingsShare.FitCamera). Cached; shared with
        // MeetingMapPing.
        private static Camera fitCam;
        internal static Camera ResolveCamera(int layer) {
            if (fitCam != null && fitCam.isActiveAndEnabled && (fitCam.cullingMask & (1 << layer)) != 0)
                return fitCam;
            fitCam = null;
            var all = Camera.allCameras;
            foreach (var c in all)
                if (c != null && c.gameObject.name == "UI Camera" && (c.cullingMask & (1 << layer)) != 0) { fitCam = c; break; }
            if (fitCam == null)
                foreach (var c in all)
                    if (c != null && c != Camera.main && (c.cullingMask & (1 << layer)) != 0) { fitCam = c; break; }
            if (fitCam == null) fitCam = Camera.main;
            return fitCam;
        }

        private static GameObject root;          // parented to the map, holds button + panel
        private static TMPro.TextMeshPro buttonText;
        private static SpriteRenderer buttonBg;
        private static GameObject panel;
        private static TMPro.TextMeshPro[] cells;
        private static Sprite whiteSprite;
        private static bool open;
        private static int consumedClickFrame = -1;

        // ---------- click latch ----------
        //
        // Input.GetMouseButtonDown(0) is only reliable when read from Update: it is true for
        // exactly the one Update call in which the button went down. MapBehaviour has no Update,
        // only FixedUpdate, and Unity runs every FixedUpdate for a frame BEFORE that frame's
        // Update (see UTSShieldOutlines' header comment for the same ordering fact) - reading
        // GetMouseButtonDown directly from FixedUpdate can miss the click entirely (it fires
        // between two FixedUpdate calls) or, if FixedUpdate runs more than once per rendered
        // frame, "see" the same click several times over.
        //
        // Fix: latch the click once per rendered frame from a HudManager.Update postfix (HudManager
        // always ticks during a round, same as the other HudManager.Update-driven features in this
        // mod) into ClickFrame/ClickPos, public so MeetingMapPing's own FixedUpdate-based HandleClick
        // can share the exact same latch instead of reading Input itself. Because Update runs AFTER
        // this frame's FixedUpdate calls, a consumer only ever sees a given ClickFrame value one
        // frame late - consumers must therefore compare against "have I already handled this
        // ClickFrame value", not against the current Time.frameCount, so each logical click is still
        // handled exactly once no matter how many FixedUpdate calls happen to run before the next
        // Update ticks ClickFrame forward.
        public static int ClickFrame = -1;
        public static Vector3 ClickPos;
        private static int handledClickFrame = -1;

        [HarmonyPatch(typeof(HudManager), nameof(HudManager.Update))]
        private static class ClickLatchPatch {
            public static void Postfix() {
                if (Input.GetMouseButtonDown(0)) {
                    ClickFrame = Time.frameCount;
                    ClickPos = Input.mousePosition;
                }
            }
        }

        // The click that OPENS the map (the vanilla map button) is the same click the latch
        // above just captured. Without this, the very first FixedUpdate of the freshly-shown
        // map used to see that still-fresh ClickFrame as unhandled and immediately reprocess
        // it as a click ON the overlay (toggle button / ping), even though the player's click
        // actually landed on the "open map" button, not on anything drawn inside the map.
        // MapBehaviour.Show() fires exactly once per real open (unlike the FixedUpdate-based
        // root-activation flag, which only flips once per meeting, not once per open/close
        // cycle within the same meeting), so latching both consumers' "already handled" state
        // right here discards the opening click every time, regardless of how many times the
        // map is opened and closed during one meeting. Centralized here (rather than
        // duplicated per FixedUpdate postfix) so MeetingMapPing's own latch stays consistent
        // no matter which of the two FixedUpdate postfixes on MapBehaviour happens to run
        // first in a given frame.
        [HarmonyPatch(typeof(MapBehaviour), nameof(MapBehaviour.Show))]
        private static class ShowPatch {
            public static void Postfix() {
                handledClickFrame = Time.frameCount; // the frame the latch writes for this click, whichever Update ran first
                MeetingMapPing.DiscardOpeningClick();
            }
        }

        [HarmonyPatch(typeof(MapBehaviour), nameof(MapBehaviour.FixedUpdate))]
        private static class MapPatch {
            public static void Postfix(MapBehaviour __instance) {
                try {
                    // meeting-only, like the map ping (the round map stays untouched)
                    if (MeetingHud.Instance == null) {
                        if (root != null && root.activeSelf) { SetOpen(false); root.SetActive(false); }
                        return;
                    }
                    Ensure(__instance);
                    if (root == null) return;
                    if (!root.activeSelf) root.SetActive(true);
                    Refit(__instance);
                    HandleClick();
                } catch (Exception e) {
                    UsefulTORStuffPlugin.Logger?.LogError($"[MapLang] update failed: {e}");
                }
            }
        }

        // ---------- construction ----------

        private static void Ensure(MapBehaviour map) {
            if (root != null) return;
            var here = map.HerePoint;
            if (here == null) return;
            // A fresh root/buttonText instance is about to be built - force Refit's next call to
            // write .text at least once, even if this round's label happens to equal a stale value
            // left over from a previous (destroyed) instance.
            lastLabel = null;
            int layer = map.gameObject.layer;
            int sortLayerId = here.sortingLayerID;
            int sortBase = here.sortingOrder + 20; // safely above the map background/icons

            if (whiteSprite == null) {
                var tex = new Texture2D(1, 1, TextureFormat.ARGB32, false);
                tex.SetPixel(0, 0, Color.white);
                tex.Apply();
                tex.hideFlags |= HideFlags.HideAndDontSave;
                whiteSprite = Sprite.Create(tex, new Rect(0, 0, 1, 1), new Vector2(0.5f, 0.5f), 1f);
                whiteSprite.hideFlags |= HideFlags.HideAndDontSave;
            }

            root = new GameObject("UTSMapLangDropdown") { layer = layer };
            root.transform.SetParent(map.transform, false);
            root.transform.localScale = Vector3.one;

            // button backing + text
            buttonBg = NewSprite("btnBg", root.transform, layer, sortLayerId, sortBase,
                new Color(0f, 0f, 0f, 0.55f));
            buttonText = NewText("btnText", root.transform, layer, sortLayerId, sortBase + 1, 1.3f);
            buttonText.alignment = TMPro.TextAlignmentOptions.Center;

            // dropdown panel (hidden until the button is clicked)
            panel = new GameObject("panel") { layer = layer };
            panel.transform.SetParent(root.transform, false);
            int rows = (Codes.Length + Columns - 1) / Columns;
            var panelBg = NewSprite("panelBg", panel.transform, layer, sortLayerId, sortBase + 2,
                new Color(0.05f, 0.05f, 0.09f, 0.92f));
            panelBg.transform.localScale = new Vector3(Columns * CellW + 0.2f, rows * CellH + 0.2f, 1f);
            cells = new TMPro.TextMeshPro[Codes.Length];
            for (int i = 0; i < Codes.Length; i++) {
                var cell = NewText($"cell{i}", panel.transform, layer, sortLayerId, sortBase + 3, 1.05f);
                cell.alignment = TMPro.TextAlignmentOptions.Center;
                cell.text = Names[i];
                cell.transform.localPosition = CellCenter(i);
                cells[i] = cell;
            }
            panel.SetActive(false);
            open = false;
        }

        private static Vector3 CellCenter(int index) {
            int rows = (Codes.Length + Columns - 1) / Columns;
            int col = index % Columns, row = index / Columns;
            float x = (col - (Columns - 1) / 2f) * CellW;
            float y = ((rows - 1) / 2f - row) * CellH;
            return new Vector3(x, y, -0.02f);
        }

        private static SpriteRenderer NewSprite(string name, Transform parent, int layer,
                int sortLayerId, int order, Color color) {
            var go = new GameObject(name) { layer = layer };
            go.transform.SetParent(parent, false);
            var sr = go.AddComponent<SpriteRenderer>();
            sr.sprite = whiteSprite;
            sr.color = color;
            sr.sortingLayerID = sortLayerId;
            sr.sortingOrder = order;
            return sr;
        }

        private static TMPro.TextMeshPro NewText(string name, Transform parent, int layer,
                int sortLayerId, int order, float fontSize) {
            var go = new GameObject(name) { layer = layer };
            go.transform.SetParent(parent, false);
            var t = go.AddComponent<TMPro.TextMeshPro>();
            t.fontSize = fontSize;
            t.enableWordWrapping = false;
            var mr = go.GetComponent<MeshRenderer>();
            if (mr != null) { mr.sortingLayerID = sortLayerId; mr.sortingOrder = order; }
            return t;
        }

        // ---------- per-frame layout ----------

        // Last label string actually written to buttonText, and the button width computed from it
        // (ForceMeshUpdate + textBounds) - the label only changes when the language selection or the
        // open/closed arrow changes, not every tick, but this used to rebuild the TMP mesh and re-read
        // textBounds unconditionally every FixedUpdate the map is open. Invalidated in Ensure() and
        // SetOpen(): Ensure() because a fresh buttonText instance (a new map/root after the old one was
        // destroyed between rounds) must always get its first .text write even if this round's label
        // happens to equal a stale value left over from the previous instance; SetOpen() as a second,
        // belt-and-braces invalidation right at the one place the label's arrow suffix flips.
        private static string lastLabel;
        private static float lastBw = 1.6f;

        private static void Refit(MapBehaviour map) {
            var cam = ResolveCamera(root.layer);
            if (cam == null) return;

            string cfg = UTSLocalization.ModLanguage?.Value?.Trim().ToLowerInvariant() ?? "auto";
            int cur = Math.Max(0, Array.IndexOf(Codes, cfg));
            string shown = cur == 0 ? $"auto ({UTSLocalization.ActiveCode})" : Names[cur];
            string label = UTSLocalization.Tr("uts.maplang.label", shown) + (open ? "  ^" : "  v");

            // camera-fit per frame: pin the button to the bottom-right viewport corner
            Vector3 corner = cam.ViewportToWorldPoint(new Vector3(0.985f, 0.03f, 10f));
            Vector3 local = root.transform.parent.InverseTransformPoint(corner);
            local.z = -3f;

            float bw;
            if (label != lastLabel) {
                buttonText.text = label;
                buttonText.ForceMeshUpdate();
                var tb = buttonText.textBounds;
                bw = Mathf.Max(1.6f, tb.size.x + 0.3f);
                lastLabel = label;
                lastBw = bw;
            } else {
                bw = lastBw;
            }
            float bh = 0.34f;
            // anchor: right edge at the corner
            Vector3 btnCenter = local + new Vector3(-bw / 2f, bh / 2f, 0f);
            buttonText.transform.localPosition = btnCenter + new Vector3(0f, 0f, -0.02f);
            buttonBg.transform.localPosition = btnCenter;
            buttonBg.transform.localScale = new Vector3(bw, bh, 1f);

            if (open) {
                int rows = (Codes.Length + Columns - 1) / Columns;
                float panelH = rows * CellH + 0.2f, panelW = Columns * CellW + 0.2f;
                // panel sits above the button, right edges aligned
                panel.transform.localPosition = btnCenter
                    + new Vector3(bw / 2f - panelW / 2f, bh / 2f + panelH / 2f + 0.06f, -0.05f);
                for (int i = 0; i < cells.Length; i++)
                    cells[i].color = i == cur
                        ? new Color(1f, 0.83f, 0.2f)
                        : new Color(1f, 1f, 1f, 0.92f);
            }
        }

        // ---------- input ----------

        private static void HandleClick() {
            if (ClickFrame < 0 || ClickFrame == handledClickFrame) return;
            handledClickFrame = ClickFrame;
            var cam = ResolveCamera(root != null ? root.layer : 5);
            if (cam == null) return;
            Vector3 world = cam.ScreenToWorldPoint(ClickPos);

            if (open) {
                // panel open: EVERY click is ours - select a cell or just close
                consumedClickFrame = ClickFrame;
                int hit = CellAt(world);
                if (hit >= 0) {
                    UTSLocalization.ModLanguage.Value = Codes[hit]; // SettingChanged re-applies live
                    TrySwitchVanilla(Codes[hit]);
                }
                SetOpen(false);
                return;
            }
            if (OverButton(world)) {
                consumedClickFrame = ClickFrame;
                SetOpen(true);
            }
        }

        // For languages Among Us itself offers, switch the WHOLE GAME too - the mod texts
        // alone updating while the vanilla UI stays put reads as "doesn't work" (playtest
        // feedback). CurrentLanguage has a public setter; TranslationController.SetLanguage
        // fires the vanilla refresh (TextTranslatorTMP re-translates live UI, incl. the map
        // room names) AND our own SetLanguage postfix (mod re-apply). Tier-B codes have no
        // vanilla equivalent - the game language stays and the GetString postfix takes over
        // once the vanilla.<code>.json tables ship. "auto" changes nothing vanilla-side.
        private static void TrySwitchVanilla(string code) {
            try {
                if (code == "auto" || Array.IndexOf(UTSLocalization.TierBCodes, code) >= 0) return;
                string enumName = code == "en" ? "English" : code;
                if (!Enum.TryParse<SupportedLangs>(enumName, true, out var lang)) return;
                var settings = AmongUs.Data.DataManager.Settings;
                if (settings?.Language != null && settings.Language.CurrentLanguage != lang) {
                    settings.Language.CurrentLanguage = lang;
                    try { settings.Save(); } catch { }
                    // SetLanguage takes the language's TranslatedImageSet (Languages is the
                    // Dictionary<SupportedLangs, TranslatedImageSet> lookup).
                    var tc = DestroyableSingleton<TranslationController>.Instance;
                    if (tc != null && tc.Languages != null
                        && tc.Languages.TryGetValue(lang, out var set) && set != null)
                        tc.SetLanguage(set);
                }
            } catch (Exception e) {
                UsefulTORStuffPlugin.Logger?.LogWarning($"[MapLang] vanilla switch failed: {e.Message}");
            }
        }

        private static void SetOpen(bool value) {
            open = value;
            if (panel != null) panel.SetActive(value);
            // Belt-and-braces: the label's arrow suffix depends on `open`, so this already forces a
            // mismatch on the next Refit() naturally - invalidated explicitly anyway, right where the
            // suffix flips, rather than relying on that string comparison alone.
            lastLabel = null;
        }

        private static bool OverButton(Vector3 world) {
            if (buttonBg == null) return false;
            Vector3 p = buttonBg.transform.InverseTransformPoint(world);
            // the 1x1 sprite is scaled to bw x bh, so local space is a unit rect
            return Mathf.Abs(p.x) <= 0.5f + 0.05f && Mathf.Abs(p.y) <= 0.5f + 0.15f;
        }

        private static int CellAt(Vector3 world) {
            if (panel == null) return -1;
            Vector3 p = panel.transform.InverseTransformPoint(world);
            for (int i = 0; i < cells.Length; i++) {
                Vector3 c = CellCenter(i);
                if (Mathf.Abs(p.x - c.x) <= CellW / 2f && Mathf.Abs(p.y - c.y) <= CellH / 2f)
                    return i;
            }
            return -1;
        }

        /// MeetingMapPing calls this before turning a click into a ping. True while the
        /// dropdown is open (menu clicks must never ping) or when the pointer is on the button.
        /// Compares against ClickFrame (not Time.frameCount): consumedClickFrame is now also
        /// recorded in ClickFrame's timebase, so this stays correct no matter which of the two
        /// FixedUpdate postfixes on MapBehaviour (this one's or MeetingMapPing's) happens to
        /// run first in a given frame - both read the same latched click.
        public static bool IsPointerOverToggle(MapBehaviour map, Camera cam) {
            if ((ClickFrame >= 0 && ClickFrame == consumedClickFrame) || open) return true;
            try {
                var uiCam = ResolveCamera(root != null ? root.layer : map.gameObject.layer);
                return uiCam != null && OverButton(uiCam.ScreenToWorldPoint(ClickPos));
            } catch { return false; }
        }
    }
}
