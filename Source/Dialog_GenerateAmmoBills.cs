using System;
using System.Linq;
using System.Collections.Generic;
using RimWorld;
using Verse;
using CombatExtended;
using UnityEngine;

namespace CEReloaded
{
    public class Dialog_GenerateAmmoBills
    {
        private static Texture2D IconTrash => LoadoutUtility.TexTrash;

        public Window parentWindow;
        private List<Building_WorkTable> ammoBenches = new List<Building_WorkTable>();
        
        // FIX: Renamed from AmmoFilterMode to AmmoBillFilterMode to avoid collision with namespace-level enum
        private AmmoBillFilterMode filterMode;
        private Vector2 scrollPosition;
        private string ammoTypeFilter = "All";
        private string benchFilter = "All benches";
        private string nameFilter = "";

        // Scope + cleanup for "Auto-generate missing" specifically - independent of the browsing filterMode
        // above (that one just decides what the TABLE shows; these decide what the button actually creates/
        // removes, and are surfaced explicitly rather than silently inheriting whatever filter happens to be
        // selected right now).

        private enum SortMode
        {
            Name,
            Special,
            Assigned,
            BillStatus,
            Loadouts
        }
        private SortMode currentSortMode = SortMode.Name;
        private bool sortDescending = false;
        
        private class ActiveBillInfo
        {
            public Building_WorkTable bench;
            public Bill_Production bill;
            public string label;
            // Nur die Menge ("5", "x3", "Forever") - fuer die schmale Auftragsspalte der Waffen-Ansicht,
            // wo der Bankname nicht danebenpasst und stattdessen im Tooltip steht. Die Munitionsseite
            // benutzt weiter ihr label und laesst dieses Feld leer.
            public string amount;
            // "stelle her bis X" (Unendlichkeitszeichen) statt "mache N Stueck" (x1).
            public bool isUntil;
        }
        private class AmmoEntry
        {
            public ThingDef ammoDef;
            public RecipeDef recipeDef;
            // ABSOLUTE Menge (2026-08-30): was ein Klick auf eine Werkbank dem Auftrag hinzufuegt. Vorher stand hier
            // die Zahl der Chargen und daneben "= 500"; jetzt steht die 500 direkt drin und ist eintippbar.
            public string amountString = "1";
            public int yield;
            public bool isResearched;
            public string ammoType;
            public List<string> loadouts = new List<string>();
            public List<Loadout> loadoutRefs = new List<Loadout>();   // same order as loadouts; for the contents tooltip
            public int assignedPawns;          // how many colonists carry a loadout that includes this ammo
            public int assignedDemand;         // summed rounds those colonists' loadouts specify (rations x assignees)
            // Some loadout asks for "any ammo of this caliber" and would ACCEPT this variant, even though the
            // default pick resolved to a sibling so assignedDemand stayed 0. Only gates the obsolete/unneeded
            // question, never the production quantity - see LoadoutUtility.CollectGenericAmmoFamilies.
            public bool genericallyCovered;
            // ALLE laufenden Auftraege dieser Sorte, beide Arten - die Art steht je Zeile als Symbol (2026-08-30).
            public List<ActiveBillInfo> activeBills = new List<ActiveBillInfo>();
            public List<Building_WorkTable> validBenches = new List<Building_WorkTable>();
            public List<ThingDefCountClass> ings = new List<ThingDefCountClass>();   // sorted recipe ingredients (icons)
        }
        private List<AmmoEntry> ammoEntries = new List<AmmoEntry>();
        // Sicht-Cache fuer die gefilterte+sortierte Munitionsliste (2026-08-15). ammoEntries wird an Ort
        // und Stelle neu gefuellt, die Referenz aendert sich also
        // nie - deshalb ein Zaehler statt eines Referenzvergleichs.
        private int ammoEntriesVersion;
        private List<AmmoEntry> visibleCache;
        private string visibleCacheKey;
        private float visibleCacheTime = -99f;
        // Dasselbe fuer die Waffenuebersicht. Deren Datenblock (overviewWeapons/woInfo) hatte laengst
        // einen 1,5s-Cache, die Auswahl darauf lief trotzdem pro Bild.
        private List<ThingDef> wovVisCache;
        private string wovVisCacheKey;

        // Ammo-tab column layout, shared by the header row and the body so the two never drift apart.
        // Special/Assig. are 20% wider than before; an Ingredients column now sits between Assig. and Active Bills.
        // AbAssigX/W (2026-08-01) now hosts the shared Availability bar instead of a bare
        // colored-dot + count - 20px wider (70 -> 90, matching the bar's own convention elsewhere, e.g.
        // Dialog_SquadSetup's GenAvailColW) to fit the segmented bar properly; every column after it shifted
        // +20f to keep the same gaps.
        // Widened again 90 -> 130 (2026-08-01, same-day follow-up) now that the Loadouts
        // column is a compact circle+count (not a wide name list) and no longer needs 100px - narrowed
        // 100 -> 50 to give the room back to Availability. Net effect on everything from Ingredients
        // onward: +40 up through AbLoadoutX, then -50 from AbLoadoutW's own shrink for AbBatchX (net -10
        // from the original 876, so the Batches column actually gained a little breathing room, not lost any).
        // Namensspalte +44 (2026-08-22: fuenf Zeilen hintereinander lauteten
        // alle ".30-06 Springfie...", die Sortenangabe in Klammern fiel weg und war nur noch per
        // Mouseover zu erfahren). Die Zahl ist nicht geraten: das sichtbare Namensfeld ist 115 Punkte
        // breit (Zeile 65..180, siehe labelRect), das reicht fuer rund 18 Zeichen - ".30-06 Springfield
        // (AP)" hat 23. Mit 159 Punkten passen rund 25.
        //
        // NACHGEBESSERT 2026-08-23, weil derselbe Test erneut ROT kam: das Verbreitern allein hat gar
        // nichts geaendert, und zwar aus zwei unabhaengigen Gruenden - beide sind die Hausfehlerklasse
        // "eine Frage, zwei Rechnungen":
        // (1) Der NAME wurde weiterhin bei 115 Punkten abgeschnitten. Die Breite stand zweimal da: einmal
        //     als labelRect (jetzt 159) und einmal als feste 115 im Truncate-Aufruf daneben. Nur die
        //     erste wurde mitgezogen, also blieb ".30-06 Springfie..." exakt so stehen wie vorher.
        //     Truncate leitet sich jetzt aus labelRect.width ab - eine Zahl, eine Quelle.
        // (2) Am Zeilenende lagen die 44 Punkte NICHT brach. Der Renderer hat 971 gemeldet, das Modell
        //     dort setzte das Ertrags-Feld aber bei AbBatchX + 45 an; im Code steht es hinter Knopf (34)
        //     + Luft (4) + Eingabefeld (40) + Luft (5), also bei AbBatchX + 83. Echte rechte Kante vor
        //     der Verschiebung: 1009 von 1016 - sieben Punkte Luft, nicht 45. Mit +44 stand sie bei
        //     1053, und genau das war im Test zu sehen ("= 5(" statt "= 500", Ueberschrift zu
        //     "Loadou" gekuerzt). Das Modell im Layout-Renderer ist mitkorrigiert worden.
        // Die 44 kommen deshalb jetzt aus zwei Spalten, die sie wirklich uebrig haben, und keine Spalte
        // wird unter ihren Inhalt gekuerzt:
        //   Auftragsspalte 150 -> 120: Inhalt ist Muelleimer (20) + Werkbanksymbol bei +25 + Text bei
        //     +48; der Text hat damit noch 68 Punkte fuer Kurzform plus Menge ("#2 (500)").
        //   Generieren-Spalte 118 -> 106: Inhalt ist Werkbankknopf (54) + Lupe bei +58 (20), endet also
        //     bei 78; die Ersatztexte ("No bench", "Not researched") bekommen die vollen 106.
        // Neue rechte Kante: 1007 von 1016. Die Auftragsspalte behaelt bewusst mehr als ihr Inhalt
        // braucht, weil ihre Ueberschrift ("Active bills 'until'") laenger ist als jede Zelle darunter.
        // REIHENFOLGE seit 2026-08-30: Name | Special | Verfuegbarkeit | Beladungen |
        // Zutaten | Auftraege "until" | Auftraege "once" | Menge | Erzeugen auf. Die Menge steht direkt vor dem
        // Erzeugen-Knopf, weil sie sagt, was ein Klick darauf hinzufuegt; Erzeugen ist die letzte Spalte.
        private const float AbNameX = 30f, AbNameW = 194f;
        private const float AbSpecialX = 234f, AbSpecialW = 66f;
        private const float AbAssigX = 306f, AbAssigW = 130f;
        private const float AbLoadoutX = 442f, AbLoadoutW = 50f;
        private const float AbIngX = 498f, AbIngW = 122f;
        // EINE Auftragszeile fuer beide Arten (2026-08-30): "active bills until" und "active bills" in einer
        // Spalte, mit Unendlichkeits- bzw. x1-Zeichen. Die Art steht als
        // Symbol in der Zeile, nicht mehr in der Spaltenwahl.
        private const float AbActiveX = 626f, AbActiveW = 150f;
        private const float AbAmountX = 782f, AbAmountW = 56f;
        // Waffen-Ansicht: die Auftragsspalte beginnt hinter der Bankspalte (842 + 78 + 6). Sie nimmt den
        // Rest der Zeile ein - bei der Vorgabebreite des Fensters (Ansichtsbreite 1016) sind das 88 Pixel.
        // WovActiveMinW ist die Notbremse fuer kleine Bildschirme: passt nicht einmal das hinein, entfaellt
        // die Spalte ganz, statt aus dem sichtbaren Bereich zu ragen. Der Knopf "Alle Waffenauftraege
        // loeschen" unten bleibt dann der Weg. Gehoert zum offenen Punkt ueber schmale Bildschirme.
        // Auftragsspalte VOR der Menge (2026-08-30) und fest 110 breit statt "der Rest der Zeile" -
        // rechts stehen jetzt zwei Werkbank-Knoepfe. Die Art des Auftrags sagt das Symbol in der Zeile.
        private const float WovActiveX = 658f, WovActiveW = 110f;
        // AbGenW ist neu (2026-08-23): die Breite dieser Spalte stand vorher dreimal als rohe Zahl da
        // (110 in der Ueberschrift, 116 zweimal an den Ersatztexten) und keine der drei kannte die
        // anderen - beim Verschieben waere genau das wieder auseinandergelaufen.
        // Zwei Werkbank-Knoepfe je Bank: x1 (Einzelauftrag) zuerst, dann Unendlichkeit (stelle her bis) - dieselbe
        // Reihenfolge wie in der Waffen-Ansicht: der Einzelauftrag steht an erster Stelle.
        private const float AbGenX = 846f, AbGenW = 164f;
        
        public Dialog_GenerateAmmoBills()
        {
            filterMode = RoleUtility.Ui().abFilter;
            RefreshBenchesAndAmmo();
        }
        
        private HashSet<ThingDef> GetRelevantAmmoDefs()
        {
            HashSet<ThingDef> relevantAmmo = new HashSet<ThingDef>();
            
            if (filterMode == AmmoBillFilterMode.AllAmmunition)
            {
                foreach (ThingDef w in DefDatabase<ThingDef>.AllDefs.Where(d => d.IsRangedWeapon))
                {
                    CompProperties_AmmoUser ammoProps = w.GetCompProperties<CompProperties_AmmoUser>();
                    if (ammoProps != null && ammoProps.ammoSet != null)
                    {
                        foreach (var ammoLink in ammoProps.ammoSet.ammoTypes)
                        {
                            if (ammoLink.ammo != null)
                            {
                                relevantAmmo.Add(ammoLink.ammo);
                            }
                        }
                    }
                }
                return relevantAmmo;
            }
            
            if (filterMode == AmmoBillFilterMode.ActiveBills)
            {
                {
                    foreach (Building_WorkTable bench in LoadoutUtility.AllColonyBenches())
                    {
                        if (bench.billStack != null)
                        {
                            foreach (Bill bill in bench.billStack.Bills)
                            {
                                Bill_Production bp = bill as Bill_Production;
                                if (bp != null && bp.recipe != null && bp.recipe.products != null && bp.recipe.products.Any())
                                {
                                    ThingDef product = bp.recipe.products[0].thingDef;
                                    if (product != null && typeof(CombatExtended.AmmoThing).IsAssignableFrom(product.thingClass))
                                    {
                                        relevantAmmo.Add(product);
                                    }
                                }
                            }
                        }
                    }
                }
            }
            else if (filterMode == AmmoBillFilterMode.AvailableWeapons)
            {
                List<ThingDef> owned = LoadoutUtility.GetAllOwnedWeapons();
                foreach (ThingDef w in owned)
                {
                    if (w.IsRangedWeapon)
                    {
                        CompProperties_AmmoUser ammoProps = w.GetCompProperties<CompProperties_AmmoUser>();
                        if (ammoProps != null && ammoProps.ammoSet != null)
                        {
                            foreach (AmmoLink link in ammoProps.ammoSet.ammoTypes)
                            {
                                if (link.ammo != null) relevantAmmo.Add(link.ammo);
                            }
                        }
                    }
                }
            }
            else
            {
                // FIX: Use LoadoutUtility.GetAssignedLoadouts() instead of non-existent LoadoutManager.AssignedLoadouts
                IEnumerable<Loadout> loadoutsToScan = (filterMode == AmmoBillFilterMode.AssignedLoadouts) 
                    ? LoadoutUtility.GetAssignedLoadouts()
                    : (IEnumerable<Loadout>)LoadoutManager.Loadouts;
                    
                foreach (Loadout loadout in loadoutsToScan)
                {
                    if (loadout.Slots == null) continue;
                    foreach (LoadoutSlot slot in loadout.Slots)
                    {
                        if (slot.thingDef != null && typeof(CombatExtended.AmmoThing).IsAssignableFrom(slot.thingDef.thingClass))
                        {
                            relevantAmmo.Add(slot.thingDef);
                        }
                        else if (slot.genericDef != null)
                        {
                            ThingDef gun = LoadoutUtility.GetWeaponForGenericAmmo(slot.genericDef);
                            foreach (ThingDef ammo in LoadoutUtility.GetAmmoTypesFor(gun))
                                relevantAmmo.Add(ammo);
                        }
                    }
                }
            }

            // Any ammo type with a REAL active bill somewhere is always relevant, no matter what the current
            // filter's own definition of "relevant" says - otherwise RefreshAmmoList never creates an
            // AmmoEntry for it at all (it only creates one for a product already IN relevantAmmo), which
            // makes a bill orphaned by a changed/removed assignment permanently invisible to both "Prune
            // unneeded ammo bills" and "Delete ammo bills" - exactly the case those two actions most need to
            // see (this was the root cause of "Delete ammo bills" silently leaving bills behind under the
            // default Assigned-loadouts filter).
            // MUNITION MIT AUFTRAG NUR DORT, WO DER FILTER SIE MEINT (2026-08-30): unter dem Filter
            // "Assigned loadouts" erschienen auch Sorten, die niemandem zugewiesen sind. Ursache: dieser
            // Block lief bisher IMMER und hat
            // jede Sorte hereingeholt, fuer die irgendwo ein Auftrag laeuft - damit zeigte 'Assigned loadouts' Zeilen
            // mit 'Loadouts: None'. Ein Filter, der etwas anderes zeigt als er sagt, ist keiner.
            // Verloren geht dadurch nichts: fuer laufende Auftraege gibt es den eigenen Filter 'Active bills', und
            // ueberfluessige findet 'Optimize ammo bills' bzw. 'Remove unneeded bills'.
            if (filterMode == AmmoBillFilterMode.ActiveBills || filterMode == AmmoBillFilterMode.AllAmmunition)
                foreach (Building_WorkTable bench in LoadoutUtility.AllColonyBenches())
                {
                    if (bench.billStack == null) continue;
                    foreach (Bill bill in bench.billStack.Bills)
                    {
                        if (bill is Bill_Production bp && bp.recipe?.products != null && bp.recipe.products.Count == 1)
                        {
                            ThingDef product = bp.recipe.products[0].thingDef;
                            if (product != null && typeof(CombatExtended.AmmoThing).IsAssignableFrom(product.thingClass))
                                relevantAmmo.Add(product);
                        }
                    }
                }

            return relevantAmmo;
        }
        
        private bool HasValidEntries(Building_WorkTable bench, HashSet<ThingDef> relevantAmmo)
        {
            foreach (RecipeDef r in bench.def.AllRecipes)
            {
                if (r.products != null && r.products.Count == 1)
                {
                    ThingDef product = r.products[0].thingDef;
                    bool isAmmo = (relevantAmmo == null) 
                        ? typeof(CombatExtended.AmmoThing).IsAssignableFrom(product.thingClass)
                        : relevantAmmo.Contains(product);
                        
                    if (isAmmo)
                    {
                        bool researched = LoadoutUtility.RecipeIsResearched(r);

                        bool showUnresearched = filterMode == AmmoBillFilterMode.AssignedLoadouts;
                        if (!researched && !showUnresearched) continue;

                        return true;
                    }
                }
            }
            return false;
        }

        // JEDER Klick in diesem Fenster laesst ALLES neu rechnen (2026-08-30): Bill- und Ammo-Uebersicht
        // aktualisierten sich traege - egal welcher Knopf gedrueckt wird, alle Elemente muessen sofort
        // neu rechnen.
        // Bewusst NICHT knopfweise: dieses Fenster hat Dutzende Knoepfe (Muelleimer je Auftrag, Werkbank-Knoepfe,
        // Haken, Filter, Sortierkoepfe, Fussleiste), und es gilt ausdruecklich "egal welcher". Deshalb eine
        // Stelle: hat das Ereignis dieses Bildes ein Maus-Ereignis VERBRAUCHT (Event.current.type == Used), hat also
        // irgendein Bedienelement zugeschlagen - dann faellt jeder Zwischenspeicher dieses Fensters. Tastatureingaben
        // sind absichtlich aussen vor, sonst rechnete jeder Tastendruck im Mengenfeld die ganze Liste neu.
        private void RefreshAllAfterClick()
        {
            LoadoutUtility.InvalidateAmmoOwnership();
            CombatReadiness.Invalidate();
            RefreshBenchesAndAmmo();
            visibleCache = null;
            overviewWeapons = null;   // Waffen-Ansicht haengt an ihrem eigenen Zwischenspeicher
        }

        public void RefreshBenchesAndAmmo()
        {
            HashSet<ThingDef> relevantAmmo = GetRelevantAmmoDefs();
            
            ammoBenches = LoadoutUtility.AllColonyBenches()
                .Where(b => HasValidEntries(b, relevantAmmo))
                .OrderBy(b => b.thingIDNumber)
                .ToList();
            RebuildBenchLabelCache();

            RefreshAmmoList(relevantAmmo);
        }
        
