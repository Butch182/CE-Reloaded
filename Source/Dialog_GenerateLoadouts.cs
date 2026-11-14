using System;
using System.Linq;
using System.Collections.Generic;
using RimWorld;
using Verse;
using CombatExtended;
using UnityEngine;

namespace CEReloaded
{
    public enum SortMode { Name, Special, Score, Loadouts, Total, Carried, Stored, Assigned, Available }
    // Loads its own static Texture2D fields - see Dialog_AssignLoadouts' own identical
    // comment for why this attribute is required (2026-08-01).
    [StaticConstructorOnStartup]
    public class Dialog_GenerateLoadouts
    {
        public Window parentWindow;
        
        private List<ThingDef> allWeapons;
        private List<ThingDef> allMeleeWeapons;
        private List<ThingDef> allGrenades;
        private List<Def> allMedicines;
        private List<Def> allFoods;
        private HashSet<ThingDef> availableWeaponsCache;
        
        
        
        // Highest weapon score, cached against LoadoutUtility.WeaponScoreVersion - see where it is used in the
        // draw loop. -1 means "never computed".
        private string sidearmMaxBulk = "5.0";
        // Meal and medicine were REMOVED here without replacement. There used to be four fields and four
        // internal properties that read the mod settings - and nothing read them anymore: four settings with no
        // effect. Food belongs to the Equipment Pack, not to the weapon template; the settings now take effect
        // there (Dialog_GenerateEquipmentPack.PickFromSettingOrAuto). This comment stays as a signpost so the
        // wiring doesn't get recreated here by accident a third time.

        // --- Role Mode state (drag weapon loadouts onto a role to produce a scaled "[Role] ..." loadout) ---
        private bool roleMode;
        // Arm My Colony's "Use roles" option lands the player straight in Role Mode instead of Weapons
        // mode when its own generate step opens this same dialog - same view, just a different starting tab.
        internal bool RoleMode { get => roleMode; set => roleMode = value; }
        private LoadoutRole activeRoleObj;
        private LoadoutRole lastNamedRole;
        private string roleNameEdit = "";
        // Was the name field actually typed into? Without this, every time the field loses focus it would
        // write, even when nothing changed (see the field itself).
        private bool roleNameEditDirty;
        private float roleNameEditAt;
        private readonly Dictionary<LoadoutRole, List<Loadout>> roleComp = new Dictionary<LoadoutRole, List<Loadout>>();
        private Loadout dragLoadout;
        // Dragging a WEAPON building block (lazy rework): the template is only created on DROP onto a role card,
        // not when the drag starts - otherwise the row jumps into the loadout section above it while the click
        // is still in progress (the row used to jump up above the heading).
        private ThingDef dragWeaponDef;
        private Vector2 roleSourceScroll, roleListScroll, rolePreviewScroll;
        private string roleSourceSearch = "", roleCardSearch = "", equipmentPackSearch = "";
        // Template name per weapon for the building-block list in Role Mode (lazy rework) - built once, because
        // the prefix comes from the mod settings and practically never changes.
        private Dictionary<ThingDef, string> roleWeaponLabelCache;
        // Default slots per weapon for the building-block list (the weapon view in the role editor lacked all
        // the important info). Exactly the same cache as Dialog_PickPacks.WeaponSlots - building a default slot
        // list is too expensive for every row in every frame, but it only changes with the mod settings, so
        // once per window lifetime.
        // The mass/bulk half was removed again when that column went away - a cache nobody reads anymore is
        // exactly the kind of leftover someone later mistakes for being in use.
        private readonly Dictionary<ThingDef, List<LoadoutSlot>> roleWeaponSlotsCache = new Dictionary<ThingDef, List<LoadoutSlot>>();
        private Vector2 equipmentPackScroll;
        // ====== Input buffers per ROLE, not per ROW INDEX ======
        //
        // Symptom: the ammo factor of the 'Support' role jumped from 2 to 1 and that of 'New role' from 1 to 2 -
        // the values had shifted by one row while the list order changed: the row was addressed by index
        // instead of by identity.
        //
        // The key used to be "c" + i + "a" - the running number of the drawn card. But the list re-sorts by
        // RoleSortTier, and that tier changes the moment a role gets its first building block (tier 2 "empty"
        // -> tier 0 "draft"). A single drop therefore shifts half the list. Afterwards buffer "c3a" still holds
        // the text of the role that was just in slot 3 - and Widgets.TextFieldNumeric writes exactly that text
        // as the VALUE into the role that now sits there. The numbers move down a row without anyone typing.
        //
        // Worse than the wrong number is the side effect: the card field calls AutoCommitRole on every detected
        // change - so the mod saves a role the player never touched, and for a GLOBAL role that ends up in the
        // mod settings file via PersistRoles, i.e. it affects every future game.
        //
        // Now the buffer is keyed on the role OBJECT (four fields per role, order Am/Me/Fo/Gr). Re-sorting can
        // no longer shift anything, and a deleted role's entry is dropped at the next cleanup because nothing
        // asks for it anymore.
        private readonly Dictionary<LoadoutRole, string[]> cardFactorBuffers = new Dictionary<LoadoutRole, string[]>();

        // FIX: Cache textures to avoid creating new Texture2D every frame

        private static Texture2D tEMP, tFlame, tTox, tSmoke, tAmmoOverlay, tStun, tConcuss, tFlash, tArmorPiercing, tFragment, tExplosive, tExplosiveSecondary;
        public static Texture2D TexEMP { get { if (tEMP == null) tEMP = ContentFinder<Texture2D>.Get("UI/Icon_EMP", false) ?? BaseContent.WhiteTex; return tEMP; } }
        public static Texture2D TexFlame { get { if (tFlame == null) tFlame = ContentFinder<Texture2D>.Get("UI/Icon_Flame", false) ?? BaseContent.WhiteTex; return tFlame; } }
        public static Texture2D TexTox { get { if (tTox == null) tTox = ContentFinder<Texture2D>.Get("UI/Icon_Tox", false) ?? BaseContent.WhiteTex; return tTox; } }
        public static Texture2D TexSmoke { get { if (tSmoke == null) tSmoke = ContentFinder<Texture2D>.Get("UI/Icon_Smoke", false) ?? BaseContent.WhiteTex; return tSmoke; } }
        public static Texture2D TexStun { get { if (tStun == null) tStun = ContentFinder<Texture2D>.Get("UI/Icon_Stun", false) ?? BaseContent.WhiteTex; return tStun; } }
        public static Texture2D TexConcuss { get { if (tConcuss == null) tConcuss = ContentFinder<Texture2D>.Get("UI/Icon_Concuss", false) ?? BaseContent.WhiteTex; return tConcuss; } }
        public static Texture2D TexFlash { get { if (tFlash == null) tFlash = ContentFinder<Texture2D>.Get("UI/Icon_Flash", false) ?? BaseContent.WhiteTex; return tFlash; } }
        public static Texture2D TexAmmoOverlay { get { if (tAmmoOverlay == null) tAmmoOverlay = ContentFinder<Texture2D>.Get("UI/Icon_AmmoOverlay", false) ?? BaseContent.WhiteTex; return tAmmoOverlay; } }
        public static Texture2D TexArmorPiercing { get { if (tArmorPiercing == null) tArmorPiercing = ContentFinder<Texture2D>.Get("UI/Icon_ArmorPiercing", false) ?? BaseContent.WhiteTex; return tArmorPiercing; } }
        public static Texture2D TexFragment { get { if (tFragment == null) tFragment = ContentFinder<Texture2D>.Get("UI/Icon_Fragment", false) ?? BaseContent.WhiteTex; return tFragment; } }
        public static Texture2D TexExplosive { get { if (tExplosive == null) tExplosive = ContentFinder<Texture2D>.Get("UI/Icon_Explosive", false) ?? BaseContent.WhiteTex; return tExplosive; } }
        public static Texture2D TexExplosiveSecondary { get { if (tExplosiveSecondary == null) tExplosiveSecondary = ContentFinder<Texture2D>.Get("UI/Icon_ExplosiveSecondary", false) ?? BaseContent.WhiteTex; return tExplosiveSecondary; } }

        // Draws the effect icons for a single ammo type left-to-right at (x,y). Each icon carries the small ammo
        // badge (top-right) to mark it as ammo-derived, and its own hover tooltip. Returns the total width used.
        // ammoDefForDetail (2026-07-31, optional, additive) - when the caller has the actual ammo ThingDef handy,
        // the tooltip gains a real per-round detail line (explosion radius, fragment count, ...) via LoadoutUtility.
        // AbilityDetailSuffix; callers with only a bare SpecialAbility bitmask (e.g. aggregated multi-source
        // displays) just omit it and get the old generic-only tooltip, unchanged.
        // maxWidth: the strip used to grow unbounded to the right. Eleven flags are possible, which is over
        // 200px - every column in the mod is narrower, so a weapon with many effects ran into the neighbouring
        // column. Instead of enlarging a constant in one place (which only covers the ONE observed case and
        // breaks again with the next effect flag), the strip now cuts itself off and appends a "+N" whose
        // tooltip lists the rest. The width is thus an upper bound rather than a hope, however many flags a
        // weapon ever gets. Default infinity = old behaviour for every caller without its own column width.
        // tipLines as in DrawWeaponSpecialIcons: if the caller collects them, the per-icon tooltips AND the
        // tooltip on the "+N" are skipped - ALL lines (including cut-off ones) go into the list, and the caller
        // puts one combined tooltip over the whole cell.
        public static float DrawAmmoEffectIcons(float x, float y, SpecialAbility ab, ThingDef ammoDefForDetail = null, float maxWidth = float.MaxValue, List<string> tipLines = null)
        {
            float ix = x;
            ThingDef proj = ammoDefForDetail != null ? LoadoutUtility.GetAmmoProjectile(ammoDefForDetail) : null;
            List<string> overflow = new List<string>();
            void Draw(SpecialAbility flag, Texture2D tex, string tip)
            {
                string full = tip + LoadoutUtility.AbilityDetailSuffix(flag, proj);
                if (tipLines != null) tipLines.Add(full);
                // 19px for this icon plus 20px reserve for a possible "+N" after it - otherwise the last icon
                // just barely fits and the counter hangs over the edge again.
                if (overflow.Count > 0 || ix - x + 19f + 20f > maxWidth) { overflow.Add(full); return; }
                Rect r = new Rect(ix, y, 16f, 16f);
                GUI.DrawTexture(r, tex);
                GUI.DrawTexture(new Rect(r.xMax - 7f, r.y - 3f, 9f, 9f), TexAmmoOverlay);
                if (tipLines == null) TooltipHandler.TipRegion(r, full);
                ix += 19f;
            }
            if (ab.HasFlag(SpecialAbility.EMP)) Draw(SpecialAbility.EMP, TexEMP, "CER_GenerateLoadouts_AmmoTipEMP".Translate());
            if (ab.HasFlag(SpecialAbility.Concussive)) Draw(SpecialAbility.Concussive, TexConcuss, "CER_GenerateLoadouts_AmmoTipConcussive".Translate());
            if (ab.HasFlag(SpecialAbility.Flash)) Draw(SpecialAbility.Flash, TexFlash, "CER_GenerateLoadouts_AmmoTipFlash".Translate());
            if (ab.HasFlag(SpecialAbility.Stun)) Draw(SpecialAbility.Stun, TexStun, "CER_GenerateLoadouts_AmmoTipStun".Translate());
            if (ab.HasFlag(SpecialAbility.Flame)) Draw(SpecialAbility.Flame, TexFlame, "CER_GenerateLoadouts_AmmoTipIncendiary".Translate());
            if (ab.HasFlag(SpecialAbility.Tox)) Draw(SpecialAbility.Tox, TexTox, "CER_GenerateLoadouts_AmmoTipToxic".Translate());
            if (ab.HasFlag(SpecialAbility.Smoke)) Draw(SpecialAbility.Smoke, TexSmoke, "CER_GenerateLoadouts_AmmoTipSmoke".Translate());
            if (ab.HasFlag(SpecialAbility.Fragment)) Draw(SpecialAbility.Fragment, TexFragment, "CER_GenerateLoadouts_AmmoTipFragment".Translate());
            if (ab.HasFlag(SpecialAbility.ArmorPiercing)) Draw(SpecialAbility.ArmorPiercing, TexArmorPiercing, "CER_GenerateLoadouts_AmmoTipArmorPiercing".Translate());
            if (ab.HasFlag(SpecialAbility.Explosive)) Draw(SpecialAbility.Explosive, TexExplosive, "CER_GenerateLoadouts_AmmoTipExplosive".Translate());
            if (ab.HasFlag(SpecialAbility.ExplosiveSecondary)) Draw(SpecialAbility.ExplosiveSecondary, TexExplosiveSecondary, "CER_GenerateLoadouts_AmmoTipExplosiveSecondary".Translate());
            ix += DrawOverflowMarker(ix, y, overflow, tip: tipLines == null);
            return ix - x;
        }

        // The "+N" at the end of a cut-off icon strip. Its own function so both strips (ammo and weapon) look
        // the same and use the same tooltip layout.
        private static float DrawOverflowMarker(float x, float y, List<string> hidden, bool tip = true)
        {
            if (hidden == null || hidden.Count == 0) return 0f;
            Rect r = new Rect(x, y, 20f, 16f);
            GameFont pf = Text.Font;
            Text.Font = GameFont.Tiny;
            GUI.color = new Color(1f, 1f, 1f, 0.75f);
            Widgets.Label(r, "+" + hidden.Count);
            GUI.color = Color.white;
            Text.Font = pf;
            // tip is off when the caller puts a combined tooltip over the whole cell - two overlapping tooltip
            // regions would otherwise show both boxes stacked.
            if (tip)
                TooltipHandler.TipRegion(r, "CER_GenerateLoadouts_MoreEffectsTip".Translate(hidden.Count.ToString())
                    + "\n" + string.Join("\n", hidden.ToArray()));
            return 20f;
        }

        // Draws a weapon's INTRINSIC special icons (no ammo badge) left-to-right at (x,y), mirroring the Special column
        // of the main list. Reused by the "produce missing weapons" popup so its rows match the loadout tab.
        // maxWidth as in DrawAmmoEffectIcons - see there. The overflow is less noticeable here because the tooltip
        // covers the whole strip anyway and names every line; it is cut off regardless so the icons don't run
        // into the neighbouring column.
        // tipLines (combined tooltip restored): if the caller collects the lines, this function registers NO
        // strip tooltip of its own - the caller puts one over the whole cell (intrinsic + ammo in one box).
        public static float DrawWeaponSpecialIcons(float x, float y, ThingDef weapon, float maxWidth = float.MaxValue, List<string> tipLines = null)
        {
            SpecialAbility ab = LoadoutUtility.DetectSpecialAbilities(weapon);
            LoadoutUtility.DetectArmorPiercing(weapon, out bool apIntrinsic, out _);
            ThingDef proj = LoadoutUtility.IntrinsicDetailProjectile(weapon);
            float ix = x;
            int hiddenCount = 0;
            string tip = "CER_GenerateLoadouts_SpecialAbilitiesHeader".Translate();
            void Draw(SpecialAbility flag, Texture2D tex, string line)
            {
                // The tooltip ALWAYS gets the line, even for cut-off icons - otherwise the information would be
                // lost, not just the picture.
                tip += "\n" + line + LoadoutUtility.AbilityDetailSuffix(flag, proj);
                if (hiddenCount > 0 || ix - x + 18f + 20f > maxWidth) { hiddenCount++; return; }
                GUI.DrawTexture(new Rect(ix, y, 16f, 16f), tex);
                ix += 18f;
            }
            if (ab.HasFlag(SpecialAbility.EMP)) Draw(SpecialAbility.EMP, TexEMP, "CER_GenerateLoadouts_WeaponTipEMP".Translate());
            if (ab.HasFlag(SpecialAbility.Concussive)) Draw(SpecialAbility.Concussive, TexConcuss, "CER_GenerateLoadouts_WeaponTipConcussive".Translate());
            if (ab.HasFlag(SpecialAbility.Flash)) Draw(SpecialAbility.Flash, TexFlash, "CER_GenerateLoadouts_WeaponTipFlash".Translate());
            if (ab.HasFlag(SpecialAbility.Stun)) Draw(SpecialAbility.Stun, TexStun, "CER_GenerateLoadouts_WeaponTipStun".Translate());
            if (ab.HasFlag(SpecialAbility.Flame)) Draw(SpecialAbility.Flame, TexFlame, "CER_GenerateLoadouts_WeaponTipIncendiary".Translate());
            if (ab.HasFlag(SpecialAbility.Tox)) Draw(SpecialAbility.Tox, TexTox, "CER_GenerateLoadouts_WeaponTipToxic".Translate());
            if (ab.HasFlag(SpecialAbility.Smoke)) Draw(SpecialAbility.Smoke, TexSmoke, "CER_GenerateLoadouts_WeaponTipSmoke".Translate());
            if (ab.HasFlag(SpecialAbility.Fragment)) Draw(SpecialAbility.Fragment, TexFragment, "CER_GenerateLoadouts_WeaponTipFragment".Translate());
            if (apIntrinsic) Draw(SpecialAbility.ArmorPiercing, TexArmorPiercing, "CER_GenerateLoadouts_WeaponTipArmorPiercing".Translate());
            if (ab.HasFlag(SpecialAbility.Explosive)) Draw(SpecialAbility.Explosive, TexExplosive, "CER_GenerateLoadouts_WeaponTipExplosive".Translate());
            if (ab.HasFlag(SpecialAbility.ExplosiveSecondary)) Draw(SpecialAbility.ExplosiveSecondary, TexExplosiveSecondary, "CER_GenerateLoadouts_WeaponTipExplosiveSecondary".Translate());
            // "+N" after the strip if it was cut off - the tooltip underneath names all lines anyway, the marker
            // only says there are more than are visible.
            if (hiddenCount > 0)
            {
                GameFont pf = Text.Font;
                Text.Font = GameFont.Tiny;
                GUI.color = new Color(1f, 1f, 1f, 0.75f);
                Widgets.Label(new Rect(ix, y, 20f, 16f), "+" + hiddenCount);
                GUI.color = Color.white;
                Text.Font = pf;
                ix += 20f;
            }
            if (tipLines != null) { if (ix > x || hiddenCount > 0) tipLines.Add(tip.ToString()); }
            else if (ix > x) TooltipHandler.TipRegion(new Rect(x, y, ix - x, 16f), tip);
            return ix - x;
        }

