using System.Collections.Generic;
using System.Linq;
using Verse;
using CombatExtended;

namespace CEReloaded
{
    // Per-pawn hidden personal anchor ("[Pawn-Loadout] <pawnId>") - the ONE Loadout object a pawn with any kind
    // of personal customization is actually SetLoadout-assigned to (a pawn with none stays assigned
    // directly to their template, exactly as before this mechanism existed). Two independent things live
    // on it:
    //   - OwnSlots: personal extra ammo (Dialog_ChangeAmmo).
    //   - Extra parents (MultiParentUtility): Equipment Packs this pawn carries beyond their template - a
    //     LIVE reference, not a baked copy, so editing a Pack later is reflected automatically for
    //     everyone referencing it (the same guarantee CE's own native ParentLoadout chain already gives).
    // Anchored onto the pawn's real weapon-loadout template via CE's native single parentID - Loadout.
    // Slots (patched to also walk the extra-parents list, see Patch_Loadout_MultiParentSlots) is the
    // own+parent(+extra-parents) union every consumer in this mod reads (LoadoutMainAndSide,
    // SupplyRequirementsFor, AssignPlanner, ammo-need aggregation, and CE's OWN JobGiver_UpdateLoadout,
    // decompiled and confirmed to re-read a pawn's loadout.Slots fresh every evaluation cycle) - so
    // attaching or detaching a pack here needs nothing else, no Harmony hook into CE's hauling AI.
    //
    // This exists because the shared template can only ever have ONE such anchor pointing at it in a
    // meaningful, personal way - two pawns using the same "R| Assault Rifle" who want DIFFERENT packs
    // need their own attachment point, not a change to the shared template itself.
    public static class AmmoAssignmentUtility
    {
        // "[Ammo]" read as a player-facing name (it shows up if this leaks anywhere, e.g. a stale save from
        // before a bugfix) - "[Pawn-Loadout]" says what the object actually is instead. LegacyMarkerPrefix
        // keeps anchors from saves made before this rename fully working (still recognized, still cleaned
        // up if orphaned) - only NEWLY created anchors (EnsureAmmoLink) ever get the new prefix.
        public const string MarkerPrefix = "[Pawn-Loadout]";
        private const string LegacyMarkerPrefix = "[Ammo]";

        // Requires the numeric pawn-ID suffix EnsureAmmoLink always writes ("[Pawn-Loadout] Ryder 1003" -
        // the name is cosmetic/readability only, added 2026-07-30; older saves still have the bare-ID form
        // "[Pawn-Loadout] 1003"), not just the bracket prefix - a player's own pre-existing loadout that
        // happens to be named "[Pawn-Loadout] Sniper" (a perfectly reasonable thing to type) must never be
        // mistaken for one of our internal anchors. This matters most for CleanupOrphanedAnchors, which
        // DELETES anything this returns true for with no confirmation - a false positive there is silent
        // data loss, not just a display glitch. Checking only the LAST whitespace token is numeric (rather
        // than the old "whole remainder is JUST the number") is a real, if small, widening of that false-
        // positive surface - a coincidentally-named "[Pawn-Loadout] Backup 2" would now also match, where
        // it didn't before. Accepted deliberately for the name to be worth adding at all; a player would
        // still have to pick this exact unusual bracketed prefix AND end it with a number, and
        // CleanupOrphanedAnchors additionally only ever sweeps anchors no live pawn/caravan still points to.
        public static bool IsAmmoLink(Loadout lo)
        {
            if (lo == null || string.IsNullOrEmpty(lo.label)) return false;
            string prefix = lo.label.StartsWith(MarkerPrefix) ? MarkerPrefix
                : (lo.label.StartsWith(LegacyMarkerPrefix) ? LegacyMarkerPrefix : null);
            if (prefix == null) return false;
            string rest = lo.label.Substring(prefix.Length).Trim();
            string[] parts = rest.Split(' ');
            return parts.Length > 0 && int.TryParse(parts[parts.Length - 1], out _);
        }

        // What the player's UI should call "their loadout" - unwraps the hidden personal anchor back to
        // the real template underneath. Callers that show "current loadout" (gear tab, assign screen) want this.
        public static Loadout DisplayLoadout(Loadout assigned)
        {
            Loadout cur = assigned;
            int guard = 0;
            while (cur != null && IsAmmoLink(cur) && guard++ < 8)
            {
                if (cur.ParentLoadout == null) break;
                cur = cur.ParentLoadout;
            }
            return cur;
        }

        public static Loadout GetAmmoLeaf(Pawn pawn)
        {
            Loadout cur = pawn?.GetLoadout();
            return IsAmmoLink(cur) ? cur : null;
        }

