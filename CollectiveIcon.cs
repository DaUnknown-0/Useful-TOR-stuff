// Useful TOR Stuff - Copyright (C) 2026 DaUnknown-0
// Licensed under GPL-3.0-or-later. See LICENSE for details.

/*
 * CollectiveIcon - the Unknown's Collective logo as a small round icon to the left of the
 * "Unknown's Collective (N)" line in PingTracker's text, during rounds (User 29.10.).
 *
 * The line is written by UnknownsCollective.Render (that file exists word for word in five repos,
 * so it stays untouched). This mod only puts a SpriteRenderer next to the line: it looks the line up
 * in the text's parsed characters (TMP's textInfo, at most five times a second because
 * GetParsedText allocates) and follows its first character at the same rate. textInfo describes the
 * last mesh TMP built, so the icon trails a text change by a fifth of a second at most. Without this mod the
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
        private static long _nextSearch;
        private static bool _failed;
        // PERF (2026-11-13): Tick used to read Unity state back every frame (Time.unscaledTime, the == null
        // checks, the character info, the sprite bounds, renderer.enabled), and each of those reads boxes a
        // value on the il2cpp heap through Il2CppInterop. The line only moves when the text above it changes,
        // so the whole lookup now runs five times a second; in between the icon simply stays where it is.
        private static float _spriteH = 1f;
        private static bool _shown;
        private static float _lastSize = -1f, _lastX = float.NaN, _lastY = float.NaN;
        private static bool _inRound;
        private static bool Gone(Object o) => ReferenceEquals(o, null) || o.m_CachedPtr == IntPtr.Zero;

        /// <summary>Called by the PingTracker postfix after the text for this frame is set.</summary>
        internal static void Tick(TMP_Text tmp)
        {
            if (_failed || Gone(tmp)) return;
            long now = Environment.TickCount64;
            if (now < _nextSearch) return;
            _nextSearch = now + (long)(SearchEvery * 1000f);
            try
            {
                _inRound = !Gone(ShipStatus.Instance);
                var info = tmp.textInfo;
                if (!_inRound || ReferenceEquals(info, null)) { Hide(); return; }

                string parsed = tmp.GetParsedText();
                _index = parsed != null ? parsed.IndexOf(Needle, StringComparison.Ordinal) : -1;
                if (_index < 0 || !StillAt(info, _index)) { Hide(); return; }

                var ci = info.characterInfo[_index];
                if (!ci.isVisible) { Hide(); return; }

                if (Gone(_icon) || ReferenceEquals(_owner, null) || _owner.Pointer != tmp.Pointer) Create(tmp);
                if (Gone(_icon)) return;

                // Cap height of the "U" decides the size; the icon is a bit taller than the line and
                // sits centred on it, one small gap to the left of the first letter.
                float h = Mathf.Max(0.01f, ci.topLeft.y - ci.bottomLeft.y);
                float size = h * 1.7f;
                float cy = (ci.topLeft.y + ci.bottomLeft.y) * 0.5f;
                float cx = ci.bottomLeft.x - size * 0.5f - h * 0.35f;
                if (size != _lastSize || cx != _lastX || cy != _lastY)
                {
                    _lastSize = size; _lastX = cx; _lastY = cy;
                    var t = _icon.transform;
                    t.localScale = Vector3.one * (size / Mathf.Max(0.0001f, _spriteH));
                    t.localPosition = new Vector3(cx, cy, -0.01f);
                }
                if (!_shown) { _icon.enabled = true; _shown = true; }
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
            _spriteH = sprite.bounds.size.y;
            _shown = true;
            _lastSize = -1f;
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
            if (_shown && !Gone(_icon)) _icon.enabled = false;
            _shown = false;
        }
    }
}
