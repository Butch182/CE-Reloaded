using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using RimWorld;
using Verse;
using UnityEngine;
using CombatExtended;

namespace CEReloaded
{
    // Compatibility self-audit. Walks every weapon ThingDef currently loaded (i.e. from every ACTIVE mod, since all defs
    // share one DefDatabase) and flags weapons that CE Reloaded's classifiers handle poorly. This is how we find exotic
    // modded-weapon edge cases without guessing mods: whatever modlist is running gets checked. Safe to keep in release.
    public static class WeaponAudit
    {
        public enum Severity { High, Medium, Low }

        public class Finding
        {
            public ThingDef weapon;
            public string source;
            public Severity severity;
            public string category;
            public string detail;
        }

        public static int LastScannedCount;
        public static int LastModCount;

        public static List<Finding> Run()
        {
            var findings = new List<Finding>();
            var mods = new HashSet<string>();
            int scanned = 0;

            foreach (ThingDef w in DefDatabase<ThingDef>.AllDefs)
            {
                if (!LoadoutUtility.IsRealWeaponDef(w)) continue;
                scanned++;
                string src = w.modContentPack != null ? w.modContentPack.Name : "Core";
                mods.Add(src);

                void Add(Severity sev, string cat, string detail) =>
                    findings.Add(new Finding { weapon = w, source = src, severity = sev, category = cat, detail = detail });

                try
                {
                    bool ce = LoadoutUtility.IsCeCompatible(w);
                    bool selfAmmo = LoadoutUtility.IsSelfAmmoWeapon(w);
                    bool utility = LoadoutUtility.IsUtilityWeapon(w);

                    // A 0 score is only a red flag for a *standard combat weapon*: a gun that fires ammo, or a CE melee
                    // weapon. Grenades/thrown (self-ammo), utility/marker weapons (binoculars, flares, smoke) and
                    // improvised items (e.g. raw wood) score 0 by design and must NOT be flagged.
                    bool realGun = w.IsRangedWeapon && !selfAmmo && !utility &&
                                   (w.GetCompProperties<CompProperties_AmmoUser>() != null || LoadoutUtility.GetAmmoSet(w) != null);
                    bool realMelee = w.IsMeleeWeapon && !selfAmmo && !utility &&
                                     w.tools != null && w.tools.Any(t => t is ToolCE);

                    // 1. Score must be a finite, sensible number.
                    LoadoutUtility.GetWeaponScore(w, out float score, out _);
                    if (float.IsNaN(score) || float.IsInfinity(score))
                        Add(Severity.High, "CER_WeaponAudit_CatScoreInvalid".Translate(), "CER_WeaponAudit_DetailScoreInvalid".Translate(score.ToString()));
                    else if ((realGun || realMelee) && score <= 0f && HasRealDamage(w))
                        Add(Severity.Medium, "CER_WeaponAudit_CatScoreZero".Translate(), "CER_WeaponAudit_DetailScoreZero".Translate());

                    // 2. Ranged weapon flagged non-CE although it carries an ammoSet or a projectile → likely misdetected.
                    if (w.IsRangedWeapon && !ce)
                    {
                        bool armed = LoadoutUtility.GetAmmoSet(w) != null ||
                                     (w.Verbs != null && w.Verbs.Any(v => v.defaultProjectile != null));
                        if (armed)
                            Add(Severity.Medium, "CER_WeaponAudit_CatNotCeButArmed".Translate(), "CER_WeaponAudit_DetailNotCeButArmed".Translate());
                    }

                    // 3. Normal CE ranged weapon whose ammo picker comes back empty → ammo bills can't be generated.
                    if (w.IsRangedWeapon && ce && !selfAmmo && LoadoutUtility.GetAmmoSet(w) != null)
                    {
                        if (LoadoutUtility.GetLoadoutAmmoDefs(w, false).Count == 0)
                            Add(Severity.Medium, "CER_WeaponAudit_CatRangedNoAmmo".Translate(), "CER_WeaponAudit_DetailRangedNoAmmo".Translate());
                    }

                    // 3b. CE-flagged ranged weapon (CompProperties_AmmoUser present) whose ammo SET itself never
                    // resolved at all - strictly worse than check 3 above (empty AFTER resolving): there is no
                    // ammo family for this weapon whatsoever, so no amount of research/production can ever arm
                    // it. IsCeCompatible alone can't see this (it only checks the comp is present, not whether
                    // ammoSet actually resolved), and the live in-game scan (CombatReadinessAlerts) had the same
                    // gap - both gated on GetAmmoSet(w) != null, which this weapon can never satisfy, so it
                    // silently skipped every ammo-related check with zero alert (2026-08-01).
                    if (w.IsRangedWeapon && ce && !selfAmmo && !utility && w.GetCompProperties<CompProperties_AmmoUser>() != null
                        && LoadoutUtility.GetAmmoSet(w) == null)
                        Add(Severity.High, "CER_WeaponAudit_CatAmmoSetUnresolved".Translate(), "CER_WeaponAudit_DetailAmmoSetUnresolved".Translate());

                    // 4. Self-ammo weapon (grenade/javelin) that has no crafting recipe → can't be produced anywhere.
                    if (selfAmmo && LoadoutUtility.GetRecipeFor(w) == null)
                        Add(Severity.Low, "CER_WeaponAudit_CatSelfAmmoNoRecipe".Translate(), "CER_WeaponAudit_DetailSelfAmmoNoRecipe".Translate());
                }
                catch (Exception e)
                {
                    Add(Severity.High, "CER_WeaponAudit_CatException".Translate(), "CER_WeaponAudit_DetailException".Translate(e.GetType().Name, e.Message));
                }
            }

            LastScannedCount = scanned;
            LastModCount = mods.Count;
            return findings
                .OrderBy(f => (int)f.severity)
                .ThenBy(f => f.source)
                .ThenBy(f => f.weapon.label)
                .ToList();
        }

