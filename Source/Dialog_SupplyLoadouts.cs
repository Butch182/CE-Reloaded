using System;
using System.Collections.Generic;
using System.Linq;
using RimWorld;
using Verse;
using UnityEngine;
using CombatExtended;

namespace CEReloaded
{
    // The unified "make these loadouts real" popup (WORKFLOW_OVERHAUL_PLAN.md section 2). Replaces
    // Dialog_ProduceMissingWeapons and the old reactive Generate-time "resolve generic ammo" popup with
    // ONE dialog covering four independent sections - Main weapons / Sidearms & grenades / Shields / Ammunition -
    // each hidden entirely when empty, so it works standalone for any subset (e.g. ammo-only when called from the
    // Assign flow's new ammo-gap check). Generic ammo is auto-resolved to the player's default firearm ammo
    // preference instead of forcing a review step (plan 5.8); a small "Change..." link reopens the choice per row.
    public class Dialog_SupplyLoadouts : Window
    {
        // What to supply. Any group may be empty/null.
        public class Request
        {
            public Dictionary<ThingDef, int> mainWeapons = new Dictionary<ThingDef, int>();
            public Dictionary<ThingDef, int> secondaryWeapons;
            public Dictionary<ThingDef, int> shields;
            // Equipment Pack contents (medicine/food/drugs/utility) not yet covered - see
            // LoadoutUtility.ScanMissingAssignedPackItems. Already resolved to concrete ThingDefs (a bill
            // always targets one recipe, so any generic slot's family is picked down to one member upstream).
            public Dictionary<ThingDef, int> packItems;
            // Zu jedem dieser konkreten Defs der Def, an dem der BEDARF haengt (2026-08-19,
            // supply-medicine-row-numbers). Bei "Any Medicine" ist das der generische Def, waehrend der
            // Schluessel oben das Herstellungsziel ist - der Verfuegbarkeits-Balken muss den Bedarfs-Def
            // fragen, sonst beantwortet er eine andere Frage als der Equipment-Packs-Reiter und liest sich
            // bei 600 gefordert / 75 vorhanden als voll gedeckt. Fehlt der Eintrag, gilt das konkrete Def.
            public Dictionary<ThingDef, Def> packItemDemandDefs;

            // ============ IST `count` schon um laufende Auftraege bereinigt? (2026-08-18) ============
            //
            // Im Test gemessen: ein Doppelabzug. 7 Traeger, 3 in Arbeit, 0 vorhanden - der
            // Verfuegbarkeits-Tooltip derselben Zeile sagt korrekt "Still missing: 4", das Mengenfeld
            // schlaegt 1 vor. 7 minus 3 minus 3.
            //
            // Die Ursache ist keine Fehlrechnung, sondern eine unausgesprochene Annahme an der Naht: die
            // Zahl in mainWeapons/secondaryWeapons bedeutet je nach Erbauer etwas anderes.
            //   ScanMissingAssignedWeapons zieht laufende Auftraege bereits ab (2026-07-31, damit die
            //     "habe ich etwas zu produzieren vergessen"-Pruefung nicht meldet, was laengst eingereiht ist).
            //   ComputeMissingSplit und ComputeMissingForCreatedLoadouts tun das NICHT.
            // BuildWeaponEntry zog danach noch einmal ab - fuer die zweite Sorte richtig, fuer die erste
            // doppelt. Die Pack-Zeile daneben laesst den zweiten Abzug ausdruecklich weg und begruendet es
            // genau so; die Waffen-Zeile hat dieselbe Ueberlegung nie bekommen.
            //
            // Statt eine der beiden Rechnungen umzustellen (beide sind fuer ihren Zweck richtig) sagt der
            // Erbauer jetzt, was seine Zahl bedeutet. Vorgabe false = "roher Bedarf", also das bisherige
            // Verhalten fuer alle, die nichts setzen.
            public bool weaponCountsNetOfProduction;
            // Ammo: either pre-resolved (Generate flow's dry-run) or computed from assigned pawns' actual loadouts
            // (Assign flow, via LoadoutUtility.ComputeAmmoNeedForAssignedPawns before building this Request).
            public Dictionary<LoadoutGenericDef, int> genericAmmo;
            public Dictionary<ThingDef, int> specificAmmo;
            public Dictionary<ThingDef, Building_WorkTable> ammoBenches;
            // Assig. column, split into two ALWAYS-independent indicators (2026-08-02) - it used to be one
            // dict-set whose MEANING silently flipped between "real pawn" and "loadout template" depending on
            // which scope radio was active (two screenshots of the same colonist showing
            // wildly different "assigned" counts). Now every caller populates both regardless of which scope
            // drives the visible row/count numbers: assignedUsedBy/weaponAssignedUsedBy = real colonists
            // (diamond icon), templateUsedBy/weaponTemplateUsedBy = managed Loadout templates (circle icon).
            public Dictionary<Def, List<string>> assignedUsedBy;   // ammo/generic def -> pawn names
            public Dictionary<Def, List<string>> templateUsedBy;   // ammo/generic def -> loadout labels
            public Dictionary<ThingDef, List<string>> weaponAssignedUsedBy;   // weapon def -> pawn names
            public Dictionary<ThingDef, List<string>> weaponTemplateUsedBy;   // weapon def -> loadout labels
            public Action onConfirm;
            public Action onCancel;
            public Action onBack;   // set when there is a previous step to return to (e.g. the assign preview)
            public ArmReport armReport;   // Arm-my-Colony run: record what actually gets queued on confirm

            // Der Bereichs-Streifen "Consider assigned weapons / created loadouts" (2026-08-01) ist am
            // 2026-09-07 entfallen: seit dem Lazy-Umbau gibt es je benutzter Waffe eine Vorlage, beide
            // Umfaenge waren
            // dieselbe Menge. Das Fenster rechnet nur noch mit den zugewiesenen Waffen.
            // Ammo/weapon bills nobody currently demands - opt-OUT, not opt-in
            // (2026-08-01, by design): every candidate shows checked by default; the player unchecks
            // individual ones they want to keep. Whatever's still checked when Confirm is clicked gets pruned
            // via the matching onPrune* callback, invoked with exactly that final list.
            public List<ThingDef> obsoleteAmmoCandidates;
            public Action<List<ThingDef>> onPruneObsoleteAmmo;
            public List<ThingDef> obsoleteWeaponCandidates;
            public Action<List<ThingDef>> onPruneObsoleteWeapons;
        }

        private class WeaponEntry
        {
            public ThingDef weapon;
            public RecipeDef recipe;
            public bool craftable;
            public string countStr;
            public bool selected;
            public List<Building_WorkTable> benches = new List<Building_WorkTable>();
            public Dictionary<Building_WorkTable, string> benchLabels;
            public Building_WorkTable bench;
            public List<ThingDef> stuffs = new List<ThingDef>();
            public ThingDef stuff;
            public int unfulfilled;
            public List<ThingDefCountClass> ings = new List<ThingDefCountClass>();
            // Split 2026-08-02 - see Request.assignedUsedBy/templateUsedBy's own comment. assignedNames = real
            // colonists currently assigned this weapon (diamond icon); templateNames = managed Loadout
            // templates that include it (circle icon). Always both populated, independent of the scope radio.
            public List<string> assignedNames = new List<string>();
            public List<string> templateNames = new List<string>();
            public int yield = 1;          // units produced per craft (almost always 1 for weapons, kept for symmetry with AmmoEntry)
            // Which SECTION this row came from (set by BuildPackItemEntry), not a property of `weapon` itself -
            // a pack-sourced utility item that also happens to be IsWeapon (binoculars, a Spotter/Tactical/EMP
            // Kit's smoke/flashbang/EMP grenades) still demands via AmmoAssignmentUtility.GetPawnPacks, never
            // GetWeaponInventory. Branching DrawWeaponEntry's availability bar on e.weapon.IsWeapon instead of
            // this used to understate pack-sourced utility weapons' real demand (2026-07-31 fix).
            public bool isPackItem;
            // Nur fuer Pack-Zeilen gesetzt und nur fuer die BESTANDSFRAGE gedacht - siehe
            // Request.packItemDemandDefs. Null heisst "identisch mit weapon".
            public Def demandDef;
        }

        private class AmmoEntry
        {
            public ThingDef resolved;          // the concrete ammo actually produced (generics are resolved eagerly)
            public LoadoutGenericDef sourceGeneric;   // non-null if this entry originated from a generic placeholder
            public List<ThingDef> alternatives = new List<ThingDef>();   // other researched variants of the same family
            public bool selected;
            public string countStr;
            public List<string> assignedNames = new List<string>();
            public List<string> templateNames = new List<string>();
            public RecipeDef recipe;
            public List<ThingDefCountClass> ings = new List<ThingDefCountClass>();
            public List<Building_WorkTable> benches = new List<Building_WorkTable>();   // all candidate benches, built once
            public Building_WorkTable bench;
            public bool alreadyInProduction;
            public int yield = 1;          // units produced per craft (ammo recipes commonly yield >1 per batch)
        }

        private readonly List<WeaponEntry> mainEntries = new List<WeaponEntry>();
        private readonly List<WeaponEntry> secEntries = new List<WeaponEntry>();
        private readonly List<WeaponEntry> shieldEntries = new List<WeaponEntry>();
        private readonly List<WeaponEntry> packEntries = new List<WeaponEntry>();
        private readonly List<AmmoEntry> ammoEntries = new List<AmmoEntry>();
        private readonly Action onConfirm;
        private readonly Action onCancel;
        private readonly Action onBack;
        private readonly Dictionary<ThingDef, Building_WorkTable> presetAmmoBenches;
        private readonly ArmReport armReport;
        private bool confirmed;
        private bool wentBack;
        private Vector2 scroll;
        private bool diffOnly = true;   // "Show only what's missing" (plan 5.9) - on by default, this IS a reactive gap-filling popup

        // Obsolete-cleanup (2026-08-01) - see Request's own field comments.
        private readonly List<ThingDef> obsoleteAmmoCandidates;
        private readonly HashSet<ThingDef> obsoleteAmmoChecked;
        private readonly Action<List<ThingDef>> onPruneObsoleteAmmo;
        private readonly List<ThingDef> obsoleteWeaponCandidates;
        private readonly HashSet<ThingDef> obsoleteWeaponChecked;
        private readonly Action<List<ThingDef>> onPruneObsoleteWeapons;

        private enum SortMode { Name, Count, Assigned }
        private SortMode currentSortMode = SortMode.Name;
        private bool sortDescending = false;

        public override Vector2 InitialSize => new Vector2(Mathf.Min(1192f, UI.screenWidth - 40f), Mathf.Min(760f, UI.screenHeight - 40f));
        // Nicht unter RimWorlds Lernhilfe schieben (2026-08-19) - siehe
        // LoadoutUtility.KeepClearOfLearningReadout. Ohne Kollision aendert das nichts.
        protected override void SetInitialSizeAndPosition()
        {
            base.SetInitialSizeAndPosition();
            windowRect = LoadoutUtility.KeepClearOfLearningReadout(windowRect);
        }

        // UsedX's own column widened from 32 to 60 (2026-08-02, everything from IngX on shifted +32) to fit
        // TWO indicators side by side (diamond=assigned, circle=used in loadout) instead of one - see
        // DrawAssignmentIndicators.
        // KORRIGIERT 2026-08-18: der Satz, der hier stand ("~90px Luft, kein Ueberlaufrisiko bei der
        // Maximalbreite 1160"), stimmt nicht mehr: bei schmalen Bildschirmen reicht der Platz nicht.
        // Die Maximalbreite ist laengst 1192 (InitialSize eine Zeile darueber), und die Zeile endet heute
        // bei 1124 - Luft ist also nur noch, solange das Fenster wirklich seine Maximalbreite bekommt.
        // Die harte Rechnung: rechte Kante 1124 -> noetige Fensterbreite 1176 -> noetige Bildschirmbreite
        // 1216 (InitialSize nimmt Bildschirmbreite minus 40). Darunter ragen Werkbank-Knopf, Sprung- und
        // Info-Symbol aus dem Fenster, und einen waagrechten Schieber gibt es hier nicht: sie sind dann
        // nicht abgeschnitten, sondern unerreichbar. Ein Kommentar, der eine ueberholte Zahl behauptet, ist
        // gefaehrlicher als gar keiner - die naechste Aenderung wuerde daraus weiterrechnen.
        private const float IconX = 26f, NameX = 52f;
        // ---- Spaltenlage bei schmalem Fenster (2026-08-25: Weg (a) mit Rueckfall (b)) ----
        // Die Zeile zeichnete an festen X. Unter 1216 Bildbreite (oder mit UI-Skalierung) ragten Werkbank,
        // Sprung- und Info-Symbol aus der Sicht - und weil es keinen waagrechten Bildlauf gab, waren sie
        // UNERREICHBAR, nicht nur abgeschnitten. Jetzt rutschen alle Spalten ab "Special" um colShift nach
        // links, und die Namensspalte gibt die Breite ab, bis NameMinW erreicht ist (Weg a). Reicht auch das
        // nicht, bleibt die Zeile breiter als die Sicht, und viewRect wird auf die Zeilenbreite gesetzt -
        // RimWorlds ScrollView zeigt dann von selbst einen waagrechten Balken (Weg b als Rueckfall): unbequem,
        // aber nichts ist mehr unerreichbar. Die Kopfzeile benutzt dieselben Werte, steht aber ausserhalb der
        // ScrollView - beim Rueckfall wandert sie nicht mit; das ist der Preis des Rueckfalls, nicht des Wegs (a).
        // Basiswerte = die bisherigen festen X; ein separater Layout-Renderer (SupplyLoadouts_RowsAt)
        // rechnet mit denselben Zahlen.
        private const float SpecialXBase = 240f, UsedXBase = 320f, IngXBase = 390f, MaterialXBase = 546f;
        private const float BatchXBase = 676f;   // batch-count field, restored 2026-07-30 - paired with CountX, synced both ways
        private const float CountXBase = 768f;   // absolute target count (shifted right from its old 644f slot to make room for BatchX)
        private const float NameMinW = 90f;
        private static float colShift;   // <= 0; je Zeichendurchgang aus der Sichtbreite gesetzt (UpdateColumnShift)
        private static float SpecialX => SpecialXBase + colShift;
        private static float UsedX => UsedXBase + colShift;
        private static float IngX => IngXBase + colShift;
        private static float MaterialX => MaterialXBase + colShift;
        private static float BatchX => BatchXBase + colShift;
        private static float CountX => CountXBase + colShift;
        private static float ProdX => ProdXBase + colShift;
        private static float BenchX => BenchXBase + colShift;
        private static float JumpX => BenchX + BenchBtnW + 8f;
        private static float InfoX => JumpX + 22f;
        // Rechte Kante der Zeile ohne Schiebung (Info-Ende) und nach Schiebung.
        private const float RowRightEdge = BenchXBase + BenchBtnW + 8f + 22f + 18f;   // = 1124
        private static float RowWidth => RowRightEdge + colShift;
        private static void UpdateColumnShift(float viewWidth)
            => colShift = Mathf.Clamp(viewWidth - RowRightEdge, -(SpecialXBase - NameX - 4f - NameMinW), 0f);
        // BenchBtnW 54 -> 74 und die Folgespalten daraus abgeleitet statt zweimal 54 von Hand (2026-08-13,
        // Werkbank-Spalte). Die 20px kommen NICHT aus einer Nachbarspalte, sondern aus der
        // Fensterbreite: rechts war kein Millimeter frei (Info endete exakt auf der Kante der Liste), deshalb
        // ist InitialSize um 32 gewachsen. Gerechnet auf 1920: Liste 1140 breit, rechte Kante 1124 - das
        // gilt NUR ab 1216 Bildschirmbreite, siehe die Rechnung bei IconX oben.
        private const float ProdXBase = 832f, BenchXBase = 1002f;   // ProdX/BenchX/JumpX/InfoX: siehe die Schiebung bei IconX
        // Die Werkbank-Spalte zeigt entweder einen Knopf (BenchIconButton, 54x24 ab y+2) oder - wenn es gar
        // keine passende Bank gibt - ein Warnzeichen. Das sass linksbuendig am Spaltenanfang und damit
        // sichtbar neben der Achse der Knoepfe in den Zeilen darueber und darunter (2026-08-12: zentriert
        // zu den Workbench-Knoepfen). Einmal hier abgeleitet statt
        // an drei Aufrufstellen wiederholt, damit es nicht wieder auseinanderlaeuft.
        private const float BenchBtnW = 74f, BenchBtnH = 24f, BenchWarnSize = 18f;
        private static Rect BenchWarnRect(float y) => new Rect(
            BenchX + (BenchBtnW - BenchWarnSize) / 2f, y + 2f + (BenchBtnH - BenchWarnSize) / 2f,
            BenchWarnSize, BenchWarnSize);

