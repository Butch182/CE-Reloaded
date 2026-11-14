using System.Collections.Generic;
using Verse;
using RimWorld;
using UnityEngine;

namespace CEReloaded
{
    public class CEReloadedSettings : ModSettings
    {
        // Skill requirement ranges default to the full min..max the game supports (vanilla 0..20; read from
        // SkillRecord so it tracks whatever the running game/mods define). Players can narrow it afterwards.
        public int ammoBillMinSkill = 0;
        public int ammoBillMaxSkill = SkillRecord.MaxLevel;
        public int weaponBillMinSkill = 0;
        public int weaponBillMaxSkill = SkillRecord.MaxLevel;


        public string defMealDefName = "GenericMeal";
        public string defMealCount = "2";
        public string defMedicineDefName = "GenericMedicine";
        public string defMedicineCount = "3";
        // Sentinel for the sidearm/shield defaults below: instead of one fixed def, the pick is made dynamically
        // from what the colony owns or can afford to craft (Dialog_GenerateLoadouts.EnsureAutoPicks). An explicit
        // def always wins; "" means none.
        public const string AutoPick = "auto";

        // One preset button's COMPLETE slider set (0..1 each) for the Squad Setup popup - generate side and assign
        // side. Presets are full configurations: applying one sets every slider, including Anti-mech, so two presets
        // never half-mix. Editable in the mod settings (Presets tab); factory defaults below.
        public class PresetTuning : IExposable
        {
            public float pool, mech, count, ammoVar;   // generate side
            public float craft, melee, diversity, budget;       // assign side
            public float packCoverage;                          // assign side (Equipment Packs)

            public void ExposeData()
            {
                Scribe_Values.Look(ref pool, "pool", 0.3f);
                Scribe_Values.Look(ref mech, "mech", 0.5f);
                Scribe_Values.Look(ref count, "count", 0.35f);
                Scribe_Values.Look(ref ammoVar, "ammoVar", 0.5f);
                Scribe_Values.Look(ref craft, "craft", 0.3f);
                Scribe_Values.Look(ref melee, "melee", 0.35f);
                Scribe_Values.Look(ref diversity, "diversity", 0.7f);
                Scribe_Values.Look(ref budget, "budget", 0.8f);
                Scribe_Values.Look(ref packCoverage, "packCoverage", 0.5f);
            }
        }

