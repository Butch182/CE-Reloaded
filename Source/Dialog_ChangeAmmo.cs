using System;
using System.Collections.Generic;
using System.Linq;
using RimWorld;
using Verse;
using UnityEngine;
using CombatExtended;

namespace CEReloaded
{
    // Per-pawn (or per-selection) ammo editor - lets a player see and fully REDEFINE what colonists
    // carry, independent of whatever their shared Weapon Loadout template bakes in by default. Checking/
    // unchecking always edits the pawn's OWN personal choice (AmmoAssignmentUtility's hidden per-pawn
    // anchor); the first edit for a pawn seeds that personal list from whatever is currently effectively
    // active (so "just remove this one type" or "just add one more" both read naturally instead of only
    // ever working relative to an empty list), and from then on the anchor's own ammo REPLACES the
    // template's inherited default entirely (see Patch_Loadout_MultiParentSlots) rather than piling
    // alongside it - editing ammo here never touches the shared template, so other pawns on the same
    // Weapon Loadout are unaffected. One unified checkbox table (checkbox, icon, Special effect icons,
    // count + stock) mirrors the Weapon Loadouts tab's own ammo table, reusing that tab's own icon-
    // drawing helper rather than re-implementing it. With multiple colonists selected, every one of them
    // gets their own section (portrait + their own table) - or, toggled to "Group by ammo type", one row
    // per ammo type shared across whichever of the selected colonists can use it, edited for all at once.
    public class Dialog_ChangeAmmo : Window
    {
        private readonly List<Pawn> pawns;
        private Vector2 scroll;
        private readonly Dictionary<string, string> countBuffers = new Dictionary<string, string>();
        private readonly Dictionary<Pawn, AmmoSnapshot> originalAmmo = new Dictionary<Pawn, AmmoSnapshot>();
        private bool groupByAmmoType = false;
        private string search = "";

        // Grouped view's own column sort - only meaningful there, since the per-pawn view has no shared header row.
        private enum GroupSortMode { Name, Count, Stock }
        private GroupSortMode groupSortMode = GroupSortMode.Name;
        private bool groupSortDescending = false;

        // 600f used to clip the bottom bar - Close/Clear/Reset/"Produce missing ammo..." add up to 614f wide
        // (2026-07-30: the button's own label was cut off at the window edge).
        // 800 wide since the three comparison bars (240) moved into the rows: at 700 the per-colonist rows
        // kept only 140px for the name after the window margins and the scrollbar, AmmoRoleCell's minimum
        // (150) dropped the bars entirely, and the grouped rows truncated "(AP-HE)"-style names. 800 leaves
        // ~240px for the name in both row kinds.
        public override Vector2 InitialSize =>
            new Vector2(Mathf.Min(800f, UI.screenWidth - 40f), Mathf.Min(pawns.Count > 1 ? 760f : 520f, UI.screenHeight - 60f));
        // Do not slide under RimWorld's learning readout - see LoadoutUtility.KeepClearOfLearningReadout.
        // Without a collision this changes nothing.
        protected override void SetInitialSizeAndPosition()
        {
            base.SetInitialSizeAndPosition();
            windowRect = LoadoutUtility.KeepClearOfLearningReadout(windowRect);
        }

        // Single-pawn convenience constructor - kept for the Assign Loadouts tab's own "Change ammo" cell.
        // The gizmo (one per selected pawn) always collapses the CURRENT selection into one call to the
        // list constructor below instead of stacking one dialog per pawn (see Patch_Pawn_ChangeAmmoGizmo).
        public Dialog_ChangeAmmo(Pawn pawn, Loadout template) : this(new List<Pawn> { pawn }) { }

        public Dialog_ChangeAmmo(List<Pawn> pawns) : base(new ReadoutAwareWindowDrawing())
        {
            this.pawns = pawns.Where(p => p != null).Distinct().ToList();
            doCloseX = true;
            forcePause = true;
            absorbInputAroundWindow = true;

            // Snapshot each pawn's personal ammo exactly as it stood before any edit in this window, so
            // "Reset" can revert to it later regardless of how many checkbox/count changes happened since.
            foreach (Pawn p in this.pawns)
            {
                Loadout leaf = AmmoAssignmentUtility.GetAmmoLeaf(p);
                var snap = new AmmoSnapshot { wasCleared = leaf != null && AmmoAssignmentUtility.IsAmmoCleared(leaf) };
                if (leaf != null)
                {
                    foreach (LoadoutSlot s in leaf.OwnSlots.Where(s => LoadoutUtility.IsAmmoDef(s.thingDef)))
                        snap.slots[s.thingDef] = s.count;
                    // "Any ammo" own slots (2026-08-01) - without this, Reset silently dropped a pawn's
                    // committed generic pick: it only ever removed/restored concrete slots, so a generic own
                    // slot survived the removal pass untouched but was never re-added either, leaving the
                    // pawn on whatever the LAST edit happened to be instead of back at the true original.
                    foreach (LoadoutSlot s in leaf.OwnSlots.Where(s => s.genericDef != null))
                        snap.genericSlots[s.genericDef] = s.count;
                }
                originalAmmo[p] = snap;
            }
        }

        // Compares each pawn's CURRENT personal ammo against the snapshot taken when this window opened
        // (2026-08-02: the automatic Supply popup should only ever open because of something
        // the player actually just did, not every time this dialog happens to close with some pre-existing
        // gap still sitting around). Reuses the exact same snapshot the Reset button already compares
        // against - no new data model, just a read instead of a revert.
        private bool AnyPawnAmmoChanged()
        {
            foreach (Pawn p in pawns)
            {
                if (!originalAmmo.TryGetValue(p, out AmmoSnapshot snap)) continue;
                Loadout leaf = AmmoAssignmentUtility.GetAmmoLeaf(p);
                bool nowCleared = leaf != null && AmmoAssignmentUtility.IsAmmoCleared(leaf);
                if (nowCleared != snap.wasCleared) return true;
                Dictionary<ThingDef, int> curSlots = new Dictionary<ThingDef, int>();
                Dictionary<LoadoutGenericDef, int> curGeneric = new Dictionary<LoadoutGenericDef, int>();
                if (leaf != null)
                {
                    foreach (LoadoutSlot s in leaf.OwnSlots.Where(s => LoadoutUtility.IsAmmoDef(s.thingDef)))
                        curSlots[s.thingDef] = s.count;
                    foreach (LoadoutSlot s in leaf.OwnSlots.Where(s => s.genericDef != null))
                        curGeneric[s.genericDef] = s.count;
                }
                if (curSlots.Count != snap.slots.Count || curGeneric.Count != snap.genericSlots.Count) return true;
                foreach (var kv in curSlots) if (!snap.slots.TryGetValue(kv.Key, out int c) || c != kv.Value) return true;
                foreach (var kv in curGeneric) if (!snap.genericSlots.TryGetValue(kv.Key, out int c) || c != kv.Value) return true;
            }
            return false;
        }

        public override void PostClose()
        {
            base.PostClose();
            // This dialog can be opened standalone from the map gizmo without Dialog_CEReloadedTabs ever
            // being open - it needs its own end-of-session production check, not just the tabs window's.
            // Only when something actually changed (2026-08-02, see AnyPawnAmmoChanged's own comment).
            // BUT only STANDALONE (otherwise the window popped up unasked about twelve times in a row, each time
            // to be dismissed with Cancel). If the tabs window is open, it has its own COLLECTED prompt when
            // leaving the Assign tab - exactly the one meant to replace the per-assignment prompt
            // (Dialog_CEReloadedTabs.CheckSupplyOnLeavingAssign: the prompt always comes at the end). As long as
            // this hook fired in addition, the per-assignment prompt was never really gone: change ammo once,
            // close the window, production dialog - in the middle of working in the same tab.
            if (AnyPawnAmmoChanged() && Find.WindowStack?.WindowOfType<Dialog_CEReloadedTabs>() == null)
                LoadoutUtility.CheckAndOpenMissingProductionPopup();
            // Readiness cache invalidation (2026-08-01: the readiness widget flagged a
            // pawn whose card then showed fully green right after an ammo edit here) - this dialog is a
            // common way a pawn's ammo-related readiness status actually changes, and it never told
            // CombatReadiness a real update just happened. Same "check when you're actually done, not per-
            // click" convention as the production check right above.
            CombatReadiness.Invalidate();
        }

