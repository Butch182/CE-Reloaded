using System.Collections.Generic;
using System.Linq;
using RimWorld;
using UnityEngine;
using Verse;
using CombatExtended;

namespace CEReloaded
{
    // AssignedNotCarried: the one state none of the seven other categories caught. A colonist WITH a real
    // loadout who holds NO weapon at all while their assigned weapon sits in storage otherwise falls through
    // everything: NoLoadout does not apply (they have one), WeaponUnavailable does not apply (the weapon
    // exists), and LowAmmo hangs off the carried weapon, which does not exist. The result was a green 'Ready'
    // with empty hands - exactly the scenario this mod exists for.
    public enum ReadinessIssue { NoLoadout, WeaponWithoutAmmo, AmmoNotProduced, LowAmmo, Overloaded, WeaponUnavailable, CapacityBlocked, AssignedNotCarried }

    // Shared, cached combat-readiness scan. RimWorld's alert readout polls each alert on a rotating schedule, so all
    // three alerts read from ONE computation that refreshes at most every few seconds. This is also the backbone the
    // in-tab warnings / auto-manage phases will reuse. Purely read-only - it never changes game state.
    public static class CombatReadiness
    {
        private const int RecomputeInterval = 250;   // ~4 seconds at normal speed

        private static int lastTick = -99999;
        private static readonly Dictionary<ReadinessIssue, List<Pawn>> culprits = new Dictionary<ReadinessIssue, List<Pawn>>
        {
            { ReadinessIssue.NoLoadout,         new List<Pawn>() },
            { ReadinessIssue.WeaponWithoutAmmo, new List<Pawn>() },
            { ReadinessIssue.AmmoNotProduced,   new List<Pawn>() },
            { ReadinessIssue.LowAmmo,           new List<Pawn>() },
            { ReadinessIssue.Overloaded,        new List<Pawn>() },
            { ReadinessIssue.WeaponUnavailable, new List<Pawn>() },
            { ReadinessIssue.CapacityBlocked,   new List<Pawn>() },
            { ReadinessIssue.AssignedNotCarried, new List<Pawn>() },
        };

        public static List<Pawn> Culprits(ReadinessIssue issue)
        {
            EnsureFresh();
            return culprits[issue];
        }

        // Forces the next Culprits() call to recompute instead of trusting the tick-interval cache - needed
        // because virtually every dialog this mod opens (Dialog_ArmColony, Dialog_CEReloadedTabs itself,
        // Dialog_SupplyLoadouts, ...) sets forcePause = true, which freezes Find.TickManager.TicksGame for as
        // long as ANY of them is open. RecomputeInterval (250 ticks) never naturally elapses across a whole
        // paused session, so a background tab left open behind Arm my Colony kept showing pre-run readiness
        // numbers (still "9 have no loadout" right after assigning all 9) until the player unpaused for
        // several real seconds afterward (2026-08-01 - also explains the "ghost ammo bills"
        // symptom: the alert's own culprit lists, not the actual Bill objects, were what was stale). Called at
        // the one moment this session already treats as "a real update just happened, tell everything to
        // refresh" - Dialog_ArmColony's own Arm() - rather than threading invalidation through every single
        // mutation site across the mod.
        public static void Invalidate() { lastTick = -99999; MutationVersion++; }

        // Counter of every reported change to assignments/anchors: the Assign tab's draw caches (round-robin
        // calculation, row info, ammo cell, stock pool) used to be rebuilt EVERY frame - at 143 Hz roughly 90
        // rebuilds per second for a paused game in which nothing changes between two frames unless the player
        // clicks. Now the two-lock rule applies: a cache lives until this number changes OR a short maximum
        // age is reached. Invalidate() is already the one point every mutation of the mod calls - the counter
        // just piggybacks on it; a forgotten mutation site therefore makes the value stale for a fraction of a
        // second, never permanently wrong.
        public static int MutationVersion { get; private set; }

