// Useful TOR Stuff - Copyright (C) 2026 DaUnknown-0
// Licensed under GPL-3.0-or-later. See LICENSE for details.
// Based on The Other Roles (https://github.com/TheOtherRolesAU/TheOtherRoles), GPL-3.0.

/*
 * RoundReplayWorld - the replay's player perspective on the real map (User 2026-10-02: "is there
 * really no way to load the map in the lobby, for an accurate replay?").
 *
 * The ship prefabs are Addressables (AmongUsClient.ShipPrefabs, index = map id), so they load in the
 * lobby too. The prefab is instantiated under an INACTIVE holder far away from the lobby, so no Awake
 * runs, and every MonoBehaviour is destroyed before the holder is switched on: what is left are the
 * renderers, the animators and the colliders, i.e. a picture of the ship with its walls but without
 * a ShipStatus, consoles, vents or doors that could reach into the game (ShipStatus.Instance stays
 * null). The doors' colliders are kept and switched from the recorded door log, so closed doors block
 * sight as they did.
 *
 * A camera of our own renders the area around the chosen player into a texture in the replay panel.
 * On top lies a darkness mask cut like his light: his recorded radius and rays against
 * Constants.ShadowMask on the copied walls. Figures (PoolablePlayer, the end screen's bean) show the
 * other players in the look he saw (Morphling, Camouflage), and only when the recording says he could
 * see them; bodies lie where players died until the next meeting or a Cleaner/Vulture.
 *
 * Host only, loaded when the panel opens, released when it closes.
 *
 * MAPS BUILT AT RUNTIME (Unknown's Atlas, which builds its maps over the Skeld; Submerged and any
 * other map the Addressables cannot give us): the host copies the LIVE ship at the round end
 * (OnGameEnd prefix, the ship is still intact) under an inactive, DontDestroyOnLoad holder. Atlas
 * hangs its whole world under the ship and creates its textures HideAndDontSave, so the copy keeps
 * them; the Skeld materials Atlas reuses (MaskingShader...) live in the Skeld bundle, which the host
 * therefore keeps loaded with a handle of its own until the next round starts. The copy is stripped
 * when the panel first opens and kept (switched off) between openings.
 */

using System;
using System.Collections.Generic;
using System.Linq;
using TheOtherRoles;
using UnityEngine;
using UnityEngine.AddressableAssets;
using UnityEngine.ResourceManagement.AsyncOperations;
using UnityEngine.UI;

namespace UsefulTORStuff {

    public static partial class RoundReplay {

        private static readonly Vector3 WorldOffset = new Vector3(800f, 800f, 0f);
        // The game's light, measured 2026-10-02 against freeplay screenshots on the Skeld (radius 5 and 1):
        // it ends exactly at CalculateLightRadius (world units), its brightness falls LINEARLY from the
        // source to that edge, and unlit floor keeps about 23 % of its lit brightness. The source sits
        // 0.1 below the player's position.
        private const int MaskW = 240, WorldRays = 360;
        private const float WorldDark = 0.77f, WorldEdge = 0.15f, WorldWallBias = 0.2f;
        internal static readonly Vector2 LightOffset = new Vector2(0f, -0.1f);

        private static int worldState;                 // 0 none, 1 loading, 2 ready, 3 failed
        private static AsyncOperationHandle<GameObject> worldHandle;
        private static bool worldHandleSet;
        private static GameObject worldHolder, worldFigures;
        private static Vector2 worldShift;             // game world -> copy
        private static readonly List<(Collider2D[] Cols, SpriteRenderer[] Rends)> worldDoors = new List<(Collider2D[], SpriteRenderer[])>();
        private static Camera worldCam;
        private static RenderTexture worldRt;
        private static GameObject worldArea;
        private static Texture2D worldMaskTex;
        private static Color32[] worldMaskPx;
        private static int worldMaskH;
        private static readonly float[] worldRayLen = new float[WorldRays];
        private static readonly Dictionary<byte, (PoolablePlayer P, string Key, Vector2 Last)> figures = new Dictionary<byte, (PoolablePlayer, string, Vector2)>();
        private static readonly Dictionary<byte, GameObject> bodies = new Dictionary<byte, GameObject>();
        private static int worldDoorSample = -1;
        private static float worldZoom = 1f;
        private static bool worldView = true;          // the host can fall back to the minimap
        private static TMPro.TextMeshProUGUI viewLabel;

        // What the live ship had switched off (seasonal decorations, map variants: scripts toggle them,
        // and the copy has no scripts). Paths of sibling indices + names from the ship root, taken at
        // the round end while the ship still exists.
        private static readonly List<(int[] Path, string Name, bool Renderer)> shipOff = new List<(int[], string, bool)>();