        // Factory defaults per preset - the hardcoded baseline the settings' Reset restores.
        public static PresetTuning DefaultPresetBalanced() => new PresetTuning
        { pool = 0.3f, mech = 0.5f, count = 0.35f, ammoVar = 0.5f, craft = 0.3f, melee = 0.35f, diversity = 0.7f, budget = 0.8f, packCoverage = 0.5f };
        public static PresetTuning DefaultPresetAntiMech() => new PresetTuning
        { pool = 0.3f, mech = 0.75f, count = 0.35f, ammoVar = 0.65f, craft = 0.3f, melee = 0.35f, diversity = 0.6f, budget = 0.9f, packCoverage = 0.5f };
        // Economy is the frugal preset: the low pool trims shields further via the pool-gate in RecomputeGenerate.
        // budget = 0 (was 0.35): Economy is meant to push "cheap weapons" to the minimum - that is THE dial this
        // button is about, so it sits at the stop here. craft deliberately stays just above 0 (0.15), not at 0:
        // 0 means "build NOTHING" (AssignPlanner then drops candidates without stock entirely) and would just be a
        // second copy of "Available items". The intended order is: take what is there first, and if nothing is
        // there, craft the CHEAP variant.
        //
        // ====== diversity 0.7 -> 0.15, and THAT was the cause of excessive production ======
        // Symptom: Economy still produced far too much. With "Cheap weapons" all the way left (so the dial did
        // take effect), the plan still contained Kord 6P50, MG 338, T-9 launcher, SKS and KS-23 side by side.
        // Worked through in AssignPlanner: the price penalty for a weapon that must be crafted is
        // priceWeight * cost share, at least 45 points. The repetition penalty next to it is
        // diversity * 55 * (times already assigned) - at 0.7 that is 38.5 for the SECOND colonist with the same
        // weapon, 77 for the third, plus a diversity * 25 bonus for every still-unfilled weapon class
        // (SupportMG, Marksman, CQC, Launcher). With nine colonists this penalty overtakes the price from the
        // third one on - after that every expensive weapon wins simply because it is new. The budget dial can do
        // nothing against that, however far left it is.
        // Frugal, for weapons, means ONE type, one caliber, one production chain, built in quantity. That is
        // exactly the left end of this dial ("Always use best"), which is why it now sits close to the stop
        // instead of the middle. Not at 0: a melee and a ranged pick may still differ, and a colonist who is bad
        // with the weapon should not have the same one forced on them.
        // Existing settings: these presets go through Scribe_Deep, so the OLD value really is stored in the
        // saved settings file - changing the factory default alone reaches nobody there.
        // Hence the migration line in ExposeData below, same as for budget 0.35 -> 0.
        public static PresetTuning DefaultPresetEconomy() => new PresetTuning
        { pool = 0.15f, mech = 0.5f, count = 0.35f, ammoVar = 0.3f, craft = 0.15f, melee = 0.35f, diversity = 0.15f, budget = 0f, packCoverage = 0.2f };
        public static PresetTuning DefaultPresetBestGear() => new PresetTuning
        { pool = 1f, mech = 0.5f, count = 0.1f, ammoVar = 0.8f, craft = 0.9f, melee = 0.35f, diversity = 0.7f, budget = 1f, packCoverage = 0.8f };
        public static PresetTuning DefaultPresetRanged() => new PresetTuning
        { pool = 0.3f, mech = 0.5f, count = 0.35f, ammoVar = 0.5f, craft = 0.3f, melee = 0.1f, diversity = 0.7f, budget = 0.8f, packCoverage = 0.5f };
        // Best Gear, but "High value <-> all loadouts" pushed all the way right: every weapon the pool contains
        // (which, at pool=1, is every owned-or-researched weapon) gets a loadout instead of just each class's top.
        public static PresetTuning DefaultPresetAll() => new PresetTuning
        { pool = 1f, mech = 0.5f, count = 1f, ammoVar = 0.8f, craft = 0.9f, melee = 0.35f, diversity = 0.7f, budget = 1f, packCoverage = 0.8f };
        // Balanced, but BOTH "prefer available" dials pinned fully to available: pool=0 (generate side -
        // only weapons the colony already owns enter the pool, never the full researched list) and craft=0
        // (assign side - never queue production, just redistribute/assign from what's already in stock).
        // count=1 ("All loadouts"): the quality bar is a SEPARATE concern from availability - with pool=0
        // already restricting candidates to owned weapons only, a mid quality bar on top of that silently
        // dropped a genuinely-owned-and-stocked weapon (e.g. a MAC-10, low score but 10 sitting in storage)
        // just because a rifle scored higher elsewhere. "Available items" means "everything we have", full stop.
        public static PresetTuning DefaultPresetPreferAvailable() => new PresetTuning
        { pool = 0f, mech = 0.5f, count = 1f, ammoVar = 0.5f, craft = 0f, melee = 0.35f, diversity = 0.7f, budget = 0.8f, packCoverage = 0.5f };

        public PresetTuning presetBalanced = DefaultPresetBalanced();
        public PresetTuning presetAntiMech = DefaultPresetAntiMech();
        public PresetTuning presetEconomy = DefaultPresetEconomy();
        public PresetTuning presetBestGear = DefaultPresetBestGear();
        public PresetTuning presetRanged = DefaultPresetRanged();
        public PresetTuning presetAll = DefaultPresetAll();
        public PresetTuning presetPreferAvailable = DefaultPresetPreferAvailable();

