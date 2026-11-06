using System;
using System.Collections.Generic;
using System.Linq;
using RimWorld;
using Verse;
using UnityEngine;
using CombatExtended;

namespace CEReloaded
{
    // Auto-generate a whole starter PALETTE of Equipment Packs in one pass - Base Supply, a Medic Kit, a
    // Combat Drugs Kit, a Shield Kit, a Spotter, a Tactical Kit, an EMP Kit - the same way the Weapon
    // Loadouts tab's auto-generate builds one row per weapon instead of asking the player to hand-build each
    // one. Sized to match Dialog_SquadSetup so it drops into the Arm my Colony flow without feeling like a
    // different tool.
    //
    // Deliberately no SCORED weapons of any kind here (sidearms, combat grenades) - those are Weapon
    // Loadouts now, attached to a pawn/Role the same generic way Equipment Packs are (see
    // MultiParentUtility), so there is exactly one place that owns weapon+ammo scoring/filtering instead of
    // a second, weaker copy. Equipment Packs are pure supply: medicine, food, drugs, shields, and utility
    // weapons (binoculars, smoke/EMP-only/flashbang grenades - IsWeapon technically, but never scored and
    // never competing for a Weapon Loadout's "best weapon" slot, see EquipmentPacks.CategoryOf) - split into
    // small, focused kits by purpose rather than one grab-bag. NOT backpacks/vests/bandoliers - vanilla's
    // own Apparel Policy already manages those with its own working auto-equip; there's nothing left for a
    // Loadout to add on top.
    //
    // Cheap/Expensive and Lightweight/Heavy drive every plan's ITEM picks; Essential/All drives which
    // templates default to checked (TemplateOrder, most essential first). Every item stays fully editable
    // afterwards (change the def, change the count, drop it) - the sliders only ever set the STARTING
    // point, never overwriting a plan/checkbox the player has touched (matches the "Locked" idea from the
    // Weapon Loadouts auto-generate table). Re-running this after packs already exist refines them in place
    // (same name -> contents replaced, not duplicated).
    public class Dialog_GenerateEquipmentPack : Window
    {
        // Sourced from the shared LoadoutUtility.TexPin (2026-08-01, first piece of the cross-tab
        // generate-preview icon language) - was an independent ContentFinder.Get of the identical texture.
        private static Texture2D PinTex => LoadoutUtility.TexPin;
        private readonly Dialog_GenerateLoadouts loadoutsDialog;
        private readonly Action<Loadout> onGenerated;
        private readonly Action<int> onClosed;
        private readonly Action onBack;
        private readonly bool skipExisting;
        private bool committed;
        private int committedCount;
        private bool wentBack;

        private float costSlider = 0.35f;    // 0 = Cheap .. 1 = Expensive
        private float weightSlider = 0.5f;   // 0 = Lightweight .. 1 = Heavy
        private bool useAnyTypes = true;     // always pick CE's "Any Medicine"/"Any Meal"/"Any Drug" generics
        // Simple/Detailed toggle - same control, same default-hidden convention as the Weapon Loadouts
        // popup (Dialog_SquadSetup): Simple shows just the preset buttons, Detailed unfolds every slider.
        private bool advancedOpen = false;

        private class PackItem
        {
            public Def def;
            public int count;
            public string countBuf;
            public Func<List<Def>> pool;   // options offered when the player clicks to change this item
            // Only meaningful when def is a MadeFromStuff ThingDef (e.g. a shield) - null for everything
            // else. Purely a cost-preview choice: CE's own LoadoutSlot has no per-slot stuff concept (a
            // pawn's actual crafted item picks its material at production time, same as any other bill),
            // so this only feeds PackMaterialCosts' ingredient total, never gets baked into the Loadout.
            public ThingDef stuff;
            // Anzeige-Cache (2026-08-29, perf-packwizard-draw-cost): LabelCap.ToString().Truncate(...) lief pro
            // Zeile pro Durchgang - Truncate misst den Text in einer Schleife und legt dabei Zeichenketten an.
            // Ungueltig, sobald der Def oder die verfuegbare Breite wechselt (beides wird beim Lesen geprueft).
            public string labelCache;
            public Def labelCacheDef;
            public float labelCacheW = -1f;
            // Verfuegbarkeits-Abschnitte (2026-09-06, perf-packwizard-draw-time - gemessen: 20 ms/s bei
            // 2600 Aufrufen/s). BuildAvailabilitySegments fragt je Aufruf Bestand am Boden und gesperrten
            // Bestand ab; die Toepfe dahinter (ComputeItemOwnership) sind ohnehin 0,3 s alt. Dieselbe Frist.
            public LoadoutUtility.AvailabilitySegments segCache;
            public Def segCacheDef;
            public float segCacheTime = -99f;
        }

        private class PackPlan
        {
            public string name;
            public bool include;
            public bool exists;
            // Zur Loeschung vorgemerkt (2026-08-12): bestehende Packs kennen Pin / Loeschen / Bearbeitet,
            // ueberall gleich. Nur fuer `exists`-Zeilen
            // erreichbar; ein Pack, das es noch gar nicht gibt, kann man nicht loeschen.
            public bool delete;
            // Was `include` sagte, BEVOR der Muelleimer angeklickt wurde (2026-09-04). Ohne diese Zahl war
            // der Umschalter einseitig: Pin -> Muelleimer setzt include auf false, Muelleimer -> Pin nahm
            // das nie zurueck. Siehe die ausfuehrliche Begruendung am Umschalter.
            public bool includeBeforeDelete = true;
            public bool edited;   // once true, sliders/checkbox stop touching this plan's items (matches
                                  // the Weapon Loadouts auto-generate table's row-lock idea)
            public bool includeTouched;   // once true, the Essential<->All slider stops touching the checkbox too
            // True only when `items` currently holds this plan's REAL current Loadout content (read back via
            // ItemsFromLoadout in Upsert below), as opposed to `exists` alone, which just means a real Loadout
            // by this name exists - a real pack can still exist with zero items ItemsFromLoadout can classify,
            // in which case `items` falls back to the generic slider recommendation and should keep tracking
            // sliders like a brand-new plan would (2026-08-01).
            public bool seededReal;
            public List<PackItem> items = new List<PackItem>();
            // Zeichen-Cache (2026-08-29, perf-packwizard-draw-cost): Materialkosten (Woerterbuch + Rezeptzutaten je
            // Gegenstand) und Masse/Sperrigkeit wurden pro Zeile in JEDEM Durchgang neu gerechnet - gemessen 0,55 ms
            // je Zeichnen und 6-10 MB/s bei 130 Bildern, obwohl RecomputeIfNeeded die Liste laengst nur noch 4x je
            // Sekunde neu baut. uiDirty setzt jeder Pfad, der `items` anfasst (Upsert, Edit, die Auswahlmenues);
            // gelesen und geloescht wird es einmal je Zeile in DrawRow.
            public bool uiDirty = true;
            public Dictionary<ThingDef, int> costsCache;
            public float massCache, bulkCache;
        }

        // Every template's place in the Essential<->All coverage order (lower = more essential, always
        // covered first) - Base Supply is what nearly every loadout ends up carrying; the rest are
        // progressively more specialized. Order, not name matching, is what the slider steps through.
        // Combat Drugs Kit sits last on purpose: go-juice/yayo/etc. is a deliberate, situational choice (drug
        // dependency risk, not "everyday supply" like the rest), so it should never be handed out by default
        // just because the slider sits at its normal Balanced position - only an explicit high coverage
        // setting or a manual checkbox tick includes it.
        private static readonly string[] TemplateOrder = { "Base Supply", "Medic Kit", "Shield Kit", "Spotter", "Tactical Kit", "EMP Kit", "Combat Drugs Kit" };
        private static int TemplateRank(string name) { int i = Array.IndexOf(TemplateOrder, name); return i < 0 ? TemplateOrder.Length : i; }

        // 0 = only the most essential template (Base Supply); 1 = every template. Sets each UN-TOUCHED
        // checkbox's default every frame (like the other sliders' live preview) - once a player clicks a
        // checkbox by hand it stops listening to this slider, same "Locked" idea as edited items.
        private float coverageSlider = 0.4f;

        // Per-template checkbox state, keyed by name so it survives the every-frame Recompute() below.
        private readonly Dictionary<string, bool> includeState = new Dictionary<string, bool>();
        private readonly List<PackPlan> plans = new List<PackPlan>();
        private Vector2 scroll;
        // Fussleisten-Cache (2026-08-29, perf-packwizard-draw-cost): AllEquipmentPacks().Count() lief pro Durchgang
        // ueber alle Loadouts der Kolonie, und die beiden Beschriftungen mit Platzhalter wurden pro Durchgang neu
        // formatiert. Der Zaehler zieht mit Recompute nach (hoechstens 250 ms alt), die Texte an ihrer Zahl.
        private int existingPackCountCache;
        private string delLabelCache; private int delLabelCacheCount = -1; private float delWCache;
        private string genLabelCache; private int genLabelChecked = -1, genLabelDeleted = -1;

        // 1208f -> 1138f (2026-08-01), matching MaterialsW's own 230->160 shrink below - the window doesn't
        // need to carry width nothing actually uses.
        public override Vector2 InitialSize => new Vector2(Mathf.Min(1138f, UI.screenWidth - 40f), Mathf.Min(1080f, UI.screenHeight - 30f));
        // Nicht unter RimWorlds Lernhilfe schieben (2026-08-19) - siehe
        // LoadoutUtility.KeepClearOfLearningReadout. Ohne Kollision aendert das nichts.
        protected override void SetInitialSizeAndPosition()
        {
            base.SetInitialSizeAndPosition();
            windowRect = LoadoutUtility.KeepClearOfLearningReadout(windowRect);
        }

        // onClosed/skipExisting exist for Arm My Colony's own automated flow, which needs this popup to sit
        // in a bigger step chain: unlike the weapon-loadout step, Equipment Packs are optional supply, so
        // closing this WITHOUT generating anything is not an abort - onClosed always fires on close (count
        // 0 if nothing was actually generated) and the chain just continues to the next step either way.
        // skipExisting starts already-existing packs UNCHECKED (only fill the gap) instead of every
        // template defaulting to checked.
        public Dialog_GenerateEquipmentPack(Dialog_GenerateLoadouts loadoutsDialog, Action<Loadout> onGenerated = null,
            Action<int> onClosed = null, bool skipExisting = false, Action onBack = null) : base(new ReadoutAwareWindowDrawing())
        {
            this.loadoutsDialog = loadoutsDialog;
            this.onGenerated = onGenerated;
            this.onClosed = onClosed;
            this.skipExisting = skipExisting;
            this.onBack = onBack;
            doCloseX = true;
            forcePause = true;
            absorbInputAroundWindow = true;
        }

        public override void PostClose()
        {
            base.PostClose();
            if (wentBack) onBack?.Invoke();
            else onClosed?.Invoke(committed ? committedCount : 0);
        }

        // Default* methods on Dialog_GenerateLoadouts want "how thrifty" (higher = cheaper-biased); our own
        // slider reads left-to-right as Cheap->Expensive, so it is the mirror image of that.
        private float Econ => 1f - costSlider;

