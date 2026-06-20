// Useful TOR Stuff - Copyright (C) 2026 DaUnknown-0
// Licensed under GPL-3.0-or-later. See LICENSE for details.
// Based on The Other Roles (https://github.com/TheOtherRolesAU/TheOtherRoles), GPL-3.0.

/*
 * InvertVision - new Invert option "Inverted Vision": a TRUE colour negative for the inverted player
 * while the modifier is active (Invert.meetings > 0).
 *
 * The earlier approach drew the invert material onto a cloned HUD SpriteRenderer (HudManager.FullScreen).
 * That sits in the UI render pass *over* the world, so an OneMinusDstColor blend there does not reliably
 * invert the rendered game world. This version composites the negative over the WORLD camera instead:
 * a small Il2Cpp MonoBehaviour on Camera.main implements OnRenderImage and, after copying the rendered
 * scene through, draws a white full-screen quad with blend (OneMinusDstColor, Zero) in GL immediate
 * mode -> final = white*(1-dst) = 1 - dst, a real per-pixel negative of the scene. GL immediate-mode
 * drawing is exactly what the built-in "Hidden/Internal-Colored" shader is designed for, so no custom
 * shader / AssetBundle is needed. Only the world camera is inverted; the separate HUD stays readable.
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

        private static Material invertMaterial;  // Internal-Colored with invert blend
        private static bool materialTried;
        private static bool typeRegistered;
        private static Camera attachedCam;       // camera we last added the component to

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

            // Register the Il2Cpp camera component once so it can be attached to Camera.main later.
            try {
                if (!typeRegistered) {
                    ClassInjector.RegisterTypeInIl2Cpp<InvertVisionCamera>();
                    typeRegistered = true;
                }
            } catch (Exception e) {
                UsefulTORStuffPlugin.Logger?.LogWarning($"[InvertVision] component registration failed: {e.Message}");
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

        // Keep the invert component attached to the live world camera. Cheap GetComponent check; the
        // component survives until the camera is destroyed (scene change), then gets re-added.
        [HarmonyPatch(typeof(HudManager), nameof(HudManager.Update))]
        static class HudUpdatePatch {
            public static void Postfix() {
                try {
                    if (!typeRegistered) return;
                    var cam = Camera.main;
                    if (cam == null) return;
                    if (cam == attachedCam) return; // already attached to this camera
                    if (cam.GetComponent<InvertVisionCamera>() == null)
                        cam.gameObject.AddComponent<InvertVisionCamera>();
                    attachedCam = cam;
                } catch (Exception e) {
                    UsefulTORStuffPlugin.Logger?.LogError($"[InvertVision] camera attach failed: {e}");
                }
            }
        }

        // Il2Cpp MonoBehaviour: inverts the rendered scene in OnRenderImage. When inactive it just
        // blits the scene through unchanged, so it is safe to leave attached permanently.
        public class InvertVisionCamera : MonoBehaviour {
            public InvertVisionCamera(IntPtr ptr) : base(ptr) { }

            public void OnRenderImage(RenderTexture src, RenderTexture dest) {
                try {
                    EnsureMaterial();
                    if (invertMaterial == null || !Active()) {
                        Graphics.Blit(src, dest);
                        return;
                    }
                    // Copy the rendered scene, then overlay a white full-screen quad with the
                    // OneMinusDstColor/Zero blend → the destination becomes 1 - scene.
                    Graphics.Blit(src, dest);
                    var prev = RenderTexture.active;
                    RenderTexture.active = dest;
                    GL.PushMatrix();
                    GL.LoadOrtho();
                    invertMaterial.SetPass(0);
                    GL.Begin(7); // 7 == GL.QUADS (the const isn't exposed in the Il2Cpp binding)
                    GL.Color(Color.white);
                    GL.Vertex3(0f, 0f, 0f);
                    GL.Vertex3(1f, 0f, 0f);
                    GL.Vertex3(1f, 1f, 0f);
                    GL.Vertex3(0f, 1f, 0f);
                    GL.End();
                    GL.PopMatrix();
                    RenderTexture.active = prev;
                } catch (Exception e) {
                    UsefulTORStuffPlugin.Logger?.LogError($"[InvertVision] OnRenderImage failed: {e}");
                    try { Graphics.Blit(src, dest); } catch { }
                }
            }
        }
    }
}
