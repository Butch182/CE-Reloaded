using System;
using System.Collections.Generic;
using System.Linq;
using RimWorld;
using Verse;
using UnityEngine;
using CombatExtended;

namespace CEReloaded
{
    // The "Equipment Packs" tab: create/edit/delete Equipment Packs. Each pack is a normal CE Loadout tagged
    // "[Pack] Name" (see EquipmentPacks.cs) that can hold pure supply content from several categories at
    // once - one "Field Medic Kit" pack might contain medicine, food and a backpack together. Never a
    // weapon of any kind (main, sidearm, or grenade) - those are Weapon Loadouts, attached the same
    // generic way via MultiParentUtility. This tab is the only place a pack's CONTENTS get authored; every
    // other screen that references packs (Role Mode's drag list, Assign Loadouts' per-pawn attachment)
    // only ever browses or attaches what's built here.
    public class Dialog_Ingredients
    {
        public Window parentWindow;

        private Loadout selectedPack;
        private string nameBuffer = "";
        private Vector2 listScroll, editorScroll;
        private readonly Dictionary<string, string> countBuffers = new Dictionary<string, string>();
        // Zu welchem Wert wurde der Puffer zuletzt befuellt (2026-08-13: Menge von 30 auf 8 geaendert und
        // uebernommen - Masse, Sperrigkeit und Kosten folgten der 8, das Mengenfeld zeigte aber weiter 30,
        // auch nach Auswahlwechsel).
        // Der Puffer wurde nur befuellt, wenn der SCHLUESSEL fehlte. Wer die Zeile einmal gesehen hatte, sah
        // seinen Text danach fuer immer - auch wenn die Menge von aussen geaendert wurde (Auto-generate
        // schreibt direkt in den Pack).
        // Das war nicht nur eine falsche Anzeige: Widgets.TextFieldNumeric liest den PUFFER und schreibt das
        // Ergebnis nach `val` zurueck, und der Aufrufer setzt damit `slot.count`. Die veraltete 30 haette die
        // echte 8 im naechsten Frame ueberschrieben - die Aenderung waere beim blossen Ansehen des Editors
        // wieder verschwunden.
        private readonly Dictionary<string, int> countBufferFor = new Dictionary<string, int>();

        // Mengenfeld mit selbst-ungueltig-werdendem Puffer. Der Puffer gilt nur so lange, wie der Wert
        // dahinter derselbe ist, zu dem er befuellt wurde; sonst wird er frisch aus dem Slot gesetzt.
        // Waehrend des Tippens bleibt er erhalten (der Aufrufer schreibt den getippten Wert sofort in den
        // Slot, seeded und slot.count laufen also mit).
        private void DrawCountField(Rect r, string key, int current, ref int val)
        {
            if (!countBuffers.TryGetValue(key, out string buf)
                || !countBufferFor.TryGetValue(key, out int seeded) || seeded != current)
                buf = current.ToString();
            Widgets.TextFieldNumeric(r, ref val, ref buf, 0f, 99999f);
            countBuffers[key] = buf;
            countBufferFor[key] = val;
        }
        // Defs never change within a running game - computed once instead of re-scanning the whole ThingDef
        // database with LINQ every single frame this tab is open (DoWindowContents runs every frame, not
        // just on interaction).
        private static List<Def> utilityPoolCache;

        private const float SectionHeaderH = 24f;
        private const float AddRowH = 32f;
        private const float ItemRowH = 30f;
        private const float EmptyRowH = 22f;
        private const float SectionGapH = 18f;

        // One accent color per category, reused for the card backdrop and the (tinted) section title -
        // same meaning, same color, everywhere a category shows up in this tab.
        private static readonly Dictionary<PackCategory, Color> CategoryAccent = new Dictionary<PackCategory, Color>
        {
            { PackCategory.Medical, new Color(0.85f, 0.35f, 0.35f) },
            { PackCategory.Food, new Color(0.85f, 0.72f, 0.30f) },
            { PackCategory.Drugs, new Color(0.68f, 0.48f, 0.88f) },
            { PackCategory.Utility, new Color(0.35f, 0.65f, 0.88f) },
            { PackCategory.Other, new Color(0.65f, 0.65f, 0.65f) },
        };

