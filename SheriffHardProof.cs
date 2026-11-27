// Useful TOR Stuff - Copyright (C) 2026 DaUnknown-0
// Licensed under GPL-3.0-or-later. See LICENSE for details.
// Based on The Other Roles (https://github.com/TheOtherRolesAU/TheOtherRoles), GPL-3.0.

/*
 * SheriffHardProof - lobby rule as a host option (User 2026-11-27): "Sheriff darf nicht mehr random in der ersten
 * Runde schiessen. Schiesst als Sheriff bitte nur, wenn ihr einen Kill gesehen habt oder anderes sehr verdaechtiges
 * Verhalten (bspw. morphen)."
 *
 * Until the first meeting the Sheriff's shot only goes through on a player he has caught at hard proof. Without it
 * the click is refused: nothing dies, a short chat line explains why, and the button gets a short cooldown
 * (option 1354, 5-10 s, default 8). After the first meeting the Sheriff shoots as usual. Option 1355 puts the
 * Thief's shot under the same rule. Every refused shot goes to the host only (module 227 sub 1), who gets the list
 * as chat lines back in the lobby, never during the round (a playing host would learn who the Sheriff is).
 *
 * WHAT COUNTS (picked by the user on the Hardproof page, 27.11.): a kill, morphing (and turning back), the Ninja's
 * jump, the Yoyo's blink, a body cleaned away (Janitor/Cleaner) or eaten (Vulture) and a Werewolf in wolf form.
 * From the pass over every killing role: the Skinwalker putting on a skin, the Faker dropping a fake body, the
 * Copycat's copied morph and the Necromancer raising a thrall. Follow-ups (User 27.11.): a body falling in view with
 * exactly one living player near it; the Maniac's bomb only when it is handed to the Sheriff himself with exactly
 * one player in reach (TOR's Bomber not at all: his bomb is hidden while it is laid).
 *
 * "COULD HE SEE IT" is decided on the viewer's own client from what his screen draws: his positions (lag included),
 * his light radius, walls and doors, nothing while a task, the map or the camera screen covers the room (the camera
 * picture itself counts), and not an actor in a vent, an invisible Ninja or a Chameleon faded below half. Venting, the Trickster box, camouflage, standing at a body, fake tasking, the Vampire's bite
 * and the Warlock's curse kill do NOT count.
 *
 * WHO MAY HAVE SEEN IT: the Sheriff himself (in his light radius, nothing solid in between), the Sheriff on the
 * security cameras (the camera minigame open, the spot near a camera and in its line of sight), or the Sheriff's
 * Lover (seen by the Lover's own client and reported to the Sheriff over module byte 227).
 *
 * DESYNC SAFETY. The decision is taken on the Sheriff's own client, BEFORE TOR's button sends anything: the prefix on
 * CustomButton.onClickEvent refuses the click, so no murder RPC, no blank, no armor break, no Medic shield hit and no
 * misfire leaves his game. Every other client simply never hears of the attempt. Deliberately NOT enforced on the
 * receiving side: a receiver that refused a murder the Sheriff's game had already carried out would be exactly the
 * desync this has to avoid. Observations are per observer and only feed that observer's (or his Sheriff's) own
 * decision; the Lover report changes nothing but the Sheriff's local list. The rule switches off at the first
 * meeting on every client by the same event (MeetingHud.Start). A Sheriff without Forgotten Fixes is not checked (no
 * patch on his game); the lobby's mod sync closes that gap, as with the other client-side rules.
 *
 * Kills count only with the killer next to the victim (TOR snaps the killer onto the body): a Warlock's curse kill,
 * a Vampire's delayed bite and a bomb blast are reported with the killer somewhere else, which is exactly why they
 * are not hard proof.
 *
 * Options 1353-1354 (General tab), module byte 227 on UTSRpc.CallId = 240. See ID-Registry.md.
 */

using System;
using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using UnityEngine;
using TheOtherRoles;
using TheOtherRoles.CustomGameModes;
using TheOtherRoles.Objects;
using static TheOtherRoles.TheOtherRoles;
using Types = TheOtherRoles.CustomOption.CustomOptionType;

namespace UsefulTORStuff {

    public static class SheriffHardProof {

        public static CustomOption Enabled;        // 1353
        public static CustomOption BlockCooldown;  // 1354
        public static CustomOption ForThief;       // 1355 - the same rule for the Thief's shot (User 27.11.)

