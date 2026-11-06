using System;
using System.Collections.Generic;
using System.Linq;
using RimWorld;
using UnityEngine;
using Verse;
using CombatExtended;

namespace CEReloaded
{
    // The "one press wonder": a short preview of where the colony stands, then a single button that generates the
    // loadouts you're missing, queues the ammo, auto-assigns by skill, and opens the produce popup for the shortfall.
    public class Dialog_ArmColony : Window
    {
        private readonly Dialog_GenerateLoadouts loadoutsDialog;
        private readonly Dialog_GenerateAmmoBills billsDialog;
        private readonly Dialog_AssignLoadouts assignDialog;

        private int colonists, unarmed, existingLoadouts, existingPacks;
        // Live "how many bills would this scope actually touch" counts for the two TriSwitchRow delete tables
        // below - computed once in Analyze() (bills can't change while this modal window is open), not per
        // frame. None is always 0 by definition and isn't stored.
        private int ammoBillsManagedCount, ammoBillsAllCount, weaponBillsManagedCount, weaponBillsAllCount;
        private int weaponLoadoutsManagedCount, weaponLoadoutsAllCount, equipmentPacksManagedCount, equipmentPacksAllCount;

        // Hard blockers that make arming physically impossible (see Preflight). Empty = good to go.
        private List<string> blockers = new List<string>();

        // Step switches. The three mode radios are only presets that set all of these at once, so the player can
        // fine-tune single rows right in the table. Placeholder initializers only - the constructor calls
        // ApplyMode(Normal) to set the REAL starting values, so a customized Normal preset (mod settings'
        // Presets tab) is reflected immediately rather than these going stale against it.
        private bool resetPawns;
        // 3-state (None/ManagedOnly/All), not a bool (2026-08-01) - see LoadoutUtility.BillDeleteScope.
        private LoadoutUtility.BillDeleteScope deleteAmmoBills;
        private LoadoutUtility.BillDeleteScope deleteWeaponBills;
        // 3-state (None/ManagedOnly/All), not a bool (2026-08-01) - ManagedOnly only ever
        // touches loadouts/packs this mod's own wizards generated, so a template/pack built by hand survives.
        private LoadoutUtility.BillDeleteScope deleteWeaponLoadouts;
        private LoadoutUtility.BillDeleteScope deleteEquipmentPacks;
        // Removed (2026-07-30): a separate "Equipment Packs"/"Generate" overwrite
        // toggle for refreshing just the standard-named templates in place, without touching custom ones,
        // was judged too specific a middle ground to be worth the UI complexity. Now always non-destructive
        // (skipExisting/presetLockExisting hardcoded true in Arm()) - existing packs/loadouts are only ever
        // touched via the two Delete switches above. NOTED ALTERNATIVE, not built: a single, more general
        // "Protect existing assets" toggle covering packs+loadouts+roles together ("Locks existing equipment
        // packs, loadouts and roles" / "Does not lock them, allowing them to be overwritten") - revisit this
        // shape if the per-step toggle need comes back, rather than the old two-separate-toggles design.
        private bool makeAmmoBills;
        // The assign step always runs in "fill gaps" mode (only colonists still without a loadout get one) -
        // it used to have its own separate "re-plan everyone" toggle, but that's redundant with "Reset first":
        // once reset has unassigned everyone, "fill gaps" naturally re-plans the whole colony anyway. The
        // player can still override this preset in the assign preview's own "Keep assigned" checkbox if needed.
        // Off by default, never part of a preset (like the two delete switches) - a deliberate opt-in, not
        // something either mode should silently flip. On: step 1 opens straight into Role Mode instead of
        // Weapons mode, and step 3 assigns by Role exclusively (same "Roles only" checkbox Assign Loadouts
        // already has, just preset here instead of left for the player to remember to tick).
        private bool useRoles = false;

        // Cautious: pure fill-gaps, nothing unassigned or deleted. Normal: unassign + rebuild ammo bills from
        // scratch (cheap to redo, low regret). Aggressive: everything - pawns, ammo bills, weapon bills, weapon
        // loadouts AND Equipment Packs, no exceptions (by design: Aggressive ticks and deletes EVERYTHING).
        private enum ArmMode { Cautious, Normal, Aggressive }

        // Which of the 6 step switches each mode sets - customizable in the mod settings (Presets tab,
        // "Arm my Colony mode presets") since 2026-07-30 instead of hardcoded here. Falls back to the
        // factory defaults if settings somehow aren't loaded (shouldn't happen in practice).
        private static CEReloadedSettings.ArmModePreset PresetFor(ArmMode m)
        {
            CEReloadedSettings st = CEReloadedMod.settings;
            switch (m)
            {
                case ArmMode.Cautious: return st?.armCautious ?? CEReloadedSettings.DefaultArmCautious();
                case ArmMode.Aggressive: return st?.armAggressive ?? CEReloadedSettings.DefaultArmAggressive();
                default: return st?.armNormal ?? CEReloadedSettings.DefaultArmNormal();
            }
        }

        private void ApplyMode(ArmMode m)
        {
            CEReloadedSettings.ArmModePreset p = PresetFor(m);
            resetPawns = p.resetPawns;
            deleteAmmoBills = p.deleteAmmoBills;
            deleteWeaponBills = p.deleteWeaponBills;
            deleteWeaponLoadouts = p.deleteWeaponLoadouts;
            deleteEquipmentPacks = p.deleteEquipmentPacks;
            makeAmmoBills = p.createAmmoBills;
        }

        private bool MatchesMode(ArmMode m)
        {
            CEReloadedSettings.ArmModePreset p = PresetFor(m);
            return resetPawns == p.resetPawns && deleteAmmoBills == p.deleteAmmoBills
                && deleteWeaponBills == p.deleteWeaponBills
                && (deleteWeaponLoadouts == LoadoutUtility.BillDeleteScope.All) == (p.deleteWeaponLoadouts == LoadoutUtility.BillDeleteScope.All)
                && deleteEquipmentPacks == p.deleteEquipmentPacks && makeAmmoBills == p.createAmmoBills;
        }

        private string ModeLabel()
        {
            if (MatchesMode(ArmMode.Cautious)) return "CER_ArmColony_ModeCautious".Translate();
            if (MatchesMode(ArmMode.Normal)) return "CER_ArmColony_ModeNormal".Translate();
            if (MatchesMode(ArmMode.Aggressive)) return "CER_ArmColony_ModeAggressive".Translate();
            return "CER_ArmColony_ModeCustom".Translate();
        }

        public override Vector2 InitialSize => new Vector2(Mathf.Min(992f, UI.screenWidth - 40f), Mathf.Min(912f, UI.screenHeight - 40f));
        // Nicht unter RimWorlds Lernhilfe schieben (2026-08-19) - siehe
        // LoadoutUtility.KeepClearOfLearningReadout. Ohne Kollision aendert das nichts.
        protected override void SetInitialSizeAndPosition()
        {
            base.SetInitialSizeAndPosition();
            windowRect = LoadoutUtility.KeepClearOfLearningReadout(windowRect);
        }

        public Dialog_ArmColony(Dialog_GenerateLoadouts l, Dialog_GenerateAmmoBills b, Dialog_AssignLoadouts a) : base(new ReadoutAwareWindowDrawing())
        {
            loadoutsDialog = l; billsDialog = b; assignDialog = a;
            doCloseX = true;
            forcePause = true;
            absorbInputAroundWindow = true;
            closeOnClickedOutside = true;
            ApplyMode(ArmMode.Normal);   // default mode - reads the CURRENT (possibly customized) preset
            Analyze();
        }

