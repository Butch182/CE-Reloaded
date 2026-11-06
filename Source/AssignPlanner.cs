using System;
using System.Collections.Generic;
using System.Linq;
using RimWorld;
using UnityEngine;
using Verse;
using CombatExtended;

namespace CEReloaded
{
    // One loadout the solver can hand out, with everything scoring needs precomputed once per popup open.
    public class AssignCandidate
    {
        public Loadout loadout;
        public ThingDef mainWeapon;
        public float score;          // general weapon quality (0..~111), ammo-independent - the anchor
        public float fleshScore;     // computed damage vs flesh (0..100)
        public float mechScore;      // computed damage vs mechanoids (0..100)
        public bool isRanged;
        public WeaponGroup group;
        public float mechAffinity;   // 0 = anti-flesh, 0.5 = neutral, 1 = anti-mech
        public bool producible;      // recipe researched AND a suitable workbench is built
        public bool isRole;          // a "[Role]" loadout
        public bool hasMelee;        // the loadout carries a melee weapon somewhere (main or sidearm)
        public bool hasRanged;       // the loadout carries a ranged weapon somewhere (main or sidearm)
        // Virtual candidate: a weapon the colony owns or can produce that has NO loadout yet (loadout == null).
        // Picking it creates a default loadout on Apply - this is what lets "Craft new weapons" reach beyond the
        // existing loadout list instead of settling for whatever scraps happen to have one.
        public bool isVirtual;
        // Vom Spieler GESPERRT (2026-09-04): das Loadout bzw. die Rolle steht weiter in der Liste - das
        // Auswahlfenster muss sie ausgegraut zeigen koennen, sonst gaebe es keinen Weg zurueck zum
        // Entsperren -, aber der Loeser laesst sie aus. Gesperrte WAFFEN kommen gar nicht erst als
        // Kandidat an (Dialog_SquadSetup.BuildAssignStaticData filtert sie), weil ihre Zeile im
        // Auswahlfenster aus einer anderen Quelle kommt.
        public bool isDisabled;
    }

    public class AssignPlanEntry
    {
        public Pawn pawn;
        public Loadout current;      // null when the pawn has no real loadout
        public Loadout planned;      // solver result; null = left without a loadout (or a virtual pick, see below)
        public ThingDef plannedMain; // the planned loadout's main weapon (for supply checks)
        public List<Loadout> plannedPacks = new List<Loadout>();  // Equipment Packs carried alongside the weapon - a fitting subset, unless a Role already bakes its own in
        public List<Loadout> plannedSidearms = new List<Loadout>();   // extra Weapon Loadout(s) attached via multi-parent (SidearmAssignmentUtility)
        // Zweitwaffe/Granate als blosse WAFFE geplant, weil beim Planen keine Vorlage existierte (Lazy-
        // Umbau 2026-09-01). Wird NIE in der Vorschau materialisiert - erst ApplyPreviewPlan macht daraus
        // ueber EnsureDefaultLoadout die geteilte Vorlage und haengt sie in plannedSidearms um.
        public List<ThingDef> plannedSidearmDefs;
        public bool plannedVirtual;  // picked a weapon with no loadout yet: a default loadout is created on Apply
        // Hat der SPIELER diesen Pack-/Zweitwaffen-Satz ausdruecklich festgelegt (Zelle in der Vorschau,
        // Einfuegen, Kopieren)? Der Unterschied ist tragend und nicht kosmetisch (2026-08-19):
        // eine LEERE Liste heisst beim Loeser "nichts ausgesucht", beim Spieler dagegen "nimm ihm alles weg".
        // Ohne diese Unterscheidung kann WantsPackWrite nur eines von beidem richtig machen.
        public bool packPinned;
        public bool sidearmPinned;
        // Dritter Fall derselben Frage (2026-08-27, im Test gemessen): der Loeser HAT fuer diese
        // Zeile ausgesucht, "Respect max bulk" hat aber ALLES gestrichen. Die Liste ist dann leer wie beim
        // "nichts ausgesucht" - und ohne diese Marke behaelt der Kolonist seine heutigen, schwereren Packs,
        // waehrend Schranke und Vorschau glauben, er trage nichts. Genau so gemessen: Ltz stand MIT Haken
        // bei Sperrigkeit 48.4/33.5 und damit HOEHER als ohne Haken (41.2/33.5), weil ohne Haken die
        // leichteren Loeser-Packs geschrieben worden waeren und mit Haken die alten sieben haengen blieben.
        // Der Kommentar an EffectivePacks hat diesen Fall seit dem 25.08. als "bewusst offen" gefuehrt; er
        // ist es nicht mehr - er macht das Versprechen des Hakens ("nicht mehr zuweisen als tragbar") falsch.
        // Apply would really write this row's pack/sidearm set (planned set differs from what the pawn carries
        // right now, and there IS a template to hang it on). Kept as a stored flag rather than a live compare,
        // because AssignPlan.Changes is read from OnGUI and the two getters behind it walk the pawn's
        // multi-parent chain and allocate - see AssignPlanner.RefreshExtrasChanged for who refreshes it.
        public bool extrasChanged;
        public bool kept;            // locked or keep-assigned: untouched by the solver
        public bool pinned;          // explicit player override (Dialog_SquadSetup) - a stronger "kept" with a chosen loadout
        public bool needsCraft;      // planned main weapon is not in stock (would have to be produced)
        // Was dieser Kolonist NACH dem Uebernehmen traegt, und seine Grenzen (2026-08-30:
        // Mass/Bulk als eigene Spalten in der Vorschau). Gefuellt in derselben Schleife, die auch die
        // Ueberladungs-Warnung ausloest - eine Frage, eine Rechnung: was der Balken zeigt, ist genau das, was die
        // Warnung meint. Ohne Plan (behalten/uebersprungen) steht dort, was er HEUTE traegt.
        public float previewMass, previewBulk, capMass, capBulk;
        // Hat ReweighEntry diese Zeile WIRKLICH gewogen (dann darf die Ueberladungs-Box sie nennen), oder
        // blieb es bei der Vorgabe "heutiger Stand"? Gespeichert, damit RefreshOverloadProblem die Box
        // jederzeit aus den Eintragsfeldern neu bauen kann - auch beim In-Place-Pin (2026-09-01).
        public bool previewWeighed;
        public bool cannotFight;     // permanently incapable of violence: skipped, never assigned

        // "Apply has something to write for this row" - the ONE definition, shared by the Apply button's own
        // count (AssignPlan.Changes) and the assign list's "show changes only" filter, which used to carry a
        // hand-copied duplicate of it and therefore hid exactly the same rows the button miscounted.
        public bool IsChange => !kept && (planned != current || plannedVirtual || extrasChanged);
    }

    // What the UI should offer to resolve a problem. The solver stays UI-free and only tags intent; the popup maps
    // the tag to an actual button + navigation.
    // Seit dem Lazy-Rueckbau (2026-09-17) gibt es nur noch EIN Sprungziel: den Rollen-Reiter, wenn "nur Rollen"
    // an ist und keine Rolle existiert. "CreateLoadouts" sprang in die Waffenliste, die es nicht mehr gibt.
    public enum ProblemAction { None, OpenRolesTab }

    // One entry in the reactive problem list: the message and an optional suggested action.
    public class AssignProblem
    {
        public string text;
        public ProblemAction action;
        // Kennzeichnet DIE Ueberladungs-Zeile, damit RefreshOverloadProblem sie beim In-Place-Pin an Ort
        // und Stelle ersetzen kann, statt die ganze Box neu bauen zu muessen (2026-09-01).
        public bool isOverloadLine;
        public AssignProblem(string text, ProblemAction action = ProblemAction.None)
        {
            this.text = text; this.action = action;
        }
    }

    // The pure result of one solver run: per-pawn outcome plus the interactive problem list. Nothing is applied.
    public class AssignPlan
    {
        public List<AssignPlanEntry> entries = new List<AssignPlanEntry>();
        public List<AssignProblem> problems = new List<AssignProblem>();
        // Counts the rows Apply will actually write. `extrasChanged` is the pack/sidearm half (2026-08-12 fix,
        // pinning ONLY a sidearm on an otherwise-kept colonist left the button at "(nothing to
        // apply)" although the cell's own tooltip promises "nothing applies until you press Apply") - such a row
        // legitimately has planned == current, since the pin never touches the main weapon, so the weapon-only
        // comparison below counted zero and the whole change read as non-existent.
        public int Changes => entries.Count(e => e.IsChange);
        public int CraftCount => entries.Count(e => !e.kept && e.needsCraft);
    }

    // Side-effect-free auto-assign solver. All four sliders enter one value function, so dragging a slider re-ranks
    // the same candidate pool live:
    //   base  = lerp(generalScore, lerp(fleshScore, mechScore, mech), tilt)   where tilt = |mech-0.5|*2 * MechTilt
    //           -> at the neutral middle it is the ammo-independent general weapon score (a top gun like the charge
    //              rifle wins); toward either end the computed flesh/mech damage table tilts the ranking so a weapon
    //              is only overtaken when it genuinely does more to the chosen target type.
    //   value = base
    //         - (1-craft) * 80 * (1-afford)   when the weapon must be made   (use existing <-> craft new)
    //         - (1-budget) * 50 * scarcity^2  when it must be made   (cheap <-> best weapon, KNAPPHEIT der Zutaten,
    //           see LoadoutUtility.MaterialAffordability - WORKFLOW_OVERHAUL_PLAN.md 6b/6d)
    //         - (1-budget) * 45 * cost/maxCost when it must be made  (cheap <-> best weapon, PREIS des Stuecks -
    //           2026-08-14 ergaenzt, siehe CraftPenalty: ohne diesen Anteil war der Regler in einer Kolonie mit
    //           vollen Lagern wirkungslos, weil dort jede Waffe "bezahlbar" ist)
    //         - diversity * (55 * reuse + 8 * groupUse + 35 * overCap)   (always best <-> cover roles)
    //         + diversity * 25 for the first pick of an unfilled role group (SupportMG/Marksman/CQC/Launcher)
    // The ranged/melee slider shifts the shooter-vs-brawler threshold per pawn instead of scoring options.
    // Colonists permanently incapable of violence are skipped entirely (never assigned a combat loadout).
    public static class AssignPlanner
    {
        // How far the anti-flesh/anti-mech slider may pull the ranking away from the trusted general score at its
        // extremes (0..1). At 0.6 a fully specialized target-damage pick still keeps 40% of the general score, so a
        // dominant weapon stays competitive and is only dethroned by one that is clearly better against that target.
        private const float MechTilt = 0.6f;

        // One pawn's settled/pinned bookkeeping, carried between the two reservation passes in Compute (see there
        // for why the reservation needs two passes).
        private class PrepEntry
        {
            public Pawn pawn;
            public AssignPlanEntry entry;
            public bool isPinned, kept, settled, carriesMain, packPinned, sidearmPinned;
            public ThingDef mainW;
        }