        public Dialog_GenerateLoadouts()
        {
            allWeapons = DefDatabase<ThingDef>.AllDefs.Where(LoadoutUtility.IsRealWeaponDef).ToList();
            allWeapons.SortBy(w => w.label);

            allMeleeWeapons = allWeapons.Where(w => w.IsMeleeWeapon && !IsGrenade(w)).ToList();
            allMeleeWeapons.SortBy(w => w.label);
            
            allGrenades = allWeapons.Where(w => IsGrenade(w)).ToList();
            allGrenades.SortBy(w => w.label);
            
            // Shared attribute-driven option lists (same in the mod settings) - see LoadoutUtility for the rules.
            allMedicines = LoadoutUtility.LoadoutMedicineOptions();
            allFoods = LoadoutUtility.LoadoutFoodOptions();

            availableWeaponsCache = new HashSet<ThingDef>(LoadoutUtility.GetAllOwnedWeapons());

            // Everything the player can edit in the tab comes from the per-save UI state (seeded from settings for
            // a fresh colony, null = not seeded yet) so it persists across window opens and reloads. Direction
            // contract: settings changes flow INTO the save, tab edits never write the global settings back.
            CERUiState ui = RoleUtility.Ui();
            var cfg = CEReloadedMod.settings;
            sidearmMaxBulk = ui.loSidearmMaxBulk ?? (cfg != null ? cfg.sidearmMaxBulk : sidearmMaxBulk);
            // Meal/medicine are deliberately NO LONGER read here - see the comment at the removed fields
            // above.
        }

        // ---- Dynamic ("Auto") sidearm, grenade & shield defaults ---------------------------------------------------
        // The sidearm/grenade/shield default settings accept CEReloadedSettings.AutoPick: instead of one hardcoded
        // def, the pick comes from what the colony can actually field RIGHT NOW - owned gear first (handing out is
        // free), then researched+craftable gear discounted by real material affordability (the same economy signal
        // the Craft/Budget sliders lean on). Quality blends each candidate's anti-flesh/anti-mech target scores by
        // the mech slider, so "Anti-mech" pulls the picks toward EMP grenades and AP-capable backups without any
        // hardcoded def names. Candidate pools are cached per tab lifetime (RefreshCache() invalidates); the winner
        // is re-scored per call because it depends on live inputs (mech slider, material stock).
        private List<ThingDef> autoPoolSidearmMelee, autoPoolSidearmRanged, autoPoolShield;
        private Dictionary<ThingDef, int> autoOwned;
        private bool autoPoolsDone;
        private string autoPoolsBulkKey;   // the sidearm bulk cap the pools were built with

        private void EnsureAutoPools()
        {
            // Self-invalidating on a bulk-cap change: the cap decides WHICH weapons qualify as sidearms, so pools
            // built with the old value would keep serving stale Auto picks.
            string bulkNow = sidearmMaxBulk ?? "5.0";
            if (autoPoolsDone && autoPoolsBulkKey == bulkNow) return;
            autoPoolsDone = true;
            autoPoolsBulkKey = bulkNow;
            autoPoolSidearmMelee = new List<ThingDef>();
            autoPoolSidearmRanged = new List<ThingDef>();
            autoPoolShield = new List<ThingDef>();

            if (!float.TryParse(bulkNow.Replace(',', '.'), System.Globalization.NumberStyles.Any, System.Globalization.CultureInfo.InvariantCulture, out float maxBulk)) maxBulk = 5.0f;
            StatDef bulkStat = DefDatabase<StatDef>.GetNamed("Bulk", false);
            autoOwned = Dialog_AssignLoadouts.BuildOwnedWeaponCounts();
            bool BulkOk(ThingDef d) => bulkStat == null || d.GetStatValueAbstract(bulkStat) <= maxBulk + 0.01f;

            foreach (ThingDef d in DefDatabase<ThingDef>.AllDefsListForReading)
            {
                if (d.destroyOnDrop) continue;
                bool own = autoOwned.TryGetValue(d, out int c) && c > 0;
                if (!own && !IsCraftable(d)) continue;

                if (LoadoutUtility.IsShieldApparel(d)) { autoPoolShield.Add(d); continue; }
                if (!d.IsWeapon || LoadoutUtility.IsUtilityWeapon(d)) continue;

                if (IsGrenade(d)) continue;

                // Sidearm classes are size-defined, not list-defined (community rule of thumb): light one-handed
                // melee, and any firearm compact enough to ride along - pistols always, machine pistols and heavy
                // SMGs under the default bulk cap of 5, rifles (bulk 10+) never.
                if (d.IsMeleeWeapon && LoadoutUtility.IsOneHandedWeapon(d) && BulkOk(d))
                    autoPoolSidearmMelee.Add(d);
                else if (d.IsRangedWeapon && !d.IsMeleeWeapon && !LoadoutUtility.IsSelfAmmoWeapon(d) && BulkOk(d))
                {
                    List<Def> ammo = LoadoutUtility.GetLoadoutAmmoDefs(d, false);
                    if (ammo.Count == 0 || ammo.Any(a => IsResearched(a)))
                        autoPoolSidearmRanged.Add(d);
                }
            }
        }

        // Fallback inputs when no live slider drives a pick: the persisted per-save UI state (what the popup last
        // used). econ 0 = materials flush, 1 = tight - derived from the pool slider's mindset (left = stick to what
        // you own = thrifty).
        private static float UiMech() { CERUiState ui = RoleUtility.Ui(); return ui != null ? ui.mechAffinity : 0.5f; }
        private static float UiEcon() { CERUiState ui = RoleUtility.Ui(); return ui != null ? 1f - ui.agPool : 0.5f; }

        // One economy-weighted pick from a candidate pool. `mech` (0..1) blends anti-flesh vs anti-mech quality;
        // `econ` (0..1) scales how hard a craft-only candidate is discounted for scarce materials - the tighter the
        // economy, the more a cheap-but-owned backup beats a marginally better one that must be crafted.
        private ThingDef PickAuto(List<ThingDef> pool, float mech, float econ, System.Func<ThingDef, float> quality = null)
        {
            if (pool == null) return null;
            ThingDef best = null;
            float bestV = 0f;
            float affordWeight = Mathf.Lerp(0.35f, 0.75f, Mathf.Clamp01(econ));
            foreach (ThingDef d in pool)
            {
                float q;
                if (quality != null) q = quality(d);
                else
                {
                    // researchedOnly: PickBest CHOOSES a weapon, so it is a suggestion, not a
                    // display.
                    LoadoutUtility.GetWeaponTargetScores(d, out float f, out float m, researchedOnly: true);
                    q = Mathf.Lerp(f, m, Mathf.Clamp01(mech));
                }
                if (q <= 0f) continue;
                bool own = autoOwned != null && autoOwned.TryGetValue(d, out int c) && c > 0;
                float afford = own ? 1f : LoadoutUtility.MaterialAffordability(d);
                float v = q * Mathf.Lerp(1f, afford, affordWeight) * (own ? 1.2f : 1f);
                if (v > bestV) { bestV = v; best = d; }
            }
            return best;
        }

        // Shield fitness: market value proxies protection/tech, but the wielder has to LUG the thing. Mass and
        // bulk discount the pick - much harder for MOBILE loadouts (ranged main with a blade backup) than for a
        // dedicated melee blocker. This is what stops a heavy tower shield from being "the best shield" for the
        // whole squad while the blocker who actually stands in the line still gets it.
        private static StatDef shieldBulkStat;
        private static float ShieldQuality(ThingDef d, bool mobile)
        {
            if (shieldBulkStat == null) shieldBulkStat = DefDatabase<StatDef>.GetNamed("Bulk", false);
            float mass = d.GetStatValueAbstract(StatDefOf.Mass);
            float bulk = shieldBulkStat != null ? d.GetStatValueAbstract(shieldBulkStat) : 0f;
            float burden = mobile ? 1f + mass * 0.35f + bulk * 0.15f
                                  : 1f + mass * 0.08f + bulk * 0.04f;
            return Mathf.Max(1f, d.BaseMarketValue) / burden;
        }

        // The editable pool, exposed for the auto-generate popup's per-row shield picker.
        internal List<ThingDef> ShieldPool() { EnsureAutoPools(); return autoPoolShield.OrderBy(d => d.label).ToList(); }

        // Resolve one of the sidearm/grenade/shield default settings: "" = none, AutoPick = the dynamic colony-aware
        // pick above, anything else = that exact def (the player's explicit choice always wins).
        private ThingDef ResolveDefaultDef(string defName, System.Func<ThingDef> auto)
        {
            if (string.IsNullOrEmpty(defName)) return null;
            if (defName == CEReloadedSettings.AutoPick) return auto();
            return DefDatabase<ThingDef>.GetNamedSilentFail(defName);
        }

        internal ThingDef DefaultSidearmMelee(float? mech = null, float? econ = null) => CEReloadedMod.settings == null ? null
            : ResolveDefaultDef(CEReloadedMod.settings.defSidearmMeleeDefName, () => { EnsureAutoPools(); return PickAuto(autoPoolSidearmMelee, mech ?? UiMech(), econ ?? UiEcon()); });
        internal ThingDef DefaultSidearmRanged(float? mech = null, float? econ = null) => CEReloadedMod.settings == null ? null
            : ResolveDefaultDef(CEReloadedMod.settings.defSidearmRangedDefName, () => { EnsureAutoPools(); return PickAuto(autoPoolSidearmRanged, mech ?? UiMech(), econ ?? UiEcon()); });

        // A fresh default WeaponLoadoutSettings for one weapon (prefix name, default sidearm/food/medicine, ammo all
        // zero except the picked default). Used both to seed every weapon on init and to reset one on Unload.
        // Which default sidearm a weapon's loadout should carry: the sidearm complements the main weapon, so a
        // ranged trooper gets the melee backup and a melee fighter gets the ranged backup (falling back to the other
        // if only one is configured). "" for both = no sidearm.
        internal ThingDef PickDefaultSidearm(ThingDef mainWeapon, float? mech = null, float? econ = null)
        {
            ThingDef melee = DefaultSidearmMelee(mech, econ);
            ThingDef ranged = DefaultSidearmRanged(mech, econ);
            // The sidearm complements the main: ranged mains (launchers and explosive mains included - their backup
            // must work point-blank) get the melee backup, melee fighters the ranged one; fall back to the other
            // class if only one resolves.
            bool mainRanged = mainWeapon != null && mainWeapon.IsRangedWeapon && !mainWeapon.IsMeleeWeapon;
            ThingDef pick = mainRanged ? (melee ?? ranged) : (ranged ?? melee);
            return (pick != null && pick != mainWeapon) ? pick : null;
        }

        internal ThingDef DefaultShield(float? econ = null, bool mobile = false)
        {
            var cfg = CEReloadedMod.settings;
            return cfg == null ? null : ResolveDefaultDef(cfg.defShieldDefName, () => { EnsureAutoPools(); return PickAuto(autoPoolShield, 0.5f, econ ?? UiEcon(), d => ShieldQuality(d, mobile)); });
        }

        // static: the body reads only mod settings and static helpers, no state of this window. That way the
        // overload preview in the solver can ask for the same default without a dialog having to be open or
        // passed through the solver.
        internal static WeaponLoadoutSettings BuildDefaultSettings(ThingDef w)
        {
            // Naming rule ONLY from VirtualLoadoutLabel: previously three places computed the same prefix formula
            // independently - any divergence would have broken the "does it already exist?" detection that
            // dedup, fill-gaps seeding and the delete path depend on.
            var s = new WeaponLoadoutSettings
            {
                loadoutName = LoadoutUtility.VirtualLoadoutLabel(w)
            };

            List<Def> weaponAmmoDefs = LoadoutUtility.GetLoadoutAmmoDefs(w, IsSelfAmmoWeapon(w));
            if (weaponAmmoDefs.Count > 0)
            {
                foreach (var a in weaponAmmoDefs) s.ammoCounts[a] = "0";
                Def defaultAmmo = LoadoutUtility.PickDefaultAmmo(weaponAmmoDefs);
                s.ammoCounts[defaultAmmo] = LoadoutUtility.GetDefaultAmmoCountForWeapon(w).ToString();
            }
            return s;
        }

        // Create (or fetch) the default loadout for a weapon - used when auto-assign picks a weapon that has no
        // loadout yet (a "virtual" candidate). Uses the same defaults as the Generate tab (prefix name, default ammo,
        // sidearm, supplies) and never overwrites an existing loadout of that name.
        // WHAT goes into a generated weapon template - the weapon plus the configured ammo amounts.
        // Extracted so the overload warning before applying can weigh a template that doesn't exist yet: the
        // solver regularly picks a weapon WITHOUT a template ("virtual"), and that is exactly where the whole
        // load is new and most likely too heavy.
        // Deliberately a shared function and not a second calculation: a warning that calculates differently
        // from what the generator builds would be worse than none.
        internal static List<LoadoutSlot> DefaultSlotsFor(ThingDef wDef, WeaponLoadoutSettings settings)
        {
            List<LoadoutSlot> slots = new List<LoadoutSlot>();
            if (!IsGrenade(wDef)) slots.Add(new LoadoutSlot(wDef, 1));
            if (settings?.ammoCounts != null)
                foreach (var kv in settings.ammoCounts)
                {
                    if (!int.TryParse(kv.Value, out int amount) || amount <= 0) continue;
                    if (kv.Key is ThingDef td) slots.Add(new LoadoutSlot(td, amount));
                    else if (kv.Key is LoadoutGenericDef gd) slots.Add(new LoadoutSlot(gd, amount));
                }
            return slots;
        }

        // Mass and bulk of the same default, without creating anything.
        public static void DefaultMassBulkFor(ThingDef wDef, out float mass, out float bulk)
        {
            mass = 0f; bulk = 0f;
            if (wDef == null) return;
            LoadoutUtility.SlotsMassBulk(DefaultSlotsFor(wDef, BuildDefaultSettings(wDef)), out mass, out bulk);
        }

        // Static (lazy rework): this is THE one place where a weapon template is created in the background.
        // Previously the call went through parentWindow.LoadoutsDialog - on two call paths (arm chain,
        // readiness indicator) that was null, and a virtual pick was silently lost on apply.
        public static Loadout EnsureDefaultLoadout(ThingDef w)
        {
            WeaponLoadoutSettings st = BuildDefaultSettings(w);
            string name = string.IsNullOrEmpty(st.loadoutName) ? LoadoutUtility.VirtualLoadoutLabel(w) : st.loadoutName;
            Loadout existing = LoadoutManager.Loadouts.FirstOrDefault(l => l != null && !l.defaultLoadout && l.label == name);
            if (existing != null) return existing;
            return CreateOrUpdateWeaponLoadout(w, st, out _);
        }

        // ---- Auto-generate support (generate-section of Dialog_SquadSetup) ----------------------------------------
        internal List<ThingDef> AllWeaponsList => allWeapons;
        internal HashSet<ThingDef> AvailableWeaponsCache => availableWeaponsCache;

        // One ammo ration inside a planned loadout (a loadout can carry several types, e.g. FMJ + EMP).
        internal class AmmoPick
        {
            public Def def;
            public int count;
        }

        // Live edit: while a loadout is loaded, any change writes straight back to it. This rewrites the loaded
        // loadout's slots from the current settings - contents only, no ammo bills, no messages - and is gated behind
        // a change signature so it only runs when something actually changed.
        private static void AddDefSlot(Loadout lo, Def d, int amount)
        {
            if (d is ThingDef td) lo.AddSlot(new LoadoutSlot(td, amount));
            else if (d is LoadoutGenericDef gd) lo.AddSlot(new LoadoutSlot(gd, amount));
        }

        // Defs are immutable at runtime, and both checks run per weapon per frame in the list pre-filter -
        // memoised for the session so the tag/label ToLower string churn happens once per def, not every frame.
        private static readonly Dictionary<ThingDef, bool> grenadeCache = new Dictionary<ThingDef, bool>();

        internal static bool IsGrenade(ThingDef w)
        {
            if (w == null) return false;
            if (grenadeCache.TryGetValue(w, out bool cached)) return cached;
            bool result = ComputeIsGrenade(w);
            grenadeCache[w] = result;
            return result;
        }

        private static bool ComputeIsGrenade(ThingDef w)
        {
            if (w.GetCompProperties<CompProperties_AmmoUser>() != null) return false;
            if (w.weaponTags != null && w.weaponTags.Any(t => t.ToLower().Contains("grenade") || t.ToLower().Contains("bomb") || t.ToLower().Contains("throw"))) return true;
            if (w.thingCategories != null && w.thingCategories.Any(c => c.defName.ToLower().Contains("grenade"))) return true;
            return false;
        }

        // Routes ammo-bill generation for weapons that are their own ammo (grenades + CE AmmoDef weapons like javelins).
        // The shared definition lives in LoadoutUtility so the weapon-audit tool uses the exact same rule.
        // static: only forwards to LoadoutUtility, no instance state - BuildDefaultSettings is static too and
        // needs it that way.
        private static bool IsSelfAmmoWeapon(ThingDef w) => LoadoutUtility.IsSelfAmmoWeapon(w);

        internal static bool IsResearched(Def def)
        {
            if (def is LoadoutGenericDef gd)
            {
                ThingDef gun = LoadoutUtility.GetWeaponForGenericAmmo(gd);
                if (gun != null)
                {
                    foreach (ThingDef ammo in LoadoutUtility.GetAmmoTypesFor(gun))
                    {
                        if (IsResearched(ammo)) return true;
                    }
                }
                return false;
            }
            if (def is ThingDef td)
            {
                if (td.recipeMaker != null && td.recipeMaker.researchPrerequisite != null && !td.recipeMaker.researchPrerequisite.IsFinished) return false;
                if (td.recipeMaker != null && td.recipeMaker.researchPrerequisites != null && td.recipeMaker.researchPrerequisites.Any(x => !x.IsFinished)) return false;

                // Also check the direct recipe that produces it
                RecipeDef recipe = LoadoutUtility.GetRecipeFor(td);
                if (recipe != null && !LoadoutUtility.RecipeIsResearched(recipe)) return false;
            }

            return true;
        }

        // A weapon is craftable only if it actually has a crafting recipe (found-only weapons like persona/unique gear
        // have none) and that recipe is researched. Used by the "Researched" weapons filter so it never lists gear you
        // can only find, not make.
        internal bool IsCraftable(ThingDef w)
        {
            return w != null && LoadoutUtility.GetRecipeFor(w) != null && IsResearched(w);
        }

        private static string CategoryLabel(WeaponCategory c)
        {
            switch (c)
            {
                case WeaponCategory.Melee: return "CER_GenerateLoadouts_CategoryMelee".Translate();
                case WeaponCategory.Firearms: return "CER_GenerateLoadouts_CategoryFirearms".Translate();
                case WeaponCategory.HeavyWeapons: return "CER_GenerateLoadouts_CategoryHeavy".Translate();
                case WeaponCategory.Others: return "CER_GenerateLoadouts_CategoryOthers".Translate();
                default: return "CER_GenerateLoadouts_CategoryAllTypes".Translate();
            }
        }


