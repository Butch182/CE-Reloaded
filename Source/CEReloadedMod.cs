using UnityEngine;
using Verse;
using System.Linq;
using System.Collections.Generic;
using RimWorld;
using CombatExtended;

namespace CEReloaded
{
    public class CEReloadedMod : Mod
    {
        public static CEReloadedSettings settings;

        private enum SettingsTab { Filters, Loadouts, Roles, AmmoBills, AutoAssign, Presets, Alerts, Tools, Debug }
        private SettingsTab settingsTab = SettingsTab.Filters;
        private Vector2 rolesScroll = Vector2.zero;
        private Vector2 debugScroll = Vector2.zero;
        private Vector2 presetGridScroll = Vector2.zero;
        // Senkrechte Bildlaufleiste fuer die Reiter, die ihren Inhalt allein mit dem Listing aufbauen
        // (2026-08-19). Die gemessene Hoehe kommt aus dem VORIGEN Durchgang - Listing_Standard rechnet erst
        // beim Zeichnen, eine Vorabrechnung waere eine zweite Wahrheit neben derselben Layoutkette.
        private Vector2 tabScroll = Vector2.zero;
        private SettingsTab tabScrollFor = SettingsTab.Filters;
        private readonly Dictionary<SettingsTab, float> tabContentHeight = new Dictionary<SettingsTab, float>();
        // Edit buffers for the preset grid's numeric fields, keyed "<preset>.<row>".
        private readonly Dictionary<string, string> presetBufs = new Dictionary<string, string>();

        public CEReloadedMod(ModContentPack content) : base(content)
        {
            settings = GetSettings<CEReloadedSettings>();
        }

        // Most settings are read live, but the filter/sort/toggle DEFAULTS seed the per-save UI state only when a
        // colony is first played - editing them mid-game used to do nothing for the running save, which reads as
        // "the setting is broken". Snapshot them when the settings window opens; on close, push exactly the values
        // the player CHANGED into the current save (untouched ones never stomp in-save tweaks).
        private class SeedSnapshot
        {
            public AmmoFilterMode af;
            public bool detailed;
            public AmmoBillFilterMode ab;
            public AssignLoadoutFilter alf;
            public RoleAssignMode roles;
            public string mealName, mealCount, medName, medCount, bulk, batches;
        }
        private SeedSnapshot seedSnapshot;

        private SeedSnapshot CaptureSeeds() => new SeedSnapshot
        {
            af = settings.defAmmoFilter,
            detailed = settings.startTabsDetailed,
            ab = settings.defAmmoBillFilter, alf = settings.autoAssignLoadoutFilter,
            roles = settings.autoAssignRoleMode,
            mealName = settings.defMealDefName, mealCount = settings.defMealCount,
            medName = settings.defMedicineDefName, medCount = settings.defMedicineCount,
            bulk = settings.sidearmMaxBulk, batches = settings.defAmmoBatches
        };

        public override void WriteSettings()
        {
            base.WriteSettings();
            // Waffen-Scores haengen ueber die Traglast an den Munitionsmengen hier drin - ohne Leeren
            // behaelt jede schon bewertete Waffe ihren alten Wert bis zum Spielneustart (2026-08-09).
            LoadoutUtility.ClearWeaponScoreCache();
            ApplyChangedSeedsToCurrentGame();
            seedSnapshot = null;
        }

        private void ApplyChangedSeedsToCurrentGame()
        {
            if (seedSnapshot == null || Current.Game == null || Current.ProgramState != ProgramState.Playing) return;
            CERUiState ui = RoleUtility.Ui();
            if (ui == null) return;
            SeedSnapshot o = seedSnapshot;
            int n = 0;
            if (o.af != settings.defAmmoFilter) { ui.loAmmoFilter = settings.defAmmoFilter; n++; }
            if (o.ab != settings.defAmmoBillFilter) { ui.abFilter = settings.defAmmoBillFilter; n++; }
            if (o.alf != settings.autoAssignLoadoutFilter) { ui.asLoadoutFilter = settings.autoAssignLoadoutFilter; n++; }
            if (o.roles != settings.autoAssignRoleMode) { ui.asRoleMode = settings.autoAssignRoleMode; n++; }
            if (o.detailed != settings.startTabsDetailed)
            {
                ui.abDetailed = settings.startTabsDetailed;
                ui.asDetailed = settings.startTabsDetailed;
                n++;
            }
            // Mahlzeit/Medizin brauchen diesen Weg seit 2026-08-14 nicht mehr: sie werden nicht mehr in den
            // Spielstand-Zustand gesaet, sondern beim Erzeugen eines Equipment Packs direkt aus den
            // Einstellungen gelesen (Dialog_GenerateEquipmentPack). Eine Aenderung wirkt damit sofort auf
            // den naechsten erzeugten Pack, ohne Umweg - und die vier Zeilen hier wuerden nur noch Felder
            // fuellen, die niemand liest. Die Zaehlung `n` (die Meldung "N geaenderte Vorgaben uebernommen")
            // ist deshalb ebenfalls um vier Faelle kleiner, und das ist richtig so.
            if (o.bulk != settings.sidearmMaxBulk) { ui.loSidearmMaxBulk = settings.sidearmMaxBulk; n++; }
            if (o.batches != settings.defAmmoBatches) { ui.abDefaultBatches = settings.defAmmoBatches; n++; }
            if (n > 0)
                Messages.Message(n == 1
                    ? "CER_ModSettings_AppliedChangedDefaultsSingular".Translate(n.ToString())
                    : "CER_ModSettings_AppliedChangedDefaultsPlural".Translate(n.ToString()),
                    MessageTypeDefOf.TaskCompletion, false);
        }

        public override string SettingsCategory()
        {
            return "CE Reloaded";
        }

        public override void DoSettingsWindowContents(Rect inRect)
        {
            if (seedSnapshot == null) seedSnapshot = CaptureSeeds();
            // --- Tab row ---
            float tabW = inRect.width / 9f;
            float tabH = 30f;
            DrawTab(new Rect(inRect.x + tabW * 0, inRect.y, tabW, tabH), "CER_ModSettings_TabFilters".Translate(), SettingsTab.Filters);
            DrawTab(new Rect(inRect.x + tabW * 1, inRect.y, tabW, tabH), "CER_ModSettings_TabLoadouts".Translate(), SettingsTab.Loadouts);
            DrawTab(new Rect(inRect.x + tabW * 2, inRect.y, tabW, tabH), "CER_ModSettings_TabRoles".Translate(), SettingsTab.Roles);
            DrawTab(new Rect(inRect.x + tabW * 3, inRect.y, tabW, tabH), "CER_ModSettings_TabAmmoBills".Translate(), SettingsTab.AmmoBills);
            DrawTab(new Rect(inRect.x + tabW * 4, inRect.y, tabW, tabH), "CER_ModSettings_TabAutoAssign".Translate(), SettingsTab.AutoAssign);
            DrawTab(new Rect(inRect.x + tabW * 5, inRect.y, tabW, tabH), "CER_ModSettings_TabPresets".Translate(), SettingsTab.Presets);
            DrawTab(new Rect(inRect.x + tabW * 6, inRect.y, tabW, tabH), "CER_ModSettings_TabAlerts".Translate(), SettingsTab.Alerts);
            DrawTab(new Rect(inRect.x + tabW * 7, inRect.y, tabW, tabH), "CER_ModSettings_TabTools".Translate(), SettingsTab.Tools);
            DrawTab(new Rect(inRect.x + tabW * 8, inRect.y, tabW, tabH), "CER_ModSettings_TabDebug".Translate(), SettingsTab.Debug);

            Rect body = new Rect(inRect.x, inRect.y + tabH + 10f, inRect.width, inRect.height - tabH - 10f);

            // Was unter die Fensterkante rutscht, war bisher WEG (2026-08-19, am Reiter "Presets"
            // aufgefallen: Ueberschrift und Erklaertext von "Arm my Colony mode presets" standen da,
            // die Tabelle mit den sechs Schaltern und ihr Zuruecksetzen-Knopf darunter gar nicht - "auch
            // nicht nach Scrollversuchen", weil es nichts zu scrollen gab).
            // Ursache ist die Groessenkette, nicht dieser eine Reiter: Listing_Standard schneidet
            // nicht ab und meldet auch nichts, es legt einfach weiter Rechtecke unterhalb des sichtbaren
            // Bereichs an. Das Fenster selbst ist gedeckelt (Patch_ModSettingsSize, hoechstens Bildhoehe-40),
            // also kann JEDER Inhalt, der laenger wird als der Rest des Fensters, still verschwinden - beim
            // Presets-Reiter etwa 740 Punkte Inhalt gegen 664 Punkte Platz bei 820 Fensterhoehe.
            // Deshalb hier fuer alle Reiter, nicht dort fuer einen.
            // Roles und Debug bleiben aussen vor: die haben eine EIGENE Bildlaufleiste ueber die volle
            // Resthoehe und rechnen dafuer mit `body` - eine zweite darum wuerde ihre Hoehenrechnung
            // aushebeln.
            bool ownScroll = settingsTab == SettingsTab.Roles || settingsTab == SettingsTab.Debug;
            if (tabScrollFor != settingsTab)
            {
                tabScrollFor = settingsTab;
                tabScroll = Vector2.zero;
            }
            float measured;
            tabContentHeight.TryGetValue(settingsTab, out measured);
            bool scrolled = !ownScroll && measured > body.height;
            Rect listRect = body;
            if (scrolled)
            {
                Rect view = new Rect(0f, 0f, body.width - 16f, measured);
                Widgets.BeginScrollView(body, ref tabScroll, view);
                listRect = view;
            }
            Listing_Standard ls = new Listing_Standard();
            // ====== DAS ist die eigentliche Ursache (2026-08-19, im Test widerlegt) ======
            // Die erste Erklaerung war falsch: Listing legt seine Rechtecke NICHT einfach unterhalb des
            // sichtbaren Bereichs weiter an. Verse.Listing.NewColumnIfNeeded bricht bei
            // `curY + hoehe > listingRect.height` in eine ZWEITE SPALTE um - curY faellt auf 0 zurueck und
            // curX waechst um ColumnWidth + 17, also aus dem Fenster hinaus. Der Inhalt war damit nicht
            // unten weg, sondern RECHTS daneben, und unsichtbar.
            // Zwei Folgen, und beide erklaeren genau das, was im Test gemessen wurde:
            //   1. Der Presets-Reiter zeigte Ueberschrift und Erklaertext, dann leeren Raum - die Tabelle
            //      stand in der unsichtbaren zweiten Spalte.
            //   2. Weil curY beim Umbruch auf 0 zurueckfaellt, wird CurHeight NIE groesser als body.height.
            //      Die Bildlauf-Bedingung darunter konnte deshalb gar nicht ausloesen, und der Reiter
            //      verhielt sich nach meinem ersten Fix exakt wie vorher. Genau so kam es zurueck:
            //      "Scrollen aendert daran nichts, es gibt auch keine sichtbare Bildlaufleiste."
            // maxOneColumn schaltet den Umbruch ab. Erst dann laeuft der Inhalt wirklich nach unten, erst
            // dann misst CurHeight die echte Hoehe, und erst dann greift die Bildlaufleiste.
            // Beides wird gebraucht - einzeln loest keines der zwei das Problem.
            ls.maxOneColumn = true;
            ls.Begin(listRect);

            switch (settingsTab)
            {
                case SettingsTab.Filters:    DoFiltersTab(ls); break;
                case SettingsTab.Loadouts:   DoLoadoutsTab(ls); break;
                case SettingsTab.Roles:      DoRolesTab(ls, body); break;
                case SettingsTab.AmmoBills:  DoAmmoBillsTab(ls); break;
                case SettingsTab.AutoAssign: DoAutoAssignTab(ls); break;
                case SettingsTab.Presets:    DoPresetsTab(ls); break;
                case SettingsTab.Alerts:     DoAlertsTab(ls); break;
                case SettingsTab.Tools:      DoToolsTab(ls); break;
                case SettingsTab.Debug:      DoDebugTab(ls, body); break;
            }

            ls.End();
            // Vor EndScrollView lesen ist egal, nach ls.End() nicht: CurHeight ist der Stand, den das
            // Listing beim Schliessen erreicht hat. Nur fuer die Reiter merken, die wir auch wirklich
            // umschliessen - bei Roles/Debug endet das Listing weit oberhalb ihrer eigenen Liste, die Zahl
            // wuerde also das Gegenteil dessen behaupten, was auf dem Schirm steht.
            if (!ownScroll) tabContentHeight[settingsTab] = ls.CurHeight;
            if (scrolled) Widgets.EndScrollView();
            base.DoSettingsWindowContents(inRect);
        }