        // Disambiguated bench labels/ids ("Machining table", or "Machining table 2" + id "#2" when the map
        // has several of the same bench def) - built once whenever ammoBenches itself is rebuilt instead of
        // recomputing a Count()+Where().ToList()+IndexOf() over it on every single GetBenchLabel/GetBenchId
        // call. Those calls happen per visible row per bench per frame while this window is open, so what
        // was a handful of full-list scans is now two dictionary lookups.
        private readonly Dictionary<Building_WorkTable, string> benchLabelCache = new Dictionary<Building_WorkTable, string>();
        private readonly Dictionary<Building_WorkTable, string> benchIdCache = new Dictionary<Building_WorkTable, string>();

        private void RebuildBenchLabelCache()
        {
            benchLabelCache.Clear();
            benchIdCache.Clear();
            foreach (var grp in ammoBenches.GroupBy(b => b.def.label))
            {
                List<Building_WorkTable> sameType = grp.ToList();
                if (sameType.Count <= 1)
                {
                    benchLabelCache[sameType[0]] = sameType[0].def.label;
                    benchIdCache[sameType[0]] = "";
                    continue;
                }
                for (int i = 0; i < sameType.Count; i++)
                {
                    benchLabelCache[sameType[i]] = sameType[i].def.label + " " + (i + 1);
                    benchIdCache[sameType[i]] = "#" + (i + 1);
                }
            }
        }

        private string GetBenchLabel(Building_WorkTable bench)
        {
            if (bench == null) return "CER_GenerateAmmoBills_BenchLabelNone".Translate();
            return benchLabelCache.TryGetValue(bench, out string s) ? s : bench.def.label;
        }

        // Just the disambiguating id ("#2") for a bench when duplicates of its def exist; "" for a unique bench.
        private string GetBenchId(Building_WorkTable bench)
        {
            if (bench == null) return "";
            return benchIdCache.TryGetValue(bench, out string s) ? s : "";
        }

        private void RefreshAmmoList(HashSet<ThingDef> providedAmmo = null)
        {
            HashSet<ThingDef> relevantAmmo = providedAmmo ?? GetRelevantAmmoDefs();
            
            Dictionary<ThingDef, string> previousBatches = new Dictionary<ThingDef, string>();
            foreach (var e in ammoEntries)
            {
                if (!string.IsNullOrEmpty(e.amountString))
                {
                    previousBatches[e.ammoDef] = e.amountString;
                }
            }

            ammoEntries.Clear();
            // Zaehler fuer den Sicht-Cache unten (2026-08-15): die Liste
            // wird an Ort und Stelle geleert und neu gefuellt, ihre Referenz aendert sich also NIE - ein
            // Cache, der sich auf die Referenz verliesse, wuerde die neuen Eintraege nicht bemerken.
            ammoEntriesVersion++;
            ammoTypeFilter = "All";
            
            foreach (RecipeDef r in DefDatabase<RecipeDef>.AllDefs)
            {
                if (r.products != null && r.products.Count == 1)
                {
                    ThingDef product = r.products[0].thingDef;
                    bool isAmmo = (relevantAmmo == null) 
                        ? typeof(CombatExtended.AmmoThing).IsAssignableFrom(product.thingClass)
                        : relevantAmmo.Contains(product);
                        
                    if (isAmmo)
                    {
                        AmmoEntry entry = ammoEntries.FirstOrDefault(e => e.ammoDef == product);
                        if (entry == null)
                        {
                            bool researched = LoadoutUtility.RecipeIsResearched(r);

                            bool showUnresearched = filterMode == AmmoBillFilterMode.AssignedLoadouts;
                            if (!researched && !showUnresearched)
                            {
                                continue;
                            }
                            
                            string label = product.label;
                            int start = label.LastIndexOf('(');
                            int end = label.LastIndexOf(')');
                            string type = "Generic";
                            if (start != -1 && end != -1 && end > start)
                            {
                                type = label.Substring(start + 1, end - start - 1);
                            }
                            
                            // Vorgabe = Chargen x Ertrag, also die Zahl, die frueher rechts als "= 500" stand.
                            string defBatchStr = RoleUtility.Ui()?.abDefaultBatches
                                ?? (CEReloadedMod.settings != null ? CEReloadedMod.settings.defAmmoBatches : "1");
                            int defBatches = int.TryParse(defBatchStr, out int dbParsed) ? Mathf.Max(1, dbParsed) : 1;
                            int recipeYield = r.products[0].count;
                            string initBatch = (defBatches * Mathf.Max(1, recipeYield)).ToString();
                            if (previousBatches.ContainsKey(product))
                            {
                                initBatch = previousBatches[product];
                            }

                            entry = new AmmoEntry
                            {
                                ammoDef = product,
                                recipeDef = r,
                                yield = r.products[0].count,
                                isResearched = researched,
                                ammoType = type,
                                amountString = initBatch,
                                ings = LoadoutUtility.GetRecipeIngredients(r)
                            };
                            ammoEntries.Add(entry);
                        }
                        
                        foreach (Building_WorkTable bench in ammoBenches)
                        {
                            if (bench.def.AllRecipes.Contains(r) && !entry.validBenches.Contains(bench))
                            {
                                if (CEReloadedMod.settings != null && CEReloadedMod.settings.hideDuplicateBenches)
                                {
                                    if (entry.validBenches.Any(b => b.def == bench.def)) continue;
                                }
                                entry.validBenches.Add(bench);
                            }
                        }
                    }
                }
            }
            
            // Map each loadout label to the colonists currently assigned it, so the Assigned column can show who actually carries this ammo.
            // Every colony map plus every player-controlled caravan - not just whoever is on the viewed map.
            Dictionary<string, List<string>> loadoutPawns = new Dictionary<string, List<string>>();
            foreach (Pawn pawn in LoadoutUtility.AllColonyPawns())
            {
                // Unwrapped past the hidden per-pawn "[Pawn-Loadout]" anchor - a personalized pawn (packs,
                // sidearm(s), extra ammo, basically everyone now) is actually SetLoadout-assigned to
                // that anchor, not their real template. Keying this dict by the RAW label meant the
                // lookup below (by the real template's label) never matched for any of them - their
                // ammo demand silently vanished from the assigned-demand count entirely.
                Loadout lo = AmmoAssignmentUtility.DisplayLoadout(pawn.GetLoadout());
                if (lo == null || lo.defaultLoadout) continue;
                if (!loadoutPawns.TryGetValue(lo.label, out List<string> names))
                {
                    names = new List<string>();
                    loadoutPawns[lo.label] = names;
                }
                names.Add(pawn.LabelShort);
            }

            foreach (AmmoEntry entry in ammoEntries)
            {
                entry.loadouts.Clear();
                entry.loadoutRefs.Clear();
                entry.genericallyCovered = false;
                Dictionary<string, int> loadoutRounds = new Dictionary<string, int>();   // rounds of THIS ammo per loadout label
                foreach (Loadout loadout in LoadoutManager.Loadouts)
                {
                    if (loadout.Slots == null) continue;
                    // Rounds come from THIS loadout's own live Slots (own+inherited union) - whatever object
                    // was actually iterated, anchor or template - not the resolved template's raw baseline
                    // (2026-08-01 fix, corrected same day: a pawn's PERSONAL ammo override lives only on
                    // their own anchor, never written back to the shared template, and is exactly what makes
                    // GetRelevantAmmoDefs' AssignedLoadouts filter show this ammo at all - that filter reads
                    // anchors' own live Slots the same way. Reading only the resolved template's Slots here
                    // made every such row wrongly show "None" under that filter.)
                    int rounds = 0;
                    foreach (LoadoutSlot slot in loadout.Slots)
                    {
                        if (slot.thingDef == entry.ammoDef) rounds += slot.count;
                        // A still-unresolved generic "Any ammo for X" slot that would DEFAULT-resolve to this
                        // exact entry counts too (2026-08-02 fix: a caliber with two variants'
                        // bills active, only one flagged obsolete even though neither concrete type was
                        // demanded anymore - the real remaining demand was a pawn still on the generic
                        // placeholder, invisible here since it only ever checked slot.thingDef).
                        else if (slot.genericDef != null && slot.genericDef.defName != null
                            && slot.genericDef.defName.StartsWith(LoadoutUtility.GenericAmmoPrefix))
                        {
                            ThingDef gun = LoadoutUtility.GetWeaponForGenericAmmo(slot.genericDef);
                            List<ThingDef> fam = LoadoutUtility.GetAmmoTypesFor(gun).Where(LoadoutUtility.IsAmmoResearched).ToList();
                            if (fam.Count > 0 && LoadoutUtility.PickDefaultAmmo(fam.Cast<Def>().ToList()) == entry.ammoDef)
                                rounds += slot.count;
                            // ...and even when it resolved to a SIBLING, this slot still accepts our variant, so
                            // our bill is not obsolete (2026-08-09 fix, same report as the Supply popup's own
                            // version). Whole family, not just the researched ones - the flag only ever protects
                            // a bill from the delete list, see LoadoutUtility.CollectGenericAmmoFamilies.
                            if (LoadoutUtility.GetAmmoTypesFor(gun).Contains(entry.ammoDef))
                                entry.genericallyCovered = true;
                        }
                    }
                    if (rounds <= 0) continue;
                    // Resolve to the real template for DISPLAY/dedup ONLY (2026-08-01) - iterating
                    // LoadoutManager.Loadouts directly used to add the ANCHOR itself here, one per
                    // personalized pawn (virtually everyone now), instead of one row per actual reusable
                    // template: it inflated this column to one entry PER PAWN and leaked the anchor's raw
                    // internal "[Pawn-Loadout] <id>" label into the player-facing Loadouts column. First
                    // Loadout object (anchor or template) to report this ammo for a given resolved name wins -
                    // same "first wins, no summing" simplicity the pre-fix code always had, just keyed by the
                    // resolved display name instead of the raw (possibly anchor) label.
                    Loadout real = AmmoAssignmentUtility.DisplayLoadout(loadout);
                    if (real == null || real.defaultLoadout || AmmoAssignmentUtility.IsAmmoLink(real)) continue;
                    if (entry.loadouts.Contains(real.label)) continue;
                    entry.loadouts.Add(real.label);
                    entry.loadoutRefs.Add(real);
                    loadoutRounds[real.label] = rounds;
                }

                entry.assignedPawns = 0;
                entry.assignedDemand = 0;
                // Dieselbe Reservekurve wie ueberall sonst (2026-08-02 gefixt): hier wurde vorher flach
                // aufsummiert (Kolonisten x Schuss), waehrend JEDER andere Weg zu einem Bill-Ziel ueber
                // LoadoutUtility.ComputeAmmoNeedForLoadouts -> WeightedAmmoDemand laeuft. Das zaehlt per
                // Design nur die ersten paar Kolonisten voll und faellt danach ab, weil ein "stelle her
                // bis X"-Auftrag nur den LAGERbestand zaehlt und getragene Munition nicht.
                // Folge war: 6 Kolonisten x 200 Schuss ergaben ueber den Nachschub-Popup ~660, ueber das
                // Auto-generate in diesem Tab (und damit ueber Arm my Colony) 1200. Zwei Knoepfe, doppelt
                // verschiedenes Ergebnis, ohne dass das UI es erklaert.
                List<int> perPawn = new List<int>();
                foreach (string loLabel in entry.loadouts)
                {
                    if (loadoutPawns.TryGetValue(loLabel, out List<string> pw) && pw.Count > 0)
                    {
                        entry.assignedPawns += pw.Count;
                        int rounds = loadoutRounds.TryGetValue(loLabel, out int rr) ? rr : 0;
                        for (int k = 0; k < pw.Count; k++) perPawn.Add(rounds);
                    }
                }
                entry.assignedDemand = perPawn.Count > 0 ? LoadoutUtility.WeightedAmmoDemand(perPawn) : 0;

                entry.activeBills.Clear();
                foreach (Building_WorkTable bench in LoadoutUtility.AllColonyBenches())
                {
                    if (bench.billStack == null) continue;
                    string benchLabel = GetBenchLabel(bench);
                    foreach (Bill_Production bp in bench.billStack.Bills.OfType<Bill_Production>())
                    {
                        if (bp.recipe == null || bp.recipe.products == null || !bp.recipe.products.Any(pr => pr.thingDef == entry.ammoDef)) continue;
                        bool until = LoadoutUtility.IsUntilBill(bp);
                        if (!until && bp.repeatMode != BillRepeatModeDefOf.RepeatCount) continue;   // 'Forever' hat keine Menge
                        entry.activeBills.Add(new ActiveBillInfo {
                            bench = bench, bill = bp, isUntil = until,
                            // Menge immer in STUECK (2026-08-30): ein Einzelauftrag ueber 1 Durchgang macht bei Ertrag 500
                            // eben 500 Schuss - 'x1' waere die Zahl der Durchgaenge und liest sich wie ein Stueck.
                            amount = (until ? bp.targetCount : bp.repeatCount * Mathf.Max(1, bp.recipe.products[0].count)).ToString(),
                            label = benchLabel + " (" + (until ? bp.targetCount : bp.repeatCount * Mathf.Max(1, bp.recipe.products[0].count)) + ")"
                        });
                    }
                }
                // Die Dauerauftraege zuerst (2026-08-30: der "until"-Auftrag steht immer ganz oben, wenn es
                // mehrere fuer eine Zeile gibt) - sie sind der Normalfall, die Einzelauftraege
                // haengen darunter. Stabil: innerhalb einer Art bleibt die Reihenfolge der Baenke.
                entry.activeBills = entry.activeBills.OrderByDescending(b => b.isUntil).ToList();
            }

            // EINE Quelle fuer "ueberfluessig" (2026-08-12): genau die
            // Liste, die auch das Nachschub-Fenster unter "Obsolete bills" zeigt. Vorher beantwortete dieser
            // Tab die Frage selbst - ueber `entry.activeBills`, und das sammelt bewusst NUR Auftraege vom Typ
            // "stelle her bis X" (fuer die Zielmengen-Rechnung genau richtig). Ein "mache 5x"-Auftrag zaehlte
            // damit nirgends mit: der Knopf stand auf (0), waehrend das Fenster daneben denselben Auftrag als
            // ueberfluessig auflistete. Zwei Zahlen fuer dieselbe Sache, und die kleinere war die falsche.
            // Einmal pro Aktualisierung berechnet, nicht pro Frame - die Funktion laeuft ueber alle Baenke
            // und alle Kolonisten, und der Knopf wird jeden Frame gezeichnet.
            unneededAmmoDefs = new HashSet<ThingDef>(LoadoutUtility.UnneededAmmoBillCandidates());

            ammoEntries.SortBy(e => e.ammoDef.label);
        }

        // Siehe RefreshAmmoList: die geteilte Antwort auf "welche Munition wird produziert, obwohl sie
        // niemand mehr braucht". Deckungsgleich mit dem Abschnitt "Obsolete bills" im Nachschub-Fenster.
        private HashSet<ThingDef> unneededAmmoDefs = new HashSet<ThingDef>();

        public bool weaponsMode;
        private readonly HashSet<ThingDef> selectedWeapons = new HashSet<ThingDef>();
        // EINE Wahrheit fuer die Munitions-Haken, wie selectedWeapons daneben (2026-08-31, im Test: der
        // Master-Schalter hakte in der Munitions-Ansicht nie
        // etwas AN, nur ab - viermal reproduziert). Vorher lebte der Haken als bool auf den AmmoEntry-
        // Objekten selbst, und deren Lebensdauer haengt am Neubau von ammoEntries/visibleCache - ein
        // Schreiben auf das eine Exemplar war dann im naechsten Bild nicht mehr das, was gezeichnet
        // wurde. Ein Def-Set kennt keine Objektidentitaet, damit ist die Klasse Fehler strukturell weg
        // (dieselbe Bauform, die in der Waffen-Ansicht nachweislich funktioniert).
        private readonly HashSet<ThingDef> selectedAmmoDefs = new HashSet<ThingDef>();
        // Menge und Material pro Waffenzeile (2026-08-13). Nur was der Spieler
        // wirklich angefasst hat, steht hier drin - alles andere faellt auf 1 Stueck und das Standardmaterial
        // zurueck, damit der Ein-Klick-Fall ohne jede Vorarbeit funktioniert.
        private readonly Dictionary<ThingDef, string> wovCount = new Dictionary<ThingDef, string>();
        private readonly Dictionary<ThingDef, ThingDef> wovStuff = new Dictionary<ThingDef, ThingDef>();

        // Das Standardmaterial einer Waffe - Stahl, wenn erlaubt, sonst das erste zulaessige. Dieselbe Wahl,
        // die Dialog_SupplyLoadouts.BuildWeaponEntry trifft, damit beide Wege dasselbe vorschlagen.
        private static ThingDef DefaultStuffFor(ThingDef w)
        {
            if (w == null || !w.MadeFromStuff) return null;
            List<ThingDef> allowed = GenStuff.AllowedStuffsFor(w).ToList();
            return allowed.FirstOrDefault(s => s == ThingDefOf.Steel) ?? allowed.FirstOrDefault();
        }
        private Vector2 weaponsScroll;
        private string weaponsSearch = "";
        private List<ThingDef> overviewWeapons;
        // This window doesn't force-pause (unlike most of this mod's other popups), so real time passes while
        // it's open - a bench finishing construction, research completing, or a bill elsewhere changing would
        // otherwise leave "No bench"/"Not researched"/bill status frozen at whatever it was when the tab was
        // last entered. A short TTL (matches LoadoutUtility.WeaponHasActiveBill's own 1s cache convention)
        // keeps the overview honest without re-scanning every def/bench/pawn every single frame.
        private float overviewCacheTime = -99f;
        private float wovMaxScore = 1f;   // best score in the overview; the score bars normalise against it
        private class WOInfo { public RecipeDef recipe; public bool researched; public bool hasBench; public bool inLoadout; public bool inAssignedLoadout; public bool hasActiveBill; public List<ThingDefCountClass> ings; public float score; public SpecialAbility special;
            // Die Werkbaenke dieser Waffe, schon nach "Doppelte ausblenden" gefiltert (2026-08-16: bei mehreren
            // Werkbaenken gibt es auch mehrere Eintraege pro Zeile).
            // Wie auf der Munitionsseite VOR dem Zeichnen ermittelt - die Zeilenhoehe haengt daran, und
            // eine Hoehe, die erst beim Zeichnen feststeht, kann der Bildlauf nicht vorher summieren.
            public List<Building_WorkTable> benches = new List<Building_WorkTable>();
            // Die laufenden Auftraege dieser Waffe (2026-08-16, Punkt 3 seines Waffen-Reiter-Auftrags:
            // "muelleimer je aktivem auftrag"). Wie die Baenke daneben VOR dem Zeichnen ermittelt - die
            // Zeilenhoehe haengt daran, und der Bildlauf braucht die Gesamthoehe vor der ersten Zeile.
            public List<ActiveBillInfo> activeBills = new List<ActiveBillInfo>(); }
        private Dictionary<ThingDef, WOInfo> woInfo;
        private enum WovSort { Score, Name, Owned }
        private WovSort wovSort = WovSort.Score;
        private bool wovSortDesc = true;
        // AllWeapons ist am 2026-08-16 entfallen - der Wert ist hier mit raus, damit kein
        // toter Zweig zurueckbleibt. Der Filter wird nirgends gespeichert, also kann auch kein alter
        // Einstellungsstand darauf zeigen.
        private enum WovFilter { AssignedLoadouts, AvailableWeapons, ResearchedWeapons, ActiveBills }
        private WovFilter wovFilter = WovFilter.AvailableWeapons;

