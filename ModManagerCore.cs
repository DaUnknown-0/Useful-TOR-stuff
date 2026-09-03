// Useful TOR Stuff - Copyright (C) 2026 DaUnknown-0
// Licensed under GPL-3.0-or-later. See LICENSE for details.

using System;
using System.Collections.Generic;
using BepInEx.Configuration;
using UnityEngine;

namespace UsefulTORStuff
{
    // Mod-Informationen für die Registry (nur innerhalb UsefulTORStuff verwendet).
    public class ModInfo
    {
        public string Guid;
        public string Name;
        public Version Version;
        public string RepositoryOwner;
        public string RepositoryName;
        public Color ButtonColor;
        public Func<bool> HasUpdate;
        public Action TriggerUpdate;
        // F2: Liefert die rohen Release-Notes (GitHub-`body`) der neuesten Version, oder null wenn
        // der Updater diese Methode (noch) nicht hat (ältere installierte Version → Notes werden
        // dann im UI ausgeblendet statt zu scheitern).
        public Func<string> GetReleaseNotes;
        // Stößt einen erneuten GitHub-Release-Check an (beim Öffnen des Mod Managers).
        public Action TriggerCheck;
        // Test/Stable-Kanalwechsel (für den "Testversionen anzeigen"-Schalter). HasChannelRelease(true)
        // = es gibt einen Stable-Release (vX.Y.Z), (false) = es gibt einen Test-Release (vX.Y.Z.W).
        // TriggerChannelSwitch(stable) lädt den neusten Release des Kanals erzwungen (auch als Downgrade).
        public Func<bool, bool> HasChannelRelease;
        public Action<bool> TriggerChannelSwitch;
        // True when the mod's updater successfully fetched its GitHub release list. Lets the UI show
        // "check unavailable" instead of a misleading "up to date" when the check failed/rate-limited.
        public Func<bool> ReleasesLoaded;
        // Download-Zustand für die Mod-Manager-Anzeige.
        // GetUpdateState: 0 = idle, 1 = downloading, 2 = success (restart), 3 = error.
        public Func<int> GetUpdateState;
        public Func<float> GetUpdateProgress;
        // True sobald der GitHub-Release-Check dieses Mods abgeschlossen ist (Erfolg oder Fehler).
        public Func<bool> GetCheckCompleted;
        public ConfigEntry<bool> Enabled;
        // Laufzeit-Zustand: war der Mod beim Spielstart aktiv (geladen)? Unterscheidet sich von
        // Enabled.Value, sobald der Nutzer im Manager umschaltet — die Änderung greift erst nach
        // Neustart, der Mod läuft bis dahin weiter.
        public bool RuntimeEnabled;
        // Optionaler zusätzlicher Live-Toggle (z. B. HostFix' Snitch-Fallback). Wirkt sofort —
        // der jeweilige Mod liest den Wert zur Laufzeit, daher kein Neustart nötig.
        public ConfigEntry<bool> ExtraToggle;
        public string ExtraToggleLabel;
    }

    // Zentrale Registry für alle Mods. Liest Mod-Daten aus AppDomain (keine Compile-Zeit-Referenzen).
    public static class ModManagerRegistry
    {
        private const string RegistryKeyPrefix = "ModManager.RegisteredMod.";
        private const string ModManagerEnabledKey = "ModManager.IsEnabled";
        private const string ManifestKey = "ModManager.Manifest";

        // Registriert einen neuen Mod in der Registry (sollte von jedem Plugin in Load() aufgerufen werden).
        public static void RegisterMod(string guid, Dictionary<string, object> data)
        {
            try
            {
                // Speichere Mod-Daten
                AppDomain.CurrentDomain.SetData(RegistryKeyPrefix + guid, data);

                // Füge GUID zum Manifest hinzu
                var manifest = GetManifest();
                if (!manifest.Contains(guid))
                {
                    manifest.Add(guid);
                    AppDomain.CurrentDomain.SetData(ManifestKey, manifest);
                    UsefulTORStuffPlugin.Logger?.LogInfo($"Registered mod in Mod Manager: {guid}");
                }
            }
            catch (Exception ex)
            {
                UsefulTORStuffPlugin.Logger?.LogError($"Failed to register mod {guid}: {ex}");
            }
        }

