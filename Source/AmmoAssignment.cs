using System.Collections.Generic;
using System.Linq;
using Verse;
using CombatExtended;

namespace CEReloaded
{
    // Per-pawn hidden personal anchor ("[Pawn-Loadout] <pawnId>") - the ONE Loadout object a pawn with any kind
    // of personal customization is actually SetLoadout-assigned to (a pawn with none stays assigned
    // directly to their template, exactly as before this mechanism existed). Two independent things live
    // on it:
    //   - OwnSlots: personal extra ammo (Dialog_ChangeAmmo).
    //   - Extra parents (MultiParentUtility): Equipment Packs this pawn carries beyond their template - a
    //     LIVE reference, not a baked copy, so editing a Pack later is reflected automatically for
    //     everyone referencing it (the same guarantee CE's own native ParentLoadout chain already gives).
    // Anchored onto the pawn's real weapon-loadout template via CE's native single parentID - Loadout.
    // Slots (patched to also walk the extra-parents list, see Patch_Loadout_MultiParentSlots) is the
    // own+parent(+extra-parents) union every consumer in this mod reads (LoadoutMainAndSide,
    // SupplyRequirementsFor, AssignPlanner, ammo-need aggregation, and CE's OWN JobGiver_UpdateLoadout,
    // decompiled and confirmed to re-read a pawn's loadout.Slots fresh every evaluation cycle) - so
    // attaching or detaching a pack here needs nothing else, no Harmony hook into CE's hauling AI.
    //
    // This exists because the shared template can only ever have ONE such anchor pointing at it in a
    // meaningful, personal way - two pawns using the same "R| Assault Rifle" who want DIFFERENT packs
    // need their own attachment point, not a change to the shared template itself.
    public static class AmmoAssignmentUtility
    {
        // "[Ammo]" read as a player-facing name (it shows up if this leaks anywhere, e.g. a stale save from
        // before a bugfix) - "[Pawn-Loadout]" says what the object actually is instead. LegacyMarkerPrefix
        // keeps anchors from saves made before this rename fully working (still recognized, still cleaned
        // up if orphaned) - only NEWLY created anchors (EnsureAmmoLink) ever get the new prefix.
        public const string MarkerPrefix = "[Pawn-Loadout]";
        private const string LegacyMarkerPrefix = "[Ammo]";

        // Requires the numeric pawn-ID suffix EnsureAmmoLink always writes ("[Pawn-Loadout] <name> 1003" -
        // the name is cosmetic/readability only, added 2026-07-30; older saves still have the bare-ID form
        // "[Pawn-Loadout] 1003"), not just the bracket prefix - a player's own pre-existing loadout that
        // happens to be named "[Pawn-Loadout] Sniper" (a perfectly reasonable thing to type) must never be
        // mistaken for one of our internal anchors. This matters most for CleanupOrphanedAnchors, which
        // DELETES anything this returns true for with no confirmation - a false positive there is silent
        // data loss, not just a display glitch. Checking only the LAST whitespace token is numeric (rather
        // than the old "whole remainder is JUST the number") is a real, if small, widening of that false-
        // positive surface - a coincidentally-named "[Pawn-Loadout] Backup 2" would now also match, where
        // it didn't before. Accepted deliberately for the name to be worth adding at all; a player would
        // still have to pick this exact unusual bracketed prefix AND end it with a number, and
        // CleanupOrphanedAnchors additionally only ever sweeps anchors no live pawn/caravan still points to.
        public static bool IsAmmoLink(Loadout lo)
        {
            if (lo == null || string.IsNullOrEmpty(lo.label)) return false;
            string prefix = lo.label.StartsWith(MarkerPrefix) ? MarkerPrefix
                : (lo.label.StartsWith(LegacyMarkerPrefix) ? LegacyMarkerPrefix : null);
            if (prefix == null) return false;
            string rest = lo.label.Substring(prefix.Length).Trim();
            string[] parts = rest.Split(' ');
            return parts.Length > 0 && int.TryParse(parts[parts.Length - 1], out _);
        }

