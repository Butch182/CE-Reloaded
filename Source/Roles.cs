using System;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;
using RimWorld;
using Verse;
using CombatExtended;

namespace CEReloaded
{
    // A Role is a set of SCALING FACTORS applied to an existing weapon loadout. In Role Mode you drag a weapon loadout
    // onto a role; CE Reloaded clones it and multiplies each item's count by the role's per-category factor (ammo,
    // medicine, food, grenades), then saves it as a "[Role] <name>" loadout. Everything stays on the weapon loadout, so
    // nothing is duplicated and non-role users are unaffected. A factor of 0 strips that category; 1 leaves it untouched.
    public class LoadoutRole : IExposable
    {
        public string name = "New Role";
        public float ammoFactor = 1f;
        public float medicineFactor = 1f;
        public float foodFactor = 1f;
        public float grenadeFactor = 1f;

        // WHAT the role was composed of - the labels of the source loadouts, in the order they were dragged on.
        //
        // Why this is needed: a finished role is a FLATTENED loadout (RoleUtility.BuildRoleLoadout merges the
        // sources and multiplies the counts). The result no longer tells which loadouts it came from. So the
        // composition only lived in a runtime dictionary of the dialog and was gone as soon as the window closed.
        // Consequence - exactly the failure seen: select a role, drag ONE pack on, Update, and the weapon is gone,
        // because "the role is exactly what is in the row" met an empty row.
        //
        // Labels, not UniqueIDs: CE assigns IDs as max+1 and REUSES them after deletion. A stored ID could later
        // point at a completely different loadout - silently wrong content instead of a visibly missing row. A
        // renamed source loadout simply drops out of the row on the next selection; that is the more harmless
        // outcome, and the mod looks up its roles by label everywhere anyway.
        public List<string> composition = new List<string>();

        // ============ WHICH loadout this role IS - by identity, not by name ============
        //
        // Principle: nothing is recognized by prefixes any more - prefixes are purely cosmetic and added at
        // display time, not stored in the string.
        //
        // Previously BOTH went through the label: whether a loadout IS a role (label.StartsWith("[Role]")) and
        // WHICH loadout belongs to a role (label == "[Role] " + name). That is why the tab showed the warning
        // "do not rename these loadouts" - it was correct, and it was the symptom.
        //
        // The ID is explicitly the RIGHT tool here, even though the comment on `composition` says IDs get
        // recycled. The difference is cleanup: an orphaned ID is purged on deletion
        // (LoadoutDeletionUtility.ForgetSideBooks removes it everywhere, just like the four other side books).
        // That is not possible for `composition`, because it references FOREIGN loadouts that someone else
        // deletes - here the number points at the role's own loadout.
        // 0 means "not linked yet". Existing data is filled in ONCE on load
        // (CEReloadedGameComponent.AdoptMarkersOnce) - after that nothing asks the label any more.
        public int loadoutId;

        public void ExposeData()
        {
            Scribe_Values.Look(ref name, "name", "New Role");
            Scribe_Values.Look(ref ammoFactor, "ammoFactor", 1f);
            Scribe_Values.Look(ref medicineFactor, "medicineFactor", 1f);
            Scribe_Values.Look(ref foodFactor, "foodFactor", 1f);
            Scribe_Values.Look(ref grenadeFactor, "grenadeFactor", 1f);
            Scribe_Collections.Look(ref composition, "composition", LookMode.Value);
            Scribe_Values.Look(ref loadoutId, "loadoutId", 0);
            // A save from before this change lacks the node, and Scribe_Collections then sets the list to null -
            // any later use would crash. Such roles deliberately stay WITHOUT a composition: guessing would be
            // worse than an empty row (see above).
            if (Scribe.mode == LoadSaveMode.PostLoadInit && composition == null) composition = new List<string>();
        }

        public LoadoutRole Clone() => new LoadoutRole
        {
            name = name, ammoFactor = ammoFactor, medicineFactor = medicineFactor,
            foodFactor = foodFactor, grenadeFactor = grenadeFactor,
            composition = composition != null ? new List<string>(composition) : new List<string>()
            // loadoutId is DELIBERATELY not copied: a copy is a new role and gets its own loadout the first time
            // it is written. If it took over the ID, two roles would point at the same loadout and overwrite
            // each other.
        };
    }

    public enum ItemKind { Weapon, Ammo, Medicine, Food, Grenade, Other }

    public class RoleMergeEntry
    {
        public ThingDef thing;
        public LoadoutGenericDef generic;
        public string label;
        public ItemKind kind;
        public int baseCount;
        public int scaledCount;
    }

