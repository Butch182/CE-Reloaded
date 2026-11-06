using System.Collections.Generic;
using System.Linq;
using RimWorld;
using UnityEngine;
using Verse;
using CombatExtended;

namespace CEReloaded
{
    // AssignedNotCarried (2026-08-09): der eine Zustand, den keine der
    // sieben anderen Kategorien traf. Ein Kolonist MIT echtem Loadout, der GAR KEINE Waffe in der Hand
    // hat, waehrend seine zugewiesene Waffe im Lager liegt, faellt sonst durch alles durch: NoLoadout
    // greift nicht (er hat ja eins), WeaponUnavailable greift nicht (die Waffe existiert ja), und LowAmmo
    // haengt an der getragenen Waffe, die es nicht gibt. Ergebnis war gruenes 'Ready' bei leeren Haenden -
    // genau das Szenario, wegen dem es diesen Mod gibt.
    public enum ReadinessIssue { NoLoadout, WeaponWithoutAmmo, AmmoNotProduced, LowAmmo, Overloaded, WeaponUnavailable, CapacityBlocked, AssignedNotCarried }

    // Shared, cached combat-readiness scan. RimWorld's alert readout polls each alert on a rotating schedule, so all
    // three alerts read from ONE computation that refreshes at most every few seconds. This is also the backbone the
    // in-tab warnings / auto-manage phases will reuse. Purely read-only - it never changes game state.
    public static class CombatReadiness
    {
        private const int RecomputeInterval = 250;   // ~4 seconds at normal speed

        private static int lastTick = -99999;
        private static readonly Dictionary<ReadinessIssue, List<Pawn>> culprits = new Dictionary<ReadinessIssue, List<Pawn>>
        {
            { ReadinessIssue.NoLoadout,         new List<Pawn>() },
            { ReadinessIssue.WeaponWithoutAmmo, new List<Pawn>() },
            { ReadinessIssue.AmmoNotProduced,   new List<Pawn>() },
            { ReadinessIssue.LowAmmo,           new List<Pawn>() },
            { ReadinessIssue.Overloaded,        new List<Pawn>() },
            { ReadinessIssue.WeaponUnavailable, new List<Pawn>() },
            { ReadinessIssue.CapacityBlocked,   new List<Pawn>() },
            { ReadinessIssue.AssignedNotCarried, new List<Pawn>() },
        };

        public static List<Pawn> Culprits(ReadinessIssue issue)
        {
            EnsureFresh();
            return culprits[issue];
        }

        // Forces the next Culprits() call to recompute instead of trusting the tick-interval cache - needed
        // because virtually every dialog this mod opens (Dialog_ArmColony, Dialog_CEReloadedTabs itself,
        // Dialog_SupplyLoadouts, ...) sets forcePause = true, which freezes Find.TickManager.TicksGame for as
        // long as ANY of them is open. RecomputeInterval (250 ticks) never naturally elapses across a whole
        // paused session, so a background tab left open behind Arm my Colony kept showing pre-run readiness
        // numbers (still "9 have no loadout" right after assigning all 9) until the player unpaused for
        // several real seconds afterward (2026-08-01 - also explains the "ghost ammo bills"
        // symptom: the alert's own culprit lists, not the actual Bill objects, were what was stale). Called at
        // the one moment this session already treats as "a real update just happened, tell everything to
        // refresh" - Dialog_ArmColony's own Arm() - rather than threading invalidation through every single
        // mutation site across the mod.
        public static void Invalidate() { lastTick = -99999; MutationVersion++; }

        // Zaehler jeder gemeldeten Aenderung an Zuweisungen/Ankern (2026-08-25): die
        // Zeichen-Caches des Assign-Reiters (Reihum-Rechnung, Zeileninfo, Munitionszelle, Bestandstopf)
        // wurden bis heute in JEDEM Bild neu gebaut - bei 143 Hz rund 90 Neuaufbauten je Sekunde fuer ein
        // pausiertes Spiel, in dem sich zwischen zwei Bildern nichts aendert, ausser der Spieler klickt.
        // Jetzt gilt die Zwei-Sperren-Regel: ein Cache lebt, bis diese
        // Zahl sich aendert ODER ein kurzes Hoechstalter erreicht ist. Invalidate() ist seit 2026-08-01 der
        // eine Punkt, den jede Mutation des Mods ohnehin ruft - der Zaehler haengt sich nur dazu; eine
        // vergessene Mutationsstelle macht den Wert damit fuer den Bruchteil einer Sekunde alt, nie
        // dauerhaft falsch.
        public static int MutationVersion { get; private set; }