        internal void DoRoleMode(Rect inRect)
        {
            CEReloadedSettings st = CEReloadedMod.settings;
            RoleUtility.EnsureSeeded(st);

            Text.Font = GameFont.Medium;
            Widgets.Label(new Rect(0f, 0f, inRect.width - 170f, 30f), "CER_GenerateLoadouts_RoleModeTitle".Translate());
            Text.Font = GameFont.Small;
            // The "don't rename [Role] loadouts" warning has been removed. It was correct while detection relied
            // on the label - now it relies on a dedicated ID, and a rename (even in CE's own editor) can no longer
            // break the link. A warning that is no longer true teaches people to skip warnings.

            float top = 58f;
            // 210 -> 290: both lists slightly narrower so the role frame on the right and the preview get
            // bigger and show more. The preview is where you actually read what a role contains, and it often
            // showed "5 more below". The 80 points come from the three boxes above - all three scroll anyway;
            // there it costs one row, here it gains three.
            float previewH = 290f;
            float panelsH = inRect.height - top - previewH - 8f;
            float gap = 12f;
            float leftW = (inRect.width - gap) * 0.52f;
            // Left column splits in two: loadouts (halved from its old full height) above, Equipment
            // Packs below - both are drag sources onto Role cards (a pack is just a Loadout with no
            // main weapon, so the existing drag/merge mechanism handles it with no changes of its own).
            float leftTopH = (panelsH - gap) * 0.5f;
            Rect leftPanel = new Rect(0f, top, leftW, leftTopH);
            Rect leftPanelPacks = new Rect(0f, top + leftTopH + gap, leftW, panelsH - leftTopH - gap);
            Rect rightPanel = new Rect(leftW + gap, top, inRect.width - leftW - gap, panelsH);

            DrawRoleSourceList(leftPanel);
            DrawEquipmentPackSourceList(leftPanelPacks);
            DrawRoleCards(rightPanel, st);

            Rect previewPanel = new Rect(0f, inRect.height - previewH, inRect.width, previewH);
            DrawRolePreview(previewPanel, st);

            // Finish any drag on mouse-up, and draw the floating chip that follows the cursor while dragging.
            if (dragLoadout != null || dragWeaponDef != null)
            {
                Vector2 mp = Event.current.mousePosition;
                Rect chip = new Rect(mp.x + 10f, mp.y + 6f, 190f, 24f);
                Widgets.DrawBoxSolid(chip, new Color(0.15f, 0.15f, 0.17f, 0.92f));
                if (dragWeaponDef != null)
                    Widgets.ThingIcon(new Rect(chip.x + 2f, chip.y + 1f, 22f, 22f), dragWeaponDef);
                GUI.color = new Color(0.85f, 0.9f, 1f);
                string chipLbl = dragLoadout != null ? RoleUtility.DisplayLabel(dragLoadout) : dragWeaponDef.LabelCap.ToString();
                float chipTextX = dragWeaponDef != null ? chip.x + 26f : chip.x + 4f;
                Widgets.Label(new Rect(chipTextX, chip.y + 1f, chip.xMax - chipTextX - 4f, 22f), chipLbl.Truncate(chip.xMax - chipTextX - 8f));
                GUI.color = Color.white;
            }
            if (Event.current.type == EventType.MouseUp) { dragLoadout = null; dragWeaponDef = null; }
        }

        private void DrawRoleSourceList(Rect panel)
        {
            Widgets.DrawMenuSection(panel);
            Rect header = panel.ContractedBy(6f);
            // Measure the label width instead of a fixed 96f: in German BOTH boxes showed only the start of the
            // word - the wrap of the longer translation fell outside the 22f rect, so two different sections
            // appeared to have the same name. The search field moves along; the 60% cap keeps it usable if a
            // language gets even longer.
            string srcHeaderTxt = "CER_GenerateLoadouts_YourLoadoutsHeader".Translate();
            float srcHeaderW = Mathf.Min(Text.CalcSize(srcHeaderTxt).x + 4f, header.width * 0.6f);
            Widgets.Label(new Rect(header.x, header.y, srcHeaderW, 22f), srcHeaderTxt.Truncate(srcHeaderW));
            Rect roleSourceFieldRect = new Rect(header.x + srcHeaderW + 4f, header.y - 2f, header.width - srcHeaderW - 4f - 24f, 22f);
            roleSourceSearch = Widgets.TextField(roleSourceFieldRect, roleSourceSearch);
            if (LoadoutUtility.ClearSearchButton(roleSourceFieldRect, header.y - 2f, roleSourceSearch)) roleSourceSearch = "";

            // Sources = your real loadouts (not the default one, and not existing role loadouts, Equipment
            // Packs - packs get their own source list below - or the hidden per-pawn "[Pawn-Loadout]"
            // anchors, which are internal bookkeeping never meant to be dragged/picked by a player).
            // In addition to the identity check (IsPlainWeaponLoadout asks IsRoleLoadout), the MARKER is checked
            // too - and this is the only place in the mod where that is still allowed.
            // Reason: the identity can be missing temporarily (a loadout whose role was just reset or deleted
            // belongs to nobody for a moment), and in exactly that moment it showed up here as a draggable
            // "weapon template" - with the result that a role could be dropped into a role. The marker works as
            // a safety net here because it can only exclude MORE: it decides nothing about display or behaviour,
            // it just keeps something out of a list where it never belongs. The root cause is fixed as well
            // (RoleUtility.AdoptRoleLoadoutsByName on the reset button).
            List<Loadout> sources = LoadoutManager.Loadouts
                .Where(l => LoadoutUtility.IsPlainWeaponLoadout(l) && !RoleUtility.IsRoleLabel(l.label)
                    && (string.IsNullOrEmpty(roleSourceSearch) || (l.label != null && l.label.IndexOf(roleSourceSearch, StringComparison.OrdinalIgnoreCase) >= 0)))
                .ToList();

            // WEAPONS as building blocks (lazy rework): this list used to be fed ONLY from pre-generated
            // templates - without bulk generation it would be empty and the role builder dead. Now every weapon
            // that has no default template yet is listed below the loadouts; on pickup (drag start) the shared
            // template is created invisibly via EnsureDefaultLoadout.
            // Label dedup against the existing loadouts per frame (one HashSet build, same order of magnitude
            // as the sources query above); the template names per weapon are cached - the prefix comes from the
            // mod settings and practically never changes (worst case, a weapon shows up next to its template
            // until the window is reopened).
            HashSet<string> existingLabels = new HashSet<string>(LoadoutManager.Loadouts
                .Where(l => l != null && !l.defaultLoadout && l.label != null).Select(l => l.label));
            if (roleWeaponLabelCache == null)
            {
                roleWeaponLabelCache = new Dictionary<ThingDef, string>();
                foreach (ThingDef w in allWeapons) roleWeaponLabelCache[w] = LoadoutUtility.VirtualLoadoutLabel(w);
            }
            List<ThingDef> weaponBlocks = allWeapons
                .Where(w => !existingLabels.Contains(roleWeaponLabelCache[w])
                    && (string.IsNullOrEmpty(roleSourceSearch) || (w.label != null && w.label.IndexOf(roleSourceSearch, StringComparison.OrdinalIgnoreCase) >= 0)))
                .ToList();

            // ===== Columns as in the weapon picker window: the weapon view in the role editor lacked all the
            // important info - reworked after the "Choose weapon" window, but with the layout adapted to the
            // available space =====
            //
            // Previously this showed only a name, plus an icon for weapons. But someone building a role decides
            // exactly the same things as in the picker window: is the weapon any good, is one available at all,
            // and what does it cost in carrying capacity. That was only in the tooltip - so only after aiming and
            // waiting, and one row at a time.
            //
            // The fixed column widths of Dialog_PickPacks are deliberately NOT copied: this box is roughly half
            // as wide as that window. Instead, whatever no longer fits drops out from right to left, and the name
            // gets what is left (never less than NameMinW). The drop order is chosen by usefulness: the contents
            // strip first (it is also in the tooltip), then mass/bulk, then availability, the score last.
            const float SrcRowH = 30f;
            const float NameMinW = 140f;
            float rowW = header.width - 16f;                  // minus scrollbar
            float srcScoreW = 0f, srcSpecW = 0f, srcAvailW = 0f, srcContW = 0f;
            float restW = rowW - 52f;                         // lock + icon + margins
            if (restW >= NameMinW + 68f) { srcScoreW = 68f; restW -= srcScoreW; }
            // Mass/bulk is OUT, replaced by the damage column: instead of mass/bulk, more important info such as
            // the damage type. When building a role what matters is what the weapon is good AGAINST - carrying
            // capacity is decided at the colonist, not the building block. Same icons as in the picker window:
            // intrinsic weapon effects, followed by those of the default ammo with the badge.
            if (restW >= NameMinW + 96f) { srcSpecW = 96f; restW -= srcSpecW; }
            if (restW >= NameMinW + 62f) { srcAvailW = 62f; restW -= srcAvailW; }
            if (restW >= NameMinW + 104f) { srcContW = 104f; restW -= srcContW; }
            float srcNameW = restW;

            // Column header - three bars side by side can't be told apart without labels. Same style as the
            // mod's other header rows: Tiny, 55% opacity, plain label.
            Rect colHead = new Rect(header.x, header.y + 26f, rowW, 16f);
            Text.Font = GameFont.Tiny;
            GUI.color = new Color(1f, 1f, 1f, 0.55f);
            float chx = colHead.x + 52f + srcNameW;
            if (srcScoreW > 0f) { Widgets.Label(new Rect(chx, colHead.y, srcScoreW - 4f, 16f), "CER_PickPacks_ScoreColumnHeader".Translate()); chx += srcScoreW; }
            if (srcSpecW > 0f) { Widgets.Label(new Rect(chx, colHead.y, srcSpecW - 4f, 16f), "CER_GenerateLoadouts_ColSpecial".Translate()); chx += srcSpecW; }
            if (srcAvailW > 0f) { Widgets.Label(new Rect(chx, colHead.y, srcAvailW - 4f, 16f), "CER_PickPacks_AvailColumnHeader".Translate()); chx += srcAvailW; }
            if (srcContW > 0f) Widgets.Label(new Rect(chx, colHead.y, srcContW - 4f, 16f), "CER_PickPacks_ContentsColumnHeader".Translate());
            Text.Font = GameFont.Small;
            GUI.color = Color.white;

            Rect outRect = new Rect(header.x, header.y + 44f, header.width, header.height - 44f);

            // ===== ONE list instead of two sections (assigned weapons used to jump to the top - fixed) =====
            //
            // Previously the existing templates were on top, and below a subheading the weapons without a
            // template. As soon as a weapon was assigned anywhere, its template was created - and its row jumped
            // from the lower block into the upper one. To the player that's the same weapon in a different spot,
            // without having done anything in this list.
            //
            // The difference was purely internal anyway (template already exists / gets created on drop); both
            // rows look the same and do the same thing when dragged. So one shared list, alphabetical by the
            // DISPLAYED name - a weapon always stays in the same spot, whether or not someone is carrying it. The
            // only exception remains locking: locked rows sink to the end, but that is a player action in this
            // very list.
            var srcRows = new List<KeyValuePair<Loadout, ThingDef>>();
            foreach (Loadout lo in sources) srcRows.Add(new KeyValuePair<Loadout, ThingDef>(lo, null));
            foreach (ThingDef w in weaponBlocks) srcRows.Add(new KeyValuePair<Loadout, ThingDef>(null, w));
            string SrcRowName(KeyValuePair<Loadout, ThingDef> r)
                => r.Key != null ? (RoleUtility.DisplayLabel(r.Key) ?? "") : (r.Value.LabelCap.ToString() ?? "");
            bool SrcRowLocked(KeyValuePair<Loadout, ThingDef> r)
                => r.Key != null ? LoadoutUtility.IsLoadoutDisabled(r.Key) : LoadoutUtility.IsWeaponDisabled(r.Value);
            srcRows = srcRows.OrderBy(r => SrcRowLocked(r) ? 1 : 0)
                .ThenBy(SrcRowName, StringComparer.OrdinalIgnoreCase).ToList();

            Rect viewRect = new Rect(0, 0, rowW, srcRows.Count * SrcRowH + 4f);
            Widgets.BeginScrollView(outRect, ref roleSourceScroll, viewRect);
            float ry = 0f;
            foreach (KeyValuePair<Loadout, ThingDef> r in srcRows)
            {
                Rect row = new Rect(0, ry, rowW, SrcRowH - 2f);
                if (r.Key != null) DrawRoleSourceLoadoutRow(row, r.Key, srcNameW, srcScoreW, srcSpecW, srcAvailW, srcContW);
                else DrawRoleSourceWeaponRow(row, r.Value, srcNameW, srcScoreW, srcSpecW, srcAvailW, srcContW);
                ry += SrcRowH;
            }
            if (srcRows.Count == 0)
            {
                GUI.color = new Color(1f, 1f, 1f, 0.5f);
                // Empty because of the SEARCH is different from empty because nothing exists yet: the
                // neighbouring "Your Equipment" box has long distinguished the two cases, while this one claimed
                // "No loadouts yet" for every search term without matches - i.e. that there were no templates at
                // all, while the list was full without a search term.
                Widgets.Label(new Rect(4f, 4f, viewRect.width - 8f, 40f), string.IsNullOrEmpty(roleSourceSearch)
                    ? "CER_GenerateLoadouts_NoLoadoutsYet".Translate()
                    : "CER_GenerateLoadouts_NoLoadoutsMatchSearch".Translate(roleSourceSearch));
                GUI.color = Color.white;
            }
            Widgets.EndScrollView();
        }

        // The damage column of a building-block row: the weapon's intrinsic effects, followed by those of the
        // default ammo WITH the ammo badge - exactly the cells from Dialog_PickPacks.DrawDamageCell, so the same
        // weapon doesn't show two different pictures here and in the picker window. ONE combined tooltip over
        // the whole cell instead of per-icon tooltips.
        private static void DrawSourceDamageCell(Rect cell, ThingDef weapon, IEnumerable<LoadoutSlot> slots)
        {
            if (weapon == null) return;
            float iy = cell.y + (cell.height - 16f) / 2f;
            List<string> tipLines = new List<string>();
            float used = DrawWeaponSpecialIcons(cell.x, iy, weapon, cell.width, tipLines);
            ThingDef ammo = null;
            if (slots != null)
                foreach (LoadoutSlot s in slots)
                    if (s.thingDef != null && LoadoutUtility.IsAmmoDef(s.thingDef)) { ammo = s.thingDef; break; }
            if (ammo != null)
            {
                SpecialAbility ab = LoadoutUtility.GetAmmoEffects(ammo);
                if (ab != SpecialAbility.None)
                    DrawAmmoEffectIcons(cell.x + used + (used > 0f ? 2f : 0f), iy, ab, ammo,
                        Mathf.Max(0f, cell.width - used - (used > 0f ? 2f : 0f)), tipLines);
            }
            if (tipLines.Count > 0 && LoadoutUtility.TipWanted(cell))
                TooltipHandler.TipRegion(cell, string.Join("\n\n", tipLines));
        }

        // A building-block list row, "existing loadout" variant. Same cells as in the picker window, and
        // explicitly the same drawers from LoadoutUtility - a second calculation for the same question is the
        // class of bug that hits this mod most often.
        private void DrawRoleSourceLoadoutRow(Rect row, Loadout lo, float nameW, float scoreW, float specW, float availW, float contW)
        {
            if (Mouse.IsOver(row)) Widgets.DrawHighlight(row);
            bool locked = LoadoutUtility.IsLoadoutDisabled(lo);
            Dialog_AssignLoadouts.AnalyzeLoadout(lo, out ThingDef main, out float score, out _, out _);

            // Lock first - the drag area starts after it, otherwise it swallows the click.
            Loadout lockLo = lo;
            bool lockedNow = locked;
            DrawSourceLockToggle(new Rect(row.x + 4f, row.y + (row.height - 18f) / 2f, 18f, 18f), locked,
                () => LoadoutUtility.SetLoadoutDisabled(lockLo, !lockedNow), Mouse.IsOver(row), isLoadout: true);

            if (locked) GUI.color = new Color(1f, 1f, 1f, 0.4f);
            if (main != null) Widgets.ThingIcon(new Rect(row.x + 26f, row.y + (row.height - 22f) / 2f, 22f, 22f), main);
            Rect nameRect = new Rect(row.x + 52f, row.y, nameW, row.height);
            Text.Anchor = TextAnchor.MiddleLeft;
            Widgets.Label(nameRect, RoleUtility.DisplayLabel(lo).Truncate(nameW - 4f));
            Text.Anchor = TextAnchor.UpperLeft;
            GUI.color = Color.white;

            float cx = row.x + 52f + nameW;
            if (scoreW > 0f)
            {
                Rect r = new Rect(cx, row.y + (row.height - 16f) / 2f, scoreW - 8f, 16f);
                LoadoutUtility.ScoreBar(r, score, 100f, null, true, null);
                cx += scoreW;
            }
            if (specW > 0f)
            {
                DrawSourceDamageCell(new Rect(cx, row.y, specW - 8f, row.height), main, lo.Slots);
                cx += specW;
            }
            if (availW > 0f)
            {
                if (main != null)
                {
                    Rect r = new Rect(cx + 2f, row.y + (row.height - 20f) / 2f, availW - 10f, 20f);
                    // TipWanted first: BuildWeaponLocationTooltip iterates maps and colonists, and this bar is in
                    // EVERY row - same precaution as in the picker window.
                    LoadoutUtility.DrawAvailabilityBar(r, LoadoutUtility.WeaponAvailability(main), horizontal: true,
                        LoadoutUtility.TipWanted(r) ? LoadoutUtility.BuildWeaponLocationTooltip(main) : null);
                }
                cx += availW;
            }
            if (contW > 0f)
            {
                DrawSourceContentsStrip(new Rect(cx, row.y, contW - 6f, row.height), lo.Slots);
                cx += contW;
            }

            // The drag area ends before the cells on the right: they have their own tooltips (score, locations,
            // contents), and a drag start over them would hide those.
            Rect dragArea = new Rect(row.x + 22f, row.y, 30f + nameW, row.height);
            Loadout tipLo = lo;
            TooltipHandler.TipRegion(dragArea, () => LoadoutUtility.LoadoutContentsTooltip(tipLo), tipLo.UniqueID ^ 0x5C0F);
            if (locked) return;   // locked = no building block, just as it can't be picked in the picker window
            if (Mouse.IsOver(dragArea) && Event.current.type == EventType.MouseDown && Event.current.button == 0)
            {
                dragLoadout = lo;
                Event.current.Use();
            }
        }