        // No longer keyed on "did the viewed map change" - the scan itself is empire-wide now (every colony map,
        // every caravan), so a mech assault on an outpost still raises the alert while the player is looking at
        // the main base. Just a plain tick-interval cache.
        // Real-time counterpart to RecomputeInterval - four seconds are enough to notice a change in a paused
        // game without extra computation while the game is running.
        private const float RealSecondsInterval = 4f;
        private static float lastReal = -9999f;

        // ONLY while one of this mod's windows is actually open (a PC left running overnight spun up its fan
        // by morning).
        //
        // The real-time limit was added for EXACTLY ONE case: our windows pause the game, game time stands
        // still, and so the tick limit never thawed. But it hung off the bare clock - which keeps running even
        // while RimWorld sits untouched in the main menu. Result: every four seconds a full pass over all
        // workbenches, all bills, all weapons on every map and all colonists - all night long, without anything
        // able to change. That is not a memory leak but a constant load; it explains a spinning fan better than
        // growing memory, because it costs the same on every pass.
        //
        // With none of our windows open, only the tick limit applies again: if the game runs, it ticks and we
        // recompute every 250 ticks as before; if it is paused, nothing that could move these numbers changes
        // without our windows anyway.
        //
        // Checked via the namespace rather than a list of window types: an enumeration would silently become
        // incomplete with the next new dialog, and then the old bug would return (stale numbers behind an
        // open tab).
        private static bool AnyModWindowOpen()
        {
            var stack = Find.WindowStack;
            if (stack == null) return false;
            IList<Window> open = stack.Windows;
            for (int i = 0; i < open.Count; i++)
                if (open[i] != null && open[i].GetType().Namespace == "CEReloaded") return true;
            return false;
        }

        private static void EnsureFresh()
        {
            int now = Find.TickManager != null ? Find.TickManager.TicksGame : 0;
            // Game time jumping backwards means: a different game (main menu -> new colony or an older save).
            // lastTick is static and survives the game switch, so now - lastTick was strongly NEGATIVE and thus
            // forever < RecomputeInterval - the cache never thawed again and returned pawn objects from the
            // discarded game. Clicking the alert then jumped to a map that no longer exists.
            if (now < lastTick) lastTick = -99999;
            // Second brake on the REAL clock: game time stands still while paused, and every window of this mod
            // pauses automatically. So the cache never thawed while a mod window was open - precisely in the
            // panic routine before a raid (pause, equip by hand, check the light) it showed the old numbers.
            // Both limits must hold: only when game time OR real time has advanced far enough is it recomputed.
            // That keeps the load in a running game as low as before.
            float rt = Time.realtimeSinceStartup;
            bool tickStale = (now - lastTick) >= RecomputeInterval;
            bool realStale = (rt - lastReal) >= RealSecondsInterval && AnyModWindowOpen();
            if (!tickStale && !realStale) return;
            lastReal = rt;
            lastTick = now;
            foreach (List<Pawn> l in culprits.Values) l.Clear();
            Recompute();
        }