        // No longer keyed on "did the viewed map change" - the scan itself is empire-wide now (every colony map,
        // every caravan), so a mech assault on an outpost still raises the alert while the player is looking at
        // the main base. Just a plain tick-interval cache.
        // Echtzeit-Gegenstueck zu RecomputeInterval - vier Sekunden reichen, um eine Aenderung im
        // pausierten Spiel zu bemerken, ohne im laufenden Spiel zusaetzlich zu rechnen.
        private const float RealSecondsInterval = 4f;
        private static float lastReal = -9999f;

        // NUR solange wirklich ein Fenster dieses Mods offen ist (2026-08-15: ein ueber Nacht laufender
        // PC drehte morgens den Luefter hoch).
        //
        // Die Echtzeit-Schranke ist am 09.08. fuer GENAU EINEN Fall entstanden: unsere Fenster pausieren
        // das Spiel, die Spielzeit steht still, und die Tick-Schranke taute deshalb nie auf. Sie hing aber
        // an der blossen Uhr - und die laeuft auch dann, wenn RimWorld unberuehrt im Hauptmenue-Nichts
        // steht. Ergebnis: alle vier Sekunden ein voller Durchlauf ueber alle Werkbaenke, alle Auftraege,
        // alle Waffen jeder Karte und alle Kolonisten - die ganze Nacht durch, ohne dass sich irgendetwas
        // aendern koennte. Das ist kein Speicherleck, sondern Dauerlast; sie erklaert einen drehenden
        // Luefter besser als wachsender Speicher, weil sie pro Durchlauf gleich teuer bleibt.
        //
        // Ist kein Fenster von uns offen, gilt wieder allein die Tick-Schranke: laeuft das Spiel, tickt es
        // und wir rechnen wie bisher alle 250 Ticks; ist es pausiert, aendert sich ohne unsere Fenster
        // ohnehin nichts, was diese Zahlen bewegen koennte.
        //
        // Ueber den Namensraum statt ueber eine Liste von Fenstertypen: eine Aufzaehlung waere beim
        // naechsten neuen Dialog still unvollstaendig, und dann kaeme der Fehler von 2026-08-01 zurueck
        // (Zahlen von vorher hinter einem offenen Tab).
        private static bool AnyModWindowOpen()
        {
            var stack = Find.WindowStack;
            if (stack == null) return false;
            IList<Window> open = stack.Windows;
            for (int i = 0; i < open.Count; i++)
                if (open[i] != null && open[i].GetType().Namespace == "CEReloaded") return true;
            return false;
        }

        private static void EnsureFresh()
        {
            int now = Find.TickManager != null ? Find.TickManager.TicksGame : 0;
            // Rueckwaerts gesprungene Spielzeit heisst: anderes Spiel (Hauptmenue -> neue Kolonie oder
            // ein aelterer Spielstand). lastTick ist statisch und ueberlebt den Spielwechsel, also war
            // now - lastTick dann stark NEGATIV und damit fuer immer < RecomputeInterval - der Cache
            // taute nie wieder auf und lieferte Pawn-Objekte aus dem weggeworfenen Spiel. Ein Klick auf
            // die Meldung sprang dann auf eine Karte, die es nicht mehr gibt.
            if (now < lastTick) lastTick = -99999;
            // Zweite Bremse an der ECHTEN Uhr (2026-08-09): die Spielzeit steht in der Pause still, und
            // jedes Fenster dieses Mods pausiert automatisch. Der Cache taute dadurch nie auf, solange
            // man in einem Mod-Fenster war - genau im Panik-Ablauf vor einem Raid (pausieren, von Hand
            // ausruesten, aufs Licht schauen) zeigte er die Zahlen von vorher.
            // Beide Schranken muessen halten: erst wenn Spielzeit ODER Echtzeit weit genug ist, wird neu
            // gerechnet. Das haelt die Last im laufenden Spiel unveraendert niedrig.
            float rt = Time.realtimeSinceStartup;
            bool tickStale = (now - lastTick) >= RecomputeInterval;
            bool realStale = (rt - lastReal) >= RealSecondsInterval && AnyModWindowOpen();
            if (!tickStale && !realStale) return;
            lastReal = rt;
            lastTick = now;
            foreach (List<Pawn> l in culprits.Values) l.Clear();
            Recompute();
        }

