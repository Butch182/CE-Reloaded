using System;
using System.Collections.Generic;
using System.Linq;
using RimWorld;
using UnityEngine;
using Verse;
using CombatExtended;

namespace CEReloaded
{
    // One loadout the solver can hand out, with everything scoring needs precomputed once per popup open.
    public class AssignCandidate
    {
        public Loadout loadout;
        public ThingDef mainWeapon;
        public float score;          // general weapon quality (0..~111), ammo-independent - the anchor
        public float fleshScore;     // computed damage vs flesh (0..100)
        public float mechScore;      // computed damage vs mechanoids (0..100)
        public bool isRanged;
        public WeaponGroup group;
        public float mechAffinity;   // 0 = anti-flesh, 0.5 = neutral, 1 = anti-mech
        public bool producible;      // recipe researched AND a suitable workbench is built
        public bool isRole;          // a "[Role]" loadout
        public bool hasMelee;        // the loadout carries a melee weapon somewhere (main or sidearm)
        public bool hasRanged;       // the loadout carries a ranged weapon somewhere (main or sidearm)
        // Virtual candidate: a weapon the colony owns or can produce that has NO loadout yet (loadout == null).
        // Picking it creates a default loadout on Apply - this is what lets "Craft new weapons" reach beyond the
        // existing loadout list instead of settling for whatever scraps happen to have one.
        public bool isVirtual;
        // DISABLED by the player: the loadout or role stays in the list - the picker must be able to show it
        // greyed out, otherwise there would be no way back to re-enable it - but the solver skips it. Disabled
        // WEAPONS never become candidates in the first place (Dialog_SquadSetup.BuildAssignStaticData filters
        // them), because their row in the picker comes from a different source.
        public bool isDisabled;
    }

    public class AssignPlanEntry
    {
        public Pawn pawn;
        public Loadout current;      // null when the pawn has no real loadout
        public Loadout planned;      // solver result; null = left without a loadout (or a virtual pick, see below)
        public ThingDef plannedMain; // the planned loadout's main weapon (for supply checks)
        public List<Loadout> plannedPacks = new List<Loadout>();  // Equipment Packs carried alongside the weapon - a fitting subset, unless a Role already bakes its own in
        public List<Loadout> plannedSidearms = new List<Loadout>();   // extra Weapon Loadout(s) attached via multi-parent (SidearmAssignmentUtility)
        // Sidearm/grenade planned as a bare WEAPON because no template existed at planning time (lazy
        // creation). Never materialized in the preview - only ApplyPreviewPlan turns it into the shared
        // template via EnsureDefaultLoadout and moves it into plannedSidearms.
        public List<ThingDef> plannedSidearmDefs;
        public bool plannedVirtual;  // picked a weapon with no loadout yet: a default loadout is created on Apply
        // Did the PLAYER set this pack/sidearm set explicitly (preview cell, paste, copy)? The distinction is
        // load-bearing, not cosmetic: for the solver an EMPTY list means "nothing picked", for the player it
        // means "take everything away". Without this flag WantsPackWrite can only get one of the two right.
        public bool packPinned;
        public bool sidearmPinned;
        // A third case of the same question: the solver DID pick for this row, but "Respect max bulk" removed
        // EVERYTHING. The list is then empty just like "nothing picked" - and without this flag the colonist
        // kept today's heavier packs while the cap and the preview assumed he carries nothing. Measured: with
        // the box ticked a colonist ended up at bulk 48.4/33.5, i.e. HIGHER than without it (41.2/33.5),
        // because without the box the lighter solver packs would have been written, and with it the old seven
        // stayed attached. The comment on EffectivePacks used to list this case as "deliberately open"; it no
        // longer is - it breaks the box's promise ("don't assign more than can be carried").
        // Apply would really write this row's pack/sidearm set (planned set differs from what the pawn carries
        // right now, and there IS a template to hang it on). Kept as a stored flag rather than a live compare,
        // because AssignPlan.Changes is read from OnGUI and the two getters behind it walk the pawn's
        // multi-parent chain and allocate - see AssignPlanner.RefreshExtrasChanged for who refreshes it.
        public bool extrasChanged;
        public bool kept;            // locked or keep-assigned: untouched by the solver
        public bool pinned;          // explicit player override (Dialog_SquadSetup) - a stronger "kept" with a chosen loadout
        public bool needsCraft;      // planned main weapon is not in stock (would have to be produced)
        // What this colonist carries AFTER Apply, and his limits (mass/bulk as separate preview columns).
        // Filled in the same loop that raises the overload warning - one question, one calculation: what the
        // bar shows is exactly what the warning means. Without a plan (kept/skipped) it holds what he carries
        // TODAY.
        public float previewMass, previewBulk, capMass, capBulk;
        // Did ReweighEntry REALLY weigh this row (then the overload box may list it), or did it stay at the
        // "current state" default? Stored so RefreshOverloadProblem can rebuild the box from the entry fields
        // at any time - including on an in-place pin.
        public bool previewWeighed;
        public bool cannotFight;     // permanently incapable of violence: skipped, never assigned

        // "Apply has something to write for this row" - the ONE definition, shared by the Apply button's own
        // count (AssignPlan.Changes) and the assign list's "show changes only" filter, which used to carry a
        // hand-copied duplicate of it and therefore hid exactly the same rows the button miscounted.
        public bool IsChange => !kept && (planned != current || plannedVirtual || extrasChanged);
    }

    // What the UI should offer to resolve a problem. The solver stays UI-free and only tags intent; the popup maps
    // the tag to an actual button + navigation.
    // Since the lazy-creation rollback there is only ONE jump target: the Roles tab, when "Roles only" is on
    // and no role exists. "CreateLoadouts" jumped to the weapon list, which no longer exists.
    public enum ProblemAction { None, OpenRolesTab }

    // One entry in the reactive problem list: the message and an optional suggested action.
    public class AssignProblem
    {
        public string text;
        public ProblemAction action;
        // Marks THE overload line so RefreshOverloadProblem can replace it in place on an in-place pin instead
        // of rebuilding the whole box.
        public bool isOverloadLine;
        public AssignProblem(string text, ProblemAction action = ProblemAction.None)
        {
            this.text = text; this.action = action;
        }
    }

    // The pure result of one solver run: per-pawn outcome plus the interactive problem list. Nothing is applied.
    public class AssignPlan
    {
        public List<AssignPlanEntry> entries = new List<AssignPlanEntry>();
        public List<AssignProblem> problems = new List<AssignProblem>();
        // Counts the rows Apply will actually write. `extrasChanged` is the pack/sidearm half (2026-08-12 fix,
        // pinning ONLY a sidearm on an otherwise-kept colonist left the button at "(nothing to
        // apply)" although the cell's own tooltip promises "nothing applies until you press Apply") - such a row
        // legitimately has planned == current, since the pin never touches the main weapon, so the weapon-only
        // comparison below counted zero and the whole change read as non-existent.
        public int Changes => entries.Count(e => e.IsChange);
        public int CraftCount => entries.Count(e => !e.kept && e.needsCraft);
    }

    // Side-effect-free auto-assign solver. All four sliders enter one value function, so dragging a slider re-ranks
    // the same candidate pool live:
    //   base  = lerp(generalScore, lerp(fleshScore, mechScore, mech), tilt)   where tilt = |mech-0.5|*2 * MechTilt
    //           -> at the neutral middle it is the ammo-independent general weapon score (a top gun like the charge
    //              rifle wins); toward either end the computed flesh/mech damage table tilts the ranking so a weapon
    //              is only overtaken when it genuinely does more to the chosen target type.
    //   value = base
    //         - (1-craft) * 80 * (1-afford)   when the weapon must be made   (use existing <-> craft new)
    //         - (1-budget) * 50 * scarcity^2  when it must be made   (cheap <-> best weapon, SCARCITY of the
    //           ingredients, see LoadoutUtility.MaterialAffordability)
    //         - (1-budget) * 45 * cost/maxCost when it must be made  (cheap <-> best weapon, PRICE of the item -
    //           see CraftPenalty: without this term the slider did nothing in a colony with full stockpiles,
    //           because there every weapon is "affordable")
    //         - diversity * (55 * reuse + 8 * groupUse + 35 * overCap)   (always best <-> cover roles)
    //         + diversity * 25 for the first pick of an unfilled role group (SupportMG/Marksman/CQC/Launcher)
    // The ranged/melee slider shifts the shooter-vs-brawler threshold per pawn instead of scoring options.
    // Colonists permanently incapable of violence are skipped entirely (never assigned a combat loadout).
    public static class AssignPlanner
    {
        // How far the anti-flesh/anti-mech slider may pull the ranking away from the trusted general score at its
        // extremes (0..1). At 0.6 a fully specialized target-damage pick still keeps 40% of the general score, so a
        // dominant weapon stays competitive and is only dethroned by one that is clearly better against that target.
        private const float MechTilt = 0.6f;

        // One pawn's settled/pinned bookkeeping, carried between the two reservation passes in Compute (see there
        // for why the reservation needs two passes).
        private class PrepEntry
        {
            public Pawn pawn;
            public AssignPlanEntry entry;
            public bool isPinned, kept, settled, carriesMain, packPinned, sidearmPinned;
            public ThingDef mainW;
        }

