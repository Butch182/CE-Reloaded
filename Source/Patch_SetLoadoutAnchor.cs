using Verse;
using HarmonyLib;
using CombatExtended;

namespace CEReloaded
{
    // CombatExtended.Utility_Loadouts.SetLoadout(pawn, loadout) - the extension method EVERY caller in the
    // game uses to reassign a pawn's loadout, ours included - is a raw dictionary set
    // (LoadoutManager.AssignedLoadouts[pawn] = loadout), completely unaware of a personalized pawn's hidden
    // "[Pawn-Loadout]" anchor (AmmoAssignmentUtility). AmmoAssignmentUtility.AssignTemplate already re-points an
    // existing anchor instead of orphaning it for the 3 call sites this mod controls (Dialog_AssignLoadouts,
    // Dialog_GenerateLoadouts) - those never reach this patch at all, since AssignTemplate re-points the
    // anchor itself and only falls through to pawn.SetLoadout for a pawn with no anchor to preserve.
    //
    // This patch exists ONLY for reassignments that DON'T go through AssignTemplate - another mod calling
    // SetLoadout directly, for instance - where silently losing a pawn's Packs/sidearm(s)/extra ammo would
    // otherwise be a real regression. Deliberately does NOT protect CE's OWN native "Select loadout"
    // dropdown/editor (2026-07-30, by design): reassigning through vanilla CE's own UI should behave
    // exactly like vanilla CE always did, with zero invisible interference from our side - a change in CE's
    // vanilla editor should not drag along any leftovers. A player who wants
    // packs/sidearm preserved across a reassignment has our own Assign Loadouts tab for that, where the
    // carry-over is visible, not a hidden side effect of a menu that knows nothing about our system.
    [HarmonyPatch(typeof(Utility_Loadouts), nameof(Utility_Loadouts.SetLoadout), new[] { typeof(Pawn), typeof(Loadout) })]
    public static class Patch_Utility_Loadouts_SetLoadout
    {
        public static bool Prefix(Pawn pawn, Loadout loadout)
        {
            if (pawn == null) return true;
            // Waehrend der Deinstallations-Vorbereitung wird der Anker gerade aufgeloest - nicht dorthin umlenken.
            if (SaveCleanseUtility.Running) return true;
            // Inside CE's own native picker (per-pawn column quick-pick) or its native loadout browser -
            // step completely out of the way, same scoping the availability-coloring exclusion already uses.
            // InsideNativeLoadoutPick ergaenzt (2026-08-09): CurrentDoCellPawn allein greift hier nie, weil
            // CEs Dropdown das Menue nur BAUT und die Auswahl einen Frame spaeter feuert - siehe die
            // ausfuehrliche Begruendung an der Stelle, wo das Flag gesetzt wird.
            if (Patch_LoadoutTooltip.CurrentDoCellPawn != null || Patch_LoadoutTooltip.InsideVanillaLoadoutEditor
                || Patch_LoadoutTooltip.InsideNativeLoadoutPick) return true;
            // Assigning one of our own internal markers IS a genuine direct assignment - EnsureAmmoLink's
            // own pawn.SetLoadout(link) call for a BRAND NEW anchor relies on this running unintercepted.
            if (loadout != null && (AmmoAssignmentUtility.IsAmmoLink(loadout) || EquipmentPackUtility.IsEquipmentPackLoadout(loadout)))
                return true;
            // Unassigning entirely (null, or the default loadout) is a deliberate full detach, matching
            // AmmoAssignmentUtility.AssignTemplate's own contract (template == null -> no-op there too) -
            // dropping the anchor along with it here is consistent, not a regression.
            if (loadout == null || loadout.defaultLoadout) return true;

            Loadout anchor = AmmoAssignmentUtility.GetAmmoLeaf(pawn);
            if (anchor == null) return true;   // nothing to preserve - plain assignment is already correct
            // An anchor with nothing on it yet (no extra ammo, no packs/sidearms - e.g. one EnsureAmmoLink
            // just created but nothing has been attached to) has nothing worth preserving - let the plain
            // assignment proceed instead of re-pointing it. (Comment corrected 2026-08-01, was stale: neither
            // AmmoAssignmentUtility.SetPawnPacks nor SidearmAssignmentUtility.SetPawnSidearms's own "detach
            // everything" branch calls SetLoadout or removes the anchor in the current code - both just empty
            // the extra-parents list and leave the anchor, however empty, exactly where it is. This guard
            // isn't about catching a caller mid-teardown, it's just the general "nothing here to preserve"
            // case.)
            if (anchor.OwnSlots.Count == 0 && MultiParentUtility.GetExtraParents(anchor).Count == 0) return true;

            // Re-point the EXISTING anchor at the new template instead of letting the pawn get reassigned
            // straight onto it - preserves whatever extra ammo/packs/sidearm(s) it carries.
            anchor.parentID = loadout.UniqueID;
            return false;
        }
    }
}
