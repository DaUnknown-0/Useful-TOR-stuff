// Useful TOR Stuff - Copyright (C) 2026 DaUnknown-0
// Licensed under GPL-3.0-or-later. See LICENSE for details.
// Based on The Other Roles (https://github.com/TheOtherRolesAU/TheOtherRoles), GPL-3.0.

using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;
using BepInEx;
using BepInEx.Unity.IL2CPP.Utils;
using Il2CppInterop.Runtime.Attributes;
using UnityEngine;
using UnityEngine.Networking;
using UnityEngine.SceneManagement;
using UnityEngine.UI;
using AmongUs.Data;
using Assets.InnerNet;
using Twitch;

namespace UsefulTORStuff {
    // Self-updater that checks the GitHub releases of this repo and offers an in-game update
    // button on the main menu. Mirrors TOR's own ModUpdater flow but uses its own GithubRelease
    // DTOs so this plugin needs no compile-time reference to TheOtherRoles.
    public class UsefulTORStuffUpdater : MonoBehaviour {
        public const string RepositoryOwner = "DaUnknown-0";
        public const string RepositoryName = "Useful-TOR-stuff";
        public const string PluginAssetName = "UsefulTORStuff.dll";

        public static UsefulTORStuffUpdater Instance { get; private set; }

        public UsefulTORStuffUpdater(IntPtr ptr) : base(ptr) { }

        private bool _busy;
        private bool _showPopUp = true;
        // Einmal-Flag: die gesammelte Update-Ankündigung (Manager-Modus) nur einmal pro Sitzung zeigen.
        private bool _showConsolidatedAnnouncement = true;
        public List<GithubRelease> Releases;

        // Download-Zustand für den Mod Manager. 0 = idle, 1 = downloading,
        // 2 = success (restart required), 3 = error. Lebt in der Instanz, damit das
        // Mod-Manager-UI ihn über Schließen/Öffnen hinweg abfragen kann.
        private int _updateState;
        private float _updateProgress;

        // True sobald der GitHub-Release-Check abgeschlossen ist (Erfolg oder Fehler). Vom
        // Mod Manager abgefragt, um die gesammelte Update-Ankündigung erst nach allen Checks zu zeigen.
        private bool _checkCompleted;

        public void Awake() {
            if (Instance) Destroy(Instance);
            Instance = this;
            // AUDIT-2026-08-23 (L-21): guarded. A .old still locked by a virus scanner, or a
            // plugin folder this process cannot enumerate, threw straight out of Awake - which
            // aborts the component's initialisation, so the updater silently did not exist for the
            // rest of the session. Cleaning up a leftover file is not worth that.
            try {
                foreach (var file in Directory.GetFiles(Paths.PluginPath, PluginAssetName + ".old"))
                    try { File.Delete(file); } catch { }
            } catch (Exception e) {
                UsefulTORStuffPlugin.Logger?.LogWarning($"[UTS] Could not clean up old plugin files: {e.Message}");
            }
        }

        private void Start() {
            if (_busy) return;
            this.StartCoroutine(CoCheckForUpdate());
            SceneManager.add_sceneLoaded((Action<Scene, LoadSceneMode>)OnSceneLoaded);
        }

        [HideFromIl2Cpp]
        public void StartDownloadRelease(GithubRelease release, bool managerMode = false) {
            if (_busy) return;
            this.StartCoroutine(CoDownloadRelease(release, managerMode));
        }

        // Vom Mod Manager beim Öffnen ausgelöster erneuter GitHub-Release-Check (gedrosselt
        // auf 1×/Minute durch ModManagerRegistry.MaybeCheckForUpdates).
        [HideFromIl2Cpp]
        public void TriggerCheckFromManager() {
            if (_busy) return;          // läuft bereits ein Check/Download — nicht doppelt starten
            _checkCompleted = false;    // erlaubt UI/Ankündigung, den laufenden Re-Check zu erkennen
            this.StartCoroutine(CoCheckForUpdate());
        }

        // Reflection-/direkt-aufrufbare Getter für das Mod-Manager-UI.
        [HideFromIl2Cpp]
        public int GetUpdateState() => _updateState;

