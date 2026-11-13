// Useful TOR Stuff - Copyright (C) 2026 DaUnknown-0
// Licensed under GPL-3.0-or-later. See LICENSE for details.

/*
 * FastStartPlugin - a second, tiny BepInEx plugin inside the Forgotten Fixes DLL whose only job is to switch on
 * HarmonyBatch before The Other Roles loads (2026-11-13, User: start plus login under 20 s, +10 % ok).
 *
 * BepInEx loads the plugins in the order of their GUIDs, each one after its dependencies. Every mod depends on TOR,
 * so TOR always came first and its 189 patch builds ran before any of our code: Forgotten Fixes itself (and its
 * HarmonyBatch copy) loads as the twelfth plugin. This plugin has no dependency and a GUID that sorts first, so it
 * loads before TOR, and TOR's patches on in-game classes are built after the login as well.
 *
 * It touches no TOR type (TOR is not loaded yet when it runs). It is not a mod of its own: the mod sync, the Mod
 * Manager's delete check and the inventory skip its GUID (UTSModCatalog.IsNeverSyncable, ModManagerDelete).
 * Switch: [Performance] Batch Harmony Patches At Start in BepInEx/config/a.tormods.faststart.cfg.
 */

using BepInEx;
using BepInEx.Unity.IL2CPP;

namespace UsefulTORStuff {

    [BepInPlugin(Guid, "TOR Mods Fast Start", "1.0.0")]
    public class FastStartPlugin : BasePlugin {
        public const string Guid = "a.tormods.faststart";

        public override void Load() {
            HarmonyBatch.Install(Config.Bind("Performance", "Batch Harmony Patches At Start", true,
                    "Build every patched method once per Harmony.PatchAll, and the patches on in-game classes after the login (faster start).").Value,
                s => Log.LogInfo("[HarmonyBatch] " + s));
        }
    }
}
