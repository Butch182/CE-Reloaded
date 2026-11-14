using Verse;

namespace CEReloaded
{
    // Per-save snapshot of the three tabs' UI state (filters, sort, collapse). Seeded from the global mod settings when a
    // colony is first played, then it persists with the save and survives reloads - so a player's tweaks stick, while a
    // brand-new colony starts from the settings defaults. Held by CEReloadedGameComponent.
    public class CERUiState : IExposable
    {
        // Generate Loadouts
        public SortMode loSort = SortMode.Score;
        public bool loSortDesc = true;
        public WeaponFilterMode loWeaponFilter = WeaponFilterMode.AvailableWeapons;
        public WeaponFilterMode loSidearmFilter = WeaponFilterMode.AvailableWeapons;
        public AmmoFilterMode loAmmoFilter = AmmoFilterMode.AllAmmo;
        public WeaponCategory loCategory = WeaponCategory.All;
        public bool loDetailed = false;     // Simple/Detailed toggle for this tab, seeded from settings.startTabsDetailed

        // Supply/bulk defaults edited IN the tabs - per save. Direction contract: settings changes flow INTO the
        // save (seed + settings-close sync), tab edits never write back to the global settings. null = unseeded
        // (fall back to settings), "" = an explicit None.
        // loMealDefName/loMealCount/loMedDefName/loMedCount were removed: meal and medicine are no longer
        // seeded into the save, but read directly from the mod settings when an Equipment Pack is created.
        // An old save still carries the four values - Scribe simply skips them.
        public string loSidearmMaxBulk;
        public string abDefaultBatches;

        // Generate Ammo Bills
        public AmmoBillFilterMode abFilter = AmmoBillFilterMode.AssignedLoadouts;
        public bool abFiltersOpen = false;
        public bool abDetailed = false;

        // Assign Loadouts
        public AssignLoadoutFilter asLoadoutFilter = AssignLoadoutFilter.AvailableMainWeapon;
        public bool asSortMelee = false;    // pawn grid sort: false = by ranged score, true = by melee score
        public bool asCompact = false;      // pawn grid: full cards vs one slim table row per colonist
        // Used to be a bool (now RoleAssignMode) - see there. Migration below.
        public RoleAssignMode asRoleMode = RoleAssignMode.Mixed;
        public bool asDetailed = false;

        // Auto-assign preview: the CHECKBOXES per save. The four slider fields below are dead -
        // Dialog_SquadSetup starts from settings.presetBalanced every time it opens (principle: sliders
        // never live in the save; defaults are changed in the presets in the mod settings). The fields
        // only remain so an old save doesn't miss anything.
        public float asCraft = 0.3f;        // use existing weapons <-> craft new weapons (tipping point on MaterialAffordability)
        public float asMelee = 0.35f;       // prefer ranged <-> prefer melee
        public float asDiversity = 0.7f;    // always use best <-> cover different roles
        public float asBudget = 0.8f;       // cheap weapons <-> best weapon (tipping point on MaterialAffordability, quadratic)
        public bool asKeepAssigned = true;
        public bool asRedistribute = true;  // carried non-loadout weapons count as redistributable (and are dropped on apply)
        public bool asCreateNew = false;    // let the solver invent brand-new default loadouts (virtual picks); off by default
        // Re-pick each pawn's ammo fresh at assign time (current stock/research + the mech slider) instead of
        // trusting whatever's frozen in the shared Loadout template - on by default (2026-08-01).
        public bool asOverrideAmmo = true;
        // "Respect max bulk" (2026-08-25): the solver keeps every assignment (main weapon +
        // packs + sidearms) under the colonist's own CE carry bulk/weight instead of only warning afterward.
        // On by default; a missing key in an older save reads as true, which is the intended default.

        // Auto-generate popup sliders (0..1) + flags - kept in memory until the game restarts, not per save.
        public float agPool = 0.3f;         // for available weapons <-> for researched weapons (base anchor; see poolManuallySet)
        public float agCount = 0.35f;       // high value loadouts <-> all loadouts
        public float agAmmoVar = 0.5f;      // only one ammo type <-> maximum flexibility (how eagerly rations are split)

        // Shared Anti-flesh <-> Anti-mech slider (used by BOTH generate-time ammo/grenade choice and assign-time
        // target scoring - one dialect, one value). The CORE is intelligent
        // (confirmed mechanoid encounters, else a slow days-played ramp - section 6a); the slider itself is only a
        // tipping-point seed. "ManuallySet" flags mean: once the player drags a slider themselves, the dynamic system
        // stops touching it - their explicit choice always wins, forever (until they use "Use recommended setup",
        // which resets these flags so the intelligent core resumes control).
        public float mechAffinity = 0.5f;
        public bool mechManuallySet = false;
        public bool poolManuallySet = false;
        public bool hasFoughtMechanoids = false;   // sticky: true the first time a hostile mechanoid is seen on the map

