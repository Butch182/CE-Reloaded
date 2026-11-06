using System.Linq;
using RimWorld;
using Verse;
using HarmonyLib;
using UnityEngine;
using CombatExtended;

namespace CEReloaded
{
    // Intercepts CE's own native "edit loadout" pencil button (PawnColumnWorker_Loadout.DoCell, the
    // "Manage loadouts" policy table) - but ONLY for a pawn whose assignment is actually managed by us: a
    // Role (shared, baked-together contents) or a personal anchor ("[Pawn-Loadout]" - every loadout our own
    // system assigns lives on one of these, see AmmoAssignmentUtility.AssignTemplate). For those, jump
    // straight to our own Assign Loadouts tab, pre-filtered to this pawn - CE's own flat editor would either
    // show a raw internal anchor object's confusing contents, or silently let the player edit a Role in
    // place (affecting every OTHER colonist on it) with no warning. A plain loadout never touched by our
    // system (hand-picked via CE's own dropdown, still a genuinely bare Loadout - see Patch_SetLoadoutAnchor)
    // is left to CE's native editor untouched - there's nothing of ours to jump to there.
    [HarmonyPatch(typeof(CombatExtended.PawnColumnWorker_Loadout), "DoCell")]
    public static class Patch_LoadoutEditButton
    {
        // Runs BEFORE the original DoCell, which is where CE's own pencil-icon Widgets.ButtonImage actually
        // detects the click. Consuming the mouse-up event here (Event.current.Use()) when it lands inside
        // the pencil icon's own rect means the original's later Mouse.IsOver/click check never fires for
        // it - the icon still draws normally, but nothing opens from it once we've already handled the
        // click ourselves. Everything else in DoCell (Rearm, the loadout dropdown, held-item clear icon)
        // is completely untouched.
        public static void Prefix(Rect rect, Pawn pawn)
        {
            // Die Ereignispruefung ZUERST (2026-08-17): sie ist die billigste von
            // allen und in jedem Bild ausser dem einen Klick-Bild schon das Ende. Vorher stand darueber ein
            // pawn.GetLoadout() plus ManagedByUs pro Zeile pro Durchgang - Arbeit fuer eine Frage, die sich
            // fast immer mit einem Feldvergleich beantworten laesst.
            if (Event.current == null || Event.current.type != EventType.MouseUp) return;
            if (pawn?.outfits == null || !pawn.Spawned) return;
            Patch_LoadoutTooltip.CellInfo info = Patch_LoadoutTooltip.GetCellInfo(pawn);
            if (!info.managedByUs) return;

            // DIE ZELLE SELBST (2026-09-04): ein Klick auf den Loadout-Namen oeffnet unser Waffenfenster
            // statt CEs Aufklappmenue "Nothing / [Role] ... / Save current combo as Role". Der Lazy-Umbau
            // hat diesen Weg ueberall sonst laengst ersetzt; hier war er stehengeblieben, was in mehreren
            // Tests aufgefallen ist.
            //
            // Warum das keine Verarmung ist: das Waffenfenster kann alles, was das Menue konnte, und mehr -
            // "Keins" steht als oberste Zeile, die Rollen stehen als eigene Sektion, dazu Wertung, Bestand,
            // Gewicht und die Waffen selbst. Der einzige Menuepunkt ohne Entsprechung ist "Save current
            // combo as Role", und der gehoert bewusst nicht ins Auswahlfenster (2026-09-03); er lebt weiter
            // auf den Karten und
            // Kompaktzeilen des Reiters "Kolonisten ausruesten".
            //
            // Wie beim Stift: nur fuer Kolonisten, deren Zuweisung WIRKLICH von uns verwaltet wird, und
            // nur durch Verbrauch des MouseUp-Ereignisses - CE zeichnet die Zelle unveraendert, sie oeffnet
            // danach nur etwas anderes. Ein Kolonist auf einem von Hand gebauten CE-Loadout behaelt CEs
            // eigenes Menue.
            Rect cellRect = Patch_LoadoutTooltip.LoadoutButtonRect(rect, pawn, info);
            if (Mouse.IsOver(cellRect))
            {
                Event.current.Use();
                Dialog_AssignLoadouts.OpenWeaponPickerFor(pawn);
                return;
            }

            Rect editRect = EditButtonRect(rect, pawn, info);
            if (!Mouse.IsOver(editRect)) return;

            Event.current.Use();
            // Reuse an already-open tabs window instead of stacking a second, desynced one on top
            // (2026-08-02 fix: this and Patch_MainTabWindow's own shortcut button both used to
            // unconditionally build a brand new Dialog_CEReloadedTabs, exactly the class of bug
            // Dialog_LoadoutEditor's own "Save as Role" button already hit and fixed once before - two
            // independent windows with their own separate Dialog_AssignLoadouts/Dialog_GenerateLoadouts
            // instances that don't share state).
            Dialog_CEReloadedTabs tabs = Find.WindowStack.Windows.OfType<Dialog_CEReloadedTabs>().FirstOrDefault();
            if (tabs != null) { tabs.JumpToAssign(pawn.LabelShort); return; }
            tabs = new Dialog_CEReloadedTabs();
            tabs.JumpToAssign(pawn.LabelShort);
            Find.WindowStack.Add(tabs);
        }

