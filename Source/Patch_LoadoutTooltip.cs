using System;
using System.Linq;
using System.Collections.Generic;
using RimWorld;
using Verse;
using HarmonyLib;
using CombatExtended;
using UnityEngine;

namespace CEReloaded
{
    [HarmonyPatch(typeof(FloatMenu), MethodType.Constructor, new Type[] { typeof(List<FloatMenuOption>) })]
    public static class Patch_FloatMenu
    {
        public static void Prefix(List<FloatMenuOption> options)
        {
            if (options != null)
            {
                var loadouts = CombatExtended.LoadoutManager.Loadouts;
                if (loadouts == null) return;

                // This prefix runs for EVERY float menu in the game (right-click orders included), so the
                // label match is one dictionary build instead of options x loadouts scans.
                var byLabel = new Dictionary<string, Loadout>();
                foreach (var l in loadouts)
                    if (l != null && l.LabelCap != null && !byLabel.ContainsKey(l.LabelCap)) byLabel[l.LabelCap] = l;

                // Equipment Packs are ingredient building blocks, never a complete loadout a pawn can be
                // directly assigned AS THEIR MAIN WEAPON LOADOUT - strip them out of CE's own native
                // "Current" column loadout-picker specifically (CurrentDoCellPawn set - see the third filter
                // below for the exact same gating and its own reasoning). Used to run unconditionally for
                // EVERY FloatMenu in the game, which also stripped Equipment Packs out of the generic
                // "Select/Load/Delete loadout..." management dialog (CE's own loadout browser) where seeing
                // and picking one is exactly the point - packs were repeatedly reported missing from
                // that picker before this was scoped down to just the one context where hiding them is
                // actually correct.
                if (Patch_LoadoutTooltip.CurrentDoCellPawn != null)
                {
                    options.RemoveAll(opt => opt.Label != null && byLabel.TryGetValue(opt.Label, out Loadout packOpt)
                        && EquipmentPackUtility.IsEquipmentPackLoadout(packOpt));

                    // Die Markierung an die AKTION haengen, nicht an den Frame (2026-08-09).
                    //
                    // Der Anker-Schutz in Patch_Utility_Loadouts_SetLoadout fragt CurrentDoCellPawn ab, um sich
                    // aus CEs eigenem Dropdown herauszuhalten. Das funktionierte hier nie: CEs DoCell ruft
                    // SetLoadout nicht selbst auf, es BAUT nur dieses Menue. Geklickt wird einen Frame spaeter
                    // aus dem FloatMenu-Fenster heraus - da ist CurrentDoCellPawn laengst wieder null.
                    // Folge: wer ueber CEs Dropdown ein Loadout waehlte, landete im Anker-Umbiegen-Pfad und
                    // schleppte Packs, Zweitwaffen und persoenliche Munition mit - exakt das Gegenteil dessen,
                    // was dieser Schutz erreichen soll ("wenn ich im Vanilla-Editor etwas aendere, erwarte ich
                    // nicht, Leichen mitzuziehen").
                    // Ein Frame-Flag kann das nicht loesen. Die Aktion selbst ist der einzige Zeitpunkt, der
                    // zuverlaessig "jetzt laeuft eine Auswahl aus CEs Dropdown" bedeutet - also wird sie
                    // umschlossen. finally, damit eine Ausnahme in CEs Code das Flag nicht dauerhaft stehen
                    // laesst (dieselbe Vorsichtsmassnahme wie bei InsideVanillaLoadoutEditor).
                    foreach (FloatMenuOption opt in options)
                    {
                        if (opt?.action == null) continue;
                        Action inner = opt.action;
                        opt.action = () =>
                        {
                            Patch_LoadoutTooltip.InsideNativeLoadoutPick = true;
                            try { inner(); }
                            finally { Patch_LoadoutTooltip.InsideNativeLoadoutPick = false; }
                        };
                    }
                }

                // Per-pawn hidden personal anchors (AmmoAssignmentUtility) are internal bookkeeping
                // objects, never something to hand-assign - same reasoning as Equipment Packs above.
                options.RemoveAll(opt => opt.Label != null && byLabel.TryGetValue(opt.Label, out Loadout ammoOpt)
                    && AmmoAssignmentUtility.IsAmmoLink(ammoOpt));

                // Bare "W|"-style Weapon Loadout TEMPLATES too, but ONLY in the exact FloatMenu CE's own
                // native "Manage loadouts" table opens (CurrentDoCellPawn is set - see Patch_LoadoutTooltip's
                // DoCell Prefix) - picking one directly there bypasses our own Assign Loadouts/Squad Setup
                // dialogs entirely (no pack/sidearm/ammo carried over) and silently resets the pawn to
                // template defaults. Deliberately scoped to JUST that menu: our own dialogs build plenty of
                // FloatMenus that need to show these same templates (the sidearm picker, "jump to
                // loadouts", ...), and a global strip would break every one of them. Roles and any hand-
                // baked "Save as reusable Loadout" object are unaffected - only gated on the SAME prefix the
                // player configured for generated templates, so this is a no-op if they turned it off.
                if (Patch_LoadoutTooltip.CurrentDoCellPawn != null)
                {
                    options.RemoveAll(opt => opt.Label != null && byLabel.TryGetValue(opt.Label, out Loadout wOpt)
                        && LoadoutUtility.IsGeneratedTemplate(wOpt));
                }

                // Availability coloring + tooltip - deliberately NOT shown inside CE's own native FloatMenus:
                // neither the native loadout browser (Dialog_ManageLoadouts' "Select/Delete/Parent loadout"
                // buttons, InsideVanillaLoadoutEditor) NOR the native per-pawn "Current" column quick-pick
                // (CurrentDoCellPawn set - both scoped the same way template-hiding above already is). Kept
                // only in this mod's OWN dialogs that list loadouts by name - expanded
                // 2026-07-30 from "just the loadout browser" to also cover the native quick-pick menu.
                if (!Patch_LoadoutTooltip.InsideVanillaLoadoutEditor && Patch_LoadoutTooltip.CurrentDoCellPawn == null)
                {
                    foreach (var opt in options)
                    {
                        if (opt.Label == null || !byLabel.TryGetValue(opt.Label, out Loadout matchedLoadout)) continue;
                        {
                            var originalAction = opt.mouseoverGuiAction;
                            opt.mouseoverGuiAction = rect => {
                                if (originalAction != null) originalAction(rect);

                                string tooltip = Patch_LoadoutTooltip.GetLoadoutTooltip(matchedLoadout, null);
                                if (!string.IsNullOrEmpty(tooltip))
                                {
                                    TooltipHandler.TipRegion(rect, new TipSignal(tooltip, matchedLoadout.GetHashCode() ^ 8472));
                                }
                            };

                            // Traffic-light label: can this loadout still be handed out? Green = main weapon in
                            // stock, yellow = exactly one left (the next assignment takes the last one), red =
                            // truly none anywhere. Since 2026-08-09 "do not pick up" loot counts toward that
                            // stock like anything else, so the old grey "none free but a forbidden copy exists"
                            // state no longer exists - it is now green/yellow plus the shared "!" marker, same
                            // convention every other stock display in the mod uses.
                            LoadoutUtility.LoadoutMainAndSide(matchedLoadout, out ThingDef mainW, out _);
                            if (mainW != null)
                            {
                                int avail = Patch_LoadoutTooltip.ComputeAvailability(mainW, out _, out _, out int forbidden);
                                string color = avail >= 2 ? "#7FD87F" : (avail == 1 ? "#F0D95E" : "#F07F6E");
                                opt.Label = LoadoutUtility.AppendForbiddenMark("<color=" + color + ">" + opt.Label + "</color>", forbidden);
                            }
                        }
                    }
                }

                // This exact FloatMenu - CE's own native "current loadout" picker - is the one place players
                // actually pick a loadout for a pawn day-to-day; our own Assign Loadouts/Squad Setup dialogs
                // already offer "Save current combo as Role..." in their own pick menus, but a player who
                // never opens those never sees it. Patch_LoadoutTooltip's DoCell Prefix stashes which pawn
                // this exact click came from (cleared again by its own Postfix right after) so this otherwise
                // pawn-blind FloatMenu constructor can still append the same action here.
                // Only offered when the pawn's assignment is actually the hidden "[Pawn-Loadout]" anchor - a
                // plain template (or no loadout at all, e.g. "Nothing") has nothing personalized worth
                // baking into a reusable Role. cellPawn.GetLoadout() != null alone isn't enough to gate this:
                // CE's own GetLoadout() returns the real, non-null default Loadout object even for "Nothing".
                Pawn cellPawn = Patch_LoadoutTooltip.CurrentDoCellPawn;

                // GetAmmoLeaf alone doesn't exclude a Role-assigned pawn (2026-08-01 fix) - a Role pawn also
                // has a personal anchor (AssignTemplate wraps every assignment in one), so this used to still
                // offer "Save combo as Role" on an already-Role pawn too, same doubled-name bug the other 3
                // entry points had - excluded the same way (DisplayLoadout-unwrap + RoleUtility.IsRoleLoadout).
                if (cellPawn != null && AmmoAssignmentUtility.GetAmmoLeaf(cellPawn) != null
                    && !RoleUtility.IsRoleLoadout(AmmoAssignmentUtility.DisplayLoadout(cellPawn.GetLoadout())))
                {
                    // No tabs window reachable from a native-UI patch like this one - open a fresh one,
                    // pre-filled with this pawn's combo as a Role Mode draft, same as every other "Save as
                    // Role" entry point now (review/rename/adjust before Create, not an instant silent save).
                    options.Add(new FloatMenuOption("CER_LoadoutTooltip_SaveComboAsRole".Translate(),
                        () =>
                        {
                            Dialog_CEReloadedTabs tabs = new Dialog_CEReloadedTabs();
                            tabs.LoadoutsDialog.StartRoleDraftFromPawn(cellPawn, AmmoAssignmentUtility.ComposedDisplayLabel(cellPawn));
                            tabs.JumpToLoadouts();
                            Find.WindowStack.Add(tabs);
                        }));
                }
            }
        }
    }

