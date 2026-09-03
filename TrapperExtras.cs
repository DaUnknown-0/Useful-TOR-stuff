// Useful TOR Stuff - Copyright (C) 2026 DaUnknown-0
// Licensed under GPL-3.0-or-later. See LICENSE for details.
// Based on The Other Roles (https://github.com/TheOtherRolesAU/TheOtherRoles), GPL-3.0.

/*
 * TrapperExtras - the trapper can find their own traps, and keep the trap log.
 *
 * TWO GAPS IN TOR'S TRAPPER, both about information the trapper already owns but cannot reach.
 *
 *  1) THE LOG NAMES A TRAP THE TRAPPER CANNOT LOCATE. TOR writes "Trap 3:" followed by who walked
 *     into it (MeetingPatch.cs:714), using Trap.instanceId - a counter that already runs 1..X. The
 *     trapper is never shown WHERE trap 3 is. The traps are visible in the world to them
 *     (Trap.cs:40) but only on the screen they are standing on, so on Airship or Polus a number is
 *     an answer to a question they cannot ask. Placing the same numbers on the map closes that.
 *
 *  2) THE LOG IS GONE THE MOMENT THE MEETING ENDS. It is written into the meeting chat, and the
 *     traps that produced it are destroyed a few lines later (Trap.clearRevealedTraps, called from
 *     the same prefix at MeetingPatch.cs:763). After the meeting there is no way back to it - and
 *     it cannot simply be re-posted into the chat either, because the chat is hidden during a round
 *     (that is LobbyLeakGuard's job). So the log is captured while it still exists and shown in a
 *     panel of our own.
 *
 * NEITHER GIVES THE TRAPPER ANYTHING NEW. The map markers show their own traps; the log is the
 * text TOR already put in front of them. Both are presentation, which is why they are plain host
 * options (default ON) rather than anything gated on a handshake: nothing here is sent, and a
 * client without the mod is not disadvantaged by another client's map being easier to read.
 *
 * REACHING TOR'S TRAP TYPE. `Trap` is internal to TheOtherRoles (Objects/Trap.cs:9), so it cannot
 * be named from here. It is resolved once through AccessTools.TypeByName and read by reflection -
 * the same route the other Tor* files take for TORMapOptions and friends. Everything on it that
 * this file needs is a plain managed field on a plain managed class (TOR's own types are not
 * Il2Cpp), so the reflection is ordinary. If any handle fails to resolve, both features log once
 * and switch themselves off rather than throwing per frame.
 */

using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq.Expressions;
using System.Reflection;
using System.Text;
using HarmonyLib;
using TheOtherRoles;
using TheOtherRoles.Objects;
using TheOtherRoles.Utilities;
using UnityEngine;
using Types = TheOtherRoles.CustomOption.CustomOptionType;

namespace UsefulTORStuff {
    public static class TrapperExtras {

        public static CustomOption OptionMapMarkers;      // 1346
        public static CustomOption OptionPersistentLog;   // 1347

        public static void CreateOptions() {
            try {
                OptionMapMarkers = new CustomOption(
                    1346, Types.Crewmate, "Show Own Traps On The Map",
                    new string[] { "Off", "On" }, "On",
                    CustomOptionHolder.trapperSpawnRate, false);
                UTSLocalization.BindOptionTitle(OptionMapMarkers, "uts.trapperextras.option_map");

                OptionPersistentLog = new CustomOption(
                    1347, Types.Crewmate, "Trap Log Stays Available",
                    new string[] { "Off", "On" }, "On",
                    CustomOptionHolder.trapperSpawnRate, false);
                UTSLocalization.BindOptionTitle(OptionPersistentLog, "uts.trapperextras.option_log");

                // Put both at the end of the Trapper's own block, after "Trapper Info Type", so the
                // role reads as one unit in the settings list instead of gaining two strays.
                var opts = CustomOption.options;
                opts.Remove(OptionMapMarkers);
                opts.Remove(OptionPersistentLog);
                int idx = opts.IndexOf(CustomOptionHolder.trapperInfoType);
                if (idx < 0) idx = opts.IndexOf(CustomOptionHolder.trapperSpawnRate);
                if (idx < 0) idx = opts.Count - 1;
                opts.Insert(idx + 1, OptionMapMarkers);
                opts.Insert(idx + 2, OptionPersistentLog);

                UsefulTORStuffPlugin.Logger?.LogInfo("[TrapperExtras] Options created under Trapper.");
            } catch (Exception e) {
                UsefulTORStuffPlugin.Logger?.LogError($"[TrapperExtras] CreateOptions failed: {e}");
            }
        }

