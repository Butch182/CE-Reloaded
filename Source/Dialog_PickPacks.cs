using System;
using System.Collections.Generic;
using System.Linq;
using RimWorld;
using Verse;
using UnityEngine;
using CombatExtended;

namespace CEReloaded
{
    // Which Loadout(s) apply here? A pawn (or a plan preview row) can carry several at once (Equipment
    // Packs are pure supply and sidearms are just extra Weapon Loadouts - neither has a reason to limit a
    // pawn to just one) - a checklist rather than a FloatMenu, since FloatMenu closes on every pick and
    // can't stay open for ticking several boxes. Styled like the Auto-Generate popup's own row list
    // (checkbox, name, inline contents, right-aligned Mass/Bulk + production cost) instead of a bare
    // CheckboxLabeled column, so a player can actually tell what an item contains before deciding to carry it.
    // Generic over what "current selection" means and what a toggle does - Dialog_AssignLoadouts uses it
    // to mutate a pawn's live personal anchor immediately; Dialog_SquadSetup's preview uses it to update a
    // pending pin instead, since nothing there applies until the player presses Apply. Also generic over
    // WHICH loadouts are offered (defaults to Equipment Packs, the original use) and how they're labeled -
    // Equipment Packs store a bare name and need StripMarker's cosmetic prefix; Weapon Loadouts already
    // bake their prefix into the stored label, so they're shown as-is.
    public class Dialog_PickPacks : Window
    {
        private readonly string windowTitle;
        private readonly string subtitle;
        // Short, stable identifier for the Copy button's clipboard source (2026-08-01 fix:
        // the paste tooltip elsewhere used to show the ENTIRE instructional subtitle verbatim, e.g. "<pawn>
        // - tick which Weapon Loadout(s) to carry as a sidearm. Changes apply immediately." as if that whole
        // sentence were a "source" - confusing, and sometimes outright misleading (a subtitle warning
        // "nothing applies until Apply" pasted into a context where it DOES apply immediately). Defaults to
        // subtitle for any caller that doesn't bother passing one, so this is purely additive.
        private readonly string sourceLabel;
        private readonly string itemColumnLabel;
        // Fixed identifier for the clipboard - NOT the translated column name (see LoadoutClipboard.HasContent).
        // Defaults to "pack", because that is this dialog's original purpose.
        private readonly string clipboardKind;
        private readonly string emptyMessage;
        private readonly Func<List<Loadout>> allOptions;
        private readonly Func<List<Loadout>> getCurrent;
        private readonly Action<List<Loadout>> onChanged;
        private readonly Func<Loadout, string> displayLabel;
        // Optional: when set, a small section header is drawn whenever this changes between two consecutive
        // rows in `allOptions()`'s own order - the caller controls grouping entirely by how it sorts the list
        // (e.g. "Backup weapons" rows first, then "Explosives", then "Shields"); this dialog just detects the
        // boundary. Null (the default) draws a plain flat list, unchanged from before - only a caller that
        // actually mixes categories (the Sidearm picker) needs to pass one.
        // A groupLabel that returns NULL for a group gets no separator row at all - its rows sit directly below
        // the column headers (an extra "Backup weapons" separator below the column headers was redundant). The
        // decision WHICH group is the silent one belongs to the caller, not to this dialog: "suppress the first"
        // would be wrong as soon as a colony has no ordinary sidearms at all and the list starts with
        // "Explosives" - then precisely the group whose name says something would be unlabeled. The height
        // calculation and the draw loop both check for the same change, so a null group drops out of both alike.
        private readonly Func<Loadout, string> groupLabel;
        // Optional: a comparative 0..100 score (LoadoutUtility.GetWeaponScore/GetShieldScore) shown as its
        // own column - only the Sidearm picker's mixed weapon/explosive/shield pool has one; a plain
        // Equipment Pack has no combat score concept at all, so this stays null there.
        private readonly Func<Loadout, float> scoreFn;
        // Optional: shows and lets the player edit CEReloadedMod.settings.sidearmMaxBulk right in this popup -
        // the SAME setting AssignPlanner's own PickSidearm reads to cap what auto-assign can hand out, not a
        // separate copy of the value. Only meaningful for the Sidearm picker.
        private readonly bool showSidearmBulkCap;
        // Optional: the representative ThingDef to check map availability for (Dialog_AssignLoadouts.
        // RepresentativeIcon) - same traffic-light convention (green/yellow/gray/red) the native CE loadout
        // FloatMenu already shows for a pawn's MAIN weapon; the Sidearm picker's own candidates never had
        // this at all. Null for Equipment Packs - a pack is a bundle, not one weapon to check stock for.
        private readonly Func<Loadout, ThingDef> availDefFn;
        // Live-mutation callers only (Dialog_AssignLoadouts/Dialog_LoadoutEditor - onChanged writes straight
        // to the pawn's real anchor) - Dialog_SquadSetup's own two call sites leave this false since nothing
        // there is real yet (a pending pin, not applied until Apply), so checking for missing production
        // before anything was actually committed would be checking against state that doesn't exist.
        private readonly bool checkProductionOnClose;
        // Lazy rework: weapons WITHOUT an existing template as their own section below the loadout rows. Ticking
        // one runs materializeWeapon (EnsureDefaultLoadout) - the shared template is created at exactly that
        // moment, and in the next frame the weapon is a normal loadout row (allOptions contains it, weaponOptions
        // no longer does). Both null -> behavior unchanged.
        private readonly Func<List<ThingDef>> weaponOptions;
        private readonly Func<ThingDef, Loadout> materializeWeapon;
        // SINGLE-SELECT mode (modeled on the sidearm window instead of a separate window): the same window now
        // also hosts the main weapon selection. Radio instead of checkbox, one click selects and closes;
        // optionally a "None"/"Auto" row at the very top and an extra button at the bottom (e.g. "Save combination
        // as role"). The Copy button is dropped in this mode - a single selection is not a set.
        private readonly bool singleSelect;
        private readonly Action<Loadout> onPickedSingle;
        private readonly Action onNonePicked;
        private readonly string noneLabel;
        private readonly Loadout currentSingle;
        private readonly ThingDef currentWeaponSingle;
        // Groups the WEAPON section (e.g. ranged/melee) analogous to groupLabel for the loadouts;
        // null -> a single "Weapons" separator row as before.
        private readonly Func<ThingDef, string> weaponGroupLabel;
        // How many copies the LIVE preview plan has already earmarked for others (scarcity fix, carried over from
        // the old pin menu) - shows up as a line in the availability tooltip.
        private readonly Func<ThingDef, int> reservedFn;
        private readonly string extraActionLabel;
        private readonly Action extraAction;
        // Lock switch: if any lock is toggled while this window is open, this callback fires on close - the
        // Assign tab hooks the reassignment prompt onto it, the pin preview a silent recompute.
        private readonly Action onDisabledChanged;
        private bool locksTouched;
        // Copy source for single-select mode (the weapon picker gets a Copy button too): returns the colonist's
        // current assignment, null = nothing to copy.
        private readonly Func<Loadout> copyCurrent;
        // Collapsed group bars, per window opening.
        private readonly HashSet<string> collapsedGroups = new HashSet<string>();
        // "Do not use" icons as separate, replaceable PNGs (the previous lock items were unclear):
        // Textures/UI/Icon_DoNotUse.png (locked) and Icon_DoNotUseOff.png (unlocked).
        private static Texture2D texDoNotUseOn, texDoNotUseOff;
        internal static Texture2D TexDoNotUseOn => texDoNotUseOn ?? (texDoNotUseOn = ContentFinder<Texture2D>.Get("UI/Icon_DoNotUse", false) ?? BaseContent.BadTex);
        internal static Texture2D TexDoNotUseOff => texDoNotUseOff ?? (texDoNotUseOff = ContentFinder<Texture2D>.Get("UI/Icon_DoNotUseOff", false) ?? BaseContent.BadTex);
        // Defaults per weapon once per window lifetime - BuildDefaultSettings scans ammo lists, which must not run
        // per row and frame (the same baseline-load class as the frame-rate throttling).
        private readonly Dictionary<ThingDef, List<LoadoutSlot>> weaponSlotsCache = new Dictionary<ThingDef, List<LoadoutSlot>>();
        private readonly Dictionary<ThingDef, (float mass, float bulk)> weaponMassBulkCache = new Dictionary<ThingDef, (float, float)>();
        // Extensions: damage column, production cost for weapon rows too, sortable column headers (always
        // active, default score descending, sorted WITHIN the groups - the separator rows stay in place),
        // availability filter as in the old weapons tab. An Assigned column was in briefly and was removed again;
        // likewise the radio dots in single-select mode - a click on the row selects, the current choice carries
        // the selection highlight.
        private enum PickSort { Name, Score, Special, Avail, Cost, Mass }
        private PickSort pickSort = PickSort.Score;
        private bool pickSortDesc = true;
        // Default is ALWAYS "Available" - deliberately not persisted, every opening starts the same.
        private WeaponFilterMode pickerFilter = WeaponFilterMode.AvailableWeapons;
        // Second filter group: weapon type, AND-combined with availability. -1 = all types.
        private int typeFilter = -1;
        private static int WeaponTypeOf(ThingDef d)
        {
            if (d == null) return 2;
            if (Dialog_GenerateLoadouts.IsGrenade(d) || LoadoutUtility.IsShieldApparel(d)) return 2;
            if (d.IsRangedWeapon && !d.IsMeleeWeapon) return 0;
            if (d.IsMeleeWeapon) return 1;
            return 2;
        }
        private Dictionary<ThingDef, int> ownedCounts;
        private readonly Dictionary<ThingDef, Dictionary<ThingDef, int>> weaponCostCache = new Dictionary<ThingDef, Dictionary<ThingDef, int>>();
        private bool WeaponCols => weaponOptions != null;
        private const float DamageColW = 96f;
        private string search = "";
        private Vector2 scroll;