    [HarmonyPatch(typeof(CombatExtended.PawnColumnWorker_Loadout), "DoCell")]
    public static class Patch_LoadoutTooltip
    {
        // Which pawn's row DoCell is currently drawing/handling clicks for - set here, read by
        // Patch_FloatMenu.Prefix (which has no pawn parameter of its own), cleared again below once DoCell
        // returns. Safe as a plain static: DoCell runs synchronously and isn't reentrant across pawns, and
        // the FloatMenu this cell opens (if the button was clicked this frame) is always constructed nested
        // INSIDE this same DoCell call, while this field is still set.
        public static Pawn CurrentDoCellPawn;

        // Set by Patch_ManageLoadoutsDialog below (mirrors CurrentDoCellPawn's own pattern) - true for the
        // whole duration of CE's own native Dialog_ManageLoadouts.DoWindowContents, which is where its
        // "Select/Delete/Parent loadout" buttons build their FloatMenus. Read by Patch_FloatMenu.Prefix to
        // skip the availability coloring/tooltip specifically in THAT editor - see the field's own comment
        // there for why (user explicitly wanted it reverted only in CE's native loadout browser, kept
        // everywhere else this mod's own dialogs show a loadout-picking FloatMenu).
        public static bool InsideVanillaLoadoutEditor;

