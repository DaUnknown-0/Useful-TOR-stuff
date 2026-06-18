// Useful TOR Stuff - Copyright (C) 2026 DaUnknown-0
// Licensed under GPL-3.0-or-later. See LICENSE for details.
// Based on The Other Roles (https://github.com/TheOtherRolesAU/TheOtherRoles), GPL-3.0.

/*
 * VultureGuessEat - new Vulture option "Vulture Counts Guessed Players As Eaten".
 *
 * The Vulture wins by eating enough corpses (Vulture.eatenBodies reaches
 * Vulture.vultureNumberToWin). Eating happens in RPCProcedure.cleanBody, which only fires for a
 * real DeadBody on the map. A guess death produces NO body: RPCProcedure.guesserShoot kills the
 * target via dyingTarget.Exiled() (like a vote-out), so guessed players vanish without a corpse
 * and the Vulture can never eat them.
 *
 * With this option ON we treat the VULTURE'S OWN guess kill as a meal: when the Vulture guesses a
 * player dead we increment Vulture.eatenBodies and re-check the win, mirroring cleanBody's win
 * snippet (RPC.cs). Like cleanBody (which requires the Vulture to be the one cleaning), only the
 * Vulture acting itself counts — a Vulture can only guess in the Guesser game mode, so in classic
 * modes (where only the Guesser roles guess) this stays a no-op.
 *
 * The win check runs host-authoritatively (CheckEndCriteriaPatch.CheckAndEndGameForVultureWin via
 * Vulture.triggerVultureWin), so — like SheriffParityWin — the feature ALWAYS applies from the
 * host regardless of who has the mod; it is NOT gated on "everyone has the mod".
 *
 * guesserShoot is internal/static in TOR's RPCProcedure, so it is patched via reflection + Harmony
 * (like the Bloody patches in UsefulTORStuffPlugin and SheriffParityWin's win-check patches).
 *
 * A child sub-option "Play Eat Sound On Counted Guess" (only selectable while the parent is ON)
 * plays TOR's existing vultureEat sound on a counted guess, mirroring the eat-button. The postfix
 * runs on every client, so the sound is heard by everyone in the meeting (intended).
 */

using System;
using System.Reflection;
using HarmonyLib;
using TheOtherRoles;
using TheOtherRoles.Utilities;
using static TheOtherRoles.TheOtherRoles;
using Types = TheOtherRoles.CustomOption.CustomOptionType;

namespace UsefulTORStuff {
    public static class VultureGuessEat {
        // Set in CreateOptions(); read by the guesserShoot postfix.
        public static CustomOption Option; // Off/On toggle
        public static CustomOption SoundOption; // Off/On toggle, child of Option — play the eat sound on a counted guess

        // Create the in-game option (under Vulture). Called from UsefulTORStuffPlugin.Load() after
        // TOR has already run CustomOptionHolder.Load() (guaranteed by the hard dependency).
        public static void CreateOptions() {
            try {
                Option = CustomOption.Create(
                    1202, Types.Neutral, "Vulture Counts Guessed Players As Eaten",
                    false, CustomOptionHolder.vultureSpawnRate);

                // Options render in CustomOption.options list order (filtered by type), so a
                // late-created option lands at the BOTTOM of the Neutral tab. Move ours directly
                // after the Vulture core options so it appears under "Vulture".
                var opts = CustomOption.options;
                opts.Remove(Option);
                int idx = opts.IndexOf(CustomOptionHolder.vultureShowArrows);
                if (idx < 0) idx = opts.Count - 1;
                opts.Insert(idx + 1, Option);

                // Child of Option: only selectable when "Counts Guessed Players As Eaten" is on
                // (auto "- " prefix + visibility filtering). Insert directly after its parent.
                SoundOption = CustomOption.Create(
                    1203, Types.Neutral, "Play Eat Sound On Counted Guess",
                    false, Option);

                opts.Remove(SoundOption);
                int sIdx = opts.IndexOf(Option);
                if (sIdx < 0) sIdx = opts.Count - 1;
                opts.Insert(sIdx + 1, SoundOption);

                UsefulTORStuffPlugin.Logger?.LogInfo("[VultureGuessEat] Options created under Vulture.");
            } catch (Exception e) {
                UsefulTORStuffPlugin.Logger?.LogError($"[VultureGuessEat] CreateOptions failed: {e}");
            }
        }