    // Per-save role list, so a playthrough can have its own roles on top of the global library. Auto-instantiated by
    // RimWorld for every game (it creates all GameComponent subclasses that have a (Game) constructor).
    public class CEReloadedGameComponent : GameComponent
    {
        // ONE remembered reference to this component (from a performance pass).
        // `Game.GetComponent<T>()` walks the game's component list and does a type test per entry. That sits on
        // CE's hottest getter: `Loadout.Slots` goes through Patch_Loadout_MultiParentSlots, which calls TWO of
        // them per access (MultiParentUtility.Map and AmmoAssignmentUtility.IsAmmoCleared) - while CE's own think
        // tree touches the getter per colonist and decision, even with no mod window open.
        // Cached against the Game OBJECT, not a time limit: while a game runs the answer is immutable (RimWorld
        // builds all GameComponents with the Game), and a new or loaded game IS a different object. So this
        // cache cannot go stale without anyone having to remember a cleanup - exactly the mistake a TTL cache
        // would make here.
        // Otherwise behaves exactly like the old expression: null while no game is running.
        private static Game cachedGame;
        private static CEReloadedGameComponent cachedComp;

        public static CEReloadedGameComponent Cached
        {
            get
            {
                Game g = Current.Game;
                if (g == null) { cachedGame = null; cachedComp = null; return null; }
                if (!ReferenceEquals(g, cachedGame)) { cachedGame = g; cachedComp = g.GetComponent<CEReloadedGameComponent>(); }
                return cachedComp;
            }
        }

        public List<LoadoutRole> localRoles = new List<LoadoutRole>();
        public CERUiState ui;
        // Real 1..N Loadout attachment (see MultiParentUtility/Patch_Loadout_MultiParentSlots) - keyed by
        // the OWNER loadout's own UniqueID (a Role, or a pawn's hidden personal anchor), value is a
        // comma-joined list of the attached Loadouts' UniqueIDs. Only Roles and per-pawn anchors ever
        // appear as a key; plain Weapon Loadouts and Equipment Packs stay pure leaves, so a cycle can
        // never form through this side channel. A plain string rather than a List<int> value - Scribe's
        // dictionary looker has no LookMode for a list-typed value without a custom IExposable wrapper,
        // and this is simple enough not to need one.
        public Dictionary<int, string> loadoutExtraParents = new Dictionary<int, string>();

        // Per-pawn-anchor "deliberately zero ammo" marker, keyed by the anchor's UniqueID. An anchor with
        // no ammo-type slots of its own is normally indistinguishable from "no personal override yet" (see
        // Patch_Loadout_MultiParentSlots), so unchecking a pawn's last ammo type would otherwise silently
        // let the Weapon Loadout template's own default snap back instead of actually going to zero. This
        // flag is the difference between those two "empty" states.
        public HashSet<int> ammoExplicitlyCleared = new HashSet<int>();

        // Weapon Loadout templates OUR wizard actually generated (LoadoutUtility.MarkAsManaged, called at
        // the one Dialog_GenerateLoadouts creation site) - a reliable "is this ours" signal that survives a
        // rename, unlike the "W|" prefix convention (LoadoutUtility.IsManagedByUs falls back to the prefix
        // only for loadouts generated before this set existed, so nothing already in a save needs migrating).
        public HashSet<int> managedLoadoutIds = new HashSet<int>();
        // Which loadouts ARE EQUIPMENT PACKS - by identity instead of label (same principle: recognize nothing
        // by prefixes). Same pattern as managedLoadoutIds next to it: add on creation, remove on deletion
        // (LoadoutDeletionUtility - mandatory, because CE's IDs are recycled), and the "[Pack]" marker in the
        // label is only a fallback for older data.
        public HashSet<int> packLoadoutIds = new HashSet<int>();

        // Production bills OUR wizard actually created (LoadoutUtility.MarkBillAsManaged, called from both
        // AddWeaponBillOnce and AddOrUpdateAmmoBill) - keyed by Bill.GetUniqueLoadID() (a stable, save-
        // surviving string ID every vanilla Bill already carries via ILoadReferenceable), not object
        // reference (a Bill instance doesn't survive a save/load round-trip, but its loadID does). Lets
        // Arm-my-Colony's "Delete ammo/weapon bills" switches offer a precise "delete only what THIS mod
        // queued" scope alongside the existing blunt "delete everything for a recognized type" one, instead
        // of only ever being able to do the latter (2026-08-01).
        // This used to say "never cleaned up - a dead ID is harmless because only existing bills are iterated".
        // For CORRECTNESS that is still true. For SIZE it is not: the set grew with every bill ever created,
        // never shrank and is saved with the game - over a long playthrough with regular use of the resupply
        // wizard that is thousands of dead strings in every save. LoadoutUtility.PruneManagedBillIds now cleans
        // it up on the cleanup tick (once per in-game hour, together with the orphaned anchors).
        public HashSet<string> managedBillIds = new HashSet<string>();

        // Colonists the PLAYER declared off-limits for arming, keyed by Pawn.thingIDNumber (stable within a
        // save, unlike an object reference). Deliberately fed into LoadoutUtility.CannotFight rather than
        // checked separately: that one rule is what the solver, the readiness scan, the Arm-my-Colony preview
        // and both Assign screens already agree on, so a flagged colonist goes grey and gets skipped
        // everywhere at once instead of in whichever places got remembered (2026-08-09: merges the
        // long-standing "mark a non-combatant" wish with the finding that the popup's green check did
        // nothing on a colonist who has no loadout yet).
        public HashSet<int> excludedPawnIds = new HashSet<int>();

