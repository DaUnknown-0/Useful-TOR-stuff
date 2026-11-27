using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using HarmonyLib;
using Hazel;
using TheOtherRoles;
using TheOtherRoles.Objects;
using TheOtherRoles.Utilities;
using UnityEngine;
using static TheOtherRoles.TheOtherRoles;

namespace UsefulTORStuff {
    internal static class AuditFixA_RpcBoundary {
        private static readonly Type rpcType = TorType.Get("TheOtherRoles.CustomRPC");
        private static readonly Dictionary<byte, string> names = Enum.GetValues(rpcType).Cast<object>()
            .ToDictionary(x => Convert.ToByte(x), x => Enum.GetName(rpcType, x));
        private static readonly HashSet<byte> garlicPlaced = new();
        private static readonly Dictionary<string, float> acceptedAt = new();
        private static readonly HashSet<byte> meetingShots = new();
        private static readonly HashSet<bool> propTimerPhases = new();
        private static readonly Dictionary<int, (byte target, float until)> thiefKills = new();
        private static readonly Dictionary<byte, float> diedAt = new();
        private static Vector2 recentBombPosition;
        private static float recentBombRange, recentBombUntil = -1f;
        private static PlayerControl recentBomber;
        private static float nextLog;
        internal static bool Playing => AmongUsClient.Instance != null
            && AmongUsClient.Instance.GameState == InnerNet.InnerNetClient.GameStates.Started;
        internal static bool Alive(PlayerControl player) => UnknownsNet.SenderAlive(player);
        internal static bool Host(PlayerControl player) => UTSRpc.IsHost(player);
        internal static PlayerControl Player(byte id) => Helpers.playerById(id);
        private static bool Exists(byte id) => Player(id)?.Data != null;
        private static bool Living(byte id) => Alive(Player(id));
        private static bool Owner(PlayerControl sender, PlayerControl owner) => owner != null
            && sender != null && (sender == owner || Host(sender));
        internal static object Field(string type, string field) => AccessTools.Field(TorType.Get(type), field)?.GetValue(null);
        private static bool Prop => Field("TheOtherRoles.CustomGameModes.PropHunt", "isPropHuntGM") is true;
        private static bool Hide => Field("TheOtherRoles.CustomGameModes.HideNSeek", "isHideNSeekGM") is true;
        private static bool InDictionary(string field, byte id) =>
            Field("TheOtherRoles.CustomGameModes.PropHunt", field) is IDictionary dictionary && dictionary.Contains(id);
        private static bool PropFlag(string field) => Field("TheOtherRoles.CustomGameModes.PropHunt", field) is true;
        private static bool Near(PlayerControl sender, PlayerControl target, float range = 3f) => target != null
            && UnknownsNet.SenderNear(sender, target.GetTruePosition(), range);
        private static bool AttackRole(PlayerControl sender) => sender != null && (sender.Data?.Role?.IsImpostor == true
            || sender == Sheriff.sheriff || sender == Jackal.jackal || sender == Sidekick.sidekick
            || sender == Thief.thief || (LoverRevenger.revenger != null && sender == LoverRevenger.revenger));

        // Audit 16.11. UTS Teil 2: Thief-Selbstbruch bleibt erlaubt, Crew-Fernentpanzerung entfällt.
        internal static bool ArmorAttempt(PlayerControl sender, byte targetId) {
            var target = Player(targetId);
            if (!Playing || MeetingHud.Instance != null || !Alive(target)) return false;
            if (Host(sender)) return true;
            if ((sender == target || sender == Bomber.bomber || sender == recentBomber) && BombNear(target)) return true;
            bool recentDeath = sender != null && diedAt.TryGetValue(sender.PlayerId, out float death) && Time.time - death <= 1f;
            if (!Alive(sender) && !recentDeath) return false;
            bool? companion = AuditFixA_CompanionRoles.Attack(sender, target);
            if (companion.HasValue) return companion.Value && ExileController.Instance == null && IntroCutscene.Instance == null
                && (sender == target || Near(sender, target, 5f));
            if (sender == target) {
                if (sender != Thief.thief && sender != Sheriff.sheriff) return false;
                return PlayerControl.AllPlayerControls.ToArray().Any(p => Alive(p) && p != sender && Near(sender, p)
                    && (sender == Thief.thief
                        ? Thief.isFailedThiefKill(p, sender, RoleInfo.getRoleInfoForPlayer(p, false).FirstOrDefault())
                        : !SheriffTarget(p)));
            }
            // Die Bombe prüft auf jedem Opferclient und kann auch einen schon gestorbenen Bomber haben.
            if (sender == Bomber.bomber && Bomber.bomb?.bomb != null && Bomber.isActive)
                return Vector2.Distance(target.GetTruePosition(), Bomber.bomb.bomb.transform.position) <= Bomber.destructionRange + 2f;
            return AttackRole(sender) && (Vector2.Distance(sender.GetTruePosition(), target.GetTruePosition()) <= 5f
                || (sender == Vampire.vampire && Vampire.bitten == target)
                || (sender == Ninja.ninja && Ninja.ninjaMarked == target)
                || (sender == Warlock.warlock && Near(Warlock.curseVictim, target)));
        }

