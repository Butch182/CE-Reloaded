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
        // Cached component access instead of Game.GetComponent on EVERY call - this function hangs off
        // Loadout.Slots via Patch_Loadout_MultiParentSlots, see the reasoning at
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

        // One shared, EMPTY list instead of a fresh one on every miss. The normal case is "this loadout
        // has no extra parents", and this function hangs off Loadout.Slots via
        // Patch_Loadout_MultiParentSlots - i.e. off a getter that CE's think tree calls per colonist and
        // decision. An allocation per call is the most expensive way to say "nothing" there.
        // No caller modifies the list (all read only: Count, foreach, Select) - it is still declared
        // ReadOnly so a future caller that tries anyway fails immediately instead of silently changing
        // the state of everyone else.
        private static readonly List<int> NoExtraParents = new List<int>(0);

        public static List<int> GetExtraParentIds(Loadout owner)
        {
            if (owner == null) return NoExtraParents;
            Dictionary<int, string> map = Map();
            if (map == null || !map.TryGetValue(owner.UniqueID, out string raw)) return NoExtraParents;
            return ParseIds(raw);
        }

        // The extra parents of this loadout AND those of all its ancestors, combined.
        // This is necessary because CE's own chain runs PAST our patch: with a parent present,
        // Loadout.Slots builds _slots.Concat(ParentSlots), and ParentSlots iterates `ancestor.OwnSlots` -
        // the RAW field, not the (patched) Slots property. So the postfix on the child only appends its
        // OWN extra parents, never those of the parent.
        // Practical consequence, measured live: a colonist with a role that had an Equipment Pack
        // attached did not have the pack contents in their slots - our own preview showed them (it
        // reads role.Slots, i.e. the ROLE's patched getter), CE's pickup AI did not see them. The mod
        // therefore ordered supplies that nobody picked up.
        // Cost: the normal case pays one dictionary check on Count and returns the shared list - no
        // allocation. A new list is only built when an ANCESTOR actually has extra parents (role with
        // pack). That matters here because CE's think tree hangs off Loadout.Slots.
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
                // Only allocate here, and do NOT modify the cached list from ParseIds.
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

        // This mapping knows a deleted loadout in BOTH roles, and both must go
        // (LoadoutDeletionUtility calls this, nothing else does): as a KEY, when a deleted role or anchor
        // itself carried packs, and as a VALUE in every other owner's list, when a deleted pack or
        // sidearm WAS carried. The value is the dangerous case: CE assigns IDs as max+1 and so recycles
        // them, which means a leftover number sooner or later attaches a completely unrelated new
        // object to that colonist.
        // parseCache needs nothing: it is keyed by the RAW string, and a changed entry is a different
        // string and therefore a different cache entry.
        public static void ForgetLoadout(int id)
        {
            Dictionary<int, string> map = Map();
            if (map == null) return;
            map.Remove(id);
            foreach (int owner in map.Keys.ToList())
            {
                List<int> ids = ParseIds(map[owner]);
                if (!ids.Contains(id)) continue;
                // New list, do NOT modify ids - that is the shared, cached instance from ParseIds.
                List<int> kept = ids.Where(v => v != id).ToList();
                if (kept.Count == 0) map.Remove(owner);
                else map[owner] = string.Join(",", kept);
            }
        }
    }
}