        // True nur waehrend die AKTION eines Menuepunkts aus CEs eigenem Loadout-Dropdown laeuft. Gesetzt in
        // Patch_FloatMenu, wo die Aktionen dieses Menues umschlossen werden - siehe die Begruendung dort.
        // Notwendig, weil CurrentDoCellPawn nur waehrend des ZEICHNENS gesetzt ist, der Klick aber einen
        // Frame spaeter kommt. Ein Frame-Flag kann diese Luecke prinzipiell nicht schliessen.
        public static bool InsideNativeLoadoutPick;

        public static void Prefix(Pawn pawn) => CurrentDoCellPawn = pawn;

        // ============ Alles, was eine Zelle pro Zeile braucht - einmal pro BILD (2026-08-17) ============
        //
        // Anlass: schon das Oeffnen des Vanilla-Assign-Reiters liess den Luefter hochdrehen.
        // Dieselbe Klasse Verschwendung, die frameRowCache in
        // Dialog_AssignLoadouts schon behoben hat, nur an der Stelle mit dem meisten Publikum - CEs eigene
        // Loadout-Spalte sitzt auf einem VANILLA-Reiter, und RimWorld zeichnet ein Fenster mindestens
        // zweimal pro Bild (Layout + Repaint).
        //
        // Pro Zeile lief bisher pro DURCHGANG: dreimal pawn.GetLoadout() (hier zweimal ueber
        // LoadoutButtonRect, einmal in Patch_LoadoutEditButton, das nochmal separat GetLoadout ruft),
        // dreimal HoldTrackerAnythingHeld, dreimal .Translate(), und einmal ComposedDisplayLabel - und das
        // ist der teure Posten: es haengt DisplayLoadout, GetPawnSidearms, GetPawnPacks und ExtraAmmoSlots
        // hintereinander, und jedes davon fasst die Inhaltsliste eines Loadouts an, die bei einem Kolonisten
        // mit Anker bei JEDEM Zugriff neu zusammengesetzt wird (Vorlage + Rolle + Packs + Zweitwaffen).
        //
        // Die Grenze ist bewusst genau ein Bild: eine Zuweisung, die der Spieler klickt, wird immer erst im
        // naechsten Bild gezeichnet - der Klick selbst laeuft aus dem FloatMenu-Fenster heraus, lange
        // nachdem diese Spalte fuer dieses Bild fertig ist.
        internal struct CellInfo
        {
            public Loadout assigned;
            public bool isAnchor;
            public bool isRole;
            public bool isOurs;
            // Das Urteil von Patch_LoadoutEditButton.ManagedByUs, aus denselben zwei Teilen gebaut:
            // IsAmmoLink(assigned) || IsRoleLoadout(assigned). Fuer einen Anker ist isAnchor bereits true,
            // fuer alles andere ist `real` identisch mit `assigned` - beide Wege geben dasselbe.
            public bool managedByUs;
            public bool held;
            public string label;
        }

        private static readonly Dictionary<Pawn, CellInfo> frameCellCache = new Dictionary<Pawn, CellInfo>();
        private static int frameCellStamp = -1;
        private static float frameRearmWidth;

        private static void EnsureFrame()
        {
            if (Time.frameCount == frameCellStamp) return;
            frameCellStamp = Time.frameCount;
            frameCellCache.Clear();
            // Pro Bild statt pro Zelle: GetWidthCached ist billig, .Translate() davor nicht ganz - und es
            // lief bis zu dreimal je Zeile. Nicht einmalig statisch, weil ein Sprachwechsel im laufenden
            // Spiel sonst eine Breite aus der alten Sprache behielte.
            frameRearmWidth = "CE_UpdateLoadoutNow".Translate().GetWidthCached();
        }

