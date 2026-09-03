// Useful TOR Stuff - Copyright (C) 2026 DaUnknown-0
// Licensed under GPL-3.0-or-later. See LICENSE for details.
// Based on The Other Roles (https://github.com/TheOtherRolesAU/TheOtherRoles), GPL-3.0.

/*
 * TricksterAvatarSabotage - new Trickster option "Avatar Mixup Sabotage" (works on EVERY map).
 *
 * The vanilla "Mushroom Mixup" only exists on The Fungle, so we don't use it. Instead we reproduce
 * the effect ourselves so it works on any map: a button makes every alive player look like another
 * (a random derangement of their outfits) for a configurable duration, then everyone reverts.
 *
 * Sync: the triggering Trickster computes the mapping (playerId -> source playerId whose look to wear)
 * and broadcasts it with a custom RPC (246); every client applies the whole mapping ONCE when it
 * arrives (exactly like TOR's own Camouflager RPC handler sets its disguise once, RPC.cs's
 * camouflagerCamouflage - not per tick), with a cheap 0.5s-throttled re-apply as a safety net against
 * something else overwriting a borrowed look, and reverts via setDefaultLook when the timer ends or
 * a meeting starts.
 *
 * Constraints (couldUse): not during the Camouflager flash, not while a mixup is already running, and
 * a shared cooldown with Lights-Out (using this also puts Lights-Out on cooldown and vice-versa via
 * the LightsReady gate). The Lights-Out button is private static in TOR's HudManagerStartPatch
 * (reflection).
 */

using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using HarmonyLib;
using Hazel;
using UnityEngine;
using TheOtherRoles;
using TheOtherRoles.Objects;
using static TheOtherRoles.TheOtherRoles;
using Types = TheOtherRoles.CustomOption.CustomOptionType;

namespace UsefulTORStuff {
    public static class TricksterAvatarSabotage {
        public const byte MixupRpcId = 246;

        public static CustomOption Option;          // Off/On toggle
        public static CustomOption CooldownOption;   // own cooldown (seconds)
        public static CustomOption DurationOption;    // effect duration (seconds)

        private static CustomButton sabotageButton;
        private static FieldInfo lightsOutButtonField;
        private static FieldInfo camouflagerButtonField;
        // The camouflagerButton instance whose CouldUse we've already wrapped (TOR rebuilds the button
        // every HudManager.Start, so we re-wrap each fresh instance exactly once).
        private static CustomButton _wrappedCamoButton;

        // Active mixup state (synced via RPC; identical on every client).
        private static readonly Dictionary<byte, byte> mixupMap = new Dictionary<byte, byte>();
        private static float mixupTimer;
        // Throttle for the periodic look re-apply safety net (looks are set once in ApplyMixup, this
        // just guards against something else overwriting them mid-effect).
        private static float nextReapply;

        public static void CreateOptions() {
            try {
                Option = CustomOption.Create(
                    1300, Types.Impostor, "Trickster Avatar Mixup Sabotage",
                    false, CustomOptionHolder.tricksterSpawnRate);
                UTSLocalization.BindOptionTitle(Option, "uts.tricksteravatarsabotage.option_name");
                CooldownOption = CustomOption.Create(
                    1301, Types.Impostor, "Avatar Mixup Sabotage Cooldown", 30f, 10f, 60f, 5f, CustomOptionHolder.tricksterSpawnRate);
                UTSLocalization.BindOptionTitle(CooldownOption, "uts.tricksteravatarsabotage.cooldown_option");
                DurationOption = CustomOption.Create(
                    1302, Types.Impostor, "Avatar Mixup Sabotage Duration", 10f, 3f, 30f, 1f, CustomOptionHolder.tricksterSpawnRate);
                UTSLocalization.BindOptionTitle(DurationOption, "uts.tricksteravatarsabotage.duration_option");

                var opts = CustomOption.options;
                foreach (var o in new[] { Option, CooldownOption, DurationOption }) opts.Remove(o);
                int idx = opts.IndexOf(CustomOptionHolder.tricksterLightsOutDuration);
                if (idx < 0) idx = opts.Count - 1;
                opts.Insert(idx + 1, Option);
                opts.Insert(idx + 2, CooldownOption);
                opts.Insert(idx + 3, DurationOption);

                UsefulTORStuffPlugin.Logger?.LogInfo("[TricksterAvatarSabotage] Options created under Trickster.");
            } catch (Exception e) {
                UsefulTORStuffPlugin.Logger?.LogError($"[TricksterAvatarSabotage] CreateOptions failed: {e}");
            }
        }