        private const byte RpcId = UsefulTORStuffPlugin.SheriffHardProofRpcId;
        private const byte SubLoverSaw = 0;        // offenderId (a shooter's Lover -> everyone, only that shooter uses it)
        private const byte SubRefused = 1;         // targetId, role (shooter -> host only): for the host's list after the round
        private const byte RoleSheriff = 0, RoleThief = 1;

        // Host: the refused shots of the last round, shown in the lobby chat afterwards (never during the round: a
        // playing host would learn who the Sheriff is).
        private static readonly List<string> refusedLog = new List<string>();
        private static bool logPending;
        private static float roundStart;

        // Players the LOCAL Sheriff caught at hard proof this round (empty on every other client).
        private static readonly HashSet<byte> caught = new HashSet<byte>();
        // Players the local Lover already reported, so a wolf in sight is not resent every tick.
        private static readonly HashSet<byte> reported = new HashSet<byte>();
        private static bool meetingSeen;

        // A kill counts when the killer stands within kill range of the body. MurderPlayer only starts the kill
        // animation, so at this point the killer is still where he struck from (long kill range is 2.5).
        private const float KillerNearBody = 3.2f;
        // How far around a security camera its picture reaches (world units); line of sight from the camera too.
        private const float CameraReach = 4.2f;

        public static void CreateOptions() {
            try {
                Enabled = CustomOption.Create(1353, Types.General,
                    "Sheriff Needs Hard Proof Before The First Meeting", false, null, true);
                BlockCooldown = CustomOption.Create(1354, Types.General,
                    "Cooldown After A Refused Shot", 8f, 5f, 10f, 1f, Enabled);
                ForThief = CustomOption.Create(1355, Types.General,
                    "Also For The Thief", false, Enabled);
                UTSLocalization.BindOptionTitle(Enabled, "uts.sheriffproof.option_name");
                UTSLocalization.BindOptionTitle(BlockCooldown, "uts.sheriffproof.option_cooldown");
                UTSLocalization.BindOptionTitle(ForThief, "uts.sheriffproof.option_thief");
            } catch (Exception e) {
                UsefulTORStuffPlugin.Logger?.LogError($"[SheriffHardProof] CreateOptions failed: {e}");
            }
        }

        public static void RegisterRpc() => UTSRpc.Register(RpcId, HandleModuleRpc);

        // ---- state ----

        private static bool RuleActive() {
            try {
                if (Enabled == null || !Enabled.getBool() || meetingSeen) return false;
                if (HideNSeek.isHideNSeekGM || AntiStartKill.IsPropHuntGM()) return false;
                return AmongUsClient.Instance != null && AmongUsClient.Instance.GameState == InnerNet.InnerNetClient.GameStates.Started;
            } catch { return false; }
        }

        private static bool Alive(PlayerControl p) => p != null && p.Data != null && !p.Data.IsDead && !p.Data.Disconnected;

        private static PlayerControl PartnerOf(PlayerControl p) {
            if (p == null || Lovers.lover1 == null || Lovers.lover2 == null) return null;
            if (Lovers.lover1.PlayerId == p.PlayerId) return Lovers.lover2;
            if (Lovers.lover2.PlayerId == p.PlayerId) return Lovers.lover1;
            return null;
        }

        /// <summary>True for a player whose shot this rule judges: the Sheriff, and the Thief when 1355 is on.</summary>
        private static bool IsShooter(PlayerControl p) {
            if (p == null) return false;
            if (Sheriff.sheriff != null && Sheriff.sheriff.PlayerId == p.PlayerId) return true;
            return ForThief != null && ForThief.getBool() && Thief.thief != null && Thief.thief.PlayerId == p.PlayerId;
        }

        private static void Reset() {
            caught.Clear();
            reported.Clear();
            meetingSeen = false;
            bombSuspect = byte.MaxValue;
            wasMorphed = false;
        }

        // ---- observing ----