        // Neun Knoepfe dieser Datei laufen seit 2026-08-27 ueber LoadoutUtility.ButtonTextFit statt ueber
        // Widgets.ButtonText: die Reiterleiste, die Zellen der Preset-Tabelle, Aufraeumen, Rolle loeschen und
        // die vier Aufklappknoepfe mit dynamischem Wert. Grund: drei der fuenf deutschen Kappungen desselben
        // Tages sassen in dieser Datei, und ButtonText zentriert und schneidet dann BEIDSEITIG ab - ein Knopf
        // mit zu langem Text wird unlesbar, ohne dass man es dem Code ansieht.
        // Die Umstellung ist fuer jeden Knopf, dessen Text ohnehin passt, ein No-op: ButtonTextFit misst
        // zuerst und stuft nur dann auf Tiny bzw. kuerzt sichtbar. Deshalb aendert sich an Englisch nichts.
        // Die uebrigen Knoepfe dieser Datei rechnen ihre Breite bereits aus dem Text (ResetButton und die
        // beiden Preset-Resets) und brauchen das nicht.
        private void DrawTab(Rect r, string label, SettingsTab tab)
        {
            if (settingsTab == tab) Widgets.DrawHighlightSelected(r);
            if (LoadoutUtility.ButtonTextFit(r, label)) settingsTab = tab;
        }

        // Display label for a preset tuning column header. The internal "name" stays the original English word -
        // it also doubles as part of presetBufs' dictionary key, so only the label shown to the player is translated.
        private static string PresetDisplayLabel(string internalName)
        {
            switch (internalName)
            {
                case "Balanced": return "CER_ModSettings_PresetBalanced".Translate();
                case "Anti-Mech": return "CER_ModSettings_PresetAntiMech".Translate();
                case "Economy": return "CER_ModSettings_PresetEconomy".Translate();
                case "Best Gear": return "CER_ModSettings_PresetBestGear".Translate();
                case "Focus Ranged": return "CER_ModSettings_PresetFocusRanged".Translate();
                case "All": return "CER_ModSettings_PresetAll".Translate();
                case "Available items": return "CER_ModSettings_PresetAvailableItems".Translate();
                default: return internalName;
            }
        }

        // --- PRESETS TAB: the complete slider set each preset button applies in the Squad Setup popups. ---
        private void DoPresetsTab(Listing_Standard ls)
        {
            Text.Font = GameFont.Medium;
            ls.Label("CER_ModSettings_PresetTuningHeader".Translate());
            Text.Font = GameFont.Small;
            ls.Gap(6f);
            ls.Label("CER_ModSettings_PresetTuningDesc".Translate());
            ls.Gap(10f);

            var presets = new (string name, CEReloadedSettings.PresetTuning t)[]
            {
                ("Balanced", settings.presetBalanced),
                ("Anti-Mech", settings.presetAntiMech),
                ("Economy", settings.presetEconomy),
                ("Best Gear", settings.presetBestGear),
                ("Focus Ranged", settings.presetRanged),
                ("All", settings.presetAll),
                ("Available items", settings.presetPreferAvailable)
            };

            const float lblW = 240f, colW = 88f, colGap = 6f, rowH = 26f;
            // Kopfzeilenhoehe GEMESSEN statt gesetzt (2026-08-23: "Focus Ranged"
            // und "Available items" brachen auf zwei Zeilen um und wurden dann oben UND unten beschnitten -
            // die erste Zeile vom Beschreibungstext darueber, die zweite von den Eingabefeldern darunter).
            // Ursache war eine feste 24: einwortige Koepfe passen in eine Zeile, zweiwortige nicht, und
            // Widgets.Label bricht dann um, ohne dass das Rechteck mitwaechst.
            // Eine groessere feste Zahl waere hier die falsche Loesung - welche Kopfzeile umbricht, haengt
            // an der SPRACHE: "Available items" braucht zwei Zeilen, "Verfuegbare Gegenstaende" moeglicherweise
            // drei. Deshalb wird die Hoehe aus dem laengsten wirklich vorhandenen Kopf berechnet, mit
            // Text.CalcHeight bei genau der Spaltenbreite, in der er auch gezeichnet wird. Nach unten auf
            // eine Zeile begrenzt, damit die Tabelle nicht schrumpft.
            // Die uebrigen Masse haengen ohnehin schon an headerH (gridH und rowY leiten sich davon ab) -
            // es genuegt also, diese eine Zahl richtig zu bestimmen.
            // Schrift pro Kopf (2026-08-26: "Anti-Maschinoid" stand als "Anti-Maschin"/"oid"
            // da): passt ein einzelnes WORT nicht in die Spalte, trennt Unity mitten im Wort - dann lieber
            // dieser Kopf in Tiny, wo das Wort einzeilig passt. Umbrueche an Wortgrenzen (zwei Zeilen)
            // bleiben erlaubt und erwuenscht. Die Hoehenmessung unten benutzt DIESELBE Schriftwahl,
            // sonst stimmt headerH nicht zum Gezeichneten.
            GameFont HeaderFont(string label)
            {
                GameFont saved = Text.Font;
                Text.Font = GameFont.Small;
                bool wordTooWide = false;
                foreach (string word in label.Split(' '))
                    if (Text.CalcSize(word).x > colW) { wordTooWide = true; break; }
                Text.Font = saved;
                return wordTooWide ? GameFont.Tiny : GameFont.Small;
            }
            GameFont prevHeaderFont = Text.Font;
            float headerH = 24f;
            foreach (var pr in presets)
            {
                string hl = PresetDisplayLabel(pr.name);
                Text.Font = HeaderFont(hl);
                headerH = Mathf.Max(headerH, Text.CalcHeight(hl, colW) + 4f);
            }
            Text.Font = prevHeaderFont;
            // Wide enough for every preset column, however many exist - grows automatically if a preset is
            // ever added later instead of silently clipping/wrapping the header (as a fixed window width did
            // once there were 7 of these). Horizontal scroll rather than trying to shrink columns to fit,
            // since RimWorld's own Mod Settings window width isn't something a mod controls.
            float neededW = lblW + presets.Length * (colW + colGap);
            const int tuningRowCount = 9;
            float gridH = headerH + tuningRowCount * rowH;
            Rect gridOuter = ls.GetRect(gridH + 16f);   // +16 for the scrollbar strip BeginScrollView reserves
            Rect gridView = new Rect(0, 0, Mathf.Max(neededW, gridOuter.width), gridH);
            Widgets.BeginScrollView(gridOuter, ref presetGridScroll, gridView);

            for (int i = 0; i < presets.Length; i++)
            {
                string hl = PresetDisplayLabel(presets[i].name);
                Text.Font = HeaderFont(hl);
                Text.Anchor = TextAnchor.MiddleCenter;
                Widgets.Label(new Rect(lblW + i * (colW + colGap), 0, colW, headerH), hl);
                Text.Anchor = TextAnchor.UpperLeft;
                Text.Font = prevHeaderFont;
            }

            float rowY = headerH;
            void Row(string label, string key, string tip,
                System.Func<CEReloadedSettings.PresetTuning, float> get, System.Action<CEReloadedSettings.PresetTuning, float> set)
            {
                Rect r = new Rect(0, rowY, gridView.width, rowH);
                Widgets.Label(new Rect(r.x, r.y + 2f, lblW - 6f, 24f), label);
                TooltipHandler.TipRegion(new Rect(r.x, r.y, lblW, rowH), tip);
                for (int i = 0; i < presets.Length; i++)
                {
                    var t = presets[i].t;
                    if (t == null) continue;
                    string bufKey = presets[i].name + "." + key;
                    presetBufs.TryGetValue(bufKey, out string buf);
                    float v = get(t);
                    Widgets.TextFieldNumeric(new Rect(r.x + lblW + i * (colW + colGap), r.y, colW, 24f), ref v, ref buf, 0f, 1f);
                    presetBufs[bufKey] = buf;
                    set(t, v);
                }
                rowY += rowH;
            }

            Row("CER_ModSettings_RowMech".Translate(), "mech", "CER_ModSettings_RowMechTip".Translate(), t => t.mech, (t, v) => t.mech = v);
            Row("CER_ModSettings_RowAmmoVar".Translate(), "ammoVar", "CER_ModSettings_RowAmmoVarTip".Translate(), t => t.ammoVar, (t, v) => t.ammoVar = v);
            Row("CER_ModSettings_RowCraft".Translate(), "craft", "CER_ModSettings_RowCraftTip".Translate(), t => t.craft, (t, v) => t.craft = v);
            Row("CER_ModSettings_RowMelee".Translate(), "melee", "CER_ModSettings_RowMeleeTip".Translate(), t => t.melee, (t, v) => t.melee = v);
            Row("CER_ModSettings_RowDiversity".Translate(), "diversity", "CER_ModSettings_RowDiversityTip".Translate(), t => t.diversity, (t, v) => t.diversity = v);
            Row("CER_ModSettings_RowBudget".Translate(), "budget", "CER_ModSettings_RowBudgetTip".Translate(), t => t.budget, (t, v) => t.budget = v);
            Row("CER_ModSettings_RowPackCoverage".Translate(), "packCoverage", "CER_ModSettings_RowPackCoverageTip".Translate(), t => t.packCoverage, (t, v) => t.packCoverage = v);
            Widgets.EndScrollView();

            ls.Gap(10f);
            Rect resetR = ls.GetRect(28f);
            // Breite gemessen statt 240f fest - dieselbe Sache wie ResetButton (2026-08-26).
            string resetPresetsLabel = "CER_ModSettings_ResetPresetsButton".Translate();
            if (Widgets.ButtonText(new Rect(resetR.x, resetR.y, Mathf.Max(240f, Text.CalcSize(resetPresetsLabel).x + 24f), 28f), resetPresetsLabel))
            {
                settings.presetBalanced = CEReloadedSettings.DefaultPresetBalanced();
                settings.presetAntiMech = CEReloadedSettings.DefaultPresetAntiMech();
                settings.presetEconomy = CEReloadedSettings.DefaultPresetEconomy();
                settings.presetBestGear = CEReloadedSettings.DefaultPresetBestGear();
                settings.presetRanged = CEReloadedSettings.DefaultPresetRanged();
                settings.presetAll = CEReloadedSettings.DefaultPresetAll();
                settings.presetPreferAvailable = CEReloadedSettings.DefaultPresetPreferAvailable();
                presetBufs.Clear();
                Messages.Message("CER_ModSettings_PresetsResetMsg".Translate(), MessageTypeDefOf.TaskCompletion, false);
            }

            ls.GapLine(14f);
            Text.Font = GameFont.Medium;
            ls.Label("CER_ModSettings_ArmModePresetsHeader".Translate());
            Text.Font = GameFont.Small;
            ls.Gap(6f);
            ls.Label("CER_ModSettings_ArmModePresetsDesc".Translate());
            ls.Gap(10f);

            var armPresets = new (string name, CEReloadedSettings.ArmModePreset p)[]
            {
                ("CER_ModSettings_ArmCautious".Translate(), settings.armCautious),
                ("CER_ModSettings_ArmNormal".Translate(), settings.armNormal),
                ("CER_ModSettings_ArmAggressive".Translate(), settings.armAggressive)
            };
            const float armLblW = 220f, armColW = 100f, armRowH = 26f, armHeaderH = 24f;
            Rect armGrid = ls.GetRect(armHeaderH + 6 * armRowH);
            for (int i = 0; i < armPresets.Length; i++)
            {
                Text.Anchor = TextAnchor.MiddleCenter;
                Widgets.Label(new Rect(armGrid.x + armLblW + i * armColW, armGrid.y, armColW, armHeaderH), armPresets[i].name);
                Text.Anchor = TextAnchor.UpperLeft;
            }
            float armRowY = armGrid.y + armHeaderH;
            void ArmRow(string label, System.Func<CEReloadedSettings.ArmModePreset, bool> get, System.Action<CEReloadedSettings.ArmModePreset, bool> set)
            {
                Widgets.Label(new Rect(armGrid.x, armRowY + 3f, armLblW - 6f, 20f), label);
                for (int i = 0; i < armPresets.Length; i++)
                {
                    var p = armPresets[i].p;
                    bool v = get(p);
                    Rect cb = new Rect(armGrid.x + armLblW + i * armColW + armColW / 2f - 10f, armRowY + 2f, 20f, 20f);
                    Widgets.Checkbox(cb.position, ref v, 20f);
                    set(p, v);
                }
                armRowY += armRowH;
            }
            // 3-state cycle (None -> Managed -> All -> None on click) - "Delete ammo/weapon bills" are scope
            // choices now, not a plain on/off (2026-08-01), so a bare checkbox no longer fits this cell.
            void ArmScopeRow(string label, System.Func<CEReloadedSettings.ArmModePreset, LoadoutUtility.BillDeleteScope> get,
                System.Action<CEReloadedSettings.ArmModePreset, LoadoutUtility.BillDeleteScope> set)
            {
                Widgets.Label(new Rect(armGrid.x, armRowY + 3f, armLblW - 6f, 20f), label);
                for (int i = 0; i < armPresets.Length; i++)
                {
                    var p = armPresets[i].p;
                    LoadoutUtility.BillDeleteScope v = get(p);
                    string txt = v == LoadoutUtility.BillDeleteScope.None ? "CER_ModSettings_ScopeOff".Translate() : v == LoadoutUtility.BillDeleteScope.ManagedOnly ? "CER_ModSettings_ScopeManaged".Translate() : "CER_ModSettings_ScopeAll".Translate();
                    Rect cb = new Rect(armGrid.x + armLblW + i * armColW + 4f, armRowY + 1f, armColW - 8f, 22f);
                    if (LoadoutUtility.ButtonTextFit(cb, txt))
                    {
                        var next = v == LoadoutUtility.BillDeleteScope.None ? LoadoutUtility.BillDeleteScope.ManagedOnly
                            : v == LoadoutUtility.BillDeleteScope.ManagedOnly ? LoadoutUtility.BillDeleteScope.All
                            : LoadoutUtility.BillDeleteScope.None;
                        set(p, next);
                    }
                }
                armRowY += armRowH;
            }
            ArmRow("CER_ModSettings_ArmRowResetPawns".Translate(), t => t.resetPawns, (t, v) => t.resetPawns = v);
            ArmScopeRow("CER_ModSettings_ArmRowDeleteAmmoBills".Translate(), t => t.deleteAmmoBills, (t, v) => t.deleteAmmoBills = v);
            ArmScopeRow("CER_ModSettings_ArmRowDeleteWeaponBills".Translate(), t => t.deleteWeaponBills, (t, v) => t.deleteWeaponBills = v);
            ArmScopeRow("CER_ModSettings_ArmRowDeleteWeaponLoadouts".Translate(), t => t.deleteWeaponLoadouts, (t, v) => t.deleteWeaponLoadouts = v);
            ArmScopeRow("CER_ModSettings_ArmRowDeleteEquipmentPacks".Translate(), t => t.deleteEquipmentPacks, (t, v) => t.deleteEquipmentPacks = v);
            ArmRow("CER_ModSettings_ArmRowCreateAmmoBills".Translate(), t => t.createAmmoBills, (t, v) => t.createAmmoBills = v);

            ls.Gap(6f);
            Rect armResetR = ls.GetRect(28f);
            // Breite gemessen statt 240f fest - dieselbe Sache wie ResetButton (2026-08-26); die deutsche
            // Beschriftung dieses Knopfs ist die laengste der ganzen Datei (~450px).
            string resetArmLabel = "CER_ModSettings_ResetArmPresetsButton".Translate();
            if (Widgets.ButtonText(new Rect(armResetR.x, armResetR.y, Mathf.Max(240f, Text.CalcSize(resetArmLabel).x + 24f), 28f), resetArmLabel))
            {
                settings.armCautious = CEReloadedSettings.DefaultArmCautious();
                settings.armNormal = CEReloadedSettings.DefaultArmNormal();
                settings.armAggressive = CEReloadedSettings.DefaultArmAggressive();
                Messages.Message("CER_ModSettings_ArmPresetsResetMsg".Translate(), MessageTypeDefOf.TaskCompletion, false);
            }
        }