        public override void DoWindowContents(Rect inRect)
        {
            Text.Font = GameFont.Medium;
            Widgets.Label(new Rect(0, 0, inRect.width - 200f, 30f), "CER_ChangeAmmo_Title".Translate());
            Text.Font = GameFont.Small;

            if (pawns.Count > 1)
            {
                Rect toggleRect = new Rect(inRect.width - 190f, 6f, 190f, 24f);
                bool newVal = groupByAmmoType;
                Widgets.CheckboxLabeled(toggleRect, "CER_ChangeAmmo_GroupByAmmoType".Translate(), ref newVal);
                if (newVal != groupByAmmoType) { groupByAmmoType = newVal; countBuffers.Clear(); }
                TooltipHandler.TipRegion(toggleRect,
                    "CER_ChangeAmmo_GroupByAmmoTypeTip".Translate());
            }

            GUI.color = new Color(1f, 1f, 1f, 0.6f);
            string sub = pawns.Count == 1
                ? "CER_ChangeAmmo_SubSingle".Translate(pawns[0].LabelShortCap.ToString())
                : "CER_ChangeAmmo_SubMulti".Translate(pawns.Count.ToString());
            Widgets.Label(new Rect(0, 30f, inRect.width, 42f), sub);
            GUI.color = Color.white;

            float y = 76f;
            Rect searchRect = new Rect(0, y, 240f, 26f);
            search = Widgets.TextField(searchRect, search);
            if (string.IsNullOrEmpty(search))
            {
                GUI.color = new Color(1f, 1f, 1f, 0.4f);
                Text.Anchor = TextAnchor.MiddleLeft;
                Widgets.Label(new Rect(searchRect.x + 6f, searchRect.y, searchRect.width - 12f, searchRect.height), "CER_ChangeAmmo_FilterPlaceholder".Translate());
                Text.Anchor = TextAnchor.UpperLeft;
                GUI.color = Color.white;
            }
            else if (LoadoutUtility.ClearSearchButton(searchRect, y, search))
            {
                search = "";
            }
            y += 32f;
            Widgets.DrawLineHorizontal(0, y, inRect.width);
            y += 6f;

            Rect outRect = new Rect(0, y, inRect.width, inRect.height - y - 44f);
            if (pawns.Count > 1 && groupByAmmoType) DoGroupedView(outRect);
            else DoPerPawnView(outRect);

            // The four footer buttons share ONE font size - see LoadoutUtility.ButtonRowFont. Hence the rects are
            // computed up front instead of per button at its own place.
            Rect closeRect = new Rect(0, inRect.height - 34f, 120f, 30f);
            Rect clearRect = new Rect(132f, inRect.height - 34f, 150f, 30f);
            Rect copyRect = new Rect(294f, inRect.height - 34f, 120f, 30f);
            Rect resetRect = new Rect(426f, inRect.height - 34f, 120f, 30f);
            string closeLbl = "CER_ChangeAmmo_Close".Translate().ToString();
            string clearLbl = "CER_ChangeAmmo_ClearAllAmmo".Translate().ToString();
            string copyLbl = "CER_ChangeAmmo_Copy".Translate().ToString();
            string resetLbl = "CER_ChangeAmmo_Reset".Translate().ToString();
            GameFont bottomFont = LoadoutUtility.ButtonRowFont((closeRect, closeLbl), (clearRect, clearLbl), (copyRect, copyLbl), (resetRect, resetLbl));
            bool RowBtn(Rect r, string lbl) { GameFont pf = Text.Font; Text.Font = bottomFont; bool c = LoadoutUtility.ButtonTextFit(r, lbl); Text.Font = pf; return c; }
            if (RowBtn(closeRect, closeLbl)) Close();

            if (RowBtn(clearRect, clearLbl))
            {
                foreach (Pawn p in pawns)
                {
                    Loadout tmpl = AmmoAssignmentUtility.DisplayLoadout(p.GetLoadout());
                    if (tmpl == null) continue;
                    Loadout link = AmmoAssignmentUtility.EnsureAmmoLink(p, tmpl);
                    // Remove generic ammo slots TOO: the filter only knew concrete ammo, an own entry
                    // "Any ammo for <weapon>" stayed behind. Visible consequence: press "remove all ammo", the row
                    // stays checked, and the colonist keeps fetching resupply - while SetAmmoCleared at the same time
                    // throws away the inherited ammo. Reset, Copy and Paste already handle both slot kinds; only this
                    // button did not.
                    // Via the central definition. Previously there was a separate test here that only counted a
                    // placeholder as ammo if its WEAPON was still in the DefDatabase - exactly the case
                    // LoadoutUtility.IsGenericAmmoDef deliberately answers differently: if the weapon mod disappears,
                    // the placeholder stays ammo and would have remained here forever although "remove all" was
                    // pressed.
                    foreach (LoadoutSlot s in link.OwnSlots.Where(LoadoutUtility.IsAmmoSlot).ToList())
                        link.RemoveSlot(s);
                    AmmoAssignmentUtility.SetAmmoCleared(link, true);
                }
                LoadoutUtility.InvalidateAmmoOwnership();
                countBuffers.Clear();
                // The displacement memory belongs to the current state - see anyAmmoDisplaced.
                anyAmmoDisplaced.Clear();
            }
            TooltipHandler.TipRegion(clearRect, pawns.Count == 1
                ? "CER_ChangeAmmo_ClearAllTipSingle".Translate(pawns[0].LabelShortCap.ToString())
                : "CER_ChangeAmmo_ClearAllTipMulti".Translate(pawns.Count.ToString()));

            // Copy button (ammo had no Copy button, the other columns did).
            // Copies the ammo choice of the colonist whose window is currently open; it is pasted via the same
            // single paste column in Assign Loadouts that also takes packs and sidearms. With several colonists
            // selected the FIRST one is copied - a clipboard holds one selection, and "the ammo of seven people
            // at once" would not be one.
            // What is copied is the union AS THE WINDOW SHOWS IT - no longer just the own anchor: a role
            // colonist's anchor is EMPTY by construction (AssignTemplate clears it on a role change), so Copy
            // reported "0 entries" there while two checked rows with amounts were visible. The old concern
            // (the template default travels to the target as a personal setting) is exactly what the player
            // expects when pasting: the target should get the same VISIBLE selection.
            if (RowBtn(copyRect, copyLbl))
            {
                Pawn src = pawns.FirstOrDefault();
                List<LoadoutSlot> ammoSlots = EffectiveAmmoSlotsAsShown(src);
                LoadoutClipboard.CopyAmmo(ammoSlots, src != null ? src.LabelShortCap.ToString() : "");
                // Singular and plural kept separate: the message used to read "Copied 1 ammo entr(ies)". The same
                // pattern CEReloadedMod already uses for the applied defaults - a parenthesized ending cannot be
                // formed cleanly in several of the 13 languages anyway.
                string srcName = src != null ? src.LabelShortCap.ToString() : "";
                Messages.Message(ammoSlots.Count == 1
                    ? "CER_ChangeAmmo_CopiedMessageSingular".Translate(ammoSlots.Count.ToString(), srcName)
                    : "CER_ChangeAmmo_CopiedMessagePlural".Translate(ammoSlots.Count.ToString(), srcName),
                    MessageTypeDefOf.TaskCompletion, false);
            }
            TooltipHandler.TipRegion(copyRect, "CER_ChangeAmmo_CopyTip".Translate());

            if (RowBtn(resetRect, resetLbl))
            {
                foreach (Pawn p in pawns)
                {
                    if (!originalAmmo.TryGetValue(p, out AmmoSnapshot snap)) continue;
                    Loadout leaf = AmmoAssignmentUtility.GetAmmoLeaf(p);
                    if (leaf == null)
                    {
                        if (snap.slots.Count == 0 && snap.genericSlots.Count == 0 && !snap.wasCleared) continue;
                        Loadout template = AmmoAssignmentUtility.DisplayLoadout(p.GetLoadout());
                        if (template == null) continue;
                        leaf = AmmoAssignmentUtility.EnsureAmmoLink(p, template);
                    }
                    foreach (LoadoutSlot s in leaf.OwnSlots.Where(s => LoadoutUtility.IsAmmoDef(s.thingDef)).ToList())
                        leaf.RemoveSlot(s);
                    foreach (LoadoutSlot s in leaf.OwnSlots.Where(s => s.genericDef != null).ToList())
                        leaf.RemoveSlot(s);
                    foreach (KeyValuePair<ThingDef, int> kv in snap.slots)
                        leaf.AddSlot(new LoadoutSlot(kv.Key, kv.Value));
                    foreach (KeyValuePair<LoadoutGenericDef, int> kv in snap.genericSlots)
                        leaf.AddSlot(new LoadoutSlot(kv.Key, kv.Value));
                    AmmoAssignmentUtility.SetAmmoCleared(leaf, snap.wasCleared);
                }
                LoadoutUtility.InvalidateAmmoOwnership();
                countBuffers.Clear();
                // The displacement memory belongs to the current state - see anyAmmoDisplaced.
                anyAmmoDisplaced.Clear();
            }
            TooltipHandler.TipRegion(resetRect,
                "CER_ChangeAmmo_ResetTip".Translate());

            // No "Produce missing ammo..." button here anymore (2026-07-30) - production is checked
            // automatically for the WHOLE colony (every assigned weapon/sidearm/ammo type, not just what's
            // touched in this one dialog) the moment the CE Reloaded tabs window closes - see
            // Dialog_CEReloadedTabs.OpenEndOfSessionProducePopupIfNeeded. A manual button was easy to forget;
            // the automatic check can't be, and Cancel in the review popup itself still covers the rare case
            // of assigning something you deliberately don't want produced yet.
        }

