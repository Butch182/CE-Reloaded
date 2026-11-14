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
            // Just the amount ("5", "x3", "Forever") - for the narrow bills column of the weapons view, where the
            // bench name doesn't fit next to it and goes into the tooltip instead. The ammo side keeps using its
            // label and leaves this field empty.
            public string amount;
            // "do until X" (infinity sign) instead of "do N times" (x1).
            public bool isUntil;
        }
        private class AmmoEntry
        {
            public ThingDef ammoDef;
            public RecipeDef recipeDef;
            // ABSOLUTE amount: what a click on a workbench adds to the bill. Previously this held the number of
            // batches with "= 500" next to it; now the 500 is in here directly and can be typed.
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
            // ALL running bills of this type, both kinds - the kind is shown per row as an icon.
            public List<ActiveBillInfo> activeBills = new List<ActiveBillInfo>();
            public List<Building_WorkTable> validBenches = new List<Building_WorkTable>();
            public List<ThingDefCountClass> ings = new List<ThingDefCountClass>();   // sorted recipe ingredients (icons)
        }
        private List<AmmoEntry> ammoEntries = new List<AmmoEntry>();
        // View cache for the filtered+sorted ammo list. ammoEntries is refilled in place, so its reference never
        // changes - hence a counter instead of a reference comparison.
        private int ammoEntriesVersion;
        private List<AmmoEntry> visibleCache;
        private string visibleCacheKey;
        private float visibleCacheTime = -99f;
        // The same for the weapons overview. Its data block (overviewWeapons/woInfo) has long had a 1.5s cache,
        // but the selection on top of it still ran every frame.
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
        // Name column +44 (five rows in a row all read ".30-06 Springfie...", the variant in parentheses was
        // cut off and only visible on mouseover). The number isn't a guess: the visible name field is 115
        // points wide (x 65..180, see labelRect), enough for about 18 characters - ".30-06 Springfield (AP)"
        // has 23. With 159 points about 25 fit.
        //
        // FOLLOW-UP: widening alone changed nothing, for two independent reasons - both from the recurring
        // "one question, two calculations" class of bug:
        // (1) The NAME was still truncated at 115 points. The width was stated twice: once as labelRect (now
        //     159) and once as a fixed 115 in the Truncate call next to it. Only the first was updated, so
        //     ".30-06 Springfie..." stayed exactly as before. Truncate now derives from labelRect.width - one
        //     number, one source.
        // (2) The 44 points at the end of the row were NOT spare. The layout renderer reported 971, but its
        //     model placed the yield field at AbBatchX + 45; in the code it sits after button (34) + gap (4)
        //     + input field (40) + gap (5), i.e. at AbBatchX + 83. Actual right edge before the shift: 1009 of
        //     1016 - seven points of room, not 45. With +44 it was at 1053, which is exactly what showed up
        //     ("= 5(" instead of "= 500", header cut to "Loadou"). The model in the layout renderer has been
        //     corrected too.
        // So the 44 now come from two columns that really have them to spare, and no column is cut below its
        // content:
        //   Bills column 150 -> 120: content is trash can (20) + bench icon at +25 + text at +48; the text
        //     still has 68 points for the short form plus amount ("#2 (500)").
        //   Generate column 118 -> 106: content is bench button (54) + magnifier at +58 (20), so it ends at
        //     78; the fallback texts ("No bench", "Not researched") get the full 106.
        // New right edge: 1007 of 1016. The bills column deliberately keeps more than its content needs,
        // because its header ("Active bills 'until'") is longer than any cell below it.
        // COLUMN ORDER: Name | Special | Availability | Loadouts | Ingredients | Bills "until" | Bills "once" |
        // Amount | Generate on. The amount sits directly before the generate button because it says what a
        // click adds; Generate is the last column.
        private const float AbNameX = 30f, AbNameW = 194f;
        private const float AbSpecialX = 234f, AbSpecialW = 66f;
        private const float AbAssigX = 306f, AbAssigW = 130f;
        private const float AbLoadoutX = 442f, AbLoadoutW = 50f;
        private const float AbIngX = 498f, AbIngW = 122f;
        // ONE bills column for both kinds: "active bills until" and "active bills" in one column, with an
        // infinity or x1 sign. The kind is shown as an icon in the row, no longer by the column choice.
        private const float AbActiveX = 626f, AbActiveW = 150f;
        private const float AbAmountX = 782f, AbAmountW = 56f;
        // Weapons view: the bills column starts after the bench column (842 + 78 + 6). It takes the rest of the
        // row - at the window's default width (view width 1016) that is 88 pixels.
        // WovActiveMinW is the emergency brake for small screens: if not even that fits, the column is dropped
        // entirely instead of sticking out of the visible area. The "Delete all weapon bills" button below then
        // remains the way to clear them. Narrow screens are not fully handled yet.
        // Bills column BEFORE the amount and a fixed 110 wide instead of "the rest of the row" - there are now
        // two workbench buttons on the right. The kind of bill is shown by the icon in the row.
        private const float WovActiveX = 658f, WovActiveW = 110f;
        // AbGenW is new: this column's width used to appear three times as a raw number (110 in the header,
        // 116 twice at the fallback texts), and none of the three knew about the others - when shifting columns
        // exactly that would have drifted apart again.
        // Two workbench buttons per bench: x1 (single bill) first, then infinity (do until) - the same order as
        // in the weapons view: the single bill comes first.
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
            // AMMO WITH A BILL ONLY WHERE THE FILTER MEANS IT: under the "Assigned loadouts" filter, types
            // assigned to nobody showed up too. Cause: this block used to run ALWAYS and pulled in every type
            // with a bill running anywhere - so 'Assigned loadouts' showed rows with 'Loadouts: None'. A filter
            // that shows something other than what it says is no filter.
            // Nothing is lost by this: running bills have their own 'Active bills' filter, and unneeded ones are
            // found by 'Optimize ammo bills' or 'Remove unneeded bills'.
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

        // EVERY click in this window recomputes EVERYTHING: the bill and ammo overviews used to update
        // sluggishly - no matter which button is pressed, all elements must recompute immediately.
        // Deliberately NOT per button: this window has dozens of buttons (trash can per bill, workbench
        // buttons, checkboxes, filters, sort headers, footer), and it has to hold for any of them. Hence one
        // place: if this frame's event CONSUMED a mouse event (Event.current.type == Used), i.e. some control
        // fired - then every cache of this window is dropped. Keyboard input is deliberately excluded,
        // otherwise every keystroke in the amount field would recompute the whole list.
        private void RefreshAllAfterClick()
        {
            LoadoutUtility.InvalidateAmmoOwnership();
            CombatReadiness.Invalidate();
            RefreshBenchesAndAmmo();
            visibleCache = null;
            overviewWeapons = null;   // the weapons view depends on its own cache
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
            // Counter for the view cache below: the list is cleared and refilled in place, its reference NEVER
            // changes - a cache relying on the reference would not notice the new entries.
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
                            
                            // Default = batches x yield, i.e. the number that used to show on the right as "= 500".
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
                // The same reserve curve as everywhere else: this used to sum flat (colonists x rounds), while
                // EVERY other path to a bill target goes through LoadoutUtility.ComputeAmmoNeedForLoadouts ->
                // WeightedAmmoDemand. By design that counts only the first few colonists fully and then tapers
                // off, because a "do until X" bill only counts STOCKPILED ammo, not ammo being carried.
                // The result was: 6 colonists x 200 rounds gave ~660 via the supply popup, but 1200 via
                // Auto-generate in this tab (and thus via Arm my Colony). Two buttons, results differing by a
                // factor of two, without the UI explaining it.
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
                        if (!until && bp.repeatMode != BillRepeatModeDefOf.RepeatCount) continue;   // 'Forever' has no amount
                        entry.activeBills.Add(new ActiveBillInfo {
                            bench = bench, bill = bp, isUntil = until,
                            // Amount always in UNITS: a single bill over 1 run with yield 500 makes 500 rounds -
                            // 'x1' would be the number of runs and reads like a single unit.
                            amount = (until ? bp.targetCount : bp.repeatCount * Mathf.Max(1, bp.recipe.products[0].count)).ToString(),
                            label = benchLabel + " (" + (until ? bp.targetCount : bp.repeatCount * Mathf.Max(1, bp.recipe.products[0].count)) + ")"
                        });
                    }
                }
                // "Until" bills first (the "until" bill is always on top when a row has several) - they are the
                // normal case, single bills go below. Stable: within one kind the bench order is preserved.
                entry.activeBills = entry.activeBills.OrderByDescending(b => b.isUntil).ToList();
            }

                // ONE source for "unneeded": exactly the list the supply window shows under "Obsolete bills".
                // Previously this tab answered the question itself - via `entry.activeBills`, which deliberately
                // collects ONLY "do until X" bills (exactly right for the target amount calculation). A "do 5x"
                // bill therefore counted nowhere: the button showed (0) while the window next to it listed the
                // same bill as unneeded. Two numbers for the same thing, and the smaller one was wrong.
                // Computed once per refresh, not per frame - the function runs over all benches and all
                // colonists, and the button is drawn every frame.
            unneededAmmoDefs = new HashSet<ThingDef>(LoadoutUtility.UnneededAmmoBillCandidates());

            ammoEntries.SortBy(e => e.ammoDef.label);
        }

        // See RefreshAmmoList: the shared answer to "which ammo is being produced although nobody needs it
        // anymore". Identical to the "Obsolete bills" section in the supply window.
        private HashSet<ThingDef> unneededAmmoDefs = new HashSet<ThingDef>();

        public bool weaponsMode;
        private readonly HashSet<ThingDef> selectedWeapons = new HashSet<ThingDef>();
        // ONE source of truth for the ammo checkboxes, like selectedWeapons next to it (the master toggle in the
        // ammo view never checked anything ON, only off). Previously the checkbox lived as a bool on the
        // AmmoEntry objects themselves, and their lifetime depends on rebuilding ammoEntries/visibleCache - a
        // write to one instance was no longer what got drawn in the next frame. A def set has no object
        // identity, so this class of bug is structurally gone (the same pattern that demonstrably works in the
        // weapons view).
        private readonly HashSet<ThingDef> selectedAmmoDefs = new HashSet<ThingDef>();
        // Amount and material per weapon row. Only what the player actually touched is stored here -
        // everything else falls back to 1 unit and the default material, so the one-click case works without
        // any preparation.
        private readonly Dictionary<ThingDef, string> wovCount = new Dictionary<ThingDef, string>();
        private readonly Dictionary<ThingDef, ThingDef> wovStuff = new Dictionary<ThingDef, ThingDef>();

        // A weapon's default material - steel if allowed, otherwise the first permitted one. The same choice
        // Dialog_SupplyLoadouts.BuildWeaponEntry makes, so both paths suggest the same thing.
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
            // This weapon's workbenches, already filtered by "Hide duplicates" (with several workbenches there
            // are several entries per row).
            // Determined BEFORE drawing, as on the ammo side - the row height depends on it, and a height that
            // is only known while drawing can't be summed up front by the scroll view.
            public List<Building_WorkTable> benches = new List<Building_WorkTable>();
            // This weapon's running bills (a trash can per active bill). Determined BEFORE drawing like the
            // benches next to it - the row height depends on it, and the scroll view needs the total height
            // before the first row.
            public List<ActiveBillInfo> activeBills = new List<ActiveBillInfo>(); }
        private Dictionary<ThingDef, WOInfo> woInfo;
        private enum WovSort { Score, Name, Owned }
        private WovSort wovSort = WovSort.Score;
        private bool wovSortDesc = true;
        // AllWeapons has been removed - the value is gone here too, so no dead branch is left behind. The
        // filter is never saved, so no old settings state can point to it.
        private enum WovFilter { AssignedLoadouts, AvailableWeapons, ResearchedWeapons, ActiveBills }
        private WovFilter wovFilter = WovFilter.AvailableWeapons;

        // The assignment state the list was built for. The list used to be built only on entering the tab and
        // after clicks IN the window - an arm run or a "Confirm & supply" from a child window changes the
        // assignments while this overview is open behind it, and it only noticed on its own next click.
        // That once left it completely empty under the "Assigned loadouts" filter: built at the moment the run
        // had reset everything and not yet assigned anything new.
        // CombatReadiness.MutationVersion is incremented at every place that writes assignments (Apply,
        // single assignment, None, supply window) - exactly the signal that was missing here.
        private int builtForMutation = -1;

        public void DoWindowContents(Rect inRect)
        {
            if (builtForMutation != CombatReadiness.MutationVersion)
            {
                builtForMutation = CombatReadiness.MutationVersion;
                RefreshBenchesAndAmmo();
                visibleCache = null;
            }
            // Remember the state BEFORE drawing, check afterwards - see RefreshAllAfterClick.
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
                        // The header row still sets BATCHES - per row that becomes the unit count (batches x yield).
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
            // Fitting is done in LoadoutUtility.FitRadioRow - one calculation for this row AND the matching one
            // in the weapons tab, with a visible minimum gap and a second line as the last resort. The
            // rationale is at the helper.
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
            // Tooltips on EVERY filter: none of the five radio buttons had one - noticeable because almost
            // everything else in this mod has one. Two of the five in the weapons tab already had one - so the
            // row was half explained, which is worse than not at all: people stop looking.
            // Three of the texts are shared by both views because they mean the same thing (Filter...Tip); the
            // two ammo-specific ones have their own.
            void AmmoRadio(string label, AmmoBillFilterMode m, string tip)
            {
                if (LoadoutUtility.Radio(ref afx, y, label, filterMode == m, tip) && filterMode != m)
                {
                    filterMode = m;
                    RefreshBenchesAndAmmo();
                }
            }
            AmmoRadio(ammoRadioLabels[0], AmmoBillFilterMode.AssignedLoadouts, "CER_GenerateAmmoBills_FilterAssignedTip".Translate());
            // "Created loadouts" has been removed - since the lazy rework it is the same set as "Assigned
            // loadouts". The enum value AvailableLoadouts only remains so old settings files load;
            // CEReloadedSettings maps it to Assigned on load.
            AmmoRadio(ammoRadioLabels[1], AmmoBillFilterMode.AvailableWeapons, "CER_GenerateAmmoBills_AbAvailableWeaponsTip".Translate());
            AmmoRadio(ammoRadioLabels[2], AmmoBillFilterMode.ActiveBills, "CER_GenerateAmmoBills_FilterActiveBillsTip".Translate());
            AmmoRadio(ammoRadioLabels[3], AmmoBillFilterMode.AllAmmunition, "CER_GenerateAmmoBills_AbAllAmmunitionTip".Translate());

            if (CEReloadedMod.settings != null)
            {
                // In the SAME font as the flow - the rect was measured with it.
                bool hideDup = CEReloadedMod.settings.hideDuplicateBenches;
                Widgets.CheckboxLabeled(ammoFit.checkboxRect, dupLabel, ref hideDup);
                // The checkbox sat in the same row and was the only one without a tooltip.
                TooltipHandler.TipRegion(ammoFit.checkboxRect, "CER_GenerateAmmoBills_HideDuplicateBenchesTip".Translate());
                if (hideDup != CEReloadedMod.settings.hideDuplicateBenches)
                {
                    CEReloadedMod.settings.hideDuplicateBenches = hideDup;
                    RefreshBenchesAndAmmo();
                }
            }
            Text.Font = prevRowFont;
            y += 30f + ammoFit.extraHeight;

            // "Remove obsolete ammo bills" lives in the "Auto-generate" popup itself (Dialog_SupplyLoadouts, via
            // OpenMissingAmmoSupplyPopup), no longer in a row here. The "Assigned/Created loadouts" scope toggle
            // that sat next to it has been removed entirely.

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
            // ALL AT ONCE: ammo and weapon bills get a button at the top left that toggles ALL of them - except
            // the locked ones (e.g. dark-red cross because not researched).
            // The toggle sits above the checkbox column and toggles exactly the rows that could also be checked
            // individually - locked ones (not researched / no workbench) are left out.
            // Rule as everywhere in the mod: if any checkbox is still unchecked, the toggle turns ALL on; if all
            // are on, it turns all off.
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
            // Filtering and sorting used to run every FRAME. With CE's full ammo roster that's 50-150 entries,
            // shuffled through three times (Where -> ToList -> OrderBy -> ToList), and when sorting by
            // availability the key additionally calls GetAmmoOwnership per entry. Drawing itself has long had
            // scroll culling (see below) - BUILDING the list was the remaining part that always ran for all.
            // The key contains everything that can change selection or order, plus a maximum age of half a
            // second for whatever depends on live stock (sorting by availability/bills). Same design as the
            // mod's other caches, just shorter: the stock counters below have 1s themselves.
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
            // An empty table now SAYS why it is empty (the table was completely empty and nothing said the
            // search had found nothing - if you forgot the search, the display doesn't tell you whether the
            // filter is to blame). Two different reasons, two different sentences - the same distinction the
            // three lists in the role tab make.
            if (visibleCache.Count == 0)
            {
                GUI.color = new Color(1f, 1f, 1f, 0.55f);
                Widgets.Label(new Rect(6f, 6f, viewRect.width - 12f, 60f),
                    string.IsNullOrEmpty(nameFilter)
                        // Insert the filter name instead of writing it into the sentence (in German the empty
                        // message named "Res. ammo" and "Res. weapons", while the radios right above it carried
                        // the translated labels). A sentence that quotes a button must ASK the button - otherwise
                        // the two drift apart with every translation. And it now names only the filter of THIS
                        // view: the other one wouldn't help here anyway.
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
                // From the same shared list as the button count and the supply window (see unneededAmmoDefs).
                bool unneeded = unneededAmmoDefs.Contains(entry.ammoDef);
                if (unneeded)
                {
                    Widgets.DrawBoxSolid(new Rect(0f, listY, viewRect.width, rowHeight), new Color(0.55f, 0.4f, 0.15f, 0.12f));
                    TooltipHandler.TipRegion(new Rect(0f, listY, viewRect.width, rowHeight),
                        "CER_GenerateAmmoBills_UnneededBillTooltip".Translate());
                }

                // Checkbox - reads and writes selectedAmmoDefs, never a field on the entry object anymore
                // (see the rationale at selectedAmmoDefs itself).
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
                // 115 -> 159: derived from AbNameW instead of written down twice (AbNameX + AbNameW - 65).
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
                // TipWanted: the location tooltip iterates all maps - only build it when it is actually shown.
                LoadoutUtility.DrawAvailabilityBar(assignedCell, abSeg, horizontal: true,
                    LoadoutUtility.TipWanted(assignedCell) ? LoadoutUtility.BuildAmmoLocationTooltip(entry.ammoDef) : null);

                // Ingredients column (sorted icons, "!" if a resource isn't in storage, whole-cell tooltip).
                // mult is this row's own Batches field - without it the shortage check only validated
                // stock against ONE batch, silently missing shortfalls on any row with Batches > 1.
                GUI.color = entry.isResearched ? Color.white : Color.grey;
                // The ingredients column counts in BATCHES, the field is now in units - so convert back.
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
                            // The single bill produces the ENTERED AMOUNT - for ammo, that means as many runs as
                            // are needed for this unit count.
                            int runs = Mathf.Max(1, Mathf.CeilToInt(abAmount2 / (float)Mathf.Max(1, entry.yield)));
                            // A single click by the player - counts as set by hand (see AddOrUpdateAmmoBill).
                            LoadoutUtility.AddOrRaiseOnceBill(bench, entry.recipeDef, runs, null, markManaged: false);
                            LoadoutUtility.RefreshBillsTab(bench);
                            RefreshBenchesAndAmmo();
                            break;
                        }
                        if (pressed == 2)
                        {
                            GenerateBillForEntry(entry, bench, additive: true, markManaged: false);   // single click = set by hand
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
                    // White instead of green: green means "covered / all good" everywhere in this mod. Here the
                    // dot only counts HOW MANY templates use this ammo - a number, not a verdict. On a pure count
                    // column, green implied a judgement nobody is making there.
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
                    //
                    // A two-step variant (+1/+5) with a magazine display on the total was tried briefly and
                    // removed again: production runs are batches rather than magazines. The numbers: a CE batch
                    // is 200-500 rounds, a magazine 10-60 - a magazine step is finer than what the recipe can
                    // produce at all.
                    // That's why TexMagazine and MagazineSizeForAmmo are gone again too - code nobody calls reads
                    // like code that's needed next time around. The placeholder icon
                    // Textures/UI/Icon_Magazine.png stays: an image file claims nothing.
                    // ONE field instead of button + batches: it holds the unit count that a click on a workbench
                    // to the right adds to the bill.
                    Rect amountRect = new Rect(AbAmountX, centerY, AbAmountW, 24f);
                    entry.amountString = Widgets.TextField(amountRect, entry.amountString);
                    TooltipHandler.TipRegion(amountRect, "CER_GenerateAmmoBills_AmountTooltip".Translate(entry.yield.ToString()));
                }

                listY += rowHeight;
            }
            Widgets.EndScrollView();
            GUI.color = Color.white;

            // --- Bottom bar: destructive action left, actions flowing in from the right (the window's X closes). ---
            // Width measured instead of a fixed 180f (in German the label ran out of the rect on both sides).
            // The neighbouring button hangs off delAmmoRect.xMax and moves along on its own.
            // First ALL four labels and widths, then ONE font size for the whole row, then draw (in German the
            // two left buttons were drawn large and the two right ones small). The two left ones were drawn
            // BEFORE BottomBarFont even ran and never took part in its calculation - each measured on its own.
            // Exactly the case the helper is meant to rule out; it only knew half the row.
            string delAmmoLabel = "CER_GenerateAmmoBills_DeleteAllBillsButton".Translate();
            float delAmmoW = LoadoutUtility.BottomBtnW(delAmmoLabel, 180f, 260f);
            Rect delAmmoRect = new Rect(0f, inRect.height - LoadoutUtility.BottomBarH, delAmmoW, LoadoutUtility.BottomBtnH);

            int unneededCount = unneededAmmoDefs.Count;
            // Same measurement as the neighbour - the same overflow class, just not noticed yet.
            string pruneLabel = "CER_GenerateAmmoBills_RemoveUnneededButton".Translate(unneededCount.ToString());
            float pruneW = LoadoutUtility.BottomBtnW(pruneLabel, 220f, 300f);
            Rect pruneRect = new Rect(delAmmoRect.xMax + 8f, inRect.height - LoadoutUtility.BottomBarH, pruneW, LoadoutUtility.BottomBtnH);

            // Over the VISIBLE list (the same class of issue as in the weapons view): GenerateBills only creates
            // what the filter shows - the number in the button must be the same.
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
                        // Delete with the selection the button also COUNTED - the plain call without an argument
                        // deliberately cleans up more narrowly (see there), and that would be the next number
                        // that doesn't match the action: "(4)" on the button and "2 bills removed" in the
                        // message afterwards.
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
            // Order and labels as in the weapons view: "Produce selected (N)" on the right, the wizard to its
            // left. Previously the wizard was on the right and the other button had a different name - two
            // views, two vocabularies for the same thing.
            float xr = inRect.width;
            if (LoadoutUtility.BottomActionButton(inRect, ref xr, LoadoutUtility.PrimaryBtnW, ammoSelLabel))
            {
                if (selAmmoCount == 0)
                {
                    Messages.Message("CER_GenerateAmmoBills_NoAmmoSelected".Translate(), MessageTypeDefOf.RejectInput, false);
                    Text.Font = prevAmmoBarFont;   // Text.Font is static - an early exit here would otherwise leak into the next window
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

            // The same fitting as the filter row of the ammo tab, via the same helper instead of a second copy
            // of the formula - exactly that duplication caused the row to fit here while the other one was
            // cramped (see LoadoutUtility.FitRadioRow).
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
            // Order as in the ammo tab ("active bills" comes after "available weapons", as for ammo bills),
            // and "All weapons" is gone - it was the only filter that filtered nothing at all, and its slot now
            // holds the duplicate-benches toggle.
            WovRadio(wovRadioLabels[2], WovFilter.ActiveBills, "CER_GenerateAmmoBills_FilterActiveBillsTip".Translate());
            WovRadio(wovRadioLabels[3], WovFilter.ResearchedWeapons, "CER_GenerateAmmoBills_WovResearchedWeaponsTooltip".Translate());

            // The same toggle as in the ammo tab, and it belongs here: a weapon row's bench entries depend on
            // it. Without it the setting could only be changed in the mod settings - for something you see right
            // in front of you.
            if (CEReloadedMod.settings != null)
            {
                bool wovHideDup = CEReloadedMod.settings.hideDuplicateBenches;
                Widgets.CheckboxLabeled(wovFit.checkboxRect, wovDupLabel, ref wovHideDup);
                // Twin of the checkbox in the ammo tab - the same tooltip.
                TooltipHandler.TipRegion(wovFit.checkboxRect, "CER_GenerateAmmoBills_HideDuplicateBenchesTip".Translate());
                if (wovHideDup != CEReloadedMod.settings.hideDuplicateBenches)
                {
                    CEReloadedMod.settings.hideDuplicateBenches = wovHideDup;
                    CEReloadedMod.settings.Write();
                    overviewWeapons = null;   // rebuild the bench list per weapon - it depends on this toggle
                }
            }
            Text.Font = prevWovFont;

            // + extraHeight: if the checkbox wraps onto its own line, the divider must move with it - everything
            // below hangs off hy, so this one place is enough.
            float hy = LoadoutUtility.Divider(64f + wovFit.extraHeight, inRect.width) + 2f;
            void SortHdr(float x, float wdt, string label, WovSort mode)
            {
                if (LoadoutUtility.ColumnHeader(new Rect(x, hy, wdt, 22f), label, wovSort == mode, wovSortDesc))
                {
                    if (wovSort == mode) wovSortDesc = !wovSortDesc;
                    else { wovSort = mode; wovSortDesc = mode != WovSort.Name; }
                }
            }
            // The same toggle as in the ammo view - here above the checkbox column at x=6.
            {
                // Over the VISIBLE list, not overviewWeapons: with the "Available weapons" filter, 12 checkable
                // rows were on screen, the toggle checked all 48 of the full list, and the button said "Produce
                // selected (48)" - a number four times what the player could see. The ammo view next to it has
                // always computed over BuildVisibleAmmoEntries; this was the second, drifted copy of the same
                // toggle.
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
            // Availability BEFORE ingredients, then the bills BEFORE the amount - the columns are narrower for
            // it, so there's room on the right for two workbench buttons per bench.
            SortHdr(430f, 130f, "CER_GenerateAmmoBills_WovColOwned".Translate(), WovSort.Owned);
            LoadoutUtility.ColumnHeader(new Rect(570f, hy, 80f, 22f), "CER_GenerateAmmoBills_WovColIngredients".Translate(), false, false, false);
            LoadoutUtility.ColumnHeader(new Rect(WovActiveX, hy, WovActiveW, 22f), "CER_GenerateAmmoBills_WovColActiveBills".Translate(), false, false, false);
            // Amount and material - the 100px between stock and action used to be empty, so the two new cells
            // fit in without any column having to give way.
            // Material BEFORE amount (columns swapped).
            // The amount header may be wider than its field - otherwise "Amount" drops to the small font.
            LoadoutUtility.ColumnHeader(new Rect(776f, hy, 30f, 22f), "CER_GenerateAmmoBills_WovColMaterial".Translate(), false, false, false);
            LoadoutUtility.ColumnHeader(new Rect(812f, hy, 60f, 22f), "CER_GenerateAmmoBills_ColAmount".Translate(), false, false, false);
            // "Generate on..." above the action column - the SAME key as in ammo mode, no second translation.
            // The column had no header at all before; with several bench entries below it, it needs one.
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
                    // Collect all bills instead of stopping at the first: the trash can per bill needs the bills
                    // themselves, not just the fact that there are some.
                    // Deliberately WITHOUT the ammo side's repeatMode filter: that one only takes "do until X"
                    // because it computes target amounts with them. Nothing is computed here, things are deleted
                    // - and a "do 5x" bill (exactly what the button on the left creates) would otherwise be
                    // invisible and impossible to click away.
                    List<ActiveBillInfo> wovBills = new List<ActiveBillInfo>();
                    foreach (Building_WorkTable bench in LoadoutUtility.AllColonyBenches())
                    {
                        if (bench.billStack == null) continue;
                        foreach (Bill_Production bp in bench.billStack.Bills.OfType<Bill_Production>())
                        {
                            if (bp.recipe == null || bp.recipe.products == null) continue;
                            if (!bp.recipe.products.Any(pr => pr.thingDef == d)) continue;
                            // Amount in UNITS and the kind as a flag - the same calculation as on the ammo side.
                            bool wUntil = LoadoutUtility.IsUntilBill(bp);
                            int wYield = bp.recipe.products.Count > 0 ? Mathf.Max(1, bp.recipe.products[0].count) : 1;
                            string amount = wUntil ? bp.targetCount.ToString()
                                : bp.repeatMode == BillRepeatModeDefOf.RepeatCount ? (bp.repeatCount * wYield).ToString()
                                : bp.repeatMode.LabelCap.ToString();
                            wovBills.Add(new ActiveBillInfo { bench = bench, bill = bp, amount = amount, isUntil = wUntil,
                                label = GetBenchLabel(bench) + " (" + amount + ")" });
                        }
                    }
                    // "Until" bills first - the same rule as on the ammo side.
                    wovBills = wovBills.OrderByDescending(b => b.isUntil).ToList();
                    activeBill = wovBills.Count > 0;
                    WOInfo wi = new WOInfo { recipe = r, researched = res, hasBench = res && LoadoutUtility.WeaponBenchExists(d), inLoadout = inLo, inAssignedLoadout = inAssigned, hasActiveBill = activeBill, ings = LoadoutUtility.GetRecipeIngredients(r), score = sc, special = LoadoutUtility.DetectSpecialAbilities(d), activeBills = wovBills };
                    // The same duplicates rule as in ammo mode: with "Hide duplicates" only the first bench of
                    // each bench TYPE counts. A colony with six fabrication benches would otherwise get six
                    // identical rows.
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

            // Recompute selection and sorting only when something actually changed. overviewCacheTime is part
            // of the key: when the 1.5s data cache above expires, this one is dropped automatically. A click on
            // filter, column, direction or a character in the search field changes the key and takes effect
            // immediately - so the list never lags behind. Important for the "Available weapons" filter and the
            // "Owned" sort: both called OwnedTotal (full realm scan behind a 1s cache) per weapon per frame,
            // over ALL weapon defs in the game.
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
            // Row height grows with the number of workbenches - just like in ammo mode (there rowHeight =
            // Max(40, Max(activeBills, benches) * 24 + 8)). Sum first, then draw: the scroll view needs the
            // total height before the first row is created.
            float RowHeightOf(ThingDef wd)
            {
                WOInfo wi = woInfo.TryGetValue(wd, out WOInfo x) ? x : null;
                int benchRows = wi != null && wi.researched ? wi.benches.Count : 0;
                // The bills column can also be longer than the bench column (three bills on one bench are three
                // rows, but only one bench) - just like in ammo mode, there Max(activeBills, benches).
                int billRows = wi != null ? wi.activeBills.Count : 0;
                return Mathf.Max(34f, Mathf.Max(benchRows, billRows) * 24f + 10f);
            }
            float totalH = 4f;
            foreach (ThingDef wd in wovVisCache) totalH += RowHeightOf(wd);
            Rect viewRect = new Rect(0, 0, outRect.width - 16f, totalH);
            Widgets.BeginScrollView(outRect, ref weaponsScroll, viewRect);
            // Twin of the ammo view - see there.
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

                // Cap at 84 instead of 90: the ingredients column starts at 450, the strip was allowed to grow to
                // 452 and stuck two pixels into it - found by the layout renderer, not by eye. The same three
                // icons plus "+N" still fit (19px per icon, 20px reserve).
                // The WEAPON drawer, not the ammo drawer (Molotov and Stick Bomb carried an ammo badge - again
                // two implementations of the same thing): DrawAmmoEffectIcons stamps the small ammo badge on
                // EVERY icon - right for an ammo row, wrong for a weapon. The supply window and tab 2 have always
                // drawn it via DrawWeaponSpecialIcons; this view was the exception. Not anymore.
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
                // Same uniform pass as the ammo row's own bar just above - every Availability bar in the mod gets
                // the where-is-it breakdown now.
                // TipWanted: the location tooltip iterates all maps - only build it when it is actually shown.
                Rect wAvailRect = new Rect(430f, ry + 7f, 130f, 18f);
                LoadoutUtility.DrawAvailabilityBar(wAvailRect, wSeg, horizontal: true,
                    LoadoutUtility.TipWanted(wAvailRect) ? LoadoutUtility.BuildWeaponLocationTooltip(w) : null);

                Rect actRect = new Rect(876f, ry + 4f, 136f, 24f);
                if (producible)
                {
                    // ONE CLICK (modelled on the ammo bills: one click is enough). Previously this button only
                    // opened the supply window, and production only happened there - two windows for one bill,
                    // while the ammo side next to it does it directly in the row.
                    // The amount is to the left (default 1), the material to the right of it as an icon button,
                    // both optional: without any preparation a click produces exactly one unit from the default
                    // material. The mod picks the workbench as everywhere else - the first matching one.
                    string cntStr = wovCount.TryGetValue(w, out string cs) ? cs : "1";
                    Rect cntRect = new Rect(812f, ry + 4f, 44f, 24f);
                    string newCnt = Widgets.TextField(cntRect, cntStr);
                    if (newCnt != cntStr) wovCount[w] = newCnt;
                    if (LoadoutUtility.TipWanted(cntRect)) TooltipHandler.TipRegion(cntRect, "CER_GenerateAmmoBills_WovCountTip".Translate());

                    ThingDef stuff = wovStuff.TryGetValue(w, out ThingDef st) ? st : DefaultStuffFor(w);
                    if (w.MadeFromStuff)
                    {
                        Rect matBtn = new Rect(776f, ry + 5f, 26f, 22f);
                        // The same cell as in the Equipment Packs tab - see LoadoutUtility.StuffButton.
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

                    // ONE ENTRY PER WORKBENCH instead of a single "Produce": a workbench icon as for the ammo
                    // bills, "Generate on" above it, and with several workbenches several entries per row.
                    // Literally the same layout as the ammo side: bench icon button with a number for duplicates,
                    // the magnifier next to it to jump there, one row per bench.
                    // The previous button silently took the FIRST matching bench - in a colony with several
                    // benches it wasn't controllable where production happens, and not even visible where it did.
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
                // Running bills with a trash can per bill - the same layout as in ammo mode. Deliberately
                // OUTSIDE the producible branch: a bill can be running while the weapon currently can't be
                // produced (bench torn down, research removed by a mod) - exactly then you want to be able to
                // delete it.
                float wovActiveW = WovActiveW;
                if (info.activeBills.Count > 0)
                {
                    float billY = ry + 4f;
                    foreach (ActiveBillInfo abi in info.activeBills.ToList())
                    {
                        if (DrawActiveBillRow(new Rect(WovActiveX, billY, wovActiveW, 22f), abi))
                        {
                            overviewWeapons = null;   // bench list and bill list share the same cache
                            break;
                        }
                        billY += 24f;
                    }
                    GUI.color = Color.white;
                }

                if (!producible)
                {
                    GUI.color = Color.gray;
                    // Narrower ONLY when there really are bills to the right - otherwise the text keeps its old
                    // width. A non-producible recipe with a running bill is the rare case (bench torn down), and
                    // there the bill is the more important information.
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
            // The same row calculation as in the ammo view: the left button wasn't registered here at all, and
            // on the right no BottomBarFont ran at all - two buttons, two measurements, so in German the same
            // size mismatch, just not noticed yet.
            string delWepLabel = "CER_GenerateAmmoBills_DeleteAllWeaponBillsButton".Translate().ToString();
            float delWepW = LoadoutUtility.BottomBtnW(delWepLabel, 190f, 300f);
            Rect delWepRect = new Rect(0f, inRect.height - LoadoutUtility.BottomBarH, delWepW, LoadoutUtility.BottomBtnH);

            // The number in the button states what the click REALLY creates (see the header toggle above): only
            // what the filter currently shows is produced anyway - if this showed the number of all weapons ever
            // checked, the button promised more than it did.
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


        // Extracted so the view cache in DoAmmoListContents has exactly ONE place where it is built. Content
        // unchanged from the earlier inline version - reads only fields, no drawing locals.
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


        // Extracted - see BuildVisibleAmmoEntries, same design, same reason. Reads only fields.
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
                        default:                         pass = true; break;                                 // can't happen, only catches new values
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
        // targetOverride is the EXACT stock level set as "do until X" - unrounded: the exact value the curve
        // delivers is set as the "until" amount. Previously there was only batchesOverride, and batches * yield
        // was computed from it again: with CE's several-hundred-round recipes, a demand of 60 rounds became a
        // standing "keep 500 in stock", and because of Max(1, ...) even the smallest demand got a full run.
        // Supply Loadouts had already done away with exactly that; this path followed suit.
        // The batches path deliberately stays multiplicative: there the player typed a number of RUNS into the
        // field, so "3" really means three times the recipe yield.
        // ONE bill row for BOTH views: the ammo and the weapons view each had their own copy of the same cell,
        // which is why the ammo side lacked the kind icon the weapon side already had.
        // Layout: trash can | workbench icon with the bill kind as a badge at the TOP RIGHT (the same spot as on
        // the generate buttons) | amount in UNITS. Returns true if the bill was deleted.
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

        // ONE button pair for both views: x1 (single bill) on the left, infinity (do until) on the right, the
        // magnifier after them. The badge sits at the top right of each button. Returns: 0 nothing, 1 x1,
        // 2 infinity. Both buttons work with the SAME amount from the column before - the x1 is a symbol for
        // "once", not a unit count (x1 adds the entered amount, not just 1).
        private int DrawBenchBillButtons(float x, float y, Building_WorkTable bench, string benchName, int amount, int onceYield = 1)
        {
            int result = 0;
            string id = GetBenchId(bench);
            Rect onceRect = new Rect(x, y, 54f, 22f);
            // The x1 tooltip states the unit count that is REALLY produced (field 200, bill 500): a single bill
            // counts RUNS, so ammo is only produced in whole batches - rounded up, the way GenerateBillForEntry
            // computes it (the same CeilToInt formula). For the infinity button the field number stays correct;
            // an "until X" target knows no batches.
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

            // `additive` is the button in the "Generate on" column: it adds the amount ON TOP of an existing "do
            // until" bill. The bulk paths (Auto-generate, Arm my Colony) still set the target amount absolutely -
            // they compute the demand instead of adding it up.
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
            // Only what is ACTUALLY in the table right now: the bulk action used to run over ammoEntries, i.e.
            // also over rows the search or bench filter currently hides. You check something, then type into the
            // search field, and still get a bill for the vanished row. The weapons overview has long warned
            // about exactly this case; it was missing here.
            // BuildVisibleAmmoEntries instead of a second, hand-rebuilt filter condition: the copy here didn't
            // know the type filter that was added later - "show only incendiary ammo" and then Generate still
            // created bills for hidden types. One question, one calculation.
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
                    // Respect the bench filter in the bulk action too - the per-row "Generate on..." choice has
                    // long done so; here validBenches[0] was taken blindly, possibly scheduling on a bench the
                    // table doesn't even show.
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
        // The previously required pawnsWereUnassigned parameter has been removed: it only existed to suppress
        // the prune call at the end of this method in the resetPawns case, and that call is gone entirely - see
        // below.
        public void ArmAutoGenerateMissing(ArmReport rep)
        {
            filterMode = AmmoBillFilterMode.AssignedLoadouts;
            RefreshBenchesAndAmmo();
            int made = AutoGenerateAll(rep != null ? rep.ammoQueued : null);
            if (rep != null) rep.ammoBillsCreated += made;

            // NOTHING IS DELETED HERE ANYMORE. Principle: nothing is deleted without asking - deletion only
            // happens via the supply window.
            // This step used to call PruneUnneededAmmoBills() and was therefore the last place in the mod where
            // bills disappeared without player action - no popup, no checkboxes, in ALL three modes. Of all
            // things, Cautious, whose summary promises "nothing is deleted", deleted too.
            //
            // A partial fix already existed (the pawnsWereUnassigned guard): after a resetPawns nobody is
            // assigned anymore, so "does anyone still need this?" is no for EVERY ammo, and the scythe took
            // hand-set bills with it. But that was only the worst single case of the same rule violation, not
            // the rule.
            //
            // Nothing is lost by this: the supply popup shows unneeded bills in its own section with checkboxes
            // to deselect, and it is wired up twice in THIS flow - Dialog_AssignLoadouts.OpenProducePopup (on
            // every Apply) and LoadoutUtility.CheckAndOpenMissingProductionPopup (end of the run), both via
            // LoadoutUtility.AttachObsoleteCleanup. Plus the dedicated button in the ammo tab. So the cleanup
            // still exists - just where the player sees it and checks it off.
        }

        // Bills whose ammo nobody currently assigned demands anymore - production that kept running after a
        // loadout was edited/deleted or a colonist reassigned, quietly consuming materials for nothing. Distinct
        // from an entry with active bills AND real demand (left alone) or no bills at all (nothing to prune).
        // genericallyCovered gates this too, so the row highlight, the bottom-bar count and the actual prune all
        // agree - and so a caliber somebody asks for generically never gets its siblings deleted (2026-08-09).
        // !IsSelfAmmoWeapon: SECOND occurrence of the same bug (the first was in
        // LoadoutUtility.UnneededAmmoBillCandidates, see the detailed block there). Thrown explosives and
        // thrown weapons - satchel charge, grenades, spears - are ThingClass AmmoThing and therefore show up as
        // an ammo row in this tab. But `assignedDemand` comes from the ammo demand calculation, which
        // explicitly excludes exactly these items (they belong to the weapon side, which offsets real
        // ownership). For them `assignedDemand == 0` is therefore NOT a statement about demand, just the
        // consequence of nobody here being responsible - and "0" used to mean "delete".
        // Failure scenario: a colonist carries satchel charges as a sidearm, the bill for them is running -
        // and this delete path would have cleared it as unneeded while the button above showed "(0)". Because
        // the DISPLAY (unneededAmmoDefs, see RefreshAmmoList) already goes through UnneededAmmoBillCandidates
        // and knew the exclusion; only this path didn't. Two numbers for the same question, and the wrong one
        // did the deleting.
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
            // An EXPLICITLY checked row must actually disappear (the supply window listed three unneeded bills,
            // only two were removed, and the third row - 23x75mmR (Buck) - remained unchanged afterwards).
            // Cause: the list in the window comes from LoadoutUtility.UnneededAmmoBillCandidates and scans ALL
            // benches, but this delete path ran over `ammoEntries`, which is the bill overview's list built by
            // scope/search filter. A bill for ammo not currently in it was undeletable - so the window offered an
            // action that could do nothing.
            // Exactly the same diagnosis has long been in the comment of the sister method ArmDeleteAllAmmoBills,
            // which therefore goes directly over the benches; it never made it here.
            // DELIBERATELY only for the `only` path, i.e. for a real selection by the player. The blanket call
            // (only == null, e.g. from the arm run) keeps its previous, narrower scope - silently widening it here
            // would mean an arm pass suddenly deletes more bills than before, which would be a step in the wrong
            // direction.
            // The pass runs over EVERY checked def, including those that already had a row above:
            // `entry.activeBills` only collects "do until X" bills, so a "do 5x" bill for the same ammo was left
            // standing and the row came back unchanged on the next open. Nothing can be deleted twice - whatever
            // the first pass removed is no longer in the billStack here.
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
                // Blocked ammo (unresearched / no bench) must NOT be silently sorted out here anymore once someone
                // actually needs it (a colonist required only .30-06 Springfield (AP-I), amount 40 - the supply
                // window showed NO row for it at all, not even with "show only missing" turned off). The demand
                // vanished without a trace, and the screen reported "nothing missing" while a colonist was
                // permanently left empty-handed.
                // Dialog_SupplyLoadouts has long handled this case: BuildAmmoEntries explicitly creates a
                // non-selectable "can't be produced" row for unresearched ammo (see the comment there). Just as with
                // the bill-size bug before, the filter in the CALLER was the problem - whatever is dropped here never
                // reaches the window.
                // Without demand the old behaviour stays: unresearched ammo that nobody requires has no business in
                // the supply window.
                bool blocked = !entry.isResearched || entry.validBenches.Count == 0;
                if (blocked && entry.assignedDemand <= 0) continue;
                // Unrounded, same reasoning as in GenerateBillForEntry: what goes into the popup preview here must
                // be the same number the bill later carries - otherwise the preview shows 500 and the bill says 60
                // (or vice versa).
                // The same rule as in AutoGenerateAll: demand if available, otherwise the typed batch count.
                // Previously this additionally depended on the scope toggle here, and on the filter in
                // AutoGenerateAll - two different switches for the same question, which is exactly why the two
                // paths could produce different numbers.
                int target;
                if (entry.assignedDemand > 0)
                    target = entry.assignedDemand;
                else if (!int.TryParse(entry.amountString, out int amount) || amount <= 0)
                    continue;
                else
                    target = amount;
                // Is the existing bill ENOUGH, rather than: is there one at all (confirmed with a clean A/B check).
                // Here `if (entry.activeBills.Count > 0) continue;` used to stand BEFORE the demand calculation - a
                // bill "until 100" made a row with 400 demand vanish completely from the supply popup, and with the
                // default "show only missing" that read as "nothing missing". The quantity-exact test for this
                // already existed, but in Dialog_SupplyLoadouts.BuildAmmoEntries - one layer TOO LATE for this
                // entry point: whatever is filtered out here never reaches the popup and can't be re-evaluated
                // there. Hence the same calculation right at the source.
                // Deliberately the same HighestBillTargetFor as there: RepeatCount bills ("do 5x") don't count
                // because they don't hold a stock level. A bill that is too small is raised on confirm instead of
                // doubled - AddOrUpdateAmmoBill sets targetCount absolutely.
                // The quantity check applies only to producible rows: a blocked row should specifically NOT
                // disappear because of an existing bill - it is the hint itself.
                if (!blocked && LoadoutUtility.HighestBillTargetFor(entry.ammoDef) >= target) continue;
                specificAmmo[entry.ammoDef] = target;
                // Without a bench there is no bench to preset - Dialog_SupplyLoadouts then finds one itself
                // (AmmoBenchesFor) and otherwise shows its own warning sign in the workbench column.
                if (entry.validBenches.Count > 0) benches[entry.ammoDef] = entry.validBenches[0];
            }
            // Obsolete-bill preview, same scope swap - a live snapshot of what "Remove obsolete ammo bills"
            // would delete right now, shown in the popup itself so the player can see it before confirming.
            // Scope-AWARE variant: UnneededAmmoEntries() only knows the currently assigned pawns. But this preview
            // is computed INSIDE the scope swap above - with the toggle on "created loadouts", the assignment view
            // still applied.
            // Failure scenario: a colony with created loadouts not yet assigned to anyone reported EVERY ammo bill
            // as unneeded, including the ones just generated.
            // Now that the checkboxes are pre-checked again, this is no longer a display bug but one click away
            // from data loss - hence first here.
            List<ThingDef> obsoleteCandidates =
                LoadoutUtility.UnneededAmmoBillCandidates();
            // See the rationale at the Request below - computed here already because otherwise the "nothing to
            // do" shortcut below swallows the window when the ONLY thing to clean up is an orphaned weapon bill.
            // Exactly that case was the problem.
            List<ThingDef> obsoleteWeapons =
                LoadoutUtility.UnneededWeaponBillCandidates();

            filterMode = saved;
            RefreshBenchesAndAmmo();

            // Only if there is REALLY nothing - otherwise the window opens and says what it checked.
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
            // One pass, three outputs: the template names for ammo (templateUsedBy) AND those for weapons/packs -
            // see the block above weaponTemplateUsedBy further down.
            LoadoutUtility.ComputeMissingForCreatedLoadouts(out _, out _, out Dictionary<ThingDef, List<string>> weaponTemplateUsedBy,
                out _, out Dictionary<ThingDef, List<string>> templatePackUsedBy, out _, out _, out var templateUsedBy, out _);
            foreach (var kv in templatePackUsedBy) weaponTemplateUsedBy[kv.Key] = kv.Value;
            // The WEAPON names as well: a thrown-explosive row sits in the ammo list, but its demand is computed on
            // the weapon side - without this dictionary its Assig. column reliably claims "nobody", even when two
            // colonists carry it as a sidearm. Full rationale in Dialog_SupplyLoadouts, where the names are
            // assigned.
            //
            // CORRECTED: this first used ScanMissingAssignedWeapons, which answers a DIFFERENT question - namely
            // who is still MISSING the weapon. Its usedBy is only filled once stock AND running bills are used
            // up; someone who already carries it never appears there. Via this entry point the column kept
            // saying "nobody" while the same window content listed "Assigned to: <pawn> x4, <pawn> x4".
            // BuildWeaponUsedBy is the question actually being asked here.
            Dictionary<ThingDef, List<string>> weaponAssignedUsedBy = Dialog_AssignLoadouts.BuildWeaponUsedBy(
                LoadoutUtility.AllColonyPawns().Select(p => (p.GetLoadout(), p.LabelShortCap.ToString())));
            // And the TEMPLATE half of the same question (second half of the same problem: "no loadout uses the
            // Satchel Charge"). This entry point never set weaponTemplateUsedBy, so the circle dot of a
            // thrown-explosive row was structurally always grey here - regardless of how many templates contain
            // the item. Exactly the gap the comment at Request.assignedUsedBy warns about ("every caller fills
            // BOTH"), and the only one of the five call sites that didn't.

            Find.WindowStack.Add(new Dialog_SupplyLoadouts(new Dialog_SupplyLoadouts.Request
            {
                specificAmmo = specificAmmo,
                ammoBenches = benches,
                assignedUsedBy = assignedUsedBy,
                templateUsedBy = templateUsedBy,
                weaponAssignedUsedBy = weaponAssignedUsedBy,
                weaponTemplateUsedBy = weaponTemplateUsedBy,
                obsoleteAmmoCandidates = obsoleteCandidates,
                // The WEAPON half of the same cleanup list, previously never set here (found by searching for
                // further occurrences): this entry point is older than the shared hookup
                // LoadoutUtility.AttachObsoleteCleanup and only wired up its own ammo half.
                // Consequence: an orphaned WEAPON bill could be cleaned up from the supply window when it opened by
                // itself on a tab switch, but not when opened here via Auto-generate - the same question, two
                // answers, depending on the path.
                // Not via AttachObsoleteCleanup, because the ammo half here deliberately keeps its own message
                // ("Removed N unneeded ammo bills"); the candidate set is the same.
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

        // One-click: create a bill for every ammo the current list needs, ignoring the checkboxes. The amount
        // follows real demand - the reserve curve over all colonists assigned this ammo - and only where there
        // is no demand at all, the typed batch count.
        //
        // The binding to the FILTER has been removed, and that was the actual bug behind "Auto-generate
        // requests 500, the supply popup 150". The old comment here claimed the other filters had "no
        // assignment signal" - that isn't true: entry.assignedDemand is computed for EVERY row when the list is
        // built, regardless of the filter. So if the filter was set to anything other than "assigned
        // loadouts", the amount silently fell back to the default batch count - 500 for a 500-round recipe -
        // while the popup path (OpenMissingAmmoSupplyPopup switches internally to AssignedLoadouts) used the
        // demand.
        // The filter decides WHICH ammo is in the list. How much of it to produce is an independent question -
        // and it has only one right answer, not one per filter.
        // An earlier fix had only removed the rounding up and missed this switch.
        private int AutoGenerateAll(Dictionary<ThingDef, int> queuedRecord = null)
        {
            int count = 0;
            foreach (AmmoEntry entry in ammoEntries)
            {
                if (!entry.isResearched || entry.validBenches.Count == 0) continue;
                // The demand EXACTLY as the reserve curve delivers it - no more rounding up to whole runs (see
                // GenerateBillForEntry).
                int? target = null;
                if (entry.assignedDemand > 0)
                    target = entry.assignedDemand;
                // Second occurrence of the same pattern as in the popup path above (found by searching): here too
                // a plain `activeBills.Count > 0` made a bill "until 100" count as done with 400 demand. The
                // "never overwrite" of the old comment is still honoured without carrying the bug along: covered
                // now means covered by QUANTITY, and because we stop when the target is sufficient, the write path
                // below can no longer reach a hand-set LARGER target - it only raises.
                if (target.HasValue && LoadoutUtility.HighestBillTargetFor(entry.ammoDef) >= target.Value) continue;
                if (!target.HasValue && entry.activeBills.Count > 0) continue;   // without a demand figure, keep the old conservative verdict
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
