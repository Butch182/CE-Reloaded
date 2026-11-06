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
        public override Vector2 InitialSize =>
            new Vector2(700f, Mathf.Min(pawns.Count > 1 ? 760f : 520f, UI.screenHeight - 60f));
        // Nicht unter RimWorlds Lernhilfe schieben (2026-08-19) - siehe
        // LoadoutUtility.KeepClearOfLearningReadout. Ohne Kollision aendert das nichts.
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
            // ABER nur STANDALONE (2026-08-14: im Test sprang das Fenster rund zwoelfmal ungefragt auf,
            // jedes Mal mit Cancel wegzuklicken). Steht das Tab-Fenster offen, hat es
            // seinen eigenen, GESAMMELTEN Hinweis beim Verlassen des Assign-Tabs - genau den, der die
            // Nachfrage pro Einzelzuweisung ersetzen sollte (Dialog_CEReloadedTabs.CheckSupplyOnLeavingAssign,
            // seit 10.08.: der Hinweis kommt immer am Ende). Solange dieser Haken hier
            // zusaetzlich feuerte, war die Nachfrage pro Zuweisung nie wirklich weg: einmal Munition
            // aendern, Fenster zu, Produktionsdialog - mitten in der Arbeit im selben Tab.
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

            // Die vier Knoepfe der Fussleiste teilen EINE Schriftstufe (de-buttontext-sweep, 2026-08-29) - siehe
            // LoadoutUtility.ButtonRowFont. Rechtecke deshalb vorab, nicht mehr je Knopf an seiner Stelle.
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
                    // Generische Munitions-Slots MIT loeschen (2026-08-13): der
                    // Filter kannte nur konkrete Munition, ein eigener Eintrag "Any ammo for <Waffe>" blieb
                    // liegen. Sichtbare Folge: man drueckt "alle Munition entfernen", die Zeile bleibt
                    // angehakt stehen, und der Kolonist holt weiter Nachschub - waehrend SetAmmoCleared
                    // gleichzeitig die geerbte Munition wegwirft. Reset, Kopieren und Einfuegen fassen beide
                    // Slot-Arten laengst an; nur dieser Knopf nicht.
                    // Ueber die zentrale Definition (2026-08-18). Vorher stand hier ein eigener Test, der
                    // einen Platzhalter nur dann als Munition gelten liess, wenn seine WAFFE noch in der
                    // DefDatabase steht - genau der Fall, den LoadoutUtility.IsGenericAmmoDef ausdruecklich
                    // anders beantwortet: verschwindet der Waffen-Mod, bleibt der Platzhalter Munition und
                    // waere hier fuer immer stehengeblieben, obwohl "alles loeschen" gedrueckt wurde.
                    foreach (LoadoutSlot s in link.OwnSlots.Where(LoadoutUtility.IsAmmoSlot).ToList())
                        link.RemoveSlot(s);
                    AmmoAssignmentUtility.SetAmmoCleared(link, true);
                }
                LoadoutUtility.InvalidateAmmoOwnership();
                countBuffers.Clear();
                // Das Verdraengungs-Gedaechtnis gehoert zum aktuellen Stand - siehe anyAmmoDisplaced.
                anyAmmoDisplaced.Clear();
            }
            TooltipHandler.TipRegion(clearRect, pawns.Count == 1
                ? "CER_ChangeAmmo_ClearAllTipSingle".Translate(pawns[0].LabelShortCap.ToString())
                : "CER_ChangeAmmo_ClearAllTipMulti".Translate(pawns.Count.ToString()));

            // Kopieren-Knopf (2026-08-13: Munition hatte keinen Copy-Knopf, die anderen Spalten schon).
            // Kopiert die Munitionswahl des Kolonisten, dessen Fenster gerade offen ist; eingefuegt wird sie
            // ueber dieselbe eine Einfuege-Spalte in Assign Loadouts, die auch Packs und Zweitwaffen
            // uebernimmt. Bei mehreren ausgewaehlten Kolonisten wird der ERSTE kopiert - eine Ablage haelt
            // eine Auswahl, und "die Munition von sieben Leuten gleichzeitig" waere keine.
            // Kopiert wird die Union, WIE DAS FENSTER SIE ZEIGT - nicht mehr nur der eigene Anker
            // (Umbau 2026-08-26): der Anker eines Rollen-Kolonisten ist konstruktionsbedingt LEER
            // (AssignTemplate raeumt ihn beim Rollenwechsel), also meldete Copy dort "0 entries",
            // waehrend zwei angehakte Zeilen mit Mengen sichtbar waren. Die alte Sorge (Vorlagen-
            // Voreinstellung wandert als persoenliche Festlegung aufs Ziel) ist genau das, was der
            // Spieler beim Einfuegen erwartet: das Ziel soll dieselbe SICHTBARE Auswahl bekommen.
            if (RowBtn(copyRect, copyLbl))
            {
                Pawn src = pawns.FirstOrDefault();
                List<LoadoutSlot> ammoSlots = EffectiveAmmoSlotsAsShown(src);
                LoadoutClipboard.CopyAmmo(ammoSlots, src != null ? src.LabelShortCap.ToString() : "");
                // Ein- und Mehrzahl getrennt (2026-08-19): die Meldung
                // schrieb "Copied 1 ammo entr(ies)". Dieselbe Bauform, die CEReloadedMod fuer die
                // uebernommenen Vorgaben schon benutzt - eine Klammer-Endung laesst sich in mehreren der
                // 13 Sprachen ohnehin nicht sauber bilden.
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
                // Das Verdraengungs-Gedaechtnis gehoert zum aktuellen Stand - siehe anyAmmoDisplaced.
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
                string carried = BuildCarriedAmmoSummary(s.pawn);
                if (!string.IsNullOrEmpty(carried))
                {
                    Text.Font = GameFont.Tiny;
                    GUI.color = new Color(1f, 1f, 1f, 0.55f);
                    Widgets.Label(new Rect(header.x + 34f, header.y + 24f, viewRect.width - 34f, 20f), carried.Truncate(viewRect.width - 34f - 4f));
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
            // Der Namenskopf endet vor den drei Vergleichsbalken (2026-09-06), und die bekommen ihre eigenen
            // Koepfe - dieselbe Rechnung wie AmmoRoleCell fuer die Zeilen darunter (Symbolkante 190, Luft 8).
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
                            // IsAmmoSlot (2026-08-14): steht daneben noch ein "Any ammo", ist der Kolonist
                            // NICHT auf null gesetzt, sondern hat eine Wahl - ihn hier als "bewusst leer" zu
                            // markieren wirkt erst spaeter, wenn auch der generische Haken faellt: dann
                            // bliebe er ohne jede Munition statt bei der Vorgabe der Vorlage.
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
                // Der Namensspalte den Platz fuer den Rollen-Balken abziehen (2026-09-04) - siehe
                // AmmoRoleCell unten. Ohne Platz faellt der Balken weg und der Name bekommt alles.
                Rect roleCell1 = AmmoRoleCell(row, 190f);
                Rect labelRect = new Rect(row.x + 50f, row.y,
                    (roleCell1.width > 0f ? roleCell1.x - 8f : row.xMax - 246f) - (row.x + 50f), 24f);
                // Der Zustands-Zusatz kommt NACH dem Kuerzen dazu (2026-08-23, dritte Fundstelle per
                // Regel-7-Grep): er endet selbst auf ")", also haette TruncateKeepingSuffix ihn fuer die
                // Munitionssorte gehalten und ausgerechnet die Sorte weggeschnitten - " (not researched)"
                // haette ueberlebt, "(FMJ)" nicht. Deshalb erst den Namen auf den verbleibenden Platz
                // kuerzen, dann den Zusatz anhaengen.
                string ammoSuffix = researched ? "" : "CER_ChangeAmmo_NotResearchedSuffix".Translate().ToString();
                string labelText = LoadoutUtility.TruncateKeepingSuffix(ammoDef.label,
                    labelRect.width - 4f - (ammoSuffix.Length > 0 ? Text.CalcSize(ammoSuffix).x : 0f)) + ammoSuffix;
                Widgets.Label(labelRect, labelText);
                GUI.color = Color.white;
                string names = string.Join(", ", applicable.Select(s => s.pawn.LabelShortCap));
                TooltipHandler.TipRegion(new Rect(iconRect.x, iconRect.y, labelRect.xMax - iconRect.xMin, 24f),
                    "CER_ChangeAmmo_AppliesToTip".Translate(LoadoutUtility.GetAmmoRecipeTooltip(ammoDef).ToString(), names));

                SpecialAbility ammoEff = LoadoutUtility.GetAmmoEffects(ammoDef);
                // Die Deckelung ist die ECHTE Luecke bis zum Mengenfeld (row.xMax - 122f), nicht eine
                // eigene Zahl: 150f war groesser als der Platz, ab vier Effekten lief der Streifen ins Feld
                // hinein (2026-08-15, Review-Befund). Aus denselben zwei Konstanten gerechnet, damit die
                // beiden beim naechsten Verschieben nicht wieder auseinanderlaufen.
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
                // "!" auf den Bestands-Punkt, wenn ein Teil des gezaehlten Vorrats als "nicht aufsammeln"
                // markiert ist (2026-08-09) - der Punkt ist hier die Bestandsanzeige der Zeile.
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
        // Abhaken nimmt ihn allen wieder weg (2026-08-12, zweite Fundstelle desselben Fehlers - im Test
        // fiel genau DIESE Zeile auf). Vorher war
        // jeder Klick, der nicht auf "An" hinauslief, ein No-Op.
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
                    // Dieselbe Verdraengung wie in der Einzelansicht - der Schalter gibt es zweimal.
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
            // Waffensymbol MIT Munitionsmarke - siehe LoadoutUtility.DrawGenericAmmoIcon.
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

        // Wo steht der "wogegen ist das gut"-Balken in DIESER Zeile (2026-09-04: in den Spalten zwischen
        // Munitionsname und Schadensart)? Genau dort: rechts vom Namen,
        // links von der Symbolspalte, deren Kante die Zeile selbst kennt (190 bzw. 210 - die beiden
        // Zeilenzeichner haben historisch verschiedene Kanten, deshalb ist sie ein Parameter und
        // keine dritte Konstante).
        //
        // Faellt der Name unter MinNameW, gibt es KEINEN Balken: ein abgeschnittener Munitionsname
        // ist teurer als eine fehlende Zusatzinfo - man kann dann nicht mehr unterscheiden, welche
        // Sorte man gerade anhakt. Der Balken ist der Zusatz, der Name ist die Sache selbst.
        // Seit 2026-09-06 drei Vergleichsbalken (Schaden / scharf / stumpf, normiert aufs Kaliber) statt
        // des einen Deutungsbalkens - siehe LoadoutUtility.DrawAmmoStatBars.
        private static Rect AmmoRoleCell(Rect row, float effIconX)
        {
            const float MinNameW = 150f, Gap = 8f;
            float right = row.xMax - effIconX - Gap;              // linke Kante der Symbolspalte, minus Luft
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
                    // IsAmmoSlot (2026-08-14) - siehe die gleichlautende Stelle in der Gruppen-Ansicht: ein
                    // noch stehender "Any ammo"-Slot ist eine Wahl, kein leerer Zustand.
                    if (!link.OwnSlots.Any(LoadoutUtility.IsAmmoSlot)) AmmoAssignmentUtility.SetAmmoCleared(link, true);
                }
                LoadoutUtility.InvalidateAmmoOwnership(ammoDef);
            }

            Rect iconRect = new Rect(row.x + 26f, row.y + 2f, 20f, 20f);
            GUI.color = Color.white;
            Widgets.ThingIcon(iconRect, ammoDef);

            GUI.color = researched ? (hasAmmo ? Color.white : new Color(1f, 1f, 1f, 0.55f)) : Color.gray;
            // Zwilling der Gruppen-Zeile weiter oben - dieselbe Zelle, nur mit DIESER Zeile eigener
            // Symbolspalten-Kante (210 statt 190). Beide holen sie aus AmmoRoleCell, damit sie beim
            // naechsten Verschieben nicht auseinanderlaufen.
            Rect roleCell2 = AmmoRoleCell(row, 210f);
            Rect labelRect = new Rect(row.x + 50f, row.y,
                (roleCell2.width > 0f ? roleCell2.x - 8f : row.xMax - 246f) - (row.x + 50f), 24f);
            // Siehe die Zwillingsstelle weiter oben: Zusatz erst nach dem Kuerzen anhaengen.
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
            // Deckelung = echte Luecke bis zum Mengenfeld (row.xMax - 146f), siehe die Zwillingsstelle oben.
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
            // Die Abbruchbedingung zaehlt generische Eigen-Slots mit (2026-08-14, dritte Fundstelle
            // derselben Frage): ein Kolonist, der NUR auf "Any ammo" steht, hat sehr wohl schon eine eigene
            // Wahl getroffen. Vorher galt er hier als unberuehrt, und der naechste Klick hat ihm die
            // konkrete Vorgabe-Munition der Vorlage zusaetzlich in den Anker kopiert - also genau den
            // Doppelzustand "Any ammo UND drei konkrete Sorten", den der Fehlerbericht beschreibt.
            // Kopiert wird weiterhin nur KONKRETE Munition: ein geerbter generischer Platzhalter ist keine
            // Wahl, die man materialisieren muesste, sondern das, wovon man sich gerade loest.
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
        // Genau die Munitions-Eigenslots, die DIESES FENSTER dem Spieler auch zeigt - eine Frage, eine
        // Antwort (2026-08-18: Kopieren meldete drei Eintraege, im Fenster
        // standen zwei).
        // Der Kopieren-Knopf zaehlte vorher jeden Eigenslot des Ankers, der irgendwie nach Munition aussah
        // ("IsAmmoDef(thingDef) || genericDef != null"), waehrend die Zeilen darueber aus
        // RelevantAmmoPool/RelevantGenericWeapons kommen - also nur aus den Waffen, die der Kolonist
        // WIRKLICH traegt. Ein Rest-Slot einer laengst abgenommenen Zweitwaffe wurde damit mitgezaehlt und
        // sogar mitkopiert, obwohl ihn niemand sehen und niemand gebrauchen konnte (Einfuegen wirft ihn am
        // anderen Ende ohnehin weg, siehe Dialog_AssignLoadouts.PasteAmmo).
        // Ausserdem ueber IsGenericAmmoDef statt "genericDef != null": ein generischer VERSORGUNGS-Eintrag
        // (etwa "Any Medicine") ist keine Munition und hat in der Munitions-Ablage nichts verloren.
        // Seit 2026-08-26 aus der UNION (GetLoadout().Slots) statt aus leaf.OwnSlots: der Anker eines
        // Rollen-Kolonisten ist konstruktionsbedingt leer (AmmoAssignmentUtility.AssignTemplate raeumt
        // OwnSlots beim Rollenwechsel), die Zeilen des Fensters kommen aber aus der Union - Copy sah
        // also 0, wo das Fenster 2 angehakte Sorten zeigte. Erste Fundstelle je Sorte gewinnt, weil die
        // Mengenfelder des Fensters mit FirstOrDefault aus derselben Liste lesen.
        private static List<LoadoutSlot> EffectiveAmmoSlotsAsShown(Pawn src)
        {
            if (src == null) return new List<LoadoutSlot>();
            List<LoadoutSlot> effective = src.GetLoadout()?.Slots?.ToList() ?? new List<LoadoutSlot>();
            HashSet<ThingDef> shownConcrete = new HashSet<ThingDef>(RelevantAmmoPool(effective));
            HashSet<ThingDef> shownGenericWeapons = new HashSet<ThingDef>(RelevantGenericWeapons(effective));
            var result = new List<LoadoutSlot>();
            var seenConcrete = new HashSet<ThingDef>();
            // Generische Slots NICHT ueber den Def dedupen (CE-Generics haben alle Hash 0, siehe
            // DefRefComparer) - der Schluessel ist die Waffe, zu der der Generic gehoert.
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

        // "Hat dieser Anker eigene Munition?" - konkrete UND generische Slots (2026-08-13).
        // Dieselbe Frage stellt Patch_Loadout_MultiParentSlots, und beide muessen sie gleich
        // beantworten, sonst wirft der eine die geerbte Munition weg, waehrend der andere sie
        // zurueckholt.
        // Ueber LoadoutUtility.IsAmmoSlot (2026-08-14) - dieselbe Frage wie in
        // Patch_Loadout_MultiParentSlots, jetzt auch wirklich dieselbe Antwort. Solange die beiden eigene
        // Fassungen hatten, war ein Auseinanderlaufen nur eine Frage der Zeit, und genau das ist passiert.
        private static bool HasOwnAmmoSlot(Loadout link)
            => link?.OwnSlots != null && link.OwnSlots.Any(LoadoutUtility.IsAmmoSlot);

        // Was bedeutet "Any ammo abhaken"? Das haengt daran, WOHER der Haken kam - und genau diese
        // Unterscheidung fehlte (2026-08-14, erneut aufgetreten: nach dem Abhaken BEIDER generischer Zeilen
        // stand der Kolonist ohne jede Munition da, statt auf die Vorgabe der Vorlage zurueckzufallen).
        //   - Der Kolonist hatte den generischen Eintrag SELBST gesetzt -> Abhaken heisst "zurueck zur
        //     Vorlage". 'cleared' muss dann FALSE sein, sonst unterdrueckt der Anker alles Geerbte und der
        //     Kolonist steht mit leeren Haenden da.
        //   - Der generische Eintrag kam aus der VORLAGE -> Abhaken heisst "diese Munition will ich nicht".
        //     Da es nichts Eigenes zu entfernen gab, haelt nur das ausdrueckliche 'cleared' sie draussen.
        // Bleibt daneben noch irgendein eigener Munitions-Slot stehen, ist ohnehin nichts geleert.
        // Der Fix vom 13.08. hat nur den dritten Fall behandelt und die ersten beiden zusammengeworfen.
        private static bool ClearedAfterGenericOff(Loadout link, bool hadOwnGeneric)
            => !HasOwnAmmoSlot(link) && !hadOwnGeneric;

        // Was das Einschalten von "Any ammo" VERDRAENGT hat - je Kolonist und Waffe, solange dieses Fenster
        // offen ist (2026-08-18).
        //
        // Fehlerbild: "Any ammo" ging zwar aus, hakte dann aber andere Sorten wieder an. Im Einzelnen:
        // vorher nur Ion 150 angehakt, Any ammo AN -> alles konkrete aus, Any ammo AUS -> ploetzlich
        // Charged 120 UND Ion 150.
        // Nachgerechnet und nicht geraten: das Einschalten LOESCHT die eigenen konkreten Slots der Waffe
        // (die Zeile "RemoveSlot" im Ein-Zweig), und das Ausschalten kann sie nicht zurueckholen. Danach hat
        // der Anker gar keine eigene Munition mehr, gilt also als "nie angefasst" - und die Vorgabe der
        // VORLAGE kommt durch. Genau das sah aus wie "hakt andere Sorten an".
        //
        // Ein Schalter, der beim Einschalten Daten wegwirft, laesst sich nicht ausschalten. Also merkt sich
        // das Fenster, was es verdraengt hat, und legt es beim Ausschalten zurueck.
        // Das ersetzt NICHT den Rueckfall auf die Vorlage: hatte der Kolonist vorher gar nichts Eigenes
        // (er stand also selbst auf der Vorlage), gibt es nichts zurueckzulegen und es bleibt beim alten
        // Verhalten - genau der Fall, um den es am 2026-08-14 ging ("stand ohne jede Munition da").
        // Grenze, bewusst: das Gedaechtnis lebt nur so lange wie dieses Fenster. Wer Any ammo einschaltet,
        // das Fenster schliesst und es spaeter wieder ausschaltet, landet beim Rueckfall auf die Vorlage -
        // dauerhaft speichern hiesse, den Spielstand um einen Rueckgaengig-Puffer zu erweitern.
        private readonly Dictionary<string, List<LoadoutSlot>> anyAmmoDisplaced = new Dictionary<string, List<LoadoutSlot>>();

        private static string DisplacedKey(Pawn pawn, ThingDef weapon) =>
            (pawn != null ? pawn.thingIDNumber : 0) + "|" + (weapon != null ? weapon.defName : "?");

        // Nimmt die eigenen konkreten Slots dieser Waffenfamilie weg und merkt sie sich. Als KOPIEN, weil
        // die Originale gleich aus dem Loadout fliegen.
        private void DisplaceOwnFamilyAmmo(Loadout link, Pawn pawn, ThingDef weapon, List<ThingDef> family)
        {
            if (link?.OwnSlots == null) return;
            List<LoadoutSlot> hit = link.OwnSlots.Where(s => family.Contains(s.thingDef)).ToList();
            anyAmmoDisplaced[DisplacedKey(pawn, weapon)] = hit.Select(s => new LoadoutSlot(s.thingDef, s.count)).ToList();
            foreach (LoadoutSlot own in hit) link.RemoveSlot(own);
        }

        // Legt zurueck, was das Einschalten weggenommen hat. False, wenn es nichts zurueckzulegen gab -
        // dann gilt weiter der Rueckfall auf die Vorlage.
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
        // Der Haken geht jetzt in BEIDE Richtungen (2026-08-12).
        // Vorher stand hier "Klicken im angehakten Zustand ist ein No-Op - es gibt keinen eigenen
        // Aus-Zustand, die Zeile haekelt sich nur als Nebenwirkung ab, wenn man unten eine konkrete Sorte
        // waehlt". Das war als Absicht gemeint und kam als Fehler an: ein Haken, der sich nicht wieder
        // ausschalten laesst, ist genau das "ich klicke und es tut sich nichts", das hier schon mehrfach
        // aufgefallen war - und im Test hat es die Gegenprobe blockiert, weil der generische
        // Eintrag nicht mehr wegging. Aus heisst dasselbe wie in der Zeile darunter (DrawAmmoRow): den
        // eigenen Slot entfernen und, wenn danach gar keine eigene Munition mehr steht, ausdruecklich
        // "geleert" markieren, damit der Vorlagen-Default nicht still zurueckkommt.
        private void DrawAnyAmmoRow(Rect row, Pawn pawn, Loadout template, ThingDef weapon, LoadoutGenericDef generic, List<LoadoutSlot> effective)
        {
            bool isGeneric = HasGenericSlot(effective, generic);
            bool check = isGeneric;
            Widgets.Checkbox(new Vector2(row.x, row.y + 2f), ref check, 20f);
            if (!check && isGeneric && generic != null)
            {
                // EnsurePersonalAmmo auch hier: der generische Slot kann GEERBT sein (aus der Vorlage), dann
                // gibt es nichts zu entfernen und nur das ausdrueckliche "geleert" am eigenen Anker haelt ihn
                // draussen - dieselbe Mechanik wie beim Anhaken eine Zeile weiter unten.
                EnsurePersonalAmmo(pawn, template, effective);
                Loadout link = AmmoAssignmentUtility.GetAmmoLeaf(pawn);
                if (link != null)
                {
                    // Hatte der Kolonist diesen generischen Eintrag SELBST gesetzt, oder kam er aus der
                    // Vorlage? Davon haengt ab, was das Abhaken bedeutet - vor dem Entfernen festhalten.
                    bool hadOwnGeneric = link.OwnSlots.Any(s => s.genericDef == generic);
                    foreach (LoadoutSlot own in link.OwnSlots.Where(s => s.genericDef == generic).ToList())
                        link.RemoveSlot(own);
                    // VOR der cleared-Rechnung: die zurueckgelegten Slots sind eigene Munition, der Anker
                    // gilt danach also zu Recht nicht als geleert.
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
                    // Verdraengen statt loeschen - siehe anyAmmoDisplaced.
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
            // Waffensymbol MIT Munitionsmarke - siehe LoadoutUtility.DrawGenericAmmoIcon.
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