        // Same row, "weapon without a template" variant. The numbers come from the default slot list
        // (DefaultSlotsFor), i.e. exactly what dragging would create - not from the bare weapon.
        private void DrawRoleSourceWeaponRow(Rect row, ThingDef w, float nameW, float scoreW, float specW, float availW, float contW)
        {
            if (Mouse.IsOver(row)) Widgets.DrawHighlight(row);
            bool locked = LoadoutUtility.IsWeaponDisabled(w);

            ThingDef lockW = w;
            bool lockedNow = locked;
            DrawSourceLockToggle(new Rect(row.x + 4f, row.y + (row.height - 18f) / 2f, 18f, 18f), locked,
                () => LoadoutUtility.SetWeaponDisabled(lockW, !lockedNow), Mouse.IsOver(row), isLoadout: false);

            if (locked) GUI.color = new Color(1f, 1f, 1f, 0.4f);
            Widgets.ThingIcon(new Rect(row.x + 26f, row.y + (row.height - 22f) / 2f, 22f, 22f), w);
            Rect nameRect = new Rect(row.x + 52f, row.y, nameW, row.height);
            Text.Anchor = TextAnchor.MiddleLeft;
            Widgets.Label(nameRect, w.LabelCap.ToString().Truncate(nameW - 4f));
            Text.Anchor = TextAnchor.UpperLeft;
            GUI.color = Color.white;

            float cx = row.x + 52f + nameW;
            if (scoreW > 0f)
            {
                LoadoutUtility.GetWeaponScore(w, out float sc, out string scTip);
                Rect r = new Rect(cx, row.y + (row.height - 16f) / 2f, scoreW - 8f, 16f);
                LoadoutUtility.ScoreBar(r, sc, 100f, null, true, LoadoutUtility.TipWanted(r) ? scTip : null);
                cx += scoreW;
            }
            List<LoadoutSlot> slots = RoleWeaponSlots(w);
            if (specW > 0f)
            {
                DrawSourceDamageCell(new Rect(cx, row.y, specW - 8f, row.height), w, slots);
                cx += specW;
            }
            if (availW > 0f)
            {
                Rect r = new Rect(cx + 2f, row.y + (row.height - 20f) / 2f, availW - 10f, 20f);
                LoadoutUtility.DrawAvailabilityBar(r, LoadoutUtility.WeaponAvailability(w), horizontal: true,
                    LoadoutUtility.TipWanted(r) ? LoadoutUtility.BuildWeaponLocationTooltip(w) : null);
                cx += availW;
            }
            if (contW > 0f)
            {
                DrawSourceContentsStrip(new Rect(cx, row.y, contW - 6f, row.height), slots);
                cx += contW;
            }

            Rect dragArea = new Rect(row.x + 22f, row.y, 30f + nameW, row.height);
            ThingDef tipW = w;
            TooltipHandler.TipRegion(dragArea, () => tipW.LabelCap.ToString() + "\n\n" + (tipW.description ?? ""), tipW.shortHash ^ 0x5C11);
            if (locked) return;
            if (Mouse.IsOver(dragArea) && Event.current.type == EventType.MouseDown && Event.current.button == 0)
            {
                // Only remember the WEAPON - the template is created on drop onto a role card
                // (materializing it on pickup made the row jump into the loadout section above
                // immediately).
                dragWeaponDef = tipW;
                Event.current.Use();
            }
        }

        // Same toggle as in the picker window (Dialog_PickPacks.DrawWeaponRow), so a lock looks and reacts the
        // same everywhere: faintly visible until the mouse is over the row or it is locked.
        private static void DrawSourceLockToggle(Rect r, bool locked, Action toggle, bool rowHovered, bool isLoadout)
        {
            GUI.color = new Color(1f, 1f, 1f, locked || rowHovered ? 1f : 0.3f);
            if (Widgets.ButtonImage(r, locked ? Dialog_PickPacks.TexDoNotUseOn : Dialog_PickPacks.TexDoNotUseOff)) toggle();
            GUI.color = Color.white;
            // Template and weapon each get their own wording - the same as in the picker window, so the same lock
            // doesn't seem to promise two different things in two places.
            string key = isLoadout
                ? (locked ? "CER_PickPacks_UnlockLoadoutTip" : "CER_PickPacks_LockLoadoutTip")
                : (locked ? "CER_PickPacks_UnlockTip" : "CER_PickPacks_LockTip");
            TooltipHandler.TipRegion(r, key.Translate());
        }

        // Up to four icons plus "+N" - the short version of the contents; the long version is in the name's
        // tooltip. Deliberately fewer icons than in the picker window (six there): the column is narrower here,
        // and four fit without clipping.
        private static void DrawSourceContentsStrip(Rect r, List<LoadoutSlot> slots)
        {
            if (slots == null || slots.Count == 0) return;
            const int maxIcons = 4;
            const float iconSize = 20f, gap = 3f;
            float x = r.x;
            float iy = r.y + (r.height - iconSize) / 2f;
            int shown = Mathf.Min(maxIcons, slots.Count);
            for (int i = 0; i < shown; i++)
            {
                LoadoutSlot s = slots[i];
                Rect ir = new Rect(x, iy, iconSize, iconSize);
                if (s.thingDef != null) Widgets.ThingIcon(ir, s.thingDef);
                else if (s.genericDef != null)
                {
                    Texture2D badge = GenericIcons.For(s.genericDef);
                    if (badge != null) GUI.DrawTexture(ir, badge);
                }
                x += iconSize + gap;
            }
            if (slots.Count > shown)
            {
                GUI.color = new Color(1f, 1f, 1f, 0.6f);
                Text.Anchor = TextAnchor.MiddleLeft;
                Widgets.Label(new Rect(x, r.y, Mathf.Max(0f, r.xMax - x), r.height), "CER_PickPacks_MoreCount".Translate((slots.Count - shown).ToString()));
                Text.Anchor = TextAnchor.UpperLeft;
                GUI.color = Color.white;
            }
        }

        // Default slots of a weapon, built once and cached. Exactly the same pattern as
        // Dialog_PickPacks.WeaponSlots; a default list per row per frame would be unaffordable with a
        // three-digit weapon count.
        private List<LoadoutSlot> RoleWeaponSlots(ThingDef w)
        {
            if (!roleWeaponSlotsCache.TryGetValue(w, out List<LoadoutSlot> slots))
            {
                slots = DefaultSlotsFor(w, BuildDefaultSettings(w));
                roleWeaponSlotsCache[w] = slots;
            }
            return slots;
        }

        // Browse/drag surface for Equipment Packs: drag one onto a Role card (same mechanism as any
        // other loadout - a pack is just a Loadout with no main weapon). Packs are authored in their own
        // Equipment Packs tab now, not here - this list is assignment only, not creation.
        private void DrawEquipmentPackSourceList(Rect panel)
        {
            Widgets.DrawMenuSection(panel);
            Rect header = panel.ContractedBy(6f);
            // Width measured instead of a fixed 140f - same issue as in the weapons box above.
            string packHeaderTxt = "CER_GenerateLoadouts_YourEquipmentPacksHeader".Translate();
            float packHeaderW = Mathf.Min(Text.CalcSize(packHeaderTxt).x + 4f, header.width * 0.6f);
            Widgets.Label(new Rect(header.x, header.y, packHeaderW, 22f), packHeaderTxt.Truncate(packHeaderW));
            // Search field as in the list above: "Your equipment packs" gets a type-ahead search just like
            // "Your weapons". Same style, same clear cross - and as a side effect both LISTS are now the same
            // height: the weapons list had lost 26 points to its header, the pack list hadn't, so its list was
            // exactly that one row taller even though the boxes themselves are the same size.
            Rect packFieldRect = new Rect(header.x + packHeaderW + 4f, header.y - 2f, header.width - packHeaderW - 4f - 24f, 22f);
            equipmentPackSearch = Widgets.TextField(packFieldRect, equipmentPackSearch);
            if (LoadoutUtility.ClearSearchButton(packFieldRect, header.y - 2f, equipmentPackSearch)) equipmentPackSearch = "";

            List<Loadout> packs = EquipmentPackUtility.AllEquipmentPacks();
            if (!string.IsNullOrEmpty(equipmentPackSearch))
                packs = packs.Where(l => EquipmentPackUtility.StripMarker(l.label)
                    .IndexOf(equipmentPackSearch, StringComparison.OrdinalIgnoreCase) >= 0).ToList();
            Rect outRect = new Rect(header.x, header.y + 26f, header.width, header.height - 26f);
            Rect viewRect = new Rect(0, 0, outRect.width - 16f, packs.Count * 30f + 4f);
            Widgets.BeginScrollView(outRect, ref equipmentPackScroll, viewRect);
            float ry = 0f;
            foreach (Loadout pack in packs)
            {
                Rect row = new Rect(0, ry, viewRect.width, 28f);
                if (Mouse.IsOver(row)) Widgets.DrawHighlight(row);
                Rect delRect = new Rect(row.xMax - 22f, row.y + 3f, 20f, 20f);
                Widgets.Label(new Rect(row.x + 6f, row.y + 3f, row.width - 32f, 22f), EquipmentPackUtility.StripMarker(pack.label).Truncate(row.width - 38f));
                // Confirmation added (2026-08-01) - unlike Role deletion and the bulk "delete existing packs"
                // button, a single pack could previously be deleted with one click, silently dropping it from
                // every colonist currently carrying it.
                Loadout packCapture = pack;
                if (Widgets.ButtonText(delRect, "CER_GenerateLoadouts_RemoveButton".Translate()))
                {
                    Find.WindowStack.Add(LoadoutUtility.CompactConfirmation(
                        "CER_GenerateLoadouts_DeletePackConfirm".Translate(EquipmentPackUtility.StripMarker(packCapture.label)),
                        () => LoadoutDeletionUtility.Delete(packCapture), true));
                }
                TooltipHandler.TipRegion(delRect, "CER_GenerateLoadouts_DeletePackTip".Translate());
                Rect dragArea = new Rect(row.x, row.y, row.width - 26f, row.height);
                // Contents in the tooltip, same reasoning as in the loadout list above. Deliberately on the
                // DRAG AREA and not the whole row: the delete button on the right has its own tooltip, and that
                // must not be covered by the contents.
                Loadout tipPack = pack;
                TooltipHandler.TipRegion(dragArea, () => LoadoutUtility.LoadoutContentsTooltip(tipPack), tipPack.UniqueID ^ 0x5C10);
                if (Mouse.IsOver(dragArea) && Event.current.type == EventType.MouseDown && Event.current.button == 0)
                {
                    dragLoadout = pack;
                    Event.current.Use();
                }
                TooltipHandler.TipRegion(dragArea, "CER_GenerateLoadouts_DragPackTip".Translate());
                ry += 30f;
            }
            if (packs.Count == 0)
            {
                GUI.color = new Color(1f, 1f, 1f, 0.5f);
                Widgets.Label(new Rect(4f, 4f, viewRect.width - 8f, 40f), string.IsNullOrEmpty(equipmentPackSearch)
                    ? "CER_GenerateLoadouts_NoPacksYet".Translate()
                    : "CER_GenerateLoadouts_NoPacksMatchSearch".Translate(equipmentPackSearch));
                GUI.color = Color.white;
            }
            Widgets.EndScrollView();
        }