        // Arm my Colony's Cautious/Normal/Aggressive mode presets - which of the 6 step switches each one
        // sets, editable in the mod settings (Presets tab) same as the slider presets above. Used to be
        // hardcoded in Dialog_ArmColony.ApplyMode; a player who e.g. wants Normal to also delete weapon
        // bills, or Aggressive to leave Equipment Packs alone, had no way to do that (2026-07-30: players
        // should be able to choose which step is active in which mode).
        public class ArmModePreset : IExposable
        {
            public bool resetPawns, createAmmoBills;
            // 3-state, not a bool (2026-08-01) - None/ManagedOnly/All, so a player who's hand-placed their own
            // weapon/ammo bills can still get Reset's power without losing them: ManagedOnly only ever
            // touches bills this mod's own tooling queued (LoadoutUtility.IsManagedBill), All is the original,
            // blunt "every bill for a recognized type, no matter who set it up" behavior. deleteWeaponLoadouts/
            // deleteEquipmentPacks got the SAME upgrade (2026-08-01) - ManagedOnly only
            // touches loadouts/packs this mod's own wizards generated (LoadoutUtility.IsGeneratedTemplate/
            // IsGeneratedPack), so a hand-built template/pack survives a scoped reset.
            public LoadoutUtility.BillDeleteScope deleteAmmoBills, deleteWeaponBills, deleteWeaponLoadouts, deleteEquipmentPacks;

            // ============ A missing key must mean THIS MODE's default ============
            // Symptom: in Aggressive, "delete weapon loadouts" and "delete equipment packs" were supposed to
            // switch to the middle column but did not. "Just press Reset once" would have been too easy a way
            // out - it would leave a broken state and demand a manual step nobody takes.
            //
            // The mechanism: on load, Scribe_Deep.Look builds a FRESH ArmModePreset and calls ExposeData on it.
            // DefaultArmAggressive() never gets a say afterwards, because the result is not null. If a key was
            // missing from the file - and older files lack exactly these two, they were added later - the
            // default value written here decided. That was None ("keep") for ALL THREE modes, so Aggressive
            // permanently did the opposite of what its own factory default says.
            //
            // This is NOT a migration (migration logic was deliberately removed before the first release) -
            // nothing is rewritten or detected here. The default value is simply the right one now: the mode's
            // instead of the field's. A key that IS in the file still wins, so a deliberately chosen setting
            // stays untouched.
            // A dedicated field for this does NOT work: on load Scribe_Deep builds a fresh object via Activator
            // and calls ExposeData on it - anything a caller set beforehand is already gone. Hence a static slot
            // that the caller sets immediately BEFORE Scribe_Deep.Look and clears afterwards (see LoadArmPreset).
            // Scribe_Deep.Look runs synchronously, so nothing can interfere.
            internal static ArmModePreset LoadingDefaults;

            private static LoadoutUtility.BillDeleteScope Def(System.Func<ArmModePreset, LoadoutUtility.BillDeleteScope> pick,
                                                              LoadoutUtility.BillDeleteScope fallback)
                => LoadingDefaults != null ? pick(LoadingDefaults) : fallback;

            public void ExposeData()
            {
                Scribe_Values.Look(ref resetPawns, "resetPawns", LoadingDefaults != null && LoadingDefaults.resetPawns);
                Scribe_Values.Look(ref deleteAmmoBills, "deleteAmmoBillsScope", Def(p => p.deleteAmmoBills, LoadoutUtility.BillDeleteScope.None));
                Scribe_Values.Look(ref deleteWeaponBills, "deleteWeaponBillsScope", Def(p => p.deleteWeaponBills, LoadoutUtility.BillDeleteScope.None));
                Scribe_Values.Look(ref deleteWeaponLoadouts, "deleteWeaponLoadoutsScope", Def(p => p.deleteWeaponLoadouts, LoadoutUtility.BillDeleteScope.None));
                // This step is two-level now (off / delete hand-built ones); a saved ManagedOnly - the old
                // Aggressive default - now means "off", not "everything".
                if (Scribe.mode == LoadSaveMode.LoadingVars && deleteWeaponLoadouts == LoadoutUtility.BillDeleteScope.ManagedOnly)
                    deleteWeaponLoadouts = LoadoutUtility.BillDeleteScope.None;
                Scribe_Values.Look(ref deleteEquipmentPacks, "deleteEquipmentPacksScope", Def(p => p.deleteEquipmentPacks, LoadoutUtility.BillDeleteScope.None));
                Scribe_Values.Look(ref createAmmoBills, "createAmmoBills", LoadingDefaults == null || LoadingDefaults.createAmmoBills);
            }
        }

