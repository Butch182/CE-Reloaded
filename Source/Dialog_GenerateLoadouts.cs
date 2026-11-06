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
        
        
        
        // Hoechster Waffen-Score, gemerkt gegen LoadoutUtility.WeaponScoreVersion - siehe die Fundstelle
        // in der Zeichenschleife. -1 heisst "noch nie gerechnet".
        private string sidearmMaxBulk = "5.0";
        // Mahlzeit und Medizin sind hier am 2026-08-14 ERSATZLOS ENTFALLEN. Es standen vier Felder und vier
        // internal-Eigenschaften hier, die die Mod-Einstellungen einlasen - und niemand hat sie mehr gelesen:
        // "vier Einstellungen ohne Wirkung". Verpflegung gehoert zum Equipment Pack, nicht zur
        // Waffenvorlage; die Einstellungen wirken jetzt dort (Dialog_GenerateEquipmentPack.
        // PickFromSettingOrAuto). Der Kommentar bleibt als Wegweiser stehen, damit die Verdrahtung nicht
        // aus Versehen ein drittes Mal entsteht.

        // --- Role Mode state (drag weapon loadouts onto a role to produce a scaled "[Role] ..." loadout) ---
        private bool roleMode;
        // Arm My Colony's "Use roles" option lands the player straight in Role Mode instead of Weapons
        // mode when its own generate step opens this same dialog - same view, just a different starting tab.
        internal bool RoleMode { get => roleMode; set => roleMode = value; }
        private LoadoutRole activeRoleObj;
        private LoadoutRole lastNamedRole;
        private string roleNameEdit = "";
        // Wurde am Namensfeld wirklich getippt? Ohne das wuerde jedes Verlassen des Feldes schreiben, auch
        // wenn nichts geaendert wurde (2026-08-17, siehe das Feld selbst).
        private bool roleNameEditDirty;
        private float roleNameEditAt;
        private readonly Dictionary<LoadoutRole, List<Loadout>> roleComp = new Dictionary<LoadoutRole, List<Loadout>>();
        private Loadout dragLoadout;
        // Ziehen eines WAFFEN-Bausteins (Lazy-Umbau): die Vorlage entsteht erst beim DROP auf eine
        // Rollenkarte, nicht beim Anfassen - sonst springt die Zeile noch waehrend des Klicks in die
        // Loadout-Sektion darueber (2026-09-02: die Zeile sprang nach oben ueber die Ueberschrift).
        private ThingDef dragWeaponDef;
        private Vector2 roleSourceScroll, roleListScroll, rolePreviewScroll;
        private string roleSourceSearch = "", roleCardSearch = "", equipmentPackSearch = "";
        // Vorlagen-Name je Waffe fuer die Bausteine-Liste im Rollen-Modus (Lazy-Umbau 2026-09-01) - einmal
        // gebaut, weil das Praefix aus den Mod-Einstellungen kommt und sich praktisch nie aendert.
        private Dictionary<ThingDef, string> roleWeaponLabelCache;
        // Vorgabe-Slots je Waffe fuer die Bausteine-Liste (2026-09-04: der Waffenansicht im Rollen-Editor
        // fehlten alle wichtigen Infos). Genau derselbe Zwischenspeicher wie
        // in Dialog_PickPacks.WeaponSlots - eine Vorgabe-Slotliste zu bauen ist zu teuer fuer jede Zeile
        // in jedem Bild, aendert sich aber nur mit den Mod-Einstellungen, also einmal je Fensterleben.
        // Die Masse/Sperrigkeits-Haelfte ist am selben Tag wieder entfallen, als die Spalte verschwand
        // (Masse/Sperrigkeit flog raus) - ein Zwischenspeicher, den niemand mehr liest, ist genau
        // die Sorte Rest, die spaeter jemand fuer benutzt haelt.
        private readonly Dictionary<ThingDef, List<LoadoutSlot>> roleWeaponSlotsCache = new Dictionary<ThingDef, List<LoadoutSlot>>();
        private Vector2 equipmentPackScroll;
        // ====== Eingabepuffer je ROLLE, nicht je ZEILENNUMMER (2026-09-04) ======
        //
        // Befund im Test: der Ammo-Faktor der Rolle 'Support' war von 2 auf 1 gesprungen und der von
        // 'New role' von 1 auf 2 - die Werte waren also um eine Zeile verrutscht, waehrend sich die
        // Reihenfolge der Liste aenderte: Zeile per Index statt per Identitaet angefasst. Genau das war es.
        //
        // Der Schluessel war "c" + i + "a" - die laufende Nummer der gezeichneten Karte. Die Liste sortiert
        // sich aber nach RoleSortTier um, und dieser Rang aendert sich in dem Moment, in dem eine Rolle
        // ihren ersten Baustein bekommt (Stufe 2 "leer" -> Stufe 0 "Entwurf"). Ein einziges Draufziehen
        // verschiebt also die halbe Liste. Danach steht in Puffer "c3a" noch der Text der Rolle, die eben
        // auf Platz 3 lag - und Widgets.TextFieldNumeric schreibt genau diesen Text als WERT in die Rolle,
        // die jetzt dort steht. Die Zahlen wandern eine Zeile weiter, ohne dass jemand etwas getippt hat.
        //
        // Schlimmer als die falsche Zahl ist die Folge daneben: das Kartenfeld ruft bei jeder erkannten
        // Aenderung AutoCommitRole - also speichert der Mod eine Rolle, die der Spieler nie angefasst hat,
        // und bei einer GLOBALEN Rolle landet das ueber PersistRoles in der Mod-Einstellungsdatei, wirkt
        // also in jedem kuenftigen Spiel.
        //
        // Jetzt haengt der Puffer am Rollen-OBJEKT (vier Felder je Rolle, Reihenfolge Am/Me/Fo/Gr).
        // Umsortieren kann daran nichts mehr verschieben, und eine geloeschte Rolle nimmt ihren Eintrag
        // beim naechsten Aufraeumen mit, weil niemand mehr danach fragt.
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
        // maxWidth (2026-08-09): die Leiste wuchs ungebremst nach rechts. Elf
        // Flags sind moeglich, das sind ueber 200px - jede Spalte im Mod ist schmaler, also lief eine Waffe
        // mit vielen Effekten in die Nachbarspalte. Statt an einer Stelle eine Konstante zu vergroessern
        // (was nur den EINEN beobachteten Fall deckt und beim naechsten Effekt-Flag wieder bricht) bricht die
        // Leiste jetzt selbst ab und setzt ein "+N", dessen Tooltip die uebrigen aufzaehlt. Die Breite ist
        // damit eine Obergrenze statt einer Hoffnung, egal wie viele Flags eine Waffe je bekommt.
        // Vorgabe unendlich = altes Verhalten fuer jeden Aufrufer ohne eigene Spaltenbreite.
        // tipLines wie bei DrawWeaponSpecialIcons (2026-09-02): sammelt der Aufrufer, entfallen die
        // Einzel-Tooltips je Symbol UND der Tooltip am "+N" - ALLE Zeilen (auch abgeschnittene) landen
        // in der Liste, der Aufrufer legt einen Sammel-Tooltip ueber die ganze Zelle.
        public static float DrawAmmoEffectIcons(float x, float y, SpecialAbility ab, ThingDef ammoDefForDetail = null, float maxWidth = float.MaxValue, List<string> tipLines = null)
        {
            float ix = x;
            ThingDef proj = ammoDefForDetail != null ? LoadoutUtility.GetAmmoProjectile(ammoDefForDetail) : null;
            List<string> overflow = new List<string>();
            void Draw(SpecialAbility flag, Texture2D tex, string tip)
            {
                string full = tip + LoadoutUtility.AbilityDetailSuffix(flag, proj);
                if (tipLines != null) tipLines.Add(full);
                // 19px fuer dieses Symbol plus 20px Reserve fuer ein moegliches "+N" dahinter - sonst passt
                // das letzte Symbol gerade noch und der Zaehler haengt wieder ueber der Kante.
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

        // Das "+N" am Ende einer abgeschnittenen Symbolleiste. Eigene Funktion, damit beide Leisten
        // (Munition und Waffe) gleich aussehen und denselben Tooltip-Aufbau benutzen.
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
            // tip aus, wenn der Aufrufer einen Sammel-Tooltip ueber die ganze Zelle legt (2026-09-02) -
            // zwei uebereinanderliegende Tooltip-Flaechen zeigen sonst beide Kaesten gestapelt.
            if (tip)
                TooltipHandler.TipRegion(r, "CER_GenerateLoadouts_MoreEffectsTip".Translate(hidden.Count.ToString())
                    + "\n" + string.Join("\n", hidden.ToArray()));
            return 20f;
        }

        // Draws a weapon's INTRINSIC special icons (no ammo badge) left-to-right at (x,y), mirroring the Special column
        // of the main list. Reused by the "produce missing weapons" popup so its rows match the loadout tab.
        // maxWidth wie bei DrawAmmoEffectIcons - siehe dort. Hier faellt der Ueberlauf weniger auf, weil der
        // Tooltip ohnehin ueber der ganzen Leiste liegt und alle Zeilen nennt; abgeschnitten wird trotzdem,
        // damit die Symbole nicht in die Nachbarspalte laufen.
        // tipLines (2026-09-02, Sammel-Tooltip wiederhergestellt): sammelt der Aufrufer
        // die Zeilen ein, registriert diese Funktion KEINEN eigenen Streifen-Tooltip - er legt selbst
        // einen ueber die ganze Zelle (intrinsisch + Munition in einem Kasten).
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
                // Der Tooltip bekommt die Zeile IMMER, auch fuer abgeschnittene Symbole - sonst waere die
                // Information weg statt nur das Bild.
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
            // "+N" hinter der Leiste, wenn abgeschnitten wurde - der Tooltip darunter nennt ohnehin alle
            // Zeilen, das Zeichen sagt nur, dass es mehr sind als sichtbar.
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
            // Mahlzeit/Medizin werden hier bewusst NICHT mehr eingelesen - siehe den Kommentar an den
            // entfallenen Feldern oben.
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
                    // researchedOnly: PickBest WAEHLT eine Waffe aus, ist also ein Vorschlag und keine
                    // Anzeige (2026-08-09).
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

        // static seit 2026-08-15: der Rumpf liest ausschliesslich Mod-Einstellungen und statische Helfer,
        // keinen Zustand dieses Fensters. Damit kann die Ueberlast-Vorschau im Loeser dieselbe Vorgabe
        // erfragen, ohne dass ein Dialog offen sein oder durch den Loeser gereicht werden muss.
        internal static WeaponLoadoutSettings BuildDefaultSettings(ThingDef w)
        {
            // Namensregel NUR aus VirtualLoadoutLabel (2026-09-01): vorher rechneten drei Stellen dieselbe
            // Praefix-Formel unabhaengig - jede Abweichung haette die "gibt es schon?"-Erkennung gebrochen,
            // an der Dedup, Fill-Gaps-Seeding und der Loeschpfad haengen.
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
        // WAS in einer erzeugten Waffen-Vorlage steht - Waffe plus die eingestellten Munitionsmengen.
        // Herausgezogen (2026-08-15), damit die Ueberlast-Warnung vor dem Uebernehmen eine Vorlage wiegen
        // kann, die es noch gar nicht gibt: der Loeser waehlt regelmaessig eine Waffe OHNE Vorlage
        // ("virtuell"), und genau dort ist die ganze Last neu und am ehesten zu schwer.
        // Bewusst als geteilte Funktion und nicht als zweite Rechnung: eine Warnung, die anders rechnet als
        // der Erzeuger baut, waere schlimmer als keine.
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

        // Masse und Sperrigkeit derselben Vorgabe, ohne irgendetwas anzulegen.
        public static void DefaultMassBulkFor(ThingDef wDef, out float mass, out float bulk)
        {
            mass = 0f; bulk = 0f;
            if (wDef == null) return;
            LoadoutUtility.SlotsMassBulk(DefaultSlotsFor(wDef, BuildDefaultSettings(wDef)), out mass, out bulk);
        }

        // Statisch seit 2026-09-01 (Lazy-Umbau): das ist DIE eine Stelle, an der eine Waffen-Vorlage im
        // Hintergrund entsteht. Vorher hing der Aufruf an parentWindow.LoadoutsDialog - in zwei Aufrufwegen
        // (Arm-Kette, Bereitschaftsanzeige) war das null, und ein virtueller Pick ging beim Uebernehmen
        // still verloren.
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
        // static: reicht nur an LoadoutUtility durch, kein Instanzzustand - BuildDefaultSettings
        // ist seit 2026-08-15 ebenfalls statisch und braucht es so.
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
            // Die Warnung "benenne [Role]-Loadouts nicht um" ist am 2026-08-17 entfallen. Sie war richtig,
            // solange die Erkennung am Etikett hing - jetzt haengt sie an einer eigenen Kennung, und ein
            // Umbenennen (auch in CEs eigenem Editor) kann die Verbindung nicht mehr zerreissen. Eine
            // Warnung, die nicht mehr stimmt, erzieht dazu, Warnungen zu ueberlesen.

            float top = 58f;
            // 210 -> 290 (2026-08-18): beide Listen etwas schmaeler, damit auch der Rollen-Rahmen rechts,
            // und die Vorschau wird groesser und zeigt mehr. Die Vorschau ist die Stelle, an der man
            // wirklich liest, was eine Rolle enthaelt,
            // und sie hat bisher haeufig "5 more below" gezeigt. Die 80 Punkte kommen von den drei
            // Kaesten darueber - die scrollen ohnehin alle drei, dort kostet es eine Zeile, hier bringt es
            // drei.
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
            // Beschriftungsbreite messen statt 96f fest (2026-08-26: auf Deutsch stand in
            // BEIDEN Kaesten nur der Wortanfang - der Umbruch der laengeren Uebersetzung lag ausserhalb des
            // 22f-Rects, zwei verschiedene Bereiche hiessen dadurch gleich). Das Suchfeld rueckt mit;
            // die Kappung bei 60% haelt es bedienbar, falls eine Sprache noch laenger wird.
            string srcHeaderTxt = "CER_GenerateLoadouts_YourLoadoutsHeader".Translate();
            float srcHeaderW = Mathf.Min(Text.CalcSize(srcHeaderTxt).x + 4f, header.width * 0.6f);
            Widgets.Label(new Rect(header.x, header.y, srcHeaderW, 22f), srcHeaderTxt.Truncate(srcHeaderW));
            Rect roleSourceFieldRect = new Rect(header.x + srcHeaderW + 4f, header.y - 2f, header.width - srcHeaderW - 4f - 24f, 22f);
            roleSourceSearch = Widgets.TextField(roleSourceFieldRect, roleSourceSearch);
            if (LoadoutUtility.ClearSearchButton(roleSourceFieldRect, header.y - 2f, roleSourceSearch)) roleSourceSearch = "";

            // Sources = your real loadouts (not the default one, and not existing role loadouts, Equipment
            // Packs - packs get their own source list below - or the hidden per-pawn "[Pawn-Loadout]"
            // anchors, which are internal bookkeeping never meant to be dragged/picked by a player).
            // Zusaetzlich zur Identitaetspruefung (IsPlainWeaponLoadout fragt IsRoleLoadout) auch der
            // MARKER - und das ist die einzige Stelle im Mod, an der das noch erlaubt ist (2026-08-18).
            // Begruendung: die Identitaet kann voruebergehend fehlen (ein Loadout, dessen Rolle gerade
            // zurueckgesetzt oder geloescht wurde, gehoert einen Moment lang zu niemandem), und in genau
            // diesem Moment stand es hier als ziehbare "Waffen-Vorlage" - mit dem Ergebnis, dass eine Rolle
            // in eine Rolle gelegt werden konnte. Der Marker taugt hier als Sicherheitsnetz, weil er nur
            // MEHR ausschliessen kann: er entscheidet nichts ueber Anzeige oder Verhalten, er haelt nur
            // etwas aus einer Liste heraus, das dort nie hingehoert. Die Wurzel ist trotzdem repariert
            // (RoleUtility.AdoptRoleLoadoutsByName am Zuruecksetzen-Knopf).
            List<Loadout> sources = LoadoutManager.Loadouts
                .Where(l => LoadoutUtility.IsPlainWeaponLoadout(l) && !RoleUtility.IsRoleLabel(l.label)
                    && (string.IsNullOrEmpty(roleSourceSearch) || (l.label != null && l.label.IndexOf(roleSourceSearch, StringComparison.OrdinalIgnoreCase) >= 0)))
                .ToList();

            // WAFFEN als Bausteine (Lazy-Umbau 2026-09-01): frueher speiste sich diese Liste NUR aus vorab
            // erzeugten Vorlagen - ohne Massenerzeugung waere sie leer und der Rollen-Baukasten tot. Jetzt
            // steht unter den Loadouts jede Waffe, zu der noch keine Standard-Vorlage existiert; beim
            // Anfassen (Ziehbeginn) entsteht die geteilte Vorlage unsichtbar ueber EnsureDefaultLoadout.
            // Label-Dedup gegen den Loadout-Bestand pro Bild (ein HashSet-Aufbau, gleiche Groessenordnung
            // wie die sources-Abfrage darueber); die Vorlagen-Namen je Waffe sind gecacht - das Praefix
            // kommt aus den Mod-Einstellungen und aendert sich praktisch nie (schlimmstenfalls steht eine
            // Waffe bis zum naechsten Fensteroeffnen doppelt neben ihrer Vorlage).
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

            // ===== Spalten wie im Waffen-Auswahlfenster (2026-09-04): der Waffenansicht im Rollen-Editor
            // fehlten alle wichtigen Infos - komplett ueberarbeitet nach dem Vorbild von "Choose weapon",
            // aber im Layout an den verfuegbaren Platz angepasst =====
            //
            // Vorher stand hier nur ein Name, bei Waffen zusaetzlich ein Symbol. Wer eine Rolle
            // zusammenbaut, entscheidet aber genau dasselbe wie im Auswahlfenster: taugt die Waffe etwas,
            // ist ueberhaupt eine da, und was kostet sie an Traglast. Das stand ausschliesslich im
            // Tooltip - also erst nach Zielen und Warten, und pro Zeile einzeln.
            //
            // NICHT die festen Spaltenbreiten von Dialog_PickPacks uebernommen: dieser Kasten ist rund
            // halb so breit wie jenes Fenster - das ist der "passe das layout an den platz an"-Teil.
            // Stattdessen faellt von rechts nach links weg, was nicht mehr passt, und der Name bekommt,
            // was uebrig bleibt (nie weniger als NameMinW). Die Reihenfolge des Wegfallens ist nach
            // Nutzen gewaehlt: der Inhaltsstreifen zuerst (er steht auch im Tooltip), dann
            // Masse/Sperrigkeit, dann Verfuegbarkeit, zuletzt die Wertung.
            const float SrcRowH = 30f;
            const float NameMinW = 140f;
            float rowW = header.width - 16f;                  // minus Bildlaufleiste
            float srcScoreW = 0f, srcSpecW = 0f, srcAvailW = 0f, srcContW = 0f;
            float restW = rowW - 52f;                         // Schloss + Symbol + Raender
            if (restW >= NameMinW + 68f) { srcScoreW = 68f; restW -= srcScoreW; }
            // Masse/Sperrigkeit ist RAUS und durch die Schadensspalte ersetzt (2026-09-04): statt Masse/Bulk
            // eine wichtigere Info, z.B. die Schadensart. Beim Rollenbauen zaehlt, WOGEGEN die Waffe
            // taugt - Traglast entscheidet man am Kolonisten, nicht am Baustein. Dieselben Symbole wie im
            // Auswahlfenster: intrinsische Waffeneffekte, dahinter die der Standard-Munition mit Plakette.
            if (restW >= NameMinW + 96f) { srcSpecW = 96f; restW -= srcSpecW; }
            if (restW >= NameMinW + 62f) { srcAvailW = 62f; restW -= srcAvailW; }
            if (restW >= NameMinW + 104f) { srcContW = 104f; restW -= srcContW; }
            float srcNameW = restW;

            // Spaltenkopf - drei Balken nebeneinander sind ohne Beschriftung nicht zuzuordnen. Gleiche
            // Bauform wie die uebrigen Kopfzeilen des Mods: Tiny, 55 % Deckkraft, reine Beschriftung.
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

            // ===== EINE Liste statt zwei Sektionen (2026-09-04: zugeteilte Waffen sprangen nach oben -
            // das ist weg) =====
            //
            // Vorher standen oben die vorhandenen Vorlagen und unter einer Zwischenueberschrift die
            // Waffen ohne Vorlage. Sobald eine Waffe irgendwo zugeteilt wurde, entstand ihre Vorlage -
            // und ihre Zeile sprang aus dem unteren Block in den oberen. Fuer den Spieler ist das
            // dieselbe Waffe an einer anderen Stelle, ohne dass er in dieser Liste etwas getan haette.
            //
            // Der Unterschied war ohnehin nur intern (Vorlage existiert schon / entsteht beim Ablegen);
            // beide Zeilen sehen gleich aus und tun beim Ziehen dasselbe. Also eine gemeinsame Liste,
            // alphabetisch nach dem ANGEZEIGTEN Namen - da steht eine Waffe immer an derselben Stelle,
            // egal ob sie gerade jemand traegt. Einzige Ausnahme bleibt das Sperren: Gesperrtes rutscht
            // ans Ende, aber das ist eine Handlung des Spielers genau in dieser Liste.
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
                // Leer wegen der SUCHE ist etwas anderes als leer, weil es noch nichts gibt (2026-08-19, im
                // Test aufgefallen): der Nachbarkasten "Your Equipment" unterscheidet die beiden Faelle
                // laengst, dieser hier behauptete bei jedem Suchbegriff ohne Treffer "No loadouts yet" -
                // also, es gebe gar keine Vorlagen, waehrend die Liste ohne Suchbegriff voll ist.
                Widgets.Label(new Rect(4f, 4f, viewRect.width - 8f, 40f), string.IsNullOrEmpty(roleSourceSearch)
                    ? "CER_GenerateLoadouts_NoLoadoutsYet".Translate()
                    : "CER_GenerateLoadouts_NoLoadoutsMatchSearch".Translate(roleSourceSearch));
                GUI.color = Color.white;
            }
            Widgets.EndScrollView();
        }

        // Die Schadensspalte einer Bausteine-Zeile: intrinsische Effekte der Waffe, dahinter die der
        // Standard-Munition MIT Munitions-Plakette - genau die Zellen aus Dialog_PickPacks.DrawDamageCell,
        // damit dieselbe Waffe hier und im Auswahlfenster nicht zwei verschiedene Bilder zeigt. EIN
        // Sammel-Tooltip ueber die ganze Zelle statt Einzeltooltips je Symbol.
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

        // Eine Zeile der Bausteine-Liste, Fassung "vorhandenes Loadout". Dieselben Zellen wie im
        // Auswahlfenster, und ausdruecklich dieselben Zeichner aus LoadoutUtility - eine zweite Rechnung
        // fuer dieselbe Frage ist die Fehlerklasse, die diesen Mod am haeufigsten trifft.
        private void DrawRoleSourceLoadoutRow(Rect row, Loadout lo, float nameW, float scoreW, float specW, float availW, float contW)
        {
            if (Mouse.IsOver(row)) Widgets.DrawHighlight(row);
            bool locked = LoadoutUtility.IsLoadoutDisabled(lo);
            Dialog_AssignLoadouts.AnalyzeLoadout(lo, out ThingDef main, out float score, out _, out _);

            // Schloss zuerst - der Ziehbereich beginnt dahinter, sonst frisst er den Klick darauf.
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
                    // TipWanted davor: BuildWeaponLocationTooltip laeuft ueber Karten und Kolonisten, und
                    // dieser Balken steht in JEDER Zeile - dieselbe Vorsicht wie im Auswahlfenster.
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

            // Der Ziehbereich endet vor den Zellen rechts: die haben eigene Tooltips (Wertung, Fundorte,
            // Inhalt), und ein Ziehstart darueber wuerde die verdecken.
            Rect dragArea = new Rect(row.x + 22f, row.y, 30f + nameW, row.height);
            Loadout tipLo = lo;
            TooltipHandler.TipRegion(dragArea, () => LoadoutUtility.LoadoutContentsTooltip(tipLo), tipLo.UniqueID ^ 0x5C0F);
            if (locked) return;   // gesperrt = kein Baustein, so wie es im Auswahlfenster nicht waehlbar ist
            if (Mouse.IsOver(dragArea) && Event.current.type == EventType.MouseDown && Event.current.button == 0)
            {
                dragLoadout = lo;
                Event.current.Use();
            }
        }

        // Dieselbe Zeile, Fassung "Waffe ohne Vorlage". Die Zahlen kommen aus der Vorgabe-Slotliste
        // (DefaultSlotsFor), also aus genau dem, was beim Ziehen entstuende - nicht aus der nackten Waffe.
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
                // NUR die Waffe merken - die Vorlage entsteht erst beim Drop auf eine Rollenkarte
                // (2026-09-02: Materialisieren beim Anfassen liess die Zeile sofort in die
                // Loadout-Sektion darueber springen).
                dragWeaponDef = tipW;
                Event.current.Use();
            }
        }

        // Derselbe Schalter wie im Auswahlfenster (Dialog_PickPacks.DrawWeaponRow), damit ein Schloss
        // ueberall gleich aussieht und gleich reagiert: schwach sichtbar, bis die Maus auf der Zeile
        // steht oder es zu ist.
        private static void DrawSourceLockToggle(Rect r, bool locked, Action toggle, bool rowHovered, bool isLoadout)
        {
            GUI.color = new Color(1f, 1f, 1f, locked || rowHovered ? 1f : 0.3f);
            if (Widgets.ButtonImage(r, locked ? Dialog_PickPacks.TexDoNotUseOn : Dialog_PickPacks.TexDoNotUseOff)) toggle();
            GUI.color = Color.white;
            // Vorlage und Waffe bekommen ihren eigenen Wortlaut - derselbe wie im Auswahlfenster, damit
            // dasselbe Schloss nicht an zwei Stellen zwei verschiedene Dinge zu versprechen scheint.
            string key = isLoadout
                ? (locked ? "CER_PickPacks_UnlockLoadoutTip" : "CER_PickPacks_LockLoadoutTip")
                : (locked ? "CER_PickPacks_UnlockTip" : "CER_PickPacks_LockTip");
            TooltipHandler.TipRegion(r, key.Translate());
        }

        // Bis zu vier Symbole plus "+N" - die Kurzfassung des Inhalts; die Langfassung steht im Tooltip
        // des Namens. Bewusst weniger Symbole als im Auswahlfenster (dort sechs): die Spalte ist hier
        // schmaler, und vier passen ohne Kappung.
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

        // Vorgabe-Slots einer Waffe, einmal gebaut und gemerkt. Genau dieselbe Bauform wie
        // Dialog_PickPacks.WeaponSlots; eine Vorgabeliste je Zeile je Bild waere bei dreistelliger
        // Waffenzahl nicht bezahlbar.
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
            // Breite gemessen statt 140f fest - dieselbe Sache wie im Waffen-Kasten darueber (2026-08-26).
            string packHeaderTxt = "CER_GenerateLoadouts_YourEquipmentPacksHeader".Translate();
            float packHeaderW = Mathf.Min(Text.CalcSize(packHeaderTxt).x + 4f, header.width * 0.6f);
            Widgets.Label(new Rect(header.x, header.y, packHeaderW, 22f), packHeaderTxt.Truncate(packHeaderW));
            // Suchfeld wie in der Liste darueber (2026-08-18): "Your equipment packs" bekommt eine
            // Type-ahead-Suche genau wie "Your weapons". Dieselbe Bauform, dasselbe Loeschkreuz -
            // und nebenbei sind damit auch die beiden LISTEN gleich hoch: die Waffenliste hat 26 Punkte an
            // ihren Kopf verloren, die Packliste bisher nicht, also war ihre Liste um genau diese Zeile
            // hoeher, obwohl die Kaesten selbst gleich gross sind. Genau das war der erste Befund.
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
                // Inhalt im Tooltip, gleiche Begruendung wie in der Loadout-Liste darueber. Bewusst auf der
                // ZIEHFLAECHE und nicht auf der ganzen Zeile: der Loeschknopf rechts hat seinen eigenen
                // Tooltip, und der darf nicht vom Inhalt ueberdeckt werden.
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
                // Die neue Karte wird SOFORT die ausgewaehlte (2026-08-15, im Test gefunden).
                // Vorher blieb die vorher ausgewaehlte Rolle aktiv, und das Namensfeld unten gehoert der
                // ausgewaehlten - der getippte Name landete also auf einer FREMDEN Rolle: man tippte den
                // Namen der neuen Rolle, und eine andere Karte hiess ploetzlich anders. Eine stille
                // Umbenennung der falschen Zeile ist der schlimmste Ausgang dieses Knopfes.
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
                        // Der ganze Vorgang steht seit 2026-09-04 in RoleUtility.ResetGlobalRoles - samt
                        // dem Wiederzuordnen der gleichnamigen Loadouts (2026-08-18) UND dem Loeschen der
                        // Loadouts, die danach zu keiner Rolle mehr gehoeren. Zweiter Knopf derselben Art
                        // sitzt in den Mod-Einstellungen und ruft jetzt dieselbe Zeile.
                        RoleUtility.ResetGlobalRoles(st);
                        // Nur die Reihen der WEGGEWORFENEN Rollen vergessen - die spielstandlokalen Rollen
                        // ueberleben das Zuruecksetzen, und mit ihnen ihre gerade zusammengezogene Reihe.
                        var alive = new HashSet<LoadoutRole>(RoleUtility.AllKnownRoles());
                        foreach (LoadoutRole gone in roleComp.Keys.Where(r => !alive.Contains(r)).ToList())
                            roleComp.Remove(gone);
                        if (activeRoleObj != null && !alive.Contains(activeRoleObj)) { activeRoleObj = null; lastNamedRole = null; }
                        PersistRoles();
                    }, true));
            TooltipHandler.TipRegion(resetRect, "CER_GenerateLoadouts_ResetRolesTip".Translate());

            // Der Rueckweg fuer geloeschte Vorgaberollen (2026-09-04) - steht nur da, wenn wirklich eine
            // fehlt, und traegt die Zahl. Ohne ihn war "Zuruecksetzen" der einzige Weg zurueck, und der
            // wirft jede selbst gebaute Rolle mit weg. Begruendung in RoleUtility.RestoreMissingPresetRoles.
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

            // Wer hat welche Rolle gerade (2026-08-25): anzeigen, wie vielen Pawns die Rolle aktuell
            // zugeordnet ist - EINMAL je Zeichendurchgang fuer alle
            // Karten, nicht je Karte ueber alle Kolonisten. DisplayLoadout schaut durch den persoenlichen
            // Anker auf die eigentliche Vorlage, so wie jede andere Zaehlung im Mod.
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
            // Karte waechst mit der Zahl der Anhaenge (2026-08-14, im Test aufgefallen): die Chips
            // zeichneten in EINER Zeile ohne Umbruch bei fester Kartenhoehe. Ab etwa vier Anhaengen lagen
            // die letzten komplett ausserhalb des Kastens - samt ihrer [x]-Knoepfe, die Packs waren also
            // nicht mehr zu entfernen, und die ganze Rolle musste neu gebaut werden.
            // Struktureller Fix statt groesserer Konstante: die Kette Kartenhoehe -> Chip-Zeile
            // ist die Ursache, nicht die konkrete Zahl 80.
            const float chipRowH = 24f;
            const float cardBaseH = 80f;
            // Platz fuer das Bild der Hauptwaffe im gekennzeichneten Chip - siehe MainOf weiter unten.
            const float mainIconW = 22f;
            float cardW = outRect.width - 16f - 4f;
            float chipAreaW = cardW - 12f;

            // Der angezeigte Name der Karte - beim Umbenennen der GETIPPTE, sonst der gespeicherte. Hier
            // oben, weil ihn ausser der Beschriftung auch die Hauptwaffen-Kuer braucht und die beiden nie
            // verschiedene Namen sehen duerfen.
            string CardNameOf(LoadoutRole r)
            {
                if (r == activeRoleObj && !string.IsNullOrWhiteSpace(roleNameEdit)) return roleNameEdit.Trim();
                return r?.name;
            }
            // Einmal je Zeichendurchgang statt einmal je Abfrage: Hoehenrechnung und Zeichenschleife fragen
            // dasselbe zweimal, und die Kuer liest die Slots jeder Zusammenstellungs-Zeile.
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
            // Breite und Beschriftung eines Chips an EINER Stelle: die Hoehenrechnung und das Zeichnen
            // hatten die Formel bisher doppelt, mit einem Kommentar daneben, dass sie nicht auseinander-
            // laufen duerfen. Mit dem Bild der Hauptwaffe waeren es drei Zahlen gewesen, die zusammenpassen
            // muessen (die Kette reparieren, nicht die naechste Konstante drehen).
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
                // Eine Karte, die noch NIE erzeugt wurde und auf der nichts liegt, verschwindet ohne
                // Rueckfrage (2026-08-15). Die Rueckfrage schuetzt hier nichts: es gibt kein Loadout dazu,
                // also auch keinen Kolonisten, der daran haengen koennte - sie kostet nur einen zweiten
                // Klick fuer eine leere Platzhalterkarte, die man versehentlich angelegt hat.
                // Anlass: im Test entfernte das x an einer frisch angelegten, leeren 'New role'-Karte sie
                // nicht. Beweisen laesst sich das aus dem Code NICHT - der Entfernungspfad
                // unten ist korrekt, und die Fernsteuerung im Test hat mit Bestaetigungsfenstern
                // nachweislich Probleme. Deshalb hier keine erfundene
                // Ursache, sondern der Griff, der beide Erklaerungen erledigt: kein Fenster, kein Problem.
                // Fuer eine erzeugte Rolle bleibt die Rueckfrage von 2026-08-01 unveraendert.
                // ...UND jede GLOBALE Vorgaberolle (2026-09-06, Nachtest: das x an der
                // leeren Karte '[G] Marksman' hat sie OHNE Rueckfrage aus der globalen Bibliothek entfernt).
                // Die Ausnahme von 2026-08-15 gilt fuer eine versehentlich angelegte, leere LOKALE Karte -
                // eine leere Vorgabe ist kein Versehen, und ihr Verlust trifft jede Kolonie. Genau dafuer gibt
                // es den Rueckfragetext mit dem Hinweis auf 'Vorlagen zurueck' - der lief hier nie.
                bool needsConfirm = created || CompFor(role).Count > 0 || (!isLocal && RoleUtility.IsPresetRoleName(role.name));
                Action removeRole = () =>
                {
                    // Das "[Role] <Name>"-Loadout GEHT MIT (2026-09-04, zwei Befunde mit derselben
                    // Wurzel: eine schon geloeschte Rolle wurde in Assign Loadouts weiter angezeigt,
                    // und die Geister-Rollen nach Loeschen + Zuruecksetzen).
                    //
                    // Bis hierher entfernte dieser Knopf nur den Buchhaltungseintrag - mit der Begruendung,
                    // ein Loeschen des Loadouts koennte einen lebenden Verweis haengen lassen. Das stimmte,
                    // als es LoadoutDeletionUtility noch nicht gab; seitdem ist genau das dort erledigt
                    // (zugewiesene Kolonisten fallen sauber auf "keine Vorlage", Anker mit eigenem Inhalt
                    // werden umgehaengt statt geloescht, alle Nebenbuecher vergessen die ID).
                    //
                    // Was die alte Fassung dagegen hinterliess, war schlimmer als ein haengender Verweis:
                    // ein Loadout OHNE Besitzer. Ohne Rolle dahinter ist es fuer jede Identitaetspruefung
                    // eine gewoehnliche Vorlage - es wurde in der Zuweisung selbst unter "Keine Rollen"
                    // weiter verteilt, und zwar unter seinem rohen Etikett "[Role] Assault", weil der
                    // Marker nur bei einer ERKANNTEN Rolle abgestreift wird. Und beim naechsten
                    // "Zuruecksetzen" adoptierte die frische Vorgaberolle gleichen Namens es wieder
                    // (RoleUtility.AdoptRoleLoadoutsByName) - dann stand die geloeschte Rolle als "erzeugt"
                    // samt Waffeninhalt da, aber mit leerer Bausteinreihe.
                    Loadout doomed = RoleUtility.RoleLoadoutOf(roleToDelete);
                    if (!stCapture.roles.Remove(roleToDelete)) localList?.Remove(roleToDelete);
                    roleComp.Remove(roleToDelete);
                    if (activeRoleObj == roleToDelete) { activeRoleObj = null; lastNamedRole = null; }
                    if (doomed != null) LoadoutDeletionUtility.Delete(doomed);
                };
                if (Widgets.ButtonText(delCard, "CER_GenerateLoadouts_RemoveButton".Translate()))
                {
                    if (needsConfirm)
                        // Bei einer der sieben eingebauten Vorlagen sagt die Rueckfrage AUCH, dass sie aus
                        // der globalen Bibliothek verschwindet und wo sie wieder herkommt (2026-09-04): im
                        // Test wurde das x als "rueckgaengig" gelesen, und der alte Text hat dem nicht
                        // widersprochen - er sprach nur von "der Rolle und dem Loadout dahinter".
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
                // Der GETIPPTE Name, solange diese Rolle die aktive ist (2026-08-14, im Test
                // aufgefallen: der Name wurde zweimal eingegeben, weil die Karte weiter "New role" zeigte -
                // und bei mehreren neuen Rollen untereinander stehen sonst lauter gleiche Zeilen).
                // NUR die Anzeige, `role.name` bleibt unangetastet: CommitRole sucht das schon angelegte
                // Loadout ueber den ALTEN Namen ("[Role] <role.name>"), um es beim Umbenennen zu behalten
                // statt ein zweites daneben zu legen. Wuerde der Puffer hier live in role.name geschrieben,
                // wuerde genau diese Suche ins Leere laufen - aus dem Umbenennen wuerde ein Duplikat, und
                // jeder Kolonist an der alten Rolle bliebe an der alten haengen.
                // Nur bei nicht-leerer Eingabe, damit ein geleertes Feld nicht eine namenlose Karte zeigt.
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
                    // Der Erklaertext haengt auch an der BESCHRIFTUNG (2026-09-07: nirgends stand, wofuer die
                    // vier stehen, und an den Beschriftungen selbst kam kein Erklaertext). Zwei Buchstaben
                    // ohne Erklaerung sind ein Raetsel, und
                    // das Feld daneben erklaerte im gesperrten Zustand nur die Sperre, nicht den Faktor.
                    TooltipHandler.TipRegion(lr, tip);
                    Rect fr = new Rect(fx + 26f, card.y + 25f, 38f, 22f);
                    if (factorsEditable)
                    {
                        float beforeFactor = g();
                        CardFactorField(fr, role, slot, g, s);
                        // Ein geaenderter Faktor aendert den Inhalt der Rolle genauso wie ein Chip - also
                        // wird er genauso sofort geschrieben (2026-08-17). Nur bei echter Aenderung, sonst
                        // liefe das pro Bild.
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
                        // Erst was der Faktor bedeutet, dann warum er gesperrt ist - nicht nur die Sperre.
                        TooltipHandler.TipRegion(fr, tip + "\n\n" + "CER_GenerateLoadouts_FactorLockedTip".Translate());
                    }
                    fx += 70f;
                }
                FField("CER_GenerateLoadouts_FactorAmLabel".Translate(), 0, () => role.ammoFactor, v => role.ammoFactor = v, "CER_GenerateLoadouts_AmmoFactorTip".Translate());
                FField("CER_GenerateLoadouts_FactorMeLabel".Translate(), 1, () => role.medicineFactor, v => role.medicineFactor = v, "CER_GenerateLoadouts_MedicineFactorTip".Translate());
                FField("CER_GenerateLoadouts_FactorFoLabel".Translate(), 2, () => role.foodFactor, v => role.foodFactor = v, "CER_GenerateLoadouts_FoodFactorTip".Translate());
                FField("CER_GenerateLoadouts_FactorGrLabel".Translate(), 3, () => role.grenadeFactor, v => role.grenadeFactor = v, "CER_GenerateLoadouts_GrenadeFactorTip".Translate());

                // Die Zahl der Kolonisten auf dieser Rolle, rechts in der Faktor-Zeile (siehe pawnsByLoadout
                // oben): dort stand bisher nichts, und die Kopfzeile ist mit Name, Zustand und zwei Knoepfen
                // voll. Nur fuer angelegte Rollen - eine Rolle ohne Loadout kann niemand haben. Die Namen
                // stehen im Mouseover.
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
                    // ERSTELLTE Rolle ohne gespeicherte Zusammensetzung sagt, was sie WIRKLICH enthaelt
                    // (2026-08-23, im Test aufgefallen: die Zeile einer erstellten Rolle zeigte nur
                    // "drag a loadout here" und ausgegraute Faktorfelder - dass ein Gewehr drinsteckt,
                    // erfuhr man erst ueber die Vorschau darunter oder im Waffenmenue der Zuweisung).
                    // Der Zustand selbst ist richtig und laesst sich nicht wegprogrammieren: eine Rolle aus
                    // einer frueheren Spielsitzung hat keine gespeicherte Reihe von Bausteinen, und die laesst
                    // sich auch nicht rekonstruieren - siehe die Weiche an RoleAutoSaves. Falsch war nur,
                    // dass die Zeile daraus "hier ist nichts" gemacht hat. Sie liest den Inhalt jetzt aus
                    // dem gespeicherten Rollen-Loadout selbst, ueber dieselbe Funktion, mit der auch der
                    // Assign-Reiter die Hauptwaffe einer Vorlage bestimmt - nicht ueber eine zweite eigene.
                    Loadout savedRole = created ? RoleUtility.RoleLoadoutOf(role) : null;
                    ThingDef savedMain = null;
                    if (savedRole != null) Dialog_AssignLoadouts.AnalyzeLoadout(savedRole, out savedMain, out _, out _, out _);
                    GUI.color = new Color(1f, 1f, 1f, 0.4f);
                    Rect hintRect = new Rect(chipX, chipY, card.width - 12f, 20f);
                    Widgets.Label(hintRect, savedMain != null
                        ? "CER_GenerateLoadouts_CreatedRoleContentHintFmt".Translate(savedMain.LabelCap)
                        : "CER_GenerateLoadouts_DragLoadoutHerePlaceholder".Translate());
                    GUI.color = Color.white;
                    // Der Erklaertext fehlte an dieser Zeile ganz - auch das war Teil des Befunds.
                    TooltipHandler.TipRegion(hintRect, savedMain != null
                        ? "CER_GenerateLoadouts_CreatedRoleContentTip".Translate()
                        : "CER_GenerateLoadouts_DragLoadoutHereTip".Translate());
                }
                else
                {
                    int removeIdx = -1;
                    ThingDef roleMainW = MainOf(role);
                    // Haengt an der ROLLE, nicht am einzelnen Baustein - deshalb einmal vor der Schleife.
                    // NUR fuer ANGELEGTE Rollen (2026-08-30: die Rolle war noch nicht einmal erzeugt,
                    // geschweige denn einem Pawn zugewiesen - und trotzdem war das x gesperrt, mit dem
                    // Text ueber leer ausgehende Kolonisten). Die Sperre schuetzt das GESPEICHERTE Rollen-Loadout
                    // davor, geleert zu werden; eine Vorlage, die nie geschrieben wurde, hat nichts zu schuetzen -
                    // ihr letzter Baustein geht weg wie jeder andere, die Karte sagt dann 'drag a loadout here'.
                    bool blockedRemove = comp.Count <= 1 && created;
                    for (int c = 0; c < comp.Count; c++)
                    {
                        // Strip the "[Pack:Category] " marker for display - an Equipment Pack dragged in
                        // reads as "Standard AP", not the raw internal label.
                        string t = ChipText(comp[c]);
                        bool isMain = RoleUtility.CompHasDef(comp[c], roleMainW);
                        float w = ChipWidth(comp[c], isMain);
                        // Umbruch mit derselben Rechnung wie ChipRows oben - laufen die beiden auseinander,
                        // passt die reservierte Kartenhoehe nicht zu dem, was wirklich gezeichnet wird.
                        if (chipX > card.x + 6f && chipX + w > card.x + 6f + chipAreaW)
                        {
                            chipX = card.x + 6f;
                            chipY += chipRowH;
                        }
                        Rect chip = new Rect(chipX, chipY, w, 20f);
                        // Der Chip, der die HAUPTWAFFE liefert, hebt sich ab und traegt ihr Bild (2026-08-14,
                        // Variante a): eine Rolle darf mehrere Waffen buendeln, sie sagte bisher nur
                        // nicht, welche davon die Hauptwaffe wird - die uebrigen lesen sich damit als
                        // Zweitwaffen. Welche es ist, entscheidet RoleUtility.MainWeaponOf und damit dieselbe
                        // Kuer, die spaeter auf dem fertigen Rollen-Loadout laeuft.
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
                        // Hinter TipWanted, weil der Text jetzt zusammengesetzt wird und die Karte in einer
                        // langen Rollenliste sonst pro Bild einen Tooltip je Chip baut, den niemand sieht.
                        if (LoadoutUtility.TipWanted(chipTextRect))
                            TooltipHandler.TipRegion(chipTextRect, isMain
                                ? ("CER_GenerateLoadouts_RoleMainWeaponTip".Translate(roleMainW.LabelCap).ToString()
                                   + "\n\n" + "CER_GenerateLoadouts_RoleComponentSnapshotTip".Translate().ToString())
                                : "CER_GenerateLoadouts_RoleComponentSnapshotTip".Translate().ToString());
                        Rect chipXRect = new Rect(chip.xMax - 20f, chip.y, 20f, 20f);
                        if (blockedRemove)
                        {
                            // SICHTBAR gesperrt statt stillschweigend abgelehnt (2026-08-18, im Test als toter
                            // Knopf aufgefallen: normaler Klick, 350 ms, 700 ms - der Baustein blieb
                            // stehen). Der Klick kam an, er lief nur in die Ablehnung unten und hinterliess
                            // nichts als eine Meldung am oberen Bildrand - nicht von
                            // einem kaputten Knopf zu unterscheiden. Gesperrt heisst
                            // ausgegraut. TintedButton statt GUI.enabled, weil RimWorlds Knopfgrafik ueber
                            // GUI.DrawTexture laeuft und GUI.enabled dort nachweislich nichts zeichnet
                            // (siehe LoadoutUtility.TintedButton).
                            LoadoutUtility.TintedButton(chipXRect, null, disabled: true);
                            GUI.color = new Color(1f, 1f, 1f, 0.45f);
                            Text.Anchor = TextAnchor.MiddleCenter;
                            Widgets.Label(chipXRect, "CER_GenerateLoadouts_RemoveButton".Translate());
                            Text.Anchor = TextAnchor.UpperLeft;
                            GUI.color = Color.white;
                            // Den Satz 'jeder Kolonist auf dieser Rolle stuende leer da' nur, wenn jemand darauf ist.
                            if (LoadoutUtility.TipWanted(chipXRect))
                                TooltipHandler.TipRegion(chipXRect, OnRole(role).Count > 0
                                    ? "CER_GenerateLoadouts_RemoveLastChipBlockedTip".Translate(CardNameOf(role))
                                    : "CER_GenerateLoadouts_RemoveLastChipBlockedTipNoColonists".Translate(CardNameOf(role)));
                        }
                        else if (Widgets.ButtonText(chipXRect, "CER_GenerateLoadouts_RemoveButton".Translate())) removeIdx = c;
                        chipX = chip.xMax + 4f;
                    }
                    // Entfernen schreibt sofort (2026-08-17) - siehe RoleAutoSaves.
                    //
                    // DEN LETZTEN Baustein wegzunehmen wird ABGELEHNT (2026-08-18, im Test gefunden:
                    // nach Fenster zu und auf war der Baustein WIEDER DA, zweimal reproduziert).
                    // Ursache war die Aenderung vom Vortag: RoleNeedsCommit sagt bei leerer Reihe
                    // "nichts zu tun", also lief kein Commit, also blieb role.composition unveraendert - und
                    // beim naechsten Oeffnen baute CompFor die Reihe aus genau dieser Zusammensetzung wieder
                    // auf. Der Chip kam zurueck, weil das Entfernen nie irgendwo ankam.
                    // Es einfach doch zu schreiben waere falsch: CommitRole lehnt eine Aktualisierung, die
                    // eine gefuellte Rolle LEEREN wuerde, seit dem 16.08. ausdruecklich ab - genau der
                    // Schaden, um den sich dieser ganze Punkt dreht. Die ehrliche Antwort ist deshalb, den
                    // Griff abzulehnen, statt ihn stillschweigend zurueckzunehmen: eine Rolle ohne Bausteine
                    // gibt es nicht, wer sie los sein will, loescht sie.
                    // Die Ablehnung steht seit dem 18.08. AM KNOPF (blockedRemove oben), nicht mehr hier:
                    // hier war sie unsichtbar und las sich als Defekt. Hier ankommen kann deshalb nur noch
                    // ein erlaubtes Entfernen.
                    if (removeIdx >= 0)
                    {
                        comp.RemoveAt(removeIdx);
                        // Leert das Entfernen eine NICHT angelegte Vorlage, wird ihre gemerkte Zusammensetzung
                        // gleich mit geleert - AutoCommitRole schreibt bei leerer Reihe nichts, und CompFor
                        // baute die Reihe beim naechsten Oeffnen sonst aus genau dieser Vorlage wieder auf
                        // (derselbe Mechanismus wie beim Punkt vom 18.08.). Ein Loadout wird dabei nicht
                        // geleert, es gibt keins.
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
                    // Ein gezogener WAFFEN-Baustein materialisiert erst JETZT (2026-09-02) -
                    // ab hier identisch zum Zug einer existierenden Vorlage.
                    Loadout dropLo = dragLoadout != null ? dragLoadout : EnsureDefaultLoadout(dragWeaponDef);
                    if (dropLo != null && !comp.Contains(dropLo)) comp.Add(dropLo);
                    SetActiveRole(role);
                    Event.current.Use();
                    dragLoadout = null;
                    dragWeaponDef = null;
                    // Und Draufziehen ebenso - das ist der Handgriff, um den es ging.
                    AutoCommitRole(role, st);
                }

                ry += cardH + 6f;
            }
            // Dritter Kasten mit Suchfeld, dritte Leermeldung - hier gab es bisher GAR KEINE (
            // 2026-08-19): tippt man einen Suchbegriff, der auf keine Rolle passt, blieb der Kasten
            // einfach leer, und man sieht der Anzeige nicht an, ob die Suche schuld ist oder ob es
            // wirklich keine Rollen mehr gibt. Rollen ohne Suche kann es nicht geben (die eingebauten
            // sind immer da), deshalb nur der Suchfall.
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
                // Erst leeren, dann uebernehmen (2026-08-15): Clone() bringt seit der Rueckgewinnung die
                // gespeicherte Zusammensetzung mit, und CompFor fuellt die Reihe daraus schon beim ersten
                // Zugriff. Ein blosses AddRange haette danach jede Quelle doppelt in der Kopie stehen.
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
            // Ueber die Identitaet (2026-08-17) - nicht mehr ueber das Etikett. Eine in CEs eigenem Editor
            // umbenannte Rolle wird damit weiterhin als "existiert schon" erkannt, statt als neu zu gelten
            // und beim naechsten Speichern ein zweites Loadout danebenzustellen.
            Loadout existing = RoleUtility.RoleLoadoutOf(role);

            // Header: editable final name + "(exists - delta)" hint.
            Widgets.Label(new Rect(inner.x, inner.y + 2f, 56f, 24f), "CER_GenerateLoadouts_PreviewLabel".Translate());
            Widgets.Label(new Rect(inner.x + 60f, inner.y + 2f, 46f, 24f), "CER_GenerateLoadouts_RoleTagLabel".Translate());
            // Namensfeld: schreibt beim VERLASSEN, nicht pro Tastendruck (2026-08-17).
            //
            // Der Plan im Punkt sagte "pro Tastendruck schreiben, wie bei den Packs". Beim Bauen ist mir
            // aufgefallen, dass das hier etwas anderes bedeutet als dort: der Name einer Rolle IST das
            // Etikett des geteilten Loadouts, an dem Kolonisten haengen. Pro Tastendruck zu schreiben hiesse,
            // beim Tippen von "Medic" nacheinander die Rollen "M", "Me", "Med" anzulegen bzw. umzubenennen -
            // und jeder Kolonist darauf wandert mit. Ein Pack hat dieses Problem nicht, weil an ihm niemand
            // haengt.
            // Deshalb: der Inhalt (Chips, Faktoren) schreibt sofort, der NAME beim Verlassen des Feldes.
            // Das ist die einzige bewusste Abweichung von seinem Auftrag, und sie geht in die Richtung, in
            // der ein Fehler nicht wehtut.
            string beforeName = roleNameEdit;
            roleNameEdit = Widgets.TextField(new Rect(inner.x + 108f, inner.y, 180f, 24f), roleNameEdit);
            // NACH EINER KURZEN RUHEPAUSE statt beim Fokusverlust (2026-08-18, im Test gefunden: nach
            // Fenster zu und auf trug die Rolle wieder den alten Namen, und das CE-Loadout wurde nie
            // umbenannt - zweimal reproduziert).
            // Die erste Fassung haengte am Fokus (GUI.SetNextControlName + GetNameOfFocusedControl).
            // Das hat nicht getragen; woran genau, ist von aussen nicht zu sehen, und eine Wette auf IMGUIs
            // Fokusverwaltung ist hier auch die falsche Bauform. Eine Ruhepause ist unabhaengig davon,
            // deterministisch und loest zugleich das Problem, weswegen ich ueberhaupt nicht pro Tastendruck
            // schreibe: beim Tippen von "Medic" entstehen keine Rollen "M", "Me", "Med".
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
            // Die linke Kante des Hauptknopfes steht schon hier fest - die Marke darunter braucht sie als
            // Grenze, damit sie sich beim Messen nicht in den Knopf schiebt.
            float createRectLeft = inner.xMax - 230f;
            // ENTFERNT 2026-08-18: hier stand ein Zaehler fuer den "Alle erzeugen/aktualisieren"-Knopf, und
            // mit dem Knopf ist er gegenstandslos geworden. Er war nicht bloss ueberfluessig, sondern teuer:
            // die Schleife rief RoleNeedsCommit fuer JEDE Rolle in JEDEM Bild, und das rechnet pro Rolle
            // einen vollstaendigen Merge ueber alle ihre Bausteine.
            // "(existiert - Aenderung)" hing bis zum 2026-08-14 allein an `existing != null`, sagte also in
            // Wahrheit nur "es gibt schon eins" und stand auch dann noch da, wenn gar nichts mehr zu tun war.
            // Zusammen mit dem Zaehlerfehler in RoleNeedsCommit las sich das als "da ist noch was offen",
            // waehrend der Einzelknopf daneben "unveraendert" meldete. Jetzt sagt die Marke genau das, was
            // derselbe Test auch dem Zaehler sagt - dieselbe Quelle, keine zweite Rechnung.
            if (existing != null)
            {
                bool roleDirty = role != null && RoleNeedsCommit(role, comp);
                GUI.color = roleDirty ? new Color(0.95f, 0.85f, 0.4f) : new Color(1f, 1f, 1f, 0.45f);
                // Breite GEMESSEN statt fest 120 (2026-08-15, im Test gefunden): "(exists - up to
                // date)" passte nicht hinein und stand als "(exists - up to" da, waehrend rechts daneben
                // Platz frei war. Die kuerzere Variante "(exists - delta)" passte zufaellig - deshalb fiel
                // es nur an einer der beiden auf. Uebersetzungen sind laenger als das Englische, eine feste
                // Zahl ist hier also grundsaetzlich falsch.
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

            // ============ KEINE Knoepfe mehr, ausnahmslos (2026-08-18) ============
            // Ziel: es gibt nur noch "automatically saved" - die Knoepfe "create / update all (1)" und
            // "update role" tauchten manchmal noch auf.
            //
            // Am 17.08. blieben zwei Ausnahmen stehen, und genau die tauchten wieder auf: eine Rolle ohne
            // gespeicherte Zusammensetzung (aus einer frueheren Spielsitzung) und eine, deren Speichern eine
            // verwaiste gleichnamige Rolle adoptiert haette. Dazu kam, dass EINE solche Rolle irgendwo in
            // der Liste die Knoepfe fuer ALLE zurueckholte - daher das "manchmal".
            //
            // Warum das jetzt gefahrlos geht, Punkt fuer Punkt:
            //  - Leere Reihe: AutoCommitRole steigt bei comp.Count == 0 aus. Eine Rolle, von der der Mod die
            //    Zusammensetzung nicht kennt, wird also von allein NIE angefasst - der Fall, gegen den die
            //    erste Ausnahme gebaut war, kann ohne Zutun gar nicht eintreten.
            //  - Leeren einer gefuellten Rolle: CommitRole lehnt das seit dem 16.08. hart ab, unabhaengig
            //    von jedem Knopf.
            //  - Adoption einer verwaisten Rolle mit Traegern: die Rueckfrage ist geblieben, sie sitzt jetzt
            //    in AutoCommitRole. Eine einmalige Frage im richtigen Moment ist das, was hier gebraucht
            //    wird - kein Knopf, der dauerhaft dasteht.
            // Was bleibt, ist die ruhige Zeile "wird automatisch gespeichert" und die Vorschau darunter.
            GUI.color = new Color(1f, 1f, 1f, 0.45f);
            Text.Anchor = TextAnchor.MiddleRight;
            Widgets.Label(new Rect(createAllRect.x, inner.y, createRect.xMax - createAllRect.x, 30f),
                "CER_GenerateLoadouts_RoleAutoSavedHint".Translate());
            Text.Anchor = TextAnchor.UpperLeft;
            GUI.color = Color.white;

            // Existing role loadout, for the delta.
            Dictionary<string, int> existMap = SlotMap(existing);
            Dictionary<string, string> existLabels = new Dictionary<string, string>();
            // Inhalt der BESTEHENDEN Rolle, mit Def und Menge - fuer Masse und Volumen weiter unten
            // (2026-08-23). Hier gesammelt und nicht dort noch einmal, weil dieser Durchlauf ueber
            // existing.Slots ohnehin laeuft: Slots ist bei einem Loadout MIT Eltern kein Feld, sondern ein
            // Neuaufbau pro Zugriff, und die Rollen-Loadouts haengen ihre Packs genau so an.
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
            // "Geladene Ansicht": nichts abgelegt, die Rolle existiert schon - die Liste zeigt dann ihren
            // IST-Zustand, und es wird gerade gar nichts geschrieben. Das ist unten dreimal wichtig
            // (Masse/Volumen, Stapelzahl, Legende), deshalb einmal benannt statt dreimal nachgefragt.
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
                // Pack-Inhalte gehoeren in diese Liste (2026-08-13, zwei Befunde: "Any Medicine" war ohne
                // Grund durchgestrichen, und die Vorschau zeigte nur den Inhalt der Loadouts, nicht den der
                // Equipment Packs). Beides war dieselbe Ursache: RoleUtility.
                // Merge liefert bewusst nur Waffen und Munition, weil ein Pack seit dem 09.08. an die Rolle
                // ANGEHAENGT statt eingeschmolzen wird (siehe CommitRole). Die Vorschau las nur die
                // Merge-Haelfte - und weil das bestehende Rollen-Loadout den Pack-Inhalt ueber die
                // Multi-Parent-Kette sehr wohl fuehrt, tauchte er unten als "nicht mehr enthalten" auf:
                // durchgestrichen und rot, obwohl er bleibt. Das war die einzige Stelle im Mod, an der
                // Durchstreichen nicht "wird geloescht" hiess.
                // Der Packname steht dahinter, damit erkennbar bleibt, woher die Zeile kommt - sie laesst
                // sich hier naemlich nicht einzeln aendern, sondern nur ueber das Pack selbst.
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
            // Nur drei Zeilen passen hier hinein - siehe DrawScrollMoreHint, warum das ohne Hinweis wie eine
            // vollstaendige Liste aussieht.
            LoadoutUtility.DrawScrollMoreHint(tableOut, tableView, rolePreviewScroll, 22f);

            // Mass / bulk + legend.
            float m = 0f, b = 0f;
            StatDef bulkStat = DefDatabase<StatDef>.GetNamed("Bulk", false);
            // In der geladenen Ansicht wiegt das, was die Rolle SCHON enthaelt (2026-08-23, im Test
            // aufgefallen: eine erstellte Rolle zeigte "Result: 0 stacks", "Mass 0.0/40.0 kg", "Bulk 0.0/20.0",
            // waehrend die Liste direkt darueber Gewehr und 150 Schuss auflistete).
            // Die Null war nicht falsch gerechnet, sondern falsch beschriftet: alle drei Werte lasen
            // resultRows, also das, was dieser Ablegevorgang schreiben WUERDE - und der schreibt hier nichts.
            // Fuer den Spieler steht an den Balken aber "Mass" und "Bulk", nicht "Aenderung der Masse". Eine
            // Rolle mit Gewehr und 150 Schuss, die 0,0 kg behauptet, ist schlicht eine Falschaussage - und
            // ausgerechnet bei einer bestehenden Rolle ist die Traglast die Frage, wegen der man hinsieht.
            // Die Balken zeigen jetzt in beiden Faellen dasselbe: was die Rolle nach diesem Bildschirm haelt.
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
            // Der Inhalt angehaengter Equipment Packs zaehlt MIT (2026-08-13, im Test aufgefallen: die
            // vier Fertiggerichte aus dem Pack fehlten in der Liste UND in Masse/Bulk).
            // Warum er ueberhaupt fehlte, ist kein Fehler, sondern eine Entscheidung: RoleUtility.Merge
            // schmilzt Packs bewusst NICHT ein, sie haengen als echte Referenz an der Rolle (seit
            // 09.08.: echte Verbindungen auch bei Rollen). Der Kolonist bekommt den Inhalt also
            // sehr wohl - nur diese Vorschau hat ihn nie mitgerechnet, und damit war ihre Traglast-Aussage
            // fuer jede Rolle mit Pack zu niedrig. Wer knapp unter der Grenze lag, war in Wahrheit darueber.
            // Nicht in resultRows aufgenommen: die Liste zeigt, was in die Rolle GESCHRIEBEN wird, und das
            // ist der Pack-Inhalt gerade nicht - er bleibt referenziert. Er zaehlt hier nur fuer die Last.
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
            // Die Legende nur dort, wo es wirklich etwas zu unterscheiden gibt (2026-08-23, zweite Haelfte
            // derselben Meldung: hinter "Result: 0 stacks" standen "added changed removed" ohne jede Zahl -
            // das sah nach einer Beschriftung aus, deren Werte fehlen). In der geladenen Ansicht wird
            // nichts hinzugefuegt, geaendert oder entfernt, die drei Woerter erklaeren also Farben, die in
            // der Liste darueber gar nicht vorkommen.
            string legend = (existing != null && !loadedView) ? "CER_GenerateLoadouts_ResultLegend".Translate() : "";
            // Die Zahl nennt den Pack-Anteil getrennt, statt ihn stillschweigend zu verschlucken - sonst
            // widerspricht sie den Balken direkt darunter, die ihn ja mitrechnen.
            // Seit 2026-08-13 ist die ERSTE Zahl die Gesamtsumme (Stapelzahl und
            // Liste passen nicht zusammen"): vorher stand dort die Zahl OHNE Packs und daneben der Pack-Anteil,
            // also "3 + 2" ueber einer Liste mit fuenf Zeilen - man musste selbst addieren, um zu merken, dass
            // nichts fehlt. Jetzt zaehlt die erste Zahl genau die Zeilen, die man daruber sieht (durchgestrichene
            // sind sichtbar KEIN Ergebnis), und der Pack-Anteil steht als Erlaeuterung dahinter.
            // In der geladenen Ansicht zaehlen die Zeilen der Liste selbst - dasselbe Versprechen wie oben,
            // nur fuer den Fall, in dem resultRows leer ist: die erste Zahl ist immer die Anzahl der Zeilen,
            // die man darueber sieht.
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

        // Wie viele Kolonisten an einem VERWAISTEN Loadout haengen, das CommitRole gleich adoptieren wuerde.
        // 0 heisst: entweder gibt es kein gleichnamiges Loadout, oder diese Rolle besitzt es ohnehin schon
        // (dann ist es kein Waisenkind, sondern ein normales Update), oder es traegt niemand.
        // Spiegelt bewusst exakt die Fallback-Logik in CommitRole - findet die dort das Objekt ueber den ALTEN
        // Namen, wird gar nicht adoptiert und hier darf auch nicht gewarnt werden.
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
            // Packs zaehlen als Inhalt, auch wenn sie nicht mehr in `res` landen (2026-08-09) - sonst gilt eine
            // Rolle, die NUR aus einem Equipment Pack besteht, faelschlich als leer und laesst sich gar nicht
            // mehr anlegen. Genau das waere die stille Nebenwirkung davon, sie aus dem Merge zu nehmen.
            List<Loadout> rolePacks = RoleUtility.PacksIn(comp);
            // REINES UMBENENNEN, bevor die "nichts zu tun"-Absage greift (2026-08-15, im Test
            // gefunden: aenderte man NUR den Namen einer bestehenden Rolle, blieb Update AUSGEGRAUT).
            // Ursache war nicht der Knopf, sondern diese Zeile: ohne etwas Hineingezogenes ist comp leer,
            // und damit galt auch eine Umbenennung als nichts.
            // Bewusst OHNE Neuaufbau des Inhalts: comp ist leer, ein Rebuild wuerde die Rolle LEEREN. Hier
            // wird nur das Etikett des bestehenden Objekts geaendert - jeder Kolonist daran behaelt exakt
            // seine Ausruestung, denn es ist dasselbe Loadout.
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
            // ZUERST ueber die Identitaet (2026-08-17): RoleLoadoutOf kennt das Loadout dieser Rolle, ohne
            // ihren Namen zu benutzen - und schreibt die Verknuepfung beim ersten Mal fest. Damit findet ein
            // Umbenennen sein Ziel auch dann noch, wenn das Etikett laengst nicht mehr passt (etwa weil es
            // jemand in CEs eigenem Editor geaendert hat). Erst wenn die Rolle noch gar kein Loadout hat,
            // greift der alte Weg ueber das Etikett.
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
                // Packs als ECHTE Referenz anhaengen, nicht einschmelzen (2026-08-09) - dieselbe
                // Multi-Parent-Mechanik, die ein Pack an einem einzelnen Kolonisten schon immer benutzt hat.
                // Muss NACH AddLoadout stehen: die Zuordnung wird ueber die UniqueID gespeichert, und die
                // vergibt erst der LoadoutManager.
                MultiParentUtility.SetExtraParents(made, rolePacks);
                // Die Rolle merkt sich AB JETZT ihr Loadout ueber die Identitaet (2026-08-17) - das ist der
                // Grund, warum sie nicht mehr am Namen erkannt werden muss. Nach AddLoadout, weil die
                // UniqueID erst dort vergeben wird.
                RoleUtility.LinkRoleLoadout(role, made);
                role.name = name;
                RememberComposition(role, comp);
                // Der ANGEZEIGTE Name, nicht das gespeicherte Etikett (2026-08-18): die vier
                // Vollzugsmeldungen dieser Funktion sagten "[Role] Foo", waehrend jede Liste daneben
                // laengst "Foo" schreibt. Der Marker ist Buchhaltung und wird nirgends gezeigt -
                // siehe LoadoutUtility.LoadoutDisplayName.
                message = "CER_GenerateLoadouts_CreatedMessage".Translate(LoadoutUtility.LoadoutDisplayName(made.label));
                return true;
            }

            Dictionary<string, int> oldMap = SlotMap(existing);

            // HARTE SPERRE: eine Aktualisierung darf eine gefuellte Rolle niemals LEEREN (2026-08-16).
            // Genau das ist der Schaden, um den sich das Autosave der Rollen dreht: eine
            // Rolle, deren Zusammensetzung der Mod nicht mehr kennt, wird aus einer leeren Kaestchen-Reihe
            // neu gebaut - und jeder Kolonist daran steht ohne Ausruestung da. Bisher haengte der Schutz
            // daran, dass immer ein Mensch auf "Update" drueckt und die durchgestrichene Vorschau sieht.
            // Das ist kein Schutz, das ist Aufmerksamkeit; und es ist der einzige Grund, warum das
            // Sofort-Speichern noch nicht gebaut ist.
            // Hier steht die Regel jetzt im Code, wo sie unabhaengig von Reihenfolge und Aufmerksamkeit
            // gilt: nichts drin UND vorher war etwas drin UND auch keine Packs, die den Inhalt tragen
            // koennten -> abgelehnt, mit Begruendung, ohne etwas anzufassen.
            // "Vorher war etwas drin" heisst AUCH: sie hatte angehaengte Packs. Eine Rolle, die ihren
            // ganzen Inhalt ueber ein Pack bezieht, hat null eigene Slots - mit oldMap allein waere sie
            // durch diese Sperre gefallen, und SetExtraParents haette ihr unten das Pack abgenommen.
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
            // Beim Update dieselbe Referenz-Anhaengung wie beim Anlegen - sonst behaelt eine bestehende Rolle
            // ewig ihren alten Schnappschuss, und der Fix waere nur fuer neue Rollen wirksam.
            MultiParentUtility.SetExtraParents(existing, rolePacks);
            existing.label = newLabel;
            // Auch beim Aktualisieren festschreiben (2026-08-17) - eine Rolle aus einem alten Spielstand
            // hatte noch keine Verknuepfung, und dies ist der Moment, in dem sie sicher bekannt ist.
            RoleUtility.LinkRoleLoadout(role, existing);
            role.name = name;
            RememberComposition(role, comp);
            Dictionary<string, int> newMap = SlotMap(existing);
            int added = 0, removed = 0, changed = 0;
            foreach (var kv in newMap) { if (!oldMap.TryGetValue(kv.Key, out int o)) added++; else if (o != kv.Value) changed++; }
            foreach (var kv in oldMap) if (!newMap.ContainsKey(kv.Key)) removed++;
            // Einmal fuer alle vier Meldungen darunter - siehe Kommentar bei CreatedMessage.
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
        // Der Name, den die Karte ANZEIGT - beim Umbenennen der getippte, sonst der gespeicherte.
        // Eine Stelle fuer Beschriftung, Hauptwaffen-Kuer und die Frage "ist hier etwas zu tun".
        private string EffectiveRoleName(LoadoutRole r)
        {
            if (r == activeRoleObj && !string.IsNullOrWhiteSpace(roleNameEdit)) return roleNameEdit.Trim();
            return (r?.name ?? "").Trim();
        }

        // Nur auf den zwei Wegen aufgerufen, die den Inhalt der Rolle wirklich neu bauen - NICHT im
        // reinen Umbenenn-Zweig. Dort ist comp leer, und die gespeicherte Zusammensetzung damit zu
        // ueberschreiben hiesse, die Rueckgewinnung genau in dem Moment wegzuwerfen, in dem sie am
        // wenigsten weh tun darf: beim Umbenennen aendert sich am Inhalt ja nichts.
        private static void RememberComposition(LoadoutRole role, List<Loadout> comp)
        {
            if (role == null) return;
            List<string> labels = new List<string>();
            if (comp != null)
                foreach (Loadout l in comp)
                    if (l != null && !string.IsNullOrEmpty(l.label) && !labels.Contains(l.label)) labels.Add(l.label);
            // Hier stand vom 04.09. bis 06.09. eine Diagnosezeile: sie
            // sollte belegen, ob ein Ziehen auf eine lokale Rolle noch eine GLOBALE mitschreibt. Der
            // Testlauf vom 04.09. ("alle anderen Rollen behielten
            // ihre Faktoren") war der Lauf ohne diese Zeile - die Ursache waren die zeilennummer-gebundenen
            // Eingabepuffer, siehe cardFactorBuffers. Wieder ausgebaut.
            role.composition = labels;
        }

        // ================== Rollen speichern sofort, wie Equipment Packs (2026-08-17) ==================
        //
        // Ziel seit 2026-08-15: Rollen werden automatisch erzeugt / aktualisiert, genau wie Equipment
        // Packs. Die Knoepfe "create/update all" und "update role xyz" sind weg, stattdessen wird sofort
        // gespeichert.
        //
        // Schritt 1 (die Rolle merkt sich ihre Zusammensetzung) steht seit dem 15.08. und ist im Test
        // bestaetigt. Das hier ist Schritt 2.
        //
        // DIE WEICHE, und sie ist nicht Vorsicht, sondern notwendig: eine Rolle aus einer frueheren Spielsitzung
        // hat KEINE gespeicherte Zusammensetzung (im Test am 2026-08-17 belegt - solche Rollen
        // zeigen weiter "drag a loadout here"). Fuer sie baut ein Update die Rolle aus einer leeren Reihe neu
        // und wuerde sie leeren. Solche Rollen behalten deshalb Vorschau und Knopf, bis sie einmal bewusst
        // zusammengesetzt wurden - danach sind auch sie im neuen Verhalten.
        //
        // ZWEITE BREMSE: adoptiert das Speichern eine verwaiste gleichnamige Rolle, an der noch Kolonisten
        // haengen, fragt der Knopfweg vorher nach. Diese Frage darf nicht bei jedem Ziehen aufpoppen - also
        // speichert so eine Rolle nicht von selbst, sondern behaelt ihren Knopf.
        // Seit 2026-08-18 ohne Ausnahme wahr - die Funktion bleibt als EINE Stelle stehen, an der die Frage
        // beantwortet wird, falls es je wieder eine Ausnahme geben soll. Warum die zwei alten hier weg sind,
        // steht ausfuehrlich am Knopfblock in DrawRolePreview; kurz: die eine kann ohne Zutun gar nicht
        // eintreten (AutoCommitRole ruehrt eine leere Reihe nicht an), die andere ist zur Rueckfrage in
        // AutoCommitRole gewandert.
        private bool RoleAutoSaves(LoadoutRole role) => role != null;

        // Schreibt sofort, ohne Meldung und ohne Knopf. Still, weil eine Vollzugsmeldung bei JEDEM Ziehen
        // genau die Sorte Rauschen waere, die Packs auch nicht machen - was passiert ist, steht in der
        // Vorschau darunter.
        private void AutoCommitRole(LoadoutRole role, CEReloadedSettings st)
        {
            if (!RoleAutoSaves(role)) return;
            List<Loadout> comp = CompFor(role);
            string name = EffectiveRoleName(role);
            if (comp.Count == 0 || name.Length == 0) return;
            if (!RoleNeedsCommit(role, comp)) return;

            // Die EINE Rueckfrage, die aus dem Knopfweg hierher gewandert ist (2026-08-18): adoptiert das
            // Speichern eine verwaiste gleichnamige Rolle, an der noch Kolonisten haengen, werden die im
            // selben Moment umgeruestet. Das Umruesten ist harmlos, das Ueberraschende daran ist, dass es
            // unangekuendigt kommt - deshalb warnen statt verbieten, und nur, wenn wirklich jemand daran
            // haengt. Sie kann nicht in einer Schleife aufpoppen: AutoCommitRole laeuft an Ereignissen
            // (Ziehen, Baustein entfernen, Faktor aendern, Umbenennen), nicht pro Bild.
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
            // Ein anstehender Namenswechsel IST Arbeit, auch ohne dass etwas hineingezogen wurde
            // (2026-08-15) - sonst bleibt der Knopf grau und die Umbenennung ist ueber die Oberflaeche
            // gar nicht ausloesbar. CommitRole hat dafuer jetzt einen eigenen, inhaltsschonenden Pfad.
            if (role != null)
            {
                string pending = EffectiveRoleName(role);
                if (pending.Length > 0 && pending != (role.name ?? "").Trim()
                    && RoleUtility.RoleLoadoutOf(role) != null)
                    return true;
            }
            if (comp == null || comp.Count == 0) return false;
            // Kein "res.Count == 0 -> false" mehr (2026-08-13, dieselbe Ursache wie bei canAct oben): eine
            // reine Pack-Rolle hat keine Merge-Zeilen und galt deshalb als "nichts zu tun".
            List<RoleMergeEntry> res = RoleUtility.Merge(role, comp).Where(r => r.scaledCount > 0).ToList();
            Loadout existing = RoleUtility.RoleLoadoutOf(role);
            if (existing == null) return true;
            // OwnSlots, NICHT SlotMap(existing) (2026-08-14). Das war der ganze Fehler: `SlotMap` liest
            // `Loadout.Slots`, also eigene UND geerbte Slots - bei einer Rolle mit angehaengtem Pack steht der
            // PACK-INHALT mit drin. Verglichen wurde das gegen `res`, und da sind die Packs ausdruecklich
            // NICHT enthalten (siehe CommitRole: sie werden als echte Referenz angehaengt, nicht
            // eingeschmolzen). Zwei verschiedene Mengen, also war der Vergleich fuer JEDE Rolle mit Pack
            // dauerhaft "ungleich" - bei einer reinen Pack-Rolle sogar 0 gegen n.
            // Sichtbar wurde es so (14.08., im Test zweimal gemessen): nach "Alle erzeugen/
            // aktualisieren" stand weiter "(exists - delta)" an der Zeile und der Zaehler weiter auf (2),
            // waehrend der Einzelknopf derselben Rolle korrekt "unveraendert" meldete. CommitRole vergleicht
            // vorher/nachher DIESELBE Quelle und hatte deshalb recht; nur dieser Zaehler verglich Aepfel mit
            // Birnen.
            // OwnSlots ist genau das, was CommitRole schreibt (Clear + ein Slot je res-Eintrag) - damit
            // beantworten Zaehler und Einzelknopf dieselbe Frage aus derselben Quelle. Die Packs werden
            // weiterhin verglichen, nur eben dort, wo sie wirklich stehen: als Elternliste, unten.
            Dictionary<string, int> oldMap = new Dictionary<string, int>();
            foreach (LoadoutSlot s in existing.OwnSlots ?? new List<LoadoutSlot>())
                oldMap[SlotKey(s.thingDef, s.genericDef)] = s.count;
            Dictionary<string, int> newMap = new Dictionary<string, int>();
            foreach (RoleMergeEntry e in res) newMap[SlotKey(e.thing, e.generic)] = e.scaledCount;
            if (oldMap.Count != newMap.Count) return true;
            foreach (var kv in newMap) if (!oldMap.TryGetValue(kv.Key, out int o) || o != kv.Value) return true;
            // Die PACKS mitvergleichen (2026-08-13, Nebenbefund desselben Fehlers): aendert man an einer
            // bestehenden Rolle NUR die angehaengten Packs, waren beide Slot-Karten gleich und der Mod meldete
            // "unveraendert" - obwohl CommitRole die Aenderung sehr wohl schreibt. "Alle erzeugen/aktualisieren"
            // hat sie deshalb nicht mitgezaehlt.
            var oldPacks = new HashSet<int>(MultiParentUtility.GetExtraParents(existing).Select(l => l.UniqueID));
            var newPacks = new HashSet<int>(RoleUtility.PacksIn(comp).Select(l => l.UniqueID));
            if (!oldPacks.SetEquals(newPacks)) return true;
            return false;
        }

        // ENTFERNT 2026-08-18: CreateAllRoles - der Rumpf des Knopfes "Alle erzeugen/aktualisieren".
        // Mit dem Knopf faellt auch er weg; jede Rolle speichert von selbst, sobald sich etwas an ihr
        // aendert (AutoCommitRole). Ein Sammelknopf haette nichts mehr zu tun.

        // Die GLOBALE Rollenliste liegt in den Mod-Einstellungen, und die schreibt RimWorld von sich aus nur,
        // wenn der Spieler das Einstellungsfenster schliesst - dieser Reiter hat es nie getan. Fuer eine
        // save-lokale Rolle (alles, was "+ New role" anlegt) traegt der Spielstand die Daten ohnehin; fuer
        // eine globale gingen Name, Faktoren und ab heute auch die Zusammensetzung beim Beenden verloren.
        // Einmal pro bestaetigtem Vorgang, nicht pro Bild: Write() serialisiert die ganze Einstellungsdatei.
        private static void PersistRoles()
        {
            if (CEReloadedMod.settings != null) CEReloadedMod.settings.Write();
        }

        // Merkzettel, wieviele Loadouts es beim letzten Aufraeumen gab. Aendert sich die Zahl, ist irgendwo
        // eines geloescht (oder dazugekommen) - erst dann muessen die gemerkten Reihen geprueft werden.
        private int roleCompCheckedCount = -1;

        private List<Loadout> CompFor(LoadoutRole role)
        {
            if (role == null) return new List<Loadout>();
            // GELOESCHTE Quellen aus den gemerkten Reihen werfen (2026-08-18: es gab keine Weapon Loadouts
            // mehr, aber die Rollen hatten immer noch welche zugeteilt).
            // Die fruehere Beschreibung war falsch: die Reihe wird beim ERSTEN Zugriff aus der
            // gespeicherten Zusammensetzung aufgebaut und danach im Fenster festgehalten. Loescht man die
            // Quell-Vorlagen, waehrend das Fenster offen ist, zeigt die Karte weiter Bausteine, hinter denen
            // nichts mehr steht - und ein Klick darauf haette mit einem toten Objekt gerechnet.
            // Nicht jedes Bild, sondern nur wenn sich die Zahl der Loadouts geaendert hat: derselbe Riegel,
            // den RebuildLoadoutsByWeaponFrame benutzt.
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
                // RUECKGEWINNUNG beim ersten Zugriff (2026-08-15, LoadoutRole.composition - die lange
                // Begruendung steht dort). Ohne sie startet jede bestehende Rolle mit leerer Reihe, und das
                // naechste Update ERSETZT ihren Inhalt, statt ihn zu ergaenzen.
                // Bewusst nur beim ANLEGEN des Eintrags: wer alle Anhaenge von Hand entfernt, bekommt sie
                // nicht beim naechsten Bild wieder hereingespuelt - der Eintrag existiert dann als leere
                // Liste, und dieser Zweig laeuft nicht mehr.
                // Ein Quell-Loadout, das es nicht mehr gibt (geloescht oder umbenannt), faellt still heraus.
                if (role.composition != null && role.composition.Count > 0)
                {
                    foreach (string label in role.composition)
                    {
                        if (string.IsNullOrEmpty(label)) continue;
                        Loadout src = LoadoutManager.Loadouts.FirstOrDefault(
                            l => l != null && !l.defaultLoadout && l.label == label);
                        // LAZY NACHBAUEN (2026-09-07, im Test: "Faktoren auch bei fertigen Rollen gesperrt",
                        // mit dem Hinweistext "Drop a loadout onto this role", obwohl die Rolle laengst eine
                        // Waffe enthielt). Seit dem Lazy-Umbau (01.09.) entstehen Waffen-Vorlagen nur bei
                        // Bedarf und verschwinden, wenn niemand sie mehr traegt - eine Rolle, die aus so
                        // einer Vorlage gebaut wurde, fand ihre Quelle dann nicht mehr, die Reihe blieb leer,
                        // und leer heisst gesperrt. Die Vorlage ist aber jederzeit wieder herstellbar, genau
                        // dafuer gibt es EnsureDefaultLoadout: eine Waffen-Vorlage IST ihre Waffe.
                        if (src == null) src = RecreateTemplateByLabel(label);
                        if (src != null && !list.Contains(src)) list.Add(src);
                    }
                }
                // ZWEITER Notnagel: die Rolle hat ein Loadout, aber keine auffindbare Zusammensetzung
                // (angelegt, bevor sie gemerkt wurde, oder ueber einen Weg, der sie nie gemerkt hat). Dann
                // sind die Waffen des Rollen-Loadouts die Quellen - jede Waffe hat ihre Vorlage. Einmal
                // gemerkt, damit das naechste Oeffnen nicht wieder suchen muss.
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

        // Eine Quell-Vorlage, die es gerade nicht gibt, aus ihrem Etikett nachbauen: das Etikett einer
        // Waffen-Vorlage ist VirtualLoadoutLabel(waffe), also laesst sich die Waffe daraus zurueckfinden.
        // Nur echte Waffen; alles andere (Packs, fremde Loadouts) bleibt verschwunden - das war vorher so
        // und ist richtig, weil es dafuer keine Vorlage gibt, die man nachbauen koennte.
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

        // Ueber die Identitaet (2026-08-17). Das ist die Frage "wurde diese Rolle schon einmal geschrieben"
        // und sie darf nicht davon abhaengen, wie das Loadout gerade heisst.
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

            // Eine BESTEHENDE Rolle im Reiter auswaehlen (2026-08-16): hat ein Pawn eine Rolle, steht
            // statt eines inaktiven "Save as role" ein Knopf "Edit Role", der in den Rollen-Editor springt.
            // Bewusst NICHT ueber StartRoleDraftFromPawn: das legt einen
        // ENTWURF aus den Bestandteilen des Kolonisten an - bei einem Kolonisten, der die Rolle schon
        // traegt, waere das eine Kopie statt der Rolle selbst.
        // Gesucht wird ueber den Namen, weil das im ganzen Mod die Identitaet einer Rolle ist (das Loadout
        // heisst "[Role] <name>"). Findet er nichts, passiert nichts - dann ist die Rolle geloescht worden,
        // waehrend das Fenster offen war, und ein stiller Fehlschlag ist besser als eine leere Karte.
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
            // Die Zusammensetzung SOFORT festhalten (2026-09-04, Befund im Test: die Rolle hiess
            // weiter '[S] Henry Big Boy +Base Supply', aber in ihrer Zeile stand 'drag a loadout here' -
            // uebrig blieb eine Rolle, deren Name eine Waffe und ein Pack verspricht und die nichts
            // enthielt).
            //
            // Ursache: dieser Entwurf fuellte nur die LAUFZEIT-Reihe (roleComp). role.composition blieb
            // leer, und sobald CompFor die Reihe neu aufbaut - was es tut, sobald sich die Zahl der
            // Loadouts geaendert hat -, baut es sie aus genau dieser leeren Zusammensetzung wieder auf.
            // Die Karte war danach dauerhaft leer, und die Faktorfelder ausgegraut.
            //
            // Bewusst nur MERKEN, nicht anlegen: ein Entwurf soll ausdruecklich erst auf Knopfdruck zur
            // echten Rolle werden (siehe der Kommentar oben). Gemerkt wird ueber die Etiketten - der
            // Wegwerf-Traeger fuer persoenliche Munition faellt dabei still heraus, weil es ihn in der
            // Loadout-Liste nicht gibt; das ist die dokumentierte Zusage von CompFor.
            RememberComposition(role, comp);
            SetActiveRole(role);
        }

        public void DoWindowContents(Rect inRect)
        {
            // Seit dem Lazy-Umbau (2026-09-01) gibt es NUR noch den Rollen-Reiter (2026-09-06, Aufraeumen):
            // der Zeichenkoerper des alten Waffen-Loadouts-Reiters (rund 950 Zeilen - Filterzeile,
            // Waffenliste mit Lade-/Bearbeiten-/Zuweisen-Knoepfen, Fussleiste) stand hier bis heute und war
            // unerreichbar - jeder Aufrufer setzt RoleMode vorher auf true (Dialog_CEReloadedTabs zeichnet den
            // Rollen-Reiter direkt ueber DoRoleMode, Dialog_ArmColony.RolesStep setzt RoleMode im Konstruktor).
            // Im Test am 03.09. von aussen belegt: zwei Tests rot, weil es die Oberflaeche nicht mehr
            // gab. Die Klasse selbst lebt weiter - fuer die Rollen und als Heimat von EnsureDefaultLoadout.
            RoleMode = true;
            DoRoleMode(inRect);
        }

        // Statischer Kern (2026-09-01, Lazy-Umbau): braucht keinerlei Fensterzustand, damit die
        // Hintergrund-Erzeugung (EnsureDefaultLoadout) ohne offenes Fenster laufen kann.
        // status: 0 = neu angelegt, 1 = aktualisiert, 2 = unveraendert.
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