        [HideFromIl2Cpp]
        public float GetUpdateProgress() => _updateProgress;

        [HideFromIl2Cpp]
        public bool GetCheckCompleted() => _checkCompleted;

        // True when the release list was successfully fetched. The Mod Manager uses this to show
        // "check unavailable" instead of a misleading "up to date" when the GitHub call failed/rate-limited.
        [HideFromIl2Cpp]
        public bool ReleasesLoaded() => Releases != null && Releases.Count > 0;

        [HideFromIl2Cpp]
        private IEnumerator CoCheckForUpdate() {
            _busy = true;
            var www = new UnityWebRequest();
            www.SetMethod(UnityWebRequest.UnityWebRequestMethod.Get);
            www.SetUrl($"https://api.github.com/repos/{RepositoryOwner}/{RepositoryName}/releases");
            // GitHub weist UA-lose Clients ab (P2.8) — eindeutigen User-Agent setzen.
            www.SetRequestHeader("User-Agent", $"UsefulTORStuff/{UsefulTORStuffPlugin.PluginVersion}");
            www.downloadHandler = new DownloadHandlerBuffer();
            var operation = www.SendWebRequest();

            while (!operation.isDone) {
                yield return new WaitForEndOfFrame();
            }

            if (www.isNetworkError || www.isHttpError) {
                www.downloadHandler.Dispose();
                www.Dispose();
                _checkCompleted = true;
                _busy = false;
                yield break;
            }

            // GitHub liefert bei Rate-Limit (403) oder Fehlern ein JSON-OBJEKT statt eines
            // Arrays; Deserialize/Sort dürfen die Coroutine nicht killen, sonst bliebe _busy
            // für die ganze Session true und alle weiteren Checks/Downloads wären blockiert
            // (P0.2). try/catch ist hier möglich, weil dieser Block kein yield enthält.
            try {
                Releases = JsonSerializer.Deserialize<List<GithubRelease>>(www.downloadHandler.text);
                if (Releases != null) Releases.Sort(SortReleases);
            } catch (Exception ex) {
                UsefulTORStuffPlugin.Logger?.LogWarning($"TOR - Forgotten Fixes update check: failed to parse GitHub releases ({ex.Message}). Treating as 'no update'.");
                // Releases unverändert lassen (ggf. null) — überall als "kein Update" behandelt.
            } finally {
                www.downloadHandler.Dispose();
                www.Dispose();
                _checkCompleted = true;
                _busy = false;
            }
        }

