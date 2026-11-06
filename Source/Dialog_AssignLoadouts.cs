using System;
using System.Collections.Generic;
using System.Linq;
using RimWorld;
using Verse;
using UnityEngine;
using CombatExtended;
using static CEReloaded.LoadoutUtility;

namespace CEReloaded
{
    // Loads its own static Texture2D fields (e.g. MassBulkBarTex) - RimWorld's own asset-loading safety
    // check requires this attribute to guarantee that loading happens on the main thread (2026-08-01,
    // flagged by RimWorld's own startup log: "probably needs a StaticConstructorOnStartup attribute").
    [StaticConstructorOnStartup]
    public class Dialog_AssignLoadouts
    {
        public Dialog_CEReloadedTabs parentWindow;
        // Separate per-view-mode (2026-08-01) - card and compact rows have very different heights, so a
        // single shared raw pixel offset landed on an unrelated part of the roster whenever the player
        // toggled compactView; each mode now keeps its own scroll position instead.
        private Vector2 cardScrollPosition;
        private Vector2 compactScrollPosition;

        private List<Pawn> cachedPawns;
        private Dictionary<Pawn, float> rangedScores = new Dictionary<Pawn, float>();
        private Dictionary<Pawn, float> meleeScores = new Dictionary<Pawn, float>();
        private Dictionary<Pawn, string> traitStrings = new Dictionary<Pawn, string>();
        private Dictionary<Pawn, string> traitTooltipStrings = new Dictionary<Pawn, string>();
        private Dictionary<Pawn, string> capacityTooltips = new Dictionary<Pawn, string>();
        // Per-frame (not per-row) fulfillment cache - one map scan per drawn frame instead of one per row.
        private Dictionary<ThingDef, int> frameOwnedCache;
        // Shared, DECREMENTING view of frameOwnedCache (2026-08-01) - SingleFulfillStatus's non-ammo branch
        // used to just check frameOwnedCache[td] > 0, a flat colony-wide total with no notion of "already
        // claimed by an earlier pawn this same frame", so every pawn independently checking the same shared
        // item (e.g. a satchel-charge sidearm) saw the identical raw count and ALL of them read "Stored"
        // even when stock only covered a few (observed: 7 pawns, 2 in stock, every one showed "in
        // storage"). frameFulfillCache memoizes the per-(pawn,def) RESULT so a row that calls this twice for
        // the same item (DrawFulfillDot: once for the segment color, once for the tooltip text) doesn't
        // double-decrement its own single claim.
        private Dictionary<ThingDef, int> frameRemainingCache;
        private Dictionary<(Pawn, Def, int), FulfillStatus> frameFulfillCache;
        // Die Aufstellung je (Kolonist, Gegenstand, Bedarf, Quelle) - EINMAL fuer Streifen und Tooltip (2026-08-30,
        // siehe RichSegCached). Lebt im selben 0,3-s-Block wie die Caches darueber.
        private Dictionary<(Pawn, Def, int, RichStock), LoadoutUtility.AvailabilitySegments> frameRichSegCache;

        // Trag- und Sperrigkeitsgrenze pro Kolonist, EINMAL pro Bild statt pro Zeile pro Bild (2026-08-14,
        // Befund 4 der ersten Performance-Runde). Beide Werte kommen aus RimWorlds StatWorker.GetValue, und
        // das hat laut Dekompilat KEINEN eigenen Cache: es laeuft jedes Mal ueber die Stat-Teile des Pawns
        // (Kleidung, Gesundheit, Hediffs), im Entwicklermodus zusaetzlich durch IsDisabledFor. Gebraucht
        // werden sie in beiden Ansichten pro sichtbarer Zeile - und ausserdem zweimal je Zeile, weil
        // Masse- und Sperrigkeitsbalken getrennt fragen.
        // Frame-Cache wie seine drei Nachbarn oben, aus demselben Grund: die Werte KOENNEN sich aendern
        // (ein Kolonist zieht etwas an, wird verletzt), aber nicht innerhalb eines Bildes. Geleert wird er
        // an derselben Stelle wie die anderen drei.
        // Der StatDef selbst liegt in LoadoutUtility.CarryBulkStat - vorher stand hier zweimal ein
        // DefDatabase-Nachschlag ueber den STRING "CarryBulk", ebenfalls pro Zeile pro Bild.
        private readonly Dictionary<Pawn, (float mass, float bulk)> frameCapCache = new Dictionary<Pawn, (float, float)>();

        // Was die Munitions-Zelle eines Kolonisten anzeigt - EINMAL pro Bild statt pro Zelle pro Zeile
        // (2026-08-14, Befund 2 der ersten Performance-Runde). Der Aufbau ist der teuerste in der ganzen
        // Zeile: `GetLoadout().Slots` baut bei jedem angeankerten Kolonisten - also praktisch jedem mit
        // einer Zuweisung dieses Mods - die Vereinigung aus eigenen und geerbten Slots neu auf, `.ToList()`
        // kopiert sie, `RelevantAmmoPool` laeuft ueber jede Waffe darin, und der `active`-Filter ist
        // O(Pool x Slots) mit einer Closure. Das steht in BEIDEN Ansichten pro Kolonist.
        // Frame-Cache wie seine Nachbarn: der Zustand kann sich aendern (Munition umstellen, Zweitwaffe
        // anhaengen), aber nicht innerhalb eines Bildes. Geleert an derselben Stelle wie die anderen.
        private class AmmoCellState
        {
            public List<LoadoutSlot> effective;
            public List<ThingDef> pool;
            public List<ThingDef> active;
        }

        private readonly Dictionary<Pawn, AmmoCellState> frameAmmoCache = new Dictionary<Pawn, AmmoCellState>();

        // Das BILD, fuer das die reinen Caches oben gebaut wurden - siehe DoWindowContents. -1, damit der
        // erste Aufruf nach dem Oeffnen sicher neu baut (Time.frameCount ist nie negativ).
        private int frameCacheStamp = -1;
        // Lebensdauer der Zeilen-Caches jenseits eines Bildes - siehe den Kommentar am Neuaufbau in
        // DoWindowContents (2026-08-25).
        private int rowCacheVersion = -1;
        private float rowCacheAt = -99f;
        private const float RowCacheMaxAge = 0.3f;

        // Die drei teuren Ableitungen, die JEDE Zeile braucht - Masse/Sperrigkeit, Haupt- und Zweitwaffe,
        // "haengt ein Pack dran" (2026-08-17, gemessen: "Zeile:kompakt 167 ms/s bei 725 Aufrufen/s",
        // also 230 Mikrosekunden je Zeile).
        // Alle drei greifen auf `Loadout.Slots` zu, und das ist bei jedem angeankerten Kolonisten kein Feld,
        // sondern ein Neuaufbau der Vereinigung aus eigenen und geerbten Slots - PRO ZUGRIFF. Dazu kommen in
        // LoadoutMainAndSide vier LINQ-Zwischenlisten und in GetExtraParents eine weitere. Das lief bisher
        // dreimal pro Zeile pro DURCHGANG, und RimWorld schickt mindestens zwei Durchgaenge je Bild.
        // Genau dieselbe Begruendung wie bei frameAmmoCache/frameCapCache daneben - und dieselbe Grenze: die
        // Werte KOENNEN sich aendern (Vorlage tauschen, Pack anhaengen), aber nicht innerhalb eines Bildes.
        // Beide Ansichten lesen daraus, sonst waere es wieder nur die halbe Fundstelle.
        private struct RowInfo
        {
            public float mass, bulk;
            public ThingDef mainW, sideW;
            public bool hasPacks;
            // Die Zellen daneben - dieselbe Begruendung, nur eine Ebene tiefer: GetPawnPacks/GetPawnSidearms
            // laufen ueber MultiParentUtility.GetExtraParents (Parsen + drei LINQ-Listen), CarriedPackSlots
            // zusaetzlich ueber die flache Union einer Rolle. Das steht dreimal in derselben Zeile
            // (Pack-Zelle, ihr Rollen-Zweig, Zweitwaffen-Zelle) und lief pro Durchgang.
            // NUR LESEN - diese Listen sind geteilt. Wer sie aendern will, holt sich seine eigene beim
            // Werkzeug (die Klick-Wege tun das ohnehin, sie laufen ausserhalb des Zeichnens).
            public List<Loadout> packs;
            public List<LoadoutSlot> packSlots;
            public Dictionary<Def, int> packNeeded;
            public List<Loadout> sidearms;
            public List<ThingDef> sidearmIcons;
            // Das angezeigte Loadout (Vorlage/Rolle, nicht der Anker) und die Posten, die die WAFFEN-Zelle
            // vertritt (2026-08-30): bei einem CE-eigenen Loadout mit Gewehr, Gladius,
            // Mahlzeiten und Medizin zeigte die Zelle nur das Gewehr - alles andere hatte keine Zelle.
            public Loadout display;
            public List<WeaponCellItem> weaponCellItems;
        }

        private struct WeaponCellItem { public Def def; public RichStock kind; public int need; }

        private readonly Dictionary<Pawn, RowInfo> frameRowCache = new Dictionary<Pawn, RowInfo>();

        private RowInfo GetRowInfo(Pawn pawn)
        {
            if (pawn != null && frameRowCache.TryGetValue(pawn, out RowInfo hit)) return hit;
            RowInfo info = new RowInfo();
            Loadout assigned = pawn?.GetLoadout();
            Loadout display = AmmoAssignmentUtility.DisplayLoadout(assigned);
            info.hasPacks = MultiParentUtility.GetExtraParents(assigned).Any(EquipmentPackUtility.IsEquipmentPackLoadout);
            // Der ANKER fuer Masse/Sperrigkeit, die WAFFEN-VORLAGE fuer die Icons - genau die Aufteilung, die
            // vorher an beiden Aufrufstellen stand (siehe den Kommentar am Massebalken der kompakten Zeile).
            if (assigned != null && !assigned.defaultLoadout)
                LoadoutUtility.GetLoadoutMassBulk(assigned, out info.mass, out info.bulk);
            if (display != null && !display.defaultLoadout)
                LoadoutUtility.LoadoutMainAndSide(display, out info.mainW, out info.sideW);
            info.packs = AmmoAssignmentUtility.GetPawnPacks(pawn) ?? new List<Loadout>();
            info.packSlots = AmmoAssignmentUtility.CarriedPackSlots(pawn) ?? new List<LoadoutSlot>();
            info.packNeeded = NeededCountsFrom(info.packSlots);
            info.sidearms = SidearmAssignmentUtility.GetPawnSidearms(pawn) ?? new List<Loadout>();
            info.sidearmIcons = info.sidearms.Select(RepresentativeIcon).ToList();
            info.display = display;
            info.weaponCellItems = BuildWeaponCellItems(info, display);
            if (pawn != null) frameRowCache[pawn] = info;
            return info;
        }

        // Alles, was die Waffen-Zelle vertritt: die Hauptwaffe plus jeden EIGENEN Slot des angezeigten Loadouts,
        // den keine Nachbarzelle zeigt - Munition (auch 'Any ammo for X') gehoert der Munitionszelle, Pack-Inhalt
        // der Pack-Zelle, Zweitwaffen der Zweitwaffen-Zelle. Bei unseren eigenen Vorlagen (eine Waffe + Munition)
        // bleibt genau die Hauptwaffe uebrig, die Zelle sieht also aus wie bisher; bei einem CE-eigenen Loadout
        // kommen Gladius, Mahlzeiten und Medizin dazu. Einmal je 0,3 s (RowInfo), nicht je Durchgang.
        private static List<WeaponCellItem> BuildWeaponCellItems(RowInfo info, Loadout display)
        {
            var list = new List<WeaponCellItem>();
            if (info.mainW != null) list.Add(new WeaponCellItem { def = info.mainW, kind = RichStock.Weapon, need = 1 });
            if (display == null || display.defaultLoadout || display.OwnSlots == null) return list;
            foreach (LoadoutSlot s in display.OwnSlots)
            {
                Def d = (Def)s.thingDef ?? s.genericDef;
                if (d == null || d == info.mainW) continue;
                if (LoadoutUtility.IsAmmoSlot(s)) continue;
                if (info.packSlots != null && info.packSlots.Any(p => ((Def)p.thingDef ?? p.genericDef) == d)) continue;
                if (s.thingDef != null && info.sidearmIcons != null && info.sidearmIcons.Contains(s.thingDef)) continue;
                bool dup = false;
                for (int i = 0; i < list.Count; i++) if (list[i].def == d) { dup = true; break; }
                if (dup) continue;
                bool weaponish = s.thingDef != null && (s.thingDef.IsWeapon || LoadoutUtility.IsShieldApparel(s.thingDef));
                list.Add(new WeaponCellItem { def = d, kind = weaponish ? RichStock.Weapon : RichStock.Item, need = Mathf.Max(1, s.count) });
            }
            return list;
        }

        // Vorformatierte Beschriftungen je Kolonist (2026-08-25, perf-cell-row-next): Faehigkeitsbalken,
        // Masse/Sperrigkeit und die Aufschluesselung bauten ihre Texte in JEDEM Durchgang neu (Translate mit
        // Argumenten, F1-Formatierung, string.Format, dazu fuenf Skill-/Kapazitaetsabfragen) - gemessen 45 bis
        // 76 ms/s je Abschnitt, fuer Werte, die sich zwischen zwei Bildern nie aendern. Lebensdauer wie
        // frameRowCache (Versionszaehler + 0,3 s), geleert an derselben Stelle.
        private struct RowLabels { public string rangedCard, meleeCard, rangedCompact, meleeCompact, mass, bulk, b1, b2; }
        private readonly Dictionary<Pawn, RowLabels> frameLabelCache = new Dictionary<Pawn, RowLabels>();

        private RowLabels GetRowLabels(Pawn pawn)
        {
            if (frameLabelCache.TryGetValue(pawn, out RowLabels hit)) return hit;
            RowInfo info = GetRowInfo(pawn);
            GetCapacities(pawn, out float capMass, out float capBulk);
            float rScore = rangedScores.TryGetValue(pawn, out float rs) ? rs : 0f;
            float mScore = meleeScores.TryGetValue(pawn, out float ms) ? ms : 0f;
            float shootingSkill = pawn.skills?.GetSkill(SkillDefOf.Shooting)?.Level ?? 0;
            float meleeSkill = pawn.skills?.GetSkill(SkillDefOf.Melee)?.Level ?? 0;
            float sight = pawn.health.capacities.GetLevel(PawnCapacityDefOf.Sight);
            float manipulation = pawn.health.capacities.GetLevel(PawnCapacityDefOf.Manipulation);
            float moving = pawn.health.capacities.GetLevel(PawnCapacityDefOf.Moving);
            RowLabels l = new RowLabels
            {
                rangedCard = LoadoutUtility.ScoreBarLabel(rScore, "CER_AssignLoadouts_RangedLabel".Translate()),
                meleeCard = LoadoutUtility.ScoreBarLabel(mScore, "CER_AssignLoadouts_MeleeLabel".Translate()),
                rangedCompact = LoadoutUtility.ScoreBarLabel(rScore, "CER_AssignLoadouts_RangedAbbrev".Translate()),
                meleeCompact = LoadoutUtility.ScoreBarLabel(mScore, "CER_AssignLoadouts_MeleeAbbrev".Translate()),
                mass = "CER_AssignLoadouts_MassBarFmt".Translate(info.mass.ToString("F1"), capMass.ToString("F1")).ToString(),
                bulk = string.Format("{0:F1}/{1:F1}", info.bulk, capBulk),
                b1 = "CER_AssignLoadouts_BreakdownRangedFmt".Translate(shootingSkill.ToString(), Mathf.RoundToInt(sight * 100f).ToString(), Mathf.RoundToInt(manipulation * 100f).ToString()).ToString(),
                b2 = "CER_AssignLoadouts_BreakdownMeleeFmt".Translate(meleeSkill.ToString(), Mathf.RoundToInt(sight * 100f).ToString(), Mathf.RoundToInt(moving * 100f).ToString(), Mathf.RoundToInt(manipulation * 100f).ToString()).ToString(),
            };
            frameLabelCache[pawn] = l;
            return l;
        }

        private AmmoCellState GetAmmoCellState(Pawn pawn)
        {
            if (pawn != null && frameAmmoCache.TryGetValue(pawn, out AmmoCellState hit)) return hit;
            List<LoadoutSlot> effective = pawn?.GetLoadout()?.Slots?.ToList() ?? new List<LoadoutSlot>();
            List<ThingDef> pool = Dialog_ChangeAmmo.RelevantAmmoPool(effective);
            List<ThingDef> active = pool.Where(d => effective.Any(s => s.thingDef == d)).ToList();
            // Ein generischer Eintrag ("Any ammo for X") IST eine gesetzte Munitionswahl (2026-08-14,
            // im Test aufgefallen): vorher zaehlte hier nur KONKRETE Munition, ein Kolonist auf
            // "Any ammo" fiel damit in den Warnzweig und die Zelle behauptete, er habe nichts zu
            // schiessen - genau das Gegenteil dessen, was der Spieler gerade eingestellt hatte.
            // Aufgeloest auf die konkrete Vorgabesorte (ResolveGenericAmmo), nicht bloss "irgendwas
            // gesetzt": dieselbe Aufloesung, mit der der Mod auch die Produktion plant
            // (MergeResolvedGenericAmmoDemand). Dadurch stimmen Symbol, Zaehler und Verfuegbarkeits-
            // Aufschluesselung dieser Zelle mit dem ueberein, was wirklich beschafft wird.
            foreach (LoadoutSlot s in effective)
            {
                if (!LoadoutUtility.IsGenericAmmoDef(s.genericDef)) continue;
                ThingDef resolved = LoadoutUtility.ResolveGenericAmmo(s.genericDef as LoadoutGenericDef);
                if (resolved != null && !active.Contains(resolved)) active.Add(resolved);
            }
            AmmoCellState state = new AmmoCellState { effective = effective, pool = pool, active = active };
            if (pawn != null) frameAmmoCache[pawn] = state;
            return state;
        }

        // Wie viele Schuss dieser Sorte dieser Kolonist eingeplant hat. Konkrete Slots werden summiert (zwei
        // Slots derselben Sorte sind zusammen der Bedarf); findet sich keiner, kommt die Zahl aus dem
        // generischen Slot, der auf genau diese Sorte aufloest ("Any ammo for X"). Genau die Aufloesung, mit
        // der GetAmmoCellState die Sorte ueberhaupt erst in die Liste gesetzt hat - ohne diesen zweiten Weg
        // gaebe es fuer sie keine Zahl.
        private static int AmmoCountFor(List<LoadoutSlot> effective, ThingDef d)
        {
            if (effective == null || d == null) return 0;
            int direct = effective.Where(s => s.thingDef == d).Sum(s => s.count);
            if (direct > 0) return direct;
            foreach (LoadoutSlot s in effective)
            {
                if (!LoadoutUtility.IsGenericAmmoDef(s.genericDef)) continue;
                if (LoadoutUtility.ResolveGenericAmmo(s.genericDef as LoadoutGenericDef) == d) return s.count;
            }
            return 0;
        }

        private void GetCapacities(Pawn pawn, out float capMass, out float capBulk)
        {
            if (pawn != null && frameCapCache.TryGetValue(pawn, out var hit)) { capMass = hit.mass; capBulk = hit.bulk; return; }
            // CEs CarryWeight, nicht Vanillas CarryingCapacity - siehe LoadoutUtility.CarryWeightOf.
            capMass = LoadoutUtility.CarryWeightOf(pawn);
            StatDef bulkStat = LoadoutUtility.CarryBulkStat;
            capBulk = bulkStat != null && pawn != null ? pawn.GetStatValue(bulkStat) : 20f;
            if (capMass <= 0f) capMass = 35f;
            if (capBulk <= 0f) capBulk = 20f;
            if (pawn != null) frameCapCache[pawn] = (capMass, capBulk);
        }

        // 330, not the old 360 - at the tabs window's normal width (~1032 usable), 360 only fit 2 cards per
        // row; 330 reliably fits 3 (the "3 per row" the card grid is meant to read as) while still leaving
        // the Mass/Bulk bars and the Ammo/Pack/Sidearm cell row enough width to stay readable.
        private const float CardWidth = 330f;
        // Compact view's Weapon column start - right after the Melee score bar (326 + 80 wide + 6 gap),
        // shared between DrawCompactHeader and DrawPawnRowCompact so they can never drift apart again.
        // 412 -> 398 (2026-08-13): nach dem Speichern-Knopf liessen sich ein paar Pixel
        // sparen - zwischen dem Speichern-Knopf (endet 214) und dem ersten Skill-Balken lagen 26px Luft.
        // Die beiden Balken ruecken um 14 nach links, die Waffenspalte gewinnt genau diese 14px.
        // 398 -> 372 (2026-08-31): in der kompakten Ansicht liefen zu lange Waffennamen ueber den Knopf
        // hinaus - die Kolonisten-Spalte ist deshalb etwas schmaler. Die Namensspalte gibt 26 Punkte ab (130 -> 104),
        // Rollenknopf und die beiden Faehigkeitsbalken ruecken um genau dieselben 26 mit - rechts vom
        // Waffenknopf bleibt jede Zelle, wo sie war (nichts sonst verschieben).
        private const float WeaponColX = 372f;
        // Shrunk by 21 - the old separate "Equipped: <weapon>" detail row is gone (see DrawWeaponStatusDot),
        // replaced by a corner dot on the loadout button itself, same as Ammo/Pack/Sidearm's own dots.
        private static float CardHeight => 205f;
        private const float Margin = 10f;

        private static readonly Texture2D MassBulkBarTex = SolidColorMaterials.NewSolidColorTexture(new Color(0f, 0.8f, 0.8f));
        // Ueber der Kapazitaet (2026-08-12) - dunkelrot statt tuerkis, damit der Balken schon aus dem
        // Augenwinkel anders aussieht und man nicht erst die Zahlen lesen muss.
        private static readonly Texture2D OverCapBarTex = SolidColorMaterials.NewSolidColorTexture(new Color(0.75f, 0.18f, 0.18f));

        // Masse/Sperrigkeit-Beschriftungen der Kolonisten-Karte, einmal uebersetzt und gemessen statt pro
        // Karte pro Bild (2026-08-26: "Mas"/"Sper" - die 32f/30f-Rects waren fuer die
        // deutschen Woerter zu schmal und Widgets.Label clippt stumpf). Sprache aendert sich nur mit
        // Neustart - RimWorlds Umschalten im Lauf ist ohnehin defekt.
        private static string cardMassCaption, cardBulkCaption;
        private static float cardMassCaptionW, cardBulkCaptionW;

        private AssignLoadoutFilter loadoutFilter = AssignLoadoutFilter.AvailableMainWeapon;
        // Always on now (2026-07-30) - these used to be player-facing checkboxes ("Produce missing weapons"/
        // "...main weapons"/"...sidearms"), removed: the end-of-session production check
        // (LoadoutUtility.CheckAndOpenMissingProductionPopup)
        // covers this need automatically now regardless, so there is no longer a reason to let a player turn
        // OFF the immediate post-apply check - always including everything is simply the safer default, and
        // every popup this still feeds is still fully reviewable/cancellable before anything is queued.
        internal const bool produceMissing = true;
        internal const bool produceMainWeapons = true;
        internal const bool produceSidearms = true;
        // Drei Werte statt zwei seit 2026-08-19 - siehe RoleAssignMode.
        internal RoleAssignMode roleMode = RoleAssignMode.Mixed;
        // Der Rollen-Schalter, wie er VOR einem Arm-my-Colony-Lauf stand (2026-09-04: nach Arm my Colony
        // liessen sich ueberall nur noch Rollen waehlen).
        //
        // Ursache: die Wurzelfrage des Assistenten ("mit Rollen arbeiten?") wird als roleModeOverride
        // in genau dieses Feld geschrieben - und DoWindowContents spiegelt das Feld in jedem Bild in
        // den Spielstand (ui.asRoleMode). Damit wurde aus einer Antwort FUER DIESEN LAUF die dauerhafte
        // Einstellung des Spielers: das Waffen-Auswahlfenster (Dialog_SquadSetup.OpenPinMenu) baut seine
        // Waffenliste aus demselben Schalter und zeigte danach ueberall nur noch Rollen.
        // Solange dieses Feld gesetzt ist, laeuft ein Lauf mit erzwungenem Modus: die Spiegelung in den
        // Spielstand pausiert, und am Ende des Laufs (FinishArmReport - Apply wie Abbruch gehen dort
        // durch) wird der alte Wert zurueckgestellt.
        private RoleAssignMode? armRoleModeRestore;
        private bool sortByMelee = false;   // pawn grid sort: ranged score (default) vs melee score
        private bool compactView = false;   // full cards vs one slim table row per colonist
        private string pawnSearch = "";

        // Compact view's own column-header sort (2026-08-01), mirroring the currentSortMode/sortDescending +
        // ColumnHeader pattern already used by Dialog_SupplyLoadouts/Dialog_GenerateAmmoBills. Only "Name" is
        // offered - Ranged/Melee already have their own dedicated sort radios above the grid (SortRadio), and
        // a Weapon/Ammo/Pack/Sidearm fulfillment-status sort was deliberately left out: SingleFulfillStatus's
        // "Stored" branch DECREMENTS a shared per-frame stock pool the first time each (pawn, def) pair is
        // resolved (see frameRemainingCache), so which pawn "claims" limited stock depends on resolution
        // order - sorting by that same status would make the sort itself decide the claim order, which is not
        // a clean, well-defined ordering. Card view is intentionally NOT sorted by this - it has no column
        // header row to click (see DoWindowContents' compactView-only branch below), and its own scroll order
        // already comes from the same rank/score sort every view has always used.
        private enum SortMode { Default, Name, Ranged, Melee }
        private SortMode currentSortMode = SortMode.Default;
        private bool sortDescending = false;

        // Same toggle-direction-on-repeat-click convention as the reference dialogs; unlike theirs there is no
        // persistent entry list to re-sort here (compact rows are re-sorted fresh from cachedPawns every frame
        // in DoWindowContents), so flipping the fields is the whole job.
        private void SetSort(SortMode mode)
        {
            if (currentSortMode == mode) sortDescending = !sortDescending;
            else { currentSortMode = mode; sortDescending = false; }
        }

        // Lets an external jump-in (e.g. the native "Show in loadout editor" FloatMenu entry) pre-fill the
        // pawn search box, so the tab opens already filtered to one specific colonist instead of the whole grid.
        public void SetPawnSearch(string s) => pawnSearch = s ?? "";

        // Drag-and-drop loadout swap between two pawns (card/compact grid) - dragPawn != null IS "a drag is in
        // progress". Same raw Input-polling mechanism as Dialog_SquadSetup's (confirmed working there): IMGUI
        // EventType.MouseDown/MouseUp checks did not reliably fire for this kind of press-drag-release here either.
        private Pawn dragPawn;

        // Set only for the duration of an Arm-my-Colony run: the running tally that the final overview window shows.
        private ArmReport armReport;

        public Dialog_AssignLoadouts()
        {
            // Seed from the per-save UI state (itself seeded from mod-settings for a fresh colony), so a player's choices
            // persist across window opens and reloads.
            CERUiState ui = RoleUtility.Ui();
            loadoutFilter = ui.asLoadoutFilter;
            roleMode = ui.asRoleMode;
            sortByMelee = ui.asSortMelee;
            compactView = ui.asCompact;
        }

        // --- Accessors for the auto-assign section of Dialog_SquadSetup ---
        internal List<Pawn> CachedPawns { get { if (cachedPawns == null) RefreshPawns(); return cachedPawns; } }
        internal float RangedScoreOf(Pawn p) => rangedScores.TryGetValue(p, out float v) ? v : 0f;
        internal float MeleeScoreOf(Pawn p) => meleeScores.TryGetValue(p, out float v) ? v : 0f;

        // Splits the weapon shortfall of a set of assigned loadouts into MAIN weapons vs. SECONDARY (sidearms +
        // grenades). Owned stock is counted once against the combined need; a def that is a main weapon in ANY loadout
        // is reported in the main bucket (crafting it also covers its sidearm use). Utility items are ignored.
        // Static (no instance state involved) so the consolidated Readiness alert can build a Supply request
        // without needing a live Dialog_AssignLoadouts instance.
        internal static void ComputeMissingSplit(IEnumerable<Loadout> loadouts, IEnumerable<Loadout> sidearmLoadouts,
            out Dictionary<ThingDef, int> mainMissing, out Dictionary<ThingDef, int> secMissing, out Dictionary<ThingDef, int> shieldMissing,
            IEnumerable<LoadoutSlot> extraSidearmSlots = null)
            => ComputeMissingSplit(loadouts, sidearmLoadouts, BuildOwnedWeaponCounts(), out mainMissing, out secMissing, out shieldMissing, extraSidearmSlots);

        // Split each loadout's weapon needs into main-weapon, sidearm and SHIELD shortfalls, given an "available"
        // pool. Auto-assign passes the whole owned pool (it redistributes everything); a single manual assignment
        // passes only free storage + that pawn's own weapons, so a weapon another colonist is already holding
        // doesn't hide the shortfall. Shields are apparel, not weapons, so they're tracked in their own bucket
        // (plan section 6) rather than folded into secondary weapons.
        //
        // sidearmLoadouts (2026-08-01) - THE single shared "is this covered" method now, used by every screen
        // that needs it (Supply popup via OpenProducePopup, the standalone Readiness alert via
        // BuildSupplyRequestForPawns, and Dialog_SquadSetup's own assign-preview badges via
        // BuildPlanWideShortfalls) instead of each one hand-rolling its own demand-aggregation - the SAME
        // "stock checked without subtracting what other consumers already claimed" bug had been fixed
        // independently in three different places before landing here (goal: one availability logic).
        // Sidearm-attached loadouts are folded into the SAME needs/shieldNeeds
        // totals as `loadouts` (so stock is subtracted once across every consumer of a shared def) but
        // deliberately NEVER run through AnalyzeLoadout - a sidearm loadout's own gun is what a pawn carries
        // AS a sidearm, not their "main weapon" pick, so it must never populate mainDefs on its own (that
        // would misclassify an otherwise-unrelated sidearm-only def as a "main weapons" shortfall instead of
        // "sidearms"). Optional/nullable so existing single-list callers don't need to change.
        internal static void ComputeMissingSplit(IEnumerable<Loadout> loadouts, IEnumerable<Loadout> sidearmLoadouts, Dictionary<ThingDef, int> owned,
            out Dictionary<ThingDef, int> mainMissing, out Dictionary<ThingDef, int> secMissing, out Dictionary<ThingDef, int> shieldMissing,
            IEnumerable<LoadoutSlot> extraSidearmSlots = null)
        {
            Dictionary<ThingDef, int> needs = new Dictionary<ThingDef, int>();
            Dictionary<ThingDef, int> shieldNeeds = new Dictionary<ThingDef, int>();
            HashSet<ThingDef> mainDefs = new HashSet<ThingDef>();
            void FoldSlot(LoadoutSlot slot)
            {
                ThingDef w = slot.thingDef;
                if (w == null) return;
                int n = Mathf.Max(1, slot.count);
                if (LoadoutUtility.IsShieldApparel(w)) shieldNeeds[w] = (shieldNeeds.TryGetValue(w, out int sc) ? sc : 0) + n;
                else if (w.IsWeapon && !LoadoutUtility.IsUtilityWeapon(w)) needs[w] = (needs.TryGetValue(w, out int c) ? c : 0) + n;
            }
            void FoldSlots(Loadout lo)
            {
                if (lo == null || lo.Slots == null) return;
                foreach (LoadoutSlot slot in lo.Slots) FoldSlot(slot);
            }
            foreach (Loadout lo in loadouts)
            {
                if (lo == null || lo.Slots == null) continue;
                if (AnalyzeLoadout(lo, out ThingDef m, out _, out _, out _)) mainDefs.Add(m);
                FoldSlots(lo);
            }
            if (sidearmLoadouts != null)
                foreach (Loadout lo in sidearmLoadouts) FoldSlots(lo);
            // Virtuelle Zweitwaffen/Granaten-Picks (Lazy-Umbau 2026-09-01): beim Planen existiert noch kein
            // Vorlagen-Objekt, der Bedarf ihrer Slots (Waffe selbst; Schild kann hier nie vorkommen) muss
            // aber in DIESELBEN Toepfe, sonst fehlt er in jeder Fehlmengen-Anzeige.
            if (extraSidearmSlots != null)
                foreach (LoadoutSlot slot in extraSidearmSlots) FoldSlot(slot);

            mainMissing = new Dictionary<ThingDef, int>();
            secMissing = new Dictionary<ThingDef, int>();
            shieldMissing = new Dictionary<ThingDef, int>();
            foreach (var kv in needs)
            {
                int have = owned.TryGetValue(kv.Key, out int o) ? o : 0;
                int miss = kv.Value - have;
                if (miss <= 0) continue;
                if (mainDefs.Contains(kv.Key)) mainMissing[kv.Key] = miss;
                else secMissing[kv.Key] = miss;
            }
            foreach (var kv in shieldNeeds)
            {
                int have = owned.TryGetValue(kv.Key, out int o) ? o : 0;
                int miss = kv.Value - have;
                if (miss > 0) shieldMissing[kv.Key] = miss;
            }
        }

        // Weapon/shield def -> names of the pawns whose (planned or current) loadout contains it, for the supply
        // popup's Assig. column. Covers mains, sidearms, grenades and shields alike.
        // internal seit 2026-08-18: das ist die EINE Antwort auf "wer hat diese Waffe zugewiesen", und der
        // Munitions-Reiter braucht sie auch. Vorher hat er dort ScanMissingAssignedWeapons benutzt - und das
        // beantwortet eine ANDERE Frage: wem sie noch FEHLT. Wer sie laengst traegt, steht dort nie drin,
        // und genau deshalb hat der Fix vom selben Tag nur auf einem von zwei Wegen gegriffen.
        internal static Dictionary<ThingDef, List<string>> BuildWeaponUsedBy(IEnumerable<(Loadout loadout, string name)> pairs)
        {
            var map = new Dictionary<ThingDef, List<string>>();
            foreach (var (lo, name) in pairs)
            {
                if (lo == null || lo.defaultLoadout || lo.Slots == null) continue;
                foreach (LoadoutSlot slot in lo.Slots)
                {
                    ThingDef w = slot.thingDef;
                    if (w == null) continue;
                    if (!LoadoutUtility.IsShieldApparel(w) && (!w.IsWeapon || LoadoutUtility.IsUtilityWeapon(w))) continue;
                    if (!map.TryGetValue(w, out List<string> l)) map[w] = l = new List<string>();
                    if (!l.Contains(name)) l.Add(name);
                }
            }
            return map;
        }

        // Builds a full Supply request (weapons + shields + ammo, all filtered to researched/producible-relevant
        // entries) for an arbitrary set of pawns' CURRENT loadouts - the entry point the consolidated Readiness
        // alert (and anything else outside the Assign tab's own flow) uses to open Dialog_SupplyLoadouts without
        // needing a live Dialog_AssignLoadouts instance. Returns null if there's nothing to supply.
        internal static Dialog_SupplyLoadouts.Request BuildSupplyRequestForPawns(IEnumerable<Pawn> pawns)
        {
            List<Pawn> pawnList = pawns.Where(p => p != null).ToList();

            // Der Fehlbestand an Waffen und Schilden kommt aus WeaponShortfallsByPawn, NICHT mehr aus
            // ComputeMissingSplit gegen den kolonieweiten Bestand (2026-08-17):
            // dieser Einstieg wird ausschliesslich aus der Bereitschaftsanzeige heraus benutzt, und die
            // meldet reihum - "diesem Kolonisten ist nichts mehr uebrig geblieben". Der kolonieweite
            // Bestand zaehlt dagegen jede Waffe mit, die ein anderer schon traegt, und sagte deshalb
            // "es fehlt nichts" zu genau der Luecke, wegen der das Fenster aufgemacht wurde. Siehe die
            // Kopfzeile ueber WeaponShortfallsByPawn.
            //
            // Die EINSORTIERUNG (Hauptwaffe vs. Zweitwaffe) haengt weiter allein an den Vorlagen der
            // Kolonisten: AnalyzeLoadout kuert pro Loadout genau eine Hauptwaffe, und eine Zweitwaffe darf
            // um diesen Platz nicht konkurrieren (2026-08-13, sidearms-appear-in-supply: "je nach Weg
            // landen dieselben Waffen einmal unter 'Main weapons' und einmal unter 'Sidearms & grenades'").
            List<Loadout> mainLoadouts = pawnList
                .Select(p => AmmoAssignmentUtility.DisplayLoadout(p.GetLoadout()))
                .Where(l => l != null && !l.defaultLoadout).ToList();
            HashSet<ThingDef> mainDefs = new HashSet<ThingDef>();
            foreach (Loadout lo in mainLoadouts)
                if (AnalyzeLoadout(lo, out ThingDef m, out _, out _, out _)) mainDefs.Add(m);
            var mainMissing = new Dictionary<ThingDef, int>();
            var secMissing = new Dictionary<ThingDef, int>();
            var shieldMissing = new Dictionary<ThingDef, int>();
            Dictionary<Pawn, Dictionary<ThingDef, int>> shortfalls = WeaponShortfallsByPawn();
            foreach (Pawn p in pawnList)
            {
                if (!shortfalls.TryGetValue(p, out Dictionary<ThingDef, int> miss)) continue;
                foreach (var kv in miss)
                {
                    Dictionary<ThingDef, int> bucket = LoadoutUtility.IsShieldApparel(kv.Key) ? shieldMissing
                        : (mainDefs.Contains(kv.Key) ? mainMissing : secMissing);
                    bucket[kv.Key] = (bucket.TryGetValue(kv.Key, out int c) ? c : 0) + kv.Value;
                }
            }

            LoadoutUtility.ComputeAmmoNeedForAssignedPawns(pawnList, out var generics, out var specifics, out var assignedUsedBy);
            Dictionary<ThingDef, List<string>> weaponAssignedUsedBy = BuildWeaponUsedBy(pawnList.Select(p => (p.GetLoadout(), p.LabelShortCap)));
            // Circle indicator (used-in-loadout) is always computed too, independent of the (nonexistent, here)
            // scope toggle - see LoadoutUtility.CheckAndOpenMissingProductionPopup's own matching comment for
            // why the two indicators must never be scope-dependent (2026-08-02).
            LoadoutUtility.ComputeMissingForCreatedLoadouts(out _, out _, out Dictionary<ThingDef, List<string>> weaponTemplateUsedBy,
                out _, out var templatePackUsedBy, out _, out _, out Dictionary<Def, List<string>> templateUsedBy, out _);
            foreach (var kv in templatePackUsedBy) weaponTemplateUsedBy[kv.Key] = kv.Value;
            // Quantity-aware, not the old binary "any bill exists at all" gate (2026-08-01 fix, mirrors
            // LoadoutUtility.CheckAndOpenMissingProductionPopup's own same-shape fix from 2026-07-31, which
            // this sibling call site never got) - the old !WeaponHasActiveBill(kv.Key) suppressed the WHOLE
            // shortfall the instant ANY bill for that ammo existed anywhere, even a leftover 1-unit bill from
            // before more colonists got assigned it, silently under-proposing real demand on re-trigger.
            // + CarriedStockCount (2026-08-12): der Bedarf hier ist der JEDES
            // zugewiesenen Kolonisten, der Bestand war aber nur Lager + laufende Auftraege. Was die Leute im
            // Rucksack haben, zaehlte fuer niemanden - und genau das ist bei Munition der Normalfall, sobald
            // einmal ausgeruestet wurde. Ergebnis war ein Vorschlag, Munition herzustellen, die die Kolonie
            // laengst hat, nur eben am Mann statt im Regal. Siehe LoadoutUtility.CarriedStockCount.
            var missingSpecifics = specifics.Where(kv => LoadoutUtility.IsAmmoResearched(kv.Key)
                && LoadoutUtility.AmmoOnHand(kv.Key) + LoadoutUtility.WeaponQueuedCount(kv.Key) < kv.Value)
                .ToDictionary(kv => kv.Key, kv => kv.Value);
            var relevantGenerics = generics.Where(kv =>
                LoadoutUtility.GetAmmoTypesFor(LoadoutUtility.GetWeaponForGenericAmmo(kv.Key)).Any(LoadoutUtility.IsAmmoResearched))
                .ToDictionary(kv => kv.Key, kv => kv.Value);

            // Ab hier DASSELBE Nachbereiten wie im Uebernehmen-Weg (2026-08-13, im Test: ueber die
            // Bereitschaftsanzeige geoeffnet fehlten die beiden 23x75mmR-Zeilen und die Abschnitte
            // Pack items / Not producible).
            // Der Grund war kein Rechenfehler, sondern eine halbe Anfrage: dieser Einstieg hat packItems nie
            // gefuellt, die Dauerauftrag-Ergaenzung nie laufen lassen und die Obsolet-Pruefung nie angehaengt.
            // Drei Fenster, dieselbe Frage, drei verschieden vollstaendige Antworten.
            LoadoutUtility.ScanMissingAssignedPackItems(
                pawnList.Select(p => (p, AmmoAssignmentUtility.GetPawnPacks(p))), out var missingPackItems, out var packUsedBy,
                out var packDemandDefs);
            // Kein IsProducible-Filter (2026-08-13) - gleiche Begruendung wie
            // bei den Waffen einen Zweig weiter unten: eine nicht herstellbare Zeile stellt das Fenster
            // selbst richtig dar, stilles Weglassen liest sich dagegen als "nichts fehlt". Genau daran ist
            // die Medizin-Zeile verschwunden, obwohl der Tooltip daneben "Still missing: 42" sagte.
            var produciblePackItems = new Dictionary<ThingDef, int>(missingPackItems);
            foreach (var kv in packUsedBy) weaponAssignedUsedBy[kv.Key] = kv.Value;

            foreach (var kv in LoadoutUtility.ConsumablesNeedingMaintenance(specifics))
                if (!missingSpecifics.ContainsKey(kv.Key)) missingSpecifics[kv.Key] = kv.Value;
            foreach (var kv in LoadoutUtility.ConsumablesNeedingMaintenance(missingPackItems, requireConsumable: true))
                if (!produciblePackItems.ContainsKey(kv.Key)) produciblePackItems[kv.Key] = kv.Value;
            foreach (var kv in LoadoutUtility.ConsumablesNeedingMaintenance(mainMissing, requireConsumable: true))
                if (!mainMissing.ContainsKey(kv.Key)) mainMissing[kv.Key] = kv.Value;
            foreach (var kv in LoadoutUtility.ConsumablesNeedingMaintenance(secMissing, requireConsumable: true))
                if (!secMissing.ContainsKey(kv.Key)) secMissing[kv.Key] = kv.Value;

            var req = new Dialog_SupplyLoadouts.Request
            {
                mainWeapons = mainMissing,
                secondaryWeapons = secMissing,
                shields = shieldMissing,
                packItems = produciblePackItems,
                packItemDemandDefs = packDemandDefs,
                genericAmmo = relevantGenerics,
                specificAmmo = missingSpecifics,
                assignedUsedBy = assignedUsedBy,
                templateUsedBy = templateUsedBy,
                weaponAssignedUsedBy = weaponAssignedUsedBy,
                weaponTemplateUsedBy = weaponTemplateUsedBy
            };
            LoadoutUtility.AttachObsoleteCleanup(req);

            int total = mainMissing.Count + secMissing.Count + shieldMissing.Count + produciblePackItems.Count
                + relevantGenerics.Count + missingSpecifics.Count
                + req.obsoleteAmmoCandidates.Count + req.obsoleteWeaponCandidates.Count;
            return total == 0 ? null : req;
        }

        // Kapazitaets-Balken (Masse oder Sperrigkeit) mit sichtbarer Ueberlast-Warnung. EINE Funktion fuer
        // Kartenansicht und kompakte Zeile, damit die beiden nicht auseinanderlaufen.
        //
        // 2026-08-12, Grundsatz: erlauben, aber eine gut sichtbare Warnung einbauen.
        // Vorher war ein zu schweres Loadout nur an zwei Zahlen im Balken zu
        // erkennen (25.5/20.0), und der Balken selbst sah wegen Clamp01 aus wie ein sauber gefuellter.
        //
        // Warum der Text NICHT "ueberladen" sagt: der Kolonist wird es nie. CEs Abhol-Job prueft vor jedem
        // Stueck CanFitInInventory und wirft Ueberschuss sogar aktiv wieder ab (JobGiver_UpdateLoadout:135
        // und :314, im Dekompilat nachgelesen). Was wirklich passiert, ist stiller: er holt den Rest gar nicht
        // erst, sein Loadout bleibt dauerhaft halb erfuellt, und ohne diese Warnung sagt ihm das niemand.
        // Genau darum ist die Warnung der Fix und nicht eine harte Grenze beim Zuweisen: die Sperrigkeits-
        // grenze schwankt mit Ruestung und Verletzungen, ein hartes Limit wuerde je nach Tagesform Sachen
        // verweigern, die morgen passen.
        // pawn/bulk (2026-08-30): rechts in der Leiste das Symbol des getragenen Gegenstands, der DIESE
        // Grenze hebt (Rucksack/Weste fuer Sperrigkeit, Exoframe fuer Masse), bei mehreren mit '...'; die Beschriftung
        // rueckt entsprechend. Siehe LoadoutUtility.CarryBoostersOf. `boosters` gibt der Aufrufer weiter an den Tooltip.
        private static bool DrawCapacityBar(Rect bar, float load, float cap, string label, Pawn pawn, bool bulk, out List<LoadoutUtility.CarryBooster> boosters)
        {
            bool over = cap > 0f && load > cap;
            boosters = LoadoutUtility.CarryBoostersOf(pawn, bulk);
            // Zweifarbiger Fuellstand (2026-08-30): die koerpereigene Tragkraft ist die Grenze zwischen den
            // beiden Toenen - alles darueber traegt er nur, WEIL er den Rucksack anhat. Dieselbe Zerlegung, die der
            // Tooltip auflistet (Grundwert + jeder Gegenstand), damit Leiste und Text dieselbe Geschichte erzaehlen.
            float natural = cap;
            foreach (LoadoutUtility.CarryBooster b in boosters) natural -= b.offset;
            LoadoutUtility.DrawCarryFillBar(bar, load, cap, Mathf.Max(0f, natural), over);
            float reserve = over ? 16f : 0f;
            reserve += LoadoutUtility.DrawCarryBoosterIcon(bar, boosters, reserve);
            // Beschriftung mit eigenem Kontrast (2026-08-13) - siehe LoadoutUtility.BarLabel. Der tuerkise
            // Fuellstand lag genau unter den ersten Ziffern, also ausgerechnet unter dem IST-Wert.
            LoadoutUtility.BarLabel(bar, label, reserve);
            // Das gemeinsame Warnzeichen des Mods, oben rechts halb ueber der Kante - dieselbe Geometrie wie
            // an den Zutaten- und Werkbank-Zellen, damit es als dasselbe Zeichen gelesen wird.
            // Ganz INNERHALB des Balkens (2026-08-13: bei zwei Zeilen hing das Warndreieck halb ueber dem
            // Zellrand). An den Zutaten-Zellen faellt das Ueberstehen nicht auf,
            // weil rechts daneben Platz ist - am rechten Rand der kompakten Zeile ist keiner.
            if (over) LoadoutUtility.DrawWarningBadge(new Rect(bar.xMax - 16f, bar.y - 3f, 15f, 15f), LoadoutUtility.ColBad);
            return over;
        }

        // Eine Tragkraft-Leiste fuer eine FREMDE Ansicht (2026-08-30, die Mass/Bulk-Spalten der Zuweisungs-Vorschau):
        // dieselbe Leiste, dasselbe Traeger-Symbol, derselbe Tooltip wie in der kompakten Ansicht - nur mit Zahlen,
        // die der Aufrufer mitbringt (dort der GEPLANTE Stand, nicht der heutige). Kein zweiter Nachbau.
        internal static void DrawPawnCapacityBar(Rect bar, float load, float cap, Pawn pawn, bool bulk)
        {
            string label = bulk
                ? string.Format("{0:F1}/{1:F1}", load, cap)
                : "CER_AssignLoadouts_MassBarFmt".Translate(load.ToString("F1"), cap.ToString("F1")).ToString();
            bool over = DrawCapacityBar(bar, load, cap, label, pawn, bulk, out List<LoadoutUtility.CarryBooster> boosters);
            if (LoadoutUtility.TipWanted(bar))
                TooltipHandler.TipRegion(bar, WithOverCapNote(LoadoutUtility.CarryCapacityTooltip(pawn, bulk, load, cap, boosters), over));
        }

        // EINE Id je Zelle und Kolonist - stabil ueber die Bilder hinweg, verschieden zwischen den Zellen. Ohne sie
        // waere der Tooltip einer Zelle derselbe wie der der Nachbarzelle, sobald beide denselben Text haetten.
        private static int TipId(Pawn pawn, Def d, int salt)
            => (pawn != null ? pawn.thingIDNumber * 31 : 0) ^ (d != null ? d.shortHash * 17 : 0) ^ (salt << 24);

        private static string WithOverCapNote(string tip, bool over)
            => over ? tip + "\n\n" + "CER_AssignLoadouts_OverCapacityTip".Translate().ToString() : tip;

        private static bool BenchExistsFor(ThingDef w)
        {
            List<ThingDef> benchDefs = LoadoutUtility.GetWorkbenchesFor(w);
            if (benchDefs == null || benchDefs.Count == 0) return false;
            return LoadoutUtility.AllColonyBenches().Any(b => benchDefs.Contains(b.def));
        }

        // Builds and shows the ONE supply popup (weapons + shields + ammo, WORKFLOW_OVERHAUL_PLAN.md section 2) for
        // a set of (pawn, planned loadout) pairs. Secondary (sidearm/grenade) shortfall is only included in the
        // "...and sidearms" mode; skipped-superior info is only passed from Auto-Assign. Ammo need is computed
        // directly from the PLANNED loadouts (not pawn.GetLoadout(), which hasn't been updated yet at this point in
        // the deferred-apply flow) via LoadoutUtility.ComputeAmmoNeedForLoadouts, and only surfaced for ammo that
        // isn't already in production anywhere - this is what closes the "ammo was never checked" gap for real,
        // in the single popup the player actually sees, rather than a second one bolted on afterward. When
        // onConfirm is set the assignment is deferred behind the popup's confirm button. Returns true if shown.
        private bool OpenProducePopup(IEnumerable<(Pawn pawn, Loadout loadout)> planned, Action onConfirm = null, Action onCancel = null, Action onBack = null, IEnumerable<(Pawn pawn, List<Loadout> packs)> packAssignments = null, IEnumerable<(Pawn pawn, List<Loadout> sidearms)> sidearmAssignments = null)
            => OpenProducePopup(planned, BuildOwnedWeaponCounts(), onConfirm, onCancel, onBack, packAssignments, sidearmAssignments);

        // Der Bereichs-Schalter "Consider assigned weapons / created loadouts" dieses Popups (2026-08-01
        // bis 2026-09-07) ist entfallen, siehe Dialog_SupplyLoadouts.Request. Der Bedarf
        // kommt aus genau `planned`/`packAssignments`/`sidearmAssignments`, den Kolonisten dieses Laufs.
        // Der Parameter `skipped` (die "Not producible - superior loadouts skipped"-Liste des Loesers) ist am
        // 2026-08-25 samt Anzeige entfallen, weil er keinen Sinn ergab.
        private bool OpenProducePopup(IEnumerable<(Pawn pawn, Loadout loadout)> planned, Dictionary<ThingDef, int> ownedPool, Action onConfirm, Action onCancel = null, Action onBack = null, IEnumerable<(Pawn pawn, List<Loadout> packs)> packAssignments = null, IEnumerable<(Pawn pawn, List<Loadout> sidearms)> sidearmAssignments = null)
        {
            List<(Pawn pawn, Loadout loadout)> pairs = planned.ToList();
            // Planned SIDEARM loadouts (2026-08-01: a satchel charge sidearm assigned to 7
            // colonists with only 2 in stock showed "in storage"/no production needed everywhere - Supply
            // popup didn't even list it) - a sidearm lives on its OWN separate Loadout object, attached via
            // SidearmAssignmentUtility, never inside e.planned/pairs' own main-loadout Slots. Before this fix
            // ComputeMissingSplit/ComputeAmmoNeedForLoadouts/BuildWeaponUsedBy only ever saw the main
            // loadouts, so sidearm-only weapons (grenades, satchel charges, ...) and their ammo were
            // completely invisible to this popup's shortfall check, no matter how many pawns needed one.
            // Folded in via ComputeMissingSplit's own dedicated sidearmLoadouts param (2026-08-01) - same
            // demand-sum-then-subtract-owned-once logic as the main loadouts, just never run through
            // AnalyzeLoadout (a sidearm's own gun must never populate mainDefs on its own - see that method's
            // comment). This is now the SAME method Dialog_SquadSetup's own preview badges call too, so the
            // two screens can never drift out of sync with each other again.
            List<(Pawn pawn, Loadout loadout)> sidearmPairs = sidearmAssignments != null
                ? sidearmAssignments.Where(a => a.sidearms != null).SelectMany(a => a.sidearms.Where(s => s != null).Select(s => (a.pawn, s))).ToList()
                : new List<(Pawn, Loadout)>();
            // Same gap, same fix, for packs (2026-08-02: shields never showed up as missing
            // anywhere) - shields live inside a "Shield Kit" Equipment Pack now, not a pawn's main loadout,
            // so ComputeMissingSplit's shieldMissing below (fed only by `pairs`/sidearmPairs) could never
            // find one no matter what was actually assigned. Packs attach the exact same way sidearms do
            // (their own separate Loadout object, never inside e.planned/pairs' own Slots), so they need the
            // identical fold-in treatment.
            List<(Pawn pawn, Loadout loadout)> packPairs = packAssignments != null
                ? packAssignments.Where(a => a.packs != null).SelectMany(a => a.packs.Where(pk => pk != null).Select(pk => (a.pawn, pk))).ToList()
                : new List<(Pawn, Loadout)>();

            Dictionary<ThingDef, int> mainReq, secReq, shieldMissing, missingSpecifics, produciblePackItems;
            // Zu jeder Pack-Zeile der Def, an dem der BEDARF haengt - folgt DEMSELBEN Zweig wie die Zeilen
            // selbst, sonst traegt eine Zeile aus dem einen Umfang die Herkunft aus dem anderen.
            Dictionary<ThingDef, Def> packDemandDefsForRequest = null;
            Dictionary<LoadoutGenericDef, int> relevantGenerics;

            // Both indicator sources are ALWAYS computed now, regardless of scope (2026-08-02) - the "Assig."
            // column used to be a single dict-set whose MEANING silently flipped between "real pawn" and
            // "loadout template" depending on this scope flag (two screenshots of the same
            // colonist showing wildly different numbers). Only the visible DEMAND numbers (mainReq etc.
            // below) still depend on scope; the diamond/circle indicators are independent of it.
            // Assigned (diamond) - this run's own planned pawn/sidearm demand.
            //
            // Munition kommt aus dem LIVE-Loadout des Kolonisten, nicht aus der Vorlage (2026-08-13):
            // Supply Loadouts schaute faelschlicherweise noch in das urspruengliche Loadout.
            // Der Ablauf ist der Grund:
            // ApplyPreviewPlan ruft apply() VOR diesem Popup, und apply() schreibt die frisch gewaehlte
            // Munition ("Override ammo", standardmaessig AN, reglerabhaengig) auf den persoenlichen Anker
            // des Kolonisten. Die geplante VORLAGE behaelt dabei unveraendert die Munition, mit der sie
            // erzeugt wurde. Wer hier die Vorlage liest, liest also genau die Munition, die der Kolonist
            // NICHT bekommt - das Fenster schlug Kaliber zum Herstellen vor, die niemand benutzt, und
            // schrieb im "Used by" den Namen dessen daneben, der stattdessen etwas anderes traegt.
            // Der Anker ist die einzige Stelle, an der beides zusammenkommt: Waffe(n) aus der Vorlage,
            // Zweitwaffen und Packs als Extra-Eltern, und die persoenliche Munitionswahl, die jede
            // geerbte ersetzt (Patch_Loadout_MultiParentSlots). Ein Eintrag pro Kolonist statt einer pro
            // Vorlage+Zweitwaffe, weil die Vereinigung ohnehin schon alles enthaelt.
            // Nebeneffekt, gewollt: damit rechnet dieser Einstieg dieselbe Summe wie
            // BuildSupplyRequestForPawns, das schon immer p.GetLoadout() gelesen hat.
            List<(Loadout loadout, string pawnName)> ammoPairs = pairs.Select(p => p.pawn)
                .Concat(sidearmPairs.Select(p => p.pawn))
                .Where(p => p != null)
                .Distinct()
                .Select(p => (loadout: p.GetLoadout(), pawnName: p.LabelShortCap))
                .Where(t => t.loadout != null && !t.loadout.defaultLoadout)
                .ToList();
            Dictionary<ThingDef, List<string>> weaponAssignedUsedBy = BuildWeaponUsedBy(ammoPairs);
            LoadoutUtility.ComputeAmmoNeedForLoadouts(ammoPairs, out var pairGenerics, out var pairSpecifics, out Dictionary<Def, List<string>> assignedUsedBy);
            Dictionary<ThingDef, int> assignedMissingPackItems = new Dictionary<ThingDef, int>();
            Dictionary<ThingDef, Def> assignedPackDemandDefs = null;
            if (packAssignments != null)
            {
                LoadoutUtility.ScanMissingAssignedPackItems(packAssignments, out assignedMissingPackItems, out var assignedPackUsedBy, out assignedPackDemandDefs);
                foreach (var kv in assignedPackUsedBy) weaponAssignedUsedBy[kv.Key] = kv.Value;
            }
            // Used in loadout (circle) - every managed template that exists.
            LoadoutUtility.ComputeMissingForCreatedLoadouts(out var createdMain, out var createdSidearms, out Dictionary<ThingDef, List<string>> weaponTemplateUsedBy,
                out var createdPackItems, out var templatePackUsedBy, out var createdGenerics, out var createdSpecifics, out Dictionary<Def, List<string>> templateUsedBy, out var createdPackDemandDefs);
            foreach (var kv in templatePackUsedBy) weaponTemplateUsedBy[kv.Key] = kv.Value;

            ComputeMissingSplit(pairs.Select(p => p.loadout), sidearmPairs.Select(p => p.loadout).Concat(packPairs.Select(p => p.loadout)), ownedPool, out var mainMissing, out var secMissing, out shieldMissing);
            mainReq = produceMainWeapons ? mainMissing : new Dictionary<ThingDef, int>();
            secReq = produceSidearms ? secMissing : new Dictionary<ThingDef, int>();
            // The Arm overview records inside the popup's confirm (GenerateAll) - what was ACTUALLY queued,
            // including ammo, after any deselections - so nothing is double counted here.

            // Filter to what Dialog_SupplyLoadouts would actually show a row for (researched ammo only), so an
            // unresearched-only need can't pop the popup open just to say "nothing to supply".
            // Quantity-aware, not the old binary "any bill exists at all" gate (2026-08-01 fix, mirrors
            // LoadoutUtility.CheckAndOpenMissingProductionPopup's own same-shape fix from 2026-07-31, which
            // this sibling call site never got) - the old !WeaponHasActiveBill(kv.Key) suppressed the WHOLE
            // shortfall the instant ANY bill for that ammo existed anywhere, even a leftover 1-unit bill from
            // before more colonists got assigned it, silently under-proposing real demand on re-trigger.
            // + CarriedStockCount (2026-08-12): hier ist es die direkte Folge des Fixes von heute frueh -
            // seit `sidearmAssignments` nicht mehr auf !kept gefiltert wird, steckt im Bedarf oben auch
            // der Zweitwaffen-Munitionsbedarf von Kolonisten, die ihre Munition SCHON TRAGEN. Ohne den
            // dritten Bestandstopf meldet genau dieser Fall eine Fehlmenge, die es nicht gibt. Die alte
            // Loesung dafuer war der kept-Filter, und der hat zu viel weggeworfen (siehe dort).
            missingSpecifics = pairSpecifics.Where(kv => LoadoutUtility.IsAmmoResearched(kv.Key)
                && LoadoutUtility.AmmoOnHand(kv.Key) + LoadoutUtility.WeaponQueuedCount(kv.Key) < kv.Value)
                .ToDictionary(kv => kv.Key, kv => kv.Value);
            relevantGenerics = pairGenerics.Where(kv =>
                LoadoutUtility.GetAmmoTypesFor(LoadoutUtility.GetWeaponForGenericAmmo(kv.Key)).Any(LoadoutUtility.IsAmmoResearched))
                .ToDictionary(kv => kv.Key, kv => kv.Value);

            // Fold in consumables that need a standing maintenance bill even though raw current stock
            // alone wouldn't otherwise earn them a row (2026-08-01, this is the most commonly hit call
            // site - a normal Apply goes through here) - see LoadoutUtility.ConsumablesNeedingMaintenance's
            // own comment. Pack items get the same treatment further down, once missingPackItems exists.
            foreach (var kv in LoadoutUtility.ConsumablesNeedingMaintenance(pairSpecifics))
                if (!missingSpecifics.ContainsKey(kv.Key)) missingSpecifics[kv.Key] = kv.Value;
            foreach (var kv in LoadoutUtility.ConsumablesNeedingMaintenance(mainMissing, requireConsumable: true))
                if (!mainReq.ContainsKey(kv.Key)) mainReq[kv.Key] = kv.Value;
            foreach (var kv in LoadoutUtility.ConsumablesNeedingMaintenance(secMissing, requireConsumable: true))
                if (!secReq.ContainsKey(kv.Key)) secReq[kv.Key] = kv.Value;

            // Equipment Pack shortfalls, colony-wide (not scoped to just this run's changed pawns, same reach
            // as LoadoutUtility.ScanMissingAssignedPackItems' own standalone use) - folded into this SAME popup
            // so it's feature-complete and Dialog_ArmReport no longer needs its own separate end-of-chain
            // safety-net popup for Arm-my-Colony runs (2026-07-31: one popup, not two in a
            // row - the old second popup was the only place pack items were ever checked at all).
            // Kein IsProducible-Filter mehr (2026-08-13) - siehe den
            // Erstellt-Zweig oben und LoadoutUtility.CheckAndOpenMissingProductionPopup.
            produciblePackItems = new Dictionary<ThingDef, int>(assignedMissingPackItems);
            packDemandDefsForRequest = assignedPackDemandDefs;
            foreach (var kv in LoadoutUtility.ConsumablesNeedingMaintenance(assignedMissingPackItems, requireConsumable: true))
                if (!produciblePackItems.ContainsKey(kv.Key)) produciblePackItems[kv.Key] = kv.Value;

            var req = new Dialog_SupplyLoadouts.Request
            {
                mainWeapons = mainReq,
                secondaryWeapons = secReq,
                shields = shieldMissing,
                packItems = produciblePackItems,
                packItemDemandDefs = packDemandDefsForRequest,
                genericAmmo = relevantGenerics,
                specificAmmo = missingSpecifics,
                assignedUsedBy = assignedUsedBy,
                templateUsedBy = templateUsedBy,
                weaponAssignedUsedBy = weaponAssignedUsedBy,
                weaponTemplateUsedBy = weaponTemplateUsedBy,
                onConfirm = onConfirm,
                onCancel = onCancel,
                onBack = onBack,
                armReport = armReport
            };
            // Obsolete-bill cleanup (2026-08-01, noticed repeatedly - this exact popup, by far the
            // most commonly seen Supply Loadouts instance in the whole mod, never got this at all; only the
            // separate, far rarer end-of-session LoadoutUtility.CheckAndOpenMissingProductionPopup did).
            // Shared helper, not a second hand-rolled copy - see AttachObsoleteCleanup's own comment.
            LoadoutUtility.AttachObsoleteCleanup(req);
            int total = mainReq.Count + secReq.Count + shieldMissing.Count
                + relevantGenerics.Count + missingSpecifics.Count + produciblePackItems.Count
                + req.obsoleteAmmoCandidates.Count + req.obsoleteWeaponCandidates.Count;
            if (total > 0)
            {
                // The popup itself may still resolve every raw need as "already covered" (queued production claims
                // the shortfall, generic ammo resolves to a round already in production) - only show it when a row
                // would actually be visible; otherwise the caller proceeds as if nothing was missing.
                Dialog_SupplyLoadouts dlg = new Dialog_SupplyLoadouts(req);
                if (dlg.AnythingToSupply) { Find.WindowStack.Add(dlg); return true; }
            }
            return false;
        }

        private bool PawnMatchesSearch(Pawn p)
        {
            if (string.IsNullOrEmpty(pawnSearch)) return true;
            string full = p.Name != null ? p.Name.ToStringFull : p.LabelShort;
            return (full != null && full.IndexOf(pawnSearch, System.StringComparison.OrdinalIgnoreCase) >= 0)
                || (p.LabelShort != null && p.LabelShort.IndexOf(pawnSearch, System.StringComparison.OrdinalIgnoreCase) >= 0);
        }

        // Does a loadout (via its main weapon) pass the top-of-tab weapons filter?
        private bool LoadoutPassesFilter(ThingDef mainWeapon, Dictionary<ThingDef, int> owned)
        {
            if (mainWeapon == null) return true;
            switch (loadoutFilter)
            {
                case AssignLoadoutFilter.AvailableMainWeapon:
                    return owned != null && owned.TryGetValue(mainWeapon, out int c) && c > 0;
                case AssignLoadoutFilter.ResearchedMainWeapon:
                    RecipeDef r = LoadoutUtility.GetRecipeFor(mainWeapon);
                    if (r != null && LoadoutUtility.RecipeIsResearched(r)) return true;
                    return owned != null && owned.TryGetValue(mainWeapon, out int c2) && c2 > 0;
                default:
                    return true;
            }
        }

        // Can this weapon be crafted right now (recipe researched AND a suitable workbench is built)?
        internal static bool ProducibleNow(ThingDef w)
        {
            RecipeDef r = LoadoutUtility.GetRecipeFor(w);
            return r != null && LoadoutUtility.RecipeIsResearched(r) && BenchExistsFor(w);
        }

        // Same question for ammo: LoadoutUtility.IsAmmoResearched only checks the recipe's research
        // prerequisite, not whether a bench able to run it is actually built yet - a fresh colony can have
        // an already-researched ammo recipe with no Machining table/reloading bench on the map at all, which
        // used to read as plain "missing (can be crafted)" everywhere (2026-07-31: no bench could even be
        // built for the ammo - the tooltip claimed craftable while
        // the player couldn't even construct the bench yet).
        internal static bool AmmoProducibleNow(ThingDef ammoDef) => LoadoutUtility.IsAmmoResearched(ammoDef) && BenchExistsFor(ammoDef);

        public void RefreshPawns()
        {
            // Every colonist in the empire - every colony map plus every player-controlled caravan - not just
            // whichever map the camera happens to be looking at.
            cachedPawns = LoadoutUtility.AllColonyPawns().ToList();
            rangedScores.Clear();
            meleeScores.Clear();
            traitStrings.Clear();
            traitTooltipStrings.Clear();
            capacityTooltips.Clear();

            foreach (Pawn pawn in cachedPawns)
            {
                // Ranged Score
                float shootingSkill = pawn.skills.GetSkill(SkillDefOf.Shooting).Level;
                float sight = pawn.health.capacities.GetLevel(PawnCapacityDefOf.Sight);
                float manipulation = pawn.health.capacities.GetLevel(PawnCapacityDefOf.Manipulation);
                float rangedScore = (shootingSkill * 10f) * sight * Mathf.Min(manipulation, 1f);
                
                // Penalize if blind
                if (sight <= 0.1f) rangedScore = 0f;
                rangedScores[pawn] = rangedScore;

                // Melee Score
                float meleeSkill = pawn.skills.GetSkill(SkillDefOf.Melee).Level;
                float moving = pawn.health.capacities.GetLevel(PawnCapacityDefOf.Moving);
                float meleeScore = (meleeSkill * 10f) * sight * moving * manipulation;

                List<string> notableTraits = new List<string>();
                List<string> traitTips = new List<string>();
                
                if (pawn.story != null && pawn.story.traits != null)
                {
                    TraitDef brawlerDef = DefDatabase<TraitDef>.GetNamedSilentFail("Brawler");
                    TraitDef nimbleDef = DefDatabase<TraitDef>.GetNamedSilentFail("Nimble");
                    TraitDef toughDef = DefDatabase<TraitDef>.GetNamedSilentFail("Tough");
                    TraitDef wimpDef = DefDatabase<TraitDef>.GetNamedSilentFail("Wimp");
                    TraitDef shootingAccDef = DefDatabase<TraitDef>.GetNamedSilentFail("ShootingAccuracy");

                    // Badge word is the REAL trait's own label (Trait.LabelCap), not our own translation -
                    // "Brawler"/"Nimble"/etc. are vanilla TraitDefs, already translated by Core into every
                    // language RimWorld ships; inventing our own CER_ copy of that word would duplicate
                    // Ludeon's translation work and risk drifting from whatever word Core actually uses
                    // elsewhere in the UI for the same trait (2026-07-31, user-caught: "das hört sich eher
                    // nach vanilla an, wir dürfen nur in unserer Codebase suchen"). Only the TIP (our own
                    // explanation of the scoring effect) is our content, parameterized with the real label.
                    if (brawlerDef != null && pawn.story.traits.HasTrait(brawlerDef)) { meleeScore *= 1.5f; string lbl = pawn.story.traits.GetTrait(brawlerDef)?.LabelCap ?? brawlerDef.label; notableTraits.Add(lbl); traitTips.Add("CER_AssignLoadouts_TraitBrawlerTip".Translate(lbl)); }
                    if (nimbleDef != null && pawn.story.traits.HasTrait(nimbleDef)) { meleeScore *= 1.25f; string lbl = pawn.story.traits.GetTrait(nimbleDef)?.LabelCap ?? nimbleDef.label; notableTraits.Add(lbl); traitTips.Add("CER_AssignLoadouts_TraitNimbleTip".Translate(lbl)); }
                    if (toughDef != null && pawn.story.traits.HasTrait(toughDef)) { meleeScore *= 1.5f; string lbl = pawn.story.traits.GetTrait(toughDef)?.LabelCap ?? toughDef.label; notableTraits.Add(lbl); traitTips.Add("CER_AssignLoadouts_TraitToughTip".Translate(lbl)); }
                    if (wimpDef != null && pawn.story.traits.HasTrait(wimpDef)) { meleeScore *= 0.1f; string lbl = pawn.story.traits.GetTrait(wimpDef)?.LabelCap ?? wimpDef.label; notableTraits.Add(lbl); traitTips.Add("CER_AssignLoadouts_TraitWimpTip".Translate(lbl)); }

                    if (shootingAccDef != null && pawn.story.traits.HasTrait(shootingAccDef))
                    {
                        string lbl = pawn.story.traits.GetTrait(shootingAccDef)?.LabelCap ?? shootingAccDef.label;
                        if (pawn.story.traits.GetTrait(shootingAccDef).Degree > 0) { notableTraits.Add(lbl); traitTips.Add("CER_AssignLoadouts_TraitCarefulShooterTip".Translate(lbl)); }
                        else { notableTraits.Add(lbl); traitTips.Add("CER_AssignLoadouts_TraitTriggerHappyTip".Translate(lbl)); }
                    }
                }

                if (pawn.health != null && pawn.health.hediffSet != null)
                {
                    var missingParts = pawn.health.hediffSet.GetMissingPartsCommonAncestors();
                    foreach (var mp in missingParts)
                    {
                        notableTraits.Add("CER_AssignLoadouts_MissingPartFmt".Translate(mp.Part.Label));
                        traitTips.Add("CER_AssignLoadouts_MissingPartTipFmt".Translate(mp.Part.Label));
                    }
                    foreach (var hediff in pawn.health.hediffSet.hediffs)
                    {
                        if (hediff is Hediff_AddedPart || hediff is Hediff_Implant)
                        {
                            notableTraits.Add(hediff.LabelCap);
                            traitTips.Add("CER_AssignLoadouts_ModifiesCapacitiesTipFmt".Translate(hediff.LabelCap));
                        }
                    }
                }
                
                if (sight <= 0.1f || moving <= 0.1f) meleeScore = 0f;
                meleeScores[pawn] = meleeScore;
                traitStrings[pawn] = string.Join(", ", notableTraits.ToArray());
                traitTooltipStrings[pawn] = string.Join("\n", traitTips.ToArray());
                capacityTooltips[pawn] = BuildCapacityTooltip(pawn);
            }
            
            SortPawns();
        }

        // Non-combatants (pacifists, babies - the shared CannotFight rule) always sink to the END: nothing can be
        // assigned to them, so they must never occupy the prime card slots. Within each group the chosen skill
        // sort (ranged/melee radios) applies.
        private void SortPawns()
        {
            if (cachedPawns == null) return;
            cachedPawns = cachedPawns
                .OrderBy(p => LoadoutUtility.CannotFight(p) ? 1 : 0)
                .ThenByDescending(p => sortByMelee ? MeleeScoreOf(p) : RangedScoreOf(p))
                .ToList();
        }

        public void DoWindowContents(Rect inRect)
        {
            if (cachedPawns == null)
            {
                RefreshPawns();
            }

            if (cachedPawns == null || cachedPawns.Count == 0)
            {
                Widgets.Label(inRect, "CER_AssignLoadouts_NoColonists".Translate());
                return;
            }

            if (Time.frameCount != frameCacheStamp)
            {
                frameCacheStamp = Time.frameCount;
                // Die vier "Bild-Caches" leben seit 2026-08-25 laenger als ein Bild (perf-cell-row-next):
                // Bestandstopf, Tragkraft, Munitionszelle und Zeileninfo wurden bei 143 Hz rund 90 Mal je
                // Sekunde neu gebaut - Slots-Vereinigung je Kolonist, Kartenscan nach Waffen, RelevantAmmoPool
                // - fuer ein Spiel, das pausiert ist. Jetzt gilt die Zwei-Sperren-Regel: neu bei jeder
                // gemeldeten Mutation (CombatReadiness.MutationVersion) oder nach 0,3 s. Was darunter liegt
                // (frameRemainingCache: ein ABZAEHLENDER Topf, frameFulfillCache: Memo je Durchgang) bleibt
                // je Durchgang, das ist seine Natur.
                float now = Time.realtimeSinceStartup;
                if (frameOwnedCache == null || rowCacheVersion != CombatReadiness.MutationVersion || now - rowCacheAt > RowCacheMaxAge)
                {
                    rowCacheVersion = CombatReadiness.MutationVersion;
                    rowCacheAt = now;
                    frameOwnedCache = BuildOwnedWeaponCounts();
                    frameCapCache.Clear();
                    frameAmmoCache.Clear();
                    frameRowCache.Clear();
                    frameLabelCache.Clear();
                    // Topf UND Memo gehoeren seit 2026-08-27 in denselben Block wie die vier darueber.
                    // Der Kommentar von 2026-08-17 sagte "der Fulfill-Cache merkt sich Ergebnisse aus genau
                    // diesem Zaehler und muss deshalb MIT IHM ZUSAMMEN zurueck" - das stimmt, und es ist die
                    // ganze Bedingung. Sie muessen zusammen zurueckgesetzt werden, nicht je Durchgang:
                    // solange beide stehen, liefert jeder wiederholte Aufruf seinen gemerkten Wert und zaehlt
                    // deshalb NICHTS mehr ab - der Topf kann also gar nicht leerlaufen. Ein neuer Schluessel
                    // (eine Zeile, die vorher nicht gezeichnet wurde) zaehlt vom Rest ab, und das ist richtig
                    // so: die schon gezeichneten Zeilen haben ihren Anteil bereits genommen.
                    // Der Grund fuer die Aenderung, gemessen am 27.08.: die Statusrechnung in DrawFulfillDot
                    // kostete 229 der 509 ms/s des ganzen Reiters - 45 %, jeden Durchgang neu, fuer ein
                    // pausiertes Spiel. Sie haengt an genau denselben Daten wie die vier Caches darueber.
                    frameRemainingCache = new Dictionary<ThingDef, int>(frameOwnedCache);
                    frameFulfillCache = new Dictionary<(Pawn, Def, int), FulfillStatus>();
                    frameRichSegCache = new Dictionary<(Pawn, Def, int, RichStock), LoadoutUtility.AvailabilitySegments>();
                }
            }

            // --- Top bar: search + weapons filter (radios) + produce/roles toggles. The fine-tuning (sourcing,
            // ranged/melee, anti-mech, diversity) lives in the Auto-assign preview popup. ---
            CERUiState ui = RoleUtility.Ui();
            // Selbstheilung: sollte ein Lauf einmal enden, ohne durch FinishArmReport zu gehen (Fenster
            // anders geschlossen), holt das die Ruecksetzung beim naechsten Bild nach - sonst bliebe der
            // erzwungene Modus stehen und wuerde ab dann doch wieder gespiegelt.
            if (armReport == null) RestoreArmRoleMode();
            ui.asLoadoutFilter = loadoutFilter;
            // NICHT waehrend eines Arm-my-Colony-Laufs mit erzwungenem Rollenmodus (siehe armRoleModeRestore).
            if (!armRoleModeRestore.HasValue) ui.asRoleMode = roleMode;
            ui.asSortMelee = sortByMelee;
            ui.asCompact = compactView;
            LoadoutUtility.ModeDetailToggle(inRect.width, ref ui.asDetailed);

            // Row 1: search + the grid-sort radios (both modes - sorting is basic navigation). Detailed mode adds
            // the weapons-filter radios in row 2 and the produce/roles toggles in row 3; Simple mode has one job
            // here (assign), production decisions live in the Supply flow now.
            float topY = 4f;
            pawnSearch = LoadoutUtility.SearchBox(0f, topY, pawnSearch);

            float sx = 288f;
            void SortRadio(string label, bool melee)
            {
                if (LoadoutUtility.Radio(ref sx, topY, label, sortByMelee == melee) && sortByMelee != melee)
                {
                    sortByMelee = melee;
                    SortPawns();
                }
            }
            SortRadio("CER_AssignLoadouts_SortByRanged".Translate(), false);
            SortRadio("CER_AssignLoadouts_SortByMelee".Translate(), true);

            // Compact view: one slim table row per colonist instead of the full cards (portraits and weapon icons stay).
            string cvLbl = "CER_AssignLoadouts_CompactViewLabel".Translate();
            float cvW = Text.CalcSize(cvLbl).x + 30f;
            Rect cvRect = new Rect(inRect.width - 56f - cvW, topY, cvW, 24f);
            Widgets.CheckboxLabeled(cvRect, cvLbl, ref compactView);
            TooltipHandler.TipRegion(cvRect, "CER_AssignLoadouts_CompactViewTip".Translate());

            // Die Filterzeilen des Detailmodus sind RAUS (2026-09-03: die Filter "all loadouts /
            // researched weapons" ergaben keinen Sinn mehr, und der "Assign roles"-Knopf oben auch
            // nicht): der Loadout-Filter gehoerte zur alten Loadout-Auswahl (das Waffen-Fenster hat
            // eigene Filter), und der Rollen-Modus wird dort eingestellt, wo er wirkt - in der
            // Auto-Vorschau. Der Kopf ist damit in beiden Modi gleich schlank.
            float gridTop = LoadoutUtility.Divider(30f, inRect.width);

            List<Pawn> visiblePawns = string.IsNullOrEmpty(pawnSearch) ? cachedPawns : cachedPawns.Where(PawnMatchesSearch).ToList();

            // Compact view's own Name column sort (2026-08-01) - a freshly-sorted COPY built for display only.
            // visiblePawns is cachedPawns itself when there's no search text (see above), and cachedPawns is a
            // shared list read elsewhere (CachedPawns accessor, used by Dialog_SquadSetup's auto-assign
            // planner) - sorting it in place would silently reorder that too. Card view deliberately keeps
            // reading visiblePawns unsorted (its own rank/score order); only the compact row loop below reads
            // displayPawns.
            List<Pawn> displayPawns = visiblePawns;
            if (compactView && currentSortMode != SortMode.Default)
            {
                displayPawns = new List<Pawn>(visiblePawns);
                // Ranged/Melee dazu (2026-08-09): bei 20 Kolonisten war "wer schiesst am besten" nur durch
                // Zeile-fuer-Zeile-Lesen zu beantworten, obwohl die Werte direkt danebenstehen. Absteigend als
                // erster Klick, weil bei einer Faehigkeit "die Besten zuerst" die gemeinte Frage ist - beim
                // Namen ist aufsteigend das Erwartete, deshalb der Unterschied.
                switch (currentSortMode)
                {
                    case SortMode.Ranged:
                        displayPawns.Sort((a, b) => RangedScoreOf(b).CompareTo(RangedScoreOf(a)));
                        break;
                    case SortMode.Melee:
                        displayPawns.Sort((a, b) => MeleeScoreOf(b).CompareTo(MeleeScoreOf(a)));
                        break;
                    default:
                        displayPawns.Sort((a, b) => string.Compare(a.LabelShortCap, b.LabelShortCap, StringComparison.OrdinalIgnoreCase));
                        break;
                }
                if (sortDescending) displayPawns.Reverse();
            }

            int columns = Mathf.FloorToInt((inRect.width + Margin) / (CardWidth + Margin));
            if (columns < 1) columns = 1;
            if (compactView) columns = 1;

            // One line now - the weapon status used to need a second "Equipped:" detail row below this one,
            // but that's now a corner dot on the loadout button itself (DrawWeaponStatusDot), same as Ammo/
            // Pack/Sidearm's own dots.
            const float CompactRowH = 32f;
            int rows = Mathf.CeilToInt((float)visiblePawns.Count / columns);
            float totalHeight = compactView ? visiblePawns.Count * CompactRowH : rows * (CardHeight + Margin);

            // Column headers - compact view never had any (every column was unlabeled). Same convention
            // Dialog_GenerateLoadouts' own Weapon Loadouts tab list already uses for its headers (Tiny font,
            // 55% alpha, plain labels, no divider box) - not inventing a new header style here.
            if (compactView)
            {
                DrawCompactHeader(new Rect(0f, gridTop, inRect.width - 20f, 18f));
                gridTop += 20f;
            }

            Rect viewRect = new Rect(0, 0, inRect.width - 20f, totalHeight);
            Rect scrollRect = new Rect(0, gridTop, inRect.width, inRect.height - gridTop - 50f);
            if (compactView) Widgets.BeginScrollView(scrollRect, ref compactScrollPosition, viewRect);
            else Widgets.BeginScrollView(scrollRect, ref cardScrollPosition, viewRect);

            // Scroll culling (2026-08-02, found while chasing a lag): rows outside the visible
            // window only advance the cursor, same pattern Dialog_SquadSetup's own two row lists already use
            // - this dialog's per-row work (SingleFulfillStatus etc.) is already frame-cached at the
            // LoadoutUtility level, so an off-screen row's own marginal cost was mostly GUI layout/tooltip-
            // string overhead, not the dominant lag source (that was BuildOwnedWeaponCounts, fixed above) -
            // but it scales with roster size and is free to fix while already here.
            int col = 0, row = 0;
            foreach (Pawn pawn in displayPawns)
            {
                if (compactView)
                {
                    float ry = row * CompactRowH;
                    if (ry + CompactRowH < compactScrollPosition.y || ry > compactScrollPosition.y + scrollRect.height) { row++; continue; }
                    DrawPawnRowCompact(new Rect(0f, ry, viewRect.width, CompactRowH - 2f), pawn, row % 2 == 1);
                    row++;
                    continue;
                }
                float x = col * (CardWidth + Margin);
                float y = row * (CardHeight + Margin);
                if (y + CardHeight < cardScrollPosition.y || y > cardScrollPosition.y + scrollRect.height)
                {
                    col++;
                    if (col >= columns) { col = 0; row++; }
                    continue;
                }
                DrawPawnCard(new Rect(x, y, CardWidth, CardHeight), pawn);
                col++;
                if (col >= columns) { col = 0; row++; }
            }

            DrawDragChipAndReset();
            Widgets.EndScrollView();

            // --- Bottom bar: actions flowing in from the right (primary rightmost); the window's X closes. ---
            float xr = inRect.width;
            // EINE Schriftstufe fuer die ganze Reihe (2026-09-03: auf
            // Deutsch stand "Munition massenhaft ersetzen" klein zwischen zwei grossen Nachbarn) - siehe
            // LoadoutUtility.BottomBarFont.
            string autoAssignLbl = "CER_AssignLoadouts_AutoAssignBtn".Translate().ToString();
            string massAmmoLbl = "CER_AssignLoadouts_MassReplaceAmmoBtn".Translate().ToString();
            string unassignAllLbl = "CER_AssignLoadouts_UnassignAllBtn".Translate().ToString();
            GameFont prevBarFont = Text.Font;
            Text.Font = LoadoutUtility.BottomBarFont(
                (LoadoutUtility.PrimaryBtnW, autoAssignLbl, true),
                (LoadoutUtility.SecondaryBtnW, massAmmoLbl, false),
                (LoadoutUtility.SecondaryBtnW, unassignAllLbl, false));
            if (LoadoutUtility.BottomActionButton(inRect, ref xr, LoadoutUtility.PrimaryBtnW, autoAssignLbl,
                tip: "CER_AssignLoadouts_AutoAssignBtnTip".Translate(),
                icon: LoadoutUtility.TexWand))
            {
                RefreshPawns();
                Find.WindowStack.Add(new Dialog_SquadSetup(null, this));
            }
            if (LoadoutUtility.BottomActionButton(inRect, ref xr, LoadoutUtility.SecondaryBtnW, massAmmoLbl,
                tip: "CER_AssignLoadouts_MassReplaceAmmoBtnTip".Translate()))
            {
                Find.WindowStack.Add(new Dialog_MassReplaceAmmo());
            }
            // "Unassign all" (2026-09-03): nimmt JEDEM freien Kolonisten die Zuweisung weg -
            // ueber den ankersicheren Weg (ArmUnassignAll behaelt Packs/Zweitwaffen am Anker), mit
            // Bestaetigung, weil es die ganze Kolonie betrifft.
            if (LoadoutUtility.BottomActionButton(inRect, ref xr, LoadoutUtility.SecondaryBtnW, unassignAllLbl,
                tip: "CER_AssignLoadouts_UnassignAllBtnTip".Translate()))
            {
                Find.WindowStack.Add(LoadoutUtility.CompactConfirmation(
                    "CER_AssignLoadouts_UnassignAllConfirm".Translate(),
                    () =>
                    {
                        int n = ArmUnassignAll();
                        CombatReadiness.Invalidate();
                        RefreshPawns();
                        Messages.Message("CER_AssignLoadouts_UnassignAllDoneFmt".Translate(n.ToString()), MessageTypeDefOf.TaskCompletion, false);
                    }, true));
            }
            Text.Font = prevBarFont;
        }

        // Arm-my-Colony: refresh, then open the live auto-assign preview with production enabled. The Arm modes map
        // onto the popup's tuning: Aggressive plans everyone anew, Filling-gaps keeps current assignments; the
        // sourcing radios preset the use-existing/craft-new slider. The report shows at the very end of the flow.
        public void ArmAutoAssignAndProduce(bool aggressive, ArmReport rep, Action onBack = null, RoleAssignMode? roleModeOverride = null)
        {
            armReport = rep;
            RefreshPawns();
            // Nur FUER DIESEN LAUF - siehe armRoleModeRestore. Ein zweiter Lauf ohne dazwischenliegendes
            // FinishArmReport darf den urspruenglichen Wert nicht ueberschreiben, deshalb nur beim ersten Mal merken.
            if (roleModeOverride.HasValue)
            {
                if (!armRoleModeRestore.HasValue) armRoleModeRestore = roleMode;
                roleMode = roleModeOverride.Value;
            }
            // Craft willingness is set in the preview's own slider (persisted per save); the Arm mode only decides
            // whether existing assignments are kept (Filling gaps) or everyone is re-planned (Aggressive).
            Find.WindowStack.Add(new Dialog_SquadSetup(null, this, presetKeepAssigned: !aggressive,
                onBack: onBack));
        }

        // Aggressive reset: send every free colonist back to the default (no) loadout so the run reassigns from a clean slate.
        public int ArmUnassignAll()
        {
            Loadout none = LoadoutManager.Loadouts.FirstOrDefault(l => l.defaultLoadout);
            int n = 0;
            foreach (Pawn p in LoadoutUtility.AllColonyPawns().ToList())
            {
                Loadout cur = p.GetLoadout();
                if (cur == null || cur.defaultLoadout) continue;
                // Anker BEHALTEN statt SetLoadout(none) (2026-09-01, dieselbe Fehlerklasse
                // wie im Apply-Pfad unten seit dem 25.08.): SetLoadout(none) laeuft am persoenlichen Anker
                // vorbei, der haengt danach an niemandem, und der naechste Aufraeum-Tick loescht ihn SAMT
                // Packs, Zweitwaffen und eigener Munition. Beim Lazy-Umbau wiegt das doppelt, weil "Reset
                // first" in Normal/Aggressive Standard ist und unmittelbar vor der Neuzuweisung laeuft.
                // Ein Anker MIT Zusatz-Eltern wird auf None umgehaengt (Packs/Zweitwaffen bleiben), nur die
                // eigene Munition der alten Waffe geht mit ihr; ein Kolonist ohne solchen Anker geht den
                // alten Weg.
                if (none != null && AmmoAssignmentUtility.IsAmmoLink(cur) && MultiParentUtility.GetExtraParents(cur).Count > 0)
                {
                    Loadout link = AmmoAssignmentUtility.EnsureAmmoLink(p, none);
                    foreach (LoadoutSlot own in link.OwnSlots.Where(LoadoutUtility.IsAmmoSlot).ToList()) link.RemoveSlot(own);
                    AmmoAssignmentUtility.SetAmmoCleared(link, false);
                }
                else p.SetLoadout(none);
                n++;
            }
            return n;
        }

        // Den Rollen-Schalter zuruecknehmen, den die Wurzelfrage von Arm my Colony erzwungen hat.
        // Idempotent - wer ihn nicht erzwungen hat, aendert hier nichts.
        private void RestoreArmRoleMode()
        {
            if (!armRoleModeRestore.HasValue) return;
            roleMode = armRoleModeRestore.Value;
            armRoleModeRestore = null;
        }

        // Show the Arm-my-Colony overview and end the run. Safe to call once per run; further calls are no-ops.
        private void FinishArmReport(string note)
        {
            if (armReport == null) return;
            if (!string.IsNullOrEmpty(note)) armReport.notes.Add(note);
            ArmReport r = armReport;
            armReport = null;
            RestoreArmRoleMode();
            Find.WindowStack.Add(new Dialog_ArmReport(r));
        }

        public int CountUnarmedColonists()
        {
            int n = 0;
            foreach (Pawn p in LoadoutUtility.AllColonyPawns())
            {
                // Pacifists and babies never get armed, so they are not "unarmed" in the actionable sense.
                if (LoadoutUtility.CannotFight(p)) continue;
                // Ueber DisplayLoadout, sonst gilt jeder Kolonist mit persoenlichem Anker als bewaffnet -
                // der Anker selbst ist nie das Default, auch wenn darunter keine Vorlage mehr haengt.
                Loadout lo = AmmoAssignmentUtility.DisplayLoadout(p.GetLoadout());
                if (lo == null || lo.defaultLoadout) n++;
            }
            return n;
        }

        // ---------------------------------------------------------------------------------
        // Auto-assign (the solver itself lives in AssignPlanner; the popup previews its plan live)
        // ---------------------------------------------------------------------------------

        // Commit a previewed plan: assign/clear loadouts, then (with production on) defer everything behind the
        // produce popup exactly like before - cancelling it leaves every colonist untouched.
        internal void ApplyPreviewPlan(AssignPlan plan, bool keepAssigned, bool redistributeCarried = false, Action onBack = null,
            Dictionary<Pawn, List<Dialog_GenerateLoadouts.AmmoPick>> ammoOverrides = null)
        {
            // Materialize virtual picks first: create the default loadout for each newly picked weapon, so the rest
            // of the flow (produce popup, assignment, report) treats them like any other loadout.
            // Statisch seit 2026-09-01 (Lazy-Umbau): der Weg ueber parentWindow.LoadoutsDialog war in der
            // Arm-Kette und der Bereitschaftsanzeige null, und der Pick ging dort still verloren.
            foreach (AssignPlanEntry e in plan.entries)
            {
                if (e.kept) continue;
                if (e.plannedVirtual && e.plannedMain != null)
                {
                    Loadout made = Dialog_GenerateLoadouts.EnsureDefaultLoadout(e.plannedMain);
                    if (made != null) { e.planned = made; e.plannedVirtual = false; }
                }
                // Zweitwaffen/Granaten, die der Loeser als blosse Waffe gewaehlt hat (kein Vorlagen-Objekt
                // existierte beim Planen) - erst JETZT entsteht die geteilte Vorlage, nie in der Vorschau.
                if (e.plannedSidearmDefs != null && e.plannedSidearmDefs.Count > 0)
                {
                    foreach (ThingDef sd in e.plannedSidearmDefs)
                    {
                        Loadout sMade = Dialog_GenerateLoadouts.EnsureDefaultLoadout(sd);
                        if (sMade == null) continue;
                        if (e.plannedSidearms == null) e.plannedSidearms = new List<Loadout>();
                        if (!e.plannedSidearms.Contains(sMade)) e.plannedSidearms.Add(sMade);
                    }
                    e.plannedSidearmDefs = null;
                }
            }

            Action apply = () =>
            {
                Loadout none = LoadoutManager.Loadouts.FirstOrDefault(l => l.defaultLoadout);
                int assigned = 0;
                foreach (AssignPlanEntry e in plan.entries)
                {
                    if (e.kept) continue;
                    if (e.planned != null)
                    {
                        if (e.planned != e.current) AmmoAssignmentUtility.AssignTemplate(e.pawn, e.planned);
                        // Equipment Pack layer, scored alongside the weapon (AssignPlanner.plannedPacks) -
                        // a live reference on this pawn's own hidden personal anchor, never the shared
                        // template (see AmmoAssignmentUtility). Only touches anything when the SET of packs
                        // actually differs from what this pawn already has, so kept-in-place pawns' packs
                        // are untouched.
                        // The "does this differ" test itself now lives in AssignPlanner (WantsPackWrite/
                        // WantsSidearmWrite), because the Apply button's own change count has to ask the
                        // exact same question - it used to carry its own weapon-only idea of "changed" and
                        // therefore read "(nothing to apply)" over a pending pack/sidearm pin it was about
                        // to write (2026-08-12).
                        if (AssignPlanner.WantsPackWrite(e))
                            AmmoAssignmentUtility.SetPawnPacks(e.pawn, e.planned, e.plannedPacks ?? new List<Loadout>());
                        if (AssignPlanner.WantsSidearmWrite(e))
                            SidearmAssignmentUtility.SetPawnSidearms(e.pawn, e.planned, e.plannedSidearms ?? new List<Loadout>());
                        // "Override ammo" (2026-08-01, on by default): write the freshly-picked
                        // ammo (computed in Dialog_SquadSetup, mech-slider + current stock/research aware) onto
                        // this pawn's personal anchor, replacing whatever it had - the SAME mechanism Dialog_
                        // ChangeAmmo already uses for a manual per-pawn override, just applied automatically at
                        // assign time instead of requiring the player to visit that dialog separately. Roles are
                        // never in ammoOverrides (Dialog_SquadSetup excludes them - fixed by design, same as
                        // packs/shields/explosives already do), so a Role pawn's ammo is untouched here too.
                        if (ammoOverrides != null && ammoOverrides.TryGetValue(e.pawn, out List<Dialog_GenerateLoadouts.AmmoPick> picks))
                        {
                            Loadout link = AmmoAssignmentUtility.EnsureAmmoLink(e.pawn, e.planned);
                            foreach (LoadoutSlot old in link.OwnSlots.Where(s => LoadoutUtility.IsAmmoDef(s.thingDef)).ToList())
                                link.RemoveSlot(old);
                            foreach (Dialog_GenerateLoadouts.AmmoPick pick in picks)
                                if (pick.def is ThingDef td) link.AddSlot(new LoadoutSlot(td, pick.count));
                            AmmoAssignmentUtility.SetAmmoCleared(link, picks.Count == 0);
                        }
                        assigned++;
                    }
                    else
                    {
                        // Anker BEHALTEN statt SetLoadout(none) (2026-08-25, vorbestehend):
                        // SetLoadout(none) laeuft am Anker vorbei, der haengt danach an niemandem mehr, und das
                        // naechste Aufraeumen loescht ihn - samt Packs, Zweitwaffen und eigener Munition. Genau
                        // das versprach der Kommentar an WantsPackWrite seit dem 19.08. NICHT zu tun ("would
                        // strip the packs off every colonist the plan simply left without a weapon"). Jetzt
                        // wird der Anker auf None umgehaengt (EnsureAmmoLink), Packs und Zweitwaffen bleiben;
                        // nur die eigene Munition der alten Waffe geht mit ihr - ohne Waffe traegt sie niemand.
                        // Ein Kolonist OHNE Anker (Vorlage direkt zugewiesen, nichts zu bewahren) geht den alten
                        // Weg, damit kein leerer Anker fuer nichts entsteht.
                        if (e.current != null)
                        {
                            Loadout raw = e.pawn.GetLoadout();
                            if (none != null && AmmoAssignmentUtility.IsAmmoLink(raw) && MultiParentUtility.GetExtraParents(raw).Count > 0)
                            {
                                Loadout link = AmmoAssignmentUtility.EnsureAmmoLink(e.pawn, none);
                                foreach (LoadoutSlot own in link.OwnSlots.Where(LoadoutUtility.IsAmmoSlot).ToList()) link.RemoveSlot(own);
                                AmmoAssignmentUtility.SetAmmoCleared(link, false);
                            }
                            else e.pawn.SetLoadout(none);
                        }
                        // Packs need no weapon plan (2026-08-12): a colonist who can't fight still carries
                        // supply - that is what AssignPlanner's own pack-only pass computes for them - and
                        // their anchor hangs off CE's "None" marker, exactly like the live Assign tab's Pack
                        // cell already does it. This whole branch used to do nothing but unassign, so that
                        // pass produced a correct plan every single time and it was dropped here, unwritten.
                        // WantsPackWrite never treats an empty plan on a loadout-less row as "clear these" -
                        // see its own comment - so nobody loses a pack just for going unassigned.
                        if (none != null && AssignPlanner.WantsPackWrite(e))
                            AmmoAssignmentUtility.SetPawnPacks(e.pawn, none, e.plannedPacks ?? new List<Loadout>());
                        // Zweitwaffen genauso (2026-08-25, beim Nachgehen eines Falls: Balork hatte keine
                        // Waffe, eine eingefuegte Hellebarde wurde nicht angezeigt; dazu nach aehnlichen
                        // Schlupfloechern gesucht): eine hier angeheftete Zweitwaffe bei einem Kolonisten ohne
                        // geplante Waffe wurde beim Apply still verworfen, weil dieser Zweig nur Packs schrieb
                        // und WantsSidearmWrite auf planned != null sperrte. Die Zweitwaffe haengt aber wie das
                        // Pack am Anker ueber der None-Vorlage - der Assign-Reiter erlaubt das seit heute auch.
                        if (none != null && AssignPlanner.WantsSidearmWrite(e))
                            SidearmAssignmentUtility.SetPawnSidearms(e.pawn, none, e.plannedSidearms ?? new List<Loadout>());
                    }
                    // Redistribute carried weapons: drop everything this pawn carries that is not part of the new
                    // loadout, so manually picked-up gear returns to circulation and can be hauled to whoever the
                    // plan gave it to. CE's hold tracker would otherwise make the pawn cling to it forever.
                    if (redistributeCarried) DropNonLoadoutWeapons(e.pawn, e.planned);
                }
                // "Ohne Loadout geblieben" heisst: haette eins bekommen sollen und hat keins - nicht "hat
                // keins" (2026-08-13, zwei Befunde derselben Ursache, plus eine Nebenwirkung einer
                // Aenderung vom selben Tag).
                // Die Zeile zaehlte jede Zeile mit !kept && planned == null. Da landen aber auch Leute, fuer
                // die das gar nicht gilt: ein Pazifist, dem nur ein Pack angeheftet wurde, faellt durch die
                // extrasChanged-Regel am Ende von AssignPlanner.Compute auf kept=false und hat naturgemaess
                // kein geplantes Loadout - angezeigt wurde daraufhin "0 Kolonisten zugewiesen, 1 ohne Loadout
                // geblieben", obwohl niemand fehlte. Und seit ein vom Spieler ausgeschlossener Kolonist
                // planned=null/kept=false traegt (der Muelleimer-Zustand), waere er hier ebenfalls als
                // Fehlschlag gezaehlt worden - dabei ist es genau das, was der Spieler wollte.
                // CannotFight deckt beide Faelle ab: das Spielerkennzeichen UND Pazifist/Baby.
                int left = plan.entries.Count(x => !x.kept && x.planned == null && !LoadoutUtility.CannotFight(x.pawn));
                string msg = assigned == 1
                    ? "CER_AssignLoadouts_AutoAssignedSingular".Translate(assigned.ToString())
                    : "CER_AssignLoadouts_AutoAssignedPlural".Translate(assigned.ToString());
                if (left > 0) msg += "CER_AssignLoadouts_LeftWithoutLoadout".Translate(left.ToString());
                Messages.Message(msg + ".", left > 0 ? MessageTypeDefOf.CautionInput : MessageTypeDefOf.TaskCompletion, false);
                if (armReport != null)
                {
                    foreach (AssignPlanEntry e in plan.entries)
                    {
                        if (e.kept || e.planned == null) continue;
                        AnalyzeLoadout(e.planned, out ThingDef mw2, out _, out _, out _);
                        // Last-write-wins, keyed by pawn (2026-08-01: "Cloud -> MosqueFAL"
                        // and "Ryder -> MosqueFAL" each appearing 4-5 times in the final Arm my Colony summary)
                        // - apply() can legitimately run more than once for the SAME plan within one Arm-my-
                        // Colony session (e.g. pinning one colonist re-applies the whole current plan, not just
                        // that one pawn's own change), and this loop used to just Add() every time with no
                        // dedup at all, so a pawn re-touched N times across those re-applies accumulated N
                        // near-identical rows in the report instead of showing their one, final assignment.
                        armReport.assignments.RemoveAll(a => a.pawn == e.pawn);
                        armReport.assignments.Add(new ArmAssignment { pawn = e.pawn, loadout = e.planned, mainWeapon = mw2 });
                    }
                    armReport.assignments.Sort((a, b) => string.Compare(a.pawn.LabelShortCap, b.pawn.LabelShortCap, StringComparison.OrdinalIgnoreCase));
                    armReport.pawnsLeftUnassigned = left;
                }
                RefreshPawns();
                // Readiness cache invalidation (2026-08-01): this is the ONE shared commit path
                // for both the Assign-preview's own apply AND the header widget's "fix no-loadout colonists"
                // one-click action, but never told CombatReadiness a real update just happened - every dialog
                // this mod opens force-pauses the game, so the tick-interval cache can't naturally refresh for
                // up to ~4 real seconds after closing everything, during which the widget keeps re-offering an
                // already-applied fix. Every other real commit path already calls this (Dialog_ArmColony,
                // Dialog_SupplyLoadouts, Dialog_PawnGear) - this was the one gap.
                CombatReadiness.Invalidate();
                // FinishArmReport deliberately NOT called here anymore (2026-08-01) - it's now the produce
                // popup's own job to call it once IT resolves (confirm/cancel/nothing-to-produce), so the
                // Arm Report screen still only appears AFTER that step, not the instant apply() itself runs.
            };

            // Assignment commits IMMEDIATELY now (2026-08-01: all three steps should behave the same -
            // Equipment Packs and Weapon Loadouts both already commit the moment
            // their own Continue/Generate is clicked; only this Assign step used to defer the real
            // AssignTemplate/SetPawnPacks/SetPawnSidearms writes behind the LATER Supply popup's own
            // Confirm button, so clicking Back there (not Cancel) left every pawn's assignment silently
            // never having happened even though the wizard had already visually moved past this step.
            // apply() and the Supply popup below are fully independent concerns - WHO gets WHAT loadout
            // (this) vs. WHAT PRODUCTION to queue for it (that) - so running apply() first and letting the
            // popup handle production-only from here on doesn't lose or duplicate anything either used to do.
            apply();

            // The ammo-gap check (WORKFLOW_OVERHAUL_PLAN.md section 7 - neither the solver nor manual assignment
            // ever checked whether newly assigned loadouts have ammo production at all) is now folded INTO the
            // production popup, so the player sees ONE combined Supply popup (weapons + shields + ammo) instead
            // of two sequential ones - that consolidation is the actual point of this dialog. Always runs now
            // (2026-07-30) - the "produce missing weapons" off path used to skip straight to a lighter ammo-only
            // popup instead; removed along with the toggle itself.
            Action onCancel = () =>
            {
                // Assignment already happened above (no longer gated behind this popup) - only the
                // PRODUCTION step was skipped, so the old "aborted" wording (implying nothing happened at
                // all) would now be actively misleading.
                if (armReport != null) armReport.cancelled = false;
                FinishArmReport("CER_AssignLoadouts_ProduceSkippedNote".Translate());
            };
            var pairs = plan.entries.Where(e => !e.kept && e.planned != null).Select(e => (e.pawn, e.planned));
            // Whole plan, not just the changed subset above - kept pawns' packs are unchanged live state, and
            // newly-planned pawns' packs are exactly what apply() already set, so this already reflects
            // post-apply reality.
            var packAssignments = plan.entries.Select(e => (e.pawn, e.plannedPacks));
            // Ganzer Plan, nicht nur die geaenderten Zeilen (2026-08-12) - genau wie packAssignments eine
            // Zeile darueber. Die alte Begruendung ("ein kept-Pawn traegt seine Zweitwaffe ja schon, das
            // waere Doppelzaehlung") gilt nur, wenn er sie WIRKLICH traegt. Verlangt sein Loadout ein Messer,
            // das die Kolonie nie hergestellt hat, entstand hier ueberhaupt kein Bedarf - und weil nach einem
            // Arm-Lauf praktisch jeder Kolonist "kept" ist, hiess das: fehlende Zweitwaffen tauchen im
            // Nachschub-Fenster GAR NICHT auf.
            // Kein Doppelzaehl-Risiko, und das ist keine Vermutung: ComputeMissingSplit zieht `owned`
            // (BuildOwnedWeaponCounts) ab, und das zaehlt jeden getragenen Gegenstand mit, kept oder nicht -
            // Bedarf 1 und Bestand 1 heben sich fuer den Pawn, der sein Messer hat, exakt auf, waehrend fuer
            // den ohne Messer eine echte Fehlmenge uebrig bleibt.
            // Exakt dieselbe Korrektur hat Dialog_SquadSetup.BuildPlanWideShortfalls am 2026-08-02 bekommen
            // (dort als "Messer zeigt 'bereit', obwohl die Kolonie keins hat" aufgefallen); der Kommentar hier
            // verwies sogar auf jenen Filter als Vorbild - nur war der zu dem Zeitpunkt schon entfernt.
            var sidearmAssignments = plan.entries.Select(e => (e.pawn, e.plannedSidearms));
            // onConfirm here is just the report wrap-up now - apply() already ran above, unconditionally;
            // this popup is purely a "anything to produce for what was just assigned?" follow-up from here.
            if (!OpenProducePopup(pairs, () => FinishArmReport(null), onCancel, onBack, packAssignments, sidearmAssignments))
                FinishArmReport(null);   // nothing to produce - finish immediately, same as before
        }

        // CE's Utility_HoldTracker is internal, so the forget call goes through reflection (resolved once).
        private static readonly System.Reflection.MethodInfo HoldTrackerForgetMI =
            HarmonyLib.AccessTools.Method("CombatExtended.Utility_HoldTracker:HoldTrackerForget", new[] { typeof(Pawn), typeof(Thing) });
        // Gated on Patch_LoadoutTooltip.HoldTrackerAnythingHeld first (2026-08-01, log spam:
        // "X wasn't being tracked by HoldTracker and tried to forget a ThingDef Y") - the old unconditional
        // call fired for EVERY dropped non-loadout weapon regardless of whether CE ever actually hold-tracked
        // it, and most dropped weapons came from a normal loadout assignment, never a manual force-pickup, so
        // the overwhelming majority of these calls were guaranteed no-ops that CE itself logs a warning for.
        // Not a per-item check (CE exposes no reflected "is THIS thing tracked for this pawn" query) - a
        // pawn with ANY hold-tracked item can still log one spurious warning for a different, untracked item
        // being dropped alongside it, but that's the rare case instead of the common one.
        private static void HoldForget(Pawn p, Thing t)
        {
            if (!Patch_LoadoutTooltip.HoldTrackerAnythingHeld(p)) return;
            try { HoldTrackerForgetMI?.Invoke(null, new object[] { p, t }); } catch { }
        }

        // Drop every weapon this pawn carries that is not part of the given loadout (utility items are left alone).
        // CE's hold tracker pins manually picked-up weapons to the pawn, so forget the record first - otherwise the
        // pawn re-collects the weapon right after dropping it.
        private static void DropNonLoadoutWeapons(Pawn p, Loadout lo)
        {
            if (p == null || p.Map == null) return;
            HashSet<ThingDef> allowed = new HashSet<ThingDef>();
            if (lo != null && lo.Slots != null)
                foreach (LoadoutSlot s in lo.Slots)
                    if (s.thingDef != null && s.thingDef.IsWeapon) allowed.Add(s.thingDef);
            // Packs/sidearms too (2026-08-02 fix: a freshly-assigned sidearm/pack weapon got
            // dropped again immediately by THIS SAME apply pass) - `lo` is only the bare main-weapon
            // template; SetPawnPacks/SetPawnSidearms already ran earlier in ApplyPreviewPlan's own apply()
            // loop for this exact pawn, so their weapon defs are real, current state by the time this runs,
            // not something that needs `e.plannedPacks`/`e.plannedSidearms` threaded through as extra params.
            foreach (Loadout pack in AmmoAssignmentUtility.GetPawnPacks(p) ?? new List<Loadout>())
                if (pack?.Slots != null)
                    foreach (LoadoutSlot s in pack.Slots)
                        if (s.thingDef != null && s.thingDef.IsWeapon) allowed.Add(s.thingDef);
            foreach (Loadout side in SidearmAssignmentUtility.GetPawnSidearms(p) ?? new List<Loadout>())
                if (side?.Slots != null)
                    foreach (LoadoutSlot s in side.Slots)
                        if (s.thingDef != null && s.thingDef.IsWeapon) allowed.Add(s.thingDef);

            if (p.equipment != null && p.equipment.Primary != null && !allowed.Contains(p.equipment.Primary.def))
            {
                ThingWithComps eq = p.equipment.Primary;
                HoldForget(p, eq);
                p.equipment.TryDropEquipment(eq, out _, p.Position, false);
            }
            if (p.inventory != null && p.inventory.innerContainer != null)
            {
                foreach (Thing t in p.inventory.innerContainer
                    .Where(x => x.def != null && x.def.IsWeapon && !LoadoutUtility.IsUtilityWeapon(x.def) && !allowed.Contains(x.def)).ToList())
                {
                    HoldForget(p, t);
                    p.inventory.innerContainer.TryDrop(t, p.Position, p.Map, ThingPlaceMode.Near, out _);
                }
            }
        }

        // The preview popup was closed without applying. If an Arm-my-Colony run is in flight, account for it.
        internal void CancelArmAtAssign()
        {
            if (armReport == null) return;
            armReport.cancelled = true;
            FinishArmReport("CER_AssignLoadouts_ArmCancelledNote".Translate());
        }

        // Soft caps that keep the squad's ranged mix balanced (mostly rifles, a few specialists). Caps are maxima,
        // not targets, and are ignored entirely as a fallback so nobody is left unarmed while loadouts remain.
        // Colony-size-aware, SUB-LINEAR caps (WORKFLOW_OVERHAUL_PLAN.md 6d) - replaces the old flat-percentage
        // formula, which scaled specialist roles linearly forever (a 30-pawn colony got up to 8 Marksmen). A small
        // colony still gets an essential floor of one specialist per moderate role once it's big enough to spare
        // one; growth beyond that flattens out (a few more specialists, not proportionally many more) since
        // Rifle/CQC-style general combat should absorb most of a large colony's growth instead. Launcher is the
        // rarest specialist: needs a slightly bigger colony before it appears at all, and grows slower still.
        internal static Dictionary<WeaponGroup, int> BuildGroupCaps(int shooterCount)
        {
            int s = Mathf.Max(1, shooterCount);
            int moderate = s <= 3 ? 1 : Mathf.Clamp(Mathf.RoundToInt(1 + (s - 3) / 8f), 1, 4);
            int rare = s < 3 ? 0 : (s <= 5 ? 1 : Mathf.Clamp(Mathf.RoundToInt(1 + (s - 5) / 12f), 1, 3));
            return new Dictionary<WeaponGroup, int>
            {
                { WeaponGroup.Launcher,  rare },
                { WeaponGroup.SupportMG, moderate },
                { WeaponGroup.Marksman,  moderate }
                // Rifle / CQC / Archaic / Melee / Other are uncapped (the fill) - general combat absorbs colony growth.
            };
        }

        // A loadout counts as RANGED as soon as it carries any (non-utility) firearm - that firearm is the primary and
        // melee is just a sidearm. Only pure-melee loadouts are melee. Within the chosen role the highest-scoring
        // weapon is the "main weapon" (so two firearms fall back to the higher score, as the user intended).
        internal static bool AnalyzeLoadout(Loadout lo, out ThingDef main, out float score, out bool isRanged, out WeaponGroup group)
        {
            main = null; score = 0f; isRanged = false; group = WeaponGroup.Other;
            if (lo == null || lo.Slots == null) return false;

            // Real weapons only - never a thrown explosive (a satchel charge's raw blast score can dwarf the
            // actual gun) and never a utility item.
            List<ThingDef> weps = lo.Slots.Select(s => s.thingDef)
                .Where(w => w != null && w.IsWeapon && !LoadoutUtility.IsUtilityWeapon(w) && !LoadoutUtility.IsThrownExplosive(w))
                .Distinct().ToList();
            if (weps.Count == 0) return false;

            // The loadout's own name identifies its main weapon far more reliably than any score/class guess - see
            // LoadoutUtility.LoadoutNameMatchesWeapon. Without this, a melee-primary loadout's default ranged
            // sidearm (every melee main gets one - PickDefaultSidearm) outranked the melee weapon outright under
            // the old "ranged always wins" rule, so the solver's stock/production checks silently validated the
            // SIDEARM instead of the weapon the loadout is actually named and built around.
            ThingDef mainW = weps.FirstOrDefault(w => LoadoutUtility.LoadoutNameMatchesWeapon(lo.label, w));
            if (mainW == null)
            {
                ThingDef bestRanged = null; float bestRangedScore = float.NegativeInfinity;
                ThingDef bestMelee = null; float bestMeleeScore = float.NegativeInfinity;
                foreach (ThingDef w in weps)
                {
                    LoadoutUtility.GetWeaponScore(w, out float sc, out _);
                    if (w.IsRangedWeapon && !w.IsMeleeWeapon) { if (sc > bestRangedScore) { bestRangedScore = sc; bestRanged = w; } }
                    else if (w.IsMeleeWeapon) { if (sc > bestMeleeScore) { bestMeleeScore = sc; bestMelee = w; } }
                }
                mainW = bestRanged ?? bestMelee;
            }
            if (mainW == null) return false;

            LoadoutUtility.GetWeaponScore(mainW, out score, out _);
            isRanged = mainW.IsRangedWeapon && !mainW.IsMeleeWeapon;
            main = mainW;
            group = LoadoutUtility.ClassifyWeaponGroup(main);
            return true;
        }

        // Frame-gated cache (2026-08-02 fix: this dialog's own DoWindowContents rebuilt this
        // from scratch - a full ThingsInGroup(Weapon)+ThingsInGroup(Apparel) map scan plus every colony
        // pawn's equipment/inventory/apparel - on EVERY call, unlike every other equivalent-cost lookup in
        // the mod, which is frame- or TTL-cached. Unity IMGUI dispatches DoWindowContents multiple times
        // per rendered frame (Layout pass, Repaint pass, once per input event), so this ran several times
        // per frame, not once - the single strongest lag suspect found tracing the "Assign Loadouts tab
        // feels laggy" report. Dialog_SquadSetup's own copy of this same call is already cheap by
        // comparison - it only runs once, in that dialog's constructor, never per-draw.
        private static Dictionary<ThingDef, int> ownedWeaponCountsCache;
        private static int ownedWeaponCountsCacheFrame = -1;

        internal static Dictionary<ThingDef, int> BuildOwnedWeaponCounts()
        {
            if (Time.frameCount == ownedWeaponCountsCacheFrame) return ownedWeaponCountsCache;
            ownedWeaponCountsCacheFrame = Time.frameCount;
            return ownedWeaponCountsCache = BuildOwnedWeaponCountsUncached();
        }

        private static Dictionary<ThingDef, int> BuildOwnedWeaponCountsUncached()
        {
            Dictionary<ThingDef, int> counts = new Dictionary<ThingDef, int>();

            // Deduplicate by Thing instance: an equipped/carried weapon can show up both in the map weapon lister and
            // in the pawn's equipment/inventory. Counting it twice made auto-assign think the single owned weapon was
            // free for two colonists at once (the same R-6 handed to two pawns, both "in stock").
            HashSet<Thing> seen = new HashSet<Thing>();
            void Add(Thing t)
            {
                if (t == null || t.def == null || LoadoutUtility.IsHiddenInFog(t) || !seen.Add(t)) return;
                if (!t.def.IsWeapon && !LoadoutUtility.IsShieldApparel(t.def)) return;
                counts[t.def] = (counts.TryGetValue(t.def, out int c) ? c : 0) + t.stackCount;
            }

            // Every colony map, not just the one in view - a weapon stored at an outpost is still owned.
            foreach (Map map in LoadoutUtility.PlayerMaps())
            {
                // Loose map things count even while marked "do not pick up" (2026-08-09, see
                // LoadoutUtility.IsMarkedDoNotPickUp) - a raid's dropped weapons are exactly the pool this
                // screen gets opened for. The "!" on the availability bar is what says they still need releasing.
                foreach (Thing t in map.listerThings.ThingsInGroup(ThingRequestGroup.Weapon))
                    Add(t);
                // Shields are apparel, not weapons - the weapon group above never catches them, so scan storage/ground
                // apparel too (plan section 6: shields need the same "already own one" check weapons get).
                foreach (Thing t in map.listerThings.ThingsInGroup(ThingRequestGroup.Apparel))
                    if (LoadoutUtility.IsShieldApparel(t.def)) Add(t);
            }
            foreach (Pawn p in LoadoutUtility.AllColonyPawns())
            {
                if (p.equipment != null && p.equipment.Primary != null) Add(p.equipment.Primary);
                if (p.inventory != null && p.inventory.innerContainer != null)
                    foreach (Thing t in p.inventory.innerContainer) Add(t);
                if (p.apparel != null && p.apparel.WornApparel != null)
                    foreach (Apparel a in p.apparel.WornApparel) if (LoadoutUtility.IsShieldApparel(a.def)) Add(a);
            }
            return counts;
        }

        // Weapons genuinely available for ONE pawn: free storage across every colony map plus what that pawn
        // already carries. Weapons other colonists are holding are excluded, so assigning a single pawn a
        // loadout whose weapon isn't free still offers to produce it.
        private static Dictionary<ThingDef, int> AvailableWeaponsForPawn(Pawn pawn)
        {
            Dictionary<ThingDef, int> counts = new Dictionary<ThingDef, int>();

            // Same Thing-instance dedup as BuildOwnedWeaponCounts above: a weapon can show up both in the map
            // lister and in a pawn's equipment/inventory, and without this a genuinely-owned weapon could be
            // double-counted, making a real shortfall look already covered.
            HashSet<Thing> seen = new HashSet<Thing>();
            void Add(Thing t)
            {
                if (t == null || t.def == null || LoadoutUtility.IsHiddenInFog(t) || !seen.Add(t)) return;
                if (!t.def.IsWeapon && !LoadoutUtility.IsShieldApparel(t.def)) return;
                counts[t.def] = (counts.TryGetValue(t.def, out int c) ? c : 0) + t.stackCount;
            }

            foreach (Map map in LoadoutUtility.PlayerMaps())
            {
                // Same "do not pick up" stock counts too, see BuildOwnedWeaponCounts above (2026-08-09).
                foreach (Thing t in map.listerThings.ThingsInGroup(ThingRequestGroup.Weapon))
                    Add(t);
                foreach (Thing t in map.listerThings.ThingsInGroup(ThingRequestGroup.Apparel))
                    if (LoadoutUtility.IsShieldApparel(t.def)) Add(t);
            }
            if (pawn != null)
            {
                if (pawn.equipment != null && pawn.equipment.Primary != null) Add(pawn.equipment.Primary);
                if (pawn.inventory != null && pawn.inventory.innerContainer != null)
                    foreach (Thing t in pawn.inventory.innerContainer) Add(t);
                if (pawn.apparel != null && pawn.apparel.WornApparel != null)
                    foreach (Apparel a in pawn.apparel.WornApparel) if (LoadoutUtility.IsShieldApparel(a.def)) Add(a);
            }
            return counts;
        }

        // ================================================================================================
        // EINE Rechnung fuer "wem fehlt welche Waffe" (2026-08-17)
        // ================================================================================================
        //
        // Es gab zwei, und sie haben sich widersprochen. Die Bereitschaftsanzeige sprach REIHUM an: jeder
        // Kolonist deckt zuerst aus dem, was er selbst traegt, dann aus dem freien Bestand auf den Karten -
        // wer leer ausgeht, ist gemeldet. Das Nachschub-Fenster rechnete stattdessen die GESAMTNACHFRAGE der
        // betroffenen Kolonisten gegen den GESAMTBESTAND der Kolonie (BuildOwnedWeaponCounts), und darin
        // steckt auch jede Waffe, die ein ANDERER Kolonist laengst in der Hand hat. Ergebnis im Test:
        // "Production gaps (2) - open Supply" oeffnete ein Fenster mit "Everything needed is already in
        // stock or in production". Dieselbe Fehlerklasse wie UnneededAmmoBillCandidates/UnneededAmmoEntries:
        // eine Frage, zwei Rechnungen.
        //
        // Diese Funktion IST die Rechnung. Die Warnung liest daraus, WER etwas nicht bekommt, das
        // Nachschub-Fenster liest daraus, WAS zu beschaffen ist - sie koennen sich nicht mehr widersprechen.
        // Bewusst NICHT enthalten: ob fuer den Fehlbestand schon ein Auftrag laeuft. Das ist eine zweite,
        // eigene Frage ("wird es schon behoben"), und beide Seiten beantworten sie weiterhin selbst - die
        // Warnung ueber ihre produced-Menge, das Fenster ueber die Stueckzahl seiner Zeilen.
        //
        // Bedarf = die Slots-Union des Ankers: Vorlage PLUS Zweitwaffen PLUS Packs (die haengen als
        // Extra-Eltern daran, siehe MultiParentUtility) - genau das, was der Kolonist laut Plan traegt.
        // ------------------------------------------------------------------------------------------------
        // 2026-08-17, Variante "beides umstellen": eine Zelle zeigt
        // nur, was fuer DIESEN Kolonisten wirklich frei ist.
        //
        // Dieselbe Rechnung deckt jetzt AUCH die Nicht-Waffen ab (Munition, Pack-Inhalte, generische Slots).
        // Vorher hatten die Zellen dafuer eine zweite Sicht: jede Zelle verglich ihren eigenen Bedarf mit dem
        // Bestand der GANZEN Kolonie, ohne abzuziehen, was andere Kolonisten schon beanspruchen - also sagten
        // bei 102 Medizin und 50 pro Zelle ALLE Zellen "nichts fehlt", auch wenn die Summe weit ueber 102 lag.
        // Bei Waffen war es eine Spur schaerfer: dort zaehlte sogar die Waffe mit, die ein anderer gerade in
        // der Hand hielt, und das Ergebnis haing zusaetzlich an der ZEICHENREIHENFOLGE.
        //
        // Jetzt gilt fuer beides dieselbe Reihenfolge (AllColonyPawns, nicht die Zeichenreihenfolge) und
        // dieselbe Regel: erst deckt jeder aus dem, was er selbst traegt, dann aus dem freien Bestand auf den
        // Karten. Was danach offen ist, ist die Fehlmenge dieses Kolonisten.
        //
        // Was BEWUSST unberuehrt bleibt: Dialog_SquadSetup rechnet fuer die Zuweisungs-VORSCHAU weiter gegen
        // den Gesamtbestand (BuildOwnedWeaponCounts). Das ist kein Widerspruch, sondern eine andere Frage -
        // beim Umverteilen ist die Waffe eines anderen sehr wohl greifbar, beim Anzeigen des Ist-Zustands
        // nicht.
        private sealed class LoadoutClaims
        {
            // Waffen und Schilde - was WeaponShortfallsByPawn seit dem Vormittag liefert, unveraendert.
            public readonly Dictionary<Pawn, Dictionary<ThingDef, int>> weaponMissing = new Dictionary<Pawn, Dictionary<ThingDef, int>>();
            // Alles andere aus derselben Slots-Union. Def, nicht ThingDef: ein generischer Slot
            // ("Any Medicine") hat keinen konkreten ThingDef und muss trotzdem beansprucht werden.
            public readonly Dictionary<Pawn, Dictionary<Def, int>> itemMissing = new Dictionary<Pawn, Dictionary<Def, int>>();
            // WELCHE Defs fuer diesen Kolonisten ueberhaupt durchgerechnet wurden. Ohne das koennte eine
            // Zelle "kein Eintrag in der Fehlliste" nicht von "gar nicht gefragt" unterscheiden - und eine
            // Vorschauzeile im Loadout-Editor, die noch gar nicht zugewiesen ist, laese sich als "vorhanden".
            public readonly Dictionary<Pawn, HashSet<Def>> evaluated = new Dictionary<Pawn, HashSet<Def>>();
            // Was dieser Kolonist von diesem Def wirklich BEKOMMT: eigenes Getragenes plus sein Anteil am
            // freien Bestand. Das ist die Zahl, mit der eine einzelne Zelle rechnen kann (siehe
            // ClaimedAllocation) - die Fehlmenge daneben ist die des ganzen Kolonisten und taugt dafuer nicht.
            public readonly Dictionary<Pawn, Dictionary<Def, int>> allocated = new Dictionary<Pawn, Dictionary<Def, int>>();

            public bool Knows(Pawn pawn, Def d) => pawn != null && d != null
                && evaluated.TryGetValue(pawn, out HashSet<Def> set) && set.Contains(d);
        }

        private static LoadoutClaims claimsCache;
        private static int claimsCacheVersion = -1;
        private static float claimsCacheAt = -99f;
        // Nicht mehr je Bild (2026-08-25, perf-cell-row-next): BuildClaimsUncached laeuft ueber jeden
        // Kolonisten samt Slots-Vereinigung und Inventar - bei 143 Hz rund 90 Mal je Sekunde fuer ein
        // pausiertes Spiel, und die Rechnung landete als erster Aufrufer in der Munitionszelle der ersten
        // Zeile (gemessen 86 ms/s, der groesste Posten der Zellenreihe). Zwei Sperren statt einer: der
        // Versionszaehler der zentralen Invalidierung (CombatReadiness.MutationVersion, jede Mutation des
        // Mods ruft sie) UND ein kurzes Hoechstalter - eine vergessene Mutationsstelle macht das Ergebnis
        // damit fuer 0,3 s alt, nie dauerhaft falsch. Im laufenden Spiel (Bereitschaftsanzeige) heisst
        // 0,3 s: ein gerade eingelagerter Stapel zaehlt einen Wimpernschlag spaeter.
        private const float ClaimsMaxAge = 0.3f;

        private static LoadoutClaims Claims()
        {
            float now = Time.realtimeSinceStartup;
            if (claimsCache != null && claimsCacheVersion == CombatReadiness.MutationVersion && now - claimsCacheAt < ClaimsMaxAge) return claimsCache;
            claimsCacheVersion = CombatReadiness.MutationVersion;
            claimsCacheAt = now;
            return claimsCache = BuildClaimsUncached();
        }

        internal static Dictionary<Pawn, Dictionary<ThingDef, int>> WeaponShortfallsByPawn() => Claims().weaponMissing;

        // Wie viel von diesem Gegenstand dieser Kolonist WIRKLICH bekommt, nachdem alle anderen ihren
        // Anteil genommen haben - eigenes Getragenes plus der Anteil aus dem freien Bestand. -1 heisst
        // "dazu sagt die Rechnung nichts" (keine Vorlage, oder der Gegenstand steht nicht darin), dann
        // bleibt es beim bisherigen Weg.
        //
        // Die ZUTEILUNG, nicht die Fehlmenge (korrigiert 2026-08-17 abends, im Test gefunden). Vorher
        // gab diese Funktion die Fehlmenge des KOLONISTEN zurueck, und BuildAvailabilitySegments schrieb sie
        // direkt in jede Zelle. Damit sagten zwei Zellen desselben Kolonisten fuer denselben Gegenstand
        // wieder wortgleich dasselbe - genau der Fehler, der am 2026-08-16 schon einmal behoben war
        // ("die 1-Stueck-Zelle meldete denselben Mangel wie die 5-Stueck-Zelle").
        // Befund: die ZAHLEN zu Medicine waren identisch und bezogen sich auf den
        // Gesamtbedarf des Kolonisten (13 = 12 aus dem Pack + 1 aus Sonstiges), nicht auf den Anteil der
        // jeweiligen Zelle.
        // Mit der Zuteilung rechnet jede Zelle wieder selbst: fehlt = ihr eigener Bedarf minus dem, was der
        // Kolonist ueberhaupt bekommt. Die 1-Stueck-Zelle ist damit gedeckt, die 12-Stueck-Zelle nicht -
        // und der Bezugspunkt ist trotzdem der Kolonist und nicht mehr die ganze Kolonie.
        internal static int ClaimedAllocation(Pawn pawn, Def d)
        {
            if (pawn == null || d == null) return -1;
            LoadoutClaims claims = Claims();
            if (!claims.Knows(pawn, d)) return -1;
            return claims.allocated.TryGetValue(pawn, out Dictionary<Def, int> al)
                && al.TryGetValue(d, out int n) ? n : 0;
        }

        private static LoadoutClaims BuildClaimsUncached()
        {
            // Der Pool ist der FREIE Bestand - Boden und Lager auf allen Spielerkarten. Was ein Kolonist am
            // Mann hat, steht bewusst NICHT darin (es haengt an ihm, nicht an der Karte) und wird unten pro
            // Kolonist einzeln abgezogen; genau darin unterscheidet sich diese Rechnung von
            // BuildOwnedWeaponCounts, das alles in einen Topf wirft. "Nicht aufsammeln" zaehlt mit, gleiche
            // Begruendung wie dort (2026-08-09).
            Dictionary<ThingDef, int> pool = new Dictionary<ThingDef, int>();
            HashSet<Thing> seen = new HashSet<Thing>();
            void AddPool(Thing t)
            {
                if (t == null || t.def == null || LoadoutUtility.IsHiddenInFog(t) || !seen.Add(t)) return;
                if (!t.def.IsWeapon && !LoadoutUtility.IsShieldApparel(t.def)) return;
                pool[t.def] = (pool.TryGetValue(t.def, out int c) ? c : 0) + t.stackCount;
            }
            foreach (Map map in LoadoutUtility.PlayerMaps())
            {
                foreach (Thing t in map.listerThings.ThingsInGroup(ThingRequestGroup.Weapon)) AddPool(t);
                // Schilde sind Apparel, die Waffengruppe oben findet sie nie - gleiche Sonderbehandlung wie
                // in BuildOwnedWeaponCounts.
                foreach (Thing t in map.listerThings.ThingsInGroup(ThingRequestGroup.Apparel))
                    if (LoadoutUtility.IsShieldApparel(t.def)) AddPool(t);
            }

            // Der Topf fuer alles, was KEINE Waffe und kein Schild ist - Munition, Medizin, Rationen,
            // Bauteile, generische Slots. Erst beim ersten Nachfragen pro Def gefuellt, damit hier nicht
            // die halbe Gegenstandsdatenbank durchgezaehlt wird, sondern nur, was wirklich jemand
            // zugewiesen bekommen hat. Lager UND Boden, wie ueberall im Mod.
            // Was Kolonisten am Mann haben, steht bewusst nicht darin - das wird unten pro Kolonist
            // einzeln abgezogen, genau wie beim Waffentopf darueber.
            // Referenzvergleich fuer JEDEN Def-Schluessel dieser Rechnung (2026-08-25): CEs generische Defs
            // tragen ohne den Startfix in CEReloadedMain alle Hash 0 und gelten als gleich - siehe dort. Mit
            // Standardvergleich fielen "Any ammo for M1 Garand" und "Any Perishable Meal" auf EINEN Schluessel,
            // der Nahrungstopf war nach dem ersten Kolonisten leer, und jeder danach las "fehlt" (mit
            // vier Screenshots belegt). Der Startfix behebt die Wurzel; der Vergleicher hier haelt die
            // Rechnung auch dann richtig, wenn ein anderer Mod solche Defs spaeter noch einmal ohne Hash anlegt.
            Dictionary<Def, int> itemPool = new Dictionary<Def, int>(LoadoutUtility.DefRefComparer.Instance);
            int ItemFree(Def d)
            {
                if (itemPool.TryGetValue(d, out int have)) return have;
                // StoredStockCount statt IngredientStockCount (2026-08-18): der Pack-Topf enthaelt auch
                // Dinge, die RimWorld nicht als Rohstoff fuehrt (Schilde, Ferngläser, Wurfsachen) - fuer die
                // liefert der Rohstoffzaehler immer 0, und im Regal liegend werden sie zusaetzlich vom
                // Bodenzaehler uebersprungen. Siehe LoadoutUtility.StoredStockCount.
                int stock = d is ThingDef td2
                    ? LoadoutUtility.StoredStockCount(td2) + LoadoutUtility.GroundStockCount(td2)
                    : LoadoutUtility.GenericStockCount(d as LoadoutGenericDef);
                itemPool[d] = stock;
                return stock;
            }

            var claims = new LoadoutClaims();
            foreach (Pawn pawn in LoadoutUtility.AllColonyPawns())
            {
                Loadout lo = pawn.GetLoadout();
                // Aufgeloest entschieden, roh gelesen - dieselbe Unterscheidung wie in
                // CombatReadinessAlerts.Recompute: ob ueberhaupt eine Vorlage haengt, sagt DisplayLoadout,
                // was er traegt, steht in der Slots-Union des Ankers.
                Loadout display = AmmoAssignmentUtility.DisplayLoadout(lo);
                // Kein Abbruch mehr bei display.defaultLoadout (2026-08-25): ein Anker ueber CEs
                // None-Loadout traegt Packs und seit heute auch Zweitwaffen - die gehoeren in die Reihum-
                // Rechnung wie bei jedem anderen. Ein nacktes None ohne Anker hat leere Slots und faellt
                // ueber `needs.Count == 0 && itemNeeds.Count == 0` unten ohnehin heraus.
                if (lo == null || lo.Slots == null || display == null) continue;

                Dictionary<ThingDef, int> needs = new Dictionary<ThingDef, int>();
                Dictionary<Def, int> itemNeeds = new Dictionary<Def, int>();
                foreach (LoadoutSlot slot in lo.Slots)
                {
                    ThingDef w = slot.thingDef;
                    int want = Mathf.Max(1, slot.count);
                    if (w == null)
                    {
                        // Generischer Slot ("Any Medicine", "Any Meal") - kein konkreter ThingDef, aber
                        // sehr wohl ein Anspruch auf den gemeinsamen Bestand seiner Familie.
                        // Generische MUNITION ("Any ammo for X") bleibt draussen (2026-08-25): ihr Bestand
                        // wird ueber ResolveGenericAmmo/ComputeAmmoAvailability gerechnet, nicht ueber den
                        // Gegenstandstopf - EnsureGenericSupplyDefs kennt sie absichtlich nicht, GenericStockCount
                        // lieferte fuer sie immer 0, also ein garantiert falsches "fehlt".
                        if (slot.genericDef != null && !LoadoutUtility.IsGenericAmmoDef(slot.genericDef))
                            itemNeeds[slot.genericDef] = (itemNeeds.TryGetValue(slot.genericDef, out int gc) ? gc : 0) + want;
                        continue;
                    }
                    // Genau das Praedikat von ComputeMissingSplit.FoldSlots - Waffen und Schilde, keine
                    // Utility-Waffen (fuer die gibt es auch im Nachschub-Fenster nichts zu tun).
                    if (!LoadoutUtility.IsShieldApparel(w) && (!w.IsWeapon || LoadoutUtility.IsUtilityWeapon(w)))
                    {
                        // Utility-Waffen bleiben AUSSEN VOR - in beiden Toepfen. Sie sind Waffen, stehen
                        // also nicht im Gegenstandsbestand (resourceCounter kennt keine Waffen), und ein
                        // Anspruch gegen einen leeren Topf waere ein garantiert falsches Rot. Fuer sie
                        // gilt weiter der alte Weg in SingleFulfillStatus.
                        if (!w.IsWeapon && !LoadoutUtility.IsShieldApparel(w))
                            itemNeeds[w] = (itemNeeds.TryGetValue(w, out int ic) ? ic : 0) + want;
                        continue;
                    }
                    needs[w] = (needs.TryGetValue(w, out int c) ? c : 0) + want;
                }
                if (needs.Count == 0 && itemNeeds.Count == 0) continue;

                HashSet<Def> evaluated = new HashSet<Def>(LoadoutUtility.DefRefComparer.Instance);
                Dictionary<Def, int> allocated = new Dictionary<Def, int>(LoadoutUtility.DefRefComparer.Instance);

                Dictionary<ThingDef, int> missing = null;
                foreach (var kv in needs)
                {
                    evaluated.Add(kv.Key);
                    int carried = CarriedCountOf(pawn, kv.Key);
                    int open = kv.Value - carried;
                    int got = Mathf.Min(carried, kv.Value);
                    if (open > 0 && pool.TryGetValue(kv.Key, out int free) && free > 0)
                    {
                        int take = Mathf.Min(free, open);
                        pool[kv.Key] = free - take;
                        open -= take;
                        got += take;
                    }
                    allocated[kv.Key] = got;
                    if (open <= 0) continue;
                    if (missing == null) missing = new Dictionary<ThingDef, int>();
                    missing[kv.Key] = open;
                }
                if (missing != null) claims.weaponMissing[pawn] = missing;

                Dictionary<Def, int> itemMissing = null;
                foreach (var kv in itemNeeds)
                {
                    evaluated.Add(kv.Key);
                    int carried = kv.Key is ThingDef itd
                        ? LoadoutUtility.PawnCarriedCount(pawn, itd)
                        : LoadoutUtility.PawnCarriedGenericCount(pawn, kv.Key as LoadoutGenericDef);
                    int open = kv.Value - carried;
                    int got = Mathf.Min(carried, kv.Value);
                    if (open > 0)
                    {
                        int freeItem = ItemFree(kv.Key);
                        if (freeItem > 0)
                        {
                            int take = Mathf.Min(freeItem, open);
                            itemPool[kv.Key] = freeItem - take;
                            open -= take;
                            got += take;
                        }
                    }
                    allocated[kv.Key] = got;
                    if (open <= 0) continue;
                    if (itemMissing == null) itemMissing = new Dictionary<Def, int>(LoadoutUtility.DefRefComparer.Instance);
                    itemMissing[kv.Key] = open;
                }
                if (itemMissing != null) claims.itemMissing[pawn] = itemMissing;

                claims.evaluated[pawn] = evaluated;
                claims.allocated[pawn] = allocated;
            }
            return claims;
        }

        // Was dieser Kolonist von diesem Def wirklich bei sich hat - Hand, Rucksack und (fuer Schilde)
        // am Koerper. Getragene Ruestung zaehlt mit, sonst laese ein getragenes Schild als fehlend.
        private static int CarriedCountOf(Pawn pawn, ThingDef def)
        {
            int c = 0;
            if (pawn == null || def == null) return 0;
            if (pawn.equipment != null && pawn.equipment.Primary != null && pawn.equipment.Primary.def == def)
                c += pawn.equipment.Primary.stackCount;
            if (pawn.inventory != null && pawn.inventory.innerContainer != null)
                foreach (Thing t in pawn.inventory.innerContainer) if (t.def == def) c += t.stackCount;
            if (pawn.apparel != null && pawn.apparel.WornApparel != null)
                foreach (Apparel a in pawn.apparel.WornApparel) if (a.def == def) c += a.stackCount;
            return c;
        }

        private void DrawPawnCard(Rect rect, Pawn pawn)
        {
            DrawPawnCardInner(rect, pawn);
        }

        private void DrawPawnCardInner(Rect rect, Pawn pawn)
        {
            Widgets.DrawMenuSection(rect);
            HandleDropTarget(rect, pawn);

            // Portrait
            Rect portraitRect = new Rect(rect.x + 5f, rect.y + 5f, 60f, 60f);
            GUI.color = new Color(1f, 1f, 1f, 0.2f);
            Widgets.DrawHighlight(portraitRect);
            GUI.color = Color.white;
            
            RenderTexture portrait = PortraitsCache.Get(pawn, new Vector2(60f, 60f), Rot4.South, default(Vector3), 1f, true, true, false, false, null, null, false);
            if (portrait != null)
            {
                GUI.DrawTexture(portraitRect, portrait);
            }


            // Save as Role - top-right corner, out of the way of the name/portrait/edit-gear click targets.
            // Opens Role Mode with a pre-filled draft (see Dialog_GenerateLoadouts.StartRoleDraftFromPawn)
            // instead of instantly baking and saving - the player reviews/renames/adjusts factors in the
            // already-existing Role Mode preview and explicitly presses Create there.
            // 120f statt 90f (2026-08-26: "Als Rolle speichern" wurde von ButtonText in zwei
            // gequetschte Kleinstzeilen umgebrochen). Der Namenszeile daneben gehoeren die 30px weniger -
            // sie kuerzt ohnehin sichtbar. ButtonTextFit faengt Sprachen ab, denen auch 120 nicht reichen.
            Rect saveRoleRect = new Rect(rect.xMax - 124f, rect.y + 3f, 120f, 22f);
            Loadout cardAssigned = pawn.GetLoadout();
            // DisplayLoadout, not the raw assignment (2026-08-01 fix, adopting the gating the old
            // Dialog_LoadoutEditor already had right before that dialog was removed) - a Role-assigned pawn
            // still has a personal "[Pawn-Loadout]" anchor (AssignTemplate wraps every assignment in one),
            // so checking the raw assignment alone can't
            // tell a Role apart from an ad-hoc one; this used to let "Save as Role" fire on a Role pawn too,
            // producing a doubled "[Role] [Role] X" draft name.
            Loadout cardDisplay = AmmoAssignmentUtility.DisplayLoadout(cardAssigned);
            bool cardIsRole = RoleUtility.IsRoleLoadout(cardDisplay);
            // cardDisplay statt cardAssigned auch hier: der rohe Wert ist bei personalisierten Kolonisten der
            // Anker, und der ist nie das Default - "Als Rolle speichern" erschien dadurch auch bei einem
            // Kolonisten, unter dessen Anker gar keine Vorlage mehr haengt.
            // AUCH ein reiner Pack-Traeger hat etwas zu speichern (2026-08-16): auch ein Pazifist mit
            // zugeordnetem Equipment Pack hat genug fuer eine Rolle, also "Save as role" auch fuer ihn.
            // Er irrt sich nicht: eine Rolle NUR aus einem Pack ist ausdruecklich vorgesehen - CommitRole
            // hat am 2026-08-09 genau dafuer einen eigenen Zweig bekommen ("sonst gilt eine Rolle, die NUR
            // aus einem Equipment Pack besteht, faelschlich als leer"). Die Bedingung hier kannte diesen
            // Fall nur nicht und fragte allein nach der Waffen-Vorlage.
            RowInfo rowInfo = GetRowInfo(pawn);
            bool cardHasPacks = rowInfo.hasPacks;
            bool cardHasLoadout = (cardDisplay != null && !cardDisplay.defaultLoadout && !cardIsRole) || (cardHasPacks && !cardIsRole);
            if (cardIsRole)
            {
                // Traegt er schon eine Rolle, ist "als Rolle speichern" die falsche Frage - dann will man
                // an die Rolle selbst (2026-08-16). Springt in den Rollen-Reiter und waehlt
                // GENAU diese Rolle aus, statt einen Entwurf aus seinen Teilen zu bauen.
                if (LoadoutUtility.ButtonTextFit(saveRoleRect, "CER_AssignLoadouts_EditRoleBtn".Translate()) && parentWindow != null)
                {
                    // Der ROHE Name, nicht die Anzeigefassung: die Rolle heisst intern "[Role] <name>",
                    // und genau dieses <name> steht im LoadoutRole-Objekt. DisplayLabel haengt zusaetzlich
                    // das konfigurierbare "R| " davor - damit wuerde die Suche ins Leere laufen.
                    string roleName = (cardDisplay.label ?? "").Substring(
                        (cardDisplay.label ?? "").StartsWith(RoleUtility.RoleMarker) ? RoleUtility.RoleMarker.Length : 0).Trim();
                    parentWindow.LoadoutsDialog.FocusExistingRole(roleName);
                    parentWindow.JumpToLoadouts();
                }
            }
            else if (cardHasLoadout)
            {
                if (LoadoutUtility.ButtonTextFit(saveRoleRect, "CER_AssignLoadouts_SaveAsRoleBtn".Translate()) && parentWindow != null)
                {
                    parentWindow.LoadoutsDialog.StartRoleDraftFromPawn(pawn, AmmoAssignmentUtility.ComposedDisplayLabel(pawn));
                    parentWindow.JumpToLoadouts();
                }
            }
            else
            {
                // Plain dimmed label, no button box, when there's nothing to save (2026-07-30:
                // alpha-tinting the WHOLE button - box, bevel and all - down to 35% made the button's own
                // beveled-edge highlight visually separate from the now-faint text, reading as a strikethrough.
                // House style: an inactive element should just look plain/dim, never struck through.
                GUI.color = new Color(1f, 1f, 1f, 0.35f);
                Text.Anchor = TextAnchor.MiddleCenter;
                // Dieselbe Einpassung wie die Knopf-Zweige darueber, nur ohne Knopfrahmen.
                GameFont prevRoleFont = Text.Font;
                bool prevRoleWrap = Text.WordWrap;
                Text.WordWrap = false;
                string dimmedRoleLbl = "CER_AssignLoadouts_SaveAsRoleBtn".Translate();
                if (Text.CalcSize(dimmedRoleLbl).x > saveRoleRect.width - 4f) Text.Font = GameFont.Tiny;
                Widgets.Label(saveRoleRect, dimmedRoleLbl.Truncate(saveRoleRect.width - 4f));
                Text.WordWrap = prevRoleWrap;
                Text.Font = prevRoleFont;
                Text.Anchor = TextAnchor.UpperLeft;
                GUI.color = Color.white;
            }
            // Der Hinweis muss zum Knopf passen (2026-08-16, beim Nachsehen selbst gefunden): seit dem
            // Edit-Role-Zweig oben stand am NEUEN Knopf weiterhin "hier gibt es nichts zu speichern" -
            // cardHasLoadout ist bei einem Rollen-Traeger absichtlich false. Ein Knopf, dessen Hinweis das
            // Gegenteil dessen sagt, was er tut, ist schlimmer als gar keiner.
            if (LoadoutUtility.TipWanted(saveRoleRect)) TooltipHandler.TipRegion(saveRoleRect, cardIsRole
                ? "CER_AssignLoadouts_EditRoleTip".Translate()
                : (cardHasLoadout ? "CER_AssignLoadouts_SaveRoleTipHas".Translate()
                                  : "CER_AssignLoadouts_SaveRoleTipNone".Translate()));

            // Pencil/magnifier removed (2026-08-01, closes a gap in the "Barebones editor -> Other button"
            // request from earlier the same day): the original ask was to drop this button in BOTH compact
            // AND card view, replaced by the Other-item cell in the bottom row (DrawOtherItemsCell, already
            // shipped here) - only compact view actually got it, card view's own pencil (which opened the
            // barebones Dialog_LoadoutEditor, a dialog since removed entirely) was left in place by mistake
            // ("barebone editor ist immer noch da"). Ammo/Pack/Sidearm/Other now cover
            // everything that editor used to for an ad-hoc pawn; a Role pawn's read-only banner has no
            // equivalent here anyway (Roles are edited in Role Mode, not per-pawn).

            // Name - width must clear the "Save as Role" button in the card's own top-right corner (94px
            // reserved from rect.xMax: 4px margin + 90px button - was 124px before the pencil above it was
            // removed) - otherwise a long name overlaps the button instead of truncating.
            Rect nameRect = new Rect(rect.x + 75f, rect.y + 5f, rect.width - 80f - 124f, 24f);
            Text.Font = GameFont.Small;
            string fullName = pawn.Name != null ? pawn.Name.ToStringFull : pawn.LabelShort;
            Widgets.Label(nameRect, fullName.Truncate(nameRect.width));
            TooltipHandler.TipRegion(nameRect, fullName);

            // Equipment tooltip lives on the portrait only, so it doesn't stack on top of the traits tooltip below.
            Rect hoverRect = new Rect(rect.x + 5f, rect.y + 5f, 60f, 60f);
            TooltipHandler.TipRegion(hoverRect, new TipSignal(() => GetEquipmentTooltip(pawn) + "CER_AssignLoadouts_ClickToViewEditGear".Translate(), pawn.GetHashCode() * 37));

            // Clicking the pawn opens the gear editor IN FRONT of this window - selecting the pawn on the map would
            // put the vanilla Gear tab uselessly behind the fullscreen tabs dialog.
            if (Widgets.ButtonInvisible(hoverRect) || Widgets.ButtonInvisible(nameRect))
                Find.WindowStack.Add(new Dialog_PawnGear(pawn));

            // Traits (16f: the Tiny-font line must not touch the ranged bar starting at y+42)
            Rect traitsRect = new Rect(rect.x + 75f, rect.y + 25f, rect.width - 80f, 16f);
            Text.Font = GameFont.Tiny;
            GUI.color = Color.gray;
            Widgets.Label(traitsRect, traitStrings[pawn].Truncate(traitsRect.width));
            if (!string.IsNullOrEmpty(traitTooltipStrings[pawn]))
            {
                TooltipHandler.TipRegion(traitsRect, traitTooltipStrings[pawn]);
            }
            GUI.color = Color.white;

            // Scores
            float rScore = rangedScores[pawn];
            float mScore = meleeScores[pawn];
            float maxScore = SkillRecord.MaxLevel * 10f;

            Rect rBarRect = new Rect(rect.x + 75f, rect.y + 42f, rect.width - 80f, 16f);
            Rect mBarRect = new Rect(rect.x + 75f, rect.y + 60f, rect.width - 80f, 16f);
            
            RowLabels cardLabels = GetRowLabels(pawn);   // vorformatiert, siehe GetRowLabels
            DrawScoreBarPrebuilt(rBarRect, rScore, maxScore, cardLabels.rangedCard);
            DrawScoreBarPrebuilt(mBarRect, mScore, maxScore, cardLabels.meleeCard);
            
            Rect scoreTooltipRect = new Rect(rect.x + 75f, rect.y + 42f, rect.width - 80f, 34f);
            if (LoadoutUtility.TipWanted(scoreTooltipRect))
                TooltipHandler.TipRegion(scoreTooltipRect, "CER_AssignLoadouts_ScoreCalcTooltip".Translate((maxScore / 10f).ToString()));

            // Loadout Assignment (rows sit tight under the skill bars - dead vertical space just cost card slots)
            Rect loadoutLabelRect = new Rect(rect.x + 5f, rect.y + 79f, 60f, 24f);
            Widgets.Label(loadoutLabelRect, "CER_AssignLoadouts_WeaponLabel".Translate());

            // Unwrap any hidden per-pawn "extra ammo" leaf (AmmoAssignmentUtility) straight away, so every
            // read of currentLoadout below - label, pack cell, edit button, main/side icons - keeps seeing
            // the real weapon template exactly as before, regardless of whether this pawn has one. Mass/bulk
            // still reads the RAW assignment (assignedLoadout) - that leaf's own Slots union is what
            // actually adds up to everything this pawn carries, extra ammo included.
            Loadout assignedLoadout = pawn.GetLoadout();
            Loadout currentLoadout = AmmoAssignmentUtility.DisplayLoadout(assignedLoadout);
            bool hasLoadout = currentLoadout != null && !currentLoadout.defaultLoadout;
            // CellLabel, nicht DisplayLabel (2026-08-13) - dasselbe wie in der kompakten Zeile: das kurze
            // Praefix wiederholt nur das Symbol daneben. Im Auswahlmenue (OpenLoadoutPickMenu) steht
            // weiterhin der volle Name.
            string btnLabel = hasLoadout ? LoadoutUtility.CellLabel(currentLoadout) : "CER_AssignLoadouts_NoneLoadout".Translate().ToString();

            // The loadout's main weapon + sidearm, shown as icons before the name (main weapon always first).
            // Aus GetRowInfo, siehe dort - dieselbe Kuer wie in der kompakten Zeile, jetzt einmal pro Bild.
            ThingDef cardMainW = rowInfo.mainW, cardSideW = rowInfo.sideW;

            // Non-combatants (pacifists, babies - the same CannotFight rule the solver uses) can't be handed a
            // loadout manually either: the menu only ever offers "None", so a stray earlier assignment can still
            // be cleared but nothing new can be given.
            bool cannotFight = LoadoutUtility.CannotFight(pawn);

            // Reclaims the space the magnifier button used to sit in here - it moved up next to Save as Role.
            Rect loadoutBtnRect = new Rect(rect.x + 75f, rect.y + 79f, rect.width - 75f - 5f - 5f, 24f);
            // The status dot (drawn last, below) claims the button's own top-right corner - its hit-test
            // rect must BLOCK the button underneath there (no highlight, no click), not just sit visually
            // on top of it, so the invisible click-rect is shrunk to exclude that corner entirely. Icon/
            // label content below still uses the full loadoutBtnRect (unaffected, no visual shift).
            Rect btnHitRect = new Rect(loadoutBtnRect.x, loadoutBtnRect.y, loadoutBtnRect.width - DotReserveW, loadoutBtnRect.height);
            // No longer dimmed for cannotFight pawns - a pacifist can still carry an Equipment Pack (see
            // DrawEquipmentPackCell), so greying out the whole row read as "nothing works here" when
            // something usually does. The tooltip below still explains why no WEAPON can be assigned.
            // Blautoene wie in CEs eigener Loadout-Spalte - dunkleres Blau = Rolle, blasses = eigene
            // Waffen-Vorlage (2026-08-13: die Blau-Abstufungen unterscheiden Rollen und Waffen, so wie
            // beim Vanilla-"Current loadout" draussen).
            // Am 2026-08-15 erneut aufgefallen: die KOMPAKTE Ansicht hatte es seit dem 13.08., die
            // KARTENANSICHT nie - der
            // Fix von damals hat nur eine der beiden Zeichenstellen erwischt. Dieselben zwei Konstanten,
            // dieselbe Bedingung, damit die zwei Ansichten nicht wieder auseinanderlaufen koennen.
            // TintedButton statt DrawAtlas+ButtonText (2026-08-16, im Test gemessen): die zweite
            // Haelfte hat die erste uebermalt, die Einfaerbung war deshalb NIE zu sehen - in beiden
            // Ansichten und seit dem ersten Tag. Siehe LoadoutUtility.TintedButton.
            // LoadoutTintFor statt hasLoadout+cardIsRole (2026-08-16: ein in CE erstelltes Vanilla-Loadout
            // hatte einen Blauton am Knopf, den es nicht haben soll): die Bedingung hier faerbte
            // JEDES Loadout ein, nicht nur unsere. Der Fehler ist aelter als der Sichtbarkeits-Fix von
            // heute Mittag - er war bis dahin nur nicht zu sehen. Jetzt dieselbe Regel wie in CEs eigener
            // Spalte, aus einer Funktion.
            bool loadoutClicked = LoadoutUtility.TintedButton(btnHitRect, LoadoutUtility.LoadoutTintFor(cardAssigned));
            {
                float ix = loadoutBtnRect.x + 5f;
                if (cardMainW != null) { Widgets.ThingIcon(new Rect(ix, loadoutBtnRect.y + 2f, 20f, 20f), cardMainW); ix += 22f; }
                if (cardSideW != null) { Widgets.ThingIcon(new Rect(ix, loadoutBtnRect.y + 2f, 20f, 20f), cardSideW); ix += 22f; }
                bool hasIcons = cardMainW != null;
                Text.Anchor = hasIcons ? TextAnchor.MiddleLeft : TextAnchor.MiddleCenter;
                Rect lblRect = LoadoutBtnLabelRect(loadoutBtnRect, ix, hasIcons);
                Widgets.Label(lblRect, btnLabel.Truncate(lblRect.width));
                Text.Anchor = TextAnchor.UpperLeft;
                GUI.color = Color.white;
                // The loadout button is also the drag handle for swapping loadouts between two colonists - dragging
                // the weapon selector itself reads more naturally than dragging the portrait.
                if (!cannotFight && hasLoadout) HandleDragHandle(loadoutBtnRect, pawn);
                // Same "dot wins" tooltip-exclusion as every other status dot in this dialog - only register
                // this wider tooltip while the mouse is NOT over the weapon dot's own corner.
                Rect weaponDotRectCard = WeaponDotRect(loadoutBtnRect);
                if (!Mouse.IsOver(weaponDotRectCard))
                {
                    // Die ganze Kette hinter die Maus-Pruefung: LoadoutContentsTooltip laeuft ueber jeden
                    // Slot der Vorlage und lief so pro Karte pro Bild (LoadoutUtility.TipWanted).
                    if (LoadoutUtility.TipWanted(loadoutBtnRect))
                    {
                        if (dragPawn != null && dragPawn != pawn)
                            TooltipHandler.TipRegion(loadoutBtnRect, "CER_AssignLoadouts_DropSwapTip".Translate(dragPawn.LabelShortCap));
                        else if (cannotFight) TooltipHandler.TipRegion(loadoutBtnRect, "CER_AssignLoadouts_CannotFightTip".Translate()
                            + (hasLoadout ? "CER_AssignLoadouts_CannotFightClearSuffix".Translate().ToString() : ""));
                        else if (hasLoadout) TooltipHandler.TipRegion(loadoutBtnRect, LoadoutUtility.LoadoutContentsTooltip(currentLoadout)
                            + "CER_AssignLoadouts_DragToSwapSuffix".Translate());
                    }
                }
            }
            if (loadoutClicked) OpenLoadoutPickMenu(pawn, currentLoadout, hasLoadout, cannotFight);
            // Weapon status: same corner-dot convention as the Ammo/Pack/Sidearm cells, on the loadout button
            // itself - replaces the old separate "Equipped: <weapon>" detail row entirely (what's actually in
            // the pawn's hands right now, if different from the plan, now lives in this dot's own tooltip).
            DrawWeaponStatusDot(loadoutBtnRect, pawn, cardMainW);

            // Loadout mass & bulk vs. THIS pawn's carry capacity, drawn as fillable bars (same style as Generate
            // Loadouts). Always shown - with no loadout the load is simply 0. Generic "Any …" entries use CE's own
            // category averages. Reads assignedLoadout (not the unwrapped currentLoadout) so any extra ammo
            // counts toward the total too.
            // Masse/Sperrigkeit kommen aus GetRowInfo (Frame-Cache statt einmal pro Durchgang) - die Rechnung
            // selbst ist unveraendert, inklusive der Regel, dass sie am ANKER haengt und nicht an der
            // entpackten Waffenvorlage (2026-08-14, im Test als Nebenbefund gemessen: ein Kolonist
            // mit DREI direkt angehaengten Packs zeigte sonst 0.0/32.0 kg, weil die Waffen-Vorlage eines
            // reinen Pack-Traegers das "None"-Objekt ist. Schwer wiegt es, weil die Ueberladungs-Warnung an
            // genau dieser Zahl haengt: steht dort 0, warnt niemand).
            float lMass = rowInfo.mass, lBulk = rowInfo.bulk;
            GetCapacities(pawn, out float capMass, out float capBulk);

            // Die beiden Tooltip-Texte stehen jetzt UNTEN in ihrer eigenen Bedingung (siehe
            // LoadoutUtility.TipWanted) - sie sahen hier harmlos aus und liefen trotzdem pro Karte pro Bild
            // durch RimWorlds Uebersetzung.

            // Right under the loadout row - used to sit a line lower, sharing space with the old "Equipped:"
            // detail row (now gone, see DrawWeaponStatusDot above).
            float by = rect.y + 105f;
            float halfW = (rect.width - 12f - 10f) / 2f;
            if (cardMassCaption == null)
            {
                cardMassCaption = "CER_AssignLoadouts_MassLabel".Translate();
                cardBulkCaption = "CER_AssignLoadouts_BulkLabel".Translate();
                cardMassCaptionW = Text.CalcSize(cardMassCaption).x + 2f;
                cardBulkCaptionW = Text.CalcSize(cardBulkCaption).x + 2f;
            }
            // Die Beschriftung bekommt, was sie braucht, der Balken den Rest - mit Deckel bei 55% der
            // Haelfte, damit auch eine sehr lange Uebersetzung nie den ganzen Balken frisst (LabelCell
            // kuerzt dann sichtbar statt stumpf zu clippen).
            float massCapW = Mathf.Min(cardMassCaptionW, halfW * 0.55f);
            LoadoutUtility.LabelCell(new Rect(rect.x + 6f, by, massCapW, 20f), cardMassCaption);
            Rect massBar = new Rect(rect.x + 6f + massCapW + 2f, by + 1f, halfW - massCapW - 2f, 18f);
            bool massOver = DrawCapacityBar(massBar, lMass, capMass, GetRowLabels(pawn).mass, pawn, false, out var massBoosters);   // vorformatiert, siehe GetRowLabels
            Rect massTipRect = new Rect(rect.x + 6f, by, halfW, 20f);
            if (LoadoutUtility.TipWanted(massTipRect))
                TooltipHandler.TipRegion(massTipRect, WithOverCapNote(LoadoutUtility.CarryCapacityTooltip(pawn, false, lMass, capMass, massBoosters), massOver));

            float bx = rect.x + 6f + halfW + 10f;
            float bulkCapW = Mathf.Min(cardBulkCaptionW, halfW * 0.55f);
            LoadoutUtility.LabelCell(new Rect(bx, by, bulkCapW, 20f), cardBulkCaption);
            Rect bulkBar = new Rect(bx + bulkCapW + 2f, by + 1f, halfW - bulkCapW - 2f, 18f);
            bool bulkOver = DrawCapacityBar(bulkBar, lBulk, capBulk, GetRowLabels(pawn).bulk, pawn, true, out var bulkBoosters);
            Rect bulkTipRect = new Rect(bx, by, halfW, 20f);
            if (LoadoutUtility.TipWanted(bulkTipRect))
            {
                string bulkTip = LoadoutUtility.CarryCapacityTooltip(pawn, true, lBulk, capBulk, bulkBoosters);
                TooltipHandler.TipRegion(bulkTipRect, WithOverCapNote(bulkTip, bulkOver));
            }

            // Ammo/Pack/Sidearm/Other cells always all four present now, each cell showing "None"/dash itself
            // when there's nothing yet - a button that only sometimes exists (gated on whether ANY Equipment
            // Pack or sidearm-candidate loadout exists ANYWHERE in the game) read as broken/inconsistent, not
            // as "nothing to show here". Icon-only, same as compact view - these cells are too narrow for a
            // readable text label (it just truncated to "Ammo: .30-06 Sp...", "Pack: E| Ba...", never
            // actually readable), the icon carries the same info at a glance and full detail is still one
            // hover away in each cell's own tooltip. Four equal-width cells now (was three) - Other joined
            // the row alongside the existing three (2026-08-01) rather than replacing any of them, so all
            // four shrank proportionally to keep the row's own total width unchanged; DotReserveW/paste-strip/
            // overflow-badge math inside each cell is unaffected by width alone (same formulas as compact
            // view's already-narrower 68px cells, which ship fine today), just re-centers within less room.
            {
                by += 22f;
                float fullW = rect.width - 12f;
                // Die Einfuege-Spalte ganz rechts kostet nur dann Breite, wenn wirklich etwas in der Ablage
                // liegt (2026-08-13) - sonst teilen sich die vier Zellen die volle Reihe wie bisher.
                float pasteW = ClipboardHasAny() ? PasteColW + 6f : 0f;
                float cellW = (fullW - 3 * 6f - pasteW) / 4f;
                float cx = rect.x + 6f;
                // Kategorie-Symbol vor jeder Zelle (2026-08-09): in der kompakten Ansicht sagt die
                // Spaltenueberschrift, worum es geht - hier gab es keine, die vier Zellen standen unbeschriftet
                // nebeneinander. Das Symbol wird VON der Zellbreite abgezogen, nicht zusaetzlich davorgesetzt,
                // damit die Gesamtbreite der Reihe unveraendert bleibt und rechts nichts hinausrutscht.
                float catW;
                // Hellgrauer Hintergrund pro Gruppe (2026-08-12). Eine Gruppe ist genau das,
                // was hier ohnehin schon ein gemeinsames Rechteck hat: Kategorie-Symbol, Einfuege-Symbol,
                // Auswahlknopf und Verfuegbarkeits-Rechteck. Kein neues Mass - `cellW` definiert die Gruppe
                // seit jeher, sie war nur unsichtbar, weshalb die vier Zellen als acht lose Elemente
                // nebeneinander gelesen wurden. 2px Luft nach oben/unten, damit die Flaeche den 24px-Inhalt
                // umschliesst statt ihn zu beschneiden.
                void GroupBg() => Widgets.DrawBoxSolid(new Rect(cx, by - 2f, cellW, 28f), LoadoutUtility.ColGroupBg);
                // Das Kategorie-Symbol gehoert zur KLICKFLAECHE der Zelle (2026-08-13, im Test aufgefallen:
                // an der Stelle, an der der Tooltip 'Pack' erscheint, tat ein Klick nichts - erst
                // rund 25 px weiter rechts oeffnete derselbe Klick den Pack-Picker). Und die
                // Zahl passt: das Symbol traegt seinen eigenen Tooltip, liegt aber links VOR dem Knopf der
                // Zelle (18px) und sitzt zugleich sichtbar innerhalb desselben grauen Gruppenkastens - es
                // sieht also aus wie ein Teil der Zelle und war keiner.
                // `extraHit` statt den Knopf nach links zu ziehen: Widgets.ButtonText malt seinen eigenen
                // Hintergrund und wuerde das Symbol dabei uebermalen. ButtonInvisible zeichnet nichts.
                Rect CatHit() => new Rect(cx, by, 18f, 24f);
                GroupBg();
                catW = LoadoutUtility.DrawCategoryIcon(new Rect(cx, by, cellW, 24f), LoadoutUtility.TexCatAmmo, "CER_AssignLoadouts_LabelAmmo".Translate());
                DrawChangeAmmoCell(new Rect(cx + catW, by, cellW - catW, 24f), pawn, currentLoadout, iconOnly: true, extraHit: CatHit());
                cx += cellW + 6f;
                GroupBg();
                catW = LoadoutUtility.DrawCategoryIcon(new Rect(cx, by, cellW, 24f), LoadoutUtility.TexCatPack, "CER_AssignLoadouts_LabelPack".Translate());
                DrawEquipmentPackCell(new Rect(cx + catW, by, cellW - catW, 24f), pawn, currentLoadout, iconOnly: true, extraHit: CatHit());
                cx += cellW + 6f;
                GroupBg();
                catW = LoadoutUtility.DrawCategoryIcon(new Rect(cx, by, cellW, 24f), LoadoutUtility.TexCatSidearm, "CER_AssignLoadouts_LabelSidearm".Translate());
                DrawSidearmCell(new Rect(cx + catW, by, cellW - catW, 24f), pawn, currentLoadout, iconOnly: true, extraHit: CatHit());
                cx += cellW + 6f;
                GroupBg();
                catW = LoadoutUtility.DrawCategoryIcon(new Rect(cx, by, cellW, 24f), LoadoutUtility.TexCatOther, "CER_AssignLoadouts_LabelOther".Translate());
                DrawOtherItemsCell(new Rect(cx + catW, by, cellW - catW, 24f), pawn, currentLoadout, iconOnly: true, extraHit: CatHit());
                if (pasteW > 0f)
                {
                    cx += cellW + 6f;
                    DrawPasteColumn(new Rect(cx, by, PasteColW, 24f), pawn, currentLoadout,
                        updated => AmmoAssignmentUtility.SetPawnPacks(pawn, currentLoadout, updated),
                        updated => PasteSidearmsGuarded(pawn, currentLoadout, updated),
                        mlo => { AmmoAssignmentUtility.AssignTemplate(pawn, mlo); CombatReadiness.Invalidate(); });
                }
            }

            // Stats Breakdown Box
            Rect breakdownRect = new Rect(rect.x + 5f, rect.y + 153f, rect.width - 10f, 45f);
            Widgets.DrawMenuSection(breakdownRect);

            RowLabels breakLabels = GetRowLabels(pawn);   // Skills, Kapazitaeten und beide Texte vorgebaut, siehe GetRowLabels
            
            Text.Font = GameFont.Tiny;
            GUI.color = Color.gray;
            string b1 = breakLabels.b1;
            string b2 = breakLabels.b2;
            
            Text.Anchor = TextAnchor.MiddleLeft;
            Widgets.Label(new Rect(breakdownRect.x + 6f, breakdownRect.y + 6f, breakdownRect.width - 12f, 16f), b1);
            Widgets.Label(new Rect(breakdownRect.x + 6f, breakdownRect.y + 22f, breakdownRect.width - 12f, 16f), b2);
            Text.Anchor = TextAnchor.UpperLeft;
            GUI.color = Color.white;
            Text.Font = GameFont.Small;
            
            // Tooltip for Breakdown Box (precomputed in RefreshPawns).
            if (LoadoutUtility.TipWanted(breakdownRect))
            {
                if (!capacityTooltips.TryGetValue(pawn, out string breakdownTip)) breakdownTip = BuildCapacityTooltip(pawn);
                TooltipHandler.TipRegion(breakdownRect, breakdownTip);
            }
        }

        // Dasselbe Fenster aus RimWorlds EIGENEM Zuweisungs-Reiter heraus (2026-09-04).
        //
        // Der Lazy-Umbau hat den Weg "Loadout-Zelle anklicken" ueberall auf das Waffen-Auswahlfenster
        // umgestellt - nur nicht dort, wo CEs eigene Spalte sitzt. Das fiel in mehreren Tests
        // hintereinander auf: "die Zelle oeffnet weiterhin nur das alte Auswahlmenue
        // Nothing / Rollen / Save combo". Das war keine Design-Entscheidung, sondern die Stelle, an der
        // der Umbau aufgehoert hat.
        //
        // Eine WEGWERF-Instanz, wenn kein Planer offen ist - genau die Bauform, die der Arm-my-Colony-
        // Knopf auf demselben Reiter seit jeher benutzt: der Konstruktor liest nur den gespeicherten
        // Oberflaechenzustand, und das Fenster selbst wird nie gezeigt. Ist der Planer offen, wird SEINE
        // Instanz benutzt, damit Filter und Rollen-Schalter dieselben sind wie im sichtbaren Fenster.
        public static void OpenWeaponPickerFor(Pawn pawn)
        {
            if (pawn == null) return;
            Dialog_CEReloadedTabs tabs = Find.WindowStack.Windows.OfType<Dialog_CEReloadedTabs>().FirstOrDefault();
            Dialog_AssignLoadouts dlg = tabs?.AssignDialog ?? new Dialog_AssignLoadouts();
            Loadout current = AmmoAssignmentUtility.DisplayLoadout(pawn.GetLoadout());
            dlg.OpenLoadoutPickMenu(pawn, current, current != null && !current.defaultLoadout,
                LoadoutUtility.CannotFight(pawn));
        }

        // The per-colonist loadout picker menu, shared by the full cards and the compact rows.
        internal void OpenLoadoutPickMenu(Pawn pawn, Loadout currentLoadout, bool hasLoadout, bool cannotFight)
        {
            // Seit dem Lazy-Umbau (2026-09-01) ein Waffen-Auswahlfenster statt eines Loadout-FloatMenus:
            // der Spieler waehlt die WAFFE (mit Score, Bestand, Gewicht), die geteilte Vorlage dahinter
            // entsteht unsichtbar (EnsureDefaultLoadout). Handgebaute Loadouts und Rollen bleiben als
            // eigene Sektion waehlbar - der klassische Weg faellt nicht weg, er ist nur nicht mehr der
            // einzige. Generierte Vorlagen erscheinen NICHT mehr als Listeneintraege: sie sind
            // Buchfuehrung, ihre Waffe steht ja schon in der Waffenliste.
            Action nonePicked = () =>
            {
                var defLoadout = LoadoutManager.Loadouts.FirstOrDefault(l => l.defaultLoadout);
                Loadout cur = pawn.GetLoadout();
                // NUR DIE WAFFE wegnehmen, nicht alles (2026-08-16): Pazifisten haben keine Waffe und
                // trotzdem Equipment Packs - das muss weiter gehen.
                // Die Gegenprobe steht direkt darunter im Code: die Pack-Zelle erlaubt
                // einem Pazifisten ausdruecklich ein Pack, und packOnlyPawns teilt Kampfunfaehigen von sich
                // aus welche zu. "Pack ohne Waffe" ist also ein vorgesehener Zustand - ueber DIESEN Weg war
                // er nur nicht herstellbar.
                // Ursache: pawn.SetLoadout(default) ersetzt den ganzen Anker, und an dem haengen Packs,
                // Zweitwaffen und Einzelposten als Zusatz-Eltern. Jetzt bleibt der Anker stehen und
                // verliert nur seinen Waffen-Elternteil (parentID -> Standard-Loadout). Die Zelle zeigt
                // danach "None", weil DisplayLoadout am Standard-Loadout endet - genau wie vorher.
                // Ist am Anker wirklich nichts dran, bleibt der alte, saubere Weg: sonst schleppt der
                // Kolonist einen leeren Anker mit sich herum.
                bool hasExtras = cur != null && AmmoAssignmentUtility.IsAmmoLink(cur)
                    && (MultiParentUtility.GetExtraParents(cur).Count > 0
                        || (cur.OwnSlots != null && cur.OwnSlots.Count > 0));
                if (hasExtras && defLoadout != null)
                {
                    // Die eigene Munition der abgenommenen Waffe geht MIT ihr (2026-09-03, im Test): die
                    // Karte sagte danach "Waffe: keine" und ihre
                    // Munitionszelle war leer, waehrend die Kolonie-Rechnung denselben Kolonisten
                    // unveraendert unter "Zugewiesen an: Yttoyyn x150" fuehrte - zwei Stellen desselben
                    // Fensters, die sich widersprachen, und der Widerspruch ueberlebte einen
                    // Fenster-Neustart. Ursache: der Anker behaelt beim Umhaengen seine eigenen
                    // Munitionsslots, und die Kolonie-Rechnung liest genau die.
                    // UnseedOwnAmmoForDetached statt "alle eigenen Munitionsslots weg": es entfernt nur,
                    // was aus der abgenommenen Vorlage stammt UND das keine verbliebene Waffe verschiessen
                    // kann - eine persoenliche Munitionswahl fuer eine noch getragene Zweitwaffe bleibt
                    // also stehen. NACH dem Umhaengen gerufen, weil es "was traegt er jetzt noch" aus
                    // anchor.Slots liest.
                    Loadout detachedTemplate = cur.ParentLoadout;
                    cur.parentID = defLoadout.UniqueID;
                    if (detachedTemplate != null && !detachedTemplate.defaultLoadout)
                    {
                        AmmoAssignmentUtility.UnseedOwnAmmoForDetached(cur, new List<Loadout> { detachedTemplate });
                        LoadoutUtility.InvalidateAmmoOwnership();
                    }
                }
                else pawn.SetLoadout(defLoadout);
                CombatReadiness.Invalidate();   // 2026-08-01 - see ApplyPreviewPlan's apply() for why
            };
            // "Kann nicht kaempfen" heisst KEINE WAFFE, nicht "gar nichts" (2026-08-14) - ein Baby
            // (CannotCarryAnything) bekommt gar kein Fenster; ein Pazifist bekommt eins ohne Waffen-Sektion,
            // mit "Keins" und den waffenfreien Loadouts/Rollen (der Sanitaeter-Fall).
            if (cannotFight && LoadoutUtility.CannotCarryAnything(pawn) && !hasLoadout)
            {
                Messages.Message("CER_AssignLoadouts_CannotFightMsg".Translate(pawn.LabelShortCap), MessageTypeDefOf.RejectInput, false);
                return;
            }

            // Die Loadout-Sektion des Fensters: handgebaute Loadouts und Rollen. Ausgeschlossen sind
            // Equipment Packs (reine Versorgung, wuerde die Waffe still ersetzen - 2026-07-30), die
            // versteckten Pro-Kolonist-Anker (Befund 2026-09-01: die standen hier bisher ALS EINTRAEGE
            // drin, mit rohem "[Pawn-Loadout] <id>"-Etikett - der Wizard schuetzt sich davor laengst)
            // und generierte Vorlagen (deren Waffe steht schon in der Waffenliste).
            bool showWeapons = roleMode != RoleAssignMode.RolesOnly && !cannotFight;
            Func<List<Loadout>> loadoutOptions = () => LoadoutManager.Loadouts
                // Generierte Vorlagen KOMPLETT raus, auch die aktuelle (2026-09-02, laut Screenshot:
                // die aktuelle Vorlage stand doppelt da - als Loadout-Zeile UND als markierte Waffe;
                // ihre Waffe traegt die Markierung, das reicht).
                .Where(x => x != null && !x.defaultLoadout
                    && !EquipmentPackUtility.IsEquipmentPackLoadout(x)
                    && !AmmoAssignmentUtility.IsAmmoLink(x)
                    && !LoadoutUtility.IsGeneratedTemplate(x))
                .Where(l =>
                {
                    // Beide Richtungen (2026-08-19): der Auswahlfilter folgt demselben Schalter wie der
                    // Planer; das gerade zugewiesene Loadout bleibt immer sichtbar (sonst nicht abwaehlbar).
                    if (l != currentLoadout)
                    {
                        bool isRole = RoleUtility.IsRoleLoadout(l);
                        if (roleMode == RoleAssignMode.RolesOnly && !isRole) return false;
                        if (roleMode == RoleAssignMode.NoRoles && isRole) return false;
                    }
                    // Fuer einen Kampfunfaehigen bleiben genau die waffenfreien Vorlagen uebrig (Slots-
                    // Vereinigung, damit eine erbende Rolle nicht durchrutscht; Schilde sind keine Waffe).
                    if (cannotFight && (l.Slots ?? new List<LoadoutSlot>()).Any(s => s.thingDef != null && s.thingDef.IsWeapon)) return false;
                    return true;
                })
                .OrderBy(x => RoleUtility.DisplayLabel(x))
                .ToList();

            // Ist die aktuelle Zuweisung eine generierte Vorlage, markiert das Fenster ihre WAFFE als
            // gewaehlt - fuer den Spieler IST die Waffe die Zuweisung, das Vorlagen-Objekt existiert
            // fuer ihn nicht.
            Loadout curDisplay = currentLoadout;
            ThingDef curWeapon = null;
            if (curDisplay != null && LoadoutUtility.IsGeneratedTemplate(curDisplay)
                && AnalyzeLoadout(curDisplay, out ThingDef curMain, out _, out _, out _))
            {
                curWeapon = curMain;
                curDisplay = null;
            }

            // Leeres Fenster verhindern (2026-09-03: bei Grunt und Shenny kam ein leeres "Choose weapon" -
            // es soll ueberall so sein wie beim Baby): ein Kampfunfaehiger ohne Zuweisung
            // und ohne ein einziges waffenfreies Loadout bekommt die Meldung statt eines Fensters, in
            // dem ausser 'Keins' nichts steht.
            if (cannotFight && !hasLoadout && loadoutOptions().Count == 0)
            {
                Messages.Message("CER_AssignLoadouts_CannotFightMsg".Translate(pawn.LabelShortCap), MessageTypeDefOf.RejectInput, false);
                return;
            }

            Pawn localPawn = pawn;
            // Zuweisen und fertig - KEIN Nachschub-Popup an dieser Stelle (2026-08-10, bewusst so
            // entschieden): der Fehlbestand wird beim Verlassen des Reiters gesammelt gezeigt
            // (Dialog_CEReloadedTabs, Signaturvergleich gegen den Stand beim Betreten).
            // Waffe wie Loadout laufen durch DENSELBEN Pfad: der Einzelauswahl-Modus materialisiert eine
            // Waffe selbst (materializeWeapon) und liefert hier immer ein Loadout ab.
            Action<Loadout> pickedSingle = lo =>
            {
                AmmoAssignmentUtility.AssignTemplate(localPawn, lo);
                CombatReadiness.Invalidate();   // 2026-08-01 - siehe ApplyPreviewPlan's apply()
            };
            // "Kombination als Rolle sichern" ist hier RAUS (2026-09-03, gehoert nicht ins
            // Auswahlfenster) - die Karten/Kompaktzeilen des Reiters tragen den Weg weiterhin.

            // EIN Fenster fuer beide Faelle (2026-09-02, nach dem Vorbild des Zweitwaffen-Fensters statt
            // eines eigenen): dieselbe Dialog_PickPacks-Optik wie die Zweitwaffen-
            // Checkliste, nur im Einzelauswahl-Modus. Die Waffenliste entsteht EINMAL beim Oeffnen.
            List<ThingDef> mainChoices = showWeapons ? LoadoutUtility.MainWeaponChoices(curWeapon) : new List<ThingDef>();
            Find.WindowStack.Add(new Dialog_PickPacks(
                "CER_PickWeapon_SubtitleAssign".Translate(pawn.LabelShortCap.ToString()).ToString(),
                () => new List<Loadout>(), updated => { },
                windowTitle: "CER_PickWeapon_Title".Translate().ToString(),
                itemColumnLabel: "CER_GenerateLoadouts_WeaponBlocksHeader".Translate().ToString(),
                allOptions: loadoutOptions,
                emptyMessage: "CER_PickWeapon_NothingMatches".Translate().ToString(),
                displayLabel: lo => RoleUtility.DisplayLabel(lo) ?? "?",
                // Getrennte Sektionen "Vanilla loadouts" und "Roles" (2026-09-03) statt eines Sammelkopfs.
                groupLabel: lo => (RoleUtility.IsRoleLoadout(lo)
                    ? "CER_PickPacks_GroupRoles".Translate().ToString()
                    : "CER_PickPacks_GroupVanilla".Translate().ToString()),
                scoreFn: SidearmAssignmentUtility.ScoreOf,
                availDefFn: RepresentativeIcon,
                sourceLabel: pawn.LabelShortCap,
                weaponOptions: () => mainChoices,
                materializeWeapon: Dialog_GenerateLoadouts.EnsureDefaultLoadout,
                singleSelect: true,
                onPickedSingle: pickedSingle,
                onNonePicked: nonePicked,
                currentSingle: curDisplay, currentWeaponSingle: curWeapon,
                weaponGroupLabel: d => (d.IsRangedWeapon && !d.IsMeleeWeapon
                    ? "CER_AssignLoadouts_RangedHeader".Translate().ToString()
                    : "CER_AssignLoadouts_MeleeHeader".Translate().ToString()),
                // Kopierquelle: die aktuelle Zuweisung des Kolonisten (2026-09-03) - eingefuegt wird sie
                // ueber die Einfuege-Spalte der Zeilen (Ablage-Sorte 'mainweapon').
                copyCurrent: () =>
                {
                    Loadout c = AmmoAssignmentUtility.DisplayLoadout(localPawn.GetLoadout());
                    return c != null && !c.defaultLoadout ? c : null;
                },
                // Gesperrte Waffen: wurden Schloesser umgelegt und tragen Kolonisten laut Zuweisung
                // noch gesperrte Waffen, fragt beim Schliessen eine Bestaetigung nach der Neuzuweisung -
                // sie oeffnet die Auto-Vorschau mit keepAssigned an; der Loeser gibt von sich aus genau
                // die Zeilen mit gesperrter Waffe frei (AssignPlanner: carriesDisabled), der Rest bleibt.
                // "Go back" fuehrt ZURUECK ins Auswahlfenster (2026-09-03) - mit frisch
                // gelesenem Zuweisungsstand, denn der Pick davor kann ihn geaendert haben.
                onDisabledChanged: () =>
                {
                    int carriers = LoadoutUtility.DisabledWeaponCarrierCount();
                    if (carriers == 0) return;
                    Find.WindowStack.Add(LoadoutUtility.CompactConfirmation(
                        "CER_PickPacks_DisabledCarriersConfirm".Translate(carriers.ToString()),
                        () =>
                        {
                            RefreshPawns();
                            Find.WindowStack.Add(new Dialog_SquadSetup(null, this, presetKeepAssigned: true));
                        }, false,
                        cancelAct: () =>
                        {
                            Loadout curNow = AmmoAssignmentUtility.DisplayLoadout(localPawn.GetLoadout());
                            if (curNow != null && curNow.defaultLoadout) curNow = null;
                            OpenLoadoutPickMenu(localPawn, curNow, curNow != null, LoadoutUtility.CannotFight(localPawn));
                        }));
                }));
        }

        // Column headers for compact view - mirrors DrawPawnRowCompact's own column math exactly (same
        // literal offsets) so every label lines up with what's actually drawn below it. Same header
        // convention (Tiny font, 55% alpha, plain labels) Dialog_GenerateLoadouts' own Weapon Loadouts tab
        // list already uses - not a new style invented just for this view.
        private void DrawCompactHeader(Rect rect)
        {
            Text.Font = GameFont.Tiny;
            GUI.color = new Color(1f, 1f, 1f, 0.55f);
            if (LoadoutUtility.ColumnHeader(new Rect(rect.x + 34f, rect.y, 104f, rect.height), "CER_AssignLoadouts_ColonistHeader".Translate(),
                currentSortMode == SortMode.Name, sortDescending, true)) SetSort(SortMode.Name);
            // Sortierbar wie die Namensspalte (2026-08-09) - dieselben Rects wie vorher, nur klickbar.
            if (LoadoutUtility.ColumnHeader(new Rect(rect.x + 214f, rect.y, 80f, rect.height), "CER_AssignLoadouts_RangedHeader".Translate(),
                currentSortMode == SortMode.Ranged, sortDescending, true)) SetSort(SortMode.Ranged);
            if (LoadoutUtility.ColumnHeader(new Rect(rect.x + 300f, rect.y, 80f, rect.height), "CER_AssignLoadouts_MeleeHeader".Translate(),
                currentSortMode == SortMode.Melee, sortDescending, true)) SetSort(SortMode.Melee);
            // Weapon column starts right after the Melee bar actually ends (326+80+6), not at a stale 366f -
            // that used to be 40px INSIDE the melee bar's own rect, overlapping it (2026-07-30). Narrows
            // the weapon column by the same 46px, which was also asked for separately.
            Widgets.Label(new Rect(rect.x + WeaponColX, rect.y, 100f, rect.height), "CER_AssignLoadouts_WeaponHeader".Translate());

            // 80 -> 96 (2026-08-29: "177.1/92...." wurde neben dem Warnzeichen gekuerzt, und Platz war
            // noch da). Kopf und Zeile identisch.
            // 96 -> 112 (2026-08-30, im Test: '260.5/92...' und '95.5/92....' gekappt). Die Zelle
            // traegt DREI Dinge: die Zahl (bis '999.9/999.9', Tiny ~55), das Traeger-Symbol (size+4 = 18) und das Warnzeichen
            // (16) plus Innenabstand - rund 100 im Extremfall; 96 reichte nur ohne Symbol. Kopf und Zeile gleich.
            float bulkW = 112f, massW = 112f;
            Rect bulkBar = new Rect(rect.xMax - bulkW - 4f, rect.y, bulkW, rect.height);
            Rect massBar = new Rect(bulkBar.x - 6f - massW, rect.y, massW, rect.height);

            // Other is now a 4th icon-cell here, same size/row as Ammo/Pack/Sidearm (2026-08-01: it used
            // to be a separate undersized "+" button squeezed in before Mass with no
            // header label at all, the only one of the four not lined up with the others - see
            // DrawPawnRowCompact's own matching fix).
            const float compactCellW = 68f;
            // Dieselbe Breitenrechnung wie in der Zeile darunter (2026-08-13): reserviert die Zeile eine
            // Einfuege-Spalte, MUSS die Kopfzeile denselben Betrag abziehen - sonst stehen die vier
            // Beschriftungen ab dem Moment schief ueber ihren Zellen, in dem etwas in der Ablage liegt.
            // Genau diese Art Drift war der Grund fuer den Kopfzeilen-Fix vom 2026-08-01.
            float headerPasteW = ClipboardHasAny() ? PasteColW + 6f : 0f;
            Rect loadoutBtnRect = new Rect(rect.x + WeaponColX, rect.y, massBar.x - 6f - (rect.x + WeaponColX) - 4 * compactCellW - headerPasteW - 24f, rect.height);
            Text.Anchor = TextAnchor.MiddleCenter;
            Widgets.Label(new Rect(loadoutBtnRect.xMax + 6f, rect.y, compactCellW, rect.height), "CER_AssignLoadouts_LabelAmmo".Translate());
            Widgets.Label(new Rect(loadoutBtnRect.xMax + 6f + compactCellW + 6f, rect.y, compactCellW, rect.height), "CER_AssignLoadouts_LabelPack".Translate());
            Widgets.Label(new Rect(loadoutBtnRect.xMax + 6f + 2 * (compactCellW + 6f), rect.y, compactCellW, rect.height), "CER_AssignLoadouts_LabelSidearm".Translate());
            Widgets.Label(new Rect(loadoutBtnRect.xMax + 6f + 3 * (compactCellW + 6f), rect.y, compactCellW, rect.height), "CER_AssignLoadouts_LabelOther".Translate());
            // Die Einfuege-Spalte bekommt eine ueberSCHRIFT (2026-09-04). Der Platz war seit dem 13.08.
            // reserviert, beschriftet war er nie - die Spalte bestand aus einem 26 Punkte breiten Symbol bei
            // 65 % Deckkraft und sonst nichts. Zweimal ist sie im Test deshalb nicht gefunden worden, und
            // beide Male hiess es, der Copy-Knopf verspreche eine Spalte, die es nicht gibt (2026-08-23 und heute).
            // Beim ersten Mal ist nur das Symbol nachgetragen worden; ein Symbol ohne Namen in einer Tabelle,
            // deren vier andere Spalten Namen tragen, liest sich aber wie Zierrat. Der vorige Fix
            // war unvollstaendig.
            // DRITTER Anlauf (2026-09-07, im Test: von der Paste-Ueberschrift waren nur einzelne Strichfragmente
            // zu sehen): das Wort passte nicht in 26 Punkte und brach in Zeilen um, die der Kopf nicht hat.
            // Jetzt steht dasselbe Symbol wie in den Zeilen darunter im Kopf - so weiss man, was die Spalte
            // ist - und der Name wandert in den Erklaertext. Kein Text, der in eine Spalte passen muss,
            // die schmaler ist als jedes Wort dafuer in dreizehn Sprachen.
            if (headerPasteW > 0f)
            {
                Rect pasteHead = new Rect(loadoutBtnRect.xMax + 6f + 4 * (compactCellW + 6f), rect.y, PasteColW, rect.height);
                float ico = Mathf.Min(18f, pasteHead.height);
                GUI.color = new Color(1f, 1f, 1f, 0.8f);
                LoadoutUtility.DrawPasteIcon(new Rect(pasteHead.x + (pasteHead.width - ico) / 2f, pasteHead.y + (pasteHead.height - ico) / 2f, ico, ico));
                GUI.color = Color.white;
                TooltipHandler.TipRegion(pasteHead, "CER_AssignLoadouts_LabelPasteCol".Translate());
            }
            Text.Anchor = TextAnchor.UpperLeft;

            Widgets.Label(massBar, "CER_AssignLoadouts_MassLabel".Translate());
            Widgets.Label(bulkBar, "CER_AssignLoadouts_BulkLabel".Translate());

            GUI.color = Color.white;
            Text.Font = GameFont.Small;
        }

        // Compact view: everything essential from the card in one slim row (portrait, name, both skill bars, loadout
        // with weapon icons, mass/bulk). What no longer fits lives in the tooltips of the respective cell.
        private void DrawPawnRowCompact(Rect rect, Pawn pawn, bool alt)
        {
            DrawPawnRowCompactInner(rect, pawn, alt);
        }

        private void DrawPawnRowCompactInner(Rect rect, Pawn pawn, bool alt)
        {
            if (alt) Widgets.DrawLightHighlight(rect);
            if (Mouse.IsOver(rect)) Widgets.DrawHighlight(rect);
            HandleDropTarget(rect, pawn);

            // Portrait + name open the gear editor, exactly like the card.
            Rect portraitRect = new Rect(rect.x + 2f, rect.y, 26f, 30f);
            RenderTexture portrait = PortraitsCache.Get(pawn, new Vector2(26f, 30f), Rot4.South, default(Vector3), 1f, true, true, false, false, null, null, false);
            if (portrait != null) GUI.DrawTexture(portraitRect, portrait);
            TooltipHandler.TipRegion(portraitRect, new TipSignal(() => GetEquipmentTooltip(pawn) + "CER_AssignLoadouts_ClickToViewEditGear".Translate(), pawn.GetHashCode() * 37));

            // 130 -> 104 (2026-08-31, siehe WeaponColX): der volle Name steht ohnehin im Mouseover.
            Rect nameRect = new Rect(rect.x + 34f, rect.y, 104f, rect.height);
            Text.Anchor = TextAnchor.MiddleLeft;
            Widgets.Label(nameRect, pawn.LabelShortCap.Truncate(100f));
            Text.Anchor = TextAnchor.UpperLeft;
            // Ganzer Block hinter die Maus-Pruefung (siehe LoadoutUtility.TipWanted): ToStringFull,
            // BuildCapacityTooltip und die Verkettung liefen pro Zeile pro Bild, sichtbar wurde davon
            // hoechstens eine.
            if (LoadoutUtility.TipWanted(nameRect))
            {
                string fullName = pawn.Name != null ? pawn.Name.ToStringFull : pawn.LabelShort;
                if (!capacityTooltips.TryGetValue(pawn, out string breakdownTip)) breakdownTip = BuildCapacityTooltip(pawn);
                string traits = traitStrings.TryGetValue(pawn, out string tr) && !string.IsNullOrEmpty(tr) ? "\n" + tr : "";
                TooltipHandler.TipRegion(nameRect, fullName + traits + "\n\n" + breakdownTip);
            }
            if (Widgets.ButtonInvisible(portraitRect) || Widgets.ButtonInvisible(nameRect))
                Find.WindowStack.Add(new Dialog_PawnGear(pawn));

            // Save as Role - between the (now narrower) name and the skill bars. "Save" reads as a plain
            // action label at this width; the tooltip's own lead line spells out what it actually does
            // (2026-07-30: was labeled "Role", which read as a status/filter rather than an action button).
            // 54f statt 44f (2026-08-26, gleiche Ueberlaufklasse wie der Kartenknopf: "Speichern" braucht
            // mehr als 44) - bis zum Faehigkeitsbalken bei +226 ist Luft, und ButtonTextFit stuft den
            // Rest ein, statt zwei Zeilen zu quetschen.
            Rect saveRoleRectCompact = new Rect(rect.x + 144f, rect.y + (rect.height - 22f) / 2f, 54f, 22f);
            Loadout compactAssigned = pawn.GetLoadout();
            // See cardHasLoadout's own comment above - same DisplayLoadout-unwrap fix (2026-08-01).
            bool compactIsRole = RoleUtility.IsRoleLoadout(AmmoAssignmentUtility.DisplayLoadout(compactAssigned));
            // DIESELBEN drei Faelle wie in der Kartenansicht (2026-08-16). Ich hatte den Umbau dort gemacht
            // und diese Stelle stehen lassen - genau die Fehlerklasse, die in diesem Mod am haeufigsten
            // vorkommt (zwei Zeichenstellen, eine angefasst). Hier passt "Rolle bearbeiten" nicht in 44
            // Pixel, deshalb ein kurzes eigenes Wort; die Wirkung ist identisch.
            RowInfo rowInfo = GetRowInfo(pawn);
            bool compactHasPacks = rowInfo.hasPacks;
            bool compactHasLoadout = ((compactAssigned != null && !compactAssigned.defaultLoadout) || compactHasPacks) && !compactIsRole;
            if (compactIsRole)
            {
                if (LoadoutUtility.ButtonTextFit(saveRoleRectCompact, "CER_AssignLoadouts_EditBtnCompact".Translate()) && parentWindow != null)
                {
                    Loadout compactDisplay = AmmoAssignmentUtility.DisplayLoadout(compactAssigned);
                    string compactRoleName = (compactDisplay?.label ?? "").Substring(
                        (compactDisplay?.label ?? "").StartsWith(RoleUtility.RoleMarker) ? RoleUtility.RoleMarker.Length : 0).Trim();
                    parentWindow.LoadoutsDialog.FocusExistingRole(compactRoleName);
                    parentWindow.JumpToLoadouts();
                }
            }
            else if (compactHasLoadout)
            {
                if (LoadoutUtility.ButtonTextFit(saveRoleRectCompact, "CER_AssignLoadouts_SaveBtnCompact".Translate()) && parentWindow != null)
                {
                    parentWindow.LoadoutsDialog.StartRoleDraftFromPawn(pawn, AmmoAssignmentUtility.ComposedDisplayLabel(pawn));
                    parentWindow.JumpToLoadouts();
                }
            }
            else
            {
                // Same house-style fix as the card view's own button - see its comment (2026-07-30).
                GUI.color = new Color(1f, 1f, 1f, 0.35f);
                Text.Anchor = TextAnchor.MiddleCenter;
                GameFont prevCompactFont = Text.Font;
                bool prevCompactWrap = Text.WordWrap;
                Text.WordWrap = false;
                string dimmedCompactLbl = "CER_AssignLoadouts_SaveBtnCompact".Translate();
                if (Text.CalcSize(dimmedCompactLbl).x > saveRoleRectCompact.width - 4f) Text.Font = GameFont.Tiny;
                Widgets.Label(saveRoleRectCompact, dimmedCompactLbl.Truncate(saveRoleRectCompact.width - 4f));
                Text.WordWrap = prevCompactWrap;
                Text.Font = prevCompactFont;
                Text.Anchor = TextAnchor.UpperLeft;
                GUI.color = Color.white;
            }
            if (LoadoutUtility.TipWanted(saveRoleRectCompact)) TooltipHandler.TipRegion(saveRoleRectCompact, compactIsRole
                ? "CER_AssignLoadouts_EditRoleTip".Translate()
                : (compactHasLoadout ? "CER_AssignLoadouts_SaveRoleTipCompactHas".Translate()
                                     : "CER_AssignLoadouts_SaveRoleTipCompactNone".Translate()));

            // Both skill bars, same colors and tooltip as on the card.
            float maxScore = SkillRecord.MaxLevel * 10f;
            Rect rBar = new Rect(rect.x + 200f, rect.y + 6f, 80f, 18f);
            Rect mBar = new Rect(rect.x + 286f, rect.y + 6f, 80f, 18f);
            RowLabels rowLabels = GetRowLabels(pawn);   // vorformatiert, siehe GetRowLabels
            DrawScoreBarPrebuilt(rBar, rangedScores[pawn], maxScore, rowLabels.rangedCompact);
            DrawScoreBarPrebuilt(mBar, meleeScores[pawn], maxScore, rowLabels.meleeCompact);
            Rect skillTipRect = new Rect(rBar.x, rect.y, 172f, rect.height);
            if (LoadoutUtility.TipWanted(skillTipRect)) TooltipHandler.TipRegion(skillTipRect, "CER_AssignLoadouts_RangedMeleeScoreTip".Translate());

            // Eigener Abschnitt, damit zwischen den gemessenen Bloecken keine Luecke bleibt: in der
            // Kartenansicht stecken dieselben drei Abfragen (GetLoadout, DisplayLoadout, CannotFight) in
            // "Karte: Waffenknopf", hier stehen sie im Code vor den Balken. Beim Vergleich der Ansichten
            // also zu "Kompakt: Waffenknopf" dazuzaehlen.
            // Right-aligned fixed cells: bulk, mass, loadout editor. The loadout button takes the middle remainder.
            // Mass and Bulk are the SAME width now (Mass made narrower, same as
            // Bulk) - was wider than Bulk for no real reason (Mass's "kg" suffix fits fine at this width too).
            // 80 -> 96 (2026-08-29: "177.1/92...." wurde neben dem Warnzeichen gekuerzt, und Platz war
            // noch da). Kopf und Zeile identisch.
            // 96 -> 112 (2026-08-30, im Test: '260.5/92...' und '95.5/92....' gekappt). Die Zelle
            // traegt DREI Dinge: die Zahl (bis '999.9/999.9', Tiny ~55), das Traeger-Symbol (size+4 = 18) und das Warnzeichen
            // (16) plus Innenabstand - rund 100 im Extremfall; 96 reichte nur ohne Symbol. Kopf und Zeile gleich.
            float bulkW = 112f, massW = 112f;
            Rect bulkBar = new Rect(rect.xMax - bulkW - 4f, rect.y + 6f, bulkW, 18f);
            Rect massBar = new Rect(bulkBar.x - 6f - massW, rect.y + 6f, massW, 18f);

            // Unwrap any hidden per-pawn "extra ammo" leaf (AmmoAssignmentUtility) for display; mass/bulk
            // reads the raw assignment so extra ammo still counts toward the total.
            Loadout assignedLoadout = pawn.GetLoadout();
            Loadout currentLoadout = AmmoAssignmentUtility.DisplayLoadout(assignedLoadout);
            bool hasLoadout = currentLoadout != null && !currentLoadout.defaultLoadout;
            bool cannotFight = LoadoutUtility.CannotFight(pawn);

            // Masse/Sperrigkeit kommen aus GetRowInfo (Frame-Cache statt einmal pro Durchgang) - die Rechnung
            // selbst ist unveraendert, inklusive der Regel, dass sie am ANKER haengt und nicht an der
            // entpackten Waffenvorlage (2026-08-14, im Test als Nebenbefund gemessen: ein Kolonist
            // mit DREI direkt angehaengten Packs zeigte sonst 0.0/32.0 kg, weil die Waffen-Vorlage eines
            // reinen Pack-Traegers das "None"-Objekt ist. Schwer wiegt es, weil die Ueberladungs-Warnung an
            // genau dieser Zahl haengt: steht dort 0, warnt niemand).
            float lMass = rowInfo.mass, lBulk = rowInfo.bulk;
            GetCapacities(pawn, out float capMass, out float capBulk);

            Text.Font = GameFont.Tiny;
            // Dieselbe Ueberlast-Warnung wie in der Kartenansicht (2026-08-12) - zweite Fundstelle desselben
            // Balkenpaares, per Grep mitgenommen, damit die kompakte Zeile nicht die einzige Ansicht ist, die
            // ein nicht tragbares Loadout kommentarlos anzeigt.
            bool massOverC = DrawCapacityBar(massBar, lMass, capMass, GetRowLabels(pawn).mass, pawn, false, out var massBoostersC);   // vorformatiert, siehe GetRowLabels
            if (LoadoutUtility.TipWanted(massBar)) TooltipHandler.TipRegion(massBar, WithOverCapNote(LoadoutUtility.CarryCapacityTooltip(pawn, false, lMass, capMass, massBoostersC), massOverC));
            bool bulkOverC = DrawCapacityBar(bulkBar, lBulk, capBulk, GetRowLabels(pawn).bulk, pawn, true, out var bulkBoostersC);
            if (LoadoutUtility.TipWanted(bulkBar)) TooltipHandler.TipRegion(bulkBar, WithOverCapNote(LoadoutUtility.CarryCapacityTooltip(pawn, true, lBulk, capBulk, bulkBoostersC), bulkOverC));
            Text.Font = GameFont.Small;

            // Loadout button with the main weapon + sidearm icons, filling the space between skills and mass -
            // the Ammo/Pack/Sidearm cells always all three present now (each shows "None" itself rather than
            // vanishing when nothing exists yet anywhere in the game), each taking a rightmost slice.
            // Icon-only, one fixed width for all three - these used to be full text buttons ("Ammo: None",
            // "Pack: E| Base Supply", "Sidearm: W| knife") that each ended up a different width depending on
            // label length, the opposite of "compact". The icon carries the same info at a glance; full detail
            // is still one hover away in each cell's (unchanged) tooltip. Doubled from the original 34f - the
            // fulfillment status icon each cell now also draws needs the extra room to sit next to the item
            // icon instead of overlapping it.
            // Other is now a 4th icon-cell, same size and row as Ammo/Pack/Sidearm (2026-08-01
            // - see DrawCompactHeader's own matching fix for the full story: it used to be a separate,
            // undersized bare "+" button squeezed in before Mass, with no header label and no dash/icon+badge
            // rendering like its siblings - now just another call to the same DrawOtherItemsCell the card
            // view already uses).
            // Dieselbe eine Einfuege-Spalte wie in der Kartenansicht (2026-08-13), hinter "Sonstiges" und
            // vor dem Massebalken. Kostet nur Breite, wenn wirklich etwas in der Ablage liegt.
            float compactPasteW = ClipboardHasAny() ? PasteColW + 6f : 0f;
            const float compactCellW = 68f;
            float ammoCellW = compactCellW;
            float packCellW = compactCellW;
            float sidearmCellW = compactCellW;
            float otherCellW = compactCellW;
            // Aus GetRowInfo (Frame-Cache): LoadoutMainAndSide baut die Slots-Union neu auf und legt vier
            // LINQ-Zwischenlisten an - das lief pro Zeile pro Durchgang.
            ThingDef rowMainW = rowInfo.mainW, rowSideW = rowInfo.sideW;
            Rect loadoutBtnRect = new Rect(rect.x + WeaponColX, rect.y + 3f, massBar.x - 6f - (rect.x + WeaponColX) - sidearmCellW - packCellW - ammoCellW - otherCellW - compactPasteW - 24f, 24f);
            DrawChangeAmmoCell(new Rect(loadoutBtnRect.xMax + 6f, rect.y + 3f, ammoCellW, 24f), pawn, currentLoadout, iconOnly: true);
            DrawEquipmentPackCell(new Rect(loadoutBtnRect.xMax + 6f + ammoCellW + 6f, rect.y + 3f, packCellW, 24f), pawn, currentLoadout, iconOnly: true);
            DrawSidearmCell(new Rect(loadoutBtnRect.xMax + 6f + ammoCellW + 6f + packCellW + 6f, rect.y + 3f, sidearmCellW, 24f), pawn, currentLoadout, iconOnly: true);
            DrawOtherItemsCell(new Rect(loadoutBtnRect.xMax + 6f + ammoCellW + 6f + packCellW + 6f + sidearmCellW + 6f, rect.y + 3f, otherCellW, 24f), pawn, currentLoadout, iconOnly: true);
            if (compactPasteW > 0f)
                DrawPasteColumn(new Rect(loadoutBtnRect.xMax + 6f + ammoCellW + 6f + packCellW + 6f + sidearmCellW + 6f + otherCellW + 6f, rect.y + 3f, PasteColW, 24f),
                    pawn, currentLoadout,
                    updated => AmmoAssignmentUtility.SetPawnPacks(pawn, currentLoadout, updated),
                    updated => PasteSidearmsGuarded(pawn, currentLoadout, updated),
                    mlo => { AmmoAssignmentUtility.AssignTemplate(pawn, mlo); CombatReadiness.Invalidate(); });

            // No longer dimmed for cannotFight pawns - a pacifist can still carry an Equipment Pack (see
            // DrawEquipmentPackCell), so greying out the whole row read as "nothing works here" when
            // something usually does. The tooltip below still explains why no WEAPON can be assigned.
            // Button drawn into a rect narrowed by DotReserveW, not the full loadoutBtnRect (2026-08-01,
            // the button's own background touched the availability dot with no gap, unlike
            // every other cell in this row) - mirrors the card view's own btnHitRect convention exactly
            // (icon/label/WeaponDotRect below still use the full-width loadoutBtnRect, unaffected).
            Rect btnHitRect = new Rect(loadoutBtnRect.x, loadoutBtnRect.y, loadoutBtnRect.width - DotReserveW, loadoutBtnRect.height);
            // Blautoene wie in CEs eigener Loadout-Spalte (2026-08-13: die Blau-Abstufungen unterscheiden
            // Rollen und Waffen, so wie beim Vanilla-"Current loadout" draussen) - dunkleres Blau = Rolle,
            // blasses = eigene Waffen-Vorlage. Das ist
            // zugleich der Ersatz fuer das weggefallene Praefix: die Unterscheidung steckt jetzt in der
            // Farbe statt in drei Zeichen Text.
            // TintedButton statt DrawAtlas+ButtonText (2026-08-16, im Test gemessen): die zweite
            // Haelfte hat die erste uebermalt, die Einfaerbung war deshalb NIE zu sehen - in beiden
            // Ansichten und seit dem ersten Tag. Siehe LoadoutUtility.TintedButton.
            // Siehe die Kartenansicht - dieselbe Regel aus derselben Funktion, damit die zwei Ansichten
            // nicht wieder auseinanderlaufen koennen.
            bool loadoutClicked = LoadoutUtility.TintedButton(btnHitRect, LoadoutUtility.LoadoutTintFor(compactAssigned));
            float ix = loadoutBtnRect.x + 5f;
            if (rowMainW != null) { Widgets.ThingIcon(new Rect(ix, loadoutBtnRect.y + 2f, 20f, 20f), rowMainW); ix += 22f; }
            if (rowSideW != null) { Widgets.ThingIcon(new Rect(ix, loadoutBtnRect.y + 2f, 20f, 20f), rowSideW); ix += 22f; }
            Text.Anchor = rowMainW != null ? TextAnchor.MiddleLeft : TextAnchor.MiddleCenter;
            Rect lblRect = LoadoutBtnLabelRect(loadoutBtnRect, ix, rowMainW != null);
            // CellLabel statt DisplayLabel (Praefix weg) und eine Schriftgroesse kleiner (2026-08-13)
            // - zusammen mit den 14px aus der Spaltenverschiebung
            // reicht das fuer den ganzen Waffennamen, statt "W| R-6 ...".
            GameFont prevLblFont = Text.Font;
            Text.Font = GameFont.Tiny;
            Widgets.Label(lblRect, (hasLoadout ? LoadoutUtility.CellLabel(currentLoadout) : "CER_AssignLoadouts_NoneLoadout".Translate().ToString()).Truncate(lblRect.width));
            Text.Font = prevLblFont;
            Text.Anchor = TextAnchor.UpperLeft;
            GUI.color = Color.white;
            if (!cannotFight && hasLoadout) HandleDragHandle(loadoutBtnRect, pawn);
            // The weapon status dot (drawn last, see below) claims its own corner of loadoutBtnRect - while
            // the mouse is over it, it must win outright, not stack its own tooltip underneath this wider one.
            Rect weaponDotRect = WeaponDotRect(loadoutBtnRect);
            if (!Mouse.IsOver(weaponDotRect))
            {
                // Siehe die gleichlautende Kette in DrawPawnCard - dasselbe pro Zeile pro Bild.
                if (LoadoutUtility.TipWanted(loadoutBtnRect))
                {
                    if (dragPawn != null && dragPawn != pawn)
                        TooltipHandler.TipRegion(loadoutBtnRect, "CER_AssignLoadouts_DropSwapTip".Translate(dragPawn.LabelShortCap));
                    else if (cannotFight) TooltipHandler.TipRegion(loadoutBtnRect, "CER_AssignLoadouts_CannotFightTip".Translate()
                        + (hasLoadout ? "CER_AssignLoadouts_CannotFightClearSuffix".Translate().ToString() : ""));
                    else if (hasLoadout) TooltipHandler.TipRegion(loadoutBtnRect, LoadoutUtility.LoadoutContentsTooltip(currentLoadout)
                        + "CER_AssignLoadouts_DragToSwapSuffix".Translate());
                }
            }
            if (loadoutClicked) OpenLoadoutPickMenu(pawn, currentLoadout, hasLoadout, cannotFight);
            // Weapon status: same corner-dot convention as the Ammo/Pack/Sidearm cells, on the loadout button
            // itself - replaces the old separate "Equipped: <weapon>" detail line entirely (what's actually in
            // the pawn's hands right now, if different from the plan, now lives in this dot's own tooltip).
            DrawWeaponStatusDot(loadoutBtnRect, pawn, rowMainW);
        }

        // ---- Change-ammo cell (card + compact) --------------------------------------------------------------

        // Opens Dialog_ChangeAmmo for this ONE pawn - extra ammo on top of whatever their weapon loadout (and
        // any attached Equipment Pack) already provides, via a hidden per-pawn leaf loadout (see
        // AmmoAssignmentUtility) instead of touching the shared template. `currentLoadout` here is already
        // unwrapped past that leaf (DisplayLoadout was applied where this pawn's card/row builds it) - it is
        // exactly the template the leaf should sit on top of.
        private void DrawChangeAmmoCell(Rect cell, Pawn pawn, Loadout currentLoadout, bool iconOnly = false, Rect? extraHit = null)
        {
            DrawChangeAmmoCellInner(cell, pawn, currentLoadout, iconOnly, extraHit);
        }

        private void DrawChangeAmmoCellInner(Rect cell, Pawn pawn, Loadout currentLoadout, bool iconOnly, Rect? extraHit)
        {
            // Auch ohne Hauptwaffe, sobald eine Zweitwaffe ueber CEs None-Loadout Munition braucht (2026-08-25,
            // im Test gefunden): der Anker traegt deren Munition in seiner Slots-Vereinigung, die Zelle zeigte trotzdem
            // den Strich und das Munitionsfenster war unerreichbar. Das Fenster selbst arbeitet ueber den Anker
            // (GetAmmoLeaf), nicht ueber die Vorlage - es braucht keine Hauptwaffe.
            bool hasLoadout = currentLoadout != null
                && (!currentLoadout.defaultLoadout || GetAmmoCellState(pawn).pool.Count > 0);
            Text.Font = GameFont.Tiny;
            if (!hasLoadout)
            {
                // Kompakte Ansicht: derselbe Helfer und dieselbe Geometrie wie jeder andere Leerzustand
                // (2026-08-10: die Knoepfe hatten verschiedene Groessen; 2026-08-11 nachgebessert,
                // der erste Anlauf hatte die Verschmaelerung vergessen und blieb 12px daneben). Vorher stand
                // hier ein rohes Label ueber der vollen Zelle mit dem gerade gueltigen Anker - der Strich
                // sass links oben statt mittig.
                if (iconOnly)
                {
                    // Leerzustand auf DIESELBE Geometrie wie eine gefuellte Zelle (2026-08-11) - und das ist
                    // seit 2026-08-14 die volle Zelle, siehe den Kommentar an DotReserveW.
                    DrawCellDash(cell);
                    Text.Font = GameFont.Small;
                    return;
                }
                GUI.color = new Color(1f, 1f, 1f, 0.35f);
                Widgets.Label(cell, "CER_AssignLoadouts_AmmoDashLabel".Translate());
                GUI.color = Color.white;
                Text.Font = GameFont.Small;
                return;
            }
            // The FULLY RESOLVED ammo the pawn actually carries - a Weapon Loadout template's own baked
            // default counts just as much as a personal override (AmmoAssignmentUtility/
            // Patch_Loadout_MultiParentSlots' full-replace semantics). Reading only the personal anchor's
            // OwnSlots here (ExtraAmmoSlots) showed "None" for every pawn who never opened the Ammo
            // Loadouts popup, even when their weapon loadout already carries a perfectly real ammo type -
            // "None" must mean "genuinely carries nothing", not "never personally touched."
            AmmoCellState ammoState = GetAmmoCellState(pawn);
            List<LoadoutSlot> effective = ammoState.effective;
            List<ThingDef> pool = ammoState.pool;
            List<ThingDef> active = ammoState.active;
            // pool leer = gar keine Munitionswaffe, nichts einzustellen; pool voll und active leer = braucht
            // Munition, hat keine, echte Luecke.
            // Der TEXT dazu entsteht erst im Zweig, der ihn wirklich zeichnet (2026-08-17,
            // gemessen): in der kompakten Ansicht steht in dieser Zelle nur ein Symbol, der
            // Text wurde also pro Kolonist pro Durchgang gebaut, uebersetzt und weggeworfen.
            bool noAmmoWarning = pool.Count > 0 && active.Count == 0;
            GUI.color = noAmmoWarning ? LoadoutUtility.ColBad : Color.white;
            // The status dot's own corner is reserved from the BUTTON's hit-rect (not just drawn visually
            // on top of it) - Widgets.ButtonText handles background+highlight+click as one atomic call over
            // whatever rect it's given, so the only way for the dot to truly "win" over the button
            // underneath (no highlight, no click) is for the button's own rect to never extend under it.
            Rect btnRect = new Rect(cell.x, cell.y, cell.width - DotReserveW, cell.height);
            bool clicked;
            if (iconOnly)
            {
                clicked = Widgets.ButtonText(btnRect, "") | (extraHit.HasValue && Widgets.ButtonInvisible(extraHit.Value));
                if (pool.Count == 0) DrawCellDash(cell);
                else if (noAmmoWarning) DrawCellWarning(cell);
                else DrawCellIcon(cell, active[0], active.Count > 1 ? active.Count : 0);
            }
            else
            {
                string summary = pool.Count == 0 ? "CER_AssignLoadouts_AmmoSummaryNone".Translate().ToString()
                    : noAmmoWarning ? "CER_AssignLoadouts_ZeroTypesLabel".Translate().ToString()
                    : active.Count == 1 ? active[0].LabelCap.ToString()
                    : "CER_AssignLoadouts_TypesCountFmt".Translate(active.Count.ToString()).ToString();
                clicked = Widgets.ButtonText(btnRect, "CER_AssignLoadouts_AmmoPrefixFmt".Translate(summary.Truncate(btnRect.width - 44f))) | (extraHit.HasValue && Widgets.ButtonInvisible(extraHit.Value));
            }
            // Geforderte Menge = die Slot-Menge DIESES Kolonisten fuer diese Sorte; die Begruendung dazu
            // steht bei AmmoSegFor.
            Rect ammoDot = DrawFulfillDot(cell, pawn, active, "CER_AssignLoadouts_LabelAmmo",
                RichStock.Ammo, richNeedFor: d => effective.Where(s => s.thingDef == (ThingDef)d).Sum(s => s.count));
            GUI.color = Color.white;
            Text.Font = GameFont.Small;
            if (!Mouse.IsOver(ammoDot) && LoadoutUtility.TipWanted(cell))
                TooltipHandler.TipRegion(cell, (pool.Count == 0
                    ? "CER_AssignLoadouts_AmmoTipNoWeapon".Translate(pawn.LabelShortCap)
                    : noAmmoWarning
                        ? "CER_AssignLoadouts_AmmoTipNoneSetup".Translate(pawn.LabelShortCap)
                        // AmmoCountFor statt effective.First(...): seit "Any ammo" als gesetzte Wahl zaehlt,
                        // steht in `active` auch eine Sorte, die als GENERISCHER Slot dasteht und gar keinen
                        // konkreten Slot hat - First() hat dafuer geworfen, und zwar in OnGUI, in jedem Bild,
                        // solange der Zeiger auf der Zelle stand. Ausgerechnet im "Any ammo"-Fall, fuer den
                        // die Ergaenzung gebaut wurde (2026-08-15, Review-Befund).
                        : "CER_AssignLoadouts_AmmoTipList".Translate(pawn.LabelShortCap, string.Join(", ", active.Select(d => d.LabelCap.ToString() + " x" + AmmoCountFor(effective, d)))))
                    + "CER_AssignLoadouts_AmmoTipClickSuffix".Translate());
            if (clicked) Find.WindowStack.Add(new Dialog_ChangeAmmo(pawn, currentLoadout));
        }

        // ---- Shared icon-only cell rendering (compact view) ---------------------------------------------------
        // Same fixed-width treatment for the Ammo/Pack/Sidearm cells - a single representative icon plus a
        // "+N" corner badge when there's more than one, instead of a text label that made the three cells
        // different widths depending on how long a loadout's name happened to be. Full detail stays in the
        // (unchanged) tooltip; nothing here is lost, just not spelled out in the row itself anymore.
        // Centered within the cell MINUS the fulfill-dot's own always-reserved strip (DotReserveW) on the
        // right edge - not the full cell (2026-08-01: glyphs still looked off-center after
        // the paste-button fix, in BOTH card and compact view). DrawFulfillDot draws its corner strip at
        // cell.xMax-10..cell.xMax on this SAME cell, but every glyph drawn here still centered itself
        // against the FULL cell width as if that strip didn't exist - visually shifting the icon right of
        // where the remaining (non-dot) space's own true center actually is.
        private static Rect VisualCell(Rect cell) => new Rect(cell.x, cell.y, cell.width - DotReserveW, cell.height);

        private static void DrawCellDash(Rect cell)
        {
            GUI.color = new Color(1f, 1f, 1f, 0.35f);
            Text.Anchor = TextAnchor.MiddleCenter;
            Widgets.Label(VisualCell(cell), "CER_AssignLoadouts_DashPlaceholder".Translate());
            Text.Anchor = TextAnchor.UpperLeft;
            GUI.color = Color.white;
        }

        private static void DrawCellWarning(Rect cell)
        {
            // Das PNG-Ausrufezeichen, mittig (2026-09-07: bei Munition kein normales Rufezeichen, sondern
            // das PNG-Ausrufezeichen, in der Mitte).
            // Der Text-Glyph war die letzte Stelle, die noch durch Text.CurFontStyle lief - siehe
            // LoadoutUtility.DrawWarningBadge, warum das an jedem Ort anders aussah. Weiss auf der roten
            // Zelle, nicht eingefaerbt.
            Rect vc = VisualCell(cell);
            float s = Mathf.Min(16f, vc.height - 4f);
            LoadoutUtility.DrawExclamationGlyph(new Rect(vc.x + (vc.width - s) / 2f, vc.y + (vc.height - s) / 2f, s, s), Color.white);
        }

        // Per-pawn "equipped" check on top of LoadoutUtility's shared FulfillStatus (color/word live there
        // now so Dialog_ChangeAmmo's ammo rows can use the exact same square-and-wording convention) -
        // equipped beats sitting in a stockpile beats merely being queued for production beats having none
        // of the above. Compact view's per-cell status dot shows the WORST of these across everything the
        // cell represents, so a Pack with 3 items where only 1 is missing still flags as "not fully ready"
        // instead of quietly showing green because most of it happens to be fine. Internal so Dialog_
        // LoadoutEditor's own rows (drawn in a separate floating Window) can reuse the SAME frameOwnedCache
        // this instance already refreshes every frame it's on screen - correct as long as the editor is
        // only ever opened while this dialog (or its owning tab window) is still visible, which is always
        // true today since it's opened FROM one of this dialog's own cells.
        // Accepts a plain ThingDef OR a LoadoutGenericDef (e.g. a pack's "Any Medicine"/"Any Meal" slot) - a
        // generic has no single concrete def to check ownership/stock against, so it gets its own family-wide
        // resolution (LoadoutUtility.PawnAlreadyHasGeneric/GenericStockCount) instead of ThingDef's per-def cache.
        // `needed` ist die vom Loadout GEFORDERTE Stueckzahl (2026-08-09). Vorher fragte diese Funktion nur
        // "hat er ueberhaupt so etwas" - ein Kolonist mit 1 von 5 Medizin galt damit als fertig ausgeruestet,
        // der Fuenfer-Verbandskasten war gruen, und weil gruen bedeutet "nichts zu tun", wurde auch kein
        // Nachschub eingeplant. Default 1 lassen, damit die Aufrufer ohne Mengenbegriff (Waffe, Zweitwaffe -
        // dort ist die geforderte Menge immer 1) sich exakt wie bisher verhalten.
        // Das Urteil aus der gemeinsamen Reihum-Rechnung (siehe LoadoutClaims), wenn sie fuer genau diesen
        // Kolonisten und genau diesen Gegenstand ueberhaupt eine Aussage hat. false heisst NICHT "vorhanden",
        // sondern "nicht gefragt" - dann entscheidet der Aufrufer wie bisher weiter. Genau diese
        // Unterscheidung ist der Grund fuer das evaluated-Set: ohne sie laese sich eine Zeile, die an gar
        // keinem Kolonisten haengt (Vorschau im Loadout-Editor), stillschweigend als "im Lager".
        private static bool ClaimedStatus(Pawn pawn, Def d, out FulfillStatus status)
        {
            status = FulfillStatus.Missing;
            LoadoutClaims claims = Claims();
            if (!claims.Knows(pawn, d)) return false;

            bool isWeaponish = d is ThingDef wtd && (wtd.IsWeapon || LoadoutUtility.IsShieldApparel(wtd));
            bool short_ = isWeaponish
                ? claims.weaponMissing.TryGetValue(pawn, out Dictionary<ThingDef, int> wm) && wm.ContainsKey((ThingDef)d)
                : claims.itemMissing.TryGetValue(pawn, out Dictionary<Def, int> im) && im.ContainsKey(d);
            if (!short_) { status = FulfillStatus.Stored; return true; }

            // Fehlt wirklich - dann bleibt nur noch die zweite, eigene Frage: wird es schon hergestellt?
            // Fuer Waffen dieselbe Quelle wie bisher, fuer Gegenstaende die bildweise gecachte
            // Bestandsaufstellung (ihr inProduction ist WeaponQueuedCount, nur eben einmal pro Def statt
            // einmal pro Zelle). Ein generischer Slot hat kein einzelnes Rezept - der bleibt "fehlt".
            if (d is ThingDef ptd)
            {
                if (isWeaponish) { if (LoadoutUtility.WeaponHasActiveBill(ptd)) status = FulfillStatus.InProduction; }
                else
                {
                    LoadoutUtility.ComputeItemOwnership(ptd, out _, out _, out _, out _, out int inProd);
                    if (inProd > 0) status = FulfillStatus.InProduction;
                }
            }
            return true;
        }

        // Der Memo-Speicher umschliesst seit 2026-08-27 die GANZE Funktion. Vorher sass er mitten im
        // ThingDef-Zweig, also HINTER der Inventarabfrage und der Reihum-Rechnung - und die zwei Zweige
        // darueber (generische Defs, Munition) hatten gar keinen. Ausgerechnet die generischen sind der
        // Regelfall: ein Ausruestungspaket enthaelt "Any Medicine", "Any Perishable Meal", "Any Drugs".
        // Gemessen kostete das 69 us je Aufruf bei 3330 Aufrufen je Sekunde.
        // Gefahrlos, weil alle drei Zweige nur LESEN. Die einzige Stelle, die etwas veraendert, ist der
        // Abzug vom Rest-Topf weiter unten - und der laeuft genau einmal je Schluessel, weil der zweite
        // Aufruf hier oben schon zurueckkehrt. Das war beim alten, engeren Memo genauso.
        internal FulfillStatus SingleFulfillStatus(Pawn pawn, Def d, int needed = 1)
        {
            if (needed < 1) needed = 1;
            var memoKey = (pawn, d, needed);
            if (frameFulfillCache != null && frameFulfillCache.TryGetValue(memoKey, out FulfillStatus memoHit))
                return memoHit;
            FulfillStatus computed = SingleFulfillStatusUncached(pawn, d, needed);
            if (frameFulfillCache != null) frameFulfillCache[memoKey] = computed;
            return computed;
        }

        private FulfillStatus SingleFulfillStatusUncached(Pawn pawn, Def d, int needed)
        {
            if (d is LoadoutGenericDef gd)
            {
                if (LoadoutUtility.PawnCarriedGenericCount(pawn, gd) >= needed) return FulfillStatus.Equipped;
                if (ClaimedStatus(pawn, gd, out FulfillStatus gStatus)) return gStatus;
                // Fallback fuer Zeilen, die (noch) an keinem Kolonisten haengen - z.B. eine ungespeicherte
                // Vorschau im Loadout-Editor. >= needed statt > 0 (2026-08-17): "3 im Lager" hat einen
                // Fuenferposten nie gedeckt, die Zelle sagte trotzdem gruen.
                if (LoadoutUtility.GenericStockCount(gd) >= needed) return FulfillStatus.Stored;
                return FulfillStatus.Missing;   // no single recipe to be "in production" for
            }
            ThingDef td = d as ThingDef;
            if (td == null) return FulfillStatus.Missing;
            if (LoadoutUtility.PawnCarriedCount(pawn, td) >= needed) return FulfillStatus.Equipped;
            // Die Reihum-Rechnung schlaegt jeden der Wege darunter, wo sie diesen Kolonisten und diesen
            // Gegenstand kennt - sie ist dieselbe, aus der Bereitschaftsanzeige und Nachschub-Fenster lesen.
            if (ClaimedStatus(pawn, td, out FulfillStatus claimed)) return claimed;
            // frameOwnedCache (BuildOwnedWeaponCounts) is WEAPON-only by design (IsWeapon || shield apparel) -
            // an ammo def (arrow, bullet, ...) never appears in it at all, so checking it here always missed
            // real stock for ammo specifically, falling all the way through to "Missing" even with plenty
            // stockpiled (2026-07-31: "Arrow (stone): not owned, not stored, not in
            // production" despite 100 in a stockpile zone - Dialog_ChangeAmmo's own separate, correct
            // tooltip showed "100 in stock, 20 free" for the exact same item at the exact same time). Ammo
            // routes through the same demand-aware stock check Dialog_ChangeAmmo already uses instead.
            if (LoadoutUtility.IsAmmoDef(td))
            {
                int ammoAvail = LoadoutUtility.ComputeAmmoAvailability(td, out _, out _);
                if (ammoAvail > 0) return FulfillStatus.Stored;
            }
            else
            {
                // Memoized (2026-08-01) + a shared per-frame pool that DECREMENTS as each (pawn, td) pair is
                // first resolved - frameOwnedCache alone is a flat colony-wide total with no notion of
                // "already claimed by an earlier pawn this frame", so every pawn independently checking it
                // saw the SAME raw count and ALL read "Stored" even when stock only covered a few of them
                // (observed: 7 pawns with a satchel-charge sidearm, 2 in stock, every single one showed
                // "in storage"). Memoization matters too, not just the pool - DrawFulfillDot alone calls this
                // twice per item (segment color, then tooltip text), which would double-decrement the SAME
                // pawn's own single claim without it.
                // Die geforderte MENGE gehoert in den Schluessel (2026-08-15, Review-Befund). Ohne sie
                // entschied die ERSTE Zelle des Bildes fuer alle spaeteren mit: liegt Medizin x5 in einem
                // Pack und Medizin x1 als Einzelposten, und die Kolonie hat 3 frei, dann merkte sich die
                // Pack-Zelle 'fehlt' (3 < 5) und die Einzel-Zelle bekam dieselbe Antwort zurueck, obwohl
                // 3 >= 1 - roter Streifen und Tooltip 'fehlt' neben drei freien Stueck im Lager. In
                // umgekehrter Zeichenreihenfolge waere es ein falsches Gruen gewesen.
                // Der eigentliche Zweck des Memos bleibt: DrawFulfillDot fragt pro Gegenstand zweimal
                // (Segmentfarbe, dann Tooltip) - mit derselben Menge, also weiterhin derselbe Schluessel
                // und kein zweiter Abzug vom Bild-Vorrat.
                FulfillStatus result = FulfillStatus.Missing;
                // Der Pool kennt NUR Waffen und Schilde (BuildOwnedWeaponCounts filtert auf IsWeapon bzw.
                // Schild-Apparel). Alles andere - Medizin, Mahlzeiten, Drogen, Bauteile - steht dort nie drin,
                // der Nachschlag ging also immer ins Leere und die Zelle zeichnete "fehlt", obwohl 200 Stueck
                // im Lager lagen. Besonders irrefuehrend, weil der Tooltip DERSELBEN Zelle den echten Bestand
                // nannte: Balken und Text widersprachen sich (2026-08-09 gefixt, Schwere hoch).
                // Fuer Munition gab es dafuer laengst einen eigenen Zweig oben; Nicht-Waffen hatten nie einen.
                // Bewusst dieselbe Quelle wie der Tooltip (ComputeItemOwnership), damit die beiden per
                // Konstruktion nicht mehr auseinanderlaufen koennen - und bewusst freeStored, nicht der
                // Gesamtbestand: was andere Kolonisten schon beanspruchen, ist fuer diesen hier nicht frei.
                bool poolKnowsIt = frameRemainingCache != null && frameRemainingCache.ContainsKey(td);
                if (!poolKnowsIt && !td.IsWeapon && !LoadoutUtility.IsShieldApparel(td))
                {
                    LoadoutUtility.ComputeItemOwnership(td, out _, out _, out _, out int freeStored, out int inProd);
                    if (freeStored >= needed) result = FulfillStatus.Stored;
                    else if (inProd > 0) result = FulfillStatus.InProduction;
                    return result;
                }
                if (frameRemainingCache != null && frameRemainingCache.TryGetValue(td, out int remaining) && remaining > 0)
                {
                    frameRemainingCache[td] = remaining - 1;
                    result = FulfillStatus.Stored;
                }
                else if (LoadoutUtility.WeaponHasActiveBill(td))
                {
                    result = FulfillStatus.InProduction;
                }
                return result;
            }
            if (LoadoutUtility.WeaponHasActiveBill(td)) return FulfillStatus.InProduction;
            return FulfillStatus.Missing;
        }

        // WorstFulfillStatus ist am 2026-08-15 ersatzlos entfallen. Sie fiel beim Umbau von DrawFulfillDot
        // auf: ihr einziger Aufrufer war die alte Fassung dieser Zelle, die den ganzen Inhalt auf ihren
        // SCHLECHTESTEN Status zusammenfaltete. Seit der Harmonisierung vom 2026-07-30 zeichnet die Zelle
        // stattdessen einen Abschnitt pro Gegenstand (ein Pack mit 2 ausgeruesteten und 1 fehlenden Teil
        // zeigt gruen x2 / rot x1), und der Aufruf verschwand damals mit. Zurueckgeblieben war eine
        // Methode ohne Aufrufer im ganzen Mod - geprueft per Grep ueber Source/, ausserhalb der
        // kompilierten Artefakte kein Treffer, und sie ist internal, also auch nicht von aussen erreichbar.

        // Geforderte Stueckzahl pro Def aus einer Menge von Loadouts (Pack-Slots, "Other"-Slots). Summiert
        // ueber alle Slots, die auf denselben Def zeigen - zwei Packs mit je 3 Medizin fordern 6, nicht 3.
        // Wird als neededFor an DrawFulfillDot gegeben, damit die Mengenpruefung in SingleFulfillStatus
        // ueberhaupt eine Zahl zu vergleichen hat (2026-08-09).
        // Gibt seit 2026-08-15 das WOERTERBUCH selbst zurueck statt eines Delegaten darauf
        // (perf): das Woerterbuch braucht die Zelle ohnehin, der Delegat davor war
        // eine zweite Zuweisung auf dem Heap - pro Zelle, pro Kolonist, pro Bild. Die Nachschlageregel
        // ("unbekannt heisst 1") ist mit umgezogen, sie steht jetzt in NeededOf.
        private static Dictionary<Def, int> NeededCountsFrom(IEnumerable<LoadoutSlot> slots)
        {
            Dictionary<Def, int> need = new Dictionary<Def, int>(LoadoutUtility.DefRefComparer.Instance);   // siehe BuildClaimsUncached
            if (slots != null)
                foreach (LoadoutSlot s in slots)
                {
                    Def key = (Def)s.thingDef ?? s.genericDef;
                    if (key == null) continue;
                    need[key] = (need.TryGetValue(key, out int c) ? c : 0) + Mathf.Max(1, s.count);
                }
            return need;
        }

        private static int NeededOf(Dictionary<Def, int> need, Def d)
            => d != null && need != null && need.TryGetValue(d, out int n) ? n : 1;

        // Der koloniweite Bestandsaufriss fuer einen GEGENSTAND (Pack-Inhalt, "Sonstiges"), wie ihn der
        // reiche Tooltip zeigt. Stand am 2026-08-15 dreimal wortgleich als Lambda an drei Aufrufstellen
        // (Pack aus Rolle, Pack, Sonstiges) - eine Kopie zu aendern und die anderen zwei zu vergessen war
        // hier nur eine Frage der Zeit. Als statische Methode kostet der Aufruf ausserdem keine
        // Zuweisung auf dem Heap, waehrend jedes der drei Lambdas pro Zelle pro Bild eine brauchte,
        // auch wenn der Tooltip gar nicht gebaut wurde.
        // neededCount ist KOLONIWEIT und nicht der Bedarf dieses einen Kolonisten (2026-08-12: bei 3 auf
        // Lager und Bedarf 10 (2x5) lautet der Tooltip richtig "3 assigned in
        // storage, 7 missing"): der Bestand daneben kommt aus ComputeItemOwnership und ist koloniweit -
        // beides muss dieselbe Menge meinen, sonst vergleicht der Balken Aepfel mit Birnen und die
        // Fehlmenge faellt zu klein aus oder ganz weg.
        // need: was DIESE Zelle verlangt (2026-08-16). Vorher stand hier ColonyWideAssignedNeed, also die
        // Forderung der ganzen Kolonie - dieselbe Zahl in jeder Zelle, egal ob sie ein Stueck oder fuenf
        // will. Der BESTAND bleibt koloniweit (das ist die Frage "wo liegt es"), nur die geforderte Menge
        // ist jetzt die der Zelle - identisch zu WeaponSegFor und AmmoSegFor daneben.
        private static LoadoutUtility.AvailabilitySegments ItemSegFor(Pawn pawn, Def d, int need)
        {
            LoadoutUtility.ComputeItemOwnership(d, out int carriedOnly, out int carriedAndAssigned, out int assignedInStore, out int freeStored, out int inProduction);
            return LoadoutUtility.BuildAvailabilitySegments(carriedOnly, carriedAndAssigned, assignedInStore, freeStored,
                inProduction, pawn, d, neededCount: Mathf.Max(1, need));
        }

        // Dasselbe fuer eine WAFFE - andere Bestandsquelle (GetWeaponInventory, dieselbe, die die
        // Waffenuebersicht und der Hauptwaffen-Punkt benutzen). Die geforderte Menge bleibt beim Aufrufer:
        // der Rollen-Zweig zaehlt sie aus den Slots der Rolle, der normale Zweig aus den angehaengten
        // Zweitwaffen-Loadouts - das ist der einzige Unterschied zwischen den beiden Stellen und der
        // Grund, warum hier ein Wert und kein Flag steht.
        private static LoadoutUtility.AvailabilitySegments WeaponSegFor(Pawn pawn, Def d, int need)
            => LoadoutUtility.WeaponAvailability((ThingDef)d, pawn, need);

        // Dritte Quelle: MUNITION. Wieder eine eigene Bestandsfunktion (GetAmmoOwnership), und wieder
        // eine geforderte Menge, die nur der Aufrufer kennt - hier die Slot-Menge DIESES Kolonisten fuer
        // diese Sorte (2026-08-01: der rote Abschnitt des Balkens kam aus der einfachen
        // Statuseinstufung pro Gegenstand, waehrend dieser Tooltip gar keine geforderte Menge kannte -
        // seine Zeile "Still missing" konnte deshalb nie erscheinen, obwohl der Balken daneben rot war.
        // Die beiden widersprachen sich, statt dass einer den anderen erklaert).
        private static LoadoutUtility.AvailabilitySegments AmmoSegFor(Pawn pawn, Def d, int need)
        {
            LoadoutUtility.GetAmmoOwnership((ThingDef)d, out int carriedOnly, out int carriedAndAssigned, out int assignedInStore, out int freeStored, out int inProduction);
            return LoadoutUtility.BuildAvailabilitySegments(carriedOnly, carriedAndAssigned, assignedInStore, freeStored,
                inProduction, pawn, (ThingDef)d, neededCount: need);
        }

        // Welche Bestandsquelle der reiche Tooltip einer Zelle benutzt. Drei Domaenen, drei Funktionen -
        // das war bis 2026-08-15 ein durchgereichtes Lambda pro Aufrufstelle, sechsmal, und drei davon
        // wortgleich.
        private enum RichStock { None, Item, Weapon, Ammo }

        private static LoadoutUtility.AvailabilitySegments RichSegFor(RichStock kind, Pawn pawn, Def d, Func<Def, int> needFor)
        {
            if (kind == RichStock.Weapon) return WeaponSegFor(pawn, d, needFor != null ? needFor(d) : 1);
            if (kind == RichStock.Ammo) return AmmoSegFor(pawn, d, needFor != null ? needFor(d) : 1);
            // Dritter Zweig jetzt wie die zwei darueber: die Menge kommt vom Aufrufer (2026-08-16).
            return ItemSegFor(pawn, d, needFor != null ? needFor(d) : 1);
        }

        // EINE Aufstellung fuer Streifen UND Tooltip (2026-08-30: der Streifen der
        // Munitionszelle war komplett gruen bei 1 von 150 getragen, 500 in Produktion, 149 fehlend). Ursache:
        // der Streifen fragte SingleFulfillStatus mit Bedarf 1 (neededFor war dort null), der Tooltip AmmoSegFor
        // mit Bedarf 150 - EINE Frage, ZWEI Rechnungen, die haeufigste Fehlerklasse dieses Mods. Jetzt liest
        // der Streifen dieselbe Aufstellung wie der Tooltip, gemerkt im 0,3-s-Block der Zeilen-Caches; der
        // Tooltip trifft denselben Eintrag und kann dem Streifen deshalb nicht mehr widersprechen.
        private LoadoutUtility.AvailabilitySegments RichSegCached(RichStock kind, Pawn pawn, Def d, int need)
        {
            var key = (pawn, d, need, kind);
            if (frameRichSegCache != null && frameRichSegCache.TryGetValue(key, out LoadoutUtility.AvailabilitySegments hit)) return hit;
            LoadoutUtility.AvailabilitySegments seg = RichSegFor(kind, pawn, d, _ => need);
            if (frameRichSegCache != null) frameRichSegCache[key] = seg;
            return seg;
        }

        // Der Streifen einer Zelle zeigt ANTEILIG, wo die Stuecke sind, die DIESER Kolonist braucht - nicht den
        // Bestand der ganzen Kolonie (500 in Produktion sagen nichts ueber seine 150). Reihenfolge und Ausschluss
        // nach Vorgabe: getragen (gruen), dann was ihm die Reihum-Rechnung aus dem Lager zuteilt (blau),
        // dann Produktion NUR fuer den Rest, der sonst fehlt (beige), dann fehlt (rot). Kein Stueck steht in zwei
        // Abschnitten - die Abschnitte duerfen sich nicht ueberschneiden. Der Tooltip daneben nennt
        // weiterhin die vollen Kolonie-Zahlen; der Streifen ist ihre Uebersetzung auf diesen einen Bedarf.
        // Getragen wird GEZAEHLT (PawnCarriedCount), nicht das 1-Stueck-Kaerbchen aus BuildAvailabilitySegments:
        // bei 300 gefordert und 150 getragen soll der Streifen halb gruen sein, nicht zu 1/301.
        // Ohne Kolonisten (koloniweite Sicht) gibt es keinen Bedarf - dann bleibt die Aufstellung, wie sie ist.
        private static LoadoutUtility.AvailabilitySegments CapToNeed(LoadoutUtility.AvailabilitySegments s, Pawn pawn, Def d, int need)
        {
            if (pawn == null) return s;
            need = Mathf.Max(1, need);
            var r = new LoadoutUtility.AvailabilitySegments { pawnContext = true, missingIsPawnSpecific = true, forbidden = s.forbidden };
            int carried = d is ThingDef td
                ? (td.IsWeapon || LoadoutUtility.IsShieldApparel(td) ? CarriedCountOf(pawn, td) : LoadoutUtility.PawnCarriedCount(pawn, td))
                : LoadoutUtility.PawnCarriedGenericCount(pawn, d as LoadoutGenericDef);
            int rem = need;
            r.equippedByPawn = Mathf.Clamp(carried, 0, rem); rem -= r.equippedByPawn;
            // Zuteilung der Reihum-Rechnung = Getragenes + sein Anteil am freien Bestand (BuildClaimsUncached);
            // -1 heisst 'nicht gefragt' (keine Vorlage), dann zaehlt der freie Bestand ungeteilt.
            int alloc = ClaimedAllocation(pawn, d);
            int store = alloc >= 0 ? Mathf.Max(0, alloc - carried) : Mathf.Max(0, s.assignedInStore + s.available);
            // Der Lageranteil ist BLAU - 'ihm zugeteilt, im Lager, muss er noch holen' (2026-08-30): vier
            // Farben im Streifen, alle auf DIESEN
            // Kolonisten bezogen - gruen getragen, blau zu holen, beige in Produktion, rot fehlt. Erklaert wird das
            // nicht durch eine Gelb-Aufteilung, sondern durch einen eigenen Block oben im Tooltip (PawnBlock), der
            // genau diese vier Zahlen nennt; die koloniweite Aufstellung steht darunter als Info.
            r.assignedInStore = Mathf.Clamp(store, 0, rem); rem -= r.assignedInStore;
            r.inProduction = Mathf.Clamp(s.inProduction, 0, rem); rem -= r.inProduction;
            r.missing = rem;
            return r;
        }

        // Der obere Block eines Zellen-Tooltips (2026-08-30): die vier Farben des Streifens mit den Zahlen
        // DIESES Kolonisten - Ueberschrift 'Fuer <Name> (braucht N):', dann gruen/blau/beige/rot, dieselben Hex-Werte
        // wie im Streifen und in der koloniweiten Aufstellung, damit Farbe = Bedeutung ueberall gleich ist.
        // 'Missing' heisst: weder getragen noch im Lager fuer ihn noch in Produktion.
        private static string PawnBlock(Pawn pawn, LoadoutUtility.AvailabilitySegments part, int need)
        {
            if (pawn == null) return "";
            var sb = new System.Text.StringBuilder();
            sb.Append("<b>").Append("CER_AssignLoadouts_PawnBlockHeader".Translate(pawn.LabelShortCap, Mathf.Max(1, need).ToString()).ToString()).Append("</b>");
            sb.Append("\n  <color=#66D966>").Append("CER_AssignLoadouts_PawnBlockEquipped".Translate(part.equippedByPawn.ToString()).ToString()).Append("</color>");
            sb.Append("\n  <color=#8CB8F2>").Append("CER_AssignLoadouts_PawnBlockToFetch".Translate((part.assignedInStore + part.available).ToString()).ToString()).Append("</color>");
            sb.Append("\n  <color=#C7AD7A>").Append("CER_AssignLoadouts_PawnBlockInProduction".Translate(part.inProduction.ToString()).ToString()).Append("</color>");
            sb.Append("\n  <color=#E65959>").Append("CER_AssignLoadouts_PawnBlockMissing".Translate(part.missing.ToString()).ToString()).Append("</color>");
            return sb.ToString();
        }

        // Zellen-Tooltip fuer EINEN Posten: Name, Kolonisten-Block, Leerzeile, koloniweite Aufstellung (Info).
        // `compact`: KURZFORM ab drei Posten in einer Zelle (2026-08-30: Waffen-Erklaertext hoeher als der
        // Bildschirm: ein CE-eigenes Loadout mit fuenf Posten gab fuenf Bloecke zu je 17 Zeilen, der Kasten begann
        // oberhalb der Bildkante, bei 2560x1440). Dann steht die koloniweite Aufstellung in EINER Zeile je Posten, ohne
        // 'Carried by'/'Assigned to' - der Kolonisten-Block bleibt, er ist der Sinn der Zelle.
        private string CellItemTooltip(RichStock kind, Pawn pawn, Def d, int need, string heading, bool compact = false)
        {
            LoadoutUtility.AvailabilitySegments seg = RichSegCached(kind, pawn, d, need);
            string colonyHead = "<b>" + "CER_AssignLoadouts_ColonyBlockHeader".Translate().ToString() + "</b>";
            string colony = compact
                ? colonyHead + " " + LoadoutUtility.AvailabilityOneLiner(seg)
                : LoadoutUtility.BuildAvailabilityTooltip(seg, LocationTipFor(kind, d), heading: colonyHead);
            if (pawn == null) return heading + "\n" + colony;
            return heading + "\n" + PawnBlock(pawn, CapToNeed(seg, pawn, d, need), need) + (compact ? "\n" : "\n\n") + colony;
        }

        private static void AddSeg(ref LoadoutUtility.AvailabilitySegments sum, LoadoutUtility.AvailabilitySegments part)
        {
            sum.equippedByPawn += part.equippedByPawn;
            sum.available += part.available;
            sum.assignedInStore += part.assignedInStore;
            sum.assignedCarried += part.assignedCarried;
            sum.carriedUnassigned += part.carriedUnassigned;
            sum.inProduction += part.inProduction;
            sum.missing += part.missing;
            sum.forbidden += part.forbidden;
            sum.ground += part.ground;
        }

        // Die Orts-Aufstellung zur selben Domaene - drei Bestandsquellen, drei Aufstellungen, damit keine
        // Zelle die eines fremden Topfes zeigt. Ein generischer Slot ("Any Perishable Meal") hat keinen
        // eigenen Ort, deshalb dort null statt einer erfundenen Zeile.
        private static string LocationTipFor(RichStock kind, Def d)
        {
            if (kind == RichStock.Weapon) return LoadoutUtility.BuildWeaponLocationTooltip(d as ThingDef);
            if (kind == RichStock.Ammo) return LoadoutUtility.BuildAmmoLocationTooltip(d as ThingDef);
            return d is ThingDef td ? LoadoutUtility.BuildPackItemLocationTooltip(td) : null;
        }

        // The width reserved from a button's OWN hit-rect for the status dot's corner (see DrawFulfillDot/
        // DrawWeaponStatusDot) - reserved UNCONDITIONALLY, whether or not a dot actually gets drawn there
        // this frame (whether one would applies is data-dependent - checking that first just to decide the
        // button's geometry would be pointless extra work for a ~13px sliver). Widgets.ButtonText handles
        // background+highlight+click as one atomic call over whatever rect it's given, with no way to carve
        // a hole out of part of it - so the only way the dot can truly BLOCK the button underneath (no
        // highlight, no click registering there) is for the button's own rect to never extend under it.
        private const float DotReserveW = 13f;

        // Beschriftung des Waffenknopfes: sie endete bisher bei xMax-6 und lief damit UNTER den
        // Statuspunkt und ueber den gezeichneten Knopfrand hinaus (der Knopf selbst hoert bei
        // xMax-DotReserveW auf) - genau die '...' aus dem Befund vom 2026-08-31. Eine
        // Funktion fuer beide Ansichten, damit die Kappung nicht wieder nur an einer Stelle stimmt:
        // Karte und kompakte Zeile hatten denselben Rechenfehler, zweimal getippt.
        private static Rect LoadoutBtnLabelRect(Rect btnRect, float iconEndX, bool hasIcons) => hasIcons
            // Max(0) und nicht Max(12): steht die Ablage-Spalte im Weg UND traegt der Kolonist zwei Waffen,
            // bleibt fuer Text wirklich kein Platz - dann lieber gar keiner als drei Punkte ueber dem Statuspunkt.
            ? new Rect(iconEndX + 2f, btnRect.y, Mathf.Max(0f, btnRect.xMax - DotReserveW - 2f - (iconEndX + 2f)), btnRect.height)
            : new Rect(btnRect.x, btnRect.y, btnRect.width - DotReserveW, btnRect.height);

        // NoPasteStripW (24px, links von jeder Zelle abgezogen) ist am 2026-08-14 ersatzlos entfallen -
        // hier steht nur noch, warum, damit es niemand "wieder repariert". Der Streifen war der Platz des
        // ZELLEIGENEN Einfuege-Knopfs (DrawPasteButton, pasteW 20 + gap 4). Pack und Sidearm hatten so
        // einen, Munition und Sonstiges nicht - die zwei reservierten die Breite deshalb leer mit, damit
        // alle vier gleich breit blieben (2026-08-01). Am 2026-08-13 ist der Knopf aus den Zellen
        // verschwunden und durch EINE gemeinsame Spalte ganz rechts ersetzt worden (DrawPasteColumn);
        // Pack/Sidearm haben ihren Abzug dabei verloren, Munition/Sonstiges und saemtliche Leer- und
        // Rollen-Zweige haben ihn behalten. Ergebnis: "die buttons von pack und others sind
        // verschieden breit" - genau 24px, und zwar nur, sobald in Pack/Sidearm wirklich etwas drin war
        // (der Leerzweig zog ja weiter ab). Reserviert wird jetzt nur noch das, was es wirklich gibt: der
        // Streifen des Verfuegbarkeitsbalkens rechts (DotReserveW).

        // A small notification-style badge in a cell's own top-right corner - same spot regardless of the
        // cell's width/height, so it lines up consistently whether the cell is a small icon-only compact
        // button or a wider text button on the card. A thin dark outline keeps it readable against
        // whatever icon or highlight color happens to sit behind it. Silently does nothing if there is no
        // def to report on (e.g. "None" cells) - no badge is its own honest answer there. Returns the dot's
        // own (outlined) rect - empty (zero-size) if nothing was drawn - so the caller can skip registering
        // its own wider cell tooltip while the mouse is over the dot: RimWorld stacks EVERY TipRegion whose
        // rect contains the cursor, so a cell-wide tooltip registered on top of the dot's own small one used
        // to show BOTH at once, bleeding into each other. The dot must win outright in its own corner.
        // richStock: welche echte Bestandsaufstellung der Tooltip zeigt (koloniweit getragen/zugewiesen/
        // gelagert/in Produktion, statt nur des zusammengefallenen Urteils fuer diesen einen Kolonisten).
        // Gesetzt, bekommt der Tooltip einen vollen Block pro Gegenstand statt einer flachen Zeile
        // "- Gegenstand: Status" (2026-08-01, dieselbe geteilte Balken+Tooltip-Logik, die die Owned-Spalte
        // im Weapon-Loadouts-Tab schon benutzt - hier nur einmal pro Gegenstand, weil eine Zelle mehrere
        // buendeln kann). RichStock.None heisst flacher Text wie frueher.
        // richNeedFor: die geforderte Menge fuer Weapon/Ammo, die nur der Aufrufer kennt. Item braucht sie
        // nicht - dort ist der Bedarf koloniweit und steckt in ItemSegFor.
        //
        // War bis 2026-08-15 EIN Parameter `Func<Def, AvailabilitySegments> richSegFor`, und jede der
        // SECHS Aufrufstellen reichte ihr eigenes Lambda herein. Drei davon
        // waren wortgleich. Jetzt sagt der Aufrufer nur noch, WELCHE Bestandsquelle gilt, und die drei
        // Item-Lambdas fallen als Heap-Zuweisung ganz weg: sie wurden pro Zelle pro Kolonist pro BILD
        // gebaut und in aller Regel nie aufgerufen, denn der Tooltip-Text entsteht seit dem
        // TipWanted-Guard unten ohnehin nur unter der Maus.
        // itemLabelKey ist der SCHLUESSEL, nicht der fertige Text (2026-08-17):
        // gebraucht wird er nur im Tooltip-Zweig ganz unten, uebersetzt wurde er bisher aber an jeder der
        // sechs Aufrufstellen sofort - vier Zellen je Kolonist je Durchgang, und jede Uebersetzung legt eine
        // Zeichenkette an. Ein Schluessel-Literal legt nichts an.
        // Der Erklaertext einer gesperrten Rollen-Zelle - EINE Stelle fuer alle drei (Pack, Zweitwaffe,
        // Sonstiges), damit sie nicht wieder auseinanderlaufen: die drei Zweige hatten schon zweimal
        // unterschiedlich viel (erst fehlte in zweien die Sperre selbst, dann in einem der Streifen).
        //
        // OHNE TipWanted-Vorfilter, anders als die reichen Tooltips daneben (2026-08-23): der Text hier
        // ist zwei Translate-Aufrufe und eine Verkettung, keine koloniweite Bestandsaufnahme - der
        // Filter haette also nichts gespart und war eine zusaetzliche Bedingung zwischen "Maus ist
        // drueber" und "Text erscheint". Genau das war der Befund im Test: gesperrte Zellen
        // gaben beim Zeigen GAR KEINEN Text, waehrend dieselbe Zelle bei einem Kolonisten ohne Rolle
        // einen hatte. TooltipHandler.TipRegion prueft Mouse.IsOver ohnehin selbst.
        //
        // Das Diagnoselogging von hier ist RAUS (2026-08-23, nach dem Fund): im
        // Nachtest zeigten alle drei Zellen Text, das Log wurde gar nicht gebraucht.
        //
        // Was die Ursache war, bleibt UNBEWIESEN, und das gehoert hierher, damit es niemand spaeter als
        // geklaert liest: gebaut wurde der Wegfall des TipWanted-Vorfilters an Pack und Zweitwaffe. An der
        // dritten Zelle (Sonstiges), die im Test ebenfalls als textlos aufgefallen war, ist funktional
        // NICHTS geaendert worden - sie wurde nur in diesen Aufruf verschoben. Fuer diese eine Zelle war
        // der Befund also sehr wahrscheinlich falsch, und damit ist auch fuer die anderen beiden nicht
        // sicher, ob der Vorfilter wirklich der Grund war. Wer das hier wieder anfasst: die drei Zellen
        // laufen bewusst durch EINEN Aufruf, damit sie nicht erneut auseinanderlaufen.
        private static void RoleCellTip(Rect cell, TaggedString text)
        {
            TooltipHandler.TipRegion(cell, text);
        }

        // Wiederverwendete Puffer statt `Where().Distinct().ToList()` je Aufruf (2026-08-27, perf-cell-row-next).
        // Diese Funktion laeuft VIER MAL je Kolonist je Durchgang - bei 12 Kolonisten und 143 Durchgaengen
        // sind das rund 6900 Aufrufe je Sekunde, und jeder hat bisher vier Objekte auf dem Haufen angelegt
        // (Where-Iterator, Distinct-Iterator, dessen internes Set, die Liste), die alle im selben Bild wieder
        // Muell waren. Gemessen wurden 39 MB/s Neuanlage im Assign-Reiter; das hier ist ein Posten davon.
        // Die Liste verlaesst die Funktion nie - sie wird nur durchlaufen und fuer den Tooltip gelesen.
        // Semantik unveraendert: HashSet mit dem Standardvergleich verhaelt sich wie Distinct(), und die
        // Reihenfolge des ersten Vorkommens bleibt erhalten, weil die Liste in Durchlaufreihenfolge waechst.
        private static readonly List<Def> fulfillBuf = new List<Def>();
        private static readonly HashSet<Def> fulfillSeen = new HashSet<Def>();
        private static bool fulfillBufInUse;

        private Rect DrawFulfillDot(Rect cell, Pawn pawn, IEnumerable<Def> defs, string itemLabelKey,
            RichStock richStock = RichStock.None, Dictionary<Def, int> neededFor = null, Func<Def, int> richNeedFor = null)
        {
            // Der Wiedereintritts-Schutz ist Vorsorge, kein bekannter Fall: heute ruft nichts unterhalb dieser
            // Funktion sie erneut auf. Wer das spaeter tut, bekommt eine eigene Liste statt eines still
            // ueberschriebenen Puffers - das ist der Unterschied zwischen langsam und falsch.
            bool ownBuf = !fulfillBufInUse;
            List<Def> list;
            if (ownBuf)
            {
                fulfillBufInUse = true;
                fulfillBuf.Clear();
                fulfillSeen.Clear();
                list = fulfillBuf;
            }
            else list = new List<Def>();
            try
            {
                if (defs != null)
                    foreach (Def d in defs)
                        if (d != null && (!ownBuf || fulfillSeen.Add(d)) && (ownBuf || !list.Contains(d)))
                            list.Add(d);
                return DrawFulfillDotInner(cell, pawn, list, itemLabelKey, richStock, neededFor, richNeedFor);
            }
            finally { if (ownBuf) fulfillBufInUse = false; }
        }

        private Rect DrawFulfillDotInner(Rect cell, Pawn pawn, List<Def> list, string itemLabelKey,
            RichStock richStock, Dictionary<Def, int> neededFor, Func<Def, int> richNeedFor)
        {
            if (list.Count == 0) return default;
            // Harmonized (2026-07-30): one segment per item's own status, tallied into the shared bar model,
            // instead of collapsing the whole cell to its single WORST status - a Pack with 2 equipped slots
            // and 1 missing now actually shows that split (green x2, red x1) instead of one flat red dot.
            var seg = new LoadoutUtility.AvailabilitySegments { missingIsPawnSpecific = pawn != null, pawnContext = pawn != null };
            foreach (Def d in list)
            {
                // Reiche Zellen (alle vier heute): der Streifen rechnet mit DERSELBEN Aufstellung wie der Tooltip
                // darunter und mit dem Bedarf, den der Aufrufer nennt - anteilig auf diesen Kolonisten gekappt
                // (2026-08-30). Der Zweig darunter bleibt fuer RichStock.None.
                if (richStock != RichStock.None)
                {
                    int need = richNeedFor != null ? richNeedFor(d) : NeededOf(neededFor, d);
                    AddSeg(ref seg, CapToNeed(RichSegCached(richStock, pawn, d, need), pawn, d, need));
                    continue;
                }
                switch (SingleFulfillStatus(pawn, d, NeededOf(neededFor, d)))
                {
                    case FulfillStatus.Equipped: seg.equippedByPawn++; break;
                    // ZUGEWIESEN UND IM LAGER, nicht "frei verfuegbar" (2026-08-18: die Anzeige war gelb statt
                    // blau - "Assigned and in storage" ist blau, und die vertikale Bestandsanzeige nutzt die
                    // zentralen Farbcodes).
                    // Der Fehler sass in der Zuordnung, nicht in den Farben: die Liste
                    // dieser Zelle IST das, was dieser Kolonist braucht - ein Stueck davon im Lager ist also
                    // per Definition "ihm zugewiesen und im Lager" (blau) und nie "frei verfuegbar" (mit
                    // Kolonist im Blick gelb). Der reiche Mouseover daneben hat es die ganze Zeit richtig
                    // gesagt ("Assigned, in storage: 3"), nur der Balken nicht - dieselbe Frage, zwei
                    // Antworten.
                    // Ohne Kolonisten-Bezug bleibt es beim alten Topf: dann gibt es niemanden, dem es
                    // zugewiesen waere.
                    case FulfillStatus.Stored:
                        if (pawn != null) seg.assignedInStore++; else seg.available++;
                        break;
                    case FulfillStatus.InProduction: seg.inProduction++; break;
                    default: seg.missing++; break;
                }
            }
            // A full-height strip on the cell's right edge, not a small corner square - same width as
            // before, just as tall as the button itself now (a strip as wide as before, but as tall as the
            // button). Flush with the cell's own top/bottom (no
            // vertical poke at all) - a wider tooltip belonging to something drawn directly ABOVE/BELOW this
            // cell (e.g. the Bulk bar's own tooltip in card view) could otherwise overlap and stack both
            // tooltips at once; RimWorld draws every TipRegion whose rect contains the cursor, so overlap
            // always means both show - the fix has to be "don't let the rects overlap", not a draw-order trick.
            Rect outline = new Rect(cell.xMax - 10f, cell.y, 10f, cell.height);
            LoadoutUtility.DrawAvailabilityBar(outline, seg, horizontal: false, drawTooltip: false);
            // AB HIER NUR NOCH UNTER DER MAUS (2026-08-13). Der Balken oben muss
            // in jedem Bild gezeichnet werden, der TEXT darunter nicht - und er ist der teure Teil: pro
            // Gegenstand eine volle koloniweite Aufstellung (richSegFor -> ComputeItemOwnership,
            // ColonyWideAssignedNeed, BuildAvailabilityTooltip), zusammengesetzt zu einem mehrzeiligen
            // String, danach von RimWorld verworfen, weil die Maus woanders steht. Diese Zelle gibt es
            // viermal pro Kolonist. Siehe LoadoutUtility.TipWanted, warum das Verwerfen garantiert ist.
            if (!LoadoutUtility.TipWanted(outline)) return outline;
            string itemLabel = itemLabelKey.Translate().ToString();
            string tip;
            if (richStock != RichStock.None)
            {
                // extraTip = die "wo liegt es / wer traegt es / wem ist es zugewiesen"-Aufstellung
                // (2026-08-15, im Test gefunden: eine 'Zugewiesen an'-Liste kam in KEINEM der beiden
                // Tooltips vor - weder bei der Rolle noch beim direkt angehaengten Pack). Das war eine
                // echte Luecke: JEDER andere
                // Verfuegbarkeitsbalken des Mods reicht diese Aufstellung an BuildAvailabilityTooltip
                // durch, ausgerechnet die vier Zellen des Assign-Tabs nicht - dabei ist "wer hat die
                // Medizin eigentlich" dort die naheliegendste Frage.
                // Ohne eigenen TipWanted-Guard: dieser ganze Block sitzt schon hinter einem (oben), er
                // laeuft also nur unter der Maus.
                // Der Bedarf DIESER Zelle, nicht der der ganzen Kolonie (2026-08-16, im Test: Pack-Zelle und
                // Sonstiges-Zelle desselben Kolonisten waren fuer
                // denselben Gegenstand WORTGLEICH, und bei 3 vorhandenen Medizin meldete die 1-Stueck-Zelle
                // denselben Mangel wie die 5-Stueck-Zelle).
                // Die Waffen- und die Munitionszelle machen es laengst richtig: koloniweiter BESTAND, aber
                // die geforderte MENGE aus dem Aufrufer - genau die Mischung, die "davon fehlen dir noch X"
                // ueberhaupt beantwortbar macht. Nur die Gegenstandszelle nahm ColonyWideAssignedNeed, und
                // damit war ihre Fehlmenge in jeder Zelle dieselbe.
                // Faellt auf die Mengentabelle der Zelle zurueck (neededFor) - dieselbe, aus der auch die
                // Balkenfarbe ihre Menge nimmt. Streifen und Text reden damit endlich ueber dieselbe Zahl.
                Func<Def, int> cellNeedFor = richNeedFor ?? (dd => NeededOf(neededFor, dd));
                // Die Ueberschrift je Gegenstand geht als Parameter hinein (2026-08-17) - hier ging es bisher
                // nur deshalb gut, weil string.Join ToString() ruft und dabei die Tags stehen laesst. Das ist
                // dieselbe Bauform, an der die Waffen-Zelle ihre Farben verloren hat, nur zufaellig auf der
                // heilen Seite. Zufall ist keine Absicherung.
                // Je Posten: Kolonisten-Block oben (die Streifen-Farben mit SEINEN Zahlen), darunter die
                // koloniweite Aufstellung als Info (2026-08-30).
                tip = itemLabel + ":\n\n" + string.Join("\n\n", list.Select(d =>
                    CellItemTooltip(richStock, pawn, d, cellNeedFor(d), "<b>" + d.LabelCap.ToString() + "</b>", compact: list.Count > 2)));
            }
            else
            {
                // Per-item breakdown, not one aggregate word - "missing" alone doesn't say WHICH of several
                // items (a pack can hold several slots, a pawn can carry several ammo types) is the actual
                // gap. Same "- item: status" convention as Dialog_SquadSetup's own Supply-badge tooltip.
                // Dieselbe Mengenpruefung wie die Segmente darueber (2026-08-09) - sonst behauptet der Tooltip
                // "ausgeruestet", waehrend der Streifen daneben rot ist, und das ist genau die Sorte
                // Widerspruch, die diese Zelle schon einmal produziert hat.
                // .ToString() an jedem LabelCap (2026-08-17, dieselbe Falle wie beim Waffen-Tooltip, per
                // Regel-7-Grep gefunden): ohne das wird der ganze Einzeiler zu einer TaggedString, und ihre
                // Rueckwandlung in `string tip` loescht die Farbe, die FulfillWordColored gerade gesetzt hat -
                // das Statuswort stand hier also schwarz-weiss da. Die Mehrzeiler-Zweige kamen nur davon,
                // weil string.Join ueber ToString() geht.
                tip = list.Count == 1
                    ? itemLabel + ": " + list[0].LabelCap.ToString() + " - " + LoadoutUtility.FulfillWordColored(SingleFulfillStatus(pawn, list[0], NeededOf(neededFor, list[0]))) + "."
                    : itemLabel + ":\n" + string.Join("\n", list.Select(d => "- " + d.LabelCap.ToString() + ": " + LoadoutUtility.FulfillWordColored(SingleFulfillStatus(pawn, d, NeededOf(neededFor, d)))));
            }
            // Feste Id je Zelle (2026-08-30, siehe LoadoutUtility.DrawFetchGearButton): der Text nennt Bestand und
            // Produktion, die sich im Spiel laufend aendern - ohne feste Id geht der Kasten bei jeder Aenderung zu.
            string tipText = tip;
            TooltipHandler.TipRegion(outline, new TipSignal(() => tipText, TipId(pawn, list != null && list.Count > 0 ? list[0] : null, 0x11)));
            return outline;
        }

        // Same outline formula DrawWeaponStatusDot draws at, exposed separately so a caller can check
        // Mouse.IsOver(WeaponDotRect(...)) to guard its OWN wider button tooltip BEFORE the dot itself is
        // drawn later in the same row (draw order matters visually - the dot must paint on top of the
        // button - but the "does the dot win" mouse check does not need to wait for that). Full-height
        // strip on the button's own right edge, flush top/bottom - EXACTLY DrawFulfillDot's own geometry
        // (same width, same flush placement), fixed 2026-07-30: this used to still poke 1px above AND
        // below the button (a leftover from before the "make it flush" pass reached this one), which both
        // gave it a visibly longer bottom edge than DrawFulfillDot's dots AND put its left edge 1px further
        // from the button than theirs - the "gap looks different for Weapons vs Ammo/Pack/Sidearm" report.
        private static Rect WeaponDotRect(Rect btnRect) => new Rect(btnRect.xMax - 10f, btnRect.y, 11f, btnRect.height);

        // Weapon-button status dot - same corner-dot-plus-"dot wins" convention as DrawFulfillDot, but for the
        // single main weapon on the loadout button itself rather than a whole cell of several items. Carries
        // richer detail in its tooltip (what's actually equipped right now, not just the plan) and keeps the
        // old line's "click while queued to jump to the bench's Bills tab" behavior, since replacing a whole
        // detail row with one small dot must not quietly drop something players relied on.
        private void DrawWeaponStatusDot(Rect btnRect, Pawn pawn, ThingDef mainW)
        {
            if (mainW == null) return;
            FulfillStatus status = SingleFulfillStatus(pawn, mainW);   // fuer den Werkbank-Klick unten
            // ALLE Posten, die diese Zelle vertritt (2026-08-30) - bei unseren Vorlagen genau die
            // Hauptwaffe, bei einem CE-eigenen Loadout auch Gladius, Mahlzeiten, Medizin. Anteilig wie die
            // Nachbarzellen (CapToNeed), aus derselben Aufstellung wie der Tooltip.
            List<WeaponCellItem> items = GetRowInfo(pawn).weaponCellItems;
            var seg = new LoadoutUtility.AvailabilitySegments { missingIsPawnSpecific = pawn != null, pawnContext = pawn != null };
            if (items != null && items.Count > 0)
                for (int i = 0; i < items.Count; i++)
                    AddSeg(ref seg, CapToNeed(RichSegCached(items[i].kind, pawn, items[i].def, items[i].need), pawn, items[i].def, items[i].need));
            else
                AddSeg(ref seg, CapToNeed(RichSegCached(RichStock.Weapon, pawn, mainW, 1), pawn, mainW, 1));
            Rect outline = WeaponDotRect(btnRect);
            LoadoutUtility.DrawAvailabilityBar(outline, seg, horizontal: false, drawTooltip: false);

            // Der KLICK bleibt ungebremst - er muss in jedem Bild angeboten werden, sonst geht er verloren.
            // Nur der Tooltip-TEXT darunter wandert hinter die Maus-Pruefung (siehe LoadoutUtility.TipWanted):
            // er zieht eine volle Bestandsaufstellung und lief bisher pro Kolonist pro Bild.
            // Mouse.IsOver zuerst (2026-08-13): FindActiveBillBench ist als
            // einzige der Auftrags-Abfragen UNGECACHT und laeuft ueber jede Werkbank und jeden Auftragsstapel.
            // Der Klick darunter kann ohnehin nur ausloesen, wenn der Zeiger drin steht - also braucht auch
            // die Suche nach der Werkbank nur dann zu laufen.
            if (!Mouse.IsOver(outline)) return;
            Building_WorkTable queuedBench = status == FulfillStatus.InProduction ? LoadoutUtility.FindActiveBillBench(mainW) : null;
            if (queuedBench != null && Widgets.ButtonInvisible(outline)) LoadoutUtility.OpenBillsTab(queuedBench);
            if (!LoadoutUtility.TipWanted(outline)) return;

            ThingWithComps primary = pawn.equipment != null ? pawn.equipment.Primary : null;
            string equippedName = primary != null ? primary.LabelCap.ToString() : "CER_AssignLoadouts_NothingEquipped".Translate().ToString();
            // Rich colony-wide breakdown (2026-08-01, same treatment the Pack/Ammo/Sidearm cells already got)
            // instead of a flat "status word" line - the bar's own color still comes from `seg`/SingleFulfill
            // Status above (the fast, per-frame-claim-decrementing check correct for THIS pending plan), only
            // the TOOLTIP TEXT is upgraded to the full Total/Equipped/Available/... block.
            // Vierte Fundstelle desselben Rumpfs (2026-08-15, per Regel-7-Grep vom SYMPTOM aus - gesucht
            // war BuildAvailabilityTooltip, nicht die Funktion, die ich gerade angefasst hatte). need = 0
            // ist hier KEINE Willkuer, sondern genau das, was der Aufruf vorher tat: er liess neededCount
            // weg, und dessen Vorgabewert ist 0 - damit bleibt der rote "fehlt"-Abschnitt aus. Mit 1 haette
            // dieser Tooltip bei einer Waffe ohne Bestand plotzlich eine Fehlmenge behauptet, die die
            // anderen Zellen dort nicht zeigen.
            // Seit 2026-08-30 mit Bedarf 1 statt 0 und aus dem Cache - dieselbe Zahl wie im Streifen darueber.
            var richSeg = RichSegCached(RichStock.Weapon, pawn, mainW, 1);
            // Ueberschrift und Schlusszeile gehen als PARAMETER hinein, statt aussen herumgebaut zu werden
            // (2026-08-17: im Tooltip fehlte die Faerbung, mit Screenshots belegt). Der alte
            // Ausdruck machte aus der ganzen Kette eine TaggedString, und deren Rueckwandlung in `string tip`
            // loeschte jedes <color>, das die Aufstellung gerade eingebaut hatte - der Waffen-Tooltip war
            // deshalb als einziger schneeweiss. Begruendung in voller Laenge ueber BuildAvailabilityTooltip.
            string trailer = "CER_AssignLoadouts_CurrentlyEquippedFmt".Translate(equippedName).ToString();
            if (queuedBench != null) trailer += "CER_AssignLoadouts_QueuedOnBenchFmt".Translate(queuedBench.LabelCap).ToString();
            if (items == null || items.Count <= 1)
            {
                string oneTip = CellItemTooltip(RichStock.Weapon, pawn, mainW, 1, "<b>" + mainW.LabelCap.ToString() + "</b>") + "\n\n" + trailer;
                TooltipHandler.TipRegion(outline, new TipSignal(() => oneTip, TipId(pawn, mainW, 0x12)));
                return;
            }
            // Ein Block je Posten, wie die Pack-Zelle es fuer ihren Inhalt macht (2026-08-30: im Tooltip stehen
            // alle Posten). Die Hauptwaffe steht vorn, die Schlusszeile bleibt.
            string tip = string.Join("\n\n", items.Select(it =>
                CellItemTooltip(it.kind, pawn, it.def, it.need, "<b>" + it.def.LabelCap.ToString() + (it.need > 1 ? " x" + it.need : "") + "</b>", compact: items.Count > 2)));
            string weaponTip = tip + "\n\n" + trailer;
            TooltipHandler.TipRegion(outline, new TipSignal(() => weaponTip, TipId(pawn, mainW, 0x13)));
        }

        // Hier gehoert die VOLLE Zelle herein, nicht der Knopf-Rect - VisualCell() zieht DotReserveW
        // selbst ab. (2026-08-10 kurz falsch herum gebaut und sofort zurueckgenommen: den schon
        // verschmaelerten Knopf-Rect zu uebergeben haette den Abzug verdoppelt.)
        private static void DrawCellIcon(Rect cell, ThingDef icon, int overflowCount)
        {
            Rect vc = VisualCell(cell);
            float size = Mathf.Min(20f, vc.height - 4f);
            Rect iconRect = new Rect(vc.x + (vc.width - size) / 2f - (overflowCount > 0 ? 8f : 0f), vc.y + (vc.height - size) / 2f, size, size);
            GUI.color = Color.white;
            if (icon != null) Widgets.ThingIcon(iconRect, icon);
            if (overflowCount > 0)
            {
                // Bottom-right, not top - DrawFulfillDot's status badge already claims the cell's top-right
                // corner, and the two used to land on top of each other on a small compact icon cell.
                GUI.color = new Color(1f, 1f, 1f, 0.75f);
                Text.Font = GameFont.Tiny;
                Text.Anchor = TextAnchor.LowerLeft;
                // Das Abzeichen wird nach LINKS gerueckt, wenn es sonst hinausliefe - NICHT in der Breite
                // gekappt (2026-08-12, im Test gefunden: das Abzeichen zeigte nur '2' statt '+2' - das Plus
                // war abgeschnitten). Genau das war der Fehler der ersten Deckelung vom
                // 2026-08-10: sie begrenzte die BREITE auf vc.xMax, und sobald der Rest schmaler war als
                // der Text, schnitt Unity ihn ab. Der Text muss immer ganz da sein; verschieben darf man
                // ihn, abschneiden nicht.
                string badge = "CER_AssignLoadouts_OverflowCountFmt".Translate(overflowCount.ToString());
                float badgeW = Text.CalcSize(badge).x + 1f;
                float badgeX = Mathf.Min(iconRect.xMax - 2f, vc.xMax - badgeW);
                Widgets.Label(new Rect(Mathf.Max(vc.x, badgeX), vc.y, badgeW, vc.height - 1f), badge);
                Text.Anchor = TextAnchor.UpperLeft;
                Text.Font = GameFont.Small;
            }
            GUI.color = Color.white;
        }

        // First real item in a Loadout's own contents - a pack's first supply item, or a sidearm loadout's
        // main weapon (always added before its ammo, so it's reliably the first slot). One shared stand-in
        // icon for "what is this loadout, at a glance" wherever a whole Loadout needs to shrink to one glyph.
        internal static ThingDef RepresentativeIcon(Loadout lo)
        {
            if (lo?.OwnSlots == null) return null;
            foreach (LoadoutSlot s in lo.OwnSlots)
            {
                ThingDef td = s.thingDef ?? (s.genericDef != null ? LoadoutUtility.RepresentativeIconDef(s.genericDef) : null);
                if (td != null) return td;
            }
            return null;
        }

        // ---- Equipment Pack cell (card + compact) -----------------------------------------------------------

        // View/swap/detach the Equipment Pack attached to this pawn's current loadout. The loadout
        // button's own tooltip (LoadoutContentsTooltip) already lists a pack's contents transparently
        // once attached, since it just reads .Slots (own+parent union) - this cell is only about WHICH
        // pack, and changing it.
        private void DrawEquipmentPackCell(Rect cell, Pawn pawn, Loadout currentLoadout, bool iconOnly = false, Rect? extraHit = null)
        {
            DrawEquipmentPackCellInner(cell, pawn, currentLoadout, iconOnly, extraHit);
        }

        private void DrawEquipmentPackCellInner(Rect cell, Pawn pawn, Loadout currentLoadout, bool iconOnly, Rect? extraHit)
        {
            // Packs are pure supply, not a weapon - unlike the Ammo/Sidearm cells (which need a real Weapon
            // Loadout to hang off of), a pacifist with no weapon plan at all can still carry one (e.g. to act
            // as a dedicated doctor). Mechanically this needs no anchor changes: for a pawn with no weapon
            // loadout, `currentLoadout` is already CE's own real "None" marker object (never null), a perfectly
            // valid template to attach a pack's hidden per-pawn anchor onto - same as attaching one to a real
            // weapon template. Only a pawn who can't carry ANYTHING (a baby) is blocked outright.
            Text.Font = GameFont.Tiny;
            if (LoadoutUtility.CannotCarryAnything(pawn))
            {
                GUI.color = new Color(1f, 1f, 1f, 0.35f);
                if (iconOnly)
                {
                    // Leerzustand auf DIESELBE Geometrie wie eine gefuellte Zelle (2026-08-11) - und das ist
                    // seit 2026-08-14 die volle Zelle, siehe den Kommentar an DotReserveW.
                    DrawCellDash(cell);
                    Text.Font = GameFont.Small;
                    return;
                }
                Widgets.Label(cell, "CER_AssignLoadouts_PackDashLabel".Translate());
                GUI.color = Color.white;
                Text.Font = GameFont.Small;
                return;
            }
            // A Role-assigned pawn has no per-pawn pack of their own to show/edit here - everything they carry
            // is already baked into the Role's own flat Slots union (2026-07-31 fix). Read-only preview of the
            // Role's actual content instead of a misleading "None" that used to show even though the Role
            // plainly provides a pack - the read-only treatment built for Dialog_SquadSetup's pre-apply
            // preview never reached this LIVE, already-applied tab (card AND compact view share this one
            // method). No paste button, no click-to-open-picker - editing means editing the Role itself
            // (Roles tab), not this one colonist.
            if (RoleUtility.IsRoleLoadout(currentLoadout))
            {
                Dialog_SquadSetup.ExtractRoleDisplayContent(currentLoadout, out _, out List<Def> roleContent);
                // Volle Zelle, exakt wie jeder andere Pfad (2026-08-12 stand hier ein 24px-Streifen, damit
                // der Rollen-Zweig genauso breit zeichnete wie der normale; seit 2026-08-14 zieht ihn kein
                // Pfad mehr ab, siehe Kommentar an DotReserveW). Ein Einfuege-Knopf gehoert hier ohnehin
                // nicht hin - eine Rolle bearbeitet man im Roles-Tab, nicht pro Kolonist.
                Rect roleCell = cell;
                Rect roleBtnRect = new Rect(roleCell.x, roleCell.y, roleCell.width - DotReserveW, roleCell.height);
                // SICHTBAR deaktiviert statt nur wirkungslos (2026-08-15): ein Teil, der sich wegen einer
                // Rolle nicht aendern laesst, bekommt einen ausgegrauten Knopf - nichts Eigenes, so wie
                // Vanilla es machen wuerde.
                // Genau das tut GUI.enabled = false: RimWorlds eigener Knopf zeichnet sich damit grau und
                // nimmt keinen Klick an - kein Nachbau, keine eigene Farbe (nie
                // durchstreichen fuer "deaktiviert"). Nur die Knopfflaeche, nicht der Inhalt: Symbol,
                // Zahl und Verfuegbarkeits-Streifen darunter sind Information und bleiben lesbar.
                // Eigener Grauton statt GUI.enabled (2026-08-16, im Test paarweise gemessen:
                // gesperrte und normale Zelle lieferten beide 106,81,46, Abweichung null). GUI.enabled
                // sperrt den Klick - gezeichnet hat es nie etwas, weil RimWorlds Knopfgrafik ueber
                // GUI.DrawTexture laeuft. Siehe LoadoutUtility.TintedButton.
                LoadoutUtility.TintedButton(roleBtnRect, null, disabled: true);
                if (roleContent.Count == 0) DrawCellDash(roleCell);
                else
                {
                    ThingDef repIcon = roleContent[0] as ThingDef ?? LoadoutUtility.RepresentativeIconDef(roleContent[0] as LoadoutGenericDef);
                    DrawCellIcon(roleCell, repIcon, roleContent.Count > 1 ? roleContent.Count : 0);
                }
                Text.Font = GameFont.Small;
                // Der Verfuegbarkeits-Streifen fehlte in DIESEM Zweig komplett (2026-08-14, im Test
                // gemessen): ein Kolonist mit direkt angehaengtem Pack bekam sein farbiges Rechteck, ein
                // Kolonist mit demselben Pack ueber eine ROLLE gar keins - ausgerechnet dort, wo am ehesten
                // etwas fehlt. Der Zweig kehrt vor dem Aufruf weiter unten zurueck und hatte nie einen
                // eigenen.
                // CarriedPackSlots, dieselbe Quelle wie der normale Pfad: die kennt Pack-Inhalte AUS EINER
                // ROLLE (GetPawnPacks kennt nur direkt angehaengte Pack-Objekte, und ueber eine Rolle kommt
                // keins - genau deshalb ist diese Funktion am 13.08. entstanden). Damit beantworten beide
                // Zweige die Frage aus demselben Topf, statt zwei Rechnungen zu haben.
                // Aus GetRowInfo (Frame-Cache) - dieselbe Quelle wie der normale Zweig unten.
                List<LoadoutSlot> rolePackSlots = GetRowInfo(pawn).packSlots;
                Rect rolePackDot = DrawFulfillDot(roleCell, pawn, rolePackSlots.Select(s => (Def)s.thingDef ?? s.genericDef),
                    "CER_AssignLoadouts_LabelPack",
                    RichStock.Item, neededFor: GetRowInfo(pawn).packNeeded);
                if (!Mouse.IsOver(rolePackDot)) RoleCellTip(roleCell, (roleContent.Count > 0
                    ? "CER_AssignLoadouts_RoleIncludedInFmt".Translate(RoleUtility.DisplayLabel(currentLoadout), string.Join(", ", roleContent.Select(d => d.LabelCap.ToString())))
                    : "CER_AssignLoadouts_RoleNoPackItems".Translate())
                    + "CER_AssignLoadouts_RoleBakedSuffix".Translate());
                return;
            }

            // Kein eigener Einfuege-Streifen mehr in dieser Zelle (2026-08-13): es gibt genau EINE Spalte
            // dafuer, ganz rechts hinter "Sonstiges" - siehe DrawPasteColumn. Die Zelle bekommt dadurch die
            // Breite zurueck, die sie hier dauerhaft reserviert hatte, obwohl die Ablage immer nur ein
            // Objekt haelt.
            // Per-pawn, via the pawn's own hidden personal anchor (AmmoAssignmentUtility) - never the
            // shared template, so two colonists on the same weapon loadout can carry different packs. A
            // pawn can carry several packs at once, so this opens a checklist rather than a single-pick FloatMenu.
            List<Loadout> attached = GetRowInfo(pawn).packs;
            // Als lokale Funktion statt als Variable (2026-08-13): in der
            // kompakten Ansicht (iconOnly) braucht sie NUR der Tooltip, und der wird seit heute nur
            // noch unter der Maus gebaut. Vorher lief die Verkettung pro Zelle pro Zeile pro Bild ins
            // Leere. Direkt aufgerufen kostet eine lokale Funktion keine Zuweisung auf dem Heap.
            string Label() => attached.Count > 0 ? string.Join(", ", attached.Select(a => EquipmentPackUtility.StripMarker(a.label))) : "CER_AssignLoadouts_PackNoneLabel".Translate().ToString();
            Rect btnRect = new Rect(cell.x, cell.y, cell.width - DotReserveW, cell.height);
            bool clicked;
            if (iconOnly)
            {
                clicked = Widgets.ButtonText(btnRect, "") | (extraHit.HasValue && Widgets.ButtonInvisible(extraHit.Value));
                if (attached.Count == 0) DrawCellDash(cell);
                else DrawCellIcon(cell, RepresentativeIcon(attached[0]), attached.Count > 1 ? attached.Count : 0);
            }
            else
            {
                clicked = Widgets.ButtonText(btnRect, "CER_AssignLoadouts_PackPrefixFmt".Translate(Label().Truncate(btnRect.width - 40f))) | (extraHit.HasValue && Widgets.ButtonInvisible(extraHit.Value));
            }
            // ItemSegFor gibt die echte koloniweite Aufstellung fuer einen Pack-Gegenstand (konkret oder
            // generisch) - dieselbe Quelle, die auch der Pack-Editor benutzt, statt eines zweiten
            // Bestands-Durchlaufs hier. BuildAvailabilitySegments nimmt dort auch einen generischen Def
            // (2026-08-01: "Any Perishable Meal" zeigte immer "Equipped: 0", obwohl der
            // Kolonist eine passende Mahlzeit traegt).
            // Inhalt aus CarriedPackSlots (2026-08-13) - siehe dessen Kommentar: `attached` kennt nur direkt
            // angehaengte Pack-OBJEKTE, und was ueber eine Rolle kommt, ist keins. Genau deshalb fehlte im
            // Tooltip ausgerechnet der Inhalt, der aus der Rolle stammte.
            List<LoadoutSlot> packSlots = GetRowInfo(pawn).packSlots;
            Rect packDot = DrawFulfillDot(cell, pawn, packSlots.Select(s => (Def)s.thingDef ?? s.genericDef), "CER_AssignLoadouts_LabelPack",
                RichStock.Item, neededFor: GetRowInfo(pawn).packNeeded);
            Text.Font = GameFont.Small;
            if (!Mouse.IsOver(packDot) && LoadoutUtility.TipWanted(cell))
                TooltipHandler.TipRegion(cell, (attached.Count > 0
                    ? "CER_AssignLoadouts_PackAttachedFmt".Translate(Label())
                    : "CER_AssignLoadouts_PackNoneAttached".Translate()) + "CER_AssignLoadouts_PackClickChooseFmt".Translate(pawn.LabelShortCap));
            if (clicked)
                Find.WindowStack.Add(new Dialog_PickPacks("CER_AssignLoadouts_PickPacksTitleFmt".Translate(pawn.LabelShortCap),
                    () => AmmoAssignmentUtility.GetPawnPacks(pawn),
                    updated => AmmoAssignmentUtility.SetPawnPacks(pawn, currentLoadout, updated),
                    sourceLabel: pawn.LabelShortCap, checkProductionOnClose: true));
        }

        // A pack/sidearm selection copied via Dialog_PickPacks' own Copy button (LoadoutClipboard) can be
        // pasted straight onto another colonist from here - no need to reopen the checklist and re-tick the
        // same boxes per pawn. Only appears once something of the matching kind has actually been copied.
        // Draws in a dedicated strip carved off the LEFT edge of `cell` and returns what's left for the
        // caller's own button/icon - never overlapping the cell's own clickable area (a shared hit-region
        // with the cell behind it made this trivial to misclick before). A no-op returning `cell` unchanged
        // when nothing of the matching kind has been copied yet, so callers can unconditionally reassign
        // their own `cell` to this call's result with zero cost on the common path.
        // EINE Einfuege-Spalte fuer die ganze Zellenreihe (2026-08-13: rechts nach "Sonstiges", im
        // Leerzustand ganz verschwunden). Vorher hatte jede Zelle ihren eigenen
        // Einfuege-Streifen und reservierte dessen Breite dauerhaft - vier Streifen fuer eine Ablage, die
        // immer nur EIN Objekt haelt. Der Platz ging an vier Stellen verloren, und
        // genutzt wurde immer nur eine.
        // Die Spalte weiss selbst, was gerade in der Ablage liegt, und schreibt es an die richtige Stelle -
        // deshalb steht die Zuordnung Kennung -> Schreibziel hier und nicht mehr in den Zellen.
        // PasteColW ist 0, wenn nichts Passendes in der Ablage liegt: die Spalte verschwindet dann ganz und
        // die vier Zellen bekommen den Platz.
        internal const float PasteColW = 26f;

        // Der cannotFight-Schutz aus DrawSidearmCell, hierher gezogen, damit ihn beide Ansichten benutzen
        // (2026-08-13). Einfuegen umgeht die Klick-Sperre der Zelle - ohne diese Pruefung bekaeme ein
        // Pazifist eine Zweitwaffe, sobald irgendwo Kopieren gedrueckt wurde (Fix vom 2026-08-01).
        internal static void PasteSidearmsGuarded(Pawn pawn, Loadout currentLoadout, List<Loadout> updated)
        {
            if (LoadoutUtility.CannotFight(pawn))
            {
                Messages.Message("CER_AssignLoadouts_CannotFightMsg".Translate(pawn.LabelShortCap), MessageTypeDefOf.RejectInput, false);
                return;
            }
            SidearmAssignmentUtility.SetPawnSidearms(pawn, currentLoadout, updated);
        }

        internal static bool ClipboardHasAny() => LoadoutClipboard.HasContent("pack")
            || LoadoutClipboard.HasContent("sidearm") || LoadoutClipboard.HasContent("ammo")
            || LoadoutClipboard.HasContent("other") || LoadoutClipboard.HasContent("mainweapon");

        // Sonstige Gegenstaende eines Kolonisten uebernehmen (2026-08-25: "Add other item" hatte keinen
        // Copy-Knopf und damit auch kein Einfuegen). Gleiche Form wie PasteAmmo:
        // auf den persoenlichen Anker, erst die eigenen Sonstiges-Slots raeumen (alles, was weder Waffe noch
        // Munition ist - genau die Menge, die DrawOtherItemsCellInner zeigt), dann die kopierten setzen.
        // Eine leere Ablage raeumt nur, wie bei Packs. Ohne Vorlage laesst sich kein Anker aufspannen - dann
        // false, und der Aufrufer meldet nichts (wie PasteAmmo). CEs "None"-Loadout reicht als Vorlage, genau
        // wie bei Packs und Zweitwaffen.
        internal static bool PasteOtherItems(Pawn pawn, Loadout currentLoadout, List<LoadoutSlot> slots)
        {
            if (pawn == null) return false;
            Loadout template = AmmoAssignmentUtility.DisplayLoadout(currentLoadout) ?? currentLoadout ?? pawn.GetLoadout();
            if (template == null) return false;
            Loadout link = AmmoAssignmentUtility.EnsureAmmoLink(pawn, template);
            if (link == null) return false;
            foreach (LoadoutSlot own in link.OwnSlots.Where(s => s.thingDef != null && !s.thingDef.IsWeapon && !LoadoutUtility.IsAmmoDef(s.thingDef)).ToList())
                link.RemoveSlot(own);
            foreach (LoadoutSlot s in slots ?? new List<LoadoutSlot>())
                if (s.thingDef != null) link.AddSlot(new LoadoutSlot(s.thingDef, s.count));
            CombatReadiness.Invalidate();
            return true;
        }

        // Munition eines Kolonisten uebernehmen (2026-08-13). Schreibt auf denselben persoenlichen Anker,
        // den auch das Munitions-Fenster benutzt: erst dessen eigene Munitionsslots raeumen, dann die
        // kopierten setzen. SetAmmoCleared wie dort - bleibt nach dem Einfuegen gar keine Munition uebrig,
        // muss der Anker ausdruecklich als "geleert" gelten, sonst kommt die Vorlagen-Voreinstellung still
        // zurueck (siehe Dialog_ChangeAmmo, derselbe Fall beim Abhaken der letzten Sorte).
        internal static bool PasteAmmo(Pawn pawn, Loadout currentLoadout, List<LoadoutSlot> slots)
        {
            if (pawn == null) return false;
            // pawn.GetLoadout() als dritter Rueckgriff wie in PasteOtherItems: ohne ihn war ein Kolonist
            // mit null-currentLoadout der eine Pfad, auf dem der Klick GAR NICHTS sagte - kein Schreiben,
            // keine Meldung (return false heisst fuer den Aufrufer "schon gemeldet", was hier nie stimmte).
            Loadout template = AmmoAssignmentUtility.DisplayLoadout(currentLoadout) ?? currentLoadout ?? pawn.GetLoadout();
            if (template == null) return false;
            Loadout link = AmmoAssignmentUtility.EnsureAmmoLink(pawn, template);
            if (link == null) return false;

            // Leere Ablage raeumt das Ziel auf - der Vertrag steht seit je an LoadoutClipboard.CopyAmmo
            // ("einfuegen raeumt beim Ziel auf"), war aber nie gebaut: eine leere Kopie lief in die
            // "passt nichts"-Meldung bzw. tat still gar nichts, und die Einfuege-Spalte versprach ein
            // Einfuegen, das es strukturell nicht geben konnte (Lauf 2026-08-26, Rollen-Kolonist).
            List<LoadoutSlot> srcAll = slots ?? new List<LoadoutSlot>();
            if (srcAll.Count == 0)
            {
                foreach (LoadoutSlot own in link.OwnSlots.Where(s => LoadoutUtility.IsAmmoDef(s.thingDef) || s.genericDef != null).ToList())
                    link.RemoveSlot(own);
                AmmoAssignmentUtility.SetAmmoCleared(link, true);
                LoadoutUtility.InvalidateAmmoOwnership();
                return true;
            }

            // Nur uebernehmen, was zu einer Waffe DIESES Kolonisten passt (2026-08-13, Variante
            // "b) mit deutlicher Warnmeldung").
            // Vorher landete alles unveraendert auf seinem Anker - und weil eine eigene Munitionsliste JEDE
            // geerbte verdraengt (Patch_Loadout_MultiParentSlots), stand ein Kolonist mit anderer Waffe danach
            // ganz ohne passende Munition da: seine eigenen Sorten weg, die eingefuegten gehoeren zu keiner
            // seiner Waffen. Im Test genau so gemessen (.45 Colt auf einen KS-23-Traeger).
            // Bleibt nichts uebrig, wird GAR NICHTS geschrieben - sonst waere er nach dem Einfuegen schlechter
            // dran als vorher, und das ist der Fall, den (b) ausdruecklich verhindern soll.
            var fitting = new HashSet<Def>(LoadoutUtility.DefRefComparer.Instance);   // siehe BuildClaimsUncached
            if (link.Slots != null)
                foreach (LoadoutSlot ws in link.Slots)
                    if (ws.thingDef != null && ws.thingDef.IsWeapon && !LoadoutUtility.IsSelfAmmoWeapon(ws.thingDef))
                        foreach (Def a in LoadoutUtility.GetLoadoutAmmoDefs(ws.thingDef, false)) fitting.Add(a);

            List<LoadoutSlot> src = slots ?? new List<LoadoutSlot>();
            List<LoadoutSlot> usable = src.Where(s => fitting.Contains((Def)s.thingDef ?? s.genericDef)).ToList();
            int dropped = src.Count - usable.Count;
            if (usable.Count == 0)
            {
                Messages.Message("CER_AssignLoadouts_PasteAmmoNoneFitsFmt".Translate(pawn.LabelShortCap), MessageTypeDefOf.RejectInput, false);
                return false;
            }
            if (dropped > 0)
                Messages.Message("CER_AssignLoadouts_PasteAmmoPartialFmt".Translate(
                    usable.Count.ToString(), dropped.ToString(), pawn.LabelShortCap), MessageTypeDefOf.CautionInput, false);
            slots = usable;
            foreach (LoadoutSlot own in link.OwnSlots.Where(s => LoadoutUtility.IsAmmoDef(s.thingDef) || s.genericDef != null).ToList())
                link.RemoveSlot(own);
            foreach (LoadoutSlot s in slots ?? new List<LoadoutSlot>())
                link.AddSlot(s.thingDef != null ? new LoadoutSlot(s.thingDef, s.count) : new LoadoutSlot(s.genericDef, s.count));
            // IsAmmoSlot (2026-08-14): eingefuegt werden auch generische Eintraege ("Any ammo for X"). Mit
            // dem alten Nur-Konkret-Test galt ein rein generisches Einfuegen als "gar keine Munition" und
            // markierte den Kolonisten als bewusst leer - er stand damit spaeter ohne alles da.
            AmmoAssignmentUtility.SetAmmoCleared(link, !link.OwnSlots.Any(LoadoutUtility.IsAmmoSlot));
            LoadoutUtility.InvalidateAmmoOwnership();
            // Beim TEILfall hat PasteAmmo schon selbst gemeldet, was uebernommen und was weggelassen wurde -
            // dann waere die pauschale Erfolgsmeldung des Aufrufers eine zweite, ungenauere Aussage ueber
            // denselben Klick. false heisst hier also nicht "fehlgeschlagen", sondern "ich habe es schon gesagt".
            return dropped == 0;
        }

        // Zeichnet die Spalte und schreibt beim Klick dorthin, wo der Inhalt hingehoert.
        internal static void DrawPasteColumn(Rect r, Pawn pawn, Loadout currentLoadout,
            Action<List<Loadout>> applyPacks, Action<List<Loadout>> applySidearms, Action<Loadout> applyMain = null)
        {
            bool isPack = LoadoutClipboard.HasContent("pack");
            bool isSide = LoadoutClipboard.HasContent("sidearm");
            bool isAmmo = LoadoutClipboard.HasContent("ammo");
            // Sonstiges als vierte Sorte (2026-08-25: "Add other item" hatte keinen Copy-Knopf und damit
            // auch kein Einfuegen) - Slot-Kopien wie bei Munition, siehe
            // LoadoutClipboard.CopyOther und PasteOtherItems.
            bool isOther = LoadoutClipboard.HasContent("other");
            // Hauptwaffen-Zuweisung als fuenfte Sorte (2026-09-03, "choose a weapon soll auch einen copy
            // button bekommen, und beim einfuegen dann beruecksichtigt werden") - kopiert im
            // Auswahlfenster, eingefuegt hier.
            bool isMain = applyMain != null && LoadoutClipboard.HasContent("mainweapon");
            if (!isPack && !isSide && !isAmmo && !isOther && !isMain) return;
            string label = (isPack ? "CER_AssignLoadouts_LabelPack"
                : isSide ? "CER_AssignLoadouts_LabelSidearmsPlural"
                : isOther ? "CER_AssignLoadouts_LabelOther"
                : isMain ? "CER_AssignLoadouts_LabelMainWeapon" : "CER_AssignLoadouts_LabelAmmo").Translate();
            // Ein sichtbarer Streifen, nicht nur ein Hervorheben unter der Maus (2026-09-04, zweiter Befund
            // derselben Sache): eine Spalte, die man erst sieht, wenn die Maus zufaellig darauf steht, ist
            // fuer jeden unsichtbar, der sie SUCHT. Dezent bleibt sie trotzdem - dieselbe Zurueckhaltung wie
            // beim Symbol ("das paste icon ist etwas zu zach, mach das dezenter").
            GUI.color = new Color(1f, 1f, 1f, 0.07f);
            GUI.DrawTexture(r, BaseContent.WhiteTex);
            GUI.color = Color.white;
            if (Mouse.IsOver(r)) Widgets.DrawHighlight(r);
            // DAS SYMBOL, das beim Zusammenlegen verlorengegangen ist (2026-08-23, im Test aufgefallen:
            // "'Copy' verweist auf eine Einfuege-Spalte, die ich nicht gefunden habe" - an vier Stellen gesucht).
            // Kein Wunder: diese Spalte zeichnete AUSSER einem Hervorheben
            // unter der Maus nichts. Sie war eine unsichtbare Klickflaeche, und ein Erklaertext, den man
            // nur findet, wenn man zufaellig darueberfaehrt, hilft niemandem.
            // Die abgeloeste zelleigene Fassung (DrawPasteButton, direkt darunter, hat seit dem
            // Zusammenlegen keine Aufrufer mehr) hat das Symbol die ganze Zeit gezeichnet - beim
            // Umbau ist genau diese eine Zeile nicht mitgewandert. Gleiche Deckkraft wie dort, damit
            // die Spalte dezent bleibt: das Einfuege-Symbol soll dezent und weiss sein, nicht bunt.
            GUI.color = new Color(1f, 1f, 1f, 0.65f);
            LoadoutUtility.DrawPasteIcon(r.ContractedBy(2f));
            GUI.color = Color.white;

            if (Widgets.ButtonInvisible(r))
            {
                // Fehlt diese Zeile, waehrend die MouseUp-Zeile darueber steht, liegt es am Klick selbst
                // und nicht an einer der Sperren darunter.
                // Die Vollzugsmeldung erst NACH der Vorbedingung (2026-08-13, im Test gefunden):
                // vorher lief sie unbedingt, auch wenn das Ziel die Zweitwaffe gar nicht annehmen konnte -
                // "Pasted..." fuer etwas, das nie passiert ist. Die Pruefung steht hier und nicht nur in den
                // beiden apply-Aktionen, weil nur hier bekannt ist, ob die MELDUNG noch kommen soll.
                // DREI Wege, EINE Rueckmeldungsregel (2026-08-13, im Test: Munition meldete immer etwas,
                // auch faelschlich; Packs und Zweitwaffen meldeten gar nichts - als Spieler wusste man nach
                // dem Klick nicht, ob etwas passiert ist).
                // Zwei getrennte Fehler steckten darin. Erstens lief die pauschale Erfolgsmeldung frueher
                // UNBEDINGT, auch wenn PasteAmmo gerade abgelehnt hatte - dann standen "Nichts eingefuegt"
                // und "Eingefuegt" gleichzeitig da. Zweitens gab es sie fuer Packs und Zweitwaffen gar nicht,
                // weil deren apply-Aktionen still sind.
                // Jetzt sagt der Schreibende zurueck, ob er wirklich geschrieben hat (PasteAmmo), und die
                // Erfolgsmeldung kommt genau dann - fuer alle drei Sorten gleich.
                // Rollen-Kolonisten nehmen nichts an (2026-08-25): ihre Pack-, Zweitwaffen-
                // und Sonstiges-Zellen sind gesperrt ("edit the Role itself"), die Einfuege-Spalte schrieb aber
                // trotzdem auf den Anker - unsichtbar, und ohne Rueckweg ausser einer leeren Ablage. Munition
                // bleibt erlaubt, die Munitionszelle ist bei Rollen bearbeitbar.
                if ((isPack || isSide || isOther) && RoleUtility.IsRoleLoadout(currentLoadout))
                {
                    Messages.Message("CER_AssignLoadouts_PasteRoleRejectMsg".Translate(pawn.LabelShortCap), MessageTypeDefOf.RejectInput, false);
                }
                else if ((isSide || isMain) && LoadoutUtility.CannotFight(pawn))
                {
                    Messages.Message("CER_AssignLoadouts_CannotFightMsg".Translate(pawn.LabelShortCap), MessageTypeDefOf.RejectInput, false);
                }
                // Dieselbe Grenze fuer PACKS wie die Pack-Zelle selbst (2026-08-15, Review-Befund): die
                // Zelle eines Babys zeigt nur einen Strich, die Einfuege-Spalte hat frueher trotzdem
                // geschrieben und "Eingefuegt" gemeldet. Danach hing das Pack an einem Kolonisten, der es
                // ueber die eigene Zelle nicht mehr loswerden konnte - es gab dort keinen Bedienweg mehr.
                // Die Vorschau-Zwillingsstelle in Dialog_SquadSetup prueft das laengst; hier stand es nur
                // fuer Zweitwaffen. Als Meldung und nicht als verschwundener Knopf, aus demselben Grund
                // wie eine Zeile darueber: ein Klick ohne jede Rueckmeldung war schon einmal ein Fehlerbericht.
                else if ((isPack || isOther) && LoadoutUtility.CannotCarryAnything(pawn))
                {
                    Messages.Message("CER_AssignLoadouts_CannotCarryMsg".Translate(pawn.LabelShortCap), MessageTypeDefOf.RejectInput, false);
                }
                else
                {
                    bool announce = true;
                    if (isPack) applyPacks(LoadoutClipboard.Items);
                    else if (isSide) applySidearms(LoadoutClipboard.Items);
                    else if (isMain)
                    {
                        // Nur eine noch existierende Vorlage einfuegen - die Ablage kann eine inzwischen
                        // geloeschte halten (z.B. nach 'Delete own loadouts').
                        Loadout mlo = (LoadoutClipboard.Items ?? new List<Loadout>())
                            .FirstOrDefault(l => l != null && LoadoutManager.Loadouts.Contains(l));
                        if (mlo != null) applyMain(mlo);
                        else announce = false;
                    }
                    else if (isOther) announce = PasteOtherItems(pawn, currentLoadout, LoadoutClipboard.OtherSlots);
                    else announce = PasteAmmo(pawn, currentLoadout, LoadoutClipboard.AmmoSlots);
                    if (announce)
                        Messages.Message("CER_AssignLoadouts_PastedMsgFmt".Translate(label, LoadoutClipboard.SourceLabel, pawn.LabelShortCap), MessageTypeDefOf.TaskCompletion, false);
                }
            }
            if (LoadoutUtility.TipWanted(r)) TooltipHandler.TipRegion(r, "CER_AssignLoadouts_PasteTipFmt".Translate(label, LoadoutClipboard.SourceLabel, pawn.LabelShortCap));
        }

        // kind = feste Kennung ("pack"/"sidearm"), label = was in Meldung und Tooltip steht. Getrennt seit
        // 2026-08-13: vorher war beides derselbe uebersetzte String, und der Knopf erschien nur, wenn zwei
        // verschiedene Keyed-Schluessel zufaellig denselben Wert hatten (siehe LoadoutClipboard.HasContent).
        //
        // OHNE AUFRUFER seit dem Zusammenlegen auf die gemeinsame Spalte (DrawPasteColumn, direkt darueber).
        // Absichtlich stehen geblieben ist das nicht - es ist ein Rest, und er war 2026-08-23 sogar
        // schaedlich: er hielt als einziger das Einfuege-Symbol, waehrend die Spalte, die ihn abgeloest hat,
        // gar nichts zeichnete. Wer hier etwas aendert, aendert wahrscheinlich die falsche Stelle - die
        // benutzte ist DrawPasteColumn.
        internal static Rect DrawPasteButton(Rect cell, string kind, string label, Pawn pawn, Action<List<Loadout>> apply)
        {
            const float pasteW = 20f, gap = 4f;
            // Reserve this strip's width UNCONDITIONALLY, even when there's nothing in the clipboard for
            // `kind` right now (2026-08-01: "half the icons aren't centered") - the old early
            // return handed callers the FULL cell whenever the clipboard was empty, so DrawCellIcon/
            // DrawCellDash/DrawCellWarning centered themselves differently depending on clipboard state:
            // narrower (paste strip reserved) the moment a player copies something for that kind, full width
            // before that. Comparing rows/cells where clipboard state happened to differ made icons look
            // randomly misaligned relative to each other. Always returning the same narrowed remainder keeps
            // every icon-only cell's own centering stable regardless of clipboard state - only the paste
            // button ITSELF stays conditional.
            Rect remainder = new Rect(cell.x + pasteW + gap, cell.y, cell.width - pasteW - gap, cell.height);
            if (!LoadoutClipboard.HasContent(kind)) return remainder;
            Rect pasteRect = new Rect(cell.x, cell.y, pasteW, cell.height);
            if (Mouse.IsOver(pasteRect)) Widgets.DrawHighlight(pasteRect);
            // White, not colored - the paste icon should be subtle and white, not colorful.
            // Dimmed slightly (not full-opacity white) to stay subtle next to the
            // cell's own content instead of drawing the eye first.
            GUI.color = new Color(1f, 1f, 1f, 0.65f);
            LoadoutUtility.DrawPasteIcon(pasteRect.ContractedBy(2f));
            GUI.color = Color.white;
            if (Widgets.ButtonInvisible(pasteRect))
            {
                apply(LoadoutClipboard.Items);
                Messages.Message("CER_AssignLoadouts_PastedMsgFmt".Translate(label, LoadoutClipboard.SourceLabel, pawn.LabelShortCap), MessageTypeDefOf.TaskCompletion, false);
            }
            if (LoadoutUtility.TipWanted(pasteRect)) TooltipHandler.TipRegion(pasteRect, "CER_AssignLoadouts_PasteTipFmt".Translate(label, LoadoutClipboard.SourceLabel, pawn.LabelShortCap));
            return remainder;
        }

        // Mirrors DrawEquipmentPackCell exactly, one column over - this dialog had a Pack cell but no
        // Sidearm cell, meaning a pawn's sidearm could only ever be set from Dialog_SquadSetup's batch
        // preview, never from this direct per-pawn view. Same immediate-apply contract as the Pack cell:
        // no pending pin here, the checklist mutates the pawn's actual assignment right away.
        private void DrawSidearmCell(Rect cell, Pawn pawn, Loadout currentLoadout, bool iconOnly = false, Rect? extraHit = null)
        {
            DrawSidearmCellInner(cell, pawn, currentLoadout, iconOnly, extraHit);
        }

        private void DrawSidearmCellInner(Rect cell, Pawn pawn, Loadout currentLoadout, bool iconOnly, Rect? extraHit)
        {
            // Keine Hauptwaffe noetig (2026-08-25): Balork hatte "Weapon: None", eine eingefuegte
            // Hellebarde erhoehte seine Sperrigkeit, die Zelle zeigte sie aber nicht. Zweitwaffen ohne
            // Hauptwaffe zuzulassen ging, solange es keine technische Grenze gibt.
            // Es gibt keine: die Zweitwaffe haengt wie ein Pack am persoenlichen Anker des Kolonisten
            // (SetPawnSidearms -> EnsureAmmoLink), und der laesst sich auch ueber CEs "None"-Loadout
            // aufspannen - genau das hat das Einfuegen bewiesen. Nur diese Zelle sperrte auf "hat ein
            // Loadout" und war damit das Schlupfloch: Einfuegen schrieb, die Zelle schwieg, und erst eine
            // spaeter zugewiesene Waffe machte die laengst haengende Zweitwaffe sichtbar.
            // Jetzt dieselbe Grenze wie die Pack-Zelle: nur wer gar nichts tragen kann, bekommt den Strich.
            // Der Nullfall bleibt (ein Pawn ohne Inventar hat gar kein Loadout-Objekt).
            Text.Font = GameFont.Tiny;
            if (currentLoadout == null || LoadoutUtility.CannotCarryAnything(pawn))
            {
                GUI.color = new Color(1f, 1f, 1f, 0.35f);
                if (iconOnly)
                {
                    // Leerzustand auf DIESELBE Geometrie wie eine gefuellte Zelle (2026-08-11) - und das ist
                    // seit 2026-08-14 die volle Zelle, siehe den Kommentar an DotReserveW.
                    DrawCellDash(cell);
                    Text.Font = GameFont.Small;
                    return;
                }
                Widgets.Label(cell, "CER_AssignLoadouts_SidearmDashLabel".Translate());
                GUI.color = Color.white;
                Text.Font = GameFont.Small;
                return;
            }
            // Non-combatants (pacifists, babies - the same CannotFight rule the Weapon cell/drag-swap path use)
            // can't be handed a NEW sidearm either - this cell used to have no such gate at all, letting a
            // CannotFight pawn with an existing loadout still open the full sidearm picker and get a weapon
            // attached despite being unable to use it.
            bool cannotFight = LoadoutUtility.CannotFight(pawn);
            // Same Role read-only treatment as the Pack cell above - see its own comment (2026-07-31 fix).
            if (RoleUtility.IsRoleLoadout(currentLoadout))
            {
                Dialog_SquadSetup.ExtractRoleDisplayContent(currentLoadout, out List<ThingDef> roleContent, out _);
                // Volle Zelle, exakt wie jeder andere Pfad (2026-08-12 stand hier ein 24px-Streifen, damit
                // der Rollen-Zweig genauso breit zeichnete wie der normale; seit 2026-08-14 zieht ihn kein
                // Pfad mehr ab, siehe Kommentar an DotReserveW). Ein Einfuege-Knopf gehoert hier ohnehin
                // nicht hin - eine Rolle bearbeitet man im Roles-Tab, nicht pro Kolonist.
                Rect roleCell = cell;
                Rect roleBtnRect = new Rect(roleCell.x, roleCell.y, roleCell.width - DotReserveW, roleCell.height);
                // SICHTBAR deaktiviert, wie in der Pack-Zelle (2026-08-15): bei Rollen nicht benutzbare
                // Knoepfe sind ausgegraut, so wie Vanilla es macht. Umgesetzt war das zuerst nur an
                // EINER von DREI Stellen.
                // Es gibt drei Rollen-Zweige in dieser Datei (Pack, Zweitwaffe, Sonstiges); nur der
                // erste bekam GUI.enabled = false, die anderen zwei zeichneten weiter einen ganz
                // normalen Knopf - und der Test hatte genau die eine Zelle geprueft, die schon gefixt war.
                // Eigener Grauton statt GUI.enabled (2026-08-16, im Test paarweise gemessen:
                // gesperrte und normale Zelle lieferten beide 106,81,46, Abweichung null). GUI.enabled
                // sperrt den Klick - gezeichnet hat es nie etwas, weil RimWorlds Knopfgrafik ueber
                // GUI.DrawTexture laeuft. Siehe LoadoutUtility.TintedButton.
                LoadoutUtility.TintedButton(roleBtnRect, null, disabled: true);
                if (roleContent.Count == 0) DrawCellDash(roleCell);
                else DrawCellIcon(roleCell, roleContent[0], roleContent.Count > 1 ? roleContent.Count : 0);
                Text.Font = GameFont.Small;
                // Zweite Fundstelle desselben Lochs (2026-08-14, per Suche vom SYMPTOM aus - aufgefallen
                // war nur die Pack-Zelle): auch der Rollen-Zweig der Zweitwaffen-Zelle zeichnete gar keinen
                // Verfuegbarkeits-Streifen. Dieselbe Bestandsquelle wie der normale Pfad unten
                // (GetWeaponInventory), die geforderte Menge aus den Slots der Rolle selbst.
                Rect roleSideDot = DrawFulfillDot(roleCell, pawn, roleContent.Cast<Def>(),
                    "CER_AssignLoadouts_LabelSidearm",
                    RichStock.Weapon, richNeedFor: d => currentLoadout?.Slots?.Where(s => s.thingDef == (ThingDef)d).Sum(s => Mathf.Max(1, s.count)) ?? 1);
                if (!Mouse.IsOver(roleSideDot)) RoleCellTip(roleCell, (roleContent.Count > 0
                    ? "CER_AssignLoadouts_RoleIncludedInFmt".Translate(RoleUtility.DisplayLabel(currentLoadout), string.Join(", ", roleContent.Select(d => d.LabelCap.ToString())))
                    : "CER_AssignLoadouts_RoleNoSidearmItems".Translate())
                    + "CER_AssignLoadouts_RoleBakedSuffix".Translate());
                return;
            }

            // Kein eigener Einfuege-Streifen mehr in dieser Zelle (2026-08-13) - es gibt eine gemeinsame
            // Spalte ganz rechts, siehe DrawPasteColumn. Der cannotFight-Schutz von 2026-08-01 ist dorthin
            // mitgewandert und NICHT entfallen: er hing an genau diesem Callback und war noetig, weil das
            // Einfuegen die Klick-Sperre weiter unten umgeht - ein Pazifist bekam sonst eine Zweitwaffe,
            // sobald irgendwo Kopieren gedrueckt worden war.
            List<Loadout> attached = GetRowInfo(pawn).sidearms;
            // Als lokale Funktion statt als Variable (2026-08-13): in der
            // kompakten Ansicht (iconOnly) braucht sie NUR der Tooltip, und der wird seit heute nur
            // noch unter der Maus gebaut. Vorher lief die Verkettung pro Zelle pro Zeile pro Bild ins
            // Leere. Direkt aufgerufen kostet eine lokale Funktion keine Zuweisung auf dem Heap.
            // CellLabel statt des rohen Etiketts (2026-08-18, im Test aufgefallen: der Mouseover sagte
            // "Sidearms attached: W| satchel charge", waehrend dieselbe Zweitwaffe in RimWorlds Loadout-Spalte
            // und im Dropdown der Karte laengst ohne Vorsilbe steht). Vergessene Fundstelle desselben Fixes -
            // die Pack-Zelle zwei Bildschirmseiten darueber raeumt ihre Etiketten seit dem 16.08. auf.
            string Label() => attached.Count > 0 ? string.Join(", ", attached.Select(a => LoadoutUtility.CellLabel(a))) : "CER_AssignLoadouts_SidearmNoneLabel".Translate().ToString();
            Rect btnRect = new Rect(cell.x, cell.y, cell.width - DotReserveW, cell.height);
            bool clicked;
            if (iconOnly)
            {
                clicked = Widgets.ButtonText(btnRect, "") | (extraHit.HasValue && Widgets.ButtonInvisible(extraHit.Value));
                if (attached.Count == 0) DrawCellDash(cell);
                else DrawCellIcon(cell, RepresentativeIcon(attached[0]), attached.Count > 1 ? attached.Count : 0);
            }
            else
            {
                clicked = Widgets.ButtonText(btnRect, "CER_AssignLoadouts_SidearmPrefixFmt".Translate(Label().Truncate(btnRect.width - 52f))) | (extraHit.HasValue && Widgets.ButtonInvisible(extraHit.Value));
            }
            // RepresentativeIcon resolves each attached sidearm Loadout to its own main weapon ThingDef - the
            // weapon domain (GetWeaponInventory), not the pack-item or ammo-slot domains, so the rich tooltip
            // uses the SAME empire-wide-stock function the main weapon dot below (and the Weapon Loadouts
            // tab's own Owned column) already calls.
            // neededCount = wie viele angehaengte Zweitwaffen-Loadouts auf genau diese Waffe hinauslaufen,
            // jedes mit seiner eigenen Slot-Menge (2026-08-01, dieselbe globale Korrektur wie in der
            // Munitionszelle darueber).
            Rect sidearmDot = DrawFulfillDot(cell, pawn, GetRowInfo(pawn).sidearmIcons, "CER_AssignLoadouts_LabelSidearm",
                RichStock.Weapon, richNeedFor: d => attached.Where(a => RepresentativeIcon(a) == (ThingDef)d)
                    .Sum(a => a.Slots?.FirstOrDefault(s => s.thingDef == (ThingDef)d)?.count ?? 1));
            Text.Font = GameFont.Small;
            if (!Mouse.IsOver(sidearmDot))
            {
                if (cannotFight) TooltipHandler.TipRegion(cell, "CER_AssignLoadouts_CannotFightTip".Translate());
                else if (LoadoutUtility.TipWanted(cell)) TooltipHandler.TipRegion(cell, (attached.Count > 0
                    ? "CER_AssignLoadouts_SidearmAttachedFmt".Translate(Label())
                    : "CER_AssignLoadouts_SidearmNoneAttached".Translate()) + "CER_AssignLoadouts_SidearmClickChooseFmt".Translate(pawn.LabelShortCap));
            }
            if (clicked && cannotFight)
            {
                Messages.Message("CER_AssignLoadouts_CannotFightMsg".Translate(pawn.LabelShortCap), MessageTypeDefOf.RejectInput, false);
            }
            else if (clicked)
            {
                Loadout localCurrent = currentLoadout;
                Find.WindowStack.Add(new Dialog_PickPacks("CER_AssignLoadouts_PickSidearmsTitleFmt".Translate(pawn.LabelShortCap),
                    () => SidearmAssignmentUtility.GetPawnSidearms(pawn),
                    updated => SidearmAssignmentUtility.SetPawnSidearms(pawn, localCurrent, updated),
                    windowTitle: "CER_AssignLoadouts_WeaponLoadoutsWindowTitle".Translate(), itemColumnLabel: "CER_AssignLoadouts_LabelSidearmsPlural".Translate(), clipboardKind: "sidearm",
                    allOptions: () => SidearmAssignmentUtility.CandidateLoadouts(localCurrent),
                    emptyMessage: "CER_AssignLoadouts_NoOtherLoadoutsMsg".Translate(),
                    displayLabel: lo => LoadoutUtility.CellLabel(lo) ?? "CER_AssignLoadouts_QuestionMarkFallback".Translate().ToString(),
                    groupLabel: SidearmAssignmentUtility.CategoryLabel,
                    scoreFn: SidearmAssignmentUtility.ScoreOf,
                    showSidearmBulkCap: true,
                    availDefFn: RepresentativeIcon, sourceLabel: pawn.LabelShortCap, checkProductionOnClose: true,
                    // Lazy-Umbau (2026-09-01): Waffen ohne Vorlage als eigene Sektion - Anhaken erzeugt
                    // die geteilte Vorlage im Hintergrund und haengt sie wie jede andere an.
                    weaponOptions: () => LoadoutUtility.WeaponsWithoutTemplate(),
                    materializeWeapon: Dialog_GenerateLoadouts.EnsureDefaultLoadout));
            }
        }

        // ---- "Other" items cell (card + compact) ------------------------------------------------------------

        // Mirrors DrawEquipmentPackCell's own structure (dash gate, Role read-only preview, icon-only cell,
        // fulfill dot, click-to-open-picker) one column over again - the fourth structured cell alongside
        // Ammo/Pack/Sidearm, added 2026-08-01 so a single pawn can carry one-off items (a tool, jewelry, a
        // resource stack, ...) that don't fit any of those three without reopening the barebones per-pawn
        // editor. No paste button here (unlike Pack/Sidearm) - LoadoutClipboard only ever stores Loadout
        // lists (packs/sidearms), and a raw ThingDef+count pair has nothing equivalent to copy/paste yet.
        private void DrawOtherItemsCell(Rect cell, Pawn pawn, Loadout currentLoadout, bool iconOnly = false, Rect? extraHit = null)
        {
            DrawOtherItemsCellInner(cell, pawn, currentLoadout, iconOnly, extraHit);
        }

        private void DrawOtherItemsCellInner(Rect cell, Pawn pawn, Loadout currentLoadout, bool iconOnly, Rect? extraHit)
        {
            Text.Font = GameFont.Tiny;
            if (LoadoutUtility.CannotCarryAnything(pawn))
            {
                GUI.color = new Color(1f, 1f, 1f, 0.35f);
                if (iconOnly)
                {
                    // Leerzustand auf DIESELBE Geometrie wie eine gefuellte Zelle (2026-08-11) - und das ist
                    // seit 2026-08-14 die volle Zelle, siehe den Kommentar an DotReserveW.
                    DrawCellDash(cell);
                    Text.Font = GameFont.Small;
                    return;
                }
                Widgets.Label(cell, "CER_AssignLoadouts_OtherDashLabel".Translate());
                GUI.color = Color.white;
                Text.Font = GameFont.Small;
                return;
            }
            // Same Role read-only treatment as the Pack/Sidearm cells (2026-08-01) - editing means editing
            // the Role itself (Roles tab), not this one colonist. No content preview here (unlike Pack/
            // Sidearm's ExtractRoleDisplayContent) - a Role has no distinct "other items" bucket separate
            // from its own flat Slots union, so a plain dash + explanatory tooltip is the honest answer.
            if (RoleUtility.IsRoleLoadout(currentLoadout))
            {
                // Volle Zelle, exakt wie jeder andere Pfad (2026-08-12 stand hier ein 24px-Streifen, damit
                // der Rollen-Zweig genauso breit zeichnete wie der normale; seit 2026-08-14 zieht ihn kein
                // Pfad mehr ab, siehe Kommentar an DotReserveW). Ein Einfuege-Knopf gehoert hier ohnehin
                // nicht hin - eine Rolle bearbeitet man im Roles-Tab, nicht pro Kolonist.
                Rect roleCell = cell;
                Rect roleBtnRect = new Rect(roleCell.x, roleCell.y, roleCell.width - DotReserveW, roleCell.height);
                // SICHTBAR deaktiviert, wie in der Pack-Zelle - dritte und letzte Fundstelle (2026-08-15): bei
                // Rollen nicht benutzbare Knoepfe sind ausgegraut, so wie Vanilla es macht. Umgesetzt war das
                // zuerst nur an EINER von DREI Stellen.
                // Es gibt drei Rollen-Zweige in dieser Datei (Pack, Zweitwaffe, Sonstiges); nur der
                // erste bekam GUI.enabled = false, die anderen zwei zeichneten weiter einen ganz
                // normalen Knopf - und der Test hatte genau die eine Zelle geprueft, die schon gefixt war.
                // Eigener Grauton statt GUI.enabled (2026-08-16, im Test paarweise gemessen:
                // gesperrte und normale Zelle lieferten beide 106,81,46, Abweichung null). GUI.enabled
                // sperrt den Klick - gezeichnet hat es nie etwas, weil RimWorlds Knopfgrafik ueber
                // GUI.DrawTexture laeuft. Siehe LoadoutUtility.TintedButton.
                LoadoutUtility.TintedButton(roleBtnRect, null, disabled: true);
                DrawCellDash(roleCell);
                Text.Font = GameFont.Small;
                RoleCellTip(roleCell, "CER_AssignLoadouts_OtherRoleNotice".Translate());
                return;
            }

            // The pawn's own personal "other" items - own slots on the anchor that are neither a weapon
            // (Sidearm's domain) nor an ammo def (Ammo's domain). Never inherited from the template/pack/
            // sidearm chain (those don't carry arbitrary items), so OwnSlots alone is the full picture here.
            Loadout anchor = AmmoAssignmentUtility.GetAmmoLeaf(pawn);
            List<LoadoutSlot> otherSlots = anchor?.OwnSlots
                ?.Where(s => s.thingDef != null && !s.thingDef.IsWeapon && !LoadoutUtility.IsAmmoDef(s.thingDef))
                .ToList() ?? new List<LoadoutSlot>();
            // Als lokale Funktion statt als Variable (2026-08-13): in der
            // kompakten Ansicht (iconOnly) braucht sie NUR der Tooltip, und der wird seit heute nur
            // noch unter der Maus gebaut. Vorher lief die Verkettung pro Zelle pro Zeile pro Bild ins
            // Leere. Direkt aufgerufen kostet eine lokale Funktion keine Zuweisung auf dem Heap.
            string Label() => otherSlots.Count > 0
                ? string.Join(", ", otherSlots.Select(s => s.thingDef.LabelCap.ToString()))
                : "CER_AssignLoadouts_OtherNoneLabel".Translate().ToString();
            Rect btnRect = new Rect(cell.x, cell.y, cell.width - DotReserveW, cell.height);
            bool clicked;
            if (iconOnly)
            {
                clicked = Widgets.ButtonText(btnRect, "") | (extraHit.HasValue && Widgets.ButtonInvisible(extraHit.Value));
                if (otherSlots.Count == 0) DrawCellDash(cell);
                else DrawCellIcon(cell, otherSlots[0].thingDef, otherSlots.Count > 1 ? otherSlots.Count : 0);
            }
            else
            {
                clicked = Widgets.ButtonText(btnRect, "CER_AssignLoadouts_OtherPrefixFmt".Translate(Label().Truncate(btnRect.width - 40f))) | (extraHit.HasValue && Widgets.ButtonInvisible(extraHit.Value));
            }
            // ComputeItemOwnership, same generic-item stock check the Pack cell's own tooltip already uses -
            // correct for an arbitrary carryable ThingDef, not just weapons/ammo.
            // ItemSegFor, dieselbe Bestandsquelle wie die Pack-Zelle - richtig fuer einen beliebigen
            // tragbaren ThingDef, nicht nur fuer Waffen/Munition. Der koloniweite Bedarf steckt dort mit
            // drin, aus demselben Grund wie in der Pack-Zelle: diese Zelle trug exakt dieselbe Mischung
            // aus koloniweitem Bestand und Ein-Pawn-Bedarf (per Suche gefunden).
            Rect otherDot = DrawFulfillDot(cell, pawn, otherSlots.Select(s => s.thingDef), "CER_AssignLoadouts_LabelOther",
                RichStock.Item, neededFor: NeededCountsFrom(otherSlots));
            Text.Font = GameFont.Small;
            if (!Mouse.IsOver(otherDot) && LoadoutUtility.TipWanted(cell))
                TooltipHandler.TipRegion(cell, (otherSlots.Count > 0
                    ? "CER_AssignLoadouts_OtherAttachedFmt".Translate(Label())
                    : "CER_AssignLoadouts_OtherNoneAttached".Translate()) + "CER_AssignLoadouts_OtherClickChooseFmt".Translate(pawn.LabelShortCap));
            if (clicked) Find.WindowStack.Add(new Dialog_PickOtherItem(pawn, currentLoadout));
        }

        // ---- Drag-and-drop loadout swap (loadout-button handle) -----------------------------------------------

        // The loadout button starts a drag; a press without movement is left to the button's normal click
        // (OpenLoadoutPickMenu). Uses raw Unity input polling (Input.GetMouseButtonDown/Up), NOT IMGUI's
        // Event.current.type - mirrors Dialog_SquadSetup's swap drag, where the EventType approach did not
        // reliably fire for this kind of press-drag-release. DrawDragChipAndReset force-clears dragPawn whenever
        // the button isn't held at all, so a missed "up" frame can never leave the drag stuck.
        private void HandleDragHandle(Rect handle, Pawn pawn)
        {
            if (dragPawn == null && Mouse.IsOver(handle) && Input.GetMouseButtonDown(0))
                dragPawn = pawn;
        }

        private void HandleDropTarget(Rect area, Pawn pawn)
        {
            // dragPawn itself is never CannotFight (HandleDragHandle only arms for a draggable, non-cannotFight
            // source) - only the hovered TARGET needs the check, so the "will accept" outline agrees with what
            // SwapLoadouts actually allows on drop instead of promising a swap that silently no-ops after.
            if (dragPawn == null || dragPawn == pawn || !Mouse.IsOver(area) || LoadoutUtility.CannotFight(pawn)) return;
            Widgets.DrawBox(area, 2);
            if (Input.GetMouseButtonUp(0))
            {
                SwapLoadouts(dragPawn, pawn);
                dragPawn = null;
            }
        }

        // Swap two colonists' actual assigned loadouts, right now - unlike Dialog_SquadSetup's preview-stage swap
        // (which pins a plan that Apply later commits), there is no deferred plan here: SetLoadout takes effect
        // immediately, same as picking one from OpenLoadoutPickMenu would.
        private void SwapLoadouts(Pawn a, Pawn b)
        {
            if (LoadoutUtility.CannotFight(a) || LoadoutUtility.CannotFight(b))
            {
                Messages.Message("CER_AssignLoadouts_SwapCannotFightMsg".Translate(), MessageTypeDefOf.RejectInput, false);
                return;
            }
            // ====== Die VORLAGEN tauschen, nicht die Anker (2026-09-04) ======
            //
            // Befund im Test: "Swapped loadouts: Mack and Ryder." - INNERHALB des Fensters hielt das,
            // nach dem Schliessen des Planers stand wieder die AUSGANGSLAGE. Gegenprobe: eine
            // Einzelzuweisung ueber die Zelle ueberlebt das Schliessen sehr wohl - es hing also am Ziehen.
            //
            // Ursache: pawn.GetLoadout() liefert bei einem personalisierten Kolonisten seinen ANKER, nicht
            // die geteilte Vorlage (siehe AmmoAssignmentUtility) - und nach einem Arm-my-Colony-Durchlauf
            // hat praktisch jeder einen. Getauscht wurden also die Anker: A wurde B's Anker zugewiesen und
            // umgekehrt. Der Anker-Schutz in Patch_Utility_Loadouts_SetLoadout tritt dabei ausdruecklich
            // BEISEITE (IsAmmoLink -> "das ist eine echte Direktzuweisung"), also blieb es bei der rohen
            // Zuweisung. Zwei Kolonisten hingen danach an fremden Ankern - ein Zustand, den das naechste
            // Aufraeumen wieder geradezieht. Genau deshalb sah der Tausch im offenen Fenster richtig aus
            // und war nach dem Schliessen weg.
            //
            // Richtig ist, was der Einzelweg tut: die VORLAGE lesen (DisplayLoadout schaut durch den Anker)
            // und ueber AssignTemplate setzen - das haengt den EIGENEN Anker jedes Kolonisten an die neue
            // Vorlage um und nimmt Packs, Zweitwaffen und persoenliche Munition mit.
            Loadout la = AmmoAssignmentUtility.DisplayLoadout(a.GetLoadout());
            Loadout lb = AmmoAssignmentUtility.DisplayLoadout(b.GetLoadout());
            bool aHas = la != null && !la.defaultLoadout, bHas = lb != null && !lb.defaultLoadout;
            if (!aHas && !bHas)
            {
                Messages.Message("CER_AssignLoadouts_SwapNeitherHasLoadout".Translate(), MessageTypeDefOf.RejectInput, false);
                return;
            }
            if (aHas && bHas && la == lb)
            {
                Messages.Message("CER_AssignLoadouts_SwapSameLoadout".Translate(), MessageTypeDefOf.RejectInput, false);
                return;
            }
            // Extra parents (Packs) live keyed by the anchor Loadout's OWN UniqueID, not the pawn, so
            // swapping which pawn is assigned to which anchor object carries them along automatically.
            // NIE null (2026-08-13, im CE-Dekompilat bestaetigt): CEs
            // GetLoadout heilt nur einen FEHLENDEN Schluessel, ein Schluessel mit Wert null bleibt fuer
            // immer null - und CEs eigene Loadout-Spalte macht ungeschuetzt pawn.GetLoadout().label.
            // Fehlszenario: A hat eine Vorlage, B nicht; A per Ziehen auf B - danach wirft CEs native
            // Loadouts-Tabelle in A's Zeile pro Bild eine NullReferenceException.
            // Ueberall sonst im Mod steht dafuer das Default-Loadout ("keine Zuweisung"), siehe
            // ArmUnassignAll; das war die einzige Stelle mit null.
            Loadout noneLo = LoadoutManager.Loadouts.FirstOrDefault(l => l.defaultLoadout);
            // Ueber AssignTemplate statt SetLoadout, wo es wirklich eine Vorlage gibt - siehe oben. Der
            // Weg auf "keine Vorlage" bleibt SetLoadout(none): AssignTemplate lehnt null/Default ab, und
            // fuer das Abhaengen ist die rohe Zuweisung die richtige (dieselbe Zusage wie ArmUnassignAll).
            if (bHas) AmmoAssignmentUtility.AssignTemplate(a, lb); else a.SetLoadout(noneLo);
            if (aHas) AmmoAssignmentUtility.AssignTemplate(b, la); else b.SetLoadout(noneLo);
            LoadoutUtility.InvalidateAmmoOwnership();
            CombatReadiness.Invalidate();
            Messages.Message("CER_AssignLoadouts_SwappedMsgFmt".Translate(a.LabelShortCap, b.LabelShortCap), MessageTypeDefOf.TaskCompletion, false);
        }

        // Floating chip that follows the cursor while dragging + the end-of-frame reset when released over empty
        // space. Called inside the assign scroll view so its coordinates match the cards'/rows'.
        private void DrawDragChipAndReset()
        {
            if (dragPawn != null)
            {
                Loadout lo = AmmoAssignmentUtility.DisplayLoadout(dragPawn.GetLoadout());
                Vector2 mp = Event.current.mousePosition;
                Rect chip = new Rect(mp.x + 12f, mp.y + 6f, 220f, 26f);
                Widgets.DrawBoxSolid(chip, new Color(0.15f, 0.15f, 0.17f, 0.95f));
                string lab = dragPawn.LabelShortCap + "  ->  " + (lo != null && !lo.defaultLoadout ? LoadoutUtility.LoadoutDisplayName(lo) : "CER_AssignLoadouts_NoneLoadout".Translate().ToString());
                GUI.color = new Color(0.85f, 0.9f, 1f);
                Widgets.Label(new Rect(chip.x + 6f, chip.y + 3f, chip.width - 12f, 22f), lab.Truncate(208f));
                GUI.color = Color.white;
            }
            if (dragPawn != null && !Input.GetMouseButton(0)) dragPawn = null;
        }

        // Combat scores are driven by Sight, Manipulation and Moving. This tooltip shows each capacity's level and,
        // when it is below full, exactly what is dragging it down (injuries, missing parts, pain, genes) so the
        // player can tell at a glance whether a colonist is fit for ranged or melee duty.
        private static string BuildCapacityTooltip(Pawn pawn)
        {
            System.Text.StringBuilder sb = new System.Text.StringBuilder();
            sb.Append("CER_AssignLoadouts_CombatCapabilitiesHeader".Translate());
            sb.Append("CER_AssignLoadouts_CapacitiesSubheader".Translate());
            sb.Append("\n" + CapacityLine(pawn, PawnCapacityDefOf.Sight, "CER_AssignLoadouts_RoleAimingShooting".Translate()));
            sb.Append("\n" + CapacityLine(pawn, PawnCapacityDefOf.Manipulation, "CER_AssignLoadouts_RoleAimMeleeHit".Translate()));
            sb.Append("\n" + CapacityLine(pawn, PawnCapacityDefOf.Moving, "CER_AssignLoadouts_RoleClosingMelee".Translate()));
            return sb.ToString();
        }

        private static string CapacityLine(Pawn pawn, PawnCapacityDef capacity, string role)
        {
            List<PawnCapacityUtility.CapacityImpactor> impactors = new List<PawnCapacityUtility.CapacityImpactor>();
            float level;
            try { level = PawnCapacityUtility.CalculateCapacityLevel(pawn.health.hediffSet, capacity, impactors); }
            catch { level = pawn.health.capacities.GetLevel(capacity); impactors = null; }

            int pct = Mathf.RoundToInt(level * 100f);
            string name = capacity.GetLabelFor(pawn).CapitalizeFirst();
            string pctStr = pct >= 100 ? pct + "%"
                          : (pct >= 50 ? "<color=#FFC44D>" : "<color=#FF6B6B>") + pct + "%</color>";

            System.Text.StringBuilder sb = new System.Text.StringBuilder();
            sb.Append("<b>" + name + ":</b> " + pctStr + "  <i>(" + role + ")</i>");

            if (level < 0.999f && impactors != null)
            {
                HashSet<string> seen = new HashSet<string>();
                foreach (var imp in impactors)
                {
                    string r = null;
                    try { r = imp.Readable(pawn); } catch { }
                    if (string.IsNullOrEmpty(r) || !seen.Add(r)) continue;
                    sb.Append("\n   • " + r);
                }
            }
            return sb.ToString();
        }

        private string GetEquipmentTooltip(Pawn pawn)
        {
            System.Text.StringBuilder sb = new System.Text.StringBuilder();
            sb.AppendLine("CER_AssignLoadouts_CurrentlyEquippedHeader".Translate());

            bool hasEquip = false;
            if (pawn.equipment != null && pawn.equipment.Primary != null)
            {
                sb.AppendLine("• " + pawn.equipment.Primary.LabelCap);
                hasEquip = true;
            }

            // Clothing/apparel is intentionally omitted here - only weapons and inventory are relevant to loadouts.

            if (pawn.inventory != null && pawn.inventory.innerContainer.Count > 0)
            {
                sb.AppendLine("CER_AssignLoadouts_InventoryHeader".Translate());
                foreach(var inv in pawn.inventory.innerContainer)
                {
                    sb.AppendLine("• " + inv.LabelCap);
                    hasEquip = true;
                }
            }

            if (!hasEquip) return "CER_AssignLoadouts_NothingEquippedMsg".Translate();
            return sb.ToString().TrimEnd();
        }

        private void DrawScoreBar(Rect rect, float score, float maxScore, string label)
        {
            LoadoutUtility.ScoreBar(rect, score, maxScore, label);
        }

        // Mit vorgebauter Beschriftung (GetRowLabels) - erspart je Durchgang eine Uebersetzung mit Argumenten.
        private void DrawScoreBarPrebuilt(Rect rect, float score, float maxScore, string prebuilt)
        {
            LoadoutUtility.ScoreBar(rect, score, maxScore, null, prebuiltLabel: prebuilt);
        }
    }
}
