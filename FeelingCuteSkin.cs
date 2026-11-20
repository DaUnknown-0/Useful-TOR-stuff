// Useful TOR Stuff - Copyright (C) 2026 DaUnknown-0
// Licensed under GPL-3.0-or-later. See LICENSE for details.

/*
 * FeelingCuteSkin - "Feeling Cute Overalls" als echter Among-Us-Skin (Skin-Tab, Skin-Slot, Netz-ID).
 *
 * Katalog: ein eigener SkinData (skin_Feeling_Cute_Overalls) wird an HatManager.allSkins angehängt, sobald der
 * HatManager gefragt wird. Er ist für alle Spieler mit Forgotten Fixes bekannt, damit sie ihn am Träger sehen. Im
 * Skin-Tab erscheint er nur für DrSuspect und den Mod-Autor (Sperrlogik in UnknownHat.cs) und erst, wenn das
 * Bildpaket geladen ist. Seine ViewDataRef und PreviewData sind die des leeren Skins: jeder Vanilla-Ladepfad, der
 * ihn doch erreicht, lädt nur "kein Skin".
 *
 * Laden im Spiel (GameAssembly 4.7.0 nachgelesen): CosmeticsLayer.SetSkin(string) -> HatManager.GetSkinById ->
 * CosmeticsLayer.SetSkin(SkinData) -> SkinLayer.SetSkin(SkinData, ...) -> Addressable -> SkinLayer.SetSkin(SkinViewData).
 * Der Prefix auf SkinLayer.SetSkin(SkinData) merkt sich die Ebene als Träger und gibt Vanilla den leeren Skin.
 * CosmeticsCache.CoAddSkin und TORs Helpers.setLook bekommen für unsere ID die leere ID (GetSkin selbst nie patchen, siehe SetLookPatch).
 *
 * Anzeige: Vanilla-Skins sind Unity-Animationsclips, die sich zur Laufzeit nicht bauen lassen. Stattdessen hängt
 * neben der Hut-Vorderebene jedes Trägers eine eigene Ebene auf dem Kopfknoten (NodeId 0), also genau dort, wo
 * die Feeling-Cute-Hüte ihre Bilder zeigen. Damit gilt dieselbe Leinwand (450 x 562, Pivot
 * 0.53/0.575) wie für die Hutbilder. Pro Bild: Ruhe, eines von 12 Laufbildern synchron zum echten Laufbild, Leiter.
 * Andere Animationen (Spawn, Lüftung, Kill, Geist) blenden die Ebene aus. Die z-Tiefe folgt der echten Skin-Ebene,
 * Farbe/Alpha und Maskierung folgen dem Grundkörper (Unsichtbarkeit, Kleiderschrank-Masken). Optionale
 * Farbmasken (mask_*.png) werden in der Spielerfarbe eingefärbt darübergelegt.
 */

using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using HarmonyLib;
using Il2CppInterop.Runtime.InteropTypes.Arrays;
using PowerTools;
using UnityEngine;

namespace UsefulTORStuff;

internal static class FeelingCuteSkin {
    internal const string SkinName = "Feeling Cute Overalls";
    internal const string ProductId = "skin_Feeling_Cute_Overalls";
    private const int WalkCount = 12;

    // 19.11.: Latzhose aus den Ebenen von feeling-cute-v3 (GPT-Bilder) herausgetrennt, Audits/2026-11-19/skin_overalls.
    private static readonly CostumePackage package = new(SkinName, "feeling-cute-v3-overalls.zip", 989679,
        "babf3dfc78222380bf678b2c07c101938e7efc5d62006e7542cbd962fd73ddf8", Register);

    private static Sprite idle, climb, icon, maskIdle, maskClimb;
    private static Sprite[] walk, maskWalk;
    private static SkinData data;
    private static IntPtr dataPtr, emptyPtr;
    private static SkinData empty;
    private static bool failed;

    /// <summary>Bilder geladen: erst dann erscheint der Skin im Skin-Tab.</summary>
    internal static bool Loaded { get; private set; }

    internal static bool IsOurs(string productId) => productId == ProductId;

