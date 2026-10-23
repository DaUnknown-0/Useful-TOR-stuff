// Useful TOR Stuff - Copyright (C) 2026 DaUnknown-0
// Licensed under GPL-3.0-or-later. See LICENSE for details.
// Based on The Other Roles (https://github.com/TheOtherRolesAU/TheOtherRoles), GPL-3.0.

/*
 * EasterEggCoding - a small easter egg for the mod's author (User 23.10.).
 *
 * If the author dies in the first round of a game (killed before the first meeting is over, or
 * voted out in that first meeting), his name is replaced by an animated "Coding..." for the rest of
 * the game: over his ghost and on his meeting plate. Voted out in that first meeting, the exile line
 * reads "<name> continues to code the mod." instead of the usual one.
 *
 * Who the author is: SHA-256 of his account's PUID (or, without one, of his friend code), so the id
 * itself is not in this public source. Every client with this mod recognises him on its own; nothing
 * is sent. Purely cosmetic: no role, no win, no vote is touched.
 *
 * "First round" ends when the first meeting's exile screen is gone (MeetingHud and ExileController
 * both gone after having been there). Names are written after TOR's own per-frame name pass
 * (HudManager.Update / MeetingHud.Update postfixes, Priority.Last); the exile line in a
 * TranslationController.GetString postfix (Priority.Last, after TOR's role line) plus completeString.
 */

using System;
using System.Security.Cryptography;
using System.Text;
using HarmonyLib;
using Il2CppInterop.Runtime.InteropTypes.Arrays;
using UnityEngine;

namespace UsefulTORStuff {
    public static class EasterEggCoding {
        private static readonly string[] OwnerHashes = {
            "d10f0e308524211c45d93ec3300efca28e35488ea35598196c67b1ababb7fc19",   // PUID
            "cf2eab42fba217e7af4237f7a3bcd35402f2790602e07d92c6344096701480c6",   // friend code
        };

        private static ShipStatus game;          // the game the state below belongs to
        private static byte owner = 255;         // the author's PlayerId in this game (255 = not here)
        private static float nextOwnerLookup;
        private static bool inMeeting;
        private static int meetingsDone;
        private static bool coding;              // he died in the first round: "Coding..." from now on
        private static bool exileLine;           // the exile screen showing him right now gets our line
        private static string exileName = "";

        internal static bool IsOwner(PlayerControl p) {
            try {
                if (p == null || p.Data == null) return false;
                if (diagForceLocal && p == PlayerControl.LocalPlayer) return true;
                return Matches(p.Data.Puid) || Matches(p.Data.FriendCode);
            } catch { return false; }
        }

        // ---- autotest (Diagnostics "EasterEgg Test", freeplay only): eject self, shot of the line, shot of the ghost ----
        internal static BepInEx.Configuration.ConfigEntry<bool> DiagTest;
        private static bool diagForceLocal;
        private static float diagStart = -1f;
        private static int diagStep;

        private static void DiagTick() {
            if (DiagTest == null || !DiagTest.Value || ShipStatus.Instance == null || PlayerControl.LocalPlayer == null) return;
            if (diagStart < 0f) { diagStart = Time.time; diagStep = 0; return; }
            float t = Time.time - diagStart;
            var lp = PlayerControl.LocalPlayer;
            string shots = BepInEx.Paths.BepInExRootPath;
            if (diagStep == 0 && t > 10f) {
                diagStep = 1;
                bool real = Matches(lp.Data.Puid) || Matches(lp.Data.FriendCode);
                UsefulTORStuffPlugin.Logger?.LogInfo($"[EasterEgg] diag: local player recognised by id: {real}");
                if (!real) diagForceLocal = true;
                owner = 255; nextOwnerLookup = 0f;
                var ship = ShipStatus.Instance;
                var ec = UnityEngine.Object.Instantiate(ship.ExileCutscenePrefab);
                ec.transform.SetParent(HudManager.Instance.transform, false);
                ec.transform.localPosition = new Vector3(0f, 0f, -60f);
                HudManager.Instance.SetHudActive(false);
                ec.BeginForGameplay(lp.Data, false);
                UsefulTORStuffPlugin.Logger?.LogInfo($"[EasterEgg] diag: eject started, line '{ec.completeString}'");
            } else if (diagStep == 1 && t > 16f) {
                diagStep = 2;
                ScreenCapture.CaptureScreenshot(System.IO.Path.Combine(shots, "EasterEgg_exile.png"));
                UsefulTORStuffPlugin.Logger?.LogInfo($"[EasterEgg] diag: exile shot, text '{ExileController.Instance?.Text?.text}'");
            } else if (diagStep == 2 && t > 26f) {
                diagStep = 3;
                ScreenCapture.CaptureScreenshot(System.IO.Path.Combine(shots, "EasterEgg_ghost.png"));
                UsefulTORStuffPlugin.Logger?.LogInfo($"[EasterEgg] diag: ghost shot, dead {lp.Data.IsDead}, coding {coding}, name '{lp.cosmetics?.nameText?.text}'");
            } else if (diagStep == 3 && t > 28f) {
                diagStep = 4;
                UsefulTORStuffPlugin.Logger?.LogInfo("[EasterEgg] diag: all done");
            }
        }