        private static void Recompute()
        {
            // 1. Everything currently being produced by a non-suspended colonist bill (products of any kind),
            // on any colony map.
            HashSet<ThingDef> produced = new HashSet<ThingDef>();
            foreach (Building_WorkTable bench in LoadoutUtility.AllColonyBenches())
            {
                if (bench.billStack == null) continue;
                foreach (Bill_Production bill in bench.billStack.Bills.OfType<Bill_Production>())
                {
                    if (bill.suspended || bill.recipe == null || bill.recipe.products == null) continue;
                    foreach (ThingDefCountClass p in bill.recipe.products)
                        if (p.thingDef != null) produced.Add(p.thingDef);
                }
            }

            int stockThreshold = 0;
            if (CEReloadedMod.settings != null) int.TryParse(CEReloadedMod.settings.alertAmmoStockThreshold, out stockThreshold);

            // 2. "Wem fehlt welche Waffe" - der Reihum-Anspruch auf den freien Bestand steht seit
            // 2026-08-17 in Dialog_AssignLoadouts.WeaponShortfallsByPawn und wird von dort gelesen, statt
            // hier ein zweites Mal gerechnet zu werden. Grund und Fehlerbild: siehe die Kopfzeile dort
            // (diese Warnung meldete eine Luecke, die das Nachschub-Fenster mit
            // seiner eigenen Rechnung nicht kannte). Waffen mit "nicht aufsammeln" zaehlen weiterhin als
            // Bestand (2026-08-09) - eine Warnung "niemand kann bewaffnet werden", waehrend zwanzig
            // Gewehre auf dem Boden liegen, war die Beschwerde, aus der diese Regel kam.
            Dictionary<Pawn, Dictionary<ThingDef, int>> weaponShortfalls = Dialog_AssignLoadouts.WeaponShortfallsByPawn();

            HashSet<Pawn> noLoadout = new HashSet<Pawn>();
            HashSet<Pawn> noAmmo = new HashSet<Pawn>();
            HashSet<Pawn> notProduced = new HashSet<Pawn>();
            HashSet<Pawn> lowAmmo = new HashSet<Pawn>();
            HashSet<Pawn> overloaded = new HashSet<Pawn>();
            HashSet<Pawn> weaponUnavail = new HashSet<Pawn>();
            HashSet<Pawn> capacityBlocked = new HashSet<Pawn>();
            HashSet<Pawn> assignedNotCarried = new HashSet<Pawn>();

            foreach (Pawn pawn in LoadoutUtility.AllColonyPawns())
            {
                Loadout lo = pawn.GetLoadout();
                // Aufgeloest entschieden, roh gelesen (2026-08-13): lo ist bei jedem personalisierten
                // Kolonisten sein persoenlicher Anker, und der ist NIE das Default-Loadout - "hat eine
                // Vorlage" war damit an dieser Stelle immer wahr, sobald ein Anker existiert, auch wenn
                // darunter gar keine Vorlage mehr haengt (geloescht, oder aufs Default umgehaengt). Genau
                // dann fehlte der Kolonist in "kein Loadout", obwohl er keins hat. Fuer den INHALT bleibt es
                // bei lo.Slots: die Vereinigung ueber die ganze Kette ist das, was er wirklich traegt.
                Loadout display = AmmoAssignmentUtility.DisplayLoadout(lo);
                bool hasRealLoadout = display != null && !display.defaultLoadout;

                // Never been through the workflow at all: no loadout, no CE-managed ammo/weapon restock. The most
                // fundamental readiness gap - without this, a colony that never generated/assigned a single loadout
                // reads as "Ready" (every other check only fires once a real loadout exists to be incomplete).
                // Pacifists and babies can never fight, so a missing loadout is not an issue for them.
                if (!hasRealLoadout && !LoadoutUtility.CannotFight(pawn))
                    noLoadout.Add(pawn);

                // Inhalt auch pruefen, wenn nur Zweitwaffen ueber CEs None-Loadout haengen (2026-08-25):
                // "kein Loadout" bleibt fuer die Zeile oben richtig (keine Hauptwaffe), aber die
                // Munition der Hellebarde oder des Revolvers daran will genauso geprueft sein.
                bool hasSidearmsOnly = !hasRealLoadout && (SidearmAssignmentUtility.GetPawnSidearms(pawn)?.Count ?? 0) > 0;
                if ((hasRealLoadout || hasSidearmsOnly) && lo.Slots != null)
                {
                    // Ammo the loadout carries (concrete + generic placeholders).
                    HashSet<ThingDef> loadoutAmmo = new HashSet<ThingDef>();
                    HashSet<LoadoutGenericDef> loadoutGeneric = new HashSet<LoadoutGenericDef>();
                    List<ThingDef> ammoWeapons = new List<ThingDef>();
                    List<ThingDef> loadoutWeapons = new List<ThingDef>();
                    foreach (LoadoutSlot slot in lo.Slots)
                    {
                        if (slot.genericDef != null) loadoutGeneric.Add(slot.genericDef);
                        if (slot.thingDef == null) continue;
                        bool realWeapon = slot.thingDef.IsWeapon && !LoadoutUtility.IsSelfAmmoWeapon(slot.thingDef);
                        if (realWeapon)
                            for (int k = 0, need = slot.count < 1 ? 1 : slot.count; k < need; k++) loadoutWeapons.Add(slot.thingDef);
                        if (realWeapon && LoadoutUtility.GetAmmoSet(slot.thingDef) != null)
                            ammoWeapons.Add(slot.thingDef);
                        else if (realWeapon && slot.thingDef.GetCompProperties<CompProperties_AmmoUser>() != null)
                            // Comp present (marked as a CE ammo-using weapon) but the ammo SET itself never
                            // resolved (broken/incomplete 3rd-party CE patch) - this weapon can NEVER carry
                            // valid ammo, a worse case than "picker came back empty after resolving" (which
                            // WeaponWithoutAmmo/AmmoNotProduced below already catch via ammoWeapons). Without
                            // this branch such a weapon silently skips every ammo check with zero alert
                            // (2026-08-01, systemic blind spot, shared with Dialog_WeaponAudit's
                            // own check #3, which gated the same way).
                            noAmmo.Add(pawn);
                        else if (!realWeapon)
                            loadoutAmmo.Add(slot.thingDef);   // ammo/consumables only - weapons (incl. melee) never belong here
                    }

                    // Zugewiesen, traegt aber GAR NICHTS (2026-08-09). Bewusst nur dieser eine, harte Fall -
                    // nicht "irgendein Slot fehlt noch": Letzteres traefe in jeder lebenden Kolonie staendig zu
                    // (jemand ist immer gerade unterwegs zum Regal) und die Warnung waere binnen einer Stunde
                    // Tapete. "Loadout sagt Waffe, Haende leer" ist dagegen der Zustand, in dem jemand im
                    // Gefecht wirklich nichts zu tun hat.
                    // equipment.Primary, nicht das Inventar: eine Waffe im Rucksack ist zwar nicht gezueckt,
                    // aber CE zieht sie im Kampf selbst - leere Haende UND leeres Inventar waere zu eng.
                    if (loadoutWeapons.Count > 0
                        && (pawn.equipment == null || pawn.equipment.Primary == null)
                        && (pawn.inventory?.innerContainer == null
                            || !pawn.inventory.innerContainer.Any(t => loadoutWeapons.Contains(t.def)))
                        && !LoadoutUtility.CannotFight(pawn))
                        assignedNotCarried.Add(pawn);

                    // Eine Loadout-Waffe, die der Kolonist nicht schon haelt, die auch nicht mehr frei im
                    // Lager liegt (der Reihum-Anspruch oben hat sie einem anderen zugeschlagen) und fuer
                    // die kein Auftrag laeuft. Die MENGE kommt aus der gemeinsamen Rechnung; hier steht nur
                    // noch, worueber diese Warnung ueberhaupt spricht:
                    // - laeuft ein Auftrag, ist es keine Luecke mehr, sondern Wartezeit (produced);
                    // - Wurfsprengsaetze/Granaten sind Verbrauchsgut, nicht "die Waffe fehlt ihm"
                    //   (IsSelfAmmoWeapon - sie stehen bewusst im Munitions-Zweig, siehe loadoutAmmo);
                    // - Schilde meldet diese Warnung weiterhin nicht (das Nachschub-Fenster kennt sie,
                    //   diese Warnung hat sie noch nie gemeldet - das bleibt eine eigene Entscheidung).
                    if (weaponShortfalls.TryGetValue(pawn, out Dictionary<ThingDef, int> missingGear)
                        && missingGear.Keys.Any(w => !produced.Contains(w)
                            && !LoadoutUtility.IsSelfAmmoWeapon(w)
                            && !LoadoutUtility.IsShieldApparel(w)))
                        weaponUnavail.Add(pawn);

                    foreach (ThingDef weapon in ammoWeapons)
                    {
                        List<ThingDef> concrete = LoadoutUtility.GetAmmoTypesFor(weapon).ToList();
                        LoadoutGenericDef generic = LoadoutUtility.GetGenericAmmoDef(weapon);

                        bool hasAmmo = concrete.Any(a => loadoutAmmo.Contains(a)) || (generic != null && loadoutGeneric.Contains(generic));
                        if (!hasAmmo) noAmmo.Add(pawn);

                        bool anyProduced = concrete.Any(a => produced.Contains(a));
                        // "Not in production" is only actionable when the ammo can actually be produced - it has a
                        // researched recipe. If nothing is researched yet, the real problem is research/availability
                        // (covered by the other alerts), not a missing bill, so don't raise this one.
                        bool anyResearched = concrete.Any(a => LoadoutUtility.IsAmmoResearched(a));
                        if (!anyProduced && anyResearched)
                        {
                            // With a stock threshold set, a big reserve counts as covered even without a running bill.
                            if (stockThreshold <= 0) notProduced.Add(pawn);
                            else
                            {
                                int owned = concrete.Sum(a => LoadoutUtility.PlayerMaps().Sum(map => map.listerThings.ThingsOfDef(a).Sum(t => LoadoutUtility.IsHiddenInFog(t) ? 0 : t.stackCount)));
                                if (owned < stockThreshold) notProduced.Add(pawn);
                            }
                        }
                    }

                    // Too full to fetch the rest of the loadout: bulk/weight already maxed, so CE can't queue a
                    // pickup for anything still missing no matter how much is in storage - a real "own carry
                    // capacity" problem the player can't fix by producing more, distinct from WeaponWithoutAmmo
                    // (which only checks "carries SOME usable ammo at all", so a pawn who already has their
                    // primary round but is stuck full before a SECOND wanted type - e.g. an AP top-up - ever
                    // gets fetched reads as "fine" there). Checked against every individual ammo def/generic the
                    // loadout actually lists, not just per-weapon "has any".
                    CompInventory capInv = pawn.TryGetComp<CompInventory>();
                    if (capInv != null && (capInv.currentBulk >= capInv.capacityBulk - 0.01f || capInv.currentWeight >= capInv.capacityWeight - 0.01f))
                    {
                        HashSet<ThingDef> carriedDefs = new HashSet<ThingDef>();
                        if (pawn.inventory != null && pawn.inventory.innerContainer != null)
                            foreach (Thing t in pawn.inventory.innerContainer) carriedDefs.Add(t.def);
                        // Equipped Primary (e.g. a melee main weapon) never shows up in inventory.innerContainer -
                        // without this it reads as "missing" and falsely blocks a fully-equipped pawn.
                        if (pawn.equipment != null && pawn.equipment.Primary != null) carriedDefs.Add(pawn.equipment.Primary.def);
                        // Dieselbe Begruendung fuer GETRAGENE Kleidung (2026-08-13):
                        // ein Schild ist Apparel und stand deshalb nie in dieser Menge, obwohl loadoutAmmo es
                        // enthaelt (dort steht jeder Nicht-Waffen-Slot). Fehlszenario: Nahkaempfer mit Shield
                        // Kit, traegt sein Schild, Bulk am Anschlag -> stand dauerhaft unter "Capacity blocked",
                        // obwohl er alles hat. Diese Warnung ging nie wieder weg.
                        if (pawn.apparel != null && pawn.apparel.WornApparel != null)
                            foreach (Apparel a in pawn.apparel.WornApparel) carriedDefs.Add(a.def);
                        if (loadoutAmmo.Any(a => !carriedDefs.Contains(a))) capacityBlocked.Add(pawn);
                    }
                }

                // Low on ammo: the pawn's equipped CE gun vs. spare ammo it is actually carrying.
                // Die Rechnung selbst steht seit 2026-08-13 in LowAmmoState - siehe dort, warum: der
                // Klick-Weg der Bereitschaftsanzeige muss dieselbe Schwelle und dieselbe getragene Menge
                // kennen, sonst beantwortet er eine andere Frage als die, die die Warnung gestellt hat.
                if (LowAmmoState(pawn, out LowAmmoInfo lowInfo) && lowInfo.IsLow) lowAmmo.Add(pawn);

                // Overloaded: over CE weight or bulk capacity (slows the pawn, hurts aim/dodge).
                CompInventory inv = pawn.TryGetComp<CompInventory>();
                if (inv != null && (inv.currentWeight > inv.capacityWeight || inv.currentBulk > inv.capacityBulk))
                    overloaded.Add(pawn);
            }

            culprits[ReadinessIssue.NoLoadout].AddRange(noLoadout);
            culprits[ReadinessIssue.WeaponWithoutAmmo].AddRange(noAmmo);
            culprits[ReadinessIssue.AmmoNotProduced].AddRange(notProduced);
            culprits[ReadinessIssue.LowAmmo].AddRange(lowAmmo);
            culprits[ReadinessIssue.Overloaded].AddRange(overloaded);
            culprits[ReadinessIssue.WeaponUnavailable].AddRange(weaponUnavail);
            culprits[ReadinessIssue.CapacityBlocked].AddRange(capacityBlocked);
            culprits[ReadinessIssue.AssignedNotCarried].AddRange(assignedNotCarried);
        }