    internal static void Init() => package.Load();
    internal static void Poll() => package.Poll();

    private static void Register(byte[] bytes) {
        using var archive = new ZipArchive(new MemoryStream(bytes, false), ZipArchiveMode.Read);
        Sprite Load(string name) => FeelingCuteHat.LoadSprite(CostumePackage.ReadEntry(archive, name));
        Sprite Optional(string name) => archive.GetEntry(name) == null ? null : Load(name);

        idle = Load("idle.png");
        climb = Load("climb.png");
        walk = Enumerable.Range(1, WalkCount).Select(i => Load($"walk_{i:00}.png")).ToArray();
        var iconBytes = archive.GetEntry("icon.png") != null ? CostumePackage.ReadEntry(archive, "icon.png") : null;
        icon = iconBytes != null ? LoadIcon(iconBytes) : idle;

        // Farbmasken: ganz oder gar nicht, sonst stimmt die Einfärbung in einzelnen Posen nicht.
        maskIdle = Optional("mask_idle.png");
        maskClimb = Optional("mask_climb.png");
        maskWalk = Enumerable.Range(1, WalkCount).Select(i => Optional($"mask_walk_{i:00}.png")).ToArray();
        if (maskIdle == null || maskClimb == null || maskWalk.Any(m => m == null)) {
            maskIdle = maskClimb = null;
            maskWalk = null;
        }
        Loaded = true;
        UsefulTORStuffPlugin.Logger?.LogInfo($"[{SkinName}] Skin geladen{(maskWalk != null ? " (mit Farbmasken)" : "")}.");
    }

    private static Sprite LoadIcon(byte[] bytes) {
        var texture = new Texture2D(2, 2, TextureFormat.ARGB32, false);
        if (!ImageConversion.LoadImage(texture, bytes, true)) throw new InvalidDataException("icon.png");
        texture.hideFlags |= HideFlags.HideAndDontSave | HideFlags.DontUnloadUnusedAsset;
        var sprite = Sprite.Create(texture, new Rect(0, 0, texture.width, texture.height), new Vector2(0.5f, 0.5f),
            texture.width / 0.9f);
        sprite.hideFlags |= HideFlags.HideAndDontSave | HideFlags.DontUnloadUnusedAsset;
        return sprite;
    }

    private static bool Alive(UnityEngine.Object o) => !ReferenceEquals(o, null) && o.m_CachedPtr != IntPtr.Zero;

    // ---- Katalog ---------------------------------------------------------------------------------

    private static void EnsureRegistered(HatManager manager) {
        if (failed || !Alive(manager)) return;
        if (Alive(data)) return;
        try {
            var all = manager.allSkins;
            if (all == null) return;
            SkinData none = null;
            foreach (var skin in all) {
                if (!Alive(skin)) continue;
                string id = skin.ProductId;
                if (id == ProductId) { data = skin; dataPtr = skin.Pointer; }
                else if (id == SkinData.EmptyId) none = skin;
            }
            if (none == null) return;
            empty = none;
            emptyPtr = none.Pointer;
            if (Alive(data)) return;

            var created = ScriptableObject.CreateInstance<SkinData>();
            created.name = SkinName;
            created.ProductId = ProductId;
            created.StoreName = SkinName;
            created.ViewDataRef = none.ViewDataRef;
            created.PreviewData = none.PreviewData;
            created.ChipOffset = none.ChipOffset;
            created.displayOrder = 99;
            created.Free = true;
            created.NotInStore = true;
            created.PreviewCrewmateColor = false;
            created.hideFlags |= HideFlags.HideAndDontSave | HideFlags.DontUnloadUnusedAsset;

            var list = new List<SkinData>(all.Length + 1);
            foreach (var skin in all) list.Add(skin);
            list.Add(created);
            manager.allSkins = new Il2CppReferenceArray<SkinData>(list.ToArray());
            data = created;
            dataPtr = created.Pointer;
            UsefulTORStuffPlugin.Logger?.LogInfo($"[{SkinName}] Skin im Katalog registriert ({ProductId}).");
        } catch (Exception ex) {
            failed = true;
            UsefulTORStuffPlugin.Logger?.LogWarning($"[{SkinName}] Registrierung abgebrochen: {ex.Message}");
        }
    }