        [HideFromIl2Cpp]
        private IEnumerator CoDownloadRelease(GithubRelease release, bool managerMode) {
            _busy = true;
            _updateState = 1;
            _updateProgress = 0f;

            // Im Manager-Modus wird kein Among-Us-TwitchPopup erzeugt; der Mod Manager zeigt
            // Fortschritt/Status selbst über GetUpdateState()/GetUpdateProgress() an.
            GenericPopup popup = null;
            GameObject button = null;
            if (!managerMode) {
                popup = Instantiate(TwitchManager.Instance.TwitchPopup);
                popup.TextAreaTMP.fontSize *= 0.7f;
                popup.TextAreaTMP.enableAutoSizing = false;

                popup.Show();

                button = popup.transform.GetChild(2).gameObject;
                button.SetActive(false);
                popup.TextAreaTMP.text = UTSLocalization.Tr("uts.updater.updating_wait");
            }

            var asset = release.Assets.Find(FilterPluginAsset);
            if (asset == null) {
                UsefulTORStuffPlugin.Logger?.LogError(
                    $"[Updater] update failed: release \"{release.Version}\" has no \"{PluginAssetName}\" asset.");
                _updateState = 3;
                if (!managerMode) {
                    popup.TextAreaTMP.text = UTSLocalization.Tr("uts.updater.update_failed");
                    button.SetActive(true);
                }
                _busy = false;
                yield break;
            }
            var www = new UnityWebRequest();
            www.SetMethod(UnityWebRequest.UnityWebRequestMethod.Get);
            www.SetUrl(asset.DownloadUrl);
            www.downloadHandler = new DownloadHandlerBuffer();
            var operation = www.SendWebRequest();

            while (!operation.isDone) {
                _updateProgress = www.downloadProgress;
                if (!managerMode) {
                    int stars = Mathf.CeilToInt(www.downloadProgress * 10);
                    string bar = new String((char)0x25A0, stars) + new String((char)0x25A1, 10 - stars);
                    popup.TextAreaTMP.text = UTSLocalization.Tr("uts.updater.downloading_progress", bar);
                }
                yield return new WaitForEndOfFrame();
            }

            if (www.isNetworkError || www.isHttpError) {
                www.downloadHandler.Dispose();
                www.Dispose();
                _updateState = 3;
                if (!managerMode) {
                    popup.TextAreaTMP.text = UTSLocalization.Tr("uts.updater.update_failed");
                    button.SetActive(true);
                }
                _busy = false;
                yield break;
            }
            if (!managerMode) {
                popup.TextAreaTMP.text = UTSLocalization.Tr("uts.updater.download_complete");
            }

            var filePath = Path.Combine(Paths.PluginPath, asset.Name);

            // Move the working DLL aside before writing the download, so a write failure below can
            // roll back to it instead of leaving the plugin folder without a usable Useful TOR Stuff
            // at all. Guarded in its own try/catch: a locked .old (virus scanner, another process)
            // must not silently proceed and overwrite the still-working plugin file (mirrors
            // Nightfall/NightfallUpdater.cs and UnknownsCollection/UnknownsCollectionUpdater.cs).
            var moved = false;
            try {
                if (File.Exists(filePath + ".old")) File.Delete(filePath + ".old");
                if (File.Exists(filePath)) File.Move(filePath, filePath + ".old");
                moved = true;
            } catch (Exception e) {
                UsefulTORStuffPlugin.Logger?.LogError(
                    $"[Updater] update failed: could not move the old plugin file aside ({e.Message}).");
                www.downloadHandler.Dispose();
                www.Dispose();
                _updateState = 3;
                if (!managerMode) {
                    popup.TextAreaTMP.text = UTSLocalization.Tr("uts.updater.update_failed");
                    button.SetActive(true);
                }
                _busy = false;
                yield break;
            }

            System.Threading.Tasks.Task persistTask = null;
            var hasError = false;
            try {
                persistTask = File.WriteAllBytesAsync(filePath, www.downloadHandler.data);
            } catch (Exception e) {
                UsefulTORStuffPlugin.Logger?.LogError($"[Updater] update failed: could not start writing the new plugin file ({e.Message}).");
                hasError = true;
                persistTask = null;
            }
            while (persistTask != null && !persistTask.IsCompleted) {
                if (persistTask.Exception != null) {
                    hasError = true;
                    break;
                }

                yield return new WaitForEndOfFrame();
            }
            // AUDIT (mirrors UnknownsCollectionUpdater.cs): Task.IsCompleted is also true for
            // Faulted/Canceled, so a task that already failed by the very first check never enters
            // the loop above and hasError stays false. Re-check after the loop so a write failure is
            // never reported as a successful update.
            if (!hasError && persistTask != null && !persistTask.IsCompletedSuccessfully) hasError = true;

            www.downloadHandler.Dispose();
            www.Dispose();

            if (!hasError) {
                _updateState = 2;
                if (!managerMode) {
                    popup.TextAreaTMP.text = UTSLocalization.Tr("uts.updater.update_success");
                }
            } else {
                // ROLL BACK: the working DLL was moved aside to .old before the download was
                // written, so a failed write used to leave the plugin folder with no usable Useful
                // TOR Stuff at all (a half-written file, or nothing) - the mod simply stopped
                // loading next start, with the only trace an update popup that said it had failed.
                // Putting the old file back makes a failed update a no-op again (same rollback shape
                // as UTSModDownloader.cs's per-mod sync jobs).
                try {
                    if (moved && File.Exists(filePath + ".old")) {
                        if (File.Exists(filePath)) File.Delete(filePath);
                        File.Move(filePath + ".old", filePath);
                        UsefulTORStuffPlugin.Logger?.LogWarning("[Updater] update failed - restored the previous plugin file.");
                    } else if (File.Exists(filePath)) {
                        // No .old to restore from (nothing was moved aside, or it is already gone) -
                        // leaving the half-written download in place would be picked up as the plugin
                        // DLL on next start. Delete it instead so the folder is left without Useful
                        // TOR Stuff rather than with a broken one.
                        try { File.Delete(filePath); } catch { }
                    }
                } catch (Exception e) {
                    UsefulTORStuffPlugin.Logger?.LogError(
                        $"[Updater] update failed AND the previous plugin file could not be restored ({e.Message}). "
                        + $"Reinstall Useful TOR Stuff manually: the working DLL is next to it, named \"{PluginAssetName}.old\".");
                }
                _updateState = 3;
                if (!managerMode) {
                    popup.TextAreaTMP.text = UTSLocalization.Tr("uts.updater.update_failed");
                }
            }
            if (!managerMode) button.SetActive(true);
            _busy = false;
        }