        // Stand der Zuweisungen, fuer den die Liste gebaut wurde (2026-09-06, oi-billoverview-stale-after-
        // reset). Die Liste entstand bisher nur beim Betreten des Reiters und nach Klicks IM Fenster - ein
        // Arm-Durchlauf oder ein "Confirm & supply" aus einem Kind-Fenster aendert die Zuweisungen, waehrend
        // diese Uebersicht dahinter offen steht, und sie merkte es erst beim naechsten eigenen Klick.
        // Einmal ist sie so mit Filter "Assigned loadouts" komplett leer stehen geblieben: gebaut in dem
        // Moment, in dem der Durchlauf alles zurueckgesetzt und noch nichts neu vergeben hatte.
        // CombatReadiness.MutationVersion zaehlt an jeder Stelle hoch, die Zuweisungen schreibt (Apply,
        // Einzelzuweisung, Keins, Nachschub-Fenster) - genau das Signal, das hier fehlte.
        private int builtForMutation = -1;

        public void DoWindowContents(Rect inRect)
        {
            if (builtForMutation != CombatReadiness.MutationVersion)
            {
                builtForMutation = CombatReadiness.MutationVersion;
                RefreshBenchesAndAmmo();
                visibleCache = null;
            }
            // Zustand VOR dem Zeichnen merken, danach pruefen - siehe RefreshAllAfterClick.
            bool mouseEvent = Event.current != null
                && (Event.current.type == EventType.MouseDown || Event.current.type == EventType.MouseUp);
            DoWindowContentsInner(inRect);
            if (mouseEvent && Event.current != null && Event.current.type == EventType.Used) RefreshAllAfterClick();
        }

