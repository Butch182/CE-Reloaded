using System;
using System.Collections.Generic;
using System.Linq;
using RimWorld;
using Verse;
using UnityEngine;
using CombatExtended;

namespace CEReloaded
{
    // The unified "make these loadouts real" popup. Replaces
    // Dialog_ProduceMissingWeapons and the old reactive Generate-time "resolve generic ammo" popup with
    // ONE dialog covering four independent sections - Main weapons / Sidearms & grenades / Shields / Ammunition -
    // each hidden entirely when empty, so it works standalone for any subset (e.g. ammo-only when called from the
    // Assign flow's new ammo-gap check). Generic ammo is auto-resolved to the player's default firearm ammo
    // preference instead of forcing a review step (plan 5.8); a small "Change..." link reopens the choice per row.
    public class Dialog_SupplyLoadouts : Window
    {
        // What to supply. Any group may be empty/null.
        public class Request
        {
            public Dictionary<ThingDef, int> mainWeapons = new Dictionary<ThingDef, int>();
            public Dictionary<ThingDef, int> secondaryWeapons;
            public Dictionary<ThingDef, int> shields;
            // Equipment Pack contents (medicine/food/drugs/utility) not yet covered - see
            // LoadoutUtility.ScanMissingAssignedPackItems. Already resolved to concrete ThingDefs (a bill
            // always targets one recipe, so any generic slot's family is picked down to one member upstream).
            public Dictionary<ThingDef, int> packItems;
            // For each of these concrete defs, the def the DEMAND is attached to. For "Any Medicine" that is the
            // generic def, while the key above is the production target - the availability bar must ask the
            // demand def, otherwise it answers a different question than the Equipment Packs tab and reads as
            // fully covered at 600 required / 75 available. Without an entry the concrete def applies.
            public Dictionary<ThingDef, Def> packItemDemandDefs;

            // ============ Is `count` already net of running bills? ============
            //
            // Observed: a double subtraction. 7 carriers, 3 in production, 0 in stock - the availability
            // tooltip of the same row correctly says "Still missing: 4", the quantity field suggests 1.
            // 7 minus 3 minus 3.
            //
            // The cause is not a miscalculation but an unstated assumption at the seam: the number in
            // mainWeapons/secondaryWeapons means something different depending on who built it.
            //   ScanMissingAssignedWeapons already subtracts running bills (so the "did I forget to produce
            //     something" check doesn't report what has long been queued).
            //   ComputeMissingSplit and ComputeMissingForCreatedLoadouts do NOT.
            // BuildWeaponEntry then subtracted again - right for the second kind, double for the first. The
            // pack row next to it explicitly skips the second subtraction and gives exactly this reason; the
            // weapon row never got the same consideration.
            //
            // Instead of changing either calculation (both are right for their purpose), the builder now states
            // what its number means. Default false = "raw demand", i.e. the previous behavior for everyone who
            // doesn't set it.
            public bool weaponCountsNetOfProduction;
            // Ammo: either pre-resolved (Generate flow's dry-run) or computed from assigned pawns' actual loadouts
            // (Assign flow, via LoadoutUtility.ComputeAmmoNeedForAssignedPawns before building this Request).
            public Dictionary<LoadoutGenericDef, int> genericAmmo;
            public Dictionary<ThingDef, int> specificAmmo;
            public Dictionary<ThingDef, Building_WorkTable> ammoBenches;
            // Assig. column, split into two ALWAYS-independent indicators (2026-08-02) - it used to be one
            // dict-set whose MEANING silently flipped between "real pawn" and "loadout template" depending on
            // which scope radio was active (the same colonist could show
            // wildly different "assigned" counts). Now every caller populates both regardless of which scope
            // drives the visible row/count numbers: assignedUsedBy/weaponAssignedUsedBy = real colonists
            // (diamond icon), templateUsedBy/weaponTemplateUsedBy = managed Loadout templates (circle icon).
            public Dictionary<Def, List<string>> assignedUsedBy;   // ammo/generic def -> pawn names
            public Dictionary<Def, List<string>> templateUsedBy;   // ammo/generic def -> loadout labels
            public Dictionary<ThingDef, List<string>> weaponAssignedUsedBy;   // weapon def -> pawn names
            public Dictionary<ThingDef, List<string>> weaponTemplateUsedBy;   // weapon def -> loadout labels
            public Action onConfirm;
            public Action onCancel;
            public Action onBack;   // set when there is a previous step to return to (e.g. the assign preview)
            public ArmReport armReport;   // Arm-my-Colony run: record what actually gets queued on confirm

            // The scope strip "Consider assigned weapons / created loadouts" was removed: since lazy creation
            // there is one template per used weapon, so both scopes were the same set. The window now only
            // works with the assigned weapons.
            // Ammo/weapon bills nobody currently demands - opt-OUT, not opt-in
            // (2026-08-01, by design): every candidate shows checked by default; the player unchecks
            // individual ones they want to keep. Whatever's still checked when Confirm is clicked gets pruned
            // via the matching onPrune* callback, invoked with exactly that final list.
            public List<ThingDef> obsoleteAmmoCandidates;
            public Action<List<ThingDef>> onPruneObsoleteAmmo;
            public List<ThingDef> obsoleteWeaponCandidates;
            public Action<List<ThingDef>> onPruneObsoleteWeapons;
        }

        private class WeaponEntry
        {
            public ThingDef weapon;
            public RecipeDef recipe;
            public bool craftable;
            public string countStr;
            public bool selected;
            public List<Building_WorkTable> benches = new List<Building_WorkTable>();
            public Dictionary<Building_WorkTable, string> benchLabels;
            public Building_WorkTable bench;
            public List<ThingDef> stuffs = new List<ThingDef>();
            public ThingDef stuff;
            public int unfulfilled;
            public List<ThingDefCountClass> ings = new List<ThingDefCountClass>();
            // Split 2026-08-02 - see Request.assignedUsedBy/templateUsedBy's own comment. assignedNames = real
            // colonists currently assigned this weapon (diamond icon); templateNames = managed Loadout
            // templates that include it (circle icon). Always both populated, independent of the scope radio.
            public List<string> assignedNames = new List<string>();
            public List<string> templateNames = new List<string>();
            public int yield = 1;          // units produced per craft (almost always 1 for weapons, kept for symmetry with AmmoEntry)
            // Which SECTION this row came from (set by BuildPackItemEntry), not a property of `weapon` itself -
            // a pack-sourced utility item that also happens to be IsWeapon (binoculars, a Spotter/Tactical/EMP
            // Kit's smoke/flashbang/EMP grenades) still demands via AmmoAssignmentUtility.GetPawnPacks, never
            // GetWeaponInventory. Branching DrawWeaponEntry's availability bar on e.weapon.IsWeapon instead of
            // this used to understate pack-sourced utility weapons' real demand (2026-07-31 fix).
            public bool isPackItem;
            // Only set for pack rows and only meant for the STOCK question - see Request.packItemDemandDefs.
            // Null means "same as weapon".
            public Def demandDef;
        }

        private class AmmoEntry
        {
            public ThingDef resolved;          // the concrete ammo actually produced (generics are resolved eagerly)
            public LoadoutGenericDef sourceGeneric;   // non-null if this entry originated from a generic placeholder
            public List<ThingDef> alternatives = new List<ThingDef>();   // other researched variants of the same family
            public bool selected;
            public string countStr;
            public List<string> assignedNames = new List<string>();
            public List<string> templateNames = new List<string>();
            public RecipeDef recipe;
            public List<ThingDefCountClass> ings = new List<ThingDefCountClass>();
            public List<Building_WorkTable> benches = new List<Building_WorkTable>();   // all candidate benches, built once
            public Building_WorkTable bench;
            public bool alreadyInProduction;
            public int yield = 1;          // units produced per craft (ammo recipes commonly yield >1 per batch)
        }

        private readonly List<WeaponEntry> mainEntries = new List<WeaponEntry>();
        private readonly List<WeaponEntry> secEntries = new List<WeaponEntry>();
        private readonly List<WeaponEntry> shieldEntries = new List<WeaponEntry>();
        private readonly List<WeaponEntry> packEntries = new List<WeaponEntry>();
        private readonly List<AmmoEntry> ammoEntries = new List<AmmoEntry>();
        private readonly Action onConfirm;
        private readonly Action onCancel;
        private readonly Action onBack;
        private readonly Dictionary<ThingDef, Building_WorkTable> presetAmmoBenches;
        private readonly ArmReport armReport;
        private bool confirmed;
        private bool wentBack;
        private Vector2 scroll;
        private bool diffOnly = true;   // "Show only what's missing" (plan 5.9) - on by default, this IS a reactive gap-filling popup

        // Obsolete-cleanup (2026-08-01) - see Request's own field comments.
        private readonly List<ThingDef> obsoleteAmmoCandidates;
        private readonly HashSet<ThingDef> obsoleteAmmoChecked;
        private readonly Action<List<ThingDef>> onPruneObsoleteAmmo;
        private readonly List<ThingDef> obsoleteWeaponCandidates;
        private readonly HashSet<ThingDef> obsoleteWeaponChecked;
        private readonly Action<List<ThingDef>> onPruneObsoleteWeapons;

        private enum SortMode { Name, Count, Assigned }
        private SortMode currentSortMode = SortMode.Name;
        private bool sortDescending = false;

        public override Vector2 InitialSize => new Vector2(Mathf.Min(1192f, UI.screenWidth - 40f), Mathf.Min(760f, UI.screenHeight - 40f));
        // Don't slide under RimWorld's learning readout - see LoadoutUtility.KeepClearOfLearningReadout.
        // Without a collision this changes nothing.
        protected override void SetInitialSizeAndPosition()
        {
            base.SetInitialSizeAndPosition();
            windowRect = LoadoutUtility.KeepClearOfLearningReadout(windowRect);
        }

        // UsedX's own column widened from 32 to 60 (2026-08-02, everything from IngX on shifted +32) to fit
        // TWO indicators side by side (diamond=assigned, circle=used in loadout) instead of one - see
        // DrawAssignmentIndicators.
        // CORRECTED: the sentence that used to be here ("~90px of slack, no overflow risk at the max width
        // 1160") is no longer true: on narrow screens the space is not enough.
        // The max width has long been 1192 (InitialSize one line above), and the row now ends at 1124 - so
        // there is slack only as long as the window really gets its max width.
        // The hard math: right edge 1124 -> required window width 1176 -> required screen width 1216
        // (InitialSize takes screen width minus 40). Below that the workbench button, jump and info icons
        // stick out of the window, and there is no horizontal scrollbar here: they are then not clipped but
        // unreachable. A comment that asserts an outdated number is more dangerous than none at all - the
        // next change would keep calculating from it.
        private const float IconX = 26f, NameX = 52f;
        // ---- Column layout in a narrow window (approach (a) with fallback (b)) ----
        // The row drew at fixed X positions. Below 1216 screen width (or with UI scaling) the workbench, jump
        // and info icons stuck out of view - and since there was no horizontal scrolling they were
        // UNREACHABLE, not just clipped. Now all columns from "Special" onward shift left by colShift, and the
        // name column gives up width until NameMinW is reached (approach a). If even that is not enough, the
        // row stays wider than the view and viewRect is set to the row width - RimWorld's ScrollView then
        // shows a horizontal bar on its own (approach b as fallback): inconvenient, but nothing is
        // unreachable anymore. The header row uses the same values but sits outside the ScrollView - in the
        // fallback it does not scroll along; that is the price of the fallback, not of approach (a).
        // Base values = the previous fixed X positions; a separate layout renderer (SupplyLoadouts_RowsAt)
        // calculates with the same numbers.
        private const float SpecialXBase = 240f, UsedXBase = 320f, IngXBase = 390f, MaterialXBase = 546f;
        private const float BatchXBase = 676f;   // batch-count field, restored 2026-07-30 - paired with CountX, synced both ways
        private const float CountXBase = 768f;   // absolute target count (shifted right from its old 644f slot to make room for BatchX)
        private const float NameMinW = 90f;
        private static float colShift;   // <= 0; set from the view width on every draw pass (UpdateColumnShift)
        private static float SpecialX => SpecialXBase + colShift;
        private static float UsedX => UsedXBase + colShift;
        private static float IngX => IngXBase + colShift;
        private static float MaterialX => MaterialXBase + colShift;
        private static float BatchX => BatchXBase + colShift;
        private static float CountX => CountXBase + colShift;
        private static float ProdX => ProdXBase + colShift;
        private static float BenchX => BenchXBase + colShift;
        private static float JumpX => BenchX + BenchBtnW + 8f;
        private static float InfoX => JumpX + 22f;
        // Right edge of the row without shift (end of Info) and after shift.
        private const float RowRightEdge = BenchXBase + BenchBtnW + 8f + 22f + 18f;   // = 1124
        private static float RowWidth => RowRightEdge + colShift;
        private static void UpdateColumnShift(float viewWidth)
            => colShift = Mathf.Clamp(viewWidth - RowRightEdge, -(SpecialXBase - NameX - 4f - NameMinW), 0f);
        // BenchBtnW 54 -> 74, with the following columns derived from it instead of hand-writing 54 twice. The
        // 20px do NOT come from a neighboring column but from the window width: there was not a millimeter
        // free on the right (Info ended exactly on the list's edge), which is why InitialSize grew by 32.
        // Calculated at 1920: list 1140 wide, right edge 1124 - this holds ONLY from 1216 screen width up,
        // see the math at IconX above.
        private const float ProdXBase = 832f, BenchXBase = 1002f;   // ProdX/BenchX/JumpX/InfoX: see the shift at IconX
        // The workbench column shows either a button (BenchIconButton, 54x24 from y+2) or - when there is no
        // suitable bench at all - a warning sign. That sat left-aligned at the column start and thus visibly
        // off the axis of the buttons in the rows above and below (now centered on the Workbench buttons).
        // Derived once here instead of repeated at three call sites, so they don't drift apart again.
        private const float BenchBtnW = 74f, BenchBtnH = 24f, BenchWarnSize = 18f;
        private static Rect BenchWarnRect(float y) => new Rect(
            BenchX + (BenchBtnW - BenchWarnSize) / 2f, y + 2f + (BenchBtnH - BenchWarnSize) / 2f,
            BenchWarnSize, BenchWarnSize);

        // The block reason belongs IN the row, not just in the tooltip: "recipe available, research done, but
        // no suitable workbench" used to draw only a warning sign at the far right, and anyone not hovering
        // it saw a greyed-out row without any reason - which reads as a bug in the mod.
        // The column is the same one in which the !craftable branch already states its reason, so all block
        // reasons sit on one axis. The WIDTH comes from the two column starts, not from a guessed number, and
        // LabelCell truncates hard to it - this very cell had once been emptied because an untruncated
        // Widgets.Label overflowed it. The full sentence is still in the tooltip, here as there.
        private const float ReasonCellW = ProdXBase - MaterialXBase - 8f;   // both columns shift equally, the width stays
        private static void DrawBlockedReasonCell(float y, string text, string tip)
        {
            Rect r = new Rect(MaterialX, y, ReasonCellW, 24f);
            GUI.color = LoadoutUtility.ColBad;
            LoadoutUtility.LabelCell(r, text);
            GUI.color = Color.white;
            if (!string.IsNullOrEmpty(tip)) TooltipHandler.TipRegion(r, tip);
        }
        private const float RowH = 30f, HeaderH = 26f;