        // Drossel für den Update-Re-Check beim Öffnen des Mod Managers: höchstens 1×/Minute,
        // damit wiederholtes Öffnen (oder ein Öffnen direkt nach dem Start-Check) die GitHub-API
        // nicht zuspammt.
        private static DateTime _lastUpdateCheckUtc = DateTime.MinValue;
        private static readonly TimeSpan UpdateCheckInterval = TimeSpan.FromMinutes(1);

        // Setzt den Drossel-Zeitstempel, ohne einen Check auszulösen. Für den automatischen
        // Start-Check der Updater gedacht — so zählt dieser als „in der letzten Minute geprüft“.
        public static void MarkUpdateCheckNow()
        {
            _lastUpdateCheckUtc = DateTime.UtcNow;
        }

        // Stößt für alle laufenden Mods mit GitHub-Repo einen erneuten Release-Check an —
        // außer der letzte Check liegt weniger als eine Minute zurück. Wird beim Öffnen des
        // Mod Managers aufgerufen.
        public static void MaybeCheckForUpdates()
        {
            var now = DateTime.UtcNow;
            var since = now - _lastUpdateCheckUtc;
            if (since < UpdateCheckInterval)
            {
                UsefulTORStuffPlugin.Logger?.LogInfo(
                    $"Mod Manager: Update-Check übersprungen (letzter Check vor {since.TotalSeconds:F0}s).");
                return;
            }

            _lastUpdateCheckUtc = now;
            int n = 0;
            foreach (var mod in GetAllMods())
            {
                if (!mod.RuntimeEnabled) continue;                                  // nicht geladen
                if (string.IsNullOrWhiteSpace(mod.RepositoryOwner)
                    || string.IsNullOrWhiteSpace(mod.RepositoryName)) continue;     // lokale Mods ohne Repo
                try { mod.TriggerCheck?.Invoke(); n++; }
                catch (Exception ex)
                {
                    UsefulTORStuffPlugin.Logger?.LogWarning(
                        $"Mod Manager: Re-Check für {mod.Guid} fehlgeschlagen: {ex.Message}");
                }
            }

            UsefulTORStuffPlugin.Logger?.LogInfo($"Mod Manager: Update-Re-Check für {n} Mod(s) gestartet.");
        }

        // Gibt das Manifest (Liste aller registrierten Mod-GUIDs) zurück.
        private static List<string> GetManifest()
        {
            var data = AppDomain.CurrentDomain.GetData(ManifestKey);
            return data as List<string> ?? new List<string>();
        }

        // PERF (2026-09-01 audit): GetAllMods() re-reads every registered mod's Dictionary out of
        // AppDomain and re-converts it to a fresh ModInfo on EVERY call - MaybeCheckForUpdates and the
        // Mod Manager UI both call it repeatedly (the UI while its panel is open). A short TTL cache
        // avoids repeating that work call after call within the same instant; it is additionally
        // invalidated the moment the manifest's mod COUNT changes (a mod registering mid-TTL becomes
        // visible immediately instead of waiting out the rest of the window). The returned ModInfo
        // instances are the SAME objects across cache hits, not fresh copies per call - the fields the
        // UI holds onto (_entryRefs) and the callbacks (HasUpdate, TriggerUpdate, ...) are all set once
        // at conversion time and never mutated afterwards, so a shared reference behaves identically to
        // a fresh one for every existing caller.
        private const float CacheTtlSeconds = 2f;
        private static List<ModInfo> _cachedMods;
        private static float _cachedAtRealtime = float.NegativeInfinity;
        private static int _cachedManifestCount = -1;