        public static void TryPatch(Harmony harmony) {
            try {
                var hmsp = typeof(CustomOption).Assembly.GetType("TheOtherRoles.HudManagerStartPatch");
                lightsOutButtonField = hmsp?.GetField("lightsOutButton", BindingFlags.NonPublic | BindingFlags.Static);
                if (lightsOutButtonField == null)
                    UsefulTORStuffPlugin.Logger?.LogWarning("[TricksterAvatarSabotage] lightsOutButton field not found, shared cooldown disabled.");

                camouflagerButtonField = hmsp?.GetField("camouflagerButton", BindingFlags.NonPublic | BindingFlags.Static);
                if (camouflagerButtonField == null)
                    UsefulTORStuffPlugin.Logger?.LogWarning("[TricksterAvatarSabotage] camouflagerButton field not found, Camo block during mixup disabled.");
            } catch (Exception e) {
                UsefulTORStuffPlugin.Logger?.LogError($"[TricksterAvatarSabotage] TryPatch failed: {e}");
            }

            // Event-driven re-apply: Camouflager.resetCamouflage/Morphling.resetMorph/
            // SurveillanceMinigamePatch.resetNightVision each call setDefaultLook/setLook on
            // players directly, independent of our own throttled safety-net re-apply, and can run
            // in the same tick right after we last wrote a borrowed look - overwriting it until
            // the next throttled pass catches up (previously up to 0.5s of a wrong/default look
            // showing). Hooking these three re-apply the mixup mapping the instant one of them
            // fires, on top of (not instead of) the throttled safety net.
            // Camouflager/Morphling are public classes with public static methods, so they patch
            // directly by type; SurveillanceMinigamePatch is an internal class in TOR's assembly
            // (its resetNightVision method is public but unreachable via typeof() from here), so
            // it needs the reflection fallback - if that ever fails to resolve (TOR renames/moves
            // it), only this one re-apply hook is skipped, the other two and the throttled net
            // still work.
            try {
                harmony.Patch(
                    AccessTools.Method(typeof(Camouflager), nameof(Camouflager.resetCamouflage)),
                    postfix: new HarmonyMethod(typeof(TricksterAvatarSabotage), nameof(ReapplyOnResetPostfix)));
                harmony.Patch(
                    AccessTools.Method(typeof(Morphling), nameof(Morphling.resetMorph)),
                    postfix: new HarmonyMethod(typeof(TricksterAvatarSabotage), nameof(ReapplyOnResetPostfix)));
            } catch (Exception e) {
                UsefulTORStuffPlugin.Logger?.LogError($"[TricksterAvatarSabotage] Camouflager/Morphling reset hooks failed: {e}");
            }
            try {
                var smp = typeof(CustomOption).Assembly.GetType("TheOtherRoles.Patches.SurveillanceMinigamePatch");
                var resetNightVision = smp?.GetMethod("resetNightVision", BindingFlags.Public | BindingFlags.Static);
                if (resetNightVision == null) {
                    UsefulTORStuffPlugin.Logger?.LogWarning("[TricksterAvatarSabotage] resetNightVision not found, night-vision reset re-apply hook disabled.");
                } else {
                    harmony.Patch(resetNightVision,
                        postfix: new HarmonyMethod(typeof(TricksterAvatarSabotage), nameof(ReapplyOnResetPostfix)));
                }
            } catch (Exception e) {
                UsefulTORStuffPlugin.Logger?.LogError($"[TricksterAvatarSabotage] resetNightVision hook failed: {e}");
            }
        }

        // Shared postfix target for all three reset hooks above.
        public static void ReapplyOnResetPostfix() {
            try {
                // Only while a round is actually running: at round start TOR resets Camouflager/Morphling
                // before the resetVariables postfix clears the mixup, so a stale map must not be re-applied.
                if (mixupTimer > 0f && mixupMap.Count > 0 && AmongUsClient.Instance != null
                    && AmongUsClient.Instance.GameState == InnerNet.InnerNetClient.GameStates.Started)
                    ReapplyLooks();
            } catch (Exception e) {
                UsefulTORStuffPlugin.Logger?.LogError($"[TricksterAvatarSabotage] ReapplyOnResetPostfix failed: {e}");
            }
        }

        private static CustomButton LightsOutButton() {
            try { return lightsOutButtonField?.GetValue(null) as CustomButton; } catch { return null; }
        }
        private static CustomButton CamouflagerButton() {
            try { return camouflagerButtonField?.GetValue(null) as CustomButton; } catch { return null; }
        }

