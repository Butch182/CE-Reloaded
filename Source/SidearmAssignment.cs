using System.Collections.Generic;
using System.Linq;
using Verse;
using CombatExtended;

namespace CEReloaded
{
    // A pawn's extra Weapon Loadout(s) carried alongside their main - "sidearms still exist, they just
    // live as their own separate Weapon Loadout(s) now instead of baked into the main one" (this mod moved
    // sidearms out of Equipment Packs and out of inline main-loadout content; this is the
    // other half of that promise - auto-assign has to actually attach a second loadout, not just leave the
    // player to do it by hand). Shares the SAME per-pawn extra-parents list Equipment Packs use
    // (MultiParentUtility, via the pawn's hidden "[Pawn-Loadout]" anchor - see AmmoAssignmentUtility) since that is
    // what actually feeds Loadout.Slots; identified within that shared list purely by NOT being an
    // Equipment Pack (the only other kind of thing ever placed there). A pawn can carry several sidearms
    // (a pistol AND a knife, say) - same "no artificial one-slot limit" reasoning Equipment Packs already
    // got, and the manual picker (Dialog_PickLoadouts) is a checklist for exactly that reason.
    public static class SidearmAssignmentUtility
    {
        public static List<Loadout> GetPawnSidearms(Pawn pawn)
        {
            Loadout leaf = AmmoAssignmentUtility.GetAmmoLeaf(pawn);
            if (leaf == null) return new List<Loadout>();
            return MultiParentUtility.GetExtraParents(leaf).Where(l => !EquipmentPackUtility.IsEquipmentPackLoadout(l)).ToList();
        }

        // The single mutation entrypoint for "this pawn now carries exactly these sidearms" - used by both
        // the manual picker and the auto-assign apply step. Preserves whatever pack(s) already share the
        // same extra-parents list - this only ever touches the sidearm portion of it. An empty result
        // detaches every sidearm (and cleans up the anchor entirely if nothing else - no packs, no extra
        // ammo - is left on it either).
        public static void SetPawnSidearms(Pawn pawn, Loadout template, List<Loadout> sidearms)
        {
            if (pawn == null || template == null) return;
            List<Loadout> newSidearms = (sidearms ?? new List<Loadout>()).Where(l => l != null).Distinct().ToList();
            Loadout existing = AmmoAssignmentUtility.GetAmmoLeaf(pawn);
            List<Loadout> currentSidearms = existing != null
                ? MultiParentUtility.GetExtraParents(existing).Where(l => !EquipmentPackUtility.IsEquipmentPackLoadout(l)).ToList()
                : new List<Loadout>();
            List<Loadout> packs = existing != null
                ? MultiParentUtility.GetExtraParents(existing).Where(EquipmentPackUtility.IsEquipmentPackLoadout).ToList()
                : new List<Loadout>();
            List<Loadout> combined = packs.Concat(newSidearms).ToList();
            if (combined.Count == 0)
            {
                // Every loadout we assign lives on the pawn's anchor permanently now (AssignTemplate) -
                // clearing the last sidearm just empties the extra-parents list, it never tears the anchor
                // down and reverts to a bare template assignment.
                if (existing == null) return;
                MultiParentUtility.SetExtraParents(existing, new List<Loadout>());
                AmmoAssignmentUtility.UnseedOwnAmmoForDetached(existing, currentSidearms.Concat(packs));
                CombatReadiness.Invalidate();
                return;
            }
            Loadout leaf = AmmoAssignmentUtility.EnsureAmmoLink(pawn, template);
            MultiParentUtility.SetExtraParents(leaf, combined);
            // First clean up the detached ones, then seed the new ones - see UnseedOwnAmmoForDetached.
            AmmoAssignmentUtility.UnseedOwnAmmoForDetached(leaf, currentSidearms.Except(newSidearms));
            // Ammo-starvation-on-attach fix (2026-08-01) - see AmmoAssignmentUtility.
            // SeedOwnAmmoForNewlyAttached's own comment for the full mechanism. Scoped to just the sidearm(s)
            // genuinely new in this call, not every sidearm the pawn now carries.
            AmmoAssignmentUtility.SeedOwnAmmoForNewlyAttached(leaf, newSidearms.Except(currentSidearms));
            // Readiness cache invalidation (2026-08-01: a pawn the widget flagged as
            // having a problem showed fully green/fine on their own Assign Loadouts card right after
            // attaching a sidearm - this is the SINGLE shared entrypoint for it, and it never told
            // CombatReadiness a real update just happened, same class of gap already fixed for Dialog_
            // AssignLoadouts' own commit paths).
            CombatReadiness.Invalidate();
        }

        // Gates the whole Sidearm column/cell. Always true since the lazy-template rework: the picker
        // offers every WEAPON (the template is created when it is ticked), no longer only existing
        // Weapon Loadout objects - a colony without a single template still has sidearms to choose from.
        // Before that, this exact return value would have silently hidden the whole sidearm column once
        // bulk generation was removed.
        public static bool HasSidearmOptions()
        {
            return true;
        }