        public static AssignPlan Compute(
            List<Pawn> pawns, Func<Pawn, float> rangedScore, Func<Pawn, float> meleeScore,
            List<AssignCandidate> allCandidates, Dictionary<ThingDef, int> owned,
            float craft, float mech, float melee, float diversity, float budget,
            RoleAssignMode roleMode, bool keepAssigned, bool redistribute,
            HashSet<Pawn> locked, Dictionary<Pawn, Loadout> pinned = null,
            Dictionary<Pawn, List<Loadout>> pinnedPacks = null, float sidearmCoverage = 0f,
            Dictionary<Pawn, List<Loadout>> pinnedSidearms = null, float packCoverage = 0f,
            float grenadeCoverage = 0f, float shieldCoverage = 0f)
        {
            AssignPlan plan = new AssignPlan();
            // Virtual candidates (weapons without a template) ALWAYS compete since lazy creation - the template
            // is an invisible background object that only comes into existence on Apply. The former opt-in
            // checkbox "Create new loadouts" went away together with the mandatory bulk creation.
            // `!c.isDisabled`: a disabled role/template stays in the candidate list so the picker can show it
            // greyed out and re-enable it - but the solver never hands it out. Exactly the same promise the
            // toggle makes for weapons.
            List<AssignCandidate> cands = allCandidates.Where(c => !c.isDisabled && RoleModeAllows(c, roleMode)).ToList();

            // Split pawns into settled (locked / keep-assigned / pinned) and targets; settled pawns reserve their
            // weapon so the solver never double-allocates it to someone else.
            Dictionary<ThingDef, int> stock = new Dictionary<ThingDef, int>(owned);
            List<AssignPlanEntry> targets = new List<AssignPlanEntry>();
            void Reserve(ThingDef d, int n)
            {
                if (d != null && stock.TryGetValue(d, out int c)) stock[d] = Mathf.Max(0, c - n);
            }

            // Build one PrepEntry per plannable pawn first (cannot-fight pawns are resolved immediately, same as
            // before). The actual stock reservation happens in TWO passes below - see the comment there for why.
            List<PrepEntry> prep = new List<PrepEntry>();
            foreach (Pawn p in pawns)
            {
                // Unwrapped past any hidden per-pawn "extra ammo" leaf (AmmoAssignmentUtility) - planning
                // compares against the real weapon template, not that bookkeeping object.
                Loadout cur = AmmoAssignmentUtility.DisplayLoadout(p.GetLoadout());
                if (cur != null && cur.defaultLoadout) cur = null;

                // Permanently incapable of violence (pacifist trait/backstory) or a baby: can never wield a weapon,
                // so leave whatever they have untouched and skip them. Temporary states (downed, anesthetised) are
                // NOT this, so an unconscious colonist is still planned normally.
                // EXCLUDED by the player means: the loadout is TAKEN AWAY on Apply - a colonist can't be
                // deleted, but his gear assignment can; he ends up without one. Previously he landed in the
                // branch below with planned=cur/kept=true, i.e. "leave untouched" - so the trash icon in the
                // row did not take anything away and was an empty gesture.
                // kept=false + planned=null is exactly the combination ApplyPreviewPlan's apply() has always
                // written as "remove assignment" (SetLoadout(none)); cannotFight stays true so the row is still
                // drawn as the player's decision and the solver leaves it alone.
                // plannedPacks stay his current ones: packs are pure supply and don't hang off the weapon
                // loadout - what gets taken away is the WEAPON, not his bandages.
                // Order matters: the FACT comes first. A pacifist who is also excluded falls into the lower
                // branch and stays untouched - his row icon is a dead lock with no way back, so silently taking
                // his loadout away here would be a trap.
                if (!LoadoutUtility.CannotFightByNature(p) && LoadoutUtility.IsExcludedFromArming(p))
                {
                    plan.entries.Add(new AssignPlanEntry { pawn = p, current = cur, planned = null, kept = false, cannotFight = true,
                        plannedPacks = AmmoAssignmentUtility.GetPawnPacks(p),
                        plannedSidearms = new List<Loadout>() });
                    continue;
                }
                if (LoadoutUtility.CannotFight(p))
                {
                    // A PACK pin reaches them too (2026-08-12): the preview's Pack cell is gated on
                    // CannotCarryAnything, not on CannotFight, so it is deliberately offered to a pacifist -
                    // but this branch returns before the pin lookup further down ever runs, so a pinned set
                    // used to be silently replaced by whatever they already carried on the next re-solve.
                    // (`kept` is settled for every entry by the sweep at the end of this method.)
                    List<Loadout> cfPinnedPacks = null;
                    bool cfPackPinned = pinnedPacks != null && pinnedPacks.TryGetValue(p, out cfPinnedPacks);
                    plan.entries.Add(new AssignPlanEntry { pawn = p, current = cur, planned = cur, kept = true, cannotFight = true,
                        plannedPacks = cfPackPinned ? cfPinnedPacks : AmmoAssignmentUtility.GetPawnPacks(p),
                    packPinned = cfPackPinned,
                        plannedSidearms = SidearmAssignmentUtility.GetPawnSidearms(p) });
                    continue;
                }

                // Pinned is a player override, not a "nothing to do" state: it must
                // stay OUT of `kept` (Changes/apply/produce-list all key off `kept` meaning "skip, planned==current
                // already handled") so the pin actually applies and shows up as a change. It IS excluded from the
                // solver's target list, exactly like a kept pawn, so it never gets reshuffled by a slider tweak.
                Loadout pinnedLoadout = null;
                bool isPinned = pinned != null && pinned.TryGetValue(p, out pinnedLoadout);
                // `locked` means "this pawn's kept-state is FLIPPED relative to the current global default" -
                // under keepAssigned it's an explicit per-pawn RELEASE (so one colonist can still be replanned
                // even though everyone else stays kept), under !keepAssigned it's the original explicit LOCK.
                // Dialog_SquadSetup clears the set whenever the global checkbox itself is toggled, so a lock
                // set while replanning never silently becomes a release once Keep-current is switched on (or
                // vice versa) - membership always means "differs from today's default", never a fixed sense.
                // "kept" fundamentally means "keep the CURRENT assignment as-is" - a pawn with no current
                // loadout has nothing to keep, so kept must never be true for them regardless of locked/
                // keepAssigned (2026-08-01: clicking the Assign step's own "lock
                // everyone" master toggle, with keepAssigned off and every pawn's cur==null - e.g. right
                // after a Back-navigation that wiped every assignment - added all of them to `locked`, which
                // under the !keepAssigned branch used to mean kept=true regardless of cur, freezing every
                // pawn at "nothing assigned, nothing planned" with ZERO way for the solver to ever plan them
                // again short of clicking the SAME toggle a second time to release them - Apply showed 0
                // changes and stayed that way until the player realized the toggle was itself the undo).
                // The keepAssigned==true branch already had this `cur != null` guard; it was just missing
                // from the other one.
                // Disabled weapon in the assignment: "keep" does not exist for such a row - under keepAssigned
                // the preview should replan exactly this row while all others stay. Uses the anchor's RAW slot
                // union so a disabled sidearm releases the row too.
                bool carriesDisabled = false;
                Loadout rawForDisabled = p.GetLoadout();
                if (rawForDisabled != null && !rawForDisabled.defaultLoadout)
                {
                    // The same applies to a disabled ROLE or TEMPLATE: the row is released whether the lock sits
                    // on a weapon inside it or on the loadout itself. Goes through DisplayLoadout because a
                    // personalized colonist hangs off his anchor, not directly off the template - the raw
                    // assignment would always be the anchor here.
                    if (LoadoutUtility.IsLoadoutDisabled(rawForDisabled)
                        || LoadoutUtility.IsLoadoutDisabled(AmmoAssignmentUtility.DisplayLoadout(rawForDisabled)))
                        carriesDisabled = true;
                    if (!carriesDisabled)
                        foreach (int epid in MultiParentUtility.GetExtraParentIds(rawForDisabled))
                        {
                            Loadout extra = LoadoutManager.Loadouts?.FirstOrDefault(l => l != null && l.UniqueID == epid);
                            if (LoadoutUtility.IsLoadoutDisabled(extra)) { carriesDisabled = true; break; }
                        }
                    if (!carriesDisabled && rawForDisabled.Slots != null)
                        foreach (LoadoutSlot ds in rawForDisabled.Slots)
                            if (ds.thingDef != null && ds.thingDef.IsWeapon && LoadoutUtility.IsWeaponDisabled(ds.thingDef)) { carriesDisabled = true; break; }
                }
                bool mainKept = !isPinned && cur != null && !carriesDisabled && (keepAssigned
                    ? !locked.Contains(p)
                    : locked.Contains(p));
                bool settled = mainKept || isPinned;
                ThingDef pinMain = null;
                if (isPinned) Dialog_AssignLoadouts.AnalyzeLoadout(pinnedLoadout, out pinMain, out _, out _, out _);
                ThingDef mainW = mainKept ? null : pinMain;
                if (mainKept && cur != null) Dialog_AssignLoadouts.AnalyzeLoadout(cur, out mainW, out _, out _, out _);

                // A manual pack/sidearm pin (Dialog_SquadSetup's own per-row pickers) is independent of the
                // weapon's kept/pinned state - a player can override just the pack or sidearm without also
                // pinning the weapon (settled/mainW/plannedMain above all stay keyed on `mainKept`, unrelated
                // to these). `entryKept` (2026-08-02 fix: pinning just a sidearm/pack on an otherwise-kept
                // pawn never actually applied) is the
                // NARROWER flag that actually lands on AssignPlanEntry.kept: it must also exclude a pack/
                // sidearm pin, exactly like it already excludes a weapon pin, since Dialog_AssignLoadouts.
                // ApplyPreviewPlan's own apply loop does a blanket `if (e.kept) continue;` that skipped the
                // ENTIRE entry - pack/sidearm pin included - whenever the main weapon itself stayed kept.
                List<Loadout> pinnedPackList = null;
                bool packPinned = pinnedPacks != null && pinnedPacks.TryGetValue(p, out pinnedPackList);
                List<Loadout> pinnedSidearmList = null;
                bool sidearmPinned = pinnedSidearms != null && pinnedSidearms.TryGetValue(p, out pinnedSidearmList);
                bool entryKept = mainKept && !packPinned && !sidearmPinned;

                AssignPlanEntry e = new AssignPlanEntry
                {
                    pawn = p,
                    current = cur,
                    kept = entryKept,
                    pinned = isPinned,
                    planned = isPinned ? pinnedLoadout : (mainKept ? cur : null),
                    plannedMain = isPinned ? pinMain : null,
                    // Settled (kept/pinned) pawns keep whatever pack(s) they already carry - the solver only
                    // freely (re-)picks packs for pawns whose weapon it is also freely (re-)picking, unless
                    // the player pinned the pack set directly.
                    plannedPacks = packPinned ? pinnedPackList : (settled ? AmmoAssignmentUtility.GetPawnPacks(p) : new List<Loadout>()),
                    // Same idea as packs: a settled pawn keeps whatever sidearm(s) they already carry; the
                    // solver only freely (re-)picks one for pawns whose main weapon it is also freely picking.
                    plannedSidearms = sidearmPinned ? pinnedSidearmList : (settled ? SidearmAssignmentUtility.GetPawnSidearms(p) : new List<Loadout>()),
                    packPinned = packPinned,
                    sidearmPinned = sidearmPinned
                };
                plan.entries.Add(e);
                prep.Add(new PrepEntry { pawn = p, entry = e, isPinned = isPinned, kept = mainKept, settled = settled, mainW = mainW, packPinned = packPinned, sidearmPinned = sidearmPinned });
            }

            // Pass 1: reserve every pawn's own carried gear that is NEVER going to be dropped for this plan - kept
            // pawns always (Apply skips them outright), and targets too when "Redistribute carried weapons" is off
            // (then nothing gets dropped for anyone). This must run for EVERY such pawn before pass 2 evaluates any
            // pinned claim, regardless of combat-score order: a single interleaved pass let a high-score pinned pawn
            // (processed first, since `pawns` is score-sorted) claim a weapon def that a low-score pawn was already
            // holding onto for good but had not been reserved yet - showing a false "in stock" for a copy that, in
            // reality, can never reach the pinned pawn because its actual holder never lets it go.
            foreach (PrepEntry x in prep)
            {
                if (x.settled || !redistribute)
                {
                    if (x.pawn.equipment != null && x.pawn.equipment.Primary != null)
                    {
                        Reserve(x.pawn.equipment.Primary.def, 1);
                        if (x.pawn.equipment.Primary.def == x.mainW) x.carriesMain = true;
                    }
                    if (x.pawn.inventory != null && x.pawn.inventory.innerContainer != null)
                        foreach (Thing t in x.pawn.inventory.innerContainer)
                            if (t.def != null && t.def.IsWeapon)
                            {
                                Reserve(t.def, t.stackCount);
                                if (t.def == x.mainW) x.carriesMain = true;
                            }
                }
                // A KEPT pawn's own main weapon is claimed here too, unconditionally (no stock check, matching the
                // original code) - this must land before ANY pinned pawn's stock check in pass 2, and being
                // unconditional it never depends on other pawns' order, so it is safe to do in pass 1.
                if (x.kept && x.mainW != null && !x.carriesMain) Reserve(x.mainW, 1);
            }

            // Pass 2: pinned claims against the now-final stock, still in score-priority order - competition among
            // pinned pawns for the same def is intentional (first-come-first-served by priority), unlike pass 1's
            // unconditional holds. A settled pawn who doesn't already hold their (current or pinned) main weapon
            // needs one from stock; check BEFORE reserving so needsCraft reflects availability at claim time.
            foreach (PrepEntry x in prep)
            {
                if (x.isPinned && x.mainW != null && !x.carriesMain)
                {
                    bool hasStock = stock.TryGetValue(x.mainW, out int s) && s > 0;
                    x.entry.needsCraft = !hasStock;
                    Reserve(x.mainW, 1);
                }
                if (!x.settled) targets.Add(x.entry);
            }
            HashSet<Pawn> packPinnedPawns = new HashSet<Pawn>(prep.Where(x => x.packPinned).Select(x => x.pawn));
            // Straight from the dictionary as well, not only from `prep`: cannotFight pawns never enter prep
            // (they are resolved in their own branch above), so a pacifist with a pinned pack was missing from
            // this set and the pack-only pass below promptly overwrote the pin with its own auto-pick.
            if (pinnedPacks != null) foreach (Pawn pp in pinnedPacks.Keys) packPinnedPawns.Add(pp);
            HashSet<Pawn> sidearmPinnedPawns = new HashSet<Pawn>(prep.Where(x => x.sidearmPinned).Select(x => x.pawn));

            // The "superior loadouts skipped" list (skippedSuperior) and its per-colonist shadow run were removed
            // without replacement because they made no sense. With them went PickBest's ignoreAvailability
            // parameter and the "Unobtainable and skipped" message in BuildProblems. Side effect: up to two full
            // PickBest runs less per colonist.

            // Shooter vs brawler per pawn: the melee slider shifts the threshold (right = melee wins ties and more).
            float shift = (melee - 0.5f) * 300f;
            Dictionary<Pawn, bool> isShooter = new Dictionary<Pawn, bool>();
            foreach (AssignPlanEntry e in targets)
            {
                float r = rangedScore(e.pawn);
                float m = meleeScore(e.pawn);
                isShooter[e.pawn] = r >= m + shift;
            }
            int shooterCount = isShooter.Values.Count(v => v);
            List<AssignPlanEntry> order = targets
                .OrderByDescending(e => isShooter[e.pawn] ? rangedScore(e.pawn) : meleeScore(e.pawn))
                .ToList();

            Dictionary<WeaponGroup, int> caps = Dialog_AssignLoadouts.BuildGroupCaps(shooterCount);
            Dictionary<WeaponGroup, int> usedInGroup = new Dictionary<WeaponGroup, int>();
            Dictionary<AssignCandidate, int> timesUsed = new Dictionary<AssignCandidate, int>();   // keyed by candidate (virtuals have no loadout)

            // The penalty for everything that must be BUILT first - one formula for main weapon, sidearm and
            // throwables (it used to exist three times verbatim in the three pickers; the cost extension below
            // would otherwise have landed in only one of them again).
            //
            // Two terms, two different questions:
            //   craft   "Use existing <-> Craft new"   -> depends on the SCARCITY of the ingredients
            //   budget  "Cheap weapons <-> Best weapon" -> depends on SCARCITY *and* PRICE
            //
            // The price term is newer: picking Economy produced exorbitant build costs. Before, BOTH sliders hung
            // on MaterialAffordability alone, i.e. on the stock/demand ratio - in a colony with full stockpiles
            // that is 1.0 for every weapon, both penalties drop to 0, and "Cheap weapons" at the far left changes
            // exactly nothing. That was precisely the state observed in testing.
            // Relative to the most expensive candidate of this run, not against an absolute silver threshold:
            // what counts as "expensive" depends on the tech era and the mod pack, and the slider should work in
            // a bow-and-spear colony just as well as between assault rifle and heavy MG.
            // Three separate reference values because these are three separate competitions: the three pickers
            // compare firearms only with firearms, melee only with melee and throwables only with throwables.
            // With ONE shared maximum the most expensive MG would have set the scale and every knife and grenade
            // would have landed at ~0 across the board - the slider would again distinguish nothing there,
            // exactly the bug this fix addresses.
            float maxCostRanged = 0f, maxCostMelee = 0f, maxCostGrenade = 0f;
            // How far apart are a competition's candidates in QUALITY? That is the scale the price term has to
            // measure up against (Economy still produced far too much after the first fix).
            //
            // The first fix only introduced the price term - correct, but incomplete. It was capped at 45
            // points while candidate scores reach up to ~111. If two weapons are 60 points apart, price can no
            // longer flip the decision no matter how far left the slider is - the "Economy" preset then still
            // plans the MG. The first check only verified that the slider ARRIVES (it does), not that it can
            // DECIDE.
            //
            // So the weight now follows the spread of the respective competition instead of a fixed number: at
            // the far left price may outvote any quality difference within the pool, at the far right it does
            // not count at all, proportionally in between. The old 45 remain as a floor - this change can only
            // make the slider stronger, never weaker.
            // Important: this applies only to weapons that must be PRODUCED (the caller only calls CraftPenalty
            // when !hasStock). Weapons in stock are completely unaffected - "cheap" here means "cheap to BUILD",
            // not "worse".
            float spreadRanged = 0f, spreadMelee = 0f, spreadGrenade = 0f;
            float minRanged = float.MaxValue, minMelee = float.MaxValue, minGrenade = float.MaxValue;
            float maxScoreRanged = 0f, maxScoreMelee = 0f, maxScoreGrenade = 0f;
            foreach (AssignCandidate c in cands)
            {
                float cc = LoadoutUtility.WeaponCraftCost(c.mainWeapon);
                if (Dialog_GenerateLoadouts.IsGrenade(c.mainWeapon))
                {
                    if (cc > maxCostGrenade) maxCostGrenade = cc;
                    if (c.score < minGrenade) minGrenade = c.score;
                    if (c.score > maxScoreGrenade) maxScoreGrenade = c.score;
                }
                else if (c.isRanged)
                {
                    if (cc > maxCostRanged) maxCostRanged = cc;
                    if (c.score < minRanged) minRanged = c.score;
                    if (c.score > maxScoreRanged) maxScoreRanged = c.score;
                }
                else
                {
                    if (cc > maxCostMelee) maxCostMelee = cc;
                    if (c.score < minMelee) minMelee = c.score;
                    if (c.score > maxScoreMelee) maxScoreMelee = c.score;
                }
            }
            spreadRanged = minRanged <= maxScoreRanged ? maxScoreRanged - minRanged : 0f;
            spreadMelee = minMelee <= maxScoreMelee ? maxScoreMelee - minMelee : 0f;
            spreadGrenade = minGrenade <= maxScoreGrenade ? maxScoreGrenade - minGrenade : 0f;
            float CraftPenalty(AssignCandidate c)
            {
                float afford = LoadoutUtility.MaterialAffordability(c.mainWeapon);
                // Both scarcity penalties as before, unchanged. Quadratic in the budget term so a slightly more
                // expensive but clearly better weapon still wins at low scarcity.
                float scarcity = 1f - afford;
                float penalty = (1f - craft) * 80f * (1f - afford)
                    + (1f - budget) * 50f * scarcity * scarcity;
                // Linear in the price term, not quadratic: the point here is that the MEDIUM price difference
                // counts too (assault rifle vs MG), not only the extreme one.
                bool isGren = Dialog_GenerateLoadouts.IsGrenade(c.mainWeapon);
                float maxCost = isGren ? maxCostGrenade : c.isRanged ? maxCostRanged : maxCostMelee;
                // Weight = spread of this competition, at least the previous 45 (see above).
                float priceWeight = Mathf.Max(45f, isGren ? spreadGrenade : c.isRanged ? spreadRanged : spreadMelee);
                if (maxCost > 0f)
                    penalty += (1f - budget) * priceWeight * Mathf.Clamp01(LoadoutUtility.WeaponCraftCost(c.mainWeapon) / maxCost);
                return penalty;
            }

            // CARRY CAPACITY IS NO LONGER A CONSTRAINT ON ASSIGNMENT: it must be possible to assign ANYTHING;
            // weight and bulk play no role in assignment - the player makes sure it is sensible (maybe a
            // backpack goes on afterwards). That removed the "Respect max bulk" checkbox again, and with it
            // everything attached to it: the main weapon cap, the remaining budget for packs/sidearm/throwables
            // and the two messages about it ("reduced set", "nothing fits"). The solver again assigns what fits
            // best.
            // What REMAINS is the WARNING below: anyone over his limit after Apply is listed with numbers in the
            // "Problems with the current settings" box and gets red bars - see the loop for the overload
            // messages. It is the only place where carry capacity still matters to the solver, and that is
            // exactly what these two helpers are for.
            // CapOf now lives as the static CapacityOf next to ReweighEntry (see there); WhyExceeds and the box
            // construction itself moved next to it as RefreshOverloadProblem.

            AssignCandidate PickBest(bool wantRanged, float pawnRanged, float pawnMelee)
            {
                AssignCandidate best = null;
                float bestVal = float.NegativeInfinity;
                foreach (AssignCandidate c in cands)
                {
                    // Grenades and shields are their own separate pools now (PickGrenade/PickShield below) -
                    // neither is ever a pawn's MAIN weapon, just something that rides alongside it.
                    if (Dialog_GenerateLoadouts.IsGrenade(c.mainWeapon) || LoadoutUtility.IsShieldApparel(c.mainWeapon)) continue;
                    if (c.isRanged != wantRanged) continue;
                    // "Respect max bulk": anything that cannot be carried on its own does not compete at all.
                    bool hasStock = stock.TryGetValue(c.mainWeapon, out int s) && s > 0;
                    if (!hasStock && (craft <= 0.001f || !c.producible)) continue;

                    // Anchor on the general (ammo-independent) score, tilt toward the computed target damage as the
                    // mech slider leaves the neutral middle. Real numbers decide flesh vs mech, not a flat weight.
                    float targetDamage = Mathf.Lerp(c.fleshScore, c.mechScore, mech);
                    float tilt = Mathf.Abs(mech - 0.5f) * 2f * MechTilt;
                    float val = Mathf.Lerp(c.score, targetDamage, tilt);
                    // Only what must be built costs anything - a weapon already on the shelf is free at every
                    // slider position. This is also the "check what's there first" part of the Economy preset:
                    // existing gear has no penalty here by construction.
                    if (!hasStock) val -= CraftPenalty(c);
                    // A virtual pick creates a brand-new loadout; prefer an equivalent existing loadout when tied.
                    if (c.isVirtual) val -= 4f;
                    int used = timesUsed.TryGetValue(c, out int t) ? t : 0;
                    int inGroup = usedInGroup.TryGetValue(c.group, out int g) ? g : 0;
                    val -= diversity * 55f * used;
                    val -= diversity * 8f * inGroup;
                    if (caps.TryGetValue(c.group, out int cap) && inGroup + 1 > cap)
                        val -= diversity * 35f * (inGroup + 1 - cap);
                    // Cover roles doesn't just avoid duplicates - it actively PULLS toward unfilled role groups: the
                    // first pick of an uncovered SupportMG (full-auto), Marksman, CQC or Launcher slot bridges up to
                    // ~25 score points, so a squad at high diversity fields one of each when candidates exist.
                    if (inGroup == 0 && (c.group == WeaponGroup.SupportMG || c.group == WeaponGroup.Marksman
                        || c.group == WeaponGroup.CQC || c.group == WeaponGroup.Launcher))
                        val += diversity * 25f;

                    // Complementary backup: reward a loadout that also lets this pawn use their OTHER combat skill -
                    // a melee backup (knife) for a melee-capable shooter, a ranged backup for a shooting brawler.
                    // Scaled by that skill and capped, so it only breaks ties between otherwise similar loadouts
                    // (e.g. Henry Big Boy + knife beats Henry Big Boy + grenade for an 86-melee colonist).
                    if (c.isRanged && c.hasMelee) val += Mathf.Min(pawnMelee, 80f) * 0.12f;
                    if (!c.isRanged && c.hasRanged) val += Mathf.Min(pawnRanged, 80f) * 0.12f;

                    if (val > bestVal) { bestVal = val; best = c; }
                }
                return best;
            }

            // Equipment Packs are pure supply now (medicine/food/drugs/utility - weapons of every kind,
            // sidearms and grenades included, are Weapon Loadouts and attach via Roles/multi-parent
            // instead, see MultiParentUtility). A pack whose contents are a genuine mix (or Food-led, like
            // Base Supply's food+medicine split) is still a baseline everyone carries - there's nothing to
            // specialize there. But a pack STRONGLY dominated by one category reads as a specialist role
            // (a Medic Kit), and handing that to literally everyone just piles every specialist's gear onto
            // every colonist regardless of fit. Structural, not name-based (a player's own hand-authored
            // packs get the same treatment as the auto-generated defaults):
            //   - Medical-dominant -> best Medicine skill first, so far down the list as the coverage slider
            //     reaches (2026-08-09; used to be a fixed top-40% cut the slider could not move).
            //   - Contains a shield -> the melee-oriented half (this mod's own weapon-loadout generator
            //     already pairs shields with melee, see DefaultShield's `mobile` param) - unless the whole
            //     squad is ranged, in which case gating it would starve everyone. Checked by CONTENT
            //     (ContainsShield), not "Utility-dominant" - a hand-authored Utility-dominant pack with no
            //     shield in it (say, a player's own webbing-only kit) has no weapon-compatibility reason to
            //     be melee-only.
            // A pack's own supply shortfall is still surfaced downstream by the existing Dialog_SupplyLoadouts
            // flow once .Slots includes it (same as main-weapon ammo today).
            List<Loadout> packPool = EquipmentPackUtility.AllEquipmentPacks();
            List<Pawn> packEligible = order.Where(e => !packPinnedPawns.Contains(e.pawn)).Select(e => e.pawn).ToList();
            // Pacifists/non-combatants can't fight but CAN still carry Equipment Pack supply (e.g. a dedicated
            // doctor) - the manual Assign tab already allows this (CannotCarryAnything, not the broader
            // CannotFight, is the only real block there), but the auto-solver used to gate ALL pack logic on
            // being in `order` (fight-capable only), so a pacifist got whatever pack they already happened to
            // have (line ~194 above) and never a fresh auto-assignment - even a high-Medicine-skill doctor who'd
            // otherwise top the medic ranking right below (2026-08-01 fix). Kept OUT of anyMeleeTarget/
            // shieldEligible below on purpose - a shield is pointless for someone who never fights, and their
            // presence shouldn't skew whether the FIGHTING squad counts as melee-capable.
            // IsExcludedFromArming must be filtered OUT here even though it is part of CannotFight: CannotFight
            // bundles two different statements. "Pacifist or baby" only means "no WEAPON" - a pacifist should
            // very much get a Medic Kit, which is exactly what this branch is for. "The player unticked him",
            // on the other hand, means "leave him completely alone"; auto-assigning him a pack here would be the
            // opposite of what that checkbox means.
            List<Pawn> packOnlyPawns = pawns.Where(p => LoadoutUtility.CannotFight(p) && !LoadoutUtility.CannotCarryAnything(p)
                && !LoadoutUtility.IsExcludedFromArming(p) && !packPinnedPawns.Contains(p)).ToList();
            // The fixed 40% medic cutoff that used to live here is gone. It wasn't wrong, just immovable: it meant
            // the same as the current skill ordering further down (rankRecipients), but with a number the
            // coverage slider could not touch - exactly the inconsistency at issue. Choosing "who gets a Medic
            // Kit first" now happens in one place instead of two; medicPackEligible remains as the base set.
            List<Pawn> medicPackEligible = packEligible.Concat(packOnlyPawns).ToList();
            bool anyMeleeTarget = packEligible.Any(p => !isShooter[p]);

            // Shields get their OWN gradual, best-melee-fighter-first fill (shieldCoverage, 0..1) instead of
            // sharing packCoverage's rank-threshold tiers below - shield need is a melee-vs-ranged question,
            // a different axis from "how much general supply does the squad get". Same "proportional slot
            // count, filled in priority order" shape sidearmCoverage/grenadeCoverage already use further
            // down - NOT "every eligible pawn gets it the instant a tier unlocks". Eligible = melee-oriented
            // pawns, unless the WHOLE squad is ranged (nobody to prioritize over anyone else, so everybody
            // qualifies) - same rule the old inline gate used. `order` is already sorted best-combat-
            // priority-first (rangedScore for shooters, meleeScore for everyone else), so walking it in
            // order naturally hands shields to the squad's best melee fighters first. Content-based
            // throughout (ContainsShield inspects the pack's actual slots) - never keyed off a pack's name,
            // so a player's own hand-authored shield pack is treated identically to the built-in "Shield Kit".
            List<Pawn> shieldEligible = packEligible.Where(p => !(anyMeleeTarget && isShooter[p])).ToList();
            int shieldSlots = Mathf.RoundToInt(shieldEligible.Count * shieldCoverage);
            HashSet<Pawn> shieldGiven = new HashSet<Pawn>(shieldEligible.Take(shieldSlots));
            List<Loadout> shieldPacks = packPool.Where(EquipmentPackUtility.ContainsShield).ToList();

            // packCoverage (0..1) ranks pack TYPES by essentialness and includes progressively more of them
            // as the slider rises - it does NOT mean "some pawns get every pack, others get none" (that was
            // the original, wrong reading: coverage gated PAWNS, not WHICH packs, so a mid-slider setting
            // could hand a shooter's whole squad zero food/medicine while a melee pawn further down the list
            // got everything). Base Supply-like packs (mixed content, or Food-led - the base kit
            // every pawn needs first: food+medicine) rank most essential; Medical/Drugs/Utility-specialized
            // content only layers on top once included. Every ELIGIBLE pawn gets every pack up to the
            // threshold (still subject to the Medical gate below) - coverage never excludes a pawn outright
            // anymore, only which pack TYPES exist for anyone at that setting. Shield-bearing packs are
            // excluded from this ranking/threshold entirely - shieldCoverage above already decided their fate.
            int PackRank(Loadout pack)
            {
                PackCategory? dom = EquipmentPackUtility.DominantCategory(pack);
                if (dom == null || dom == PackCategory.Food) return 0;
                if (dom == PackCategory.Medical) return 1;
                if (dom == PackCategory.Drugs) return 2;
                if (dom == PackCategory.Utility) return 3;
                return 4;
            }
            List<Loadout> rankedPackPool = packPool.Where(p => !shieldPacks.Contains(p)).ToList();
            int maxPackRank = rankedPackPool.Count > 0 ? rankedPackPool.Max(PackRank) : 0;
            // steps: 0 = nothing at all ("No packs"), 1 = rank 0 only, ..., maxPackRank+1 = every rank
            // ("Generous packs") - keeps "fully disable packs" reachable at the slider's own left extreme
            // while everything above it fills in essential-first, matching how Dialog_GenerateEquipmentPack's
            // own Essential<->All slider already ranks its templates (TemplateOrder/TemplateRank).
            int packSteps = maxPackRank + 2;

            // From here on GRADUAL instead of all-or-nothing: as with shields, and for medicine-heavy packs
            // colonists with a high Medical skill get the pack first.
            // Previously the same slider did different things on different rows: for shields 60% meant "the six
            // best-suited out of ten", for a Medic Pack still "all or none" depending on whether the rank
            // threshold was crossed - the preview jumped from 0 to everyone.
            //
            // Intended behavior: doctors first, but gradually more, then helpers, gradually more, then everyone,
            // gradually more. Measured was 0 -> 5 -> 6 -> 9 with five colonists added on a single 5% step. The
            // cause was NOT the ramp, which already existed - it was a HARD rank threshold on top
            // (packRankThreshold): it only admitted a rank from the middle of its own ramp onward, and at that
            // moment the ramp was already at 50%. So every rank popped in with half its group at once. The
            // threshold is gone now; the ramp alone decides, and for a rank not yet reached it yields zero
            // carriers on its own.
            //
            // This means rank 0 (base supply) ramps too - previously "all or none". That is the price of this
            // behavior and at the same time the only way to keep the left end ("No packs"): at slider 0 every
            // ramp is zero, so nobody gets anything. Previously the rank threshold held that end.
            float packPos = packCoverage * (packSteps - 1);
            List<Pawn> combatOrderedEligible = order.Select(e => e.pawn).Where(packEligible.Contains).Concat(packOnlyPawns).ToList();
            // Medicine by Medical skill, descending - by design. Pacifists are deliberately included: a doctor
            // who never fights is the best carrier for a Medic Kit, not the worst (same reasoning as the
            // packOnlyPawns branch above).
            List<Pawn> medicalOrdered = medicPackEligible
                .OrderByDescending(p => p.skills?.GetSkill(SkillDefOf.Medicine)?.Level ?? 0)
                .ThenBy(p => p.LabelShortCap.ToString())
                .ToList();
            Dictionary<int, HashSet<Pawn>> rankRecipients = new Dictionary<int, HashSet<Pawn>>();
            for (int r = 0; r <= maxPackRank; r++)
            {
                List<Pawn> pool = r == 1 ? medicalOrdered : combatOrderedEligible;
                float fill = Mathf.Clamp01(packPos - r);
                // Round up, not to nearest: otherwise nothing happens at the START of each step again. With ten
                // carriers, rounding would need 5% of slider travel before the first one even appears - exactly
                // the "nothing happens between 25 and 75" effect seen before. Rounding up means: as soon as a
                // step begins something moves, and after that one carrier is added after another.
                int slots = fill <= 0f ? 0 : Mathf.Clamp(Mathf.CeilToInt(pool.Count * fill), 1, pool.Count);
                rankRecipients[r] = new HashSet<Pawn>(pool.Take(slots));
            }

            // `e` is the entry whose carry budget the packs count against ("Respect max bulk") - for the combat
            // loop the row's entry, for the pack-only pass the pacifist's entry.
            List<Loadout> ChoosePacksFor(Pawn pawn, AssignPlanEntry e)
            {
                var chosen = new List<Loadout>();
                // A pack only goes in if it fits the remaining budget; otherwise it is remembered for the message
                // and the NEXT one is tried (a lighter Medic Kit may still fit when the base supply no longer
                // did). Without the checkbox TryTake accepts everything.
                // PACKS ARE EXEMPT FROM THE CAP: it makes no sense to cram in two sidearms and give no packs for
                // it - packs are assigned regardless, the weight warning stays. They still count against the
                // budget (so the sidearm afterwards sees what is already loaded), but a pack is never left out
                // anymore - and the overload warning below lists the colonist with numbers as before.
                void Take(Loadout pack) { chosen.Add(pack); }
                // One shield pack, not every shield pack in the pool (2026-07-31 fix) - AddRange used to hand
                // a shield-eligible pawn ALL of them simultaneously whenever 2+ distinct shield-bearing packs
                // existed (e.g. a player's own custom shield pack alongside the built-in "Shield Kit"), most
                // visible at shieldCoverage=1.0 where every eligible pawn was affected at once.
                if (shieldGiven.Contains(pawn) && shieldPacks.Count > 0) Take(shieldPacks[0]);
                foreach (Loadout pack in rankedPackPool)
                {
                    int rank = PackRank(pack);
                    // Instead of the old all-or-nothing threshold: is this pawn among the carriers this rank covers
                    // at the current slider position? For medicine that is the skill order, otherwise the combat
                    // order (same as for shields).
                    // This removes the old separate medics gate: it meant the same thing, but with a fixed 40%
                    // cutoff the slider could not move.
                    if (rankRecipients.TryGetValue(rank, out HashSet<Pawn> recips) && !recips.Contains(pawn)) continue;
                    Take(pack);
                }
                return chosen;
            }

            // ---- What REALLY hangs on the pawn after Apply in terms of extras ----
            // An EMPTY plan list means "don't touch" (WantsPackWrite/WantsSidearmWrite): the colonist then keeps
            // what he carries today - and that has weight, so the overload warning must see it.
            // Exception: a role pick that switches templates clears the anchor (EnsureAmmoLink), then nothing
            // hangs on it anymore. EffectivePacks/EffectiveSidearms are therefore the sets AFTER Apply and the
            // warning's only source. They are STATIC helpers next to ReweighEntry (see there) - the in-place pin
            // needs the same answer as this solve.

            // Sidearm: a second Weapon Loadout attached the same live way Equipment Packs are (multi-parent,
            // see SidearmAssignmentUtility) - opposite weapon type from the main pick (a ranged main gets a
            // melee backup, a melee main a ranged one), same "sidearms still exist, just as their own
            // Weapon Loadout now" idea the current architecture was built around. sidearmCoverage (0..1)
            // covers that fraction of the squad, best-combat-priority first (the same order weapons are
            // handed out in) rather than a per-pawn coin flip, so a partial setting always arms the
            // sharpest shooters/fighters first instead of a scattershot subset.
            int sidearmGiven = 0;
            // Its own reuse count, separate from timesUsed above - a loadout already handed out as someone's
            // MAIN shouldn't be penalized as a sidearm pick for someone else (different "slot"), but two
            // pawns both getting the exact same sidearm should still be discouraged as diversity rises,
            // same shape as PickBest's own reuse penalty (just without the group-cap/role-coverage terms,
            // which are about the squad's MAIN loadout composition, not its backups).
            Dictionary<AssignCandidate, int> timesUsedSidearm = new Dictionary<AssignCandidate, int>();

            // A proper sidearm is a compact BACKUP weapon, not just "the next-best opposite-type gun" - a
            // full battle rifle scoring well should never get auto-picked as someone's "sidearm" just
            // because it out-scores a pistol. Reuses the same cap the old baked-in-sidearm mechanism
            // already had (CEReloadedSettings.sidearmMaxBulk, still configurable in mod settings and now
            // also inline in this popup) rather than inventing a second, disconnected setting.
            // Goes through SidearmAssignmentUtility.MaxSidearmBulk - the same number the sidearm picker now uses
            // for hiding entries. One source, two readers.
            float maxSidearmBulk = SidearmAssignmentUtility.MaxSidearmBulk();

            // `limit` = the colonist's remaining budget ("Respect max bulk"), null = no cap.
            AssignCandidate PickSidearm(bool wantRanged, ThingDef excludeWeapon)
            {
                AssignCandidate best = null;
                float bestVal = float.NegativeInfinity;
                foreach (AssignCandidate c in cands)
                {
                    // Grenades and shields are their own separate pools now (PickGrenade/PickShield below) -
                    // a real backup weapon shouldn't compete with either for the same slot, or overlap into
                    // their pools.
                    if (Dialog_GenerateLoadouts.IsGrenade(c.mainWeapon) || LoadoutUtility.IsShieldApparel(c.mainWeapon)) continue;
                    if (c.isRanged != wantRanged || c.mainWeapon == excludeWeapon) continue;
                    if (LoadoutUtility.WeaponBulk(c.mainWeapon) > maxSidearmBulk) continue;
                    bool hasStock = stock.TryGetValue(c.mainWeapon, out int s) && s > 0;
                    if (!hasStock && (craft <= 0.001f || !c.producible)) continue;
                    float targetDamage = Mathf.Lerp(c.fleshScore, c.mechScore, mech);
                    float tilt = Mathf.Abs(mech - 0.5f) * 2f * MechTilt;
                    float val = Mathf.Lerp(c.score, targetDamage, tilt);
                    if (!hasStock) val -= CraftPenalty(c);
                    if (c.isVirtual) val -= 4f;
                    int usedSide = timesUsedSidearm.TryGetValue(c, out int ts) ? ts : 0;
                    val -= diversity * 55f * usedSide;
                    if (val > bestVal) { bestVal = val; best = c; }
                }
                return best;
            }

            // Explosives: a separate budget and pool from real backup weapons (PickSidearm above), sharing
            // only the same underlying attach mechanism and the same "Sidearm" list on the pawn - a satchel
            // charge is not a substitute for a knife, so it must never compete with one for the same slot.
            // grenadeCoverage (0..1) covers this fraction of the squad, same best-combat-priority-first shape
            // as sidearmCoverage; no melee/ranged split here, a grenade complements everyone the same way.
            int grenadeGiven = 0;
            Dictionary<AssignCandidate, int> timesUsedGrenade = new Dictionary<AssignCandidate, int>();

            // `limit` as in PickSidearm.
            AssignCandidate PickGrenade(ThingDef excludeWeapon)
            {
                AssignCandidate best = null;
                float bestVal = float.NegativeInfinity;
                foreach (AssignCandidate c in cands)
                {
                    if (!Dialog_GenerateLoadouts.IsGrenade(c.mainWeapon) || c.mainWeapon == excludeWeapon) continue;
                    bool hasStock = stock.TryGetValue(c.mainWeapon, out int s) && s > 0;
                    if (!hasStock && (craft <= 0.001f || !c.producible)) continue;
                    float targetDamage = Mathf.Lerp(c.fleshScore, c.mechScore, mech);
                    float tilt = Mathf.Abs(mech - 0.5f) * 2f * MechTilt;
                    float val = Mathf.Lerp(c.score, targetDamage, tilt);
                    if (!hasStock) val -= CraftPenalty(c);
                    if (c.isVirtual) val -= 4f;
                    int usedNade = timesUsedGrenade.TryGetValue(c, out int tn) ? tn : 0;
                    val -= diversity * 55f * usedNade;
                    if (val > bestVal) { bestVal = val; best = c; }
                }
                return best;
            }

            // Collected during the main loop below, resolved in two passes AFTER every pawn has a main
            // weapon: entries wanting a MELEE sidearm (their main is ranged) fill sidearmSlots first, entries
            // wanting a RANGED sidearm (their main is melee) only get one once every melee-wanting entry
            // already has theirs. A shooter's backup blade is the far more broadly useful case (ammo runs
            // out, raiders close to melee range) than a brawler also carrying a gun, so partial coverage
            // should arm every shooter with a blade before arming any brawler with a gun - not interleave
            // the two by combat-priority order the way a single pass naturally would.
            List<(AssignPlanEntry entry, bool wantRanged)> sidearmEligible = new List<(AssignPlanEntry, bool)>();
            // Same idea, no melee/ranged split - a grenade suits everyone the same way, so insertion order
            // (already best-combat-priority-first, same as the main loop below) is the only ordering needed.
            List<AssignPlanEntry> grenadeEligible = new List<AssignPlanEntry>();

            foreach (AssignPlanEntry e in order)
            {
                float pr = rangedScore(e.pawn), pm = meleeScore(e.pawn);
                // "Respect max bulk": the colonist's carry capacity MINUS whatever stays attached regardless of
                // this pick (pinned extras, today's packs without a new assignment - ReservedBeforeMain), as the
                // cap for the main weapon.
                AssignCandidate pick = PickBest(isShooter[e.pawn], pr, pm) ?? PickBest(!isShooter[e.pawn], pr, pm);
                if (pick == null) continue;
                bool hasStock = stock.TryGetValue(pick.mainWeapon, out int s2) && s2 > 0;
                e.planned = pick.loadout;
                e.plannedVirtual = pick.isVirtual;
                e.plannedMain = pick.mainWeapon;
                e.needsCraft = !hasStock;
                if (hasStock) stock[pick.mainWeapon] = s2 - 1;
                usedInGroup[pick.group] = (usedInGroup.TryGetValue(pick.group, out int g2) ? g2 : 0) + 1;
                timesUsed[pick] = (timesUsed.TryGetValue(pick, out int t2) ? t2 : 0) + 1;

                // A pinned pack set (Dialog_SquadSetup's own pack picker) already carries the right value
                // from entry creation above - the solver leaves it alone regardless of the weapon pick.
                // A Role loadout may already have pack content BAKED into it directly (Role Mode's own
                // drag-a-pack-onto-a-role mechanism, RoleUtility.Merge) - auto-attaching every pack again
                // here on top would double it up (e.g. two lots of Base Supply medicine, one from the
                // Role's own bake and one live via the per-pawn anchor). Only plain, non-Role assignments
                // get every pack auto-attached; a Role's author already made that choice deliberately.
                if (packPool.Count > 0 && !packPinnedPawns.Contains(e.pawn) && !pick.isRole)
                    e.plannedPacks = ChoosePacksFor(e.pawn, e);

                // Same Role exception as packs - a Role that already bakes a second weapon in via its own
                // drag-multiple-loadouts-onto-a-role mechanism should not also get one auto-attached here.
                // A pinned sidearm set (Dialog_SquadSetup's own sidearm picker) already carries the right
                // value from entry creation above - the solver leaves it alone regardless of the weapon pick.
                if (!pick.isRole && !sidearmPinnedPawns.Contains(e.pawn))
                {
                    sidearmEligible.Add((e, !pick.isRanged));
                    grenadeEligible.Add(e);
                }
            }

            // Against the pool that's ACTUALLY competing for a slot, not the whole squad (2026-08-02 fix,
            // order.Count includes Role picks and pinned pawns, which sidearmEligible/
            // grenadeEligible above deliberately exclude - at the slider's own extremes (0%/100%) the two
            // pools happen to agree, but at any setting in between, a squad with a non-trivial mix of Role/
            // pinned picks got a real coverage percentage well off from what the slider position implied).
            int sidearmSlots = Mathf.RoundToInt(sidearmEligible.Count * sidearmCoverage);
            int grenadeSlots = Mathf.RoundToInt(grenadeEligible.Count * grenadeCoverage);

            // Melee-wanting entries (wantRanged == false) sort first - that pass structure stays exactly as
            // is. WITHIN each of the two groups, entries used to just fall back to insertion (combat-priority)
            // order; now ranked by whichever skill the incoming sidearm actually calls on - a ranged main
            // with a strong Melee skill is the one who gets real use out of a backup blade, so a limited
            // sidearmSlots budget should reach them before someone who'd barely land a hit with it.
            foreach (var (e, wantRanged) in sidearmEligible.OrderBy(x => x.wantRanged)
                .ThenByDescending(x => x.wantRanged ? rangedScore(x.entry.pawn) : meleeScore(x.entry.pawn)))
            {
                if (sidearmGiven >= sidearmSlots) break;
                AssignCandidate side = PickSidearm(wantRanged, e.plannedMain);
                // A virtual candidate has no loadout object (see isVirtual above). Before lazy creation it was
                // discarded here - now the bare WEAPON goes into plannedSidearmDefs, and the template is only
                // created on Apply (ApplyPreviewPlan).
                // A non-virtual candidate without a loadout remains a data error and is still dropped.
                if (side != null && !side.isVirtual && side.loadout == null) side = null;
                if (side == null) continue;
                bool sideStock = stock.TryGetValue(side.mainWeapon, out int ss) && ss > 0;
                if (side.isVirtual)
                {
                    if (e.plannedSidearmDefs == null) e.plannedSidearmDefs = new List<ThingDef>();
                    e.plannedSidearmDefs.Add(side.mainWeapon);
                }
                else e.plannedSidearms = new List<Loadout> { side.loadout };
                if (sideStock) stock[side.mainWeapon] = ss - 1;
                timesUsedSidearm[side] = (timesUsedSidearm.TryGetValue(side, out int tu) ? tu : 0) + 1;
                sidearmGiven++;
            }

            // Who should carry live ordnance, algorithmically rather than by any hardcoded trait list (a
            // hardcoded def would silently stop matching the moment a modded trait/skill system replaces or
            // extends the vanilla one). Two signals, both universal to any RimWorld install:
            //   - Shooting skill, as a stand-in for throwing accuracy - CE grenades fly a real ballistic arc,
            //     so a poor throw risks landing short, on the thrower's own position or a nearby ally.
            //   - Live proximity to a mental break (mood vs. this exact pawn's OWN break thresholds, already
            //     inflated by whatever trait causes it - Volatile, a modded equivalent, doesn't matter which)
            //     - a break with explosives in inventory is the textbook self-inflicted disaster.
            // Pyromaniac is the one trait actually named here, since it is guaranteed vanilla-present (not
            // mod-dependent) and its specific risk - torching stored ordnance - isn't a mood/mental-break
            // question at all, so the general check above wouldn't otherwise catch it.
            float ExplosivesSuitability(Pawn p)
            {
                float s = 0f;
                SkillRecord shoot = p.skills?.GetSkill(SkillDefOf.Shooting);
                if (shoot != null && !shoot.TotallyDisabled) s += shoot.Level;
                if (p.mindState?.mentalBreaker != null && p.needs?.mood != null)
                {
                    float moodPct = p.needs.mood.CurLevelPercentage;
                    float breakRisk = Mathf.Max(0f, p.mindState.mentalBreaker.BreakThresholdMinor - moodPct);
                    s -= breakRisk * 100f;
                }
                if (p.story?.traits != null && p.story.traits.HasTrait(TraitDefOf.Pyromaniac)) s -= 15f;
                return s;
            }

            // Explosives fill independently, into the SAME plannedSidearms list a real backup weapon may have
            // already claimed above - one shared attach mechanism and GUI cell, two separate budgets/pools
            // feeding it, by design: grenades and a real sidearm are never in competition with
            // each other, but both end up listed as "this pawn's sidearms" either way. Ordered by
            // ExplosivesSuitability (descending) instead of plain combat-priority order, so a limited
            // grenadeSlots budget reaches the safest/most accurate carriers first.
            foreach (AssignPlanEntry e in grenadeEligible.OrderByDescending(e => ExplosivesSuitability(e.pawn)))
            {
                if (grenadeGiven >= grenadeSlots) break;
                AssignCandidate nade = PickGrenade(e.plannedMain);
                if (nade == null) continue;
                bool nadeStock = stock.TryGetValue(nade.mainWeapon, out int ns) && ns > 0;
                // Same split as for the sidearm above (lazy creation): virtual -> remember the weapon,
                // real -> attach the template. A real candidate without a loadout stays out.
                if (nade.isVirtual)
                {
                    if (e.plannedSidearmDefs == null) e.plannedSidearmDefs = new List<ThingDef>();
                    e.plannedSidearmDefs.Add(nade.mainWeapon);
                }
                else if (nade.loadout != null) e.plannedSidearms.Add(nade.loadout);
                if (nadeStock) stock[nade.mainWeapon] = ns - 1;
                timesUsedGrenade[nade] = (timesUsedGrenade.TryGetValue(nade, out int tn2) ? tn2 : 0) + 1;
                grenadeGiven++;
            }

            // Shields are Equipment Pack content now, not a sidearm-style pick - ChoosePacksFor above already
            // handles them via their own dedicated shieldCoverage fill (melee-oriented pawns first, best
            // fighters first, unless the whole squad is ranged). No separate fill pass needed here.

            // Pack-only pass for pacifists/non-combatants (2026-08-01 fix, see packOnlyPawns above) - they
            // never entered `order`, so they never went through the main weapon-picking loop above where
            // every other pawn's ChoosePacksFor call lives. Their AssignPlanEntry already exists (added at
            // the very top of this method, kept=true, plannedPacks defaulted to whatever they already had) -
            // just overwrite plannedPacks with a real auto-assignment, same function, same rules.
            if (packPool.Count > 0)
                foreach (Pawn p in packOnlyPawns)
                {
                    AssignPlanEntry entry = plan.entries.FirstOrDefault(x => x.pawn == p);
                    if (entry == null) continue;
                    entry.plannedPacks = ChoosePacksFor(p, entry);
                    // `kept` defaulted true above (the pacifist "nothing to do" entry) and must be flipped once
                    // this freshly-computed pack plan differs from what they already carry, or ApplyPreviewPlan's
                    // `if (e.kept) continue;` discards it (2026-08-01 fix: the whole "give
                    // pacifists an auto-assigned pack" feature computed correctly but never applied to anyone).
                    // The flip itself now lives in the single sweep at the end of this method - same condition,
                    // one place, and it covers the pin path too.
                }

            // "Does this even fit on the colonist?" belongs BEFORE Apply (two of nine colonists were overloaded
            // after Auto-assign - one at mass 56.9/30.4 kg, almost double - and the preview said nothing about
            // it. The red bars and the proper tooltip only appeared afterwards in the Assign tab, and only on
            // hover).
            // No hard limit: the principle is "allow it, but add a clearly visible warning".
            // The warning had been on the capacity bar all along - just one step too late. The solver itself
            // stays untouched.
            // Computed against CE's own values for the SPECIFIC pawn (not the median), because here a specific
            // loadout is intended for a specific colonist.
            // Carry capacity and the explanation text come from the same helpers as the cap above (CapOf,
            // WhyExceeds) - deliberately ONE calculation for both.
            // The whole weighing logic (default to current state, three weight sources, extras count too; also
            // the medic case "can't fight but gets packs") lives in ReweighEntry - ONE calculation for this
            // solve AND the in-place pin (Dialog_SquadSetup.AdoptExtrasPin). The box line WITH REASON AND
            // NUMBERS is built by RefreshOverloadProblem from the entry fields - the same function runs on a pin,
            // so box and bars never show two different states.
            foreach (AssignPlanEntry e in plan.entries)
                ReweighEntry(e);
            RefreshOverloadProblem(plan);

            // "Respect max bulk", the two consequences of the cap - both only filled with the checkbox on:
            // who got no carryable main weapon at all, and who had packs/sidearms left out.

            BuildProblems(plan, cands, targets, craft, mech, melee, diversity, roleMode);

            // Colonists who got a loadout but currently can't use it (downed / injured / paralysed) - may be
            // temporary, so they are only flagged, not blocked (per design). Skipped pacifists are already obvious
            // from their "can't fight - skipped" row above, so they are not repeated here.
            var weak = order.Where(e => (e.planned != null || e.plannedVirtual) && rangedScore(e.pawn) <= 0f && meleeScore(e.pawn) <= 0f)
                .Select(e => e.pawn.LabelShortCap).ToList();
            if (weak.Count > 0)
                plan.problems.Add(new AssignProblem("CER_AssignPlanner_WeakColonistsProblem".Translate(weak.Count.ToString(), string.Join(", ", weak.ToArray()))));

            // Last, after every pass above has settled plannedPacks/plannedSidearms - one O(pawns) sweep per
            // solve instead of the same comparison per frame out of ApplyLabel.
            foreach (AssignPlanEntry e in plan.entries)
            {
                RefreshExtrasChanged(e);
                // The invariant the whole pack/sidearm layer depends on: `kept` means "skip this row
                // wholesale" for both the change count and the apply loop, so it must never be true while
                // there is still pack/sidearm work on it. Cheap here, and it covers every producer at once -
                // the pin path, the pacifist pack-only pass (which grew its own copy of this line), and any
                // future one.
                if (e.extrasChanged) e.kept = false;
            }

            return plan;
        }

