using System;
using System.Collections.Generic;
using System.Linq;
using RimWorld;
using UnityEngine;
using Verse;
using CombatExtended;

namespace CEReloaded
{
    // The unified "Generate Loadouts" + "Assign Loadouts" wizard. Replaces
    // Dialog_AutoGenerateLoadouts and Dialog_AutoAssignPreview. Two independently collapsible sections; the entry
    // point decides which start open and MUST work with only one active (Generate-tab button = generate only,
    // Assign-tab button = assign only). When both are active, the Anti-flesh/Anti-mech slider is hoisted into one
    // shared control since it drives both phases identically; each section keeps its own remaining sliders.
    // Loads its own static Texture2D fields (e.g. LockTex) - see Dialog_AssignLoadouts' own identical
    // comment for why this attribute is required (2026-08-01).
    [StaticConstructorOnStartup]
    public class Dialog_SquadSetup : Window
    {
        private readonly Dialog_GenerateLoadouts genOwner;
        private readonly Dialog_AssignLoadouts assignOwner;
        private readonly Action<int> onDone;
        private readonly Action onCancel;
        private readonly Action onBack;   // Arm-my-Colony chain only: reopen the previous step (null = no Back button)
        private bool applied;
        private bool wentBack;
        // Set by the "Skip" button (Arm-my-Colony chain only) - like wentBack, tells PostClose this close
        // is chain NAVIGATION (move on to the next step with nothing applied here), not an abort, so its
        // onCancel path must not fire. Continues the chain via onDone(0), same "nothing happened this step"
        // signal a 0-checked Equipment Packs step already sends.
        private bool skipped;

        // ---- shared ----
        private float mech;
        private bool advancedOpen = false;   // "Advanced": sliders hidden behind this by default (plan 5.1)
        private bool diffOnly = false;       // "Show only changes" (plan 5.9)

        // Ammo variety per weapon (0 = exactly one type). Until the lazy-creation rollback this lived in the
        // Generate section and never had an effect on the assign path - "Override ammo" always called
        // ChooseAmmoPicks with 0.
        private float ammoVar;

        // ---- assign-side state (ported from the old Dialog_AutoAssignPreview) ----
        private static readonly Texture2D LockTex = ContentFinder<Texture2D>.Get("UI/Icon_Lock", true);
        // For every "protected from auto-replan, but still fully editable by hand" indicator - a padlock reads
        // as "nothing here can change", which is backwards for these rows/pawns (this caused
        // confusion when comparing Equipment Packs/Weapon Loadouts/Assign Loadouts side by side). LockTex
        // above stays reserved for genuinely non-interactive cases (e.g. a pawn who can never get a loadout).
        // Sourced from the shared LoadoutUtility.TexPin (2026-08-01) - this file, Dialog_GenerateEquipmentPack.cs,
        // and Dialog_AssignLoadouts.cs all independently loaded the identical texture before; centralized now.
        private static Texture2D PinTex => LoadoutUtility.TexPin;
        // Wide enough for a full per-item Ready/InProduction/Missing breakdown (main, sidearm, grenades, shield,
        // several ammo types) - not just a single "in stock" word. InitialSize below is widened by the exact same
        // amount so every other column (Planned loadout in particular) keeps its original width. Grown by the
        // same 60px SidearmColW below was narrowed by, so the Main/Packs/Ammo squares (which live in here) get
        // more room without changing the window's total width.
        private const float SupplyColW = 320f;
        private const float PinColW = 26f;
        // "Pack: X" cell squeezed between the planned-loadout cell and the Supply badge - only reserved
        // when the player has authored at least one Equipment Pack (see InitialSize below). Wide enough
        // for the pack name(s) plus a small per-item feasibility icon row.
        // 220 -> 176 (Sidearms and Pack columns slightly smaller so there is still room for the mass/bulk bars
        // at the end). The cells show names and an icon row; 176 still fits both, and the icon row truncates
        // with '+N' when needed.
        private const float PackColW = 176f;
        // "Sidearm: X, Y" cell right after Pack (or right after Planned when there's no Pack column) -
        // the manual multi-select entry point (Dialog_PickPacks/Dialog_PickLoadouts) a plain FloatMenu
        // can't offer, since it closes on the very first pick. Narrower than PackColW (2026-07-30) - the
        // icon-row-with-"+N"-overflow content it renders doesn't need as much room as the
        // freed-up space is worth to the Main/Packs/Ammo availability squares (SupplyColW) instead.
        private const float SidearmColW = PackColW - 60f;
        // The Planned-loadout column's content (an icon + a short truncated name) never needs more than
        // this - capping it instead of letting it stretch all the way to the Supply badge keeps the Pack
        // column from drifting far right into empty space whenever the loadout name happens to be short.
        // Narrowed from 280 to free up room for the Medical column and the new Sidearm cell without
        // growing the window by their full combined width.
        private const float PlanCellW = 220f;
        // Base x (before +PinColW) of the Planned-loadout column - shifted right from the old 540 by
        // MedColW+8 to make room for the Medical bar inserted before Current.
        private const float PlanColBaseX = 634f;

        // Where the Pack column starts: right after the capped Planned-loadout column, or the old
        // tight-against-Supply position on a narrow window where PlanCellW isn't actually available -
        // whichever puts it further left. Shared by the header row and DrawPawnRow so they always agree.
        // Without a Pack column at all (no Equipment Packs authored), there's nothing to protect from
        // drifting into empty space - Planned stretches all the way to the Supply badge, same as before
        // PlanCellW existed. Also reserves room for the Sidearm column (hasSidearms) on its right so Pack
        // never drifts far enough right to collide with it.
        // Lower bound of the Planned column. This cell is the ONLY entry point for pinning and for
        // drag-to-swap; if its computed width goes negative, no Rect.Contains holds anymore, and on narrow
        // screens both interactions are lost without replacement - with nothing pointing it out. Calculated:
        // 1920 -> 184px (tight but intact), 1600 -> 130px, 1440 -> -30px, 1366 -> -104px.
        private const float PlanCellMinW = 120f;

        // Mass and bulk at the far right, behind the supply column: new columns at the far right, without
        // reinventing the wheel - the bar from the compact Assign Loadouts view is reused. Exactly that:
        // Dialog_AssignLoadouts.DrawPawnCapacityBar draws the same bar with the same carrier icon and the same
        // tooltip. 104 instead of the compact view's 112 - here there is no warning sign NEXT to the number,
        // the same row is just narrower; measured, '260.5/92.5 kg' with the icon fits.
        private const float MassColW = 104f, BulkColW = 104f;
        private const float MassBulkColTotalW = MassColW + BulkColW + 18f;

        // Which of the two EXTRA columns still fit at this window width? If there is not enough room, the
        // Sidearm column goes first, then the Pack column. Both are display only; the Planned cell is a control
        // and must never drop below its minimum width - so the others give way, not it. Deliberately the
        // existing mechanism (the columns are switchable via hasPacks/hasSidearms anyway) instead of a new
        // special case, and deliberately ONE function that header and rows call together - two separate
        // calculations would be exactly the kind of "both are reasonable and contradict each other" that has
        // cost this codebase time before.
        // At 1920 and wider nothing changes: neither reduction kicks in there.
        private void EffectiveColumns(float totalW, ref bool hasPacks, ref bool hasSidearms)
        {
            float minPackX = PlanColBaseX + PinColW + PlanCellMinW + 6f;
            if (hasSidearms && PackColX(totalW, hasPacks, true) < minPackX) hasSidearms = false;
            if (hasPacks && PackColX(totalW, true, hasSidearms) < minPackX) hasPacks = false;
        }

        // ONE paste column for the whole row, at the far right behind Sidearm - the same decision the live tab
        // already implemented, carried over here. Previously each of the two cells in this preview carried its
        // own paste strip and reserved its width permanently, although the clipboard only ever holds ONE
        // object.
        // Ammo and Other items are DELIBERATELY not included: this preview writes nothing live, it collects
        // pins - and for neither is there a cell here a pin could go into; their paste branches
        // (PasteAmmo/PasteOtherItems) write straight to the anchor and would break the preview's "nothing
        // happens before Apply" contract. Since the clipboard holds only one kind at a time anyway, the column
        // can never run into those branches. (The missing column with ammo in the clipboard is therefore not a
        // bug.)
        private static bool PreviewClipboardHasAny()
            => LoadoutClipboard.HasContent("pack") || LoadoutClipboard.HasContent("sidearm")
               || LoadoutClipboard.HasContent("mainweapon");

        private static float PasteColTotalW => PreviewClipboardHasAny() ? Dialog_AssignLoadouts.PasteColW + 6f : 0f;

        private float PasteColX(float totalW, bool hasPacks, bool hasSidearms)
        {
            if (hasSidearms) return SidearmColX(totalW, hasPacks, true) + SidearmColW + 6f;
            if (hasPacks) return PackColX(totalW, true, false) + PackColW + 6f;
            return totalW - MassBulkColTotalW - SupplyColW - PasteColTotalW;
        }

        private float PackColX(float totalW, bool hasPacks, bool hasSidearms)
        {
            float rightAnchored = totalW - MassBulkColTotalW - SupplyColW - PasteColTotalW - (hasPacks ? PackColW + 6f : 0f) - (hasSidearms ? SidearmColW + 6f : 0f);
            if (!hasPacks) return rightAnchored;
            float leftAnchored = PlanColBaseX + PinColW + PlanCellW + 6f;
            return Mathf.Min(leftAnchored, rightAnchored);
        }

        // Sidearm column: right after Pack when one is shown, otherwise the same left/right-anchored
        // choice Pack itself would have made in its place.
        private float SidearmColX(float totalW, bool hasPacks, bool hasSidearms)
        {
            if (!hasSidearms) return totalW - MassBulkColTotalW - SupplyColW - PasteColTotalW;
            if (hasPacks) return PackColX(totalW, true, true) + PackColW + 6f;
            float leftAnchored = PlanColBaseX + PinColW + PlanCellW + 6f;
            float rightAnchored = totalW - MassBulkColTotalW - SupplyColW - PasteColTotalW - SidearmColW - 6f;
            return Mathf.Min(leftAnchored, rightAnchored);
        }

        // Is there at least one real Weapon Loadout a pawn could carry as a sidearm? Gates the whole
        // Sidearm column the same way hasPacks gates Pack - checked against the global loadout list (not
        // `candidates`, which may not be built yet the first time InitialSize/the header need this).
        private float craft, melee, diversity, budget;
        // Sidearms still exist - they just live as their own separate Weapon Loadout now instead of baked
        // into the main one (see SidearmAssignmentUtility). 0 = nobody gets one; 1 = everybody does, best
        // combat priority first (melee sidearms for ranged mains fill up before ranged sidearms for melee
        // mains - see AssignPlanner's two-pass PickSidearm ordering). A modest opt-in by default rather
        // than fully off - most squads want at least their sharpest shooters carrying a backup blade.
        private float sidearmCoverage = 0.2f;
        // A separate pool/budget from sidearmCoverage above - a grenade is not a substitute for a real backup
        // weapon (AssignPlanner.PickGrenade never competes with PickSidearm for the same slot), it just lands
        // in the same "Sidearm" list and cell once picked. 0 = nobody gets one by default; a modest opt-in
        // like sidearmCoverage, not full off - most squads want at least their sharpest fighters carrying one.
        private float grenadeCoverage = 0.2f;
        // Shields are Equipment Pack content (a "Shield Kit" pack), never a separate sidearm-style pick - an
        // earlier version of this mod tried shields as their own standalone Weapon Loadout candidate
        // (mirroring grenades) with a dedicated shieldCoverage slider - removed: that candidate only ever
        // got created from actual map stock/production capability, completely independent of any Equipment
        // Pack referencing the same shield def, so a shield authored ONLY inside a Pack (the normal way to
        // do it) never became a candidate and the slider never did anything.
        // shieldCoverage gets its OWN dedicated slider again (separate from packCoverage below) - shield
        // need is driven by melee-vs-ranged role, a different axis entirely from "how much general supply
        // does the squad get", so lumping it into the same knob as Medic Kit/Base Supply/etc. meant a
        // player could never dial up shields without also dialing up every other specialist pack. Gradual,
        // best-melee-fighter-first fill (same proportional-slot-count shape sidearmCoverage/grenadeCoverage
        // already use below) instead of the old rank-threshold's "everyone eligible gets it the instant this
        // tier unlocks" cliff - see AssignPlanner.Compute's own shieldCoverage handling. Checked by CONTENT
        // (EquipmentPackUtility.ContainsShield inspects the pack's actual slots), never by the pack's name -
        // a player's own hand-authored shield pack gets identical treatment to the built-in "Shield Kit".
        private float shieldCoverage = 0.3f;
        // 0 = nobody gets any pack at all; 1 = every eligible pawn gets every applicable pack type (Medical
        // gating still applies; shields are excluded from this entirely now - see shieldCoverage above).
        // NOT a per-pawn coverage like sidearmCoverage - ranks pack TYPES by essentialness (food/medicine
        // first, specialized content layered on after) and includes more of them as this rises; every
        // eligible pawn always gets every INCLUDED type, never "some pawns get everything, others get
        // nothing" (see AssignPlanner.PackRank). Defaults to the middle, same "balanced" position every
        // other slider here sits at - not 0 (that read as broken/no packs at all, unlike sidearms which are
        // meant to be a deliberate opt-in) and not 1 (Generous piles on every pack type unasked).
        private float packCoverage = 0.5f;
        // createNewLoadouts went away with lazy creation: virtual picks always compete, the template is an
        // invisible background object. UiState.asCreateNew stays as a dead Scribe line so old saves load
        // without migration noise.
        private bool keepAssigned, redistribute, overrideAmmo;
        private readonly HashSet<Pawn> locked = new HashSet<Pawn>();
        private readonly Dictionary<Pawn, Loadout> pinned = new Dictionary<Pawn, Loadout>();   // NEW: manual override (5.5)
        // Manual per-pawn Equipment Pack override from the preview's own Pack cell - independent of the
        // weapon pin above, so a player can override just the pack set without also pinning the weapon.
        private readonly Dictionary<Pawn, List<Loadout>> pinnedPacks = new Dictionary<Pawn, List<Loadout>>();
        // Same idea for sidearms - a FloatMenu can only ever pick one option and close, so picking SEVERAL
        // sidearms needs its own checklist (Dialog_PickLoadouts), backed by its own pending-pin dictionary.
        private readonly Dictionary<Pawn, List<Loadout>> pinnedSidearms = new Dictionary<Pawn, List<Loadout>>();
        // Drag one colonist row onto another to swap their planned loadouts (pins both, so the swap sticks).
        // dragPawn != null IS "a drag is in progress" - see HandleDragHandle's comment for why this uses plain
        // MouseDown/MouseUp instead of EventType.MouseDrag.
        private Pawn dragPawn;

        // Supply-badge breakdown: whether EVERY item a planned loadout needs (main, sidearm, grenades, shield,
        // ammo) is covered, not just the main weapon - see SupplyRequirementsFor. Cached per Loadout object,
        // cleared whenever the plan recomputes.
        private enum ReqStatus { Ready, InProduction, Missing }
        private enum ReqCategory { MainWeapon, Sidearm, Grenade, Shield, Ammo, Pack }
        // neededCount: how many units a Missing item's crafting bill should cover - 1 for anything equip-once
        // (weapon/shield), the actual shortfall for stackable ammo (a bill for 1 round would be absurd).
        private class SupplyReqItem { public ThingDef icon; public string label; public ReqStatus status; public ReqCategory category; public int neededCount = 1; }
        // Short enough to fit DrawTotalCostLine's fixed 74px label column - "Main weapons:"/"Ammunition:" wrapped
        // or got cut off there (word-wrap only breaks at a space, and "Ammunition:" has none before the colon).
        private static readonly (ReqCategory cat, string label)[] CostCategoryLabels =
        {
            (ReqCategory.MainWeapon, "CER_SquadSetup_CostLabelMain".Translate()),
            (ReqCategory.Sidearm, "CER_SquadSetup_CostLabelSidearms".Translate()),
            (ReqCategory.Grenade, "CER_SquadSetup_CostLabelGrenades".Translate()),
            (ReqCategory.Shield, "CER_SquadSetup_CostLabelShields".Translate()),
            (ReqCategory.Ammo, "CER_SquadSetup_CostLabelAmmo".Translate()),
            (ReqCategory.Pack, "CER_SquadSetup_CostLabelPacks".Translate()),
        };
        // Keyed by (Loadout, Pawn), NOT just Loadout (2026-07-30, real bug found: when several pawns share the
        // SAME Role loadout, SupplyRequirementsFor's own result is pawn-specific throughout - e.needsCraft for
        // the main weapon depends on reservation order (whichever pawn's turn came first got the last copy in
        // stock), and WeaponStatus checks e.pawn's OWN gear via PawnAlreadyHasItem for every other item - so
        // caching by Loadout alone let the FIRST pawn processed on a Role silently overwrite what every OTHER
        // pawn on that same Role saw, showing "everything covered" for all of them even when only one copy
        // actually existed. Observed: Assign preview showed every Role-Breacher pawn's main weapon green,
        // but the very next Supply popup then asked to craft 3 more KS-23s.
        private readonly Dictionary<(Loadout, Pawn), List<SupplyReqItem>> supplyReqCache = new Dictionary<(Loadout, Pawn), List<SupplyReqItem>>();
        // Plan-WIDE shortfall, built once per RecomputeAssign (2026-07-31 real bug fix, found by a code-trace
        // audit): SupplyRequirementsFor/SidearmRequirementsFor/PackRequirementsFor used to each independently
        // ask "does at least one exist in the colony right now" per row - several pawns in the SAME preview
        // batch wanting the same scarce sidearm/grenade/shield/ammo/pack item each passed that check and all
        // showed green, even when the colony doesn't own enough for all of them together (the exact class of
        // bug already diagnosed once for the crafting-cost summary strip below, in the comment on
        // ComputeAssignCraftCostsByCategory - "3 pawns each wanting a Buckler with only 2 in stock all showed
        // 'in stock'" - but never applied to these per-row status dots until now). Reuses the SAME aggregation
        // functions that strip already trusts (Dialog_AssignLoadouts.ComputeMissingSplit,
        // LoadoutUtility.ComputeAmmoNeedForLoadouts) instead of a new parallel implementation. Main weapon
        // status is untouched - e.needsCraft already comes from AssignPlanner's own real, sequential
        // reservation and was never part of this bug.
        private Dictionary<ThingDef, int> planWideSecMissing = new Dictionary<ThingDef, int>();
        private Dictionary<ThingDef, int> planWideShieldMissing = new Dictionary<ThingDef, int>();
        private Dictionary<ThingDef, int> planWideAmmoMissing = new Dictionary<ThingDef, int>();
        private Dictionary<ThingDef, int> planWidePackItemMissing = new Dictionary<ThingDef, int>();
        private Dictionary<LoadoutGenericDef, int> planWidePackGenericMissing = new Dictionary<LoadoutGenericDef, int>();

        // Cache for the preview's cost bar (ComputeAssignCraftCostsByCategory). It used to run completely anew
        // every IMGUI pass: ProducibleNow + GetRecipeIngredients (new list + sort with string comparison) per
        // def, across the whole plan.
        //
        // Deliberately TWO guards, because one alone would be dangerous here (which is why this was left
        // undone for a while):
        // - `assignPlanVersion` increments on every plan change and validates the normal case immediately.
        //   It is incremented at the only two places a plan changes: RecomputeAssign (the full re-solve) and
        //   AdoptExtrasPin (the in-place change from pinning/pasting).
        // - The half second next to it is insurance against exactly the case that held this back: if a third
        //   change site were added later and forgot the counter, the number would be stale for half a second
        //   instead of permanently wrong. A silently wrong cost figure is the only really expensive outcome here.
        private int assignPlanVersion;
        private Dictionary<ReqCategory, Dictionary<ThingDef, int>> costCache;
        private int costCacheVersion = -1;
        private float costCacheTime = -999f;
        // The same two guards, second use: SupplyRequirementsFor has always had supplyReqCache, but its two
        // siblings SidearmRequirementsFor and PackRequirementsFor ran unthrottled per row per frame - and every
        // row calls all three.
        // Why NOT the design of supplyReqCache (only cleared on RecomputeAssign): that one reads the TEMPLATE
        // (e.planned), which pinning doesn't change. These two read the PINS - exactly what changes without a
        // re-solve. Hence assignPlanVersion, which increments on RecomputeAssign AND on AdoptExtrasPin, plus the
        // same half second as insurance against a third change site forgotten in the future. Separate per
        // colonist, because both are pawn-specific.
        private readonly Dictionary<Pawn, List<SupplyReqItem>> sidearmReqCache = new Dictionary<Pawn, List<SupplyReqItem>>();
        private readonly Dictionary<Pawn, List<SupplyReqItem>> packReqCache = new Dictionary<Pawn, List<SupplyReqItem>>();
        private int extraReqCacheVersion = -1;
        private float extraReqCacheTime = -999f;
        private List<AssignCandidate> candidates;
        private Dictionary<ThingDef, int> owned;
        private AssignPlan assignPlan;
        private bool assignDirty = true;
        private Vector2 assignScroll;

        public override Vector2 InitialSize
        {
            get
            {
                // 1060 -> 1150 (2026-08-01) - the generate row's real overlap was addAmmoType(+) at a fixed
                // x=694 colliding with ammoCount's own range, not a lack of window width; that's fixed at the
                // Rect level now (addAmmoType moved to x=740, Effect/Availability/Status LEFT-anchored right
                // after it - see GenEffectColX etc. and DrawGenerateRow). This just needs to comfortably fit
                // GenStatusColX + GenStatusColW (976+92=1068) plus the scrollbar/margins - a much smaller bump
                // than the 1272 tried first, which only pushed those columns into a large dead gap instead.
                // 1150 -> 1160 (2026-08-02, "first column" unification) - every column from the weapon icon
                // onward shifted +142 (status tag moved next to the checkbox instead), so the row's real right
                // edge is now GenAvailColX + GenAvailColW (1012+90=1102) plus scrollbar/margins - re-verified
                // with a layout renderer, which caught this as an OVERFLOW-X before this
                // bump (was still 1150, 4px short of the new right edge).
                float w = 1024f;
                // Pack/Sidearm/Supply are always reserved now (the cells always draw, showing "-"/"None"
                // when nothing exists yet) and Pack/Sidearm are left-anchored right after the Planned-
                // loadout column (see PackColX/SidearmColX) - so the window only needs to be exactly that
                // wide: Plan column + Pack + Sidearm + the Supply badge, back to back. Deriving this from
                // the SAME constants PackColX/SidearmColX use (rather than an independently-tuned number)
                // is deliberate - a separate magic number here previously drifted out of sync with the
                // real column math, leaving a large dead gap between the Sidearm column and the Supply
                // badge (the badge is right-anchored to the window's own edge, so any extra width here
                // shows up as empty space between it and the left-anchored columns, not as more room for
                // either).
                float packSidearmStart = PlanColBaseX + PinColW + PlanCellW + 6f;
                w = packSidearmStart + PackColW + 6f + SidearmColW + 6f + SupplyColW + MassBulkColTotalW + 16f;
                return new Vector2(Mathf.Min(w, UI.screenWidth - 40f), Mathf.Min(1080f, UI.screenHeight - 30f));
            }
        }

        protected override void SetInitialSizeAndPosition()
        {
            base.SetInitialSizeAndPosition();
            windowRect.y = Mathf.Max(20f, windowRect.y - 90f);   // clear the tab window's own header/filter row
            // Don't slide under RimWorld's learning readout - see LoadoutUtility.KeepClearOfLearningReadout.
            // Without a collision this changes nothing.
            // After moving up, not before: the learning readout starts at y=8, so the window moves EXACTLY into
            // its height here - checking beforehand would have missed the collision.
            windowRect = LoadoutUtility.KeepClearOfLearningReadout(windowRect);
        }

        public Dialog_SquadSetup(Dialog_GenerateLoadouts genOwner, Dialog_AssignLoadouts assignOwner,
            Action<int> onDone = null, Action onCancel = null, bool? presetKeepAssigned = null,
            Action onBack = null) : base(new ReadoutAwareWindowDrawing())
        {
            this.assignOwner = assignOwner;
            this.genOwner = genOwner ?? assignOwner?.parentWindow?.LoadoutsDialog;
            this.onDone = onDone;
            this.onCancel = onCancel;
            this.onBack = onBack;
            doCloseX = true;
            forcePause = true;
            absorbInputAroundWindow = true;
            closeOnAccept = false;

            // The sliders start at the default on EVERY open: slider positions don't belong in the save; defaults
            // are changed via the presets in the mod settings.
            // The default IS the "Balanced" preset from the mod settings for all fixed sliders - the same source as
            // "Use recommended setup" -, anti-mech and pool come, as there, from the automatic assessment
            // (mechanoids seen, research level). Previously the values came from the save (CERUiState): whoever
            // last touched the sliders decided what showed on the next open. Only the CHECKBOXES below stay in the
            // save.
            CERUiState ui = RoleUtility.Ui();
            ApplyRecommendedSliders(ui);
            keepAssigned = ui.asKeepAssigned;
            redistribute = ui.asRedistribute;
            overrideAmmo = ui.asOverrideAmmo;
            if (presetKeepAssigned.HasValue) keepAssigned = presetKeepAssigned.Value;
            BuildAssignStaticData();
        }