        public override void DoWindowContents(Rect inRect)
        {
            RecomputeIfNeeded();

            Text.Font = GameFont.Medium;
            Widgets.Label(new Rect(0, 0, inRect.width, 30f), "CER_GenerateEquipmentPack_Title".Translate());
            Text.Font = GameFont.Small;
            GUI.color = new Color(1f, 1f, 1f, 0.6f);
            Widgets.Label(new Rect(0, 30f, inRect.width, 34f), "CER_GenerateEquipmentPack_Intro".Translate());
            GUI.color = Color.white;

            float y = 68f;

            // "Use recommended setup" + presets: always visible, the one-click path - same convention as
            // the Weapon Loadouts popup (Dialog_SquadSetup).
            // Der Knopf waechst mit seiner Beschriftung, statt sie zu verkleinern (2026-09-03,
            // auf Deutsch stand "Empfohlenes Setup verwenden" in kleiner
            // Schrift neben vier Voreinstellungen in normaler - genau die gemischten Groessen, die der
            // Abgleich ausschliessen soll). Die Voreinstellungen daneben messen sich seit je selbst; hier
            // stand eine feste Breite von 190, und 190 reicht dem englischen Text, sonst keinem.
            string recLbl = "CER_GenerateEquipmentPack_RecommendedBtn".Translate().ToString();
            float recW = Mathf.Max(190f, Text.CalcSize(recLbl).x + 20f);
            if (LoadoutUtility.ButtonTextFit(new Rect(0, y, recW, 26f), recLbl)) UseRecommended();
            TooltipHandler.TipRegion(new Rect(0, y, recW, 26f), "CER_GenerateEquipmentPack_RecommendedTip".Translate());
            float px = recW + 14f;
            foreach (string presetKey in PresetKeys)
            {
                string presetLabel = PresetLabel(presetKey);
                float pw = Text.CalcSize(presetLabel).x + 20f;
                if (Widgets.ButtonText(new Rect(px, y, pw, 26f), presetLabel)) ApplyPreset(presetKey);
                px += pw + 6f;
            }
            LoadoutUtility.ModeDetailToggle(new Rect(inRect.width - LoadoutUtility.ModeDetailToggleW, y - 2f, LoadoutUtility.ModeDetailToggleW, LoadoutUtility.ModeDetailToggleW),
                ref advancedOpen,
                "CER_GenerateEquipmentPack_DetailedTip".Translate(),
                "CER_GenerateEquipmentPack_SimpleTip".Translate());
            y += 34f;

            // "Any X" IMMER sichtbar, nicht erst hinter dem Detail-Schalter (2026-08-25: der Schalter gehoert
            // in die schlanke Ansicht, nicht in den erweiterten Filter). Gleiche Begruendung wie beim
            // Rollen-Schalter im Assign-Fenster: eine
            // Grundsatzentscheidung ("konkrete Sorte oder Gattung?") gehoert in Sichtweite, die drei
            // Feinregler darunter duerfen eingeklappt bleiben.
            // Ueber CheckboxLabeledFit (2026-09-03): im festen
            // 220-Punkte-Feld brach die deutsche Beschriftung auf zwei Zeilen um und wurde oben UND unten
            // angeschnitten. Rechts daneben steht bis zum Detail-Schalter nichts, das Feld darf also
            // mitwachsen.
            bool anyTypes = useAnyTypes;
            Rect anyTypesRect = LoadoutUtility.CheckboxLabeledFit(new Rect(0, y, 220f, 24f),
                "CER_GenerateEquipmentPack_UseAnyTypesLabel".Translate(), ref anyTypes,
                Mathf.Max(220f, inRect.width - LoadoutUtility.ModeDetailToggleW - 20f));
            if (anyTypes != useAnyTypes) useAnyTypes = anyTypes;
            TooltipHandler.TipRegion(anyTypesRect, "CER_GenerateEquipmentPack_UseAnyTypesTip".Translate());
            y += 30f;

            if (advancedOpen)
            {
                y = DrawSlider(inRect, y, "CER_GenerateEquipmentPack_CheapLabel".Translate(), "CER_GenerateEquipmentPack_ExpensiveLabel".Translate(), ref costSlider,
                    "CER_GenerateEquipmentPack_CostTip".Translate());
                y = DrawSlider(inRect, y, "CER_GenerateEquipmentPack_LightweightLabel".Translate(), "CER_GenerateEquipmentPack_HeavyLabel".Translate(), ref weightSlider,
                    "CER_GenerateEquipmentPack_WeightTip".Translate());
                y = DrawSlider(inRect, y, "CER_GenerateEquipmentPack_EssentialLabel".Translate(), "CER_GenerateEquipmentPack_AllLabel".Translate(), ref coverageSlider,
                    "CER_GenerateEquipmentPack_CoverageTip".Translate());
                y += 6f;
            }
            Widgets.DrawLineHorizontal(0, y, inRect.width);
            y += 10f;

            Rect listRect = new Rect(0, y, inRect.width, inRect.height - y - LoadoutUtility.BottomBarH);
            DrawPlanList(listRect);

            // Bottom bar: Back (if part of the Arm-my-Colony chain) + Cancel (always) + Skip (Arm-my-Colony
            // chain only) + the primary action - same layout convention Dialog_SquadSetup's own popups use
            // (LoadoutUtility.BottomBarH/BottomBtnH/CloseBtnW/PrimaryBtnW), so this step doesn't stand out
            // with a bottom bar that behaves differently from the rest of the flow.
            // Alle Beschriftungen der Fussleiste zuerst, dann EINE Schriftstufe fuer die ganze Reihe
            // (2026-09-04, dieselbe Fundstellenklasse wie in der Auftragsuebersicht: dort standen die linken
            // Knoepfe gross und die rechten klein, weil jeder fuer sich gemessen hat). Der Zaehlerlauf und der
            // Beschriftungs-Cache des Primaerknopfes sind dafuer hierher gewandert - die Reihenstufe braucht
            // seinen Text, bevor irgendetwas gezeichnet wird.
            // Excludes plans GenerateChecked() would itself skip as empty (2026-08-01 fix) - a plan can be
            // ticked but have every item manually deleted, which used to still count toward this button's
            // own "Generate N" label even though GenerateChecked() silently produces nothing for it.
            // Zum LOESCHEN markierte Packs zaehlen mit (2026-08-15, Review-Befund). Das Loeschen setzt
            // `include` auf false, also fielen sie aus dieser Zahl heraus: markierte man drei Packs zum
            // Loeschen und sonst nichts, stand auf dem Knopf "Weiter" - und der Klick hat die drei Packs
            // geloescht. Ein Knopf, der zerstoert, muss das vorher sagen.
            int checkedCount = 0, deleteCount = 0;
            for (int pi = 0; pi < plans.Count; pi++)
            {
                if (plans[pi].include && plans[pi].items.Count > 0) checkedCount++;
                if (plans[pi].delete) deleteCount++;
            }
            if (genLabelCache == null || genLabelChecked != checkedCount || genLabelDeleted != deleteCount)
            {
                genLabelCache =
                      checkedCount > 0 && deleteCount > 0 ? "CER_GenerateEquipmentPack_GenerateAndDeleteBtn".Translate(checkedCount.ToString(), deleteCount.ToString()).ToString()
                    : deleteCount > 0 ? "CER_GenerateEquipmentPack_DeleteOnlyBtn".Translate(deleteCount.ToString()).ToString()
                    : checkedCount > 0 ? "CER_GenerateEquipmentPack_GenerateBtn".Translate(checkedCount.ToString()).ToString()
                    : "CER_GenerateEquipmentPack_ContinueBtn".Translate().ToString();
                genLabelChecked = checkedCount;
                genLabelDeleted = deleteCount;
            }
            string genLabel = genLabelCache;
            string epBackLbl = "CER_GenerateEquipmentPack_BackBtn".Translate().ToString();
            string epCancelLbl = "CER_GenerateEquipmentPack_CancelBtn".Translate().ToString();
            string epSkipLbl = "CER_GenerateEquipmentPack_SkipBtn".Translate().ToString();
            int epExistingCount = existingPackCountCache;
            if (epExistingCount > 0 && (delLabelCache == null || delLabelCacheCount != epExistingCount))
            {
                delLabelCache = "CER_GenerateEquipmentPack_DeleteExistingButton".Translate(epExistingCount.ToString()).ToString();
                // In Small gemessen, nicht in der zufaellig gesetzten Schrift - der Wert ist gecacht und
                // haette sonst je nach erstem Durchgang eine andere Breite behalten.
                delWCache = LoadoutUtility.BottomBtnW(delLabelCache, 0f, 9999f) + 6f;
                delLabelCacheCount = epExistingCount;
            }
            var epCells = new List<(float width, string label, bool icon)>();
            if (onBack != null) epCells.Add((100f, epBackLbl, false));
            epCells.Add((LoadoutUtility.CloseBtnW, epCancelLbl, false));
            if (epExistingCount > 0) epCells.Add((delWCache, delLabelCache, false));
            if (onBack != null) epCells.Add((LoadoutUtility.SkipBtnW, epSkipLbl, false));
            epCells.Add((LoadoutUtility.PrimaryBtnW, genLabel, false));
            GameFont prevEpBarFont = Text.Font;
            Text.Font = LoadoutUtility.BottomBarFont(epCells.ToArray());

            float cancelEndX;
            if (onBack != null)
            {
                Rect backRect = new Rect(0f, inRect.height - LoadoutUtility.BottomBarH, 100f, LoadoutUtility.BottomBtnH);
                if (LoadoutUtility.ButtonTextFit(backRect, epBackLbl))
                {
                    wentBack = true;
                    Close();
                }
                TooltipHandler.TipRegion(backRect, "CER_GenerateEquipmentPack_BackTip".Translate());
                Rect cancelRect = new Rect(108f, inRect.height - LoadoutUtility.BottomBarH, LoadoutUtility.CloseBtnW, LoadoutUtility.BottomBtnH);
                if (LoadoutUtility.ButtonTextFit(cancelRect, epCancelLbl)) Close();
                cancelEndX = cancelRect.xMax;
            }
            else
            {
                if (LoadoutUtility.BottomCloseButton(inRect, epCancelLbl)) Close();
                cancelEndX = LoadoutUtility.CloseBtnW;
            }

            // "Delete existing equipment packs" - right of Cancel, matching the equivalent bottom-bar button
            // added to the Weapon Loadouts generate screen (2026-08-01). Only shown when at
            // least one already exists.
            {
                int existingPackCount = epExistingCount;   // siehe Recompute - nicht je Durchgang zaehlen
                if (existingPackCount > 0)
                {
                    string delLabel = delLabelCache;
                    float delW = delWCache;
                    Rect delRect = new Rect(cancelEndX + 10f, inRect.height - LoadoutUtility.BottomBarH, delW, LoadoutUtility.BottomBtnH);
                    // ButtonTextFit statt Widgets.ButtonText (2026-09-04): der rohe Aufruf misst nicht und
                    // ignorierte die Schriftstufe der Reihe.
                    if (LoadoutUtility.ButtonTextFit(delRect, delLabel))
                    {
                        string confirmMsg = existingPackCount == 1
                            ? "CER_GenerateEquipmentPack_DeleteExistingConfirmSingular".Translate(existingPackCount.ToString())
                            : "CER_GenerateEquipmentPack_DeleteExistingConfirmPlural".Translate(existingPackCount.ToString());
                        Find.WindowStack.Add(LoadoutUtility.CompactConfirmation(
                            confirmMsg,
                            () =>
                            {
                                LoadoutDeletionUtility.DeleteAll(EquipmentPackUtility.AllEquipmentPacks());
                                // Full wipe, not a partial refresh: an untouched PackPlan's own `exists` flag
                                // is only ever set once (Upsert, on first creation) and Recompute() never
                                // revisits it - clearing lets next frame's Recompute() rebuild every plan fresh
                                // via Upsert, so a just-deleted pack correctly reads as "new" again (mirrors
                                // Dialog_SquadSetup's own genPlan.Clear() after its equivalent delete-all).
                                plans.Clear();
                                string deletedMsg = existingPackCount == 1
                                    ? "CER_GenerateEquipmentPack_DeletedExistingSingular".Translate(existingPackCount.ToString())
                                    : "CER_GenerateEquipmentPack_DeletedExistingPlural".Translate(existingPackCount.ToString());
                                Messages.Message(deletedMsg, MessageTypeDefOf.TaskCompletion, false);
                            },
                            destructive: true));
                    }
                    TooltipHandler.TipRegion(delRect, "CER_GenerateEquipmentPack_DeleteExistingTip".Translate());
                }
            }

            // Always clickable, even at 0 checked - unticking every template is a valid choice ("I don't want
            // any Equipment Packs this run"), not a disabled state. GenerateChecked() with nothing checked is
            // already a safe no-op, so this just advances the Arm-my-Colony flow without creating anything.
            // Excludes plans GenerateChecked() would itself skip as empty (2026-08-01 fix) - a plan can be
            // ticked but have every item manually deleted, which used to still count toward this button's
            // own "Generate N" label even though GenerateChecked() silently produces nothing for it.
            // Zum LOESCHEN markierte Packs zaehlen mit (2026-08-15, Review-Befund). Das Loeschen setzt
            // `include` auf false, also fielen sie aus dieser Zahl heraus: markierte man drei Packs zum
            // Loeschen und sonst nichts, stand auf dem Knopf "Weiter" - und der Klick hat die drei Packs
            // geloescht. Ein Knopf, der zerstoert, muss das vorher sagen.
            float xr = inRect.width;
            Rect genRect = new Rect(xr - LoadoutUtility.PrimaryBtnW, inRect.height - LoadoutUtility.BottomBarH, LoadoutUtility.PrimaryBtnW, LoadoutUtility.BottomBtnH);
            xr -= LoadoutUtility.PrimaryBtnW + 10f;
            // Only shown mid-Arm-my-Colony (onBack != null is the same "are we in that chain" signal the
            // Back button above already uses) - a standalone open from the Equipment Packs tab has nothing
            // to "skip" to. Drawn AFTER the primary action so it lands immediately to its LEFT, not its right -
            // this is the "move on, generate nothing here" choice, distinct from Cancel (which reads as
            // aborting) and from ticking nothing then pressing the primary button (same net effect, just
            // less discoverable).
            if (onBack != null && LoadoutUtility.BottomActionButton(inRect, ref xr, LoadoutUtility.SkipBtnW, epSkipLbl,
                tip: "CER_GenerateEquipmentPack_SkipTip".Translate()))
            {
                Close();
            }
            bool genClicked = LoadoutUtility.ButtonTextFit(genRect, genLabel);
            Text.Font = prevEpBarFont;
            if (genClicked)
            {
                committed = true;
                committedCount = checkedCount;
                Loadout first = GenerateChecked();
                if (first != null) onGenerated?.Invoke(first);
                Close();
            }
        }

