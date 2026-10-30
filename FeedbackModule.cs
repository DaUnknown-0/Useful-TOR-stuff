// Useful TOR Stuff - Copyright (C) 2026 DaUnknown-0
// Licensed under GPL-3.0-or-later. See LICENSE for details.

/*
 * FeedbackModule - live map feedback from the players to the mod's author, as part of UTS.
 *
 * Taken over from the standalone mod "Unknown's Feedback" (User 2026-10-30): a player presses the bug
 * button (FeedbackButton, lobby and round) or F4, types what they would change at that spot, picks a
 * category and saves. Notes wait in BepInEx/UnknownsFeedback/outbox.json and are delivered in the lobby
 * to the author's client only (FeedbackNet, own callId 233, unchanged so players who still run the
 * standalone mod keep reaching the author). Same data folder as the standalone mod, so waiting notes
 * carry over.
 *
 * The standalone mod is switched off when both are installed: its Harmony patches are removed as soon
 * as the main menu stands, and its config gets Enabled = false so it stays out on the next start.
 * Config lives in UTS's own file, section "Feedback". Works without a UTS host (nothing here is a
 * lobby setting).
 */

using System;
using System.IO;
using System.Text.RegularExpressions;
using BepInEx;
using BepInEx.Configuration;
using BepInEx.Unity.IL2CPP;
using HarmonyLib;
using UnityEngine;

namespace UsefulTORStuff;

internal static class FeedbackModule
{
    internal const string StandaloneGuid = "com.daunknown.unknownsfeedback";

    internal static ConfigEntry<bool> Enabled;
    internal static ConfigEntry<bool> ShowButton;
    internal static ConfigEntry<bool> HardInputLock;
    internal static ConfigEntry<bool> DiagSelfTest;
    internal static ConfigEntry<bool> DiagForceGuest;
    internal static KeyCode Hotkey = KeyCode.F4;

    internal static bool Active => Enabled?.Value == true && loaded;
    private static bool loaded, tookOver;

    internal static void Bind(ConfigFile config)
    {
        Enabled = config.Bind("Feedback", "Enabled", true,
            "Feedback notes to the mod's author: the bug button (lobby and round) or the hotkey opens a note editor.");
        var key = config.Bind("Feedback", "Hotkey", "F4", "Key that opens the feedback editor (Unity KeyCode name, e.g. F4, F7, Insert)");
        if (!Enum.TryParse(key.Value, true, out Hotkey)) Hotkey = KeyCode.F4;
        ShowButton = config.Bind("Feedback", "ShowButton", true, "Show the small bug button at the top right in the lobby and during a round.");
        HardInputLock = config.Bind("Feedback", "HardInputLock", true,
            "While the editor is open, no key reaches the game or any mod (native input lock). Off: only movement and ability hotkeys are blocked.");
        DiagSelfTest = config.Bind("Feedback", "SelfTest", false,
            "Autotest: start a freeplay on its own, write a note through the editor, take a picture, check the store. Leave off.");
        DiagForceGuest = config.Bind("Feedback", "ForceGuest", false,
            "Autotest: treat this client as a normal player even if it is the author's (notes go to the outbox). Leave off.");
    }

    /// <summary>After PatchAll in UsefulTORStuffPlugin.Load.</summary>
    internal static void Init()
    {
        if (Enabled?.Value != true) {
            UsefulTORStuffPlugin.Logger.LogInfo("[Feedback] disabled in config");
            return;
        }
        try {
            FeedbackStore.Init();
            FeedbackNet.Init();
            loaded = true;
            TakeOverStandalone();
            UsefulTORStuffPlugin.Logger.LogInfo($"[Feedback] ready (hotkey {Hotkey}, data {FeedbackStore.Dir})");
        } catch (Exception e) {
            UsefulTORStuffPlugin.Logger.LogError($"[Feedback] init failed: {e}");
        }
    }

    /// <summary>The standalone "Unknown's Feedback": patches out now, config off for the next start.</summary>
    internal static void TakeOverStandalone()
    {
        if (tookOver || !loaded) return;
        try {
            if (!IL2CPPChainloader.Instance.Plugins.ContainsKey(StandaloneGuid)) return;
            tookOver = true;
            Harmony.UnpatchID(StandaloneGuid);
            string cfg = Path.Combine(Paths.ConfigPath, StandaloneGuid + ".cfg");
            if (File.Exists(cfg)) {
                string text = File.ReadAllText(cfg);
                string off = Regex.Replace(text, @"(?m)^Enabled\s*=\s*true\s*$", "Enabled = false");
                if (off != text) File.WriteAllText(cfg, off);
            }
            UsefulTORStuffPlugin.Logger.LogInfo("[Feedback] standalone Unknown's Feedback found: switched off, UTS takes over");
        } catch (Exception e) {
            UsefulTORStuffPlugin.Logger.LogWarning($"[Feedback] could not switch off the standalone mod: {e.Message}");
        }
    }

    /// <summary>Plugins load one after another; the standalone mod may come after UTS. The main menu is the first
    /// point where every plugin has run its Load.</summary>
    [HarmonyPatch(typeof(MainMenuManager), nameof(MainMenuManager.Start))]
    private static class MainMenuTakeOver
    {
        public static void Postfix() => TakeOverStandalone();
    }
}
