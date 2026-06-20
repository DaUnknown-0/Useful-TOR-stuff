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
        private static bool shaderMissing;            // Shader.Find dauerhaft fehlgeschlagen → nicht retrien
        private static bool lastActive;              // DIAG: log only on state change

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

        // Per-Runden-Reset: nach Spielwechsel wird der HudManager (und damit unser Overlay-Kind)
        // neu erzeugt; der statische overlay-Verweis zeigt dann auf ein zerstörtes GameObject und
        // wird nicht zuverlässig als null erkannt → ohne Reset bliebe der Negativ-Filter ab Runde 2
        // aus. Wir zerstören ein noch lebendes Overlay und nullen die Laufzeit-Statics. Das Material
        // bleibt erhalten (in EnsureMaterial vor dem Entladen geschützt bzw. bei Bedarf neu gebaut).
        public static void Reset() {
            try {
                if (overlay != null) {
                    try { UnityEngine.Object.Destroy(overlay.gameObject); } catch { }
                }
            } catch { }
            overlay = null;
            lastActive = false;
        }

        // Läuft pro Runde auf jedem Client (gleiches Muster wie TiebreakerMultiple.ResetPatch).
        [HarmonyPatch(typeof(RPCProcedure), nameof(RPCProcedure.resetVariables))]
        static class ResetPatch {
            public static void Postfix() { Reset(); }
        }

        // Build the invert material from the built-in Internal-Colored shader.
        // Wichtig: Unity zerstört ein per `new Material(...)` erzeugtes Material beim Szenenwechsel
        // (Spielende → neue Runde). Beim ersten Build überlebte das Material genau einen Spielwechsel
        // nicht, ein `materialTried`-Flag verhinderte den Neuaufbau → der Negativ-Filter „erschien
        // einmal, danach nie wieder". Fix: (1) `HideAndDontSave` + `DontDestroyOnLoad` schützen das
        // Material vor dem Entladen; (2) ist es trotzdem zerstört (fake-null), bauen wir es neu auf,
        // solange der Shader grundsätzlich verfügbar ist (`shaderMissing` verhindert nur Endlos-Retries
        // bei wirklich fehlendem Shader).
        private static void EnsureMaterial() {
            if (invertMaterial != null) return;   // gültiges Material vorhanden
            if (shaderMissing) return;            // Shader dauerhaft nicht da → nicht weiter versuchen
            try {
                var shader = Shader.Find("Hidden/Internal-Colored");
                if (shader == null) {
                    shaderMissing = true;
                    UsefulTORStuffPlugin.Logger?.LogWarning("[InvertVision] Hidden/Internal-Colored not found — inverted vision unavailable.");
                    return;
                }
                invertMaterial = new Material(shader);
                invertMaterial.hideFlags = HideFlags.HideAndDontSave; // überlebt Szenenwechsel
                UnityEngine.Object.DontDestroyOnLoad(invertMaterial);
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
                    if (active != lastActive) {
                        lastActive = active;
                        UsefulTORStuffPlugin.Logger?.LogInfo(
                            $"[InvertVision][DIAG] active={active}, material={(invertMaterial != null)}, " +
                            $"overlay={(overlay != null)}, FullScreen={(__instance.FullScreen != null)}.");
                    }

                    // Stale-Overlay aussortieren: ein zerstörtes oder unter einem anderen (neuen)
                    // HudManager hängendes Overlay verwerfen, damit es frisch unter __instance
                    // erzeugt wird. Selbstheilung auch ohne den resetVariables-Hook.
                    if (overlay != null) {
                        try {
                            if (overlay.gameObject == null || overlay.transform.parent != __instance.transform)
                                overlay = null;
                        } catch { overlay = null; }
                    }

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