        // Der Sperrgrund gehoert IN die Zeile, nicht nur in den Tooltip (2026-08-13):
        // "Rezept da, Forschung fertig, aber keine passende Werkbank" zeichnete
        // bis dahin nur ein Warnzeichen ganz rechts, und wer nicht mit der Maus draufging, sah eine
        // ausgegraute Zeile ohne jede Begruendung - das liest sich als Fehler des Mods.
        // Die Spalte ist dieselbe, in der der !craftable-Zweig seinen Grund schon nennt, damit alle
        // Sperrgruende auf einer Achse stehen. Die BREITE kommt aus den beiden Spaltenanfaengen, nicht aus
        // einer geratenen Zahl, und LabelCell kuerzt hart darauf - genau diese Zelle war am 2026-08-02
        // geleert worden, weil ein ungekuerztes Widgets.Label sie ueberlaufen liess. Der volle Satz steht
        // weiterhin im Tooltip, hier wie dort.
        private const float ReasonCellW = ProdXBase - MaterialXBase - 8f;   // beide Spalten rutschen gleich, die Breite bleibt
        private static void DrawBlockedReasonCell(float y, string text, string tip)
        {
            Rect r = new Rect(MaterialX, y, ReasonCellW, 24f);
            GUI.color = LoadoutUtility.ColBad;
            LoadoutUtility.LabelCell(r, text);
            GUI.color = Color.white;
            if (!string.IsNullOrEmpty(tip)) TooltipHandler.TipRegion(r, tip);
        }
        private const float RowH = 30f, HeaderH = 26f;

        public Dialog_SupplyLoadouts(Request req) : base(new ReadoutAwareWindowDrawing())
        {
            doCloseX = true;
            forcePause = true;
            absorbInputAroundWindow = true;

            DropObsoleteFromProduceLists(req);
            // EINE Aenderung, EIN Angebot (2026-08-16): nach einer Aenderung das Nachschub-Fenster
            // anzubieten ist gut, beim Reiterwechsel noch einmal ist zu viel.
            // Hier statt an den Aufrufern, weil JEDER Weg zum Nachschub-Fenster durch diesen
            // Konstruktor laeuft - eine Marke pro Aufrufer waere beim naechsten Einstieg wieder
            // unvollstaendig. Was danach mit dem Fenster passiert (produzieren oder Cancel), aendert daran
            // nichts: angeboten wurde es, und ein zweites Mal fuer dieselbe Aenderung zu fragen ist genau
            // das Nerven, das vermieden werden soll. Der dauerhafte Zustand steht ohnehin in der Bereitschaftsanzeige.
            LoadoutUtility.lastSupplyOfferSignature = LoadoutUtility.AssignmentSignature();

            WeaponEntry WithNames(WeaponEntry we)
            {
                if (req.weaponAssignedUsedBy != null && req.weaponAssignedUsedBy.TryGetValue(we.weapon, out List<string> an)) we.assignedNames = an;
                if (req.weaponTemplateUsedBy != null && req.weaponTemplateUsedBy.TryGetValue(we.weapon, out List<string> tn)) we.templateNames = tn;
                return we;
            }
            foreach (var kvp in Order(req.mainWeapons)) mainEntries.Add(WithNames(BuildWeaponEntry(kvp.Key, kvp.Value, req.weaponCountsNetOfProduction)));
            if (req.secondaryWeapons != null)
                foreach (var kvp in Order(req.secondaryWeapons)) secEntries.Add(WithNames(BuildWeaponEntry(kvp.Key, kvp.Value, req.weaponCountsNetOfProduction)));
            if (req.shields != null)
                foreach (var kvp in Order(req.shields)) shieldEntries.Add(WithNames(BuildWeaponEntry(kvp.Key, kvp.Value, req.weaponCountsNetOfProduction)));
            // Ein Def, der schon in einer Waffen-/Schild-Sektion steht, darf NICHT zusaetzlich als
            // Pack-Gegenstand erscheinen: ein Schild in einem Equipment Pack erzeugt seinen Bedarf in
            // BEIDEN Request-Buckets (shieldMissing via ComputeMissingSplit UND missingPackItems via
            // ScanMissingAssignedPackItems - dieselben Pack-Slots, zweimal gelesen). GenerateAll und
            // ComputeTotalCost concatenieren alle vier Listen, aus einem Bedarf wuerden also zwei
            // Auftraege (2026-08-26, Pavise im Shield Kit: 3 fehlend -> 6 produziert).
            var weaponSectionDefs = new HashSet<ThingDef>();
            foreach (WeaponEntry we in mainEntries) weaponSectionDefs.Add(we.weapon);
            foreach (WeaponEntry we in secEntries) weaponSectionDefs.Add(we.weapon);
            foreach (WeaponEntry we in shieldEntries) weaponSectionDefs.Add(we.weapon);
            if (req.packItems != null)
                foreach (var kvp in Order(req.packItems))
                {
                    if (weaponSectionDefs.Contains(kvp.Key)) continue;
                    Def demandDef = null;
                    if (req.packItemDemandDefs != null) req.packItemDemandDefs.TryGetValue(kvp.Key, out demandDef);
                    packEntries.Add(WithNames(BuildPackItemEntry(kvp.Key, kvp.Value, demandDef)));
                }
            BuildAmmoEntries(req);

            onConfirm = req.onConfirm;
            onCancel = req.onCancel;
            onBack = req.onBack;
            presetAmmoBenches = req.ammoBenches;
            armReport = req.armReport;

            obsoleteAmmoCandidates = req.obsoleteAmmoCandidates ?? new List<ThingDef>();
            // Opt-IN, not opt-out (2026-08-02 fix): this popup can be reached as a
            // side effect of an action taken somewhere that has nothing to do with deleting bills on purpose
            // (e.g. Weapon Loadouts' own Apply, which only ever deletes/generates loadout TEMPLATES - never
            // bills, that responsibility was deliberately kept in Supply Loadouts alone) - starting every
            // auto-detected "obsolete" candidate already checked meant a player who wasn't specifically here
            // to prune bills could confirm and delete production they never reviewed. Empty by default now;
            // the player must deliberately tick each one they actually want gone.
            // 2026-08-09 wieder VORBELEGT: angehakt, also loeschen, ist der Default. Der Kommentar darueber
            // beschreibt die Gegenposition von Anfang August - bewusst geaendert, kein
            // Versehen. Die Erkennung selbst bleibt der wunde Punkt: ein falsch erkannter Auftrag ist
            // mit Vorbelegung nur noch einen Confirm-Klick vom Loeschen entfernt - wenn die
            // Obsolet-Erkennung etwas falsch einordnet, schlaegt es hier durch.
            // 2026-08-14: vorbelegt wird nur noch, was UNSER Assistent selbst angelegt hat (die sind
            // opt-out). Ein von Hand
            // gesetzter Auftrag wird weiterhin VORGESCHLAGEN, aber ohne Haken - er verlangt eine bewusste
            // Entscheidung ("MANUELL angelegte auftraege sind default OFF, also opt in").
            // Das entschaerft genau den Fall, der die Entscheidung ausgeloest hat: ein Auftrag, den der
            // Spieler eine Minute vorher selbst angelegt hat, stand angehakt zum Loeschen da, weil kein
            // Loadout ihn verlangt. Der Vorschlag stimmt weiterhin - nur der vorbelegte Haken war der
            // Unterschied zwischen "Hinweis" und "ein Klick vom Datenverlust entfernt".
            obsoleteAmmoChecked = new HashSet<ThingDef>(obsoleteAmmoCandidates.Where(LoadoutUtility.AllBillsForDefAreOurs));
            onPruneObsoleteAmmo = req.onPruneObsoleteAmmo;
            obsoleteWeaponCandidates = req.obsoleteWeaponCandidates ?? new List<ThingDef>();
            obsoleteWeaponChecked = new HashSet<ThingDef>(obsoleteWeaponCandidates.Where(LoadoutUtility.AllBillsForDefAreOurs));
            onPruneObsoleteWeapons = req.onPruneObsoleteWeapons;
        }

        public override void PostClose()
        {
            base.PostClose();
            // Back is navigation to the previous step, not a cancel - nothing here has been applied yet (onConfirm
            // only fires from the button below), so there is nothing to undo.
            if (wentBack) return;
            if (!confirmed) onCancel?.Invoke();
        }

        private static IEnumerable<KeyValuePair<ThingDef, int>> Order(Dictionary<ThingDef, int> d)
        {
            if (d == null) yield break;
            foreach (var kv in d.OrderByDescending(k => k.Value).ThenBy(k => k.Key.label)) yield return kv;
        }

        // ================================================================================================
        // Weapons / Shields (identical structure - a shield is just a ThingDef with a recipe like any weapon)
        // ================================================================================================

        private static int ComputeUnfulfilled(ThingDef w)
        {
            int n = 0;
            foreach (Pawn p in LoadoutUtility.AllColonyPawns())
            {
                Loadout lo = p.GetLoadout();
                if (lo == null || lo.defaultLoadout || lo.Slots == null || !lo.Slots.Any(s => s.thingDef == w)) continue;
                if (!LoadoutUtility.PawnAlreadyHasItem(p, w)) n++;
            }
            return n;
        }

        // netOfProduction: siehe Request.weaponCountsNetOfProduction - true heisst "die Zahl ist schon um
        // laufende Auftraege bereinigt, zieh sie kein zweites Mal ab".
        private static WeaponEntry BuildWeaponEntry(ThingDef weapon, int count, bool netOfProduction = false)
        {
            WeaponEntry e = new WeaponEntry { weapon = weapon, countStr = count.ToString() };
            e.recipe = LoadoutUtility.GetRecipeFor(weapon);
            e.craftable = e.recipe != null && LoadoutUtility.RecipeIsResearched(e.recipe);
            e.unfulfilled = ComputeUnfulfilled(weapon);
            e.ings = LoadoutUtility.GetRecipeIngredients(e.recipe);

            if (e.craftable)
            {
                // Die Bankliste kommt aus dem REZEPT, das wirklich in Auftrag geht - nicht aus dem
                // Produkt (2026-09-04, Absturz: "satchel charge an der Munitionsbank").
                // GetWorkbenchesFor(weapon) vereinigt die Baenke ALLER Rezepte, die diesen Gegenstand
                // herstellen; angelegt wird der Auftrag dann mit e.recipe. Bei einem Gegenstand mit
                // mehreren Rezepten konnte so eine Bank herauskommen, die genau dieses Rezept nicht
                // fuehrt - und RimWorlds Auftragsstapel nimmt das kommentarlos an.
                e.benches = LoadoutUtility.BenchesForRecipe(e.recipe);
                if (CEReloadedMod.settings != null && CEReloadedMod.settings.hideDuplicateBenches)
                {
                    HashSet<ThingDef> seenDefs = new HashSet<ThingDef>();
                    e.benches = e.benches.Where(b => seenDefs.Add(b.def)).ToList();
                }
                e.benchLabels = LoadoutUtility.BuildBenchLabels(e.benches);
                e.bench = e.benches.FirstOrDefault();

                if (weapon.MadeFromStuff)
                {
                    e.stuffs = GenStuff.AllowedStuffsFor(weapon).ToList();
                    e.stuff = e.stuffs.FirstOrDefault(s => s == ThingDefOf.Steel) ?? e.stuffs.FirstOrDefault();
                    e.ings = LoadoutUtility.GetRecipeIngredients(e.recipe, e.stuff);
                }
            }

            e.selected = e.craftable && e.bench != null;

            if (e.craftable && !netOfProduction
                && (CEReloadedMod.settings == null || CEReloadedMod.settings.claimOpenWeaponProduction))
            {
                int queued = QueuedCount(e.recipe);
                if (queued > 0)
                {
                    int remaining = Mathf.Max(0, count - queued);
                    e.countStr = remaining.ToString();
                    if (remaining == 0) e.selected = false;
                }
            }

            if (e.craftable && e.recipe != null && e.recipe.products != null && e.recipe.products.Count > 0)
                e.yield = Mathf.Max(1, e.recipe.products[0].count);
            return e;
        }

        // Equipment Pack item (medicine/food/drug/utility) row - same shape as BuildWeaponEntry (reused
        // rather than a parallel class, since recipe/bench/stuff/ingredients logic doesn't care whether the
        // ThingDef is a weapon), but WITHOUT the two weapon-loadout-specific pieces: ComputeUnfulfilled reads
        // real Weapon Loadout slots (meaningless for a pack item's demand, which lives in
        // AmmoAssignmentUtility.GetPawnPacks instead - already resolved into `count` by the caller), and the
        // "claimOpenWeaponProduction" already-queued reduction (the caller's shortfall already accounts for
        // in-production stock via LoadoutUtility.ComputeItemOwnership, so subtracting queued count again here
        // would double-count it).

        // ====== EINE Rechnung fuer den Verfuegbarkeits-Balken einer Zeile (2026-08-19) ======
        // Vorher stand sie nur im Normalfall; die zwei Frueh-Ausstiege der Zeile (kein Rezept, keine
        // Werkbank) bauten ihren eigenen Balken - und zwar aus GetWeaponInventory, das fuer einen
        // Pack-Inhalt naturgemaess null liefert. Im Test gemessen: ohne Drug lab sagte
        // die Medizin-Zeile Total 0 / Available 0 / alles 0, waehrend der Equipment-Packs-Reiter im selben
        // Moment Total 77 / Assigned in storage 67 / Assigned & carried 8 zeigte.
        // Dieselbe Fehlerklasse wie der urspruengliche Befund - repariert war nur der Hauptzweig,
        // die zwei Ausstiege daneben nicht.
        // Ob eine Werkbank dasteht, aendert nichts daran, WIEVIEL die Kolonie hat. Es aendert nur, ob sich
        // etwas herstellen laesst - und das sagt die Zeile ohnehin in eigenen Worten.
        private static LoadoutUtility.AvailabilitySegments RowAvailability(WeaponEntry e, int inProd)
        {
            if (!e.isPackItem)
            {
                // NICHT auf LoadoutUtility.WeaponAvailability umstellen (2026-08-15, als vier andere Kopien
                // dieser Rechnung dorthin zusammengezogen wurden): das `inProd` oben zaehlt nur die
                // Auftraege zum REZEPT DIESER ZEILE (QueuedFromBills ueber e.recipe), waehrend
                // WeaponQueuedCount jeden Auftrag nimmt, der die Waffe irgendwie herstellt. Bei mehreren
                // Rezepten fuer dieselbe Waffe sind das verschiedene Zahlen, und diese Zeile MEINT ihr
                // eigenes Rezept - sie steht neben dessen Werkbank und dessen Materialien.
                LoadoutUtility.GetWeaponInventory(e.weapon, out int wTotal, out int wCarried, out int wStored, out int wAssigned);
                int wCarriedAndAssigned = Mathf.Min(wCarried, wAssigned);
                int wCarriedOnly = wCarried - wCarriedAndAssigned;
                int wAssignedInStore = Mathf.Min(wStored, Mathf.Max(0, wAssigned - wCarried));
                int wFreeStored = wStored - wAssignedInStore;
                // neededCount must be TOTAL DEMAND (how many pawns' loadouts want this weapon, period -
                // wAssigned already is exactly that), not existing-stock-plus-demand: the old formula
                // (wTotal + inProd + e.unfulfilled) added the already-owned stock a second time on top of
                // e.unfulfilled (itself already a full "how many still need one" count), inflating "Still
                // missing" by however many were already sitting in storage/carried.
                return LoadoutUtility.BuildAvailabilitySegments(wCarriedOnly, wCarriedAndAssigned, wAssignedInStore, wFreeStored, inProd,
                    def: e.weapon, neededCount: wAssigned);
            }
            else
            {
                // Pack item (medicine/food/drug/utility) - ownership comes from AmmoAssignmentUtility.
                // GetPawnPacks demand, not a Weapon Loadout slot, and there's no "equipped" concept either
                // (never worn/wielded) - GetWeaponInventory would just read zero/wrong numbers here.
                // Bestand IMMER gegen den Bedarfs-Def (2026-08-19, supply-medicine-row-numbers) - `e.weapon`
                // ist das Herstellungsziel, und wer damit fragt, bekommt fuer einen generisch geforderten
                // Pack-Inhalt "nichts zugewiesen, alles frei" zurueck: ComputeItemOwnership zaehlt bei einem
                // konkreten Def nur konkret benannte Pack-Eintraege als Bedarf. Gemessen: derselbe Bestand
                // stand im Equipment-Packs-Reiter als "Assigned, in storage: 75 / Available: 0", hier als
                // "Assigned: 0 / Available: 75" - bei 600 geforderter Medizin.
                Def availDef = e.demandDef ?? e.weapon;
                LoadoutUtility.ComputeItemOwnership(availDef, out int pCarriedOnly, out int pCarriedAndAssigned, out int pAssignedInStore, out int pFreeStored, out _);
                // neededCount wie im Waffen-Zweig darueber (2026-08-19): der fehlte hier als einziger, und
                // ohne ihn kann der Balken gar keinen roten Fehlmengen-Abschnitt haben - eine Zeile, die es
                // nur GIBT, weil etwas fehlt, stand damit komplett gruen da. Die Zahl ist dieselbe, die der
                // Waffen-Zweig nimmt: der Gesamtbedarf, nicht die Fehlmenge (die steckt schon in seg.missing).
                return LoadoutUtility.BuildAvailabilitySegments(pCarriedOnly, pCarriedAndAssigned, pAssignedInStore, pFreeStored, inProd,
                    def: availDef, neededCount: LoadoutUtility.ColonyWideAssignedNeed(availDef));
            }
        }
        // ====== Dasselbe fuer die MUNITIONS-Zeilen (2026-08-22) ======
        // Sie bauten ihre Segmente von Hand und gaben fuer beide Getragen-Toepfe eine feste 0 mit. Folge, im
        // Test gesehen: die Zeile '.45 Colt (FMJ)' sagte oben 'Total: 0' und listete direkt darunter
        // 'Carried by: Yttoyyn x1'. Zwei Aussagen in EINEM Tooltip, die sich widersprechen - der Ortstooltip
        // liest das Inventar, die Zahlen darueber taten es nicht.
        // Seine Vermutung war ein geladenes Magazin; das war es nicht, es waren die fest verdrahteten Nullen.
        // LoadoutUtility.GetAmmoOwnership liefert genau die Fuenfer-Aufstellung, die der Assign-Reiter, das
        // Munitions-Fenster und der Auftrags-Reiter laengst benutzen - dieses Fenster war der einzige Ausreisser.
        // neededCount bleibt, denn nur dann hat der Balken seinen roten Fehlmengen-Abschnitt: Gesamtbedarf =
        // was schon getragen UND zugewiesen ist, plus was noch aus dem Lager kommen muss.
        private static LoadoutUtility.AvailabilitySegments AmmoRowAvailability(ThingDef ammoDef)
        {
            LoadoutUtility.GetAmmoOwnership(ammoDef, out int co, out int ca, out int ais, out int fs, out int ip);
            LoadoutUtility.ComputeAmmoAvailability(ammoDef, out _, out int demandedFromStorage);
            return LoadoutUtility.BuildAvailabilitySegments(co, ca, ais, fs, ip,
                def: ammoDef, neededCount: ca + demandedFromStorage);
        }