        private static void Recompute()
        {
            // 1. Everything currently being produced by a non-suspended colonist bill (products of any kind),
            // on any colony map.
            HashSet<ThingDef> produced = new HashSet<ThingDef>();
            foreach (Building_WorkTable bench in LoadoutUtility.AllColonyBenches())
            {
                if (bench.billStack == null) continue;
                foreach (Bill_Production bill in bench.billStack.Bills.OfType<Bill_Production>())
                {
                    if (bill.suspended || bill.recipe == null || bill.recipe.products == null) continue;
                    foreach (ThingDefCountClass p in bill.recipe.products)
                        if (p.thingDef != null) produced.Add(p.thingDef);
                }
            }

            int stockThreshold = 0;
            if (CEReloadedMod.settings != null) int.TryParse(CEReloadedMod.settings.alertAmmoStockThreshold, out stockThreshold);

            // 2. "Who is missing which weapon" - the round-robin claim on free stock lives in
            // Dialog_AssignLoadouts.WeaponShortfallsByPawn and is read from there instead of being computed a
            // second time here. Reason and failure mode: see the header comment there (this alert reported a gap
            // the resupply window did not know about with its own calculation). Weapons marked "forbidden" still
            // count as stock - an alert "nobody can be armed" while twenty rifles lie on the floor was the
            // problem this rule came from.
            Dictionary<Pawn, Dictionary<ThingDef, int>> weaponShortfalls = Dialog_AssignLoadouts.WeaponShortfallsByPawn();

            HashSet<Pawn> noLoadout = new HashSet<Pawn>();
            HashSet<Pawn> noAmmo = new HashSet<Pawn>();
            HashSet<Pawn> notProduced = new HashSet<Pawn>();
            HashSet<Pawn> lowAmmo = new HashSet<Pawn>();
            HashSet<Pawn> overloaded = new HashSet<Pawn>();
            HashSet<Pawn> weaponUnavail = new HashSet<Pawn>();
            HashSet<Pawn> capacityBlocked = new HashSet<Pawn>();
            HashSet<Pawn> assignedNotCarried = new HashSet<Pawn>();

            foreach (Pawn pawn in LoadoutUtility.AllColonyPawns())
            {
                Loadout lo = pawn.GetLoadout();
                // Decide on the resolved loadout, read the raw one: for every personalized colonist lo is their
                // personal anchor, and that is NEVER the default loadout - so "has a template" was always true
                // here as soon as an anchor existed, even with no template underneath any more (deleted, or
                // re-parented to the default). Exactly then the colonist was missing from "no loadout" although
                // they have none. For the CONTENT it stays lo.Slots: the union over the whole chain is what they
                // really carry.
                Loadout display = AmmoAssignmentUtility.DisplayLoadout(lo);
                bool hasRealLoadout = display != null && !display.defaultLoadout;

                // Never been through the workflow at all: no loadout, no CE-managed ammo/weapon restock. The most
                // fundamental readiness gap - without this, a colony that never generated/assigned a single loadout
                // reads as "Ready" (every other check only fires once a real loadout exists to be incomplete).
                // Pacifists and babies can never fight, so a missing loadout is not an issue for them.
                if (!hasRealLoadout && !LoadoutUtility.CannotFight(pawn))
                    noLoadout.Add(pawn);

                // Check the content too when only sidearms hang off CE's None loadout: "no loadout" stays correct
                // for the row above (no main weapon), but the ammo of an attached halberd or revolver needs
                // checking just the same.
                bool hasSidearmsOnly = !hasRealLoadout && (SidearmAssignmentUtility.GetPawnSidearms(pawn)?.Count ?? 0) > 0;
                if ((hasRealLoadout || hasSidearmsOnly) && lo.Slots != null)
                {
                    // Ammo the loadout carries (concrete + generic placeholders).
                    HashSet<ThingDef> loadoutAmmo = new HashSet<ThingDef>();
                    HashSet<LoadoutGenericDef> loadoutGeneric = new HashSet<LoadoutGenericDef>();
                    List<ThingDef> ammoWeapons = new List<ThingDef>();
                    List<ThingDef> loadoutWeapons = new List<ThingDef>();
                    foreach (LoadoutSlot slot in lo.Slots)
                    {
                        if (slot.genericDef != null) loadoutGeneric.Add(slot.genericDef);
                        if (slot.thingDef == null) continue;
                        bool realWeapon = slot.thingDef.IsWeapon && !LoadoutUtility.IsSelfAmmoWeapon(slot.thingDef);
                        if (realWeapon)
                            for (int k = 0, need = slot.count < 1 ? 1 : slot.count; k < need; k++) loadoutWeapons.Add(slot.thingDef);
                        if (realWeapon && LoadoutUtility.GetAmmoSet(slot.thingDef) != null)
                            ammoWeapons.Add(slot.thingDef);
                        else if (realWeapon && slot.thingDef.GetCompProperties<CompProperties_AmmoUser>() != null)
                            // Comp present (marked as a CE ammo-using weapon) but the ammo SET itself never
                            // resolved (broken/incomplete 3rd-party CE patch) - this weapon can NEVER carry
                            // valid ammo, a worse case than "picker came back empty after resolving" (which
                            // WeaponWithoutAmmo/AmmoNotProduced below already catch via ammoWeapons). Without
                            // this branch such a weapon silently skips every ammo check with zero alert
                            // (2026-08-01, systemic blind spot, shared with Dialog_WeaponAudit's
                            // own check #3, which gated the same way).
                            noAmmo.Add(pawn);
                        else if (!realWeapon)
                            loadoutAmmo.Add(slot.thingDef);   // ammo/consumables only - weapons (incl. melee) never belong here
                    }

                    // Assigned, but carries NOTHING at all. Deliberately only this one hard case - not "some slot
                    // is still missing": the latter would be true constantly in any living colony (somebody is
                    // always on the way to the shelf) and the alert would be wallpaper within an hour. "Loadout
                    // says weapon, hands empty", on the other hand, is the state in which someone really has
                    // nothing to do in a fight.
                    // equipment.Primary, not the inventory: a weapon in the backpack is not drawn, but CE draws it
                    // itself in combat - empty hands AND empty inventory would be too narrow.
                    if (loadoutWeapons.Count > 0
                        && (pawn.equipment == null || pawn.equipment.Primary == null)
                        && (pawn.inventory?.innerContainer == null
                            || !pawn.inventory.innerContainer.Any(t => loadoutWeapons.Contains(t.def)))
                        && !LoadoutUtility.CannotFight(pawn))
                        assignedNotCarried.Add(pawn);

                    // A loadout weapon the colonist does not already hold, that is no longer free in storage (the
                    // round-robin claim above gave it to someone else), and for which no bill is running. The
                    // QUANTITY comes from the shared calculation; all that is left here is what this alert is about:
                    // - if a bill is running, it is no longer a gap but a wait (produced);
                    // - thrown explosives/grenades are consumables, not "the weapon is missing"
                    //   (IsSelfAmmoWeapon - they deliberately sit in the ammo branch, see loadoutAmmo);
                    // - this alert still does not report shields (the resupply window knows them, this alert never
                    //   reported them - that remains a separate decision).
                    if (weaponShortfalls.TryGetValue(pawn, out Dictionary<ThingDef, int> missingGear)
                        && missingGear.Keys.Any(w => !produced.Contains(w)
                            && !LoadoutUtility.IsSelfAmmoWeapon(w)
                            && !LoadoutUtility.IsShieldApparel(w)))
                        weaponUnavail.Add(pawn);

                    foreach (ThingDef weapon in ammoWeapons)
                    {
                        List<ThingDef> concrete = LoadoutUtility.GetAmmoTypesFor(weapon).ToList();
                        LoadoutGenericDef generic = LoadoutUtility.GetGenericAmmoDef(weapon);

                        bool hasAmmo = concrete.Any(a => loadoutAmmo.Contains(a)) || (generic != null && loadoutGeneric.Contains(generic));
                        if (!hasAmmo) noAmmo.Add(pawn);

                        bool anyProduced = concrete.Any(a => produced.Contains(a));
                        // "Not in production" is only actionable when the ammo can actually be produced - it has a
                        // researched recipe. If nothing is researched yet, the real problem is research/availability
                        // (covered by the other alerts), not a missing bill, so don't raise this one.
                        bool anyResearched = concrete.Any(a => LoadoutUtility.IsAmmoResearched(a));
                        if (!anyProduced && anyResearched)
                        {
                            // With a stock threshold set, a big reserve counts as covered even without a running bill.
                            if (stockThreshold <= 0) notProduced.Add(pawn);
                            else
                            {
                                int owned = concrete.Sum(a => LoadoutUtility.PlayerMaps().Sum(map => map.listerThings.ThingsOfDef(a).Sum(t => LoadoutUtility.IsHiddenInFog(t) ? 0 : t.stackCount)));
                                if (owned < stockThreshold) notProduced.Add(pawn);
                            }
                        }
                    }

                    // Too full to fetch the rest of the loadout: bulk/weight already maxed, so CE can't queue a
                    // pickup for anything still missing no matter how much is in storage - a real "own carry
                    // capacity" problem the player can't fix by producing more, distinct from WeaponWithoutAmmo
                    // (which only checks "carries SOME usable ammo at all", so a pawn who already has their
                    // primary round but is stuck full before a SECOND wanted type - e.g. an AP top-up - ever
                    // gets fetched reads as "fine" there). Checked against every individual ammo def/generic the
                    // loadout actually lists, not just per-weapon "has any".
                    CompInventory capInv = pawn.TryGetComp<CompInventory>();
                    if (capInv != null && (capInv.currentBulk >= capInv.capacityBulk - 0.01f || capInv.currentWeight >= capInv.capacityWeight - 0.01f))
                    {
                        HashSet<ThingDef> carriedDefs = new HashSet<ThingDef>();
                        if (pawn.inventory != null && pawn.inventory.innerContainer != null)
                            foreach (Thing t in pawn.inventory.innerContainer) carriedDefs.Add(t.def);
                        // Equipped Primary (e.g. a melee main weapon) never shows up in inventory.innerContainer -
                        // without this it reads as "missing" and falsely blocks a fully-equipped pawn.
                        if (pawn.equipment != null && pawn.equipment.Primary != null) carriedDefs.Add(pawn.equipment.Primary.def);
                        // Same reasoning for WORN apparel: a shield is apparel and so was never in this set, although
                        // loadoutAmmo contains it (every non-weapon slot is there). Failure scenario: melee fighter
                        // with a Shield Kit, wearing the shield, bulk at the limit -> permanently listed under
                        // "Capacity blocked" although they have everything. The alert never went away.
                        if (pawn.apparel != null && pawn.apparel.WornApparel != null)
                            foreach (Apparel a in pawn.apparel.WornApparel) carriedDefs.Add(a.def);
                        if (loadoutAmmo.Any(a => !carriedDefs.Contains(a))) capacityBlocked.Add(pawn);
                    }
                }

                // Low on ammo: the pawn's equipped CE gun vs. spare ammo it is actually carrying.
                // The calculation itself lives in LowAmmoState - see there for why: the readiness widget's click
                // path must know the same threshold and the same carried amount, otherwise it answers a different
                // question than the one the alert asked.
                if (LowAmmoState(pawn, out LowAmmoInfo lowInfo) && lowInfo.IsLow) lowAmmo.Add(pawn);

                // Overloaded: over CE weight or bulk capacity (slows the pawn, hurts aim/dodge).
                CompInventory inv = pawn.TryGetComp<CompInventory>();
                if (inv != null && (inv.currentWeight > inv.capacityWeight || inv.currentBulk > inv.capacityBulk))
                    overloaded.Add(pawn);
            }

            culprits[ReadinessIssue.NoLoadout].AddRange(noLoadout);
            culprits[ReadinessIssue.WeaponWithoutAmmo].AddRange(noAmmo);
            culprits[ReadinessIssue.AmmoNotProduced].AddRange(notProduced);
            culprits[ReadinessIssue.LowAmmo].AddRange(lowAmmo);
            culprits[ReadinessIssue.Overloaded].AddRange(overloaded);
            culprits[ReadinessIssue.WeaponUnavailable].AddRange(weaponUnavail);
            culprits[ReadinessIssue.CapacityBlocked].AddRange(capacityBlocked);
            culprits[ReadinessIssue.AssignedNotCarried].AddRange(assignedNotCarried);
        }