        // Weapons the PLAYER has DISABLED: the automation never hands them out, and in the selection window they
        // are greyed out at the end of their group, where they can be re-enabled. defNames instead of ThingDef
        // references - stable in the save, and a weapon from a mod removed later just silently drops out of the
        // set.
        public HashSet<string> disabledWeapons = new HashSet<string>();

        // The same for ROLES and hand-built loadouts: not only weapons, roles and loadouts can be disabled too,
        // with all consequences. UniqueIDs instead of defNames here, since a loadout has no defName - and
        // because CE REUSES its IDs after deletion (max+1), this set must be purged on deletion, otherwise the
        // next template with the same number counts as disabled.
        // LoadoutDeletionUtility.ForgetSideBooks does that - the one place where every ID list of this mod is
        // cleaned up.
        public HashSet<int> disabledLoadouts = new HashSet<int>();

        // Which Equipment Pack the generator created for which of its plans - plan name -> Loadout.UniqueID.
        // Previously the generator recognized "does this already exist" solely by the LABEL text, with two
        // consequences: a renamed pack counted as new on the next run and was created a second time, and two
        // packs with the same name could not be told apart.
        // The name remains as a fallback for packs created before this record existed.
        public Dictionary<string, int> packPlanIds = new Dictionary<string, int>();

        public CEReloadedGameComponent(Game game) { }

        // ============ ONE-TIME ADOPTION: marker -> identity ============
        //
        // Goal: no code may look for prefixes any more, whether weapon, sidearm, equipment pack or role.
        //
        // Without this, the fallbacks in IsRoleLoadout/IsEquipmentPackLoadout/IsGeneratedTemplate could not go
        // away: everything created BEFORE the switch does not know its identity and would abruptly become an
        // ordinary loadout once the marker is dropped - roles would no longer be roles, packs no longer packs.
        // That would not be a display bug but data loss as far as the game is concerned.
        //
        // So what the marker still knows is filled in ONCE on load. After that the marker is pure cosmetics and
        // nobody asks it any more. The pass is cheap (one loop over the loadout list) and idempotent - HashSets
        // and an ID assignment that is already correct.
        //
        // Deliberately NO migration version number: the pass may run every time. A counter that remembers
        // "already done" would be exactly the kind of state that is wrong in the next special case - and
        // repeating costs nothing here.
        public override void FinalizeInit() => AdoptMarkersOnce();

        public override void LoadedGame() => AdoptMarkersOnce();

        private void AdoptMarkersOnce()
        {
            List<Loadout> all = LoadoutManager.Loadouts;
            if (all == null) return;

            // Roles: match by name, because that was the link until now. From here on the ID holds and the name
            // is free. The body lives in RoleUtility because the reset button needs it too - see there.
            RoleUtility.AdoptRoleLoadoutsByName();

            foreach (Loadout lo in all)
            {
                if (lo == null || lo.defaultLoadout || lo.label == null) continue;
                if (EquipmentPackUtility.IsEquipmentPackLabel(lo.label)) packLoadoutIds.Add(lo.UniqueID);
                // Weapon templates: the prefix is configurable and defaults to "none". So check both spellings -
                // the configured one and the historical "W|".
                CEReloadedSettings s = CEReloadedMod.settings;
                string mark = s != null && !string.IsNullOrEmpty(s.loadoutPrefix) ? s.loadoutPrefix : "W|";
                if (lo.label.StartsWith(mark) || lo.label.StartsWith("W|")) managedLoadoutIds.Add(lo.UniqueID);
            }
        }

        // Automatic version of the Debug settings tab's manual "Clean up orphaned anchors" button - a
        // dead/captured/otherwise-removed colonist never cleans up their own hidden "[Pawn-Loadout]" anchor on the
        // way out, so without this they'd accumulate silently forever over a long playthrough. Runs about
        // once per in-game hour (2500 ticks) - cheap enough (a handful of pawn/loadout scans) that there's
        // no reason to make the player remember to click a button for basic hygiene.
        public override void GameComponentTick()
        {
            if (Find.TickManager.TicksGame % 2500 == 0)
            {
                AmmoAssignmentUtility.CleanupOrphanedAnchors();
                // The same hygiene for the bill bookkeeping set: it grew with every bill ever created and never
                // shrank, although it is written into the save.
                LoadoutUtility.PruneManagedBillIds();
            }
        }

