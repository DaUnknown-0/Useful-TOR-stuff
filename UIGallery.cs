// Useful TOR Stuff - Copyright (C) 2026 DaUnknown-0
// Licensed under GPL-3.0-or-later. See LICENSE for details.

/*
 * UIGallery - autotest only: photographs every UTS screen so a redesign can be checked without a
 * second player. Config "Diagnostics / UI Gallery Test" (off by default, never ships on).
 *
 * Sequence: main menu -> Mod Manager, one shot per tab -> freeplay on The Skeld -> lobby menu,
 * newcomer shield, early-death shield (host panel and viewer), session statistics, mod sync panel,
 * password gate, kill feed, then UC's help panel and Role Control's F7 panel through reflection.
 * Shots land in <game>/UTSShots/gallery_NN_<name>.png; "[UIGallery] done" in the log ends the run.
 *
 * Freeplay has no GameStartManager, so while the gallery runs LobbyScreen.Exists and the menu
 * entries' visibility are forced to "lobby, host": the panels never query a real lobby object.
 */

using System;
using System.Collections;
using System.Reflection;
using BepInEx.Configuration;
using HarmonyLib;
using UnityEngine;

namespace UsefulTORStuff {

    public static class UIGallery {
        public static ConfigEntry<bool> Enabled;
        public static bool Active => Enabled != null && Enabled.Value;

        private static bool menuFired, gameFired;
        private static int shot;

        public static void Bind(ConfigFile config) {
            Enabled = config.Bind("Diagnostics", "UI Gallery Test", false,
                "Autotest only: opens every UTS panel in turn (main menu and freeplay) and saves screenshots to UTSShots.");
        }

        private static void Log(string s) => UsefulTORStuffPlugin.Logger?.LogInfo("[UIGallery] " + s);

        // Read back synchronously at the end of the frame: ScreenCapture.CaptureScreenshot turned out
        // to write each file only when the NEXT capture was requested, so every shot showed the
        // following step (2026-10-07).
        private static IEnumerator Shot(string tag) {
            yield return new WaitForEndOfFrame();
            try {
                string dir = System.IO.Path.Combine(BepInEx.Paths.GameRootPath, "UTSShots");
                System.IO.Directory.CreateDirectory(dir);
                string file = System.IO.Path.Combine(dir, $"gallery_{shot++:D2}_{tag}.png");
                var tex = new Texture2D(Screen.width, Screen.height, TextureFormat.RGB24, false);
                tex.ReadPixels(new Rect(0, 0, Screen.width, Screen.height), 0, 0);
                tex.Apply();
                System.IO.File.WriteAllBytes(file, ImageConversion.EncodeToPNG(tex));
                UnityEngine.Object.Destroy(tex);
                Log($"shot {tag} -> {file}");
            } catch (Exception e) { Log($"shot {tag} failed: {e.Message}"); }
        }

        // ---- main menu: the Mod Manager ----
        [HarmonyPatch(typeof(MainMenuManager), nameof(MainMenuManager.Start))]
        private static class MenuPatch {
            private static void Postfix(MainMenuManager __instance) {
                if (menuFired || !Active) return;
                menuFired = true;
                BepInEx.Unity.IL2CPP.Utils.MonoBehaviourExtensions.StartCoroutine(__instance, MenuRun());
            }
        }

        private static IEnumerator MenuRun() {
            float t0 = Time.time;
            while (true) {
                bool done = false;
                try { done = EOSManager.Instance != null && EOSManager.Instance.HasFinishedLoginFlow(); } catch { }
                if (done || Time.time - t0 > 45f) break;
                yield return null;
            }
            yield return new WaitForSeconds(2f);
            var mm = ModManagerUI.Instance;
            if (mm != null) {
                mm.Show();
                yield return new WaitForSeconds(1.5f);
                yield return Shot("modmanager_installed");
                mm.DiagShowTab(1);
                yield return new WaitForSeconds(1f);
                yield return Shot("modmanager_allmods");
                // a sample pack and a pending restart, so the modpack row and the restart button show
                var sample = UTSModpacks.FromCurrent("Gallery pack");
                UTSModpacks.Add(sample);
                UTSRestart.Mark();
                mm.DiagShowTab(2);
                yield return new WaitForSeconds(1f);
                yield return Shot("modmanager_modpacks");
                mm.DiagRename(sample);
                yield return new WaitForSeconds(1f);
                yield return Shot("modmanager_rename");
                mm.DiagRename(null);
                UTSModpacks.Delete(sample);
                mm.Hide();
                yield return new WaitForSeconds(0.5f);
            } else Log("no ModManagerUI");

            // freeplay on The Skeld (same route as SubmergedSelfTest)
            try {
                var popover = UnityEngine.Object.FindObjectOfType<FreeplayPopover>(true);
                if (popover == null) { Log("no FreeplayPopover"); yield break; }
                for (var t = popover.transform; t != null; t = t.parent) if (!t.gameObject.activeSelf) t.gameObject.SetActive(true);
                popover.Show();
                popover.PlayMap((MapNames)0);
                Log("freeplay started");
            } catch (Exception e) { Log("freeplay start failed: " + e.Message); }
        }

