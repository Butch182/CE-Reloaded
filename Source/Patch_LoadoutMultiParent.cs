using System.Collections.Generic;
using System.Linq;
using System.Text;
using HarmonyLib;
using Verse;
using CombatExtended;

namespace CEReloaded
{
    // CORRECTION (2026-08-09, verified): the sentence that used to stand here - "CE's Loadout.Slots getter
    // returns a FRESH list every call" - is WRONG, and it is the dangerous kind of wrong because the next
    // change to this file would be reasoned from it. CE's getter only builds a new union list when the
    // loadout HAS a parent; for a parentless loadout it returns OwnSlots itself, the live list. That is why
    // this Postfix must never mutate __result in place and always builds its own `combined` copy below -
    // writing into __result would be writing into the loadout's real slot list. (Same fact already recorded
    // outside this file: "non-parented loadouts: Slots == OwnSlots (live list); never Clear+re-add from
    // itself".) The Postfix as written is safe; only the reasoning above it was not.
    //
    // This is the one patch that gives real 1..N Loadout
    // attachment on top of CE's native single-parent chain (see MultiParentUtility for who actually uses
    // it - Roles and per-pawn anchors, never plain Weapon Loadouts or Equipment Packs), AND the one place
    // a pawn's personal ammo choice fully REPLACES rather than merely supplements what their Weapon
    // Loadout template would otherwise provide.
    [HarmonyPatch(typeof(Loadout), nameof(Loadout.Slots), MethodType.Getter)]
    public static class Patch_Loadout_MultiParentSlots
    {
        // Threaded across the WHOLE recursive walk instead of reallocated fresh per top-level Postfix call
        // (2026-08-01: the old code's own comment claimed the guard was "shared across the
        // whole walk", but AppendFrom's recursive `lo.Slots` access re-enters this SAME patched getter as a
        // brand-new top-level call, which built its OWN fresh guard set - a genuine 2-node extra-parent
        // cycle would recurse unbounded into an uncatchable StackOverflowException instead of the "silently
        // stop" the old comment claimed). Not currently reachable via any exposed picker/paste path today -
        // MultiParentUtility's own value-exclusion rules mean a value can never itself become a key - but
        // worth hardening properly rather than leaving a broken safety net for a future call site. Single
        // static, not [ThreadStatic]: RimWorld's game/UI logic that ever touches Loadout.Slots runs on the
        // main thread only, and a nested call here is always part of the SAME call stack as its root, never
        // concurrent with it.
        private static HashSet<int> activeGuard;

