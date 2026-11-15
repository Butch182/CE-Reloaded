using System;
using System.Collections.Generic;
using System.Linq;
using RimWorld;
using UnityEngine;
using Verse;
using HarmonyLib;
using CombatExtended;

namespace CEReloaded
{
    // Companion "Loadout" panel docked to the right of the Gear tab (CE's inventory ITab): the assigned loadout
    // with per-slot fulfilment - carried, waiting in storage, in production, or genuinely missing - plus jump
    // buttons to the CE loadout editor and the CE Reloaded workflow tabs. Driven from a FillTab postfix via
    // ImmediateWindow, so its lifetime is exactly "while the gear tab is visible": nothing to clean up, and it
    // keeps working however other mods redecorate the tab contents themselves.
    [HarmonyPatch]
    public static class Patch_GearTabLoadoutPanel
    {
        static IEnumerable<System.Reflection.MethodBase> TargetMethods()
        {
            var targets = new List<System.Reflection.MethodBase>();
            var ce = AccessTools.Method(typeof(ITab_Inventory), "FillTab");
            var vanilla = AccessTools.Method(typeof(ITab_Pawn_Gear), "FillTab");
            if (ce != null) targets.Add(ce);
            if (vanilla != null && vanilla != ce) targets.Add(vanilla);
            return targets;
        }

        private static int lastFrame = -1;

        public static void Postfix()
        {
            // ONLY in Repaint (the panel was registered every frame but never drawn).
            // WindowStack.ImmediateWindow bails out
            // in its first line for every other event type, so it registers nothing. Unity sends
            // the Layout event FIRST each frame and Repaint only afterwards, both with the same
            // Time.frameCount: the guard below consumed the frame in the Layout pass (where
            // registration has no effect) and never let the Repaint pass through. The panel was
            // therefore never registered even ONCE - the tab itself is drawn by RimWorld in exactly
            // such an ImmediateWindow, so we rely on the same mechanism.
            if (Event.current == null || Event.current.type != EventType.Repaint) return;
            if (Time.frameCount == lastFrame) return;   // CE's FillTab may chain through the vanilla one - one panel per frame
            Pawn pawn = Find.Selector.SingleSelectedThing as Pawn;
            if (pawn == null || !pawn.IsColonist || pawn.Map == null) return;   // corpses/enemies have no loadout workflow
            Window win = Find.WindowStack.currentlyDrawnWindow;
            if (win == null) return;
            lastFrame = Time.frameCount;

            // Dock to the tab window's right edge; fall back to the left when the screen ends there.
            Rect tab = win.windowRect;
            const float panelW = 340f;
            float x = tab.xMax - 1f;
            if (x + panelW > UI.screenWidth) x = tab.x - panelW + 1f;
            // KEEP IT ON SCREEN (the gear tab panel was missing): this panel hangs
            // NEXT TO the tab, not inside it - and since RimWorld 1.6 the tab's width is no longer
            // decided by RimWorld alone. If another mod replaces the gear tab with a much wider one (for
            // example 'Nice Inventory Tab CE Integration'), both candidates - to the right and
            // to the left - end up off screen, and the panel exists but is invisible. Clamped, it shows
            // at worst half over the tab instead of not at all.
            x = Mathf.Clamp(x, 0f, Mathf.Max(0f, UI.screenWidth - panelW));
            float y = Mathf.Clamp(tab.y, 0f, Mathf.Max(0f, UI.screenHeight - Mathf.Min(tab.height, UI.screenHeight)));
            Rect panelRect = new Rect(x, y, panelW, Mathf.Min(tab.height, UI.screenHeight));
            Pawn local = pawn;
            Find.WindowStack.ImmediateWindow(738253, panelRect, WindowLayer.GameUI,
                () => DrawPanel(new Rect(0f, 0f, panelRect.width, panelRect.height).ContractedBy(12f), local));
        }

        // ---- fulfilment cache (rebuilt when the pawn/loadout changes or every ~1.5 real seconds) ----

        private class SlotStatus
        {
            public Def def;             // ThingDef or LoadoutGenericDef
            public ThingDef iconDef;    // what to draw (generics show their weapon's icon, dimmed)
            public bool genericIcon;
            public int need, carried;
            public int stored;          // -1 = unknown (generics: matching every map thing is too hot for a UI cache)
            // How much of the stock counted above is marked "do not pick up" -
            // puts the shared "!" on this row's status display.
            public int forbidden;
            public bool inProduction;
        }