        [HideFromIl2Cpp]
        private static bool FilterPluginAsset(GithubAsset asset) {
            return asset.Name == PluginAssetName;
        }

        [HideFromIl2Cpp]
        private static int SortReleases(GithubRelease a, GithubRelease b) {
            if (a.IsNewer(b.Version)) return -1;
            if (b.IsNewer(a.Version)) return 1;
            return 0;
        }

        private void OnSceneLoaded(Scene scene, LoadSceneMode mode) {
            if (scene.name != "MainMenu") return;

            // Wenn Mod-Manager aktiviert ist, keine eigenen Update-Buttons anzeigen. Stattdessen
            // zeigt UsefulTORStuff (als Manager-Besitzer) einmalig eine gesammelte Ankündigung mit
            // allen Mods, die ein Update brauchen. Unabhängig von den eigenen Releases.
            if (ModManagerRegistry.IsModManagerEnabled()) {
                if (_showConsolidatedAnnouncement) {
                    _showConsolidatedAnnouncement = false;
                    this.StartCoroutine(CoShowConsolidatedUpdateAnnouncement());
                }
                return;
            }

            if (_busy || Releases == null) return;

            var latestRelease = UpdateTarget();
            if (latestRelease == null || !IsActualUpdate(latestRelease.Version, UsefulTORStuffPlugin.Version) || !latestRelease.Assets.Any(FilterPluginAsset))
                return;

            var template = GameObject.Find("ExitGameButton");
            if (!template) return;

            var button = Instantiate(template, null);
            // Stacked above TOR's updater (0.124), the Chance updater (0.21) and the Host Fix
            // updater (0.30) to avoid overlap.
            button.GetComponent<AspectPosition>().anchorPoint = new Vector2(0.458f, 0.39f);

            PassiveButton passiveButton = button.GetComponent<PassiveButton>();
            passiveButton.OnClick = new Button.ButtonClickedEvent();
            passiveButton.OnClick.AddListener((Action)(() => {
                StartDownloadRelease(latestRelease);
                button.SetActive(false);
            }));

            var text = button.transform.GetComponentInChildren<TMPro.TMP_Text>();
            string t = UTSLocalization.Tr("uts.updater.button_label");
            StartCoroutine(Effects.Lerp(0.1f, (Action<float>)(p => text.SetText(t))));
            passiveButton.OnMouseOut.AddListener((Action)(() => text.color = Color.green));
            passiveButton.OnMouseOver.AddListener((Action)(() => text.color = Color.white));
            text.color = Color.green;

            if (_showPopUp) {
                var announcement = UTSLocalization.Tr("uts.updater.announcement_new_update", latestRelease.Tag, latestRelease.Description);
                var mgr = FindObjectOfType<MainMenuManager>(true);
                if (mgr != null)
                    mgr.StartCoroutine(CoShowAnnouncement(announcement, shortTitle: UTSLocalization.Tr("uts.updater.announcement_short_title"), date: latestRelease.PublishedAt));
            }
            _showPopUp = false;
        }