        /// <summary>Something that counts as hard proof against <paramref name="actor"/> happened at <paramref name="pos"/>.
        /// Runs on every client; only the Sheriff and the Sheriff's Lover look at it.</summary>
        internal static void Witness(PlayerControl actor, Vector2 pos, string what) {
            try {
                if (actor == null || !RuleActive()) return;
                var lp = PlayerControl.LocalPlayer;
                if (!Alive(lp)) return;
                bool isSheriff = IsShooter(lp);
                var partner = PartnerOf(lp);
                bool isLover = !isSheriff && Alive(partner) && IsShooter(partner);
                if (!isSheriff && !isLover) return;
                var shooter = isSheriff ? lp : partner;
                if (actor.PlayerId == lp.PlayerId || actor.PlayerId == shooter.PlayerId) return;   // nobody proves against himself
                string how = Sees(lp, actor, pos);
                if (how == null) return;
                Grant(actor, what + " (" + how + ")", isSheriff);
            } catch (Exception e) {
                UsefulTORStuffPlugin.Logger?.LogWarning($"[SheriffHardProof] witness failed: {e.Message}");
            }
        }

        /// <summary>The Sheriff (or his Lover, who reports it) now holds proof against <paramref name="actor"/>.</summary>
        private static void Grant(PlayerControl actor, string why, bool isSheriff) {
            try {
                if (isSheriff) {
                    if (caught.Add(actor.PlayerId))
                        UsefulTORStuffPlugin.Logger?.LogInfo($"[SheriffHardProof] caught {actor.Data?.PlayerName}: {why}.");
                } else if (reported.Add(actor.PlayerId) && UTSRpc.CanSend) {
                    var w = UTSRpc.Begin(RpcId);
                    w.Write(SubLoverSaw);
                    w.Write(actor.PlayerId);
                    AmongUsClient.Instance.FinishRpcImmediately(w);
                    UsefulTORStuffPlugin.Logger?.LogInfo($"[SheriffHardProof] told the Sheriff: {actor.Data?.PlayerName}, {why}.");
                }
            } catch { }
        }

        /// <summary>"sight" or "camera" when <paramref name="viewer"/>'s screen shows <paramref name="actor"/> at
        /// <paramref name="pos"/> right now, else null. Runs on the viewer's own client, so the positions are exactly the
        /// ones his screen draws (network lag included), the light radius is his (lights sabotage, Lighter, Sunglasses
        /// and Trickster via TOR's CalculateLightRadius patches), walls and closed doors block (shadow mask).</summary>
        private static string Sees(PlayerControl viewer, PlayerControl actor, Vector2 pos) {
            var ship = ShipStatus.Instance;
            if (ship == null || !Drawn(actor, viewer)) return null;
            if (!ScreenCovered()) {
                float r;
                try { r = ship.CalculateLightRadius(viewer.Data); } catch { r = 1f; }
                if (RoundReplay.CanSee(viewer.GetTruePosition(), r, pos)) return "sight";
            }
            return OnCameras(ship, pos) ? "camera" : null;
        }

        /// <summary>A task or other minigame (the camera screen aside, which OnCameras reads) or the map covers the
        /// room: whatever happens next to him then is not on his screen.</summary>
        private static bool ScreenCovered() {
            try {
                // any minigame covers the room, the camera screen as well (what it shows is OnCameras' business)
                if (Minigame.Instance != null) return true;
                if (MapBehaviour.Instance != null && MapBehaviour.Instance.IsOpen) return true;
            } catch { }
            return false;
        }

        /// <summary>False when the actor is not drawn for this viewer: in a vent, the Ninja's invisibility, a Chameleon
        /// faded below half (TOR's own alpha).</summary>
        private static bool Drawn(PlayerControl actor, PlayerControl viewer) {
            try {
                if (actor.inVent) return false;
                if (Ninja.ninja != null && actor.PlayerId == Ninja.ninja.PlayerId && Ninja.isInvisble) return false;
                if (Chameleon.chameleon != null && Chameleon.chameleon.Exists(c => c != null && c.PlayerId == actor.PlayerId)
                    && Chameleon.visibility(actor.PlayerId) < 0.5f) return false;
            } catch { }
            return true;
        }

        private static bool OnCameras(ShipStatus ship, Vector2 pos) {
            try {
                var mg = Minigame.Instance;
                if (mg == null || ship.AllCameras == null) return false;
                var cams = ship.AllCameras;
                var planet = mg.TryCast<PlanetSurveillanceMinigame>();
                if (planet != null) {
                    // one camera at a time
                    int i = planet.currentCamera;
                    return i >= 0 && i < cams.Length && SeenBy(cams[i], pos);
                }
                if (mg.TryCast<SurveillanceMinigame>() == null && mg.TryCast<FungleSurveillanceMinigame>() == null) return false;
                foreach (var c in cams) if (SeenBy(c, pos)) return true;
            } catch { }
            return false;
        }

