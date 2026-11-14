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
        // Second payload (ammo had no Copy button while the other columns did):
        // ammo is not a loadout object but a list of slots on a colonist's personal anchor. Hence its
        // own field instead of a detour - Items stays exactly as before for packs and sidearms, and a
        // clipboard only ever holds ONE kind (Kind decides which).
        // Copies of the slots are copied, not the slots themselves: otherwise two colonists would hang
        // on the same object and a quantity change on the second would silently change the first too.
        public static List<LoadoutSlot> AmmoSlots { get; private set; }
        // Third payload ("Add other item" had no Copy button and therefore no paste either): a
        // colonist's other items, stored like ammo as slot copies from the personal anchor.
        // Kind = "other".
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

        // Copy ammo. An empty selection is allowed and means the same as for packs: pasting
        // clears the target.
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
        // Bound to the game in which the copy was made. The comment above always claimed the clipboard
        // was cleared on load - but nothing ever called that. A static survives switching saves, so the
        // clipboard in save B still offered loadout objects from save A.
        // Instead of patching a load hook, the reference is checked here: a different Game instance
        // means an empty clipboard. Costs nothing and cannot be forgotten.
        private static Game sourceGame;

        // `kind` is a FIXED identifier ("pack" / "sidearm"), no longer translated text.
        // It used to hold the picker window's column label, and the paste side compared it against its
        // own translated text - two different Keyed keys whose values happened to have to match for a
        // button to appear at all. That is exactly what killed the paste button for packs in every
        // non-English language.
        // The earlier fix aligned both sides on identical strings; that held, but only as long as
        // nobody touched either translation. An identifier that is never translated cannot drift apart.
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