        // Factory defaults - the exact behavior Dialog_ArmColony.ApplyMode used to hardcode. Old bool
        // false/true map 1:1 onto None/All (2026-08-01 scope upgrade) - byte-for-byte the same default
        // behavior as before, ManagedOnly is purely a new option a player can dial in manually.
        public static ArmModePreset DefaultArmCautious() => new ArmModePreset
        { resetPawns = false, deleteAmmoBills = LoadoutUtility.BillDeleteScope.None, deleteWeaponBills = LoadoutUtility.BillDeleteScope.None, deleteWeaponLoadouts = LoadoutUtility.BillDeleteScope.None, deleteEquipmentPacks = LoadoutUtility.BillDeleteScope.None, createAmmoBills = true };
        public static ArmModePreset DefaultArmNormal() => new ArmModePreset
        { resetPawns = true, deleteAmmoBills = LoadoutUtility.BillDeleteScope.ManagedOnly, deleteWeaponBills = LoadoutUtility.BillDeleteScope.ManagedOnly, deleteWeaponLoadouts = LoadoutUtility.BillDeleteScope.None, deleteEquipmentPacks = LoadoutUtility.BillDeleteScope.None, createAmmoBills = true };
        // The two LIST rows are on ManagedOnly, not All: Aggressive should set "delete weapons" and
        // "delete equipment packs" to the Normal option, not the Cautious one. With both on None, Aggressive was
        // a mode that did nothing at all in two rows - contradicting its own name.
        // ManagedOnly rather than All because the trade-off differs from bills: a bill is re-created in a
        // minute, but a hand-built weapon loadout or a hand-assembled pack is work nobody can restore.
        // "Everything the wizard created itself" is the most aggressive level that still cleans up rather
        // than destroys.
        public static ArmModePreset DefaultArmAggressive() => new ArmModePreset
        { resetPawns = true, deleteAmmoBills = LoadoutUtility.BillDeleteScope.All, deleteWeaponBills = LoadoutUtility.BillDeleteScope.All, deleteWeaponLoadouts = LoadoutUtility.BillDeleteScope.None, deleteEquipmentPacks = LoadoutUtility.BillDeleteScope.ManagedOnly, createAmmoBills = true };

        // Sets this mode's factory defaults while Scribe_Deep builds the object, and clears them afterwards -
        // so they do not accidentally apply to the next mode.
        private static void LoadArmPreset(ref ArmModePreset field, string label, ArmModePreset factory)
        {
            ArmModePreset.LoadingDefaults = factory;
            try { Scribe_Deep.Look(ref field, label); }
            finally { ArmModePreset.LoadingDefaults = null; }
            if (field == null) field = factory;
        }

        public ArmModePreset armCautious = DefaultArmCautious();
        public ArmModePreset armNormal = DefaultArmNormal();
        public ArmModePreset armAggressive = DefaultArmAggressive();