        private static void RecordShipState() {
            shipOff.Clear();
            var ship = ShipStatus.Instance;
            if (ship == null) return;
            try {
                var stack = new Stack<(Transform T, int[] Path)>();
                stack.Push((ship.transform, new int[0]));
                while (stack.Count > 0) {
                    var (t, path) = stack.Pop();
                    for (int c = 0; c < t.childCount; c++) {
                        var ch = t.GetChild(c);
                        var p = new int[path.Length + 1];
                        Array.Copy(path, p, path.Length); p[path.Length] = c;
                        if (!ch.gameObject.activeSelf) { shipOff.Add((p, ch.name, false)); continue; }
                        var sr = ch.GetComponent<SpriteRenderer>();
                        if (sr != null && !sr.enabled) shipOff.Add((p, ch.name, true));
                        stack.Push((ch, p));
                    }
                }
            } catch (Exception e) { UsefulTORStuffPlugin.Logger?.LogWarning($"[RoundReplay] ship state failed: {e.Message}"); }
        }

        private static int ApplyShipState(Transform root) {
            int n = 0;
            foreach (var (path, name, renderer) in shipOff) {
                Transform t = root;
                foreach (int c in path) { if (t == null || c >= t.childCount) { t = null; break; } t = t.GetChild(c); }
                if (t == null || t.name != name) continue;      // hierarchy differs here: leave it
                if (renderer) { var sr = t.GetComponent<SpriteRenderer>(); if (sr != null) sr.enabled = false; }
                else t.gameObject.SetActive(false);
                n++;
            }
            return n;
        }

        // the live copy for maps built at runtime
        private static GameObject liveHolder, liveClone;
        private static bool liveStripped;
        private static AsyncOperationHandle<GameObject> keepHandle;
        private static bool keepHandleSet;

        private static bool VanillaLoadable() {
            try {
                if (recMapId < 0) return false;
                if (!string.IsNullOrEmpty(AppDomain.CurrentDomain.GetData("UnknownsAtlas.ActiveMap") as string)) return false;
                var prefabs = AmongUsClient.Instance?.ShipPrefabs;
                return prefabs != null && recMapId < prefabs.Count && prefabs[recMapId] != null;
            } catch { return false; }
        }

        private static bool WorldPossible() {
            if (!hostRec && !HostData) return false;
            if (liveHolder != null) return true;
            return !recLive && VanillaLoadable();
        }

        private static bool recLive;      // decided at the round end: this round's map needs the live copy

        /// <summary>Round end (host): a map the Addressables cannot rebuild is copied as it stands.</summary>
        private static void CaptureLiveShip() {
            ReleaseLive();
            recLive = !VanillaLoadable();
            if (!recLive) return;
            var ship = ShipStatus.Instance;
            if (ship == null) return;
            try {
                float t0 = Time.realtimeSinceStartup;
                liveHolder = new GameObject("UTSReplayLiveShip");
                liveHolder.SetActive(false);
                UnityEngine.Object.DontDestroyOnLoad(liveHolder);
                liveClone = UnityEngine.Object.Instantiate(ship.gameObject, liveHolder.transform);
                liveStripped = false;
                // the Skeld bundle under an Atlas map stays loaded while the copy may need its materials
                var prefabs = AmongUsClient.Instance?.ShipPrefabs;
                if (prefabs != null && recMapId >= 0 && recMapId < prefabs.Count && prefabs[recMapId] != null) {
                    keepHandle = Addressables.LoadAssetAsync<GameObject>(prefabs[recMapId].RuntimeKey);
                    keepHandleSet = true;
                }
                UsefulTORStuffPlugin.Logger?.LogInfo($"[RoundReplay] live ship copied for the perspective view ({mapName}), {(Time.realtimeSinceStartup - t0) * 1000f:F0} ms, bundle kept: {keepHandleSet}.");
            } catch (Exception e) {
                UsefulTORStuffPlugin.Logger?.LogWarning($"[RoundReplay] live ship copy failed: {e.Message}");
                ReleaseLive();
            }
        }

        private static void ReleaseLive() {
            if (liveHolder != null) UnityEngine.Object.Destroy(liveHolder);
            liveHolder = null; liveClone = null; liveStripped = false;
            if (keepHandleSet) { try { Addressables.Release(keepHandle); } catch { } keepHandleSet = false; }
        }