        private void Analyze()
        {
            colonists = LoadoutUtility.AllColonyPawns().Count();
            unarmed = assignDialog != null ? assignDialog.CountUnarmedColonists() : 0;
            // Plain templates only now - used to count EVERYTHING non-default (Packs, Roles, hidden per-
            // pawn anchors included), which made this number mean nothing next to the new, precise Packs count.
            // Nur die von Hand gebauten (2026-09-06): seit dem Lazy-Umbau erzeugt der Mod je benutzter Waffe
            // eine unsichtbare Vorlage. Die hier mitzuzaehlen hiesse, dem Spieler "9 Loadouts" zu melden, die
            // er nirgends sehen kann.
            existingLoadouts = LoadoutManager.Loadouts.Count(l => LoadoutUtility.IsPlainWeaponLoadout(l) && !LoadoutUtility.IsGeneratedTemplate(l));
            existingPacks = EquipmentPackUtility.AllEquipmentPacks().Count;
            ammoBillsManagedCount = LoadoutUtility.CountAmmoBillsForScope(LoadoutUtility.BillDeleteScope.ManagedOnly);
            ammoBillsAllCount = LoadoutUtility.CountAmmoBillsForScope(LoadoutUtility.BillDeleteScope.All);
            weaponBillsManagedCount = LoadoutUtility.CountWeaponBillsForScope(LoadoutUtility.BillDeleteScope.ManagedOnly);
            weaponBillsAllCount = LoadoutUtility.CountWeaponBillsForScope(LoadoutUtility.BillDeleteScope.All);
            weaponLoadoutsManagedCount = LoadoutUtility.CountWeaponLoadoutsForScope(LoadoutUtility.BillDeleteScope.ManagedOnly);
            weaponLoadoutsAllCount = LoadoutUtility.CountWeaponLoadoutsForScope(LoadoutUtility.BillDeleteScope.All);
            equipmentPacksManagedCount = LoadoutUtility.CountEquipmentPacksForScope(LoadoutUtility.BillDeleteScope.ManagedOnly);
            equipmentPacksAllCount = LoadoutUtility.CountEquipmentPacksForScope(LoadoutUtility.BillDeleteScope.All);
            Preflight();
        }

        // Collect only KO-criteria: conditions under which arming cannot physically proceed. These are rare on purpose
        // (a single crafting spot already lets you build some weapon), so we block just the truly impossible cases.
        // Checks every colony map, not just the one currently in view - a bench or owned weapon on a different
        // base still means arming is possible.
        private void Preflight()
        {
            blockers.Clear();
            if (!LoadoutUtility.PlayerMaps().Any())
            {
                blockers.Add("CER_ArmColony_NoActiveMap".Translate());
                return;
            }
            if (colonists == 0)
                blockers.Add("CER_ArmColony_NoFreeColonists".Translate());

            List<ThingDef> weapons = DefDatabase<ThingDef>.AllDefs
                .Where(d => LoadoutUtility.IsRealWeaponDef(d) && !LoadoutUtility.IsUtilityWeapon(d)).ToList();
            bool anyResearched = weapons.Any(d => { RecipeDef r = LoadoutUtility.GetRecipeFor(d); return r != null && LoadoutUtility.RecipeIsResearched(r); });
            bool anyProducible = weapons.Any(d => { RecipeDef r = LoadoutUtility.GetRecipeFor(d); return r != null && LoadoutUtility.RecipeIsResearched(r) && LoadoutUtility.WeaponBenchExists(d); });
            // Weapons marked "do not pick up" count as existing stock here too (2026-08-09, see
            // LoadoutUtility.IsMarkedDoNotPickUp) - blocking the whole Arm-my-Colony chain with "no weapon at
            // all" while a raid's drop lies on the ground was the headline case of that change.
            bool ownedWeaponExists = LoadoutUtility.PlayerMaps().Any(map => map.listerThings.AllThings.Any(t => t != null && t.def != null && t.def.IsWeapon && !t.def.destroyOnDrop
                && !LoadoutUtility.IsUtilityWeapon(t.def) && !LoadoutUtility.IsHiddenInFog(t)));

            // Only a blocker when you can neither build nor hand out any weapon at all.
            if (!anyProducible && !ownedWeaponExists)
            {
                if (!anyResearched)
                    blockers.Add("CER_ArmColony_NoWeaponResearched".Translate());
                else
                    blockers.Add("CER_ArmColony_NoWeaponBench".Translate());
            }
        }