        public override void ExposeData()
        {
            Scribe_Collections.Look(ref localRoles, "cerLocalRoles", LookMode.Deep);
            Scribe_Deep.Look(ref ui, "cerUiState");
            Scribe_Collections.Look(ref loadoutExtraParents, "cerLoadoutExtraParents", LookMode.Value, LookMode.Value);
            Scribe_Collections.Look(ref ammoExplicitlyCleared, "cerAmmoExplicitlyCleared", LookMode.Value);
            Scribe_Collections.Look(ref managedLoadoutIds, "cerManagedLoadoutIds", LookMode.Value);
            Scribe_Collections.Look(ref packLoadoutIds, "cerPackLoadoutIds", LookMode.Value);
            Scribe_Collections.Look(ref managedBillIds, "cerManagedBillIds", LookMode.Value);
            Scribe_Collections.Look(ref excludedPawnIds, "cerExcludedPawnIds", LookMode.Value);
            Scribe_Collections.Look(ref disabledWeapons, "cerDisabledWeapons", LookMode.Value);
            Scribe_Collections.Look(ref disabledLoadouts, "cerDisabledLoadouts", LookMode.Value);
            Scribe_Collections.Look(ref packPlanIds, "cerPackPlanIds", LookMode.Value, LookMode.Value);
            if (Scribe.mode == LoadSaveMode.PostLoadInit)
            {
                if (localRoles == null) localRoles = new List<LoadoutRole>();
                if (ui == null) ui = CERUiState.FromSettings(CEReloadedMod.settings);
                if (loadoutExtraParents == null) loadoutExtraParents = new Dictionary<int, string>();
                if (ammoExplicitlyCleared == null) ammoExplicitlyCleared = new HashSet<int>();
                if (managedLoadoutIds == null) managedLoadoutIds = new HashSet<int>();
                if (packLoadoutIds == null) packLoadoutIds = new HashSet<int>();
                if (managedBillIds == null) managedBillIds = new HashSet<string>();
                if (excludedPawnIds == null) excludedPawnIds = new HashSet<int>();
                if (disabledWeapons == null) disabledWeapons = new HashSet<string>();
                if (disabledLoadouts == null) disabledLoadouts = new HashSet<int>();
                if (packPlanIds == null) packPlanIds = new Dictionary<string, int>();
            }
        }

        // Current save's UI state, created (seeded from settings) on first access within a game.
        public CERUiState UiState()
        {
            if (ui == null) ui = CERUiState.FromSettings(CEReloadedMod.settings);
            return ui;
        }

        // Hard reset of the save's remembered UI state to the current mod-menu settings (the Tools tab button).
        // World facts survive (the sticky mechanoid flag), everything else including manual slider overrides is reseeded.
        public void ResetUiToSettings()
        {
            bool fought = ui != null && ui.hasFoughtMechanoids;
            ui = CERUiState.FromSettings(CEReloadedMod.settings);
            ui.hasFoughtMechanoids = fought;
        }
    }

    public static class RoleUtility
    {
        // Fixed, non-customizable marker so role loadouts are regex-detectable everywhere. The rest of the name is free.
        public const string RoleMarker = "[Role]";

        // The current save's local roles, or null when no game is loaded (e.g. the mod-settings screen from the menu).
        public static List<LoadoutRole> LocalRoles()
        {
            CEReloadedGameComponent comp = CEReloadedGameComponent.Cached;
            return comp != null ? comp.localRoles : null;
        }

        // Per-save UI state, or a fresh settings-seeded instance when no game is loaded (dialogs only open in-game anyway).
        public static CERUiState Ui()
        {
            CEReloadedGameComponent comp = CEReloadedGameComponent.Cached;
            return comp != null ? comp.UiState() : CERUiState.FromSettings(CEReloadedMod.settings);
        }

        // ============ Recognition by identity, not by name ============
        //
        // A loadout is a role exactly when some LoadoutRole points at it. The label no longer matters for that -
        // it is the first step towards recognizing nothing by prefixes.
        //
        // IsRoleLabel remains as a FALLBACK, only for the one case that must still exist: a role from a save
        // made before this change has loadoutId == 0. As soon as it is touched once, RoleLoadoutOf records the
        // link, and from then on only that counts. The fallback is therefore self-healing and disappears from
        // existing data on its own.
        //
        // Cached because this is asked per row per frame (CE's own Loadout column, our cells, the FloatMenu
        // tinting). The key is the role count plus the loadout count - both change exactly when the answer can
        // change.
        private static HashSet<int> roleIdCache;
        private static int roleIdCacheKey = -1;

        private static HashSet<int> RoleLoadoutIds()
        {
            List<LoadoutRole> all = AllKnownRoles();
            int key = all.Count * 100003 + (LoadoutManager.Loadouts?.Count ?? 0);
            if (roleIdCache != null && key == roleIdCacheKey) return roleIdCache;
            roleIdCacheKey = key;
            var ids = new HashSet<int>();
            foreach (LoadoutRole r in all) if (r != null && r.loadoutId != 0) ids.Add(r.loadoutId);
            return roleIdCache = ids;
        }

        // Global (mod settings) AND save-local - a role can live in both lists, and the question "is this a
        // role" must not depend on where it is stored.
        public static List<LoadoutRole> AllKnownRoles()
        {
            var all = new List<LoadoutRole>();
            if (CEReloadedMod.settings?.roles != null) all.AddRange(CEReloadedMod.settings.roles);
            CEReloadedGameComponent comp = CEReloadedGameComponent.Cached;
            if (comp?.localRoles != null) all.AddRange(comp.localRoles);
            return all;
        }

