using System.Collections.Generic;
using System.Linq;
using Verse;
using CombatExtended;

namespace CEReloaded
{
    // Real 1..N attachment for Loadouts, on top of CE's native single-parent parentID chain - backs
    // Patch_Loadout_MultiParentSlots. Only two kinds of Loadout ever get an entry here: a Role ("[Role]
    // X", may reference several Weapon Loadouts + Equipment Packs so a whole kit is one bundle instead of
    // duplicating sidearm/grenade handling inside Equipment Packs) and a pawn's own hidden personal
    // anchor (may reference extras on top of whatever Role/template they're using - see
    // AmmoAssignmentUtility). Plain Weapon Loadouts and Equipment Packs stay pure leaves and never appear
    // as a KEY here, so a cycle can never form through this side channel - Patch_Loadout_MultiParentSlots
    // still guards against one anyway, defensively.
    public static class MultiParentUtility
    {
        // Gemerkter Komponenten-Zugriff statt Game.GetComponent bei JEDEM Aufruf (2026-08-14) - diese
        // Funktion haengt ueber Patch_Loadout_MultiParentSlots an Loadout.Slots, siehe die Begruendung an
        // CEReloadedGameComponent.Cached.
        private static Dictionary<int, string> Map() => CEReloadedGameComponent.Cached?.loadoutExtraParents;

        // Loadout.Slots (Patch_Loadout_MultiParentSlots) is CE's own hottest getter - read by every pawn's
        // inventory/loadout-compliance checks, not just our own UI. Re-splitting and re-parsing the same
        // comma string on every single call was pure waste. Cached by the RAW STRING itself (not by owner
        // id) - correctness never depends on remembering to invalidate anything: a given string can only
        // ever parse to one result, full stop, whether it's read this game session or the next.
        private static readonly Dictionary<string, List<int>> parseCache = new Dictionary<string, List<int>>();

        private static List<int> ParseIds(string raw)
        {
            if (string.IsNullOrEmpty(raw)) return new List<int>();
            if (parseCache.TryGetValue(raw, out List<int> cached)) return cached;
            List<int> parsed = raw.Split(',').Select(s => int.TryParse(s, out int v) ? v : -1).Where(v => v >= 0).ToList();
            parseCache[raw] = parsed;
            return parsed;
        }

        // Eine geteilte, LEERE Liste statt einer frischen bei jedem Fehlschlag (2026-08-09). Der Normalfall
        // ist "dieses Loadout hat keine Extra-Eltern", und diese
        // Funktion haengt ueber Patch_Loadout_MultiParentSlots an Loadout.Slots - also an einem Getter, den
        // CEs Think-Tree pro Kolonist und Entscheidung aufruft. Eine Allokation pro Aufruf ist dort die
        // teuerste Art, "nichts" zu sagen.
        // Die Liste wird von keinem Aufrufer veraendert (alle nur lesend: Count, foreach, Select) - sie ist
        // trotzdem als ReadOnly deklariert, damit ein kuenftiger Aufrufer, der es doch versucht, sofort
        // auffliegt statt still den Zustand aller anderen mitzuaendern.
        private static readonly List<int> NoExtraParents = new List<int>(0);

        public static List<int> GetExtraParentIds(Loadout owner)
        {
            if (owner == null) return NoExtraParents;
            Dictionary<int, string> map = Map();
            if (map == null || !map.TryGetValue(owner.UniqueID, out string raw)) return NoExtraParents;
            return ParseIds(raw);
        }