        // ---- "Low on ammo" - der Zustand DAHINTER, nicht nur das Ja/Nein ----------------------------
        //
        // 2026-08-13: ein Klick auf "low ammo", der zu einer Loesung fuehren soll, oeffnete ein
        // Supply-Loadouts-Fenster, das nur anbot, Satchel Charges abzubrechen - Unsinn.
        // Die Sache zerlegt sich so:
        // "Low on ammo" hat DREI Ursachen, und jede braucht eine ANDERE Antwort.
        //   A) Der Kolonist hat die Munition schlicht noch nicht abgeholt (sie liegt im Lager oder ist in
        //      Produktion) -> die Loesung ist warten, nicht produzieren.
        //   B) Sein Loadout bzw. seine Rolle sieht von vornherein weniger vor, als die Warnschwelle
        //      verlangt -> dann wird die Warnung NIE verschwinden, egal wie voll das Lager ist. Das ist der
        //      Fall, den bisher niemand behandelt hat, und der unangenehmste: eine Warnung, die man nicht
        //      wegbekommen kann.
        //   C) Die Munition fehlt wirklich und niemand stellt sie her -> Nachschub-Fenster, und darin MUSS
        //      genau diese Munition stehen.
        //
        // Diese Struktur ist die gemeinsame Grundlage fuer die Warnung selbst UND fuer den Klick-Weg in
        // LoadoutUtility. Bewusst EINE Rechnung: die Warnung sagt "zu wenig", der Klick muss von derselben
        // Schwelle und derselben getragenen Menge ausgehen, sonst schickt er den Spieler wegen einer
        // anderen Zahl irgendwohin.
        public enum LowAmmoCause
        {
            Pending,        // A - Bestand oder laufender Auftrag decken die Luecke, es ist unterwegs
            PlanTooSmall,   // B - das Loadout/die Rolle fordert weniger als die Warnschwelle
            NotProduced     // C - die Luecke ist ungedeckt, es entsteht auch nichts
        }

