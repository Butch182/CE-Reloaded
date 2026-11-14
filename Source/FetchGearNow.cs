using System;
using System.Collections.Generic;
using System.Text;
using UnityEngine;
using CombatExtended;
using HarmonyLib;
using RimWorld;
using Verse;
using Verse.AI;

namespace CEReloaded
{
    // "Fetch gear now": a button that makes everyone pick up their equipment IMMEDIATELY.
    //
    // Why CE doesn't do this by itself, verified in decompiled code (CombatExtended.JobGiver_UpdateLoadout):
    // the pickup giver sits in the think tree at priority 2.8, BELOW work (5.5 / 9), and only gets
    // priority 30 when its throttle (_throttle, 1800 ticks = 30 s per colonist) has expired. The think tree
    // is only consulted at the end of the current job anyway. If the giver returns a job, it immediately
    // resets the throttle to "expired" and so keeps fetching piece by piece - if it returns NONE (nothing in
    // storage yet, just assigned), it locks for 30 seconds. After an Arm my Colony run that means: finish
    // the current job first, then wait up to 30 s, then fetch. It looks as if nothing is happening.
    //
    // The button does exactly two things per colonist: set the throttle to "expired" (so CE's own giver
    // then keeps fetching by itself until nothing is missing) and issue the FIRST pickup job as a player
    // order - that interrupts like a right-click order, but no job the player could not otherwise interrupt
    // either (surgery, prisoner transport); there the colonist fetches as soon as the job ends, because the
    // throttle stays open. No Harmony patch of its own, no job of its own: CE's job, CE's choice, CE's order.
    public static class FetchGearNow
    {
        // Safety net (goal: they fetch EVERYTHING and not just 1 item):
        // while a colonist is in fetch mode, CE's giver returns priority 30 (Harmony prefix below), no matter
        // what its throttle says - until CE itself reports 'nothing left to fetch' (TryGiveJob == null, postfix).
        // That way 'everything' does not depend on a detail of CE's throttle logic. Limits: drafted, downed,
        // sleep time in the schedule or rest need below 20 % end the mode, as do 10000 ticks (4 h of
        // game time) - an unreachable item must not bind anyone forever.
        private const int ForcedMaxTicks = 10000;
        private static readonly Dictionary<int, int> forcedSince = new Dictionary<int, int>();
        private static Game forcedGame;

        public static bool IsForced(Pawn p)
        {
            if (p == null) return false;
            if (!ReferenceEquals(forcedGame, Current.Game)) { forcedSince.Clear(); forcedGame = Current.Game; }
            if (!forcedSince.TryGetValue(p.thingIDNumber, out int since)) return false;
            bool stop = p.Dead || p.Downed || p.Drafted || !p.Spawned || p.InMentalState
                || GenTicks.TicksGame - since > ForcedMaxTicks
                || (p.timetable != null && p.timetable.CurrentAssignment == TimeAssignmentDefOf.Sleep)
                || (p.needs?.rest != null && p.needs.rest.CurLevel < 0.2f);
            if (stop) { forcedSince.Remove(p.thingIDNumber); return false; }
            return true;
        }

        public static void Release(Pawn p) { if (p != null) forcedSince.Remove(p.thingIDNumber); }

        // Preview for the button's tooltip ('what am I interrupting now, and what will the pawn fetch' -
        // also for large colonies). Deliberately NOT via CE's GetUpdateLoadoutJob (that searches the map per
        // colonist and only returns the NEXT item anyway), but cheaply: per loadout slot 'required minus
        // carried', and whether any of it lies in storage/on the ground (both counters cached for 1 s). What is
        // not in storage is named as 'nothing there' - the button won't fetch that either. The current job comes
        // from RimWorld's own report (GetReport). Text only built under the mouse and kept for 0.5 s; at most
        // MaxPawnLines colonists with at most MaxItemsPerPawn items each, the rest as '... and N more'.
        // LINE budget instead of colonist budget (large colony: twelve colonists at two to three lines each
        // were already taller than the screen at 1440 pixels and 1.25 scaling). A colonist costs
        // one line (skipped) or two (activity + items).
        private const int MaxLines = 20, MaxItemsPerPawn = 4;
        private static string previewCache;
        private static float previewAt = -99f;