        private void DoWindowContentsInner(Rect inRect)
        {
            // Mode toggle (top-right, tinted so it stands out): the ammo bill overview <-> the weapons overview.
            if (weaponsMode)
            {
                if (LoadoutUtility.ModeToggle(inRect.width, "CER_GenerateAmmoBills_ToggleToAmmoBills".Translate())) weaponsMode = false;
                DrawWeaponsOverview(inRect);
                return;
            }
            if (LoadoutUtility.ModeToggle(inRect.width, "CER_GenerateAmmoBills_ToggleToWeaponBills".Translate())) { weaponsMode = true; overviewWeapons = null; }   // rebuild the cache on entry (bench changes etc.)

            CERUiState ui = RoleUtility.Ui();
            LoadoutUtility.ModeDetailToggle(inRect.width, ref ui.abDetailed, LoadoutUtility.ModeToggleW + 8f);

            float y = 0f;

            // Row 1: search + default batches on the left, the workbench filter right-aligned before the toggle.
            // Batches/Workbench are Detailed-mode only - Simple mode keeps whatever was already set and skips
            // straight to the ammunition filter, which stays visible in both (it's how you find things, not a
            // production-setup detail).
            nameFilter = LoadoutUtility.SearchBox(0f, y, nameFilter);

            if (ui.abDetailed)
            {
                {
                    // Per save (ui field); tab edits never write the global settings back.
                    string curBatches = ui.abDefaultBatches ?? (CEReloadedMod.settings != null ? CEReloadedMod.settings.defAmmoBatches : "1");
                    Widgets.Label(new Rect(288f, y + 2f, 110f, 24f), "CER_GenerateAmmoBills_DefaultBatchesLabel".Translate());
                    string newBatches = Widgets.TextField(new Rect(400f, y, 46f, 24f), curBatches);
                    if (newBatches != curBatches)
                    {
                        ui.abDefaultBatches = newBatches;
                        // Die Kopfzeile setzt weiter CHARGEN - je Zeile wird daraus die Stueckzahl (Chargen x Ertrag).
                        int nb = int.TryParse(newBatches, out int nbParsed) ? Mathf.Max(1, nbParsed) : 1;
                        foreach (var entry in ammoEntries) entry.amountString = (nb * Mathf.Max(1, entry.yield)).ToString();
                    }
                }

                // Sits well left of the corner so it never runs under the Simple/Detailed toggle and the mode button.
                if (LoadoutUtility.Dropdown(inRect.width - 500f, y, 76f, "CER_GenerateAmmoBills_WorkbenchLabel".Translate(), 180f, benchFilter))
                {
                    List<FloatMenuOption> benchOptions = new List<FloatMenuOption>();
                    benchOptions.Add(new FloatMenuOption("CER_GenerateAmmoBills_AllBenchesOption".Translate(), () => benchFilter = "All benches"));
                    HashSet<string> availableBenches = new HashSet<string>();
                    foreach (var e in ammoEntries)
                        foreach (var b in e.validBenches) availableBenches.Add(GetBenchLabel(b));
                    foreach (string benchName in availableBenches.OrderBy(b => b))
                    {
                        string localBench = benchName;
                        benchOptions.Add(new FloatMenuOption(localBench, () => benchFilter = localBench));
                    }
                    Find.WindowStack.Add(new FloatMenu(benchOptions));
                }
            }

            ui.abFilter = filterMode;   // keep the per-save state in sync
            y += 30f;

            // Row 2: Ammunition filter as radios, plus the hide-duplicates toggle on the right.
            // Einpassung seit 2026-08-27 in LoadoutUtility.FitRadioRow - eine Rechnung fuer diese Zeile UND
            // die gleichnamige im Waffen-Reiter, mit sichtbarem Mindestabstand und einer zweiten Zeile als
            // letzter Stufe. Die Begruendung steht am Helfer.
            string dupLabel = "CER_GenerateAmmoBills_HideDuplicateBenches".Translate();
            string[] ammoRadioLabels =
            {
                "CER_GenerateAmmoBills_AbAssignedLoadouts".Translate(),
                "CER_GenerateAmmoBills_AbAvailableWeapons".Translate(),
                "CER_GenerateAmmoBills_AbActiveBills".Translate(),
                "CER_GenerateAmmoBills_AbAllAmmunition".Translate(),
            };
            GameFont prevRowFont = Text.Font;
            LoadoutUtility.RadioRowFit ammoFit =
                LoadoutUtility.FitRadioRow(92f, y, inRect.width, ammoRadioLabels, dupLabel);
            Text.Font = ammoFit.font;
            Widgets.Label(new Rect(0, y + 2f, 88f, 24f), "CER_GenerateAmmoBills_AmmunitionLabel".Translate());
            float afx = 92f;
            // Erklaertexte an JEDEN Filter (2026-09-04, Befund im Test: keiner der fuenf Radioknoepfe hatte
            // einen Erklaertext - das fiel auf, weil sonst in diesem Mod fast alles einen hat). Zwei der fuenf
            // im Waffen-Reiter hatten laengst einen - die Zeile war also
            // halb erklaert, was schlimmer ist als gar nicht: man hoert auf zu suchen.
            // Drei der Texte teilen sich beide Ansichten, weil sie dasselbe bedeuten (Filter...Tip);
            // die zwei munitionsspezifischen haben eigene.
            void AmmoRadio(string label, AmmoBillFilterMode m, string tip)
            {
                if (LoadoutUtility.Radio(ref afx, y, label, filterMode == m, tip) && filterMode != m)
                {
                    filterMode = m;
                    RefreshBenchesAndAmmo();
                }
            }
            AmmoRadio(ammoRadioLabels[0], AmmoBillFilterMode.AssignedLoadouts, "CER_GenerateAmmoBills_FilterAssignedTip".Translate());
            // "Created loadouts" ist am 2026-09-07 entfallen - seit dem Lazy-Umbau dieselbe
            // Menge wie "Assigned loadouts". Der Enum-Wert AvailableLoadouts bleibt nur, damit alte
            // Einstellungsdateien laden; CEReloadedSettings biegt ihn beim Laden auf Assigned um.
            AmmoRadio(ammoRadioLabels[1], AmmoBillFilterMode.AvailableWeapons, "CER_GenerateAmmoBills_AbAvailableWeaponsTip".Translate());
            AmmoRadio(ammoRadioLabels[2], AmmoBillFilterMode.ActiveBills, "CER_GenerateAmmoBills_FilterActiveBillsTip".Translate());
            AmmoRadio(ammoRadioLabels[3], AmmoBillFilterMode.AllAmmunition, "CER_GenerateAmmoBills_AbAllAmmunitionTip".Translate());

            if (CEReloadedMod.settings != null)
            {
                // In DERSELBEN Schrift wie der Fluss - mit ihr ist das Rechteck gemessen worden.
                bool hideDup = CEReloadedMod.settings.hideDuplicateBenches;
                Widgets.CheckboxLabeled(ammoFit.checkboxRect, dupLabel, ref hideDup);
                // Der Haken stand in derselben Zeile und war der einzige ohne Erklaertext (2026-09-04).
                TooltipHandler.TipRegion(ammoFit.checkboxRect, "CER_GenerateAmmoBills_HideDuplicateBenchesTip".Translate());
                if (hideDup != CEReloadedMod.settings.hideDuplicateBenches)
                {
                    CEReloadedMod.settings.hideDuplicateBenches = hideDup;
                    RefreshBenchesAndAmmo();
                }
            }
            Text.Font = prevRowFont;
            y += 30f + ammoFit.extraHeight;

            // "Remove obsolete ammo bills" wohnt seit 2026-08-01 im "Auto-generate"-Popup selbst
            // (Dialog_SupplyLoadouts, via OpenMissingAmmoSupplyPopup), nicht mehr in einer Zeile hier. Der
            // Bereichs-Schalter "Assigned/Created loadouts", der daneben stand, ist am 2026-09-07 ganz entfallen.

            y = LoadoutUtility.Divider(y, inRect.width);

            // --- Table Header (shared vanilla-style plain-text headers) ---
            void AbSort(float x, float w, string label, SortMode mode, bool defaultDesc = false)
            {
                if (LoadoutUtility.ColumnHeader(new Rect(x, y + 2f, w, 22f), label, currentSortMode == mode, sortDescending))
                {
                    if (currentSortMode == mode) sortDescending = !sortDescending;
                    else { currentSortMode = mode; sortDescending = defaultDesc; }
                }
            }
            // ALLE AUF EINMAL (2026-08-30): Munitions- und Waffen-Auftraege bekommen links oben einen Knopf,
            // der ALLE umschaltet - ausser den gesperrten (z.B. dunkelrotes Kreuz, weil nicht erforscht).
            // Der Schalter sitzt ueber der Hakenspalte und schaltet genau die Zeilen, die man auch
            // einzeln anhaken koennte - gesperrte (nicht erforscht / keine Werkbank) bleiben aussen vor.
            // Regel wie ueberall im Mod: haengt noch irgendwo ein Haken, macht der Schalter ALLE an; sind alle an,
            // macht er alle aus.
            {
                List<AmmoEntry> togglable = BuildVisibleAmmoEntries().Where(e => e.isResearched && e.validBenches.Count > 0).ToList();
                bool allOn = togglable.Count > 0 && togglable.All(e => selectedAmmoDefs.Contains(e.ammoDef));
                Rect masterR = new Rect(4f, y + 3f, 20f, 20f);
                if (togglable.Count > 0)
                {
                    if (Widgets.ButtonImage(masterR, allOn ? Widgets.CheckboxOnTex : Widgets.CheckboxOffTex))
                        foreach (AmmoEntry e in togglable)
                        {
                            if (allOn) selectedAmmoDefs.Remove(e.ammoDef);
                            else selectedAmmoDefs.Add(e.ammoDef);
                        }
                    TooltipHandler.TipRegion(masterR, allOn
                        ? "CER_GenerateAmmoBills_MasterUncheckTip".Translate()
                        : "CER_GenerateAmmoBills_MasterCheckTip".Translate(togglable.Count.ToString()));
                }
            }
            AbSort(AbNameX, AbNameW, "CER_GenerateAmmoBills_ColName".Translate(), SortMode.Name);
            AbSort(AbSpecialX, AbSpecialW, "CER_GenerateAmmoBills_ColSpecial".Translate(), SortMode.Special);
            AbSort(AbAssigX, AbAssigW, "CER_GenerateAmmoBills_ColAssig".Translate(), SortMode.Assigned, true);
            AbSort(AbLoadoutX, AbLoadoutW, "CER_GenerateAmmoBills_ColLoadouts".Translate(), SortMode.Loadouts);
            LoadoutUtility.ColumnHeader(new Rect(AbIngX, y + 2f, AbIngW, 22f), "CER_GenerateAmmoBills_ColIngredients".Translate(), false, false, false);
            AbSort(AbActiveX, AbActiveW, "CER_GenerateAmmoBills_WovColActiveBills".Translate(), SortMode.BillStatus);
            LoadoutUtility.ColumnHeader(new Rect(AbAmountX, y + 2f, AbAmountW + 4f, 22f), "CER_GenerateAmmoBills_ColAmount".Translate(), false, false, false);
            LoadoutUtility.ColumnHeader(new Rect(AbGenX, y + 2f, AbGenW, 22f), "CER_GenerateAmmoBills_ColGenerateOn".Translate(), false, false, false);
            y += 28f;

            // --- Ammo List Section ---
            // Filtern und Sortieren liefen pro BILD (2026-08-15). Bei CEs
            // vollem Munitionsbestand sind das 50-150 Eintraege, dreimal durchgeschaufelt (Where -> ToList
            // -> OrderBy -> ToList), und bei Sortierung nach Verfuegbarkeit ruft der Schluessel zusaetzlich
            // GetAmmoOwnership pro Eintrag. Das Zeichnen selbst hat laengst eine Scroll-Kulisse (siehe
            // unten) - die Liste zu BAUEN war der Rest, der immer fuer alle lief.
            // Der Schluessel enthaelt alles, was Auswahl oder Reihenfolge aendern kann, plus ein
            // Hoechstalter von einer halben Sekunde fuer das, was von lebendem Bestand abhaengt
            // (Sortierung nach Verfuegbarkeit/Auftraegen). Dieselbe Bauart wie die uebrigen Caches des
            // Mods, nur kuerzer: die Bestandszaehler darunter haben selbst 1s.
            string visKey = ammoEntriesVersion + "|" + ammoTypeFilter + "|" + nameFilter + "|" + benchFilter
                + "|" + (int)currentSortMode + "|" + (sortDescending ? 1 : 0);
            if (visibleCache == null || visKey != visibleCacheKey || Time.realtimeSinceStartup - visibleCacheTime > 0.5f)
            {
                visibleCacheKey = visKey;
                visibleCacheTime = Time.realtimeSinceStartup;
                visibleCache = BuildVisibleAmmoEntries();
            }

            float totalHeight = 0f;
            foreach (var e in visibleCache)
            {
                int filteredBenches = benchFilter == "All benches" ? e.validBenches.Count : (e.validBenches.Any(b => GetBenchLabel(b) == benchFilter) ? 1 : 0);
                float h = Mathf.Max(40f, Mathf.Max(e.activeBills.Count, filteredBenches) * 24f + 8f);
                totalHeight += h;
            }

            Rect viewRect = new Rect(0, 0, inRect.width - 16f, totalHeight);
            Rect scrollRect = new Rect(0, y, inRect.width, inRect.height - y - 50f);

            Widgets.BeginScrollView(scrollRect, ref scrollPosition, viewRect);
            // Leere Tabelle SAGT jetzt, warum sie leer ist (2026-09-04, Befund im Test: die Tabelle war
            // vollstaendig leer, und kein Satz sagte, dass die Suche nichts gefunden hat - wer die Suche
            // vergessen hat, sieht der Anzeige nicht an, ob es am Filter
            // liegt). Zwei verschiedene Gruende, zwei verschiedene Saetze - dieselbe Unterscheidung, die
            // die drei Listen im Rollen-Reiter seit dem 19.08. machen.
            if (visibleCache.Count == 0)
            {
                GUI.color = new Color(1f, 1f, 1f, 0.55f);
                Widgets.Label(new Rect(6f, 6f, viewRect.width - 12f, 60f),
                    string.IsNullOrEmpty(nameFilter)
                        // Den Filternamen einsetzen statt ihn in den Satz zu schreiben (2026-09-04, Befund
                        // im Test auf Deutsch: die Leermeldung nannte "Res. ammo" und "Res.
                        // weapons", waehrend die Radios direkt darueber "Erf. Munition" und "Erf. Waffen"
                        // heissen). Ein Satz, der einen Knopf zitiert, muss den Knopf FRAGEN - sonst
                        // driften die beiden bei jeder Uebersetzung auseinander. Und er nennt jetzt nur
                        // den Filter DIESER Ansicht: der andere half hier ohnehin nicht.
                        ? "CER_GenerateAmmoBills_NothingMatchesFilter".Translate(
                              "CER_GenerateAmmoBills_AbAllAmmunition".Translate())
                        : "CER_GenerateAmmoBills_NothingMatchesSearch".Translate(nameFilter));
                GUI.color = Color.white;
            }
            float listY = 0f;
            foreach (AmmoEntry entry in visibleCache)
            {
                if (!entry.isResearched)
                {
                    selectedAmmoDefs.Remove(entry.ammoDef);
                    GUI.color = Color.grey;
                }
                else
                {
                    GUI.color = Color.white;
                }

                // Calculate row height
                int filteredBenches = benchFilter == "All benches" ? entry.validBenches.Count : (entry.validBenches.Any(b => GetBenchLabel(b) == benchFilter) ? 1 : 0);
                float rowHeight = Mathf.Max(40f, Mathf.Max(entry.activeBills.Count, filteredBenches) * 24f + 8f);

                // Scroll culling (2026-08-01, the whole tab felt sluggish): this loop draws
                // several icons/buttons/tooltip-hosting rects per row (checkbox, ammo icon, special-effect
                // icons, availability bar, ingredient cell, per-bench buttons, batch stepper...) - with
                // enough ammo types in view (CE's own roster easily runs to 50-150+ under "All ammunition"),
                // doing all of that for rows scrolled completely out of sight added up. Rows outside the
                // visible window only advance the cursor, matching the same pattern Dialog_SquadSetup's own
                // row loops already use.
                if (listY + rowHeight < scrollPosition.y || listY > scrollPosition.y + scrollRect.height)
                {
                    listY += rowHeight;
                    continue;
                }

                float centerY = listY + (rowHeight - 24f) / 2f;

                // Bills running for nobody's current demand - the row itself carries the warning, not just the
                // bottom-bar button's count, so it's obvious WHICH entries a prune would touch before clicking it.
                // Aus derselben geteilten Liste wie Knopfzahl und Nachschub-Fenster (siehe unneededAmmoDefs).
                bool unneeded = unneededAmmoDefs.Contains(entry.ammoDef);
                if (unneeded)
                {
                    Widgets.DrawBoxSolid(new Rect(0f, listY, viewRect.width, rowHeight), new Color(0.55f, 0.4f, 0.15f, 0.12f));
                    TooltipHandler.TipRegion(new Rect(0f, listY, viewRect.width, rowHeight),
                        "CER_GenerateAmmoBills_UnneededBillTooltip".Translate());
                }

                // Checkbox - liest und schreibt selectedAmmoDefs, nie mehr ein Feld am Entry-Objekt
                // (siehe die Begruendung an selectedAmmoDefs selbst).
                if (entry.isResearched && entry.validBenches.Count > 0)
                {
                    bool sel = selectedAmmoDefs.Contains(entry.ammoDef);
                    bool was = sel;
                    Widgets.Checkbox(new Vector2(4f, centerY + 2f), ref sel, 20f);
                    if (sel != was) { if (sel) selectedAmmoDefs.Add(entry.ammoDef); else selectedAmmoDefs.Remove(entry.ammoDef); }
                }
                else
                {
                    selectedAmmoDefs.Remove(entry.ammoDef);
                    GUI.DrawTexture(new Rect(4f, centerY + 2f, 20f, 20f), Widgets.CheckboxOffTex);
                }

                // Icon
                Rect iconRect = new Rect(30f, centerY, 28f, 28f);
                Widgets.ThingIcon(iconRect, entry.ammoDef);

                // Label
                // 115 -> 159: aus AbNameW abgeleitet statt zweimal hingeschrieben (AbNameX + AbNameW - 65).
                Rect labelRect = new Rect(65f, centerY + 2f, AbNameX + AbNameW - 65f, 24f);
                Widgets.Label(labelRect, LoadoutUtility.TruncateKeepingSuffix(entry.ammoDef.label, labelRect.width));
                // Lazy (2026-08-01, whole-tab performance report): GetAmmoRecipeTooltip builds a multi-line
                // string (damage/AP extraction, ability names, ingredient list) - a plain-string TipRegion
                // call forced that every frame for every row regardless of hover.
                TooltipHandler.TipRegion(new Rect(iconRect.x, iconRect.y, labelRect.xMax - iconRect.xMin, 28f),
                    () => LoadoutUtility.GetAmmoRecipeTooltip(entry.ammoDef), entry.ammoDef.GetHashCode() ^ 0x4A11);

                // Special column: per-ammo effect icons.
                SpecialAbility ammoEff = LoadoutUtility.GetAmmoEffects(entry.ammoDef);
                if (ammoEff != SpecialAbility.None) Dialog_GenerateLoadouts.DrawAmmoEffectIcons(AbSpecialX, centerY + 4f, ammoEff, entry.ammoDef, AbSpecialW);

                // Availability column (2026-08-01 - was a bare colored-dot + assigned-pawn-
                // count, the same square shape/look the availability bars elsewhere already own for a
                // DIFFERENT concept). Now the SAME shared bar as everywhere else, fed by GetAmmoOwnership
                // (the loadout-ammo-slot-demand domain function) - colony-wide, no pawn
                // context, with the same rich Total/Equipped/Available/... tooltip for free.
                Rect assignedCell = new Rect(AbAssigX, centerY + 2f, AbAssigW, 20f);
                LoadoutUtility.GetAmmoOwnership(entry.ammoDef, out int abCarriedOnly, out int abCarriedAndAssigned, out int abAssignedInStore, out int abFreeStored, out int abInProduction);
                var abSeg = LoadoutUtility.BuildAvailabilitySegments(abCarriedOnly, abCarriedAndAssigned, abAssignedInStore, abFreeStored, abInProduction, def: entry.ammoDef);
                // Where-is-it breakdown (2026-08-02, made uniform) - every OTHER
                // Availability bar in the mod already got this treatment
                // (BuildWeaponLocationTooltip/BuildPackItemLocationTooltip/BuildAmmoLocationTooltip); this
                // one was the last holdout, still using the bare segment-only default tooltip.
                // TipWanted: der Orts-Tooltip laeuft ueber alle Karten - nur bauen, wenn er auch gezeigt wird.
                LoadoutUtility.DrawAvailabilityBar(assignedCell, abSeg, horizontal: true,
                    LoadoutUtility.TipWanted(assignedCell) ? LoadoutUtility.BuildAmmoLocationTooltip(entry.ammoDef) : null);

                // Ingredients column (sorted icons, "!" if a resource isn't in storage, whole-cell tooltip).
                // mult is this row's own Batches field - without it the shortage check only validated
                // stock against ONE batch, silently missing shortfalls on any row with Batches > 1.
                GUI.color = entry.isResearched ? Color.white : Color.grey;
                // Die Zutatenspalte rechnet in CHARGEN, das Feld steht jetzt in Stueck - also zurueckrechnen.
                int abAmount = int.TryParse(entry.amountString, out int abAmountParsed) ? Mathf.Max(1, abAmountParsed) : 1;
                int abBatches = Mathf.Max(1, Mathf.CeilToInt(abAmount / (float)Mathf.Max(1, entry.yield)));
                LoadoutUtility.DrawIngredientCell(AbIngX, AbIngW, centerY, entry.ings, abBatches);
                GUI.color = entry.isResearched ? Color.white : Color.grey;

                // Active Bills
                if (entry.activeBills.Count > 0)
                {
                    float benchY = listY + 4f;
                    bool billDeleted = false;
                    foreach (ActiveBillInfo info in entry.activeBills.ToList())
                    {
                        if (DrawActiveBillRow(new Rect(AbActiveX, benchY, AbActiveW, 22f), info)) { billDeleted = true; break; }
                        benchY += 24f;
                    }
                    if (billDeleted) { RefreshBenchesAndAmmo(); break; }
                }
                else
                {
                    GUI.color = Color.gray;
                    Widgets.Label(new Rect(AbActiveX, centerY + 2f, AbActiveW, 24f), "CER_GenerateAmmoBills_ActiveBillsNone".Translate());
                }


                // Generate On... Buttons (bench icon + "#id" when duplicates exist)
                if (entry.validBenches.Count > 0 && entry.isResearched)
                {
                    GUI.color = Color.white;
                    float benchY = listY + 4f;
                    foreach (Building_WorkTable bench in entry.validBenches)
                    {
                        string benchName = GetBenchLabel(bench);
                        if (benchFilter != "All benches" && benchName != benchFilter) continue;

                        int abAmount2 = int.TryParse(entry.amountString, out int abAmtParsed) ? Mathf.Max(0, abAmtParsed) : 0;
                        int pressed = DrawBenchBillButtons(AbGenX, benchY, bench, benchName, abAmount2, entry.yield);
                        if (pressed == 1)
                        {
                            // Der Einzelauftrag stellt die EINGETRAGENE MENGE her - bei Munition also so viele
                            // Durchgaenge, wie fuer diese Stueckzahl noetig sind (2026-08-30).
                            int runs = Mathf.Max(1, Mathf.CeilToInt(abAmount2 / (float)Mathf.Max(1, entry.yield)));
                            // Einzelklick des Spielers - zaehlt wie von Hand gesetzt (siehe AddOrUpdateAmmoBill).
                            LoadoutUtility.AddOrRaiseOnceBill(bench, entry.recipeDef, runs, null, markManaged: false);
                            LoadoutUtility.RefreshBillsTab(bench);
                            RefreshBenchesAndAmmo();
                            break;
                        }
                        if (pressed == 2)
                        {
                            GenerateBillForEntry(entry, bench, additive: true, markManaged: false);   // Einzelklick = von Hand
                            RefreshBenchesAndAmmo();
                            break;
                        }
                        benchY += 24f;
                    }
                }
                else if (entry.validBenches.Count == 0 && entry.isResearched)
                {
                    GUI.color = Color.gray;
                    Widgets.Label(new Rect(AbGenX, centerY + 2f, AbGenW, 24f), "CER_GenerateAmmoBills_NoBench".Translate());
                    GUI.color = Color.white;
                }
                else if (!entry.isResearched)
                {
                    GUI.color = Color.gray;
                    Widgets.Label(new Rect(AbGenX, centerY + 2f, AbGenW, 24f), "CER_GenerateAmmoBills_NotResearched".Translate());
                    GUI.color = Color.white;
                }

                // Loadout column: a circle + count (2026-08-01) - not a comma-joined,
                // truncated name list (which both read poorly with more than 1-2 loadouts AND was the exact
                // spot the "[Pawn-Loado..." internal anchor name used to leak into, before the population
                // fix above started resolving through real templates). Full names + contents are in the
                // tooltip, lazily built - only when actually hovered.
                if (entry.loadouts.Count > 0)
                {
                    GUI.color = Color.white;
                    Rect loadoutRect = new Rect(AbLoadoutX, centerY + 2f, AbLoadoutW, 24f);
                    // Weiss statt gruen (2026-08-17): der Loadout-Knopf in der Bill-Uebersicht war gruen,
                    // einheitlich waere weiss. Begruendung:
                    // gruen bedeutet in diesem Mod ueberall "gedeckt / alles gut". Hier zaehlt der Punkt nur,
                    // WIE VIELE Vorlagen diese Munition benutzen - eine Zahl, kein Urteil. Gruen hat an einer
                    // reinen Zaehlspalte eine Bewertung behauptet, die dort niemand trifft.
                    LoadoutUtility.DrawDotSolid(new Rect(AbLoadoutX + 2f, centerY + 9f, 9f, 9f), Color.white);
                    Text.Anchor = TextAnchor.MiddleLeft;
                    Widgets.Label(new Rect(AbLoadoutX + 15f, centerY, 40f, 24f), entry.loadouts.Count.ToString());
                    Text.Anchor = TextAnchor.UpperLeft;
                    TooltipHandler.TipRegion(loadoutRect,
                        () => string.Join("\n\n", entry.loadoutRefs.Select(l => LoadoutUtility.LoadoutContentsTooltip(l)).ToArray()),
                        entry.ammoDef.GetHashCode() ^ 0x10AD);
                }
                else
                {
                    GUI.color = Color.gray;
                    Widgets.Label(new Rect(AbLoadoutX, centerY + 2f, AbLoadoutW, 24f), "CER_GenerateAmmoBills_LoadoutsNone".Translate());
                }

                // Batches & Yield Column
                if (entry.isResearched && entry.validBenches.Count > 0)
                {
                    GUI.color = Color.white;
                    // "+<yield>" stepper (2026-08-01, applied everywhere this batch/
                    // yield pattern shows up) - one click bumps batches up by exactly one, i.e. the eventual
                    // total by exactly `yield` - a quicker, more tactile top-up than typing a raw batch
                    // number, without removing that direct-typing option (still the field right after it).
                    // "+<yield>" stepper (2026-08-01, applied everywhere this batch/
                    // yield pattern shows up) - one click bumps batches up by exactly one, i.e. the eventual
                    // total by exactly `yield` - a quicker, more tactile top-up than typing a raw batch
                    // number, without removing that direct-typing option (still the field right after it).
                    //
                    // 2026-08-16: hier stand fuer eine Stunde ein Umbau auf zwei Schritte (+1/+5) samt
                    // Magazin-Anzeige an der Gesamtmenge. Wieder entfernt: Produktionen sind eher
                    // Produktionschargen als Magazine. Die Zahlen dazu: eine CE-Charge sind 200-500 Schuss,
                    // ein Magazin 10-60 -
                    // ein Magazin-Schritt ist feiner als das, was das Rezept ueberhaupt herstellen kann.
                    // Deshalb sind auch TexMagazine und MagazineSizeForAmmo wieder raus - Code, den niemand ruft,
                    // liest sich beim naechsten Mal wie Code, der gebraucht wird. Das Platzhalter-Symbol
                    // Textures/UI/Icon_Magazine.png bleibt liegen: eine Bilddatei behauptet nichts.
                    // EIN Feld statt Knopf + Chargen (2026-08-30): hier steht die Stueckzahl, die ein Klick
                    // auf eine Werkbank rechts daneben dem Auftrag hinzufuegt.
                    Rect amountRect = new Rect(AbAmountX, centerY, AbAmountW, 24f);
                    entry.amountString = Widgets.TextField(amountRect, entry.amountString);
                    TooltipHandler.TipRegion(amountRect, "CER_GenerateAmmoBills_AmountTooltip".Translate(entry.yield.ToString()));
                }

                listY += rowHeight;
            }
            Widgets.EndScrollView();
            GUI.color = Color.white;

            // --- Bottom bar: destructive action left, actions flowing in from the right (the window's X closes). ---
            // Breite gemessen statt 180f fest (2026-08-26: auf Deutsch stand
            // "lle Munitionsauftraege loesche" - beidseitig aus dem Rect gelaufen). Der Nachbarknopf
            // haengt an delAmmoRect.xMax und rueckt von selbst mit.
            // Erst ALLE vier Beschriftungen und Breiten, dann EINE Schriftstufe fuer die ganze Reihe, dann
            // zeichnen (2026-09-04, Befund im Test auf Deutsch: links "Alle Munitionsauftraege
            // loeschen" und "Unnoetige Auftraege entfernen (13)" gross, rechts "Munitionsauftraege
            // optimieren" und "Ausgewaehlte herstellen" klein). Die beiden linken wurden gezeichnet, BEVOR
            // BottomBarFont ueberhaupt lief, und waren an seiner Rechnung nie beteiligt - jeder mass fuer
            // sich. Genau der Fall, den der Helfer seit 2026-09-03 ausschliessen soll; er kannte nur die
            // halbe Reihe.
            string delAmmoLabel = "CER_GenerateAmmoBills_DeleteAllBillsButton".Translate();
            float delAmmoW = LoadoutUtility.BottomBtnW(delAmmoLabel, 180f, 260f);
            Rect delAmmoRect = new Rect(0f, inRect.height - LoadoutUtility.BottomBarH, delAmmoW, LoadoutUtility.BottomBtnH);

            int unneededCount = unneededAmmoDefs.Count;
            // Gleiche Messung wie beim Nachbarn - dieselbe Ueberlaufklasse, nur noch nicht aufgefallen.
            string pruneLabel = "CER_GenerateAmmoBills_RemoveUnneededButton".Translate(unneededCount.ToString());
            float pruneW = LoadoutUtility.BottomBtnW(pruneLabel, 220f, 300f);
            Rect pruneRect = new Rect(delAmmoRect.xMax + 8f, inRect.height - LoadoutUtility.BottomBarH, pruneW, LoadoutUtility.BottomBtnH);

            // Ueber die SICHTBARE Liste (2026-09-03, dieselbe Fundstellenklasse wie in der Waffen-Ansicht):
            // GenerateBills legt nur an, was der Filter zeigt - die Zahl im Knopf muss dieselbe sein.
            List<AmmoEntry> selVisibleAmmo = (visibleCache ?? BuildVisibleAmmoEntries()).Where(e => selectedAmmoDefs.Contains(e.ammoDef)).ToList();
            int selAmmoCount = selVisibleAmmo.Count;
            string ammoSelLabel = selAmmoCount > 0
                ? "CER_GenerateAmmoBills_ProduceSelectedCount".Translate(selAmmoCount.ToString()).ToString()
                : "CER_GenerateAmmoBills_ProduceSelectedLabel".Translate().ToString();
            string ammoAutoLabel = "CER_GenerateAmmoBills_AutoGenerateButton".Translate().ToString();

            GameFont prevAmmoBarFont = Text.Font;
            Text.Font = LoadoutUtility.BottomBarFont(
                (delAmmoW, delAmmoLabel.ToString(), false),
                (pruneW, pruneLabel.ToString(), false),
                (LoadoutUtility.PrimaryBtnW, ammoSelLabel, false),
                (LoadoutUtility.SecondaryBtnW, ammoAutoLabel, true));

            if (LoadoutUtility.ButtonTextFit(delAmmoRect, delAmmoLabel))
            {
                Find.WindowStack.Add(LoadoutUtility.CompactConfirmation(
                    "CER_GenerateAmmoBills_DeleteAllBillsConfirm".Translate(),
                    () =>
                    {
                        int n = ArmDeleteAllAmmoBills();
                        Messages.Message(n == 1
                            ? "CER_GenerateAmmoBills_DeletedAmmoBillsSingular".Translate(n.ToString())
                            : "CER_GenerateAmmoBills_DeletedAmmoBillsPlural".Translate(n.ToString()), MessageTypeDefOf.TaskCompletion, false);
                    }, destructive: true));
            }
            TooltipHandler.TipRegion(delAmmoRect, "CER_GenerateAmmoBills_DeleteAllBillsTooltip".Translate());

            GUI.enabled = unneededCount > 0;
            if (LoadoutUtility.ButtonTextFit(pruneRect, pruneLabel))
            {
                Find.WindowStack.Add(LoadoutUtility.CompactConfirmation(
                    "CER_GenerateAmmoBills_PruneConfirm".Translate(),
                    () =>
                    {
                        // Mit der Auswahl loeschen, die der Knopf auch GEZAEHLT hat (2026-08-12) - der
                        // pauschale Aufruf ohne Argument raeumt bewusst enger auf (siehe dort), und das
                        // waere jetzt die naechste Zahl, die nicht zur Tat passt: "(4)" auf dem Knopf und
                        // "2 Auftraege entfernt" in der Meldung danach.
                        int n = PruneUnneededAmmoBills(new HashSet<ThingDef>(unneededAmmoDefs));
                        Messages.Message(n == 1
                            ? "CER_GenerateAmmoBills_RemovedUnneededSingular".Translate(n.ToString())
                            : "CER_GenerateAmmoBills_RemovedUnneededPlural".Translate(n.ToString()), MessageTypeDefOf.TaskCompletion, false);
                    }, destructive: true));
            }
            GUI.enabled = true;
            TooltipHandler.TipRegion(pruneRect, "CER_GenerateAmmoBills_PruneTooltip".Translate());

            // The Auto-... wand button sits in the bottom-right CORNER on every tab (one consistent home).
            // Opens the same "Supply Loadouts" review popup every other produce-missing entry point in the
            // mod uses, instead of silently creating bills with no preview - a player reviewing a screen
            // notices "wait, that's only 3 ammo types" immediately; a silent background pass does not (this
            // is exactly how the earlier filter-scoping bug in Delete/Prune went unnoticed for so long).
            // Reihenfolge und Beschriftung wie in der Waffen-Ansicht (2026-08-30): rechts steht
            // "Produce selected (N)", links davon der Assistent. Vorher stand der Assistent rechts und der andere
            // Knopf hiess anders - zwei Ansichten, zwei Sprachen fuer dieselbe Sache.
            float xr = inRect.width;
            if (LoadoutUtility.BottomActionButton(inRect, ref xr, LoadoutUtility.PrimaryBtnW, ammoSelLabel))
            {
                if (selAmmoCount == 0)
                {
                    Messages.Message("CER_GenerateAmmoBills_NoAmmoSelected".Translate(), MessageTypeDefOf.RejectInput, false);
                    Text.Font = prevAmmoBarFont;   // Text.Font ist statisch - ein Ausstieg hier faerbt sonst das naechste Fenster
                    return;
                }
                GenerateBills();
            }
            // Renamed from "Auto-generate missing" (2026-08-01: shorter, matches the
            // sizing/naming convention every other wand-icon "let the mod do it" button in the mod uses -
            // Auto-assign, Arm my Colony). The scope/cleanup choices this button used to need pre-set via the
            // row above are now asked inside the popup itself, so the tooltip no longer needs to explain them.
            if (LoadoutUtility.BottomActionButton(inRect, ref xr, LoadoutUtility.SecondaryBtnW, ammoAutoLabel,
                tip: "CER_GenerateAmmoBills_AutoGenerateTooltip".Translate(),
                icon: LoadoutUtility.TexWand))
            {
                OpenMissingAmmoSupplyPopup();
            }
            Text.Font = prevAmmoBarFont;
        }
        
