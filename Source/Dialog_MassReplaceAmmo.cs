using System;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;
using Verse;
using RimWorld;
using CombatExtended;

namespace CEReloaded
{
    public class ReplaceableAmmoSlot
    {
        public Loadout loadout;
        public LoadoutSlot slot;
        public AmmoSetDef ammoSet;
        public ThingDef originalAmmo;              // null for a generic "Any Ammo for X" slot
        public LoadoutGenericDef originalGeneric;  // set for a generic slot
        public ThingDef selectedAmmo;              // a concrete replacement (null if none / kept / went generic)
        public LoadoutGenericDef selectedGeneric;  // a generic "Any Ammo for X" replacement
        public string originalLabel;

        public bool IsGeneric => originalGeneric != null;
        // Changed = a concrete ammo or a generic placeholder was picked that differs from the original.
        public bool IsChanged => selectedGeneric != null ? selectedGeneric != originalGeneric
                                                         : (selectedAmmo != null && selectedAmmo != originalAmmo);
        public string SelectionLabel => selectedGeneric != null ? selectedGeneric.label
                                      : (selectedAmmo != null ? selectedAmmo.label : originalLabel);
    }

    public enum AmmoFilter
    {
        Available,
        Researched,
        All
    }

    public class Dialog_MassReplaceAmmo : Window
    {
        private List<ReplaceableAmmoSlot> allSlots = new List<ReplaceableAmmoSlot>();
        private AmmoFilter currentFilter = AmmoFilter.Researched;
        private Vector2 scrollPosition = Vector2.zero;
        // Volltextsuche wie in jeder anderen Liste des Mods (2026-08-14) - dieses Fenster war das einzige
        // ohne, bei ueber 60 Zeilen. Sie filtert NUR die ANZEIGE: `ApplyChanges` laeuft weiterhin ueber
        // `allSlots`, eine vor dem Tippen getroffene Auswahl geht also nicht verloren, nur weil ihre Zeile
        // gerade nicht sichtbar ist.
        private string search = "";
        // "N von M waehlbar" neben den Filter-Radios. Der Filter hier entscheidet ausschliesslich, welche
        // Munition im AUSWAHLMENUE einer Zeile steht - an der Liste selbst aendert er per Definition nichts
        // (die zeigt, was in den Vorlagen STEHT, und gerade die unerforschte Sorte will man ja ersetzen).
        // Genau das war das Problem: der Umschalter sah tot aus, weil seine Wirkung erst zwei Klicks spaeter
        // sichtbar wurde. Statt die Liste falsch zu filtern, sagt die Zahl sofort, was er tut.
        // Gemerkt gegen den Filterwert, damit AmmoProducibleNow nicht pro Bild ueber alle Sorten laeuft.
        private AmmoFilter choiceCountFor = (AmmoFilter)(-1);
        private int choiceCountAllowed, choiceCountTotal;
        
        public override Vector2 InitialSize => new Vector2(800f, 600f);
        // Nicht unter RimWorlds Lernhilfe schieben (2026-08-19) - siehe
        // LoadoutUtility.KeepClearOfLearningReadout. Ohne Kollision aendert das nichts.
        protected override void SetInitialSizeAndPosition()
        {
            base.SetInitialSizeAndPosition();
            windowRect = LoadoutUtility.KeepClearOfLearningReadout(windowRect);
        }