        // ---- load ----
        private static void WorldLoad() {
            if (worldState != 0) return;
            if (!WorldPossible()) { worldState = 3; return; }
            if (liveHolder != null && liveClone != null) {
                try {
                    worldHolder = liveHolder;
                    worldHolder.transform.position = WorldOffset;
                    if (!liveStripped) { WorldPrepare(liveClone, 0); liveStripped = true; }
                    else {
                        worldHolder.SetActive(true);
                        worldDoorSample = -1;
                        worldFigures = new GameObject("UTSReplayFigures");
                        worldState = 2;
                    }
                } catch (Exception e) {
                    worldState = 3;
                    UsefulTORStuffPlugin.Logger?.LogWarning($"[RoundReplay] live map failed: {e}");
                }
                return;
            }
            try {
                var reference = AmongUsClient.Instance.ShipPrefabs[recMapId];
                worldHandle = Addressables.LoadAssetAsync<GameObject>(reference.RuntimeKey);
                worldHandleSet = true;
                worldState = 1;
                UsefulTORStuffPlugin.Logger?.LogInfo($"[RoundReplay] loading map {recMapId} for the perspective view.");
            } catch (Exception e) {
                worldState = 3;
                UsefulTORStuffPlugin.Logger?.LogWarning($"[RoundReplay] map load failed: {e.Message}");
            }
        }

        private static void WorldTick() {
            if (worldState != 1) return;
            try {
                if (!worldHandle.IsDone) return;
                if (worldHandle.Status != AsyncOperationStatus.Succeeded || worldHandle.Result == null) {
                    worldState = 3;
                    UsefulTORStuffPlugin.Logger?.LogWarning($"[RoundReplay] map load ended with {worldHandle.Status}.");
                    return;
                }
                WorldBuild(worldHandle.Result);
            } catch (Exception e) {
                worldState = 3;
                UsefulTORStuffPlugin.Logger?.LogWarning($"[RoundReplay] map build failed: {e}");
                WorldDestroy(false);
            }
        }

        private static void WorldBuild(GameObject prefab) {
            float t0 = Time.realtimeSinceStartup;
            worldHolder = new GameObject("UTSReplayWorld");
            worldHolder.SetActive(false);
            worldHolder.transform.position = WorldOffset;
            var inst = UnityEngine.Object.Instantiate(prefab, worldHolder.transform);
            int off = ApplyShipState(inst.transform);
            WorldPrepare(inst, off, t0);
        }

        // inst sits under worldHolder (inactive): doors, scripts out, switch on
        private static void WorldPrepare(GameObject inst, int off, float t0 = -1f) {
            if (t0 < 0f) t0 = Time.realtimeSinceStartup;
            worldShift = (Vector2)inst.transform.position - recShipPos;

            // the doors, in ShipStatus.AllDoors order (the order the recording used)
            worldDoors.Clear();
            var ship = inst.GetComponent<ShipStatus>();
            if (ship != null && ship.AllDoors != null)
                foreach (var d in ship.AllDoors) {
                    if (d == null) { worldDoors.Add((new Collider2D[0], new SpriteRenderer[0])); continue; }
                    worldDoors.Add((d.GetComponentsInChildren<Collider2D>(true).ToArray(), d.GetComponentsInChildren<SpriteRenderer>(true).ToArray()));
                }

            // strip every script: nothing of this copy may reach into the game
            int removed = 0;
            for (int pass = 0; pass < 3; pass++) {
                var all = inst.GetComponentsInChildren<MonoBehaviour>(true);
                if (all.Length == 0) break;
                foreach (var mb in all) {
                    if (mb == null) continue;
                    try { UnityEngine.Object.DestroyImmediate(mb); removed++; } catch { }
                }
            }
            foreach (var a in inst.GetComponentsInChildren<AudioSource>(true)) if (a != null) a.enabled = false;
            worldHolder.SetActive(true);
            worldDoorSample = -1;

            worldFigures = new GameObject("UTSReplayFigures");
            worldFigures.transform.position = Vector3.zero;

            worldState = 2;
            UsefulTORStuffPlugin.Logger?.LogInfo($"[RoundReplay] map {recMapId} ready: {removed} script(s) removed, {off}/{shipOff.Count} switched-off part(s) matched, {worldDoors.Count} door(s), shift {worldShift}, {(Time.realtimeSinceStartup - t0) * 1000f:F0} ms.");
        }