        // ================================================================================================
        // Generate-side pure logic (ported verbatim from Dialog_AutoGenerateLoadouts)
        // ================================================================================================

        // Multi-ammo choice (up to 4 rations): the PRIMARY is what the old single pick was (mech-direction if
        // decisive, else the configured default). Extra rations only join when they add real coverage - each
        // candidate is valued by how far its anti-flesh/anti-mech affinity sits from everything already packed
        // (near-duplicates add nothing), scaled by how mixed the expected threat is (mid-slider = mixed = variety
        // pays; hard anti-X = one job, one round). The total round count stays exactly the single-ammo budget
        // (variety splits it, never adds weight), and a ration is only worth carrying in whole magazines (min one),
        // so small budgets naturally stay single-ammo instead of ending up with four token stashes.
        private List<Dialog_GenerateLoadouts.AmmoPick> ChooseAmmoPicks(ThingDef w)
        {
            var picks = new List<Dialog_GenerateLoadouts.AmmoPick>();
            bool selfAmmo = LoadoutUtility.IsSelfAmmoWeapon(w);
            List<Def> defs = LoadoutUtility.GetLoadoutAmmoDefs(w, selfAmmo)
                .Where(a => Dialog_GenerateLoadouts.IsResearched(a)).ToList();

            if (defs.Count == 0) return picks;

            // Less-lethal rounds (beanbag, rubber: Stun WITHOUT the EMP counter-effect) are capture/riot tools, not
            // combat ammo. The ballistic model parks them at the anti-flesh EXTREME (zero penetration + stun nudge),
            // which made the coverage metric grab them as the maximum-contrast ration at hard anti-mech of all
            // places. The auto-pick skips them entirely; they stay available through the row's "+" picker for
            // deliberate guard/warden loadouts.
            List<Def> lethal = defs.Where(d => !(d is ThingDef td)
                || (LoadoutUtility.GetAmmoEffects(td) & SpecialAbility.Stun) == 0
                || (LoadoutUtility.GetAmmoEffects(td) & SpecialAbility.EMP) != 0).ToList();
            if (lethal.Count > 0) defs = lethal;   // a weapon with ONLY less-lethal rounds keeps them (better than nothing)

            if (!int.TryParse(LoadoutUtility.GetDefaultAmmoCountForWeapon(w), out int budget) || budget <= 0) budget = 60;

            float dir = (mech - 0.5f) * 2f;
            // Is there any non-EMP type at all? Only then may EMP drop out of the primary slot - a weapon that
            // knows NOTHING but EMP would otherwise get no ammo at all.
            bool hasNonEmp = defs.Any(d => d is ThingDef ntd && (LoadoutUtility.GetAmmoEffects(ntd) & SpecialAbility.EMP) == 0);
            Def primary = null;
            if (Mathf.Abs(dir) >= 0.2f)
            {
                // RELATIVE pick, not absolute: at full anti-mech even a mediocre AP round is the right choice over
                // FMJ. The old form demanded an absolutely-good-vs-mech score, so calibers that all struggle against
                // mech armor (most rifle rounds) silently fell back to the configured default - FMJ at 100% anti-mech.
                // The default only wins when the variants barely differ in the slider's direction; the spread gate
                // scales with |dir|, so near the middle only strongly-different variants (EMP) flip the pick, while
                // at the extremes any meaningful difference does.
                ThingDef best = null;
                float bestV = float.MinValue, worstV = float.MaxValue;
                foreach (Def d in defs)
                {
                    if (!(d is ThingDef td)) continue;
                    float v = dir * LoadoutUtility.ItemMechAffinity(td);
                    // EMP sits at the very top of the anti-mech AXIS but is stun-utility, not a killing round - as
                    // the BULK ration it would leave the pawn unable to finish anything. The malus hands the primary
                    // slot to the best penetrating damage round (Sabot > AP); EMP joins as a ration below instead.
                    //
                    // 0.7 did NOT achieve that, measured: EMP carries the hard affinity +1, so 0.70 with the malus -
                    // and no killing round comes close. Sabot sits at +0.27, Slug at +0.43. Result at the right end
                    // of the slider: all four 12-gauge weapons got '(EMP)' as PRIMARY ammo, and Slug dropped out of
                    // the pair entirely - although the same weapon plans Slug at the slider's middle. With rifle
                    // calibers it only goes unnoticed because there is no EMP type there at all.
                    // A bigger malus would be another guessed number. The intent above is stated absolutely -
                    // 'never the bulk ration' -, so it is implemented absolutely too: EMP is not eligible for the
                    // PRIMARY slot. As a ration further down it stays selectable unchanged, and that is exactly
                    // where it belongs. The same construction as the less-lethal filter above: exclude, but only
                    // as long as something else remains.
                    if (dir > 0f && hasNonEmp && (LoadoutUtility.GetAmmoEffects(td) & SpecialAbility.EMP) != 0) continue;
                    if (v > bestV) { bestV = v; best = td; }
                    if (v < worstV) worstV = v;
                }
                if (best != null && bestV - worstV >= 0.15f) primary = best;
            }
            if (primary == null) primary = LoadoutUtility.PickDefaultAmmo(defs);
            if (primary == null) return picks;
            picks.Add(new Dialog_GenerateLoadouts.AmmoPick { def = primary, count = budget });

            // Hard left on the variety slider = exactly one type, full stop. Generic "Any ammo" placeholders and
            // self-ammo weapons (grenade mains, javelins) can't be split either way.
            if (ammoVar <= 0.02f || selfAmmo || !(primary is ThingDef primaryTd)) return picks;

            // Ration quantum: whole magazines, at least one, aiming at ~25% of the budget.
            int mag = 1;
            CompProperties_AmmoUser au = w.GetCompProperties<CompProperties_AmmoUser>();
            if (au != null) mag = Mathf.Max(1, au.magazineSize);
            int ration = Mathf.Max(1, Mathf.RoundToInt(budget * 0.25f / mag)) * mag;

            float mixNeed = 1f - Mathf.Abs(dir);              // 1 = mixed threat expected, 0 = player said exactly what they fight
            // The variety slider OVERRIDES the threat-certainty damper, it doesn't just scale it: at full
            // flexibility the player explicitly asked for variety, so a hard anti-mech slider must not silently
            // veto it (mixNeed ~0 used to crush the weight so far that even "maximum" barely ever split). The
            // slider blends mixNeed toward 1 (weight side) and drops the accept bar (threshold side); the
            // whole-magazine ration floor still applies at every setting.
            float weight = 0.2f + 0.8f * Mathf.Lerp(mixNeed, 1f, ammoVar);
            float thrScale = Mathf.Lerp(1.9f, 0.55f, ammoVar);
            float[] slotThreshold = { 0.5f * thrScale, 0.75f * thrScale, 1.0f * thrScale };   // the 2nd/3rd/4th ration get progressively harder to justify

            var chosenAff = new List<float> { LoadoutUtility.ItemMechAffinity(primaryTd) };
            bool haveEmp = (LoadoutUtility.GetAmmoEffects(primaryTd) & SpecialAbility.EMP) != 0;
            bool haveFlame = (LoadoutUtility.GetAmmoEffects(primaryTd) & SpecialAbility.Flame) != 0;
            while (picks.Count < 4)
            {
                // The primary keeps the majority stake: never let a new ration push it below 40% (or below one ration).
                if (picks[0].count - ration < Mathf.Max(ration, Mathf.RoundToInt(budget * 0.4f))) break;

                ThingDef best = null;
                float bestV = 0f;
                foreach (Def d in defs)
                {
                    if (!(d is ThingDef td)) continue;
                    if (picks.Any(p => p.def == td)) continue;
                    float aff = LoadoutUtility.ItemMechAffinity(td);
                    float minDist = float.MaxValue;
                    foreach (float a in chosenAff) minDist = Mathf.Min(minDist, Mathf.Abs(aff - a));
                    float v = minDist * weight;
                    // EMP complements the kinetic anti-mech round FUNCTIONALLY (stun vs damage), not on the affinity
                    // axis - Sabot and EMP sit nearly on the same spot there, so pure axis distance would never add
                    // the classic Sabot + EMP-magazine combo. One EMP ration earns a direct bid that grows with the
                    // anti-mech lean and the flexibility slider, bypassing the threat-mix damper (it is same-axis
                    // support, not cross-axis variety).
                    if (!haveEmp && dir > 0f && (LoadoutUtility.GetAmmoEffects(td) & SpecialAbility.EMP) != 0)
                        v = Mathf.Max(v, dir * (0.35f + 0.6f * ammoVar));
                    // Incendiary mirrors the EMP rule on the anti-flesh side - fire panics and area-denies flesh
                    // targets (mechs don't panic), a functional complement the axis distance can't see. Deliberately
                    // a weaker bid than EMP's: fire is a hazard to loot and terrain, so it earns its magazine only
                    // at a stronger anti-flesh lean or a higher flexibility setting.
                    else if (!haveFlame && dir < 0f && (LoadoutUtility.GetAmmoEffects(td) & SpecialAbility.Flame) != 0)
                        v = Mathf.Max(v, -dir * (0.25f + 0.5f * ammoVar));
                    if (v > bestV) { bestV = v; best = td; }
                }
                if (best == null || bestV < slotThreshold[picks.Count - 1]) break;
                if ((LoadoutUtility.GetAmmoEffects(best) & SpecialAbility.EMP) != 0) haveEmp = true;
                if ((LoadoutUtility.GetAmmoEffects(best) & SpecialAbility.Flame) != 0) haveFlame = true;

                picks[0].count -= ration;
                picks.Add(new Dialog_GenerateLoadouts.AmmoPick { def = best, count = ration });
                chosenAff.Add(LoadoutUtility.ItemMechAffinity(best));
            }
            return picks;
        }

        // ================================================================================================
        // Assign-side pure logic (ported verbatim from Dialog_AutoAssignPreview)
        // ================================================================================================

        private static void LoadoutWeaponTypes(Loadout lo, out bool hasMelee, out bool hasRanged)
        {
            hasMelee = false; hasRanged = false;
            if (lo == null || lo.Slots == null) return;
            foreach (LoadoutSlot s in lo.Slots)
            {
                ThingDef td = s.thingDef;
                if (td == null || !td.IsWeapon) continue;
                if (td.IsMeleeWeapon) hasMelee = true;
                if (td.IsRangedWeapon) hasRanged = true;
            }
        }

        // Release every pin whose weapon has since been disabled (a lock must trigger a re-evaluation). A pin
        // ranks above the solver - without this step precisely the colonist the player just locked the weapon
        // on would stay stuck with it. Main AND sidearm pins, because both point to a loadout with a weapon
        // in it.
        private void DropPinsOnDisabledWeapons()
        {
            bool DisabledInside(Loadout lo)
            {
                if (lo == null) return false;
                // The loadout ITSELF being disabled counts the same (locking roles/loadouts).
                if (LoadoutUtility.IsLoadoutDisabled(lo)) return true;
                if (lo.Slots == null) return false;
                foreach (LoadoutSlot s in lo.Slots)
                    if (s.thingDef != null && s.thingDef.IsWeapon && LoadoutUtility.IsWeaponDisabled(s.thingDef)) return true;
                return false;
            }
            foreach (Pawn p in pinned.Where(kv => DisabledInside(kv.Value)).Select(kv => kv.Key).ToList())
                pinned.Remove(p);
            foreach (Pawn p in pinnedSidearms.Keys.ToList())
            {
                List<Loadout> keep = pinnedSidearms[p].Where(l => !DisabledInside(l)).ToList();
                if (keep.Count == pinnedSidearms[p].Count) continue;
                if (keep.Count == 0) pinnedSidearms.Remove(p); else pinnedSidearms[p] = keep;
            }
        }

        private void BuildAssignStaticData()
        {
            owned = Dialog_AssignLoadouts.BuildOwnedWeaponCounts();
            candidates = new List<AssignCandidate>();
            foreach (Loadout lo in LoadoutManager.Loadouts)
            {
                if (lo == null || lo.defaultLoadout) continue;
                // Hidden per-pawn anchors ("[Pawn-Loadout] <id>", Equipment Packs) are internal bookkeeping
                // objects, never a real template - .Slots walks the WHOLE ancestor chain, so an anchor's own
                // AnalyzeLoadout still resolves to whatever weapon its (unrelated) owner actually carries.
                // Without this filter such an anchor could get picked here as if it were a genuine loadout for
                // a COMPLETELY DIFFERENT pawn, leaking the raw internal label ("[Pawn-Loadout] 363") into that
                // other pawn's planned-loadout cell - the same exclusion the grenade candidate loop below
                // already applies.
                if (AmmoAssignmentUtility.IsAmmoLink(lo) || EquipmentPackUtility.IsEquipmentPackLoadout(lo)) continue;
                if (!Dialog_AssignLoadouts.AnalyzeLoadout(lo, out ThingDef main, out float sc, out bool ranged, out WeaponGroup grp)) continue;
                // Disabled weapons: the automation never hands them out.
                // Roles stay exempt - what a role author baked in is their decision.
                if (!RoleUtility.IsRoleLoadout(lo) && LoadoutUtility.IsWeaponDisabled(main)) continue;
                // researchedOnly: this is the SUGGESTION, not the display - the anti-mech slider must not rank the
                // squad by a sabot round nobody can produce.
                LoadoutUtility.GetWeaponTargetScores(main, out float fSc, out float mSc, researchedOnly: true);
                LoadoutWeaponTypes(lo, out bool loMelee, out bool loRanged);
                candidates.Add(new AssignCandidate
                {
                    loadout = lo,
                    mainWeapon = main,
                    score = sc,
                    fleshScore = fSc,
                    mechScore = mSc,
                    isRanged = ranged,
                    group = grp,
                    mechAffinity = LoadoutUtility.LoadoutMechAffinity01(lo),
                    producible = Dialog_AssignLoadouts.ProducibleNow(main),
                    isRole = RoleUtility.IsRoleLoadout(lo),
                    hasMelee = loMelee,
                    hasRanged = loRanged,
                    // Disabled roles/templates DELIBERATELY stay in the list: the picker draws its rows from
                    // exactly this list, and without a row there would be no way left to re-enable them. The
                    // solver skips them (AssignPlanner.Compute), and so does the coverage calculation below.
                    isDisabled = LoadoutUtility.IsLoadoutDisabled(lo)
                });
            }

            // Disabled ones do NOT count as "covered" here - otherwise a disabled template would prevent the same
            // weapon from moving up as a virtual candidate, and the weapon would be locked along with the
            // TEMPLATE. The template is what is disabled, not the weapon.
            HashSet<ThingDef> covered = new HashSet<ThingDef>(candidates.Where(c => !c.isDisabled).Select(c => c.mainWeapon));
            foreach (ThingDef d in DefDatabase<ThingDef>.AllDefsListForReading)
            {
                if (!LoadoutUtility.IsRealWeaponDef(d) || LoadoutUtility.IsUtilityWeapon(d) || covered.Contains(d)) continue;
                if (Dialog_GenerateLoadouts.IsGrenade(d)) continue;
                if (LoadoutUtility.IsWeaponDisabled(d)) continue;
                bool ownedAny = owned.TryGetValue(d, out int oc) && oc > 0;
                bool prod = Dialog_AssignLoadouts.ProducibleNow(d);
                if (!ownedAny && !prod) continue;
                LoadoutUtility.GetWeaponScore(d, out float sc2, out _);
                if (sc2 <= 0f) continue;
                LoadoutUtility.GetWeaponTargetScores(d, out float fV, out float mV, researchedOnly: true);
                ThingDef vSide = genOwner != null ? genOwner.PickDefaultSidearm(d) : null;
                bool vMelee = d.IsMeleeWeapon || (vSide != null && vSide.IsMeleeWeapon);
                bool vRanged = d.IsRangedWeapon || (vSide != null && vSide.IsRangedWeapon);
                candidates.Add(new AssignCandidate
                {
                    loadout = null,
                    isVirtual = true,
                    mainWeapon = d,
                    score = sc2,
                    fleshScore = fV,
                    mechScore = mV,
                    isRanged = d.IsRangedWeapon && !d.IsMeleeWeapon,
                    group = LoadoutUtility.ClassifyWeaponGroup(d),
                    mechAffinity = Mathf.Clamp01(0.5f + 0.5f * LoadoutUtility.ItemMechAffinity(d)),
                    producible = prod,
                    isRole = false,
                    hasMelee = vMelee,
                    hasRanged = vRanged
                });
            }

            // Grenades: AnalyzeLoadout deliberately never treats a thrown explosive as a "main weapon" (right
            // for scoring a REGULAR weapon's own main pick), which left every real grenade-only Weapon Loadout
            // with no way to ever become an AssignCandidate at all - PickGrenade's pool was silently empty
            // regardless of grenadeCoverage. Bypass AnalyzeLoadout here instead; one candidate per grenade type.
            HashSet<ThingDef> grenadeCovered = new HashSet<ThingDef>();
            foreach (Loadout lo in LoadoutManager.Loadouts)
            {
                if (lo == null || lo.Slots == null || !LoadoutUtility.IsPlainWeaponLoadout(lo)) continue;
                ThingDef nadeDef = lo.Slots.Select(s => s.thingDef).FirstOrDefault(td => td != null && Dialog_GenerateLoadouts.IsGrenade(td));
                if (nadeDef == null || grenadeCovered.Contains(nadeDef)) continue;
                if (LoadoutUtility.IsWeaponDisabled(nadeDef)) continue;
                grenadeCovered.Add(nadeDef);
                LoadoutUtility.GetWeaponScore(nadeDef, out float ndSc, out _);
                if (ndSc <= 0f) continue;
                LoadoutUtility.GetWeaponTargetScores(nadeDef, out float ndF, out float ndM, researchedOnly: true);
                candidates.Add(new AssignCandidate
                {
                    loadout = lo,
                    mainWeapon = nadeDef,
                    score = ndSc,
                    fleshScore = ndF,
                    mechScore = ndM,
                    isRanged = true,
                    group = LoadoutUtility.ClassifyWeaponGroup(nadeDef),
                    mechAffinity = LoadoutUtility.LoadoutMechAffinity01(lo),
                    producible = Dialog_AssignLoadouts.ProducibleNow(nadeDef),
                    isRole = false,
                    hasMelee = false,
                    hasRanged = true,
                    isDisabled = LoadoutUtility.IsLoadoutDisabled(lo)
                });
            }

            // Virtual grenade candidates (lazy creation): the loop above only covers grenades that already have
            // a template, and the main weapon virtuals exclude grenades (they are not a main weapon). Without
            // this block the grenade slider would silently have NOTHING left to hand out once mandatory bulk
            // creation went away.
            foreach (ThingDef d in DefDatabase<ThingDef>.AllDefsListForReading)
            {
                if (!LoadoutUtility.IsRealWeaponDef(d) || LoadoutUtility.IsUtilityWeapon(d)) continue;
                if (!Dialog_GenerateLoadouts.IsGrenade(d) || grenadeCovered.Contains(d)) continue;
                if (LoadoutUtility.IsWeaponDisabled(d)) continue;
                bool ownedNade = owned.TryGetValue(d, out int onc) && onc > 0;
                bool prodNade = Dialog_AssignLoadouts.ProducibleNow(d);
                if (!ownedNade && !prodNade) continue;
                LoadoutUtility.GetWeaponScore(d, out float gSc, out _);
                if (gSc <= 0f) continue;
                LoadoutUtility.GetWeaponTargetScores(d, out float gF, out float gM, researchedOnly: true);
                candidates.Add(new AssignCandidate
                {
                    loadout = null,
                    isVirtual = true,
                    mainWeapon = d,
                    score = gSc,
                    fleshScore = gF,
                    mechScore = gM,
                    isRanged = true,
                    group = LoadoutUtility.ClassifyWeaponGroup(d),
                    mechAffinity = Mathf.Clamp01(0.5f + 0.5f * LoadoutUtility.ItemMechAffinity(d)),
                    producible = prodNade,
                    isRole = false,
                    hasMelee = false,
                    hasRanged = true
                });
            }

            // Shields are Equipment Pack content now (a "Shield Kit" pack), not a standalone Weapon Loadout
            // candidate - AssignPlanner.ChoosePacksFor already gates a shield-containing pack correctly. No
            // shield-specific candidate is created here anymore.

            // Since lazy creation this runs over ALL candidates including virtual ones: "there is something
            // better that no loadout uses" was a gap indicator for the template stock - virtual candidates close
            // exactly that gap on their own, so the old calculation would have nagged wrongly forever.
            // Without the disabled ones: the coverage calculation answers "is there something better the solver
            // could take" - what it may never take doesn't belong in the answer.
            assignDirty = true;
        }

        private void RecomputeAssign()
        {
            // Pawn override (plan 5.5) is handled INSIDE the solver (AssignPlanner.Compute's `pinned` parameter):
            // a pinned pawn is excluded from re-planning exactly like a locked pawn (so it never gets reshuffled by
            // a later slider tweak) but keeps `kept == false`, so it still counts as a change, still applies on
            // Apply, and still shows a Supply badge - unlike a genuinely "kept, nothing to do" pawn.
            assignPlan = AssignPlanner.Compute(assignOwner.CachedPawns, assignOwner.RangedScoreOf, assignOwner.MeleeScoreOf,
                candidates, owned, craft, mech, melee, diversity, budget,
                assignOwner.roleMode, keepAssigned, redistribute, locked, pinned, pinnedPacks,
                sidearmCoverage, pinnedSidearms, packCoverage, grenadeCoverage, shieldCoverage);
            assignDirty = false;
            supplyReqCache.Clear();
            BuildPlanWideShortfalls();
            assignPlanVersion++;
        }

        // See the field comments above. Mirrors ComputeAssignCraftCostsByCategory's own already-correct
        // aggregation for sidearm/grenade/shield/ammo; adds the same idea for pack items, which that method
        // never aggregated either (it still sums pack costs via the per-row PackRequirementsFor, so the cost
        // strip's own Pack total has the identical latent gap - out of scope to fix here, but noted).

        // The slots of all virtual sidearm/grenade picks of a plan - from the same defaults the template is
        // created from on Apply (lazy creation). For the shortfall buckets, which otherwise only go through
        // existing template objects.
        private static List<LoadoutSlot> VirtualSidearmSlots(AssignPlan plan)
            => plan == null ? new List<LoadoutSlot>()
               : plan.entries.Where(e => e.plannedSidearmDefs != null)
                   .SelectMany(e => e.plannedSidearmDefs)
                   .SelectMany(d => Dialog_GenerateLoadouts.DefaultSlotsFor(d, Dialog_GenerateLoadouts.BuildDefaultSettings(d)))
                   .ToList();