        public Dialog_MassReplaceAmmo() : base(new ReadoutAwareWindowDrawing())
        {
            this.doCloseX = true;
            this.forcePause = true;
            this.absorbInputAroundWindow = true;

            // Seed the availability filter from the mod-settings default (shared with the other ammo pickers).
            if (CEReloadedMod.settings != null)
            {
                switch (CEReloadedMod.settings.defAmmoFilter)
                {
                    case AmmoFilterMode.AllAmmo: currentFilter = AmmoFilter.All; break;
                    case AmmoFilterMode.ResearchedAmmo: currentFilter = AmmoFilter.Researched; break;
                    case AmmoFilterMode.AvailableAmmo: currentFilter = AmmoFilter.Available; break;
                }
            }

            // Build map of AmmoDef -> AmmoSetDef
            Dictionary<ThingDef, AmmoSetDef> ammoToSetMap = new Dictionary<ThingDef, AmmoSetDef>();
            foreach (AmmoSetDef setDef in DefDatabase<AmmoSetDef>.AllDefs)
            {
                if (setDef.ammoTypes != null)
                {
                    foreach (AmmoLink link in setDef.ammoTypes)
                    {
                        if (link.ammo != null && !ammoToSetMap.ContainsKey(link.ammo))
                        {
                            ammoToSetMap[link.ammo] = setDef;
                        }
                    }
                }
            }

            // Find all loadouts with ammo slots - both concrete ammo and generic "Any Ammo for X" placeholders.
            // OwnSlots, NOT Slots: Loadout.RemoveSlot/AddSlot (CE's own implementation) only ever touch a
            // loadout's OWN backing list - calling RemoveSlot with a slot that's actually owned by an
            // ANCESTOR (inherited via Slots' own parent-chain walk) is a silent no-op (List.Remove just
            // returns false), so ApplyChanges below would fail to remove the old ammo while still adding
            // the new one, leaving the pawn carrying/demanding BOTH. This is exactly what happens for any
            // personalized pawn (a Pack/Sidearm attached via the hidden per-pawn anchor) who never
            // personally touched their own ammo yet - their anchor's ammo is 100% inherited from the real
            // template, so it was never safe to edit through the anchor row in the first place. Scoping to
            // OwnSlots means only the actual owner (the template itself, or an anchor that already has its
            // own ammo override) ever shows a row - editing the template's own row still correctly cascades
            // to every pawn on it (Slots resolves the union live), and it also removes the duplicate-listing
            // noise of the same inherited slot showing up once per pawn on top of the template's own row.
            foreach (Loadout loadout in LoadoutManager.Loadouts)
            {
                // Hidden per-pawn personal anchors ("[Pawn-Loadout] <id>") are internal bookkeeping, never
                // meant to surface to the player (2026-08-01: this scan had no such filter,
                // leaking the raw internal label as its own row - every other loadout-listing screen in the
                // mod already excludes these the same way).
                if (AmmoAssignmentUtility.IsAmmoLink(loadout)) continue;
                if (loadout.OwnSlots == null) continue;
                foreach (LoadoutSlot slot in loadout.OwnSlots)
                {
                    if (slot.thingDef != null && ammoToSetMap.TryGetValue(slot.thingDef, out AmmoSetDef setDef))
                    {
                        allSlots.Add(new ReplaceableAmmoSlot
                        {
                            loadout = loadout,
                            slot = slot,
                            ammoSet = setDef,
                            originalAmmo = slot.thingDef,
                            selectedAmmo = slot.thingDef,
                            originalLabel = slot.thingDef.label
                        });
                    }
                    else if (slot.genericDef != null)
                    {
                        ThingDef gun = LoadoutUtility.GetWeaponForGenericAmmo(slot.genericDef);
                        AmmoSetDef genSet = LoadoutUtility.GetAmmoSet(gun);
                        if (genSet != null)
                        {
                            allSlots.Add(new ReplaceableAmmoSlot
                            {
                                loadout = loadout,
                                slot = slot,
                                ammoSet = genSet,
                                originalAmmo = null,
                                originalGeneric = slot.genericDef,
                                selectedAmmo = null,   // keep generic by default
                                originalLabel = slot.genericDef.label
                            });
                        }
                    }
                }
            }
        }