        // ---- "Low on ammo" - the state BEHIND it, not just the yes/no ----------------------------
        //
        // A click on "low ammo", meant to lead to a fix, used to open a Supply Loadouts window that only offered
        // to cancel Satchel Charges - nonsense.
        // It breaks down like this:
        // "Low on ammo" has THREE causes, and each needs a DIFFERENT answer.
        //   A) The colonist simply has not picked up the ammo yet (it is in storage or in production)
        //      -> the fix is to wait, not to produce.
        //   B) Their loadout or role plans for less than the alert threshold from the start -> then the alert
        //      will NEVER go away, however full storage is. That is the case nobody handled before, and the most
        //      annoying one: an alert you cannot get rid of.
        //   C) The ammo really is missing and nobody produces it -> resupply window, and exactly this ammo MUST
        //      be listed there.
        //
        // This structure is the shared basis for the alert itself AND for the click path in LoadoutUtility.
        // Deliberately ONE calculation: the alert says "too little", the click must start from the same
        // threshold and the same carried amount, otherwise it sends the player somewhere because of a different
        // number.
        public enum LowAmmoCause
        {
            Pending,        // A - stock or a running bill covers the gap, it is on the way
            PlanTooSmall,   // B - the loadout/role asks for less than the alert threshold
            NotProduced     // C - the gap is uncovered and nothing is being produced
        }