        private void DrawRoleCards(Rect panel, CEReloadedSettings st)
        {
            Widgets.DrawMenuSection(panel);
            Rect header = panel.ContractedBy(6f);
            Widgets.Label(new Rect(header.x, header.y, 46f, 22f), "CER_GenerateLoadouts_RolesHeader".Translate());
            List<LoadoutRole> local = RoleUtility.LocalRoles();
            Rect addSaveRect = new Rect(header.xMax - 108f, header.y - 2f, 108f, 24f);
            if (local != null)
            {
                // The new card immediately becomes the SELECTED one. Previously the previously selected role
                // stayed active, and the name field below belongs to the selected one - so the typed name landed
                // on a DIFFERENT role: you typed the new role's name and another card was suddenly renamed. A
                // silent rename of the wrong row is the worst possible outcome of this button.
                if (LoadoutUtility.ButtonTextFit(addSaveRect, "CER_GenerateLoadouts_NewRoleButton".Translate().ToString()))
                {
                    LoadoutRole added = new LoadoutRole { name = "CER_GenerateLoadouts_NewRoleDefaultName".Translate() };
                    local.Insert(0, added);
                    SetActiveRole(added);
                }
                TooltipHandler.TipRegion(addSaveRect, "CER_GenerateLoadouts_NewRoleTip".Translate());
            }
            Rect resetRect = new Rect(header.xMax - 108f - 8f - 84f, header.y - 2f, 84f, 24f);
            if (LoadoutUtility.ButtonTextFit(resetRect, "CER_GenerateLoadouts_ResetButton".Translate().ToString()))
                Find.WindowStack.Add(LoadoutUtility.CompactConfirmation("CER_GenerateLoadouts_ResetRolesConfirm".Translate(),
                    () =>
                    {
                        // The whole procedure lives in RoleUtility.ResetGlobalRoles - including re-linking the
                        // loadouts with matching names AND deleting the loadouts that no longer belong to any
                        // role afterwards. A second button of the same kind sits in the mod settings and now
                        // calls the same line.
                        RoleUtility.ResetGlobalRoles(st);
                        // Forget only the rows of the DISCARDED roles - save-local roles survive the reset, and
                        // with them their just-assembled row.
                        var alive = new HashSet<LoadoutRole>(RoleUtility.AllKnownRoles());
                        foreach (LoadoutRole gone in roleComp.Keys.Where(r => !alive.Contains(r)).ToList())
                            roleComp.Remove(gone);
                        if (activeRoleObj != null && !alive.Contains(activeRoleObj)) { activeRoleObj = null; lastNamedRole = null; }
                        PersistRoles();
                    }, true));
            TooltipHandler.TipRegion(resetRect, "CER_GenerateLoadouts_ResetRolesTip".Translate());

            // The way back for deleted preset roles - only shown when one is actually missing, and it carries
            // the count. Without it "Reset" was the only way back, and that throws away every self-built role
            // too. Rationale in RoleUtility.RestoreMissingPresetRoles.
            int missingPresets = RoleUtility.MissingPresetRoles(st).Count;
            float restoreW = 0f;
            if (missingPresets > 0)
            {
                string restoreLbl = "CER_GenerateLoadouts_RestorePresetsButton".Translate(missingPresets.ToString()).ToString();
                restoreW = LoadoutUtility.BottomBtnW(restoreLbl, 110f, 200f);
                Rect restoreRect = new Rect(resetRect.x - 8f - restoreW, header.y - 2f, restoreW, 24f);
                if (LoadoutUtility.ButtonTextFit(restoreRect, restoreLbl))
                {
                    int n = RoleUtility.RestoreMissingPresetRoles(st);
                    if (n > 0)
                    {
                        PersistRoles();
                        Messages.Message("CER_GenerateLoadouts_RestoredPresetsMessage".Translate(n.ToString()),
                            MessageTypeDefOf.TaskCompletion, false);
                    }
                }
                TooltipHandler.TipRegion(restoreRect, "CER_GenerateLoadouts_RestorePresetsTip".Translate());
                restoreW += 8f;
            }

            float searchW = header.width - 50f - 208f - 24f - restoreW;
            Rect roleCardFieldRect = new Rect(header.x + 50f, header.y - 2f, searchW, 22f);
            roleCardSearch = Widgets.TextField(roleCardFieldRect, roleCardSearch);
            if (LoadoutUtility.ClearSearchButton(roleCardFieldRect, header.y - 2f, roleCardSearch)) roleCardSearch = "";

            // Three tiers (2026-07-30, refined): a draft you're actively working on - has
            // at least one composition source attached (e.g. just came from "Save as Role"/"create role from
            // loadout"), but not yet formally created - sits at the very TOP, where you're already looking.
            // Already-created (saved) roles come next. A genuinely untouched, empty "+ New role" placeholder
            // (no composition source at all yet) sinks to the very BOTTOM instead of competing for the same
            // top spot - it used to share the same "not yet created" tier as an in-progress draft, which
            // buried a real, actively-edited draft under a pile of never-touched template cards.
            int RoleSortTier(LoadoutRole r) => RoleCreated(r) ? 1 : (CompFor(r).Count > 0 ? 0 : 2);
            List<LoadoutRole> all = AllRoles(st)
                .Where(r => string.IsNullOrEmpty(roleCardSearch) || (r.name != null && r.name.IndexOf(roleCardSearch, StringComparison.OrdinalIgnoreCase) >= 0))
                .OrderBy(RoleSortTier)
                .ToList();

            // Who has which role right now: show how many pawns the role is currently assigned to - computed
            // ONCE per draw pass for all cards, not per card over all colonists. DisplayLoadout looks through
            // the personal anchor to the actual template, like every other count in the mod.
            Dictionary<Loadout, List<string>> pawnsByLoadout = new Dictionary<Loadout, List<string>>();
            foreach (Pawn p in LoadoutUtility.AllColonyPawns())
            {
                Loadout lo = AmmoAssignmentUtility.DisplayLoadout(p.GetLoadout());
                if (lo == null || lo.defaultLoadout) continue;
                if (!pawnsByLoadout.TryGetValue(lo, out List<string> names)) pawnsByLoadout[lo] = names = new List<string>();
                names.Add(p.LabelShortCap);
            }
            List<string> OnRole(LoadoutRole r)
            {
                Loadout rl = RoleUtility.RoleLoadoutOf(r);
                return rl != null && pawnsByLoadout.TryGetValue(rl, out List<string> n) ? n : new List<string>();
            }
            Rect outRect = new Rect(header.x, header.y + 26f, header.width, header.height - 26f);
            // The card grows with the number of attachments: the chips used to draw in ONE row without wrapping
            // at a fixed card height. From about four attachments on, the last ones were completely outside the
            // box - along with their [x] buttons, so the packs could no longer be removed and the whole role had
            // to be rebuilt.
            // Structural fix instead of a bigger constant: the chain card height -> chip row is the cause, not
            // the specific number 80.
            const float chipRowH = 24f;
            const float cardBaseH = 80f;
            // Room for the main weapon's icon in the marked chip - see MainOf further down.
            const float mainIconW = 22f;
            float cardW = outRect.width - 16f - 4f;
            float chipAreaW = cardW - 12f;

            // The card's displayed name - the TYPED one while renaming, otherwise the stored one. Up here
            // because besides the label the main-weapon pick needs it too, and the two must never see
            // different names.
            string CardNameOf(LoadoutRole r)
            {
                if (r == activeRoleObj && !string.IsNullOrWhiteSpace(roleNameEdit)) return roleNameEdit.Trim();
                return r?.name;
            }
            // Once per draw pass instead of once per query: the height calculation and the draw loop ask the
            // same thing twice, and the pick reads the slots of every composition row.
            Dictionary<LoadoutRole, ThingDef> mainWeaponOfRole = new Dictionary<LoadoutRole, ThingDef>();
            ThingDef MainOf(LoadoutRole r)
            {
                if (r == null) return null;
                if (!mainWeaponOfRole.TryGetValue(r, out ThingDef m))
                {
                    m = RoleUtility.MainWeaponOf(CompFor(r), CardNameOf(r));
                    mainWeaponOfRole[r] = m;
                }
                return m;
            }
            // Chip width and label in ONE place: the height calculation and the drawing used to duplicate the
            // formula, with a comment next to it saying they must not drift apart. With the main weapon icon
            // that would have been three numbers that must match (fix the chain, don't turn the next constant).
            string ChipText(Loadout l) => EquipmentPackUtility.StripMarker(l.label).Truncate(120f);
            float ChipWidth(Loadout l, bool isMain)
                => Mathf.Min(Text.CalcSize(ChipText(l)).x + 30f + (isMain ? mainIconW : 0f), chipAreaW);
            int ChipRows(List<Loadout> comp, ThingDef mainDef)
            {
                if (comp == null || comp.Count == 0) return 1;
                int rows = 1;
                float x = 0f;
                foreach (Loadout l in comp)
                {
                    if (l == null) continue;
                    float w = ChipWidth(l, RoleUtility.CompHasDef(l, mainDef));
                    if (x > 0f && x + w > chipAreaW) { rows++; x = 0f; }
                    x += w + 4f;
                }
                return rows;
            }
            float CardHeight(LoadoutRole r) => cardBaseH + (ChipRows(CompFor(r), MainOf(r)) - 1) * chipRowH;

            float totalH = 4f;
            foreach (LoadoutRole r in all) totalH += CardHeight(r) + 6f;
            Rect viewRect = new Rect(0, 0, outRect.width - 16f, totalH);
            Widgets.BeginScrollView(outRect, ref roleListScroll, viewRect);
            float ry = 0f;
            LoadoutRole copySource = null;
            for (int i = 0; i < all.Count; i++)
            {
                LoadoutRole role = all[i];
                bool isLocal = local != null && local.Contains(role);
                bool created = RoleCreated(role);
                float cardH = CardHeight(role);
                Rect card = new Rect(2f, ry, viewRect.width - 4f, cardH);

                bool hoverDrop = (dragLoadout != null || dragWeaponDef != null) && Mouse.IsOver(card);
                if (hoverDrop) Widgets.DrawBox(card, 2);
                if (role == activeRoleObj) Widgets.DrawHighlightSelected(card); else Widgets.DrawLightHighlight(card);
                if (!created) Widgets.DrawBoxSolid(card, new Color(0f, 0f, 0f, 0.30f));   // inactive roles are visibly dimmer

                // Name + scope tag.
                GUI.color = isLocal ? new Color(0.6f, 0.8f, 1f, 0.8f) : new Color(1f, 1f, 1f, 0.55f);
                Rect tagRect = new Rect(card.x + 6f, card.y + 4f, 26f, 20f);
                Widgets.Label(tagRect, isLocal ? "CER_GenerateLoadouts_LocalTag".Translate() : "CER_GenerateLoadouts_GlobalTag".Translate());
                GUI.color = Color.white;
                TooltipHandler.TipRegion(tagRect, isLocal
                    ? "CER_GenerateLoadouts_LocalTagTip".Translate()
                    : "CER_GenerateLoadouts_GlobalTagTip".Translate());
                // Right-anchored fixed-width controls FIRST (delete, then copy just left of it), so the badge
                // and name label can be sized to end exactly where the next one starts - fixes the copy icon
                // visually overlapping the badge's own right edge (both were independently hardcoded against
                // card.xMax and drifted into each other).
                Rect delCard = new Rect(card.xMax - 24f, card.y + 4f, 20f, 20f);
                // Confirmation added (2026-08-01) - every colonist currently assigned to this Role keeps
                // pointing at its real shared Loadout even after this bookkeeping entry is removed (deleting
                // the underlying Loadout object outright would risk a live dangling reference, so this
                // deliberately only removes the Roles-tab entry, same as before) - but the click itself had no
                // confirm at all, unlike the "Reset" button right above it doing something far less permanent.
                // The actual removal runs directly inside the confirmation's own callback (a LATER frame, once
                // the player clicks Confirm in the popup) rather than through the delRole/post-loop-check
                // pattern every OTHER row here uses without confirmation - that pattern only works within the
                // single frame the click happens on, since delRole is a fresh local every call.
                LoadoutRole roleToDelete = role;
                List<LoadoutRole> localList = local;
                CEReloadedSettings stCapture = st;
                // A card that was NEVER created and has nothing on it disappears without confirmation. The
                // confirmation protects nothing here: there is no loadout for it, so no colonist could depend
                // on it - it only costs a second click for an empty placeholder card created by accident. It
                // also sidesteps an unexplained case where the x on a freshly created, empty 'New role' card
                // did not remove it: the removal path below is correct, so rather than invent a cause, the
                // dialog is skipped - no window, no problem.
                // For a created role the confirmation stays unchanged.
                // ...AND every GLOBAL preset role: the x on the empty card '[G] Marksman' used to remove it from
                // the global library WITHOUT confirmation. The exception above is for an accidentally created,
                // empty LOCAL card - an empty preset is not an accident, and losing it affects every colony. That
                // is exactly what the confirmation text with the hint about 'restore presets' is for - and it
                // never ran here.
                bool needsConfirm = created || CompFor(role).Count > 0 || (!isLocal && RoleUtility.IsPresetRoleName(role.name));
                Action removeRole = () =>
                {
                    // The "[Role] <Name>" loadout GOES WITH IT (two problems with the same root: an already
                    // deleted role was still shown in Assign Loadouts, and ghost roles after delete + reset).
                    //
                    // Until now this button only removed the bookkeeping entry - on the grounds that deleting the
                    // loadout could leave a live reference dangling. That was true before LoadoutDeletionUtility
                    // existed; since then exactly that is handled there (assigned colonists fall back cleanly to
                    // "no template", anchors with their own contents are re-parented instead of deleted, all side
                    // ledgers forget the ID).
                    //
                    // What the old version left behind instead was worse than a dangling reference: a loadout
                    // WITHOUT an owner. Without a role behind it, every identity check sees an ordinary template -
                    // it kept being handed out in the assignment itself under "No roles", under its raw label
                    // "[Role] Assault", because the marker is only stripped for a RECOGNIZED role. And on the next
                    // "Reset" the fresh preset role with the same name adopted it again
                    // (RoleUtility.AdoptRoleLoadoutsByName) - then the deleted role showed up as "created" with
                    // weapon contents, but with an empty building-block row.
                    Loadout doomed = RoleUtility.RoleLoadoutOf(roleToDelete);
                    if (!stCapture.roles.Remove(roleToDelete)) localList?.Remove(roleToDelete);
                    roleComp.Remove(roleToDelete);
                    if (activeRoleObj == roleToDelete) { activeRoleObj = null; lastNamedRole = null; }
                    if (doomed != null) LoadoutDeletionUtility.Delete(doomed);
                };
                if (Widgets.ButtonText(delCard, "CER_GenerateLoadouts_RemoveButton".Translate()))
                {
                    if (needsConfirm)
                        // For one of the seven built-in presets the confirmation ALSO says that it disappears
                        // from the global library and where to get it back: the x was easily read as "undo", and
                        // the old text did not contradict that - it only spoke of "the role and the loadout
                        // behind it".
                        Find.WindowStack.Add(LoadoutUtility.CompactConfirmation(
                            RoleUtility.IsPresetRoleName(roleToDelete.name)
                                ? "CER_GenerateLoadouts_DeleteRoleConfirmPreset".Translate(roleToDelete.name)
                                : "CER_GenerateLoadouts_DeleteRoleConfirm".Translate(roleToDelete.name), removeRole, true));
                    else
                        removeRole();
                }
                TooltipHandler.TipRegion(delCard, "CER_GenerateLoadouts_DeleteRoleTip".Translate());

                // Unobtrusive icon, not a labeled button - a copy is a much rarer action than delete and
                // shouldn't compete with it for attention. Duplicates name, factors AND the current dragged
                // composition, so the copy previews identically to the original until the player changes it.
                Rect copyCard = new Rect(delCard.x - 22f, card.y + 4f, 20f, 20f);
                if (Widgets.ButtonImage(copyCard, TexButton.Copy)) copySource = role;
                TooltipHandler.TipRegion(copyCard, "CER_GenerateLoadouts_DuplicateRoleTip".Translate());

                // Status badge: does a "[Role] <name>" loadout already exist? (computed above as 'created')
                GUI.color = created ? LoadoutUtility.ColGood : new Color(1f, 1f, 1f, 0.4f);
                Text.Anchor = TextAnchor.MiddleRight;
                Rect badgeRect = new Rect(copyCard.x - 6f - 68f, card.y + 3f, 68f, 22f);
                Widgets.Label(badgeRect, created ? "CER_GenerateLoadouts_BadgeCreated".Translate() : "CER_GenerateLoadouts_BadgeNew".Translate());
                Text.Anchor = TextAnchor.UpperLeft;
                GUI.color = Color.white;
                TooltipHandler.TipRegion(badgeRect, created ? "CER_GenerateLoadouts_BadgeCreatedTip".Translate() : "CER_GenerateLoadouts_BadgeNewTip".Translate());

                GUI.color = created ? Color.white : new Color(1f, 1f, 1f, 0.5f);
                // The TYPED name while this role is the active one (the name was otherwise entered twice
                // because the card kept showing "New role" - and with several new roles you'd get a stack of
                // identical rows).
                // Display ONLY, `role.name` stays untouched: CommitRole looks up the already-created loadout by
                // the OLD name ("[Role] <role.name>") so it keeps it on rename instead of adding a second one
                // next to it. If the buffer were written live into role.name here, exactly that lookup would come
                // up empty - the rename would turn into a duplicate, and every colonist on the old role would
                // stay stuck on the old one.
                // Only for non-empty input, so a cleared field doesn't show a nameless card.
                string cardName = CardNameOf(role);
                Widgets.Label(new Rect(card.x + 32f, card.y + 3f, badgeRect.x - 6f - (card.x + 32f), 22f), "<b>" + cardName + "</b>");
                GUI.color = Color.white;

                // Editable factors (edits persist to wherever the role lives - global settings or this save).
                float fx = card.x + 6f;
                bool factorsEditable = CompFor(role).Count > 0;   // only once a loadout is dropped on the role
                void FField(string lbl, int slot, System.Func<float> g, System.Action<float> s, string tip)
                {
                    GUI.color = new Color(1f, 1f, 1f, 0.7f);
                    Rect lr = new Rect(fx, card.y + 27f, 26f, 20f);
                    Widgets.Label(lr, lbl);
                    GUI.color = Color.white;
                    // The explanation also goes on the LABEL: nowhere did it say what the four stand for, and the
                    // labels themselves had no tooltip. Two letters without explanation are a riddle, and the field
                    // next to it, when locked, only explained the lock, not the factor.
                    TooltipHandler.TipRegion(lr, tip);
                    Rect fr = new Rect(fx + 26f, card.y + 25f, 38f, 22f);
                    if (factorsEditable)
                    {
                        float beforeFactor = g();
                        CardFactorField(fr, role, slot, g, s);
                        // A changed factor changes the role's contents just like a chip - so it is written
                        // immediately the same way. Only on an actual change, otherwise it would run every
                        // frame.
                        if (g() != beforeFactor) AutoCommitRole(role, st);
                        TooltipHandler.TipRegion(fr, tip);
                    }
                    else
                    {
                        Widgets.DrawBoxSolid(fr, new Color(0.16f, 0.16f, 0.18f));
                        GUI.color = new Color(1f, 1f, 1f, 0.45f);
                        Text.Anchor = TextAnchor.MiddleCenter;
                        Widgets.Label(fr, g().ToString("0.##"));
                        Text.Anchor = TextAnchor.UpperLeft;
                        GUI.color = Color.white;
                        // First what the factor means, then why it is locked - not just the lock.
                        TooltipHandler.TipRegion(fr, tip + "\n\n" + "CER_GenerateLoadouts_FactorLockedTip".Translate());
                    }
                    fx += 70f;
                }
                FField("CER_GenerateLoadouts_FactorAmLabel".Translate(), 0, () => role.ammoFactor, v => role.ammoFactor = v, "CER_GenerateLoadouts_AmmoFactorTip".Translate());
                FField("CER_GenerateLoadouts_FactorMeLabel".Translate(), 1, () => role.medicineFactor, v => role.medicineFactor = v, "CER_GenerateLoadouts_MedicineFactorTip".Translate());
                FField("CER_GenerateLoadouts_FactorFoLabel".Translate(), 2, () => role.foodFactor, v => role.foodFactor = v, "CER_GenerateLoadouts_FoodFactorTip".Translate());
                FField("CER_GenerateLoadouts_FactorGrLabel".Translate(), 3, () => role.grenadeFactor, v => role.grenadeFactor = v, "CER_GenerateLoadouts_GrenadeFactorTip".Translate());

                // The number of colonists on this role, on the right of the factor row (see pawnsByLoadout
                // above): nothing was there before, and the header is full with name, state and two buttons.
                // Only for created roles - a role without a loadout can't be held by anyone. The names are in
                // the mouseover.
                if (created)
                {
                    List<string> onRole = OnRole(role);
                    Rect cntRect = new Rect(card.xMax - 30f - 120f, card.y + 27f, 120f, 20f);
                    GUI.color = onRole.Count > 0 ? new Color(1f, 1f, 1f, 0.75f) : new Color(1f, 1f, 1f, 0.4f);
                    Text.Anchor = TextAnchor.MiddleRight;
                    Widgets.Label(cntRect, "CER_GenerateLoadouts_RoleAssignedCountFmt".Translate(onRole.Count.ToString()));
                    Text.Anchor = TextAnchor.UpperLeft;
                    GUI.color = Color.white;
                    TooltipHandler.TipRegion(cntRect, onRole.Count > 0
                        ? "CER_GenerateLoadouts_RoleAssignedCountTip".Translate(string.Join(", ", onRole.ToArray()))
                        : "CER_GenerateLoadouts_RoleAssignedNoneTip".Translate());
                }

                // Composition chips (dropped loadouts) with X to remove.
                List<Loadout> comp = CompFor(role);
                float chipX = card.x + 6f;
                float chipY = card.y + 52f;
                if (comp.Count == 0)
                {
                    // A CREATED role without a stored composition says what it ACTUALLY contains (the row of a
                    // created role used to show only "drag a loadout here" and greyed-out factor fields - that it
                    // contained a rifle could only be seen in the preview below or in the assignment's weapon
                    // menu).
                    // The state itself is correct and can't be programmed away: a role from an earlier game
                    // session has no stored row of building blocks, and it can't be reconstructed either - see
                    // the switch at RoleAutoSaves. What was wrong was only that the row turned this into "nothing
                    // here". It now reads the contents from the stored role loadout itself, via the same function
                    // the Assign tab uses to determine a template's main weapon - not via a second one of its own.
                    Loadout savedRole = created ? RoleUtility.RoleLoadoutOf(role) : null;
                    ThingDef savedMain = null;
                    if (savedRole != null) Dialog_AssignLoadouts.AnalyzeLoadout(savedRole, out savedMain, out _, out _, out _);
                    GUI.color = new Color(1f, 1f, 1f, 0.4f);
                    Rect hintRect = new Rect(chipX, chipY, card.width - 12f, 20f);
                    Widgets.Label(hintRect, savedMain != null
                        ? "CER_GenerateLoadouts_CreatedRoleContentHintFmt".Translate(savedMain.LabelCap)
                        : "CER_GenerateLoadouts_DragLoadoutHerePlaceholder".Translate());
                    GUI.color = Color.white;
                    // This row had no tooltip at all either.
                    TooltipHandler.TipRegion(hintRect, savedMain != null
                        ? "CER_GenerateLoadouts_CreatedRoleContentTip".Translate()
                        : "CER_GenerateLoadouts_DragLoadoutHereTip".Translate());
                }
                else
                {
                    int removeIdx = -1;
                    ThingDef roleMainW = MainOf(role);
                    // Belongs to the ROLE, not the individual building block - hence once before the loop.
                    // ONLY for CREATED roles (the role had not even been created yet, let alone assigned to a
                    // pawn - and still the x was locked, with text about colonists ending up empty-handed). The
                    // lock protects the STORED role loadout from being emptied; a template that was never written
                    // has nothing to protect - its last building block goes away like any other, and the card then
                    // says 'drag a loadout here'.
                    bool blockedRemove = comp.Count <= 1 && created;
                    for (int c = 0; c < comp.Count; c++)
                    {
                        // Strip the "[Pack:Category] " marker for display - an Equipment Pack dragged in
                        // reads as "Standard AP", not the raw internal label.
                        string t = ChipText(comp[c]);
                        bool isMain = RoleUtility.CompHasDef(comp[c], roleMainW);
                        float w = ChipWidth(comp[c], isMain);
                        // Wrap with the same calculation as ChipRows above - if the two drift apart, the reserved
                        // card height doesn't match what is actually drawn.
                        if (chipX > card.x + 6f && chipX + w > card.x + 6f + chipAreaW)
                        {
                            chipX = card.x + 6f;
                            chipY += chipRowH;
                        }
                        Rect chip = new Rect(chipX, chipY, w, 20f);
                        // The chip that supplies the MAIN WEAPON stands out and carries its icon: a role may
                        // bundle several weapons, but it didn't say which one becomes the main weapon - the rest
                        // now read as sidearms. Which one it is is decided by RoleUtility.MainWeaponOf, i.e. the
                        // same pick that later runs on the finished role loadout.
                        Widgets.DrawBoxSolid(chip, isMain
                            ? new Color(0.30f, 0.47f, 0.34f, 0.95f)
                            : new Color(0.25f, 0.35f, 0.28f, 0.9f));
                        float textX = chip.x + 4f;
                        if (isMain)
                        {
                            Widgets.DrawBoxSolid(new Rect(chip.x, chip.y, 2f, chip.height), LoadoutUtility.ColGood);
                            Widgets.ThingIcon(new Rect(chip.x + 4f, chip.y + 1f, 18f, 18f), roleMainW);
                            textX = chip.x + 4f + mainIconW;
                        }
                        Rect chipTextRect = new Rect(textX, chip.y, chip.xMax - 20f - textX, 20f);
                        Widgets.Label(chipTextRect, t);
                        // Snapshot warning (2026-08-01, confirmed): RoleUtility.Merge/BuildRoleLoadout
                        // flattens every dropped-in component's CURRENT contents into the Role's own Slots
                        // only at Create/Update time - unlike a pawn's normal "+Pack" attach (which stays a
                        // live MultiParentUtility link), editing this item afterward never reaches colonists
                        // already on this Role until Create/Update is clicked again here.
                        // Behind TipWanted because the text is now assembled, and in a long role list the card
                        // would otherwise build an unseen tooltip per chip every frame.
                        if (LoadoutUtility.TipWanted(chipTextRect))
                            TooltipHandler.TipRegion(chipTextRect, isMain
                                ? ("CER_GenerateLoadouts_RoleMainWeaponTip".Translate(roleMainW.LabelCap).ToString()
                                   + "\n\n" + "CER_GenerateLoadouts_RoleComponentSnapshotTip".Translate().ToString())
                                : "CER_GenerateLoadouts_RoleComponentSnapshotTip".Translate().ToString());
                        Rect chipXRect = new Rect(chip.xMax - 20f, chip.y, 20f, 20f);
                        if (blockedRemove)
                        {
                            // VISIBLY disabled instead of silently refused (it behaved like a dead button: normal
                            // click, 350 ms, 700 ms - the building block stayed). The click arrived, it just ran
                            // into the refusal below and left nothing but a message at the top of the screen -
                            // indistinguishable from a broken button. Disabled means greyed out. TintedButton
                            // instead of GUI.enabled, because RimWorld's button texture goes through
                            // GUI.DrawTexture, and GUI.enabled demonstrably draws nothing there (see
                            // LoadoutUtility.TintedButton).
                            LoadoutUtility.TintedButton(chipXRect, null, disabled: true);
                            GUI.color = new Color(1f, 1f, 1f, 0.45f);
                            Text.Anchor = TextAnchor.MiddleCenter;
                            Widgets.Label(chipXRect, "CER_GenerateLoadouts_RemoveButton".Translate());
                            Text.Anchor = TextAnchor.UpperLeft;
                            GUI.color = Color.white;
                            // The 'every colonist on this role would be left empty-handed' sentence only if someone is on it.
                            if (LoadoutUtility.TipWanted(chipXRect))
                                TooltipHandler.TipRegion(chipXRect, OnRole(role).Count > 0
                                    ? "CER_GenerateLoadouts_RemoveLastChipBlockedTip".Translate(CardNameOf(role))
                                    : "CER_GenerateLoadouts_RemoveLastChipBlockedTipNoColonists".Translate(CardNameOf(role)));
                        }
                        else if (Widgets.ButtonText(chipXRect, "CER_GenerateLoadouts_RemoveButton".Translate())) removeIdx = c;
                        chipX = chip.xMax + 4f;
                    }
                    // Removal writes immediately - see RoleAutoSaves.
                    //
                    // Removing the LAST building block is REFUSED (after closing and reopening the window the
                    // building block was BACK, reproducibly).
                    // The cause was an earlier change: RoleNeedsCommit says "nothing to do" for an empty row, so
                    // no commit ran, so role.composition stayed unchanged - and on the next open CompFor rebuilt
                    // the row from exactly that composition. The chip came back because the removal never landed
                    // anywhere.
                    // Simply writing it anyway would be wrong: CommitRole explicitly refuses an update that would
                    // EMPTY a filled role - exactly the damage this whole thing is about. The honest answer is
                    // therefore to refuse the action instead of silently undoing it: a role without building
                    // blocks doesn't exist; to get rid of it, delete it.
                    // The refusal now sits ON THE BUTTON (blockedRemove above), no longer here: here it was
                    // invisible and read as a defect. So only a permitted removal can arrive here.
                    if (removeIdx >= 0)
                    {
                        comp.RemoveAt(removeIdx);
                        // If the removal empties a NOT yet created template, its stored composition is cleared
                        // too - AutoCommitRole writes nothing for an empty row, and CompFor would otherwise
                        // rebuild the row from exactly this template on the next open (same mechanism as the
                        // last-block case above). No loadout is emptied in the process; there isn't one.
                        if (comp.Count == 0 && !created) role.composition = new System.Collections.Generic.List<string>();
                        AutoCommitRole(role, st);
                    }
                }

                // Click selects; drop adds.
                if (Event.current.type == EventType.MouseDown && Event.current.button == 0 && Mouse.IsOver(card) && dragLoadout == null && dragWeaponDef == null)
                {
                    SetActiveRole(role);
                }
                if ((dragLoadout != null || dragWeaponDef != null) && Event.current.type == EventType.MouseUp && Mouse.IsOver(card))
                {
                    // A dragged WEAPON building block materializes only NOW -
                    // from here on identical to dragging an existing template.
                    Loadout dropLo = dragLoadout != null ? dragLoadout : EnsureDefaultLoadout(dragWeaponDef);
                    if (dropLo != null && !comp.Contains(dropLo)) comp.Add(dropLo);
                    SetActiveRole(role);
                    Event.current.Use();
                    dragLoadout = null;
                    dragWeaponDef = null;
                    // Dropping, too - that is the whole point of this interaction.
                    AutoCommitRole(role, st);
                }

                ry += cardH + 6f;
            }
            // Third box with a search field, third empty message - there was NONE here before: typing a search
            // term that matches no role left the box simply empty, and the display didn't show whether the
            // search was to blame or there really were no roles left. Roles without a search can't be absent
            // (the built-in ones are always there), so only the search case.
            if (all.Count == 0 && !string.IsNullOrEmpty(roleCardSearch))
            {
                GUI.color = new Color(1f, 1f, 1f, 0.5f);
                Widgets.Label(new Rect(4f, 4f, viewRect.width - 8f, 40f),
                    "CER_GenerateLoadouts_NoRolesMatchSearch".Translate(roleCardSearch));
                GUI.color = Color.white;
            }
            Widgets.EndScrollView();

            if (copySource != null)
            {
                LoadoutRole copy = copySource.Clone();
                copy.name = UniqueRoleCopyName(copySource.name, st);
                // Same list the original lives in (save-local stays local, global stays global), at the top -
                // same "freshest first" placement a brand new role gets.
                if (local != null && local.Contains(copySource)) local.Insert(0, copy);
                else st.roles.Insert(0, copy);
                // Clear first, then copy over: Clone() carries the stored composition along, and CompFor fills
                // the row from it on first access. A plain AddRange would then have every source twice in the
                // copy.
                List<Loadout> copyComp = CompFor(copy);
                copyComp.Clear();
                copyComp.AddRange(CompFor(copySource));
                SetActiveRole(copy);
            }
        }