        public override void DoWindowContents(Rect inRect)
        {
            Text.Font = GameFont.Medium;
            Widgets.Label(new Rect(0, 0, inRect.width, 34f), "CER_ArmColony_Title".Translate());
            Text.Font = GameFont.Small;

            float y = 42f;
            void Line(string s, float h = 24f) { Widgets.Label(new Rect(0, y, inRect.width, h), s); y += h; }

            GUI.color = new Color(1f, 1f, 1f, 0.85f);
            Line("CER_ArmColony_Intro".Translate(), 42f);
            GUI.color = Color.white;
            y += 4f;
            Widgets.DrawLineHorizontal(0, y, inRect.width); y += 10f;

            Line("CER_ArmColony_ColonistsLine".Translate(colonists.ToString(), unarmed.ToString()));
            // Two separate lines (2026-08-01: crammed onto one line read awkwardly) instead of
            // one wide combined line.
            Line("CER_ArmColony_ExistingLoadoutsLine".Translate(existingLoadouts.ToString()));
            Line("CER_ArmColony_ExistingPacksLine".Translate(existingPacks.ToString()));
            y += 6f;

            // Mode radios double as master presets for a per-step switch table: clicking a mode ticks every row to
            // that mode's behavior, and each row can then be fine-tuned with its own checkbox. Per row the cell whose
            // behavior the checkbox currently produces is tinted, so mixed (custom) setups still read correctly.
            Widgets.DrawLineHorizontal(0, y, inRect.width); y += 10f;

            const float stepColW = 220f, colGap = 8f;
            float cellW = (inRect.width - stepColW - colGap) / 2f;
            float col1 = stepColW + colGap, col2 = col1 + cellW + colGap;

            // Its own standalone radio pair, NOT a checkbox row in the Filling-gaps/Aggressive table below -
            // it never was one of that table's presets to begin with (deliberately excluded from ApplyMode,
            // see useRoles' own field comment), so showing it as just another toggle under those two mode
            // columns read as if picking a mode also decided this, which it never has.
            Line("CER_ArmColony_StructureHeader".Translate(), 24f);
            float rxRoles = col1;
            if (LoadoutUtility.Radio(ref rxRoles, y, "CER_ArmColony_AdHocLabel".Translate(), !useRoles,
                "CER_ArmColony_AdHocTip".Translate())) useRoles = false;
            rxRoles = col2;
            if (LoadoutUtility.Radio(ref rxRoles, y, "CER_ArmColony_RolesLabel".Translate(), useRoles,
                "CER_ArmColony_RolesTip".Translate())) useRoles = true;
            y += 48f;
            Widgets.DrawLineHorizontal(0, y, inRect.width); y += 10f;

            Line("CER_ArmColony_PickModeHeader".Translate(), 24f);
            // Generated from the ACTUAL preset values (mod settings' Presets tab), not hardcoded text - a
            // customized mode (e.g. Normal also deleting weapon bills) must never leave a stale tooltip
            // claiming the old factory behavior.
            string DescribeMode(ArmMode m)
            {
                CEReloadedSettings.ArmModePreset p = PresetFor(m);
                List<string> parts = new List<string>();
                if (p.resetPawns) parts.Add("CER_ArmColony_DescUnassign".Translate());
                if (p.deleteAmmoBills == LoadoutUtility.BillDeleteScope.ManagedOnly) parts.Add("CER_ArmColony_DescDeleteAmmoManaged".Translate());
                else if (p.deleteAmmoBills == LoadoutUtility.BillDeleteScope.All) parts.Add("CER_ArmColony_DescDeleteAmmoAll".Translate());
                if (p.deleteWeaponBills == LoadoutUtility.BillDeleteScope.ManagedOnly) parts.Add("CER_ArmColony_DescDeleteWeaponBillsManaged".Translate());
                else if (p.deleteWeaponBills == LoadoutUtility.BillDeleteScope.All) parts.Add("CER_ArmColony_DescDeleteWeaponBillsAll".Translate());
                if (p.deleteWeaponLoadouts == LoadoutUtility.BillDeleteScope.All) parts.Add("CER_ArmColony_DescDeleteWeaponLoadouts".Translate());
                if (p.deleteEquipmentPacks == LoadoutUtility.BillDeleteScope.ManagedOnly) parts.Add("CER_ArmColony_DescDeleteEquipmentPacksManaged".Translate());
                else if (p.deleteEquipmentPacks == LoadoutUtility.BillDeleteScope.All) parts.Add("CER_ArmColony_DescDeleteEquipmentPacks".Translate());
                string destructive = parts.Count > 0
                    ? "CER_ArmColony_DescDestructivePrefix".Translate(string.Join(", ", parts))
                    : "CER_ArmColony_DescNothing".Translate();
                string ammo = p.createAmmoBills ? "CER_ArmColony_DescAmmoCreate".Translate() : "CER_ArmColony_DescAmmoNone".Translate();
                return destructive + ammo + "CER_ArmColony_DescSuffix".Translate();
            }
            string cautiousTip = DescribeMode(ArmMode.Cautious);
            string normalTip = DescribeMode(ArmMode.Normal);
            string aggrTip = DescribeMode(ArmMode.Aggressive);

            // Starts at col1 (same x the switch-row table's own "off" cell starts at below), not x=0 - so
            // "Cautious" sits directly above "Nobody is unassigned." instead of above the step-name column,
            // which never has anything at this height anyway.
            float modeW = (inRect.width - col1) / 3f;
            float rx = col1;
            if (LoadoutUtility.Radio(ref rx, y, "CER_ArmColony_RadioCautious".Translate(), MatchesMode(ArmMode.Cautious), cautiousTip)) ApplyMode(ArmMode.Cautious);
            rx = col1 + modeW;
            if (LoadoutUtility.Radio(ref rx, y, "CER_ArmColony_RadioNormal".Translate(), MatchesMode(ArmMode.Normal), normalTip)) ApplyMode(ArmMode.Normal);
            rx = col1 + modeW * 2f;
            if (LoadoutUtility.Radio(ref rx, y, "CER_ArmColony_RadioAggressive".Translate(), MatchesMode(ArmMode.Aggressive), aggrTip)) ApplyMode(ArmMode.Aggressive);
            y += 38f;

            // Returns whether THIS cell was clicked (used by the 3-state delete rows below to pick which of
            // their 3 options was chosen - a plain 2-state SwitchRow cell just ignores the return value).
            // Width is now a parameter (2026-08-01) - a grouped 2-state row needs to merge adjacent
            // mode-columns that share the same value into one wider cell instead of a fixed 50/50 split.
            bool Cell(float x, float w, string s, bool on, float h)
            {
                if (on) Widgets.DrawBoxSolid(new Rect(x - 4f, y - 2f, w + 8f, h + 2f), new Color(0.5f, 0.8f, 0.5f, 0.12f));
                GUI.color = on ? Color.white : new Color(1f, 1f, 1f, 0.45f);
                Widgets.Label(new Rect(x, y, w, h), s);
                GUI.color = Color.white;
                return Widgets.ButtonInvisible(new Rect(x, y, w, h));
            }

            // 2-state rows: FESTE Aufteilung (2026-09-07): die Schritte "delete hand-built weapon loadouts"
            // und "ammo bills" bekommen unter Cautious eine Aus- und unter Normal/Aggressive eine An-Option,
            // damit niemand erst den Haken setzen muss. Bis dahin
            // richteten sich die Zellen nach den Preset-Werten (2026-08-01): stand ein Schritt in allen drei
            // Presets gleich, gab es EINE Zelle ueber die ganze Breite - und die einzige Bedienung war der
            // Haken links, weil der Rueckgabewert von Cell hier ignoriert wurde. Jetzt: links (unter
            // Cautious) immer die Aus-Zelle, rechts (unter Normal und Aggressive) immer die An-Zelle, beide
            // klickbar. Die Vorgaben der Presets bleiben, wie sie sind - nur die Anzeige ist fest.
            void SwitchRow(string step, ref bool val, string offText, string onText, float h, string tip)
            {
                Text.Font = GameFont.Tiny;
                Widgets.Checkbox(new Vector2(0f, y), ref val, 20f);
                GUI.color = new Color(0.75f, 0.85f, 1f);
                Widgets.Label(new Rect(26f, y + 3f, stepColW - 26f, h), step);
                GUI.color = Color.white;
                TooltipHandler.TipRegion(new Rect(0f, y, stepColW, 22f), tip);

                if (Cell(col1, modeW - colGap, offText, !val, h)) val = false;
                if (Cell(col1 + modeW, modeW * 2f - colGap, onText, val, h)) val = true;
                Text.Font = GameFont.Small;
                y += h + 4f;
            }

            // 3-state rows (deleteAmmoBills/deleteWeaponBills) get one FIXED column per real scope choice,
            // aligned 1:1 with the 3 mode radios above (col1/col1+modeW/col1+modeW*2) - not preset-grouped
            // like SwitchRow above, since these ARE the 3 real options rather than a 2-state value that
            // happens to land somewhere across 3 modes. Each cell is directly clickable (no separate
            // checkbox - there's no single "off" state to toggle, only which of the 3 to pick).
            void TriSwitchRow(string step, ref LoadoutUtility.BillDeleteScope val, string noneText, string managedText, string allText, int managedCount, int allCount, float h, string tip)
            {
                Text.Font = GameFont.Tiny;
                // Leftmost state icon (2026-08-01, corrected - no custom badge, the exact
                // same vanilla textures the 2-state rows' own Widgets.Checkbox already draws: CheckboxOffTex/
                // CheckboxOnTex for None/All, plus vanilla's own tri-state CheckboxPartialTex for Managed -
                // not a hand-drawn glyph).
                Rect stateR = new Rect(1f, y - 1f, 20f, 20f);
                Texture2D stateTex = val == LoadoutUtility.BillDeleteScope.None ? Widgets.CheckboxOffTex
                    : val == LoadoutUtility.BillDeleteScope.All ? Widgets.CheckboxOnTex : Widgets.CheckboxPartialTex;
                GUI.DrawTexture(stateR, stateTex);
                GUI.color = new Color(0.75f, 0.85f, 1f);
                Widgets.Label(new Rect(26f, y + 3f, stepColW - 26f, h), step);
                GUI.color = Color.white;
                TooltipHandler.TipRegion(new Rect(0f, y, stepColW, 22f), tip);
                float gw = modeW - colGap;
                if (Cell(col1, gw, noneText.Translate("0"), val == LoadoutUtility.BillDeleteScope.None, h))
                    val = LoadoutUtility.BillDeleteScope.None;
                if (Cell(col1 + modeW, gw, managedText.Translate(managedCount.ToString()), val == LoadoutUtility.BillDeleteScope.ManagedOnly, h))
                    val = LoadoutUtility.BillDeleteScope.ManagedOnly;
                // "(7+0)" statt "(7)" in der Alles-Spalte (2026-08-25): gab es keine handgebauten Loadouts
                // oder Packs, stand in der Aggressive-Spalte trotzdem (7) und (1) - "(7+0)" und "(1+0)" macht
                // es deutlich. Die Summe allein
                // sah aus wie ein Widerspruch zum Text "including hand-built ones", wenn beide Spalten
                // dieselbe Zahl zeigten. Jetzt: vom Assistenten erzeugte + handgebaute, in dieser
                // Reihenfolge - die Spalte links daneben zeigt genau den ersten Summanden. Gilt fuer alle
                // vier Zeilen, die durch diese Funktion laufen (Munitions-/Waffenauftraege, Loadouts, Packs).
                int handBuiltCount = Mathf.Max(0, allCount - managedCount);
                if (Cell(col1 + modeW * 2f, gw, allText.Translate(managedCount + "+" + handBuiltCount), val == LoadoutUtility.BillDeleteScope.All, h))
                    val = LoadoutUtility.BillDeleteScope.All;
                Text.Font = GameFont.Small;
                y += h + 4f;
            }

            SwitchRow("CER_ArmColony_StepResetPawns".Translate(), ref resetPawns,
                "CER_ArmColony_ResetPawnsOff".Translate(),
                "CER_ArmColony_ResetPawnsOn".Translate(), 34f,
                "CER_ArmColony_ResetPawnsTip".Translate());
            TriSwitchRow("CER_ArmColony_StepDeleteAmmoBills".Translate(), ref deleteAmmoBills,
                "CER_ArmColony_DeleteAmmoNone",
                "CER_ArmColony_DeleteAmmoManaged",
                "CER_ArmColony_DeleteAmmoAll", ammoBillsManagedCount, ammoBillsAllCount, 34f,
                "CER_ArmColony_DeleteAmmoTip".Translate());
            TriSwitchRow("CER_ArmColony_StepDeleteWeaponBills".Translate(), ref deleteWeaponBills,
                "CER_ArmColony_DeleteWeaponBillsNone",
                "CER_ArmColony_DeleteWeaponBillsManaged",
                "CER_ArmColony_DeleteWeaponBillsAll", weaponBillsManagedCount, weaponBillsAllCount, 34f,
                "CER_ArmColony_DeleteWeaponBillsTip".Translate());
            // ZWEISTUFIG seit 2026-09-06: "Delete weapon loadouts" ergab nur noch fuer von Hand gebaute
            // Loadouts Sinn. Die mittlere Stufe loeschte
            // genau die unsichtbaren Vorlagen, die der Mod seit dem Lazy-Umbau je Waffe selbst anlegt - und
            // derselbe Lauf legte sie Sekunden spaeter wieder an; Rollen verloren dabei ihre Bausteine. Uebrig
            // bleibt das Einzige, was der Spieler sehen und verlieren kann: von Hand in CEs Editor gebaute
            // Loadouts. Die Vorlagen des Mods fasst dieser Schritt nicht mehr an.
            // Gespeichert bleibt das alte Feld (None/All); ein altes ManagedOnly liest sich als "aus".
            bool deleteHandBuilt = deleteWeaponLoadouts == LoadoutUtility.BillDeleteScope.All;
            int handBuiltLoadouts = Mathf.Max(0, weaponLoadoutsAllCount - weaponLoadoutsManagedCount);
            SwitchRow("CER_ArmColony_StepDeleteWeaponLoadouts".Translate(), ref deleteHandBuilt,
                "CER_ArmColony_DeleteWeaponLoadoutsNone".Translate(handBuiltLoadouts.ToString()),
                "CER_ArmColony_DeleteWeaponLoadoutsAll".Translate(handBuiltLoadouts.ToString()), 34f,
                "CER_ArmColony_DeleteWeaponLoadoutsTip".Translate());
            deleteWeaponLoadouts = deleteHandBuilt ? LoadoutUtility.BillDeleteScope.All : LoadoutUtility.BillDeleteScope.None;
            TriSwitchRow("CER_ArmColony_StepDeleteEquipmentPacks".Translate(), ref deleteEquipmentPacks,
                "CER_ArmColony_DeleteEquipmentPacksNone",
                "CER_ArmColony_DeleteEquipmentPacksManaged",
                "CER_ArmColony_DeleteEquipmentPacksAll", equipmentPacksManagedCount, equipmentPacksAllCount, 34f,
                "CER_ArmColony_DeleteEquipmentPacksTip".Translate());
            SwitchRow("CER_ArmColony_StepAmmoBills".Translate(), ref makeAmmoBills,
                "CER_ArmColony_AmmoBillsOff".Translate(),
                "CER_ArmColony_AmmoBillsOn".Translate(), 36f,
                "CER_ArmColony_AmmoBillsTip".Translate());
            y += 4f;

            // KO-criteria: if anything makes arming impossible, spell it out and refuse to proceed.
            if (blockers.Count > 0)
            {
                GUI.color = new Color(0.95f, 0.5f, 0.45f);
                Line("CER_ArmColony_BlockersHeader".Translate(), 24f);
                foreach (string b in blockers) Line("CER_ArmColony_BlockerBullet".Translate(b), 40f);
                GUI.color = Color.white;
            }

            if (LoadoutUtility.BottomCloseButton(inRect, "CER_ArmColony_CancelBtn".Translate())) Close();

            bool blocked = blockers.Count > 0;
            GUI.color = blocked ? new Color(1f, 1f, 1f, 0.4f) : new Color(0.62f, 0.9f, 0.62f);
            Rect goRect = new Rect(inRect.width - LoadoutUtility.PrimaryBtnW, inRect.height - LoadoutUtility.BottomBarH, LoadoutUtility.PrimaryBtnW, LoadoutUtility.BottomBtnH);
            bool go = LoadoutUtility.ButtonTextFit(goRect, "CER_ArmColony_GoBtn".Translate().ToString());
            GUI.color = Color.white;
            TooltipHandler.TipRegion(goRect, "CER_ArmColony_GoTip".Translate(ModeLabel()));
            if (go)
            {
                if (blocked)
                    Find.WindowStack.Add(new Dialog_MessageBox("CER_ArmColony_CantProceed".Translate(string.Join("\n", blockers.Select(b => "CER_ArmColony_CantProceedBullet".Translate(b).ToString()).ToArray()))));
                else
                    Arm();
            }
        }