        // What the player's UI should call "their loadout" - unwraps the hidden personal anchor back to
        // the real template underneath. Callers that show "current loadout" (gear tab, assign screen) want this.
        public static Loadout DisplayLoadout(Loadout assigned)
        {
            Loadout cur = assigned;
            int guard = 0;
            while (cur != null && IsAmmoLink(cur) && guard++ < 8)
            {
                if (cur.ParentLoadout == null) break;
                cur = cur.ParentLoadout;
            }
            return cur;
        }

        public static Loadout GetAmmoLeaf(Pawn pawn)
        {
            Loadout cur = pawn?.GetLoadout();
            return IsAmmoLink(cur) ? cur : null;
        }

        // Does this pawn actually carry a personalized combo (sidearm, pack, or personal ammo override) -
        // not just "is on a hidden anchor at all" (an anchor can theoretically exist with nothing on it,
        // e.g. mid-edit). Drives which rows get the "combo" repaint in CE's native loadout table - a pawn
        // still on their bare template should look exactly like CE's own unmodified UI, not be flagged as
        // special when there's nothing extra to show.
        public static bool HasPersonalization(Pawn pawn) =>
            SidearmAssignmentUtility.GetPawnSidearms(pawn).Count > 0
            || GetPawnPacks(pawn).Count > 0
            || ExtraAmmoSlots(pawn).Count > 0;

        // The FULL bundle a personalized pawn actually carries - the real template PLUS every Equipment
        // Pack and sidearm attached via the same hidden anchor (MultiParentUtility) - not just the bare
        // template name DisplayLoadout resolves to on its own. A Role loadout bakes main+sidearm+packs into
        // ONE object (RoleUtility.Merge) precisely so it reads as a single complete thing; a non-Role
        // pawn's bundle is spread across the anchor's extra-parents instead (by design - Roles stay
        // optional), so showing just the unwrapped template name silently hid that packs/sidearm apply at
        // all. Composed on the fly for DISPLAY only - never persisted, never fed back into anything that
        // reads a real Loadout object.
        public static string ComposedDisplayLabel(Pawn pawn)
        {
            Loadout template = DisplayLoadout(pawn?.GetLoadout());
            if (template == null) return "CER_AmmoAssignment_NoneLabel".Translate();
            // Without the short prefix (also in CE's own menu): this text sits in CE's own Loadout column,
            // where space is even tighter than in our compact row - and where the blue tint
            // (Patch_LoadoutTooltip) already tells role and template apart. The selection menu keeps the full name.
            string label = LoadoutUtility.CellLabel(template);
            List<Loadout> sidearms = SidearmAssignmentUtility.GetPawnSidearms(pawn);
            List<Loadout> packs = GetPawnPacks(pawn);
            List<LoadoutSlot> extraAmmo = ExtraAmmoSlots(pawn);
            if (sidearms.Count == 0 && packs.Count == 0 && extraAmmo.Count == 0) return label;

            var extras = new List<string>();
            // CellLabel for the sidearms too ("M16A4 +W| M..." showed the main weapon without prefix and the
            // attachment with it, in the SAME row). The main weapon is already handled that way above, the packs
            // via StripMarker - only the sidearms were shown raw. The prefix is bookkeeping, not a name, and it eats
            // width exactly where there is least of it (CE's own Loadout column).
            extras.AddRange(sidearms.Select(s => s != null
                ? LoadoutUtility.CellLabel(s)
                : "CER_AmmoAssignment_UnknownLabel".Translate().ToString()));
            extras.AddRange(packs.Select(p => EquipmentPackUtility.StripMarker(p.label)));
            if (extraAmmo.Count > 0) extras.Add("CER_AmmoAssignment_ExtraAmmoCount".Translate(extraAmmo.Count.ToString()));

            // Capped at 2 named extras regardless of how much is actually stacked (packs/sidearms are
            // uncapped - "stack as many as you like" - so the raw list has no natural upper bound) - the pixel-
            // width Truncate() at every display site already keeps this from overflowing its box, but a
            // bounded "+N more" reads better than an ellipsis cutting a name off mid-word, and keeps the
            // untruncated tooltip from growing without limit either.
            const int maxNamed = 2;
            List<string> shown = extras.Take(maxNamed).ToList();
            int remaining = extras.Count - shown.Count;
            string extrasText = string.Join(", ", shown) + (remaining > 0 ? "CER_AmmoAssignment_MoreSuffix".Translate(remaining.ToString()).ToString() : "");
            return "CER_AmmoAssignment_LabelWithExtras".Translate(label, extrasText);
        }