        internal static CellInfo GetCellInfo(Pawn pawn)
        {
            EnsureFrame();
            if (pawn == null) return new CellInfo();
            if (frameCellCache.TryGetValue(pawn, out CellInfo hit)) return hit;

            CellInfo info = new CellInfo();
            Loadout assigned = pawn.GetLoadout();
            info.assigned = assigned;
            if (assigned != null)
            {
                info.isAnchor = AmmoAssignmentUtility.IsAmmoLink(assigned);
                Loadout real = info.isAnchor ? AmmoAssignmentUtility.DisplayLoadout(assigned) : assigned;
                info.isRole = RoleUtility.IsRoleLoadout(real);
                info.isOurs = info.isAnchor || info.isRole || LoadoutUtility.IsGeneratedTemplate(real);
                info.managedByUs = info.isAnchor || info.isRole;
                // Nur wenn er ueberhaupt gezeichnet wird - ein Kolonist auf einem fremden, von Hand
                // gebauten Loadout laeuft hier nicht durch ComposedDisplayLabel.
                if (info.isOurs)
                    info.label = info.isAnchor
                        ? AmmoAssignmentUtility.ComposedDisplayLabel(pawn)
                        : LoadoutUtility.CellLabel(real);
            }
            info.held = HoldTrackerAnythingHeld(pawn);
            frameCellCache[pawn] = info;
            return info;
        }

        // Guarantees CurrentDoCellPawn resets even if the patched DoCell throws mid-render - without this,
        // an exception between Prefix and Postfix leaves the flag stuck true for the rest of the process
        // (2026-08-01: this field has no other reset path, so every FloatMenu built afterward
        // would keep reading a stale pawn). Runs after Postfix on the normal path too (harmless - resetting
        // an already-null field twice is a no-op) and never swallows the original exception.
        public static Exception Finalizer(Exception __exception)
        {
            CurrentDoCellPawn = null;
            return __exception;
        }

