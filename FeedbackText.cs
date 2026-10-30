// Useful TOR Stuff - Copyright (C) 2026 DaUnknown-0
// Feedback module, taken over from the standalone Unknown's Feedback (2026-10-30).
// Licensed under GPL-3.0-or-later. See LICENSE for details.

/*
 * FeedbackText - the few lines the mod shows, German or English after the game's language.
 */

using System.Collections.Generic;

namespace UsefulTORStuff;

internal static class FeedbackText
{
    private static readonly Dictionary<string, (string De, string En)> Lines = new()
    {
        ["title"] = ("Feedback", "Feedback"),
        ["where.0"] = ("{0}  ·  {1}  ·  deine Position {2:0.0} | {3:0.0}", "{0}  ·  {1}  ·  your position {2:0.0} | {3:0.0}"),
        ["where.1"] = ("{0}  ·  {1}  ·  gewählter Punkt {2:0.0} | {3:0.0}", "{0}  ·  {1}  ·  picked point {2:0.0} | {3:0.0}"),
        ["where.2"] = ("{0}  ·  {1}  ·  Bereich {4:0.0} × {5:0.0} m um {2:0.0} | {3:0.0}", "{0}  ·  {1}  ·  area {4:0.0} × {5:0.0} m around {2:0.0} | {3:0.0}"),
        ["loc"] = ("Ort:", "Place:"),
        ["loc.here"] = ("Hier", "Here"),
        ["loc.point"] = ("Punkt wählen", "Pick a point"),
        ["loc.area"] = ("Bereich ziehen", "Drag an area"),
        ["pick.point"] = ("Klicke auf den Ort, im Spielbild oder auf der Karte", "Click the spot, in the game view or on the map"),
        ["pick.area"] = ("Ziehe einen Rahmen um den Bereich, im Spielbild oder auf der Karte", "Drag a frame around the area, in the game view or on the map"),
        ["pick.esc"] = ("Esc zurück", "Esc back"),
        ["pick.map_open"] = ("Karte öffnen", "Open map"),
        ["pick.map_close"] = ("Karte schließen", "Close map"),
        ["pick.failed"] = ("Ort konnte nicht bestimmt werden", "Could not work out that spot"),
        ["mic"] = ("Diktat (2×)", "Dictate (2×)"),
        ["mic.confirm"] = ("Nochmals klicken", "Click again"),
        ["mic.on"] = ("Hört zu …", "Listening …"),
        ["mic.none"] = ("Kein Diktat", "No dictation"),
        ["mic.unavailable"] = ("Windows hat keine Spracherkennung installiert. Win+H (Windows-Diktat) probieren.",
                               "Windows has no speech recognizer installed. Try Win+H (Windows voice typing)."),
        ["mic.failed"] = ("Mikrofon nicht verfügbar: {0}", "Microphone not available: {0}"),
        ["placeholder"] = ("Was soll hier anders sein?", "What should be different here?"),
        ["hint"] = ("Strg+Enter speichern  ·  Enter neue Zeile  ·  Pfeiltasten bewegen  ·  Strg+V einfügen  ·  Esc abbrechen",
                    "Ctrl+Enter save  ·  Enter new line  ·  arrow keys move  ·  Ctrl+V paste  ·  Esc cancel"),
        ["short"] = ("Sehr kurz (unter {0} Zeichen). Zum Senden noch einmal Speichern drücken.",
                     "Very short (under {0} characters). Press Save again to send it."),
        ["earlier"] = ("Frühere", "Earlier"),
        ["new"] = ("Neu", "New"),
        ["earlier.none"] = ("Noch keine eigenen Notizen", "No notes of your own yet"),
        ["editing"] = ("Bearbeitung {0}/{1}", "editing {0}/{1}"),
        ["received.edit"] = ("Feedback von {0} geändert: {1}", "Feedback from {0} edited: {1}"),
        ["save"] = ("Speichern", "Save"),
        ["cancel"] = ("Abbrechen", "Cancel"),
        ["cat.bug"] = ("Bug", "Bug"),
        ["cat.look"] = ("Optik", "Looks"),
        ["cat.balance"] = ("Balance", "Balance"),
        ["cat.idea"] = ("Idee", "Idea"),
        ["cat.other"] = ("Sonstiges", "Other"),
        ["phase.lobby"] = ("Lobby", "lobby"),
        ["phase.round"] = ("Runde", "round"),
        ["phase.meeting"] = ("Meeting", "meeting"),
        ["phase.ghost"] = ("als Geist", "as a ghost"),
        ["saved.owner"] = ("Feedback gespeichert ({0} insgesamt)", "Feedback saved ({0} in total)"),
        ["saved.lobby"] = ("Feedback gespeichert, wird gleich zugestellt", "Feedback saved, will be delivered shortly"),
        ["saved.round"] = ("Feedback gespeichert, geht nach dem Spiel raus ({0} wartend)", "Feedback saved, goes out after the game ({0} waiting)"),
        ["delivered"] = ("{0} Feedback-Notiz(en) an {1} zugestellt", "{0} feedback note(s) delivered to {1}"),
        ["received"] = ("Feedback von {0} ({1}, {2}): {3}", "Feedback from {0} ({1}, {2}): {3}"),
        ["empty"] = ("Erst etwas schreiben", "Write something first"),
    };

    internal static bool German
    {
        get {
            try { return AmongUs.Data.DataManager.Settings.Language.CurrentLanguage == SupportedLangs.German; }
            catch { return true; }
        }
    }

    internal static string Get(string key, params object[] args)
    {
        if (!Lines.TryGetValue(key, out var l)) return key;
        string s = German ? l.De : l.En;
        if (args == null || args.Length == 0) return s;
        try { return string.Format(System.Globalization.CultureInfo.InvariantCulture, s, args); } catch { return s; }
    }

    internal static string Category(byte c) => Get("cat." + FeedbackCategories.Key(c));

    internal static string MapName(string key) => key switch
    {
        "skeld" => "The Skeld",
        "dleks" => "dlekS ehT",
        "mira" => "MIRA HQ",
        "polus" => "Polus",
        "airship" => "Airship",
        "fungle" => "The Fungle",
        "submerged" => "Submerged",
        "lobby" => "Lobby",
        "museum" => "Vesper Museum",
        "wald" => "Forest Station",
        "park" => "Moonlight Carnival",
        "train" => "Polaris Express",
        _ => OtherMapName(key),
    };

    /// <summary>Maps of other mods: the display name they publish under "UnknownsAtlas.ActiveMapName" while
    /// that map stands, else the key itself.</summary>
    private static string OtherMapName(string key)
    {
        if (string.IsNullOrEmpty(key)) return "?";
        try {
            if (System.AppDomain.CurrentDomain.GetData("UnknownsAtlas.ActiveMap") is string active
                && string.Equals(active, key, System.StringComparison.OrdinalIgnoreCase)
                && System.AppDomain.CurrentDomain.GetData("UnknownsAtlas.ActiveMapName") is string name
                && !string.IsNullOrEmpty(name))
                return name;
        } catch { }
        return key;
    }

    /// <summary>Player text shown in TMP: tags stay text.</summary>
    internal static string Plain(string s) => "<noparse>" + (s ?? "").Replace("</noparse>", "") + "</noparse>";
}