        // ================================================================================================
        // Presets & "Use recommended setup" - same one-click convention as Dialog_SquadSetup's own popups,
        // just tuned for this window's own 3 sliders (Cheap/Expensive, Lightweight/Heavy, Essential/All)
        // instead of the Weapon Loadouts assign/generate axes.
        // ================================================================================================
        // Schluessel bleiben englisch (sie steuern ApplyPreset), die BESCHRIFTUNG kommt aus Keyed
        // (2026-09-03: die vier Knoepfe waren als einzige in diesem Fenster unuebersetzt).
        private static readonly string[] PresetKeys = { "Balanced", "Economy", "Best Gear", "Essentials only" };

        private static string PresetLabel(string key)
        {
            switch (key)
            {
                case "Economy": return "CER_GenerateEquipmentPack_PresetEconomy".Translate().ToString();
                case "Best Gear": return "CER_GenerateEquipmentPack_PresetBestGear".Translate().ToString();
                case "Essentials only": return "CER_GenerateEquipmentPack_PresetEssentialsOnly".Translate().ToString();
                default: return "CER_GenerateEquipmentPack_PresetBalanced".Translate().ToString();
            }
        }

        private void UseRecommended()
        {
            costSlider = 0.35f; weightSlider = 0.5f; coverageSlider = 0.4f; useAnyTypes = true;
            ReleaseUnEditedSeeds();
        }

        private void ApplyPreset(string name)
        {
            switch (name)
            {
                case "Economy": costSlider = 0f; weightSlider = 0.25f; coverageSlider = 0.2f; break;
                case "Best Gear": costSlider = 1f; weightSlider = 0.8f; coverageSlider = 1f; break;
                case "Essentials only": costSlider = 0.35f; weightSlider = 0.4f; coverageSlider = 0f; break;
                default: costSlider = 0.35f; weightSlider = 0.5f; coverageSlider = 0.4f; break;   // Balanced
            }
            useAnyTypes = true;
            ReleaseUnEditedSeeds();
        }

        // seededReal (see Upsert's own comment) protects an existing pack's REAL content from being silently
        // overwritten on the VERY NEXT Recompute() call after this dialog first opens/reopens (e.g. via Back
        // navigation) - but it never got CLEARED afterward, so it ended up protecting that same content
        // FOREVER, for the rest of the dialog session, even against an explicit "recompute everything" ask
        // like clicking a different preset (2026-08-01: unchecking a pinned existing pack's
        // protection then switching preset never changed its shown contents at all, which was not
        // logical). Clicking a preset/Recommended button IS exactly that explicit ask - release every
        // plan the player hasn't personally hand-edited so it goes back to tracking the live slider output,
        // same as a plan that never existed as a real pack already does. A plan the player DID edit
        // (p.edited) stays protected regardless - that's a deliberate personal choice, not stale seed data.
        // Seit 2026-08-25 loest derselbe Klick auch die HAKEN wieder (vorher blieben manuell ein-/
        // ausgeschaltete Packs haengen, auch wenn man oben auf Balanced / Economy etc. klickte).
        // Ursache: der Klick auf einen Haken setzt
        // includeTouched, und Recompute laesst eine beruehrte Zeile bewusst in Ruhe - richtig gegen den
        // Essential<->All-Regler (sonst koennte man keinen Haken halten, waehrend man zieht), aber ein
        // Preset ist die ausdrueckliche Bitte "stell alles neu ein", und die hat bisher nur den Inhalt
        // neu gerechnet, nie die Auswahl. Freigegeben wird dieselbe Menge wie beim Inhalt: alles, was
        // nicht von Hand bearbeitet ist - und zusaetzlich nichts, was zum Loeschen vorgemerkt ist, denn
        // ein Muelleimer ist genauso eine bewusste Entscheidung wie eine Bearbeitung.
        // includeState muss mit raus: Upsert liest es beim naechsten Aufbau zurueck und wuerde den
        // alten Haken sonst still wiederherstellen.
        private void ReleaseUnEditedSeeds()
        {
            recomputeDirty = true;   // ein Preset ist die ausdrueckliche Bitte um Neuberechnung - siehe RecomputeIfNeeded
            foreach (PackPlan p in plans)
            {
                if (p.edited) continue;
                p.seededReal = false;
                if (p.delete) continue;
                p.includeTouched = false;
                includeState.Remove(p.name);
            }
        }

        private float DrawSlider(Rect inRect, float y, string left, string right, ref float val, string tip)
        {
            const float lblW = 110f;
            Rect row = new Rect(0, y, inRect.width, 24f);
            Text.Anchor = TextAnchor.MiddleRight;
            Widgets.Label(new Rect(row.x, row.y, lblW, 24f), left);
            Text.Anchor = TextAnchor.MiddleLeft;
            Widgets.Label(new Rect(row.xMax - lblW, row.y, lblW, 24f), right);
            Text.Anchor = TextAnchor.UpperLeft;
            Rect sr = new Rect(row.x + lblW + 10f, row.y + 3f, row.width - 2f * lblW - 20f, 18f);
            val = Widgets.HorizontalSlider(sr, val, 0f, 1f, true);
            TooltipHandler.TipRegion(row, tip);
            return y + 28f;
        }

        // ---- Plan list rendering ---------------------------------------------------------------------------
        // Compact rows, not big vertical cards - matches the flat table look of the Weapon Loadouts and
        // Assign Loadouts lists elsewhere in the mod (checkbox, name, inline contents, right-aligned
        // stats) instead of a visually different "card" style for this one popup. A row is normally one
        // line tall and grows to fit more lines only when a pack has more than one item - the same
        // pattern Weapon Loadouts already uses for a weapon with more than one ammo type.

        // RowGapH 8 -> 4 (2026-08-12: zu viel Zeilenabstand).
        // Bewusst NUR der Zwischenraum: RowLineH bleibt bei 26, weil die Inhaltszeile daraus als
        // RowLineH - 2 (also 24px) gezeichnet wird - daran zu drehen haette den Inhalt geschrumpft statt
        // den Abstand. Dieselbe Aenderung im Schwester-Popup (Dialog_SquadSetup.RowH).
        private const float RowLineH = 26f, RowGapH = 4f;
        private const float ItemsColX = 190f;
        // Mass/Bulk is a short fixed text; Materials needs real room for several ingredient icon+count
        // chips (LoadoutUtility.DrawTotalCostLine) - it replaced a single abstract silver-cost number,
        // which named a price but never said what would actually have to be crafted. Availability (2026-07-30)
        // is its OWN column now, before Materials - it used to be crammed inline at the end of each item's own
        // row content, which read as part of "Contents" rather than a real column of its own.
        // MaterialsW was 230f (2026-08-01: a big empty gap between Materials and Mass/Bulk) -
        // DrawIngredientCell advances 25px per icon chip, so 230f reserved room for ~9 distinct materials;
        // PackMaterialCosts aggregates recipe ingredients across a pack's own items (1-3 items per template,
        // each needing 1-3 raw materials from CE's own small crafting-material pool - Steel/Components/Cloth/
        // Chemfuel/Leather/...), realistically topping out well under half that. 160f comfortably fits ~6
        // chips, matching the window's own InitialSize shrink below.
        private const float MassBulkW = 190f, MaterialsW = 160f, AvailColW = 140f;
        private const float StatsColW = AvailColW + MaterialsW + MassBulkW;

        // The "+" add-item button now lives in the last item's own row (far right) instead of a dedicated
        // row underneath - one line per actual item, or exactly one line for an empty pack (still needs
        // somewhere to put the "+").
        private float MeasureRow(PackPlan p) => Mathf.Max(p.items.Count, 1) * RowLineH + RowGapH;