        public static AssignPlan Compute(
            List<Pawn> pawns, Func<Pawn, float> rangedScore, Func<Pawn, float> meleeScore,
            List<AssignCandidate> allCandidates, Dictionary<ThingDef, int> owned,
            float craft, float mech, float melee, float diversity, float budget,
            RoleAssignMode roleMode, bool keepAssigned, bool redistribute,
            HashSet<Pawn> locked, Dictionary<Pawn, Loadout> pinned = null,
            Dictionary<Pawn, List<Loadout>> pinnedPacks = null, float sidearmCoverage = 0f,
            Dictionary<Pawn, List<Loadout>> pinnedSidearms = null, float packCoverage = 0f,
            float grenadeCoverage = 0f, float shieldCoverage = 0f)
        {
            AssignPlan plan = new AssignPlan();
            // Virtuelle Kandidaten (Waffen ohne Vorlage) treten seit dem Lazy-Umbau (2026-09-01) IMMER an -
            // die Vorlage ist ein unsichtbares Hintergrund-Objekt, das erst beim Uebernehmen entsteht. Der
            // fruehere Opt-in-Haken "Create new loadouts" entfiel mit der Pflicht-Massenerzeugung.
            // `!c.isDisabled` (2026-09-04): eine gesperrte Rolle/Vorlage bleibt in der Kandidatenliste,
            // damit das Auswahlfenster sie ausgegraut anzeigen und wieder entsperren kann - aber der Loeser
            // vergibt sie nie. Genau dieselbe Zusage, die der Schalter fuer Waffen gibt.
            List<AssignCandidate> cands = allCandidates.Where(c => !c.isDisabled && RoleModeAllows(c, roleMode)).ToList();

            // Split pawns into settled (locked / keep-assigned / pinned) and targets; settled pawns reserve their
            // weapon so the solver never double-allocates it to someone else.
            Dictionary<ThingDef, int> stock = new Dictionary<ThingDef, int>(owned);
            List<AssignPlanEntry> targets = new List<AssignPlanEntry>();
            void Reserve(ThingDef d, int n)
            {
                if (d != null && stock.TryGetValue(d, out int c)) stock[d] = Mathf.Max(0, c - n);
            }

            // Build one PrepEntry per plannable pawn first (cannot-fight pawns are resolved immediately, same as
            // before). The actual stock reservation happens in TWO passes below - see the comment there for why.
            List<PrepEntry> prep = new List<PrepEntry>();
            foreach (Pawn p in pawns)
            {
                // Unwrapped past any hidden per-pawn "extra ammo" leaf (AmmoAssignmentUtility) - planning
                // compares against the real weapon template, not that bookkeeping object.
                Loadout cur = AmmoAssignmentUtility.DisplayLoadout(p.GetLoadout());
                if (cur != null && cur.defaultLoadout) cur = null;

                // Permanently incapable of violence (pacifist trait/backstory) or a baby: can never wield a weapon,
                // so leave whatever they have untouched and skip them. Temporary states (downed, anesthetised) are
                // NOT this, so an unconscious colonist is still planned normally.
                // Vom Spieler ausgeschlossen heisst ab 2026-08-13: das Loadout wird beim Uebernehmen WEGGENOMMEN
                // - ein Kolonist laesst sich nicht loeschen, die Zuweisung seiner Ausruestung aber sehr wohl;
                // er steht danach ohne da. Vorher landete er im Zweig darunter mit planned=cur/kept=true, also
                // "unangetastet lassen" - dann hat der Muelleimer im Zeilensymbol nichts weggenommen und war
                // eine leere Geste.
                // kept=false + planned=null ist genau die Kombination, die ApplyPreviewPlans apply() schon
                // immer als "Zuweisung entfernen" schreibt (SetLoadout(none)); cannotFight bleibt true, damit
                // die Zeile weiter als Entscheidung des Spielers gezeichnet wird und der Solver ihn nicht antastet.
                // plannedPacks bleiben seine aktuellen: die Packs sind reine Versorgung und haengen nicht am
                // Waffen-Loadout - weggenommen wird die WAFFE, nicht sein Verbandszeug.
                // Reihenfolge zaehlt: die TATSACHE zuerst. Ein Pazifist, der zusaetzlich ausgeschlossen ist,
                // faellt in den unteren Zweig und bleibt unangetastet - sein Zeilensymbol ist ein totes
                // Schloss ohne Rueckweg, ihm hier still das Loadout wegzunehmen waere eine Falle.
                if (!LoadoutUtility.CannotFightByNature(p) && LoadoutUtility.IsExcludedFromArming(p))
                {
                    plan.entries.Add(new AssignPlanEntry { pawn = p, current = cur, planned = null, kept = false, cannotFight = true,
                        plannedPacks = AmmoAssignmentUtility.GetPawnPacks(p),
                        plannedSidearms = new List<Loadout>() });
                    continue;
                }
                if (LoadoutUtility.CannotFight(p))
                {
                    // A PACK pin reaches them too (2026-08-12): the preview's Pack cell is gated on
                    // CannotCarryAnything, not on CannotFight, so it is deliberately offered to a pacifist -
                    // but this branch returns before the pin lookup further down ever runs, so a pinned set
                    // used to be silently replaced by whatever they already carried on the next re-solve.
                    // (`kept` is settled for every entry by the sweep at the end of this method.)
                    List<Loadout> cfPinnedPacks = null;
                    bool cfPackPinned = pinnedPacks != null && pinnedPacks.TryGetValue(p, out cfPinnedPacks);
                    plan.entries.Add(new AssignPlanEntry { pawn = p, current = cur, planned = cur, kept = true, cannotFight = true,
                        plannedPacks = cfPackPinned ? cfPinnedPacks : AmmoAssignmentUtility.GetPawnPacks(p),
                    packPinned = cfPackPinned,
                        plannedSidearms = SidearmAssignmentUtility.GetPawnSidearms(p) });
                    continue;
                }

                // Pinned (WORKFLOW_OVERHAUL_PLAN.md 5.5) is a player override, not a "nothing to do" state: it must
                // stay OUT of `kept` (Changes/apply/produce-list all key off `kept` meaning "skip, planned==current
                // already handled") so the pin actually applies and shows up as a change. It IS excluded from the
                // solver's target list, exactly like a kept pawn, so it never gets reshuffled by a slider tweak.
                Loadout pinnedLoadout = null;
                bool isPinned = pinned != null && pinned.TryGetValue(p, out pinnedLoadout);
                // `locked` means "this pawn's kept-state is FLIPPED relative to the current global default" -
                // under keepAssigned it's an explicit per-pawn RELEASE (so one colonist can still be replanned
                // even though everyone else stays kept), under !keepAssigned it's the original explicit LOCK.
                // Dialog_SquadSetup clears the set whenever the global checkbox itself is toggled, so a lock
                // set while replanning never silently becomes a release once Keep-current is switched on (or
                // vice versa) - membership always means "differs from today's default", never a fixed sense.
                // "kept" fundamentally means "keep the CURRENT assignment as-is" - a pawn with no current
                // loadout has nothing to keep, so kept must never be true for them regardless of locked/
                // keepAssigned (2026-08-01: clicking the Assign step's own "lock
                // everyone" master toggle, with keepAssigned off and every pawn's cur==null - e.g. right
                // after a Back-navigation that wiped every assignment - added all of them to `locked`, which
                // under the !keepAssigned branch used to mean kept=true regardless of cur, freezing every
                // pawn at "nothing assigned, nothing planned" with ZERO way for the solver to ever plan them
                // again short of clicking the SAME toggle a second time to release them - Apply showed 0
                // changes and stayed that way until the player realized the toggle was itself the undo).
                // The keepAssigned==true branch already had this `cur != null` guard; it was just missing
                // from the other one.
                // Gesperrte Waffe an der Zuweisung (2026-09-03): "behalten" gibt es
                // fuer so eine Zeile nicht - genau sie soll die Vorschau unter keepAssigned neu planen,
                // alle anderen bleiben stehen. Ueber die ROHE Slots-Vereinigung des Ankers, damit auch
                // eine gesperrte Zweitwaffe die Zeile freigibt.
                bool carriesDisabled = false;
                Loadout rawForDisabled = p.GetLoadout();
                if (rawForDisabled != null && !rawForDisabled.defaultLoadout)
                {
                    // Seit 2026-09-04 gilt dasselbe fuer eine gesperrte ROLLE bzw. VORLAGE: die Zeile wird
                    // freigegeben, egal ob die Sperre an einer Waffe darin oder am Loadout selbst haengt.
                    // Ueber DisplayLoadout, weil ein personalisierter Kolonist an seinem Anker haengt und
                    // nicht direkt an der Vorlage - die rohe Zuweisung waere hier immer der Anker.
                    if (LoadoutUtility.IsLoadoutDisabled(rawForDisabled)
                        || LoadoutUtility.IsLoadoutDisabled(AmmoAssignmentUtility.DisplayLoadout(rawForDisabled)))
                        carriesDisabled = true;
                    if (!carriesDisabled)
                        foreach (int epid in MultiParentUtility.GetExtraParentIds(rawForDisabled))
                        {
                            Loadout extra = LoadoutManager.Loadouts?.FirstOrDefault(l => l != null && l.UniqueID == epid);
                            if (LoadoutUtility.IsLoadoutDisabled(extra)) { carriesDisabled = true; break; }
                        }
                    if (!carriesDisabled && rawForDisabled.Slots != null)
                        foreach (LoadoutSlot ds in rawForDisabled.Slots)
                            if (ds.thingDef != null && ds.thingDef.IsWeapon && LoadoutUtility.IsWeaponDisabled(ds.thingDef)) { carriesDisabled = true; break; }
                }
                bool mainKept = !isPinned && cur != null && !carriesDisabled && (keepAssigned
                    ? !locked.Contains(p)
                    : locked.Contains(p));
                bool settled = mainKept || isPinned;
                ThingDef pinMain = null;
                if (isPinned) Dialog_AssignLoadouts.AnalyzeLoadout(pinnedLoadout, out pinMain, out _, out _, out _);
                ThingDef mainW = mainKept ? null : pinMain;
                if (mainKept && cur != null) Dialog_AssignLoadouts.AnalyzeLoadout(cur, out mainW, out _, out _, out _);

                // A manual pack/sidearm pin (Dialog_SquadSetup's own per-row pickers) is independent of the
                // weapon's kept/pinned state - a player can override just the pack or sidearm without also
                // pinning the weapon (settled/mainW/plannedMain above all stay keyed on `mainKept`, unrelated
                // to these). `entryKept` (2026-08-02 fix: pinning just a sidearm/pack on an otherwise-kept
                // pawn never actually applied) is the
                // NARROWER flag that actually lands on AssignPlanEntry.kept: it must also exclude a pack/
                // sidearm pin, exactly like it already excludes a weapon pin, since Dialog_AssignLoadouts.
                // ApplyPreviewPlan's own apply loop does a blanket `if (e.kept) continue;` that skipped the
                // ENTIRE entry - pack/sidearm pin included - whenever the main weapon itself stayed kept.
                List<Loadout> pinnedPackList = null;
                bool packPinned = pinnedPacks != null && pinnedPacks.TryGetValue(p, out pinnedPackList);
                List<Loadout> pinnedSidearmList = null;
                bool sidearmPinned = pinnedSidearms != null && pinnedSidearms.TryGetValue(p, out pinnedSidearmList);
                bool entryKept = mainKept && !packPinned && !sidearmPinned;

                AssignPlanEntry e = new AssignPlanEntry
                {
                    pawn = p,
                    current = cur,
                    kept = entryKept,
                    pinned = isPinned,
                    planned = isPinned ? pinnedLoadout : (mainKept ? cur : null),
                    plannedMain = isPinned ? pinMain : null,
                    // Settled (kept/pinned) pawns keep whatever pack(s) they already carry - the solver only
                    // freely (re-)picks packs for pawns whose weapon it is also freely (re-)picking, unless
                    // the player pinned the pack set directly.
                    plannedPacks = packPinned ? pinnedPackList : (settled ? AmmoAssignmentUtility.GetPawnPacks(p) : new List<Loadout>()),
                    // Same idea as packs: a settled pawn keeps whatever sidearm(s) they already carry; the
                    // solver only freely (re-)picks one for pawns whose main weapon it is also freely picking.
                    plannedSidearms = sidearmPinned ? pinnedSidearmList : (settled ? SidearmAssignmentUtility.GetPawnSidearms(p) : new List<Loadout>()),
                    packPinned = packPinned,
                    sidearmPinned = sidearmPinned
                };
                plan.entries.Add(e);
                prep.Add(new PrepEntry { pawn = p, entry = e, isPinned = isPinned, kept = mainKept, settled = settled, mainW = mainW, packPinned = packPinned, sidearmPinned = sidearmPinned });
            }

            // Pass 1: reserve every pawn's own carried gear that is NEVER going to be dropped for this plan - kept
            // pawns always (Apply skips them outright), and targets too when "Redistribute carried weapons" is off
            // (then nothing gets dropped for anyone). This must run for EVERY such pawn before pass 2 evaluates any
            // pinned claim, regardless of combat-score order: a single interleaved pass let a high-score pinned pawn
            // (processed first, since `pawns` is score-sorted) claim a weapon def that a low-score pawn was already
            // holding onto for good but had not been reserved yet - showing a false "in stock" for a copy that, in
            // reality, can never reach the pinned pawn because its actual holder never lets it go.
            foreach (PrepEntry x in prep)
            {
                if (x.settled || !redistribute)
                {
                    if (x.pawn.equipment != null && x.pawn.equipment.Primary != null)
                    {
                        Reserve(x.pawn.equipment.Primary.def, 1);
                        if (x.pawn.equipment.Primary.def == x.mainW) x.carriesMain = true;
                    }
                    if (x.pawn.inventory != null && x.pawn.inventory.innerContainer != null)
                        foreach (Thing t in x.pawn.inventory.innerContainer)
                            if (t.def != null && t.def.IsWeapon)
                            {
                                Reserve(t.def, t.stackCount);
                                if (t.def == x.mainW) x.carriesMain = true;
                            }
                }
                // A KEPT pawn's own main weapon is claimed here too, unconditionally (no stock check, matching the
                // original code) - this must land before ANY pinned pawn's stock check in pass 2, and being
                // unconditional it never depends on other pawns' order, so it is safe to do in pass 1.
                if (x.kept && x.mainW != null && !x.carriesMain) Reserve(x.mainW, 1);
            }

            // Pass 2: pinned claims against the now-final stock, still in score-priority order - competition among
            // pinned pawns for the same def is intentional (first-come-first-served by priority), unlike pass 1's
            // unconditional holds. A settled pawn who doesn't already hold their (current or pinned) main weapon
            // needs one from stock; check BEFORE reserving so needsCraft reflects availability at claim time.
            foreach (PrepEntry x in prep)
            {
                if (x.isPinned && x.mainW != null && !x.carriesMain)
                {
                    bool hasStock = stock.TryGetValue(x.mainW, out int s) && s > 0;
                    x.entry.needsCraft = !hasStock;
                    Reserve(x.mainW, 1);
                }
                if (!x.settled) targets.Add(x.entry);
            }
            HashSet<Pawn> packPinnedPawns = new HashSet<Pawn>(prep.Where(x => x.packPinned).Select(x => x.pawn));
            // Straight from the dictionary as well, not only from `prep`: cannotFight pawns never enter prep
            // (they are resolved in their own branch above), so a pacifist with a pinned pack was missing from
            // this set and the pack-only pass below promptly overwrote the pin with its own auto-pick.
            if (pinnedPacks != null) foreach (Pawn pp in pinnedPacks.Keys) packPinnedPawns.Add(pp);
            HashSet<Pawn> sidearmPinnedPawns = new HashSet<Pawn>(prep.Where(x => x.sidearmPinned).Select(x => x.pawn));

            // Die "superior loadouts skipped"-Liste (skippedSuperior) und ihr Schattenlauf pro Kolonist sind am
            // 2026-08-25 ersatzlos entfallen, weil sie keinen Sinn ergab. Mit ihr der Parameter
            // ignoreAvailability von PickBest und die Meldung
            // "Unobtainable and skipped" in BuildProblems. Nebeneffekt: bis zu zwei komplette PickBest-Laeufe
            // je Kolonist weniger (stand seit dem 2026-08-15 auf der Performance-Restliste).

            // Shooter vs brawler per pawn: the melee slider shifts the threshold (right = melee wins ties and more).
            float shift = (melee - 0.5f) * 300f;
            Dictionary<Pawn, bool> isShooter = new Dictionary<Pawn, bool>();
            foreach (AssignPlanEntry e in targets)
            {
                float r = rangedScore(e.pawn);
                float m = meleeScore(e.pawn);
                isShooter[e.pawn] = r >= m + shift;
            }
            int shooterCount = isShooter.Values.Count(v => v);
            List<AssignPlanEntry> order = targets
                .OrderByDescending(e => isShooter[e.pawn] ? rangedScore(e.pawn) : meleeScore(e.pawn))
                .ToList();

            Dictionary<WeaponGroup, int> caps = Dialog_AssignLoadouts.BuildGroupCaps(shooterCount);
            Dictionary<WeaponGroup, int> usedInGroup = new Dictionary<WeaponGroup, int>();
            Dictionary<AssignCandidate, int> timesUsed = new Dictionary<AssignCandidate, int>();   // keyed by candidate (virtuals have no loadout)

            // Der Abzug fuer alles, was erst GEBAUT werden muss - eine Formel fuer Hauptwaffe, Zweitwaffe
            // und Wurfsprengstoff (bis zum 2026-08-14 stand sie dreimal wortgleich in den drei Pickern; die
            // Kostenerweiterung darunter waere sonst wieder nur an einer der drei gelandet).
            //
            // Zwei Anteile, zwei verschiedene Fragen:
            //   craft   "Vorhandenes nutzen <-> Neues bauen"  -> haengt an der KNAPPHEIT der Zutaten
            //   budget  "Guenstige Waffen <-> Beste Waffe"    -> haengt an der KNAPPHEIT *und* am PREIS
            //
            // Der Preisanteil ist neu (2026-08-14): wer Economy waehlte, bekam exorbitant hohe Baukosten.
            // Vorher hingen BEIDE Regler allein an MaterialAffordability,
            // also am Verhaeltnis Bestand/Bedarf - in einer Kolonie mit vollen Lagern steht das fuer jede
            // Waffe auf 1.0, beide Abzuege fallen auf 0, und "Guenstige Waffen" ganz links aendert exakt
            // nichts mehr. Genau dieser Zustand war im Test zu sehen.
            // Relativ zum teuersten Kandidaten dieses Laufs, nicht gegen eine absolute Silberschwelle: was
            // "teuer" heisst, haengt am Zeitalter und am Modpack, und der Regler soll in einer Bogen-und-
            // Speer-Kolonie genauso greifen wie zwischen Sturmgewehr und schwerem MG.
            // Drei getrennte Bezugswerte, weil es drei getrennte Wettbewerbe sind: die drei Picker
            // vergleichen Schusswaffen nur mit Schusswaffen, Nahkampf nur mit Nahkampf und Wurfsprengstoff
            // nur mit Wurfsprengstoff. Mit EINEM gemeinsamen Maximum haette das teuerste MG den Massstab
            // gesetzt und jedes Messer, jede Granate waere pauschal bei ~0 gelandet - der Regler haette
            // dort dann wieder nichts unterschieden, also genau der Fehler, den dieser Fix behebt.
            float maxCostRanged = 0f, maxCostMelee = 0f, maxCostGrenade = 0f;
            // Wie weit liegen die Kandidaten eines Wettbewerbs QUALITATIV auseinander? Das ist der Massstab,
            // an dem sich der Preisanteil messen muss (2026-08-16, zweiter Befund zu Economy: es wurde immer
            // noch extrem viel produziert).
            //
            // Ehrlich festgehalten: der Fix vom 14.08. hat den Preisanteil ueberhaupt erst eingebaut -
            // richtig, aber unvollstaendig. Er ist auf 45 Punkte gedeckelt, waehrend die Bewertungen der
            // Kandidaten bis ~111 reichen. Liegen zwei Waffen 60 Punkte auseinander, kann der Preis die
            // Entscheidung gar nicht mehr drehen, egal wie weit links der Regler steht - die Voreinstellung
            // "Economy" plant dann weiter das MG. Beim ersten Mal habe ich geprueft, ob der Regler ANKOMMT
            // (er tut es), und nicht, ob er ENTSCHEIDEN kann.
            //
            // Deshalb haengt das Gewicht jetzt an der Spanne des jeweiligen Wettbewerbs statt an einer festen
            // Zahl: ganz links darf der Preis jede Qualitaetsdifferenz innerhalb des Pools ueberstimmen, ganz
            // rechts zaehlt er gar nicht, dazwischen anteilig. Die alten 45 bleiben als Untergrenze - diese
            // Aenderung kann den Regler nur staerker machen, nie schwaecher.
            // Wichtig: das gilt ausschliesslich fuer Waffen, die HERGESTELLT werden muessen (der Aufrufer
            // ruft CraftPenalty nur bei !hasStock). Vorhandene Waffen bleiben voellig unberuehrt - "guenstig"
            // heisst hier "billig zu BAUEN", nicht "schlechter".
            float spreadRanged = 0f, spreadMelee = 0f, spreadGrenade = 0f;
            float minRanged = float.MaxValue, minMelee = float.MaxValue, minGrenade = float.MaxValue;
            float maxScoreRanged = 0f, maxScoreMelee = 0f, maxScoreGrenade = 0f;
            foreach (AssignCandidate c in cands)
            {
                float cc = LoadoutUtility.WeaponCraftCost(c.mainWeapon);
                if (Dialog_GenerateLoadouts.IsGrenade(c.mainWeapon))
                {
                    if (cc > maxCostGrenade) maxCostGrenade = cc;
                    if (c.score < minGrenade) minGrenade = c.score;
                    if (c.score > maxScoreGrenade) maxScoreGrenade = c.score;
                }
                else if (c.isRanged)
                {
                    if (cc > maxCostRanged) maxCostRanged = cc;
                    if (c.score < minRanged) minRanged = c.score;
                    if (c.score > maxScoreRanged) maxScoreRanged = c.score;
                }
                else
                {
                    if (cc > maxCostMelee) maxCostMelee = cc;
                    if (c.score < minMelee) minMelee = c.score;
                    if (c.score > maxScoreMelee) maxScoreMelee = c.score;
                }
            }
            spreadRanged = minRanged <= maxScoreRanged ? maxScoreRanged - minRanged : 0f;
            spreadMelee = minMelee <= maxScoreMelee ? maxScoreMelee - minMelee : 0f;
            spreadGrenade = minGrenade <= maxScoreGrenade ? maxScoreGrenade - minGrenade : 0f;
            float CraftPenalty(AssignCandidate c)
            {
                float afford = LoadoutUtility.MaterialAffordability(c.mainWeapon);
                // Beide Knappheits-Abzuege wie bisher, unveraendert. Quadratisch beim Budget-Anteil, damit
                // eine leicht teurere, aber klar bessere Waffe bei geringer Knappheit trotzdem gewinnt.
                float scarcity = 1f - afford;
                float penalty = (1f - craft) * 80f * (1f - afford)
                    + (1f - budget) * 50f * scarcity * scarcity;
                // Linear im Preisanteil, nicht quadratisch: hier ist der Punkt gerade, dass auch der
                // MITTLERE Preisunterschied zaehlt (Sturmgewehr gegen MG), nicht nur der extreme.
                bool isGren = Dialog_GenerateLoadouts.IsGrenade(c.mainWeapon);
                float maxCost = isGren ? maxCostGrenade : c.isRanged ? maxCostRanged : maxCostMelee;
                // Gewicht = Spanne dieses Wettbewerbs, mindestens die bisherigen 45 (siehe oben).
                float priceWeight = Mathf.Max(45f, isGren ? spreadGrenade : c.isRanged ? spreadRanged : spreadMelee);
                if (maxCost > 0f)
                    penalty += (1f - budget) * priceWeight * Mathf.Clamp01(LoadoutUtility.WeaponCraftCost(c.mainWeapon) / maxCost);
                return penalty;
            }

            // TRAGKRAFT IST BEI DER ZUTEILUNG KEINE SCHRANKE MEHR (2026-08-30): zuweisen soll man ALLES koennen,
            // Gewicht und Bulk spielen bei der Zuweisung keine Rolle - der Spieler sorgt selbst dafuer, dass es
            // sinnvoll ist (vielleicht zieht er nachher noch einen Rucksack an). Damit ist der Haken
            // "Respect max bulk" vom 25.08. wieder weg und mit ihm alles, was
            // daran hing: die Hauptwaffen-Schranke, das Rest-Budget fuer Packs/Zweitwaffe/Sprengstoff und die zwei
            // Meldungen darueber ("reduced set", "nothing fits"). Der Loeser teilt wieder zu, was am besten passt.
            // Was BLEIBT, ist die WARNUNG unten: wer nach dem Uebernehmen ueber seiner Grenze liegt, steht mit Zahlen
            // im Kasten "Problems with the current settings" und hat rote Balken - siehe die Schleife bei den
            // Ueberladungs-Meldungen. Sie ist der einzige Ort, an dem Tragkraft den Loeser noch interessiert, und
            // dafuer braucht sie genau diese zwei Helfer.
            // CapOf lebt seit 2026-09-01 als statisches CapacityOf neben ReweighEntry (siehe dort);
            // WhyExceeds und der Box-Aufbau selbst am selben Tag als RefreshOverloadProblem daneben.

            AssignCandidate PickBest(bool wantRanged, float pawnRanged, float pawnMelee)
            {
                AssignCandidate best = null;
                float bestVal = float.NegativeInfinity;
                foreach (AssignCandidate c in cands)
                {
                    // Grenades and shields are their own separate pools now (PickGrenade/PickShield below) -
                    // neither is ever a pawn's MAIN weapon, just something that rides alongside it.
                    if (Dialog_GenerateLoadouts.IsGrenade(c.mainWeapon) || LoadoutUtility.IsShieldApparel(c.mainWeapon)) continue;
                    if (c.isRanged != wantRanged) continue;
                    // "Respect max bulk": was allein schon nicht traegt, tritt gar nicht an.
                    bool hasStock = stock.TryGetValue(c.mainWeapon, out int s) && s > 0;
                    if (!hasStock && (craft <= 0.001f || !c.producible)) continue;

                    // Anchor on the general (ammo-independent) score, tilt toward the computed target damage as the
                    // mech slider leaves the neutral middle. Real numbers decide flesh vs mech, not a flat weight.
                    float targetDamage = Mathf.Lerp(c.fleshScore, c.mechScore, mech);
                    float tilt = Mathf.Abs(mech - 0.5f) * 2f * MechTilt;
                    float val = Mathf.Lerp(c.score, targetDamage, tilt);
                    // Nur was gebaut werden muss, kostet etwas - eine Waffe, die schon im Regal liegt, ist
                    // fuer jeden Reglerstand gratis. Das ist zugleich der "erst schauen, was da ist"-Teil
                    // Economy-Vorgabe: Vorhandenes hat hier per Konstruktion keinen Abzug.
                    if (!hasStock) val -= CraftPenalty(c);
                    // A virtual pick creates a brand-new loadout; prefer an equivalent existing loadout when tied.
                    if (c.isVirtual) val -= 4f;
                    int used = timesUsed.TryGetValue(c, out int t) ? t : 0;
                    int inGroup = usedInGroup.TryGetValue(c.group, out int g) ? g : 0;
                    val -= diversity * 55f * used;
                    val -= diversity * 8f * inGroup;
                    if (caps.TryGetValue(c.group, out int cap) && inGroup + 1 > cap)
                        val -= diversity * 35f * (inGroup + 1 - cap);
                    // Cover roles doesn't just avoid duplicates - it actively PULLS toward unfilled role groups: the
                    // first pick of an uncovered SupportMG (full-auto), Marksman, CQC or Launcher slot bridges up to
                    // ~25 score points, so a squad at high diversity fields one of each when candidates exist.
                    if (inGroup == 0 && (c.group == WeaponGroup.SupportMG || c.group == WeaponGroup.Marksman
                        || c.group == WeaponGroup.CQC || c.group == WeaponGroup.Launcher))
                        val += diversity * 25f;

                    // Complementary backup: reward a loadout that also lets this pawn use their OTHER combat skill -
                    // a melee backup (knife) for a melee-capable shooter, a ranged backup for a shooting brawler.
                    // Scaled by that skill and capped, so it only breaks ties between otherwise similar loadouts
                    // (e.g. Henry Big Boy + knife beats Henry Big Boy + grenade for an 86-melee colonist).
                    if (c.isRanged && c.hasMelee) val += Mathf.Min(pawnMelee, 80f) * 0.12f;
                    if (!c.isRanged && c.hasRanged) val += Mathf.Min(pawnRanged, 80f) * 0.12f;

                    if (val > bestVal) { bestVal = val; best = c; }
                }
                return best;
            }

            // Equipment Packs are pure supply now (medicine/food/drugs/utility - weapons of every kind,
            // sidearms and grenades included, are Weapon Loadouts and attach via Roles/multi-parent
            // instead, see MultiParentUtility). A pack whose contents are a genuine mix (or Food-led, like
            // Base Supply's food+medicine split) is still a baseline everyone carries - there's nothing to
            // specialize there. But a pack STRONGLY dominated by one category reads as a specialist role
            // (a Medic Kit), and handing that to literally everyone just piles every specialist's gear onto
            // every colonist regardless of fit. Structural, not name-based (a player's own hand-authored
            // packs get the same treatment as the auto-generated defaults):
            //   - Medical-dominant -> best Medicine skill first, so far down the list as the coverage slider
            //     reaches (2026-08-09; used to be a fixed top-40% cut the slider could not move).
            //   - Contains a shield -> the melee-oriented half (this mod's own weapon-loadout generator
            //     already pairs shields with melee, see DefaultShield's `mobile` param) - unless the whole
            //     squad is ranged, in which case gating it would starve everyone. Checked by CONTENT
            //     (ContainsShield), not "Utility-dominant" - a hand-authored Utility-dominant pack with no
            //     shield in it (say, a player's own webbing-only kit) has no weapon-compatibility reason to
            //     be melee-only.
            // A pack's own supply shortfall is still surfaced downstream by the existing Dialog_SupplyLoadouts
            // flow once .Slots includes it (same as main-weapon ammo today).
            List<Loadout> packPool = EquipmentPackUtility.AllEquipmentPacks();
            List<Pawn> packEligible = order.Where(e => !packPinnedPawns.Contains(e.pawn)).Select(e => e.pawn).ToList();
            // Pacifists/non-combatants can't fight but CAN still carry Equipment Pack supply (e.g. a dedicated
            // doctor) - the manual Assign tab already allows this (CannotCarryAnything, not the broader
            // CannotFight, is the only real block there), but the auto-solver used to gate ALL pack logic on
            // being in `order` (fight-capable only), so a pacifist got whatever pack they already happened to
            // have (line ~194 above) and never a fresh auto-assignment - even a high-Medicine-skill doctor who'd
            // otherwise top the medic ranking right below (2026-08-01 fix). Kept OUT of anyMeleeTarget/
            // shieldEligible below on purpose - a shield is pointless for someone who never fights, and their
            // presence shouldn't skew whether the FIGHTING squad counts as melee-capable.
            // IsExcludedFromArming muss hier RAUS, obwohl es Teil von CannotFight ist (2026-08-09, direkt beim
            // Einbau des Merkers aufgefallen): CannotFight buendelt zwei verschiedene Aussagen. "Pazifist oder
            // Baby" heisst nur "keine WAFFE" - ein Pazifist soll sehr wohl ein Medic Kit bekommen, genau dafuer
            // gibt es diesen Zweig. "Der Spieler hat ihn abgehakt" heisst dagegen "lass ihn komplett in Ruhe";
            // ihm hier automatisch ein Pack zuzuteilen waere das Gegenteil dessen, was der Haken bedeutet.
            List<Pawn> packOnlyPawns = pawns.Where(p => LoadoutUtility.CannotFight(p) && !LoadoutUtility.CannotCarryAnything(p)
                && !LoadoutUtility.IsExcludedFromArming(p) && !packPinnedPawns.Contains(p)).ToList();
            // Die frueher hier stehende feste 40%-Medic-Grenze ist am 2026-08-09 entfallen. Sie war nicht
            // falsch, nur unbeweglich: sie meinte dasselbe wie die jetzige Skill-Reihenfolge weiter unten
            // (rankRecipients), aber mit einer Zahl, die der Abdeckungs-Regler nicht anfassen konnte - genau
            // die Inkonsistenz, ueber die der Punkt lief. Die Auswahl "wer bekommt ein Medic Kit zuerst"
            // passiert jetzt an einer Stelle statt an zweien; medicPackEligible bleibt als Grundmenge.
            List<Pawn> medicPackEligible = packEligible.Concat(packOnlyPawns).ToList();
            bool anyMeleeTarget = packEligible.Any(p => !isShooter[p]);

            // Shields get their OWN gradual, best-melee-fighter-first fill (shieldCoverage, 0..1) instead of
            // sharing packCoverage's rank-threshold tiers below - shield need is a melee-vs-ranged question,
            // a different axis from "how much general supply does the squad get". Same "proportional slot
            // count, filled in priority order" shape sidearmCoverage/grenadeCoverage already use further
            // down - NOT "every eligible pawn gets it the instant a tier unlocks". Eligible = melee-oriented
            // pawns, unless the WHOLE squad is ranged (nobody to prioritize over anyone else, so everybody
            // qualifies) - same rule the old inline gate used. `order` is already sorted best-combat-
            // priority-first (rangedScore for shooters, meleeScore for everyone else), so walking it in
            // order naturally hands shields to the squad's best melee fighters first. Content-based
            // throughout (ContainsShield inspects the pack's actual slots) - never keyed off a pack's name,
            // so a player's own hand-authored shield pack is treated identically to the built-in "Shield Kit".
            List<Pawn> shieldEligible = packEligible.Where(p => !(anyMeleeTarget && isShooter[p])).ToList();
            int shieldSlots = Mathf.RoundToInt(shieldEligible.Count * shieldCoverage);
            HashSet<Pawn> shieldGiven = new HashSet<Pawn>(shieldEligible.Take(shieldSlots));
            List<Loadout> shieldPacks = packPool.Where(EquipmentPackUtility.ContainsShield).ToList();

            // packCoverage (0..1) ranks pack TYPES by essentialness and includes progressively more of them
            // as the slider rises - it does NOT mean "some pawns get every pack, others get none" (that was
            // the original, wrong reading: coverage gated PAWNS, not WHICH packs, so a mid-slider setting
            // could hand a shooter's whole squad zero food/medicine while a melee pawn further down the list
            // got everything). Base Supply-like packs (mixed content, or Food-led - the "Grundausrüstung"
            // every pawn needs first: food+medicine) rank most essential; Medical/Drugs/Utility-specialized
            // content only layers on top once included. Every ELIGIBLE pawn gets every pack up to the
            // threshold (still subject to the Medical gate below) - coverage never excludes a pawn outright
            // anymore, only which pack TYPES exist for anyone at that setting. Shield-bearing packs are
            // excluded from this ranking/threshold entirely - shieldCoverage above already decided their fate.
            int PackRank(Loadout pack)
            {
                PackCategory? dom = EquipmentPackUtility.DominantCategory(pack);
                if (dom == null || dom == PackCategory.Food) return 0;
                if (dom == PackCategory.Medical) return 1;
                if (dom == PackCategory.Drugs) return 2;
                if (dom == PackCategory.Utility) return 3;
                return 4;
            }
            List<Loadout> rankedPackPool = packPool.Where(p => !shieldPacks.Contains(p)).ToList();
            int maxPackRank = rankedPackPool.Count > 0 ? rankedPackPool.Max(PackRank) : 0;
            // steps: 0 = nothing at all ("No packs"), 1 = rank 0 only, ..., maxPackRank+1 = every rank
            // ("Generous packs") - keeps "fully disable packs" reachable at the slider's own left extreme
            // while everything above it fills in essential-first, matching how Dialog_GenerateEquipmentPack's
            // own Essential<->All slider already ranks its templates (TemplateOrder/TemplateRank).
            int packSteps = maxPackRank + 2;

            // Ab hier GRADUELL statt alles-oder-nichts (2026-08-09): wie bei Schilden, und bei medizinlastigen
            // Packs bekommen Kolonisten mit hohem Medical-Skill das Pack bevorzugt zuerst.
            // Vorher tat derselbe Regler auf verschiedenen Zeilen etwas anderes: bei Schilden hiess 60% "die
            // sechs bestgeeigneten von zehn", bei einem Medic Pack weiterhin "alle oder keiner", je nachdem ob
            // die Rangschwelle gerissen wurde - die Vorschau sprang von 0 auf alle.
            //
            // 2026-08-16, Vorgabe: zuerst Aerzte, aber langsam mehr, dann Helfer, aber langsam mehr, dann
            // alle, aber langsam mehr. Im Test war 0 -> 5 -> 6 -> 9 gemessen, mit fuenf
            // Kolonisten auf einen 5%-Schritt. Ursache war NICHT die Rampe, die gab es schon - es war eine
            // HARTE Rangschwelle darueber (packRankThreshold): sie liess einen Rang erst ab der Mitte seiner
            // eigenen Rampe ueberhaupt zu, und in dem Moment stand die Rampe bereits bei 50 %. Also sprang
            // jeder Rang mit der halben Gruppe ins Bild. Die Schwelle ist jetzt weg; die Rampe allein
            // entscheidet, und sie liefert fuer einen noch nicht erreichten Rang von sich aus null Traeger.
            //
            // Damit rampt auch Rang 0 (Grundausruestung) - vorher "alle oder keiner". Das ist der Preis fuer
            // diese Vorgabe und zugleich die einzige Art, das linke Ende ("No packs") zu behalten: bei Regler 0
            // ist jede Rampe null, also bekommt niemand etwas. Vorher hielt die Rangschwelle dieses Ende.
            float packPos = packCoverage * (packSteps - 1);
            List<Pawn> combatOrderedEligible = order.Select(e => e.pawn).Where(packEligible.Contains).Concat(packOnlyPawns).ToList();
            // Medizin nach Medical-Skill, absteigend - genau so vorgegeben. Pazifisten sind hier absichtlich
            // mit drin: ein Arzt, der nie kaempft, ist der beste Traeger fuer ein Medic Kit, nicht der
            // schlechteste (dieselbe Ueberlegung wie beim packOnlyPawns-Zweig oben).
            List<Pawn> medicalOrdered = medicPackEligible
                .OrderByDescending(p => p.skills?.GetSkill(SkillDefOf.Medicine)?.Level ?? 0)
                .ThenBy(p => p.LabelShortCap.ToString())
                .ToList();
            Dictionary<int, HashSet<Pawn>> rankRecipients = new Dictionary<int, HashSet<Pawn>>();
            for (int r = 0; r <= maxPackRank; r++)
            {
                List<Pawn> pool = r == 1 ? medicalOrdered : combatOrderedEligible;
                float fill = Mathf.Clamp01(packPos - r);
                // Aufrunden, nicht kaufmaennisch runden: sonst passiert am ANFANG jeder Stufe wieder nichts.
                // Bei zehn Traegern braeuchte Runden 5 % Reglerweg, ehe der erste ueberhaupt erscheint - genau
                // das "zwischen 25 und 75 passiert gar nichts", das im Test auffiel. Aufrunden heisst: sobald
                // eine Stufe anfaengt, bewegt sich auch etwas, und danach kommt einer nach dem anderen dazu.
                int slots = fill <= 0f ? 0 : Mathf.Clamp(Mathf.CeilToInt(pool.Count * fill), 1, pool.Count);
                rankRecipients[r] = new HashSet<Pawn>(pool.Take(slots));
            }

            // `e` ist der Eintrag, dessen Tragkraft-Budget die Packs belasten ("Respect max bulk") - fuer die
            // Kampfschleife der Eintrag der Zeile, fuer den Nur-Pack-Durchgang der Pazifisten-Eintrag.
            List<Loadout> ChoosePacksFor(Pawn pawn, AssignPlanEntry e)
            {
                var chosen = new List<Loadout>();
                // Ein Pack kommt nur hinein, wenn es in das Restbudget passt; sonst wird es fuer die Meldung
                // gemerkt und das NAECHSTE probiert (ein leichteres Medic Kit kann noch passen, wenn die
                // Grundausruestung nicht mehr ging). Ohne Haken nimmt TryTake alles.
                // PACKS SIND VON DER SCHRANKE AUSGENOMMEN (2026-08-30): es ergibt keinen Sinn, zwei Zweitwaffen
                // hineinzustopfen und dafuer keine Packs zu geben - Packs werden sehr wohl zugeteilt, die Warnung
                // fuers Gewicht bleibt. Sie belasten das Budget weiterhin (die Zweitwaffe
                // danach sieht also, was schon draufliegt), aber ein Pack wird nie mehr weggelassen - und die
                // Ueberladungs-Warnung unten nennt den Kolonisten wie bisher mit Zahlen.
                void Take(Loadout pack) { chosen.Add(pack); }
                // One shield pack, not every shield pack in the pool (2026-07-31 fix) - AddRange used to hand
                // a shield-eligible pawn ALL of them simultaneously whenever 2+ distinct shield-bearing packs
                // existed (e.g. a player's own custom shield pack alongside the built-in "Shield Kit"), most
                // visible at shieldCoverage=1.0 where every eligible pawn was affected at once.
                if (shieldGiven.Contains(pawn) && shieldPacks.Count > 0) Take(shieldPacks[0]);
                foreach (Loadout pack in rankedPackPool)
                {
                    int rank = PackRank(pack);
                    // Statt der alten Alles-oder-nichts-Schwelle: gehoert dieser Pawn zu den Traegern, die
                    // dieser Rang bei der aktuellen Reglerstellung abdeckt? Bei Medizin ist das die
                    // Skill-Reihenfolge, sonst die Kampf-Reihenfolge (dieselbe wie bei Schilden).
                    // Der alte separate medics-Gate ist damit weg: er hat dasselbe gemeint, aber mit einer
                    // fixen 40%-Grenze, die der Regler nicht bewegen konnte.
                    if (rankRecipients.TryGetValue(rank, out HashSet<Pawn> recips) && !recips.Contains(pawn)) continue;
                    Take(pack);
                }
                return chosen;
            }

            // ---- Was nach dem Uebernehmen an Extras WIRKLICH haengt ----
            // Eine LEERE Plan-Liste heisst seit 2026-08-19 "nicht anfassen" (WantsPackWrite/WantsSidearmWrite): der
            // Kolonist behaelt dann, was er heute traegt - und das wiegt, also muss die Ueberladungs-Warnung es sehen.
            // Ausnahme: ein Rollen-Pick mit Vorlagenwechsel leert den Anker (EnsureAmmoLink), dann haengt nichts mehr.
            // EffectivePacks/EffectiveSidearms sind damit die Mengen NACH dem Uebernehmen und die einzige Quelle der
            // Warnung. Seit 2026-09-01 sind sie STATISCHE Helfer neben ReweighEntry (siehe dort) - der
            // In-Place-Pin braucht dieselbe Antwort wie dieser Solve.

            // Sidearm: a second Weapon Loadout attached the same live way Equipment Packs are (multi-parent,
            // see SidearmAssignmentUtility) - opposite weapon type from the main pick (a ranged main gets a
            // melee backup, a melee main a ranged one), same "sidearms still exist, just as their own
            // Weapon Loadout now" idea this session's architecture was built around. sidearmCoverage (0..1)
            // covers that fraction of the squad, best-combat-priority first (the same order weapons are
            // handed out in) rather than a per-pawn coin flip, so a partial setting always arms the
            // sharpest shooters/fighters first instead of a scattershot subset.
            int sidearmGiven = 0;
            // Its own reuse count, separate from timesUsed above - a loadout already handed out as someone's
            // MAIN shouldn't be penalized as a sidearm pick for someone else (different "slot"), but two
            // pawns both getting the exact same sidearm should still be discouraged as diversity rises,
            // same shape as PickBest's own reuse penalty (just without the group-cap/role-coverage terms,
            // which are about the squad's MAIN loadout composition, not its backups).
            Dictionary<AssignCandidate, int> timesUsedSidearm = new Dictionary<AssignCandidate, int>();

            // A proper sidearm is a compact BACKUP weapon, not just "the next-best opposite-type gun" - a
            // full battle rifle scoring well should never get auto-picked as someone's "sidearm" just
            // because it out-scores a pistol. Reuses the same cap the old baked-in-sidearm mechanism
            // already had (CEReloadedSettings.sidearmMaxBulk, still configurable in mod settings and now
            // also inline in this popup) rather than inventing a second, disconnected setting.
            // Seit 2026-08-25 ueber SidearmAssignmentUtility.MaxSidearmBulk - dieselbe Zahl, die das
            // Zweitwaffen-Auswahlfenster jetzt zum Ausblenden benutzt. Eine Quelle, zwei Leser.
            float maxSidearmBulk = SidearmAssignmentUtility.MaxSidearmBulk();

            // `limit` = das Restbudget des Kolonisten ("Respect max bulk"), null = keine Schranke.
            AssignCandidate PickSidearm(bool wantRanged, ThingDef excludeWeapon)
            {
                AssignCandidate best = null;
                float bestVal = float.NegativeInfinity;
                foreach (AssignCandidate c in cands)
                {
                    // Grenades and shields are their own separate pools now (PickGrenade/PickShield below) -
                    // a real backup weapon shouldn't compete with either for the same slot, or overlap into
                    // their pools.
                    if (Dialog_GenerateLoadouts.IsGrenade(c.mainWeapon) || LoadoutUtility.IsShieldApparel(c.mainWeapon)) continue;
                    if (c.isRanged != wantRanged || c.mainWeapon == excludeWeapon) continue;
                    if (LoadoutUtility.WeaponBulk(c.mainWeapon) > maxSidearmBulk) continue;
                    bool hasStock = stock.TryGetValue(c.mainWeapon, out int s) && s > 0;
                    if (!hasStock && (craft <= 0.001f || !c.producible)) continue;
                    float targetDamage = Mathf.Lerp(c.fleshScore, c.mechScore, mech);
                    float tilt = Mathf.Abs(mech - 0.5f) * 2f * MechTilt;
                    float val = Mathf.Lerp(c.score, targetDamage, tilt);
                    if (!hasStock) val -= CraftPenalty(c);
                    if (c.isVirtual) val -= 4f;
                    int usedSide = timesUsedSidearm.TryGetValue(c, out int ts) ? ts : 0;
                    val -= diversity * 55f * usedSide;
                    if (val > bestVal) { bestVal = val; best = c; }
                }
                return best;
            }

            // Explosives: a separate budget and pool from real backup weapons (PickSidearm above), sharing
            // only the same underlying attach mechanism and the same "Sidearm" list on the pawn - a satchel
            // charge is not a substitute for a knife, so it must never compete with one for the same slot.
            // grenadeCoverage (0..1) covers this fraction of the squad, same best-combat-priority-first shape
            // as sidearmCoverage; no melee/ranged split here, a grenade complements everyone the same way.
            int grenadeGiven = 0;
            Dictionary<AssignCandidate, int> timesUsedGrenade = new Dictionary<AssignCandidate, int>();

            // `limit` wie bei PickSidearm.
            AssignCandidate PickGrenade(ThingDef excludeWeapon)
            {
                AssignCandidate best = null;
                float bestVal = float.NegativeInfinity;
                foreach (AssignCandidate c in cands)
                {
                    if (!Dialog_GenerateLoadouts.IsGrenade(c.mainWeapon) || c.mainWeapon == excludeWeapon) continue;
                    bool hasStock = stock.TryGetValue(c.mainWeapon, out int s) && s > 0;
                    if (!hasStock && (craft <= 0.001f || !c.producible)) continue;
                    float targetDamage = Mathf.Lerp(c.fleshScore, c.mechScore, mech);
                    float tilt = Mathf.Abs(mech - 0.5f) * 2f * MechTilt;
                    float val = Mathf.Lerp(c.score, targetDamage, tilt);
                    if (!hasStock) val -= CraftPenalty(c);
                    if (c.isVirtual) val -= 4f;
                    int usedNade = timesUsedGrenade.TryGetValue(c, out int tn) ? tn : 0;
                    val -= diversity * 55f * usedNade;
                    if (val > bestVal) { bestVal = val; best = c; }
                }
                return best;
            }

            // Collected during the main loop below, resolved in two passes AFTER every pawn has a main
            // weapon: entries wanting a MELEE sidearm (their main is ranged) fill sidearmSlots first, entries
            // wanting a RANGED sidearm (their main is melee) only get one once every melee-wanting entry
            // already has theirs. A shooter's backup blade is the far more broadly useful case (ammo runs
            // out, raiders close to melee range) than a brawler also carrying a gun, so partial coverage
            // should arm every shooter with a blade before arming any brawler with a gun - not interleave
            // the two by combat-priority order the way a single pass naturally would.
            List<(AssignPlanEntry entry, bool wantRanged)> sidearmEligible = new List<(AssignPlanEntry, bool)>();
            // Same idea, no melee/ranged split - a grenade suits everyone the same way, so insertion order
            // (already best-combat-priority-first, same as the main loop below) is the only ordering needed.
            List<AssignPlanEntry> grenadeEligible = new List<AssignPlanEntry>();

            foreach (AssignPlanEntry e in order)
            {
                float pr = rangedScore(e.pawn), pm = meleeScore(e.pawn);
                // "Respect max bulk": die Tragkraft des Kolonisten MINUS dem, was unabhaengig von dieser Wahl
                // haengen bleibt (angeheftete Extras, heutige Packs ohne neue Vergabe - ReservedBeforeMain),
                // als Schranke fuer die Hauptwaffe.
                AssignCandidate pick = PickBest(isShooter[e.pawn], pr, pm) ?? PickBest(!isShooter[e.pawn], pr, pm);
                if (pick == null) continue;
                bool hasStock = stock.TryGetValue(pick.mainWeapon, out int s2) && s2 > 0;
                e.planned = pick.loadout;
                e.plannedVirtual = pick.isVirtual;
                e.plannedMain = pick.mainWeapon;
                e.needsCraft = !hasStock;
                if (hasStock) stock[pick.mainWeapon] = s2 - 1;
                usedInGroup[pick.group] = (usedInGroup.TryGetValue(pick.group, out int g2) ? g2 : 0) + 1;
                timesUsed[pick] = (timesUsed.TryGetValue(pick, out int t2) ? t2 : 0) + 1;

                // A pinned pack set (Dialog_SquadSetup's own pack picker) already carries the right value
                // from entry creation above - the solver leaves it alone regardless of the weapon pick.
                // A Role loadout may already have pack content BAKED into it directly (Role Mode's own
                // drag-a-pack-onto-a-role mechanism, RoleUtility.Merge) - auto-attaching every pack again
                // here on top would double it up (e.g. two lots of Base Supply medicine, one from the
                // Role's own bake and one live via the per-pawn anchor). Only plain, non-Role assignments
                // get every pack auto-attached; a Role's author already made that choice deliberately.
                if (packPool.Count > 0 && !packPinnedPawns.Contains(e.pawn) && !pick.isRole)
                    e.plannedPacks = ChoosePacksFor(e.pawn, e);

                // Same Role exception as packs - a Role that already bakes a second weapon in via its own
                // drag-multiple-loadouts-onto-a-role mechanism should not also get one auto-attached here.
                // A pinned sidearm set (Dialog_SquadSetup's own sidearm picker) already carries the right
                // value from entry creation above - the solver leaves it alone regardless of the weapon pick.
                if (!pick.isRole && !sidearmPinnedPawns.Contains(e.pawn))
                {
                    sidearmEligible.Add((e, !pick.isRanged));
                    grenadeEligible.Add(e);
                }
            }

            // Against the pool that's ACTUALLY competing for a slot, not the whole squad (2026-08-02 fix,
            // order.Count includes Role picks and pinned pawns, which sidearmEligible/
            // grenadeEligible above deliberately exclude - at the slider's own extremes (0%/100%) the two
            // pools happen to agree, but at any setting in between, a squad with a non-trivial mix of Role/
            // pinned picks got a real coverage percentage well off from what the slider position implied).
            int sidearmSlots = Mathf.RoundToInt(sidearmEligible.Count * sidearmCoverage);
            int grenadeSlots = Mathf.RoundToInt(grenadeEligible.Count * grenadeCoverage);

            // Melee-wanting entries (wantRanged == false) sort first - that pass structure stays exactly as
            // is. WITHIN each of the two groups, entries used to just fall back to insertion (combat-priority)
            // order; now ranked by whichever skill the incoming sidearm actually calls on - a ranged main
            // with a strong Melee skill is the one who gets real use out of a backup blade, so a limited
            // sidearmSlots budget should reach them before someone who'd barely land a hit with it.
            foreach (var (e, wantRanged) in sidearmEligible.OrderBy(x => x.wantRanged)
                .ThenByDescending(x => x.wantRanged ? rangedScore(x.entry.pawn) : meleeScore(x.entry.pawn)))
            {
                if (sidearmGiven >= sidearmSlots) break;
                AssignCandidate side = PickSidearm(wantRanged, e.plannedMain);
                // Ein virtueller Kandidat hat kein Loadout-Objekt (siehe isVirtual oben). Bis zum Lazy-Umbau
                // (2026-09-01) wurde er hier verworfen - seither wandert die blosse WAFFE in
                // plannedSidearmDefs, und die Vorlage entsteht erst beim Uebernehmen (ApplyPreviewPlan).
                // Ein nicht-virtueller Kandidat ohne Loadout bleibt ein Datenfehler und faellt weiter raus.
                if (side != null && !side.isVirtual && side.loadout == null) side = null;
                if (side == null) continue;
                bool sideStock = stock.TryGetValue(side.mainWeapon, out int ss) && ss > 0;
                if (side.isVirtual)
                {
                    if (e.plannedSidearmDefs == null) e.plannedSidearmDefs = new List<ThingDef>();
                    e.plannedSidearmDefs.Add(side.mainWeapon);
                }
                else e.plannedSidearms = new List<Loadout> { side.loadout };
                if (sideStock) stock[side.mainWeapon] = ss - 1;
                timesUsedSidearm[side] = (timesUsedSidearm.TryGetValue(side, out int tu) ? tu : 0) + 1;
                sidearmGiven++;
            }

            // Who should carry live ordnance, algorithmically rather than by any hardcoded trait list (a
            // hardcoded def would silently stop matching the moment a modded trait/skill system replaces or
            // extends the vanilla one). Two signals, both universal to any RimWorld install:
            //   - Shooting skill, as a stand-in for throwing accuracy - CE grenades fly a real ballistic arc,
            //     so a poor throw risks landing short, on the thrower's own position or a nearby ally.
            //   - Live proximity to a mental break (mood vs. this exact pawn's OWN break thresholds, already
            //     inflated by whatever trait causes it - Volatile, a modded equivalent, doesn't matter which)
            //     - a break with explosives in inventory is the textbook self-inflicted disaster.
            // Pyromaniac is the one trait actually named here, since it is guaranteed vanilla-present (not
            // mod-dependent) and its specific risk - torching stored ordnance - isn't a mood/mental-break
            // question at all, so the general check above wouldn't otherwise catch it.
            float ExplosivesSuitability(Pawn p)
            {
                float s = 0f;
                SkillRecord shoot = p.skills?.GetSkill(SkillDefOf.Shooting);
                if (shoot != null && !shoot.TotallyDisabled) s += shoot.Level;
                if (p.mindState?.mentalBreaker != null && p.needs?.mood != null)
                {
                    float moodPct = p.needs.mood.CurLevelPercentage;
                    float breakRisk = Mathf.Max(0f, p.mindState.mentalBreaker.BreakThresholdMinor - moodPct);
                    s -= breakRisk * 100f;
                }
                if (p.story?.traits != null && p.story.traits.HasTrait(TraitDefOf.Pyromaniac)) s -= 15f;
                return s;
            }

            // Explosives fill independently, into the SAME plannedSidearms list a real backup weapon may have
            // already claimed above - one shared attach mechanism and GUI cell, two separate budgets/pools
            // feeding it, exactly as agreed: grenades and a real sidearm are never in competition with
            // each other, but both end up listed as "this pawn's sidearms" either way. Ordered by
            // ExplosivesSuitability (descending) instead of plain combat-priority order, so a limited
            // grenadeSlots budget reaches the safest/most accurate carriers first.
            foreach (AssignPlanEntry e in grenadeEligible.OrderByDescending(e => ExplosivesSuitability(e.pawn)))
            {
                if (grenadeGiven >= grenadeSlots) break;
                AssignCandidate nade = PickGrenade(e.plannedMain);
                if (nade == null) continue;
                bool nadeStock = stock.TryGetValue(nade.mainWeapon, out int ns) && ns > 0;
                // Gleiche Zweiteilung wie beim Sidearm oben (Lazy-Umbau 2026-09-01): virtuell -> Waffe
                // merken, real -> Vorlage anhaengen. Ein realer Kandidat ohne Loadout bleibt draussen.
                if (nade.isVirtual)
                {
                    if (e.plannedSidearmDefs == null) e.plannedSidearmDefs = new List<ThingDef>();
                    e.plannedSidearmDefs.Add(nade.mainWeapon);
                }
                else if (nade.loadout != null) e.plannedSidearms.Add(nade.loadout);
                if (nadeStock) stock[nade.mainWeapon] = ns - 1;
                timesUsedGrenade[nade] = (timesUsedGrenade.TryGetValue(nade, out int tn2) ? tn2 : 0) + 1;
                grenadeGiven++;
            }

            // Shields are Equipment Pack content now, not a sidearm-style pick - ChoosePacksFor above already
            // handles them via their own dedicated shieldCoverage fill (melee-oriented pawns first, best
            // fighters first, unless the whole squad is ranged). No separate fill pass needed here.

            // Pack-only pass for pacifists/non-combatants (2026-08-01 fix, see packOnlyPawns above) - they
            // never entered `order`, so they never went through the main weapon-picking loop above where
            // every other pawn's ChoosePacksFor call lives. Their AssignPlanEntry already exists (added at
            // the very top of this method, kept=true, plannedPacks defaulted to whatever they already had) -
            // just overwrite plannedPacks with a real auto-assignment, same function, same rules.
            if (packPool.Count > 0)
                foreach (Pawn p in packOnlyPawns)
                {
                    AssignPlanEntry entry = plan.entries.FirstOrDefault(x => x.pawn == p);
                    if (entry == null) continue;
                    entry.plannedPacks = ChoosePacksFor(p, entry);
                    // `kept` defaulted true above (the pacifist "nothing to do" entry) and must be flipped once
                    // this freshly-computed pack plan differs from what they already carry, or ApplyPreviewPlan's
                    // `if (e.kept) continue;` discards it (2026-08-01 fix: the whole "give
                    // pacifists an auto-assigned pack" feature computed correctly but never applied to anyone).
                    // The flip itself now lives in the single sweep at the end of this method - same condition,
                    // one place, and it covers the pin path too.
                }

            // "Passt das ueberhaupt auf den Kolonisten?" gehoert VOR das Uebernehmen (2026-08-13, im Test:
            // zwei von neun Kolonisten waren nach Auto-assign ueberladen - einer mit
            // Masse 56.9/30.4 kg, also fast dem Doppelten -, und in der Vorschau stand dazu nichts. Die roten
            // Balken und der richtige Tooltip erscheinen erst danach im Assign-Tab, und nur beim Hovern).
            // Kein hartes Limit: Grundsatz ist "erlauben, aber eine gut sichtbare Warnung einbauen".
            // Die Warnung stand seither an der Kapazitaetsleiste - nur
            // eben eine Stufe zu spaet. Der Solver selbst bleibt unangetastet.
            // Gerechnet gegen CEs eigene Werte des KONKRETEN Pawns (nicht den Median), weil hier ein
            // bestimmtes Loadout einem bestimmten Kolonisten zugedacht ist.
            // Tragkraft und Begruendungstext kommen aus denselben Helfern wie die Schranke oben (CapOf,
            // WhyExceeds) - seit 2026-08-25 bewusst EINE Rechnung fuer beide.
            // Die ganze Wiege-Logik (Vorgabe heutiger Stand, drei Gewichtsquellen, Extras zaehlen mit; auch
            // der Sanitaeterinnen-Fall "kampfunfaehig, bekommt aber Packs" aus 2026-08-14) lebt seit
            // 2026-09-01 in ReweighEntry - EINE Rechnung fuer diesen Solve UND den In-Place-Pin
            // (Dialog_SquadSetup.AdoptExtrasPin). Die Box-Zeile MIT GRUND UND ZAHLEN (2026-08-16) baut
            // RefreshOverloadProblem aus den Eintragsfeldern - dieselbe Funktion laeuft beim Pin, damit
            // Box und Leisten nie zwei verschiedene Staende zeigen.
            foreach (AssignPlanEntry e in plan.entries)
                ReweighEntry(e);
            RefreshOverloadProblem(plan);

            // "Respect max bulk", die zwei Folgen der Schranke - beide nur mit Haken befuellt:
            // wer gar keine tragbare Hauptwaffe bekam, und wem Packs/Zweitwaffen weggelassen wurden.

            BuildProblems(plan, cands, targets, craft, mech, melee, diversity, roleMode);

            // Colonists who got a loadout but currently can't use it (downed / injured / paralysed) - may be
            // temporary, so they are only flagged, not blocked (per design). Skipped pacifists are already obvious
            // from their "can't fight - skipped" row above, so they are not repeated here.
            var weak = order.Where(e => (e.planned != null || e.plannedVirtual) && rangedScore(e.pawn) <= 0f && meleeScore(e.pawn) <= 0f)
                .Select(e => e.pawn.LabelShortCap).ToList();
            if (weak.Count > 0)
                plan.problems.Add(new AssignProblem("CER_AssignPlanner_WeakColonistsProblem".Translate(weak.Count.ToString(), string.Join(", ", weak.ToArray()))));

            // Last, after every pass above has settled plannedPacks/plannedSidearms - one O(pawns) sweep per
            // solve instead of the same comparison per frame out of ApplyLabel.
            foreach (AssignPlanEntry e in plan.entries)
            {
                RefreshExtrasChanged(e);
                // The invariant the whole pack/sidearm layer depends on: `kept` means "skip this row
                // wholesale" for both the change count and the apply loop, so it must never be true while
                // there is still pack/sidearm work on it. Cheap here, and it covers every producer at once -
                // the pin path, the pacifist pack-only pass (which grew its own copy of this line), and any
                // future one.
                if (e.extrasChanged) e.kept = false;
            }

            return plan;
        }