        // ---- Preview weight of a row: ONE calculation for solve AND in-place pin ----
        //
        // previewMass/previewBulk used to be set ONLY in the full solve. A manually pinned pack or sidearm,
        // however, deliberately patches the entry in place (see Dialog_SquadSetup.AdoptExtrasPin - a full
        // RecomputeAssign would reshuffle other rows), and nothing was reweighed: the row's resupply icons
        // read the new sidearms immediately while the mass/bulk bars next to them still showed the state of
        // the last solve (sidearms and packs did not move the mass/weight bar).
        // So the whole weighing logic now lives here as a static function; the solve calls it per row, the
        // pin for the one patched row. Returns whether it REALLY weighed (then the overload box may name the
        // colonist) or stayed at today's state as the default.
        internal static bool RoleClears(AssignPlanEntry e) => e.planned != null && RoleUtility.IsRoleLoadout(e.planned)
            && e.pawn != null && AmmoAssignmentUtility.DisplayLoadout(e.pawn.GetLoadout()) != e.planned;
        internal static List<Loadout> EffectivePacks(AssignPlanEntry e)
            => (e.plannedPacks != null && e.plannedPacks.Count > 0) || e.packPinned
               ? (e.plannedPacks ?? new List<Loadout>())
               : (RoleClears(e) ? new List<Loadout>() : (AmmoAssignmentUtility.GetPawnPacks(e.pawn) ?? new List<Loadout>()));
        internal static List<Loadout> EffectiveSidearms(AssignPlanEntry e)
            => (e.plannedSidearms != null && e.plannedSidearms.Count > 0) || e.sidearmPinned
               ? (e.plannedSidearms ?? new List<Loadout>())
               : (RoleClears(e) ? new List<Loadout>() : (SidearmAssignmentUtility.GetPawnSidearms(e.pawn) ?? new List<Loadout>()));