        public struct LowAmmoInfo
        {
            public ThingWithComps weapon;
            public List<ThingDef> usableAmmo;   // everything this weapon can fire
            public int carried;                 // what they actually carry of it
            public int threshold;               // what they would need for the alert to stay quiet
            public int planned;                 // what their loadout/role allows them in the first place
            // The two numbers on which Pending vs. NotProduced IS decided: they used to exist only as local
            // variables in the calculation, and the display computed them a second time on its own - with a
            // different ammo amount (only the LOADED type) and a different stock function. Result: the tooltip
            // said "none in stock, not queued" while the menu of the same display said "supplies are on the way".
            // Anything that wants to show the reason now uses these fields.
            // Only over the types their LOADOUT actually asks for (case: a colonist was listed as 'supplies in
            // stock, not picked up' because 20 rounds of a type lay around that his SKS can fire but his loadout
            // does not ask for - CE's pickup job only fetches what is in the loadout, so there was nothing to
            // fetch; his type was being produced). If the loadout asks for no concrete type (only 'Any ammo'),
            // all usable types count as before.
            public int reachable;               // reachable stock over the PLANNED types, excluding forbidden
            public List<ThingDef> plannedAmmo;  // the types from their loadout (empty = generic/all)
            public List<ThingDef> AmmoToCheck => plannedAmmo != null && plannedAmmo.Count > 0 ? plannedAmmo : usableAmmo;
            public bool anyBill;                // whether a bill is running for any of them
            public LowAmmoCause cause;
            public bool IsLow => carried < threshold;
            public int Shortfall => Mathf.Max(0, threshold - carried);
        }