        // Manager-Modus: zeigt einmalig eine gesammelte Ankündigung mit allen Mods, die ein Update
        // brauchen — statt der einzelnen per-Mod-Ankündigungen (die im Manager-Modus unterdrückt sind).
        [HideFromIl2Cpp]
        private IEnumerator CoShowConsolidatedUpdateAnnouncement() {
            var mods = ModManagerRegistry.GetAllMods();

            // Warte bis alle Mods ihren GitHub-Release-Check abgeschlossen haben (oder Timeout),
            // damit kein Update verpasst wird, dessen Check noch lief.
            for (float t = 20f; t > 0f; t -= 0.25f) {
                bool allDone = true;
                foreach (var m in mods) {
                    // Deaktivierte Mods laufen nicht und haben keinen Update-Check — überspringen,
                    // sonst würde die Schleife bis zum Timeout warten.
                    if (!m.RuntimeEnabled) continue;
                    bool done = false;
                    try { done = m.GetCheckCompleted?.Invoke() ?? true; } catch { }
                    if (!done) { allDone = false; break; }
                }
                if (allDone) break;
                yield return new WaitForSeconds(0.25f);
            }

            // Sammle alle laufenden Mods mit verfügbarem Update.
            var names = new List<string>();
            foreach (var m in mods) {
                bool has = false;
                try { has = m.RuntimeEnabled && (m.HasUpdate?.Invoke() ?? false); } catch { }
                if (has) names.Add(UTSLocalization.Tr("uts.updater.mod_list_line", m.Name, m.Version));
            }

            if (names.Count == 0) yield break;

            string announcement = UTSLocalization.Tr("uts.updater.consolidated_announcement",
                names.Count == 1 ? "" : "s", string.Join("\n", names));

            yield return this.StartCoroutine(CoShowAnnouncement(
                announcement, shortTitle: UTSLocalization.Tr("uts.updater.consolidated_short_title"),
                title: UTSLocalization.Tr("uts.updater.consolidated_title")));
        }

        [HideFromIl2Cpp]
        public IEnumerator CoShowAnnouncement(string announcement, bool show = true, string shortTitle = null, string title = "", string date = "") {
            // Default kept as null (not a Tr() call — default parameter values must be compile-time
            // constants) and resolved here so it still picks up the current language.
            if (shortTitle == null) shortTitle = UTSLocalization.Tr("uts.updater.announcement_short_title");
            // Stagger behind other mods so the other update popups appear first.
            yield return new WaitForSeconds(2f);
            // Show last of all mods. The other updaters wait for a clear popup field and then
            // settle 0.2 s before instantiating, so when a shared prior popup (e.g. Chance) closes
            // they all break on the same frame and race. To guarantee Useful TOR Stuff comes AFTER
            // Host Fix, settle longer (0.6 s) and re-check: if a popup (Host Fix's) appeared during
            // the settle, loop and wait it out again.
            for (float guard = 30f; guard > 0f; guard -= 0.6f) {
                // Wait until no announcement popup is currently visible (up to 30 s).
                for (float t = 30f; t > 0f; t -= 0.25f) {
                    if (UnityEngine.Object.FindObjectOfType<AnnouncementPopUp>() == null) break;
                    yield return new WaitForSeconds(0.25f);
                }
                // Settle longer than the other updaters (0.2 s) so they win a simultaneous clear.
                yield return new WaitForSeconds(0.6f);
                // Nothing slipped in during the settle — safe to show now.
                if (UnityEngine.Object.FindObjectOfType<AnnouncementPopUp>() == null) break;
            }

            var mgr = FindObjectOfType<MainMenuManager>(true);
            var popUpTemplate = UnityEngine.Object.FindObjectOfType<AnnouncementPopUp>(true);
            // Ohne Template würde Instantiate(null) sofort werfen; ohne Manager würde
            // mgr.StartCoroutine(...) weiter unten ein NullRef auslösen (P0.1).
            if (popUpTemplate == null || mgr == null) {
                yield break;
            }
            var popUp = UnityEngine.Object.Instantiate(popUpTemplate);

            popUp.gameObject.SetActive(true);

            Announcement optimizedAnnouncement = new() {
                Id = "usefulTORStuffAnnouncement",
                Language = 0,
                Number = 6972,
                Title = title == "" ? UTSLocalization.Tr("uts.updater.announcement_default_title") : title,
                ShortTitle = shortTitle,
                SubTitle = "",
                PinState = false,
                Date = date == "" ? DateTime.Now.Date.ToString() : date,
                Text = announcement,
            };
            mgr.StartCoroutine(Effects.Lerp(0.1f, new Action<float>((p) => {
                if (p == 1) {
                    var backup = DataManager.Player.Announcements.allAnnouncements;
                    DataManager.Player.Announcements.allAnnouncements = new();
                    popUp.Init(false);
                    DataManager.Player.Announcements.SetAnnouncements(new Announcement[] { optimizedAnnouncement });
                    popUp.CreateAnnouncementList();
                    popUp.UpdateAnnouncementText(optimizedAnnouncement.Number);
                    popUp.visibleAnnouncements[0].PassiveButton.OnClick.RemoveAllListeners();
                    DataManager.Player.Announcements.allAnnouncements = backup;
                }
            })));
        }