        public void DoWindowContents(Rect inRect)
        {
            Text.Font = GameFont.Medium;
            Widgets.Label(new Rect(0, 0, inRect.width, 30f), "CER_Ingredients_Title".Translate());
            Text.Font = GameFont.Small;
            GUI.color = new Color(1f, 1f, 1f, 0.6f);
            // Hoehe gemessen statt 22f fest (2026-08-26: der deutsche Einleitungssatz ist
            // laenger als eine Zeile und brach mitten im Satz ab - alles unterhalb des 22f-Rects war weg).
            // Der Inhalt darunter rueckt mit, sonst laege die zweite Zeile auf den Panels.
            string subtitle = "CER_Ingredients_Subtitle".Translate();
            float subtitleH = Text.CalcHeight(subtitle, inRect.width);
            Widgets.Label(new Rect(0, 30f, inRect.width, subtitleH), subtitle);
            GUI.color = Color.white;

            float top = 30f + subtitleH + 6f;
            float listW = 260f, gap = 14f;
            // 8px clear of the bottom bar - the panels' own frame border used to end EXACTLY where the
            // Auto-generate button began, so the button visually hung into the panel's border.
            float panelH = inRect.height - top - LoadoutUtility.BottomBarH - 8f;
            Rect listPanel = new Rect(0, top, listW, panelH);
            Rect editorPanel = new Rect(listW + gap, top, inRect.width - listW - gap, panelH);

            DrawPackList(listPanel);
            DrawEditor(editorPanel);

            float xr = inRect.width;
            if (LoadoutUtility.BottomActionButton(inRect, ref xr, 170f, "CER_Ingredients_AutoGenerateButton".Translate(),
                tip: "CER_Ingredients_AutoGenerateTip".Translate(),
                icon: LoadoutUtility.TexWand))
            {
                Dialog_GenerateLoadouts loadoutsDialog = (parentWindow as Dialog_CEReloadedTabs)?.LoadoutsDialog;
                Find.WindowStack.Add(new Dialog_GenerateEquipmentPack(loadoutsDialog, pack =>
                {
                    selectedPack = pack;
                    nameBuffer = EquipmentPackUtility.BareName(pack.label);
                }));
            }
        }

        // Same "(2)", "(3)", ... uniqueness convention Dialog_GenerateLoadouts' own Role-copy naming already
        // uses (2026-08-01 fix) - repeated clicks on "New" used to spam identically-named packs, indistinguishable
        // in this list or in Dialog_PickPacks except by opening each one.
        // Wie UniquePackName, aber das Pack, das gerade umbenannt wird, zaehlt nicht als Kollision mit
        // sich selbst - sonst waechst der Name bei jedem Tastendruck um ein "(2)".
        private static string UniquePackNameExcept(string baseName, Loadout self)
        {
            string b = (baseName ?? "").Trim();
            if (b.Length == 0) return b;
            var m = System.Text.RegularExpressions.Regex.Match(b, @"^(.*) \((\d+)\)$");
            string root = m.Success ? m.Groups[1].Value : b;
            HashSet<string> existing = new HashSet<string>(
                EquipmentPackUtility.AllEquipmentPacks().Where(p => !ReferenceEquals(p, self))
                    .Select(p => EquipmentPackUtility.BareName(p.label)), StringComparer.OrdinalIgnoreCase);
            if (!existing.Contains(b)) return b;
            int n = 2;
            string candidate = root + " (" + n + ")";
            while (existing.Contains(candidate)) { n++; candidate = root + " (" + n + ")"; }
            return candidate;
        }

        private static string UniquePackName(string baseName)
        {
            string b = baseName ?? "Pack";
            var m = System.Text.RegularExpressions.Regex.Match(b, @"^(.*) \((\d+)\)$");
            if (m.Success) b = m.Groups[1].Value;
            HashSet<string> existing = new HashSet<string>(EquipmentPackUtility.AllEquipmentPacks().Select(p => EquipmentPackUtility.BareName(p.label)), StringComparer.OrdinalIgnoreCase);
            if (!existing.Contains(b)) return b;
            int n = 2;
            string candidate = b + " (" + n + ")";
            while (existing.Contains(candidate)) { n++; candidate = b + " (" + n + ")"; }
            return candidate;
        }