        public static void Postfix(Rect rect, Pawn pawn)
        {
            CurrentDoCellPawn = null;
            if (pawn == null) return;
            CellInfo info = GetCellInfo(pawn);
            Loadout loadout = info.assigned;
            // Einmal statt zweimal (Knopf und Tooltip benutzen denselben Bereich - das war zweimal dieselbe
            // Rechnung inklusive zweimal HoldTrackerAnythingHeld und zweimal .Translate()).
            Rect buttonRect = LoadoutButtonRect(rect, pawn, info);

            // CE's own DoCell draws pawn.GetLoadout()'s raw label - for a pawn on a hidden personal anchor
            // ("[Pawn-Loadout] 1003") that's this mod's internal bookkeeping name, never meant for a player to see
            // (the Assign-tab FloatMenu picker already hides these; this button's own label text did not).
            // Repainted here rather than patched at the source (Prefix + skip original) so CE's own
            // dropdown/Rearm/pencil-icon click handling stays completely untouched underneath - only the
            // text is corrected. Every loadout we assign now lives on a personal anchor (AssignTemplate),
            // whether or not it also carries a pack/sidearm/personal ammo, so this checks IsAmmoLink (any
            // anchor at all) rather than HasPersonalization - ComposedDisplayLabel already collapses back to
            // the plain template name on its own when there's nothing extra to show, so a bare anchored pawn
            // still reads exactly like CE's own stock UI.
            if (loadout != null)
            {
                // A normal button background (CE's own ButtonBGAtlas), not a flat solid-color fill (2026-08-01:
                // the old flat rectangle looked inconsistent next to every other button in the
                // game) - still fully opaque, so CE's own original label underneath ("[Pawn-Loadout] 1003", our
                // internal bookkeeping name) never bleeds through, same guarantee the old flat fill had.
                // Any loadout THIS MOD generated/manages - a Role, or a plain "W|" auto-generated Weapon
                // Loadout template - gets a blue tint on top (GUI.color multiplies the atlas texture), so a
                // glance at this column tells "ours" apart from a loadout the player built by hand in CE's
                // own native editor. Widened 2026-08-01 from Role-only after plain "W|"
                // templates still showed untinted - "our loadouts" meant everything this mod's own tooling
                // produces, not just Roles specifically. Two tiers, not one shared shade (2026-08-01,
                // same-day follow-up): once EVERY generated template also turned blue, Roles needed their
                // own noticeably stronger tint to still stand out from a plain "W|" template at a glance.
                //
                // isAnchor false means the pawn's assignment IS the raw Role/template object directly (picked
                // via CE's own native FloatMenu, bypassing AssignTemplate entirely) - gap found
                // (2026-08-01): Patch_LoadoutEditButton's own "ours" badge already detects this case via a
                // direct IsRoleLoadout(assigned) check, but this tint used to require IsAmmoLink first, so the
                // two mod-owned UI elements disagreed about the exact same loadout. Tint now applies either
                // way; the ComposedDisplayLabel relabel (hiding the internal "[Pawn-Loadout]" name) stays
                // anchor-only, since a raw Role/template's own label is already the real, player-facing name.
                // Die vier Urteile kommen seit 2026-08-17 aus GetCellInfo, damit sie im zweiten Durchgang
                // desselben Bildes nicht ein zweites Mal hergeleitet werden - inhaltlich unveraendert.
                if (info.isOurs)
                {
                    Rect btnRect = buttonRect;
                    // Die Blautoene aus LoadoutUtility statt nochmal hingeschriebener Zahlen (2026-08-16):
                    // hier standen dieselben zwei Farben ein zweites Mal, und ab dem Moment kann eine
                    // Aenderung nur noch halb ankommen. Die Bedingung darueber (isOurs) bleibt absichtlich
                    // eigenstaendig: sie faerbt auch einen ins Leere zeigenden Anker, damit CEs interner Name
                    // ("[Pawn-Loadout] 1003") in JEDEM Fall uebermalt wird - LoadoutTintFor gaebe dort null
                    // zurueck, und der Knopf zeigte wieder Buchhaltung.
                    GUI.color = info.isRole ? LoadoutUtility.LoadoutTintRole : LoadoutUtility.LoadoutTintWeapon;
                    Widgets.DrawAtlas(btnRect, Widgets.ButtonBGAtlas);
                    GUI.color = Color.white;
                    // The freshly drawn background above always covers CE's own original label - text must be
                    // repainted on top EVERY time isOurs is true, not just when isAnchor (2026-08-01: a Role
                    // assigned directly, bypassing AssignTemplate so isAnchor is false, showed
                    // a completely blank button - the background painted over CE's real label with nothing put
                    // back). isAnchor still gets the FULL composed bundle (template + packs + sidearm(s));
                    // anything else (raw Role or raw generated template, picked via CE's own native FloatMenu)
                    // just needs its own already-correct, player-facing label.
                    Text.Font = GameFont.Small;
                    Text.Anchor = TextAnchor.MiddleCenter;
                    // RoleUtility.DisplayLabel statt des rohen LabelCap (2026-08-14, zweimal aufgefallen): eine
                    // Rolle heisst intern "[Role] X", und dieses Praefix ist Buchhaltung, kein Name. Der
                    // Anker-Zweig daneben zeigte laengst den sauberen Namen (ComposedDisplayLabel raeumt ihn
                    // selbst weg) - nur wer seine Rolle ueber CEs EIGENES Dropdown in genau dieser Spalte
                    // gewaehlt hat, landet hier (isAnchor false) und sah "[Role] Assault ...". Dass es sich
                    // spaeter "von selbst" auflegte, passt dazu: sobald irgendetwas ueber unseren Weg lief,
                    // bekam der Kolonist einen Anker und damit den anderen Zweig.
                    // Nebenwirkung, die im Test ebenfalls auffiel: das Praefix frass Breite,
                    // der Name wurde also frueher abgeschnitten als bei den Nachbarn.
                    // CellLabel in BEIDEN Zweigen (2026-08-16): der Anker-Zweig raeumte den Namen ueber
                    // ComposedDisplayLabel laengst so auf, der andere nicht - derselbe Knopf, zwei
                    // Namensregeln. Und "Zelle" ist hier richtig: die Faerbung eine Zeile darueber sagt die
                    // Art schon, die Vorsilbe waere Wiederholung auf der schmalsten Spalte des Spiels.
                    string label = info.label ?? "";
                    Widgets.Label(btnRect, label.Truncate(btnRect.width - 4f));
                    Text.Anchor = TextAnchor.UpperLeft;
                    GUI.color = Color.white;
                }
            }

            // TipWanted zuerst (2026-08-13, in einer Messung die heisseste Tooltip-Stelle des ganzen Mods):
            // das hier sitzt in CEs eigener Loadout-Spalte, also auf einem VANILLA-Reiter mit
            // vielen sichtbaren Zeilen - und GetLoadoutTooltip baut pro Zeile pro Bild den vollen Text, ruft
            // dabei ComposedDisplayLabel ein zweites Mal (das erste steckt im Knopf darueber), macht drei
            // volle String-Ersetzungen und ruft pro Waffe das ungecachte WeaponQueuedCount, das jeden
            // Auftragsstapel jeder Werkbank durchgeht. Acht eigene Fenster haben diese Wache schon; die
            // Stelle mit dem meisten Publikum hatte sie nicht.
            if (LoadoutUtility.TipWanted(buttonRect))
            {
                string tooltip = GetLoadoutTooltip(loadout, pawn);
                // Cover ONLY the loadout dropdown button, not CE's Rearm button and edit pencil to its right -
                // hovering those has nothing to do with our loadout info. Mirrors the rect math CE uses for that
                // button inside PawnColumnWorker_Loadout.DoCell (its "rect3").
                if (!string.IsNullOrEmpty(tooltip))
                    TooltipHandler.TipRegion(buttonRect, new TipSignal(tooltip, pawn.GetHashCode() * 777));
            }
        }

        // The sub-rect CE draws the loadout selection dropdown in: the cell minus the Rearm button, the edit
        // (pencil) icon, and - when the pawn is holding forced items - the clear (x) icon. Kept in lockstep with
        // CE's DoCell; if that layout ever changes the tooltip just covers a slightly wrong span, never worse than
        // before. IconSize (16) is CE's own constant for those trailing icons.
        // CombatExtended.Utility_HoldTracker is internal, so the "anything held?" check goes through reflection
        // (resolved once), same as Dialog_AssignLoadouts does for HoldTrackerForget.
        // Einmal in einen ECHTEN Delegaten aufgeloest statt pro Aufruf ueber MethodInfo.Invoke (2026-08-14,
        // Befund 7 der zweiten Performance-Runde). Der MethodInfo war schon vorher nur einmal gesucht - der
        // teure Teil war die INVOCATION: reflektierter Aufruf plus ein frisches `new object[1]` pro Aufruf,
        // und das bis zu dreimal pro Zeile pro Bild (LoadoutButtonRect wird fuer den Knopf und fuer den
        // Tooltip-Rect gerufen, dazu Patch_LoadoutEditButton) - in CEs eigener Loadout-Spalte, also auf
        // einem Vanilla-Reiter mit vielen sichtbaren Zeilen.
        // Delegate.CreateDelegate geht nur, weil die Methode statisch ist und genau (Pawn) -> bool hat; die
        // Pruefungen davor sind kein Zierrat, sondern der Grund, warum ein spaeteres CE-Update hier still
        // auf "false" faellt statt zu werfen.
        private static readonly System.Func<Pawn, bool> holdTrackerAnythingHeldFn = BuildHoldTrackerFn();

