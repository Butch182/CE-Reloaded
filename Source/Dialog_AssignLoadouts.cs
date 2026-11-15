using System;
using System.Collections.Generic;
using System.Linq;
using RimWorld;
using Verse;
using UnityEngine;
using CombatExtended;
using static CEReloaded.LoadoutUtility;

namespace CEReloaded
{
    // Loads its own static Texture2D fields (e.g. MassBulkBarTex) - RimWorld's own asset-loading safety
    // check requires this attribute to guarantee that loading happens on the main thread (2026-08-01,
    // flagged by RimWorld's own startup log: "probably needs a StaticConstructorOnStartup attribute").
    [StaticConstructorOnStartup]
    public class Dialog_AssignLoadouts
    {
        public Dialog_CEReloadedTabs parentWindow;
        // Separate per-view-mode (2026-08-01) - card and compact rows have very different heights, so a
        // single shared raw pixel offset landed on an unrelated part of the roster whenever the player
        // toggled compactView; each mode now keeps its own scroll position instead.
        private Vector2 cardScrollPosition;
        private Vector2 compactScrollPosition;

        private List<Pawn> cachedPawns;
        private Dictionary<Pawn, float> rangedScores = new Dictionary<Pawn, float>();
        private Dictionary<Pawn, float> meleeScores = new Dictionary<Pawn, float>();
        private Dictionary<Pawn, string> traitStrings = new Dictionary<Pawn, string>();
        private Dictionary<Pawn, string> traitTooltipStrings = new Dictionary<Pawn, string>();
        private Dictionary<Pawn, string> capacityTooltips = new Dictionary<Pawn, string>();
        // Per-frame (not per-row) fulfillment cache - one map scan per drawn frame instead of one per row.
        private Dictionary<ThingDef, int> frameOwnedCache;
        // Shared, DECREMENTING view of frameOwnedCache (2026-08-01) - SingleFulfillStatus's non-ammo branch
        // used to just check frameOwnedCache[td] > 0, a flat colony-wide total with no notion of "already
        // claimed by an earlier pawn this same frame", so every pawn independently checking the same shared
        // item (e.g. a satchel-charge sidearm) saw the identical raw count and ALL of them read "Stored"
        // even when stock only covered a few (observed: 7 pawns, 2 in stock, every one showed "in
        // storage"). frameFulfillCache memoizes the per-(pawn,def) RESULT so a row that calls this twice for
        // the same item (DrawFulfillDot: once for the segment color, once for the tooltip text) doesn't
        // double-decrement its own single claim.
        private Dictionary<ThingDef, int> frameRemainingCache;
        private Dictionary<(Pawn, Def, int), FulfillStatus> frameFulfillCache;
        // The breakdown per (colonist, item, need, source) - built ONCE for both the bar and the tooltip (see
        // RichSegCached). Lives in the same 0.3 s block as the caches above.
        private Dictionary<(Pawn, Def, int, RichStock), LoadoutUtility.AvailabilitySegments> frameRichSegCache;

        // Carry and bulk limits per colonist, computed ONCE per frame instead of per row per frame (finding 4
        // of the first performance pass). Both values come from RimWorld's StatWorker.GetValue, which per the
        // decompiled source has NO cache of its own: it walks the pawn's stat parts (apparel, health, hediffs)
        // every time, and in dev mode additionally runs IsDisabledFor. Both views need them per visible row -
        // and twice per row, since the mass and bulk bars query separately.
        // Frame cache like its three neighbours above, for the same reason: the values CAN change (a colonist
        // puts something on, gets injured), but not within a single frame. Cleared in the same place as the
        // other three.
        // The StatDef itself lives in LoadoutUtility.CarryBulkStat - previously this did a DefDatabase lookup
        // by the STRING "CarryBulk" twice, also per row per frame.
        private readonly Dictionary<Pawn, (float mass, float bulk)> frameCapCache = new Dictionary<Pawn, (float, float)>();

        // What a colonist's ammo cell shows - computed ONCE per frame instead of per cell per row (finding 2
        // of the first performance pass). This is the most expensive build in the whole row:
        // `GetLoadout().Slots` rebuilds the union of own and inherited slots for every anchored colonist -
        // practically everyone with an assignment from this mod - `.ToList()` copies it, `RelevantAmmoPool`
        // walks every weapon in it, and the `active` filter is O(pool x slots) with a closure. Both views do
        // this per colonist.
        // Frame cache like its neighbours: the state can change (switching ammo, attaching a sidearm), but
        // not within a frame. Cleared in the same place as the others.
        private class AmmoCellState
        {
            public List<LoadoutSlot> effective;
            public List<ThingDef> pool;
            public List<ThingDef> active;
        }

        private readonly Dictionary<Pawn, AmmoCellState> frameAmmoCache = new Dictionary<Pawn, AmmoCellState>();

        // The FRAME the pure caches above were built for - see DoWindowContents. -1 so the first call after
        // opening is guaranteed to rebuild (Time.frameCount is never negative).
        private int frameCacheStamp = -1;
        // Lifetime of the row caches beyond a single frame - see the comment at the rebuild in
        // DoWindowContents.
        private int rowCacheVersion = -1;
        private float rowCacheAt = -99f;
        private const float RowCacheMaxAge = 0.3f;

        // The three expensive derivations EVERY row needs - mass/bulk, main and sidearm weapon, "is a pack
        // attached" (measured at 167 ms/s for 725 compact-row calls/s, i.e. 230 microseconds per row).
        // All three go through `Loadout.Slots`, which for every anchored colonist is not a field but a rebuild
        // of the union of own and inherited slots - PER ACCESS. On top of that, LoadoutMainAndSide builds four
        // intermediate LINQ lists and GetExtraParents another. This used to run three times per row per
        // PASS, and RimWorld sends at least two passes per frame.
        // Exactly the same reasoning as frameAmmoCache/frameCapCache next to it - and the same limit: the
        // values CAN change (swapping a template, attaching a pack), but not within a frame.
        // Both views read from it; otherwise only half the call sites would be covered.
        private struct RowInfo
        {
            public float mass, bulk;
            public ThingDef mainW, sideW;
            public bool hasPacks;
            // The neighbouring cells - same reasoning, one level deeper: GetPawnPacks/GetPawnSidearms
            // go through MultiParentUtility.GetExtraParents (parsing + three LINQ lists), CarriedPackSlots
            // additionally through a role's flat union. That happens three times in the same row
            // (pack cell, its role branch, sidearm cell) and used to run per pass.
            // READ ONLY - these lists are shared. Code that wants to modify one fetches its own from the
            // utility (the click paths do that anyway; they run outside of drawing).
            public List<Loadout> packs;
            public List<LoadoutSlot> packSlots;
            public Dictionary<Def, int> packNeeded;
            public List<Loadout> sidearms;
            public List<ThingDef> sidearmIcons;
            // The displayed loadout (template/role, not the anchor) and the entries the WEAPON cell
            // represents: for a CE-native loadout with a rifle, gladius, meals and medicine, the cell used
            // to show only the rifle - everything else had no cell at all.
            public Loadout display;
            public List<WeaponCellItem> weaponCellItems;
        }

        private struct WeaponCellItem { public Def def; public RichStock kind; public int need; }

        private readonly Dictionary<Pawn, RowInfo> frameRowCache = new Dictionary<Pawn, RowInfo>();

        private RowInfo GetRowInfo(Pawn pawn)
        {
            if (pawn != null && frameRowCache.TryGetValue(pawn, out RowInfo hit)) return hit;
            RowInfo info = new RowInfo();
            Loadout assigned = pawn?.GetLoadout();
            Loadout display = AmmoAssignmentUtility.DisplayLoadout(assigned);
            info.hasPacks = MultiParentUtility.GetExtraParents(assigned).Any(EquipmentPackUtility.IsEquipmentPackLoadout);
            // The ANCHOR for mass/bulk, the WEAPON TEMPLATE for the icons - exactly the split that used to
            // live at both call sites (see the comment at the compact row's mass bar).
            if (assigned != null && !assigned.defaultLoadout)
                LoadoutUtility.GetLoadoutMassBulk(assigned, out info.mass, out info.bulk);
            if (display != null && !display.defaultLoadout)
                LoadoutUtility.LoadoutMainAndSide(display, out info.mainW, out info.sideW);
            info.packs = AmmoAssignmentUtility.GetPawnPacks(pawn) ?? new List<Loadout>();
            info.packSlots = AmmoAssignmentUtility.CarriedPackSlots(pawn) ?? new List<LoadoutSlot>();
            info.packNeeded = NeededCountsFrom(info.packSlots);
            info.sidearms = SidearmAssignmentUtility.GetPawnSidearms(pawn) ?? new List<Loadout>();
            info.sidearmIcons = info.sidearms.Select(RepresentativeIcon).ToList();
            info.display = display;
            info.weaponCellItems = BuildWeaponCellItems(info, display);
            if (pawn != null) frameRowCache[pawn] = info;
            return info;
        }

        // Everything the weapon cell represents: the main weapon plus every OWN slot of the displayed loadout
        // that no neighbouring cell shows - ammo (including 'Any ammo for X') belongs to the ammo cell, pack
        // contents to the pack cell, sidearms to the sidearm cell. For our own templates (one weapon + ammo)
        // exactly the main weapon remains, so the cell looks as before; for a CE-native loadout the gladius,
        // meals and medicine are added. Once per 0.3 s (RowInfo), not per pass.
        private static List<WeaponCellItem> BuildWeaponCellItems(RowInfo info, Loadout display)
        {
            var list = new List<WeaponCellItem>();
            if (info.mainW != null) list.Add(new WeaponCellItem { def = info.mainW, kind = RichStock.Weapon, need = 1 });
            if (display == null || display.defaultLoadout || display.OwnSlots == null) return list;
            foreach (LoadoutSlot s in display.OwnSlots)
            {
                Def d = (Def)s.thingDef ?? s.genericDef;
                if (d == null || d == info.mainW) continue;
                if (LoadoutUtility.IsAmmoSlot(s)) continue;
                if (info.packSlots != null && info.packSlots.Any(p => ((Def)p.thingDef ?? p.genericDef) == d)) continue;
                if (s.thingDef != null && info.sidearmIcons != null && info.sidearmIcons.Contains(s.thingDef)) continue;
                bool dup = false;
                for (int i = 0; i < list.Count; i++) if (list[i].def == d) { dup = true; break; }
                if (dup) continue;
                bool weaponish = s.thingDef != null && (s.thingDef.IsWeapon || LoadoutUtility.IsShieldApparel(s.thingDef));
                list.Add(new WeaponCellItem { def = d, kind = weaponish ? RichStock.Weapon : RichStock.Item, need = Mathf.Max(1, s.count) });
            }
            return list;
        }

        // Preformatted labels per colonist: skill bars, mass/bulk and the breakdown used to rebuild their
        // text in EVERY pass (Translate with arguments, F1 formatting, string.Format, plus five skill/capacity
        // queries) - measured at 45 to 76 ms/s per section, for values that never change between two frames.
        // Lifetime like frameRowCache (version counter + 0.3 s), cleared in the same place.
        private struct RowLabels { public string rangedCard, meleeCard, rangedCompact, meleeCompact, mass, bulk, b1, b2; }
        private readonly Dictionary<Pawn, RowLabels> frameLabelCache = new Dictionary<Pawn, RowLabels>();

        private RowLabels GetRowLabels(Pawn pawn)
        {
            if (frameLabelCache.TryGetValue(pawn, out RowLabels hit)) return hit;
            RowInfo info = GetRowInfo(pawn);
            GetCapacities(pawn, out float capMass, out float capBulk);
            float rScore = rangedScores.TryGetValue(pawn, out float rs) ? rs : 0f;
            float mScore = meleeScores.TryGetValue(pawn, out float ms) ? ms : 0f;
            float shootingSkill = pawn.skills?.GetSkill(SkillDefOf.Shooting)?.Level ?? 0;
            float meleeSkill = pawn.skills?.GetSkill(SkillDefOf.Melee)?.Level ?? 0;
            float sight = pawn.health.capacities.GetLevel(PawnCapacityDefOf.Sight);
            float manipulation = pawn.health.capacities.GetLevel(PawnCapacityDefOf.Manipulation);
            float moving = pawn.health.capacities.GetLevel(PawnCapacityDefOf.Moving);
            RowLabels l = new RowLabels
            {
                rangedCard = LoadoutUtility.ScoreBarLabel(rScore, "CER_AssignLoadouts_RangedLabel".Translate()),
                meleeCard = LoadoutUtility.ScoreBarLabel(mScore, "CER_AssignLoadouts_MeleeLabel".Translate()),
                rangedCompact = LoadoutUtility.ScoreBarLabel(rScore, "CER_AssignLoadouts_RangedAbbrev".Translate()),
                meleeCompact = LoadoutUtility.ScoreBarLabel(mScore, "CER_AssignLoadouts_MeleeAbbrev".Translate()),
                mass = "CER_AssignLoadouts_MassBarFmt".Translate(info.mass.ToString("F1"), capMass.ToString("F1")).ToString(),
                bulk = string.Format("{0:F1}/{1:F1}", info.bulk, capBulk),
                b1 = "CER_AssignLoadouts_BreakdownRangedFmt".Translate(shootingSkill.ToString(), Mathf.RoundToInt(sight * 100f).ToString(), Mathf.RoundToInt(manipulation * 100f).ToString()).ToString(),
                b2 = "CER_AssignLoadouts_BreakdownMeleeFmt".Translate(meleeSkill.ToString(), Mathf.RoundToInt(sight * 100f).ToString(), Mathf.RoundToInt(moving * 100f).ToString(), Mathf.RoundToInt(manipulation * 100f).ToString()).ToString(),
            };
            frameLabelCache[pawn] = l;
            return l;
        }

        private AmmoCellState GetAmmoCellState(Pawn pawn)
        {
            if (pawn != null && frameAmmoCache.TryGetValue(pawn, out AmmoCellState hit)) return hit;
            List<LoadoutSlot> effective = pawn?.GetLoadout()?.Slots?.ToList() ?? new List<LoadoutSlot>();
            List<ThingDef> pool = Dialog_ChangeAmmo.RelevantAmmoPool(effective);
            List<ThingDef> active = pool.Where(d => effective.Any(s => s.thingDef == d)).ToList();
            // A generic entry ("Any ammo for X") IS a chosen ammo selection: previously only CONCRETE ammo
            // counted here, so a colonist on "Any ammo" fell into the warning branch and the cell claimed
            // they had nothing to shoot - the exact opposite of what the player had just set.
            // Resolved to the concrete default type (ResolveGenericAmmo), not merely "something is
            // set": the same resolution the mod uses to plan production
            // (MergeResolvedGenericAmmoDemand). This keeps the icon, the counter and the availability
            // breakdown of this cell consistent with what actually gets procured.
            foreach (LoadoutSlot s in effective)
            {
                if (!LoadoutUtility.IsGenericAmmoDef(s.genericDef)) continue;
                ThingDef resolved = LoadoutUtility.ResolveGenericAmmo(s.genericDef as LoadoutGenericDef);
                if (resolved != null && !active.Contains(resolved)) active.Add(resolved);
            }
            AmmoCellState state = new AmmoCellState { effective = effective, pool = pool, active = active };
            if (pawn != null) frameAmmoCache[pawn] = state;
            return state;
        }

        // How many rounds of this type this colonist has planned. Concrete slots are summed (two slots of
        // the same type together make up the need); if there are none, the count comes from the generic
        // slot that resolves to exactly this type ("Any ammo for X"). This is the same resolution
        // GetAmmoCellState used to put the type into the list in the first place - without this second
        // path there would be no count for it.
        private static int AmmoCountFor(List<LoadoutSlot> effective, ThingDef d)
        {
            if (effective == null || d == null) return 0;
            int direct = effective.Where(s => s.thingDef == d).Sum(s => s.count);
            if (direct > 0) return direct;
            foreach (LoadoutSlot s in effective)
            {
                if (!LoadoutUtility.IsGenericAmmoDef(s.genericDef)) continue;
                if (LoadoutUtility.ResolveGenericAmmo(s.genericDef as LoadoutGenericDef) == d) return s.count;
            }
            return 0;
        }

        private void GetCapacities(Pawn pawn, out float capMass, out float capBulk)
        {
            if (pawn != null && frameCapCache.TryGetValue(pawn, out var hit)) { capMass = hit.mass; capBulk = hit.bulk; return; }
            // CE's CarryWeight, not vanilla's CarryingCapacity - see LoadoutUtility.CarryWeightOf.
            capMass = LoadoutUtility.CarryWeightOf(pawn);
            StatDef bulkStat = LoadoutUtility.CarryBulkStat;
            capBulk = bulkStat != null && pawn != null ? pawn.GetStatValue(bulkStat) : 20f;
            if (capMass <= 0f) capMass = 35f;
            if (capBulk <= 0f) capBulk = 20f;
            if (pawn != null) frameCapCache[pawn] = (capMass, capBulk);
        }

        // 330, not the old 360 - at the tabs window's normal width (~1032 usable), 360 only fit 2 cards per
        // row; 330 reliably fits 3 (the "3 per row" the card grid is meant to read as) while still leaving
        // the Mass/Bulk bars and the Ammo/Pack/Sidearm cell row enough width to stay readable.
        private const float CardWidth = 330f;
        // Compact view's Weapon column start - right after the Melee score bar (326 + 80 wide + 6 gap),
        // shared between DrawCompactHeader and DrawPawnRowCompact so they can never drift apart again.
        // 412 -> 398: a few pixels could be saved after the Save button - there were 26px of slack between
        // the Save button (ends at 214) and the first skill bar. Both bars move 14 to the left, and the
        // weapon column gains exactly those 14px.
        // 398 -> 372: in the compact view, long weapon names ran past the button, so the colonist column
        // is a bit narrower. The name column gives up 26 points (130 -> 104), and the role button and both
        // skill bars move by exactly the same 26 - every cell right of the weapon button stays where it
        // was (nothing else moves).
        private const float WeaponColX = 372f;
        // Shrunk by 21 - the old separate "Equipped: <weapon>" detail row is gone (see DrawWeaponStatusDot),
        // replaced by a corner dot on the loadout button itself, same as Ammo/Pack/Sidearm's own dots.
        private static float CardHeight => 205f;
        private const float Margin = 10f;

        private static readonly Texture2D MassBulkBarTex = SolidColorMaterials.NewSolidColorTexture(new Color(0f, 0.8f, 0.8f));
        // Over capacity - dark red instead of teal, so the bar looks different at a glance and the
        // numbers don't have to be read first.
        private static readonly Texture2D OverCapBarTex = SolidColorMaterials.NewSolidColorTexture(new Color(0.75f, 0.18f, 0.18f));

        // Mass/bulk labels on the colonist card, translated and measured once instead of per card per
        // frame. The 32f/30f rects were too narrow for the German abbreviations and Widgets.Label clips
        // hard. The language only changes with a restart - RimWorld's in-game language switch is broken
        // anyway.
        private static string cardMassCaption, cardBulkCaption;
        private static float cardMassCaptionW, cardBulkCaptionW;

        private AssignLoadoutFilter loadoutFilter = AssignLoadoutFilter.AvailableMainWeapon;
        // Always on now (2026-07-30) - these used to be player-facing checkboxes ("Produce missing weapons"/
        // "...main weapons"/"...sidearms"), removed: the end-of-session production check
        // (LoadoutUtility.CheckAndOpenMissingProductionPopup)
        // covers this need automatically now regardless, so there is no longer a reason to let a player turn
        // OFF the immediate post-apply check - always including everything is simply the safer default, and
        // every popup this still feeds is still fully reviewable/cancellable before anything is queued.
        internal const bool produceMissing = true;
        internal const bool produceMainWeapons = true;
        internal const bool produceSidearms = true;
        // Three values instead of two - see RoleAssignMode.
        internal RoleAssignMode roleMode = RoleAssignMode.Mixed;
        // The role toggle as it stood BEFORE an Arm-my-Colony run (without this, after Arm my Colony only
        // roles could be picked anywhere).
        //
        // Cause: the wizard's root question ("work with roles?") is written into exactly this field as
        // roleModeOverride - and DoWindowContents mirrors the field into the save (ui.asRoleMode) every
        // frame. That turned an answer FOR THIS RUN into the player's permanent setting: the weapon picker
        // (Dialog_SquadSetup.OpenPinMenu) builds its weapon list from the same toggle and afterwards
        // showed only roles everywhere.
        // While this field is set, a run with a forced mode is in progress: mirroring into the save is
        // paused, and at the end of the run (FinishArmReport - both apply and cancel go through it) the
        // old value is restored.
        private RoleAssignMode? armRoleModeRestore;
        private bool sortByMelee = false;   // pawn grid sort: ranged score (default) vs melee score
        private bool compactView = false;   // full cards vs one slim table row per colonist
        private string pawnSearch = "";

        // Compact view's own column-header sort (2026-08-01), mirroring the currentSortMode/sortDescending +
        // ColumnHeader pattern already used by Dialog_SupplyLoadouts/Dialog_GenerateAmmoBills. Only "Name" is
        // offered - Ranged/Melee already have their own dedicated sort radios above the grid (SortRadio), and
        // a Weapon/Ammo/Pack/Sidearm fulfillment-status sort was deliberately left out: SingleFulfillStatus's
        // "Stored" branch DECREMENTS a shared per-frame stock pool the first time each (pawn, def) pair is
        // resolved (see frameRemainingCache), so which pawn "claims" limited stock depends on resolution
        // order - sorting by that same status would make the sort itself decide the claim order, which is not
        // a clean, well-defined ordering. Card view is intentionally NOT sorted by this - it has no column
        // header row to click (see DoWindowContents' compactView-only branch below), and its own scroll order
        // already comes from the same rank/score sort every view has always used.
        private enum SortMode { Default, Name, Ranged, Melee }
        private SortMode currentSortMode = SortMode.Default;
        private bool sortDescending = false;

        // Same toggle-direction-on-repeat-click convention as the reference dialogs; unlike theirs there is no
        // persistent entry list to re-sort here (compact rows are re-sorted fresh from cachedPawns every frame
        // in DoWindowContents), so flipping the fields is the whole job.
        private void SetSort(SortMode mode)
        {
            if (currentSortMode == mode) sortDescending = !sortDescending;
            else { currentSortMode = mode; sortDescending = false; }
        }

        // Lets an external jump-in (e.g. the native "Show in loadout editor" FloatMenu entry) pre-fill the
        // pawn search box, so the tab opens already filtered to one specific colonist instead of the whole grid.
        public void SetPawnSearch(string s) => pawnSearch = s ?? "";

        // Drag-and-drop loadout swap between two pawns (card/compact grid) - dragPawn != null IS "a drag is in
        // progress". Same raw Input-polling mechanism as Dialog_SquadSetup's (confirmed working there): IMGUI
        // EventType.MouseDown/MouseUp checks did not reliably fire for this kind of press-drag-release here either.
        private Pawn dragPawn;

        // Set only for the duration of an Arm-my-Colony run: the running tally that the final overview window shows.
        private ArmReport armReport;

        public Dialog_AssignLoadouts()
        {
            // Seed from the per-save UI state (itself seeded from mod-settings for a fresh colony), so a player's choices
            // persist across window opens and reloads.
            CERUiState ui = RoleUtility.Ui();
            loadoutFilter = ui.asLoadoutFilter;
            roleMode = ui.asRoleMode;
            sortByMelee = ui.asSortMelee;
            compactView = ui.asCompact;
        }

        // --- Accessors for the auto-assign section of Dialog_SquadSetup ---
        internal List<Pawn> CachedPawns { get { if (cachedPawns == null) RefreshPawns(); return cachedPawns; } }
        internal float RangedScoreOf(Pawn p) => rangedScores.TryGetValue(p, out float v) ? v : 0f;
        internal float MeleeScoreOf(Pawn p) => meleeScores.TryGetValue(p, out float v) ? v : 0f;

        // Splits the weapon shortfall of a set of assigned loadouts into MAIN weapons vs. SECONDARY (sidearms +
        // grenades). Owned stock is counted once against the combined need; a def that is a main weapon in ANY loadout
        // is reported in the main bucket (crafting it also covers its sidearm use). Utility items are ignored.
        // Static (no instance state involved) so the consolidated Readiness alert can build a Supply request
        // without needing a live Dialog_AssignLoadouts instance.
        internal static void ComputeMissingSplit(IEnumerable<Loadout> loadouts, IEnumerable<Loadout> sidearmLoadouts,
            out Dictionary<ThingDef, int> mainMissing, out Dictionary<ThingDef, int> secMissing, out Dictionary<ThingDef, int> shieldMissing,
            IEnumerable<LoadoutSlot> extraSidearmSlots = null)
            => ComputeMissingSplit(loadouts, sidearmLoadouts, BuildOwnedWeaponCounts(), out mainMissing, out secMissing, out shieldMissing, extraSidearmSlots);

        // Split each loadout's weapon needs into main-weapon, sidearm and SHIELD shortfalls, given an "available"
        // pool. Auto-assign passes the whole owned pool (it redistributes everything); a single manual assignment
        // passes only free storage + that pawn's own weapons, so a weapon another colonist is already holding
        // doesn't hide the shortfall. Shields are apparel, not weapons, so they're tracked in their own bucket
        // (plan section 6) rather than folded into secondary weapons.
        //
        // sidearmLoadouts (2026-08-01) - THE single shared "is this covered" method now, used by every screen
        // that needs it (Supply popup via OpenProducePopup, the standalone Readiness alert via
        // BuildSupplyRequestForPawns, and Dialog_SquadSetup's own assign-preview badges via
        // BuildPlanWideShortfalls) instead of each one hand-rolling its own demand-aggregation - the SAME
        // "stock checked without subtracting what other consumers already claimed" bug had been fixed
        // independently in three different places before landing here (goal: one availability logic).
        // Sidearm-attached loadouts are folded into the SAME needs/shieldNeeds
        // totals as `loadouts` (so stock is subtracted once across every consumer of a shared def) but
        // deliberately NEVER run through AnalyzeLoadout - a sidearm loadout's own gun is what a pawn carries
        // AS a sidearm, not their "main weapon" pick, so it must never populate mainDefs on its own (that
        // would misclassify an otherwise-unrelated sidearm-only def as a "main weapons" shortfall instead of
        // "sidearms"). Optional/nullable so existing single-list callers don't need to change.
        internal static void ComputeMissingSplit(IEnumerable<Loadout> loadouts, IEnumerable<Loadout> sidearmLoadouts, Dictionary<ThingDef, int> owned,
            out Dictionary<ThingDef, int> mainMissing, out Dictionary<ThingDef, int> secMissing, out Dictionary<ThingDef, int> shieldMissing,
            IEnumerable<LoadoutSlot> extraSidearmSlots = null)
        {
            Dictionary<ThingDef, int> needs = new Dictionary<ThingDef, int>();
            Dictionary<ThingDef, int> shieldNeeds = new Dictionary<ThingDef, int>();
            HashSet<ThingDef> mainDefs = new HashSet<ThingDef>();
            void FoldSlot(LoadoutSlot slot)
            {
                ThingDef w = slot.thingDef;
                if (w == null) return;
                int n = Mathf.Max(1, slot.count);
                if (LoadoutUtility.IsShieldApparel(w)) shieldNeeds[w] = (shieldNeeds.TryGetValue(w, out int sc) ? sc : 0) + n;
                else if (w.IsWeapon && !LoadoutUtility.IsUtilityWeapon(w)) needs[w] = (needs.TryGetValue(w, out int c) ? c : 0) + n;
            }
            void FoldSlots(Loadout lo)
            {
                if (lo == null || lo.Slots == null) return;
                foreach (LoadoutSlot slot in lo.Slots) FoldSlot(slot);
            }
            foreach (Loadout lo in loadouts)
            {
                if (lo == null || lo.Slots == null) continue;
                if (AnalyzeLoadout(lo, out ThingDef m, out _, out _, out _)) mainDefs.Add(m);
                FoldSlots(lo);
            }
            if (sidearmLoadouts != null)
                foreach (Loadout lo in sidearmLoadouts) FoldSlots(lo);
            // Virtual sidearm/grenade picks (lazy rework): during planning no template object exists yet,
            // but the demand of their slots (the weapon itself; a shield can never occur here) must go into
            // the SAME pools, otherwise it is missing from every shortfall display.
            if (extraSidearmSlots != null)
                foreach (LoadoutSlot slot in extraSidearmSlots) FoldSlot(slot);

            mainMissing = new Dictionary<ThingDef, int>();
            secMissing = new Dictionary<ThingDef, int>();
            shieldMissing = new Dictionary<ThingDef, int>();
            foreach (var kv in needs)
            {
                int have = owned.TryGetValue(kv.Key, out int o) ? o : 0;
                int miss = kv.Value - have;
                if (miss <= 0) continue;
                if (mainDefs.Contains(kv.Key)) mainMissing[kv.Key] = miss;
                else secMissing[kv.Key] = miss;
            }
            foreach (var kv in shieldNeeds)
            {
                int have = owned.TryGetValue(kv.Key, out int o) ? o : 0;
                int miss = kv.Value - have;
                if (miss > 0) shieldMissing[kv.Key] = miss;
            }
        }

        // Weapon/shield def -> names of the pawns whose (planned or current) loadout contains it, for the supply
        // popup's Assig. column. Covers mains, sidearms, grenades and shields alike.
        // internal: this is THE one answer to "who has this weapon assigned", and the ammo tab needs it
        // too. Previously that tab used ScanMissingAssignedWeapons - which answers a DIFFERENT question: who
        // is still MISSING it. Anyone already carrying it never shows up there, which is why an earlier fix
        // only took effect on one of the two paths.
        internal static Dictionary<ThingDef, List<string>> BuildWeaponUsedBy(IEnumerable<(Loadout loadout, string name)> pairs)
        {
            var map = new Dictionary<ThingDef, List<string>>();
            foreach (var (lo, name) in pairs)
            {
                if (lo == null || lo.defaultLoadout || lo.Slots == null) continue;
                foreach (LoadoutSlot slot in lo.Slots)
                {
                    ThingDef w = slot.thingDef;
                    if (w == null) continue;
                    if (!LoadoutUtility.IsShieldApparel(w) && (!w.IsWeapon || LoadoutUtility.IsUtilityWeapon(w))) continue;
                    if (!map.TryGetValue(w, out List<string> l)) map[w] = l = new List<string>();
                    if (!l.Contains(name)) l.Add(name);
                }
            }
            return map;
        }

        // Builds a full Supply request (weapons + shields + ammo, all filtered to researched/producible-relevant
        // entries) for an arbitrary set of pawns' CURRENT loadouts - the entry point the consolidated Readiness
        // alert (and anything else outside the Assign tab's own flow) uses to open Dialog_SupplyLoadouts without
        // needing a live Dialog_AssignLoadouts instance. Returns null if there's nothing to supply.
        internal static Dialog_SupplyLoadouts.Request BuildSupplyRequestForPawns(IEnumerable<Pawn> pawns)
        {
            List<Pawn> pawnList = pawns.Where(p => p != null).ToList();

            // The weapon and shield shortfall comes from WeaponShortfallsByPawn, NO LONGER from
            // ComputeMissingSplit against colony-wide stock:
            // this entry point is used exclusively from the readiness display, and that reports per
            // colonist in turn - "nothing was left over for this colonist". Colony-wide stock, by contrast,
            // also counts every weapon someone else is already carrying, and therefore said "nothing is
            // missing" for exactly the gap the window was opened for. See the header comment above
            // WeaponShortfallsByPawn.
            //
            // The CLASSIFICATION (main weapon vs. sidearm) still depends solely on the colonists'
            // templates: AnalyzeLoadout picks exactly one main weapon per loadout, and a sidearm must not
            // compete for that slot (otherwise the same weapons could land under 'Main weapons' on one
            // path and under 'Sidearms & grenades' on another).
            List<Loadout> mainLoadouts = pawnList
                .Select(p => AmmoAssignmentUtility.DisplayLoadout(p.GetLoadout()))
                .Where(l => l != null && !l.defaultLoadout).ToList();
            HashSet<ThingDef> mainDefs = new HashSet<ThingDef>();
            foreach (Loadout lo in mainLoadouts)
                if (AnalyzeLoadout(lo, out ThingDef m, out _, out _, out _)) mainDefs.Add(m);
            var mainMissing = new Dictionary<ThingDef, int>();
            var secMissing = new Dictionary<ThingDef, int>();
            var shieldMissing = new Dictionary<ThingDef, int>();
            Dictionary<Pawn, Dictionary<ThingDef, int>> shortfalls = WeaponShortfallsByPawn();
            foreach (Pawn p in pawnList)
            {
                if (!shortfalls.TryGetValue(p, out Dictionary<ThingDef, int> miss)) continue;
                foreach (var kv in miss)
                {
                    Dictionary<ThingDef, int> bucket = LoadoutUtility.IsShieldApparel(kv.Key) ? shieldMissing
                        : (mainDefs.Contains(kv.Key) ? mainMissing : secMissing);
                    bucket[kv.Key] = (bucket.TryGetValue(kv.Key, out int c) ? c : 0) + kv.Value;
                }
            }

            LoadoutUtility.ComputeAmmoNeedForAssignedPawns(pawnList, out var generics, out var specifics, out var assignedUsedBy);
            Dictionary<ThingDef, List<string>> weaponAssignedUsedBy = BuildWeaponUsedBy(pawnList.Select(p => (p.GetLoadout(), p.LabelShortCap)));
            // Circle indicator (used-in-loadout) is always computed too, independent of the (nonexistent, here)
            // scope toggle - see LoadoutUtility.CheckAndOpenMissingProductionPopup's own matching comment for
            // why the two indicators must never be scope-dependent (2026-08-02).
            LoadoutUtility.ComputeMissingForCreatedLoadouts(out _, out _, out Dictionary<ThingDef, List<string>> weaponTemplateUsedBy,
                out _, out var templatePackUsedBy, out _, out _, out Dictionary<Def, List<string>> templateUsedBy, out _);
            foreach (var kv in templatePackUsedBy) weaponTemplateUsedBy[kv.Key] = kv.Value;
            // Quantity-aware, not the old binary "any bill exists at all" gate (2026-08-01 fix, mirrors
            // LoadoutUtility.CheckAndOpenMissingProductionPopup's own same-shape fix from 2026-07-31, which
            // this sibling call site never got) - the old !WeaponHasActiveBill(kv.Key) suppressed the WHOLE
            // shortfall the instant ANY bill for that ammo existed anywhere, even a leftover 1-unit bill from
            // before more colonists got assigned it, silently under-proposing real demand on re-trigger.
            // + CarriedStockCount: the demand here is that of EVERY
            // assigned colonist, but the stock was only storage + running bills. What colonists carry in their
            // backpacks counted for nobody - and with ammo that is the normal case once they have been
            // equipped. The result was a proposal to craft ammo the colony already has, just on the colonists
            // instead of on the shelf. See LoadoutUtility.CarriedStockCount.
            var missingSpecifics = specifics.Where(kv => LoadoutUtility.IsAmmoResearched(kv.Key)
                && LoadoutUtility.AmmoOnHand(kv.Key) + LoadoutUtility.WeaponQueuedCount(kv.Key) < kv.Value)
                .ToDictionary(kv => kv.Key, kv => kv.Value);
            var relevantGenerics = generics.Where(kv =>
                LoadoutUtility.GetAmmoTypesFor(LoadoutUtility.GetWeaponForGenericAmmo(kv.Key)).Any(LoadoutUtility.IsAmmoResearched))
                .ToDictionary(kv => kv.Key, kv => kv.Value);

            // From here on the SAME post-processing as the Apply path (when opened via the readiness
            // display, the two 23x75mmR rows and the Pack items / Not producible sections were missing).
            // The cause was not a calculation error but an incomplete request: this entry point never filled
            // packItems, never ran the standing-bill top-up and never attached the obsolete check.
            // Three windows, the same question, three answers of varying completeness.
            LoadoutUtility.ScanMissingAssignedPackItems(
                pawnList.Select(p => (p, AmmoAssignmentUtility.GetPawnPacks(p))), out var missingPackItems, out var packUsedBy,
                out var packDemandDefs);
            // No IsProducible filter - same reasoning as
            // for weapons one branch further down: the window itself renders a non-producible row correctly,
            // whereas silently dropping it reads as "nothing is missing". That is exactly how the medicine
            // row disappeared even though the tooltip next to it said "Still missing: 42".
            var produciblePackItems = new Dictionary<ThingDef, int>(missingPackItems);
            foreach (var kv in packUsedBy) weaponAssignedUsedBy[kv.Key] = kv.Value;

            foreach (var kv in LoadoutUtility.ConsumablesNeedingMaintenance(specifics))
                if (!missingSpecifics.ContainsKey(kv.Key)) missingSpecifics[kv.Key] = kv.Value;
            foreach (var kv in LoadoutUtility.ConsumablesNeedingMaintenance(missingPackItems, requireConsumable: true))
                if (!produciblePackItems.ContainsKey(kv.Key)) produciblePackItems[kv.Key] = kv.Value;
            foreach (var kv in LoadoutUtility.ConsumablesNeedingMaintenance(mainMissing, requireConsumable: true))
                if (!mainMissing.ContainsKey(kv.Key)) mainMissing[kv.Key] = kv.Value;
            foreach (var kv in LoadoutUtility.ConsumablesNeedingMaintenance(secMissing, requireConsumable: true))
                if (!secMissing.ContainsKey(kv.Key)) secMissing[kv.Key] = kv.Value;

            var req = new Dialog_SupplyLoadouts.Request
            {
                mainWeapons = mainMissing,
                secondaryWeapons = secMissing,
                shields = shieldMissing,
                packItems = produciblePackItems,
                packItemDemandDefs = packDemandDefs,
                genericAmmo = relevantGenerics,
                specificAmmo = missingSpecifics,
                assignedUsedBy = assignedUsedBy,
                templateUsedBy = templateUsedBy,
                weaponAssignedUsedBy = weaponAssignedUsedBy,
                weaponTemplateUsedBy = weaponTemplateUsedBy
            };
            LoadoutUtility.AttachObsoleteCleanup(req);

            int total = mainMissing.Count + secMissing.Count + shieldMissing.Count + produciblePackItems.Count
                + relevantGenerics.Count + missingSpecifics.Count
                + req.obsoleteAmmoCandidates.Count + req.obsoleteWeaponCandidates.Count;
            return total == 0 ? null : req;
        }