        private static Pawn cachedPawn;
        private static Loadout cachedLoadout;
        private static float cacheTime = -99f;
        private static readonly List<SlotStatus> slots = new List<SlotStatus>();
        private static Vector2 scroll;

        private static void EnsureCache(Pawn pawn)
        {
            Loadout lo = pawn.GetLoadout();
            if (lo != null && lo.defaultLoadout) lo = null;
            if (pawn == cachedPawn && lo == cachedLoadout && Time.realtimeSinceStartup - cacheTime < 1.5f) return;

            // Population built into a LOCAL list first, freshness markers (cachedPawn/cachedLoadout/cacheTime)
            // committed only after it succeeds (2026-08-01: they used to be set BEFORE this loop
            // ran - a broken 3rd-party ThingDef ref or LoadoutGenericDef.lambda throwing partway through would
            // still leave the cache marked "fresh", rendering zero rows for up to 1.5s instead of retrying on
            // the very next call).
            var newSlots = new List<SlotStatus>();
            if (lo != null && lo.Slots != null)
            {
                // Everything queued on any workbench across every colony map, by product def - one scan shared
                // by all slots (a bill on a different base than this pawn is still worth showing as "in production").
                HashSet<ThingDef> queued = new HashSet<ThingDef>();
                foreach (Building_WorkTable bench in LoadoutUtility.AllColonyBenches())
                {
                    if (bench.billStack == null) continue;
                    foreach (Bill_Production bp in bench.billStack.Bills.OfType<Bill_Production>())
                    {
                        if (bp.suspended || bp.recipe == null || bp.recipe.products == null) continue;
                        foreach (ThingDefCountClass pr in bp.recipe.products)
                            if (pr.thingDef != null) queued.Add(pr.thingDef);
                    }
                }

                int CountCarried(Func<ThingDef, bool> match)
                {
                    int n = 0;
                    if (pawn.equipment != null)
                        foreach (ThingWithComps eq in pawn.equipment.AllEquipmentListForReading)
                            if (match(eq.def)) n += eq.stackCount;
                    if (pawn.apparel != null)
                        foreach (Apparel a in pawn.apparel.WornApparel)
                            if (match(a.def)) n++;
                    if (pawn.inventory != null && pawn.inventory.innerContainer != null)
                        foreach (Thing t in pawn.inventory.innerContainer)
                            if (match(t.def)) n += t.stackCount;
                    // Loaded rounds count as carried, as everywhere else (CE counts them the same way). Without this
                    // a pawn with 40 rounds, 10 of them in the rifle, showed 30/40 here.
                    n += LoadoutUtility.MagazineAmmoCount(pawn, d => match(d));
                    return n;
                }

                foreach (LoadoutSlot s in lo.Slots)
                {
                    // Two packs that both bring "Any Medicine" are two slots of the same def. As two rows each
                    // compared the SAME carried count against its own share (9/3 and 9/6); one row with the
                    // summed need (9/9) is what the colonist actually has to carry.
                    Def slotDef = (Def)s.thingDef ?? s.genericDef;
                    SlotStatus same = slotDef == null ? null : newSlots.FirstOrDefault(x => x.def == slotDef);
                    if (same != null) { same.need += s.count; continue; }
                    var st = new SlotStatus { need = s.count };
                    if (s.thingDef != null)
                    {
                        ThingDef d = s.thingDef;
                        st.def = d;
                        st.iconDef = d;
                        st.carried = CountCarried(td => td == d);
                        st.stored = 0;
                        foreach (Map map in LoadoutUtility.PlayerMaps())
                        {
                            List<Thing> things = map.listerThings.ThingsOfDef(d);
                            if (things != null)
                                // "Do not pick up" stock counts too (see
                                // LoadoutUtility.IsMarkedDoNotPickUp); the exclamation mark on the cell says so.
                                foreach (Thing t in things)
                                    if (!LoadoutUtility.IsHiddenInFog(t) && !(t.ParentHolder is Pawn_EquipmentTracker || t.ParentHolder is Pawn_InventoryTracker || t.ParentHolder is Pawn_CarryTracker))
                                        st.stored += t.stackCount;
                        }
                        st.inProduction = queued.Contains(d);
                        st.forbidden = LoadoutUtility.ForbiddenStockCount(d);
                    }
                    else if (s.genericDef != null)
                    {
                        LoadoutGenericDef gd = s.genericDef;
                        st.def = gd;
                        st.iconDef = LoadoutUtility.GetWeaponForGenericAmmo(gd);
                        st.genericIcon = true;
                        st.carried = CountCarried(td => gd.lambda != null && gd.lambda(td));
                        st.stored = -1;
                        st.inProduction = queued.Any(q => gd.lambda != null && gd.lambda(q));
                        st.forbidden = LoadoutUtility.ForbiddenStockCount(gd);
                    }
                    else continue;
                    newSlots.Add(st);
                }
            }

            slots.Clear();
            slots.AddRange(newSlots);
            cachedPawn = pawn;
            cachedLoadout = lo;
            cacheTime = Time.realtimeSinceStartup;
        }