        private void Arm()
        {
            ArmReport rep = new ArmReport { modeLabel = ModeLabel() };

            // The reset switches run before anything else so the rebuild matches the current requirements exactly.
            bool deletingAmmo = deleteAmmoBills != LoadoutUtility.BillDeleteScope.None;
            bool deletingWeaponBills = deleteWeaponBills != LoadoutUtility.BillDeleteScope.None;
            if (resetPawns || deletingAmmo || deletingWeaponBills)
            {
                rep.didReset = true;
                if (deletingAmmo && billsDialog != null) rep.ammoBillsDeleted = billsDialog.ArmDeleteAllAmmoBills(deleteAmmoBills);
                if (deletingWeaponBills) rep.weaponBillsDeleted = LoadoutUtility.DeleteAllWeaponBills(deleteWeaponBills);
                if (resetPawns && assignDialog != null) rep.pawnsUnassigned = assignDialog.ArmUnassignAll();
            }
            if (deleteWeaponLoadouts != LoadoutUtility.BillDeleteScope.None)
            {
                rep.didReset = true;
                // Equipment Packs were already excluded (their own "Delete equipment packs" toggle covers
                // them) - but Role loadouts ("[Role] ...") and the hidden per-pawn anchors
                // ("[Pawn-Loadout] <id>") were NOT, so this used to nuke a player's hand-crafted, reusable
                // Roles too - a much bigger loss than "just the auto-generated templates" this toggle's own
                // label implies, and not something "Delete weapon loadouts" should touch at all (Roles have
                // their own separate concept/lifecycle in Role Mode; anchors are internal bookkeeping, not
                // a "weapon loadout" a player ever created). Both now excluded, matching the same "only
                // touch what this button's own name says" principle Equipment Packs already got.
                // ManagedOnly (2026-08-01) only touches templates THIS mod's own wizard generated
                // (IsGeneratedTemplate) - a template built by hand in CE's native loadout editor survives.
                // Der Marker zusaetzlich zur Identitaetspruefung (2026-08-18) - hier waere ein Irrtum am
                // teuersten, denn das hier LOESCHT. IsPlainWeaponLoadout fragt IsRoleLoadout, und die
                // Antwort haengt daran, dass die Rolle ihr Loadout gerade kennt. Verliert sie die
                // Verknuepfung (Zuruecksetzen im Rollen-Reiter war so ein Fall), gilt ihr Loadout einen
                // Moment lang als gewoehnliche Vorlage - und dieser Knopf haette sie mitgeloescht. Die
                // Ursache ist repariert (RoleUtility.AdoptRoleLoadoutsByName), das hier ist das Netz
                // darunter: es kann nur MEHR verschonen, nie etwas falsch loeschen.
                // NUR von Hand gebaute (2026-09-06, zweistufig): die vom Mod erzeugten Vorlagen werden hier nie
                // mehr angefasst - der Lauf legt sie ohnehin je vergebener Waffe neu an, und Rollen haengen mit
                // ihren Bausteinen daran.
                List<Loadout> doomed = LoadoutManager.Loadouts
                    .Where(x => x != null && !x.defaultLoadout && !EquipmentPackUtility.IsEquipmentPackLoadout(x)
                        && LoadoutUtility.IsPlainWeaponLoadout(x) && !RoleUtility.IsRoleLabel(x.label)
                        && !LoadoutUtility.IsGeneratedTemplate(x)).ToList();
                // Sagen, was der Loeschvorgang nebenbei anrichtet (2026-08-18): Rollen behalten ihren Inhalt,
                // verlieren aber die Bausteine, aus denen
                // sie zusammengesetzt waren. Bewusst als Zeile im Endbericht und nicht als zweite
                // Rueckfrage - dieser Ablauf hat seine Bestaetigung schon, und eine weitere fuer einen
                // Sonderfall waere genau das Nachfragen, das hier ausdruecklich unerwuenscht ist.
                int rolesLosingParts = RoleUtility.CountRolesComposedOf(doomed);
                if (rolesLosingParts > 0)
                    rep.notes.Add("CER_ArmColony_NoteRolesLoseComposition".Translate(rolesLosingParts.ToString()));
                rep.loadoutsDeleted = LoadoutDeletionUtility.DeleteAll(doomed);
            }
            if (deleteEquipmentPacks != LoadoutUtility.BillDeleteScope.None)
            {
                rep.didReset = true;
                // ManagedOnly (2026-08-01) only touches packs THIS mod's Auto-generate wizard produced
                // (IsGeneratedPack) - a pack hand-built in the Ingredients tab survives.
                List<Loadout> doomedPacks = EquipmentPackUtility.AllEquipmentPacks()
                    .Where(x => deleteEquipmentPacks == LoadoutUtility.BillDeleteScope.All || LoadoutUtility.IsGeneratedPack(x)).ToList();
                rep.packsDeleted = LoadoutDeletionUtility.DeleteAll(doomedPacks);
            }

            // Immediate confirmation of what the reset switches just did, right as the wizard chain starts -
            // distinct from Dialog_ArmReport, which only shows once the WHOLE chain finishes or is cancelled
            // (several steps and popups later). A destructive action with no undo deserves to say so right
            // away, not several screens from now.
            if (rep.didReset)
            {
                var parts = new List<string>();
                if (rep.pawnsUnassigned > 0) parts.Add((rep.pawnsUnassigned == 1 ? "CER_ArmColony_ResetColonistSingular" : "CER_ArmColony_ResetColonistPlural").Translate(rep.pawnsUnassigned.ToString()));
                if (rep.ammoBillsDeleted > 0) parts.Add((rep.ammoBillsDeleted == 1 ? "CER_ArmColony_ResetAmmoBillSingular" : "CER_ArmColony_ResetAmmoBillPlural").Translate(rep.ammoBillsDeleted.ToString()));
                if (rep.weaponBillsDeleted > 0) parts.Add((rep.weaponBillsDeleted == 1 ? "CER_ArmColony_ResetWeaponBillSingular" : "CER_ArmColony_ResetWeaponBillPlural").Translate(rep.weaponBillsDeleted.ToString()));
                if (rep.loadoutsDeleted > 0) parts.Add((rep.loadoutsDeleted == 1 ? "CER_ArmColony_ResetLoadoutSingular" : "CER_ArmColony_ResetLoadoutPlural").Translate(rep.loadoutsDeleted.ToString()));
                if (rep.packsDeleted > 0) parts.Add((rep.packsDeleted == 1 ? "CER_ArmColony_ResetPackSingular" : "CER_ArmColony_ResetPackPlural").Translate(rep.packsDeleted.ToString()));
                // Always a message, even when every ticked scope resolved to 0 affected entries (e.g. "Managed
                // only" ticked but nothing this mod ever queued exists) - silence here used to read as "did
                // this even do anything, or is it broken", not as "correctly found nothing to do" (2026-07-31).
                string msg = parts.Count > 0
                    ? "CER_ArmColony_ResetSummary".Translate(string.Join(", ", parts)).ToString()
                    : "CER_ArmColony_ResetNothing".Translate().ToString();
                Messages.Message(msg, parts.Count > 0 ? MessageTypeDefOf.TaskCompletion : MessageTypeDefOf.NeutralEvent, false);
            }

            Close();

            // Background tab state, not just a per-run local - Dialog_GenerateLoadouts/loadoutsDialog stays
            // alive for the whole session (owned by Dialog_CEReloadedTabs), so a PREVIOUS run's choice would
            // otherwise stick around and misdescribe this one.
            loadoutsDialog.RoleMode = useRoles;

            // Step 0 (Equipment Packs) -> [step 1 Roles, only if "Use roles"] -> ammo bills -> Assign
            // ("Roles only" if "Use roles"). Der fruehere Erzeugen-Schritt (Massen-Generierung der Waffen-
            // Vorlagen) entfiel mit dem Lazy-Umbau (2026-09-01): der Assign-Schritt waehlt Waffen direkt,
            // die Vorlage entsteht unsichtbar beim Uebernehmen. Step 0 has no Cancel/abort of its own -
            // Equipment Packs are optional supply, so closing that popup (X, or its own Generate button with
            // nothing checked) just continues. Every step carries a Back button to the previous one
            // (navigation only - applied steps stay applied); step 0's Back reopens this same config screen.
            Action openPacksBack = null;   // fuer den Back-Knopf des Assign-Schritts (ohne Rollen)
            Action openRoles = null;
            Action afterGenerate = () =>
            {
                // Kein resetPawns-Argument mehr (2026-08-09): dieser Schritt LEGT nur noch an und loescht
                // gar nichts, also gibt es auch keinen Fall mehr, in dem das Loeschen unterdrueckt werden
                // muesste. Veraltete Auftraege bietet der Nachschub-Dialog zum Abhaken an.
                if (makeAmmoBills && billsDialog != null) billsDialog.ArmAutoGenerateMissing(rep);
                else if (!makeAmmoBills) rep.notes.Add("CER_ArmColony_NoteAmmoSkipped".Translate());
                if (assignDialog != null)
                    // resetPawns, not a hardcoded false (2026-08-02 fix) - this used to read the old, since-
                    // removed `replanAssign` step-switch, which got replaced
                    // with a bare `false` on the reasoning "Reset always unassigns everyone first, so the
                    // Assign step's keepAssigned value is moot anyway" - true for the FIRST pass through this
                    // step (cur is null for everyone right after Reset, so mainKept is false regardless of
                    // keepAssigned), but wrong for any SUBSEQUENT recompute after the player has already
                    // Applied once and then Back-navigated to reconfigure: cur is no longer null then, so
                    // keepAssigned's value (forced true by the hardcoded false) suddenly means "keep every
                    // already-assigned pawn, don't replan" - even under Aggressive, which should mean the
                    // opposite. resetPawns tracks the same on/off shape the removed toggle always had (true
                    // for Normal/Aggressive, false for Cautious/most custom presets), so passing it through
                    // directly restores the intended behavior on every pass, not just the first.
                    // Beide Zweige sagen jetzt, was sie meinen (2026-08-19). Vorher hiess "Ad-Hoc Loadouts
                    // per Pawn" im Code `rolesOnly = false`, also "keine Einschraenkung" - und der Assistent
                    // teilte weiter Rollen aus, obwohl sein eigener Mouseover "plain per-weapon loadouts"
                    // versprach.
                    assignDialog.ArmAutoAssignAndProduce(resetPawns, rep, onBack: useRoles ? openRoles : openPacksBack,
                        roleModeOverride: useRoles ? RoleAssignMode.RolesOnly : RoleAssignMode.NoRoles);
                else Find.WindowStack.Add(new Dialog_ArmReport(rep));
            };

            if (loadoutsDialog != null)
            {
                // Back aus Packs fuehrt auf diesen Konfigurationsschirm zurueck; der fruehere Zwischenschritt
                // "Weapon Loadouts erzeugen" existiert nicht mehr (Lazy-Umbau 2026-09-01).
                Action openPacks = () => Find.WindowStack.Add(new Dialog_GenerateEquipmentPack(loadoutsDialog,
                    onClosed: n => { rep.packsGenerated = n; if (useRoles) openRoles(); else afterGenerate(); },
                    // Always non-destructive here - only "Delete equipment packs" above ever overwrites/
                    // removes an existing pack. skipExisting starts already-existing packs unchecked so
                    // this step only ever fills in what's missing (the player can still tick one back on
                    // to refresh it in place, same as the popup already allows standalone).
                    skipExisting: true,
                    onBack: () => Find.WindowStack.Add(new Dialog_ArmColony(loadoutsDialog, billsDialog, assignDialog))));
                // Role Mode as its own wizard step - Rollen bauen ihre Bausteine seit dem Lazy-Umbau direkt
                // aus Waffen, nicht mehr aus vorab erzeugten Vorlagen.
                openRoles = () => Find.WindowStack.Add(new Dialog_ArmRolesStep(loadoutsDialog, onDone: afterGenerate, onBack: openPacks));
                openPacksBack = openPacks;
                openPacks();
            }
            else
            {
                afterGenerate();
            }
        }
    }