        // "What does this pawn actually carry right now" - physical inventory ammo, not the loadout's spec
        // (see the rows below, which show what the TEMPLATE wants). Truncates to maxShown types, appending
        // "+N" for the rest so a pawn carrying a dozen odd ammo types doesn't blow out the header.
        private static string BuildCarriedAmmoSummary(Pawn p, int maxShown = 3)
        {
            if (p?.inventory?.innerContainer == null) return null;
            var counts = p.inventory.innerContainer
                .Where(t => t.def != null && LoadoutUtility.IsAmmoDef(t.def))
                .GroupBy(t => t.def)
                .Select(g => (def: g.Key, count: g.Sum(t => t.stackCount)))
                .OrderByDescending(x => x.count)
                .ToList();
            if (counts.Count == 0) return null;
            var shown = counts.Take(maxShown).Select(x => "CER_ChangeAmmo_CarriedItem".Translate(x.count.ToString(), x.def.label.ToString()).ToString());
            string line = string.Join(", ", shown);
            if (counts.Count > maxShown) line += "CER_ChangeAmmo_CarriedMore".Translate((counts.Count - maxShown).ToString());
            return "CER_ChangeAmmo_CarriedPrefix".Translate(line).ToString();
        }

        // ---- Per-pawn view: one portrait + section header + rich ammo table per selected colonist ----
        private void DoPerPawnView(Rect outRect)
        {
            var sections = pawns.Select(p =>
            {
                List<LoadoutSlot> eff = p.GetLoadout()?.Slots?.ToList() ?? new List<LoadoutSlot>();
                return new PawnSection
                {
                    pawn = p,
                    template = AmmoAssignmentUtility.DisplayLoadout(p.GetLoadout()),
                    effective = eff,
                    pool = FilterBySearch(RelevantAmmoPool(eff)),
                    // Not search-filtered - this row represents a weapon-level meta-choice ("auto-pick"),
                    // not a specific ammo type, so it should stay visible regardless of an ammo-name search.
                    genericWeapons = RelevantGenericWeapons(eff)
                };
            }).ToList();

            const float rowH = 28f, headerH = 46f, sectionGap = 14f;
            float ContentH(PawnSection s) => headerH + Mathf.Max(s.pool.Count + s.genericWeapons.Count, 1) * rowH + sectionGap;
            float totalH = sections.Sum(ContentH);

            Rect viewRect = new Rect(0, 0, outRect.width - 16f, Mathf.Max(totalH, outRect.height));
            Widgets.BeginScrollView(outRect, ref scroll, viewRect);
            float y = 0f;
            foreach (PawnSection s in sections)
            {
                Rect header = new Rect(0, y, viewRect.width, headerH);
                if (s.template != null)
                    GUI.DrawTexture(new Rect(header.x, header.y + 1f, 28f, headerH - 2f), PortraitsCache.Get(s.pawn, new Vector2(28f, headerH - 2f), Rot4.South));
                GUI.color = new Color(0.85f, 0.9f, 1f);
                Widgets.Label(new Rect(header.x + 34f, header.y + 2f, viewRect.width - 34f, 22f),
                    s.template != null
                        ? "CER_ChangeAmmo_HeaderWithTemplate".Translate(s.pawn.LabelShortCap.ToString(), EquipmentPackUtility.StripMarker(s.template.label).ToString())
                        : "CER_ChangeAmmo_HeaderNoTemplate".Translate(s.pawn.LabelShortCap.ToString()));
                GUI.color = Color.white;
                // Physical carried-ammo summary (2026-07-31) - what this pawn
                // ACTUALLY has on them right now, distinct from the loadout spec rows below. Truncated with
                // "+N" past 3 types so a pawn hauling a dozen odd rounds doesn't blow out the header.
                // Column headers for the three comparison bars, on the second header line right above them -
                // the grouped view has a header row, this view had none, so the bars were unlabeled here. Same
                // cell as the rows below (AmmoRoleCell, icon edge 210); no cell means no bars, so no headers.
                Rect statCell = AmmoRoleCell(new Rect(0f, y, viewRect.width, rowH), 210f);
                float carriedW = (statCell.width > 0f ? statCell.x - 8f : viewRect.width) - (header.x + 34f);
                if (statCell.width > 0f) LoadoutUtility.DrawAmmoStatHeaders(statCell.x, header.y + 24f);
                string carried = BuildCarriedAmmoSummary(s.pawn);
                if (!string.IsNullOrEmpty(carried))
                {
                    Text.Font = GameFont.Tiny;
                    GUI.color = new Color(1f, 1f, 1f, 0.55f);
                    Widgets.Label(new Rect(header.x + 34f, header.y + 24f, carriedW, 20f), carried.Truncate(carriedW - 4f));
                    GUI.color = Color.white;
                    Text.Font = GameFont.Small;
                }
                y += headerH;

                if (s.template == null || (s.pool.Count == 0 && s.genericWeapons.Count == 0))
                {
                    GUI.color = new Color(1f, 1f, 1f, 0.5f);
                    string msg = s.template == null ? "CER_ChangeAmmo_NoLoadoutAssigned".Translate()
                        : !string.IsNullOrEmpty(search) ? "CER_ChangeAmmo_NoAmmoMatchesSearch".Translate(search)
                        : "CER_ChangeAmmo_WeaponsDontTakeAmmo".Translate(s.pawn.LabelShortCap.ToString());
                    Widgets.Label(new Rect(4f, y, viewRect.width - 8f, rowH), msg);
                    GUI.color = Color.white;
                    y += rowH;
                }
                else
                {
                    foreach (ThingDef weapon in s.genericWeapons)
                    {
                        DrawAnyAmmoRow(new Rect(0, y, viewRect.width, rowH), s.pawn, s.template, weapon, LoadoutUtility.GetGenericAmmoDef(weapon), s.effective);
                        // Refresh so any concrete rows for THIS pawn drawn right after (same event) don't
                        // render against the pre-click snapshot - a click here mutates the pawn's loadout
                        // synchronously, and s.effective was captured once at the top of this method.
                        s.effective = s.pawn.GetLoadout()?.Slots?.ToList() ?? new List<LoadoutSlot>();
                        y += rowH;
                    }
                    foreach (ThingDef ammoDef in s.pool)
                    {
                        DrawAmmoRow(new Rect(0, y, viewRect.width, rowH), s.pawn, s.template, ammoDef, s.effective);
                        y += rowH;
                    }
                }
                y += sectionGap;
            }
            Widgets.EndScrollView();
        }

        // Column header clicked: same mode toggles direction, a different mode resets to ascending.
        private void SetGroupSort(GroupSortMode mode)
        {
            if (groupSortMode == mode) groupSortDescending = !groupSortDescending;
            else { groupSortMode = mode; groupSortDescending = false; }
        }