        // THIS role's loadout. First via the remembered identity, otherwise once via the label - and then the
        // identity is recorded so that a later rename can no longer break it. This is what makes the warning
        // in the tab unnecessary.
        public static Loadout RoleLoadoutOf(LoadoutRole role)
        {
            if (role == null || LoadoutManager.Loadouts == null) return null;
            if (role.loadoutId != 0)
            {
                Loadout byId = LoadoutManager.Loadouts.FirstOrDefault(l => l != null && l.UniqueID == role.loadoutId);
                if (byId != null) return byId;
                // Points nowhere - the loadout was removed outside our deletion routine. Drop the link and keep
                // searching normally below instead of carrying a dead number around.
                role.loadoutId = 0;
                roleIdCacheKey = -1;
            }
            // No label fallback any more: matching by name happens exactly ONCE on load (AdoptMarkersOnce). If
            // nothing is found here, the role really has no loadout yet - and then "null" is the right answer,
            // not a search by its name.
            return null;
        }

        // Bind every role WITHOUT a link to an existing "[Role] <name>" loadout. Exactly the matching that runs
        // once on load (CEReloadedGameComponent.AdoptMarkersOnce) - extracted here because it is needed in a
        // second place.
        //
        // Cause: after "Reset" in the Roles tab, "[Role] Support", "[Role] Breacher" and "[Role] Assault"
        // suddenly appeared in the "Your weapon" list - i.e. among the things you can DRAG onto a role. After
        // that a role could be put into a role, and the preview showed two main weapons with overload.
        // The cause is right here: Reset replaces the global roles with fresh defaults, and a fresh role has
        // loadoutId 0. Its old loadout lives on, though - from that moment it belongs to no role, and without
        // an owner it is an entirely ordinary loadout to every identity check. Recognition was not broken, it
        // just had nothing left to recognize.
        // Matching after the reset closes the gap at the root instead of catching it via the label afterwards.
        //
        // Returns: how many links were newly created (for the message at the button).
        public static int AdoptRoleLoadoutsByName()
        {
            List<Loadout> all = LoadoutManager.Loadouts;
            if (all == null) return 0;
            int n = 0;
            foreach (LoadoutRole r in AllKnownRoles())
            {
                if (r == null || r.loadoutId != 0) continue;
                string label = RoleMarker + " " + (r.name ?? "").Trim();
                Loadout match = all.FirstOrDefault(l => l != null && !l.defaultLoadout && l.label == label);
                if (match == null) continue;
                LinkRoleLoadout(r, match);
                n++;
            }
            return n;
        }

        // How many roles would lose their BUILDING BLOCKS if these loadouts disappear?
        // A role remembers WHICH templates it was composed of - not their content, which it flattened on save.
        // If the sources disappear it keeps its content (every colonist on it stays equipped), but its row is
        // empty and it can no longer be adjusted meaningfully.
        // That is not a bug but a consequence of the snapshot principle - it is only surprising if nobody says
        // so. That is what this number is for.
        public static int CountRolesComposedOf(IEnumerable<Loadout> doomed)
        {
            if (doomed == null) return 0;
            var labels = new HashSet<string>(doomed.Where(l => l != null && l.label != null).Select(l => l.label));
            if (labels.Count == 0) return 0;
            int n = 0;
            foreach (LoadoutRole r in AllKnownRoles())
                if (r?.composition != null && r.composition.Any(labels.Contains)) n++;
            return n;
        }

        public static void LinkRoleLoadout(LoadoutRole role, Loadout lo)
        {
            if (role == null || lo == null) return;
            role.loadoutId = lo.UniqueID;
            roleIdCacheKey = -1;
        }

        // Called from LoadoutDeletionUtility - without it, a remembered ID pointed at a RECYCLED foreign loadout
        // after deletion (CE's IDs are max+1 and get reused), and an arbitrary new template would suddenly have
        // passed itself off as a role.
        public static void ForgetRoleLoadout(int loadoutId)
        {
            foreach (LoadoutRole r in AllKnownRoles())
                if (r != null && r.loadoutId == loadoutId) r.loadoutId = 0;
            roleIdCacheKey = -1;
        }

        // NO label fallback any more. Whatever existed before the switch is filled in ONCE on load - see
        // CEReloadedGameComponent.AdoptMarkersOnce. From here on identity alone decides.
        public static bool IsRoleLoadout(Loadout lo) => lo != null && RoleLoadoutIds().Contains(lo.UniqueID);
        public static bool IsRoleLabel(string label) => !string.IsNullOrEmpty(label) && label.StartsWith(RoleMarker);