    // Arm My Colony's "Use roles" step: hosts the same Role Mode screen the Weapon Loadouts tab uses (drag
    // the Weapon Loadouts just generated in step 1 onto Role cards to bake "[Role] ..." loadouts) as an
    // actual step in the wizard, between generating Weapon Loadouts and assigning. Previously "Use roles"
    // only set a flag on the tabbed window's own (persistent, background) Dialog_GenerateLoadouts instance
    // and hoped the player noticed it flip to Role Mode after the whole wizard finished - by then Assign had
    // already run with "Roles only" and zero [Role] loadouts to pick from, since nothing had built any yet.
    public class Dialog_ArmRolesStep : Window
    {
        private readonly Dialog_GenerateLoadouts loadoutsDialog;
        private readonly Action onDone;
        private readonly Action onBack;
        // Set only by the explicit "< Back" button; everything else (Continue, or the native X - doCloseX is
        // true here) falls through to onDone in PostClose. Mirrors Dialog_GenerateEquipmentPack's own
        // wentBack pattern: buttons just set the flag and Close(), so PostClose is the ONE place that decides
        // what happens next - closing via X used to leave the whole Arm-my-Colony chain dead (no ammo bills,
        // no Assign, no report), since neither Action ever fired for that path.
        private bool wentBack;

