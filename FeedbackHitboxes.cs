// Useful TOR Stuff - Copyright (C) 2026 DaUnknown-0
// Licensed under GPL-3.0-or-later. See LICENSE for details.

/*
 * FeedbackHitboxes - shows the solid colliders around the player as thin outlines (2026-11-05), so a tester can see
 * where a wall or a piece of furniture really blocks before writing "hitbox too big".
 *
 * Switched in the feedback editor and kept on after it closes, until switched off again (or the game restarts).
 * Only solid colliders count: triggers (vents, consoles, kill ranges) and anything on a player are left out, so
 * nothing hidden is shown. Hidden during meetings. The collider list is collected every two seconds (maps build
 * theirs at round start), the outlines within 9 m are redrawn ten times a second from a small pool of lines.
 */

using System;
using System.Collections.Generic;
using UnityEngine;
using Object = UnityEngine.Object;

namespace UsefulTORStuff;

internal static class FeedbackHitboxes
{
    internal static bool On { get; private set; }
    /// <summary>Outlines drawn in the last pass (self test).</summary>
    internal static int Drawn => used;

    private const float Radius = 9f, ScanEvery = 2f, DrawEvery = 0.1f, Width = 0.035f, Z = -8f;
    private const int CircleSteps = 18;
    private static readonly Color LineColor = new(1f, 0.25f, 0.85f, 0.9f);

    private static readonly List<Collider2D> solid = new();
    private static readonly List<LineRenderer> pool = new();
    private static readonly List<Vector3> points = new();
    private static GameObject root;
    private static Material material;
    private static float nextScan, nextDraw;
    private static int used;

    internal static void Toggle()
    {
        On = !On;
        nextScan = nextDraw = 0f;
        if (!On) HideAll();
        UsefulTORStuffPlugin.Logger.LogInfo($"[Feedback] hitbox view {(On ? "on" : "off")}");
    }

    internal static void Tick()
    {
        if (!On) return;
        var lp = PlayerControl.LocalPlayer;
        if (lp == null || MeetingHud.Instance != null) { HideAll(); return; }
        float now = Time.unscaledTime;
        if (now >= nextScan) { Scan(); nextScan = now + ScanEvery; }
        if (now < nextDraw) return;
        nextDraw = now + DrawEvery;
        Draw(lp.GetTruePosition());
    }

    private static void Scan()
    {
        solid.Clear();
        try {
            foreach (var c in Object.FindObjectsOfType<Collider2D>()) {
                if (c == null || !c.enabled || c.isTrigger || !c.gameObject.activeInHierarchy) continue;
                if (c.GetComponentInParent<PlayerControl>() != null) continue;
                solid.Add(c);
            }
        } catch (Exception e) { UsefulTORStuffPlugin.Logger.LogWarning($"[Feedback] hitbox scan failed: {e.Message}"); }
    }

    private static void Draw(Vector2 centre)
    {
        used = 0;
        foreach (var c in solid) {
            try {
                if (c == null || !c.enabled || !c.gameObject.activeInHierarchy) continue;
                var b = c.bounds;
                if (Vector2.Distance(b.ClosestPoint(centre), centre) > Radius) continue;
                Outline(c);
            } catch { }
        }
        for (int i = used; i < pool.Count; i++) if (pool[i] != null) pool[i].gameObject.SetActive(false);
    }

    private static void Outline(Collider2D c)
    {
        var t = c.transform;
        if (c.TryCast<BoxCollider2D>() is BoxCollider2D box) {
            Vector2 o = box.offset, h = box.size * 0.5f;
            points.Clear();
            points.Add(t.TransformPoint(o + new Vector2(-h.x, -h.y)));
            points.Add(t.TransformPoint(o + new Vector2(h.x, -h.y)));
            points.Add(t.TransformPoint(o + new Vector2(h.x, h.y)));
            points.Add(t.TransformPoint(o + new Vector2(-h.x, h.y)));
            Line(true);
        } else if (c.TryCast<CircleCollider2D>() is CircleCollider2D circle) {
            Vector3 m = t.TransformPoint(circle.offset);
            var s = t.lossyScale;
            float r = circle.radius * Mathf.Max(Mathf.Abs(s.x), Mathf.Abs(s.y));
            points.Clear();
            for (int i = 0; i < CircleSteps; i++) {
                float a = i * Mathf.PI * 2f / CircleSteps;
                points.Add(new Vector3(m.x + Mathf.Cos(a) * r, m.y + Mathf.Sin(a) * r, 0f));
            }
            Line(true);
        } else if (c.TryCast<PolygonCollider2D>() is PolygonCollider2D poly) {
            for (int p = 0; p < poly.pathCount; p++) {
                var path = poly.GetPath(p);
                points.Clear();
                foreach (var v in path) points.Add(t.TransformPoint(v + poly.offset));
                Line(true);
            }
        } else if (c.TryCast<EdgeCollider2D>() is EdgeCollider2D edge) {
            points.Clear();
            foreach (var v in edge.points) points.Add(t.TransformPoint(v + edge.offset));
            Line(false);
        } else {
            // capsule or composite: its bounds are close enough to see where it blocks
            var b = c.bounds;
            points.Clear();
            points.Add(new Vector3(b.min.x, b.min.y, 0f));
            points.Add(new Vector3(b.max.x, b.min.y, 0f));
            points.Add(new Vector3(b.max.x, b.max.y, 0f));
            points.Add(new Vector3(b.min.x, b.max.y, 0f));
            Line(true);
        }
    }

    private static void Line(bool loop)
    {
        if (points.Count < 2) return;
        var lr = Next();
        if (lr == null) return;
        lr.loop = loop;
        lr.positionCount = points.Count;
        for (int i = 0; i < points.Count; i++) lr.SetPosition(i, new Vector3(points[i].x, points[i].y, Z));
        lr.gameObject.SetActive(true);
    }

    private static LineRenderer Next()
    {
        if (used < pool.Count && pool[used] != null) return pool[used++];
        if (root == null) {
            root = new GameObject("UTS_FeedbackHitboxes") { layer = 11 };
            Object.DontDestroyOnLoad(root);
        }
        if (material == null) material = new Material(Shader.Find("Sprites/Default")) { hideFlags = HideFlags.HideAndDontSave };
        var go = new GameObject("hitbox") { layer = 11 };
        go.transform.SetParent(root.transform, false);
        var lr = go.AddComponent<LineRenderer>();
        lr.useWorldSpace = true;
        lr.sharedMaterial = material;
        lr.startWidth = lr.endWidth = Width;
        lr.startColor = lr.endColor = LineColor;
        lr.numCapVertices = 0;
        lr.sortingOrder = 32000;
        if (used < pool.Count) pool[used] = lr; else pool.Add(lr);
        used++;
        return lr;
    }

    private static void HideAll()
    {
        foreach (var lr in pool) if (lr != null) lr.gameObject.SetActive(false);
    }
}
