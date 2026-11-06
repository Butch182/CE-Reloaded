using Verse;
using System.Collections.Generic;
using System.Linq;
using CombatExtended;

namespace CEReloaded
{
    // A tiny session-only "clipboard" so a pawn's Pack or Sidearm selection can be copied once (via the
    // Dialog_PickPacks checklist's own Copy button) and then pasted onto several other pawns from the main
    // Assign Loadouts screen, instead of reopening the checklist and re-ticking the same boxes per colonist.
    // Not persisted (Scribed) on purpose - it is a per-session UI convenience, not game state, and clearing on
    // load/new-game is exactly the behavior wanted (nothing stale to paste into a different save).
    public static class LoadoutClipboard
    {
        // Must match the copying popup's own itemColumnLabel ("Pack" or "Sidearms") - a Pack-flavored
        // clipboard should only ever offer to paste into a Pack cell, never a Sidearm one.
        public static string Kind { get; private set; }
        public static List<Loadout> Items { get; private set; }
        // Zweite Nutzlast (2026-08-13: Munition hatte keinen Copy-Knopf, die anderen Spalten schon):
        // Munition ist kein Loadout-Objekt, sondern eine Liste von Slots auf dem persoenlichen Anker eines
        // Kolonisten. Deshalb ein eigenes Feld statt eines Umwegs - Items bleibt fuer Packs und Zweitwaffen
        // genau wie bisher, und eine Ablage traegt immer nur EINE Sorte (Kind entscheidet, welche).
        // Kopiert werden Kopien der Slots, nicht die Slots selbst: sonst haenge zwei Kolonisten am selben
        // Objekt und eine Mengenaenderung beim zweiten aendert still auch den ersten.
        public static List<LoadoutSlot> AmmoSlots { get; private set; }
        // Dritte Nutzlast (2026-08-25: "Add other item" hatte keinen Copy-Knopf und damit auch kein
        // Einfuegen): die sonstigen Gegenstaende eines Kolonisten, wie Munition
        // Slot-Kopien vom persoenlichen Anker. Kind = "other".
        public static List<LoadoutSlot> OtherSlots { get; private set; }
        public static string SourceLabel { get; private set; }

        public static void Copy(string kind, List<Loadout> items, string sourceLabel)
        {
            sourceGame = Current.Game;
            Kind = kind;
            Items = (items ?? new List<Loadout>()).ToList();
            AmmoSlots = null;
            OtherSlots = null;
            SourceLabel = sourceLabel;
        }

        public static void CopyOther(IEnumerable<LoadoutSlot> slots, string sourceLabel)
        {
            sourceGame = Current.Game;
            Kind = "other";
            Items = null;
            AmmoSlots = null;
            OtherSlots = (slots ?? Enumerable.Empty<LoadoutSlot>())
                .Where(s => s != null && s.thingDef != null)
                .Select(s => new LoadoutSlot(s.thingDef, s.count))
                .ToList();
            SourceLabel = sourceLabel;
        }

        // Munition kopieren. Leere Auswahl ist erlaubt und bedeutet dasselbe wie bei Packs: einfuegen
        // raeumt beim Ziel auf.
        public static void CopyAmmo(IEnumerable<LoadoutSlot> slots, string sourceLabel)
        {
            sourceGame = Current.Game;
            Kind = "ammo";
            Items = null;
            OtherSlots = null;
            AmmoSlots = (slots ?? Enumerable.Empty<LoadoutSlot>())
                .Where(s => s != null)
                .Select(s => s.thingDef != null ? new LoadoutSlot(s.thingDef, s.count) : new LoadoutSlot(s.genericDef, s.count))
                .ToList();
            SourceLabel = sourceLabel;
        }

        // Items.Count > 0 is NOT required - copying an explicitly EMPTY selection is deliberate (2026-08-01):
        // pasting it onto another pawn clears their packs/sidearms, standing in for a
        // dedicated delete/clear button that doesn't exist. Kind is only ever set together with Items inside
        // Copy() (both null until then), so this still correctly reads "nothing copied this session" as false.
        // An das Spiel gebunden, in dem kopiert wurde (2026-08-09). Der Kommentar oben behauptet seit
        // jeher, beim Laden werde geleert - das rief nur nie jemand auf. Ein static ueberlebt den
        // Spielstandwechsel, also bot die Zwischenablage in Save B noch Loadout-Objekte aus Save A an.
        // Statt einen Lade-Hook zu patchen wird der Bezug hier geprueft: eine andere Game-Instanz
        // heisst leere Zwischenablage. Kostet nichts und kann nicht vergessen werden.
        private static Game sourceGame;

        // `kind` ist seit 2026-08-13 eine FESTE Kennung ("pack" / "sidearm"), kein uebersetzter Text mehr.
        // Vorher lag hier die Spaltenbeschriftung des Auswahlfensters drin und die Einfuege-Seite verglich
        // sie gegen ihren eigenen uebersetzten Text - zwei verschiedene Keyed-Schluessel, deren Werte
        // zufaellig gleich sein mussten, damit ein Knopf ueberhaupt erscheint. Genau daran ist der
        // Einfuege-Knopf fuer Packs in jeder Nicht-Englisch-Sprache gestorben.
        // Der Fix von damals hat beide Seiten auf gleich lautende Strings gezogen; das hat gehalten, aber
        // nur solange niemand eine der beiden Uebersetzungen anfasst. Eine Kennung, die nie uebersetzt wird,
        // kann gar nicht mehr auseinanderlaufen.
        public static bool HasContent(string kind)
        {
            if (sourceGame != null && !ReferenceEquals(sourceGame, Current.Game))
            {
                Kind = null; Items = null; AmmoSlots = null; OtherSlots = null; SourceLabel = null; sourceGame = null;
            }
            return Kind == kind && (Items != null || AmmoSlots != null || OtherSlots != null);
        }

        public static void Clear()
        {
            Kind = null;
            Items = null;
            AmmoSlots = null;
            OtherSlots = null;
            SourceLabel = null;
        }
    }
}