    private static SkinData Empty(HatManager manager) {
        if (Alive(empty)) return empty;
        EnsureRegistered(manager);
        return Alive(empty) ? empty : null;
    }

    private static bool Ours(SkinData skin) => dataPtr != IntPtr.Zero && !ReferenceEquals(skin, null) && skin.Pointer == dataPtr;

    [HarmonyPatch(typeof(HatManager), nameof(HatManager.GetSkinById))]
    private static class LookupPatch {
        [HarmonyPriority(Priority.First)]
        private static void Prefix(HatManager __instance) => EnsureRegistered(__instance);
    }

    [HarmonyPatch(typeof(HatManager), nameof(HatManager.GetUnlockedSkins))]
    private static class UnlockedPatch {
        [HarmonyPriority(Priority.First)]
        private static void Prefix(HatManager __instance) {
            Poll();
            EnsureRegistered(__instance);
        }
    }

    // ---- Träger erkennen ---------------------------------------------------------------------------

    // SkinLayer-Zeiger der Ebenen, die gerade unseren Skin tragen.
    private static readonly HashSet<IntPtr> wearers = new();

    [HarmonyPatch(typeof(SkinLayer), nameof(SkinLayer.SetSkin), typeof(SkinData), typeof(int), typeof(bool),
        typeof(CosmeticsLayer), typeof(Il2CppSystem.Action))]
    private static class SetSkinPatch {
        private static void Prefix(SkinLayer __instance, ref SkinData skinData) {
            try {
                if (ReferenceEquals(__instance, null)) return;
                var key = __instance.Pointer;
                if (!Ours(skinData)) {
                    if (wearers.Count > 0) wearers.Remove(key);
                    return;
                }
                if (wearers.Count > 256) wearers.Clear();
                wearers.Add(key);
                var none = Empty(HatManager.Instance);
                if (none != null) skinData = none;
            } catch (Exception ex) {
                UsefulTORStuffPlugin.Logger?.LogWarning($"[{SkinName}] SetSkin-Umleitung fehlgeschlagen: {ex.Message}");
            }
        }
    }

    // Eine direkt gesetzte SkinViewData (andere Mods) ersetzt unseren Skin.
    [HarmonyPatch(typeof(CosmeticsLayer), nameof(CosmeticsLayer.SetSkin), typeof(SkinViewData), typeof(int))]
    private static class DirectViewPatch {
        private static void Prefix(CosmeticsLayer __instance) {
            if (wearers.Count == 0 || ReferenceEquals(__instance, null)) return;
            var layer = __instance.skin;
            if (!ReferenceEquals(layer, null)) wearers.Remove(layer.Pointer);
        }
    }

    // Nicht CosmeticsCache.GetSkin patchen: TOR ruft es in Helpers.setLook in try/catch auf (bei jedem Rundenstart über
    // Camouflager.resetCamouflage). Mit einem Patch läuft der Aufruf über den Il2CppInterop-Trampolin, der die native
    // Exception nur loggt und null zurückgibt; setLook stürzt dann außerhalb seines try ab und bricht clearAndReloadRoles
    // ab Camouflager ab (Medium, Hacker, Tracker ... nicht zurückgesetzt, FF 1.7.6.6 bis 1.7.7). Deshalb die ID vor TOR tauschen.
    // setLook schreibt die SkinViewData direkt (Camouflager, Morphling, Zurücksetzen) und läuft nicht über SkinLayer.SetSkin.
    // Deshalb hier auch die Trägerliste pflegen, sonst bleibt die Latzhose bei Tarnung sichtbar (User 20.11.).
    [HarmonyPatch(typeof(TheOtherRoles.Helpers), nameof(TheOtherRoles.Helpers.setLook))]
    private static class SetLookPatch {
        private static void Prefix(PlayerControl target, ref string skinId) {
            bool ours = skinId == ProductId;
            if (ours) skinId = SkinData.EmptyId;
            try {
                var layer = !ReferenceEquals(target, null) && !ReferenceEquals(target.cosmetics, null) ? target.cosmetics.skin : null;
                if (ReferenceEquals(layer, null)) return;
                if (ours) {
                    if (wearers.Count > 256) wearers.Clear();
                    wearers.Add(layer.Pointer);
                } else if (wearers.Count > 0) wearers.Remove(layer.Pointer);
            } catch (Exception ex) {
                UsefulTORStuffPlugin.Logger?.LogWarning($"[{SkinName}] setLook-Abgleich fehlgeschlagen: {ex.Message}");
            }
        }
    }