        private void DrawPackList(Rect panel)
        {
            Widgets.DrawMenuSection(panel);
            Rect header = panel.ContractedBy(6f);
            Widgets.Label(new Rect(header.x, header.y, header.width - 90f, 24f), "CER_Ingredients_ListHeader".Translate());
            Rect newRect = new Rect(header.xMax - 84f, header.y - 2f, 84f, 24f);
            if (LoadoutUtility.ButtonTextFit(newRect, "CER_Ingredients_NewButton".Translate().ToString()))
            {
                // Unique name, not always the same bare default (2026-08-01 fix, edge case:
                // repeated clicks used to spam identical "New Pack" entries, indistinguishable in this list
                // and in Dialog_PickPacks except by opening each one) - same "(2)", "(3)", ... convention
                // Dialog_GenerateLoadouts' own Role-copy naming already uses.
                Loadout pack = new Loadout { defaultLoadout = false, label = EquipmentPackUtility.PackMarkerPrefix + " " + UniquePackName("CER_Ingredients_NewPackDefaultName".Translate()) };
                LoadoutManager.AddLoadout(pack);
                // Kennung setzen (2026-08-17): ein Pack wird ab jetzt ueber die Identitaet erkannt, nicht
                // ueber den "[Pack]"-Marker im Namen. NACH AddLoadout, weil die UniqueID erst dort entsteht.
                EquipmentPackUtility.MarkAsPack(pack);
                selectedPack = pack;
                nameBuffer = EquipmentPackUtility.BareName(pack.label);
            }

            List<Loadout> packs = EquipmentPackUtility.AllEquipmentPacks();
            Rect outRect = new Rect(header.x, header.y + 28f, header.width, header.height - 28f);
            Rect viewRect = new Rect(0, 0, outRect.width - 16f, packs.Count * 30f + 4f);
            Widgets.BeginScrollView(outRect, ref listScroll, viewRect);
            float ry = 0f;
            foreach (Loadout pack in packs)
            {
                Rect row = new Rect(0, ry, viewRect.width, 28f);
                bool active = pack == selectedPack;
                if (active) Widgets.DrawHighlightSelected(row); else if (Mouse.IsOver(row)) Widgets.DrawHighlight(row);
                Rect delRect = new Rect(row.xMax - 22f, row.y + 3f, 20f, 20f);
                Widgets.Label(new Rect(row.x + 6f, row.y + 3f, row.width - 32f, 22f), EquipmentPackUtility.StripMarker(pack.label).Truncate(row.width - 38f));
                // Confirmation added (2026-08-01) - unlike Role deletion and the bulk "delete existing packs"
                // button, a single pack could previously be deleted with one click, silently dropping it from
                // every colonist currently carrying it.
                Loadout packCapture = pack;
                if (Widgets.ButtonText(delRect, "CER_Ingredients_DeleteButtonGlyph".Translate()))
                {
                    Find.WindowStack.Add(LoadoutUtility.CompactConfirmation(
                        "CER_Ingredients_DeletePackConfirm".Translate(EquipmentPackUtility.StripMarker(packCapture.label)),
                        () =>
                        {
                            LoadoutDeletionUtility.Delete(packCapture);
                            if (selectedPack == packCapture) selectedPack = null;
                        }, true));
                }
                TooltipHandler.TipRegion(delRect, "CER_Ingredients_DeletePackTip".Translate());
                Rect clickArea = new Rect(row.x, row.y, row.width - 26f, row.height);
                if (Widgets.ButtonInvisible(clickArea))
                {
                    selectedPack = pack;
                    nameBuffer = EquipmentPackUtility.BareName(pack.label);
                }
                ry += 30f;
            }
            if (packs.Count == 0)
            {
                GUI.color = new Color(1f, 1f, 1f, 0.5f);
                Widgets.Label(new Rect(4f, 4f, viewRect.width - 8f, 40f), "CER_Ingredients_NoPacksYet".Translate());
                GUI.color = Color.white;
            }
            Widgets.EndScrollView();
        }