        // Does this pawn actually carry a personalized combo (sidearm, pack, or personal ammo override) -
        // not just "is on a hidden anchor at all" (an anchor can theoretically exist with nothing on it,
        // e.g. mid-edit). Drives which rows get the "combo" repaint in CE's native loadout table - a pawn
        // still on their bare template should look exactly like CE's own unmodified UI, not be flagged as
        // special when there's nothing extra to show.
        public static bool HasPersonalization(Pawn pawn) =>
            SidearmAssignmentUtility.GetPawnSidearms(pawn).Count > 0
            || GetPawnPacks(pawn).Count > 0
            || ExtraAmmoSlots(pawn).Count > 0;

        // The FULL bundle a personalized pawn actually carries - the real template PLUS every Equipment
        // Pack and sidearm attached via the same hidden anchor (MultiParentUtility) - not just the bare
        // template name DisplayLoadout resolves to on its own. A Role loadout bakes main+sidearm+packs into
        // ONE object (RoleUtility.Merge) precisely so it reads as a single complete thing; a non-Role
        // pawn's bundle is spread across the anchor's extra-parents instead (by design - Roles stay
        // optional), so showing just the unwrapped template name silently hid that packs/sidearm apply at
        // all. Composed on the fly for DISPLAY only - never persisted, never fed back into anything that
        // reads a real Loadout object.
        public static string ComposedDisplayLabel(Pawn pawn)
        {
            Loadout template = DisplayLoadout(pawn?.GetLoadout());
            if (template == null) return "CER_AmmoAssignment_NoneLabel".Translate();
            // Ohne das kurze Praefix (2026-08-13, auch in CEs eigenem Menue):
            // dieser Text steht in CEs eigener Loadout-Spalte, wo der Platz noch knapper ist als in unserer
            // kompakten Zeile - und wo die Blaufaerbung (Patch_LoadoutTooltip) Rolle und Vorlage ohnehin schon
            // unterscheidet. Im Auswahlmenue bleibt der volle Name.
            string label = LoadoutUtility.CellLabel(template);
            List<Loadout> sidearms = SidearmAssignmentUtility.GetPawnSidearms(pawn);
            List<Loadout> packs = GetPawnPacks(pawn);
            List<LoadoutSlot> extraAmmo = ExtraAmmoSlots(pawn);
            if (sidearms.Count == 0 && packs.Count == 0 && extraAmmo.Count == 0) return label;

            var extras = new List<string>();
            // CellLabel auch fuer die Zweitwaffen (2026-08-14, im freien Spielen gefunden:
            // "M16A4 +W| M..." - die Hauptwaffe ohne Praefix, der Anhang mit, in DERSELBEN Zeile). Die
            // Hauptwaffe wird oben schon so behandelt, die Packs ueber StripMarker - nur die Zweitwaffen
            // standen roh da. Das Praefix ist Buchhaltung, kein Name, und es frisst ausgerechnet dort Breite,
            // wo am wenigsten ist (CEs eigene Loadout-Spalte).
            extras.AddRange(sidearms.Select(s => s != null
                ? LoadoutUtility.CellLabel(s)
                : "CER_AmmoAssignment_UnknownLabel".Translate().ToString()));
            extras.AddRange(packs.Select(p => EquipmentPackUtility.StripMarker(p.label)));
            if (extraAmmo.Count > 0) extras.Add("CER_AmmoAssignment_ExtraAmmoCount".Translate(extraAmmo.Count.ToString()));

            // Capped at 2 named extras regardless of how much is actually stacked (packs/sidearms are
            // uncapped - "beliebig viel stacken" - so the raw list has no natural upper bound) - the pixel-
            // width Truncate() at every display site already keeps this from overflowing its box, but a
            // bounded "+N more" reads better than an ellipsis cutting a name off mid-word, and keeps the
            // untruncated tooltip from growing without limit either.
            const int maxNamed = 2;
            List<string> shown = extras.Take(maxNamed).ToList();
            int remaining = extras.Count - shown.Count;
            string extrasText = string.Join(", ", shown) + (remaining > 0 ? "CER_AmmoAssignment_MoreSuffix".Translate(remaining.ToString()).ToString() : "");
            return "CER_AmmoAssignment_LabelWithExtras".Translate(label, extrasText);
        }