        // Matches Dialog_GenerateEquipmentPack's own size (the step right before this one) - Role Mode's
        // drag-and-drop panels need real room, and a noticeably smaller popup than every other step in the
        // same wizard read as a mistake, not a deliberate choice.
        public override Vector2 InitialSize => new Vector2(Mathf.Min(1208f, UI.screenWidth - 40f), Mathf.Min(1080f, UI.screenHeight - 30f));
        // Nicht unter RimWorlds Lernhilfe schieben (2026-08-19) - siehe
        // LoadoutUtility.KeepClearOfLearningReadout. Ohne Kollision aendert das nichts.
        protected override void SetInitialSizeAndPosition()
        {
            base.SetInitialSizeAndPosition();
            windowRect = LoadoutUtility.KeepClearOfLearningReadout(windowRect);
        }

        public Dialog_ArmRolesStep(Dialog_GenerateLoadouts loadoutsDialog, Action onDone, Action onBack) : base(new ReadoutAwareWindowDrawing())
        {
            this.loadoutsDialog = loadoutsDialog;
            this.onDone = onDone;
            this.onBack = onBack;
            loadoutsDialog.RoleMode = true;
            doCloseX = true;
            forcePause = true;
            absorbInputAroundWindow = true;
        }

        public override void PostClose()
        {
            base.PostClose();
            if (wentBack) onBack?.Invoke();
            else onDone?.Invoke();
        }

        public override void DoWindowContents(Rect inRect)
        {
            Rect body = new Rect(0, 0, inRect.width, inRect.height - LoadoutUtility.BottomBarH - 6f);
            loadoutsDialog.DoWindowContents(body);

            if (LoadoutUtility.BottomCloseButton(inRect, "CER_ArmColony_BackBtn".Translate()))
            {
                wentBack = true;
                Close();
            }
            Rect goRect = new Rect(inRect.width - LoadoutUtility.PrimaryBtnW, inRect.height - LoadoutUtility.BottomBarH, LoadoutUtility.PrimaryBtnW, LoadoutUtility.BottomBtnH);
            if (LoadoutUtility.ButtonTextFit(goRect, "CER_ArmColony_ContinueBtn".Translate().ToString()))
            {
                Close();
            }
            bool anyRoles = LoadoutManager.Loadouts.Any(l => RoleUtility.IsRoleLoadout(l));
            TooltipHandler.TipRegion(goRect, anyRoles
                ? "CER_ArmColony_ContinueTipHasRoles".Translate()
                : "CER_ArmColony_ContinueTipNoRoles".Translate());
        }
    }

    // One applied assignment, kept as live references so the report can draw icons and jump to the colonist.
    public class ArmAssignment
    {
        public Pawn pawn;
        public Loadout loadout;
        public ThingDef mainWeapon;
    }

    // Running tally of everything an Arm-my-Colony run changed, rendered by Dialog_ArmReport at the very end.
    public class ArmReport
    {
        public string modeLabel;
        public bool didReset;
        public bool cancelled;
        public int pawnsUnassigned;
        public int ammoBillsDeleted;
        public int weaponBillsDeleted;
        public int loadoutsDeleted;
        public int packsDeleted;
        public int packsGenerated;
        public int ammoBillsCreated;
        public int pawnsLeftUnassigned;
        // Wieviele Posten im Nachschub-Schritt gar nicht baubar waren (keine Werkbank / kein Rezept).
        // Trennt "es war nichts noetig" von "es ging nichts" - siehe Dialog_SupplyLoadouts.GenerateAll.
        public int prodBlocked;
        public List<ArmAssignment> assignments = new List<ArmAssignment>();
        public Dictionary<ThingDef, int> weaponsQueued = new Dictionary<ThingDef, int>();   // def -> copies queued
        public Dictionary<ThingDef, int> ammoQueued = new Dictionary<ThingDef, int>();      // ammo def -> rounds queued
        public List<string> notes = new List<string>();
    }