        private static System.Func<Pawn, bool> BuildHoldTrackerFn()
        {
            System.Reflection.MethodInfo mi = HarmonyLib.AccessTools.Method(
                "CombatExtended.Utility_HoldTracker:HoldTrackerAnythingHeld", new[] { typeof(Pawn) });
            if (mi == null || !mi.IsStatic || mi.ReturnType != typeof(bool)) return null;
            try { return (System.Func<Pawn, bool>)System.Delegate.CreateDelegate(typeof(System.Func<Pawn, bool>), mi); }
            catch { return null; }
        }

        // Internal, not private - Patch_LoadoutEditButton reuses this same reflected lookup to reproduce
        // PawnColumnWorker_Loadout.DoCell's own pencil-icon rect math, rather than resolving the method twice.
        internal static bool HoldTrackerAnythingHeld(Pawn p)
        {
            if (holdTrackerAnythingHeldFn == null) return false;
            // try/catch bleibt: gefangen wird jetzt nur noch, was die AUFGERUFENE Methode selbst wirft -
            // vorher war hier zusaetzlich jeder Reflexionsfehler mit drin.
            try { return holdTrackerAnythingHeldFn(p); }
            catch { return false; }
        }

        // Nimmt "haelt der Kolonist etwas fest" und die Breite des Rearm-Knopfes aus dem Bild-Cache statt
        // beides pro Aufruf neu zu holen (2026-08-17) - die Rechnung selbst ist unveraendert.
        internal static Rect LoadoutButtonRect(Rect rect, Pawn pawn, CellInfo info)
        {
            const float iconSize = 16f;
            int num = Mathf.FloorToInt(rect.width - 4f - iconSize);
            Rect rect2 = new Rect(rect.x, rect.y + 2f, num, rect.height - 4f);
            if (info.held) rect2.width -= 4f + iconSize;
            if (!pawn.Spawned) return rect2;
            Rect rect3 = rect2.LeftPartPixels(Mathf.Max(0f, rect2.width - frameRearmWidth - 16f));
            rect3.xMax -= 2f;
            return rect3;
        }
        
        // Free copies of a weapon: stored (not carried by anyone) minus copies already promised to colonists whose
        // loadout demands one they don't carry yet. Shared by the availability tooltip and the float-menu coloring.
        // forbiddenCount is a SUBSET of storedCount, not an alternative to it (changed 2026-08-09 - it used to
        // be the opposite, an "informational only" number for stock this function deliberately skipped). Loot
        // marked "do not pick up" now counts as stock everywhere in the mod; this figure exists so callers can
        // put the shared "!" marker on the display and name the amount in their tooltip. NEVER subtract it
        // from storedCount anywhere - that would undo the whole change and double-count the loot as missing.
        // Empire-wide: sums stock/demand across every colony map, not just whichever one is currently in view -
        // see LoadoutUtility.PlayerMaps/AllColonyPawns for why (a bench or colonist on a different map is still
        // part of the same colony).
        //
        // Cached per ThingDef for a short TTL - this scans every colony map's things AND every colonist per
        // call, and callers like Dialog_PickPacks' Sidearm picker call it once PER ROW PER FRAME (~40 rows
        // in a normal colony) with no caching of their own. Uncached, that's a full empire-wide rescan 40x
        // every single frame the popup is open - the exact "very laggy" report this fixes. Same 1s TTL
        // convention already used by LoadoutUtility's own weaponInvCache/activeBillCache.
        private struct AvailResult { public int avail, stored, demanded, forbidden; }
        private static readonly Dictionary<ThingDef, AvailResult> availCache = new Dictionary<ThingDef, AvailResult>();
        private static float availCacheTime = -99f;

        // extraReserved: copies already spoken for by a PENDING, not-yet-applied plan (e.g. Dialog_SquadSetup's
        // live Assign Loadouts preview) that real committed game state (pawn.GetLoadout()) doesn't know about
        // yet - NOT part of the cache key, since it's caller/context-specific, not a colony-wide fact. Default
        // 0 keeps every other caller (CE's own native FloatMenu patch, the plain per-pawn gear tooltip, ...)
        // byte-for-byte unchanged. Added 2026-07-31: the manual weapon picker used to show a scarce weapon as
        // fully "available" even while THIS SAME live preview had already planned it for a different
        // colonist, since only real assignments were ever counted - a player picking it for a second pawn saw
        // no warning at all that they'd just have both aiming for the one copy that actually exists.
        public static int ComputeAvailability(ThingDef wDef, out int storedCount, out int demandedFromStorage, out int forbiddenCount, int extraReserved = 0)
        {
            if (Time.realtimeSinceStartup - availCacheTime > 1f)
            {
                availCache.Clear();
                availCacheTime = Time.realtimeSinceStartup;
            }
            int baseAvail, baseDemanded;
            if (wDef != null && availCache.TryGetValue(wDef, out AvailResult cached))
            {
                storedCount = cached.stored; baseDemanded = cached.demanded; forbiddenCount = cached.forbidden;
                baseAvail = cached.avail;
            }
            else
            {
                baseAvail = ComputeAvailabilityUncached(wDef, out storedCount, out baseDemanded, out forbiddenCount);
                if (wDef != null) availCache[wDef] = new AvailResult { avail = baseAvail, stored = storedCount, demanded = baseDemanded, forbidden = forbiddenCount };
            }
            if (extraReserved <= 0) { demandedFromStorage = baseDemanded; return baseAvail; }
            demandedFromStorage = baseDemanded + extraReserved;
            int adjusted = storedCount - demandedFromStorage;
            return adjusted < 0 ? 0 : adjusted;
        }