        public override void DoWindowContents(Rect inRect)
        {
            Text.Font = GameFont.Medium;
            Widgets.Label(new Rect(0f, 0f, inRect.width, 35f), "CER_MassReplaceAmmo_Title".Translate());
            Text.Font = GameFont.Small;

            // Hinweiszeile, wenn es hier wirklich etwas zu holen gibt (2026-08-13). Dieselbe Rechnung wie
            // in der Bereitschaftsanzeige, die einen hierher schickt -
            // damit die beiden nie verschiedene Zahlen nennen. Die betroffenen Zeilen sind unten zusaetzlich
            // eingefaerbt, sonst muesste man die Namen von Hand vergleichen.
            List<Loadout> betterAmmo = LoadoutUtility.TemplatesWithBetterAmmo();
            if (betterAmmo.Count > 0)
            {
                GUI.color = LoadoutUtility.ColWarn;
                Widgets.Label(new Rect(0f, 26f, inRect.width, 20f),
                    "CER_MassReplaceAmmo_BetterAmmoHint".Translate(betterAmmo.Count.ToString()));
                GUI.color = Color.white;
            }

            // Filter radios (shared style; ordered All -> Researched -> Available like every other availability filter).
            float filterY = 40f;
            Widgets.Label(new Rect(0f, filterY + 2f, 80f, 24f), "CER_MassReplaceAmmo_AmmoLabel".Translate());
            float rx = 84f;
            void FilterRadio(string label, AmmoFilter val)
            {
                if (LoadoutUtility.Radio(ref rx, filterY, label, currentFilter == val)) currentFilter = val;
            }
            FilterRadio("CER_MassReplaceAmmo_FilterAll".Translate(), AmmoFilter.All);
            FilterRadio("CER_MassReplaceAmmo_FilterResearched".Translate(), AmmoFilter.Researched);
            FilterRadio("CER_MassReplaceAmmo_FilterAvailable".Translate(), AmmoFilter.Available);

            // Das Suchfeld sitzt in der Zeile DARUNTER, rechts, neben dem Zaehler (2026-08-15: auf dem
            // Bildschirm stand woertlich "Available ammoSearch:" - das dritte
            // Radio und die Beschriftung des Suchfelds stiessen ohne Abstand aneinander).
            // Die alte Rechnung ging von "die drei Radios enden bei rund x=354" aus. Das ist keine
            // Konstante, sondern haengt an der LAENGE der drei uebersetzten Beschriftungen; im Englischen
            // reichte es schon nicht. Statt die Zahl 284 zu drehen wandert das Feld
            // in die Zeile, in der links nur der kurze Zaehlertext steht - dort kann es bei keiner Breite
            // und in keiner Sprache mit den Radios kollidieren.
            // SearchBox belegt ab x: 60 (Beschriftung) + 200 (Feld) + 24 (X-Knopf) = 284.
            search = LoadoutUtility.SearchBox(inRect.width - 284f, filterY + 26f, search);

            // Sofortige Rueckmeldung auf den Umschalter - siehe choiceCountFor.
            RefreshChoiceCount();
            GUI.color = new Color(1f, 1f, 1f, 0.6f);
            Text.Font = GameFont.Tiny;
            // Breite endet vor dem Suchfeld, damit der (im Deutschen lange) Zaehlertext nicht darunter laeuft.
            Widgets.Label(new Rect(84f, filterY + 30f, Mathf.Max(60f, inRect.width - 84f - 292f), 18f),
                "CER_MassReplaceAmmo_ChoicesCount".Translate(choiceCountAllowed.ToString(), choiceCountTotal.ToString()));
            Text.Font = GameFont.Small;
            GUI.color = Color.white;

            List<ReplaceableAmmoSlot> visible = allSlots;
            if (!string.IsNullOrEmpty(search))
            {
                string needle = search.ToLower();
                visible = allSlots.Where(s =>
                    (s.loadout?.label ?? "").ToLower().Contains(needle)
                    || (s.originalLabel ?? "").ToLower().Contains(needle)
                    || (s.SelectionLabel ?? "").ToLower().Contains(needle)).ToList();
            }

            // 96 statt 90: in der zweiten Zeile steht jetzt nicht nur der 18px-Zaehler, sondern auch das
            // 24px hohe Suchfeld (2026-08-15) - sonst schneidet die Trennlinie es an.
            float listY = LoadoutUtility.Divider(96f, inRect.width);
            Rect viewRect = new Rect(0f, 0f, inRect.width - 16f, visible.Count * 30f);
            Rect scrollRect = new Rect(0f, listY, inRect.width, inRect.height - listY - 50f);

            Widgets.BeginScrollView(scrollRect, ref scrollPosition, viewRect);

            if (visible.Count == 0)
            {
                GUI.color = new Color(1f, 1f, 1f, 0.5f);
                Widgets.Label(new Rect(0f, 4f, viewRect.width, 24f), allSlots.Count == 0
                    ? "CER_MassReplaceAmmo_NothingToReplace".Translate()
                    : "CER_MassReplaceAmmo_NoSearchHits".Translate(search));
                GUI.color = Color.white;
            }

            float rowY = 0f;
            foreach (ReplaceableAmmoSlot rSlot in visible)
            {
                // Left: ammo icon + "Loadout - Ammo name" (generic slots read "Any Ammo for X" and have no concrete icon).
                string rowLabel = "CER_MassReplaceAmmo_RowLabel".Translate(LoadoutUtility.LoadoutDisplayName(rSlot.loadout), rSlot.originalLabel);
                float lx = 0f;
                if (rSlot.originalAmmo != null) { Widgets.ThingIcon(new Rect(0f, rowY + 2f, 20f, 20f), rSlot.originalAmmo); lx = 24f; }
                // Eingefaerbt, wenn genau diese Vorlage neu erforschte Munition bekommen koennte - siehe die
                // Hinweiszeile oben. Nur die Schriftfarbe, kein Hintergrund: die Zeile bleibt anklickbar und
                // liest sich wie jede andere, sie faellt nur auf.
                bool rowHasBetter = betterAmmo.Contains(rSlot.loadout);
                if (rowHasBetter) GUI.color = LoadoutUtility.ColWarn;
                Widgets.Label(new Rect(lx, rowY + 2f, viewRect.width - 340f - lx, 24f), rowLabel.Truncate(viewRect.width - 340f - lx));
                if (rowHasBetter) GUI.color = Color.white;

                // Center: Indicator
                Rect iconRect = new Rect(viewRect.width - 330f, rowY + 2f, 24f, 24f);
                if (!rSlot.IsChanged)
                {
                    GUI.color = Color.gray;
                    Widgets.Label(iconRect, "CER_MassReplaceAmmo_IndicatorUnchanged".Translate());
                    GUI.color = Color.white;
                }
                else
                {
                    GUI.color = Color.green;
                    Widgets.Label(iconRect, "CER_MassReplaceAmmo_IndicatorChanged".Translate());
                    GUI.color = Color.white;
                }

                // Right: Dropdown button (ammo icon before the chosen ammo's name; generic picks have no icon).
                Rect dropRect = new Rect(viewRect.width - 300f, rowY, 300f, 24f);
                bool dropClicked = Widgets.ButtonText(dropRect, "");
                {
                    float dx = dropRect.x + 6f;
                    if (rSlot.selectedAmmo != null) { Widgets.ThingIcon(new Rect(dx, dropRect.y + 2f, 20f, 20f), rSlot.selectedAmmo); dx += 24f; }
                    Text.Anchor = TextAnchor.MiddleLeft;
                    Widgets.Label(new Rect(dx, dropRect.y, dropRect.xMax - dx - 6f, 24f), rSlot.SelectionLabel.Truncate(dropRect.xMax - dx - 6f));
                    Text.Anchor = TextAnchor.UpperLeft;
                }
                if (dropClicked)
                {
                    ReplaceableAmmoSlot local = rSlot;
                    List<FloatMenuOption> options = new List<FloatMenuOption>();

                    // "Keep" option: restores the original (generic stays "Any Ammo", concrete stays itself).
                    string keepLabel = local.IsGeneric
                        ? "CER_MassReplaceAmmo_KeepGeneric".Translate(local.originalLabel)
                        : "CER_MassReplaceAmmo_KeepConcrete".Translate(local.originalLabel);
                    options.Add(new FloatMenuOption(keepLabel, () => { local.selectedGeneric = null; local.selectedAmmo = local.IsGeneric ? null : local.originalAmmo; }));

                    // Generic "Any Ammo for <weapon>" for each weapon in this loadout that uses the same caliber.
                    HashSet<LoadoutGenericDef> addedGenerics = new HashSet<LoadoutGenericDef>();
                    foreach (LoadoutSlot ws in local.loadout.Slots)
                    {
                        if (ws.thingDef == null || !ws.thingDef.IsWeapon) continue;
                        if (LoadoutUtility.GetAmmoSet(ws.thingDef) != local.ammoSet) continue;
                        LoadoutGenericDef gen = LoadoutUtility.GetGenericAmmoDef(ws.thingDef);
                        if (gen == null || !addedGenerics.Add(gen)) continue;
                        LoadoutGenericDef genLocal = gen;
                        options.Add(new FloatMenuOption("CER_MassReplaceAmmo_AnyAmmoFor".Translate(ws.thingDef.label), () => { local.selectedGeneric = genLocal; local.selectedAmmo = null; }));
                    }

                    foreach (AmmoLink link in local.ammoSet.ammoTypes)
                    {
                        if (link.ammo == null) continue;

                        bool isValid = true;
                        if (currentFilter == AmmoFilter.Available)
                        {
                            // Dialog_AssignLoadouts.AmmoProducibleNow (2026-08-01, reused here) - GetWorkbenchesFor
                            // alone only lists bench DEFS eligible in principle, never intersected with benches
                            // the colony actually has built, so this used to claim "Available" for ammo with zero
                            // matching workbenches anywhere in the colony.
                            isValid = Dialog_AssignLoadouts.AmmoProducibleNow(link.ammo);
                        }
                        else if (currentFilter == AmmoFilter.Researched)
                        {
                            isValid = LoadoutUtility.IsAmmoResearched(link.ammo);
                        }

                        if (isValid)
                        {
                            ThingDef ammoToSelect = link.ammo;
                            options.Add(new FloatMenuOption(ammoToSelect.label, () => { local.selectedGeneric = null; local.selectedAmmo = ammoToSelect; }));
                        }
                    }
                    if (options.Count == 1)
                    {
                        options.Add(new FloatMenuOption("CER_MassReplaceAmmo_NoValidAmmo".Translate(), null));
                    }
                    Find.WindowStack.Add(new FloatMenu(options));
                }

                rowY += 30f;
            }
            Widgets.EndScrollView();

            // Bottom bar: Cancel pinned left, the primary action pinned right (same as every other popup).
            if (LoadoutUtility.BottomCloseButton(inRect, "CER_MassReplaceAmmo_CancelButton".Translate()))
            {
                this.Close();
            }

            float xr = inRect.width;
            if (LoadoutUtility.BottomActionButton(inRect, ref xr, LoadoutUtility.PrimaryBtnW, "CER_MassReplaceAmmo_ApplyButton".Translate()))
            {
                ApplyChanges();
                this.Close();
            }
        }

