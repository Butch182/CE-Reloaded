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
    // "Fetch gear now" (2026-08-30): ein Knopf, der alle zwingt, ihre Ausruestung SOFORT abzuholen.
    //
    // Warum CE das nicht von selbst tut, dekompiliert nachgelesen (CombatExtended.JobGiver_UpdateLoadout):
    // der Abhol-Geber steht im Denkbaum mit Prioritaet 2,8 UNTER der Arbeit (5,5 / 9) und bekommt nur dann
    // Prioritaet 30, wenn seine Drossel (_throttle, 1800 Ticks = 30 s je Kolonist) abgelaufen ist. Der Denkbaum
    // wird ohnehin erst am Ende des laufenden Jobs befragt. Liefert der Geber einen Job, setzt er die Drossel
    // sofort wieder auf "abgelaufen" und holt so Stueck fuer Stueck weiter - liefert er KEINEN (noch nichts im
    // Lager, gerade erst zugewiesen), sperrt er 30 Sekunden. Nach einem Arm-my-Colony-Durchlauf heisst das:
    // erst den laufenden Job zu Ende, dann bis zu 30 s warten, dann holen. Es sieht aus, als taete sich nichts.
    //
    // Der Knopf macht genau zwei Dinge je Kolonist: die Drossel auf "abgelaufen" stellen (damit CEs eigener
    // Geber danach von selbst weiterholt, bis nichts mehr fehlt) und den ERSTEN Abhol-Job als Spielerbefehl
    // setzen - das unterbricht wie ein Rechtsklick-Befehl, aber keinen Job, den der Spieler auch sonst nicht
    // unterbrechen darf (Operation, Gefangenentransport); dort holt er, sobald der Job endet, weil die
    // Drossel offen bleibt. Kein eigener Harmony-Patch, kein eigener Job: CEs Job, CEs Auswahl, CEs Reihenfolge.
    public static class FetchGearNow
    {
        // Sicherheitsnetz (2026-08-30, Ziel: sie holen ALLES und nicht nur 1 Item):
        // solange ein Kolonist im Abholmodus ist, liefert CEs Geber Prioritaet 30 (Harmony-Prefix unten), egal
        // was seine Drossel sagt - bis CE selbst 'nichts mehr zu holen' meldet (TryGiveJob == null, Postfix).
        // Damit haengt 'alles' nicht an einem Detail in CEs Drossel-Logik. Grenzen: eingezogen, kampfunfaehig,
        // Schlafenszeit im Zeitplan oder Ruhebedarf unter 20 % beenden den Modus, ebenso 10000 Ticks (4 h
        // Spielzeit) - ein unerreichbarer Gegenstand darf niemanden fuer immer binden.
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

        // Vorschau fuer den Mouseover des Knopfs (2026-08-30: 'was breche ich jetzt ab, und was holt der
        // Pawn' - auch bei grossen Kolonien). Bewusst NICHT ueber CEs GetUpdateLoadoutJob (das sucht je Kolonist ueber
        // die Karte und liefert ohnehin nur den NAECHSTEN Gegenstand), sondern billig: je Slot des Loadouts 'gefordert
        // minus getragen', und ob davon etwas im Lager/am Boden liegt (beide Zaehler 1 s gecacht). Was nicht im Lager
        // liegt, wird als 'nichts da' genannt - das holt auch der Knopf nicht. Der laufende Job kommt aus RimWorlds
        // eigenem Bericht (GetReport). Text nur unter der Maus gebaut und 0,5 s gemerkt; hoechstens MaxPawnLines
        // Kolonisten mit je hoechstens MaxItemsPerPawn Posten, der Rest als '... und N weitere'.
        // ZEILEN-Budget statt Kolonisten-Budget (2026-08-30, grosse Kolonie: zwoelf Kolonisten zu je zwei
        // bis drei Zeilen waren bei 1440 Bildpunkten und Skalierung 1.25 schon hoeher als der Schirm). Ein Kolonist kostet
        // eine Zeile (uebersprungen) oder zwei (Taetigkeit + Posten).
        private const int MaxLines = 20, MaxItemsPerPawn = 4;
        private static string previewCache;
        private static float previewAt = -99f;

        public static string PreviewTooltip()
        {
            // 0,5 -> 2 Sekunden (2026-08-30): der Kasten bleibt jetzt offen, aber sein
            // Inhalt sprang zweimal je Sekunde um, weil jede Zeile die laufende Taetigkeit nennt. Zwei Sekunden sind
            // langsam genug zum Lesen und schnell genug, um noch zu zeigen, was gerade passiert.
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
                // Ohne die Vorrede "nothing to fetch right now -" (2026-08-30: zu sperrig).
                // Sie sagte nichts, was nicht schon dastand: jeder Posten traegt sein eigenes "(none in stock)", und die
                // rote Farbe der Zeile sagt den Rest.
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

        // 'gefordert minus getragen' je Slot; Munition, Waffen, Packs, Generics - alles, was die Slots-Vereinigung
        // nennt (Anker + Vorlage + Packs + Zweitwaffen). fetchable = Posten, von denen etwas im Lager oder am Boden
        // liegt UND die der Kolonist ueberhaupt noch tragen kann. wontFit = im Lager, aber kein Platz mehr.
        //
        // KAPAZITAET ZAEHLT MIT (2026-09-01: bei vollem Bulk tat 'Fetch gear now' nichts mehr):
        // CEs Job-Geber prueft VOR jedem Aufheben CanFitInInventory und liefert
        // bei vollem Bulk/Gewicht schlicht KEINEN Job (dekompiliert, JobGiver_UpdateLoadout.GetPrioritySlot) -
        // kein Langsamer-Werden, einfach nichts. Diese Vorschau versprach genau solchen Kolonisten weiter
        // 'fetches: ...'. Jetzt sagt sie es je Posten ehrlich: '(won't fit - carrying limit reached)', mit
        // derselben Prueffunktion, die CE selbst benutzt. Die Bereitschaftsanzeige kannte den Zustand laengst
        // ('capacity blocked' -> open gear, CombatReadinessAlerts) - die Fetch-Seite war die Luecke.
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
                    // Dieselbe Frage, die CEs Geber stellt - passt EIN Stueck davon noch?
                    fits = capInv == null || capInv.CanFitInInventory(s.thingDef, out int _);
                }
                else
                {
                    have = LoadoutUtility.PawnCarriedGenericCount(p, s.genericDef);
                    inStock = LoadoutUtility.GenericStockCount(s.genericDef) > 0;
                    // Generische Slots ('Any Medicine') haben kein einzelnes Def zum Wiegen - dort entscheidet
                    // der Pauschalzustand 'Kolonist ist voll' (dieselbe Schwelle wie die Bereitschaftsanzeige).
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

        // Dieselbe Schwelle wie CombatReadinessAlerts' 'capacity blocked' (dort seit 2026-08-13): praktisch
        // randvoll in Bulk ODER Gewicht - mehr nimmt CEs Geber nicht mehr an.
        private static bool PawnFull(CompInventory inv)
            => inv != null && (inv.currentBulk >= inv.capacityBulk - 0.01f || inv.currentWeight >= inv.capacityWeight - 0.01f);

        // Wuerde holen, kann aber nicht mehr tragen - fuer die ehrlichen Knopf-Meldungen.
        public static bool CapacityBlocked(Pawn p)
        {
            Loadout lo = p?.GetLoadout();
            if (lo == null || lo.defaultLoadout) return false;
            MissingItems(p, lo, out _, out int wontFit);
            return wontFit > 0;
        }

        // CEs Drossel ist privat; ueber Harmony gelesen, mit Rueckfall: fehlt das Feld nach einem CE-Update,
        // bleibt der Befehl fuer den ersten Gegenstand erhalten, nur das Weiterholen liegt dann wieder bei CE.
        private static readonly System.Reflection.FieldInfo throttleField = AccessTools.Field(typeof(JobGiver_UpdateLoadout), "_throttle");

        // Wer darf losgeschickt werden: gespawnte, freie Kolonisten, die stehen und nicht eingezogen sind.
        // Schlafende und Kampfunfaehige bleiben liegen, Zusammenbrueche werden nicht unterbrochen, Karawanen
        // haben kein Lager zum Holen.
        public static bool Eligible(Pawn p)
        {
            return p != null && p.Spawned && !p.Dead && !p.Downed && !p.Drafted && !p.InMentalState
                && !p.InBed() && p.jobs != null && p.RaceProps.Humanlike && !p.IsPrisoner
                && p.health != null && p.health.capacities.CapableOf(PawnCapacityDefOf.Moving);
        }

        // Liefert, wie viele Kolonisten einen Abhol-Job bekommen haben. `withLoadout` zaehlt alle geeigneten
        // mit einer Beladung - die Differenz sind die, bei denen CE nichts mehr zu holen sieht.
        public static int Trigger(out int withLoadout, out int skipped, out int blockedFull)
        {
            int started = 0;
            withLoadout = 0; skipped = 0; blockedFull = 0;
            Dictionary<int, int> throttle = throttleField?.GetValue(null) as Dictionary<int, int>;
            foreach (Pawn p in LoadoutUtility.AllColonyPawns())
            {
                Loadout lo = p.GetLoadout();
                if (lo == null || lo.defaultLoadout) continue;
                // Uebersprungene mitzaehlen (2026-08-30: '7 checked' neben einer Vorschau mit zehn Namen - die
                // drei im Bett bzw. im Zusammenbruch fehlten in der Zahl, ohne dass die Meldung es sagte).
                if (!Eligible(p)) { skipped++; continue; }
                withLoadout++;
                if (TryStart(p, lo, throttle)) started++;
                // CE gab keinen Job, obwohl Holbares im Lager laege - er ist VOLL (2026-09-01).
                // Ohne diese Zahl las sich die Meldung wie 'alles erledigt', waehrend der Kolonist mit
                // leerer Beladung und randvollem Rucksack stehen bleibt.
                else if (CapacityBlocked(p)) blockedFull++;
            }
            return started;
        }

        // EIN Kolonist, aus dem Klick-Menue der Bereitschaftsanzeige (2026-08-30): 'liegt im Lager, nicht abgeholt'
        // und 'zugewiesen, traegt nichts' fuehren jetzt hierher statt in seine Ausruestung.
        public static bool TriggerFor(Pawn p, bool quiet = false)
        {
            bool ok = false;
            if (Eligible(p))
            {
                Loadout lo = p.GetLoadout();
                if (lo != null && !lo.defaultLoadout)
                    ok = TryStart(p, lo, throttleField?.GetValue(null) as Dictionary<int, int>);
            }
            if (quiet) return ok;   // Sammelgriff aus dem Bereitschafts-Menue: EINE Meldung fuer alle, nicht 24
            if (ok) Messages.Message("CER_LoadoutUtility_FetchGearMsgOne".Translate(p.LabelShortCap), p, MessageTypeDefOf.TaskCompletion, false);
            // 'Nichts zu holen' und 'kann nichts mehr TRAGEN' sind zwei verschiedene Antworten (2026-09-01).
            else if (CapacityBlocked(p)) Messages.Message("CER_LoadoutUtility_FetchGearMsgOneFull".Translate(p.LabelShortCap), p, MessageTypeDefOf.RejectInput, false);
            // Und eine DRITTE (2026-09-03): ein Menueeintrag versprach
            // "fetch now" und die Antwort darauf lautete "Isolde: Nothing to fetch right now"). Der
            // Eintrag entsteht aus UNSERER Bestandsrechnung (AnythingFetchable: liegt etwas Passendes
            // erreichbar herum), der Klick aus CEs Job-Geber - und der lehnt aus Gruenden ab, die unsere
            // Rechnung nicht kennt (der Stapel ist von jemand anderem reserviert, der Kolonist kommt
            // gerade nicht hin, ein anderer Auftrag laeuft). Beide Rechnungen zusammenzulegen geht nicht:
            // CEs Job-Geber LASST den Kolonisten unterwegs Ueberschuss fallen, ihn nur zum Fragen
            // aufzurufen waere eine Nebenwirkung. Also sagt die Meldung wenigstens die Wahrheit -
            // "gerade nicht" statt "gar nichts", denn "gar nichts" schickt den Spieler in den Nachschub,
            // wo er nichts zu tun findet.
            else if (AnythingFetchable(p)) Messages.Message("CER_LoadoutUtility_FetchGearMsgOneBusy".Translate(p.LabelShortCap), p, MessageTypeDefOf.RejectInput, false);
            else Messages.Message("CER_LoadoutUtility_FetchGearMsgOneNone".Translate(p.LabelShortCap), p, MessageTypeDefOf.RejectInput, false);
            return ok;
        }

        // Fuer das Bereitschafts-Menue (2026-08-30: 'Cloud (assigned but empty-handed) - fetch now' antwortete
        // 'Nothing to fetch right now' - sein Posten lag nirgends). Liegt fuer diesen Kolonisten ueberhaupt etwas Holbares?
        public static void TriggerAll(List<Pawn> pawns)
        {
            // Sammelgriff aus dem Bereitschafts-Menue: alle auf einmal, EINE Meldung.
            int ok = 0;
            foreach (Pawn p in pawns) if (TriggerFor(p, quiet: true)) ok++;
            // Einzahl auch hier - zweite Aufrufstelle desselben Schluessels (2026-09-04, Regel-7-Grep).
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
            // "abgelaufen" = dieselbe Marke, die CE selbst setzt, wenn es weiterholen will (TicksGame - 1801).
            if (throttle != null) throttle[p.thingIDNumber] = GenTicks.TicksGame - 1801;
            forcedSince[p.thingIDNumber] = GenTicks.TicksGame;
            if (!p.jobs.TryTakeOrderedJob(job, JobTag.Misc))
            {
                // Laufender Job nicht unterbrechbar - der Kolonist holt, sobald er fertig ist (Drossel offen).
                JobMaker.ReturnToPool(job);
            }
            return true;
        }

        // Die Diagnosezeile [CER DIAG fetch] (2026-08-30) ist wieder raus: sie hat gezeigt, dass CE fuer Munition alles
        // gruen sah (die Fehlmenge war unsere - das Magazin, siehe LoadoutUtility.MagazineAmmoCount) und dass der Stickbomb-
        // Slot in lo.Slots gar nicht ankam (Patch_Loadout_MultiParentSlots.IsRealAmmoSlot).
    }

    // Waehrend des Abholmodus gewinnt der Abhol-Geber jede Jobwahl (30 schlaegt Arbeit 5,5/9 und Freizeit).
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

    // CE sagt 'nichts mehr zu holen' (null) - dann ist der Abholmodus vorbei und der Kolonist geht zurueck an
    // seine Arbeit. Ein Job (nicht null) laesst den Modus stehen: das naechste Stueck folgt am Jobende.
    [HarmonyPatch(typeof(JobGiver_UpdateLoadout), "TryGiveJob")]
    public static class Patch_JobGiver_UpdateLoadout_TryGiveJob_FetchNow
    {
        public static void Postfix(Pawn pawn, Job __result)
        {
            if (__result == null) FetchGearNow.Release(pawn);
        }
    }
}