        private static bool Rate(PlayerControl sender, string key, float interval) {
            key = sender.OwnerId + ":" + key;
            if (acceptedAt.TryGetValue(key, out float previous) && Time.time - previous < interval) return false;
            return true;
        }
        private static bool SheriffTarget(PlayerControl target) => target.Data.Role.IsImpostor && (target != Mini.mini || Mini.isGrownUp())
            || Sheriff.spyCanDieToSheriff && target == Spy.spy || Sheriff.canKillNeutrals && Helpers.isNeutral(target)
            || target == Jackal.jackal || target == Sidekick.sidekick;
        private static bool BombNear(PlayerControl player) => player != null && (
            Bomber.isActive && Bomber.bomb?.bomb != null
                && Vector2.Distance(player.GetTruePosition(), Bomber.bomb.bomb.transform.position) <= Bomber.destructionRange + 2f
            || Time.time <= recentBombUntil && Vector2.Distance(player.GetTruePosition(), recentBombPosition) <= recentBombRange + 2f);
        private static bool Reporter(PlayerControl sender) => Alive(sender) && (sender == Mayor.mayor && Mayor.meetingButton
            && Mayor.remoteMeetingsLeft > 0 || sender.RemainingEmergencies > 0 || UnityEngine.Object.FindObjectsOfType<DeadBody>()
                .Any(body => UnknownsNet.SenderNear(sender, body.TruePosition, 3f)));
        private static bool Position(AuditFixA_Payload data, PlayerControl sender, bool near = true) {
            if (data.Packed() != 8) return false;
            var point = new Vector2(data.Float(), data.Float());
            return Mathf.Abs(point.x) < 1000 && Mathf.Abs(point.y) < 1000
                && (!near || UnknownsNet.SenderNear(sender, point, 3f));
        }
        private static bool RoleByte(byte role, bool modifier) => RoleInfo.allRoleInfos.Any(x => x != null
            && (byte)x.roleId == role && x.isModifier == modifier);

        // Audit 16.11. TOR-001/002/009: vor ALLEN TOR-/UTS-Empfängern, ohne Hazel-Cursor oder Payload zu ändern.
        // false stoppt keine Postfixe. ID 0 ist kein CustomRPC; der Vanilla-Rumpf wird ebenfalls übersprungen.
        [HarmonyPatch(typeof(PlayerControl), nameof(PlayerControl.HandleRpc))]
        private static class BoundaryPatch {
            [HarmonyPriority(int.MaxValue)]
            public static bool Prefix(PlayerControl __instance, ref byte callId, MessageReader reader, out bool __state) {
                AuditFixA_CompanionRoles.Observe(__instance, callId, reader);
                __state = names.ContainsKey(callId) || callId == 240 || callId >= 244 && callId <= 249 || callId >= 252;
                if (!__state) return true;
                byte incoming = callId;
                try {
                    if (__instance == null || __instance.Data == null || AmongUsClient.Instance == null)
                        throw new FormatException("Absender fehlt");
                    var data = new AuditFixA_Payload(reader);
                    bool valid = names.TryGetValue(callId, out string name)
                        ? ValidateTor(name, __instance, data) : ValidateUts(callId, __instance, data);
                    data.End();
                    if (valid) {
                        // Erst nach vollständiger Prüfung Wiederholungszustand fortschreiben.
                        if (name == "PlaceGarlic") garlicPlaced.Add(__instance.PlayerId);
                        if (name == "PropHuntStartTimer") propTimerPhases.Add(new AuditFixA_Payload(reader).Bool());
                        if (name == "ThiefStealsRole") thiefKills[__instance.OwnerId] = (new AuditFixA_Payload(reader).Byte(), Time.time + 3f);
                        if (name == "UncheckedMurderPlayer") thiefKills.Remove(__instance.OwnerId);
                        if (incoming == 246) acceptedAt[__instance.OwnerId + ":Mixup"] = Time.time;
                        return true;
                    }
                } catch (Exception) { }
                callId = 0;
                if (Time.unscaledTime >= nextLog) {
                    nextLog = Time.unscaledTime + 2f;
                    UsefulTORStuffPlugin.Logger?.LogWarning($"[Audit A] RPC {incoming} von Spieler {__instance?.PlayerId} verworfen.");
                }
                return false;
            }
            public static Exception Finalizer(Exception __exception, bool __state) {
                if (__exception == null || !__state) return __exception;
                // Audit 16.11. TOR-002: letzte Begrenzung für Ausnahmen in der Dispatcher-Patchkette.
                UsefulTORStuffPlugin.Logger?.LogWarning($"[Audit A] RPC-Verarbeitung begrenzt: {__exception.GetType().Name}");
                return null;
            }
        }