        // --- ALERTS TAB: configure the vanilla-style combat-readiness alerts (top-right). ---
        private void DoAlertsTab(Listing_Standard ls)
        {
            Text.Font = GameFont.Medium;
            ls.Label("CER_ModSettings_AlertsHeader".Translate());
            Text.Font = GameFont.Small;
            ls.Gap(6f);

            ls.Label("CER_ModSettings_AlertsDesc".Translate());
            ls.Gap(6f);

            Rect masterRect = ls.GetRect(24f);
            Widgets.CheckboxLabeled(masterRect, "CER_ModSettings_AlertMasterSwitch".Translate(), ref settings.showReadinessAlerts);
            ls.GapLine(10f);

            bool on = settings.showReadinessAlerts;
            if (!on) GUI.color = new Color(1f, 1f, 1f, 0.5f);

            Rect noLoadoutRect = ls.GetRect(24f);
            Widgets.CheckboxLabeled(noLoadoutRect, "CER_ModSettings_AlertNoLoadout".Translate(), ref settings.alertNoLoadout);
            TooltipHandler.TipRegion(noLoadoutRect, "CER_ModSettings_AlertNoLoadoutTip".Translate());
            Widgets.CheckboxLabeled(ls.GetRect(24f), "CER_ModSettings_AlertWeaponWithoutAmmo".Translate(), ref settings.alertWeaponWithoutAmmo);
            Widgets.CheckboxLabeled(ls.GetRect(24f), "CER_ModSettings_AlertWeaponUnavailable".Translate(), ref settings.alertWeaponUnavailable);
            Widgets.CheckboxLabeled(ls.GetRect(24f), "CER_ModSettings_AlertLowAmmo".Translate(), ref settings.alertLowAmmo);
            // Direkt unter dem zugehoerigen Haken, eingerueckt - die Zahl gehoert zu DIESER Warnung.
            Rect lowMagRow = ls.GetRect(24f);
            Widgets.Label(new Rect(lowMagRow.x + 28f, lowMagRow.y, 300f, 24f), "CER_ModSettings_AlertLowAmmoMagsLabel".Translate());
            settings.alertLowAmmoMags = Widgets.TextField(new Rect(lowMagRow.x + 330f, lowMagRow.y, 50f, 24f), settings.alertLowAmmoMags);
            TooltipHandler.TipRegion(new Rect(lowMagRow.x + 28f, lowMagRow.y, 352f, 24f), "CER_ModSettings_AlertLowAmmoMagsTip".Translate());
            Widgets.CheckboxLabeled(ls.GetRect(24f), "CER_ModSettings_AlertOverloaded".Translate(), ref settings.alertOverloaded);
            Rect capBlockedRect = ls.GetRect(24f);
            Widgets.CheckboxLabeled(capBlockedRect, "CER_ModSettings_AlertCapacityBlocked".Translate(), ref settings.alertCapacityBlocked);
            TooltipHandler.TipRegion(capBlockedRect, "CER_ModSettings_AlertCapacityBlockedTip".Translate());
            Rect notCarriedRect = ls.GetRect(24f);
            Widgets.CheckboxLabeled(notCarriedRect, "CER_ModSettings_AlertAssignedNotCarried".Translate(), ref settings.alertAssignedNotCarried);
            TooltipHandler.TipRegion(notCarriedRect, "CER_ModSettings_AlertAssignedNotCarriedTip".Translate());

            ls.Gap(4f);
            Widgets.CheckboxLabeled(ls.GetRect(24f), "CER_ModSettings_AlertAmmoNotProduced".Translate(), ref settings.alertAmmoNotProduced);
            Rect threshRect = ls.GetRect(24f);
            GUI.color = on ? Color.white : new Color(1f, 1f, 1f, 0.5f);
            // Breite GEMESSEN statt fest 360 (2026-09-04: auf Englisch stand
            // "...only if fewer than X rounds are stockpiled (0 =" und dann kam das Eingabefeld - die
            // Klammer wurde nie geschlossen, und RimWorlds Label schneidet ohne Auslassungszeichen ab,
            // es sah also nach einem kaputten Text aus statt nach zu wenig Platz).
            // Struktureller Fix statt groesserer Zahl: der Text bekommt, was er braucht, und
            // das Feld rueckt dahinter - nur eben nie ueber den rechten Rand hinaus.
            string threshLabel = "CER_ModSettings_AlertAmmoStockThresholdLabel".Translate();
            float threshTextX = threshRect.x + 24f;
            float fieldX = Mathf.Min(threshTextX + Text.CalcSize(threshLabel).x + 8f, threshRect.xMax - 62f);
            Widgets.Label(new Rect(threshTextX, threshRect.y, Mathf.Max(40f, fieldX - threshTextX - 6f), 24f), threshLabel);
            settings.alertAmmoStockThreshold = Widgets.TextField(new Rect(fieldX, threshRect.y, 60f, 24f), settings.alertAmmoStockThreshold);
            TooltipHandler.TipRegion(threshRect, "CER_ModSettings_AlertAmmoStockThresholdTip".Translate());

            GUI.color = Color.white;

            ResetButton(ls, "CER_ModSettings_ResetAlertsButton".Translate(), "showReadinessAlerts", "alertNoLoadout", "alertWeaponWithoutAmmo", "alertWeaponUnavailable", "alertAmmoNotProduced", "alertAmmoStockThreshold", "alertLowAmmo", "alertLowAmmoMags", "alertOverloaded", "alertCapacityBlocked", "alertAssignedNotCarried");
        }