        private static bool MapMarkersOn => OptionMapMarkers != null && UTSGate.Bool(OptionMapMarkers);
        private static bool LogOn => OptionPersistentLog != null && UTSGate.Bool(OptionPersistentLog);

        private static bool LocalIsLivingTrapper() {
            var me = PlayerControl.LocalPlayer;
            return Trapper.trapper != null && me != null && Trapper.trapper.PlayerId == me.PlayerId
                   && me.Data != null && !me.Data.IsDead;
        }

        // ================================================================================
        // TOR's internal Trap type, resolved once
        // ================================================================================
        private static bool resolved, resolveFailed;
        private static FieldInfo fTraps, fInstanceId, fRevealed, fTrappedPlayer, fTrapObject;
        private static MethodBase mGetTrapSprite;
        private static Sprite trapSprite;
        private static bool trapSpriteTried;

        // PERF (2026-09-01 audit): MapMarkerPatch reads instanceId/trap off every live Trap on every
        // physics tick while the map is open. FieldInfo.GetValue boxes the value (instanceId is an
        // int) on every single call; these compiled delegates, built once here in Resolve() from the
        // very same FieldInfo, read the field directly with no boxing - same trick SnitchLogic uses
        // for TOR's static fields, just over an INSTANCE field this time (the Trap the value comes
        // out of is a Harmony/Expression-visible `object`, so this works without ever naming TOR's
        // internal Trap type at compile time).
        private static Func<IEnumerable> getTrapsDelegate;
        private static Func<object, int> getInstanceIdDelegate;
        private static Func<object, GameObject> getTrapObjectDelegate;

        private static bool Resolve() {
            if (resolved) return !resolveFailed;
            resolved = true;
            try {
                var t = AccessTools.TypeByName("TheOtherRoles.Objects.Trap");
                if (t == null) throw new Exception("type TheOtherRoles.Objects.Trap not found");
                fTraps = AccessTools.Field(t, "traps");
                fInstanceId = AccessTools.Field(t, "instanceId");
                fRevealed = AccessTools.Field(t, "revealed");
                fTrappedPlayer = AccessTools.Field(t, "trappedPlayer");
                fTrapObject = AccessTools.Field(t, "trap");
                // The map marker uses TOR's OWN trap artwork rather than a recoloured here-point,
                // so the icon on the map is the thing the trapper sees on the floor. It is a
                // public static on the same internal class, so it comes along for the ride.
                mGetTrapSprite = AccessTools.Method(t, "getTrapSprite");
                if (fTraps == null || fInstanceId == null || fRevealed == null
                    || fTrappedPlayer == null || fTrapObject == null)
                    throw new Exception("one or more Trap fields not found");
                if (mGetTrapSprite == null)
                    UsefulTORStuffPlugin.Logger?.LogWarning(
                        "[TrapperExtras] Trap.getTrapSprite not found - map markers fall back to the "
                        + "here-point dot; the numbers still work.");

                // Perf path: compiled Expression-tree getters (no boxing, see the comment above the
                // fields). If the compile itself fails for any reason - the FieldInfos above already
                // resolved fine, so the type/fields genuinely exist - fall back to plain
                // FieldInfo.GetValue getters instead of disabling the whole feature. Slower (boxes
                // every call), but the map markers and trap log stay on for the session either way.
                try {
                    getTrapsDelegate = MakeStaticGetter<IEnumerable>(fTraps);
                    getInstanceIdDelegate = MakeInstanceGetter<int>(fInstanceId);
                    getTrapObjectDelegate = MakeInstanceGetter<GameObject>(fTrapObject);
                } catch (Exception e) {
                    UsefulTORStuffPlugin.Logger?.LogWarning(
                        "[TrapperExtras] compiled field getters failed, falling back to plain "
                        + $"reflection (slower, feature stays on): {e.Message}");
                    var traps = fTraps; var instanceId = fInstanceId; var trapObj = fTrapObject;
                    getTrapsDelegate = () => (IEnumerable)traps.GetValue(null);
                    getInstanceIdDelegate = obj => (int)instanceId.GetValue(obj);
                    getTrapObjectDelegate = obj => (GameObject)trapObj.GetValue(obj);
                }
            } catch (Exception e) {
                resolveFailed = true;
                UsefulTORStuffPlugin.Logger?.LogWarning(
                    "[TrapperExtras] TOR's Trap type could not be read, so the map markers and the "
                    + $"trap log stay off for this session: {e.Message}");
            }
            return !resolveFailed;
        }