        // Once per window opening, not per frame - this is a colony scan.
        private void EnsurePickerCaches()
        {
            if (ownedCounts == null) ownedCounts = Dialog_AssignLoadouts.BuildOwnedWeaponCounts();
        }

        private List<LoadoutSlot> WeaponSlots(ThingDef w)
        {
            if (!weaponSlotsCache.TryGetValue(w, out List<LoadoutSlot> slots))
            {
                slots = Dialog_GenerateLoadouts.DefaultSlotsFor(w, Dialog_GenerateLoadouts.BuildDefaultSettings(w));
                weaponSlotsCache[w] = slots;
                LoadoutUtility.SlotsMassBulk(slots, out float cm, out float cb);
                weaponMassBulkCache[w] = (cm, cb);
            }
            return slots;
        }

        private float WeaponSortValue(ThingDef d)
        {
            if (pickSort == PickSort.Score) { LoadoutUtility.GetWeaponScore(d, out float sc, out _); return sc; }
            if (pickSort == PickSort.Special) return LoadoutUtility.GetSpecialSortKey(d);
            if (pickSort == PickSort.Avail) return ownedCounts != null && ownedCounts.TryGetValue(d, out int o) ? o : 0;
            if (pickSort == PickSort.Cost) return LoadoutUtility.WeaponCraftCost(d);
            if (pickSort == PickSort.Mass) { WeaponSlots(d); return weaponMassBulkCache[d].bulk; }
            return 0f;
        }

        // Sorts WITHIN the groups (otherwise the separator headers got mixed up): the group index from the
        // caller's order stays the primary key - the separator rows stay fixed, only the rows below them
        // reorder. LINQ OrderBy is stable, so a list without a sort key (pack picker without score) keeps its
        // order.
        private List<ThingDef> SortedWeapons(List<ThingDef> list)
        {
            Dictionary<string, int> gidx = new Dictionary<string, int>();
            int GroupIdx(ThingDef d)
            {
                string g = weaponGroupLabel != null ? (weaponGroupLabel(d) ?? "") : "";
                if (!gidx.TryGetValue(g, out int i)) { i = gidx.Count; gidx[g] = i; }
                return i;
            }
            foreach (ThingDef d in list) GroupIdx(d);   // group order = first occurrence
            // Locked ones go to the END of the group - visible and unlockable, but at the back.
            IOrderedEnumerable<ThingDef> o = list.OrderBy(GroupIdx)
                .ThenBy(d => LoadoutUtility.IsWeaponDisabled(d) ? 1 : 0);
            if (pickSort == PickSort.Name)
                return (pickSortDesc ? o.ThenByDescending(d => d.label ?? "", StringComparer.OrdinalIgnoreCase)
                                     : o.ThenBy(d => d.label ?? "", StringComparer.OrdinalIgnoreCase)).ToList();
            return (pickSortDesc ? o.ThenByDescending(WeaponSortValue) : o.ThenBy(WeaponSortValue)).ToList();
        }

        private float LoadoutSortValue(Loadout lo)
        {
            ThingDef d = availDefFn != null ? availDefFn(lo) : null;
            if (pickSort == PickSort.Score) return scoreFn != null ? scoreFn(lo) : 0f;
            if (pickSort == PickSort.Special) return d != null ? LoadoutUtility.GetSpecialSortKey(d) : 0f;
            if (pickSort == PickSort.Avail) return d != null && ownedCounts != null && ownedCounts.TryGetValue(d, out int o) ? o : 0;
            if (pickSort == PickSort.Cost) return d != null ? LoadoutUtility.WeaponCraftCost(d) : 0f;
            if (pickSort == PickSort.Mass) { LoadoutUtility.GetLoadoutMassBulk(lo, out _, out float b); return b; }
            return 0f;
        }

        // A selection button that builds its menu on click - so the options are only created when opening, not
        // every frame (the list costs one translation per entry).
        private static void FilterDropdown(Rect r, string label, string tip, Func<List<FloatMenuOption>> build)
        {
            if (LoadoutUtility.ButtonTextFit(r, label))
            {
                List<FloatMenuOption> opts = build();
                if (opts != null && opts.Count > 0) Find.WindowStack.Add(new FloatMenu(opts));
            }
            if (!string.IsNullOrEmpty(tip) && LoadoutUtility.TipWanted(r)) TooltipHandler.TipRegion(r, tip);
        }

        // Labels of both filters - ONE place each, so button text and menu entry cannot drift apart (the most
        // common bug class in this mod).
        private static string PickerFilterLabel(WeaponFilterMode m)
        {
            if (m == WeaponFilterMode.ResearchedWeapons) return "CER_GenerateLoadouts_RadioResearched".Translate().ToString();
            if (m == WeaponFilterMode.AvailableWeapons) return "CER_GenerateLoadouts_RadioAvailable".Translate().ToString();
            return "CER_GenerateLoadouts_RadioAll".Translate().ToString();
        }

        private static string TypeFilterLabel(int t)
        {
            switch (t)
            {
                case 0: return "CER_AssignLoadouts_RangedHeader".Translate().ToString();
                case 1: return "CER_AssignLoadouts_MeleeHeader".Translate().ToString();
                case 2: return "CER_PickPacks_TypeOthers".Translate().ToString();
                case 3: return "CER_PickPacks_GroupVanilla".Translate().ToString();
                case 4: return "CER_PickPacks_GroupRoles".Translate().ToString();
                default: return "CER_PickPacks_TypeAll".Translate().ToString();
            }
        }

        private List<Loadout> SortedLoadouts(List<Loadout> list)
        {
            Dictionary<string, int> gidx = new Dictionary<string, int>();
            int GroupIdx(Loadout l)
            {
                string g = groupLabel != null ? (groupLabel(l) ?? "") : "";
                if (!gidx.TryGetValue(g, out int i)) { i = gidx.Count; gidx[g] = i; }
                return i;
            }
            foreach (Loadout l in list) GroupIdx(l);
            // Locked ones go to the END of the group, just like the weapon rows - visible and unlockable, but no
            // longer in the way.
            IOrderedEnumerable<Loadout> o = list.OrderBy(GroupIdx)
                .ThenBy(l => LoadoutUtility.IsLoadoutDisabled(l) ? 1 : 0);
            if (pickSort == PickSort.Name)
                return (pickSortDesc ? o.ThenByDescending(l => displayLabel(l) ?? "", StringComparer.OrdinalIgnoreCase)
                                     : o.ThenBy(l => displayLabel(l) ?? "", StringComparer.OrdinalIgnoreCase)).ToList();
            return (pickSortDesc ? o.ThenByDescending(LoadoutSortValue) : o.ThenBy(LoadoutSortValue)).ToList();
        }