        // ===== Weapons Overview: score/special/inventory per weapon + produce (single or multi-select) =====
        // Mirrors the ammo-bill layout exactly: row 1 search, row 2 filter radios, divider, headers.
        private void DrawWeaponsOverview(Rect inRect)
        {
            weaponsSearch = LoadoutUtility.SearchBox(0f, 0f, weaponsSearch);

            // Dieselbe Einpassung wie die Filterzeile des Munitions-Reiters, seit 2026-08-27 ueber denselben
            // Helfer statt ueber eine zweite Kopie der Formel - genau diese Doppelung hat dazu gefuehrt, dass
            // die Zeile hier passte und die andere klebte (siehe LoadoutUtility.FitRadioRow).
            string wovDupLabel = "CER_GenerateAmmoBills_HideDuplicateBenches".Translate();
            string[] wovRadioLabels =
            {
                "CER_GenerateAmmoBills_WovAssignedLoadouts".Translate(),
                "CER_GenerateAmmoBills_WovAvailableWeapons".Translate(),
                "CER_GenerateAmmoBills_WovActiveBills".Translate(),
                "CER_GenerateAmmoBills_WovResearchedWeapons".Translate(),
            };
            GameFont prevWovFont = Text.Font;
            LoadoutUtility.RadioRowFit wovFit =
                LoadoutUtility.FitRadioRow(92f, 30f, inRect.width, wovRadioLabels, wovDupLabel);
            Text.Font = wovFit.font;
            Widgets.Label(new Rect(0, 32f, 88f, 24f), "CER_GenerateAmmoBills_WeaponsLabel".Translate());
            float wfx = 92f;
            void WovRadio(string label, WovFilter m, string tip = null)
            {
                if (LoadoutUtility.Radio(ref wfx, 30f, label, wovFilter == m, tip)) wovFilter = m;
            }
            WovRadio(wovRadioLabels[0], WovFilter.AssignedLoadouts, "CER_GenerateAmmoBills_FilterAssignedTip".Translate());
            WovRadio(wovRadioLabels[1], WovFilter.AvailableWeapons, "CER_GenerateAmmoBills_WovAvailableWeaponsTooltip".Translate());
            // Reihenfolge wie im Munitions-Reiter (2026-08-16: "active bills" kommt nach "available
            // weapons", wie bei den Munitionsauftraegen), und "All weapons" ist weg - es war der
            // einzige Filter, der ueberhaupt nichts filtert, und sein Platz traegt jetzt den
            // Doppelte-Baenke-Schalter.
            WovRadio(wovRadioLabels[2], WovFilter.ActiveBills, "CER_GenerateAmmoBills_FilterActiveBillsTip".Translate());
            WovRadio(wovRadioLabels[3], WovFilter.ResearchedWeapons, "CER_GenerateAmmoBills_WovResearchedWeaponsTooltip".Translate());

            // Derselbe Schalter wie im Munitions-Reiter, und seit heute gehoert er hierher: die
            // Bank-Eintraege einer Waffenzeile richten sich danach. Ohne ihn koennte man die Einstellung
            // nur in den Mod-Einstellungen aendern - fuer etwas, das man direkt vor sich sieht.
            if (CEReloadedMod.settings != null)
            {
                bool wovHideDup = CEReloadedMod.settings.hideDuplicateBenches;
                Widgets.CheckboxLabeled(wovFit.checkboxRect, wovDupLabel, ref wovHideDup);
                // Zwilling des Hakens im Munitions-Reiter - derselbe Erklaertext (2026-09-04).
                TooltipHandler.TipRegion(wovFit.checkboxRect, "CER_GenerateAmmoBills_HideDuplicateBenchesTip".Translate());
                if (wovHideDup != CEReloadedMod.settings.hideDuplicateBenches)
                {
                    CEReloadedMod.settings.hideDuplicateBenches = wovHideDup;
                    CEReloadedMod.settings.Write();
                    overviewWeapons = null;   // Bankliste je Waffe neu aufbauen - sie haengt an diesem Schalter
                }
            }
            Text.Font = prevWovFont;

            // + extraHeight: faellt der Haken auf eine eigene Zeile, muss die Trennlinie mit - alles darunter
            // haengt an hy, also reicht diese eine Stelle.
            float hy = LoadoutUtility.Divider(64f + wovFit.extraHeight, inRect.width) + 2f;
            void SortHdr(float x, float wdt, string label, WovSort mode)
            {
                if (LoadoutUtility.ColumnHeader(new Rect(x, hy, wdt, 22f), label, wovSort == mode, wovSortDesc))
                {
                    if (wovSort == mode) wovSortDesc = !wovSortDesc;
                    else { wovSort = mode; wovSortDesc = mode != WovSort.Name; }
                }
            }
            // Derselbe Schalter wie in der Munitions-Ansicht (2026-08-30) - hier ueber der Hakenspalte bei x=6.
            {
                // Ueber die SICHTBARE Liste, nicht ueber overviewWeapons (2026-09-03, im Test):
                // bei Filter "Available weapons" standen 12 anhakbare
                // Zeilen auf dem Schirm, der Schalter hakte alle 48 der Gesamtliste an und der Knopf meldete
                // "Produce selected (48)" - eine Zahl, die viermal so hoch war wie das, was der Spieler vor
                // sich sah. Die Munitions-Ansicht daneben rechnet seit je ueber BuildVisibleAmmoEntries;
                // das hier war die zweite, abgedriftete Kopie desselben Schalters.
                List<ThingDef> wovTogglable = (wovVisCache ?? new List<ThingDef>())
                    .Where(d => woInfo.TryGetValue(d, out WOInfo wi) && wi.researched && wi.hasBench).ToList();
                bool wovAllOn = wovTogglable.Count > 0 && wovTogglable.All(d => selectedWeapons.Contains(d));
                Rect wovMasterR = new Rect(6f, hy + 2f, 20f, 20f);
                if (wovTogglable.Count > 0)
                {
                    if (Widgets.ButtonImage(wovMasterR, wovAllOn ? Widgets.CheckboxOnTex : Widgets.CheckboxOffTex))
                    {
                        if (wovAllOn) foreach (ThingDef d in wovTogglable) selectedWeapons.Remove(d);
                        else foreach (ThingDef d in wovTogglable) selectedWeapons.Add(d);
                    }
                    TooltipHandler.TipRegion(wovMasterR, wovAllOn
                        ? "CER_GenerateAmmoBills_MasterUncheckTip".Translate()
                        : "CER_GenerateAmmoBills_MasterCheckTip".Translate(wovTogglable.Count.ToString()));
                }
            }
            SortHdr(82f, 178f, "CER_GenerateAmmoBills_WovColWeapon".Translate(), WovSort.Name);
            SortHdr(300f, 58f, "CER_GenerateAmmoBills_WovColScore".Translate(), WovSort.Score);
            LoadoutUtility.ColumnHeader(new Rect(362f, hy, 60f, 22f), "CER_GenerateAmmoBills_WovColSpecial".Translate(), false, false, false);
            // Verfuegbarkeit VOR Zutaten (2026-08-30), dann die Auftraege VOR der Menge - die
            // Spalten sind dafuer enger, damit rechts zwei Werkbank-Knoepfe je Bank Platz haben.
            SortHdr(430f, 130f, "CER_GenerateAmmoBills_WovColOwned".Translate(), WovSort.Owned);
            LoadoutUtility.ColumnHeader(new Rect(570f, hy, 80f, 22f), "CER_GenerateAmmoBills_WovColIngredients".Translate(), false, false, false);
            LoadoutUtility.ColumnHeader(new Rect(WovActiveX, hy, WovActiveW, 22f), "CER_GenerateAmmoBills_WovColActiveBills".Translate(), false, false, false);
            // Menge und Material (2026-08-13) - die 100px zwischen Bestand und Aktion lagen bisher leer, die
            // beiden neuen Zellen passen also hinein, ohne dass eine Spalte weichen muss.
            // Material VOR Menge (2026-08-30, Spalten getauscht).
            // Der Mengen-Kopf darf breiter sein als sein Feld - sonst faellt "Amount" in die kleine Schrift.
            LoadoutUtility.ColumnHeader(new Rect(776f, hy, 30f, 22f), "CER_GenerateAmmoBills_WovColMaterial".Translate(), false, false, false);
            LoadoutUtility.ColumnHeader(new Rect(812f, hy, 60f, 22f), "CER_GenerateAmmoBills_ColAmount".Translate(), false, false, false);
            // "Generate on..." ueber der Aktionsspalte - DERSELBE Schluessel wie im Munitionsmodus, keine
            // zweite Uebersetzung (2026-08-16). Die Spalte hatte
            // bisher gar keine Ueberschrift; mit mehreren Bank-Eintraegen darunter braucht sie eine.
            LoadoutUtility.ColumnHeader(new Rect(876f, hy, 136f, 22f), "CER_GenerateAmmoBills_ColGenerateOn".Translate(), false, false, false);

            if (overviewWeapons == null || Time.realtimeSinceStartup - overviewCacheTime > 1.5f)
            {
                overviewCacheTime = Time.realtimeSinceStartup;
                overviewWeapons = DefDatabase<ThingDef>.AllDefs
                    .Where(d => LoadoutUtility.IsRealWeaponDef(d) && !LoadoutUtility.IsUtilityWeapon(d))
                    .OrderByDescending(d => { LoadoutUtility.GetWeaponScore(d, out float s, out _); return s; })
                    .ToList();
                woInfo = new Dictionary<ThingDef, WOInfo>();
                foreach (ThingDef d in overviewWeapons)
                {
                    RecipeDef r = LoadoutUtility.GetRecipeFor(d);
                    bool res = r != null && LoadoutUtility.RecipeIsResearched(r);
                    LoadoutUtility.GetWeaponScore(d, out float sc, out _);
                    bool inLo = LoadoutManager.Loadouts.Any(l => l != null && !l.defaultLoadout && l.Slots != null && l.Slots.Any(s => s.thingDef == d));
                    bool inAssigned = false, activeBill = false;
                    foreach (Pawn p in LoadoutUtility.AllColonyPawns())
                    {
                        Loadout lo = p.GetLoadout();
                        if (lo != null && !lo.defaultLoadout && lo.Slots != null && lo.Slots.Any(s => s.thingDef == d)) { inAssigned = true; break; }
                    }
                    // Alle Auftraege einsammeln statt beim ersten abzubrechen (2026-08-16): der Muelleimer
                    // je Auftrag braucht die Auftraege selbst, nicht nur die Auskunft "es gibt welche".
                    // BEWUSST ohne den repeatMode-Filter der Munitionsseite: die nimmt nur "stelle her bis X",
                    // weil sie damit Zielmengen rechnet. Hier wird nichts gerechnet, hier wird geloescht -
                    // und ein "mache 5x"-Auftrag (genau das, was der Knopf links erzeugt) waere sonst
                    // unsichtbar und nicht wegzuklicken.
                    List<ActiveBillInfo> wovBills = new List<ActiveBillInfo>();
                    foreach (Building_WorkTable bench in LoadoutUtility.AllColonyBenches())
                    {
                        if (bench.billStack == null) continue;
                        foreach (Bill_Production bp in bench.billStack.Bills.OfType<Bill_Production>())
                        {
                            if (bp.recipe == null || bp.recipe.products == null) continue;
                            if (!bp.recipe.products.Any(pr => pr.thingDef == d)) continue;
                            // Menge in STUECK und die Art als Flagge - dieselbe Rechnung wie auf der Munitionsseite.
                            bool wUntil = LoadoutUtility.IsUntilBill(bp);
                            int wYield = bp.recipe.products.Count > 0 ? Mathf.Max(1, bp.recipe.products[0].count) : 1;
                            string amount = wUntil ? bp.targetCount.ToString()
                                : bp.repeatMode == BillRepeatModeDefOf.RepeatCount ? (bp.repeatCount * wYield).ToString()
                                : bp.repeatMode.LabelCap.ToString();
                            wovBills.Add(new ActiveBillInfo { bench = bench, bill = bp, amount = amount, isUntil = wUntil,
                                label = GetBenchLabel(bench) + " (" + amount + ")" });
                        }
                    }
                    // Dauerauftraege zuerst - dieselbe Regel wie auf der Munitionsseite (2026-08-30).
                    wovBills = wovBills.OrderByDescending(b => b.isUntil).ToList();
                    activeBill = wovBills.Count > 0;
                    WOInfo wi = new WOInfo { recipe = r, researched = res, hasBench = res && LoadoutUtility.WeaponBenchExists(d), inLoadout = inLo, inAssignedLoadout = inAssigned, hasActiveBill = activeBill, ings = LoadoutUtility.GetRecipeIngredients(r), score = sc, special = LoadoutUtility.DetectSpecialAbilities(d), activeBills = wovBills };
                    // Dieselbe Doppelten-Regel wie im Munitionsmodus (dort ~Zeile 375): bei "Doppelte
                    // ausblenden" zaehlt je Bank-BAUART nur die erste. Eine Kolonie mit sechs
                    // Fertigungsbaenken bekaeme sonst sechs identische Zeilen.
                    if (res)
                        foreach (Building_WorkTable b in LoadoutUtility.BenchesForRecipe(r))
                        {
                            if (CEReloadedMod.settings != null && CEReloadedMod.settings.hideDuplicateBenches
                                && wi.benches.Any(x => x.def == b.def)) continue;
                            wi.benches.Add(b);
                        }
                    woInfo[d] = wi;
                }
                // overviewWeapons is sorted by score descending, so the first entry carries the maximum.
                wovMaxScore = overviewWeapons.Count > 0 ? Mathf.Max(1f, woInfo[overviewWeapons[0]].score) : 1f;
            }

            // Auswahl und Sortierung nur neu rechnen, wenn sich wirklich etwas geaendert hat (2026-08-15,
            // 2026-08-15). overviewCacheTime steckt im Schluessel: laeuft der
            // 1,5s-Datencache oben ab, faellt dieser hier automatisch mit. Ein Klick auf Filter, Spalte,
            // Richtung oder ein Zeichen im Suchfeld aendert den Schluessel und schlaegt sofort durch -
            // die Liste haengt also nie hinterher. Wichtig fuer den Filter "Available weapons" und die
            // Sortierung "Owned": beide riefen OwnedTotal (voller Reich-Scan hinter 1s-Cache) pro Waffe
            // pro Bild, ueber ALLE Waffen-Defs des Spiels.
            string wovKey = overviewCacheTime.ToString("F3") + "|" + (int)wovFilter + "|" + (int)wovSort
                + "|" + (wovSortDesc ? 1 : 0) + "|" + weaponsSearch;
            if (wovVisCache == null || wovKey != wovVisCacheKey)
            {
                wovVisCacheKey = wovKey;
                wovVisCache = BuildVisibleOverviewWeapons();
            }

            float top = hy + 26f;
            const float bottomBar = 46f;
            Rect outRect = new Rect(0, top, inRect.width, inRect.height - top - bottomBar);
            // Zeilenhoehe waechst mit der Zahl der Werkbaenke - genau wie im Munitionsmodus (dort
            // rowHeight = Max(40, Max(activeBills, benches) * 24 + 8)). Erst die Summe bilden, dann
            // zeichnen: der Bildlauf braucht die Gesamthoehe, bevor die erste Zeile entsteht.
            float RowHeightOf(ThingDef wd)
            {
                WOInfo wi = woInfo.TryGetValue(wd, out WOInfo x) ? x : null;
                int benchRows = wi != null && wi.researched ? wi.benches.Count : 0;
                // Auch die Auftragsspalte kann laenger sein als die Bankspalte (drei Auftraege auf einer
                // Bank sind drei Zeilen, aber nur eine Bank) - genau wie im Munitionsmodus, dort
                // Max(activeBills, benches).
                int billRows = wi != null ? wi.activeBills.Count : 0;
                return Mathf.Max(34f, Mathf.Max(benchRows, billRows) * 24f + 10f);
            }
            float totalH = 4f;
            foreach (ThingDef wd in wovVisCache) totalH += RowHeightOf(wd);
            Rect viewRect = new Rect(0, 0, outRect.width - 16f, totalH);
            Widgets.BeginScrollView(outRect, ref weaponsScroll, viewRect);
            // Zwilling der Munitions-Ansicht - siehe dort (2026-09-04).
            if (wovVisCache.Count == 0)
            {
                GUI.color = new Color(1f, 1f, 1f, 0.55f);
                Widgets.Label(new Rect(6f, 6f, viewRect.width - 12f, 60f),
                    string.IsNullOrEmpty(weaponsSearch)
                        ? "CER_GenerateAmmoBills_NothingMatchesFilter".Translate(
                              "CER_GenerateAmmoBills_WovResearchedWeapons".Translate())
                        : "CER_GenerateAmmoBills_NothingMatchesSearch".Translate(weaponsSearch));
                GUI.color = Color.white;
            }
            float ry = 0f;
            foreach (ThingDef w in wovVisCache)
            {
                float rowH = RowHeightOf(w);
                // Scroll culling: rows outside the visible window only advance the cursor.
                if (ry + rowH < weaponsScroll.y || ry > weaponsScroll.y + outRect.height) { ry += rowH; continue; }

                WOInfo info = woInfo[w];
                bool producible = info.researched && info.hasBench;

                Rect row = new Rect(0, ry, viewRect.width, rowH - 2f);
                if (Mouse.IsOver(row)) Widgets.DrawHighlight(row);

                if (producible)
                {
                    bool selv = selectedWeapons.Contains(w);
                    bool ns = selv;
                    Widgets.Checkbox(new Vector2(6f, ry + 5f), ref ns, 22f);
                    if (ns != selv) { if (ns) selectedWeapons.Add(w); else selectedWeapons.Remove(w); }
                }
                else
                {
                    // Dark-red X marks a weapon that can't be produced (researched recipe / bench missing). Tinted a
                    // darker red so it reads distinctly from a normal unchecked (producible-but-unselected) checkbox.
                    selectedWeapons.Remove(w);
                    GUI.color = new Color(0.55f, 0.14f, 0.14f);
                    GUI.DrawTexture(new Rect(6f, ry + 5f, 22f, 22f), Widgets.CheckboxOffTex);
                    GUI.color = Color.white;
                }

                Widgets.ThingIcon(new Rect(44f, ry + 3f, 28f, 28f), w);
                Widgets.Label(new Rect(82f, ry + 6f, 210f, 24f), w.LabelCap.Truncate(208f));

                // Same bar style (and same normalisation: relative to the best weapon shown) as the Generate
                // Loadouts score column, so the two tables read identically.
                LoadoutUtility.ScoreBar(new Rect(300f, ry + 9f, 55f, 14f), info.score, wovMaxScore, null, false, "CER_GenerateAmmoBills_ScoreBarLabel".Translate(info.score.ToString("0")));

                // Obergrenze 84 statt 90 (2026-08-13): die Zutaten-Spalte beginnt bei 450, die Leiste durfte bis
                // 452 wachsen und ragte damit zwei Pixel hinein - vom Layout-Renderer gefunden, nicht mit dem
                // Auge. Es passen weiterhin dieselben drei Symbole plus "+N" hinein (19px je Symbol, 20px Reserve).
                // Der WAFFEN-Zeichner, nicht der Munitions-Zeichner (2026-08-30: Molotov und Stick Bomb trugen
                // ein Munitionszeichen - wieder zwei Implementierungen derselben Sache):
                // DrawAmmoEffectIcons stempelt auf JEDES Symbol die kleine Munitions-Marke - richtig fuer eine
                // Munitionszeile, falsch fuer eine Waffe. Nachschub-Fenster und Reiter 2 haben es immer ueber
                // DrawWeaponSpecialIcons gezeichnet; diese Ansicht war die Ausnahme. Jetzt nicht mehr.
                Dialog_GenerateLoadouts.DrawWeaponSpecialIcons(362f, ry + 6f, w, 60f);

                // Ingredients: the shared cell (sorted icons, "!" for out-of-storage, whole-cell tooltip). A stuffed
                // weapon appends "+mat."; the empty-cell "-" is skipped then, since the material IS the ingredient.
                float ix = 570f;
                if (info.ings.Count > 0 || !w.MadeFromStuff)
                    ix = LoadoutUtility.DrawIngredientCell(570f, 80f, ry + 4f, info.ings);
                if (w.MadeFromStuff)
                {
                    Rect matRect = new Rect(ix, ry + 6f, 58f, 22f);
                    GUI.color = new Color(1f, 1f, 1f, 0.55f);
                    Widgets.Label(matRect, "CER_GenerateAmmoBills_PlusMatLabel".Translate());
                    GUI.color = Color.white;
                    TooltipHandler.TipRegion(matRect, "CER_GenerateAmmoBills_MaterialTooltip".Translate());
                }

                // Same size (130x18) as the Generate Loadouts bar so the two read identically.
                var wSeg = LoadoutUtility.WeaponAvailability(w);
                // Same "mach das einheitlich" pass as the ammo row's own bar just above - every Availability
                // bar in the mod gets the where-is-it breakdown now.
                // TipWanted: der Orts-Tooltip laeuft ueber alle Karten - nur bauen, wenn er auch gezeigt wird.
                Rect wAvailRect = new Rect(430f, ry + 7f, 130f, 18f);
                LoadoutUtility.DrawAvailabilityBar(wAvailRect, wSeg, horizontal: true,
                    LoadoutUtility.TipWanted(wAvailRect) ? LoadoutUtility.BuildWeaponLocationTooltip(w) : null);

                Rect actRect = new Rect(876f, ry + 4f, 136f, 24f);
                if (producible)
                {
                    // EIN KLICK (2026-08-13, nach dem Vorbild der Munitionsauftraege: ein Klick genuegt).
                    // Vorher oeffnete dieser Knopf nur das
                    // Nachschub-Fenster, und produziert wurde erst dort - zwei Fenster fuer einen Auftrag,
                    // waehrend die Munitionsseite daneben es direkt in der Zeile macht.
                    // Die Menge steht links daneben (Vorgabe 1), das Material rechts davon als Symbolknopf,
                    // beides optional: ohne jede Vorarbeit erzeugt ein Klick genau ein Stueck aus dem
                    // Standardmaterial. Die Werkbank waehlt der Mod wie ueberall sonst - erste passende.
                    string cntStr = wovCount.TryGetValue(w, out string cs) ? cs : "1";
                    Rect cntRect = new Rect(812f, ry + 4f, 44f, 24f);
                    string newCnt = Widgets.TextField(cntRect, cntStr);
                    if (newCnt != cntStr) wovCount[w] = newCnt;
                    if (LoadoutUtility.TipWanted(cntRect)) TooltipHandler.TipRegion(cntRect, "CER_GenerateAmmoBills_WovCountTip".Translate());

                    ThingDef stuff = wovStuff.TryGetValue(w, out ThingDef st) ? st : DefaultStuffFor(w);
                    if (w.MadeFromStuff)
                    {
                        Rect matBtn = new Rect(776f, ry + 5f, 26f, 22f);
                        // Dieselbe Zelle wie im Equipment-Packs-Reiter (2026-08-30) - siehe LoadoutUtility.StuffButton.
                        if (LoadoutUtility.StuffButton(matBtn, stuff))
                        {
                            List<FloatMenuOption> opts = new List<FloatMenuOption>();
                            foreach (ThingDef s in GenStuff.AllowedStuffsFor(w))
                            {
                                ThingDef local = s;
                                opts.Add(new FloatMenuOption(local.LabelCap, () => wovStuff[w] = local));
                            }
                            if (opts.Count > 0) Find.WindowStack.Add(new FloatMenu(opts));
                        }
                        if (LoadoutUtility.TipWanted(matBtn))
                            TooltipHandler.TipRegion(matBtn, "CER_GenerateAmmoBills_WovMaterialTip".Translate(
                                stuff != null ? stuff.LabelCap.ToString() : "-"));
                    }

                    // EIN EINTRAG JE WERKBANK statt eines einzelnen "Produce" (2026-08-16): ein Werkbank-Symbol
                    // wie bei den Munitionsauftraegen, "Generate on" darueber, und bei mehreren Werkbaenken
                    // mehrere Eintraege pro Zeile.
                    // Woertlich dieselbe Bauform wie die Munitionsseite (dort ~Zeile 833): Bank-Symbolknopf
                    // mit Nummer bei Doppelten, daneben die Lupe zum Hinspringen, eine Zeile je Bank.
                    // Der bisherige Knopf hat stillschweigend die ERSTE passende Bank genommen - in einer
                    // Kolonie mit mehreren Baenken war damit nicht steuerbar, wo produziert wird, und nicht
                    // einmal sichtbar, wo es passiert ist.
                    float benchY = ry + 4f;
                    foreach (Building_WorkTable bench in info.benches)
                    {
                        string benchName = GetBenchLabel(bench);
                        if (!int.TryParse(cntStr, out int wovWant) || wovWant < 1) wovWant = 1;
                        RecipeDef wovRec = LoadoutUtility.GetRecipeFor(w);
                        int wovPressed = DrawBenchBillButtons(actRect.x, benchY, bench, benchName, wovWant);
                        if (wovPressed != 0)
                        {
                            if (wovRec == null) Messages.Message("CER_GenerateAmmoBills_WovNoBench".Translate(), MessageTypeDefOf.RejectInput, false);
                            else
                            {
                                if (wovPressed == 1)
                                    LoadoutUtility.AddOrRaiseOnceBill(bench, wovRec, wovWant, w.MadeFromStuff ? stuff : null, markManaged: false);
                                else
                                {
                                    Bill_Production wovExisting = bench.billStack?.Bills.OfType<Bill_Production>()
                                        .FirstOrDefault(b => b.recipe == wovRec && b.repeatMode == BillRepeatModeDefOf.TargetCount);
                                    LoadoutUtility.AddOrUpdateAmmoBill(bench, wovRec, wovWant + (wovExisting != null ? wovExisting.targetCount : 0), onlyIncrease: false, markManaged: false);
                                }
                                LoadoutUtility.RefreshBillsTab(bench);
                                Messages.Message("CER_GenerateAmmoBills_WovQueuedMsg".Translate(
                                    wovWant.ToString(), w.LabelCap.ToString(), bench.LabelCap.ToString()),
                                    MessageTypeDefOf.TaskCompletion, false);
                            }
                            overviewWeapons = null;
                            break;
                        }
                        benchY += 24f;
                    }
                }
                // Laufende Auftraege mit Muelleimer je Auftrag - dieselbe Bauform wie im Munitionsmodus
                // (dort ~Zeile 815). Steht ABSICHTLICH ausserhalb des producible-Zweigs: ein Auftrag kann
                // laufen, waehrend die Waffe gerade nicht produzierbar ist (Bank abgerissen, Forschung in
                // einem Mod entzogen) - genau dann will man ihn loeschen koennen.
                float wovActiveW = WovActiveW;
                if (info.activeBills.Count > 0)
                {
                    float billY = ry + 4f;
                    foreach (ActiveBillInfo abi in info.activeBills.ToList())
                    {
                        if (DrawActiveBillRow(new Rect(WovActiveX, billY, wovActiveW, 22f), abi))
                        {
                            overviewWeapons = null;   // Bankliste und Auftragsliste haengen am selben Cache
                            break;
                        }
                        billY += 24f;
                    }
                    GUI.color = Color.white;
                }

                if (!producible)
                {
                    GUI.color = Color.gray;
                    // Schmaler NUR, wenn rechts daneben wirklich Auftraege stehen - sonst behaelt der Text
                    // seine alte Breite. Ein nicht produzierbares Rezept mit laufendem Auftrag ist der
                    // seltene Fall (Bank abgerissen), und dort ist der Auftrag die wichtigere Auskunft.
                    Rect stRect = new Rect(actRect.x, actRect.y + 1f, info.activeBills.Count > 0 ? 78f : 146f, 22f);
                    Widgets.Label(stRect, !info.researched ? "CER_GenerateAmmoBills_WovNotResearched".Translate() : "CER_GenerateAmmoBills_WovNoBench".Translate());
                    GUI.color = Color.white;
                    TooltipHandler.TipRegion(stRect, !info.researched
                        ? "CER_GenerateAmmoBills_WovNotResearchedTooltip".Translate()
                        : "CER_GenerateAmmoBills_WovNoBenchTooltip".Translate());
                }

                ry += rowH;
            }
            Widgets.EndScrollView();

            // Bottom bar: destructive action left, the primary action pinned right (the window's X closes).
            // Dieselbe Reihenrechnung wie in der Munitions-Ansicht (2026-09-04): der linke Knopf war hier
            // gar nicht angemeldet, und rechts lief ueberhaupt kein BottomBarFont - zwei Knoepfe, zwei
            // Messungen, auf Deutsch also derselbe Groessenbruch, nur noch nicht aufgefallen.
            string delWepLabel = "CER_GenerateAmmoBills_DeleteAllWeaponBillsButton".Translate().ToString();
            float delWepW = LoadoutUtility.BottomBtnW(delWepLabel, 190f, 300f);
            Rect delWepRect = new Rect(0f, inRect.height - LoadoutUtility.BottomBarH, delWepW, LoadoutUtility.BottomBtnH);

            // Die Zahl im Knopf nennt, was der Klick WIRKLICH anlegt (2026-09-03, siehe den Kopf-Schalter
            // oben): produziert wird ohnehin nur, was der Filter gerade zeigt - stand hier die Zahl aller
            // je angehakten Waffen, versprach der Knopf mehr als er tat.
            HashSet<ThingDef> visibleSel = new HashSet<ThingDef>(wovVisCache ?? new List<ThingDef>());
            int selVisibleCount = selectedWeapons.Count(w => visibleSel.Contains(w));
            string selLabel = selVisibleCount > 0 ? "CER_GenerateAmmoBills_ProduceSelectedCount".Translate(selVisibleCount.ToString()) : "CER_GenerateAmmoBills_ProduceSelectedLabel".Translate();

            GameFont prevWepBarFont = Text.Font;
            Text.Font = LoadoutUtility.BottomBarFont(
                (delWepW, delWepLabel, false),
                (LoadoutUtility.PrimaryBtnW, selLabel.ToString(), false));

            if (LoadoutUtility.ButtonTextFit(delWepRect, delWepLabel))
            {
                Find.WindowStack.Add(LoadoutUtility.CompactConfirmation(
                    "CER_GenerateAmmoBills_DeleteAllWeaponBillsConfirm".Translate(),
                    () =>
                    {
                        int n = LoadoutUtility.DeleteAllWeaponBills();
                        Messages.Message(n == 1
                            ? "CER_GenerateAmmoBills_DeletedWeaponBillsSingular".Translate(n.ToString())
                            : "CER_GenerateAmmoBills_DeletedWeaponBillsPlural".Translate(n.ToString()), MessageTypeDefOf.TaskCompletion, false);
                    }, destructive: true));
            }
            TooltipHandler.TipRegion(delWepRect, "CER_GenerateAmmoBills_DeleteAllWeaponBillsTooltip".Translate());

            float xr = inRect.width;
            if (LoadoutUtility.BottomActionButton(inRect, ref xr, LoadoutUtility.PrimaryBtnW, selLabel, selVisibleCount > 0))
            {
                // Scoped to what the CURRENT filter/search actually shows - a weapon checked earlier and then
                // hidden by a later filter change would otherwise get silently queued for production without
                // the player seeing it was still selected. The underlying selection itself is untouched
                // (flipping the filter back still shows it checked); only what gets produced right now is
                // limited to what's visible.
                HashSet<ThingDef> visibleNow = new HashSet<ThingDef>(wovVisCache);
                List<ThingDef> toProduce = selectedWeapons.Where(w => visibleNow.Contains(w)).ToList();
                int hidden = selectedWeapons.Count - toProduce.Count;
                if (hidden > 0)
                    Messages.Message("CER_GenerateAmmoBills_HiddenWeaponsMessage".Translate(hidden.ToString()), MessageTypeDefOf.CautionInput, false);
                if (toProduce.Count > 0) ProduceWeapons(toProduce);
            }
            Text.Font = prevWepBarFont;
        }


