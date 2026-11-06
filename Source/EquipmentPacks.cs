using System;
using System.Collections.Generic;
using System.Linq;
using RimWorld;
using Verse;
using UnityEngine;
using CombatExtended;

namespace CEReloaded
{
    // An Equipment Pack is a normal CE Loadout that holds pure SUPPLY content - medicine, food, drugs,
    // shields, and utility weapons (binoculars, smoke/EMP-only/flashbang grenades). NOT backpacks/vests/
    // bandoliers ("Webbing"/"Backpack" apparel layers) - vanilla's own Apparel Policy already manages those
    // (they show up right there under "Loadbearing gear") with its own working auto-equip; there's nothing
    // left for a pack to add. Never a SCORED weapon of any kind (main, sidearm, or combat grenade - those
    // are all just Weapon Loadouts now, attachable the same generic way via MultiParentUtility, so there is
    // exactly one place that owns weapon+ammo scoring/filtering instead of a second, weaker copy living
    // here). Utility weapons are the one exception: IsWeapon technically, but never scored (GetWeaponScore
    // floors them at -1) and never competing for a Weapon Loadout's "best weapon" slot, so they read as
    // plain unscored inventory here instead - same section as shields. A pack attaches PER PAWN (or per Role), via a LIVE reference (see
    // MultiParentUtility/AmmoAssignmentUtility.SetPawnPacks) - never onto the shared weapon-loadout
    // template directly, which would force everyone on that template onto the same pack.
    //
    // PackCategory is purely an editor/organization concept - it groups a pack's own slots into sections
    // in the Equipment Packs tab (see Dialog_Ingredients.cs) and picks which pool of ThingDefs a section's
    // "+ Add" offers. It is NOT encoded in the pack's label and does not partition packs into separate
    // objects - one pack's OwnSlots can contain items from every category at once.
    public enum PackCategory { Medical, Food, Drugs, Utility, Other }

    public static class EquipmentPackUtility
    {
        // "[Pack] " - same detectable-marker convention as Roles.cs's "[Role] ", so a pack's kind
        // survives a save/load without needing any of our own Scribe code (CE already persists the
        // Loadout's label for us).
        public const string PackMarkerPrefix = "[Pack]";

        // ============ Erkennung ueber die Identitaet (2026-08-17) ============
        // Sein Auftrag: "wir wollen nichts mehr erkennen an irgendwelchen praefixes". Dieselbe Loesung, die
        // Waffen-Vorlagen ueber managedLoadoutIds laengst haben und die Rollen heute bekommen haben: ein Pack
        // wird beim Anlegen eingetragen, und das Etikett entscheidet nichts mehr.
        // Der Marker-Rueckfall bleibt fuer Packs, die vor dieser Aenderung entstanden sind - er heilt sich
        // selbst, sobald so ein Pack einmal angefasst wird (MarkAsPack unten).
        public static bool IsEquipmentPackLoadout(Loadout lo)
        {
            if (lo == null) return false;
            CEReloadedGameComponent comp = CEReloadedGameComponent.Cached;
            return comp != null && comp.packLoadoutIds.Contains(lo.UniqueID);
        }

        // NACH LoadoutManager.AddLoadout aufrufen - die UniqueID vergibt erst der Manager.
        public static void MarkAsPack(Loadout lo)
        {
            if (lo == null) return;
            CEReloadedGameComponent comp = CEReloadedGameComponent.Cached;
            comp?.packLoadoutIds.Add(lo.UniqueID);
        }

        public static bool IsEquipmentPackLabel(string label) => !string.IsNullOrEmpty(label) && label.StartsWith(PackMarkerPrefix);

        // Strips ONLY the internal "[Pack]" marker - the actual editable name (what a rename text field
        // should show/round-trip), with none of the cosmetic display prefix added back in. Feeding
        // StripMarker's OWN output back into that field would bake the cosmetic prefix into the real,
        // persisted name (and double up again next time), since StripMarker is meant for read-only display.
        public static string BareName(string label)
        {
            if (!IsEquipmentPackLabel(label)) return label;
            int end = label.IndexOf(']');
            return end < 0 ? label : label.Substring(end + 1).TrimStart();
        }

