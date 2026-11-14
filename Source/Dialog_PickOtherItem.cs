using System;
using System.Collections.Generic;
using System.Linq;
using RimWorld;
using Verse;
using UnityEngine;
using CombatExtended;

namespace CEReloaded
{
    // Minimal "add any item" picker - the escape hatch for one-off items that don't fit the structured
    // Ammo/Pack/Sidearm cells (a tool, a piece of jewelry, a specific resource stack, whatever). Writes
    // straight onto the pawn's personal loadout anchor (AmmoAssignmentUtility.EnsureAmmoLink) as a plain
    // LoadoutSlot - the SAME anchor Dialog_ChangeAmmo already reads/writes, not a parallel storage
    // mechanism. Replaces the old barebones loadout editor's pencil, which used to open from both compact
    // and card view; that editor (Dialog_LoadoutEditor) has since been removed entirely, so the
    // Ammo/Pack/Sidearm/Other cells are now the only way to edit an ad-hoc pawn's loadout.
    public class Dialog_PickOtherItem : Window
    {
        private readonly Pawn pawn;
        private readonly Loadout template;
        private readonly List<ThingDef> candidates;
        private string search = "";
        private Vector2 scroll;
        // Per-def batch-add quantity (2026-08-01: "+" only ever added 1 at a time) - keyed by
        // def so scrolling/re-searching doesn't lose what was typed for a def still in view.
        private readonly Dictionary<ThingDef, string> qtyBuffers = new Dictionary<ThingDef, string>();

        // Sortable columns (2026-08-01: the old inline "Owned: N" text had no header at all,
        // reading as meaningless noise - same toggle-direction-on-repeat-click convention as every other
        // sortable list in this mod (see Dialog_AssignLoadouts.SetSort).
        private enum SortMode { Name, Assigned }
        private SortMode currentSort = SortMode.Name;
        private bool sortDescending = false;
        private void SetSort(SortMode mode)
        {
            if (currentSort == mode) sortDescending = !sortDescending;
            else { currentSort = mode; sortDescending = false; }
        }

        private const float RowH = 28f;
        private const float HeaderH = 20f;
        // Shared by the header row and every data row so the two can never drift apart (same convention as
        // Dialog_AssignLoadouts' DrawCompactHeader/DrawPawnRowCompact).
        private const float AssignedW = 60f, QtyW = 50f, AddW = 36f, RemW = 36f, ColGap = 6f;
        // "-" sits LEFT of the quantity, "+" to its right - the quantity sits between its two buttons,
        // as in any quantity picker. Everything is still anchored to the right edge; only the name column
        // gives up the extra 42px (it truncates anyway).
        private static void ColumnRects(Rect row, out Rect nameRect, out Rect assignedRect, out Rect remRect, out Rect qtyRect, out Rect addRect)
        {
            addRect = new Rect(row.xMax - AddW, row.y, AddW, row.height);
            qtyRect = new Rect(addRect.x - ColGap - QtyW, row.y, QtyW, row.height);
            remRect = new Rect(qtyRect.x - ColGap - RemW, row.y, RemW, row.height);
            assignedRect = new Rect(remRect.x - ColGap - AssignedW, row.y, AssignedW, row.height);
            nameRect = new Rect(row.x + 32f, row.y, assignedRect.x - 4f - (row.x + 32f), row.height);
        }

        public override Vector2 InitialSize => new Vector2(600f, Mathf.Min(560f, UI.screenHeight - 60f));
        // Keep clear of RimWorld's learning readout - see LoadoutUtility.KeepClearOfLearningReadout.
        // Without an overlap this changes nothing.
        protected override void SetInitialSizeAndPosition()
        {
            base.SetInitialSizeAndPosition();
            windowRect = LoadoutUtility.KeepClearOfLearningReadout(windowRect);
        }

        public Dialog_PickOtherItem(Pawn pawn, Loadout template) : base(new ReadoutAwareWindowDrawing())
        {
            this.pawn = pawn;
            this.template = template;
            doCloseX = true;
            forcePause = true;
            absorbInputAroundWindow = true;
            candidates = BuildCandidatePool();
        }