        // Gibt alle registrierten Mods zurück, indem Dictionaries aus AppDomain gelesen werden.
        public static List<ModInfo> GetAllMods()
        {
            var manifest = GetManifest();
            bool manifestChanged = manifest.Count != _cachedManifestCount;
            if (_cachedMods != null && !manifestChanged
                && (Time.realtimeSinceStartup - _cachedAtRealtime) < CacheTtlSeconds)
            {
                return _cachedMods;
            }

            var mods = new List<ModInfo>();

            try
            {
                var allGuids = new HashSet<string>(manifest);

                // Fallback hard-coded GUIDs (funktioniert mit alten Mods, die SetData() direkt verwenden)
                string[] knownGuids = {
                    "com.tormod.chancemodifier",
                    "com.trackerteam.hostfix",
                    "com.tormod.usefultorstuff",
                    "com.tormod.nightfall"
                };

                foreach (var guid in knownGuids)
                {
                    allGuids.Add(guid); // HashSet verhindert Duplikate
                }

                foreach (var guid in allGuids)
                {
                    var data = AppDomain.CurrentDomain.GetData(RegistryKeyPrefix + guid);
                    if (data is Dictionary<string, object> dict)
                    {
                        var modInfo = ConvertDictionaryToModInfo(guid, dict);
                        if (modInfo != null)
                        {
                            mods.Add(modInfo);
                        }
                    }
                }

                // Only logged when the resulting mod count actually changed since the last real
                // fetch - a cache-refresh call every couple seconds while the panel sits open used to
                // spam this line unchanged.
                if (_cachedMods == null || mods.Count != _cachedMods.Count)
                {
                    UsefulTORStuffPlugin.Logger?.LogInfo($"Mod Manager: Found {mods.Count} mods (Manifest: {manifest.Count}, Hardcoded: {knownGuids.Length})");
                }
            }
            catch (Exception ex)
            {
                UsefulTORStuffPlugin.Logger?.LogError($"Failed to retrieve registered mods: {ex}");
            }

            _cachedMods = mods;
            _cachedAtRealtime = Time.realtimeSinceStartup;
            _cachedManifestCount = manifest.Count;
            return mods;
        }

        // Konvertiert ein Dictionary (aus AppDomain) zu einem ModInfo-Objekt.
        private static ModInfo ConvertDictionaryToModInfo(string guid, Dictionary<string, object> dict)
        {
            try
            {
                var modInfo = new ModInfo
                {
                    Guid = dict.TryGetValue("Guid", out var g) ? g as string : guid,
                    Name = dict.TryGetValue("Name", out var n) ? n as string : "Unknown",
                    Version = dict.TryGetValue("Version", out var v) ? v as Version : new Version(1, 0, 0),
                    RepositoryOwner = dict.TryGetValue("RepositoryOwner", out var ro) ? ro as string : "",
                    RepositoryName = dict.TryGetValue("RepositoryName", out var rn) ? rn as string : "",
                    ButtonColor = dict.TryGetValue("ButtonColor", out var bc) && bc is Color c ? c : Color.white,
                    Enabled = dict.TryGetValue("Enabled", out var e) ? e as ConfigEntry<bool> : null,
                    // Vorhandensein des Registry-Eintrags bedeutet: der Mod ist beim Start geladen
                    // worden (deaktivierte Mods registrieren sich nicht). Default true für Altdaten.
                    RuntimeEnabled = !dict.TryGetValue("RuntimeEnabled", out var re) || !(re is bool reb) || reb,
                    // Optionaler Live-Toggle (per Referenz vom jeweiligen Mod geteilt).
                    ExtraToggle = dict.TryGetValue("ExtraToggle", out var et) ? et as ConfigEntry<bool> : null,
                    ExtraToggleLabel = dict.TryGetValue("ExtraToggleLabel", out var etl) ? etl as string : null
                };

                // Callbacks für HasUpdate und TriggerUpdate (reflection-basiert auf Updater-Instanzen)
                SetupUpdateCallbacks(modInfo);

                return modInfo;
            }
            catch (Exception ex)
            {
                UsefulTORStuffPlugin.Logger?.LogError($"Failed to convert mod data for {guid}: {ex}");
                return null;
            }
        }