        // Damage column: intrinsic effects of the weapon, followed by the effects of the default ammo WITH an
        // ammo badge (DrawAmmoEffectIcons) - normal and ammo effects can be told apart. ONE combined tooltip over
        // the whole cell with all lines and details - the drawers collect their lines instead of registering
        // individual tooltips; clipped icons are still included.
        private void DrawDamageCell(float x, float rowY, ThingDef weapon, IEnumerable<LoadoutSlot> slots)
        {
            if (weapon == null) return;
            float iy = rowY + (RowH - 16f) / 2f;
            List<string> tipLines = new List<string>();
            float used = Dialog_GenerateLoadouts.DrawWeaponSpecialIcons(x, iy, weapon, DamageColW - 8f, tipLines);
            ThingDef ammo = null;
            if (slots != null)
                foreach (LoadoutSlot s in slots)
                    if (s.thingDef != null && LoadoutUtility.IsAmmoDef(s.thingDef)) { ammo = s.thingDef; break; }
            if (ammo != null)
            {
                SpecialAbility ab = LoadoutUtility.GetAmmoEffects(ammo);
                if (ab != SpecialAbility.None)
                    Dialog_GenerateLoadouts.DrawAmmoEffectIcons(x + used + (used > 0f ? 2f : 0f), iy,
                        ab, ammo, Mathf.Max(0f, DamageColW - 8f - used - (used > 0f ? 2f : 0f)), tipLines);
            }
            if (tipLines.Count > 0)
                TooltipHandler.TipRegion(new Rect(x, rowY, DamageColW - 8f, RowH), string.Join("\n\n", tipLines));
        }

        // 46 -> 32 (too much spacing between entries). Derived from the content chain instead of guessed, as
        // with the other two lists: the tallest element in a row is the materials strip at 26px, then the
        // mass/bulk bars at 22px, and everything else is 16-20px. Each of them is centered via (RowH - h) / 2,
        // so it is independent of the row height. 32 leaves the largest element 3px of air above and below and
        // shrinks none of the content - exactly the mistake to avoid that happened in the sister popup.
        // The same number for the sidearm and pack picker: it is the same dialog, and the problem affected both.
        private const float RowH = 32f;
        // Widths redistributed (Sidearms and Score columns wider, at the expense of the gap between Contents and
        // Mass/Bulk).
        // Measured instead of guessed, with a layout renderer: the row is 948px wide (window 1000 - 2x18
        // margin - 16 scrollbar). What went where:
        //   NameColX   200 -> 234  (+34) - the name was the only clipped text in the row
        //   ScoreColW   64 ->  76  (+12)
        //   AvailColW   46 ->  80  (+34) - the bare number became the availability bar, which needs room
        //   MaterialsColW 230 -> 210 (-20) - FREE, no loss: DrawIngredientCell breaks at
        //     x + w - 22, so exactly the same 8 icons fit into 200px as before into 220px.
        // The remaining 60px come from the Contents gap. Narrowest case (sidearm picker, Score AND Avail
        // visible): 150px of content = 6 icons at 25px. A weapon loadout has weapon + ammo, so 2-4; the full
        // six only occur with Equipment Packs, which show neither Score nor Avail and so keep 306px.
        private const float NameColX = 234f;
        private const float ScoreColW = 76f;
        private const float AvailColW = 80f;
        private const float MassBulkColW = 190f;
        private const float MaterialsColW = 210f;
        private const float HeaderH = 20f;

        public override Vector2 InitialSize
        {
            get
            {
                // The weapon section counts too - in main weapon mode it provides almost all rows, without it the
                // window would be a slit. And with the two extra columns (damage, assigned) the window needs more
                // width than the plain pack list.
                int count = Mathf.Max(1, allOptions().Count + (weaponOptions != null ? weaponOptions().Count : 0));
                // Doubled from the original estimate - a handful of rows used to force an immediate scroll
                // with no room to compare more than 2-3 at a glance.
                float h = (140f + HeaderH + count * RowH + 44f) * 2f;
                float w = weaponOptions != null ? Mathf.Min(1150f, UI.screenWidth - 40f) : 1000f;
                return new Vector2(w, Mathf.Min(h, UI.screenHeight - 40f));
            }
        }

        // Do not slide under RimWorld's learning readout - see LoadoutUtility.KeepClearOfLearningReadout.
        // Without a collision this changes nothing.
        protected override void SetInitialSizeAndPosition()
        {
            base.SetInitialSizeAndPosition();
            windowRect = LoadoutUtility.KeepClearOfLearningReadout(windowRect);
        }

        public Dialog_PickPacks(string subtitle, Func<List<Loadout>> getCurrent, Action<List<Loadout>> onChanged,
            string windowTitle = null, string itemColumnLabel = null,
            Func<List<Loadout>> allOptions = null, string emptyMessage = null, Func<Loadout, string> displayLabel = null,
            Func<Loadout, string> groupLabel = null, Func<Loadout, float> scoreFn = null, bool showSidearmBulkCap = false,
            Func<Loadout, ThingDef> availDefFn = null, string sourceLabel = null, bool checkProductionOnClose = false,
            string clipboardKind = "pack",
            Func<List<ThingDef>> weaponOptions = null, Func<ThingDef, Loadout> materializeWeapon = null,
            bool singleSelect = false, Action<Loadout> onPickedSingle = null, Action onNonePicked = null,
            string noneLabel = null, Loadout currentSingle = null, ThingDef currentWeaponSingle = null,
            Func<ThingDef, string> weaponGroupLabel = null, Func<ThingDef, int> reservedFn = null,
            string extraActionLabel = null, Action extraAction = null,
            Action onDisabledChanged = null, Func<Loadout> copyCurrent = null) : base(new ReadoutAwareWindowDrawing())
        {
            this.onDisabledChanged = onDisabledChanged;
            this.copyCurrent = copyCurrent;
            this.weaponOptions = weaponOptions;
            this.materializeWeapon = materializeWeapon;
            this.singleSelect = singleSelect;
            this.onPickedSingle = onPickedSingle;
            this.onNonePicked = onNonePicked;
            this.noneLabel = noneLabel;
            this.currentSingle = currentSingle;
            this.currentWeaponSingle = currentWeaponSingle;
            this.weaponGroupLabel = weaponGroupLabel;
            this.reservedFn = reservedFn;
            this.extraActionLabel = extraActionLabel;
            this.extraAction = extraAction;
            this.subtitle = subtitle;
            this.sourceLabel = sourceLabel ?? subtitle;
            this.getCurrent = getCurrent;
            this.onChanged = onChanged;
            this.windowTitle = windowTitle ?? "CER_PickPacks_DefaultTitle".Translate();
            this.itemColumnLabel = itemColumnLabel ?? "CER_PickPacks_DefaultItemColumnLabel".Translate();
            this.clipboardKind = clipboardKind;
            this.allOptions = allOptions ?? EquipmentPackUtility.AllEquipmentPacks;
            this.emptyMessage = emptyMessage ?? "CER_PickPacks_DefaultEmptyMessage".Translate();
            this.displayLabel = displayLabel ?? (l => EquipmentPackUtility.StripMarker(l.label));
            this.groupLabel = groupLabel;
            this.scoreFn = scoreFn;
            this.showSidearmBulkCap = showSidearmBulkCap;
            this.availDefFn = availDefFn;
            this.checkProductionOnClose = checkProductionOnClose;
            doCloseX = true;
            absorbInputAroundWindow = true;
        }

        // Same "check when you're actually done, not per-click" convention Dialog_ChangeAmmo's own PostClose
        // already uses (2026-08-01: manually adding a sidearm the colony doesn't own - a Uzi -
        // showed correctly as "Still missing" in its own tooltip, but no Supply popup ever offered to queue
        // production for it, since nothing in this picker's own live-mutation callers ever checked). Gated on
        // checkProductionOnClose so the SquadSetup preview's own two call sites (pending pin, nothing real
        // yet) are unaffected.
        // Same snapshot comparison as in the tabs window: this picker mutates live, so opening and closing it
        // again without a selection must not trigger the resupply dialog.
        private string openSignature;

        public override void PostOpen()
        {
            base.PostOpen();
            openSignature = LoadoutUtility.AssignmentSignature();
        }