        public Dialog_SupplyLoadouts(Request req) : base(new ReadoutAwareWindowDrawing())
        {
            doCloseX = true;
            forcePause = true;
            absorbInputAroundWindow = true;

            DropObsoleteFromProduceLists(req);
            // ONE change, ONE offer: offering the supply window after a change is good, offering it again on a
            // tab switch is too much.
            // Here rather than at the callers, because EVERY path to the supply window goes through this
            // constructor - a flag per caller would be incomplete again at the next entry point. What happens
            // with the window afterwards (produce or Cancel) changes nothing: it was offered, and asking a
            // second time for the same change is exactly the nagging to avoid. The persistent state is shown in
            // the readiness display anyway.
            LoadoutUtility.lastSupplyOfferSignature = LoadoutUtility.AssignmentSignature();

            WeaponEntry WithNames(WeaponEntry we)
            {
                if (req.weaponAssignedUsedBy != null && req.weaponAssignedUsedBy.TryGetValue(we.weapon, out List<string> an)) we.assignedNames = an;
                if (req.weaponTemplateUsedBy != null && req.weaponTemplateUsedBy.TryGetValue(we.weapon, out List<string> tn)) we.templateNames = tn;
                return we;
            }
            foreach (var kvp in Order(req.mainWeapons)) mainEntries.Add(WithNames(BuildWeaponEntry(kvp.Key, kvp.Value, req.weaponCountsNetOfProduction)));
            if (req.secondaryWeapons != null)
                foreach (var kvp in Order(req.secondaryWeapons)) secEntries.Add(WithNames(BuildWeaponEntry(kvp.Key, kvp.Value, req.weaponCountsNetOfProduction)));
            if (req.shields != null)
                foreach (var kvp in Order(req.shields)) shieldEntries.Add(WithNames(BuildWeaponEntry(kvp.Key, kvp.Value, req.weaponCountsNetOfProduction)));
            // A def that already appears in a weapon/shield section must NOT also appear as a pack item: a shield
            // in an Equipment Pack creates its demand in BOTH request buckets (shieldMissing via
            // ComputeMissingSplit AND missingPackItems via ScanMissingAssignedPackItems - the same pack slots,
            // read twice). GenerateAll and ComputeTotalCost concatenate all four lists, so one demand would
            // become two bills (a pavise in the Shield Kit: 3 missing -> 6 produced).
            var weaponSectionDefs = new HashSet<ThingDef>();
            foreach (WeaponEntry we in mainEntries) weaponSectionDefs.Add(we.weapon);
            foreach (WeaponEntry we in secEntries) weaponSectionDefs.Add(we.weapon);
            foreach (WeaponEntry we in shieldEntries) weaponSectionDefs.Add(we.weapon);
            if (req.packItems != null)
                foreach (var kvp in Order(req.packItems))
                {
                    if (weaponSectionDefs.Contains(kvp.Key)) continue;
                    Def demandDef = null;
                    if (req.packItemDemandDefs != null) req.packItemDemandDefs.TryGetValue(kvp.Key, out demandDef);
                    packEntries.Add(WithNames(BuildPackItemEntry(kvp.Key, kvp.Value, demandDef)));
                }
            BuildAmmoEntries(req);

            onConfirm = req.onConfirm;
            onCancel = req.onCancel;
            onBack = req.onBack;
            presetAmmoBenches = req.ammoBenches;
            armReport = req.armReport;

            obsoleteAmmoCandidates = req.obsoleteAmmoCandidates ?? new List<ThingDef>();
            // Opt-IN, not opt-out (2026-08-02 fix): this popup can be reached as a
            // side effect of an action taken somewhere that has nothing to do with deleting bills on purpose
            // (e.g. Weapon Loadouts' own Apply, which only ever deletes/generates loadout TEMPLATES - never
            // bills, that responsibility was deliberately kept in Supply Loadouts alone) - starting every
            // auto-detected "obsolete" candidate already checked meant a player who wasn't specifically here
            // to prune bills could confirm and delete production they never reviewed. Empty by default now;
            // the player must deliberately tick each one they actually want gone.
            // PRE-CHECKED again: checked, i.e. delete, is the default. The comment above describes the opposite
            // position from earlier - changed deliberately, not by accident. The detection itself remains the
            // weak spot: with pre-checking, a misdetected bill is only one Confirm click away from deletion - if
            // the obsolete detection misclassifies something, it shows up here.
            // Later refined: only what OUR wizard created itself is pre-checked (those are opt-out). A bill set
            // up by hand is still SUGGESTED, but unchecked - it requires a deliberate decision (manually created
            // bills default to OFF, i.e. opt-in).
            // This defuses exactly the case behind the decision: a bill the player had created a minute earlier
            // showed up checked for deletion because no loadout demands it. The suggestion is still correct -
            // only the pre-checked box was the difference between "a hint" and "one click away from data loss".
            obsoleteAmmoChecked = new HashSet<ThingDef>(obsoleteAmmoCandidates.Where(LoadoutUtility.AllBillsForDefAreOurs));
            onPruneObsoleteAmmo = req.onPruneObsoleteAmmo;
            obsoleteWeaponCandidates = req.obsoleteWeaponCandidates ?? new List<ThingDef>();
            obsoleteWeaponChecked = new HashSet<ThingDef>(obsoleteWeaponCandidates.Where(LoadoutUtility.AllBillsForDefAreOurs));
            onPruneObsoleteWeapons = req.onPruneObsoleteWeapons;
        }

        public override void PostClose()
        {
            base.PostClose();
            // Back is navigation to the previous step, not a cancel - nothing here has been applied yet (onConfirm
            // only fires from the button below), so there is nothing to undo.
            if (wentBack) return;
            if (!confirmed) onCancel?.Invoke();
        }

        private static IEnumerable<KeyValuePair<ThingDef, int>> Order(Dictionary<ThingDef, int> d)
        {
            if (d == null) yield break;
            foreach (var kv in d.OrderByDescending(k => k.Value).ThenBy(k => k.Key.label)) yield return kv;
        }

        // ================================================================================================
        // Weapons / Shields (identical structure - a shield is just a ThingDef with a recipe like any weapon)
        // ================================================================================================

        private static int ComputeUnfulfilled(ThingDef w)
        {
            int n = 0;
            foreach (Pawn p in LoadoutUtility.AllColonyPawns())
            {
                Loadout lo = p.GetLoadout();
                if (lo == null || lo.defaultLoadout || lo.Slots == null || !lo.Slots.Any(s => s.thingDef == w)) continue;
                if (!LoadoutUtility.PawnAlreadyHasItem(p, w)) n++;
            }
            return n;
        }

        // netOfProduction: see Request.weaponCountsNetOfProduction - true means "the number is already net of
        // running bills, don't subtract them a second time".
        private static WeaponEntry BuildWeaponEntry(ThingDef weapon, int count, bool netOfProduction = false)
        {
            WeaponEntry e = new WeaponEntry { weapon = weapon, countStr = count.ToString() };
            e.recipe = LoadoutUtility.GetRecipeFor(weapon);
            e.craftable = e.recipe != null && LoadoutUtility.RecipeIsResearched(e.recipe);
            e.unfulfilled = ComputeUnfulfilled(weapon);
            e.ings = LoadoutUtility.GetRecipeIngredients(e.recipe);

            if (e.craftable)
            {
                // The bench list comes from the RECIPE that actually gets queued - not from the product (crash:
                // "satchel charge at the ammo bench"). GetWorkbenchesFor(weapon) unites the benches of ALL recipes
                // that make this item; the bill is then created with e.recipe. For an item with several recipes
                // this could yield a bench that doesn't offer exactly this recipe - and RimWorld's bill stack
                // accepts that without complaint.
                e.benches = LoadoutUtility.BenchesForRecipe(e.recipe);
                if (CEReloadedMod.settings != null && CEReloadedMod.settings.hideDuplicateBenches)
                {
                    HashSet<ThingDef> seenDefs = new HashSet<ThingDef>();
                    e.benches = e.benches.Where(b => seenDefs.Add(b.def)).ToList();
                }
                e.benchLabels = LoadoutUtility.BuildBenchLabels(e.benches);
                e.bench = e.benches.FirstOrDefault();

                if (weapon.MadeFromStuff)
                {
                    e.stuffs = GenStuff.AllowedStuffsFor(weapon).ToList();
                    e.stuff = e.stuffs.FirstOrDefault(s => s == ThingDefOf.Steel) ?? e.stuffs.FirstOrDefault();
                    e.ings = LoadoutUtility.GetRecipeIngredients(e.recipe, e.stuff);
                }
            }

            e.selected = e.craftable && e.bench != null;

            if (e.craftable && !netOfProduction
                && (CEReloadedMod.settings == null || CEReloadedMod.settings.claimOpenWeaponProduction))
            {
                int queued = QueuedCount(e.recipe);
                if (queued > 0)
                {
                    int remaining = Mathf.Max(0, count - queued);
                    e.countStr = remaining.ToString();
                    if (remaining == 0) e.selected = false;
                }
            }

            if (e.craftable && e.recipe != null && e.recipe.products != null && e.recipe.products.Count > 0)
                e.yield = Mathf.Max(1, e.recipe.products[0].count);
            return e;
        }

        // Equipment Pack item (medicine/food/drug/utility) row - same shape as BuildWeaponEntry (reused
        // rather than a parallel class, since recipe/bench/stuff/ingredients logic doesn't care whether the
        // ThingDef is a weapon), but WITHOUT the two weapon-loadout-specific pieces: ComputeUnfulfilled reads
        // real Weapon Loadout slots (meaningless for a pack item's demand, which lives in
        // AmmoAssignmentUtility.GetPawnPacks instead - already resolved into `count` by the caller), and the
        // "claimOpenWeaponProduction" already-queued reduction (the caller's shortfall already accounts for
        // in-production stock via LoadoutUtility.ComputeItemOwnership, so subtracting queued count again here
        // would double-count it).

        // ====== ONE calculation for a row's availability bar ======
        // It used to exist only for the normal case; the row's two early exits (no recipe, no workbench) built
        // their own bar - from GetWeaponInventory, which naturally returns zero for pack contents. Observed:
        // without a drug lab the medicine row said Total 0 / Available 0 / everything 0, while the Equipment
        // Packs tab at the same moment showed Total 77 / Assigned in storage 67 / Assigned & carried 8.
        // The same class of bug as the original one - only the main branch had been fixed, not the two exits
        // next to it.
        // Whether a workbench exists doesn't change HOW MUCH the colony has. It only changes whether something
        // can be produced - and the row says that in its own words anyway.
        private static LoadoutUtility.AvailabilitySegments RowAvailability(WeaponEntry e, int inProd)
        {
            if (!e.isPackItem)
            {
                // Do NOT switch to LoadoutUtility.WeaponAvailability (four other copies of this calculation were
                // consolidated there): the `inProd` above only counts bills for THIS ROW'S RECIPE (QueuedFromBills
                // via e.recipe), while WeaponQueuedCount takes every bill that makes the weapon in any way. With
                // several recipes for the same weapon those are different numbers, and this row MEANS its own
                // recipe - it sits next to that recipe's workbench and materials.
                LoadoutUtility.GetWeaponInventory(e.weapon, out int wTotal, out int wCarried, out int wStored, out int wAssigned);
                int wCarriedAndAssigned = Mathf.Min(wCarried, wAssigned);
                int wCarriedOnly = wCarried - wCarriedAndAssigned;
                int wAssignedInStore = Mathf.Min(wStored, Mathf.Max(0, wAssigned - wCarried));
                int wFreeStored = wStored - wAssignedInStore;
                // neededCount must be TOTAL DEMAND (how many pawns' loadouts want this weapon, period -
                // wAssigned already is exactly that), not existing-stock-plus-demand: the old formula
                // (wTotal + inProd + e.unfulfilled) added the already-owned stock a second time on top of
                // e.unfulfilled (itself already a full "how many still need one" count), inflating "Still
                // missing" by however many were already sitting in storage/carried.
                return LoadoutUtility.BuildAvailabilitySegments(wCarriedOnly, wCarriedAndAssigned, wAssignedInStore, wFreeStored, inProd,
                    def: e.weapon, neededCount: wAssigned);
            }
            else
            {
                // Pack item (medicine/food/drug/utility) - ownership comes from AmmoAssignmentUtility.
                // GetPawnPacks demand, not a Weapon Loadout slot, and there's no "equipped" concept either
                // (never worn/wielded) - GetWeaponInventory would just read zero/wrong numbers here.
                // Stock ALWAYS against the demand def - `e.weapon` is the production target, and asking with it
                // returns "nothing assigned, everything free" for a generically demanded pack item:
                // ComputeItemOwnership only counts explicitly named pack entries as demand for a concrete def.
                // Measured: the same stock showed in the Equipment Packs tab as "Assigned, in storage: 75 /
                // Available: 0", and here as "Assigned: 0 / Available: 75" - with 600 medicine required.
                Def availDef = e.demandDef ?? e.weapon;
                LoadoutUtility.ComputeItemOwnership(availDef, out int pCarriedOnly, out int pCarriedAndAssigned, out int pAssignedInStore, out int pFreeStored, out _);
                // neededCount as in the weapon branch above: it was the only one missing here, and without it the
                // bar can't have a red shortfall segment at all - a row that only EXISTS because something is
                // missing showed up fully green. The number is the same the weapon branch uses: total demand, not
                // the shortfall (that is already in seg.missing).
                return LoadoutUtility.BuildAvailabilitySegments(pCarriedOnly, pCarriedAndAssigned, pAssignedInStore, pFreeStored, inProd,
                    def: availDef, neededCount: LoadoutUtility.ColonyWideAssignedNeed(availDef));
            }
        }
        // ====== The same for the AMMO rows ======
        // They built their segments by hand and passed a fixed 0 for both carried buckets. Result: the row
        // '.45 Colt (FMJ)' said 'Total: 0' at the top and listed 'Carried by: <colonist> x1' right below it.
        // Two contradicting statements in ONE tooltip - the location tooltip reads the inventory, the
        // numbers above it didn't.
        // It was not a loaded magazine, it was the hard-wired zeros.
        // LoadoutUtility.GetAmmoOwnership returns exactly the five-way breakdown that the Assign tab, the ammo
        // window and the bills tab have long been using - this window was the only outlier.
        // neededCount stays, because only then does the bar have its red shortfall segment: total demand =
        // what is already carried AND assigned, plus what still has to come from storage.
        private static LoadoutUtility.AvailabilitySegments AmmoRowAvailability(ThingDef ammoDef)
        {
            LoadoutUtility.GetAmmoOwnership(ammoDef, out int co, out int ca, out int ais, out int fs, out int ip);
            LoadoutUtility.ComputeAmmoAvailability(ammoDef, out _, out int demandedFromStorage);
            return LoadoutUtility.BuildAvailabilitySegments(co, ca, ais, fs, ip,
                def: ammoDef, neededCount: ca + demandedFromStorage);
        }