        // ---- Vorschau-Gewicht einer Zeile: EINE Rechnung fuer Solve UND In-Place-Pin (2026-09-01) ----
        //
        // Bis heute wurde previewMass/previewBulk NUR im vollen Solve gesetzt. Ein von Hand angeheftetes
        // Pack oder eine angeheftete Zweitwaffe patcht den Eintrag aber absichtlich in-place (siehe
        // Dialog_SquadSetup.AdoptExtrasPin - ein voller RecomputeAssign wuerde fremde Zeilen umwerfen),
        // und dabei wurde nie nachgewogen: die Nachschub-Symbole der Zeile lasen die neuen Zweitwaffen
        // sofort, die Mass/Bulk-Leisten daneben zeigten weiter den Stand vom letzten Solve (Zweitwaffen
        // und Packs bewegten die Masse-/Gewichtsleiste nicht).
        // Deshalb liegt die ganze Wiege-Logik jetzt hier als statische Funktion; der Solve ruft sie pro
        // Zeile, der Pin fuer die eine gepatchte Zeile. Rueckgabe: wurde WIRKLICH gewogen (dann darf die
        // Ueberladungs-Box den Kolonisten nennen) oder blieb es beim heutigen Stand als Vorgabe.
        internal static bool RoleClears(AssignPlanEntry e) => e.planned != null && RoleUtility.IsRoleLoadout(e.planned)
            && e.pawn != null && AmmoAssignmentUtility.DisplayLoadout(e.pawn.GetLoadout()) != e.planned;
        internal static List<Loadout> EffectivePacks(AssignPlanEntry e)
            => (e.plannedPacks != null && e.plannedPacks.Count > 0) || e.packPinned
               ? (e.plannedPacks ?? new List<Loadout>())
               : (RoleClears(e) ? new List<Loadout>() : (AmmoAssignmentUtility.GetPawnPacks(e.pawn) ?? new List<Loadout>()));
        internal static List<Loadout> EffectiveSidearms(AssignPlanEntry e)
            => (e.plannedSidearms != null && e.plannedSidearms.Count > 0) || e.sidearmPinned
               ? (e.plannedSidearms ?? new List<Loadout>())
               : (RoleClears(e) ? new List<Loadout>() : (SidearmAssignmentUtility.GetPawnSidearms(e.pawn) ?? new List<Loadout>()));