        private void BuildPlanWideShortfalls()
        {
            planWideSecMissing = new Dictionary<ThingDef, int>();
            planWideShieldMissing = new Dictionary<ThingDef, int>();
            planWideAmmoMissing = new Dictionary<ThingDef, int>();
            planWidePackItemMissing = new Dictionary<ThingDef, int>();
            planWidePackGenericMissing = new Dictionary<LoadoutGenericDef, int>();
            if (assignPlan == null) return;

            List<(Pawn pawn, Loadout loadout)> pairs = assignPlan.entries.Where(e => !e.kept && e.planned != null)
                .Select(e => (e.pawn, e.planned)).ToList();

            // Weapon/shield shortfall (2026-08-01: now the SAME shared Dialog_AssignLoadouts.ComputeMissingSplit
            // the Supply popup itself uses, instead of a hand-rolled second copy of the same demand-sum-then-
            // subtract-owned-once logic - the identical "stock checked without subtracting what other
            // consumers already claimed" bug had already been fixed independently in three different places
            // before landing here; one shared method means the two screens can't drift out of sync again.
            // e.planned's own extras PLUS e.plannedSidearms' own attached weapon/shield, via the method's
            // dedicated sidearmLoadouts param - which (like this method's own old logic) deliberately never
            // lets a sidearm loadout's own gun populate "mainDefs" on its own.
            // NOT filtered to !e.kept (2026-08-02 fix: a kept pawn's sidearm badge showed
            // "everything covered" for a weapon the colony owns zero of) - a kept pawn's OWN demand was
            // being dropped from this fold entirely on the assumption their gear is "already reflected" in
            // `owned`, but that only holds if they actually physically carry it. ComputeMissingSplit's
            // `owned` pool (BuildOwnedWeaponCountsUncached) already counts every pawn's carried/equipped
            // gear, kept or not, so folding a kept pawn's demand back in nets to exactly 0 for one who
            // genuinely has their item (need 1, owned already includes that same 1) and correctly surfaces
            // a real shortfall for one who does not - it does not double-count anything.
            // Also folds in plannedPacks now (2026-08-02 fix: shields never showed up as
            // missing anywhere, in any screen) - shields moved from being their own Weapon Loadout to living
            // inside a "Shield Kit" Equipment Pack earlier (see BuildAssignStaticData's own
            // comment), but this fold was never updated to match: e.planned's bare Slots can never contain a
            // shield anymore (it only ever lived in a pack), so ComputeMissingSplit's shieldMissing output
            // was reading an always-empty source no matter what was actually assigned. attachedLoadouts (not
            // just sidearms anymore) mirrors BuildSupplyRequestForPawns' OWN reasoning for why this fold is
            // even needed here: that method reads a pawn's LIVE p.GetLoadout(), whose multi-parent Slots
            // union already includes attached packs/sidearms for real - but a PREVIEW plan (e.planned) has
            // no live anchor yet, so anything attached has to be folded in by hand, same as sidearms always were.
            List<Loadout> attachedLoadouts = assignPlan.entries.Where(e => e.plannedSidearms != null)
                .SelectMany(e => e.plannedSidearms)
                .Concat(assignPlan.entries.Where(e => e.plannedPacks != null).SelectMany(e => e.plannedPacks))
                .ToList();
            Dialog_AssignLoadouts.ComputeMissingSplit(pairs.Select(p => p.loadout), attachedLoadouts,
                out _, out planWideSecMissing, out planWideShieldMissing,
                extraSidearmSlots: VirtualSidearmSlots(assignPlan));

            // overrideAmmo (2026-08-01) mirrors SupplyRequirementsFor's own branch exactly, so the per-row
            // "missing" lookup there always matches what got counted here - main-weapon ammo only (sidearm
            // ammo below is unaffected by the toggle either way, same scope SupplyRequirementsFor has). Roles
            // are excluded from the override (fixed by design, same as packs/shields/explosives already
            // excluding them) - always resolved through their own frozen Slots via ComputeAmmoNeedForLoadouts,
            // regardless of the toggle.
            Dictionary<ThingDef, int> ammoNeed = new Dictionary<ThingDef, int>();
            List<(Pawn pawn, Loadout loadout)> overridePairs = overrideAmmo
                ? pairs.Where(p => !RoleUtility.IsRoleLoadout(p.loadout)).ToList() : new List<(Pawn, Loadout)>();
            List<(Pawn pawn, Loadout loadout)> templatePairs = overrideAmmo
                ? pairs.Where(p => RoleUtility.IsRoleLoadout(p.loadout)).ToList() : pairs;

            foreach (var (pawn, plo) in overridePairs)
            {
                LoadoutUtility.LoadoutMainAndSide(plo, out ThingDef mw, out _);
                if (mw == null || LoadoutUtility.IsShieldApparel(mw)) continue;
                foreach (var pick in ChooseAmmoPicks(mw))
                    if (pick.def is ThingDef td && pick.count > 0)
                        ammoNeed[td] = (ammoNeed.TryGetValue(td, out int c) ? c : 0) + pick.count;
            }
            if (templatePairs.Count > 0)
            {
                LoadoutUtility.ComputeAmmoNeedForLoadouts(templatePairs.Select(p => (p.loadout, p.pawn.LabelShortCap)),
                    out Dictionary<LoadoutGenericDef, int> generics, out Dictionary<ThingDef, int> specifics, out _);
                foreach (var kv in specifics) ammoNeed[kv.Key] = (ammoNeed.TryGetValue(kv.Key, out int c) ? c : 0) + kv.Value;
                foreach (var kv in generics)
                {
                    ThingDef gun = LoadoutUtility.GetWeaponForGenericAmmo(kv.Key);
                    List<Def> family = LoadoutUtility.GetAmmoTypesFor(gun).Where(LoadoutUtility.IsAmmoResearched).Cast<Def>().ToList();
                    ThingDef resolved = LoadoutUtility.PickDefaultAmmo(family) as ThingDef;
                    if (resolved == null) continue;
                    ammoNeed[resolved] = (ammoNeed.TryGetValue(resolved, out int c) ? c : 0) + kv.Value;
                }
            }
            // Real attached sidearms (e.plannedSidearms) also carry their OWN ammo need - not covered by the
            // pass above (that only reads e.planned's own Slots) - fold it into the SAME ammoNeed totals
            // before subtracting stock once, so a def needed by both a main loadout and a sidearm sums its
            // true combined demand instead of two independently-computed shortfalls being merged after the fact.
            // No `kept` filter anymore: after an Arm run practically every colonist is kept, so the filter
            // swallowed almost the entire sidearm ammo demand - the same class of bug and the same line as with
            // the missing sidearms one level further. The filter was the wrong half of a legitimate concern: a
            // pawn who already CARRIES his ammo must not trigger a shortfall. That is now handled by the third
            // stock bucket below (CarriedStockCount) - demand and carried stock cancel out exactly for him,
            // while the demand of someone who has nothing finally becomes visible.
            foreach (AssignPlanEntry e in assignPlan.entries)
            {
                foreach (Loadout sLo in e.plannedSidearms ?? new List<Loadout>())
                    foreach (LoadoutSlot s in sLo?.Slots ?? Enumerable.Empty<LoadoutSlot>())
                    {
                        // IsAmmoDef - the same refinement as in SidearmRequirementsFor, so the plan-wide demand
                        // and the row that shows it talk about the same set.
                        ThingDef ammoDef = s.thingDef != null && LoadoutUtility.IsAmmoDef(s.thingDef) ? s.thingDef : null;
                        if (ammoDef == null || s.count <= 0) continue;
                        ammoNeed[ammoDef] = (ammoNeed.TryGetValue(ammoDef, out int c) ? c : 0) + s.count;
                    }
                // Virtual picks (lazy creation): their ammo comes from the same defaults the template is created
                // from on Apply - same amount, same bucket.
                if (e.plannedSidearmDefs != null)
                    foreach (ThingDef sd in e.plannedSidearmDefs)
                        foreach (LoadoutSlot s in Dialog_GenerateLoadouts.DefaultSlotsFor(sd, Dialog_GenerateLoadouts.BuildDefaultSettings(sd)))
                        {
                            ThingDef ammoDef = s.thingDef != null && LoadoutUtility.IsAmmoDef(s.thingDef) ? s.thingDef : null;
                            if (ammoDef == null || s.count <= 0) continue;
                            ammoNeed[ammoDef] = (ammoNeed.TryGetValue(ammoDef, out int c) ? c : 0) + s.count;
                        }
            }
            foreach (var kv in ammoNeed)
            {
                // + GroundStockCount (2026-08-02: ammo visibly on the map still showed
                // "missing" here) - matches ComputeAmmoAvailability/GetAmmoOwnership's own identical widening,
                // so this badge can never disagree with what those functions (and the bars built on them)
                // already say about the same ammo def.
                // + CarriedStockCount: the third bucket. Storage and ground together are NOT the whole stock -
                // what a colonist has in his backpack is seen by neither. Without it every row whose demand was
                // only just added (see the kept filter above) reports the carried amount as missing.
                // Via LoadoutUtility.AmmoOnHand, so this badge and the gate in the supply window demonstrably use
                // the same sum - see there.
                int missing = kv.Value - LoadoutUtility.AmmoOnHand(kv.Key);
                if (missing > 0) planWideAmmoMissing[kv.Key] = missing;
            }

            // Pack items: no existing aggregation function (ComputeAssignCraftCostsByCategory itself never
            // built one either) - sum demand across every pawn's plannedPacks, same shape as
            // ScanMissingAssignedPackItems does for REAL assigned packs, but scoped to this PREVIEW plan.
            Dictionary<ThingDef, int> packItemDemand = new Dictionary<ThingDef, int>();
            Dictionary<LoadoutGenericDef, int> packGenericDemand = new Dictionary<LoadoutGenericDef, int>();
            // No `kept` filter anymore, same reasoning as for the sidearm demand above - and here it was even more
            // harmful: pack contents are consumables (medicine, rations) that a kept pawn is assigned but needs
            // anew after every fight.
            // Role content counts here just as in PackRequirementsFor - otherwise every new role pack row there
            // comes up with 0 missing and reports a green "ready", no matter how empty the storage is. The same
            // gap as in ScanMissingAssignedPackItems, just on the preview side.
            foreach (AssignPlanEntry e in assignPlan.entries)
            {
                var demandSlots = new List<LoadoutSlot>();
                foreach (Loadout pack in e.plannedPacks ?? new List<Loadout>())
                    if (pack?.OwnSlots != null) demandSlots.AddRange(pack.OwnSlots);
                demandSlots.AddRange(AmmoAssignmentUtility.RoleSupplySlots(e.planned));
                foreach (LoadoutSlot s in demandSlots)
                {
                    if (s.count <= 0) continue;
                    if (s.thingDef != null)
                        packItemDemand[s.thingDef] = (packItemDemand.TryGetValue(s.thingDef, out int c) ? c : 0) + s.count;
                    else if (s.genericDef != null)
                        packGenericDemand[s.genericDef] = (packGenericDemand.TryGetValue(s.genericDef, out int c) ? c : 0) + s.count;
                }
            }
            foreach (var kv in packItemDemand)
            {
                // Weapons/shields: ComputeAvailability already accounts for the carrier itself (it doesn't count
                // carried items as stock and drops their demand instead) - an additional CarriedStockCount there
                // would be a double credit. Everything else explicitly needs the third bucket.
                // + GroundStockCount: this bucket was missing here, while it has long counted for ammo and
                // weapons. Only in the non-weapon branch - ComputeAvailability reads the ground itself anyway.
                int avail = kv.Key.IsWeapon || LoadoutUtility.IsShieldApparel(kv.Key)
                    ? Patch_LoadoutTooltip.ComputeAvailability(kv.Key, out _, out _, out _)
                    // StoredStockCount instead of IngredientStockCount - see there: whatever RimWorld doesn't treat
                    // as a raw resource counted as not present at all while lying in storage.
                    : LoadoutUtility.StoredStockCount(kv.Key) + LoadoutUtility.GroundStockCount(kv.Key) + LoadoutUtility.CarriedStockCount(kv.Key);
                int missing = kv.Value - avail;
                if (missing > 0) planWidePackItemMissing[kv.Key] = missing;
            }
            foreach (var kv in packGenericDemand)
            {
                int missing = kv.Value - LoadoutUtility.GenericStockCount(kv.Key) - LoadoutUtility.CarriedStockCount(kv.Key);
                if (missing > 0) planWidePackGenericMissing[kv.Key] = missing;
            }
        }

        // Manual pin/paste for just the pack or sidearm portion of ONE pawn - patches that pawn's own
        // AssignPlanEntry directly instead of setting assignDirty, which would trigger a FULL RecomputeAssign()
        // over EVERY pawn on the next frame. That used to visibly reshuffle OTHER, untouched rows' own packs/
        // sidearms right after a completely unrelated paste - the loadout reappeared in a different
        // place) - the pinned dictionaries (pinnedPacks/pinnedSidearms) already make the solver
        // respect this pin on the NEXT real recompute (a slider tweak or preset button), so no immediate
        // full re-solve is needed just to reflect a single pawn's override. Looked up by pawn identity, not
        // by a captured AssignPlanEntry reference - a real recompute REPLACES every entry wholesale, so any
        // closure holding onto the old entry object would silently go stale (same caution the pack/sidearm
        // picker's own pending-pin read already takes).
        private void ApplyPinnedPacks(Pawn pawn, List<Loadout> updated)
        {
            pinnedPacks[pawn] = updated;
            AssignPlanEntry entry = assignPlan?.entries.FirstOrDefault(x => x.pawn == pawn);
            if (entry == null) return;
            entry.plannedPacks = updated;
            // Set explicitly by the player - even if he deselected EVERYTHING. Exactly this case differs from
            // an empty solver result, see AssignPlanEntry.packPinned.
            entry.packPinned = true;
            AdoptExtrasPin(entry);
        }

        private void ApplyPinnedSidearms(Pawn pawn, List<Loadout> updated)
        {
            // Anyone not allowed to fight gets no sidearm pinned here either (pasting a sidearm onto a pacifist
            // reported "Pasted..." and raised the change counter, while nothing appeared in her cell).
            // The cell itself has long blocked this case, and the live tab checks it on paste via
            // PasteSidearmsGuarded - only this preview variant didn't. So the pin landed in the plan, the row
            // never showed it, and on Apply ApplyPreviewPlan would have discarded it: a success message for
            // something that never happened. The same message as in the live tab, so the reason reads the same
            // in both places.
            if (updated != null && updated.Count > 0 && LoadoutUtility.CannotFight(pawn))
            {
                Messages.Message("CER_AssignLoadouts_CannotFightMsg".Translate(pawn.LabelShortCap), MessageTypeDefOf.RejectInput, false);
                return;
            }
            pinnedSidearms[pawn] = updated;
            AssignPlanEntry entry = assignPlan?.entries.FirstOrDefault(x => x.pawn == pawn);
            if (entry == null) return;
            entry.plannedSidearms = updated;
            // A pin also replaces the solver's virtual picks (lazy creation) - otherwise Apply would materialize
            // the old automatic choice in addition to the player's selection.
            entry.plannedSidearmDefs = null;
            // As with packs: an EMPTY set is also an explicit player decision here.
            entry.sidearmPinned = true;
            AdoptExtrasPin(entry);
        }

        // The half the in-place patch above used to leave behind (2026-08-12 fix: ticking a
        // sidearm for an otherwise-kept colonist left the Apply button at "(nothing to apply)", then applied
        // nothing at all and emptied the cell again). `kept` on this row was decided by the LAST full solve -
        // before this pin existed - and a kept row is skipped wholesale by both the change count and the apply
        // loop, pin included. AssignPlanner.Compute already excludes a pack/sidearm pin from `kept` when it
        // solves WITH the pin present, so a pin followed by any slider tweak worked while the same pin on its
        // own did not; this is just the in-place path converging on that same state.
        // cannotFight rows included: a pacifist is offered the Pack cell on purpose (it is gated on
        // CannotCarryAnything, not on CannotFight), and both halves that used to drop their pin - Compute's
        // own pacifist branch and Apply's "no planned loadout, nothing to do" branch - now handle it.
        // No longer static: this is the second and last place a plan changes without the solver running - so
        // the counter for the cost bar belongs here and not at the two call sites (a forgotten call site would
        // be exactly the silently wrong number this is about).
        private void AdoptExtrasPin(AssignPlanEntry entry)
        {
            AssignPlanner.RefreshExtrasChanged(entry);
            if (entry.extrasChanged) entry.kept = false;
            // REWEIGH: the row's resupply icons read a freshly pinned pack / sidearm immediately, while the
            // mass/bulk bars next to them kept the state of the last solve - previewMass was only set there. The
            // same calculation as in the solve, just for the one patched row (a full RecomputeAssign would
            // reshuffle other rows, see the comment on assignPlanVersion).
            AssignPlanner.ReweighEntry(entry);
            // And the summary box along with it (instead of the previously documented limitation): after
            // reweighing all numbers are on the entries - the overload line is replaced in place, nothing stays
            // stale until the next solve.
            AssignPlanner.RefreshOverloadProblem(assignPlan);
            assignPlanVersion++;
        }

        // Every requirement a planned loadout actually needs (main weapon, sidearm, grenades, shield, ammo) with
        // its own Ready/InProduction/Missing status - the Supply badge used to only ever check the main weapon,
        // which silently ignored everything else the loadout carries. A virtual pick (no Loadout object yet) can
        // only be checked for its main weapon, since nothing else about it is decided until it is created.
        private List<SupplyReqItem> SupplyRequirementsFor(AssignPlanEntry e)
        {
            if (e.planned == null)
            {
                var single = new List<SupplyReqItem>();
                if (e.plannedMain != null)
                    single.Add(new SupplyReqItem
                    {
                        icon = e.plannedMain,
                        label = e.plannedMain.LabelCap.ToString(),
                        category = ReqCategory.MainWeapon,
                        status = !e.needsCraft ? ReqStatus.Ready
                            : (LoadoutUtility.WeaponHasActiveBill(e.plannedMain) ? ReqStatus.InProduction : ReqStatus.Missing)
                    });
                return single;
            }
            if (supplyReqCache.TryGetValue((e.planned, e.pawn), out List<SupplyReqItem> cached)) return cached;

            Loadout lo = e.planned;
            List<SupplyReqItem> items = new List<SupplyReqItem>();

            ReqStatus WeaponStatus(ThingDef w)
            {
                // Check the target pawn's own gear first - e.pawn already equipping/carrying/wearing w means
                // nothing needs to move or be crafted for them, regardless of colony-wide stock.
                if (LoadoutUtility.PawnAlreadyHasItem(e.pawn, w)) return ReqStatus.Ready;
                // Plan-WIDE shortfall (2026-07-31 fix), not a per-row "does at least one exist anywhere" check -
                // see BuildPlanWideShortfalls' own comment. Several pawns in the same preview wanting the same
                // scarce sidearm/grenade/shield used to each independently pass a bare existence check and all
                // show Ready even when the colony doesn't own enough for all of them together.
                Dictionary<ThingDef, int> missingDict = LoadoutUtility.IsShieldApparel(w) ? planWideShieldMissing : planWideSecMissing;
                if (!missingDict.TryGetValue(w, out int missing) || missing <= 0) return ReqStatus.Ready;
                return LoadoutUtility.WeaponHasActiveBill(w) ? ReqStatus.InProduction : ReqStatus.Missing;
            }

            LoadoutUtility.LoadoutMainAndSide(lo, out ThingDef mainW, out ThingDef sideW);

            // Main weapon: reuse the solver's own reservation-aware verdict (e.needsCraft) so this can never
            // disagree with what Apply/Supply actually does with it.
            if (mainW != null)
                items.Add(new SupplyReqItem
                {
                    icon = mainW,
                    label = mainW.LabelCap.ToString(),
                    category = ReqCategory.MainWeapon,
                    status = !e.needsCraft ? ReqStatus.Ready
                        : (LoadoutUtility.WeaponHasActiveBill(mainW) ? ReqStatus.InProduction : ReqStatus.Missing)
                });
            if (sideW != null)
                items.Add(new SupplyReqItem { icon = sideW, label = sideW.LabelCap.ToString(), category = ReqCategory.Sidearm, status = WeaponStatus(sideW) });

            // Grenades / thrown explosives are their own line - LoadoutMainAndSide deliberately excludes them from
            // main/side, but the Supply popup's "Sidearms & grenades" section covers them too.
            foreach (ThingDef g in lo.Slots.Select(s => s.thingDef)
                .Where(w => w != null && w.IsWeapon && LoadoutUtility.IsThrownExplosive(w)).Distinct())
                items.Add(new SupplyReqItem { icon = g, label = g.LabelCap.ToString(), category = ReqCategory.Grenade, status = WeaponStatus(g) });

            ThingDef shield = lo.Slots.Select(s => s.thingDef).FirstOrDefault(w => w != null && LoadoutUtility.IsShieldApparel(w));
            if (shield != null)
                items.Add(new SupplyReqItem { icon = shield, label = shield.LabelCap.ToString(), category = ReqCategory.Shield, status = WeaponStatus(shield) });

            // Ammo: overrideAmmo (default on, 2026-08-01) re-picks fresh via ChooseAmmoPicks - the same mech-
            // slider-aware logic Generate already uses - instead of trusting whatever's frozen in the shared
            // Loadout template. BuildPlanWideShortfalls' own ammo demand pass mirrors this same branch, so the
            // "missing" lookup below always matches what was actually counted plan-wide. Off falls back to the
            // template's own slots unchanged (old behavior): concrete slots directly, generic placeholders
            // resolved to the same default pick the Supply popup would auto-resolve to. "Ready" means the FULL
            // required count is already in storage right now - partial coverage shows as Missing/InProduction,
            // same as everywhere else in this mod.
            // Roles are excluded (2026-08-01) - fixed by design, same as the packs/shields/
            // explosives sliders already skip them (RoleUtility.IsRoleLoadout gates those elsewhere too). A
            // Role pawn has no personal ammo anchor to write an override onto anyway (GetAmmoLeaf returns null
            // for them) - their ammo always comes straight from the Role's own frozen Slots regardless.
            IEnumerable<(ThingDef def, int count)> ammoPicks;
            if (overrideAmmo && mainW != null && !LoadoutUtility.IsShieldApparel(mainW) && !RoleUtility.IsRoleLoadout(lo))
                ammoPicks = ChooseAmmoPicks(mainW).Where(p => p.def is ThingDef).Select(p => ((ThingDef)p.def, p.count));
            else
                ammoPicks = lo.Slots.Select(s =>
                {
                    // IsAmmoDef instead of "everything that is neither a weapon nor a shield".
                    // The old exclusion was too coarse: a ROLE bakes its pack contents flat into the same slots,
                    // so medicine, rations and drugs landed in the ammo group. The visible result was the line
                    // "- Medicine: ready (no bench built)" under the heading "Ammo:" - ready and no workbench in one
                    // breath, because the suffix comes from the ammo resupply rule (AmmoUnsustainable), which was
                    // never meant for medicine. Pack contents now sit in the pack group, where they belong
                    // (PackRequirementsFor).
                    if (s.thingDef != null && LoadoutUtility.IsAmmoDef(s.thingDef))
                        return (s.thingDef, s.count);
                    if (s.genericDef != null && s.genericDef.defName != null && s.genericDef.defName.StartsWith(LoadoutUtility.GenericAmmoPrefix))
                    {
                        ThingDef gun = LoadoutUtility.GetWeaponForGenericAmmo(s.genericDef);
                        List<Def> family = LoadoutUtility.GetAmmoTypesFor(gun).Where(LoadoutUtility.IsAmmoResearched).Cast<Def>().ToList();
                        return (LoadoutUtility.PickDefaultAmmo(family) as ThingDef, s.count);
                    }
                    return ((ThingDef)null, s.count);
                });
            foreach (var (ammoDef, count) in ammoPicks)
            {
                if (ammoDef == null || count <= 0 || items.Any(it => it.icon == ammoDef)) continue;

                // Plan-wide shortfall (2026-07-31 fix) - see BuildPlanWideShortfalls. If several colonists'
                // loadouts all want the same caliber, a per-row stock check let every one of their rows read
                // "ready" off the SAME pool of rounds even when there is nowhere near enough for all of them.
                int missing = planWideAmmoMissing.TryGetValue(ammoDef, out int m) ? m : 0;
                ReqStatus st = missing <= 0 ? ReqStatus.Ready
                    : (LoadoutUtility.WeaponHasActiveBill(ammoDef) ? ReqStatus.InProduction : ReqStatus.Missing);
                items.Add(new SupplyReqItem { icon = ammoDef, label = ammoDef.LabelCap.ToString(), category = ReqCategory.Ammo, status = st, neededCount = Mathf.Max(1, missing) });
            }

            // The weapon needs ammo, but NONE of it is researched -> the cell used to stay completely empty, and
            // empty reads as "all fine". Measured with diagnostic logging: the R-6 charge rifle has FOUR ammo
            // types in CE, and our research check discards all four. The Weapon Loadouts list has long flagged
            // exactly this case; it was missing here. One entry with status Missing instead of nothing at all.
            if (mainW != null && !mainW.IsMeleeWeapon && !items.Any(it => it.category == ReqCategory.Ammo))
            {
                List<Def> anyAmmo = LoadoutUtility.GetLoadoutAmmoDefs(mainW, LoadoutUtility.IsSelfAmmoWeapon(mainW));
                if (anyAmmo != null && anyAmmo.Count > 0
                    && !anyAmmo.Any(a => Dialog_GenerateLoadouts.IsResearched(a)))
                {
                    ThingDef firstAmmo = anyAmmo.FirstOrDefault(a => a is ThingDef) as ThingDef;
                    items.Add(new SupplyReqItem
                    {
                        icon = firstAmmo,
                        label = "CER_SquadSetup_AmmoBlockedByResearch".Translate(mainW.LabelCap),
                        category = ReqCategory.Ammo,
                        status = ReqStatus.Missing,
                        neededCount = 1
                    });
                }
            }

            supplyReqCache[(lo, e.pawn)] = items;
            return items;
        }

        // For a Role-assigned pawn (no separate pinnedPacks/plannedSidearms - a Role has no pinning concept,
        // everything it provides is baked into its own flat Slots union with no preserved sub-categorization
        // of where each slot came from), extracts which of the Role's own slots read as "sidearm-shaped" vs
        // "pack-shaped" for DISPLAY purposes only - purely by CONTENT (never by name/origin), the same
        // content-based principle already used everywhere else in this codebase. Mirrors the same
        // main/side/grenade/shield/ammo split SupplyRequirementsFor already does for its own purposes.
        internal static void ExtractRoleDisplayContent(Loadout role, out List<ThingDef> sidearmIcons, out List<Def> packIcons)
        {
            sidearmIcons = new List<ThingDef>();
            packIcons = new List<Def>();
            if (role?.Slots == null) return;

            LoadoutUtility.LoadoutMainAndSide(role, out ThingDef mainW, out ThingDef sideW);
            if (sideW != null) sidearmIcons.Add(sideW);
            foreach (ThingDef g in role.Slots.Select(s => s.thingDef)
                .Where(w => w != null && w != mainW && w != sideW && w.IsWeapon && LoadoutUtility.IsThrownExplosive(w)).Distinct())
                sidearmIcons.Add(g);
            ThingDef shield = role.Slots.Select(s => s.thingDef).FirstOrDefault(w => w != null && LoadoutUtility.IsShieldApparel(w));
            if (shield != null && !sidearmIcons.Contains(shield)) sidearmIcons.Add(shield);

            foreach (LoadoutSlot s in role.Slots)
            {
                if (s.thingDef != null)
                {
                    if (s.thingDef == mainW || sidearmIcons.Contains(s.thingDef)) continue;
                    if (s.thingDef.IsWeapon || LoadoutUtility.IsAmmoDef(s.thingDef)) continue;
                    if (!packIcons.Contains(s.thingDef)) packIcons.Add(s.thingDef);
                }
                else if (s.genericDef != null)
                {
                    if (s.genericDef.defName != null && s.genericDef.defName.StartsWith(LoadoutUtility.GenericAmmoPrefix)) continue;
                    if (!packIcons.Contains(s.genericDef)) packIcons.Add(s.genericDef);
                }
            }
        }

