using System.Collections.Generic;
using System.Linq;
using HarmonyLib;
using UnityEngine;
using Verse;
using CombatExtended;

namespace CEReloaded
{
    // Adds an "Ammo Loadouts" gizmo to the pawn's own command row on the map (next to CE's own Reload/
    // Aimed shot buttons) - the on-the-fly ammo change lives right where a player would look for it, not
    // buried in a separate dialog. Opens the same Dialog_ChangeAmmo the Assign Loadouts tab's "Change ammo"
    // cell uses, which lazily creates the hidden per-pawn ammo-link loadout on the first real edit (see
    // AmmoAssignmentUtility) - nothing is generated just for the gizmo to exist.
    [HarmonyPatch(typeof(Pawn), nameof(Pawn.GetGizmos))]
    // Loads its own static Texture2D field (icon) - see Dialog_AssignLoadouts' own identical comment for why
    // this attribute is required (2026-08-01).
    [StaticConstructorOnStartup]
    public static class Patch_Pawn_ChangeAmmoGizmo
    {
        // Eager field initializer, not a lazy property (2026-08-01) - [StaticConstructorOnStartup] only
        // guarantees main-thread loading for work that actually happens INSIDE the static constructor; a
        // field left null until some later property access could still run whenever/wherever that first
        // happens, defeating the whole point of the attribute.
        private static readonly Texture2D Icon = ContentFinder<Texture2D>.Get("UI/Icon_ChangeAmmo", false) ?? BaseContent.BadTex;

        // Zwischenspeicher fuer den Statusblock unten - pro ausgewaehltem Pawn, eine halbe Sekunde.
        // Siehe dort, warum: dieser Postfix laeuft in JEDEM Bild, solange ein Kolonist ausgewaehlt ist.
        private static Pawn gizmoCachePawn;
        private static float gizmoCacheTime = -99f;
        private static Texture2D gizmoCacheIcon;
        private static Color? gizmoCacheDot;

        // Draws the same background/icon Command_Action already does, then an extra small corner dot in
        // this mod's existing FulfillColor convention (green/yellow/orange/red - see LoadoutUtility) when
        // the Postfix below was able to resolve one. Reuses that exact color logic rather than inventing a
        // new one; see Postfix for why the dot (and the icon swap) only ever gets set for a single-pawn
        // selection.
        private class Command_ActionAmmoStatus : Command_Action
        {
            public Color? statusColor;

            public override void DrawIcon(Rect rect, Material buttonMat, GizmoRenderParms parms)
            {
                base.DrawIcon(rect, buttonMat, parms);
                if (!statusColor.HasValue) return;
                Rect dot = new Rect(rect.xMax - 15f, rect.y + 3f, 12f, 12f);
                Widgets.DrawBoxSolid(new Rect(dot.x - 1f, dot.y - 1f, dot.width + 2f, dot.height + 2f), new Color(0.08f, 0.08f, 0.08f, 0.95f));
                Widgets.DrawBoxSolid(dot, statusColor.Value);
            }
        }