        // The internal "[Pack]" marker stays hardcoded and invariant (detection/persistence - never shown),
        // regardless of the cosmetic display prefix below. That prefix is purely a settings-driven "how
        // should a pack's kind read at a glance" decoration (default "E|", same idea as Weapon Loadouts'
        // own configurable prefix) - changing it can never un-recognize an already-authored pack, since
        // nothing ever matches packs BY the display prefix. Read-only display use ONLY - see BareName above
        // for anything that feeds back into an editable name field.
        // Reicht seit 2026-08-16 an LoadoutUtility.LoadoutDisplayName durch - EINE Stelle entscheidet, wie ein
        // Name aussieht, statt dass jede Stelle selbst bastelt. Der Name bleibt, weil die Aufrufstellen
        // damit lesbar sind. BareName daneben ist etwas anderes und bleibt: das ist der EDITIERBARE Name.
        public static string StripMarker(string label) => LoadoutUtility.LoadoutDisplayName(label);

        public static List<Loadout> AllEquipmentPacks() =>
            LoadoutManager.Loadouts.Where(l => l != null && !l.defaultLoadout && IsEquipmentPackLoadout(l)).ToList();

        // Wer traegt dieses Pack WIRKLICH - ueber den eigenen Anker (Extra-Elternteil, der normale Weg)
        // ODER ueber eine zugewiesene Rolle, die das Pack selbst als Extra-Elternteil mitbringt
        // (CommitRole haengt es an, statt es einzuschmelzen). Fuer die Traeger-Raute im Packs-Reiter
        // (2026-09-01) - dieselbe Frage, die Supply Loadouts pro GEGENSTAND laengst
        // beantwortet, hier pro PACK-OBJEKT. Namen wie ueberall LabelShortCap, dedupliziert.
        public static List<string> PackWearerNames(Loadout pack)
        {
            var names = new List<string>();
            if (pack == null) return names;
            foreach (Pawn p in LoadoutUtility.AllColonyPawns())
            {
                bool wears = (AmmoAssignmentUtility.GetPawnPacks(p) ?? new List<Loadout>()).Contains(pack);
                if (!wears)
                {
                    Loadout role = AmmoAssignmentUtility.DisplayLoadout(p.GetLoadout());
                    wears = role != null && RoleUtility.IsRoleLoadout(role)
                        && MultiParentUtility.GetExtraParents(role).Contains(pack);
                }
                if (wears && !names.Contains(p.LabelShortCap)) names.Add(p.LabelShortCap);
            }
            return names;
        }

        // Gates every Equipment Pack UI element outside the Equipment Packs tab itself (picker buttons on
        // weapon rows, the Generate popup's column/checkbox, the Assign popup's pack button) - hidden
        // entirely until at least one pack exists, so a player who never touches the feature sees zero
        // change anywhere else.
        public static bool AnyEquipmentPacksExist() =>
            LoadoutManager.Loadouts.Any(l => l != null && !l.defaultLoadout && IsEquipmentPackLoadout(l));