        private static bool ValidateTor(string name, PlayerControl sender, AuditFixA_Payload d) {
            bool host = Host(sender);
            byte a, b, c;
            switch (name) {
                case "ResetVaribles": case "ForceEnd": case "SetGameStarting": return host;
                case "ShareOptions": {
                    int count = d.Byte();
                    bool valid = host && count <= 200;
                    for (int i = 0; i < count; i++) {
                        uint id = d.Packed(), selection = d.Packed();
                        var option = CustomOption.options.FirstOrDefault(x => x != null && x.id == id);
                        if (selection > int.MaxValue || option != null && selection >= option.selections.Length) valid = false;
                    }
                    return valid;
                }
                case "WorkaroundSetRoles": {
                    int count = d.Byte();
                    bool valid = host && count <= 60;
                    for (int i = 0; i < count; i++) {
                        uint player = d.Packed(), role = d.Packed();
                        valid &= player <= 254 && role <= 255 && Exists((byte)player) && RoleByte((byte)role, false);
                    }
                    return valid;
                }
                case "SetRole": a = d.Byte(); b = d.Byte(); return host && RoleByte(a, false) && Exists(b);
                case "SetModifier": a = d.Byte(); b = d.Byte(); c = d.Byte(); return host && RoleByte(a, true) && Exists(b) && c <= 1;
                case "DynamicMapOption": a = d.Byte(); return host && a <= 6 && a != 3;
                case "ShareGamemode": a = d.Byte(); return host && a <= 3;
                case "SetGuesserGm": case "LawyerSetTarget": case "SetFirstKill":
                    return Exists(d.Byte()) && host;
                case "ShifterShift": case "ErasePlayerRoles": case "UncheckedExilePlayer":
                    return Exists(d.Byte()) && host && Playing;
                case "LawyerPromotesToPursuer": case "SetTiebreak": return host && Playing;
                case "VersionHandshake": {
                    d.Skip(3); float timer = d.Float(); uint owner = d.Packed();
                    if (d.Remaining != 0) d.Skip(17);
                    return owner == sender.OwnerId && (host || timer < 0);
                }
                case "StopStart": a = d.Byte(); return a == sender.PlayerId && CustomOptionHolder.anyPlayerCanStopStart.getBool();
                case "DraftModePickOrder": {
                    int count = d.Byte(); var ids = new HashSet<byte>();
                    bool valid = host && count <= PlayerControl.AllPlayerControls.Count;
                    for (int i = 0; i < count; i++) { a = d.Byte(); valid &= Exists(a) && ids.Add(a); }
                    return valid;
                }
                case "DraftModePick": a = d.Byte(); b = d.Byte(); return DraftPick(sender, a, b);
                case "ShareGhostInfo": return Ghost(sender, d);
                case "ShareRoom": a = d.Byte(); b = d.Byte(); return a == sender.PlayerId && Enum.IsDefined(typeof(SystemTypes), (SystemTypes)b);
            }
            if (!Playing) return false;
            if (!host && !Alive(sender) && name != "SidekickPromotes" && name != "SetInvisible"
                && name != "UncheckedMurderPlayer" && name != "BreakArmor") return false;
            switch (name) {
                case "UncheckedMurderPlayer":
                    a = d.Byte(); b = d.Byte(); c = d.Byte();
                    // Integration TOR-001/002: Helpers.MurderPlayer und Thief-Suizid schreiben
                    // das Animationsflag als Int32. TOR liest davon nur das erste Byte.
                    d.OptionalZeroPadding();
                    return (c == 0 || c == 255) && Murder(sender, a, b);
                case "UncheckedCmdReportDeadBody":
                    a = d.Byte(); b = d.Byte();
                    return Alive(sender) && a == sender.PlayerId && (b == 255
                        ? sender == Mayor.mayor && Mayor.meetingButton && Mayor.remoteMeetingsLeft > 0
                        : Exists(b) && !Living(b));
                case "UseUncheckedVent": {
                    uint vent = d.Packed(); a = d.Byte(); b = d.Byte();
                    return a == sender.PlayerId && (b == 0 || b == 255) && Alive(sender) && ShipStatus.Instance != null
                        && ShipStatus.Instance.AllVents.Any(v => v != null && v.Id == vent)
                        && Helpers.roleCanUseVents(sender);
                }
                case "GuesserShoot": a = d.Byte(); b = d.Byte(); c = d.Byte(); return Guess(sender, a, b, c, d.Byte());
                case "CleanBody":
                    a = d.Byte(); b = d.Byte(); return b == sender.PlayerId && Alive(sender)
                        && (sender == Cleaner.cleaner || sender == Janitor.janitor || sender == Vulture.vulture)
                        && !Living(a) && UnityEngine.Object.FindObjectsOfType<DeadBody>().Any(body => body.ParentId == a
                            && UnknownsNet.SenderNear(sender, body.TruePosition, 3f));
                case "EngineerUsedRepair": return Owner(sender, Engineer.engineer) && Engineer.remainingFixes > 0;
                // UsedRepair wird regulär vor dem eigentlichen Repair gesendet, deshalb hier auch Restladung null.
                case "EngineerFixLights": case "EngineerFixSubmergedOxygen": return Owner(sender, Engineer.engineer);
                case "MedicSetShielded": case "SetFutureShielded":
                    a = d.Byte(); return Living(a) && Owner(sender, Medic.medic) && (host || !Medic.usedShield);
                case "ShieldedMurderAttempt": return Medic.shielded != null && (host
                    || MeetingHud.Instance != null && (HandleGuesser.isGuesser(sender.PlayerId)
                        || AuditFixA_CompanionRoles.MixerShieldAttempt(sender))
                    || ArmorAttempt(sender, Medic.shielded.PlayerId));
                case "TimeMasterShield": return Alive(sender) && Owner(sender, TimeMaster.timeMaster) && !TimeMaster.shieldActive;
                case "TimeMasterRewindTime": return TimeMaster.shieldActive && TimeMaster.timeMaster != null && (host || ArmorAttempt(sender, TimeMaster.timeMaster.PlayerId));
                case "SetFutureShifted": a = d.Byte(); return Living(a) && Owner(sender, Shifter.shifter);
                case "SwapperSwap":
                    a = d.Byte(); b = d.Byte(); return MeetingHud.Instance != null && Owner(sender, Swapper.swapper)
                        && Living(a) && Living(b) && a != b;
                case "MayorSetVoteTwice": d.Bool(); return MeetingHud.Instance != null && Owner(sender, Mayor.mayor);
                // Integration TOR-001: ein lebend gesampeltes Ziel darf vor dem Morph sterben.
                case "MorphlingMorph": a = d.Byte(); return Exists(a) && Owner(sender, Morphling.morphling);
                case "CamouflagerCamouflage": return Owner(sender, Camouflager.camouflager);
                case "TrackerUsedTracker": a = d.Byte(); return Living(a) && Owner(sender, Tracker.tracker) && !Tracker.usedTracker;
                case "VampireSetBitten":
                    a = d.Byte(); b = d.Byte();
                    // Beim Report und beim Emergency sendet TOR auch ohne vorhandenen Biss einen Reset.
                    // Fix 27.11.: der Reset kommt auch vom Gebissenen selbst (sein Meeting, letzter Notfallknopf), und der
                    // Biss wird mit Netz-Verzoegerung gemessen (Killreichweite 2,5 plus Laufweg): eine Ablehnung liesse
                    // den Biss auf diesem Client fehlen und spaeter den Biss-Tod scheitern.
                    return b == 255 && a == 255 && (Owner(sender, Vampire.vampire) || Vampire.bitten == null || Reporter(sender)
                                                    || Vampire.bitten.PlayerId == sender.PlayerId)
                        || b == 0 && Living(a) && Owner(sender, Vampire.vampire) && Near(sender, Player(a), 6f);
                case "PlaceGarlic":
                    return Alive(sender) && Vampire.garlicsActive && Position(d, sender) && !garlicPlaced.Contains(sender.PlayerId);
                case "DeputyUsedHandcuffs":
                    a = d.Byte(); return Living(a) && (Owner(sender, Deputy.deputy) || Owner(sender, Sheriff.formerDeputy))
                        // Integration TOR-048: erneutes regulaeres Anlegen verbraucht auch lokal
                        // eine weitere Ladung. Empfaenger duerfen diesen Verbrauch nicht auslassen.
                        && Deputy.remainingHandcuffs > 0;
                case "DeputyPromotes": return Owner(sender, Deputy.deputy) && Deputy.promotesToSheriff > 0 && !Alive(Sheriff.sheriff);
                case "JackalCreatesSidekick": a = d.Byte(); return Living(a) && Owner(sender, Jackal.jackal) && Jackal.canCreateSidekick;
                case "SidekickPromotes": return Sidekick.promotesToJackal && !Alive(Jackal.jackal)
                    && (host || sender == Jackal.jackal || sender == Sidekick.sidekick);
                case "SetFutureErased": a = d.Byte(); return Living(a) && Owner(sender, Eraser.eraser) && !Eraser.futureErased.Contains(Player(a));
                case "SetFutureSpelled": a = d.Byte(); return Living(a) && Owner(sender, Witch.witch) && !Witch.futureSpelled.Contains(Player(a));
                case "PlaceNinjaTrace": return Owner(sender, Ninja.ninja) && Position(d, sender, false);
                case "PlacePortal": return Owner(sender, Portalmaker.portalmaker) && Position(d, sender);
                case "UsePortal": a = d.Byte(); b = d.Byte(); return a == sender.PlayerId && b <= 2 && Alive(sender);
                case "PlaceJackInTheBox": return Owner(sender, Trickster.trickster) && Position(d, sender);
                case "LightsOut": return Owner(sender, Trickster.trickster);
                case "PlaceCamera": return Owner(sender, SecurityGuard.securityGuard) && Position(d, sender);
                case "SealVent": {
                    uint vent = d.Packed(); return Owner(sender, SecurityGuard.securityGuard) && ShipStatus.Instance != null
                        && ShipStatus.Instance.AllVents.Any(v => v != null && v.Id == vent);
                }
                case "ArsonistWin": return ArsonistWinOk(sender);
                case "SetBlanked": a = d.Byte(); b = d.Byte(); return Exists(a) && (b == 0
                    ? host || a == sender.PlayerId || sender == Bomber.bomber
                    : b == 255 && Owner(sender, Pursuer.pursuer));
                case "Bloody": a = d.Byte(); b = d.Byte(); return Exists(a) && Exists(b)
                    && Bloody.bloody.Contains(Player(b)) && !Living(b);
                case "SetInvisible": a = d.Byte(); b = d.Byte(); return Owner(sender, Ninja.ninja)
                    && Ninja.ninja?.PlayerId == a && (b == 0 || b == 255);
                case "ThiefStealsRole": a = d.Byte(); return Living(a) && Owner(sender, Thief.thief) && Near(sender, Player(a))
                    && !Thief.isFailedThiefKill(Player(a), Thief.thief, RoleInfo.getRoleInfoForPlayer(Player(a), false).FirstOrDefault());
                case "SetTrap": return Owner(sender, Trapper.trapper) && Trapper.charges > 0 && Position(d, sender);
                case "TriggerTrap":
                    a = d.Byte(); b = d.Byte();
                    // TOR schreibt die Fallen-ID als int, liest aber nur byte. Den regulären 4-Byte-Sender erhalten.
                    if (d.Remaining == 3 && (d.Byte() != 0 || d.Byte() != 0 || d.Byte() != 0)) return false;
                    return a == sender.PlayerId && Alive(sender) && ValidTrap(sender, b);
                case "PlaceBomb": return Owner(sender, Bomber.bomber) && Bomber.bomb == null && Position(d, sender);
                case "DefuseBomb": return Alive(sender) && Bomber.bomb?.bomb != null
                    && UnknownsNet.SenderNear(sender, Bomber.bomb.bomb.transform.position, 3f);
                case "YoyoMarkLocation": return Owner(sender, Yoyo.yoyo) && Position(d, sender);
                case "YoyoBlink": a = d.Byte(); return (a == 0 || a == 255) && Owner(sender, Yoyo.yoyo) && Yoyo.markedLocation != null && Position(d, sender, false);
                case "BreakArmor": return Armored.armored != null && !Armored.isBrokenArmor && ArmorAttempt(sender, Armored.armored.PlayerId);
                case "HuntedShield": a = d.Byte(); return Hide && a == sender.PlayerId && Alive(sender) && !sender.Data.Role.IsImpostor;
                case "HuntedRewindTime": a = d.Byte(); return Hide && Living(a) && sender.Data.Role.IsImpostor;
                case "ShareTimer": {
                    float value = d.Float();
                    return Hide && Alive(sender) && sender.Data.Role.IsImpostor && (Equals(value, Field("TheOtherRoles.CustomGameModes.Hunter", "lightPunish"))
                        || Equals(value, Field("TheOtherRoles.CustomGameModes.Hunter", "AdminPunish")) || Equals(value, Field("TheOtherRoles.CustomGameModes.Hunter", "ArrowPunish")));
                }
                case "PropHuntStartTimer":
                    bool blackout = d.Bool();
                    // Audit 16.11. TOR-033: Intro und Countdown werden von jedem Hunter gesendet, auch Gästen.
                    return Prop && (host || sender.Data.Role.IsImpostor) && !propTimerPhases.Contains(blackout)
                        && Field("TheOtherRoles.CustomGameModes.PropHunt", "timerRunning") is false
                        && (!blackout || (float)Field("TheOtherRoles.CustomGameModes.PropHunt", "blackOutTimer") <= 0f);
                case "SetProp": a = d.Byte(); string prop = d.String(256); float x = d.Float();
                    return Prop && a == sender.PlayerId && Alive(sender) && !sender.Data.Role.IsImpostor && prop.Length > 0 && Mathf.Abs(x) < 1000;
                case "SetRevealed": a = d.Byte(); return Prop && Alive(sender) && sender.Data.Role.IsImpostor && Living(a)
                    && !Player(a).Data.Role.IsImpostor && !InDictionary("isCurrentlyRevealed", a);
                case "PropHuntSetInvis": a = d.Byte(); return Prop && a == sender.PlayerId && Alive(sender) && !sender.Data.Role.IsImpostor
                    && PropFlag("enableInvis") && !InDictionary("invisPlayers", a);
                case "PropHuntSetSpeedboost": a = d.Byte(); return Prop && a == sender.PlayerId && Alive(sender) && !sender.Data.Role.IsImpostor
                    && PropFlag("enableSpeedboost") && !InDictionary("speedboostActive", a);
                case "EventKick": a = d.Byte(); b = d.Byte(); float distance = d.Float();
                    return EventUtility.isEnabled && a == sender.PlayerId && Living(b) && Near(sender, Player(b)) && distance >= 1 && distance <= 2.5f;
                default: return false;
            }
        }