        private void DrawEditor(Rect panel)
        {
            Widgets.DrawMenuSection(panel);
            Rect inner = panel.ContractedBy(10f);
            if (selectedPack == null)
            {
                GUI.color = new Color(1f, 1f, 1f, 0.5f);
                Widgets.Label(inner, "CER_Ingredients_SelectPackPrompt".Translate());
                GUI.color = Color.white;
                return;
            }

            float y = inner.y;
            Widgets.Label(new Rect(inner.x, y, 50f, 24f), "CER_Ingredients_NameLabel".Translate());
            string newName = Widgets.TextField(new Rect(inner.x + 54f, y, inner.width - 54f, 24f), nameBuffer);
            if (newName != nameBuffer)
            {
                nameBuffer = newName;
                // Duplikatschutz auch beim UMBENENNEN (2026-08-09). Das Anlegen hatte ihn ueber
                // UniquePackName laengst, das Umbenennen nicht - man konnte einem Pack den Namen eines
                // anderen geben und hatte danach zwei gleichnamige, die weder in Auswahlmenues noch fuer
                // den Generator unterscheidbar sind.
                // Bewusst still korrigierend statt blockierend: waehrend des Tippens waere jede Meldung
                // eine Belaestigung, und ein blockiertes Feld fuehlt sich kaputt an. Ein angehaengtes
                // "(2)" ist sichtbar genug und dieselbe Loesung, die das Anlegen schon benutzt.
                string wanted = nameBuffer.Trim();
                string unique = UniquePackNameExcept(wanted, selectedPack);
                selectedPack.label = EquipmentPackUtility.PackMarkerPrefix + " " + unique;
                // Beim Umbenennen die Kennung nachziehen (2026-08-17) - das ist die Selbstheilung fuer
                // Packs, die es vor der Umstellung schon gab: sobald eines einmal angefasst wird, haengt
                // seine Erkennung an der Identitaet und nicht mehr am Marker im Namen.
                EquipmentPackUtility.MarkAsPack(selectedPack);
            }
            y += 32f;

            // Masse/Sperrigkeit + WER es traegt + was es WIRKLICH kostet (2026-09-01, beides mit vorhandenen
            // Bausteinen statt neu erfunden):
            // Traeger: die Raute ist DIESELBE Anzeige wie die Assig.-Spalte in Supply Loadouts
            // (LoadoutUtility.DrawUsedByIndicator, samt deren Mouseover-Keys; zaehlt Anker-Traeger UND
            // Rollen-Traeger, siehe EquipmentPackUtility.PackWearerNames).
            // Kosten: die abstrakte Silberzahl ist ersetzt durch DIESELBE Material-Chip-Leiste, die der
            // Pack-Assistent und die Nachschub-Fenster benutzen (DrawTotalCostLine ueber
            // GetLoadoutProductionCosts - Rohstoffe je Rezept, rot wo das Lager nicht deckt; generische
            // Posten wie "Any Medicine" haben kein eigenes Rezept und fallen wie dort bewusst heraus).
            LoadoutUtility.GetLoadoutMassBulk(selectedPack, out float packMass, out float packBulk);
            LoadoutUtility.DrawMassBulkBars(new Rect(inner.x, y, inner.width - 90f, 48f), packMass, packBulk);
            LoadoutUtility.DrawUsedByIndicator(inner.xMax - 44f, y + 12f,
                EquipmentPackUtility.PackWearerNames(selectedPack),
                "CER_SupplyLoadouts_NotAssignedTip".Translate(), diamond: true);
            y += 48f + 4f;
            LoadoutUtility.DrawTotalCostLine(inner.x, y, inner.width, LoadoutUtility.GetLoadoutProductionCosts(selectedPack));
            y += 28f;
            Widgets.DrawLineHorizontal(inner.x, y, inner.width);
            y += 10f;

            Rect bodyRect = new Rect(inner.x, y, inner.width, inner.yMax - y);

            List<LoadoutSlot> allSlots = selectedPack.OwnSlots?.ToList() ?? new List<LoadoutSlot>();
            List<Def> medPool = LoadoutUtility.LoadoutMedicineOptions();
            List<Def> foodPool = LoadoutUtility.LoadoutFoodOptions();
            List<Def> drugPool = LoadoutUtility.LoadoutDrugOptions();
            if (utilityPoolCache == null)
                utilityPoolCache = DefDatabase<ThingDef>.AllDefs
                    .Where(d => LoadoutUtility.IsShieldApparel(d) || LoadoutUtility.IsUtilityWeapon(d))
                    .OrderBy(d => d.label).Cast<Def>().ToList();
            List<Def> utilityPool = utilityPoolCache;

            int medCount = allSlots.Count(s => SlotInCategory(s, PackCategory.Medical));
            int foodCount = allSlots.Count(s => SlotInCategory(s, PackCategory.Food));
            int drugCount = allSlots.Count(s => SlotInCategory(s, PackCategory.Drugs));
            int utilityCount = allSlots.Count(s => SlotInCategory(s, PackCategory.Utility));

            float totalHeight = MeasureMultiItemSection(medCount) + MeasureMultiItemSection(foodCount)
                + MeasureMultiItemSection(drugCount) + MeasureMultiItemSection(utilityCount) + MeasureOtherSection();

            Rect viewRect = new Rect(0, 0, bodyRect.width - 16f, totalHeight);
            Widgets.BeginScrollView(bodyRect, ref editorScroll, viewRect);
            float cy = 0f;
            cy = DrawMultiItemSection(cy, viewRect.width, "CER_Ingredients_SectionMedical".Translate(), PackCategory.Medical, medPool, "CER_Ingredients_ItemNounMedicine".Translate());
            cy = DrawMultiItemSection(cy, viewRect.width, "CER_Ingredients_SectionFood".Translate(), PackCategory.Food, foodPool, "CER_Ingredients_ItemNounFood".Translate());
            cy = DrawMultiItemSection(cy, viewRect.width, "CER_Ingredients_SectionDrugs".Translate(), PackCategory.Drugs, drugPool, "CER_Ingredients_ItemNounDrug".Translate());
            cy = DrawMultiItemSection(cy, viewRect.width, "CER_Ingredients_SectionUtility".Translate(), PackCategory.Utility, utilityPool, "CER_Ingredients_ItemNounUtilityItem".Translate());
            cy = DrawOtherSection(cy, viewRect.width);
            Widgets.EndScrollView();
        }