        // Every "[Pawn-Loadout]" anchor no live pawn anywhere (any map, any caravan) is actually assigned to
        // anymore - a dead, captured or otherwise removed colonist never cleans up their own anchor on the
        // way out, so these accumulate silently over a long playthrough otherwise. Shared by the Debug
        // settings tab's manual button and CEReloadedGameComponent's own periodic auto-cleanup, so both stay
        // exactly in sync with what "orphaned" means.
        public static List<Loadout> FindOrphanedAnchors()
        {
            HashSet<Loadout> referenced = new HashSet<Loadout>();
            // AllPawns, NOT AllPawnsSpawned. A colonist in a cryptosleep casket, one being carried while
            // unconscious, or one in a landed transport pod is NOT spawned - so their anchor counted as orphaned
            // within an in-game hour and got deleted. Result on waking up: no template, no personal ammo, no
            // packs, no sidearms, without any message.
            // Our own code already proved it wrong: LoadoutUtility.AllColonyPawns uses FreeColonists, which does
            // include unspawned pawns - so the readiness scan kept counting the colonist while this function
            // considered them dead.
            // Plus the world pawns: lent-out quest colonists are on no map and in no caravan. Better one orphaned
            // anchor too many (costs nothing) than one too few.
            //
            // The same gap one holder further: a FLYING transport pod is neither map nor caravan, and its
            // passengers are not in the world pawns either. A pod flight over several tiles takes longer than
            // the 2500 ticks between two runs - so the colonist would have been un-anchored mid-flight and landed
            // without template, packs, sidearms and personal ammo, exactly the damage described above.
            // Instead of adding a third holder by hand, this now uses RimWorld's own aggregate queries, which
            // together cover everything the mod can ever touch - so this list does not need updating for every
            // new holder (gravship, quest groups).
            void AddAll(System.Collections.Generic.IEnumerable<Pawn> pawns)
            {
                if (pawns == null) return;
                foreach (Pawn p in pawns)
                {
                    Loadout lo = p?.GetLoadout();
                    if (lo != null) referenced.Add(lo);
                }
            }
            // Maps + world + "temporary" (gravship among others). Does NOT include flying transporters.
            AddAll(RimWorld.PawnsFinder.AllMapsWorldAndTemporary_Alive);
            // Maps + caravans + FLYING transporters - the half the first query lacks.
            AddAll(RimWorld.PawnsFinder.AllMapsCaravansAndTravellingTransporters_Alive);
            return LoadoutManager.Loadouts.Where(l => l != null && IsAmmoLink(l) && !referenced.Contains(l)).ToList();
        }

        // Removes every currently-orphaned anchor and returns how many were deleted.
        public static int CleanupOrphanedAnchors()
        {
            List<Loadout> orphans = FindOrphanedAnchors();
            // Via LoadoutDeletionUtility, not RemoveLoadout directly: an anchor is exactly the object whose ID
            // is in ammoExplicitlyCleared and is a key in loadoutExtraParents, and this cleanup runs in the
            // background while CE recycles the IDs.
            return LoadoutDeletionUtility.DeleteAll(orphans);
        }

        // Bakes a pawn's CURRENT full combo (main weapon + sidearm(s) + packs - whatever their .Slots union
        // resolves to right now, live references and all) into a brand new, independent Loadout object -
        // the same trick a Role already relies on to be a single, shareable, natively-pickable thing. Same
        // trade-off Roles already accept: a snapshot at save time, not a live reference - editing the
        // ORIGINAL packs/sidearm loadout afterwards does not retroactively update this baked copy. A plain
        // object with none of our internal markers, so it shows up in CE's own native pickers like any
        // hand-authored Weapon Loadout.
        public static Loadout BakeReusableLoadout(Pawn pawn)
        {
            Loadout current = pawn?.GetLoadout();
            if (current == null || current.Slots == null) return null;
            Loadout baked = new Loadout { defaultLoadout = false, label = "CER_AmmoAssignment_CustomLabelPrefix".Translate(ComposedDisplayLabel(pawn)) };
            foreach (LoadoutSlot s in current.Slots)
            {
                if (s.thingDef != null) baked.AddSlot(new LoadoutSlot(s.thingDef, s.count));
                else if (s.genericDef != null) baked.AddSlot(new LoadoutSlot(s.genericDef, s.count));
            }
            LoadoutManager.AddLoadout(baked);
            return baked;
        }