        // True only if the weapon actually carries meaningful combat damage in its defs. Distinguishes a genuine scoring
        // failure (a real gun/melee we scored 0) from a weapon that legitimately has ~0 combat value - flare guns
        // (projectile does no damage) and improvised items like raw wood (tiny tool power) must NOT be flagged.
        private static bool HasRealDamage(ThingDef w)
        {
            if (w.IsRangedWeapon)
            {
                foreach (ThingDef ammo in LoadoutUtility.GetAmmoTypesFor(w))
                    if (ProjHasDamage(LoadoutUtility.GetAmmoProjectile(ammo))) return true;
                if (w.Verbs != null)
                    foreach (VerbProperties v in w.Verbs)
                        if (v.defaultProjectile != null && ProjHasDamage(v.defaultProjectile)) return true;
                return false;
            }
            if (w.tools != null)
                foreach (Tool t in w.tools)
                    if (t.power > 9f) return true;   // a real melee weapon; improvised (raw wood ~5) stays below
            return false;
        }

        private static bool ProjHasDamage(ThingDef proj)
        {
            if (proj == null || proj.projectile == null) return false;
            // GetDamageAmount reads CE's per-shot damage correctly (same call the scorer uses); damageAmountBase is private.
            float d = proj.projectile.GetDamageAmount((Thing)null);
            if (d >= 99999f) return false;   // huge = non-lethal explosion trick, not real combat damage
            return d > 5f;                   // real bullet/arrow; a flare (~0 damage) stays below
        }

        public static string BuildReport(List<Finding> findings)
        {
            var sb = new StringBuilder();
            sb.AppendLine("CER_WeaponAudit_ReportHeader".Translate());
            sb.AppendLine("CER_WeaponAudit_ReportSummary".Translate(LastScannedCount.ToString(), LastModCount.ToString(), findings.Count.ToString()));
            sb.AppendLine();
            foreach (Finding f in findings)
                sb.AppendLine("[" + f.severity + "] " + f.weapon.label + " (" + f.weapon.defName + ")  <" + f.source + ">  " + f.category + ": " + f.detail);
            return sb.ToString();
        }
    }

    public class Dialog_WeaponAudit : Window
    {
        private readonly List<WeaponAudit.Finding> findings;
        private Vector2 scroll;

        public override Vector2 InitialSize => new Vector2(920f, 720f);
        // Nicht unter RimWorlds Lernhilfe schieben (2026-08-19) - siehe
        // LoadoutUtility.KeepClearOfLearningReadout. Ohne Kollision aendert das nichts.
        protected override void SetInitialSizeAndPosition()
        {
            base.SetInitialSizeAndPosition();
            windowRect = LoadoutUtility.KeepClearOfLearningReadout(windowRect);
        }

        public Dialog_WeaponAudit() : base(new ReadoutAwareWindowDrawing())
        {
            doCloseX = true;
            forcePause = true;
            absorbInputAroundWindow = true;
            findings = WeaponAudit.Run();
        }