        // --- TOOLS TAB: weapon audit, resetting the save to the mod settings, the guide, and preparing a save for uninstall. ---
        private void DoToolsTab(Listing_Standard ls)
        {
            Text.Font = GameFont.Medium;
            ls.Label("CER_ModSettings_ToolsHeader".Translate());
            Text.Font = GameFont.Small;
            ls.Gap(6f);

            ls.Label("CER_ModSettings_WeaponAuditDesc".Translate());
            ls.Gap(10f);

            // Beide Knopfbreiten dieses Reiters gemessen statt 280f/320f fest (2026-08-27):
            // "Aktuelles Spiel auf diese Mod-Einstellungen
            // zuruecksetzen" stand auf Deutsch als "uelles Spiel auf diese Mod-Einstellungen zurueckset" da -
            // links UND rechts abgeschnitten, weil Widgets.ButtonText zentriert und dann kappt. Der
            // Waffenpruefungs-Knopf darueber passte nur zufaellig; gemessen wird jetzt beides.
            string auditLabel = "CER_ModSettings_RunWeaponAuditButton".Translate();
            Rect r = ls.GetRect(38f);
            r.width = Mathf.Max(280f, Text.CalcSize(auditLabel).x + 24f);
            if (Widgets.ButtonText(r, auditLabel))
                Find.WindowStack.Add(new Dialog_WeaponAudit());

            // Every save starts as a copy of these mod settings and then keeps its own values. This overwrites the
            // running save's copy with the mod settings again, in one go instead of the change-by-change close sync.
            ls.Gap(16f);
            ls.GapLine();
            ls.Gap(6f);
            ls.Label("CER_ModSettings_ResetGameDesc".Translate());
            ls.Gap(10f);
            string resetGameLabel = "CER_ModSettings_ResetGameButton".Translate();
            Rect r2 = ls.GetRect(38f);
            r2.width = Mathf.Max(320f, Text.CalcSize(resetGameLabel).x + 24f);
            bool inGame = Current.Game != null;
            GUI.color = inGame ? Color.white : new Color(1f, 1f, 1f, 0.45f);
            bool resetClicked = Widgets.ButtonText(r2, resetGameLabel);
            GUI.color = Color.white;
            TooltipHandler.TipRegion(r2, inGame
                ? "CER_ModSettings_ResetGameTipActive".Translate()
                : "CER_ModSettings_ResetGameTipInactive".Translate());
            if (resetClicked && inGame)
            {
                CEReloadedGameComponent comp = CEReloadedGameComponent.Cached;
                if (comp != null)
                {
                    comp.ResetUiToSettings();
                    // Recapture the close-sync snapshot so the reseed is not double-applied on window close.
                    seedSnapshot = CaptureSeeds();
                    Messages.Message("CER_ModSettings_GameResetMsg".Translate(), MessageTypeDefOf.TaskCompletion, false);
                }
            }

            // Der Crashkurs (2026-08-30): das "?" im Fensterkopf ist weg, hier ist sein neuer Platz - einmal zeigen,
            // und den Erststart zuruecksetzen (damit ein Test das automatische erste Oeffnen pruefen kann).
            ls.Gap(16f);
            ls.GapLine();
            ls.Gap(6f);
            ls.Label("CER_ModSettings_GuideDesc".Translate());
            ls.Gap(10f);
            string showGuideLabel = "CER_ModSettings_ShowGuideButton".Translate();
            string resetGuideLabel = "CER_ModSettings_ResetGuideButton".Translate();
            Rect r3 = ls.GetRect(38f);
            float showW = Mathf.Max(200f, Text.CalcSize(showGuideLabel).x + 24f);
            if (LoadoutUtility.ButtonTextFit(new Rect(r3.x, r3.y, showW, r3.height), showGuideLabel))
                Find.WindowStack.Add(new Dialog_CERWorkflowGuide());
            float resetW = Mathf.Max(200f, Text.CalcSize(resetGuideLabel).x + 24f);
            Rect r4 = new Rect(r3.x + showW + 10f, r3.y, resetW, r3.height);
            bool guideSeen = settings != null && settings.seenGuide;
            GUI.color = guideSeen ? Color.white : new Color(1f, 1f, 1f, 0.45f);
            bool resetGuideClicked = LoadoutUtility.ButtonTextFit(r4, resetGuideLabel);
            GUI.color = Color.white;
            TooltipHandler.TipRegion(r4, "CER_ModSettings_ResetGuideTip".Translate());
            if (resetGuideClicked && guideSeen)
            {
                settings.seenGuide = false;
                settings.Write();
                if (Current.Game != null) Messages.Message("CER_ModSettings_GuideResetMsg".Translate(), MessageTypeDefOf.TaskCompletion, false);
            }

            // Run before removing the mod (see SaveCleanseUtility). Lives here rather than in the Debug tab because
            // players look for it when uninstalling. Asks for confirmation because it cannot be undone.
            ls.Gap(16f);
            ls.GapLine();
            ls.Gap(6f);
            ls.Label("CER_ModSettings_CleanseSaveTip".Translate());
            ls.Gap(10f);
            string cleanseLabel = "CER_ModSettings_CleanseSaveButton".Translate();
            Rect r5 = ls.GetRect(38f);
            r5.width = Mathf.Max(280f, Text.CalcSize(cleanseLabel).x + 24f);
            bool cleanseInGame = Current.Game != null && Current.ProgramState == ProgramState.Playing;
            GUI.color = cleanseInGame ? Color.white : new Color(1f, 1f, 1f, 0.45f);
            bool cleanseClicked = Widgets.ButtonText(r5, cleanseLabel);
            GUI.color = Color.white;
            if (!cleanseInGame) TooltipHandler.TipRegion(r5, "CER_ModSettings_ResetGameTipInactive".Translate());
            if (cleanseClicked && cleanseInGame)
            {
                Find.WindowStack.Add(LoadoutUtility.CompactConfirmation(
                    "CER_ModSettings_CleanseSaveConfirm".Translate(),
                    () =>
                    {
                        SaveCleanseUtility.Result res = SaveCleanseUtility.Run();
                        Messages.Message("CER_ModSettings_CleanseSaveDone".Translate(res.unwrapped.ToString(), res.baked.ToString(),
                            res.flattened.ToString(), res.anchorsDeleted.ToString()), MessageTypeDefOf.TaskCompletion, false);
                    },
                    destructive: true));
            }
        }