        private static void WorldDestroy(bool release = true) {
            foreach (var f in figures.Values) if (f.P != null) UnityEngine.Object.Destroy(f.P.gameObject);
            figures.Clear();
            foreach (var b in bodies.Values) if (b != null) UnityEngine.Object.Destroy(b);
            bodies.Clear();
            if (worldFigures != null) UnityEngine.Object.Destroy(worldFigures);
            if (worldHolder != null) {
                if (worldHolder == liveHolder) worldHolder.SetActive(false);    // the live copy cannot be made again
                else UnityEngine.Object.Destroy(worldHolder);
            }
            if (worldCam != null) UnityEngine.Object.Destroy(worldCam.gameObject);
            if (worldRt != null) { worldRt.Release(); UnityEngine.Object.Destroy(worldRt); }
            worldFigures = null; worldHolder = null; worldCam = null; worldRt = null; worldArea = null; viewLabel = null;
            nameLabels.Clear(); labelsShown.Clear(); nameLayer = null;
            walk.Clear();
            worldDoors.Clear();
            if (release && worldHandleSet) {
                try { Addressables.Release(worldHandle); } catch { }
                worldHandleSet = false;
            }
            worldState = 0;
        }

        // ---- panel ----
        private static void WorldBuildUi(GameObject panel, float mapH) {
            worldArea = SessionStatsUI.Box(panel, new Vector2((PanelW - MapW) / 2f, -90), new Vector2(MapW, mapH), Color.black);
            worldArea.GetComponent<Image>().raycastTarget = false;
            var pic = new GameObject("World");
            pic.transform.SetParent(worldArea.transform, false);
            var prt = pic.AddComponent<RectTransform>();
            prt.anchorMin = Vector2.zero; prt.anchorMax = Vector2.one; prt.sizeDelta = Vector2.zero;
            var raw = pic.AddComponent<RawImage>();
            raw.raycastTarget = false;
            worldRt = new RenderTexture((int)MapW, (int)mapH, 16) { name = "UTSReplayWorld" };
            raw.texture = worldRt;

            worldMaskH = Mathf.Max(16, Mathf.RoundToInt(MaskW * mapH / MapW));
            worldMaskTex = new Texture2D(MaskW, worldMaskH, TextureFormat.RGBA32, false) { wrapMode = TextureWrapMode.Clamp, filterMode = FilterMode.Bilinear };
            worldMaskTex.hideFlags |= HideFlags.DontUnloadUnusedAsset;
            worldMaskPx = new Color32[MaskW * worldMaskH];
            var dark = new GameObject("Darkness");
            dark.transform.SetParent(worldArea.transform, false);
            var drt = dark.AddComponent<RectTransform>();
            drt.anchorMin = Vector2.zero; drt.anchorMax = Vector2.one; drt.sizeDelta = Vector2.zero;
            var draw = dark.AddComponent<RawImage>();
            draw.texture = worldMaskTex;
            draw.raycastTarget = false;
            worldArea.SetActive(false);

            var cgo = new GameObject("UTSReplayWorldCam");
            UnityEngine.Object.DontDestroyOnLoad(cgo);
            worldCam = cgo.AddComponent<Camera>();
            worldCam.orthographic = true;
            worldCam.clearFlags = CameraClearFlags.SolidColor;
            worldCam.backgroundColor = Color.black;
            worldCam.cullingMask = ~(1 << 5);
            worldCam.nearClipPlane = 0.1f; worldCam.farClipPlane = 200f;
            worldCam.targetTexture = worldRt;
            worldCam.depth = -50;
            worldCam.enabled = false;
        }

        private static void ToggleWorldView() => worldView = !worldView;