        public static void Postfix(Loadout __instance, ref List<LoadoutSlot> __result)
        {
            // ...WithAncestors, nicht nur die eigenen (2026-08-13, live gemessen): CEs ParentSlots liest
            // `ancestor.OwnSlots` und damit das rohe Feld, nicht diesen gepatchten Getter - die Extra-Eltern
            // eines VORFAHREN fielen also komplett durch. Konkret: ein Kolonist auf einer Rolle, an der ein
            // Equipment Pack haengt, hatte dessen Inhalt nie in seinen Slots, obwohl jede Anzeige im Mod ihn
            // zeigte (die liest role.Slots, wo dieser Postfix laeuft). Ergebnis war bestellter Nachschub,
            // den niemand abholt. Der Fix sitzt bewusst HIER und nicht in der Zuweisung: so wirkt er auf
            // jedem Weg, ueber den eine Rolle an einen Kolonisten kommt - auch ueber CEs eigenes Dropdown -
            // und nicht nur auf unserem.
            // Kein Doppelzaehlen: RoleUtility.Merge backt Packs bewusst NICHT in die OwnSlots der Rolle ein,
            // die Pack-Slots stehen also noch nirgends in __result; GetExtraParentIdsWithAncestors entdoppelt
            // ausserdem gegen die eigenen Extra-Eltern des Kindes.
            List<int> extra = MultiParentUtility.GetExtraParentIdsWithAncestors(__instance);
            bool hasExtra = extra != null && extra.Count > 0;

            // A pawn's own ammo picks (AmmoAssignmentUtility/Dialog_ChangeAmmo) are meant to be the
            // pawn's OWN, complete, freely-editable ammo choice - not extras stacked alongside whatever
            // the shared Weapon Loadout template happened to bake in at generation time. As soon as this
            // object owns any ammo-type slot of its own, every OTHER inherited ammo slot (the template's
            // own default, or a sidearm's, pulled in via the parent chain or an extra-parent below) is
            // dropped - only this object's own ammo choices remain. Non-ammo slots (weapons, packs,
            // medicine, food, ...) are completely unaffected; this only ever touches ammo. A pawn can also
            // be explicitly cleared down to ZERO ammo (AmmoAssignmentUtility.IsAmmoCleared) - an anchor
            // with no ammo slots of its own is otherwise indistinguishable from "never customized", so
            // without this flag the template's default would silently reappear the moment the last
            // personal ammo type is removed.
            // Schleife statt LINQ+ToList, und IsAmmoCleared nur als ZWEITE Bedingung (2026-08-09).
            // Vorher baute jeder Aufruf eine Liste, die im Normalfall sofort
            // wieder verworfen wurde, und rief in jedem Fall IsAmmoCleared - das macht ein
            // Current.Game.GetComponent, also einen Komponenten-Scan, pro Aufruf. Beides passierte VOR dem
            // Early-Out darunter, der fast immer greift. Dieser Postfix haengt an Loadout.Slots, und das ist
            // kein reiner UI-Getter: ThinkNode_ConditionalHasLoadout, JobGiver_CheckReload und
            // Utility_HoldTracker rufen ihn aus CEs Think-Tree.
            // Nur die Reihenfolge und die Allokationen sind anders - die BEDINGUNG ist Zeichen fuer Zeichen
            // dieselbe wie vorher (irgendein eigener Ammo-Slot ODER explizit geleert).
            // LoadoutUtility.IsAmmoSlot statt nur IsAmmoDef(thingDef) (2026-08-14, erneut aufgetreten): ein
            // generischer Eigen-Slot ("Any ammo for X") IST eine eigene Munitionswahl. Dass er hier nicht
            // als solche zaehlte, war die eigentliche Ursache des Fehlers, den der Fix vom 13.08. auf der
            // Dialog-Seite nur halb erwischt hat - entschieden wird hier, nicht dort.
            // Fehlszenario: Haupt- und Zweitwaffe beide auf "Any ammo", einen Haken wieder weg. Danach
            // stand am Anker nur noch der generische Slot der anderen Waffe; hasOwnAmmoSlot war false, und
            // 'cleared' hatte der Dialog korrekt auf false gesetzt (es gibt ja eine eigene Wahl) - also
            // lief der Filter unten gar nicht, und die Vorgabe-Munition der Vorlage kam fuer BEIDE Waffen
            // zurueck, mit Haken neben dem noch gesetzten "Any ammo".
            bool hasOwnAmmoSlot = false;
            List<LoadoutSlot> own = __instance.OwnSlots;
            if (own != null)
                for (int i = 0; i < own.Count; i++)
                    if (IsRealAmmoSlot(own[i])) { hasOwnAmmoSlot = true; break; }
            bool hasOwnAmmo = hasOwnAmmoSlot || AmmoAssignmentUtility.IsAmmoCleared(__instance);

            if (!hasExtra && !hasOwnAmmo)
                return;

            List<LoadoutSlot> combined = new List<LoadoutSlot>(__result);
            if (hasExtra)
            {
                bool isRoot = activeGuard == null;
                if (isRoot) activeGuard = new HashSet<int>();
                HashSet<int> guard = activeGuard;
                // Mark the instance itself visited unconditionally (not gated on Add's return value) - a
                // legitimate multi-level chain (A extra-parents B, B extra-parents C, no cycle) still needs
                // to fully descend into B's own extras even though B was just marked visited by the caller a
                // moment ago. The actual cycle check is the Add(id) below, right before descending into a
                // NEW loadout - that's the one that stops a true cycle (…-> A -> B -> A) instead of hanging.
                guard.Add(__instance.UniqueID);
                try
                {
                    foreach (int id in extra) AppendFrom(id, combined, guard);
                }
                finally
                {
                    if (isRoot) activeGuard = null;
                }
            }
            if (hasOwnAmmo)
            {
                // Reference-equality against OwnSlots itself, NOT a def-membership check (2026-08-01,
                // live: a pawn's own ammo count showed doubled everywhere - "Assigned to:
                // Ryder x300" for a personal override of 150). The old check kept a slot if its DEF matched
                // one of the pawn's own ammo defs - but the INHERITED copy of that exact same def (from the
                // template's parent chain, still sitting in `combined` from CE's own native union) trivially
                // matches too, so both the inherited slot AND the real own slot survived the filter and got
                // summed together by every "Assigned to:" reader (they all do .Where(def-match).Sum(count),
                // which has no way to know two entries are the SAME logical slot). "Own ammo replaces
                // inherited ammo" has to mean "keep only slots that ARE literally in OwnSlots", not "keep
                // only slots whose def happens to also appear in OwnSlots" - those are very different checks
                // once an inherited slot shares a def with an owned one, which is exactly the common case
                // (personalizing ammo for the SAME weapon the template already listed a default for).
                HashSet<LoadoutSlot> ownSlotRefs = new HashSet<LoadoutSlot>(__instance.OwnSlots ?? new List<LoadoutSlot>());
                // Auch hier IsAmmoSlot statt IsAmmoDef(thingDef) (2026-08-14, derselbe Fehler): der
                // alte Test liess `s.thingDef == null` pauschal durch und damit AUCH einen geerbten
                // generischen Munitions-Platzhalter der Vorlage - der stand dann neben der eigenen Wahl des
                // Kolonisten, zweimal dieselbe Munition. "Eigene Munition ersetzt geerbte" muss fuer beide
                // Schreibweisen derselben Sache gelten, sonst haengt es davon ab, wie die Vorlage zufaellig
                // gebaut ist. Versorgungs-Generika aus Packs ("Any Medicine") sind KEINE Munition und
                // bleiben unangetastet - genau das trennt IsAmmoSlot.
                combined = combined.Where(s => !IsRealAmmoSlot(s) || ownSlotRefs.Contains(s)).ToList();
            }
            __result = combined;
        }

