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
        public ConfigEntry<bool> Enabled;
    }

    // Zentrale Registry für alle Mods. Liest Mod-Daten aus AppDomain (keine Compile-Zeit-Referenzen).
    public static class ModManagerRegistry
    {
        private const string RegistryKeyPrefix = "ModManager.RegisteredMod.";
        private const string ModManagerEnabledKey = "ModManager.IsEnabled";

        // Gibt alle registrierten Mods zurück, indem Dictionaries aus AppDomain gelesen werden.
        public static List<ModInfo> GetAllMods()
        {
            var mods = new List<ModInfo>();

            try
            {
                // Hard-coded GUIDs der bekannten Mods (da AppDomain keine Key-Enumeration bietet)
                string[] knownGuids = {
                    "com.tormod.chancemodifier",
                    "com.trackerteam.hostfix",
                    "com.tormod.usefultorstuff"
                };

                foreach (var guid in knownGuids)
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
            }
            catch (Exception ex)
            {
                UsefulTORStuffPlugin.Logger?.LogError($"Failed to retrieve registered mods: {ex}");
            }

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
                    Enabled = dict.TryGetValue("Enabled", out var e) ? e as ConfigEntry<bool> : null
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
                        var type = Type.GetType("TOR_ChanceModifier.ChanceModUpdater, ChanceMod");
                        var instance = type?.GetProperty("Instance")?.GetValue(null);
                        var hasUpdateMethod = type?.GetMethod("HasUpdate");
                        return instance != null && hasUpdateMethod != null && (bool)hasUpdateMethod.Invoke(instance, null);
                    };
                    modInfo.TriggerUpdate = () => {
                        var type = Type.GetType("TOR_ChanceModifier.ChanceModUpdater, ChanceMod");
                        var instance = type?.GetProperty("Instance")?.GetValue(null);
                        var triggerMethod = type?.GetMethod("TriggerUpdateFromManager");
                        triggerMethod?.Invoke(instance, null);
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
                }
                else if (modInfo.Guid == "com.tormod.usefultorstuff")
                {
                    modInfo.HasUpdate = () => UsefulTORStuffUpdater.Instance?.HasUpdate() ?? false;
                    modInfo.TriggerUpdate = () => UsefulTORStuffUpdater.Instance?.TriggerUpdateFromManager();
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