        // A colonist's carry limits - the same fallbacks as the former solve-local CapOf.
        // Without the per-solve cache: the pin path asks about exactly ONE colonist, the solve once per row.
        internal static (float mass, float bulk) CapacityOf(Pawn p)
        {
            float capMass = LoadoutUtility.CarryWeightOf(p);
            StatDef bulkStat = LoadoutUtility.CarryBulkStat;
            float capBulk = bulkStat != null ? p.GetStatValue(bulkStat) : 20f;
            if (capMass <= 0f) capMass = 35f;
            if (capBulk <= 0f) capBulk = 20f;
            return (capMass, capBulk);
        }

        internal static bool ReweighEntry(AssignPlanEntry e)
        {
            if (e == null) return false;
            e.previewWeighed = false;
            if (e.pawn == null) return false;
            RefreshExtrasChanged(e);
            (float capM0, float capB0) = CapacityOf(e.pawn);
            e.capMass = capM0; e.capBulk = capB0;
            LoadoutUtility.GetLoadoutMassBulk(e.current, out float curM, out float curB);
            e.previewMass = curM; e.previewBulk = curB;   // default: today's state, overwritten below
            if (e.cannotFight && !e.extrasChanged) return false;

            // The sets AFTER Apply, not the raw plan lists: an empty plan list means "keeps his current ones" -
            // see EffectivePacks/EffectiveSidearms right above.
            List<Loadout> extras = EffectivePacks(e).Concat(EffectiveSidearms(e)).ToList();
            bool hasVirtualSidearms = e.plannedSidearmDefs != null && e.plannedSidearmDefs.Count > 0;
            bool hasExtras = extras.Any(x => x != null) || hasVirtualSidearms;

            // ONE weight source with three cases (history in the comments in the solve): a real template, a
            // virtual pick (weighed from the same defaults the template is created from on Apply), or no weapon
            // but extras.
            float lMass, lBulk;
            if (e.planned != null)
            {
                LoadoutUtility.GetLoadoutMassBulk(e.planned, out lMass, out lBulk);
            }
            else if (e.plannedVirtual && e.plannedMain != null)
            {
                Dialog_GenerateLoadouts.DefaultMassBulkFor(e.plannedMain, out lMass, out lBulk);
            }
            else if (hasExtras)
            {
                lMass = 0f; lBulk = 0f;
            }
            else return false;   // really nothing to weigh - the default (today's state) stays
            // Packs and sidearms COUNT TOO: e.planned is the bare weapon template, the extras hang next to it as
            // separate loadouts - and those are exactly what makes up bulk.
            foreach (Loadout extra in extras)
            {
                if (extra == null) continue;
                LoadoutUtility.GetLoadoutMassBulk(extra, out float em, out float eb);
                lMass += em; lBulk += eb;
            }
            // Virtual sidearms/grenades are weighed like the virtual main weapon pick: from the same defaults the
            // template is created from on Apply (lazy creation).
            if (hasVirtualSidearms)
                foreach (ThingDef sd in e.plannedSidearmDefs)
                {
                    Dialog_GenerateLoadouts.DefaultMassBulkFor(sd, out float vm, out float vb);
                    lMass += vm; lBulk += vb;
                }
            e.previewMass = lMass; e.previewBulk = lBulk;
            e.previewWeighed = true;
            return true;
        }