        // The "where is it / who carries it / who is it assigned to" breakdown of an item row.
        // A function of its own so the three callers don't answer the question three different ways - the
        // blocked branches (no recipe, no workbench) didn't have it at all, the normal one had it inline. The
        // production section is still only added in the normal branch: where nothing can be produced, nothing
        // is in production either.
        private static string RowLocationTip(WeaponEntry e)
        {
            return e.isPackItem
                ? LoadoutUtility.BuildPackItemLocationTooltip(e.demandDef ?? e.weapon)
                : LoadoutUtility.BuildWeaponLocationTooltip(e.weapon);
        }

        private static WeaponEntry BuildPackItemEntry(ThingDef item, int count, Def demandDef = null)
        {
            WeaponEntry e = new WeaponEntry { weapon = item, countStr = count.ToString(), isPackItem = true, demandDef = demandDef };
            e.recipe = LoadoutUtility.GetRecipeFor(item);
            e.craftable = e.recipe != null && LoadoutUtility.RecipeIsResearched(e.recipe);
            e.ings = LoadoutUtility.GetRecipeIngredients(e.recipe);

            if (e.craftable)
            {
                // Twin of the weapon row above - same source, same reasoning.
                e.benches = LoadoutUtility.BenchesForRecipe(e.recipe);
                if (CEReloadedMod.settings != null && CEReloadedMod.settings.hideDuplicateBenches)
                {
                    HashSet<ThingDef> seenDefs = new HashSet<ThingDef>();
                    e.benches = e.benches.Where(b => seenDefs.Add(b.def)).ToList();
                }
                e.benchLabels = LoadoutUtility.BuildBenchLabels(e.benches);
                e.bench = e.benches.FirstOrDefault();

                if (item.MadeFromStuff)
                {
                    e.stuffs = GenStuff.AllowedStuffsFor(item).ToList();
                    e.stuff = e.stuffs.FirstOrDefault(s => s == ThingDefOf.Steel) ?? e.stuffs.FirstOrDefault();
                    e.ings = LoadoutUtility.GetRecipeIngredients(e.recipe, e.stuff);
                }
            }

            e.selected = e.craftable && e.bench != null;

            if (e.craftable && e.recipe != null && e.recipe.products != null && e.recipe.products.Count > 0)
                e.yield = Mathf.Max(1, e.recipe.products[0].count);
            return e;
        }

        // Via LoadoutUtility.QueuedFromBills, no longer with its own formula: the old, too optimistic calculation
        // used to be here, and its result feeds `remaining = count - queued` - a row was deselected and set to
        // 0 even though the bill doesn't cover the gap at all.
        private static int QueuedCount(RecipeDef recipe)
        {
            if (recipe == null) return 0;
            List<Bill_Production> bills = new List<Bill_Production>();
            foreach (Building_WorkTable b in LoadoutUtility.AllColonyBenches())
            {
                if (b.billStack == null) continue;
                foreach (Bill_Production bill in b.billStack.Bills.OfType<Bill_Production>())
                    if (bill.recipe == recipe) bills.Add(bill);
            }
            return LoadoutUtility.QueuedFromBills(bills, recipe.products != null && recipe.products.Count > 0 ? recipe.products[0].thingDef : null);
        }

        // ================================================================================================
        // Ammo (generic auto-resolved eagerly; a per-row link lets the player switch to another family member)
        // ================================================================================================

        // A def must never be in a PRODUCE list and a CLEANUP list of the same window at the same time (how can
        // something be listed as obsolete and as to-be-produced at once - isn't that a fundamental bug?). Yes -
        // and exactly the class of bug this mod produces most often: two independent calculations of the same
        // question that don't know about each other.
        //
        // Why the CLEANUP side wins and not the produce side: "superfluous" means the demand calculation across
        // all colonists or all templates finds NOTHING anymore that asks for the thing. The produce number, by
        // contrast, can be a stale value from the ammo tab's row - in the observed case that is exactly what
        // happened: the sidearm had just been removed, the demand was gone, the stack count in the row was
        // still there. Producing something that the same window lists as "nobody needs this" can never be
        // right.
        //
        // HERE and not at the callers: there are five places that assemble such a request
        // (AttachObsoleteCleanup and the four callers next to it), and one of them deliberately builds its
        // lists itself. A check per caller would be silently incomplete at the next new entry point - the same
        // trap an earlier fix fell into (it sat in ONE of the two candidate lists; the weapon half next to it
        // still contradicted the ammo row).
        //
        // Generic ammo needs none of this: UnneededAmmoBillCandidates removes via CollectGenericAmmoFamilies
        // everything a generic slot covers - so a generically demanded def never ends up in the cleanup list.
        private static void DropObsoleteFromProduceLists(Request req)
        {
            bool anyAmmo = req.obsoleteAmmoCandidates != null && req.obsoleteAmmoCandidates.Count > 0;
            bool anyWeapon = req.obsoleteWeaponCandidates != null && req.obsoleteWeaponCandidates.Count > 0;
            if (!anyAmmo && !anyWeapon) return;

            HashSet<ThingDef> obsolete = new HashSet<ThingDef>();
            if (anyAmmo) foreach (ThingDef d in req.obsoleteAmmoCandidates) if (d != null) obsolete.Add(d);
            if (anyWeapon) foreach (ThingDef d in req.obsoleteWeaponCandidates) if (d != null) obsolete.Add(d);

            // DIRECTION REVERSED (the new window was missing items, e.g. the satchel charge - the demand was not
            // calculated correctly).
            //
            // The first version decided here that "superfluous" wins and the item drops out of the produce list.
            // That reasoning still holds for the case it came from (weapon just removed, demand really gone) -
            // but it has a side effect that weighs more than the contradiction itself: when the item is in BOTH
            // lists because two demand calculations disagree, a REAL demand silently vanished from the window.
            //
            // The two outcomes are not equally bad:
            //   hide demand        -> the player produces too little and doesn't see why. Silent.
            //   leave a bill alone -> one bill too many, visible in the ammo tab, one click away.
            // So the produce side now wins, and the item drops out of the CLEANUP list. The rule "never in both
            // lists" stays unchanged - only the side that gives way changed.
            //
            // The actual cause is NOT fixed by this: two calculations disagree on throwables (the weapon demand
            // calculation overlooks throwing weapons). That is where it really needs repairing.
            StripObsolete(req.obsoleteAmmoCandidates, req);
            StripObsolete(req.obsoleteWeaponCandidates, req);
        }

        // Removes from a cleanup list everything this window wants to PRODUCE at the same time.
        // The reverse of the first version - the reasoning is above.
        private static void StripObsolete(List<ThingDef> candidates, Request req)
        {
            if (candidates == null || candidates.Count == 0) return;
            for (int i = candidates.Count - 1; i >= 0; i--)
            {
                ThingDef d = candidates[i];
                if (d == null) continue;
                if (Wants(req.specificAmmo, d) || Wants(req.mainWeapons, d) || Wants(req.secondaryWeapons, d)
                    || Wants(req.shields, d) || Wants(req.packItems, d))
                    candidates.RemoveAt(i);
            }
        }

        private static bool Wants(Dictionary<ThingDef, int> produce, ThingDef d)
        {
            return produce != null && produce.TryGetValue(d, out int n) && n > 0;
        }

        private void BuildAmmoEntries(Request req)
        {
            // resolved ammo def -> aggregated entry. Generic and specific requests both fold into this so the same
            // concrete ammo requested two ways (a generic that resolves to FMJ, plus an explicit FMJ request) merges
            // into one row instead of two.
            Dictionary<ThingDef, AmmoEntry> byResolved = new Dictionary<ThingDef, AmmoEntry>();

            AmmoEntry GetOrCreate(ThingDef resolved, LoadoutGenericDef fromGeneric)
            {
                if (!byResolved.TryGetValue(resolved, out AmmoEntry e))
                {
                    e = new AmmoEntry { resolved = resolved, sourceGeneric = fromGeneric };
                    e.recipe = LoadoutUtility.GetRecipeFor(resolved);
                    e.ings = e.recipe != null ? LoadoutUtility.GetRecipeIngredients(e.recipe) : new List<ThingDefCountClass>();
                    e.yield = Mathf.Max(1, LoadoutUtility.GetAmmoRecipeYield(resolved));
                    e.alreadyInProduction = LoadoutUtility.WeaponHasActiveBill(resolved);
                    e.selected = LoadoutUtility.IsAmmoResearched(resolved) && !e.alreadyInProduction;
                    e.countStr = "0";
                    e.benches = AmmoBenchesFor(e.recipe);
                    if (req.ammoBenches != null && req.ammoBenches.TryGetValue(resolved, out Building_WorkTable presetBench))
                        e.bench = presetBench;
                    else
                        e.bench = e.benches.FirstOrDefault();
                    byResolved[resolved] = e;
                }
                else if (fromGeneric != null) e.sourceGeneric = e.sourceGeneric ?? fromGeneric;
                return e;
            }

            void AddCount(AmmoEntry e, int n) { int.TryParse(e.countStr, out int c); e.countStr = (c + n).ToString(); }

            if (req.genericAmmo != null)
            {
                foreach (var kvp in req.genericAmmo)
                {
                    ThingDef gun = LoadoutUtility.GetWeaponForGenericAmmo(kvp.Key);
                    List<Def> family = LoadoutUtility.GetAmmoTypesFor(gun).Cast<Def>().ToList();
                    List<ThingDef> researchedFamily = family.OfType<ThingDef>().Where(a => LoadoutUtility.IsAmmoResearched(a)).ToList();
                    if (researchedFamily.Count == 0) continue;
                    // Auto-resolve (plan 5.8): pick the player's default firearm ammo preference silently, no forced
                    // review popup - the row below still lets them switch via "Change...".
                    Def picked = LoadoutUtility.PickDefaultAmmo(researchedFamily.Cast<Def>().ToList());
                    if (!(picked is ThingDef resolved)) continue;
                    AmmoEntry e = GetOrCreate(resolved, kvp.Key);
                    e.alternatives = researchedFamily.Where(a => a != resolved).ToList();
                    AddCount(e, kvp.Value);
                }
            }
            if (req.specificAmmo != null)
            {
                foreach (var kvp in req.specificAmmo)
                {
                    // Unresearched ammo used to be silently skipped entirely here - a colonist manually given
                    // an ammo type that can never be produced would then vanish from this popup with no trace,
                    // reading as "nothing missing" when it very much was. Still created (DrawAmmoEntry shows a
                    // "can't be produced" row for it, same treatment DrawWeaponEntry already gives weapons),
                    // just never selectable/counted toward a bill.
                    AmmoEntry e = GetOrCreate(kvp.Key, null);
                    AddCount(e, kvp.Value);
                }
            }

            // Anyone on "Any ammo for X" appears in these two books ONLY under the GENERIC def - never under a
            // concrete type (ComputeAmmoNeedForLoadouts files the slot the way it is in the loadout). It used to
            // be found here only via `e.sourceGeneric`, and that field is set exactly when the row came from
            // `req.genericAmmo`. Two of the four entry points into this window don't fill `genericAmmo` at all
            // (Bill Overview -> Auto-generate only passes `specificAmmo`, the demand number is already resolved
            // there) - there sourceGeneric was null, the lookup only went through the concrete def, and the
            // diamond fell to 0 with the tooltip "no colonist has a loadout that uses this ammo", although two
            // colonists carried the matching weapon. Serious, because the diamond answers the question "does
            // anyone still need this" and its answer feeds into resupply planning.
            // Resolved to the whole FAMILY, not the one default type: that is the same answer to the same
            // question CollectGenericAmmoFamilies gives for the obsolete test (see its comment - "is this still
            // needed" must not be confused with "how much should I produce"). Otherwise the two contradict each
            // other in the same window: a row counts as not superfluous because a generic slot covers it, and
            // the diamond next to it says nobody needs it.
            Dictionary<ThingDef, List<string>> ExpandGenerics(Dictionary<Def, List<string>> map)
            {
                var byVariant = new Dictionary<ThingDef, List<string>>();
                if (map == null) return byVariant;
                foreach (var kv in map)
                {
                    if (!(kv.Key is LoadoutGenericDef gd) || kv.Value == null) continue;
                    HashSet<ThingDef> family = new HashSet<ThingDef>();
                    LoadoutUtility.CollectGenericAmmoFamilies(new List<LoadoutGenericDef> { gd }, family);
                    foreach (ThingDef variant in family)
                    {
                        if (!byVariant.TryGetValue(variant, out List<string> list)) byVariant[variant] = list = new List<string>();
                        foreach (string n in kv.Value) if (!list.Contains(n)) list.Add(n);
                    }
                }
                return byVariant;
            }

            void FillNames(Dictionary<Def, List<string>> map, Action<AmmoEntry, List<string>> assign)
            {
                if (map == null) return;
                Dictionary<ThingDef, List<string>> genericByVariant = ExpandGenerics(map);
                foreach (AmmoEntry e in byResolved.Values)
                {
                    List<string> names = new List<string>();
                    if (e.sourceGeneric != null && map.TryGetValue(e.sourceGeneric, out List<string> gNames)) names.AddRange(gNames);
                    if (map.TryGetValue(e.resolved, out List<string> sNames)) names.AddRange(sNames);
                    if (genericByVariant.TryGetValue(e.resolved, out List<string> fNames)) names.AddRange(fNames);
                    assign(e, names.Distinct().ToList());
                }
            }

            FillNames(req.assignedUsedBy, (e, names) => e.assignedNames = names);
            FillNames(req.templateUsedBy, (e, names) => e.templateNames = names);

            // ============ THROWABLES GET THEIR NAMES FROM THE WEAPON SIDE ============
            //
            // The fourth occurrence of the same question, this time with two contradicting tooltips in the SAME
            // window: the satchel charge's Assig. column said "No colonist is assigned a loadout that uses this
            // ammo", while the availability tooltip of the same row listed "Assigned to: <three colonists>". Two
            // of them really carried it as a sidearm.
            //
            // The cause is not a miscalculation but a gap in responsibility. A throwable IS technically ammo
            // (AmmoThing) and therefore lands in the ammo LIST - but its demand is explicitly calculated by the
            // WEAPON SIDE (ComputeAmmoNeedForLoadouts skips IsSelfAmmoWeapon by design, for good reason:
            // otherwise the same item gets booked twice in two systems). So the row asked one calculation while
            // the other was responsible - and reliably got "nobody" back.
            //
            // So it now gets its names from where its demand is really calculated. Everything else in the list
            // stays with the ammo side unchanged.
            // It happened again later: a colonist had a satchel charge assigned, but the Assigned column said no
            // loadout uses it and it is assigned to nobody - both wrong. And the previous fix was not incomplete
            // but INEFFECTIVE: the loop ran over `ammoEntries` - and that list is only filled at the end of this
            // method (AddRange from byResolved.Values, about 30 lines further down). It was ALWAYS empty here, so
            // the body never ran once. Hence now the same source as the line above: byResolved.Values, the
            // collection that really exists at this point.
            // Merged instead of replaced: the ammo side does skip throwables by design (ComputeAmmoNeedForLoadouts
            // skips IsSelfAmmoWeapon, otherwise the same item gets booked twice), but replacing would lose a name
            // as soon as that ever changes.
            if (req.weaponAssignedUsedBy != null || req.weaponTemplateUsedBy != null)
            {
                foreach (AmmoEntry e in byResolved.Values)
                {
                    if (e?.resolved == null || !LoadoutUtility.IsSelfAmmoWeapon(e.resolved)) continue;
                    if (req.weaponAssignedUsedBy != null
                        && req.weaponAssignedUsedBy.TryGetValue(e.resolved, out List<string> wa) && wa != null)
                        e.assignedNames = e.assignedNames.Concat(wa).Distinct().ToList();
                    if (req.weaponTemplateUsedBy != null
                        && req.weaponTemplateUsedBy.TryGetValue(e.resolved, out List<string> wt) && wt != null)
                        e.templateNames = e.templateNames.Concat(wt).Distinct().ToList();
                }
            }

            // "Is the existing bill enough?" instead of "is there any?".
            // Must be HERE and not in GetOrCreate: there the demand is still 0, it is only summed up by the
            // AddCount loops above. Previously an old bill for 100 rounds was enough for a row with 500 demand to
            // count as done - and because "show only what's missing" is on by default, the row then disappeared
            // entirely and the supply popup might not open at all. Mid-game the mod thus reported "nothing
            // missing" while people ran dry in combat.
            // Compared against the bill's TARGET, not its remaining amount: countStr is a stock target here
            // ("produce until X"), not a shortfall - so the number stays untouched and only the "already
            // covered" flag becomes honest. WEAPONS have had the same quantity-exact calculation twelve lines
            // further up all along; ammo never got it.
            foreach (AmmoEntry e in byResolved.Values)
            {
                if (!int.TryParse(e.countStr, out int needed) || needed <= 0) continue;
                int existingTarget = LoadoutUtility.HighestBillTargetFor(e.resolved);
                e.alreadyInProduction = existingTarget >= needed;
                e.selected = LoadoutUtility.IsAmmoResearched(e.resolved) && !e.alreadyInProduction;
            }

            ammoEntries.AddRange(byResolved.Values.OrderByDescending(e => int.TryParse(e.countStr, out int c) ? c : 0).ThenBy(e => e.resolved.label));
        }