        private void DrawPlanList(Rect rect)
        {
            // No boxed panel background (Widgets.DrawMenuSection) - matches the plain, direct-on-window-
            // background look Dialog_SquadSetup's own Weapon Loadouts/Assign Loadouts lists already use
            // (just a header + a divider line, no enclosing box), so this popup doesn't stick out with a
            // different background shade next to the other two Arm-my-Colony steps.
            Rect inner = rect;

            // Matches the row content's own coordinate space (viewRect.width = inner.width - 16 for the
            // scrollbar gutter), so the header sits exactly over what it labels instead of drifting right.
            float rowSpaceW = inner.width - 16f;
            Text.Font = GameFont.Tiny;
            GUI.color = new Color(1f, 1f, 1f, 0.55f);
            Widgets.Label(new Rect(inner.x + 24f, inner.y, 160f, 18f), "CER_GenerateEquipmentPack_ColHeaderPack".Translate());
            Widgets.Label(new Rect(inner.x + ItemsColX, inner.y, 300f, 18f), "CER_GenerateEquipmentPack_ColHeaderContents".Translate());
            Widgets.Label(new Rect(inner.x + rowSpaceW - StatsColW, inner.y, AvailColW - 10f, 18f), "CER_GenerateEquipmentPack_ColHeaderAvailability".Translate());
            Widgets.Label(new Rect(inner.x + rowSpaceW - StatsColW + AvailColW, inner.y, MaterialsW, 18f), "CER_GenerateEquipmentPack_ColHeaderMaterials".Translate());
            Widgets.Label(new Rect(inner.x + rowSpaceW - StatsColW + AvailColW + MaterialsW + 10f, inner.y, MassBulkW - 10f, 18f), "CER_GenerateEquipmentPack_ColHeaderMassBulk".Translate());
            GUI.color = Color.white;
            Text.Font = GameFont.Small;
            float headerH = 20f;
            Widgets.DrawLineHorizontal(inner.x, inner.y + headerH, inner.width);

            Rect listArea = new Rect(inner.x, inner.y + headerH + 4f, inner.width, inner.height - headerH - 4f);
            float viewH = 4f;
            foreach (PackPlan p in plans) viewH += MeasureRow(p);
            Rect viewRect = new Rect(0, 0, listArea.width - 16f, Mathf.Max(viewH, listArea.height));
            Widgets.BeginScrollView(listArea, ref scroll, viewRect);
            float y = 4f;
            foreach (PackPlan p in plans) y = DrawRow(p, y, viewRect.width);
            Widgets.EndScrollView();
        }