        // Attach the guesserShoot postfix via reflection (public static in TOR's RPCProcedure).
        public static void TryPatch(Harmony harmony) {
            try {
                var torAsm = typeof(CustomOption).Assembly;
                var type = torAsm.GetType("TheOtherRoles.RPCProcedure");
                if (type == null) {
                    UsefulTORStuffPlugin.Logger?.LogWarning("[VultureGuessEat] RPCProcedure not found — feature disabled.");
                    return;
                }

                var guesserShoot = type.GetMethod("guesserShoot",
                    BindingFlags.Public | BindingFlags.Static, null,
                    new[] { typeof(byte), typeof(byte), typeof(byte), typeof(byte) }, null);
                if (guesserShoot == null) {
                    UsefulTORStuffPlugin.Logger?.LogWarning("[VultureGuessEat] RPCProcedure.guesserShoot(byte,byte,byte,byte) not found — feature disabled.");
                    return;
                }

                harmony.Patch(guesserShoot, postfix: new HarmonyMethod(typeof(VultureGuessEat), nameof(GuesserShootPostfix)));
                UsefulTORStuffPlugin.Logger?.LogInfo("[VultureGuessEat] Patched guesserShoot — guessed players count toward the Vulture win.");
            } catch (Exception e) {
                UsefulTORStuffPlugin.Logger?.LogError($"[VultureGuessEat] TryPatch failed: {e}");
            }
        }

        // Postfix for RPCProcedure.guesserShoot(killerId, dyingTargetId, guessedTargetId, guessedRoleId).
        // Mirrors cleanBody's semantics: the meal only counts when the VULTURE itself is the actor,
        // i.e. the Vulture did the guessing (__0 == Vulture.PlayerId) — just like cleanBody requires
        // cleaningPlayerId == Vulture.vulture.PlayerId. (A Vulture can only guess in the Guesser game
        // mode; in classic modes guessing is reserved for the Guesser roles, so this stays a no-op
        // there.) Only the directly guessed/dying target (__1) is counted (+1 per guess) — a co-dying
        // lover partner or Lawyer suicide is NOT counted. guesserShoot runs exactly once per client
        // per guess (local call + RPC handler), so this never double-counts; the host runs it too,
        // so the host-side increment + host-authoritative VultureWin check fire.
        public static void GuesserShootPostfix(byte __0, byte __1) {
            try {
                if (Option == null || !Option.getBool()) return;        // option off

                var vulture = Vulture.vulture;
                if (vulture == null || vulture.Data == null || vulture.Data.IsDead) return; // Vulture must be alive
                if (__0 != vulture.PlayerId) return; // only the Vulture's own guess counts

                var dying = Helpers.playerById(__1);
                if (dying == null) return; // guesserShoot bailed early (RPC.cs:990) — nobody died

                // Mirror cleanBody's win snippet (RPC.cs:540-543); >= instead of == as a defensive
                // hardening (harmless since each guess adds exactly 1).
                Vulture.eatenBodies++;

                // Optional flavor: play the existing Vulture eat sound on a counted guess, mirroring
                // the eat-button (Buttons.cs:1465). The postfix runs on every client, so an unconditional
                // play means everyone in the meeting hears it (intended). Reuses the bundled "vultureEat"
                // clip — SoundEffectsManager is public static in TOR's assembly.
                if (SoundOption != null && SoundOption.getBool()) {
                    SoundEffectsManager.play("vultureEat");
                }

                if (Vulture.eatenBodies >= Vulture.vultureNumberToWin) {
                    Vulture.triggerVultureWin = true;
                }
            } catch (Exception e) {
                UsefulTORStuffPlugin.Logger?.LogError($"[VultureGuessEat] GuesserShootPostfix failed: {e}");
            }
        }
    }
}