        // Player-facing label for a Role loadout - strips the invariant "[Role] " identity marker and
        // applies the same kind of clean, configurable prefix Weapon Loadouts/Equipment Packs already have
        // (2026-08-02: Roles showed their raw internal marker everywhere, the only one of
        // the three template types that did). Purely cosmetic - RoleMarker/IsRoleLabel above, and every
        // internal identity check, are completely untouched, so this can never un-recognize a Role.
        // Delegates to LoadoutUtility.LoadoutDisplayName - ONE place for all names instead of every call site
        // building its own. The name is kept so the roughly twenty call sites stay readable and did not all
        // have to be touched in one go.
        // markerShownElsewhere: false - this variant is used where there is only text (menus, messages, CE's
        // own column). Where an icon or a color sits next to it, the caller uses LoadoutUtility.CellLabel.
        public static string DisplayLabel(Loadout lo) => LoadoutUtility.LoadoutDisplayName(lo, false);

        // Populate the role list with sensible presets the first time it is used. Factors only, so no def lookups needed.
        public static void EnsureSeeded(CEReloadedSettings settings)
        {
            if (settings == null) return;
            if (settings.roles == null) settings.roles = new List<LoadoutRole>();
            if (settings.roles.Count == 0) settings.roles.AddRange(DefaultRoles());
        }

        // ============ Resetting the GLOBAL roles - in ONE place ============
        //
        // There used to be two buttons for this (Roles tab and mod settings), and they did different things: one
        // re-matched the old loadouts, the other did not. Both shared the bug the delete button also had - a
        // role whose name does NOT appear among the defaults ("Agent Rifleman") disappears on reset, but its
        // loadout "[Role] Agent Rifleman" does not. Without a role behind it, it is an ordinary template to every
        // identity check: assignment keeps handing it out (even under "No roles"), and under its raw label
        // including the "[Role]" marker.
        //
        // The order here matters: first collect WHAT belongs to the roles now (after the Clear the link is no
        // longer reachable), then reset, then re-match the ones with the same name - and only then delete what
        // is still ownerless.
        public static void ResetGlobalRoles(CEReloadedSettings settings)
        {
            if (settings == null) return;
            if (settings.roles == null) settings.roles = new List<LoadoutRole>();
            var owned = new List<Loadout>();
            foreach (LoadoutRole r in settings.roles)
            {
                Loadout lo = RoleLoadoutOf(r);
                if (lo != null) owned.Add(lo);
            }
            settings.roles.Clear();
            settings.roles.AddRange(DefaultRoles());
            // Default roles with the same name take back their old loadout: otherwise "[Role] Support" and co.
            // appeared in the DRAG list and a role could be put into a role.
            AdoptRoleLoadoutsByName();
            var stillOwned = new HashSet<int>();
            foreach (LoadoutRole r in AllKnownRoles())
                if (r != null && r.loadoutId != 0) stillOwned.Add(r.loadoutId);
            LoadoutDeletionUtility.DeleteAll(owned.Where(l => !stillOwned.Contains(l.UniqueID)));
        }

        // ============ Restore a deleted default role WITHOUT throwing everything else away ============
        //
        // The failure: drag a weapon onto the empty template card "[G] Assault", then press the x to undo it -
        // and Assault was gone from the GLOBAL library. Not reverted to the empty template: gone. The seven
        // default roles are the only starting point for roles, and the only way back was "Reset to defaults" -
        // which takes every self-built role with it. A trap with a very high price for a misclick.
        //
        // Deliberately NOT built: "deleting turns a default role back into an empty template". Delete should
        // delete - anyone who does not want Assault should be able to get rid of it, and a card that comes back
        // after deletion would itself be a bug. What is built instead is the missing way back: whichever of the
        // seven is missing comes back individually, at its place in the default order, and nothing else is
        // touched.
        public static List<LoadoutRole> MissingPresetRoles(CEReloadedSettings settings)
        {
            var missing = new List<LoadoutRole>();
            if (settings == null) return missing;
            var have = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            if (settings.roles != null)
                foreach (LoadoutRole r in settings.roles)
                    if (r != null && !string.IsNullOrEmpty(r.name)) have.Add(r.name.Trim());
            foreach (LoadoutRole d in DefaultRoles())
                if (!have.Contains(d.name)) missing.Add(d);
            return missing;
        }

        public static int RestoreMissingPresetRoles(CEReloadedSettings settings)
        {
            if (settings == null) return 0;
            if (settings.roles == null) settings.roles = new List<LoadoutRole>();
            List<LoadoutRole> presets = DefaultRoles();
            List<LoadoutRole> missing = MissingPresetRoles(settings);
            if (missing.Count == 0) return 0;
            foreach (LoadoutRole m in missing)
            {
                // At the default's position - a restored role should not be tacked on at the end (restoring by
                // hand put the role at the END of the list instead of the beginning).
                int want = presets.FindIndex(d => d.name == m.name);
                int at = Mathf.Clamp(want < 0 ? settings.roles.Count : want, 0, settings.roles.Count);
                settings.roles.Insert(at, m);
            }
            // An existing ownerless "[Role] <name>" loadout belongs to the restored role again - the same
            // adoption the reset uses.
            AdoptRoleLoadoutsByName();
            return missing.Count;
        }

