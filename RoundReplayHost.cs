// Useful TOR Stuff - Copyright (C) 2026 DaUnknown-0
// Licensed under GPL-3.0-or-later. See LICENSE for details.
// Based on The Other Roles (https://github.com/TheOtherRolesAU/TheOtherRoles), GPL-3.0.

/*
 * RoundReplayHost - the host's extra recording for the replay's player perspective (User 2026-10-02:
 * the host should see each player's perspective, to check whether someone saw something that
 * confirms other actions or not; abilities, vents and shapeshifts recorded too).
 *
 * HOST ONLY. The perspective is a host tool, so guests do not pay for the raycasts. Per 0.25 s sample
 * and player:
 *  - the light radius (ShipStatus.CalculateLightRadius: lights sabotage, Trickster, Lighter, vision
 *    options and impostor vision are all in there),
 *  - whom he could see: within the radius, not in a vent, not invisible, and no wall between, tested
 *    against Constants.ShadowMask, the mask the game's own light casts its shadows with,
 *  - his visible look (Morphling, Shapeshift, Camouflage, Skinwalker...: whatever the host's screen
 *    draws on that body).
 * Every event additionally stores who could see its spot at that moment.
 *
 * EVENTS
 *  - vents: from the samples; looks: from the samples (three or more changes in one sample are one
 *    "mass" event: Camouflage, mushroom mixup); sabotages and doors: polled per sample;
 *  - reports and the emergency button: PlayerControl.StartMeeting;
 *  - TOR abilities: prefixes on the RPCProcedure methods. They run for remote senders inside
 *    HandleRpc and for the host's own use right after his send, so one patch catches both. The
 *    sender comes from a HandleRpc prefix (cleared in the postfix and the finalizer);
 *  - UC abilities: the module byte of UC's channel 230, named after the module's class (read from
 *    UC's own handler table, so new UC roles need nothing here). Only remote senders: the host's
 *    own UC use arrives through his button, below;
 *  - the host's own ability buttons (CustomButton.onClickEvent), only when no TOR procedure already
 *    described that click.
 */

using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using HarmonyLib;
using Hazel;
using TheOtherRoles;
using TheOtherRoles.Objects;
using UnityEngine;

namespace UsefulTORStuff {

    public static partial class RoundReplay {

        internal struct Look {
            public int Color;
            public string Hat, Visor, Skin, Name;
            public bool Hidden;
            public bool SameAs(Look o) => Color == o.Color && Hat == o.Hat && Visor == o.Visor && Skin == o.Skin && Hidden == o.Hidden;
        }

        private const int EvAbility = 3, EvVent = 4, EvSabotage = 5, EvDoor = 6, EvLook = 7;

        private static bool hostRec;
        // for the real map view: which ship, where it stood, and every door change (sample, door, open)
        private static int recMapId = -1;
        private static Vector2 recShipPos;
        private static readonly List<(int S, int Door, bool Open)> doorLog = new List<(int, int, bool)>();
        private static bool[] doorOpen;
        private static readonly HashSet<byte> invisible = new HashSet<byte>();
        private static readonly HashSet<SystemTypes> sabActive = new HashSet<SystemTypes>();
        private static HashSet<SystemTypes> roomsClosed = new HashSet<SystemTypes>();
        private static readonly Dictionary<int, float> ucLast = new Dictionary<int, float>();
        private static Ev pendingMeeting;
        private static PlayerControl rpcSender;
        private static readonly List<(float T, byte Id)> bodiesGone = new List<(float, byte)>();   // Cleaner, Vulture
        private static int localNotes;

        private static bool radiusLogged;
        internal static float liveBodyH = 0.7f;       // a real player's body height in the round (figure size)