    // The closing overview: a full, itemised account of what the run did (or why it stopped) - portraits and item
    // icons instead of bare text, jump buttons to the colonists and workbenches, and the run's total material bill
    // with storage coverage, so it doubles as the "what happens next" screen.
    public class Dialog_ArmReport : Window
    {
        private readonly ArmReport r;
        private readonly Dictionary<ThingDef, int> materials = new Dictionary<ThingDef, int>();
        private Vector2 scroll;

        public override Vector2 InitialSize => new Vector2(700f, Mathf.Min(760f, UI.screenHeight - 60f));
        // Nicht unter RimWorlds Lernhilfe schieben (2026-08-19) - siehe
        // LoadoutUtility.KeepClearOfLearningReadout. Ohne Kollision aendert das nichts.
        protected override void SetInitialSizeAndPosition()
        {
            base.SetInitialSizeAndPosition();
            windowRect = LoadoutUtility.KeepClearOfLearningReadout(windowRect);
        }

        public Dialog_ArmReport(ArmReport report) : base(new ReadoutAwareWindowDrawing())
        {
            r = report;
            doCloseX = true;
            forcePause = true;
            absorbInputAroundWindow = true;
            closeOnClickedOutside = true;
            BuildMaterials();
            // The true "the whole chain just finished" moment (reset, generate, assign, produce all done) -
            // forces the Combat Readiness alert's tick-interval cache to recompute on next read instead of
            // trusting stale numbers (2026-08-01: still "9 have no loadout" right after
            // assigning all 9). See CombatReadiness.Invalidate's own comment for why the cache gets stuck.
            CombatReadiness.Invalidate();
        }

        // No LoadoutUtility.CheckAndOpenMissingProductionPopup() hook here (removed 2026-07-31, was here since
        // 2026-07-30). It used to double-check for missing production after the whole Arm-my-Colony chain
        // finished, which meant the player could see a SECOND supply popup right after already reviewing and
        // confirming the first one - annoying, and confusing when they'd deliberately declined to produce
        // something. Closing the report must not trigger the popup.
        // The real gap that justified this hook was never redundancy with the first popup, it was that
        // Equipment Pack items (medicine/food/drugs/utility) were never included in that first popup's Request
        // at all - see Dialog_AssignLoadouts.OpenProducePopup, which now folds ScanMissingAssignedPackItems
        // into the SAME in-chain popup via a planned (not yet applied) pack assignment. One feature-complete
        // popup instead of two sequential ones.

        // The run's total material bill: every queued weapon's recipe ingredients plus every created ammo bill's,
        // with the same batch math that queued them. Rendered via the shared Total-cost strip, so "red = not enough
        // in storage" reads exactly like the produce popups.
        private void BuildMaterials()
        {
            void Add(List<ThingDefCountClass> ings, int batches)
            {
                if (ings == null || batches <= 0) return;
                foreach (ThingDefCountClass ing in ings)
                    if (ing != null && ing.thingDef != null)
                        materials[ing.thingDef] = (materials.TryGetValue(ing.thingDef, out int c) ? c : 0) + ing.count * batches;
            }
            foreach (var kv in r.weaponsQueued)
            {
                RecipeDef rec = LoadoutUtility.GetRecipeFor(kv.Key);
                if (rec == null) continue;
                int yield = rec.products != null && rec.products.Count > 0 ? Mathf.Max(1, rec.products[0].count) : 1;
                Add(LoadoutUtility.GetRecipeIngredients(rec), Mathf.CeilToInt((float)kv.Value / yield));
            }
            foreach (var kv in r.ammoQueued)
            {
                RecipeDef rec = DefDatabase<RecipeDef>.AllDefs.FirstOrDefault(x => x.products != null && x.products.Count == 1
                    && x.products[0].thingDef == kv.Key && LoadoutUtility.RecipeIsResearched(x));
                if (rec == null) continue;
                int yield = Mathf.Max(1, rec.products[0].count);
                Add(LoadoutUtility.GetRecipeIngredients(rec), Mathf.CeilToInt((float)kv.Value / yield));
            }
        }

        // Jump to a workbench whose bill list produces this def. The report stays open - it's a summary the player
        // may want to keep checking against, not a step that's "done" once you've looked at one bill.
        private void JumpToBills(ThingDef product)
        {
            foreach (Building_WorkTable bench in LoadoutUtility.AllColonyBenches())
            {
                if (bench.billStack == null) continue;
                foreach (Bill_Production bp in bench.billStack.Bills.OfType<Bill_Production>())
                {
                    if (bp.recipe == null || bp.recipe.products == null) continue;
                    if (bp.recipe.products.Any(p => p.thingDef == product))
                    {
                        LoadoutUtility.OpenBillsTab(bench);
                        return;
                    }
                }
            }
            Messages.Message("CER_ArmColony_NoBillFor".Translate(product.label), MessageTypeDefOf.RejectInput, false);
        }