        private static Color SeverityColor(WeaponAudit.Severity s)
        {
            switch (s)
            {
                case WeaponAudit.Severity.High: return new Color(0.90f, 0.35f, 0.30f);
                case WeaponAudit.Severity.Medium: return new Color(0.93f, 0.73f, 0.30f);
                default: return new Color(0.60f, 0.70f, 0.85f);
            }
        }

        public override void DoWindowContents(Rect inRect)
        {
            Text.Font = GameFont.Medium;
            Widgets.Label(new Rect(0, 0, inRect.width, 34f), "CER_WeaponAudit_DialogTitle".Translate());
            Text.Font = GameFont.Small;

            string summary = "CER_WeaponAudit_DialogSummary".Translate(WeaponAudit.LastScannedCount.ToString(), WeaponAudit.LastModCount.ToString(), findings.Count.ToString());
            GUI.color = new Color(1f, 1f, 1f, 0.7f);
            Widgets.Label(new Rect(0, 36f, inRect.width, 24f), summary);
            GUI.color = Color.white;

            float top = 68f;
            Rect outRect = new Rect(0, top, inRect.width, inRect.height - top - 46f);

            if (findings.Count == 0)
            {
                GUI.color = new Color(0.55f, 0.85f, 0.55f);
                Text.Anchor = TextAnchor.MiddleCenter;
                Widgets.Label(outRect, "CER_WeaponAudit_NoIssues".Translate());
                Text.Anchor = TextAnchor.UpperLeft;
                GUI.color = Color.white;
            }
            else
            {
                Rect viewRect = new Rect(0, 0, outRect.width - 16f, findings.Count * 46f + 4f);
                Widgets.BeginScrollView(outRect, ref scroll, viewRect);
                float y = 0f;
                // Zeilenindex statt Rechnung aus y (2026-08-09): die Zeilen ruecken um 46px vor, die
                // Alternation rechnete aber mit 44 - floor(46n/44) laeuft gegen n aus, ab Zeile 22 waren zwei
                // Zeilen hintereinander ungestreift und danach wiederholte sich der Versatz. Rein kosmetisch,
                // aber der Index kann per Definition nicht auslaufen, die Division schon.
                int rowIndex = 0;
                foreach (WeaponAudit.Finding f in findings)
                {
                    Rect row = new Rect(0, y, viewRect.width, 44f);
                    if (rowIndex++ % 2 == 0) Widgets.DrawLightHighlight(row);

                    // severity chip
                    GUI.color = SeverityColor(f.severity);
                    Widgets.DrawBoxSolid(new Rect(2f, y + 6f, 5f, 32f), SeverityColor(f.severity));
                    GUI.color = Color.white;

                    Widgets.ThingIcon(new Rect(14f, y + 6f, 32f, 32f), f.weapon);

                    Rect nameRect = new Rect(54f, y + 3f, viewRect.width - 58f, 22f);
                    Text.Anchor = TextAnchor.MiddleLeft;
                    Widgets.Label(nameRect, "CER_WeaponAudit_RowName".Translate(f.weapon.LabelCap.ToString(), f.source));

                    Rect detRect = new Rect(54f, y + 23f, viewRect.width - 58f, 20f);
                    GUI.color = new Color(1f, 1f, 1f, 0.75f);
                    string tag = "<color=#" + ColorUtility.ToHtmlStringRGB(SeverityColor(f.severity)) + ">" + f.category + "</color>";
                    Widgets.Label(detRect, tag + " - " + f.detail);
                    GUI.color = Color.white;
                    Text.Anchor = TextAnchor.UpperLeft;

                    TooltipHandler.TipRegion(row, "CER_WeaponAudit_RowTooltip".Translate(f.weapon.defName, f.source, f.category, f.detail));
                    y += 46f;
                }
                Widgets.EndScrollView();
            }

            // Bottom bar: Close pinned left, the action pinned right (same as every other popup).
            if (LoadoutUtility.BottomCloseButton(inRect, "CER_WeaponAudit_CloseButton".Translate())) Close();

            float xr = inRect.width;
            if (LoadoutUtility.BottomActionButton(inRect, ref xr, LoadoutUtility.PrimaryBtnW, "CER_WeaponAudit_CopyReportButton".Translate()))
            {
                GUIUtility.systemCopyBuffer = WeaponAudit.BuildReport(findings);
                Messages.Message("CER_WeaponAudit_CopiedMessage".Translate(), MessageTypeDefOf.TaskCompletion, false);
            }
        }
    }
}