        // Every planned sidearm's own requirement (main weapon + its concrete ammo) - lives on the pawn's own
        // personal anchor, never on the shared template (e.planned), so it can't reuse SupplyRequirementsFor's
        // per-TEMPLATE cache: two pawns sharing one main loadout can carry DIFFERENT sidearms. Concrete ammo
        // only (no generic-placeholder resolution) - a reasonable simplification for what auto-assign itself
        // ever picks; a hand-built sidearm loadout using generics just won't show an ammo line here.
        private List<SupplyReqItem> SidearmRequirementsFor(AssignPlanEntry e)
        {
            if (ExtraReqCached(sidearmReqCache, e, out List<SupplyReqItem> hit)) return hit;
            List<SupplyReqItem> items = ComputeSidearmRequirementsFor(e);
            if (e?.pawn != null) sidearmReqCache[e.pawn] = items;
            return items;
        }

        // The shared gatekeeper of both caches. Discards EVERYTHING as soon as the plan has changed or the half
        // second is up - see the fields above for the why. Deliberately not stamped per entry: both calculations
        // read the same plan, so a shared stamp can't drift apart.
        private bool ExtraReqCached(Dictionary<Pawn, List<SupplyReqItem>> cache, AssignPlanEntry e, out List<SupplyReqItem> hit)
        {
            hit = null;
            if (extraReqCacheVersion != assignPlanVersion || Time.realtimeSinceStartup - extraReqCacheTime >= 0.5f)
            {
                sidearmReqCache.Clear();
                packReqCache.Clear();
                extraReqCacheVersion = assignPlanVersion;
                extraReqCacheTime = Time.realtimeSinceStartup;
                return false;
            }
            return e?.pawn != null && cache.TryGetValue(e.pawn, out hit);
        }

        private List<SupplyReqItem> ComputeSidearmRequirementsFor(AssignPlanEntry e)
        {
            var items = new List<SupplyReqItem>();
            bool hasDefs = e.plannedSidearmDefs != null && e.plannedSidearmDefs.Count > 0;
            if ((e.plannedSidearms == null || e.plannedSidearms.Count == 0) && !hasDefs) return items;
            // Virtual picks (lazy creation): the same rows as for a real template, just computed from the defaults
            // the template is created from on Apply.
            if (hasDefs)
                foreach (ThingDef sd in e.plannedSidearmDefs)
                {
                    ReqStatus vst;
                    if (LoadoutUtility.PawnAlreadyHasItem(e.pawn, sd)) vst = ReqStatus.Ready;
                    else
                    {
                        int vMissing = planWideSecMissing.TryGetValue(sd, out int vm) ? vm : 0;
                        vst = vMissing <= 0 ? ReqStatus.Ready : (LoadoutUtility.WeaponHasActiveBill(sd) ? ReqStatus.InProduction : ReqStatus.Missing);
                    }
                    items.Add(new SupplyReqItem { icon = sd, label = sd.LabelCap.ToString(), category = ReqCategory.Sidearm, status = vst });
                    foreach (LoadoutSlot s in Dialog_GenerateLoadouts.DefaultSlotsFor(sd, Dialog_GenerateLoadouts.BuildDefaultSettings(sd)))
                    {
                        ThingDef ammoDef = s.thingDef != null && LoadoutUtility.IsAmmoDef(s.thingDef) ? s.thingDef : null;
                        if (ammoDef == null || s.count <= 0 || items.Any(it => it.icon == ammoDef)) continue;
                        int missing = planWideAmmoMissing.TryGetValue(ammoDef, out int m2) ? m2 : 0;
                        ReqStatus ast = missing <= 0 ? ReqStatus.Ready : (LoadoutUtility.WeaponHasActiveBill(ammoDef) ? ReqStatus.InProduction : ReqStatus.Missing);
                        items.Add(new SupplyReqItem { icon = ammoDef, label = ammoDef.LabelCap.ToString(), category = ReqCategory.Ammo, status = ast, neededCount = Mathf.Max(1, missing) });
                    }
                }
            foreach (Loadout lo in e.plannedSidearms ?? new List<Loadout>())
            {
                if (lo == null) continue;
                // NOT AnalyzeLoadout - it deliberately never lets a thrown explosive count as a loadout's
                // "main weapon" (a satchel charge's raw blast score would otherwise outscore an actual gun
                // for the AUTO-SOLVER's own picks), which would silently drop a grenade-only sidearm's own
                // requirement line here. A manually pinned grenade sidearm is a perfectly normal choice
                // (see SidearmAssignmentUtility.CandidateLoadouts) - any real weapon-type slot counts as
                // this sidearm's "main".
                ThingDef mainW = lo.Slots?.Select(s => s.thingDef).FirstOrDefault(w => w != null && w.IsWeapon);
                if (mainW != null)
                {
                    ReqStatus st;
                    if (LoadoutUtility.PawnAlreadyHasItem(e.pawn, mainW)) st = ReqStatus.Ready;
                    else
                    {
                        // Plan-wide shortfall (2026-07-31 fix) - see BuildPlanWideShortfalls' own comment.
                        Dictionary<ThingDef, int> missingDict = LoadoutUtility.IsShieldApparel(mainW) ? planWideShieldMissing : planWideSecMissing;
                        int missing = missingDict.TryGetValue(mainW, out int m) ? m : 0;
                        st = missing <= 0 ? ReqStatus.Ready : (LoadoutUtility.WeaponHasActiveBill(mainW) ? ReqStatus.InProduction : ReqStatus.Missing);
                    }
                    items.Add(new SupplyReqItem { icon = mainW, label = mainW.LabelCap.ToString(), category = ReqCategory.Sidearm, status = st });
                }
                foreach (LoadoutSlot s in lo.Slots ?? Enumerable.Empty<LoadoutSlot>())
                {
                    // IsAmmoDef, second occurrence of the same pattern as in SupplyRequirementsFor: "not a weapon
                    // and not a shield" is no proof of "ammo" - a hand-built sidearm template may just as well
                    // contain food, and that would have got the same wrong ammo label here.
                    ThingDef ammoDef = s.thingDef != null && LoadoutUtility.IsAmmoDef(s.thingDef) ? s.thingDef : null;
                    if (ammoDef == null || s.count <= 0 || items.Any(it => it.icon == ammoDef)) continue;
                    // Plan-wide shortfall (2026-07-31 fix) - see BuildPlanWideShortfalls.
                    int missing = planWideAmmoMissing.TryGetValue(ammoDef, out int m2) ? m2 : 0;
                    ReqStatus ast = missing <= 0 ? ReqStatus.Ready : (LoadoutUtility.WeaponHasActiveBill(ammoDef) ? ReqStatus.InProduction : ReqStatus.Missing);
                    items.Add(new SupplyReqItem { icon = ammoDef, label = ammoDef.LabelCap.ToString(), category = ReqCategory.Ammo, status = ast, neededCount = Mathf.Max(1, missing) });
                }
            }
            return items;
        }

        // Manufacturing feasibility for the SET of Equipment Packs a pawn will carry - same Ready/
        // InProduction/Missing verdicts as the weapon Supply badge above, but scoped to what the packs
        // themselves hold (medicine/food/drugs/utility), independent of the main weapon's own Main/
        // Extras/Ammo columns (those never see pack content - packs live on the pawn's own hidden
        // personal anchor, not on e.planned).
        private List<SupplyReqItem> PackRequirementsFor(AssignPlanEntry e)
        {
            if (ExtraReqCached(packReqCache, e, out List<SupplyReqItem> hit)) return hit;
            List<SupplyReqItem> items = ComputePackRequirementsFor(e);
            if (e?.pawn != null) packReqCache[e.pawn] = items;
            return items;
        }

        private List<SupplyReqItem> ComputePackRequirementsFor(AssignPlanEntry e)
        {
            var items = new List<SupplyReqItem>();
            // Role content counts too - a role doesn't attach pack OBJECTS, it bakes its contents flat into its own
            // loadout (AmmoAssignmentUtility.RoleSupplySlots). Without this the list was empty for every role
            // colonist, and his medicine/rations showed up in the ammo group instead, see SupplyRequirementsFor.
            var packSlots = new List<LoadoutSlot>();
            foreach (Loadout pack in e.plannedPacks ?? new List<Loadout>())
                if (pack?.OwnSlots != null) packSlots.AddRange(pack.OwnSlots);
            packSlots.AddRange(AmmoAssignmentUtility.RoleSupplySlots(e.planned));
            if (packSlots.Count == 0) return items;
            foreach (LoadoutSlot s in packSlots)
            {
                ThingDef def = s.thingDef ?? LoadoutUtility.RepresentativeIconDef(s.genericDef);
                if (def == null || items.Any(it => it.icon == def)) continue;

                ReqStatus status;
                int missing;
                if (def.IsWeapon || LoadoutUtility.IsShieldApparel(def))
                {
                    if (LoadoutUtility.PawnAlreadyHasItem(e.pawn, def)) { status = ReqStatus.Ready; missing = 0; }
                    else
                    {
                        // Plan-wide shortfall (2026-07-31 fix) - see BuildPlanWideShortfalls.
                        missing = planWidePackItemMissing.TryGetValue(def, out int m) ? m : 0;
                        status = missing <= 0 ? ReqStatus.Ready : (LoadoutUtility.WeaponHasActiveBill(def) ? ReqStatus.InProduction : ReqStatus.Missing);
                    }
                }
                else
                {
                    // A generic ("Any Medicine", ...) has no stock of its own - BuildPlanWideShortfalls already
                    // summed every concrete def its matcher accepts across the WHOLE plan, not just this row.
                    missing = s.genericDef != null
                        ? (planWidePackGenericMissing.TryGetValue(s.genericDef, out int mg) ? mg : 0)
                        : (planWidePackItemMissing.TryGetValue(def, out int md) ? md : 0);
                    status = missing <= 0 ? ReqStatus.Ready
                        : (LoadoutUtility.WeaponHasActiveBill(def) ? ReqStatus.InProduction : ReqStatus.Missing);
                }
                // Label the GENERIC itself ("Any Perishable Meal"), not the arbitrary representative
                // concrete def picked only for the icon - the readiness check above already sums the
                // WHOLE family (GenericStockCount), so showing one random family member's name here
                // ("Nutrient paste meal") read as if that ONE specific item was what got checked,
                // confusing when the colony's actual stock is a completely different qualifying meal.
                string label = s.genericDef != null ? s.genericDef.LabelCap.ToString() : def.LabelCap.ToString();
                // category was never set here and thus silently MainWeapon (the enum default) - see the case
                // distinctions in DrawSupplyReqGroup, which all rely on the category.
                items.Add(new SupplyReqItem { icon = def, label = label, category = ReqCategory.Pack, status = status, neededCount = Mathf.Max(1, missing) });
            }
            return items;
        }

        // ================================================================================================
        // Presets & "Use recommended setup" (plan 5.1, 5.2/7, 6a-6e)
        // ================================================================================================

        // Resets every slider to the settings-derived default AND hands Pool/Mech back to the intelligent dynamic
        // core (their manual-override latch resets, so they resume tracking colony state on the next recompute).
        private void UseRecommended()
        {
            ApplyRecommendedSliders(RoleUtility.Ui());
            assignDirty = true;
        }

        // ONE source for "default" - opening the window and the "Use recommended setup" button. Fixed sliders
        // from settings.presetBalanced (factory state identical to the former literals 0.3/0.35/0.7/0.8/0.5 and
        // 0.35/0.5 - see CEReloadedSettings.DefaultPresetBalanced), anti-mech and pool dynamic.
        private void ApplyRecommendedSliders(CERUiState ui)
        {
            CEReloadedSettings.PresetTuning def = CEReloadedMod.settings?.presetBalanced ?? CEReloadedSettings.DefaultPresetBalanced();
            ammoVar = def.ammoVar;
            craft = def.craft; melee = def.melee; diversity = def.diversity; budget = def.budget;
            packCoverage = def.packCoverage;
            // These three used to be missing, although both the comment next to it and the tooltip promise "every
            // slider". Failure scenario: pull shields to 1.0 for a melee push, then press "Balanced" - eight
            // sliders jump back, the shields stay at 1.0, and the result is taken for the preset's
            // recommendation.
            // The presets have no values of their own for these three (PresetTuning doesn't know them), so the
            // factory defaults from the field declarations - exactly what "reset" can mean here without guessing.
            sidearmCoverage = 0.2f; grenadeCoverage = 0.2f; shieldCoverage = 0.3f;
            ui.mechManuallySet = false;
            mech = ui.mechAffinity = LoadoutUtility.DynamicMechDefault(ui);
        }

        // A preset is a COMPLETE slider configuration (every slider, Anti-mech included), so two presets never
        // half-mix and each button always lands on the same reproducible state. The values live in the mod settings
        // (Presets tab) with hardcoded factory defaults; "Use recommended setup" stays the dynamic alternative.
        private void ApplyPreset(string name)
        {
            var s = CEReloadedMod.settings;
            if (s == null) return;
            CEReloadedSettings.PresetTuning t;
            switch (name)
            {
                case "Anti-Mech": t = s.presetAntiMech; break;
                case "Economy": t = s.presetEconomy; break;
                case "Best Gear": t = s.presetBestGear; break;
                case "Focus Ranged": t = s.presetRanged; break;
                case "All": t = s.presetAll; break;
                case "Available items": t = s.presetPreferAvailable; break;
                default: t = s.presetBalanced; break;
            }
            if (t == null) return;

            CERUiState ui = RoleUtility.Ui();
            ui.mechManuallySet = true;
            mech = t.mech;
            ammoVar = t.ammoVar;
            craft = t.craft; melee = t.melee; diversity = t.diversity; budget = t.budget;
            packCoverage = t.packCoverage;
            // See UseRecommended: the same three sliders stayed put here too.
            sidearmCoverage = 0.2f; grenadeCoverage = 0.2f; shieldCoverage = 0.3f;
            assignDirty = true;
        }

        // Display-only translation of a preset's internal name (see ApplyPreset's own switch, which stays
        // keyed to these exact English strings so preset matching never changes behavior by active language).
        private static string PresetDisplayLabel(string name)
        {
            switch (name)
            {
                case "Anti-Mech": return "CER_SquadSetup_PresetAntiMech".Translate();
                case "Economy": return "CER_SquadSetup_PresetEconomy".Translate();
                case "Best Gear": return "CER_SquadSetup_PresetBestGear".Translate();
                case "Focus Ranged": return "CER_SquadSetup_PresetFocusRanged".Translate();
                case "All": return "CER_SquadSetup_PresetAll".Translate();
                case "Available items": return "CER_SquadSetup_PresetAvailableItems".Translate();
                default: return "CER_SquadSetup_PresetBalanced".Translate();
            }
        }

        // ================================================================================================
        // UI
        // ================================================================================================

        // ===== Decouple replanning from dragging ===========================================================
        //
        // Widgets.HorizontalSlider returns a new value on every pixel of mouse movement, every new value set
        // assignDirty, and assignDirty triggered a full replan IMMEDIATELY in DoWindowContents - i.e. once per
        // frame while dragging. The replan is the expensive part of the window (AssignPlanner over all
        // colonists x all candidate weapons, including stock reservation, pack and sidearm choice), drawing is
        // not - so "the slider stutters" was never a drawing problem.
        //
        // While the mouse button is held, replan at most every 150 ms. Nothing is lost: assignDirty stays set
        // until a replan really happened, and on release the throttle is gone the next frame - so the slider's
        // final state is always computed.
        private float lastAssignPlanTime = -99f;
        private const float AssignReplanMinInterval = 0.15f;

        private bool MayReplanAssignNow()
        {
            if (!Input.GetMouseButton(0)) return true;
            return Time.realtimeSinceStartup - lastAssignPlanTime >= AssignReplanMinInterval;
        }

        private float DrawSlider(Rect inRect, float y, string left, string right, ref float val, string tip, Action onChanged, bool enabled = true, string disabledTip = null)
        {
            const float lblW = 170f;
            Rect row = new Rect(0, y, inRect.width, 26f);
            if (!enabled) GUI.color = new Color(1f, 1f, 1f, 0.35f);
            Text.Anchor = TextAnchor.MiddleRight;
            Widgets.Label(new Rect(row.x, row.y, lblW, 24f), left);
            Text.Anchor = TextAnchor.MiddleLeft;
            Widgets.Label(new Rect(row.xMax - lblW, row.y, lblW, 24f), right);
            Text.Anchor = TextAnchor.UpperLeft;
            Rect sr = new Rect(row.x + lblW + 10f, row.y + 4f, row.width - 2f * lblW - 20f, 18f);
            if (enabled)
            {
                float nv = Widgets.HorizontalSlider(sr, val, 0f, 1f, true);
                if (!Mathf.Approximately(nv, val)) { val = nv; onChanged?.Invoke(); }
            }
            else
            {
                Widgets.HorizontalSlider(sr, val, 0f, 1f, true);
            }
            GUI.color = Color.white;
            TooltipHandler.TipRegion(row, !enabled && disabledTip != null ? disabledTip : tip);
            return y + 28f;
        }

        public override void DoWindowContents(Rect inRect)
        {
            CERUiState ui = RoleUtility.Ui();
            string title = "CER_SquadSetup_TitleAssign".Translate();
            Text.Font = GameFont.Medium;
            Widgets.Label(new Rect(0, 0, 400f, 30f), title);
            Text.Font = GameFont.Small;
            GUI.color = new Color(1f, 1f, 1f, 0.6f);
            Widgets.Label(new Rect(0, 28f, inRect.width, 20f), "CER_SquadSetup_LivePreviewNote".Translate());
            GUI.color = Color.white;

            float y = 54f;

            // "Use recommended setup" + presets: always visible, the one-click path (plan 5.1/5.2).
            float recW = 220f;
            if (LoadoutUtility.ButtonTextFit(new Rect(0, y, recW, 26f), "CER_SquadSetup_UseRecommendedSetup".Translate().ToString()))
                UseRecommended();
            TooltipHandler.TipRegion(new Rect(0, y, recW, 26f), "CER_SquadSetup_UseRecommendedSetupTip".Translate());

            float px = recW + 14f;
            // "All" only differed in the Generate sliders (pool/count) and went away with them.
            List<string> presetNames = new List<string> { "Balanced", "Anti-Mech", "Economy", "Best Gear", "Focus Ranged", "Available items" };
            foreach (string presetName in presetNames)
            {
                // presetName itself stays the untranslated internal key (also used by ApplyPreset's own switch
                // below) - only the drawn label is translated, so preset matching keeps working identically
                // regardless of the active language.
                string presetLabel = PresetDisplayLabel(presetName);
                float pw = Text.CalcSize(presetLabel).x + 20f;
                if (Widgets.ButtonText(new Rect(px, y, pw, 26f), presetLabel)) ApplyPreset(presetName);
                px += pw + 6f;
            }

            // The same Simple/Detailed icon the tabs use - Simple shows just the presets, Detailed unfolds every slider.
            LoadoutUtility.ModeDetailToggle(new Rect(inRect.width - LoadoutUtility.ModeDetailToggleW, y - 2f, LoadoutUtility.ModeDetailToggleW, LoadoutUtility.ModeDetailToggleW),
                ref advancedOpen,
                "CER_SquadSetup_DetailedModeTip".Translate(),
                "CER_SquadSetup_SimpleModeTip".Translate());
            y += 34f;

            if (advancedOpen)
            {
                y = DrawAssignSliders(inRect, y);
                y += 4f;
            }

            // Diff-only toggle (plan 5.9) - one control, both sections respect it. Rects are text-sized so the
            // check flag hugs its label (same look as the Detailed rows' checkboxes).
            bool nd = diffOnly;
            string showOnlyChangesLabel = "CER_SquadSetup_ShowOnlyChanges".Translate();
            float diffW = Text.CalcSize(showOnlyChangesLabel).x + 26f;
            Widgets.CheckboxLabeled(new Rect(0, y, diffW, 24f), showOnlyChangesLabel, ref nd);
            if (nd != diffOnly) diffOnly = nd;
            float xCursor = diffW + 34f;

            // "Keep current loadouts" decides who is even in play - that is a headline decision, not slider
            // fine-tuning, so it lives here in plain sight instead of behind the Detailed toggle. Standalone
            // only (onBack == null, the same "are we in the Arm-my-Colony chain" signal Skip buttons already
            // use elsewhere) - inside that chain this is already fully decided by "Reset first"
            // (ArmAutoAssignAndProduce passes presetKeepAssigned: !aggressive), so an editable checkbox here
            // would just duplicate a choice already made one screen earlier, or risk silently contradicting
            // it (2026-07-30, replaces an earlier "just remove it entirely" idea that would have broken the
            // standalone case, where this toggle is the ONLY way to choose full replan vs. fill-gaps).
            if (onBack == null)
            {
                string keepCurrentLabel = "CER_SquadSetup_KeepCurrentLoadouts".Translate();
                float kaW = Text.CalcSize(keepCurrentLabel).x + 26f;
                Rect kaRect = new Rect(xCursor, y, kaW, 24f);
                bool ka = keepAssigned;
                Widgets.CheckboxLabeled(kaRect, keepCurrentLabel, ref ka);
                TooltipHandler.TipRegion(kaRect, "CER_SquadSetup_KeepCurrentLoadoutsTip".Translate());
                if (ka != keepAssigned)
                {
                    keepAssigned = ka;
                    // `locked` means "differs from the CURRENT global default" - flipping the default without
                    // clearing it would silently invert every existing per-pawn override (a pawn explicitly
                    // locked under the old mode would suddenly read as explicitly released under the new one).
                    locked.Clear();
                    assignDirty = true;
                }
                xCursor = kaRect.xMax + 34f;
            }

            // The role toggle sits here in plain sight instead of in the collapsed detail row (a fundamental
            // decision belongs where it is visible, same reasoning as "Keep current loadouts" above). It stays
            // visible inside the Arm-my-Colony chain too - its default (CERUiState.asRoleMode = Mixed) reads
            // correctly on its own, and ArmAutoAssignAndProduce sets it via roleModeOverride to whatever the
            // wizard's root question chose. So the button shows what was last set - no separate default logic
            // is needed here.
            // Three values instead of two (RoleAssignMode): the third, "No roles", is the one that makes "Ad-Hoc
            // Loadouts per Pawn" actually do what its tooltip says.
            // A selector button instead of a checkbox: the toggle has three values, and it now has the same name
            // here as in the Assign tab - both via LoadoutUtility.RoleModeControl, so the two screens don't
            // drift apart again.
            // Width MEASURED instead of a fixed 170 - see RoleModeControlWidth. Capped at the top so a very long
            // translation doesn't blow up the row; then the Tiny fallback in ButtonTextFit kicks in again.
            float urW = Mathf.Clamp(LoadoutUtility.RoleModeControlWidth(assignOwner.roleMode), 170f, 320f);
            Rect urRect = new Rect(xCursor, y, urW, 24f);
            LoadoutUtility.RoleModeControl(urRect, assignOwner.roleMode, m =>
            {
                if (m == assignOwner.roleMode) return;
                assignOwner.roleMode = m;
                assignDirty = true;
            });
            xCursor = urRect.xMax + 34f;

            // "Override ammo" (2026-08-01, on by default): re-picks each pawn's ammo fresh at
            // assign time (current stock/research + the mech slider, via the SAME ChooseAmmoPicks logic Generate
            // already uses) instead of trusting whatever's frozen in the shared Loadout template. The template's
            // OWN ammo stays exactly as generated when this is off - matches the old behavior 1:1.
            string overrideAmmoLabel = "CER_SquadSetup_OverrideAmmo".Translate();
            float oaW = Text.CalcSize(overrideAmmoLabel).x + 26f;
            Rect oaRect = new Rect(xCursor, y, oaW, 24f);
            bool oa = overrideAmmo;
            Widgets.CheckboxLabeled(oaRect, overrideAmmoLabel, ref oa);
            TooltipHandler.TipRegion(oaRect, "CER_SquadSetup_OverrideAmmoTip".Translate());
            if (oa != overrideAmmo) { overrideAmmo = oa; assignDirty = true; }
            xCursor = oaRect.xMax + 34f;

            // The "Respect max bulk" checkbox was removed (it must be possible to assign ANYTHING, the player makes
            // sure it is sensible). Carry capacity is thus once again purely a WARNING after the selection - see
            // AssignPlanner, overload message.
            y += 28f;

            y = LoadoutUtility.Divider(y, inRect.width);

            // ONE decision per frame for both halves, not two: the anti-flesh/anti-mech slider sets BOTH dirty
            // flags (it is shared in the combined window), so the Generate replan ran every frame while dragging
            // too - same cause, second occurrence. If each half set the timestamp itself, they would lock each
            // other out and the two previews would alternately show different slider states.
            bool mayReplan = MayReplanAssignNow();
            bool didReplan = false;
            if (assignDirty && mayReplan)
            {
                RecomputeAssign();
                didReplan = true;
            }
            if (didReplan) lastAssignPlanTime = Time.realtimeSinceStartup;

            float bottomBarTop = inRect.height - LoadoutUtility.BottomBarH - 8f;
            DrawAssignList(inRect, y, bottomBarTop - y);

            // --- Bottom bar ---
            // In the Arm-my-Colony chain a Back button leads to the previous step; navigation, not undo - anything
            // an earlier step already applied stays applied.
            // ONE font size for the WHOLE footer. Previously only the two right buttons (Apply/Skip) went through
            // BottomBarFont, and the four left ones each measured on their own - the same half row that showed
            // up in the bill overview as "big on the left, small on the right". So ALL cells actually drawn in
            // this configuration are registered here first.
            string backLbl = "CER_SquadSetup_BackButton".Translate().ToString();
            string cancelLbl = "CER_SquadSetup_CancelButton".Translate().ToString();
            string unpinLbl = pinned.Count > 0
                ? "CER_SquadSetup_UnpinAllButton".Translate(pinned.Count.ToString()).ToString()
                : null;
            string applyLbl = ApplyLabel();
            string skipLbl = "CER_SquadSetup_SkipButton".Translate().ToString();
            var barCells = new List<(float width, string label, bool icon)>();
            if (onBack != null) { barCells.Add((100f, backLbl, false)); barCells.Add((LoadoutUtility.CloseBtnW, cancelLbl, false)); }
            else barCells.Add((LoadoutUtility.CloseBtnW, cancelLbl, false));
            if (unpinLbl != null) barCells.Add((130f, unpinLbl, false));
            barCells.Add((LoadoutUtility.PrimaryBtnW, applyLbl, false));
            if (onBack != null) barCells.Add((LoadoutUtility.SkipBtnW, skipLbl, false));
            GameFont prevBarFont = Text.Font;
            Text.Font = LoadoutUtility.BottomBarFont(barCells.ToArray());

            float cancelEndX;
            if (onBack != null)
            {
                Rect backR = new Rect(0f, inRect.height - LoadoutUtility.BottomBarH, 100f, LoadoutUtility.BottomBtnH);
                if (LoadoutUtility.ButtonTextFit(backR, backLbl))
                {
                    wentBack = true;
                    Close();
                    onBack();
                }
                TooltipHandler.TipRegion(backR, "CER_SquadSetup_BackButtonTip".Translate());
                Rect cancelR = new Rect(108f, inRect.height - LoadoutUtility.BottomBarH, LoadoutUtility.CloseBtnW, LoadoutUtility.BottomBtnH);
                if (LoadoutUtility.ButtonTextFit(cancelR, cancelLbl)) Close();
                cancelEndX = cancelR.xMax;
            }
            else
            {
                if (LoadoutUtility.BottomCloseButton(inRect, cancelLbl)) Close();
                cancelEndX = LoadoutUtility.CloseBtnW;
            }

            // Only appears once at least one colonist is pinned - a bulk undo for the manual overrides, right next
            // to Cancel since it is itself a "throw away my manual changes" action.
            if (unpinLbl != null)
            {
                Rect unpinR = new Rect(cancelEndX + 10f, inRect.height - LoadoutUtility.BottomBarH, 130f, LoadoutUtility.BottomBtnH);
                if (LoadoutUtility.ButtonTextFit(unpinR, unpinLbl))
                {
                    // A pinned pawn that was ALSO individually locked (e.g. locked first, pinned later) would
                    // otherwise still read as locked afterward - unpinning must hand them fully back to the
                    // solver (yellow lock -> green checkmark), not leave a lock behind.
                    foreach (Pawn p in pinned.Keys) locked.Remove(p);
                    pinned.Clear();
                    assignDirty = true;
                }
                TooltipHandler.TipRegion(unpinR, "CER_SquadSetup_UnpinAllTip".Translate(pinned.Count.ToString()));
            }

            float xr = inRect.width;
            string lbl = applyLbl;
            bool enabled = ApplyEnabled();
            if (LoadoutUtility.BottomActionButton(inRect, ref xr, LoadoutUtility.PrimaryBtnW, lbl, enabled,
                !ApplyHasWork() ? "CER_SquadSetup_NothingToApply".Translate() : null))
            {
                DoApply();
            }
            // Arm-my-Colony chain only (onBack != null, same signal Back/Cancel above already use) - move on
            // to the next step without applying anything from this one, distinct from Cancel (which aborts
            // the whole run) and from the primary action (which commits this step first). Drawn AFTER the
            // primary button so it lands immediately to its LEFT, not its right.
            if (onBack != null && LoadoutUtility.BottomActionButton(inRect, ref xr, LoadoutUtility.SkipBtnW, skipLbl,
                tip: "CER_SquadSetup_SkipButtonTip".Translate()))
            {
                skipped = true;
                Close();
            }
            Text.Font = prevBarFont;
        }