        // "(2)", "(3)", ... - re-derives the base name first if the source itself already carries a "(n)"
        // suffix (copying a copy), so repeated duplication counts up instead of stacking suffixes.
        private static string UniqueRoleCopyName(string baseName, CEReloadedSettings st)
        {
            string b = baseName ?? "Role";
            var m = System.Text.RegularExpressions.Regex.Match(b, @"^(.*) \((\d+)\)$");
            if (m.Success) b = m.Groups[1].Value;
            HashSet<string> existing = new HashSet<string>(AllRoles(st).Select(r => r.name), StringComparer.OrdinalIgnoreCase);
            int n = 2;
            string candidate = b + " (" + n + ")";
            while (existing.Contains(candidate)) { n++; candidate = b + " (" + n + ")"; }
            return candidate;
        }

        private void CardFactorField(Rect r, LoadoutRole role, int slot, System.Func<float> get, System.Action<float> set)
        {
            if (role == null) return;
            if (!cardFactorBuffers.TryGetValue(role, out string[] bufs)) cardFactorBuffers[role] = bufs = new string[4];
            float v = get();
            string buf = bufs[slot] ?? v.ToString("0.##");
            Widgets.TextFieldNumeric(r, ref v, ref buf, 0f, 99f);
            bufs[slot] = buf;
            set(v);
        }

        private class DispRow { public string label; public string amount; public Color color; public bool strike; }

        private void DrawRolePreview(Rect panel, CEReloadedSettings st)
        {
            Widgets.DrawMenuSection(panel);
            Rect inner = panel.ContractedBy(8f);

            if (activeRoleObj == null)
            {
                GUI.color = new Color(1f, 1f, 1f, 0.5f);
                Widgets.Label(inner, "CER_GenerateLoadouts_SelectRolePrompt".Translate());
                GUI.color = Color.white;
                return;
            }

            LoadoutRole role = activeRoleObj;
            List<Loadout> comp = CompFor(role);
            List<RoleMergeEntry> resultRows = RoleUtility.Merge(role, comp).Where(r => r.scaledCount > 0).ToList();

            string finalName = (roleNameEdit ?? "").Trim();
            // Looked up by the role's OWN current name, not the text field's (possibly just-renamed) value -
            // this IS the same role being edited, whether or not its name changed this frame. Keying off the
            // typed name instead used to make a rename look like "no existing loadout" (since nothing is
            // filed under the NEW name yet), showing "Create" instead of "Update" and losing track of the
            // already-committed object entirely.
            // By identity - no longer by label. A role renamed in CE's own editor is thus still recognized as
            // "already exists" instead of counting as new and placing a second loadout next to it on the
            // next save.
            Loadout existing = RoleUtility.RoleLoadoutOf(role);

            // Header: editable final name + "(exists - delta)" hint.
            Widgets.Label(new Rect(inner.x, inner.y + 2f, 56f, 24f), "CER_GenerateLoadouts_PreviewLabel".Translate());
            Widgets.Label(new Rect(inner.x + 60f, inner.y + 2f, 46f, 24f), "CER_GenerateLoadouts_RoleTagLabel".Translate());
            // Name field: writes when the edit SETTLES, not per keystroke.
            //
            // Writing per keystroke, as packs do, means something different here: a role's name IS the label of
            // the shared loadout colonists are attached to. Writing per keystroke would mean that typing "Medic"
            // creates or renames the roles "M", "Me", "Med" in turn - and every colonist on it moves along. A
            // pack doesn't have this problem because nobody is attached to it.
            // Therefore: the contents (chips, factors) write immediately, the NAME only once editing is done.
            // This is a deliberate deviation from the pack behaviour, in the direction where a mistake doesn't
            // hurt.
            string beforeName = roleNameEdit;
            roleNameEdit = Widgets.TextField(new Rect(inner.x + 108f, inner.y, 180f, 24f), roleNameEdit);
            // AFTER A SHORT IDLE PAUSE instead of on focus loss (after closing and reopening the window the role
            // carried its old name again, and the CE loadout was never renamed - reproducibly).
            // The first version relied on focus (GUI.SetNextControlName + GetNameOfFocusedControl). That didn't
            // hold up; exactly why isn't visible from the outside, and betting on IMGUI's focus handling is the
            // wrong design here anyway. An idle pause is independent of that, deterministic, and at the same time
            // solves the reason for not writing per keystroke in the first place: typing "Medic" doesn't create
            // roles "M", "Me", "Med".
            if (roleNameEdit != beforeName)
            {
                roleNameEditDirty = true;
                roleNameEditAt = Time.realtimeSinceStartup;
            }
            else if (roleNameEditDirty && Time.realtimeSinceStartup - roleNameEditAt > 0.8f)
            {
                roleNameEditDirty = false;
                AutoCommitRole(role, st);
            }
            // The left edge of the main button is fixed here already - the label below needs it as a limit so it
            // doesn't push into the button when measured.
            float createRectLeft = inner.xMax - 230f;
            // REMOVED: a counter for the "create/update all" button used to be here, and it became moot along with
            // the button. It wasn't merely redundant but expensive: the loop called RoleNeedsCommit for EVERY role
            // in EVERY frame, which computes a full merge over all of a role's building blocks.
            // "(exists - delta)" used to depend solely on `existing != null`, so it really only said "one already
            // exists" and was still shown when there was nothing left to do. Together with the counting bug in
            // RoleNeedsCommit this read as "something is still pending", while the single button next to it said
            // "unchanged". Now the label says exactly what the same test tells the counter - the same source, no
            // second calculation.
            if (existing != null)
            {
                bool roleDirty = role != null && RoleNeedsCommit(role, comp);
                GUI.color = roleDirty ? new Color(0.95f, 0.85f, 0.4f) : new Color(1f, 1f, 1f, 0.45f);
                // Width MEASURED instead of a fixed 120: "(exists - up to date)" didn't fit and showed as "(exists
                // - up to", while there was free space to the right. The shorter variant "(exists - delta)" fit by
                // chance - that's why it only showed up with one of the two. Translations are longer than the
                // English, so a fixed number is fundamentally wrong here.
                string existsLabel = (roleDirty
                    ? "CER_GenerateLoadouts_ExistsDeltaLabel".Translate()
                    : "CER_GenerateLoadouts_ExistsUpToDateLabel".Translate()).ToString();
                float existsX = inner.x + 294f;
                float existsW = Mathf.Min(Text.CalcSize(existsLabel).x + 6f, createRectLeft - 8f - existsX);
                Widgets.Label(new Rect(existsX, inner.y + 2f, existsW, 24f), existsLabel);
                GUI.color = Color.white;
            }
            Rect createRect = new Rect(createRectLeft, inner.y, 230f, 30f);
            Rect createAllRect = new Rect(createRect.x - 198f, inner.y, 190f, 30f);

            // ============ NO buttons anymore, without exception ============
            // Goal: there is only "automatically saved" - the buttons "create / update all (1)" and "update role"
            // still showed up sometimes.
            //
            // Two exceptions had been kept, and exactly those kept showing up: a role without a stored composition
            // (from an earlier game session) and one whose save would have adopted an orphaned role of the same
            // name. On top of that, ONE such role anywhere in the list brought the buttons back for ALL of them -
            // hence the "sometimes".
            //
            // Why this is now safe, point by point:
            //  - Empty row: AutoCommitRole bails out on comp.Count == 0. A role whose composition the mod doesn't
            //    know is therefore NEVER touched on its own - the case the first exception guarded against can't
            //    happen without player action.
            //  - Emptying a filled role: CommitRole hard-refuses that, independent of any button.
            //  - Adopting an orphaned role that has carriers: the confirmation remains, it now lives in
            //    AutoCommitRole. A one-time question at the right moment is what's needed here - not a button that
            //    stays there permanently.
            // What remains is the quiet "saved automatically" line and the preview below it.
            GUI.color = new Color(1f, 1f, 1f, 0.45f);
            Text.Anchor = TextAnchor.MiddleRight;
            Widgets.Label(new Rect(createAllRect.x, inner.y, createRect.xMax - createAllRect.x, 30f),
                "CER_GenerateLoadouts_RoleAutoSavedHint".Translate());
            Text.Anchor = TextAnchor.UpperLeft;
            GUI.color = Color.white;

            // Existing role loadout, for the delta.
            Dictionary<string, int> existMap = SlotMap(existing);
            Dictionary<string, string> existLabels = new Dictionary<string, string>();
            // Contents of the EXISTING role, with def and amount - for mass and bulk further down. Collected here
            // and not again there because this pass over existing.Slots runs anyway: for a loadout WITH parents,
            // Slots is not a field but a rebuild per access, and role loadouts attach their packs exactly that way.
            List<KeyValuePair<Def, int>> existContent = new List<KeyValuePair<Def, int>>();
            if (existing != null && existing.Slots != null)
                foreach (LoadoutSlot s in existing.Slots)
                {
                    existLabels[SlotKey(s.thingDef, s.genericDef)] = s.thingDef != null ? s.thingDef.label : (s.genericDef != null ? s.genericDef.label : "?");
                    Def d = (Def)s.thingDef ?? s.genericDef;
                    if (d != null) existContent.Add(new KeyValuePair<Def, int>(d, s.count));
                }

            // Build display rows: added (green), changed (yellow, old -> new), kept (neutral), removed (red strikethrough).
            Color cAdd = LoadoutUtility.ColGood, cChg = new Color(0.95f, 0.85f, 0.4f), cKeep = new Color(1f, 1f, 1f, 0.85f), cRem = LoadoutUtility.ColBad;
            List<DispRow> disp = new List<DispRow>();
            // "Loaded view": nothing dropped, the role already exists - the list then shows its CURRENT state, and
            // nothing is being written at all. That matters three times below (mass/bulk, stack count, legend),
            // so it is named once instead of asked three times.
            bool loadedView = comp.Count == 0 && existing != null;
            if (loadedView)
            {
                // Loaded view (nothing dropped yet): show the existing role loadout as it stands.
                foreach (var kv in existMap)
                    disp.Add(new DispRow { label = existLabels.TryGetValue(kv.Key, out string l) ? l : kv.Key, amount = kv.Value.ToString(), color = cChg });
            }
            else
            {
                HashSet<string> newKeys = new HashSet<string>();
                foreach (RoleMergeEntry r in resultRows)
                {
                    string key = SlotKey(r.thing, r.generic);
                    newKeys.Add(key);
                    // Amount always shows default -> factored so the role's effect is visible; the colour shows the
                    // delta versus an already-created role loadout (added / changed / kept).
                    string amount = r.baseCount != r.scaledCount ? r.baseCount + " -> " + r.scaledCount : r.scaledCount.ToString();
                    Color col;
                    if (existing == null) col = cKeep;
                    else if (!existMap.ContainsKey(key)) col = cAdd;
                    else if (existMap[key] != r.scaledCount) col = cChg;
                    else col = cKeep;
                    disp.Add(new DispRow { label = r.label, amount = amount, color = col });
                }
                // Pack contents belong in this list (two symptoms: "Any Medicine" was struck through for no
                // reason, and the preview showed only the contents of the loadouts, not those of the Equipment
                // Packs). Both had the same cause: RoleUtility.Merge deliberately returns only weapons and ammo,
                // because a pack is ATTACHED to the role instead of melted in (see CommitRole). The preview read
                // only the merge half - and since the existing role loadout does carry the pack contents via the
                // multi-parent chain, they appeared below as "no longer included": struck through and red, even
                // though they stay. That was the only place in the mod where strikethrough didn't mean "will be
                // deleted".
                // The pack name is appended so it's clear where the row comes from - it can't be changed
                // individually here, only via the pack itself.
                foreach (Loadout pk in RoleUtility.PacksIn(comp))
                {
                    if (pk?.OwnSlots == null) continue;
                    foreach (LoadoutSlot s in pk.OwnSlots)
                    {
                        if (s.count <= 0) continue;
                        string pKey = SlotKey(s.thingDef, s.genericDef);
                        newKeys.Add(pKey);
                        string pLabel = (s.thingDef != null ? s.thingDef.label
                            : (s.genericDef != null ? s.genericDef.label : "?")) + " (" + RoleUtility.DisplayLabel(pk) + ")";
                        Color pCol;
                        if (existing == null) pCol = cKeep;
                        else if (!existMap.ContainsKey(pKey)) pCol = cAdd;
                        else if (existMap[pKey] != s.count) pCol = cChg;
                        else pCol = cKeep;
                        disp.Add(new DispRow { label = pLabel, amount = s.count.ToString(), color = pCol });
                    }
                }
                if (existing != null)
                    foreach (var kv in existMap)
                        if (!newKeys.Contains(kv.Key))
                            disp.Add(new DispRow { label = existLabels.TryGetValue(kv.Key, out string l) ? l : kv.Key, amount = kv.Value.ToString(), color = cRem, strike = true });
            }

            // Column header.
            float headerY = inner.y + 34f;
            float nameCol = (inner.width - 16f) * 0.6f;
            GUI.color = new Color(1f, 1f, 1f, 0.6f);
            Widgets.Label(new Rect(inner.x + 4f, headerY, nameCol, 20f), "CER_GenerateLoadouts_ItemColumnHeader".Translate());
            Widgets.Label(new Rect(inner.x + nameCol + 2f, headerY, inner.width - nameCol - 6f, 20f), "CER_GenerateLoadouts_DefaultWithFactorHeader".Translate());
            GUI.color = Color.white;

            // Table.
            float tableTop = inner.y + 54f;
            Rect tableOut = new Rect(inner.x, tableTop, inner.width, inner.yMax - tableTop - 70f);
            Rect tableView = new Rect(0, 0, tableOut.width - 16f, Mathf.Max(disp.Count, 1) * 22f + 2f);
            Widgets.BeginScrollView(tableOut, ref rolePreviewScroll, tableView);
            if (disp.Count == 0)
            {
                GUI.color = new Color(1f, 1f, 1f, 0.5f);
                Widgets.Label(new Rect(2f, 2f, tableView.width - 4f, 22f), "CER_GenerateLoadouts_DropToSeeResult".Translate());
                GUI.color = Color.white;
            }
            float ty = 0f;
            float nameW = tableView.width * 0.6f;
            foreach (DispRow d in disp)
            {
                GUI.color = d.color;
                Widgets.Label(new Rect(4f, ty, nameW - 8f, 20f), d.label.Truncate(nameW - 12f));
                Widgets.Label(new Rect(nameW + 2f, ty, tableView.width - nameW - 4f, 20f), d.amount);
                if (d.strike)
                    Widgets.DrawLineHorizontal(4f, ty + 10f, Mathf.Min(Text.CalcSize(d.label).x, nameW - 12f));
                GUI.color = Color.white;
                ty += 22f;
            }
            Widgets.EndScrollView();
            // Only three rows fit here - see DrawScrollMoreHint for why that looks like a complete list without a
            // hint.
            LoadoutUtility.DrawScrollMoreHint(tableOut, tableView, rolePreviewScroll, 22f);

            // Mass / bulk + legend.
            float m = 0f, b = 0f;
            StatDef bulkStat = DefDatabase<StatDef>.GetNamed("Bulk", false);
            // In the loaded view, weigh what the role ALREADY contains (a created role showed "Result: 0 stacks",
            // "Mass 0.0/40.0 kg", "Bulk 0.0/20.0" while the list directly above it listed a rifle and 150 rounds).
            // The zero wasn't miscalculated but mislabeled: all three values read resultRows, i.e. what this drop
            // WOULD write - and it writes nothing here. But the bars say "Mass" and "Bulk", not "change in mass".
            // A role with a rifle and 150 rounds claiming 0.0 kg is simply false - and for an existing role the
            // carrying load is exactly the question you're looking for.
            // The bars now show the same thing in both cases: what the role holds after this screen.
            if (loadedView)
            {
                foreach (var kv in existContent)
                {
                    ThingDef td = kv.Key as ThingDef;
                    if (td != null)
                    {
                        m += td.GetStatValueAbstract(StatDefOf.Mass) * kv.Value;
                        if (bulkStat != null) b += td.GetStatValueAbstract(bulkStat) * kv.Value;
                    }
                    else if (kv.Key is LoadoutGenericDef gd) { m += gd.mass * kv.Value; b += gd.bulk * kv.Value; }
                }
            }
            foreach (RoleMergeEntry e in resultRows)
            {
                if (e.thing != null)
                {
                    m += e.thing.GetStatValueAbstract(StatDefOf.Mass) * e.scaledCount;
                    if (bulkStat != null) b += e.thing.GetStatValueAbstract(bulkStat) * e.scaledCount;
                }
                else if (e.generic != null) { m += e.generic.mass * e.scaledCount; b += e.generic.bulk * e.scaledCount; }
            }
            // Contents of attached Equipment Packs COUNT (the four ready meals from the pack were missing from
            // the list AND from mass/bulk).
            // Why it was missing at all is not a bug but a decision: RoleUtility.Merge deliberately does NOT melt
            // packs in, they hang off the role as a real reference (real links for roles too). So the colonist
            // does get the contents - only this preview never counted them, so its carrying-load figure was too
            // low for every role with a pack. Anyone just under the limit was actually over it.
            // Not added to resultRows: the list shows what gets WRITTEN into the role, and the pack contents are
            // exactly what isn't - they stay referenced. They only count here for the load.
            int packStacks = 0;
            foreach (Loadout pk in RoleUtility.PacksIn(comp))
            {
                if (pk?.OwnSlots == null) continue;
                foreach (LoadoutSlot s in pk.OwnSlots)
                {
                    packStacks++;
                    if (s.thingDef != null)
                    {
                        m += s.thingDef.GetStatValueAbstract(StatDefOf.Mass) * s.count;
                        if (bulkStat != null) b += s.thingDef.GetStatValueAbstract(bulkStat) * s.count;
                    }
                    else if (s.genericDef != null) { m += s.genericDef.mass * s.count; b += s.genericDef.bulk * s.count; }
                }
            }
            GUI.color = new Color(1f, 1f, 1f, 0.7f);
            // Show the legend only where there is actually something to distinguish ("Result: 0 stacks" used to
            // be followed by "added changed removed" without any number - it looked like a label whose values were
            // missing). In the loaded view nothing is added, changed or removed, so the three words explain colours
            // that don't even appear in the list above.
            string legend = (existing != null && !loadedView) ? "CER_GenerateLoadouts_ResultLegend".Translate() : "";
            // The number states the pack share separately instead of silently swallowing it - otherwise it
            // contradicts the bars right below, which do include it.
            // The FIRST number is the total (stack count and list didn't match): previously it showed the number
            // WITHOUT packs and the pack share next to it, i.e. "3 + 2" above a list with five rows - you had to
            // add it up yourself to notice nothing was missing. Now the first number counts exactly the rows you
            // see above (struck-through ones are visibly NOT part of the result), and the pack share follows as
            // an explanation.
            // In the loaded view the rows of the list itself count - the same promise as above, just for the case
            // where resultRows is empty: the first number is always the number of rows you see above.
            int shownStacks = loadedView ? disp.Count : resultRows.Count + packStacks;
            string stacksLine = packStacks > 0
                ? "CER_GenerateLoadouts_ResultStacksWithPacks".Translate(shownStacks.ToString(), packStacks.ToString()).ToString()
                : "CER_GenerateLoadouts_ResultStacks".Translate(shownStacks.ToString()).ToString();
            Widgets.Label(new Rect(inner.x, inner.yMax - 68f, inner.width, 18f), stacksLine + legend);
            GUI.color = Color.white;
            LoadoutUtility.DrawMassBulkBars(new Rect(inner.x, inner.yMax - 52f, Mathf.Min(inner.width, 340f), 52f), m, b);
        }