        // Make the block symmetric: the mixup's couldUse already disables it during an active
        // camouflage; here we wrap the Camouflager's button so it can't camouflage while a mixup is
        // running. TOR recreates camouflagerButton on every HudManager.Start, so we re-wrap the fresh
        // instance once. No-op when the feature is unused (mixupTimer stays 0 → original couldUse).
        private static void WrapCamouflagerBlock() {
            try {
                var camoBtn = CamouflagerButton();
                if (camoBtn == null || camoBtn == _wrappedCamoButton) return;
                var orig = camoBtn.CouldUse;
                camoBtn.CouldUse = (Func<bool>)(() => (orig == null || orig()) && mixupTimer <= 0f);
                _wrappedCamoButton = camoBtn;
            } catch (Exception e) {
                UsefulTORStuffPlugin.Logger?.LogError($"[TricksterAvatarSabotage] Camo block wrap failed: {e}");
            }
        }
        private static bool LightsReady() {
            var lob = LightsOutButton();
            return lob == null || lob.Timer <= 0f;
        }

        // Build a random derangement of the alive players' looks and broadcast it.
        private static void TriggerMixup() {
            try {
                var alive = PlayerControl.AllPlayerControls.ToArray()
                    .Where(p => p != null && p.Data != null && !p.Data.IsDead).Select(p => p.PlayerId).ToList();
                if (alive.Count < 2) return;

                // Shuffle, then cyclic-shift by 1 → guaranteed no player keeps their own look.
                var rnd = new System.Random();
                var shuffled = alive.OrderBy(_ => rnd.Next()).ToList();
                var map = new Dictionary<byte, byte>();
                for (int i = 0; i < shuffled.Count; i++)
                    map[shuffled[i]] = shuffled[(i + 1) % shuffled.Count];

                float dur = DurationOption != null ? UTSGate.Num(DurationOption) : 10f;

                // NOT migrated to the consolidated channel (UTSRpc.CallId = 240) - deliberately.
                // Classified NOT IDEMPOTENT: ApplyMixup() does not only assign mixupMap/mixupTimer,
                // it also fires UTSAssets.PlayMixup(), a global audible cue. Dual-sending would make
                // every new-build client play that cue TWICE within the same frame (and restart the
                // duration timer), which is directly noticeable. De-duplicating is impossible here:
                // a receiver cannot tell a dual-sending new build from an old build that will never
                // follow up on channel 240. Stays on the standalone callId 246 until the legacy
                // paths are dropped wholesale in a breaking release.
                MessageWriter writer = AmongUsClient.Instance.StartRpcImmediately(
                    PlayerControl.LocalPlayer.NetId, MixupRpcId, SendOption.Reliable, -1);
                writer.Write((byte)map.Count);
                foreach (var kv in map) { writer.Write(kv.Key); writer.Write(kv.Value); }
                writer.Write(dur);
                AmongUsClient.Instance.FinishRpcImmediately(writer);

                ApplyMixup(map, dur);

                // Shared cooldown: also put Lights-Out on its full cooldown.
                var lob = LightsOutButton();
                if (lob != null) lob.Timer = lob.MaxTimer;
            } catch (Exception e) {
                UsefulTORStuffPlugin.Logger?.LogError($"[TricksterAvatarSabotage] TriggerMixup failed: {e}");
            }
        }

        private static void ApplyMixup(Dictionary<byte, byte> map, float duration) {
            mixupMap.Clear();
            foreach (var kv in map) mixupMap[kv.Key] = kv.Value;
            mixupTimer = duration;
            // Set every borrowed look once here (perf: was re-set from every player's FixedUpdate,
            // every physics tick, for the whole effect duration - see ReapplyLooks for the throttled
            // safety-net re-application instead).
            ReapplyLooks();
            nextReapply = Time.time + 0.1f;
            // Glitchy identity-shuffle cue: the mixup is a visible global effect, so everyone hears it.
            UTSAssets.PlayMixup();
        }

        // Walks the whole mapping and re-writes each affected player's look. Called once when the
        // mixup starts and then only periodically (nextReapply) from the local player's FixedUpdate
        // pass, not per player per tick.
        private static void ReapplyLooks() {
            foreach (var kv in mixupMap) {
                var target = Helpers.playerById(kv.Key);
                var src = Helpers.playerById(kv.Value);
                if (target == null || src == null || src.Data == null) continue;
                var o = src.Data.DefaultOutfit;
                target.setLook(src.Data.PlayerName, o.ColorId, o.HatId, o.VisorId, o.SkinId, o.PetId);
            }
        }

        private static void EndMixup() {
            mixupTimer = 0f;
            mixupMap.Clear();
            try {
                foreach (var p in PlayerControl.AllPlayerControls)
                    if (p != null) p.setDefaultLook();
            } catch { }
        }