        // Classifies any def a pack slot can hold into the section of the Equipment Packs editor it belongs
        // to - the single source of truth every section (its "+ Add" pool AND its own-slot display) reads,
        // so a def can never be classified into two sections at once. Order matters for ThingDefs: ammo/
        // medicine/drugs/food are mutually exclusive by definition, but the weapon/apparel checks have to
        // come after them since nothing else overlaps those. Generics ("Any Meal" etc.) go through their
        // own canonical lookup instead, built the same way (LoadoutUtility.GenericCategoryOf).
        public static PackCategory CategoryOf(Def d)
        {
            if (d is LoadoutGenericDef gd) return LoadoutUtility.GenericCategoryOf(gd) ?? PackCategory.Other;
            if (!(d is ThingDef td) || td == null) return PackCategory.Other;
            if (td.IsMedicine) return PackCategory.Medical;
            if (td.IsDrug) return PackCategory.Drugs;
            if (td.IsIngestible && (int)td.ingestible.preferability >= (int)FoodPreferability.RawTasty) return PackCategory.Food;
            if (LoadoutUtility.IsShieldApparel(td)) return PackCategory.Utility;
            // Utility weapons (binoculars, smoke/EMP-only/flashbang grenades, ...) are IsWeapon technically,
            // but carry no combat score by design (GetWeaponScore floors them at -1) - they were never
            // competing for a Weapon Loadout's "best weapon" slot, so they belong here as plain unscored
            // inventory instead, same section as shields/webbing.
            if (LoadoutUtility.IsUtilityWeapon(td)) return PackCategory.Utility;
            return PackCategory.Other;
        }

        // A small representative icon for a whole pack - its first content item's icon (a category badge
        // for a generic slot) - so a pack reads as more than a bare name in compact list cells (Assign
        // Loadouts' Pack column, Dialog_PickPacks' rows). Packs can mix categories; this is just "what's
        // the first thing in it", not an attempt to summarize the whole contents in one icon.
        public static void DrawIcon(Rect r, Loadout pack)
        {
            LoadoutSlot first = pack?.OwnSlots?.FirstOrDefault();
            if (first == null) return;
            if (first.thingDef != null) { Widgets.ThingIcon(r, first.thingDef); return; }
            if (first.genericDef == null) return;
            Texture2D badge = GenericIcons.For(first.genericDef);
            if (badge != null) GUI.DrawTexture(r, badge);
        }

        // Which category strictly dominates a pack's contents (by slot count) - null for a genuine mix (no
        // category holding a majority), which includes an even Food+Medical split like Base Supply. Used
        // by AssignPlanner to tell "this is a specialist role's kit" (assign to a fitting subset) apart
        // from "this is baseline supply" (assign to everyone) WITHOUT hardcoding a pack's name, so a
        // player's own hand-authored packs get exactly the same treatment as the auto-generated defaults.
        public static PackCategory? DominantCategory(Loadout pack)
        {
            List<LoadoutSlot> slots = pack?.OwnSlots?.ToList() ?? new List<LoadoutSlot>();
            if (slots.Count == 0) return null;
            var counts = new Dictionary<PackCategory, int>();
            foreach (LoadoutSlot s in slots)
            {
                PackCategory cat = CategoryOf(s.thingDef != null ? (Def)s.thingDef : s.genericDef);
                counts[cat] = (counts.TryGetValue(cat, out int c) ? c : 0) + 1;
            }
            var top = counts.OrderByDescending(kv => kv.Value).First();
            return top.Value * 2 > slots.Count ? top.Key : (PackCategory?)null;
        }

        // Specifically a shield (not any Utility-dominant pack - other Utility content, like webbing, has no
        // weapon-compatibility restriction, unlike a shield). AssignPlanner uses this, not
        // DominantCategory == Utility, to decide which packs need the "melee-oriented pawns only" gate.
        public static bool ContainsShield(Loadout pack) =>
            pack?.OwnSlots != null && pack.OwnSlots.Any(s => s.thingDef != null && LoadoutUtility.IsShieldApparel(s.thingDef));

        public static string CategoryLabel(PackCategory cat)
        {
            switch (cat)
            {
                case PackCategory.Medical: return "CER_EquipmentPacks_CategoryMedical".Translate();
                case PackCategory.Food: return "CER_EquipmentPacks_CategoryFood".Translate();
                case PackCategory.Drugs: return "CER_EquipmentPacks_CategoryDrugs".Translate();
                case PackCategory.Utility: return "CER_EquipmentPacks_CategoryUtility".Translate();
                default: return "CER_EquipmentPacks_CategoryOther".Translate();
            }
        }
    }
}