        // Bulk first, that is the more common case in CE.
        internal static List<string> WhyExceeds((float mass, float bulk) load, (float mass, float bulk) cap)
        {
            List<string> why = new List<string>();
            if (load.bulk > cap.bulk)
                why.Add("CER_AssignPlanner_OverloadBulk".Translate(load.bulk.ToString("0.0"), cap.bulk.ToString("0.0")).ToString());
            if (load.mass > cap.mass)
                why.Add("CER_AssignPlanner_OverloadMass".Translate(load.mass.ToString("0.0"), cap.mass.ToString("0.0")).ToString());
            return why;
        }

        // Rebuilds THE one overload line of the problem box from the entry fields - on solve AND on an in-place
        // pin (the previously documented limitation "the box only updates on the next recompute" was
        // unnecessary - after ReweighEntry all numbers are on the entries, so there was no reason left to keep
        // the box stale).
        // Replaces the existing line in place (isOverloadLine) so its position in the box stays stable; when
        // the last overload disappears, the line disappears.
        internal static void RefreshOverloadProblem(AssignPlan plan)
        {
            if (plan == null || plan.entries == null || plan.problems == null) return;
            var overloaded = new List<string>();
            foreach (AssignPlanEntry e in plan.entries)
            {
                if (e?.pawn == null || !e.previewWeighed) continue;
                if (e.previewMass > e.capMass || e.previewBulk > e.capBulk)
                    overloaded.Add(e.pawn.LabelShortCap + " (" + string.Join(", ", WhyExceeds((e.previewMass, e.previewBulk), (e.capMass, e.capBulk)).ToArray()) + ")");
            }
            int idx = plan.problems.FindIndex(p => p != null && p.isOverloadLine);
            if (overloaded.Count == 0)
            {
                if (idx >= 0) plan.problems.RemoveAt(idx);
                return;
            }
            var line = new AssignProblem("CER_AssignPlanner_OverloadedProblem".Translate(
                overloaded.Count.ToString(), string.Join(", ", overloaded.ToArray()))) { isOverloadLine = true };
            if (idx >= 0) plan.problems[idx] = line; else plan.problems.Add(line);
        }

