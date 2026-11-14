using System.Collections.Generic;
using System.Linq;
using System.Text;
using HarmonyLib;
using Verse;
using CombatExtended;

namespace CEReloaded
{
    // CORRECTION (2026-08-09, verified): the sentence that used to stand here - "CE's Loadout.Slots getter
    // returns a FRESH list every call" - is WRONG, and it is the dangerous kind of wrong because the next
    // change to this file would be reasoned from it. CE's getter only builds a new union list when the
    // loadout HAS a parent; for a parentless loadout it returns OwnSlots itself, the live list. That is why
    // this Postfix must never mutate __result in place and always builds its own `combined` copy below -
    // writing into __result would be writing into the loadout's real slot list. (Same fact already recorded
    // outside this file: "non-parented loadouts: Slots == OwnSlots (live list); never Clear+re-add from
    // itself".) The Postfix as written is safe; only the reasoning above it was not.
    //
    // This is the one patch that gives real 1..N Loadout
    // attachment on top of CE's native single-parent chain (see MultiParentUtility for who actually uses
    // it - Roles and per-pawn anchors, never plain Weapon Loadouts or Equipment Packs), AND the one place
    // a pawn's personal ammo choice fully REPLACES rather than merely supplements what their Weapon
    // Loadout template would otherwise provide.
    [HarmonyPatch(typeof(Loadout), nameof(Loadout.Slots), MethodType.Getter)]
    public static class Patch_Loadout_MultiParentSlots
    {
        // Threaded across the WHOLE recursive walk instead of reallocated fresh per top-level Postfix call
        // (2026-08-01: the old code's own comment claimed the guard was "shared across the
        // whole walk", but AppendFrom's recursive `lo.Slots` access re-enters this SAME patched getter as a
        // brand-new top-level call, which built its OWN fresh guard set - a genuine 2-node extra-parent
        // cycle would recurse unbounded into an uncatchable StackOverflowException instead of the "silently
        // stop" the old comment claimed). Not currently reachable via any exposed picker/paste path today -
        // MultiParentUtility's own value-exclusion rules mean a value can never itself become a key - but
        // worth hardening properly rather than leaving a broken safety net for a future call site. Single
        // static, not [ThreadStatic]: RimWorld's game/UI logic that ever touches Loadout.Slots runs on the
        // main thread only, and a nested call here is always part of the SAME call stack as its root, never
        // concurrent with it.
        private static HashSet<int> activeGuard;

