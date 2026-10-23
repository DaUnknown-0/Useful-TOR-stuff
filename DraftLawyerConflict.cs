// Useful TOR Stuff - Copyright (C) 2026 DaUnknown-0
// Licensed under GPL-3.0-or-later. See LICENSE for details.
// Based on The Other Roles (https://github.com/TheOtherRolesAU/TheOtherRoles), GPL-3.0.

/*
 * DraftLawyerConflict - the role draft let a second player take Prosecutor after somebody had
 * taken Lawyer (or the other way round), and the second pick overwrote the first.
 *
 * Why: in TOR the Prosecutor is not a role of its own but the Lawyer with a flag. setRole puts
 * BOTH into the single field Lawyer.lawyer (Prosecutor also sets Lawyer.isProsecutor), so the
 * second pick silently took the role away from the first player and flipped him to "no role".
 * The draft only locks a role that is already in alreadyPicked by its OWN id (RoleDraft.cs:135),
 * and Lawyer and Prosecutor have different ids.
 *
 * Host option 1284 under the Lawyer ("Draft: Lawyer/Prosecutor Already Picked"):
 *   0 Not Offered        - the other one is not offered any more. Done with TOR's own filter
 *                          (RoleDraft.cs:132/133): while the draft runs and one of the two is taken,
 *                          lawyerIsProsecutorChance.selection is held LOCALLY at 0 (Lawyer taken ->
 *                          no Prosecutor) or 10 (Prosecutor taken -> no Lawyer). Nothing is counted
 *                          twice and the choice list stays full. The real value comes back the
 *                          moment the draft ends; it is never saved or shared.
 *   1 Random Other Role  - it is still offered; who picks it gets one of the OTHER roles on his
 *                          buttons instead (Crewmate if there is none).
 *   2 Crewmate + Guesser - it is still offered; who picks it becomes a Crewmate and the host
 *                          makes him a Guesser on top of the normal guesser count. Only in Guesser
 *                          mode; in any other mode this behaves like "Not Offered".
 * Modes 1 and 2 swap the role in a prefix on RoleDraft.sendPick, on the picker's own client,
 * before TOR's DraftModePick RPC goes out: everybody then receives the replacement, the feed
 * shows it, and both a click and TOR's timeout pick go through there. Mode 2 also tells the
 * host (module 236 on channel 240) who is owed a Guesser; the host hands it out when the intro
 * ends, after TOR's own guesser assignment (RoleDraft.cs:309), with TOR's SetGuesserGm RPC.
 *
 * Tiering (memory: Harmony detours on hot managed TOR methods die silently): no patch on the
 * per-frame draft code. The only TOR patch is sendPick (one call per player and round); the
 * rest runs in a HudManager.Update postfix (an Il2Cpp game method) and reads RoleDraft by
 * reflection. Gated on the host having UTS (UTSGate); without it TOR's behaviour stays.
 */

using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using HarmonyLib;
using Hazel;
using UnityEngine;
using TheOtherRoles;
using TheOtherRoles.Utilities;
using Types = TheOtherRoles.CustomOption.CustomOptionType;

namespace UsefulTORStuff {
    public static class DraftLawyerConflict {
        public const byte GuesserGrantRpcId = 236;
        private const int ModeHide = 0, ModeRandom = 1, ModeCrewGuesser = 2;

        public static CustomOption Mode;

        private static FieldInfo isRunningField, alreadyPickedField, buttonsField;
        private static bool resolved;

        // the real lawyerIsProsecutorChance while it is held for "Not Offered" (-1 = not held)
        private static int heldOriginal = -1;
        // host: players owed a Guesser (mode 2), handed out when the intro ends
        private static readonly HashSet<byte> guesserGrants = new HashSet<byte>();

        public static void CreateOptions() {
            try {
                Mode = CustomOption.Create(1284, Types.Neutral, "Draft: Lawyer/Prosecutor Already Picked",
                    new string[] { "Not Offered", "Random Other Role", "Crewmate + Guesser" },
                    CustomOptionHolder.lawyerSpawnRate);
                UTSLocalization.BindOptionTitle(Mode, "uts.draftlawyer.option");
                UTSLocalization.BindOptionSelections(Mode,
                    "uts.draftlawyer.mode_hide", "uts.draftlawyer.mode_random", "uts.draftlawyer.mode_guesser");

                // right after the Lawyer's other UTS options (LawyerLoverTracker), else after TOR's last Lawyer option
                var opts = CustomOption.options;
                opts.Remove(Mode);
                int idx = LawyerLoverTracker.LawyerMeeting != null ? opts.IndexOf(LawyerLoverTracker.LawyerMeeting) : -1;
                if (idx < 0) idx = opts.IndexOf(CustomOptionHolder.lawyerCanCallEmergency);
                if (idx < 0) idx = opts.Count - 1;
                opts.Insert(idx + 1, Mode);
            } catch (Exception e) {
                UsefulTORStuffPlugin.Logger?.LogError($"[DraftLawyer] option creation failed: {e}");
            }
        }

