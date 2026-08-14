// Useful TOR Stuff - Copyright (C) 2026 DaUnknown-0
// Licensed under GPL-3.0-or-later. See LICENSE for details.
// Based on The Other Roles (https://github.com/TheOtherRolesAU/TheOtherRoles), GPL-3.0.

/*
 * NewcomerShield - somebody joining your group for the first time cannot be killed before the first
 * meeting of their first round.
 *
 * Being murdered in the opening thirty seconds of the very first round you ever play, before anyone
 * has explained what a task even is, is the single worst first impression this game can make. This
 * gives newcomers exactly one round to find their feet, and it ends at the first meeting - not at
 * the end of the round - so it never decides a game.
 *
 * WHO IS "NEW"
 * The host keeps a set of FRIEND CODES that have already played a round with him. Friend codes are
 * the only identifier that survives a round and a lobby change; PlayerId and ClientId are both tied
 * to the connection and get reshuffled. The set lives in memory only: restarting the game forgets
 * everyone, which is exactly the intent ("new to this session"). The host can also mark people by
 * hand in the lobby, for the case the automatic rule does not catch (a friend who reinstalled, a
 * player everybody agrees should get a free round).
 *
 * WHY IT IS ENFORCED IN TWO PLACES
 * Among Us and TOR kill through two entirely different paths:
 *
 *  1. VANILLA impostor kills are genuinely host-authoritative: the killer's client sends
 *     CmdCheckMurder, the HOST runs CheckMurder and only then broadcasts the murder. A prefix there
 *     stops the kill even if the killer's client knows nothing about this feature. That is the hard
 *     guarantee, and it is the reason this lives in a host-side mod.
 *
 *  2. TOR ROLE kills (Sheriff, Jackal, Vampire, Werewolf, every Unknown's Collection role...) do NOT
 *     ask the host. They run Helpers.checkMuderAttempt on the KILLER's own client and then broadcast
 *     the result - the same path that carries the Medic shield and TOR's own "shield first kill".
 *     The host cannot intercept those without undoing a kill after the fact, so the rule is applied
 *     on every client that has this mod. In a lobby where everyone runs the mods that is complete.
 *
 * Either way the AUTHORITY over the list stays with the host: he decides who is shielded and hands
 * the ids out over RPC. No client can shield itself.
 *
 * WHAT THE SHIELD DELIBERATELY DOES NOT COVER
 * A shielded newcomer who is a LOVER still dies when their partner dies. TOR's lover cascade calls
 * MurderPlayer directly (PlayerControlPatch.cs:1226) and bypasses every check, and that is left
 * alone on purpose (decision 2026-08-14): the shield protects against being killed, not against the
 * bond itself. Suppressing it would mean a lover pair cannot die together in the first round, which
 * quietly guts the lovers' own rule for everybody else at the table.
 *
 * Options 1380-1381, module byte 242 on UTSRpc.CallId = 240. See ID-Registry.md.
 */

using System;
using System.Collections.Generic;
using System.Linq;
using HarmonyLib;
using Hazel;
using UnityEngine;
using TheOtherRoles;
using Types = TheOtherRoles.CustomOption.CustomOptionType;

namespace UsefulTORStuff {

    public static class NewcomerShield {

        // ---- Options (1380-1381) ----
        public static CustomOption Enabled;
        public static CustomOption NotifyKiller;

        // ---- Host state: survives rounds and lobbies, dies with the process ----
        // Friend codes that have already started a round with this host. "Session" is deliberately
        // the lifetime of the game process (user decision), so a restart gives everyone a fresh
        // round again.
        private static readonly HashSet<string> seenFriendCodes = new HashSet<string>();
        // Friend codes the host marked by hand in the lobby; they get the same one-round shield.
        private static readonly HashSet<string> manualNewcomers = new HashSet<string>();
        // ...and the opposite: people the host took the shield away from. Without this second set the
        // lobby button could not undo an AUTOMATIC detection at all - toggling a player the rule
        // already found would just add them to the manual set and leave them protected either way.
        private static readonly HashSet<string> manualExcluded = new HashSet<string>();