        private float DrawAssignSliders(Rect inRect, float y)
        {
            y = DrawSlider(inRect, y, "CER_SquadSetup_UseAvailableItems".Translate(), "CER_SquadSetup_CraftNewItems".Translate(), ref craft,
                "CER_SquadSetup_CraftSliderTip".Translate(),
                () => assignDirty = true);
            y = DrawSlider(inRect, y, "CER_SquadSetup_AntiFlesh".Translate(), "CER_SquadSetup_AntiMech".Translate(), ref mech,
                "CER_SquadSetup_MechSliderAssignTip".Translate(),
                () => { RoleUtility.Ui().mechManuallySet = true; assignDirty = true; });
            // Moved here from the Generate section: affects every assignment via "Override ammo".
            y = DrawSlider(inRect, y, "CER_SquadSetup_OnlyOneAmmoType".Translate(), "CER_SquadSetup_MaximumFlexibility".Translate(), ref ammoVar,
                "CER_SquadSetup_AmmoVarSliderTip".Translate(), () => assignDirty = true);
            y = DrawSlider(inRect, y, "CER_SquadSetup_PreferRanged".Translate(), "CER_SquadSetup_PreferMelee".Translate(), ref melee,
                "CER_SquadSetup_MeleeSliderTip".Translate(), () => assignDirty = true);
            y = DrawSlider(inRect, y, "CER_SquadSetup_AlwaysUseBest".Translate(), "CER_SquadSetup_CoverDifferentRoles".Translate(), ref diversity,
                "CER_SquadSetup_DiversitySliderTip".Translate(),
                () => assignDirty = true);
            y = DrawSlider(inRect, y, "CER_SquadSetup_CheapWeapons".Translate(), "CER_SquadSetup_BestWeapon".Translate(), ref budget,
                "CER_SquadSetup_BudgetSliderTip".Translate(), () => assignDirty = true);
            y = DrawSlider(inRect, y, "CER_SquadSetup_NoSidearm".Translate(), "CER_SquadSetup_AlwaysSidearm".Translate(), ref sidearmCoverage,
                "CER_SquadSetup_SidearmCoverageSliderTip".Translate(),
                () => assignDirty = true);
            y = DrawSlider(inRect, y, "CER_SquadSetup_NoExplosives".Translate(), "CER_SquadSetup_GenerousExplosives".Translate(), ref grenadeCoverage,
                "CER_SquadSetup_GrenadeCoverageSliderTip".Translate(),
                () => assignDirty = true);
            y = DrawSlider(inRect, y, "CER_SquadSetup_NoPacks".Translate(), "CER_SquadSetup_GenerousPacks".Translate(), ref packCoverage,
                "CER_SquadSetup_PackCoverageSliderTip".Translate(),
                () => assignDirty = true);
            y = DrawSlider(inRect, y, "CER_SquadSetup_NoShields".Translate(), "CER_SquadSetup_GenerousShields".Translate(), ref shieldCoverage,
                "CER_SquadSetup_ShieldCoverageSliderTip".Translate(),
                () => assignDirty = true);

            float cbx = 0f;
            bool CheckTight(string label, bool val, string tip)
            {
                float w = Text.CalcSize(label).x + 26f;
                if (cbx > 0f && cbx + w > inRect.width) { cbx = 0f; y += 28f; }
                Rect rr = new Rect(cbx, y, w, 24f);
                bool v = val;
                Widgets.CheckboxLabeled(rr, label, ref v);
                TooltipHandler.TipRegion(rr, tip);
                cbx += w + 34f;
                return v;
            }

            // "Produce missing/main/sidearms" checkboxes removed entirely (2026-07-30) -
            // Dialog_AssignLoadouts.produceMissing/produceMainWeapons/
            // produceSidearms are now permanently-true consts; the end-of-session production check
            // (LoadoutUtility.CheckAndOpenMissingProductionPopup) covers this need automatically regardless of
            // this screen, so there's no longer a reason to expose a way to turn any of it off.
            bool rd = CheckTight("CER_SquadSetup_RedistributeCarriedWeapons".Translate(), redistribute, "CER_SquadSetup_RedistributeCarriedWeaponsTip".Translate());
            if (rd != redistribute) { redistribute = rd; assignDirty = true; }
            // "Create new loadouts" went away with lazy creation - virtual picks are the normal case.

            // Sane default (5.0) inherited from the OLD baked-in-sidearm mechanism's own bulk cap,
            // reintroduced here for the auto-solver's separate-Weapon-Loadout sidearm too (AssignPlanner.
            // PickSidearm) - nothing else stopped a full battle rifle from being picked as someone's
            // "backup" just because it scored well. Manual sidearm picks (the Sidearm column's own
            // checklist) are NOT capped - a deliberate player choice overrides this. Lives with the other
            // small options here, not squeezed between the sliders above.
            {
                string label = "CER_SquadSetup_MaxSidearmBulk".Translate();
                float w = Text.CalcSize(label).x + 6f + 60f;
                if (cbx > 0f && cbx + w > inRect.width) { cbx = 0f; y += 28f; }
                Widgets.Label(new Rect(cbx, y + 2f, Text.CalcSize(label).x, 20f), label);
                string buf = CEReloadedMod.settings.sidearmMaxBulk;
                Rect fieldRect = new Rect(cbx + Text.CalcSize(label).x + 6f, y + 1f, 60f, 22f);
                string newBuf = Widgets.TextField(fieldRect, buf);
                // Don't forget Write() - see the twin in Dialog_PickPacks: the value lives in the mod settings and
                // was silently reset on the next start without this call.
                if (newBuf != buf) { CEReloadedMod.settings.sidearmMaxBulk = newBuf; CEReloadedMod.settings.Write(); assignDirty = true; }
                TooltipHandler.TipRegion(new Rect(cbx, y, w, 24f), "CER_SquadSetup_MaxSidearmBulkTip".Translate());
                cbx += w + 34f;
            }

            y += 28f;
            return y;
        }

        // ---- Generate list ----

        // ---- Assign list ----

        // Height of a problem line: wrapped up to three lines (Small), beyond that the rect clips - the full
        // text is in the line's tooltip.
        private static float ProblemLineH(AssignProblem p, float labelW)
        {
            GameFont prev = Text.Font;
            Text.Font = GameFont.Small;
            float h = Mathf.Clamp(Text.CalcHeight("- " + p.text, labelW), 22f, 66f);
            Text.Font = prev;
            return h;
        }

        private void DrawAssignList(Rect inRect, float y, float height)
        {
            float headerW = inRect.width - 16f;
            // Always shown now - a Pack/Sidearm cell that only sometimes exists (gated on whether ANY
            // Equipment Pack or sidearm-candidate loadout exists ANYWHERE in the game) read as broken, not
            // as "nothing to show here". Every cell already renders its own "-"/"None" state correctly.
            bool hasPacks = true;
            bool hasSidearmOptions = true;
            // On narrow screens one of the two extra columns drops out here so the Planned cell stays clickable -
            // see EffectiveColumns. Header and rows calculate with the same width (headerW == viewRect.width), so
            // they can't drift apart.
            EffectiveColumns(headerW, ref hasPacks, ref hasSidearmOptions);

            // Master toggle above the state column: one click locks EVERY colonist (nothing gets re-planned),
            // the next unlocks them all. The icon shows the action, like the Generate tab's expand-all button.
            if (assignPlan != null)
            {
                // Uses the SAME notion of state as the rows below (brought in line with the row cycle): this button
                // used to read `locked` raw, and `locked` means "differs from the global default" - with "Keep
                // current loadouts" on, that is the OPPOSITE of pinned. Header and rows could thus show different
                // things at the same time.
                // Anyone with nothing to keep (no current loadout) doesn't count here - otherwise "all pinned"
                // would never be true and the button would get stuck in one direction.
                List<AssignPlanEntry> togglable = assignPlan.entries
                    .Where(e => !LoadoutUtility.CannotFightByNature(e.pawn) && (e.current != null || e.pinned)).ToList();
                bool allKept = togglable.Count > 0 && togglable.All(e => MarkOf(e) == RowMark.Kept);
                Rect masterR = new Rect(2f, y - 1f, 18f, 18f);
                // PinTex, not LockTex - matches every per-row icon for this same `locked` state (DrawPawnRow's
                // own pin/checkbox swap, 2026-07-31: a pin still reads as fully editable, a padlock reads as
                // "can't touch this"). This header button was the one spot still showing the old padlock
                // (2026-08-01).
                if (LoadoutUtility.StateIconButton(masterR, allKept ? Widgets.CheckboxOnTex : PinTex, allKept ? Color.white : LoadoutUtility.ColPin))
                {
                    // Overrides hard, including pins and a set "take loadout away" (convention: the master toggles
                    // EVERYTHING, pins included).
                    foreach (AssignPlanEntry e in togglable) SetMark(e, allKept ? RowMark.Planned : RowMark.Kept);
                    assignDirty = true;
                }
                TooltipHandler.TipRegion(masterR, allKept
                    ? "CER_SquadSetup_UnlockEveryoneTip".Translate()
                    : "CER_SquadSetup_LockEveryoneTip".Translate());
            }

            GUI.color = new Color(1f, 1f, 1f, 0.55f);
            Text.Font = GameFont.Tiny;
            // Header x-offsets match the cell x-offsets in DrawPawnRow exactly, so each column title sits over its
            // content (portrait+name, the two score bars, the current/planned loadout cells).
            Widgets.Label(new Rect(30f + PinColW, y, 108f, 18f), "CER_SquadSetup_ColonistColumn".Translate());
            Widgets.Label(new Rect(182f + PinColW, y, 86f, 18f), "CER_SquadSetup_RangedColumn".Translate());
            Widgets.Label(new Rect(272f + PinColW, y, 86f, 18f), "CER_SquadSetup_MeleeColumn".Translate());
            Widgets.Label(new Rect(366f + PinColW, y, 86f, 18f), "CER_SquadSetup_MedicalColumn".Translate());
            Widgets.Label(new Rect(460f + PinColW, y, 134f, 18f), "CER_SquadSetup_CurrentColumn".Translate());
            // Width from the column chain, not the old fixed 200px (found by the layout checker): the Pack column
            // is right-anchored and moves left as soon as something is added on the right - since the supply
            // column became 320 wide, this heading stuck 10px into the Pack heading, and 42px with the new paste
            // column. The same calculation as the Planned CELL in DrawPawnRow, so header and content can't drift
            // apart.
            float hdrPlanX = PlanColBaseX + PinColW;
            float hdrPlanW = Mathf.Max(PlanCellMinW, PackColX(headerW, hasPacks, hasSidearmOptions) - 6f - hdrPlanX);
            Widgets.Label(new Rect(hdrPlanX, y, hdrPlanW, 18f), "CER_SquadSetup_PlannedMainWeaponColumn".Translate().Truncate(hdrPlanW));
            if (hasPacks) Widgets.Label(new Rect(PackColX(headerW, true, hasSidearmOptions), y, PackColW, 18f), "CER_SquadSetup_PackColumn".Translate());
            if (hasSidearmOptions) Widgets.Label(new Rect(SidearmColX(headerW, hasPacks, true), y, SidearmColW, 18f), "CER_SquadSetup_SidearmColumn".Translate());
            // Three EQUAL sub-headers matching the badge's own Main/Packs/Ammo split below (2026-07-30,
            // was a bigger fixed share for Main, now all three match).
            Text.Anchor = TextAnchor.MiddleCenter;
            const float hdrGap = 8f;
            float hdrEqualW = (SupplyColW - 4f - 2f * hdrGap) / 3f;
            float hdrX = headerW - MassBulkColTotalW - SupplyColW;
            Widgets.Label(new Rect(hdrX, y, hdrEqualW, 18f), "CER_SquadSetup_MainSubheader".Translate());
            Widgets.Label(new Rect(hdrX + hdrEqualW + hdrGap, y, hdrEqualW, 18f), "CER_SquadSetup_PacksSubheader".Translate());
            Widgets.Label(new Rect(hdrX + 2f * (hdrEqualW + hdrGap), y, hdrEqualW, 18f), "CER_SquadSetup_AmmoSubheader".Translate());
            Widgets.Label(new Rect(headerW - MassBulkColTotalW + 6f, y, MassColW, 18f), "CER_AssignLoadouts_MassLabel".Translate());
            Widgets.Label(new Rect(headerW - BulkColW - 6f, y, BulkColW, 18f), "CER_AssignLoadouts_BulkLabel".Translate());
            Text.Anchor = TextAnchor.UpperLeft;
            Text.Font = GameFont.Small;
            GUI.color = Color.white;
            y += 20f;
            height -= 20f;

            // AssignPlanEntry.IsChange, not a second copy of its expression (2026-08-12): with the old copy a
            // row whose ONLY pending edit was a pinned sidearm/pack disappeared from the filtered list the
            // moment the pin was set - the same blind spot that had the Apply button reading "(nothing to
            // apply)" for it.
            List<AssignPlanEntry> shown = diffOnly
                ? assignPlan.entries.Where(e => e.IsChange).ToList()
                : assignPlan.entries;

            // Wrap instead of truncate (in a large colony a message was cut off mid-sentence, e.g. '... (stick
            // bomb)) - the listed i...', so the reason was no longer readable). Every message gets up to three
            // lines, measured at the same width that is drawn below (viewRect = inRect.width - 16, label =
            // viewRect - 16).
            float problemsH = 0f;
            float problemLabelW = inRect.width - 32f;
            if (!diffOnly && assignPlan.problems.Count > 0)
                problemsH = 30f + assignPlan.problems.Take(3).Sum(p => ProblemLineH(p, problemLabelW));

            // Total crafting cost preview: one labeled line per component category (main weapons/sidearms/
            // grenades/shields/ammunition) instead of one opaque combined number, plus a grand total when more
            // than one category actually has a cost.
            Dictionary<ReqCategory, Dictionary<ThingDef, int>> costsByCategory = AssignCraftCostsByCategory();
            int costCategoryLines = costsByCategory != null ? costsByCategory.Count(kv => kv.Value.Count > 0) : 0;
            // Leading 32f = the 8f gap before the header (cy = outRect.yMax + 8f) PLUS the header's own 24f
            // advance (cy += 24f after the 16f label) - was 22f (2026-08-01 fix: the last cost
            // row/grand total still visibly touched whatever comes right after this strip, e.g. the bottom
            // button bar on a Problems-free screen). The previous 22f was a leftover from an earlier version
            // of this same headroom adjustment that never got fully recalculated to the actual 8+24 total.
            float costStripH = costsByCategory != null
                ? 32f + Mathf.Max(1, costCategoryLines) * 26f + (costCategoryLines > 1 ? 26f : 0f)
                : 0f;

            // The cost-preview strip and Problems panel used to be reserved OUTSIDE the scrollview, at a fixed
            // height computed from `height` (this section's own budget, which shrinks when both Generate and
            // Assign are open at once). When the pawn list + strip + problems together needed more room than
            // that budget actually had, the outer Mathf.Max(20f, ...) floor silently broke the accounting and
            // the strip was drawn past this section's own bottom edge - straight into the Back/Cancel button
            // bar below it (2026-08-01: the preview stuck to the
            // buttons). Fix: put all three (pawn rows, cost strip, Problems) INSIDE one scrollview sized to
            // this section's real budget - if it doesn't all fit, the player scrolls, it can never spill past
            // the section's own bottom edge into whatever is drawn after it.
            Rect outRect = new Rect(0, y, inRect.width, Mathf.Max(20f, height));
            float contentH = shown.Count * 34f + costStripH + (problemsH > 0 ? problemsH + 12f : 0f);
            Rect viewRect = new Rect(0, 0, outRect.width - 16f, Mathf.Max(contentH, outRect.height));
            Widgets.BeginScrollView(outRect, ref assignScroll, viewRect);
            float ry = 0f;
            foreach (AssignPlanEntry e in shown)
            {
                // Scroll culling: rows outside the visible window only advance the cursor.
                if (ry + 34f < assignScroll.y || ry > assignScroll.y + outRect.height) { ry += 34f; continue; }
                DrawPawnRow(new Rect(0, ry, viewRect.width, 32f), e, hasPacks, hasSidearmOptions);
                ry += 34f;
            }
            DrawDragChipAndReset();

            if (shown.Count == 0 && diffOnly)
            {
                GUI.color = new Color(1f, 1f, 1f, 0.5f);
                Widgets.Label(new Rect(0, ry + 8f, viewRect.width, 24f), "CER_SquadSetup_NoChangesCurrentSettings".Translate());
                GUI.color = Color.white;
            }

            if (costStripH > 0f)
            {
                // Headroom above this text (clear of the pawn list) AND below it (clear of the cost rows
                // that follow - 2026-07-30: it still stuck directly to the values
                // underneath even after the earlier above-side fix).
                float cy = ry + 8f;
                Text.Font = GameFont.Tiny;
                GUI.color = new Color(1f, 1f, 1f, 0.55f);
                Widgets.Label(new Rect(0, cy, viewRect.width, 16f), "CER_SquadSetup_PreviewCraftingCostsHeader".Translate());
                GUI.color = Color.white;
                Text.Font = GameFont.Small;
                cy += 24f;

                var grandTotal = new Dictionary<ThingDef, int>();
                bool any = false;
                foreach (var catLabel in CostCategoryLabels)
                {
                    if (!costsByCategory.TryGetValue(catLabel.cat, out var costs) || costs.Count == 0) continue;
                    LoadoutUtility.DrawTotalCostLine(0f, cy, viewRect.width, costs, catLabel.label);
                    cy += 26f;
                    any = true;
                    foreach (var kv in costs) grandTotal[kv.Key] = (grandTotal.TryGetValue(kv.Key, out int c) ? c : 0) + kv.Value;
                }
                if (!any) LoadoutUtility.DrawTotalCostLine(0f, cy, viewRect.width, null);
                else if (costCategoryLines > 1) LoadoutUtility.DrawTotalCostLine(0f, cy, viewRect.width, grandTotal, "CER_SquadSetup_GrandTotalLabel".Translate());
                ry += costStripH;
            }

            if (problemsH > 0)
            {
                Rect probRect = new Rect(0, ry + 4f, viewRect.width, problemsH);
                Widgets.DrawMenuSection(probRect);
                float py = probRect.y + 5f;
                GUI.color = new Color(0.95f, 0.75f, 0.35f);
                Widgets.Label(new Rect(probRect.x + 8f, py, probRect.width - 16f, 20f), "CER_SquadSetup_ProblemsHeader".Translate());
                py += 22f;
                Text.Font = GameFont.Small;
                foreach (AssignProblem p in assignPlan.problems.Take(3))
                {
                    float labelW = probRect.width - 16f;
                    float lineH = ProblemLineH(p, labelW);
                    Rect lblR = new Rect(probRect.x + 8f, py, labelW, lineH);
                    // CLICKABLE when the line knows a way out. Since the lazy-creation rollback only "roles only, but
                    // no role" remains: the jump into the weapon list led nowhere, because every owned or producible
                    // weapon is a candidate anyway.
                    bool canFix = p.action == ProblemAction.OpenRolesTab && assignOwner?.parentWindow != null;
                    if (canFix && Mouse.IsOver(lblR)) Widgets.DrawHighlight(lblR);
                    Widgets.Label(lblR, "- " + p.text);
                    TooltipHandler.TipRegion(lblR, canFix
                        ? p.text + "\n\n" + "CER_SquadSetup_ProblemClickToFix".Translate()
                        : (TaggedString)p.text);
                    if (canFix && Widgets.ButtonInvisible(lblR))
                    {
                        assignOwner.parentWindow.JumpToLoadouts();
                        Close();
                    }
                    py += lineH;
                }
                if (assignPlan.problems.Count > 3)
                    TooltipHandler.TipRegion(probRect, string.Join("\n\n", assignPlan.problems.Select(x => x.text).ToArray()));
                GUI.color = Color.white;
                ry += problemsH + 4f;
            }

            Widgets.EndScrollView();
        }