        private static bool SeenBy(SurvCamera c, Vector2 pos) {
            if (c == null) return false;
            Vector2 eye = c.transform.position;
            if ((pos - eye).sqrMagnitude > CameraReach * CameraReach) return false;
            return !PhysicsHelpers.AnythingBetween(eye, pos, Constants.ShadowMask, false);
        }

        private static void HandleModuleRpc(Hazel.MessageReader reader) {
            byte sub = reader.ReadByte();
            if (sub == SubRefused) {
                byte target = reader.ReadByte();
                byte role = reader.ReadByte();
                try {
                    var sender = UTSRpc.Sender;
                    if (AmongUsClient.Instance == null || !AmongUsClient.Instance.AmHost || sender == null) return;
                    // only a real shooter's own report: the Sheriff for role 0, the Thief for role 1
                    var expected = role == RoleThief ? Thief.thief : Sheriff.sheriff;
                    if (expected == null || expected.PlayerId != sender.PlayerId) return;
                    RecordRefusal(sender, Helpers.playerById(target), role);
                } catch { }
                return;
            }
            if (sub != SubLoverSaw) return;
            byte offender = reader.ReadByte();
            try {
                if (!RuleActive()) return;
                var lp = PlayerControl.LocalPlayer;
                var sender = UTSRpc.Sender;
                if (!IsShooter(lp) || sender == null) return;
                // only the shooter's own Lover may vouch, never for the shooter himself
                if (PartnerOf(lp)?.PlayerId != sender.PlayerId || offender == lp.PlayerId) return;
                if (caught.Add(offender))
                    UsefulTORStuffPlugin.Logger?.LogInfo($"[SheriffHardProof] the Lover {sender.Data?.PlayerName} saw proof against player {offender}.");
            } catch { }
        }

        // ---- the host's list of refused shots ----

        private static void ReportRefusal(PlayerControl shooter, PlayerControl target, byte role) {
            try {
                var client = AmongUsClient.Instance;
                if (client == null || shooter == null || target == null) return;
                if (client.AmHost) { RecordRefusal(shooter, target, role); return; }
                var w = client.StartRpcImmediately(shooter.NetId, UTSRpc.CallId, Hazel.SendOption.Reliable, client.HostId);
                w.Write(RpcId);
                w.Write(SubRefused);
                w.Write(target.PlayerId);
                w.Write(role);
                client.FinishRpcImmediately(w);
            } catch (Exception e) {
                UsefulTORStuffPlugin.Logger?.LogWarning($"[SheriffHardProof] refusal report failed: {e.Message}");
            }
        }

        private static void RecordRefusal(PlayerControl shooter, PlayerControl target, byte role) {
            if (refusedLog.Count >= 30) return;
            int secs = Mathf.Max(0, Mathf.RoundToInt(Time.time - roundStart));
            string line = $"{(role == RoleThief ? "Thief" : "Sheriff")} {shooter?.Data?.PlayerName ?? "?"} → {target?.Data?.PlayerName ?? "?"} ({secs} s)";
            refusedLog.Add(line);
            logPending = true;
            UsefulTORStuffPlugin.Logger?.LogInfo($"[SheriffHardProof] host log: {line}");
        }

        /// <summary>Host, back in the lobby: the refused shots of the last round as chat lines only he sees.</summary>
        private static void PrintLogInLobby() {
            if (!logPending) return;
            try {
                var client = AmongUsClient.Instance;
                var hud = HudManager.Instance;
                var lp = PlayerControl.LocalPlayer;
                if (client == null || !client.AmHost || client.GameState != InnerNet.InnerNetClient.GameStates.Joined
                    || LobbyBehaviour.Instance == null || hud == null || hud.Chat == null || lp == null) return;
                logPending = false;
                hud.Chat.AddChat(lp, UTSLocalization.Tr("uts.sheriffproof.log_header") + "\n" + string.Join("\n", refusedLog));
                refusedLog.Clear();
            } catch { }
        }

        // ---- the events that count ----

