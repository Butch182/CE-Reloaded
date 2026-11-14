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
                // and picking one is exactly the point - packs went missing from
                // that picker before this was scoped down to just the one context where hiding them is
                // actually correct.
                if (Patch_LoadoutTooltip.CurrentDoCellPawn != null)
                {
                    options.RemoveAll(opt => opt.Label != null && byLabel.TryGetValue(opt.Label, out Loadout packOpt)
                        && EquipmentPackUtility.IsEquipmentPackLoadout(packOpt));

                    // Attach the flag to the ACTION, not to the frame.
                    //
                    // The anchor guard in Patch_Utility_Loadouts_SetLoadout checks CurrentDoCellPawn to stay
                    // out of CE's own dropdown. That never worked here: CE's DoCell does not call SetLoadout
                    // itself, it only BUILDS this menu. The click happens one frame later from inside the
                    // FloatMenu window - by then CurrentDoCellPawn is long back to null.
                    // Consequence: picking a loadout via CE's dropdown ended up in the anchor-redirect path and
                    // dragged packs, sidearms and personal ammo along - the exact opposite of what this guard
                    // is meant to achieve (a change made in the vanilla editor should not carry over stale
                    // extras).
                    // A frame flag cannot solve this. The action itself is the only moment that reliably
                    // means "a selection from CE's dropdown is running now" - so it gets wrapped. finally,
                    // so that an exception in CE's code doesn't leave the flag set permanently (same
                    // precaution as for InsideVanillaLoadoutEditor).
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

        // True only while the ACTION of a menu entry from CE's own loadout dropdown is running. Set in
        // Patch_FloatMenu, where this menu's actions are wrapped - see the reasoning there.
        // Needed because CurrentDoCellPawn is only set while DRAWING, but the click comes one frame
        // later. A frame flag fundamentally cannot close that gap.
        public static bool InsideNativeLoadoutPick;

        public static void Prefix(Pawn pawn) => CurrentDoCellPawn = pawn;

        // ============ Everything a cell needs per row - once per FRAME ============
        //
        // Reason: merely opening the vanilla Assign tab already drove CPU load and fan noise up.
        // The same class of waste that frameRowCache already fixed in
        // Dialog_AssignLoadouts, just at the place with the largest audience - CE's own
        // loadout column sits on a VANILLA tab, and RimWorld draws a window at least
        // twice per frame (Layout + Repaint).
        //
        // Per row and per PASS this used to run: pawn.GetLoadout() three times (twice here via
        // LoadoutButtonRect, once in Patch_LoadoutEditButton, which calls GetLoadout separately again),
        // HoldTrackerAnythingHeld three times, .Translate() three times, and ComposedDisplayLabel once - and
        // that is the expensive item: it chains DisplayLoadout, GetPawnSidearms, GetPawnPacks and
        // ExtraAmmoSlots, and each of those touches a loadout's content list, which for a colonist with an
        // anchor is reassembled on EVERY access (template + role + packs + sidearms).
        //
        // The boundary is deliberately exactly one frame: an assignment the player clicks is always only
        // drawn on the next frame - the click itself runs from inside the FloatMenu window, long
        // after this column is done for the current frame.
        internal struct CellInfo
        {
            public Loadout assigned;
            public bool isAnchor;
            public bool isRole;
            public bool isOurs;
            // The verdict of Patch_LoadoutEditButton.ManagedByUs, built from the same two parts:
            // IsAmmoLink(assigned) || IsRoleLoadout(assigned). For an anchor isAnchor is already true,
            // for everything else `real` is identical to `assigned` - both paths give the same result.
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
            // Per frame instead of per cell: GetWidthCached is cheap, the .Translate() before it not quite -
            // and it ran up to three times per row. Not a one-off static, because a language change during
            // a running game would otherwise keep a width from the old language.
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
                // Only if it is drawn at all - a colonist on a foreign, hand-built loadout does not go
                // through ComposedDisplayLabel here.
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
            // Once instead of twice (button and tooltip use the same rect - that was the same calculation
            // twice, including HoldTrackerAnythingHeld twice and .Translate() twice).
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
                // The four verdicts come from GetCellInfo, so they are not derived a second time in the
                // second pass of the same frame - the content is unchanged.
                if (info.isOurs)
                {
                    Rect btnRect = buttonRect;
                    // The blue tints from LoadoutUtility instead of numbers written out again: the same two
                    // colors used to be duplicated here, and from that moment a change can only arrive
                    // halfway. The condition above (isOurs) deliberately stays separate: it also tints an
                    // anchor pointing into nothing, so CE's internal name ("[Pawn-Loadout] 1003") is painted
                    // over in EVERY case - LoadoutTintFor would return null there, and the button would show
                    // bookkeeping again.
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
                    // RoleUtility.DisplayLabel instead of the raw LabelCap: a role is internally named
                    // "[Role] X", and that prefix is bookkeeping, not a name. The anchor branch next to it
                    // already showed the clean name (ComposedDisplayLabel strips it itself) - only a colonist
                    // whose role was picked via CE's OWN dropdown in exactly this column ends up here
                    // (isAnchor false) and showed "[Role] Assault ...". That it later fixed itself "on its
                    // own" fits: as soon as anything went through our path, the colonist got an anchor and
                    // with it the other branch.
                    // Side effect: the prefix ate width,
                    // so the name was cut off earlier than its neighbors.
                    // CellLabel in BOTH branches: the anchor branch already cleaned up the name via
                    // ComposedDisplayLabel this way, the other did not - the same button, two naming rules.
                    // And "cell" is right here: the tint one line above already says the kind, the prefix
                    // would be repetition in the narrowest column of the game.
                    string label = info.label ?? "";
                    Widgets.Label(btnRect, label.Truncate(btnRect.width - 4f));
                    Text.Anchor = TextAnchor.UpperLeft;
                    GUI.color = Color.white;
                }
            }

            // TipWanted first (profiling showed this as the hottest tooltip spot in the whole mod):
            // this sits in CE's own loadout column, i.e. on a VANILLA tab with
            // many visible rows - and GetLoadoutTooltip builds the full text per row per frame, calls
            // ComposedDisplayLabel a second time (the first is in the button above), does three full
            // string replacements and calls the uncached WeaponQueuedCount per weapon, which walks every
            // bill stack of every workbench. Eight of our own windows already had this guard; the
            // spot with the largest audience did not.
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
        // Resolved once into a REAL delegate instead of going through MethodInfo.Invoke per call. The
        // MethodInfo was already looked up only once before - the expensive part was the INVOCATION: a
        // reflected call plus a fresh `new object[1]` per call, and that up to three times per row per
        // frame (LoadoutButtonRect is called for the button and for the tooltip rect, plus
        // Patch_LoadoutEditButton) - in CE's own loadout column, i.e. on a vanilla tab with many
        // visible rows.
        // Delegate.CreateDelegate only works because the method is static and has exactly
        // (Pawn) -> bool; the checks before it are not decoration but the reason a later CE update
        // silently falls back to "false" here instead of throwing.
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
            // try/catch stays: it now only catches what the CALLED method itself throws -
            // previously every reflection error was in there too.
            try { return holdTrackerAnythingHeldFn(p); }
            catch { return false; }
        }

        // Takes "is the colonist holding something" and the Rearm button width from the frame cache
        // instead of fetching both per call - the calculation itself is unchanged.
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
                // WORN APPAREL counts too. A CE shield is
                // apparel, not equipment and not inventory - it fell through all three checks above.
                // Failure scenario: three melee fighters with a Shield Kit, all three wearing their shield,
                // none in storage. Worn apparel is not spawned, so it doesn't show up in listerThings
                // either: stock 0, assigned yes, worn (so far) no -> demand 3 from storage, and the cost
                // line suggested crafting three shields that were already being worn.
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
