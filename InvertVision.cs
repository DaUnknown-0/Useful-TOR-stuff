// Useful TOR Stuff - Copyright (C) 2026 DaUnknown-0
// Licensed under GPL-3.0-or-later. See LICENSE for details.
// Based on The Other Roles (https://github.com/TheOtherRolesAU/TheOtherRoles), GPL-3.0.

/*
 * InvertVision - new Invert option "Inverted Vision": a TRUE colour negative for the inverted player
 * while the modifier is active (Invert.meetings > 0).
 *
 * No AssetBundle / custom shader file is needed. Unity ships the built-in shader
 * "Hidden/Internal-Colored", which (unlike Sprites/Default) exposes its blend factors as material
 * properties _SrcBlend/_DstBlend. Setting them to (OneMinusDstColor, Zero) and drawing a white
 * fullscreen quad gives  final = white*(1-dst) + dst*0 = 1 - dst  -> a real per-pixel inversion of
 * the framebuffer.
 *
 * We hold our own fullscreen SpriteRenderer overlay (cloned from HudManager.FullScreen so it's sized
 * correctly) with that material; the sprite texture is ignored by Internal-Colored, only its quad
 * geometry + white vertex colour matter. Using our own overlay avoids fighting TOR's transient uses
 * of the shared HudManager.FullScreen (Time Master rewind, lights, ...).
 */

using System;
using HarmonyLib;
using UnityEngine;
using UnityEngine.Rendering;
using TheOtherRoles;
using static TheOtherRoles.TheOtherRoles;
using Types = TheOtherRoles.CustomOption.CustomOptionType;

namespace UsefulTORStuff {
    public static class InvertVision {
        public static CustomOption Option;  // Off/On toggle

        private static SpriteRenderer overlay;       // our dedicated full-screen overlay
        private static Material invertMaterial;       // Internal-Colored with invert blend
        private static bool materialTried;

        public static void CreateOptions() {
            try {
                Option = CustomOption.Create(
                    1290, Types.Modifier, "Inverted Vision", false, CustomOptionHolder.modifierInvert);

                var opts = CustomOption.options;
                opts.Remove(Option);
                int idx = opts.IndexOf(CustomOptionHolder.modifierInvertDuration);
                if (idx < 0) idx = opts.Count - 1;
                opts.Insert(idx + 1, Option);

                UsefulTORStuffPlugin.Logger?.LogInfo("[InvertVision] Option created under Invert.");
            } catch (Exception e) {
                UsefulTORStuffPlugin.Logger?.LogError($"[InvertVision] CreateOptions failed: {e}");
            }
        }

        // Build the invert material from the built-in Internal-Colored shader (once).
        private static void EnsureMaterial() {
            if (materialTried) return;
            materialTried = true;
            try {
                var shader = Shader.Find("Hidden/Internal-Colored");
                if (shader == null) {
                    UsefulTORStuffPlugin.Logger?.LogWarning("[InvertVision] Hidden/Internal-Colored not found — inverted vision unavailable.");
                    return;
                }
                invertMaterial = new Material(shader);
                invertMaterial.SetColor("_Color", Color.white);
                // final = src*(1-dst) + dst*0 = 1 - dst  -> colour negative
                invertMaterial.SetInt("_SrcBlend", (int)BlendMode.OneMinusDstColor);
                invertMaterial.SetInt("_DstBlend", (int)BlendMode.Zero);
                invertMaterial.SetInt("_ZWrite", 0);
                invertMaterial.SetInt("_ZTest", (int)CompareFunction.Always);
                invertMaterial.SetInt("_Cull", (int)CullMode.Off);
                UsefulTORStuffPlugin.Logger?.LogInfo("[InvertVision] Built real invert material.");
            } catch (Exception e) {
                UsefulTORStuffPlugin.Logger?.LogWarning($"[InvertVision] Material build failed: {e.Message}");
            }
        }

        private static bool Active() {
            try {
                if (Option == null || !Option.getBool()) return false;
                if (Invert.invert == null || Invert.meetings <= 0) return false;
                var lp = PlayerControl.LocalPlayer;
                if (lp == null || lp.Data == null || lp.Data.IsDead) return false;
                for (int i = 0; i < Invert.invert.Count; i++)
                    if (Invert.invert[i] != null && Invert.invert[i].PlayerId == lp.PlayerId) return true;
                return false;
            } catch { return false; }
        }

        [HarmonyPatch(typeof(HudManager), nameof(HudManager.Update))]
        static class HudUpdatePatch {
            public static void Postfix(HudManager __instance) {
                try {
                    EnsureMaterial();
                    if (invertMaterial == null) return;

                    bool active = Active();

                    if (overlay == null && __instance.FullScreen != null) {
                        overlay = UnityEngine.Object.Instantiate(__instance.FullScreen, __instance.transform);
                        overlay.name = "UsefulInvertOverlay";
                        overlay.material = invertMaterial; // true colour negative (1 - rgb)
                        overlay.color = Color.white;
                        overlay.gameObject.SetActive(false);
                    }
                    if (overlay == null) return;

                    if (active) {
                        overlay.enabled = true;
                        if (!overlay.gameObject.activeSelf) overlay.gameObject.SetActive(true);
                    } else if (overlay.gameObject.activeSelf) {
                        overlay.enabled = false;
                        overlay.gameObject.SetActive(false);
                    }
                } catch (Exception e) {
                    UsefulTORStuffPlugin.Logger?.LogError($"[InvertVision] HudManager.Update postfix failed: {e}");
                }
            }
        }
    }
}