        internal static void DiagDump() {
            try {
                var ship = ShipStatus.Instance;
                var o = GameOptionsManager.Instance.currentNormalGameOptions;
                string sw = "?";
                try { sw = ship.Systems[SystemTypes.Electrical].TryCast<SwitchSystem>()?.Value.ToString() ?? "none"; } catch (Exception e) { sw = e.GetType().Name; }
                UsefulTORStuffPlugin.Logger?.LogInfo($"[RoundReplay] diag light: max {ship.MaxLightRadius:F2}, min {ship.MinLightRadius:F2}, crew mod {o.CrewLightMod:F2}, switch {sw}, local {ship.CalculateLightRadius(PlayerControl.LocalPlayer.Data):F2}, " +
                    string.Join(", ", PlayerControl.AllPlayerControls.ToArray().Select(p => $"{p.Data?.PlayerName}={ship.CalculateLightRadius(p.Data):F2}")));
            } catch (Exception e) { UsefulTORStuffPlugin.Logger?.LogInfo($"[RoundReplay] diag light failed: {e.Message}"); }
            int si = samples - 1;
            foreach (var a in tracks) foreach (var b in tracks) {
                if (a == b || si >= a.P.Count || si >= b.P.Count) continue;
                float d = Vector2.Distance(a.P[si], b.P[si]);
                if (d > 4f) continue;
                UsefulTORStuffPlugin.Logger?.LogInfo($"[RoundReplay] diag pair {a.Name}->{b.Name}: d {d:F2}, R {a.R[si]:F2}, vent {a.Vent[si]}/{b.Vent[si]}, hidden {HiddenAt(b, si)}, wall {PhysicsHelpers.AnythingBetween(a.P[si], b.P[si], Constants.ShadowMask, false)}, see {CanSee(a.P[si], a.R[si], b.P[si])}");
            }
        }

        private static bool HostData => tracks.Any(t => t.R.Count > 0);

        // The map from the ship itself: the game options are wrong in freeplay (always the lobby's
        // map; freeplay test 2026-10-02 loaded the Skeld for Polus, Airship and Fungle). Index = the
        // ShipPrefabs index. Any other class (Submerged...) is -1 and takes the live copy.
        private static int MapIdOf(ShipStatus ship) {
            if (ship == null) return -1;
            string cls = "";
            try { cls = ship.GetIl2CppType().Name; } catch { }
            switch (cls) {
                case "SkeldShipStatus": return ship.transform.localScale.x < 0f ? 3 : 0;   // dlekS is the Skeld mirrored
                case "MiraShipStatus": return 1;
                case "PolusShipStatus": return 2;
                case "AirshipStatus": return 4;
                case "FungleShipStatus": return 5;
                default:
                    UsefulTORStuffPlugin.Logger?.LogInfo($"[RoundReplay] ship class '{cls}' is no vanilla map: the perspective view uses the live copy.");
                    return -1;
            }
        }

        private static void HostBegin() {
            hostRec = (AmongUsClient.Instance != null && AmongUsClient.Instance.AmHost) || (DiagViewer != null && DiagViewer.Value);
            invisible.Clear(); sabActive.Clear(); roomsClosed = new HashSet<SystemTypes>(); ucLast.Clear();
            pendingMeeting = null; rpcSender = null;
            ReportsBegin();
            doorLog.Clear(); doorOpen = null; bodiesGone.Clear();
            recMapId = -1;
            try {
                recMapId = MapIdOf(ShipStatus.Instance);
                recShipPos = ShipStatus.Instance != null ? (Vector2)ShipStatus.Instance.transform.position : Vector2.zero;
            } catch { }
        }