        // The right way to hand a pawn a NEW template loadout (a different weapon, a different Role, ...).
        // ALWAYS routes through the pawn's personal anchor (creating one on first assignment if it doesn't
        // exist yet) rather than ever assigning the shared template object directly - a bare
        // pawn.SetLoadout(template) would leave any later personal customization (packs, sidearm, extra
        // ammo) with nowhere of its own to live, and worse, ammo tracked for a pawn on the raw template is
        // invisible in CE's own native loadout editor (it has nowhere real to be stored without leaking
        // onto every OTHER pawn sharing that same template). Every "assign this real loadout to this pawn"
        // call site should go through this, not pawn.SetLoadout(...) directly (a plain unassign-to-None is
        // fine as-is - there is nothing left to preserve once nothing is assigned).
        public static void AssignTemplate(Pawn pawn, Loadout template)
        {
            if (pawn == null || template == null) return;
            EnsureAmmoLink(pawn, template);
        }

        // Get (or create) this pawn's personal anchor, parented onto `template`. Reassigning the pawn to
        // a different weapon/role re-parents the SAME anchor (keeping any extra ammo/packs they already
        // had) rather than spawning a new one every time this runs.
        public static Loadout EnsureAmmoLink(Pawn pawn, Loadout template)
        {
            if (pawn == null || template == null) return template;
            Loadout current = pawn.GetLoadout();
            if (IsAmmoLink(current))
            {
                if (current.parentID != template.UniqueID)
                {
                    // Assigning a Role starts clean (2026-08-01) - a Role is documented as
                    // "one flat shared Loadout, no pinning concept," so silently carrying over a PREVIOUS
                    // template's leftover sidearm/pack/personal-ammo overrides onto a brand-new Role
                    // contradicted that. ONE-TIME reset at the moment of assignment only, gated on the
                    // parentID actually changing (re-committing to the SAME role the pawn is already on does
                    // NOT re-clear) - the player can still freely personalize ammo/sidearm/packs on a
                    // Role-assigned pawn afterward via the exact same UI paths as any other pawn; nothing
                    // about the anchor mechanism itself gets locked, only this one transition moment.
                    if (RoleUtility.IsRoleLoadout(template))
                    {
                        current.OwnSlots.Clear();
                        MultiParentUtility.SetExtraParents(current, new List<Loadout>());
                    }
                    // The OLD weapon's personal ammo goes with it.
                    //
                    // The case: a colonist carried an M1917 Enfield with 150 personally assigned rounds of
                    // .30-06 (FMJ). Via the weapon lock she was assigned a Ruger Mini-14 - a different caliber.
                    // Afterwards two displays in the SAME window contradicted each other: her card said "the
                    // weapon needs ammo, but none is set up", and the colony tally still listed the same 150
                    // rounds as "assigned to" her. Both were right.
                    //
                    // Why both: personal ammo lives as an own slot ON the anchor, and an own ammo slot displaces
                    // the template's inherited ammo (Patch_Loadout_MultiParentSlots). The anchor was only
                    // RE-PARENTED here - so the old cartridge stayed, blocked the right one and tied up 150
                    // rounds nobody needs any more.
                    //
                    // The path via "None" already cleaned this up, the path via a WEAPON CHANGE did not - same
                    // question, two answers. That is why the rule now lives here, at the ONE place every
                    // template change goes through.
                    //
                    // Conservative: only CONCRETE ammo that NONE of the now reachable weapons can fire is removed
                    // - those of the new template, plus those from attached packs and sidearms, plus weapons hanging
                    // on the anchor as own slots. A generic placeholder ("Any ammo") only resolves when read anyway
                    // and stays untouched, and the role branch above has already cleared everything.
                    else DropAmmoNoWeaponCanUse(current, template);
                    current.parentID = template.UniqueID;
                }
                return current;
            }
            Loadout link = new Loadout { defaultLoadout = false, label = MarkerPrefix + " " + pawn.LabelShort + " " + pawn.thingIDNumber, parentID = template.UniqueID };
            LoadoutManager.AddLoadout(link);
            pawn.SetLoadout(link);
            // A diagnostic line used to sit here. It produced the finding: the anchor was ALWAYS set
            // correctly - it was only lost when reopening RimWorld's Assign tab, through Better Pawn Control
            // (Compat_BetterPawnControl.cs).
            return link;
        }