        // Die "wo liegt es / wer traegt es / wem ist es zugewiesen"-Aufstellung einer Gegenstandszeile.
        // Als eigene Funktion (2026-08-23), damit die drei Aufrufer die Frage nicht dreimal verschieden
        // beantworten - die blockierten Zweige (kein Rezept, keine Werkbank) hatten sie gar nicht, der
        // normale hatte sie inline. Der Produktions-Abschnitt kommt weiterhin nur im normalen Zweig
        // dazu: wo nichts hergestellt werden kann, ist auch nichts in Produktion.
        private static string RowLocationTip(WeaponEntry e)
        {
            return e.isPackItem
                ? LoadoutUtility.BuildPackItemLocationTooltip(e.demandDef ?? e.weapon)
                : LoadoutUtility.BuildWeaponLocationTooltip(e.weapon);
        }

        private static WeaponEntry BuildPackItemEntry(ThingDef item, int count, Def demandDef = null)
        {
            WeaponEntry e = new WeaponEntry { weapon = item, countStr = count.ToString(), isPackItem = true, demandDef = demandDef };
            e.recipe = LoadoutUtility.GetRecipeFor(item);
            e.craftable = e.recipe != null && LoadoutUtility.RecipeIsResearched(e.recipe);
            e.ings = LoadoutUtility.GetRecipeIngredients(e.recipe);

            if (e.craftable)
            {
                // Zwilling der Waffenzeile darueber - dieselbe Quelle, dieselbe Begruendung (2026-09-04).
                e.benches = LoadoutUtility.BenchesForRecipe(e.recipe);
                if (CEReloadedMod.settings != null && CEReloadedMod.settings.hideDuplicateBenches)
                {
                    HashSet<ThingDef> seenDefs = new HashSet<ThingDef>();
                    e.benches = e.benches.Where(b => seenDefs.Add(b.def)).ToList();
                }
                e.benchLabels = LoadoutUtility.BuildBenchLabels(e.benches);
                e.bench = e.benches.FirstOrDefault();

                if (item.MadeFromStuff)
                {
                    e.stuffs = GenStuff.AllowedStuffsFor(item).ToList();
                    e.stuff = e.stuffs.FirstOrDefault(s => s == ThingDefOf.Steel) ?? e.stuffs.FirstOrDefault();
                    e.ings = LoadoutUtility.GetRecipeIngredients(e.recipe, e.stuff);
                }
            }

            e.selected = e.craftable && e.bench != null;

            if (e.craftable && e.recipe != null && e.recipe.products != null && e.recipe.products.Count > 0)
                e.yield = Mathf.Max(1, e.recipe.products[0].count);
            return e;
        }

        // Ueber LoadoutUtility.QueuedFromBills, nicht mehr mit eigener Formel: hier stand die alte, zu
        // optimistische Rechnung, und ihr Ergebnis geht in `remaining = count - queued` - eine Zeile wurde
        // damit abgewaehlt und auf 0 gesetzt, obwohl der Auftrag die Luecke gar nicht deckt.
        private static int QueuedCount(RecipeDef recipe)
        {
            if (recipe == null) return 0;
            List<Bill_Production> bills = new List<Bill_Production>();
            foreach (Building_WorkTable b in LoadoutUtility.AllColonyBenches())
            {
                if (b.billStack == null) continue;
                foreach (Bill_Production bill in b.billStack.Bills.OfType<Bill_Production>())
                    if (bill.recipe == recipe) bills.Add(bill);
            }
            return LoadoutUtility.QueuedFromBills(bills, recipe.products != null && recipe.products.Count > 0 ? recipe.products[0].thingDef : null);
        }

        // ================================================================================================
        // Ammo (generic auto-resolved eagerly; a per-row link lets the player switch to another family member)
        // ================================================================================================

        // Ein Def darf nie gleichzeitig in einer HERSTELLEN- und in einer AUFRAEUMEN-Liste desselben Fensters
        // stehen (2026-08-15: wie kann etwas zugleich als obsolet und als herzustellen gelistet sein - ist
        // das nicht ein grundlegender Fehler?). Ja - und zwar genau die Fehlerklasse, die dieser Mod am haeufigsten
        // produziert: zwei unabhaengige Rechnungen ueber dieselbe Frage, die einander nicht kennen.
        //
        // Warum die AUFRAEUMEN-Seite gewinnt und nicht die Herstellen-Seite: "ueberfluessig" heisst, dass die
        // Bedarfsrechnung ueber alle Kolonisten bzw. alle Vorlagen NICHTS mehr findet, was das Ding verlangt.
        // Die Herstellen-Zahl dagegen kann ein stehengebliebener Wert aus der Zeile des Munitions-Reiters
        // sein - im beobachteten Fall war genau das so: die Zweitwaffe war gerade abgenommen,
        // der Bedarf war weg, die Stapelzahl in der Zeile stand noch. Etwas zu produzieren, das im selben
        // Fenster als "niemand braucht das" gelistet ist, kann nie richtig sein.
        //
        // HIER und nicht an den Aufrufern: es gibt fuenf Baustellen, die so eine Anfrage zusammensetzen
        // (AttachObsoleteCleanup und die vier Aufrufer daneben), und eine davon baut ihre Listen bewusst
        // selbst. Eine Pruefung pro Aufrufer waere beim naechsten neuen Einstieg still unvollstaendig -
        // dieselbe Falle, in die der Fix vom 14.08. gelaufen ist (er sass in EINER der zwei
        // Kandidatenlisten; die Waffen-Haelfte daneben widersprach der Munitions-Zeile weiterhin).
        //
        // Generische Munition braucht nichts davon: UnneededAmmoBillCandidates nimmt ueber
        // CollectGenericAmmoFamilies alles heraus, was ein generischer Platz abdeckt - ein generisch
        // verlangter Def landet also gar nicht erst in der Aufraeumliste.
        private static void DropObsoleteFromProduceLists(Request req)
        {
            bool anyAmmo = req.obsoleteAmmoCandidates != null && req.obsoleteAmmoCandidates.Count > 0;
            bool anyWeapon = req.obsoleteWeaponCandidates != null && req.obsoleteWeaponCandidates.Count > 0;
            if (!anyAmmo && !anyWeapon) return;

            HashSet<ThingDef> obsolete = new HashSet<ThingDef>();
            if (anyAmmo) foreach (ThingDef d in req.obsoleteAmmoCandidates) if (d != null) obsolete.Add(d);
            if (anyWeapon) foreach (ThingDef d in req.obsoleteWeaponCandidates) if (d != null) obsolete.Add(d);

            // RICHTUNG GEDREHT (2026-08-16: im neuen Fenster fehlten Posten, z.B. die Satchel Charge -
            // der Bedarf wurde nicht richtig berechnet).
            //
            // Heute Vormittag habe ich hier entschieden, dass "ueberfluessig" gewinnt und der Gegenstand aus
            // der Herstellen-Liste faellt. Die Begruendung stimmt weiterhin fuer den Fall, aus dem sie kam
            // (Waffe gerade abgenommen, Bedarf wirklich weg) - aber sie hat eine Nebenwirkung, die schwerer
            // wiegt als der Widerspruch selbst: steht der Gegenstand in BEIDEN Listen, weil zwei
            // Bedarfsrechnungen sich uneinig sind, verschwand ein ECHTER Bedarf lautlos aus dem Fenster.
            //
            // Die zwei Ausgaenge sind nicht gleich schlimm:
            //   Bedarf verstecken  -> der Spieler produziert zu wenig und sieht nicht, warum. Still.
            //   Auftrag stehenlassen -> ein Auftrag zu viel, sichtbar im Munitions-Reiter, ein Klick weg.
            // Also gewinnt ab jetzt die Herstellen-Seite, und der Gegenstand faellt aus der AUFRAEUM-Liste.
            // Die Regel "nie in beiden Listen" bleibt damit unveraendert - nur die Seite, die nachgibt.
            //
            // Die eigentliche Ursache ist damit NICHT behoben: zwei Rechnungen widersprechen sich beim
            // Wurfsprengstoff (die Waffen-Bedarfsrechnung uebersieht Wurfwaffen). Dort ist
            // das wirklich zu reparieren.
            StripObsolete(req.obsoleteAmmoCandidates, req);
            StripObsolete(req.obsoleteWeaponCandidates, req);
        }

        // Nimmt aus einer Aufraeumliste alles heraus, was dieses Fenster gleichzeitig HERSTELLEN will.
        // Rueckwaerts zur ersten Fassung von heute Vormittag - die Begruendung steht oben.
        private static void StripObsolete(List<ThingDef> candidates, Request req)
        {
            if (candidates == null || candidates.Count == 0) return;
            for (int i = candidates.Count - 1; i >= 0; i--)
            {
                ThingDef d = candidates[i];
                if (d == null) continue;
                if (Wants(req.specificAmmo, d) || Wants(req.mainWeapons, d) || Wants(req.secondaryWeapons, d)
                    || Wants(req.shields, d) || Wants(req.packItems, d))
                    candidates.RemoveAt(i);
            }
        }

        private static bool Wants(Dictionary<ThingDef, int> produce, ThingDef d)
        {
            return produce != null && produce.TryGetValue(d, out int n) && n > 0;
        }

        private void BuildAmmoEntries(Request req)
        {
            // resolved ammo def -> aggregated entry. Generic and specific requests both fold into this so the same
            // concrete ammo requested two ways (a generic that resolves to FMJ, plus an explicit FMJ request) merges
            // into one row instead of two.
            Dictionary<ThingDef, AmmoEntry> byResolved = new Dictionary<ThingDef, AmmoEntry>();

            AmmoEntry GetOrCreate(ThingDef resolved, LoadoutGenericDef fromGeneric)
            {
                if (!byResolved.TryGetValue(resolved, out AmmoEntry e))
                {
                    e = new AmmoEntry { resolved = resolved, sourceGeneric = fromGeneric };
                    e.recipe = LoadoutUtility.GetRecipeFor(resolved);
                    e.ings = e.recipe != null ? LoadoutUtility.GetRecipeIngredients(e.recipe) : new List<ThingDefCountClass>();
                    e.yield = Mathf.Max(1, LoadoutUtility.GetAmmoRecipeYield(resolved));
                    e.alreadyInProduction = LoadoutUtility.WeaponHasActiveBill(resolved);
                    e.selected = LoadoutUtility.IsAmmoResearched(resolved) && !e.alreadyInProduction;
                    e.countStr = "0";
                    e.benches = AmmoBenchesFor(e.recipe);
                    if (req.ammoBenches != null && req.ammoBenches.TryGetValue(resolved, out Building_WorkTable presetBench))
                        e.bench = presetBench;
                    else
                        e.bench = e.benches.FirstOrDefault();
                    byResolved[resolved] = e;
                }
                else if (fromGeneric != null) e.sourceGeneric = e.sourceGeneric ?? fromGeneric;
                return e;
            }

            void AddCount(AmmoEntry e, int n) { int.TryParse(e.countStr, out int c); e.countStr = (c + n).ToString(); }

            if (req.genericAmmo != null)
            {
                foreach (var kvp in req.genericAmmo)
                {
                    ThingDef gun = LoadoutUtility.GetWeaponForGenericAmmo(kvp.Key);
                    List<Def> family = LoadoutUtility.GetAmmoTypesFor(gun).Cast<Def>().ToList();
                    List<ThingDef> researchedFamily = family.OfType<ThingDef>().Where(a => LoadoutUtility.IsAmmoResearched(a)).ToList();
                    if (researchedFamily.Count == 0) continue;
                    // Auto-resolve (plan 5.8): pick the player's default firearm ammo preference silently, no forced
                    // review popup - the row below still lets them switch via "Change...".
                    Def picked = LoadoutUtility.PickDefaultAmmo(researchedFamily.Cast<Def>().ToList());
                    if (!(picked is ThingDef resolved)) continue;
                    AmmoEntry e = GetOrCreate(resolved, kvp.Key);
                    e.alternatives = researchedFamily.Where(a => a != resolved).ToList();
                    AddCount(e, kvp.Value);
                }
            }
            if (req.specificAmmo != null)
            {
                foreach (var kvp in req.specificAmmo)
                {
                    // Unresearched ammo used to be silently skipped entirely here - a colonist manually given
                    // an ammo type that can never be produced would then vanish from this popup with no trace,
                    // reading as "nothing missing" when it very much was. Still created (DrawAmmoEntry shows a
                    // "can't be produced" row for it, same treatment DrawWeaponEntry already gives weapons),
                    // just never selectable/counted toward a bill.
                    AmmoEntry e = GetOrCreate(kvp.Key, null);
                    AddCount(e, kvp.Value);
                }
            }

            // Wer auf "Any ammo for X" steht, steht in diesen beiden Buechern NUR unter dem GENERISCHEN Def -
            // nie unter einer konkreten Sorte (ComputeAmmoNeedForLoadouts legt den Slot so ab, wie er im
            // Loadout steht). Bis zum 2026-08-14 wurde er hier nur ueber `e.sourceGeneric` gefunden, und das
            // Feld ist genau dann gesetzt, wenn die Zeile aus `req.genericAmmo` entstanden ist. Zwei der vier
            // Einstiege in dieses Fenster fuellen `genericAmmo` aber gar nicht (Bill Overview -> Auto-generate
            // reicht nur `specificAmmo` durch, die Bedarfszahl ist dort schon aufgeloest) - dort war
            // sourceGeneric null, die Suche lief nur ueber den konkreten Def, und die Raute fiel auf 0 mit dem
            // Tooltip "kein Kolonist hat ein Loadout, das diese Munition benutzt", obwohl zwei Kolonisten die
            // passende Waffe trugen. Im Test am 14.08. gemessen; schwer, weil die Raute die Frage
            // "braucht das ueberhaupt noch jemand" beantwortet und ihre Antwort in die Nachschubplanung
            // durchschlaegt.
            // Aufgeloest auf die ganze FAMILIE, nicht auf die eine Vorgabesorte: das ist dieselbe Antwort auf
            // dieselbe Frage, die CollectGenericAmmoFamilies fuer den Obsolet-Test gibt (siehe dessen
            // Kommentar - "wird das noch gebraucht" darf nicht mit "wie viel soll ich herstellen" verwechselt
            // werden). Sonst widersprechen sich beide im selben Fenster: eine Zeile gilt als nicht
            // ueberfluessig, weil ein generischer Slot sie deckt, und die Raute daneben sagt, niemand
            // brauche sie.
            Dictionary<ThingDef, List<string>> ExpandGenerics(Dictionary<Def, List<string>> map)
            {
                var byVariant = new Dictionary<ThingDef, List<string>>();
                if (map == null) return byVariant;
                foreach (var kv in map)
                {
                    if (!(kv.Key is LoadoutGenericDef gd) || kv.Value == null) continue;
                    HashSet<ThingDef> family = new HashSet<ThingDef>();
                    LoadoutUtility.CollectGenericAmmoFamilies(new List<LoadoutGenericDef> { gd }, family);
                    foreach (ThingDef variant in family)
                    {
                        if (!byVariant.TryGetValue(variant, out List<string> list)) byVariant[variant] = list = new List<string>();
                        foreach (string n in kv.Value) if (!list.Contains(n)) list.Add(n);
                    }
                }
                return byVariant;
            }

            void FillNames(Dictionary<Def, List<string>> map, Action<AmmoEntry, List<string>> assign)
            {
                if (map == null) return;
                Dictionary<ThingDef, List<string>> genericByVariant = ExpandGenerics(map);
                foreach (AmmoEntry e in byResolved.Values)
                {
                    List<string> names = new List<string>();
                    if (e.sourceGeneric != null && map.TryGetValue(e.sourceGeneric, out List<string> gNames)) names.AddRange(gNames);
                    if (map.TryGetValue(e.resolved, out List<string> sNames)) names.AddRange(sNames);
                    if (genericByVariant.TryGetValue(e.resolved, out List<string> fNames)) names.AddRange(fNames);
                    assign(e, names.Distinct().ToList());
                }
            }

            FillNames(req.assignedUsedBy, (e, names) => e.assignedNames = names);
            FillNames(req.templateUsedBy, (e, names) => e.templateNames = names);

            // ============ WURFSPRENGSTOFF HOLT SEINE NAMEN VON DER WAFFENSEITE (2026-08-18) ============
            //
            // Vierte Ausgabe derselben Frage in zwei Wochen, und diesmal war sie mit zwei
            // widersprechenden Tooltips im SELBEN Fenster belegt: die Assig.-Spalte der Satchel charge sagte
            // "No colonist is assigned a loadout that uses this ammo", waehrend der Verfuegbarkeits-Tooltip
            // derselben Zeile "Assigned to: Mack x4, Ryder x8, Sammy x4" auflistete. Mack und Sammy trugen
            // sie wirklich als Zweitwaffe.
            //
            // Die Ursache ist keine Fehlrechnung, sondern eine Zustaendigkeitsluecke. Ein Wurfsprengstoff IST
            // technisch Munition (AmmoThing) und landet deshalb in der Munitions-LISTE - seinen Bedarf
            // rechnet aber ausdruecklich die WAFFENSEITE (ComputeAmmoNeedForLoadouts ueberspringt
            // IsSelfAmmoWeapon per Design, mit guter Begruendung: sonst wird derselbe Gegenstand in zwei
            // Systemen doppelt gebucht). Die Zeile fragte also die eine Rechnung, waehrend die andere
            // zustaendig war - und bekam verlaesslich "niemand" zurueck.
            //
            // Deshalb holt sie ihre Namen jetzt von dort, wo ihr Bedarf wirklich gerechnet wird. Alles
            // andere in der Liste bleibt unveraendert bei der Munitionsseite.
            // 2026-08-31, erneut aufgetreten: mindestens Mack hatte eine Satchel Charge zugeteilt, die
            // Assigned-Spalte sagte aber, dass kein Loadout sie nutzt und sie niemandem zugeteilt ist -
            // beides falsch. Und
            // der Fix vom 18.08. war nicht unvollstaendig, sondern WIRKUNGSLOS: die Schleife lief ueber
            // `ammoEntries` - und diese Liste wird erst am Ende dieser Methode gefuellt (AddRange aus
            // byResolved.Values, rund 30 Zeilen weiter unten). Sie war hier IMMER leer, der Rumpf ist seit
            // dem 18.08. kein einziges Mal gelaufen. Deshalb jetzt dieselbe Quelle wie die Zeile darueber:
            // byResolved.Values, die Sammlung, die es an dieser Stelle wirklich schon gibt.
            // Zusammengefuehrt statt ersetzt: die Munitionsseite laesst Wurfsprengstoff zwar per Design aus
            // (ComputeAmmoNeedForLoadouts ueberspringt IsSelfAmmoWeapon, sonst wird derselbe Gegenstand
            // doppelt gebucht), aber ein Ersetzen wuerde einen Namen verlieren, sobald das je anders wird.
            if (req.weaponAssignedUsedBy != null || req.weaponTemplateUsedBy != null)
            {
                foreach (AmmoEntry e in byResolved.Values)
                {
                    if (e?.resolved == null || !LoadoutUtility.IsSelfAmmoWeapon(e.resolved)) continue;
                    if (req.weaponAssignedUsedBy != null
                        && req.weaponAssignedUsedBy.TryGetValue(e.resolved, out List<string> wa) && wa != null)
                        e.assignedNames = e.assignedNames.Concat(wa).Distinct().ToList();
                    if (req.weaponTemplateUsedBy != null
                        && req.weaponTemplateUsedBy.TryGetValue(e.resolved, out List<string> wt) && wt != null)
                        e.templateNames = e.templateNames.Concat(wt).Distinct().ToList();
                }
            }

            // "Reicht der vorhandene Auftrag?" statt "gibt es irgendeinen?" (2026-08-09).
            // Muss HIER stehen und nicht in GetOrCreate: dort ist der Bedarf noch 0, er wird erst durch die
            // AddCount-Schleifen oben aufsummiert. Vorher genuegte ein alter Auftrag ueber 100 Schuss, damit
            // eine Zeile mit 500 Bedarf als erledigt galt - und weil "nur Fehlendes zeigen" standardmaessig an
            // ist, verschwand die Zeile dann ganz und der Nachschub-Popup ging unter Umstaenden gar nicht auf.
            // Mitten im Spiel meldete der Mod also "nichts fehlt", waehrend im Gefecht Leute leer ausgingen.
            // Verglichen wird gegen das ZIEL des Auftrags, nicht gegen dessen Restmenge: countStr ist hier ein
            // Lagerziel ("stelle her bis X"), kein Fehlbetrag - deshalb bleibt die Zahl unangetastet und nur
            // das Kennzeichen "schon abgedeckt" wird ehrlich. Fuer WAFFEN gibt es dieselbe mengengenaue
            // Rechnung seit 2026-07-31 zwoelf Zeilen weiter oben; Munition hatte sie nie bekommen.
            foreach (AmmoEntry e in byResolved.Values)
            {
                if (!int.TryParse(e.countStr, out int needed) || needed <= 0) continue;
                int existingTarget = LoadoutUtility.HighestBillTargetFor(e.resolved);
                e.alreadyInProduction = existingTarget >= needed;
                e.selected = LoadoutUtility.IsAmmoResearched(e.resolved) && !e.alreadyInProduction;
            }

            ammoEntries.AddRange(byResolved.Values.OrderByDescending(e => int.TryParse(e.countStr, out int c) ? c : 0).ThenBy(e => e.resolved.label));
        }