        [HarmonyPatch(typeof(PlayerControl), nameof(PlayerControl.MurderPlayer))]
        static class MurderPatch {
            // The Vampire's delayed bite death is no hard proof even with the Vampire standing next to it (the user's
            // pick: the bite itself is not visible); his normal kill near garlic still counts.
            public static void Prefix(PlayerControl __instance, [HarmonyArgument(0)] PlayerControl target, out bool __state) {
                __state = false;
                try {
                    __state = __instance != null && target != null && Vampire.vampire != null && Vampire.bitten != null
                              && __instance.PlayerId == Vampire.vampire.PlayerId && target.PlayerId == Vampire.bitten.PlayerId;
                } catch { }
            }
            public static void Postfix(PlayerControl __instance, [HarmonyArgument(0)] PlayerControl target, bool __state) {
                try {
                    if (__instance == null || target == null || __instance.PlayerId == target.PlayerId) return;
                    Vector2 body = target.GetTruePosition();
                    BodyDropped(target, body);
                    if (__state) return;
                    if ((__instance.GetTruePosition() - body).magnitude > KillerNearBody) return;   // curse, bite, blast
                    Witness(__instance, body, "kill");
                } catch { }
            }
        }

        // User 27.11.: "Es kann auch sein, dass der Sheriff gerade die Leiche umfallen sieht und nur noch eine Person in der
        // Naehe ist." Any death (also a bite, a curse or a blast) whose body falls in the Sheriff's (or his Lover's) view,
        // with exactly ONE living player within kill range of it. Everybody near counts towards that one, seen or not (in
        // the dark, invisible, chameleon-faded), so a hidden killer next to an innocent never pins it on the innocent;
        // the one must then be on the viewer's screen. Suicides (misfire, Lover grief) are no deaths of this kind.
        private const float NearBody = 3.2f;

        private static void BodyDropped(PlayerControl victim, Vector2 body) {
            try {
                if (!RuleActive()) return;
                var lp = PlayerControl.LocalPlayer;
                PlayerControl only = null;
                int n = 0;
                foreach (var p in PlayerControl.AllPlayerControls) {
                    if (!Alive(p) || p.PlayerId == victim.PlayerId || (lp != null && p.PlayerId == lp.PlayerId)) continue;
                    if ((p.GetTruePosition() - body).sqrMagnitude > NearBody * NearBody) continue;
                    n++; only = p;
                    if (n > 1) return;
                }
                if (n != 1) return;
                // the body must be on screen too: Witness checks the one's spot; the body's own spot is checked here
                var ship = ShipStatus.Instance;
                if (lp == null || ship == null) return;
                float r;
                try { r = ship.CalculateLightRadius(lp.Data); } catch { r = 1f; }
                bool bodySeen = (!ScreenCovered() && RoundReplay.CanSee(lp.GetTruePosition(), r, body)) || OnCameras(ship, body);
                if (!bodySeen) return;
                Witness(only, only.GetTruePosition(), "alone at a body that fell");
            } catch { }
        }

        [HarmonyPatch(typeof(RPCProcedure), nameof(RPCProcedure.morphlingMorph))]
        static class MorphPatch {
            public static void Postfix() {
                var m = Morphling.morphling;
                if (m != null) Witness(m, m.GetTruePosition(), "morph");
            }
        }

        // Turning back is just as visible as turning: TOR ends the morph on a local timer, without an RPC.
        private static bool wasMorphed;

        [HarmonyPatch(typeof(RPCProcedure), nameof(RPCProcedure.placeNinjaTrace))]
        static class NinjaPatch {
            public static void Postfix([HarmonyArgument(0)] byte[] buff) {
                try {
                    var n = Ninja.ninja;
                    if (n == null || buff == null || buff.Length < 8) return;
                    // the trace marks where he vanished; he lands on his victim, which the kill covers
                    Witness(n, new Vector2(BitConverter.ToSingle(buff, 0), BitConverter.ToSingle(buff, 4)), "ninja jump");
                } catch { }
            }
        }

        [HarmonyPatch(typeof(RPCProcedure), nameof(RPCProcedure.yoyoBlink))]
        static class YoyoPatch {
            // where he leaves and where he arrives: both are the blink
            public static void Prefix() {
                var y = Yoyo.yoyo;
                if (y != null) Witness(y, y.GetTruePosition(), "yoyo blink");
            }
            public static void Postfix() {
                var y = Yoyo.yoyo;
                if (y != null) Witness(y, y.GetTruePosition(), "yoyo blink");
            }
        }