        // Switch a resolved ammo row to a different family member (from the "Change..." link) - rebuilds the entry
        // in place, field-by-field exactly like a fresh GetOrCreate, so ingredients/bench/production status AND the
        // selected checkbox all stay correct for the new choice (a family can mix already-in-production and not-yet
        // members, so leaving `selected` at the old choice's value would silently un-flag a genuinely needed bill).
        private void SwitchAmmoChoice(AmmoEntry e, ThingDef newChoice)
        {
            List<ThingDef> family = new List<ThingDef>(e.alternatives) { e.resolved };
            e.alternatives = family.Where(a => a != newChoice).ToList();
            e.resolved = newChoice;
            e.recipe = LoadoutUtility.GetRecipeFor(newChoice);
            e.ings = e.recipe != null ? LoadoutUtility.GetRecipeIngredients(e.recipe) : new List<ThingDefCountClass>();
            // Quantity-exact as when building the rows - otherwise switching the type gets the new one the old
            // "any bill is enough" verdict again.
            int switchNeeded = int.TryParse(e.countStr, out int sn) ? sn : 0;
            e.alreadyInProduction = switchNeeded > 0
                ? LoadoutUtility.HighestBillTargetFor(newChoice) >= switchNeeded
                : LoadoutUtility.WeaponHasActiveBill(newChoice);
            e.selected = LoadoutUtility.IsAmmoResearched(newChoice) && !e.alreadyInProduction;
            e.benches = AmmoBenchesFor(e.recipe);
            if (presetAmmoBenches != null && presetAmmoBenches.TryGetValue(newChoice, out Building_WorkTable presetBench))
                e.bench = presetBench;
            else
                e.bench = e.benches.FirstOrDefault();
        }

        // All benches whose def is a recipeUser of this ammo recipe, in stable id order. Built once per entry
        // (and again on a variant switch) - the draw loop reads the cached list every frame.
        private static List<Building_WorkTable> AmmoBenchesFor(RecipeDef recipe)
            // Via the shared version: a second copy of the same formula used to be here, and it only knew the
            // recipeUsers path - a recipe attached via the bench's recipes list was invisible to it. See
            // LoadoutUtility.BenchCanRun.
            => LoadoutUtility.BenchesForRecipe(recipe);

        // ================================================================================================
        // UI
        // ================================================================================================

        // True when, with the missing-only filter applied, at least one row would actually be visible - i.e. there
        // really is something to supply. Callers check this BEFORE opening the window, so the popup never appears
        // just to say "everything is already covered" (its own diffOnly filter would otherwise hide every row the
        // caller's raw need-count promised).
        public bool AnythingToSupply =>
            AnythingToProduce
            || obsoleteAmmoCandidates.Count > 0 || obsoleteWeaponCandidates.Count > 0;

        // The same WITHOUT the cleanup list. The difference is not a detail: "there is something to do here" and
        // "something is missing" are two different statements, and whoever is sent here from a WARNING means
        // the second one. Observed: the window opened by the "Low on ammo" warning contained nothing but a
        // suggestion to cancel explosive bills - good enough for AnythingToSupply, not an answer to "I'm out of
        // ammo".
        // A button like "Clean up bills" may keep using AnythingToSupply; a path that comes from a shortage
        // uses this property instead.
        public bool AnythingToProduce =>
            mainEntries.Concat(secEntries).Concat(shieldEntries).Concat(packEntries).Any(e => !e.craftable || e.countStr != "0")
            || ammoEntries.Any(e => !e.alreadyInProduction);

        // How many ROWS this window shows for production - the same condition as AnythingToProduce, just counted
        // ('Colony problems' reported (4) production gaps, but in the end there was only one). The readiness
        // display counted COLONISTS, the window shows ITEMS - four colonists with the same missing ammo are one
        // row. The button now shows this number.
        public int ProduceRowCount =>
            mainEntries.Concat(secEntries).Concat(shieldEntries).Concat(packEntries).Count(e => !e.craftable || e.countStr != "0")
            + ammoEntries.Count(e => !e.alreadyInProduction);

        public override void DoWindowContents(Rect inRect)
        {
            // Column shift for this view width (viewRect.width = inRect.width - 16, see below) - BEFORE the header
            // row, which reads the same column values.
            UpdateColumnShift(inRect.width - 16f);
            Text.Font = GameFont.Medium;
            Widgets.Label(new Rect(0, 0, inRect.width, 35f), "CER_SupplyLoadouts_Title".Translate());
            Text.Font = GameFont.Small;


            bool anyCovered = mainEntries.Concat(secEntries).Concat(shieldEntries).Any(e => e.craftable && e.countStr == "0")
                || ammoEntries.Any(e => e.alreadyInProduction);
            // Right-aligned next to the title row, clear of the window's close X (which floats over the top-right).
            Rect toggleRect = new Rect(inRect.width - 280f, 6f, 236f, 24f);
            if (anyCovered)
            {
                bool nd = diffOnly;
                Widgets.CheckboxLabeled(toggleRect, "CER_SupplyLoadouts_ShowOnlyMissingCheckbox".Translate(), ref nd);
                if (nd != diffOnly) diffOnly = nd;
            }

            float listY = 36f;
            if (LoadoutUtility.ColumnHeader(new Rect(NameX, listY, SpecialX - NameX - 4f, 24f), "CER_SupplyLoadouts_ColName".Translate(),
                currentSortMode == SortMode.Name, sortDescending, true)) SetSort(SortMode.Name);
            LoadoutUtility.ColumnHeader(new Rect(SpecialX, listY, 80f, 24f), "CER_SupplyLoadouts_ColSpecial".Translate(), false, false, false);
            // Widened to cover both indicators (2026-08-02) - diamond (assigned) + circle (used in loadout),
            // always both shown now instead of one column whose meaning used to flip with the scope radio.
            // Sorting still keys off the diamond/assigned count - the more commonly meaningful of the two.
            Rect assignedHeaderRect = new Rect(UsedX, listY, 60f, 24f);
            if (LoadoutUtility.ColumnHeader(assignedHeaderRect, "CER_SupplyLoadouts_ColAssigned".Translate(),
                currentSortMode == SortMode.Assigned, sortDescending, true)) SetSort(SortMode.Assigned);
            TooltipHandler.TipRegion(assignedHeaderRect, "CER_SupplyLoadouts_ColAssignedTip".Translate());
            LoadoutUtility.ColumnHeader(new Rect(IngX, listY, 150f, 24f), "CER_SupplyLoadouts_ColIngredients".Translate(), false, false, false);
            LoadoutUtility.ColumnHeader(new Rect(MaterialX, listY, 120f, 24f), "CER_SupplyLoadouts_ColMaterial".Translate(), false, false, false);
            LoadoutUtility.ColumnHeader(new Rect(BatchX, listY, CountX - BatchX - 4f, 24f), "CER_SupplyLoadouts_ColBatches".Translate(), false, false, false);
            // "Stock", no longer "Count": the same word meant "rounds ONE colonist carries" in the loadout editor
            // and "stock target for the whole colony" here - a factor of eight with eight people, and nothing on
            // screen said which of the two was meant. The column is only 50px wide (CountX 768 to ProdX 832), so a
            // short word plus tooltip instead of a long header.
            Rect countHeaderRect = new Rect(CountX, listY, 50f, 24f);
            if (LoadoutUtility.ColumnHeader(countHeaderRect, "CER_SupplyLoadouts_ColCount".Translate(),
                currentSortMode == SortMode.Count, sortDescending, true)) SetSort(SortMode.Count);
            TooltipHandler.TipRegion(countHeaderRect, "CER_SupplyLoadouts_ColCountTip".Translate());
            LoadoutUtility.ColumnHeader(new Rect(ProdX, listY, 155f, 24f), "CER_SupplyLoadouts_ColAvailability".Translate(), false, false, false);
            // Up to the row's right edge instead of a fixed 130: 130 already stuck out 8 px past the info icon at
            // full width, and past the view at any shift (found with the layout renderer).
            LoadoutUtility.ColumnHeader(new Rect(BenchX, listY, InfoX + 18f - BenchX, 24f), "CER_SupplyLoadouts_ColWorkbench".Translate(), false, false, false);
            listY += 28f;

            // Visible gap between the list and 'Total cost' (in a large colony the last, half-clipped row read as
            // if covered by the footer). The list ends at a divider line, the cost sits 12 points below it - a
            // clipped row thus reads as 'continues'.
            Rect outRect = new Rect(0, listY, inRect.width, inRect.height - listY - 50f - 36f);
            // Fallback (b): if the row is wider than the view despite the shift, viewRect becomes as wide as the
            // row - the ScrollView then shows a horizontal bar instead of clipping controls.
            Rect viewRect = new Rect(0, 0, Mathf.Max(outRect.width - 16f, RowWidth + 2f), ContentHeight());
            // Remember the height of the visible area for scroll culling - see RowCulled.
            visibleListH = outRect.height;
            Widgets.BeginScrollView(outRect, ref scroll, viewRect);

            float y = 0f;
            DrawWeaponSection(viewRect, ref y, "CER_SupplyLoadouts_SectionMainWeapons".Translate(), mainEntries);
            DrawWeaponSection(viewRect, ref y, "CER_SupplyLoadouts_SectionSidearms".Translate(), secEntries);
            DrawWeaponSection(viewRect, ref y, "CER_SupplyLoadouts_SectionShields".Translate(), shieldEntries);
            DrawWeaponSection(viewRect, ref y, "CER_SupplyLoadouts_SectionPackItems".Translate(), packEntries);
            DrawAmmoSection(viewRect, ref y);
            // The "Not producible - superior loadouts skipped" section was removed without replacement (it made no
            // sense and didn't fit Assign Loadouts). With it went the shadow run in the solver that filled it.
            // Nothing to show means: no section (instead of saying "nothing to cancel", the whole section is
            // omitted when there is nothing).
            if (obsoleteAmmoCandidates.Count > 0 || obsoleteWeaponCandidates.Count > 0)
            {
                if (y > 0.5f) y += 6f;
                // The heading carries the reason itself, the separate explanation line below it is gone. In the
                // left corner the master checkbox: it hard-toggles ALL rows - the same convention as the master
                // icon in the assignment tab ("master toggles EVERYTHING").
                int totalObs = obsoleteWeaponCandidates.Count + obsoleteAmmoCandidates.Count;
                bool allObsOn = obsoleteWeaponChecked.Count + obsoleteAmmoChecked.Count >= totalObs;
                bool obsToggle = allObsOn;
                Widgets.Checkbox(new Vector2(0f, y + 1f), ref obsToggle, 20f);
                if (obsToggle != allObsOn)
                {
                    obsoleteWeaponChecked.Clear();
                    obsoleteAmmoChecked.Clear();
                    if (obsToggle)
                    {
                        foreach (ThingDef d in obsoleteWeaponCandidates) obsoleteWeaponChecked.Add(d);
                        foreach (ThingDef d in obsoleteAmmoCandidates) obsoleteAmmoChecked.Add(d);
                    }
                }
                TooltipHandler.TipRegion(new Rect(0f, y, 22f, 22f), "CER_SupplyLoadouts_ObsoleteMasterTip".Translate());
                Text.Font = GameFont.Small;
                GUI.color = new Color(0.85f, 0.87f, 0.9f);
                Widgets.Label(new Rect(26f, y, viewRect.width - 26f, 22f), "<b>" + "CER_SupplyLoadouts_ObsoleteSectionTitle".Translate() + "</b>");
                GUI.color = new Color(1f, 1f, 1f, 0.18f);
                Widgets.DrawLineHorizontal(0f, y + 22f, viewRect.width);
                GUI.color = Color.white;
                y += HeaderH;
                // One shared section for both - a bill is a bill regardless of whether it produces ammo or a
                // weapon, and the keyed section string above is already worded generically.
                foreach (ThingDef d in obsoleteWeaponCandidates) DrawObsoleteWeaponRow(viewRect, ref y, d);
                foreach (ThingDef d in obsoleteAmmoCandidates) DrawObsoleteAmmoRow(viewRect, ref y, d);
            }

            if (viewRect.height < 10f)
            {
                GUI.color = new Color(1f, 1f, 1f, 0.5f);
                Widgets.Label(new Rect(0, 4f, inRect.width, 24f), "CER_SupplyLoadouts_EverythingCovered".Translate());
                GUI.color = Color.white;
            }

            Widgets.EndScrollView();
            // The cost line sits directly below this list with not a millimeter of space in between - without a
            // hint a clipped view reads as the whole list (noticed on "Obsolete bills": 3 of 7 rows visible).
            // RowH, so a row count shows there.
            LoadoutUtility.DrawScrollMoreHint(outRect, viewRect, scroll, RowH);

            // Aggregated bill: what everything currently selected will consume, so the real material hit is visible
            // before pressing Supply.
            GUI.color = new Color(1f, 1f, 1f, 0.25f);
            Widgets.DrawLineHorizontal(0f, outRect.yMax + 3f, inRect.width);
            GUI.color = Color.white;
            LoadoutUtility.DrawTotalCostLine(0f, outRect.yMax + 12f, inRect.width, ComputeTotalCost());

            // A Back button only when there is somewhere to go back to (e.g. the assign preview) - matches the
            // same Back+Cancel layout as Dialog_SquadSetup so the whole chain reads consistently.
            // ONE font size for the WHOLE footer (same class of issue as in the bill overview): Back and Cancel
            // were drawn BEFORE BottomBarFont even ran and took no part in its calculation. So all four cells are
            // registered up front.
            string backLbl = "CER_SupplyLoadouts_BackButton".Translate().ToString();
            string cancelLbl = "CER_SupplyLoadouts_CancelButton".Translate().ToString();
            bool deferredForLabel = onConfirm != null;
            string produceLabel = deferredForLabel ? "CER_SupplyLoadouts_ConfirmSupplyButton".Translate().ToString() : "CER_SupplyLoadouts_SupplySelectedButton".Translate().ToString();
            string supplyNothingLabel = "CER_SupplyLoadouts_SupplyNothingButton".Translate().ToString();
            GameFont prevBarFont = Text.Font;
            Text.Font = onBack != null
                ? LoadoutUtility.BottomBarFont(
                    (100f, backLbl, false),
                    (LoadoutUtility.CloseBtnW, cancelLbl, false),
                    (LoadoutUtility.PrimaryBtnW, produceLabel, false),
                    (LoadoutUtility.PrimaryBtnW, supplyNothingLabel, false))
                : LoadoutUtility.BottomBarFont(
                    (LoadoutUtility.CloseBtnW, cancelLbl, false),
                    (LoadoutUtility.PrimaryBtnW, produceLabel, false),
                    (LoadoutUtility.PrimaryBtnW, supplyNothingLabel, false));

            if (onBack != null)
            {
                Rect backR = new Rect(0f, inRect.height - LoadoutUtility.BottomBarH, 100f, LoadoutUtility.BottomBtnH);
                if (LoadoutUtility.ButtonTextFit(backR, backLbl))
                {
                    wentBack = true;
                    Close();
                    onBack();
                }
                TooltipHandler.TipRegion(backR, "CER_SupplyLoadouts_BackButtonTip".Translate());
                if (LoadoutUtility.ButtonTextFit(new Rect(108f, inRect.height - LoadoutUtility.BottomBarH, LoadoutUtility.CloseBtnW, LoadoutUtility.BottomBtnH), cancelLbl))
                    Close();
            }
            else if (LoadoutUtility.BottomCloseButton(inRect, cancelLbl)) Close();

            bool deferred = onConfirm != null;
            // Ammo rows only count if they are really selectable (three blocked rows - no research, no recipe,
            // no loading bench - and "Supply selected" was still active; in such cases it is greyed out, not
            // struck through, and "Supply nothing" stays active).
            // Previously `ammoEntries.Count > 0` was enough, so even a row that DrawAmmoEntry itself draws as
            // blocked (not researched or no workbench) counted. The same two conditions as there. The button is
            // dimmed via BottomActionButton, never struck through.
            // Weapon, shield and pack rows likewise (second occurrence of the same pattern): DrawWeaponEntry only
            // draws the checkbox for craftable AND bench != null - a researched weapon without a suitable
            // workbench is a blocked row and must not keep the button active.
            bool Selectable(WeaponEntry e) => e.craftable && e.bench != null;
            bool anyProducible = mainEntries.Any(Selectable) || secEntries.Any(Selectable) || shieldEntries.Any(Selectable)
                || packEntries.Any(Selectable)
                || ammoEntries.Any(e => e.bench != null && LoadoutUtility.IsAmmoResearched(e.resolved))
                || obsoleteAmmoCandidates.Count > 0 || obsoleteWeaponCandidates.Count > 0;
            float xr = inRect.width;
            if (LoadoutUtility.BottomActionButton(inRect, ref xr, LoadoutUtility.PrimaryBtnW, produceLabel, deferred || anyProducible))
            {
                confirmed = true;
                // Bills first, THEN the deferred assignment: onConfirm may finish the Arm report, and the report
                // must already contain what GenerateAll recorded as actually queued.
                GenerateAll();
                if (onPruneObsoleteAmmo != null && obsoleteAmmoChecked.Count > 0)
                    onPruneObsoleteAmmo(obsoleteAmmoChecked.ToList());
                if (onPruneObsoleteWeapons != null && obsoleteWeaponChecked.Count > 0)
                    onPruneObsoleteWeapons(obsoleteWeaponChecked.ToList());
                onConfirm?.Invoke();
                Close();
            }

            // "Supply nothing" to the LEFT of the produce button: "Supply nothing" behaves like Cancel but is more
            // descriptive.
            //
            // On inspection it is in fact exactly the same: Close() without confirmed lets PostClose fire
            // onCancel, and in this chain onCancel already means "production skipped, run finished cleanly"
            // (Dialog_AssignLoadouts explicitly sets armReport.cancelled = false there and completes the report).
            // So the button is not new behavior but the missing NAME for it - "Cancel" reads like "abort
            // everything", although nothing is aborted.
            // Cancel stays next to it: anyone used to it shouldn't have to relearn.
            if (LoadoutUtility.BottomActionButton(inRect, ref xr, LoadoutUtility.PrimaryBtnW,
                    supplyNothingLabel, true,
                    "CER_SupplyLoadouts_SupplyNothingTip".Translate()))
                Close();
            Text.Font = prevBarFont;
        }