        public static string PreviewTooltip()
        {
            // 0.5 -> 2 seconds: the box now stays open, but its content
            // flipped twice per second because every line names the current activity. Two seconds are
            // slow enough to read and fast enough to still show what is happening.
            if (previewCache != null && Time.realtimeSinceStartup - previewAt < 2f) return previewCache;
            previewAt = Time.realtimeSinceStartup;
            StringBuilder sb = new StringBuilder();
            sb.Append("CER_LoadoutUtility_FetchGearPreviewHeader".Translate().ToString());
            int linesUsed = 0, more = 0, allSet = 0, withLoadout = 0;
            foreach (Pawn p in LoadoutUtility.AllColonyPawns())
            {
                Loadout lo = p.GetLoadout();
                if (lo == null || lo.defaultLoadout) continue;
                withLoadout++;
                string skip = SkipReason(p);
                List<string> items = MissingItems(p, lo, out int fetchable, out _);
                if (skip == null && items.Count == 0) { allSet++; continue; }
                int cost = skip != null ? 1 : 2;
                if (linesUsed + cost > MaxLines) { more++; continue; }
                linesUsed += cost;
                sb.Append("\n\n").Append(p.LabelShortCap);
                if (skip != null)
                {
                    sb.Append(" - <color=#9A9A9A>").Append("CER_LoadoutUtility_FetchGearPreviewSkipped".Translate(skip).ToString()).Append("</color>");
                    continue;
                }
                string now = CurrentJobText(p, out bool interruptible);
                sb.Append(" - <color=#9A9A9A>").Append("CER_LoadoutUtility_FetchGearPreviewNow".Translate(now).ToString());
                if (!interruptible) sb.Append("CER_LoadoutUtility_FetchGearPreviewFinishesFirst".Translate().ToString());
                sb.Append("</color>\n    ");
                // Without the preamble "nothing to fetch right now -" (too bulky).
                // It said nothing that wasn't already there: every item carries its own "(none in stock)", and
                // the line's red color says the rest.
                if (fetchable == 0)
                    sb.Append("<color=#E67E5E>").Append(string.Join(", ", items)).Append("</color>");
                else
                    sb.Append("CER_LoadoutUtility_FetchGearPreviewFetches".Translate(string.Join(", ", items)).ToString());
            }
            if (withLoadout == 0) sb.Append("\n\n").Append("CER_LoadoutUtility_FetchGearPreviewNoLoadouts".Translate().ToString());
            if (more > 0) sb.Append("\n\n").Append("CER_LoadoutUtility_FetchGearPreviewMore".Translate(more.ToString()).ToString());
            if (allSet > 0) sb.Append("\n\n<color=#9A9A9A>").Append("CER_LoadoutUtility_FetchGearPreviewAllSet".Translate(allSet.ToString()).ToString()).Append("</color>");
            sb.Append("\n\n").Append("CER_LoadoutUtility_FetchGearButtonTooltip".Translate().ToString());
            previewCache = sb.ToString();
            return previewCache;
        }

        private static string SkipReason(Pawn p)
        {
            if (p.Dead || p.Downed) return "CER_LoadoutUtility_FetchGearSkipDowned".Translate();
            if (p.Drafted) return "CER_LoadoutUtility_FetchGearSkipDrafted".Translate();
            if (p.InMentalState) return "CER_LoadoutUtility_FetchGearSkipMental".Translate();
            if (!p.Spawned) return "CER_LoadoutUtility_FetchGearSkipAway".Translate();
            if (p.InBed()) return "CER_LoadoutUtility_FetchGearSkipInBed".Translate();
            if (!Eligible(p)) return "CER_LoadoutUtility_FetchGearSkipDowned".Translate();
            return null;
        }

