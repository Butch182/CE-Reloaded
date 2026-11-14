using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using RimWorld;
using Verse;

namespace CEReloaded
{
    // Better Pawn Control (Workshop 1541460369) keeps its own record of CE's loadout ID per colonist and
    // policy: when RimWorld's Assign tab CLOSES it takes a snapshot (AssignManager.SaveCurrentState,
    // Harmony postfix on Window.PreClose), when it OPENS it restores it (AssignManager.LoadState ->
    // Utility_Loadouts.SetLoadoutById, postfix on MainTabWindow_PawnTable.PostOpen). Both via
    // reflection, without knowing about CE Reloaded.
    //
    // That collides with VanillaTabKeeper: it closes the tab as soon as a window of this mod opens
    // (snapshot = state BEFORE), and reopens it as soon as the last one closes (restore = everything
    // assigned in between is overwritten). Visible as several Arm my Colony assignments vanishing as
    // soon as the planner closed (reproduced three times): colonists who already had the same anchor
    // BEFORE survive, because BPC writes back the same ID; those who got a new anchor during the run
    // are reset to the old state. The same path hits every weapon set via the cell in the Assign tab
    // (it showed 'Nothing'), and without the tab hider every assignment made while the tab is
    // closed - on the next open.
    //
    // Fix: REFRESH BPC's snapshot as soon as the last window of this mod closes - i.e. before the tab
    // reopens and BPC restores. It then restores exactly what was just assigned. This is the same call
    // BPC makes itself when the tab closes, with the same colonist list; it changes nothing in the
    // game, only BPC's records.
    // Reflection instead of a reference: BPC is not a dependency of this mod, and without it nothing
    // happens here.
    public static class BetterPawnControlCompat
    {
        private static bool probed;
        private static bool available;
        private static MethodInfo saveCurrentState;   // AssignManager.SaveCurrentState(List<Pawn>)
        private static MethodInfo colonists;          // Manager<AssignLink>.Colonists() -> IEnumerable<Pawn>

        public static bool Available
        {
            get { if (!probed) Probe(); return available; }
        }

        private static void Probe()
        {
            probed = true;
            try
            {
                Assembly asm = LoadedModManager.RunningMods
                    .SelectMany(m => m.assemblies.loadedAssemblies)
                    .FirstOrDefault(a => a.GetName().Name == "BetterPawnControl");
                if (asm == null) return;
                Type assignManager = asm.GetType("BetterPawnControl.AssignManager");
                if (assignManager == null) return;
                const BindingFlags any = BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic;
                saveCurrentState = assignManager.GetMethod("SaveCurrentState", any, null, new[] { typeof(List<Pawn>) }, null);
                // Colonists() lives on the generic base class Manager<T>; DeclaredOnly per level, because
                // FlattenHierarchy does not reliably include internal static base members.
                for (Type t = assignManager; t != null && colonists == null; t = t.BaseType)
                    colonists = t.GetMethod("Colonists", any | BindingFlags.DeclaredOnly, null, Type.EmptyTypes, null);
                available = saveCurrentState != null && colonists != null;
                if (!available)
                    Log.Warning("[CE Reloaded] Better Pawn Control is loaded, but its AssignManager.SaveCurrentState/Colonists could not be resolved - "
                        + "loadouts assigned while its Assign tab is closed may be reverted when the tab opens.");
            }
            catch (Exception e)
            {
                Log.Warning("[CE Reloaded] Better Pawn Control probe failed: " + e.Message);
            }
        }

        // Bring BPC's records up to the CURRENT state. Only in a running game and only with a map - BPC
        // itself reads Find.CurrentMap.uniqueID without a check.
        public static void RefreshAssignSnapshot()
        {
            if (!Available) return;
            if (Current.ProgramState != ProgramState.Playing || Find.CurrentMap == null) return;
            try
            {
                IEnumerable<Pawn> pawns = colonists.Invoke(null, null) as IEnumerable<Pawn>;
                saveCurrentState.Invoke(null, new object[] { pawns != null ? pawns.ToList() : new List<Pawn>() });
            }
            catch (Exception e)
            {
                Log.WarningOnce("[CE Reloaded] Better Pawn Control snapshot refresh failed: " + e.Message, 0x43455242);
            }
        }
    }
}
