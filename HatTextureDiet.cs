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
 * CACHE (2026-10-15)
 * ------------------
 * The first hat lookup of a session loads all ~1000 hats in ONE frame; measured 5.5 s, after which the login
 * finishes and the menu takes input again. Decoding the PNG and Compress(true) are what costs. So the result is
 * kept: after the first decode the DXT5 mip chain goes to BepInEx\cache\uts-hats (written on a worker thread),
 * and later starts upload it with LoadRawTextureData, no PNG, no compressing. A cache file names the size and
 * the write time of its PNG; TOR replacing a hat changes both, and the hat is decoded anew. Anything odd about a
 * cache file (wrong header, size, a refused upload) falls back to the decode and rewrites it.
 * Only a game started by the Mod Manager's "Restart now" reads the cache (user, 15.10.: speed-ups for that
 * restart only, not for closing and opening the game by hand); a normal start loads the hats as before and
 * merely writes the cache files that are missing, so the next restart finds them.
 *
 * Not gated on the host and not an in-game option: this is the local client's own memory, no
 * outcome depends on it, and it must work in a lobby whose host runs nothing at all. Off switch in
 * the BepInEx config for the day a hat looks wrong.
 */

using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Threading.Tasks;
using BepInEx.Configuration;
using HarmonyLib;
using TheOtherRoles;
using UnityEngine;
using UnityEngine.Profiling;

namespace UsefulTORStuff {

    public static class HatTextureDiet {
        private static ConfigEntry<bool> enabled;
        private static ConfigEntry<bool> compress;
        private static ConfigEntry<bool> cache;
        private static int fromCache, cacheWrites, cacheRejected;
        private static long prefixTicks, readTicks;

        private static int loaded, compressed, compressFailed;
        private static long bytesBefore, bytesAfter;

        public static void Bind(ConfigFile config) {
            enabled = config.Bind("HatTextureDiet", "Enabled", true,
                "Shrink TOR's hat textures in memory (DXT5 + no CPU copy). Client-local, no effect on " +
                "other players. Turn off if a hat looks wrong.");
            compress = config.Bind("HatTextureDiet", "Compress", true,
                "Also compress to DXT5 (a quarter of the bytes, slightly lossy at hard edges). With " +
                "this off only the CPU copy is dropped, which is lossless and saves about half.");
            cache = config.Bind("HatTextureDiet", "Cache", true,
                "Keep the compressed hats in BepInEx/cache/uts-hats, so later starts skip decoding and compressing " +
                "(the first hat of a session otherwise freezes the game for several seconds). Needs Compress.");
        }

        internal static string CacheDir => Path.Combine(BepInEx.Paths.CachePath, "uts-hats");

        private static string CacheFile(string path) {
            using var sha = SHA1.Create();
            var h = sha.ComputeHash(Encoding.UTF8.GetBytes(Path.GetFullPath(path).ToLowerInvariant()));
            return Path.Combine(CacheDir, Path.GetFileNameWithoutExtension(path) + "_" + BitConverter.ToString(h, 0, 6).Replace("-", "") + ".uhc");
        }

        // "UHC1", format, width, height, mip count, PNG size, PNG write time (UTC ticks), raw length; then the raw mip chain
        private const int HeaderSize = 40;
        private static readonly byte[] Magic = { (byte)'U', (byte)'H', (byte)'C', (byte)'1' };

        private static bool UseCache => cache != null && cache.Value && compress != null && compress.Value;
        private static bool ReadCache => UseCache && UTSRestart.IsRestart;

        private static Texture2D LoadCached(string path, FileInfo png) {
            string file = CacheFile(path);
            if (!File.Exists(file)) return null;
            Texture2D tex = null;
            try {
                long r0 = System.Diagnostics.Stopwatch.GetTimestamp();
                byte[] all = File.ReadAllBytes(file);
                readTicks += System.Diagnostics.Stopwatch.GetTimestamp() - r0;
                if (all.Length < HeaderSize || all[0] != Magic[0] || all[1] != Magic[1] || all[2] != Magic[2] || all[3] != Magic[3]) throw new InvalidDataException("header");
                int format = BitConverter.ToInt32(all, 4), w = BitConverter.ToInt32(all, 8), h = BitConverter.ToInt32(all, 12), mips = BitConverter.ToInt32(all, 16);
                long len = BitConverter.ToInt64(all, 20), ticks = BitConverter.ToInt64(all, 28);
                int raw = BitConverter.ToInt32(all, 36);
                if (len != png.Length || ticks != png.LastWriteTimeUtc.Ticks) return null;          // the PNG changed: decode anew
                if (format != (int)TextureFormat.DXT5 || raw != all.Length - HeaderSize || w <= 0 || h <= 0) throw new InvalidDataException("data");
                tex = new Texture2D(w, h, TextureFormat.DXT5, mips > 1);
                if (tex.mipmapCount != mips) throw new InvalidDataException("mips");
                var pin = GCHandle.Alloc(all, GCHandleType.Pinned);
                try { tex.LoadRawTextureData(pin.AddrOfPinnedObject() + HeaderSize, raw); } finally { pin.Free(); }
                tex.Apply(false, true);
                return tex;
            } catch (Exception e) {
                if (tex != null) UnityEngine.Object.Destroy(tex);
                cacheRejected++;
                if (cacheRejected <= 5) UsefulTORStuffPlugin.Logger?.LogWarning($"[HatTextureDiet] cache file for {Path.GetFileName(path)} not usable ({e.Message}), decoding the PNG.");
                try { File.Delete(file); } catch { }
                return null;
            }
        }