        // false if the question does not apply to this pawn at all (no weapon, no CE ammo-using weapon).
        public static bool LowAmmoState(Pawn pawn, out LowAmmoInfo info)
        {
            info = default(LowAmmoInfo);
            ThingWithComps primary = pawn?.equipment?.Primary;
            CompAmmoUser comp = primary?.TryGetComp<CompAmmoUser>();
            if (comp == null || comp.Props == null || comp.Props.magazineSize <= 0
                || comp.Props.ammoSet == null || comp.Props.ammoSet.ammoTypes == null) return false;

            info.weapon = primary;
            info.usableAmmo = comp.Props.ammoSet.ammoTypes.Select(t => (ThingDef)t.ammo).Where(a => a != null).Distinct().ToList();

            // Threshold from the settings instead of a hardcoded 2. Invalid input falls back to 2 - a mistyped
            // field must not switch the alert off.
            int magsNeeded = 2;
            if (CEReloadedMod.settings != null && int.TryParse(CEReloadedMod.settings.alertLowAmmoMags, out int mn) && mn > 0) magsNeeded = mn;
            info.threshold = magsNeeded * comp.Props.magazineSize;

            var setAmmo = new HashSet<ThingDef>(info.usableAmmo);
            if (pawn.inventory != null && pawn.inventory.innerContainer != null)
                foreach (Thing t in pawn.inventory.innerContainer)
                    if (setAmmo.Contains(t.def)) info.carried += t.stackCount;
            // The loaded magazine counts too, as in CE itself (see LoadoutUtility.MagazineAmmoCount).
            if (comp.CurrentAmmo != null && setAmmo.Contains(comp.CurrentAmmo)) info.carried += comp.CurMagCount;

            if (!info.IsLow) return true;

            // (B) What is in their loadout at all? The LIVE assignment, not DisplayLoadout. The difference
            // decides the case: DisplayLoadout walks from the personal anchor UP to the template and so returns
            // exactly what the colonist did NOT set themselves. Someone who just raised their own ammo amount
            // would still have been classified as "loadout asks for too little" - the mod would have told them
            // to change something they already changed. pawn.GetLoadout().Slots is the union of anchor,
            // template and extra parents, so really "everything their plan allows them"; a ROLE is included
            // just the same, because it IS the template. Read once, not twice - the getter rebuilds the union.
            List<LoadoutSlot> planSlots = pawn.GetLoadout()?.Slots;
            info.plannedAmmo = new List<ThingDef>();
            bool plansGeneric = false;
            if (planSlots != null)
                foreach (LoadoutSlot s in planSlots)
                {
                    if (s.count <= 0) continue;
                    if (s.thingDef != null && setAmmo.Contains(s.thingDef))
                    {
                        info.planned += s.count;
                        if (!info.plannedAmmo.Contains(s.thingDef)) info.plannedAmmo.Add(s.thingDef);
                    }
                    else if (s.genericDef != null && LoadoutUtility.GetWeaponForGenericAmmo(s.genericDef) == primary.def) { info.planned += s.count; plansGeneric = true; }
                }
            // 'Any ammo for X' asks for every type - then everything usable counts, as before.
            if (plansGeneric) info.plannedAmmo.Clear();
            if (info.planned < info.threshold) { info.cause = LowAmmoCause.PlanTooSmall; return true; }

            // (A vs. C) Is what is reachable or being produced enough to close the gap?
            // Stock here deliberately WITHOUT the carried pool (CarriedStockCount) - what other colonists
            // already carry, this one can no longer pick up.
            // Forbidden stock does NOT count as reachable - it is there, but nobody fetches it. Without this
            // deduction the click reports "supplies are on the way" for a pile of looted ammo the colonist will
            // never touch. ForbiddenStockCount overlaps with both pools above (it counts every marked item
            // outside a pawn, in storage as well as on the floor), so deduct it exactly once and clamp at 0.
            foreach (ThingDef a in info.AmmoToCheck)
            {
                info.reachable += Mathf.Max(0, LoadoutUtility.IngredientStockCount(a) + LoadoutUtility.GroundStockCount(a)
                                               - LoadoutUtility.ForbiddenStockCount(a));
                if (!info.anyBill && LoadoutUtility.WeaponHasActiveBill(a)) info.anyBill = true;
            }
            info.cause = (info.reachable >= info.Shortfall || info.anyBill) ? LowAmmoCause.Pending : LowAmmoCause.NotProduced;
            return true;
        }