        private static string SlotKey(ThingDef t, LoadoutGenericDef g)
            => t != null ? "t:" + t.defName : (g != null ? "g:" + g.defName : "?");

        private static Dictionary<string, int> SlotMap(Loadout lo)
        {
            Dictionary<string, int> map = new Dictionary<string, int>();
            if (lo == null || lo.Slots == null) return map;
            foreach (LoadoutSlot s in lo.Slots) map[SlotKey(s.thingDef, s.genericDef)] = s.count;
            return map;
        }

        // How many colonists are attached to an ORPHANED loadout that CommitRole would adopt right away.
        // 0 means: either there is no loadout with that name, or this role already owns it (then it's not an
        // orphan but a normal update), or nobody carries it.
        // Deliberately mirrors the fallback logic in CommitRole exactly - if that finds the object by the OLD
        // name, nothing is adopted and no warning may be shown here either.
        private int CountOrphanRoleCarriers(LoadoutRole role, string rawName)
        {
            string name = (rawName ?? "").Trim();
            if (name.Length == 0) return 0;
            string newLabel = RoleUtility.RoleMarker + " " + name;
            string oldLabel = RoleUtility.RoleMarker + " " + (role?.name ?? "").Trim();
            if (oldLabel == newLabel) return 0;
            if (LoadoutManager.Loadouts.Any(l => l != null && !l.defaultLoadout && l.label == oldLabel)) return 0;
            Loadout orphan = LoadoutManager.Loadouts.FirstOrDefault(l => l != null && !l.defaultLoadout && l.label == newLabel);
            if (orphan == null) return 0;
            int n = 0;
            foreach (Pawn p in LoadoutUtility.AllColonyPawns())
            {
                Loadout lo = AmmoAssignmentUtility.DisplayLoadout(p.GetLoadout());
                if (lo != null && lo.UniqueID == orphan.UniqueID) n++;
            }
            return n;
        }

        // Create or update the "[Role] name" loadout from a composition. Returns true if anything actually changed, with a
        // human-readable message describing what happened (created / renamed / +added -removed ~changed / unchanged).
        private bool CommitRole(LoadoutRole role, List<Loadout> comp, string rawName, CEReloadedSettings st, out string message)
        {
            string name = (rawName ?? "").Trim();
            List<RoleMergeEntry> res = RoleUtility.Merge(role, comp).Where(r => r.scaledCount > 0).ToList();
            // Packs count as contents even though they no longer end up in `res` - otherwise a role consisting
            // ONLY of an Equipment Pack would wrongly count as empty and could no longer be created at all. That
            // would be the silent side effect of taking them out of the merge.
            List<Loadout> rolePacks = RoleUtility.PacksIn(comp);
            // PURE RENAME, before the "nothing to do" refusal kicks in (changing ONLY the name of an existing
            // role left Update GREYED OUT). The cause wasn't the button but this line: with nothing dragged in,
            // comp is empty, and so a rename counted as nothing too.
            // Deliberately WITHOUT rebuilding the contents: comp is empty, a rebuild would EMPTY the role. Only
            // the label of the existing object is changed here - every colonist on it keeps exactly their gear,
            // because it's the same loadout.
            if (name.Length > 0 && comp.Count == 0)
            {
                string renameOld = RoleUtility.RoleMarker + " " + (role.name ?? "").Trim();
                string renameNew = RoleUtility.RoleMarker + " " + name;
                Loadout renameTarget = renameOld == renameNew ? null
                    : LoadoutManager.Loadouts.FirstOrDefault(l => l != null && !l.defaultLoadout && l.label == renameOld);
                if (renameTarget != null)
                {
                    if (st != null && AllRoles(st).Any(r => r != role && (r.name ?? "").Trim().Length > 0
                        && RoleUtility.RoleMarker + " " + r.name.Trim() == renameNew))
                    {
                        message = "CER_GenerateLoadouts_RoleNameCollision".Translate(name);
                        return false;
                    }
                    renameTarget.label = renameNew;
                    role.name = name;
                    message = "CER_GenerateLoadouts_RenamedNoChanges".Translate(renameNew);
                    return true;
                }
            }
            if (name.Length == 0 || comp.Count == 0 || (res.Count == 0 && rolePacks.Count == 0)) { message = "CER_GenerateLoadouts_NothingToCreate".Translate(); return false; }

            string newLabel = RoleUtility.RoleMarker + " " + name;
            // Refuse a name collision with a DIFFERENT role outright (2026-08-01 fix) - the newLabel fallback
            // lookup just below used to match ANY loadout with that label regardless of which LoadoutRole
            // created it, silently wiping and overwriting another role's real, in-use shared Loadout (every
            // colonist already assigned to it would have their gear reassigned out from under them with zero
            // warning). Checked BEFORE the fallback runs, not after, so the destructive path never executes.
            if (st != null && AllRoles(st).Any(r => r != role && (r.name ?? "").Trim().Length > 0
                && RoleUtility.RoleMarker + " " + r.name.Trim() == newLabel))
            {
                message = "CER_GenerateLoadouts_RoleNameCollision".Translate(name);
                return false;
            }
            // Find the loadout THIS role already committed under its OLD (current) name first - a rename
            // must reuse and relabel that SAME object (so any pawn already assigned to it keeps pointing at
            // something real) rather than leaving it behind untouched and creating an unrelated duplicate
            // under the new name, which is what looking up purely by the new typed name used to do.
            // By identity FIRST: RoleLoadoutOf knows this role's loadout without using its name - and records the
            // link the first time. That way a rename still finds its target even when the label no longer
            // matches (e.g. because it was changed in CE's own editor). Only if the role has no loadout at all
            // yet does the old label-based lookup apply.
            string oldLabel = RoleUtility.RoleMarker + " " + (role.name ?? "").Trim();
            Loadout existing = RoleUtility.RoleLoadoutOf(role);
            if (existing == null)
                existing = LoadoutManager.Loadouts.FirstOrDefault(l => l != null && !l.defaultLoadout && l.label == oldLabel);
            bool renamed = existing != null && existing.label != newLabel;
            if (existing == null)
                existing = LoadoutManager.Loadouts.FirstOrDefault(l => l != null && !l.defaultLoadout && l.label == newLabel);

            if (existing == null)
            {
                Loadout made = RoleUtility.BuildRoleLoadout(role, comp, name);
                LoadoutManager.AddLoadout(made);
                // Attach packs as a REAL reference, don't melt them in - the same multi-parent mechanism a pack
                // on a single colonist has always used.
                // Must come AFTER AddLoadout: the link is stored via the UniqueID, and the LoadoutManager only
                // assigns that there.
                MultiParentUtility.SetExtraParents(made, rolePacks);
                // FROM NOW ON the role remembers its loadout by identity - that's why it no longer has to be
                // recognized by name. After AddLoadout, because the UniqueID is only assigned there.
                RoleUtility.LinkRoleLoadout(role, made);
                role.name = name;
                RememberComposition(role, comp);
                // The DISPLAYED name, not the stored label: the four completion messages of this function said
                // "[Role] Foo" while every list next to them already wrote "Foo". The marker is bookkeeping and is
                // never shown - see LoadoutUtility.LoadoutDisplayName.
                message = "CER_GenerateLoadouts_CreatedMessage".Translate(LoadoutUtility.LoadoutDisplayName(made.label));
                return true;
            }

            Dictionary<string, int> oldMap = SlotMap(existing);

            // HARD BLOCK: an update may never EMPTY a filled role.
            // That is exactly the damage role autosave is all about: a role whose composition the mod no longer
            // knows gets rebuilt from an empty row of boxes - and every colonist on it is left without gear.
            // Previously the protection relied on a human always pressing "Update" and seeing the struck-through
            // preview. That is not protection, that's attention; and it was the only thing standing in the way of
            // immediate saving.
            // Now the rule is in the code, where it applies regardless of order and attention: nothing in it AND
            // there was something in it before AND no packs that could carry the contents -> refused, with a
            // reason, without touching anything.
            // "There was something in it before" ALSO means: it had attached packs. A role that gets all its
            // contents from a pack has zero own slots - with oldMap alone it would have fallen through this block,
            // and SetExtraParents below would have taken the pack away from it.
            if (res.Count == 0 && rolePacks.Count == 0
                && (oldMap.Count > 0 || MultiParentUtility.GetExtraParents(existing).Count > 0))
            {
                message = "CER_GenerateLoadouts_RoleWouldEmpty".Translate(RoleUtility.DisplayLabel(existing));
                return false;
            }

            existing.OwnSlots.Clear();   // OwnSlots, not Slots: Slots is own+inherited and returns a copy for parented loadouts
            foreach (RoleMergeEntry e in res)
            {
                if (e.thing != null) existing.AddSlot(new LoadoutSlot(e.thing, e.scaledCount));
                else if (e.generic != null) existing.AddSlot(new LoadoutSlot(e.generic, e.scaledCount));
            }
            // On update the same reference attachment as on create - otherwise an existing role keeps its old
            // snapshot forever, and the fix would only work for new roles.
            MultiParentUtility.SetExtraParents(existing, rolePacks);
            existing.label = newLabel;
            // Record the link on update too - a role from an old save had no link yet, and this is the moment
            // it is reliably known.
            RoleUtility.LinkRoleLoadout(role, existing);
            role.name = name;
            RememberComposition(role, comp);
            Dictionary<string, int> newMap = SlotMap(existing);
            int added = 0, removed = 0, changed = 0;
            foreach (var kv in newMap) { if (!oldMap.TryGetValue(kv.Key, out int o)) added++; else if (o != kv.Value) changed++; }
            foreach (var kv in oldMap) if (!newMap.ContainsKey(kv.Key)) removed++;
            // Once for all four messages below - see the comment at CreatedMessage.
            string shownLabel = LoadoutUtility.LoadoutDisplayName(newLabel);

            if (renamed)
            {
                message = added == 0 && removed == 0 && changed == 0
                    ? "CER_GenerateLoadouts_RenamedNoChanges".Translate(shownLabel)
                    : "CER_GenerateLoadouts_RenamedWithChanges".Translate(shownLabel, added.ToString(), removed.ToString(), changed.ToString());
                return true;
            }
            if (added == 0 && removed == 0 && changed == 0) { message = "CER_GenerateLoadouts_UnchangedMessage".Translate(shownLabel); return false; }
            message = "CER_GenerateLoadouts_UpdatedMessage".Translate(shownLabel, added.ToString(), removed.ToString(), changed.ToString());
            return true;
        }

        // True if committing this role would actually create a new loadout or change an existing one (so "Create / Update
        // all" only counts roles with pending work, not ones already up to date).
        // The name the card DISPLAYS - the typed one while renaming, otherwise the stored one.
        // One place for the label, the main-weapon pick and the question "is there anything to do here".
        private string EffectiveRoleName(LoadoutRole r)
        {
            if (r == activeRoleObj && !string.IsNullOrWhiteSpace(roleNameEdit)) return roleNameEdit.Trim();
            return (r?.name ?? "").Trim();
        }

        // Only called on the two paths that actually rebuild the role's contents - NOT in the pure rename
        // branch. There comp is empty, and overwriting the stored composition with it would throw away the
        // recovery at exactly the moment it must hurt least: renaming doesn't change the contents.
        private static void RememberComposition(LoadoutRole role, List<Loadout> comp)
        {
            if (role == null) return;
            List<string> labels = new List<string>();
            if (comp != null)
                foreach (Loadout l in comp)
                    if (l != null && !string.IsNullOrEmpty(l.label) && !labels.Contains(l.label)) labels.Add(l.label);
            // A diagnostic line used to be here to check whether dragging onto a local role also writes a
            // GLOBAL one. The cause turned out to be the row-index-bound input buffers, see cardFactorBuffers.
            // Removed again.
            role.composition = labels;
        }

        // ================== Roles save immediately, like Equipment Packs ==================
        //
        // Roles are created / updated automatically, just like Equipment Packs. The "create/update all" and
        // "update role xyz" buttons are gone; saving happens immediately instead.
        //
        // Step 1 (the role remembers its composition) came first. This is step 2.
        //
        // THE SWITCH, and it isn't caution but necessity: a role from an earlier game session has NO stored
        // composition (such roles keep showing "drag a loadout here"). For them an update rebuilds the role
        // from an empty row and would empty it. Such roles therefore keep the preview and button until they
        // have been deliberately composed once - after that they follow the new behaviour too.
        //
        // SECOND BRAKE: if saving adopts an orphaned role of the same name that still has colonists attached,
        // the button path asks first. That question must not pop up on every drag - so such a role doesn't
        // save on its own but keeps its button.
        // Now true without exception - the function stays as the ONE place where the question is answered,
        // in case there is ever an exception again. Why the two old ones are gone is explained in detail at
        // the button block in DrawRolePreview; in short: one can't happen without player action
        // (AutoCommitRole doesn't touch an empty row), the other moved into the confirmation in AutoCommitRole.
        private bool RoleAutoSaves(LoadoutRole role) => role != null;