        // Is THIS role one of the seven built-in templates (by name)? Only for the confirmation text -
        // deletion itself behaves the same for all roles.
        public static bool IsPresetRoleName(string name)
        {
            string n = (name ?? "").Trim();
            if (n.Length == 0) return false;
            foreach (LoadoutRole d in DefaultRoles())
                if (string.Equals(d.name, n, StringComparison.OrdinalIgnoreCase)) return true;
            return false;
        }

        public static List<LoadoutRole> DefaultRoles() => new List<LoadoutRole>
        {
            new LoadoutRole { name = "Assault"   },                                                       // baseline
            new LoadoutRole { name = "Marksman",  ammoFactor = 0.75f, grenadeFactor = 0f },               // fewer, precise rounds
            new LoadoutRole { name = "Support",   ammoFactor = 2f },                                      // suppression: lots of ammo
            new LoadoutRole { name = "Breacher",  grenadeFactor = 1.5f },                                 // CQC + breaching charges
            new LoadoutRole { name = "Grenadier", grenadeFactor = 3f },                                   // explosives specialist
            new LoadoutRole { name = "Medic",     medicineFactor = 3f, foodFactor = 1.5f, grenadeFactor = 0f }, // carries the squad's meds
            new LoadoutRole { name = "Melee",     medicineFactor = 1.5f, grenadeFactor = 0f }, // keeps its (sidearm) ammo, extra meds
        };

        // ---- The merge -------------------------------------------------------------------------------------------------

        // Union the dragged loadouts (max count on duplicates) and scale each item by its category factor. Returns one row
        // per item with both the base and the scaled count, so the same call drives both the preview and the real build.
        public static List<RoleMergeEntry> Merge(LoadoutRole role, IEnumerable<Loadout> dragged)
        {
            Dictionary<ThingDef, int> things = new Dictionary<ThingDef, int>();
            Dictionary<LoadoutGenericDef, int> generics = new Dictionary<LoadoutGenericDef, int>();

            if (dragged != null)
            {
                foreach (Loadout src in dragged)
                {
                    // Equipment Packs are NO longer melted in: real links for roles too (references). They now
                    // hang on the role as a real reference, just like on a single colonist - see
                    // PacksIn/CommitRole.
                    // Previously the same pack behaved differently depending on how it was attached: on a pawn it
                    // stayed linked, in a role only a copy was taken on creation and later changes never arrived
                    // there. That inconsistency was the mod's biggest source of confusion.
                    if (EquipmentPackUtility.IsEquipmentPackLoadout(src)) continue;
                    if (src == null || src.Slots == null) continue;
                    foreach (LoadoutSlot s in src.Slots)
                    {
                        if (s.thingDef != null)
                            things[s.thingDef] = things.TryGetValue(s.thingDef, out int e) ? Math.Max(e, s.count) : s.count;
                        else if (s.genericDef != null)
                            generics[s.genericDef] = generics.TryGetValue(s.genericDef, out int e) ? Math.Max(e, s.count) : s.count;
                    }
                }
            }

            List<RoleMergeEntry> rows = new List<RoleMergeEntry>();
            foreach (var kv in things)
            {
                ItemKind kind = KindOf(kv.Key);
                rows.Add(new RoleMergeEntry { thing = kv.Key, label = kv.Key.label, kind = kind, baseCount = kv.Value, scaledCount = Scale(kv.Value, FactorFor(role, kind)) });
            }
            foreach (var kv in generics)
            {
                ItemKind kind = KindOfGeneric(kv.Key);
                rows.Add(new RoleMergeEntry { generic = kv.Key, label = kv.Key.label, kind = kind, baseCount = kv.Value, scaledCount = Scale(kv.Value, FactorFor(role, kind)) });
            }
            // Weapons first, then ammo, then the rest - reads naturally in the preview.
            return rows.OrderBy(r => (int)r.kind).ThenBy(r => r.label).ToList();
        }

        // The Equipment Packs from a composition - the parts that are attached to the role as a real reference
        // instead of being melted in. The role scaling factors therefore NO longer apply to them: a reference is
        // the pack content, not a scaled copy of it. That is the price of the live link and explicitly intended -
        // a number that changes again the next time the pack is opened would otherwise just be a copy with extra
        // steps.
        public static List<Loadout> PacksIn(IEnumerable<Loadout> dragged)
        {
            List<Loadout> packs = new List<Loadout>();
            if (dragged == null) return packs;
            foreach (Loadout src in dragged)
                if (src != null && EquipmentPackUtility.IsEquipmentPackLoadout(src) && !packs.Contains(src)) packs.Add(src);
            return packs;
        }