        public override void DoWindowContents(Rect inRect)
        {
            Text.Font = GameFont.Medium;
            Widgets.Label(new Rect(0, 0, inRect.width, 34f), "CER_ArmColony_ReportTitle".Translate());
            Text.Font = GameFont.Small;
            GUI.color = new Color(1f, 1f, 1f, 0.65f);
            Widgets.Label(new Rect(0, 34f, inRect.width, 22f), "CER_ArmColony_ReportModeLine".Translate(r.modeLabel ?? "CER_ArmColony_UnknownMark".Translate().ToString()));
            GUI.color = Color.white;

            Rect outer = new Rect(0, 62f, inRect.width, inRect.height - 62f - 48f);
            float vw = outer.width - 20f;
            float vh = EstimateHeight(vw);
            Rect view = new Rect(0, 0, vw, vh);
            Widgets.BeginScrollView(outer, ref scroll, view);
            float y = 0f;

            void Header(string t, string tip = null)
            {
                Rect hr = new Rect(0, y, vw, 24f);
                Text.Font = GameFont.Small;
                GUI.color = new Color(0.75f, 0.85f, 1f);
                Widgets.Label(hr, t);
                GUI.color = Color.white;
                if (tip != null) TooltipHandler.TipRegion(hr, tip);
                y += 24f;
                Widgets.DrawLineHorizontal(0, y, vw); y += 6f;
            }
            void Item(string t, string tip = null)
            {
                Rect ir = new Rect(12f, y, vw - 12f, 22f);
                Widgets.Label(ir, t);
                if (tip != null) TooltipHandler.TipRegion(ir, tip);
                y += 22f;
            }
            void Gap() { y += 10f; }

            if (r.cancelled)
            {
                GUI.color = new Color(0.98f, 0.75f, 0.35f);
                Widgets.Label(new Rect(0, y, vw, 44f), "CER_ArmColony_AbortedNote".Translate());
                GUI.color = Color.white;
                y += 48f;
            }

            if (r.didReset)
            {
                Header("CER_ArmColony_ReportResetHeader".Translate(), "CER_ArmColony_ReportResetTip".Translate());
                Item("CER_ArmColony_ReportPawnsUnassigned".Translate(r.pawnsUnassigned.ToString()));
                Item("CER_ArmColony_ReportAmmoBillsDeleted".Translate(r.ammoBillsDeleted.ToString()));
                Item("CER_ArmColony_ReportWeaponBillsDeleted".Translate(r.weaponBillsDeleted.ToString()));
                if (r.loadoutsDeleted > 0) Item("CER_ArmColony_ReportLoadoutsDeleted".Translate(r.loadoutsDeleted.ToString()));
                if (r.packsDeleted > 0) Item("CER_ArmColony_ReportPacksDeleted".Translate(r.packsDeleted.ToString()));
                Gap();
            }

            Header("CER_ArmColony_ReportGeneratedHeader".Translate());
            Item("CER_ArmColony_ReportPacksGenerated".Translate(r.packsGenerated.ToString()),
                "CER_ArmColony_ReportPacksGeneratedTip".Translate());
            Item("CER_ArmColony_ReportAmmoBillsCreated".Translate(r.ammoBillsCreated.ToString()),
                r.ammoQueued.Count > 0 ? "CER_ArmColony_ReportAmmoBillsCreatedTipSome".Translate() : "CER_ArmColony_ReportAmmoBillsCreatedTipNone".Translate());
            Gap();

            Header("CER_ArmColony_ReportAssignmentsHeader".Translate(r.assignments.Count.ToString()), "CER_ArmColony_ReportAssignmentsTip".Translate());
            if (r.assignments.Count == 0) Item(r.cancelled ? "CER_ArmColony_ReportNoneAborted".Translate() : "CER_ArmColony_ReportNone".Translate());
            else
            {
                foreach (ArmAssignment a in r.assignments)
                {
                    Rect row = new Rect(0, y, vw, 28f);
                    if (Mouse.IsOver(row)) Widgets.DrawHighlight(row);
                    if (a.pawn != null)
                        GUI.DrawTexture(new Rect(6f, y - 2f, 26f, 30f), PortraitsCache.Get(a.pawn, new Vector2(26f, 30f), Rot4.South));
                    Widgets.Label(new Rect(40f, y + 4f, 150f, 22f), (a.pawn != null ? a.pawn.LabelShortCap.ToString() : "CER_ArmColony_UnknownMark".Translate().ToString()).Truncate(146f));
                    GUI.color = new Color(1f, 1f, 1f, 0.55f);
                    Widgets.Label(new Rect(194f, y + 4f, 24f, 22f), "CER_ArmColony_ArrowMark".Translate());
                    GUI.color = Color.white;
                    float ix = 222f;
                    if (a.mainWeapon != null) { Widgets.ThingIcon(new Rect(ix, y + 2f, 24f, 24f), a.mainWeapon); ix += 28f; }
                    Widgets.Label(new Rect(ix, y + 4f, vw - ix - 8f, 22f), (a.loadout != null ? RoleUtility.DisplayLabel(a.loadout) : "CER_ArmColony_UnknownMark".Translate().ToString()).Truncate(vw - ix - 12f));
                    string tip = "CER_ArmColony_JumpTip".Translate(a.pawn != null ? a.pawn.LabelShortCap.ToString() : "CER_ArmColony_UnknownMark".Translate().ToString());
                    if (a.loadout != null) tip = LoadoutUtility.LoadoutContentsTooltip(a.loadout) + "\n\n" + tip;
                    TooltipHandler.TipRegion(row, tip);
                    if (a.pawn != null && Widgets.ButtonInvisible(row))
                    {
                        Close();
                        CameraJumper.TryJump(a.pawn);
                        Find.Selector.ClearSelection();
                        Find.Selector.Select(a.pawn);
                    }
                    y += 28f;
                }
            }
            if (r.pawnsLeftUnassigned > 0)
            {
                GUI.color = new Color(1f, 1f, 1f, 0.6f);
                Item("CER_ArmColony_ReportPawnsLeftUnassigned".Translate(r.pawnsLeftUnassigned.ToString()));
                GUI.color = Color.white;
            }
            Gap();

            int prodRows = r.weaponsQueued.Count + r.ammoQueued.Count;
            Header("CER_ArmColony_ReportProductionHeader".Translate(prodRows.ToString()), "CER_ArmColony_ReportProductionTip".Translate());
            // Drei Faelle, nicht zwei: abgebrochen, nichts baubar, nichts noetig. Der mittlere fehlte und
            // wurde als "- nichts noetig -" gelesen (2026-08-28) - genau umgekehrt zur Wahrheit.
            if (prodRows == 0)
                Item(r.cancelled ? "CER_ArmColony_ReportNoneAbortedProd".Translate()
                    : r.prodBlocked > 0 ? "CER_ArmColony_ReportNoneBlockedProd".Translate(r.prodBlocked.ToString())
                    : "CER_ArmColony_ReportNoneNeeded".Translate());
            else
            {
                void ProdRow(ThingDef d, string amount, string tipExtra)
                {
                    Rect row = new Rect(0, y, vw, 26f);
                    if (Mouse.IsOver(row)) Widgets.DrawHighlight(row);
                    Widgets.ThingIcon(new Rect(6f, y + 1f, 22f, 22f), d);
                    Widgets.Label(new Rect(36f, y + 3f, vw - 36f - 150f, 22f), d.LabelCap.ToString().Truncate(vw - 36f - 154f));
                    Text.Anchor = TextAnchor.MiddleRight;
                    Widgets.Label(new Rect(vw - 150f, y + 1f, 74f, 22f), amount);
                    Text.Anchor = TextAnchor.UpperLeft;
                    if (LoadoutUtility.ButtonTextFit(new Rect(vw - 68f, y + 1f, 62f, 22f), "CER_ArmColony_BillsBtn".Translate().ToString())) JumpToBills(d);
                    TooltipHandler.TipRegion(new Rect(0, y, vw - 72f, 26f), tipExtra);
                    y += 26f;
                }
                foreach (var kv in r.weaponsQueued.OrderBy(k => k.Key.label))
                    ProdRow(kv.Key, "CER_ArmColony_CountMultiplier".Translate(kv.Value.ToString()),
                        (kv.Value == 1 ? "CER_ArmColony_CopyQueuedSingular" : "CER_ArmColony_CopyQueuedPlural").Translate(kv.Value.ToString()));
                foreach (var kv in r.ammoQueued.OrderBy(k => k.Key.label))
                    ProdRow(kv.Key, "CER_ArmColony_RoundsAmount".Translate(kv.Value.ToString()), "CER_ArmColony_RoundsQueuedTip".Translate(kv.Value.ToString()));
            }
            Gap();

            Header("CER_ArmColony_ReportTotalMaterialsHeader".Translate(), "CER_ArmColony_ReportTotalMaterialsTip".Translate());
            LoadoutUtility.DrawTotalCostLine(0f, y, vw, materials);
            y += 30f;

            if (r.notes.Count > 0)
            {
                Gap();
                Header("CER_ArmColony_ReportNotesHeader".Translate());
                foreach (string n in r.notes) Item(n);
            }

            Widgets.EndScrollView();

            if (LoadoutUtility.ButtonTextFit(new Rect(inRect.width - LoadoutUtility.CloseBtnW, inRect.height - LoadoutUtility.BottomBarH, LoadoutUtility.CloseBtnW, LoadoutUtility.BottomBtnH), "CER_ArmColony_CloseBtn".Translate().ToString())) Close();
        }

        private float EstimateHeight(float vw)
        {
            float h = 0f;
            if (r.cancelled) h += 48f;
            if (r.didReset) h += 30f + (3 + (r.loadoutsDeleted > 0 ? 1 : 0) + (r.packsDeleted > 0 ? 1 : 0)) * 22f + 10f;
            h += 30f + 3 * 22f + 10f;                                                        // generated
            h += 30f + Mathf.Max(1, r.assignments.Count) * 28f + (r.pawnsLeftUnassigned > 0 ? 22f : 0f) + 10f;
            h += 30f + Mathf.Max(1, r.weaponsQueued.Count + r.ammoQueued.Count) * 26f + 10f; // production queued
            h += 30f + 30f;                                                                  // total materials strip
            if (r.notes.Count > 0) h += 10f + 30f + r.notes.Count * 22f;
            return h + 20f;
        }
    }
}