        // ---- Channel awareness + semantic version comparison ----
        // Semantic comparison where a STABLE vX.Y.Z SUPERSEDES its prereleases vX.Y.Z.W (unlike
        // System.Version, which wrongly orders 1.0.0.4 > 1.0.0). >0 means a is newer than b: compare the
        // X.Y.Z base first; on a tie the finalized stable beats any prerelease, and among prereleases the
        // higher 4th part wins.
        [HideFromIl2Cpp]
        public static int SemCompare(Version a, Version b) {
            int c = new Version(a.Major, System.Math.Max(0, a.Minor), System.Math.Max(0, a.Build)).CompareTo(new Version(b.Major, System.Math.Max(0, b.Minor), System.Math.Max(0, b.Build)));
            if (c != 0) return c;
            bool aPre = a.Revision > 0, bPre = b.Revision > 0;
            if (aPre && bPre) return a.Revision.CompareTo(b.Revision);
            if (aPre == bPre) return 0;
            return aPre ? -1 : 1; // prerelease older than the finalized stable of the same base
        }

        // True when `target` is a version the user should actually install (not just "semantically newer").
        // On the test channel, stable vX.Y.Z for a user already on prerelease vX.Y.Z.W is a channel switch,
        // not an update — the base version did not advance. Channel switches go through TriggerChannelSwitch.
        [HideFromIl2Cpp]
        private static bool IsActualUpdate(Version target, Version current) {
            if (SemCompare(target, current) <= 0) return false;
            if (VersionDisplay.ShowTestVersions() && current.Revision > 0 && target.Revision <= 0) {
                var tBase = new Version(target.Major, System.Math.Max(0, target.Minor), System.Math.Max(0, target.Build));
                var cBase = new Version(current.Major, System.Math.Max(0, current.Minor), System.Math.Max(0, current.Build));
                if (tBase.CompareTo(cBase) <= 0) return false;
            }
            return true;
        }

        // Channel from the TAG FORMAT: stable = vX.Y.Z (Version.Revision <= 0), test = vX.Y.Z.W (>0).
        [HideFromIl2Cpp]
        public GithubRelease LatestInChannel(bool stable) {
            if (Releases == null) return null;
            foreach (var r in Releases) {
                if (r == null || r.Draft) continue;
                int rev;
                try { rev = r.Version.Revision; } catch { continue; }
                bool isTest = rev > 0;
                if (stable == isTest) continue;            // wrong channel
                if (r.Assets != null && r.Assets.Any(FilterPluginAsset)) return r;
            }
            return null;
        }

        [HideFromIl2Cpp]
        public bool HasChannelRelease(bool stable) => LatestInChannel(stable) != null;

