using System.Collections.Generic;
using Verse;
using RimWorld;
using UnityEngine;

namespace CEReloaded
{
    public class CEReloadedSettings : ModSettings
    {
        // Skill requirement ranges default to the full min..max the game supports (vanilla 0..20; read from
        // SkillRecord so it tracks whatever the running game/mods define). The user can narrow it afterwards.
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
        // budget = 0 seit 2026-08-14 (vorher 0.35): Economy soll "cheap weapons" auf das Minimum setzen
        // - das ist DER Regler, um den es bei diesem Knopf geht, also
        // steht er hier am Anschlag. craft bleibt bewusst knapp ueber 0 (0.15) und nicht auf 0: 0 heisst
        // "baue NICHTS" (AssignPlanner wirft Kandidaten ohne Bestand dann ganz raus) und waere damit nur eine
        // zweite Kopie von "Available items". Gewollt ist diese Reihenfolge - erst nehmen, was da ist, und
        // wenn nichts da ist, die GUENSTIGE Variante nachbauen.
        //
        // ====== diversity 0.7 -> 0.15 (2026-08-19), und DAS war die Ursache des zweiten Befunds ======
        // Befund: Economy produzierte immer noch extrem viel. Ein Screenshot zeigte die Ursache:
        // "Cheap weapons" stand ganz links, der Regler kam also an; geplant
        // wurden trotzdem Kord 6P50, MG 338, T-9 Werfer, SKS und KS-23 nebeneinander.
        // Nachgerechnet an AssignPlanner: der Preisabzug fuer eine zu bauende Waffe liegt bei
        // priceWeight * Kostenanteil, mindestens 45 Punkte. Der Wiederholungsabzug daneben ist
        // diversity * 55 * (wie oft schon vergeben) - bei 0.7 also 38.5 fuer den ZWEITEN Kolonisten mit
        // derselben Waffe, 77 fuer den dritten, dazu diversity * 25 Bonus fuer jede noch unbesetzte
        // Waffenklasse (SupportMG, Marksman, CQC, Launcher). Bei neun Kolonisten ueberholt dieser Abzug den
        // Preis ab dem dritten - danach gewinnt jede teure Waffe, einfach weil sie neu ist. Der
        // Budget-Regler kann dagegen nichts ausrichten, egal wie weit links er steht.
        // Sparsam heisst bei Waffen: EINE Sorte, ein Kaliber, eine Fertigungskette, in Menge gebaut.
        // Genau das ist die linke Seite dieses Reglers ("Always use best"), und deshalb steht er hier jetzt
        // fast am Anschlag statt in der Mitte. Nicht auf 0: eine Nahkampf- und eine Fernkampfwahl darf sich
        // weiterhin unterscheiden, und ein Kolonist, dem die Waffe gar nicht liegt, soll nicht dieselbe
        // aufgezwungen bekommen.
        // Bestand: diese Presets gehen ueber Scribe_Deep, der ALTE Wert steht also wirklich in der
        // gespeicherten Einstellungsdatei - ein geaenderter Werksstandard allein erreicht dort niemanden.
        // Deshalb steht unten in ExposeData eine Migrationszeile, genau wie fuer budget 0.35 -> 0.
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