        // The first-column control for a colonist who has NO loadout yet. Both "keep current" branches used to
        // draw a dead checkmark here, because keep-current genuinely means nothing when there is nothing to
        // keep - and that is exactly what was observed twice: the green check could be clicked, but
        // nothing changed (2026-08-09).
        //
        // Deliberately NOT wired through the existing `locked` set, even though that is the set the other
        // branches use. `locked` means "flip this pawn's kept-state against the current global default", and
        // AssignPlanner.Compute hard-guards it with `cur != null` for a reason documented at length there: a
        // no-loadout pawn who counts as "kept" is frozen at nothing-assigned-nothing-planned with no way back.
        // Reusing it here would therefore either do nothing (guard holds) or resurrect that freeze (guard
        // removed). Skipping is its own, unambiguous statement, so it gets its own storage - the per-save
        // exclusion flag, which also makes the colonist grey in every other screen at once.
        // ===== The state cycle of the Assign row =========================================================
        //
        // Previously a left click on the leftmost icon meant something different depending on the row: for a
        // colonist WITH a loadout pin or release, for one WITHOUT a loadout "skip entirely". One click, two
        // meanings - exactly the inconsistency this replaces.
        // Now the same house rule applies here as in Weapon Loadouts and Equipment Packs: ONE icon per row, it
        // shows the STATE, left click cycles forward, right click backward.
        // Order deliberately identical to DrawGenerateRow (check -> trash -> pin -> check).
        //
        // No new stored field: `locked` (not saved) carries the pin, IsExcludedFromArming (per save) the
        // removal. The cannotFight branch further down stays as it was - for a pacifist or a baby the state is
        // a fact about the pawn, not a toggle.
        private enum RowMark { Planned, Removed, Kept }

        private RowMark MarkOf(AssignPlanEntry e)
        {
            if (LoadoutUtility.IsExcludedFromArming(e.pawn)) return RowMark.Removed;
            if (e.pinned) return RowMark.Kept;
            // Without a current loadout there is nothing to keep - AssignPlanner forces kept=false there (see its
            // `cur != null` guard), so a pin would be a click that immediately snaps back.
            if (e.current == null) return RowMark.Planned;
            return (keepAssigned ? !locked.Contains(e.pawn) : locked.Contains(e.pawn)) ? RowMark.Kept : RowMark.Planned;
        }

        private void SetMark(AssignPlanEntry e, RowMark m)
        {
            LoadoutUtility.SetExcludedFromArming(e.pawn, m == RowMark.Removed);
            if (m != RowMark.Removed)
            {
                bool wantKept = m == RowMark.Kept;
                // A loadout pin (set via the planning cell) is also a "leave as is" - whoever switches to "replan"
                // from here wants to get rid of it.
                if (e.pinned && !wantKept) pinned.Remove(e.pawn);
                // `locked` means "differs from the current global default", not "is pinned" - so the direction
                // depends on keepAssigned (see AssignPlanner's kept formula).
                if (keepAssigned == wantKept) locked.Remove(e.pawn); else locked.Add(e.pawn);
            }
            assignDirty = true;
        }

        private static RowMark CycleMark(RowMark m, bool canKeep, bool forward)
        {
            if (!canKeep) return m == RowMark.Removed ? RowMark.Planned : RowMark.Removed;
            if (forward) return m == RowMark.Planned ? RowMark.Removed : (m == RowMark.Removed ? RowMark.Kept : RowMark.Planned);
            return m == RowMark.Planned ? RowMark.Kept : (m == RowMark.Kept ? RowMark.Removed : RowMark.Planned);
        }

        // Draws the icon and handles both click directions. Check the right click BEFORE Widgets.ButtonImage -
        // see DrawGenerateRow, where the same order stands for the same reason (Unity's GUI.DoControl consumes
        // MouseDown for EVERY mouse button, not just the left one).
        private void DrawRowMark(Rect iconR, Rect iconHit, AssignPlanEntry e)
        {
            RowMark mark = MarkOf(e);
            bool canKeep = e.current != null || e.pinned;
            Texture2D tex = mark == RowMark.Kept ? PinTex
                : mark == RowMark.Removed ? LoadoutUtility.TexTrash
                : Widgets.CheckboxOnTex;
            // The color goes as a PARAMETER to StateIconButton, not via GUI.color - that has been the convention
            // since the hover color fix (StateIconButton uses the same value for the normal and hover state,
            // while GUI.color would only multiply).
            Color markCol = mark == RowMark.Kept ? LoadoutUtility.ColPin
                : mark == RowMark.Removed ? LoadoutUtility.ColBad : Color.white;
            GUI.color = Color.white;
            if (Event.current.type == EventType.MouseDown && Event.current.button == 1 && Mouse.IsOver(iconHit))
            {
                SetMark(e, CycleMark(mark, canKeep, false));
                Event.current.Use();
            }
            else if (LoadoutUtility.StateIconButton(iconR, tex, markCol))
            {
                SetMark(e, CycleMark(mark, canKeep, true));
            }
            GUI.color = Color.white;

            // Build only when hovered (see LoadoutUtility.TipWanted) - five to seven .Translate() calls plus
            // concatenations, per visible colonist row per frame.
            if (!LoadoutUtility.TipWanted(iconHit)) return;
            string stateTip = mark == RowMark.Removed
                ? (e.current != null
                    ? "CER_SquadSetup_MarkRemovedTip".Translate(e.pawn.LabelShortCap.ToString()).ToString()
                    : "CER_SquadSetup_MarkRemovedNoneTip".Translate(e.pawn.LabelShortCap.ToString()).ToString())
                : mark == RowMark.Kept
                    ? "CER_SquadSetup_MarkKeptTip".Translate().ToString()
                    : "CER_SquadSetup_MarkPlannedTip".Translate().ToString();
            TooltipHandler.TipRegion(iconHit, stateTip
                + "\n\n" + "CER_SquadSetup_LeftClickLabel".Translate().ToString() + " " + MarkActionLabel(CycleMark(mark, canKeep, true))
                + "\n" + "CER_SquadSetup_RightClickLabel".Translate().ToString() + " " + MarkActionLabel(CycleMark(mark, canKeep, false)));
        }

        private static string MarkActionLabel(RowMark m)
            => m == RowMark.Removed ? "CER_SquadSetup_ActionRemoveLoadout".Translate().ToString()
             : m == RowMark.Kept ? "CER_SquadSetup_ActionKeepAsIs".Translate().ToString()
             : "CER_SquadSetup_ActionPlanNormally".Translate().ToString();

        private void DrawPawnRow(Rect row, AssignPlanEntry e, bool hasPacks, bool hasSidearmOptions)
        {
            if (Mouse.IsOver(row)) Widgets.DrawHighlight(row);
            // Drop target FIRST, so releasing a dragged colonist here swaps loadouts and consumes the mouse-up
            // before the row's own click handlers (pin menu, lock) can fire on the same release.
            HandleDropTarget(row, e.pawn);
            if (e.kept) GUI.color = new Color(1f, 1f, 1f, 0.55f);

            Rect iconR = new Rect(row.x + 2f, row.y + 6f, 20f, 20f);
            Rect iconHit = new Rect(row.x, row.y, 26f, row.height);
            // ONE icon, three states, the same cycle in EVERY row - see DrawRowMark. The former four branches
            // (cannotFight / pinned / keepAssigned / without loadout / rest) did something different with the
            // same click depending on the row; that was exactly the problem.
            if (LoadoutUtility.CannotFightByNature(e.pawn))
            {
                // The only branch that is NOT a toggle: pacifist or baby. That is a fact about the pawn, not a state
                // one could cycle - a dead icon, as before.
                GUI.color = new Color(1f, 1f, 1f, 0.5f);
                GUI.DrawTexture(iconR, LockTex);
                GUI.color = new Color(1f, 1f, 1f, 0.55f);
                TooltipHandler.TipRegion(iconHit, "CER_SquadSetup_CannotFightTip".Translate());
            }
            else DrawRowMark(iconR, iconHit, e);
            GUI.color = e.kept ? new Color(1f, 1f, 1f, 0.55f) : Color.white;

            GUI.DrawTexture(new Rect(row.x + 30f + PinColW, row.y - 2f, 32f, 36f), PortraitsCache.Get(e.pawn, new Vector2(32f, 36f), Rot4.South));
            Widgets.Label(new Rect(row.x + 68f + PinColW, row.y + 6f, 108f, 24f), e.pawn.LabelShortCap.Truncate(104f));

            float maxScore = SkillRecord.MaxLevel * 10f;
            GUI.color = Color.white;
            LoadoutUtility.ScoreBar(new Rect(row.x + 182f + PinColW, row.y + 9f, 86f, 15f), assignOwner.RangedScoreOf(e.pawn), maxScore, null,
                tooltip: "CER_SquadSetup_RangedScoreTip".Translate());
            LoadoutUtility.ScoreBar(new Rect(row.x + 272f + PinColW, row.y + 9f, 86f, 15f), assignOwner.MeleeScoreOf(e.pawn), maxScore, null,
                tooltip: "CER_SquadSetup_MeleeScoreTip".Translate());
            float medScore = (e.pawn.skills?.GetSkill(SkillDefOf.Medicine)?.Level ?? 0) * 10f;
            LoadoutUtility.ScoreBar(new Rect(row.x + 366f + PinColW, row.y + 9f, 86f, 15f), medScore, maxScore, null,
                tooltip: "CER_SquadSetup_MedicalScoreTip".Translate());

            GUI.color = new Color(1f, 1f, 1f, 0.55f);
            Rect curRect = new Rect(row.x + 460f + PinColW, row.y + 6f, 134f, 24f);
            string curLabel = e.current != null ? LoadoutUtility.CellLabel(AmmoAssignmentUtility.DisplayLoadout(e.current) ?? e.current) : "CER_SquadSetup_NoneCurrentLoadout".Translate();
            Widgets.Label(curRect, curLabel.Truncate(130f));
            if (e.current != null) TooltipHandler.TipRegion(curRect, LoadoutUtility.LoadoutContentsTooltip(e.current));
            GUI.color = e.kept ? new Color(1f, 1f, 1f, 0.55f) : Color.white;

            // Plain arrow icon (white while a pin is active); the CLICK TARGET is the planned-loadout
            // cell itself, wired below - no extra button chrome.
            // LoadoutUtility.TexArrowRight instead of TexUI.ArrowTexRight: RimWorld's arrow is itself a 144 grey,
            // so the pinned state could never get brighter than 144. Reasoning and measurement are at the
            // constant.
            Rect arrowRect = new Rect(row.x + 600f + PinColW, row.y + 5f, 26f, 24f);
            GUI.color = e.cannotFight ? new Color(1f, 1f, 1f, 0.3f)
                : (e.pinned ? LoadoutUtility.ColPinArrow : new Color(1f, 1f, 1f, 0.55f));
            GUI.DrawTexture(arrowRect.ContractedBy(3f), LoadoutUtility.TexArrowRight);
            GUI.color = e.kept ? new Color(1f, 1f, 1f, 0.55f) : Color.white;

            // Capped to PlanCellW (see its own comment) instead of stretching all the way to the Supply
            // badge - clicking this cell (below) is the only pin entry point on the row, still comfortably
            // sized as a click/drag target without dragging the Pack column along into empty space.
            float planX = row.x + PlanColBaseX + PinColW;
            // Mathf.Max as a last safeguard: EffectiveColumns already keeps the width above PlanCellMinW, but this
            // cell carries pinning AND drag-swap - a negative width here is a silent total loss of the controls,
            // not a cosmetic flaw. Guarding twice is cheaper than assuming every future column change remembers
            // EffectiveColumns.
            float planW = Mathf.Max(PlanCellMinW, PackColX(row.width, hasPacks, hasSidearmOptions) - 6f - planX);
            Rect planRect = new Rect(planX, row.y + 3f, planW, 28f);
            if (e.cannotFight)
            {
                // Two FUNDAMENTALLY different cases that used to get the same text here: LoadoutUtility.CannotFight
                // is true for a pacifist or a baby - a fact about the pawn - AND for someone the player set to
                // "skip". Nine combat-capable colonists, one of them with Shooting 18, thus showed "can't fight -
                // skipped", indistinguishable from the three genuinely incapable ones.
                // LoadoutUtility.IsExcludedFromArming was split off exactly for this distinction (see its own
                // comment: "a pacifist or a baby is a fact about the pawn ... while this one is a decision the
                // player made") - this cell just never used it.
                bool skippedByPlayer = LoadoutUtility.IsExcludedFromArming(e.pawn) && !LoadoutUtility.CannotFightByNature(e.pawn);
                // This column says in words BEFOREHAND what Apply will do - for the trash icon that is no longer
                // "will be skipped" but "loses his loadout". Anyone who never had one still gets the old sentence,
                // which is correct here.
                bool losesLoadout = skippedByPlayer && e.current != null;
                GUI.color = skippedByPlayer ? LoadoutUtility.ColWarn : LoadoutUtility.ColBad;
                Widgets.Label(new Rect(planRect.x, planRect.y + 3f, planRect.width, 24f),
                    losesLoadout ? "CER_SquadSetup_LoadoutRemoved".Translate()
                    : skippedByPlayer ? "CER_SquadSetup_SkippedByYou".Translate() : "CER_SquadSetup_CantFightSkipped".Translate());
                GUI.color = e.kept ? new Color(1f, 1f, 1f, 0.55f) : Color.white;
                TooltipHandler.TipRegion(planRect, losesLoadout
                    ? "CER_SquadSetup_LoadoutRemovedTip".Translate(e.pawn.LabelShortCap.ToString())
                    : skippedByPlayer
                        ? "CER_SquadSetup_SkippedByYouTip".Translate()
                        : "CER_SquadSetup_CannotFightNoWeaponTip".Translate());
            }
            else if (e.kept && !e.pinned)
            {
                if (e.current != null) DrawLoadoutCell(planRect, e.current, "CER_SquadSetup_KeptSuffix".Translate());
                else Widgets.Label(new Rect(planRect.x, planRect.y + 3f, planRect.width, 24f), "CER_SquadSetup_NoneKept".Translate());
            }
            else if (e.pinned && e.planned != null)
            {
                DrawLoadoutCell(planRect, e.planned, "CER_SquadSetup_PinnedSuffix".Translate());
            }
            else if (e.planned != null)
            {
                DrawLoadoutCell(planRect, e.planned, null);
            }
            else if (e.plannedVirtual && e.plannedMain != null)
            {
                // NO "(new)" suffix and NO template name anymore: for the player this is simply the planned weapon -
                // that a loadout object is created behind it on Apply is invisible bookkeeping and must not show
                // through here.
                float ix = planRect.x;
                Widgets.ThingIcon(new Rect(ix, planRect.y + 2f, 24f, 24f), e.plannedMain); ix += 26f;
                Widgets.Label(new Rect(ix + 2f, planRect.y + 5f, planRect.xMax - ix - 4f, 24f),
                    e.plannedMain.LabelCap.ToString().Truncate(planRect.xMax - ix - 8f));
            }
            else
            {
                GUI.color = LoadoutUtility.ColBad;
                Widgets.Label(new Rect(planRect.x, planRect.y + 3f, planRect.width, 24f), "CER_SquadSetup_NoLoadoutLower".Translate());
                GUI.color = Color.white;
                TooltipHandler.TipRegion(planRect, "CER_SquadSetup_NoLoadoutCouldBeAssignedTip".Translate());
            }

            // The planned loadout cell is the manual override (click to pick any loadout for this colonist - pins
            // the choice, same mechanic as the pin column) AND the drag handle for swapping two colonists' planned
            // loadouts (press here and drag onto another colonist's row) - dragging the loadout itself reads more
            // naturally than dragging the portrait, since the loadout is the thing actually being moved around.
            if (!e.cannotFight)
            {
                Widgets.DrawHighlightIfMouseover(planRect);
                HandleDragHandle(planRect, e.pawn);
                string tip = dragPawn != null && dragPawn != e.pawn && Mouse.IsOver(planRect)
                    ? "CER_SquadSetup_DropToSwapTip".Translate(dragPawn.LabelShortCap.ToString()).ToString()
                    : "CER_SquadSetup_ClickToPickLoadoutTip".Translate(e.pawn.LabelShortCap.ToString())
                        + (e.pinned ? "CER_SquadSetup_CurrentlyPinnedAutoSuffix".Translate().ToString() : "")
                        + "CER_SquadSetup_DragToSwapSuffix".Translate();
                TooltipHandler.TipRegion(planRect, tip);
                if (Widgets.ButtonInvisible(planRect)) OpenPinMenu(e.pawn);
            }

            // Pin (plan 5.5) is entirely driven by clicking the planned-loadout cell above (OpenPinMenu) - the
            // dedicated pin-button icon that used to sit here was redundant (same action, different spot) and
            // just cluttered the row right next to the Supply badge.

            // Equipment Pack preview - every authored pack goes out alongside the weapon (Equipment Packs
            // are pure supply now, no competing "combat kit" choice - see plannedPacks/AssignPlanner),
            // unless this pawn's assignment is Role-derived and already bakes its own packs in directly.
            // Clickable here too: overrides go into `pinnedPacks` (a pending pin, like the weapon's own
            // pin above), not a live mutation - nothing in this preview applies until Apply. Icon-only, no
            // name chips - which pack(s) is what the tooltip and the Pack picker checklist are for; this
            // row just needs to read "does this colonist have any, and roughly how many" at a glance.
            // CannotCarryAnything, not the broader CannotFight - a pacifist adult CAN carry an Equipment Pack
            // (AssignPlanner's own packOnlyPawns eligibility uses this exact narrower rule; this preview used
            // to over-gate on cannotFight (2026-08-01): hiding a pacifist's correctly-computed pack
            // plan from review here, mirror-image of a separate bug already fixed elsewhere in DrawSidearmCell).
            if (hasPacks && !LoadoutUtility.CannotCarryAnything(e.pawn))
            {
                Rect packRect = new Rect(planRect.xMax + 6f, row.y + 3f, PackColW, 28f);
                List<Loadout> packs = e.plannedPacks ?? new List<Loadout>();
                // A Role has no pinning concept - everything it hands out is already baked into its own flat
                // Slots union, so plannedPacks/plannedSidearms are never populated for a Role-assigned pawn.
                // Read-only preview of what the Role's own content actually includes instead of a bare "-",
                // which used to read as "this colonist gets no pack" even when their Role plainly did
                // (2026-07-30). No paste button, no click-to-open-picker - editing this
                // means editing the Role itself (Roles tab), not this one colonist.
                bool isRoleRow = packs.Count == 0 && e.planned != null && RoleUtility.IsRoleLoadout(e.planned);
                List<Def> rolePackContent = null;
                if (isRoleRow) ExtractRoleDisplayContent(e.planned, out _, out rolePackContent);

                // No paste strip in THIS cell anymore - see DrawRowPasteColumn at the very end of the row. The cell
                // gets its full width back as a result.

                if (isRoleRow && rolePackContent.Count > 0)
                {
                    GUI.color = new Color(1f, 1f, 1f, 0.7f);
                    const int maxIcons = 8;
                    const float iconSize = 22f, packGap = 4f;
                    float cx = packRect.x;
                    int shown = Mathf.Min(maxIcons, rolePackContent.Count);
                    for (int i = 0; i < shown; i++)
                    {
                        Dialog_GenerateEquipmentPack.DrawItemIcon(new Rect(cx, packRect.y + (packRect.height - iconSize) / 2f, iconSize, iconSize), rolePackContent[i]);
                        cx += iconSize + packGap;
                    }
                    if (rolePackContent.Count > shown)
                    {
                        Text.Font = GameFont.Tiny;
                        Text.Anchor = TextAnchor.MiddleLeft;
                        Widgets.Label(new Rect(cx, packRect.y, packRect.xMax - cx, packRect.height), "+" + (rolePackContent.Count - shown));
                        Text.Anchor = TextAnchor.UpperLeft;
                        Text.Font = GameFont.Small;
                    }
                    GUI.color = Color.white;
                    TooltipHandler.TipRegion(packRect, "CER_SquadSetup_IncludedInRoleTip".Translate(LoadoutUtility.LoadoutDisplayName(e.planned),
                        string.Join(", ", rolePackContent.Select(d => d.LabelCap.ToString()))));
                }
                else if (packs.Count == 0)
                {
                    GUI.color = new Color(1f, 1f, 1f, 0.4f);
                    Text.Anchor = TextAnchor.MiddleLeft;
                    Widgets.Label(packRect, "-");
                    Text.Anchor = TextAnchor.UpperLeft;
                    if (isRoleRow) TooltipHandler.TipRegion(packRect, "CER_SquadSetup_RoleNoPackItemsTip".Translate());
                }
                else
                {
                    GUI.color = e.kept ? new Color(1f, 1f, 1f, 0.55f) : new Color(1f, 1f, 1f, 0.85f);
                    const int maxIcons = 8;
                    const float iconSize = 22f, packGap = 4f;
                    float cx = packRect.x;
                    int shown = Mathf.Min(maxIcons, packs.Count);
                    for (int i = 0; i < shown; i++)
                    {
                        EquipmentPackUtility.DrawIcon(new Rect(cx, packRect.y + (packRect.height - iconSize) / 2f, iconSize, iconSize), packs[i]);
                        cx += iconSize + packGap;
                    }
                    if (packs.Count > shown)
                    {
                        Text.Font = GameFont.Tiny;
                        Text.Anchor = TextAnchor.MiddleLeft;
                        Widgets.Label(new Rect(cx, packRect.y, packRect.xMax - cx, packRect.height), "+" + (packs.Count - shown));
                        Text.Anchor = TextAnchor.UpperLeft;
                        Text.Font = GameFont.Small;
                    }
                }
                GUI.color = Color.white;

                // Role rows already got their own read-only tooltip above and are never clickable here -
                // editing means editing the Role itself, not this one colonist's pin.
                if (!isRoleRow)
                {
                    string packLbl = packs.Count > 0 ? string.Join(", ", packs.Select(p => EquipmentPackUtility.StripMarker(p.label))) : "-";
                    Widgets.DrawHighlightIfMouseover(packRect);
                    string packAttachedBase = packs.Count > 0
                        ? "CER_SquadSetup_EquipmentPacksAttached".Translate(packLbl)
                        : "CER_SquadSetup_NoEquipmentPackAttached".Translate();
                    TooltipHandler.TipRegion(packRect, packAttachedBase + "CER_SquadSetup_PackClickOverrideSuffix".Translate());
                    if (Widgets.ButtonInvisible(packRect))
                        Find.WindowStack.Add(new Dialog_PickPacks("CER_SquadSetup_TickPacksSubtitle".Translate(e.pawn.LabelShortCap.ToString()),
                            // Reads the durable pending-pin first, falling back to the solver's own current pick -
                            // NOT e.plannedPacks alone, which would go stale the instant a recompute replaces this
                            // AssignPlanEntry with a fresh one (this popup's closures keep pointing at the old one).
                            () => pinnedPacks.TryGetValue(e.pawn, out List<Loadout> pinnedNow) ? pinnedNow : e.plannedPacks,
                            updated => ApplyPinnedPacks(e.pawn, updated), sourceLabel: e.pawn.LabelShortCap));
                }
            }

            // Sidearm preview - same checklist idea as Pack (a plain FloatMenu closes on the first pick, so
            // it can't offer several sidearms), scored automatically by AssignPlanner unless overridden here.
            // Overrides go into `pinnedSidearms`, same pending-pin pattern as packs/the weapon pin above.
            if (hasSidearmOptions && !e.cannotFight)
            {
                Rect sideRect = new Rect(SidearmColX(row.width, hasPacks, true), row.y + 3f, SidearmColW, 28f);
                List<Loadout> sides = e.plannedSidearms ?? new List<Loadout>();
                List<ThingDef> virtSides = e.plannedSidearmDefs ?? new List<ThingDef>();
                // Same Role read-only treatment as the Pack cell above - see its own comment.
                bool isRoleSideRow = sides.Count == 0 && e.planned != null && RoleUtility.IsRoleLoadout(e.planned);
                List<ThingDef> roleSidearmContent = null;
                if (isRoleSideRow) ExtractRoleDisplayContent(e.planned, out roleSidearmContent, out _);

                // No paste strip of its own here either anymore - see DrawRowPasteColumn.

                if (isRoleSideRow && roleSidearmContent.Count > 0)
                {
                    GUI.color = new Color(1f, 1f, 1f, 0.7f);
                    const int maxIcons = 8;
                    const float iconSize = 22f, sideGap = 4f;
                    float cx = sideRect.x;
                    int shown = Mathf.Min(maxIcons, roleSidearmContent.Count);
                    for (int i = 0; i < shown; i++)
                    {
                        Widgets.ThingIcon(new Rect(cx, sideRect.y + (sideRect.height - iconSize) / 2f, iconSize, iconSize), roleSidearmContent[i]);
                        cx += iconSize + sideGap;
                    }
                    if (roleSidearmContent.Count > shown)
                    {
                        Text.Font = GameFont.Tiny;
                        Text.Anchor = TextAnchor.MiddleLeft;
                        Widgets.Label(new Rect(cx, sideRect.y, sideRect.xMax - cx, sideRect.height), "+" + (roleSidearmContent.Count - shown));
                        Text.Anchor = TextAnchor.UpperLeft;
                        Text.Font = GameFont.Small;
                    }
                    GUI.color = Color.white;
                    TooltipHandler.TipRegion(sideRect, "CER_SquadSetup_IncludedInRoleTip".Translate(LoadoutUtility.LoadoutDisplayName(e.planned),
                        string.Join(", ", roleSidearmContent.Select(d => d.LabelCap.ToString()))));
                }
                else if (sides.Count == 0 && virtSides.Count == 0)
                {
                    GUI.color = new Color(1f, 1f, 1f, 0.4f);
                    Text.Anchor = TextAnchor.MiddleLeft;
                    Widgets.Label(sideRect, "-");
                    Text.Anchor = TextAnchor.UpperLeft;
                    if (isRoleSideRow) TooltipHandler.TipRegion(sideRect, "CER_SquadSetup_RoleNoSidearmTip".Translate());
                }
                else
                {
                    // Icon-only, "+N" overflow - same treatment as the Pack cell right above. A real backup
                    // weapon and any auto-picked grenades now both land in this one list, so it routinely
                    // holds 2+ entries; the old icon+name-per-item layout silently cut off whatever didn't
                    // fit with no sign anything was missing, unlike Pack's already-clean overflow badge.
                    // Virtual picks (plannedSidearmDefs, lazy creation) draw the same icons - for the player a
                    // sidearm is a weapon, whether or not a template already stands behind it.
                    GUI.color = e.kept ? new Color(1f, 1f, 1f, 0.55f) : new Color(1f, 1f, 1f, 0.85f);
                    const int maxIcons = 8;
                    const float iconSize = 22f, sideGap = 4f;
                    // Not AnalyzeLoadout - it never treats a thrown explosive as a "main weapon" (right
                    // for the auto-solver's own scoring), which would leave a grenade-only sidearm with
                    // no icon at all here. Any real weapon-type slot is enough for a display icon.
                    List<ThingDef> sideIcons = sides
                        .Select(s => s?.Slots?.Select(sl => sl.thingDef).FirstOrDefault(w => w != null && w.IsWeapon))
                        .Where(d => d != null)
                        .Concat(virtSides)
                        .ToList();
                    float cx = sideRect.x;
                    int shown = Mathf.Min(maxIcons, sideIcons.Count);
                    for (int i = 0; i < shown; i++)
                    {
                        Widgets.ThingIcon(new Rect(cx, sideRect.y + (sideRect.height - iconSize) / 2f, iconSize, iconSize), sideIcons[i]);
                        cx += iconSize + sideGap;
                    }
                    if (sideIcons.Count > shown)
                    {
                        Text.Font = GameFont.Tiny;
                        Text.Anchor = TextAnchor.MiddleLeft;
                        Widgets.Label(new Rect(cx, sideRect.y, sideRect.xMax - cx, sideRect.height), "+" + (sideIcons.Count - shown));
                        Text.Anchor = TextAnchor.UpperLeft;
                        Text.Font = GameFont.Small;
                    }
                }
                GUI.color = Color.white;

                if (!isRoleSideRow)
                {
                    // The same cleanup as in the Assign tab's sidearm cell - found by searching for further
                    // occurrences before it could come back as a second bug.
                    List<string> sideNames = sides.Select(s => LoadoutUtility.CellLabel(s))
                        .Concat(virtSides.Select(d => d.LabelCap.ToString())).ToList();
                    string sideLbl = sideNames.Count > 0 ? string.Join(", ", sideNames) : "-";
                    Widgets.DrawHighlightIfMouseover(sideRect);
                    string sideAttachedBase = sideNames.Count > 0
                        ? "CER_SquadSetup_SidearmsAttached".Translate(sideLbl)
                        : "CER_SquadSetup_NoSidearmAttached".Translate();
                    TooltipHandler.TipRegion(sideRect, sideAttachedBase + "CER_SquadSetup_SidearmClickTickSuffix".Translate(e.pawn.LabelShortCap.ToString()));
                    if (Widgets.ButtonInvisible(sideRect))
                    {
                        Loadout mainLoadout = e.planned;
                        Find.WindowStack.Add(new Dialog_PickPacks("CER_SquadSetup_TickSidearmsSubtitle".Translate(e.pawn.LabelShortCap.ToString()),
                            () => pinnedSidearms.TryGetValue(e.pawn, out List<Loadout> pinnedNow) ? pinnedNow : e.plannedSidearms,
                            updated => ApplyPinnedSidearms(e.pawn, updated),
                            windowTitle: "CER_SquadSetup_WeaponLoadoutsWindowTitle".Translate(), itemColumnLabel: "CER_AssignLoadouts_LabelSidearmsPlural".Translate(), clipboardKind: "sidearm",
                            allOptions: () => SidearmAssignmentUtility.CandidateLoadouts(mainLoadout),
                            emptyMessage: "CER_SquadSetup_NoOtherWeaponLoadoutsEmptyMsg".Translate(),
                            // Via the central naming function: this caller overrode Dialog_PickPacks' default -
                            // which has long gone through LoadoutDisplayName - with the RAW label, so this still
                            // showed "W| ..." or "[Role] ...".
                            displayLabel: lo => LoadoutUtility.LoadoutDisplayName(lo) ?? "?",
                            groupLabel: SidearmAssignmentUtility.CategoryLabel,
                            scoreFn: SidearmAssignmentUtility.ScoreOf,
                            showSidearmBulkCap: true,
                            availDefFn: Dialog_AssignLoadouts.RepresentativeIcon, sourceLabel: e.pawn.LabelShortCap,
                            // Lazy creation: weapons without a template as a separate section - ticking one creates
                            // the shared template and pins it like any other sidearm.
                            weaponOptions: () => LoadoutUtility.WeaponsWithoutTemplate(),
                            materializeWeapon: Dialog_GenerateLoadouts.EnsureDefaultLoadout));
                    }
                }
            }

            // The row's ONE paste column, between Sidearm and the supply field. It writes into the same pins as
            // the cells themselves (ApplyPinnedPacks/ApplyPinnedSidearms) - a preview stays a preview, even when
            // pasting. A colonist who can't carry anything doesn't get it; for sidearms
            // Dialog_AssignLoadouts.PasteSidearmsGuarded additionally checks itself whether the colonist may
            // fight at all.
            if (PreviewClipboardHasAny() && !LoadoutUtility.CannotCarryAnything(e.pawn))
            {
                Rect pasteRect = new Rect(PasteColX(row.width, hasPacks, hasSidearmOptions), row.y + 5f,
                    Dialog_AssignLoadouts.PasteColW, 24f);
                Dialog_AssignLoadouts.DrawPasteColumn(pasteRect, e.pawn, e.current,
                    updated => ApplyPinnedPacks(e.pawn, updated),
                    updated => ApplyPinnedSidearms(e.pawn, updated),
                    // Pasting a main weapon sets a PIN in the preview - nothing is written before Apply, same as for
                    // packs and sidearms.
                    mlo => { pinned[e.pawn] = mlo; assignDirty = true; });
            }

            // Supply column on the right, split into three sub-columns - Main (the main weapon AND its
            // sidearm/grenades/shield together: multi-parent already unions all of that into one Slots list,
            // so splitting it into two separate verdicts never reflected anything real), Packs (this
            // colonist's Equipment Pack contents - shown whenever packs exist, independent of whether a
            // weapon verdict even applies) and Ammo.
            // Mass/bulk at the far right: what he carries AFTER Apply, against his limits (see
            // AssignPlanEntry.previewMass). The same bar as in the compact view, including carrier icon and tooltip.
            Color prevRowCol = GUI.color;
            GUI.color = Color.white;
            Text.Font = GameFont.Tiny;
            Dialog_AssignLoadouts.DrawPawnCapacityBar(new Rect(row.xMax - MassBulkColTotalW + 6f, row.y + 6f, MassColW, 18f),
                e.previewMass, e.capMass, e.pawn, false);
            Dialog_AssignLoadouts.DrawPawnCapacityBar(new Rect(row.xMax - BulkColW - 6f, row.y + 6f, BulkColW, 18f),
                e.previewBulk, e.capBulk, e.pawn, true);
            Text.Font = GameFont.Small;
            GUI.color = prevRowCol;

            Rect badge = new Rect(row.xMax - MassBulkColTotalW - SupplyColW, row.y + 3f, SupplyColW - 4f, row.height - 6f);
            const float gap = 8f;
            float equalW = (badge.width - 2f * gap) / 3f;
            Rect mainArea = new Rect(badge.x, badge.y, equalW, badge.height);
            Rect packsArea = new Rect(mainArea.xMax + gap, badge.y, equalW, badge.height);
            Rect ammoArea = new Rect(packsArea.xMax + gap, badge.y, equalW, badge.height);
            if (!e.kept && (e.planned != null || e.plannedVirtual))
            {
                List<SupplyReqItem> reqs = SupplyRequirementsFor(e).Concat(SidearmRequirementsFor(e)).ToList();
                DrawSupplyReqGroup(mainArea, reqs.Where(r => r.category == ReqCategory.MainWeapon || r.category == ReqCategory.Sidearm
                    || r.category == ReqCategory.Grenade || r.category == ReqCategory.Shield).ToList(), "CER_SquadSetup_MainSubheader".Translate(), showRoleTag: true);
                DrawSupplyReqGroup(ammoArea, reqs.Where(r => r.category == ReqCategory.Ammo).ToList(), "CER_SquadSetup_AmmoSubheader".Translate());
                Text.Anchor = TextAnchor.UpperLeft;
                Text.Font = GameFont.Small;
            }
            if (hasPacks && !LoadoutUtility.CannotCarryAnything(e.pawn))
            {
                DrawSupplyReqGroup(packsArea, PackRequirementsFor(e), "CER_SquadSetup_PacksSubheader".Translate());
            }

            GUI.color = Color.white;
        }