        // Which ammo can anyone still fire after the template change? Everything else is a dead assignment -
        // see the reasoning at the call site in EnsureAmmoLink.
        private static void DropAmmoNoWeaponCanUse(Loadout anchor, Loadout template)
        {
            if (anchor == null) return;
            List<LoadoutSlot> concrete = anchor.OwnSlots
                .Where(s => s.thingDef != null && !s.thingDef.IsWeapon && LoadoutUtility.IsAmmoDef(s.thingDef))
                .ToList();
            if (concrete.Count == 0) return;

            var weapons = new HashSet<ThingDef>();
            void Collect(Loadout lo)
            {
                if (lo?.Slots == null) return;
                foreach (LoadoutSlot s in lo.Slots)
                    if (s.thingDef != null && s.thingDef.IsWeapon) weapons.Add(s.thingDef);
            }
            Collect(template);
            foreach (Loadout extra in MultiParentUtility.GetExtraParents(anchor)) Collect(extra);
            foreach (LoadoutSlot s in anchor.OwnSlots)
                if (s.thingDef != null && s.thingDef.IsWeapon) weapons.Add(s.thingDef);

            var usable = new HashSet<ThingDef>();
            foreach (ThingDef w in weapons)
                foreach (ThingDef a in LoadoutUtility.GetAmmoTypesFor(w))
                    if (a != null) usable.Add(a);

            foreach (LoadoutSlot dead in concrete.Where(s => !usable.Contains(s.thingDef)))
                anchor.RemoveSlot(dead);
        }

        // Does this pawn currently have any extra ammo set up at all? (gates the "Change ammo" button's
        // summary text without forcing an anchor to exist just to check.)
        public static List<LoadoutSlot> ExtraAmmoSlots(Pawn pawn)
        {
            Loadout lo = GetAmmoLeaf(pawn);
            return lo != null ? lo.OwnSlots.ToList() : new List<LoadoutSlot>();
        }

        // "Deliberately carries no ammo at all" - distinct from "no personal override yet" (an anchor with
        // zero ammo-type OwnSlots is ambiguous between the two; see Patch_Loadout_MultiParentSlots, which
        // reads this to decide whether to suppress the template's own inherited ammo down to nothing rather
        // than letting it silently show back up).
        public static bool IsAmmoCleared(Loadout anchor)
        {
            CEReloadedGameComponent comp = CEReloadedGameComponent.Cached;
            return anchor != null && comp != null && comp.ammoExplicitlyCleared.Contains(anchor.UniqueID);
        }

        public static void SetAmmoCleared(Loadout anchor, bool cleared)
        {
            CEReloadedGameComponent comp = CEReloadedGameComponent.Cached;
            if (anchor == null || comp == null) return;
            if (cleared) comp.ammoExplicitlyCleared.Add(anchor.UniqueID);
            else comp.ammoExplicitlyCleared.Remove(anchor.UniqueID);
        }

        // Every Equipment Pack this pawn is currently carrying - a live reference (MultiParentUtility),
        // not a baked copy, so editing a Pack later is reflected automatically. A pawn can carry several
        // at once - Equipment Packs are pure supply now (no weapon content competing for one "slot"), so
        // auto-assign hands out a fitting subset (see AssignPlanner.ChoosePacksFor).
        // The extra-parents list can also hold ONE sidearm Weapon Loadout (SidearmAssignmentUtility) -
        // filtered out here by content, so a sidearm never shows up misidentified as a pack.
        public static List<Loadout> GetPawnPacks(Pawn pawn)
        {
            Loadout leaf = GetAmmoLeaf(pawn);
            if (leaf == null) return new List<Loadout>();
            return MultiParentUtility.GetExtraParents(leaf).Where(EquipmentPackUtility.IsEquipmentPackLoadout).ToList();
        }