        public override void PostClose()
        {
            base.PostClose();
            // Lock switches were toggled: the caller decides what follows - the Assign tab asks about
            // reassigning the carriers, the pin preview silently recomputes.
            if (locksTouched) onDisabledChanged?.Invoke();
            if (!checkProductionOnClose) return;
            // Not while the tabs window is open - see the full reasoning in Dialog_ChangeAmmo.PostClose. There
            // CheckSupplyOnLeavingAssign collects the same prompt once at the end; asking here as well turns every
            // attached pack into a prompt.
            if (Find.WindowStack?.WindowOfType<Dialog_CEReloadedTabs>() != null) return;
            if (openSignature == null || LoadoutUtility.AssignmentSignature() != openSignature)
                LoadoutUtility.CheckAndOpenMissingProductionPopup();
        }

        public override void DoWindowContents(Rect inRect)
        {
            Text.Font = GameFont.Medium;
            Widgets.Label(new Rect(0, 0, inRect.width, 30f), windowTitle);
            Text.Font = GameFont.Small;
            GUI.color = new Color(1f, 1f, 1f, 0.6f);
            Widgets.Label(new Rect(0, 30f, inRect.width, 34f), subtitle);
            GUI.color = Color.white;

            float y = 66f;
            search = LoadoutUtility.SearchBox(0f, y, search);

            // Availability filter next to the search (the filters from the old tab) - the same three radios with
            // the same keys as in the old weapons tab, via the measuring radio helper (the RadioButtonLabeled
            // wrapping issue was exactly the pattern this helper avoids). Affects ONLY the weapon section.
            if (WeaponCols)
            {
                EnsurePickerCaches();
                // ===== Two selection buttons instead of eight radios =====
                //
                // The roles were missing from the options at the top, and the radio buttons took up too much
                // space - hence selection buttons. Eight radios needed the whole header row, and "Vanilla
                // loadouts" was already clipped at the right edge; there was no room for a sixth type (roles)
                // anyway. Two buttons need about 300 points together instead of well over 900, and a selection
                // menu grows with every further choice without the row getting wider.
                float wfX = 300f;
                Rect availBtn = new Rect(wfX, y, 168f, 24f);
                FilterDropdown(availBtn, "CER_PickPacks_FilterShowFmt".Translate(PickerFilterLabel(pickerFilter)).ToString(),
                    "CER_PickPacks_FilterShowTip".Translate(), () =>
                {
                    var opts = new List<FloatMenuOption>();
                    foreach (WeaponFilterMode m in new[] { WeaponFilterMode.AllWeapons, WeaponFilterMode.ResearchedWeapons, WeaponFilterMode.AvailableWeapons })
                    {
                        WeaponFilterMode captured = m;
                        opts.Add(new FloatMenuOption(PickerFilterLabel(captured), () => pickerFilter = captured));
                    }
                    return opts;
                });
                wfX = availBtn.xMax + 8f;

                // Second group: weapon type. Only in single-select mode - in the sidearm checklist it would get in
                // the way of the bulk field at the top right.
                if (singleSelect)
                {
                    Rect typeBtn = new Rect(wfX, y, 168f, 24f);
                    FilterDropdown(typeBtn, "CER_PickPacks_FilterTypeFmt".Translate(TypeFilterLabel(typeFilter)).ToString(),
                        "CER_PickPacks_FilterTypeTip".Translate(), () =>
                    {
                        var opts = new List<FloatMenuOption>();
                        // -1 all, 0 ranged, 1 melee, 2 other, 3 vanilla loadouts, 4 roles.
                        // 4 is new - previously roles could not be filtered on, although they are a separate
                        // section of the list.
                        foreach (int t in new[] { -1, 0, 1, 2, 3, 4 })
                        {
                            int captured = t;
                            opts.Add(new FloatMenuOption(TypeFilterLabel(captured), () => typeFilter = captured));
                        }
                        return opts;
                    });
                }
            }

            if (showSidearmBulkCap)
            {
                var s = CEReloadedMod.settings;
                if (s != null)
                {
                    Rect bulkLbl = new Rect(inRect.width - 230f, y, 150f, 24f);
                    Widgets.Label(bulkLbl, "CER_PickPacks_MaxSidearmBulkLabel".Translate());
                    Rect bulkField = new Rect(inRect.width - 70f, y, 70f, 24f);
                    string prevBulk = s.sidearmMaxBulk;
                    s.sidearmMaxBulk = Widgets.TextField(bulkField, s.sidearmMaxBulk);
                    // The value lives in the MOD settings (across saves), but nothing in this dialog ever wrote it
                    // out - RimWorld only saves when its own settings window closes. So it took effect immediately
                    // and was gone again on the next start, which felt as if it had no effect at all. Only write
                    // on a real change, not per frame - Write() goes to disk.
                    if (s.sidearmMaxBulk != prevBulk) CEReloadedMod.settings.Write();
                    TooltipHandler.TipRegion(new Rect(bulkLbl.x, y, 230f, 24f),
                        "CER_PickPacks_SidearmBulkCapTooltip".Translate());
                }
            }
            y += 30f;
            Widgets.DrawLineHorizontal(0, y, inRect.width);
            y += 10f;

            List<Loadout> packs = allOptions();
            if (!string.IsNullOrEmpty(search))
                packs = packs.Where(p => displayLabel(p) != null && displayLabel(p).IndexOf(search, StringComparison.OrdinalIgnoreCase) >= 0).ToList();

            // "Max sidearm bulk" now takes effect here too (the field had no effect). The field sat in this
            // window and did nothing for this list - it was only the value for the automatic picker. Anyone who
            // types a number into a field above a list expects the list to react. So the same rule as in the
            // solver (PickSidearm: weapon bulk over the limit drops out; explosives and shields are never capped
            // there, and not here either - see SidearmAssignmentUtility.CapRelevantBulk). A ticked loadout ALWAYS
            // stays visible, otherwise it could not be unticked. The number of hidden ones is shown in the bar
            // at the bottom, so "where did my loadout go" has an answer.
            int hiddenByCap = 0;
            float capValue = 0f;
            if (showSidearmBulkCap)
            {
                capValue = SidearmAssignmentUtility.MaxSidearmBulk();
                List<Loadout> currentNow = getCurrent() ?? new List<Loadout>();
                int before = packs.Count;
                packs = packs.Where(p => currentNow.Contains(p) || SidearmAssignmentUtility.CapRelevantBulk(p) <= capValue).ToList();
                hiddenByCap = before - packs.Count;
            }

            // The weapon section (lazy rework): the same search, the same bulk limit as the loadout rows (grenades
            // are exempt from the limit as in the solver - PickGrenade has none, see
            // SidearmAssignmentUtility.CapRelevantBulk).
            List<ThingDef> weapons = weaponOptions != null && materializeWeapon != null
                ? (weaponOptions() ?? new List<ThingDef>()) : new List<ThingDef>();
            if (!string.IsNullOrEmpty(search))
                weapons = weapons.Where(d => d.label != null && d.label.IndexOf(search, StringComparison.OrdinalIgnoreCase) >= 0).ToList();
            if (showSidearmBulkCap && weapons.Count > 0)
            {
                int beforeW = weapons.Count;
                weapons = weapons.Where(d => Dialog_GenerateLoadouts.IsGrenade(d) || LoadoutUtility.WeaponBulk(d) <= capValue).ToList();
                hiddenByCap += beforeW - weapons.Count;
            }

            // Availability filter (weapon section only; the current weapon always stays visible).
            if (WeaponCols && weapons.Count > 0 && pickerFilter != WeaponFilterMode.AllWeapons)
            {
                EnsurePickerCaches();
                weapons = weapons.Where(d => d == currentWeaponSingle
                    || (pickerFilter == WeaponFilterMode.AvailableWeapons
                        ? ownedCounts.TryGetValue(d, out int oc) && oc > 0
                        : Dialog_GenerateLoadouts.IsResearched(d))).ToList();
            }

            // Type filter (second group, AND-combined with availability) - affects weapon AND loadout rows (their
            // type via the representative weapon; without a weapon always visible).
            // NO always-visible exception for the current choice any more (a Ruger Mini showed up under
            // "others"): the current weapon bypassed every type filter and then appeared as the only ranged
            // weapon under an unrelated filter choice - a type filter is a view lens, not an availability
            // question.
            if (WeaponCols && singleSelect && typeFilter >= 0)
            {
                if (typeFilter == 3)
                {
                    // Vanilla loadouts only: weapon list empty, roles out.
                    weapons = new List<ThingDef>();
                    packs = packs.Where(l => !RoleUtility.IsRoleLoadout(l)).ToList();
                }
                else if (typeFilter == 4)
                {
                    // Roles only - the counterpart to 3, same pattern.
                    weapons = new List<ThingDef>();
                    packs = packs.Where(l => RoleUtility.IsRoleLoadout(l)).ToList();
                }
                else
                {
                    weapons = weapons.Where(d => WeaponTypeOf(d) == typeFilter).ToList();
                    packs = packs.Where(l =>
                    {
                        ThingDef d = availDefFn != null ? availDefFn(l) : null;
                        return d == null || WeaponTypeOf(d) == typeFilter;
                    }).ToList();
                }
            }

            // Sorting is ALWAYS active (default is score) - within the groups, the separator rows stay fixed.
            EnsurePickerCaches();
            weapons = SortedWeapons(weapons);
            packs = SortedLoadouts(packs);

            float bottomBarH = 40f;
            Rect listRect = new Rect(0, y, inRect.width, inRect.height - y - bottomBarH);
            DrawPackList(listRect, packs, weapons, hiddenByCap > 0 && packs.Count == 0 && weapons.Count == 0
                ? "CER_PickPacks_AllHiddenByBulkCapFmt".Translate(capValue.ToString("0.#")).ToString() : null);
            if (hiddenByCap > 0 && packs.Count > 0)
            {
                Rect noteRect = new Rect(256f, inRect.height - bottomBarH + 6f, inRect.width - 256f, 30f);
                GUI.color = new Color(1f, 1f, 1f, 0.55f);
                Text.Anchor = TextAnchor.MiddleLeft;
                Widgets.Label(noteRect, "CER_PickPacks_HiddenByBulkCapFmt".Translate(hiddenByCap.ToString(), capValue.ToString("0.#")));
                Text.Anchor = TextAnchor.UpperLeft;
                GUI.color = Color.white;
            }

            // Close and Copy share ONE font size - see LoadoutUtility.ButtonRowFont.
            Rect closeRect = new Rect(0, inRect.height - bottomBarH + 6f, 120f, 30f);
            Rect copyRect = new Rect(128f, inRect.height - bottomBarH + 6f, 120f, 30f);
            string closeLbl = "CER_PickPacks_CloseButton".Translate().ToString();
            string copyLbl = "CER_PickPacks_CopyButton".Translate().ToString();
            GameFont bottomFont = LoadoutUtility.ButtonRowFont((closeRect, closeLbl), (copyRect, copyLbl));
            bool RowBtn(Rect r, string lbl) { GameFont pf = Text.Font; Text.Font = bottomFont; bool c = LoadoutUtility.ButtonTextFit(r, lbl); Text.Font = pf; return c; }
            if (RowBtn(closeRect, closeLbl)) Close();

            if (singleSelect)
            {
                float bx = 128f;
                // Copy the CURRENT assignment - clipboard kind 'mainweapon', the rows' paste column accepts it.
                if (copyCurrent != null)
                {
                    Loadout curCopy = copyCurrent();
                    Rect copyBtn = new Rect(bx, inRect.height - bottomBarH + 6f, 120f, 30f);
                    GUI.color = curCopy != null ? Color.white : new Color(1f, 1f, 1f, 0.45f);
                    if (LoadoutUtility.ButtonTextFit(copyBtn, "CER_PickPacks_CopyButton".Translate().ToString()) && curCopy != null)
                    {
                        LoadoutClipboard.Copy("mainweapon", new List<Loadout> { curCopy }, sourceLabel);
                        // The message also says WHERE it gets pasted. "Copied." alone leaves the player with an
                        // explanation that promises a "paste column" - which sits in a different window they first
                        // have to open. This tripped people up twice (see DrawPasteColumn).
                        Messages.Message("CER_PickPacks_CopiedWeaponMsgFmt".Translate(LoadoutUtility.LoadoutDisplayName(curCopy), sourceLabel), MessageTypeDefOf.TaskCompletion, false);
                    }
                    GUI.color = Color.white;
                    TooltipHandler.TipRegion(copyBtn, "CER_PickPacks_CopyWeaponTip".Translate());
                    bx += 128f;
                }
                // Delete all own (vanilla) loadouts - roles stay untouched; allOptions() fetches the list fresh
                // on the next frame.
                List<Loadout> ownDeletable = (allOptions() ?? new List<Loadout>()).Where(l => l != null && !RoleUtility.IsRoleLoadout(l)).ToList();
                if (ownDeletable.Count > 0)
                {
                    Rect delBtn = new Rect(bx, inRect.height - bottomBarH + 6f, 220f, 30f);
                    if (LoadoutUtility.ButtonTextFit(delBtn, "CER_PickPacks_DeleteOwnFmt".Translate(ownDeletable.Count.ToString()).ToString()))
                    {
                        Find.WindowStack.Add(LoadoutUtility.CompactConfirmation(
                            "CER_PickPacks_DeleteOwnConfirmFmt".Translate(ownDeletable.Count.ToString()),
                            () =>
                            {
                                LoadoutDeletionUtility.DeleteAll(ownDeletable);
                                CombatReadiness.Invalidate();
                            }, true));
                    }
                    TooltipHandler.TipRegion(delBtn, "CER_PickPacks_DeleteOwnTip".Translate());
                    bx += 228f;
                }
                if (extraAction != null && !string.IsNullOrEmpty(extraActionLabel))
                {
                    Rect extraRect = new Rect(bx, inRect.height - bottomBarH + 6f, 260f, 30f);
                    if (LoadoutUtility.ButtonTextFit(extraRect, extraActionLabel)) { extraAction(); Close(); }
                }
                return;
            }

            List<Loadout> currentForCopy = getCurrent() ?? new List<Loadout>();
            // An empty selection is a legitimate thing to copy (2026-08-01): pasting it
            // elsewhere clears that pawn's packs/sidearms, standing in for a delete button this dialog
            // doesn't have. No longer gated/dimmed on Count > 0 - always enabled.
            if (RowBtn(copyRect, copyLbl))
            {
                LoadoutClipboard.Copy(clipboardKind, currentForCopy, sourceLabel);
                Messages.Message((currentForCopy.Count > 0
                    ? "CER_PickPacks_CopiedMessage".Translate(currentForCopy.Count.ToString(), itemColumnLabel.ToLower())
                    : "CER_PickPacks_CopiedEmptyMessage".Translate(itemColumnLabel.ToLower())), MessageTypeDefOf.TaskCompletion, false);
            }
            TooltipHandler.TipRegion(copyRect, currentForCopy.Count > 0
                ? "CER_PickPacks_CopyTooltip".Translate()
                : "CER_PickPacks_CopyTooltipEmpty".Translate());
        }