        // Tragkraft-Grenzen eines Kolonisten - dieselben Rueckfaelle wie das fruehere Solve-lokale CapOf.
        // Ohne den per-Solve-Cache: der Pin-Pfad fragt genau EINEN Kolonisten, der Solve einmal pro Zeile.
        internal static (float mass, float bulk) CapacityOf(Pawn p)
        {
            float capMass = LoadoutUtility.CarryWeightOf(p);
            StatDef bulkStat = LoadoutUtility.CarryBulkStat;
            float capBulk = bulkStat != null ? p.GetStatValue(bulkStat) : 20f;
            if (capMass <= 0f) capMass = 35f;
            if (capBulk <= 0f) capBulk = 20f;
            return (capMass, capBulk);
        }

        internal static bool ReweighEntry(AssignPlanEntry e)
        {
            if (e == null) return false;
            e.previewWeighed = false;
            if (e.pawn == null) return false;
            RefreshExtrasChanged(e);
            (float capM0, float capB0) = CapacityOf(e.pawn);
            e.capMass = capM0; e.capBulk = capB0;
            LoadoutUtility.GetLoadoutMassBulk(e.current, out float curM, out float curB);
            e.previewMass = curM; e.previewBulk = curB;   // Vorgabe: der heutige Stand, unten ueberschrieben
            if (e.cannotFight && !e.extrasChanged) return false;

            // Die Mengen NACH dem Uebernehmen, nicht die rohen Plan-Listen: eine leere Plan-Liste heisst
            // "behaelt seine heutigen" - siehe EffectivePacks/EffectiveSidearms direkt darueber.
            List<Loadout> extras = EffectivePacks(e).Concat(EffectiveSidearms(e)).ToList();
            bool hasVirtualSidearms = e.plannedSidearmDefs != null && e.plannedSidearmDefs.Count > 0;
            bool hasExtras = extras.Any(x => x != null) || hasVirtualSidearms;

            // EINE Gewichtsquelle mit drei Faellen (Historie siehe die Kommentare im Solve, 2026-08-15):
            // echte Vorlage, virtueller Pick (aus derselben Vorgabe gewogen, aus der beim Uebernehmen die
            // Vorlage entsteht), oder keine Waffe aber Extras.
            float lMass, lBulk;
            if (e.planned != null)
            {
                LoadoutUtility.GetLoadoutMassBulk(e.planned, out lMass, out lBulk);
            }
            else if (e.plannedVirtual && e.plannedMain != null)
            {
                Dialog_GenerateLoadouts.DefaultMassBulkFor(e.plannedMain, out lMass, out lBulk);
            }
            else if (hasExtras)
            {
                lMass = 0f; lBulk = 0f;
            }
            else return false;   // wirklich nichts zu wiegen - Vorgabe (heutiger Stand) bleibt stehen
            // Packs und Zweitwaffen ZAEHLEN MIT (2026-08-13): e.planned ist die reine Waffenvorlage,
            // die Extras haengen als eigene Loadouts daneben - und genau die machen Sperrigkeit aus.
            foreach (Loadout extra in extras)
            {
                if (extra == null) continue;
                LoadoutUtility.GetLoadoutMassBulk(extra, out float em, out float eb);
                lMass += em; lBulk += eb;
            }
            // Virtuelle Zweitwaffen/Granaten wiegen wie der virtuelle Hauptwaffen-Pick: aus derselben
            // Vorgabe, aus der beim Uebernehmen die Vorlage entsteht (Lazy-Umbau 2026-09-01).
            if (hasVirtualSidearms)
                foreach (ThingDef sd in e.plannedSidearmDefs)
                {
                    Dialog_GenerateLoadouts.DefaultMassBulkFor(sd, out float vm, out float vb);
                    lMass += vm; lBulk += vb;
                }
            e.previewMass = lMass; e.previewBulk = lBulk;
            e.previewWeighed = true;
            return true;
        }