    [HarmonyPatch(typeof(CosmeticsCache), nameof(CosmeticsCache.CoAddSkin))]
    private static class CacheAddPatch {
        private static void Prefix(ref string id) { if (id == ProductId) id = SkinData.EmptyId; }
    }

    // ---- Skin-Tab ----------------------------------------------------------------------------------

    [HarmonyPatch(typeof(CosmeticData), nameof(CosmeticData.SetPreview))]
    private static class PreviewPatch {
        private static bool Prefix(CosmeticData __instance, SpriteRenderer renderer) {
            if (dataPtr == IntPtr.Zero || ReferenceEquals(__instance, null) || __instance.Pointer != dataPtr) return true;
            if (Alive(renderer)) renderer.sprite = Loaded ? icon : null;
            return false;
        }
    }

    [HarmonyPatch(typeof(CosmeticData), nameof(CosmeticData.GetItemName))]
    private static class NamePatch {
        private static void Postfix(CosmeticData __instance, ref string __result) {
            if (dataPtr != IntPtr.Zero && !ReferenceEquals(__instance, null) && __instance.Pointer == dataPtr)
                __result = SkinName;
        }
    }

    // ---- Anzeige -----------------------------------------------------------------------------------

    private sealed class Overlay {
        internal GameObject Anchor;
        internal SpriteRenderer Renderer, Mask;
        internal SpriteAnimNodeSync Source;
        internal int DepthTick;
    }

    private sealed class Owner {
        internal CosmeticsLayer Cosmetics;
        internal PlayerControl Player;
    }

    private static readonly Dictionary<IntPtr, Overlay> overlays = new();
    private static readonly Dictionary<IntPtr, Owner> owners = new();

    /// <summary>Läuft aus dem HatParent.LateUpdate-Postfix in UnknownHat für jede Spielfigur.</summary>
    internal static void Animate(HatParent hp) {
        if (failed || (wearers.Count == 0 && overlays.Count == 0)) return;
        try { Draw(hp); }
        catch (Exception ex) {
            failed = true;
            foreach (var overlay in overlays.Values) Hide(overlay);
            UsefulTORStuffPlugin.Logger?.LogWarning($"[{SkinName}] Anzeige nach einem Fehler angehalten: {ex.Message}");
        }
    }

    private static Owner OwnerOf(HatParent hp) {
        var key = hp.Pointer;
        if (owners.TryGetValue(key, out var owner) && (ReferenceEquals(owner.Cosmetics, null) || Alive(owner.Cosmetics))) return owner;
        if (owners.Count > 256) owners.Clear();
        owner = new Owner { Cosmetics = hp.GetComponentInParent<CosmeticsLayer>() };
        if (!Alive(owner.Cosmetics)) owner.Cosmetics = null;
        else {
            var pc = hp.GetComponentInParent<PlayerControl>();
            owner.Player = Alive(pc) ? pc : null;
        }
        owners[key] = owner;
        return owner;
    }

    private static void Hide(Overlay overlay) {
        if (Alive(overlay.Renderer) && overlay.Renderer.enabled) overlay.Renderer.enabled = false;
        if (Alive(overlay.Mask) && overlay.Mask.enabled) overlay.Mask.enabled = false;
    }

    private enum Pose { Hidden, Idle, Run, Climb }