        public static CERUiState FromSettings(CEReloadedSettings s)
        {
            CERUiState u = new CERUiState();
            if (s == null) return u;
            u.loSort = s.defLoadoutSort;
            u.loSortDesc = s.defLoadoutSort == SortMode.Score;   // Score reads best-first
            u.loWeaponFilter = s.defWeaponFilter;
            u.loSidearmFilter = s.defSidearmFilter;
            u.loAmmoFilter = s.defAmmoFilter;
            u.loSidearmMaxBulk = s.sidearmMaxBulk;
            u.abDefaultBatches = s.defAmmoBatches;
            u.abFilter = s.defAmmoBillFilter;
            u.asLoadoutFilter = s.autoAssignLoadoutFilter;
            u.asRoleMode = s.autoAssignRoleMode;
            u.loDetailed = s.startTabsDetailed;
            u.abDetailed = s.startTabsDetailed;
            u.asDetailed = s.startTabsDetailed;
            return u;
        }

        public void ExposeData()
        {
            Scribe_Values.Look(ref loSort, "loSort", SortMode.Score);
            Scribe_Values.Look(ref loSortDesc, "loSortDesc", true);
            Scribe_Values.Look(ref loWeaponFilter, "loWeaponFilter", WeaponFilterMode.AvailableWeapons);
            Scribe_Values.Look(ref loSidearmFilter, "loSidearmFilter", WeaponFilterMode.AvailableWeapons);
            Scribe_Values.Look(ref loAmmoFilter, "loAmmoFilter", AmmoFilterMode.AllAmmo);
            Scribe_Values.Look(ref loCategory, "loCategory", WeaponCategory.All);
            Scribe_Values.Look(ref loDetailed, "loDetailed", false);
            Scribe_Values.Look(ref loSidearmMaxBulk, "loSidearmMaxBulk");
            Scribe_Values.Look(ref abDefaultBatches, "abDefaultBatches");

            Scribe_Values.Look(ref abFilter, "abFilter", AmmoBillFilterMode.AssignedLoadouts);
            Scribe_Values.Look(ref abFiltersOpen, "abFiltersOpen", false);
            Scribe_Values.Look(ref abDetailed, "abDetailed", false);

            Scribe_Values.Look(ref asLoadoutFilter, "asLoadoutFilter", AssignLoadoutFilter.AvailableMainWeapon);
            Scribe_Values.Look(ref asSortMelee, "asSortMelee", false);
            Scribe_Values.Look(ref asCompact, "asCompact", false);
            Scribe_Values.Look(ref asRoleMode, "asRoleMode", RoleAssignMode.Mixed);
            // Migration from the old bool, same reasoning as in CEReloadedSettings:
            // a save with "roles only" checked must not silently fall back to
            // mixed.
            if (Scribe.mode == LoadSaveMode.LoadingVars)
            {
                bool legacyRolesOnly = false;
                Scribe_Values.Look(ref legacyRolesOnly, "asRolesOnly", false);
                if (legacyRolesOnly && asRoleMode == RoleAssignMode.Mixed) asRoleMode = RoleAssignMode.RolesOnly;
            }
            Scribe_Values.Look(ref asDetailed, "asDetailed", false);
            // The sliders of both assistants (asCraft/asMelee/asDiversity/asBudget, agPool/agCount/agAmmoVar,
            // mechAffinity plus the two hand locks) are NO LONGER saved: opening the Assign Loadouts screen
            // showed the most recently used slider positions instead of the recommendation. The state came
            // from the save: whoever last touched the sliders decided what showed up on the next load.
            // They remain fields, so they are remembered until the game restarts (opening twice = same
            // state); a loaded save starts with the recommendation. The checkboxes below
            // (Keep/Redistribute/...) are settings, not sliders - those stay saved. Scribe simply skips old
            // values in a save.
            Scribe_Values.Look(ref asKeepAssigned, "asKeepAssigned", true);
            Scribe_Values.Look(ref asRedistribute, "asRedistribute", true);
            Scribe_Values.Look(ref asCreateNew, "asCreateNew", false);
            Scribe_Values.Look(ref asOverrideAmmo, "asOverrideAmmo", true);
            Scribe_Values.Look(ref hasFoughtMechanoids, "hasFoughtMechanoids", false);
        }
    }
}