        private static bool Murder(PlayerControl sender, byte sourceId, byte targetId) {
            var source = Player(sourceId); var target = Player(targetId);
            if (!Alive(target) || source?.Data == null) return false;
            if (Host(sender)) return true;
            // Audit 16.11. TOR-001: TOR ändert die Thief-Rolle vor dem Kill. Genau dieses Ziel kurz vormerken.
            if (source == sender && Alive(sender) && thiefKills.TryGetValue(sender.OwnerId, out var theft)
                && theft.target == targetId && Time.time <= theft.until) return true;
            // Bombentode werden vom Opferclient gemeldet, nicht vom Bomber.
            if ((source == Bomber.bomber || source == recentBomber) && target == sender && BombNear(target)) return true;
            // Vampire-Biss (Fix 27.11., Spieltest: ein Gebissener war beim Host noch am Leben, obwohl er tot war; er hatte
            // das Meeting selbst gerufen). TOR fuehrt den Biss-Tod auf dem SENDER schon aus, eine Ablehnung hier spaltet
            // also nur die Lobby. Angenommen wird:
            //  - der verzoegerte Biss-Tod vom Vampire selbst, auch wenn dieser Client den Biss nicht kennt (die Biss-
            //    Nachricht kann hier verspaetet oder abgelehnt worden sein) oder der Vampire weit weg steht;
            //  - beim Meeting-Start die Aufloesung durch den Gebissenen selbst (er hat das Meeting gerufen; sein letzter
            //    Notfallknopf zaehlt dann nicht mehr als "Reporter") oder durch einen Reporter.
            if (source == Vampire.vampire && Alive(source)) {
                if (source == sender && (target == Vampire.bitten || Vampire.bitten == null || Near(sender, target))) return true;
                if (target == sender) return true;
                if (target == Vampire.bitten && Reporter(sender)) return true;
            }
            if (source == sender && Alive(sender) && MeetingHud.Instance == null) {
                bool? companion = AuditFixA_CompanionRoles.Attack(source, target);
                if (companion.HasValue) return companion.Value && ExileController.Instance == null && IntroCutscene.Instance == null
                    && (source == target || Near(source, target));
            }
            if (source != sender || !Alive(sender) || !(AttackRole(sender) || source == Thief.formerThief) || MeetingHud.Instance != null) return false;
            if (source == target) return source == Sheriff.sheriff || source == Thief.thief || source == LoverRevenger.revenger;
            if (source == Sidekick.sidekick && !Sidekick.canKill) return false;
            if (source == Sheriff.sheriff && !SheriffTarget(target)) return false;
            if (source == LoverRevenger.revenger && !source.Data.Role.IsImpostor && source != Sheriff.sheriff
                && source != Jackal.jackal && source != Sidekick.sidekick && source != Thief.thief
                && LoverRevenger.revengerMode == 0 && targetId != LoverRevenger.killerId) return false;
            return Near(sender, target) || source == Ninja.ninja && Ninja.ninjaMarked == target
                || source == Warlock.warlock && Near(Warlock.curseVictim, target);
        }