        // Herausgezogen 2026-08-15, damit der Sicht-Cache in
        // DoAmmoListContents genau EINE Aufbaustelle hat. Inhaltlich unveraendert gegenueber der
        // frueheren Inline-Fassung - liest ausschliesslich Felder, keine Zeichen-Locals.
        private List<AmmoEntry> BuildVisibleAmmoEntries()
        {
            var visibleEntries = ammoEntries.Where(e =>
                (ammoTypeFilter == "All" || e.ammoType == ammoTypeFilter) &&
                (string.IsNullOrEmpty(nameFilter) || e.ammoDef.label.IndexOf(nameFilter, StringComparison.OrdinalIgnoreCase) >= 0) &&
                (benchFilter == "All benches" || e.validBenches.Any(b => GetBenchLabel(b) == benchFilter))
            ).ToList();

            if (currentSortMode == SortMode.Name)
            {
                visibleEntries = sortDescending ? visibleEntries.OrderByDescending(e => e.ammoDef.label).ToList() : visibleEntries.OrderBy(e => e.ammoDef.label).ToList();
            }
            else if (currentSortMode == SortMode.Special)
            {
                visibleEntries = sortDescending
                    ? visibleEntries.OrderByDescending(e => LoadoutUtility.GetAmmoSpecialSortKey(e.ammoDef)).ThenBy(e => e.ammoDef.label).ToList()
                    : visibleEntries.OrderBy(e => LoadoutUtility.GetAmmoSpecialSortKey(e.ammoDef)).ThenBy(e => e.ammoDef.label).ToList();
            }
            else if (currentSortMode == SortMode.Assigned)
            {
                // This column now draws the shared GetAmmoOwnership-fed DrawAvailabilityBar (carried/
                // assigned-in-store/free-stored/in-production), not the old bare assigned-pawn-count dot -
                // sort by the same total the bar's own segments sum to (its rendered width), not the
                // leftover pawn-count metric that column no longer visualizes.
                int AbAvailTotal(AmmoEntry e)
                {
                    LoadoutUtility.GetAmmoOwnership(e.ammoDef, out int co, out int ca, out int ais, out int fs, out int ip);
                    return co + ca + ais + fs + ip;
                }
                visibleEntries = sortDescending
                    ? visibleEntries.OrderByDescending(e => AbAvailTotal(e)).ThenBy(e => e.ammoDef.label).ToList()
                    : visibleEntries.OrderBy(e => AbAvailTotal(e)).ThenBy(e => e.ammoDef.label).ToList();
            }
            else if (currentSortMode == SortMode.BillStatus)
            {
                visibleEntries = sortDescending ? visibleEntries.OrderByDescending(e => e.activeBills.Count).ThenBy(e => e.ammoDef.label).ToList() : visibleEntries.OrderBy(e => e.activeBills.Count).ThenBy(e => e.ammoDef.label).ToList();
            }
            else if (currentSortMode == SortMode.Loadouts)
            {
                visibleEntries = sortDescending 
                    ? visibleEntries.OrderByDescending(e => string.Join(", ", e.loadouts.ToArray())).ThenBy(e => e.ammoDef.label).ToList() 
                    : visibleEntries.OrderBy(e => string.Join(", ", e.loadouts.ToArray())).ThenBy(e => e.ammoDef.label).ToList();
            }
            return visibleEntries;
        }


        // Herausgezogen 2026-08-15 - siehe BuildVisibleAmmoEntries,
        // gleiche Bauart, gleicher Grund. Liest nur Felder.
        private List<ThingDef> BuildVisibleOverviewWeapons()
        {
            List<ThingDef> vis = overviewWeapons
                .Where(w =>
                {
                    WOInfo i = woInfo[w];
                    // Found-only gear (persona/unique weapons, wraith blade, ...) has no crafting recipe, so it can
                    // never be produced - never list it in the produce-bills overview, whatever the filter.
                    if (i.recipe == null) return false;
                    bool producible = i.researched && i.hasBench;
                    bool pass;
                    switch (wovFilter)
                    {
                        case WovFilter.AssignedLoadouts:   pass = i.inAssignedLoadout; break;               // in-loadout not-researched still shows
                        case WovFilter.AvailableWeapons:   pass = OwnedTotal(w) > 0; break;                  // owned only - matches "Available" everywhere else
                        case WovFilter.ResearchedWeapons:  pass = i.researched; break;                       // bench-less researched still shows ("No bench" tells why)
                        case WovFilter.ActiveBills:        pass = i.hasActiveBill; break;
                        default:                         pass = true; break;                                 // kann nicht auftreten, faengt nur neue Werte ab
                    }
                    if (!pass) return false;
                    return string.IsNullOrEmpty(weaponsSearch) || (w.label != null && w.label.IndexOf(weaponsSearch, StringComparison.OrdinalIgnoreCase) >= 0);
                })
                .ToList();
            switch (wovSort)
            {
                case WovSort.Name: vis = (wovSortDesc ? vis.OrderByDescending(w => w.label) : vis.OrderBy(w => w.label)).ToList(); break;
                case WovSort.Owned: vis = (wovSortDesc ? vis.OrderByDescending(w => OwnedTotal(w)) : vis.OrderBy(w => OwnedTotal(w))).ToList(); break;
                default: vis = (wovSortDesc ? vis.OrderByDescending(w => woInfo[w].score) : vis.OrderBy(w => woInfo[w].score)).ToList(); break;
            }
            return vis;
        }