        public string defSidearmDefName = "";        // deprecated single sidearm (kept for save-compat); split below
        public string defSidearmMeleeDefName = AutoPick;   // melee backup handed to RANGED-main pawns ("" = none)
        public string defSidearmRangedDefName = AutoPick;  // ranged backup handed to MELEE-main pawns ("" = none)
        public bool startTabsDetailed = false;         // global default for the per-tab Simple/Detailed toggle
        public string defShieldDefName = AutoPick;    // default CE shield for one-handed-main loadouts ("" = none)
        public string defAmmoAssault = "150";
        public string defAmmoSniper = "60";
        public string defAmmoLMG = "250";
        public string defAmmoShotgun = "50";
        // Pistols had no value of their own until now and fell back to defAmmoDefault = 150, i.e. the assault
        // rifle amount. A pistol is almost always the SECONDARY weapon, so its ammo comes ON TOP of the main
        // weapon's. 24 = two to three magazines (CE pistol magazines hold 7-15) - enough for a sidearm's job
        // ("the main weapon is empty or jammed") without dead weight.
        public string defAmmoPistol = "24";
        public string defAmmoBow = "40";
        public string defAmmoLauncher = "20";
        // Javelins, pila, ... - the weapon IS its own ammo (CE AmmoDef), carried like a quiver of throwing
        // weapons, not a "handful". Used to share ONE setting with thrown explosives below (both matched
        // IsSelfAmmoWeapon) - "4" made sense for grenades but was a poor default for javelins, which you'd
        // realistically want more like a bow's arrow count of (scaled down for their much greater bulk/
        // weight per unit). Split into its own setting so each gets a sensible default.
        public string defAmmoSelfAmmo = "10";
        // Thrown explosives (grenades, satchel charges, ...) - split out from defAmmoSelfAmmo above, which
        // used to cover both; "4" is the right ballpark for something this heavy/dangerous to carry many of.
        public string defAmmoGrenade = "4";
        public string defAmmoDefault = "150";
        public string defAmmoBatches = "1";
        // Ammo demand reserve curve (2026-08-02, by design): the ammo bill this feeds is an "until
        // X in stock" MAINTENANCE bill, not a one-shot production order, so summing every pawn's full
        // configured count (e.g. 9 pawns x 240 rounds = 2160 in the warehouse) assumes everyone empties
        // their mag at once, which never happens. See LoadoutUtility.WeightedAmmoDemand for the actual
        // curve: the first ammoReserveFullWeightPawns pawns sharing a caliber count at 100%, every pawn
        // beyond that at a shrinking 1/rank share, floored at ammoReserveTrailingFloor so a very popular
        // caliber never drops to a token amount. Floor=1.0 reproduces the old "sum everyone" behavior exactly.
        public int ammoReserveFullWeightPawns = 2;
        public float ammoReserveTrailingFloor = 0.10f;
        // 5.0 fits every CE sidearm-class weapon (autopistol 2.1, revolver 2.79, machine pistol 2.95, heavy SMG 4.5,
        // knife 1, gladius/mace 3.5) while the first real main weapons sit clearly above (chain shotgun 6.7+).
        public string sidearmMaxBulk = "5.0";
        public bool useLoadoutPrefix = false;
        public string loadoutPrefix = "W|";
        public bool usePackPrefix = false;
        public string packPrefix = "E|";
        // Roles' internal identity marker is "[Role] <name>" (RoleUtility.RoleMarker) - unlike Weapon
        // Loadouts/Equipment Packs, that marker is baked into the real stored label and used for identity
        // matching (RoleUtility.IsRoleLabel), so it can't just be swapped for a clean prefix the way
        // loadoutPrefix/packPrefix are. This is a DISPLAY-ONLY prefix instead (2026-08-02:
        // Roles showed their raw "[Role] Name" everywhere, unlike the other two template types' clean
        // configurable prefixes) - RoleUtility.DisplayLabel strips the internal marker and applies this at
        // every player-facing spot, while the actual stored label/matching is untouched.
        public bool useRolePrefix = true;
        public string rolePrefix = "[Role]";
        public bool hideDuplicateBenches = true;
        // Produce missing weapons: count weapons already queued on a bench against the shortfall, so open
        // production is claimed first and nothing is over-produced.
        public bool claimOpenWeaponProduction = true;
        public bool seenGuide = false;   // has the workflow overview auto-opened once?

        // Combat-readiness alerts (vanilla-style, top-right). Master toggle + per-alert switches.
        public bool showReadinessAlerts = true;
        public bool alertNoLoadout = true;
        public bool alertWeaponWithoutAmmo = true;
        public bool alertAmmoNotProduced = true;
        public string alertAmmoStockThreshold = "0";   // only warn if fewer than X rounds stockpiled (0 = always when no bill)
        public bool alertLowAmmo = true;
        // How many missing magazines trigger the "low ammo" alert: configurable, default stays at 2 magazines.
        // It used to be hardcoded to 2, while the CE community plans for 5-6 magazines per fight - anyone who
        // wants a higher number can set it now, without the mod making a balance decision for everyone.
        public string alertLowAmmoMags = "2";
        public bool alertOverloaded = true;
        public bool alertWeaponUnavailable = true;
        public bool alertCapacityBlocked = true;
        // Eighth category - see ReadinessIssue.AssignedNotCarried.
        public bool alertAssignedNotCarried = true;

        // Default column the Generate Loadouts weapon list sorts by when the tab opens.
        public SortMode defLoadoutSort = SortMode.Score;

