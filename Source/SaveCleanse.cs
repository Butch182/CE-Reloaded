using System.Collections.Generic;
using System.Linq;
using RimWorld;
using Verse;
using CombatExtended;

namespace CEReloaded
{
    // "Prepare save for uninstall".
    //
    // The problem this button solves: part of every colonist's equipment is not stored in CE,
    // but only in this mod.
    //   - Packs and sidearms hang off the anchor and role via the extra-parent table
    //     (MultiParentUtility). CE knows only ONE parent - without the mod they are simply gone.
    //   - Personal ammo sits as its own slot on the anchor and REPLACES the template's inherited ammo
    //     (Patch_Loadout_MultiParentSlots). CE's own Loadout.Slots just concatenates both, and CE's AI
    //     ADDS identical rows (JobGiver_UpdateLoadout.GetPrioritySlot subtracts per row from the
    //     inventory) - so without the mod every colonist would want to carry template + personal ammo.
    //   - The anchor itself would then be a visible "[Pawn-Loadout] Name 1234" entry in CE's list.
    //
    // The approach: while the mod is still running, Loadout.Slots returns the finished, correct
    // composition. Exactly that is baked into ordinary CE loadouts without extra parents and without
    // anchors. Afterwards nothing is left that only this mod understands.
    public static class SaveCleanseUtility
    {
        // While this runs, Patch_Utility_Loadouts_SetLoadout must not redirect to the anchor - otherwise
        // assigning the baked copy would just re-point the anchor we are dissolving.
        internal static bool Running;

        public struct Result
        {
            public int unwrapped;   // colonist without personal customization: straight back to the shared template/role
            public int baked;       // colonist with customization: gets its own flat copy
            public int flattened;   // role (or other loadout) with attached packs: packs merged in
            public int anchorsDeleted;
        }

        public static Result Run()
        {
            Result r = new Result();
            if (LoadoutManager.Loadouts == null) return r;
            Running = true;
            try
            {
                // 1. Colonists first, while roles and templates still return their full composition.
                //    Same two collection queries as FindOrphanedAnchors - including cryptosleep, caravans,
                //    transport pods and world pawns.
                var pawns = new HashSet<Pawn>();
                foreach (Pawn p in PawnsFinder.AllMapsWorldAndTemporary_Alive) if (p != null) pawns.Add(p);
                foreach (Pawn p in PawnsFinder.AllMapsCaravansAndTravellingTransporters_Alive) if (p != null) pawns.Add(p);

                foreach (Pawn pawn in pawns)
                {
                    Loadout anchor = pawn.GetLoadout();
                    if (!AmmoAssignmentUtility.IsAmmoLink(anchor)) continue;

                    Loadout template = anchor.parentID > 0 ? LoadoutManager.GetLoadoutById(anchor.parentID) : null;
                    bool personal = (anchor.OwnSlots != null && anchor.OwnSlots.Count > 0)
                        || MultiParentUtility.GetExtraParents(anchor).Count > 0
                        || AmmoAssignmentUtility.IsAmmoCleared(anchor);

                    if (!personal)
                    {
                        // No template (deleted) and no customization: the anchor carries nothing - then "none".
                        pawn.SetLoadout(template ?? LoadoutManager.DefaultLoadout);
                        r.unwrapped++;
                        continue;
                    }

                    string baseName = template != null ? (LoadoutUtility.LoadoutDisplayName(template) ?? template.label) : null;
                    string label = string.IsNullOrEmpty(baseName)
                        ? pawn.LabelShort
                        : "CER_SaveCleanse_BakedLabelFmt".Translate(pawn.LabelShort, baseName).ToString();
                    Loadout baked = new Loadout { defaultLoadout = false, label = label };
                    foreach (LoadoutSlot s in CloneSlots(anchor.Slots)) baked.AddSlot(s);
                    LoadoutManager.AddLoadout(baked);
                    pawn.SetLoadout(baked);
                    r.baked++;
                }

                // 2. Merge everything that still has extra parents (roles with packs) into itself.
                //    Copy the full state first, THEN clear its own slots - with extra parents Slots returns
                //    a new list, but some of its entries are the same objects.
                //    Also detach the CE parent: the snapshot already contains its slots, and CE's Slots
                //    would otherwise append them a second time - the AI adds both rows.
                //    Possible as soon as a role with packs has been given a parent loadout in CE's own
                //    window. Children of that role then inherit its full state, also without duplication.
                foreach (Loadout lo in LoadoutManager.Loadouts.ToList())
                {
                    if (lo == null || lo.defaultLoadout || AmmoAssignmentUtility.IsAmmoLink(lo)) continue;
                    if (MultiParentUtility.GetExtraParents(lo).Count == 0) continue;
                    List<LoadoutSlot> snapshot = CloneSlots(lo.Slots);
                    lo.parentID = 0;
                    MultiParentUtility.SetExtraParents(lo, new List<Loadout>());
                    foreach (LoadoutSlot s in lo.OwnSlots.ToList()) lo.RemoveSlot(s);
                    foreach (LoadoutSlot s in snapshot) lo.AddSlot(s);
                    r.flattened++;
                }

                // 3. The anchors themselves. Nothing points to them anymore; LoadoutDeletionUtility also
                //    cleans this mod's side records (IDs are recycled by CE).
                List<Loadout> anchors = LoadoutManager.Loadouts.Where(AmmoAssignmentUtility.IsAmmoLink).ToList();
                r.anchorsDeleted = LoadoutDeletionUtility.DeleteAll(anchors);

                // Better Pawn Control remembers loadout numbers and restores them when its tab opens -
                // without a fresh snapshot it would point at the anchors just deleted.
                BetterPawnControlCompat.RefreshAssignSnapshot();
                CombatReadiness.Invalidate();
            }
            finally
            {
                Running = false;
            }
            return r;
        }

        // New slot objects, never pass on the existing ones. AddSlot later merges identical entries
        // and ADDS them up - that matches CE's reading (see header comment), so demand is unchanged.
        private static List<LoadoutSlot> CloneSlots(IEnumerable<LoadoutSlot> source)
        {
            var result = new List<LoadoutSlot>();
            if (source == null) return result;
            foreach (LoadoutSlot s in source.ToList())
            {
                if (s == null || s.count <= 0) continue;
                LoadoutSlot copy;
                if (s.thingDef != null) copy = new LoadoutSlot(s.thingDef, s.count);
                else if (s.genericDef != null) copy = new LoadoutSlot(s.genericDef, s.count);
                else continue;
                copy.countType = s.countType;
                result.Add(copy);
            }
            return result;
        }
    }
}