        public static void RegisterRpc() => UTSRpc.Register(GuesserGrantRpcId, HandleGrant);

        public static void TryPatch(Harmony harmony) {
            try {
                var draft = typeof(CustomOption).Assembly.GetType("TheOtherRoles.Modules.RoleDraft");
                const BindingFlags all = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static;
                isRunningField = draft?.GetField("isRunning", all);
                alreadyPickedField = draft?.GetField("alreadyPicked", all);
                buttonsField = draft?.GetField("buttons", all);
                var sendPick = draft?.GetMethod("sendPick", all);
                resolved = isRunningField != null && alreadyPickedField != null && buttonsField != null && sendPick != null;
                if (!resolved) {
                    UsefulTORStuffPlugin.Logger?.LogWarning("[DraftLawyer] RoleDraft handles missing (isRunning=" + (isRunningField != null)
                        + ", alreadyPicked=" + (alreadyPickedField != null) + ", buttons=" + (buttonsField != null)
                        + ", sendPick=" + (sendPick != null) + ") - Lawyer/Prosecutor stay TOR's in the draft.");
                    return;
                }
                harmony.Patch(sendPick, prefix: new HarmonyMethod(typeof(DraftLawyerConflict), nameof(SendPickPrefix)));
                UsefulTORStuffPlugin.Logger?.LogInfo("[DraftLawyer] role draft patched.");
            } catch (Exception e) {
                UsefulTORStuffPlugin.Logger?.LogError($"[DraftLawyer] patch failed: {e}");
            }
        }

        // ---- state ----

        private static bool DraftRunning() {
            try { return resolved && (bool)isRunningField.GetValue(null); } catch { return false; }
        }

        private static List<byte> Picked() {
            try { return alreadyPickedField.GetValue(null) as List<byte>; } catch { return null; }
        }

        // the one of the two that is already taken in this draft (null = neither)
        private static RoleId? Taken() {
            var picked = Picked();
            if (picked == null) return null;
            if (picked.Contains((byte)RoleId.Lawyer)) return RoleId.Lawyer;
            if (picked.Contains((byte)RoleId.Prosecutor)) return RoleId.Prosecutor;
            return null;
        }

        private static int EffectiveMode() {
            if (Mode == null || !UTSGate.SettingsActive) return -1;
            int m = UTSGate.Sel(Mode);
            if (m == ModeCrewGuesser && !GuesserMode()) m = ModeHide;
            return m;
        }

        private static bool GuesserMode() => SettingsOverlayView.TorGameMode() == CustomGamemodes.Guesser;

        // TOR's CustomRPC is internal: the SetGuesserGm byte by name, resolved once (0 = not found)
        private static byte setGuesserGmId;
        private static bool setGuesserGmResolved;
        private static byte SetGuesserGmId() {
            if (setGuesserGmResolved) return setGuesserGmId;
            setGuesserGmResolved = true;
            try {
                var e = typeof(CustomOption).Assembly.GetType("TheOtherRoles.CustomRPC");
                if (e != null && Enum.IsDefined(e, "SetGuesserGm")) setGuesserGmId = Convert.ToByte(Enum.Parse(e, "SetGuesserGm"));
            } catch { }
            if (setGuesserGmId == 0) UsefulTORStuffPlugin.Logger?.LogWarning("[DraftLawyer] CustomRPC.SetGuesserGm not found - no Guesser on top.");
            return setGuesserGmId;
        }

        // ---- "Not Offered": hold TOR's prosecutor chance while the draft runs ----

        [HarmonyPatch(typeof(HudManager), nameof(HudManager.Update))]
        static class TickPatch {
            public static void Postfix() {
                try {
                    if (!resolved) return;
                    bool running = DraftRunning();
                    var taken = running ? Taken() : null;
                    if (running && taken.HasValue && EffectiveMode() == ModeHide) Hold(taken.Value == RoleId.Lawyer ? 0 : 10);
                    else Release();
                } catch (Exception e) {
                    Release();
                    UsefulTORStuffPlugin.Logger?.LogWarning($"[DraftLawyer] tick failed: {e.Message}");
                }
            }
        }

        private static void Hold(int value) {
            var o = CustomOptionHolder.lawyerIsProsecutorChance;
            if (o == null) return;
            if (heldOriginal < 0) {
                heldOriginal = o.selection;
                UsefulTORStuffPlugin.Logger?.LogInfo($"[DraftLawyer] {(value == 0 ? "Lawyer" : "Prosecutor")} taken: the other one is not offered any more.");
            }
            o.selection = value;
        }

        private static void Release() {
            if (heldOriginal < 0) return;
            var o = CustomOptionHolder.lawyerIsProsecutorChance;
            if (o != null) o.selection = heldOriginal;
            heldOriginal = -1;
        }

        // ---- "Random Other Role" / "Crewmate + Guesser": swap the pick before it goes out ----

