// Useful TOR Stuff - Copyright (C) 2026 DaUnknown-0
// Feedback module, taken over from the standalone Unknown's Feedback (2026-10-30).
// Licensed under GPL-3.0-or-later. See LICENSE for details.

/*
 * FeedbackPick - choosing the place of a note: a point (click) or an area (drag a frame), either
 * in the game view or on the map.
 *
 * The note panel hides while picking; a banner at the bottom says what to do and carries a button that
 * opens / closes the map (not in the lobby, it has none). Esc goes back to the panel unchanged.
 *
 *   game view: Camera.main (the world camera) turns the screen point into world metres.
 *   map:       MapBehaviour draws the player at HerePoint.localPosition = world / MapScale, with x
 *              times the sign of the ship's x scale (the mirrored dlekS). So a click is taken into
 *              HerePoint's parent space through the camera that renders the map (the "UI Camera",
 *              see UTS MapLanguageToggle) and multiplied back. Atlas's maps keep that relation
 *              (AtlasMuseumBuilder sets MapScale and shifts that very space).
 *
 * A drag shorter than 12 pixels counts as a point, so an area pick can still place a point.
 */

using System;
using UnityEngine;
using UnityEngine.UI;

namespace UsefulTORStuff;

internal static class FeedbackPick
{
    internal const byte ShapeHere = 0, ShapePoint = 1, ShapeArea = 2;

    internal static bool Active { get; private set; }

    private static bool areaMode;
    private static Action<byte, Vector2, Vector2> onDone;   // shape, centre, size
    private static Action onCancel;
    private static Vector3? down;
    private static bool downOnMap;

    private static GameObject root, banner, mapButton;
    private static TMPro.TextMeshProUGUI hint, mapButtonText;
    private static RectTransform frame;

    internal static void Begin(bool area, Action<byte, Vector2, Vector2> done, Action cancel)
    {
        areaMode = area;
        onDone = done;
        onCancel = cancel;
        down = null;
        Build();
        root.SetActive(true);
        frame.gameObject.SetActive(false);
        Active = true;
    }

    internal static void Tick()
    {
        if (!Active) return;
        if (Input.GetKeyDown(KeyCode.Escape)) { End(); onCancel?.Invoke(); return; }

        bool mapOpen = MapOpen();
        bool lobby = ShipStatus.Instance == null;
        mapButton.SetActive(!lobby);
        VanillaUI.SetText(mapButtonText, FeedbackText.Get(mapOpen ? "pick.map_close" : "pick.map_open"));
        VanillaUI.SetText(hint, FeedbackText.Get(areaMode ? "pick.area" : "pick.point") + "  ·  " + FeedbackText.Get("pick.esc"));

        Vector3 mouse = Input.mousePosition;
        if (Input.GetMouseButtonDown(0) && !OverOurUi() && !(mapOpen && OnMapCloseStrip(mouse))) {
            down = mouse;
            downOnMap = mapOpen;
        }
        if (down.HasValue && areaMode && Input.GetMouseButton(0)) ShowFrame(down.Value, mouse);
        if (down.HasValue && Input.GetMouseButtonUp(0)) {
            var start = down.Value;
            down = null;
            frame.gameObject.SetActive(false);
            Complete(start, mouse, downOnMap);
        }
    }

    /// <summary>Turns a press and a release (screen pixels) into the pick. Also used by the self test.</summary>
    internal static bool Complete(Vector3 start, Vector3 end, bool onMap)
    {
        if (!ToWorld(start, onMap, out var a) || !ToWorld(end, onMap, out var b)) {
            FeedbackToast.Show(FeedbackText.Get("pick.failed"), FeedbackToast.Kind.Warn, 2.5f);
            return false;
        }
        bool area = areaMode && (start - end).magnitude >= 12f;
        byte shape = area ? ShapeArea : ShapePoint;
        Vector2 centre = area ? (a + b) * 0.5f : b;
        Vector2 size = area ? new Vector2(Mathf.Abs(a.x - b.x), Mathf.Abs(a.y - b.y)) : Vector2.zero;
        if (onMap) CloseMap();
        End();
        onDone?.Invoke(shape, centre, size);
        return true;
    }

    internal static void End()
    {
        Active = false;
        down = null;
        if (root != null) root.SetActive(false);
    }

    // ---- coordinates ----

    internal static bool ToWorld(Vector3 screen, bool onMap, out Vector2 world)
    {
        world = default;
        try {
            if (!onMap) {
                var cam = Camera.main;
                if (cam == null) return false;
                var w = cam.ScreenToWorldPoint(screen);
                world = new Vector2(w.x, w.y);
                return true;
            }
            var map = MapBehaviour.Instance;
            if (map == null || map.HerePoint == null) return false;
            var mapCam = MapCamera(map.gameObject.layer);
            if (mapCam == null) return false;
            return MapSpaceToWorld(map, mapCam.ScreenToWorldPoint(screen), out world);
        } catch { return false; }
    }