        // ---- Grouped view: one row per ammo type relevant to ANY selected colonist, applied to all who can use it ----
        private void DoGroupedView(Rect outRect)
        {
            var perPawn = pawns.Select(p =>
            {
                List<LoadoutSlot> eff = p.GetLoadout()?.Slots?.ToList() ?? new List<LoadoutSlot>();
                return new PawnSection
                {
                    pawn = p,
                    template = AmmoAssignmentUtility.DisplayLoadout(p.GetLoadout()),
                    effective = eff,
                    pool = RelevantAmmoPool(eff),
                    // Same gap DoPerPawnView already closed (2026-07-31): a pawn still on their weapon's raw
                    // generic ammo placeholder has no concrete def to appear checked under here at all, so a
                    // multi-pawn selection mixing "auto-pick" and personalized colonists used to only ever
                    // show the personalized ones' concrete rows - toggling one of those rows ON then eagerly
                    // materialized (EnsurePersonalAmmo) a personal override for the STILL-generic pawns too,
                    // silently taking them off auto-pick as a side effect of a click that never mentioned them.
                    genericWeapons = RelevantGenericWeapons(eff)
                };
            }).Where(s => s.template != null).ToList();

            // Not search-filtered - same reasoning as DoPerPawnView's own genericWeapons: this represents a
            // weapon-level meta-choice, not a specific ammo type, so it stays visible regardless of search.
            List<ThingDef> allGenericWeapons = perPawn.SelectMany(s => s.genericWeapons).Distinct().OrderBy(w => w.label).ToList();
            List<ThingDef> allAmmo = FilterBySearch(perPawn.SelectMany(s => s.pool).Distinct().ToList());
            if (groupSortMode == GroupSortMode.Count)
                allAmmo = allAmmo.OrderBy(d =>
                {
                    var withVals = perPawn.Where(s => s.pool.Contains(d) && s.effective.Any(sl => sl.thingDef == d)).ToList();
                    return withVals.Count > 0 ? withVals[0].effective.First(sl => sl.thingDef == d).count : 0;
                }).ToList();
            else if (groupSortMode == GroupSortMode.Stock)
                allAmmo = allAmmo.OrderBy(d => LoadoutUtility.ComputeAmmoAvailability(d, out _, out _)).ToList();
            else
                allAmmo = allAmmo.OrderBy(d => d.label).ToList();
            if (groupSortDescending) allAmmo.Reverse();

            float contentW = outRect.width - 16f;
            // The name header ends before the three comparison bars, and they get their own headers - the same
            // calculation as AmmoRoleCell for the rows below (icon edge 190, gap 8).
            float statsX = outRect.x + contentW - 190f - 8f - LoadoutUtility.AmmoStatBarsW;
            if (LoadoutUtility.ColumnHeader(new Rect(outRect.x + 50f, outRect.y, statsX - 8f - (outRect.x + 50f), 22f), "CER_ChangeAmmo_ColName".Translate(),
                groupSortMode == GroupSortMode.Name, groupSortDescending, true)) SetGroupSort(GroupSortMode.Name);
            LoadoutUtility.DrawAmmoStatHeaders(statsX, outRect.y);
            LoadoutUtility.ColumnHeader(new Rect(outRect.x + contentW - 190f, outRect.y, 60f, 22f), "CER_ChangeAmmo_ColSpecial".Translate(), false, false, false);
            if (LoadoutUtility.ColumnHeader(new Rect(outRect.x + contentW - 122f, outRect.y, 46f, 22f), "CER_ChangeAmmo_ColCount".Translate(),
                groupSortMode == GroupSortMode.Count, groupSortDescending, true)) SetGroupSort(GroupSortMode.Count);
            if (LoadoutUtility.ColumnHeader(new Rect(outRect.x + contentW - 70f, outRect.y, 70f, 22f), "CER_ChangeAmmo_ColStock".Translate(),
                groupSortMode == GroupSortMode.Stock, groupSortDescending, true)) SetGroupSort(GroupSortMode.Stock);

            Rect scrollOut = new Rect(outRect.x, outRect.y + 24f, outRect.width, outRect.height - 24f);
            Rect viewRect = new Rect(0, 0, contentW, Mathf.Max((allGenericWeapons.Count + allAmmo.Count) * 28f + 4f, scrollOut.height));
            Widgets.BeginScrollView(scrollOut, ref scroll, viewRect);
            float ry = 4f;
            foreach (ThingDef weapon in allGenericWeapons)
            {
                DrawAnyAmmoRowGrouped(new Rect(0, ry, viewRect.width, 26f), perPawn, weapon, LoadoutUtility.GetGenericAmmoDef(weapon));
                ry += 28f;
            }
            foreach (ThingDef ammoDef in allAmmo)
            {
                var applicable = perPawn.Where(s => s.pool.Contains(ammoDef)).ToList();
                List<PawnAmmoState> withState = applicable.Select(s => new PawnAmmoState
                {
                    pawn = s.pawn,
                    template = s.template,
                    effective = s.effective,
                    hasAmmo = s.effective.Any(sl => sl.thingDef == ammoDef)
                }).ToList();
                int haveCount = withState.Count(w => w.hasAmmo);
                MultiCheckboxState state = haveCount == 0 ? MultiCheckboxState.Off
                    : (haveCount == applicable.Count ? MultiCheckboxState.On : MultiCheckboxState.Partial);

                Rect row = new Rect(0, ry, viewRect.width, 26f);
                bool researched = LoadoutUtility.IsAmmoResearched(ammoDef);
                GUI.color = researched ? Color.white : Color.gray;
                MultiCheckboxState newState = Widgets.CheckboxMulti(new Rect(row.x, row.y + 2f, 20f, 20f), state, true);
                if (newState != state)
                {
                    bool turnOn = newState == MultiCheckboxState.On;
                    foreach (PawnAmmoState w in withState)
                    {
                        EnsurePersonalAmmo(w.pawn, w.template, w.effective);
                        Loadout link = AmmoAssignmentUtility.GetAmmoLeaf(w.pawn);
                        LoadoutSlot ownExisting = link?.OwnSlots.FirstOrDefault(sl => sl.thingDef == ammoDef);
                        if (turnOn) { if (ownExisting == null) link.AddSlot(new LoadoutSlot(ammoDef, 40)); AmmoAssignmentUtility.SetAmmoCleared(link, false); RemoveConflictingGenericSlots(link, ammoDef); }
                        else if (ownExisting != null)
                        {
                            link.RemoveSlot(ownExisting);
                            // IsAmmoSlot: if an "Any ammo" is still there next to it, the colonist is NOT set to
                            // zero but has a choice - marking them "deliberately empty" here only takes effect later,
                            // when the generic checkbox goes too: then they would be left without any ammo instead
                            // of the template's default.
                            if (!link.OwnSlots.Any(LoadoutUtility.IsAmmoSlot)) AmmoAssignmentUtility.SetAmmoCleared(link, true);
                        }
                    }
                    LoadoutUtility.InvalidateAmmoOwnership(ammoDef);
                    // Slots just changed - re-resolve so the count field below reflects the new state this frame.
                    foreach (PawnAmmoState w in withState)
                    {
                        w.effective = w.pawn.GetLoadout()?.Slots?.ToList() ?? new List<LoadoutSlot>();
                        w.hasAmmo = w.effective.Any(sl => sl.thingDef == ammoDef);
                    }
                    state = newState;
                }

                Rect iconRect = new Rect(row.x + 26f, row.y + 2f, 20f, 20f);
                GUI.color = Color.white;
                Widgets.ThingIcon(iconRect, ammoDef);

                GUI.color = researched ? (state != MultiCheckboxState.Off ? Color.white : new Color(1f, 1f, 1f, 0.55f)) : Color.gray;
                // Take the space for the role bar away from the name column - see AmmoRoleCell below. Without
                // room the bar is dropped and the name gets everything.
                Rect roleCell1 = AmmoRoleCell(row, 190f);
                Rect labelRect = new Rect(row.x + 50f, row.y,
                    (roleCell1.width > 0f ? roleCell1.x - 8f : row.xMax - 246f) - (row.x + 50f), 24f);
                // The state suffix is appended AFTER truncating (third occurrence of the pattern): it ends in ")"
                // itself, so TruncateKeepingSuffix would have taken it for the ammo type and cut off the type of
                // all things - " (not researched)" would have survived, "(FMJ)" would not. So first truncate the
                // name to the remaining space, then append the suffix.
                string ammoSuffix = researched ? "" : "CER_ChangeAmmo_NotResearchedSuffix".Translate().ToString();
                string labelText = LoadoutUtility.TruncateKeepingSuffix(ammoDef.label,
                    labelRect.width - 4f - (ammoSuffix.Length > 0 ? Text.CalcSize(ammoSuffix).x : 0f)) + ammoSuffix;
                Widgets.Label(labelRect, labelText);
                GUI.color = Color.white;
                string names = string.Join(", ", applicable.Select(s => s.pawn.LabelShortCap));
                TooltipHandler.TipRegion(new Rect(iconRect.x, iconRect.y, labelRect.xMax - iconRect.xMin, 24f),
                    "CER_ChangeAmmo_AppliesToTip".Translate(LoadoutUtility.GetAmmoRecipeTooltip(ammoDef).ToString(), names));

                SpecialAbility ammoEff = LoadoutUtility.GetAmmoEffects(ammoDef);
                // The cap is the REAL gap up to the count field (row.xMax - 122f), not a number of its own: 150f
                // was larger than the space, and from four effects on the strip ran into the field. Computed
                // from the same two constants so the two do not drift apart again on the next shift.
                const float effIconX = 190f, effCountX = 122f;
                if (roleCell1.width > 0f) LoadoutUtility.DrawAmmoStatBars(roleCell1, ammoDef);
                if (ammoEff != SpecialAbility.None) Dialog_GenerateLoadouts.DrawAmmoEffectIcons(row.xMax - effIconX, row.y + 1f, ammoEff, ammoDef, effIconX - effCountX - 4f);

                if (state != MultiCheckboxState.Off)
                {
                    List<PawnAmmoState> withValues = withState.Where(w => w.hasAmmo).ToList();
                    int currentCount = withValues.Count > 0 ? withValues[0].effective.First(sl => sl.thingDef == ammoDef).count : 40;
                    string key = ammoDef.defName;
                    if (!countBuffers.TryGetValue(key, out string buf)) buf = currentCount.ToString();
                    int val = currentCount;
                    Rect cntRect = new Rect(row.xMax - 122f, row.y, 46f, 24f);
                    Widgets.TextFieldNumeric(cntRect, ref val, ref buf, 0f, 9999f);
                    countBuffers[key] = buf;
                    if (val != currentCount)
                    {
                        foreach (PawnAmmoState w in withValues)
                        {
                            EnsurePersonalAmmo(w.pawn, w.template, w.effective);
                            Loadout link = AmmoAssignmentUtility.GetAmmoLeaf(w.pawn);
                            LoadoutSlot ownSlot = link?.OwnSlots.FirstOrDefault(sl => sl.thingDef == ammoDef);
                            if (ownSlot != null) ownSlot.count = val;
                        }
                    }
                }

                int available = LoadoutUtility.ComputeAmmoAvailability(ammoDef, out int stock, out _);
                FulfillStatus ammoStatus = available > 0 ? FulfillStatus.Stored
                    : LoadoutUtility.WeaponHasActiveBill(ammoDef) ? FulfillStatus.InProduction
                    : FulfillStatus.Missing;
                Rect groupStatusDot = new Rect(row.xMax - 26f, row.y + 6f, 12f, 12f);
                Widgets.DrawBoxSolid(new Rect(groupStatusDot.x - 1f, groupStatusDot.y - 1f, groupStatusDot.width + 2f, groupStatusDot.height + 2f), new Color(0.08f, 0.08f, 0.08f, 0.95f));
                Widgets.DrawBoxSolid(groupStatusDot, LoadoutUtility.FulfillColor(ammoStatus));
                Rect groupStatusClick = new Rect(row.xMax - 34f, row.y, 34f, row.height);
                string groupStatusTip = "CER_ChangeAmmo_StatusTip".Translate(ammoDef.LabelCap.ToString(), LoadoutUtility.FulfillWord(ammoStatus).ToString(), stock.ToString(), available.ToString());
                if (ammoStatus == FulfillStatus.InProduction)
                {
                    Building_WorkTable bench = LoadoutUtility.FindActiveBillBench(ammoDef);
                    if (bench != null)
                    {
                        groupStatusTip += "CER_ChangeAmmo_ClickOpenBillsTab".Translate().ToString();
                        Widgets.DrawHighlightIfMouseover(groupStatusClick);
                        if (Widgets.ButtonInvisible(groupStatusClick)) LoadoutUtility.OpenBillsTab(bench);
                    }
                }
                else if (ammoStatus == FulfillStatus.Missing && researched)
                {
                    groupStatusTip += "CER_ChangeAmmo_ClickOpenSupplyPopup".Translate().ToString();
                    Widgets.DrawHighlightIfMouseover(groupStatusClick);
                    if (Widgets.ButtonInvisible(groupStatusClick))
                    {
                        int want = state != MultiCheckboxState.Off
                            ? (withState.FirstOrDefault(w => w.hasAmmo)?.effective.FirstOrDefault(sl => sl.thingDef == ammoDef)?.count ?? 40)
                            : 40;
                        Find.WindowStack.Add(new Dialog_SupplyLoadouts(new Dialog_SupplyLoadouts.Request
                        {
                            specificAmmo = new Dictionary<ThingDef, int> { { ammoDef, want } }
                        }));
                    }
                }
                // "!" on the stock dot when part of the counted stock is marked "forbidden" - the dot is this
                // row's stock display.
                int groupForbidden = LoadoutUtility.ForbiddenStockCount(ammoDef);
                LoadoutUtility.DrawForbiddenMarkAt(new Rect(groupStatusDot.x - 12f, groupStatusDot.y, 11f, 11f), groupForbidden, tooltip: false);
                if (groupForbidden > 0)
                    groupStatusTip += "\n" + "CER_LoadoutUtility_AvailForbidden".Translate(groupForbidden.ToString()).ToString();
                TooltipHandler.TipRegion(groupStatusClick, groupStatusTip);

                ry += 28f;
            }
            if (allAmmo.Count == 0 && allGenericWeapons.Count == 0)
            {
                GUI.color = new Color(1f, 1f, 1f, 0.5f);
                Widgets.Label(new Rect(4f, 4f, viewRect.width - 8f, 40f), !string.IsNullOrEmpty(search)
                    ? "CER_ChangeAmmo_NoAmmoMatchesSearch".Translate(search)
                    : "CER_ChangeAmmo_NoneWeaponsTakeAmmo".Translate());
                GUI.color = Color.white;
            }
            Widgets.EndScrollView();
        }

