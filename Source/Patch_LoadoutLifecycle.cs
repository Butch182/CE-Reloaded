using System.Collections.Generic;
using System.Linq;
using Verse;
using HarmonyLib;
using CombatExtended;

namespace CEReloaded
{
    // Two concerns at the birth and death of every CE loadout.
    //
    // 1) HYGIENE (always active): CE assigns loadout IDs as max+1 and therefore RECYCLES them after
    //    every deletion (LoadoutManager.GetUniqueLoadoutID, verified in decompiled code). Our side
    //    records (role, pack, managed, AmmoCleared, ExtraParents, PackPlan mappings) and CE's own
    //    parentID chains can hold numbers whose loadout vanished through CE's OWN paths (CE editor,
    //    other mods) - LoadoutDeletionUtility only cleans up what goes through OUR deletion paths. A
    //    NEWLY created loadout that inherits such a number would otherwise count as a role/pack/
    //    "deliberately without ammo" from its first second, or leftover child parentIDs would attach
    //    to the completely unrelated new object. So the ID is forgotten everywhere at birth. Safe,
    //    because every registration in the mod runs only AFTER LoadoutManager.AddLoadout - the
    //    UniqueID is only created there, which is noted as a comment at every call site.
    //
    // 2) The caller chain stays - it is part of the error line of the SetLoadoutById safety net
    //    further down, and there it is the only clue as to WHO asked for a dead loadout number.
    //
    // REMOVED: the diagnostic logging for lost assignments. It caught nothing in three full
    // assignment runs, and the lazy-template rework removed exactly the object bookkeeping in which
    // the loss was suspected.

    public static class LoadoutLifecycleDiag
    {
        internal static string Caller()
        {
            var st = new System.Diagnostics.StackTrace(2, false);
            var names = new List<string>();
            for (int i = 0; i < st.FrameCount && names.Count < 4; i++)
            {
                var m = st.GetFrame(i)?.GetMethod();
                if (m == null) continue;
                string t = m.DeclaringType?.Name ?? "?";
                if (t.Contains("Harmony") || t.Contains("LoadoutLifecycle") || t.Contains("LoadoutDeletionUtility")) continue;
                names.Add(t + "." + m.Name);
            }
            return string.Join(" < ", names);
        }
    }

    [HarmonyPatch(typeof(LoadoutManager), nameof(LoadoutManager.AddLoadout))]
    public static class Patch_LoadoutManager_AddLoadout
    {
        public static void Postfix(Loadout loadout)
        {
            if (loadout == null) return;
            LoadoutDeletionUtility.ScrubRecycledId(loadout);
        }
    }

    // A dead loadout number must not throw an exception.
    //
    // A Player.log contained exactly ONE exception, immediately after our own
    // ADD line for a freshly created role:
    //   ArgumentNullException: Value cannot be null. Parameter name: loadout
    //     at CombatExtended.Utility_Loadouts.SetLoadoutById (Verse.Pawn pawn, System.Int32 loadoutId)
    //     (wrapper managed-to-native) System.Reflection.RuntimeMethodInfo.InternalInvoke
    //     Rethrow as TargetInvocationException
    //
    // CE's version is two lines long: GetLoadoutById(id), and if the result is null an
    // ArgumentNullException is thrown. Checked (decompiled): CE itself calls this method NOWHERE, and
    // neither does this mod - the caller comes in via reflection from outside. The stack doesn't say
    // who: RimWorld truncates it on rethrow.
    //
    // What is certain anyway: a number was requested that does not exist (anymore) - and in this
    // game that is a NORMAL state, not a bug. CE assigns IDs as max+1 and therefore recycles them
    // after every deletion (see above); any number kept somewhere longer than the object behind it
    // eventually points into nothing. An exception from a drawing routine takes the rest of the frame
    // with it, and the player sees a half-drawn window.
    //
    // Hence: no throw, but a named nothing - plus ONE error line naming the number and the caller
    // chain. That way the next occurrence is not a mystery but an address.
    // A RESOLVABLE loadout passes unchanged through CE's own path (and thus through our anchor
    // patch on SetLoadout).
    [HarmonyPatch(typeof(Utility_Loadouts), nameof(Utility_Loadouts.SetLoadoutById))]
    public static class Patch_Utility_Loadouts_SetLoadoutById
    {
        public static bool Prefix(Pawn pawn, int loadoutId)
        {
            if (LoadoutManager.GetLoadoutById(loadoutId) != null) return true;
            Log.ErrorOnce("[CE Reloaded] SetLoadoutById(" + (pawn?.LabelShort ?? "?") + ", #" + loadoutId
                + "): no loadout with this id - call ignored instead of throwing."
                + " Caller: " + LoadoutLifecycleDiag.Caller(), 0x5E7B1D ^ loadoutId);
            return false;
        }
    }
}