        private float DrawRow(PackPlan p, float y, float width)
        {
            float rowH = MeasureRow(p);
            Rect full = new Rect(0, y, width, rowH - RowGapH);
            if (Mouse.IsOver(full)) Widgets.DrawHighlight(full);

            // Same look and feel as Weapon Loadouts' own "pinned, protected from re-plan" rows: an existing
            // pack left alone by Filling-gaps mode shows a PIN icon instead of a plain unchecked box - a
            // plain X read identically to "the slider just doesn't want this one", which isn't what's
            // actually happening (Recompute's own !p.includeTouched gate is what protects it).
            // A padlock used to sit here, but a padlock reads as "nothing here can be touched" everywhere
            // else in software - which was actively wrong, since every field below was always editable
            // anyway, and any edit was then silently DISCARDED because the row stayed excluded from Apply
            // (2026-07-31: just as confusing after comparing to Weapon Loadouts, where an
            // edit to a pinned row DOES apply). PIN is the right metaphor: "stays put by default, but pick
            // it up any time" - exactly what editing a field below now does (see Edit() local below).
            bool protectedSkip = skipExisting && p.exists && !p.includeTouched && !p.include;
            Rect chkRect = new Rect(0f, y + 3f, 20f, 20f);
            // BESTEHENDE Packs haben jetzt denselben Zustands-Satz wie die Weapon-Loadouts-Zeile daneben
            // (2026-08-12: ein gruener Haken ergibt wenig Sinn, weil das Pack schon angelegt ist; beide
            // sollen GENAU GLEICH funktionieren): Pin = bleibt wie es ist,
            // Muelleimer = wird geloescht, Stift = Inhalt oder Menge wurden veraendert. Der Klick schaltet
            // zwischen Pin und Muelleimer hin und her - der Pin kommt also wieder, was vorher nicht ging
            // (einmal auf den Haken geklickt, und die Zeile blieb fuer immer im Haken/X-Zyklus).
            // Der Stift ist ein ANZEIGE-Zustand, kein Klickziel: er erscheint von selbst, sobald Edit()
            // gelaufen ist, und verschwindet nicht, solange die Aenderung ansteht.
            if (p.exists)
            {
                Texture2D stateTex = p.delete ? LoadoutUtility.TexTrash : (p.edited ? LoadoutUtility.TexEditPencil : PinTex);
                GUI.color = p.delete ? LoadoutUtility.ColBad : (p.edited ? Color.white : LoadoutUtility.ColPin);
                if (LoadoutUtility.StateIconButton(chkRect, stateTex, GUI.color))
                {
                    // ====== Der Umschalter muss in BEIDE Richtungen gleich viel tun (2026-09-04) ======
                    //
                    // Befund im Test: Jedes Hin-und-Zurueck an EINER Zeile zog eine Zeile vom Zaehler ab,
                    // ohne dass sich am Bild etwas aenderte. Am Ende stand der Assistent auf 'keine Packs',
                    // obwohl er drei angehakte Packs anzeigte.
                    //
                    // Der Mechanismus: Pin -> Muelleimer setzte `include` auf false,
                    // Muelleimer -> Pin nahm es nicht zurueck. Das SYMBOL haengt an `delete` und sah danach
                    // wieder aus wie vorher; der Knopf unten zaehlt aber `include`. Anzeige und Zaehler
                    // liefen also bei jedem Klickpaar um eins auseinander - unsichtbar, bis am Ende
                    // "Continue (no packs)" dastand.
                    //
                    // Ein Umschalter, der beim Hinweg mehr aendert als beim Rueckweg, ist immer ein Fehler.
                    // Deshalb wird der vorherige Zustand gemerkt und beim Zurueckschalten wiederhergestellt.
                    p.delete = !p.delete;
                    if (p.delete)
                    {
                        p.includeBeforeDelete = p.include;
                        p.include = false;
                    }
                    else
                    {
                        p.include = p.includeBeforeDelete;
                    }
                    p.includeTouched = true;
                    includeState[p.name] = p.include;
                }
                GUI.color = Color.white;
                // Nur bauen, wenn die Maus darueber steht (2026-08-29, perf-packwizard-draw-cost): jede dieser
                // Zeilen formatiert einen Text mit Platzhalter - pro Zeile, pro Durchgang, fuer einen Tooltip,
                // den TipRegion ohnehin nur bei Mouseover zeigt.
                if (LoadoutUtility.TipWanted(chkRect)) TooltipHandler.TipRegion(chkRect, p.delete
                    ? "CER_GenerateEquipmentPack_StateDeleteTip".Translate(p.name)
                    : (p.edited ? "CER_GenerateEquipmentPack_StateEditedTip".Translate(p.name)
                                : "CER_GenerateEquipmentPack_StatePinnedTip".Translate(p.name)));
            }
            else if (protectedSkip)
            {
                GUI.color = LoadoutUtility.ColPin;
                // Un-pinning (2026-08-01, same fix as the preset buttons - see ReleaseUnEditedSeeds' own
                // comment): releasing THIS one row's seededReal so it responds to the current sliders again,
                // scoped to just this plan - not the same as clicking a preset, which is an explicit
                // "recompute everything unedited" ask; picking up ONE pin shouldn't also unfreeze every
                // OTHER still-pinned pack's own real content.
                if (LoadoutUtility.StateIconButton(chkRect, PinTex, GUI.color)) { p.include = true; p.includeTouched = true; includeState[p.name] = true; p.seededReal = false; }
                GUI.color = Color.white;
                if (LoadoutUtility.TipWanted(chkRect)) TooltipHandler.TipRegion(chkRect, "CER_GenerateEquipmentPack_ProtectedTip".Translate(p.name));
            }
            else
            {
                bool inc = p.include;
                Widgets.Checkbox(chkRect.position, ref inc, 20f);
                if (inc != p.include) { p.include = inc; p.includeTouched = true; includeState[p.name] = inc; }
                // Der NOCH NICHT bestehende Fall war der einzige ohne Erklaerung (2026-08-28 im Selbsttest
                // gefunden): Pin, Muelleimer und Stift haben je einen Tooltip, das rote X daneben hatte gar
                // keinen - ausgerechnet das Symbol, das am ehesten nach "hier stimmt etwas nicht" aussieht.
                if (LoadoutUtility.TipWanted(chkRect)) TooltipHandler.TipRegion(chkRect, inc
                    ? "CER_GenerateEquipmentPack_StateWillCreateTip".Translate(p.name)
                    : "CER_GenerateEquipmentPack_StateWontCreateTip".Translate(p.name));
            }

            // Every edit below routes through this instead of setting p.edited directly, so a pinned
            // (protectedSkip) row's edit ALWAYS takes effect - touching any field is itself the "include it"
            // action, no separate unlock click required (matches Weapon Loadouts' MarkEdited).
            void Edit()
            {
                p.uiDirty = true;
                p.edited = true;
                // IMMER aufnehmen, nicht nur aus dem gepinnten Zustand heraus (2026-08-12: eine Aenderung
                // bewirkte nichts). Genau hier lag es: die Aufnahme haing an
                // `protectedSkip`, und das ist nur wahr, solange "bestehende ueberspringen" AN ist UND die
                // Zeile unberuehrt war. Steht der Schalter aus, oder hat man die Zeile vorher einmal
                // angefasst, blieb `include` false - die Bearbeitung wurde beim Anwenden stillschweigend
                // uebersprungen. Eine Bearbeitung IST die Absicht, dieses Pack zu schreiben.
                p.include = true;
                p.includeTouched = true;
                includeState[p.name] = true;
                // Eine Bearbeitung hebt eine vorgemerkte Loeschung auf - sonst editiert man etwas, das im
                // selben Durchgang geloescht wird.
                p.delete = false;
            }

            // Kein zweites Symbol mehr (2026-08-12: nur den ZUSTAND zeigen). Hier war es besonders
            // redundant: dieser Tab kennt
            // gar kein Loeschen (canDelete war fest false), das Glyph konnte also nur "+" / "~" / "x" sagen -
            // und ob ein Pack neu ist oder aktualisiert wird, steht ohnehin in der Zeile selbst. Der Haken
            // links traegt den Zustand allein.
            Widgets.Label(new Rect(42f, y, ItemsColX - 46f, RowLineH), p.name);

            float itemsW = width - ItemsColX - StatsColW - 8f;
            float iy = y;
            PackItem toRemove = null;
            Rect lastDelRect = default;
            for (int itemIdx = 0; itemIdx < p.items.Count; itemIdx++)
            {
                PackItem item = p.items[itemIdx];
                Rect row = new Rect(ItemsColX, iy, itemsW, RowLineH - 2f);
                DrawItemIcon(new Rect(row.x, row.y + 2f, 20f, 20f), item.def);

                bool needsStuff = item.def is ThingDef stuffTd && stuffTd.MadeFromStuff;

                // Material picker (icon-only, no text label) sits right after the item's own icon, BEFORE
                // the name - reads naturally as "[steel icon] Buckler", matching how a MadeFromStuff item's
                // own generated label already prefixes the stuff name. Was a wide icon+text button squeezed
                // between Count and Delete/+ instead (2026-07-30: cramped, and
                // pushed those controls around unpredictably row to row depending on the material's name
                // length).
                float nameStartX = row.x + 24f;
                if (needsStuff)
                {
                    ThingDef stuffOwner = (ThingDef)item.def;
                    Rect stuffRect = new Rect(nameStartX, row.y + 1f, 22f, 22f);
                    bool stuffClicked = LoadoutUtility.StuffButton(stuffRect, item.stuff);
                    if (stuffClicked)
                    {
                        Edit();
                        List<ThingDef> stuffs = GenStuff.AllowedStuffsFor(stuffOwner).ToList();
                        List<FloatMenuOption> stuffOpts = stuffs.Select(s =>
                        {
                            ThingDef local = s;
                            FloatMenuOption opt = new FloatMenuOption(local.LabelCap, () => { item.stuff = local; p.uiDirty = true; }, local);
                            int have = LoadoutUtility.IngredientStockCount(local);
                            string inStockTip = "CER_GenerateEquipmentPack_InStockTip".Translate(have.ToString());
                            opt.mouseoverGuiAction = r => TooltipHandler.TipRegion(r, new TipSignal(inStockTip, local.GetHashCode() ^ 8511));
                            return opt;
                        }).ToList();
                        Find.WindowStack.Add(new FloatMenu(stuffOpts));
                    }
                    if (LoadoutUtility.TipWanted(stuffRect)) TooltipHandler.TipRegion(stuffRect, item.stuff != null
                        ? "CER_GenerateEquipmentPack_MaterialTipWithStuff".Translate(item.stuff.LabelCap.ToString())
                        : "CER_GenerateEquipmentPack_MaterialTipNoStuff".Translate());
                    nameStartX = stuffRect.xMax + 4f;
                }

                // A plain, compact label - not a big stretched button - matches the Weapon Loadouts
                // popup's own row style (icon + name, no oversized clickable box). Still click-to-change
                // via an invisible button over the same rect.
                // The name column's RIGHT edge is fixed (row.x + 24 icon + 220 max width) regardless of
                // whether a stuff-icon pushed its start further right - only the start and therefore the
                // available width shrink for a stuffable item, never the end. This is what lets Count (and
                // everything after it) sit at one fixed column for every row (2026-07-31:
                // Count visibly misaligned on the Pavise/Shield-Kit row, which has a stuff icon).
                float nameEndX = row.x + 24f + 220f;
                Rect nameRect = new Rect(nameStartX, row.y + 1f, Mathf.Max(20f, Mathf.Min(nameEndX - nameStartX, itemsW - (nameStartX - row.x) - 66f)), 22f);
                Text.Anchor = TextAnchor.MiddleLeft;
                if (item.labelCache == null || item.labelCacheDef != item.def || Mathf.Abs(item.labelCacheW - nameRect.width) > 0.5f)
                {
                    item.labelCache = item.def?.LabelCap.ToString().Truncate(nameRect.width - 4f) ?? "CER_GenerateEquipmentPack_UnknownMark".Translate().ToString();
                    item.labelCacheDef = item.def;
                    item.labelCacheW = nameRect.width;
                }
                Widgets.Label(nameRect, item.labelCache);
                Text.Anchor = TextAnchor.UpperLeft;
                // Ein Eintrag ohne Pool (aus einem Pack zurueckgelesene "sonstige" Sache, siehe
                // ItemsFromLoadout) laesst sich nicht tauschen - dann auch nicht als klickbar anbieten.
                // Sonst kaeme ein leeres FloatMenu, und ein leeres Menue ist schlimmer als kein Menue.
                bool switchable = item.pool != null;
                if (switchable) Widgets.DrawHighlightIfMouseover(nameRect);
                if (switchable && Widgets.ButtonInvisible(nameRect))
                {
                    // Mark the plan edited THE MOMENT the menu opens, not only once a pick lands: Recompute()
                    // rebuilds every un-edited plan's `items` with brand-new PackItem objects every single
                    // frame, and the parent window keeps rendering (rebuilding) underneath while a FloatMenu
                    // is open on top of it. Deferring `p.edited = true` to the option's own callback left a
                    // window where an automatic rebuild could replace this exact `item` object with a fresh
                    // one before the click landed - the pick would then mutate an orphaned object nobody
                    // still referenced, silently doing nothing (the first click "didn't take").
                    Edit();
                    List<Def> options = item.pool != null ? item.pool() : new List<Def>();
                    // Same duplicate guard as the "+" add-item menu (2026-08-01 fix) - excludes a def already
                    // used by ANOTHER item in this pack (but not this item's own current def, so re-picking
                    // the same entry still shows up), otherwise switching to it here created a second PackItem
                    // row sharing a def with an existing one, same as the add-item bug.
                    List<FloatMenuOption> opts = options
                        .Where(d => !p.items.Any(pi => pi != item && pi.def == d))
                        .Select(d =>
                        {
                            Def local = d;
                            return DefOption(local, local.LabelCap, () => { item.def = local; item.stuff = DefaultStuffFor(local); p.uiDirty = true; });
                        }).ToList();
                    Find.WindowStack.Add(new FloatMenu(opts));
                }
                if (LoadoutUtility.TipWanted(nameRect)) TooltipHandler.TipRegion(nameRect, switchable
                    ? "CER_GenerateEquipmentPack_ChangeItemTip".Translate()
                    : "CER_GenerateEquipmentPack_KeptItemTip".Translate());

                // Count gets its own fixed column, anchored to nameEndX (the name column's fixed right
                // edge, above) rather than to nameRect.xMax - this matches how Availability/Materials are
                // already their own dedicated columns instead of trailing a variable-width field.
                Rect cntRect = new Rect(nameEndX + 6f, row.y, 42f, 22f);
                int val = item.count;
                string buf = item.countBuf;
                // Echtes Eingabefeld nur bei Maus ueber der Zeile oder Fokus - siehe LoadoutUtility.NumericFieldLazy.
                LoadoutUtility.NumericFieldLazy(cntRect, "CER_pack_" + p.name + "_" + itemIdx, Mouse.IsOver(full), ref val, ref buf, 0f, 99999f);
                item.countBuf = buf;
                // Dragging the count down to 0 removes the item outright, same as the explicit X button
                // (2026-08-01 fix, matches Dialog_Ingredients' identical guard) - a 0-count item used to just
                // sit there as a phantom LoadoutSlot once generated. Reuses the same `toRemove`/Edit() the X
                // button below already routes through, so removal is handled once, after the item loop.
                if (val != item.count)
                {
                    if (val <= 0) toRemove = item;
                    else item.count = val;
                    Edit();
                }

                Rect delRect = new Rect(cntRect.xMax + 6f, row.y, 18f, 18f);
                if (Widgets.ButtonText(delRect, "CER_GenerateEquipmentPack_DeleteBtn".Translate())) toRemove = item;
                // Clear of every other clickable element in the row (name/stuff-picker/count/delete all sit
                // to its LEFT with their own gaps already) - fixes both the "+ overlaps the material picker"
                // report and the "clicking + actually clicks the row underneath" misclick, since there is
                // nothing else drawn in this space for it to collide with.
                if (itemIdx == p.items.Count - 1) lastDelRect = delRect;

                // Per-item availability (2026-07-30 harmonization) - colony-wide ownership for this exact
                // item, same shared bar as everywhere else. Its OWN dedicated column now (before Materials),
                // not crammed inline after the item's own controls - matches Dialog_Ingredients' identical
                // use of ComputeItemOwnership for the same kind of item (medicine/food/drug/shield/utility).
                // No pawn context - a pack isn't tied to one colonist here, just a template.
                if (item.def != null)
                {
                    Rect availRect = new Rect(width - StatsColW, row.y, AvailColW - 10f, RowLineH - 4f);
                    if (item.segCacheDef != item.def || Time.realtimeSinceStartup - item.segCacheTime > 0.3f)
                    {
                        LoadoutUtility.ComputeItemOwnership(item.def, out int coOnly, out int coAndAssigned, out int coAssignedInStore, out int coFreeStored, out int coInProd);
                        item.segCache = LoadoutUtility.BuildAvailabilitySegments(coOnly, coAndAssigned, coAssignedInStore, coFreeStored, coInProd, def: item.def);
                        item.segCacheDef = item.def;
                        item.segCacheTime = Time.realtimeSinceStartup;
                    }
                    var itemSeg = item.segCache;
                    // Same "In storage:/Carried by:/Assigned to:" breakdown Weapon Loadouts' own availability
                    // bar already gives, not just a bare Total/Available count (2026-07-31:
                    // wanted to see WHICH concrete food and WHERE, same detail level as elsewhere).
                    // TipWanted: der Orts-Tooltip laeuft ueber alle Karten - nur bauen, wenn er auch gezeigt wird.
                    LoadoutUtility.DrawAvailabilityBar(availRect, itemSeg, horizontal: true,
                        LoadoutUtility.TipWanted(availRect) ? LoadoutUtility.BuildPackItemLocationTooltip(item.def) : null);
                }

                iy += RowLineH;
            }
            if (toRemove != null) { p.items.Remove(toRemove); Edit(); }

            // The "+" sits right after the LAST item's own delete (X) button, with a clear gap - guaranteed
            // empty space nothing else in the row is ever drawn into, so it can never overlap or steal a
            // click from the name/material/count/delete controls (2026-07-30, fixed after two rounds of user
            // reports: first it sat flush against the column's far right edge disconnected from the row;
            // then, positioned just before the count field, it overlapped the material/stuff picker for
            // shield-type items and a click there sometimes hit the row's own "change item" button instead).
            // One combined menu across every category (a pack can already mix medicine/food/drugs/shield/
            // utility freely, e.g. Base Supply itself has both meal and medicine), each option labeled with
            // its category so the flat list stays navigable.
            float addRowY = p.items.Count > 0 ? iy - RowLineH : iy;
            float addX;
            if (p.items.Count > 0)
            {
                addX = lastDelRect.xMax + 6f;
            }
            else
            {
                float virtualNameW = Mathf.Min(220f, itemsW - 24f - 66f);
                float virtualDelXMax = ItemsColX + 24f + virtualNameW + 6f + 42f + 6f + 18f;
                addX = virtualDelXMax + 6f;
            }
            // Same size and vertical alignment as the row's own delete (X) button (18x18, row.y baseline) -
            // used to be 24x20 offset by +1f, so the two sat visibly mismatched side by side
            // (2026-07-31).
            Rect addRect = new Rect(addX, addRowY, 18f, 18f);
            if (LoadoutUtility.TipWanted(addRect)) TooltipHandler.TipRegion(addRect, "CER_GenerateEquipmentPack_AddItemTip".Translate());
            if (Widgets.ButtonText(addRect, "CER_GenerateEquipmentPack_AddBtn".Translate()))
            {
                // Same race-condition guard as the "change item" FloatMenu above: mark edited THE MOMENT the
                // menu opens, before Recompute()'s every-frame rebuild of un-edited plans can run again.
                Edit();
                List<FloatMenuOption> opts = new List<FloatMenuOption>();
                void AddCategory(string label, Func<List<Def>> pool)
                {
                    foreach (Def d in pool())
                    {
                        // Skip a def already present in this pack (2026-08-01 fix) - adding the same def twice
                        // created two separate PackItem rows for one ThingDef, which GenerateChecked() then
                        // turned into two independent LoadoutSlots for the same def in one real Loadout.
                        if (p.items.Any(pi => pi.def == d)) continue;
                        Def local = d;
                        opts.Add(DefOption(local, "CER_GenerateEquipmentPack_CategoryOption".Translate(label, local.LabelCap.ToString()), () => { p.items.Add(MakeItem(local, 1, pool)); p.uiDirty = true; }));
                    }
                }
                AddCategory("CER_GenerateEquipmentPack_CategoryMedicine".Translate(), MedicinePool);
                AddCategory("CER_GenerateEquipmentPack_CategoryFood".Translate(), FoodPool);
                AddCategory("CER_GenerateEquipmentPack_CategoryDrug".Translate(), DrugPool);
                AddCategory("CER_GenerateEquipmentPack_CategoryShield".Translate(), ShieldPool);
                AddCategory("CER_GenerateEquipmentPack_CategoryUtility".Translate(), UtilityWeaponPoolAsDefs);
                Find.WindowStack.Add(new FloatMenu(opts));
            }
            if (p.items.Count == 0) iy += RowLineH;   // reserve the one row this pack's list still needs

            // Kosten und Masse/Sperrigkeit aus dem Zeilen-Cache (siehe PackPlan.uiDirty) - neu nur, wenn sich
            // der Inhalt wirklich geaendert hat, nicht 260 Mal je Sekunde.
            if (p.uiDirty || p.costsCache == null)
            {
                p.costsCache = PackMaterialCosts(p);
                ComputeMassBulk(p, out p.massCache, out p.bulkCache);
                p.uiDirty = false;
            }
            float totalMass = p.massCache, totalBulk = p.bulkCache;
            float statsY = y + (rowH - RowGapH - 20f) / 2f;
            // Materials starts AFTER the Availability column, matching the header label's own formula
            // (rowSpaceW - StatsColW + AvailColW) - this used to be missing the "+ AvailColW" offset, so the
            // materials cost chips were drawn directly on top of the per-item availability bars instead of in
            // their own column (2026-07-30: materials and availability overlapped).
            float statsX = width - StatsColW + AvailColW;
            // What crafting this pack would actually consume, not an abstract silver price - same ingredient-
            // chip strip the produce popups use, red where storage can't cover it.
            // Ohne Mengen: 160px Spalte, siehe DrawTotalCostLine. Die Zahlen stehen im Tooltip der Zelle.
            LoadoutUtility.DrawTotalCostLine(statsX, statsY - 3f, MaterialsW, p.costsCache, "", labelW: 6f, showCounts: false);
            // Side by side, not stacked - this column has the width to spare (matches the Sidearm/Pack
            // picker's own row convention) and a pack row is rarely tall enough to want two lines here.
            LoadoutUtility.DrawMassBulkBarsHorizontal(new Rect(statsX + MaterialsW + 10f, y + (rowH - RowGapH - 22f) / 2f, MassBulkW - 10f, 22f), totalMass, totalBulk);

            return y + rowH;
        }

