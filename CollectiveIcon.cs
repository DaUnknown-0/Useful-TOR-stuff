// Useful TOR Stuff - Copyright (C) 2026 DaUnknown-0
// Licensed under GPL-3.0-or-later. See LICENSE for details.

/*
 * CollectiveIcon - the Unknown's Collective logo as a small round icon to the left of the
 * "Unknown's Collective (N)" line in PingTracker's text, during rounds (User 29.10.).
 *
 * The line is written by UnknownsCollective.Render (that file exists word for word in five repos,
 * so it stays untouched). This mod only puts a SpriteRenderer next to the line: it looks the line up
 * in the text's parsed characters (TMP's textInfo, at most five times a second because
 * GetParsedText allocates) and then follows the first character every frame. textInfo describes the
 * last mesh TMP built, so the icon trails a text change by one frame at most. Without this mod the
 * line simply stays plain text, as before.
 */

using System;
using TMPro;
using UnityEngine;
using Object = UnityEngine.Object;

namespace UsefulTORStuff
{
    internal static class CollectiveIcon
    {
        private const string Needle = "Unknown's Collective";
        private const float SearchEvery = 0.2f;

        private static SpriteRenderer _icon;
        private static TMP_Text _owner;
        private static int _index = -1;
        private static float _nextSearch;
        private static bool _failed;

        /// <summary>Called by the PingTracker postfix after the text for this frame is set.</summary>
        internal static void Tick(TMP_Text tmp)
        {
            if (_failed || tmp == null) return;
            try
            {
                var info = tmp.textInfo;
                if (ShipStatus.Instance == null || info == null) { Hide(); return; }

                if (Time.unscaledTime >= _nextSearch || !StillAt(info, _index))
                {
                    _nextSearch = Time.unscaledTime + SearchEvery;
                    string parsed = tmp.GetParsedText();
                    _index = parsed != null ? parsed.IndexOf(Needle, StringComparison.Ordinal) : -1;
                }
                if (_index < 0 || !StillAt(info, _index)) { Hide(); return; }

                var ci = info.characterInfo[_index];
                if (!ci.isVisible) { Hide(); return; }

                if (_icon == null || _owner != tmp) Create(tmp);
                if (_icon == null) return;

                // Cap height of the "U" decides the size; the icon is a bit taller than the line and
                // sits centred on it, one small gap to the left of the first letter.
                float h = Mathf.Max(0.01f, ci.topLeft.y - ci.bottomLeft.y);
                float size = h * 1.7f;
                float spriteH = _icon.sprite.bounds.size.y;
                _icon.transform.localScale = Vector3.one * (size / Mathf.Max(0.0001f, spriteH));
                float cy = (ci.topLeft.y + ci.bottomLeft.y) * 0.5f;
                _icon.transform.localPosition = new Vector3(ci.bottomLeft.x - size * 0.5f - h * 0.35f, cy, -0.01f);
                if (!_icon.enabled) _icon.enabled = true;
            }
            catch (Exception e)
            {
                _failed = true;
                UsefulTORStuffPlugin.Logger?.LogWarning($"[CollectiveIcon] disabled after an error: {e.Message}");
                Hide();
            }
        }

        private static bool StillAt(TMP_TextInfo info, int i) =>
            i >= 0 && i < info.characterCount && info.characterInfo[i].character == 'U';

        private static void Create(TMP_Text tmp)
        {
            if (_icon != null) Object.Destroy(_icon.gameObject);
            _icon = null;
            var sprite = UTSAssets.LogoIcon;
            if (sprite == null) { _failed = true; return; }
            var go = new GameObject("UTS_CollectiveIcon") { layer = tmp.gameObject.layer };
            go.transform.SetParent(tmp.transform, false);
            _icon = go.AddComponent<SpriteRenderer>();
            _icon.sprite = sprite;
            var r = tmp.GetComponent<Renderer>();
            if (r != null)
            {
                _icon.sortingLayerID = r.sortingLayerID;
                _icon.sortingOrder = r.sortingOrder + 1;
            }
            _owner = tmp;
        }

        private static void Hide()
        {
            if (_icon != null && _icon.enabled) _icon.enabled = false;
        }
    }
}