        // Switch a resolved ammo row to a different family member (from the "Change..." link) - rebuilds the entry
        // in place, field-by-field exactly like a fresh GetOrCreate, so ingredients/bench/production status AND the
        // selected checkbox all stay correct for the new choice (a family can mix already-in-production and not-yet
        // members, so leaving `selected` at the old choice's value would silently un-flag a genuinely needed bill).
        private void SwitchAmmoChoice(AmmoEntry e, ThingDef newChoice)
        {
            List<ThingDef> family = new List<ThingDef>(e.alternatives) { e.resolved };
            e.alternatives = family.Where(a => a != newChoice).ToList();
            e.resolved = newChoice;
            e.recipe = LoadoutUtility.GetRecipeFor(newChoice);
            e.ings = e.recipe != null ? LoadoutUtility.GetRecipeIngredients(e.recipe) : new List<ThingDefCountClass>();
            // Mengengenau wie beim Aufbau der Zeilen (2026-08-09) - sonst wechselt man die Sorte und bekommt
            // fuer die neue wieder das alte "irgendein Auftrag genuegt"-Urteil.
            int switchNeeded = int.TryParse(e.countStr, out int sn) ? sn : 0;
            e.alreadyInProduction = switchNeeded > 0
                ? LoadoutUtility.HighestBillTargetFor(newChoice) >= switchNeeded
                : LoadoutUtility.WeaponHasActiveBill(newChoice);
            e.selected = LoadoutUtility.IsAmmoResearched(newChoice) && !e.alreadyInProduction;
            e.benches = AmmoBenchesFor(e.recipe);
            if (presetAmmoBenches != null && presetAmmoBenches.TryGetValue(newChoice, out Building_WorkTable presetBench))
                e.bench = presetBench;
            else
                e.bench = e.benches.FirstOrDefault();
        }

        // All benches whose def is a recipeUser of this ammo recipe, in stable id order. Built once per entry
        // (and again on a variant switch) - the draw loop reads the cached list every frame.
        private static List<Building_WorkTable> AmmoBenchesFor(RecipeDef recipe)
            // Ueber die gemeinsame Fassung (2026-09-04): hier stand eine zweite Kopie derselben Formel,
            // und sie kannte nur den recipeUsers-Weg - ein Rezept, das ueber die recipes-Liste der Bank
            // haengt, war fuer sie unsichtbar. Siehe LoadoutUtility.BenchCanRun.
            => LoadoutUtility.BenchesForRecipe(recipe);

        // ================================================================================================
        // UI
        // ================================================================================================

        // True when, with the missing-only filter applied, at least one row would actually be visible - i.e. there
        // really is something to supply. Callers check this BEFORE opening the window, so the popup never appears
        // just to say "everything is already covered" (its own diffOnly filter would otherwise hide every row the
        // caller's raw need-count promised).
        public bool AnythingToSupply =>
            AnythingToProduce
            || obsoleteAmmoCandidates.Count > 0 || obsoleteWeaponCandidates.Count > 0;

        // Dasselbe OHNE die Aufraeumliste (2026-08-13). Der Unterschied ist
        // kein Detail: "es gibt hier etwas zu tun" und "es fehlt etwas" sind zwei verschiedene Aussagen, und
        // wer aus einer WARNUNG heraus hierher geschickt wird, hat die zweite gemeint. Im Test stand in
        // dem Fenster, das die Warnung "Low on ammo" geoeffnet hat, ausschliesslich ein Vorschlag,
        // Sprengsatz-Auftraege abzubrechen - fuer AnythingToSupply reicht das, fuer eine Antwort auf
        // "mir fehlt Munition" nicht.
        // Ein Knopf wie "Auftraege aufraeumen" darf weiterhin AnythingToSupply benutzen; ein Weg, der aus
        // einem Mangel kommt, nimmt diese Eigenschaft hier.
        public bool AnythingToProduce =>
            mainEntries.Concat(secEntries).Concat(shieldEntries).Concat(packEntries).Any(e => !e.craftable || e.countStr != "0")
            || ammoEntries.Any(e => !e.alreadyInProduction);

        // Wie viele ZEILEN dieses Fenster zum Produzieren zeigt - dieselbe Bedingung wie AnythingToProduce, nur
        // gezaehlt (2026-08-30: 'Colony problems' meldete (4) production gaps, am Ende gab es aber nur
        // einen). Die Bereitschaftsanzeige zaehlte KOLONISTEN, das Fenster zeigt POSTEN - vier
        // Kolonisten mit derselben fehlenden Munition sind eine Zeile. Der Knopf nennt jetzt diese Zahl.
        public int ProduceRowCount =>
            mainEntries.Concat(secEntries).Concat(shieldEntries).Concat(packEntries).Count(e => !e.craftable || e.countStr != "0")
            + ammoEntries.Count(e => !e.alreadyInProduction);