        // A small colored corner badge instead of CE's own plain gray pencil - visible confirmation that
        // THIS pencil jumps to our own Assign Loadouts tab, not CE's flat editor. Drawn before the original
        // icon (which paints on top), so it only shows in a corner the icon's own graphic doesn't cover.
        public static void Postfix(Rect rect, Pawn pawn)
        {
            if (pawn?.outfits == null || !pawn.Spawned) return;
            Patch_LoadoutTooltip.CellInfo info = Patch_LoadoutTooltip.GetCellInfo(pawn);
            if (info.managedByUs)
            {
                Rect editRect = EditButtonRect(rect, pawn, info);
                Rect dot = new Rect(editRect.xMax - 6f, editRect.y - 2f, 7f, 7f);
                Widgets.DrawBoxSolid(new Rect(dot.x - 1f, dot.y - 1f, dot.width + 2f, dot.height + 2f), new Color(0.08f, 0.08f, 0.08f, 0.95f));
                Widgets.DrawBoxSolid(dot, new Color(0.45f, 0.75f, 1f));
                // TipWanted davor (2026-08-17): dieser eine Text wurde pro Zeile pro Durchgang uebersetzt und
                // registriert, obwohl hoechstens EINE Zeile pro Bild unter der Maus liegt. Dieselbe Wache, die
                // der Loadout-Tooltip zwei Zeilen weiter oben seit dem 13.08. hat - hier fehlte sie noch.
                if (LoadoutUtility.TipWanted(editRect))
                    TooltipHandler.TipRegion(editRect, "CER_LoadoutEditButton_Tip".Translate());
            }
        }

        // Reproduces PawnColumnWorker_Loadout.DoCell's own layout math for the pencil "edit" button's rect -
        // literal port (IconSize=16; HoldTrackerAnythingHeld shifts it right by one icon-width+gap when the
        // pawn has forced-held items, same as CE's own held-item clear icon does).
        private static Rect EditButtonRect(Rect rect, Pawn pawn, Patch_LoadoutTooltip.CellInfo info)
        {
            const float iconSize = 16f;
            int num = Mathf.FloorToInt(rect.width - 4f - iconSize);
            int num2 = Mathf.FloorToInt(iconSize);
            float x = rect.x;
            float y = rect.y + (rect.height - iconSize) / 2f;
            bool held = info.held;
            Rect rect2 = new Rect(x, rect.y + 2f, num, rect.height - 4f);
            if (held) rect2.width -= 4f + num2;
            x += rect2.width + 4f;
            if (held) x += num2 + 4f;
            return new Rect(x, y, num2, num2);
        }
    }
}
