// Useful TOR Stuff - Copyright (C) 2026 DaUnknown-0
// Licensed under GPL-3.0-or-later. See LICENSE for details.
// Based on The Other Roles (https://github.com/TheOtherRolesAU/TheOtherRoles), GPL-3.0.

/*
 * DrunkRename - renames the Invert modifier to "Drunk" with a matching description.
 *
 * TOR stores modifier names in two places: the CustomOption.name field (shown in the
 * settings panel) and RoleInfo fields (shown in the intro and end-game screens). Both
 * are plain public strings that can be mutated at runtime without patching any methods.
 *
 * The option's onChange callback fires whenever the host toggles the option in settings,
 * so the rename stays live without a restart. The same ApplyRename() call at the end of
 * CreateOptions() handles configs where the option was saved as ON from a previous session.
 */

using System;
using TheOtherRoles;
using UnityEngine;
using static TheOtherRoles.TheOtherRoles;
using Types = TheOtherRoles.CustomOption.CustomOptionType;

namespace UsefulTORStuff {
    public static class DrunkRename {
        public static CustomOption Option;

        public static void CreateOptions() {
            try {
                // ID 1311: must be unique. 1310 collided with TiebreakerMultiple's "Tiebreaker
                // Quantity", which shares the config slot and scrambles both options' selections.
                Option = CustomOption.Create(
                    1311, Types.Modifier, "Rename to Drunk",
                    false, CustomOptionHolder.modifierInvert,
                    onChange: () => ApplyRename(Option.getBool()));
                UTSLocalization.BindOptionTitle(Option, "uts.drunkrename.option_name");

                var opts = CustomOption.options;
                opts.Remove(Option);
                int idx = opts.IndexOf(CustomOptionHolder.modifierInvertDuration);
                if (idx < 0) idx = opts.Count - 1;
                opts.Insert(idx + 1, Option);

                // Apply immediately if already saved as ON in the config
                if (Option.getBool()) ApplyRename(true);

                // LocalizationTOR re-applies TOR's own RoleInfo/CustomOption strings on every
                // language switch, which would overwrite the Drunk rename with "Invert" again.
                // Re-apply ours afterwards (LanguageApplied fires after LocalizationTOR.Apply()).
                UTSLocalization.LanguageApplied += () => ApplyRename(Option.getBool());

                UsefulTORStuffPlugin.Logger?.LogInfo("[DrunkRename] Option created.");
            } catch (Exception e) {
                UsefulTORStuffPlugin.Logger?.LogError($"[DrunkRename] CreateOptions failed: {e}");
            }
        }

        private static void ApplyRename(bool enable) {
            try {
                string n    = enable ? UTSLocalization.Tr("uts.drunkrename.name_drunk")
                                     : UTSLocalization.Tr("uts.drunkrename.name_invert_original");
                string desc = enable ? UTSLocalization.Tr("uts.drunkrename.intro_desc_drunk")
                                     : UTSLocalization.Tr("uts.drunkrename.intro_desc_invert");

                CustomOptionHolder.modifierInvert.name         = Helpers.cs(Color.yellow, n);
                CustomOptionHolder.modifierInvertQuantity.name =
                    UTSLocalization.Tr("uts.drunkrename.quantity_label", n);
                CustomOptionHolder.modifierInvertDuration.name = enable
                    ? UTSLocalization.Tr("uts.drunkrename.duration_label_drunk")
                    : UTSLocalization.Tr("uts.drunkrename.duration_label_invert");

                RoleInfo.invert.name             = n;
                RoleInfo.invert.introDescription = desc;
                RoleInfo.invert.shortDescription = desc;

                UsefulTORStuffPlugin.Logger?.LogInfo($"[DrunkRename] Rename {(enable ? "applied" : "reverted")}.");
            } catch (Exception e) {
                UsefulTORStuffPlugin.Logger?.LogError($"[DrunkRename] ApplyRename failed: {e}");
            }
        }
    }
}
