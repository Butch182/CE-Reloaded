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

        // WORAUS die Rolle zusammengesetzt wurde - die Etiketten der Quell-Loadouts, in der Reihenfolge des
        // Draufziehens (2026-08-15).
        //
        // Warum es das braucht: eine fertige Rolle ist ein FLACHGEKLOPFTES Loadout (RoleUtility.
        // BuildRoleLoadout mischt die Quellen zusammen und multipliziert die Mengen). Aus dem Ergebnis
        // laesst sich nicht mehr ablesen, aus welchen Loadouts es entstanden ist. Die Zusammensetzung lebte
        // deshalb nur in einem Laufzeit-Woerterbuch des Dialogs und war weg, sobald man das Fenster zumachte.
        // Folge - genau das Fehlerbild: Rolle auswaehlen, EIN Pack draufziehen, Update, und die Waffe ist weg,
        // weil "die Rolle ist genau das, was in der Reihe liegt" auf eine leere Reihe traf.
        //
        // Etiketten und nicht UniqueIDs: CE vergibt IDs als Max+1 und benutzt sie nach dem Loeschen WIEDER.
        // Eine gespeicherte ID koennte also spaeter auf ein voellig anderes Loadout zeigen - ein still
        // falscher Inhalt statt einer sichtbar fehlenden Zeile. Ein umbenanntes Quell-Loadout faellt beim
        // naechsten Auswaehlen einfach aus der Reihe; das ist der harmlosere Ausgang, und der Mod sucht
        // seine Rollen ohnehin ueberall ueber das Etikett.
        public List<string> composition = new List<string>();

        // ============ WELCHES Loadout diese Rolle IST - ueber die Identitaet, nicht ueber den Namen ============
        //
        // 2026-08-17, Grundsatz: nichts mehr an irgendwelchen Praefixen erkennen - die sollen nur noch
        // aesthetisch sein und im Nachgang hinzugefuegt werden, nicht im String stehen.
        //
        // Bis hierher lief BEIDES ueber das Etikett: ob ein Loadout eine Rolle IST (label.StartsWith("[Role]"))
        // und WELCHES Loadout zu einer Rolle gehoert (label == "[Role] " + name). Deshalb stand im Reiter die
        // Warnung "benenne diese Loadouts nicht um" - sie war richtig, und sie war das Symptom.
        //
        // Die ID ist hier ausdruecklich das RICHTIGE Mittel, obwohl der Kommentar ueber `composition` sagt,
        // dass IDs recycelt werden. Der Unterschied ist das Aufraeumen: eine verwaiste ID wird beim Loeschen
        // geputzt (LoadoutDeletionUtility.ForgetSideBooks raeumt sie ueberall weg, genau wie die vier anderen
        // Nebenbuecher). Bei `composition` geht das nicht, weil dort FREMDE Loadouts referenziert werden, die
        // jemand anders loescht - hier zeigt die Zahl auf das eigene Loadout der Rolle.
        // 0 heisst "noch nicht verknuepft". Der Altbestand wird EINMAL beim Laden nachgetragen
        // (CEReloadedGameComponent.AdoptMarkersOnce) - danach fragt nirgends mehr jemand das Etikett.
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
            // Ein Spielstand von vor dieser Aenderung hat den Knoten nicht, und Scribe_Collections setzt
            // die Liste dann auf null - jede spaetere Nutzung waere ein Absturz. Solche Rollen bleiben
            // bewusst OHNE Zusammensetzung: raten waere schlimmer als eine leere Reihe (siehe oben).
            if (Scribe.mode == LoadSaveMode.PostLoadInit && composition == null) composition = new List<string>();
        }

        public LoadoutRole Clone() => new LoadoutRole
        {
            name = name, ammoFactor = ammoFactor, medicineFactor = medicineFactor,
            foodFactor = foodFactor, grenadeFactor = grenadeFactor,
            composition = composition != null ? new List<string>(composition) : new List<string>()
            // loadoutId wird BEWUSST nicht mitkopiert: eine Kopie ist eine neue Rolle und bekommt ihr
            // eigenes Loadout, sobald sie zum ersten Mal geschrieben wird. Uebernaehme sie die ID, zeigten
            // zwei Rollen auf dasselbe Loadout und wuerden sich gegenseitig ueberschreiben.
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
        // EIN gemerkter Zugriff auf diese Komponente (2026-08-14, aus der zweiten Performance-Runde).
        // `Game.GetComponent<T>()` geht die Komponentenliste des Spiels durch und macht pro Eintrag einen
        // Typtest. Das steht an CEs heissestem Getter: `Loadout.Slots` laeuft ueber
        // Patch_Loadout_MultiParentSlots, und der ruft pro Zugriff ZWEI davon (MultiParentUtility.Map und
        // AmmoAssignmentUtility.IsAmmoCleared) - waehrend CEs eigener Think-Tree den Getter pro Kolonist
        // und Entscheidung anfasst, auch ohne offenes Mod-Fenster.
        // Gemerkt am Game-OBJEKT, nicht ueber eine Zeitschranke: solange ein Spiel laeuft, ist die Antwort
        // unveraenderlich (RimWorld baut alle GameComponents mit dem Game), und ein neues oder geladenes
        // Spiel IST ein anderes Objekt. Damit kann dieser Cache nicht veralten, ohne dass irgendwo an ein
        // Aufraeumen gedacht werden muss - genau der Fehler, den ein TTL-Cache hier machen wuerde.
        // Verhaelt sich sonst exakt wie der alte Ausdruck: null, solange kein Spiel laeuft.
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
        // Welche Loadouts EQUIPMENT PACKS sind - ueber die Identitaet statt ueber das Etikett (2026-08-17,
        // gleicher Grundsatz: nichts an Praefixen erkennen). Dieselbe Bauform wie managedLoadoutIds daneben:
        // beim Anlegen eintragen, beim Loeschen austragen (LoadoutDeletionUtility - Pflicht, weil CEs IDs
        // recycelt werden), und der "[Pack]"-Marker im Etikett bleibt nur noch Notnagel fuer Altbestand.
        public HashSet<int> packLoadoutIds = new HashSet<int>();

        // Production bills OUR wizard actually created (LoadoutUtility.MarkBillAsManaged, called from both
        // AddWeaponBillOnce and AddOrUpdateAmmoBill) - keyed by Bill.GetUniqueLoadID() (a stable, save-
        // surviving string ID every vanilla Bill already carries via ILoadReferenceable), not object
        // reference (a Bill instance doesn't survive a save/load round-trip, but its loadID does). Lets
        // Arm-my-Colony's "Delete ammo/weapon bills" switches offer a precise "delete only what THIS mod
        // queued" scope alongside the existing blunt "delete everything for a recognized type" one, instead
        // of only ever being able to do the latter (2026-08-01).
        // Bis 2026-08-15 stand hier "wird nie aufgeraeumt - eine tote ID ist harmlos, weil nur ueber noch
        // existierende Auftraege iteriert wird". Fuer die KORREKTHEIT stimmt das weiterhin. Fuer die GROESSE
        // nicht: die Menge wuchs mit jedem je erzeugten Auftrag, wurde nie kleiner und wird mitgespeichert -
        // ueber eine lange Partie mit regelmaessigem Nachschub-Assistenten sind das tausende tote
        // Zeichenketten in jedem Spielstand. Seit 2026-08-15 raeumt LoadoutUtility.PruneManagedBillIds am
        // Aufraeum-Tick auf (einmal je Spielstunde, zusammen mit den verwaisten Ankern).
        public HashSet<string> managedBillIds = new HashSet<string>();

        // Colonists the PLAYER declared off-limits for arming, keyed by Pawn.thingIDNumber (stable within a
        // save, unlike an object reference). Deliberately fed into LoadoutUtility.CannotFight rather than
        // checked separately: that one rule is what the solver, the readiness scan, the Arm-my-Colony preview
        // and both Assign screens already agree on, so a flagged colonist goes grey and gets skipped
        // everywhere at once instead of in whichever places got remembered (2026-08-09: merges the
        // long-standing "mark a non-combatant" wish with the finding that the popup's green check did
        // nothing on a colonist who has no loadout yet).
        public HashSet<int> excludedPawnIds = new HashSet<int>();

        // Vom Spieler GESPERRTE Waffen (2026-09-03): die Automatik
        // vergibt sie nie, im Auswahlfenster stehen sie ausgegraut am Gruppenende und lassen sich dort
        // wieder entsperren. defNames statt ThingDef-Referenzen - stabil im Spielstand, und eine Waffe
        // aus einem spaeter entfernten Mod verschwindet einfach still aus der Menge.
        public HashSet<string> disabledWeapons = new HashSet<string>();

        // Dasselbe fuer ROLLEN und handgebaute Loadouts (2026-09-04): nicht nur Waffen, auch Rollen und
        // Loadouts lassen sich sperren, mit allen Folgen. Hier stehen UniqueIDs statt defNames, denn ein
        // Loadout hat keinen defName -
        // und weil CE seine IDs nach dem Loeschen WIEDERVERWENDET (Max+1), muss diese Menge beim Loeschen
        // mitgeputzt werden, sonst gilt die naechste Vorlage mit derselben Nummer als gesperrt.
        // Das erledigt LoadoutDeletionUtility.ForgetSideBooks - die eine Stelle, an der jede ID-Liste
        // dieses Mods aufgeraeumt wird.
        public HashSet<int> disabledLoadouts = new HashSet<int>();

        // Welches Equipment Pack der Generator fuer welchen seiner Plaene angelegt hat - Planname ->
        // Loadout.UniqueID. Bis 2026-08-09 erkannte der Generator "gibt es das schon" ausschliesslich am
        // LABEL-Text, mit zwei Folgen: ein umbenanntes Pack galt beim naechsten Durchlauf als neu und wurde
        // ein zweites Mal angelegt, und zwei Packs mit gleichem Namen waren nicht auseinanderzuhalten.
        // Der Name bleibt als Rueckfallebene fuer Packs, die vor dieser Aufzeichnung entstanden sind.
        public Dictionary<string, int> packPlanIds = new Dictionary<string, int>();

        public CEReloadedGameComponent(Game game) { }

        // ============ EINMALIGE UEBERNAHME: Marker -> Identitaet (2026-08-17) ============
        //
        // Sein Auftrag: "schau ob jetzt wirklich kein code mehr nach den prefixes sucht, egal ob waffe,
        // sidearm, equipment pack oder rolle, das muss raus".
        //
        // Ohne diese Stelle koennten die Rueckfaelle in IsRoleLoadout/IsEquipmentPackLoadout/
        // IsGeneratedTemplate nicht verschwinden: alles, was VOR der Umstellung entstanden ist, kennt seine
        // Identitaet nicht und wuerde beim Wegfall des Markers schlagartig zu einem gewoehnlichen Loadout -
        // Rollen waeren keine Rollen mehr, Packs keine Packs. Das waere kein Anzeigefehler, sondern
        // Datenverlust in der Wahrnehmung des Spiels.
        //
        // Deshalb wird EINMAL beim Laden nachgetragen, was der Marker noch weiss. Danach ist der Marker
        // reine Kosmetik und niemand fragt ihn mehr. Der Durchlauf ist billig (eine Schleife ueber die
        // Loadout-Liste) und idempotent - HashSets und eine ID-Zuweisung, die schon stimmt.
        //
        // Bewusst KEINE Migrations-Versionsnummer: der Durchlauf darf jedes Mal laufen. Eine Zaehlung, die
        // sich merkt "schon erledigt", waere genau die Sorte Zustand, die beim naechsten Sonderfall falsch
        // steht - und hier kostet das Wiederholen nichts.
        public override void FinalizeInit() => AdoptMarkersOnce();

        public override void LoadedGame() => AdoptMarkersOnce();

        private void AdoptMarkersOnce()
        {
            List<Loadout> all = LoadoutManager.Loadouts;
            if (all == null) return;

            // Rollen: ueber den Namen zuordnen, denn genau das war bisher die Verbindung. Ab jetzt haelt
            // die ID, und der Name ist frei. Der Rumpf steht in RoleUtility, weil ihn seit 2026-08-18 auch
            // der Zuruecksetzen-Knopf braucht - siehe dort.
            RoleUtility.AdoptRoleLoadoutsByName();

            foreach (Loadout lo in all)
            {
                if (lo == null || lo.defaultLoadout || lo.label == null) continue;
                if (EquipmentPackUtility.IsEquipmentPackLabel(lo.label)) packLoadoutIds.Add(lo.UniqueID);
                // Waffen-Vorlagen: die Vorsilbe ist einstellbar und steht standardmaessig auf "keine".
                // Deshalb beide Schreibweisen pruefen - die eingestellte und die historische "W|".
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
                // Dieselbe Hygiene fuer die Auftrags-Merkliste: sie wuchs mit jedem je erzeugten Auftrag
                // und wurde nie kleiner, obwohl sie im Spielstand mitgeschrieben wird (2026-08-15).
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

        // ============ Erkennung ueber die Identitaet, nicht ueber den Namen (2026-08-17) ============
        //
        // Ein Loadout ist genau dann eine Rolle, wenn irgendeine LoadoutRole darauf zeigt. Das Etikett spielt
        // dafuer keine Rolle mehr - es ist der erste Schritt zu seinem Auftrag "nichts mehr an praefixes
        // erkennen".
        //
        // IsRoleLabel bleibt als RUECKFALL, und zwar nur fuer den einen Fall, den es noch geben muss: eine
        // Rolle aus einem Spielstand von vor dieser Aenderung hat loadoutId == 0. Sobald sie einmal
        // angefasst wird, schreibt RoleLoadoutOf die Verknuepfung fest, und ab da zaehlt nur noch die.
        // Der Rueckfall ist damit selbstheilend und verschwindet von allein aus dem Bestand.
        //
        // Gecacht, weil das hier pro Zeile pro Bild gefragt wird (CEs eigene Loadout-Spalte, unsere Zellen,
        // die FloatMenu-Faerbung). Der Schluessel ist die Rollenzahl plus die Loadout-Zahl - beides aendert
        // sich genau dann, wenn sich die Antwort aendern kann.
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

        // Global (Mod-Einstellungen) UND spielstandlokal - eine Rolle kann in beiden Listen leben, und die
        // Frage "ist das eine Rolle" darf nicht davon abhaengen, wo sie gespeichert ist.
        public static List<LoadoutRole> AllKnownRoles()
        {
            var all = new List<LoadoutRole>();
            if (CEReloadedMod.settings?.roles != null) all.AddRange(CEReloadedMod.settings.roles);
            CEReloadedGameComponent comp = CEReloadedGameComponent.Cached;
            if (comp?.localRoles != null) all.AddRange(comp.localRoles);
            return all;
        }

        // Das Loadout DIESER Rolle. Erst ueber die gemerkte Identitaet, sonst einmalig ueber das Etikett -
        // und dann wird die Identitaet festgeschrieben, damit ein spaeteres Umbenennen sie nicht mehr
        // zerreissen kann. Das ist die Stelle, die die Warnung im Reiter ueberfluessig macht.
        public static Loadout RoleLoadoutOf(LoadoutRole role)
        {
            if (role == null || LoadoutManager.Loadouts == null) return null;
            if (role.loadoutId != 0)
            {
                Loadout byId = LoadoutManager.Loadouts.FirstOrDefault(l => l != null && l.UniqueID == role.loadoutId);
                if (byId != null) return byId;
                // Zeigt ins Leere - das Loadout wurde ausserhalb unserer Loeschroutine entfernt. Verknuepfung
                // aufgeben und unten normal weitersuchen, statt eine tote Zahl mitzuschleppen.
                role.loadoutId = 0;
                roleIdCacheKey = -1;
            }
            // Kein Etiketten-Rueckfall mehr: die Zuordnung ueber den Namen passiert genau EINMAL beim Laden
            // (AdoptMarkersOnce). Findet sich hier nichts, hat die Rolle wirklich noch kein Loadout - und
            // dann ist "null" die richtige Antwort, nicht eine Suche nach ihrem Namen.
            return null;
        }

        // Jede Rolle OHNE Verknuepfung an ein vorhandenes "[Role] <Name>"-Loadout binden. Genau die
        // Zuordnung, die beim Laden einmal laeuft (CEReloadedGameComponent.AdoptMarkersOnce) - hier
        // herausgeloest, weil sie seit 2026-08-18 an einer zweiten Stelle gebraucht wird.
        //
        // Anlass (Befund mit Screenshot): nach "Zuruecksetzen" im Rollen-Reiter standen ploetzlich
        // "[Role] Support", "[Role] Breacher" und "[Role] Assault" in der Liste "Your weapon" - also unter
        // den Dingen, die man auf eine Rolle ZIEHEN kann. Danach liess sich eine Rolle in eine Rolle legen,
        // und die Vorschau zeigte zwei Hauptwaffen mit Ueberlast.
        // Die Ursache ist genau hier: Zuruecksetzen ersetzt die globalen Rollen durch frische Vorgaben, und
        // eine frische Rolle hat loadoutId 0. Ihr altes Loadout lebt aber weiter - es gehoert ab diesem
        // Moment zu keiner Rolle mehr, und ohne Besitzer ist es fuer jede Identitaetspruefung ein ganz
        // gewoehnliches Loadout. Die Erkennung war nicht kaputt, sie hatte nur nichts mehr zu erkennen.
        // Das Zuordnen nach dem Zuruecksetzen schliesst die Luecke an der Wurzel, statt sie hinterher
        // wieder am Etikett aufzufangen.
        //
        // Rueckgabe: wieviele Verknuepfungen neu entstanden sind (fuer die Meldung am Knopf).
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

        // Wieviele Rollen wuerden ihre BAUSTEINE verlieren, wenn diese Loadouts verschwinden (2026-08-18)?
        // Eine Rolle merkt sich, AUS WELCHEN Vorlagen sie zusammengesetzt wurde - nicht deren Inhalt, den
        // hat sie beim Speichern flachgeklopft. Verschwinden die Quellen, behaelt sie also ihren Inhalt
        // (jeder Kolonist darauf bleibt ausgeruestet), aber ihre Reihe ist leer und sie laesst sich nicht
        // mehr sinnvoll nachjustieren.
        // Das ist kein Fehler, sondern die Folge des Schnappschuss-Prinzips - es ueberrascht nur, wenn es
        // niemand sagt. Genau dafuer ist diese Zahl da.
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

        // Aufgerufen von LoadoutDeletionUtility - ohne das zeigte eine gemerkte ID nach dem Loeschen auf ein
        // RECYCELTES fremdes Loadout (CEs IDs sind Max+1 und werden wiederverwendet), und eine beliebige
        // neue Vorlage haette sich ploetzlich als Rolle ausgegeben.
        public static void ForgetRoleLoadout(int loadoutId)
        {
            foreach (LoadoutRole r in AllKnownRoles())
                if (r != null && r.loadoutId == loadoutId) r.loadoutId = 0;
            roleIdCacheKey = -1;
        }

        // KEIN Etiketten-Rueckfall mehr (2026-08-17). Was es vor der
        // Umstellung schon gab, wird beim Laden EINMAL nachgetragen - siehe
        // CEReloadedGameComponent.AdoptMarkersOnce. Ab hier entscheidet allein die Identitaet.
        public static bool IsRoleLoadout(Loadout lo) => lo != null && RoleLoadoutIds().Contains(lo.UniqueID);
        public static bool IsRoleLabel(string label) => !string.IsNullOrEmpty(label) && label.StartsWith(RoleMarker);

        // Player-facing label for a Role loadout - strips the invariant "[Role] " identity marker and
        // applies the same kind of clean, configurable prefix Weapon Loadouts/Equipment Packs already have
        // (2026-08-02: Roles showed their raw internal marker everywhere, the only one of
        // the three template types that did). Purely cosmetic - RoleMarker/IsRoleLabel above, and every
        // internal identity check, are completely untouched, so this can never un-recognize a Role.
        // Reicht seit 2026-08-16 an LoadoutUtility.LoadoutDisplayName durch - EINE Stelle fuer alle
        // Namen, statt dass jede Stelle selbst bastelt. Der Name bleibt, damit die knapp zwanzig
        // Aufrufstellen lesbar bleiben und nicht alle in einem Zug angefasst werden mussten.
        // markerShownElsewhere: false - diese Fassung wird dort benutzt, wo nur Text steht (Menues,
        // Meldungen, CEs eigene Spalte). Wo ein Symbol oder eine Farbe danebensteht, nimmt der Aufrufer
        // LoadoutUtility.CellLabel.
        public static string DisplayLabel(Loadout lo) => LoadoutUtility.LoadoutDisplayName(lo, false);

        // Populate the role list with sensible presets the first time it is used. Factors only, so no def lookups needed.
        public static void EnsureSeeded(CEReloadedSettings settings)
        {
            if (settings == null) return;
            if (settings.roles == null) settings.roles = new List<LoadoutRole>();
            if (settings.roles.Count == 0) settings.roles.AddRange(DefaultRoles());
        }

        // ============ Zuruecksetzen der GLOBALEN Rollen - an EINER Stelle (2026-09-04) ============
        //
        // Es gab zwei Knoepfe dafuer (Rollen-Reiter und Mod-Einstellungen), und sie machten
        // Verschiedenes: der eine ordnete die alten Loadouts wieder zu, der andere nicht. Beiden
        // gemeinsam war der Fehler, den auch der Loeschknopf hatte - eine Rolle mit einem NICHT in den
        // Vorgaben vorkommenden Namen ("Agent Rifleman") verschwindet beim Zuruecksetzen, ihr Loadout
        // "[Role] Agent Rifleman" aber nicht. Ohne Rolle dahinter ist es fuer jede Identitaetspruefung
        // eine gewoehnliche Vorlage: die Zuweisung verteilt sie weiter (auch unter "Keine Rollen"), und
        // zwar unter ihrem rohen Etikett samt "[Role]"-Marker.
        //
        // Reihenfolge hier ist wesentlich: erst einsammeln, WAS den Rollen jetzt gehoert (nach dem
        // Clear ist die Verknuepfung nicht mehr erreichbar), dann zuruecksetzen, dann die gleichnamigen
        // wieder zuordnen - und erst danach loeschen, was danach noch immer herrenlos ist.
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
            // Gleichnamige Vorgaberollen uebernehmen ihr altes Loadout zurueck (2026-08-18): sonst standen
            // "[Role] Support" & Co. in der ZIEH-Liste und eine Rolle liess
            // sich in eine Rolle legen).
            AdoptRoleLoadoutsByName();
            var stillOwned = new HashSet<int>();
            foreach (LoadoutRole r in AllKnownRoles())
                if (r != null && r.loadoutId != 0) stillOwned.Add(r.loadoutId);
            LoadoutDeletionUtility.DeleteAll(owned.Where(l => !stillOwned.Contains(l.UniqueID)));
        }

        // ============ Eine geloeschte Vorgaberolle zurueckholen, OHNE alles andere wegzuwerfen ============
        //
        // 2026-09-04, Befund im Test: eine Waffe auf die leere Vorlagenkarte "[G] Assault" gezogen, danach
        // das x gedrueckt, um es rueckgaengig zu machen - und Assault war aus der GLOBALEN Bibliothek
        // verschwunden. Nicht auf die leere Vorlage zurueckgefallen: weg. Die sieben Vorgaberollen sind der
        // einzige Startpunkt fuer Rollen, und der einzige Weg zurueck war "Voreinstellungen zuruecksetzen" -
        // das nimmt jede selbst gebaute Rolle mit. Eine Falle mit sehr hohem Preis fuer einen Fehlklick.
        //
        // Bewusst NICHT gebaut: "Loeschen macht aus einer Vorgaberolle wieder eine leere Vorlage". Loeschen
        // soll loeschen - wer Assault nicht will, soll ihn loswerden koennen, und eine Karte, die nach dem
        // Loeschen wiederkommt, ist ihr eigener Fehlerbericht. Gebaut ist statt dessen der fehlende Rueckweg:
        // was von den sieben fehlt, kommt einzeln zurueck, an seinen Platz in der Vorgabereihenfolge, und
        // nichts anderes wird angefasst.
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
                // An den Platz, den die Vorgabe hat - eine wiederhergestellte Rolle soll nicht als
                // Anhaengsel am Ende stehen (genau das passierte beim Reparieren von Hand im Test:
                // "die Rolle steht jetzt am ENDE der Liste statt am Anfang").
                int want = presets.FindIndex(d => d.name == m.name);
                int at = Mathf.Clamp(want < 0 ? settings.roles.Count : want, 0, settings.roles.Count);
                settings.roles.Insert(at, m);
            }
            // Ein bereits vorhandenes, herrenloses "[Role] <Name>"-Loadout gehoert der zurueckgeholten
            // Rolle wieder - dieselbe Adoption, die auch das Zuruecksetzen benutzt.
            AdoptRoleLoadoutsByName();
            return missing.Count;
        }

        // Ist DIESE Rolle eine der sieben eingebauten Vorlagen (nach Namen)? Nur fuer den Rueckfragetext -
        // Loeschen selbst verhaelt sich fuer alle Rollen gleich.
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
                    // Equipment Packs werden NICHT mehr eingeschmolzen (2026-08-09): echte Verbindungen auch
                    // bei Rollen (Referenzen). Sie haengen jetzt als echte Referenz an der
                    // Rolle, genau wie an einem einzelnen Kolonisten - siehe PacksIn/CommitRole.
                    // Vorher verhielt sich dasselbe Pack unterschiedlich, je nachdem wie es angehaengt wurde:
                    // am Pawn blieb es verbunden, in einer Rolle wurde beim Anlegen nur eine Kopie gezogen und
                    // spaetere Aenderungen kamen dort nie an. Alle fuenf Spieler-Simulationen haben das
                    // unabhaengig als die groesste Verstaendnisfalle des Mods benannt.
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

        // Die Equipment Packs aus einer Zusammenstellung - die Teile, die als echte Referenz an die Rolle
        // gehaengt werden statt eingeschmolzen zu werden (2026-08-09). Die Rollen-Skalierungsfaktoren wirken
        // auf sie folglich NICHT mehr: eine Referenz ist der Pack-Inhalt, nicht eine skalierte Kopie davon.
        // Das ist der Preis der Live-Verbindung und ausdruecklich gewollt - eine Zahl, die sich beim naechsten
        // Oeffnen des Packs wieder aendert, waere sonst nur eine Kopie mit Extraschritten.
        public static List<Loadout> PacksIn(IEnumerable<Loadout> dragged)
        {
            List<Loadout> packs = new List<Loadout>();
            if (dragged == null) return packs;
            foreach (Loadout src in dragged)
                if (src != null && EquipmentPackUtility.IsEquipmentPackLoadout(src) && !packs.Contains(src)) packs.Add(src);
            return packs;
        }

        // Die Slots, mit denen EINE Zeile der Zusammenstellung in die Rolle eingeht: ein Equipment Pack
        // haengt als Referenz dran (OwnSlots, wie die Vorschau es liest), alles andere wird eingeschmolzen
        // (Slots, wie Merge es liest). Beide Fundstellen gab es vorher getrennt; als geteilte Funktion,
        // weil eine Karte, die eine andere Slot-Quelle liest als das spaetere Ergebnis, genau die Art
        // stiller Falschanzeige ist, die hier schon zweimal aufgetreten ist.
        public static IEnumerable<LoadoutSlot> CompSlots(Loadout src)
        {
            if (src == null) return Enumerable.Empty<LoadoutSlot>();
            IEnumerable<LoadoutSlot> slots = EquipmentPackUtility.IsEquipmentPackLoadout(src) ? src.OwnSlots : src.Slots;
            return slots ?? Enumerable.Empty<LoadoutSlot>();
        }

        public static bool CompHasDef(Loadout src, ThingDef def)
            => def != null && CompSlots(src).Any(s => s.thingDef == def);

        // Welche Waffe der Zusammenstellung als HAUPTWAFFE gilt - dieselbe Kuer, die der Mod spaeter auf dem
        // fertigen Rollen-Loadout faehrt, nur schon vorher (LoadoutUtility.MainAndSideFrom).
        // Hintergrund (2026-08-14): eine zweite Waffe auf
        // einer Rolle ERSETZT die erste bewusst nicht - eine Rolle soll Hauptwaffe + Messer + Granate
        // buendeln duerfen. Was fehlte, war die Ansage, WELCHE der Waffen die Hauptwaffe wird; die
        // Rollen-Karte kennzeichnet damit ihren Chip.
        // Der Name geht als das Etikett hinein, das BuildRoleLoadout spaeter wirklich vergibt, damit die
        // Namens-Regel in LoadoutNameMatchesWeapon hier und dort dasselbe Ergebnis liefert.
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