        // Mirrors the DoWindowContents draw order/gap logic exactly (leading 6f gap only between two sections that
        // BOTH actually drew something) so the scrollview's reserved height never drifts from what's really drawn,
        // even when diffOnly hides every row in one or more leading sections.
        private float ContentHeight()
        {
            float h = 4f;
            bool any = false;
            void Section(int count, float rowH, float extraHeaderH = 0f)
            {
                if (count == 0) return;
                if (any) h += 6f;
                h += HeaderH + extraHeaderH + count * rowH;
                any = true;
            }
            Section(Visible(mainEntries).Count, RowH);
            Section(Visible(secEntries).Count, RowH);
            Section(Visible(shieldEntries).Count, RowH);
            Section(Visible(packEntries).Count, RowH);
            Section(VisibleAmmo().Count, RowH);
            // No explanation line below the heading anymore (the reason is in the heading itself) - the
            // extraHeaderH of 20 went away with it.
            Section(obsoleteWeaponCandidates.Count + obsoleteAmmoCandidates.Count, 26f);   // one shared header
            return h;
        }

        private List<WeaponEntry> Visible(List<WeaponEntry> src) => diffOnly ? src.Where(e => !e.craftable || e.countStr != "0").ToList() : src;
        private List<AmmoEntry> VisibleAmmo() => diffOnly ? ammoEntries.Where(e => !e.alreadyInProduction).ToList() : ammoEntries;

        // Column header clicked: same mode toggles direction, a different mode resets to ascending.
        private void SetSort(SortMode mode)
        {
            if (currentSortMode == mode) sortDescending = !sortDescending;
            else { currentSortMode = mode; sortDescending = false; }
            ApplySort();
        }

        // One shared sort state governs all four lists, so the whole table reads as one consistently-ordered
        // surface rather than each section having its own independent order.
        private void ApplySort()
        {
            SortWeapons(mainEntries);
            SortWeapons(secEntries);
            SortWeapons(shieldEntries);
            SortWeapons(packEntries);
            SortAmmo(ammoEntries);
        }

        private void SortWeapons(List<WeaponEntry> list)
        {
            Comparison<WeaponEntry> cmp;
            if (currentSortMode == SortMode.Count) cmp = (a, b) => ParseCount(a.countStr).CompareTo(ParseCount(b.countStr));
            else if (currentSortMode == SortMode.Assigned) cmp = (a, b) => a.assignedNames.Count.CompareTo(b.assignedNames.Count);
            else cmp = (a, b) => string.Compare(a.weapon.LabelCap, b.weapon.LabelCap, StringComparison.OrdinalIgnoreCase);
            list.Sort(cmp);
            if (sortDescending) list.Reverse();
        }

        private void SortAmmo(List<AmmoEntry> list)
        {
            Comparison<AmmoEntry> cmp;
            if (currentSortMode == SortMode.Count) cmp = (a, b) => ParseCount(a.countStr).CompareTo(ParseCount(b.countStr));
            else if (currentSortMode == SortMode.Assigned) cmp = (a, b) => a.assignedNames.Count.CompareTo(b.assignedNames.Count);
            else cmp = (a, b) => string.Compare(a.resolved.LabelCap, b.resolved.LabelCap, StringComparison.OrdinalIgnoreCase);
            list.Sort(cmp);
            if (sortDescending) list.Reverse();
        }

        private static int ParseCount(string s) => int.TryParse(s, out int v) ? v : 0;

        private void DrawWeaponSection(Rect viewRect, ref float y, string title, List<WeaponEntry> entries)
        {
            List<WeaponEntry> shown = Visible(entries);
            if (shown.Count == 0) return;
            if (y > 0.5f) y += 6f;
            DrawSectionHeader(viewRect, ref y, title, null);
            foreach (WeaponEntry e in shown) DrawWeaponEntry(viewRect, ref y, e);
        }

        private void DrawAmmoSection(Rect viewRect, ref float y)
        {
            List<AmmoEntry> shown = VisibleAmmo();
            if (shown.Count == 0) return;
            if (y > 0.5f) y += 6f;
            DrawSectionHeader(viewRect, ref y, "CER_SupplyLoadouts_SectionAmmunition".Translate(), null);
            foreach (AmmoEntry e in shown) DrawAmmoEntry(viewRect, ref y, e);
        }

        private void DrawSectionHeader(Rect viewRect, ref float y, string label, string subtitle)
        {
            Text.Font = GameFont.Small;
            GUI.color = new Color(0.85f, 0.87f, 0.9f);
            Widgets.Label(new Rect(0, y, viewRect.width, 22f), "<b>" + label + "</b>");
            GUI.color = new Color(1f, 1f, 1f, 0.18f);
            Widgets.DrawLineHorizontal(0f, y + 22f, viewRect.width);
            GUI.color = Color.white;
            y += HeaderH;
            if (!string.IsNullOrEmpty(subtitle))
            {
                GUI.color = new Color(1f, 1f, 1f, 0.5f);
                Widgets.Label(new Rect(2f, y, viewRect.width - 4f, 20f), subtitle);
                GUI.color = Color.white;
                y += 20f;
            }
        }

        // Opt-out delete preview (2026-08-01, by design) - every candidate shows checked/grayed with a
        // trash icon by default; unchecking one keeps it (skips it on confirm). Strikethrough only on the
        // label itself, and only while still checked - same "this is going away" visual language every other
        // delete-preview in this popup uses, but it must disappear the instant the player opts an item out,
        // otherwise an unchecked (kept) row would still visually look like it's being deleted.
        // Where does the bill come from - our wizard or by hand? (Make it visually clear what the mod created and
        // what was manual.)
        // Without this mark the only difference between the two kinds would be the pre-checked box, and that is
        // exactly what goes unnoticed when skimming. The color separates them further: muted for our own
        // (routine), warm for the hand-set ones (look here).
        // Right-aligned directly left of the magnifier icon, so it sits in the same place in both row kinds and
        // doesn't compete with the production column for space.
        private static void DrawObsoleteOriginTag(Rect row, float y, ThingDef d)
        {
            bool ours = LoadoutUtility.AllBillsForDefAreOurs(d);
            Rect tag = new Rect(row.xMax - 26f - 150f, y, 150f, 24f);
            GameFont prev = Text.Font;
            Text.Font = GameFont.Tiny;
            Text.Anchor = TextAnchor.MiddleRight;
            GUI.color = ours ? new Color(1f, 1f, 1f, 0.45f) : new Color(0.85f, 0.72f, 0.45f);
            Widgets.Label(tag, ours ? "CER_SupplyLoadouts_ObsoleteOriginMod".Translate() : "CER_SupplyLoadouts_ObsoleteOriginManual".Translate());
            GUI.color = Color.white;
            Text.Anchor = TextAnchor.UpperLeft;
            Text.Font = prev;
            TooltipHandler.TipRegion(tag, ours
                ? "CER_SupplyLoadouts_ObsoleteOriginModTip".Translate()
                : "CER_SupplyLoadouts_ObsoleteOriginManualTip".Translate());
        }

        private void DrawObsoleteAmmoRow(Rect viewRect, ref float y, ThingDef ammo)
        {
            Rect row = new Rect(0, y, viewRect.width, 24f);
            bool wasChecked = obsoleteAmmoChecked.Contains(ammo);
            bool isChecked = wasChecked;
            Widgets.Checkbox(new Vector2(0f, y + 2f), ref isChecked, 20f);
            if (isChecked != wasChecked)
            {
                if (isChecked) obsoleteAmmoChecked.Add(ammo); else obsoleteAmmoChecked.Remove(ammo);
            }
            GUI.color = isChecked ? new Color(0.9f, 0.55f, 0.5f, 0.85f) : new Color(1f, 1f, 1f, 0.5f);
            // Trash icon only while still checked/being deleted (2026-08-02: it stayed
            // visible even after opting a row out via unchecking, contradicting the row's own kept/strikethrough
            // state right next to it).
            if (isChecked) GUI.DrawTexture(new Rect(IconX, y + 3f, 16f, 16f), LoadoutUtility.TexTrash);
            Widgets.ThingIcon(new Rect(IconX + 22f, y + 1f, 20f, 20f), ammo);
            Text.Anchor = TextAnchor.MiddleLeft;
            string label = ammo.LabelCap.ToString();
            Rect nameRect = new Rect(NameX + 22f, y, 260f + colShift, 24f);   // gives up width when shifted, see IconX
            // Real strikethrough, not the old <s></s> tag (2026-08-01: Unity's legacy rich
            // text has no strikethrough tag support at all, so it showed the raw "<s>Uzi</s>" characters
            // instead of a line through the text) - see LoadoutUtility.DrawStrikethroughLabel's own comment.
            if (isChecked) LoadoutUtility.DrawStrikethroughLabel(nameRect, label);
            else Widgets.Label(nameRect, label);
            GUI.color = Color.white;
            // What's actually running for nothing, and where (2026-08-01: only the item name
            // was shown, nothing to sanity-check the bulk-delete against before confirming).
            int prodTotal = LoadoutUtility.BuildProductionDetail(ammo, out List<string> prodDetail);
            if (prodTotal > 0)
            {
                GUI.color = new Color(0.78f, 0.68f, 0.48f);
                Widgets.Label(new Rect(nameRect.xMax + 8f, y, 300f, 24f), "CER_SupplyLoadouts_ObsoleteProdFmt".Translate(prodTotal.ToString()));
                GUI.color = Color.white;
            }
            Text.Anchor = TextAnchor.UpperLeft;
            // Jump-to-bench (2026-08-02) - same search-icon convention every other row's
            // bench cell already uses, so a player can go inspect/pause the running bill directly instead of
            // only reading about it in the tooltip.
            DrawObsoleteOriginTag(row, y, ammo);
            Building_WorkTable jumpBench = LoadoutUtility.FindActiveBillBench(ammo);
            if (jumpBench != null)
            {
                Rect obsJumpRect = new Rect(row.xMax - 22f, y + 3f, 18f, 18f);
                if (Widgets.ButtonImage(obsJumpRect, TexButton.Search))
                {
                    LoadoutUtility.JumpToBench(jumpBench);
                    Find.Selector.ClearSelection();
                    Find.Selector.Select(jumpBench);
                }
                TooltipHandler.TipRegion(obsJumpRect, "CER_SupplyLoadouts_JumpToBenchTip".Translate());
            }
            string prodTip = prodDetail.Count > 0 ? "\n\n" + "CER_SupplyLoadouts_InProductionTip".Translate(string.Join("\n  ", prodDetail.ToArray())).ToString() : "";
            TooltipHandler.TipRegion(row, (isChecked
                ? "CER_SupplyLoadouts_ObsoleteAmmoTip".Translate(label)
                : "CER_SupplyLoadouts_ObsoleteAmmoKeptTip".Translate(label)).ToString() + prodTip);
            y += 26f;
        }