        /// <summary>The real map view for the chosen player at sample i (u = position between samples).
        /// Returns false when it cannot be shown (the minimap stays).</summary>
        private static bool WorldAnimate(Track pt, int i, float u) {
            bool show = pt != null && worldView && worldState == 2 && worldArea != null && worldCam != null;
            if (worldArea != null && worldArea.activeSelf != show) worldArea.SetActive(show);
            if (worldCam != null && worldCam.enabled != show) worldCam.enabled = show;
            if (worldFigures != null && worldFigures.activeSelf != show) worldFigures.SetActive(show);
            if (!show) return false;

            int pi = Mathf.Clamp(i, 0, pt.P.Count - 1);
            // the eye: the player's interpolated spot, or where he was last seen alive
            Vector2 eye = new Vector2(float.NaN, float.NaN);
            for (int k = pi; k >= 0; k--) if (!float.IsNaN(pt.P[k].x)) { eye = pt.P[k]; break; }
            if (float.IsNaN(eye.x)) eye = recShipPos;
            if (pi + 1 < pt.P.Count && !float.IsNaN(pt.P[pi].x) && !float.IsNaN(pt.P[pi + 1].x)) eye = Vector2.Lerp(pt.P[pi], pt.P[pi + 1], u);
            float r = pi < pt.R.Count ? pt.R[pi] : 0f;
            bool dead = float.IsNaN(pt.P[pi].x);
            bool vent = pi < pt.Vent.Count && pt.Vent[pi];

            ApplyDoors(pi);
            Vector2 eyeW = eye + worldShift;
            float ortho = diagOrtho > 0f ? diagOrtho : Mathf.Clamp(Mathf.Max(r, 2.6f) * 1.12f * worldZoom, 1.2f, 12f);
            worldCam.orthographicSize = ortho;
            worldCam.transform.position = new Vector3(eyeW.x, eyeW.y, WorldOffset.z - 50f);

            ulong sees = pi < pt.Sees.Count ? pt.Sees[pi] : 0UL;
            for (int k = 0; k < tracks.Count; k++) {
                var t = tracks[k];
                bool alive = pi < t.P.Count && !float.IsNaN(t.P[pi].x);
                bool inVent = pi < t.Vent.Count && t.Vent[pi];
                bool visible = alive && !inVent && (t == pt || dead || (k < 64 && (sees & (1UL << k)) != 0));
                Vector2 at = alive ? t.P[pi] : Vector2.zero;
                if (alive && pi + 1 < t.P.Count && !float.IsNaN(t.P[pi + 1].x)) at = Vector2.Lerp(t.P[pi], t.P[pi + 1], u);
                Figure(t, pi, at + worldShift, visible, t == pt);
            }
            Bodies(eyeW, dead ? 99f : r);

            PaintWorldMask(eyeW + LightOffset, eyeW, r, ortho, dead, vent);
            NameLabelsEndFrame();
            return true;
        }

        private static void ApplyDoors(int si) {
            if (si == worldDoorSample || worldDoors.Count == 0) return;
            worldDoorSample = si;
            var open = new bool[worldDoors.Count];
            for (int k = 0; k < open.Length; k++) open[k] = true;
            foreach (var (s, d, o) in doorLog) { if (s > si) break; if (d < open.Length) open[d] = o; }
            for (int k = 0; k < worldDoors.Count; k++) {
                var (cols, rends) = worldDoors[k];
                foreach (var c in cols) if (c != null && !c.isTrigger) c.enabled = !open[k];
                foreach (var rr in rends) if (rr != null) rr.color = open[k] ? Color.white : new Color(1f, 0.55f, 0.55f);
            }
        }

        // ---- figures ----
        private static PoolablePlayer beanPrefab;

        private static PoolablePlayer BeanPrefab() {
            if (beanPrefab != null) return beanPrefab;
            try { beanPrefab = HudManager.Instance?.IntroPrefab?.PlayerPrefab; } catch { }
            if (beanPrefab == null)
                foreach (var o in Resources.FindObjectsOfTypeAll(Il2CppInterop.Runtime.Il2CppType.Of<PoolablePlayer>())) {
                    var p = o?.TryCast<PoolablePlayer>();
                    if (p != null && !p.gameObject.scene.IsValid()) { beanPrefab = p; break; }   // a prefab, not a scene object
                }
            return beanPrefab;
        }

        // the outfit the others saw: the owner of that colour (own look, a Morphling's target), or the
        // bare look (Camouflage and the like)
        private static (int Color, string Hat, string Skin, string Visor, string Name) SeenOutfit(Track t, int si) {
            var look = LookAt(t, si);
            if (!look.HasValue || look.Value.Color < 0) return (t.OColor, t.OHat, t.OSkin, t.OVisor, t.Name);
            var l = look.Value;
            if (!string.IsNullOrEmpty(l.Name) || l.Color == t.OColor) {
                var owner = l.Color == t.OColor ? t : tracks.FirstOrDefault(o => o.OColor == l.Color);
                if (owner != null) return (owner.OColor, owner.OHat, owner.OSkin, owner.OVisor, string.IsNullOrEmpty(l.Name) ? owner.Name : l.Name);
            }
            return (l.Color, l.Hat, "", l.Visor, l.Name);
        }