        // Small helper for the alert explanations: "Alice, Bob and 3 others".
        public static string NameList(List<Pawn> pawns, int max = 8)
        {
            if (pawns == null || pawns.Count == 0) return "";
            List<string> names = pawns.Take(max).Select(p => p.LabelShort).ToList();
            string joined = string.Join(", ", names.ToArray());
            if (pawns.Count > max) joined += "CER_CombatReadinessAlerts_AndOthers".Translate((pawns.Count - max).ToString()).ToString();
            return joined;
        }
    }

    // ---- Alert (auto-discovered by RimWorld; Medium priority = quiet, no bell/bounce) ----------------------------
    // ONE consolidated alert instead of five separate ones: five near-
    // identical entries in the vanilla alert bar read as noise, not signal - "which of these four almost-the-same
    // alerts is the one that matters right now?". This keeps the per-category on/off settings (a category with its
    // setting off simply contributes nothing) but surfaces ONE line, "Combat readiness: N issues", with the
    // category breakdown in the explanation. Standard vanilla click behaviour (selects/jumps to the culprit pawns)
    // is kept as-is; the NEW one-click FIX action lives in the header-strip readiness widget
    // (Dialog_CEReloadedTabs.cs), which is always visible regardless of which tab is open and opens
    // Dialog_SupplyLoadouts directly for the production-related categories.
    public class Alert_CER_Readiness : Alert
    {
        public Alert_CER_Readiness() { defaultPriority = AlertPriority.Medium; }