        private static bool Guess(PlayerControl sender, byte killer, byte dying, byte target, byte role) {
            if (MeetingHud.Instance == null || killer != sender.PlayerId || !Alive(sender) || !Living(dying) || !Living(target)
                || killer == target || !HandleGuesser.isGuesser(killer) || HandleGuesser.remainingShots(killer) <= 0
                || !HandleGuesser.hasMultipleShotsPerMeeting && meetingShots.Contains(killer)) return false;
            if (MeetingHud.Instance.state != MeetingHud.VoteStates.Voted && MeetingHud.Instance.state != MeetingHud.VoteStates.NotVoted) return false;
            if (!HandleGuesser.killsThroughShield && Player(target) == Medic.shielded) return false;
            return AuditFixA_CompanionRoles.Guess(killer, target, role, out byte expected, out bool correction)
                && (dying == expected || correction && (dying == killer || dying == target));
        }

        private static bool DraftPick(PlayerControl sender, byte player, byte role) {
            var order = Field("TheOtherRoles.Modules.RoleDraft", "pickOrder") as List<byte>;
            var picked = Field("TheOtherRoles.Modules.RoleDraft", "alreadyPicked") as List<byte>;
            var info = RoleInfo.allRoleInfos.FirstOrDefault(r => r != null && (byte)r.roleId == role && !r.isModifier);
            var picker = Player(player);
            return (Host(sender) || player == sender.PlayerId) && picker?.Data?.Role != null
                && order?.Count > 0 && order[0] == player && info != null
                && picker.Data.Role.IsImpostor == info.isImpostor
                // Crewmate und normaler Impostor dürfen mehrfach vorkommen: TORs Draft gibt sie als Notlösung erneut aus, wenn
                // nichts anderes übrig ist. Vorher verwarf der Filter diese zweite Impostor-Wahl, und der Draft hing (20.11.2026).
                && (role == (byte)RoleId.Crewmate || role == (byte)RoleId.Impostor || picked?.Contains(role) == false);
        }
        private static bool ValidTrap(PlayerControl sender, byte id) {
            if (Field("TheOtherRoles.Objects.Trap", "traps") is not IEnumerable traps) return false;
            foreach (object trap in traps) {
                var type = trap.GetType();
                if ((int)AccessTools.Field(type, "instanceId").GetValue(trap) != id) continue;
                var used = AccessTools.Field(type, "trappedPlayer").GetValue(trap) as List<byte>;
                var obj = AccessTools.Field(type, "trap").GetValue(trap) as GameObject;
                return obj != null && used?.Contains(sender.PlayerId) == false
                    && (bool)AccessTools.Field(type, "triggerable").GetValue(trap)
                    && UnknownsNet.SenderNear(sender, obj.transform.position, 1f);
            }
            return false;
        }