        // One Supply sub-column (Weapons or Ammo): a simple colored "in stock" when every item in the group is
        // Ready, an icon-per-item row with a status dot when something isn't, or nothing at all when the group is
        // empty (e.g. no ammo needed for a self-ammo-only melee loadout).
        // showRoleTag: the "Main" badge merges main weapon + sidearm + grenade + shield into one visual group
        // (multi-parent already unions all of that into one Slots list, so a separate verdict per role never
        // reflected anything real) - but the tooltip listing them all under one heading read as if a sidearm
        // WAS the main weapon. When true, every item whose category isn't the pawn's actual main weapon gets a
        // "(sidearm)"/"(grenade)"/"(shield)" tag so the breakdown stays unambiguous without splitting the badge.
        private void DrawSupplyReqGroup(Rect area, List<SupplyReqItem> group, string groupLabel, bool showRoleTag = false)
        {
            if (group.Count == 0) return;

            string RoleTag(ReqCategory c)
            {
                if (!showRoleTag || c == ReqCategory.MainWeapon) return "";
                if (c == ReqCategory.Sidearm) return "CER_SquadSetup_RoleTagSidearm".Translate();
                if (c == ReqCategory.Grenade) return "CER_SquadSetup_RoleTagGrenade".Translate();
                if (c == ReqCategory.Shield) return "CER_SquadSetup_RoleTagShield".Translate();
                return "";
            }

            // An ammo item that's currently covered but can NEVER be topped up once consumed (not researched
            // or no bench) is not a genuine "everything's fine" green - it is a real, if less urgent, risk
            // (2026-08-02, the 4-state rule: green = in stock AND producible; orange = not in stock
            // but producible; orange+"!" = in stock but NOT producible; red+"!" = neither). Only ammo gets
            // this extra check (weapons/sidearms/shields keep plain green-when-covered, matching the same
            // ammo-only scope the Missing-severity split above already uses).
            bool AmmoUnsustainable(SupplyReqItem r) => r.status == ReqStatus.Ready && r.category == ReqCategory.Ammo
                && !Dialog_AssignLoadouts.AmmoProducibleNow(r.icon);

            if (group.All(r => r.status == ReqStatus.Ready) && !group.Any(AmmoUnsustainable))
            {
                // A plain green square, left-aligned to area.x - same starting position the per-item icons
                // below use (so the group doesn't visually jump to the middle of a often much wider column
                // just because everything in it happens to be covered), and the same solid-color-square
                // convention the per-item status dot below already uses, instead of a separate checkmark icon.
                float iconSize = Mathf.Min(18f, area.height);
                Rect okIconR = new Rect(area.x, area.y + (area.height - iconSize) / 2f, iconSize, iconSize);
                Widgets.DrawBoxSolid(okIconR, LoadoutUtility.ColGood);
                // Assemble only when hovered - see LoadoutUtility.TipWanted.
                if (LoadoutUtility.TipWanted(area))
                    TooltipHandler.TipRegion(area, groupLabel + "CER_SquadSetup_EverythingCoveredSuffix".Translate()
                        + string.Join("\n", group.Select(r => "- " + r.label + RoleTag(r.category)).ToArray()));
                return;
            }

            // One small icon per requirement with a status dot (green = ready, beige = in production, orange =
            // missing) - packed left to right; the full breakdown always lives in the tooltip even if more items
            // exist than fit on screen. Beige matches the shared AvailabilitySegments bar's own "in production"
            // color everywhere else now (2026-07-30) - this used to be ColInfo (blue), a color that means
            // nothing "in production"-specific anywhere else, just happened to be reused here.
            float ix = area.x;
            // The group's TEXT is only built when hovered.
            // The icons and their colors have to be drawn every frame - the status word below does not, and it is
            // the expensive part: per item ProducibleNow/AmmoProducibleNow (recipe + research + built workbenches)
            // plus ForbiddenStockCount, times three groups per visible colonist row, times every frame. See
            // LoadoutUtility.TipWanted for why RimWorld discarded the text anyway.
            bool wantTip = LoadoutUtility.TipWanted(area);
            var sb = wantTip ? new System.Text.StringBuilder(groupLabel + ":") : null;
            foreach (SupplyReqItem r in group)
            {
                // Missing has always meant one orange square whether a bill would fix it or it can NEVER be
                // produced right now (not researched / no bench) - the tooltip already said so, but the dot
                // itself gave no hint, reading as an ordinary "just needs a bill" shortfall either way.
                // Shield fix (2026-08-02: Pavise stayed orange despite being neither owned
                // nor producible) - shields are apparel, not ThingDef.IsWeapon, so the old `r.icon.IsWeapon
                // ? ... : (category==Ammo ? ... : false)` ternary fell through BOTH branches for a shield
                // and could never mark one impossible no matter what. ProducibleNow only checks recipe+bench
                // (never cares about IsWeapon), so it works for a shield exactly like it already does for a
                // gun - just route Shield through it the same way Ammo routes through AmmoProducibleNow.
                bool impossible = r.status == ReqStatus.Missing && (r.category == ReqCategory.Ammo
                    ? !Dialog_AssignLoadouts.AmmoProducibleNow(r.icon)
                    : !Dialog_AssignLoadouts.ProducibleNow(r.icon));
                // Severity split, keyed on CONSUMABLE vs DURABLE (2026-08-02, generalised from
                // the ammo-only version that shipped first):
                //   durable (weapon, sidearm, shield, a tool in a pack) -> impossible means red, full stop.
                //     One is needed, it cannot be had, and no amount of "some are in stock" applies when the
                //     colonist still ends up without one.
                //   consumable (ammo, medicine, drugs, food, thrown explosives) -> red only when the colony
                //     owns NONE at all; if some sits in stock or on the ground and it just isn't enough for
                //     everyone, that is a real but less dire gap and stays orange.
                // The first version keyed the durable side on r.icon.IsWeapon, which is FALSE for a shield
                // (apparel) - so a Pavise that was neither owned nor producible was correctly detected as
                // impossible one line above and then still painted orange here. Same hole for any durable
                // pack item. IsConsumableThing is the distinction the rest of the mod already uses
                // (LoadoutUtility.cs:352), so both sides now agree on what "used up" means.
                bool consumable = r.category == ReqCategory.Ammo || LoadoutUtility.IsConsumableThing(r.icon);
                // The stock lookup now sits IN the condition, not before it. It is only needed when the case is
                // impossible AND a consumable - previously it always ran, and GroundStockCount is not a lookup but
                // a pass over all things of that kind on every map, per item per group per row per frame.
                bool showRed = impossible && (!consumable
                    || LoadoutUtility.IngredientStockCount(r.icon) + LoadoutUtility.GroundStockCount(r.icon) <= 0);
                bool ammoUnsustainable = AmmoUnsustainable(r);
                Color dot = ammoUnsustainable ? LoadoutUtility.ColWarn
                    : r.status == ReqStatus.Ready ? LoadoutUtility.ColGood
                    : (r.status == ReqStatus.InProduction ? LoadoutUtility.AvailProductionBeige
                    : (showRed ? LoadoutUtility.ColBad : LoadoutUtility.ColWarn));
                if (ix + 18f <= area.xMax)
                {
                    Rect reqIconR = new Rect(ix, area.y + (area.height - 18f) / 2f, 18f, 18f);
                    Widgets.ThingIcon(reqIconR, r.icon);
                    // The "!" for "do not pick up" stock - bottom LEFT, because the status dot or warning badge
                    // already sits at the top right and two glyphs on top of each other make both unreadable. Its
                    // own tooltip off: the row already has one further down.
                    LoadoutUtility.DrawForbiddenMarkAt(new Rect(reqIconR.x - 2f, reqIconR.yMax - 10f, 10f, 10f),
                        LoadoutUtility.ForbiddenStockCount(r.icon), tooltip: false);
                    if (impossible || ammoUnsustainable)
                    {
                        // Shared LoadoutUtility.DrawWarningBadge (2026-08-02) - was its own hand-rolled 12x12
                        // circle+"!" here, now the same bigger/bolder badge every other warning glyph in the
                        // mod uses. `dot` above already correctly computed ColBad (red) vs ColWarn (orange)
                        // for this exact impossible case.
                        LoadoutUtility.DrawWarningBadge(new Rect(reqIconR.xMax - 12f, reqIconR.y - 6f, 16f, 16f), dot);
                    }
                    else
                    {
                        GUI.color = dot;
                        GUI.DrawTexture(new Rect(reqIconR.xMax - 7f, reqIconR.y - 2f, 8f, 8f), BaseContent.WhiteTex);
                        GUI.color = Color.white;
                    }
                    ix += 21f;
                }
                if (!wantTip) continue;
                string statusWord = r.status == ReqStatus.Ready ? "CER_SquadSetup_StatusReady".Translate()
                    : (r.status == ReqStatus.InProduction ? "CER_SquadSetup_StatusInProduction".Translate() : "CER_SquadSetup_StatusMissing".Translate());
                // Weapons already got this qualifier; ammo never did (r.icon.IsWeapon is false for an ammo
                // def), so a chosen-but-unresearched ammo type read as plain "missing" here - same as any
                // ordinary shortfall a bill would fix - with no hint that this one can never be produced at
                // all. Picking an unresearched/unowned ammo type likewise showed no warning in
                // this step. Shields (2026-08-02 fix) get the SAME weapon-shaped qualifier now,
                // routed through the same `impossible` fix above - see its own comment for why IsWeapon
                // alone used to miss them.
                if (r.status == ReqStatus.Missing)
                {
                    if (r.category == ReqCategory.Ammo)
                        statusWord += Dialog_AssignLoadouts.AmmoProducibleNow(r.icon) ? "CER_SquadSetup_CanBeCraftedSuffix".Translate() : (LoadoutUtility.IsAmmoResearched(r.icon) ? "CER_SquadSetup_NoBenchBuiltSuffix".Translate() : "CER_SquadSetup_NotResearchedSuffix".Translate());
                    else
                    {
                        // Three-way instead of "not researched / no bench": the tooltip now says exactly WHAT it is.
                        // ProducibleNow is an AND of THREE conditions - a recipe exists at all, the recipe is
                        // researched, and a suitable workbench stands. The code here knows exactly which of them is
                        // missing; it just never said so. The ammo branch one line above has long made the same
                        // distinction - this is the branch that never got it. Two of the three phrasings therefore
                        // already existed.
                        // "no recipe" is the case the ammo branch doesn't know either: a weapon nobody can produce at
                        // all (loot/mechanoid weapons). It used to read as "not researched", i.e. as something
                        // research could solve.
                        RecipeDef rec = LoadoutUtility.GetRecipeFor(r.icon);
                        statusWord += Dialog_AssignLoadouts.ProducibleNow(r.icon)
                            ? "CER_SquadSetup_CanBeCraftedSuffix".Translate()
                            : rec == null
                                ? "CER_SquadSetup_NoRecipeSuffix".Translate()
                                : LoadoutUtility.RecipeIsResearched(rec)
                                    ? "CER_SquadSetup_NoBenchBuiltSuffix".Translate()
                                    : "CER_SquadSetup_NotResearchedSuffix".Translate();
                    }
                }
                // Currently covered, but can't be topped up once consumed - the 4th state of the
                // ammo severity rule (green = in stock+producible, orange = producible only, orange+"!" =
                // in stock only, red+"!" = neither). Ready's own plain statusWord would otherwise read as an
                // unqualified "Ready", identical to a genuinely sustainable stock.
                else if (ammoUnsustainable)
                {
                    // Its own phrasing instead of the Missing suffixes: "ready (no bench built)" - ready AND no
                    // workbench in one sentence - read as a contradiction. The contradiction was only linguistic:
                    // what is meant is "enough NOW, but can't be replenished". The Missing suffixes answer the other
                    // question ("why isn't more coming") and read like an error after "ready".
                    statusWord += LoadoutUtility.IsAmmoResearched(r.icon)
                        ? "CER_SquadSetup_UnsustainableNoBenchSuffix".Translate()
                        : "CER_SquadSetup_UnsustainableNotResearchedSuffix".Translate();
                }
                sb.Append("\n- ").Append(r.label).Append(RoleTag(r.category)).Append(": ").Append(statusWord);
                // Listed separately, not mixed into statusWord - the stock now counts too, this line says how much
                // of it still has to be unforbidden.
                int reqForbidden = LoadoutUtility.ForbiddenStockCount(r.icon);
                if (reqForbidden > 0)
                    sb.Append("  ").Append("CER_LoadoutUtility_AvailForbidden".Translate(reqForbidden.ToString()).ToString());
            }
            if (wantTip) TooltipHandler.TipRegion(area, sb.ToString());
        }

        // ---- Drag-and-drop loadout swap (planned-loadout cell handle) -------------------------------------

        // The planned-loadout cell starts a drag (not the portrait/name - dragging the loadout itself is what
        // reads naturally here); a press without movement is left to the cell's normal click (OpenPinMenu).
        // Uses raw Unity input polling (Input.GetMouseButtonDown/Up), NOT IMGUI's Event.current.type - the
        // EventType.MouseDown/MouseUp approach (matching Dialog_GenerateLoadouts' Role Mode pattern) did not
        // actually trigger in this popup in practice. Polling sidesteps whatever IMGUI event-dispatch quirk that
        // was; DrawDragChipAndReset also force-clears dragPawn whenever the button isn't held at all, so a missed
        // "up" frame can never leave the drag stuck.
        private void HandleDragHandle(Rect handle, Pawn pawn)
        {
            if (assignPlan == null) return;
            AssignPlanEntry e = assignPlan.entries.FirstOrDefault(x => x.pawn == pawn);
            if (e == null || e.cannotFight) return;   // can't-fight colonists never carry a loadout to swap
            if (dragPawn == null && Mouse.IsOver(handle) && Input.GetMouseButtonDown(0))
                dragPawn = pawn;
        }

        private void HandleDropTarget(Rect area, Pawn pawn)
        {
            if (dragPawn == null || dragPawn == pawn || !Mouse.IsOver(area)) return;
            Widgets.DrawBox(area, 2);
            if (Input.GetMouseButtonUp(0))
            {
                SwapPlanned(dragPawn, pawn);
                dragPawn = null;
            }
        }

        // Swap two colonists' planned loadouts by pinning each to the other's - a deliberate override, so the swap
        // survives later slider tweaks (Auto in the pin menu hands a colonist back to the solver).
        // A planned weapon WITHOUT a template gets one created for this - the same path as the manual pick in the
        // weapon window (materializeWeapon). Previously the swap rejected exactly these rows, and since lazy
        // creation that is the normal case: dragged, "not possible", with a reference to a "(new)" marker that
        // no longer exists.
        private void SwapPlanned(Pawn a, Pawn b)
        {
            if (assignPlan == null) return;
            AssignPlanEntry ea = assignPlan.entries.FirstOrDefault(x => x.pawn == a);
            AssignPlanEntry eb = assignPlan.entries.FirstOrDefault(x => x.pawn == b);
            if (ea == null || eb == null) return;
            if (ea.cannotFight || eb.cannotFight)
            {
                Messages.Message("CER_SquadSetup_CantSwapCannotFight".Translate(), MessageTypeDefOf.RejectInput, false);
                return;
            }
            Loadout PlannedOf(AssignPlanEntry e) => e.plannedVirtual && e.plannedMain != null
                ? Dialog_GenerateLoadouts.EnsureDefaultLoadout(e.plannedMain) : e.planned;
            bool concreteA = ea.plannedVirtual ? ea.plannedMain != null : ea.planned != null;
            bool concreteB = eb.plannedVirtual ? eb.plannedMain != null : eb.planned != null;
            if (!concreteA || !concreteB)
            {
                Messages.Message("CER_SquadSetup_BothColonistsNeedConcretePlan".Translate(), MessageTypeDefOf.RejectInput, false);
                return;
            }
            bool sameVirtual = ea.plannedVirtual && eb.plannedVirtual && ea.plannedMain == eb.plannedMain;
            if (sameVirtual || (!ea.plannedVirtual && !eb.plannedVirtual && ea.planned == eb.planned))
            {
                Messages.Message("CER_SquadSetup_BothColonistsSameLoadout".Translate(), MessageTypeDefOf.RejectInput, false);
                return;
            }
            // Create only after both checks: a rejected gesture must not leave a template behind.
            Loadout la = PlannedOf(ea), lb = PlannedOf(eb);
            if (la == null || lb == null) return;
            pinned[a] = lb;
            pinned[b] = la;
            assignDirty = true;
            Messages.Message("CER_SquadSetup_SwappedPlannedLoadouts".Translate(a.LabelShortCap.ToString(), b.LabelShortCap.ToString()), MessageTypeDefOf.TaskCompletion, false);
        }