        // Sperrigkeit zuerst, das ist in CE der haeufigere Fall.
        internal static List<string> WhyExceeds((float mass, float bulk) load, (float mass, float bulk) cap)
        {
            List<string> why = new List<string>();
            if (load.bulk > cap.bulk)
                why.Add("CER_AssignPlanner_OverloadBulk".Translate(load.bulk.ToString("0.0"), cap.bulk.ToString("0.0")).ToString());
            if (load.mass > cap.mass)
                why.Add("CER_AssignPlanner_OverloadMass".Translate(load.mass.ToString("0.0"), cap.mass.ToString("0.0")).ToString());
            return why;
        }

        // Baut die EINE Ueberladungs-Zeile der Problem-Box aus den Eintragsfeldern neu - beim Solve UND
        // beim In-Place-Pin (2026-09-01: die vorher
        // dokumentierte Grenze "Box rechnet erst beim naechsten Neurechnen" war unnoetig - nach ReweighEntry
        // stehen alle Zahlen an den Eintraegen, es gab keinen Grund mehr, die Box alt zu lassen).
        // Ersetzt die bestehende Zeile an Ort und Stelle (isOverloadLine), damit ihre Position in der Box
        // stabil bleibt; verschwindet die letzte Ueberladung, verschwindet die Zeile.
        internal static void RefreshOverloadProblem(AssignPlan plan)
        {
            if (plan == null || plan.entries == null || plan.problems == null) return;
            var overloaded = new List<string>();
            foreach (AssignPlanEntry e in plan.entries)
            {
                if (e?.pawn == null || !e.previewWeighed) continue;
                if (e.previewMass > e.capMass || e.previewBulk > e.capBulk)
                    overloaded.Add(e.pawn.LabelShortCap + " (" + string.Join(", ", WhyExceeds((e.previewMass, e.previewBulk), (e.capMass, e.capBulk)).ToArray()) + ")");
            }
            int idx = plan.problems.FindIndex(p => p != null && p.isOverloadLine);
            if (overloaded.Count == 0)
            {
                if (idx >= 0) plan.problems.RemoveAt(idx);
                return;
            }
            var line = new AssignProblem("CER_AssignPlanner_OverloadedProblem".Translate(
                overloaded.Count.ToString(), string.Join(", ", overloaded.ToArray()))) { isOverloadLine = true };
            if (idx >= 0) plan.problems[idx] = line; else plan.problems.Add(line);
        }