        // Does this slot (concrete ThingDef or a generic "Any X" placeholder) belong to this section? Always goes
        // through EquipmentPackUtility.CategoryOf - the single source of truth every section's "+ Add" pool is
        // ALSO built to match, so a def can never be classified into (or offered from) two sections at once.
        private static bool SlotInCategory(LoadoutSlot s, PackCategory cat)
        {
            if (s.thingDef != null) return EquipmentPackUtility.CategoryOf(s.thingDef) == cat;
            if (s.genericDef != null) return EquipmentPackUtility.CategoryOf(s.genericDef) == cat;
            return false;
        }

        private static bool DefMatchesSlot(LoadoutSlot s, Def d)
        {
            if (d is ThingDef td) return s.thingDef == td;
            if (d is LoadoutGenericDef gd) return s.genericDef == gd;
            return false;
        }

        private static void AddDefSlot(Loadout pack, Def d, int count)
        {
            if (d is ThingDef td) pack.AddSlot(new LoadoutSlot(td, count));
            else if (d is LoadoutGenericDef gd) pack.AddSlot(new LoadoutSlot(gd, count));
        }

        // Same icon rule as DrawDefIcon's row icons: a concrete ThingDef shows its own icon, a "Any X"
        // generic gets its own hand-drawn category badge (GenericIcons) instead of a borrowed real item's
        // icon - so every FloatMenu option a player picks from already looks like the row it is about to create.
        private static FloatMenuOption DefOption(Def d, string label, Action action)
        {
            if (d is ThingDef td) return new FloatMenuOption(label, action, td);
            if (d is LoadoutGenericDef gd)
            {
                Texture2D badge = GenericIcons.For(gd);
                if (badge != null) return new FloatMenuOption(label, action, badge, Color.white);
            }
            return new FloatMenuOption(label, action);
        }

        private static void DrawDefIcon(Rect r, LoadoutSlot slot)
        {
            if (slot.thingDef != null) { Widgets.ThingIcon(r, slot.thingDef); return; }
            if (slot.genericDef == null) return;
            Texture2D badge = GenericIcons.For(slot.genericDef);
            if (badge != null) GUI.DrawTexture(r, badge);
        }