        // emptyOverride: the text for an empty list when the reason is not search/no options (everything hidden
        // by the bulk limit).
        private void DrawPackList(Rect rect, List<Loadout> packs, List<ThingDef> weapons, string emptyOverride = null)
        {
            // No boxed panel background - matches the plain, direct-on-window-background look Dialog_
            // SquadSetup's own lists use (see Dialog_GenerateEquipmentPack.DrawPlanList for the same fix),
            // so this popup's look doesn't diverge from the rest of the mod's list-style dialogs.
            Rect inner = rect;

            if (packs.Count == 0 && weapons.Count == 0)
            {
                GUI.color = new Color(1f, 1f, 1f, 0.5f);
                // Without a search text, "No matches for \"\"" is no information but an empty pair of quotes (seen
                // with the type filter "Vanilla loadouts" in a save without hand-built loadouts). Then the
                // sentence says what is really going on: the FILTER shows nothing, not the search.
                Widgets.Label(inner, emptyOverride ?? (allOptions().Count == 0 ? emptyMessage
                    : string.IsNullOrEmpty(search)
                        ? "CER_PickPacks_NoneOfThisType".Translate().ToString()
                        : "CER_PickPacks_NoMatches".Translate(search).ToString()));
                GUI.color = Color.white;
                return;
            }

            bool showScore = scoreFn != null;
            bool showAvail = availDefFn != null;
            float contentsColX = NameColX + (showScore ? ScoreColW : 0f)
                + (WeaponCols ? DamageColW : 0f) + (showAvail ? AvailColW : 0f);
            float rowSpaceW = inner.width - 16f;
            float statsColW = MassBulkColW + MaterialsColW;

            // Sortable headers (all columns sortable): click sorts, a second click reverses the direction; sorting
            // is always on (default score descending), within the groups. Only the contents column does not sort
            // (an icon strip has no measure).
            Text.Font = GameFont.Tiny;
            void HeaderCell(Rect r, string lbl, PickSort mode)
            {
                if (LoadoutUtility.ColumnHeader(r, lbl, pickSort == mode, pickSortDesc))
                {
                    if (pickSort == mode) pickSortDesc = !pickSortDesc;
                    else { pickSort = mode; pickSortDesc = mode != PickSort.Name; }
                }
            }
            HeaderCell(new Rect(inner.x + 24f, inner.y, NameColX - 30f, 18f), itemColumnLabel, PickSort.Name);
            float hx = inner.x + NameColX;
            if (showScore) { HeaderCell(new Rect(hx, inner.y, ScoreColW - 6f, 18f), "CER_PickPacks_ScoreColumnHeader".Translate(), PickSort.Score); hx += ScoreColW; }
            if (WeaponCols)
            {
                // Damage column (lists the damage types): the same icons and the same header as the special column
                // of the old weapons tab.
                HeaderCell(new Rect(hx, inner.y, DamageColW - 6f, 18f), "CER_GenerateLoadouts_ColSpecial".Translate(), PickSort.Special);
                hx += DamageColW;
            }
            if (showAvail) HeaderCell(new Rect(hx, inner.y, AvailColW - 6f, 18f), "CER_PickPacks_AvailColumnHeader".Translate(), PickSort.Avail);
            GUI.color = new Color(1f, 1f, 1f, 0.55f);
            Widgets.Label(new Rect(inner.x + contentsColX, inner.y, rowSpaceW - statsColW - contentsColX - 6f, 18f), "CER_PickPacks_ContentsColumnHeader".Translate());
            GUI.color = Color.white;
            // Production cost BEFORE mass/bulk (columns swapped). Only the order in the right block changes; the
            // row below reads the same two constants in the same new order.
            HeaderCell(new Rect(inner.x + rowSpaceW - statsColW, inner.y, MaterialsColW - 6f, 18f), "CER_PickPacks_ProductionCostColumnHeader".Translate(), PickSort.Cost);
            HeaderCell(new Rect(inner.x + rowSpaceW - statsColW + MaterialsColW, inner.y, MassBulkColW - 6f, 18f), "CER_PickPacks_MassBulkColumnHeader".Translate(), PickSort.Mass);
            Text.Font = GameFont.Small;
            Widgets.DrawLineHorizontal(inner.x, inner.y + HeaderH, inner.width);

            Rect listArea = new Rect(inner.x, inner.y + HeaderH + 4f, inner.width, inner.height - HeaderH - 4f);
            const float groupHeaderH = 26f;

            // ===== Groups as collapsible segments =====
            // The loadout and weapon sections become ONE segment list; every group with a header is a bar with an
            // arrow and a count, a click collapses/expands it (collapsedGroups, per window opening).
            // Rows without a group header (groupLabel returns null) always stay visible - the same convention as
            // before ("the silent group belongs to the caller").
            var segments = new List<(string header, List<Loadout> los, List<ThingDef> defs)>();
            {
                string curG = null; List<Loadout> bucket = null; bool first = true;
                foreach (Loadout p in packs)
                {
                    string g = groupLabel != null ? groupLabel(p) : null;
                    if (first || g != curG)
                    {
                        bucket = new List<Loadout>();
                        segments.Add((g, bucket, null));
                        curG = g; first = false;
                    }
                    bucket.Add(p);
                }
            }
            if (weapons.Count > 0)
            {
                if (weaponGroupLabel == null)
                    segments.Add(("CER_GenerateLoadouts_WeaponBlocksHeader".Translate().ToString(), null, new List<ThingDef>(weapons)));
                else
                {
                    string curG = null; List<ThingDef> bucket = null; bool first = true;
                    foreach (ThingDef w in weapons)
                    {
                        string g = weaponGroupLabel(w) ?? "";
                        if (first || g != curG)
                        {
                            bucket = new List<ThingDef>();
                            segments.Add((g, null, bucket));
                            curG = g; first = false;
                        }
                        bucket.Add(w);
                    }
                }
            }

            // The none row gets its own group bar like 'Roles' / 'Ranged' / 'Melee' below, so it reads as one
            // of the choices instead of a stray row above the first group.
            bool hasNoneRow = singleSelect && onNonePicked != null;
            string noneHeader = !hasNoneRow ? null
                : noneLabel == null ? "CER_PickPacks_GroupUnarmed".Translate().ToString()
                : "CER_PickPacks_GroupAuto".Translate().ToString();
            bool noneCollapsed = hasNoneRow && collapsedGroups.Contains(noneHeader);
            float noneH = hasNoneRow ? groupHeaderH + (noneCollapsed ? 0f : RowH) : 0f;
            float viewH = noneH + 4f;
            foreach (var seg in segments)
            {
                int segCount = seg.los != null ? seg.los.Count : seg.defs.Count;
                bool hasHeader = !string.IsNullOrEmpty(seg.header);
                bool segCollapsed = hasHeader && collapsedGroups.Contains(seg.header);
                viewH += (hasHeader ? groupHeaderH : 0f) + (segCollapsed ? 0f : segCount * RowH);
            }
            Rect viewRect = new Rect(0, 0, listArea.width - 16f, Mathf.Max(viewH, listArea.height));
            List<Loadout> current = getCurrent() ?? new List<Loadout>();
            Widgets.BeginScrollView(listArea, ref scroll, viewRect);
            float ry = 0f;
            // "No weapon"/"Auto" row of single-select mode, at the very top under its own group bar.
            if (hasNoneRow && DrawGroupBar(new Rect(0, ry, viewRect.width, groupHeaderH - 3f), noneHeader, 1, ref ry))
            {
                // No more radios: the whole row is the click target, the current choice carries the selection
                // highlight.
                Rect noneRow = new Rect(0, ry, viewRect.width, RowH);
                bool noneOn = currentSingle == null && currentWeaponSingle == null;
                if (noneOn) Widgets.DrawHighlightSelected(noneRow);
                else if (Mouse.IsOver(noneRow)) Widgets.DrawHighlight(noneRow);
                // Same icon column and full brightness as the weapon rows below; the default ("no weapon")
                // case gets the red X in the icon slot so the row is recognizable at a glance. A custom
                // label (e.g. 'Auto' in the squad planner) is not an unarm action and keeps the slot empty.
                float iconX = noneRow.x + 8f;
                if (noneLabel == null)
                    GUI.DrawTexture(new Rect(iconX + 2f, noneRow.y + (RowH - 20f) / 2f, 20f, 20f), Widgets.CheckboxOffTex);
                Rect noneNameRect = new Rect(iconX + 30f, noneRow.y, NameColX - (iconX + 30f), RowH);
                Text.Anchor = TextAnchor.MiddleLeft;
                Widgets.Label(noneNameRect,
                    (noneLabel ?? "CER_AssignLoadouts_NoneMenuOption".Translate().ToString()).Truncate(noneNameRect.width - 4f));
                Text.Anchor = TextAnchor.UpperLeft;
                if (Widgets.ButtonInvisible(noneRow)) { onNonePicked(); Close(); }
                ry += RowH;
            }
            foreach (var seg in segments)
            {
                bool hasHeader = !string.IsNullOrEmpty(seg.header);
                bool open = true;
                if (hasHeader)
                {
                    int segCount = seg.los != null ? seg.los.Count : seg.defs.Count;
                    open = DrawGroupBar(new Rect(0, ry, viewRect.width, groupHeaderH - 3f), seg.header, segCount, ref ry);
                }
                if (!open) continue;
                if (seg.los != null)
                    foreach (Loadout p in seg.los)
                    {
                        DrawPackRow(new Rect(0, ry, viewRect.width, RowH), p, current, showScore, showAvail, contentsColX, statsColW);
                        ry += RowH;
                    }
                else
                    foreach (ThingDef w in seg.defs)
                    {
                        DrawWeaponRow(new Rect(0, ry, viewRect.width, RowH), w, current, showScore, showAvail, contentsColX, statsColW);
                        ry += RowH;
                    }
            }
            Widgets.EndScrollView();
        }