    private static void Draw(HatParent hp) {
        overlays.TryGetValue(hp.Pointer, out var overlay);
        if (overlay != null && !Alive(overlay.Renderer)) { overlays.Remove(hp.Pointer); overlay = null; }

        var owner = OwnerOf(hp);
        var cosmetics = owner.Cosmetics;
        if (ReferenceEquals(cosmetics, null)) return;
        var skinLayer = cosmetics.skin;
        bool wearing = Loaded && Alive(skinLayer) && wearers.Contains(skinLayer.Pointer);
        if (!wearing) {
            if (overlay != null) Hide(overlay);
            return;
        }

        var bodySprite = cosmetics.currentBodySprite;
        var body = !ReferenceEquals(bodySprite, null) ? bodySprite.BodySprite : null;
        var player = owner.Player;
        int index = -1;
        var pose = PoseOf(cosmetics, player, body, ref index);
        if (pose == Pose.Hidden) {
            if (overlay != null) Hide(overlay);
            return;
        }

        overlay ??= Create(hp);
        var renderer = overlay.Renderer;
        Sprite sprite = pose switch { Pose.Climb => climb, Pose.Run => walk[index], _ => idle };
        var current = renderer.sprite;
        if (ReferenceEquals(current, null) || current.Pointer != sprite.Pointer) renderer.sprite = sprite;

        var front = hp.FrontLayer;
        bool flip = Alive(front) ? front.flipX : cosmetics.FlipX;
        if (renderer.flipX != flip) renderer.flipX = flip;
        Place(overlay, front);

        // Grundkörper bestimmt Sichtbarkeit (Alpha bei Unsichtbarkeit) und Maskierung (Kleiderschrank, Rauswurf).
        float alpha = body.color.a;
        var order = skinLayer.layer;
        if (!Alive(order)) order = body;
        var white = new Color(1f, 1f, 1f, alpha);
        renderer.color = white;
        renderer.maskInteraction = body.maskInteraction;
        renderer.sortingLayerID = order.sortingLayerID;
        renderer.sortingOrder = order.sortingOrder;
        if (!renderer.enabled) renderer.enabled = true;

        if (maskWalk != null) {
            var mask = overlay.Mask;
            Sprite tint = pose switch { Pose.Climb => maskClimb, Pose.Run => maskWalk[index], _ => maskIdle };
            var shown = mask.sprite;
            if (ReferenceEquals(shown, null) || shown.Pointer != tint.Pointer) mask.sprite = tint;
            mask.flipX = flip;
            int id = cosmetics.ColorId;
            Color color = id >= 0 && id < Palette.PlayerColors.Length ? (Color)Palette.PlayerColors[id] : Color.white;
            mask.color = new Color(color.r, color.g, color.b, alpha);
            mask.maskInteraction = body.maskInteraction;
            mask.sortingLayerID = order.sortingLayerID;
            mask.sortingOrder = order.sortingOrder;
            if (!mask.enabled) mask.enabled = true;
        }

        // z wie die echte Skin-Ebene: vor dem Körper, hinter Visier und Hut. Alle 15 Bilder nachgeführt.
        if (--overlay.DepthTick <= 0) {
            overlay.DepthTick = 15;
            var target = skinLayer.layer;
            if (Alive(target)) {
                float delta = target.transform.position.z - renderer.transform.position.z;
                // Körper, Skin, Hut und Visier liegen nur 1e-7 auseinander (zIndexSpacing), daher keine grobe Schwelle.
                if (delta != 0f) {
                    var a = overlay.Anchor.transform.localPosition;
                    overlay.Anchor.transform.localPosition = new Vector3(a.x, a.y, a.z + delta);
                }
            }
        }
    }

    // Die Bilder liegen auf der Hut-Leinwand am Kopfknoten (NodeId 0), wie bei den Feeling-Cute-Hüten. Die Ebene
    // übernimmt die lokale Lage der Hut-Vorderebene. Hängt die HatParent an einem anderen Knoten (Hüte ohne Wippen),
    // kommt der Abstand zwischen Kopfknoten und diesem Knoten dazu.
    private static void Place(Overlay overlay, SpriteRenderer front) {
        float x = 0f, y = 0f;
        if (Alive(front)) {
            var p = front.transform.localPosition;
            x = p.x;
            y = p.y;
        }
        var sync = overlay.Source;
        if (Alive(sync)) {
            int node = sync.NodeId;
            var nodes = sync.Parent;
            if (node != 0 && Alive(nodes)) {
                var head = nodes.GetLocalPosition(0, false);
                var used = nodes.GetLocalPosition(node, false);
                x += head.x - used.x;
                y += head.y - used.y;
            }
        }
        var t = overlay.Renderer.transform;
        var now = t.localPosition;
        if (now.x != x || now.y != y || now.z != 0f) t.localPosition = new Vector3(x, y, 0f);
    }

