using System.Reflection;
using HarmonyLib;
using Verse;

namespace CEReloaded
{
    // ====== Click-through when closing our windows ======
    //
    // Reproducible, always the same way: clicking "Cancel" in the supply window or in "Arm my Colony"
    // closes the window AND at the same time opens the dropdown that sits at the same screen position in
    // RimWorld's Assign tab underneath - both times the "Food policy" column with Lavish/Fine/Simple/...
    //
    // The cause is in RimWorld's own code, not a mistake on our side:
    // RimWorld's policy columns draw their cell with Widgets.Dropdown, which goes through
    // Widgets.ButtonInvisibleDraggable. That function does NOT query the IMGUI event but
    // Unity's polling:
    //
    //     if (Input.GetMouseButtonDown(0) && Mouse.IsOver(butRect))
    //         buttonInvisibleDraggable_activeControl = controlID;
    //     ...
    //     if (buttonInvisibleDraggable_activeControl == controlID && Input.GetMouseButtonUp(0)
    //         && Mouse.IsOver(butRect)) return DraggableResult.Pressed;
    //
    // Input.GetMouseButtonDown is true for the whole FRAME - for every draw pass, regardless of whether
    // an event was already consumed or which window is on top. That is exactly why
    // absorbInputAroundWindow does not help here: it works on the event side, and this code reads the
    // other one. So the mouse press arms the cell underneath while our window is still open; on release
    // our window is gone, the cell is exposed, and it fires.
    // buttonInvisibleDraggable_activeControl is ONE global field - it remembers exactly one armed
    // button, and that survives our window closing.
    //
    // Hence the fix right there: when one of OUR windows closes, the armed state is cleared.
    // That cannot cancel anything wanted - whoever was just using our modal window had no drag in
    // progress in RimWorld's table underneath, since that table was blocked.
    // Deliberately limited to our own windows (namespace check): for RimWorld's own windows this
    // behavior is their business, and a global intervention would change foreign behavior without
    // a known problem.
    [HarmonyPatch(typeof(WindowStack), nameof(WindowStack.TryRemove), new[] { typeof(Window), typeof(bool) })]
    public static class Patch_ClickThroughGuard
    {
        // Private field, hence AccessTools. Null-tolerant: if RimWorld ever renames it, only this
        // guard is lost - nothing else depends on it.
        private static readonly FieldInfo ActiveControl =
            AccessTools.Field(typeof(Widgets), "buttonInvisibleDraggable_activeControl");

        public static void Postfix(Window window, bool __result)
        {
            if (!__result || window == null || ActiveControl == null) return;
            if (window.GetType().Namespace != "CEReloaded") return;
            ActiveControl.SetValue(null, 0);
        }
    }
}