        // Capacity bar (mass or bulk) with a visible overload warning. ONE function for the card view and
        // the compact row, so the two cannot drift apart.
        //
        // Principle: allow it, but show a clearly visible warning.
        // Previously an overweight loadout was only recognisable by two numbers in the bar (25.5/20.0), and
        // the bar itself looked cleanly filled because of Clamp01.
        //
        // Why the text does NOT say "overloaded": the colonist never becomes overloaded. CE's pickup job
        // checks CanFitInInventory before every item and even actively drops surplus again
        // (JobGiver_UpdateLoadout:135 and :314, verified in the decompiled source). What really happens is
        // quieter: the colonist never fetches the rest, the loadout stays permanently half-fulfilled, and
        // without this warning nothing says so. That is exactly why the warning is the fix rather than a
        // hard limit at assignment time: the bulk limit fluctuates with armor and injuries, so a hard limit
        // would, depending on the day, refuse items that would fit tomorrow.
        // On the right side of the bar: the icon of the worn item that raises THIS limit (backpack/vest
        // for bulk, exoframe for mass), with '...' if there are several; the label shifts accordingly.
        // See LoadoutUtility.CarryBoostersOf. The caller passes `boosters` on to the tooltip.
        private static bool DrawCapacityBar(Rect bar, float load, float cap, string label, Pawn pawn, bool bulk, out List<LoadoutUtility.CarryBooster> boosters)
        {
            bool over = cap > 0f && load > cap;
            boosters = LoadoutUtility.CarryBoostersOf(pawn, bulk);
            // Two-tone fill: the colonist's own body carrying capacity is the boundary between the two
            // tones - everything above it is only carried BECAUSE of the backpack. The same breakdown the
            // tooltip lists (base value + each item), so bar and text tell the same story.
            float natural = cap;
            foreach (LoadoutUtility.CarryBooster b in boosters) natural -= b.offset;
            LoadoutUtility.DrawCarryFillBar(bar, load, cap, Mathf.Max(0f, natural), over);
            float reserve = over ? 16f : 0f;
            reserve += LoadoutUtility.DrawCarryBoosterIcon(bar, boosters, reserve);
            // Label with its own contrast - see LoadoutUtility.BarLabel. The teal fill sat right under the
            // first digits, i.e. precisely under the CURRENT value.
            LoadoutUtility.BarLabel(bar, label, reserve);
            // The mod's shared warning sign, top right and half over the edge - the same geometry as on the
            // ingredient and workbench cells, so it reads as the same sign.
            // Entirely INSIDE the bar (with two lines the warning triangle used to hang half over the cell
            // border). On the ingredient cells the overhang goes unnoticed because there is room to the
            // right - at the right edge of the compact row there is none.
            if (over) LoadoutUtility.DrawWarningBadge(new Rect(bar.xMax - 16f, bar.y - 3f, 15f, 15f), LoadoutUtility.ColBad);
            return over;
        }

        // A carry-capacity bar for a FOREIGN view (the Mass/Bulk columns of the assignment preview):
        // the same bar, the same carrier icon, the same tooltip as in the compact view - just with numbers
        // the caller supplies (there the PLANNED state, not the current one). No second reimplementation.
        internal static void DrawPawnCapacityBar(Rect bar, float load, float cap, Pawn pawn, bool bulk)
        {
            string label = bulk
                ? string.Format("{0:F1}/{1:F1}", load, cap)
                : "CER_AssignLoadouts_MassBarFmt".Translate(load.ToString("F1"), cap.ToString("F1")).ToString();
            bool over = DrawCapacityBar(bar, load, cap, label, pawn, bulk, out List<LoadoutUtility.CarryBooster> boosters);
            if (LoadoutUtility.TipWanted(bar))
                TooltipHandler.TipRegion(bar, WithOverCapNote(LoadoutUtility.CarryCapacityTooltip(pawn, bulk, load, cap, boosters), over));
        }

        // ONE id per cell and colonist - stable across frames, distinct between cells. Without it a cell's
        // tooltip would be the same as its neighbour's as soon as both had the same text.
        private static int TipId(Pawn pawn, Def d, int salt)
            => (pawn != null ? pawn.thingIDNumber * 31 : 0) ^ (d != null ? d.shortHash * 17 : 0) ^ (salt << 24);

        private static string WithOverCapNote(string tip, bool over)
            => over ? tip + "\n\n" + "CER_AssignLoadouts_OverCapacityTip".Translate().ToString() : tip;

        private static bool BenchExistsFor(ThingDef w)
        {
            List<ThingDef> benchDefs = LoadoutUtility.GetWorkbenchesFor(w);
            if (benchDefs == null || benchDefs.Count == 0) return false;
            return LoadoutUtility.AllColonyBenches().Any(b => benchDefs.Contains(b.def));
        }

        // Builds and shows the ONE supply popup (weapons + shields + ammo) for
        // a set of (pawn, planned loadout) pairs. Secondary (sidearm/grenade) shortfall is only included in the
        // "...and sidearms" mode; skipped-superior info is only passed from Auto-Assign. Ammo need is computed
        // directly from the PLANNED loadouts (not pawn.GetLoadout(), which hasn't been updated yet at this point in
        // the deferred-apply flow) via LoadoutUtility.ComputeAmmoNeedForLoadouts, and only surfaced for ammo that
        // isn't already in production anywhere - this is what closes the "ammo was never checked" gap for real,
        // in the single popup the player actually sees, rather than a second one bolted on afterward. When
        // onConfirm is set the assignment is deferred behind the popup's confirm button. Returns true if shown.
        private bool OpenProducePopup(IEnumerable<(Pawn pawn, Loadout loadout)> planned, Action onConfirm = null, Action onCancel = null, Action onBack = null, IEnumerable<(Pawn pawn, List<Loadout> packs)> packAssignments = null, IEnumerable<(Pawn pawn, List<Loadout> sidearms)> sidearmAssignments = null)
            => OpenProducePopup(planned, BuildOwnedWeaponCounts(), onConfirm, onCancel, onBack, packAssignments, sidearmAssignments);

        // This popup's scope toggle "Consider assigned weapons / created loadouts" has been removed, see
        // Dialog_SupplyLoadouts.Request. The demand comes from exactly `planned`/`packAssignments`/
        // `sidearmAssignments`, the colonists of this run.
        // The `skipped` parameter (the solver's "Not producible - superior loadouts skipped" list) was
        // removed along with its display because it served no purpose.
        private bool OpenProducePopup(IEnumerable<(Pawn pawn, Loadout loadout)> planned, Dictionary<ThingDef, int> ownedPool, Action onConfirm, Action onCancel = null, Action onBack = null, IEnumerable<(Pawn pawn, List<Loadout> packs)> packAssignments = null, IEnumerable<(Pawn pawn, List<Loadout> sidearms)> sidearmAssignments = null)
        {
            List<(Pawn pawn, Loadout loadout)> pairs = planned.ToList();
            // Planned SIDEARM loadouts (2026-08-01: a satchel charge sidearm assigned to 7
            // colonists with only 2 in stock showed "in storage"/no production needed everywhere - Supply
            // popup didn't even list it) - a sidearm lives on its OWN separate Loadout object, attached via
            // SidearmAssignmentUtility, never inside e.planned/pairs' own main-loadout Slots. Before this fix
            // ComputeMissingSplit/ComputeAmmoNeedForLoadouts/BuildWeaponUsedBy only ever saw the main
            // loadouts, so sidearm-only weapons (grenades, satchel charges, ...) and their ammo were
            // completely invisible to this popup's shortfall check, no matter how many pawns needed one.
            // Folded in via ComputeMissingSplit's own dedicated sidearmLoadouts param (2026-08-01) - same
            // demand-sum-then-subtract-owned-once logic as the main loadouts, just never run through
            // AnalyzeLoadout (a sidearm's own gun must never populate mainDefs on its own - see that method's
            // comment). This is now the SAME method Dialog_SquadSetup's own preview badges call too, so the
            // two screens can never drift out of sync with each other again.
            List<(Pawn pawn, Loadout loadout)> sidearmPairs = sidearmAssignments != null
                ? sidearmAssignments.Where(a => a.sidearms != null).SelectMany(a => a.sidearms.Where(s => s != null).Select(s => (a.pawn, s))).ToList()
                : new List<(Pawn, Loadout)>();
            // Same gap, same fix, for packs (2026-08-02: shields never showed up as missing
            // anywhere) - shields live inside a "Shield Kit" Equipment Pack now, not a pawn's main loadout,
            // so ComputeMissingSplit's shieldMissing below (fed only by `pairs`/sidearmPairs) could never
            // find one no matter what was actually assigned. Packs attach the exact same way sidearms do
            // (their own separate Loadout object, never inside e.planned/pairs' own Slots), so they need the
            // identical fold-in treatment.
            List<(Pawn pawn, Loadout loadout)> packPairs = packAssignments != null
                ? packAssignments.Where(a => a.packs != null).SelectMany(a => a.packs.Where(pk => pk != null).Select(pk => (a.pawn, pk))).ToList()
                : new List<(Pawn, Loadout)>();

            Dictionary<ThingDef, int> mainReq, secReq, shieldMissing, missingSpecifics, produciblePackItems;
            // For each pack row, the def the DEMAND is attached to - follows the SAME branch as the rows
            // themselves, otherwise a row from one scope would carry the origin from the other.
            Dictionary<ThingDef, Def> packDemandDefsForRequest = null;
            Dictionary<LoadoutGenericDef, int> relevantGenerics;

            // Both indicator sources are ALWAYS computed now, regardless of scope (2026-08-02) - the "Assig."
            // column used to be a single dict-set whose MEANING silently flipped between "real pawn" and
            // "loadout template" depending on this scope flag (the same colonist could show
            // wildly different numbers). Only the visible DEMAND numbers (mainReq etc.
            // below) still depend on scope; the diamond/circle indicators are independent of it.
            // Assigned (diamond) - this run's own planned pawn/sidearm demand.
            //
            // Ammo comes from the colonist's LIVE loadout, not from the template: Supply Loadouts used to
            // wrongly look at the original loadout.
            // The sequence is the reason:
            // ApplyPreviewPlan calls apply() BEFORE this popup, and apply() writes the freshly chosen ammo
            // ("Override ammo", ON by default, configurable) onto the colonist's personal anchor. The
            // planned TEMPLATE meanwhile keeps the ammo it was created with, unchanged. Reading the template
            // here therefore reads exactly the ammo the colonist does NOT get - the window proposed calibers
            // for crafting that nobody uses, and listed under "Used by" the name of a colonist who carries
            // something else instead.
            // The anchor is the only place where both come together: weapon(s) from the template, sidearms
            // and packs as extra parents, and the personal ammo choice that replaces any inherited one
            // (Patch_Loadout_MultiParentSlots). One entry per colonist instead of one per
            // template+sidearm, because the union already contains everything.
            // Intended side effect: this entry point now computes the same sum as
            // BuildSupplyRequestForPawns, which has always read p.GetLoadout().
            List<(Loadout loadout, string pawnName)> ammoPairs = pairs.Select(p => p.pawn)
                .Concat(sidearmPairs.Select(p => p.pawn))
                .Where(p => p != null)
                .Distinct()
                .Select(p => (loadout: p.GetLoadout(), pawnName: p.LabelShortCap))
                .Where(t => t.loadout != null && !t.loadout.defaultLoadout)
                .ToList();
            Dictionary<ThingDef, List<string>> weaponAssignedUsedBy = BuildWeaponUsedBy(ammoPairs);
            LoadoutUtility.ComputeAmmoNeedForLoadouts(ammoPairs, out var pairGenerics, out var pairSpecifics, out Dictionary<Def, List<string>> assignedUsedBy);
            Dictionary<ThingDef, int> assignedMissingPackItems = new Dictionary<ThingDef, int>();
            Dictionary<ThingDef, Def> assignedPackDemandDefs = null;
            if (packAssignments != null)
            {
                LoadoutUtility.ScanMissingAssignedPackItems(packAssignments, out assignedMissingPackItems, out var assignedPackUsedBy, out assignedPackDemandDefs);
                foreach (var kv in assignedPackUsedBy) weaponAssignedUsedBy[kv.Key] = kv.Value;
            }
            // Used in loadout (circle) - every managed template that exists.
            LoadoutUtility.ComputeMissingForCreatedLoadouts(out var createdMain, out var createdSidearms, out Dictionary<ThingDef, List<string>> weaponTemplateUsedBy,
                out var createdPackItems, out var templatePackUsedBy, out var createdGenerics, out var createdSpecifics, out Dictionary<Def, List<string>> templateUsedBy, out var createdPackDemandDefs);
            foreach (var kv in templatePackUsedBy) weaponTemplateUsedBy[kv.Key] = kv.Value;

            ComputeMissingSplit(pairs.Select(p => p.loadout), sidearmPairs.Select(p => p.loadout).Concat(packPairs.Select(p => p.loadout)), ownedPool, out var mainMissing, out var secMissing, out shieldMissing);
            mainReq = produceMainWeapons ? mainMissing : new Dictionary<ThingDef, int>();
            secReq = produceSidearms ? secMissing : new Dictionary<ThingDef, int>();
            // The Arm overview records inside the popup's confirm (GenerateAll) - what was ACTUALLY queued,
            // including ammo, after any deselections - so nothing is double counted here.

            // Filter to what Dialog_SupplyLoadouts would actually show a row for (researched ammo only), so an
            // unresearched-only need can't pop the popup open just to say "nothing to supply".
            // Quantity-aware, not the old binary "any bill exists at all" gate (2026-08-01 fix, mirrors
            // LoadoutUtility.CheckAndOpenMissingProductionPopup's own same-shape fix from 2026-07-31, which
            // this sibling call site never got) - the old !WeaponHasActiveBill(kv.Key) suppressed the WHOLE
            // shortfall the instant ANY bill for that ammo existed anywhere, even a leftover 1-unit bill from
            // before more colonists got assigned it, silently under-proposing real demand on re-trigger.
            // + CarriedStockCount: here it is the direct consequence of the earlier sidearm fix - since
            // `sidearmAssignments` is no longer filtered on !kept, the demand above also includes the
            // sidearm ammo demand of colonists who ALREADY CARRY their ammo. Without the third stock pool,
            // exactly this case reports a shortfall that does not exist. The old solution for that was the
            // kept filter, and it threw away too much (see there).
            missingSpecifics = pairSpecifics.Where(kv => LoadoutUtility.IsAmmoResearched(kv.Key)
                && LoadoutUtility.AmmoOnHand(kv.Key) + LoadoutUtility.WeaponQueuedCount(kv.Key) < kv.Value)
                .ToDictionary(kv => kv.Key, kv => kv.Value);
            relevantGenerics = pairGenerics.Where(kv =>
                LoadoutUtility.GetAmmoTypesFor(LoadoutUtility.GetWeaponForGenericAmmo(kv.Key)).Any(LoadoutUtility.IsAmmoResearched))
                .ToDictionary(kv => kv.Key, kv => kv.Value);

            // Fold in consumables that need a standing maintenance bill even though raw current stock
            // alone wouldn't otherwise earn them a row (2026-08-01, this is the most commonly hit call
            // site - a normal Apply goes through here) - see LoadoutUtility.ConsumablesNeedingMaintenance's
            // own comment. Pack items get the same treatment further down, once missingPackItems exists.
            foreach (var kv in LoadoutUtility.ConsumablesNeedingMaintenance(pairSpecifics))
                if (!missingSpecifics.ContainsKey(kv.Key)) missingSpecifics[kv.Key] = kv.Value;
            foreach (var kv in LoadoutUtility.ConsumablesNeedingMaintenance(mainMissing, requireConsumable: true))
                if (!mainReq.ContainsKey(kv.Key)) mainReq[kv.Key] = kv.Value;
            foreach (var kv in LoadoutUtility.ConsumablesNeedingMaintenance(secMissing, requireConsumable: true))
                if (!secReq.ContainsKey(kv.Key)) secReq[kv.Key] = kv.Value;

            // Equipment Pack shortfalls, colony-wide (not scoped to just this run's changed pawns, same reach
            // as LoadoutUtility.ScanMissingAssignedPackItems' own standalone use) - folded into this SAME popup
            // so it's feature-complete and Dialog_ArmReport no longer needs its own separate end-of-chain
            // safety-net popup for Arm-my-Colony runs (2026-07-31: one popup, not two in a
            // row - the old second popup was the only place pack items were ever checked at all).
            // No IsProducible filter any more - see the
            // Created branch above and LoadoutUtility.CheckAndOpenMissingProductionPopup.
            produciblePackItems = new Dictionary<ThingDef, int>(assignedMissingPackItems);
            packDemandDefsForRequest = assignedPackDemandDefs;
            foreach (var kv in LoadoutUtility.ConsumablesNeedingMaintenance(assignedMissingPackItems, requireConsumable: true))
                if (!produciblePackItems.ContainsKey(kv.Key)) produciblePackItems[kv.Key] = kv.Value;

            var req = new Dialog_SupplyLoadouts.Request
            {
                mainWeapons = mainReq,
                secondaryWeapons = secReq,
                shields = shieldMissing,
                packItems = produciblePackItems,
                packItemDemandDefs = packDemandDefsForRequest,
                genericAmmo = relevantGenerics,
                specificAmmo = missingSpecifics,
                assignedUsedBy = assignedUsedBy,
                templateUsedBy = templateUsedBy,
                weaponAssignedUsedBy = weaponAssignedUsedBy,
                weaponTemplateUsedBy = weaponTemplateUsedBy,
                onConfirm = onConfirm,
                onCancel = onCancel,
                onBack = onBack,
                armReport = armReport
            };
            // Obsolete-bill cleanup (2026-08-01, noticed repeatedly - this exact popup, by far the
            // most commonly seen Supply Loadouts instance in the whole mod, never got this at all; only the
            // separate, far rarer end-of-session LoadoutUtility.CheckAndOpenMissingProductionPopup did).
            // Shared helper, not a second hand-rolled copy - see AttachObsoleteCleanup's own comment.
            LoadoutUtility.AttachObsoleteCleanup(req);
            int total = mainReq.Count + secReq.Count + shieldMissing.Count
                + relevantGenerics.Count + missingSpecifics.Count + produciblePackItems.Count
                + req.obsoleteAmmoCandidates.Count + req.obsoleteWeaponCandidates.Count;
            if (total > 0)
            {
                // The popup itself may still resolve every raw need as "already covered" (queued production claims
                // the shortfall, generic ammo resolves to a round already in production) - only show it when a row
                // would actually be visible; otherwise the caller proceeds as if nothing was missing.
                Dialog_SupplyLoadouts dlg = new Dialog_SupplyLoadouts(req);
                if (dlg.AnythingToSupply) { Find.WindowStack.Add(dlg); return true; }
            }
            return false;
        }

        private bool PawnMatchesSearch(Pawn p)
        {
            if (string.IsNullOrEmpty(pawnSearch)) return true;
            string full = p.Name != null ? p.Name.ToStringFull : p.LabelShort;
            return (full != null && full.IndexOf(pawnSearch, System.StringComparison.OrdinalIgnoreCase) >= 0)
                || (p.LabelShort != null && p.LabelShort.IndexOf(pawnSearch, System.StringComparison.OrdinalIgnoreCase) >= 0);
        }

        // Does a loadout (via its main weapon) pass the top-of-tab weapons filter?
        private bool LoadoutPassesFilter(ThingDef mainWeapon, Dictionary<ThingDef, int> owned)
        {
            if (mainWeapon == null) return true;
            switch (loadoutFilter)
            {
                case AssignLoadoutFilter.AvailableMainWeapon:
                    return owned != null && owned.TryGetValue(mainWeapon, out int c) && c > 0;
                case AssignLoadoutFilter.ResearchedMainWeapon:
                    RecipeDef r = LoadoutUtility.GetRecipeFor(mainWeapon);
                    if (r != null && LoadoutUtility.RecipeIsResearched(r)) return true;
                    return owned != null && owned.TryGetValue(mainWeapon, out int c2) && c2 > 0;
                default:
                    return true;
            }
        }

        // Can this weapon be crafted right now (recipe researched AND a suitable workbench is built)?
        internal static bool ProducibleNow(ThingDef w)
        {
            RecipeDef r = LoadoutUtility.GetRecipeFor(w);
            return r != null && LoadoutUtility.RecipeIsResearched(r) && BenchExistsFor(w);
        }

        // Same question for ammo: LoadoutUtility.IsAmmoResearched only checks the recipe's research
        // prerequisite, not whether a bench able to run it is actually built yet - a fresh colony can have
        // an already-researched ammo recipe with no Machining table/reloading bench on the map at all, which
        // used to read as plain "missing (can be crafted)" everywhere (2026-07-31: no bench could even be
        // built for the ammo - the tooltip claimed craftable while
        // the player couldn't even construct the bench yet).
        internal static bool AmmoProducibleNow(ThingDef ammoDef) => LoadoutUtility.IsAmmoResearched(ammoDef) && BenchExistsFor(ammoDef);

        public void RefreshPawns()
        {
            // Every colonist in the empire - every colony map plus every player-controlled caravan - not just
            // whichever map the camera happens to be looking at.
            cachedPawns = LoadoutUtility.AllColonyPawns().ToList();
            rangedScores.Clear();
            meleeScores.Clear();
            traitStrings.Clear();
            traitTooltipStrings.Clear();
            capacityTooltips.Clear();

            foreach (Pawn pawn in cachedPawns)
            {
                // Ranged Score
                float shootingSkill = pawn.skills.GetSkill(SkillDefOf.Shooting).Level;
                float sight = pawn.health.capacities.GetLevel(PawnCapacityDefOf.Sight);
                float manipulation = pawn.health.capacities.GetLevel(PawnCapacityDefOf.Manipulation);
                float rangedScore = (shootingSkill * 10f) * sight * Mathf.Min(manipulation, 1f);
                
                // Penalize if blind
                if (sight <= 0.1f) rangedScore = 0f;
                rangedScores[pawn] = rangedScore;

                // Melee Score
                float meleeSkill = pawn.skills.GetSkill(SkillDefOf.Melee).Level;
                float moving = pawn.health.capacities.GetLevel(PawnCapacityDefOf.Moving);
                float meleeScore = (meleeSkill * 10f) * sight * moving * manipulation;

                List<string> notableTraits = new List<string>();
                List<string> traitTips = new List<string>();
                
                if (pawn.story != null && pawn.story.traits != null)
                {
                    TraitDef brawlerDef = DefDatabase<TraitDef>.GetNamedSilentFail("Brawler");
                    TraitDef nimbleDef = DefDatabase<TraitDef>.GetNamedSilentFail("Nimble");
                    TraitDef toughDef = DefDatabase<TraitDef>.GetNamedSilentFail("Tough");
                    TraitDef wimpDef = DefDatabase<TraitDef>.GetNamedSilentFail("Wimp");
                    TraitDef shootingAccDef = DefDatabase<TraitDef>.GetNamedSilentFail("ShootingAccuracy");

                    // Badge word is the REAL trait's own label (Trait.LabelCap), not our own translation -
                    // "Brawler"/"Nimble"/etc. are vanilla TraitDefs, already translated by Core into every
                    // language RimWorld ships; inventing our own CER_ copy of that word would duplicate
                    // Ludeon's translation work and risk drifting from whatever word Core actually uses
                    // elsewhere in the UI for the same trait. Only the TIP (our own explanation of the
                    // scoring effect) is our content, parameterized with the real label.
                    // explanation of the scoring effect) is our content, parameterized with the real label.
                    if (brawlerDef != null && pawn.story.traits.HasTrait(brawlerDef)) { meleeScore *= 1.5f; string lbl = pawn.story.traits.GetTrait(brawlerDef)?.LabelCap ?? brawlerDef.label; notableTraits.Add(lbl); traitTips.Add("CER_AssignLoadouts_TraitBrawlerTip".Translate(lbl)); }
                    if (nimbleDef != null && pawn.story.traits.HasTrait(nimbleDef)) { meleeScore *= 1.25f; string lbl = pawn.story.traits.GetTrait(nimbleDef)?.LabelCap ?? nimbleDef.label; notableTraits.Add(lbl); traitTips.Add("CER_AssignLoadouts_TraitNimbleTip".Translate(lbl)); }
                    if (toughDef != null && pawn.story.traits.HasTrait(toughDef)) { meleeScore *= 1.5f; string lbl = pawn.story.traits.GetTrait(toughDef)?.LabelCap ?? toughDef.label; notableTraits.Add(lbl); traitTips.Add("CER_AssignLoadouts_TraitToughTip".Translate(lbl)); }
                    if (wimpDef != null && pawn.story.traits.HasTrait(wimpDef)) { meleeScore *= 0.1f; string lbl = pawn.story.traits.GetTrait(wimpDef)?.LabelCap ?? wimpDef.label; notableTraits.Add(lbl); traitTips.Add("CER_AssignLoadouts_TraitWimpTip".Translate(lbl)); }

                    if (shootingAccDef != null && pawn.story.traits.HasTrait(shootingAccDef))
                    {
                        string lbl = pawn.story.traits.GetTrait(shootingAccDef)?.LabelCap ?? shootingAccDef.label;
                        if (pawn.story.traits.GetTrait(shootingAccDef).Degree > 0) { notableTraits.Add(lbl); traitTips.Add("CER_AssignLoadouts_TraitCarefulShooterTip".Translate(lbl)); }
                        else { notableTraits.Add(lbl); traitTips.Add("CER_AssignLoadouts_TraitTriggerHappyTip".Translate(lbl)); }
                    }
                }

                if (pawn.health != null && pawn.health.hediffSet != null)
                {
                    var missingParts = pawn.health.hediffSet.GetMissingPartsCommonAncestors();
                    foreach (var mp in missingParts)
                    {
                        notableTraits.Add("CER_AssignLoadouts_MissingPartFmt".Translate(mp.Part.Label));
                        traitTips.Add("CER_AssignLoadouts_MissingPartTipFmt".Translate(mp.Part.Label));
                    }
                    foreach (var hediff in pawn.health.hediffSet.hediffs)
                    {
                        if (hediff is Hediff_AddedPart || hediff is Hediff_Implant)
                        {
                            notableTraits.Add(hediff.LabelCap);
                            traitTips.Add("CER_AssignLoadouts_ModifiesCapacitiesTipFmt".Translate(hediff.LabelCap));
                        }
                    }
                }
                
                if (sight <= 0.1f || moving <= 0.1f) meleeScore = 0f;
                meleeScores[pawn] = meleeScore;
                traitStrings[pawn] = string.Join(", ", notableTraits.ToArray());
                traitTooltipStrings[pawn] = string.Join("\n", traitTips.ToArray());
                capacityTooltips[pawn] = BuildCapacityTooltip(pawn);
            }
            
            SortPawns();
        }

        // Non-combatants (pacifists, babies - the shared CannotFight rule) always sink to the END: nothing can be
        // assigned to them, so they must never occupy the prime card slots. Within each group the chosen skill
        // sort (ranged/melee radios) applies.
        private void SortPawns()
        {
            if (cachedPawns == null) return;
            cachedPawns = cachedPawns
                .OrderBy(p => LoadoutUtility.CannotFight(p) ? 1 : 0)
                .ThenByDescending(p => sortByMelee ? MeleeScoreOf(p) : RangedScoreOf(p))
                .ToList();
        }

        public void DoWindowContents(Rect inRect)
        {
            if (cachedPawns == null)
            {
                RefreshPawns();
            }

            if (cachedPawns == null || cachedPawns.Count == 0)
            {
                Widgets.Label(inRect, "CER_AssignLoadouts_NoColonists".Translate());
                return;
            }

            if (Time.frameCount != frameCacheStamp)
            {
                frameCacheStamp = Time.frameCount;
                // The four "frame caches" now live longer than a single frame: stock pool, carry capacity,
                // ammo cell and row info used to be rebuilt about 90 times per second at 143 Hz - slot union
                // per colonist, map scan for weapons, RelevantAmmoPool - for a game that is paused. Now the
                // two-lock rule applies: rebuild on every reported mutation (CombatReadiness.MutationVersion)
                // or after 0.3 s. What sits beneath them (frameRemainingCache: a DECREMENTING pool,
                // frameFulfillCache: memo per pass) stays per pass; that is its nature.
                float now = Time.realtimeSinceStartup;
                if (frameOwnedCache == null || rowCacheVersion != CombatReadiness.MutationVersion || now - rowCacheAt > RowCacheMaxAge)
                {
                    rowCacheVersion = CombatReadiness.MutationVersion;
                    rowCacheAt = now;
                    frameOwnedCache = BuildOwnedWeaponCounts();
                    frameCapCache.Clear();
                    frameAmmoCache.Clear();
                    frameRowCache.Clear();
                    frameLabelCache.Clear();
                    // Pool AND memo now belong in the same block as the four above.
                    // An earlier comment said "the fulfill cache remembers results from exactly this counter
                    // and must therefore be reset TOGETHER WITH IT" - that is true, and it is the whole
                    // condition. They have to be reset together, not per pass: as long as both stand, every
                    // repeated call returns its memoized value and therefore decrements NOTHING more - so the
                    // pool cannot run dry. A new key (a row that was not drawn before) decrements from the
                    // remainder, and that is correct: the rows already drawn have already taken their share.
                    // The reason for the change, as measured: the status computation in DrawFulfillDot cost
                    // 229 of the tab's 509 ms/s - 45 %, redone every pass, for a paused game. It depends on
                    // exactly the same data as the four caches above.
                    frameRemainingCache = new Dictionary<ThingDef, int>(frameOwnedCache);
                    frameFulfillCache = new Dictionary<(Pawn, Def, int), FulfillStatus>();
                    frameRichSegCache = new Dictionary<(Pawn, Def, int, RichStock), LoadoutUtility.AvailabilitySegments>();
                }
            }

            // --- Top bar: search + weapons filter (radios) + produce/roles toggles. The fine-tuning (sourcing,
            // ranged/melee, anti-mech, diversity) lives in the Auto-assign preview popup. ---
            CERUiState ui = RoleUtility.Ui();
            // Self-healing: should a run ever end without going through FinishArmReport (window closed
            // some other way), the reset is caught up on the next frame - otherwise the forced mode would
            // stay in place and from then on be mirrored into the save after all.
            if (armReport == null) RestoreArmRoleMode();
            ui.asLoadoutFilter = loadoutFilter;
            // NOT during an Arm-my-Colony run with a forced role mode (see armRoleModeRestore).
            if (!armRoleModeRestore.HasValue) ui.asRoleMode = roleMode;
            ui.asSortMelee = sortByMelee;
            ui.asCompact = compactView;
            LoadoutUtility.ModeDetailToggle(inRect.width, ref ui.asDetailed);

            // Row 1: search + the grid-sort radios (both modes - sorting is basic navigation). Detailed mode adds
            // the weapons-filter radios in row 2 and the produce/roles toggles in row 3; Simple mode has one job
            // here (assign), production decisions live in the Supply flow now.
            float topY = 4f;
            pawnSearch = LoadoutUtility.SearchBox(0f, topY, pawnSearch);

            float sx = 288f;
            void SortRadio(string label, bool melee)
            {
                if (LoadoutUtility.Radio(ref sx, topY, label, sortByMelee == melee) && sortByMelee != melee)
                {
                    sortByMelee = melee;
                    SortPawns();
                }
            }
            SortRadio("CER_AssignLoadouts_SortByRanged".Translate(), false);
            SortRadio("CER_AssignLoadouts_SortByMelee".Translate(), true);

            // Compact view: one slim table row per colonist instead of the full cards (portraits and weapon icons stay).
            string cvLbl = "CER_AssignLoadouts_CompactViewLabel".Translate();
            float cvW = Text.CalcSize(cvLbl).x + 30f;
            Rect cvRect = new Rect(inRect.width - 56f - cvW, topY, cvW, 24f);
            Widgets.CheckboxLabeled(cvRect, cvLbl, ref compactView);
            TooltipHandler.TipRegion(cvRect, "CER_AssignLoadouts_CompactViewTip".Translate());

            // The detailed mode's filter rows are GONE (the "all loadouts / researched weapons" filters no
            // longer made sense, nor did the "Assign roles" button at the top): the loadout filter belonged
            // to the old loadout picker (the weapon window has its own filters), and the role mode is set
            // where it takes effect - in the auto preview. The header is now equally slim in both modes.
            float gridTop = LoadoutUtility.Divider(30f, inRect.width);

            List<Pawn> visiblePawns = string.IsNullOrEmpty(pawnSearch) ? cachedPawns : cachedPawns.Where(PawnMatchesSearch).ToList();

            // Compact view's own Name column sort (2026-08-01) - a freshly-sorted COPY built for display only.
            // visiblePawns is cachedPawns itself when there's no search text (see above), and cachedPawns is a
            // shared list read elsewhere (CachedPawns accessor, used by Dialog_SquadSetup's auto-assign
            // planner) - sorting it in place would silently reorder that too. Card view deliberately keeps
            // reading visiblePawns unsorted (its own rank/score order); only the compact row loop below reads
            // displayPawns.
            List<Pawn> displayPawns = visiblePawns;
            if (compactView && currentSortMode != SortMode.Default)
            {
                displayPawns = new List<Pawn>(visiblePawns);
                // Ranged/Melee added: with 20 colonists, "who shoots best" could only be answered by reading
                // row by row, even though the values sit right next to the names. Descending on the first
                // click, because for a skill "best first" is the intended question - for names ascending is
                // what's expected, hence the difference.
                switch (currentSortMode)
                {
                    case SortMode.Ranged:
                        displayPawns.Sort((a, b) => RangedScoreOf(b).CompareTo(RangedScoreOf(a)));
                        break;
                    case SortMode.Melee:
                        displayPawns.Sort((a, b) => MeleeScoreOf(b).CompareTo(MeleeScoreOf(a)));
                        break;
                    default:
                        displayPawns.Sort((a, b) => string.Compare(a.LabelShortCap, b.LabelShortCap, StringComparison.OrdinalIgnoreCase));
                        break;
                }
                if (sortDescending) displayPawns.Reverse();
            }

            int columns = Mathf.FloorToInt((inRect.width + Margin) / (CardWidth + Margin));
            if (columns < 1) columns = 1;
            if (compactView) columns = 1;

            // One line now - the weapon status used to need a second "Equipped:" detail row below this one,
            // but that's now a corner dot on the loadout button itself (DrawWeaponStatusDot), same as Ammo/
            // Pack/Sidearm's own dots.
            const float CompactRowH = 32f;
            int rows = Mathf.CeilToInt((float)visiblePawns.Count / columns);
            float totalHeight = compactView ? visiblePawns.Count * CompactRowH : rows * (CardHeight + Margin);

            // Column headers - compact view never had any (every column was unlabeled). Same convention
            // Dialog_GenerateLoadouts' own Weapon Loadouts tab list already uses for its headers (Tiny font,
            // 55% alpha, plain labels, no divider box) - not inventing a new header style here.
            if (compactView)
            {
                DrawCompactHeader(new Rect(0f, gridTop, inRect.width - 20f, 18f));
                gridTop += 20f;
            }

            Rect viewRect = new Rect(0, 0, inRect.width - 20f, totalHeight);
            Rect scrollRect = new Rect(0, gridTop, inRect.width, inRect.height - gridTop - 50f);
            if (compactView) Widgets.BeginScrollView(scrollRect, ref compactScrollPosition, viewRect);
            else Widgets.BeginScrollView(scrollRect, ref cardScrollPosition, viewRect);

            // Scroll culling (2026-08-02, found while chasing a lag): rows outside the visible
            // window only advance the cursor, same pattern Dialog_SquadSetup's own two row lists already use
            // - this dialog's per-row work (SingleFulfillStatus etc.) is already frame-cached at the
            // LoadoutUtility level, so an off-screen row's own marginal cost was mostly GUI layout/tooltip-
            // string overhead, not the dominant lag source (that was BuildOwnedWeaponCounts, fixed above) -
            // but it scales with roster size and is free to fix while already here.
            int col = 0, row = 0;
            foreach (Pawn pawn in displayPawns)
            {
                if (compactView)
                {
                    float ry = row * CompactRowH;
                    if (ry + CompactRowH < compactScrollPosition.y || ry > compactScrollPosition.y + scrollRect.height) { row++; continue; }
                    DrawPawnRowCompact(new Rect(0f, ry, viewRect.width, CompactRowH - 2f), pawn, row % 2 == 1);
                    row++;
                    continue;
                }
                float x = col * (CardWidth + Margin);
                float y = row * (CardHeight + Margin);
                if (y + CardHeight < cardScrollPosition.y || y > cardScrollPosition.y + scrollRect.height)
                {
                    col++;
                    if (col >= columns) { col = 0; row++; }
                    continue;
                }
                DrawPawnCard(new Rect(x, y, CardWidth, CardHeight), pawn);
                col++;
                if (col >= columns) { col = 0; row++; }
            }

            DrawDragChipAndReset();
            Widgets.EndScrollView();

            // --- Bottom bar: actions flowing in from the right (primary rightmost); the window's X closes. ---
            float xr = inRect.width;
            // ONE font size for the whole row (in German the long bulk ammo-replace label rendered small
            // between two large neighbours) - see LoadoutUtility.BottomBarFont.
            string autoAssignLbl = "CER_AssignLoadouts_AutoAssignBtn".Translate().ToString();
            string massAmmoLbl = "CER_AssignLoadouts_MassReplaceAmmoBtn".Translate().ToString();
            string unassignAllLbl = "CER_AssignLoadouts_UnassignAllBtn".Translate().ToString();
            GameFont prevBarFont = Text.Font;
            Text.Font = LoadoutUtility.BottomBarFont(
                (LoadoutUtility.PrimaryBtnW, autoAssignLbl, true),
                (LoadoutUtility.SecondaryBtnW, massAmmoLbl, false),
                (LoadoutUtility.SecondaryBtnW, unassignAllLbl, false));
            if (LoadoutUtility.BottomActionButton(inRect, ref xr, LoadoutUtility.PrimaryBtnW, autoAssignLbl,
                tip: "CER_AssignLoadouts_AutoAssignBtnTip".Translate(),
                icon: LoadoutUtility.TexWand))
            {
                RefreshPawns();
                Find.WindowStack.Add(new Dialog_SquadSetup(null, this));
            }
            if (LoadoutUtility.BottomActionButton(inRect, ref xr, LoadoutUtility.SecondaryBtnW, massAmmoLbl,
                tip: "CER_AssignLoadouts_MassReplaceAmmoBtnTip".Translate()))
            {
                Find.WindowStack.Add(new Dialog_MassReplaceAmmo());
            }
            // "Unassign all": removes the assignment from EVERY free colonist - via the anchor-safe path
            // (ArmUnassignAll keeps packs/sidearms on the anchor), with confirmation, because it affects the
            // whole colony.
            if (LoadoutUtility.BottomActionButton(inRect, ref xr, LoadoutUtility.SecondaryBtnW, unassignAllLbl,
                tip: "CER_AssignLoadouts_UnassignAllBtnTip".Translate()))
            {
                Find.WindowStack.Add(LoadoutUtility.CompactConfirmation(
                    "CER_AssignLoadouts_UnassignAllConfirm".Translate(),
                    () =>
                    {
                        int n = ArmUnassignAll();
                        CombatReadiness.Invalidate();
                        RefreshPawns();
                        Messages.Message("CER_AssignLoadouts_UnassignAllDoneFmt".Translate(n.ToString()), MessageTypeDefOf.TaskCompletion, false);
                    }, true));
            }
            Text.Font = prevBarFont;
        }