        private static void Figure(Track t, int si, Vector2 at, bool visible, bool self) {
            figures.TryGetValue(t.Id, out var f);
            if (!visible) { if (f.P != null && f.P.gameObject.activeSelf) f.P.gameObject.SetActive(false); return; }
            if (f.P == null) {
                var prefab = BeanPrefab();
                if (prefab == null || worldFigures == null) return;
                var pp = UnityEngine.Object.Instantiate(prefab, worldFigures.transform);
                pp.gameObject.name = "Fig_" + t.Name;
                SetLayer(pp.gameObject, 8);
                f = (pp, "", at);
            }
            var pl = f.P;
            if (!pl.gameObject.activeSelf) pl.gameObject.SetActive(true);
            var o = SeenOutfit(t, si);
            string key = $"{o.Color}|{o.Hat}|{o.Skin}|{o.Visor}|{o.Name}";
            if (key != f.Key) {
                try {
                    var outfit = new NetworkedPlayerInfo.PlayerOutfit();
                    outfit.ColorId = o.Color < 0 ? 0 : o.Color;
                    outfit.HatId = o.Hat ?? ""; outfit.SkinId = o.Skin ?? ""; outfit.VisorId = o.Visor ?? "";
                    outfit.PetId = "pet_EmptyPet"; outfit.PlayerName = o.Name ?? "";
                    pl.UpdateFromPlayerOutfit(outfit, PlayerMaterial.MaskType.None, false, true);
                    if (pl.cosmetics != null && pl.cosmetics.nameText != null) {
                        pl.cosmetics.nameText.text = o.Name ?? "";
                        pl.cosmetics.nameText.color = Color.white;
                    }
                    SetLayer(pl.gameObject, 8);
                    FitFigure(pl);
                    walk.Remove(t.Id);        // a new outfit resets the body: start the clip again
                } catch (Exception e) { UsefulTORStuffPlugin.Logger?.LogWarning($"[RoundReplay] figure outfit failed: {e.Message}"); }
                f.Key = key;
            }
            float dx = at.x - f.Last.x;
            if (Mathf.Abs(dx) > 0.01f) pl.SetFlipX(dx < 0f);
            Walk(t.Id, pl, (at - f.Last).sqrMagnitude > 1e-6f);
            pl.transform.position = new Vector3(at.x, at.y, WorldOffset.z + at.y / 1000f - 1f);
            var look = LookAt(t, si);
            float alpha = look.HasValue && look.Value.Hidden ? (self ? 0.4f : 0f) : 1f;
            try {
                var body = pl.cosmetics?.currentBodySprite?.BodySprite;
                if (body != null) { var c = body.color; c.a = alpha; body.color = c; }
            } catch { }
            f.Last = at;
            figures[t.Id] = f;
            NameLabel(t.Id, o.Name, o.Color, at, t.NameDy);
        }

        // ---- walking: the game's own run/idle clips on body and skin (as UC's Illusionist clone does) ----
        private static readonly Dictionary<byte, (PowerTools.SpriteAnim Body, bool Running)> walk = new Dictionary<byte, (PowerTools.SpriteAnim, bool)>();
        private static AnimationClip idleClip, runClip;

        private static void Walk(byte id, PoolablePlayer pl, bool moving) {
            try {
                if (idleClip == null || runClip == null) {
                    var g = PlayerControl.LocalPlayer?.MyPhysics?.Animations?.group;
                    if (g != null) { idleClip = g.IdleAnim; runClip = g.RunAnim; }
                }
                if (idleClip == null || runClip == null) return;
                if (!walk.TryGetValue(id, out var w) || w.Body == null) {
                    var body = pl.cosmetics?.currentBodySprite?.BodySprite;
                    if (body == null) return;
                    var anim = body.GetComponent<PowerTools.SpriteAnim>();
                    if (anim == null) {
                        if (body.GetComponent<Animator>() == null) body.gameObject.AddComponent<Animator>();
                        anim = body.gameObject.AddComponent<PowerTools.SpriteAnim>();
                    }
                    w = (anim, !moving);      // forces the first Play below
                }
                if (w.Running != moving) {
                    w.Body.Play(moving ? runClip : idleClip, 1f);
                    var skin = pl.cosmetics?.skin;
                    var view = skin != null ? skin.skin : null;
                    if (skin != null && skin.animator != null && view != null) {
                        var clip = moving ? view.RunAnim : view.IdleAnim;
                        if (clip != null) skin.animator.Play(clip, 1f);
                    }
                    w.Running = moving;
                    if (moving && !walkLogged) {
                        walkLogged = true;
                        UsefulTORStuffPlugin.Logger?.LogInfo($"[RoundReplay] figure walks: clip '{w.Body.m_currAnim?.name ?? runClip.name}', playing {w.Body.Playing}.");
                    }
                }
                walk[id] = w;
            } catch (Exception e) {
                if (!walkLogged) { walkLogged = true; UsefulTORStuffPlugin.Logger?.LogWarning($"[RoundReplay] figure animation failed: {e.Message}"); }
            }
        }

        private static bool walkLogged;

        // ---- names above the darkness ----
        private static readonly Dictionary<byte, TMPro.TextMeshProUGUI> nameLabels = new Dictionary<byte, TMPro.TextMeshProUGUI>();
        private static readonly HashSet<byte> labelsShown = new HashSet<byte>();
        internal static float NameTagBase = 0.56f;    // a plain player's tag height over his position (freeplay measurement)
        private static GameObject nameLayer;