        // Anything a pawn could plausibly carry as a one-off - never a weapon (Sidearm cell already covers
        // that) or an ammo def (Ammo cell), never a shield (2026-08 change made shields their own Weapon
        // Loadout, carried via the Sidearm cell instead), and never something that isn't a real haulable
        // Item-category thing (buildings, pawns, plants, minified frames, ...).
        // Built once per dialog instance, not every frame - the def database doesn't change mid-session.
        private static List<ThingDef> BuildCandidatePool()
        {
            return DefDatabase<ThingDef>.AllDefsListForReading.Where(d =>
                d.category == ThingCategory.Item
                && d.EverHaulable
                && !d.IsWeapon
                && !LoadoutUtility.IsAmmoDef(d)
                && !LoadoutUtility.IsShieldApparel(d)
                && !d.destroyOnDrop
                // ====== What a colonist never CARRIES as a loadout ======
                //
                // The list used to be unfiltered. The first thirteen entries read literally:
                // '90mm flak turret', 'Abyssinian cat corpse', 'Advanced component', 'Advanced
                // night vision goggles', 'Aesthetic nose', ... and 'corpse' in the search field filled a page.
                //
                // The checks above are all correct - to RimWorld, corpses and minified buildings simply are
                // haulable items of category Item. Three kinds are therefore excluded explicitly, because
                // they make no sense as a STANDING ORDER on a colonist:
                //   - corpses: a loadout says "always carry this with you".
                //   - minified buildings (the turret): same, and they weigh a ton.
                //   - unfinished things: they belong at the bench where they are made.
                //
                // DELIBERATELY NOT excluded, although they showed up in the same list: components, eggs,
                // fruit and body parts. They rarely make sense, but there are cases (the mechanic with
                // components in the backpack, emergency rations), and "rarely sensible" is no reason to
                // take the choice away from the player. The search field does the rest.
                && !d.IsCorpse
                && !d.IsFrame
                && !d.isUnfinishedThing
                && d.thingClass != typeof(MinifiedThing)
                && !typeof(MinifiedThing).IsAssignableFrom(d.thingClass)
            ).OrderBy(d => d.LabelCap.ToString()).ToList();
        }

        // Readiness cache invalidation (2026-08-01, same class of gap fixed elsewhere this pass for every
        // other anchor-mutating dialog) - an added item changes the pawn's carried mass/bulk, which feeds
        // the Overloaded/CapacityBlocked readiness checks.
        public override void PostClose()
        {
            base.PostClose();
            CombatReadiness.Invalidate();
        }