        // Every "[Pawn-Loadout]" anchor no live pawn anywhere (any map, any caravan) is actually assigned to
        // anymore - a dead, captured or otherwise removed colonist never cleans up their own anchor on the
        // way out, so these accumulate silently over a long playthrough otherwise. Shared by the Debug
        // settings tab's manual button and CEReloadedGameComponent's own periodic auto-cleanup, so both stay
        // exactly in sync with what "orphaned" means.
        public static List<Loadout> FindOrphanedAnchors()
        {
            HashSet<Loadout> referenced = new HashSet<Loadout>();
            // AllPawns, NICHT AllPawnsSpawned (2026-08-13). Ein Kolonist in
            // einer Kryptoschlafkammer, einer der gerade bewusstlos getragen wird, oder einer in einer
            // stehenden Transportkapsel ist NICHT gespawnt - sein Anker galt damit innerhalb einer
            // Spielstunde als verwaist und wurde geloescht. Ergebnis beim Aufwachen: keine Vorlage, keine
            // persoenliche Munition, keine Packs, keine Zweitwaffen, ohne jede Meldung.
            // Der Gegenbeweis stand im eigenen Code: LoadoutUtility.AllColonyPawns benutzt FreeColonists,
            // und das enthaelt die Unspawned sehr wohl - der Bereitschafts-Scan zaehlte ihn also weiter als
            // Kolonisten, waehrend diese Funktion ihn fuer tot hielt.
            // Dazu die Welt-Pawns: ausgeliehene Quest-Kolonisten stehen auf keiner Karte und in keiner
            // Karawane. Lieber ein verwaister Anker zu viel (kostet nichts) als einer zu wenig.
            //
            // 2026-08-15, dieselbe Luecke ein Halter weiter (Review-Befund): eine FLIEGENDE Transportkapsel
            // ist weder Karte noch Karawane, und ihre Insassen stehen auch nicht in den Welt-Pawns. Ein
            // Kapselflug ueber mehrere Felder dauert laenger als die 2500 Ticks zwischen zwei Laeufen - der
            // Kolonist waere also mitten im Flug entankert worden und ohne Vorlage, Packs, Zweitwaffen und
            // persoenliche Munition gelandet, genau das Schadensbild von oben.
            // Statt einen dritten Halter von Hand nachzutragen jetzt RimWorlds eigene Sammelabfragen, die
            // beide zusammen alles abdecken, was der Mod je anfassen kann - so muss diese Liste nicht bei
            // jedem neuen Halter (Gravship, Questgruppen) erneut nachgezogen werden.
            void AddAll(System.Collections.Generic.IEnumerable<Pawn> pawns)
            {
                if (pawns == null) return;
                foreach (Pawn p in pawns)
                {
                    Loadout lo = p?.GetLoadout();
                    if (lo != null) referenced.Add(lo);
                }
            }
            // Karten + Welt + "temporaer" (u.a. Gravship). Enthaelt KEINE fliegenden Transporter.
            AddAll(RimWorld.PawnsFinder.AllMapsWorldAndTemporary_Alive);
            // Karten + Karawanen + FLIEGENDE Transporter - die Haelfte, die der ersten Abfrage fehlt.
            AddAll(RimWorld.PawnsFinder.AllMapsCaravansAndTravellingTransporters_Alive);
            return LoadoutManager.Loadouts.Where(l => l != null && IsAmmoLink(l) && !referenced.Contains(l)).ToList();
        }

        // Removes every currently-orphaned anchor and returns how many were deleted.
        public static int CleanupOrphanedAnchors()
        {
            List<Loadout> orphans = FindOrphanedAnchors();
            // Ueber LoadoutDeletionUtility, nicht ueber RemoveLoadout direkt: ein Anker ist genau das Objekt,
            // dessen ID in ammoExplicitlyCleared und als Schluessel in loadoutExtraParents steht, und diese
            // Aufraeumung laeuft im Hintergrund weiter, waehrend CE die IDs recycelt.
            return LoadoutDeletionUtility.DeleteAll(orphans);
        }

        // Bakes a pawn's CURRENT full combo (main weapon + sidearm(s) + packs - whatever their .Slots union
        // resolves to right now, live references and all) into a brand new, independent Loadout object -
        // the same trick a Role already relies on to be a single, shareable, natively-pickable thing. Same
        // trade-off Roles already accept: a snapshot at save time, not a live reference - editing the
        // ORIGINAL packs/sidearm loadout afterwards does not retroactively update this baked copy. A plain
        // object with none of our internal markers, so it shows up in CE's own native pickers like any
        // hand-authored Weapon Loadout.
        public static Loadout BakeReusableLoadout(Pawn pawn)
        {
            Loadout current = pawn?.GetLoadout();
            if (current == null || current.Slots == null) return null;
            Loadout baked = new Loadout { defaultLoadout = false, label = "CER_AmmoAssignment_CustomLabelPrefix".Translate(ComposedDisplayLabel(pawn)) };
            foreach (LoadoutSlot s in current.Slots)
            {
                if (s.thingDef != null) baked.AddSlot(new LoadoutSlot(s.thingDef, s.count));
                else if (s.genericDef != null) baked.AddSlot(new LoadoutSlot(s.genericDef, s.count));
            }
            LoadoutManager.AddLoadout(baked);
            return baked;
        }