        // Styled, clickable group bar: subtle bar, open/closed arrow, name + row count. The whole bar is the
        // click area; the header text is the collapse key. Advances ry past the bar and returns whether the
        // group is open (its rows should be drawn).
        private bool DrawGroupBar(Rect bar, string header, int count, ref float ry)
        {
            bool collapsed = collapsedGroups.Contains(header);
            Widgets.DrawBoxSolid(bar, new Color(1f, 1f, 1f, collapsed ? 0.05f : 0.08f));
            if (Mouse.IsOver(bar)) Widgets.DrawHighlight(bar);
            GUI.color = new Color(1f, 1f, 1f, 0.85f);
            GUI.DrawTexture(new Rect(bar.x + 5f, bar.y + (bar.height - 14f) / 2f, 14f, 14f),
                collapsed ? TexButton.Reveal : TexButton.Collapse);
            Text.Anchor = TextAnchor.MiddleLeft;
            Widgets.Label(new Rect(bar.x + 26f, bar.y, bar.width - 30f, bar.height), header + "  (" + count + ")");
            Text.Anchor = TextAnchor.UpperLeft;
            GUI.color = Color.white;
            if (Widgets.ButtonInvisible(bar))
            {
                if (collapsed) collapsedGroups.Remove(header);
                else collapsedGroups.Add(header);
            }
            ry += bar.height + 3f;   // callers pass a bar of groupHeaderH - 3
            return !collapsed;
        }