        // Wie viele der ueberhaupt vorkommenden Munitionssorten der aktuelle Filter zur Auswahl zulaesst.
        // Dieselben drei Bedingungen wie im Auswahlmenue unten - bewusst dieselbe Rechnung, sonst nennt die
        // Zahl etwas anderes, als das Menue dann anbietet. Nur neu gerechnet, wenn der Filter sich geaendert
        // hat: AmmoProducibleNow geht ueber die gebauten Werkbaenke der Kolonie.
        private void RefreshChoiceCount()
        {
            if (choiceCountFor == currentFilter) return;
            choiceCountFor = currentFilter;
            HashSet<ThingDef> seen = new HashSet<ThingDef>();
            int allowed = 0, total = 0;
            foreach (ReplaceableAmmoSlot s in allSlots)
            {
                if (s.ammoSet?.ammoTypes == null) continue;
                foreach (AmmoLink link in s.ammoSet.ammoTypes)
                {
                    if (link.ammo == null || !seen.Add(link.ammo)) continue;
                    total++;
                    bool ok = currentFilter == AmmoFilter.Available ? Dialog_AssignLoadouts.AmmoProducibleNow(link.ammo)
                        : currentFilter == AmmoFilter.Researched ? LoadoutUtility.IsAmmoResearched(link.ammo)
                        : true;
                    if (ok) allowed++;
                }
            }
            choiceCountAllowed = allowed;
            choiceCountTotal = total;
        }