        public struct LowAmmoInfo
        {
            public ThingWithComps weapon;
            public List<ThingDef> usableAmmo;   // alles, was diese Waffe verschiessen kann
            public int carried;                 // was er davon wirklich bei sich hat
            public int threshold;               // was er haben muesste, damit die Warnung schweigt
            public int planned;                 // was sein Loadout/seine Rolle ihm ueberhaupt zugesteht
            // Die zwei Zahlen, an denen Pending gegen NotProduced entschieden WIRD (2026-08-17,
            // lowammo-three-answers): sie standen bisher nur als lokale Variablen in der Rechnung, und die
            // Anzeige hat sie sich daneben ein zweites Mal ausgerechnet - mit einer anderen Munitionsmenge
            // (nur die GELADENE Sorte) und einer anderen Bestandsfunktion. Ergebnis im Test: der
            // Tooltip sagte "none in stock, not queued", das Menue derselben Anzeige "supplies are on the
            // way". Wer den Grund anzeigen will, nimmt jetzt diese Felder.
            // Seit 2026-08-30 nur noch ueber die Sorten, die sein LOADOUT auch verlangt (Fall: Cloud stand als
            // 'supplies in stock, not picked up' da, weil 20 Schuss einer Sorte lagen, die sein SKS zwar verschiesst,
            // sein Loadout aber nicht fordert - CEs Abhol-Job holt nur, was im Loadout steht, also war da nichts
            // abzuholen; seine Sorte wurde gerade produziert). Fordert das Loadout keine konkrete Sorte (nur
            // 'Any ammo'), gelten wie bisher alle nutzbaren.
            public int reachable;               // greifbarer Bestand ueber die GEPLANTEN Sorten, ohne Gesperrtes
            public List<ThingDef> plannedAmmo;  // die Sorten aus seinem Loadout (leer = generisch/alle)
            public List<ThingDef> AmmoToCheck => plannedAmmo != null && plannedAmmo.Count > 0 ? plannedAmmo : usableAmmo;
            public bool anyBill;                // laeuft fuer irgendeine davon ein Auftrag
            public LowAmmoCause cause;
            public bool IsLow => carried < threshold;
            public int Shortfall => Mathf.Max(0, threshold - carried);
        }