        // The right way to hand a pawn a NEW template loadout (a different weapon, a different Role, ...).
        // ALWAYS routes through the pawn's personal anchor (creating one on first assignment if it doesn't
        // exist yet) rather than ever assigning the shared template object directly - a bare
        // pawn.SetLoadout(template) would leave any later personal customization (packs, sidearm, extra
        // ammo) with nowhere of its own to live, and worse, ammo tracked for a pawn on the raw template is
        // invisible in CE's own native loadout editor (it has nowhere real to be stored without leaking
        // onto every OTHER pawn sharing that same template). Every "assign this real loadout to this pawn"
        // call site should go through this, not pawn.SetLoadout(...) directly (a plain unassign-to-None is
        // fine as-is - there is nothing left to preserve once nothing is assigned).
        public static void AssignTemplate(Pawn pawn, Loadout template)
        {
            if (pawn == null || template == null) return;
            EnsureAmmoLink(pawn, template);
        }

        // Get (or create) this pawn's personal anchor, parented onto `template`. Reassigning the pawn to
        // a different weapon/role re-parents the SAME anchor (keeping any extra ammo/packs they already
        // had) rather than spawning a new one every time this runs.
        public static Loadout EnsureAmmoLink(Pawn pawn, Loadout template)
        {
            if (pawn == null || template == null) return template;
            Loadout current = pawn.GetLoadout();
            if (IsAmmoLink(current))
            {
                if (current.parentID != template.UniqueID)
                {
                    // Assigning a Role starts clean (2026-08-01) - a Role is documented as
                    // "one flat shared Loadout, no pinning concept," so silently carrying over a PREVIOUS
                    // template's leftover sidearm/pack/personal-ammo overrides onto a brand-new Role
                    // contradicted that. ONE-TIME reset at the moment of assignment only, gated on the
                    // parentID actually changing (re-committing to the SAME role the pawn is already on does
                    // NOT re-clear) - the player can still freely personalize ammo/sidearm/packs on a
                    // Role-assigned pawn afterward via the exact same UI paths as any other pawn; nothing
                    // about the anchor mechanism itself gets locked, only this one transition moment.
                    if (RoleUtility.IsRoleLoadout(template))
                    {
                        current.OwnSlots.Clear();
                        MultiParentUtility.SetExtraParents(current, new List<Loadout>());
                    }
                    // Die eigene Munition der ALTEN Waffe geht mit ihr (2026-09-04).
                    //
                    // Der Fall: Yttoyyn trug eine M1917 Enfield mit persoenlich zugewiesenen 150 Schuss
                    // .30-06 (FMJ). Ueber die Waffensperre bekam sie eine Ruger Mini-14 zugewiesen - anderes
                    // Kaliber. Danach widersprachen sich zwei Anzeigen DESSELBEN Fensters: ihre Karte sagte
                    // "die Waffe braucht Munition, aber es ist keine eingerichtet", und die Kolonie-Rechnung
                    // fuehrte dieselben 150 Schuss weiter als "zugewiesen an Yttoyyn". Beide hatten recht.
                    //
                    // Warum beide: die persoenliche Munition liegt als eigener Slot AUF dem Anker, und ein
                    // eigener Ammo-Slot verdraengt die geerbte Munition der Vorlage (Patch_Loadout_
                    // MultiParentSlots). Der Anker wurde hier nur UMGEHAENGT - die alte Patrone blieb also
                    // liegen, blockierte die richtige und band 150 Schuss, die niemand mehr braucht.
                    //
                    // Der Weg ueber "Keins" raeumte das laengst auf, der Weg ueber
                    // einen WAFFENWECHSEL nicht - dieselbe Frage, zwei Rechnungen. Deshalb steht die Regel
                    // jetzt hier, an der EINEN Stelle, durch die jeder Vorlagenwechsel laeuft.
                    //
                    // Konservativ: raus fliegt nur KONKRETE Munition, die KEINE der jetzt erreichbaren
                    // Waffen verschiessen kann - die der neuen Vorlage, plus die aus angehaengten Packs und
                    // Zweitwaffen, plus Waffen, die als eigener Slot am Anker haengen. Ein generischer
                    // Platzhalter ("Beliebige Munition") loest sich ohnehin erst beim Lesen auf und bleibt
                    // unangetastet, und der Rollen-Zweig darueber hat schon alles geleert.
                    else DropAmmoNoWeaponCanUse(current, template);
                    current.parentID = template.UniqueID;
                }
                return current;
            }
            Loadout link = new Loadout { defaultLoadout = false, label = MarkerPrefix + " " + pawn.LabelShort + " " + pawn.thingIDNumber, parentID = template.UniqueID };
            LoadoutManager.AddLoadout(link);
            pawn.SetLoadout(link);
            // Hier stand vom 06.09. bis 07.09. eine Diagnosezeile. Sie hat den Fund
            // gebracht: der Anker wurde IMMER richtig gesetzt - verloren ging er erst beim Wiederoeffnen von
            // RimWorlds Zuweisungs-Reiter, durch Better Pawn Control (Compat_BetterPawnControl.cs).
            return link;
        }