        public static void SendPickPrefix(ref byte RoleId) {
            try {
                if (!DraftRunning()) return;
                var taken = Taken();
                if (!taken.HasValue) return;
                var other = taken.Value == TheOtherRoles.RoleId.Lawyer ? TheOtherRoles.RoleId.Prosecutor : TheOtherRoles.RoleId.Lawyer;
                if (RoleId != (byte)other) return;
                int mode = EffectiveMode();
                if (mode < 0) return;
                byte before = RoleId;
                if (mode == ModeCrewGuesser) {
                    RoleId = (byte)TheOtherRoles.RoleId.Crewmate;
                    SendGrant();
                } else {
                    // ModeRandom, and ModeHide as a safety net (the hold should have kept it off the buttons)
                    RoleId = RandomOtherOnButtons(other);
                }
                UsefulTORStuffPlugin.Logger?.LogInfo($"[DraftLawyer] pick {(TheOtherRoles.RoleId)before} while {taken.Value} is taken (mode {mode}): now {(TheOtherRoles.RoleId)RoleId}.");
            } catch (Exception e) {
                UsefulTORStuffPlugin.Logger?.LogError($"[DraftLawyer] pick swap failed: {e}");
            }
        }

        // a random role among the picker's other buttons (TOR already checked their limits), else Crewmate
        private static byte RandomOtherOnButtons(RoleId other) {
            var shown = new List<RoleInfo>();
            if (buttonsField.GetValue(null) is IList buttons) {
                foreach (var o in buttons) {
                    var b = o as ActionButton;
                    if (b == null) continue;
                    var tmp = b.GetComponentInChildren<TMPro.TextMeshPro>(true);
                    if (tmp == null) continue;
                    string text = tmp.text ?? "";
                    var info = RoleInfo.allRoleInfos.FirstOrDefault(r => r.name != null && r.name.Replace(" ", "\n") == text);
                    if (info == null || info.roleId == other || info.roleId == TheOtherRoles.RoleId.Lawyer
                        || info.roleId == TheOtherRoles.RoleId.Prosecutor || shown.Contains(info)) continue;
                    shown.Add(info);
                }
            }
            if (shown.Count == 0) return (byte)TheOtherRoles.RoleId.Crewmate;
            return (byte)shown[UnityEngine.Random.Range(0, shown.Count)].roleId;
        }

        // ---- the Guesser on top (mode 2) ----

        private static void SendGrant() {
            var lp = PlayerControl.LocalPlayer;
            if (lp == null) return;
            if (AmongUsClient.Instance != null && AmongUsClient.Instance.AmHost) { guesserGrants.Add(lp.PlayerId); return; }
            if (!UTSRpc.CanSend) return;
            var w = UTSRpc.Begin(GuesserGrantRpcId);
            AmongUsClient.Instance.FinishRpcImmediately(w);
        }

        // picker -> host, no payload: the grant is always for the sender himself
        private static void HandleGrant(MessageReader reader) {
            var sender = UTSRpc.Sender;
            if (sender == null || AmongUsClient.Instance == null || !AmongUsClient.Instance.AmHost) return;
            if (!DraftRunning() || EffectiveMode() != ModeCrewGuesser) return;
            guesserGrants.Add(sender.PlayerId);
            UsefulTORStuffPlugin.Logger?.LogInfo($"[DraftLawyer] player {sender.PlayerId} is owed a Guesser.");
        }

        [HarmonyPatch(typeof(IntroCutscene), nameof(IntroCutscene.OnDestroy))]
        static class IntroEndPatch {
            public static void Postfix() {
                Release();
                if (guesserGrants.Count == 0) return;
                try {
                    if (AmongUsClient.Instance == null || !AmongUsClient.Instance.AmHost || !GuesserMode()) return;
                    byte rpc = SetGuesserGmId();
                    if (rpc == 0) return;
                    foreach (byte id in guesserGrants) {
                        var p = Helpers.playerById(id);
                        if (p == null || p.Data == null || p.Data.Disconnected || HandleGuesser.isGuesser(id)) continue;
                        MessageWriter w = AmongUsClient.Instance.StartRpcImmediately(PlayerControl.LocalPlayer.NetId,
                            rpc, SendOption.Reliable, -1);
                        w.Write(id);
                        AmongUsClient.Instance.FinishRpcImmediately(w);
                        RPCProcedure.setGuesserGm(id);
                        UsefulTORStuffPlugin.Logger?.LogInfo($"[DraftLawyer] player {id} is a Guesser on top (Crewmate + Guesser).");
                    }
                } catch (Exception e) {
                    UsefulTORStuffPlugin.Logger?.LogError($"[DraftLawyer] guesser grant failed: {e}");
                } finally {
                    guesserGrants.Clear();
                }
            }
        }

        [HarmonyPatch(typeof(AmongUsClient), nameof(AmongUsClient.OnGameJoined))]
        static class JoinPatch {
            public static void Postfix() { Release(); guesserGrants.Clear(); }
        }
    }
}