        public override void DoWindowContents(Rect inRect)
        {
            // Spaltenschiebung fuer diese Sichtbreite (viewRect.width = inRect.width - 16, siehe unten) - VOR
            // der Kopfzeile, die dieselben Spaltenwerte liest.
            UpdateColumnShift(inRect.width - 16f);
            Text.Font = GameFont.Medium;
            Widgets.Label(new Rect(0, 0, inRect.width, 35f), "CER_SupplyLoadouts_Title".Translate());
            Text.Font = GameFont.Small;


            bool anyCovered = mainEntries.Concat(secEntries).Concat(shieldEntries).Any(e => e.craftable && e.countStr == "0")
                || ammoEntries.Any(e => e.alreadyInProduction);
            // Right-aligned next to the title row, clear of the window's close X (which floats over the top-right).
            Rect toggleRect = new Rect(inRect.width - 280f, 6f, 236f, 24f);
            if (anyCovered)
            {
                bool nd = diffOnly;
                Widgets.CheckboxLabeled(toggleRect, "CER_SupplyLoadouts_ShowOnlyMissingCheckbox".Translate(), ref nd);
                if (nd != diffOnly) diffOnly = nd;
            }

            float listY = 36f;
            if (LoadoutUtility.ColumnHeader(new Rect(NameX, listY, SpecialX - NameX - 4f, 24f), "CER_SupplyLoadouts_ColName".Translate(),
                currentSortMode == SortMode.Name, sortDescending, true)) SetSort(SortMode.Name);
            LoadoutUtility.ColumnHeader(new Rect(SpecialX, listY, 80f, 24f), "CER_SupplyLoadouts_ColSpecial".Translate(), false, false, false);
            // Widened to cover both indicators (2026-08-02) - diamond (assigned) + circle (used in loadout),
            // always both shown now instead of one column whose meaning used to flip with the scope radio.
            // Sorting still keys off the diamond/assigned count - the more commonly meaningful of the two.
            Rect assignedHeaderRect = new Rect(UsedX, listY, 60f, 24f);
            if (LoadoutUtility.ColumnHeader(assignedHeaderRect, "CER_SupplyLoadouts_ColAssigned".Translate(),
                currentSortMode == SortMode.Assigned, sortDescending, true)) SetSort(SortMode.Assigned);
            TooltipHandler.TipRegion(assignedHeaderRect, "CER_SupplyLoadouts_ColAssignedTip".Translate());
            LoadoutUtility.ColumnHeader(new Rect(IngX, listY, 150f, 24f), "CER_SupplyLoadouts_ColIngredients".Translate(), false, false, false);
            LoadoutUtility.ColumnHeader(new Rect(MaterialX, listY, 120f, 24f), "CER_SupplyLoadouts_ColMaterial".Translate(), false, false, false);
            LoadoutUtility.ColumnHeader(new Rect(BatchX, listY, CountX - BatchX - 4f, 24f), "CER_SupplyLoadouts_ColBatches".Translate(), false, false, false);
            // "Stock"/"Lager", nicht mehr "Count" (2026-08-09): dasselbe Wort stand im Loadout-Editor fuer
            // "Schuss, die EIN Kolonist traegt" und hier fuer "Lagerziel der ganzen Kolonie" - bei acht Leuten
            // ein Faktor acht, und nichts auf dem Bildschirm sagte, welches von beiden gemeint ist. Die Spalte
            // ist nur 50px breit (CountX 768 bis ProdX 832), deshalb kurzes Wort plus Tooltip statt langem Kopf.
            Rect countHeaderRect = new Rect(CountX, listY, 50f, 24f);
            if (LoadoutUtility.ColumnHeader(countHeaderRect, "CER_SupplyLoadouts_ColCount".Translate(),
                currentSortMode == SortMode.Count, sortDescending, true)) SetSort(SortMode.Count);
            TooltipHandler.TipRegion(countHeaderRect, "CER_SupplyLoadouts_ColCountTip".Translate());
            LoadoutUtility.ColumnHeader(new Rect(ProdX, listY, 155f, 24f), "CER_SupplyLoadouts_ColAvailability".Translate(), false, false, false);
            // Bis zur rechten Zeilenkante statt fester 130 (2026-08-25): 130 ragte schon bei voller Breite
            // 8 px ueber das Info-Symbol hinaus und bei jeder Schiebung ueber die Sicht (Renderer-Befund).
            LoadoutUtility.ColumnHeader(new Rect(BenchX, listY, InfoX + 18f - BenchX, 24f), "CER_SupplyLoadouts_ColWorkbench".Translate(), false, false, false);
            listY += 28f;

            // Sichtbarer Abstand zwischen Liste und 'Total cost' (2026-08-30, grosse Kolonie: die letzte,
            // halb abgeschnittene Zeile las sich wie von der Fusszeile ueberdeckt). Die Liste endet an einer Trennlinie,
            // die Kosten stehen 12 Punkte darunter - eine angeschnittene Zeile ist damit als 'geht weiter' zu lesen.
            Rect outRect = new Rect(0, listY, inRect.width, inRect.height - listY - 50f - 36f);
            // Rueckfall (b): ist die Zeile trotz Schiebung breiter als die Sicht, wird viewRect so breit wie
            // die Zeile - die ScrollView zeigt dann einen waagrechten Balken statt Bedienelemente abzuschneiden.
            Rect viewRect = new Rect(0, 0, Mathf.Max(outRect.width - 16f, RowWidth + 2f), ContentHeight());
            // Hoehe des sichtbaren Ausschnitts fuer die Scroll-Kulisse merken (2026-08-14) - siehe RowCulled.
            visibleListH = outRect.height;
            Widgets.BeginScrollView(outRect, ref scroll, viewRect);

            float y = 0f;
            DrawWeaponSection(viewRect, ref y, "CER_SupplyLoadouts_SectionMainWeapons".Translate(), mainEntries);
            DrawWeaponSection(viewRect, ref y, "CER_SupplyLoadouts_SectionSidearms".Translate(), secEntries);
            DrawWeaponSection(viewRect, ref y, "CER_SupplyLoadouts_SectionShields".Translate(), shieldEntries);
            DrawWeaponSection(viewRect, ref y, "CER_SupplyLoadouts_SectionPackItems".Translate(), packEntries);
            DrawAmmoSection(viewRect, ref y);
            // Der Abschnitt "Not producible - superior loadouts skipped" ist am 2026-08-25 ersatzlos entfallen
            // (er ergab keinen Sinn und passte nicht zu Assign Loadouts). Mit ihm der Schattenlauf im Loeser,
            // der ihn befuellt hat.
            // Nichts zu zeigen heisst: Abschnitt weg (2026-08-31: statt "nothing to cancel" zu sagen, wird
            // der ganze Abschnitt weggelassen, wenn es nichts gibt).
            if (obsoleteAmmoCandidates.Count > 0 || obsoleteWeaponCandidates.Count > 0)
            {
                if (y > 0.5f) y += 6f;
                // Ueberschrift traegt die Begruendung selbst, die eigene Erklaerzeile darunter ist weg
                // (2026-09-02, Begruendung in die Ueberschrift). Links im Eck der Master-Haken:
                // er schaltet ALLE Zeilen hart um - dieselbe Konvention wie das Master-Symbol im
                // Zuweisungs-Reiter (Entscheidung master-toggle-include-pins: "Master schaltet ALLES um").
                int totalObs = obsoleteWeaponCandidates.Count + obsoleteAmmoCandidates.Count;
                bool allObsOn = obsoleteWeaponChecked.Count + obsoleteAmmoChecked.Count >= totalObs;
                bool obsToggle = allObsOn;
                Widgets.Checkbox(new Vector2(0f, y + 1f), ref obsToggle, 20f);
                if (obsToggle != allObsOn)
                {
                    obsoleteWeaponChecked.Clear();
                    obsoleteAmmoChecked.Clear();
                    if (obsToggle)
                    {
                        foreach (ThingDef d in obsoleteWeaponCandidates) obsoleteWeaponChecked.Add(d);
                        foreach (ThingDef d in obsoleteAmmoCandidates) obsoleteAmmoChecked.Add(d);
                    }
                }
                TooltipHandler.TipRegion(new Rect(0f, y, 22f, 22f), "CER_SupplyLoadouts_ObsoleteMasterTip".Translate());
                Text.Font = GameFont.Small;
                GUI.color = new Color(0.85f, 0.87f, 0.9f);
                Widgets.Label(new Rect(26f, y, viewRect.width - 26f, 22f), "<b>" + "CER_SupplyLoadouts_ObsoleteSectionTitle".Translate() + "</b>");
                GUI.color = new Color(1f, 1f, 1f, 0.18f);
                Widgets.DrawLineHorizontal(0f, y + 22f, viewRect.width);
                GUI.color = Color.white;
                y += HeaderH;
                // One shared section for both - a bill is a bill regardless of whether it produces ammo or a
                // weapon, and the keyed section string above is already worded generically.
                foreach (ThingDef d in obsoleteWeaponCandidates) DrawObsoleteWeaponRow(viewRect, ref y, d);
                foreach (ThingDef d in obsoleteAmmoCandidates) DrawObsoleteAmmoRow(viewRect, ref y, d);
            }

            if (viewRect.height < 10f)
            {
                GUI.color = new Color(1f, 1f, 1f, 0.5f);
                Widgets.Label(new Rect(0, 4f, inRect.width, 24f), "CER_SupplyLoadouts_EverythingCovered".Translate());
                GUI.color = Color.white;
            }

            Widgets.EndScrollView();
            // Direkt unter dieser Liste steht die Kostenzeile, und dazwischen ist kein Millimeter Luft - ohne
            // Hinweis liest sich ein Ausschnitt wie die ganze Liste (2026-08-13, an "Obsolete bills"
            // aufgefallen: 3 von 7 Zeilen sichtbar). RowH, damit dort eine Zeilenzahl steht.
            LoadoutUtility.DrawScrollMoreHint(outRect, viewRect, scroll, RowH);

            // Aggregated bill: what everything currently selected will consume, so the real material hit is visible
            // before pressing Supply.
            GUI.color = new Color(1f, 1f, 1f, 0.25f);
            Widgets.DrawLineHorizontal(0f, outRect.yMax + 3f, inRect.width);
            GUI.color = Color.white;
            LoadoutUtility.DrawTotalCostLine(0f, outRect.yMax + 12f, inRect.width, ComputeTotalCost());

            // A Back button only when there is somewhere to go back to (e.g. the assign preview) - matches the
            // same Back+Cancel layout as Dialog_SquadSetup so the whole chain reads consistently.
            // EINE Schriftstufe fuer die GANZE Fussleiste (2026-09-04, dieselbe Fundstellenklasse wie in der
            // Auftragsuebersicht): Back und Cancel wurden gezeichnet, BEVOR BottomBarFont ueberhaupt lief,
            // und waren an seiner Rechnung nicht beteiligt. Deshalb alle vier Zellen vorab anmelden.
            string backLbl = "CER_SupplyLoadouts_BackButton".Translate().ToString();
            string cancelLbl = "CER_SupplyLoadouts_CancelButton".Translate().ToString();
            bool deferredForLabel = onConfirm != null;
            string produceLabel = deferredForLabel ? "CER_SupplyLoadouts_ConfirmSupplyButton".Translate().ToString() : "CER_SupplyLoadouts_SupplySelectedButton".Translate().ToString();
            string supplyNothingLabel = "CER_SupplyLoadouts_SupplyNothingButton".Translate().ToString();
            GameFont prevBarFont = Text.Font;
            Text.Font = onBack != null
                ? LoadoutUtility.BottomBarFont(
                    (100f, backLbl, false),
                    (LoadoutUtility.CloseBtnW, cancelLbl, false),
                    (LoadoutUtility.PrimaryBtnW, produceLabel, false),
                    (LoadoutUtility.PrimaryBtnW, supplyNothingLabel, false))
                : LoadoutUtility.BottomBarFont(
                    (LoadoutUtility.CloseBtnW, cancelLbl, false),
                    (LoadoutUtility.PrimaryBtnW, produceLabel, false),
                    (LoadoutUtility.PrimaryBtnW, supplyNothingLabel, false));

            if (onBack != null)
            {
                Rect backR = new Rect(0f, inRect.height - LoadoutUtility.BottomBarH, 100f, LoadoutUtility.BottomBtnH);
                if (LoadoutUtility.ButtonTextFit(backR, backLbl))
                {
                    wentBack = true;
                    Close();
                    onBack();
                }
                TooltipHandler.TipRegion(backR, "CER_SupplyLoadouts_BackButtonTip".Translate());
                if (LoadoutUtility.ButtonTextFit(new Rect(108f, inRect.height - LoadoutUtility.BottomBarH, LoadoutUtility.CloseBtnW, LoadoutUtility.BottomBtnH), cancelLbl))
                    Close();
            }
            else if (LoadoutUtility.BottomCloseButton(inRect, cancelLbl)) Close();

            bool deferred = onConfirm != null;
            // Munitionszeilen zaehlen nur, wenn sie wirklich anwaehlbar sind (2026-08-25: drei gesperrte
            // Zeilen - keine Forschung, kein Rezept, keine Ladebank -, und "Supply selected" war trotzdem
            // aktiv; in solchen Faellen ausgegraut, nicht durchgestrichen, "Supply nothing" bleibt aktiv).
            // Vorher genuegte `ammoEntries.Count > 0`, also auch eine Zeile,
            // die DrawAmmoEntry selbst als gesperrt zeichnet (nicht erforscht oder ohne Werkbank). Dieselben
            // zwei Bedingungen wie dort. Der Knopf wird ueber BottomActionButton gedimmt, nie durchgestrichen.
            // Waffen-, Schild- und Pack-Zeilen ebenso (2026-08-25, zweite Fundstelle desselben
            // Musters): DrawWeaponEntry zeichnet den Haken nur bei craftable UND bench != null - eine erforschte
            // Waffe ohne passende Werkbank ist eine gesperrte Zeile und darf den Knopf nicht aktiv halten.
            bool Selectable(WeaponEntry e) => e.craftable && e.bench != null;
            bool anyProducible = mainEntries.Any(Selectable) || secEntries.Any(Selectable) || shieldEntries.Any(Selectable)
                || packEntries.Any(Selectable)
                || ammoEntries.Any(e => e.bench != null && LoadoutUtility.IsAmmoResearched(e.resolved))
                || obsoleteAmmoCandidates.Count > 0 || obsoleteWeaponCandidates.Count > 0;
            float xr = inRect.width;
            if (LoadoutUtility.BottomActionButton(inRect, ref xr, LoadoutUtility.PrimaryBtnW, produceLabel, deferred || anyProducible))
            {
                confirmed = true;
                // Bills first, THEN the deferred assignment: onConfirm may finish the Arm report, and the report
                // must already contain what GenerateAll recorded as actually queued.
                GenerateAll();
                if (onPruneObsoleteAmmo != null && obsoleteAmmoChecked.Count > 0)
                    onPruneObsoleteAmmo(obsoleteAmmoChecked.ToList());
                if (onPruneObsoleteWeapons != null && obsoleteWeaponChecked.Count > 0)
                    onPruneObsoleteWeapons(obsoleteWeaponChecked.ToList());
                onConfirm?.Invoke();
                Close();
            }

            // "Nichts herstellen" LINKS vom Herstellen-Knopf (2026-08-16): "Supply nothing" verhaelt sich
            // wie Cancel, ist aber sprechender.
            //
            // Er hat richtig vermutet, und nachgesehen ist es sogar exakt dasselbe: Close() ohne confirmed
            // laesst PostClose onCancel ausloesen, und onCancel heisst in dieser Kette bereits "Produktion
            // uebersprungen, Lauf sauber beendet" (Dialog_AssignLoadouts setzt dort ausdruecklich
            // armReport.cancelled = false und schreibt den Bericht fertig). Der Knopf ist also kein neues
            // Verhalten, sondern der fehlende NAME dafuer - "Cancel" liest sich wie "alles abbrechen",
            // obwohl nichts abgebrochen wird.
            // Cancel bleibt daneben stehen: wer es gewohnt ist, soll sich nicht umgewoehnen muessen.
            if (LoadoutUtility.BottomActionButton(inRect, ref xr, LoadoutUtility.PrimaryBtnW,
                    supplyNothingLabel, true,
                    "CER_SupplyLoadouts_SupplyNothingTip".Translate()))
                Close();
            Text.Font = prevBarFont;
        }

        // Mirrors the DoWindowContents draw order/gap logic exactly (leading 6f gap only between two sections that
        // BOTH actually drew something) so the scrollview's reserved height never drifts from what's really drawn,
        // even when diffOnly hides every row in one or more leading sections.
        private float ContentHeight()
        {
            float h = 4f;
            bool any = false;
            void Section(int count, float rowH, float extraHeaderH = 0f)
            {
                if (count == 0) return;
                if (any) h += 6f;
                h += HeaderH + extraHeaderH + count * rowH;
                any = true;
            }
            Section(Visible(mainEntries).Count, RowH);
            Section(Visible(secEntries).Count, RowH);
            Section(Visible(shieldEntries).Count, RowH);
            Section(Visible(packEntries).Count, RowH);
            Section(VisibleAmmo().Count, RowH);
            // Keine Erklaer-Zeile mehr unter der Ueberschrift (2026-09-02 spaet: Begruendung steckt in
            // der Ueberschrift selbst) - das extraHeaderH von 20 ist mit ihr entfallen.
            Section(obsoleteWeaponCandidates.Count + obsoleteAmmoCandidates.Count, 26f);   // one shared header
            return h;
        }

        private List<WeaponEntry> Visible(List<WeaponEntry> src) => diffOnly ? src.Where(e => !e.craftable || e.countStr != "0").ToList() : src;
        private List<AmmoEntry> VisibleAmmo() => diffOnly ? ammoEntries.Where(e => !e.alreadyInProduction).ToList() : ammoEntries;

        // Column header clicked: same mode toggles direction, a different mode resets to ascending.
        private void SetSort(SortMode mode)
        {
            if (currentSortMode == mode) sortDescending = !sortDescending;
            else { currentSortMode = mode; sortDescending = false; }
            ApplySort();
        }

        // One shared sort state governs all four lists, so the whole table reads as one consistently-ordered
        // surface rather than each section having its own independent order.
        private void ApplySort()
        {
            SortWeapons(mainEntries);
            SortWeapons(secEntries);
            SortWeapons(shieldEntries);
            SortWeapons(packEntries);
            SortAmmo(ammoEntries);
        }

        private void SortWeapons(List<WeaponEntry> list)
        {
            Comparison<WeaponEntry> cmp;
            if (currentSortMode == SortMode.Count) cmp = (a, b) => ParseCount(a.countStr).CompareTo(ParseCount(b.countStr));
            else if (currentSortMode == SortMode.Assigned) cmp = (a, b) => a.assignedNames.Count.CompareTo(b.assignedNames.Count);
            else cmp = (a, b) => string.Compare(a.weapon.LabelCap, b.weapon.LabelCap, StringComparison.OrdinalIgnoreCase);
            list.Sort(cmp);
            if (sortDescending) list.Reverse();
        }

        private void SortAmmo(List<AmmoEntry> list)
        {
            Comparison<AmmoEntry> cmp;
            if (currentSortMode == SortMode.Count) cmp = (a, b) => ParseCount(a.countStr).CompareTo(ParseCount(b.countStr));
            else if (currentSortMode == SortMode.Assigned) cmp = (a, b) => a.assignedNames.Count.CompareTo(b.assignedNames.Count);
            else cmp = (a, b) => string.Compare(a.resolved.LabelCap, b.resolved.LabelCap, StringComparison.OrdinalIgnoreCase);
            list.Sort(cmp);
            if (sortDescending) list.Reverse();
        }

        private static int ParseCount(string s) => int.TryParse(s, out int v) ? v : 0;

        private void DrawWeaponSection(Rect viewRect, ref float y, string title, List<WeaponEntry> entries)
        {
            List<WeaponEntry> shown = Visible(entries);
            if (shown.Count == 0) return;
            if (y > 0.5f) y += 6f;
            DrawSectionHeader(viewRect, ref y, title, null);
            foreach (WeaponEntry e in shown) DrawWeaponEntry(viewRect, ref y, e);
        }

        private void DrawAmmoSection(Rect viewRect, ref float y)
        {
            List<AmmoEntry> shown = VisibleAmmo();
            if (shown.Count == 0) return;
            if (y > 0.5f) y += 6f;
            DrawSectionHeader(viewRect, ref y, "CER_SupplyLoadouts_SectionAmmunition".Translate(), null);
            foreach (AmmoEntry e in shown) DrawAmmoEntry(viewRect, ref y, e);
        }

        private void DrawSectionHeader(Rect viewRect, ref float y, string label, string subtitle)
        {
            Text.Font = GameFont.Small;
            GUI.color = new Color(0.85f, 0.87f, 0.9f);
            Widgets.Label(new Rect(0, y, viewRect.width, 22f), "<b>" + label + "</b>");
            GUI.color = new Color(1f, 1f, 1f, 0.18f);
            Widgets.DrawLineHorizontal(0f, y + 22f, viewRect.width);
            GUI.color = Color.white;
            y += HeaderH;
            if (!string.IsNullOrEmpty(subtitle))
            {
                GUI.color = new Color(1f, 1f, 1f, 0.5f);
                Widgets.Label(new Rect(2f, y, viewRect.width - 4f, 20f), subtitle);
                GUI.color = Color.white;
                y += 20f;
            }
        }

        // Opt-out delete preview (2026-08-01, by design) - every candidate shows checked/grayed with a
        // trash icon by default; unchecking one keeps it (skips it on confirm). Strikethrough only on the
        // label itself, and only while still checked - same "this is going away" visual language every other
        // delete-preview in this popup uses, but it must disappear the instant the player opts an item out,
        // otherwise an unchecked (kept) row would still visually look like it's being deleted.
        // Woher kommt der Auftrag - von unserem Assistenten oder von Hand? (2026-08-14: optisch klar
        // machen, was der Mod angelegt hat und was manuell.)
        // Ohne diese Marke waere der einzige Unterschied zwischen den beiden Sorten der vorbelegte Haken,
        // und der ist genau das, was man beim Ueberfliegen nicht bemerkt. Die Farbe trennt zusaetzlich:
        // gedaempft fuer unsere eigenen (Routine), warm fuer die handgesetzten (hier bitte hinsehen).
        // Rechtsbuendig direkt links vom Lupensymbol, damit sie in beiden Zeilenarten an derselben Stelle
        // steht und nicht mit der Produktionsspalte um Platz streitet.
        private static void DrawObsoleteOriginTag(Rect row, float y, ThingDef d)
        {
            bool ours = LoadoutUtility.AllBillsForDefAreOurs(d);
            Rect tag = new Rect(row.xMax - 26f - 150f, y, 150f, 24f);
            GameFont prev = Text.Font;
            Text.Font = GameFont.Tiny;
            Text.Anchor = TextAnchor.MiddleRight;
            GUI.color = ours ? new Color(1f, 1f, 1f, 0.45f) : new Color(0.85f, 0.72f, 0.45f);
            Widgets.Label(tag, ours ? "CER_SupplyLoadouts_ObsoleteOriginMod".Translate() : "CER_SupplyLoadouts_ObsoleteOriginManual".Translate());
            GUI.color = Color.white;
            Text.Anchor = TextAnchor.UpperLeft;
            Text.Font = prev;
            TooltipHandler.TipRegion(tag, ours
                ? "CER_SupplyLoadouts_ObsoleteOriginModTip".Translate()
                : "CER_SupplyLoadouts_ObsoleteOriginManualTip".Translate());
        }

