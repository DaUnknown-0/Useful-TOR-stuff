// Useful TOR Stuff - Copyright (C) 2026 DaUnknown-0
// Licensed under GPL-3.0-or-later. See LICENSE for details.
// Based on The Other Roles (https://github.com/TheOtherRolesAU/TheOtherRoles), GPL-3.0.

/*
 * HatTextureDiet - TOR's hat pack at a fraction of its memory.
 *
 * THE NUMBERS (2026-08-29, host, CrashDiagnostics breakdown)
 * -----------------------------------------------------------
 * Joining a lobby took the process from 323 MB to 1251 MB. The breakdown for that scene listed
 * 1602 Texture2D objects holding 634 MB, the twelve largest all unnamed ARGB32-with-mipmaps
 * textures of hat-shaped sizes (601x751 = brainglass, 600x750 = Dino5, twice each: normal +
 * climb). The hat folder holds 1032 PNGs, 27 MB on disk; decoded the way TOR decodes them
 * (`Helpers.loadTextureFromDisk`: ARGB32, mipmaps on) that is 597 MB. TOR loads every hat - main,
 * back, climb, flip - on the first HatManager.GetHatById of the session, marks the textures
 * DontUnloadUnusedAsset, and never lets go (Modules/CustomHats/CustomHatManager.cs:135-150,
 * Patches/HatManagerPatches.cs:19-45). On a 32-bit process that is half the address space the
 * game can comfortably use, spent before a single round has started, on every client with TOR.
 * Two of the crash dumps from this week sit exactly on such memory jumps.
 *
 * WHAT THIS DOES
 * --------------
 * A prefix on Helpers.loadTextureFromDisk (a managed TOR method nobody else patches; the only
 * caller is CreateHatSprite) that decodes the PNG exactly as TOR does and then:
 *
 *   1. Compress(highQuality: true)      ARGB32 -> DXT5. A quarter of the bytes; mipmaps stay,
 *                                       because hats are drawn far below native size on the
 *                                       player and mipmaps are what keeps that from shimmering.
 *                                       DXT5 is lossy at hard edges; at hat size on screen it is
 *                                       invisible. Sizes that are not a multiple of four are
 *                                       padded by Unity; if Compress ever refuses, the texture is
 *                                       kept as it was rather than lost.
 *   2. Apply(false, makeNoLongerReadable: true)
 *                                       drops the CPU-side copy Unity keeps for readable textures.
 *                                       Nothing in TOR's hat code reads a pixel back (no GetPixel,
 *                                       ReadPixels or EncodeToPNG anywhere under CustomHats), so
 *                                       the copy was pure overhead: roughly half of every hat.
 *
 * Together: ~597 MB -> on the order of 75 MB. MEASURED on the host the same evening, at the
 * process level where it cannot be argued with: joining a lobby went from 1251 MB to 646 MB of
 * private bytes, the lobby's Texture2D total from 634 MB to 27 MB, Unity's allocator from 848 MB
 * to 236 MB. The per-hat log line ("577 MB -> about 145 MB") takes the before-size from
 * Profiler.GetRuntimeMemorySizeLong and computes the after-size from the format, because the
 * profiler reports 0 for a texture that no longer has a CPU copy.
 *
 * Not gated on the host and not an in-game option: this is the local client's own memory, no
 * outcome depends on it, and it must work in a lobby whose host runs nothing at all. Off switch in
 * the BepInEx config for the day a hat looks wrong.
 */

using System;
using System.IO;
using BepInEx.Configuration;
using HarmonyLib;
using TheOtherRoles;
using UnityEngine;
using UnityEngine.Profiling;

namespace UsefulTORStuff {

    public static class HatTextureDiet {
        private static ConfigEntry<bool> enabled;
        private static ConfigEntry<bool> compress;

        private static int loaded, compressed, compressFailed;
        private static long bytesBefore, bytesAfter;

        public static void Bind(ConfigFile config) {
            enabled = config.Bind("HatTextureDiet", "Enabled", true,
                "Shrink TOR's hat textures in memory (DXT5 + no CPU copy). Client-local, no effect on " +
                "other players. Turn off if a hat looks wrong.");
            compress = config.Bind("HatTextureDiet", "Compress", true,
                "Also compress to DXT5 (a quarter of the bytes, slightly lossy at hard edges). With " +
                "this off only the CPU copy is dropped, which is lossless and saves about half.");
        }

        [HarmonyPatch(typeof(Helpers), nameof(Helpers.loadTextureFromDisk))]
        internal static class LoadTextureFromDiskPatch {
            public static bool Prefix(string path, ref Texture2D __result) {
                if (enabled == null || !enabled.Value) return true;
                try {
                    if (!File.Exists(path)) { __result = null; return false; }   // same as TOR: null, logged by the caller
                    var texture = new Texture2D(2, 2, TextureFormat.ARGB32, true);
                    byte[] bytes = File.ReadAllBytes(path);
                    if (!ImageConversion.LoadImage(texture, bytes, false)) {
                        // TOR would have returned a 2x2 placeholder here; a null makes the caller skip
                        // the hat, which is the honest outcome for an undecodable file.
                        UnityEngine.Object.Destroy(texture);
                        __result = null;
                        return false;
                    }

                    long before = 0;
                    try { before = Profiler.GetRuntimeMemorySizeLong(texture); } catch { }

                    if (compress != null && compress.Value) {
                        try {
                            texture.Compress(true);
                            compressed++;
                        } catch (Exception e) {
                            compressFailed++;
                            if (compressFailed <= 5)
                                UsefulTORStuffPlugin.Logger?.LogWarning(
                                    $"[HatTextureDiet] Compress failed for {Path.GetFileName(path)} " +
                                    $"({texture.width}x{texture.height}): {e.GetType().Name}: {e.Message} - kept uncompressed.");
                        }
                    }
                    // The mip chain was built by LoadImage (and rebuilt by Compress); nothing to
                    // update. What this call is for is the second argument.
                    texture.Apply(false, true);

                    // Profiler.GetRuntimeMemorySizeLong reports 0 for a texture without a CPU copy
                    // (measured: "577 MB -> 0 MB" for a thousand hats), so the after-size is computed
                    // from the format instead: DXT5 is one byte per pixel, ARGB32 four, mips add a
                    // third. The before-size stays the runtime's own number.
                    long after = 0;
                    try {
                        double bpp = texture.format == TextureFormat.DXT5 ? 1.0
                                   : texture.format == TextureFormat.DXT1 ? 0.5 : 4.0;
                        after = (long)(texture.width * texture.height * bpp * (texture.mipmapCount > 1 ? 4.0 / 3.0 : 1.0));
                    } catch { }
                    loaded++;
                    bytesBefore += before;
                    bytesAfter += after;
                    if (loaded % 250 == 0) LogTotals();

                    __result = texture;
                    return false;
                } catch (Exception e) {
                    UsefulTORStuffPlugin.Logger?.LogWarning(
                        $"[HatTextureDiet] failed on {Path.GetFileName(path)}: {e.GetType().Name}: {e.Message} - falling back to TOR's loader.");
                    return true;
                }
            }
        }

        public static void LogTotals() {
            if (loaded == 0) return;
            UsefulTORStuffPlugin.Logger?.LogInfo(
                $"[HatTextureDiet] {loaded} hat texture(s): {bytesBefore >> 20} MB -> about {bytesAfter >> 20} MB on the GPU, no CPU copy " +
                $"({compressed} compressed, {compressFailed} kept uncompressed).");
        }
    }
}
