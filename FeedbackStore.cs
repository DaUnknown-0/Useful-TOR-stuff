// Useful TOR Stuff - Copyright (C) 2026 DaUnknown-0
// Feedback module, taken over from the standalone Unknown's Feedback (2026-10-30).
// Licensed under GPL-3.0-or-later. See LICENSE for details.

/*
 * FeedbackStore - the note model and the two files a client keeps.
 *
 *   outbox.json   (every player) notes not yet acknowledged by the author's client; rewritten whole
 *                 on every change through a temp file, so a crash never leaves half a file
 *   inbox.jsonl   (the author) one received note per line; tools/build_uebersicht.py reads it. Ids
 *                 already in there are not stored twice (a lost ack makes the sender resend, see
 *                 FeedbackNet); an edited note (newer Edited) replaces its line (file rewritten).
 *   mine.json     (every player) the last 50 notes this player wrote, for editing them later.
 *
 * Both live in BepInEx/UnknownsFeedback next to the game.
 */

using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;

namespace UsefulTORStuff;

public sealed class FeedbackNote
{
    public string Id = "";
    public long Created;            // unix seconds, sender's clock
    public string Map = "";         // skeld, mira, polus, dleks, airship, fungle, submerged, lobby, or an Atlas key
    public float X, Y;              // world position (the centre of an area)
    public byte Shape;              // 0 own position, 1 picked point, 2 area (FeedbackPick)
    public float W, H;              // area size in metres, 0 for points
    public string Phase = "";       // lobby, round, meeting
    public bool Ghost;
    public float RoundSec = -1f;    // seconds since the round started, -1 outside a round
    public byte Category;           // FeedbackCategories index
    public string Text = "";
    public string Author = "";      // the sender's name when the note was written
    public string Version = "";     // sender's mod version
    // filled in by the author's client on receipt
    public long Received;
    public string FromKey = "";     // "puid#<sha256>" / "fc#<sha256>" / "name:..." of the sender (transport, not payload)
    public long Edited;             // unix seconds of the last edit, 0 = never edited (2026-10-28: notes can be edited later)
}

internal static class FeedbackCategories
{
    // index = wire value, never reorder; only append
    internal static readonly string[] Keys = { "bug", "look", "balance", "idea", "other" };
    internal static string Key(byte c) => c < Keys.Length ? Keys[c] : "other";
}

internal static class FeedbackStore
{
    internal static string Dir { get; private set; } = "";
    private static string OutboxPath => Path.Combine(Dir, "outbox.json");
    private static string InboxPath => Path.Combine(Dir, "inbox.jsonl");
    private static string MinePath => Path.Combine(Dir, "mine.json");
    private const int MineMax = 50;

    private static readonly List<FeedbackNote> outbox = new();
    private static readonly List<FeedbackNote> mine = new();
    private static readonly Dictionary<string, long> inboxIds = new();   // id -> Edited
    private static readonly object fileLock = new();