        private static int OwnedTotal(ThingDef w)
        {
            LoadoutUtility.GetWeaponInventory(w, out int total, out _, out _, out _);
            return total;
        }


        private void ProduceWeapons(List<ThingDef> ws)
        {
            Dictionary<ThingDef, int> main = new Dictionary<ThingDef, int>();
            foreach (ThingDef w in ws) main[w] = main.TryGetValue(w, out int c) ? c + 1 : 1;
            if (main.Count == 0) { Messages.Message("CER_GenerateAmmoBills_NoWeaponsSelected".Translate(), MessageTypeDefOf.RejectInput, false); return; }
            // The one supply popup everywhere: same rows, columns and total-cost strip as the Assign/Arm flows.
            Find.WindowStack.Add(new Dialog_SupplyLoadouts(new Dialog_SupplyLoadouts.Request { mainWeapons = main }));
        }

        // Returns the queued target round count (0 when nothing was queued) so callers can report what happened.
        //
        // targetOverride ist der GENAUE Lagerbestand, der als "stelle her bis X" gesetzt wird - ungerundet
        // (2026-08-09): nicht mehr gerundet - der genaue Wert, den die Kurve liefert, wird als "until"-Menge
        // gesetzt. Vorher gab es nur batchesOverride, und daraus wurde wieder
        // batches * yield gerechnet: bei CEs Mehrhundert-Schuss-Rezepten wurde aus 60 Schuss Bedarf ein
        // stehendes "halte 500 im Lager", und wegen Max(1, ...) bekam auch der kleinste Bedarf einen vollen
        // Durchgang. Supply Loadouts hatte genau das schon abgeschafft; dieser Pfad zog nach.
        // Der Batches-Pfad bleibt bewusst multiplikativ: dort hat der Spieler eine Anzahl DURCHGAENGE in das
        // Feld getippt, "3" heisst dort also wirklich dreimal die Rezeptausbeute.
        // EINE Auftragszeile fuer BEIDE Ansichten (2026-08-30, Code und Layout vereinheitlicht, wo es geht):
        // die Munitions- und die Waffen-Ansicht hatten je einen eigenen Nachbau derselben
        // Zelle, und deshalb fehlte auf der Munitionsseite das Art-Symbol, das die Waffenseite schon hatte.
        // Aufbau: Muelleimer | Werkbanksymbol mit der Auftragsart als Marke OBEN RECHTS (dieselbe Stelle wie an den
        // Erzeugen-Knoepfen) | Menge in STUECK. Liefert true, wenn der Auftrag geloescht wurde.
        private bool DrawActiveBillRow(Rect cell, ActiveBillInfo info)
        {
            GUI.color = Color.white;
            if (Widgets.ButtonImage(new Rect(cell.x, cell.y + 1f, 20f, 20f), IconTrash))
            {
                info.bench.billStack.Delete(info.bill);
                LoadoutUtility.RefreshBillsTab(info.bench);
                return true;
            }
            Rect benchIcon = new Rect(cell.x + 25f, cell.y + 1f, 20f, 20f);
            if (info.bench != null && info.bench.def.uiIcon != null) GUI.DrawTexture(benchIcon, info.bench.def.uiIcon);
            LoadoutUtility.DrawBillModeIcon(new Rect(benchIcon.xMax - 10f, benchIcon.y - 4f, 13f, 13f), info.isUntil);
            string id = GetBenchId(info.bench);
            Rect lbl = new Rect(cell.x + 52f, cell.y, Mathf.Max(10f, cell.width - 56f), 22f);
            Widgets.Label(lbl, ((id != "" ? id + " " : "") + "(" + info.amount + ")").Truncate(lbl.width));
            if (LoadoutUtility.TipWanted(cell)) TooltipHandler.TipRegion(cell, info.label);
            return false;
        }

        // EIN Knopfpaar fuer beide Ansichten: links x1 (Einzelauftrag), rechts Unendlichkeit (stelle her bis), dahinter
        // die Lupe. Die Marke sitzt oben rechts auf dem jeweiligen Knopf. Rueckgabe: 0 nichts, 1 x1, 2 unendlich.
        // Beide Knoepfe arbeiten mit DERSELBEN Menge aus der Spalte davor - das x1 ist ein Symbol fuer "einmalig",
        // keine Stueckzahl (2026-08-30: x1 fuegt die eingetragene Menge hinzu, nicht nur 1 - das x1 ist nur
        // symbolisch).
        private int DrawBenchBillButtons(float x, float y, Building_WorkTable bench, string benchName, int amount, int onceYield = 1)
        {
            int result = 0;
            string id = GetBenchId(bench);
            Rect onceRect = new Rect(x, y, 54f, 22f);
            // Der x1-Tooltip nennt die Stueckzahl, die WIRKLICH entsteht (2026-08-31, im Test:
            // Feld 200, Auftrag 500): ein Einzelauftrag zaehlt DURCHGAENGE,
            // Munition entsteht also nur in ganzen Chargen - aufgerundet, wie GenerateBillForEntry es
            // rechnet (dieselbe CeilToInt-Formel). Beim Unendlich-Knopf bleibt die Feldzahl richtig,
            // ein "bis X"-Ziel kennt keine Chargen.
            int onceAmount = Mathf.Max(1, Mathf.CeilToInt(amount / (float)Mathf.Max(1, onceYield))) * Mathf.Max(1, onceYield);
            if (LoadoutUtility.BenchIconButton(onceRect, bench, id,
                    "CER_GenerateAmmoBills_GenerateOnceTooltip".Translate(benchName, onceAmount.ToString()))) result = 1;
            LoadoutUtility.DrawBillModeIcon(new Rect(onceRect.xMax - 13f, onceRect.y - 3f, 13f, 13f), false);
            Rect untilRect = new Rect(x + 58f, y, 54f, 22f);
            if (LoadoutUtility.BenchIconButton(untilRect, bench, id,
                    "CER_GenerateAmmoBills_GenerateAdditiveTooltip".Translate(benchName, amount.ToString()))) result = 2;
            LoadoutUtility.DrawBillModeIcon(new Rect(untilRect.xMax - 13f, untilRect.y - 3f, 13f, 13f), true);
            Rect searchRect = new Rect(x + 116f, y + 1f, 20f, 20f);
            if (Widgets.ButtonImage(searchRect, TexButton.Search)) LoadoutUtility.JumpToBench(bench);
            TooltipHandler.TipRegion(searchRect, "CER_GenerateAmmoBills_JumpToWorkbenchTooltip".Translate());
            return result;
        }

        private int GenerateBillForEntry(AmmoEntry entry, Building_WorkTable bench, int? targetOverride = null, bool additive = false, bool markManaged = true)
        {
            int target;
            if (targetOverride.HasValue) target = targetOverride.Value;
            else if (!int.TryParse(entry.amountString, out int amount)) return 0;
            else target = amount;
            if (target <= 0) return 0;

            // `additive` ist der Knopf in der Spalte "Erzeugen auf" (2026-08-30): er legt die Menge auf einen schon
            // bestehenden "stelle her bis"-Auftrag DRAUF. Die Sammelwege (Auto-generate, Arm my Colony) setzen die
            // Zielmenge dagegen weiterhin absolut - sie rechnen den Bedarf aus, statt ihn aufzuaddieren.
            if (additive)
            {
                Bill_Production existing = bench.billStack?.Bills.OfType<Bill_Production>()
                    .FirstOrDefault(b => b.recipe == entry.recipeDef && b.repeatMode == BillRepeatModeDefOf.TargetCount);
                if (existing != null) target += existing.targetCount;
            }
            LoadoutUtility.AddOrUpdateAmmoBill(bench, entry.recipeDef, target, onlyIncrease: false, markManaged: markManaged);
            LoadoutUtility.RefreshBillsTab(bench);
            return target;
        }
        
        private void GenerateBills()
        {
            int count = 0;
            // Nur was gerade WIRKLICH in der Tabelle steht (2026-08-02 gefixt): vorher lief die
            // Sammelaktion ueber ammoEntries, also auch ueber Zeilen, die der Such- oder Bench-Filter
            // gerade ausblendet. Man hakt etwas an, tippt dann ins Suchfeld, und bekommt trotzdem einen
            // Auftrag fuer die verschwundene Zeile. Die Waffen-Uebersicht warnt in genau diesem Fall
            // schon lange; hier fehlte es.
            // BuildVisibleAmmoEntries statt einer zweiten, von Hand nachgebauten Filterbedingung
            // (2026-09-03): die Kopie hier kannte den Typ-Filter nicht, der spaeter dazugekommen ist -
            // "nur Brandmunition anzeigen" und dann Erzeugen legte trotzdem Auftraege fuer ausgeblendete
            // Sorten an. Eine Frage, eine Rechnung.
            foreach (AmmoEntry entry in BuildVisibleAmmoEntries())
            {
                if (!selectedAmmoDefs.Contains(entry.ammoDef)) continue;
                if (entry.validBenches.Count == 0) continue;
                
                Building_WorkTable benchToUse = null;
                if (entry.activeBills.Count > 0)
                {
                    benchToUse = entry.activeBills[0].bench;
                }
                else
                {
                    // Bench-Filter auch bei der Sammelaktion beachten - die Pro-Zeile-Auswahl
                    // "Generate on..." tut das laengst, hier wurde blind validBenches[0] genommen und
                    // damit womoeglich auf einer Bank eingeplant, die die Tabelle gar nicht zeigt.
                    benchToUse = benchFilter == "All benches"
                        ? entry.validBenches[0]
                        : (entry.validBenches.FirstOrDefault(b => GetBenchLabel(b) == benchFilter) ?? entry.validBenches[0]);
                }
                
                GenerateBillForEntry(entry, benchToUse);
                count++;
            }
            if (count > 0)
            {
                Messages.Message("CER_GenerateAmmoBills_AddedUpdatedBillsMessage".Translate(count.ToString()), MessageTypeDefOf.TaskCompletion, false);
                RefreshBenchesAndAmmo();
            }
        }

        // Arm-my-Colony: top up the ammo bills for what colonists are ACTUALLY assigned, demand-scaled. Deliberately
        // not "every created loadout" - that produced for every template ever generated, most of which nobody wears.
        // Loadouts assigned later in the run get their ammo from the assign step's supply popup, which is demand-
        // based by construction, so nothing falls through between the two.
        // Der frueher noetige Parameter pawnsWereUnassigned ist entfallen (2026-08-09): er existierte nur, um
        // den Prune-Aufruf am Ende dieser Methode im resetPawns-Fall zu unterdruecken, und dieser Aufruf ist
        // ganz weg - siehe unten.
        public void ArmAutoGenerateMissing(ArmReport rep)
        {
            filterMode = AmmoBillFilterMode.AssignedLoadouts;
            RefreshBenchesAndAmmo();
            int made = AutoGenerateAll(rep != null ? rep.ammoQueued : null);
            if (rep != null) rep.ammoBillsCreated += made;

            // HIER WIRD NICHTS MEHR GELOESCHT (2026-08-09). Grundsatz: nichts ohne Nachfrage loeschen -
            // geloescht wird nur ueber das Supply-Fenster.
            // Frueher rief dieser Schritt PruneUnneededAmmoBills() und war damit die letzte Stelle im
            // Mod, an der Auftraege ohne Zutun des Spielers verschwanden - ohne Popup, ohne Kaestchen, in ALLEN
            // drei Modi. Ausgerechnet Cautious, dessen Zusammenfassung "es wird nichts geloescht" verspricht,
            // loeschte mit.
            //
            // Ein Teilfix gab es schon (der pawnsWereUnassigned-Vorbehalt): nach einem resetPawns ist niemand
            // mehr zugewiesen, also lautet "braucht das noch jemand?" fuer JEDE Munition nein und die Sense
            // nahm auch handgesetzte Auftraege mit. Das war aber nur der schlimmste Einzelfall derselben
            // Regelverletzung, nicht die Regel.
            //
            // Verloren geht dadurch nichts: der Nachschub-Popup zeigt ueberfluessige Auftraege in seinem
            // eigenen Abschnitt mit Kaestchen zum Abwaehlen, und er ist in DIESEM Ablauf zweimal verdrahtet -
            // Dialog_AssignLoadouts.OpenProducePopup (bei jedem Apply) und
            // LoadoutUtility.CheckAndOpenMissingProductionPopup (Ende des Laufs), beide ueber
            // LoadoutUtility.AttachObsoleteCleanup. Dazu der eigene Knopf im Munitions-Tab. Das Aufraeumen
            // existiert also weiterhin - nur dort, wo der Spieler es sieht und abhakt.
        }

        // Bills whose ammo nobody currently assigned demands anymore - production that kept running after a
        // loadout was edited/deleted or a colonist reassigned, quietly consuming materials for nothing. Distinct
        // from an entry with active bills AND real demand (left alone) or no bills at all (nothing to prune).
        // genericallyCovered gates this too, so the row highlight, the bottom-bar count and the actual prune all
        // agree - and so a caliber somebody asks for generically never gets its siblings deleted (2026-08-09).
        // !IsSelfAmmoWeapon: ZWEITE Fundstelle desselben Fehlers vom 2026-08-14 (die erste sass in
        // LoadoutUtility.UnneededAmmoBillCandidates, siehe den ausfuehrlichen Block dort). Wurfsprengstoff
        // und Wurfwaffen - Satchel charge, Granaten, Speere - sind ThingClass AmmoThing und stehen deshalb
        // als Munitionszeile in diesem Reiter. `assignedDemand` kommt aber aus der Munitions-Bedarfsrechnung,
        // und die schliesst genau diese Dinger ausdruecklich aus (sie gehoeren dem Waffen-Bereich, der echten
        // Besitz gegenrechnet). Fuer sie ist `assignedDemand == 0` also KEINE Aussage ueber Bedarf, sondern
        // nur die Folge davon, dass hier niemand zustaendig ist - und "0" hiess bisher "loeschen".
        // Fehlszenario, im Test am 15.08. gesehen: ein Kolonist traegt Satchel charges als
        // Zweitwaffe, der Auftrag dafuer laeuft - und dieser Loeschpfad haette ihn als ueberfluessig
        // weggeraeumt, waehrend der Knopf darueber "(0)" anzeigte. Denn die ANZEIGE (unneededAmmoDefs, siehe
        // RefreshAmmoList) laeuft seit dem 14.08. ueber UnneededAmmoBillCandidates und kannte den Ausschluss
        // bereits; nur dieser Pfad hier nicht. Zwei Zahlen fuer dieselbe Frage, und geloescht hat die falsche.
        private List<AmmoEntry> UnneededAmmoEntries() => ammoEntries.Where(e => e.activeBills.Count > 0 && e.assignedDemand == 0
            && LoadoutUtility.IsRealAmmoDef(e.ammoDef) && !e.genericallyCovered).ToList();

        // Targeted prune - only bills nothing currently needs - as opposed to ArmDeleteAllAmmoBills's blunt
        // "delete everything" reset below. `only` restricts the prune to a caller-approved subset (e.g. the
        // Supply Loadouts popup's own per-item opt-out selection, 2026-08-01) - null/omitted prunes every
        // unneeded entry unconditionally, same as before.
        public int PruneUnneededAmmoBills(HashSet<ThingDef> only = null)
        {
            RefreshBenchesAndAmmo();
            int n = 0;
            HashSet<Building_WorkTable> touched = new HashSet<Building_WorkTable>();
            List<AmmoEntry> viaEntries = UnneededAmmoEntries().Where(e => only == null || only.Contains(e.ammoDef)).ToList();
            foreach (AmmoEntry entry in viaEntries)
                foreach (ActiveBillInfo info in entry.activeBills.ToList())
                {
                    if (info.bench != null && info.bench.billStack != null && info.bench.billStack.Bills.Contains(info.bill))
                    {
                        info.bench.billStack.Delete(info.bill);
                        touched.Add(info.bench);
                        n++;
                    }
                }
            // Eine AUSDRUECKLICH angehakte Zeile muss auch wirklich verschwinden (2026-08-12, im Test
            // belegt: das Nachschub-Fenster listete drei ueberfluessige Auftraege, abgehakt wurden zwei, und
            // die dritte Zeile - 23x75mmR (Buck) - stand danach unveraendert da). Ursache: die Liste im
            // Fenster kommt aus LoadoutUtility.UnneededAmmoBillCandidates und scannt ALLE Baenke, dieser
            // Loeschpfad lief aber ueber `ammoEntries`, und das ist die nach Bereichs-/Suchfilter gebaute
            // Liste des Bill-Overviews. Ein Auftrag fuer eine Munition, die dort gerade nicht drinsteht, war
            // unloeschbar - das Fenster bot also eine Aktion an, die nichts tun konnte.
            // Exakt dieselbe Diagnose steht seit dem 2026-07-30 im Kommentar der Schwestermethode
            // ArmDeleteAllAmmoBills, die deshalb direkt ueber die Baenke geht; hierher hat sie es nie
            // geschafft.
            // BEWUSST nur fuer den `only`-Pfad, also fuer eine echte Auswahl des Spielers. Der pauschale
            // Aufruf (only == null, u.a. aus dem Arm-Lauf) behaelt seinen bisherigen, engeren Umfang - ihn
            // hier stillschweigend zu verbreitern hiesse, dass ein Arm-Durchgang ploetzlich mehr Auftraege
            // loescht als vorher, und genau in die Richtung gibt es schon einen offenen Punkt.
            // Der Durchgang laeuft ueber JEDEN angehakten Def, auch ueber die, die oben schon eine Zeile
            // hatten (2026-08-12): `entry.activeBills` sammelt nur "stelle her bis X"-Auftraege, ein
            // "mache 5x"-Auftrag fuer dieselbe Munition blieb sonst stehen und die Zeile kam beim naechsten
            // Oeffnen unveraendert wieder. Doppelt geloescht werden kann nichts - was der erste Durchgang
            // entfernt hat, steht hier nicht mehr im billStack.
            if (only != null)
                foreach (ThingDef def in only)
                {
                    if (def == null) continue;
                    foreach (Building_WorkTable bench in LoadoutUtility.AllColonyBenches())
                    {
                        if (bench.billStack == null) continue;
                        foreach (Bill b in bench.billStack.Bills.ToList())
                        {
                            Bill_Production bp = b as Bill_Production;
                            if (bp?.recipe?.products == null) continue;
                            if (!bp.recipe.products.Any(pr => pr.thingDef == def)) continue;
                            bench.billStack.Delete(b);
                            touched.Add(bench);
                            n++;
                        }
                    }
                }
            foreach (Building_WorkTable b in touched) LoadoutUtility.RefreshBillsTab(b);
            RefreshBenchesAndAmmo();
            return n;
        }