        public static void Postfix(Loadout __instance, ref List<LoadoutSlot> __result)
        {
            // ...WithAncestors, not just its own (measured live): CE's ParentSlots reads
            // `ancestor.OwnSlots` and thus the raw field, not this patched getter - so the extra parents of
            // an ANCESTOR fell through completely. Concretely: a colonist on a role with an Equipment Pack
            // attached never had its contents in their slots, although every display in the mod showed
            // them (those read role.Slots, where this postfix runs). The result was ordered supplies that
            // nobody picked up. The fix deliberately sits HERE and not in the assignment: that way it works
            // on every path by which a role reaches a colonist - including CE's own dropdown - and not
            // only on ours.
            // No double counting: RoleUtility.Merge deliberately does NOT bake packs into the role's
            // OwnSlots, so the pack slots are not in __result yet; GetExtraParentIdsWithAncestors also
            // deduplicates against the child's own extra parents.
            List<int> extra = MultiParentUtility.GetExtraParentIdsWithAncestors(__instance);
            bool hasExtra = extra != null && extra.Count > 0;

            // A pawn's own ammo picks (AmmoAssignmentUtility/Dialog_ChangeAmmo) are meant to be the
            // pawn's OWN, complete, freely-editable ammo choice - not extras stacked alongside whatever
            // the shared Weapon Loadout template happened to bake in at generation time. As soon as this
            // object owns any ammo-type slot of its own, every OTHER inherited ammo slot (the template's
            // own default, or a sidearm's, pulled in via the parent chain or an extra-parent below) is
            // dropped - only this object's own ammo choices remain. Non-ammo slots (weapons, packs,
            // medicine, food, ...) are completely unaffected; this only ever touches ammo. A pawn can also
            // be explicitly cleared down to ZERO ammo (AmmoAssignmentUtility.IsAmmoCleared) - an anchor
            // with no ammo slots of its own is otherwise indistinguishable from "never customized", so
            // without this flag the template's default would silently reappear the moment the last
            // personal ammo type is removed.
            // A loop instead of LINQ+ToList, and IsAmmoCleared only as the SECOND condition.
            // Previously every call built a list that was discarded immediately in the normal case, and
            // always called IsAmmoCleared - which does a Current.Game.GetComponent, i.e. a component
            // scan, per call. Both happened BEFORE the early-out below, which almost always applies. This
            // postfix hangs off Loadout.Slots, and that is not a pure UI getter:
            // ThinkNode_ConditionalHasLoadout, JobGiver_CheckReload and Utility_HoldTracker call it from
            // CE's think tree.
            // Only the order and the allocations differ - the CONDITION is character for character
            // the same as before (any own ammo slot OR explicitly cleared).
            // LoadoutUtility.IsAmmoSlot instead of just IsAmmoDef(thingDef) (the bug came back): a
            // generic own slot ("Any ammo for X") IS an own ammo choice. Not counting it as one here was
            // the actual cause of the bug that an earlier fix on the dialog side only half caught - the
            // decision is made here, not there.
            // Failure scenario: main weapon and sidearm both on "Any ammo", one checkbox unticked again.
            // Afterwards the anchor only had the other weapon's generic slot; hasOwnAmmoSlot was false,
            // and the dialog had correctly set 'cleared' to false (there is an own choice) - so the filter
            // below never ran, and the template's default ammo came back for BOTH weapons, ticked next to
            // the still-set "Any ammo".
            bool hasOwnAmmoSlot = false;
            List<LoadoutSlot> own = __instance.OwnSlots;
            if (own != null)
                for (int i = 0; i < own.Count; i++)
                    if (IsRealAmmoSlot(own[i])) { hasOwnAmmoSlot = true; break; }
            bool hasOwnAmmo = hasOwnAmmoSlot || AmmoAssignmentUtility.IsAmmoCleared(__instance);

            if (!hasExtra && !hasOwnAmmo)
                return;

            List<LoadoutSlot> combined = new List<LoadoutSlot>(__result);
            if (hasExtra)
            {
                bool isRoot = activeGuard == null;
                if (isRoot) activeGuard = new HashSet<int>();
                HashSet<int> guard = activeGuard;
                // Mark the instance itself visited unconditionally (not gated on Add's return value) - a
                // legitimate multi-level chain (A extra-parents B, B extra-parents C, no cycle) still needs
                // to fully descend into B's own extras even though B was just marked visited by the caller a
                // moment ago. The actual cycle check is the Add(id) below, right before descending into a
                // NEW loadout - that's the one that stops a true cycle (...-> A -> B -> A) instead of hanging.
                guard.Add(__instance.UniqueID);
                try
                {
                    foreach (int id in extra) AppendFrom(id, combined, guard);
                }
                finally
                {
                    if (isRoot) activeGuard = null;
                }
            }
            if (hasOwnAmmo)
            {
                // Reference-equality against OwnSlots itself, NOT a def-membership check (2026-08-01,
                // live: a pawn's own ammo count showed doubled everywhere - "Assigned to:
                // <pawn> x300" for a personal override of 150). The old check kept a slot if its DEF matched
                // one of the pawn's own ammo defs - but the INHERITED copy of that exact same def (from the
                // template's parent chain, still sitting in `combined` from CE's own native union) trivially
                // matches too, so both the inherited slot AND the real own slot survived the filter and got
                // summed together by every "Assigned to:" reader (they all do .Where(def-match).Sum(count),
                // which has no way to know two entries are the SAME logical slot). "Own ammo replaces
                // inherited ammo" has to mean "keep only slots that ARE literally in OwnSlots", not "keep
                // only slots whose def happens to also appear in OwnSlots" - those are very different checks
                // once an inherited slot shares a def with an owned one, which is exactly the common case
                // (personalizing ammo for the SAME weapon the template already listed a default for).
                HashSet<LoadoutSlot> ownSlotRefs = new HashSet<LoadoutSlot>(__instance.OwnSlots ?? new List<LoadoutSlot>());
                // IsAmmoSlot instead of IsAmmoDef(thingDef) here too (same bug): the old test let
                // `s.thingDef == null` through wholesale, and with it ALSO an inherited generic ammo
                // placeholder from the template - which then sat next to the colonist's own choice, the
                // same ammo twice. "Own ammo replaces inherited" must hold for both spellings of the same
                // thing, otherwise it depends on how the template happens to be built. Supply generics from
                // packs ("Any Medicine") are NOT ammo and stay untouched - that is exactly what IsAmmoSlot
                // separates.
                combined = combined.Where(s => !IsRealAmmoSlot(s) || ownSlotRefs.Contains(s)).ToList();
            }
            __result = combined;
        }

        // ONLY real ammo falls under the own-ammo rule - not throwing weapons (the cause behind THREE
        // symptoms: a colonist not fetching stickbombs from the shelf, a satchel charge showing
        // 'Assigned 0' colony-wide, 'Fetch gear now' not naming the sidearm). CE's grenades and explosives
        // are AmmoThing, so IsAmmoSlot calls the stickbomb slot of a sidearm template 'ammo' - and as soon
        // as the anchor has its own ammo (always, as soon as a weapon needs ammo), every grenade slot of
        // the extra parents was dropped here. CE never saw the slot, never fetched, never counted.
        // A throwing weapon is a Weapon def; real ammo is not.
        // Same distinction as LoadoutUtility.IsRealAmmoDef, just for a slot (which can also be generic).
        private static bool IsRealAmmoSlot(LoadoutSlot s) =>
            LoadoutUtility.IsAmmoSlot(s) && (s.thingDef == null || LoadoutUtility.IsRealAmmoDef(s.thingDef));

        // Recursive on purpose (an extra parent's own .Slots goes through this same patched getter, so a
        // Role attached to another Role's extra-parent chain would resolve correctly) - the guard set passed
        // in is the SAME shared activeGuard the whole traversal uses, so a cycle anywhere in the chain is
        // actually caught here rather than only at whichever level happens to be the current top-level call.
        private static void AppendFrom(int id, List<LoadoutSlot> into, HashSet<int> guard)
        {
            if (!guard.Add(id)) return;
            Loadout lo = LoadoutManager.GetLoadoutById(id);
            // CE's own Slots getter never returns null (fresh list every call, confirmed above) and neither
            // does this same patch's own Postfix - but a THIRD mod's Harmony patch on the same getter could
            // still break that contract, so guard against it rather than trust it transitively forever.
            if (lo?.Slots == null) return;
            into.AddRange(lo.Slots);
        }
    }
}
