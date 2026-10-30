// Useful TOR Stuff - Copyright (C) 2026 DaUnknown-0
// Feedback module, taken over from the standalone Unknown's Feedback (2026-10-30).
// Licensed under GPL-3.0-or-later. See LICENSE for details.

/*
 * FeedbackSelfTest - autotest, only with Diagnostics.SelfTest = true (and then with its own data
 * folder BepInEx/UnknownsFeedback/selftest, see FeedbackStore.Init).
 *
 * Starts a Polus freeplay after the login (same way as Atlas's AtlasAutoFreeplay), then:
 *   1  note through the panel (author -> inbox, anyone else -> outbox), picture of the panel
 *   2  note as a forced guest -> outbox; an empty note is refused; cancel gives movement back
 *   3  point picked in the game view 2 m beside the player lands 2 m beside the player
 *   4  map opens while the player is frozen; the map's HerePoint converts back to the player's
 *      position; an area dragged on the map from the player's mark is stored as shape 2
 *   5  dictation inside the game process from the WAV named by the environment variable UF_SPEECH_WAV
 *   6  a 25 s typing window ("selftest: typing window open"): the test script sends keyboard input
 *      from outside (W held, E, R, Q, Tab, Space as real keys, then Unicode text); the text must arrive
 *      while the crewmate does not move, no meeting starts and the map stays shut (input lock)
 *   0  (UTS, 2026-10-30) the bug button stands in the round; the native input lock engages on open,
 *      answers "nothing pressed" outside the panel and is gone again after close
 * Every step logs "[Feedback] selftest: ..."; the run ends with "selftest: all done (n failed)".
 * The RPC path needs two clients and is not covered here.
 */

using System;
using System.Collections;
using System.IO;
using BepInEx.Unity.IL2CPP.Utils;
using HarmonyLib;
using UnityEngine;
using Object = UnityEngine.Object;

namespace UsefulTORStuff;

internal static class FeedbackSelfTest
{
    private static bool fired, running;
    private static int failed;

    private static bool On => FeedbackModule.DiagSelfTest?.Value == true;

    private static void Log(string s) => UsefulTORStuffPlugin.Logger.LogInfo("[Feedback] selftest: " + s);

    private static void Check(bool ok, string what)
    {
        if (!ok) failed++;
        Log((ok ? "PASS " : "FAIL ") + what);
    }

    internal static void Tick()
    {
        if (!On || running || ShipStatus.Instance == null || HudManager.Instance == null) return;
        running = true;
        HudManager.Instance.StartCoroutine(Run());
    }

