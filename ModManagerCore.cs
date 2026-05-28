// Useful TOR Stuff - Copyright (C) 2026 DaUnknown-0
// Licensed under GPL-3.0-or-later. See LICENSE for details.

using System;
using System.Collections.Generic;
using BepInEx.Configuration;
using UnityEngine;

namespace UsefulTORStuff
{
    // Mod-Informationen für die Registry. Jede Mod registriert sich selbst via AppDomain.
    public class ModInfo
    {
        public string Guid;                     // z.B. "com.tormod.chancemodifier"
        public string Name;                     // z.B. "Chance Modifier"
        public Version Version;                 // Aktuelle Version
        public string RepositoryOwner;          // "DaUnknown-0"
        public string RepositoryName;           // "TOR-Chance"
        public Color ButtonColor;               // Farbe für Update-Button (gelb, cyan, grün)
        public Func<bool> HasUpdate;            // Callback: ist Update verfügbar?
        public Action TriggerUpdate;            // Callback: Update-Download starten
        public ConfigEntry<bool> Enabled;       // BepInEx ConfigEntry für Aktivierung
    }

    // Zentrale Registry für alle Mods. Verwendet AppDomain.SetData/GetData für
    // cross-assembly Kommunikation ohne Compile-Zeit-Referenzen (analog zum Credit-Toggle).
    public static class ModManagerRegistry
    {
        private const string RegistryKeyPrefix = "ModManager.RegisteredMod.";
        private const string ModManagerEnabledKey = "ModManager.IsEnabled";

        // Registriert eine Mod in der globalen Registry.
        public static void RegisterMod(ModInfo info)
        {
            if (info == null || string.IsNullOrEmpty(info.Guid))
            {
                UsefulTORStuffPlugin.Logger?.LogWarning("RegisterMod: ModInfo is null or has no Guid — skipping.");
                return;
            }

            try
            {
                AppDomain.CurrentDomain.SetData(RegistryKeyPrefix + info.Guid, info);
                UsefulTORStuffPlugin.Logger?.LogInfo($"Registered mod: {info.Name} v{info.Version} ({info.Guid})");
            }
            catch (Exception ex)
            {
                UsefulTORStuffPlugin.Logger?.LogError($"Failed to register mod {info.Guid}: {ex}");
            }
        }

        // Gibt alle registrierten Mods zurück.
        public static List<ModInfo> GetAllMods()
        {
            var mods = new List<ModInfo>();

            try
            {
                // AppDomain hat leider keine direkte API um alle Keys zu enumerieren.
                // Wir kennen aber die GUIDs aller unserer Mods (ChanceMod, HostFix, UsefulTORStuff).
                // Alternativ: jede Mod könnte ihre GUID auch in eine Liste schreiben.
                // Für jetzt: Hard-coded GUIDs der bekannten Mods.
                string[] knownGuids = {
                    "com.tormod.chancemodifier",
                    "com.trackerteam.hostfix",
                    "com.tormod.usefultorstuff"
                };

                foreach (var guid in knownGuids)
                {
                    var data = AppDomain.CurrentDomain.GetData(RegistryKeyPrefix + guid);
                    if (data is ModInfo modInfo)
                    {
                        mods.Add(modInfo);
                    }
                }
            }
            catch (Exception ex)
            {
                UsefulTORStuffPlugin.Logger?.LogError($"Failed to retrieve registered mods: {ex}");
            }

            return mods;
        }

        // Setzt den Mod-Manager-Enabled-Status (wird von UsefulTORStuffPlugin.cs aufgerufen).
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

        // Prüft ob der Mod-Manager aktiviert ist. Update-Buttons der einzelnen Mods
        // verwenden dies um zu entscheiden, ob sie sich selbst anzeigen sollen.
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