        private static int ComputeAvailabilityUncached(ThingDef wDef, out int storedCount, out int demandedFromStorage, out int forbiddenCount)
        {
            storedCount = 0;
            forbiddenCount = 0;
            foreach (Map map in LoadoutUtility.PlayerMaps())
            {
                List<Thing> groundWeapons = map.listerThings.ThingsOfDef(wDef);
                if (groundWeapons == null) continue;
                foreach (Thing t in groundWeapons)
                {
                    if (LoadoutUtility.IsHiddenInFog(t) || t.ParentHolder is Pawn_EquipmentTracker || t.ParentHolder is Pawn_InventoryTracker || t.ParentHolder is Pawn_CarryTracker)
                        continue;
                    // Loot marked "do not pick up" now counts as stock like anything else (2026-08-09, see
                    // LoadoutUtility.IsMarkedDoNotPickUp) - it used to be skipped here entirely, which is why a
                    // fresh raid's weapons read as "none available". forbiddenCount is still tracked, but as a
                    // SUBSET of storedCount now, not an alternative to it: it drives the "!" marker and the
                    // tooltip's own line, and must never be subtracted again anywhere.
                    if (LoadoutUtility.IsMarkedDoNotPickUp(t)) forbiddenCount += t.stackCount;
                    storedCount += t.stackCount;
                }
            }

            demandedFromStorage = 0;
            foreach (Pawn p in LoadoutUtility.AllColonyPawns())
            {
                bool carries = false;
                if (p.equipment != null && p.equipment.Primary != null && p.equipment.Primary.def == wDef) carries = true;
                else if (p.inventory != null && p.inventory.innerContainer != null && p.inventory.innerContainer.Any(t => t.def == wDef)) carries = true;
                else if (p.carryTracker != null && p.carryTracker.CarriedThing != null && p.carryTracker.CarriedThing.def == wDef) carries = true;
                // GETRAGENE KLEIDUNG zaehlt mit (2026-08-13). Ein CE-Schild ist
                // Apparel, kein Equipment und kein Inventar - es fiel durch alle drei Pruefungen darueber.
                // Fehlszenario: drei Nahkaempfer mit Shield Kit, alle drei tragen ihr Schild, keins im Lager.
                // Getragene Apparel ist nicht gespawnt, taucht also auch in listerThings nicht auf: Bestand 0,
                // zugewiesen ja, getragen (bisher) nein -> Bedarf 3 aus dem Lager, und die Kostenzeile schlug
                // vor, drei Schilde herzustellen, die laengst getragen werden.
                else if (p.apparel != null && p.apparel.WornApparel != null && p.apparel.WornApparel.Any(t => t.def == wDef)) carries = true;

                Loadout pLoadout = p.GetLoadout();
                bool assigned = pLoadout != null && pLoadout.Slots != null && pLoadout.Slots.Any(s => s.thingDef == wDef);

                if (assigned && !carries)
                    demandedFromStorage += 1;
            }

            int available = storedCount - demandedFromStorage;
            return available < 0 ? 0 : available;
        }

