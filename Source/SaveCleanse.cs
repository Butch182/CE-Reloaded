using System.Collections.Generic;
using System.Linq;
using RimWorld;
using Verse;
using CombatExtended;

namespace CEReloaded
{
    // "Spielstand fuer die Deinstallation vorbereiten" (2026-09-17).
    //
    // Das Problem, das dieser Knopf loest: ein Teil der Ausruestung jedes Kolonisten steht nicht in CE,
    // sondern nur in diesem Mod.
    //   - Packs und Zweitwaffen haengen ueber die Zusatz-Eltern-Tabelle (MultiParentUtility) an Anker und
    //     Rolle. CE kennt nur EINEN Elternteil - ohne den Mod sind sie einfach weg.
    //   - Persoenliche Munition liegt als eigener Slot am Anker und VERDRAENGT die geerbte Munition der
    //     Vorlage (Patch_Loadout_MultiParentSlots). CEs eigenes Loadout.Slots haengt beide nur aneinander,
    //     und CEs KI ADDIERT gleiche Zeilen (JobGiver_UpdateLoadout.GetPrioritySlot zieht pro Zeile vom
    //     Inventar ab) - ohne den Mod wollte jeder Kolonist also Vorlage + persoenliche Munition tragen.
    //   - Der Anker selbst waere danach ein sichtbarer "[Pawn-Loadout] Name 1234"-Eintrag in CEs Liste.
    //
    // Der Weg: solange der Mod noch laeuft, liefert Loadout.Slots die fertige, richtige Zusammensetzung.
    // Genau die wird in gewoehnliche CE-Loadouts ohne Zusatz-Eltern und ohne Anker eingebacken. Danach ist
    // nichts mehr uebrig, was nur dieser Mod versteht.
    public static class SaveCleanseUtility
    {
        // Solange das laeuft, darf Patch_Utility_Loadouts_SetLoadout nicht auf den Anker umlenken - sonst
        // wuerde das Zuweisen der eingebackenen Kopie nur den Anker umhaengen, den wir gerade aufloesen.
        internal static bool Running;

        public struct Result
        {
            public int unwrapped;   // Kolonist ohne persoenliche Anpassung: direkt zurueck auf die geteilte Vorlage/Rolle
            public int baked;       // Kolonist mit Anpassung: bekommt eine eigene, flache Kopie
            public int flattened;   // Rolle (oder anderes Loadout) mit angehaengten Packs: Packs eingeschmolzen
            public int anchorsDeleted;
        }