    private static Pose PoseOf(CosmeticsLayer cosmetics, PlayerControl player, SpriteRenderer body, ref int index) {
        if (!Alive(body) || !body.enabled || cosmetics.CurrentBodyType != PlayerBodyTypes.Normal) return Pose.Hidden;
        if (ReferenceEquals(player, null)) return Pose.Idle;   // Vorschaufiguren (Kleiderschrank, Rauswurf, Endbildschirm)
        var info = player.Data;
        if (!Alive(info) || info.IsDead || player.inVent || player.inMovingPlat) return Pose.Hidden;
        var physics = player.MyPhysics;
        var anims = Alive(physics) ? physics.Animations : null;
        if (!Alive(anims)) return Pose.Idle;
        var animator = anims.Animator;
        var group = anims.group;
        var clip = Alive(animator) ? animator.GetCurrentAnimation() : null;
        if (ReferenceEquals(group, null) || !Alive(clip)) return Pose.Idle;
        var current = clip.Pointer;
        if (Same(group.ClimbUpAnim, current) || Same(group.ClimbDownAnim, current)) return Pose.Climb;
        if (player.onLadder) return Pose.Climb;
        if (Same(group.RunAnim, current)) {
            index = UnknownHat.RunFrameIndex(player);
            return index >= 0 ? Pose.Run : Pose.Idle;
        }
        return Same(group.IdleAnim, current) ? Pose.Idle : Pose.Hidden;
    }

    private static bool Same(AnimationClip clip, IntPtr current) => !ReferenceEquals(clip, null) && clip.Pointer == current;

    private static Overlay Create(HatParent hp) {
        // Geschwister der Hut-Vorderebene. Der Knoten-Sync des Spiels führt bereits die HatParent am Knoten; ein
        // eigener Sync auf der Ebene addierte den Kopfknoten ein zweites Mal (Spieltest 19.11.: 0,2 Einheiten zu hoch).
        var front = hp.FrontLayer;
        var parent = Alive(front) && !ReferenceEquals(front.transform.parent, null) ? front.transform.parent : hp.transform;
        var anchor = new GameObject("FeelingCuteSkin");
        anchor.layer = hp.gameObject.layer;
        anchor.transform.SetParent(parent, false);
        var go = new GameObject("Layer");
        go.layer = anchor.layer;
        go.transform.SetParent(anchor.transform, false);
        var renderer = go.AddComponent<SpriteRenderer>();
        if (Alive(front)) go.transform.localScale = front.transform.localScale;
        var manager = HatManager.Instance;
        if (Alive(manager) && Alive(manager.DefaultShader)) renderer.sharedMaterial = manager.DefaultShader;

        var source = hp.SpriteSyncNode;
        if (!Alive(source)) source = hp.GetComponent<SpriteAnimNodeSync>();

        SpriteRenderer mask = null;
        if (maskWalk != null) {
            var maskGo = new GameObject("Mask");
            maskGo.layer = anchor.layer;
            maskGo.transform.SetParent(go.transform, false);
            maskGo.transform.localPosition = new Vector3(0f, 0f, -0.00000003f);   // unter zIndexSpacing (1e-7)
            mask = maskGo.AddComponent<SpriteRenderer>();
            mask.sharedMaterial = renderer.sharedMaterial;
            mask.enabled = false;
        }

        if (overlays.Count > 128)
            foreach (var dead in overlays.Where(e => !Alive(e.Value.Renderer)).Select(e => e.Key).ToArray())
                overlays.Remove(dead);
        var overlay = new Overlay { Anchor = anchor, Renderer = renderer, Mask = mask, Source = source };
        overlays[hp.Pointer] = overlay;
        return overlay;
    }
}