        private static void DrawPanel(Rect inRect, Pawn pawn)
        {
            if (pawn == null || pawn.Destroyed) return;
            EnsureCache(pawn);
            Loadout lo = cachedLoadout;

            Text.Font = GameFont.Tiny;
            GUI.color = new Color(1f, 1f, 1f, 0.55f);
            Widgets.Label(new Rect(inRect.x, inRect.y, inRect.width, 18f), "CER_GearTabLoadoutPanel_Header".Translate());
            GUI.color = Color.white;
            Text.Font = GameFont.Small;
            float y = inRect.y + 18f;

            float bottomBar = 34f;

            if (lo == null)
            {
                Widgets.Label(new Rect(inRect.x, y + 2f, inRect.width, 24f), "CER_GearTabLoadoutPanel_NoLoadoutAssigned".Translate());
                GUI.color = new Color(1f, 1f, 1f, 0.6f);
                Widgets.Label(new Rect(inRect.x, y + 28f, inRect.width, 80f),
                    "CER_GearTabLoadoutPanel_NoLoadoutHint".Translate());
                GUI.color = Color.white;
            }
            else
            {
                float ix = inRect.x;
                LoadoutUtility.LoadoutMainAndSide(lo, out ThingDef mainW, out ThingDef sideW);
                if (mainW != null) { Widgets.ThingIcon(new Rect(ix, y, 26f, 26f), mainW); ix += 28f; }
                if (sideW != null) { Widgets.ThingIcon(new Rect(ix, y, 26f, 26f), sideW); ix += 28f; }
                Text.Anchor = TextAnchor.MiddleLeft;
                // Title shows the FULL bundle (real template + packs + sidearm(s)), not just the hidden
                // per-pawn "extra ammo" leaf's raw label - the item list below still reads `lo` (the raw
                // assignment) directly, so any extra ammo still counts there regardless of this label.
                string displayLabel = AmmoAssignmentUtility.ComposedDisplayLabel(pawn);
                // ====== Measure against the FINISHED, bold line ======
                //
                // A long loadout label ('R-6 charge rifle +satchel charge, Base Su...') was laid out
                // on two lines, and its FIRST line sat ABOVE the 'Loadout'
                // header.
                //
                // The cause is a half measurement: Truncate measured the RAW text, but it was drawn
                // through the key wrapped in <b>...</b> - and bold is wider. So the "truncated" text
                // did not fit after all, WordWrap broke it onto two lines, and because the anchor is
                // MiddleLeft, a two-line block is centered vertically in a 26-point-high rect:
                // the first line sticks out at the TOP, right onto the header.
                //
                // Two fixes, both needed: measure against the finished string (then the truncation
                // is right), and turn WordWrap off (a title is single-line - otherwise the next
                // edge case tears the same hole open again).
                float titleAvail = inRect.xMax - ix - 8f;
                bool prevWrapTitle = Text.WordWrap;
                Text.WordWrap = false;
                string shownTitle = displayLabel ?? "";
                float fullW = Text.CalcSize("CER_GearTabLoadoutPanel_TitleBold".Translate(shownTitle).ToString()).x;
                if (fullW > titleAvail && shownTitle.Length > 3)
                {
                    // First truncate roughly in proportion, then fine-tune - two to three measurements
                    // instead of one per character (this runs every frame).
                    int keep = Mathf.Clamp(Mathf.FloorToInt(shownTitle.Length * (titleAvail / fullW)) - 1, 1, shownTitle.Length);
                    shownTitle = shownTitle.Substring(0, keep);
                    while (shownTitle.Length > 1
                        && Text.CalcSize("CER_GearTabLoadoutPanel_TitleBold".Translate(shownTitle + "...").ToString()).x > titleAvail)
                        shownTitle = shownTitle.Substring(0, shownTitle.Length - 1);
                    shownTitle += "...";
                }
                Widgets.Label(new Rect(ix + 2f, y, titleAvail, 26f),
                    "CER_GearTabLoadoutPanel_TitleBold".Translate(shownTitle));
                Text.WordWrap = prevWrapTitle;
                Text.Anchor = TextAnchor.UpperLeft;
                // The full name in the tooltip - only the display is truncated, the information stays.
                if (LoadoutUtility.TipWanted(new Rect(ix + 2f, y, titleAvail, 26f)) && shownTitle != displayLabel)
                    TooltipHandler.TipRegion(new Rect(ix + 2f, y, titleAvail, 26f), displayLabel);
                y += 30f;
                Widgets.DrawLineHorizontal(inRect.x, y, inRect.width);
                y += 5f;

                Text.Font = GameFont.Tiny;
                GUI.color = new Color(1f, 1f, 1f, 0.55f);
                Widgets.Label(new Rect(inRect.x + 28f, y, 120f, 16f), "CER_GearTabLoadoutPanel_ColItem".Translate());
                Text.Anchor = TextAnchor.MiddleRight;
                Widgets.Label(new Rect(inRect.xMax - 160f, y, 62f, 16f), "CER_GearTabLoadoutPanel_ColHaveNeed".Translate());
                Text.Anchor = TextAnchor.UpperLeft;
                Widgets.Label(new Rect(inRect.xMax - 88f, y, 88f, 16f), "CER_GearTabLoadoutPanel_ColStatus".Translate());
                GUI.color = Color.white;
                Text.Font = GameFont.Small;
                y += 18f;

                const float rowH = 26f;
                Rect outRect = new Rect(inRect.x, y, inRect.width, inRect.yMax - bottomBar - y - 6f);
                Rect view = new Rect(0, 0, outRect.width - 16f, slots.Count * rowH);
                Widgets.BeginScrollView(outRect, ref scroll, view);
                float ry = 0f;
                foreach (SlotStatus st in slots)
                {
                    Rect row = new Rect(0, ry, view.width, rowH - 2f);
                    if (Mouse.IsOver(row)) Widgets.DrawHighlight(row);

                    if (st.iconDef != null)
                    {
                        // genericIcon means: this is the WEAPON, drawn for "any ammo for it".
                        // Then the ammo badge in the top-right belongs to it (second occurrence after
                        // Dialog_ChangeAmmo) - otherwise the weapon would sit here indistinguishable from a
                        // row that really means the weapon, with only the dimmed white separating the two.
                        Rect gearIcon = new Rect(0f, ry + 1f, 22f, 22f);
                        if (st.genericIcon)
                        {
                            GUI.color = new Color(1f, 1f, 1f, 0.55f);
                            LoadoutUtility.DrawGenericAmmoIcon(gearIcon, st.iconDef);
                        }
                        else Widgets.ThingIcon(gearIcon, st.iconDef);
                        GUI.color = Color.white;
                    }
                    else if (st.def is LoadoutGenericDef gd)
                    {
                        // Non-ammo generics ("Any Medicine", "Any Perishable Meal") have no weapon to stand in
                        // for them; they get the same hand-drawn category badge as everywhere else in the mod.
                        Texture2D badge = GenericIcons.For(gd);
                        if (badge != null) GUI.DrawTexture(new Rect(0f, ry + 1f, 22f, 22f), badge);
                    }
                    Widgets.Label(new Rect(28f, ry + 3f, view.width - 28f - 150f, 22f),
                        (st.def != null ? st.def.label : "CER_GearTabLoadoutPanel_UnknownItem".Translate().ToString()).Truncate(view.width - 28f - 154f));

                    int missing = Mathf.Max(0, st.need - st.carried);
                    string status;
                    Color c;
                    // Harmonized (2026-07-30): "in storage" and "in production" used to both be ColInfo (blue),
                    // indistinguishable from each other here - now yellow/beige, matching FulfillStatus.Stored
                    // and the shared AvailabilitySegments bar's own "in production" color everywhere else.
                    if (missing == 0) { status = "CER_GearTabLoadoutPanel_StatusOk".Translate(); c = LoadoutUtility.ColGood; }
                    else if (st.stored >= missing && st.stored >= 0) { status = "CER_GearTabLoadoutPanel_StatusInStorage".Translate(); c = LoadoutUtility.FulfillColor(FulfillStatus.Stored); }
                    else if (st.inProduction) { status = "CER_GearTabLoadoutPanel_StatusInProduction".Translate(); c = LoadoutUtility.AvailProductionBeige; }
                    else { status = "CER_GearTabLoadoutPanel_StatusMissing".Translate(); c = LoadoutUtility.ColBad; }

                    Text.Anchor = TextAnchor.MiddleRight;
                    GUI.color = missing == 0 ? new Color(1f, 1f, 1f, 0.8f) : c;
                    Widgets.Label(new Rect(view.width - 150f, ry + 1f, 62f, 22f), "CER_GearTabLoadoutPanel_CarriedOverNeed".Translate(st.carried.ToString(), st.need.ToString()));
                    Text.Anchor = TextAnchor.UpperLeft;

                    GUI.color = c;
                    Rect statusDot = new Rect(view.width - 78f, ry + 8f, 8f, 8f);
                    GUI.DrawTexture(statusDot, BaseContent.WhiteTex);
                    Text.Font = GameFont.Tiny;
                    Widgets.Label(new Rect(view.width - 66f, ry + 4f, 66f, 18f), status);
                    Text.Font = GameFont.Small;
                    GUI.color = Color.white;
                    // The shared "!" on this row's stock display - its own tooltip off, the whole row
                    // already has one below and the smaller one would swallow it.
                    LoadoutUtility.DrawForbiddenMark(statusDot, st.forbidden, tooltip: false);

                    // TipWanted first (second occurrence of the same pattern):
                    // this text was assembled PER ROW PER PASS from up to six translations,
                    // although at most one row per frame is under the mouse - and the gear tab
                    // is open practically all the time during normal play.
                    if (LoadoutUtility.TipWanted(row))
                    TooltipHandler.TipRegion(row,
                        (st.def != null ? st.def.LabelCap.ToString() : "CER_GearTabLoadoutPanel_UnknownItem".Translate().ToString())
                        + "CER_GearTabLoadoutPanel_NeededCarried".Translate(st.need.ToString(), st.carried.ToString()).ToString()
                        + (st.stored >= 0 ? "CER_GearTabLoadoutPanel_FreeInStorage".Translate(st.stored.ToString()).ToString() : "")
                        + (st.inProduction ? "CER_GearTabLoadoutPanel_QueuedOnWorkbench".Translate().ToString() : "")
                        + (missing > 0 && !st.inProduction && (st.stored < missing || st.stored < 0)
                            ? "CER_GearTabLoadoutPanel_NotCovered".Translate().ToString() : "")
                        + (st.forbidden > 0
                            ? "\n" + "CER_LoadoutUtility_AvailForbidden".Translate(st.forbidden.ToString()).ToString() : ""));

                    ry += rowH;
                }
                Widgets.EndScrollView();
            }

            // Jump buttons: CE editor for THIS loadout, and the CE Reloaded workflow entry point.
            // Two buttons instead of three (lazy-template rework): the "Generate" jump led to the
            // Weapon Loadouts tab, which no longer exists - weapons are picked when assigning.
            float bw = (inRect.width - 6f) / 2f;
            float by = inRect.yMax - 28f;

            // A colonist managed by us has three layers (shared weapon template, personal ammo amounts,
            // packs); CE's editor only knows the first one and showed e.g. '.30-06 150' with no packs for a
            // colonist that really carries 40 rounds plus two packs - and editing that number there changes
            // it for every colonist with this weapon. Such colonists get ONE button into our planner,
            // filtered to them (the same target CE's own pencil button jumps to, Patch_LoadoutEditButton):
            // 'Edit' and 'Assign' would both lead there, and two buttons doing the same thing read as if
            // they did different things. A hand-built CE loadout keeps both - CE's editor is right for it.
            if (Patch_LoadoutTooltip.GetCellInfo(pawn).managedByUs)
            {
                Rect one = new Rect(inRect.x, by, inRect.width, 28f);
                if (LoadoutUtility.ButtonTextFit(one, "CER_LoadoutTooltip_OpenPlannerButton".Translate().ToString()))
                    OpenTabs(pawn.LabelShort);
                if (LoadoutUtility.TipWanted(one))
                    TooltipHandler.TipRegion(one, "CER_GearTabLoadoutPanel_EditTooltipPlanner".Translate().ToString());
                return;
            }

            Rect b1 = new Rect(inRect.x, by, bw, 28f);
            Rect b2 = new Rect(inRect.x + bw + 6f, by, bw, 28f);

            // The buttons share ONE font size - see
            // LoadoutUtility.ButtonRowFont; bw is fixed, the texts are translated.
            string editLbl = "CER_GearTabLoadoutPanel_EditButton".Translate().ToString();
            string assignLbl = "CER_GearTabLoadoutPanel_AssignButton".Translate().ToString();
            GameFont rowFont = LoadoutUtility.ButtonRowFont((b1, editLbl), (b2, assignLbl));
            GameFont prevRowFont = Text.Font;
            Text.Font = rowFont;
            // Open the real TEMPLATE, not the anchor: CE's own loadout editor opened with a name like
            // '[Pawn-Loadout] <name> <id>' and showed only ONE row as content, 'Knife 1'.
            //
            // Both have the same cause: `lo` is pawn.GetLoadout(), and for a personalized colonist that
            // is their internal anchor - hence the raw name with the pawn ID, and hence only half the
            // content (the anchor only carries the personal extras, the weapon is in the template
            // above it). Every other place in the mod that opens CE's editor passes a real template;
            // only this one did not. DisplayLoadout looks through the anchor - same question,
            // same answer as everywhere else.
            Loadout editTarget = AmmoAssignmentUtility.DisplayLoadout(lo);
            if (editTarget != null && editTarget.defaultLoadout) editTarget = null;
            if (editTarget != null)
            {
                if (LoadoutUtility.ButtonTextFit(b1, editLbl)) Find.WindowStack.Add(new Dialog_ManageLoadouts(editTarget));
            }
            else
            {
                GUI.color = new Color(1f, 1f, 1f, 0.35f);
                LoadoutUtility.ButtonTextFit(b1, editLbl);   // dimmed, not "active: false" (that style strikes the label through)
                GUI.color = Color.white;
            }
            if (LoadoutUtility.TipWanted(b1))
                // Depend on editTarget, no longer on the raw anchor - otherwise the explanation text
                // promises "edit" on a greyed-out button.
                TooltipHandler.TipRegion(b1, editTarget != null ? "CER_GearTabLoadoutPanel_EditTooltipHasLoadout".Translate().ToString() : "CER_GearTabLoadoutPanel_EditTooltipNoLoadout".Translate().ToString());

            if (LoadoutUtility.ButtonTextFit(b2, assignLbl)) OpenTabs(pawn.LabelShort);
            if (LoadoutUtility.TipWanted(b2)) TooltipHandler.TipRegion(b2, "CER_GearTabLoadoutPanel_AssignTooltip".Translate());
            Text.Font = prevRowFont;
        }

        internal static void OpenTabs(string pawnFilter = null)
        {
            // Reuse an already-open tabs window - the same fix that Patch_LoadoutEditButton and
            // LoadoutUtility.OpenBillOverview already carry: two parallel Dialog_CEReloadedTabs mean two
            // separate Assign instances that don't see each other.
            Dialog_CEReloadedTabs tabs = Find.WindowStack.Windows.OfType<Dialog_CEReloadedTabs>().FirstOrDefault();
            if (tabs != null) { tabs.JumpToAssign(pawnFilter); return; }
            tabs = new Dialog_CEReloadedTabs();
            tabs.JumpToAssign(pawnFilter);
            Find.WindowStack.Add(tabs);
        }
    }
}
