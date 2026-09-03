// Useful TOR Stuff - Copyright (C) 2026 DaUnknown-0
// Licensed under GPL-3.0-or-later. See LICENSE for details.

/*
 * UTSRejoin - the way back into the lobby after a mod sync, or after any other reason the game is
 * suddenly gone.
 *
 * A downloaded DLL only takes effect after a restart (BepInEx loads plugins once, at startup), so
 * the mod-sync feature ends with the player having to quit the game. Without this, "sync the mods"
 * really means "leave, hunt for the lobby code you never wrote down, and hope the round has not
 * started". So before the restart we persist code + region + a timestamp, and the main menu offers
 * one button to walk straight back in.
 *
 * The same problem exists for a REAL crash - an Il2Cpp exception, Alt-F4, a dropped connection mid
 * round - and since 2026-08-16 it is covered by the same mechanism: a Harmony postfix below calls
 * RememberCurrentLobby() on every AmongUsClient.OnGameJoined, i.e. on every lobby join or create,
 * not only after a sync. So whatever kills the process afterwards - the restart, or the crash it
 * was trying to avoid - the main menu still finds a fresh entry to offer.
 *
 * TOR already has a related helper (LobbyScreenPatch.LobbyJoinBind: remember the last GameId,
 * rejoin on LShift), but it keeps the id in a static field that dies with the process, which is
 * exactly the moment we care about. That file is original source and is left untouched; this is a
 * separate, persisted path in our own mod.
 *
 * The entry EXPIRES after 30 minutes. A stale code is worse than none: hours later that lobby is
 * either gone or has become somebody else's round.
 */

using System;
using System.Globalization;
using BepInEx.Configuration;
using HarmonyLib;
using UnityEngine;
using UnityEngine.SceneManagement;
using UnityEngine.UI;

namespace UsefulTORStuff {

    public static class UTSRejoin {

        private static readonly TimeSpan MaxAge = TimeSpan.FromMinutes(30);

        internal static ConfigEntry<string> SavedCode;
        internal static ConfigEntry<string> SavedRegion;
        internal static ConfigEntry<string> SavedStamp;
        private static ConfigFile _config;

        public static void Bind(ConfigFile config) {
            _config = config;
            SavedCode = config.Bind("ModSync", "RejoinCode", "",
                "Lobby code remembered before a mod sync restart. Cleared once used or expired.");
            SavedRegion = config.Bind("ModSync", "RejoinRegion", "",
                "Server region of the remembered lobby.");
            SavedStamp = config.Bind("ModSync", "RejoinStamp", "",
                "UTC timestamp of the remembered lobby (round-trip format). Entries expire after 30 minutes.");
        }

        // Batches the three .Value writes below into a single disk save: BepInEx's ConfigFile
        // writes the WHOLE file on every .Value set by default (SaveOnConfigSet), so setting three
        // entries back to back would otherwise serialize and write the file three times for what is
        // conceptually one update. Temporarily disabling SaveOnConfigSet defers all three writes to
        // one explicit Save() call; the finally puts the flag back so every OTHER config entry in
        // the mod keeps its normal per-set save behavior.
        private static void BatchedSave(Action writes) {
            if (_config == null) { writes(); return; }
            bool prev = _config.SaveOnConfigSet;
            _config.SaveOnConfigSet = false;
            try {
                writes();
            } finally {
                _config.SaveOnConfigSet = prev;
            }
            _config.Save();
        }

        // Called after a successful sync (UTSModDownloader, right before the restart) and, since
        // 2026-08-16, on every AmongUsClient.OnGameJoined (see LobbyJoinPatch below) - so a real
        // crash mid-round leaves the same rejoin entry behind that a mod sync would have.
        public static void RememberCurrentLobby() {
            try {
                if (SavedCode == null || AmongUsClient.Instance == null) return;
                int gameId = AmongUsClient.Instance.GameId;
                if (gameId == 0) return;

                string code = InnerNet.GameCode.IntToGameName(gameId);
                if (string.IsNullOrEmpty(code)) return;

                string region = "";
                try { region = DestroyableSingleton<ServerManager>.Instance?.CurrentRegion?.Name ?? ""; }
                catch { }

                string stamp = DateTime.UtcNow.ToString("o", CultureInfo.InvariantCulture);
                BatchedSave(() => {
                    SavedCode.Value = code;
                    SavedRegion.Value = region;
                    SavedStamp.Value = stamp;
                });
                UsefulTORStuffPlugin.Logger?.LogInfo(
                    $"[ModSync] remembered lobby {code} ({region}) for the post-restart rejoin.");
            } catch (Exception ex) {
                UsefulTORStuffPlugin.Logger?.LogWarning($"[ModSync] could not remember the lobby: {ex.Message}");
            }
        }

        public static void Clear() {
            if (SavedCode == null) return;
            BatchedSave(() => {
                SavedCode.Value = "";
                SavedRegion.Value = "";
                SavedStamp.Value = "";
            });
        }