        // NUR echte Munition faellt unter die Eigene-Munition-Regel - keine Wurfwaffen (2026-08-30, die Ursache hinter DREI
        // Fehlerbildern: ein Kolonist holt die Stickbombs aus dem Regal nicht, eine Satchel Charge steht koloniweit auf
        // 'Assigned 0', 'Fetch gear now' nennt die Zweitwaffe nicht). CEs Granaten und Sprengsaetze sind AmmoThing,
        // also sagt IsAmmoSlot fuer den Stickbomb-Slot einer
        // Zweitwaffen-Vorlage 'Munition' - und sobald der Anker eigene Munition hat (immer, sobald eine Waffe Munition
        // braucht), flog jeder Granaten-Slot der Extra-Eltern hier heraus. CE sah den Slot nie, holte nie, zaehlte nie.
        // Eine Wurfwaffe ist ein Weapon-Def; echte Munition nicht.
        // Dieselbe Trennung wie LoadoutUtility.IsRealAmmoDef, nur fuer einen Slot (der auch generisch sein kann).
        private static bool IsRealAmmoSlot(LoadoutSlot s) =>
            LoadoutUtility.IsAmmoSlot(s) && (s.thingDef == null || LoadoutUtility.IsRealAmmoDef(s.thingDef));

        // Recursive on purpose (an extra parent's own .Slots goes through this same patched getter, so a
        // Role attached to another Role's extra-parent chain would resolve correctly) - the guard set passed
        // in is the SAME shared activeGuard the whole traversal uses, so a cycle anywhere in the chain is
        // actually caught here rather than only at whichever level happens to be the current top-level call.
        private static void AppendFrom(int id, List<LoadoutSlot> into, HashSet<int> guard)
        {
            if (!guard.Add(id)) return;
            Loadout lo = LoadoutManager.GetLoadoutById(id);
            // CE's own Slots getter never returns null (fresh list every call, confirmed above) and neither
            // does this same patch's own Postfix - but a THIRD mod's Harmony patch on the same getter could
            // still break that contract, so guard against it rather than trust it transitively forever.
            if (lo?.Slots == null) return;
            into.AddRange(lo.Slots);
        }
    }
}