        // "What supply CONTENT does this pawn carry" - at slot level, not pack-object level. GetPawnPacks next to
        // it answers a different, equally valid question ("which pack OBJECTS hang on it") and stays unchanged -
        // but anyone wanting to know WHAT the pawn carries was blind to everything coming via a ROLE: a role
        // does not attach its packs as extra parents, RoleUtility.Merge bakes the whole content into ONE flat
        // loadout. That is the point of a role, not a bug - but every caller of GetPawnPacks simply did not see
        // role-carried supply.
        // This showed up as four symptoms before the common cause was known: the availability tooltip on the
        // pack cell showed only directly attached content (missing precisely the scarce medicine from the
        // role), the role carrier was missing from "Assigned to" entirely, another colonist was listed with 2
        // instead of 32, and the demand numbers were correspondingly too low - the mod planned no resupply for
        // part of the colony.
        //
        // What counts as a role's pack content: everything that is NOT a weapon, NOT a shield and NOT ammo.
        // That is exactly the definition of an Equipment Pack (pure supply), just read from the flat union
        // instead of a separate object. Generic entries count too, unless they are ammo placeholders
        // (GetWeaponForGenericAmmo != null).
        public static List<LoadoutSlot> CarriedPackSlots(Pawn pawn)
        {
            var result = new List<LoadoutSlot>();
            if (pawn == null) return result;
            foreach (Loadout pack in GetPawnPacks(pawn))
                if (pack?.OwnSlots != null) result.AddRange(pack.OwnSlots);

            result.AddRange(RoleSupplySlots(DisplayLoadout(pawn.GetLoadout())));
            return result;
        }

        // The same question for a PLANNED role loadout that is not on any pawn yet: the preview in the Assign
        // tab only knows the plan, never the live anchor, so it saw the role content past CarriedPackSlots -
        // and, lacking a pack object, put it into the AMMO group. One definition, two entry points.
        public static List<LoadoutSlot> RoleSupplySlots(Loadout role)
        {
            var result = new List<LoadoutSlot>();
            if (role == null || !RoleUtility.IsRoleLoadout(role) || role.Slots == null) return result;
            foreach (LoadoutSlot s in role.Slots)
                if (IsSupplySlot(s)) result.Add(s);
            return result;
        }

        // A slot that is supply - not combat gear. Deliberately here and not in every caller, so that
        // "what is pack content" has exactly one definition.
        private static bool IsSupplySlot(LoadoutSlot s)
        {
            if (s == null) return false;
            if (s.thingDef != null)
                return !s.thingDef.IsWeapon
                    && !LoadoutUtility.IsShieldApparel(s.thingDef)
                    && !LoadoutUtility.IsAmmoDef(s.thingDef);
            if (s.genericDef != null)
                return LoadoutUtility.GetWeaponForGenericAmmo(s.genericDef) == null;
            return false;
        }

        // The single mutation entrypoint for "this pawn now carries exactly these packs" - used by both
        // the manual Assign Loadouts "Pack:" cell and the auto-assign apply step. A live reference, never
        // touches the shared template or copies any slots. Preserves whatever sidearm (SidearmAssignmentUtility)
        // already shares the same extra-parents list - this only ever touches the pack portion of it. An
        // empty combined result detaches - and cleans up the anchor entirely if it now carries nothing at
        // all (no extra ammo either), so detaching everything doesn't leave an inert hidden object behind.
        public static void SetPawnPacks(Pawn pawn, Loadout template, List<Loadout> packs)
        {
            if (pawn == null || template == null) return;
            List<Loadout> newPacks = (packs ?? new List<Loadout>()).Where(p => p != null).Distinct().ToList();
            Loadout existing = GetAmmoLeaf(pawn);
            List<Loadout> currentPacks = existing != null
                ? MultiParentUtility.GetExtraParents(existing).Where(EquipmentPackUtility.IsEquipmentPackLoadout).ToList()
                : new List<Loadout>();
            List<Loadout> others = existing != null
                ? MultiParentUtility.GetExtraParents(existing).Where(l => !EquipmentPackUtility.IsEquipmentPackLoadout(l)).ToList()
                : new List<Loadout>();
            List<Loadout> combined = others.Concat(newPacks).ToList();
            if (combined.Count == 0)
            {
                // Every loadout we assign lives on the pawn's anchor permanently now (see AssignTemplate) -
                // clearing the last pack just empties the extra-parents list, it never tears the anchor
                // down and reverts to a bare template assignment.
                if (existing == null) return;
                MultiParentUtility.SetExtraParents(existing, new List<Loadout>());
                UnseedOwnAmmoForDetached(existing, currentPacks.Concat(others));
                CombatReadiness.Invalidate();
                return;
            }
            Loadout leaf = EnsureAmmoLink(pawn, template);
            MultiParentUtility.SetExtraParents(leaf, combined);
            // Clean up the detached ones first, then seed the new ones - see UnseedOwnAmmoForDetached.
            UnseedOwnAmmoForDetached(leaf, currentPacks.Except(newPacks));
            // Same ammo-starvation-on-attach fix as SidearmAssignmentUtility.SetPawnSidearms (see its own
            // comment for the full mechanism) - packs are documented as never containing an ammo-consuming
            // weapon, but nothing structurally enforces that, so this stays a cheap no-op safety net rather
            // than an assumption.
            SeedOwnAmmoForNewlyAttached(leaf, newPacks.Except(currentPacks));
            // Readiness cache invalidation (2026-08-01: a pawn the widget flagged as
            // having a problem showed fully green/fine on their own Assign Loadouts card right after this
            // exact mutation path - this is the SINGLE shared entrypoint for attaching/detaching a pack, and
            // it never told CombatReadiness a real update just happened, same class of gap already fixed for
            // Dialog_AssignLoadouts' own commit paths).
            CombatReadiness.Invalidate();
        }