        // The ONE definition of "Apply will write this row's packs" - read both by the change count
        // (AssignPlanEntry.extrasChanged) and by the writer itself (Dialog_AssignLoadouts.ApplyPreviewPlan),
        // so the button and the commit can't disagree about whether anything is pending.
        // Deliberately NOT gated on a planned loadout: packs hang off the pawn's own hidden anchor, not off a
        // weapon plan, so a row with no loadout at all can still receive one (the pack-only pass for
        // pacifists) - its anchor template is then CE's own "None" marker, exactly what the live Assign tab's
        // Pack cell already attaches to. For such a row an EMPTY plan is not a removal instruction, though:
        // plannedPacks is only ever filled for rows the solver actually picked for, so treating empty as
        // "clear" would strip the packs off every colonist the plan simply left without a weapon.
        public static bool WantsPackWrite(AssignPlanEntry e)
        {
            if (e == null) return false;
            List<Loadout> want = e.plannedPacks ?? new List<Loadout>();
            // ====== The guard used to cover only rows WITHOUT a weapon plan - that was too narrow ======
            // The reasoning one line above is correct and complete: plannedPacks is ONLY filled for rows the
            // solver really picked a pack for. An empty list therefore means "nothing picked", never "take his
            // away". But that only held as long as the row also got no weapon planned - for a row WITH a
            // weapon plan and an empty pack plan, the set comparison below went through and deleted the
            // colonist's pack.
            // Measured: three colonists had a Medic pack set by hand, the preview showed "-" everywhere in the
            // Pack column, and after Apply it was gone for all three - without notice, without a message.
            // A pack is removed via the Pack cell, not as a side effect of a weapon assignment.
            // ====== Third case: the solver picked, the carry cap removed everything ======
            // Then the empty list IS the instruction. Without this exception Apply writes nothing, the colonist
            // keeps today's packs, and the "Respect max bulk" checkbox doesn't keep its promise - exactly the
            // measured case. No new surprise risk: with the checkbox off the solver replaces this row's pack
            // set anyway.
            if (want.Count == 0 && !e.packPinned) return false;
            return !SameLoadoutSet(AmmoAssignmentUtility.GetPawnPacks(e.pawn), want);
        }