        private static void NameLabel(byte id, string name, int color, Vector2 worldAt, float nameDy) {
            if (worldArea == null || worldCam == null) return;
            if (nameLayer == null) {
                nameLayer = new GameObject("Names");
                nameLayer.transform.SetParent(worldArea.transform, false);
                var r = nameLayer.AddComponent<RectTransform>();
                r.anchorMin = Vector2.zero; r.anchorMax = Vector2.one; r.sizeDelta = Vector2.zero;
            }
            if (!nameLabels.TryGetValue(id, out var lbl) || lbl == null) {
                lbl = SessionStatsUI.Label(nameLayer, "", 20, TMPro.FontStyles.Bold, Color.white, Vector2.zero, Vector2.zero,
                                           new Vector2(0.5f, 0f), Vector2.zero, new Vector2(260, 60), TMPro.TextAlignmentOptions.Bottom);
                lbl.enableWordWrapping = false;
                lbl.outlineWidth = 0.3f; lbl.outlineColor = new Color32(0, 0, 0, 255);
                nameLabels[id] = lbl;
            }
            bool cb = false;
            try { cb = AmongUs.Data.DataManager.Settings.Accessibility.ColorBlindMode; } catch { }
            string text = name ?? "";
            if (cb && color >= 0) {
                string cn = "";
                try { cn = Palette.GetColorName(color); } catch { }
                if (cn.Length > 1) cn = cn.Substring(0, 1).ToUpperInvariant() + cn.Substring(1).ToLowerInvariant();   // "Green", as the game writes it
                if (!string.IsNullOrEmpty(cn)) text += $"\n<size=72%>{cn}</size>";
            }
            if (lbl.text != text) lbl.text = text;
            // 0.36 above the centre (the text's foot), at the game's size: 15.5 px at the game camera's 3 (screenshot comparison)
            // the measured tag height when there is one (a tall hat lifts the name): NameTagBase is a
            // plain player's tag height, which sits at 0.36
            float dy = float.IsNaN(nameDy) ? 0.36f : 0.36f + (nameDy - NameTagBase);
            var vp = worldCam.WorldToViewportPoint(new Vector3(worldAt.x, worldAt.y + dy, 0f));
            var area = worldArea.GetComponent<RectTransform>().rect;
            var rt = lbl.rectTransform;
            rt.anchoredPosition = new Vector2(vp.x * area.width, vp.y * area.height);
            lbl.fontSize = 15.5f * 3f / Mathf.Max(0.5f, worldCam.orthographicSize) * (area.height / 596f);
            if (!lbl.gameObject.activeSelf) lbl.gameObject.SetActive(true);
            labelsShown.Add(id);
        }

        private static void NameLabelsEndFrame() {
            foreach (var kv in nameLabels)
                if (kv.Value != null && !labelsShown.Contains(kv.Key) && kv.Value.gameObject.activeSelf) kv.Value.gameObject.SetActive(false);
            labelsShown.Clear();
        }

        // The end screen's bean is drawn bigger than a player in the round (1.85x at scale 0.7, freeplay
        // comparison 2026-10-02), so its scale comes from the body height of a real player measured
        // during the recording.
        private static float figureScale = -1f;

        private static void FitFigure(PoolablePlayer pl) {
            if (figureScale < 0f) {
                figureScale = 0.38f;
                try {
                    pl.transform.localScale = Vector3.one;
                    var body = pl.cosmetics?.currentBodySprite?.BodySprite;
                    float h = body != null ? body.bounds.size.y : 0f;
                    if (h > 0.05f) figureScale = liveBodyH / h;
                    UsefulTORStuffPlugin.Logger?.LogInfo($"[RoundReplay] figure scale {figureScale:F3} (bean body {h:F2}, player body {liveBodyH:F2}).");
                } catch { }
            }
            pl.transform.localScale = new Vector3(figureScale, figureScale, 1f);
            // the game draws names above the darkness: ours are UI labels over the mask (NameLabel)
            try {
                if (pl.cosmetics?.nameText != null) pl.cosmetics.nameText.gameObject.SetActive(false);
                if (pl.cosmetics?.colorBlindText != null) pl.cosmetics.colorBlindText.gameObject.SetActive(false);
            } catch { }
        }

        private static void SetLayer(GameObject go, int layer) {
            foreach (var tr in go.GetComponentsInChildren<Transform>(true)) tr.gameObject.layer = layer;
        }