        // --- DEBUG TAB: every Loadout object CE's own LoadoutManager knows about, unfiltered - including
        // our own hidden per-pawn ammo anchors, Equipment Packs and Roles, none of which ever show up in
        // CE's native pickers (Patch_FloatMenu strips them there on purpose). Read-only except for the one
        // cleanup action - for troubleshooting/verification, not day-to-day use. ---
        private void DoDebugTab(Listing_Standard ls, Rect body)
        {
            Text.Font = GameFont.Medium;
            ls.Label("CER_ModSettings_DebugHeader".Translate());
            Text.Font = GameFont.Small;
            GUI.color = new Color(1f, 1f, 1f, 0.6f);
            ls.Label("CER_ModSettings_DebugDesc".Translate());
            GUI.color = Color.white;
            ls.Gap(6f);


            if (Current.Game == null || Current.ProgramState != ProgramState.Playing)
            {
                ls.Label("CER_ModSettings_DebugNoGame".Translate());
                return;
            }

            List<Loadout> all = LoadoutManager.Loadouts.Where(l => l != null).ToList();

            // Shared with CEReloadedGameComponent's own periodic auto-cleanup (every ~1 in-game hour), so
            // this button is really just "do it now" rather than the only way it ever happens.
            List<Loadout> orphans = AmmoAssignmentUtility.FindOrphanedAnchors();

            Rect bar = ls.GetRect(32f);
            Rect cleanupRect = new Rect(bar.x, bar.y, 260f, 30f);
            GUI.enabled = orphans.Count > 0;
            if (LoadoutUtility.ButtonTextFit(cleanupRect, "CER_ModSettings_CleanupOrphanedAnchorsButton".Translate(orphans.Count.ToString())))
            {
                int removed = AmmoAssignmentUtility.CleanupOrphanedAnchors();
                Messages.Message(removed == 1
                    ? "CER_ModSettings_RemovedOrphansSingular".Translate(removed.ToString())
                    : "CER_ModSettings_RemovedOrphansPlural".Translate(removed.ToString()),
                    MessageTypeDefOf.TaskCompletion, false);
            }
            GUI.enabled = true;
            TooltipHandler.TipRegion(cleanupRect, "CER_ModSettings_CleanupOrphanedAnchorsTip".Translate());

            ls.Gap(6f);

            float top = body.y + ls.CurHeight;
            float nameW = 260f, catW = 110f, parentW = 220f;
            float axCat = body.x + nameW + 8f;
            float axParent = axCat + catW + 8f;
            float axSlots = axParent + parentW + 8f;
            GUI.color = new Color(1f, 1f, 1f, 0.7f);
            Widgets.Label(new Rect(body.x + 2f, top, nameW, 22f), "CER_ModSettings_DebugColLabel".Translate());
            Widgets.Label(new Rect(axCat, top, catW, 22f), "CER_ModSettings_DebugColCategory".Translate());
            Widgets.Label(new Rect(axParent, top, parentW, 22f), "CER_ModSettings_DebugColParent".Translate());
            Widgets.Label(new Rect(axSlots, top, 60f, 22f), "CER_ModSettings_DebugColSlots".Translate());
            GUI.color = Color.white;
            top += 24f;
            Widgets.DrawLineHorizontal(body.x, top, body.width);
            top += 4f;

            const float rowH = 26f;
            // The vanilla Mod-Settings dialog draws its own Close/Reset button strip as an overlay UNDER
            // whatever inRect it hands to DoSettingsWindowContents - it isn't excluded from inRect's own
            // height, so a scrollview that greedily fills all the way to body.yMax gets drawn (and is
            // clickable) behind that strip. Every other tab's content is short enough to never reach it;
            // this one deliberately fills the whole tab, so it needs to reserve the same bottom margin our
            // own dialogs already set aside for their bottom button bar (LoadoutUtility.BottomBarH).
            Rect outRect = new Rect(body.x, top, body.width, body.yMax - top - LoadoutUtility.BottomBarH);
            Rect viewRect = new Rect(0, 0, outRect.width - 16f, all.Count * rowH + 4f);
            Widgets.BeginScrollView(outRect, ref debugScroll, viewRect);
            for (int i = 0; i < all.Count; i++)
            {
                Loadout l = all[i];
                Rect row = new Rect(0, i * rowH, viewRect.width, rowH - 2f);
                if (i % 2 == 0) Widgets.DrawLightHighlight(row);

                bool isOrphan = false;
                string category;
                if (l.defaultLoadout) category = "CER_ModSettings_CategoryDefault".Translate();
                else if (AmmoAssignmentUtility.IsAmmoLink(l)) { category = "CER_ModSettings_CategoryAmmoAnchor".Translate(); isOrphan = orphans.Contains(l); }
                else if (EquipmentPackUtility.IsEquipmentPackLoadout(l)) category = "CER_ModSettings_CategoryEquipmentPack".Translate();
                else if (RoleUtility.IsRoleLoadout(l)) category = "CER_ModSettings_CategoryRole".Translate();
                else category = "CER_ModSettings_CategoryWeaponLoadout".Translate();

                GUI.color = isOrphan ? LoadoutUtility.ColBad : Color.white;
                Widgets.Label(new Rect(0, row.y + 2f, nameW, 22f), (LoadoutUtility.CellLabel(l) ?? "?").Truncate(nameW - 4f));
                Widgets.Label(new Rect(axCat - body.x, row.y + 2f, catW, 22f), category + (isOrphan ? "CER_ModSettings_OrphanSuffix".Translate() : ""));
                GUI.color = Color.white;
                Loadout parent = l.ParentLoadout;
                Widgets.Label(new Rect(axParent - body.x, row.y + 2f, parentW, 22f), (parent?.label ?? "-").Truncate(parentW - 4f));
                int slotCount = l.OwnSlots?.Count ?? 0;
                Widgets.Label(new Rect(axSlots - body.x, row.y + 2f, 60f, 22f), slotCount.ToString());

                // OwnSlots above is the raw count on THIS object alone (0 for an anchor with no extra ammo
                // of its own, even though it clearly carries plenty once its parent chain and extra-parents
                // resolve) - the tooltip shows the FULLY RESOLVED .Slots union instead, since that's what
                // actually matters for "what does this loadout end up giving a pawn".
                List<Loadout> extras = MultiParentUtility.GetExtraParents(l);
                string tip = "CER_ModSettings_DebugTipHeader".Translate(l.UniqueID.ToString(), slotCount.ToString());
                if (parent != null) tip += "CER_ModSettings_DebugTipParent".Translate(LoadoutUtility.CellLabel(parent));
                // Angezeigte Namen, nicht rohe Etiketten (2026-08-17): die Spalte daneben nennt die Art schon,
                // deshalb CellLabel. Vorher stand hier als einziger Stelle dieser Liste noch "W| ...".
                if (extras.Count > 0) tip += "CER_ModSettings_DebugTipExtraParents".Translate(string.Join(", ", extras.Select(e => LoadoutUtility.CellLabel(e))));
                List<LoadoutSlot> resolved = l.Slots?.ToList() ?? new List<LoadoutSlot>();
                tip += resolved.Count > 0
                    ? "CER_ModSettings_DebugTipResolvedContents".Translate(resolved.Count.ToString(), string.Join("\n", resolved.Select(s =>
                        "CER_ModSettings_DebugTipSlotLine".Translate(((s.thingDef != null ? (Def)s.thingDef : s.genericDef)?.LabelCap.ToString() ?? "?"), s.count.ToString()).ToString())))
                    : "CER_ModSettings_DebugTipResolvedContentsNone".Translate();
                TooltipHandler.TipRegion(row, tip);
            }
            Widgets.EndScrollView();
        }

        // --- ROLES TAB: scaling factors applied to a weapon loadout in Generate Loadouts -> Role Mode. ---
        private bool rolesShowLocal;
        private void DoRolesTab(Listing_Standard ls, Rect body)
        {
            RoleUtility.EnsureSeeded(settings);

            Text.Font = GameFont.Medium;
            ls.Label("CER_ModSettings_RolesHeader".Translate());
            Text.Font = GameFont.Small;
            ls.Label("CER_ModSettings_RolesDesc".Translate());
            ls.Gap(6f);

            // Scope: the global library (every colony) vs. roles saved only in the current game. Shared Radio style
            // (dot directly before the label) - the vanilla RadioButtonLabeled parked the dot at the far end of the rect.
            Rect scopeRect = ls.GetRect(28f);
            float scopeX = scopeRect.x;
            if (LoadoutUtility.Radio(ref scopeX, scopeRect.y + 2f, "CER_ModSettings_RolesScopeGlobal".Translate(), !rolesShowLocal)) rolesShowLocal = false;
            if (LoadoutUtility.Radio(ref scopeX, scopeRect.y + 2f, "CER_ModSettings_RolesScopeLocal".Translate(), rolesShowLocal)) rolesShowLocal = true;
            ls.Gap(4f);

            List<LoadoutRole> list = rolesShowLocal ? RoleUtility.LocalRoles() : settings.roles;

            // Add / Reset bar (at the top so it is always visible).
            Rect bar = ls.GetRect(32f);
            if (list != null)
            {
                // Beide Breiten gemessen statt 150f/170f fest (2026-08-27): "Voreinstellungen zuruecksetzen"
                // endete auf
                // Deutsch als "Voreinstellungen zurueckset". Dieselbe Rechnung wie ResetButton weiter unten -
                // dieser eine Knopf lief nur deshalb aus der Reihe, weil er den gemeinsamen Helfer nicht
                // benutzt (er sitzt in einer Reihe neben "+ Rolle hinzufuegen", nicht allein auf seiner Zeile).
                string addRoleLabel = "CER_ModSettings_AddRoleButton".Translate();
                float addRoleW = Mathf.Max(150f, Text.CalcSize(addRoleLabel).x + 24f);
                if (Widgets.ButtonText(new Rect(bar.x, bar.y, addRoleW, 30f), addRoleLabel))
                    list.Add(new LoadoutRole { name = rolesShowLocal ? "CER_ModSettings_NewSaveRoleName".Translate() : "CER_ModSettings_NewRoleName".Translate() });
                if (!rolesShowLocal)
                {
                    string resetRolesLabel = "CER_ModSettings_ResetRolesToPresetsButton".Translate();
                    Rect resetRect = new Rect(bar.x + addRoleW + 10f, bar.y,
                        Mathf.Max(170f, Text.CalcSize(resetRolesLabel).x + 24f), 30f);
                    if (Widgets.ButtonText(resetRect, resetRolesLabel))
                    {
                        // Dieselbe Zeile wie der Zuruecksetzen-Knopf im Rollen-Reiter (2026-09-04) - vorher
                        // machten die zwei Knoepfe Verschiedenes, und beide liessen die Loadouts der
                        // weggeworfenen Rollen herrenlos zurueck.
                        RoleUtility.ResetGlobalRoles(settings);
                    }
                    TooltipHandler.TipRegion(resetRect, "CER_ModSettings_ResetRolesToPresetsTip".Translate());

                    // Zwilling des Knopfes im Rollen-Reiter (2026-09-04): holt NUR die fehlenden
                    // Vorgaberollen zurueck und laesst alles andere in Ruhe. Erscheint nur, wenn etwas
                    // fehlt. Ohne ihn war der Nachbarknopf der einzige Weg zurueck - und der wirft jede
                    // selbst gebaute Rolle mit weg.
                    int missingPresets = RoleUtility.MissingPresetRoles(settings).Count;
                    if (missingPresets > 0)
                    {
                        string restoreLbl = "CER_ModSettings_RestorePresetRolesButton".Translate(missingPresets.ToString()).ToString();
                        Rect restoreRect = new Rect(resetRect.xMax + 10f, bar.y,
                            LoadoutUtility.BottomBtnW(restoreLbl, 170f, 300f), 30f);
                        if (LoadoutUtility.ButtonTextFit(restoreRect, restoreLbl))
                        {
                            int n = RoleUtility.RestoreMissingPresetRoles(settings);
                            if (n > 0)
                                Messages.Message("CER_ModSettings_RestoredPresetRolesMessage".Translate(n.ToString()),
                                    MessageTypeDefOf.TaskCompletion, false);
                        }
                        TooltipHandler.TipRegion(restoreRect, "CER_ModSettings_RestorePresetRolesTip".Translate());
                    }
                }
            }
            ls.Gap(6f);

            if (list == null)
            {
                GUI.color = new Color(1f, 1f, 1f, 0.6f);
                ls.Label("CER_ModSettings_RolesNoSaveLoaded".Translate());
                GUI.color = Color.white;
                return;
            }

            float top = body.y + ls.CurHeight;

            // Column header
            float nameW = 150f, colW = 62f, gap = 4f;
            float axAmmo = body.x + nameW + 10f;
            float axMed = axAmmo + colW + gap;
            float axFood = axMed + colW + gap;
            float axNade = axFood + colW + gap;
            float axDel = axNade + colW + gap;
            GUI.color = new Color(1f, 1f, 1f, 0.7f);
            Widgets.Label(new Rect(body.x + 2f, top, nameW, 22f), "CER_ModSettings_RoleColName".Translate());
            Widgets.Label(new Rect(axAmmo, top, colW, 22f), "CER_ModSettings_RoleColAmmo".Translate());
            Widgets.Label(new Rect(axMed, top, colW, 22f), "CER_ModSettings_RoleColMedicine".Translate());
            Widgets.Label(new Rect(axFood, top, colW, 22f), "CER_ModSettings_RoleColFood".Translate());
            Widgets.Label(new Rect(axNade, top, colW, 22f), "CER_ModSettings_RoleColGrenades".Translate());
            GUI.color = Color.white;
            top += 24f;
            Widgets.DrawLineHorizontal(body.x, top, body.width);
            top += 4f;

            const float rowH = 34f;
            Rect outRect = new Rect(body.x, top, body.width, body.yMax - top - 8f);
            Rect viewRect = new Rect(0, 0, outRect.width - 16f, list.Count * rowH + 4f);
            Widgets.BeginScrollView(outRect, ref rolesScroll, viewRect);
            int toDelete = -1;
            for (int i = 0; i < list.Count; i++)
            {
                LoadoutRole role = list[i];
                Rect row = new Rect(0, i * rowH, viewRect.width, rowH - 4f);
                if (i % 2 == 0) Widgets.DrawLightHighlight(row);
                float ry = row.y + 4f;
                role.name = Widgets.TextField(new Rect(2f, ry, nameW, 24f), role.name);
                FactorField(new Rect(axAmmo - body.x, ry, colW, 24f), role, 0, () => role.ammoFactor, v => role.ammoFactor = v);
                FactorField(new Rect(axMed - body.x, ry, colW, 24f), role, 1, () => role.medicineFactor, v => role.medicineFactor = v);
                FactorField(new Rect(axFood - body.x, ry, colW, 24f), role, 2, () => role.foodFactor, v => role.foodFactor = v);
                FactorField(new Rect(axNade - body.x, ry, colW, 24f), role, 3, () => role.grenadeFactor, v => role.grenadeFactor = v);
                Rect delRect = new Rect(axDel - body.x, ry + 2f, 22f, 22f);
                if (LoadoutUtility.ButtonTextFit(delRect, "CER_ModSettings_DeleteRoleButton".Translate())) toDelete = i;
                TooltipHandler.TipRegion(delRect, "CER_ModSettings_DeleteRoleTip".Translate());
            }
            Widgets.EndScrollView();
            if (toDelete >= 0)
            {
                // Das Loadout der Rolle GEHT MIT (2026-09-04, derselbe Fehler wie am Loeschknopf im
                // Rollen-Reiter - zweite Fundstelle desselben Musters). Ohne das bleibt "[Role] Assault"
                // als besitzerloses Loadout stehen: es gehoert dann zu keiner Rolle mehr, gilt damit
                // ueberall als ganz gewoehnliche Vorlage und wird in der Zuweisung selbst unter "Keine
                // Rollen" wieder verteilt - mit seinem rohen Etikett samt "[Role]"-Marker.
                Loadout doomed = RoleUtility.RoleLoadoutOf(list[toDelete]);
                list.RemoveAt(toDelete);
                if (doomed != null) LoadoutDeletionUtility.Delete(doomed);
            }
        }

