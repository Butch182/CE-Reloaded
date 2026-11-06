using System.Collections.Generic;
using System.Linq;
using Verse;
using HarmonyLib;
using CombatExtended;

namespace CEReloaded
{
    // Zwei Anliegen am Geburts- und Sterbepunkt jedes CE-Loadouts.
    //
    // 1) HYGIENE (immer aktiv): CE vergibt Loadout-IDs als Max+1 und RECYCELT sie damit nach jedem
    //    Loeschen (LoadoutManager.GetUniqueLoadoutID, dekompiliert nachgelesen). Unsere Nebenbuecher
    //    (Rollen-, Pack-, Managed-, AmmoCleared-, ExtraParents-, PackPlan-Zuordnungen) und CEs eigene
    //    parentID-Ketten koennen Zahlen halten, deren Loadout ueber CEs EIGENE Wege verschwunden ist
    //    (CE-Editor, fremde Mods) - LoadoutDeletionUtility putzt nur, was durch UNSERE Loeschpfade
    //    geht. Ein NEU angelegtes Loadout, das so eine Zahl erbt, gilt sonst ab der ersten Sekunde als
    //    Rolle/Pack/"bewusst ohne Munition", oder stehengebliebene Kind-parentIDs haengen sich an das
    //    voellig fremde neue Objekt. Deshalb wird die ID bei der Geburt ueberall vergessen. Sicher,
    //    weil jede Registrierung im Mod erst NACH LoadoutManager.AddLoadout laeuft - die UniqueID
    //    entsteht erst dort, das steht als Kommentar an allen Aufrufstellen.
    //
    // 2) Die Aufruferkette (Caller) bleibt - sie steht in der Fehlerzeile des SetLoadoutById-Netzes
    //    weiter unten und ist dort der einzige Hinweis, WER eine tote Loadout-Nummer gefragt hat.
    //
    // AUSGEBAUT 2026-09-06: das Diagnoselogging fuer den Zuweisungs-Verlust. Es hat in drei vollen
    // Zuweisungslaeufen nichts gefangen, und der Lazy-Umbau hat genau die Objekt-Buchhaltung entfernt,
    // in der der Verlust vermutet wurde.

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

    // Eine tote Loadout-Nummer darf kein Ausrufezeichen werfen (2026-09-04).
    //
    // Im Player.log eines Testlaufs stand genau EINE Ausnahme, unmittelbar nach unserer eigenen
    // ADD-Zeile fuer eine frisch angelegte Rolle:
    //   ArgumentNullException: Value cannot be null. Parameter name: loadout
    //     at CombatExtended.Utility_Loadouts.SetLoadoutById (Verse.Pawn pawn, System.Int32 loadoutId)
    //     (wrapper managed-to-native) System.Reflection.RuntimeMethodInfo.InternalInvoke
    //     Rethrow as TargetInvocationException
    //
    // CEs Fassung ist zwei Zeilen lang: GetLoadoutById(id), und ist das Ergebnis null, fliegt eine
    // ArgumentNullException. Nachgesehen (dekompiliert) ruft CE selbst diese Methode NIRGENDS auf, und
    // dieser Mod auch nicht - der Aufrufer kommt ueber Reflexion von aussen. Wer das ist, sagt der Stapel
    // nicht: RimWorld kuerzt ihn beim Rethrow ab.
    //
    // Was hier trotzdem sicher feststeht: es wurde nach einer Nummer gefragt, die es nicht (mehr) gibt -
    // und genau das ist in diesem Spiel ein NORMALER Zustand, kein Programmfehler. CE vergibt IDs als
    // Max+1 und recycelt sie deshalb nach jedem Loeschen (siehe oben); jede Zahl, die irgendwo laenger
    // liegt als das Objekt dahinter, zeigt irgendwann ins Leere. Eine Ausnahme aus einer Zeichenroutine
    // reisst dabei den Rest des Bildaufbaus mit, und der Spieler sieht ein halb gezeichnetes Fenster.
    //
    // Deshalb: kein Wurf, sondern ein benanntes Nichts - plus EINE Fehlerzeile, die die Nummer und die
    // Aufruferkette nennt. Damit ist der naechste Auftritt kein Raetsel mehr, sondern eine Adresse.
    // Ein AUFLOESBARES Loadout laeuft unveraendert durch CEs eigenen Weg (und damit durch unseren
    // Anker-Patch auf SetLoadout).
    [HarmonyPatch(typeof(Utility_Loadouts), nameof(Utility_Loadouts.SetLoadoutById))]
    public static class Patch_Utility_Loadouts_SetLoadoutById
    {
        public static bool Prefix(Pawn pawn, int loadoutId)
        {
            if (LoadoutManager.GetLoadoutById(loadoutId) != null) return true;
            Log.ErrorOnce("[CE Reloaded] SetLoadoutById(" + (pawn?.LabelShort ?? "?") + ", #" + loadoutId
                + "): es gibt kein Loadout mit dieser Nummer - Aufruf ignoriert statt Ausnahme."
                + " Aufrufer: " + LoadoutLifecycleDiag.Caller(), 0x5E7B1D ^ loadoutId);
            return false;
        }
    }
}
