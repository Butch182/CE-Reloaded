using System.Collections.Generic;
using RimWorld;
using Verse;
using HarmonyLib;
using UnityEngine;

namespace CEReloaded
{
    [HarmonyPatch(typeof(RimWorld.MainTabWindow_PawnTable), "DoWindowContents")]
    public static class Patch_MainTabWindow_Assign
    {
        // Lazily built once, reused across frames - the readiness ampel's fix-actions need a live Dialog_
        // GenerateLoadouts/AssignLoadouts to open Squad Setup with, but constructing one re-scans the whole
        // map (RefreshBenchesAndAmmo-equivalent cost elsewhere), so this must never happen every frame just
        // to draw a status dot. Only ever actually invoked from inside a fix-action's own click closure.
        // These are static fields - they survive for the WHOLE PROCESS lifetime, not just one save, since
        // RimWorld doesn't restart the process on load. Without cachedForGame below, loading a different
        // save after ever using this widget once would keep handing Dialog_SquadSetup the PREVIOUS save's
        // Dialog_GenerateLoadouts - including its own availableWeaponsCache snapshot of THAT colony's
        // owned weapons - silently showing stale data for the new one.
        private static Dialog_GenerateLoadouts cachedLoadoutsDialog;
        private static Dialog_AssignLoadouts cachedAssignDialog;
        private static Game cachedForGame;

        private static void EnsureCacheForCurrentGame()
        {
            if (cachedForGame == Current.Game) return;
            cachedForGame = Current.Game;
            cachedLoadoutsDialog = null;
            cachedAssignDialog = null;
        }

        // A prefix and finalizer used to live here only for the [CER DIAG perf-vanilla-assign] measurement
        // (since removed): it delivered its number - 1.64 ms per frame for the whole tab, 0.43 ms of that
        // from our patches. That rules this tab out as the cause of high load, and a patch that only
        // measures is a cost in itself.
        public static void Postfix(Rect rect, RimWorld.MainTabWindow_PawnTable __instance)
        {
            if (!(__instance is RimWorld.MainTabWindow_Assign)) return;
            EnsureCacheForCurrentGame();

            // Position the button row at bottom-right, but use the window's actual visible area (the rect
            // passed here is the inner content rect of the window).
            // 130 -> 210 -> 150 (too bulky): "Advanced Loadout Management" was too long, the button is now
            // "Loadout Planner" and as wide as "Arm my Colony" next to it - three equally wide buttons in a
            // row. ButtonTextFit handles long translations.
            float btnWidth = 150f;
            float btnHeight = 30f;
            float armWidth = 150f;
            float gap = 8f;

            float rowY = rect.height - btnHeight - 10f;
            if (rowY < 0f) rowY = 0f;   // fallback if rect.height is too small/negative

            Rect btnRect = new Rect(rect.width - btnWidth - 10f, rowY, btnWidth, btnHeight);
            Rect armRect = new Rect(btnRect.x - gap - armWidth, rowY, armWidth, btnHeight);
            // "Fetch gear now" sits between the readiness display and Arm my Colony - the same
            // order as in our window's header.
            float fetchWidth = 150f;
            Rect fetchRect = new Rect(armRect.x - gap - fetchWidth, rowY, fetchWidth, btnHeight);
            // The readiness display gets ALL the space left of the button row - it used to be a fixed
            // 150 points, and longer translations were cut off mid-word while half the bar to the left
            // stayed empty.
            // DrawReadinessWidget right-aligns itself within this rect and only takes as much as the text
            // needs, so a larger rect changes nothing for languages that already fit.
            // Same calculation as in the header of our own window (Dialog_CEReloadedTabs).
            Rect readinessRect = new Rect(0f, rowY, Mathf.Max(0f, fetchRect.x - gap), btnHeight);

            // Reuse an already-open tabs window instead of stacking a second, desynced one on top (2026-08-02
            // fix - same class of bug as Patch_LoadoutEditButton's own identical fix).
            // ONE font size for the whole row (in longer translations two buttons dropped to the small font
            // while the third stayed normal). ButtonTextFit measures per button, but three equally wide
            // buttons side by side have to agree - see ButtonRowFont.
            GameFont rowFont = LoadoutUtility.ButtonRowFont(
                (fetchRect, "CER_LoadoutUtility_FetchGearButtonLabel".Translate().ToString()),
                (armRect, "CER_LoadoutUtility_ArmColonyButtonLabel".Translate().ToString()),
                (btnRect, "CER_MainTabWindow_OpenButton".Translate().ToString()));
            GameFont prevRowFont = Text.Font;
            Text.Font = rowFont;
            if (LoadoutUtility.ButtonTextFit(btnRect, "CER_MainTabWindow_OpenButton".Translate().ToString()))
            {
                if (!Find.WindowStack.IsOpen<Dialog_CEReloadedTabs>()) Find.WindowStack.Add(new Dialog_CEReloadedTabs());
            }
            TooltipHandler.TipRegion(btnRect, "CER_MainTabWindow_OpenWorkflowTip".Translate());

            if (LoadoutUtility.DrawArmColonyButton(armRect))
            {
                // Same first-ever-open workflow guide Dialog_CEReloadedTabs.PostOpen already shows
                // (2026-08-02: the flashiest, most inviting first click on a fresh colony -
                // this exact button - skipped it entirely, since it builds Dialog_ArmColony directly and
                // never opens the tabs window at all).
                Dialog_CERWorkflowGuide.OpenIfFirstTime();
                Find.WindowStack.Add(new Dialog_ArmColony(
                    cachedLoadoutsDialog ?? (cachedLoadoutsDialog = new Dialog_GenerateLoadouts()),
                    new Dialog_GenerateAmmoBills(),
                    cachedAssignDialog ?? (cachedAssignDialog = new Dialog_AssignLoadouts())));
            }

            LoadoutUtility.DrawFetchGearButton(fetchRect);
            Text.Font = prevRowFont;

            LoadoutUtility.DrawReadinessWidget(readinessRect,
                () => cachedLoadoutsDialog ?? (cachedLoadoutsDialog = new Dialog_GenerateLoadouts()),
                () => cachedAssignDialog ?? (cachedAssignDialog = new Dialog_AssignLoadouts()));
        }
    }
}
