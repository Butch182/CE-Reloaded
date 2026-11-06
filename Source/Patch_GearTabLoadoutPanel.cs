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
            // NUR im Repaint (2026-09-03: das Feld wurde jedes Bild angemeldet, aber nie gezeichnet).
            // WindowStack.ImmediateWindow steigt
            // bei jedem anderen Ereignistyp in der ersten Zeile wieder aus, meldet also nichts an. Unity
            // schickt pro Bild ZUERST das Layout-Ereignis und erst danach Repaint, beide mit demselben
            // Time.frameCount: der Riegel darunter verbrauchte das Bild im Layout-Durchgang (wo die
            // Anmeldung wirkungslos ist) und liess den Repaint-Durchgang gar nicht mehr durch. Das Feld
            // wurde damit nie EIN einziges Mal wirklich angemeldet - der Reiter selbst wird von RimWorld in
            // genau so einem ImmediateWindow gezeichnet, wir haengen also am gleichen Mechanismus.
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
            // AUF DEM SCHIRM HALTEN (2026-08-30: das Gear-Tab-Feld fehlte): dieses Feld haengt
            // NEBEN dem Reiter, nicht darin - und wie breit der Reiter ist, bestimmt seit RimWorld 1.6 nicht mehr
            // nur RimWorld. Ersetzt ein anderer Mod den Ausruestungs-Reiter durch einen deutlich breiteren (der
            // Testlauf hatte 'Nice Inventory Tab CE Integration' aktiv), landen beide Kandidaten - rechts daneben
            // und links daneben - ausserhalb des Bildschirms, und das Feld ist da, aber unsichtbar. Geklemmt sieht
            // man es notfalls halb ueber dem Reiter statt gar nicht.
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
            // Wie viel des oben gezaehlten Bestands als "nicht aufsammeln" markiert ist (2026-08-09) -
            // zieht das gemeinsame "!" auf die Statusanzeige dieser Zeile.
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
                    return n;
                }

                foreach (LoadoutSlot s in lo.Slots)
                {
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
                                // "Nicht aufsammeln"-Bestand zaehlt mit (2026-08-09, siehe
                                // LoadoutUtility.IsMarkedDoNotPickUp); das Rufezeichen an der Zelle sagt es dazu.
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
                // ====== Gegen die FERTIGE, fett gesetzte Zeile messen (2026-09-04) ======
                //
                // Befund beim Steam-Screenshot: die Loadout-Bezeichnung 'R-6 charge rifle +satchel charge,
                // Base Su...' wurde zweizeilig gesetzt, und ihre ERSTE Zeile lag UEBER der Ueberschrift
                // 'Loadout'.
                //
                // Die Ursache ist eine halbe Messung: Truncate hat den ROHTEXT gemessen, gezeichnet wurde
                // er aber durch den Schluessel in <b>...</b> - und fett ist breiter. Der "gekuerzte" Text
                // passte also doch nicht, WordWrap hat ihn auf zwei Zeilen gebrochen, und weil der Anker
                // MiddleLeft ist, wird ein zweizeiliger Block in einem 26 Punkte hohen Rechteck mittig
                // gesetzt: die erste Zeile ragt nach OBEN heraus, genau auf die Ueberschrift.
                //
                // Zwei Griffe, beide noetig: gegen die fertige Zeichenkette messen (dann stimmt die
                // Kuerzung), und WordWrap aus (ein Titel ist einzeilig - sonst reisst der naechste
                // Sonderfall dasselbe Loch wieder auf).
                float titleAvail = inRect.xMax - ix - 8f;
                bool prevWrapTitle = Text.WordWrap;
                Text.WordWrap = false;
                string shownTitle = displayLabel ?? "";
                float fullW = Text.CalcSize("CER_GearTabLoadoutPanel_TitleBold".Translate(shownTitle).ToString()).x;
                if (fullW > titleAvail && shownTitle.Length > 3)
                {
                    // Erst proportional grob kuerzen, dann feinschleifen - zwei bis drei Messungen statt
                    // einer je Zeichen (das hier laeuft in jedem Bild).
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
                // Der volle Name im Mouseover - gekuerzt wird nur die Anzeige, die Auskunft bleibt.
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
                        // genericIcon heisst: das ist die WAFFE, gezeichnet fuer "irgendeine Munition dafuer".
                        // Dann gehoert die Munitionsmarke oben rechts dazu (2026-08-13, zweite Fundstelle nach
                        // Dialog_ChangeAmmo) - sonst steht die Waffe hier ohne jeden Unterschied zu einer Zeile,
                        // die wirklich die Waffe meint, und nur das gedimmte Weiss trennt die beiden.
                        Rect gearIcon = new Rect(0f, ry + 1f, 22f, 22f);
                        if (st.genericIcon)
                        {
                            GUI.color = new Color(1f, 1f, 1f, 0.55f);
                            LoadoutUtility.DrawGenericAmmoIcon(gearIcon, st.iconDef);
                        }
                        else Widgets.ThingIcon(gearIcon, st.iconDef);
                        GUI.color = Color.white;
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
                    // Das gemeinsame "!" auf der Bestandsanzeige dieser Zeile (2026-08-09) - eigener Tooltip
                    // aus, die ganze Zeile hat unten schon einen und der kleinere wuerde ihn schlucken.
                    LoadoutUtility.DrawForbiddenMark(statusDot, st.forbidden, tooltip: false);

                    // TipWanted davor (2026-08-17, zweite Fundstelle desselben Musters):
                    // dieser Text wurde PRO ZEILE PRO DURCHGANG aus bis zu sechs Uebersetzungen zusammengesetzt,
                    // obwohl hoechstens eine Zeile pro Bild unter der Maus liegt - und der Ausruestungs-Reiter
                    // ist waehrend des normalen Spielens praktisch dauernd offen.
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
            // Zwei Knoepfe statt drei (Lazy-Umbau 2026-09-01): der "Generate"-Sprung fuehrte in den
            // Weapon-Loadouts-Reiter, und den gibt es nicht mehr - Waffen waehlt man beim Zuweisen.
            float bw = (inRect.width - 6f) / 2f;
            float by = inRect.yMax - 28f;
            Rect b1 = new Rect(inRect.x, by, bw, 28f);
            Rect b2 = new Rect(inRect.x + bw + 6f, by, bw, 28f);

            // Die Knoepfe teilen EINE Schriftstufe (de-buttontext-sweep, 2026-08-29) - siehe
            // LoadoutUtility.ButtonRowFont; bw ist fest, die Texte sind uebersetzt.
            string editLbl = "CER_GearTabLoadoutPanel_EditButton".Translate().ToString();
            string assignLbl = "CER_GearTabLoadoutPanel_AssignButton".Translate().ToString();
            GameFont rowFont = LoadoutUtility.ButtonRowFont((b1, editLbl), (b2, assignLbl));
            GameFont prevRowFont = Text.Font;
            Text.Font = rowFont;
            // Die echte VORLAGE oeffnen, nicht den Anker (2026-09-04): CEs eigener Loadout-Editor ging mit
            // dem Namen '[Pawn-Loadout] Sammy 441509' auf und zeigte als Inhalt nur EINE Zeile, 'Knife 1'.
            //
            // Beides ist dieselbe Ursache: `lo` ist pawn.GetLoadout(), und das ist bei einem
            // personalisierten Kolonisten sein interner Anker - daher der rohe Name samt Pawn-ID, und daher
            // der halbe Inhalt (der Anker traegt nur die persoenlichen Extras, die Waffe steht in der
            // Vorlage darueber). Jede andere Stelle im Mod, die CEs Editor oeffnet, uebergibt eine echte
            // Vorlage; nur diese hier nicht. DisplayLoadout schaut durch den Anker - dieselbe Frage,
            // dieselbe Antwort wie ueberall sonst.
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
                // Am editTarget haengen, nicht mehr am rohen Anker - sonst verspricht der Erklaertext
                // "bearbeiten" an einem ausgegrauten Knopf (2026-09-04).
                TooltipHandler.TipRegion(b1, editTarget != null ? "CER_GearTabLoadoutPanel_EditTooltipHasLoadout".Translate().ToString() : "CER_GearTabLoadoutPanel_EditTooltipNoLoadout".Translate().ToString());

            if (LoadoutUtility.ButtonTextFit(b2, assignLbl)) OpenTabs();
            if (LoadoutUtility.TipWanted(b2)) TooltipHandler.TipRegion(b2, "CER_GearTabLoadoutPanel_AssignTooltip".Translate());
            Text.Font = prevRowFont;
        }

        private static void OpenTabs()
        {
            // Reuse an already-open tabs window (2026-09-01) - dieselbe Korrektur, die
            // Patch_LoadoutEditButton und LoadoutUtility.OpenBillOverview laengst tragen: zwei parallele
            // Dialog_CEReloadedTabs heisst zwei getrennte Assign-Instanzen, die sich nicht sehen.
            Dialog_CEReloadedTabs tabs = Find.WindowStack.Windows.OfType<Dialog_CEReloadedTabs>().FirstOrDefault();
            if (tabs != null) { tabs.JumpToAssign(); return; }
            tabs = new Dialog_CEReloadedTabs();
            tabs.JumpToAssign();
            Find.WindowStack.Add(tabs);
        }
    }
}