        // false, wenn die Frage fuer diesen Pawn gar nicht gilt (keine Waffe, keine CE-Munitionswaffe).
        public static bool LowAmmoState(Pawn pawn, out LowAmmoInfo info)
        {
            info = default(LowAmmoInfo);
            ThingWithComps primary = pawn?.equipment?.Primary;
            CompAmmoUser comp = primary?.TryGetComp<CompAmmoUser>();
            if (comp == null || comp.Props == null || comp.Props.magazineSize <= 0
                || comp.Props.ammoSet == null || comp.Props.ammoSet.ammoTypes == null) return false;

            info.weapon = primary;
            info.usableAmmo = comp.Props.ammoSet.ammoTypes.Select(t => (ThingDef)t.ammo).Where(a => a != null).Distinct().ToList();

            // Schwelle aus den Einstellungen statt hartkodierter 2 (2026-08-09). Ungueltige Eingabe faellt
            // auf 2 zurueck - ein vertipptes Feld darf die Warnung nicht abschalten.
            int magsNeeded = 2;
            if (CEReloadedMod.settings != null && int.TryParse(CEReloadedMod.settings.alertLowAmmoMags, out int mn) && mn > 0) magsNeeded = mn;
            info.threshold = magsNeeded * comp.Props.magazineSize;

            var setAmmo = new HashSet<ThingDef>(info.usableAmmo);
            if (pawn.inventory != null && pawn.inventory.innerContainer != null)
                foreach (Thing t in pawn.inventory.innerContainer)
                    if (setAmmo.Contains(t.def)) info.carried += t.stackCount;
            // Das geladene Magazin zaehlt mit, wie bei CE selbst (2026-08-30, siehe LoadoutUtility.MagazineAmmoCount).
            if (comp.CurrentAmmo != null && setAmmo.Contains(comp.CurrentAmmo)) info.carried += comp.CurMagCount;

            if (!info.IsLow) return true;

            // (B) Was steht ueberhaupt in seinem Loadout? Die LEBENDE Zuweisung, nicht DisplayLoadout.
            // Der Unterschied entscheidet den Fall: DisplayLoadout laeuft vom persoenlichen Anker AUFWAERTS
            // zur Vorlage und liefert damit genau das, was der Kolonist NICHT selbst gesetzt hat. Wer seine
            // Munitionsmenge gerade selbst erhoeht hat, waere so weiter als "Loadout fordert zu wenig"
            // eingestuft worden - der Mod haette ihn aufgefordert, etwas zu aendern, das er schon geaendert
            // hat. pawn.GetLoadout().Slots ist die Vereinigung aus Anker, Vorlage und Extra-Eltern, also
            // wirklich "alles, was sein Plan ihm zugesteht"; eine ROLLE ist darin genauso enthalten, weil
            // sie die Vorlage IST. Einmal lesen, nicht zweimal - der Getter baut die Vereinigung neu.
            List<LoadoutSlot> planSlots = pawn.GetLoadout()?.Slots;
            info.plannedAmmo = new List<ThingDef>();
            bool plansGeneric = false;
            if (planSlots != null)
                foreach (LoadoutSlot s in planSlots)
                {
                    if (s.count <= 0) continue;
                    if (s.thingDef != null && setAmmo.Contains(s.thingDef))
                    {
                        info.planned += s.count;
                        if (!info.plannedAmmo.Contains(s.thingDef)) info.plannedAmmo.Add(s.thingDef);
                    }
                    else if (s.genericDef != null && LoadoutUtility.GetWeaponForGenericAmmo(s.genericDef) == primary.def) { info.planned += s.count; plansGeneric = true; }
                }
            // 'Any ammo for X' verlangt jede Sorte - dann zaehlt alles Nutzbare, wie bisher.
            if (plansGeneric) info.plannedAmmo.Clear();
            if (info.planned < info.threshold) { info.cause = LowAmmoCause.PlanTooSmall; return true; }

            // (A gegen C) Reicht das, was greifbar ist oder gerade entsteht, um die Luecke zu schliessen?
            // Bestand hier bewusst OHNE den getragenen Topf (CarriedStockCount) - was andere Kolonisten
            // schon am Mann haben, kann dieser hier nicht mehr abholen.
            // Gesperrter Bestand ("nicht aufsammeln") zaehlt NICHT als greifbar - er liegt zwar da, aber
            // niemand holt ihn. Ohne diesen Abzug meldet der Klick "Nachschub ist unterwegs" fuer einen
            // Haufen Beutemunition, den der Kolonist nie anfassen wird. ForbiddenStockCount ueberschneidet
            // sich mit beiden Toepfen darueber (es zaehlt jedes markierte Stueck ausserhalb eines Pawns,
            // im Lager wie auf dem Boden), deshalb genau einmal abziehen und bei 0 abschneiden.
            foreach (ThingDef a in info.AmmoToCheck)
            {
                info.reachable += Mathf.Max(0, LoadoutUtility.IngredientStockCount(a) + LoadoutUtility.GroundStockCount(a)
                                               - LoadoutUtility.ForbiddenStockCount(a));
                if (!info.anyBill && LoadoutUtility.WeaponHasActiveBill(a)) info.anyBill = true;
            }
            info.cause = (info.reachable >= info.Shortfall || info.anyBill) ? LowAmmoCause.Pending : LowAmmoCause.NotProduced;
            return true;
        }

