using HarmonyLib;
using RimWorld;
using UnityEngine;
using Verse;

namespace CEReloaded
{
    // The vanilla Mod Settings window is cramped for a mod with this many tabs/tuning rows - the Presets
    // grid alone needs real width (7 preset columns) and ends up scrolling/clipping in the default size.
    // Grows the window rather than replacing its size outright (Mathf.Max against whatever vanilla already
    // returned), so this only ever makes it bigger, never smaller/different for some other RimWorld version's
    // own default - and it is a genuinely global change (every mod's settings window gets the same room),
    // not something scoped to just this mod, since Dialog_ModSettings has no per-mod size hook to patch
    // against instead.
    [HarmonyPatch(typeof(Dialog_ModSettings), nameof(Dialog_ModSettings.InitialSize), MethodType.Getter)]
    public static class Patch_ModSettingsSize
    {
        public static void Postfix(ref Vector2 __result)
        {
            float w = Mathf.Min(Mathf.Max(__result.x, 1100f), UI.screenWidth - 40f);
            float h = Mathf.Min(Mathf.Max(__result.y, 820f), UI.screenHeight - 40f);
            __result = new Vector2(w, h);
        }
    }
}