        // Same opt-out delete preview as DrawObsoleteAmmoRow above, for a weapon-producing bill instead of an
        // ammo one (2026-08-01) - reuses the exact same Keyed strings (already worded generically, "bill"
        // not "ammo bill") rather than duplicating a near-identical set of translations.
        private void DrawObsoleteWeaponRow(Rect viewRect, ref float y, ThingDef weapon)
        {
            Rect row = new Rect(0, y, viewRect.width, 24f);
            bool wasChecked = obsoleteWeaponChecked.Contains(weapon);
            bool isChecked = wasChecked;
            Widgets.Checkbox(new Vector2(0f, y + 2f), ref isChecked, 20f);
            if (isChecked != wasChecked)
            {
                if (isChecked) obsoleteWeaponChecked.Add(weapon); else obsoleteWeaponChecked.Remove(weapon);
            }
            GUI.color = isChecked ? new Color(0.9f, 0.55f, 0.5f, 0.85f) : new Color(1f, 1f, 1f, 0.5f);
            if (isChecked) GUI.DrawTexture(new Rect(IconX, y + 3f, 16f, 16f), LoadoutUtility.TexTrash);
            Widgets.ThingIcon(new Rect(IconX + 22f, y + 1f, 20f, 20f), weapon);
            Text.Anchor = TextAnchor.MiddleLeft;
            string label = weapon.LabelCap.ToString();
            Rect nameRect = new Rect(NameX + 22f, y, 260f + colShift, 24f);   // gives up width when shifted, see IconX
            if (isChecked) LoadoutUtility.DrawStrikethroughLabel(nameRect, label);
            else Widgets.Label(nameRect, label);
            GUI.color = Color.white;
            int prodTotal = LoadoutUtility.BuildProductionDetail(weapon, out List<string> prodDetail);
            if (prodTotal > 0)
            {
                GUI.color = new Color(0.78f, 0.68f, 0.48f);
                Widgets.Label(new Rect(nameRect.xMax + 8f, y, 300f, 24f), "CER_SupplyLoadouts_ObsoleteProdFmt".Translate(prodTotal.ToString()));
                GUI.color = Color.white;
            }
            Text.Anchor = TextAnchor.UpperLeft;
            DrawObsoleteOriginTag(row, y, weapon);
            Building_WorkTable jumpBench2 = LoadoutUtility.FindActiveBillBench(weapon);
            if (jumpBench2 != null)
            {
                Rect obsJumpRect2 = new Rect(row.xMax - 22f, y + 3f, 18f, 18f);
                if (Widgets.ButtonImage(obsJumpRect2, TexButton.Search))
                {
                    LoadoutUtility.JumpToBench(jumpBench2);
                    Find.Selector.ClearSelection();
                    Find.Selector.Select(jumpBench2);
                }
                TooltipHandler.TipRegion(obsJumpRect2, "CER_SupplyLoadouts_JumpToBenchTip".Translate());
            }
            string prodTip = prodDetail.Count > 0 ? "\n\n" + "CER_SupplyLoadouts_InProductionTip".Translate(string.Join("\n  ", prodDetail.ToArray())).ToString() : "";
            TooltipHandler.TipRegion(row, (isChecked
                ? "CER_SupplyLoadouts_ObsoleteAmmoTip".Translate(label)
                : "CER_SupplyLoadouts_ObsoleteAmmoKeptTip".Translate(label)).ToString() + prodTip);
            y += 26f;
        }

        // Two independent slim indicators, always both shown regardless of the scope radio (2026-08-02,
        // replaces the old single DrawUsedBySquare - see Request.assignedUsedBy's own comment for why one
        // dict-set whose meaning silently flipped between "real pawn" and "loadout template" had to go).
        // Diamond = assigned to a real colonist right now; circle = used by a managed Loadout template. The
        // names live in each icon's own mouseover, not in a fat text column.
        private static void DrawAssignmentIndicators(float y, List<string> assignedNames, List<string> templateNames, string assignedNoneTip, string templateNoneTip)
        {
            DrawIndicatorCell(UsedX, y, assignedNames, assignedNoneTip, diamond: true);
            DrawIndicatorCell(UsedX + 32f, y, templateNames, templateNoneTip, diamond: false);
        }

        // The body lives in LoadoutUtility.DrawUsedByIndicator (including the white-instead-of-ColGood comment) -
        // the Packs tab draws its carrier diamond through the same function.
        private static void DrawIndicatorCell(float x, float y, List<string> names, string noneTip, bool diamond)
            => LoadoutUtility.DrawUsedByIndicator(x, y, names, noneTip, diamond);

        // "+<yield>" stepper button next to the exact target count (2026-08-01 fix) -
        // replaces the old "type a batch number, it multiplies against yield" field, which needed a mental
        // multiplication step for something a click can just do directly: each click adds one batch's worth
        // straight onto Count. Count itself is still freely typable for an exact target that isn't a whole
        // multiple of yield.
        private static void DrawBatchCountPair(float y, int yield, ref string countStr)
        {
            Rect addRect = new Rect(BatchX, y, CountX - BatchX - 4f, 24f);
            Rect countRect = new Rect(CountX, y, 45f, 24f);
            if (LoadoutUtility.ButtonTextFit(addRect, "CER_SupplyLoadouts_BatchAddButton".Translate(yield.ToString()).ToString()))
            {
                int.TryParse(countStr, out int cur);
                countStr = Mathf.Max(0, cur + yield).ToString();
            }
            TooltipHandler.TipRegion(addRect, "CER_SupplyLoadouts_BatchAddTip".Translate(yield.ToString()));
            countStr = Widgets.TextField(countRect, countStr);
            TooltipHandler.TipRegion(countRect, "CER_SupplyLoadouts_ExactCountTip".Translate());
        }

        // Scroll culling. This window was the ONLY one of the three list windows without it -
        // Dialog_AssignLoadouts and Dialog_SquadSetup have long had it, in the same form: a row outside the
        // visible area only advances the cursor and draws nothing. That is not cosmetic here but noticeable: a
        // weapon row calls GetWeaponScore, builds two tooltips, draws the availability bar and queries the
        // workbenches, an ammo row the same - and the window regularly lists more rows than fit.
        // Safe because these two methods set NOTHING that doesn't come from a click (checked: only
        // `e.selected` from the checkbox row and `e.stuff`/`e.bench` from menu callbacks). A skipped row thus
        // keeps exactly its state - the confirm below reads all entries, not just the drawn ones.
        private float visibleListH;

        private bool RowCulled(ref float y, float h)
        {
            if (y + h < scroll.y || y > scroll.y + visibleListH) { y += h; return true; }
            return false;
        }

