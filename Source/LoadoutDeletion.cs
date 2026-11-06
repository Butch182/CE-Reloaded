using System.Collections.Generic;
using System.Linq;
using Verse;
using CombatExtended;

namespace CEReloaded
{
    // Der einzige richtige Weg, ein Loadout zu loeschen. CEs eigenes LoadoutManager.RemoveLoadout kennt
    // genau EINEN Verweis auf das geloeschte Objekt - Pawns, deren AssignedLoadouts-WERT dieses Objekt
    // ist -, und setzt die aufs Default. Alles andere in diesem Mod zeigt ueber eine ZAHL darauf, und
    // keine dieser Zahlen wurde je aufgeraeumt (2026-08-13):
    //
    //   1. Der persoenliche Anker eines Kolonisten haengt per CE-eigener parentID an der Vorlage. Ein
    //      personalisierter Kolonist ist NICHT direkt der Vorlage zugewiesen, sondern seinem Anker -
    //      RemoveLoadout sieht ihn also gar nicht. In derselben Sitzung faellt das nicht auf, weil CEs
    //      Loadout._parent den geloeschten Verweis noch gecacht haelt; nach dem naechsten Laden ist der
    //      Cache leer, GetLoadoutById findet nichts, ParentLoadout gibt null zurueck (und setzt _parentID
    //      NICHT zurueck - das passiert nur im Zweig "gefunden"), und DisplayLoadout bricht beim Anker ab.
    //      Beim Kolonisten steht dann sein interner Ankername als Loadout.
    //   2. CEs GetUniqueLoadoutID ist Max(uniqueID)+1, IDs werden also nach dem Loeschen WIEDERVERWENDET -
    //      und Anker tragen fast immer die hoechste. Jede stehengebliebene Zahl zeigt damit frueher oder
    //      spaeter auf ein fremdes, neues Objekt: eine frisch angelegte Vorlage erbt die Packs eines toten
    //      Kolonisten, ein neues Pack gilt als "von uns erzeugt", ein Plan findet das falsche Pack wieder.
    //
    // Deshalb wird hier JEDE Stelle geputzt, an der eine Loadout-ID ueberlebt - die drei Nebenbuecher aus
    // dem Befund plus packPlanIds, das dieselbe Falle hat (Wert = Pack-ID; die Selbstheilung in
    // FindGeneratedPackForPlan greift erst, wenn die ID ins Leere zeigt, und genau das tut sie bei einer
    // recycelten ID nicht mehr).
    public static class LoadoutDeletionUtility
    {
        public static void Delete(Loadout lo)
        {
            if (lo == null) return;
            DeleteInternal(lo, new HashSet<int>());
        }

        // Bequemlichkeit fuer die Sammel-Loeschknoepfe, und zugleich die Stelle, an der die Liste
        // materialisiert wird: mehrere Aufrufstellen iterierten bisher direkt ueber LoadoutManager.Loadouts,
        // waehrend das Loeschen genau diese Liste veraendert.
        public static int DeleteAll(IEnumerable<Loadout> loadouts)
        {
            List<Loadout> doomed = (loadouts ?? Enumerable.Empty<Loadout>()).Where(l => l != null).ToList();
            var visited = new HashSet<int>();
            foreach (Loadout lo in doomed) DeleteInternal(lo, visited);
            return doomed.Count;
        }

        // Geburts-Hygiene fuer eine RECYCELTE ID (Patch_LoadoutManager_AddLoadout, 2026-08-31): ein
        // frisch angelegtes Loadout darf nichts erben, was ein GLEICHNUMMERIERTER Vorgaenger hinterliess,
        // der an unseren Loeschpfaden vorbei verschwunden ist (CE-Editor, fremde Mods). Zwei Dinge:
        // die Nebenbuecher vergessen die ID, und stehengebliebene Kind-parentIDs werden abgehaengt -
        // NICHT geloescht wie beim Loeschpfad: ein Anker mit haengender parentID kann noch einem
        // lebenden Kolonisten gehoeren, und ihn zu loeschen wuerde den aufs Default werfen.
        internal static void ScrubRecycledId(Loadout newborn)
        {
            if (newborn == null) return;
            int id = newborn.UniqueID;
            ForgetSideBooks(id);
            List<Loadout> all = LoadoutManager.Loadouts;
            if (all == null) return;
            Loadout none = all.FirstOrDefault(l => l != null && l.defaultLoadout);
            foreach (Loadout child in all.Where(l => l != null && l != newborn && l.parentID == id).ToList())
                child.parentID = AmmoAssignmentUtility.IsAmmoLink(child) && none != null ? none.UniqueID : 0;
        }