        /// <summary>ArsonistWin (fix 27.11., playtest: a guest Arsonist ignited, everybody died on HIS screen, the host
        /// refused the message and the round went on for everyone else, a desync). TOR's own button has already exiled
        /// everyone on the Arsonist's game before this message arrives, so refusing it can only split the lobby. The
        /// douse list on this client may lag the Arsonist's (a ShareGhostInfo that arrives late or was refused, a kill
        /// still in flight) and TOR itself compares douses by PlayerId, never by object. So: the sender must be the
        /// living Arsonist (that is the forgery guard); an incomplete douse list on THIS client is logged, not refused.</summary>
        private static bool ArsonistWinOk(PlayerControl sender) {
            if (!Owner(sender, Arsonist.arsonist) || !Alive(Arsonist.arsonist)) return false;
            try {
                var missing = PlayerControl.AllPlayerControls.ToArray()
                    .Where(p => p != null && Alive(p) && p.PlayerId != Arsonist.arsonist.PlayerId
                                && !Arsonist.dousedPlayers.Any(y => y != null && y.PlayerId == p.PlayerId))
                    .Select(p => p.Data?.PlayerName ?? p.PlayerId.ToString()).ToList();
                if (missing.Count > 0)
                    UsefulTORStuffPlugin.Logger?.LogWarning($"[Audit A] ArsonistWin accepted although this client has no douse for: {string.Join(", ", missing)} (the Arsonist's game already ignited).");
            } catch { }
            return true;
        }

