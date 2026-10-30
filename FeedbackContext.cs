// Useful TOR Stuff - Copyright (C) 2026 DaUnknown-0
// Feedback module, taken over from the standalone Unknown's Feedback (2026-10-30).
// Licensed under GPL-3.0-or-later. See LICENSE for details.

/*
 * FeedbackContext - where and when a note is written: map key, phase, ghost, round clock.
 *
 * Map keys match the picture folders tools/build_uebersicht.py knows: the vanilla maps by the ship's
 * object name (MapId as fallback), custom maps by the AppDomain entry "UnknownsAtlas.ActiveMap" that
 * Atlas sets while one of its maps stands (other map mods may set it as well).
 */

using System;
using UnityEngine;

namespace UsefulTORStuff;

internal static class FeedbackContext
{
    private static ShipStatus roundShip;
    private static float roundStart = -1f;

    /// <summary>Called every frame: notices a new round to start its clock.</summary>
    internal static void Tick()
    {
        var ship = ShipStatus.Instance;
        if (ship == null) { roundShip = null; roundStart = -1f; return; }
        if (ship != roundShip) { roundShip = ship; roundStart = Time.time; }
    }

    internal static string MapKey()
    {
        var ship = ShipStatus.Instance;
        if (ship == null) return LobbyBehaviour.Instance != null ? "lobby" : "menu";
        try {
            var atlas = AppDomain.CurrentDomain.GetData("UnknownsAtlas.ActiveMap") as string;
            if (!string.IsNullOrEmpty(atlas)) return atlas.ToLowerInvariant();
        } catch { }
        string n = (ship.name ?? "").ToLowerInvariant();
        if (n.Contains("submerged")) return "submerged";
        if (n.Contains("dleks")) return "dleks";
        if (n.Contains("skeld")) return "skeld";
        if (n.Contains("mira")) return "mira";
        if (n.Contains("polus")) return "polus";
        if (n.Contains("airship")) return "airship";
        if (n.Contains("fungle")) return "fungle";
        try {
            switch (GameOptionsManager.Instance.CurrentGameOptions.MapId) {
                case 0: return "skeld";
                case 1: return "mira";
                case 2: return "polus";
                case 3: return "dleks";
                case 4: return "airship";
                case 5: return "fungle";
                case 6: return "submerged";
            }
        } catch { }
        return n;
    }

    internal static string Phase()
    {
        if (ShipStatus.Instance == null) return "lobby";
        if (MeetingHud.Instance != null || ExileController.Instance != null) return "meeting";
        return "round";
    }

    internal static bool Ghost()
    {
        try { return PlayerControl.LocalPlayer?.Data != null && PlayerControl.LocalPlayer.Data.IsDead; }
        catch { return false; }
    }

    internal static float RoundSeconds() => roundStart < 0f ? -1f : Time.time - roundStart;
}