        private static bool Matches(string id) {
            if (string.IsNullOrEmpty(id)) return false;
            using var sha = SHA256.Create();
            var hash = sha.ComputeHash(Encoding.UTF8.GetBytes(id));
            var sb = new StringBuilder(64);
            foreach (byte b in hash) sb.Append(b.ToString("x2"));
            string h = sb.ToString();
            foreach (var o in OwnerHashes) if (o == h) return true;
            return false;
        }

        private static PlayerControl Owner() => owner == 255 ? null : TheOtherRoles.Helpers.playerById(owner);

        // "Coding" plus 0-3 dots; the missing dots stay as invisible characters so the text keeps its width
        private static string CodingText() {
            int n = (int)(Time.time * 2.5f) % 4;
            return "Coding" + new string('.', n) + (n < 3 ? "<alpha=#00>" + new string('.', 3 - n) : "");
        }

        private static void ResetFor(ShipStatus ship) {
            game = ship; owner = 255; nextOwnerLookup = 0f;
            inMeeting = false; meetingsDone = 0; coding = false; exileLine = false; exileName = "";
        }

        // ---- per frame: round tracking, death, name over the ghost ----

        [HarmonyPatch(typeof(HudManager), nameof(HudManager.Update))]
        static class HudPatch {
            [HarmonyPriority(Priority.Last)]
            public static void Postfix() {
                try { DiagTick(); } catch (Exception e) { UsefulTORStuffPlugin.Logger?.LogWarning($"[EasterEgg] diag: {e.Message}"); }
                try {
                    var ship = ShipStatus.Instance;
                    if (ship == null) { if (game != null) ResetFor(null); return; }
                    if (ship != game) ResetFor(ship);

                    // the author's PlayerId, looked up now and then (players can still be arriving at the start)
                    if (owner == 255 && Time.time >= nextOwnerLookup) {
                        nextOwnerLookup = Time.time + 2f;
                        foreach (var p in PlayerControl.AllPlayerControls)
                            if (IsOwner(p)) { owner = p.PlayerId; break; }
                    }
                    var me = Owner();
                    if (me == null || me.Data == null) return;

                    // death first, then the round count: a death on the exile screen still counts as round one
                    if (!coding && me.Data.IsDead && meetingsDone == 0) {
                        coding = true;
                        UsefulTORStuffPlugin.Logger?.LogInfo("[EasterEgg] the author died in round one: Coding...");
                    }
                    bool meeting = MeetingHud.Instance != null || ExileController.Instance != null;
                    if (inMeeting && !meeting) { meetingsDone++; exileLine = false; }
                    inMeeting = meeting;

                    if (coding && me.Data.IsDead && me.cosmetics != null && me.cosmetics.nameText != null)
                        me.cosmetics.nameText.text = CodingText();
                } catch { }
            }
        }

        // ---- meeting plate ----

        [HarmonyPatch(typeof(MeetingHud), nameof(MeetingHud.Update))]
        static class MeetingPatch {
            [HarmonyPriority(Priority.Last)]
            public static void Postfix(MeetingHud __instance) {
                try {
                    if (!coding || owner == 255 || __instance == null || __instance.playerStates == null) return;
                    foreach (var pva in __instance.playerStates)
                        if (pva != null && pva.TargetPlayerId == owner && pva.NameText != null) pva.NameText.text = CodingText();
                } catch { }
            }
        }

        // ---- exile line ----

        [HarmonyPatch(typeof(ExileController), nameof(ExileController.BeginForGameplay))]
        static class ExileBeginPatch {
            // before vanilla builds its line, so the GetString postfix below already sees the flag
            public static void Prefix([HarmonyArgument(0)] NetworkedPlayerInfo exiled) {
                try {
                    exileLine = false;
                    if (exiled == null || meetingsDone != 0 || exiled.Object == null || !IsOwner(exiled.Object)) return;
                    exileLine = true;
                    exileName = exiled.PlayerName ?? "";
                    coding = true;    // voted out in round one: the name codes on as well
                    UsefulTORStuffPlugin.Logger?.LogInfo("[EasterEgg] the author was voted out in round one.");
                } catch { }
            }

            [HarmonyPriority(Priority.Last)]
            public static void Postfix(ExileController __instance) {
                try { if (exileLine && __instance != null) __instance.completeString = Line(); } catch { }
            }
        }

        private static string Line() => $"{exileName} continues to code the mod.";

        [HarmonyPatch(typeof(TranslationController), nameof(TranslationController.GetString),
            new Type[] { typeof(StringNames), typeof(Il2CppReferenceArray<Il2CppSystem.Object>) })]
        static class ExileTextPatch {
            [HarmonyPriority(Priority.Last)]
            public static void Postfix(ref string __result, [HarmonyArgument(0)] StringNames id) {
                if (!exileLine) return;
                if (id == StringNames.ExileTextPN || id == StringNames.ExileTextSN || id == StringNames.ExileTextPP
                    || id == StringNames.ExileTextSP || id == StringNames.ExileTextNonConfirm)
                    __result = Line();
            }
        }

        [HarmonyPatch(typeof(AmongUsClient), nameof(AmongUsClient.OnGameJoined))]
        static class JoinPatch {
            public static void Postfix() => ResetFor(null);
        }
    }
}