        // ---- Everyone: who is shielded in THIS round (player ids, valid until the first meeting) ----
        private static readonly HashSet<byte> shielded = new HashSet<byte>();

        private const byte RpcId = UsefulTORStuffPlugin.NewcomerShieldRpcId;
        private const byte SubSetShields = 0;   // count, ids...
        private const byte SubClear      = 1;

        public static bool Active => shielded.Count > 0;
        public static bool IsShielded(byte playerId) => shielded.Contains(playerId);
        public static IReadOnlyCollection<byte> Shielded => shielded;

        // The outline colour. Deliberately NOT one of TOR's: the Medic shield is cyan
        // (Color32(0,221,255), TheOtherRoles.cs:442) and "shield first kill" is plain blue
        // (PlayerControlPatch.cs). Gold is unmistakable next to both, and reads as "protected"
        // rather than "healed".
        public static readonly Color ShieldColor = new Color32(255, 205, 40, byte.MaxValue);

        // Players we painted ourselves, so the outline can be taken back off again when the shield
        // ends. Never touch anyone else's outline - TOR owns those.
        private static readonly HashSet<byte> outlined = new HashSet<byte>();

        public static void CreateOptions() {
            try {
                Enabled = CustomOption.Create(1380, Types.General,
                    "Protect Players New To This Session", false, null, true);
                NotifyKiller = CustomOption.Create(1381, Types.General,
                    "Tell The Killer Why The Kill Failed", true, Enabled);
                UTSLocalization.BindOptionTitle(Enabled, "uts.newcomershield.option_name");
                UTSLocalization.BindOptionTitle(NotifyKiller, "uts.newcomershield.option_notify");
                UsefulTORStuffPlugin.Logger?.LogInfo("[NewcomerShield] Options created.");
            } catch (Exception e) {
                UsefulTORStuffPlugin.Logger?.LogError($"[NewcomerShield] CreateOptions failed: {e}");
            }
        }

        public static void RegisterRpc() {
            UTSRpc.Register(RpcId, HandleModuleRpc);
        }

        // ---- helpers ----
        private static bool AmHost() => AmongUsClient.Instance != null && AmongUsClient.Instance.AmHost;

        // The identifier. Empty codes are treated as "not identifiable" and simply never shielded:
        // the alternative would be matching on the player NAME, which anybody can copy.
        public static string CodeOf(PlayerControl p) {
            try { return p?.Data?.FriendCode ?? ""; } catch { return ""; }
        }

        private static bool IsAlive(PlayerControl p) =>
            p != null && p.Data != null && !p.Data.IsDead && !p.Data.Disconnected;

        // Host-side view for the lobby panel: is this player currently going to be shielded next round?
        // The host's word beats the rule in both directions: an explicit exclusion wins over
        // everything, an explicit mark wins over "already seen".
        public static bool WouldShield(PlayerControl p) {
            if (Enabled == null || !Enabled.getBool()) return false;
            string code = CodeOf(p);
            if (string.IsNullOrEmpty(code)) return false;
            if (manualExcluded.Contains(code)) return false;
            return manualNewcomers.Contains(code) || !seenFriendCodes.Contains(code);
        }

        public static bool IsManual(PlayerControl p) {
            string code = CodeOf(p);
            return !string.IsNullOrEmpty(code) && manualNewcomers.Contains(code);
        }

        // Toggled from the lobby panel (host only).
        // Flips whatever the player's CURRENT state is, however it came about. Protected becomes
        // excluded, unprotected becomes marked - so the button always does what its label says.
        public static void ToggleManual(PlayerControl p) {
            string code = CodeOf(p);
            if (string.IsNullOrEmpty(code)) return;
            if (WouldShield(p)) {
                manualNewcomers.Remove(code);
                manualExcluded.Add(code);
            } else {
                manualExcluded.Remove(code);
                manualNewcomers.Add(code);
            }
        }

        // ---- RPC ----
        private static void SendSetShields(List<byte> ids) {
            try {
                MessageWriter w = UTSRpc.Begin(RpcId);
                w.Write(SubSetShields);
                w.Write((byte)Math.Min(ids.Count, 255));
                for (int i = 0; i < ids.Count && i < 255; i++) w.Write(ids[i]);
                AmongUsClient.Instance.FinishRpcImmediately(w);
                ApplySetShields(ids);
            } catch (Exception e) {
                UsefulTORStuffPlugin.Logger?.LogError($"[NewcomerShield] send failed: {e}");
            }
        }