        private static Func<T> MakeStaticGetter<T>(FieldInfo field) {
            Expression body = Expression.Field(null, field);
            if (field.FieldType != typeof(T)) body = Expression.Convert(body, typeof(T));
            return Expression.Lambda<Func<T>>(body).Compile();
        }

        private static Func<object, T> MakeInstanceGetter<T>(FieldInfo field) {
            var objParam = Expression.Parameter(typeof(object), "obj");
            Expression typed = Expression.Convert(objParam, field.DeclaringType);
            Expression body = Expression.Field(typed, field);
            if (field.FieldType != typeof(T)) body = Expression.Convert(body, typeof(T));
            return Expression.Lambda<Func<object, T>>(body, objParam).Compile();
        }

        /// The live trap list, as plain objects. Empty when anything failed to resolve, so every
        /// caller can foreach over it without a null check.
        private static IEnumerable Traps() {
            if (!Resolve()) return Array.Empty<object>();
            return getTrapsDelegate() ?? (IEnumerable)Array.Empty<object>();
        }

        // ================================================================================
        // 1) The trapper's own traps, numbered, on the map
        // ================================================================================
        private static readonly Dictionary<int, GameObject> markers = new();
        // Reused across ticks (Clear()ed, not reallocated) - this runs every physics tick the map is
        // open, and a fresh HashSet/List per tick was pure churn for what is at most a handful of
        // live traps.
        private static readonly HashSet<int> markerSeenScratch = new();
        private static readonly List<int> markerGoneScratch = new();

        [HarmonyPatch(typeof(MapBehaviour), nameof(MapBehaviour.FixedUpdate))]
        internal static class MapMarkerPatch {
            public static void Postfix(MapBehaviour __instance) {
                try {
                    if (!MapMarkersOn || !LocalIsLivingTrapper()) { ClearMarkers(); return; }
                    if (__instance == null || __instance.HerePoint == null) { ClearMarkers(); return; }
                    var ship = MapUtilities.CachedShipStatus;
                    if (ship == null) { ClearMarkers(); return; }

                    var seen = markerSeenScratch;
                    seen.Clear();
                    foreach (var t in Traps()) {
                        if (t == null) continue;
                        int id = getInstanceIdDelegate(t);
                        var go = getTrapObjectDelegate(t);
                        if (go == null) continue;
                        seen.Add(id);

                        // The map is the world divided by MapScale, mirrored on x for the maps whose
                        // ship transform is flipped - exactly what TOR does for its own here-points
                        // (MapBehaviourPatch.cs:45-48). Kept identical so a trap marker lands where a
                        // player marker for the same spot would.
                        Vector3 v = go.transform.position;
                        v /= ship.MapScale;
                        v.x *= Mathf.Sign(ship.transform.localScale.x);
                        v.z = -2.2f;                       // a hair in front of TOR's here-points

                        if (!markers.TryGetValue(id, out var marker) || marker == null) {
                            marker = MakeMarker(__instance, id);
                            markers[id] = marker;
                        }
                        marker.transform.localPosition = v;
                    }

                    // Traps that no longer exist (revealed ones are destroyed after a meeting).
                    var gone = markerGoneScratch;
                    gone.Clear();
                    foreach (var kv in markers) if (!seen.Contains(kv.Key)) gone.Add(kv.Key);
                    foreach (var id in gone) {
                        if (markers[id] != null) UnityEngine.Object.Destroy(markers[id]);
                        markers.Remove(id);
                    }
                } catch (Exception e) {
                    UsefulTORStuffPlugin.Logger?.LogError($"[TrapperExtras] map markers failed: {e}");
                    ClearMarkers();
                }
            }
        }