        // Arm-my-Colony: refresh, then open the live auto-assign preview with production enabled. The Arm modes map
        // onto the popup's tuning: Aggressive plans everyone anew, Filling-gaps keeps current assignments; the
        // sourcing radios preset the use-existing/craft-new slider. The report shows at the very end of the flow.
        public void ArmAutoAssignAndProduce(bool aggressive, ArmReport rep, Action onBack = null, RoleAssignMode? roleModeOverride = null)
        {
            armReport = rep;
            RefreshPawns();
            // Only FOR THIS RUN - see armRoleModeRestore. A second run without an intervening FinishArmReport
            // must not overwrite the original value, so it is only remembered the first time.
            if (roleModeOverride.HasValue)
            {
                if (!armRoleModeRestore.HasValue) armRoleModeRestore = roleMode;
                roleMode = roleModeOverride.Value;
            }
            // Craft willingness is set in the preview's own slider (persisted per save); the Arm mode only decides
            // whether existing assignments are kept (Filling gaps) or everyone is re-planned (Aggressive).
            Find.WindowStack.Add(new Dialog_SquadSetup(null, this, presetKeepAssigned: !aggressive,
                onBack: onBack));
        }

        // Aggressive reset: send every free colonist back to the default (no) loadout so the run reassigns from a clean slate.
        public int ArmUnassignAll()
        {
            Loadout none = LoadoutManager.Loadouts.FirstOrDefault(l => l.defaultLoadout);
            int n = 0;
            foreach (Pawn p in LoadoutUtility.AllColonyPawns().ToList())
            {
                Loadout cur = p.GetLoadout();
                if (cur == null || cur.defaultLoadout) continue;
                // KEEP the anchor instead of SetLoadout(none) (same class of bug as in the apply path below):
                // SetLoadout(none) bypasses the personal anchor, which is then attached to nobody, and the
                // next cleanup tick deletes it ALONG WITH packs, sidearms and own ammo. With the lazy rework
                // this matters twice as much, because "Reset first" is the default in Normal/Aggressive and
                // runs immediately before the reassignment.
                // An anchor WITH extra parents is re-hung onto None (packs/sidearms stay), only the old
                // weapon's own ammo goes with it; a colonist without such an anchor takes the old path.
                if (none != null && AmmoAssignmentUtility.IsAmmoLink(cur) && MultiParentUtility.GetExtraParents(cur).Count > 0)
                {
                    Loadout link = AmmoAssignmentUtility.EnsureAmmoLink(p, none);
                    foreach (LoadoutSlot own in link.OwnSlots.Where(LoadoutUtility.IsAmmoSlot).ToList()) link.RemoveSlot(own);
                    AmmoAssignmentUtility.SetAmmoCleared(link, false);
                }
                else p.SetLoadout(none);
                n++;
            }
            return n;
        }

        // Revert the role toggle that Arm my Colony's root question forced.
        // Idempotent - if it was not forced, nothing changes here.
        private void RestoreArmRoleMode()
        {
            if (!armRoleModeRestore.HasValue) return;
            roleMode = armRoleModeRestore.Value;
            armRoleModeRestore = null;
        }

        // Show the Arm-my-Colony overview and end the run. Safe to call once per run; further calls are no-ops.
        private void FinishArmReport(string note)
        {
            if (armReport == null) return;
            if (!string.IsNullOrEmpty(note)) armReport.notes.Add(note);
            ArmReport r = armReport;
            armReport = null;
            RestoreArmRoleMode();
            Find.WindowStack.Add(new Dialog_ArmReport(r));
        }

        public int CountUnarmedColonists()
        {
            int n = 0;
            foreach (Pawn p in LoadoutUtility.AllColonyPawns())
            {
                // Pacifists and babies never get armed, so they are not "unarmed" in the actionable sense.
                if (LoadoutUtility.CannotFight(p)) continue;
                // Via DisplayLoadout, otherwise every colonist with a personal anchor counts as armed -
                // the anchor itself is never the default, even when no template hangs beneath it any more.
                Loadout lo = AmmoAssignmentUtility.DisplayLoadout(p.GetLoadout());
                if (lo == null || lo.defaultLoadout) n++;
            }
            return n;
        }

        // ---------------------------------------------------------------------------------
        // Auto-assign (the solver itself lives in AssignPlanner; the popup previews its plan live)
        // ---------------------------------------------------------------------------------

        // Commit a previewed plan: assign/clear loadouts, then (with production on) defer everything behind the
        // produce popup exactly like before - cancelling it leaves every colonist untouched.
        internal void ApplyPreviewPlan(AssignPlan plan, bool keepAssigned, bool redistributeCarried = false, Action onBack = null,
            Dictionary<Pawn, List<Dialog_GenerateLoadouts.AmmoPick>> ammoOverrides = null)
        {
            // Materialize virtual picks first: create the default loadout for each newly picked weapon, so the rest
            // of the flow (produce popup, assignment, report) treats them like any other loadout.
            // Static since the lazy rework: the path via parentWindow.LoadoutsDialog was null in the
            // Arm chain and the readiness display, and the pick was silently lost there.
            foreach (AssignPlanEntry e in plan.entries)
            {
                if (e.kept) continue;
                if (e.plannedVirtual && e.plannedMain != null)
                {
                    Loadout made = Dialog_GenerateLoadouts.EnsureDefaultLoadout(e.plannedMain);
                    if (made != null) { e.planned = made; e.plannedVirtual = false; }
                }
                // Sidearms/grenades the solver picked as a bare weapon (no template object existed during
                // planning) - only NOW is the shared template created, never in the preview.
                if (e.plannedSidearmDefs != null && e.plannedSidearmDefs.Count > 0)
                {
                    foreach (ThingDef sd in e.plannedSidearmDefs)
                    {
                        Loadout sMade = Dialog_GenerateLoadouts.EnsureDefaultLoadout(sd);
                        if (sMade == null) continue;
                        if (e.plannedSidearms == null) e.plannedSidearms = new List<Loadout>();
                        if (!e.plannedSidearms.Contains(sMade)) e.plannedSidearms.Add(sMade);
                    }
                    e.plannedSidearmDefs = null;
                }
            }

            Action apply = () =>
            {
                Loadout none = LoadoutManager.Loadouts.FirstOrDefault(l => l.defaultLoadout);
                int assigned = 0;
                foreach (AssignPlanEntry e in plan.entries)
                {
                    if (e.kept) continue;
                    if (e.planned != null)
                    {
                        if (e.planned != e.current) AmmoAssignmentUtility.AssignTemplate(e.pawn, e.planned);
                        // Equipment Pack layer, scored alongside the weapon (AssignPlanner.plannedPacks) -
                        // a live reference on this pawn's own hidden personal anchor, never the shared
                        // template (see AmmoAssignmentUtility). Only touches anything when the SET of packs
                        // actually differs from what this pawn already has, so kept-in-place pawns' packs
                        // are untouched.
                        // The "does this differ" test itself now lives in AssignPlanner (WantsPackWrite/
                        // WantsSidearmWrite), because the Apply button's own change count has to ask the
                        // exact same question - it used to carry its own weapon-only idea of "changed" and
                        // therefore read "(nothing to apply)" over a pending pack/sidearm pin it was about
                        // to write (2026-08-12).
                        if (AssignPlanner.WantsPackWrite(e))
                            AmmoAssignmentUtility.SetPawnPacks(e.pawn, e.planned, e.plannedPacks ?? new List<Loadout>());
                        if (AssignPlanner.WantsSidearmWrite(e))
                            SidearmAssignmentUtility.SetPawnSidearms(e.pawn, e.planned, e.plannedSidearms ?? new List<Loadout>());
                        // "Override ammo" (2026-08-01, on by default): write the freshly-picked
                        // ammo (computed in Dialog_SquadSetup, mech-slider + current stock/research aware) onto
                        // this pawn's personal anchor, replacing whatever it had - the SAME mechanism Dialog_
                        // ChangeAmmo already uses for a manual per-pawn override, just applied automatically at
                        // assign time instead of requiring the player to visit that dialog separately. Roles are
                        // never in ammoOverrides (Dialog_SquadSetup excludes them - fixed by design, same as
                        // packs/shields/explosives already do), so a Role pawn's ammo is untouched here too.
                        if (ammoOverrides != null && ammoOverrides.TryGetValue(e.pawn, out List<Dialog_GenerateLoadouts.AmmoPick> picks))
                        {
                            Loadout link = AmmoAssignmentUtility.EnsureAmmoLink(e.pawn, e.planned);
                            foreach (LoadoutSlot old in link.OwnSlots.Where(s => LoadoutUtility.IsAmmoDef(s.thingDef)).ToList())
                                link.RemoveSlot(old);
                            foreach (Dialog_GenerateLoadouts.AmmoPick pick in picks)
                                if (pick.def is ThingDef td) link.AddSlot(new LoadoutSlot(td, pick.count));
                            AmmoAssignmentUtility.SetAmmoCleared(link, picks.Count == 0);
                        }
                        assigned++;
                    }
                    else
                    {
                        // KEEP the anchor instead of SetLoadout(none) (pre-existing issue):
                        // SetLoadout(none) bypasses the anchor, which is then attached to nobody, and the next
                        // cleanup deletes it - along with packs, sidearms and own ammo. That is exactly what
                        // the comment on WantsPackWrite promised NOT to do ("would strip the packs off every
                        // colonist the plan simply left without a weapon"). Now the anchor is re-hung onto
                        // None (EnsureAmmoLink), packs and sidearms stay; only the old weapon's own ammo goes
                        // with it - without a weapon nobody carries it. A colonist WITHOUT an anchor (template
                        // assigned directly, nothing to preserve) takes the old path, so no empty anchor is
                        // created for nothing.
                        if (e.current != null)
                        {
                            Loadout raw = e.pawn.GetLoadout();
                            if (none != null && AmmoAssignmentUtility.IsAmmoLink(raw) && MultiParentUtility.GetExtraParents(raw).Count > 0)
                            {
                                Loadout link = AmmoAssignmentUtility.EnsureAmmoLink(e.pawn, none);
                                foreach (LoadoutSlot own in link.OwnSlots.Where(LoadoutUtility.IsAmmoSlot).ToList()) link.RemoveSlot(own);
                                AmmoAssignmentUtility.SetAmmoCleared(link, false);
                            }
                            else e.pawn.SetLoadout(none);
                        }
                        // Packs need no weapon plan (2026-08-12): a colonist who can't fight still carries
                        // supply - that is what AssignPlanner's own pack-only pass computes for them - and
                        // their anchor hangs off CE's "None" marker, exactly like the live Assign tab's Pack
                        // cell already does it. This whole branch used to do nothing but unassign, so that
                        // pass produced a correct plan every single time and it was dropped here, unwritten.
                        // WantsPackWrite never treats an empty plan on a loadout-less row as "clear these" -
                        // see its own comment - so nobody loses a pack just for going unassigned.
                        if (none != null && AssignPlanner.WantsPackWrite(e))
                            AmmoAssignmentUtility.SetPawnPacks(e.pawn, none, e.plannedPacks ?? new List<Loadout>());
                        // Same for sidearms (found while tracing a colonist with no weapon whose added halberd
                        // was not shown, then searching for similar loopholes): a sidearm pinned here on a
                        // colonist without a planned weapon was silently discarded on Apply, because this
                        // branch only wrote packs and WantsSidearmWrite was gated on planned != null. But the
                        // sidearm hangs on the anchor above the None template just like the pack - the Assign
                        // tab allows that too now.
                        if (none != null && AssignPlanner.WantsSidearmWrite(e))
                            SidearmAssignmentUtility.SetPawnSidearms(e.pawn, none, e.plannedSidearms ?? new List<Loadout>());
                    }
                    // Redistribute carried weapons: drop everything this pawn carries that is not part of the new
                    // loadout, so manually picked-up gear returns to circulation and can be hauled to whoever the
                    // plan gave it to. CE's hold tracker would otherwise make the pawn cling to it forever.
                    if (redistributeCarried) DropNonLoadoutWeapons(e.pawn, e.planned);
                }
                // "Left without a loadout" means: should have received one and didn't - not "has none"
                // (two findings with the same cause, plus a side effect of another change).
                // The line counted every row with !kept && planned == null. But that also catches colonists
                // it doesn't apply to: a pacifist who only had a pack pinned falls to kept=false through the
                // extrasChanged rule at the end of AssignPlanner.Compute and naturally has no planned
                // loadout - the summary then read "0 colonists assigned, 1 left without a loadout" even
                // though nobody was missing. And since a colonist excluded by the player carries
                // planned=null/kept=false (the trash-bin state), they would have been counted here as a
                // failure too - when that is exactly what the player wanted.
                // CannotFight covers both cases: the player flag AND pacifist/baby.
                int left = plan.entries.Count(x => !x.kept && x.planned == null && !LoadoutUtility.CannotFight(x.pawn));
                string msg = assigned == 1
                    ? "CER_AssignLoadouts_AutoAssignedSingular".Translate(assigned.ToString())
                    : "CER_AssignLoadouts_AutoAssignedPlural".Translate(assigned.ToString());
                if (left > 0) msg += "CER_AssignLoadouts_LeftWithoutLoadout".Translate(left.ToString());
                Messages.Message(msg + ".", left > 0 ? MessageTypeDefOf.CautionInput : MessageTypeDefOf.TaskCompletion, false);
                if (armReport != null)
                {
                    foreach (AssignPlanEntry e in plan.entries)
                    {
                        if (e.kept || e.planned == null) continue;
                        AnalyzeLoadout(e.planned, out ThingDef mw2, out _, out _, out _);
                        // Last-write-wins, keyed by pawn (2026-08-01: "<pawn> -> <weapon>"
                        // and the same pair each appearing 4-5 times in the final Arm my Colony summary)
                        // - apply() can legitimately run more than once for the SAME plan within one Arm-my-
                        // Colony session (e.g. pinning one colonist re-applies the whole current plan, not just
                        // that one pawn's own change), and this loop used to just Add() every time with no
                        // dedup at all, so a pawn re-touched N times across those re-applies accumulated N
                        // near-identical rows in the report instead of showing their one, final assignment.
                        armReport.assignments.RemoveAll(a => a.pawn == e.pawn);
                        armReport.assignments.Add(new ArmAssignment { pawn = e.pawn, loadout = e.planned, mainWeapon = mw2 });
                    }
                    armReport.assignments.Sort((a, b) => string.Compare(a.pawn.LabelShortCap, b.pawn.LabelShortCap, StringComparison.OrdinalIgnoreCase));
                    armReport.pawnsLeftUnassigned = left;
                }
                RefreshPawns();
                // Readiness cache invalidation (2026-08-01): this is the ONE shared commit path
                // for both the Assign-preview's own apply AND the header widget's "fix no-loadout colonists"
                // one-click action, but never told CombatReadiness a real update just happened - every dialog
                // this mod opens force-pauses the game, so the tick-interval cache can't naturally refresh for
                // up to ~4 real seconds after closing everything, during which the widget keeps re-offering an
                // already-applied fix. Every other real commit path already calls this (Dialog_ArmColony,
                // Dialog_SupplyLoadouts, Dialog_PawnGear) - this was the one gap.
                CombatReadiness.Invalidate();
                // FinishArmReport deliberately NOT called here anymore (2026-08-01) - it's now the produce
                // popup's own job to call it once IT resolves (confirm/cancel/nothing-to-produce), so the
                // Arm Report screen still only appears AFTER that step, not the instant apply() itself runs.
            };

            // Assignment commits IMMEDIATELY now (2026-08-01: all three steps should behave the same -
            // Equipment Packs and Weapon Loadouts both already commit the moment
            // their own Continue/Generate is clicked; only this Assign step used to defer the real
            // AssignTemplate/SetPawnPacks/SetPawnSidearms writes behind the LATER Supply popup's own
            // Confirm button, so clicking Back there (not Cancel) left every pawn's assignment silently
            // never having happened even though the wizard had already visually moved past this step.
            // apply() and the Supply popup below are fully independent concerns - WHO gets WHAT loadout
            // (this) vs. WHAT PRODUCTION to queue for it (that) - so running apply() first and letting the
            // popup handle production-only from here on doesn't lose or duplicate anything either used to do.
            apply();

            // The ammo-gap check (neither the solver nor manual assignment
            // ever checked whether newly assigned loadouts have ammo production at all) is now folded INTO the
            // production popup, so the player sees ONE combined Supply popup (weapons + shields + ammo) instead
            // of two sequential ones - that consolidation is the actual point of this dialog. Always runs now
            // (2026-07-30) - the "produce missing weapons" off path used to skip straight to a lighter ammo-only
            // popup instead; removed along with the toggle itself.
            Action onCancel = () =>
            {
                // Assignment already happened above (no longer gated behind this popup) - only the
                // PRODUCTION step was skipped, so the old "aborted" wording (implying nothing happened at
                // all) would now be actively misleading.
                if (armReport != null) armReport.cancelled = false;
                FinishArmReport("CER_AssignLoadouts_ProduceSkippedNote".Translate());
            };
            var pairs = plan.entries.Where(e => !e.kept && e.planned != null).Select(e => (e.pawn, e.planned));
            // Whole plan, not just the changed subset above - kept pawns' packs are unchanged live state, and
            // newly-planned pawns' packs are exactly what apply() already set, so this already reflects
            // post-apply reality.
            var packAssignments = plan.entries.Select(e => (e.pawn, e.plannedPacks));
            // Whole plan, not just the changed rows - exactly like packAssignments one line above. The old
            // reasoning ("a kept pawn already carries its sidearm, that would be double counting") only holds
            // if the pawn REALLY carries it. If its loadout asks for a knife the colony never made, no demand
            // arose here at all - and since practically every colonist is "kept" after an Arm run, that
            // meant: missing sidearms did NOT SHOW UP AT ALL in the supply window.
            // No double-counting risk, and that is not a guess: ComputeMissingSplit subtracts `owned`
            // (BuildOwnedWeaponCounts), which counts every carried item, kept or not - demand 1 and stock 1
            // cancel out exactly for the pawn that has its knife, while a real shortfall remains for the one
            // without.
            // Dialog_SquadSetup.BuildPlanWideShortfalls received exactly the same correction (noticed there
            // as a knife showing 'ready' although the colony had none); the comment here even pointed to
            // that filter as the model - except it had already been removed by then.
            var sidearmAssignments = plan.entries.Select(e => (e.pawn, e.plannedSidearms));
            // onConfirm here is just the report wrap-up now - apply() already ran above, unconditionally;
            // this popup is purely a "anything to produce for what was just assigned?" follow-up from here.
            if (!OpenProducePopup(pairs, () => FinishArmReport(null), onCancel, onBack, packAssignments, sidearmAssignments))
                FinishArmReport(null);   // nothing to produce - finish immediately, same as before
        }

        // CE's Utility_HoldTracker is internal, so the forget call goes through reflection (resolved once).
        private static readonly System.Reflection.MethodInfo HoldTrackerForgetMI =
            HarmonyLib.AccessTools.Method("CombatExtended.Utility_HoldTracker:HoldTrackerForget", new[] { typeof(Pawn), typeof(Thing) });
        // Gated on Patch_LoadoutTooltip.HoldTrackerAnythingHeld first (2026-08-01, log spam:
        // "X wasn't being tracked by HoldTracker and tried to forget a ThingDef Y") - the old unconditional
        // call fired for EVERY dropped non-loadout weapon regardless of whether CE ever actually hold-tracked
        // it, and most dropped weapons came from a normal loadout assignment, never a manual force-pickup, so
        // the overwhelming majority of these calls were guaranteed no-ops that CE itself logs a warning for.
        // Not a per-item check (CE exposes no reflected "is THIS thing tracked for this pawn" query) - a
        // pawn with ANY hold-tracked item can still log one spurious warning for a different, untracked item
        // being dropped alongside it, but that's the rare case instead of the common one.
        private static void HoldForget(Pawn p, Thing t)
        {
            if (!Patch_LoadoutTooltip.HoldTrackerAnythingHeld(p)) return;
            try { HoldTrackerForgetMI?.Invoke(null, new object[] { p, t }); } catch { }
        }

        // Drop every weapon this pawn carries that is not part of the given loadout (utility items are left alone).
        // CE's hold tracker pins manually picked-up weapons to the pawn, so forget the record first - otherwise the
        // pawn re-collects the weapon right after dropping it.
        private static void DropNonLoadoutWeapons(Pawn p, Loadout lo)
        {
            if (p == null || p.Map == null) return;
            HashSet<ThingDef> allowed = new HashSet<ThingDef>();
            if (lo != null && lo.Slots != null)
                foreach (LoadoutSlot s in lo.Slots)
                    if (s.thingDef != null && s.thingDef.IsWeapon) allowed.Add(s.thingDef);
            // Packs/sidearms too (2026-08-02 fix: a freshly-assigned sidearm/pack weapon got
            // dropped again immediately by THIS SAME apply pass) - `lo` is only the bare main-weapon
            // template; SetPawnPacks/SetPawnSidearms already ran earlier in ApplyPreviewPlan's own apply()
            // loop for this exact pawn, so their weapon defs are real, current state by the time this runs,
            // not something that needs `e.plannedPacks`/`e.plannedSidearms` threaded through as extra params.
            foreach (Loadout pack in AmmoAssignmentUtility.GetPawnPacks(p) ?? new List<Loadout>())
                if (pack?.Slots != null)
                    foreach (LoadoutSlot s in pack.Slots)
                        if (s.thingDef != null && s.thingDef.IsWeapon) allowed.Add(s.thingDef);
            foreach (Loadout side in SidearmAssignmentUtility.GetPawnSidearms(p) ?? new List<Loadout>())
                if (side?.Slots != null)
                    foreach (LoadoutSlot s in side.Slots)
                        if (s.thingDef != null && s.thingDef.IsWeapon) allowed.Add(s.thingDef);

            if (p.equipment != null && p.equipment.Primary != null && !allowed.Contains(p.equipment.Primary.def))
            {
                ThingWithComps eq = p.equipment.Primary;
                HoldForget(p, eq);
                p.equipment.TryDropEquipment(eq, out _, p.Position, false);
            }
            if (p.inventory != null && p.inventory.innerContainer != null)
            {
                foreach (Thing t in p.inventory.innerContainer
                    .Where(x => x.def != null && x.def.IsWeapon && !LoadoutUtility.IsUtilityWeapon(x.def) && !allowed.Contains(x.def)).ToList())
                {
                    HoldForget(p, t);
                    p.inventory.innerContainer.TryDrop(t, p.Position, p.Map, ThingPlaceMode.Near, out _);
                }
            }
        }

        // The preview popup was closed without applying. If an Arm-my-Colony run is in flight, account for it.
        internal void CancelArmAtAssign()
        {
            if (armReport == null) return;
            armReport.cancelled = true;
            FinishArmReport("CER_AssignLoadouts_ArmCancelledNote".Translate());
        }

        // Soft caps that keep the squad's ranged mix balanced (mostly rifles, a few specialists). Caps are maxima,
        // not targets, and are ignored entirely as a fallback so nobody is left unarmed while loadouts remain.
        // Colony-size-aware, SUB-LINEAR caps - replaces the old flat-percentage
        // formula, which scaled specialist roles linearly forever (a 30-pawn colony got up to 8 Marksmen). A small
        // colony still gets an essential floor of one specialist per moderate role once it's big enough to spare
        // one; growth beyond that flattens out (a few more specialists, not proportionally many more) since
        // Rifle/CQC-style general combat should absorb most of a large colony's growth instead. Launcher is the
        // rarest specialist: needs a slightly bigger colony before it appears at all, and grows slower still.
        internal static Dictionary<WeaponGroup, int> BuildGroupCaps(int shooterCount)
        {
            int s = Mathf.Max(1, shooterCount);
            int moderate = s <= 3 ? 1 : Mathf.Clamp(Mathf.RoundToInt(1 + (s - 3) / 8f), 1, 4);
            int rare = s < 3 ? 0 : (s <= 5 ? 1 : Mathf.Clamp(Mathf.RoundToInt(1 + (s - 5) / 12f), 1, 3));
            return new Dictionary<WeaponGroup, int>
            {
                { WeaponGroup.Launcher,  rare },
                { WeaponGroup.SupportMG, moderate },
                { WeaponGroup.Marksman,  moderate }
                // Rifle / CQC / Archaic / Melee / Other are uncapped (the fill) - general combat absorbs colony growth.
            };
        }

        // A loadout counts as RANGED as soon as it carries any (non-utility) firearm - that firearm is the primary and
        // melee is just a sidearm. Only pure-melee loadouts are melee. Within the chosen role the highest-scoring
        // weapon is the "main weapon" (so two firearms fall back to the higher score, as intended).
        internal static bool AnalyzeLoadout(Loadout lo, out ThingDef main, out float score, out bool isRanged, out WeaponGroup group)
        {
            main = null; score = 0f; isRanged = false; group = WeaponGroup.Other;
            if (lo == null || lo.Slots == null) return false;

            // Real weapons only - never a thrown explosive (a satchel charge's raw blast score can dwarf the
            // actual gun) and never a utility item.
            List<ThingDef> weps = lo.Slots.Select(s => s.thingDef)
                .Where(w => w != null && w.IsWeapon && !LoadoutUtility.IsUtilityWeapon(w) && !LoadoutUtility.IsThrownExplosive(w))
                .Distinct().ToList();
            if (weps.Count == 0) return false;

            // The loadout's own name identifies its main weapon far more reliably than any score/class guess - see
            // LoadoutUtility.LoadoutNameMatchesWeapon. Without this, a melee-primary loadout's default ranged
            // sidearm (every melee main gets one - PickDefaultSidearm) outranked the melee weapon outright under
            // the old "ranged always wins" rule, so the solver's stock/production checks silently validated the
            // SIDEARM instead of the weapon the loadout is actually named and built around.
            ThingDef mainW = weps.FirstOrDefault(w => LoadoutUtility.LoadoutNameMatchesWeapon(lo.label, w));
            if (mainW == null)
            {
                ThingDef bestRanged = null; float bestRangedScore = float.NegativeInfinity;
                ThingDef bestMelee = null; float bestMeleeScore = float.NegativeInfinity;
                foreach (ThingDef w in weps)
                {
                    LoadoutUtility.GetWeaponScore(w, out float sc, out _);
                    if (w.IsRangedWeapon && !w.IsMeleeWeapon) { if (sc > bestRangedScore) { bestRangedScore = sc; bestRanged = w; } }
                    else if (w.IsMeleeWeapon) { if (sc > bestMeleeScore) { bestMeleeScore = sc; bestMelee = w; } }
                }
                mainW = bestRanged ?? bestMelee;
            }
            if (mainW == null) return false;

            LoadoutUtility.GetWeaponScore(mainW, out score, out _);
            isRanged = mainW.IsRangedWeapon && !mainW.IsMeleeWeapon;
            main = mainW;
            group = LoadoutUtility.ClassifyWeaponGroup(main);
            return true;
        }

        // Frame-gated cache (2026-08-02 fix: this dialog's own DoWindowContents rebuilt this
        // from scratch - a full ThingsInGroup(Weapon)+ThingsInGroup(Apparel) map scan plus every colony
        // pawn's equipment/inventory/apparel - on EVERY call, unlike every other equivalent-cost lookup in
        // the mod, which is frame- or TTL-cached. Unity IMGUI dispatches DoWindowContents multiple times
        // per rendered frame (Layout pass, Repaint pass, once per input event), so this ran several times
        // per frame, not once - the single strongest lag suspect found tracing the "Assign Loadouts tab
        // feels laggy" report. Dialog_SquadSetup's own copy of this same call is already cheap by
        // comparison - it only runs once, in that dialog's constructor, never per-draw.
        private static Dictionary<ThingDef, int> ownedWeaponCountsCache;
        private static int ownedWeaponCountsCacheFrame = -1;

        internal static Dictionary<ThingDef, int> BuildOwnedWeaponCounts()
        {
            if (Time.frameCount == ownedWeaponCountsCacheFrame) return ownedWeaponCountsCache;
            ownedWeaponCountsCacheFrame = Time.frameCount;
            return ownedWeaponCountsCache = BuildOwnedWeaponCountsUncached();
        }

        private static Dictionary<ThingDef, int> BuildOwnedWeaponCountsUncached()
        {
            Dictionary<ThingDef, int> counts = new Dictionary<ThingDef, int>();

            // Deduplicate by Thing instance: an equipped/carried weapon can show up both in the map weapon lister and
            // in the pawn's equipment/inventory. Counting it twice made auto-assign think the single owned weapon was
            // free for two colonists at once (the same R-6 handed to two pawns, both "in stock").
            HashSet<Thing> seen = new HashSet<Thing>();
            void Add(Thing t)
            {
                if (t == null || t.def == null || LoadoutUtility.IsHiddenInFog(t) || !seen.Add(t)) return;
                if (!t.def.IsWeapon && !LoadoutUtility.IsShieldApparel(t.def)) return;
                counts[t.def] = (counts.TryGetValue(t.def, out int c) ? c : 0) + t.stackCount;
            }

            // Every colony map, not just the one in view - a weapon stored at an outpost is still owned.
            foreach (Map map in LoadoutUtility.PlayerMaps())
            {
                // Loose map things count even while marked "do not pick up" (2026-08-09, see
                // LoadoutUtility.IsMarkedDoNotPickUp) - a raid's dropped weapons are exactly the pool this
                // screen gets opened for. The "!" on the availability bar is what says they still need releasing.
                foreach (Thing t in map.listerThings.ThingsInGroup(ThingRequestGroup.Weapon))
                    Add(t);
                // Shields are apparel, not weapons - the weapon group above never catches them, so scan storage/ground
                // apparel too (plan section 6: shields need the same "already own one" check weapons get).
                foreach (Thing t in map.listerThings.ThingsInGroup(ThingRequestGroup.Apparel))
                    if (LoadoutUtility.IsShieldApparel(t.def)) Add(t);
            }
            foreach (Pawn p in LoadoutUtility.AllColonyPawns())
            {
                if (p.equipment != null && p.equipment.Primary != null) Add(p.equipment.Primary);
                if (p.inventory != null && p.inventory.innerContainer != null)
                    foreach (Thing t in p.inventory.innerContainer) Add(t);
                if (p.apparel != null && p.apparel.WornApparel != null)
                    foreach (Apparel a in p.apparel.WornApparel) if (LoadoutUtility.IsShieldApparel(a.def)) Add(a);
            }
            return counts;
        }

        // Weapons genuinely available for ONE pawn: free storage across every colony map plus what that pawn
        // already carries. Weapons other colonists are holding are excluded, so assigning a single pawn a
        // loadout whose weapon isn't free still offers to produce it.
        private static Dictionary<ThingDef, int> AvailableWeaponsForPawn(Pawn pawn)
        {
            Dictionary<ThingDef, int> counts = new Dictionary<ThingDef, int>();

            // Same Thing-instance dedup as BuildOwnedWeaponCounts above: a weapon can show up both in the map
            // lister and in a pawn's equipment/inventory, and without this a genuinely-owned weapon could be
            // double-counted, making a real shortfall look already covered.
            HashSet<Thing> seen = new HashSet<Thing>();
            void Add(Thing t)
            {
                if (t == null || t.def == null || LoadoutUtility.IsHiddenInFog(t) || !seen.Add(t)) return;
                if (!t.def.IsWeapon && !LoadoutUtility.IsShieldApparel(t.def)) return;
                counts[t.def] = (counts.TryGetValue(t.def, out int c) ? c : 0) + t.stackCount;
            }

            foreach (Map map in LoadoutUtility.PlayerMaps())
            {
                // Same "do not pick up" stock counts too, see BuildOwnedWeaponCounts above (2026-08-09).
                foreach (Thing t in map.listerThings.ThingsInGroup(ThingRequestGroup.Weapon))
                    Add(t);
                foreach (Thing t in map.listerThings.ThingsInGroup(ThingRequestGroup.Apparel))
                    if (LoadoutUtility.IsShieldApparel(t.def)) Add(t);
            }
            if (pawn != null)
            {
                if (pawn.equipment != null && pawn.equipment.Primary != null) Add(pawn.equipment.Primary);
                if (pawn.inventory != null && pawn.inventory.innerContainer != null)
                    foreach (Thing t in pawn.inventory.innerContainer) Add(t);
                if (pawn.apparel != null && pawn.apparel.WornApparel != null)
                    foreach (Apparel a in pawn.apparel.WornApparel) if (LoadoutUtility.IsShieldApparel(a.def)) Add(a);
            }
            return counts;
        }