        private void DrawWeaponEntry(Rect viewRect, ref float y, WeaponEntry e)
        {
            if (RowCulled(ref y, RowH)) return;
            GUI.color = e.craftable ? Color.white : Color.gray;
            Widgets.ThingIcon(new Rect(IconX, y + 2f, 20f, 20f), e.weapon);
            Rect nameRect = new Rect(NameX, y, SpecialX - NameX - 4f, 24f);
            LoadoutUtility.LabelCell(nameRect, e.weapon.LabelCap);
            GUI.color = Color.white;
            LoadoutUtility.GetWeaponScore(e.weapon, out _, out string wTip);
            TooltipHandler.TipRegion(new Rect(IconX, y, SpecialX - IconX, 24f), wTip);

            DrawAssignmentIndicators(y, e.assignedNames, e.templateNames,
                "CER_SupplyLoadouts_NotAssignedTip".Translate(), "CER_SupplyLoadouts_NotUsedInLoadoutTip".Translate());

            if (!e.craftable)
            {
                // Name ONE reason, not two to choose from (side by side, the ammo row said "Needs research:
                // Advanced ammunition", while the weapon row next to it still said "can't be crafted (no recipe /
                // not researched)" - two possibilities in one sentence, and afterwards nobody knows which applies).
                // The distinction is cheap and the function for it has long existed - the ammo side has used
                // MissingResearchFor for a while, the weapon side never got it.
                // No recipe is the final case (trade goods, relic); missing research is the fixable one, and then
                // the project's name belongs with it.
                GUI.color = Color.gray;
                string missingResW = LoadoutUtility.GetRecipeFor(e.weapon) == null ? null : LoadoutUtility.MissingResearchFor(e.weapon);
                // Limited to ReasonCellW instead of a fixed 400: 400 reached from 546 to 946 and thus 114 points
                // DEEP into the bar column at 832. As long as this row had no bar at all, nobody noticed; since it
                // has one, it would have been an overlap - introduced in the same pass and caught when
                // recalculating the column positions. ReasonCellW is ProdX - MaterialX - 8 and exists exactly for
                // this; the twin below (no workbench) has long used it via DrawBlockedReasonCell.
                Rect reasonRect = new Rect(MaterialX, y, ReasonCellW, 24f);
                if (!string.IsNullOrEmpty(missingResW))
                {
                    Widgets.Label(reasonRect, "CER_SupplyLoadouts_WeaponNeedsResearch".Translate(missingResW));
                    GUI.color = Color.white;
                    TooltipHandler.TipRegion(reasonRect, "CER_SupplyLoadouts_WeaponNeedsResearchTip".Translate(missingResW));
                }
                else
                {
                    Widgets.Label(reasonRect, "CER_SupplyLoadouts_ItemNoRecipe".Translate());
                    GUI.color = Color.white;
                    TooltipHandler.TipRegion(reasonRect, "CER_SupplyLoadouts_ItemNoRecipeTip".Translate());
                }
                // This exit gets the stock bar too: that something can't be produced doesn't mean nothing is
                // there - healroot grows, trade goods sit on the shelf. Without a bar the row stood there without
                // any number, and the player had to switch to another tab to see whether anything is missing.
                // The same gap as in the ammo branches, found by searching from the symptom: this bar too showed
                // its numbers without the "where is it / who carries it" breakdown every unblocked row has.
                Rect noRecipeAvailRect = new Rect(ProdX, y + 1f, 155f, 22f);
                LoadoutUtility.DrawAvailabilityBar(noRecipeAvailRect, RowAvailability(e, 0), horizontal: true,
                    LoadoutUtility.TipWanted(noRecipeAvailRect) ? RowLocationTip(e) : null);
                y += RowH;
                return;
            }

            // Same treatment DrawAmmoEntry already gives its own "researched but no bench" case - a bare,
            // fully grayed row explaining WHY instead of checkbox/batches/count controls that would be
            // meaningless (nothing here can actually be queued). Used to leave Batches/Count fully live and
            // editable even with no bench at all, in a DIFFERENT column and wording than the ammo row's own
            // version of the exact same problem (2026-07-31: wanted the two unified - same
            // text, same column, non-interactive, visibly grayed).
            if (e.bench == null)
            {
                // Icon + tooltip (2026-08-02, second pass - the first fix just swapped in ANOTHER fixed string,
                // which still overflowed this cell on its own once the localized wording ran long; a fixed-
                // WIDTH cell can never safely hold ANY variable-length sentence, no matter how short it looks
                // in English). No text in the cell at all now - a small warning badge (can never overflow,
                // its size doesn't depend on any string) with the full "No <bench type>" detail moved entirely
                // into the tooltip, same design the ammo row below already used for the unresearched case.
                Rect noBenchIconR = BenchWarnRect(y);
                LoadoutUtility.DrawWarningBadge(noBenchIconR, LoadoutUtility.ColBad);
                TooltipHandler.TipRegion(new Rect(BenchX, y, 200f, 24f), "CER_SupplyLoadouts_NoBenchWithTypeLabel".Translate(LoadoutUtility.BenchTypeNames(e.weapon)));
                // The reason is now visible in the row too - see DrawBlockedReasonCell.
                DrawBlockedReasonCell(y, "CER_SupplyLoadouts_NoBenchRowLabel".Translate(LoadoutUtility.BenchTypeNames(e.weapon)),
                    "CER_SupplyLoadouts_NoBenchWithTypeLabel".Translate(LoadoutUtility.BenchTypeNames(e.weapon)));

                // The same bar as in a row WITH a workbench - see RowAvailability.
                // Previously this had its own calculation from GetWeaponInventory, which returns zero for pack
                // contents: without a drug lab the medicine row said Total 0, while the Equipment Packs tab showed
                // 77 at the same moment. The workbench decides whether something can be PRODUCED - not how much of
                // it the colony has.
                Rect noBenchAvailRect = new Rect(ProdX, y + 1f, 155f, 22f);
                LoadoutUtility.DrawAvailabilityBar(noBenchAvailRect, RowAvailability(e, 0), horizontal: true,
                    LoadoutUtility.TipWanted(noBenchAvailRect) ? RowLocationTip(e) : null);

                y += RowH;
                return;
            }

            {
                bool sel = e.selected;
                Widgets.Checkbox(new Vector2(0f, y + 2f), ref sel, 20f);
                e.selected = sel;
            }

            Dialog_GenerateLoadouts.DrawWeaponSpecialIcons(SpecialX, y + 4f, e.weapon, UsedX - SpecialX - 6f);

            int orderCount = int.TryParse(e.countStr, out int oc) ? Mathf.Max(1, oc) : 1;
            // ing.count is a PER-BATCH cost, not per unit of the target count - passing the raw want here
            // overstated the shortage check by up to e.yield-times for a yield>1 recipe (almost always 1 for
            // weapons, but shared code paths route some pack items - e.g. yield>1 meds - through this same
            // WeaponEntry/DrawIngredientCell call, see isPackItem above). 2026-07-31 fix.
            int batches = e.yield > 0 ? Mathf.CeilToInt((float)orderCount / e.yield) : orderCount;
            if (e.ings.Count > 0 || e.stuffs.Count == 0)
                LoadoutUtility.DrawIngredientCell(IngX, 150f, y + 3f, e.ings, batches);

            if (e.stuffs.Count > 0)
            {
                // Button shows the material's icon before its name (and the menu options carry icons too).
                Rect matRect = new Rect(MaterialX, y, 120f, 24f);
                bool matClicked = Widgets.ButtonText(matRect, "");
                {
                    float mx = matRect.x + 6f;
                    if (e.stuff != null) { Widgets.ThingIcon(new Rect(mx, y + 2f, 20f, 20f), e.stuff); mx += 24f; }
                    Text.Anchor = TextAnchor.MiddleLeft;
                    Widgets.Label(new Rect(mx, matRect.y, matRect.xMax - mx - 4f, 24f),
                        (e.stuff != null ? e.stuff.LabelCap.ToString() : "CER_SupplyLoadouts_AnyMaterial".Translate().ToString()).Truncate(matRect.xMax - mx - 8f));
                    Text.Anchor = TextAnchor.UpperLeft;
                }
                if (matClicked)
                {
                    List<FloatMenuOption> opts = new List<FloatMenuOption>();
                    foreach (ThingDef s in e.stuffs)
                    {
                        ThingDef local = s;
                        // Re-derive the ingredient cell for the new material, so its icon + stock warning follow.
                        FloatMenuOption opt = new FloatMenuOption(local.LabelCap, () => { e.stuff = local; e.ings = LoadoutUtility.GetRecipeIngredients(e.recipe, local); }, local);
                        int needed = LoadoutUtility.GetRecipeIngredients(e.recipe, local).FirstOrDefault(i => i.thingDef == local)?.count ?? 0;
                        int have = LoadoutUtility.IngredientStockCount(local);
                        opt.mouseoverGuiAction = r => TooltipHandler.TipRegion(r, new TipSignal(
                            "CER_SupplyLoadouts_MaterialStockTip".Translate(needed.ToString(), have.ToString()).ToString(), local.GetHashCode() ^ 8511));
                        opts.Add(opt);
                    }
                    Find.WindowStack.Add(new FloatMenu(opts));
                }
            }
            else
            {
                GUI.color = Color.gray;
                Widgets.Label(new Rect(MaterialX, y + 2f, 120f, 24f), "CER_SupplyLoadouts_NoMaterialDash".Translate());
                GUI.color = Color.white;
            }

            DrawBatchCountPair(y, e.yield, ref e.countStr);

            // The SUM comes from the shared rule (LoadoutUtility.QueuedFromBills), no longer from adding up the
            // individual bill counts: two "until 50" bills don't make 100 together, and what is already in
            // storage is not produced again. This number colors the bar - exactly where the row used to be too
            // optimistic.
            // The breakdown below still names what EACH workbench is set to; that is a different question from
            // "how much is still coming" and deliberately stays as it is.
            List<Bill_Production> prodBills = new List<Bill_Production>();
            List<string> prodDetail = new List<string>();
            foreach (Building_WorkTable b in e.benches)
            {
                if (b.billStack == null) continue;
                foreach (Bill_Production bill in b.billStack.Bills.OfType<Bill_Production>())
                    if (bill.recipe == e.recipe)
                    {
                        prodBills.Add(bill);
                        int cnt = bill.repeatMode == BillRepeatModeDefOf.RepeatCount ? bill.repeatCount : bill.targetCount;
                        prodDetail.Add((e.benchLabels.TryGetValue(b, out string bl) ? bl : b.def.label) + " (x" + cnt + ")");
                    }
            }
            int inProd = LoadoutUtility.QueuedFromBills(prodBills, e.weapon);
            // Harmonized availability bar (2026-07-30) - replaces the old plain "N / M" text cell. No single
            // pawn here (this popup can cover many colonists at once), so the trailing shortfall segment
            // (e.unfulfilled) renders grey, not red.
            LoadoutUtility.AvailabilitySegments prodSeg = RowAvailability(e, inProd);
            // Where-is-it/who-has-it breakdown (2026-08-01: this bar's "Available" used to show
            // no location detail at all, unlike every sibling Availability bar in the mod) + the existing
            // in-production detail, joined the same "\n\n" way BuildAvailabilityTooltip itself sections things.
            // TipWanted: the location breakdown runs over all maps and all colonists - see
            // LoadoutUtility.TipWanted. This was the SEVENTH occurrence of the same pattern; the other six had
            // been guarded already, this one slipped through and was added later.
            Rect prodRect = new Rect(ProdX, y + 1f, 155f, 22f);
            string combinedTip = null;
            if (LoadoutUtility.TipWanted(prodRect))
            {
                string invTip = RowLocationTip(e);
                string prodTip = prodDetail.Count > 0 ? "CER_SupplyLoadouts_InProductionTip".Translate(string.Join("\n  ", prodDetail.ToArray())).ToString() : null;
                combinedTip = string.Join("\n\n", new[] { invTip, prodTip }.Where(s => !string.IsNullOrEmpty(s)));
            }
            LoadoutUtility.DrawAvailabilityBar(prodRect, prodSeg, horizontal: true, string.IsNullOrEmpty(combinedTip) ? null : combinedTip);

            // e.bench == null already returned early above - this point is only reached with a real bench.
            {
                string bid = LoadoutUtility.BenchId(e.bench, e.benches);
                if (LoadoutUtility.BenchIconButton(new Rect(BenchX, y + 2f, BenchBtnW, BenchBtnH), e.bench, bid, e.bench.def.LabelCap))
                {
                    List<FloatMenuOption> opts = new List<FloatMenuOption>();
                    foreach (Building_WorkTable b in e.benches)
                    {
                        Building_WorkTable local = b;
                        string optLabel = e.benchLabels.TryGetValue(local, out string ol) ? ol : local.def.label;
                        opts.Add(new FloatMenuOption(optLabel, () => e.bench = local));
                    }
                    Find.WindowStack.Add(new FloatMenu(opts));
                }
                Rect jumpRect = new Rect(JumpX, y + 4f, 18f, 18f);
                if (Widgets.ButtonImage(jumpRect, TexButton.Search))
                {
                    LoadoutUtility.JumpToBench(e.bench);
                    Find.Selector.ClearSelection();
                    Find.Selector.Select(e.bench);
                }
                TooltipHandler.TipRegion(jumpRect, "CER_SupplyLoadouts_JumpToBenchTip".Translate());
                Rect infoRect = new Rect(InfoX, y + 4f, 18f, 18f);
                if (Widgets.ButtonImage(infoRect, TexButton.Info)) LoadoutUtility.OpenBillsTab(e.bench);
                TooltipHandler.TipRegion(infoRect, "CER_SupplyLoadouts_OpenBillsTip".Translate());
            }

            y += RowH;
        }

        private void DrawAmmoEntry(Rect viewRect, ref float y, AmmoEntry e)
        {
            if (RowCulled(ref y, RowH)) return;
            bool researched = LoadoutUtility.IsAmmoResearched(e.resolved);
            GUI.color = researched ? Color.white : Color.gray;
            Widgets.ThingIcon(new Rect(IconX, y + 2f, 20f, 20f), e.resolved);
            Rect nameRect = new Rect(NameX, y, SpecialX - NameX - 4f, 24f);
            string autoTag = e.sourceGeneric != null ? "CER_SupplyLoadouts_AutoTag".Translate().ToString() : "";
            LoadoutUtility.LabelCell(nameRect, e.resolved.label + autoTag);
            GUI.color = Color.white;
            TooltipHandler.TipRegion(new Rect(IconX, y, SpecialX - IconX, 24f), LoadoutUtility.GetAmmoRecipeTooltip(e.resolved)
                + (e.sourceGeneric != null ? "CER_SupplyLoadouts_AutoResolvedTip".Translate().ToString() : ""));

            DrawAssignmentIndicators(y + 2f, e.assignedNames, e.templateNames,
                "CER_SupplyLoadouts_NotAssignedAmmoTip".Translate(), "CER_SupplyLoadouts_NotUsedInLoadoutAmmoTip".Translate());

            // Same treatment DrawWeaponEntry gives an uncraftable weapon / no-bench weapon - a bare, fully
            // grayed row explaining WHY instead of checkbox/count/bench controls that would be meaningless
            // for something that can't be queued at all. Two distinct "will never work right now" reasons -
            // not researched, or researched but no suitable workbench built yet - both left the row fully
            // checked/selectable before (the checkbox default only ever considered research, never bench),
            // which silently promised a bill that Confirm's own GenerateAll would then just skip with no
            // explanation. The "no bench" case now shares the EXACT same text and column (BenchX, not
            // MaterialX) as the weapon row's own version of the identical problem (2026-07-31: the two
            // belong unified).
            bool hasBench = e.bench != null;
            if (!researched)
            {
                GUI.color = LoadoutUtility.ColWarn;
                // Now names the missing research project - "not researched" alone didn't say WHAT has to be
                // researched, and that is exactly where a first-time playthrough got stuck. Falls back to the old
                // text when no project can be determined.
                string missingRes = LoadoutUtility.MissingResearchFor(e.resolved);
                Rect notResRect = new Rect(MaterialX, y, 220f, 24f);
                LoadoutUtility.LabelCell(notResRect, string.IsNullOrEmpty(missingRes)
                    ? "CER_SupplyLoadouts_AmmoNotResearched".Translate().ToString()
                    : "CER_SupplyLoadouts_AmmoNeedsResearch".Translate(missingRes).ToString());
                if (!string.IsNullOrEmpty(missingRes))
                    TooltipHandler.TipRegion(notResRect, "CER_SupplyLoadouts_AmmoNeedsResearchTip".Translate(missingRes, LoadoutUtility.BenchTypeNames(e.resolved)));
                GUI.color = Color.white;

                // Can't make MORE, but whatever's already in storage still matters - a blocked row used to
                // show no stock info at all, reading as "there is definitely none" when the colony might
                // already own plenty (bought/looted, or crafted before the bench was lost/dismantled).
                // The SAME calculation as the normal row further down: as long as no suitable workbench existed,
                // the supply screen showed NOTHING AT ALL in the Batches / Stock / Availability columns - but the
                // pure DEMAND doesn't depend on the workbench. That was a reasoning error: a missing workbench (or
                // research) blocks PRODUCING, not knowing. Precisely in this situation you most urgently want to
                // know HOW MUCH is missing - that is the reason to build the workbench in the first place.
                // Previously this showed only a stock figure without any demand, so the bar was always "full" and
                // said the opposite of what was going on.
                // ComputeAmmoAvailability knows nothing about workbenches - the same source every other bar in the
                // mod takes its numbers from. Both blocked branches (no research / no workbench) get it, not just
                // the one seen first.
                // WITH the location breakdown: the bar here had the same numbers as in the bills tab, but its
                // mouseover left out "Carried by:" and "Assigned to:" - two screens, the same row, different
                // amounts of information. The workbench decides whether something can be PRODUCED; who has the
                // ammo on their belt right now has nothing to do with it. TipWanted as everywhere, the breakdown
                // runs over all maps and would otherwise be built for nothing every frame.
                Rect availRect = new Rect(ProdX, y + 1f, 155f, 22f);
                LoadoutUtility.DrawAvailabilityBar(availRect, AmmoRowAvailability(e.resolved), horizontal: true,
                    LoadoutUtility.TipWanted(availRect) ? LoadoutUtility.BuildAmmoLocationTooltip(e.resolved) : null);

                y += RowH;
                return;
            }
            if (!hasBench)
            {
                // Icon + tooltip, no inline text - see DrawWeaponEntry's identical fix above.
                // RED, not orange: "no suitable workbench available" is not a hint in this column but a hard
                // block - no bill can be created. This matches the color rule that already applies to durable
                // items: impossible is red, scarce is orange.
                LoadoutUtility.DrawWarningBadge(BenchWarnRect(y), LoadoutUtility.ColBad);
                TooltipHandler.TipRegion(new Rect(BenchX, y, 200f, 24f), "CER_SupplyLoadouts_NoBenchWithTypeLabel".Translate(LoadoutUtility.BenchTypeNames(e.resolved)));
                // The reason is now visible in the row too - see DrawBlockedReasonCell.
                DrawBlockedReasonCell(y, "CER_SupplyLoadouts_NoBenchRowLabel".Translate(LoadoutUtility.BenchTypeNames(e.resolved)),
                    "CER_SupplyLoadouts_NoBenchWithTypeLabel".Translate(LoadoutUtility.BenchTypeNames(e.resolved)));

                // The SAME calculation as the normal row further down: as long as no suitable workbench existed,
                // the supply screen showed NOTHING AT ALL in the Batches / Stock / Availability columns - but the
                // pure DEMAND doesn't depend on the workbench. That was a reasoning error: a missing workbench (or
                // research) blocks PRODUCING, not knowing. Precisely in this situation you most urgently want to
                // know HOW MUCH is missing - that is the reason to build the workbench in the first place.
                // Previously this showed only a stock figure without any demand, so the bar was always "full" and
                // said the opposite of what was going on.
                // ComputeAmmoAvailability knows nothing about workbenches - the same source every other bar in the
                // mod takes its numbers from. Both blocked branches (no research / no workbench) get it, not just
                // the one seen first.
                // WITH the location breakdown: the bar here had the same numbers as in the bills tab, but its
                // mouseover left out "Carried by:" and "Assigned to:" - two screens, the same row, different
                // amounts of information. The workbench decides whether something can be PRODUCED; who has the
                // ammo on their belt right now has nothing to do with it. TipWanted as everywhere, the breakdown
                // runs over all maps and would otherwise be built for nothing every frame.
                Rect availRect = new Rect(ProdX, y + 1f, 155f, 22f);
                LoadoutUtility.DrawAvailabilityBar(availRect, AmmoRowAvailability(e.resolved), horizontal: true,
                    LoadoutUtility.TipWanted(availRect) ? LoadoutUtility.BuildAmmoLocationTooltip(e.resolved) : null);

                y += RowH;
                return;
            }

            bool sel = e.selected;
            Widgets.Checkbox(new Vector2(0f, y + 2f), ref sel, 20f);
            e.selected = sel;

            SpecialAbility ammoEff = LoadoutUtility.GetAmmoEffects(e.resolved);
            if (ammoEff != SpecialAbility.None) Dialog_GenerateLoadouts.DrawAmmoEffectIcons(SpecialX, y + 3f, ammoEff, e.resolved, UsedX - SpecialX - 6f);

            // ing.count is a PER-BATCH cost - passing the raw target count as mult (or defaulting to 1, as
            // this used to) overstates/understates the shortage check by up to e.yield-times for any ammo
            // recipe yielding more than 1 per craft (most of them). Same fix as DrawWeaponEntry's own
            // orderCount/batches split below (2026-07-31).
            int ammoOrderCount = int.TryParse(e.countStr, out int aoc) ? Mathf.Max(1, aoc) : 1;
            int ammoBatches = e.yield > 0 ? Mathf.CeilToInt((float)ammoOrderCount / e.yield) : ammoOrderCount;
            LoadoutUtility.DrawIngredientCell(IngX, 150f, y + 3f, e.ings, ammoBatches);

            // Ammo has no Stuff, so its Material cell holds the variant switch instead (now for every row, no longer
            // hidden behind the old used-by text).
            Rect matRect = new Rect(MaterialX, y + 2f, 120f, 24f);
            if (e.alternatives.Count > 0)
            {
                if (Widgets.ButtonText(matRect, "CER_SupplyLoadouts_ChangeButton".Translate().ToString().Truncate(116f)))
                {
                    List<FloatMenuOption> opts = new List<FloatMenuOption>();
                    foreach (ThingDef alt in e.alternatives)
                    {
                        ThingDef local = alt;
                        opts.Add(new FloatMenuOption(local.label, () => SwitchAmmoChoice(e, local)));
                    }
                    Find.WindowStack.Add(new FloatMenu(opts));
                }
                TooltipHandler.TipRegion(matRect, "CER_SupplyLoadouts_SwitchVariantTip".Translate());
            }
            else
            {
                // "Material" only ever means "switch variant" for an ammo row (there's no Stuff concept for
                // ammo - the ingredients cell just left of this already shows what actually gets consumed,
                // e.g. Wood for a stone arrow) - a bare dash with no explanation read as if the row had no
                // material at all, when it just means no OTHER researched caliber/round exists to switch to.
                GUI.color = Color.gray;
                Rect noAltRect = new Rect(MaterialX, y + 4f, 120f, 24f);
                Widgets.Label(noAltRect, "CER_SupplyLoadouts_NoAlternativeDash".Translate());
                GUI.color = Color.white;
                TooltipHandler.TipRegion(noAltRect, "CER_SupplyLoadouts_NoAlternativeTip".Translate());
            }

            if (e.selected)
                DrawBatchCountPair(y, e.yield, ref e.countStr);

            // How much is already sitting in storage right now - the actual question "do I even need to
            // produce this" answers first, before whether a bill happens to be running too. This bar's own
            // segments have no per-pawn "carried/assigned" breakdown (unlike a discrete weapon) - reserved-
            // by-storage comes from ComputeAvailability's own demand figure, actual production from the same
            // WeaponQueuedCount every other bar uses (despite the name, it counts any produced ThingDef). The
            // tooltip's where-is-it breakdown (BuildAmmoLocationTooltip, 2026-08-01) is independent of that
            // and works fine regardless.
            int ammoInProd = LoadoutUtility.WeaponQueuedCount(e.resolved);
            // Clamped to actual stock (2026-08-01: "Assigned, in storage" showed 40 in blue
            // while the colony genuinely owned zero) - demandedFromStorage is ComputeAmmoAvailability's own
            // RAW, unclamped shortfall figure ("how much every assigned pawn's slot wants minus what they
            // already carry"), which can exceed actual stock by design (that's what a shortfall IS). Every
            // other BuildAvailabilitySegments caller clamps via Min(stock, demand) first; this one skipped
            // it, so requesting more than existed silently painted the missing rounds as if they were
            // sitting in storage. neededCount now engages the bar's own red "missing" segment for any real
            // shortfall, matching this same file's WEAPON row (a few lines up) which already does this.
            var ammoSeg = AmmoRowAvailability(e.resolved);
            Rect prodRect = new Rect(ProdX, y + 1f, 155f, 22f);
            // TipWanted: the location tooltip runs over all maps - only build it when it is actually shown.
            LoadoutUtility.DrawAvailabilityBar(prodRect, ammoSeg, horizontal: true,
                LoadoutUtility.TipWanted(prodRect) ? LoadoutUtility.BuildAmmoLocationTooltip(e.resolved) : null);

            if (e.bench != null)
            {
                // Same compact icon button (+ "#id" for duplicates) and jump/info pair as the weapon rows above -
                // one visual language for "which bench" everywhere.
                string bid = LoadoutUtility.BenchId(e.bench, e.benches);
                if (LoadoutUtility.BenchIconButton(new Rect(BenchX, y + 2f, BenchBtnW, BenchBtnH), e.bench, bid, e.bench.def.LabelCap))
                {
                    Dictionary<Building_WorkTable, string> benchLabels = LoadoutUtility.BuildBenchLabels(e.benches);
                    List<FloatMenuOption> opts = new List<FloatMenuOption>();
                    foreach (Building_WorkTable b in e.benches)
                    {
                        Building_WorkTable local = b;
                        opts.Add(new FloatMenuOption(benchLabels.TryGetValue(local, out string ol) ? ol : local.def.label, () => e.bench = local));
                    }
                    Find.WindowStack.Add(new FloatMenu(opts));
                }
                Rect jumpRect = new Rect(JumpX, y + 4f, 18f, 18f);
                if (Widgets.ButtonImage(jumpRect, TexButton.Search))
                {
                    LoadoutUtility.JumpToBench(e.bench);
                    Find.Selector.ClearSelection();
                    Find.Selector.Select(e.bench);
                }
                TooltipHandler.TipRegion(jumpRect, "CER_SupplyLoadouts_JumpToBenchTip".Translate());
                Rect infoRect = new Rect(InfoX, y + 4f, 18f, 18f);
                if (Widgets.ButtonImage(infoRect, TexButton.Info)) LoadoutUtility.OpenBillsTab(e.bench);
                TooltipHandler.TipRegion(infoRect, "CER_SupplyLoadouts_OpenBillsTip".Translate());
            }
            else
            {
                // Icon + tooltip, no inline text - see DrawWeaponEntry's identical fix above.
                // RED, not orange: "no suitable workbench available" is not a hint in this column but a hard
                // block - no bill can be created. This matches the color rule that already applies to durable
                // items: impossible is red, scarce is orange.
                LoadoutUtility.DrawWarningBadge(BenchWarnRect(y), LoadoutUtility.ColBad);
                TooltipHandler.TipRegion(new Rect(BenchX, y, 200f, 24f), "CER_SupplyLoadouts_NoBenchWithTypeLabel".Translate(LoadoutUtility.BenchTypeNames(e.resolved)));
            }

            y += RowH;
        }