        // extraReservedFor: optional per-weapon lookup for copies a pending (not yet applied) plan has already
        // spoken for elsewhere - see ComputeAvailability's own comment. Null for every caller that has no such
        // plan (unchanged behavior); only Dialog_SquadSetup's live-preview picker supplies one.
        public static string GetLoadoutTooltip(Loadout loadout, Pawn pawn = null, Func<ThingDef, int> extraReservedFor = null)
        {
            string tooltipText = "";
            if (pawn != null)
            {
                string shootingStr = "0";
                if (pawn.skills != null && pawn.skills.GetSkill(SkillDefOf.Shooting) != null)
                    shootingStr = pawn.skills.GetSkill(SkillDefOf.Shooting).TotallyDisabled ? "-" : pawn.skills.GetSkill(SkillDefOf.Shooting).Level.ToString();
                    
                string meleeStr = "0";
                if (pawn.skills != null && pawn.skills.GetSkill(SkillDefOf.Melee) != null)
                    meleeStr = pawn.skills.GetSkill(SkillDefOf.Melee).TotallyDisabled ? "-" : pawn.skills.GetSkill(SkillDefOf.Melee).Level.ToString();
                    
                string equipped = (pawn.equipment != null && pawn.equipment.Primary != null) ? pawn.equipment.Primary.LabelCap.ToString() : "CER_LoadoutTooltip_NoneEquipped".Translate().ToString();

                // The FULL bundle, not the raw assignment's own label - a pawn on a hidden anchor would
                // otherwise show its internal "[Pawn-Loadout] <id>" name here instead of anything a player recognizes.
                string curLoadoutName = AmmoAssignmentUtility.ComposedDisplayLabel(pawn);

                // NBSP substitution runs AFTER translation, over the whole composed phrase (template text
                // included) - keeps the exact same "no mid-phrase wrap" behavior the original hardcoded
                // literals had, without baking non-breaking spaces into the translation file itself.
                string curAssignStr = "CER_LoadoutTooltip_CurrentlyAssigned".Translate(curLoadoutName).ToString().Replace(" ", "\u00A0");
                string curEquipStr = "CER_LoadoutTooltip_CurrentlyEquipped".Translate(equipped).ToString().Replace(" ", "\u00A0");
                string combatSkillsStr = "CER_LoadoutTooltip_CombatSkills".Translate(shootingStr, meleeStr).ToString().Replace(" ", "\u00A0");
                
                tooltipText = curAssignStr + "\n" + curEquipStr + "\n\n" + combatSkillsStr;
            }

            if (loadout != null && loadout.Slots != null)
            {
                List<ThingDef> weaponsInLoadout = new List<ThingDef>();
                foreach (LoadoutSlot slot in loadout.Slots)
                {
                    if (slot.thingDef != null && slot.thingDef.IsWeapon)
                    {
                        if (!weaponsInLoadout.Contains(slot.thingDef))
                        {
                            weaponsInLoadout.Add(slot.thingDef);
                        }
                    }
                }

                if (weaponsInLoadout.Count > 0)
                {
                    if (!string.IsNullOrEmpty(tooltipText)) tooltipText += "\n\n";
                    tooltipText += "CER_LoadoutTooltip_AvailabilityHeader".Translate();

                    foreach (ThingDef wDef in weaponsInLoadout)
                    {
                        int extra = extraReservedFor?.Invoke(wDef) ?? 0;
                        int available = ComputeAvailability(wDef, out int storedCount, out int demandedFromStorage, out int forbiddenCount, extra);

                        string line = "CER_LoadoutTooltip_AvailabilityLine".Translate(wDef.label, available.ToString(), storedCount.ToString(), demandedFromStorage.ToString());

                        // A shortfall that is already being crafted is not a real problem - account for queued bills so
                        // weapons in production are not flagged in alarming red.
                        int shortfall = demandedFromStorage - storedCount;
                        if (available == 0 && shortfall > 0)
                        {
                            int queued = LoadoutUtility.WeaponQueuedCount(wDef);
                            if (queued >= shortfall) line += "CER_LoadoutTooltip_InProduction".Translate(queued.ToString());
                            else if (queued > 0) line += "CER_LoadoutTooltip_InProductionMissing".Translate(queued.ToString(), (shortfall - queued).ToString());
                            line = line.Replace(" ", "\u00A0");
                            // Blue when production fully covers the gap, yellow when it partly does, red when nothing is queued.
                            string color = queued >= shortfall ? "#8FCBF0" : (queued > 0 ? "#F0D95E" : "red");
                            line = "<color=" + color + ">" + line + "</color>";
                        }
                        else
                        {
                            line = line.Replace(" ", "\u00A0");
                        }
                        // Counted as stock since 2026-08-09 (see LoadoutUtility.IsMarkedDoNotPickUp) - this
                        // suffix says how much of the number just shown is still marked "do not pick up", it
                        // is NOT an extra amount on top. The key's own wording was changed to match.
                        if (forbiddenCount > 0)
                            line += ("\u00A0\u00A0<color=#B0A080>" + "CER_LoadoutTooltip_ForbiddenNearby".Translate(forbiddenCount.ToString()) + "</color>").Replace(" ", "\u00A0");
                        tooltipText += "\n" + line;
                    }
                }
            }
            
            return tooltipText.Trim();
        }
    }

    // Marks the whole duration of CE's own native loadout browser's DoWindowContents (Select/Delete/
    // Parent-loadout buttons all build their FloatMenus inline in this one method, decompiled source
    // confirmed - no smaller named method to target instead). Same Prefix/Postfix bracket pattern
    // Patch_LoadoutTooltip's own CurrentDoCellPawn already uses.
    [HarmonyPatch(typeof(CombatExtended.Dialog_ManageLoadouts), "DoWindowContents")]
    public static class Patch_ManageLoadoutsDialog
    {
        public static void Prefix() => Patch_LoadoutTooltip.InsideVanillaLoadoutEditor = true;
        public static void Postfix() => Patch_LoadoutTooltip.InsideVanillaLoadoutEditor = false;

        // Same stuck-flag risk as Patch_LoadoutTooltip's own CurrentDoCellPawn (2026-08-01) - if
        // DoWindowContents throws, Postfix never runs and InsideVanillaLoadoutEditor stays true forever,
        // permanently suppressing availability coloring/tooltips in every OTHER FloatMenu this mod builds.
        public static Exception Finalizer(Exception __exception)
        {
            Patch_LoadoutTooltip.InsideVanillaLoadoutEditor = false;
            return __exception;
        }
    }
}