        // ---- per sample ----
        private static void HostSample() {
            var ship = ShipStatus.Instance;
            if (ship == null) return;
            int si = samples - 1, n = tracks.Count;
            var changed = new List<(Track T, Look From, Look To)>();
            foreach (var t in tracks) {
                while (t.R.Count < si) { t.R.Add(0f); t.Sees.Add(0UL); }
                var p = TheOtherRoles.Helpers.playerById(t.Id);
                bool alive = p != null && si < t.P.Count && !float.IsNaN(t.P[si].x);
                float r = 0f;
                if (alive) {
                    try { r = ship.CalculateLightRadius(p.Data); }
                    catch (Exception e) { r = 1f; if (!radiusLogged) { radiusLogged = true; UsefulTORStuffPlugin.Logger?.LogWarning($"[RoundReplay] light radius of {t.Name} failed: {e}"); } }
                }
                t.R.Add(r);
                t.Sees.Add(0UL);
                if (!alive) continue;
                var look = ReadLook(p);
                if (t.Looks.Count == 0 || si < 8) {
                    if (t.Looks.Count == 0) t.Looks.Add((si, look)); else t.Looks[0] = (t.Looks[0].S, look);
                    continue;
                }
                var last = t.Looks[t.Looks.Count - 1].L;
                if (last.SameAs(look)) continue;
                t.Looks.Add((si, look));
                changed.Add((t, last, look));
            }

            for (int a = 0; a < n && a < 64; a++) {
                var ta = tracks[a];
                float r = ta.R[si];
                if (r <= 0f || ta.Vent[si]) continue;
                Vector2 eye = ta.P[si] + LightOffset;
                ulong mask = 0UL;
                for (int b = 0; b < n && b < 64; b++) {
                    if (b == a) continue;
                    var tb = tracks[b];
                    var pb = tb.P[si];
                    if (float.IsNaN(pb.x) || tb.Vent[si] || HiddenAt(tb, si)) continue;
                    if (CanSee(eye, r, pb)) mask |= 1UL << b;
                }
                ta.Sees[si] = mask;
            }

            // vents
            if (si > 0)
                foreach (var t in tracks) {
                    if (si >= t.P.Count || float.IsNaN(t.P[si].x) || float.IsNaN(t.P[si - 1].x)) continue;
                    if (t.Vent[si] == t.Vent[si - 1]) continue;
                    Vector2 at = t.Vent[si] ? t.P[si - 1] : t.P[si];
                    Note(EvVent, t.Id, at, UTSLocalization.Tr(t.Vent[si] ? "uts.replay.ev.vent_in" : "uts.replay.ev.vent_out", WhoId(t.Id), RoomAt(at)));
                }

            // looks
            if (changed.Count >= 3) {
                var c0 = changed[0];
                Note(EvLook, 255, c0.T.P[si], UTSLocalization.Tr("uts.replay.ev.mass_look", changed.Count), witnesses: false);
            } else
                foreach (var c in changed) Note(EvLook, c.T.Id, c.T.P[si], LookText(c.T, c.From, c.To));

            PollSabotage(ship);
            PollDoors(ship);
            if (si == 8) {
                try {
                    var lp = PlayerControl.LocalPlayer;
                    var body = lp.cosmetics.currentBodySprite.BodySprite;
                    float h = body.bounds.size.y / Mathf.Max(0.1f, lp.transform.localScale.y / 0.7f);   // Mini/Giant out
                    if (h > 0.2f && h < 2f) liveBodyH = h;
                } catch { }
            }
        }

        internal static bool CanSee(Vector2 eye, float r, Vector2 target) {
            float reach = r + 0.25f;    // half a body: an edge already shows
            if ((target - eye).sqrMagnitude > reach * reach) return false;
            return !PhysicsHelpers.AnythingBetween(eye, target, Constants.ShadowMask, false);
        }

        private static bool HiddenAt(Track t, int si) {
            for (int k = t.Looks.Count - 1; k >= 0; k--) if (t.Looks[k].S <= si) return t.Looks[k].L.Hidden;
            return false;
        }

        private static Look ReadLook(PlayerControl p) {
            var l = new Look { Color = -1, Hat = "", Visor = "", Skin = "", Name = "" };
            var c = p.cosmetics;
            if (c == null) return l;
            try { l.Color = c.ColorId; } catch { }
            try { l.Hat = c.hat?.Hat?.ProductId ?? ""; } catch { }
            try { l.Visor = c.visor?.visorData?.ProductId ?? ""; } catch { }
            try { l.Skin = c.skin != null && c.skin.skin != null ? c.skin.skin.name : ""; } catch { }   // SkinViewData: the asset name identifies it
            try { l.Name = PlainName(c.nameText != null ? c.nameText.text : ""); } catch { }
            l.Hidden = invisible.Contains(p.PlayerId);
            try {
                var body = c.currentBodySprite?.BodySprite;
                if (body != null && body.color.a < 0.3f) l.Hidden = true;
            } catch { }
            return l;
        }

        // first line of a name tag, without TMP tags (TOR writes role lines and colours into it)
        private static string PlainName(string s) {
            if (string.IsNullOrEmpty(s)) return "";
            int nl = s.IndexOf('\n');
            if (nl >= 0) s = s.Substring(0, nl);
            var sb = new System.Text.StringBuilder();
            bool tag = false;
            foreach (char ch in s) {
                if (ch == '<') tag = true;
                else if (ch == '>') tag = false;
                else if (!tag) sb.Append(ch);
            }
            return sb.ToString().Trim();
        }

        private static int BaseColor(byte id) {
            try { var p = TheOtherRoles.Helpers.playerById(id); if (p?.Data != null) return p.Data.DefaultOutfit.ColorId; } catch { }
            return -1;
        }