        private void ApplyChanges()
        {
            int changedCount = 0;
            
            // Group by loadout
            var loadoutGroups = allSlots.GroupBy(s => s.loadout);
            foreach (var loadoutGroup in loadoutGroups)
            {
                Loadout loadout = loadoutGroup.Key;
                List<ReplaceableAmmoSlot> changedSlots = loadoutGroup.Where(s => s.IsChanged).ToList();
                if (changedSlots.Count == 0) continue;

                // Remove only the slots that actually changed (concrete or generic), keep everything else untouched.
                foreach (var rSlot in changedSlots)
                {
                    loadout.RemoveSlot(rSlot.slot);
                }

                // Add newly chosen concrete ammo, deduped, taking MAX count to prevent accumulation. Also
                // merges against any UNCHANGED slot already sitting on this loadout with the same resolved
                // type (2026-08-02 fix: a weapon redirected to an ammo type another, untouched
                // slot on the SAME loadout already used produced two separate LoadoutSlot objects for the
                // same def - every def-keyed slot lookup elsewhere in the mod only ever finds the first one,
                // leaving the second an invisible stale duplicate carrying real demand nobody could see or edit).
                // OwnSlots, NICHT Slots (2026-08-09). Slots ist die Vereinigung aus eigenen und GEERBTEN
                // Eintraegen, RemoveSlot fasst aber nur die eigenen an. Ein Treffer auf einem geerbten Eintrag
                // lief also ins Leere - entfernt wurde nichts, und direkt danach kam per AddSlot ein zweiter
                // Eintrag derselben Sorte dazu. Ergebnis: dieselbe Munition zweimal im Loadout, und jede
                // def-basierte Suche im Rest des Mods findet nur die erste davon.
                // Genau diese Falle steht schon im Klassenkommentar oben als Grund, warum der SUCHLAUF auf
                // OwnSlots beschraenkt wurde - das Uebernehmen hat dieselbe Behandlung nie bekommen.
                // Fachlich ist das auch die richtige Lesart: aendert man geerbte Munition, soll
                // ein PERSOENLICHER Eintrag entstehen, der die Vererbung ersetzt. Genau das passiert jetzt -
                // der geerbte Eintrag bleibt unangetastet, der neue eigene ueberschreibt ihn.
                foreach (var ammoGroup in changedSlots.Where(s => s.selectedAmmo != null).GroupBy(s => s.selectedAmmo))
                {
                    int count = ammoGroup.Max(s => s.slot.count);
                    LoadoutSlot existing = loadout.OwnSlots?.FirstOrDefault(s => s.thingDef == ammoGroup.Key);
                    if (existing != null) { count = Mathf.Max(count, existing.count); loadout.RemoveSlot(existing); }
                    loadout.AddSlot(new LoadoutSlot(ammoGroup.Key, count));
                }

                // Same merge for generic "Any Ammo for X" placeholders.
                foreach (var genGroup in changedSlots.Where(s => s.selectedGeneric != null).GroupBy(s => s.selectedGeneric))
                {
                    int count = genGroup.Max(s => s.slot.count);
                    LoadoutSlot existing = loadout.OwnSlots?.FirstOrDefault(s => s.genericDef == genGroup.Key);
                    if (existing != null) { count = Mathf.Max(count, existing.count); loadout.RemoveSlot(existing); }
                    loadout.AddSlot(new LoadoutSlot(genGroup.Key, count));
                }

                changedCount++;
            }

            if (changedCount > 0)
            {
                Messages.Message("CER_MassReplaceAmmo_UpdatedMessage".Translate(changedCount.ToString()), MessageTypeDefOf.TaskCompletion, false);
            }
        }
    }
}