        /// TOR's own trap artwork, fetched once. Null when the handle did not resolve, which is
        /// the signal to fall back to a here-point dot rather than draw nothing.
        private static Sprite TrapSprite() {
            if (trapSpriteTried) return trapSprite;
            trapSpriteTried = true;
            try { trapSprite = mGetTrapSprite?.Invoke(null, null) as Sprite; }
            catch (Exception e) {
                UsefulTORStuffPlugin.Logger?.LogWarning($"[TrapperExtras] trap sprite failed: {e.Message}");
            }
            return trapSprite;
        }

        /*
         * One marker: TOR's trap icon with its number on it.
         *
         * The icon is the same sprite the trap is drawn with on the floor (Trapper_Trap_Ingame,
         * loaded at 300 px per unit), so the map says the same thing the world does. It is built as
         * a fresh SpriteRenderer under the here-point's PARENT - the same transform TOR parents its
         * own map markers to, which is what puts it in map space - rather than by cloning the
         * here-point, because a clone would carry the dot's own sprite, sizing and material along
         * with it.
         *
         * Sorting is copied off the here-point instead of guessed: the map draws into its own
         * sorting layer, and a renderer that names a different one is either in front of everything
         * or invisible.
         */
        private static GameObject MakeMarker(MapBehaviour map, int id) {
            var reference = map.HerePoint;
            var sprite = TrapSprite();

            GameObject go;
            SpriteRenderer sr;
            if (sprite != null) {
                go = new GameObject("UTS_TrapMarker");
                go.transform.SetParent(reference.transform.parent, false);
                sr = go.AddComponent<SpriteRenderer>();
                sr.sprite = sprite;
                /*
                 * SIZED AGAINST THE HERE-POINT, NOT AGAINST A GUESS.
                 *
                 * The trap sprite is loaded at 300 px per unit, which is WORLD scale - but map
                 * markers live in a space where positions are divided by ShipStatus.MapScale, so an
                 * unscaled sprite would tower over the map, and by a different amount on every map.
                 * The here-point is the player dot the game itself draws there, so it is the ruler:
                 * match its rendered height and the icon sits right everywhere, whatever Among Us
                 * does with the map next.
                 */
                float target = reference.bounds.size.y;
                float own = sprite.bounds.size.y;
                float k = (own > 0.0001f && target > 0.0001f) ? target / own : 0.55f;
                go.transform.localScale = Vector3.one * k * 1.6f;   // a trap reads better than a dot
            } else {
                var clone = UnityEngine.Object.Instantiate(reference, reference.transform.parent, true);
                clone.enabled = true;
                clone.color = Trapper.color;
                clone.gameObject.SetActive(true);
                go = clone.gameObject;
                sr = clone;
            }
            sr.sortingLayerName = reference.sortingLayerName;
            sr.sortingOrder = reference.sortingOrder + 1;
            go.SetActive(true);

            // The number, so the marker answers the question the log asks ("where is trap 3?").
            var label = new GameObject("TrapNumber");
            label.transform.SetParent(go.transform, false);
            label.transform.localPosition = new Vector3(0f, 0f, -0.1f);
            // Undo the icon's own scale, so the number is the same size on every marker instead of
            // inheriting whatever the sprite needed.
            float inv = go.transform.localScale.x > 0.001f ? 1f / go.transform.localScale.x : 1f;
            label.transform.localScale = new Vector3(inv, inv, 1f);
            var tmp = label.AddComponent<TMPro.TextMeshPro>();
            tmp.text = id.ToString();
            tmp.fontSize = 2.2f;
            tmp.color = Color.white;
            tmp.alignment = TMPro.TextAlignmentOptions.Center;
            tmp.fontStyle = TMPro.FontStyles.Bold;
            tmp.outlineWidth = 0.28f;                  // the icon is busy; a bare glyph vanishes on it
            tmp.outlineColor = new Color32(0, 0, 0, 255);
            var mr = label.GetComponent<MeshRenderer>();
            if (mr != null) {
                mr.sortingLayerName = sr.sortingLayerName;
                mr.sortingOrder = sr.sortingOrder + 1;
            }
            return go;
        }

