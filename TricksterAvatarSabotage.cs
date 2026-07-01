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
 * and broadcasts it with a custom RPC (246); every client then re-applies the look each frame (like
 * TOR's Camouflager does) via Helpers.setLook, and reverts via setDefaultLook when the timer ends or
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

        public static void CreateOptions() {
            try {
                Option = CustomOption.Create(
                    1300, Types.Impostor, "Trickster Avatar Mixup Sabotage",
                    false, CustomOptionHolder.tricksterSpawnRate);
                CooldownOption = CustomOption.Create(
                    1301, Types.Impostor, "Avatar Mixup Sabotage Cooldown", 30f, 10f, 60f, 5f, CustomOptionHolder.tricksterSpawnRate);
                DurationOption = CustomOption.Create(
                    1302, Types.Impostor, "Avatar Mixup Sabotage Duration", 10f, 3f, 30f, 1f, CustomOptionHolder.tricksterSpawnRate);

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

        public static void TryPatch() {
            try {
                var hmsp = typeof(CustomOption).Assembly.GetType("TheOtherRoles.HudManagerStartPatch");
                lightsOutButtonField = hmsp?.GetField("lightsOutButton", BindingFlags.NonPublic | BindingFlags.Static);
                if (lightsOutButtonField == null)
                    UsefulTORStuffPlugin.Logger?.LogWarning("[TricksterAvatarSabotage] lightsOutButton field not found — shared cooldown disabled.");

                camouflagerButtonField = hmsp?.GetField("camouflagerButton", BindingFlags.NonPublic | BindingFlags.Static);
                if (camouflagerButtonField == null)
                    UsefulTORStuffPlugin.Logger?.LogWarning("[TricksterAvatarSabotage] camouflagerButton field not found — Camo block during mixup disabled.");
            } catch (Exception e) {
                UsefulTORStuffPlugin.Logger?.LogError($"[TricksterAvatarSabotage] TryPatch failed: {e}");
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

                float dur = DurationOption != null ? DurationOption.getFloat() : 10f;

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
            // Glitchy identity-shuffle cue: the mixup is a visible global effect, so everyone hears it.
            UTSAssets.PlayMixup();
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
            public static bool Prefix(byte callId, MessageReader reader) {
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
                        ApplyMixup(map, dur);
                    } catch { }
                    return false;
                }
                return true;
            }
        }

        // Re-apply each affected player's borrowed look every frame (Low priority → after TOR's own
        // look updates). End on timer expiry or when a meeting starts.
        [HarmonyPatch(typeof(PlayerControl), nameof(PlayerControl.FixedUpdate))]
        [HarmonyPriority(Priority.Low)]
        static class ApplyPatch {
            public static void Postfix(PlayerControl __instance) {
                try {
                    if (mixupTimer <= 0f || __instance == null) return;

                    if (mixupMap.TryGetValue(__instance.PlayerId, out byte srcId)) {
                        var src = Helpers.playerById(srcId);
                        if (src != null && src.Data != null) {
                            var o = src.Data.DefaultOutfit;
                            __instance.setLook(src.Data.PlayerName, o.ColorId, o.HatId, o.VisorId, o.SkinId, o.PetId);
                        }
                    }

                    // Timer/expiry handled once per frame (on the local player's pass).
                    if (__instance == PlayerControl.LocalPlayer) {
                        if (MeetingHud.Instance != null) { EndMixup(); return; }
                        mixupTimer -= Time.fixedDeltaTime;
                        if (mixupTimer <= 0f) EndMixup();
                    }
                } catch { }
            }
        }

        [HarmonyPatch(typeof(RPCProcedure), nameof(RPCProcedure.resetVariables))]
        static class ResetPatch {
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
                        () => Option != null && Option.getBool()
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
                    sabotageButton.MaxTimer = CooldownOption != null ? CooldownOption.getFloat() : 30f;
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