        // The slots with which ONE row of the composition enters the role: an Equipment Pack is attached as a
        // reference (OwnSlots, as the preview reads it), everything else is melted in (Slots, as Merge reads it).
        // Both lookups used to exist separately; it is a shared function because a card that reads a different
        // slot source than the later result is exactly the kind of silent misdisplay that has already happened
        // twice here.
        public static IEnumerable<LoadoutSlot> CompSlots(Loadout src)
        {
            if (src == null) return Enumerable.Empty<LoadoutSlot>();
            IEnumerable<LoadoutSlot> slots = EquipmentPackUtility.IsEquipmentPackLoadout(src) ? src.OwnSlots : src.Slots;
            return slots ?? Enumerable.Empty<LoadoutSlot>();
        }

        public static bool CompHasDef(Loadout src, ThingDef def)
            => def != null && CompSlots(src).Any(s => s.thingDef == def);

        // Which weapon of the composition counts as the MAIN weapon - the same pick the mod later makes on the
        // finished role loadout, just earlier (LoadoutUtility.MainAndSideFrom).
        // Background: a second weapon on a role deliberately does NOT replace the first - a role may bundle main
        // weapon + knife + grenade. What was missing was saying WHICH of the weapons becomes the main weapon; the
        // role card marks its chip with this.
        // The name goes in as the label BuildRoleLoadout will actually assign later, so that the naming rule in
        // LoadoutNameMatchesWeapon gives the same result here and there.
        public static ThingDef MainWeaponOf(IEnumerable<Loadout> dragged, string finalName)
        {
            if (dragged == null) return null;
            List<ThingDef> defs = new List<ThingDef>();
            foreach (Loadout src in dragged)
                foreach (LoadoutSlot s in CompSlots(src))
                    if (s.thingDef != null) defs.Add(s.thingDef);
            LoadoutUtility.MainAndSideFrom(defs, RoleMarker + " " + (finalName ?? ""), out ThingDef main, out _);
            return main;
        }

        public static Loadout BuildRoleLoadout(LoadoutRole role, IEnumerable<Loadout> dragged, string finalName)
        {
            Loadout lo = new Loadout();
            lo.label = RoleMarker + " " + finalName;
            lo.defaultLoadout = false;
            foreach (RoleMergeEntry e in Merge(role, dragged))
            {
                if (e.scaledCount <= 0) continue;
                if (e.thing != null) lo.AddSlot(new LoadoutSlot(e.thing, e.scaledCount));
                else if (e.generic != null) lo.AddSlot(new LoadoutSlot(e.generic, e.scaledCount));
            }
            return lo;
        }

        private static int Scale(int baseCount, float factor)
        {
            if (factor == 1f) return baseCount;
            if (factor <= 0f) return 0;
            int r = (int)Math.Round(baseCount * (double)factor, MidpointRounding.AwayFromZero);
            return Math.Max(1, r);   // keep at least 1 of anything you asked to keep (factor > 0)
        }

        public static float FactorFor(LoadoutRole role, ItemKind kind)
        {
            switch (kind)
            {
                case ItemKind.Ammo: return role.ammoFactor;
                case ItemKind.Medicine: return role.medicineFactor;
                case ItemKind.Food: return role.foodFactor;
                case ItemKind.Grenade: return role.grenadeFactor;
                default: return 1f;   // weapons / sidearms / anything else pass through untouched
            }
        }

        public static ItemKind KindOf(ThingDef d)
        {
            if (d == null) return ItemKind.Other;
            if (LoadoutUtility.IsThrownExplosive(d)) return ItemKind.Grenade;
            if (d.thingClass != null && typeof(CombatExtended.AmmoThing).IsAssignableFrom(d.thingClass)) return ItemKind.Ammo;
            if (d.IsWeapon) return ItemKind.Weapon;
            if (d.IsMedicine) return ItemKind.Medicine;
            if (d.IsDrug) return ItemKind.Medicine; // most drugs are also ingestible - must beat the food fallback below, matching KindOfGeneric's "Drug" bucket
            if (d.IsIngestible && d.ingestible != null && d.ingestible.foodType != FoodTypeFlags.None) return ItemKind.Food;
            return ItemKind.Other;
        }

        private static ItemKind KindOfGeneric(LoadoutGenericDef g)
        {
            if (g == null || g.defName == null) return ItemKind.Other;
            string n = g.defName;
            if (n.IndexOf("Ammo", StringComparison.OrdinalIgnoreCase) >= 0) return ItemKind.Ammo;
            if (n.IndexOf("Medicine", StringComparison.OrdinalIgnoreCase) >= 0 || n.IndexOf("Drug", StringComparison.OrdinalIgnoreCase) >= 0) return ItemKind.Medicine;
            if (n.IndexOf("Food", StringComparison.OrdinalIgnoreCase) >= 0 || n.IndexOf("Meal", StringComparison.OrdinalIgnoreCase) >= 0 ||
                n.IndexOf("Nutrient", StringComparison.OrdinalIgnoreCase) >= 0 || n.IndexOf("Perishable", StringComparison.OrdinalIgnoreCase) >= 0) return ItemKind.Food;
            return ItemKind.Other;
        }
    }
}