        // Arm-my-Colony: delete ammo bills on every colony bench so the run rebuilds them from scratch. Scope
        // (2026-08-01) - ManagedOnly only deletes bills this mod's own tooling queued (LoadoutUtility.
        // IsManagedBill); All deletes every bill for a recognized ammo type regardless of who set it up (the
        // original, only behavior before scope existed - including any the player queued themselves).
        // Scans every colony bench directly (mirrors LoadoutUtility.DeleteAllWeaponBills's own unconditional
        // approach) instead of going through the filterMode-scoped `ammoEntries` list - the earlier version
        // only ever saw bills for ammo defs relevant under whatever "Ammunition:" browsing filter happened to
        // be selected, so bills for out-of-scope/orphaned ammo types were invisible and silently survived a
        // "Delete ammo bills" press (2026-07-30: bills kept accumulating despite using this).
        public int ArmDeleteAllAmmoBills(LoadoutUtility.BillDeleteScope scope = LoadoutUtility.BillDeleteScope.All)
        {
            int n = 0;
            if (scope == LoadoutUtility.BillDeleteScope.None) return n;
            HashSet<Building_WorkTable> touched = new HashSet<Building_WorkTable>();
            foreach (Building_WorkTable bench in LoadoutUtility.AllColonyBenches())
            {
                if (bench.billStack == null) continue;
                foreach (Bill bill in bench.billStack.Bills.ToList())
                {
                    Bill_Production bp = bill as Bill_Production;
                    if (bp == null || bp.recipe?.products == null) continue;
                    if (scope == LoadoutUtility.BillDeleteScope.ManagedOnly && !LoadoutUtility.IsManagedBill(bp)) continue;
                    if (bp.recipe.products.Any(p => p.thingDef != null && LoadoutUtility.IsAmmoDef(p.thingDef)))
                    {
                        bench.billStack.Delete(bill);
                        touched.Add(bench);
                        n++;
                    }
                }
            }
            foreach (Building_WorkTable b in touched) LoadoutUtility.RefreshBillsTab(b);
            RefreshBenchesAndAmmo();
            return n;
        }

        // The standalone tab's own "Auto-generate" - builds a Dialog_SupplyLoadouts request for every ammo
        // that's missing a bill for the assigned weapons (the only scope since 2026-09-07), independent of whatever the
        // "Ammunition:" browsing filter happens to be set to right now, and opens the same review popup every
        // other produce-missing entry point in the mod uses - now with its own scope radio + obsolete-bill
        // cleanup toggle drawn INSIDE that popup (2026-08-01, moved off this tab's own settings row into
        // the popup). Temporarily swaps filterMode to compute the
        // right relevant-ammo set (GetRelevantAmmoDefs is filterMode-driven), then restores it so the
        // player's own browsing filter/table is untouched by having clicked this button.
        private void OpenMissingAmmoSupplyPopup()
        {
            AmmoBillFilterMode saved = filterMode;
            filterMode = AmmoBillFilterMode.AssignedLoadouts;
            RefreshBenchesAndAmmo();

            var specificAmmo = new Dictionary<ThingDef, int>();
            var benches = new Dictionary<ThingDef, Building_WorkTable>();
            foreach (AmmoEntry entry in ammoEntries)
            {
                // Blockierte Munition (unerforscht / keine Bank) darf NICHT mehr stillschweigend hier
                // aussortiert werden, sobald jemand sie wirklich braucht (2026-08-12, im Test mit
                // eigenem Aufbau belegt: ein Kolonist verlangte ausschliesslich .30-06 Springfield (AP-I),
                // Menge 40 - im Nachschub-Fenster erschien dafuer GAR KEINE Zeile, auch nicht mit
                // abgeschaltetem "nur Fehlendes zeigen"). Der Bedarf verschwand also spurlos, und der
                // Bildschirm meldete "nichts fehlt", waehrend ein Kolonist dauerhaft leer ausging.
                // Dialog_SupplyLoadouts kann diesen Fall laengst: BuildAmmoEntries legt fuer unerforschte
                // Munition ausdruecklich eine nicht auswaehlbare "kann nicht produziert werden"-Zeile an
                // (siehe dortigen Kommentar). Genau wie beim Auftragsgroessen-Fehler eine Stunde vorher war
                // der Filter im AUFRUFER das Problem - was hier wegfaellt, erreicht das Fenster nie.
                // Ohne Bedarf bleibt es beim alten Verhalten: eine unerforschte Munition, die niemand
                // verlangt, hat im Nachschub-Fenster nichts verloren.
                bool blocked = !entry.isResearched || entry.validBenches.Count == 0;
                if (blocked && entry.assignedDemand <= 0) continue;
                // Ungerundet, gleiche Begruendung wie in GenerateBillForEntry (2026-08-09): was hier in die
                // Popup-Vorschau geht, muss dieselbe Zahl sein, die spaeter der Auftrag traegt - sonst zeigt
                // die Vorschau 500 und der Auftrag lautet auf 60 (oder umgekehrt).
                // Dieselbe Regel wie in AutoGenerateAll (2026-08-09): Bedarf wenn vorhanden, sonst die
                // eingetippte Chargenzahl. Vorher haing es hier zusaetzlich am Scope-Schalter, in
                // AutoGenerateAll am Filter - zwei verschiedene Weichen fuer dieselbe Frage, und genau
                // deshalb konnten die beiden Wege unterschiedliche Zahlen liefern.
                int target;
                if (entry.assignedDemand > 0)
                    target = entry.assignedDemand;
                else if (!int.TryParse(entry.amountString, out int amount) || amount <= 0)
                    continue;
                else
                    target = amount;
                // Reicht der vorhandene Auftrag, statt: gibt es ueberhaupt einen (2026-08-12, im Test
                // mit sauberer A/B-Probe belegt). Hier stand `if (entry.activeBills.Count > 0) continue;` VOR
                // der Bedarfsrechnung - ein Auftrag "bis 100" liess damit eine Zeile mit 400 Bedarf komplett
                // aus dem Nachschub-Popup verschwinden, und mit dem Standard "nur Fehlendes zeigen" las sich
                // das als "nichts fehlt". Der mengengenaue Test dafuer existiert seit dem 2026-08-09, sitzt
                // aber in Dialog_SupplyLoadouts.BuildAmmoEntries - also eine Schicht ZU SPAET fuer diesen
                // Einstieg: was hier ausgesiebt wird, erreicht das Popup nie und kann dort nicht mehr
                // nachbewertet werden. Deshalb dieselbe Rechnung schon an der Quelle.
                // Bewusst dasselbe HighestBillTargetFor wie dort: RepeatCount-Auftraege ("mache 5x") zaehlen
                // nicht, weil sie keinen Lagerstand halten. Ein zu kleiner Auftrag wird beim Bestaetigen
                // angehoben statt verdoppelt - AddOrUpdateAmmoBill setzt targetCount absolut.
                // Die Mengenpruefung gilt nur fuer produzierbare Zeilen: eine blockierte Zeile soll gerade
                // NICHT wegen eines vorhandenen Auftrags verschwinden - sie ist der Hinweis selbst.
                if (!blocked && LoadoutUtility.HighestBillTargetFor(entry.ammoDef) >= target) continue;
                specificAmmo[entry.ammoDef] = target;
                // Ohne Bank gibt es keine vorzugebende Bank - Dialog_SupplyLoadouts sucht sich dann selbst
                // eine (AmmoBenchesFor) und zeigt sonst sein eigenes Warnzeichen in der Werkbank-Spalte.
                if (entry.validBenches.Count > 0) benches[entry.ammoDef] = entry.validBenches[0];
            }
            // Obsolete-bill preview, same scope swap - a live snapshot of what "Remove obsolete ammo bills"
            // would delete right now, shown in the popup itself so the player can see it before confirming.
            // Scope-BEWUSSTE Variante (2026-08-09 gefixt): UnneededAmmoEntries() kennt nur die aktuell
            // zugewiesenen Pawns. Diese Vorschau wird aber INNERHALB des Scope-Wechsels oben berechnet -
            // steht der Schalter auf "erstellte Loadouts", galt trotzdem die Zuweisungs-Sicht.
            // Fehlszenario: eine Kolonie mit angelegten, aber noch niemandem zugewiesenen Loadouts
            // meldete JEDEN Munitions-Auftrag als ueberfluessig, auch die gerade selbst erzeugten.
            // Seit die Haekchen wieder vorbelegt sind (seit 09.08.) ist das kein
            // Anzeigefehler mehr, sondern ein Klick vom Datenverlust entfernt - deshalb hier zuerst.
            List<ThingDef> obsoleteCandidates =
                LoadoutUtility.UnneededAmmoBillCandidates();
            // Siehe die Begruendung am Request unten - hier schon berechnet, weil sonst die
            // "nichts zu tun"-Abkuerzung darunter das Fenster verschluckt, wenn AUSSCHLIESSLICH ein
            // verwaister Waffenauftrag aufzuraeumen ist. Genau dieser Fall war das Problem.
            List<ThingDef> obsoleteWeapons =
                LoadoutUtility.UnneededWeaponBillCandidates();

            filterMode = saved;
            RefreshBenchesAndAmmo();

            // Nur wenn WIRKLICH nichts da ist - sonst geht das Fenster auf und sagt, was es geprueft hat.
            if (specificAmmo.Count == 0 && obsoleteCandidates.Count == 0 && obsoleteWeapons.Count == 0)
            {
                // Sound only, no toast (2026-08-02: this fires often enough during normal
                // Ammo Bills upkeep that the message text itself was just spam) - RejectInput's own sound
                // (ClickReject), same feedback the player already associates with "nothing to do here".
                Verse.Sound.SoundStarter.PlayOneShotOnCamera(SoundDefOf.ClickReject, null);
                return;
            }

            // Both Assig. indicator sources, always both regardless of scope (2026-08-02) - matches every other
            // Dialog_SupplyLoadouts.Request construction site now, see LoadoutUtility.
            // CheckAndOpenMissingProductionPopup's own comment for the full "one dict-set silently changed
            // meaning" story this fixes. This popup used to pass neither at all (Assi column always blank here).
            LoadoutUtility.ComputeAmmoNeedForAssignedPawns(LoadoutUtility.AllColonyPawns(), out _, out _, out var assignedUsedBy);
            // Ein Durchlauf, drei Rueckgaben (2026-08-31): die Vorlagen-Namen der Munition (templateUsedBy)
            // UND die der Waffen/Packs - siehe der Block ueber weaponTemplateUsedBy weiter unten.
            LoadoutUtility.ComputeMissingForCreatedLoadouts(out _, out _, out Dictionary<ThingDef, List<string>> weaponTemplateUsedBy,
                out _, out Dictionary<ThingDef, List<string>> templatePackUsedBy, out _, out _, out var templateUsedBy, out _);
            foreach (var kv in templatePackUsedBy) weaponTemplateUsedBy[kv.Key] = kv.Value;
            // Die WAFFEN-Namen zusaetzlich (2026-08-18): eine Wurfsprengstoff-Zeile steht in der
            // Munitionsliste, ihr Bedarf wird aber auf der Waffenseite gerechnet - ohne dieses Woerterbuch
            // behauptet ihre Assig.-Spalte verlaesslich "niemand", auch wenn zwei Kolonisten sie als
            // Zweitwaffe tragen. Begruendung in voller Laenge in Dialog_SupplyLoadouts, dort wo die Namen
            // zugeordnet werden.
            //
            // KORRIGIERT am selben Tag: hier stand zuerst ScanMissingAssignedWeapons, und das beantwortet
            // eine ANDERE Frage - naemlich wem die Waffe noch FEHLT. Sein usedBy wird erst gefuellt, wenn
            // Bestand UND laufende Auftraege aufgebraucht sind; wer sie laengst traegt, steht dort nie drin.
            // Im Test sofort gemessen: ueber diesen Einstieg sagte die Spalte weiter "niemand",
            // waehrend derselbe Fensterinhalt "Assigned to: Kachnovo x4, Sammy x4" auflistete. BuildWeaponUsedBy
            // ist die Frage, die hier wirklich gestellt wird.
            Dictionary<ThingDef, List<string>> weaponAssignedUsedBy = Dialog_AssignLoadouts.BuildWeaponUsedBy(
                LoadoutUtility.AllColonyPawns().Select(p => (p.GetLoadout(), p.LabelShortCap.ToString())));
            // Und die VORLAGEN-Haelfte derselben Frage (2026-08-31 - zweite Haelfte desselben Befunds:
            // "kein Loadout nutzt die Satchel Charge"). Dieser Einstieg hat
            // weaponTemplateUsedBy nie gesetzt, also stand der Kreis-Punkt einer Wurfsprengstoff-Zeile hier
            // strukturell immer auf grau - unabhaengig davon, wie viele Vorlagen das Ding enthalten. Genau
            // die Luecke, vor der der Kommentar an Request.assignedUsedBy seit dem 02.08. warnt ("jeder
            // Aufrufer fuellt BEIDE"), und die einzige der fuenf Baustellen, die es nicht tat.

            Find.WindowStack.Add(new Dialog_SupplyLoadouts(new Dialog_SupplyLoadouts.Request
            {
                specificAmmo = specificAmmo,
                ammoBenches = benches,
                assignedUsedBy = assignedUsedBy,
                templateUsedBy = templateUsedBy,
                weaponAssignedUsedBy = weaponAssignedUsedBy,
                weaponTemplateUsedBy = weaponTemplateUsedBy,
                obsoleteAmmoCandidates = obsoleteCandidates,
                // Die WAFFEN-Haelfte derselben Aufraeumliste, hier bis zum 2026-08-14 nie gesetzt (per
                // Suche nach weiteren Fundstellen gefunden): dieser Einstieg ist aelter als der gemeinsame Anschluss
                // LoadoutUtility.AttachObsoleteCleanup und hat nur seine eigene Munitions-Haelfte verdrahtet.
                // Folge: ein verwaister WAFFEN-Auftrag liess sich aus dem Nachschub-Fenster aufraeumen, wenn
                // es sich beim Reiterwechsel von selbst oeffnete, aber nicht, wenn man es hier ueber
                // Auto-generate oeffnete - dieselbe Frage, zwei Antworten, je nach Weg.
                // Nicht ueber AttachObsoleteCleanup, weil die Munitions-Haelfte hier bewusst ihre eigene
                // Meldung ("Removed N unneeded ammo bills") behaelt; der Kandidatensatz ist derselbe.
                obsoleteWeaponCandidates = obsoleteWeapons,
                onPruneObsoleteWeapons = approved => LoadoutUtility.PruneBillsFor(approved),
                onPruneObsoleteAmmo = approved =>
                {
                    int n = PruneUnneededAmmoBills(new HashSet<ThingDef>(approved));
                    if (n > 0) Messages.Message(n == 1
                        ? "CER_GenerateAmmoBills_RemovedObsoleteSingular".Translate(n.ToString())
                        : "CER_GenerateAmmoBills_RemovedObsoletePlural".Translate(n.ToString()), MessageTypeDefOf.TaskCompletion, false);
                }
            }));
        }

        // One-click: create a bill for every ammo the current list needs, ignoring the checkboxes. Die Menge
        // folgt dem echten Bedarf - der Reservekurve ueber alle Kolonisten, die diese Munition zugewiesen
        // haben - und nur wo es gar keinen Bedarf gibt, der eingetippten Chargenzahl.
        //
        // Die Bindung an den FILTER ist am 2026-08-09 entfallen, und das war der eigentliche Fehler hinter
        // "Auto-generate fordert 500, der Nachschub-Popup 150" (viermal reproduziert). Der alte Kommentar
        // hier behauptete, die anderen Filter haetten "kein Zuweisungssignal" - das stimmt nicht:
        // entry.assignedDemand wird beim Aufbau der Liste fuer JEDE Zeile berechnet, unabhaengig vom Filter.
        // Stand der Filter also auf irgendetwas anderes als "zugewiesene Loadouts", fiel die Menge stillschweigend
        // auf die Standard-Chargenzahl zurueck - bei einem 500er-Rezept eben 500 -, waehrend der Popup-Pfad
        // (OpenMissingAmmoSupplyPopup schaltet intern auf AssignedLoadouts) den Bedarf nahm.
        // Der Filter entscheidet, WELCHE Munition in der Liste steht. Wie viel davon herzustellen ist, ist
        // eine davon unabhaengige Frage - und darauf gibt es nur eine richtige Antwort, nicht eine pro Filter.
        // Mein Fix vom 2026-08-03 hatte nur das Aufrunden entfernt und diese Weiche uebersehen.
        private int AutoGenerateAll(Dictionary<ThingDef, int> queuedRecord = null)
        {
            int count = 0;
            foreach (AmmoEntry entry in ammoEntries)
            {
                if (!entry.isResearched || entry.validBenches.Count == 0) continue;
                // Der Bedarf GENAU so, wie die Reservekurve ihn liefert - kein Aufrunden auf ganze Durchgaenge
                // mehr (2026-08-09, siehe GenerateBillForEntry).
                int? target = null;
                if (entry.assignedDemand > 0)
                    target = entry.assignedDemand;
                // Zweite Fundstelle desselben Musters wie im Popup-Pfad oben (2026-08-12, per Suche
                // gefunden): auch hier stand ein blosses `activeBills.Count > 0` und liess
                // einen Auftrag "bis 100" bei 400 Bedarf als erledigt gelten. Das "never overwrite" des alten
                // Kommentars bleibt gewahrt, ohne den Fehler mitzunehmen: gedeckt heisst jetzt gedeckt der
                // MENGE nach, und weil wir bei ausreichendem Ziel abbrechen, kann der Schreibpfad darunter
                // ein handgesetztes GROESSERES Ziel gar nicht mehr erreichen - er hebt nur noch an.
                if (target.HasValue && LoadoutUtility.HighestBillTargetFor(entry.ammoDef) >= target.Value) continue;
                if (!target.HasValue && entry.activeBills.Count > 0) continue;   // ohne Bedarfszahl bleibt es beim alten, konservativen Urteil
                int queued = GenerateBillForEntry(entry, entry.validBenches[0], target);
                if (queued <= 0) continue;
                if (queuedRecord != null)
                    queuedRecord[entry.ammoDef] = (queuedRecord.TryGetValue(entry.ammoDef, out int prev) ? prev : 0) + queued;
                count++;
            }
            if (count > 0)
            {
                Messages.Message("CER_GenerateAmmoBills_AutoGeneratedBillsMessage".Translate(count.ToString()), MessageTypeDefOf.TaskCompletion, false);
                RefreshBenchesAndAmmo();
            }
            else
            {
                // Sound only, no toast - see the identical fix on this method's own manual-popup sibling above.
                Verse.Sound.SoundStarter.PlayOneShotOnCamera(SoundDefOf.ClickReject, null);
            }
            return count;
        }
    }
}