        // Every real Weapon Loadout a pawn could carry as a sidearm - INCLUDING a grenade-only loadout
        // (e.g. "W| Frag grenade") and a shield-only loadout (e.g. "W| Riot shield"), both of which
        // Dialog_AssignLoadouts.AnalyzeLoadout deliberately excludes (never lets a thrown explosive or a
        // shield count as a loadout's "main weapon" - a satchel charge's raw blast score would otherwise
        // outscore an actual gun for the AUTO-SOLVER's own main-weapon pick, and a shield isn't a weapon at
        // all). That exclusion is correct for scoring, but both are perfectly normal MANUAL choices here, so
        // this is sourced straight from any real weapon-or-shield slot instead of going through AnalyzeLoadout.
        public static List<Loadout> CandidateLoadouts(Loadout excludeMain)
        {
            return LoadoutManager.Loadouts.Where(l => l != null && !l.defaultLoadout && !RoleUtility.IsRoleLoadout(l)
                    && !EquipmentPackUtility.IsEquipmentPackLoadout(l) && !AmmoAssignmentUtility.IsAmmoLink(l)
                    && l != excludeMain && l.Slots != null
                    && l.Slots.Any(s => s.thingDef != null && (s.thingDef.IsWeapon || LoadoutUtility.IsShieldApparel(s.thingDef))))
                .OrderBy(l => CategoryRank(l)).ThenBy(l => l.label).ToList();
        }

        // Which section a candidate loadout falls under in the manual picker (Dialog_PickPacks' optional
        // group-header support) - a real backup weapon, a grenade-type explosive, or a shield. Mirrors
        // exactly how AssignPlanner's three auto-assign pools (PickSidearm/PickGrenade/PickShield) already
        // classify the same three categories, just read back from a loadout's own slot instead of a ThingDef.
        // "Backup weapons" deliberately returns NULL: the columns already have headers, so an extra
        // "Backup weapons" separator row was redundant. In Dialog_PickPacks null means "no separator
        // row" - ordinary sidearms are the normal case and sit directly under the column header, which
        // is "Sidearms" anyway.
        // Explosives and Shields keep their row: it says something no column says.
        // The key CER_SidearmAssignment_CategoryBackupWeapons stays in all 13 languages,
        // so reverting this is one line rather than 13 files.
        public static string CategoryLabel(Loadout lo)
        {
            string kind = CategoryKind(lo);
            if (kind == "Explosives") return "CER_SidearmAssignment_CategoryExplosives".Translate();
            if (kind == "Shields") return "CER_SidearmAssignment_CategoryShields".Translate();
            return null;
        }

        // The "Max sidearm bulk" limit from the mod settings as a number - ONE place for the solver and
        // the picker window. Previously AssignPlanner parsed the string itself, and the window only knew
        // the string and did nothing with it.
        public static float MaxSidearmBulk()
        {
            string bulkSetting = CEReloadedMod.settings?.sidearmMaxBulk ?? "5.0";
            if (!float.TryParse(bulkSetting.Replace(',', '.'), System.Globalization.NumberStyles.Any,
                System.Globalization.CultureInfo.InvariantCulture, out float v)) v = 5.0f;
            return v;
        }

        // The bulk this limit applies to: the loadout's WEAPON (WeaponBulk, exactly like PickSidearm in
        // the solver), not the whole loadout including ammo. Explosives and shields: 0 - the solver does
        // not apply the limit to them (PickGrenade has none), so the window doesn't either.
        public static float CapRelevantBulk(Loadout lo)
        {
            // Same weapon as the solver: PickSidearm measures c.mainWeapon from
            // AnalyzeLoadout/LoadoutMainAndSide - the weapon matching the name, else the best-rated ranged,
            // else the best melee weapon, never a grenade or utility. The "first weapon slot" used to measure
            // a different weapon than the solver for multi-weapon loadouts. Without a main weapon (pure
            // grenade or shield loadout) 0: the solver has no limit there.
            if (lo == null) return 0f;
            LoadoutUtility.LoadoutMainAndSide(lo, out ThingDef main, out _);
            if (main == null || Dialog_GenerateLoadouts.IsGrenade(main) || LoadoutUtility.IsShieldApparel(main)) return 0f;
            return LoadoutUtility.WeaponBulk(main);
        }

        // Untranslated internal classification key - kept separate from the translated display label above
        // so CategoryRank's own comparison below never breaks under a non-English display language.
        private static string CategoryKind(Loadout lo)
        {
            if (lo?.Slots == null) return "Backup weapons";
            if (lo.Slots.Any(s => s.thingDef != null && Dialog_GenerateLoadouts.IsGrenade(s.thingDef))) return "Explosives";
            if (lo.Slots.Any(s => s.thingDef != null && LoadoutUtility.IsShieldApparel(s.thingDef))) return "Shields";
            return "Backup weapons";
        }

        // Comparative 0..100 score for the manual picker's Score column - GetWeaponScore for a real backup
        // weapon or a grenade, GetShieldScore for a shield, read off whichever def Dialog_AssignLoadouts'
        // RepresentativeIcon already picks as "the" item for this loadout (its main weapon, always added
        // before ammo, or a shield/grenade's own single slot).
        public static float ScoreOf(Loadout lo)
        {
            ThingDef d = Dialog_AssignLoadouts.RepresentativeIcon(lo);
            if (d == null) return 0f;
            if (LoadoutUtility.IsShieldApparel(d))
            {
                LoadoutUtility.GetShieldScore(d, out float ssc, out _);
                return ssc;
            }
            LoadoutUtility.GetWeaponScore(d, out float wsc, out _);
            return wsc;
        }

        private static int CategoryRank(Loadout lo)
        {
            string c = CategoryKind(lo);
            if (c == "Explosives") return 1;
            if (c == "Shields") return 2;
            return 0;   // Backup weapons
        }
    }
}