        // Setzt HasUpdate und TriggerUpdate Callbacks via Reflection auf Updater-Instanzen.
        private static void SetupUpdateCallbacks(ModInfo modInfo)
        {
            try
            {
                if (modInfo.Guid == "com.tormod.chancemodifier")
                {
                    modInfo.HasUpdate = () => {
                        var type = Type.GetType("TOR_ChanceModifier.ChanceModUpdater, TOR-ChanceModifier");
                        var instance = type?.GetProperty("Instance")?.GetValue(null);
                        var hasUpdateMethod = type?.GetMethod("HasUpdate");
                        return instance != null && hasUpdateMethod != null && (bool)hasUpdateMethod.Invoke(instance, null);
                    };
                    modInfo.TriggerUpdate = () => {
                        var type = Type.GetType("TOR_ChanceModifier.ChanceModUpdater, TOR-ChanceModifier");
                        var instance = type?.GetProperty("Instance")?.GetValue(null);
                        var triggerMethod = type?.GetMethod("TriggerUpdateFromManager");
                        triggerMethod?.Invoke(instance, null);
                    };
                    modInfo.GetUpdateState = () => {
                        var type = Type.GetType("TOR_ChanceModifier.ChanceModUpdater, TOR-ChanceModifier");
                        var instance = type?.GetProperty("Instance")?.GetValue(null);
                        var method = type?.GetMethod("GetUpdateState");
                        return instance != null && method != null ? (int)method.Invoke(instance, null) : 0;
                    };
                    modInfo.GetUpdateProgress = () => {
                        var type = Type.GetType("TOR_ChanceModifier.ChanceModUpdater, TOR-ChanceModifier");
                        var instance = type?.GetProperty("Instance")?.GetValue(null);
                        var method = type?.GetMethod("GetUpdateProgress");
                        return instance != null && method != null ? (float)method.Invoke(instance, null) : 0f;
                    };
                    modInfo.GetCheckCompleted = () => {
                        var type = Type.GetType("TOR_ChanceModifier.ChanceModUpdater, TOR-ChanceModifier");
                        var instance = type?.GetProperty("Instance")?.GetValue(null);
                        var method = type?.GetMethod("GetCheckCompleted");
                        return instance != null && method != null && (bool)method.Invoke(instance, null);
                    };
                    modInfo.TriggerCheck = () => {
                        var type = Type.GetType("TOR_ChanceModifier.ChanceModUpdater, TOR-ChanceModifier");
                        var instance = type?.GetProperty("Instance")?.GetValue(null);
                        var method = type?.GetMethod("TriggerCheckFromManager");
                        method?.Invoke(instance, null);
                    };
                    modInfo.GetReleaseNotes = () => {
                        var type = Type.GetType("TOR_ChanceModifier.ChanceModUpdater, TOR-ChanceModifier");
                        var instance = type?.GetProperty("Instance")?.GetValue(null);
                        var method = type?.GetMethod("GetReleaseNotes");   // F2: probe — null on older installs
                        return instance != null && method != null ? method.Invoke(instance, null) as string : null;
                    };
                    modInfo.HasChannelRelease = (s) => {
                        var type = Type.GetType("TOR_ChanceModifier.ChanceModUpdater, TOR-ChanceModifier");
                        var instance = type?.GetProperty("Instance")?.GetValue(null);
                        var m = type?.GetMethod("HasChannelRelease");
                        return instance != null && m != null && (bool)m.Invoke(instance, new object[] { s });
                    };
                    modInfo.TriggerChannelSwitch = (s) => {
                        var type = Type.GetType("TOR_ChanceModifier.ChanceModUpdater, TOR-ChanceModifier");
                        var instance = type?.GetProperty("Instance")?.GetValue(null);
                        var m = type?.GetMethod("TriggerChannelSwitch");
                        m?.Invoke(instance, new object[] { s });
                    };
                    modInfo.ReleasesLoaded = () => {
                        var type = Type.GetType("TOR_ChanceModifier.ChanceModUpdater, TOR-ChanceModifier");
                        var instance = type?.GetProperty("Instance")?.GetValue(null);
                        var m = type?.GetMethod("ReleasesLoaded");
                        return instance != null && m != null ? (bool)m.Invoke(instance, null) : true;
                    };
                }
                else if (modInfo.Guid == "com.trackerteam.hostfix")
                {
                    modInfo.HasUpdate = () => {
                        var type = Type.GetType("HostFixPlugin.HostFixUpdater, HostFixPlugin");
                        var instance = type?.GetProperty("Instance")?.GetValue(null);
                        var hasUpdateMethod = type?.GetMethod("HasUpdate");
                        return instance != null && hasUpdateMethod != null && (bool)hasUpdateMethod.Invoke(instance, null);
                    };
                    modInfo.TriggerUpdate = () => {
                        var type = Type.GetType("HostFixPlugin.HostFixUpdater, HostFixPlugin");
                        var instance = type?.GetProperty("Instance")?.GetValue(null);
                        var triggerMethod = type?.GetMethod("TriggerUpdateFromManager");
                        triggerMethod?.Invoke(instance, null);
                    };
                    modInfo.GetUpdateState = () => {
                        var type = Type.GetType("HostFixPlugin.HostFixUpdater, HostFixPlugin");
                        var instance = type?.GetProperty("Instance")?.GetValue(null);
                        var method = type?.GetMethod("GetUpdateState");
                        return instance != null && method != null ? (int)method.Invoke(instance, null) : 0;
                    };
                    modInfo.GetUpdateProgress = () => {
                        var type = Type.GetType("HostFixPlugin.HostFixUpdater, HostFixPlugin");
                        var instance = type?.GetProperty("Instance")?.GetValue(null);
                        var method = type?.GetMethod("GetUpdateProgress");
                        return instance != null && method != null ? (float)method.Invoke(instance, null) : 0f;
                    };
                    modInfo.GetCheckCompleted = () => {
                        var type = Type.GetType("HostFixPlugin.HostFixUpdater, HostFixPlugin");
                        var instance = type?.GetProperty("Instance")?.GetValue(null);
                        var method = type?.GetMethod("GetCheckCompleted");
                        return instance != null && method != null && (bool)method.Invoke(instance, null);
                    };
                    modInfo.TriggerCheck = () => {
                        var type = Type.GetType("HostFixPlugin.HostFixUpdater, HostFixPlugin");
                        var instance = type?.GetProperty("Instance")?.GetValue(null);
                        var method = type?.GetMethod("TriggerCheckFromManager");
                        method?.Invoke(instance, null);
                    };
                    modInfo.GetReleaseNotes = () => {
                        var type = Type.GetType("HostFixPlugin.HostFixUpdater, HostFixPlugin");
                        var instance = type?.GetProperty("Instance")?.GetValue(null);
                        var method = type?.GetMethod("GetReleaseNotes");   // F2: probe — null on older installs
                        return instance != null && method != null ? method.Invoke(instance, null) as string : null;
                    };
                    modInfo.HasChannelRelease = (s) => {
                        var type = Type.GetType("HostFixPlugin.HostFixUpdater, HostFixPlugin");
                        var instance = type?.GetProperty("Instance")?.GetValue(null);
                        var m = type?.GetMethod("HasChannelRelease");
                        return instance != null && m != null && (bool)m.Invoke(instance, new object[] { s });
                    };
                    modInfo.TriggerChannelSwitch = (s) => {
                        var type = Type.GetType("HostFixPlugin.HostFixUpdater, HostFixPlugin");
                        var instance = type?.GetProperty("Instance")?.GetValue(null);
                        var m = type?.GetMethod("TriggerChannelSwitch");
                        m?.Invoke(instance, new object[] { s });
                    };
                    modInfo.ReleasesLoaded = () => {
                        var type = Type.GetType("HostFixPlugin.HostFixUpdater, HostFixPlugin");
                        var instance = type?.GetProperty("Instance")?.GetValue(null);
                        var m = type?.GetMethod("ReleasesLoaded");
                        return instance != null && m != null ? (bool)m.Invoke(instance, null) : true;
                    };
                }
                else if (modInfo.Guid == "com.tormod.usefultorstuff")
                {
                    modInfo.HasUpdate = () => UsefulTORStuffUpdater.Instance?.HasUpdate() ?? false;
                    modInfo.TriggerUpdate = () => UsefulTORStuffUpdater.Instance?.TriggerUpdateFromManager();
                    modInfo.GetUpdateState = () => UsefulTORStuffUpdater.Instance?.GetUpdateState() ?? 0;
                    modInfo.GetUpdateProgress = () => UsefulTORStuffUpdater.Instance?.GetUpdateProgress() ?? 0f;
                    modInfo.GetCheckCompleted = () => UsefulTORStuffUpdater.Instance?.GetCheckCompleted() ?? false;
                    modInfo.TriggerCheck = () => UsefulTORStuffUpdater.Instance?.TriggerCheckFromManager();
                    modInfo.GetReleaseNotes = () => UsefulTORStuffUpdater.Instance?.GetReleaseNotes();
                    modInfo.HasChannelRelease = (s) => UsefulTORStuffUpdater.Instance?.HasChannelRelease(s) ?? false;
                    modInfo.TriggerChannelSwitch = (s) => UsefulTORStuffUpdater.Instance?.TriggerChannelSwitch(s);
                    modInfo.ReleasesLoaded = () => UsefulTORStuffUpdater.Instance?.ReleasesLoaded() ?? true;
                }
                else if (modInfo.Guid == "com.tormod.unknownscollection")
                {
                    var tn = "UnknownsCollection.UnknownsCollectionUpdater, UnknownsCollection";
                    modInfo.HasUpdate = () => {
                        var type = Type.GetType(tn);
                        var instance = type?.GetProperty("Instance")?.GetValue(null);
                        var m = type?.GetMethod("HasUpdate");
                        return instance != null && m != null && (bool)m.Invoke(instance, null);
                    };
                    modInfo.TriggerUpdate = () => {
                        var type = Type.GetType(tn);
                        var instance = type?.GetProperty("Instance")?.GetValue(null);
                        type?.GetMethod("TriggerUpdateFromManager")?.Invoke(instance, null);
                    };
                    modInfo.GetUpdateState = () => {
                        var type = Type.GetType(tn);
                        var instance = type?.GetProperty("Instance")?.GetValue(null);
                        var m = type?.GetMethod("GetUpdateState");
                        return instance != null && m != null ? (int)m.Invoke(instance, null) : 0;
                    };
                    modInfo.GetUpdateProgress = () => {
                        var type = Type.GetType(tn);
                        var instance = type?.GetProperty("Instance")?.GetValue(null);
                        var m = type?.GetMethod("GetUpdateProgress");
                        return instance != null && m != null ? (float)m.Invoke(instance, null) : 0f;
                    };
                    modInfo.GetCheckCompleted = () => {
                        var type = Type.GetType(tn);
                        var instance = type?.GetProperty("Instance")?.GetValue(null);
                        var m = type?.GetMethod("GetCheckCompleted");
                        return instance != null && m != null && (bool)m.Invoke(instance, null);
                    };
                    modInfo.TriggerCheck = () => {
                        var type = Type.GetType(tn);
                        var instance = type?.GetProperty("Instance")?.GetValue(null);
                        type?.GetMethod("TriggerCheckFromManager")?.Invoke(instance, null);
                    };
                    modInfo.GetReleaseNotes = () => {
                        var type = Type.GetType(tn);
                        var instance = type?.GetProperty("Instance")?.GetValue(null);
                        var m = type?.GetMethod("GetReleaseNotes");
                        return instance != null && m != null ? m.Invoke(instance, null) as string : null;
                    };
                    modInfo.HasChannelRelease = (s) => {
                        var type = Type.GetType(tn);
                        var instance = type?.GetProperty("Instance")?.GetValue(null);
                        var m = type?.GetMethod("HasChannelRelease");
                        return instance != null && m != null && (bool)m.Invoke(instance, new object[] { s });
                    };
                    modInfo.TriggerChannelSwitch = (s) => {
                        var type = Type.GetType(tn);
                        var instance = type?.GetProperty("Instance")?.GetValue(null);
                        type?.GetMethod("TriggerChannelSwitch")?.Invoke(instance, new object[] { s });
                    };
                    modInfo.ReleasesLoaded = () => {
                        var type = Type.GetType(tn);
                        var instance = type?.GetProperty("Instance")?.GetValue(null);
                        var m = type?.GetMethod("ReleasesLoaded");
                        return instance != null && m != null ? (bool)m.Invoke(instance, null) : true;
                    };
                }
                else if (modInfo.Guid == "com.tormod.nightfall")
                {
                    var tn = "Nightfall.NightfallUpdater, Nightfall";
                    modInfo.HasUpdate = () => {
                        var type = Type.GetType(tn);
                        var instance = type?.GetProperty("Instance")?.GetValue(null);
                        var m = type?.GetMethod("HasUpdate");
                        return instance != null && m != null && (bool)m.Invoke(instance, null);
                    };
                    modInfo.TriggerUpdate = () => {
                        var type = Type.GetType(tn);
                        var instance = type?.GetProperty("Instance")?.GetValue(null);
                        type?.GetMethod("TriggerUpdateFromManager")?.Invoke(instance, null);
                    };
                    modInfo.GetUpdateState = () => {
                        var type = Type.GetType(tn);
                        var instance = type?.GetProperty("Instance")?.GetValue(null);
                        var m = type?.GetMethod("GetUpdateState");
                        return instance != null && m != null ? (int)m.Invoke(instance, null) : 0;
                    };
                    modInfo.GetUpdateProgress = () => {
                        var type = Type.GetType(tn);
                        var instance = type?.GetProperty("Instance")?.GetValue(null);
                        var m = type?.GetMethod("GetUpdateProgress");
                        return instance != null && m != null ? (float)m.Invoke(instance, null) : 0f;
                    };
                    modInfo.GetCheckCompleted = () => {
                        var type = Type.GetType(tn);
                        var instance = type?.GetProperty("Instance")?.GetValue(null);
                        var m = type?.GetMethod("GetCheckCompleted");
                        return instance != null && m != null && (bool)m.Invoke(instance, null);
                    };
                    modInfo.TriggerCheck = () => {
                        var type = Type.GetType(tn);
                        var instance = type?.GetProperty("Instance")?.GetValue(null);
                        type?.GetMethod("TriggerCheckFromManager")?.Invoke(instance, null);
                    };
                    modInfo.GetReleaseNotes = () => {
                        var type = Type.GetType(tn);
                        var instance = type?.GetProperty("Instance")?.GetValue(null);
                        var m = type?.GetMethod("GetReleaseNotes");
                        return instance != null && m != null ? m.Invoke(instance, null) as string : null;
                    };
                    modInfo.HasChannelRelease = (s) => {
                        var type = Type.GetType(tn);
                        var instance = type?.GetProperty("Instance")?.GetValue(null);
                        var m = type?.GetMethod("HasChannelRelease");
                        return instance != null && m != null && (bool)m.Invoke(instance, new object[] { s });
                    };
                    modInfo.TriggerChannelSwitch = (s) => {
                        var type = Type.GetType(tn);
                        var instance = type?.GetProperty("Instance")?.GetValue(null);
                        type?.GetMethod("TriggerChannelSwitch")?.Invoke(instance, new object[] { s });
                    };
                    modInfo.ReleasesLoaded = () => {
                        var type = Type.GetType(tn);
                        var instance = type?.GetProperty("Instance")?.GetValue(null);
                        var m = type?.GetMethod("ReleasesLoaded");
                        return instance != null && m != null ? (bool)m.Invoke(instance, null) : true;
                    };
                }
            }
            catch (Exception ex)
            {
                UsefulTORStuffPlugin.Logger?.LogWarning($"Failed to setup callbacks for {modInfo.Guid}: {ex}");
            }
        }

        // Setzt den Mod-Manager-Enabled-Status.
        public static void SetModManagerEnabled(bool enabled)
        {
            try
            {
                AppDomain.CurrentDomain.SetData(ModManagerEnabledKey, enabled);
            }
            catch (Exception ex)
            {
                UsefulTORStuffPlugin.Logger?.LogError($"Failed to set ModManager enabled status: {ex}");
            }
        }

        // Prüft ob der Mod-Manager aktiviert ist.
        public static bool IsModManagerEnabled()
        {
            try
            {
                var data = AppDomain.CurrentDomain.GetData(ModManagerEnabledKey);
                return data is bool b && b;
            }
            catch
            {
                return false;
            }
        }
    }
}