        private static string LookText(Track t, Look from, Look to) {
            if (to.Hidden != from.Hidden)
                return UTSLocalization.Tr(to.Hidden ? "uts.replay.ev.invisible" : "uts.replay.ev.visible", WhoId(t.Id));
            if (to.Color == BaseColor(t.Id)) return UTSLocalization.Tr("uts.replay.ev.look_self", WhoId(t.Id));
            var like = tracks.FirstOrDefault(o => o.Id != t.Id && BaseColor(o.Id) == to.Color);
            if (like != null) return UTSLocalization.Tr("uts.replay.ev.look_like", WhoId(t.Id), WhoId(like.Id, false));
            return UTSLocalization.Tr("uts.replay.ev.look_other", WhoId(t.Id));
        }

        // ---- sabotages and doors ----
        private static readonly SystemTypes[] SabSystems = {
            SystemTypes.Electrical, SystemTypes.Reactor, SystemTypes.Laboratory, SystemTypes.LifeSupp,
            SystemTypes.Comms, SystemTypes.HeliSabotage, SystemTypes.MushroomMixupSabotage,
        };

        private static void PollSabotage(ShipStatus ship) {
            foreach (var st in SabSystems) {
                bool on = SabotageOn(ship, st);
                if (on == sabActive.Contains(st)) continue;
                if (on) sabActive.Add(st); else sabActive.Remove(st);
                string what = UTSLocalization.Tr(SabKey(st));
                byte by = on ? SaboteurOf(st) : (byte)255;
                string text = by != 255 ? UTSLocalization.Tr("uts.replay.ev.sab_on_by", what, WhoId(by))
                                        : UTSLocalization.Tr(on ? "uts.replay.ev.sab_on" : "uts.replay.ev.sab_off", what);
                Note(EvSabotage, by, RoomCenter(ship, st), text, witnesses: false);
            }
        }

        private static string SabKey(SystemTypes st) => st switch {
            SystemTypes.Electrical => "uts.replay.sab.lights",
            SystemTypes.LifeSupp => "uts.replay.sab.o2",
            SystemTypes.Comms => "uts.replay.sab.comms",
            SystemTypes.MushroomMixupSabotage => "uts.replay.sab.mushroom",
            _ => "uts.replay.sab.reactor",
        };

        private static bool SabotageOn(ShipStatus ship, SystemTypes st) {
            try {
                if (ship.Systems == null || !ship.Systems.ContainsKey(st)) return false;
                var sys = ship.Systems[st];
                if (sys == null) return false;
                var sw = sys.TryCast<SwitchSystem>(); if (sw != null) return sw.IsActive;
                var re = sys.TryCast<ReactorSystemType>(); if (re != null) return re.IsActive;
                var he = sys.TryCast<HeliSabotageSystem>(); if (he != null) return he.IsActive;
                var o2 = sys.TryCast<LifeSuppSystemType>(); if (o2 != null) return o2.IsActive;
                var hud = sys.TryCast<HudOverrideSystemType>(); if (hud != null) return hud.IsActive;
                var hq = sys.TryCast<HqHudSystemType>(); if (hq != null) return hq.IsActive;
                var mu = sys.TryCast<MushroomMixupSabotageSystem>(); if (mu != null) return mu.IsActive;
            } catch { }
            return false;
        }

        private static void PollDoors(ShipStatus ship) {
            try {
                var doors = ship.AllDoors;
                if (doors == null) return;
                var now = new HashSet<SystemTypes>();
                foreach (var d in doors) if (d != null && !d.IsOpen) now.Add(d.Room);
                if (doorOpen == null || doorOpen.Length != doors.Length) { doorOpen = new bool[doors.Length]; for (int k = 0; k < doorOpen.Length; k++) doorOpen[k] = true; }
                for (int k = 0; k < doors.Length; k++) {
                    bool open = doors[k] == null || doors[k].IsOpen;
                    if (open == doorOpen[k]) continue;
                    doorOpen[k] = open;
                    doorLog.Add((samples - 1, k, open));
                }
                foreach (var room in now)
                    if (!roomsClosed.Contains(room))
                        Note(EvDoor, 255, RoomCenter(ship, room), UTSLocalization.Tr("uts.replay.ev.doors", RoomName(room)), witnesses: false);
                roomsClosed = now;
            } catch { }
        }