        // ---- freeplay: the lobby panels ----
        [HarmonyPatch(typeof(HudManager), nameof(HudManager.Update))]
        private static class GamePatch {
            private static void Postfix(HudManager __instance) {
                if (gameFired || !Active) return;
                var ac = AmongUsClient.Instance;
                if (ac == null || ac.NetworkMode != NetworkModes.FreePlay || ShipStatus.Instance == null || PlayerControl.LocalPlayer == null) return;
                gameFired = true;
                BepInEx.Unity.IL2CPP.Utils.MonoBehaviourExtensions.StartCoroutine(__instance, GameRun());
            }
        }

        private static IEnumerator GameRun() {
            yield return new WaitForSeconds(6f);
            Log("game run begins");

            // the corner tile and its menu
            var menu = LobbyMenuUI.Instance;
            yield return new WaitForSeconds(1f);
            if (menu != null) { menu.Toggle(); yield return new WaitForSeconds(1f); yield return Shot("lobby_menu"); menu.Close(); }

            var nc = NewcomerShieldUI.Instance;
            if (nc != null) { nc.Toggle(); yield return new WaitForSeconds(1f); yield return Shot("newcomer_shield"); nc.Close(); }

            var ed = EarlyDeathShieldUI.Instance;
            if (ed != null) {
                ed.Toggle(); yield return new WaitForSeconds(1f); yield return Shot("earlydeath_host"); ed.Close();
                EarlyDeathShield.DiagSampleStats();
                ed.DiagOpenViewer(); yield return new WaitForSeconds(1f); yield return Shot("earlydeath_viewer"); ed.Close();
            }

            var st = SessionStatsUI.Instance;
            if (st != null) {
                SessionStats.DiagSample();
                st.Toggle(); yield return new WaitForSeconds(1.5f); yield return Shot("session_stats");
                st.DiagShowTitles(); yield return new WaitForSeconds(1f); yield return Shot("session_titles"); st.Close();
            }

            var ms = UTSModSyncUI.Instance;
            if (ms != null) { ms.Open(); yield return new WaitForSeconds(1f); yield return Shot("mod_sync"); ms.Close(); }

            var gate = LobbyPasswordGate.Instance;
            if (gate != null) { gate.ShowPanel(); yield return new WaitForSeconds(1f); yield return Shot("password_gate"); gate.HidePanel(); }

            GhostKillFeed.DiagSample();
            yield return new WaitForSeconds(1f);
            yield return Shot("kill_feed");

            // other mods' overlays, when present
            if (Invoke("UnknownsCollection.UCHelpMenu, UnknownsCollection", "DiagToggle")) {
                yield return new WaitForSeconds(1.5f); yield return Shot("uc_help");
                Invoke("UnknownsCollection.UCHelpMenu, UnknownsCollection", "DiagToggle");
            }
            if (Invoke("ForceImpostorMod.RoleControlUI, ForceImpostorMod", "DiagToggle")) {
                yield return new WaitForSeconds(1.5f); yield return Shot("role_control");
                Invoke("ForceImpostorMod.RoleControlUI, ForceImpostorMod", "DiagToggle");
            }
            // UC's colour grant: player list, hex entry, the question the target sees
            try { AppDomain.CurrentDomain.SetData("UTS.UIGallery.Active", true); } catch { }
            string[] grant = { "colorgrant_list", "colorgrant_hex", "colorgrant_prompt" };
            for (int s = 0; s < grant.Length; s++) {
                if (!Invoke("UnknownsCollection.UCColorGrantUI, UnknownsCollection", "DiagShow", s)) break;
                yield return new WaitForSeconds(1.2f); yield return Shot(grant[s]);
            }
            Invoke("UnknownsCollection.UCColorGrantUI, UnknownsCollection", "DiagShow", -1);
            yield return new WaitForSeconds(0.5f);
            Log("done");
        }

        private static bool Invoke(string typeName, string method, params object[] args) {
            try {
                var t = Type.GetType(typeName);
                var m = t?.GetMethod(method, BindingFlags.Public | BindingFlags.Static);
                if (m == null) { Log($"{typeName}.{method} not found"); return false; }
                m.Invoke(null, args.Length == 0 ? null : args);
                return true;
            } catch (Exception e) { Log($"{typeName}.{method} failed: {e.Message}"); return false; }
        }
    }
}