        // Zweite Fundstelle desselben Musters (2026-09-04, Regel-7-Grep): auch hier hing der Eingabepuffer
        // an der ZEILENNUMMER ("G"/"L" + i + Feldbuchstabe). Diese Liste sortiert sich zwar nicht von
        // selbst um, aber sie wird kuerzer, sobald man eine Rolle loescht - danach zeigt jeder Puffer ab
        // der Loeschstelle auf die naechste Rolle, und der erste Tastendruck schreibt eine fremde Zahl.
        // Die ausfuehrliche Begruendung steht am Zwilling in Dialog_GenerateLoadouts.
        private readonly Dictionary<LoadoutRole, string[]> roleFactorBuffers = new Dictionary<LoadoutRole, string[]>();
        private void FactorField(Rect r, LoadoutRole role, int slot, System.Func<float> get, System.Action<float> set)
        {
            if (role == null) return;
            if (!roleFactorBuffers.TryGetValue(role, out string[] bufs)) roleFactorBuffers[role] = bufs = new string[4];
            float v = get();
            string buf = bufs[slot] ?? v.ToString("0.##");
            Widgets.TextFieldNumeric(r, ref v, ref buf, 0f, 99f);
            bufs[slot] = buf;
            set(v);
        }

        // --- Per-tab "reset to defaults" -----------------------------------------------------------------------------
        private static readonly CEReloadedSettings SettingsDefaults = new CEReloadedSettings();

        private void ResetFields(params string[] names)
        {
            System.Type t = typeof(CEReloadedSettings);
            foreach (string n in names)
            {
                var f = t.GetField(n);
                if (f != null) f.SetValue(settings, f.GetValue(SettingsDefaults));
            }
            settings.Write();
        }

        // Breite gemessen statt 230f fest (2026-08-26: die deutschen Beschriftungen -
        // "Munitionsauftraege auf Standard zuruecksetzen" u.a. - liefen beidseitig aus dem Knopf).
        // Das Einstellungsfenster ist mindestens 1100 breit (Patch_ModSettingsSize), Platz ist da.
        private void ResetButton(Listing_Standard ls, string buttonLabel, params string[] names)
        {
            ls.Gap(10f);
            Rect r = ls.GetRect(30f);
            r.width = Mathf.Max(230f, Text.CalcSize(buttonLabel).x + 24f);
            if (Widgets.ButtonText(r, buttonLabel)) ResetFields(names);
        }

        // --- FILTERS TAB: default UI states of the various weapon/ammo pickers, plus the workbench list toggle. ---
        private void DoFiltersTab(Listing_Standard ls)
        {
            Text.Font = GameFont.Medium;
            ls.Label("CER_ModSettings_FiltersHeader".Translate());
            Text.Font = GameFont.Small;
            ls.Gap(6f);

            EnumDropdown(ls, "CER_ModSettings_DefaultAmmoFilterLabel".Translate(), settings.defAmmoFilter, v => settings.defAmmoFilter = v);
            EnumDropdown(ls, "CER_ModSettings_DefaultAmmoBillFilterLabel".Translate(), settings.defAmmoBillFilter, v => settings.defAmmoBillFilter = v,
                new[] { AmmoBillFilterMode.AvailableLoadouts });   // "Created loadouts" entfallen (2026-09-07)

            ls.GapLine(12f);

            Rect benchRect = ls.GetRect(24f);
            Widgets.CheckboxLabeled(benchRect, "CER_ModSettings_HideDuplicateBenches".Translate(), ref settings.hideDuplicateBenches);
            TooltipHandler.TipRegion(benchRect, "CER_ModSettings_HideDuplicateBenchesTip".Translate());

            Rect claimRect = ls.GetRect(24f);
            Widgets.CheckboxLabeled(claimRect, "CER_ModSettings_ClaimOpenWeaponProduction".Translate(), ref settings.claimOpenWeaponProduction);
            TooltipHandler.TipRegion(claimRect, "CER_ModSettings_ClaimOpenWeaponProductionTip".Translate());

            ResetButton(ls, "CER_ModSettings_ResetFiltersButton".Translate(), "defAmmoFilter", "defAmmoBillFilter", "hideDuplicateBenches", "claimOpenWeaponProduction");
        }