            // ============ Ein fehlender Schluessel muss die Vorgabe DIESES MODUS bedeuten (2026-08-17) ============
            // Befund: bei Aggressive sollten "delete weapon loadouts" und "delete equipment packs" auf die
            // mittlere Spalte wechseln, taten es aber nicht. Ein "einmal Zuruecksetzen druecken" waere zu
            // bequem gewesen - es haette einen kaputten Zustand gelassen und einen Handgriff verlangt, den
            // niemand macht.
            //
            // Der Mechanismus: Scribe_Deep.Look baut beim Laden ein FRISCHES ArmModePreset und ruft darauf
            // ExposeData. DefaultArmAggressive() kommt danach nie zum Zug, weil das Ergebnis nicht null ist.
            // Fehlte ein Schluessel in der Datei - und in seiner fehlen genau diese zwei, sie sind erst am
            // 2026-08-01 dazugekommen -, dann entschied der hier hingeschriebene Vorgabewert. Der stand fuer
            // ALLE DREI Modi auf None, also "behalten". Der Aggressive-Modus hat damit dauerhaft das Gegenteil
            // dessen getan, was sein eigener Werksstand sagt.
            //
            // Das ist KEINE Migration (Migrationslogik ist vor dem ersten Release bewusst entfernt worden,
            // 2026-08-02) - hier wird nichts umgeschrieben und nichts erkannt. Der Vorgabewert
            // ist nur endlich der richtige: der des Modus statt der des Feldes. Ein Schluessel, der in der
            // Datei STEHT, gewinnt weiterhin - eine bewusst gewaehlte Einstellung bleibt also unangetastet.
            // Ein eigenes Feld dafuer geht NICHT: Scribe_Deep baut beim Laden ein frisches Objekt ueber
            // Activator und ruft darauf ExposeData - alles, was ein Aufrufer vorher gesetzt haette, ist da
            // schon weg. Deshalb eine statische Ablage, die der Aufrufer unmittelbar VOR Scribe_Deep.Look
            // setzt und danach wieder leert (siehe LoadArmPreset). Scribe_Deep.Look laeuft synchron, es kann
            // also nichts dazwischenkommen.
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
                // Der Schritt ist seit 2026-09-06 zweistufig (aus / von Hand gebaute loeschen); ein gespeichertes
                // ManagedOnly - die alte Aggressive-Vorgabe - heisst jetzt "aus", nicht "alles".
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
        // Die zwei LISTEN-Zeilen stehen auf ManagedOnly, nicht auf All (2026-08-16): Aggressive soll
        // "delete weapons" und "delete equipment packs" auf die Normal-Option setzen, nicht auf Cautious.
        // Standen beide auf None, war Aggressive ein Modus, der in zwei Zeilen gar nichts tut - ein
        // Widerspruch zu seinem eigenen Namen.
        // ManagedOnly und nicht All, weil der Unterschied hier ein anderer ist als bei den Auftraegen: ein
        // Auftrag ist in einer Minute neu gestellt, eine von Hand gebaute Waffen-Vorlage oder ein selbst
        // zusammengestelltes Pack ist Arbeit, die niemand wiederherstellen kann. "Alles, was der Assistent
        // selbst angelegt hat" ist die aggressivste Stufe, die noch aufraeumt statt zu zerstoeren.
        public static ArmModePreset DefaultArmAggressive() => new ArmModePreset
        { resetPawns = true, deleteAmmoBills = LoadoutUtility.BillDeleteScope.All, deleteWeaponBills = LoadoutUtility.BillDeleteScope.All, deleteWeaponLoadouts = LoadoutUtility.BillDeleteScope.None, deleteEquipmentPacks = LoadoutUtility.BillDeleteScope.ManagedOnly, createAmmoBills = true };