        // Floating chip that follows the cursor while dragging + the end-of-frame reset when released over empty
        // space. Called inside the assign scroll view so its coordinates match the rows'.
        private void DrawDragChipAndReset()
        {
            if (dragPawn != null)
            {
                AssignPlanEntry e = assignPlan?.entries.FirstOrDefault(x => x.pawn == dragPawn);
                Vector2 mp = Event.current.mousePosition;
                Rect chip = new Rect(mp.x + 12f, mp.y + 6f, 220f, 26f);
                Widgets.DrawBoxSolid(chip, new Color(0.15f, 0.15f, 0.17f, 0.95f));
                // The same chip exists a second time in Dialog_AssignLoadouts (DrawDragChip) and goes through
                // LoadoutDisplayName there - here it still showed the raw label including its prefix.
                string lab = dragPawn.LabelShortCap + "  ->  " + (e != null && e.planned != null ? LoadoutUtility.LoadoutDisplayName(e.planned) : "?");
                GUI.color = new Color(0.85f, 0.9f, 1f);
                Widgets.Label(new Rect(chip.x + 6f, chip.y + 3f, chip.width - 12f, 22f), lab.Truncate(208f));
                GUI.color = Color.white;
            }
            // Force-clear whenever the button isn't held at all (not just on the exact "up" frame) - a safety net
            // so a missed release event (focus loss, dropped over a non-row area, etc.) can never leave the drag
            // permanently stuck.
            if (dragPawn != null && !Input.GetMouseButton(0)) dragPawn = null;
        }

        // Pin FloatMenu: every candidate loadout, best score first; a pinned pawn gets an Auto option to hand them
        // back to the solver.
        private void OpenPinMenu(Pawn pawn)
        {
            // How many copies of a weapon does this SAME live preview already plan to hand to OTHER
            // colonists, that real committed game state (pawn.GetLoadout(), which ComputeAvailability's own
            // scan reads) doesn't know about yet? "kept" entries mirror their pawn's real current state
            // exactly, so they're already reflected there - only a genuinely NEW planned pick is an extra
            // claim. Without this, a single-copy weapon read as fully available to every pawn's picker
            // simultaneously, since none of those picks are real yet (2026-07-31: picking a
            // scarce weapon here showed no warning at all that another colonist's own planned row already
            // claimed the one copy that exists).
            Dictionary<ThingDef, int> plannedElsewhere = new Dictionary<ThingDef, int>();
            if (assignPlan != null)
                foreach (AssignPlanEntry other in assignPlan.entries)
                    if (other.pawn != pawn && !other.kept && other.plannedMain != null)
                        plannedElsewhere[other.plannedMain] = (plannedElsewhere.TryGetValue(other.plannedMain, out int c) ? c : 0) + 1;
            int ExtraReservedFor(ThingDef w) => w != null && plannedElsewhere.TryGetValue(w, out int n) ? n : 0;

            // The same filter as the solver - see AssignPlanner.RoleModeAllows. Exception as in the Assign tab:
            // whatever the colonist CURRENTLY carries always stays selectable, otherwise an existing assignment
            // could no longer be confirmed.
            // DisplayLoadout, not the raw assignment (found in a cross-check): as soon as an assignment has really
            // been applied, the colonist holds the hidden personal anchor and not the role itself - so the
            // comparison below stopped matching exactly when it was needed. Visibly: with the toggle on "No
            // roles", a colonist just assigned "[Role] Rifleman" was missing his OWN role in the list - confirming
            // was no longer possible.
            // The Assign tab has long done it this way at the same spot (Dialog_AssignLoadouts,
            // OpenLoadoutPickMenu already gets currentLoadout unwrapped) - this was the second occurrence of the
            // same question with a second calculation.
            Loadout pawnCurrent = AmmoAssignmentUtility.DisplayLoadout(pawn?.GetLoadout());

            // Since lazy creation this is the weapon picker instead of a FloatMenu over the candidate loadouts: what
            // gets pinned is a WEAPON (the shared template is created on pin via EnsureDefaultLoadout - a pin is an
            // explicit player choice, so the template is needed) or, as before, a role or a hand-built loadout
            // from its own section.
            RoleAssignMode rm = assignOwner != null ? assignOwner.roleMode : RoleAssignMode.Mixed;
            // Generated templates COMPLETELY out, including the current one (as in the assignment tab: otherwise
            // the same weapon appears twice - as a loadout row and as a marked weapon).
            Func<List<Loadout>> loadoutOptions = () => candidates
                .Where(c => !c.isVirtual && c.loadout != null
                    && (AssignPlanner.RoleModeAllows(c, rm) || c.loadout == pawnCurrent)
                    && !LoadoutUtility.IsGeneratedTemplate(c.loadout))
                // Sort by the DISPLAYED name, not the stored one: otherwise the roles all sort under "[" and the
                // templates under "W", and the list doesn't read alphabetically.
                .OrderBy(c => LoadoutUtility.LoadoutDisplayName(c.loadout))
                .Select(c => c.loadout)
                .ToList();

            Loadout curDisplay = pawnCurrent;
            ThingDef curWeapon = null;
            if (curDisplay != null && LoadoutUtility.IsGeneratedTemplate(curDisplay)
                && Dialog_AssignLoadouts.AnalyzeLoadout(curDisplay, out ThingDef curMain, out _, out _, out _))
            {
                curWeapon = curMain;
                curDisplay = null;
            }

            Pawn localPin = pawn;
            // Weapon and loadout go through the SAME path - single-select mode materializes a weapon itself and
            // always returns a loadout; a pin is an explicit player choice, so the template is needed.
            Action<Loadout> pickedSingle = lo => { pinned[localPin] = lo; assignDirty = true; };
            // "Auto" releases the pin - the solver decides on its own again. ALWAYS offered ("None" as a weapon
            // choice had disappeared - the row depended on "only when pinned", so the freshly opened window was
            // missing its top row entirely). Without a pin the click does nothing - Remove on a missing key is
            // harmless.
            Action unpin = () => { pinned.Remove(localPin); assignDirty = true; };

            // "Save combination as role" is OUT of the picker - the paths via cards/compact rows and the loadout
            // tooltip remain.

            // ONE window for both cases - the same Dialog_PickPacks look as the sidearm checklist, in single-select
            // mode; the old pin menu's scarcity line hangs off the availability tooltip as reservedFn.
            List<ThingDef> pinChoices = rm != RoleAssignMode.RolesOnly
                ? LoadoutUtility.MainWeaponChoices(curWeapon) : new List<ThingDef>();
            Find.WindowStack.Add(new Dialog_PickPacks(
                "CER_PickWeapon_SubtitlePin".Translate(pawn.LabelShortCap.ToString()).ToString(),
                () => new List<Loadout>(), updated => { },
                windowTitle: "CER_PickWeapon_Title".Translate().ToString(),
                itemColumnLabel: "CER_GenerateLoadouts_WeaponBlocksHeader".Translate().ToString(),
                allOptions: loadoutOptions,
                emptyMessage: "CER_PickWeapon_NothingMatches".Translate().ToString(),
                displayLabel: lo => LoadoutUtility.LoadoutDisplayName(lo) ?? "?",
                // Separate sections "Vanilla loadouts" and "Roles".
                groupLabel: lo => (RoleUtility.IsRoleLoadout(lo)
                    ? "CER_PickPacks_GroupRoles".Translate().ToString()
                    : "CER_PickPacks_GroupVanilla".Translate().ToString()),
                scoreFn: SidearmAssignmentUtility.ScoreOf,
                availDefFn: Dialog_AssignLoadouts.RepresentativeIcon,
                sourceLabel: pawn.LabelShortCap,
                weaponOptions: () => pinChoices,
                materializeWeapon: Dialog_GenerateLoadouts.EnsureDefaultLoadout,
                singleSelect: true,
                onPickedSingle: pickedSingle,
                onNonePicked: unpin,
                noneLabel: "CER_SquadSetup_AutoUnpinOption".Translate().ToString(),
                currentSingle: curDisplay, currentWeaponSingle: curWeapon,
                weaponGroupLabel: d => (d.IsRangedWeapon && !d.IsMeleeWeapon
                    ? "CER_AssignLoadouts_RangedHeader".Translate().ToString()
                    : "CER_AssignLoadouts_MeleeHeader".Translate().ToString()),
                reservedFn: ExtraReservedFor,
                // Copy source: the current assignment - pasting goes through the paste column of the preview rows
                // (sets a pin there).
                copyCurrent: () => pawnCurrent != null && !pawnCurrent.defaultLoadout ? pawnCurrent : null,
                // Disabled weapon: the preview sits right behind - recompute silently.
                //
                // `assignDirty = true` alone was not enough (a weapon disabled in the picker stayed in the
                // loadout): the solver does recompute then, but over the candidate list built ON OPEN - and that
                // is exactly where disabled weapons are filtered out (BuildAssignStaticData, four loops). So the
                // weapon was still on offer unchanged and promptly planned again.
                // carriesDisabled in the solver only covers the other case: a lock hits a weapon the colonist
                // ALREADY has assigned - then it releases the row. A mere PLAN carries nothing, carriesDisabled
                // doesn't see it.
                onDisabledChanged: () =>
                {
                    // Release pins on a now-disabled weapon, otherwise the lock survives exactly where the player just
                    // set it - the candidate list no longer knows the weapon, but the pin holds it on the colonist.
                    DropPinsOnDisabledWeapons();
                    BuildAssignStaticData();   // sets assignDirty itself
                }));
        }

        // Per-category ingredient bill of everything the current plan would queue for crafting (same batch math as
        // the Supply popup's), keyed by requirement category - feeds the "Preview" strip below the assign list
        // with one labeled line per component (Main weapons / Sidearms / Grenades / Shields / Ammunition) instead
        // of one opaque combined total, plus a final grand total.
        //
        // Main weapon reuses the solver's own reservation-aware verdict (SupplyRequirementsFor -> e.needsCraft), so
        // that part can never disagree with what Apply/Supply actually does with it. Sidearms/grenades/shields/ammo
        // do NOT reuse SupplyRequirementsFor's per-entry status here - that check only ever asks "is at least one
        // available" independently for every single pawn, so several pawns sharing one scarce buckler/grenade/ammo
        // type each read it as covered even though the colony doesn't actually own enough for all of them together
        // (a real case: 3 pawns each wanting a Buckler with only 2 in stock all showed "in stock"). Instead this
        // aggregates the WHOLE plan's demand first via Dialog_AssignLoadouts.ComputeMissingSplit/LoadoutUtility.
        // ComputeAmmoNeedForLoadouts - the exact same functions the real Supply popup's request is built from - and
        // nets each def against owned stock ONCE, so the preview total is the true shortfall, not an undercount.
        // The cached entry point - see the fields costCache/assignPlanVersion above for the two guards.
        // All display call sites go through here; the calculation itself stays unchanged below.
        private Dictionary<ReqCategory, Dictionary<ThingDef, int>> AssignCraftCostsByCategory()
        {
            if (costCache != null && costCacheVersion == assignPlanVersion
                && Time.realtimeSinceStartup - costCacheTime < 0.5f) return costCache;
            costCache = ComputeAssignCraftCostsByCategory();
            costCacheVersion = assignPlanVersion;
            costCacheTime = Time.realtimeSinceStartup;
            return costCache;
        }

        private Dictionary<ReqCategory, Dictionary<ThingDef, int>> ComputeAssignCraftCostsByCategory()
        {
            var byCategory = new Dictionary<ReqCategory, Dictionary<ThingDef, int>>();
            if (assignPlan == null) return byCategory;

            void AddCost(ReqCategory cat, ThingDef def, int count)
            {
                // Ammo needs its own check: the general one only asks "is there a recipe", the ammo variant also
                // requires a BUILT workbench. Without it a researched ammo without a suitable bench counted into
                // the cost bar and was skipped on generation - sum and result didn't match.
                if (def == null || count <= 0) return;
                bool producible = LoadoutUtility.IsAmmoDef(def)
                    ? Dialog_AssignLoadouts.AmmoProducibleNow(def)
                    : Dialog_AssignLoadouts.ProducibleNow(def);
                if (!producible || LoadoutUtility.WeaponHasActiveBill(def)) return;
                RecipeDef r = LoadoutUtility.GetRecipeFor(def);
                if (r == null) return;
                int yield = r.products != null && r.products.Count > 0 ? Mathf.Max(1, r.products[0].count) : 1;
                int batches = Mathf.CeilToInt((float)count / yield);
                if (!byCategory.TryGetValue(cat, out var costs)) byCategory[cat] = costs = new Dictionary<ThingDef, int>();
                foreach (ThingDefCountClass ing in LoadoutUtility.GetRecipeIngredients(r))
                    if (ing != null && ing.thingDef != null)
                        costs[ing.thingDef] = (costs.TryGetValue(ing.thingDef, out int c) ? c : 0) + ing.count * batches;
            }

            // Main weapon only stays per-pawn - e.needsCraft is a genuine per-pawn, reservation-aware verdict
            // (AssignPlanner's own sequential stock consumption), never a shared plan-wide total the way
            // Sidearm/Grenade/Shield/Ammo/Pack now are (see below).
            foreach (AssignPlanEntry e in assignPlan.entries)
            {
                if (e.kept) continue;
                foreach (SupplyReqItem item in SupplyRequirementsFor(e))
                    if (item.category == ReqCategory.MainWeapon && item.status == ReqStatus.Missing)
                        AddCost(ReqCategory.MainWeapon, item.icon, item.neededCount);
            }

            // Sidearm/Grenade/Shield/Ammo/Pack (2026-08-01 fix): read the SAME plan-wide shortfall
            // dictionaries SupplyRequirementsFor/SidearmRequirementsFor/PackRequirementsFor themselves
            // already consult (BuildPlanWideShortfalls, 2026-07-31) - ONCE PER DEF, not once per pawn row.
            // The old code here iterated each pawn's own SupplyReqItem list for these categories, but every
            // pawn sharing a scarce item reports the SAME already-combined neededCount - summing that once
            // per pawn who has it multiplied the real cost by however many pawns shared it. A SEPARATE
            // secMissing/shieldMissing/ammoNeed re-derivation used to run right after that and ADD ON TOP,
            // a second, independent layer of the exact same double-counting bug. Both are gone now - this
            // is the one and only place these 5 categories' cost gets added.
            foreach (var kv in planWideSecMissing)
                AddCost(LoadoutUtility.IsThrownExplosive(kv.Key) ? ReqCategory.Grenade : ReqCategory.Sidearm, kv.Key, kv.Value);
            foreach (var kv in planWideShieldMissing)
                AddCost(ReqCategory.Shield, kv.Key, kv.Value);
            foreach (var kv in planWideAmmoMissing)
                AddCost(ReqCategory.Ammo, kv.Key, kv.Value);
            foreach (var kv in planWidePackItemMissing)
                AddCost(ReqCategory.Pack, kv.Key, kv.Value);
            foreach (var kv in planWidePackGenericMissing)
            {
                ThingDef rep = LoadoutUtility.RepresentativeIconDef(kv.Key);
                if (rep != null) AddCost(ReqCategory.Pack, rep, kv.Value);
            }

            return byCategory;
        }

        // Main weapon only - the sidearm has its own dedicated column now (SidearmColX), so showing it here
        // too would just duplicate it. Deliberately ignores LoadoutMainAndSide's own "side" pick (an
        // old-style loadout with 2 weapons baked directly into one object) for the same reason - whatever
        // this pawn carries as a sidearm reads from the Sidearm column, not from here.
        private void DrawLoadoutCell(Rect cell, Loadout lo, string suffix)
        {
            // Defensive unwrap: a raw hidden anchor should never reach this far (BuildAssignStaticData now
            // excludes them from candidates), but showing DisplayLoadout's real-template label here too costs
            // nothing and keeps this cell honest even if some other path ever hands it an anchor directly.
            Loadout display = AmmoAssignmentUtility.DisplayLoadout(lo) ?? lo;
            float ix = cell.x;
            LoadoutUtility.LoadoutMainAndSide(lo, out ThingDef pMain, out _);
            if (pMain != null) { Widgets.ThingIcon(new Rect(ix, cell.y + 2f, 24f, 24f), pMain); ix += 26f; }
            // The shield is part of the fighting kit too - show it next to the weapon instead of hiding it in the tooltip.
            ThingDef pShield = lo.Slots != null
                ? lo.Slots.Select(s => s.thingDef).FirstOrDefault(d => d != null && LoadoutUtility.IsShieldApparel(d))
                : null;
            if (pShield != null) { Widgets.ThingIcon(new Rect(ix, cell.y + 2f, 24f, 24f), pShield); ix += 26f; }
            // CellLabel instead of the raw label: the "Planned main weapon" column still showed the prefix -
            // 'W| M1 Garand', 'W| Henry Big Boy', ... This spot was overlooked in an earlier refactor: the roles
            // next to it had long read cleanly, only the weapon templates didn't.
            // markerShownElsewhere (that is inside CellLabel) is right here - the weapon icon sits just left of
            // it, so the prefix would say the same thing a second time and eat width.
            string label = LoadoutUtility.CellLabel(display) + (suffix ?? "");
            Widgets.Label(new Rect(ix + 2f, cell.y + 5f, cell.xMax - ix - 4f, 24f), label.Truncate(cell.xMax - ix - 8f));
            TooltipHandler.TipRegion(cell, LoadoutUtility.LoadoutContentsTooltip(lo));
        }

        // ================================================================================================
        // Apply
        // ================================================================================================

        private string ApplyLabel()
        {
            int changes = assignPlan?.Changes ?? 0;
            if (changes == 0)
                return onDone != null ? "CER_SquadSetup_ContinueButton".Translate().ToString()
                    : "CER_SquadSetup_ApplyLabel".Translate().ToString() + "CER_SquadSetup_ApplyNothingSuffix".Translate();
            string changeWord = changes == 1 ? "CER_SquadSetup_ChangeSingular".Translate() : "CER_SquadSetup_ChangePlural".Translate();
            return "CER_SquadSetup_ApplyChangesLabel".Translate(changes.ToString(), changeWord);
        }

        // Whether ApplyLabel's own zero-count branch is currently showing - shared by ApplyEnabled's tooltip
        // (the button itself is never disabled anymore, but the hover hint should still say so when true).
        private bool ApplyHasWork()
        {
            return (assignPlan?.Changes ?? 0) > 0;
        }

        // Never hard-disabled (2026-08-02) - the "Changes" dirty-detection this
        // button used to gate on has real, confirmed blind spots (e.g. a kept pawn's already-assigned pack
        // gaining new content elsewhere never flips Changes, even though there's genuinely something to
        // review/produce) - a fully greyed-out button gave the player no way to force a re-check when that
        // detection misses something. ApplyLabel's own "(nothing to apply)" suffix communicates the state
        // instead of GUI.enabled=false; clicking through with truly zero changes is a harmless no-op
        // (ApplyPreviewPlan skips every kept/unchanged entry).
        private bool ApplyEnabled() => true;

        // Re-shows THIS SAME instance - the Back target for the Supply popup that appears after Apply. Used to
        // reconstruct a fresh Dialog_SquadSetup here (2026-08-01: pressing Back after Apply
        // silently discarded every pin/lock/slider edit, as if the assign step had never been touched - it
        // must stay consistent). That was wrong for this specific hop: unlike the
        // OTHER Back buttons in the chain (which genuinely need to reflect whatever the PRECEDING step just
        // changed, so reconstructing fresh there is correct), nothing upstream of Assign changes between
        // Apply and this popup's own Back - there is nothing to re-derive, only state to lose. Close() doesn't
        // invalidate the instance (PostClose already ran with applied=true, so re-showing it fires no spurious
        // cancel bookkeeping), so simply re-adding `this` preserves every field exactly as the player left it.
        private Action ReopenAssignStep() => () => Find.WindowStack.Add(this);

        // "Override ammo" (2026-08-01): fresh per-pawn ammo picks for every non-kept, non-Role
        // entry in the CURRENT assignPlan, computed the same mech-slider-aware way Generate already does
        // (ChooseAmmoPicks) - handed to Dialog_AssignLoadouts.ApplyPreviewPlan to actually write onto each
        // pawn's personal anchor. Roles excluded (fixed by design, same as packs/shields/explosives already
        // exclude them - see BuildPlanWideShortfalls/SupplyRequirementsFor's matching exclusion).
        private Dictionary<Pawn, List<Dialog_GenerateLoadouts.AmmoPick>> BuildAmmoOverrides()
        {
            if (!overrideAmmo || assignPlan == null) return null;
            var result = new Dictionary<Pawn, List<Dialog_GenerateLoadouts.AmmoPick>>();
            foreach (AssignPlanEntry e in assignPlan.entries)
            {
                if (e.kept || e.planned == null || RoleUtility.IsRoleLoadout(e.planned)) continue;
                LoadoutUtility.LoadoutMainAndSide(e.planned, out ThingDef mw, out _);
                if (mw == null || LoadoutUtility.IsShieldApparel(mw)) continue;
                List<Dialog_GenerateLoadouts.AmmoPick> picks = ChooseAmmoPicks(mw);
                // The sidearms too (found while chasing a different issue): Dialog_AssignLoadouts.ApplyPreviewPlan
                // deletes EVERY ammo row from the colonist's anchor on Apply and writes exactly this list, and an
                // ammo row of the anchor's own displaces any inherited one (Patch_Loadout_MultiParentSlots).
                // As long as only the main weapon was in here, "Override ammo" (ON by default) thus meant: the
                // sidearm ends up without ammo - even the one SetPawnSidearms had explicitly seeded seconds
                // earlier (SeedOwnAmmoForNewlyAttached). Self-ammo sidearms (grenades, javelins) are weapon demand,
                // not ammo, and stay out - ChooseAmmoPicks returns the weapon itself for them, and that doesn't
                // belong in an ammo row.
                if (e.plannedSidearms != null)
                    foreach (Loadout sa in e.plannedSidearms)
                    {
                        if (sa?.Slots == null) continue;
                        foreach (LoadoutSlot s in sa.Slots)
                        {
                            ThingDef sw = s.thingDef;
                            if (sw == null || !sw.IsWeapon || LoadoutUtility.IsSelfAmmoWeapon(sw)) continue;
                            if (LoadoutUtility.GetAmmoSet(sw) == null) continue;
                            foreach (Dialog_GenerateLoadouts.AmmoPick extra in ChooseAmmoPicks(sw))
                                if (extra.def != null && !picks.Any(p => p.def == extra.def)) picks.Add(extra);
                        }
                    }
                // Virtual sidearms (lazy creation): the same ammo seeding, just straight from the weapon instead of
                // from a template - that is only created on Apply.
                if (e.plannedSidearmDefs != null)
                    foreach (ThingDef sw in e.plannedSidearmDefs)
                    {
                        if (!sw.IsWeapon || LoadoutUtility.IsSelfAmmoWeapon(sw)) continue;
                        if (LoadoutUtility.GetAmmoSet(sw) == null) continue;
                        foreach (Dialog_GenerateLoadouts.AmmoPick extra in ChooseAmmoPicks(sw))
                            if (extra.def != null && !picks.Any(p => p.def == extra.def)) picks.Add(extra);
                    }
                result[e.pawn] = picks;
            }
            return result;
        }

        private void DoApply()
        {
            applied = true;
            Close();
            assignOwner.ApplyPreviewPlan(assignPlan, keepAssigned, redistribute, ReopenAssignStep(), BuildAmmoOverrides());
        }

        public override void PostClose()
        {
            base.PostClose();
            CERUiState ui = RoleUtility.Ui();
            // Sliders are NO LONGER written back - the next open starts at the default anyway; only the checkboxes
            // persist (in the save).
            ui.mechAffinity = mech;   // for the ammo choice on generation until the next open
            ui.asKeepAssigned = keepAssigned; ui.asRedistribute = redistribute;
            ui.asOverrideAmmo = overrideAmmo;
            // Back and Skip are navigation within the chain, not a cancel - the abort bookkeeping must not
            // fire for either. Skip continues the chain exactly like a 0-checked Equipment Packs step does.
            if (skipped)
            {
                onDone?.Invoke(0);
            }
            else if (!applied && !wentBack)
            {
                assignOwner.CancelArmAtAssign();
                onCancel?.Invoke();
            }
        }
    }
}