        // A weapon row of the weapon section: same columns as a loadout row, just computed from the defaults
        // (DefaultSlotsFor/DefaultMassBulkFor) from which the shared template is created on ticking. The
        // production cost column stays empty - a template that does not exist yet has no bill; the weapon's own
        // availability is shown in the Avail bar.
        private void DrawWeaponRow(Rect row, ThingDef w, List<Loadout> current, bool showScore, bool showAvail, float contentsColX, float statsColW)
        {
            if (Mouse.IsOver(row)) Widgets.DrawHighlight(row);

            bool locked = LoadoutUtility.IsWeaponDisabled(w);
            bool picked;
            float iconX;
            if (singleSelect)
            {
                // No more radio: the whole row selects, the current weapon carries the selection highlight.
                if (w == currentWeaponSingle) Widgets.DrawHighlightSelected(row);
                // Lock switch - BEFORE the row click, otherwise the invisible row button eats the click on the lock.
                // Locked: row greyed out, not selectable, placed at the end of the group (SortedWeapons); the switch
                // stays usable.
                // Custom, replaceable icons instead of RimWorld's forbid hand (the finger was unclear as an icon) -
                // Textures/UI/Icon_DoNotUse(.Off).png.
                Rect lockRect = new Rect(row.x + NameColX - 24f, row.y + (RowH - 18f) / 2f, 18f, 18f);
                GUI.color = new Color(1f, 1f, 1f, locked || Mouse.IsOver(row) ? 1f : 0.3f);
                if (Widgets.ButtonImage(lockRect, locked ? TexDoNotUseOn : TexDoNotUseOff))
                {
                    LoadoutUtility.SetWeaponDisabled(w, !locked);
                    locked = !locked;
                    locksTouched = true;
                }
                GUI.color = Color.white;
                TooltipHandler.TipRegion(lockRect, locked ? "CER_PickPacks_UnlockTip".Translate() : "CER_PickPacks_LockTip".Translate());
                picked = !locked && Widgets.ButtonInvisible(row);
                iconX = row.x + 8f;
            }
            else
            {
                bool newOn = false;
                Widgets.Checkbox(new Vector2(row.x, row.y + (RowH - 24f) / 2f), ref newOn, 24f);
                picked = newOn;
                iconX = row.x + 30f;
            }
            // Weapon icon before the name - loadout rows have none, but in a plain weapon list it carries the
            // recognition.
            if (locked) GUI.color = new Color(1f, 1f, 1f, 0.4f);
            Widgets.ThingIcon(new Rect(iconX, row.y + (RowH - 24f) / 2f, 24f, 24f), w);
            Rect nameRect = new Rect(iconX + 30f, row.y, row.x + NameColX - (iconX + 30f) - (singleSelect ? 30f : 4f), RowH);
            Text.Anchor = TextAnchor.MiddleLeft;
            Widgets.Label(nameRect, w.LabelCap.ToString().Truncate(nameRect.width - 4f));
            Text.Anchor = TextAnchor.UpperLeft;
            GUI.color = Color.white;
            // The detailed weapon tooltip as in the old weapons tab: name + description on the name, the score
            // derivation on the bar. TipWanted first - otherwise the text would be built per row and frame.
            if (LoadoutUtility.TipWanted(nameRect))
                TooltipHandler.TipRegion(nameRect, w.LabelCap.ToString() + "\n\n" + (w.description ?? ""));
            if (!singleSelect && Widgets.ButtonInvisible(nameRect)) picked = true;

            if (picked)
            {
                Loadout made = materializeWeapon(w);
                if (made != null)
                {
                    if (singleSelect) { onPickedSingle?.Invoke(made); Close(); }
                    else if (!current.Contains(made))
                    {
                        List<Loadout> updated = new List<Loadout>(current) { made };
                        onChanged(updated);
                    }
                }
            }

            float afterName = row.x + NameColX;
            if (showScore)
            {
                LoadoutUtility.GetWeaponScore(w, out float sc, out string scTip);
                Rect scoreRect = new Rect(afterName, row.y + (RowH - 16f) / 2f, ScoreColW - 8f, 16f);
                LoadoutUtility.ScoreBar(scoreRect, sc, 100f, null, true,
                    LoadoutUtility.TipWanted(scoreRect) ? scTip : null);
                afterName += ScoreColW;
            }
            if (WeaponCols)
            {
                DrawDamageCell(afterName, row.y, w, WeaponSlots(w));
                afterName += DamageColW;
            }
            if (showAvail)
            {
                Rect availRect = new Rect(afterName + 4f, row.y + (RowH - 20f) / 2f, AvailColW - 12f, 20f);
                var seg = LoadoutUtility.WeaponAvailability(w);
                string availTip = null;
                if (LoadoutUtility.TipWanted(availRect))
                {
                    availTip = LoadoutUtility.BuildWeaponLocationTooltip(w);
                    // Scarcity line from the old pin menu: the colony stock bar does not know what the LIVE preview
                    // plan has already earmarked for others.
                    int reserved = reservedFn != null ? reservedFn(w) : 0;
                    if (reserved > 0)
                        availTip = (availTip ?? "") + "\n" + "CER_PickWeapon_ReservedTipFmt".Translate(reserved.ToString()).ToString();
                }
                LoadoutUtility.DrawAvailabilityBar(availRect, seg, horizontal: true, availTip);
            }

            // NO contents strip for weapon rows any more (contents are only shown for roles and vanilla loadouts)
            // - weapon + default ammo are already shown in the name and damage column; the slots are only needed
            // for the cost calculation.
            List<LoadoutSlot> slots = WeaponSlots(w);

            float statsX = row.width - statsColW;
            // Production cost of the defaults (it was empty everywhere): the same calculation as for the loadout
            // rows, just from the default slots instead of a loadout object.
            if (!weaponCostCache.TryGetValue(w, out Dictionary<ThingDef, int> wCosts))
            {
                wCosts = LoadoutUtility.GetSlotsProductionCosts(slots);
                weaponCostCache[w] = wCosts;
            }
            LoadoutUtility.DrawTotalCostLine(statsX, row.y + (RowH - 26f) / 2f, MaterialsColW - 10f, wCosts, "", labelW: 0f, showCounts: false);

            (float mass, float bulk) = weaponMassBulkCache[w];
            LoadoutUtility.DrawMassBulkBarsHorizontal(new Rect(statsX + MaterialsColW, row.y + (RowH - 22f) / 2f, MassBulkColW - 10f, 22f), mass, bulk);
        }