        // Store the translation KEY, not the translated value - a static readonly array is only initialized once
        // per process, so pre-translating here would freeze these labels in whatever language was active at first
        // use even after the player switches languages from the Options menu mid-session. Translate at the point
        // of use instead (GetExplanation() below).
        private static readonly (ReadinessIssue issue, string labelKey, System.Func<CEReloadedSettings, bool> enabled)[] Categories =
        {
            (ReadinessIssue.NoLoadout,         "CER_CombatReadinessAlerts_NoLoadout", s => s.alertNoLoadout),
            (ReadinessIssue.WeaponWithoutAmmo, "CER_CombatReadinessAlerts_WeaponWithoutAmmo", s => s.alertWeaponWithoutAmmo),
            (ReadinessIssue.AmmoNotProduced,   "CER_CombatReadinessAlerts_AmmoNotProduced", s => s.alertAmmoNotProduced),
            (ReadinessIssue.WeaponUnavailable, "CER_CombatReadinessAlerts_WeaponUnavailable", s => s.alertWeaponUnavailable),
            (ReadinessIssue.LowAmmo,           "CER_CombatReadinessAlerts_LowAmmo", s => s.alertLowAmmo),
            (ReadinessIssue.Overloaded,        "CER_CombatReadinessAlerts_Overloaded", s => s.alertOverloaded),
            (ReadinessIssue.CapacityBlocked,   "CER_CombatReadinessAlerts_CapacityBlocked", s => s.alertCapacityBlocked),
            (ReadinessIssue.AssignedNotCarried, "CER_CombatReadinessAlerts_AssignedNotCarried", s => s.alertAssignedNotCarried),
        };

        private List<Pawn> AllCulprits()
        {
            CEReloadedSettings s = CEReloadedMod.settings;
            HashSet<Pawn> all = new HashSet<Pawn>();
            foreach (var cat in Categories)
                if (s == null || cat.enabled(s)) all.UnionWith(CombatReadiness.Culprits(cat.issue));
            return all.ToList();
        }

        public override AlertReport GetReport()
        {
            if (CEReloadedMod.settings == null || !CEReloadedMod.settings.showReadinessAlerts) return AlertReport.Inactive;
            return AlertReport.CulpritsAre(AllCulprits());
        }

        public override string GetLabel()
        {
            int n = AllCulprits().Count;
            if (n <= 0) return "CER_CombatReadinessAlerts_Label".Translate();
            return n == 1
                ? "CER_CombatReadinessAlerts_LabelWithIssueSingular".Translate(n.ToString())
                : "CER_CombatReadinessAlerts_LabelWithIssuePlural".Translate(n.ToString());
        }

        public override TaggedString GetExplanation()
        {
            CEReloadedSettings s = CEReloadedMod.settings;
            var sb = new System.Text.StringBuilder();
            foreach (var cat in Categories)
            {
                if (s != null && !cat.enabled(s)) continue;
                List<Pawn> p = CombatReadiness.Culprits(cat.issue);
                if (p.Count == 0) continue;
                sb.Append("\n").Append("CER_CombatReadinessAlerts_CategoryLine".Translate(cat.labelKey.Translate(), p.Count.ToString(), CombatReadiness.NameList(p)).ToString());
            }
            return "CER_CombatReadinessAlerts_ExplanationHeader".Translate().ToString() + "\n" + sb.ToString().TrimStart();
        }
    }
}