        // Raw ingredients this pack's checked items would actually consume if crafted, summed across every
        // item and scaled to full batches - what LoadoutUtility.DrawTotalCostLine renders as chips. Generics
        // ("Any Medicine", ...) have no recipe of their own to cost out (they resolve to whatever's on hand
        // at pickup time), so they're skipped here rather than guessing one representative's recipe.
        private static Dictionary<ThingDef, int> PackMaterialCosts(PackPlan p)
        {
            var costs = new Dictionary<ThingDef, int>();
            foreach (PackItem item in p.items)
            {
                if (!(item.def is ThingDef td)) continue;
                RecipeDef r = LoadoutUtility.GetRecipeFor(td);
                if (r == null) continue;
                int yield = r.products != null && r.products.Count > 0 ? Mathf.Max(1, r.products[0].count) : 1;
                int batches = Mathf.CeilToInt((float)item.count / yield);
                foreach (ThingDefCountClass ing in LoadoutUtility.GetRecipeIngredients(r, item.stuff))
                    if (ing?.thingDef != null)
                        costs[ing.thingDef] = (costs.TryGetValue(ing.thingDef, out int c) ? c : 0) + ing.count * batches;
            }
            return costs;
        }

        // Masse/Sperrigkeit eines Plans - einmal je Inhaltsaenderung (siehe PackPlan.uiDirty). BulkStat ist
        // LoadoutUtilitys aufgeloester StatDef, statt DefDatabase.GetNamed je Zeile je Durchgang.
        private static void ComputeMassBulk(PackPlan p, out float mass, out float bulk)
        {
            mass = 0f; bulk = 0f;
            StatDef bulkStat = LoadoutUtility.BulkStat;
            foreach (PackItem item in p.items)
            {
                if (item.def is ThingDef td)
                {
                    mass += td.GetStatValueAbstract(StatDefOf.Mass) * item.count;
                    if (bulkStat != null) bulk += td.GetStatValueAbstract(bulkStat) * item.count;
                }
                else if (item.def is LoadoutGenericDef gd)
                {
                    mass += gd.mass * item.count;
                    bulk += gd.bulk * item.count;
                }
            }
        }

        // Internal (not private) so Dialog_SquadSetup's Role-derived read-only Pack/Sidearm display can reuse
        // the exact same icon convention instead of duplicating it (2026-07-30).
        internal static void DrawItemIcon(Rect r, Def def)
        {
            if (def is ThingDef td) { Widgets.ThingIcon(r, td); return; }
            if (def is LoadoutGenericDef gd)
            {
                Texture2D badge = GenericIcons.For(gd);
                if (badge != null) GUI.DrawTexture(r, badge);
            }
        }

        // Same icon rule as DrawItemIcon's row icons: a concrete ThingDef shows its own icon, a "Any X"
        // generic gets its own hand-drawn category badge (GenericIcons) instead of a borrowed real item's
        // icon - so the FloatMenu a player picks from already looks like the row it is about to create.
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

        // ---- Plan recomputation -----------------------------------------------------------------------------

        // Rebuilds every UN-edited plan from the current slider values. A plan the player has touched
        // (def/count/removal) is marked `edited` and is skipped here from then on, exactly like a "Locked"
        // row in the Weapon Loadouts auto-generate table.
        //
        // NICHT MEHR IN JEDEM BILD (2026-08-25: Flackern bei laufendem Luefter; die
        // Messzeile darin: Dialog_GenerateEquipmentPack 140 ms/s bei 140 Bildern/s, 0.41 ms je LAYOUT-Durchgang
        // und 3 MB/s neu belegt). Das war dieser Aufruf: bis heute in JEDEM Durchgang jedes Bildes sieben
        // Gegenstandslisten neu gebaut - der alte Kommentar ("cheap enough ... every frame") stammt aus einer
        // Zeit mit einem Durchgang bei 60 Bildern; bei 143 Hz und zwei Durchgaengen sind es rund 300 Laeufe
        // pro Sekunde fuer ein Bild, das stillsteht. RecomputeIfNeeded rechnet, wenn sich eine Eingabe
        // geaendert hat (Regler, Any-X, Zahl der Plaene, die zwei Vorgaben aus den Mod-Einstellungen), wenn es
        // jemand ausdruecklich verlangt (Preset, Empfehlung - recomputeDirty) - und sonst hoechstens viermal
        // je Sekunde, damit alles, was hier nicht aufgezaehlt ist (Pin geloest, Pack in einem anderen Fenster
        // geloescht, ein vergessener Pfad), spaetestens nach einer Viertelsekunde nachzieht. Recompute ist
        // idempotent (Upsert laesst Bearbeitetes und Geschuetztes in Ruhe): ein Lauf zu viel kostet nur Zeit,
        // ein Lauf zu wenig hoechstens 250 ms Verzoegerung.
        private bool recomputeDirty = true;
        private float lastRecomputeAt = -1f;
        private (float cost, float weight, float coverage, bool anyTypes, int plans, string med, string meal) lastRecomputeKey;

        private void RecomputeIfNeeded()
        {
            var key = (costSlider, weightSlider, coverageSlider, useAnyTypes, plans.Count,
                CEReloadedMod.settings?.defMedicineDefName, CEReloadedMod.settings?.defMealDefName);
            float now = Time.realtimeSinceStartup;
            if (!recomputeDirty && key.Equals(lastRecomputeKey) && now - lastRecomputeAt < 0.25f) return;
            recomputeDirty = false;
            lastRecomputeKey = key;
            lastRecomputeAt = now;
            Recompute();
        }

        private void Recompute()
        {
            existingPackCountCache = EquipmentPackUtility.AllEquipmentPacks().Count();   // fuer die Fussleiste, siehe dort
            // Same order as TemplateOrder (most essential first, Combat Drugs Kit deliberately last) - this
            // is also the row DISPLAY order, a separate thing from the coverage-slider ranking.
            Upsert("Base Supply", BuildBaseSupplyItems());
            Upsert("Medic Kit", BuildMedicKitItems());
            Upsert("Shield Kit", BuildShieldKitItems());
            Upsert("Spotter", BuildSpotterItems());
            Upsert("Tactical Kit", BuildTacticalKitItems());
            Upsert("EMP Kit", BuildEmpKitItems());
            Upsert("Combat Drugs Kit", BuildCombatDrugsKitItems());

            // Essential<->All: how many of the templates above (in TemplateOrder) default to checked, most
            // essential first - only for a plan the player hasn't clicked themselves.
            int includeCount = Mathf.Max(1, Mathf.RoundToInt(coverageSlider * TemplateOrder.Length));
            foreach (PackPlan p in plans)
                if (!p.includeTouched)
                    p.include = TemplateRank(p.name) < includeCount && !(skipExisting && p.exists);
        }