        // The ONE definition of "Apply will write this row's packs" - read both by the change count
        // (AssignPlanEntry.extrasChanged) and by the writer itself (Dialog_AssignLoadouts.ApplyPreviewPlan),
        // so the button and the commit can't disagree about whether anything is pending.
        // Deliberately NOT gated on a planned loadout: packs hang off the pawn's own hidden anchor, not off a
        // weapon plan, so a row with no loadout at all can still receive one (the pack-only pass for
        // pacifists) - its anchor template is then CE's own "None" marker, exactly what the live Assign tab's
        // Pack cell already attaches to. For such a row an EMPTY plan is not a removal instruction, though:
        // plannedPacks is only ever filled for rows the solver actually picked for, so treating empty as
        // "clear" would strip the packs off every colonist the plan simply left without a weapon.
        public static bool WantsPackWrite(AssignPlanEntry e)
        {
            if (e == null) return false;
            List<Loadout> want = e.plannedPacks ?? new List<Loadout>();
            // ====== Der Schutz galt nur fuer Zeilen OHNE Waffenplan - das war zu eng (2026-08-19) ======
            // Die Begruendung eine Zeile darueber ist richtig und vollstaendig: plannedPacks wird NUR fuer
            // Zeilen gefuellt, fuer die der Loeser wirklich ein Pack ausgesucht hat. Eine leere Liste heisst
            // also "nichts ausgesucht", nie "nimm ihm seine weg". Genau das galt aber bisher nur, solange
            // die Zeile auch keine Waffe geplant bekam - bei einer Zeile MIT Waffenplan und leerem Packplan
            // lief die Mengenvergleichszeile darunter durch und hat das Pack des Kolonisten geloescht.
            // Im Test gemessen: drei Kolonisten hatten von Hand ein Medic-Pack, die Vorschau zeigte in der
            // Pack-Spalte
            // ueberall "-", und nach Apply war es bei allen dreien weg - ohne Ankuendigung, ohne Meldung.
            // Ein Pack nimmt man ueber die Pack-Zelle weg, nicht als Nebenwirkung einer Waffenzuweisung.
            // ====== Dritter Fall: der Loeser hat ausgesucht, die Tragkraft-Schranke hat alles gestrichen ======
            // Dann IST die leere Liste die Anweisung (2026-08-27). Ohne diese Ausnahme schreibt Apply nichts,
            // der Kolonist behaelt seine heutigen Packs, und der Haken "Respect max bulk" haelt sein
            // Versprechen nicht - genau der gemessene Fall Ltz. Kein neues Ueberraschungsrisiko: bei
            // ausgeschaltetem Haken ersetzt der Loeser den Packsatz dieser Zeile ohnehin.
            if (want.Count == 0 && !e.packPinned) return false;
            return !SameLoadoutSet(AmmoAssignmentUtility.GetPawnPacks(e.pawn), want);
        }