        // Grouped-view equivalent of DrawAnyAmmoRow - one row per weapon, applied to every SELECTED colonist
        // who actually owns it (not just one pawn). Checking it (fully or from Partial) commits every
        // not-yet-generic applicable colonist to the generic placeholder itself (same 2026-08-01 fix as the
        // per-pawn row - see its own comment for why just clearing overrides and hoping isn't enough).
        // Unchecking takes it away from all of them again (second occurrence of the same bug - exactly THIS row
        // was the one noticed). Previously every click that did not end up "on" was a no-op.
        private void DrawAnyAmmoRowGrouped(Rect row, List<PawnSection> perPawn, ThingDef weapon, LoadoutGenericDef generic)
        {
            List<PawnSection> applicable = perPawn.Where(s => s.effective.Any(sl => sl.thingDef == weapon)).ToList();
            if (applicable.Count == 0) return;
            int genericCount = applicable.Count(s => HasGenericSlot(s.effective, generic));
            MultiCheckboxState state = genericCount == 0 ? MultiCheckboxState.Off
                : (genericCount == applicable.Count ? MultiCheckboxState.On : MultiCheckboxState.Partial);

            MultiCheckboxState newState = Widgets.CheckboxMulti(new Rect(row.x, row.y + 2f, 20f, 20f), state, true);
            if (newState == MultiCheckboxState.On && generic != null)
            {
                List<ThingDef> family = LoadoutUtility.GetAmmoTypesFor(weapon).ToList();
                int count = int.TryParse(LoadoutUtility.GetDefaultAmmoCountForWeapon(weapon), out int c) && c > 0 ? c : 40;
                foreach (PawnSection s in applicable)
                {
                    if (HasGenericSlot(s.effective, generic)) continue;
                    EnsurePersonalAmmo(s.pawn, s.template, s.effective);
                    Loadout link = AmmoAssignmentUtility.GetAmmoLeaf(s.pawn);
                    if (link == null) continue;
                    // The same displacement as in the single view - the switch exists twice.
                    DisplaceOwnFamilyAmmo(link, s.pawn, weapon, family);
                    if (!link.OwnSlots.Any(sl => sl.genericDef == generic)) link.AddSlot(new LoadoutSlot(generic, count));
                    AmmoAssignmentUtility.SetAmmoCleared(link, true);
                    s.effective = s.pawn.GetLoadout()?.Slots?.ToList() ?? new List<LoadoutSlot>();
                }
                foreach (ThingDef fam in family) LoadoutUtility.InvalidateAmmoOwnership(fam);
            }
            else if (newState == MultiCheckboxState.Off && state != MultiCheckboxState.Off && generic != null)
            {
                List<ThingDef> family = LoadoutUtility.GetAmmoTypesFor(weapon).ToList();
                foreach (PawnSection s in applicable)
                {
                    if (!HasGenericSlot(s.effective, generic)) continue;
                    EnsurePersonalAmmo(s.pawn, s.template, s.effective);
                    Loadout link = AmmoAssignmentUtility.GetAmmoLeaf(s.pawn);
                    if (link == null) continue;
                    bool hadOwnGeneric = link.OwnSlots.Any(sl => sl.genericDef == generic);
                    foreach (LoadoutSlot own in link.OwnSlots.Where(sl => sl.genericDef == generic).ToList())
                        link.RemoveSlot(own);
                    RestoreDisplacedAmmo(link, s.pawn, weapon);
                    AmmoAssignmentUtility.SetAmmoCleared(link, ClearedAfterGenericOff(link, hadOwnGeneric));
                    s.effective = s.pawn.GetLoadout()?.Slots?.ToList() ?? new List<LoadoutSlot>();
                }
                foreach (ThingDef fam in family) LoadoutUtility.InvalidateAmmoOwnership(fam);
            }

            Rect iconRect = new Rect(row.x + 26f, row.y + 2f, 20f, 20f);
            GUI.color = Color.white;
            // Weapon icon WITH an ammo badge - see LoadoutUtility.DrawGenericAmmoIcon.
            LoadoutUtility.DrawGenericAmmoIcon(iconRect, weapon);
            Rect labelRect = new Rect(row.x + 50f, row.y, row.width - 246f - 50f, 24f);
            GUI.color = state == MultiCheckboxState.Off ? new Color(1f, 1f, 1f, 0.55f) : new Color(0.7f, 0.85f, 1f);
            string labelText = "CER_ChangeAmmo_AnyAmmoForWeapon".Translate(weapon.LabelCap.ToString());
            Widgets.Label(labelRect, labelText.Truncate(labelRect.width - 4f));
            GUI.color = Color.white;
            string names = string.Join(", ", applicable.Select(s => s.pawn.LabelShortCap));
            TooltipHandler.TipRegion(new Rect(iconRect.x, iconRect.y, labelRect.xMax - iconRect.xMin, 24f),
                "CER_ChangeAmmo_AnyAmmoGroupedTip".Translate(weapon.LabelCap.ToString(), names));
        }

        // Where does the "what is this good against" bar sit in THIS row (in the columns between ammo name and
        // damage type)? Exactly there: right of the name, left of the icon column, whose edge the row itself
        // knows (190 or 210 - the two row drawers historically have different edges, which is why it is a
        // parameter and not a third constant).
        //
        // If the name falls below MinNameW there is NO bar: a truncated ammo name costs more than a missing
        // piece of extra info - you could no longer tell which type you are checking. The bar is the extra,
        // the name is the thing itself.
        // Three comparison bars (damage / sharp / blunt, normalized to the caliber) instead of the single
        // interpretation bar - see LoadoutUtility.DrawAmmoStatBars.
        private static Rect AmmoRoleCell(Rect row, float effIconX)
        {
            const float MinNameW = 150f, Gap = 8f;
            float right = row.xMax - effIconX - Gap;              // left edge of the icon column, minus a gap
            float left = right - LoadoutUtility.AmmoStatBarsW;
            if (left - (row.x + 50f) < MinNameW) return new Rect(0f, 0f, 0f, 0f);
            return new Rect(left, row.y + 4f, LoadoutUtility.AmmoStatBarsW, 16f);
        }