        private static void ClearMarkers() {
            if (markers.Count == 0) return;
            foreach (var kv in markers) if (kv.Value != null) UnityEngine.Object.Destroy(kv.Value);
            markers.Clear();
        }

        // ================================================================================
        // 2) The trap log, kept
        // ================================================================================
        /// One block per revealed trap, in the order the meetings produced them. Plain strings: the
        /// roles behind them are resolved at capture time because the traps, and after a round the
        /// role statics too, are gone by the time anyone reads this.
        private static readonly List<string> log = new();

        public static void ClearLog() { log.Clear(); posted = 0; CloseView(); }

        /*
         * CAPTURE POINT: the prefix of Trap.clearRevealedTraps.
         *
         * TOR writes the log and destroys the traps inside ONE method - its StartMeeting prefix -
         * so no prefix or postfix of that method can stand between the two. clearRevealedTraps is
         * the seam: it is called at MeetingPatch.cs:763, immediately after the log block, and at
         * that moment the traps still exist AND their trappedPlayer lists have already been shuffled
         * by TOR (MeetingPatch.cs:715). Reading them here therefore reproduces exactly what the
         * trapper just saw in the chat, shuffle included - rather than the true trigger order, which
         * TOR shuffles precisely to keep from them.
         */
        [HarmonyPatch]
        internal static class CaptureLogPatch {
            public static MethodBase TargetMethod() {
                var t = AccessTools.TypeByName("TheOtherRoles.Objects.Trap");
                return t == null ? null : AccessTools.Method(t, "clearRevealedTraps");
            }

            public static bool Prepare() =>
                AccessTools.TypeByName("TheOtherRoles.Objects.Trap") != null;

            public static void Prefix() {
                try {
                    if (!LogOn) return;
                    // Only the trapper is shown this, and only while alive - the same condition TOR
                    // puts on writing it into the chat (MeetingPatch.cs:709), ghost info included.
                    if (!(LocalIsLivingTrapper() || Helpers.shouldShowGhostInfo())) return;

                    foreach (var t in Traps()) {
                        if (t == null) continue;
                        if (!(bool)fRevealed.GetValue(t)) continue;
                        int id = getInstanceIdDelegate(t);
                        var ids = fTrappedPlayer.GetValue(t) as List<byte>;
                        if (ids == null) continue;

                        var sb = new StringBuilder();
                        sb.Append(UTSLocalization.Tr("uts.trapperextras.log_trap")).Append(' ').Append(id).Append(':');
                        foreach (byte pid in ids) {
                            var p = Helpers.playerById(pid);
                            if (p == null) continue;
                            sb.Append('\n').Append("  ").Append(Describe(p));
                        }
                        log.Add(sb.ToString());
                    }
                } catch (Exception e) {
                    UsefulTORStuffPlugin.Logger?.LogError($"[TrapperExtras] log capture failed: {e}");
                }
            }
        }

        /// The same three presentations TOR offers, read from the same option, so the kept log says
        /// exactly what the chat said (Trapper.infoType: 0 role, 1 good/evil, 2 name).
        private static string Describe(PlayerControl p) {
            try {
                if (Trapper.infoType == 0) return RoleInfo.GetRolesString(p, false, false, true);
                if (Trapper.infoType == 1)
                    return UTSLocalization.Tr(Helpers.isNeutral(p) || p.Data.Role.IsImpostor
                        ? "uts.trapperextras.evil" : "uts.trapperextras.good");
                return p.Data.PlayerName;
            } catch { return "?"; }
        }