    /// <summary>A point in the map's render space (as the map camera sees it) to world metres.</summary>
    internal static bool MapSpaceToWorld(MapBehaviour map, Vector3 mapWorld, out Vector2 world)
    {
        world = default;
        var ship = ShipStatus.Instance;
        if (ship == null) return false;
        var local = map.HerePoint.transform.parent.InverseTransformPoint(mapWorld);
        float sign = Mathf.Sign(ship.transform.localScale.x);
        world = new Vector2(local.x * ship.MapScale * sign, local.y * ship.MapScale);
        return true;
    }

    private static Camera mapCam;

    internal static Camera MapCamera(int layer)
    {
        if (mapCam != null && mapCam.isActiveAndEnabled && (mapCam.cullingMask & (1 << layer)) != 0) return mapCam;
        mapCam = null;
        foreach (var c in Camera.allCameras)
            if (c != null && c.gameObject.name == "UI Camera" && (c.cullingMask & (1 << layer)) != 0) { mapCam = c; break; }
        if (mapCam == null)
            foreach (var c in Camera.allCameras)
                if (c != null && c != Camera.main && (c.cullingMask & (1 << layer)) != 0) { mapCam = c; break; }
        return mapCam ??= Camera.main;
    }

    // ---- map ----

    internal static bool MapOpen()
    {
        try { return MapBehaviour.Instance != null && MapBehaviour.Instance.IsOpen; } catch { return false; }
    }

    internal static void OpenMap()
    {
        try {
            var hud = HudManager.Instance;
            var lp = PlayerControl.LocalPlayer;
            if (hud == null || lp == null) return;
            // the game refuses the map while the player cannot move; the panel froze the player itself
            bool frozen = !lp.moveable;
            if (frozen) lp.moveable = true;
            try { hud.ToggleMapVisible(new MapOptions { Mode = MapOptions.Modes.Normal }); }
            finally { if (frozen) lp.moveable = false; }
        } catch (Exception e) { UsefulTORStuffPlugin.Logger.LogWarning($"[Feedback] map open failed: {e.Message}"); }
    }

    internal static void CloseMap()
    {
        try { if (MapOpen()) MapBehaviour.Instance.Close(); } catch { }
    }

    private static bool OnMapCloseStrip(Vector3 screen) => screen.y > Screen.height * 0.85f;

    private static bool OverOurUi()
    {
        try {
            var es = UnityEngine.EventSystems.EventSystem.current;
            return es != null && es.IsPointerOverGameObject();
        } catch { return false; }
    }

    // ---- view ----

    private static void ShowFrame(Vector3 a, Vector3 b)
    {
        float s = VanillaUI.Scale(root);
        var min = Vector2.Min(a, b) / s;
        var size = (Vector2)(Vector2.Max(a, b) - Vector2.Min(a, b)) / s;
        frame.anchoredPosition = min;
        frame.sizeDelta = size;
        frame.gameObject.SetActive(size.x > 2f || size.y > 2f);
    }

    private static void Build()
    {
        if (root != null) return;
        root = VanillaUI.Canvas("UnknownsFeedbackPick", 31001, true);

        // the frame being dragged: screen pixels / canvas scale, anchored bottom-left
        var f = VanillaUI.Ring(root, VanillaUI.Amber, 6, 4);
        frame = VanillaUI.Rect(f, Vector2.zero, Vector2.zero, Vector2.zero);
        var fill = VanillaUI.Rounded(f, new Color(1f, 0.75f, 0.2f, 0.15f), 6);
        VanillaUI.Stretch(fill);
        foreach (var img in f.GetComponentsInChildren<Image>(true)) img.raycastTarget = false;

        // at the bottom: on top it covered the map's upper rooms and its close arrow (picture 27.10.)
        banner = VanillaUI.Panel(root, new Vector2(1280f, 92f), new Vector2(0.5f, 0f), new Vector2(0.5f, 0f), new Vector2(0f, 16f));
        float inset = VanillaUI.FrameW + 22f;
        hint = VanillaUI.Label(banner, "", 22f, VanillaUI.TextColor, new Vector2(0, 0), new Vector2(1, 1), new Vector2(0, 0.5f),
                               new Vector2(inset, 0f), new Vector2(-inset - 260f - 2 * inset, -10f), TMPro.TextAlignmentOptions.Left);
        hint.rectTransform.anchorMax = new Vector2(1, 1);
        hint.rectTransform.offsetMin = new Vector2(inset, 6f);
        hint.rectTransform.offsetMax = new Vector2(-260f - inset, -6f);
        mapButton = VanillaUI.Button(banner, "", new Vector2(-inset, 0f), new Vector2(240f, 54f), VanillaUI.Teal,
                                     () => { if (MapOpen()) CloseMap(); else OpenMap(); }, out mapButtonText,
                                     new Vector2(1, 0.5f), new Vector2(1, 0.5f), new Vector2(1, 0.5f));
        root.SetActive(false);
    }
}