        private void Upsert(string name, List<PackItem> freshItems)
        {
            PackPlan existing = plans.FirstOrDefault(p => p.name == name);
            if (existing != null)
            {
                // existing.seededReal (this plan's items currently hold a real pack's ACTUAL content, read
                // back via ItemsFromLoadout below on this plan's FIRST Upsert) must gate this exactly like
                // existing.edited already does - not just on creation. Recompute() calls Upsert() again every
                // single frame the window is open, and without this check an existing-but-untouched plan's
                // real, seeded content got silently overwritten back to the generic slider recommendation on
                // the very next frame (edited was still false), even though the row visually looked
                // untouched. GenerateChecked() then wrote THAT generic content over the pack's real slots on
                // Generate - an edit nobody made in this session, silently discarded (2026-08-01, confirmed a
                // prior investigation's hypothesis). A plan that doesn't exist yet, or exists but has nothing
                // ItemsFromLoadout could read back, has no real content to protect, so it keeps tracking the
                // sliders live until edited, same as before.
                if (!existing.edited && !existing.seededReal)
                {
                    // Was der Schieberegler gar nicht kennt, ueberlebt jede Neuberechnung (2026-08-09).
                    // ItemsFromLoadout liest solche Sachen jetzt mit
                    // (pool == null), aber das allein haette nur den ERSTEN Frame geschuetzt: sobald
                    // ReleaseUnEditedSeeds das seededReal loescht - und das tut jeder Preset-Klick -, wuerde
                    // freshItems die ganze Liste ersetzen und die von Hand eingelegte Munition waere wieder
                    // weg. Ein Preset-Klick heisst "rechne die EMPFEHLUNG neu", nicht "wirf weg, wozu die
                    // Empfehlung gar keine Meinung hat".
                    List<PackItem> keptExtras = existing.items.Where(i => i.pool == null).ToList();
                    existing.items = freshItems;
                    existing.uiDirty = true;
                    foreach (PackItem k in keptExtras)
                        if (!existing.items.Any(i => i.def == k.def)) existing.items.Add(k);
                }
                return;
            }
            if (freshItems.Count == 0) return;
            var plan = new PackPlan { name = name, items = freshItems };
            string fullLabel = EquipmentPackUtility.PackMarkerPrefix + " " + name;
            // Feste Kennung statt Namenstext (2026-08-09) - siehe FindGeneratedPackForPlan.
            Loadout realLoadout = LoadoutUtility.FindGeneratedPackForPlan(name, fullLabel);
            plan.exists = realLoadout != null;
            if (plan.exists)
            {
                // Show what the pack REALLY contains right now, not the slider's fresh recommendation - a
                // brand-new dialog instance (e.g. re-opened via "< Back" after Generate) used to always show
                // the generic default again for an already-existing pack, making a just-made edit look
                // reverted even though the real Loadout still had it (2026-07-31: Assign
                // Loadouts correctly showed the edited content, this popup did not). The slider-driven
                // recommendation only makes sense for a pack that doesn't exist yet.
                List<PackItem> realItems = ItemsFromLoadout(realLoadout);
                if (realItems.Count > 0) { plan.items = realItems; plan.seededReal = true; }
            }
            if (includeState.TryGetValue(name, out bool inc)) { plan.include = inc; plan.includeTouched = true; }
            plans.Add(plan);
        }

        // Reads a real, already-generated pack's current contents back into editable PackItems - the pool
        // (for the "click to change" menu) is inferred from the item's own category, same classification
        // EquipmentPackUtility already uses to group pack contents everywhere else. Stuff choice can't be
        // recovered (CE's own LoadoutSlot has no per-slot stuff concept, see PackItem.stuff's own comment) -
        // re-defaulted the same way a freshly-added item would be.
        private List<PackItem> ItemsFromLoadout(Loadout lo)
        {
            var result = new List<PackItem>();
            if (lo?.OwnSlots == null) return result;
            foreach (LoadoutSlot s in lo.OwnSlots)
            {
                Def d = s.thingDef != null ? (Def)s.thingDef : s.genericDef;
                if (d == null) continue;
                Func<List<Def>> pool;
                switch (EquipmentPackUtility.CategoryOf(d))
                {
                    case PackCategory.Medical: pool = MedicinePool; break;
                    case PackCategory.Food: pool = FoodPool; break;
                    case PackCategory.Drugs: pool = DrugPool; break;
                    case PackCategory.Utility:
                        pool = (d is ThingDef utd && LoadoutUtility.IsShieldApparel(utd)) ? (Func<List<Def>>)ShieldPool : UtilityWeaponPoolAsDefs;
                        break;
                    // Everything else (ammo, ordinary apparel, resources, tools, plain weapons) is kept with a
                    // null pool instead of being dropped (2026-08-09, option (a) of the
                    // alternatives). This function feeds a full clear-and-rewrite in
                    // GenerateChecked, so anything it fails to read back was DELETED from the pack: put 50
                    // rounds into a generated Medic Pack via the CE loadout editor, run the generator again,
                    // and the ammo was silently gone. There is no item pool this popup could offer to switch
                    // such a slot to - that is exactly why it used to be skipped - but "we cannot offer you
                    // alternatives" was never a reason to throw the slot away. A null pool means the row shows
                    // and counts and can be deleted like any other, it just cannot be swapped for something
                    // else; the click handler and the tooltip both branch on that.
                    default: pool = null; break;
                }
                result.Add(MakeItem(d, s.count, pool));
            }
            return result;
        }

        private static PackItem MakeItem(Def def, int count, Func<List<Def>> pool) =>
            new PackItem { def = def, count = count, countBuf = count.ToString(), pool = pool, stuff = DefaultStuffFor(def) };

        // Same "Steel if allowed, else whatever's first" convention Dialog_SupplyLoadouts' own weapon
        // rows already use for their material picker - null for anything that isn't MadeFromStuff (most
        // pack contents: medicine, food, drugs, utility gear - only a shield typically qualifies here).
        private static ThingDef DefaultStuffFor(Def def)
        {
            if (!(def is ThingDef td) || !td.MadeFromStuff) return null;
            List<ThingDef> stuffs = GenStuff.AllowedStuffsFor(td).ToList();
            return stuffs.FirstOrDefault(s => s == ThingDefOf.Steel) ?? stuffs.FirstOrDefault();
        }

        // ---- Templates ----------------------------------------------------------------------------------------

        // Everyone's baseline: food + medicine, no combat content - this is the pack that ends up attached to
        // almost every weapon loadout or role, regardless of what else they carry.
        private List<PackItem> BuildBaseSupplyItems()
        {
            var items = new List<PackItem>();
            Def food = PickFoodDef();
            Def med = PickMedicineDef();
            if (food != null) items.Add(MakeItem(food, ScaleCount(MealBaseCount), FoodPool));
            if (med != null) items.Add(MakeItem(med, ScaleCount(MedicineBaseCount), MedicinePool));
            return items;
        }

        // Dedicated healer: medicine only, and more of it than the baseline pack carries.
        private List<PackItem> BuildMedicKitItems()
        {
            var items = new List<PackItem>();
            Def med = PickMedicineDef();
            // Doppelte Grundmenge, nicht die feste 4 von frueher - so bleibt "der Sanitaeterkasten traegt
            // mehr als das Grundpack" auch dann wahr, wenn die Grundmenge in den Einstellungen gedreht wird.
            if (med != null) items.Add(MakeItem(med, ScaleCount(MedicineBaseCount * 2), MedicinePool));
            return items;
        }

        // Shield only - mobility vs. maximum protection follows the weight slider, same trade-off the Weapon
        // Loadouts tab's own shield picks make. Backpacks/vests/webbing are NOT here - vanilla's own Apparel
        // Policy already manages those (see LoadoutUtility.IsUtilityApparel); there's nothing for a pack to add.
        private List<PackItem> BuildShieldKitItems()
        {
            var items = new List<PackItem>();
            bool mobile = weightSlider < 0.5f;
            ThingDef shield = loadoutsDialog?.DefaultShield(Econ, mobile);
            if (shield != null) items.Add(MakeItem(shield, 1, ShieldPool));
            return items;
        }

        // Utility weapons (binoculars, smoke/EMP-only/flashbang grenades, ...) are IsWeapon technically, but
        // never scored (GetWeaponScore floors them at -1) and never compete for a Weapon Loadout's "best
        // weapon" slot, so they read as plain unscored inventory here instead (see EquipmentPacks.
        // CategoryOf). Split into small, focused kits by actual purpose rather than one grab-bag "Utility
        // Kit" - a spotter doesn't need smoke, and a breacher doesn't need binoculars.

        // The spotter half of an artillery-calling pair (binoculars) - Verb_MarkForArtillery is the precise,
        // mod-portable identifier (same check IsUtilityWeapon itself uses), not a label guess.
        private List<PackItem> BuildSpotterItems()
        {
            var items = new List<PackItem>();
            ThingDef binoculars = PickUtilityWeapon(w => w.Verbs != null
                && w.Verbs.Any(v => v.verbClass != null && v.verbClass.Name.Contains("MarkForArtillery")));
            if (binoculars != null) items.Add(MakeItem(binoculars, 1, UtilityWeaponPoolAsDefs));
            return items;
        }

        // The classic breach/entry pair - one smoke grenade (concealment) + one flashbang (blind/stun) -
        // distinct purpose from Spotter's artillery-marking or EMP Kit's anti-mech role.
        private List<PackItem> BuildTacticalKitItems()
        {
            var items = new List<PackItem>();
            ThingDef smoke = PickUtilityWeapon(w => LabelContains(w, "smoke grenade"));
            ThingDef flash = PickUtilityWeapon(w => LabelContains(w, "flashbang"));
            if (smoke != null) items.Add(MakeItem(smoke, 1, UtilityWeaponPoolAsDefs));
            if (flash != null) items.Add(MakeItem(flash, 1, UtilityWeaponPoolAsDefs));
            return items;
        }

        // EMP grenades - anti-mech/anti-electronics utility, the same Anti-flesh/Anti-mech theme the rest of
        // this mod already scores weapons and ammo against. Scales with the weight slider like a consumable
        // (carry more when Heavy), unlike Spotter's one-off tool.
        private List<PackItem> BuildEmpKitItems()
        {
            var items = new List<PackItem>();
            ThingDef emp = PickUtilityWeapon(w => LabelContains(w, "emp"));
            if (emp != null) items.Add(MakeItem(emp, ScaleCount(2), UtilityWeaponPoolAsDefs));
            return items;
        }

        // A combat drug (go-juice, Yayo, beer, ...) - the classic CE loadout choice for a melee bruiser or
        // anyone who needs a combat-stat boost, distinct from Medic Kit's healing-focused medicine.
        private List<PackItem> BuildCombatDrugsKitItems()
        {
            var items = new List<PackItem>();
            Def drug = PickDrugDef();
            if (drug != null) items.Add(MakeItem(drug, ScaleCount(3), DrugPool));
            return items;
        }

        // ---- Shared pickers -------------------------------------------------------------------------------------

        // NEUTRAL IN DER MITTE (2026-08-16): die Vorgabe (in dem Fall 2 und 3) soll gelten, wenn der
        // Regler in der Mitte steht - genau 50 % zwischen lightweight und heavy. Das ergibt am meisten
        // Sinn: die Mitte ist der neutrale Wert.
        // Vorher: Mathf.Lerp(0.5f, 2f, weightSlider). Eine GERADE zwischen 0,5 und 2 hat ihren neutralen
        // Punkt bei einem Drittel, nicht in der Mitte - in Mittelstellung kam deshalb der Faktor 1,25
        // heraus, und aus eingestellten 5/7 wurden 6/9. Der Regler hat den Wert also nie ausgelassen, er
        // hat ihn nur an der falschen Stelle getroffen; genau das wurde im Test gemessen.
        // Jetzt geometrisch statt linear: 2^((t-0,5)*2) ist bei 0 exakt die Haelfte, in der Mitte exakt 1
        // und bei 1 exakt das Doppelte. "Halb so viel" und "doppelt so viel" sind ohnehin die Sprache,
        // in der man ueber Mengen denkt - eine Gerade wuerde eines der beiden Enden verzerren.
        private int ScaleCount(int baseCount) => Mathf.Max(1, Mathf.RoundToInt(baseCount * Mathf.Pow(2f, (weightSlider - 0.5f) * 2f)));