        // Draws the card backdrop for a whole section (height = that section's own Measure*Section() result,
        // so the tint always matches exactly what's actually drawn) then the tinted, optionally badge-counted
        // title on top - one call per section, right before its content.
        private void DrawSectionCard(float y, float width, float height, PackCategory cat, string title, int? count = null)
        {
            Color accent = CategoryAccent.TryGetValue(cat, out Color c) ? c : Color.grey;
            Rect card = new Rect(0, y, width, Mathf.Max(height - SectionGapH, 0f));
            Widgets.DrawBoxSolid(card, new Color(accent.r, accent.g, accent.b, 0.05f));
            GUI.color = Color.Lerp(Color.white, accent, 0.6f);
            Widgets.Label(new Rect(0, y + 2f, width, 22f), count.HasValue ? "CER_Ingredients_SectionTitleWithCount".Translate(title, count.Value.ToString()).ToString() : title);
            GUI.color = Color.white;
        }

        // Other: escape hatch for anything not covered above - hands off to CE's own native editor, which
        // already supports adding literally anything a Loadout can hold (including weapons, if a player
        // insists - not something this tab's own pickers offer, since weapons belong in Weapon Loadouts).
        private float DrawOtherSection(float y, float width)
        {
            // Der Abschnitt LISTET jetzt, was drinsteht (2026-08-13): was ueber den "Other"-Knopf / CEs
            // Editor hinzugefuegt wurde, steht unten wie in den anderen Bereichen, mit Symbol und Beschreibung.
            // Vorher stand hier nur ein Hinweis und ein Knopf: alles, was ueber CEs eigenen Editor
            // hineinkam und in keine der vier Kategorien faellt, war in diesem Fenster unsichtbar - es
            // zaehlte in Masse und Sperrigkeit mit, ohne dass irgendwo stand, warum (so geschehen mit
            // einer Lizard skin).
            // Gezeichnet wird dieselbe Zeile wie in DrawMultiItemSection: Symbol, Name, Bestandsbalken,
            // Mengenfeld, Loeschknopf. Bewusst KEIN eigener "+ Hinzufuegen"-Knopf - das Hinzufuegen bleibt
            // CEs Editor, weil hier per Definition alles landet, wofuer dieser Tab keine eigene Auswahl hat.
            List<LoadoutSlot> slots = OtherSlots();
            DrawSectionCard(y, width, MeasureOtherSection(slots.Count), PackCategory.Other, "CER_Ingredients_SectionOther".Translate(), slots.Count);
            y += SectionHeaderH;

            GUI.color = new Color(1f, 1f, 1f, 0.6f);
            Widgets.Label(new Rect(0, y, width, 34f), "CER_Ingredients_OtherSectionHint".Translate());
            GUI.color = Color.white;
            y += 38f;
            if (LoadoutUtility.ButtonTextFit(new Rect(0, y, 220f, 28f), "CER_Ingredients_OpenCELoadoutEditorButton".Translate().ToString()))
                Find.WindowStack.Add(new CombatExtended.Dialog_ManageLoadouts(selectedPack));
            y += 28f;

            LoadoutSlot toRemove = null;
            foreach (LoadoutSlot slot in slots)
            {
                Rect row = new Rect(0, y, width, 28f);
                Def def = slot.thingDef != null ? (Def)slot.thingDef : slot.genericDef;
                DrawDefIcon(new Rect(row.x, row.y + 2f, 24f, 24f), slot);

                const float barW = 90f, barGap = 8f, rightReserved = 70f;
                Rect ownedBarRect = new Rect(row.xMax - rightReserved - barGap - barW, row.y + 5f, barW, 18f);
                float labelW = ownedBarRect.x - 8f - (row.x + 28f);
                Widgets.Label(new Rect(row.x + 28f, row.y + 3f, labelW, 22f), def.LabelCap.ToString().Truncate(labelW - 4f));
                LoadoutUtility.ComputeItemOwnership(def, out int carriedOnly, out int carriedAndAssigned, out int assignedInStore, out int freeStored, out int inProduction);
                var ownedSeg = LoadoutUtility.BuildAvailabilitySegments(carriedOnly, carriedAndAssigned, assignedInStore, freeStored, inProduction, def: def);
                // TipWanted: der Orts-Tooltip laeuft ueber alle Karten - nur bauen, wenn er auch gezeigt wird.
                LoadoutUtility.DrawAvailabilityBar(ownedBarRect, ownedSeg, horizontal: true,
                    LoadoutUtility.TipWanted(ownedBarRect) ? LoadoutUtility.BuildPackItemLocationTooltip(def) : null);

                string key = selectedPack.UniqueID + "_other_" + (slot.thingDef != null ? slot.thingDef.defName : slot.genericDef.defName);
                int val = slot.count;
                Rect cntRect = new Rect(row.xMax - 66f, row.y + 2f, 44f, 24f);
                DrawCountField(cntRect, key, slot.count, ref val);
                if (val != slot.count) { if (val <= 0) toRemove = slot; else slot.count = val; }
                Rect delRect = new Rect(row.xMax - 20f, row.y + 3f, 20f, 20f);
                if (Widgets.ButtonText(delRect, "CER_Ingredients_DeleteButtonGlyph".Translate())) toRemove = slot;
                y += ItemRowH;
            }
            if (toRemove != null) selectedPack.RemoveSlot(toRemove);
            if (slots.Count == 0)
            {
                GUI.color = new Color(1f, 1f, 1f, 0.4f);
                Widgets.Label(new Rect(0, y, width, EmptyRowH), "CER_Ingredients_NoneAdded".Translate());
                GUI.color = Color.white;
                y += EmptyRowH;
            }
            y += SectionGapH;
            return y;
        }