        [HarmonyPatch(typeof(PlayerControl), nameof(PlayerControl.HandleRpc))]
        [HarmonyPriority(Priority.High)]
        static class HandleRpcPatch {
            public static bool Prefix(byte callId, MessageReader reader, PlayerControl __instance) {
                if (callId == MixupRpcId) {
                    try {
                        int count = reader.ReadByte();
                        var map = new Dictionary<byte, byte>();
                        for (int i = 0; i < count; i++) {
                            byte pid = reader.ReadByte();
                            byte src = reader.ReadByte();
                            map[pid] = src;
                        }
                        float dur = reader.ReadSingle();
                        // Owner-authored: only the Trickster (or the host) may trigger the mixup -
                        // without this guard any client could fire the global avatar shuffle at will
                        // with an arbitrary duration (AUDIT-2026-08-15). __instance is the sender.
                        if (UTSRpc.RequireOwnerOrHost(__instance, Trickster.trickster, "TricksterAvatarSabotage.Mixup"))
                            ApplyMixup(map, dur);
                    } catch { }
                    return false;
                }
                return true;
            }
        }

        // Timer/expiry only (Low priority -> after TOR's own look updates); looks themselves are set
        // once in ApplyMixup/ReapplyLooks, not per player per tick here.
        [HarmonyPatch(typeof(PlayerControl), nameof(PlayerControl.FixedUpdate))]
        [HarmonyPriority(Priority.Low)]
        static class ApplyPatch {
            public static void Postfix(PlayerControl __instance) {
                try {
                    if (mixupTimer <= 0f || __instance == null) return;

                    // Timer/expiry + throttled re-apply handled once per tick (on the local player's
                    // pass), not once per player per tick.
                    if (__instance == PlayerControl.LocalPlayer) {
                        if (MeetingHud.Instance != null) { EndMixup(); return; }
                        mixupTimer -= Time.fixedDeltaTime;
                        if (mixupTimer <= 0f) { EndMixup(); return; }
                        if (Time.time >= nextReapply) {
                            ReapplyLooks();
                            nextReapply = Time.time + 0.1f;
                        }
                    }
                } catch { }
            }
        }

        [HarmonyPatch(typeof(RPCProcedure), nameof(RPCProcedure.resetVariables))]
        static class ResetPatch {
            public static void Postfix() { mixupTimer = 0f; mixupMap.Clear(); nextReapply = 0f; }
        }

        // Same lobby-leak rule as the roles (AUDIT M-12): the byte-keyed state above is keyed by
        // PlayerId, which is handed out per LOBBY, and resetVariables only arrives from a host
        // that has this mod. Clearing on OnGameJoined too keeps a previous lobby's ids from
        // acting on whoever inherits them here.
        [HarmonyPatch(typeof(AmongUsClient), nameof(AmongUsClient.OnGameJoined))]
        static class GameJoinPatch {
            public static void Postfix() { mixupTimer = 0f; mixupMap.Clear(); }
        }

        [HarmonyPatch(typeof(HudManager), nameof(HudManager.Start))]
        [HarmonyPriority(Priority.Low)]
        static class HudStartPatch {
            public static void Postfix(HudManager __instance) {
                try {
                    sabotageButton = new CustomButton(
                        () => {
                            TriggerMixup();
                            sabotageButton.Timer = sabotageButton.MaxTimer;
                        },
                        () => Option != null && UTSGate.Bool(Option)
                              && Trickster.trickster != null && Trickster.trickster == PlayerControl.LocalPlayer
                              && PlayerControl.LocalPlayer.Data != null && !PlayerControl.LocalPlayer.Data.IsDead,
                        // Any map; not during camouflage, not while a mixup runs, shared CD with lights.
                        () => PlayerControl.LocalPlayer.CanMove
                              && Camouflager.camouflageTimer <= 0f && mixupTimer <= 0f && LightsReady(),
                        () => { },
                        // Own mixup icon; the Camouflager sprite only as fallback.
                        UTSAssets.MixupIcon ?? Camouflager.getButtonSprite(),
                        // Trickster is an Impostor: avoid right-side kill-button slots and the
                        // upperRowLeft used by place-box / lights-out. upperRowFarLeft is free.
                        CustomButton.ButtonPositions.upperRowFarLeft,
                        __instance,
                        KeyCode.C,
                        false,
                        ""
                    );
                    sabotageButton.MaxTimer = CooldownOption != null ? UTSGate.Num(CooldownOption) : 30f;
                    sabotageButton.Timer = sabotageButton.MaxTimer; // start on cooldown

                    // Block the Camouflager's button while a mixup is active (other direction of the
                    // existing camo↔mixup block). Runs here at Priority.Low, after TOR built its button.
                    WrapCamouflagerBlock();
                } catch (Exception e) {
                    UsefulTORStuffPlugin.Logger?.LogError($"[TricksterAvatarSabotage] Button creation failed: {e}");
                }
            }
        }
    }
}