        private static void SendClear() {
            try {
                MessageWriter w = UTSRpc.Begin(RpcId);
                w.Write(SubClear);
                AmongUsClient.Instance.FinishRpcImmediately(w);
                ApplyClear();
            } catch (Exception e) {
                UsefulTORStuffPlugin.Logger?.LogError($"[NewcomerShield] clear failed: {e}");
            }
        }

        private static void HandleModuleRpc(MessageReader reader) {
            try {
                byte sub = reader.ReadByte();
                switch (sub) {
                    case SubSetShields: {
                        int n = reader.ReadByte();
                        var ids = new List<byte>(n);
                        for (int i = 0; i < n; i++) ids.Add(reader.ReadByte());
                        // Host-authoritative: a forged message would let any client shield anyone.
                        if (UTSRpc.RequireHost("NewcomerShield.SetShields")) ApplySetShields(ids);
                        break;
                    }
                    case SubClear:
                        if (UTSRpc.RequireHost("NewcomerShield.Clear")) ApplyClear();
                        break;
                }
            } catch (Exception e) {
                UsefulTORStuffPlugin.Logger?.LogError($"[NewcomerShield] rpc failed: {e}");
            }
        }

        private static void ApplySetShields(List<byte> ids) {
            shielded.Clear();
            foreach (byte id in ids) shielded.Add(id);
            if (shielded.Count > 0)
                UsefulTORStuffPlugin.Logger?.LogInfo(
                    $"[NewcomerShield] {shielded.Count} player(s) shielded until the first meeting.");
        }

        private static void ApplyClear() => shielded.Clear();

        // ====================================================================
        // Host: decide at the start of the round
        // ====================================================================
        [HarmonyPatch(typeof(IntroCutscene), nameof(IntroCutscene.OnDestroy))]
        [HarmonyPriority(Priority.Low)]
        static class IntroEndPatch {
            public static void Postfix() {
                try {
                    if (!AmHost()) return;
                    if (Enabled == null || !Enabled.getBool()) return;

                    var ids = new List<byte>();
                    foreach (var p in PlayerControl.AllPlayerControls.ToArray()) {
                        if (!IsAlive(p)) continue;
                        string code = CodeOf(p);
                        if (string.IsNullOrEmpty(code)) continue;

                        bool isNew = manualNewcomers.Contains(code) || !seenFriendCodes.Contains(code);
                        if (isNew) ids.Add(p.PlayerId);

                        // Playing this round counts as having been here, whether shielded or not.
                        // The manual mark is one-shot for the same reason: the host ticked a box for
                        // THIS round, not forever.
                        seenFriendCodes.Add(code);
                        manualNewcomers.Remove(code);
                        manualExcluded.Remove(code);
                    }

                    if (ids.Count > 0) SendSetShields(ids);
                } catch (Exception e) {
                    UsefulTORStuffPlugin.Logger?.LogError($"[NewcomerShield] round start failed: {e}");
                }
            }
        }

        // The shield ends with the first meeting - not with the round. It buys a newcomer the opening
        // minutes, it never decides the game.
        [HarmonyPatch(typeof(MeetingHud), nameof(MeetingHud.Start))]
        static class MeetingStartPatch {
            public static void Postfix() {
                try {
                    if (shielded.Count == 0) return;
                    if (AmHost()) SendClear();
                    else shielded.Clear();   // clients drop it locally too, in case the RPC is lost
                } catch { }
            }
        }

        // ====================================================================
        // Enforcement 1: vanilla kills, host-authoritative
        //
        // CheckMurder runs on the HOST (the killer only sends CmdCheckMurder). Returning false here
        // stops the kill for a client that has never heard of this feature - the hard guarantee.
        // ====================================================================
        [HarmonyPatch(typeof(PlayerControl), nameof(PlayerControl.CheckMurder))]
        static class CheckMurderPatch {
            public static bool Prefix(PlayerControl __instance, [HarmonyArgument(0)] PlayerControl target) {
                try {
                    if (shielded.Count == 0 || target == null) return true;
                    if (!shielded.Contains(target.PlayerId)) return true;
                    UsefulTORStuffPlugin.Logger?.LogInfo(
                        $"[NewcomerShield] blocked a vanilla kill on {target.Data?.PlayerName} (shielded).");
                    return false;
                } catch { return true; }
            }
        }