        // ================================================================================
        // The trapper's own section in the chat, and the button that opens it
        // ================================================================================
        /*
         * THE LOG LIVES IN THE CHAT, which is where TOR already puts it - it is simply unreachable
         * afterwards, because the chat is hidden during a round. So this does not build a window of
         * its own: it opens the real chat and writes whatever the trapper has not been shown yet.
         *
         * TWO THINGS HAVE TO GIVE WAY FOR THAT, and both are narrow and explicit:
         *
         *  1. LobbyLeakGuard hides the chat for a living player in a round - that is a client-crash
         *     fix, not a nicety. It already carries a list of exemptions (meeting, exile screen,
         *     dead players, freeplay, lovers with chat enabled); the trapper reading their log is
         *     one more line in that list, and only while the view is actually open.
         *  2. Opening the chat also offers the text box, and the whole point of the clamp is that a
         *     living player does not talk mid-round. So SENDING is refused while the view is open.
         *     The trapper gets to READ, which is all this feature is for.
         */
        public static bool LogViewOpen { get; private set; }

        /// How many captured entries have been written into the chat. Re-opening therefore adds
        /// nothing, and a new meeting's entries appear the next time it is opened.
        private static int posted;

        private static void CloseView() {
            if (!LogViewOpen) return;
            LogViewOpen = false;
            try { HudManager.Instance?.Chat?.ForceClosed(); } catch { }
        }

        private static void ToggleView() {
            if (LogViewOpen) { CloseView(); return; }
            try {
                var hud = HudManager.Instance;
                if (hud == null || hud.Chat == null) return;

                LogViewOpen = true;
                // ChatController has no Open(): SetVisible puts the button back and Toggle is
                // what actually opens the window. Guarded, because Toggle would CLOSE an open one.
                hud.Chat.SetVisible(true);
                if (!hud.Chat.IsOpenOrOpening) hud.Chat.Toggle();

                var me = PlayerControl.LocalPlayer;
                if (me == null) return;

                if (posted == 0)
                    hud.Chat.AddChat(me, Helpers.cs(Trapper.color, UTSLocalization.Tr("uts.trapperextras.log_title")));
                if (log.Count == 0 && posted == 0) {
                    hud.Chat.AddChat(me, UTSLocalization.Tr("uts.trapperextras.log_empty"));
                    return;
                }
                for (; posted < log.Count; posted++) hud.Chat.AddChat(me, log[posted]);
            } catch (Exception e) {
                UsefulTORStuffPlugin.Logger?.LogError($"[TrapperExtras] trap log view failed: {e}");
                LogViewOpen = false;
            }
        }

        /// No talking through the door this opens - see point 2 in the note above.
        [HarmonyPatch(typeof(ChatController), nameof(ChatController.SendChat))]
        internal static class NoSendWhileViewingPatch {
            public static bool Prefix() => !LogViewOpen;
        }

        /// The view closes with the chat, however the chat was closed (Escape, the X, another
        /// patch) - otherwise LogViewOpen would stay true and keep the clamp exempted for the rest
        /// of the round.
        [HarmonyPatch(typeof(HudManager), nameof(HudManager.Update))]
        internal static class ViewWatchPatch {
            public static void Postfix(HudManager __instance) {
                try {
                    if (!LogViewOpen) return;
                    if (__instance == null || __instance.Chat == null) { LogViewOpen = false; return; }
                    if (!__instance.Chat.IsOpenOrOpening) LogViewOpen = false;
                } catch { LogViewOpen = false; }
            }
        }

        private static CustomButton logButton;

        [HarmonyPatch(typeof(HudManager), nameof(HudManager.Start))]
        internal static class ButtonPatch {
            public static void Postfix(HudManager __instance) {
                try {
                    logButton = new CustomButton(
                        () => { logButton.Timer = 0f; ToggleView(); },
                        // The trapper only, alive, option on. A dead trapper keeps nothing on screen:
                        // TOR stops writing them the log at that point too.
                        () => LogOn && LocalIsLivingTrapper(),
                        () => true,                          // reading is free and always possible
                        () => { CloseView(); },
                        Trapper.getButtonSprite(),
                        CustomButton.ButtonPositions.upperRowRight,
                        __instance,
                        KeyCode.L
                    );
                    logButton.Timer = 0f;
                    logButton.MaxTimer = 0f;
                    logButton.buttonText = UTSLocalization.Tr("uts.trapperextras.button");
                } catch (Exception e) {
                    UsefulTORStuffPlugin.Logger?.LogError($"[TrapperExtras] log button failed: {e}");
                }
            }
        }
    }
}