        // Welche Munition kann nach dem Vorlagenwechsel ueberhaupt noch jemand verschiessen? Alles andere
        // ist tote Zuweisung - siehe die Begruendung an der Aufrufstelle in EnsureAmmoLink.
        private static void DropAmmoNoWeaponCanUse(Loadout anchor, Loadout template)
        {
            if (anchor == null) return;
            List<LoadoutSlot> concrete = anchor.OwnSlots
                .Where(s => s.thingDef != null && !s.thingDef.IsWeapon && LoadoutUtility.IsAmmoDef(s.thingDef))
                .ToList();
            if (concrete.Count == 0) return;

            var weapons = new HashSet<ThingDef>();
            void Collect(Loadout lo)
            {
                if (lo?.Slots == null) return;
                foreach (LoadoutSlot s in lo.Slots)
                    if (s.thingDef != null && s.thingDef.IsWeapon) weapons.Add(s.thingDef);
            }
            Collect(template);
            foreach (Loadout extra in MultiParentUtility.GetExtraParents(anchor)) Collect(extra);
            foreach (LoadoutSlot s in anchor.OwnSlots)
                if (s.thingDef != null && s.thingDef.IsWeapon) weapons.Add(s.thingDef);

            var usable = new HashSet<ThingDef>();
            foreach (ThingDef w in weapons)
                foreach (ThingDef a in LoadoutUtility.GetAmmoTypesFor(w))
                    if (a != null) usable.Add(a);

            foreach (LoadoutSlot dead in concrete.Where(s => !usable.Contains(s.thingDef)))
                anchor.RemoveSlot(dead);
        }

        // Does this pawn currently have any extra ammo set up at all? (gates the "Change ammo" button's
        // summary text without forcing an anchor to exist just to check.)
        public static List<LoadoutSlot> ExtraAmmoSlots(Pawn pawn)
        {
            Loadout lo = GetAmmoLeaf(pawn);
            return lo != null ? lo.OwnSlots.ToList() : new List<LoadoutSlot>();
        }

        // "Deliberately carries no ammo at all" - distinct from "no personal override yet" (an anchor with
        // zero ammo-type OwnSlots is ambiguous between the two; see Patch_Loadout_MultiParentSlots, which
        // reads this to decide whether to suppress the template's own inherited ammo down to nothing rather
        // than letting it silently show back up).
        public static bool IsAmmoCleared(Loadout anchor)
        {
            CEReloadedGameComponent comp = CEReloadedGameComponent.Cached;
            return anchor != null && comp != null && comp.ammoExplicitlyCleared.Contains(anchor.UniqueID);
        }

        public static void SetAmmoCleared(Loadout anchor, bool cleared)
        {
            CEReloadedGameComponent comp = CEReloadedGameComponent.Cached;
            if (anchor == null || comp == null) return;
            if (cleared) comp.ammoExplicitlyCleared.Add(anchor.UniqueID);
            else comp.ammoExplicitlyCleared.Remove(anchor.UniqueID);
        }

        // Every Equipment Pack this pawn is currently carrying - a live reference (MultiParentUtility),
        // not a baked copy, so editing a Pack later is reflected automatically. A pawn can carry several
        // at once - Equipment Packs are pure supply now (no weapon content competing for one "slot"), so
        // auto-assign hands out a fitting subset (see AssignPlanner.ChoosePacksFor).
        // The extra-parents list can also hold ONE sidearm Weapon Loadout (SidearmAssignmentUtility) -
        // filtered out here by content, so a sidearm never shows up misidentified as a pack.
        public static List<Loadout> GetPawnPacks(Pawn pawn)
        {
            Loadout leaf = GetAmmoLeaf(pawn);
            if (leaf == null) return new List<Loadout>();
            return MultiParentUtility.GetExtraParents(leaf).Where(EquipmentPackUtility.IsEquipmentPackLoadout).ToList();
        }