        public static void Postfix(Pawn __instance, ref IEnumerable<Gizmo> __result)
        {
            if (__instance == null || !__instance.IsColonistPlayerControlled) return;
            Loadout template = AmmoAssignmentUtility.DisplayLoadout(__instance.GetLoadout());
            if (template == null || template.defaultLoadout) return;
            // Previously gated on "only once extra ammo is ALREADY set up" - but that made the gizmo the
            // only way to discover this feature impossible to ever trigger: a pawn with no customization
            // yet has zero extra ammo, so the gizmo hid itself right when a player would need it to add
            // the FIRST one. Shown for any pawn with a real loadout instead; Dialog_ChangeAmmo already
            // handles "nothing customized yet" cleanly (starts from the template's own defaults).
            Pawn pawn = __instance;

            // At-a-glance icon (2026-08-01): the pawn's primary weapon's own currently-picked concrete ammo
            // type, plus a stock-status corner dot, instead of the static generic icon - reusing
            // ComputeAmmoAvailability/PawnAlreadyHasItem/WeaponHasActiveBill/FulfillColor exactly as
            // Dialog_ChangeAmmo's own status dot does, not new logic. Deliberately restricted to a single
            // selected colonist whose primary weapon resolves to exactly ONE concrete ammo type right now:
            // - A pawn can carry several different ammo-consuming weapons (main + sidearms) with no single
            //   "the" ammo type to show - RelevantAmmoPool (see Dialog_ChangeAmmo) can be multi-valued, so a
            //   weapon on "Any ammo" or with 0/2+ concrete picks falls back to the old static icon rather
            //   than guessing.
            // - Multi-pawn selections fall back too: RimWorld's own Command.GroupsWith (decompiled Verse.
            //   Command) requires identical `icon` for gizmos from different pawns to merge into one button
            //   - the exact mechanism the action's own IsOpen-guard comment below relies on for "one click,
            //   one dialog, whole selection". Varying icon per pawn would fragment that merge into one
            //   button per distinct ammo type/pawn; varying only the dot (via a shared static icon) would
            //   still merge, but the grid only ever draws ONE representative gizmo per merged group, so the
            //   dot would silently reflect just one arbitrary pawn's status while claiming to speak for the
            //   whole selection. Neither is worth it for a glance-icon - single-pawn selection is the common
            //   case this is actually useful for, and every other case is pixel-identical to before.
            Texture2D gizmoIcon = Icon;
            Color? statusDot = null;
            if (Find.Selector.SelectedPawns.Count(p => p != null && p.IsColonistPlayerControlled) == 1)
            {
                // GECACHT (2026-08-13, in einer Messung als teuerster Posten im NORMALEN Spiel aufgefallen).
                // Dieser Block sitzt in Pawn.GetGizmos, und RimWorlds GizmoGridDrawer
                // baut die Knopfleiste in JEDEM Bild neu - solange also ein Kolonist ausgewaehlt ist, lief
                // hier pro Bild: Slots-Union des Pawns (CEs Getter baut sie jedes Mal neu), zwei LINQ-Ketten,
                // und vor allem ComputeAmmoAvailability, dessen eigener Cache am Bildzaehler haengt und
                // deshalb pro Bild neu rechnet - inklusive Kartendurchlauf und einer Schleife ueber ALLE
                // Kolonisten. Das ist der Zustand beim normalen Spielen, ohne dass ein Mod-Fenster offen ist.
                // Eine halbe Sekunde ist hier reichlich: das Ergebnis ist ein Symbol und ein Farbpunkt.
                if (Time.realtimeSinceStartup - gizmoCacheTime > 0.5f || gizmoCachePawn != pawn)
                {
                    gizmoCachePawn = pawn;
                    gizmoCacheTime = Time.realtimeSinceStartup;
                    gizmoCacheIcon = null;
                    gizmoCacheDot = null;
                    ThingDef primary = pawn.equipment?.Primary?.def;
                    List<ThingDef> family = primary != null ? LoadoutUtility.GetAmmoTypesFor(primary).ToList() : null;
                    if (family != null && family.Count > 0)
                    {
                        List<LoadoutSlot> effective = pawn.GetLoadout()?.Slots;
                        List<ThingDef> chosen = effective == null ? new List<ThingDef>()
                            : effective.Where(s => s.thingDef != null && family.Contains(s.thingDef))
                                .Select(s => s.thingDef).Distinct().ToList();
                        if (chosen.Count == 1)
                        {
                            ThingDef ammoDef = chosen[0];
                            if (ammoDef.uiIcon != null) gizmoCacheIcon = ammoDef.uiIcon;
                            int available = LoadoutUtility.ComputeAmmoAvailability(ammoDef, out _, out _);
                            FulfillStatus status = LoadoutUtility.PawnAlreadyHasItem(pawn, ammoDef) ? FulfillStatus.Equipped
                                : available > 0 ? FulfillStatus.Stored
                                : LoadoutUtility.WeaponHasActiveBill(ammoDef) ? FulfillStatus.InProduction
                                : FulfillStatus.Missing;
                            gizmoCacheDot = LoadoutUtility.FulfillColor(status);
                        }
                    }
                }
                if (gizmoCacheIcon != null) gizmoIcon = gizmoCacheIcon;
                statusDot = gizmoCacheDot;
            }

            Command_ActionAmmoStatus cmd = new Command_ActionAmmoStatus
            {
                defaultLabel = "CER_PawnChangeAmmoGizmo_Label".Translate(),
                defaultDesc = "CER_PawnChangeAmmoGizmo_Desc".Translate(),
                icon = gizmoIcon,
                statusColor = statusDot,
                // RimWorld merges identical gizmos across a multi-selection and fires EACH selected pawn's
                // own action on a single click (the same mechanism vanilla Draft/Attack rely on for "one
                // click affects everyone selected"). Rather than stack one Dialog_ChangeAmmo per pawn, this
                // collapses the WHOLE current selection into one call - and the IsOpen guard below means
                // only the first of the (possibly several) merged invocations this click actually opens a
                // window; the rest are no-ops.
                action = () =>
                {
                    if (Find.WindowStack.IsOpen(typeof(Dialog_ChangeAmmo))) return;
                    List<Pawn> selected = Find.Selector.SelectedPawns
                        .Where(p => p != null && p.IsColonistPlayerControlled)
                        .Where(p => { Loadout t = AmmoAssignmentUtility.DisplayLoadout(p.GetLoadout()); return t != null && !t.defaultLoadout; })
                        .ToList();
                    if (selected.Count == 0) selected.Add(pawn);
                    Find.WindowStack.Add(new Dialog_ChangeAmmo(selected));
                }
            };
            __result = __result.Concat(new[] { (Gizmo)cmd });
        }
    }
}