        private void DrawObsoleteAmmoRow(Rect viewRect, ref float y, ThingDef ammo)
        {
            Rect row = new Rect(0, y, viewRect.width, 24f);
            bool wasChecked = obsoleteAmmoChecked.Contains(ammo);
            bool isChecked = wasChecked;
            Widgets.Checkbox(new Vector2(0f, y + 2f), ref isChecked, 20f);
            if (isChecked != wasChecked)
            {
                if (isChecked) obsoleteAmmoChecked.Add(ammo); else obsoleteAmmoChecked.Remove(ammo);
            }
            GUI.color = isChecked ? new Color(0.9f, 0.55f, 0.5f, 0.85f) : new Color(1f, 1f, 1f, 0.5f);
            // Trash icon only while still checked/being deleted (2026-08-02: it stayed
            // visible even after opting a row out via unchecking, contradicting the row's own kept/strikethrough
            // state right next to it).
            if (isChecked) GUI.DrawTexture(new Rect(IconX, y + 3f, 16f, 16f), LoadoutUtility.TexTrash);
            Widgets.ThingIcon(new Rect(IconX + 22f, y + 1f, 20f, 20f), ammo);
            Text.Anchor = TextAnchor.MiddleLeft;
            string label = ammo.LabelCap.ToString();
            Rect nameRect = new Rect(NameX + 22f, y, 260f + colShift, 24f);   // gibt bei Schiebung Breite ab, siehe IconX
            // Real strikethrough, not the old <s></s> tag (2026-08-01: Unity's legacy rich
            // text has no strikethrough tag support at all, so it showed the raw "<s>Uzi</s>" characters
            // instead of a line through the text) - see LoadoutUtility.DrawStrikethroughLabel's own comment.
            if (isChecked) LoadoutUtility.DrawStrikethroughLabel(nameRect, label);
            else Widgets.Label(nameRect, label);
            GUI.color = Color.white;
            // What's actually running for nothing, and where (2026-08-01: only the item name
            // was shown, nothing to sanity-check the bulk-delete against before confirming).
            int prodTotal = LoadoutUtility.BuildProductionDetail(ammo, out List<string> prodDetail);
            if (prodTotal > 0)
            {
                GUI.color = new Color(0.78f, 0.68f, 0.48f);
                Widgets.Label(new Rect(nameRect.xMax + 8f, y, 300f, 24f), "CER_SupplyLoadouts_ObsoleteProdFmt".Translate(prodTotal.ToString()));
                GUI.color = Color.white;
            }
            Text.Anchor = TextAnchor.UpperLeft;
            // Jump-to-bench (2026-08-02) - same search-icon convention every other row's
            // bench cell already uses, so a player can go inspect/pause the running bill directly instead of
            // only reading about it in the tooltip.
            DrawObsoleteOriginTag(row, y, ammo);
            Building_WorkTable jumpBench = LoadoutUtility.FindActiveBillBench(ammo);
            if (jumpBench != null)
            {
                Rect obsJumpRect = new Rect(row.xMax - 22f, y + 3f, 18f, 18f);
                if (Widgets.ButtonImage(obsJumpRect, TexButton.Search))
                {
                    LoadoutUtility.JumpToBench(jumpBench);
                    Find.Selector.ClearSelection();
                    Find.Selector.Select(jumpBench);
                }
                TooltipHandler.TipRegion(obsJumpRect, "CER_SupplyLoadouts_JumpToBenchTip".Translate());
            }
            string prodTip = prodDetail.Count > 0 ? "\n\n" + "CER_SupplyLoadouts_InProductionTip".Translate(string.Join("\n  ", prodDetail.ToArray())).ToString() : "";
            TooltipHandler.TipRegion(row, (isChecked
                ? "CER_SupplyLoadouts_ObsoleteAmmoTip".Translate(label)
                : "CER_SupplyLoadouts_ObsoleteAmmoKeptTip".Translate(label)).ToString() + prodTip);
            y += 26f;
        }

        // Same opt-out delete preview as DrawObsoleteAmmoRow above, for a weapon-producing bill instead of an
        // ammo one (2026-08-01) - reuses the exact same Keyed strings (already worded generically, "bill"
        // not "ammo bill") rather than duplicating a near-identical set of translations.
        private void DrawObsoleteWeaponRow(Rect viewRect, ref float y, ThingDef weapon)
        {
            Rect row = new Rect(0, y, viewRect.width, 24f);
            bool wasChecked = obsoleteWeaponChecked.Contains(weapon);
            bool isChecked = wasChecked;
            Widgets.Checkbox(new Vector2(0f, y + 2f), ref isChecked, 20f);
            if (isChecked != wasChecked)
            {
                if (isChecked) obsoleteWeaponChecked.Add(weapon); else obsoleteWeaponChecked.Remove(weapon);
            }
            GUI.color = isChecked ? new Color(0.9f, 0.55f, 0.5f, 0.85f) : new Color(1f, 1f, 1f, 0.5f);
            if (isChecked) GUI.DrawTexture(new Rect(IconX, y + 3f, 16f, 16f), LoadoutUtility.TexTrash);
            Widgets.ThingIcon(new Rect(IconX + 22f, y + 1f, 20f, 20f), weapon);
            Text.Anchor = TextAnchor.MiddleLeft;
            string label = weapon.LabelCap.ToString();
            Rect nameRect = new Rect(NameX + 22f, y, 260f + colShift, 24f);   // gibt bei Schiebung Breite ab, siehe IconX
            if (isChecked) LoadoutUtility.DrawStrikethroughLabel(nameRect, label);
            else Widgets.Label(nameRect, label);
            GUI.color = Color.white;
            int prodTotal = LoadoutUtility.BuildProductionDetail(weapon, out List<string> prodDetail);
            if (prodTotal > 0)
            {
                GUI.color = new Color(0.78f, 0.68f, 0.48f);
                Widgets.Label(new Rect(nameRect.xMax + 8f, y, 300f, 24f), "CER_SupplyLoadouts_ObsoleteProdFmt".Translate(prodTotal.ToString()));
                GUI.color = Color.white;
            }
            Text.Anchor = TextAnchor.UpperLeft;
            DrawObsoleteOriginTag(row, y, weapon);
            Building_WorkTable jumpBench2 = LoadoutUtility.FindActiveBillBench(weapon);
            if (jumpBench2 != null)
            {
                Rect obsJumpRect2 = new Rect(row.xMax - 22f, y + 3f, 18f, 18f);
                if (Widgets.ButtonImage(obsJumpRect2, TexButton.Search))
                {
                    LoadoutUtility.JumpToBench(jumpBench2);
                    Find.Selector.ClearSelection();
                    Find.Selector.Select(jumpBench2);
                }
                TooltipHandler.TipRegion(obsJumpRect2, "CER_SupplyLoadouts_JumpToBenchTip".Translate());
            }
            string prodTip = prodDetail.Count > 0 ? "\n\n" + "CER_SupplyLoadouts_InProductionTip".Translate(string.Join("\n  ", prodDetail.ToArray())).ToString() : "";
            TooltipHandler.TipRegion(row, (isChecked
                ? "CER_SupplyLoadouts_ObsoleteAmmoTip".Translate(label)
                : "CER_SupplyLoadouts_ObsoleteAmmoKeptTip".Translate(label)).ToString() + prodTip);
            y += 26f;
        }

        // Two independent slim indicators, always both shown regardless of the scope radio (2026-08-02,
        // replaces the old single DrawUsedBySquare - see Request.assignedUsedBy's own comment for why one
        // dict-set whose meaning silently flipped between "real pawn" and "loadout template" had to go).
        // Diamond = assigned to a real colonist right now; circle = used by a managed Loadout template. The
        // names live in each icon's own mouseover, not in a fat text column.
        private static void DrawAssignmentIndicators(float y, List<string> assignedNames, List<string> templateNames, string assignedNoneTip, string templateNoneTip)
        {
            DrawIndicatorCell(UsedX, y, assignedNames, assignedNoneTip, diamond: true);
            DrawIndicatorCell(UsedX + 32f, y, templateNames, templateNoneTip, diamond: false);
        }

        // Rumpf lebt seit 2026-09-01 in LoadoutUtility.DrawUsedByIndicator (samt dem Weiss-statt-ColGood-
        // Kommentar von 2026-08-02) - der Packs-Reiter zeichnet seine Traeger-Raute ueber dieselbe Funktion.
        private static void DrawIndicatorCell(float x, float y, List<string> names, string noneTip, bool diamond)
            => LoadoutUtility.DrawUsedByIndicator(x, y, names, noneTip, diamond);

        // "+<yield>" stepper button next to the exact target count (2026-08-01 fix) -
        // replaces the old "type a batch number, it multiplies against yield" field, which needed a mental
        // multiplication step for something a click can just do directly: each click adds one batch's worth
        // straight onto Count. Count itself is still freely typable for an exact target that isn't a whole
        // multiple of yield.
        private static void DrawBatchCountPair(float y, int yield, ref string countStr)
        {
            Rect addRect = new Rect(BatchX, y, CountX - BatchX - 4f, 24f);
            Rect countRect = new Rect(CountX, y, 45f, 24f);
            if (LoadoutUtility.ButtonTextFit(addRect, "CER_SupplyLoadouts_BatchAddButton".Translate(yield.ToString()).ToString()))
            {
                int.TryParse(countStr, out int cur);
                countStr = Mathf.Max(0, cur + yield).ToString();
            }
            TooltipHandler.TipRegion(addRect, "CER_SupplyLoadouts_BatchAddTip".Translate(yield.ToString()));
            countStr = Widgets.TextField(countRect, countStr);
            TooltipHandler.TipRegion(countRect, "CER_SupplyLoadouts_ExactCountTip".Translate());
        }

        // Scroll-Kulisse (2026-08-14, Befund 5 der ersten Performance-Runde). Dieses Fenster war das
        // EINZIGE der drei Listenfenster ohne - Dialog_AssignLoadouts und Dialog_SquadSetup haben sie
        // laengst, mit derselben Form: eine Zeile ausserhalb des sichtbaren Ausschnitts schiebt nur den
        // Cursor weiter und zeichnet nichts. Das ist hier kein Schoenheitsfehler, sondern spuerbar: eine
        // Waffenzeile ruft GetWeaponScore, baut zwei Tooltips, zeichnet die Verfuegbarkeitsleiste und
        // fragt die Werkbaenke ab, eine Munitionszeile dasselbe - und das Fenster listet regelmaessig
        // mehr Zeilen, als hineinpassen.
        // Sicher, weil in diesen beiden Methoden NICHTS gesetzt wird, das nicht aus einem Klick kommt
        // (geprueft: nur `e.selected` aus der Kaestchen-Zeile und `e.stuff`/`e.bench` aus Menue-Rueckrufen).
        // Eine uebersprungene Zeile behaelt damit exakt ihren Zustand - das Bestaetigen unten liest alle
        // Eintraege, nicht nur die gezeichneten.
        private float visibleListH;

        private bool RowCulled(ref float y, float h)
        {
            if (y + h < scroll.y || y > scroll.y + visibleListH) { y += h; return true; }
            return false;
        }