        // Shared single-row drawer (checkbox, icon, name, special icons, count, stock) for one pawn + one
        // ammo def. "effective" is the pawn's CURRENT resolved ammo (their weapon loadout's own default,
        // or their personal override if they have one) - checked state always reflects reality, not just
        // "did I personally add this".
        private void DrawAmmoRow(Rect row, Pawn pawn, Loadout template, ThingDef ammoDef, List<LoadoutSlot> effective)
        {
            LoadoutSlot effectiveSlot = effective.FirstOrDefault(s => s.thingDef == ammoDef);
            bool hasAmmo = effectiveSlot != null;
            bool researched = LoadoutUtility.IsAmmoResearched(ammoDef);

            GUI.color = researched ? Color.white : Color.gray;
            bool check = hasAmmo;
            Widgets.Checkbox(new Vector2(row.x, row.y + 2f), ref check, 20f);
            if (check != hasAmmo)
            {
                EnsurePersonalAmmo(pawn, template, effective);
                Loadout link = AmmoAssignmentUtility.GetAmmoLeaf(pawn);
                LoadoutSlot ownExisting = link?.OwnSlots.FirstOrDefault(s => s.thingDef == ammoDef);
                if (check) { if (ownExisting == null) link.AddSlot(new LoadoutSlot(ammoDef, 40)); AmmoAssignmentUtility.SetAmmoCleared(link, false); RemoveConflictingGenericSlots(link, ammoDef); }
                else if (ownExisting != null)
                {
                    link.RemoveSlot(ownExisting);
                    // Unchecking the last remaining type would otherwise let the template's own default
                    // silently reappear (an anchor with zero ammo slots reads as "never customized" - see
                    // Patch_Loadout_MultiParentSlots) - mark it explicitly cleared instead.
                    // IsAmmoSlot - see the matching spot in the grouped view: a remaining "Any ammo" slot is a
                    // choice, not an empty state.
                    if (!link.OwnSlots.Any(LoadoutUtility.IsAmmoSlot)) AmmoAssignmentUtility.SetAmmoCleared(link, true);
                }
                LoadoutUtility.InvalidateAmmoOwnership(ammoDef);
            }

            Rect iconRect = new Rect(row.x + 26f, row.y + 2f, 20f, 20f);
            GUI.color = Color.white;
            Widgets.ThingIcon(iconRect, ammoDef);

            GUI.color = researched ? (hasAmmo ? Color.white : new Color(1f, 1f, 1f, 0.55f)) : Color.gray;
            // Twin of the grouped row further up - the same cell, just with THIS row's own icon column edge
            // (210 instead of 190). Both get it from AmmoRoleCell so they do not drift apart on the next shift.
            Rect roleCell2 = AmmoRoleCell(row, 210f);
            Rect labelRect = new Rect(row.x + 50f, row.y,
                (roleCell2.width > 0f ? roleCell2.x - 8f : row.xMax - 246f) - (row.x + 50f), 24f);
            // See the twin spot further up: append the suffix only after truncating.
            string ammoSuffix = researched ? "" : "CER_ChangeAmmo_NotResearchedSuffix".Translate().ToString();
            string labelText = LoadoutUtility.TruncateKeepingSuffix(ammoDef.label,
                labelRect.width - 4f - (ammoSuffix.Length > 0 ? Text.CalcSize(ammoSuffix).x : 0f)) + ammoSuffix;
            Widgets.Label(labelRect, labelText);
            GUI.color = Color.white;
            TooltipHandler.TipRegion(new Rect(iconRect.x, iconRect.y, labelRect.xMax - iconRect.xMin, 24f),
                () => LoadoutUtility.GetAmmoRecipeTooltip(ammoDef), ammoDef.GetHashCode() ^ 0x6A22);

            SpecialAbility ammoEff = LoadoutUtility.GetAmmoEffects(ammoDef);
            // Shifted 190 -> 210 (2026-08-01) to make room for the new 90px-wide Availability bar replacing
            // the old 12x12 status dot further right - see below.
            // Cap = real gap up to the count field (row.xMax - 146f), see the twin spot above.
            const float effIconX2 = 210f, effCountX2 = 146f;
            if (roleCell2.width > 0f) LoadoutUtility.DrawAmmoStatBars(roleCell2, ammoDef);
            if (ammoEff != SpecialAbility.None) Dialog_GenerateLoadouts.DrawAmmoEffectIcons(row.xMax - effIconX2, row.y + 1f, ammoEff, ammoDef, effIconX2 - effCountX2 - 4f);

            if (check)
            {
                string key = pawn.thingIDNumber + "_" + ammoDef.defName;
                int currentCount = effectiveSlot?.count ?? 40;
                if (!countBuffers.TryGetValue(key, out string buf)) buf = currentCount.ToString();
                int val = currentCount;
                // Shifted 122 -> 146 (2026-08-01), same reason as the effect icons above.
                Rect cntRect = new Rect(row.xMax - 146f, row.y, 46f, 24f);
                Widgets.TextFieldNumeric(cntRect, ref val, ref buf, 0f, 9999f);
                countBuffers[key] = buf;
                if (val != currentCount)
                {
                    EnsurePersonalAmmo(pawn, template, effective);
                    Loadout link = AmmoAssignmentUtility.GetAmmoLeaf(pawn);
                    LoadoutSlot ownSlot = link?.OwnSlots.FirstOrDefault(s => s.thingDef == ammoDef);
                    if (ownSlot != null) ownSlot.count = val;
                }
            }

            // Standard shared Availability bar (2026-08-01: this was the one spot in the mod
            // still using a plain colored square + a plain tooltip, predating the shared-bar convention -
            // no good reason found for the difference, so switched to match). Fed by the same GetAmmoOwnership
            // + pawn context Dialog_AssignLoadouts' own Ammo cell already uses, so "Equipped" correctly means
            // THIS pawn, same rich tooltip as every other Availability bar in the mod.
            LoadoutUtility.GetAmmoOwnership(ammoDef, out int carriedOnly, out int carriedAndAssigned, out int assignedInStore, out int freeStored, out int inProduction);
            var seg = LoadoutUtility.BuildAvailabilitySegments(carriedOnly, carriedAndAssigned, assignedInStore, freeStored, inProduction, pawn, ammoDef);
            Rect availBar = new Rect(row.xMax - 92f, row.y + 3f, 90f, 20f);

            // Click-to-fix (open the Supply popup for Missing, jump to Bills for InProduction) is unrelated
            // to the bar's own display - kept driven by the existing PawnAlreadyHasItem/ComputeAmmoAvailability/
            // WeaponHasActiveBill classification, just as an invisible button over the bar now instead of the
            // old dot, with its hint appended via the bar's own extraTip parameter.
            int available = LoadoutUtility.ComputeAmmoAvailability(ammoDef, out _, out _);
            FulfillStatus ammoStatus = LoadoutUtility.PawnAlreadyHasItem(pawn, ammoDef) ? FulfillStatus.Equipped
                : available > 0 ? FulfillStatus.Stored
                : LoadoutUtility.WeaponHasActiveBill(ammoDef) ? FulfillStatus.InProduction
                : FulfillStatus.Missing;
            Building_WorkTable prodBench = ammoStatus == FulfillStatus.InProduction ? LoadoutUtility.FindActiveBillBench(ammoDef) : null;
            string clickHint = ammoStatus == FulfillStatus.InProduction && prodBench != null ? "CER_ChangeAmmo_ClickOpenBillsTab".Translate().ToString()
                : ammoStatus == FulfillStatus.Missing && researched ? "CER_ChangeAmmo_ClickOpenSupplyPopup".Translate().ToString()
                : null;
            LoadoutUtility.DrawAvailabilityBar(availBar, seg, horizontal: true, clickHint);
            if (clickHint != null)
            {
                Widgets.DrawHighlightIfMouseover(availBar);
                if (Widgets.ButtonInvisible(availBar))
                {
                    if (prodBench != null) LoadoutUtility.OpenBillsTab(prodBench);
                    else
                    {
                        int want = effectiveSlot?.count ?? 40;
                        Find.WindowStack.Add(new Dialog_SupplyLoadouts(new Dialog_SupplyLoadouts.Request
                        {
                            specificAmmo = new Dictionary<ThingDef, int> { { ammoDef, want } }
                        }));
                    }
                }
            }
        }

