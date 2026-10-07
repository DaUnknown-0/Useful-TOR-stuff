// Useful TOR Stuff - Copyright (C) 2026 DaUnknown-0
// Licensed under GPL-3.0-or-later. See LICENSE for details.

/*
 * ColorblindLabels - the colour names of the game's colourblind mode where TOR draws players as
 * coloured dots without them.
 *
 * With "Colorblind Mode" on, the game writes each player's colour name under the crewmate. TOR adds
 * places that show players only as coloured icons, and there the name was missing:
 *   - the map: the Trapper's trapped players and the Snitch's evil players
 *     (TOR MapBehaviourPatch.herePoints);
 *   - the admin table while the Hacker's ability runs (TOR tints the CounterArea icons).
 * Each such icon gets a small label with the colour name, in the same font as the game's own
 * colourblind text. Anonymous dots stay anonymous: a Trapper with "anonymous map" gets no names,
 * and the Hacker's "only light/dark" mode says light or dark, nothing more.
 *
 * Only while the local player has colourblind mode on; nothing changes for anyone else. TOR's code
 * is not touched, everything is postfixes on the methods TOR already patches.
 */

using System;
using System.Collections.Generic;
using HarmonyLib;
using TheOtherRoles;
using UnityEngine;

namespace UsefulTORStuff {

    public static class ColorblindLabels {
        private const string LabelName = "UTSColorblindLabel";

        public static bool On() {
            try { return AmongUs.Data.DataManager.Settings.Accessibility.ColorBlindMode; } catch { return false; }
        }

        // ---- the label: a copy of the game's own colourblind text, so font and style match ----
        private static TMPro.TextMeshPro Template() {
            try {
                var lp = PlayerControl.LocalPlayer;
                return lp != null && lp.cosmetics != null ? lp.cosmetics.colorBlindText : null;
            } catch { return null; }
        }

        /// <summary>Puts (or updates, or hides with text null) the colour-name label under a map icon.</summary>
        internal static void Label(Transform icon, string text, float scale, float yOffset) {
            if (icon == null) return;
            var existing = icon.Find(LabelName);
            if (string.IsNullOrEmpty(text)) {
                if (existing != null && existing.gameObject.activeSelf) existing.gameObject.SetActive(false);
                return;
            }
            TMPro.TextMeshPro t;
            if (existing == null) {
                var tpl = Template();
                if (tpl == null) return;
                t = UnityEngine.Object.Instantiate(tpl, icon);
                t.name = LabelName;
                t.gameObject.layer = icon.gameObject.layer;
                t.enableWordWrapping = false;
                t.alignment = TMPro.TextAlignmentOptions.Center;
                t.color = Color.white;
                var sr = icon.GetComponent<SpriteRenderer>();
                var mr = t.GetComponent<MeshRenderer>();
                if (sr != null && mr != null) { mr.sortingLayerID = sr.sortingLayerID; mr.sortingOrder = sr.sortingOrder + 5; }
            } else {
                t = existing.GetComponent<TMPro.TextMeshPro>();
                if (t == null) return;
            }
            if (!t.gameObject.activeSelf) t.gameObject.SetActive(true);
            // the icon may be scaled; keep the label at the same on-screen size
            var ls = icon.lossyScale;
            float k = Mathf.Abs(ls.x) > 0.0001f ? 1f / Mathf.Abs(ls.x) : 1f;
            t.transform.localScale = new Vector3(scale * k, scale * k, 1f);
            t.transform.localPosition = new Vector3(0f, yOffset * k, -0.05f);
            if (t.text != text) t.text = text;
        }

        internal static string ColorName(int colorId) {
            try { if (colorId >= 0 && colorId < Palette.PlayerColors.Length) return Palette.GetColorName(colorId); } catch { }
            return null;
        }

        // TOR's class is internal; the field itself is public static
        private static System.Reflection.FieldInfo herePointsField;
        internal static Dictionary<byte, SpriteRenderer> HerePoints() {
            herePointsField ??= AccessTools.Field(AccessTools.TypeByName("TheOtherRoles.Patches.MapBehaviourPatch"), "herePoints");
            return herePointsField?.GetValue(null) as Dictionary<byte, SpriteRenderer>;
        }

        // ---- the map: ghosts' dots and the Trapper's trapped players ----
        [HarmonyPatch(typeof(MapBehaviour), nameof(MapBehaviour.FixedUpdate))]
        private static class MapPatch {
            [HarmonyPriority(Priority.Low)]   // after TOR's postfix, which creates the dots
            private static void Postfix() {
                try {
                    var points = HerePoints();
                    if (points == null || points.Count == 0) return;
                    bool on = On();
                    var lp = PlayerControl.LocalPlayer;
                    // the Trapper's anonymous map draws every dot grey: no names there
                    bool anonymous = lp != null && Trapper.trapper != null && lp.PlayerId == Trapper.trapper.PlayerId && Trapper.anonymousMap;
                    foreach (var kv in points) {
                        if (kv.Value == null) continue;
                        string text = null;
                        if (on && !anonymous) {
                            var p = Helpers.playerById(kv.Key);
                            // the colour the dot shows (Morphling/Camouflage change it), the same TOR used
                            if (p != null && p.CurrentOutfit != null) text = ColorName(p.CurrentOutfit.ColorId);
                        }
                        Label(kv.Value.transform, text, 0.55f, -0.32f);
                    }
                } catch { }
            }
        }

        // ---- the admin table with the Hacker's colours ----
        [HarmonyPatch(typeof(CounterArea), nameof(CounterArea.UpdateCount))]
        private static class AdminPatch {
            [HarmonyPriority(Priority.Low)]   // after TOR's postfix, which tints the icons
            private static void Postfix(CounterArea __instance) {
                try {
                    bool on = On();
                    bool hacking = Hacker.hacker != null && Hacker.hacker == PlayerControl.LocalPlayer && Hacker.hackerTimer > 0;
                    var icons = __instance.myIcons;
                    for (int i = 0; i < icons.Count; i++) {
                        var icon = icons[i];
                        if (icon == null) continue;
                        string text = null;
                        if (on && hacking && icon.gameObject.activeSelf) {
                            var r = icon.GetComponent<SpriteRenderer>();
                            if (r != null && r.material != null && r.material.HasProperty("_BodyColor")) {
                                Color c = r.material.GetColor("_BodyColor");
                                int id = IndexOfColor(c);
                                if (Hacker.onlyColorType)
                                    text = UTSLocalization.Tr(id == 7 ? "uts.colorblind.light" : "uts.colorblind.dark");
                                else
                                    text = ColorName(id);
                            }
                        }
                        Label(icon.transform, text, 0.45f, -0.28f);
                    }
                } catch { }
            }
        }

        // nearest palette colour (the material colour comes back as floats)
        private static int IndexOfColor(Color c) {
            int best = -1; float bestD = 0.02f;
            var pal = Palette.PlayerColors;
            for (int i = 0; i < pal.Length; i++) {
                Color p = pal[i];
                float d = Mathf.Abs(p.r - c.r) + Mathf.Abs(p.g - c.g) + Mathf.Abs(p.b - c.b);
                if (d < bestD) { bestD = d; best = i; }
            }
            return best;
        }
    }
}