        // Small helper for the alert explanations: "Alice, Bob and 3 others".
        public static string NameList(List<Pawn> pawns, int max = 8)
        {
            if (pawns == null || pawns.Count == 0) return "";
            List<string> names = pawns.Take(max).Select(p => p.LabelShort).ToList();
            string joined = string.Join(", ", names.ToArray());
            if (pawns.Count > max) joined += "CER_CombatReadinessAlerts_AndOthers".Translate((pawns.Count - max).ToString()).ToString();
            return joined;
        }
    }

    // ---- Alert (auto-discovered by RimWorld; Medium priority = quiet, no bell/bounce) ----------------------------
    // ONE consolidated alert instead of five separate ones (WORKFLOW_OVERHAUL_PLAN.md section 4): five near-
    // identical entries in the vanilla alert bar read as noise, not signal - "which of these four almost-the-same
    // alerts is the one that matters right now?". This keeps the per-category on/off settings (a category with its
    // setting off simply contributes nothing) but surfaces ONE line, "Combat readiness: N issues", with the
    // category breakdown in the explanation. Standard vanilla click behaviour (selects/jumps to the culprit pawns)
    // is kept as-is; the NEW one-click FIX action lives in the header-strip readiness widget
    // (Dialog_CEReloadedTabs.cs), which is always visible regardless of which tab is open and opens
    // Dialog_SupplyLoadouts directly for the production-related categories.
    public class Alert_CER_Readiness : Alert
    {
        public Alert_CER_Readiness() { defaultPriority = AlertPriority.Medium; }