        // Which ammo variant a new firearm loadout defaults to (classic FMJ by default).
        public DefaultFirearmAmmo defFirearmAmmo = DefaultFirearmAmmo.FMJ;

        // Role templates (scaling factors applied to a weapon loadout in Role Mode). Seeded with presets on first use.
        public List<LoadoutRole> roles = new List<LoadoutRole>();

        public WeaponFilterMode defWeaponFilter = WeaponFilterMode.AvailableWeapons;
        public WeaponFilterMode defSidearmFilter = WeaponFilterMode.ResearchedWeapons;   // "Available" hid pistols on colonies that own none yet
        public AmmoFilterMode defAmmoFilter = AmmoFilterMode.AllAmmo;
        public AmmoBillFilterMode defAmmoBillFilter = AmmoBillFilterMode.AssignedLoadouts;

        // Assign Loadouts tab - default states of its controls (the fine-tuning sliders live in the auto-assign
        // preview popup and are remembered per save instead).
        public AssignLoadoutFilter autoAssignLoadoutFilter = AssignLoadoutFilter.AvailableMainWeapon;
        // Replaces the old bool `autoAssignRolesOnly` - see RoleAssignMode. The old value is carried over on
        // load, see the migration in ExposeData.
        public RoleAssignMode autoAssignRoleMode = RoleAssignMode.Mixed;