        private static string RoomName(SystemTypes room) {
            try { return TranslationController.Instance.GetString(room); } catch { return room.ToString(); }
        }

        private static string RoomAt(Vector2 pos) {
            try {
                var ship = ShipStatus.Instance;
                if (ship != null && ship.FastRooms != null)
                    foreach (var room in ship.FastRooms.Values)
                        if (room != null && room.roomArea != null && room.roomArea.OverlapPoint(pos)) return RoomName(room.RoomId);
            } catch { }
            return "-";
        }

        private static Vector2 RoomCenter(ShipStatus ship, SystemTypes id) {
            try {
                if (ship.FastRooms != null && ship.FastRooms.ContainsKey(id)) {
                    var room = ship.FastRooms[id];
                    if (room != null && room.roomArea != null) return room.roomArea.bounds.center;
                }
            } catch { }
            return new Vector2(float.NaN, float.NaN);
        }

        // ---- events ----
        private static void Note(int kind, byte actor, Vector2 pos, string text, bool witnesses = true, byte target = 255) {
            if (!recording || !hostRec) return;
            var e = new Ev { T = clock, Kind = kind, Pos = pos, Text = text, Actor = actor, Target = target };
            if (witnesses && !float.IsNaN(pos.x)) e.Seen = Witnesses(pos, actor);
            events.Add(e);
            if (PlayerControl.LocalPlayer != null && actor == PlayerControl.LocalPlayer.PlayerId) localNotes++;
        }

        /// <summary>Who could see this spot at the latest sample: alive, outside vents, within his
        /// light radius and with no wall in between. The actor himself does not count.</summary>
        private static List<byte> Witnesses(Vector2 pos, byte actor) {
            var list = new List<byte>();
            foreach (var t in tracks) {
                if (t.Id == actor || t.R.Count == 0) continue;
                int i = t.R.Count - 1;
                if (i >= t.P.Count) continue;
                float r = t.R[i];
                if (r <= 0f || t.Vent[i] || float.IsNaN(t.P[i].x)) continue;
                if (CanSee(t.P[i] + LightOffset, r, pos)) list.Add(t.Id);
            }
            return list;
        }

        private static Vector2 PosOf(byte id) {
            var p = TheOtherRoles.Helpers.playerById(id);
            return p != null ? (Vector2)p.transform.position : new Vector2(float.NaN, float.NaN);
        }

        /// <summary>A player as a coloured name, with the role (the replay only opens after the round).</summary>
        internal static string WhoId(byte id, bool role = true) {
            var p = TheOtherRoles.Helpers.playerById(id);
            if (p != null) return GhostKillFeed.Who(p, role);
            var t = tracks.FirstOrDefault(x => x.Id == id);
            return t != null ? $"<color=#{ColorUtility.ToHtmlStringRGB(t.Col)}>{t.Name}</color>" : "?";
        }

        // ---- reports and the emergency button ----
        [HarmonyPatch(typeof(PlayerControl), nameof(PlayerControl.StartMeeting))]
        static class StartMeetingPatch {
            [HarmonyPriority(Priority.First)]
            public static void Prefix(PlayerControl __instance, [HarmonyArgument(0)] NetworkedPlayerInfo target) {
                try {
                    if (!recording || !hostRec || __instance == null) return;
                    Vector2 at = __instance.transform.position;
                    string text = target == null
                        ? UTSLocalization.Tr("uts.replay.ev.button", WhoId(__instance.PlayerId))
                        : UTSLocalization.Tr("uts.replay.ev.report", WhoId(__instance.PlayerId), WhoId(target.PlayerId));
                    pendingMeeting = new Ev { Kind = EvMeeting, Pos = at, Text = text, Actor = __instance.PlayerId, Seen = Witnesses(at, __instance.PlayerId) };
                } catch { }
            }
        }

        // the meeting edge in RecordTick takes the reporter's sentence if one is waiting
        private static Ev MeetingEvent() {
            var e = pendingMeeting ?? new Ev { Kind = EvMeeting, Pos = new Vector2(float.NaN, float.NaN), Text = UTSLocalization.Tr("uts.replay.meeting") };
            pendingMeeting = null;
            e.T = clock;
            return e;
        }

        // ---- RPC sender context ----
        [HarmonyPatch(typeof(PlayerControl), nameof(PlayerControl.HandleRpc))]
        static class SenderPatch {
            [HarmonyPriority(Priority.First)]
            public static void Prefix(PlayerControl __instance, byte callId, MessageReader reader) {
                rpcSender = __instance;
                if (callId == UcCallId) try { UcTap(__instance, reader); } catch { }
            }