        // Writes immediately, without a message and without a button. Quiet, because a completion message on
        // EVERY drag would be exactly the kind of noise packs don't make either - what happened is shown in
        // the preview below.
        private void AutoCommitRole(LoadoutRole role, CEReloadedSettings st)
        {
            if (!RoleAutoSaves(role)) return;
            List<Loadout> comp = CompFor(role);
            string name = EffectiveRoleName(role);
            if (comp.Count == 0 || name.Length == 0) return;
            if (!RoleNeedsCommit(role, comp)) return;

            // The ONE confirmation that moved here from the button path: if saving adopts an orphaned role of the
            // same name that still has colonists attached, they are re-equipped at the same moment. The
            // re-equipping is harmless; the surprising part is that it comes unannounced - hence warn rather than
            // forbid, and only when someone is actually attached. It can't pop up in a loop: AutoCommitRole runs
            // on events (drag, remove building block, change factor, rename), not per frame.
            int orphanPawns = CountOrphanRoleCarriers(role, name);
            if (orphanPawns > 0)
            {
                LoadoutRole localRole = role;
                List<Loadout> localComp = comp;
                string localName = name;
                CEReloadedSettings localSt = st;
                Find.WindowStack.Add(LoadoutUtility.CompactConfirmation(
                    "CER_GenerateLoadouts_RoleAdoptsOrphanConfirm".Translate(localName, orphanPawns.ToString()),
                    () =>
                    {
                        if (CommitRole(localRole, localComp, localName, localSt, out string _)) PersistRoles();
                    }));
                return;
            }
            if (CommitRole(role, comp, name, st, out string _)) PersistRoles();
        }

        private bool RoleNeedsCommit(LoadoutRole role, List<Loadout> comp)
        {
            // A pending name change IS work, even without anything dragged in - otherwise the button stays grey
            // and the rename can't be triggered from the UI at all. CommitRole now has its own,
            // contents-preserving path for it.
            if (role != null)
            {
                string pending = EffectiveRoleName(role);
                if (pending.Length > 0 && pending != (role.name ?? "").Trim()
                    && RoleUtility.RoleLoadoutOf(role) != null)
                    return true;
            }
            if (comp == null || comp.Count == 0) return false;
            // No more "res.Count == 0 -> false" (same cause as canAct above): a pure pack role has no merge rows
            // and therefore counted as "nothing to do".
            List<RoleMergeEntry> res = RoleUtility.Merge(role, comp).Where(r => r.scaledCount > 0).ToList();
            Loadout existing = RoleUtility.RoleLoadoutOf(role);
            if (existing == null) return true;
            // OwnSlots, NOT SlotMap(existing). That was the whole bug: `SlotMap` reads `Loadout.Slots`, i.e. own
            // AND inherited slots - for a role with an attached pack, the PACK CONTENTS are included. That was
            // compared against `res`, and there packs are explicitly NOT included (see CommitRole: they are
            // attached as a real reference, not melted in). Two different sets, so the comparison was permanently
            // "unequal" for EVERY role with a pack - for a pure pack role even 0 against n.
            // The visible effect: after "create/update all", "(exists - delta)" stayed on the row and the counter
            // stayed at (2), while the single button of the same role correctly reported "unchanged". CommitRole
            // compares before/after from the SAME source and was therefore right; only this counter compared
            // apples to oranges.
            // OwnSlots is exactly what CommitRole writes (Clear + one slot per res entry) - so counter and single
            // button answer the same question from the same source. Packs are still compared, just where they
            // actually live: as the parent list, below.
            Dictionary<string, int> oldMap = new Dictionary<string, int>();
            foreach (LoadoutSlot s in existing.OwnSlots ?? new List<LoadoutSlot>())
                oldMap[SlotKey(s.thingDef, s.genericDef)] = s.count;
            Dictionary<string, int> newMap = new Dictionary<string, int>();
            foreach (RoleMergeEntry e in res) newMap[SlotKey(e.thing, e.generic)] = e.scaledCount;
            if (oldMap.Count != newMap.Count) return true;
            foreach (var kv in newMap) if (!oldMap.TryGetValue(kv.Key, out int o) || o != kv.Value) return true;
            // Compare the PACKS too (a side effect of the same bug): if only the attached packs of an existing
            // role were changed, both slot maps were equal and the mod reported "unchanged" - even though
            // CommitRole does write the change. "Create/update all" therefore didn't count it.
            var oldPacks = new HashSet<int>(MultiParentUtility.GetExtraParents(existing).Select(l => l.UniqueID));
            var newPacks = new HashSet<int>(RoleUtility.PacksIn(comp).Select(l => l.UniqueID));
            if (!oldPacks.SetEquals(newPacks)) return true;
            return false;
        }

        // REMOVED: CreateAllRoles - the body of the "create/update all" button. It goes along with the button;
        // every role saves on its own as soon as something about it changes (AutoCommitRole). A bulk button
        // would have nothing left to do.

        // The GLOBAL role list lives in the mod settings, and RimWorld only writes those on its own when the
        // player closes the settings window - this tab never did. For a save-local role (everything "+ New role"
        // creates) the save carries the data anyway; for a global one, the name, factors and now also the
        // composition were lost on exit.
        // Once per confirmed action, not per frame: Write() serializes the whole settings file.
        private static void PersistRoles()
        {
            if (CEReloadedMod.settings != null) CEReloadedMod.settings.Write();
        }

        // Remembers how many loadouts there were at the last cleanup. If the number changes, one was deleted
        // (or added) somewhere - only then do the remembered rows need checking.
        private int roleCompCheckedCount = -1;

        private List<Loadout> CompFor(LoadoutRole role)
        {
            if (role == null) return new List<Loadout>();
            // Throw DELETED sources out of the remembered rows (with no weapon loadouts left, the roles still had
            // some assigned).
            // The earlier description was wrong: the row is built from the stored composition on FIRST access
            // and then held in the window. If the source templates are deleted while the window is open, the card
            // keeps showing building blocks with nothing behind them - and a click on one would have computed
            // with a dead object.
            // Not every frame, only when the number of loadouts has changed: the same latch that
            // RebuildLoadoutsByWeaponFrame uses.
            int loCount = LoadoutManager.Loadouts != null ? LoadoutManager.Loadouts.Count : 0;
            if (loCount != roleCompCheckedCount)
            {
                roleCompCheckedCount = loCount;
                foreach (var kv in roleComp)
                    kv.Value?.RemoveAll(l => l == null || LoadoutManager.Loadouts == null || !LoadoutManager.Loadouts.Contains(l));
            }
            if (!roleComp.TryGetValue(role, out List<Loadout> list))
            {
                list = new List<Loadout>();
                // RECOVERY on first access (LoadoutRole.composition - the long rationale is there). Without it
                // every existing role starts with an empty row, and the next update REPLACES its contents instead
                // of adding to them.
                // Deliberately only when the entry is CREATED: if someone removes all attachments by hand, they
                // don't get washed back in on the next frame - the entry then exists as an empty list, and this
                // branch no longer runs.
                // A source loadout that no longer exists (deleted or renamed) silently drops out.
                if (role.composition != null && role.composition.Count > 0)
                {
                    foreach (string label in role.composition)
                    {
                        if (string.IsNullOrEmpty(label)) continue;
                        Loadout src = LoadoutManager.Loadouts.FirstOrDefault(
                            l => l != null && !l.defaultLoadout && l.label == label);
                        // REBUILD LAZILY (factors were locked even on finished roles, with the hint text "Drop a
                        // loadout onto this role", although the role had long contained a weapon). Since the lazy
                        // rework, weapon templates are only created on demand and disappear when nobody carries
                        // them anymore - a role built from such a template then no longer found its source, the
                        // row stayed empty, and empty means locked. But the template can be recreated at any time;
                        // that's exactly what EnsureDefaultLoadout is for: a weapon template IS its weapon.
                        if (src == null) src = RecreateTemplateByLabel(label);
                        if (src != null && !list.Contains(src)) list.Add(src);
                    }
                }
                // SECOND fallback: the role has a loadout but no findable composition (created before it was
                // remembered, or via a path that never remembered it). Then the weapons of the role loadout are
                // the sources - every weapon has its template. Remembered once so the next open doesn't have to
                // search again.
                if (list.Count == 0)
                {
                    Loadout own = RoleUtility.RoleLoadoutOf(role);
                    var ownSlots = own?.OwnSlots;
                    if (ownSlots != null)
                        foreach (LoadoutSlot s in ownSlots)
                            if (s?.thingDef != null && LoadoutUtility.IsRealWeaponDef(s.thingDef))
                            {
                                Loadout t = EnsureDefaultLoadout(s.thingDef);
                                if (t != null && !list.Contains(t)) list.Add(t);
                            }
                    if (list.Count > 0) RememberComposition(role, list);
                }
                roleComp[role] = list;
            }
            return list;
        }

        // Rebuild a source template that doesn't currently exist from its label: a weapon template's label is
        // VirtualLoadoutLabel(weapon), so the weapon can be recovered from it.
        // Real weapons only; everything else (packs, foreign loadouts) stays gone - that's how it was before and
        // it's correct, because there is no template to rebuild for those.
        private static Loadout RecreateTemplateByLabel(string label)
        {
            if (string.IsNullOrEmpty(label)) return null;
            foreach (ThingDef d in DefDatabase<ThingDef>.AllDefsListForReading)
            {
                if (!LoadoutUtility.IsRealWeaponDef(d)) continue;
                if (LoadoutUtility.VirtualLoadoutLabel(d) != label) continue;
                return EnsureDefaultLoadout(d);
            }
            return null;
        }

        // By identity. This is the question "has this role ever been written", and it must not depend on what
        // the loadout happens to be called right now.
        private static bool RoleCreated(LoadoutRole role) => RoleUtility.RoleLoadoutOf(role) != null;

        // This save's local roles first, then global roles (null local list when no game is loaded). Local-
        // first matters for display order: DrawRoleCards' OrderBy(created) is a STABLE sort, so within the
        // "not yet created" tier a freshly Insert(0, ...)-ed local role only actually lands FIRST overall
        // if the whole local list precedes the global list going in - global-first (the old order) let
        // every never-committed global preset (Support, Breacher, ...) sit ahead of a just-created role
        // that also hadn't been committed yet, which read as "new role goes to the bottom".
        private static List<LoadoutRole> AllRoles(CEReloadedSettings st)
        {
            List<LoadoutRole> local = RoleUtility.LocalRoles();
            List<LoadoutRole> all = local != null ? new List<LoadoutRole>(local) : new List<LoadoutRole>();
            all.AddRange(st.roles);
            return all;
        }

        private void SetActiveRole(LoadoutRole role)
        {
            activeRoleObj = role;
            if (lastNamedRole != role)
            {
                lastNamedRole = role;
                roleNameEdit = role != null ? role.name : "";
            }
        }

            // Select an EXISTING role in the tab: if a pawn has a role, an "Edit Role" button that jumps into
            // the role editor replaces the inactive "Save as role".
            // Deliberately NOT via StartRoleDraftFromPawn: that creates a DRAFT from the colonist's parts - for a
            // colonist who already carries the role, that would be a copy instead of the role itself.
            // Looked up by name, because throughout the mod that is a role's identity (the loadout is called
            // "[Role] <name>"). If nothing is found, nothing happens - then the role was deleted while the window
            // was open, and a silent failure is better than an empty card.
        public bool FocusExistingRole(string roleName)
        {
            string want = (roleName ?? "").Trim();
            if (want.Length == 0) return false;
            CEReloadedSettings st = CEReloadedMod.settings;
            if (st == null) return false;
            foreach (LoadoutRole r in AllRoles(st))
            {
                if (r != null && string.Equals((r.name ?? "").Trim(), want, StringComparison.OrdinalIgnoreCase))
                {
                    RoleMode = true;
                    SetActiveRole(r);
                    return true;
                }
            }
            return false;
        }

        // Every "Save as Role" button used to bake+save a pawn's current combo instantly, with no review
        // step. They now call this instead: pre-seed Role Mode with a DRAFT role so the ALREADY-EXISTING
        // preview/factor/rename UI shows before anything is committed - the player still has to press
        // Create/Update themselves (CommitRole), same as building a role by hand.
        //
        // Decomposed into its actual PARTS (the real Weapon Loadout template, each attached Equipment Pack,
        // each attached sidearm, any personal ammo override) rather than adding pawn.GetLoadout() itself as
        // the single dragged source - for a personalized pawn that IS the hidden internal "[Pawn-Loadout]
        // <id>" anchor object, which used to leak into the player-facing composition chips as one opaque,
        // unlabeled item instead of the real, individually-removable pieces a player actually recognizes.
        public void StartRoleDraftFromPawn(Pawn pawn, string defaultName)
        {
            Loadout assigned = pawn?.GetLoadout();
            if (assigned == null) return;
            RoleMode = true;
            // Strip a leading "[Role] " if the pawn's OWN composed name already carries one (2026-08-01 fix)
            // - saving a fresh combo from a pawn who's already Role-assigned passes ComposedDisplayLabel's
            // unmodified "[Role] Rifleman" as defaultName, and CommitRole prepends its own RoleMarker on top
            // when this draft is later committed, producing a doubled "[Role] [Role] Rifleman" name.
            string cleanDefaultName = !string.IsNullOrEmpty(defaultName) && defaultName.StartsWith(RoleUtility.RoleMarker)
                ? defaultName.Substring(RoleUtility.RoleMarker.Length).Trim() : defaultName;
            LoadoutRole role = new LoadoutRole { name = string.IsNullOrEmpty(cleanDefaultName) ? "CER_GenerateLoadouts_NewRoleDefaultName".Translate() : cleanDefaultName };
            RoleUtility.LocalRoles()?.Insert(0, role);
            List<Loadout> comp = CompFor(role);

            Loadout template = AmmoAssignmentUtility.DisplayLoadout(assigned);
            if (template != null) comp.Add(template);
            comp.AddRange(AmmoAssignmentUtility.GetPawnPacks(pawn));
            comp.AddRange(SidearmAssignmentUtility.GetPawnSidearms(pawn));

            List<LoadoutSlot> extraAmmo = AmmoAssignmentUtility.ExtraAmmoSlots(pawn);
            if (extraAmmo.Count > 0)
            {
                // A throwaway, never-persisted Loadout - purely a carrier for the composition chip list and
                // RoleUtility.Merge's own IEnumerable<Loadout> input, same as RoleUtility.BuildRoleLoadout's
                // own "new Loadout()" before it's ever added to LoadoutManager.
                Loadout ammoWrap = new Loadout { label = "CER_GenerateLoadouts_PersonalAmmoLabel".Translate(), defaultLoadout = false };
                foreach (LoadoutSlot s in extraAmmo)
                {
                    if (s.thingDef != null) ammoWrap.AddSlot(new LoadoutSlot(s.thingDef, s.count));
                    else if (s.genericDef != null) ammoWrap.AddSlot(new LoadoutSlot(s.genericDef, s.count));
                }
                comp.Add(ammoWrap);
            }
            // Record the composition IMMEDIATELY (the role was still called '[S] Henry Big Boy +Base Supply', but
            // its row said 'drag a loadout here' - leaving a role whose name promises a weapon and a pack and
            // which contained nothing).
            //
            // Cause: this draft only filled the RUNTIME row (roleComp). role.composition stayed empty, and as soon
            // as CompFor rebuilds the row - which it does whenever the number of loadouts has changed - it
            // rebuilds it from exactly that empty composition. The card was then permanently empty, and the
            // factor fields greyed out.
            //
            // Deliberately only REMEMBER, don't create: a draft should explicitly only become a real role on a
            // button press (see the comment above). Remembered via the labels - the throwaway carrier for
            // personal ammo silently drops out because it doesn't exist in the loadout list; that is the
            // documented contract of CompFor.
            RememberComposition(role, comp);
            SetActiveRole(role);
        }

        public void DoWindowContents(Rect inRect)
        {
            // Since the lazy rework there is ONLY the role tab: the drawing body of the old weapon loadouts tab
            // (around 950 lines - filter row, weapon list with load/edit/assign buttons, footer) stayed here and
            // was unreachable - every caller sets RoleMode to true beforehand (Dialog_CEReloadedTabs draws the
            // role tab directly via DoRoleMode, Dialog_ArmColony.RolesStep sets RoleMode in its constructor).
            // The class itself lives on - for the roles and as the home of EnsureDefaultLoadout.
            RoleMode = true;
            DoRoleMode(inRect);
        }

        // Static core (lazy rework): needs no window state at all, so background creation
        // (EnsureDefaultLoadout) can run without an open window.
        // status: 0 = newly created, 1 = updated, 2 = unchanged.
        internal static Loadout CreateOrUpdateWeaponLoadout(ThingDef wDef, WeaponLoadoutSettings settings, out int status)
        {
            string loadoutName = string.IsNullOrEmpty(settings.loadoutName) ? LoadoutUtility.VirtualLoadoutLabel(wDef) : settings.loadoutName;

            Loadout existingLoadout = LoadoutManager.Loadouts.FirstOrDefault(l => l != null && !l.defaultLoadout && l.label == loadoutName);
            Loadout targetLoadout;
            List<LoadoutSlot> oldSlots = new List<LoadoutSlot>();

            if (existingLoadout != null)
            {
                targetLoadout = existingLoadout;
                // OwnSlots, not Slots: Slots is own+parent (a computed union once an Equipment Pack is
                // attached) - removing from that could try to remove slots that aren't actually in this
                // loadout's own list at all. OwnSlots is always this loadout's own live backing list,
                // parented or not.
                if (targetLoadout.OwnSlots != null)
                {
                    var slotsCopy = targetLoadout.OwnSlots.ToList();
                    foreach (var s in slotsCopy)
                    {
                        if (s.thingDef != null) oldSlots.Add(new LoadoutSlot(s.thingDef, s.count));
                        else if (s.genericDef != null) oldSlots.Add(new LoadoutSlot(s.genericDef, s.count));
                        targetLoadout.RemoveSlot(s);
                    }
                }
            }
            else
            {
                targetLoadout = new Loadout();
                targetLoadout.label = loadoutName;
                targetLoadout.defaultLoadout = false;
            }

            foreach (LoadoutSlot s in DefaultSlotsFor(wDef, settings)) targetLoadout.AddSlot(s);

            if (existingLoadout == null)
            {
                LoadoutManager.AddLoadout(targetLoadout);
                LoadoutUtility.MarkAsManaged(targetLoadout);
                status = 0;
            }
            else
            {
                // Compare OwnSlots, not Slots - this generate action only ever changes the weapon's
                // own content; an attached pack changing independently elsewhere shouldn't make
                // every weapon that uses it report as "updated" here.
                bool changed = false;
                if (oldSlots.Count != targetLoadout.OwnSlots.Count) changed = true;
                else
                {
                    foreach (var ns in targetLoadout.OwnSlots)
                    {
                        var os = oldSlots.FirstOrDefault(s => s.thingDef == ns.thingDef && s.genericDef == ns.genericDef);
                        if (os == null || os.count != ns.count) { changed = true; break; }
                    }
                }
                // targetLoadout IS existingLoadout in this branch - the new slots were already rebuilt in
                // place above. No Clear+re-add here: for non-parented loadouts CE's Slots property returns
                // the LIVE own-slot list, so clearing and then iterating it would wipe the loadout empty.
                status = changed ? 1 : 2;
            }
            return targetLoadout;
        }

        private void DrawScoreBar(Rect rect, float score, float maxScore, string tooltip, bool drawText = true)
        {
            LoadoutUtility.ScoreBar(rect, score, maxScore, null, drawText, tooltip);
        }
    }
}