        // Die Extra-Eltern dieses Loadouts UND die aller Vorfahren, zusammengelegt.
        // Das muss sein, weil CEs eigene Kette an unserem Patch VORBEI laeuft: Loadout.Slots baut bei
        // vorhandenem Elternteil _slots.Concat(ParentSlots), und ParentSlots iteriert `ancestor.OwnSlots` -
        // das ROHE Feld, nicht die (von uns gepatchte) Slots-Eigenschaft. Der Postfix am Kind haengt also
        // nur dessen EIGENE Extra-Eltern an, die des Elternteils nie.
        // Praktische Folge, live gemessen (2026-08-13): ein Kolonist mit einer Rolle, an der ein Equipment
        // Pack haengt, hatte den Pack-Inhalt nicht in seinen Slots - unsere eigene Vorschau zeigte ihn (die
        // liest role.Slots, also den gepatchten Getter der ROLLE), CEs Abhol-KI sah ihn nicht. Der Mod
        // bestellte damit Nachschub, den niemand einsammelt.
        // Kosten: der Normalfall zahlt eine Dictionary-Pruefung auf Count und gibt die geteilte Liste
        // zurueck - keine Allokation. Nur wenn ein VORFAHR wirklich Extra-Eltern hat (Rolle mit Pack), wird
        // ueberhaupt eine neue Liste gebaut. Das zaehlt hier, weil an Loadout.Slots CEs Think-Tree haengt.
        public static List<int> GetExtraParentIdsWithAncestors(Loadout owner)
        {
            if (owner == null) return NoExtraParents;
            Dictionary<int, string> map = Map();
            if (map == null || map.Count == 0) return NoExtraParents;

            List<int> own = map.TryGetValue(owner.UniqueID, out string raw) ? ParseIds(raw) : null;
            List<int> merged = null;
            int guard = 0;
            for (Loadout a = owner.ParentLoadout; a != null && guard++ < 8; a = a.ParentLoadout)
            {
                if (!map.TryGetValue(a.UniqueID, out string araw)) continue;
                List<int> ids = ParseIds(araw);
                if (ids.Count == 0) continue;
                // Erst hier allokieren, und die gecachte Liste aus ParseIds dabei NICHT veraendern.
                if (merged == null) merged = own != null ? new List<int>(own) : new List<int>();
                for (int i = 0; i < ids.Count; i++)
                    if (!merged.Contains(ids[i])) merged.Add(ids[i]);
            }
            return merged ?? own ?? NoExtraParents;
        }

        public static List<Loadout> GetExtraParents(Loadout owner) =>
            GetExtraParentIds(owner).Select(LoadoutManager.GetLoadoutById).Where(l => l != null).ToList();

        public static void SetExtraParents(Loadout owner, List<Loadout> parents)
        {
            if (owner == null) return;
            Dictionary<int, string> map = Map();
            if (map == null) return;
            List<Loadout> real = (parents ?? new List<Loadout>()).Where(p => p != null && p.UniqueID != owner.UniqueID).Distinct().ToList();
            if (real.Count == 0) map.Remove(owner.UniqueID);
            else map[owner.UniqueID] = string.Join(",", real.Select(p => p.UniqueID));
        }

        // Diese Zuordnung kennt ein geloeschtes Loadout in BEIDEN Rollen, und beide muessen weg
        // (LoadoutDeletionUtility ruft das, sonst niemand): als SCHLUESSEL, wenn eine geloeschte Rolle oder
        // ein geloeschter Anker selbst Packs trug, und als WERT in der Liste jedes anderen Besitzers, wenn
        // ein geloeschtes Pack bzw. eine geloeschte Zweitwaffe getragen WURDE. Der Wert ist der gefaehrliche
        // Fall: CE vergibt IDs als Max+1 und recycelt sie damit, eine stehengebliebene Zahl haengt also
        // frueher oder spaeter ein voellig fremdes, neues Objekt an diesen Kolonisten.
        // parseCache braucht nichts: er ist am ROHSTRING geschluesselt, ein geaenderter Eintrag ist ein
        // anderer String und damit ein anderer Cache-Eintrag.
        public static void ForgetLoadout(int id)
        {
            Dictionary<int, string> map = Map();
            if (map == null) return;
            map.Remove(id);
            foreach (int owner in map.Keys.ToList())
            {
                List<int> ids = ParseIds(map[owner]);
                if (!ids.Contains(id)) continue;
                // Neue Liste, NICHT ids veraendern - das ist die geteilte, gecachte Instanz aus ParseIds.
                List<int> kept = ids.Where(v => v != id).ToList();
                if (kept.Count == 0) map.Remove(owner);
                else map[owner] = string.Join(",", kept);
            }
        }
    }
}