        // ====================================================================
        // Enforcement 2: every TOR/UC role kill, on the killer's own client
        //
        // POSTFIX, not prefix: HarmonyX runs every prefix regardless of what the others return, so a
        // prefix that skips the original would still let another mod's prefix act on a kill we have
        // already refused. Overriding the RESULT afterwards is the pattern this codebase settled on
        // for overrides against TOR-patched methods. The original has no side effects on this path -
        // it only reaches its "PerformKill" default - so letting it run costs nothing.
        // ====================================================================
        [HarmonyPatch(typeof(Helpers), nameof(Helpers.checkMuderAttempt))]
        static class CheckMurderAttemptPatch {
            public static void Postfix(PlayerControl killer, PlayerControl target,
                                       ref MurderAttemptResult __result) {
                try {
                    if (shielded.Count == 0 || target == null) return;
                    if (__result == MurderAttemptResult.SuppressKill) return;   // already refused
                    if (!shielded.Contains(target.PlayerId)) return;

                    __result = MurderAttemptResult.SuppressKill;

                    // Feedback for the killer, on his own client only. Without it the button simply
                    // eats the cooldown and the round looks broken; with it he stops wasting attempts
                    // on someone he cannot kill anyway.
                    if (NotifyKiller != null && NotifyKiller.getBool()
                        && killer != null && PlayerControl.LocalPlayer != null
                        && killer.PlayerId == PlayerControl.LocalPlayer.PlayerId) {
                        var hud = HudManager.Instance;
                        if (hud != null && hud.Chat != null)
                            hud.Chat.AddChat(PlayerControl.LocalPlayer,
                                UTSLocalization.Tr("uts.newcomershield.kill_blocked"));
                    }
                } catch { }
            }
        }

        // ====================================================================
        // Enforcement 0: a shielded player cannot even be TARGETED
        //
        // Refusing the kill afterwards is not enough for every role, and the Thief is the proof: TOR
        // runs his suicide BEFORE it looks at the result (Buttons.cs, thiefKillButton - only
        // BlankKill returns early), so a Thief clicking a shielded newcomer would kill himself for a
        // kill that never happens. The same hole would cost an impostor his cooldown for nothing.
        //
        // TOR's own targeting helper takes an "untargetable" list, so the cleanest fix is to put
        // shielded players on it: no highlight, no click, for every role at once. The checks further
        // down stay as the safety net for roles that kill without targeting (the Guesser).
        // ====================================================================
        // The helper lives in PlayerControlFixedUpdatePatch, NOT PlayerControlPatch - looking for the
        // latter is why the first attempt logged "setTarget not found" and left shielded players
        // targetable. The class is public, so this is a plain attribute patch; the reflection
        // fallback below stays as a diagnostic for future TOR renames.
        [HarmonyPatch(typeof(TheOtherRoles.Patches.PlayerControlFixedUpdatePatch),
                      nameof(TheOtherRoles.Patches.PlayerControlFixedUpdatePatch.setTarget))]
        static class SetTargetPatch {
            public static void Prefix(ref List<PlayerControl> untargetablePlayers) =>
                SetTargetPrefix(ref untargetablePlayers);
        }

        private static void SetTargetPrefix(ref List<PlayerControl> untargetablePlayers) {
            try {
                if (shielded.Count == 0) return;
                var list = untargetablePlayers != null
                    ? new List<PlayerControl>(untargetablePlayers) : new List<PlayerControl>();
                foreach (byte id in shielded) {
                    var p = Helpers.playerById(id);
                    if (p != null && !list.Contains(p)) list.Add(p);
                }
                untargetablePlayers = list;
            } catch { }
        }