        // No hardcoded defName guess for any of these three - LoadoutMedicineOptions/LoadoutFoodOptions/
        // LoadoutDrugOptions already discovered and classified every real LoadoutGenericDef the game
        // actually has (EnsureGenericSupplyDefs scans DefDatabase and checks IsMedicine/IsDrug/IsIngestible
        // on whatever each generic's own matcher accepts - it never assumes a specific defName). Medicine
        // and Food used to guess "GenericMedicine"/"GenericMeal" by exact name via FindGeneric; when that
        // string didn't match the real def, useAnyTypes silently fell back to a SPECIFIC concrete med/food
        // instead of the generic - a pack authored to want "Any Medicine" ended up requiring one exact
        // item, reading as "missing" even with the colony stocked on a different, perfectly good medicine.
        // Die vier Mod-Einstellungen "Standard-Mahlzeit / -Medizin" samt Mengen wirken seit 2026-08-14 GENAU
        // HIER und nirgends sonst (2026-08-14: aus dem Vorlagen-Assistenten raus, in den Einstellungen als
        // reiner Standard). Vorher waren sie
        // wirkungslos: der Waffen-Vorlagen-Assistent las sie in vier Felder ein, die niemand mehr benutzte.
        // Verpflegung gehoert zum Pack, nicht zur Waffenvorlage - damit kann sie auch nicht mehr doppelt
        // eingeplant werden.
        // Leerer Name heisst ausdruecklich "keins" (das ist die eigene Option des Einstellungs-Menues) und
        // laesst den Posten im Pack ganz weg. Ein Name, den es in dieser Modliste nicht gibt, faellt still
        // auf die bisherige Automatik zurueck - eine Einstellung aus einem anderen Modpack soll den
        // Pack-Assistenten nicht lahmlegen.
        private Def PickFromSettingOrAuto(string defName, List<Def> pool, out bool suppressed)
        {
            suppressed = false;
            if (defName != null && defName.Length == 0) { suppressed = true; return null; }
            if (!string.IsNullOrEmpty(defName))
            {
                Def chosen = pool.FirstOrDefault(d => d.defName == defName);
                // Ein GENERISCHER Vorgabewert unterliegt weiter dem Schalter "Any-Typen benutzen"
                // (2026-08-15, Review-Befund): ausgeliefert wird als Standard genau "GenericMeal" bzw.
                // "GenericMedicine", also traf der Vorgabewert immer und kam VOR dem Schalter - der Haken
                // war damit wirkungslos, das Pack bekam auch abgewaehlt "Any Meal". Ein KONKRETER
                // Vorgabewert (der Spieler hat eine bestimmte Mahlzeit gewaehlt) gewinnt weiterhin, der
                // ist ja die ausdruecklichere Ansage.
                if (chosen != null && !(chosen is LoadoutGenericDef && !useAnyTypes)) return chosen;
            }
            if (useAnyTypes) { Def g = pool.FirstOrDefault(d => d is LoadoutGenericDef); if (g != null) return g; }
            return PickByCost(pool.OfType<ThingDef>().ToList());
        }

        private Def PickMedicineDef()
            => PickFromSettingOrAuto(CEReloadedMod.settings?.defMedicineDefName, LoadoutUtility.LoadoutMedicineOptions(), out _);

        private Def PickFoodDef()
            => PickFromSettingOrAuto(CEReloadedMod.settings?.defMealDefName, LoadoutUtility.LoadoutFoodOptions(), out _);

        // Die eingestellten Stueckzahlen als GRUNDMENGE - der Gewichts-Regler skaliert sie weiterhin
        // (ScaleCount), und das Verhaeltnis zwischen den Vorlagen bleibt erhalten: der Sanitaeterkasten
        // traegt weiter das Doppelte des Grundpacks. Unlesbare oder unsinnige Eingaben fallen auf die
        // bisherigen Festwerte zurueck, damit ein vertipptes Feld keinen leeren Pack erzeugt.
        private static int SettingCount(string raw, int fallback)
            => int.TryParse((raw ?? "").Trim(), out int n) && n > 0 ? n : fallback;
        private static int MealBaseCount => SettingCount(CEReloadedMod.settings?.defMealCount, 2);
        private static int MedicineBaseCount => SettingCount(CEReloadedMod.settings?.defMedicineCount, 2);

        private Def PickDrugDef()
        {
            List<Def> pool = LoadoutUtility.LoadoutDrugOptions();
            if (useAnyTypes) { Def g = pool.FirstOrDefault(d => d is LoadoutGenericDef); if (g != null) return g; }
            return PickByCost(pool.OfType<ThingDef>().ToList());
        }

        private static List<Def> MedicinePool() => LoadoutUtility.LoadoutMedicineOptions();
        private static List<Def> FoodPool() => LoadoutUtility.LoadoutFoodOptions();
        private static List<Def> DrugPool() => LoadoutUtility.LoadoutDrugOptions();
        private List<Def> ShieldPool() => (loadoutsDialog?.ShieldPool() ?? new List<ThingDef>()).Cast<Def>().ToList();
        private List<ThingDef> UtilityWeaponPool() =>
            (loadoutsDialog?.AllWeaponsList ?? new List<ThingDef>()).Where(LoadoutUtility.IsUtilityWeapon).OrderBy(d => d.label).ToList();
        private List<Def> UtilityWeaponPoolAsDefs() => UtilityWeaponPool().Cast<Def>().ToList();

        private static bool LabelContains(ThingDef d, string needle) => d?.label != null && d.label.ToLowerInvariant().Contains(needle);

        // First owned-or-craftable utility weapon matching `match`, in pool order (alphabetical) - used by
        // the small focused utility kits (Spotter/Tactical/EMP), each picking one specific kind
        // rather than offering a cost/weight choice (there's no "cheap vs best" among binoculars).
        private ThingDef PickUtilityWeapon(Func<ThingDef, bool> match)
        {
            var ownedSet = loadoutsDialog?.AvailableWeaponsCache;
            foreach (ThingDef w in UtilityWeaponPool())
            {
                if (!match(w)) continue;
                bool own = ownedSet != null && ownedSet.Contains(w);
                if (own || (loadoutsDialog?.IsCraftable(w) ?? false)) return w;
            }
            return null;
        }

        // Continuous cheap<->expensive pick across a pool ranked by market value - the cost slider's position
        // maps straight onto an index in the sorted list, so dragging it slides smoothly through the options
        // instead of flipping between two fixed picks.
        private ThingDef PickByCost(List<ThingDef> pool)
        {
            if (pool == null || pool.Count == 0) return null;
            List<ThingDef> sorted = pool.OrderBy(d => d.BaseMarketValue).ToList();
            int idx = Mathf.Clamp(Mathf.RoundToInt(costSlider * (sorted.Count - 1)), 0, sorted.Count - 1);
            return sorted[idx];
        }

        // ---- Generation -----------------------------------------------------------------------------------------

        private Loadout GenerateChecked()
        {
            Loadout first = null;
            List<string> collisions = new List<string>();
            foreach (PackPlan plan in plans)
            {
                // Vorgemerkte Loeschung zuerst - derselbe Weg, den der "alle bestehenden Packs loeschen"-
                // Knopf oben schon nimmt (LoadoutDeletionUtility). Nur echte, von uns erzeugte Packs:
                // ein handgebautes Pack unter demselben Namen ist nicht unseres zum Loeschen, exakt wie die
                // Kollisionspruefung im Erzeugen-Zweig darunter.
                if (plan.delete)
                {
                    Loadout doomed = LoadoutUtility.FindGeneratedPackForPlan(plan.name,
                        EquipmentPackUtility.PackMarkerPrefix + " " + plan.name);
                    if (doomed != null && LoadoutUtility.IsGeneratedPack(doomed)) LoadoutDeletionUtility.Delete(doomed);
                    continue;
                }
                if (!plan.include || plan.items.Count == 0) continue;
                string fullLabel = EquipmentPackUtility.PackMarkerPrefix + " " + plan.name;
                // Feste Kennung statt Namenstext (2026-08-09) - siehe FindGeneratedPackForPlan. Wirkt auch auf
                // die Kollisionspruefung direkt darunter: ein umbenanntes eigenes Pack wird jetzt als das
                // eigene erkannt, statt als fremdes Pack unter demselben Namen missverstanden zu werden.
                Loadout pack = LoadoutUtility.FindGeneratedPackForPlan(plan.name, fullLabel);
                // Refuse a name collision with a pack the wizard did NOT create (2026-08-01,
                // mirrors the same-day Role name-collision fix) - an existing pack under this name that isn't
                // already tracked as wizard-generated is either hand-built or predates this tracking - either
                // way, silently wiping and relabeling it here would be exactly the "silent data loss/
                // reclassification" class already fixed for Roles. Skipped, not generated;
                // reported below. (The "ItemsFromLoadout can't even read Other-category content back" half of
                // the original reason is gone since 2026-08-09 - it reads and keeps those slots now - but the
                // refusal stands on its own: a hand-built pack is not ours to overwrite.)
                if (pack != null && !LoadoutUtility.IsGeneratedPack(pack))
                {
                    collisions.Add(plan.name);
                    continue;
                }
                if (pack == null)
                {
                    pack = new Loadout { defaultLoadout = false, label = fullLabel };
                    LoadoutManager.AddLoadout(pack);
                }
                else
                {
                    foreach (LoadoutSlot s in pack.OwnSlots.ToList()) pack.RemoveSlot(s);
                }
                // Kennung setzen (2026-08-17), fuer BEIDE Wege - frisch angelegt wie wiederverwendet. Beim
                // wiederverwendeten Pack ist es die Selbstheilung fuer Altbestand: es kannte seine Identitaet
                // bisher nicht, ab jetzt schon, und der "[Pack]"-Marker im Namen wird dafuer nicht mehr
                // gebraucht. NACH AddLoadout, weil die UniqueID erst dort entsteht.
                EquipmentPackUtility.MarkAsPack(pack);
                foreach (PackItem item in plan.items)
                {
                    if (item.def is ThingDef td) pack.AddSlot(new LoadoutSlot(td, item.count));
                    else if (item.def is LoadoutGenericDef gd) pack.AddSlot(new LoadoutSlot(gd, item.count));
                }
                // Tracks this as OUR output (2026-08-01, mirrors Dialog_GenerateLoadouts' own MarkAsManaged
                // at its template-creation site) - marked whether freshly created or an existing wizard-made
                // pack just got regenerated in place, so Dialog_ArmColony's "delete only generated packs"
                // scope can tell it apart from one hand-built in the Ingredients tab.
                LoadoutUtility.MarkAsManaged(pack);
                // Und die Zuordnung Plan -> dieses Pack festhalten, damit ein spaeteres Umbenennen sie nicht
                // mehr zerreisst (2026-08-09). MarkAsManaged allein sagt nur "von uns", nicht "zu welchem Plan".
                LoadoutUtility.RecordGeneratedPackForPlan(plan.name, pack);
                if (first == null) first = pack;
            }
            if (collisions.Count > 0)
                Messages.Message("CER_GenerateEquipmentPack_NameCollisionSkipped".Translate(string.Join(", ", collisions)), MessageTypeDefOf.RejectInput, false);
            return first;
        }
    }
}