        // Same for sidearms. Bis 2026-08-25 stand hier `e.planned == null -> false` ("sidearms genuinely need a
        // real weapon loadout to hang off of") - das stimmte nie: die Zweitwaffe haengt wie das Pack am
        // persoenlichen Anker, und der laesst sich ueber CEs None-Vorlage aufspannen. Folge war ein stilles
        // Schlupfloch: eine in der Vorschau angeheftete Zweitwaffe bei einem Kolonisten ohne geplante Waffe
        // zaehlte nicht als Aenderung und wurde beim Apply verworfen (Fall Balork/Jamie; dazu nach
        // aehnlichen Schlupfloechern gesucht). Dialog_AssignLoadouts.ApplyPreviewPlan schreibt sie jetzt im
        // planned==null-Zweig ueber die None-Vorlage, parallel zur Pack-Zeile dort.
        public static bool WantsSidearmWrite(AssignPlanEntry e)
        {
            if (e == null) return false;
            // Dieselbe Luecke wie bei den Packs, per Regel-7-Grep an der Zwillingsstelle gefunden
            // (2026-08-19): plannedSidearms wird nur gefuellt, wenn der Loeser wirklich eine Zweitwaffe
            // ausgesucht hat - und er waehlt nur fuer so viele Kolonisten, wie der Regler hergibt, der Rest
            // bleibt leer. Leer heisst also "nicht ausgesucht", nicht "nimm ihm seine weg". Aufgefallen war
            // nur der Pack-Fall; dieser hier waere als naechstes gekommen.
            // Dieselbe dritte Ausnahme wie bei den Packs (Regel-7-Grep an der Zwillingsstelle, 2026-08-27):
            // hat der Picker fuer diese Zeile wirklich gesucht und nur die Schranke hat ihn gestoppt, heisst
            // leer "keine Zweitwaffe" - sonst bleibt die heutige haengen und frisst genau das Budget, das die
            // Schranke gerade freigehalten hat.
            // Ein virtueller Pick (blosse Waffe, Vorlage entsteht erst beim Uebernehmen) ist IMMER eine
            // Aenderung: eine Vorlage zu dieser Waffe existierte beim Planen nicht, also kann der Kolonist
            // sie auch nicht schon tragen (Lazy-Umbau 2026-09-01).
            bool hasDefs = e.plannedSidearmDefs != null && e.plannedSidearmDefs.Count > 0;
            if ((e.plannedSidearms == null || e.plannedSidearms.Count == 0) && !hasDefs && !e.sidearmPinned) return false;
            if (hasDefs) return true;
            return !SameLoadoutSet(SidearmAssignmentUtility.GetPawnSidearms(e.pawn), e.plannedSidearms);
        }