        // ====================================================================
        // The shield you can SEE
        //
        // Painted every frame on the player's own body sprite, exactly the way TOR paints the Medic
        // shield (material properties _Outline / _OutlineColor). Two rules keep it out of TOR's way:
        // we only ever paint players on OUR list, and we only ever clear an outline we painted
        // ourselves - otherwise this would fight the Medic shield and the role highlights.
        // ====================================================================
        [HarmonyPatch(typeof(PlayerControl), nameof(PlayerControl.FixedUpdate))]
        [HarmonyPriority(Priority.Low)]   // after TOR's own outline pass, so ours is the one that shows
        static class OutlinePatch {
            public static void Postfix(PlayerControl __instance) {
                try {
                    if (__instance == null) return;
                    var sprite = __instance.cosmetics?.currentBodySprite?.BodySprite;
                    if (sprite == null || sprite.material == null) return;

                    byte id = __instance.PlayerId;
                    bool shouldShow = shielded.Contains(id)
                                      && __instance.Data != null && !__instance.Data.IsDead;

                    if (shouldShow) {
                        sprite.material.SetFloat("_Outline", 1f);
                        sprite.material.SetColor("_OutlineColor", ShieldColor);
                        outlined.Add(id);
                    } else if (outlined.Remove(id)) {
                        // Ours to clear, and only ours. TOR repaints whatever it owns on its own pass.
                        sprite.material.SetFloat("_Outline", 0f);
                    }
                } catch { }
            }
        }

        // ====================================================================
        // Lobby preview
        //
        // The shield is decided at the start of the round, but seeing it only then is too late: the
        // point is that everybody - the newcomer included - knows about it BEFORE the round starts.
        // So the host broadcasts the same list already in the lobby and refreshes it whenever it
        // changes (someone joins or leaves, the host ticks a box, the option is switched).
        // ====================================================================
        private static float nextPreview;
        private static string lastPreviewKey = "";

        [HarmonyPatch(typeof(GameStartManager), nameof(GameStartManager.Update))]
        [HarmonyPriority(Priority.Low)]
        static class LobbyPreviewPatch {
            public static void Postfix() {
                try {
                    if (!AmHost() || AmongUsClient.Instance == null) return;
                    // LOBBY ONLY, checked on the ship rather than on this patch running: a
                    // GameStartManager that keeps updating into a started round (it happens - see the
                    // lobby-screen fallout) would otherwise recompute the preview mid-game, find
                    // everybody already "seen", and clear the shield the round is relying on.
                    if (ShipStatus.Instance != null) return;
                    if (Time.realtimeSinceStartup < nextPreview) return;
                    nextPreview = Time.realtimeSinceStartup + 0.5f;

                    var ids = new List<byte>();
                    if (Enabled != null && Enabled.getBool()) {
                        foreach (var p in PlayerControl.AllPlayerControls.ToArray()) {
                            if (p == null || p.Data == null || p.Data.Disconnected) continue;
                            if (WouldShield(p)) ids.Add(p.PlayerId);
                        }
                    }

                    // Only send when something actually changed - this runs every lobby frame.
                    ids.Sort();
                    string key = string.Join(",", ids);
                    if (key == lastPreviewKey) return;
                    lastPreviewKey = key;

                    if (ids.Count > 0) SendSetShields(ids);
                    else SendClear();
                } catch { }
            }
        }

        // ====================================================================
        // Resets
        // ====================================================================
        [HarmonyPatch(typeof(RPCProcedure), nameof(RPCProcedure.resetVariables))]
        static class ResetPatch {
            // Only the per-round shield: the seen set is the whole point and must survive.
            public static void Postfix() {
                shielded.Clear();
                outlined.Clear();
                lastPreviewKey = "";
            }
        }

        [HarmonyPatch(typeof(AmongUsClient), nameof(AmongUsClient.OnGameJoined))]
        static class LobbyResetPatch {
            // Player ids are per connection, so the shield never carries into another lobby.
            // seenFriendCodes deliberately does: the same person in a new lobby is not new again.
            public static void Postfix() {
                shielded.Clear();
                outlined.Clear();
                manualNewcomers.Clear();   // a hand-picked mark belongs to the lobby it was made in
                lastPreviewKey = "";
            }
        }
    }
}