        // --- LOADOUTS TAB: naming, sidearm bulk cap, default meal/medicine and per-type ammo counts. ---
        private void DoLoadoutsTab(Listing_Standard ls)
        {
            const float textWidth = 100f;
            Text.Font = GameFont.Medium;
            ls.Label("CER_ModSettings_LoadoutsHeader".Translate());
            Text.Font = GameFont.Small;
            ls.Gap(6f);

            Rect firearmAmmoRow = ls.GetRect(0f);
            EnumDropdown(ls, "CER_ModSettings_DefaultFirearmAmmoLabel".Translate(), settings.defFirearmAmmo, v => settings.defFirearmAmmo = v);
            TooltipHandler.TipRegion(new Rect(firearmAmmoRow.x, firearmAmmoRow.y, firearmAmmoRow.width, 24f),
                "CER_ModSettings_FirearmAmmoTip".Translate());
            ls.Gap(4f);

            // Was a real, persisted, ACTIVELY-APPLIED field (see UiState.cs's own seeding from it) with no
            // checkbox anywhere to actually change it (2026-08-01 fix, found during a full-mod review pass) -
            // permanently stuck at its false default, unreachable by any player through the settings screen.
            Rect detailedRect = ls.GetRect(24f);
            Widgets.CheckboxLabeled(new Rect(detailedRect.x, detailedRect.y, detailedRect.width / 2 - 20f, 24f),
                "CER_ModSettings_StartTabsDetailedLabel".Translate(), ref settings.startTabsDetailed);
            TooltipHandler.TipRegion(detailedRect, "CER_ModSettings_StartTabsDetailedTip".Translate());
            ls.Gap(4f);

            Rect prefixRect = ls.GetRect(24f);
            bool noPrefix = !settings.useLoadoutPrefix;
            Widgets.CheckboxLabeled(new Rect(prefixRect.x, prefixRect.y, prefixRect.width / 2 - 20f, 24f), "CER_ModSettings_NoLoadoutPrefix".Translate(), ref noPrefix);
            settings.useLoadoutPrefix = !noPrefix;
            if (settings.useLoadoutPrefix)
            {
                settings.loadoutPrefix = Widgets.TextField(new Rect(prefixRect.x + prefixRect.width / 2, prefixRect.y, textWidth * 2, 24f), settings.loadoutPrefix);
            }
            else
            {
                GUI.color = Color.gray;
                Widgets.TextField(new Rect(prefixRect.x + prefixRect.width / 2, prefixRect.y, textWidth * 2, 24f), "CER_ModSettings_DisabledPlaceholder".Translate());
                GUI.color = Color.white;
            }

            // Purely cosmetic, same as the weapon-loadout prefix above - Equipment Packs are still matched
            // internally by their own invariant "[Pack]" marker regardless of this text, so changing it can
            // never un-recognize an already-authored pack.
            Rect packPrefixRect = ls.GetRect(24f);
            bool noPackPrefix = !settings.usePackPrefix;
            Widgets.CheckboxLabeled(new Rect(packPrefixRect.x, packPrefixRect.y, packPrefixRect.width / 2 - 20f, 24f), "CER_ModSettings_NoPackPrefix".Translate(), ref noPackPrefix);
            settings.usePackPrefix = !noPackPrefix;
            if (settings.usePackPrefix)
            {
                settings.packPrefix = Widgets.TextField(new Rect(packPrefixRect.x + packPrefixRect.width / 2, packPrefixRect.y, textWidth * 2, 24f), settings.packPrefix);
            }
            else
            {
                GUI.color = Color.gray;
                Widgets.TextField(new Rect(packPrefixRect.x + packPrefixRect.width / 2, packPrefixRect.y, textWidth * 2, 24f), "CER_ModSettings_DisabledPlaceholder".Translate());
                GUI.color = Color.white;
            }

            // Same idea, for Roles (2026-08-02) - purely cosmetic too, RoleUtility still matches internally
            // by the invariant "[Role]" marker regardless of this text.
            Rect rolePrefixRect = ls.GetRect(24f);
            bool noRolePrefix = !settings.useRolePrefix;
            Widgets.CheckboxLabeled(new Rect(rolePrefixRect.x, rolePrefixRect.y, rolePrefixRect.width / 2 - 20f, 24f), "CER_ModSettings_NoRolePrefix".Translate(), ref noRolePrefix);
            settings.useRolePrefix = !noRolePrefix;
            if (settings.useRolePrefix)
            {
                settings.rolePrefix = Widgets.TextField(new Rect(rolePrefixRect.x + rolePrefixRect.width / 2, rolePrefixRect.y, textWidth * 2, 24f), settings.rolePrefix);
            }
            else
            {
                GUI.color = Color.gray;
                Widgets.TextField(new Rect(rolePrefixRect.x + rolePrefixRect.width / 2, rolePrefixRect.y, textWidth * 2, 24f), "CER_ModSettings_DisabledPlaceholder".Translate());
                GUI.color = Color.white;
            }

            Rect bulkRect = ls.GetRect(24f);
            Widgets.Label(bulkRect.LeftHalf(), "CER_ModSettings_SidearmMaxBulkLabel".Translate());
            settings.sidearmMaxBulk = Widgets.TextField(new Rect(bulkRect.x + bulkRect.width / 2, bulkRect.y, textWidth, 24f), settings.sidearmMaxBulk);

            // Split sidearm: the backup complements the main weapon. Used by Assign's "would this weapon likely
            // come with a complementary backup" scoring bonus for weapons with no loadout yet (Dialog_SquadSetup's
            // virtual candidates) - not baked into any generated loadout directly (sidearms are their own Weapon
            // Loadout now, attached to a pawn/role separately). None = never counts as having a backup.
            WeaponSettingDropdown(ls, "CER_ModSettings_SidearmMeleeLabel".Translate(), settings.defSidearmMeleeDefName,
                d => LoadoutUtility.IsRealWeaponDef(d) && d.IsMeleeWeapon, v => settings.defSidearmMeleeDefName = v,
                "CER_ModSettings_SidearmMeleeTip".Translate(), offerAuto: true);
            WeaponSettingDropdown(ls, "CER_ModSettings_SidearmRangedLabel".Translate(), settings.defSidearmRangedDefName,
                d => LoadoutUtility.IsRealWeaponDef(d) && d.IsRangedWeapon, v => settings.defSidearmRangedDefName = v,
                "CER_ModSettings_SidearmRangedTip".Translate(), offerAuto: true);
            WeaponSettingDropdown(ls, "CER_ModSettings_DefaultShieldLabel".Translate(), settings.defShieldDefName,
                d => LoadoutUtility.IsShieldApparel(d), v => settings.defShieldDefName = v,
                "CER_ModSettings_DefaultShieldTip".Translate(), offerAuto: true);

            // Shared attribute-driven option lists (same as the Generate tab's pickers) - the old "any ingestible"
            // list offered raw megasloth meat and worse as a loadout default.
            List<Def> allFoods = LoadoutUtility.LoadoutFoodOptions();
            List<Def> allMedicines = LoadoutUtility.LoadoutMedicineOptions();

            // Sagt seit 2026-08-14 ausdruecklich, WO diese vier Werte wirken. Sie waren bis dahin
            // wirkungslos, und die blossen Beschriftungen
            // "Standard-Mahlzeit"/"Standard-Medizin" sagen von sich aus nicht, worauf sie sich beziehen -
            // genau die Luecke, aus der die Verwirrung entstanden ist.
            GUI.color = new Color(1f, 1f, 1f, 0.6f);
            Text.Font = GameFont.Tiny;
            ls.Label("CER_ModSettings_MealMedicineScopeNote".Translate());
            Text.Font = GameFont.Small;
            GUI.color = Color.white;

            Rect mealRect = ls.GetRect(24f);
            Widgets.Label(mealRect.LeftHalf(), "CER_ModSettings_DefaultMealLabel".Translate());
            Def currentMeal = allFoods.FirstOrDefault(f => f.defName == settings.defMealDefName);
            if (LoadoutUtility.ButtonTextFit(new Rect(mealRect.x + mealRect.width / 2, mealRect.y, textWidth * 2, 24f), currentMeal != null ? currentMeal.label.Truncate(textWidth * 2) : "CER_ModSettings_NoneOption".Translate()))
            {
                List<FloatMenuOption> mealOpts = new List<FloatMenuOption> { new FloatMenuOption("CER_ModSettings_NoneOption".Translate(), () => { settings.defMealDefName = ""; }) };
                foreach (var f in allFoods)
                {
                    Def localF = f;
                    mealOpts.Add(new FloatMenuOption(localF.label, () => { settings.defMealDefName = localF.defName; }));
                }
                Find.WindowStack.Add(new FloatMenu(mealOpts));
            }

            Rect mealCountRect = ls.GetRect(24f);
            Widgets.Label(mealCountRect.LeftHalf(), "CER_ModSettings_DefaultMealCountLabel".Translate());
            settings.defMealCount = Widgets.TextField(new Rect(mealCountRect.x + mealCountRect.width / 2, mealCountRect.y, textWidth, 24f), settings.defMealCount);

            Rect medRect = ls.GetRect(24f);
            Widgets.Label(medRect.LeftHalf(), "CER_ModSettings_DefaultMedicineLabel".Translate());
            Def currentMed = allMedicines.FirstOrDefault(m => m.defName == settings.defMedicineDefName);
            if (LoadoutUtility.ButtonTextFit(new Rect(medRect.x + medRect.width / 2, medRect.y, textWidth * 2, 24f), currentMed != null ? currentMed.label.Truncate(textWidth * 2) : "CER_ModSettings_NoneOption".Translate()))
            {
                List<FloatMenuOption> medOpts = new List<FloatMenuOption> { new FloatMenuOption("CER_ModSettings_NoneOption".Translate(), () => { settings.defMedicineDefName = ""; }) };
                foreach (var m in allMedicines)
                {
                    Def localM = m;
                    medOpts.Add(new FloatMenuOption(localM.label, () => { settings.defMedicineDefName = localM.defName; }));
                }
                Find.WindowStack.Add(new FloatMenu(medOpts));
            }

            Rect medCountRect = ls.GetRect(24f);
            Widgets.Label(medCountRect.LeftHalf(), "CER_ModSettings_DefaultMedicineCountLabel".Translate());
            settings.defMedicineCount = Widgets.TextField(new Rect(medCountRect.x + medCountRect.width / 2, medCountRect.y, textWidth, 24f), settings.defMedicineCount);

            ls.GapLine(12f);

            Text.Font = GameFont.Medium;
            ls.Label("CER_ModSettings_AmmoCountsHeader".Translate());
            Text.Font = GameFont.Small;
            ls.Gap(6f);

            // Beschriftungsbreite GEMESSEN statt 180 fest (2026-09-07: 'Grenades / thrown explosives:'
            // wurde abgeschnitten, auf Deutsch in beiden Spalten). Die Kette:
            // Spaltenbreite des Reiters -> zwei Spalten je (Beschriftung + 60er-Feld). Die Beschriftung ist so
            // breit wie die laengste der zehn in der aktiven Sprache; passen zwei Spalten damit nicht mehr
            // nebeneinander, faellt der Block auf eine Spalte je Zeile zurueck, statt irgendwo abzuschneiden.
            // Pistolen (2026-08-09) haben einen eigenen Wert, weil sie sonst auf die 150 des Allgemein-Feldes fallen.
            (string label, string tip, System.Func<string> get, System.Action<string> set)[] ammoFields =
            {
                ("CER_ModSettings_AmmoAssaultLabel",  null,                               () => settings.defAmmoAssault,  v => settings.defAmmoAssault = v),
                ("CER_ModSettings_AmmoSniperLabel",   null,                               () => settings.defAmmoSniper,   v => settings.defAmmoSniper = v),
                ("CER_ModSettings_AmmoLMGLabel",      null,                               () => settings.defAmmoLMG,      v => settings.defAmmoLMG = v),
                ("CER_ModSettings_AmmoShotgunLabel",  null,                               () => settings.defAmmoShotgun,  v => settings.defAmmoShotgun = v),
                ("CER_ModSettings_AmmoBowLabel",      null,                               () => settings.defAmmoBow,      v => settings.defAmmoBow = v),
                ("CER_ModSettings_AmmoLauncherLabel", null,                               () => settings.defAmmoLauncher, v => settings.defAmmoLauncher = v),
                ("CER_ModSettings_AmmoDefaultLabel",  null,                               () => settings.defAmmoDefault,  v => settings.defAmmoDefault = v),
                ("CER_ModSettings_AmmoSelfAmmoLabel", "CER_ModSettings_AmmoSelfAmmoTip",  () => settings.defAmmoSelfAmmo, v => settings.defAmmoSelfAmmo = v),
                ("CER_ModSettings_AmmoGrenadeLabel",  "CER_ModSettings_AmmoGrenadeTip",   () => settings.defAmmoGrenade,  v => settings.defAmmoGrenade = v),
                ("CER_ModSettings_AmmoPistolLabel",   "CER_ModSettings_AmmoPistolTip",    () => settings.defAmmoPistol,   v => settings.defAmmoPistol = v),
            };
            float ammoLabelW = 0f;
            foreach (var f in ammoFields) ammoLabelW = Mathf.Max(ammoLabelW, Text.CalcSize(f.label.Translate()).x);
            ammoLabelW = Mathf.Ceil(ammoLabelW) + 8f;
            const float ammoFieldW = 60f, ammoColGap = 24f;
            int ammoCols = 2f * (ammoLabelW + ammoFieldW) + ammoColGap <= ls.ColumnWidth ? 2 : 1;
            float ammoColW = ammoCols == 2 ? (ls.ColumnWidth - ammoColGap) / 2f : ls.ColumnWidth;
            for (int i = 0; i < ammoFields.Length; i += ammoCols)
            {
                Rect ammoRow = ls.GetRect(24f);
                for (int c = 0; c < ammoCols && i + c < ammoFields.Length; c++)
                {
                    var f = ammoFields[i + c];
                    float x = ammoRow.x + c * (ammoColW + ammoColGap);
                    Rect labelR = new Rect(x, ammoRow.y, ammoLabelW, 24f);
                    Widgets.Label(labelR, f.label.Translate());
                    f.set(Widgets.TextField(new Rect(x + ammoLabelW, ammoRow.y, ammoFieldW, 24f), f.get()));
                    if (f.tip != null) TooltipHandler.TipRegion(labelR, f.tip.Translate());
                }
            }

            ResetButton(ls, "CER_ModSettings_ResetLoadoutDefaultsButton".Translate(), "defLoadoutSort", "defFirearmAmmo", "startTabsDetailed", "useLoadoutPrefix", "loadoutPrefix", "usePackPrefix", "packPrefix", "sidearmMaxBulk",
                "defSidearmMeleeDefName", "defSidearmRangedDefName", "defShieldDefName",
                "defMealDefName", "defMealCount", "defMedicineDefName", "defMedicineCount",
                "defAmmoAssault", "defAmmoSniper", "defAmmoLMG", "defAmmoShotgun", "defAmmoPistol", "defAmmoBow", "defAmmoLauncher", "defAmmoSelfAmmo", "defAmmoGrenade", "defAmmoDefault");
        }

