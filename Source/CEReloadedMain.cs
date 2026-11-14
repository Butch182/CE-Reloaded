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
    [StaticConstructorOnStartup]
    public static class CEReloadedMain
    {
        static CEReloadedMain()
        {
            var harmony = new Harmony("Butch.CEReloaded");

            // Each patch class INDIVIDUALLY, not via PatchAll().
            //
            // PatchAll processes all classes in one loop and lets a HarmonyException propagate. If Combat
            // Extended changes a signature - and several of our targets depend on method names as text,
            // e.g. 'DoCell' on the loadout column header - the first error aborts the WHOLE loop.
            // Everything that would have come after is never patched.
            // The order comes from reflection and is not guaranteed: WHICH half of the mod survives
            // would be practically random - and the player would see a half-working mod
            // without any hint as to why.
            // Processed individually, everything not affected itself survives, and the log names exactly
            // the class that fails. A broken patch is still an error - it just no longer takes others down.
            int patched = 0;
            List<string> failed = new List<string>();
            foreach (Type t in AccessTools.GetTypesFromAssembly(typeof(CEReloadedMain).Assembly))
            {
                try
                {
                    if (new PatchClassProcessor(harmony, t).Patch() != null) patched++;
                }
                catch (Exception e)
                {
                    failed.Add(t.Name);
                    Log.Error("[CE Reloaded] Patch class " + t.Name + " could not be applied - this part of the mod "
                        + "stays inactive, the rest keeps working. Usually this means Combat Extended renamed a "
                        + "method or changed its signature.\n" + e);
                }
            }
            if (failed.Count > 0)
                Log.Warning("[CE Reloaded] " + patched + " patch classes active, " + failed.Count
                    + " failed: " + string.Join(", ", failed.ToArray()));
            else
                Log.Message("[CE Reloaded] Loaded - " + patched + " patch classes applied.");

            // CE's generic loadout defs ("Any Meal", "Any Medicine", "Any ammo for <weapon>") get a real
            // hash (FOOD availability turned red for many colonists as soon as ONE colonist switched to
            // "Any ammo", and blue again once they switched back).
            // Cause, confirmed in decompiled code: Verse.Def.GetHashCode() returns the field `defNameHash`,
            // and Def.Equals(Def) compares exactly that field (plus type). It is only set by
            // Def.ResolveDefNameHash(), which RimWorld's def loader calls for XML defs. CE, however, creates
            // its LoadoutGenericDefs in code (static constructor of LoadoutGenericDef: new LoadoutGenericDef()
            // + DefDatabase.Add) and never calls it - all these defs carry hash 0 and count as ONE key in any
            // Dictionary/HashSet with the default comparer. In the round-robin availability calculation
            // (Dialog_AssignLoadouts.BuildClaimsUncached) the ammo demand (150 rounds) thus ended up under
            // the same key as "Any Perishable Meal"; the food pool was used up after the first colonist,
            // and everyone after that read "missing".
            // The earlier diagnosis (DefRefComparer in LoadoutUtility) had the symptom right and the cause
            // wrong: not a collision of three strings, but a field that was never set - on ALL generics,
            // so also in every dictionary that was not switched over back then.
            // Here, after all patches: earlier than any window of this mod, and CE itself keeps no
            // hash-based collection of these defs (DefDatabase uses lists and a dictionary keyed by the
            // defName STRING). RunClassConstructor ensures CE's static constructor has already created the
            // defs - otherwise it only runs on first touch of the type. Idempotent: a def that already
            // carries the hash is left alone.
            System.Runtime.CompilerServices.RuntimeHelpers.RunClassConstructor(typeof(LoadoutGenericDef).TypeHandle);
            int hashed = 0;
            foreach (LoadoutGenericDef gd in DefDatabase<LoadoutGenericDef>.AllDefsListForReading)
            {
                if (gd == null || string.IsNullOrEmpty(gd.defName) || gd.GetHashCode() != 0) continue;
                gd.ResolveDefNameHash();
                hashed++;
            }
            if (hashed > 0 && Prefs.DevMode)
                Log.Message("[CE Reloaded] Resolved missing hashes for " + hashed + " generic loadout defs from Combat Extended.");
        }
    }

    public enum WeaponFilterMode { AllWeapons, ResearchedWeapons, AvailableWeapons, AssignedLoadout }
    public enum AmmoFilterMode { AllAmmo, ResearchedAmmo, AvailableAmmo }
    // Preferred concrete ammo variant a fresh loadout picks for a firearm. FMJ/AP/HP match the "(...)" label suffix
    // CE uses; Generic keeps the "Any Ammo for X" placeholder. Falls back gracefully when a weapon has no such variant.
    public enum DefaultFirearmAmmo { FMJ, AP, HP, Generic }
    // AvailableLoadouts ("Created loadouts") is no longer a filter - the value only remains so that a
    // previously saved setting still loads; CEReloadedSettings redirects it to AssignedLoadouts, and the
    // selection in the settings does not offer it.
    public enum AmmoBillFilterMode { AssignedLoadouts, AvailableLoadouts, AvailableWeapons, ActiveBills, AllAmmunition }
    public enum WeaponCategory { All, Melee, Firearms, HeavyWeapons, Others }
    // Which loadouts the assignment considers at all. This used to be a bool `rolesOnly`, which could
    // only express two of the three cases:
    // "roles only" and "everything". The third was missing - and that is exactly what the assistant
    // promises in its own tooltip: "Ad-Hoc Loadouts per Pawn - Off = plain per-weapon loadouts". It
    // set the bool to false, i.e. "no restriction", and therefore kept handing out roles. Two values
    // cannot express that promise at all.
    // Mixed deliberately comes first: it is the previous behavior and the default.
    public enum RoleAssignMode { Mixed, RolesOnly, NoRoles }
    // Which loadouts the Assign tab offers/auto-assigns: all of them, only those whose main weapon you own, or only
    // those whose main weapon's recipe is researched.
    public enum AssignLoadoutFilter { All, AvailableMainWeapon, ResearchedMainWeapon }

    public class WeaponLoadoutSettings
    {
        // What remains of the Weapon Loadouts tab after the lazy-template rework: name and ammo amounts of a
        // template that EnsureDefaultLoadout creates in the background. The selection, loading and signature
        // fields of the old table editor are gone.
        public string loadoutName = "";
        public Dictionary<Def, string> ammoCounts = new Dictionary<Def, string>();
    }
}
