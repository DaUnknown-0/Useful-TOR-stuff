// Useful TOR Stuff - Copyright (C) 2026 DaUnknown-0
// Licensed under GPL-3.0-or-later. See LICENSE for details.

/*
 * MusicVolumeDiag - autotest only: does the game's Music slider reach our music? A file BepInEx\musicvol_test.flag
 * (deleted first) makes the main menu measure what the listener hears (AudioListener.GetOutputData, RMS) with the
 * music volume as it is and at 0, logs every playing source with volume and mixer group, puts the volume back and
 * quits. Nothing happens without the file.
 */

using System.Collections;
using BepInEx;
using HarmonyLib;
using UnityEngine;

namespace UsefulTORStuff {

    internal static class MusicVolumeDiag {
        private static string Flag => System.IO.Path.Combine(Paths.BepInExRootPath, "musicvol_test.flag");
        // The flag is taken at plugin load: a test that never reached the main menu must not leave the SoundManager
        // patches below on for the next normal start (Opus audit 2026-10-21, N4).
        private static bool armed;
        private static void Log(string s) => UsefulTORStuffPlugin.Logger?.LogInfo("[MusicVolTest] " + s);

        [HarmonyPatch(typeof(MainMenuManager), nameof(MainMenuManager.Start))]
        private static class Patch {
            private static void Postfix() {
                if (!armed || DraftMusicDriver.Instance == null) return;
                armed = false;
                BepInEx.Unity.IL2CPP.Utils.MonoBehaviourExtensions.StartCoroutine(DraftMusicDriver.Instance, Run());
            }
        }

        // Who creates the SoundManager? Logged at plugin load and with the stack of every SoundManager constructor
        // call afterwards (only with the flag file).
        internal static void AtLoad(Harmony h) {
            if (!System.IO.File.Exists(Flag)) return;
            try { System.IO.File.Delete(Flag); } catch { return; }
            armed = true;
            Log($"at load: SoundManager exists {SoundManager.instance != null}");
            try {
                foreach (var c in AccessTools.GetDeclaredConstructors(typeof(SoundManager)))
                    if (c.GetParameters().Length == 0)
                        h.Patch(c, postfix: new HarmonyMethod(typeof(MusicVolumeDiag), nameof(CtorPostfix)));
            } catch (System.Exception e) { Log("ctor patch failed: " + e.Message); }
            try {
                h.Patch(AccessTools.PropertyGetter(typeof(SoundManager), nameof(SoundManager.Instance)),
                    prefix: new HarmonyMethod(typeof(MusicVolumeDiag), nameof(InstancePrefix)));
            } catch (System.Exception e) { Log("Instance patch failed: " + e.Message); }
            try {
                h.Patch(AccessTools.Method(typeof(SoundManager), "Start"), prefix: new HarmonyMethod(typeof(MusicVolumeDiag), nameof(StartPrefix)));
            } catch (System.Exception e) { Log("Start patch failed: " + e.Message); }
        }

        private static int lazyLogs;
        private static void InstancePrefix() {
            if (SoundManager.instance != null || lazyLogs++ > 2) return;
            Log("SoundManager.Instance asked while none exists, by " + System.Environment.StackTrace);
        }

        private static void CtorPostfix() => Log("SoundManager constructed at " + System.Environment.StackTrace);

        private static void StartPrefix(SoundManager __instance) =>
            Log($"SoundManager.Start '{__instance.name}' scene '{__instance.gameObject.scene.name}', mixer {(__instance.mixer != null ? "yes" : "NULL")} {System.Environment.StackTrace}");

        private static float Rms() {
            var buf = new float[2048];
            double sum = 0;
            for (int ch = 0; ch < 2; ch++) {
                AudioListener.GetOutputData(buf, ch);
                foreach (var v in buf) sum += v * v;
            }
            return Mathf.Sqrt((float)(sum / (buf.Length * 2)));
        }

        private static IEnumerator Measure(string what) {
            float acc = 0; int n = 0;
            for (int i = 0; i < 20; i++) { acc += Rms(); n++; yield return new WaitForSeconds(0.05f); }
            Log($"{what}: listener RMS {acc / n:F5} (music volume setting {SoundManager.MusicVolume:F2})");
        }

        private static void Sources() {
            foreach (var a in Object.FindObjectsOfType<AudioSource>()) {
                try {
                    if (a == null || a.clip == null || !a.isPlaying) continue;
                    Log($"  playing '{a.clip.name}' volume {a.volume:F2}, group '{(a.outputAudioMixerGroup != null ? a.outputAudioMixerGroup.name : "-")}', mute {a.mute}, object '{a.gameObject.name}'");
                } catch { }
            }
        }

        private static IEnumerator Run() {
            yield return new WaitForSeconds(10f);           // menu music swapped in and faded up
            var sm = SoundManager.Instance;
            var audio = AmongUs.Data.DataManager.Settings.Audio;
            float before = audio.MusicVolume;
            Log($"start: settings music {before:F2}, SoundManager.MusicVolume {SoundManager.MusicVolume:F2} dB?, "
                + $"MusicChannel {(sm.MusicChannel != null ? sm.MusicChannel.name : "NULL")}, musicMixer {(sm.musicMixer != null ? sm.musicMixer.name : "NULL")}, "
                + $"mixer {(sm.mixer != null ? sm.mixer.name : "NULL")}, listener volume {AudioListener.volume:F2}, paused {AudioListener.pause}, focus {Application.isFocused}");
            foreach (var m in Object.FindObjectsOfType<SoundManager>())
                Log($"  SoundManager '{m.name}' id {m.GetInstanceID()}, scene '{m.gameObject.scene.name}', mixer {(m.mixer != null ? "yes" : "NULL")}, musicMixer {(m.musicMixer != null ? "yes" : "NULL")}, is Instance {m == sm}");
            foreach (var mx in Resources.FindObjectsOfTypeAll(Il2CppInterop.Runtime.Il2CppType.Of<UnityEngine.Audio.AudioMixer>())) Log($"  AudioMixer asset '{mx.name}'");
            foreach (var g in Resources.FindObjectsOfTypeAll(Il2CppInterop.Runtime.Il2CppType.Of<UnityEngine.Audio.AudioMixerGroup>())) Log($"  AudioMixerGroup '{g.name}'");
            Sources();
            yield return Measure("as set");
            audio.MusicVolume = 0f;                        // what the options slider writes; fires OnMusicVolumeChanged
            yield return new WaitForSeconds(0.5f);
            Log($"after settings music 0: SoundManager.MusicVolume {SoundManager.MusicVolume:F2}");
            Sources();
            yield return Measure("music 0");
            audio.MusicVolume = before;
            yield return new WaitForSeconds(0.5f);
            Sources();
            yield return Measure("restored");
            Log("done");
            yield return new WaitForSeconds(1f);
            Application.Quit();
        }
    }
}