        // A remembered lobby that is still young enough to be worth offering.
        public static bool TryGetFresh(out string code, out string region) {
            code = null; region = null;
            if (SavedCode == null || string.IsNullOrWhiteSpace(SavedCode.Value)) return false;

            DateTime stamp;
            if (!DateTime.TryParse(SavedStamp.Value, CultureInfo.InvariantCulture,
                                   DateTimeStyles.RoundtripKind, out stamp)) return false;
            if (DateTime.UtcNow - stamp.ToUniversalTime() > MaxAge) {
                Clear();
                return false;
            }

            code = SavedCode.Value.Trim().ToUpperInvariant();
            region = SavedRegion.Value ?? "";
            return true;
        }

        // Switch back to the region the lobby lived on, if it is not the one already selected.
        // A region we cannot find is not fatal: the join is attempted anyway and simply fails to
        // find the code, which is the same thing the player would see typing it in by hand.
        private static void EnsureRegion(string regionName) {
            if (string.IsNullOrEmpty(regionName)) return;
            try {
                var sm = DestroyableSingleton<ServerManager>.Instance;
                if (sm == null) return;
                if (sm.CurrentRegion != null
                    && string.Equals(sm.CurrentRegion.Name, regionName, StringComparison.OrdinalIgnoreCase))
                    return;

                foreach (var region in sm.AvailableRegions) {
                    if (region == null) continue;
                    if (!string.Equals(region.Name, regionName, StringComparison.OrdinalIgnoreCase)) continue;
                    sm.SetRegion(region);
                    UsefulTORStuffPlugin.Logger?.LogInfo($"[ModSync] rejoin switched region to {regionName}.");
                    return;
                }
                UsefulTORStuffPlugin.Logger?.LogWarning(
                    $"[ModSync] rejoin region '{regionName}' not available - joining on the current region.");
            } catch (Exception ex) {
                UsefulTORStuffPlugin.Logger?.LogWarning($"[ModSync] rejoin region switch failed: {ex.Message}");
            }
        }

        // Runs the actual join on the given MonoBehaviour. Same call TOR's own LShift rejoin uses.
        public static void JoinRemembered(MonoBehaviour host) {
            string code, region;
            if (!TryGetFresh(out code, out region) || host == null) return;

            EnsureRegion(region);
            // One shot: whether or not the join succeeds, the code has been used. Leaving it around
            // would keep offering a button for a lobby that is most likely already running.
            Clear();

            try {
                int id = InnerNet.GameCode.GameNameToInt(code);
                if (id == 0) return;
                host.StartCoroutine(AmongUsClient.Instance.CoJoinOnlineGameFromCode(id));
            } catch (Exception ex) {
                UsefulTORStuffPlugin.Logger?.LogError($"[ModSync] rejoin failed: {ex}");
            }
        }

        // Fires on every join - a normal join, a rejoin, and creating a lobby as host - so the
        // remembered entry is refreshed continuously and a real crash (Il2Cpp exception, Alt-F4, a
        // dropped connection) leaves the same rejoin entry a mod sync would have. Runs once per
        // join, so unlike the tick-driven features in this mod no throttling is needed here.
        [HarmonyPatch(typeof(AmongUsClient), nameof(AmongUsClient.OnGameJoined))]
        static class LobbyJoinPatch {
            public static void Postfix() => RememberCurrentLobby();
        }
    }

    // Main-menu button, built the same way every updater builds its own: clone ExitGameButton and
    // move it via AspectPosition. Column x = 0.458 is shared with the update buttons
    // (0.124 / 0.21 / 0.30 / 0.38 / 0.39 / 0.48); 0.34 is the free slot between them.
    public class UTSRejoinButton : MonoBehaviour {
        public UTSRejoinButton(IntPtr ptr) : base(ptr) { }

        public void Start() {
            SceneManager.add_sceneLoaded((Action<Scene, LoadSceneMode>)OnSceneLoaded);
        }

        private void OnSceneLoaded(Scene scene, LoadSceneMode mode) {
            if (scene.name != "MainMenu") return;
            try { Build(); }
            catch (Exception ex) { UsefulTORStuffPlugin.Logger?.LogWarning($"[ModSync] rejoin button failed: {ex.Message}"); }
        }

        [HideFromIl2Cpp]
        private void Build() {
            string code, region;
            if (!UTSRejoin.TryGetFresh(out code, out region)) return;

            var template = GameObject.Find("ExitGameButton");
            if (!template) return;

            var button = Instantiate(template, null);
            button.name = "UTSRejoinButton";
            button.GetComponent<AspectPosition>().anchorPoint = new Vector2(0.458f, 0.34f);

            PassiveButton passive = button.GetComponent<PassiveButton>();
            passive.OnClick = new Button.ButtonClickedEvent();
            passive.OnClick.AddListener((Action)(() => {
                button.SetActive(false);
                UTSRejoin.JoinRemembered(this);
            }));

            var text = button.transform.GetComponentInChildren<TMPro.TMP_Text>();
            string label = UTSLocalization.Tr("uts.modsync.rejoin_button", code);
            // The label is applied through a one-frame lerp because Among Us rewrites the cloned
            // button's text on its own first frame (same trick the updaters use).
            StartCoroutine(Effects.Lerp(0.1f, (Action<float>)(p => text.SetText(label))));
            passive.OnMouseOut.AddListener((Action)(() => text.color = Color.cyan));
            passive.OnMouseOver.AddListener((Action)(() => text.color = Color.white));
            text.color = Color.cyan;
        }
    }
}