        // ================================================================================================
        // ONE calculation for "who is missing which weapon"
        // ================================================================================================
        //
        // There used to be two, and they contradicted each other. The readiness display allocated IN TURN:
        // each colonist first covers from what they carry, then from the free stock on the maps - whoever
        // comes up empty is reported. The supply window instead computed the TOTAL DEMAND of the affected
        // colonists against the colony's TOTAL STOCK (BuildOwnedWeaponCounts), which also includes every
        // weapon ANOTHER colonist is already holding. The result: "Production gaps (2) - open Supply"
        // opened a window saying "Everything needed is already in stock or in production". Same class of
        // bug as UnneededAmmoBillCandidates/UnneededAmmoEntries: one question, two calculations.
        //
        // This function IS the calculation. The warning reads from it WHO doesn't get something, the
        // supply window reads WHAT needs to be procured - they can no longer contradict each other.
        // Deliberately NOT included: whether a bill is already running for the shortfall. That is a
        // second, separate question ("is it already being fixed"), and both sides keep answering it
        // themselves - the warning via its produced amount, the window via its rows' quantities.
        //
        // Demand = the anchor's slot union: template PLUS sidearms PLUS packs (attached as extra parents,
        // see MultiParentUtility) - exactly what the colonist carries according to the plan.
        // ------------------------------------------------------------------------------------------------
        // Both views use this now: a cell shows
        // only what is really free for THIS colonist.
        //
        // The same calculation now ALSO covers non-weapons (ammo, pack contents, generic slots).
        // Previously the cells had a second view for those: each cell compared its own demand with the
        // stock of the WHOLE colony without subtracting what other colonists already claimed - so with
        // 102 medicine and 50 per cell, ALL cells said "nothing missing", even if the total was far above
        // 102. For weapons it was a notch worse: there even the weapon another colonist was holding
        // counted, and the result additionally depended on the DRAWING ORDER.
        //
        // Now both use the same order (AllColonyPawns, not the drawing order) and the same rule: each
        // colonist first covers from what they carry, then from the free stock on the maps. Whatever is
        // still open afterwards is that colonist's shortfall.
        //
        // What is DELIBERATELY left untouched: Dialog_SquadSetup still computes against total stock for
        // the assignment PREVIEW (BuildOwnedWeaponCounts). That is not a contradiction but a different
        // question - when redistributing, another colonist's weapon is very much available; when showing
        // the current state, it is not.
        private sealed class LoadoutClaims
        {
            // Weapons and shields - what WeaponShortfallsByPawn already delivered before, unchanged.
            public readonly Dictionary<Pawn, Dictionary<ThingDef, int>> weaponMissing = new Dictionary<Pawn, Dictionary<ThingDef, int>>();
            // Everything else from the same slot union. Def, not ThingDef: a generic slot
            // ("Any Medicine") has no concrete ThingDef and must still be claimed.
            public readonly Dictionary<Pawn, Dictionary<Def, int>> itemMissing = new Dictionary<Pawn, Dictionary<Def, int>>();
            // WHICH defs were evaluated for this colonist at all. Without this a cell could not tell
            // "no entry in the shortfall list" apart from "never asked" - and a preview row in the loadout
            // editor that isn't assigned yet would read as "available".
            public readonly Dictionary<Pawn, HashSet<Def>> evaluated = new Dictionary<Pawn, HashSet<Def>>();
            // What this colonist really GETS of this def: own carried items plus its share of the free
            // stock. That is the number a single cell can work with (see ClaimedAllocation) - the shortfall
            // next to it belongs to the whole colonist and is not suitable for that.
            public readonly Dictionary<Pawn, Dictionary<Def, int>> allocated = new Dictionary<Pawn, Dictionary<Def, int>>();

            public bool Knows(Pawn pawn, Def d) => pawn != null && d != null
                && evaluated.TryGetValue(pawn, out HashSet<Def> set) && set.Contains(d);
        }

        private static LoadoutClaims claimsCache;
        private static int claimsCacheVersion = -1;
        private static float claimsCacheAt = -99f;
        // No longer per frame: BuildClaimsUncached walks every colonist including the slot union and
        // inventory - at 143 Hz about 90 times per second for a paused game, and the calculation landed as
        // the first caller in the first row's ammo cell (measured 86 ms/s, the largest item of the cell
        // row). Two locks instead of one: the version counter of the central invalidation
        // (CombatReadiness.MutationVersion, every mutation in the mod calls it) AND a short maximum age -
        // a forgotten mutation site thus makes the result 0.3 s stale, never permanently wrong. In a
        // running game (readiness display) 0.3 s means: a stack that was just stored counts a blink later.
        private const float ClaimsMaxAge = 0.3f;

        private static LoadoutClaims Claims()
        {
            float now = Time.realtimeSinceStartup;
            if (claimsCache != null && claimsCacheVersion == CombatReadiness.MutationVersion && now - claimsCacheAt < ClaimsMaxAge) return claimsCache;
            claimsCacheVersion = CombatReadiness.MutationVersion;
            claimsCacheAt = now;
            return claimsCache = BuildClaimsUncached();
        }

        internal static Dictionary<Pawn, Dictionary<ThingDef, int>> WeaponShortfallsByPawn() => Claims().weaponMissing;

        // How much of this item this colonist REALLY gets after everyone else has taken their share -
        // own carried items plus the share from the free stock. -1 means "the calculation has nothing to
        // say about this" (no template, or the item isn't in it); then the previous path applies.
        //
        // The ALLOCATION, not the shortfall. Previously this function returned the COLONIST's shortfall,
        // and BuildAvailabilitySegments wrote it directly into every cell. So two cells of the same
        // colonist for the same item again said word for word the same thing - exactly the bug that had
        // been fixed once before (the 1-item cell reported the same shortage as the 5-item cell).
        // Finding: the medicine NUMBERS were identical and referred to the colonist's total demand
        // (13 = 12 from the pack + 1 from misc), not to the respective cell's share.
        // With the allocation each cell computes for itself again: missing = its own demand minus what
        // the colonist gets at all. The 1-item cell is thereby covered, the 12-item cell is not - and the
        // reference point is still the colonist, no longer the whole colony.
        internal static int ClaimedAllocation(Pawn pawn, Def d)
        {
            if (pawn == null || d == null) return -1;
            LoadoutClaims claims = Claims();
            if (!claims.Knows(pawn, d)) return -1;
            return claims.allocated.TryGetValue(pawn, out Dictionary<Def, int> al)
                && al.TryGetValue(d, out int n) ? n : 0;
        }

        private static LoadoutClaims BuildClaimsUncached()
        {
            // The pool is the FREE stock - ground and storage on all player maps. What a colonist carries
            // is deliberately NOT in it (it belongs to the colonist, not to the map) and is subtracted per
            // colonist below; that is exactly how this calculation differs from BuildOwnedWeaponCounts,
            // which throws everything into one pool. "Do not pick up" counts too, same reasoning as there.
            Dictionary<ThingDef, int> pool = new Dictionary<ThingDef, int>();
            HashSet<Thing> seen = new HashSet<Thing>();
            void AddPool(Thing t)
            {
                if (t == null || t.def == null || LoadoutUtility.IsHiddenInFog(t) || !seen.Add(t)) return;
                if (!t.def.IsWeapon && !LoadoutUtility.IsShieldApparel(t.def)) return;
                pool[t.def] = (pool.TryGetValue(t.def, out int c) ? c : 0) + t.stackCount;
            }
            foreach (Map map in LoadoutUtility.PlayerMaps())
            {
                foreach (Thing t in map.listerThings.ThingsInGroup(ThingRequestGroup.Weapon)) AddPool(t);
                // Shields are apparel, the weapon group above never finds them - same special handling as
                // in BuildOwnedWeaponCounts.
                foreach (Thing t in map.listerThings.ThingsInGroup(ThingRequestGroup.Apparel))
                    if (LoadoutUtility.IsShieldApparel(t.def)) AddPool(t);
            }

            // The pool for everything that is NOT a weapon or shield - ammo, medicine, rations,
            // components, generic slots. Only filled on the first request per def, so this doesn't count
            // through half the item database, only what someone was actually assigned. Storage AND
            // ground, as everywhere in the mod.
            // What colonists carry is deliberately not in it - that is subtracted per colonist below,
            // exactly like the weapon pool above.
            // Reference comparison for EVERY def key of this calculation: without the startup fix in
            // CEReloadedMain, CE's generic defs all carry hash 0 and compare as equal - see there. With the
            // default comparer, "Any ammo for M1 Garand" and "Any Perishable Meal" fell onto ONE key, the
            // food pool was empty after the first colonist, and everyone after that read "missing". The
            // startup fix addresses the root cause; the comparer here keeps the calculation correct even
            // if another mod later creates such defs without a hash again.
            Dictionary<Def, int> itemPool = new Dictionary<Def, int>(LoadoutUtility.DefRefComparer.Instance);
            int ItemFree(Def d)
            {
                if (itemPool.TryGetValue(d, out int have)) return have;
                // StoredStockCount instead of IngredientStockCount: the pack pool also contains things
                // RimWorld doesn't treat as a resource (shields, binoculars, throwables) - for those the
                // resource counter always returns 0, and when lying on a shelf they are additionally skipped
                // by the ground counter. See LoadoutUtility.StoredStockCount.
                int stock = d is ThingDef td2
                    ? LoadoutUtility.StoredStockCount(td2) + LoadoutUtility.GroundStockCount(td2)
                    : LoadoutUtility.GenericStockCount(d as LoadoutGenericDef);
                itemPool[d] = stock;
                return stock;
            }

            var claims = new LoadoutClaims();
            foreach (Pawn pawn in LoadoutUtility.AllColonyPawns())
            {
                Loadout lo = pawn.GetLoadout();
                // Decided on the resolved loadout, read from the raw one - the same distinction as in
                // CombatReadinessAlerts.Recompute: whether a template is attached at all is answered by
                // DisplayLoadout, what the colonist carries is in the anchor's slot union.
                Loadout display = AmmoAssignmentUtility.DisplayLoadout(lo);
                // No longer bailing out on display.defaultLoadout: an anchor over CE's None loadout carries
                // packs and now sidearms too - they belong in the turn-based calculation like any other.
                // A bare None without an anchor has empty slots and drops out below via
                // `needs.Count == 0 && itemNeeds.Count == 0` anyway.
                if (lo == null || lo.Slots == null || display == null) continue;

                Dictionary<ThingDef, int> needs = new Dictionary<ThingDef, int>();
                Dictionary<Def, int> itemNeeds = new Dictionary<Def, int>();
                foreach (LoadoutSlot slot in lo.Slots)
                {
                    ThingDef w = slot.thingDef;
                    int want = Mathf.Max(1, slot.count);
                    if (w == null)
                    {
                        // Generic slot ("Any Medicine", "Any Meal") - no concrete ThingDef, but definitely
                        // a claim on the shared stock of its family.
                        // Generic AMMO ("Any ammo for X") stays out: its stock is computed via
                        // ResolveGenericAmmo/ComputeAmmoAvailability, not via the item pool -
                        // EnsureGenericSupplyDefs deliberately doesn't know it, and GenericStockCount always
                        // returned 0 for it, i.e. a guaranteed false "missing".
                        if (slot.genericDef != null && !LoadoutUtility.IsGenericAmmoDef(slot.genericDef))
                            itemNeeds[slot.genericDef] = (itemNeeds.TryGetValue(slot.genericDef, out int gc) ? gc : 0) + want;
                        continue;
                    }
                    // Exactly the predicate of ComputeMissingSplit.FoldSlots - weapons and shields, no utility
                    // weapons (there is nothing to do for them in the supply window either).
                    if (!LoadoutUtility.IsShieldApparel(w) && (!w.IsWeapon || LoadoutUtility.IsUtilityWeapon(w)))
                    {
                        // Utility weapons stay OUT - in both pools. They are weapons, so they aren't in the
                        // item stock (resourceCounter knows no weapons), and a claim against an empty pool
                        // would be a guaranteed false red. The old path in SingleFulfillStatus still applies
                        // to them.
                        if (!w.IsWeapon && !LoadoutUtility.IsShieldApparel(w))
                            itemNeeds[w] = (itemNeeds.TryGetValue(w, out int ic) ? ic : 0) + want;
                        continue;
                    }
                    needs[w] = (needs.TryGetValue(w, out int c) ? c : 0) + want;
                }
                if (needs.Count == 0 && itemNeeds.Count == 0) continue;

                HashSet<Def> evaluated = new HashSet<Def>(LoadoutUtility.DefRefComparer.Instance);
                Dictionary<Def, int> allocated = new Dictionary<Def, int>(LoadoutUtility.DefRefComparer.Instance);

                Dictionary<ThingDef, int> missing = null;
                foreach (var kv in needs)
                {
                    evaluated.Add(kv.Key);
                    int carried = CarriedCountOf(pawn, kv.Key);
                    int open = kv.Value - carried;
                    int got = Mathf.Min(carried, kv.Value);
                    if (open > 0 && pool.TryGetValue(kv.Key, out int free) && free > 0)
                    {
                        int take = Mathf.Min(free, open);
                        pool[kv.Key] = free - take;
                        open -= take;
                        got += take;
                    }
                    allocated[kv.Key] = got;
                    if (open <= 0) continue;
                    if (missing == null) missing = new Dictionary<ThingDef, int>();
                    missing[kv.Key] = open;
                }
                if (missing != null) claims.weaponMissing[pawn] = missing;

                Dictionary<Def, int> itemMissing = null;
                foreach (var kv in itemNeeds)
                {
                    evaluated.Add(kv.Key);
                    int carried = kv.Key is ThingDef itd
                        ? LoadoutUtility.PawnCarriedCount(pawn, itd)
                        : LoadoutUtility.PawnCarriedGenericCount(pawn, kv.Key as LoadoutGenericDef);
                    int open = kv.Value - carried;
                    int got = Mathf.Min(carried, kv.Value);
                    if (open > 0)
                    {
                        int freeItem = ItemFree(kv.Key);
                        if (freeItem > 0)
                        {
                            int take = Mathf.Min(freeItem, open);
                            itemPool[kv.Key] = freeItem - take;
                            open -= take;
                            got += take;
                        }
                    }
                    allocated[kv.Key] = got;
                    if (open <= 0) continue;
                    if (itemMissing == null) itemMissing = new Dictionary<Def, int>(LoadoutUtility.DefRefComparer.Instance);
                    itemMissing[kv.Key] = open;
                }
                if (itemMissing != null) claims.itemMissing[pawn] = itemMissing;

                claims.evaluated[pawn] = evaluated;
                claims.allocated[pawn] = allocated;
            }
            return claims;
        }

        // What this colonist actually has of this def on them - hands, backpack and (for shields) on the
        // body. Worn apparel counts too, otherwise a worn shield would read as missing.
        private static int CarriedCountOf(Pawn pawn, ThingDef def)
        {
            int c = 0;
            if (pawn == null || def == null) return 0;
            if (pawn.equipment != null && pawn.equipment.Primary != null && pawn.equipment.Primary.def == def)
                c += pawn.equipment.Primary.stackCount;
            if (pawn.inventory != null && pawn.inventory.innerContainer != null)
                foreach (Thing t in pawn.inventory.innerContainer) if (t.def == def) c += t.stackCount;
            if (pawn.apparel != null && pawn.apparel.WornApparel != null)
                foreach (Apparel a in pawn.apparel.WornApparel) if (a.def == def) c += a.stackCount;
            return c;
        }

        private void DrawPawnCard(Rect rect, Pawn pawn)
        {
            DrawPawnCardInner(rect, pawn);
        }

        private void DrawPawnCardInner(Rect rect, Pawn pawn)
        {
            Widgets.DrawMenuSection(rect);
            HandleDropTarget(rect, pawn);

            // Portrait
            Rect portraitRect = new Rect(rect.x + 5f, rect.y + 5f, 60f, 60f);
            GUI.color = new Color(1f, 1f, 1f, 0.2f);
            Widgets.DrawHighlight(portraitRect);
            GUI.color = Color.white;
            
            RenderTexture portrait = PortraitsCache.Get(pawn, new Vector2(60f, 60f), Rot4.South, default(Vector3), 1f, true, true, false, false, null, null, false);
            if (portrait != null)
            {
                GUI.DrawTexture(portraitRect, portrait);
            }


            // Save as Role - top-right corner, out of the way of the name/portrait/edit-gear click targets.
            // Opens Role Mode with a pre-filled draft (see Dialog_GenerateLoadouts.StartRoleDraftFromPawn)
            // instead of instantly baking and saving - the player reviews/renames/adjusts factors in the
            // already-existing Role Mode preview and explicitly presses Create there.
            // 120f instead of 90f (ButtonText wrapped the translated "Save as Role" label into two squeezed
            // tiny lines). The name row next to it loses the 30px - it visibly truncates anyway. ButtonTextFit
            // handles languages for which even 120 is not enough.
            Rect saveRoleRect = new Rect(rect.xMax - 124f, rect.y + 3f, 120f, 22f);
            Loadout cardAssigned = pawn.GetLoadout();
            // DisplayLoadout, not the raw assignment (2026-08-01 fix, adopting the gating the old
            // Dialog_LoadoutEditor already had right before that dialog was removed) - a Role-assigned pawn
            // still has a personal "[Pawn-Loadout]" anchor (AssignTemplate wraps every assignment in one),
            // so checking the raw assignment alone can't
            // tell a Role apart from an ad-hoc one; this used to let "Save as Role" fire on a Role pawn too,
            // producing a doubled "[Role] [Role] X" draft name.
            Loadout cardDisplay = AmmoAssignmentUtility.DisplayLoadout(cardAssigned);
            bool cardIsRole = RoleUtility.IsRoleLoadout(cardDisplay);
            // cardDisplay instead of cardAssigned here too: for personalized colonists the raw value is the
            // anchor, which is never the default - so "Save as Role" also appeared for a colonist whose
            // anchor no longer has any template under it.
            // A pure pack carrier ALSO has something to save: even a pacifist with an assigned Equipment
            // Pack has enough for a role, so "Save as role" applies to them too.
            // This is intended: a role consisting ONLY of a pack is explicitly supported - CommitRole has its
            // own branch for exactly that (otherwise a role consisting ONLY of an Equipment Pack would wrongly
            // count as empty). The condition here simply didn't know that case and only asked about the
            // weapon template.
            RowInfo rowInfo = GetRowInfo(pawn);
            bool cardHasPacks = rowInfo.hasPacks;
            bool cardHasLoadout = (cardDisplay != null && !cardDisplay.defaultLoadout && !cardIsRole) || (cardHasPacks && !cardIsRole);
            if (cardIsRole)
            {
                // If the colonist already has a role, "save as role" is the wrong question - what is wanted
                // then is the role itself. Jumps to the Roles tab and selects EXACTLY this role instead of
                // building a draft from the colonist's parts.
                if (LoadoutUtility.ButtonTextFit(saveRoleRect, "CER_AssignLoadouts_EditRoleBtn".Translate()) && parentWindow != null)
                {
                    // The RAW name, not the display form: the role is internally named "[Role] <name>", and
                    // exactly this <name> is stored in the LoadoutRole object. DisplayLabel additionally
                    // prepends the configurable "R| " - the search would then come up empty.
                    string roleName = (cardDisplay.label ?? "").Substring(
                        (cardDisplay.label ?? "").StartsWith(RoleUtility.RoleMarker) ? RoleUtility.RoleMarker.Length : 0).Trim();
                    parentWindow.LoadoutsDialog.FocusExistingRole(roleName);
                    parentWindow.JumpToLoadouts();
                }
            }
            else if (cardHasLoadout)
            {
                if (LoadoutUtility.ButtonTextFit(saveRoleRect, "CER_AssignLoadouts_SaveAsRoleBtn".Translate()) && parentWindow != null)
                {
                    parentWindow.LoadoutsDialog.StartRoleDraftFromPawn(pawn, AmmoAssignmentUtility.ComposedDisplayLabel(pawn));
                    parentWindow.JumpToLoadouts();
                }
            }
            else
            {
                // Plain dimmed label, no button box, when there's nothing to save (2026-07-30:
                // alpha-tinting the WHOLE button - box, bevel and all - down to 35% made the button's own
                // beveled-edge highlight visually separate from the now-faint text, reading as a strikethrough.
                // House style: an inactive element should just look plain/dim, never struck through.
                GUI.color = new Color(1f, 1f, 1f, 0.35f);
                Text.Anchor = TextAnchor.MiddleCenter;
                // Same fitting as the button branches above, just without a button frame.
                GameFont prevRoleFont = Text.Font;
                bool prevRoleWrap = Text.WordWrap;
                Text.WordWrap = false;
                string dimmedRoleLbl = "CER_AssignLoadouts_SaveAsRoleBtn".Translate();
                if (Text.CalcSize(dimmedRoleLbl).x > saveRoleRect.width - 4f) Text.Font = GameFont.Tiny;
                Widgets.Label(saveRoleRect, dimmedRoleLbl.Truncate(saveRoleRect.width - 4f));
                Text.WordWrap = prevRoleWrap;
                Text.Font = prevRoleFont;
                Text.Anchor = TextAnchor.UpperLeft;
                GUI.color = Color.white;
            }
            // The hint must match the button: since the Edit-Role branch above was added, the NEW button
            // still showed "nothing to save here" - cardHasLoadout is deliberately false for a role carrier.
            // A button whose hint says the opposite of what it does is worse than none at all.
            if (LoadoutUtility.TipWanted(saveRoleRect)) TooltipHandler.TipRegion(saveRoleRect, cardIsRole
                ? "CER_AssignLoadouts_EditRoleTip".Translate()
                : (cardHasLoadout ? "CER_AssignLoadouts_SaveRoleTipHas".Translate()
                                  : "CER_AssignLoadouts_SaveRoleTipNone".Translate()));

            // Pencil/magnifier removed: this button was meant to go in BOTH compact AND card view, replaced
            // by the Other-item cell in the bottom row (DrawOtherItemsCell, already shipped here) - only
            // compact view actually got it, card view's own pencil (which opened the barebones
            // Dialog_LoadoutEditor, a dialog since removed entirely) was left in place by mistake.
            // Ammo/Pack/Sidearm/Other now cover everything that editor used to for an ad-hoc pawn; a Role
            // pawn's read-only banner has no equivalent here anyway (Roles are edited in Role Mode, not
            // per-pawn).

            // Name - width must clear the "Save as Role" button in the card's own top-right corner (94px
            // reserved from rect.xMax: 4px margin + 90px button - was 124px before the pencil above it was
            // removed) - otherwise a long name overlaps the button instead of truncating.
            Rect nameRect = new Rect(rect.x + 75f, rect.y + 5f, rect.width - 80f - 124f, 24f);
            Text.Font = GameFont.Small;
            string fullName = pawn.Name != null ? pawn.Name.ToStringFull : pawn.LabelShort;
            Widgets.Label(nameRect, fullName.Truncate(nameRect.width));
            TooltipHandler.TipRegion(nameRect, fullName);

            // Equipment tooltip lives on the portrait only, so it doesn't stack on top of the traits tooltip below.
            Rect hoverRect = new Rect(rect.x + 5f, rect.y + 5f, 60f, 60f);
            TooltipHandler.TipRegion(hoverRect, new TipSignal(() => GetEquipmentTooltip(pawn) + "CER_AssignLoadouts_ClickToViewEditGear".Translate(), pawn.GetHashCode() * 37));

            // Clicking the pawn opens the gear editor IN FRONT of this window - selecting the pawn on the map would
            // put the vanilla Gear tab uselessly behind the fullscreen tabs dialog.
            if (Widgets.ButtonInvisible(hoverRect) || Widgets.ButtonInvisible(nameRect))
                Find.WindowStack.Add(new Dialog_PawnGear(pawn));

            // Traits (16f: the Tiny-font line must not touch the ranged bar starting at y+42)
            Rect traitsRect = new Rect(rect.x + 75f, rect.y + 25f, rect.width - 80f, 16f);
            Text.Font = GameFont.Tiny;
            GUI.color = Color.gray;
            Widgets.Label(traitsRect, traitStrings[pawn].Truncate(traitsRect.width));
            if (!string.IsNullOrEmpty(traitTooltipStrings[pawn]))
            {
                TooltipHandler.TipRegion(traitsRect, traitTooltipStrings[pawn]);
            }
            GUI.color = Color.white;

            // Scores
            float rScore = rangedScores[pawn];
            float mScore = meleeScores[pawn];
            float maxScore = SkillRecord.MaxLevel * 10f;

            Rect rBarRect = new Rect(rect.x + 75f, rect.y + 42f, rect.width - 80f, 16f);
            Rect mBarRect = new Rect(rect.x + 75f, rect.y + 60f, rect.width - 80f, 16f);
            
            RowLabels cardLabels = GetRowLabels(pawn);   // preformatted, see GetRowLabels
            DrawScoreBarPrebuilt(rBarRect, rScore, maxScore, cardLabels.rangedCard);
            DrawScoreBarPrebuilt(mBarRect, mScore, maxScore, cardLabels.meleeCard);
            
            Rect scoreTooltipRect = new Rect(rect.x + 75f, rect.y + 42f, rect.width - 80f, 34f);
            if (LoadoutUtility.TipWanted(scoreTooltipRect))
                TooltipHandler.TipRegion(scoreTooltipRect, "CER_AssignLoadouts_ScoreCalcTooltip".Translate((maxScore / 10f).ToString()));

            // Loadout Assignment (rows sit tight under the skill bars - dead vertical space just cost card slots)
            Rect loadoutLabelRect = new Rect(rect.x + 5f, rect.y + 79f, 60f, 24f);
            Widgets.Label(loadoutLabelRect, "CER_AssignLoadouts_WeaponLabel".Translate());

            // Unwrap any hidden per-pawn "extra ammo" leaf (AmmoAssignmentUtility) straight away, so every
            // read of currentLoadout below - label, pack cell, edit button, main/side icons - keeps seeing
            // the real weapon template exactly as before, regardless of whether this pawn has one. Mass/bulk
            // still reads the RAW assignment (assignedLoadout) - that leaf's own Slots union is what
            // actually adds up to everything this pawn carries, extra ammo included.
            Loadout assignedLoadout = pawn.GetLoadout();
            Loadout currentLoadout = AmmoAssignmentUtility.DisplayLoadout(assignedLoadout);
            bool hasLoadout = currentLoadout != null && !currentLoadout.defaultLoadout;
            // CellLabel, not DisplayLabel - same as in the compact row: the short prefix only repeats the
            // icon next to it. The pick menu (OpenLoadoutPickMenu) still shows the full name.
            string btnLabel = hasLoadout ? LoadoutUtility.CellLabel(currentLoadout) : "CER_AssignLoadouts_NoneLoadout".Translate().ToString();

            // The loadout's main weapon + sidearm, shown as icons before the name (main weapon always first).
            // From GetRowInfo, see there - the same pick as in the compact row, now once per frame.
            ThingDef cardMainW = rowInfo.mainW, cardSideW = rowInfo.sideW;

            // Non-combatants (pacifists, babies - the same CannotFight rule the solver uses) can't be handed a
            // loadout manually either: the menu only ever offers "None", so a stray earlier assignment can still
            // be cleared but nothing new can be given.
            bool cannotFight = LoadoutUtility.CannotFight(pawn);

            // Reclaims the space the magnifier button used to sit in here - it moved up next to Save as Role.
            Rect loadoutBtnRect = new Rect(rect.x + 75f, rect.y + 79f, rect.width - 75f - 5f - 5f, 24f);
            // The status dot (drawn last, below) claims the button's own top-right corner - its hit-test
            // rect must BLOCK the button underneath there (no highlight, no click), not just sit visually
            // on top of it, so the invisible click-rect is shrunk to exclude that corner entirely. Icon/
            // label content below still uses the full loadoutBtnRect (unaffected, no visual shift).
            Rect btnHitRect = new Rect(loadoutBtnRect.x, loadoutBtnRect.y, loadoutBtnRect.width - DotReserveW, loadoutBtnRect.height);
            // No longer dimmed for cannotFight pawns - a pacifist can still carry an Equipment Pack (see
            // DrawEquipmentPackCell), so greying out the whole row read as "nothing works here" when
            // something usually does. The tooltip below still explains why no WEAPON can be assigned.
            // Shades of blue as in CE's own loadout column - darker blue = role, pale = own weapon template
            // (the blue shades distinguish roles and weapons, just like vanilla's "Current loadout"
            // outside).
            // The COMPACT view had this, the CARD view never did - the
            // earlier fix only caught one of the two draw sites. The same two constants, the same
            // condition, so the two views cannot drift apart again.
            // TintedButton instead of DrawAtlas+ButtonText: the second half painted over the first, so
            // the tint was NEVER visible - in both views and from day one. See LoadoutUtility.TintedButton.
            // LoadoutTintFor instead of hasLoadout+cardIsRole (a vanilla loadout created in CE had a blue
            // tint on its button it should not have): the condition here tinted EVERY loadout, not just
            // ours. The bug is older than the visibility fix - it simply wasn't visible until then. Now the
            // same rule as in CE's own column, from one function.
            bool loadoutClicked = LoadoutUtility.TintedButton(btnHitRect, LoadoutUtility.LoadoutTintFor(cardAssigned));
            {
                float ix = loadoutBtnRect.x + 5f;
                if (cardMainW != null) { Widgets.ThingIcon(new Rect(ix, loadoutBtnRect.y + 2f, 20f, 20f), cardMainW); ix += 22f; }
                if (cardSideW != null) { Widgets.ThingIcon(new Rect(ix, loadoutBtnRect.y + 2f, 20f, 20f), cardSideW); ix += 22f; }
                bool hasIcons = cardMainW != null;
                Text.Anchor = hasIcons ? TextAnchor.MiddleLeft : TextAnchor.MiddleCenter;
                Rect lblRect = LoadoutBtnLabelRect(loadoutBtnRect, ix, hasIcons);
                Widgets.Label(lblRect, btnLabel.Truncate(lblRect.width));
                Text.Anchor = TextAnchor.UpperLeft;
                GUI.color = Color.white;
                // The loadout button is also the drag handle for swapping loadouts between two colonists - dragging
                // the weapon selector itself reads more naturally than dragging the portrait.
                if (!cannotFight && hasLoadout) HandleDragHandle(loadoutBtnRect, pawn);
                // Same "dot wins" tooltip-exclusion as every other status dot in this dialog - only register
                // this wider tooltip while the mouse is NOT over the weapon dot's own corner.
                Rect weaponDotRectCard = WeaponDotRect(loadoutBtnRect);
                if (!Mouse.IsOver(weaponDotRectCard))
                {
                    // The whole chain goes behind the mouse check: LoadoutContentsTooltip walks every slot
                    // of the template and so ran per card per frame (LoadoutUtility.TipWanted).
                    if (LoadoutUtility.TipWanted(loadoutBtnRect))
                    {
                        if (dragPawn != null && dragPawn != pawn)
                            TooltipHandler.TipRegion(loadoutBtnRect, "CER_AssignLoadouts_DropSwapTip".Translate(dragPawn.LabelShortCap));
                        else if (cannotFight) TooltipHandler.TipRegion(loadoutBtnRect, "CER_AssignLoadouts_CannotFightTip".Translate()
                            + (hasLoadout ? "CER_AssignLoadouts_CannotFightClearSuffix".Translate().ToString() : ""));
                        else if (hasLoadout) TooltipHandler.TipRegion(loadoutBtnRect, LoadoutUtility.LoadoutContentsTooltip(currentLoadout)
                            + "CER_AssignLoadouts_DragToSwapSuffix".Translate());
                    }
                }
            }
            if (loadoutClicked) OpenLoadoutPickMenu(pawn, currentLoadout, hasLoadout, cannotFight);
            // Weapon status: same corner-dot convention as the Ammo/Pack/Sidearm cells, on the loadout button
            // itself - replaces the old separate "Equipped: <weapon>" detail row entirely (what's actually in
            // the pawn's hands right now, if different from the plan, now lives in this dot's own tooltip).
            DrawWeaponStatusDot(loadoutBtnRect, pawn, cardMainW);

            // Loadout mass & bulk vs. THIS pawn's carry capacity, drawn as fillable bars (same style as Generate
            // Loadouts). Always shown - with no loadout the load is simply 0. Generic "Any ..." entries use CE's own
            // category averages. Reads assignedLoadout (not the unwrapped currentLoadout) so any extra ammo
            // counts toward the total too.
            // Mass/bulk come from GetRowInfo (frame cache instead of once per pass) - the calculation itself
            // is unchanged, including the rule that it hangs off the ANCHOR and not the unwrapped weapon
            // template (otherwise a colonist with THREE directly attached packs showed 0.0/32.0 kg, because
            // the weapon template of a pure pack carrier is the "None" object. This matters because the
            // overload warning depends on exactly this number: if it reads 0, nothing warns).
            float lMass = rowInfo.mass, lBulk = rowInfo.bulk;
            GetCapacities(pawn, out float capMass, out float capBulk);

            // Both tooltip texts now sit BELOW in their own condition (see LoadoutUtility.TipWanted) - they
            // looked harmless here and still ran through RimWorld's translation per card per frame.

            // Right under the loadout row - used to sit a line lower, sharing space with the old "Equipped:"
            // detail row (now gone, see DrawWeaponStatusDot above).
            float by = rect.y + 105f;
            float halfW = (rect.width - 12f - 10f) / 2f;
            if (cardMassCaption == null)
            {
                cardMassCaption = "CER_AssignLoadouts_MassLabel".Translate();
                cardBulkCaption = "CER_AssignLoadouts_BulkLabel".Translate();
                cardMassCaptionW = Text.CalcSize(cardMassCaption).x + 2f;
                cardBulkCaptionW = Text.CalcSize(cardBulkCaption).x + 2f;
            }
            // The label gets what it needs, the bar the rest - capped at 55% of the half, so even a very
            // long translation never eats the whole bar (LabelCell then visibly truncates instead of
            // clipping hard).
            float massCapW = Mathf.Min(cardMassCaptionW, halfW * 0.55f);
            LoadoutUtility.LabelCell(new Rect(rect.x + 6f, by, massCapW, 20f), cardMassCaption);
            Rect massBar = new Rect(rect.x + 6f + massCapW + 2f, by + 1f, halfW - massCapW - 2f, 18f);
            bool massOver = DrawCapacityBar(massBar, lMass, capMass, GetRowLabels(pawn).mass, pawn, false, out var massBoosters);   // preformatted, see GetRowLabels
            Rect massTipRect = new Rect(rect.x + 6f, by, halfW, 20f);
            if (LoadoutUtility.TipWanted(massTipRect))
                TooltipHandler.TipRegion(massTipRect, WithOverCapNote(LoadoutUtility.CarryCapacityTooltip(pawn, false, lMass, capMass, massBoosters), massOver));

            float bx = rect.x + 6f + halfW + 10f;
            float bulkCapW = Mathf.Min(cardBulkCaptionW, halfW * 0.55f);
            LoadoutUtility.LabelCell(new Rect(bx, by, bulkCapW, 20f), cardBulkCaption);
            Rect bulkBar = new Rect(bx + bulkCapW + 2f, by + 1f, halfW - bulkCapW - 2f, 18f);
            bool bulkOver = DrawCapacityBar(bulkBar, lBulk, capBulk, GetRowLabels(pawn).bulk, pawn, true, out var bulkBoosters);
            Rect bulkTipRect = new Rect(bx, by, halfW, 20f);
            if (LoadoutUtility.TipWanted(bulkTipRect))
            {
                string bulkTip = LoadoutUtility.CarryCapacityTooltip(pawn, true, lBulk, capBulk, bulkBoosters);
                TooltipHandler.TipRegion(bulkTipRect, WithOverCapNote(bulkTip, bulkOver));
            }

            // Ammo/Pack/Sidearm/Other cells always all four present now, each cell showing "None"/dash itself
            // when there's nothing yet - a button that only sometimes exists (gated on whether ANY Equipment
            // Pack or sidearm-candidate loadout exists ANYWHERE in the game) read as broken/inconsistent, not
            // as "nothing to show here". Icon-only, same as compact view - these cells are too narrow for a
            // readable text label (it just truncated to "Ammo: .30-06 Sp...", "Pack: E| Ba...", never
            // actually readable), the icon carries the same info at a glance and full detail is still one
            // hover away in each cell's own tooltip. Four equal-width cells now (was three) - Other joined
            // the row alongside the existing three (2026-08-01) rather than replacing any of them, so all
            // four shrank proportionally to keep the row's own total width unchanged; DotReserveW/paste-strip/
            // overflow-badge math inside each cell is unaffected by width alone (same formulas as compact
            // view's already-narrower 68px cells, which ship fine today), just re-centers within less room.
            {
                by += 22f;
                float fullW = rect.width - 12f;
                // The paste column on the far right only takes width when something is actually on the
                // clipboard - otherwise the four cells share the full row as before.
                float pasteW = ClipboardHasAny() ? PasteColW + 6f : 0f;
                float cellW = (fullW - 3 * 6f - pasteW) / 4f;
                float cx = rect.x + 6f;
                // Category icon in front of each cell: in the compact view the column header says what the
                // cell is about - here there was none, the four cells sat side by side unlabeled. The icon is
                // taken FROM the cell width, not added in front of it, so the row's total width stays
                // unchanged and nothing slips out on the right.
                float catW;
                // Light grey background per group. A group is exactly what already shares one rectangle
                // here: category icon, paste icon, pick button and availability rectangle. No new measure -
                // `cellW` has always defined the group, it was just invisible, which is why the four cells
                // read as eight loose elements side by side. 2px margin above/below so the area encloses the
                // 24px content instead of clipping it.
                void GroupBg() => Widgets.DrawBoxSolid(new Rect(cx, by - 2f, cellW, 28f), LoadoutUtility.ColGroupBg);
                // The category icon belongs to the cell's CLICK AREA (where the 'Pack' tooltip appeared, a
                // click did nothing - only about 25 px further right did the same click open the pack
                // picker). And the numbers add up: the icon carries its own tooltip, but sits to the left
                // BEFORE the cell's button (18px) while visibly sitting inside the same grey group box - so
                // it looked like part of the cell and wasn't.
                // `extraHit` instead of pulling the button to the left: Widgets.ButtonText paints its own
                // background and would paint over the icon. ButtonInvisible draws nothing.
                Rect CatHit() => new Rect(cx, by, 18f, 24f);
                GroupBg();
                catW = LoadoutUtility.DrawCategoryIcon(new Rect(cx, by, cellW, 24f), LoadoutUtility.TexCatAmmo, "CER_AssignLoadouts_LabelAmmo".Translate());
                DrawChangeAmmoCell(new Rect(cx + catW, by, cellW - catW, 24f), pawn, currentLoadout, iconOnly: true, extraHit: CatHit());
                cx += cellW + 6f;
                GroupBg();
                catW = LoadoutUtility.DrawCategoryIcon(new Rect(cx, by, cellW, 24f), LoadoutUtility.TexCatPack, "CER_AssignLoadouts_LabelPack".Translate());
                DrawEquipmentPackCell(new Rect(cx + catW, by, cellW - catW, 24f), pawn, currentLoadout, iconOnly: true, extraHit: CatHit());
                cx += cellW + 6f;
                GroupBg();
                catW = LoadoutUtility.DrawCategoryIcon(new Rect(cx, by, cellW, 24f), LoadoutUtility.TexCatSidearm, "CER_AssignLoadouts_LabelSidearm".Translate());
                DrawSidearmCell(new Rect(cx + catW, by, cellW - catW, 24f), pawn, currentLoadout, iconOnly: true, extraHit: CatHit());
                cx += cellW + 6f;
                GroupBg();
                catW = LoadoutUtility.DrawCategoryIcon(new Rect(cx, by, cellW, 24f), LoadoutUtility.TexCatOther, "CER_AssignLoadouts_LabelOther".Translate());
                DrawOtherItemsCell(new Rect(cx + catW, by, cellW - catW, 24f), pawn, currentLoadout, iconOnly: true, extraHit: CatHit());
                if (pasteW > 0f)
                {
                    cx += cellW + 6f;
                    DrawPasteColumn(new Rect(cx, by, PasteColW, 24f), pawn, currentLoadout,
                        updated => AmmoAssignmentUtility.SetPawnPacks(pawn, currentLoadout, updated),
                        updated => PasteSidearmsGuarded(pawn, currentLoadout, updated),
                        mlo => { AmmoAssignmentUtility.AssignTemplate(pawn, mlo); CombatReadiness.Invalidate(); });
                }
            }

            // Stats Breakdown Box
            Rect breakdownRect = new Rect(rect.x + 5f, rect.y + 153f, rect.width - 10f, 45f);
            Widgets.DrawMenuSection(breakdownRect);

            RowLabels breakLabels = GetRowLabels(pawn);   // skills, capacities and both texts prebuilt, see GetRowLabels
            
            Text.Font = GameFont.Tiny;
            GUI.color = Color.gray;
            string b1 = breakLabels.b1;
            string b2 = breakLabels.b2;
            
            Text.Anchor = TextAnchor.MiddleLeft;
            Widgets.Label(new Rect(breakdownRect.x + 6f, breakdownRect.y + 6f, breakdownRect.width - 12f, 16f), b1);
            Widgets.Label(new Rect(breakdownRect.x + 6f, breakdownRect.y + 22f, breakdownRect.width - 12f, 16f), b2);
            Text.Anchor = TextAnchor.UpperLeft;
            GUI.color = Color.white;
            Text.Font = GameFont.Small;
            
            // Tooltip for Breakdown Box (precomputed in RefreshPawns).
            if (LoadoutUtility.TipWanted(breakdownRect))
            {
                if (!capacityTooltips.TryGetValue(pawn, out string breakdownTip)) breakdownTip = BuildCapacityTooltip(pawn);
                TooltipHandler.TipRegion(breakdownRect, breakdownTip);
            }
        }