        private static string CurrentJobText(Pawn p, out bool interruptible)
        {
            interruptible = true;
            Job job = p.CurJob;
            if (job == null) return "CER_LoadoutUtility_FetchGearPreviewIdle".Translate();
            interruptible = job.def == null || job.def.playerInterruptible;
            string report = null;
            try { report = p.jobs?.curDriver?.GetReport(); } catch { report = null; }
            if (string.IsNullOrEmpty(report)) report = job.def?.reportString ?? job.def?.label ?? "?";
            return report.CapitalizeFirst();
        }

        // 'required minus carried' per slot; ammo, weapons, packs, generics - everything the Slots union
        // names (anchor + template + packs + sidearms). fetchable = items of which something lies in storage
        // or on the ground AND which the colonist can still carry at all. wontFit = in storage, but no room left.
        //
        // CAPACITY COUNTS TOO (with full bulk 'Fetch gear now' used to do nothing):
        // CE's job giver checks CanFitInInventory BEFORE every pickup and simply returns NO job
        // at full bulk/weight (decompiled, JobGiver_UpdateLoadout.GetPrioritySlot) -
        // no slowing down, just nothing. This preview kept promising such colonists
        // 'fetches: ...'. Now it says so honestly per item: '(won't fit - carrying limit reached)', using
        // the same check function CE itself uses. The readiness display already knew this state
        // ('capacity blocked' -> open gear, CombatReadinessAlerts) - the fetch side was the gap.
        private static List<string> MissingItems(Pawn p, Loadout lo, out int fetchable, out int wontFit)
        {
            var lines = new List<string>();
            fetchable = 0;
            wontFit = 0;
            int extra = 0;
            List<LoadoutSlot> slots = lo.Slots;
            if (slots == null) return lines;
            CompInventory capInv = p.TryGetComp<CompInventory>();
            bool pawnFull = PawnFull(capInv);
            foreach (LoadoutSlot s in slots)
            {
                if (s.count <= 0) continue;
                Def d = (Def)s.thingDef ?? s.genericDef;
                if (d == null) continue;
                int have;
                bool inStock;
                bool fits;
                if (s.thingDef != null)
                {
                    have = LoadoutUtility.PawnCarriedCount(p, s.thingDef);
                    if (p.equipment?.Primary != null && p.equipment.Primary.def == s.thingDef) have += 1;
                    inStock = LoadoutUtility.StoredStockCount(s.thingDef) + LoadoutUtility.GroundStockCount(s.thingDef) > 0;
                    // The same question CE's giver asks - does ONE piece of it still fit?
                    fits = capInv == null || capInv.CanFitInInventory(s.thingDef, out int _);
                }
                else
                {
                    have = LoadoutUtility.PawnCarriedGenericCount(p, s.genericDef);
                    inStock = LoadoutUtility.GenericStockCount(s.genericDef) > 0;
                    // Generic slots ('Any Medicine') have no single def to weigh - there the blanket state
                    // 'colonist is full' decides (same threshold as the readiness display).
                    fits = !pawnFull;
                }
                int missing = s.count - have;
                if (missing <= 0) continue;
                if (inStock && fits) fetchable++;
                else if (inStock) wontFit++;
                if (lines.Count >= MaxItemsPerPawn) { extra++; continue; }
                string label = missing + "x " + d.LabelCap.ToString();
                lines.Add(!inStock ? "CER_LoadoutUtility_FetchGearPreviewNotInStock".Translate(label).ToString()
                    : (!fits ? "CER_LoadoutUtility_FetchGearPreviewWontFit".Translate(label).ToString() : label));
            }
            if (extra > 0) lines.Add("CER_LoadoutUtility_FetchGearPreviewMoreItems".Translate(extra.ToString()).ToString());
            return lines;
        }

        // Same threshold as CombatReadinessAlerts' 'capacity blocked': practically
        // full in bulk OR weight - CE's giver accepts nothing more beyond that.
        private static bool PawnFull(CompInventory inv)
            => inv != null && (inv.currentBulk >= inv.capacityBulk - 0.01f || inv.currentWeight >= inv.capacityWeight - 0.01f);