        // If this pawn already has a personal ammo override (Dialog_ChangeAmmo - any own ammo slot on their
        // anchor), attaching a NEW sidearm/pack whose own default ammo isn't part of that override yet would
        // otherwise vanish entirely: Patch_LoadoutMultiParent's own-ammo filter treats the anchor's own ammo
        // list as a COMPLETE replacement for every inherited ammo slot, including one just attached this
        // moment (2026-08-01: "attaching a sidearm to an already ammo-personalized pawn
        // silently starves the new weapon of ammo" - repro was a rifle+personalized-ammo pawn later getting
        // a pistol sidearm, whose caliber then generated zero demand, so CE never hauled ammo for it).
        // Scoped to just the NEWLY attached loadout(s)' own ammo types, not a full re-seed of everything
        // effective - that would silently overwrite whatever the player already chose for the main weapon
        // or an earlier sidearm/pack. Deliberately does NOT fire when the pawn instead explicitly cleared
        // ALL personal ammo (IsAmmoCleared) - that is a deliberate "no ammo at all" choice, not an
        // in-progress personalization, and a newly attached item should respect it the same way the
        // existing ones already do rather than silently reintroducing ammo demand.
        internal static void SeedOwnAmmoForNewlyAttached(Loadout anchor, IEnumerable<Loadout> newlyAttached)
        {
            if (anchor == null || newlyAttached == null) return;
            if (anchor.OwnSlots == null || !anchor.OwnSlots.Any(s => LoadoutUtility.IsAmmoDef(s.thingDef))) return;
            HashSet<ThingDef> haveOwn = anchor.OwnSlots.Where(s => LoadoutUtility.IsAmmoDef(s.thingDef)).Select(s => s.thingDef).ToHashSet();
            foreach (Loadout lo in newlyAttached)
            {
                if (lo?.Slots == null) continue;
                foreach (LoadoutSlot s in lo.Slots.Where(s => LoadoutUtility.IsAmmoDef(s.thingDef)))
                    if (haveOwn.Add(s.thingDef)) anchor.AddSlot(new LoadoutSlot(s.thingDef, s.count));
            }
        }

