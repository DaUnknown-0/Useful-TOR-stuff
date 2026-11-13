// Useful TOR Stuff - Copyright (C) 2026 DaUnknown-0
// Licensed under GPL-3.0-or-later. See LICENSE for details.

/*
 * ClipFill - AudioClip.SetData without one huge il2cpp array per clip (2026-11-13).
 *
 * clip.SetData(float[] samples, 0) looks free, but Il2CppInterop turns the managed float[] into an
 * Il2CppStructArray<float> of the same length first: a decoded music track of 65-98 s stereo is a single
 * 22-35 MB object on the il2cpp heap. The draft/lobby/meeting music decodes two or three tracks per round, and
 * the Boehm GC, which scans conservatively and never moves anything, did not get those blocks back: the heap
 * grew by about the decoded sizes every round while the live data stayed at ~17 MB (analysis with Fable, the
 * "ready" log lines summed to 357 MB against 376 MB of heap after six rounds).
 *
 * Here the samples go in through one small reused il2cpp array (16384 floats = 64 KB), piece by piece with the
 * offset in sample frames; only the last, shorter piece needs a fresh small array. This file exists in the mods
 * that load long clips (Forgotten Fixes, Unknown's Collection, Unknown's Atlas), namespace aside identical.
 */

using System;
using Il2CppInterop.Runtime.InteropTypes.Arrays;
using UnityEngine;

namespace UsefulTORStuff {

    internal static class ClipFill {
        private const int ChunkFloats = 16384;
        private static Il2CppStructArray<float> chunk;

        /// <summary>Writes all samples (interleaved, `channels` per frame) into the clip from frame 0. Main thread only.</summary>
        internal static void SetData(AudioClip clip, float[] samples, int channels) {
            if (clip == null || samples == null || samples.Length == 0) return;
            if (channels < 1) channels = 1;
            int per = Math.Max(channels, ChunkFloats / channels * channels);
            if (chunk == null || chunk.Length != per) chunk = new Il2CppStructArray<float>(per);
            int pos = 0;
            while (pos < samples.Length) {
                int n = Math.Min(per, samples.Length - pos);
                var target = n == per ? chunk : new Il2CppStructArray<float>(n);
                for (int i = 0; i < n; i++) target[i] = samples[pos + i];
                clip.SetData(target, pos / channels);
                pos += n;
            }
        }
    }
}
