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

            // Jede Patch-Klasse EINZELN, nicht per PatchAll() (2026-08-09).
            //
            // PatchAll verarbeitet alle Klassen in einer Schleife und laesst eine HarmonyException nach oben
            // durch. Aendert Combat Extended eine Signatur - und mehrere unserer Ziele haengen an
            // Methodennamen als Text, etwa 'DoCell' am Loadout-Spaltenkopf -, bricht der erste Fehler die
            // GANZE Schleife ab. Alles, was danach an der Reihe gewesen waere, wird nie gepatcht.
            // Die Reihenfolge kommt aus der Reflection und ist nicht garantiert: WELCHE Haelfte des Mods
            // ueberlebt, waere damit praktisch Zufall - und der Spieler saehe einen halb funktionierenden Mod
            // ohne jeden Hinweis, warum.
            // Einzeln verarbeitet ueberlebt alles, was nicht selbst betroffen ist, und das Log nennt genau die
            // Klasse, die haengt. Ein kaputter Patch bleibt ein Fehler - er reisst nur nichts mehr mit.
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

            // CEs generische Loadout-Defs ("Any Meal", "Any Medicine", "Any ammo for <Waffe>") bekommen einen
            // echten Hash (2026-08-25: die Verfuegbarkeit der NAHRUNG wurde fuer viele Kolonisten rot, sobald
            // EIN Kolonist auf "Any ammo" wechselte, und wieder blau, sobald er zurueckwechselte).
            // Ursache, per Dekompilat belegt: Verse.Def.GetHashCode() liefert das Feld `defNameHash`, und
            // Def.Equals(Def) vergleicht genau dieses Feld (plus Typ). Gesetzt wird es nur durch
            // Def.ResolveDefNameHash(), das RimWorlds Def-Lader fuer XML-Defs aufruft. CE erzeugt seine
            // LoadoutGenericDefs aber im Code (Statikkonstruktor von LoadoutGenericDef: new LoadoutGenericDef()
            // + DefDatabase.Add) und ruft es nie - alle diese Defs tragen Hash 0 und gelten fuer jedes
            // Dictionary/HashSet mit Standardvergleich als EIN Schluessel. In der Reihum-Rechnung der
            // Verfuegbarkeit (Dialog_AssignLoadouts.BuildClaimsUncached) landete damit der Munitionsbedarf
            // (150 Schuss) unter demselben Schluessel wie "Any Perishable Meal"; der Nahrungstopf war nach dem
            // ersten Kolonisten aufgebraucht, und jeder danach las "fehlt".
            // Die Diagnose vom 2026-08-01 (DefRefComparer in LoadoutUtility) hatte das Symptom richtig und
            // die Ursache falsch: keine Kollision dreier Strings, sondern ein nie gesetztes Feld - bei ALLEN
            // Generics, also auch bei jedem Woerterbuch, das damals nicht umgestellt wurde.
            // Hier, nach allen Patches: frueher als jedes Fenster dieses Mods, und CE selbst haelt keine
            // hash-basierte Sammlung dieser Defs (DefDatabase nutzt Listen und ein Woerterbuch nach dem
            // defName-STRING). RunClassConstructor stellt sicher, dass CEs Statikkonstruktor die Defs schon
            // angelegt hat - er laeuft sonst erst bei der ersten Beruehrung des Typs. Idempotent: ein Def, der
            // den Hash schon traegt, wird nicht angefasst.
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
    // AvailableLoadouts ("Created loadouts") ist seit 2026-09-07 kein Filter mehr - der Wert bleibt nur,
    // damit eine gespeicherte Einstellung von vorher noch laedt; CEReloadedSettings biegt ihn auf
    // AssignedLoadouts um, und die Auswahl in den Einstellungen bietet ihn nicht an.
    public enum AmmoBillFilterMode { AssignedLoadouts, AvailableLoadouts, AvailableWeapons, ActiveBills, AllAmmunition }
    public enum WeaponCategory { All, Melee, Firearms, HeavyWeapons, Others }
    // Welche Loadouts die Zuweisung ueberhaupt in Betracht zieht (2026-08-19). Vorher war das ein bool
    // `rolesOnly`, und der konnte nur zwei der drei Faelle:
    // "nur Rollen" und "alles". Der dritte fehlte - und genau den verspricht der Assistent in seinem
    // eigenen Mouseover: "Ad-Hoc Loadouts per Pawn - Off = plain per-weapon loadouts". Er setzte den
    // bool auf false, also "keine Einschraenkung", und teilte deshalb weiter Rollen aus. Mit zwei
    // Werten laesst sich dieses Versprechen gar nicht ausdruecken.
    // Mixed steht bewusst an erster Stelle: das ist das bisherige Verhalten und der Standardwert.
    public enum RoleAssignMode { Mixed, RolesOnly, NoRoles }
    // Which loadouts the Assign tab offers/auto-assigns: all of them, only those whose main weapon you own, or only
    // those whose main weapon's recipe is researched.
    public enum AssignLoadoutFilter { All, AvailableMainWeapon, ResearchedMainWeapon }

    public class WeaponLoadoutSettings
    {
        // Was vom Waffen-Loadouts-Reiter uebrig ist (Lazy-Rueckbau 2026-09-17): Name und Munitionsmengen einer
        // Vorlage, die EnsureDefaultLoadout im Hintergrund anlegt. Auswahl-, Lade- und Signaturfelder des alten
        // Tabellen-Editors sind entfallen.
        public string loadoutName = "";
        public Dictionary<Def, string> ammoCounts = new Dictionary<Def, string>();
    }
}