        // Same for sidearms. This used to say `e.planned == null -> false` ("sidearms genuinely need a
        // real weapon loadout to hang off of") - that was never true: the sidearm hangs off the personal anchor
        // just like the pack, and that anchor can be set up via CE's None template. The result was a silent
        // loophole: a sidearm pinned in the preview for a colonist without a planned weapon didn't count as a
        // change and was discarded on Apply (similar loopholes were searched for as well).
        // Dialog_AssignLoadouts.ApplyPreviewPlan now writes it in the planned==null branch via the None
        // template, parallel to the pack line there.
        public static bool WantsSidearmWrite(AssignPlanEntry e)
        {
            if (e == null) return false;
            // The same gap as for packs, found by grepping the twin location: plannedSidearms is only filled
            // when the solver really picked a sidearm - and it only picks for as many colonists as the slider
            // allows, the rest stay empty. So empty means "not picked", not "take his away". Only the pack case
            // had been noticed; this one would have been next.
            // The same third exception as for packs (again found at the twin location): if the picker really
            // searched for this row and only the cap stopped it, empty means "no sidearm" - otherwise today's
            // stays attached and eats exactly the budget the cap just freed up.
            // A virtual pick (bare weapon, template only created on Apply) is ALWAYS a change: a template for
            // this weapon didn't exist at planning time, so the colonist can't already be carrying it (lazy
            // creation).
            bool hasDefs = e.plannedSidearmDefs != null && e.plannedSidearmDefs.Count > 0;
            if ((e.plannedSidearms == null || e.plannedSidearms.Count == 0) && !hasDefs && !e.sidearmPinned) return false;
            if (hasDefs) return true;
            return !SameLoadoutSet(SidearmAssignmentUtility.GetPawnSidearms(e.pawn), e.plannedSidearms);
        }

