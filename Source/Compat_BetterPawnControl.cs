using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using RimWorld;
using Verse;

namespace CEReloaded
{
    // Better Pawn Control (Workshop 1541460369) fuehrt je Kolonist und Richtlinie ein eigenes Buch ueber
    // CEs Loadout-ID: beim SCHLIESSEN von RimWorlds Zuweisungs-Reiter macht er einen Schnappschuss
    // (AssignManager.SaveCurrentState, Harmony-Postfix auf Window.PreClose), beim OEFFNEN spielt er ihn
    // zurueck (AssignManager.LoadState -> Utility_Loadouts.SetLoadoutById, Postfix auf
    // MainTabWindow_PawnTable.PostOpen). Beides ueber Reflexion, ohne CE Reloaded zu kennen.
    //
    // Das kollidiert mit VanillaTabKeeper: der schliesst den Reiter, sobald ein Fenster dieses Mods aufgeht
    // (Schnappschuss = Stand VORHER), und oeffnet ihn wieder, sobald das letzte zugeht (Rueckspielen =
    // alles ueberschrieben, was dazwischen zugewiesen wurde). Sichtbar als "5 von 9 Zuweisungen aus Arm
    // my Colony sind weg, sobald man den Planer schliesst" (2026-09-07, im Test dreimal reproduziert):
    // wer VORHER schon denselben Anker hatte, ueberlebt, weil BPC dieselbe ID zurueckschreibt; wer im Lauf
    // einen neuen Anker bekam, wird auf den alten Stand zurueckgesetzt. Derselbe Weg trifft jede Waffe,
    // die ueber die Zelle im Zuweisungs-Reiter gesetzt wird (im Test stand dort 'Nothing'), und ohne
    // Reiter-Verstecker jede Zuweisung, die bei geschlossenem Reiter passiert - beim naechsten Oeffnen.
    //
    // Loesung: BPCs Schnappschuss AUFFRISCHEN, sobald das letzte Fenster dieses Mods zugeht - also
    // bevor der Reiter wieder aufgeht und BPC zurueckspielt. Dann spielt er genau das zurueck, was
    // gerade zugewiesen wurde. Das ist derselbe Aufruf, den BPC beim Reiter-Schliessen selbst macht,
    // mit derselben Kolonistenliste; er aendert nichts am Spiel, nur an BPCs Buch.
    // Reflexion statt Verweis: BPC ist nicht Voraussetzung dieses Mods, und ohne ihn passiert hier nichts.
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
                // Colonists() steht auf der generischen Basisklasse Manager<T>; DeclaredOnly je Stufe, weil
                // FlattenHierarchy internal-statische Basismitglieder nicht sicher mitnimmt.
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

        // BPCs Buch auf den JETZIGEN Stand bringen. Nur im laufenden Spiel und nur mit Karte - BPC selbst
        // liest Find.CurrentMap.uniqueID ohne Pruefung.
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