        // visited schuetzt gegen den Fall, dass die Rekursion (Anker ohne eigenen Inhalt werden
        // mitgeloescht) im Kreis laeuft. Ueber unsere eigenen Pfade kann eine Anker-an-Anker-Kette nicht
        // entstehen, aber CEs parentID ist ein frei setzbares Feld, und diese Funktion loescht Dinge.
        private static void DeleteInternal(Loadout lo, HashSet<int> visited)
        {
            if (lo == null || !visited.Add(lo.UniqueID)) return;
            int id = lo.UniqueID;

            RepointChildren(lo, id, visited);
            ForgetSideBooks(id);
            LoadoutManager.RemoveLoadout(lo);
        }

        // Alles, was dieses Loadout als CE-Elternteil hatte, bekommt ein neues Ziel - denn eine
        // stehengebliebene parentID ist nach dem ID-Recycling schlimmer als eine kaputte: sie zeigt dann
        // auf irgendein neues Objekt statt auf nichts.
        private static void RepointChildren(Loadout lo, int id, HashSet<int> visited)
        {
            List<Loadout> all = LoadoutManager.Loadouts;
            if (all == null) return;
            Loadout none = all.FirstOrDefault(l => l != null && l.defaultLoadout);
            foreach (Loadout child in all.Where(l => l != null && l != lo && l.parentID == id).ToList())
            {
                if (!AmmoAssignmentUtility.IsAmmoLink(child))
                {
                    // Ein normales Loadout mit Elternteil (CEs eigener Editor kann das): es verliert die
                    // geerbten Slots, behaelt aber Name und eigenen Inhalt - "kein Elternteil" ist dafuer
                    // ein voellig gueltiger Zustand.
                    child.parentID = 0;
                    continue;
                }
                // Ein Anker OHNE eigenen Inhalt ist reine Buchhaltung: mitloeschen, dann setzt CEs
                // RemoveLoadout seinen Kolonisten sauber aufs Default - genau das, was ein direkt der
                // Vorlage zugewiesener Kolonist auch bekommt.
                if (!HasOwnContent(child)) { DeleteInternal(child, visited); continue; }
                // Ein Anker MIT Inhalt (persoenliche Munition, Packs, Zweitwaffe) wird umgehaengt statt
                // geloescht. Ihn zu loeschen waere stiller Datenverlust aus einer Aktion, deren Name nur
                // "loesche diese Vorlage" sagt; auf das Default umgehaengt behaelt der Kolonist seine
                // Extras, liest sich als "keine Vorlage" (nicht als interner Ankername), und die naechste
                // Zuweisung haengt denselben Anker samt Extras wieder an die neue Vorlage - das ist die
                // dokumentierte Zusage von EnsureAmmoLink.
                child.parentID = none != null ? none.UniqueID : 0;
            }
        }

        // Traegt dieser Anker etwas, das beim Loeschen verloren ginge? Das "bewusst leer"-Kennzeichen zaehlt
        // mit: es IST eine Entscheidung des Spielers, auch wenn sie sich als Abwesenheit von Slots zeigt.
        // OwnSlots null-sicher: CEs _slots geht ungeschuetzt durch Scribe_Collections und kommt aus einem
        // Spielstand ohne Slots als null zurueck - CE selbst raeumt es mit ?. ab, und beide Stellen in
        // Patch_Loadout_MultiParentSlots pruefen es ebenfalls. Hier haengt ein Loeschen daran.
        private static bool HasOwnContent(Loadout anchor) =>
            (anchor.OwnSlots != null && anchor.OwnSlots.Count > 0)
            || MultiParentUtility.GetExtraParentIds(anchor).Count > 0
            || AmmoAssignmentUtility.IsAmmoCleared(anchor);

        private static void ForgetSideBooks(int id)
        {
            MultiParentUtility.ForgetLoadout(id);
            // Die Verknuepfung Rolle -> ihr Loadout (2026-08-17). Sie ist der Grund, warum eine Rolle nicht
            // mehr am Namen erkannt werden muss - und damit die fuenfte Zahl, die hier geputzt gehoert.
            // Bliebe sie stehen, gaebe sich die naechste Vorlage mit der recycelten ID als Rolle aus.
            RoleUtility.ForgetRoleLoadout(id);
            CEReloadedGameComponent comp = CEReloadedGameComponent.Cached;
            if (comp == null) return;
            comp.ammoExplicitlyCleared.Remove(id);
            // Die Sperrliste fuer Rollen/Loadouts (2026-09-04) - sechste Zahl, gleiche Falle: eine
            // stehengebliebene ID wuerde nach dem Recycling eine wildfremde neue Vorlage sperren, und
            // niemand faende je heraus, warum die Automatik sie meidet.
            comp.disabledLoadouts.Remove(id);
            comp.managedLoadoutIds.Remove(id);
            comp.packLoadoutIds.Remove(id);
            foreach (string plan in comp.packPlanIds.Where(kv => kv.Value == id).Select(kv => kv.Key).ToList())
                comp.packPlanIds.Remove(plan);
        }
    }
}