        // Called once per solve and again whenever a single row's pin is patched in place without a re-solve
        // (Dialog_SquadSetup.ApplyPinnedPacks/ApplyPinnedSidearms).
        public static void RefreshExtrasChanged(AssignPlanEntry e)
        {
            if (e == null) return;
            e.extrasChanged = WantsPackWrite(e) || WantsSidearmWrite(e);
        }

        // Order-independent set compare by UniqueID, null-tolerant on both the lists and their contents (a null
        // entry used to be reachable via a virtual sidearm pick - fixed at the source, but this runs every frame
        // via Changes, so it must never be the thing that throws).
        private static bool SameLoadoutSet(List<Loadout> a, List<Loadout> b)
        {
            var ia = (a ?? new List<Loadout>()).Where(l => l != null).Select(l => l.UniqueID).OrderBy(i => i);
            var ib = (b ?? new List<Loadout>()).Where(l => l != null).Select(l => l.UniqueID).OrderBy(i => i);
            return ia.SequenceEqual(ib);
        }

        // ====== Darf dieser Kandidat bei diesem Schalterwert ueberhaupt vergeben werden? ======
        // Drei Faelle statt zwei (2026-08-19, siehe RoleAssignMode): NoRoles ist der neue, und er ist der
        // einzige, mit dem sich "Ad-Hoc Loadouts per Pawn" im Assistenten ueberhaupt einhalten laesst.
        // Mixed ist das bisherige Verhalten und bleibt der Standard.
        // OEFFENTLICH und an EINER Stelle, weil zwei Listen dieselbe Frage stellen: der Loeser hier und
        // das Anpinn-Menue der Vorschau (Dialog_SquadSetup.OpenPinMenu). Beim ersten Anlauf hatte nur der
            // Loeser gefiltert - im Test fiel prompt auf, dass die Handauswahl bei "Keine Rollen"
        // weiterhin beide Rollen anbot, waehrend der eigene Mouseover des Schalters ausdruecklich "and the
        // per-colonist picker" verspricht. Genau die Doppelung, aus der schon die Fehlbeschriftung kam.
        public static bool RoleModeAllows(AssignCandidate c, RoleAssignMode mode)
        {
            if (c == null) return false;
            if (mode == RoleAssignMode.RolesOnly) return c.isRole;
            if (mode == RoleAssignMode.NoRoles) return !c.isRole;
            return true;
        }

        // The interactive problem list: entries appear only once the current slider weighting makes them matter.
        private static void BuildProblems(AssignPlan plan, List<AssignCandidate> cands, List<AssignPlanEntry> targets,
            float craft, float mech, float melee, float diversity, RoleAssignMode roleMode)
        {
            void P(string text, ProblemAction action = ProblemAction.None)
                => plan.problems.Add(new AssignProblem(text, action));

            if (targets.Count == 0)
            {
                P("CER_AssignPlanner_NothingToAssign".Translate());
                return;
            }
            if (cands.Count == 0)
            {
                // Drei Modi, drei Gruende (2026-08-19): "es gibt keine Rolle" und "es gibt keine
                // Vorlage ausser Rollen" sind verschiedene Sackgassen, und wer nur "keine Loadouts"
                // liest, sucht an der falschen Stelle.
                if (roleMode == RoleAssignMode.RolesOnly)
                    P("CER_AssignPlanner_RolesOnlyNoRoles".Translate(), ProblemAction.OpenRolesTab);
                else
                    P(roleMode == RoleAssignMode.NoRoles ? "CER_AssignPlanner_NoRolesNoPlain".Translate()
                        : "CER_AssignPlanner_NoLoadoutsExist".Translate());
                return;
            }

            int unassigned = targets.Count(e => e.planned == null && !e.plannedVirtual);
            if (unassigned > 0)
            {
                // Seit dem Lazy-Umbau ist jede besessene oder herstellbare Waffe Kandidat - bleibt jemand leer,
                // fehlen wirklich Waffen (2026-09-17: der fruehere Zweig "du besitzt Waffen ohne Loadout" konnte
                // nur noch fuer gesperrte Waffen ausloesen und ist entfallen).
                P(craft <= 0.001f
                    ? "CER_AssignPlanner_UnassignedNoOwned".Translate(unassigned.ToString())
                    : "CER_AssignPlanner_UnassignedNoProducible".Translate(unassigned.ToString()));
            }

            if (mech >= 0.6f && !cands.Any(c => c.mechAffinity >= 0.65f))
                P("CER_AssignPlanner_NoAntiMechOption".Translate());
            if (mech <= 0.25f && !cands.Any(c => c.mechAffinity <= 0.35f))
                P("CER_AssignPlanner_NoAntiFleshOption".Translate());

            if (melee >= 0.6f && !cands.Any(c => !c.isRanged))
                P("CER_AssignPlanner_NoMeleeLoadouts".Translate());
            if (melee <= 0.4f && !cands.Any(c => c.isRanged))
                P("CER_AssignPlanner_NoRangedLoadouts".Translate());

            if (diversity >= 0.6f)
            {
                int groups = cands.Select(c => c.group).Distinct().Count();
                if (groups < Mathf.Min(3, targets.Count))
                    P("CER_AssignPlanner_FewWeaponGroups".Translate(groups.ToString()));
            }
        }
    }
}