        // Would fetch, but can't carry any more - for the honest button messages.
        public static bool CapacityBlocked(Pawn p)
        {
            Loadout lo = p?.GetLoadout();
            if (lo == null || lo.defaultLoadout) return false;
            MissingItems(p, lo, out _, out int wontFit);
            return wontFit > 0;
        }

        // CE's throttle is private; read via Harmony, with a fallback: if the field is missing after a CE update,
        // the order for the first item still works, only the continued fetching is left to CE again.
        private static readonly System.Reflection.FieldInfo throttleField = AccessTools.Field(typeof(JobGiver_UpdateLoadout), "_throttle");

        // Who may be sent: spawned, free colonists who are standing and not drafted.
        // Sleeping and downed colonists are left alone, mental breaks are not interrupted, caravans
        // have no storage to fetch from.
        public static bool Eligible(Pawn p)
        {
            return p != null && p.Spawned && !p.Dead && !p.Downed && !p.Drafted && !p.InMentalState
                && !p.InBed() && p.jobs != null && p.RaceProps.Humanlike && !p.IsPrisoner
                && p.health != null && p.health.capacities.CapableOf(PawnCapacityDefOf.Moving);
        }

        // Returns how many colonists got a pickup job. `withLoadout` counts all eligible ones
        // with a loadout - the difference are those for whom CE sees nothing left to fetch.
        public static int Trigger(out int withLoadout, out int skipped, out int blockedFull)
        {
            int started = 0;
            withLoadout = 0; skipped = 0; blockedFull = 0;
            Dictionary<int, int> throttle = throttleField?.GetValue(null) as Dictionary<int, int>;
            foreach (Pawn p in LoadoutUtility.AllColonyPawns())
            {
                Loadout lo = p.GetLoadout();
                if (lo == null || lo.defaultLoadout) continue;
                // Count skipped ones too ('7 checked' next to a preview with ten names - the three in bed
                // or in a mental break were missing from the number without the message saying so).
                if (!Eligible(p)) { skipped++; continue; }
                withLoadout++;
                if (TryStart(p, lo, throttle)) started++;
                // CE gave no job although something fetchable lies in storage - the colonist is FULL.
                // Without this number the message read like 'all done', while the colonist stands
                // there with an empty loadout and a stuffed backpack.
                else if (CapacityBlocked(p)) blockedFull++;
            }
            return started;
        }

        // ONE colonist, from the readiness display's click menu: 'in storage, not picked up'
        // and 'assigned, carrying nothing' now lead here instead of to their gear.
        public static bool TriggerFor(Pawn p, bool quiet = false)
        {
            bool ok = false;
            if (Eligible(p))
            {
                Loadout lo = p.GetLoadout();
                if (lo != null && !lo.defaultLoadout)
                    ok = TryStart(p, lo, throttleField?.GetValue(null) as Dictionary<int, int>);
            }
            if (quiet) return ok;   // bulk action from the readiness menu: ONE message for all, not 24
            if (ok) Messages.Message("CER_LoadoutUtility_FetchGearMsgOne".Translate(p.LabelShortCap), p, MessageTypeDefOf.TaskCompletion, false);
            // 'Nothing to fetch' and 'can't CARRY any more' are two different answers.
            else if (CapacityBlocked(p)) Messages.Message("CER_LoadoutUtility_FetchGearMsgOneFull".Translate(p.LabelShortCap), p, MessageTypeDefOf.RejectInput, false);
            // And a THIRD: a menu entry promised "fetch now" and the answer was "Nothing to fetch right
            // now". The
            // entry comes from OUR stock calculation (AnythingFetchable: something suitable lies
            // around reachable), the click from CE's job giver - and that one declines for reasons our
            // calculation doesn't know (the stack is reserved by someone else, the colonist can't
            // reach it right now, another order is running). Merging both calculations is not possible:
            // CE's job giver makes the colonist DROP surplus along the way, so calling it just to ask
            // would be a side effect. So the message at least tells the truth -
            // "not right now" instead of "nothing at all", because "nothing at all" sends the player to
            // the supply window, where there is nothing to do.
            else if (AnythingFetchable(p)) Messages.Message("CER_LoadoutUtility_FetchGearMsgOneBusy".Translate(p.LabelShortCap), p, MessageTypeDefOf.RejectInput, false);
            else Messages.Message("CER_LoadoutUtility_FetchGearMsgOneNone".Translate(p.LabelShortCap), p, MessageTypeDefOf.RejectInput, false);
            return ok;
        }

