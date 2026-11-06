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
        // the paste tooltip elsewhere used to show the ENTIRE instructional subtitle verbatim, e.g. "Sachiko
        // - tick which Weapon Loadout(s) to carry as a sidearm. Changes apply immediately." as if that whole
        // sentence were a "source" - confusing, and sometimes outright misleading (a subtitle warning
        // "nothing applies until Apply" pasted into a context where it DOES apply immediately). Defaults to
        // subtitle for any caller that doesn't bother passing one, so this is purely additive.
        private readonly string sourceLabel;
        private readonly string itemColumnLabel;
        // Feste Kennung fuer die Zwischenablage - NICHT der uebersetzte Spaltenname (2026-08-13, siehe
        // LoadoutClipboard.HasContent). Standard "pack", weil das der urspruengliche Zweck dieses Dialogs ist.
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
        // Ein groupLabel, das fuer eine Gruppe NULL zurueckgibt, bekommt gar keine Zwischenzeile - ihre
        // Zeilen sitzen direkt unter den Spaltenueberschriften (2026-08-15: eine zusaetzliche Zwischenzeile
        // "Backup weapons" unter den Spaltenueberschriften war doppelt). Die
        // Entscheidung, WELCHE Gruppe die stillschweigende ist, gehoert dem Aufrufer und nicht diesem
        // Dialog: "die erste unterdruecken" waere falsch, sobald eine Kolonie gar keine gewoehnlichen
        // Zweitwaffen hat und die Liste mit "Explosives" anfaengt - dann waere ausgerechnet die Gruppe
        // unbeschriftet, deren Name etwas sagt. Hoehenrechnung und Zeichenschleife pruefen beide auf
        // denselben Wechsel, eine null-Gruppe faellt also in beiden gleich heraus.
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
        // Lazy-Umbau (2026-09-01): Waffen OHNE existierende Vorlage als eigene Sektion unter den Loadout-
        // Zeilen. Beim Anhaken laeuft materializeWeapon (EnsureDefaultLoadout) - die geteilte Vorlage
        // entsteht genau dann, und im naechsten Bild ist die Waffe eine normale Loadout-Zeile (allOptions
        // enthaelt sie, weaponOptions nicht mehr). Beide null -> Verhalten unveraendert.
        private readonly Func<List<ThingDef>> weaponOptions;
        private readonly Func<ThingDef, Loadout> materializeWeapon;
        // EINZELAUSWAHL-Modus (2026-09-02, nach dem Vorbild des Zweitwaffen-Fensters statt eines eigenen
        // Fensters): dasselbe Fenster traegt jetzt auch die
        // Hauptwaffen-Auswahl. Radio statt Haken, ein Klick waehlt und schliesst; optional eine
        // "Keins"/"Auto"-Zeile ganz oben und ein Zusatzknopf unten (z.B. "Kombination als Rolle sichern").
        // Der Kopieren-Knopf entfaellt in diesem Modus - eine Einzelauswahl ist keine Menge.
        private readonly bool singleSelect;
        private readonly Action<Loadout> onPickedSingle;
        private readonly Action onNonePicked;
        private readonly string noneLabel;
        private readonly Loadout currentSingle;
        private readonly ThingDef currentWeaponSingle;
        // Gruppiert die WAFFEN-Sektion (z.B. Fernkampf/Nahkampf) analog zu groupLabel fuer die Loadouts;
        // null -> eine einzelne "Waffen"-Zwischenzeile wie bisher.
        private readonly Func<ThingDef, string> weaponGroupLabel;
        // Wieviele Exemplare der LEBENDE Vorschau-Plan schon anderen zugedacht hat (Knappheits-Fix
        // 2026-07-31, uebernommen aus dem alten Pin-Menue) - erscheint als Zeile im Verfuegbarkeits-Tooltip.
        private readonly Func<ThingDef, int> reservedFn;
        private readonly string extraActionLabel;
        private readonly Action extraAction;
        // Sperr-Schalter (2026-09-03): wird waehrend dieser Fensteroeffnung
        // irgendein Schloss umgelegt, feuert beim Schliessen dieser Rueckruf - der Zuweisungs-Reiter
        // haengt daran die Neuzuweisungs-Nachfrage, die Pin-Vorschau ein stilles Neurechnen.
        private readonly Action onDisabledChanged;
        private bool locksTouched;
        // Kopierquelle des Einzelmodus (2026-09-03, "choose a weapon soll auch einen copy button
        // bekommen"): liefert die aktuelle Zuweisung des Kolonisten, null = nichts zu kopieren.
        private readonly Func<Loadout> copyCurrent;
        // Eingeklappte Gruppenleisten, je Fensteroeffnung (2026-09-03, "mach die submenues collapsable").
        private readonly HashSet<string> collapsedGroups = new HashSet<string>();
        // "Nicht verwenden"-Symbole als eigene, ersetzbare PNGs (2026-09-03, "die lock items sind ein
        // witz ... mal ein symbol png dass ich ersetzen kann"): Textures/UI/Icon_DoNotUse.png (gesperrt)
        // und Icon_DoNotUseOff.png (frei).
        private static Texture2D texDoNotUseOn, texDoNotUseOff;
        internal static Texture2D TexDoNotUseOn => texDoNotUseOn ?? (texDoNotUseOn = ContentFinder<Texture2D>.Get("UI/Icon_DoNotUse", false) ?? BaseContent.BadTex);
        internal static Texture2D TexDoNotUseOff => texDoNotUseOff ?? (texDoNotUseOff = ContentFinder<Texture2D>.Get("UI/Icon_DoNotUseOff", false) ?? BaseContent.BadTex);
        // Vorgaben je Waffe einmal pro Fensterlebensdauer - BuildDefaultSettings scannt Munitionslisten,
        // das darf nicht pro Zeile und Bild laufen (dieselbe Grundlast-Klasse wie bei der Bildraten-Drosselung).
        private readonly Dictionary<ThingDef, List<LoadoutSlot>> weaponSlotsCache = new Dictionary<ThingDef, List<LoadoutSlot>>();
        private readonly Dictionary<ThingDef, (float mass, float bulk)> weaponMassBulkCache = new Dictionary<ThingDef, (float, float)>();
        // Ausbau 2026-09-02 (am selben Abend nachjustiert): Schaden-Spalte,
        // Produktionskosten auch fuer Waffen-Zeilen, sortierbare Spaltenkoepfe (immer aktiv, Vorgabe
        // Score absteigend, sortiert INNERHALB der Gruppen - die Zwischenzeilen bleiben stehen),
        // Verfuegbarkeits-Filter wie im alten Waffen-Reiter. Eine Zugewiesen-Spalte war kurz drin und ist
        // wieder raus; ebenso die Radio-Punkte im Einzelmodus - ein Klick auf die
        // Zeile waehlt, die aktuelle Wahl traegt die Auswahl-Hinterlegung.
        private enum PickSort { Name, Score, Special, Avail, Cost, Mass }
        private PickSort pickSort = PickSort.Score;
        private bool pickSortDesc = true;
        // Vorgabe IMMER "Available" -
        // bewusst nicht persistiert, jedes Oeffnen startet gleich.
        private WeaponFilterMode pickerFilter = WeaponFilterMode.AvailableWeapons;
        // Zweite Filtergruppe (2026-09-02 spaet, "fuege rechts noch drei filter ein ... kombinierend"):
        // Waffentyp, UND-verknuepft mit der Verfuegbarkeit. -1 = alle Typen.
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

        // Einmal pro Fensteroeffnung, nicht pro Bild - das ist ein Kolonie-Scan.
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

        // Sortiert INNERHALB der Gruppen (2026-09-02: sonst kamen die Zwischenueberschriften
        // durcheinander): der Gruppenindex aus der Aufruferreihenfolge bleibt Primaerschluessel - die
        // Zwischenzeilen stehen fest, nur die Zeilen darunter ordnen sich um. LINQ-OrderBy ist stabil,
        // eine Liste ohne Sortierschluessel (Pack-Picker ohne Score) behaelt also ihre Reihenfolge.
        private List<ThingDef> SortedWeapons(List<ThingDef> list)
        {
            Dictionary<string, int> gidx = new Dictionary<string, int>();
            int GroupIdx(ThingDef d)
            {
                string g = weaponGroupLabel != null ? (weaponGroupLabel(d) ?? "") : "";
                if (!gidx.TryGetValue(g, out int i)) { i = gidx.Count; gidx[g] = i; }
                return i;
            }
            foreach (ThingDef d in list) GroupIdx(d);   // Gruppenreihenfolge = erstes Auftreten
            // Gesperrte ans GRUPPENende - sichtbar und entsperrbar, aber hinten.
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

        // Ein Auswahlknopf, der beim Klick sein Menue baut - die Optionen entstehen also erst beim
        // Oeffnen und nicht in jedem Bild (die Liste kostet je Eintrag eine Uebersetzung).
        private static void FilterDropdown(Rect r, string label, string tip, Func<List<FloatMenuOption>> build)
        {
            if (LoadoutUtility.ButtonTextFit(r, label))
            {
                List<FloatMenuOption> opts = build();
                if (opts != null && opts.Count > 0) Find.WindowStack.Add(new FloatMenu(opts));
            }
            if (!string.IsNullOrEmpty(tip) && LoadoutUtility.TipWanted(r)) TooltipHandler.TipRegion(r, tip);
        }

        // Beschriftungen der beiden Filter - je EINE Stelle, damit Knopftext und Menueeintrag nicht
        // auseinanderlaufen koennen (das ist die haeufigste Fehlerklasse in diesem Mod).
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
            // Gesperrte ans GRUPPENende, genau wie bei den Waffenzeilen (2026-09-04) - sichtbar und
            // entsperrbar, aber nicht mehr im Weg.
            IOrderedEnumerable<Loadout> o = list.OrderBy(GroupIdx)
                .ThenBy(l => LoadoutUtility.IsLoadoutDisabled(l) ? 1 : 0);
            if (pickSort == PickSort.Name)
                return (pickSortDesc ? o.ThenByDescending(l => displayLabel(l) ?? "", StringComparer.OrdinalIgnoreCase)
                                     : o.ThenBy(l => displayLabel(l) ?? "", StringComparer.OrdinalIgnoreCase)).ToList();
            return (pickSortDesc ? o.ThenByDescending(LoadoutSortValue) : o.ThenBy(LoadoutSortValue)).ToList();
        }

        // Schaden-Spalte: intrinsische Effekte der Waffe, dahinter die Effekte der Standard-Munition MIT
        // Munitions-Plakette (DrawAmmoEffectIcons) - normale und Munitions-Effekte unterscheidbar
        // (2026-09-02). EIN Sammel-Tooltip ueber die ganze Zelle mit allen Zeilen samt Details
        // (am selben Abend wiederhergestellt) - die Zeichner sammeln
        // ihre Zeilen ein statt Einzel-Tooltips zu registrieren; abgeschnittene Symbole stehen trotzdem drin.
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

        // 46 -> 32 (2026-08-13: zu viel Zeilenabstand bei den Eintraegen). Aus der
        // Inhaltskette abgeleitet statt geraten, wie bei den anderen beiden Listen: das hoechste Element in
        // einer Zeile ist die Materialleiste mit 26px, danach kommen die Masse/Sperrigkeits-Balken mit 22px
        // und alles andere ist 16-20px. Jedes davon wird ueber (RowH - h) / 2 mittig gesetzt, ist also von
        // der Zeilenhoehe unabhaengig. 32 laesst dem groessten Element 3px Luft nach oben und unten und
        // schrumpft nichts vom Inhalt - genau der Fehler, der beim Schwester-Popup zu vermeiden war.
        // Dieselbe Zahl fuer Zweitwaffen- und Pack-Auswahl: es ist derselbe Dialog, der Befund betraf
        // beide.
        private const float RowH = 32f;
        // Breiten neu aufgeteilt 2026-08-15 (Sidearms- und Score-Spalte breiter, auf Kosten der Luecke
        // zwischen Contents und Mass/Bulk).
        // Gemessen statt geraten, mit einem Layout-Renderer: die Zeile ist 948px breit (Fenster 1000 - 2x18
        // Rand - 16 Rollbalken). Was wohin ging:
        //   NameColX   200 -> 234  (+34) - der Name war der einzige abgeschnittene Text der Zeile
        //   ScoreColW   64 ->  76  (+12)
        //   AvailColW   46 ->  80  (+34) - die nackte Zahl wurde der Verfuegbarkeitsbalken, der braucht Platz
        //   MaterialsColW 230 -> 210 (-20) - GESCHENKT, kein Verlust: DrawIngredientCell bricht bei
        //     x + w - 22, also passen in 200px genau dieselben 8 Symbole wie vorher in 220px.
        // Die restlichen 60px kommen aus der Contents-Luecke. Schmalster Fall (Zweitwaffen-Picker, Score
        // UND Avail sichtbar): 150px Inhalt = 6 Symbole a 25px. Ein Waffen-Loadout hat Waffe + Munition,
        // also 2-4; die vollen sechs gibt es nur bei Equipment Packs, und die zeigen weder Score noch Avail
        // und behalten deshalb 306px.
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
                // Waffen-Sektion zaehlt mit (2026-09-02) - im Hauptwaffen-Modus stellt sie fast alle
                // Zeilen, ohne sie waere das Fenster ein Schlitz. Und mit den zwei Zusatzspalten
                // (Schaden, Zugewiesen) braucht das Fenster mehr Breite als die reine Pack-Liste.
                int count = Mathf.Max(1, allOptions().Count + (weaponOptions != null ? weaponOptions().Count : 0));
                // Doubled from the original estimate - a handful of rows used to force an immediate scroll
                // with no room to compare more than 2-3 at a glance.
                float h = (140f + HeaderH + count * RowH + 44f) * 2f;
                float w = weaponOptions != null ? Mathf.Min(1150f, UI.screenWidth - 40f) : 1000f;
                return new Vector2(w, Mathf.Min(h, UI.screenHeight - 40f));
            }
        }

        // Nicht unter RimWorlds Lernhilfe schieben (2026-08-19) - siehe
        // LoadoutUtility.KeepClearOfLearningReadout. Ohne Kollision aendert das nichts.
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
        // Gleicher Schnappschuss-Vergleich wie im Tabs-Fenster (2026-08-09): dieser Picker mutiert live,
        // ein Oeffnen-und-wieder-Zumachen
        // ohne Auswahl darf den Nachschub-Dialog nicht ausloesen.
        private string openSignature;

        public override void PostOpen()
        {
            base.PostOpen();
            openSignature = LoadoutUtility.AssignmentSignature();
        }

        public override void PostClose()
        {
            base.PostClose();
            // Sperr-Schalter wurden umgelegt: der Aufrufer entscheidet, was folgt -
            // Zuweisungs-Reiter fragt nach Neuzuweisung der Traeger, die Pin-Vorschau rechnet still neu.
            if (locksTouched) onDisabledChanged?.Invoke();
            if (!checkProductionOnClose) return;
            // Nicht, solange das Tab-Fenster offen ist (2026-08-14) - siehe die ausfuehrliche Begruendung in
            // Dialog_ChangeAmmo.PostClose. Dort sammelt CheckSupplyOnLeavingAssign denselben Hinweis einmal
            // am Ende ein; hier zusaetzlich zu fragen macht aus jedem angehaengten Pack eine Rueckfrage.
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

            // Verfuegbarkeits-Filter neben der Suche (2026-09-02, die Filter aus dem alten Reiter)
            // - dieselben drei Radios mit denselben Schluesseln wie im alten
            // Waffen-Reiter, ueber den messenden Radio-Helfer (der RadioButtonLabeled-Umbruch von gestern
            // war genau die Bauform, die dieser Helfer vermeidet). Wirkt NUR auf die Waffen-Sektion.
            if (WeaponCols)
            {
                EnsurePickerCaches();
                // ===== Zwei Auswahlknoepfe statt acht Radios (2026-09-04) =====
                //
                // Befund: bei den Optionen oben fehlten die Rollen, und die Radiobuttons nahmen zu viel
                // Platz ein - daher Auswahlknoepfe. Beides stimmte -
                // acht Radios brauchten die ganze Kopfzeile, und "Vanilla loadouts" wurde am rechten
                // Rand schon abgeschnitten; fuer einen sechsten Typ (Rollen) war ohnehin kein Platz.
                // Zwei Knoepfe brauchen zusammen rund 300 Punkte statt gut 900, und ein Auswahlmenue
                // waechst mit jeder weiteren Wahl, ohne dass die Zeile breiter wird.
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

                // Zweite Gruppe: Waffentyp. Nur im Einzelmodus - in der Zweitwaffen-Checkliste saesse sie
                // dem Sperrigkeits-Feld rechts oben im Weg.
                if (singleSelect)
                {
                    Rect typeBtn = new Rect(wfX, y, 168f, 24f);
                    FilterDropdown(typeBtn, "CER_PickPacks_FilterTypeFmt".Translate(TypeFilterLabel(typeFilter)).ToString(),
                        "CER_PickPacks_FilterTypeTip".Translate(), () =>
                    {
                        var opts = new List<FloatMenuOption>();
                        // -1 alle, 0 Fernkampf, 1 Nahkampf, 2 Sonstige, 3 Vanilla-Loadouts, 4 Rollen.
                        // Die 4 ist neu (2026-09-04, "fehlt uns noch roles oder?") - vorher liess sich
                        // auf Rollen nicht filtern, obwohl sie eine eigene Sektion der Liste sind.
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
                    // Der Wert lebt in den MOD-Einstellungen (spielstand-uebergreifend), aber nichts in diesem
                    // Dialog hat ihn je weggeschrieben - RimWorld speichert nur beim Schliessen des eigenen
                    // Einstellungsfensters. Er wirkte also sofort und war beim naechsten Start wieder weg, was
                    // sich anfuehlte, als haette er gar keine Wirkung (2026-08-09). Nur bei echter Aenderung
                    // schreiben, nicht pro Frame - Write() geht auf die Platte.
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

            // "Max sidearm bulk" WIRKT jetzt auch hier (2026-08-25: das Feld hatte keine Auswirkung).
            // Das Feld stand in diesem Fenster und tat fuer diese Liste nichts - es war
            // nur der Wert fuer den Automatik-Picker. Wer eine Zahl in ein Feld ueber einer Liste tippt,
            // erwartet, dass die Liste reagiert. Also dieselbe Regel wie im Loeser (PickSidearm: Waffen-
            // Sperrigkeit ueber der Grenze faellt raus; Sprengmittel und Schilde sind dort nie begrenzt, hier
            // auch nicht - siehe SidearmAssignmentUtility.CapRelevantBulk). Ein angehaktes Loadout bleibt
            // IMMER sichtbar, sonst koennte man es nicht mehr abwaehlen. Die Zahl der Ausgeblendeten steht
            // unten in der Leiste, damit "wo ist mein Loadout hin" eine Antwort hat.
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

            // Die Waffen-Sektion (Lazy-Umbau 2026-09-01): dieselbe Suche, dieselbe Sperrigkeitsgrenze wie
            // die Loadout-Zeilen (Granaten sind wie im Loeser von der Grenze ausgenommen - PickGrenade
            // kennt keine, siehe SidearmAssignmentUtility.CapRelevantBulk).
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

            // Verfuegbarkeits-Filter (nur Waffen-Sektion; die aktuelle Waffe bleibt immer sichtbar).
            if (WeaponCols && weapons.Count > 0 && pickerFilter != WeaponFilterMode.AllWeapons)
            {
                EnsurePickerCaches();
                weapons = weapons.Where(d => d == currentWeaponSingle
                    || (pickerFilter == WeaponFilterMode.AvailableWeapons
                        ? ownedCounts.TryGetValue(d, out int oc) && oc > 0
                        : Dialog_GenerateLoadouts.IsResearched(d))).ToList();
            }

            // Typ-Filter (zweite Gruppe, UND-verknuepft mit der Verfuegbarkeit) - wirkt auf Waffen- UND
            // Loadout-Zeilen (deren Typ ueber die repraesentative Waffe; ohne Waffe immer sichtbar).
            // KEIN Immer-sichtbar mehr fuer die aktuelle Wahl (2026-09-03: die Ruger Mini stand unter
            // "others"): die aktuelle Waffe umging jeden Typ-Filter und stand dann als
            // einzige Fernkampfwaffe unter einer fremden Filterwahl - ein Typ-Filter ist eine Sichtlinse,
            // keine Verfuegbarkeitsfrage).
            if (WeaponCols && singleSelect && typeFilter >= 0)
            {
                if (typeFilter == 3)
                {
                    // Nur Vanilla-Loadouts: Waffenliste leer, Rollen raus.
                    weapons = new List<ThingDef>();
                    packs = packs.Where(l => !RoleUtility.IsRoleLoadout(l)).ToList();
                }
                else if (typeFilter == 4)
                {
                    // Nur Rollen (2026-09-04) - das Gegenstueck zu 3, gleiche Bauform.
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

            // Sortierung IMMER aktiv (2026-09-02, nachjustiert: Vorgabe ist Score) -
            // innerhalb der Gruppen, die Zwischenzeilen stehen fest.
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

            // Close und Copy teilen EINE Schriftstufe (de-buttontext-sweep, 2026-08-29) - siehe LoadoutUtility.ButtonRowFont.
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
                // Kopieren der AKTUELLEN Zuweisung (2026-09-03, "choose a weapon soll auch einen copy
                // button bekommen") - Ablage-Sorte 'mainweapon', die Einfuege-Spalte der Zeilen nimmt sie an.
                if (copyCurrent != null)
                {
                    Loadout curCopy = copyCurrent();
                    Rect copyBtn = new Rect(bx, inRect.height - bottomBarH + 6f, 120f, 30f);
                    GUI.color = curCopy != null ? Color.white : new Color(1f, 1f, 1f, 0.45f);
                    if (LoadoutUtility.ButtonTextFit(copyBtn, "CER_PickPacks_CopyButton".Translate().ToString()) && curCopy != null)
                    {
                        LoadoutClipboard.Copy("mainweapon", new List<Loadout> { curCopy }, sourceLabel);
                        // Die Meldung sagt seit 2026-09-04 auch, WOHIN eingefuegt wird. "Kopiert." allein
                        // laesst den Spieler mit einem Erklaertext zurueck, der eine "Einfuege-Spalte"
                        // verspricht - und die steht in einem anderen Fenster, das er dafuer erst oeffnen
                        // muss. Zweimal als Fehler aufgefallen (siehe DrawPasteColumn).
                        Messages.Message("CER_PickPacks_CopiedWeaponMsgFmt".Translate(LoadoutUtility.LoadoutDisplayName(curCopy), sourceLabel), MessageTypeDefOf.TaskCompletion, false);
                    }
                    GUI.color = Color.white;
                    TooltipHandler.TipRegion(copyBtn, "CER_PickPacks_CopyWeaponTip".Translate());
                    bx += 128f;
                }
                // Alle eigenen (Vanilla-)Loadouts loeschen (2026-09-03, "Delete all own loadouts <n>") -
                // Rollen bleiben unberuehrt; die Liste holt sich allOptions() beim naechsten Bild frisch.
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

        // emptyOverride: der Text fuer eine leere Liste, wenn der Grund nicht Suche/keine Optionen ist
        // (2026-08-25: alles von der Sperrigkeitsgrenze ausgeblendet).
        private void DrawPackList(Rect rect, List<Loadout> packs, List<ThingDef> weapons, string emptyOverride = null)
        {
            // No boxed panel background - matches the plain, direct-on-window-background look Dialog_
            // SquadSetup's own lists use (see Dialog_GenerateEquipmentPack.DrawPlanList for the same fix),
            // so this popup's look doesn't diverge from the rest of the mod's list-style dialogs.
            Rect inner = rect;

            if (packs.Count == 0 && weapons.Count == 0)
            {
                GUI.color = new Color(1f, 1f, 1f, 0.5f);
                // Ohne Suchtext ist "Keine Treffer fuer \"\"" keine Auskunft, sondern ein leeres
                // Anfuehrungszeichenpaar (2026-09-04, im Test am Typ-Filter "Vanilla
                // loadouts" in einem Spielstand ohne handgebaute Loadouts). Dann sagt der Satz, was
                // wirklich los ist: der FILTER zeigt nichts, nicht die Suche.
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

            // Sortierbare Koepfe (2026-09-02, alle Spalten sortierbar): Klick sortiert,
            // zweiter Klick dreht die Richtung; sortiert wird immer (Vorgabe Score absteigend), innerhalb
            // der Gruppen. Nur die Inhalts-Spalte sortiert nicht (eine Symbolleiste hat kein Mass).
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
                // Schaden-Spalte (2026-09-02, "die damage types bitte listen"): dieselben Symbole und
                // derselbe Kopf wie die Besonderheit-Spalte des alten Waffen-Reiters.
                HeaderCell(new Rect(hx, inner.y, DamageColW - 6f, 18f), "CER_GenerateLoadouts_ColSpecial".Translate(), PickSort.Special);
                hx += DamageColW;
            }
            if (showAvail) HeaderCell(new Rect(hx, inner.y, AvailColW - 6f, 18f), "CER_PickPacks_AvailColumnHeader".Translate(), PickSort.Avail);
            GUI.color = new Color(1f, 1f, 1f, 0.55f);
            Widgets.Label(new Rect(inner.x + contentsColX, inner.y, rowSpaceW - statsColW - contentsColX - 6f, 18f), "CER_PickPacks_ContentsColumnHeader".Translate());
            GUI.color = Color.white;
            // Produktionskosten VOR Masse/Sperrigkeit (2026-08-15, Spalten getauscht). Nur die Reihenfolge
            // im rechten Block dreht sich;
            // die Zeile unten liest dieselben zwei Konstanten in derselben neuen Reihenfolge.
            HeaderCell(new Rect(inner.x + rowSpaceW - statsColW, inner.y, MaterialsColW - 6f, 18f), "CER_PickPacks_ProductionCostColumnHeader".Translate(), PickSort.Cost);
            HeaderCell(new Rect(inner.x + rowSpaceW - statsColW + MaterialsColW, inner.y, MassBulkColW - 6f, 18f), "CER_PickPacks_MassBulkColumnHeader".Translate(), PickSort.Mass);
            Text.Font = GameFont.Small;
            Widgets.DrawLineHorizontal(inner.x, inner.y + HeaderH, inner.width);

            Rect listArea = new Rect(inner.x, inner.y + HeaderH + 4f, inner.width, inner.height - HeaderH - 4f);
            const float groupHeaderH = 26f;

            // ===== Gruppen als einklappbare Segmente (2026-09-03, "mach die submenues collapsable") =====
            // Loadout- und Waffen-Sektion werden EINE Segmentliste; jede Gruppe mit Kopfzeile ist eine
            // Leiste mit Pfeil und Zaehler, Klick klappt ein/aus (collapsedGroups, je Fensteroeffnung).
            // Zeilen ohne Gruppenkopf (groupLabel liefert null) bleiben immer sichtbar - dieselbe
            // Konvention wie bisher ("die stillschweigende Gruppe gehoert dem Aufrufer").
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

            float noneH = singleSelect && onNonePicked != null ? RowH : 0f;
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
            // "Keins"/"Auto"-Zeile des Einzelauswahl-Modus, ganz oben - dieselbe Radio-Optik wie die
            // Zeilen darunter, damit sie als gleichwertige Wahl liest.
            if (singleSelect && onNonePicked != null)
            {
                // Keine Radios mehr (2026-09-02, nachjustiert: "entferne den radiobutton komplett") -
                // die ganze Zeile ist der Klick, die aktuelle Wahl traegt die Auswahl-Hinterlegung.
                Rect noneRow = new Rect(0, ry, viewRect.width, RowH);
                bool noneOn = currentSingle == null && currentWeaponSingle == null;
                if (noneOn) Widgets.DrawHighlightSelected(noneRow);
                else if (Mouse.IsOver(noneRow)) Widgets.DrawHighlight(noneRow);
                Text.Anchor = TextAnchor.MiddleLeft;
                GUI.color = new Color(1f, 1f, 1f, 0.75f);
                Widgets.Label(new Rect(noneRow.x + 8f, noneRow.y, NameColX - 12f, RowH),
                    (noneLabel ?? "CER_AssignLoadouts_NoneMenuOption".Translate().ToString()).Truncate(NameColX - 16f));
                GUI.color = Color.white;
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
                    // Gestylte, klickbare Gruppenleiste (2026-09-03): dezenter Balken, Auf/Zu-Pfeil,
                    // Name + Zeilenzahl. Die ganze Leiste ist die Klickflaeche.
                    Rect bar = new Rect(0, ry, viewRect.width, groupHeaderH - 3f);
                    bool segCollapsed = collapsedGroups.Contains(seg.header);
                    Widgets.DrawBoxSolid(bar, new Color(1f, 1f, 1f, segCollapsed ? 0.05f : 0.08f));
                    if (Mouse.IsOver(bar)) Widgets.DrawHighlight(bar);
                    GUI.color = new Color(1f, 1f, 1f, 0.85f);
                    GUI.DrawTexture(new Rect(bar.x + 5f, bar.y + (bar.height - 14f) / 2f, 14f, 14f),
                        segCollapsed ? TexButton.Reveal : TexButton.Collapse);
                    Text.Anchor = TextAnchor.MiddleLeft;
                    int segCount = seg.los != null ? seg.los.Count : seg.defs.Count;
                    Widgets.Label(new Rect(bar.x + 26f, bar.y, bar.width - 30f, bar.height),
                        seg.header + "  (" + segCount + ")");
                    Text.Anchor = TextAnchor.UpperLeft;
                    GUI.color = Color.white;
                    if (Widgets.ButtonInvisible(bar))
                    {
                        if (segCollapsed) collapsedGroups.Remove(seg.header);
                        else collapsedGroups.Add(seg.header);
                    }
                    ry += groupHeaderH;
                    open = !segCollapsed;
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

        // Eine Waffen-Zeile der Waffen-Sektion: gleiche Spalten wie eine Loadout-Zeile, nur aus der
        // Vorgabe gerechnet (DefaultSlotsFor/DefaultMassBulkFor), aus der beim Anhaken die geteilte
        // Vorlage entsteht. Die Produktionskosten-Spalte bleibt leer - eine Vorlage, die es noch nicht
        // gibt, hat keinen Auftrag; die Verfuegbarkeit der Waffe selbst steht im Avail-Balken.
        private void DrawWeaponRow(Rect row, ThingDef w, List<Loadout> current, bool showScore, bool showAvail, float contentsColX, float statsColW)
        {
            if (Mouse.IsOver(row)) Widgets.DrawHighlight(row);

            bool locked = LoadoutUtility.IsWeaponDisabled(w);
            bool picked;
            float iconX;
            if (singleSelect)
            {
                // Kein Radio mehr (2026-09-02, nachjustiert): die ganze Zeile waehlt, die aktuelle
                // Waffe traegt die Auswahl-Hinterlegung.
                if (w == currentWeaponSingle) Widgets.DrawHighlightSelected(row);
                // Sperr-Schalter (2026-09-03) - VOR dem Zeilen-Klick, sonst frisst
                // der unsichtbare Zeilenknopf den Klick auf das Schloss. Gesperrt: Zeile ausgegraut,
                // nicht waehlbar, steht am Gruppenende (SortedWeapons); der Schalter bleibt bedienbar.
                // Eigene, ersetzbare Symbole statt RimWorlds Verbots-Hand (2026-09-03, der Finger war als
                // Symbol unklar) - Textures/UI/Icon_DoNotUse(.Off).png.
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
            // Waffensymbol vor dem Namen - die Loadout-Zeilen haben keins, aber bei einer reinen
            // Waffenliste traegt es die Wiedererkennung (2026-09-02).
            if (locked) GUI.color = new Color(1f, 1f, 1f, 0.4f);
            Widgets.ThingIcon(new Rect(iconX, row.y + (RowH - 24f) / 2f, 24f, 24f), w);
            Rect nameRect = new Rect(iconX + 30f, row.y, row.x + NameColX - (iconX + 30f) - (singleSelect ? 30f : 4f), RowH);
            Text.Anchor = TextAnchor.MiddleLeft;
            Widgets.Label(nameRect, w.LabelCap.ToString().Truncate(nameRect.width - 4f));
            Text.Anchor = TextAnchor.UpperLeft;
            GUI.color = Color.white;
            // Der ausfuehrliche Waffen-Tooltip wie im alten Waffen-Reiter (2026-09-02):
            // Name + Beschreibung am Namen, die Wertungs-Herleitung am Balken. TipWanted davor - der
            // Text entstuende sonst pro Zeile und Bild.
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
                    // Knappheits-Zeile aus dem alten Pin-Menue (2026-07-31): was der LEBENDE Vorschau-Plan
                    // schon anderen zugedacht hat, weiss der Kolonie-Bestandsbalken nicht.
                    int reserved = reservedFn != null ? reservedFn(w) : 0;
                    if (reserved > 0)
                        availTip = (availTip ?? "") + "\n" + "CER_PickWeapon_ReservedTipFmt".Translate(reserved.ToString()).ToString();
                }
                LoadoutUtility.DrawAvailabilityBar(availRect, seg, horizontal: true, availTip);
            }

            // KEIN Inhalts-Streifen mehr fuer Waffen-Zeilen (2026-09-03, "contents zeig nur etwas an bei
            // roles und vanilla loadouts, beim rest nicht") - Waffe+Standard-Munition stehen ohnehin in
            // Name und Schaden-Spalte; die Slots braucht nur noch die Kostenrechnung.
            List<LoadoutSlot> slots = WeaponSlots(w);

            float statsX = row.width - statsColW;
            // Produktionskosten der Vorgabe (2026-09-02, "production cost ist ueberall leer"): dieselbe
            // Rechnung wie bei den Loadout-Zeilen, nur aus der Slot-Vorgabe statt einem Loadout-Objekt.
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
                // Einzelauswahl ohne Radio (2026-09-02, nachjustiert): die GANZE Zeile waehlt und
                // schliesst; die aktuelle Wahl traegt die Auswahl-Hinterlegung.
                if (pack == currentSingle) Widgets.DrawHighlightSelected(row);
                // Sperr-Schalter auch fuer ROLLEN und Loadouts (2026-09-04, mit allen Folgen) - identisch
                // zur Waffenzeile: VOR dem
                // Zeilen-Klick gezeichnet, sonst frisst der unsichtbare Zeilenknopf den Klick aufs Schloss.
                // Nur in der Einzelauswahl; die Pack-Checkliste kennt kein Sperren.
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
                // Der NAME schaltet mit (2026-08-28, beim Selbsttest selbst hineingelaufen: ein Klick auf
                // "Shield Kit" tat gar nichts, nur das 24x24-Kaestchen davor reagierte). RimWorlds eigener
                // beschrifteter Haken schaltet ueberall sonst auf der ganzen Beschriftung - hier sah die
                // Beschriftung aus wie ein Ziel und war keins. Bewusst NUR die Namensspalte und nicht die
                // ganze Zeile: rechts davon liegen Inhalts- und Materialzellen mit eigenen Tooltips.
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
                // Der gemeinsame Verfuegbarkeitsbalken statt der nackten Zahl (2026-08-15). Exakt die
                // Bauform, die
                // die Waffenuebersicht fuer dieselbe Frage schon benutzt: GetWeaponInventory -> die vier
                // Toepfe getragen / zugewiesen-und-getragen / zugewiesen-im-Lager / frei im Lager, plus was
                // an der Werkbank haengt. OHNE Pawn-Kontext, obwohl der Picker fuer einen Kolonisten offen
                // ist - der Balken faerbt "frei im Lager" dann gruen statt gelb, und das ist hier die
                // richtige Aussage: die Frage der Zeile ist "gibt es das ueberhaupt", nicht "hat DIESER
                // Kolonist es schon". Gegenprobe zur alten Anzeige: die zeigte stored-demanded, also genau
                // den gruenen Abschnitt - es geht nichts verloren, es kommt nur der Rest dazu.
                // Vorher war das eine Ampelzahl (gruen 2+, gelb 1, rot 0) aus ComputeAvailability.
                ThingDef mainDef = availDefFn(pack);
                Rect availRect = new Rect(afterName + 4f, row.y + (RowH - 20f) / 2f, AvailColW - 12f, 20f);
                if (mainDef != null)
                {
                    var seg = LoadoutUtility.WeaponAvailability(mainDef);
                    // TipWanted davor: BuildWeaponLocationTooltip laeuft ueber alle Karten und alle
                    // Kolonisten, und dieser Balken steht in JEDER Zeile der Liste.
                    LoadoutUtility.DrawAvailabilityBar(availRect, seg, horizontal: true,
                        LoadoutUtility.TipWanted(availRect) ? LoadoutUtility.BuildWeaponLocationTooltip(mainDef) : null);
                }
            }

            List<LoadoutSlot> slots = pack.OwnSlots?.ToList() ?? new List<LoadoutSlot>();
            float contentsW = row.width - statsColW - contentsColX - 8f;
            DrawContentsStrip(new Rect(row.x + contentsColX, row.y, contentsW, RowH), slots);

            // Rechter Block, seit 2026-08-15 in der Reihenfolge Produktionskosten -> Masse/Sperrigkeit.
            float statsX = row.width - statsColW;

            Dictionary<ThingDef, int> prodCosts = LoadoutUtility.GetLoadoutProductionCosts(pack);
            // Ohne Mengen: 200px Spalte, siehe DrawTotalCostLine. Die Zahlen stehen im Tooltip der Zelle.
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