        // First personal edit for a pawn: copy whatever ammo is CURRENTLY effectively active (their
        // Weapon Loadout's own default, most commonly) into their personal anchor before applying any
        // check/uncheck/count edit - so "remove just this one" and "add one more" both read naturally
        // instead of only ever working relative to an empty personal list. Idempotent - a pawn who
        // already has any personal ammo of their own is left untouched. Was made internal so the barebones
        // Dialog_LoadoutEditor's ammo-row remove button could reuse the exact same seeding logic instead of
        // keeping a second copy; that dialog has since been removed, so every caller now lives in this file.
        internal static void EnsurePersonalAmmo(Pawn pawn, Loadout template, List<LoadoutSlot> effective)
        {
            Loadout link = AmmoAssignmentUtility.EnsureAmmoLink(pawn, template);
            // The early-out counts generic own slots too (third occurrence of the same question): a colonist
            // who is ONLY on "Any ammo" has already made a choice of their own. Previously they counted as
            // untouched here, and the next click additionally copied the template's concrete default ammo into
            // the anchor - exactly the double state "Any ammo AND three concrete types".
            // Still only CONCRETE ammo is copied: an inherited generic placeholder is not a choice that needs
            // materializing, but the very thing being moved away from.
            if (link.OwnSlots.Any(LoadoutUtility.IsAmmoSlot)) return;
            foreach (LoadoutSlot s in effective.Where(s => LoadoutUtility.IsAmmoDef(s.thingDef)).ToList())
                link.AddSlot(new LoadoutSlot(s.thingDef, s.count));
        }

        private List<ThingDef> FilterBySearch(List<ThingDef> pool) =>
            string.IsNullOrEmpty(search) ? pool : pool.Where(d => d.label.IndexOf(search, StringComparison.OrdinalIgnoreCase) >= 0).ToList();

        private class PawnSection
        {
            public Pawn pawn;
            public Loadout template;
            public List<ThingDef> pool;
            public List<LoadoutSlot> effective;
            public List<ThingDef> genericWeapons;
        }

        private class PawnAmmoState
        {
            public Pawn pawn;
            public Loadout template;
            public List<LoadoutSlot> effective;
            public bool hasAmmo;
        }

        private class AmmoSnapshot
        {
            public Dictionary<ThingDef, int> slots = new Dictionary<ThingDef, int>();
            public Dictionary<LoadoutGenericDef, int> genericSlots = new Dictionary<LoadoutGenericDef, int>();
            public bool wasCleared;
        }

        // Only ammo that actually fits a weapon this pawn effectively carries (main weapon or a sidearm,
        // wherever that sidearm comes from - "effective" is the pawn's own resolved .Slots, so an attached
        // sidearm shows up here too, not just the main weapon) - not every ammo type in the game.
        // Shared with Dialog_AssignLoadouts' own compact "Ammo: X" cell summary, so the two never drift -
        // that cell used to independently ask "does the pawn have PERSONAL extra ammo" (ExtraAmmoSlots),
        // which said "None" for every pawn who never touched this popup even when their weapon loadout
        // already carried a perfectly real ammo type. Both should agree on what "relevant ammo" even means.
        // Exactly the own ammo slots THIS WINDOW shows the player - one question, one answer (Copy reported
        // three entries while the window showed two).
        // The Copy button used to count every own slot of the anchor that looked like ammo in any way
        // ("IsAmmoDef(thingDef) || genericDef != null"), while the rows above come from
        // RelevantAmmoPool/RelevantGenericWeapons - i.e. only from the weapons the colonist REALLY carries. A
        // leftover slot of a long-detached sidearm was therefore counted and even copied, although nobody could
        // see or use it (Paste discards it at the other end anyway, see Dialog_AssignLoadouts.PasteAmmo).
        // Also via IsGenericAmmoDef instead of "genericDef != null": a generic SUPPLY entry (e.g. "Any
        // Medicine") is not ammo and does not belong in the ammo clipboard.
        // Taken from the UNION (GetLoadout().Slots) instead of leaf.OwnSlots: a role colonist's anchor is empty
        // by construction (AmmoAssignmentUtility.AssignTemplate clears OwnSlots on a role change), but the
        // window's rows come from the union - so Copy saw 0 where the window showed 2 checked types. The first
        // occurrence per type wins, because the window's count fields read from the same list with
        // FirstOrDefault.
        private static List<LoadoutSlot> EffectiveAmmoSlotsAsShown(Pawn src)
        {
            if (src == null) return new List<LoadoutSlot>();
            List<LoadoutSlot> effective = src.GetLoadout()?.Slots?.ToList() ?? new List<LoadoutSlot>();
            HashSet<ThingDef> shownConcrete = new HashSet<ThingDef>(RelevantAmmoPool(effective));
            HashSet<ThingDef> shownGenericWeapons = new HashSet<ThingDef>(RelevantGenericWeapons(effective));
            var result = new List<LoadoutSlot>();
            var seenConcrete = new HashSet<ThingDef>();
            // Do NOT dedupe generic slots by def (CE generics all have hash 0, see DefRefComparer) - the key is
            // the weapon the generic belongs to.
            var seenGenericWeapons = new HashSet<ThingDef>();
            foreach (LoadoutSlot s in effective)
            {
                if (LoadoutUtility.IsAmmoDef(s.thingDef))
                {
                    if (shownConcrete.Contains(s.thingDef) && seenConcrete.Add(s.thingDef)) result.Add(s);
                    continue;
                }
                if (!LoadoutUtility.IsGenericAmmoDef(s.genericDef)) continue;
                ThingDef forWeapon = LoadoutUtility.GetWeaponForGenericAmmo(s.genericDef);
                if (forWeapon != null && shownGenericWeapons.Contains(forWeapon) && seenGenericWeapons.Add(forWeapon)) result.Add(s);
            }
            return result;
        }

        internal static List<ThingDef> RelevantAmmoPool(List<LoadoutSlot> effective)
        {
            var result = new List<ThingDef>();
            var seen = new HashSet<ThingDef>();
            foreach (LoadoutSlot s in effective)
            {
                if (s.thingDef == null || !s.thingDef.IsWeapon || Dialog_GenerateLoadouts.IsGrenade(s.thingDef)) continue;
                foreach (Def ammo in LoadoutUtility.GetLoadoutAmmoDefs(s.thingDef, false))
                    if (ammo is ThingDef atd && seen.Add(atd)) result.Add(atd);
            }
            return result;
        }

        // Weapons in `effective` still on their template's raw generic ammo placeholder ("Any 5.56mm Ammo")
        // rather than a personalized concrete pick (2026-08-01 fix). Every OTHER ammo-aware function in the
        // mod (Supply popup, production checks) already resolves a still-generic slot to its concrete
        // default and treats the pawn as carrying/needing it - this dialog's own checkbox-per-concrete-def
        // table had no way to show that at all (the slot's own thingDef is genuinely null), so every row
        // rendered unchecked for a never-personalized pawn even though they were actually fine. Surfaced as
        // its own explicit, always-checkable row instead rather than guessing which
        // existing concrete row should silently read as checked.
        internal static List<ThingDef> RelevantGenericWeapons(List<LoadoutSlot> effective)
        {
            var result = new List<ThingDef>();
            foreach (LoadoutSlot s in effective)
            {
                if (s.thingDef == null || !s.thingDef.IsWeapon || Dialog_GenerateLoadouts.IsGrenade(s.thingDef)) continue;
                if (LoadoutUtility.GetGenericAmmoDef(s.thingDef) != null) result.Add(s.thingDef);
            }
            return result;
        }

        // "Does this anchor have its own ammo?" - concrete AND generic slots.
        // Patch_Loadout_MultiParentSlots asks the same question, and both must answer it the same way,
        // otherwise one throws the inherited ammo away while the other brings it back.
        // Via LoadoutUtility.IsAmmoSlot - the same question as in Patch_Loadout_MultiParentSlots, now really
        // with the same answer too. As long as the two had their own versions, drifting apart was only a
        // matter of time, and that is exactly what happened.
        private static bool HasOwnAmmoSlot(Loadout link)
            => link?.OwnSlots != null && link.OwnSlots.Any(LoadoutUtility.IsAmmoSlot);

        // What does "unchecking Any ammo" mean? That depends on WHERE the check came from - and exactly this
        // distinction was missing (after unchecking BOTH generic rows the colonist was left without any ammo
        // instead of falling back to the template's default).
        //   - The colonist had set the generic entry THEMSELVES -> unchecking means "back to the template".
        //     'cleared' must then be FALSE, otherwise the anchor suppresses everything inherited and the
        //     colonist is left empty-handed.
        //   - The generic entry came from the TEMPLATE -> unchecking means "I do not want this ammo".
        //     Since there was nothing of its own to remove, only the explicit 'cleared' keeps it out.
        // If any other own ammo slot remains next to it, nothing is cleared anyway.
        // An earlier fix only handled the third case and lumped the first two together.
        private static bool ClearedAfterGenericOff(Loadout link, bool hadOwnGeneric)
            => !HasOwnAmmoSlot(link) && !hadOwnGeneric;