        private void DrawWeaponEntry(Rect viewRect, ref float y, WeaponEntry e)
        {
            if (RowCulled(ref y, RowH)) return;
            GUI.color = e.craftable ? Color.white : Color.gray;
            Widgets.ThingIcon(new Rect(IconX, y + 2f, 20f, 20f), e.weapon);
            Rect nameRect = new Rect(NameX, y, SpecialX - NameX - 4f, 24f);
            LoadoutUtility.LabelCell(nameRect, e.weapon.LabelCap);
            GUI.color = Color.white;
            LoadoutUtility.GetWeaponScore(e.weapon, out _, out string wTip);
            TooltipHandler.TipRegion(new Rect(IconX, y, SpecialX - IconX, 24f), wTip);

            DrawAssignmentIndicators(y, e.assignedNames, e.templateNames,
                "CER_SupplyLoadouts_NotAssignedTip".Translate(), "CER_SupplyLoadouts_NotUsedInLoadoutTip".Translate());

            if (!e.craftable)
            {
                // EINEN Grund nennen, nicht zwei zur Auswahl (2026-08-13, im Test gegenuebergestellt:
                // die Munitions-Zeile sagt "Needs research: Advanced ammunition", die
                // Waffen-Zeile daneben sagte weiter "can't be crafted (no recipe / not researched)" - zwei
                // Moeglichkeiten in einem Satz, und man weiss hinterher nicht, welche zutrifft).
                // Die Unterscheidung ist billig und die Funktion dafuer gibt es laengst - die Munitionsseite
                // benutzt MissingResearchFor seit dem 09.08., die Waffenseite hatte es nie bekommen.
                // Kein Rezept ist der endgueltige Fall (Handelsware, Relikt); fehlende Forschung ist der
                // behebbare, und dann gehoert der Name des Projekts dazu.
                GUI.color = Color.gray;
                string missingResW = LoadoutUtility.GetRecipeFor(e.weapon) == null ? null : LoadoutUtility.MissingResearchFor(e.weapon);
                // Auf ReasonCellW begrenzt statt fester 400 (2026-08-19): 400 reichten von 546 bis 946 und
                // damit 114 Punkte WEIT in die Balkenspalte bei 832 hinein. Solange diese Zeile gar keinen
                // Balken hatte, ist das niemandem aufgefallen; seit sie einen hat, waere es eine
                // Ueberlappung gewesen - von mir im selben Durchgang eingebaut und beim Nachrechnen der
                // Spaltenpositionen aufgefallen. ReasonCellW ist ProdX - MaterialX - 8 und genau dafuer da;
                // der Zwilling darunter (keine Werkbank) benutzt es ueber DrawBlockedReasonCell laengst.
                Rect reasonRect = new Rect(MaterialX, y, ReasonCellW, 24f);
                if (!string.IsNullOrEmpty(missingResW))
                {
                    Widgets.Label(reasonRect, "CER_SupplyLoadouts_WeaponNeedsResearch".Translate(missingResW));
                    GUI.color = Color.white;
                    TooltipHandler.TipRegion(reasonRect, "CER_SupplyLoadouts_WeaponNeedsResearchTip".Translate(missingResW));
                }
                else
                {
                    Widgets.Label(reasonRect, "CER_SupplyLoadouts_ItemNoRecipe".Translate());
                    GUI.color = Color.white;
                    TooltipHandler.TipRegion(reasonRect, "CER_SupplyLoadouts_ItemNoRecipeTip".Translate());
                }
                // Auch dieser Ausstieg bekommt den Bestandsbalken (2026-08-19): dass sich etwas nicht
                // herstellen laesst, heisst nicht, dass nichts da ist - Kraeutermedizin waechst, Handelsware
                // liegt im Regal. Ohne Balken stand die Zeile ohne jede Zahl da, und der Spieler musste in
                // einen anderen Reiter wechseln, um zu sehen, ob ihm ueberhaupt etwas fehlt.
                // Dieselbe Luecke wie in den Munitions-Zweigen, per Suche vom Symptom aus gefunden
                // (2026-08-23): auch dieser Balken zeigte seine Zahlen ohne die
                // "wo liegt es / wer traegt es"-Aufstellung, die jede nicht blockierte Zeile hat.
                Rect noRecipeAvailRect = new Rect(ProdX, y + 1f, 155f, 22f);
                LoadoutUtility.DrawAvailabilityBar(noRecipeAvailRect, RowAvailability(e, 0), horizontal: true,
                    LoadoutUtility.TipWanted(noRecipeAvailRect) ? RowLocationTip(e) : null);
                y += RowH;
                return;
            }

            // Same treatment DrawAmmoEntry already gives its own "researched but no bench" case - a bare,
            // fully grayed row explaining WHY instead of checkbox/batches/count controls that would be
            // meaningless (nothing here can actually be queued). Used to leave Batches/Count fully live and
            // editable even with no bench at all, in a DIFFERENT column and wording than the ammo row's own
            // version of the exact same problem (2026-07-31: wanted the two unified - same
            // text, same column, non-interactive, visibly grayed).
            if (e.bench == null)
            {
                // Icon + tooltip (2026-08-02, second pass - the first fix just swapped in ANOTHER fixed string,
                // which still overflowed this cell on its own once the localized wording ran long; a fixed-
                // WIDTH cell can never safely hold ANY variable-length sentence, no matter how short it looks
                // in English). No text in the cell at all now - a small warning badge (can never overflow,
                // its size doesn't depend on any string) with the full "No <bench type>" detail moved entirely
                // into the tooltip, same design the ammo row below already used for the unresearched case.
                Rect noBenchIconR = BenchWarnRect(y);
                LoadoutUtility.DrawWarningBadge(noBenchIconR, LoadoutUtility.ColBad);
                TooltipHandler.TipRegion(new Rect(BenchX, y, 200f, 24f), "CER_SupplyLoadouts_NoBenchWithTypeLabel".Translate(LoadoutUtility.BenchTypeNames(e.weapon)));
                // Der Grund jetzt auch sichtbar in der Zeile - siehe DrawBlockedReasonCell.
                DrawBlockedReasonCell(y, "CER_SupplyLoadouts_NoBenchRowLabel".Translate(LoadoutUtility.BenchTypeNames(e.weapon)),
                    "CER_SupplyLoadouts_NoBenchWithTypeLabel".Translate(LoadoutUtility.BenchTypeNames(e.weapon)));

                // Derselbe Balken wie in einer Zeile MIT Werkbank (2026-08-19) - siehe RowAvailability.
                // Vorher stand hier eine eigene Rechnung aus GetWeaponInventory, die fuer einen Pack-Inhalt
                // null liefert: ohne Drug lab sagte die Medizin-Zeile Total 0, waehrend der
                // Equipment-Packs-Reiter im selben Moment 77 zeigte. Die Werkbank entscheidet, ob sich etwas
                // HERSTELLEN laesst - nicht, wieviel die Kolonie davon hat.
                Rect noBenchAvailRect = new Rect(ProdX, y + 1f, 155f, 22f);
                LoadoutUtility.DrawAvailabilityBar(noBenchAvailRect, RowAvailability(e, 0), horizontal: true,
                    LoadoutUtility.TipWanted(noBenchAvailRect) ? RowLocationTip(e) : null);

                y += RowH;
                return;
            }

            {
                bool sel = e.selected;
                Widgets.Checkbox(new Vector2(0f, y + 2f), ref sel, 20f);
                e.selected = sel;
            }

            Dialog_GenerateLoadouts.DrawWeaponSpecialIcons(SpecialX, y + 4f, e.weapon, UsedX - SpecialX - 6f);

            int orderCount = int.TryParse(e.countStr, out int oc) ? Mathf.Max(1, oc) : 1;
            // ing.count is a PER-BATCH cost, not per unit of the target count - passing the raw want here
            // overstated the shortage check by up to e.yield-times for a yield>1 recipe (almost always 1 for
            // weapons, but shared code paths route some pack items - e.g. yield>1 meds - through this same
            // WeaponEntry/DrawIngredientCell call, see isPackItem above). 2026-07-31 fix.
            int batches = e.yield > 0 ? Mathf.CeilToInt((float)orderCount / e.yield) : orderCount;
            if (e.ings.Count > 0 || e.stuffs.Count == 0)
                LoadoutUtility.DrawIngredientCell(IngX, 150f, y + 3f, e.ings, batches);

            if (e.stuffs.Count > 0)
            {
                // Button shows the material's icon before its name (and the menu options carry icons too).
                Rect matRect = new Rect(MaterialX, y, 120f, 24f);
                bool matClicked = Widgets.ButtonText(matRect, "");
                {
                    float mx = matRect.x + 6f;
                    if (e.stuff != null) { Widgets.ThingIcon(new Rect(mx, y + 2f, 20f, 20f), e.stuff); mx += 24f; }
                    Text.Anchor = TextAnchor.MiddleLeft;
                    Widgets.Label(new Rect(mx, matRect.y, matRect.xMax - mx - 4f, 24f),
                        (e.stuff != null ? e.stuff.LabelCap.ToString() : "CER_SupplyLoadouts_AnyMaterial".Translate().ToString()).Truncate(matRect.xMax - mx - 8f));
                    Text.Anchor = TextAnchor.UpperLeft;
                }
                if (matClicked)
                {
                    List<FloatMenuOption> opts = new List<FloatMenuOption>();
                    foreach (ThingDef s in e.stuffs)
                    {
                        ThingDef local = s;
                        // Re-derive the ingredient cell for the new material, so its icon + stock warning follow.
                        FloatMenuOption opt = new FloatMenuOption(local.LabelCap, () => { e.stuff = local; e.ings = LoadoutUtility.GetRecipeIngredients(e.recipe, local); }, local);
                        int needed = LoadoutUtility.GetRecipeIngredients(e.recipe, local).FirstOrDefault(i => i.thingDef == local)?.count ?? 0;
                        int have = LoadoutUtility.IngredientStockCount(local);
                        opt.mouseoverGuiAction = r => TooltipHandler.TipRegion(r, new TipSignal(
                            "CER_SupplyLoadouts_MaterialStockTip".Translate(needed.ToString(), have.ToString()).ToString(), local.GetHashCode() ^ 8511));
                        opts.Add(opt);
                    }
                    Find.WindowStack.Add(new FloatMenu(opts));
                }
            }
            else
            {
                GUI.color = Color.gray;
                Widgets.Label(new Rect(MaterialX, y + 2f, 120f, 24f), "CER_SupplyLoadouts_NoMaterialDash".Translate());
                GUI.color = Color.white;
            }

            DrawBatchCountPair(y, e.yield, ref e.countStr);

            // Die SUMME kommt aus der gemeinsamen Regel (LoadoutUtility.QueuedFromBills), nicht mehr aus dem
            // Aufaddieren der einzelnen Auftragszahlen: zwei "bis 50"-Auftraege machen zusammen nicht 100,
            // und was schon im Lager liegt, wird nicht noch einmal hergestellt. Diese Zahl faerbt den
            // Balken - genau da war die Zeile bisher zu optimistisch (2026-08-15, Review-Befund).
            // Die Aufstellung darunter nennt weiter, worauf JEDE Werkbank eingestellt ist; das ist eine
            // andere Frage als "wie viel kommt noch" und bleibt absichtlich, wie sie ist.
            List<Bill_Production> prodBills = new List<Bill_Production>();
            List<string> prodDetail = new List<string>();
            foreach (Building_WorkTable b in e.benches)
            {
                if (b.billStack == null) continue;
                foreach (Bill_Production bill in b.billStack.Bills.OfType<Bill_Production>())
                    if (bill.recipe == e.recipe)
                    {
                        prodBills.Add(bill);
                        int cnt = bill.repeatMode == BillRepeatModeDefOf.RepeatCount ? bill.repeatCount : bill.targetCount;
                        prodDetail.Add((e.benchLabels.TryGetValue(b, out string bl) ? bl : b.def.label) + " (x" + cnt + ")");
                    }
            }
            int inProd = LoadoutUtility.QueuedFromBills(prodBills, e.weapon);
            // Harmonized availability bar (2026-07-30) - replaces the old plain "N / M" text cell. No single
            // pawn here (this popup can cover many colonists at once), so the trailing shortfall segment
            // (e.unfulfilled) renders grey, not red.
            LoadoutUtility.AvailabilitySegments prodSeg = RowAvailability(e, inProd);
            // Where-is-it/who-has-it breakdown (2026-08-01: this bar's "Available" used to show
            // no location detail at all, unlike every sibling Availability bar in the mod) + the existing
            // in-production detail, joined the same "\n\n" way BuildAvailabilityTooltip itself sections things.
            // TipWanted: die Orts-Aufstellung laeuft ueber alle Karten und alle Kolonisten - siehe
            // LoadoutUtility.TipWanted. Das war die SIEBTE Fundstelle desselben Musters; sechs sind heute
            // abgesichert worden, diese eine ist durchgerutscht (nachgereicht).
            Rect prodRect = new Rect(ProdX, y + 1f, 155f, 22f);
            string combinedTip = null;
            if (LoadoutUtility.TipWanted(prodRect))
            {
                string invTip = RowLocationTip(e);
                string prodTip = prodDetail.Count > 0 ? "CER_SupplyLoadouts_InProductionTip".Translate(string.Join("\n  ", prodDetail.ToArray())).ToString() : null;
                combinedTip = string.Join("\n\n", new[] { invTip, prodTip }.Where(s => !string.IsNullOrEmpty(s)));
            }
            LoadoutUtility.DrawAvailabilityBar(prodRect, prodSeg, horizontal: true, string.IsNullOrEmpty(combinedTip) ? null : combinedTip);

            // e.bench == null already returned early above - this point is only reached with a real bench.
            {
                string bid = LoadoutUtility.BenchId(e.bench, e.benches);
                if (LoadoutUtility.BenchIconButton(new Rect(BenchX, y + 2f, BenchBtnW, BenchBtnH), e.bench, bid, e.bench.def.LabelCap))
                {
                    List<FloatMenuOption> opts = new List<FloatMenuOption>();
                    foreach (Building_WorkTable b in e.benches)
                    {
                        Building_WorkTable local = b;
                        string optLabel = e.benchLabels.TryGetValue(local, out string ol) ? ol : local.def.label;
                        opts.Add(new FloatMenuOption(optLabel, () => e.bench = local));
                    }
                    Find.WindowStack.Add(new FloatMenu(opts));
                }
                Rect jumpRect = new Rect(JumpX, y + 4f, 18f, 18f);
                if (Widgets.ButtonImage(jumpRect, TexButton.Search))
                {
                    LoadoutUtility.JumpToBench(e.bench);
                    Find.Selector.ClearSelection();
                    Find.Selector.Select(e.bench);
                }
                TooltipHandler.TipRegion(jumpRect, "CER_SupplyLoadouts_JumpToBenchTip".Translate());
                Rect infoRect = new Rect(InfoX, y + 4f, 18f, 18f);
                if (Widgets.ButtonImage(infoRect, TexButton.Info)) LoadoutUtility.OpenBillsTab(e.bench);
                TooltipHandler.TipRegion(infoRect, "CER_SupplyLoadouts_OpenBillsTip".Translate());
            }

            y += RowH;
        }