    private static IEnumerator Run()
    {
        for (float t0 = Time.time; Time.time - t0 < 8f;) yield return null;
        var lp = PlayerControl.LocalPlayer;
        Vector2 me = lp.transform.position;

        // 0: bug button, input lock
        Check(FeedbackButton.Visible, "bug button shown in the round");
        Shot("selftest_button.png");
        yield return null;
        FeedbackPanel.Open();
        Check(FeedbackInputLock.Engaged, "native input lock engaged on open");
        bool anyOutside = Input.GetKey(KeyCode.W) || Input.GetKeyDown(KeyCode.E) || Input.anyKey || Input.GetAxis("Horizontal") != 0f;
        string strOutside = Input.inputString;
        Check(!anyOutside && strOutside == "", "keys read as nothing outside the panel");
        using (FeedbackInputLock.Own()) { _ = Input.GetKey(KeyCode.W); _ = Input.inputString; _ = Input.anyKeyDown; }
        Check(true, "the panel's own reads pass the lock without a crash");
        Check(!FeedbackButton.Visible, "bug button hidden while the editor is open");
        FeedbackPanel.Close();
        Check(!FeedbackInputLock.Engaged, "input lock released on close");

        // 1 + 2: panel, stores, refusals
        FeedbackPanel.Open();
        FeedbackPanel.TestType("Testnotiz: <b>Tür</b> klemmt hier, äöüß " + new string('x', 400));
        FeedbackPanel.TestCategory(1);
        string id1 = FeedbackPanel.LastDraftId;
        Check(FeedbackPanel.IsOpen, "panel opens");
        Check(!lp.moveable, "movement locked while open");
        for (float t0 = Time.time; Time.time - t0 < 1.5f;) yield return null;
        Shot("selftest_panel.png");
        yield return null;
        bool owner = FeedbackOwner.LocalIsOwner;
        FeedbackPanel.Save();
        Check(owner ? FeedbackStore.InInbox(id1) : FeedbackStore.Outbox.Count > 0,
              $"note 1 stored ({(owner ? "author -> inbox" : "guest -> outbox")}, map {FeedbackContext.MapKey()})");
        Check(!FeedbackPanel.IsOpen && lp.moveable, "panel closes after save, movement back");

        FeedbackOwner.ForceGuest = true;
        FeedbackPanel.Open();
        string id2 = FeedbackPanel.LastDraftId;
        FeedbackPanel.TestType("Gast-Notiz");
        FeedbackPanel.Save();
        FeedbackPanel.Save();   // under 20 characters: the second Save confirms (since 2026-10-28)
        FeedbackOwner.ForceGuest = false;
        Check(FeedbackStore.Outbox.Count > 0 && FeedbackStore.Outbox[FeedbackStore.Outbox.Count - 1].Id == id2, "note 2 as guest in the outbox");
        FeedbackPanel.Open();
        FeedbackPanel.Save();   // empty: must stay open
        Check(FeedbackPanel.IsOpen, "empty note is refused");
        FeedbackPanel.Close();
        Check(!FeedbackPanel.IsOpen && lp.moveable, "cancel closes, movement back");

        // 3: point in the game view
        FeedbackPanel.Open();
        FeedbackPanel.TestPick(false);
        Check(FeedbackPick.Active, "pick mode starts");
        for (float t0 = Time.time; Time.time - t0 < 1f;) yield return null;
        Shot("selftest_pick_banner.png");
        yield return null;
        var target = me + new Vector2(2f, 1f);
        var screen = Camera.main.WorldToScreenPoint(new Vector3(target.x, target.y, 0f));
        FeedbackPick.Complete(screen, screen, false);
        var d = FeedbackPanel.Draft;
        Check(d != null && d.Shape == FeedbackPick.ShapePoint && Vector2.Distance(new Vector2(d.X, d.Y), target) < 0.05f,
              $"game view point lands where clicked ({d?.X:0.00}|{d?.Y:0.00}, expected {target.x:0.00}|{target.y:0.00})");
        FeedbackPanel.TestType("Punkt im Spielbild");
        FeedbackPanel.Save();

        // 4: area on the map
        FeedbackPanel.Open();
        FeedbackPanel.TestPick(true);
        FeedbackPick.OpenMap();
        for (float t0 = Time.time; Time.time - t0 < 1.5f;) yield return null;
        Check(FeedbackPick.MapOpen(), "map opens while the player is frozen");
        var map = MapBehaviour.Instance;
        if (map != null && FeedbackPick.MapOpen()) {
            Check(FeedbackPick.MapSpaceToWorld(map, map.HerePoint.transform.position, out var here) && Vector2.Distance(here, me) < 0.3f,
                  $"map HerePoint converts back to the player ({here.x:0.00}|{here.y:0.00} vs {me.x:0.00}|{me.y:0.00})");
            var cam = FeedbackPick.MapCamera(map.gameObject.layer);
            var a = cam.WorldToScreenPoint(map.HerePoint.transform.position);
            var b = a + new Vector3(140f, -90f, 0f);
            Check(FeedbackPick.ToWorld(a, true, out var aw) && Vector2.Distance(aw, me) < 0.3f,
                  $"map click on the player's mark converts to the player ({aw.x:0.00}|{aw.y:0.00})");
            Shot("selftest_map.png");
            yield return null;
            FeedbackPick.Complete(a, b, true);
            d = FeedbackPanel.Draft;
            Check(d != null && d.Shape == FeedbackPick.ShapeArea && d.W > 0.5f && d.H > 0.5f,
                  $"map drag stored as area ({d?.W:0.0} x {d?.H:0.0} m around {d?.X:0.0}|{d?.Y:0.0})");
            for (float t0 = Time.time; Time.time - t0 < 0.8f;) yield return null;
            Check(!FeedbackPick.MapOpen(), "map closes after the pick");
            FeedbackPanel.TestType("Bereich auf der Karte");
            Shot("selftest_area_panel.png");
            yield return null;
            FeedbackPanel.Save();
        } else {
            FeedbackPick.End();
            FeedbackPanel.Close();
        }

        // 5: dictation from a WAV, in this process
        string wav = Environment.GetEnvironmentVariable("UF_SPEECH_WAV");
        if (!string.IsNullOrEmpty(wav) && File.Exists(wav)) {
            FeedbackPanel.Open();
            bool started = FeedbackSpeech.StartFromFile(wav);
            Check(started, $"speech recognizer starts in the game process ({FeedbackSpeech.Culture}) {FeedbackSpeech.Problem}");
            bool shot = false;
            for (float t0 = Time.time; started && Time.time - t0 < 25f && FeedbackSpeech.Current == FeedbackSpeech.State.Listening;) {
                if (!shot && Time.time - t0 > 2.5f) { Shot("selftest_dictation.png"); shot = true; }
                yield return null;
            }
            for (int i = 0; i < 5; i++) yield return null;
            string heard = FeedbackPanel.TestBuffer;
            Check(heard.Length > 10, $"dictation filled the note: '{heard}'");
            if (heard.Length > 0) FeedbackPanel.Save(); else FeedbackPanel.Close();
        } else Log("speech step skipped (UF_SPEECH_WAV not set)");

        // 6a: control without the editor: the same outside W must walk the crewmate, else 6 proves nothing
        Vector2 ctrl0 = lp.transform.position;
        Log("control window open");
        int seenFree = 0;
        for (float t0 = Time.time; Time.time - t0 < 8f;) { if (ModsSeeKeys()) seenFree++; yield return null; }
        Log($"control: crewmate moved {Vector2.Distance(ctrl0, lp.transform.position):0.00} without the editor");
        Check(seenFree > 0, $"control: without the editor mods see the outside keys ({seenFree} frames)");

        // 6: typing from outside
        FeedbackPanel.Open();
        FeedbackPanel.TestType("");
        Vector2 before = lp.transform.position;
        Log("typing window open");
        int seenLocked = 0;
        for (float t0 = Time.time; Time.time - t0 < 25f;) { if (ModsSeeKeys()) seenLocked++; yield return null; }
        Check(seenLocked == 0, $"with the editor open no mod sees a key ({seenLocked} frames)");
        string typed = FeedbackPanel.TestBuffer;
        Log($"typed from outside: '{typed}'");
        Check(typed.Length > 0, "keys typed from outside reach the editor");
        Check(Vector2.Distance(before, lp.transform.position) < 0.05f, "the crewmate did not walk while typing");
        Check(MeetingHud.Instance == null, "no meeting started while typing");
        Check(MapBehaviour.Instance == null || !MapBehaviour.Instance.IsOpen, "map stayed shut while typing");
        Shot("selftest_typed.png");
        yield return null;
        FeedbackPanel.Close();

        Log($"all done ({failed} failed)");
    }

