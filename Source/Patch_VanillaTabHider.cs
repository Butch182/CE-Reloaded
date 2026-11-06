using System;
using RimWorld;
using Verse;
using HarmonyLib;

namespace CEReloaded
{
    // RimWorlds eigener Kolonisten-Reiter (Assign/Work/Schedule - alles MainTabWindow_PawnTable) bleibt
    // offen, waehrend eines unserer Fenster darueber liegt, und zeichnet dabei jedes Bild weiter:
    // gemessen 464-467 ms/s bei 130 Bildern/s, also zwei Drittel der gesamten Fensterlast, fuer einen
    // Reiter, den in dem Moment niemand sieht (gemessen 2026-08-28). Deshalb wird er geschlossen.
    //
    // Mechanik: sobald das ERSTE Fenster dieses Mods in den WindowStack kommt und gerade ein
    // Kolonisten-Reiter offen ist, wird der Reiter geschlossen (EscapeCurrentTab) und gemerkt. Sobald das
    // LETZTE Fenster dieses Mods den Stack verlaesst, geht derselbe Reiter wieder auf (SetCurrentTab) -
    // der Spieler landet also nach dem Schliessen wieder dort, wo er war, und nicht auf der blanken Karte.
    // Nur Kolonisten-Tabellen, nicht Inspect/Architect: die sind billig und tragen Kontext (markierter
    // Kolonist), den ein Zuklappen verlieren wuerde. Verkettete Fenster (Arm my Colony: eins schliesst,
    // das naechste oeffnet) halten den Reiter zu, weil beim Schliessen noch ein Mod-Fenster im Stack liegt.
    //
    // Erkennung ueber den Namensraum statt einer Basisklasse: die 14 Fenster dieses Mods erben alle direkt
    // von Window, und ein FloatMenu oder eine Rueckfrage (Verse) darf den Reiter weder schliessen noch
    // sein Wiederoeffnen ausloesen.
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
            // Better Pawn Control spielt beim Wiederoeffnen des Zuweisungs-Reiters SEINEN Schnappschuss
            // der Loadout-IDs zurueck - den er beim Schliessen oben in NotifyWindowAdded gemacht hat, also
            // VOR allem, was der Spieler in unseren Fenstern zugewiesen hat (2026-09-07, "5 von 9
            // Zuweisungen weg, sobald man den Planer schliesst"). Deshalb hier zuerst sein Buch auf den
            // jetzigen Stand bringen, dann erst den Reiter zurueckholen. Auch ohne gemerkten Reiter: eine
            // Zuweisung bei geschlossenem Reiter traefe sonst dasselbe beim naechsten Oeffnen.
            // Begruendung in voller Laenge: Compat_BetterPawnControl.cs.
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

    // Window.Close() und jeder Type-Ueberlauf von TryRemove laufen durch diese Ueberladung - eine
    // Stelle reicht.
    [HarmonyPatch(typeof(WindowStack), nameof(WindowStack.TryRemove), new Type[] { typeof(Window), typeof(bool) })]
    public static class Patch_WindowStack_TryRemove_VanillaTabHider
    {
        public static void Postfix(Window window, bool __result)
        {
            if (__result) VanillaTabKeeper.NotifyWindowRemoved(window);
        }
    }
}