        public static Result Run()
        {
            Result r = new Result();
            if (LoadoutManager.Loadouts == null) return r;
            Running = true;
            try
            {
                // 1. Kolonisten zuerst, solange Rollen und Vorlagen noch ihre volle Zusammensetzung liefern.
                //    Dieselben zwei Sammelabfragen wie FindOrphanedAnchors - Kryoschlaf, Karawane, fliegende
                //    Kapsel und Welt-Pawns eingeschlossen (reference-rimworld-pawn-enumeration).
                var pawns = new HashSet<Pawn>();
                foreach (Pawn p in PawnsFinder.AllMapsWorldAndTemporary_Alive) if (p != null) pawns.Add(p);
                foreach (Pawn p in PawnsFinder.AllMapsCaravansAndTravellingTransporters_Alive) if (p != null) pawns.Add(p);

                foreach (Pawn pawn in pawns)
                {
                    Loadout anchor = pawn.GetLoadout();
                    if (!AmmoAssignmentUtility.IsAmmoLink(anchor)) continue;

                    Loadout template = anchor.parentID > 0 ? LoadoutManager.GetLoadoutById(anchor.parentID) : null;
                    bool personal = (anchor.OwnSlots != null && anchor.OwnSlots.Count > 0)
                        || MultiParentUtility.GetExtraParents(anchor).Count > 0
                        || AmmoAssignmentUtility.IsAmmoCleared(anchor);

                    if (!personal)
                    {
                        // Ohne Vorlage (geloescht) und ohne Anpassung traegt der Anker nichts - dann "keins".
                        pawn.SetLoadout(template ?? LoadoutManager.DefaultLoadout);
                        r.unwrapped++;
                        continue;
                    }

                    string baseName = template != null ? (LoadoutUtility.LoadoutDisplayName(template) ?? template.label) : null;
                    string label = string.IsNullOrEmpty(baseName)
                        ? pawn.LabelShort
                        : "CER_SaveCleanse_BakedLabelFmt".Translate(pawn.LabelShort, baseName).ToString();
                    Loadout baked = new Loadout { defaultLoadout = false, label = label };
                    foreach (LoadoutSlot s in CloneSlots(anchor.Slots)) baked.AddSlot(s);
                    LoadoutManager.AddLoadout(baked);
                    pawn.SetLoadout(baked);
                    r.baked++;
                }

                // 2. Alles, was noch Zusatz-Eltern hat (Rollen mit Packs), in sich selbst einschmelzen.
                //    Erst den vollen Stand kopieren, DANN die eigenen Slots leeren - Slots liefert bei
                //    Zusatz-Eltern zwar eine neue Liste, deren Eintraege aber teils dieselben Objekte sind.
                //    Auch den CE-Elternteil abhaengen (2026-10-26, Review-Befund): der Snapshot enthaelt dessen
                //    Slots schon, und CEs Slots haengt sie sonst ein zweites Mal an - die KI addiert beide Zeilen.
                //    Moeglich, sobald jemand einer Rolle mit Packs in CEs eigenem Fenster einen Eltern-Loadout
                //    gegeben hat. Kinder dieser Rolle erben danach deren vollen Stand, ebenfalls ohne Dopplung.
                foreach (Loadout lo in LoadoutManager.Loadouts.ToList())
                {
                    if (lo == null || lo.defaultLoadout || AmmoAssignmentUtility.IsAmmoLink(lo)) continue;
                    if (MultiParentUtility.GetExtraParents(lo).Count == 0) continue;
                    List<LoadoutSlot> snapshot = CloneSlots(lo.Slots);
                    lo.parentID = 0;
                    MultiParentUtility.SetExtraParents(lo, new List<Loadout>());
                    foreach (LoadoutSlot s in lo.OwnSlots.ToList()) lo.RemoveSlot(s);
                    foreach (LoadoutSlot s in snapshot) lo.AddSlot(s);
                    r.flattened++;
                }

                // 3. Die Anker selbst. Niemand zeigt mehr auf sie; LoadoutDeletionUtility raeumt dabei auch die
                //    Seitenbuecher dieses Mods (IDs werden von CE recycelt).
                List<Loadout> anchors = LoadoutManager.Loadouts.Where(AmmoAssignmentUtility.IsAmmoLink).ToList();
                r.anchorsDeleted = LoadoutDeletionUtility.DeleteAll(anchors);

                // Better Pawn Control merkt sich Loadout-Nummern und spielt sie beim Oeffnen seines Reiters
                // zurueck - ohne frischen Schnappschuss zeigte er auf die eben geloeschten Anker.
                BetterPawnControlCompat.RefreshAssignSnapshot();
                CombatReadiness.Invalidate();
            }
            finally
            {
                Running = false;
            }
            return r;
        }

        // Neue Slot-Objekte, nie die vorhandenen weiterreichen. AddSlot fasst gleiche Eintraege spaeter zusammen
        // und ADDIERT dabei - das entspricht CEs Lesart (siehe Kopfkommentar), aendert also nichts am Bedarf.
        private static List<LoadoutSlot> CloneSlots(IEnumerable<LoadoutSlot> source)
        {
            var result = new List<LoadoutSlot>();
            if (source == null) return result;
            foreach (LoadoutSlot s in source.ToList())
            {
                if (s == null || s.count <= 0) continue;
                LoadoutSlot copy;
                if (s.thingDef != null) copy = new LoadoutSlot(s.thingDef, s.count);
                else if (s.genericDef != null) copy = new LoadoutSlot(s.genericDef, s.count);
                else continue;
                copy.countType = s.countType;
                result.Add(copy);
            }
            return result;
        }
    }
}