        // For the readiness menu (an 'assigned but empty-handed - fetch now' entry answered 'Nothing to
        // fetch right now' because the item was nowhere). Is there anything fetchable for this colonist at all?
        public static void TriggerAll(List<Pawn> pawns)
        {
            // Bulk action from the readiness menu: everyone at once, ONE message.
            int ok = 0;
            foreach (Pawn p in pawns) if (TriggerFor(p, quiet: true)) ok++;
            // Singular here too - second call site of the same key.
            if (ok == 1) Messages.Message("CER_LoadoutUtility_FetchGearMsgSentOne".Translate(), MessageTypeDefOf.TaskCompletion, false);
            else if (ok > 0) Messages.Message("CER_LoadoutUtility_FetchGearMsgSent".Translate(ok.ToString()), MessageTypeDefOf.TaskCompletion, false);
            else Messages.Message("CER_LoadoutUtility_FetchGearMsgNone".Translate(pawns.Count.ToString(), "0"), MessageTypeDefOf.RejectInput, false);
        }

        public static bool AnythingFetchable(Pawn p)
        {
            Loadout lo = p?.GetLoadout();
            if (lo == null || lo.defaultLoadout) return false;
            MissingItems(p, lo, out int fetchable, out _);
            return fetchable > 0;
        }

        private static bool TryStart(Pawn p, Loadout lo, Dictionary<int, int> throttle)
        {
            Job job = JobGiver_UpdateLoadout.GetUpdateLoadoutJob(p);
            if (job == null) return false;
            // "expired" = the same mark CE itself sets when it wants to keep fetching (TicksGame - 1801).
            if (throttle != null) throttle[p.thingIDNumber] = GenTicks.TicksGame - 1801;
            forcedSince[p.thingIDNumber] = GenTicks.TicksGame;
            if (!p.jobs.TryTakeOrderedJob(job, JobTag.Misc))
            {
                // Current job not interruptible - the colonist fetches as soon as it is done (throttle open).
                JobMaker.ReturnToPool(job);
            }
            return true;
        }

        // The diagnostic line [CER DIAG fetch] is gone again: it showed that CE saw everything green for ammo
        // (the shortfall was ours - the magazine, see LoadoutUtility.MagazineAmmoCount) and that the stickbomb
        // slot never arrived in lo.Slots at all (Patch_Loadout_MultiParentSlots.IsRealAmmoSlot).
    }

    // During fetch mode the pickup giver wins every job choice (30 beats work 5.5/9 and recreation).
    [HarmonyPatch(typeof(JobGiver_UpdateLoadout), "GetPriority")]
    public static class Patch_JobGiver_UpdateLoadout_GetPriority_FetchNow
    {
        public static bool Prefix(Pawn pawn, ref float __result)
        {
            if (!FetchGearNow.IsForced(pawn)) return true;
            __result = 30f;
            return false;
        }
    }

    // CE says 'nothing left to fetch' (null) - then fetch mode is over and the colonist returns to
    // work. A job (not null) keeps the mode: the next piece follows at the end of the job.
    [HarmonyPatch(typeof(JobGiver_UpdateLoadout), "TryGiveJob")]
    public static class Patch_JobGiver_UpdateLoadout_TryGiveJob_FetchNow
    {
        public static void Postfix(Pawn pawn, Job __result)
        {
            if (__result == null) FetchGearNow.Release(pawn);
        }
    }
}