        public override void DoWindowContents(Rect inRect)
        {
            Text.Font = GameFont.Medium;
            Widgets.Label(new Rect(0, 0, inRect.width, 30f), "CER_PickOtherItem_Title".Translate());
            Text.Font = GameFont.Small;
            GUI.color = new Color(1f, 1f, 1f, 0.6f);
            Widgets.Label(new Rect(0, 30f, inRect.width, 34f), "CER_PickOtherItem_Subtitle".Translate(pawn.LabelShortCap.ToString()));
            GUI.color = Color.white;

            float y = 68f;
            Rect searchRect = new Rect(0, y, 260f, 26f);
            search = Widgets.TextField(searchRect, search);
            if (string.IsNullOrEmpty(search))
            {
                GUI.color = new Color(1f, 1f, 1f, 0.4f);
                Text.Anchor = TextAnchor.MiddleLeft;
                Widgets.Label(new Rect(searchRect.x + 6f, searchRect.y, searchRect.width - 12f, searchRect.height), "CER_PickOtherItem_SearchPlaceholder".Translate());
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

            List<ThingDef> shown = string.IsNullOrEmpty(search)
                ? candidates
                : candidates.Where(d => d.LabelCap.ToString().IndexOf(search, StringComparison.OrdinalIgnoreCase) >= 0).ToList();

            // Read once per frame, not once per row (2026-08-01, alongside the sort work below - sorting by
            // Assigned needs every row's count up front anyway, not just the handful currently scrolled into
            // view). Read-only lookup (GetAmmoLeaf, not EnsureAmmoLink) - just looking shouldn't create an
            // anchor that doesn't exist yet.
            Loadout existingAnchor = AmmoAssignmentUtility.GetAmmoLeaf(pawn);
            Dictionary<ThingDef, int> assignedCounts = existingAnchor?.OwnSlots
                ?.Where(s => s.thingDef != null)
                .ToDictionary(s => s.thingDef, s => s.count) ?? new Dictionary<ThingDef, int>();

            shown = currentSort == SortMode.Assigned
                ? (sortDescending
                    ? shown.OrderByDescending(d => assignedCounts.TryGetValue(d, out int c) ? c : 0).ToList()
                    : shown.OrderBy(d => assignedCounts.TryGetValue(d, out int c) ? c : 0).ToList())
                : (sortDescending
                    ? shown.OrderByDescending(d => d.LabelCap.ToString()).ToList()
                    : shown); // already alphabetical from BuildCandidatePool when ascending

            // Column headers - width matches the scrollview's own viewRect (outRect.width - 16f scrollbar
            // gutter) so every column lines up with the data rows below it, not with the wider outer rect.
            Rect headerRow = new Rect(0, y, inRect.width - 16f, HeaderH);
            ColumnRects(headerRow, out Rect hName, out Rect hAssigned, out _, out Rect hQty, out _);
            Text.Anchor = TextAnchor.MiddleLeft;
            if (LoadoutUtility.ColumnHeader(hName, "CER_PickOtherItem_NameHeader".Translate(), currentSort == SortMode.Name, sortDescending, true))
                SetSort(SortMode.Name);
            Text.Anchor = TextAnchor.MiddleRight;
            if (LoadoutUtility.ColumnHeader(hAssigned, "CER_PickOtherItem_AssignedHeader".Translate(), currentSort == SortMode.Assigned, sortDescending, true))
                SetSort(SortMode.Assigned);
            GUI.color = new Color(1f, 1f, 1f, 0.72f);
            Widgets.Label(hQty, "CER_PickOtherItem_QtyHeader".Translate());
            GUI.color = Color.white;
            Text.Anchor = TextAnchor.UpperLeft;
            y += HeaderH + 2f;

            Rect outRect = new Rect(0, y, inRect.width, inRect.height - y - 40f);
            DrawList(outRect, shown, assignedCounts);

            // Close and Copy share ONE font size - see LoadoutUtility.ButtonRowFont.
            Rect closeRect = new Rect(0, inRect.height - 34f, 120f, 30f);
            Rect copyRect = new Rect(128f, inRect.height - 34f, 120f, 30f);
            string closeLbl = "CER_PickOtherItem_CloseButton".Translate().ToString();
            string copyLbl = "CER_PickOtherItem_CopyButton".Translate().ToString();
            GameFont bottomFont = LoadoutUtility.ButtonRowFont((closeRect, closeLbl), (copyRect, copyLbl));
            bool RowBtn(Rect r, string lbl) { GameFont pf = Text.Font; Text.Font = bottomFont; bool c = LoadoutUtility.ButtonTextFit(r, lbl); Text.Font = pf; return c; }
            if (RowBtn(closeRect, closeLbl)) Close();

            // Copy as for packs and sidearms ("Add other item" had no Copy button and therefore no paste
            // either). What gets copied is what the colonist carries in other items RIGHT NOW - the same
            // set the Other cell shows; this window has no selection. An empty list is allowed, as for
            // packs: pasting it clears the target.
            // Pasting goes through the shared paste column in the Assign tab (DrawPasteColumn).
            if (RowBtn(copyRect, copyLbl))
            {
                List<LoadoutSlot> mine = existingAnchor?.OwnSlots
                    ?.Where(s => s.thingDef != null && !s.thingDef.IsWeapon && !LoadoutUtility.IsAmmoDef(s.thingDef))
                    .ToList() ?? new List<LoadoutSlot>();
                LoadoutClipboard.CopyOther(mine, pawn.LabelShortCap);
                Messages.Message((mine.Count > 0
                    ? "CER_PickOtherItem_CopiedMsg".Translate(mine.Count.ToString(), pawn.LabelShortCap.ToString())
                    : "CER_PickOtherItem_CopiedEmptyMsg".Translate(pawn.LabelShortCap.ToString())), MessageTypeDefOf.TaskCompletion, false);
            }
            TooltipHandler.TipRegion(copyRect, "CER_PickOtherItem_CopyTip".Translate());
        }

        private void DrawList(Rect outRect, List<ThingDef> shown, Dictionary<ThingDef, int> assignedCounts)
        {
            if (shown.Count == 0)
            {
                GUI.color = new Color(1f, 1f, 1f, 0.5f);
                Widgets.Label(outRect, "CER_PickOtherItem_NoMatches".Translate(search));
                GUI.color = Color.white;
                return;
            }

            Rect viewRect = new Rect(0, 0, outRect.width - 16f, shown.Count * RowH);
            Widgets.BeginScrollView(outRect, ref scroll, viewRect);
            // Viewport culling (2026-08-01, performance issue): the candidate pool can be
            // thousands of ThingDefs wide with a large modlist - drawing EVERY row unconditionally every
            // single frame (icon lookup + label + numeric field + button + tooltip registration, x
            // thousands) regardless of scroll position was the real ongoing cost, not the one-time pool
            // build in the constructor. Only rows whose Y range actually falls inside the visible scroll
            // window get drawn now.
            int firstVisible = Mathf.Max(0, Mathf.FloorToInt(scroll.y / RowH));
            int lastVisible = Mathf.Min(shown.Count - 1, Mathf.CeilToInt((scroll.y + outRect.height) / RowH));
            for (int i = firstVisible; i <= lastVisible; i++)
                DrawRow(new Rect(0, i * RowH, viewRect.width, RowH), shown[i], assignedCounts.TryGetValue(shown[i], out int c) ? c : 0);
            Widgets.EndScrollView();
        }

        private void DrawRow(Rect row, ThingDef def, int assigned)
        {
            if (Mouse.IsOver(row)) Widgets.DrawHighlight(row);

            Widgets.ThingIcon(new Rect(row.x + 2f, row.y + 2f, 24f, 24f), def);

            ColumnRects(row, out Rect nameRect, out Rect assignedRect, out Rect remRectFull, out Rect qtyRectFull, out Rect addRectFull);
            Rect addRect = new Rect(addRectFull.x, row.y + 3f, addRectFull.width, row.height - 6f);
            Rect qtyRect = new Rect(qtyRectFull.x, row.y + 3f, qtyRectFull.width, row.height - 6f);
            Rect remRect = new Rect(remRectFull.x, row.y + 3f, remRectFull.width, row.height - 6f);

            // Under the "Assigned" header now (2026-08-01: the old inline "Owned: N" text had
            // no column context at all) - bare number only, the header carries the meaning now.
            if (assigned > 0)
            {
                GUI.color = new Color(0.6f, 0.85f, 1f);
                Text.Anchor = TextAnchor.MiddleRight;
                Widgets.Label(assignedRect, assigned.ToString());
                Text.Anchor = TextAnchor.UpperLeft;
                GUI.color = Color.white;
            }

            Text.Anchor = TextAnchor.MiddleLeft;
            Widgets.Label(nameRect, def.LabelCap.Truncate(nameRect.width - 4f));
            Text.Anchor = TextAnchor.UpperLeft;

            // Batch quantity (2026-08-01: "+" only ever added 1 at a time, no way to add e.g.
            // 30 in a single click) - defaults to 1, remembered per-def in qtyBuffers.
            if (!qtyBuffers.TryGetValue(def, out string buf)) buf = "1";
            int.TryParse(buf, out int qty);
            if (qty < 1) qty = 1;
            Widgets.TextFieldNumeric(qtyRect, ref qty, ref buf, 1f, 9999f);
            qtyBuffers[def] = buf;

            // The way back. Previously ALL three removal paths led nowhere: this dialog could only add,
            // the editor that could remove has been deleted, and we intercept CE's own pencil for managed
            // colonists. A typo in the quantity could only be undone via "set pawn to a role" - and that
            // wipes everything, packs and sidearms included.
            // Removes the quantity entered in the field; if nothing is left afterwards, the whole entry
            // is dropped instead of staying as a zero row.
            Loadout leaf = AmmoAssignmentUtility.GetAmmoLeaf(pawn);
            LoadoutSlot ownSlot = leaf?.OwnSlots?.FirstOrDefault(s => s.thingDef == def);
            bool prevEnabled = GUI.enabled;
            GUI.enabled = ownSlot != null;
            if (LoadoutUtility.ButtonTextFit(remRect, "CER_PickOtherItem_RemoveButton".Translate().ToString()) && ownSlot != null)
            {
                int removed = Mathf.Min(qty, ownSlot.count);
                if (removed >= ownSlot.count) leaf.RemoveSlot(ownSlot);
                else ownSlot.count -= removed;
                Messages.Message("CER_PickOtherItem_RemovedMsg".Translate(removed.ToString(), def.LabelCap.ToString(), pawn.LabelShortCap.ToString()), MessageTypeDefOf.TaskCompletion, false);
            }
            GUI.enabled = prevEnabled;
            TooltipHandler.TipRegion(remRect, ownSlot != null
                ? "CER_PickOtherItem_RemoveTip".Translate(ownSlot.count.ToString())
                : "CER_PickOtherItem_RemoveNothingTip".Translate());

            if (LoadoutUtility.ButtonTextFit(addRect, "CER_PickOtherItem_AddButton".Translate().ToString()))
            {
                // Bump an existing own slot for this def rather than adding a second one (mirrors Dialog_
                // LoadoutEditor's own count-field editing for an existing own slot) - clicking "+" repeatedly
                // on the same item should stack the count, not spawn duplicate rows in the anchor's OwnSlots.
                Loadout anchor = AmmoAssignmentUtility.EnsureAmmoLink(pawn, template);
                LoadoutSlot existing = anchor.OwnSlots.FirstOrDefault(s => s.thingDef == def);
                if (existing != null) existing.count += qty;
                else anchor.AddSlot(new LoadoutSlot(def, qty));
                Messages.Message("CER_PickOtherItem_AddedMsg".Translate(qty.ToString(), def.LabelCap.ToString(), pawn.LabelShortCap.ToString()), MessageTypeDefOf.TaskCompletion, false);
            }
            TooltipHandler.TipRegion(row, def.LabelCap);
        }
    }
}