        // The same window, opened from RimWorld's OWN assign tab.
        //
        // The lazy rework switched the "click the loadout cell" path everywhere to the weapon picker -
        // except where CE's own column sits. That cell kept opening only the old picker menu
        // (Nothing / Roles / Save combo). That was not a design decision, just the point where the
        // rework stopped.
        //
        // A THROWAWAY instance when no planner is open - exactly the construction the Arm-my-Colony
        // button on the same tab has always used: the constructor only reads the saved UI state, and the
        // window itself is never shown. If the planner is open, ITS instance is used, so filters and the
        // role toggle are the same as in the visible window.
        public static void OpenWeaponPickerFor(Pawn pawn)
        {
            if (pawn == null) return;
            Dialog_CEReloadedTabs tabs = Find.WindowStack.Windows.OfType<Dialog_CEReloadedTabs>().FirstOrDefault();
            Dialog_AssignLoadouts dlg = tabs?.AssignDialog ?? new Dialog_AssignLoadouts();
            Loadout current = AmmoAssignmentUtility.DisplayLoadout(pawn.GetLoadout());
            dlg.OpenLoadoutPickMenu(pawn, current, current != null && !current.defaultLoadout,
                LoadoutUtility.CannotFight(pawn));
        }

        // The per-colonist loadout picker menu, shared by the full cards and the compact rows.
        internal void OpenLoadoutPickMenu(Pawn pawn, Loadout currentLoadout, bool hasLoadout, bool cannotFight)
        {
            // Since the lazy rework a weapon picker window instead of a loadout FloatMenu: the player picks
            // the WEAPON (with score, stock, weight), and the shared template behind it is created invisibly
            // (EnsureDefaultLoadout). Hand-built loadouts and roles remain selectable in their own section -
            // the classic path doesn't go away, it's just no longer the only one. Generated templates NO
            // longer appear as list entries: they are bookkeeping, and their weapon is already in the
            // weapon list.
            Action nonePicked = () =>
            {
                var defLoadout = LoadoutManager.Loadouts.FirstOrDefault(l => l.defaultLoadout);
                Loadout cur = pawn.GetLoadout();
                // Remove ONLY THE WEAPON, not everything: pacifists have no weapon and still have Equipment
                // Packs - that has to keep working.
                // The cross-check is right below in the code: the pack cell explicitly allows a pacifist a
                // pack, and packOnlyPawns assigns packs to non-combatants on its own. "Pack without weapon"
                // is therefore an intended state - it just couldn't be reached via THIS path.
                // Cause: pawn.SetLoadout(default) replaces the whole anchor, and packs, sidearms and single
                // items hang on it as extra parents. Now the anchor stays and only loses its weapon parent
                // (parentID -> default loadout). The cell then shows "None", because DisplayLoadout ends at
                // the default loadout - exactly as before.
                // If there is really nothing on the anchor, the old clean path remains: otherwise the
                // colonist would drag an empty anchor around.
                bool hasExtras = cur != null && AmmoAssignmentUtility.IsAmmoLink(cur)
                    && (MultiParentUtility.GetExtraParents(cur).Count > 0
                        || (cur.OwnSlots != null && cur.OwnSlots.Count > 0));
                if (hasExtras && defLoadout != null)
                {
                    // The removed weapon's own ammo goes WITH it: the card afterwards said "Weapon: none"
                    // and its ammo cell was empty, while the colony calculation still listed the same
                    // colonist unchanged under "Assigned to: <name> x150" - two places in the same window
                    // contradicting each other, and the contradiction survived a window restart. Cause:
                    // when re-hung, the anchor keeps its own ammo slots, and the colony calculation reads
                    // exactly those.
                    // UnseedOwnAmmoForDetached instead of "remove all own ammo slots": it removes only what
                    // came from the detached template AND that no remaining weapon can fire - a personal
                    // ammo choice for a still-carried sidearm therefore stays. Called AFTER re-hanging,
                    // because it reads "what does the colonist still carry" from anchor.Slots.
                    Loadout detachedTemplate = cur.ParentLoadout;
                    cur.parentID = defLoadout.UniqueID;
                    if (detachedTemplate != null && !detachedTemplate.defaultLoadout)
                    {
                        AmmoAssignmentUtility.UnseedOwnAmmoForDetached(cur, new List<Loadout> { detachedTemplate });
                        LoadoutUtility.InvalidateAmmoOwnership();
                    }
                }
                else pawn.SetLoadout(defLoadout);
                CombatReadiness.Invalidate();   // 2026-08-01 - see ApplyPreviewPlan's apply() for why
            };
            // "Cannot fight" means NO WEAPON, not "nothing at all" - a baby (CannotCarryAnything) gets no
            // window at all; a pacifist gets one without the weapon section, with "None" and the
            // weapon-free loadouts/roles (the medic case).
            if (cannotFight && LoadoutUtility.CannotCarryAnything(pawn) && !hasLoadout)
            {
                Messages.Message("CER_AssignLoadouts_CannotFightMsg".Translate(pawn.LabelShortCap), MessageTypeDefOf.RejectInput, false);
                return;
            }

            // The window's loadout section: hand-built loadouts and roles. Excluded are Equipment Packs
            // (pure supply, would silently replace the weapon), the hidden per-colonist anchors (these
            // used to appear here AS ENTRIES, with a raw "[Pawn-Loadout] <id>" label - the wizard has long
            // guarded against that) and generated templates (their weapon is already in the weapon list).
            bool showWeapons = roleMode != RoleAssignMode.RolesOnly && !cannotFight;
            Func<List<Loadout>> loadoutOptions = () => LoadoutManager.Loadouts
                // Generated templates COMPLETELY out, including the current one (otherwise the current
                // template appeared twice - as a loadout row AND as the marked weapon; its weapon carries
                // the mark, that is enough).
                .Where(x => x != null && !x.defaultLoadout
                    && !EquipmentPackUtility.IsEquipmentPackLoadout(x)
                    && !AmmoAssignmentUtility.IsAmmoLink(x)
                    && !LoadoutUtility.IsGeneratedTemplate(x))
                .Where(l =>
                {
                    // Both directions: the picker filter follows the same toggle as the planner; the
                    // currently assigned loadout always stays visible (otherwise it couldn't be deselected).
                    if (l != currentLoadout)
                    {
                        bool isRole = RoleUtility.IsRoleLoadout(l);
                        if (roleMode == RoleAssignMode.RolesOnly && !isRole) return false;
                        if (roleMode == RoleAssignMode.NoRoles && isRole) return false;
                    }
                    // For a non-combatant exactly the weapon-free templates remain (slot union, so an
                    // inheriting role doesn't slip through; shields are not weapons).
                    if (cannotFight && (l.Slots ?? new List<LoadoutSlot>()).Any(s => s.thingDef != null && s.thingDef.IsWeapon)) return false;
                    return true;
                })
                .OrderBy(x => RoleUtility.DisplayLabel(x))
                .ToList();

            // If the current assignment is a generated template, the window marks its WEAPON as
            // selected - for the player the weapon IS the assignment; the template object doesn't exist
            // for them.
            Loadout curDisplay = currentLoadout;
            ThingDef curWeapon = null;
            if (curDisplay != null && LoadoutUtility.IsGeneratedTemplate(curDisplay)
                && AnalyzeLoadout(curDisplay, out ThingDef curMain, out _, out _, out _))
            {
                curWeapon = curMain;
                curDisplay = null;
            }

            // Prevent an empty window (some colonists got an empty "Choose weapon" - it should behave like
            // the baby case everywhere): a non-combatant without an assignment
            // and without a single weapon-free loadout gets the message instead of a window that contains
            // nothing but 'None'.
            if (cannotFight && !hasLoadout && loadoutOptions().Count == 0)
            {
                Messages.Message("CER_AssignLoadouts_CannotFightMsg".Translate(pawn.LabelShortCap), MessageTypeDefOf.RejectInput, false);
                return;
            }

            Pawn localPawn = pawn;
            // Assign and done - NO supply popup at this point (a deliberate decision): the shortfall is
            // shown collectively when leaving the tab (Dialog_CEReloadedTabs, signature comparison against
            // the state on entry).
            // Weapon and loadout go through the SAME path: the single-select mode materializes a weapon
            // itself (materializeWeapon) and always delivers a loadout here.
            Action<Loadout> pickedSingle = lo =>
            {
                AmmoAssignmentUtility.AssignTemplate(localPawn, lo);
                CombatReadiness.Invalidate();   // 2026-08-01 - see ApplyPreviewPlan's apply()
            };
            // "Save combination as role" is OUT here (it doesn't belong in the picker window) - the tab's
            // cards/compact rows still offer that path.

            // ONE window for both cases (modeled on the sidearm window instead of a separate one): the
            // same Dialog_PickPacks look as the sidearm
            // checklist, just in single-select mode. The weapon list is built ONCE on opening.
            List<ThingDef> mainChoices = showWeapons ? LoadoutUtility.MainWeaponChoices(curWeapon) : new List<ThingDef>();
            Find.WindowStack.Add(new Dialog_PickPacks(
                "CER_PickWeapon_SubtitleAssign".Translate(pawn.LabelShortCap.ToString()).ToString(),
                () => new List<Loadout>(), updated => { },
                windowTitle: "CER_PickWeapon_Title".Translate().ToString(),
                itemColumnLabel: "CER_GenerateLoadouts_WeaponBlocksHeader".Translate().ToString(),
                allOptions: loadoutOptions,
                emptyMessage: "CER_PickWeapon_NothingMatches".Translate().ToString(),
                // Bare name: the group header ('Roles' / 'Vanilla loadouts') already says what the row is.
                displayLabel: lo => LoadoutUtility.LoadoutBareName(lo) ?? "?",
                // Separate "Vanilla loadouts" and "Roles" sections instead of one combined header.
                groupLabel: lo => (RoleUtility.IsRoleLoadout(lo)
                    ? "CER_PickPacks_GroupRoles".Translate().ToString()
                    : "CER_PickPacks_GroupVanilla".Translate().ToString()),
                scoreFn: SidearmAssignmentUtility.ScoreOf,
                availDefFn: RepresentativeIcon,
                sourceLabel: pawn.LabelShortCap,
                weaponOptions: () => mainChoices,
                materializeWeapon: Dialog_GenerateLoadouts.EnsureDefaultLoadout,
                singleSelect: true,
                onPickedSingle: pickedSingle,
                onNonePicked: nonePicked,
                currentSingle: curDisplay, currentWeaponSingle: curWeapon,
                weaponGroupLabel: d => (d.IsRangedWeapon && !d.IsMeleeWeapon
                    ? "CER_AssignLoadouts_RangedHeader".Translate().ToString()
                    : "CER_AssignLoadouts_MeleeHeader".Translate().ToString()),
                // Copy source: the colonist's current assignment - it is pasted via the rows' paste
                // column (clipboard kind 'mainweapon').
                copyCurrent: () =>
                {
                    Loadout c = AmmoAssignmentUtility.DisplayLoadout(localPawn.GetLoadout());
                    return c != null && !c.defaultLoadout ? c : null;
                },
                // Locked weapons: if locks were toggled and colonists are, per their assignment, still
                // carrying locked weapons, closing asks for confirmation of a reassignment - it opens the
                // auto preview with keepAssigned on; the solver itself frees exactly the rows with a locked
                // weapon (AssignPlanner: carriesDisabled), the rest stays.
                // "Go back" leads BACK into the picker window - with freshly read assignment state, since
                // the pick before it may have changed it.
                onDisabledChanged: () =>
                {
                    int carriers = LoadoutUtility.DisabledWeaponCarrierCount();
                    if (carriers == 0) return;
                    Find.WindowStack.Add(LoadoutUtility.CompactConfirmation(
                        "CER_PickPacks_DisabledCarriersConfirm".Translate(carriers.ToString()),
                        () =>
                        {
                            RefreshPawns();
                            Find.WindowStack.Add(new Dialog_SquadSetup(null, this, presetKeepAssigned: true));
                        }, false,
                        cancelAct: () =>
                        {
                            Loadout curNow = AmmoAssignmentUtility.DisplayLoadout(localPawn.GetLoadout());
                            if (curNow != null && curNow.defaultLoadout) curNow = null;
                            OpenLoadoutPickMenu(localPawn, curNow, curNow != null, LoadoutUtility.CannotFight(localPawn));
                        }));
                }));
        }

        // Column headers for compact view - mirrors DrawPawnRowCompact's own column math exactly (same
        // literal offsets) so every label lines up with what's actually drawn below it. Same header
        // convention (Tiny font, 55% alpha, plain labels) Dialog_GenerateLoadouts' own Weapon Loadouts tab
        // list already uses - not a new style invented just for this view.
        private void DrawCompactHeader(Rect rect)
        {
            Text.Font = GameFont.Tiny;
            GUI.color = new Color(1f, 1f, 1f, 0.55f);
            if (LoadoutUtility.ColumnHeader(new Rect(rect.x + 34f, rect.y, 104f, rect.height), "CER_AssignLoadouts_ColonistHeader".Translate(),
                currentSortMode == SortMode.Name, sortDescending, true)) SetSort(SortMode.Name);
            // Sortable like the name column - the same rects as before, just clickable.
            if (LoadoutUtility.ColumnHeader(new Rect(rect.x + 214f, rect.y, 80f, rect.height), "CER_AssignLoadouts_RangedHeader".Translate(),
                currentSortMode == SortMode.Ranged, sortDescending, true)) SetSort(SortMode.Ranged);
            if (LoadoutUtility.ColumnHeader(new Rect(rect.x + 300f, rect.y, 80f, rect.height), "CER_AssignLoadouts_MeleeHeader".Translate(),
                currentSortMode == SortMode.Melee, sortDescending, true)) SetSort(SortMode.Melee);
            // Weapon column starts right after the Melee bar actually ends (326+80+6), not at a stale 366f -
            // that used to be 40px INSIDE the melee bar's own rect, overlapping it (2026-07-30). Narrows
            // the weapon column by the same 46px.
            Widgets.Label(new Rect(rect.x + WeaponColX, rect.y, 100f, rect.height), "CER_AssignLoadouts_WeaponHeader".Translate());

            // 80 -> 96: "177.1/92...." was truncated next to the warning sign, and there was still room.
            // Header and row identical.
            // 96 -> 112 ('260.5/92...' and '95.5/92....' were clipped). The cell carries THREE things: the
            // number (up to '999.9/999.9', Tiny ~55), the carrier icon (size+4 = 18) and the warning sign (16)
            // plus padding - about 100 in the extreme case; 96 was only enough without the icon. Header and
            // row the same.
            float bulkW = 112f, massW = 112f;
            Rect bulkBar = new Rect(rect.xMax - bulkW - 4f, rect.y, bulkW, rect.height);
            Rect massBar = new Rect(bulkBar.x - 6f - massW, rect.y, massW, rect.height);

            // Other is now a 4th icon-cell here, same size/row as Ammo/Pack/Sidearm (2026-08-01: it used
            // to be a separate undersized "+" button squeezed in before Mass with no
            // header label at all, the only one of the four not lined up with the others - see
            // DrawPawnRowCompact's own matching fix).
            const float compactCellW = 68f;
            // The same width calculation as in the row below: if the row reserves a paste column, the
            // header MUST subtract the same amount - otherwise the four labels sit askew over their cells
            // the moment something is on the clipboard.
            // Exactly this kind of drift was the reason for the earlier header fix.
            float headerPasteW = ClipboardHasAny() ? PasteColW + 6f : 0f;
            Rect loadoutBtnRect = new Rect(rect.x + WeaponColX, rect.y, massBar.x - 6f - (rect.x + WeaponColX) - 4 * compactCellW - headerPasteW - 24f, rect.height);
            Text.Anchor = TextAnchor.MiddleCenter;
            Widgets.Label(new Rect(loadoutBtnRect.xMax + 6f, rect.y, compactCellW, rect.height), "CER_AssignLoadouts_LabelAmmo".Translate());
            Widgets.Label(new Rect(loadoutBtnRect.xMax + 6f + compactCellW + 6f, rect.y, compactCellW, rect.height), "CER_AssignLoadouts_LabelPack".Translate());
            Widgets.Label(new Rect(loadoutBtnRect.xMax + 6f + 2 * (compactCellW + 6f), rect.y, compactCellW, rect.height), "CER_AssignLoadouts_LabelSidearm".Translate());
            Widgets.Label(new Rect(loadoutBtnRect.xMax + 6f + 3 * (compactCellW + 6f), rect.y, compactCellW, rect.height), "CER_AssignLoadouts_LabelOther".Translate());
            // The paste column gets a HEADER. The space had long been reserved but was never labeled - the
            // column consisted of a 26-point-wide icon at 65 % opacity and nothing else. It was overlooked
            // twice because of that, and both times the Copy button seemed to promise a column that doesn't
            // exist.
            // The first time only the icon was added; but an icon without a name in a table whose four
            // other columns carry names reads like decoration. The previous fix was incomplete.
            // THIRD attempt (only scattered stroke fragments of the Paste header were visible): the word
            // didn't fit into 26 points and wrapped into lines the header doesn't have.
            // Now the header shows the same icon as the rows below it - so it's clear what the column is -
            // and the name moves into the explanatory text. No text that has to fit into a column narrower
            // than any word for it in thirteen languages.
            if (headerPasteW > 0f)
            {
                Rect pasteHead = new Rect(loadoutBtnRect.xMax + 6f + 4 * (compactCellW + 6f), rect.y, PasteColW, rect.height);
                float ico = Mathf.Min(18f, pasteHead.height);
                GUI.color = new Color(1f, 1f, 1f, 0.8f);
                LoadoutUtility.DrawPasteIcon(new Rect(pasteHead.x + (pasteHead.width - ico) / 2f, pasteHead.y + (pasteHead.height - ico) / 2f, ico, ico));
                GUI.color = Color.white;
                TooltipHandler.TipRegion(pasteHead, "CER_AssignLoadouts_LabelPasteCol".Translate());
            }
            Text.Anchor = TextAnchor.UpperLeft;

            Widgets.Label(massBar, "CER_AssignLoadouts_MassLabel".Translate());
            Widgets.Label(bulkBar, "CER_AssignLoadouts_BulkLabel".Translate());

            GUI.color = Color.white;
            Text.Font = GameFont.Small;
        }

        // Compact view: everything essential from the card in one slim row (portrait, name, both skill bars, loadout
        // with weapon icons, mass/bulk). What no longer fits lives in the tooltips of the respective cell.
        private void DrawPawnRowCompact(Rect rect, Pawn pawn, bool alt)
        {
            DrawPawnRowCompactInner(rect, pawn, alt);
        }

        private void DrawPawnRowCompactInner(Rect rect, Pawn pawn, bool alt)
        {
            if (alt) Widgets.DrawLightHighlight(rect);
            if (Mouse.IsOver(rect)) Widgets.DrawHighlight(rect);
            HandleDropTarget(rect, pawn);

            // Portrait + name open the gear editor, exactly like the card.
            Rect portraitRect = new Rect(rect.x + 2f, rect.y, 26f, 30f);
            RenderTexture portrait = PortraitsCache.Get(pawn, new Vector2(26f, 30f), Rot4.South, default(Vector3), 1f, true, true, false, false, null, null, false);
            if (portrait != null) GUI.DrawTexture(portraitRect, portrait);
            TooltipHandler.TipRegion(portraitRect, new TipSignal(() => GetEquipmentTooltip(pawn) + "CER_AssignLoadouts_ClickToViewEditGear".Translate(), pawn.GetHashCode() * 37));

            // 130 -> 104 (see WeaponColX): the full name is in the mouseover anyway.
            Rect nameRect = new Rect(rect.x + 34f, rect.y, 104f, rect.height);
            Text.Anchor = TextAnchor.MiddleLeft;
            Widgets.Label(nameRect, pawn.LabelShortCap.Truncate(100f));
            Text.Anchor = TextAnchor.UpperLeft;
            // Whole block behind the mouse check (see LoadoutUtility.TipWanted): ToStringFull,
            // BuildCapacityTooltip and the concatenation ran per row per frame, while at most one of them
            // was ever visible.
            if (LoadoutUtility.TipWanted(nameRect))
            {
                string fullName = pawn.Name != null ? pawn.Name.ToStringFull : pawn.LabelShort;
                if (!capacityTooltips.TryGetValue(pawn, out string breakdownTip)) breakdownTip = BuildCapacityTooltip(pawn);
                string traits = traitStrings.TryGetValue(pawn, out string tr) && !string.IsNullOrEmpty(tr) ? "\n" + tr : "";
                TooltipHandler.TipRegion(nameRect, fullName + traits + "\n\n" + breakdownTip);
            }
            if (Widgets.ButtonInvisible(portraitRect) || Widgets.ButtonInvisible(nameRect))
                Find.WindowStack.Add(new Dialog_PawnGear(pawn));

            // Save as Role - between the (now narrower) name and the skill bars. "Save" reads as a plain
            // action label at this width; the tooltip's own lead line spells out what it actually does
            // (2026-07-30: was labeled "Role", which read as a status/filter rather than an action button).
            // 54f instead of 44f (same overflow class as the card button: the German "Save" label needs
            // more than 44) - there is room up to the skill bar at +226, and ButtonTextFit scales the
            // rest down instead of squeezing two lines.
            Rect saveRoleRectCompact = new Rect(rect.x + 144f, rect.y + (rect.height - 22f) / 2f, 54f, 22f);
            Loadout compactAssigned = pawn.GetLoadout();
            // See cardHasLoadout's own comment above - same DisplayLoadout-unwrap fix (2026-08-01).
            bool compactIsRole = RoleUtility.IsRoleLoadout(AmmoAssignmentUtility.DisplayLoadout(compactAssigned));
            // The SAME three cases as in the card view. The rework was first done there and this spot was
            // left behind - exactly the bug class that occurs most often in this mod (two draw sites, one
            // touched). "Edit role" doesn't fit into 44 pixels here, hence a short word of its own; the
            // effect is identical.
            RowInfo rowInfo = GetRowInfo(pawn);
            bool compactHasPacks = rowInfo.hasPacks;
            bool compactHasLoadout = ((compactAssigned != null && !compactAssigned.defaultLoadout) || compactHasPacks) && !compactIsRole;
            if (compactIsRole)
            {
                if (LoadoutUtility.ButtonTextFit(saveRoleRectCompact, "CER_AssignLoadouts_EditBtnCompact".Translate()) && parentWindow != null)
                {
                    Loadout compactDisplay = AmmoAssignmentUtility.DisplayLoadout(compactAssigned);
                    string compactRoleName = (compactDisplay?.label ?? "").Substring(
                        (compactDisplay?.label ?? "").StartsWith(RoleUtility.RoleMarker) ? RoleUtility.RoleMarker.Length : 0).Trim();
                    parentWindow.LoadoutsDialog.FocusExistingRole(compactRoleName);
                    parentWindow.JumpToLoadouts();
                }
            }
            else if (compactHasLoadout)
            {
                if (LoadoutUtility.ButtonTextFit(saveRoleRectCompact, "CER_AssignLoadouts_SaveBtnCompact".Translate()) && parentWindow != null)
                {
                    parentWindow.LoadoutsDialog.StartRoleDraftFromPawn(pawn, AmmoAssignmentUtility.ComposedDisplayLabel(pawn));
                    parentWindow.JumpToLoadouts();
                }
            }
            else
            {
                // Same house-style fix as the card view's own button - see its comment (2026-07-30).
                GUI.color = new Color(1f, 1f, 1f, 0.35f);
                Text.Anchor = TextAnchor.MiddleCenter;
                GameFont prevCompactFont = Text.Font;
                bool prevCompactWrap = Text.WordWrap;
                Text.WordWrap = false;
                string dimmedCompactLbl = "CER_AssignLoadouts_SaveBtnCompact".Translate();
                if (Text.CalcSize(dimmedCompactLbl).x > saveRoleRectCompact.width - 4f) Text.Font = GameFont.Tiny;
                Widgets.Label(saveRoleRectCompact, dimmedCompactLbl.Truncate(saveRoleRectCompact.width - 4f));
                Text.WordWrap = prevCompactWrap;
                Text.Font = prevCompactFont;
                Text.Anchor = TextAnchor.UpperLeft;
                GUI.color = Color.white;
            }
            if (LoadoutUtility.TipWanted(saveRoleRectCompact)) TooltipHandler.TipRegion(saveRoleRectCompact, compactIsRole
                ? "CER_AssignLoadouts_EditRoleTip".Translate()
                : (compactHasLoadout ? "CER_AssignLoadouts_SaveRoleTipCompactHas".Translate()
                                     : "CER_AssignLoadouts_SaveRoleTipCompactNone".Translate()));

            // Both skill bars, same colors and tooltip as on the card.
            float maxScore = SkillRecord.MaxLevel * 10f;
            Rect rBar = new Rect(rect.x + 200f, rect.y + 6f, 80f, 18f);
            Rect mBar = new Rect(rect.x + 286f, rect.y + 6f, 80f, 18f);
            RowLabels rowLabels = GetRowLabels(pawn);   // preformatted, see GetRowLabels
            DrawScoreBarPrebuilt(rBar, rangedScores[pawn], maxScore, rowLabels.rangedCompact);
            DrawScoreBarPrebuilt(mBar, meleeScores[pawn], maxScore, rowLabels.meleeCompact);
            Rect skillTipRect = new Rect(rBar.x, rect.y, 172f, rect.height);
            if (LoadoutUtility.TipWanted(skillTipRect)) TooltipHandler.TipRegion(skillTipRect, "CER_AssignLoadouts_RangedMeleeScoreTip".Translate());

            // Own section, so no gap remains between the measured blocks: in the card view the same three
            // queries (GetLoadout, DisplayLoadout, CannotFight) are part of "Card: weapon button", here they
            // sit in the code before the bars. When comparing the views, add them to "Compact: weapon
            // button".
            // Right-aligned fixed cells: bulk, mass, loadout editor. The loadout button takes the middle remainder.
            // Mass and Bulk are the SAME width now (Mass made narrower, same as
            // Bulk) - was wider than Bulk for no real reason (Mass's "kg" suffix fits fine at this width too).
            // 80 -> 96: "177.1/92...." was truncated next to the warning sign, and there was still room.
            // Header and row identical.
            // 96 -> 112 ('260.5/92...' and '95.5/92....' were clipped). The cell carries THREE things: the
            // number (up to '999.9/999.9', Tiny ~55), the carrier icon (size+4 = 18) and the warning sign (16)
            // plus padding - about 100 in the extreme case; 96 was only enough without the icon. Header and
            // row the same.
            float bulkW = 112f, massW = 112f;
            Rect bulkBar = new Rect(rect.xMax - bulkW - 4f, rect.y + 6f, bulkW, 18f);
            Rect massBar = new Rect(bulkBar.x - 6f - massW, rect.y + 6f, massW, 18f);

            // Unwrap any hidden per-pawn "extra ammo" leaf (AmmoAssignmentUtility) for display; mass/bulk
            // reads the raw assignment so extra ammo still counts toward the total.
            Loadout assignedLoadout = pawn.GetLoadout();
            Loadout currentLoadout = AmmoAssignmentUtility.DisplayLoadout(assignedLoadout);
            bool hasLoadout = currentLoadout != null && !currentLoadout.defaultLoadout;
            bool cannotFight = LoadoutUtility.CannotFight(pawn);

            // Mass/bulk come from GetRowInfo (frame cache instead of once per pass) - the calculation itself
            // is unchanged, including the rule that it hangs off the ANCHOR and not the unwrapped weapon
            // template (otherwise a colonist with THREE directly attached packs showed 0.0/32.0 kg, because
            // the weapon template of a pure pack carrier is the "None" object. This matters because the
            // overload warning depends on exactly this number: if it reads 0, nothing warns).
            float lMass = rowInfo.mass, lBulk = rowInfo.bulk;
            GetCapacities(pawn, out float capMass, out float capBulk);

            Text.Font = GameFont.Tiny;
            // The same overload warning as in the card view - second occurrence of the same pair of bars,
            // found by grep, so the compact row isn't the only view that shows an uncarriable loadout
            // without comment.
            bool massOverC = DrawCapacityBar(massBar, lMass, capMass, GetRowLabels(pawn).mass, pawn, false, out var massBoostersC);   // preformatted, see GetRowLabels
            if (LoadoutUtility.TipWanted(massBar)) TooltipHandler.TipRegion(massBar, WithOverCapNote(LoadoutUtility.CarryCapacityTooltip(pawn, false, lMass, capMass, massBoostersC), massOverC));
            bool bulkOverC = DrawCapacityBar(bulkBar, lBulk, capBulk, GetRowLabels(pawn).bulk, pawn, true, out var bulkBoostersC);
            if (LoadoutUtility.TipWanted(bulkBar)) TooltipHandler.TipRegion(bulkBar, WithOverCapNote(LoadoutUtility.CarryCapacityTooltip(pawn, true, lBulk, capBulk, bulkBoostersC), bulkOverC));
            Text.Font = GameFont.Small;

            // Loadout button with the main weapon + sidearm icons, filling the space between skills and mass -
            // the Ammo/Pack/Sidearm cells always all three present now (each shows "None" itself rather than
            // vanishing when nothing exists yet anywhere in the game), each taking a rightmost slice.
            // Icon-only, one fixed width for all three - these used to be full text buttons ("Ammo: None",
            // "Pack: E| Base Supply", "Sidearm: W| knife") that each ended up a different width depending on
            // label length, the opposite of "compact". The icon carries the same info at a glance; full detail
            // is still one hover away in each cell's (unchanged) tooltip. Doubled from the original 34f - the
            // fulfillment status icon each cell now also draws needs the extra room to sit next to the item
            // icon instead of overlapping it.
            // Other is now a 4th icon-cell, same size and row as Ammo/Pack/Sidearm (2026-08-01
            // - see DrawCompactHeader's own matching fix for the full story: it used to be a separate,
            // undersized bare "+" button squeezed in before Mass, with no header label and no dash/icon+badge
            // rendering like its siblings - now just another call to the same DrawOtherItemsCell the card
            // view already uses).
            // The same single paste column as in the card view, after "Other" and before the mass bar.
            // Only takes width when something is actually on the clipboard.
            float compactPasteW = ClipboardHasAny() ? PasteColW + 6f : 0f;
            const float compactCellW = 68f;
            float ammoCellW = compactCellW;
            float packCellW = compactCellW;
            float sidearmCellW = compactCellW;
            float otherCellW = compactCellW;
            // From GetRowInfo (frame cache): LoadoutMainAndSide rebuilds the slot union and creates four
            // intermediate LINQ lists - that used to run per row per pass.
            ThingDef rowMainW = rowInfo.mainW, rowSideW = rowInfo.sideW;
            Rect loadoutBtnRect = new Rect(rect.x + WeaponColX, rect.y + 3f, massBar.x - 6f - (rect.x + WeaponColX) - sidearmCellW - packCellW - ammoCellW - otherCellW - compactPasteW - 24f, 24f);
            DrawChangeAmmoCell(new Rect(loadoutBtnRect.xMax + 6f, rect.y + 3f, ammoCellW, 24f), pawn, currentLoadout, iconOnly: true);
            DrawEquipmentPackCell(new Rect(loadoutBtnRect.xMax + 6f + ammoCellW + 6f, rect.y + 3f, packCellW, 24f), pawn, currentLoadout, iconOnly: true);
            DrawSidearmCell(new Rect(loadoutBtnRect.xMax + 6f + ammoCellW + 6f + packCellW + 6f, rect.y + 3f, sidearmCellW, 24f), pawn, currentLoadout, iconOnly: true);
            DrawOtherItemsCell(new Rect(loadoutBtnRect.xMax + 6f + ammoCellW + 6f + packCellW + 6f + sidearmCellW + 6f, rect.y + 3f, otherCellW, 24f), pawn, currentLoadout, iconOnly: true);
            if (compactPasteW > 0f)
                DrawPasteColumn(new Rect(loadoutBtnRect.xMax + 6f + ammoCellW + 6f + packCellW + 6f + sidearmCellW + 6f + otherCellW + 6f, rect.y + 3f, PasteColW, 24f),
                    pawn, currentLoadout,
                    updated => AmmoAssignmentUtility.SetPawnPacks(pawn, currentLoadout, updated),
                    updated => PasteSidearmsGuarded(pawn, currentLoadout, updated),
                    mlo => { AmmoAssignmentUtility.AssignTemplate(pawn, mlo); CombatReadiness.Invalidate(); });

            // No longer dimmed for cannotFight pawns - a pacifist can still carry an Equipment Pack (see
            // DrawEquipmentPackCell), so greying out the whole row read as "nothing works here" when
            // something usually does. The tooltip below still explains why no WEAPON can be assigned.
            // Button drawn into a rect narrowed by DotReserveW, not the full loadoutBtnRect (2026-08-01,
            // the button's own background touched the availability dot with no gap, unlike
            // every other cell in this row) - mirrors the card view's own btnHitRect convention exactly
            // (icon/label/WeaponDotRect below still use the full-width loadoutBtnRect, unaffected).
            Rect btnHitRect = new Rect(loadoutBtnRect.x, loadoutBtnRect.y, loadoutBtnRect.width - DotReserveW, loadoutBtnRect.height);
            // Shades of blue as in CE's own loadout column (the blue shades distinguish roles and weapons,
            // just like vanilla's "Current loadout" outside) - darker blue = role, pale = own weapon
            // template. This is
            // also the replacement for the dropped prefix: the distinction now lives in the color instead
            // of in three characters of text.
            // TintedButton instead of DrawAtlas+ButtonText: the second half painted over the first, so
            // the tint was NEVER visible - in both views and from day one. See LoadoutUtility.TintedButton.
            // See the card view - the same rule from the same function, so the two views cannot drift
            // apart again.
            bool loadoutClicked = LoadoutUtility.TintedButton(btnHitRect, LoadoutUtility.LoadoutTintFor(compactAssigned));
            float ix = loadoutBtnRect.x + 5f;
            if (rowMainW != null) { Widgets.ThingIcon(new Rect(ix, loadoutBtnRect.y + 2f, 20f, 20f), rowMainW); ix += 22f; }
            if (rowSideW != null) { Widgets.ThingIcon(new Rect(ix, loadoutBtnRect.y + 2f, 20f, 20f), rowSideW); ix += 22f; }
            Text.Anchor = rowMainW != null ? TextAnchor.MiddleLeft : TextAnchor.MiddleCenter;
            Rect lblRect = LoadoutBtnLabelRect(loadoutBtnRect, ix, rowMainW != null);
            // CellLabel instead of DisplayLabel (prefix gone) and one font size smaller
            // - together with the 14px from the column shift
            // that is enough for the whole weapon name instead of "W| R-6 ...".
            GameFont prevLblFont = Text.Font;
            Text.Font = GameFont.Tiny;
            Widgets.Label(lblRect, (hasLoadout ? LoadoutUtility.CellLabel(currentLoadout) : "CER_AssignLoadouts_NoneLoadout".Translate().ToString()).Truncate(lblRect.width));
            Text.Font = prevLblFont;
            Text.Anchor = TextAnchor.UpperLeft;
            GUI.color = Color.white;
            if (!cannotFight && hasLoadout) HandleDragHandle(loadoutBtnRect, pawn);
            // The weapon status dot (drawn last, see below) claims its own corner of loadoutBtnRect - while
            // the mouse is over it, it must win outright, not stack its own tooltip underneath this wider one.
            Rect weaponDotRect = WeaponDotRect(loadoutBtnRect);
            if (!Mouse.IsOver(weaponDotRect))
            {
                // See the identical chain in DrawPawnCard - the same per row per frame.
                if (LoadoutUtility.TipWanted(loadoutBtnRect))
                {
                    if (dragPawn != null && dragPawn != pawn)
                        TooltipHandler.TipRegion(loadoutBtnRect, "CER_AssignLoadouts_DropSwapTip".Translate(dragPawn.LabelShortCap));
                    else if (cannotFight) TooltipHandler.TipRegion(loadoutBtnRect, "CER_AssignLoadouts_CannotFightTip".Translate()
                        + (hasLoadout ? "CER_AssignLoadouts_CannotFightClearSuffix".Translate().ToString() : ""));
                    else if (hasLoadout) TooltipHandler.TipRegion(loadoutBtnRect, LoadoutUtility.LoadoutContentsTooltip(currentLoadout)
                        + "CER_AssignLoadouts_DragToSwapSuffix".Translate());
                }
            }
            if (loadoutClicked) OpenLoadoutPickMenu(pawn, currentLoadout, hasLoadout, cannotFight);
            // Weapon status: same corner-dot convention as the Ammo/Pack/Sidearm cells, on the loadout button
            // itself - replaces the old separate "Equipped: <weapon>" detail line entirely (what's actually in
            // the pawn's hands right now, if different from the plan, now lives in this dot's own tooltip).
            DrawWeaponStatusDot(loadoutBtnRect, pawn, rowMainW);
        }