        // Store the translation KEY, not the translated value - a static readonly array is only initialized once
        // per process, so pre-translating here would freeze these labels in whatever language was active at first
        // use even after the player switches languages from the Options menu mid-session. Translate at the point
        // of use instead (GetExplanation() below).
        private static readonly (ReadinessIssue issue, string labelKey, System.Func<CEReloadedSettings, bool> enabled)[] Categories =
        {
            (ReadinessIssue.NoLoadout,         "CER_CombatReadinessAlerts_NoLoadout", s => s.alertNoLoadout),
            (ReadinessIssue.WeaponWithoutAmmo, "CER_CombatReadinessAlerts_WeaponWithoutAmmo", s => s.alertWeaponWithoutAmmo),
            (ReadinessIssue.AmmoNotProduced,   "CER_CombatReadinessAlerts_AmmoNotProduced", s => s.alertAmmoNotProduced),
            (ReadinessIssue.WeaponUnavailable, "CER_CombatReadinessAlerts_WeaponUnavailable", s => s.alertWeaponUnavailable),
            (ReadinessIssue.LowAmmo,           "CER_CombatReadinessAlerts_LowAmmo", s => s.alertLowAmmo),
            (ReadinessIssue.Overloaded,        "CER_CombatReadinessAlerts_Overloaded", s => s.alertOverloaded),
            (ReadinessIssue.CapacityBlocked,   "CER_CombatReadinessAlerts_CapacityBlocked", s => s.alertCapacityBlocked),
            (ReadinessIssue.AssignedNotCarried, "CER_CombatReadinessAlerts_AssignedNotCarried", s => s.alertAssignedNotCarried),
        };

        private List<Pawn> AllCulprits()
        {
            CEReloadedSettings s = CEReloadedMod.settings;
            HashSet<Pawn> all = new HashSet<Pawn>();
            foreach (var cat in Categories)
                if (s == null || cat.enabled(s)) all.UnionWith(CombatReadiness.Culprits(cat.issue));
            return all.ToList();
        }

        public override AlertReport GetReport()
        {
            if (CEReloadedMod.settings == null || !CEReloadedMod.settings.showReadinessAlerts) return AlertReport.Inactive;
            return AlertReport.CulpritsAre(AllCulprits());
        }

        public override string GetLabel()
        {
            int n = AllCulprits().Count;
            if (n <= 0) return "CER_CombatReadinessAlerts_Label".Translate();
            return n == 1
                ? "CER_CombatReadinessAlerts_LabelWithIssueSingular".Translate(n.ToString())
                : "CER_CombatReadinessAlerts_LabelWithIssuePlural".Translate(n.ToString());
        }

        public override TaggedString GetExplanation()
        {
            CEReloadedSettings s = CEReloadedMod.settings;
            var sb = new System.Text.StringBuilder();
            foreach (var cat in Categories)
            {
                if (s != null && !cat.enabled(s)) continue;
                List<Pawn> p = CombatReadiness.Culprits(cat.issue);
                if (p.Count == 0) continue;
                sb.Append("\n").Append("CER_CombatReadinessAlerts_CategoryLine".Translate(cat.labelKey.Translate(), p.Count.ToString(), CombatReadiness.NameList(p)).ToString());
            }
            return "CER_CombatReadinessAlerts_ExplanationHeader".Translate().ToString() + "\n" + sb.ToString().TrimStart();
        }
    }
}