        // "Welchen Versorgungs-INHALT traegt dieser Pawn" - auf Slot-Ebene, nicht auf Pack-Objekt-Ebene
        // (2026-08-13). GetPawnPacks daneben beantwortet eine andere, ebenfalls richtige Frage ("welche
        // Pack-OBJEKTE haengen an ihm") und bleibt unveraendert - aber wer wissen will, WAS er traegt, war
        // damit bisher blind fuer alles, was ueber eine ROLLE kommt: eine Rolle haengt ihre Packs nicht als
        // Extra-Eltern an, RoleUtility.Merge backt den ganzen Inhalt in EIN flaches Loadout. Das ist der Sinn
        // einer Rolle, kein Fehler - aber jeder Aufrufer von GetPawnPacks hat role-getragene Versorgung
        // schlicht nicht gesehen.
        // Im Test sind davon vier Symptome aufgefallen, ohne die gemeinsame Ursache zu kennen: der
        // Verfuegbarkeits-Tooltip an der Pack-Zelle zeigte nur direkt angehaengte Inhalte (ausgerechnet die
        // knappe Medizin aus der Rolle fehlte), in "Assigned to" fehlte die Rollen-Traegerin ganz, ein
        // anderer Kolonist stand dort mit 2 statt 32, und die Bedarfszahlen waren entsprechend zu niedrig -
        // der Mod plante fuer einen Teil der Kolonie keinen Nachschub ein.
        //
        // Was als Pack-Inhalt einer Rolle zaehlt: alles, was KEINE Waffe, KEIN Schild und KEINE Munition ist.
        // Genau das ist die Definition eines Equipment Packs (reine Versorgung), nur hier aus der flachen
        // Union herausgelesen statt aus einem eigenen Objekt. Generische Eintraege zaehlen mit, ausser sie
        // sind Munitions-Platzhalter (GetWeaponForGenericAmmo != null).
        public static List<LoadoutSlot> CarriedPackSlots(Pawn pawn)
        {
            var result = new List<LoadoutSlot>();
            if (pawn == null) return result;
            foreach (Loadout pack in GetPawnPacks(pawn))
                if (pack?.OwnSlots != null) result.AddRange(pack.OwnSlots);

            result.AddRange(RoleSupplySlots(DisplayLoadout(pawn.GetLoadout())));
            return result;
        }

        // Dieselbe Frage fuer ein GEPLANTES Rollen-Loadout, das noch an keinem Pawn haengt (2026-08-13):
        // die Vorschau im Assign-Tab kennt nur den Plan, nie den lebenden Anker,
        // hat den Rollen-Inhalt deshalb an CarriedPackSlots vorbei gesehen - und ihn mangels Pack-Objekt in
        // die MUNITIONS-Gruppe gelegt. Eine Definition, zwei Einstiege.
        public static List<LoadoutSlot> RoleSupplySlots(Loadout role)
        {
            var result = new List<LoadoutSlot>();
            if (role == null || !RoleUtility.IsRoleLoadout(role) || role.Slots == null) return result;
            foreach (LoadoutSlot s in role.Slots)
                if (IsSupplySlot(s)) result.Add(s);
            return result;
        }

        // Ein Slot, der Versorgung ist - kein Kampfgeraet. Bewusst hier und nicht in jedem Aufrufer, damit
        // "was ist Pack-Inhalt" genau eine Definition hat.
        private static bool IsSupplySlot(LoadoutSlot s)
        {
            if (s == null) return false;
            if (s.thingDef != null)
                return !s.thingDef.IsWeapon
                    && !LoadoutUtility.IsShieldApparel(s.thingDef)
                    && !LoadoutUtility.IsAmmoDef(s.thingDef);
            if (s.genericDef != null)
                return LoadoutUtility.GetWeaponForGenericAmmo(s.genericDef) == null;
            return false;
        }

        // The single mutation entrypoint for "this pawn now carries exactly these packs" - used by both
        // the manual Assign Loadouts "Pack:" cell and the auto-assign apply step. A live reference, never
        // touches the shared template or copies any slots. Preserves whatever sidearm (SidearmAssignmentUtility)
        // already shares the same extra-parents list - this only ever touches the pack portion of it. An
        // empty combined result detaches - and cleans up the anchor entirely if it now carries nothing at
        // all (no extra ammo either), so detaching everything doesn't leave an inert hidden object behind.
        public static void SetPawnPacks(Pawn pawn, Loadout template, List<Loadout> packs)
        {
            if (pawn == null || template == null) return;
            List<Loadout> newPacks = (packs ?? new List<Loadout>()).Where(p => p != null).Distinct().ToList();
            Loadout existing = GetAmmoLeaf(pawn);
            List<Loadout> currentPacks = existing != null
                ? MultiParentUtility.GetExtraParents(existing).Where(EquipmentPackUtility.IsEquipmentPackLoadout).ToList()
                : new List<Loadout>();
            List<Loadout> others = existing != null
                ? MultiParentUtility.GetExtraParents(existing).Where(l => !EquipmentPackUtility.IsEquipmentPackLoadout(l)).ToList()
                : new List<Loadout>();
            List<Loadout> combined = others.Concat(newPacks).ToList();
            if (combined.Count == 0)
            {
                // Every loadout we assign lives on the pawn's anchor permanently now (see AssignTemplate) -
                // clearing the last pack just empties the extra-parents list, it never tears the anchor
                // down and reverts to a bare template assignment.
                if (existing == null) return;
                MultiParentUtility.SetExtraParents(existing, new List<Loadout>());
                UnseedOwnAmmoForDetached(existing, currentPacks.Concat(others));
                CombatReadiness.Invalidate();
                return;
            }
            Loadout leaf = EnsureAmmoLink(pawn, template);
            MultiParentUtility.SetExtraParents(leaf, combined);
            // Erst die abgenommenen aufraeumen, dann die neuen saeen - siehe UnseedOwnAmmoForDetached.
            UnseedOwnAmmoForDetached(leaf, currentPacks.Except(newPacks));
            // Same ammo-starvation-on-attach fix as SidearmAssignmentUtility.SetPawnSidearms (see its own
            // comment for the full mechanism) - packs are documented as never containing an ammo-consuming
            // weapon, but nothing structurally enforces that, so this stays a cheap no-op safety net rather
            // than an assumption.
            SeedOwnAmmoForNewlyAttached(leaf, newPacks.Except(currentPacks));
            // Readiness cache invalidation (2026-08-01: a pawn the widget flagged as
            // having a problem showed fully green/fine on their own Assign Loadouts card right after this
            // exact mutation path - this is the SINGLE shared entrypoint for attaching/detaching a pack, and
            // it never told CombatReadiness a real update just happened, same class of gap already fixed for
            // Dialog_AssignLoadouts' own commit paths).
            CombatReadiness.Invalidate();
        }