        private static bool Ghost(PlayerControl sender, AuditFixA_Payload d) {
            byte id = d.Byte(), kind = d.Byte();
            bool same = id == sender.PlayerId;
            byte target;
            switch ((RPCProcedure.GhostInfoTypes)kind) {
                case RPCProcedure.GhostInfoTypes.HandcuffNoticed:
                    // Integration TOR-048: nach Apply entfernt TOR alle ausstehenden Eintraege.
                    // Ein neuer Cuff waehrend des bekannten Effekts darf erneut bemerkt werden.
                    return same && Deputy.handcuffedPlayers.Contains(id);
                case RPCProcedure.GhostInfoTypes.HandcuffOver: return same;
                case RPCProcedure.GhostInfoTypes.ArsonistDouse:
                    target = d.Byte(); return same && Owner(sender, Arsonist.arsonist) && Living(target) && !Arsonist.dousedPlayers.Any(y => y != null && y.PlayerId == target);
                case RPCProcedure.GhostInfoTypes.BountyTarget: target = d.Byte(); return same && sender == BountyHunter.bountyHunter && (target == 255 || Living(target));
                case RPCProcedure.GhostInfoTypes.NinjaMarked: target = d.Byte(); return same && sender == Ninja.ninja && (target == 255 || Living(target));
                case RPCProcedure.GhostInfoTypes.WarlockTarget: target = d.Byte(); return same && sender == Warlock.warlock && (target == 255 || Living(target));
                case RPCProcedure.GhostInfoTypes.MediumInfo: d.String(); return same && sender == Medium.medium;
                case RPCProcedure.GhostInfoTypes.DetectiveOrMedicInfo: d.String(); return same && (sender == Detective.detective || sender == Medic.medic);
                case RPCProcedure.GhostInfoTypes.BlankUsed: return same;
                case RPCProcedure.GhostInfoTypes.VampireTimer: d.Byte(); return same && sender == Vampire.vampire;
                case RPCProcedure.GhostInfoTypes.DeathReasonAndKiller:
                    target = d.Byte(); byte reason = d.Byte(), killer = d.Byte();
                    return same && Exists(target) && !Living(target) && Exists(killer) && reason <= 9
                        && (Host(sender) || target == id || killer == id);
                default: return false;
            }
        }

        private static bool ValidateUts(byte call, PlayerControl sender, AuditFixA_Payload d) {
            if (call == 240) call = d.Byte();
            switch (call) {
                case 229: { byte kind = d.Byte(); d.Byte(); return kind == 0 ? Host(sender) : kind == 1; }
                case 232: return d.Byte() == 0 && EasterEggCoding.IsOwner(sender);
                case 234: { byte target = d.Byte(); return sender == Mayor.mayor && MeetingHud.Instance != null
                        && (target == 255 || target == 253 || Living(target)); }
                case 235: { byte kind = d.Byte(); if (kind == 0) { d.String(); byte target = d.Byte(); return target == 255 || Exists(target); }
                    if (kind == 1) { byte device = d.Byte(); d.Bool(); return device >= 1 && device <= 8; } return false; }
                case 236: return true; // Kein Payload; DraftLawyerConflict prüft den offenen Claim beim Grant.
                case 237: return Host(sender); // Kein Payload, nur Host setzt den Draft-Timer zurück.
                case 238: { byte kind = d.Byte(); if (kind == 0) return true;
                    if (!Host(sender) || kind > 5) return false;
                    d.Skip(d.Remaining); return true; } // Vertrauenswürdige Statistikdaten, eigener begrenzter Empfänger.
                case 239: { byte kind = d.Byte(); if (kind == 3) return true;
                    // Integration A/D: Subtyp 5 beendet auch eine leere Rundenzuweisung.
                    if (!Host(sender) || kind > 5) return false;
                    d.Skip(d.Remaining); return true; }
                case 241: { byte kind = d.Byte(); if (kind == 0) return Host(sender) && IdList(d);
                    if (kind == 1) return Host(sender) && Exists(d.Byte()); return kind == 2 && Host(sender); }
                case 242: { byte kind = d.Byte(); return kind == 0 ? Host(sender) && IdList(d) : kind == 1 && Host(sender); }
                case 243: return Host(sender) && IdList(d);
                case 244: d.Bool(); return Host(sender);
                case 245: return d.Byte() == 0 && ArmorAttempt(sender, d.Byte());
                case 246: return Mixup(sender, d);
                // Revenger hat zustandsbezogene Prüfungen direkt am Empfänger.
                case 247: return LoverRevenger.ValidateIncoming(sender, d);
                // Die übrigen Kanäle haben bereits eigene Host-/Owner-Prüfungen und begrenzte Reader-Catches.
                case 248: d.Bool(); return Owner(sender, Trapper.trapper);
                case 249: return Owner(sender, Medic.medic);
                case 252: return Owner(sender, Bomber.bomber);
                case 253: d.Skip(3); uint owner = d.Packed(); if (d.Remaining != 0) d.Skip(17); return owner == sender.OwnerId;
                case 254: float x = d.Float(), y = d.Float(); return Alive(sender) && MeetingHud.Instance != null && Mathf.Abs(x) < 1000 && Mathf.Abs(y) < 1000;
                case 255: {
                    uint count = d.Packed(); if (count > 512) return false;
                    for (uint i = 0; i < count; i++) { d.Byte(); d.Byte(); for (int j = 0; j < 4; j++) d.Packed(); }
                    if (d.Remaining != 0) d.Packed(); return true;
                }
                default: return false;
            }
        }
        private static bool IdList(AuditFixA_Payload data) {
            int count = data.Byte(); var ids = new HashSet<byte>();
            bool valid = count <= PlayerControl.AllPlayerControls.Count;
            for (int i = 0; i < count; i++) { byte id = data.Byte(); valid &= Exists(id) && ids.Add(id); }
            return valid;
        }
        private static bool Mixup(PlayerControl sender, AuditFixA_Payload d) {
            int count = d.Byte(); var targets = new HashSet<byte>(); var sources = new HashSet<byte>();
            bool valid = Playing && MeetingHud.Instance == null && Alive(sender) && Owner(sender, Trickster.trickster)
                && TricksterAvatarSabotage.Option != null && UTSGate.Bool(TricksterAvatarSabotage.Option)
                && UsefulVersionHandshake.EveryoneHasMod() && !TricksterAvatarSabotage.IsMixupActive
                && Camouflager.camouflageTimer <= 0 && Trickster.lightsOutTimer <= 0
                && count >= 2 && count <= PlayerControl.AllPlayerControls.Count;
            for (int i = 0; i < count; i++) {
                byte target = d.Byte(), source = d.Byte();
                valid &= target != source && Living(target) && Living(source) && targets.Add(target) && sources.Add(source);
            }
            float duration = d.Float();
            valid &= targets.SetEquals(sources) && duration > 0 && duration <= 30
                && TricksterAvatarSabotage.DurationOption != null && Mathf.Abs(duration - UTSGate.Num(TricksterAvatarSabotage.DurationOption)) < 0.01f;
            return valid && Rate(sender, "Mixup", duration + UTSGate.Num(TricksterAvatarSabotage.CooldownOption) - 0.5f);
        }