        // Sum of every ingredient the current selection would consume (weapons and ammo alike, batches included) -
        // mirrors GenerateAll's batch math exactly so the strip never promises a different bill than gets queued.
        private Dictionary<ThingDef, int> ComputeTotalCost()
        {
            var costs = new Dictionary<ThingDef, int>();
            void Add(List<ThingDefCountClass> ings, int batches)
            {
                if (ings == null || batches <= 0) return;
                foreach (ThingDefCountClass ing in ings)
                    if (ing != null && ing.thingDef != null)
                        costs[ing.thingDef] = (costs.TryGetValue(ing.thingDef, out int c) ? c : 0) + ing.count * batches;
            }
            // The conditions here must be EXACTLY the same as in GenerateAll - that was the cause of an old
            // "Total cost" bug (finally traced to its cause after a long time of puzzlement).
            // The workbench check was missing: the cost bar counted every researched row, but GenerateAll skips
            // any row without a built (or since demolished) bench. A researched ammo without a suitable bench thus
            // went into the displayed sum and was then silently skipped - the preview promised material that was
            // never consumed. It affected both sides, weapons and ammo, because GenerateAll checks e.bench on both
            // and this calculation on neither.
            // If a condition is ever added here again, it must be added in both places.
            foreach (WeaponEntry e in mainEntries.Concat(secEntries).Concat(shieldEntries).Concat(packEntries))
            {
                if (!e.selected || !e.craftable || e.recipe == null) continue;
                if (e.bench == null || e.bench.Destroyed) continue;
                if (!int.TryParse(e.countStr, out int want) || want <= 0) continue;
                int yield = e.recipe.products != null && e.recipe.products.Count > 0 ? Mathf.Max(1, e.recipe.products[0].count) : 1;
                Add(e.ings, Mathf.CeilToInt((float)want / yield));
            }
            foreach (AmmoEntry e in ammoEntries)
            {
                if (!e.selected || e.recipe == null) continue;
                if (e.bench == null || e.bench.Destroyed) continue;
                if (!int.TryParse(e.countStr, out int want) || want <= 0) continue;
                int yield = LoadoutUtility.GetAmmoRecipeYield(e.resolved);
                Add(e.ings, yield > 0 ? Mathf.CeilToInt((float)want / yield) : 0);
            }
            return costs;
        }

        private void GenerateAll()
        {
            int weaponsAdded = 0;
            int ammoAdded = 0, ammoUpdated = 0;
            HashSet<Building_WorkTable> refresh = new HashSet<Building_WorkTable>();

            foreach (WeaponEntry e in mainEntries.Concat(secEntries).Concat(shieldEntries).Concat(packEntries))
            {
                if (!e.selected || !e.craftable || e.bench == null || e.bench.Destroyed) continue;
                if (!int.TryParse(e.countStr, out int want) || want <= 0) continue;
                int yield = e.recipe.products != null && e.recipe.products.Count > 0 ? e.recipe.products[0].count : 1;
                if (yield <= 0) yield = 1;
                int batches = Mathf.CeilToInt((float)want / yield);
                LoadoutUtility.AddWeaponBillOnce(e.bench, e.recipe, batches, e.stuff);
                weaponsAdded++;
                refresh.Add(e.bench);
                // Arm report: record what was ACTUALLY queued (after the player's deselections/count edits).
                // packEntries (isPackItem == true - e.g. a pack-sourced utility weapon like a smoke/flashbang
                // grenade, or since 2026-07-31 a pack's medicine/food/drug items too, see Dialog_AssignLoadouts.
                // OpenProducePopup) folds into this SAME weaponsQueued bucket - no separate "pack items queued"
                // bucket exists. Safe: every current reader of weaponsQueued (Dialog_ArmColony's report:
                // BuildMaterials via GetRecipeFor, and the generic "Production queued" row list) treats it as an
                // undifferentiated def->count bag already - nothing downstream assumes every key IsWeapon.
                if (armReport != null)
                    armReport.weaponsQueued[e.weapon] = (armReport.weaponsQueued.TryGetValue(e.weapon, out int prev) ? prev : 0) + want;
            }

            foreach (AmmoEntry e in ammoEntries)
            {
                if (!e.selected || e.recipe == null || e.bench == null || e.bench.Destroyed) continue;
                if (!int.TryParse(e.countStr, out int want) || want <= 0) continue;
                // The bill's target is passed AS TYPED, not rounded up to a whole batch (2026-07-30) - CE's
                // own TargetCount mode ("craft until stock >= X") already handles any X fine, exactly like a
                // vanilla "craft simple meals until 50 in stock" bill isn't bound to the cooking yield per
                // batch either. Rounding here used to silently replace the player's real target (e.g. typing
                // 50 with a 200-yield recipe actually queued a bill targeting 200) - Count is the authoritative
                // value now; Batches (in the UI) is only ever a quick-fill helper for typing it.
                // Arm-my-Colony's own ammo-bills step (ArmAutoGenerateMissing) runs BEFORE Assign, scoped to
                // ammo already-assigned pawns need - on a brand-new colony that's legitimately empty (nobody
                // is assigned yet), so it reports "Ammo bills created: 0" by design, not by bug. The REAL need
                // then surfaces here, in the Assign step's own supply popup, moments later in the SAME run -
                // but this popup's GenerateAll only ever recorded the queued AMOUNT into armReport.ammoQueued,
                // never into armReport.ammoBillsCreated (the report's own headline number), so a fresh colony
                // showed a flatly wrong "Ammo bills created: 0 - nothing was missing" even while the very same
                // report's "Production queued" section, a few lines down, listed real ammo bills that this
                // exact popup had just created (2026-07-31: it looked as if no ammo bill could be made at
                // all - traced to this reporting gap, not a functional one;
                // the bills themselves were always created correctly). Counting Added (not Updated) here
                // matches ArmAutoGenerateMissing's own "created" semantics exactly.
                switch (LoadoutUtility.AddOrUpdateAmmoBill(e.bench, e.recipe, want, onlyIncrease: false))
                {
                    case LoadoutUtility.BillChange.Added: ammoAdded++; refresh.Add(e.bench); if (armReport != null) armReport.ammoBillsCreated++; break;
                    case LoadoutUtility.BillChange.Updated: ammoUpdated++; refresh.Add(e.bench); break;
                }
                if (armReport != null)
                    armReport.ammoQueued[e.resolved] = (armReport.ammoQueued.TryGetValue(e.resolved, out int prev) ? prev : 0) + want;
            }

            // What could NOT go into a bill at all (no suitable workbench, no recipe, not researched) - separate
            // from "the player deselected it". The Arm-my-Colony report used to know only two cases, cancelled
            // and "- nothing needed -", and therefore claimed "nothing needed" in a colony without any
            // workbench, while the step right before listed 3 pavises and 28 meals as missing.
            if (armReport != null)
                armReport.prodBlocked =
                    mainEntries.Concat(secEntries).Concat(shieldEntries).Concat(packEntries)
                        .Count(e => !e.craftable || e.bench == null || e.bench.Destroyed)
                    + ammoEntries.Count(e => e.recipe == null || e.bench == null || e.bench.Destroyed);

            foreach (Building_WorkTable b in refresh) LoadoutUtility.RefreshBillsTab(b);
            // The real central "something just got queued" moment for EVERY path that reaches this popup
            // (Arm my Colony, manual Assign, Squad Setup's own supply preview, the end-of-session missing-
            // production check) - not just the Arm-my-Colony chain, which is all Dialog_ArmReport's own
            // Invalidate() call covered before (2026-08-01 follow-up fix - a review
            // found the readiness badge still went stale after confirming THIS popup directly, outside the
            // full Arm-my-Colony run).
            CombatReadiness.Invalidate();

            List<string> parts = new List<string>();
            if (weaponsAdded > 0) parts.Add("CER_SupplyLoadouts_WeaponBillsAddedPart".Translate(weaponsAdded.ToString()).ToString());
            if (ammoAdded > 0) parts.Add("CER_SupplyLoadouts_AmmoBillsAddedPart".Translate(ammoAdded.ToString()).ToString());
            if (ammoUpdated > 0) parts.Add("CER_SupplyLoadouts_AmmoBillsUpdatedPart".Translate(ammoUpdated.ToString()).ToString());
            Messages.Message(parts.Count > 0 ? "CER_SupplyLoadouts_SupplySummary".Translate(string.Join(", ", parts.ToArray())).ToString() : "CER_SupplyLoadouts_NothingSelected".Translate().ToString(),
                parts.Count > 0 ? MessageTypeDefOf.TaskCompletion : MessageTypeDefOf.RejectInput, false);
        }
    }
}