        // If this pawn already has a personal ammo override (Dialog_ChangeAmmo - any own ammo slot on their
        // anchor), attaching a NEW sidearm/pack whose own default ammo isn't part of that override yet would
        // otherwise vanish entirely: Patch_LoadoutMultiParent's own-ammo filter treats the anchor's own ammo
        // list as a COMPLETE replacement for every inherited ammo slot, including one just attached this
        // moment (2026-08-01: "attaching a sidearm to an already ammo-personalized pawn
        // silently starves the new weapon of ammo" - repro was a rifle+personalized-ammo pawn later getting
        // a pistol sidearm, whose caliber then generated zero demand, so CE never hauled ammo for it).
        // Scoped to just the NEWLY attached loadout(s)' own ammo types, not a full re-seed of everything
        // effective - that would silently overwrite whatever the player already chose for the main weapon
        // or an earlier sidearm/pack. Deliberately does NOT fire when the pawn instead explicitly cleared
        // ALL personal ammo (IsAmmoCleared) - that is a deliberate "no ammo at all" choice, not an
        // in-progress personalization, and a newly attached item should respect it the same way the
        // existing ones already do rather than silently reintroducing ammo demand.
        internal static void SeedOwnAmmoForNewlyAttached(Loadout anchor, IEnumerable<Loadout> newlyAttached)
        {
            if (anchor == null || newlyAttached == null) return;
            if (anchor.OwnSlots == null || !anchor.OwnSlots.Any(s => LoadoutUtility.IsAmmoDef(s.thingDef))) return;
            HashSet<ThingDef> haveOwn = anchor.OwnSlots.Where(s => LoadoutUtility.IsAmmoDef(s.thingDef)).Select(s => s.thingDef).ToHashSet();
            foreach (Loadout lo in newlyAttached)
            {
                if (lo?.Slots == null) continue;
                foreach (LoadoutSlot s in lo.Slots.Where(s => LoadoutUtility.IsAmmoDef(s.thingDef)))
                    if (haveOwn.Add(s.thingDef)) anchor.AddSlot(new LoadoutSlot(s.thingDef, s.count));
            }
        }