        // What switching on "Any ammo" DISPLACED - per colonist and weapon, as long as this window is open.
        //
        // Failure: "Any ammo" did switch off, but then re-checked other types. In detail: only Ion 150 checked
        // before, Any ammo ON -> all concrete off, Any ammo OFF -> suddenly Charged 120 AND Ion 150.
        // Worked through, not guessed: switching on DELETES the weapon's own concrete slots (the "RemoveSlot"
        // line in the on branch), and switching off cannot bring them back. Afterwards the anchor has no own
        // ammo at all, so it counts as "never touched" - and the TEMPLATE's default comes through. That is
        // exactly what looked like "checks other types".
        //
        // A switch that throws data away when switched on cannot be switched off. So the window remembers what
        // it displaced and puts it back when switching off.
        // This does NOT replace the fallback to the template: if the colonist had nothing of their own before
        // (so they were on the template themselves), there is nothing to put back and the old behavior stays -
        // exactly the case where they were left without any ammo.
        // Deliberate limit: the memory only lives as long as this window. Anyone who switches Any ammo on,
        // closes the window and switches it off later ends up with the fallback to the template - storing it
        // permanently would mean extending the save with an undo buffer.
        private readonly Dictionary<string, List<LoadoutSlot>> anyAmmoDisplaced = new Dictionary<string, List<LoadoutSlot>>();

        private static string DisplacedKey(Pawn pawn, ThingDef weapon) =>
            (pawn != null ? pawn.thingIDNumber : 0) + "|" + (weapon != null ? weapon.defName : "?");

        // Removes this weapon family's own concrete slots and remembers them. As COPIES, because the originals
        // are about to be removed from the loadout.
        private void DisplaceOwnFamilyAmmo(Loadout link, Pawn pawn, ThingDef weapon, List<ThingDef> family)
        {
            if (link?.OwnSlots == null) return;
            List<LoadoutSlot> hit = link.OwnSlots.Where(s => family.Contains(s.thingDef)).ToList();
            anyAmmoDisplaced[DisplacedKey(pawn, weapon)] = hit.Select(s => new LoadoutSlot(s.thingDef, s.count)).ToList();
            foreach (LoadoutSlot own in hit) link.RemoveSlot(own);
        }

        // Puts back what switching on took away. False if there was nothing to put back - then the fallback to
        // the template still applies.
        private bool RestoreDisplacedAmmo(Loadout link, Pawn pawn, ThingDef weapon)
        {
            string key = DisplacedKey(pawn, weapon);
            if (link == null || !anyAmmoDisplaced.TryGetValue(key, out List<LoadoutSlot> back)) return false;
            anyAmmoDisplaced.Remove(key);
            if (back == null || back.Count == 0) return false;
            foreach (LoadoutSlot s in back)
                if (!link.OwnSlots.Any(o => o.thingDef == s.thingDef))
                    link.AddSlot(new LoadoutSlot(s.thingDef, s.count));
            return true;
        }

        private static bool HasGenericSlot(List<LoadoutSlot> effective, LoadoutGenericDef generic) =>
            generic != null && effective.Any(s => s.genericDef == generic);

        // Picking a concrete ammo type un-commits the pawn from "Any ammo" for that same weapon family -
        // the generic pick and a concrete pick are meant to be mutually exclusive (see DrawAnyAmmoRow).
        // Without this, a pawn who'd already committed to the generic placeholder for a weapon and then
        // checked one specific type back in would show BOTH the generic row and that concrete row checked.
        private static void RemoveConflictingGenericSlots(Loadout link, ThingDef ammoDef)
        {
            if (link == null || ammoDef == null) return;
            foreach (LoadoutSlot g in link.OwnSlots.Where(s => s.genericDef != null
                && LoadoutUtility.GetAmmoTypesFor(LoadoutUtility.GetWeaponForGenericAmmo(s.genericDef)).Contains(ammoDef)).ToList())
                link.RemoveSlot(g);
        }

        // Checkbox-row stand-in for "this pawn's own choice for this weapon is the generic any-ammo
        // placeholder" - checked while `effective` carries a slot literally tagged with `generic`.
        // Clicking it while unchecked commits to that placeholder EXPLICITLY (2026-08-01 fix): earlier this
        // only cleared the personal override and hoped the effective union would then surface a generic
        // slot on its own, but a freshly generated template's own default is a CONCRETE pick, not a generic
        // one (see PickDefaultAmmo - "FMJ" is the default setting, not "Generic"), so there was usually
        // nothing generic to inherit and the checkbox never actually lit up (pawn silently ended up with
        // whatever the template's own single concrete pick was, or nothing at all if it had none). Now
        // seeds the anchor the same way every other row does (EnsurePersonalAmmo), clears this weapon's own
        // concrete overrides, then adds the generic def itself as an own slot - and marks the anchor
        // explicitly cleared (Patch_Loadout_MultiParentSlots' own-ammo filter only counts CONCRETE own
        // slots for the "own replaces inherited" gate, so an own slot that's generic-only wouldn't
        // otherwise suppress the template's inherited concrete default from leaking back in alongside it).
        // The check now works in BOTH directions.
        // Previously this said "clicking while checked is a no-op - there is no own off state, the row only
        // unchecks itself as a side effect when you pick a concrete type below". That was meant as intent and
        // came across as a bug: a checkbox that cannot be switched off again is exactly the "I click and nothing
        // happens" that had been noticed here several times already - and it blocked the counter-check, because
        // the generic entry no longer went away. Off means the same as in the row below (DrawAmmoRow): remove
        // the own slot and, if no own ammo remains afterwards, explicitly mark it "cleared" so the template
        // default does not silently come back.
        private void DrawAnyAmmoRow(Rect row, Pawn pawn, Loadout template, ThingDef weapon, LoadoutGenericDef generic, List<LoadoutSlot> effective)
        {
            bool isGeneric = HasGenericSlot(effective, generic);
            bool check = isGeneric;
            Widgets.Checkbox(new Vector2(row.x, row.y + 2f), ref check, 20f);
            if (!check && isGeneric && generic != null)
            {
                // EnsurePersonalAmmo here too: the generic slot may be INHERITED (from the template), then there is
                // nothing to remove and only the explicit "cleared" on the own anchor keeps it out - the same
                // mechanism as when checking a row further down.
                EnsurePersonalAmmo(pawn, template, effective);
                Loadout link = AmmoAssignmentUtility.GetAmmoLeaf(pawn);
                if (link != null)
                {
                    // Did the colonist set this generic entry THEMSELVES, or did it come from the template? What
                    // unchecking means depends on that - record it before removing.
                    bool hadOwnGeneric = link.OwnSlots.Any(s => s.genericDef == generic);
                    foreach (LoadoutSlot own in link.OwnSlots.Where(s => s.genericDef == generic).ToList())
                        link.RemoveSlot(own);
                    // BEFORE the cleared calculation: the restored slots are own ammo, so the anchor rightly does
                    // not count as cleared afterwards.
                    RestoreDisplacedAmmo(link, pawn, weapon);
                    AmmoAssignmentUtility.SetAmmoCleared(link, ClearedAfterGenericOff(link, hadOwnGeneric));
                }
                foreach (ThingDef fam in LoadoutUtility.GetAmmoTypesFor(weapon)) LoadoutUtility.InvalidateAmmoOwnership(fam);
            }
            else if (check && !isGeneric && generic != null)
            {
                EnsurePersonalAmmo(pawn, template, effective);
                Loadout link = AmmoAssignmentUtility.GetAmmoLeaf(pawn);
                if (link != null)
                {
                    List<ThingDef> family = LoadoutUtility.GetAmmoTypesFor(weapon).ToList();
                    // Displace instead of delete - see anyAmmoDisplaced.
                    DisplaceOwnFamilyAmmo(link, pawn, weapon, family);
                    if (!link.OwnSlots.Any(s => s.genericDef == generic))
                    {
                        int count = int.TryParse(LoadoutUtility.GetDefaultAmmoCountForWeapon(weapon), out int c) && c > 0 ? c : 40;
                        link.AddSlot(new LoadoutSlot(generic, count));
                    }
                    AmmoAssignmentUtility.SetAmmoCleared(link, true);
                }
            }

            Rect iconRect = new Rect(row.x + 26f, row.y + 2f, 20f, 20f);
            GUI.color = Color.white;
            // Weapon icon WITH an ammo badge - see LoadoutUtility.DrawGenericAmmoIcon.
            LoadoutUtility.DrawGenericAmmoIcon(iconRect, weapon);
            Rect labelRect = new Rect(row.x + 50f, row.y, row.width - 246f - 50f, 24f);
            GUI.color = isGeneric ? new Color(0.7f, 0.85f, 1f) : new Color(1f, 1f, 1f, 0.55f);
            string labelText = "CER_ChangeAmmo_AnyAmmoForWeapon".Translate(weapon.LabelCap.ToString());
            Widgets.Label(labelRect, labelText.Truncate(labelRect.width - 4f));
            GUI.color = Color.white;
            TooltipHandler.TipRegion(new Rect(iconRect.x, iconRect.y, labelRect.xMax - iconRect.xMin, 24f),
                "CER_ChangeAmmo_AnyAmmoRowTip".Translate(weapon.LabelCap.ToString()));
        }
    }
}