        [HarmonyPatch(typeof(RPCProcedure), nameof(RPCProcedure.cleanBody))]
        static class CleanPatch {
            public static void Prefix([HarmonyArgument(0)] byte playerId, [HarmonyArgument(1)] byte cleaningPlayerId) {
                try {
                    var cleaner = Helpers.playerById(cleaningPlayerId);
                    if (cleaner == null) return;
                    Vector2 at = cleaner.GetTruePosition();
                    foreach (var b in UnityEngine.Object.FindObjectsOfType<DeadBody>())
                        if (b != null && b.ParentId == playerId) { at = b.TruePosition; break; }
                    Witness(cleaner, at, "body cleaned");
                } catch { }
            }
        }

        // TOR's Bomber is NOT watched: his bomb is hidden from the crew until it arms (Objects/Bomb.cs), so nobody
        // sees it being laid. The bomb rule (User 27.11.) is the Maniac's below: handed to the Sheriff himself.

        // ---- Unknown's Collection (no compile reference: patched and read by reflection when it is loaded) ----

        private static bool ucTried;
        private static FieldInfo fiManiac, fiWerewolf, fiWolfForm, fiSkinwalker, fiFaker, fiCopycat, fiNecromancer;
        private static float nextWolfCheck;

        private static void HookCollection() {
            if (ucTried) return;
            ucTried = true;
            try {
                var maniac = AccessTools.TypeByName("UnknownsCollection.Maniac");
                if (maniac != null) {
                    fiManiac = AccessTools.Field(maniac, "maniac");
                    fiCarrier = AccessTools.Field(maniac, "bombCarrier");
                    fiDetected = AccessTools.Field(maniac, "bombDetected");
                    var plant = AccessTools.Method(maniac, "ApplyPlantBomb");
                    var pass = AccessTools.Method(maniac, "ApplyPassBomb");
                    var pre = new HarmonyMethod(AccessTools.Method(typeof(SheriffHardProof), nameof(BombHandPrefix)));
                    var post = new HarmonyMethod(AccessTools.Method(typeof(SheriffHardProof), nameof(BombHandPostfix)));
                    if (fiCarrier != null && fiDetected != null) {
                        if (plant != null) UsefulTORStuffPlugin.HarmonyInstance?.Patch(plant, prefix: pre, postfix: post);
                        if (pass != null) UsefulTORStuffPlugin.HarmonyInstance?.Patch(pass, prefix: pre, postfix: post);
                    }
                }
                fiSkinwalker = Hook("UnknownsCollection.Skinwalker", "ApplySkin", "skinwalker", nameof(SkinPostfix));
                fiFaker = Hook("UnknownsCollection.Faker", "ApplyPlace", "faker", nameof(FakerPostfix));
                fiCopycat = Hook("UnknownsCollection.Copycat", "ApplyUseAbility", "copycat", nameof(CopycatPostfix));
                fiNecromancer = Hook("UnknownsCollection.Necromancer", "ApplyRaise", "necromancer", nameof(RaisePostfix));
                var wolf = AccessTools.TypeByName("UnknownsCollection.Werewolf");
                if (wolf != null) {
                    fiWerewolf = AccessTools.Field(wolf, "werewolf");
                    fiWolfForm = AccessTools.Field(wolf, "wolfForm");
                }
                UsefulTORStuffPlugin.Logger?.LogInfo($"[SheriffHardProof] Collection hooks: maniac {fiManiac != null}, werewolf {fiWerewolf != null && fiWolfForm != null}, " +
                    $"skinwalker {fiSkinwalker != null}, faker {fiFaker != null}, copycat {fiCopycat != null}, necromancer {fiNecromancer != null}.");
            } catch (Exception e) {
                UsefulTORStuffPlugin.Logger?.LogWarning($"[SheriffHardProof] Collection hooks failed: {e.Message}");
            }
        }

        /// <summary>Postfix <paramref name="postfix"/> on <paramref name="type"/>.<paramref name="method"/>; returns the
        /// role's player field, or null when the role or method is not there (older Collection).</summary>
        private static FieldInfo Hook(string type, string method, string field, string postfix) {
            try {
                var t = AccessTools.TypeByName(type);
                var f = t != null ? AccessTools.Field(t, field) : null;
                var m = t != null ? AccessTools.Method(t, method) : null;
                if (f == null || m == null) return null;
                UsefulTORStuffPlugin.HarmonyInstance?.Patch(m,
                    postfix: new HarmonyMethod(AccessTools.Method(typeof(SheriffHardProof), postfix)));
                return f;
            } catch { return null; }
        }