        // Called once per solve and again whenever a single row's pin is patched in place without a re-solve
        // (Dialog_SquadSetup.ApplyPinnedPacks/ApplyPinnedSidearms).
        public static void RefreshExtrasChanged(AssignPlanEntry e)
        {
            if (e == null) return;
            e.extrasChanged = WantsPackWrite(e) || WantsSidearmWrite(e);
        }

        // Order-independent set compare by UniqueID, null-tolerant on both the lists and their contents (a null
        // entry used to be reachable via a virtual sidearm pick - fixed at the source, but this runs every frame
        // via Changes, so it must never be the thing that throws).
        private static bool SameLoadoutSet(List<Loadout> a, List<Loadout> b)
        {
            var ia = (a ?? new List<Loadout>()).Where(l => l != null).Select(l => l.UniqueID).OrderBy(i => i);
            var ib = (b ?? new List<Loadout>()).Where(l => l != null).Select(l => l.UniqueID).OrderBy(i => i);
            return ia.SequenceEqual(ib);
        }

        // ====== May this candidate be handed out at this toggle value at all? ======
        // Three cases instead of two (see RoleAssignMode): NoRoles is the new one, and it is the only one with
        // which "Ad-Hoc Loadouts per Pawn" can be honored in the wizard at all.
        // Mixed is the previous behavior and stays the default.
        // PUBLIC and in ONE place, because two lists ask the same question: the solver here and the preview's
        // pin menu (Dialog_SquadSetup.OpenPinMenu). In the first attempt only the solver filtered - the manual
        // picker under "No roles" promptly still offered both roles, while the toggle's own tooltip explicitly
        // promises "and the per-colonist picker". Exactly the duplication the mislabeling came from.
        public static bool RoleModeAllows(AssignCandidate c, RoleAssignMode mode)
        {
            if (c == null) return false;
            if (mode == RoleAssignMode.RolesOnly) return c.isRole;
            if (mode == RoleAssignMode.NoRoles) return !c.isRole;
            return true;
        }

        // The interactive problem list: entries appear only once the current slider weighting makes them matter.
        private static void BuildProblems(AssignPlan plan, List<AssignCandidate> cands, List<AssignPlanEntry> targets,
            float craft, float mech, float melee, float diversity, RoleAssignMode roleMode)
        {
            void P(string text, ProblemAction action = ProblemAction.None)
                => plan.problems.Add(new AssignProblem(text, action));

            if (targets.Count == 0)
            {
                P("CER_AssignPlanner_NothingToAssign".Translate());
                return;
            }
            if (cands.Count == 0)
            {
                // Three modes, three reasons: "there is no role" and "there is no template other than roles" are
                // different dead ends, and anyone reading just "no loadouts" looks in the wrong place.
                if (roleMode == RoleAssignMode.RolesOnly)
                    P("CER_AssignPlanner_RolesOnlyNoRoles".Translate(), ProblemAction.OpenRolesTab);
                else
                    P(roleMode == RoleAssignMode.NoRoles ? "CER_AssignPlanner_NoRolesNoPlain".Translate()
                        : "CER_AssignPlanner_NoLoadoutsExist".Translate());
                return;
            }

            int unassigned = targets.Count(e => e.planned == null && !e.plannedVirtual);
            if (unassigned > 0)
            {
                // Since lazy creation every owned or producible weapon is a candidate - if someone stays empty,
                // weapons are really missing (the former branch "you own weapons without a loadout" could only
                // fire for disabled weapons anymore and was removed).
                P(craft <= 0.001f
                    ? "CER_AssignPlanner_UnassignedNoOwned".Translate(unassigned.ToString())
                    : "CER_AssignPlanner_UnassignedNoProducible".Translate(unassigned.ToString()));
            }

            if (mech >= 0.6f && !cands.Any(c => c.mechAffinity >= 0.65f))
                P("CER_AssignPlanner_NoAntiMechOption".Translate());
            if (mech <= 0.25f && !cands.Any(c => c.mechAffinity <= 0.35f))
                P("CER_AssignPlanner_NoAntiFleshOption".Translate());

            if (melee >= 0.6f && !cands.Any(c => !c.isRanged))
                P("CER_AssignPlanner_NoMeleeLoadouts".Translate());
            if (melee <= 0.4f && !cands.Any(c => c.isRanged))
                P("CER_AssignPlanner_NoRangedLoadouts".Translate());

            if (diversity >= 0.6f)
            {
                int groups = cands.Select(c => c.group).Distinct().Count();
                if (groups < Mathf.Min(3, targets.Count))
                    P("CER_AssignPlanner_FewWeaponGroups".Translate(groups.ToString()));
            }
        }
    }
}