        // The interop's array-to-byte[] conversion goes element by element: for a thousand hats it doubled the
        // first start (measured 15.10.: 10.6 s instead of 5.5 s in the loader). The elements of an Il2Cpp array
        // follow its header of four pointers, so one block copy does it; the first hat checks that both ways agree.
        private static int fastCopy;   // 0 unknown, 1 agrees, -1 differs (stay with the conversion)

        private static byte[] RawBytes(Texture2D tex) {
            var arr = tex.GetRawTextureData();
            if (fastCopy >= 0) {
                var fast = new byte[arr.Length];
                Marshal.Copy(arr.Pointer + 4 * IntPtr.Size, fast, 0, fast.Length);
                if (fastCopy == 1) return fast;
                byte[] slow = arr;
                bool same = slow.Length == fast.Length && slow.AsSpan().SequenceEqual(fast);
                fastCopy = same ? 1 : -1;
                if (!same) UsefulTORStuffPlugin.Logger?.LogWarning("[HatTextureDiet] block copy differs from the conversion, using the conversion.");
                return slow;
            }
            return arr;
        }

        // Main thread: copies the mip chain (the texture is still readable here); a worker writes it.
        private static void SaveCached(string path, FileInfo png, Texture2D tex) {
            byte[] raw = RawBytes(tex);
            int w = tex.width, h = tex.height, mips = tex.mipmapCount;
            long len = png.Length, ticks = png.LastWriteTimeUtc.Ticks;
            string file = CacheFile(path);
            cacheWrites++;
            Task.Run(() => {
                try {
                    Directory.CreateDirectory(CacheDir);
                    string tmp = file + ".part";
                    using (var fs = new FileStream(tmp, FileMode.Create, FileAccess.Write))
                    using (var bw = new BinaryWriter(fs)) {
                        bw.Write(Magic); bw.Write((int)TextureFormat.DXT5); bw.Write(w); bw.Write(h); bw.Write(mips);
                        bw.Write(len); bw.Write(ticks); bw.Write(raw.Length); bw.Write(raw);
                    }
                    if (File.Exists(file)) File.Delete(file);
                    File.Move(tmp, file);
                } catch (Exception e) {
                    UsefulTORStuffPlugin.Logger?.LogWarning($"[HatTextureDiet] cache write for {Path.GetFileName(path)} failed: {e.Message}");
                }
            });
        }

        [HarmonyPatch(typeof(Helpers), nameof(Helpers.loadTextureFromDisk))]
        internal static class LoadTextureFromDiskPatch {
            public static bool Prefix(string path, ref Texture2D __result) {
                if (enabled == null || !enabled.Value) return true;
                long p0 = System.Diagnostics.Stopwatch.GetTimestamp();
                try {
                    var png = new FileInfo(path);
                    if (!png.Exists) { __result = null; return false; }   // same as TOR: null, logged by the caller
                    if (ReadCache) {
                        var cached = LoadCached(path, png);
                        if (cached != null) {
                            fromCache++;
                            loaded++;
                            bytesAfter += (long)(cached.width * cached.height * (cached.mipmapCount > 1 ? 4.0 / 3.0 : 1.0));
                            if (loaded % 250 == 0) LogTotals();
                            __result = cached;
                            return false;
                        }
                    }
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
                        long t0 = System.Diagnostics.Stopwatch.GetTimestamp();
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
                        compressTicks += System.Diagnostics.Stopwatch.GetTimestamp() - t0;
                    }
                    // a restart rewrites what it could not use; a normal start only adds what is missing
                    if (UseCache && texture.format == TextureFormat.DXT5 && (UTSRestart.IsRestart || !File.Exists(CacheFile(path)))) {
                        try { SaveCached(path, png, texture); }
                        catch (Exception e) { UsefulTORStuffPlugin.Logger?.LogWarning($"[HatTextureDiet] cache copy failed: {e.Message}"); }
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
                } finally {
                    prefixTicks += System.Diagnostics.Stopwatch.GetTimestamp() - p0;
                }
            }
        }

        // Main-thread time spent inside Compress(true), so the memory win can be weighed against the
        // loading time it costs (audit 2026-10-04).
        private static long compressTicks;

        public static void LogTotals() {
            if (loaded == 0) return;
            long ms = compressTicks * 1000 / System.Diagnostics.Stopwatch.Frequency;
            UsefulTORStuffPlugin.Logger?.LogInfo(
                $"[HatTextureDiet] {loaded} hat texture(s): {bytesBefore >> 20} MB -> about {bytesAfter >> 20} MB on the GPU, no CPU copy " +
                $"({compressed} compressed, {compressFailed} kept uncompressed; compression took {ms} ms on the main thread; " +
                $"{fromCache} from the cache, {cacheWrites} written to it, {cacheRejected} cache files refused; " +
                $"{prefixTicks * 1000 / System.Diagnostics.Stopwatch.Frequency} ms in the loader in all, " +
                $"{readTicks * 1000 / System.Diagnostics.Stopwatch.Frequency} ms of it reading cache files).");
        }
    }
}