        private static PlayerControl Get(FieldInfo f) { try { return f?.GetValue(null) as PlayerControl; } catch { return null; } }

        // Skinwalker puts on his victim's skin: the body vanishes and he changes look, like a morph.
        private static void SkinPostfix() { var p = Get(fiSkinwalker); if (p != null) Witness(p, p.GetTruePosition(), "skin (Skinwalker)"); }

        // Faker drops a fake body where he stands: a body appearing out of nothing next to him.
        private static void FakerPostfix(object[] __args) {
            try {
                var p = Get(fiFaker);
                if (p == null) return;
                Vector2 at = __args != null && __args.Length > 1 && __args[1] is Vector2 v ? v : p.GetTruePosition();
                Witness(p, at, "fake body (Faker)");
            } catch { }
        }

        // Copycat: only a copied morph counts (a copied shot is a kill and counts there; camo, shield, vent never do).
        private static void CopycatPostfix(object[] __args) {
            try {
                if (__args == null || __args.Length < 1 || Convert.ToByte(__args[0]) != 1) return;   // Ability.Morphling
                var p = Get(fiCopycat);
                if (p != null) Witness(p, p.GetTruePosition(), "morph (Copycat)");
            } catch { }
        }

        // Necromancer raises a body into a thrall: a corpse standing up at his feet.
        private static void RaisePostfix() { var p = Get(fiNecromancer); if (p != null) Witness(p, p.GetTruePosition(), "raise (Necromancer)"); }

        // ---- the Maniac's bomb (User 27.11.: only when it is handed to the Sheriff himself, and only if it can have been
        // just one player) ----
        // When the bomb lands on the Sheriff (planted by the Maniac or passed on by its carrier), every alive player within
        // reach of him is a suspect. Exactly one suspect becomes proof once the Sheriff notices the bomb (the Maniac's
        // warning, or at once if it is already ticking); two or more, or none, prove nothing. A passed bomb can come
        // from an innocent carrier: the user's call, the Sheriff then risks his misfire as usual.
        private static FieldInfo fiCarrier, fiDetected;
        private const float BombReach = 3.2f;
        private static byte bombSuspect = byte.MaxValue;
        private static byte carrierBefore = byte.MaxValue;

        private static void BombHandPrefix() {
            try { carrierBefore = (fiCarrier?.GetValue(null) as PlayerControl)?.PlayerId ?? byte.MaxValue; } catch { carrierBefore = byte.MaxValue; }
        }

        private static void BombHandPostfix() {
            try {
                if (!RuleActive()) return;
                var lp = PlayerControl.LocalPlayer;
                var carrier = fiCarrier?.GetValue(null) as PlayerControl;
                if (!Alive(lp) || !IsShooter(lp) || carrier == null) return;
                if (carrier.PlayerId != lp.PlayerId || carrierBefore == lp.PlayerId) return;
                Vector2 me = lp.GetTruePosition();
                PlayerControl only = null;
                int n = 0;
                foreach (var p in PlayerControl.AllPlayerControls) {
                    if (!Alive(p) || p.PlayerId == lp.PlayerId || p.inVent) continue;
                    if ((p.GetTruePosition() - me).sqrMagnitude > BombReach * BombReach) continue;
                    n++; only = p;
                }
                bombSuspect = n == 1 ? only.PlayerId : byte.MaxValue;
                UsefulTORStuffPlugin.Logger?.LogInfo($"[SheriffHardProof] the bomb reached the Sheriff with {n} player(s) in reach" +
                    (n == 1 ? $": {only.Data?.PlayerName} is the suspect." : ", nobody to blame."));
            } catch { }
        }

        private static void BombTick() {
            if (bombSuspect == byte.MaxValue) return;
            try {
                var lp = PlayerControl.LocalPlayer;
                var carrier = fiCarrier?.GetValue(null) as PlayerControl;
                if (!IsShooter(lp) || carrier == null || carrier.PlayerId != lp.PlayerId) { bombSuspect = byte.MaxValue; return; }
                if (!(fiDetected?.GetValue(null) is bool known) || !known) return;
                var p = Helpers.playerById(bombSuspect);
                bombSuspect = byte.MaxValue;
                if (p != null) Grant(p, "handed the shooter the bomb (Maniac)", true);
            } catch { bombSuspect = byte.MaxValue; }
        }

