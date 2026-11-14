using System;
using RimWorld;
using Verse;
using HarmonyLib;

namespace CEReloaded
{
    // RimWorld's own colonist tabs (Assign/Work/Schedule - all MainTabWindow_PawnTable) stay open while
    // one of our windows is on top, and keep drawing every frame: measured at 464-467 ms/s at 130 fps,
    // i.e. two thirds of the total window load, for a tab nobody can see at that moment. So it is
    // closed.
    //
    // Mechanics: as soon as the FIRST window of this mod enters the WindowStack while a colonist tab is
    // open, the tab is closed (EscapeCurrentTab) and remembered. As soon as the LAST window of this mod
    // leaves the stack, the same tab reopens (SetCurrentTab) - so after closing, the player ends up where
    // they were rather than on the bare map.
    // Only colonist tables, not Inspect/Architect: those are cheap and carry context (the selected
    // colonist) that closing would lose. Chained windows (Arm my Colony: one closes, the next opens)
    // keep the tab closed, because a mod window is still on the stack when the first one closes.
    //
    // Detection by namespace instead of a base class: the 14 windows of this mod all inherit directly
    // from Window, and a FloatMenu or a confirmation dialog (Verse) must neither close the tab nor
    // trigger its reopening.
    public static class VanillaTabKeeper
    {
        private static MainButtonDef closedTab;

        private static bool IsOurs(Window w) => w != null && w.GetType().Namespace == "CEReloaded";

        public static void NotifyWindowAdded(Window w)
        {
            if (!IsOurs(w) || Current.ProgramState != ProgramState.Playing) return;
            MainTabsRoot root = Find.MainTabsRoot;
            MainButtonDef open = root?.OpenTab;
            if (open?.TabWindow is MainTabWindow_PawnTable)
            {
                closedTab = open;
                root.EscapeCurrentTab(false);
            }
        }

        public static void NotifyWindowRemoved(Window w)
        {
            if (!IsOurs(w) || AnyOfOursStillOpen()) return;
            // When the Assign tab reopens, Better Pawn Control restores ITS snapshot of the loadout IDs -
            // the one it took when the tab was closed in NotifyWindowAdded above, i.e. BEFORE everything
            // the player assigned in our windows (several assignments vanished as soon as the planner was
            // closed). So first bring its records up to date here, and only then restore the tab. Also
            // without a remembered tab: an assignment made while the tab was closed would otherwise suffer
            // the same fate on the next open.
            // Full reasoning: Compat_BetterPawnControl.cs.
            BetterPawnControlCompat.RefreshAssignSnapshot();
            if (closedTab == null) return;
            MainButtonDef tab = closedTab;
            closedTab = null;
            if (Current.ProgramState != ProgramState.Playing) return;
            Find.MainTabsRoot?.SetCurrentTab(tab, false);
        }

        private static bool AnyOfOursStillOpen()
        {
            var windows = Find.WindowStack?.Windows;
            if (windows == null) return false;
            for (int i = 0; i < windows.Count; i++)
                if (IsOurs(windows[i])) return true;
            return false;
        }
    }

    [HarmonyPatch(typeof(WindowStack), nameof(WindowStack.Add))]
    public static class Patch_WindowStack_Add_VanillaTabHider
    {
        public static void Postfix(Window window) => VanillaTabKeeper.NotifyWindowAdded(window);
    }

    // Window.Close() and every type-based overload of TryRemove go through this overload - one
    // place is enough.
    [HarmonyPatch(typeof(WindowStack), nameof(WindowStack.TryRemove), new Type[] { typeof(Window), typeof(bool) })]
    public static class Patch_WindowStack_TryRemove_VanillaTabHider
    {
        public static void Postfix(Window window, bool __result)
        {
            if (__result) VanillaTabKeeper.NotifyWindowRemoved(window);
        }
    }
}