        // Setzt die Werksvorgaben dieses Modus, solange Scribe_Deep das Objekt aufbaut, und raeumt sie
        // danach wieder weg - damit sie nicht versehentlich fuer den naechsten Modus gelten.
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
        // Pistolen hatten bis 2026-08-09 gar keinen eigenen Wert und fielen auf defAmmoDefault = 150 zurueck,
        // also Sturmgewehr-Menge. Eine Pistole ist fast immer die ZWEITwaffe, ihre Munition kommt also OBEN
        // DRAUF auf die der Hauptwaffe. 24 = zwei bis drei Magazine (CE-Pistolenmagazine fassen 7-15) - genug
        // fuer den Zweck einer Zweitwaffe ("die Hauptwaffe ist leer oder klemmt"), ohne Ballast.
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
        // Ab wie vielen fehlenden Magazinen die Warnung "wenig Munition" kommt (2026-08-09): einstellbar,
        // Vorgabe bleibt 2 Magazine. Stand vorher hartkodiert auf 2,
        // waehrend die CE-Community mit 5-6 Magazinen pro Gefecht rechnet - wer die Zahl hoeher will, kann
        // sie jetzt stellen, ohne dass der Mod fuer alle eine Balance-Entscheidung trifft.
        public string alertLowAmmoMags = "2";
        public bool alertOverloaded = true;
        public bool alertWeaponUnavailable = true;
        public bool alertCapacityBlocked = true;
        // Neue achte Kategorie (2026-08-09) - siehe ReadinessIssue.AssignedNotCarried.
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
        // Aus dem bool `autoAssignRolesOnly` geworden (2026-08-19) - siehe RoleAssignMode. Der alte
        // Wert wird beim Laden uebernommen, siehe die Migration in ExposeData.
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
            // Ueber LoadArmPreset statt direkt (2026-08-17, Aggressive-Modus): ein
            // Schluessel, der in der Datei FEHLT, muss die Vorgabe DIESES MODUS bedeuten und nicht die des
            // Feldes. Begruendung in voller Laenge an ArmModePreset.LoadingDefaults.
            LoadArmPreset(ref armCautious, "armCautious", DefaultArmCautious());
            LoadArmPreset(ref armNormal, "armNormal", DefaultArmNormal());
            LoadArmPreset(ref armAggressive, "armAggressive", DefaultArmAggressive());
            // Die drei BillDeleteScope-Migrationen, die hier standen, sind am 2026-08-02 entfernt worden
            // (vor dem ersten Release gibt es keine alten Einstellungsdateien zu ueberbruecken).
            // Sie sollten Einstellungsdateien aus frueheren Entwicklungsstaenden ueberbruecken, indem
            // sie ein gespeichertes None/None auf den heutigen Werksstand hochzogen.
            // Zwei Gruende, warum das Entfernen hier unbedenklich ist: erstens standen sie ungegated in
            // ExposeData und liefen damit auch beim SPEICHERN, was jede bewusst auf None gestellte
            // Arm-Einstellung noch in derselben Spielsitzung wieder hochkippte - sie waren also selbst der
            // Bug. Zweitens ist die Richtung des Ausfalls harmlos: ohne sie bleibt eine alte Datei bei
            // None/None stehen, der Aggressive-Modus loescht dann WENIGER als gedacht statt mehr.
            // Ein brandneuer Einstellungsstand bekommt ohnehin DefaultArmAggressive() = All/All.
            // Steht der Aggressive-Modus nach einem Update auf None: einmal in den Presets-Tab
            // und auf All stellen, das war es.
            // One-time migration: a settings file saved before this preset's "count" default changed from
            // 0.35 to 1f (see DefaultPresetPreferAvailable's own comment) still has the stale 0.35 baked in -
            // the field initializer above only ever applies to a BRAND NEW file, never overwrites an
            // already-loaded value. Narrow and exact on purpose: only touches the specific old default,
            // never a value the player deliberately chose afterwards.
            // NUR beim LADEN (2026-08-13): ExposeData laeuft auch beim
            // SPEICHERN, und ungegated hat diese Zeile dort einen frisch getippten Wert wieder
            // ueberschrieben. Das Preset-Gitter benutzt Widgets.TextFieldNumeric - wer dort von Hand 0.35
            // eintippt und die Einstellungen schliesst, dessen Zahl stand korrekt in der Datei und war im
            // Speicher schon wieder 1.0; beim naechsten Schreiben landete die 1.0 dann auch auf der Platte.
            // Genau die Bauform, die der Kommentarblock ueber den entfernten BillDeleteScope-Migrationen
            // als "sie waren also selbst der Bug" beschreibt - dieselbe Falle, eine Migration weiter.
            if (Scribe.mode == LoadSaveMode.LoadingVars
                && Mathf.Approximately(presetPreferAvailable.count, 0.35f)) presetPreferAvailable.count = 1f;
            // Dieselbe Bauform, gleiche Begruendung, fuer Economy budget 0.35 -> 0 (2026-08-14). Ohne diese
            // Zeile bliebe die Aenderung (Economy setzt guenstige Waffen auf das Minimum) in
            // jedem bestehenden Spiel unsichtbar: in der gespeicherten Einstellungsdatei steht 0.35, und ein
            // geaenderter Werksstandard erreicht nur eine brandneue Datei. Genau wie oben nur beim LADEN
            // und nur auf den exakten alten Standardwert - wer spaeter selbst 0.35 eintippt, behaelt ihn
            // bis zum naechsten Spielstart.
            if (Scribe.mode == LoadSaveMode.LoadingVars
                && Mathf.Approximately(presetEconomy.budget, 0.35f)) presetEconomy.budget = 0f;
            // Dieselbe Bauform fuer Economy diversity 0.7 -> 0.15 (2026-08-19, zweiter Befund: Economy
            // produzierte immer noch extrem viel) - Begruendung bei
            // DefaultPresetEconomy. Nur beim LADEN und nur auf den exakten alten Standardwert; wer selbst
            // 0.7 eingetragen hat, behaelt ihn.
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
            // "Created loadouts" gibt es seit 2026-09-07 nicht mehr (siehe AmmoBillFilterMode).
            if (defAmmoBillFilter == AmmoBillFilterMode.AvailableLoadouts) defAmmoBillFilter = AmmoBillFilterMode.AssignedLoadouts;

            Scribe_Values.Look(ref autoAssignLoadoutFilter, "autoAssignLoadoutFilter", AssignLoadoutFilter.AvailableMainWeapon);
            Scribe_Values.Look(ref autoAssignRoleMode, "autoAssignRoleMode", RoleAssignMode.Mixed);
            // Migration vom alten bool (2026-08-19). Ohne sie faellt jeder, der "Roles only" angehakt
            // hatte, stillschweigend auf "gemischt" zurueck - eine Verhaltensaenderung, die niemand
            // angeordnet hat. Nur beim LADEN, und nur wenn der alte Knoten ueberhaupt in der Datei
            // steht: Scribe_Values schreibt einen Vorgabewert nicht mit, ein nie angefasstes "false"
            // gibt es dort also gar nicht.
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