        // Die Gegenrichtung zu SeedOwnAmmoForNewlyAttached (2026-08-14). Das Saeen gab es seit dem
        // 2026-08-01, das Aufraeumen nie: eine abgenommene Zweitwaffe verliert nur ihre Elternverknuepfung,
        // ihre beim Anhaengen auf den PERSOENLICHEN Anker kopierte Munition bleibt dort fuer immer stehen.
        // Der Kolonist verlangt danach dauerhaft Munition fuer eine Waffe, die er nicht mehr traegt.
        //
        // Aufgefallen ist es am auffaelligsten bei Wurfsprengstoff, weil der SEINE EIGENE Munition ist
        // (IsSelfAmmoWeapon): der zurueckgebliebene Slot ist dann der Sprengsatz selbst, und der ist eine
        // WAFFE. Fehlszenario, am 14.08. Schritt fuer Schritt belegt - Zweitwaffe
        // "W| satchel charge" abgehakt (Zelle zeigt danach das rote X, sie ist wirklich weg), Nachschub-
        // Fenster geoeffnet: der Sprengsatz steht weiter als zu produzieren drin, mit LEERER Assig-Spalte,
        // und taucht gleichzeitig NICHT unter "ueberfluessige Auftraege" auf. Beides erklaert derselbe
        // Rest-Slot: ComputeWeaponDemand sieht ihn (Waffenslot -> Auftrag gilt als gebraucht),
        // ComputeAmmoNeedForLoadouts ueberspringt ihn (IsSelfAmmoWeapon -> keine Zuweisung in der Raute).
        //
        // Entfernt wird NUR, was beides erfuellt: es kam aus dem gerade abgenommenen Loadout, UND keine der
        // noch getragenen Waffen kann es verschiessen. Damit bleibt eine persoenliche Munitionswahl fuer
        // eine weiterhin getragene Waffe unangetastet - das ist der ganze Zweck des Ankers.
        // GetLoadoutAmmoDefs mit IsSelfAmmoWeapon, nicht Dialog_ChangeAmmo.RelevantAmmoPool: der Pool laesst
        // Wurfsachen ausdruecklich aus (er beantwortet "welche Sorten darf der Spieler hier auswaehlen"),
        // und genau die duerfen hier nicht durchs Raster fallen.
        internal static void UnseedOwnAmmoForDetached(Loadout anchor, IEnumerable<Loadout> detached)
        {
            if (anchor?.OwnSlots == null || detached == null) return;
            // Einmal festhalten: die Aufrufer reichen LINQ-Ausdruecke herein, und diese Funktion laeuft
            // zweimal darueber (konkrete Munition, dann die Platzhalter).
            List<Loadout> detachedList = detached.Where(l => l != null).ToList();
            HashSet<ThingDef> fromDetached = new HashSet<ThingDef>();
            foreach (Loadout lo in detachedList)
            {
                if (lo?.Slots == null) continue;
                foreach (LoadoutSlot s in lo.Slots)
                    if (LoadoutUtility.IsAmmoDef(s.thingDef)) fromDetached.Add(s.thingDef);
            }
            if (fromDetached.Count == 0) return;
            // Nach dem Umhaengen gelesen, nicht davor: `Slots` ist die Vereinigung aus eigenen Slots und
            // den VERBLIEBENEN Eltern, also genau das, was der Kolonist ab jetzt traegt.
            HashSet<ThingDef> stillRelevant = new HashSet<ThingDef>();
            foreach (LoadoutSlot s in anchor.Slots ?? new List<LoadoutSlot>())
            {
                ThingDef w = s.thingDef;
                if (w == null || !w.IsWeapon) continue;
                foreach (Def ammo in LoadoutUtility.GetLoadoutAmmoDefs(w, LoadoutUtility.IsSelfAmmoWeapon(w)))
                    if (ammo is ThingDef atd) stillRelevant.Add(atd);
            }
            foreach (LoadoutSlot own in anchor.OwnSlots.ToList())
                if (own.thingDef != null && fromDetached.Contains(own.thingDef) && !stillRelevant.Contains(own.thingDef))
                    anchor.RemoveSlot(own);

            // DASSELBE fuer den generischen Platzhalter (2026-08-18). Der Block darueber raeumt nur
            // KONKRETE Munition weg - ein "Any ammo for <Waffe>", das der Kolonist fuer die abgenommene
            // Zweitwaffe gesetzt hatte, blieb fuer immer auf dem Anker stehen. Das ist derselbe Schaden,
            // gegen den die Funktion 2026-08-14 geschrieben wurde, nur ueber den anderen Weg: der
            // Platzhalter loest sich in Patch_Loadout_MultiParentSlots wieder in echte Munition auf, der
            // Kolonist verlangt sie also weiter fuer eine Waffe, die er nicht mehr traegt. Sichtbar wurde
            // es an einer ganz anderen Stelle - der Kopieren-Knopf im Munitionsfenster zaehlte drei
            // Eintraege, wo das Fenster zwei zeigte.
            // Ein Platzhalter nennt seine Waffe selbst, die Bedingung ist also enger als oben: weg, wenn
            // die Waffe aus dem gerade Abgenommenen kam UND keine verbliebene Waffe dieselbe ist.
            HashSet<ThingDef> weaponsFromDetached = new HashSet<ThingDef>();
            foreach (Loadout lo in detachedList)
            {
                if (lo?.Slots == null) continue;
                foreach (LoadoutSlot s in lo.Slots)
                    if (s.thingDef != null && s.thingDef.IsWeapon) weaponsFromDetached.Add(s.thingDef);
            }
            if (weaponsFromDetached.Count > 0)
            {
                HashSet<ThingDef> stillCarriedWeapons = new HashSet<ThingDef>();
                foreach (LoadoutSlot s in anchor.Slots ?? new List<LoadoutSlot>())
                    if (s.thingDef != null && s.thingDef.IsWeapon) stillCarriedWeapons.Add(s.thingDef);
                foreach (LoadoutSlot own in anchor.OwnSlots.ToList())
                {
                    if (!LoadoutUtility.IsGenericAmmoDef(own.genericDef)) continue;
                    ThingDef forWeapon = LoadoutUtility.GetWeaponForGenericAmmo(own.genericDef);
                    if (forWeapon != null && weaponsFromDetached.Contains(forWeapon) && !stillCarriedWeapons.Contains(forWeapon))
                        anchor.RemoveSlot(own);
                }
            }
        }
    }
}