        // --- AMMO BILLS TAB ---
        private void DoAmmoBillsTab(Listing_Standard ls)
        {
            const float textWidth = 100f;
            Text.Font = GameFont.Medium;
            ls.Label("CER_ModSettings_AmmoBillsHeader".Translate());
            Text.Font = GameFont.Small;
            ls.Gap(6f);

            Rect ammoBatchRect = ls.GetRect(24f);
            Widgets.Label(ammoBatchRect.LeftHalf(), "CER_ModSettings_DefaultAmmoBatchesLabel".Translate());
            settings.defAmmoBatches = Widgets.TextField(new Rect(ammoBatchRect.x + ammoBatchRect.width / 2, ammoBatchRect.y, textWidth, 24f), settings.defAmmoBatches);

            ls.Label("CER_ModSettings_SkillRequirementsLabel".Translate());
            IntRange skillRange = new IntRange(settings.ammoBillMinSkill, settings.ammoBillMaxSkill);
            Rect rangeRect = ls.GetRect(28f);
            Widgets.IntRange(rangeRect, 1, ref skillRange, 0, SkillRecord.MaxLevel, "CER_ModSettings_SkillRangeFormat");
            settings.ammoBillMinSkill = skillRange.min;
            settings.ammoBillMaxSkill = skillRange.max;

            ls.Gap(10f);
            // Der Hinweis haengt am Rechteck der Ueberschrift SELBST. Vorher an `ls.GetRect(0f)` - ein
            // Rechteck mit Hoehe 0, ueber dem der Zeiger nie steht: der Text, der die zwei Regler darunter
            // ueberhaupt erklaert, war damit tot (2026-08-15, Review-Befund).
            Rect ammoReserveHdr = ls.Label("CER_ModSettings_AmmoReserveHeader".Translate());
            TooltipHandler.TipRegion(ammoReserveHdr, "CER_ModSettings_AmmoReserveHeaderTip".Translate());
            Rect fullWeightRect = ls.GetRect(24f);
            Widgets.Label(fullWeightRect.LeftHalf(), "CER_ModSettings_AmmoReserveFullWeightLabel".Translate(settings.ammoReserveFullWeightPawns.ToString()));
            settings.ammoReserveFullWeightPawns = Mathf.RoundToInt(Widgets.HorizontalSlider(fullWeightRect.RightHalf(), settings.ammoReserveFullWeightPawns, 0f, 10f, true));
            TooltipHandler.TipRegion(fullWeightRect, "CER_ModSettings_AmmoReserveFullWeightTip".Translate());

            Rect floorRect = ls.GetRect(24f);
            Widgets.Label(floorRect.LeftHalf(), "CER_ModSettings_AmmoReserveFloorLabel".Translate(Mathf.RoundToInt(settings.ammoReserveTrailingFloor * 100f).ToString()));
            settings.ammoReserveTrailingFloor = Widgets.HorizontalSlider(floorRect.RightHalf(), settings.ammoReserveTrailingFloor, 0f, 1f, true);
            TooltipHandler.TipRegion(floorRect, "CER_ModSettings_AmmoReserveFloorTip".Translate());

            ResetButton(ls, "CER_ModSettings_ResetAmmoBillsButton".Translate(), "defAmmoBatches", "ammoBillMinSkill", "ammoBillMaxSkill",
                "ammoReserveFullWeightPawns", "ammoReserveTrailingFloor");
        }

        // --- AUTO-ASSIGN TAB: default states of the Assign Loadouts controls. ---
        private void DoAutoAssignTab(Listing_Standard ls)
        {
            Text.Font = GameFont.Medium;
            ls.Label("CER_ModSettings_AutoAssignHeader".Translate());
            Text.Font = GameFont.Small;
            ls.Gap(6f);

            ls.Label("CER_ModSettings_AutoAssignDesc".Translate());
            ls.Gap(6f);

            Rect filterRect = ls.GetRect(24f);
            Widgets.Label(filterRect.LeftHalf(), "CER_ModSettings_DefaultWeaponsFilterLabel".Translate());
            if (LoadoutUtility.ButtonTextFit(new Rect(filterRect.x + filterRect.width / 2, filterRect.y, 220f, 24f), LoadoutFilterLabel(settings.autoAssignLoadoutFilter)))
            {
                List<FloatMenuOption> opts = new List<FloatMenuOption>
                {
                    new FloatMenuOption(LoadoutFilterLabel(AssignLoadoutFilter.All), () => settings.autoAssignLoadoutFilter = AssignLoadoutFilter.All),
                    new FloatMenuOption(LoadoutFilterLabel(AssignLoadoutFilter.AvailableMainWeapon), () => settings.autoAssignLoadoutFilter = AssignLoadoutFilter.AvailableMainWeapon),
                    new FloatMenuOption(LoadoutFilterLabel(AssignLoadoutFilter.ResearchedMainWeapon), () => settings.autoAssignLoadoutFilter = AssignLoadoutFilter.ResearchedMainWeapon)
                };
                Find.WindowStack.Add(new FloatMenu(opts));
            }
            TooltipHandler.TipRegion(filterRect, "CER_ModSettings_AutoAssignFilterTip".Translate());

            // "Produce missing/main/sidearms" settings removed entirely (2026-07-30) - the
            // end-of-session production check (LoadoutUtility.CheckAndOpenMissingProductionPopup) now always
            // covers this, so there's nothing left to configure here.
            // Aus dem Haken ist eine Auswahl mit drei Werten geworden (2026-08-19) - siehe RoleAssignMode.
            // Ueber dieselbe EnumDropdown-Bauform wie die Filter darueber, statt eines eigenen Bedienelements.
            EnumDropdown(ls, "CER_ModSettings_RoleModeLabel".Translate(), settings.autoAssignRoleMode, v => settings.autoAssignRoleMode = v);

            ls.GapLine(12f);
            ls.Label("CER_ModSettings_WeaponSkillRequirementsLabel".Translate());
            IntRange wSkill = new IntRange(settings.weaponBillMinSkill, settings.weaponBillMaxSkill);
            Rect wRangeRect = ls.GetRect(28f);
            Widgets.IntRange(wRangeRect, 2, ref wSkill, 0, SkillRecord.MaxLevel, "CER_ModSettings_SkillRangeFormat");
            settings.weaponBillMinSkill = wSkill.min;
            settings.weaponBillMaxSkill = wSkill.max;

            ResetButton(ls, "CER_ModSettings_ResetAutoAssignButton".Translate(), "autoAssignLoadoutFilter", "autoAssignRoleMode", "weaponBillMinSkill", "weaponBillMaxSkill");
        }

        private static string LoadoutFilterLabel(AssignLoadoutFilter m)
        {
            switch (m)
            {
                case AssignLoadoutFilter.All: return "CER_ModSettings_FilterAllLoadouts".Translate();
                case AssignLoadoutFilter.AvailableMainWeapon: return "CER_ModSettings_FilterAvailableMainWeapon".Translate();
                case AssignLoadoutFilter.ResearchedMainWeapon: return "CER_ModSettings_FilterResearchedMainWeapon".Translate();
                default: return m.ToString();
            }
        }

        // The same wording the actual UI filters use, so the settings defaults read identically to the tabs.
        private static string EnumLabel<T>(T v) where T : struct, System.Enum
        {
            if (v is WeaponFilterMode wf)
                switch (wf)
                {
                    case WeaponFilterMode.AllWeapons: return "CER_ModSettings_EnumAll".Translate();
                    case WeaponFilterMode.ResearchedWeapons: return "CER_ModSettings_EnumResearched".Translate();
                    case WeaponFilterMode.AvailableWeapons: return "CER_ModSettings_EnumAvailable".Translate();
                    case WeaponFilterMode.AssignedLoadout: return "CER_ModSettings_EnumInLoadout".Translate();
                }
            if (v is RoleAssignMode rm)
                switch (rm)
                {
                    case RoleAssignMode.Mixed: return "CER_LoadoutUtility_RoleModeMixed".Translate();
                    case RoleAssignMode.RolesOnly: return "CER_LoadoutUtility_RoleModeRolesOnly".Translate();
                    case RoleAssignMode.NoRoles: return "CER_LoadoutUtility_RoleModeNoRoles".Translate();
                }
            if (v is AmmoFilterMode af)
                switch (af)
                {
                    case AmmoFilterMode.AllAmmo: return "CER_ModSettings_EnumAll".Translate();
                    case AmmoFilterMode.ResearchedAmmo: return "CER_ModSettings_EnumResearched".Translate();
                    case AmmoFilterMode.AvailableAmmo: return "CER_ModSettings_EnumAvailable".Translate();
                }
            if (v is AmmoBillFilterMode bf)
                switch (bf)
                {
                    case AmmoBillFilterMode.AssignedLoadouts: return "CER_ModSettings_EnumAssignedLoadouts".Translate();
                    case AmmoBillFilterMode.AvailableLoadouts: return "CER_ModSettings_EnumAvailableLoadouts".Translate();
                    case AmmoBillFilterMode.AvailableWeapons: return "CER_ModSettings_EnumAvailableWeapons".Translate();
                    case AmmoBillFilterMode.ActiveBills: return "CER_ModSettings_EnumActiveBills".Translate();
                    case AmmoBillFilterMode.AllAmmunition: return "CER_ModSettings_EnumAllAmmunition".Translate();
                }
            if (v is AssignLoadoutFilter lf)
                switch (lf)
                {
                    case AssignLoadoutFilter.All: return "CER_ModSettings_FilterAllLoadouts".Translate();
                    case AssignLoadoutFilter.AvailableMainWeapon: return "CER_ModSettings_FilterAvailableMainWeapon".Translate();
                    case AssignLoadoutFilter.ResearchedMainWeapon: return "CER_ModSettings_FilterResearchedMainWeapon".Translate();
                }
            return v.ToString();
        }

        // A "None + defs matching filter" dropdown backed by a settings def-name string. No availability filter - every
        // matching def is selectable (unlike the Generate tab's per-row sidearm dropdown). The filter carries the full
        // condition (IsWeapon / IsApparel etc.) so this backs weapons and shield apparel alike.
        private void WeaponSettingDropdown(Listing_Standard ls, string label, string cur, System.Func<ThingDef, bool> filter, System.Action<string> set, string tip, bool offerAuto = false)
        {
            Rect r = ls.GetRect(24f);
            Widgets.Label(r.LeftHalf(), label);
            bool isAuto = offerAuto && cur == CEReloadedSettings.AutoPick;
            ThingDef curDef = string.IsNullOrEmpty(cur) || isAuto ? null : DefDatabase<ThingDef>.GetNamedSilentFail(cur);
            float bw = r.width / 2f;
            string btnLabel = isAuto ? "CER_ModSettings_AutoRecommended".Translate() : (curDef != null ? curDef.label.Truncate(bw) : "CER_ModSettings_NoneOption".Translate());
            if (LoadoutUtility.ButtonTextFit(new Rect(r.x + bw, r.y, bw, 24f), btnLabel))
            {
                List<FloatMenuOption> opts = new List<FloatMenuOption>();
                if (offerAuto) opts.Add(new FloatMenuOption("CER_ModSettings_AutoRecommended".Translate(), () => set(CEReloadedSettings.AutoPick)));
                opts.Add(new FloatMenuOption("CER_ModSettings_NoneOption".Translate(), () => set("")));
                foreach (ThingDef wd in DefDatabase<ThingDef>.AllDefs.Where(d => !d.destroyOnDrop && filter(d)).OrderBy(d => d.label))
                {
                    ThingDef local = wd;
                    opts.Add(new FloatMenuOption(local.label, () => set(local.defName)));
                }
                Find.WindowStack.Add(new FloatMenu(opts));
            }
            TooltipHandler.TipRegion(r, tip);
        }

        private void EnumDropdown<T>(Listing_Standard ls, string label, T current, System.Action<T> setter, T[] exclude = null) where T : struct, System.Enum
        {
            const float textWidth = 100f;
            Rect r = ls.GetRect(24f);
            Widgets.Label(r.LeftHalf(), label);
            if (LoadoutUtility.ButtonTextFit(new Rect(r.x + r.width / 2, r.y, textWidth * 2, 24f), EnumLabel(current)))
            {
                List<FloatMenuOption> opts = new List<FloatMenuOption>();
                foreach (T val in System.Enum.GetValues(typeof(T)))
                {
                    if (exclude != null && exclude.Contains(val)) continue;
                    T local = val;
                    opts.Add(new FloatMenuOption(EnumLabel(val), () => setter(local)));
                }
                Find.WindowStack.Add(new FloatMenu(opts));
            }
        }
    }
}