        // ---- Change-ammo cell (card + compact) --------------------------------------------------------------

        // Opens Dialog_ChangeAmmo for this ONE pawn - extra ammo on top of whatever their weapon loadout (and
        // any attached Equipment Pack) already provides, via a hidden per-pawn leaf loadout (see
        // AmmoAssignmentUtility) instead of touching the shared template. `currentLoadout` here is already
        // unwrapped past that leaf (DisplayLoadout was applied where this pawn's card/row builds it) - it is
        // exactly the template the leaf should sit on top of.
        private void DrawChangeAmmoCell(Rect cell, Pawn pawn, Loadout currentLoadout, bool iconOnly = false, Rect? extraHit = null)
        {
            DrawChangeAmmoCellInner(cell, pawn, currentLoadout, iconOnly, extraHit);
        }

        private void DrawChangeAmmoCellInner(Rect cell, Pawn pawn, Loadout currentLoadout, bool iconOnly, Rect? extraHit)
        {
            // Also without a main weapon, as soon as a sidearm on CE's None loadout needs ammo: the anchor
            // carries its ammo in its slot union, yet the cell still showed the dash and the ammo window was
            // unreachable. The window itself works via the anchor (GetAmmoLeaf), not via the template - it
            // needs no main weapon.
            bool hasLoadout = currentLoadout != null
                && (!currentLoadout.defaultLoadout || GetAmmoCellState(pawn).pool.Count > 0);
            Text.Font = GameFont.Tiny;
            if (!hasLoadout)
            {
                // Compact view: the same helper and the same geometry as every other empty state (the
                // buttons had different sizes; the first fix forgot the narrowing and was 12px off).
                // Previously this was a raw label over the full cell with the currently valid anchor - the
                // dash sat top left instead of centered.
                if (iconOnly)
                {
                    // Empty state on the SAME geometry as a filled cell - which is now the full cell, see
                    // the comment at DotReserveW.
                    DrawCellDash(cell);
                    Text.Font = GameFont.Small;
                    return;
                }
                GUI.color = new Color(1f, 1f, 1f, 0.35f);
                Widgets.Label(cell, "CER_AssignLoadouts_AmmoDashLabel".Translate());
                GUI.color = Color.white;
                Text.Font = GameFont.Small;
                return;
            }
            // The FULLY RESOLVED ammo the pawn actually carries - a Weapon Loadout template's own baked
            // default counts just as much as a personal override (AmmoAssignmentUtility/
            // Patch_Loadout_MultiParentSlots' full-replace semantics). Reading only the personal anchor's
            // OwnSlots here (ExtraAmmoSlots) showed "None" for every pawn who never opened the Ammo
            // Loadouts popup, even when their weapon loadout already carries a perfectly real ammo type -
            // "None" must mean "genuinely carries nothing", not "never personally touched."
            AmmoCellState ammoState = GetAmmoCellState(pawn);
            List<LoadoutSlot> effective = ammoState.effective;
            List<ThingDef> pool = ammoState.pool;
            List<ThingDef> active = ammoState.active;
            // pool empty = no ammo-using weapon at all, nothing to configure; pool full and active empty =
            // needs ammo, has none, a real gap.
            // The TEXT for it is only built in the branch that actually draws it (measured): in the
            // compact view this cell only holds an icon, so the text was built, translated and thrown away
            // per colonist per pass.
            bool noAmmoWarning = pool.Count > 0 && active.Count == 0;
            GUI.color = noAmmoWarning ? LoadoutUtility.ColBad : Color.white;
            // The status dot's own corner is reserved from the BUTTON's hit-rect (not just drawn visually
            // on top of it) - Widgets.ButtonText handles background+highlight+click as one atomic call over
            // whatever rect it's given, so the only way for the dot to truly "win" over the button
            // underneath (no highlight, no click) is for the button's own rect to never extend under it.
            Rect btnRect = new Rect(cell.x, cell.y, cell.width - DotReserveW, cell.height);
            bool clicked;
            if (iconOnly)
            {
                clicked = Widgets.ButtonText(btnRect, "") | (extraHit.HasValue && Widgets.ButtonInvisible(extraHit.Value));
                if (pool.Count == 0) DrawCellDash(cell);
                else if (noAmmoWarning) DrawCellWarning(cell);
                else DrawCellIcon(cell, active[0], active.Count > 1 ? active.Count : 0);
            }
            else
            {
                string summary = pool.Count == 0 ? "CER_AssignLoadouts_AmmoSummaryNone".Translate().ToString()
                    : noAmmoWarning ? "CER_AssignLoadouts_ZeroTypesLabel".Translate().ToString()
                    : active.Count == 1 ? active[0].LabelCap.ToString()
                    : "CER_AssignLoadouts_TypesCountFmt".Translate(active.Count.ToString()).ToString();
                clicked = Widgets.ButtonText(btnRect, "CER_AssignLoadouts_AmmoPrefixFmt".Translate(summary.Truncate(btnRect.width - 44f))) | (extraHit.HasValue && Widgets.ButtonInvisible(extraHit.Value));
            }
            // Requested amount = THIS colonist's slot amount for this type; the reasoning is at
            // AmmoSegFor.
            Rect ammoDot = DrawFulfillDot(cell, pawn, active, "CER_AssignLoadouts_LabelAmmo",
                RichStock.Ammo, richNeedFor: d => effective.Where(s => s.thingDef == (ThingDef)d).Sum(s => s.count));
            GUI.color = Color.white;
            Text.Font = GameFont.Small;
            if (!Mouse.IsOver(ammoDot) && LoadoutUtility.TipWanted(cell))
                TooltipHandler.TipRegion(cell, (pool.Count == 0
                    ? "CER_AssignLoadouts_AmmoTipNoWeapon".Translate(pawn.LabelShortCap)
                    : noAmmoWarning
                        ? "CER_AssignLoadouts_AmmoTipNoneSetup".Translate(pawn.LabelShortCap)
                        // AmmoCountFor instead of effective.First(...): since "Any ammo" counts as a chosen
                        // selection, `active` can also contain a type that exists only as a GENERIC slot and
                        // has no concrete slot at all - First() threw for it, in OnGUI, every frame, as long
                        // as the pointer was on the cell. Precisely in the "Any ammo" case the addition was
                        // built for (found in review).
                        : "CER_AssignLoadouts_AmmoTipList".Translate(pawn.LabelShortCap, string.Join(", ", active.Select(d => d.LabelCap.ToString() + " x" + AmmoCountFor(effective, d)))))
                    + "CER_AssignLoadouts_AmmoTipClickSuffix".Translate());
            if (clicked) Find.WindowStack.Add(new Dialog_ChangeAmmo(pawn, currentLoadout));
        }

        // ---- Shared icon-only cell rendering (compact view) ---------------------------------------------------
        // Same fixed-width treatment for the Ammo/Pack/Sidearm cells - a single representative icon plus a
        // "+N" corner badge when there's more than one, instead of a text label that made the three cells
        // different widths depending on how long a loadout's name happened to be. Full detail stays in the
        // (unchanged) tooltip; nothing here is lost, just not spelled out in the row itself anymore.
        // Centered within the cell MINUS the fulfill-dot's own always-reserved strip (DotReserveW) on the
        // right edge - not the full cell (2026-08-01: glyphs still looked off-center after
        // the paste-button fix, in BOTH card and compact view). DrawFulfillDot draws its corner strip at
        // cell.xMax-10..cell.xMax on this SAME cell, but every glyph drawn here still centered itself
        // against the FULL cell width as if that strip didn't exist - visually shifting the icon right of
        // where the remaining (non-dot) space's own true center actually is.
        private static Rect VisualCell(Rect cell) => new Rect(cell.x, cell.y, cell.width - DotReserveW, cell.height);

        private static void DrawCellDash(Rect cell)
        {
            GUI.color = new Color(1f, 1f, 1f, 0.35f);
            Text.Anchor = TextAnchor.MiddleCenter;
            Widgets.Label(VisualCell(cell), "CER_AssignLoadouts_DashPlaceholder".Translate());
            Text.Anchor = TextAnchor.UpperLeft;
            GUI.color = Color.white;
        }

        private static void DrawCellWarning(Rect cell)
        {
            // The PNG exclamation mark, centered (for ammo, not a plain text exclamation mark but the PNG
            // one, in the middle).
            // The text glyph was the last place still going through Text.CurFontStyle - see
            // LoadoutUtility.DrawWarningBadge for why that looked different everywhere. White on the red
            // cell, not tinted.
            Rect vc = VisualCell(cell);
            float s = Mathf.Min(16f, vc.height - 4f);
            LoadoutUtility.DrawExclamationGlyph(new Rect(vc.x + (vc.width - s) / 2f, vc.y + (vc.height - s) / 2f, s, s), Color.white);
        }

        // Per-pawn "equipped" check on top of LoadoutUtility's shared FulfillStatus (color/word live there
        // now so Dialog_ChangeAmmo's ammo rows can use the exact same square-and-wording convention) -
        // equipped beats sitting in a stockpile beats merely being queued for production beats having none
        // of the above. Compact view's per-cell status dot shows the WORST of these across everything the
        // cell represents, so a Pack with 3 items where only 1 is missing still flags as "not fully ready"
        // instead of quietly showing green because most of it happens to be fine. Internal so Dialog_
        // LoadoutEditor's own rows (drawn in a separate floating Window) can reuse the SAME frameOwnedCache
        // this instance already refreshes every frame it's on screen - correct as long as the editor is
        // only ever opened while this dialog (or its owning tab window) is still visible, which is always
        // true today since it's opened FROM one of this dialog's own cells.
        // Accepts a plain ThingDef OR a LoadoutGenericDef (e.g. a pack's "Any Medicine"/"Any Meal" slot) - a
        // generic has no single concrete def to check ownership/stock against, so it gets its own family-wide
        // resolution (LoadoutUtility.PawnAlreadyHasGeneric/GenericStockCount) instead of ThingDef's per-def cache.
        // `needed` is the quantity the loadout REQUESTS. Previously this function only asked "does the
        // colonist have such a thing at all" - a colonist with 1 of 5 medicine thus counted as fully
        // equipped, the five-pack first-aid kit was green, and since green means "nothing to do", no
        // resupply was planned either. Default 1 kept so callers without a quantity concept (weapon,
        // sidearm - where the requested amount is always 1) behave exactly as before.
        // The verdict from the shared turn-based calculation (see LoadoutClaims), if it has anything to
        // say about exactly this colonist and exactly this item. false does NOT mean "available", but
        // "not asked" - then the caller decides as before. Exactly this distinction is the reason for the
        // evaluated set: without it a row not attached to any colonist (preview in the loadout editor)
        // would silently read as "in storage".
        private static bool ClaimedStatus(Pawn pawn, Def d, out FulfillStatus status)
        {
            status = FulfillStatus.Missing;
            LoadoutClaims claims = Claims();
            if (!claims.Knows(pawn, d)) return false;

            bool isWeaponish = d is ThingDef wtd && (wtd.IsWeapon || LoadoutUtility.IsShieldApparel(wtd));
            bool short_ = isWeaponish
                ? claims.weaponMissing.TryGetValue(pawn, out Dictionary<ThingDef, int> wm) && wm.ContainsKey((ThingDef)d)
                : claims.itemMissing.TryGetValue(pawn, out Dictionary<Def, int> im) && im.ContainsKey(d);
            if (!short_) { status = FulfillStatus.Stored; return true; }

            // Really missing - then only the second, separate question remains: is it already being
            // produced? For weapons the same source as before, for items the per-frame cached stock
            // breakdown (its inProduction is WeaponQueuedCount, just once per def instead of once per
            // cell). A generic slot has no single recipe - it stays "missing".
            if (d is ThingDef ptd)
            {
                if (isWeaponish) { if (LoadoutUtility.WeaponHasActiveBill(ptd)) status = FulfillStatus.InProduction; }
                else
                {
                    LoadoutUtility.ComputeItemOwnership(ptd, out _, out _, out _, out _, out int inProd);
                    if (inProd > 0) status = FulfillStatus.InProduction;
                }
            }
            return true;
        }

        // The memo now wraps the WHOLE function. Previously it sat in the middle of the ThingDef branch,
        // i.e. AFTER the inventory query and the turn-based calculation - and the two branches above it
        // (generic defs, ammo) had none at all. Yet the generic ones are the common case: an equipment
        // pack contains "Any Medicine", "Any Perishable Meal", "Any Drugs". Measured, that cost 69 us per
        // call at 3330 calls per second.
        // Safe, because all three branches only READ. The only place that changes anything is the
        // deduction from the remaining pool further down - and that runs exactly once per key, because
        // the second call already returns up here. That was the same with the old, narrower memo.
        internal FulfillStatus SingleFulfillStatus(Pawn pawn, Def d, int needed = 1)
        {
            if (needed < 1) needed = 1;
            var memoKey = (pawn, d, needed);
            if (frameFulfillCache != null && frameFulfillCache.TryGetValue(memoKey, out FulfillStatus memoHit))
                return memoHit;
            FulfillStatus computed = SingleFulfillStatusUncached(pawn, d, needed);
            if (frameFulfillCache != null) frameFulfillCache[memoKey] = computed;
            return computed;
        }

        private FulfillStatus SingleFulfillStatusUncached(Pawn pawn, Def d, int needed)
        {
            if (d is LoadoutGenericDef gd)
            {
                if (LoadoutUtility.PawnCarriedGenericCount(pawn, gd) >= needed) return FulfillStatus.Equipped;
                if (ClaimedStatus(pawn, gd, out FulfillStatus gStatus)) return gStatus;
                // Fallback for rows not (yet) attached to any colonist - e.g. an unsaved preview in the
                // loadout editor. >= needed instead of > 0: "3 in storage" never covered a five-item entry,
                // yet the cell said green.
                if (LoadoutUtility.GenericStockCount(gd) >= needed) return FulfillStatus.Stored;
                return FulfillStatus.Missing;   // no single recipe to be "in production" for
            }
            ThingDef td = d as ThingDef;
            if (td == null) return FulfillStatus.Missing;
            if (LoadoutUtility.PawnCarriedCount(pawn, td) >= needed) return FulfillStatus.Equipped;
            // The turn-based calculation beats every path below wherever it knows this colonist and this
            // item - it is the same one the readiness display and the supply window read from.
            if (ClaimedStatus(pawn, td, out FulfillStatus claimed)) return claimed;
            // frameOwnedCache (BuildOwnedWeaponCounts) is WEAPON-only by design (IsWeapon || shield apparel) -
            // an ammo def (arrow, bullet, ...) never appears in it at all, so checking it here always missed
            // real stock for ammo specifically, falling all the way through to "Missing" even with plenty
            // stockpiled (2026-07-31: "Arrow (stone): not owned, not stored, not in
            // production" despite 100 in a stockpile zone - Dialog_ChangeAmmo's own separate, correct
            // tooltip showed "100 in stock, 20 free" for the exact same item at the exact same time). Ammo
            // routes through the same demand-aware stock check Dialog_ChangeAmmo already uses instead.
            if (LoadoutUtility.IsAmmoDef(td))
            {
                int ammoAvail = LoadoutUtility.ComputeAmmoAvailability(td, out _, out _);
                if (ammoAvail > 0) return FulfillStatus.Stored;
            }
            else
            {
                // Memoized (2026-08-01) + a shared per-frame pool that DECREMENTS as each (pawn, td) pair is
                // first resolved - frameOwnedCache alone is a flat colony-wide total with no notion of
                // "already claimed by an earlier pawn this frame", so every pawn independently checking it
                // saw the SAME raw count and ALL read "Stored" even when stock only covered a few of them
                // (observed: 7 pawns with a satchel-charge sidearm, 2 in stock, every single one showed
                // "in storage"). Memoization matters too, not just the pool - DrawFulfillDot alone calls this
                // twice per item (segment color, then tooltip text), which would double-decrement the SAME
                // pawn's own single claim without it.
                // The requested AMOUNT belongs in the key (found in review). Without it the FIRST cell of
                // the frame decided for all later ones: with medicine x5 in a pack and medicine x1 as a
                // single entry, and 3 free in the colony, the pack cell memoized 'missing' (3 < 5) and the
                // single cell got the same answer back, although 3 >= 1 - a red bar and a 'missing' tooltip
                // next to three free items in storage. In reverse drawing order it would have been a false
                // green.
                // The actual purpose of the memo remains: DrawFulfillDot asks twice per item (segment color,
                // then tooltip) - with the same amount, so still the same key and no second deduction from
                // the frame stock.
                FulfillStatus result = FulfillStatus.Missing;
                // The pool knows ONLY weapons and shields (BuildOwnedWeaponCounts filters on IsWeapon or
                // shield apparel). Everything else - medicine, meals, drugs, components - is never in it,
                // so the lookup always came up empty and the cell drew "missing" even with 200 in storage.
                // Particularly misleading because the tooltip of the SAME cell named the real stock: bar
                // and text contradicted each other.
                // Ammo has long had its own branch above for this; non-weapons never had one.
                // Deliberately the same source as the tooltip (ComputeItemOwnership), so the two cannot
                // drift apart by construction - and deliberately freeStored, not total stock: what other
                // colonists already claim is not free for this one.
                bool poolKnowsIt = frameRemainingCache != null && frameRemainingCache.ContainsKey(td);
                if (!poolKnowsIt && !td.IsWeapon && !LoadoutUtility.IsShieldApparel(td))
                {
                    LoadoutUtility.ComputeItemOwnership(td, out _, out _, out _, out int freeStored, out int inProd);
                    if (freeStored >= needed) result = FulfillStatus.Stored;
                    else if (inProd > 0) result = FulfillStatus.InProduction;
                    return result;
                }
                if (frameRemainingCache != null && frameRemainingCache.TryGetValue(td, out int remaining) && remaining > 0)
                {
                    frameRemainingCache[td] = remaining - 1;
                    result = FulfillStatus.Stored;
                }
                else if (LoadoutUtility.WeaponHasActiveBill(td))
                {
                    result = FulfillStatus.InProduction;
                }
                return result;
            }
            if (LoadoutUtility.WeaponHasActiveBill(td)) return FulfillStatus.InProduction;
            return FulfillStatus.Missing;
        }

        // WorstFulfillStatus was removed without replacement. It came up while reworking DrawFulfillDot: its
        // only caller was the old version of this cell, which folded the whole content into its WORST
        // status. Since the harmonization the cell instead draws one segment per item (a pack with 2
        // equipped parts and 1 missing shows green x2 / red x1), and the call disappeared back then. What
        // remained was a method without callers anywhere in the mod - checked by grep over Source/, no hit
        // outside the compiled artifacts, and it is internal, so not reachable from outside either.

        // Requested quantity per def from a set of loadouts (pack slots, "Other" slots). Summed over all
        // slots that point to the same def - two packs with 3 medicine each request 6, not 3.
        // Passed to DrawFulfillDot as neededFor, so the quantity check in SingleFulfillStatus has a number
        // to compare at all.
        // Returns the DICTIONARY itself instead of a delegate over it
        // (perf): the cell needs the dictionary anyway, and the delegate in front of it was
        // a second heap allocation - per cell, per colonist, per frame. The lookup rule
        // ("unknown means 1") moved along with it and now lives in NeededOf.
        private static Dictionary<Def, int> NeededCountsFrom(IEnumerable<LoadoutSlot> slots)
        {
            Dictionary<Def, int> need = new Dictionary<Def, int>(LoadoutUtility.DefRefComparer.Instance);   // see BuildClaimsUncached
            if (slots != null)
                foreach (LoadoutSlot s in slots)
                {
                    Def key = (Def)s.thingDef ?? s.genericDef;
                    if (key == null) continue;
                    need[key] = (need.TryGetValue(key, out int c) ? c : 0) + Mathf.Max(1, s.count);
                }
            return need;
        }

        private static int NeededOf(Dictionary<Def, int> need, Def d)
            => d != null && need != null && need.TryGetValue(d, out int n) ? n : 1;

        // The colony-wide stock breakdown for an ITEM (pack content, "Other"), as shown by the rich
        // tooltip. This used to exist three times word for word as a lambda at three call sites (pack
        // from role, pack, other) - changing one copy and forgetting the other two was only a matter of
        // time. As a static method the call also costs no heap allocation, whereas each of the three
        // lambdas needed one per cell per frame, even when the tooltip wasn't built at all.
        // neededCount is COLONY-WIDE and not this one colonist's need (with 3 in storage and a need of
        // 10 (2x5) the tooltip correctly reads "3 assigned in
        // storage, 7 missing"): the stock next to it comes from ComputeItemOwnership and is colony-wide -
        // both must refer to the same quantity, otherwise the bar compares apples with oranges and the
        // shortfall comes out too small or vanishes entirely.
        // need: what THIS cell requests. Previously this was ColonyWideAssignedNeed, i.e. the whole
        // colony's demand - the same number in every cell, whether it wants one item or five. The STOCK
        // stays colony-wide (that's the question "where is it"), only the requested amount is now the
        // cell's - identical to WeaponSegFor and AmmoSegFor next to it.
        private static LoadoutUtility.AvailabilitySegments ItemSegFor(Pawn pawn, Def d, int need)
        {
            LoadoutUtility.ComputeItemOwnership(d, out int carriedOnly, out int carriedAndAssigned, out int assignedInStore, out int freeStored, out int inProduction);
            return LoadoutUtility.BuildAvailabilitySegments(carriedOnly, carriedAndAssigned, assignedInStore, freeStored,
                inProduction, pawn, d, neededCount: Mathf.Max(1, need));
        }

        // The same for a WEAPON - a different stock source (GetWeaponInventory, the same one the weapon
        // overview and the main weapon dot use). The requested amount stays with the caller: the role
        // branch counts it from the role's slots, the normal branch from the attached sidearm loadouts -
        // that is the only difference between the two sites and the reason why this is a value and not a
        // flag.
        private static LoadoutUtility.AvailabilitySegments WeaponSegFor(Pawn pawn, Def d, int need)
            => LoadoutUtility.WeaponAvailability((ThingDef)d, pawn, need);

        // Third source: AMMO. Again its own stock function (GetAmmoOwnership), and again a requested
        // amount only the caller knows - here THIS colonist's slot amount for this type (the bar's red
        // segment came from the simple per-item status classification, while this tooltip knew no
        // requested amount at all - so its "Still missing" line could never appear, even though the bar
        // next to it was red. The two contradicted each other instead of one explaining the other).
        private static LoadoutUtility.AvailabilitySegments AmmoSegFor(Pawn pawn, Def d, int need)
        {
            LoadoutUtility.GetAmmoOwnership((ThingDef)d, out int carriedOnly, out int carriedAndAssigned, out int assignedInStore, out int freeStored, out int inProduction);
            return LoadoutUtility.BuildAvailabilitySegments(carriedOnly, carriedAndAssigned, assignedInStore, freeStored,
                inProduction, pawn, (ThingDef)d, neededCount: need);
        }

        // Which stock source a cell's rich tooltip uses. Three domains, three functions - this used to be
        // a lambda passed through per call site, six times, and three of them word for word identical.
        private enum RichStock { None, Item, Weapon, Ammo }

        private static LoadoutUtility.AvailabilitySegments RichSegFor(RichStock kind, Pawn pawn, Def d, Func<Def, int> needFor)
        {
            if (kind == RichStock.Weapon) return WeaponSegFor(pawn, d, needFor != null ? needFor(d) : 1);
            if (kind == RichStock.Ammo) return AmmoSegFor(pawn, d, needFor != null ? needFor(d) : 1);
            // Third branch now like the two above: the amount comes from the caller.
            return ItemSegFor(pawn, d, needFor != null ? needFor(d) : 1);
        }

        // ONE breakdown for bar AND tooltip (the ammo cell's bar was entirely green with 1 of 150 carried,
        // 500 in production, 149 missing). Cause:
        // the bar asked SingleFulfillStatus with a need of 1 (neededFor was null there), the tooltip asked
        // AmmoSegFor with a need of 150 - ONE question, TWO calculations, the most common bug class in this
        // mod. Now the bar reads the same breakdown as the tooltip, memoized in the row caches' 0.3 s
        // block; the tooltip hits the same entry and therefore can no longer contradict the bar.
        private LoadoutUtility.AvailabilitySegments RichSegCached(RichStock kind, Pawn pawn, Def d, int need)
        {
            var key = (pawn, d, need, kind);
            if (frameRichSegCache != null && frameRichSegCache.TryGetValue(key, out LoadoutUtility.AvailabilitySegments hit)) return hit;
            LoadoutUtility.AvailabilitySegments seg = RichSegFor(kind, pawn, d, _ => need);
            if (frameRichSegCache != null) frameRichSegCache[key] = seg;
            return seg;
        }

        // A cell's bar shows PROPORTIONALLY where the items THIS colonist needs are - not the stock of the
        // whole colony (500 in production say nothing about their 150). Order and exclusion as specified:
        // carried (green), then what the turn-based calculation allots them from storage (blue), then
        // production ONLY for the remainder that would otherwise be missing (beige), then missing (red). No
        // item is in two segments - the segments must not overlap. The tooltip next to it still names
        // the full colony numbers; the bar is their translation onto this one need.
        // Carried is COUNTED (PawnCarriedCount), not the 1-item tick from BuildAvailabilitySegments:
        // with 300 requested and 150 carried the bar should be half green, not 1/301.
        // Without a colonist (colony-wide view) there is no need - then the breakdown stays as it is.
        private static LoadoutUtility.AvailabilitySegments CapToNeed(LoadoutUtility.AvailabilitySegments s, Pawn pawn, Def d, int need)
        {
            if (pawn == null) return s;
            need = Mathf.Max(1, need);
            var r = new LoadoutUtility.AvailabilitySegments { pawnContext = true, missingIsPawnSpecific = true, forbidden = s.forbidden };
            int carried = d is ThingDef td
                ? (td.IsWeapon || LoadoutUtility.IsShieldApparel(td) ? CarriedCountOf(pawn, td) : LoadoutUtility.PawnCarriedCount(pawn, td))
                : LoadoutUtility.PawnCarriedGenericCount(pawn, d as LoadoutGenericDef);
            int rem = need;
            r.equippedByPawn = Mathf.Clamp(carried, 0, rem); rem -= r.equippedByPawn;
            // Allocation from the turn-based calculation = carried + their share of the free stock
            // (BuildClaimsUncached); -1 means 'not asked' (no template), then the free stock counts undivided.
            int alloc = ClaimedAllocation(pawn, d);
            int store = alloc >= 0 ? Mathf.Max(0, alloc - carried) : Mathf.Max(0, s.assignedInStore + s.available);
            // The storage share is BLUE - 'allotted to them, in storage, still to be fetched': four
            // colors in the bar, all relative to THIS
            // colonist - green carried, blue to fetch, beige in production, red missing. This is explained
            // not by a yellow split but by a separate block at the top of the tooltip (PawnBlock) that
            // names exactly these four numbers; the colony-wide breakdown sits below it as info.
            r.assignedInStore = Mathf.Clamp(store, 0, rem); rem -= r.assignedInStore;
            r.inProduction = Mathf.Clamp(s.inProduction, 0, rem); rem -= r.inProduction;
            r.missing = rem;
            return r;
        }

        // The top block of a cell tooltip: the bar's four colors with THIS colonist's numbers - heading
        // 'For <name> (needs N):', then green/blue/beige/red, the same hex values as in the bar and the
        // colony-wide breakdown, so color = meaning is the same everywhere.
        // 'Missing' means: neither carried nor in storage for them nor in production.
        private static string PawnBlock(Pawn pawn, LoadoutUtility.AvailabilitySegments part, int need)
        {
            if (pawn == null) return "";
            var sb = new System.Text.StringBuilder();
            sb.Append("<b>").Append("CER_AssignLoadouts_PawnBlockHeader".Translate(pawn.LabelShortCap, Mathf.Max(1, need).ToString()).ToString()).Append("</b>");
            sb.Append("\n  <color=#66D966>").Append("CER_AssignLoadouts_PawnBlockEquipped".Translate(part.equippedByPawn.ToString()).ToString()).Append("</color>");
            sb.Append("\n  <color=#8CB8F2>").Append("CER_AssignLoadouts_PawnBlockToFetch".Translate((part.assignedInStore + part.available).ToString()).ToString()).Append("</color>");
            sb.Append("\n  <color=#C7AD7A>").Append("CER_AssignLoadouts_PawnBlockInProduction".Translate(part.inProduction.ToString()).ToString()).Append("</color>");
            sb.Append("\n  <color=#E65959>").Append("CER_AssignLoadouts_PawnBlockMissing".Translate(part.missing.ToString()).ToString()).Append("</color>");
            return sb.ToString();
        }

        // Cell tooltip for ONE entry: name, colonist block, blank line, colony-wide breakdown (info).
        // `compact`: SHORT FORM from three entries in a cell upward (the weapon explanation was taller than
        // the screen: a CE-native loadout with five entries produced five blocks of 17 lines each, and the
        // box started above the screen edge at 2560x1440). Then the colony-wide breakdown is ONE line per
        // entry, without 'Carried by'/'Assigned to' - the colonist block stays, it is the point of the cell.
        private string CellItemTooltip(RichStock kind, Pawn pawn, Def d, int need, string heading, bool compact = false)
        {
            LoadoutUtility.AvailabilitySegments seg = RichSegCached(kind, pawn, d, need);
            string colonyHead = "<b>" + "CER_AssignLoadouts_ColonyBlockHeader".Translate().ToString() + "</b>";
            string colony = compact
                ? colonyHead + " " + LoadoutUtility.AvailabilityOneLiner(seg)
                : LoadoutUtility.BuildAvailabilityTooltip(seg, LocationTipFor(kind, d), heading: colonyHead);
            if (pawn == null) return heading + "\n" + colony;
            return heading + "\n" + PawnBlock(pawn, CapToNeed(seg, pawn, d, need), need) + (compact ? "\n" : "\n\n") + colony;
        }

        private static void AddSeg(ref LoadoutUtility.AvailabilitySegments sum, LoadoutUtility.AvailabilitySegments part)
        {
            sum.equippedByPawn += part.equippedByPawn;
            sum.available += part.available;
            sum.assignedInStore += part.assignedInStore;
            sum.assignedCarried += part.assignedCarried;
            sum.carriedUnassigned += part.carriedUnassigned;
            sum.inProduction += part.inProduction;
            sum.missing += part.missing;
            sum.forbidden += part.forbidden;
            sum.ground += part.ground;
        }

        // The location breakdown for the same domain - three stock sources, three breakdowns, so no cell
        // shows one from a foreign pool. A generic slot ("Any Perishable Meal") has no location of its
        // own, hence null there instead of an invented line.
        private static string LocationTipFor(RichStock kind, Def d)
        {
            if (kind == RichStock.Weapon) return LoadoutUtility.BuildWeaponLocationTooltip(d as ThingDef);
            if (kind == RichStock.Ammo) return LoadoutUtility.BuildAmmoLocationTooltip(d as ThingDef);
            return d is ThingDef td ? LoadoutUtility.BuildPackItemLocationTooltip(td) : null;
        }

        // The width reserved from a button's OWN hit-rect for the status dot's corner (see DrawFulfillDot/
        // DrawWeaponStatusDot) - reserved UNCONDITIONALLY, whether or not a dot actually gets drawn there
        // this frame (whether one would applies is data-dependent - checking that first just to decide the
        // button's geometry would be pointless extra work for a ~13px sliver). Widgets.ButtonText handles
        // background+highlight+click as one atomic call over whatever rect it's given, with no way to carve
        // a hole out of part of it - so the only way the dot can truly BLOCK the button underneath (no
        // highlight, no click registering there) is for the button's own rect to never extend under it.
        private const float DotReserveW = 13f;

        // Weapon button label: it used to end at xMax-6 and thus ran UNDER the status dot and past the
        // drawn button border (the button itself ends at xMax-DotReserveW) - exactly the truncation '...'
        // that was observed. One function for both views, so the clipping isn't right in only one place
        // again: card and compact row had the same calculation error, typed twice.
        private static Rect LoadoutBtnLabelRect(Rect btnRect, float iconEndX, bool hasIcons) => hasIcons
            // Max(0) and not Max(12): if the clipboard column is in the way AND the colonist carries two
            // weapons, there really is no room left for text - better none than three dots over the status dot.
            ? new Rect(iconEndX + 2f, btnRect.y, Mathf.Max(0f, btnRect.xMax - DotReserveW - 2f - (iconEndX + 2f)), btnRect.height)
            : new Rect(btnRect.x, btnRect.y, btnRect.width - DotReserveW, btnRect.height);

        // NoPasteStripW (24px, subtracted on the left of every cell) was removed without replacement -
        // this note only remains to explain why, so nobody "fixes it back". The strip was the space of
        // the cell's OWN paste button (DrawPasteButton, pasteW 20 + gap 4). Pack and Sidearm had one, Ammo
        // and Other did not - those two reserved the width empty so all four stayed equally wide. Later
        // the button disappeared from the cells and was replaced by ONE shared column on the far right
        // (DrawPasteColumn); Pack/Sidearm lost their deduction then, while Ammo/Other and all empty and
        // role branches kept it. Result: the Pack and Other buttons had different widths - exactly 24px,
        // and only once Pack/Sidearm actually contained something (the empty branch still subtracted).
        // Now only what actually exists is reserved: the availability bar's strip on the right
        // (DotReserveW).