        private void DrawAmmoEntry(Rect viewRect, ref float y, AmmoEntry e)
        {
            if (RowCulled(ref y, RowH)) return;
            bool researched = LoadoutUtility.IsAmmoResearched(e.resolved);
            GUI.color = researched ? Color.white : Color.gray;
            Widgets.ThingIcon(new Rect(IconX, y + 2f, 20f, 20f), e.resolved);
            Rect nameRect = new Rect(NameX, y, SpecialX - NameX - 4f, 24f);
            string autoTag = e.sourceGeneric != null ? "CER_SupplyLoadouts_AutoTag".Translate().ToString() : "";
            LoadoutUtility.LabelCell(nameRect, e.resolved.label + autoTag);
            GUI.color = Color.white;
            TooltipHandler.TipRegion(new Rect(IconX, y, SpecialX - IconX, 24f), LoadoutUtility.GetAmmoRecipeTooltip(e.resolved)
                + (e.sourceGeneric != null ? "CER_SupplyLoadouts_AutoResolvedTip".Translate().ToString() : ""));

            DrawAssignmentIndicators(y + 2f, e.assignedNames, e.templateNames,
                "CER_SupplyLoadouts_NotAssignedAmmoTip".Translate(), "CER_SupplyLoadouts_NotUsedInLoadoutAmmoTip".Translate());

            // Same treatment DrawWeaponEntry gives an uncraftable weapon / no-bench weapon - a bare, fully
            // grayed row explaining WHY instead of checkbox/count/bench controls that would be meaningless
            // for something that can't be queued at all. Two distinct "will never work right now" reasons -
            // not researched, or researched but no suitable workbench built yet - both left the row fully
            // checked/selectable before (the checkbox default only ever considered research, never bench),
            // which silently promised a bill that Confirm's own GenerateAll would then just skip with no
            // explanation. The "no bench" case now shares the EXACT same text and column (BenchX, not
            // MaterialX) as the weapon row's own version of the identical problem (2026-07-31: the two
            // belong unified).
            bool hasBench = e.bench != null;
            if (!researched)
            {
                GUI.color = LoadoutUtility.ColWarn;
                // Nennt jetzt das fehlende Forschungsprojekt beim Namen (2026-08-09) - "nicht erforscht"
                // allein sagte nicht, WAS man erforschen muss, und genau daran ist der Neulings-Durchlauf
                // haengengeblieben. Faellt auf den alten Text zurueck, wenn sich kein Projekt ermitteln laesst.
                string missingRes = LoadoutUtility.MissingResearchFor(e.resolved);
                Rect notResRect = new Rect(MaterialX, y, 220f, 24f);
                LoadoutUtility.LabelCell(notResRect, string.IsNullOrEmpty(missingRes)
                    ? "CER_SupplyLoadouts_AmmoNotResearched".Translate().ToString()
                    : "CER_SupplyLoadouts_AmmoNeedsResearch".Translate(missingRes).ToString());
                if (!string.IsNullOrEmpty(missingRes))
                    TooltipHandler.TipRegion(notResRect, "CER_SupplyLoadouts_AmmoNeedsResearchTip".Translate(missingRes, LoadoutUtility.BenchTypeNames(e.resolved)));
                GUI.color = Color.white;

                // Can't make MORE, but whatever's already in storage still matters - a blocked row used to
                // show no stock info at all, reading as "there is definitely none" when the colony might
                // already own plenty (bought/looted, or crafted before the bench was lost/dismantled).
                // DIESELBE Rechnung wie die normale Zeile weiter unten (2026-08-16): solange keine passende
                // Werkbank stand, zeigte der Nachschub-Bildschirm in den Spalten Batches / Stock / Availability
                // GAR NICHTS an - der reine BEDARF haengt aber nicht an der Werkbank. Das war ein Denkfehler:
                // eine fehlende
                // Werkbank (oder Forschung) blockiert das HERSTELLEN, nicht das Wissen. Ausgerechnet in
                // dieser Lage will man am dringendsten wissen, WIE VIEL fehlt - das ist ja der Grund, die
                // Werkbank ueberhaupt zu bauen. Vorher stand hier nur ein Lagerbestand ohne jeden Bedarf,
                // der Balken war also immer "voll" und sagte das Gegenteil dessen, was los war.
                // ComputeAmmoAvailability kennt keine Werkbank - dieselbe Quelle, aus der jeder andere
                // Balken des Mods seine Zahlen zieht. Beide blockierten Zweige (keine Forschung / keine
                // Werkbank) bekommen es, nicht nur der zuerst gesehene.
                // MIT der Orts-Aufstellung (2026-08-23): der Balken hatte hier
                // dieselben Zahlen wie im Auftrags-Reiter, aber sein Mouseover liess "Getragen von:" und
                // "Zugewiesen an:" weg - zwei Bildschirme, dieselbe Zeile, unterschiedlich viel Auskunft.
                // Die Werkbank entscheidet, ob sich etwas HERSTELLEN laesst; wer die Munition gerade am
                // Guertel hat, hat damit nichts zu tun. TipWanted wie ueberall, die Aufstellung laeuft
                // ueber alle Karten und wird sonst pro Bild umsonst gebaut.
                Rect availRect = new Rect(ProdX, y + 1f, 155f, 22f);
                LoadoutUtility.DrawAvailabilityBar(availRect, AmmoRowAvailability(e.resolved), horizontal: true,
                    LoadoutUtility.TipWanted(availRect) ? LoadoutUtility.BuildAmmoLocationTooltip(e.resolved) : null);

                y += RowH;
                return;
            }
            if (!hasBench)
            {
                // Icon + tooltip, no inline text - see DrawWeaponEntry's identical fix above.
                // ROT, nicht orange (2026-08-12): "keine passende Werkbank vorhanden" ist in
                // dieser Spalte kein Hinweis, sondern ein harter Riegel - es kann kein Auftrag entstehen.
                // Das deckt sich mit der Farbregel, die fuer dauerhafte Gegenstaende schon gilt: unmoeglich
                // ist rot, knapp ist orange.
                LoadoutUtility.DrawWarningBadge(BenchWarnRect(y), LoadoutUtility.ColBad);
                TooltipHandler.TipRegion(new Rect(BenchX, y, 200f, 24f), "CER_SupplyLoadouts_NoBenchWithTypeLabel".Translate(LoadoutUtility.BenchTypeNames(e.resolved)));
                // Der Grund jetzt auch sichtbar in der Zeile - siehe DrawBlockedReasonCell.
                DrawBlockedReasonCell(y, "CER_SupplyLoadouts_NoBenchRowLabel".Translate(LoadoutUtility.BenchTypeNames(e.resolved)),
                    "CER_SupplyLoadouts_NoBenchWithTypeLabel".Translate(LoadoutUtility.BenchTypeNames(e.resolved)));

                // DIESELBE Rechnung wie die normale Zeile weiter unten (2026-08-16): solange keine passende
                // Werkbank stand, zeigte der Nachschub-Bildschirm in den Spalten Batches / Stock / Availability
                // GAR NICHTS an - der reine BEDARF haengt aber nicht an der Werkbank. Das war ein Denkfehler:
                // eine fehlende
                // Werkbank (oder Forschung) blockiert das HERSTELLEN, nicht das Wissen. Ausgerechnet in
                // dieser Lage will man am dringendsten wissen, WIE VIEL fehlt - das ist ja der Grund, die
                // Werkbank ueberhaupt zu bauen. Vorher stand hier nur ein Lagerbestand ohne jeden Bedarf,
                // der Balken war also immer "voll" und sagte das Gegenteil dessen, was los war.
                // ComputeAmmoAvailability kennt keine Werkbank - dieselbe Quelle, aus der jeder andere
                // Balken des Mods seine Zahlen zieht. Beide blockierten Zweige (keine Forschung / keine
                // Werkbank) bekommen es, nicht nur der zuerst gesehene.
                // MIT der Orts-Aufstellung (2026-08-23): der Balken hatte hier
                // dieselben Zahlen wie im Auftrags-Reiter, aber sein Mouseover liess "Getragen von:" und
                // "Zugewiesen an:" weg - zwei Bildschirme, dieselbe Zeile, unterschiedlich viel Auskunft.
                // Die Werkbank entscheidet, ob sich etwas HERSTELLEN laesst; wer die Munition gerade am
                // Guertel hat, hat damit nichts zu tun. TipWanted wie ueberall, die Aufstellung laeuft
                // ueber alle Karten und wird sonst pro Bild umsonst gebaut.
                Rect availRect = new Rect(ProdX, y + 1f, 155f, 22f);
                LoadoutUtility.DrawAvailabilityBar(availRect, AmmoRowAvailability(e.resolved), horizontal: true,
                    LoadoutUtility.TipWanted(availRect) ? LoadoutUtility.BuildAmmoLocationTooltip(e.resolved) : null);

                y += RowH;
                return;
            }

            bool sel = e.selected;
            Widgets.Checkbox(new Vector2(0f, y + 2f), ref sel, 20f);
            e.selected = sel;

            SpecialAbility ammoEff = LoadoutUtility.GetAmmoEffects(e.resolved);
            if (ammoEff != SpecialAbility.None) Dialog_GenerateLoadouts.DrawAmmoEffectIcons(SpecialX, y + 3f, ammoEff, e.resolved, UsedX - SpecialX - 6f);

            // ing.count is a PER-BATCH cost - passing the raw target count as mult (or defaulting to 1, as
            // this used to) overstates/understates the shortage check by up to e.yield-times for any ammo
            // recipe yielding more than 1 per craft (most of them). Same fix as DrawWeaponEntry's own
            // orderCount/batches split below (2026-07-31).
            int ammoOrderCount = int.TryParse(e.countStr, out int aoc) ? Mathf.Max(1, aoc) : 1;
            int ammoBatches = e.yield > 0 ? Mathf.CeilToInt((float)ammoOrderCount / e.yield) : ammoOrderCount;
            LoadoutUtility.DrawIngredientCell(IngX, 150f, y + 3f, e.ings, ammoBatches);

            // Ammo has no Stuff, so its Material cell holds the variant switch instead (now for every row, no longer
            // hidden behind the old used-by text).
            Rect matRect = new Rect(MaterialX, y + 2f, 120f, 24f);
            if (e.alternatives.Count > 0)
            {
                if (Widgets.ButtonText(matRect, "CER_SupplyLoadouts_ChangeButton".Translate().ToString().Truncate(116f)))
                {
                    List<FloatMenuOption> opts = new List<FloatMenuOption>();
                    foreach (ThingDef alt in e.alternatives)
                    {
                        ThingDef local = alt;
                        opts.Add(new FloatMenuOption(local.label, () => SwitchAmmoChoice(e, local)));
                    }
                    Find.WindowStack.Add(new FloatMenu(opts));
                }
                TooltipHandler.TipRegion(matRect, "CER_SupplyLoadouts_SwitchVariantTip".Translate());
            }
            else
            {
                // "Material" only ever means "switch variant" for an ammo row (there's no Stuff concept for
                // ammo - the ingredients cell just left of this already shows what actually gets consumed,
                // e.g. Wood for a stone arrow) - a bare dash with no explanation read as if the row had no
                // material at all, when it just means no OTHER researched caliber/round exists to switch to.
                GUI.color = Color.gray;
                Rect noAltRect = new Rect(MaterialX, y + 4f, 120f, 24f);
                Widgets.Label(noAltRect, "CER_SupplyLoadouts_NoAlternativeDash".Translate());
                GUI.color = Color.white;
                TooltipHandler.TipRegion(noAltRect, "CER_SupplyLoadouts_NoAlternativeTip".Translate());
            }

            if (e.selected)
                DrawBatchCountPair(y, e.yield, ref e.countStr);

            // How much is already sitting in storage right now - the actual question "do I even need to
            // produce this" answers first, before whether a bill happens to be running too. This bar's own
            // segments have no per-pawn "carried/assigned" breakdown (unlike a discrete weapon) - reserved-
            // by-storage comes from ComputeAvailability's own demand figure, actual production from the same
            // WeaponQueuedCount every other bar uses (despite the name, it counts any produced ThingDef). The
            // tooltip's where-is-it breakdown (BuildAmmoLocationTooltip, 2026-08-01) is independent of that
            // and works fine regardless.
            int ammoInProd = LoadoutUtility.WeaponQueuedCount(e.resolved);
            // Clamped to actual stock (2026-08-01: "Assigned, in storage" showed 40 in blue
            // while the colony genuinely owned zero) - demandedFromStorage is ComputeAmmoAvailability's own
            // RAW, unclamped shortfall figure ("how much every assigned pawn's slot wants minus what they
            // already carry"), which can exceed actual stock by design (that's what a shortfall IS). Every
            // other BuildAvailabilitySegments caller clamps via Min(stock, demand) first; this one skipped
            // it, so requesting more than existed silently painted the missing rounds as if they were
            // sitting in storage. neededCount now engages the bar's own red "missing" segment for any real
            // shortfall, matching this same file's WEAPON row (a few lines up) which already does this.
            var ammoSeg = AmmoRowAvailability(e.resolved);
            Rect prodRect = new Rect(ProdX, y + 1f, 155f, 22f);
            // TipWanted: der Orts-Tooltip laeuft ueber alle Karten - nur bauen, wenn er auch gezeigt wird.
            LoadoutUtility.DrawAvailabilityBar(prodRect, ammoSeg, horizontal: true,
                LoadoutUtility.TipWanted(prodRect) ? LoadoutUtility.BuildAmmoLocationTooltip(e.resolved) : null);

            if (e.bench != null)
            {
                // Same compact icon button (+ "#id" for duplicates) and jump/info pair as the weapon rows above -
                // one visual language for "which bench" everywhere.
                string bid = LoadoutUtility.BenchId(e.bench, e.benches);
                if (LoadoutUtility.BenchIconButton(new Rect(BenchX, y + 2f, BenchBtnW, BenchBtnH), e.bench, bid, e.bench.def.LabelCap))
                {
                    Dictionary<Building_WorkTable, string> benchLabels = LoadoutUtility.BuildBenchLabels(e.benches);
                    List<FloatMenuOption> opts = new List<FloatMenuOption>();
                    foreach (Building_WorkTable b in e.benches)
                    {
                        Building_WorkTable local = b;
                        opts.Add(new FloatMenuOption(benchLabels.TryGetValue(local, out string ol) ? ol : local.def.label, () => e.bench = local));
                    }
                    Find.WindowStack.Add(new FloatMenu(opts));
                }
                Rect jumpRect = new Rect(JumpX, y + 4f, 18f, 18f);
                if (Widgets.ButtonImage(jumpRect, TexButton.Search))
                {
                    LoadoutUtility.JumpToBench(e.bench);
                    Find.Selector.ClearSelection();
                    Find.Selector.Select(e.bench);
                }
                TooltipHandler.TipRegion(jumpRect, "CER_SupplyLoadouts_JumpToBenchTip".Translate());
                Rect infoRect = new Rect(InfoX, y + 4f, 18f, 18f);
                if (Widgets.ButtonImage(infoRect, TexButton.Info)) LoadoutUtility.OpenBillsTab(e.bench);
                TooltipHandler.TipRegion(infoRect, "CER_SupplyLoadouts_OpenBillsTip".Translate());
            }
            else
            {
                // Icon + tooltip, no inline text - see DrawWeaponEntry's identical fix above.
                // ROT, nicht orange (2026-08-12): "keine passende Werkbank vorhanden" ist in
                // dieser Spalte kein Hinweis, sondern ein harter Riegel - es kann kein Auftrag entstehen.
                // Das deckt sich mit der Farbregel, die fuer dauerhafte Gegenstaende schon gilt: unmoeglich
                // ist rot, knapp ist orange.
                LoadoutUtility.DrawWarningBadge(BenchWarnRect(y), LoadoutUtility.ColBad);
                TooltipHandler.TipRegion(new Rect(BenchX, y, 200f, 24f), "CER_SupplyLoadouts_NoBenchWithTypeLabel".Translate(LoadoutUtility.BenchTypeNames(e.resolved)));
            }

            y += RowH;
        }

        // Sum of every ingredient the current selection would consume (weapons and ammo alike, batches included) -
        // mirrors GenerateAll's batch math exactly so the strip never promises a different bill than gets queued.
        private Dictionary<ThingDef, int> ComputeTotalCost()
        {
            var costs = new Dictionary<ThingDef, int>();
            void Add(List<ThingDefCountClass> ings, int batches)
            {
                if (ings == null || batches <= 0) return;
                foreach (ThingDefCountClass ing in ings)
                    if (ing != null && ing.thingDef != null)
                        costs[ing.thingDef] = (costs.TryGetValue(ing.thingDef, out int c) ? c : 0) + ing.count * batches;
            }
            // Die Bedingungen hier muessen ZEICHENGENAU dieselben sein wie in GenerateAll - das war die
            // Ursache eines alten "Total cost"-Falls (2026-08-09 endlich auf die Ursache zurueckgefuehrt,
            // nach langer Ratlosigkeit).
            // Es fehlte die Werkbank-Pruefung: die Kostenleiste zaehlte jede erforschte Zeile mit, GenerateAll
            // ueberspringt aber jede ohne gebaute (oder inzwischen abgerissene) Bank. Eine erforschte Munition
            // ohne passende Bank ging also in die angezeigte Summe ein und wurde danach still uebersprungen -
            // die Vorschau versprach Material, das nie verbraucht wurde. Betraf beide Seiten, Waffen wie
            // Munition, weil GenerateAll auf beiden e.bench prueft und diese Rechnung auf keiner.
            // Wenn hier je wieder eine Bedingung dazukommt, muss sie an beiden Stellen dazukommen.
            foreach (WeaponEntry e in mainEntries.Concat(secEntries).Concat(shieldEntries).Concat(packEntries))
            {
                if (!e.selected || !e.craftable || e.recipe == null) continue;
                if (e.bench == null || e.bench.Destroyed) continue;
                if (!int.TryParse(e.countStr, out int want) || want <= 0) continue;
                int yield = e.recipe.products != null && e.recipe.products.Count > 0 ? Mathf.Max(1, e.recipe.products[0].count) : 1;
                Add(e.ings, Mathf.CeilToInt((float)want / yield));
            }
            foreach (AmmoEntry e in ammoEntries)
            {
                if (!e.selected || e.recipe == null) continue;
                if (e.bench == null || e.bench.Destroyed) continue;
                if (!int.TryParse(e.countStr, out int want) || want <= 0) continue;
                int yield = LoadoutUtility.GetAmmoRecipeYield(e.resolved);
                Add(e.ings, yield > 0 ? Mathf.CeilToInt((float)want / yield) : 0);
            }
            return costs;
        }

        private void GenerateAll()
        {
            int weaponsAdded = 0;
            int ammoAdded = 0, ammoUpdated = 0;
            HashSet<Building_WorkTable> refresh = new HashSet<Building_WorkTable>();

            foreach (WeaponEntry e in mainEntries.Concat(secEntries).Concat(shieldEntries).Concat(packEntries))
            {
                if (!e.selected || !e.craftable || e.bench == null || e.bench.Destroyed) continue;
                if (!int.TryParse(e.countStr, out int want) || want <= 0) continue;
                int yield = e.recipe.products != null && e.recipe.products.Count > 0 ? e.recipe.products[0].count : 1;
                if (yield <= 0) yield = 1;
                int batches = Mathf.CeilToInt((float)want / yield);
                LoadoutUtility.AddWeaponBillOnce(e.bench, e.recipe, batches, e.stuff);
                weaponsAdded++;
                refresh.Add(e.bench);
                // Arm report: record what was ACTUALLY queued (after the player's deselections/count edits).
                // packEntries (isPackItem == true - e.g. a pack-sourced utility weapon like a smoke/flashbang
                // grenade, or since 2026-07-31 a pack's medicine/food/drug items too, see Dialog_AssignLoadouts.
                // OpenProducePopup) folds into this SAME weaponsQueued bucket - no separate "pack items queued"
                // bucket exists. Safe: every current reader of weaponsQueued (Dialog_ArmColony's report:
                // BuildMaterials via GetRecipeFor, and the generic "Production queued" row list) treats it as an
                // undifferentiated def->count bag already - nothing downstream assumes every key IsWeapon.
                if (armReport != null)
                    armReport.weaponsQueued[e.weapon] = (armReport.weaponsQueued.TryGetValue(e.weapon, out int prev) ? prev : 0) + want;
            }

            foreach (AmmoEntry e in ammoEntries)
            {
                if (!e.selected || e.recipe == null || e.bench == null || e.bench.Destroyed) continue;
                if (!int.TryParse(e.countStr, out int want) || want <= 0) continue;
                // The bill's target is passed AS TYPED, not rounded up to a whole batch (2026-07-30) - CE's
                // own TargetCount mode ("craft until stock >= X") already handles any X fine, exactly like a
                // vanilla "craft simple meals until 50 in stock" bill isn't bound to the cooking yield per
                // batch either. Rounding here used to silently replace the player's real target (e.g. typing
                // 50 with a 200-yield recipe actually queued a bill targeting 200) - Count is the authoritative
                // value now; Batches (in the UI) is only ever a quick-fill helper for typing it.
                // Arm-my-Colony's own ammo-bills step (ArmAutoGenerateMissing) runs BEFORE Assign, scoped to
                // ammo already-assigned pawns need - on a brand-new colony that's legitimately empty (nobody
                // is assigned yet), so it reports "Ammo bills created: 0" by design, not by bug. The REAL need
                // then surfaces here, in the Assign step's own supply popup, moments later in the SAME run -
                // but this popup's GenerateAll only ever recorded the queued AMOUNT into armReport.ammoQueued,
                // never into armReport.ammoBillsCreated (the report's own headline number), so a fresh colony
                // showed a flatly wrong "Ammo bills created: 0 - nothing was missing" even while the very same
                // report's "Production queued" section, a few lines down, listed real ammo bills that this
                // exact popup had just created (2026-07-31: it looked as if no ammo bill could be made at
                // all - traced to this reporting gap, not a functional one;
                // the bills themselves were always created correctly). Counting Added (not Updated) here
                // matches ArmAutoGenerateMissing's own "created" semantics exactly.
                switch (LoadoutUtility.AddOrUpdateAmmoBill(e.bench, e.recipe, want, onlyIncrease: false))
                {
                    case LoadoutUtility.BillChange.Added: ammoAdded++; refresh.Add(e.bench); if (armReport != null) armReport.ammoBillsCreated++; break;
                    case LoadoutUtility.BillChange.Updated: ammoUpdated++; refresh.Add(e.bench); break;
                }
                if (armReport != null)
                    armReport.ammoQueued[e.resolved] = (armReport.ammoQueued.TryGetValue(e.resolved, out int prev) ? prev : 0) + want;
            }

            // Was gar nicht in einen Auftrag gehen KONNTE (keine passende Werkbank, kein Rezept, nicht
            // erforscht) - getrennt von "der Spieler hat es abgewaehlt". Der Arm-my-Colony-Bericht kannte
            // bisher nur zwei Faelle, abgebrochen und "- nichts noetig -", und behauptete deshalb in einer
            // Kolonie ohne jede Werkbank "nichts noetig", waehrend genau der Schritt davor 3 Pavise und
            // 28 Mahlzeiten als fehlend auflistete (2026-08-28, im Selbsttest gefunden).
            if (armReport != null)
                armReport.prodBlocked =
                    mainEntries.Concat(secEntries).Concat(shieldEntries).Concat(packEntries)
                        .Count(e => !e.craftable || e.bench == null || e.bench.Destroyed)
                    + ammoEntries.Count(e => e.recipe == null || e.bench == null || e.bench.Destroyed);

            foreach (Building_WorkTable b in refresh) LoadoutUtility.RefreshBillsTab(b);
            // The real central "something just got queued" moment for EVERY path that reaches this popup
            // (Arm my Colony, manual Assign, Squad Setup's own supply preview, the end-of-session missing-
            // production check) - not just the Arm-my-Colony chain, which is all Dialog_ArmReport's own
            // Invalidate() call covered before (2026-08-01 follow-up fix - a review
            // found the readiness badge still went stale after confirming THIS popup directly, outside the
            // full Arm-my-Colony run).
            CombatReadiness.Invalidate();

            List<string> parts = new List<string>();
            if (weaponsAdded > 0) parts.Add("CER_SupplyLoadouts_WeaponBillsAddedPart".Translate(weaponsAdded.ToString()).ToString());
            if (ammoAdded > 0) parts.Add("CER_SupplyLoadouts_AmmoBillsAddedPart".Translate(ammoAdded.ToString()).ToString());
            if (ammoUpdated > 0) parts.Add("CER_SupplyLoadouts_AmmoBillsUpdatedPart".Translate(ammoUpdated.ToString()).ToString());
            Messages.Message(parts.Count > 0 ? "CER_SupplyLoadouts_SupplySummary".Translate(string.Join(", ", parts.ToArray())).ToString() : "CER_SupplyLoadouts_NothingSelected".Translate().ToString(),
                parts.Count > 0 ? MessageTypeDefOf.TaskCompletion : MessageTypeDefOf.RejectInput, false);
        }
    }
}