        public override void ExposeData()
        {
            base.ExposeData();
            Scribe_Deep.Look(ref presetBalanced, "presetBalanced");
            Scribe_Deep.Look(ref presetAntiMech, "presetAntiMech");
            Scribe_Deep.Look(ref presetEconomy, "presetEconomy");
            Scribe_Deep.Look(ref presetBestGear, "presetBestGear");
            Scribe_Deep.Look(ref presetRanged, "presetRanged");
            Scribe_Deep.Look(ref presetAll, "presetAll");
            Scribe_Deep.Look(ref presetPreferAvailable, "presetPreferAvailable");
            if (presetBalanced == null) presetBalanced = DefaultPresetBalanced();
            if (presetAntiMech == null) presetAntiMech = DefaultPresetAntiMech();
            if (presetEconomy == null) presetEconomy = DefaultPresetEconomy();
            if (presetBestGear == null) presetBestGear = DefaultPresetBestGear();
            if (presetRanged == null) presetRanged = DefaultPresetRanged();
            if (presetAll == null) presetAll = DefaultPresetAll();
            if (presetPreferAvailable == null) presetPreferAvailable = DefaultPresetPreferAvailable();
            // Via LoadArmPreset rather than directly: a key MISSING from the file must mean THIS MODE's default,
            // not the field's. Full reasoning at ArmModePreset.LoadingDefaults.
            LoadArmPreset(ref armCautious, "armCautious", DefaultArmCautious());
            LoadArmPreset(ref armNormal, "armNormal", DefaultArmNormal());
            LoadArmPreset(ref armAggressive, "armAggressive", DefaultArmAggressive());
            // The three BillDeleteScope migrations that used to be here were removed (before the first release
            // there are no old settings files to bridge).
            // They were meant to bridge settings files from earlier development builds by raising a saved
            // None/None to the current factory default.
            // Two reasons why removing them is safe: first, they ran ungated in ExposeData and therefore also
            // on SAVE, which flipped any Arm setting deliberately set to None back up within the same game
            // session - they were the bug themselves. Second, the failure direction is harmless: without them an
            // old file stays at None/None, and Aggressive then deletes LESS than intended rather than more.
            // A brand-new settings file gets DefaultArmAggressive() = All/All anyway.
            // If Aggressive is on None after an update: set it to All once in the Presets tab, done.
            // One-time migration: a settings file saved before this preset's "count" default changed from
            // 0.35 to 1f (see DefaultPresetPreferAvailable's own comment) still has the stale 0.35 baked in -
            // the field initializer above only ever applies to a BRAND NEW file, never overwrites an
            // already-loaded value. Narrow and exact on purpose: only touches the specific old default,
            // never a value the player deliberately chose afterwards.
            // LOAD ONLY: ExposeData also runs on SAVE, and ungated this line overwrote a freshly typed value
            // there. The preset grid uses Widgets.TextFieldNumeric - typing 0.35 by hand and closing the settings
            // wrote the correct number to the file, but in memory it was already back to 1.0; the next write then
            // put the 1.0 on disk too. Exactly the pattern the comment above the removed BillDeleteScope
            // migrations describes as "they were the bug themselves" - the same trap, one migration later.
            if (Scribe.mode == LoadSaveMode.LoadingVars
                && Mathf.Approximately(presetPreferAvailable.count, 0.35f)) presetPreferAvailable.count = 1f;
            // Same pattern, same reasoning, for Economy budget 0.35 -> 0. Without this line the change (Economy
            // sets cheap weapons to the minimum) would stay invisible in every existing game: the saved settings
            // file has 0.35, and a changed factory default only reaches a brand-new file. As above, LOAD ONLY and
            // only for the exact old default - anyone who types 0.35 themselves later keeps it until the next
            // game start.
            if (Scribe.mode == LoadSaveMode.LoadingVars
                && Mathf.Approximately(presetEconomy.budget, 0.35f)) presetEconomy.budget = 0f;
            // Same pattern for Economy diversity 0.7 -> 0.15 (Economy still produced far too much) - reasoning at
            // DefaultPresetEconomy. LOAD ONLY and only for the exact old default; anyone who entered 0.7
            // themselves keeps it.
            if (Scribe.mode == LoadSaveMode.LoadingVars
                && Mathf.Approximately(presetEconomy.diversity, 0.7f)) presetEconomy.diversity = 0.15f;
            Scribe_Values.Look(ref ammoBillMinSkill, "ammoBillMinSkill", 0);
            Scribe_Values.Look(ref ammoBillMaxSkill, "ammoBillMaxSkill", SkillRecord.MaxLevel);
            Scribe_Values.Look(ref weaponBillMinSkill, "weaponBillMinSkill", 0);
            Scribe_Values.Look(ref weaponBillMaxSkill, "weaponBillMaxSkill", SkillRecord.MaxLevel);

            Scribe_Values.Look(ref defMealDefName, "defMealDefName", "GenericMeal");
            Scribe_Values.Look(ref defMealCount, "defMealCount", "2");
            Scribe_Values.Look(ref defMedicineDefName, "defMedicineDefName", "GenericMedicine");
            Scribe_Values.Look(ref defMedicineCount, "defMedicineCount", "3");
            Scribe_Values.Look(ref defSidearmDefName, "defSidearmDefName", "");
            Scribe_Values.Look(ref defSidearmMeleeDefName, "defSidearmMeleeDefName", AutoPick);
            Scribe_Values.Look(ref defSidearmRangedDefName, "defSidearmRangedDefName", AutoPick);
            Scribe_Values.Look(ref defShieldDefName, "defShieldDefName", AutoPick);
            Scribe_Values.Look(ref defAmmoAssault, "defAmmoAssault", "150");
            Scribe_Values.Look(ref defAmmoSniper, "defAmmoSniper", "60");
            Scribe_Values.Look(ref defAmmoLMG, "defAmmoLMG", "250");
            Scribe_Values.Look(ref defAmmoShotgun, "defAmmoShotgun", "50");
            Scribe_Values.Look(ref defAmmoPistol, "defAmmoPistol", "24");
            Scribe_Values.Look(ref defAmmoBow, "defAmmoBow", "40");
            Scribe_Values.Look(ref defAmmoLauncher, "defAmmoLauncher", "20");
            Scribe_Values.Look(ref defAmmoSelfAmmo, "defAmmoSelfAmmo", "10");
            Scribe_Values.Look(ref defAmmoGrenade, "defAmmoGrenade", "4");
            Scribe_Values.Look(ref defAmmoDefault, "defAmmoDefault", "150");
            Scribe_Values.Look(ref defAmmoBatches, "defAmmoBatches", "1");
            Scribe_Values.Look(ref ammoReserveFullWeightPawns, "ammoReserveFullWeightPawns", 2);
            Scribe_Values.Look(ref ammoReserveTrailingFloor, "ammoReserveTrailingFloor", 0.10f);
            Scribe_Values.Look(ref sidearmMaxBulk, "sidearmMaxBulk", "5.0");
            Scribe_Values.Look(ref useLoadoutPrefix, "useLoadoutPrefix", false);
            Scribe_Values.Look(ref loadoutPrefix, "loadoutPrefix", "W|");
            Scribe_Values.Look(ref usePackPrefix, "usePackPrefix", false);
            Scribe_Values.Look(ref packPrefix, "packPrefix", "E|");
            Scribe_Values.Look(ref useRolePrefix, "useRolePrefix", true);
            Scribe_Values.Look(ref rolePrefix, "rolePrefix", "[Role]");
            Scribe_Values.Look(ref hideDuplicateBenches, "hideDuplicateBenches", true);
            Scribe_Values.Look(ref claimOpenWeaponProduction, "claimOpenWeaponProduction", true);
            Scribe_Values.Look(ref seenGuide, "seenGuide", false);
            Scribe_Values.Look(ref startTabsDetailed, "startTabsDetailed", false);
            Scribe_Values.Look(ref showReadinessAlerts, "showReadinessAlerts", true);
            Scribe_Values.Look(ref alertNoLoadout, "alertNoLoadout", true);
            Scribe_Values.Look(ref alertWeaponWithoutAmmo, "alertWeaponWithoutAmmo", true);
            Scribe_Values.Look(ref alertAmmoNotProduced, "alertAmmoNotProduced", true);
            Scribe_Values.Look(ref alertAmmoStockThreshold, "alertAmmoStockThreshold", "0");
            Scribe_Values.Look(ref alertLowAmmo, "alertLowAmmo", true);
            Scribe_Values.Look(ref alertLowAmmoMags, "alertLowAmmoMags", "2");
            Scribe_Values.Look(ref alertOverloaded, "alertOverloaded", true);
            Scribe_Values.Look(ref alertWeaponUnavailable, "alertWeaponUnavailable", true);
            Scribe_Values.Look(ref alertCapacityBlocked, "alertCapacityBlocked", true);
            Scribe_Values.Look(ref alertAssignedNotCarried, "alertAssignedNotCarried", true);

            Scribe_Values.Look(ref defLoadoutSort, "defLoadoutSort", SortMode.Score);
            Scribe_Values.Look(ref defFirearmAmmo, "defFirearmAmmo", DefaultFirearmAmmo.FMJ);
            Scribe_Collections.Look(ref roles, "roles", LookMode.Deep);
            if (Scribe.mode == LoadSaveMode.PostLoadInit && roles == null) roles = new List<LoadoutRole>();
            Scribe_Values.Look(ref defWeaponFilter, "defWeaponFilter", WeaponFilterMode.AvailableWeapons);
            Scribe_Values.Look(ref defSidearmFilter, "defSidearmFilter", WeaponFilterMode.ResearchedWeapons);
            Scribe_Values.Look(ref defAmmoFilter, "defAmmoFilter", AmmoFilterMode.AllAmmo);
            Scribe_Values.Look(ref defAmmoBillFilter, "defAmmoBillFilter", AmmoBillFilterMode.AssignedLoadouts);
            // "Created loadouts" no longer exists (see AmmoBillFilterMode).
            if (defAmmoBillFilter == AmmoBillFilterMode.AvailableLoadouts) defAmmoBillFilter = AmmoBillFilterMode.AssignedLoadouts;

            Scribe_Values.Look(ref autoAssignLoadoutFilter, "autoAssignLoadoutFilter", AssignLoadoutFilter.AvailableMainWeapon);
            Scribe_Values.Look(ref autoAssignRoleMode, "autoAssignRoleMode", RoleAssignMode.Mixed);
            // Migration from the old bool. Without it, anyone who had "Roles only" ticked would silently fall
            // back to "mixed" - a behavior change nobody asked for. LOAD ONLY, and only if the old node is in the
            // file at all: Scribe_Values does not write a default value, so an untouched "false" is never there.
            if (Scribe.mode == LoadSaveMode.LoadingVars)
            {
                bool legacyRolesOnly = false;
                Scribe_Values.Look(ref legacyRolesOnly, "autoAssignRolesOnly", false);
                if (legacyRolesOnly && autoAssignRoleMode == RoleAssignMode.Mixed)
                    autoAssignRoleMode = RoleAssignMode.RolesOnly;
            }
        }
    }
}