        // The update target follows the shared "show test versions" toggle. OFF -> newest STABLE only.
        // ON -> newest PRERELEASE when its base is >= latest stable base (test channel target); only use
        // stable when stable base is strictly higher (genuine new stable beyond any prerelease).
        [HideFromIl2Cpp]
        public GithubRelease UpdateTarget() {
            if (Releases == null) return null;
            var stable = LatestInChannel(true);
            if (!VersionDisplay.ShowTestVersions()) return stable;
            var pre = LatestInChannel(false);
            if (pre == null) return stable;
            if (stable == null) return pre;
            var stableBase = new Version(stable.Version.Major, System.Math.Max(0, stable.Version.Minor), System.Math.Max(0, stable.Version.Build));
            var preBase = new Version(pre.Version.Major, System.Math.Max(0, pre.Version.Minor), System.Math.Max(0, pre.Version.Build));
            return stableBase.CompareTo(preBase) > 0 ? stable : pre;
        }

        // Callback-Methoden für ModManagerRegistry: Prüft ob ein Update verfügbar ist.
        [HideFromIl2Cpp]
        public bool HasUpdate() {
            var t = UpdateTarget();
            return t != null && t.Assets.Any(FilterPluginAsset)
                && IsActualUpdate(t.Version, UsefulTORStuffPlugin.Version);
        }

        // Roh-Release-Notes (GitHub-`body`) der Ziel-Version (aus dem bereits geladenen JSON).
        [HideFromIl2Cpp]
        public string GetReleaseNotes() => UpdateTarget()?.Description ?? "";

        // Callback-Methode für ModManagerRegistry: Startet den Update-Download.
        [HideFromIl2Cpp]
        public void TriggerUpdateFromManager() {
            var t = UpdateTarget();
            if (t != null && t.Assets.Any(FilterPluginAsset)
                && IsActualUpdate(t.Version, UsefulTORStuffPlugin.Version))
                StartDownloadRelease(t, managerMode: true);
        }

        // Force-install the latest release of the given channel (deliberate channel switch, may be an
        // up- OR downgrade). Only downloads if it is REALLY a different version than the running build.
        [HideFromIl2Cpp]
        public void TriggerChannelSwitch(bool stable) {
            var r = LatestInChannel(stable);
            if (r != null && SemCompare(r.Version, UsefulTORStuffPlugin.Version) != 0)
                StartDownloadRelease(r, managerMode: true);
        }
    }

    // Minimal DTOs matching the GitHub Releases API JSON. Kept local so this plugin needs no
    // compile-time reference to TheOtherRoles.
    public class GithubRelease {
        [JsonPropertyName("id")]
        public int Id { get; set; }

        [JsonPropertyName("tag_name")]
        public string Tag { get; set; }

        [JsonPropertyName("name")]
        public string Name { get; set; }

        [JsonPropertyName("draft")]
        public bool Draft { get; set; }

        [JsonPropertyName("prerelease")]
        public bool Prerelease { get; set; }

        [JsonPropertyName("created_at")]
        public string CreatedAt { get; set; }

        [JsonPropertyName("published_at")]
        public string PublishedAt { get; set; }

        [JsonPropertyName("body")]
        public string Description { get; set; }

        [JsonPropertyName("assets")]
        public List<GithubAsset> Assets { get; set; }

        // TryParse, not Parse (AUDIT-2026-08-23, L-22). Tag is whatever text the GitHub API
        // returned, and a release tagged anything that is not "vX.Y[.Z[.W]]" - a name, a date, a
        // typo - made this property THROW. The sort comparison reads it for every pair, so one bad
        // tag anywhere in the feed took down the whole comparison and left the release list in
        // arbitrary order, from which "the newest release" is then picked. A tag that cannot be
        // read is treated as version zero instead: it sorts last, IsNewer is false for it, and it
        // is simply never offered as an update.
        public Version Version =>
            Version.TryParse((Tag ?? string.Empty).Replace("v", string.Empty), out var v) ? v : new Version(0, 0, 0, 0);

        public bool IsNewer(Version version) {
            return Version > version;
        }
    }

    public class GithubAsset {
        [JsonPropertyName("url")]
        public string Url { get; set; }

        [JsonPropertyName("id")]
        public int Id { get; set; }

        [JsonPropertyName("name")]
        public string Name { get; set; }

        [JsonPropertyName("size")]
        public int Size { get; set; }

        [JsonPropertyName("browser_download_url")]
        public string DownloadUrl { get; set; }
    }
}