        // A small notification-style badge in a cell's own top-right corner - same spot regardless of the
        // cell's width/height, so it lines up consistently whether the cell is a small icon-only compact
        // button or a wider text button on the card. A thin dark outline keeps it readable against
        // whatever icon or highlight color happens to sit behind it. Silently does nothing if there is no
        // def to report on (e.g. "None" cells) - no badge is its own honest answer there. Returns the dot's
        // own (outlined) rect - empty (zero-size) if nothing was drawn - so the caller can skip registering
        // its own wider cell tooltip while the mouse is over the dot: RimWorld stacks EVERY TipRegion whose
        // rect contains the cursor, so a cell-wide tooltip registered on top of the dot's own small one used
        // to show BOTH at once, bleeding into each other. The dot must win outright in its own corner.
        // richStock: which real stock breakdown the tooltip shows (colony-wide carried/assigned/
        // stored/in production, instead of only the collapsed verdict for this one colonist).
        // When set, the tooltip gets a full block per item instead of a flat line
        // "- item: status" (the same shared bar+tooltip logic the Owned column in the Weapon Loadouts
        // tab already uses - here once per item, because a cell can bundle several).
        // RichStock.None means flat text as before.
        // richNeedFor: the requested amount for Weapon/Ammo that only the caller knows. Item doesn't need
        // it - there the need is colony-wide and lives in ItemSegFor.
        //
        // This used to be ONE parameter `Func<Def, AvailabilitySegments> richSegFor`, and each of the
        // SIX call sites passed in its own lambda. Three of them
        // were word for word identical. Now the caller only says WHICH stock source applies, and the three
        // item lambdas disappear entirely as heap allocations: they were built per cell per colonist per
        // FRAME and as a rule never called, because since the TipWanted guard below the tooltip text is
        // only built under the mouse anyway.
        // itemLabelKey is the KEY, not the finished text:
        // it is only needed in the tooltip branch at the very bottom, but used to be translated right away
        // at each of the six call sites - four cells per colonist per pass, and each translation allocates
        // a string. A key literal allocates nothing.
        // The explanation text of a locked role cell - ONE place for all three (pack, sidearm, other), so
        // they don't drift apart again: the three branches had already diverged twice (first the lock
        // itself was missing in two, then the bar in one).
        //
        // WITHOUT a TipWanted pre-filter, unlike the rich tooltips next to it: the text here is two
        // Translate calls and a concatenation, not a colony-wide stock survey - so the filter would have
        // saved nothing and was an extra condition between "mouse is over it" and "text appears". That
        // was exactly the observed symptom: locked cells showed NO text at all on hover, while the same
        // cell for a colonist without a role had one. TooltipHandler.TipRegion checks Mouse.IsOver itself
        // anyway.
        //
        // The diagnostic logging that was here has been REMOVED after the finding: on retest all three
        // cells showed text, and the log wasn't needed at all.
        //
        // What the cause was remains UNPROVEN, and that belongs here so nobody later reads it as settled:
        // what was built is the removal of the TipWanted pre-filter on Pack and Sidearm. On the third
        // cell (Other), which had also been reported as textless, NOTHING was changed functionally - it
        // was only moved into this call. For that one cell the report was therefore very likely wrong,
        // which also makes it uncertain whether the pre-filter was really the cause for the other two.
        // If you touch this again: the three cells deliberately go through ONE call so they don't drift
        // apart again.
        private static void RoleCellTip(Rect cell, TaggedString text)
        {
            TooltipHandler.TipRegion(cell, text);
        }

        // Reused buffers instead of `Where().Distinct().ToList()` per call.
        // This function runs FOUR TIMES per colonist per pass - with 12 colonists and 143 passes that is
        // about 6900 calls per second, and each one used to allocate four heap objects (Where iterator,
        // Distinct iterator, its internal set, the list), all of which were garbage again within the same
        // frame. 39 MB/s of new allocations were measured in the Assign tab; this is one item of that.
        // The list never leaves the function - it is only iterated and read for the tooltip.
        // Semantics unchanged: a HashSet with the default comparer behaves like Distinct(), and the order
        // of first occurrence is preserved because the list grows in iteration order.
        private static readonly List<Def> fulfillBuf = new List<Def>();
        private static readonly HashSet<Def> fulfillSeen = new HashSet<Def>();
        private static bool fulfillBufInUse;

        private Rect DrawFulfillDot(Rect cell, Pawn pawn, IEnumerable<Def> defs, string itemLabelKey,
            RichStock richStock = RichStock.None, Dictionary<Def, int> neededFor = null, Func<Def, int> richNeedFor = null)
        {
            // The re-entrancy guard is a precaution, not a known case: today nothing below this function
            // calls it again. Whoever does so later gets their own list instead of a silently overwritten
            // buffer - that is the difference between slow and wrong.
            bool ownBuf = !fulfillBufInUse;
            List<Def> list;
            if (ownBuf)
            {
                fulfillBufInUse = true;
                fulfillBuf.Clear();
                fulfillSeen.Clear();
                list = fulfillBuf;
            }
            else list = new List<Def>();
            try
            {
                if (defs != null)
                    foreach (Def d in defs)
                        if (d != null && (!ownBuf || fulfillSeen.Add(d)) && (ownBuf || !list.Contains(d)))
                            list.Add(d);
                return DrawFulfillDotInner(cell, pawn, list, itemLabelKey, richStock, neededFor, richNeedFor);
            }
            finally { if (ownBuf) fulfillBufInUse = false; }
        }

        private Rect DrawFulfillDotInner(Rect cell, Pawn pawn, List<Def> list, string itemLabelKey,
            RichStock richStock, Dictionary<Def, int> neededFor, Func<Def, int> richNeedFor)
        {
            if (list.Count == 0) return default;
            // Harmonized (2026-07-30): one segment per item's own status, tallied into the shared bar model,
            // instead of collapsing the whole cell to its single WORST status - a Pack with 2 equipped slots
            // and 1 missing now actually shows that split (green x2, red x1) instead of one flat red dot.
            var seg = new LoadoutUtility.AvailabilitySegments { missingIsPawnSpecific = pawn != null, pawnContext = pawn != null };
            foreach (Def d in list)
            {
                // Rich cells (all four today): the bar computes with the SAME breakdown as the tooltip
                // below and with the need the caller names - capped proportionally to this colonist.
                // The branch below remains for RichStock.None.
                if (richStock != RichStock.None)
                {
                    int need = richNeedFor != null ? richNeedFor(d) : NeededOf(neededFor, d);
                    AddSeg(ref seg, CapToNeed(RichSegCached(richStock, pawn, d, need), pawn, d, need));
                    continue;
                }
                switch (SingleFulfillStatus(pawn, d, NeededOf(neededFor, d)))
                {
                    case FulfillStatus.Equipped: seg.equippedByPawn++; break;
                    // ASSIGNED AND IN STORAGE, not "freely available" (the display was yellow instead of
                    // blue - "Assigned and in storage" is blue, and the vertical stock display uses the
                    // central color codes).
                    // The bug was in the mapping, not in the colors: this cell's list
                    // IS what this colonist needs - an item of it in storage is therefore by definition
                    // "assigned to them and in storage" (blue) and never "freely available" (yellow, with a
                    // colonist in view). The rich mouseover next to it said it correctly all along
                    // ("Assigned, in storage: 3"), only the bar didn't - the same question, two
                    // answers.
                    // Without a colonist reference the old pool stays: then there is no one it would be
                    // assigned to.
                    case FulfillStatus.Stored:
                        if (pawn != null) seg.assignedInStore++; else seg.available++;
                        break;
                    case FulfillStatus.InProduction: seg.inProduction++; break;
                    default: seg.missing++; break;
                }
            }
            // A full-height strip on the cell's right edge, not a small corner square - same width as
            // before, just as tall as the button itself now (a strip as wide as before, but as tall as the
            // button). Flush with the cell's own top/bottom (no
            // vertical poke at all) - a wider tooltip belonging to something drawn directly ABOVE/BELOW this
            // cell (e.g. the Bulk bar's own tooltip in card view) could otherwise overlap and stack both
            // tooltips at once; RimWorld draws every TipRegion whose rect contains the cursor, so overlap
            // always means both show - the fix has to be "don't let the rects overlap", not a draw-order trick.
            Rect outline = new Rect(cell.xMax - 10f, cell.y, 10f, cell.height);
            LoadoutUtility.DrawAvailabilityBar(outline, seg, horizontal: false, drawTooltip: false);
            // FROM HERE ON ONLY UNDER THE MOUSE. The bar above has to
            // be drawn every frame, the TEXT below does not - and it is the expensive part: per item a full
            // colony-wide breakdown (richSegFor -> ComputeItemOwnership, ColonyWideAssignedNeed,
            // BuildAvailabilityTooltip), assembled into a multi-line string, then discarded by RimWorld
            // because the mouse is elsewhere. This cell exists four times per colonist. See
            // LoadoutUtility.TipWanted for why the discarding is guaranteed.
            if (!LoadoutUtility.TipWanted(outline)) return outline;
            string itemLabel = itemLabelKey.Translate().ToString();
            string tip;
            if (richStock != RichStock.None)
            {
                // extraTip = the "where is it / who carries it / who is it assigned to" breakdown
                // (an 'Assigned to' list appeared in NEITHER of the two tooltips - neither for the role
                // nor for the directly attached pack). That was a
                // real gap: EVERY other
                // availability bar in the mod passes this breakdown on to BuildAvailabilityTooltip, but
                // not the four cells of the Assign tab - even though "who actually has the medicine" is
                // the most obvious question there.
                // No TipWanted guard of its own: this whole block already sits behind one (above), so
                // it only runs under the mouse.
                // The need of THIS cell, not of the whole colony (the pack cell and the Other cell of the
                // same colonist were WORD FOR WORD identical for
                // the same item, and with 3 medicine available the 1-item cell reported
                // the same shortage as the 5-item cell).
                // The weapon and ammo cells have long done it right: colony-wide STOCK, but the
                // requested AMOUNT from the caller - exactly the mix that makes "you are still missing X"
                // answerable at all. Only the item cell took ColonyWideAssignedNeed, and so its shortfall
                // was the same in every cell.
                // Falls back to the cell's quantity table (neededFor) - the same one the bar color takes
                // its amount from. Bar and text finally talk about the same number.
                Func<Def, int> cellNeedFor = richNeedFor ?? (dd => NeededOf(neededFor, dd));
                // The per-item heading is passed in as a parameter - this only worked before because
                // string.Join calls ToString() and leaves the tags intact. That is the same construction
                // through which the weapon cell lost its colors, just accidentally on the safe side.
                // Luck is not a safeguard.
                // Per entry: colonist block on top (the bar colors with THEIR numbers), below it the
                // colony-wide breakdown as info.
                tip = itemLabel + ":\n\n" + string.Join("\n\n", list.Select(d =>
                    CellItemTooltip(richStock, pawn, d, cellNeedFor(d), "<b>" + d.LabelCap.ToString() + "</b>", compact: list.Count > 2)));
            }
            else
            {
                // Per-item breakdown, not one aggregate word - "missing" alone doesn't say WHICH of several
                // items (a pack can hold several slots, a pawn can carry several ammo types) is the actual
                // gap. Same "- item: status" convention as Dialog_SquadSetup's own Supply-badge tooltip.
                // The same quantity check as the segments above - otherwise the tooltip claims
                // "equipped" while the bar next to it is red, and that is exactly the kind of
                // contradiction this cell has produced before.
                // .ToString() on every LabelCap (same trap as in the weapon tooltip, found by grepping
                // for the same pattern): without it the whole one-liner becomes a TaggedString, and its
                // conversion back into `string tip` strips the color FulfillWordColored just set -
                // so the status word appeared here in black and white. The multi-line branches only got
                // away with it because string.Join goes through ToString().
                tip = list.Count == 1
                    ? itemLabel + ": " + list[0].LabelCap.ToString() + " - " + LoadoutUtility.FulfillWordColored(SingleFulfillStatus(pawn, list[0], NeededOf(neededFor, list[0]))) + "."
                    : itemLabel + ":\n" + string.Join("\n", list.Select(d => "- " + d.LabelCap.ToString() + ": " + LoadoutUtility.FulfillWordColored(SingleFulfillStatus(pawn, d, NeededOf(neededFor, d)))));
            }
            // Fixed id per cell (see LoadoutUtility.DrawFetchGearButton): the text names stock and
            // production, which change constantly during play - without a fixed id the box closes on every change.
            string tipText = tip;
            TooltipHandler.TipRegion(outline, new TipSignal(() => tipText, TipId(pawn, list != null && list.Count > 0 ? list[0] : null, 0x11)));
            return outline;
        }

        // Same outline formula DrawWeaponStatusDot draws at, exposed separately so a caller can check
        // Mouse.IsOver(WeaponDotRect(...)) to guard its OWN wider button tooltip BEFORE the dot itself is
        // drawn later in the same row (draw order matters visually - the dot must paint on top of the
        // button - but the "does the dot win" mouse check does not need to wait for that). Full-height
        // strip on the button's own right edge, flush top/bottom - EXACTLY DrawFulfillDot's own geometry
        // (same width, same flush placement), fixed 2026-07-30: this used to still poke 1px above AND
        // below the button (a leftover from before the "make it flush" pass reached this one), which both
        // gave it a visibly longer bottom edge than DrawFulfillDot's dots AND put its left edge 1px further
        // from the button than theirs - the "gap looks different for Weapons vs Ammo/Pack/Sidearm" report.
        private static Rect WeaponDotRect(Rect btnRect) => new Rect(btnRect.xMax - 10f, btnRect.y, 11f, btnRect.height);

        // Weapon-button status dot - same corner-dot-plus-"dot wins" convention as DrawFulfillDot, but for the
        // single main weapon on the loadout button itself rather than a whole cell of several items. Carries
        // richer detail in its tooltip (what's actually equipped right now, not just the plan) and keeps the
        // old line's "click while queued to jump to the bench's Bills tab" behavior, since replacing a whole
        // detail row with one small dot must not quietly drop something players relied on.
        private void DrawWeaponStatusDot(Rect btnRect, Pawn pawn, ThingDef mainW)
        {
            if (mainW == null) return;
            FulfillStatus status = SingleFulfillStatus(pawn, mainW);   // for the workbench click below
            // ALL entries this cell represents - for our templates exactly the main weapon, for a CE-native
            // loadout also gladius, meals, medicine. Proportional like the neighbouring cells (CapToNeed),
            // from the same breakdown as the tooltip.
            List<WeaponCellItem> items = GetRowInfo(pawn).weaponCellItems;
            var seg = new LoadoutUtility.AvailabilitySegments { missingIsPawnSpecific = pawn != null, pawnContext = pawn != null };
            if (items != null && items.Count > 0)
                for (int i = 0; i < items.Count; i++)
                    AddSeg(ref seg, CapToNeed(RichSegCached(items[i].kind, pawn, items[i].def, items[i].need), pawn, items[i].def, items[i].need));
            else
                AddSeg(ref seg, CapToNeed(RichSegCached(RichStock.Weapon, pawn, mainW, 1), pawn, mainW, 1));
            Rect outline = WeaponDotRect(btnRect);
            LoadoutUtility.DrawAvailabilityBar(outline, seg, horizontal: false, drawTooltip: false);

            // The CLICK stays unthrottled - it has to be offered every frame, otherwise it gets lost.
            // Only the tooltip TEXT below moves behind the mouse check (see LoadoutUtility.TipWanted):
            // it pulls a full stock breakdown and used to run per colonist per frame.
            // Mouse.IsOver first: FindActiveBillBench is the
            // only one of the bill queries that is UNCACHED and walks every workbench and every bill stack.
            // The click below can only trigger when the pointer is inside anyway - so the workbench search
            // only needs to run then too.
            if (!Mouse.IsOver(outline)) return;
            Building_WorkTable queuedBench = status == FulfillStatus.InProduction ? LoadoutUtility.FindActiveBillBench(mainW) : null;
            if (queuedBench != null && Widgets.ButtonInvisible(outline)) LoadoutUtility.OpenBillsTab(queuedBench);
            if (!LoadoutUtility.TipWanted(outline)) return;

            ThingWithComps primary = pawn.equipment != null ? pawn.equipment.Primary : null;
            string equippedName = primary != null ? primary.LabelCap.ToString() : "CER_AssignLoadouts_NothingEquipped".Translate().ToString();
            // Rich colony-wide breakdown (2026-08-01, same treatment the Pack/Ammo/Sidearm cells already got)
            // instead of a flat "status word" line - the bar's own color still comes from `seg`/SingleFulfill
            // Status above (the fast, per-frame-claim-decrementing check correct for THIS pending plan), only
            // the TOOLTIP TEXT is upgraded to the full Total/Equipped/Available/... block.
            // Fourth occurrence of the same body (found by grepping from the SYMPTOM - the search was for
            // BuildAvailabilityTooltip, not for the function that had just been changed). need = 0
            // is NOT arbitrary here but exactly what the call did before: it omitted neededCount, whose
            // default is 0 - so the red "missing" segment stays off. With 1 this tooltip would suddenly
            // have claimed a shortfall for a weapon without stock that the other cells there don't show.
            // Now uses need 1 instead of 0 and comes from the cache - the same number as in the bar above.
            var richSeg = RichSegCached(RichStock.Weapon, pawn, mainW, 1);
            // Heading and closing line are passed in as PARAMETERS instead of being built around it (the
            // tooltip was missing its coloring). The old
            // expression turned the whole chain into a TaggedString, and its conversion back into `string tip`
            // stripped every <color> the breakdown had just inserted - the weapon tooltip was therefore the
            // only one that was plain white. Full reasoning above BuildAvailabilityTooltip.
            string trailer = "CER_AssignLoadouts_CurrentlyEquippedFmt".Translate(equippedName).ToString();
            if (queuedBench != null) trailer += "CER_AssignLoadouts_QueuedOnBenchFmt".Translate(queuedBench.LabelCap).ToString();
            if (items == null || items.Count <= 1)
            {
                string oneTip = CellItemTooltip(RichStock.Weapon, pawn, mainW, 1, "<b>" + mainW.LabelCap.ToString() + "</b>") + "\n\n" + trailer;
                TooltipHandler.TipRegion(outline, new TipSignal(() => oneTip, TipId(pawn, mainW, 0x12)));
                return;
            }
            // One block per entry, as the pack cell does for its contents (the tooltip lists all entries).
            // The main weapon comes first, the closing line stays.
            string tip = string.Join("\n\n", items.Select(it =>
                CellItemTooltip(it.kind, pawn, it.def, it.need, "<b>" + it.def.LabelCap.ToString() + (it.need > 1 ? " x" + it.need : "") + "</b>", compact: items.Count > 2)));
            string weaponTip = tip + "\n\n" + trailer;
            TooltipHandler.TipRegion(outline, new TipSignal(() => weaponTip, TipId(pawn, mainW, 0x13)));
        }

        // This takes the FULL cell, not the button rect - VisualCell() subtracts DotReserveW itself.
        // (Briefly built the other way round and immediately reverted: passing the already narrowed
        // button rect would have doubled the deduction.)
        private static void DrawCellIcon(Rect cell, ThingDef icon, int overflowCount)
        {
            Rect vc = VisualCell(cell);
            float size = Mathf.Min(20f, vc.height - 4f);
            Rect iconRect = new Rect(vc.x + (vc.width - size) / 2f - (overflowCount > 0 ? 8f : 0f), vc.y + (vc.height - size) / 2f, size, size);
            GUI.color = Color.white;
            if (icon != null) Widgets.ThingIcon(iconRect, icon);
            if (overflowCount > 0)
            {
                // Bottom-right, not top - DrawFulfillDot's status badge already claims the cell's top-right
                // corner, and the two used to land on top of each other on a small compact icon cell.
                GUI.color = new Color(1f, 1f, 1f, 0.75f);
                Text.Font = GameFont.Tiny;
                Text.Anchor = TextAnchor.LowerLeft;
                // The badge is shifted to the LEFT if it would otherwise run out - NOT clipped in width
                // (the badge showed only '2' instead of '+2' - the plus was cut off). That was exactly the
                // bug of the first cap: it limited the WIDTH to vc.xMax, and as soon as the remainder was
                // narrower than the text, Unity cut it off. The text must always be complete; it may be
                // moved, not clipped.
                string badge = "CER_AssignLoadouts_OverflowCountFmt".Translate(overflowCount.ToString());
                float badgeW = Text.CalcSize(badge).x + 1f;
                float badgeX = Mathf.Min(iconRect.xMax - 2f, vc.xMax - badgeW);
                Widgets.Label(new Rect(Mathf.Max(vc.x, badgeX), vc.y, badgeW, vc.height - 1f), badge);
                Text.Anchor = TextAnchor.UpperLeft;
                Text.Font = GameFont.Small;
            }
            GUI.color = Color.white;
        }

        // First real item in a Loadout's own contents - a pack's first supply item, or a sidearm loadout's
        // main weapon (always added before its ammo, so it's reliably the first slot). One shared stand-in
        // icon for "what is this loadout, at a glance" wherever a whole Loadout needs to shrink to one glyph.
        internal static ThingDef RepresentativeIcon(Loadout lo)
        {
            if (lo?.OwnSlots == null) return null;
            foreach (LoadoutSlot s in lo.OwnSlots)
            {
                ThingDef td = s.thingDef ?? (s.genericDef != null ? LoadoutUtility.RepresentativeIconDef(s.genericDef) : null);
                if (td != null) return td;
            }
            return null;
        }

        // ---- Equipment Pack cell (card + compact) -----------------------------------------------------------

        // View/swap/detach the Equipment Pack attached to this pawn's current loadout. The loadout
        // button's own tooltip (LoadoutContentsTooltip) already lists a pack's contents transparently
        // once attached, since it just reads .Slots (own+parent union) - this cell is only about WHICH
        // pack, and changing it.
        private void DrawEquipmentPackCell(Rect cell, Pawn pawn, Loadout currentLoadout, bool iconOnly = false, Rect? extraHit = null)
        {
            DrawEquipmentPackCellInner(cell, pawn, currentLoadout, iconOnly, extraHit);
        }

        private void DrawEquipmentPackCellInner(Rect cell, Pawn pawn, Loadout currentLoadout, bool iconOnly, Rect? extraHit)
        {
            // Packs are pure supply, not a weapon - unlike the Ammo/Sidearm cells (which need a real Weapon
            // Loadout to hang off of), a pacifist with no weapon plan at all can still carry one (e.g. to act
            // as a dedicated doctor). Mechanically this needs no anchor changes: for a pawn with no weapon
            // loadout, `currentLoadout` is already CE's own real "None" marker object (never null), a perfectly
            // valid template to attach a pack's hidden per-pawn anchor onto - same as attaching one to a real
            // weapon template. Only a pawn who can't carry ANYTHING (a baby) is blocked outright.
            Text.Font = GameFont.Tiny;
            if (LoadoutUtility.CannotCarryAnything(pawn))
            {
                GUI.color = new Color(1f, 1f, 1f, 0.35f);
                if (iconOnly)
                {
                    // Empty state on the SAME geometry as a filled cell - which is now the full cell, see
                    // the comment at DotReserveW.
                    DrawCellDash(cell);
                    Text.Font = GameFont.Small;
                    return;
                }
                Widgets.Label(cell, "CER_AssignLoadouts_PackDashLabel".Translate());
                GUI.color = Color.white;
                Text.Font = GameFont.Small;
                return;
            }
            // A Role-assigned pawn has no per-pawn pack of their own to show/edit here - everything they carry
            // is already baked into the Role's own flat Slots union (2026-07-31 fix). Read-only preview of the
            // Role's actual content instead of a misleading "None" that used to show even though the Role
            // plainly provides a pack - the read-only treatment built for Dialog_SquadSetup's pre-apply
            // preview never reached this LIVE, already-applied tab (card AND compact view share this one
            // method). No paste button, no click-to-open-picker - editing means editing the Role itself
            // (Roles tab), not this one colonist.
            if (RoleUtility.IsRoleLoadout(currentLoadout))
            {
                Dialog_SquadSetup.ExtractRoleDisplayContent(currentLoadout, out _, out List<Def> roleContent);
                // Full cell, exactly like every other path (there used to be a 24px strip here so the role
                // branch drew as wide as the normal one; no path subtracts it any more, see the comment at
                // DotReserveW). A paste button doesn't belong here anyway - a role is edited in the Roles
                // tab, not per colonist.
                Rect roleCell = cell;
                Rect roleBtnRect = new Rect(roleCell.x, roleCell.y, roleCell.width - DotReserveW, roleCell.height);
                // VISIBLY disabled instead of merely ineffective: a part that can't be changed because of a
                // role gets a greyed-out button - nothing custom, the way vanilla would do it.
                // That is exactly what GUI.enabled = false does: RimWorld's own button draws itself grey and
                // accepts no click - no reimplementation, no custom color (never
                // strike through for "disabled"). Only the button surface, not the content: icon,
                // number and availability bar below are information and stay readable.
                // Own grey tone instead of GUI.enabled (measured side by side: locked and normal cells both
                // rendered 106,81,46, zero difference). GUI.enabled
                // blocks the click - it never drew anything, because RimWorld's button graphic goes through
                // GUI.DrawTexture. See LoadoutUtility.TintedButton.
                LoadoutUtility.TintedButton(roleBtnRect, null, disabled: true);
                if (roleContent.Count == 0) DrawCellDash(roleCell);
                else
                {
                    ThingDef repIcon = roleContent[0] as ThingDef ?? LoadoutUtility.RepresentativeIconDef(roleContent[0] as LoadoutGenericDef);
                    DrawCellIcon(roleCell, repIcon, roleContent.Count > 1 ? roleContent.Count : 0);
                }
                Text.Font = GameFont.Small;
                // The availability bar was missing entirely in THIS branch: a colonist with a directly
                // attached pack got its colored rectangle, a colonist with the same pack via a ROLE got
                // none at all - precisely where something is most likely missing. The branch returns before
                // the call further down and never had its own.
                // CarriedPackSlots, the same source as the normal path: it knows pack contents FROM A
                // ROLE (GetPawnPacks only knows directly attached pack objects, and none come via a role -
                // that is exactly why this function was created). So both branches answer the question
                // from the same pool instead of having two calculations.
                // From GetRowInfo (frame cache) - the same source as the normal branch below.
                List<LoadoutSlot> rolePackSlots = GetRowInfo(pawn).packSlots;
                Rect rolePackDot = DrawFulfillDot(roleCell, pawn, rolePackSlots.Select(s => (Def)s.thingDef ?? s.genericDef),
                    "CER_AssignLoadouts_LabelPack",
                    RichStock.Item, neededFor: GetRowInfo(pawn).packNeeded);
                if (!Mouse.IsOver(rolePackDot)) RoleCellTip(roleCell, (roleContent.Count > 0
                    ? "CER_AssignLoadouts_RoleIncludedInFmt".Translate(RoleUtility.DisplayLabel(currentLoadout), string.Join(", ", roleContent.Select(d => d.LabelCap.ToString())))
                    : "CER_AssignLoadouts_RoleNoPackItems".Translate())
                    + "CER_AssignLoadouts_RoleBakedSuffix".Translate());
                return;
            }

            // No paste strip of its own in this cell any more: there is exactly ONE column for that, on the
            // far right after "Other" - see DrawPasteColumn. The cell thereby gets back the width it had
            // permanently reserved here, even though the clipboard only ever holds one object.
            // Per-pawn, via the pawn's own hidden personal anchor (AmmoAssignmentUtility) - never the
            // shared template, so two colonists on the same weapon loadout can carry different packs. A
            // pawn can carry several packs at once, so this opens a checklist rather than a single-pick FloatMenu.
            List<Loadout> attached = GetRowInfo(pawn).packs;
            // As a local function instead of a variable: in the
            // compact view (iconOnly) ONLY the tooltip needs it, and that is now only built
            // under the mouse. Previously the concatenation ran per cell per row per frame for
            // nothing. Called directly, a local function costs no heap allocation.
            string Label() => attached.Count > 0 ? string.Join(", ", attached.Select(a => EquipmentPackUtility.StripMarker(a.label))) : "CER_AssignLoadouts_PackNoneLabel".Translate().ToString();
            Rect btnRect = new Rect(cell.x, cell.y, cell.width - DotReserveW, cell.height);
            bool clicked;
            if (iconOnly)
            {
                clicked = Widgets.ButtonText(btnRect, "") | (extraHit.HasValue && Widgets.ButtonInvisible(extraHit.Value));
                if (attached.Count == 0) DrawCellDash(cell);
                else DrawCellIcon(cell, RepresentativeIcon(attached[0]), attached.Count > 1 ? attached.Count : 0);
            }
            else
            {
                clicked = Widgets.ButtonText(btnRect, "CER_AssignLoadouts_PackPrefixFmt".Translate(Label().Truncate(btnRect.width - 40f))) | (extraHit.HasValue && Widgets.ButtonInvisible(extraHit.Value));
            }
            // ItemSegFor returns the real colony-wide breakdown for a pack item (concrete or
            // generic) - the same source the pack editor uses, instead of a second
            // stock pass here. BuildAvailabilitySegments also accepts a generic def there
            // ("Any Perishable Meal" always showed "Equipped: 0" even though the
            // colonist carried a matching meal).
            // Contents from CarriedPackSlots - see its comment: `attached` only knows directly
            // attached pack OBJECTS, and what comes via a role isn't one. That is exactly why the
            // tooltip was missing precisely the contents that came from the role.
            List<LoadoutSlot> packSlots = GetRowInfo(pawn).packSlots;
            Rect packDot = DrawFulfillDot(cell, pawn, packSlots.Select(s => (Def)s.thingDef ?? s.genericDef), "CER_AssignLoadouts_LabelPack",
                RichStock.Item, neededFor: GetRowInfo(pawn).packNeeded);
            Text.Font = GameFont.Small;
            if (!Mouse.IsOver(packDot) && LoadoutUtility.TipWanted(cell))
                TooltipHandler.TipRegion(cell, (attached.Count > 0
                    ? "CER_AssignLoadouts_PackAttachedFmt".Translate(Label())
                    : "CER_AssignLoadouts_PackNoneAttached".Translate()) + "CER_AssignLoadouts_PackClickChooseFmt".Translate(pawn.LabelShortCap));
            if (clicked)
                Find.WindowStack.Add(new Dialog_PickPacks("CER_AssignLoadouts_PickPacksTitleFmt".Translate(pawn.LabelShortCap),
                    () => AmmoAssignmentUtility.GetPawnPacks(pawn),
                    updated => AmmoAssignmentUtility.SetPawnPacks(pawn, currentLoadout, updated),
                    sourceLabel: pawn.LabelShortCap, checkProductionOnClose: true));
        }

        // A pack/sidearm selection copied via Dialog_PickPacks' own Copy button (LoadoutClipboard) can be
        // pasted straight onto another colonist from here - no need to reopen the checklist and re-tick the
        // same boxes per pawn. Only appears once something of the matching kind has actually been copied.
        // Draws in a dedicated strip carved off the LEFT edge of `cell` and returns what's left for the
        // caller's own button/icon - never overlapping the cell's own clickable area (a shared hit-region
        // with the cell behind it made this trivial to misclick before). A no-op returning `cell` unchanged
        // when nothing of the matching kind has been copied yet, so callers can unconditionally reassign
        // their own `cell` to this call's result with zero cost on the common path.
        // ONE paste column for the whole cell row (on the right after "Other", disappearing entirely when
        // empty). Previously each cell had its own
        // paste strip and reserved its width permanently - four strips for a clipboard that
        // only ever holds ONE object. The space was lost in four places, and
        // only one was ever used.
        // The column itself knows what is currently on the clipboard and writes it to the right place -
        // that is why the mapping kind -> write target lives here and no longer in the cells.
        // PasteColW is 0 when nothing matching is on the clipboard: the column then disappears entirely
        // and the four cells get the space.
        internal const float PasteColW = 26f;

        // The cannotFight guard from DrawSidearmCell, moved here so both views use it.
        // Pasting bypasses the cell's click lock - without this check a pacifist would get a sidearm
        // as soon as Copy was pressed anywhere.
        internal static void PasteSidearmsGuarded(Pawn pawn, Loadout currentLoadout, List<Loadout> updated)
        {
            if (LoadoutUtility.CannotFight(pawn))
            {
                Messages.Message("CER_AssignLoadouts_CannotFightMsg".Translate(pawn.LabelShortCap), MessageTypeDefOf.RejectInput, false);
                return;
            }
            SidearmAssignmentUtility.SetPawnSidearms(pawn, currentLoadout, updated);
        }

        internal static bool ClipboardHasAny() => LoadoutClipboard.HasContent("pack")
            || LoadoutClipboard.HasContent("sidearm") || LoadoutClipboard.HasContent("ammo")
            || LoadoutClipboard.HasContent("other") || LoadoutClipboard.HasContent("mainweapon");

        // Take over a colonist's other items ("Add other item" had no Copy button and therefore no
        // paste either). Same shape as PasteAmmo:
        // onto the personal anchor, first clear its own Other slots (everything that is neither weapon nor
        // ammo - exactly the set DrawOtherItemsCellInner shows), then set the copied ones.
        // An empty clipboard only clears, as with packs. Without a template no anchor can be set up - then
        // false, and the caller reports nothing (like PasteAmmo). CE's "None" loadout suffices as a
        // template, just as for packs and sidearms.
        internal static bool PasteOtherItems(Pawn pawn, Loadout currentLoadout, List<LoadoutSlot> slots)
        {
            if (pawn == null) return false;
            Loadout template = AmmoAssignmentUtility.DisplayLoadout(currentLoadout) ?? currentLoadout ?? pawn.GetLoadout();
            if (template == null) return false;
            Loadout link = AmmoAssignmentUtility.EnsureAmmoLink(pawn, template);
            if (link == null) return false;
            foreach (LoadoutSlot own in link.OwnSlots.Where(s => s.thingDef != null && !s.thingDef.IsWeapon && !LoadoutUtility.IsAmmoDef(s.thingDef)).ToList())
                link.RemoveSlot(own);
            foreach (LoadoutSlot s in slots ?? new List<LoadoutSlot>())
                if (s.thingDef != null) link.AddSlot(new LoadoutSlot(s.thingDef, s.count));
            CombatReadiness.Invalidate();
            return true;
        }

        // Take over a colonist's ammo. Writes to the same personal anchor
        // the ammo window uses: first clear its own ammo slots, then set the
        // copied ones. SetAmmoCleared as there - if no ammo at all remains after pasting, the anchor must
        // explicitly count as "cleared", otherwise the template default silently comes back (see
        // Dialog_ChangeAmmo, the same case when unticking the last type).
        internal static bool PasteAmmo(Pawn pawn, Loadout currentLoadout, List<LoadoutSlot> slots)
        {
            if (pawn == null) return false;
            // pawn.GetLoadout() as a third fallback as in PasteOtherItems: without it, a colonist with a
            // null currentLoadout was the one path on which the click said NOTHING AT ALL - no write,
            // no message (return false means "already reported" to the caller, which was never true here).
            Loadout template = AmmoAssignmentUtility.DisplayLoadout(currentLoadout) ?? currentLoadout ?? pawn.GetLoadout();
            if (template == null) return false;
            Loadout link = AmmoAssignmentUtility.EnsureAmmoLink(pawn, template);
            if (link == null) return false;

            // An empty clipboard clears the target - the contract has always been stated at
            // LoadoutClipboard.CopyAmmo ("pasting clears the target"), but was never built: an empty copy
            // ran into the "nothing fits" message or silently did nothing, and the paste column promised a
            // paste that structurally could not exist (seen on a role colonist).
            List<LoadoutSlot> srcAll = slots ?? new List<LoadoutSlot>();
            if (srcAll.Count == 0)
            {
                foreach (LoadoutSlot own in link.OwnSlots.Where(s => LoadoutUtility.IsAmmoDef(s.thingDef) || s.genericDef != null).ToList())
                    link.RemoveSlot(own);
                AmmoAssignmentUtility.SetAmmoCleared(link, true);
                LoadoutUtility.InvalidateAmmoOwnership();
                return true;
            }

            // Only take over what fits a weapon of THIS colonist (with a clear warning message for the
            // rest).
            // Previously everything landed unchanged on their anchor - and because an own ammo list
            // displaces EVERY inherited one (Patch_Loadout_MultiParentSlots), a colonist with a different
            // weapon was left without any matching ammo: their own types gone, the pasted ones belonging to
            // none of their weapons. Observed exactly like that (.45 Colt pasted onto a KS-23 carrier).
            // If nothing remains, NOTHING is written - otherwise they would be worse off after pasting than
            // before, which is exactly the case the warning variant is meant to prevent.
            var fitting = new HashSet<Def>(LoadoutUtility.DefRefComparer.Instance);   // see BuildClaimsUncached
            if (link.Slots != null)
                foreach (LoadoutSlot ws in link.Slots)
                    if (ws.thingDef != null && ws.thingDef.IsWeapon && !LoadoutUtility.IsSelfAmmoWeapon(ws.thingDef))
                        foreach (Def a in LoadoutUtility.GetLoadoutAmmoDefs(ws.thingDef, false)) fitting.Add(a);

            List<LoadoutSlot> src = slots ?? new List<LoadoutSlot>();
            List<LoadoutSlot> usable = src.Where(s => fitting.Contains((Def)s.thingDef ?? s.genericDef)).ToList();
            int dropped = src.Count - usable.Count;
            if (usable.Count == 0)
            {
                Messages.Message("CER_AssignLoadouts_PasteAmmoNoneFitsFmt".Translate(pawn.LabelShortCap), MessageTypeDefOf.RejectInput, false);
                return false;
            }
            if (dropped > 0)
                Messages.Message("CER_AssignLoadouts_PasteAmmoPartialFmt".Translate(
                    usable.Count.ToString(), dropped.ToString(), pawn.LabelShortCap), MessageTypeDefOf.CautionInput, false);
            slots = usable;
            foreach (LoadoutSlot own in link.OwnSlots.Where(s => LoadoutUtility.IsAmmoDef(s.thingDef) || s.genericDef != null).ToList())
                link.RemoveSlot(own);
            foreach (LoadoutSlot s in slots ?? new List<LoadoutSlot>())
                link.AddSlot(s.thingDef != null ? new LoadoutSlot(s.thingDef, s.count) : new LoadoutSlot(s.genericDef, s.count));
            // IsAmmoSlot: generic entries ("Any ammo for X") get pasted too. With the old concrete-only
            // test a purely generic paste counted as "no ammo at all" and marked the colonist as
            // deliberately empty - they later ended up with nothing.
            AmmoAssignmentUtility.SetAmmoCleared(link, !link.OwnSlots.Any(LoadoutUtility.IsAmmoSlot));
            LoadoutUtility.InvalidateAmmoOwnership();
            // In the PARTIAL case PasteAmmo has already reported itself what was taken over and what was
            // left out - the caller's blanket success message would then be a second, less precise
            // statement about the same click. So false here doesn't mean "failed" but "already reported".
            return dropped == 0;
        }

