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

        // Prefix und Finalizer standen hier nur fuer die Messung [CER DIAG perf-vanilla-assign] (2026-08-17,
        // wieder entfernt): sie hat ihre Zahl geliefert - 1,64 ms je Bild fuer den ganzen Reiter, davon
        // 0,43 ms unsere Patches. Damit ist dieser Reiter als Luefter-Ursache abgehakt, und ein Patch, der
        // nur noch misst, ist selbst Kosten.
        public static void Postfix(Rect rect, RimWorld.MainTabWindow_PawnTable __instance)
        {
            if (!(__instance is RimWorld.MainTabWindow_Assign)) return;
            EnsureCacheForCurrentGame();

            // Position the button row at bottom-right, but use the window's actual visible area (the rect
            // passed here is the inner content rect of the window).
            // 130 -> 210 -> 150 (2026-08-30: zu sperrig): "Advanced Loadout
            // Management" war zu lang, der Knopf heisst jetzt "Loadout Planner" und ist so breit wie "Arm my Colony"
            // daneben - drei gleich breite Knoepfe in einer Reihe. ButtonTextFit faengt lange Uebersetzungen ab.
            float btnWidth = 150f;
            float btnHeight = 30f;
            float armWidth = 150f;
            float gap = 8f;

            float rowY = rect.height - btnHeight - 10f;
            if (rowY < 0f) rowY = 0f;   // fallback if rect.height is too small/negative

            Rect btnRect = new Rect(rect.width - btnWidth - 10f, rowY, btnWidth, btnHeight);
            Rect armRect = new Rect(btnRect.x - gap - armWidth, rowY, armWidth, btnHeight);
            // "Fetch gear now" zwischen Bereitschaft und Arm my Colony (2026-08-30) - dieselbe
            // Reihenfolge wie im Kopf unseres Fensters.
            float fetchWidth = 150f;
            Rect fetchRect = new Rect(armRect.x - gap - fetchWidth, rowY, fetchWidth, btnHeight);
            // Die Anzeige bekommt den GANZEN Platz links der Knopfreihe (2026-09-03) - vorher waren es
            // feste 150 Punkte, und auf Deutsch stand darin "9 brauchen
            // Aufmerksaml", mitten im Wort abgeschnitten, waehrend links davon die halbe Leiste leer war.
            // DrawReadinessWidget richtet sich in diesem Feld selbst rechtsbuendig aus und nimmt nur so viel,
            // wie der Text braucht; ein groesseres Feld aendert also nichts fuer Sprachen, die ohnehin passen.
            // Dieselbe Rechnung wie im Kopf unseres eigenen Fensters (Dialog_CEReloadedTabs).
            Rect readinessRect = new Rect(0f, rowY, Mathf.Max(0f, fetchRect.x - gap), btnHeight);

            // Reuse an already-open tabs window instead of stacking a second, desynced one on top (2026-08-02
            // fix - same class of bug as Patch_LoadoutEditButton's own identical fix).
            // EINE Schriftstufe fuer die ganze Reihe (2026-08-30: auf Deutsch standen
            // 'Ausruestung jetzt holen' und 'Kolonie bewaffnen' klein, 'Beladungsplaner' daneben normal). ButtonTextFit
            // misst je Knopf; drei gleich breite Knoepfe nebeneinander muessen sich aber einigen - siehe ButtonRowFont.
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