        // ---- bodies: from the death until the next meeting, a Cleaner or a Vulture ----
        private static void Bodies(Vector2 eyeW, float radius) {
            var alive = new HashSet<byte>();
            foreach (var e in events) {
                if (e.Kind != EvDeath || e.T > viewT || e.Target == 255 || float.IsNaN(e.Pos.x)) continue;
                if (events.Any(m => m.Kind == EvMeeting && m.T > e.T && m.T <= viewT)) continue;
                if (bodiesGone.Any(g => g.Id == e.Target && g.T >= e.T && g.T <= viewT)) continue;
                Vector2 at = e.Pos + worldShift;
                if (!CanSee(eyeW, radius, at)) continue;
                alive.Add(e.Target);
                if (!bodies.TryGetValue(e.Target, out var go) || go == null) {
                    go = MakeBody(e.Target);
                    if (go == null) continue;
                    bodies[e.Target] = go;
                }
                go.transform.position = new Vector3(at.x, at.y, WorldOffset.z + at.y / 1000f);
                if (!go.activeSelf) go.SetActive(true);
            }
            foreach (var kv in bodies) if (kv.Value != null && !alive.Contains(kv.Key) && kv.Value.activeSelf) kv.Value.SetActive(false);
        }

        private static GameObject MakeBody(byte id) {
            try {
                var prefab = GameManager.Instance != null ? GameManager.Instance.DeadBodyPrefab : null;
                if (prefab == null || worldFigures == null) return null;
                var holder = new GameObject("BodyHolder");
                holder.SetActive(false);
                var db = UnityEngine.Object.Instantiate(prefab, holder.transform);
                var t = tracks.FirstOrDefault(x => x.Id == id);
                int color = t != null && t.OColor >= 0 ? t.OColor : 0;
                if (db.bodyRenderers != null) foreach (var r in db.bodyRenderers) if (r != null) PlayerMaterial.SetColors(color, r);
                var go = db.gameObject;
                foreach (var mb in go.GetComponentsInChildren<MonoBehaviour>(true)) if (mb != null) UnityEngine.Object.DestroyImmediate(mb);
                go.transform.SetParent(worldFigures.transform, true);
                UnityEngine.Object.Destroy(holder);
                SetLayer(go, 8);
                return go;
            } catch (Exception e) {
                UsefulTORStuffPlugin.Logger?.LogWarning($"[RoundReplay] body failed: {e.Message}");
                return null;
            }
        }

        // ---- the light ----
        private static void PaintWorldMask(Vector2 source, Vector2 centre, float radius, float ortho, bool dead, bool vent) {
            if (worldMaskTex == null) return;
            if (dead) {                                     // a ghost sees everything
                for (int k = 0; k < worldMaskPx.Length; k++) worldMaskPx[k] = new Color32(0, 0, 0, 0);
            } else {
                int mask = Constants.ShadowMask;
                for (int k = 0; k < WorldRays; k++) {
                    float a = k * Mathf.PI * 2f / WorldRays;
                    var dir = new Vector2(Mathf.Cos(a), Mathf.Sin(a));
                    var hit = Physics2D.Raycast(source, dir, radius, mask);
                    worldRayLen[k] = vent ? 0f : hit.collider != null ? Mathf.Min(radius, hit.distance + WorldWallBias) : radius;
                }
                float unit = ortho / (worldMaskH / 2f);
                Vector2 off = source - centre;     // the light sits a little below the picture's centre
                float rr = Mathf.Max(0.05f, radius);
                for (int y = 0; y < worldMaskH; y++) {
                    for (int x = 0; x < MaskW; x++) {
                        float dx = (x + 0.5f - MaskW / 2f) * unit - off.x, dy = (y + 0.5f - worldMaskH / 2f) * unit - off.y;
                        float d = Mathf.Sqrt(dx * dx + dy * dy);
                        float f = (Mathf.Atan2(dy, dx) / (Mathf.PI * 2f) + 1f) % 1f * WorldRays;
                        int k0 = (int)f % WorldRays, k1 = (k0 + 1) % WorldRays;
                        float limit = Mathf.Lerp(worldRayLen[k0], worldRayLen[k1], f - Mathf.Floor(f));
                        float lit = Mathf.Clamp01((limit - d) / WorldEdge);
                        float a = WorldDark * Mathf.Lerp(1f, Mathf.Clamp01(d / rr), lit);   // linear falloff inside
                        worldMaskPx[y * MaskW + x] = new Color32(0, 0, 0, (byte)(a * 255f));
                    }
                }
            }
            worldMaskTex.SetPixels32(worldMaskPx);
            worldMaskTex.Apply(false, false);
        }
    }
}