        // Draws the column and, on click, writes the content to where it belongs.
        internal static void DrawPasteColumn(Rect r, Pawn pawn, Loadout currentLoadout,
            Action<List<Loadout>> applyPacks, Action<List<Loadout>> applySidearms, Action<Loadout> applyMain = null)
        {
            bool isPack = LoadoutClipboard.HasContent("pack");
            bool isSide = LoadoutClipboard.HasContent("sidearm");
            bool isAmmo = LoadoutClipboard.HasContent("ammo");
            // Other as the fourth kind ("Add other item" had no Copy button and therefore no paste
            // either) - slot copies as with ammo, see
            // LoadoutClipboard.CopyOther and PasteOtherItems.
            bool isOther = LoadoutClipboard.HasContent("other");
            // Main weapon assignment as the fifth kind (the weapon picker has a Copy button too, and
            // pasting must honor it) - copied in the picker window, pasted here.
            bool isMain = applyMain != null && LoadoutClipboard.HasContent("mainweapon");
            if (!isPack && !isSide && !isAmmo && !isOther && !isMain) return;
            string label = (isPack ? "CER_AssignLoadouts_LabelPack"
                : isSide ? "CER_AssignLoadouts_LabelSidearmsPlural"
                : isOther ? "CER_AssignLoadouts_LabelOther"
                : isMain ? "CER_AssignLoadouts_LabelMainWeapon" : "CER_AssignLoadouts_LabelAmmo").Translate();
            // A visible strip, not just a highlight under the mouse: a column you only see when the mouse
            // happens to rest on it is invisible to anyone LOOKING for it. It stays subtle nonetheless -
            // the same restraint as with the icon.
            GUI.color = new Color(1f, 1f, 1f, 0.07f);
            GUI.DrawTexture(r, BaseContent.WhiteTex);
            GUI.color = Color.white;
            if (Mouse.IsOver(r)) Widgets.DrawHighlight(r);
            // THE ICON that was lost when the columns were merged (the Copy tooltip pointed to a paste
            // column that could not be found).
            // No wonder: apart from a highlight
            // under the mouse this column drew nothing. It was an invisible click area, and an explanation
            // you only find by accidentally hovering over it helps nobody.
            // The replaced per-cell version (DrawPasteButton, directly below, has had no callers since the
            // merge) drew the icon all along - this one line just didn't move over in the
            // rework. Same opacity as there, so the
            // column stays subtle: the paste icon should be subtle and white, not colorful.
            GUI.color = new Color(1f, 1f, 1f, 0.65f);
            LoadoutUtility.DrawPasteIcon(r.ContractedBy(2f));
            GUI.color = Color.white;

            if (Widgets.ButtonInvisible(r))
            {
                // If this line is missing while the MouseUp line above is present, the problem is the click
                // itself and not one of the locks below.
                // The completion message only AFTER the precondition: previously it ran
                // unconditionally, even when the target could not accept the sidearm at all -
                // "Pasted..." for something that never happened. The check sits here and not only in the
                // two apply actions, because only here is it known whether the MESSAGE should still come.
                // THREE paths, ONE feedback rule (ammo always reported something, even wrongly; packs and
                // sidearms reported nothing at all - after the click the player couldn't tell whether
                // anything had happened).
                // Two separate bugs were involved. First, the blanket success message used to run
                // UNCONDITIONALLY, even when PasteAmmo had just declined - then "Nothing pasted"
                // and "Pasted" showed at the same time. Second, it didn't exist for packs and sidearms at
                // all, because their apply actions are silent.
                // Now the writer reports back whether it really wrote (PasteAmmo), and the
                // success message comes exactly then - the same for all three kinds.
                // Role colonists accept nothing: their pack, sidearm
                // and Other cells are locked ("edit the Role itself"), but the paste column still wrote
                // to the anchor - invisibly, and with no way back except an empty clipboard. Ammo
                // stays allowed, the ammo cell is editable for roles.
                if ((isPack || isSide || isOther) && RoleUtility.IsRoleLoadout(currentLoadout))
                {
                    Messages.Message("CER_AssignLoadouts_PasteRoleRejectMsg".Translate(pawn.LabelShortCap), MessageTypeDefOf.RejectInput, false);
                }
                else if ((isSide || isMain) && LoadoutUtility.CannotFight(pawn))
                {
                    Messages.Message("CER_AssignLoadouts_CannotFightMsg".Translate(pawn.LabelShortCap), MessageTypeDefOf.RejectInput, false);
                }
                // The same limit for PACKS as the pack cell itself (found in review): a baby's cell shows
                // only a dash, but the paste column used to write anyway and report "Pasted". Afterwards the
                // pack hung on a colonist who could no longer get rid of it via their own cell - there was
                // no control path left. The preview twin in Dialog_SquadSetup has long checked this; here it
                // was only done for sidearms. As a message and not as a vanished button, for the same reason
                // as one line above: a click without any feedback looks like a bug.
                else if ((isPack || isOther) && LoadoutUtility.CannotCarryAnything(pawn))
                {
                    Messages.Message("CER_AssignLoadouts_CannotCarryMsg".Translate(pawn.LabelShortCap), MessageTypeDefOf.RejectInput, false);
                }
                else
                {
                    bool announce = true;
                    if (isPack) applyPacks(LoadoutClipboard.Items);
                    else if (isSide) applySidearms(LoadoutClipboard.Items);
                    else if (isMain)
                    {
                        // Only paste a template that still exists - the clipboard may hold one that has since
                        // been deleted (e.g. after 'Delete own loadouts').
                        Loadout mlo = (LoadoutClipboard.Items ?? new List<Loadout>())
                            .FirstOrDefault(l => l != null && LoadoutManager.Loadouts.Contains(l));
                        if (mlo != null) applyMain(mlo);
                        else announce = false;
                    }
                    else if (isOther) announce = PasteOtherItems(pawn, currentLoadout, LoadoutClipboard.OtherSlots);
                    else announce = PasteAmmo(pawn, currentLoadout, LoadoutClipboard.AmmoSlots);
                    if (announce)
                        Messages.Message("CER_AssignLoadouts_PastedMsgFmt".Translate(label, LoadoutClipboard.SourceLabel, pawn.LabelShortCap), MessageTypeDefOf.TaskCompletion, false);
                }
            }
            if (LoadoutUtility.TipWanted(r)) TooltipHandler.TipRegion(r, "CER_AssignLoadouts_PasteTipFmt".Translate(label, LoadoutClipboard.SourceLabel, pawn.LabelShortCap));
        }

        // kind = fixed identifier ("pack"/"sidearm"), label = what appears in the message and tooltip.
        // Kept separate because both used to be the same translated string, and the button only appeared
        // when two different Keyed keys happened to have the same value (see LoadoutClipboard.HasContent).
        //
        // NO CALLERS since the merge into the shared column (DrawPasteColumn, directly above).
        // It was not kept on purpose - it is a leftover, and at one point it was even
        // harmful: it alone held the paste icon, while the column that replaced it
        // drew nothing at all. Whoever changes something here is probably changing the wrong place - the
        // one in use is DrawPasteColumn.
        internal static Rect DrawPasteButton(Rect cell, string kind, string label, Pawn pawn, Action<List<Loadout>> apply)
        {
            const float pasteW = 20f, gap = 4f;
            // Reserve this strip's width UNCONDITIONALLY, even when there's nothing in the clipboard for
            // `kind` right now (2026-08-01: "half the icons aren't centered") - the old early
            // return handed callers the FULL cell whenever the clipboard was empty, so DrawCellIcon/
            // DrawCellDash/DrawCellWarning centered themselves differently depending on clipboard state:
            // narrower (paste strip reserved) the moment a player copies something for that kind, full width
            // before that. Comparing rows/cells where clipboard state happened to differ made icons look
            // randomly misaligned relative to each other. Always returning the same narrowed remainder keeps
            // every icon-only cell's own centering stable regardless of clipboard state - only the paste
            // button ITSELF stays conditional.
            Rect remainder = new Rect(cell.x + pasteW + gap, cell.y, cell.width - pasteW - gap, cell.height);
            if (!LoadoutClipboard.HasContent(kind)) return remainder;
            Rect pasteRect = new Rect(cell.x, cell.y, pasteW, cell.height);
            if (Mouse.IsOver(pasteRect)) Widgets.DrawHighlight(pasteRect);
            // White, not colored - the paste icon should be subtle and white, not colorful.
            // Dimmed slightly (not full-opacity white) to stay subtle next to the
            // cell's own content instead of drawing the eye first.
            GUI.color = new Color(1f, 1f, 1f, 0.65f);
            LoadoutUtility.DrawPasteIcon(pasteRect.ContractedBy(2f));
            GUI.color = Color.white;
            if (Widgets.ButtonInvisible(pasteRect))
            {
                apply(LoadoutClipboard.Items);
                Messages.Message("CER_AssignLoadouts_PastedMsgFmt".Translate(label, LoadoutClipboard.SourceLabel, pawn.LabelShortCap), MessageTypeDefOf.TaskCompletion, false);
            }
            if (LoadoutUtility.TipWanted(pasteRect)) TooltipHandler.TipRegion(pasteRect, "CER_AssignLoadouts_PasteTipFmt".Translate(label, LoadoutClipboard.SourceLabel, pawn.LabelShortCap));
            return remainder;
        }

        // Mirrors DrawEquipmentPackCell exactly, one column over - this dialog had a Pack cell but no
        // Sidearm cell, meaning a pawn's sidearm could only ever be set from Dialog_SquadSetup's batch
        // preview, never from this direct per-pawn view. Same immediate-apply contract as the Pack cell:
        // no pending pin here, the checklist mutates the pawn's actual assignment right away.
        private void DrawSidearmCell(Rect cell, Pawn pawn, Loadout currentLoadout, bool iconOnly = false, Rect? extraHit = null)
        {
            DrawSidearmCellInner(cell, pawn, currentLoadout, iconOnly, extraHit);
        }

        private void DrawSidearmCellInner(Rect cell, Pawn pawn, Loadout currentLoadout, bool iconOnly, Rect? extraHit)
        {
            // No main weapon needed: a colonist with "Weapon: None" had a pasted halberd that increased
            // their bulk, but the cell didn't show it. Allowing sidearms without a main weapon was fine
            // as long as there was no technical limit.
            // There is none: the sidearm hangs on the colonist's personal anchor like a pack
            // (SetPawnSidearms -> EnsureAmmoLink), and that can also be spanned over CE's "None" loadout -
            // exactly what pasting proved. Only this cell was gated on "has a
            // loadout" and was thus the loophole: pasting wrote, the cell stayed silent, and only a
            // later-assigned weapon made the long-attached sidearm visible.
            // Now the same limit as the pack cell: only a colonist who can't carry anything gets the dash.
            // The null case remains (a pawn without inventory has no loadout object at all).
            Text.Font = GameFont.Tiny;
            if (currentLoadout == null || LoadoutUtility.CannotCarryAnything(pawn))
            {
                GUI.color = new Color(1f, 1f, 1f, 0.35f);
                if (iconOnly)
                {
                    // Empty state on the SAME geometry as a filled cell - which is now the full cell, see
                    // the comment at DotReserveW.
                    DrawCellDash(cell);
                    Text.Font = GameFont.Small;
                    return;
                }
                Widgets.Label(cell, "CER_AssignLoadouts_SidearmDashLabel".Translate());
                GUI.color = Color.white;
                Text.Font = GameFont.Small;
                return;
            }
            // Non-combatants (pacifists, babies - the same CannotFight rule the Weapon cell/drag-swap path use)
            // can't be handed a NEW sidearm either - this cell used to have no such gate at all, letting a
            // CannotFight pawn with an existing loadout still open the full sidearm picker and get a weapon
            // attached despite being unable to use it.
            bool cannotFight = LoadoutUtility.CannotFight(pawn);
            // Same Role read-only treatment as the Pack cell above - see its own comment (2026-07-31 fix).
            if (RoleUtility.IsRoleLoadout(currentLoadout))
            {
                Dialog_SquadSetup.ExtractRoleDisplayContent(currentLoadout, out List<ThingDef> roleContent, out _);
                // Full cell, exactly like every other path (there used to be a 24px strip here so the role
                // branch drew as wide as the normal one; no path subtracts it any more, see the comment at
                // DotReserveW). A paste button doesn't belong here anyway - a role is edited in the Roles
                // tab, not per colonist.
                Rect roleCell = cell;
                Rect roleBtnRect = new Rect(roleCell.x, roleCell.y, roleCell.width - DotReserveW, roleCell.height);
                // VISIBLY disabled, as in the pack cell: buttons unusable because of a role are greyed
                // out, the way vanilla does it. At first this was implemented in only
                // ONE of THREE places.
                // There are three role branches in this file (pack, sidearm, other); only the
                // first got GUI.enabled = false, the other two kept drawing a perfectly
                // normal button - and verification had covered exactly the one cell that was already fixed.
                // Own grey tone instead of GUI.enabled (measured side by side: locked and normal cells both
                // rendered 106,81,46, zero difference). GUI.enabled
                // blocks the click - it never drew anything, because RimWorld's button graphic goes through
                // GUI.DrawTexture. See LoadoutUtility.TintedButton.
                LoadoutUtility.TintedButton(roleBtnRect, null, disabled: true);
                if (roleContent.Count == 0) DrawCellDash(roleCell);
                else DrawCellIcon(roleCell, roleContent[0], roleContent.Count > 1 ? roleContent.Count : 0);
                Text.Font = GameFont.Small;
                // Second occurrence of the same hole (found by searching from the SYMPTOM - only the pack
                // cell had been noticed): the sidearm cell's role branch didn't draw any
                // availability bar either. The same stock source as the normal path below
                // (GetWeaponInventory), the requested amount from the role's own slots.
                Rect roleSideDot = DrawFulfillDot(roleCell, pawn, roleContent.Cast<Def>(),
                    "CER_AssignLoadouts_LabelSidearm",
                    RichStock.Weapon, richNeedFor: d => currentLoadout?.Slots?.Where(s => s.thingDef == (ThingDef)d).Sum(s => Mathf.Max(1, s.count)) ?? 1);
                if (!Mouse.IsOver(roleSideDot)) RoleCellTip(roleCell, (roleContent.Count > 0
                    ? "CER_AssignLoadouts_RoleIncludedInFmt".Translate(RoleUtility.DisplayLabel(currentLoadout), string.Join(", ", roleContent.Select(d => d.LabelCap.ToString())))
                    : "CER_AssignLoadouts_RoleNoSidearmItems".Translate())
                    + "CER_AssignLoadouts_RoleBakedSuffix".Translate());
                return;
            }

            // No paste strip of its own in this cell any more - there is a shared column on the far right,
            // see DrawPasteColumn. The cannotFight guard moved there along with it and was NOT
            // dropped: it hung on exactly this callback and was needed because pasting bypasses the click
            // lock further down - otherwise a pacifist got a sidearm as soon as Copy had been pressed
            // anywhere.
            List<Loadout> attached = GetRowInfo(pawn).sidearms;
            // As a local function instead of a variable: in the
            // compact view (iconOnly) ONLY the tooltip needs it, and that is now only built
            // under the mouse. Previously the concatenation ran per cell per row per frame for
            // nothing. Called directly, a local function costs no heap allocation.
            // CellLabel instead of the raw label (the mouseover said
            // "Sidearms attached: W| satchel charge", while the same sidearm in RimWorld's loadout column
            // and in the card's dropdown had long been shown without prefix). A forgotten occurrence of the
            // same fix - the pack cell two screens above already cleans up its labels.
            string Label() => attached.Count > 0 ? string.Join(", ", attached.Select(a => LoadoutUtility.CellLabel(a))) : "CER_AssignLoadouts_SidearmNoneLabel".Translate().ToString();
            Rect btnRect = new Rect(cell.x, cell.y, cell.width - DotReserveW, cell.height);
            bool clicked;
            if (iconOnly)
            {
                clicked = Widgets.ButtonText(btnRect, "") | (extraHit.HasValue && Widgets.ButtonInvisible(extraHit.Value));
                if (attached.Count == 0) DrawCellDash(cell);
                else DrawCellIcon(cell, RepresentativeIcon(attached[0]), attached.Count > 1 ? attached.Count : 0);
            }
            else
            {
                clicked = Widgets.ButtonText(btnRect, "CER_AssignLoadouts_SidearmPrefixFmt".Translate(Label().Truncate(btnRect.width - 52f))) | (extraHit.HasValue && Widgets.ButtonInvisible(extraHit.Value));
            }
            // RepresentativeIcon resolves each attached sidearm Loadout to its own main weapon ThingDef - the
            // weapon domain (GetWeaponInventory), not the pack-item or ammo-slot domains, so the rich tooltip
            // uses the SAME empire-wide-stock function the main weapon dot below (and the Weapon Loadouts
            // tab's own Owned column) already calls.
            // neededCount = how many attached sidearm loadouts resolve to exactly this weapon, each with
            // its own slot amount (the same global correction as in the ammo cell above).
            Rect sidearmDot = DrawFulfillDot(cell, pawn, GetRowInfo(pawn).sidearmIcons, "CER_AssignLoadouts_LabelSidearm",
                RichStock.Weapon, richNeedFor: d => attached.Where(a => RepresentativeIcon(a) == (ThingDef)d)
                    .Sum(a => a.Slots?.FirstOrDefault(s => s.thingDef == (ThingDef)d)?.count ?? 1));
            Text.Font = GameFont.Small;
            if (!Mouse.IsOver(sidearmDot))
            {
                if (cannotFight) TooltipHandler.TipRegion(cell, "CER_AssignLoadouts_CannotFightTip".Translate());
                else if (LoadoutUtility.TipWanted(cell)) TooltipHandler.TipRegion(cell, (attached.Count > 0
                    ? "CER_AssignLoadouts_SidearmAttachedFmt".Translate(Label())
                    : "CER_AssignLoadouts_SidearmNoneAttached".Translate()) + "CER_AssignLoadouts_SidearmClickChooseFmt".Translate(pawn.LabelShortCap));
            }
            if (clicked && cannotFight)
            {
                Messages.Message("CER_AssignLoadouts_CannotFightMsg".Translate(pawn.LabelShortCap), MessageTypeDefOf.RejectInput, false);
            }
            else if (clicked)
            {
                Loadout localCurrent = currentLoadout;
                Find.WindowStack.Add(new Dialog_PickPacks("CER_AssignLoadouts_PickSidearmsTitleFmt".Translate(pawn.LabelShortCap),
                    () => SidearmAssignmentUtility.GetPawnSidearms(pawn),
                    updated => SidearmAssignmentUtility.SetPawnSidearms(pawn, localCurrent, updated),
                    windowTitle: "CER_AssignLoadouts_WeaponLoadoutsWindowTitle".Translate(), itemColumnLabel: "CER_AssignLoadouts_LabelSidearmsPlural".Translate(), clipboardKind: "sidearm",
                    allOptions: () => SidearmAssignmentUtility.CandidateLoadouts(localCurrent),
                    emptyMessage: "CER_AssignLoadouts_NoOtherLoadoutsMsg".Translate(),
                    displayLabel: lo => LoadoutUtility.CellLabel(lo) ?? "CER_AssignLoadouts_QuestionMarkFallback".Translate().ToString(),
                    groupLabel: SidearmAssignmentUtility.CategoryLabel,
                    scoreFn: SidearmAssignmentUtility.ScoreOf,
                    showSidearmBulkCap: true,
                    availDefFn: RepresentativeIcon, sourceLabel: pawn.LabelShortCap, checkProductionOnClose: true,
                    // Lazy rework: weapons without a template as their own section - ticking one creates
                    // the shared template in the background and attaches it like any other.
                    weaponOptions: () => LoadoutUtility.WeaponsWithoutTemplate(),
                    materializeWeapon: Dialog_GenerateLoadouts.EnsureDefaultLoadout));
            }
        }

        // ---- "Other" items cell (card + compact) ------------------------------------------------------------

        // Mirrors DrawEquipmentPackCell's own structure (dash gate, Role read-only preview, icon-only cell,
        // fulfill dot, click-to-open-picker) one column over again - the fourth structured cell alongside
        // Ammo/Pack/Sidearm, added 2026-08-01 so a single pawn can carry one-off items (a tool, jewelry, a
        // resource stack, ...) that don't fit any of those three without reopening the barebones per-pawn
        // editor. No paste button here (unlike Pack/Sidearm) - LoadoutClipboard only ever stores Loadout
        // lists (packs/sidearms), and a raw ThingDef+count pair has nothing equivalent to copy/paste yet.
        private void DrawOtherItemsCell(Rect cell, Pawn pawn, Loadout currentLoadout, bool iconOnly = false, Rect? extraHit = null)
        {
            DrawOtherItemsCellInner(cell, pawn, currentLoadout, iconOnly, extraHit);
        }

        private void DrawOtherItemsCellInner(Rect cell, Pawn pawn, Loadout currentLoadout, bool iconOnly, Rect? extraHit)
        {
            Text.Font = GameFont.Tiny;
            if (LoadoutUtility.CannotCarryAnything(pawn))
            {
                GUI.color = new Color(1f, 1f, 1f, 0.35f);
                if (iconOnly)
                {
                    // Empty state on the SAME geometry as a filled cell - which is now the full cell, see
                    // the comment at DotReserveW.
                    DrawCellDash(cell);
                    Text.Font = GameFont.Small;
                    return;
                }
                Widgets.Label(cell, "CER_AssignLoadouts_OtherDashLabel".Translate());
                GUI.color = Color.white;
                Text.Font = GameFont.Small;
                return;
            }
            // Same Role read-only treatment as the Pack/Sidearm cells (2026-08-01) - editing means editing
            // the Role itself (Roles tab), not this one colonist. No content preview here (unlike Pack/
            // Sidearm's ExtractRoleDisplayContent) - a Role has no distinct "other items" bucket separate
            // from its own flat Slots union, so a plain dash + explanatory tooltip is the honest answer.
            if (RoleUtility.IsRoleLoadout(currentLoadout))
            {
                // Full cell, exactly like every other path (there used to be a 24px strip here so the role
                // branch drew as wide as the normal one; no path subtracts it any more, see the comment at
                // DotReserveW). A paste button doesn't belong here anyway - a role is edited in the Roles
                // tab, not per colonist.
                Rect roleCell = cell;
                Rect roleBtnRect = new Rect(roleCell.x, roleCell.y, roleCell.width - DotReserveW, roleCell.height);
                // VISIBLY disabled, as in the pack cell - third and last occurrence: buttons unusable
                // because of a role are greyed out, the way vanilla does it. At first this was implemented
                // in only ONE of THREE places.
                // There are three role branches in this file (pack, sidearm, other); only the
                // first got GUI.enabled = false, the other two kept drawing a perfectly
                // normal button - and verification had covered exactly the one cell that was already fixed.
                // Own grey tone instead of GUI.enabled (measured side by side: locked and normal cells both
                // rendered 106,81,46, zero difference). GUI.enabled
                // blocks the click - it never drew anything, because RimWorld's button graphic goes through
                // GUI.DrawTexture. See LoadoutUtility.TintedButton.
                LoadoutUtility.TintedButton(roleBtnRect, null, disabled: true);
                DrawCellDash(roleCell);
                Text.Font = GameFont.Small;
                RoleCellTip(roleCell, "CER_AssignLoadouts_OtherRoleNotice".Translate());
                return;
            }

            // The pawn's own personal "other" items - own slots on the anchor that are neither a weapon
            // (Sidearm's domain) nor an ammo def (Ammo's domain). Never inherited from the template/pack/
            // sidearm chain (those don't carry arbitrary items), so OwnSlots alone is the full picture here.
            Loadout anchor = AmmoAssignmentUtility.GetAmmoLeaf(pawn);
            List<LoadoutSlot> otherSlots = anchor?.OwnSlots
                ?.Where(s => s.thingDef != null && !s.thingDef.IsWeapon && !LoadoutUtility.IsAmmoDef(s.thingDef))
                .ToList() ?? new List<LoadoutSlot>();
            // As a local function instead of a variable: in the
            // compact view (iconOnly) ONLY the tooltip needs it, and that is now only built
            // under the mouse. Previously the concatenation ran per cell per row per frame for
            // nothing. Called directly, a local function costs no heap allocation.
            string Label() => otherSlots.Count > 0
                ? string.Join(", ", otherSlots.Select(s => s.thingDef.LabelCap.ToString()))
                : "CER_AssignLoadouts_OtherNoneLabel".Translate().ToString();
            Rect btnRect = new Rect(cell.x, cell.y, cell.width - DotReserveW, cell.height);
            bool clicked;
            if (iconOnly)
            {
                clicked = Widgets.ButtonText(btnRect, "") | (extraHit.HasValue && Widgets.ButtonInvisible(extraHit.Value));
                if (otherSlots.Count == 0) DrawCellDash(cell);
                else DrawCellIcon(cell, otherSlots[0].thingDef, otherSlots.Count > 1 ? otherSlots.Count : 0);
            }
            else
            {
                clicked = Widgets.ButtonText(btnRect, "CER_AssignLoadouts_OtherPrefixFmt".Translate(Label().Truncate(btnRect.width - 40f))) | (extraHit.HasValue && Widgets.ButtonInvisible(extraHit.Value));
            }
            // ComputeItemOwnership, same generic-item stock check the Pack cell's own tooltip already uses -
            // correct for an arbitrary carryable ThingDef, not just weapons/ammo.
            // ItemSegFor, the same stock source as the pack cell - correct for any
            // carryable ThingDef, not just weapons/ammo. The colony-wide need is included there,
            // for the same reason as in the pack cell: this cell carried exactly the same mix
            // of colony-wide stock and single-pawn need (found by search).
            Rect otherDot = DrawFulfillDot(cell, pawn, otherSlots.Select(s => s.thingDef), "CER_AssignLoadouts_LabelOther",
                RichStock.Item, neededFor: NeededCountsFrom(otherSlots));
            Text.Font = GameFont.Small;
            if (!Mouse.IsOver(otherDot) && LoadoutUtility.TipWanted(cell))
                TooltipHandler.TipRegion(cell, (otherSlots.Count > 0
                    ? "CER_AssignLoadouts_OtherAttachedFmt".Translate(Label())
                    : "CER_AssignLoadouts_OtherNoneAttached".Translate()) + "CER_AssignLoadouts_OtherClickChooseFmt".Translate(pawn.LabelShortCap));
            if (clicked) Find.WindowStack.Add(new Dialog_PickOtherItem(pawn, currentLoadout));
        }

        // ---- Drag-and-drop loadout swap (loadout-button handle) -----------------------------------------------

        // The loadout button starts a drag; a press without movement is left to the button's normal click
        // (OpenLoadoutPickMenu). Uses raw Unity input polling (Input.GetMouseButtonDown/Up), NOT IMGUI's
        // Event.current.type - mirrors Dialog_SquadSetup's swap drag, where the EventType approach did not
        // reliably fire for this kind of press-drag-release. DrawDragChipAndReset force-clears dragPawn whenever
        // the button isn't held at all, so a missed "up" frame can never leave the drag stuck.
        private void HandleDragHandle(Rect handle, Pawn pawn)
        {
            if (dragPawn == null && Mouse.IsOver(handle) && Input.GetMouseButtonDown(0))
                dragPawn = pawn;
        }

        private void HandleDropTarget(Rect area, Pawn pawn)
        {
            // dragPawn itself is never CannotFight (HandleDragHandle only arms for a draggable, non-cannotFight
            // source) - only the hovered TARGET needs the check, so the "will accept" outline agrees with what
            // SwapLoadouts actually allows on drop instead of promising a swap that silently no-ops after.
            if (dragPawn == null || dragPawn == pawn || !Mouse.IsOver(area) || LoadoutUtility.CannotFight(pawn)) return;
            Widgets.DrawBox(area, 2);
            if (Input.GetMouseButtonUp(0))
            {
                SwapLoadouts(dragPawn, pawn);
                dragPawn = null;
            }
        }

        // Swap two colonists' actual assigned loadouts, right now - unlike Dialog_SquadSetup's preview-stage swap
        // (which pins a plan that Apply later commits), there is no deferred plan here: SetLoadout takes effect
        // immediately, same as picking one from OpenLoadoutPickMenu would.
        private void SwapLoadouts(Pawn a, Pawn b)
        {
            if (LoadoutUtility.CannotFight(a) || LoadoutUtility.CannotFight(b))
            {
                Messages.Message("CER_AssignLoadouts_SwapCannotFightMsg".Translate(), MessageTypeDefOf.RejectInput, false);
                return;
            }
            // ====== Swap the TEMPLATES, not the anchors ======
            //
            // Observed: "Swapped loadouts: <A> and <B>." - WITHIN the window it held, but after closing
            // the planner the ORIGINAL state was back. Cross-check: a single assignment via the cell does
            // survive closing - so it was specific to dragging.
            //
            // Cause: pawn.GetLoadout() returns a personalized colonist's ANCHOR, not the
            // shared template (see AmmoAssignmentUtility) - and after an Arm-my-Colony run
            // practically everyone has one. So the anchors were swapped: A was assigned B's anchor and
            // vice versa. The anchor guard in Patch_Utility_Loadouts_SetLoadout explicitly steps
            // ASIDE for that (IsAmmoLink -> "this is a real direct assignment"), so the raw
            // assignment stood. Two colonists then hung on foreign anchors - a state the next
            // cleanup straightens out again. That is exactly why the swap looked right in the open window
            // and was gone after closing.
            //
            // Correct is what the single path does: read the TEMPLATE (DisplayLoadout looks through the
            // anchor) and set it via AssignTemplate - that re-hangs each colonist's OWN anchor onto the new
            // template and takes packs, sidearms and personal ammo along.
            Loadout la = AmmoAssignmentUtility.DisplayLoadout(a.GetLoadout());
            Loadout lb = AmmoAssignmentUtility.DisplayLoadout(b.GetLoadout());
            bool aHas = la != null && !la.defaultLoadout, bHas = lb != null && !lb.defaultLoadout;
            if (!aHas && !bHas)
            {
                Messages.Message("CER_AssignLoadouts_SwapNeitherHasLoadout".Translate(), MessageTypeDefOf.RejectInput, false);
                return;
            }
            if (aHas && bHas && la == lb)
            {
                Messages.Message("CER_AssignLoadouts_SwapSameLoadout".Translate(), MessageTypeDefOf.RejectInput, false);
                return;
            }
            // Extra parents (Packs) live keyed by the anchor Loadout's OWN UniqueID, not the pawn, so
            // swapping which pawn is assigned to which anchor object carries them along automatically.
            // NEVER null (confirmed in the CE decompile): CE's
            // GetLoadout only heals a MISSING key; a key with value null stays null
            // forever - and CE's own loadout column does pawn.GetLoadout().label unguarded.
            // Failure scenario: A has a template, B doesn't; drag A onto B - afterwards CE's native
            // loadouts table throws a NullReferenceException in A's row every frame.
            // Everywhere else in the mod the default loadout ("no assignment") stands for this, see
            // ArmUnassignAll; this was the only place with null.
            Loadout noneLo = LoadoutManager.Loadouts.FirstOrDefault(l => l.defaultLoadout);
            // Via AssignTemplate instead of SetLoadout where there really is a template - see above. The
            // path to "no template" stays SetLoadout(none): AssignTemplate rejects null/default, and
            // for detaching the raw assignment is the right one (the same guarantee as ArmUnassignAll).
            if (bHas) AmmoAssignmentUtility.AssignTemplate(a, lb); else a.SetLoadout(noneLo);
            if (aHas) AmmoAssignmentUtility.AssignTemplate(b, la); else b.SetLoadout(noneLo);
            LoadoutUtility.InvalidateAmmoOwnership();
            CombatReadiness.Invalidate();
            Messages.Message("CER_AssignLoadouts_SwappedMsgFmt".Translate(a.LabelShortCap, b.LabelShortCap), MessageTypeDefOf.TaskCompletion, false);
        }

        // Floating chip that follows the cursor while dragging + the end-of-frame reset when released over empty
        // space. Called inside the assign scroll view so its coordinates match the cards'/rows'.
        private void DrawDragChipAndReset()
        {
            if (dragPawn != null)
            {
                Loadout lo = AmmoAssignmentUtility.DisplayLoadout(dragPawn.GetLoadout());
                Vector2 mp = Event.current.mousePosition;
                Rect chip = new Rect(mp.x + 12f, mp.y + 6f, 220f, 26f);
                Widgets.DrawBoxSolid(chip, new Color(0.15f, 0.15f, 0.17f, 0.95f));
                string lab = dragPawn.LabelShortCap + "  ->  " + (lo != null && !lo.defaultLoadout ? LoadoutUtility.LoadoutDisplayName(lo) : "CER_AssignLoadouts_NoneLoadout".Translate().ToString());
                GUI.color = new Color(0.85f, 0.9f, 1f);
                Widgets.Label(new Rect(chip.x + 6f, chip.y + 3f, chip.width - 12f, 22f), lab.Truncate(208f));
                GUI.color = Color.white;
            }
            if (dragPawn != null && !Input.GetMouseButton(0)) dragPawn = null;
        }

        // Combat scores are driven by Sight, Manipulation and Moving. This tooltip shows each capacity's level and,
        // when it is below full, exactly what is dragging it down (injuries, missing parts, pain, genes) so the
        // player can tell at a glance whether a colonist is fit for ranged or melee duty.
        private static string BuildCapacityTooltip(Pawn pawn)
        {
            System.Text.StringBuilder sb = new System.Text.StringBuilder();
            sb.Append("CER_AssignLoadouts_CombatCapabilitiesHeader".Translate());
            sb.Append("CER_AssignLoadouts_CapacitiesSubheader".Translate());
            sb.Append("\n" + CapacityLine(pawn, PawnCapacityDefOf.Sight, "CER_AssignLoadouts_RoleAimingShooting".Translate()));
            sb.Append("\n" + CapacityLine(pawn, PawnCapacityDefOf.Manipulation, "CER_AssignLoadouts_RoleAimMeleeHit".Translate()));
            sb.Append("\n" + CapacityLine(pawn, PawnCapacityDefOf.Moving, "CER_AssignLoadouts_RoleClosingMelee".Translate()));
            return sb.ToString();
        }

        private static string CapacityLine(Pawn pawn, PawnCapacityDef capacity, string role)
        {
            List<PawnCapacityUtility.CapacityImpactor> impactors = new List<PawnCapacityUtility.CapacityImpactor>();
            float level;
            try { level = PawnCapacityUtility.CalculateCapacityLevel(pawn.health.hediffSet, capacity, impactors); }
            catch { level = pawn.health.capacities.GetLevel(capacity); impactors = null; }

            int pct = Mathf.RoundToInt(level * 100f);
            string name = capacity.GetLabelFor(pawn).CapitalizeFirst();
            string pctStr = pct >= 100 ? pct + "%"
                          : (pct >= 50 ? "<color=#FFC44D>" : "<color=#FF6B6B>") + pct + "%</color>";

            System.Text.StringBuilder sb = new System.Text.StringBuilder();
            sb.Append("<b>" + name + ":</b> " + pctStr + "  <i>(" + role + ")</i>");

            if (level < 0.999f && impactors != null)
            {
                HashSet<string> seen = new HashSet<string>();
                foreach (var imp in impactors)
                {
                    string r = null;
                    try { r = imp.Readable(pawn); } catch { }
                    if (string.IsNullOrEmpty(r) || !seen.Add(r)) continue;
                    sb.Append("\n   • " + r);
                }
            }
            return sb.ToString();
        }

        private string GetEquipmentTooltip(Pawn pawn)
        {
            System.Text.StringBuilder sb = new System.Text.StringBuilder();
            sb.AppendLine("CER_AssignLoadouts_CurrentlyEquippedHeader".Translate());

            bool hasEquip = false;
            if (pawn.equipment != null && pawn.equipment.Primary != null)
            {
                sb.AppendLine("• " + pawn.equipment.Primary.LabelCap);
                hasEquip = true;
            }

            // Clothing/apparel is intentionally omitted here - only weapons and inventory are relevant to loadouts.

            if (pawn.inventory != null && pawn.inventory.innerContainer.Count > 0)
            {
                sb.AppendLine("CER_AssignLoadouts_InventoryHeader".Translate());
                foreach(var inv in pawn.inventory.innerContainer)
                {
                    sb.AppendLine("• " + inv.LabelCap);
                    hasEquip = true;
                }
            }

            if (!hasEquip) return "CER_AssignLoadouts_NothingEquippedMsg".Translate();
            return sb.ToString().TrimEnd();
        }

        private void DrawScoreBar(Rect rect, float score, float maxScore, string label)
        {
            LoadoutUtility.ScoreBar(rect, score, maxScore, label);
        }

        // With prebuilt labels (GetRowLabels) - saves one translation with arguments per pass.
        private void DrawScoreBarPrebuilt(Rect rect, float score, float maxScore, string prebuilt)
        {
            LoadoutUtility.ScoreBar(rect, score, maxScore, null, prebuiltLabel: prebuilt);
        }
    }
}