        // Alles, was in keine der vier Kategorien faellt - dieselbe Einteilung, die auch die Abschnitte
        // darueber benutzen (EquipmentPackUtility.CategoryOf), damit ein Gegenstand nie in zwei Abschnitten
        // steht und nie in keinem.
        private List<LoadoutSlot> OtherSlots()
            => selectedPack?.OwnSlots?.Where(s => SlotInCategory(s, PackCategory.Other)).ToList() ?? new List<LoadoutSlot>();

        private float MeasureOtherSection() => MeasureOtherSection(OtherSlots().Count);

        private static float MeasureOtherSection(int count)
            => SectionHeaderH + 38f + 28f + (count > 0 ? count * ItemRowH : EmptyRowH) + SectionGapH;

        // Shared editor for "a list of stackable things + counts" sections (Medical/Food/Drugs) -
        // only lists/adds slots belonging to this section's pool, since a pack's OwnSlots holds every
        // category's items together now. Pools mix concrete ThingDefs with CE's "Any X" generic
        // placeholders (LoadoutGenericDef) - both render the same way, just with a borrowed icon for generics.
        private float DrawMultiItemSection(float y, float width, string title, PackCategory cat, List<Def> optionPool, string itemNoun)
        {
            List<LoadoutSlot> allSlots = selectedPack.OwnSlots?.ToList() ?? new List<LoadoutSlot>();
            List<LoadoutSlot> slots = allSlots.Where(s => SlotInCategory(s, cat)).ToList();

            DrawSectionCard(y, width, MeasureMultiItemSection(slots.Count), cat, title, slots.Count);
            y += SectionHeaderH;

            Rect addRect = new Rect(0, y, 140f, 24f);
            if (LoadoutUtility.ButtonTextFit(addRect, "CER_Ingredients_AddItemButton".Translate(itemNoun).ToString()))
            {
                List<FloatMenuOption> opts = new List<FloatMenuOption>();
                foreach (Def d in optionPool)
                {
                    if (allSlots.Any(s => DefMatchesSlot(s, d))) continue;
                    Def local = d;
                    // "(Not researched)" suffix (2026-08-02, applies to every category here -
                    // Medical/Food/Drugs/Utility alike, not just weapon-shaped utility items like shields).
                    // Only for a concrete ThingDef with a real recipe - a generic "Any X" placeholder has no
                    // single research gate of its own (it resolves to whichever family member is available),
                    // and an item with no recipe at all (nothing to research) isn't "not researched" either.
                    string label = local.LabelCap.ToString();
                    if (local is ThingDef ltd)
                    {
                        RecipeDef recipe = LoadoutUtility.GetRecipeFor(ltd);
                        if (recipe != null && !LoadoutUtility.RecipeIsResearched(recipe))
                            label += "CER_Ingredients_NotResearchedSuffix".Translate();
                    }
                    opts.Add(DefOption(local, label, () => AddDefSlot(selectedPack, local, 1)));
                }
                if (opts.Count == 0) opts.Add(new FloatMenuOption("CER_Ingredients_AllAlreadyAdded".Translate(), null));
                Find.WindowStack.Add(new FloatMenu(opts));
            }
            y += AddRowH;

            LoadoutSlot toRemove = null;
            foreach (LoadoutSlot slot in slots)
            {
                Rect row = new Rect(0, y, width, 28f);
                Def def = slot.thingDef != null ? (Def)slot.thingDef : slot.genericDef;
                DrawDefIcon(new Rect(row.x, row.y + 2f, 24f, 24f), slot);

                // Owned bar sits between the name and the count field - same 90px width and right-side
                // reservation convention as everywhere else this bar is used.
                const float barW = 90f, barGap = 8f, rightReserved = 70f;
                Rect ownedBarRect = new Rect(row.xMax - rightReserved - barGap - barW, row.y + 5f, barW, 18f);
                float labelW = ownedBarRect.x - 8f - (row.x + 28f);
                Widgets.Label(new Rect(row.x + 28f, row.y + 3f, labelW, 22f), def.LabelCap.ToString().Truncate(labelW - 4f));
                LoadoutUtility.ComputeItemOwnership(def, out int carriedOnly, out int carriedAndAssigned, out int assignedInStore, out int freeStored, out int inProduction);
                var ownedSeg = LoadoutUtility.BuildAvailabilitySegments(carriedOnly, carriedAndAssigned, assignedInStore, freeStored, inProduction, def: def);
                // Same "In storage:/Carried by:/Assigned to:" breakdown as Weapon Loadouts' own availability
                // bar, not just a bare Total/Available count (2026-07-31, first noticed in the Auto-Generate
                // Equipment Packs popup - this editor shares the exact same bar/ownership call, same gap).
                // TipWanted: der Orts-Tooltip laeuft ueber alle Karten - nur bauen, wenn er auch gezeigt wird.
                LoadoutUtility.DrawAvailabilityBar(ownedBarRect, ownedSeg, horizontal: true,
                    LoadoutUtility.TipWanted(ownedBarRect) ? LoadoutUtility.BuildPackItemLocationTooltip(def) : null);

                // Keyed by pack too, not just defName (2026-08-02 fix: two different packs
                // sharing an item could show/edit each other's stale buffered text after a quick switch -
                // same fix Dialog_ChangeAmmo's own analogous buffer already applies via pawn.thingIDNumber).
                string key = selectedPack.UniqueID + "_" + (slot.thingDef != null ? slot.thingDef.defName : slot.genericDef.defName);
                int val = slot.count;
                Rect cntRect = new Rect(row.xMax - 66f, row.y + 2f, 44f, 24f);
                DrawCountField(cntRect, key, slot.count, ref val);
                // Dragging the count down to 0 removes the slot outright, same as the explicit X button
                // (2026-08-01 fix) - a 0-count slot used to just sit there, still occupying a row and still
                // counted in this section's own item-count badge, indistinguishable from a real entry except
                // by opening the count field itself ("ghost" demand no one could see at a glance).
                if (val != slot.count) { if (val <= 0) toRemove = slot; else slot.count = val; }
                Rect delRect = new Rect(row.xMax - 20f, row.y + 3f, 20f, 20f);
                if (Widgets.ButtonText(delRect, "CER_Ingredients_DeleteButtonGlyph".Translate())) toRemove = slot;
                y += ItemRowH;
            }
            if (toRemove != null) selectedPack.RemoveSlot(toRemove);
            if (slots.Count == 0)
            {
                GUI.color = new Color(1f, 1f, 1f, 0.4f);
                Widgets.Label(new Rect(0, y, width, EmptyRowH), "CER_Ingredients_NoneAdded".Translate());
                GUI.color = Color.white;
                y += EmptyRowH;
            }
            y += SectionGapH;
            return y;
        }

        private float MeasureMultiItemSection(int count) => SectionHeaderH + AddRowH + (count > 0 ? count * ItemRowH : EmptyRowH) + SectionGapH;
    }
}