    /// <summary>What a mod's hotkey check sees (outside the editor's own reads).</summary>
    private static bool ModsSeeKeys() =>
        Input.GetKey(KeyCode.W) || Input.GetKey(KeyCode.E) || Input.GetKey(KeyCode.R) || Input.GetKey(KeyCode.Q)
        || Input.GetKey(KeyCode.Tab) || Input.GetKey(KeyCode.Space) || Input.anyKey;

    private static void Shot(string file)
    {
        try {
            string path = Path.Combine(FeedbackStore.Dir, file);
            ScreenCapture.CaptureScreenshot(path);
            Log("picture " + path);
        } catch (Exception e) { Log("picture failed: " + e.Message); }
    }

    [HarmonyPatch(typeof(MainMenuManager), nameof(MainMenuManager.Start))]
    private static class AutoFreeplay
    {
        public static void Postfix(MainMenuManager __instance)
        {
            if (!On || fired) return;
            fired = true;
            __instance.StartCoroutine(StartFreeplay());
        }

        private static IEnumerator StartFreeplay()
        {
            Log("waiting for the login ...");
            float t0 = Time.time;
            while (true) {
                bool ok = false;
                try { ok = EOSManager.Instance != null && EOSManager.Instance.HasFinishedLoginFlow(); } catch { }
                if (ok) break;
                if (Time.time - t0 > 45f) {
                    Log("login not finished after 45 s, starting anyway");
                    try { foreach (var popup in Object.FindObjectsOfType<TimeOutPopupHandler>()) popup.gameObject.SetActive(false); } catch { }
                    break;
                }
                yield return null;
            }
            try {
                var popover = Object.FindObjectOfType<FreeplayPopover>(true);
                if (popover == null) { Log("FAIL no FreeplayPopover"); yield break; }
                for (var t = popover.transform; t != null; t = t.parent) if (!t.gameObject.activeSelf) t.gameObject.SetActive(true);
                popover.Show();
                popover.PlayMap(MapNames.Polus);
                Log("freeplay started (Polus)");
            } catch (Exception e) { Log("FAIL freeplay start: " + e); }
        }
    }
}