            [HarmonyPriority(Priority.Last)]
            public static void Postfix() => rpcSender = null;

            public static Exception Finalizer(Exception __exception) { rpcSender = null; return __exception; }
        }

        // ---- TOR abilities ----
        private static readonly string[] TorTaps = {
            "morphlingMorph", "camouflagerCamouflage", "medicSetShielded", "shieldedMurderAttempt", "timeMasterShield",
            "timeMasterRewindTime", "trackerUsedTracker", "vampireSetBitten", "placeGarlic", "deputyUsedHandcuffs",
            "jackalCreatesSidekick", "setFutureErased", "setFutureShifted", "setFutureShielded", "setFutureSpelled",
            "placePortal", "usePortal", "placeJackInTheBox", "lightsOut", "placeCamera", "sealVent", "cleanBody",
            "setBlanked", "setTrap", "triggerTrap", "placeBomb", "defuseBomb", "yoyoMarkLocation", "yoyoBlink",
            "engineerFixLights", "engineerUsedRepair", "setInvisible", "breakArmor", "swapperSwap", "guesserShoot",
        };

        internal static void TryPatch(Harmony harmony) {
            var pre = new HarmonyMethod(AccessTools.Method(typeof(RoundReplay), nameof(TorTapPrefix))) { priority = Priority.First };
            int ok = 0;
            foreach (var name in TorTaps) {
                try {
                    var m = AccessTools.Method(typeof(RPCProcedure), name);
                    if (m == null) { UsefulTORStuffPlugin.Logger?.LogWarning($"[RoundReplay] RPCProcedure.{name} not found."); continue; }
                    harmony.Patch(m, prefix: pre);
                    ok++;
                } catch (Exception e) {
                    UsefulTORStuffPlugin.Logger?.LogWarning($"[RoundReplay] tap on {name} failed: {e.Message}");
                }
            }
            UsefulTORStuffPlugin.Logger?.LogInfo($"[RoundReplay] {ok}/{TorTaps.Length} TOR ability taps.");
            RegisterReports();
        }

        private static void TorTapPrefix(MethodBase __originalMethod, object[] __args) {
            try {
                string name = __originalMethod.Name;
                // invisibility is state, needed even when the moment itself is not described
                if (name == "setInvisible") {
                    byte who = (byte)__args[0];
                    if ((byte)__args[1] == byte.MaxValue) invisible.Remove(who); else invisible.Add(who);
                    return;
                }
                var sender = rpcSender ?? PlayerControl.LocalPlayer;
                if (sender == null) return;
                // this client's own procedures: its button click is described already, no report
                if (PlayerControl.LocalPlayer != null && sender.PlayerId == PlayerControl.LocalPlayer.PlayerId) localTaps++;
                if (!recording || !hostRec) return;
                byte a = sender.PlayerId, target = 255;
                byte Arg(int i) => __args.Length > i && __args[i] is byte b ? b : (byte)255;
                // in the meeting: the Swapper's swap and every Guesser shot
                if (name == "swapperSwap") {
                    Note(EvAbility, a, PosOf(a), UTSLocalization.Tr("uts.replay.tor.swapperSwap", WhoId(a), WhoId(Arg(0), false), WhoId(Arg(1), false)), witnesses: false);
                    return;
                }
                if (name == "guesserShoot") {
                    byte killer = Arg(0), dying = Arg(1), guessed = Arg(2), roleId = Arg(3);
                    string role = "?";
                    try { role = RoleInfo.allRoleInfos.FirstOrDefault(r => (byte)r.roleId == roleId)?.name ?? "?"; } catch { }
                    string result = UTSLocalization.Tr(dying == guessed ? "uts.replay.guess_hit" : "uts.replay.guess_miss");
                    Note(EvAbility, killer, PosOf(killer), UTSLocalization.Tr("uts.replay.tor.guesserShoot", WhoId(killer), WhoId(guessed, false), role, result), witnesses: false, target: guessed);
                    return;
                }
                if (inMeeting) return;
                switch (name) {
                    case "vampireSetBitten": if (Arg(1) != 0) return; target = Arg(0); break;
                    case "setBlanked": if (Arg(1) == 0) return; target = Arg(0); break;
                    case "usePortal": a = Arg(0); break;
                    case "triggerTrap": a = Arg(0); break;
                    case "cleanBody": a = Arg(1); target = Arg(0); break;
                    case "morphlingMorph": case "medicSetShielded": case "trackerUsedTracker": case "deputyUsedHandcuffs":
                    case "jackalCreatesSidekick": case "setFutureErased": case "setFutureShifted": case "setFutureShielded":
                    case "setFutureSpelled":
                        target = Arg(0); break;
                }
                Note(EvAbility, a, PosOf(a), UTSLocalization.Tr("uts.replay.tor." + name, WhoId(a), target != 255 ? WhoId(target, false) : ""), target: target);
                if (name == "cleanBody") bodiesGone.Add((clock, target));
            } catch (Exception e) {
                UsefulTORStuffPlugin.Logger?.LogWarning($"[RoundReplay] TOR tap failed: {e.Message}");
            }
        }