        [HarmonyPatch(typeof(HudManager), nameof(HudManager.Update))]
        static class TickPatch {
            public static void Postfix() {
                try {
                    PrintLogInLobby();
                    if (!RuleActive()) { wasMorphed = false; return; }
                    var m = Morphling.morphling;
                    bool morphed = m != null && Morphling.morphTimer > 0f;
                    if (wasMorphed && !morphed && m != null) Witness(m, m.GetTruePosition(), "morph ends");
                    wasMorphed = morphed;
                    BombTick();
                    if (fiWerewolf == null || Time.time < nextWolfCheck) return;
                    nextWolfCheck = Time.time + 0.2f;
                    if (fiWolfForm.GetValue(null) is bool form && form && fiWerewolf.GetValue(null) is PlayerControl w && Alive(w))
                        Witness(w, w.GetTruePosition(), "werewolf form");
                } catch { }
            }
        }

        // ---- the shot ----

        private static FieldInfo fiSheriffButton, fiThiefButton;

        private static CustomButton TorButton(ref FieldInfo fi, string name) {
            fi ??= typeof(CustomOption).Assembly.GetType("TheOtherRoles.HudManagerStartPatch")
                ?.GetField(name, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static);
            return fi?.GetValue(null) as CustomButton;
        }

        [HarmonyPatch(typeof(CustomButton), nameof(CustomButton.onClickEvent))]
        static class SheriffShotPatch {
            [HarmonyPriority(Priority.First)]
            public static bool Prefix(CustomButton __instance) {
                try {
                    if (__instance == null || !RuleActive()) return true;
                    var lp = PlayerControl.LocalPlayer;
                    if (!Alive(lp) || !IsShooter(lp)) return true;
                    PlayerControl target;
                    byte role;
                    if (Sheriff.sheriff != null && lp.PlayerId == Sheriff.sheriff.PlayerId && TorButton(ref fiSheriffButton, "sheriffKillButton") == __instance) {
                        target = Sheriff.currentTarget; role = RoleSheriff;
                    } else if (Thief.thief != null && lp.PlayerId == Thief.thief.PlayerId && TorButton(ref fiThiefButton, "thiefKillButton") == __instance) {
                        target = Thief.currentTarget; role = RoleThief;
                    } else return true;
                    // the same gate as TOR's own click: only a click that would really shoot is judged
                    if (!(__instance.Timer < 0f) || __instance.HasButton == null || !__instance.HasButton()
                        || __instance.CouldUse == null || !__instance.CouldUse()) return true;
                    if (target == null || caught.Contains(target.PlayerId)) return true;

                    __instance.Timer = BlockCooldown != null ? BlockCooldown.getFloat() : 8f;
                    var hud = HudManager.Instance;
                    if (hud != null && hud.Chat != null)
                        hud.Chat.AddChat(lp, UTSLocalization.Tr("uts.sheriffproof.refused"));
                    UsefulTORStuffPlugin.Logger?.LogInfo($"[SheriffHardProof] refused the {(role == RoleThief ? "Thief" : "Sheriff")}'s shot on {target.Data?.PlayerName}: no hard proof seen before the first meeting.");
                    ReportRefusal(lp, target, role);
                    return false;
                } catch { return true; }
            }
        }

        // ---- round boundaries ----

        [HarmonyPatch(typeof(MeetingHud), nameof(MeetingHud.Start))]
        static class MeetingPatch {
            public static void Postfix() {
                if (!meetingSeen && Enabled != null && Enabled.getBool())
                    UsefulTORStuffPlugin.Logger?.LogInfo("[SheriffHardProof] first meeting: the Sheriff shoots freely from now on.");
                meetingSeen = true;
            }
        }

        [HarmonyPatch(typeof(IntroCutscene), nameof(IntroCutscene.OnDestroy))]
        static class IntroPatch {
            public static void Prefix() {
                HookCollection();
                roundStart = Time.time;
                refusedLog.Clear();        // the list is per round; an unread one from the round before goes
                logPending = false;
            }
        }

        [HarmonyPatch(typeof(RPCProcedure), nameof(RPCProcedure.resetVariables))]
        static class ResetPatch {
            public static void Postfix() => Reset();
        }

        [HarmonyPatch(typeof(AmongUsClient), nameof(AmongUsClient.OnGameJoined))]
        static class JoinPatch {
            public static void Postfix() => Reset();
        }
    }
}