        [HarmonyPatch(typeof(RPCProcedure), nameof(RPCProcedure.guesserShoot))]
        private static class ShotPatch {
            [HarmonyPriority(int.MaxValue)]
            public static void Prefix(byte killerId, ref byte dyingTargetId, byte guessedTargetId, byte guessedRoleId, out bool __state) {
                AuditFixA_CompanionRoles.PrepareShot(killerId, ref dyingTargetId, guessedTargetId, guessedRoleId);
                __state = Living(dyingTargetId);
            }
            public static void Postfix(byte killerId, byte dyingTargetId, bool __state) {
                if (__state && !Living(dyingTargetId)) meetingShots.Add(killerId);
            }
        }
        [HarmonyPatch(typeof(MeetingHud), nameof(MeetingHud.Start))]
        private static class MeetingPatch { public static void Prefix() { meetingShots.Clear(); } }
        [HarmonyPatch(typeof(MeetingHud), nameof(MeetingHud.Close))]
        private static class MeetingClosePatch { public static void Postfix() { AuditFixA_CompanionRoles.Clear(); } }
        [HarmonyPatch(typeof(RPCProcedure), nameof(RPCProcedure.resetVariables))]
        private static class ResetPatch { public static void Postfix() { Clear(); } }
        [HarmonyPatch(typeof(AmongUsClient), nameof(AmongUsClient.OnGameJoined))]
        private static class JoinPatch { public static void Postfix() { Clear(); } }
        // Audit 16.11. TOR-001: Opferpakete dürfen kurz nach dem lokalen Abräumen der Explosion eintreffen.
        [HarmonyPatch(typeof(Bomb), nameof(Bomb.explode))]
        private static class BombSnapshotPatch {
            // Integration A/C: Position vor dem ersetzenden Explosions-Prefix sichern.
            [HarmonyPriority(Priority.First)]
            public static void Prefix(Bomb b) {
                if (b?.bomb == null || !Bomber.isActive) return;
                recentBombPosition = b.bomb.transform.position;
                recentBombRange = Bomber.destructionRange;
                recentBomber = Bomber.bomber;
                recentBombUntil = Time.time + 3f;
            }
        }
        // Audit 16.11. UTS Teil 2: gleichzeitiger Tod des Angreifers verwirft keinen bereits ausgelösten Rüstungsbruch.
        [HarmonyPatch(typeof(PlayerControl), nameof(PlayerControl.MurderPlayer))]
        private static class RecentDeathPatch {
            [HarmonyPriority(int.MaxValue)]
            public static void Prefix([HarmonyArgument(0)] PlayerControl target, out bool __state) { __state = Alive(target); }
            public static void Postfix([HarmonyArgument(0)] PlayerControl target, bool __state) {
                if (__state && target?.Data?.IsDead == true) diedAt[target.PlayerId] = Time.time;
            }
        }
        private static void Clear() {
            AuditFixA_CompanionRoles.Clear();
            garlicPlaced.Clear(); acceptedAt.Clear(); meetingShots.Clear(); propTimerPhases.Clear(); thiefKills.Clear();
            recentBombUntil = -1f; recentBomber = null; diedAt.Clear();
        }
    }

    // Audit 16.11. TOR-009: auch die regulären Sendemethoden dürfen keinen Gast-Sync starten.
    [HarmonyPatch(typeof(CustomOption), nameof(CustomOption.ShareOptionSelections))]
    internal static class AuditFixA_ShareOptionsSend {
        [HarmonyPriority(int.MaxValue)]
        private static bool Prefix() => AmongUsClient.Instance != null && AmongUsClient.Instance.AmHost;
    }
    [HarmonyPatch(typeof(CustomOption), nameof(CustomOption.ShareOptionChange))]
    internal static class AuditFixA_ShareOptionChangeSend {
        [HarmonyPriority(int.MaxValue)]
        private static bool Prefix() => AmongUsClient.Instance != null && AmongUsClient.Instance.AmHost;
    }
}
