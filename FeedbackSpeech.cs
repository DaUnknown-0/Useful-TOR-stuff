// Useful TOR Stuff - Copyright (C) 2026 DaUnknown-0
// Feedback module, taken over from the standalone Unknown's Feedback (2026-10-30).
// Licensed under GPL-3.0-or-later. See LICENSE for details.

/*
 * FeedbackSpeech - dictation into the note through the Windows speech recognizer (SAPI 8.0 desktop
 * recognizer, offline, part of the Windows language pack: "Microsoft Speech Recognizer 8.0 for
 * Windows (German - Germany)" on a German Windows).
 *
 * Measured 2026-10-27 with synthetic German speech (Hedda voice -> WAV -> DictationGrammar): two of
 * three sentences word-perfect, the third with errors ("Lüftungsschacht ... zu nah" -> "Delfter muss
 * Schacht ... Sender"). Good enough for a draft the player corrects by typing.
 *
 * The recognizer runs on SAPI's own threads; its events only fill a queue that the game thread
 * drains (Unity objects must never be touched from there). One engine per process, created on the
 * first use (that takes about a second), the dictation grammar stays loaded.
 * Language: the recognizer matching the game language (German / English), else any installed one.
 */

using System;
using System.Collections.Concurrent;
using System.Linq;
using System.Speech.Recognition;

namespace UsefulTORStuff;

internal static class FeedbackSpeech
{
    internal enum State { Off, Starting, Listening, Unavailable }

    internal static State Current { get; private set; } = State.Off;
    /// <summary>The words heard so far in the phrase being spoken (grey preview), "" when none.</summary>
    internal static string Hypothesis { get; private set; } = "";
    internal static string Problem { get; private set; } = "";
    internal static string Culture { get; private set; } = "";

    private static SpeechRecognitionEngine engine;
    private static readonly ConcurrentQueue<string> finals = new();
    private static volatile string pendingHypothesis = "";
    private static volatile string pendingProblem;
    private static volatile bool started, stopped;
    private static bool probed;
    private static bool available;

    /// <summary>Is any recognizer installed? Checked once.</summary>
    internal static bool Available
    {
        get {
            if (!probed) {
                probed = true;
                try { available = SpeechRecognitionEngine.InstalledRecognizers().Count > 0; }
                catch (Exception e) {
                    available = false;
                    Problem = e.Message;
                    UsefulTORStuffPlugin.Logger.LogWarning($"[Feedback] speech: no recognizer ({e.Message})");
                }
                if (!available) Current = State.Unavailable;
            }
            return available;
        }
    }

    /// <summary>Starts listening on the default microphone; false when it cannot.</summary>
    internal static bool Start() => Start(null);

    /// <summary>Self test: recognizes a WAV file instead of the microphone.</summary>
    internal static bool StartFromFile(string wav) => Start(wav);

    private static bool Start(string wav)
    {
        if (!Available) return false;
        if (Current == State.Listening || Current == State.Starting) return true;
        try {
            if (engine == null) {
                var all = SpeechRecognitionEngine.InstalledRecognizers();
                string want = FeedbackText.German ? "de" : "en";
                var info = all.FirstOrDefault(r => r.Culture.TwoLetterISOLanguageName == want) ?? all[0];
                Culture = info.Culture.Name;
                engine = new SpeechRecognitionEngine(info);
                engine.LoadGrammar(new DictationGrammar());
                engine.SpeechHypothesized += (_, e) => pendingHypothesis = e.Result?.Text ?? "";
                engine.SpeechRecognized += (_, e) => {
                    pendingHypothesis = "";
                    if (e.Result != null && !string.IsNullOrWhiteSpace(e.Result.Text)) finals.Enqueue(e.Result.Text);
                };
                engine.RecognizeCompleted += (_, e) => {
                    if (e.Error != null) pendingProblem = e.Error.Message;
                    stopped = true;
                };
                UsefulTORStuffPlugin.Logger.LogInfo($"[Feedback] speech: recognizer {info.Description} ({Culture})");
            }
            if (wav != null) engine.SetInputToWaveFile(wav);
            else engine.SetInputToDefaultAudioDevice();
            stopped = false;
            started = true;
            engine.RecognizeAsync(RecognizeMode.Multiple);
            Current = State.Listening;
            Problem = "";
            return true;
        } catch (Exception e) {
            Problem = e.Message;
            Current = State.Off;
            UsefulTORStuffPlugin.Logger.LogWarning($"[Feedback] speech: start failed ({e.Message})");
            return false;
        }
    }

    internal static void Stop()
    {
        if (engine == null || Current != State.Listening) return;
        try { engine.RecognizeAsyncCancel(); } catch { }
        Current = State.Off;
        Hypothesis = "";
    }

    /// <summary>Game thread: hands over finished phrases (text to append) and refreshes the preview.</summary>
    internal static string Drain()
    {
        string text = null;
        while (finals.TryDequeue(out var s)) text = text == null ? s : text + " " + s;
        Hypothesis = Current == State.Listening ? pendingHypothesis : "";
        if (pendingProblem != null) { Problem = pendingProblem; pendingProblem = null; }
        if (stopped && Current == State.Listening && started) {
            // the input ended on its own (file done, microphone gone)
            Current = State.Off;
            started = false;
        }
        return text;
    }
}