        // The counterpart to SeedOwnAmmoForNewlyAttached. Seeding existed, cleanup never did: a detached
        // sidearm only loses its parent link, while the ammo copied onto the PERSONAL anchor on attach stays
        // there forever. The colonist then permanently demands ammo for a weapon they no longer carry.
        //
        // It was most visible with thrown explosives, because they are THEIR OWN ammo (IsSelfAmmoWeapon): the
        // leftover slot is then the explosive itself, and that is a WEAPON. Failure scenario, step by step:
        // untick the sidearm "W| satchel charge" (the cell then shows the red X, it really is gone), open the
        // resupply window: the charge is still listed as to be produced, with an EMPTY Assigned column, and at
        // the same time does NOT appear under "unneeded bills". The same leftover slot explains both:
        // ComputeWeaponDemand sees it (weapon slot -> bill counts as needed), ComputeAmmoNeedForLoadouts skips
        // it (IsSelfAmmoWeapon -> no assignment in the grid).
        //
        // Only what meets both conditions is removed: it came from the loadout just detached, AND none of the
        // weapons still carried can fire it. A personal ammo choice for a weapon still carried stays
        // untouched - that is the whole purpose of the anchor.
        // GetLoadoutAmmoDefs with IsSelfAmmoWeapon, not Dialog_ChangeAmmo.RelevantAmmoPool: the pool explicitly
        // leaves out throwables (it answers "which types may the player pick here"), and exactly those must
        // not slip through the net here.
        internal static void UnseedOwnAmmoForDetached(Loadout anchor, IEnumerable<Loadout> detached)
        {
            if (anchor?.OwnSlots == null || detached == null) return;
            // Materialize once: callers pass in LINQ expressions, and this function iterates twice
            // (concrete ammo, then the placeholders).
            List<Loadout> detachedList = detached.Where(l => l != null).ToList();
            HashSet<ThingDef> fromDetached = new HashSet<ThingDef>();
            foreach (Loadout lo in detachedList)
            {
                if (lo?.Slots == null) continue;
                foreach (LoadoutSlot s in lo.Slots)
                    if (LoadoutUtility.IsAmmoDef(s.thingDef)) fromDetached.Add(s.thingDef);
            }
            if (fromDetached.Count == 0) return;
            // Read after re-parenting, not before: `Slots` is the union of own slots and the REMAINING parents,
            // i.e. exactly what the colonist carries from now on.
            HashSet<ThingDef> stillRelevant = new HashSet<ThingDef>();
            foreach (LoadoutSlot s in anchor.Slots ?? new List<LoadoutSlot>())
            {
                ThingDef w = s.thingDef;
                if (w == null || !w.IsWeapon) continue;
                foreach (Def ammo in LoadoutUtility.GetLoadoutAmmoDefs(w, LoadoutUtility.IsSelfAmmoWeapon(w)))
                    if (ammo is ThingDef atd) stillRelevant.Add(atd);
            }
            foreach (LoadoutSlot own in anchor.OwnSlots.ToList())
                if (own.thingDef != null && fromDetached.Contains(own.thingDef) && !stillRelevant.Contains(own.thingDef))
                    anchor.RemoveSlot(own);

            // The SAME for the generic placeholder. The block above only removes CONCRETE ammo - an
            // "Any ammo for <weapon>" the colonist had set for the detached sidearm stayed on the anchor forever.
            // That is the same damage this function was written against, just via the other path: the
            // placeholder resolves back into real ammo in Patch_Loadout_MultiParentSlots, so the colonist keeps
            // demanding it for a weapon they no longer carry. It surfaced somewhere else entirely - the copy
            // button in the ammo window counted three entries where the window showed two.
            // A placeholder names its own weapon, so the condition is narrower than above: removed if the weapon
            // came from what was just detached AND no remaining weapon is the same one.
            HashSet<ThingDef> weaponsFromDetached = new HashSet<ThingDef>();
            foreach (Loadout lo in detachedList)
            {
                if (lo?.Slots == null) continue;
                foreach (LoadoutSlot s in lo.Slots)
                    if (s.thingDef != null && s.thingDef.IsWeapon) weaponsFromDetached.Add(s.thingDef);
            }
            if (weaponsFromDetached.Count > 0)
            {
                HashSet<ThingDef> stillCarriedWeapons = new HashSet<ThingDef>();
                foreach (LoadoutSlot s in anchor.Slots ?? new List<LoadoutSlot>())
                    if (s.thingDef != null && s.thingDef.IsWeapon) stillCarriedWeapons.Add(s.thingDef);
                foreach (LoadoutSlot own in anchor.OwnSlots.ToList())
                {
                    if (!LoadoutUtility.IsGenericAmmoDef(own.genericDef)) continue;
                    ThingDef forWeapon = LoadoutUtility.GetWeaponForGenericAmmo(own.genericDef);
                    if (forWeapon != null && weaponsFromDetached.Contains(forWeapon) && !stillCarriedWeapons.Contains(forWeapon))
                        anchor.RemoveSlot(own);
                }
            }
        }
    }
}