    private static readonly JsonSerializerOptions Json = new()
    {
        IncludeFields = true,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,   // umlauts stay readable in the files
    };
    private static readonly JsonSerializerOptions JsonIndented = new()
    {
        IncludeFields = true,
        WriteIndented = true,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    internal static void Init()
    {
        Dir = Path.Combine(BepInEx.Paths.BepInExRootPath, "UnknownsFeedback");
        // the self test writes notes of its own: never into the real inbox/outbox
        if (FeedbackModule.DiagSelfTest?.Value == true) {
            Dir = Path.Combine(Dir, "selftest");
            try { if (Directory.Exists(Dir)) Directory.Delete(Dir, true); } catch { }
        }
        Directory.CreateDirectory(Dir);
        LoadOutbox();
        HashOldInboxKeys();
        LoadInboxIds();
        LoadMine();
    }

    // ---- own notes (for editing) ----

    internal static IReadOnlyList<FeedbackNote> Mine => mine;

    internal static void Remember(FeedbackNote n)
    {
        mine.RemoveAll(x => x.Id == n.Id);
        mine.Add(Copy(n));
        while (mine.Count > MineMax) mine.RemoveAt(0);
        WriteJson(MinePath, mine);
    }

    internal static FeedbackNote Copy(FeedbackNote n) =>
        JsonSerializer.Deserialize<FeedbackNote>(JsonSerializer.Serialize(n, Json), Json);

    private static void LoadMine()
    {
        try {
            if (!File.Exists(MinePath)) return;
            var list = JsonSerializer.Deserialize<List<FeedbackNote>>(File.ReadAllText(MinePath, Encoding.UTF8), Json);
            if (list != null) mine.AddRange(list);
        } catch (Exception e) { UsefulTORStuffPlugin.Logger.LogWarning($"[Feedback] mine.json unreadable: {e.Message}"); }
    }

    private static void WriteJson<T>(string path, T value)
    {
        lock (fileLock) {
            try {
                string tmp = path + ".tmp";
                File.WriteAllText(tmp, JsonSerializer.Serialize(value, JsonIndented), new UTF8Encoding(false));
                if (File.Exists(path)) File.Replace(tmp, path, null);
                else File.Move(tmp, path);
            } catch (Exception e) {
                UsefulTORStuffPlugin.Logger.LogError($"[Feedback] {Path.GetFileName(path)} save failed: {e.Message}");
            }
        }
    }

    internal static IReadOnlyList<FeedbackNote> Outbox => outbox;
    internal static int InboxCount => inboxIds.Count;
    internal static long InboxEdited(string id) => inboxIds.TryGetValue(id, out var e) ? e : -1;

    internal static string NewId()
    {
        var b = Guid.NewGuid().ToByteArray();
        var sb = new StringBuilder(12);
        for (int i = 0; i < 6; i++) sb.Append(b[i].ToString("x2"));
        return sb.ToString();
    }

    // ---- outbox (sender) ----

    internal static void AddToOutbox(FeedbackNote n)
    {
        outbox.RemoveAll(x => x.Id == n.Id);       // an edit of a note that is still waiting replaces it
        outbox.Add(n);
        SaveOutbox();
    }

    internal static bool RemoveFromOutbox(string id)
    {
        int removed = outbox.RemoveAll(n => n.Id == id);
        if (removed > 0) SaveOutbox();
        return removed > 0;
    }

    private static void LoadOutbox()
    {
        try {
            if (!File.Exists(OutboxPath)) return;
            var list = JsonSerializer.Deserialize<List<FeedbackNote>>(File.ReadAllText(OutboxPath, Encoding.UTF8), Json);
            if (list != null) outbox.AddRange(list);
            if (outbox.Count > 0) UsefulTORStuffPlugin.Logger.LogInfo($"[Feedback] outbox: {outbox.Count} note(s) waiting for delivery");
        } catch (Exception e) {
            // keep the broken file for a look instead of overwriting it with an empty list
            UsefulTORStuffPlugin.Logger.LogError($"[Feedback] outbox unreadable, kept as outbox.broken.json: {e.Message}");
            try { File.Copy(OutboxPath, Path.Combine(Dir, "outbox.broken.json"), true); } catch { }
        }
    }

    private static void SaveOutbox()
    {
        lock (fileLock) {
            try {
                string tmp = OutboxPath + ".tmp";
                File.WriteAllText(tmp, JsonSerializer.Serialize(outbox, JsonIndented), new UTF8Encoding(false));
                if (File.Exists(OutboxPath)) File.Replace(tmp, OutboxPath, null);
                else File.Move(tmp, OutboxPath);
            } catch (Exception e) {
                UsefulTORStuffPlugin.Logger.LogError($"[Feedback] outbox save failed: {e.Message}");
            }
        }
    }

    // ---- inbox (author) ----

    internal static bool InInbox(string id) => inboxIds.ContainsKey(id);

    /// <summary>Appends a note to the inbox. False when its id is already there (resent after a lost ack), unless the note is
    /// a newer edit of it: then its line is replaced.</summary>
    internal static bool AddToInbox(FeedbackNote n)
    {
        if (string.IsNullOrEmpty(n.Id)) return false;
        if (inboxIds.TryGetValue(n.Id, out long edited)) return n.Edited > edited && ReplaceInInbox(n);
        lock (fileLock) {
            try {
                File.AppendAllText(InboxPath, JsonSerializer.Serialize(n, Json) + "\n", new UTF8Encoding(false));
            } catch (Exception e) {
                UsefulTORStuffPlugin.Logger.LogError($"[Feedback] inbox write failed: {e.Message}");
                return false;
            }
        }
        inboxIds[n.Id] = n.Edited;
        return true;
    }

    private static bool ReplaceInInbox(FeedbackNote n)
    {
        lock (fileLock) {
            try {
                var lines = File.Exists(InboxPath) ? File.ReadAllLines(InboxPath, Encoding.UTF8) : Array.Empty<string>();
                var outLines = new List<string>();
                foreach (var line in lines) {
                    if (string.IsNullOrWhiteSpace(line)) continue;
                    try {
                        var old = JsonSerializer.Deserialize<FeedbackNote>(line, Json);
                        if (old != null && old.Id == n.Id) continue;
                    } catch { }
                    outLines.Add(line);
                }
                outLines.Add(JsonSerializer.Serialize(n, Json));
                string tmp = InboxPath + ".tmp";
                File.WriteAllText(tmp, string.Join("\n", outLines) + "\n", new UTF8Encoding(false));
                if (File.Exists(InboxPath)) File.Replace(tmp, InboxPath, null); else File.Move(tmp, InboxPath);
            } catch (Exception e) {
                UsefulTORStuffPlugin.Logger.LogError($"[Feedback] inbox rewrite failed: {e.Message}");
                return false;
            }
        }
        inboxIds[n.Id] = n.Edited;
        return true;
    }

    /// <summary>Inboxes from before 2026-10-30 keep the sender's PUID / friend code in plain text: rewritten once to the
    /// SHA-256 form FeedbackOwner.KeyOf uses now, so no plain account id stays on disk.</summary>
    private static void HashOldInboxKeys()
    {
        try {
            if (!File.Exists(InboxPath)) return;
            var lines = File.ReadAllLines(InboxPath, Encoding.UTF8);
            bool changed = false;
            var outLines = new List<string>(lines.Length);
            foreach (var line in lines) {
                if (string.IsNullOrWhiteSpace(line)) continue;
                string l = line;
                try {
                    var n = JsonSerializer.Deserialize<FeedbackNote>(line, Json);
                    if (n != null && n.FromKey != null && (n.FromKey.StartsWith("puid:") || n.FromKey.StartsWith("fc:"))) {
                        int c = n.FromKey.IndexOf(':');
                        n.FromKey = n.FromKey.Substring(0, c) + "#" + FeedbackOwner.Hash(n.FromKey.Substring(c + 1));
                        l = JsonSerializer.Serialize(n, Json);
                        changed = true;
                    }
                } catch { }
                outLines.Add(l);
            }
            if (!changed) return;
            lock (fileLock) {
                string tmp = InboxPath + ".tmp";
                File.WriteAllText(tmp, string.Join("\n", outLines) + "\n", new UTF8Encoding(false));
                File.Replace(tmp, InboxPath, null);
            }
            UsefulTORStuffPlugin.Logger.LogInfo("[Feedback] inbox: plain account ids replaced by hashes");
        } catch (Exception e) {
            UsefulTORStuffPlugin.Logger.LogWarning($"[Feedback] inbox key migration skipped: {e.Message}");
        }
    }

    private static void LoadInboxIds()
    {
        try {
            if (!File.Exists(InboxPath)) return;
            foreach (var line in File.ReadAllLines(InboxPath, Encoding.UTF8)) {
                if (string.IsNullOrWhiteSpace(line)) continue;
                try {
                    var n = JsonSerializer.Deserialize<FeedbackNote>(line, Json);
                    if (n != null && !string.IsNullOrEmpty(n.Id)) inboxIds[n.Id] = n.Edited;
                } catch { /* a damaged line does not block the rest */ }
            }
        } catch (Exception e) {
            UsefulTORStuffPlugin.Logger.LogError($"[Feedback] inbox unreadable: {e.Message}");
        }
    }
}