        // ---- UC abilities (remote senders) ----
        private const byte UcCallId = 230;
        private static Dictionary<byte, string> ucModules;
        private static readonly HashSet<string> UcSilent = new HashSet<string> {
            "PlayerTuning", "TeslaVersionHandshake", "UCColorGrant", "Colorblind", "Giant", "Sleepwalker",
            "LastWords", "Gambler", "King", "SixthSense", "Witness", "AuditorHud",
        };

        private static void UcTap(PlayerControl sender, MessageReader reader) {
            if (!recording || !hostRec || inMeeting || sender == null || reader == null) return;
            if (reader.Length - reader.Position < 1) return;
            int pos = reader.Position;
            byte module = reader.ReadByte();
            reader.Position = pos;
            string name = UcModuleName(module);
            if (name == null || UcSilent.Contains(name)) return;
            int key = sender.PlayerId << 8 | module;
            if (ucLast.TryGetValue(key, out var last) && clock - last < 2f) return;     // a burst is one use
            ucLast[key] = clock;
            Note(EvAbility, sender.PlayerId, sender.transform.position, UTSLocalization.Tr("uts.replay.ev.uc", WhoId(sender.PlayerId), name));
            if (events.Count > 0 && events[events.Count - 1].Actor == sender.PlayerId) events[events.Count - 1].Generic = true;
        }

        private static string UcModuleName(byte module) {
            if (ucModules == null) {
                ucModules = new Dictionary<byte, string>();
                try {
                    Type rpc = null;
                    foreach (var asm in AppDomain.CurrentDomain.GetAssemblies()) { rpc = asm.GetType("UnknownsCollection.UCRpc"); if (rpc != null) break; }
                    var field = rpc?.GetField("handlers", BindingFlags.NonPublic | BindingFlags.Static);
                    if (field?.GetValue(null) is IDictionary table)
                        foreach (DictionaryEntry kv in table) {
                            var t = (kv.Value as Delegate)?.Method?.DeclaringType;
                            while (t != null && t.IsNested && t.Name.Contains("<")) t = t.DeclaringType;
                            if (t != null) ucModules[(byte)kv.Key] = t.Name;
                        }
                    UsefulTORStuffPlugin.Logger?.LogInfo($"[RoundReplay] {ucModules.Count} UC module name(s).");
                } catch (Exception e) {
                    UsefulTORStuffPlugin.Logger?.LogWarning($"[RoundReplay] UC module table unreadable: {e.Message}");
                }
            }
            return ucModules.TryGetValue(module, out var n) ? n : null;
        }

        // ---- the host's own ability buttons ----
        [HarmonyPatch(typeof(CustomButton), nameof(CustomButton.onClickEvent))]
        static class ButtonPatch {
            [HarmonyPriority(Priority.First)]
            public static void Prefix(CustomButton __instance, out int __state) {
                __state = -1;
                try {
                    if (recording && !inMeeting && __instance.Timer < 0f && __instance.HasButton() && __instance.CouldUse())
                        __state = localTaps;
                } catch { }
            }

            public static void Postfix(CustomButton __instance, int __state) {
                try {
                    if (__state < 0 || __state != localTaps) return;      // not used, or a TOR tap described it
                    string label = __instance.buttonText;
                    if (string.IsNullOrWhiteSpace(label)) return;         // kill buttons: the death says it
                    LocalAbility(label);
                } catch { }
            }
        }
    }
}