        private void DrawPackRow(Rect row, Loadout pack, List<Loadout> current, bool showScore, bool showAvail, float contentsColX, float statsColW)
        {
            if (Mouse.IsOver(row)) Widgets.DrawHighlight(row);

            Rect nameRect = new Rect(row.x + 30f, row.y, NameColX - 34f, RowH);
            if (singleSelect)
            {
                // Single-select without radio: the WHOLE row selects and closes; the current choice carries the
                // selection highlight.
                if (pack == currentSingle) Widgets.DrawHighlightSelected(row);
                // Lock switch for ROLES and loadouts too (with all consequences) - identical to the weapon row:
                // drawn BEFORE the row click, otherwise the invisible row button eats the click on the lock.
                // Only in single-select mode; the pack checklist has no locking.
                bool loLocked = LoadoutUtility.IsLoadoutDisabled(pack);
                Rect loLockRect = new Rect(row.x + NameColX - 24f, row.y + (RowH - 18f) / 2f, 18f, 18f);
                GUI.color = new Color(1f, 1f, 1f, loLocked || Mouse.IsOver(row) ? 1f : 0.3f);
                if (Widgets.ButtonImage(loLockRect, loLocked ? TexDoNotUseOn : TexDoNotUseOff))
                {
                    LoadoutUtility.SetLoadoutDisabled(pack, !loLocked);
                    loLocked = !loLocked;
                    locksTouched = true;
                }
                GUI.color = Color.white;
                TooltipHandler.TipRegion(loLockRect, loLocked ? "CER_PickPacks_UnlockLoadoutTip".Translate() : "CER_PickPacks_LockLoadoutTip".Translate());
                Rect singleName = new Rect(row.x + 8f, row.y, NameColX - 36f, RowH);
                if (loLocked) GUI.color = new Color(1f, 1f, 1f, 0.4f);
                Text.Anchor = TextAnchor.MiddleLeft;
                Widgets.Label(singleName, displayLabel(pack).Truncate(singleName.width - 4f));
                Text.Anchor = TextAnchor.UpperLeft;
                GUI.color = Color.white;
                if (!loLocked && Widgets.ButtonInvisible(row)) { onPickedSingle?.Invoke(pack); Close(); }
            }
            else
            {
                bool on = current.Contains(pack);
                bool newOn = on;
                Widgets.Checkbox(new Vector2(row.x, row.y + (RowH - 24f) / 2f), ref newOn, 24f);

                Text.Anchor = TextAnchor.MiddleLeft;
                Widgets.Label(nameRect, displayLabel(pack).Truncate(nameRect.width - 4f));
                Text.Anchor = TextAnchor.UpperLeft;
                // The NAME toggles too (a click on "Shield Kit" did nothing, only the 24x24 box in front of it
                // reacted). RimWorld's own labeled checkbox toggles on the whole label everywhere else - here the
                // label looked like a target and was not one. Deliberately ONLY the name column and not the whole
                // row: to its right are the contents and materials cells with their own tooltips.
                if (Widgets.ButtonInvisible(nameRect)) newOn = !on;

                if (newOn != on)
                {
                    List<Loadout> updated = new List<Loadout>(current);
                    if (newOn) updated.Add(pack); else updated.Remove(pack);
                    onChanged(updated);
                }
            }

            float afterName = row.x + NameColX;
            if (showScore)
            {
                float sc = scoreFn(pack);
                Rect scoreRect = new Rect(afterName, row.y + (RowH - 16f) / 2f, ScoreColW - 8f, 16f);
                LoadoutUtility.ScoreBar(scoreRect, sc, 100f, null, true, null);
                afterName += ScoreColW;
            }

            if (WeaponCols)
            {
                DrawDamageCell(afterName, row.y, availDefFn != null ? availDefFn(pack) : null, pack.Slots);
                afterName += DamageColW;
            }

            if (showAvail)
            {
                // The shared availability bar instead of the bare number. Exactly the pattern the weapon overview
                // already uses for the same question: GetWeaponInventory -> the four pools carried /
                // assigned-and-carried / assigned-in-storage / free in storage, plus what is queued at the
                // workbench. WITHOUT pawn context, although the picker is open for one colonist - the bar then
                // colors "free in storage" green instead of yellow, and that is the right statement here: the
                // row's question is "does this exist at all", not "does THIS colonist already have it". Cross-check
                // against the old display: it showed stored-demanded, i.e. exactly the green segment - nothing is
                // lost, only the rest is added.
                // Previously this was a traffic-light number (green 2+, yellow 1, red 0) from ComputeAvailability.
                ThingDef mainDef = availDefFn(pack);
                Rect availRect = new Rect(afterName + 4f, row.y + (RowH - 20f) / 2f, AvailColW - 12f, 20f);
                if (mainDef != null)
                {
                    var seg = LoadoutUtility.WeaponAvailability(mainDef);
                    // TipWanted first: BuildWeaponLocationTooltip runs over all maps and all colonists, and this bar
                    // sits in EVERY row of the list.
                    LoadoutUtility.DrawAvailabilityBar(availRect, seg, horizontal: true,
                        LoadoutUtility.TipWanted(availRect) ? LoadoutUtility.BuildWeaponLocationTooltip(mainDef) : null);
                }
            }

            List<LoadoutSlot> slots = pack.OwnSlots?.ToList() ?? new List<LoadoutSlot>();
            float contentsW = row.width - statsColW - contentsColX - 8f;
            DrawContentsStrip(new Rect(row.x + contentsColX, row.y, contentsW, RowH), slots);

            // Right block, in the order production cost -> mass/bulk.
            float statsX = row.width - statsColW;

            Dictionary<ThingDef, int> prodCosts = LoadoutUtility.GetLoadoutProductionCosts(pack);
            // Without amounts: 200px column, see DrawTotalCostLine. The numbers are in the cell's tooltip.
            LoadoutUtility.DrawTotalCostLine(statsX, row.y + (RowH - 26f) / 2f, MaterialsColW - 10f, prodCosts, "", labelW: 0f, showCounts: false);

            LoadoutUtility.GetLoadoutMassBulk(pack, out float mass, out float bulk);
            LoadoutUtility.DrawMassBulkBarsHorizontal(new Rect(statsX + MaterialsColW, row.y + (RowH - 22f) / 2f, MassBulkColW - 10f, 22f), mass, bulk);
        }

        // A compact row of small icons (up to 6, "+N" beyond that) standing in for a full item list -
        // hover shows the real listing. Keeps every pack to one row regardless of how many items it holds.
        private static void DrawContentsStrip(Rect r, List<LoadoutSlot> slots)
        {
            if (slots.Count == 0)
            {
                GUI.color = new Color(1f, 1f, 1f, 0.4f);
                Text.Anchor = TextAnchor.MiddleLeft;
                Widgets.Label(r, "CER_PickPacks_EmptyContents".Translate());
                Text.Anchor = TextAnchor.UpperLeft;
                GUI.color = Color.white;
                return;
            }

            const int maxIcons = 6;
            const float iconSize = 22f, gap = 3f;
            float x = r.x;
            float iconY = r.y + (RowH - iconSize) / 2f;
            int shown = Mathf.Min(maxIcons, slots.Count);
            for (int i = 0; i < shown; i++)
            {
                DrawSlotIcon(new Rect(x, iconY, iconSize, iconSize), slots[i]);
                x += iconSize + gap;
            }
            if (slots.Count > shown)
            {
                GUI.color = new Color(1f, 1f, 1f, 0.6f);
                Text.Anchor = TextAnchor.MiddleLeft;
                Widgets.Label(new Rect(x, r.y, r.xMax - x, RowH), "CER_PickPacks_MoreCount".Translate((slots.Count - shown).ToString()));
                Text.Anchor = TextAnchor.UpperLeft;
                GUI.color = Color.white;
            }

            string listing = string.Join("\n", slots.Select(s =>
                "CER_PickPacks_ContentsLine".Translate(
                    (s.thingDef != null ? (Def)s.thingDef : s.genericDef)?.LabelCap.ToString() ?? "CER_PickPacks_UnknownItemLabel".Translate().ToString(),
                    s.count.ToString())));
            TooltipHandler.TipRegion(r, listing);
        }

        private static void DrawSlotIcon(Rect r, LoadoutSlot slot)
        {
            if (slot.thingDef != null) { Widgets.ThingIcon(r, slot.thingDef); return; }
            if (slot.genericDef == null) return;
            Texture2D badge = GenericIcons.For(slot.genericDef);
            if (badge != null) GUI.DrawTexture(r, badge);
        }
    }
}
