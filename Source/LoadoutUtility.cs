using System;
using System.Collections.Generic;
using System.Linq;
using RimWorld;
using Verse;
using CombatExtended;
using UnityEngine;

namespace CEReloaded
{
    [Flags]
    public enum SpecialAbility
    {
        None = 0,
        EMP = 1,
        Flame = 2,
        Tox = 4,
        Smoke = 8,
        Stun = 16,
        Concussive = 32,
        Flash = 64,
        ArmorPiercing = 128,
        Fragment = 256,
        // Primary Bomb/BombSuper damage - a real projectile-radius explosion (2026-07-31, closes the gap
        // found auditing every damageDef CE actually uses: ~79 files use "Bomb" as their PRIMARY damage and
        // got no icon at all before this).
        Explosive = 512,
        // Bomb_Secondary specifically - the small explosive "HE kicker" riding on an otherwise-kinetic AP
        // round (e.g. Bullet_762x51mmNATO_HE, label "7.62mm NATO bullet (AP-HE)"), applied to the SAME
        // target already hit, not a radius blast. Deliberately a separate flag/icon from Explosive - it was
        // wrongly folded into Concussive before this audit, and mechanically it is neither a real explosion
        // nor blunt suppression.
        ExplosiveSecondary = 1024
    }

    /// <summary>Role buckets used by auto-assign to keep a squad's weapon mix balanced.</summary>
    public enum WeaponGroup { Rifle, Marksman, SupportMG, Launcher, CQC, Archaic, Melee, Other }

    // How a pawn stands with respect to one item right now - equipped beats sitting in a stockpile
    // beats merely being queued for production beats having none of the above. Shared across every
    // dialog that shows a fulfillment square (Assign Loadouts, Change Ammo, ...) so the color/wording
    // convention stays identical everywhere.
    public enum FulfillStatus { Equipped, Stored, InProduction, Missing }

    // Loads its own static Texture2D fields (e.g. tModeSimple) - see Dialog_AssignLoadouts' own identical
    // comment for why this attribute is required (2026-08-01).
    [StaticConstructorOnStartup]
    public static class LoadoutUtility
    {
        public static Color FulfillColor(FulfillStatus s)
        {
            switch (s)
            {
                case FulfillStatus.Equipped: return new Color(0.4f, 0.85f, 0.4f);
                case FulfillStatus.Stored: return new Color(0.9f, 0.85f, 0.3f);
                case FulfillStatus.InProduction: return new Color(0.95f, 0.6f, 0.2f);
                default: return new Color(0.9f, 0.35f, 0.35f);
            }
        }

        public static string FulfillWord(FulfillStatus s)
        {
            switch (s)
            {
                case FulfillStatus.Equipped: return "CER_LoadoutUtility_WordEquipped".Translate();
                case FulfillStatus.Stored: return "CER_LoadoutUtility_WordInStorage".Translate();
                case FulfillStatus.InProduction: return "CER_LoadoutUtility_WordInProduction".Translate();
                default: return "CER_LoadoutUtility_WordMissing".Translate();
            }
        }

        // Same word as FulfillWord, wrapped in the exact hex each status renders as in the shared
        // AvailabilitySegments bar (green/yellow/beige/red) - so a tooltip listing several items' statuses
        // reads at a glance instead of needing to cross-reference the dot color separately (2026-07-30,
        // each status word in the tooltip is written in its own color).
        public static string FulfillWordColored(FulfillStatus s)
        {
            string hex;
            switch (s)
            {
                case FulfillStatus.Equipped: hex = "#66D966"; break;
                case FulfillStatus.Stored: hex = "#E6D94D"; break;
                case FulfillStatus.InProduction: hex = "#C7AD7A"; break;
                default: hex = "#E65959"; break;
            }
            return "<color=" + hex + ">" + FulfillWord(s) + "</color>";
        }

        // ---- Unified "availability" bar (2026-07-30 harmonization pass) ---------------------------------
        // One shared data shape + one shared drawing routine, in two render modes: a horizontal bar with
        // per-segment numbers (the look Weapon Loadouts' own "Owned" column already had) and a compact
        // vertical strip of colors only (the look Assign Loadouts' per-cell status dots already had).
        // Replaces the previously separate/ad-hoc implementations across Dialog_SupplyLoadouts,
        // Dialog_GenerateLoadouts (Owned + Available columns) and Dialog_AssignLoadouts (fulfillment dots).
        //
        // Segment order/meaning (front to back):
        //   green  - equippedByPawn: THIS pawn (if one was asked about) has it right now. Deliberately
        //            never used for "merely exists somewhere" - a player glancing at a pawn-specific bar
        //            and seeing green must be able to trust that means the PAWN has it, not just the colony.
        //   yellow - available: free stock (storage or ground), regardless of pawn context. Can appear
        //            ALONGSIDE green (pawn already has one, and more sit free in storage).
        //   light blue  - assignedInStore: reserved by an assignment, still sitting in storage.
        //   deep blue   - assignedCarried: reserved by an assignment AND carried - by someone OTHER than
        //            the pawn in question, if any (that pawn's own copy is `equippedByPawn` instead).
        //   grey   - carriedUnassigned: carried by someone, not tied to any assignment at all.
        //   beige  - inProduction: queued for crafting, doesn't physically exist yet.
        //   red/grey - missing: shortfall against a caller-supplied need (red if a specific pawn's need,
        //            generic grey for a colony-wide need with no one pawn to blame). Omitted entirely
        //            (0) when no need was supplied - a bare ownership overview has nothing to be "missing".
        public struct AvailabilitySegments
        {
            public int equippedByPawn, available, assignedInStore, assignedCarried, carriedUnassigned, inProduction, missing;
            public bool missingIsPawnSpecific;
            // Der Gegenstand, ueber den diese Aufstellung spricht (2026-08-30): die koloniweite Zeile 'Missing' rechnet
            // damit ihre EIGENE Zahl (ColonyAssignedDemand - Bestand), statt die kolonistenbezogene mitzubenutzen.
            public Def def;
            // How much of the counted stock above is marked "do not pick up" (2026-08-09). NOT a segment of
            // its own and deliberately NOT subtracted from anything - those units are already sitting inside
            // `available`/`assignedInStore`/... exactly like any other stock. It only drives the "!" marker
            // and its own tooltip line, which is the whole point: the number stays honest AND the player can
            // see that part of it still has to be released. Total must not include it, or every bar would
            // double-count its own loot.
            public int forbidden;
            // Wie viel des gezaehlten Bestands LOSE auf der Karte liegt statt in einer Lagerzone
            // (2026-08-13): einheitlich anzeigen - auch im Verfuegbarkeitsbalken mit einer eigenen Farbe,
            // wie beim Rest. Genau wie `forbidden`: KEIN
            // zusaetzlicher Bestand, sondern eine Teilmenge des schon gezaehlten - deshalb nicht in Total,
            // sondern aus `available` herausgeschnitten und in eigener Farbe gezeichnet.
            public int ground;
            // Whether this bar is being drawn for a specific pawn at all (not whether that pawn happens to
            // own the item) - decides `available`'s own color: green when there's no pawn to possibly
            // confuse it with (a bare colony-wide overview - "available" IS the good/complete answer there),
            // yellow when a pawn IS in view (green is reserved for THAT pawn's own copy there, so a merely-
            // in-storage unit must read as visually distinct, not "this pawn's covered too").
            public bool pawnContext;
            public int Total => equippedByPawn + available + assignedInStore + assignedCarried + carriedUnassigned + inProduction;
        }

        private static readonly Color AvailEquippedGreen = FulfillColor(FulfillStatus.Equipped);
        private static readonly Color AvailStoredYellow = FulfillColor(FulfillStatus.Stored);
        // "Nicht aufsammeln"-Bestand als eigener Abschnitt (2026-08-13, etwas heller, ins
        // Gelbliche). Bewusst ein GRUEN und kein Orange: die Stuecke sind vorhanden und zaehlen mit - sie
        // sind nur gesperrt. Ein Warnton haette sie wie fehlend gelesen. Es liegt zwischen dem
        // Verfuegbar-Gruen (0.4/0.85/0.4) und dem Lager-Gelb, ist also als "dasselbe, aber anders"
        // erkennbar, ohne eine neue Bedeutungsfarbe einzufuehren.
        private static readonly Color AvailForbiddenGreen = new Color(0.68f, 0.9f, 0.42f);
        // "Liegt lose herum" (2026-08-13). Derselbe Gedanke wie beim Sperr-Gruen: es IST vorhanden, es liegt
        // nur nicht im Regal - also eine Nachbarfarbe des Verfuegbar-Tons, kein Warnton. Etwas blasser und
        // ins Graugruene gezogen, damit die drei Abschnitte (verfuegbar / am Boden / gesperrt) in dieser
        // Reihenfolge auch nebeneinander unterscheidbar bleiben.
        private static readonly Color AvailGroundGreen = new Color(0.52f, 0.78f, 0.55f);
        private static readonly Color AvailAssignedInStoreBlue = new Color(0.55f, 0.72f, 0.95f);
        private static readonly Color AvailAssignedCarriedBlue = new Color(0.26f, 0.42f, 0.72f);
        private static readonly Color AvailCarriedGrey = new Color(0.40f, 0.40f, 0.44f);
        // Public - also the one "in production" color Dialog_SquadSetup's own ReqStatus dot convention uses
        // now (2026-07-30 harmonization), replacing that spot's own ColInfo (blue), which never meant
        // anything in-production-specific elsewhere and just happened to be reused there.
        public static readonly Color AvailProductionBeige = new Color(0.78f, 0.68f, 0.48f);
        private static readonly Color AvailMissingRed = FulfillColor(FulfillStatus.Missing);

        // Die zwei Unterpunkte des Mouseover-Textes tragen GENAU die Farbe ihres eigenen Balkenabschnitts,
        // aus derselben Konstante gerechnet statt als zweite Zahl danebengeschrieben (2026-08-15: die
        // Sperr-Zeile war orange, waehrend ihr Balkenstueck laengst gruen ist - sie gehoert in den
        // Gruenton des Balkens). Orange bleibt dem Ausrufezeichen und dem
        // Rahmen vorbehalten: das ist die WARNUNG, die Zeile ist nur eine Aufschluesselung.
        // Einmal beim Laden gerechnet, nicht pro Mouseover - eine Zeichenkette pro Aufruf waere hier zwar
        // billig, aber ueberfluessig.
        private static readonly string GroundHex = "#" + ColorUtility.ToHtmlStringRGB(AvailGroundGreen);
        private static readonly string ForbiddenHex = "#" + ColorUtility.ToHtmlStringRGB(AvailForbiddenGreen);

        // Builds the segment breakdown from whichever domain-specific "raw ownership" numbers the caller
        // already has (weapons/pack-items/ammo each compute carried/assigned/stored differently - this
        // only unifies the PRESENTATION on top of them, not the underlying scans). Optionally carves this
        // pawn's own copy out of whichever bucket it's actually sitting in in to its own green segment, and
        // optionally appends a trailing missing/shortfall segment against neededCount.
        // forbidden: -1 (the default) means "work it out here from `def`". Deliberately NOT a per-caller
        // duty (2026-08-09): there are ~20 call sites for this bar, and making each one remember to pass
        // the flagged count is exactly how "uniformly, for EVERY kind of item" would have quietly decayed
        // into "for the three screens I remembered". A caller that already has the number (or that is
        // drawing a bar for something with no single def, e.g. a whole bundle) can still pass it directly.
        public static AvailabilitySegments BuildAvailabilitySegments(
            int carriedOnly, int carriedAndAssigned, int assignedInStore, int freeStored, int inProduction,
            Pawn pawn = null, Def def = null, int neededCount = 0, int forbidden = -1)
        {
            var seg = new AvailabilitySegments
            {
                assignedInStore = assignedInStore,
                assignedCarried = carriedAndAssigned,
                carriedUnassigned = carriedOnly,
                inProduction = inProduction,
                available = freeStored,
                pawnContext = pawn != null,
                def = def,
                forbidden = forbidden >= 0 ? forbidden : ForbiddenStockCount(def),
                ground = def is ThingDef gtd ? GroundStockCount(gtd) : 0
            };

            // def widened from ThingDef to the common Def base (2026-08-01: a generic Pack
            // slot like "Any Perishable Meal" always showed "Equipped: 0" even when the hovered pawn
            // genuinely carries a qualifying meal, because the old ThingDef-only param was always null for
            // a generic and PawnAlreadyHasItem can't answer "does X satisfy this generic's own matcher" -
            // PawnAlreadyHasGeneric can). Every existing caller already passes a ThingDef (or nothing), which
            // upcasts to Def with no change in behavior.
            bool pawnHasIt = pawn != null && def is ThingDef td ? PawnAlreadyHasItem(pawn, td)
                : pawn != null && def is LoadoutGenericDef gd && PawnAlreadyHasGeneric(pawn, gd);
            if (pawnHasIt)
            {
                if (seg.assignedCarried > 0) seg.assignedCarried -= 1;
                else if (seg.carriedUnassigned > 0) seg.carriedUnassigned -= 1;
                seg.equippedByPawn += 1;
            }

            if (neededCount > 0)
            {
                seg.missing = Mathf.Max(0, neededCount - seg.Total);
                seg.missingIsPawnSpecific = pawn != null;

                // DIE Zeile, um die es beim "Still missing: 0"-Fehler ging (2026-08-17, Variante
                // "beides umstellen"). Die Rechnung eine Zeile darueber vergleicht den Bedarf
                // DIESER Zelle mit dem Bestand der GANZEN Kolonie: 50 gefordert, 102 vorhanden, also
                // "fehlt nichts" - auch dann, wenn die Zuweisungen aller Kolonisten zusammen weit ueber 102
                // liegen und in Wahrheit fuer diesen hier nichts mehr uebrig ist. Bei Waffen zaehlte sogar
                // die Waffe eines anderen mit, die der gerade in der Hand haelt.
                // Die Reihum-Rechnung weiss es besser, und zwar fuer genau diesen Kolonisten. Sagt sie
                // nichts (-1: keine Vorlage, oder der Gegenstand steht nicht darin), bleibt es bei der
                // Zahl darueber - koloniweite Balken ohne Kolonistenbezug sind davon nicht betroffen, die
                // geben gar keinen Pawn mit.
                //
                // Gerechnet wird gegen die ZUTEILUNG, nicht mit der Fehlmenge des Kolonisten (korrigiert
                // 2026-08-17 abends): `neededCount` ist der Bedarf DIESER Zelle, und der gehoert gegen das
                // gehalten, was der Kolonist ueberhaupt bekommt. Die Fehlmenge direkt einzusetzen hat zwei
                // Zellen desselben Kolonisten wieder dieselbe Zahl zeigen lassen - der Fehler, den
                // cell-tooltips-say-this-cell am 16.08. schon einmal abgestellt hatte.
                int alloc = Dialog_AssignLoadouts.ClaimedAllocation(pawn, def);
                if (alloc >= 0) seg.missing = Mathf.Max(0, neededCount - alloc);
            }
            return seg;
        }

        // Shared "where is it" breakdown for an availability bar's extraTip - storedTT is WHERE it physically
        // sits (a shelf/rack), carriedTT/assignedTT are WHO has it. One implementation so every caller that
        // bothers to compute these three strings (currently Dialog_GenerateLoadouts, Dialog_SquadSetup) shows
        // byte-for-byte the same section-headered format, instead of each hand-rolling its own join.
        // Der eigene "marked do not pick up"-Abschnitt ist am 2026-08-15 WIEDER ENTFALLEN - er stand
        // zweimal im Tooltip, einmal reicht.
        // Das ist keine Ruecknahme der Vorgabe vom 2026-08-09 ("im Tooltip getrennt
        // auflisten"), sondern deren Erfuellung an der richtigen Stelle: seit dem Umbau der Balken steht
        // die Zahl laengst OBEN in der Aufstellung ('Marked "do not pick up": 4'), zusammen mit allen
        // anderen Zahlen. Der Abschnitt darunter wiederholte sie und nannte als Ort meist genau das, was
        // eine Zeile hoeher schon stand ('Lying on the ground: 4' oben gegen 'On the ground: 4' unten).
        // Was dabei wegfaellt und was nicht: die ZAHL bleibt (oben), die Warnung bleibt (oranger Rand und
        // "!" am Balken), der genaue Lagerplatz eines GESPERRTEN Stapels faellt weg - der stand nur hier.
        // Das ist der bewusst in Kauf genommene Verlust; die Orte des Bestands als Ganzes nennt "In
        // storage:" weiterhin.
        // stuffTT: "welches Material habe ich eigentlich" (2026-08-09) - siehe BuildStuffBreakdownLines.
        // Null fuer alles, was nicht aus einem Material gefertigt wird, und auch dann, wenn der ganze
        // Bestand aus einem einzigen Material besteht; die Zeile erscheint also nur, wo sie etwas sagt.
        public static string BuildLocationTooltip(string storedTT, string carriedTT, string assignedTT, string stuffTT = null)
        {
            return string.Join("\n\n", new[]
            {
                !string.IsNullOrEmpty(storedTT) ? "CER_LoadoutUtility_LocationTooltipStored".Translate(storedTT).ToString() : null,
                !string.IsNullOrEmpty(carriedTT) ? "CER_LoadoutUtility_LocationTooltipCarried".Translate(carriedTT).ToString() : null,
                !string.IsNullOrEmpty(assignedTT) ? "CER_LoadoutUtility_LocationTooltipAssigned".Translate(assignedTT).ToString() : null,
                !string.IsNullOrEmpty(stuffTT) ? "CER_LoadoutUtility_LocationTooltipMaterials".Translate(stuffTT).ToString() : null
            }.Where(s => s != null).ToArray());
        }

        // horizontal: numbers-in-segments bar (Weapon Loadouts' historical "Owned" look). !horizontal:
        // compact vertical strip, colors only, stacked top-to-bottom (Assign Loadouts' historical dot look).
        public static void DrawAvailabilityBar(Rect r, AvailabilitySegments seg, bool horizontal, string extraTip = null, bool drawTooltip = true)
        {
            // No pawn in view: "available" IS the good/complete answer, same green the vertical bar's own
            // "equipped by this pawn" segment uses (the two never appear together when there's no pawn, so
            // no ambiguity). A pawn IS in view: green is taken by that pawn's own copy, so "merely in
            // storage" must read as visually distinct - yellow.
            Color availableColor = seg.pawnContext ? AvailStoredYellow : AvailEquippedGreen;
            string availableHex = seg.pawnContext ? "#E6D94D" : "#66D966";

            int total = seg.Total + seg.missing;
            Widgets.DrawBoxSolid(r, new Color(0.15f, 0.15f, 0.17f));
            if (total > 0)
            {
                float pos = horizontal ? r.x : r.y;
                void Seg(int n, Color c)
                {
                    if (n <= 0) return;
                    float len = (horizontal ? r.width : r.height) * n / total;
                    Rect segR = horizontal ? new Rect(pos, r.y, len, r.height) : new Rect(r.x, pos, r.width, len);
                    Widgets.DrawBoxSolid(segR, c);
                    if (horizontal && len >= 14f)
                    {
                        // Per-segment luminance decides text color, not a hardcoded "just yellow needs it" -
                        // black reads better on every genuinely light segment (green/yellow/light blue/beige),
                        // white on the two dark ones (the deep "assigned & carried" blue, the carried-without-
                        // assignment grey) - a blanket black would make THOSE unreadable instead.
                        float lum = c.r * 0.3f + c.g * 0.59f + c.b * 0.11f;
                        GameFont pf = Text.Font;
                        Text.Font = GameFont.Tiny;
                        Text.Anchor = TextAnchor.MiddleCenter;
                        GUI.color = lum > 0.55f ? new Color(0.08f, 0.08f, 0.08f, 0.9f) : new Color(1f, 1f, 1f, 0.95f);
                        Widgets.Label(segR, n.ToString());
                        GUI.color = Color.white;
                        Text.Anchor = TextAnchor.UpperLeft;
                        Text.Font = pf;
                    }
                    pos += len;
                }
                Seg(seg.equippedByPawn, AvailEquippedGreen);
                // Der gesperrte Anteil wird aus dem Verfuegbar-Abschnitt HERAUSGESCHNITTEN, nicht zusaetzlich
                // gezeichnet (2026-08-13): die Stuecke sind in `available` schon enthalten, ein eigener Balken
                // obendrauf haette die Summe verfaelscht. Deshalb Min gegen available - liegt Gesperrtes in
                // anderen Toepfen (z.B. bereits zugewiesen), bleibt es dort gezaehlt und der Rand plus das
                // Ausrufezeichen tragen die Warnung wie bisher.
                int forbiddenShown = Mathf.Min(Mathf.Max(0, seg.forbidden), seg.available);
                // Der Boden-Anteil wird NACH dem gesperrten herausgeschnitten und gegen den Rest gedeckelt:
                // ein gesperrter Stapel, der auf dem Boden liegt, steckt in BEIDEN Zahlen, und zweimal
                // abzuziehen wuerde den Balken verfaelschen.
                int groundShown = Mathf.Min(Mathf.Max(0, seg.ground), seg.available - forbiddenShown);
                Seg(seg.available - forbiddenShown - groundShown, availableColor);
                Seg(groundShown, AvailGroundGreen);
                Seg(forbiddenShown, AvailForbiddenGreen);
                Seg(seg.assignedInStore, AvailAssignedInStoreBlue);
                Seg(seg.assignedCarried, AvailAssignedCarriedBlue);
                Seg(seg.carriedUnassigned, AvailCarriedGrey);
                Seg(seg.inProduction, AvailProductionBeige);
                // Always red, not grey-for-colony-wide/red-for-pawn-specific (2026-08-01, reversing an
                // earlier decision from the same day: "Still missing" entries should be REALLY red
                // - a genuine production shortfall reads as a real problem either
                // way, colony-wide or per-pawn, and having two different colors for the same underlying
                // meaning was apparently more confusing than the distinction was worth in practice).
                Seg(seg.missing, AvailMissingRed);
            }
            // Orange outline whenever part of this stock is flagged (2026-08-09) - deliberately IN ADDITION to
            // the "!" below, not instead of it. The glyph is small and sits on top of the rightmost segment,
            // where a narrow segment's own number can crowd it; the outline is unmissable and can never
            // collide with anything, so the signal survives even on the tightest bar in the mod.
            GUI.color = seg.forbidden > 0 ? ForbiddenMarkColor : new Color(1f, 1f, 1f, 0.25f);
            Widgets.DrawBox(r, 1);
            GUI.color = Color.white;

            // The "!" on every stock display that has flagged units in it (2026-08-09). Its own TipRegion is
            // suppressed here - the whole bar already gets one below, and two overlapping regions makes the
            // smaller one win and swallow the rich breakdown.
            DrawForbiddenMark(r, seg.forbidden, tooltip: false);

            // TipWanted davor (2026-08-13): BuildAvailabilityTooltip setzt einen
            // acht Zeilen langen Block zusammen, und dieser Balken steht in JEDER Zeile JEDER Liste des Mods.
            // RimWorld hat den Text ohnehin verworfen, solange die Maus woanders stand - siehe TipWanted.
            if (drawTooltip && TipWanted(r)) TooltipHandler.TipRegion(r, BuildAvailabilityTooltip(seg, extraTip));
        }

        // The rich "Total/Equipped/Available/Assigned in storage/Assigned & carried/Carried without
        // assignment/In production/Still missing" tooltip body, split out of DrawAvailabilityBar (2026-08-01)
        // so a caller that needs several of these stacked in one tooltip - e.g. Dialog_AssignLoadouts'
        // Ammo/Pack/Sidearm cells, which each bundle SEVERAL distinct items into one bar/dot - can build one
        // rich block per item instead of hand-rolling a flatter "- item: status word" summary. Every status
        // line always shown, even at 0 (2026-08-01 - reverts an earlier "only show
        // non-zero lines" behavior). "Missing" is the one exception: it's only ever meaningful when this bar
        // has an actual pawn/demand context (missingIsPawnSpecific or a real neededCount was supplied) -
        // always showing "Still missing: 0" on a context-free colony-wide bar (which never sets seg.missing
        // at all) would be a confusing, meaningless line.
        //
        // Color tags are wrapped around the ALREADY-RESOLVED translated text in plain C# - and "already
        // resolved" has to mean a REAL string, not just textually-after-Translate() (2026-08-01, root cause
        // finally found by decompiling RimWorld itself): TaggedString.operator+(string,TaggedString) and
        // operator+(TaggedString,string) mean the instant ANY .Translate() result appears ANYWHERE in a
        // concatenation chain, the WHOLE expression becomes a TaggedString, not a string - and TaggedString's
        // own implicit conversion back to string calls RawText.StripTags(), which silently deletes every
        // <color> tag the moment that TaggedString collapses back into a string-typed variable (here, every
        // `tip +=`). This is why the "pure C#" rewrite still rendered with zero color despite looking
        // identical in style to the proven-working c1e62c1/Patch_LoadoutTooltip patterns: THOSE either never
        // touch TaggedString at all (c1e62c1, pre-localization, plain literals) or force each .Translate()
        // to a real string via .ToString() BEFORE wrapping it in color (GetLoadoutTooltip's own `string line
        // = "...".Translate(...);` then a LATER, separate `line = "<color=...>" + line + "</color>";` with
        // zero TaggedString left in that second expression). Fixed the same way here: .ToString() forces
        // each Translate() call to resolve to a real string BEFORE it ever touches a <color> literal, so
        // none of these concatenations ever become a TaggedString in the first place.
        //
        // ============ heading/trailer sind der Grund, warum die Falle oben NICHT reicht (2026-08-17) ============
        // Befund mit zwei Screenshots nebeneinander: die Munitions-Zelle zeigte den Block farbig, die
        // Waffen-Zelle denselben Block schneeweiss - Hinweis auf fehlenden zentralen Code. Ziel: zentral,
        // damit es fuer alle gleich laeuft, ausser es gibt einen SEHR guten Grund dagegen.
        //
        // Die zentrale Funktion gab es schon, sie war es nicht. Der Unterschied lag EINE Ebene darueber:
        // die Waffen-Zelle baute ihre Ueberschrift und ihre Schlusszeile selbst um das Ergebnis herum
        //     string tip = mainW.LabelCap + ":\n" + BuildAvailabilityTooltip(seg) + "\n\n" + "...".Translate(x);
        // und damit war die GANZE Kette eine TaggedString - deren implizite Rueckwandlung in einen string
        // (genau hier, bei der Zuweisung an `string tip`) ruft RawText.StripTags() und loescht jedes <color>,
        // das diese Funktion gerade eingebaut hat. Die Munitions-Zelle kam nur deshalb davon, weil sie ueber
        // string.Join geht: das ruft ToString(), und das laesst die Tags stehen.
        // Eine Falle, die man an der Aufrufstelle nicht SIEHT, faengt man dort auch nicht ab - deshalb kann
        // die Ueberschrift und die Schlusszeile jetzt HIER hinein, statt aussen herumgebaut zu werden. Damit
        // findet die letzte Zusammensetzung immer in dieser Funktion statt, und ein neuer Aufrufer kann die
        // Faerbung nicht mehr aus Versehen wegwerfen.
        // Die koloniweite Aufstellung in EINER Zeile - dieselben Zeilen-Keys und Farben wie BuildAvailabilityTooltip, aber
        // nur die Posten mit Wert > 0 (Total immer), durch Kommas getrennt. Fuer Zellen-Tooltips mit vielen Posten
        // (2026-08-30: Waffen-Erklaertext hoeher als der Bildschirm).
        public static string AvailabilityOneLiner(AvailabilitySegments seg)
        {
            var parts = new List<string> { "CER_LoadoutUtility_AvailTotal".Translate(seg.Total.ToString()).ToString() };
            void Add(string key, int v, string hex) { if (v > 0) parts.Add("<color=" + hex + ">" + key.Translate(v.ToString()).ToString() + "</color>"); }
            // "Equipped" gehoert in die Zeile, sobald ein Kolonist im Spiel ist (2026-09-06): Total zaehlt
            // diesen Topf mit, die Kurzform liess ihn weg - die Posten summierten sich also nie auf die
            // Zahl, neben der sie stehen. Dieselbe Regel wie in der langen Fassung (nur mit Kolonistenbezug).
            if (seg.pawnContext) Add("CER_LoadoutUtility_AvailEquipped", seg.equippedByPawn, "#66D966");
            Add("CER_LoadoutUtility_AvailAvailable", seg.available, seg.pawnContext ? "#E6D94D" : "#66D966");
            Add("CER_LoadoutUtility_AvailAssignedInStore", seg.assignedInStore, "#8CB8F2");
            Add("CER_LoadoutUtility_AvailAssignedCarried", seg.assignedCarried, "#8CB8F2");
            Add("CER_LoadoutUtility_AvailCarriedUnassigned", seg.carriedUnassigned, "#A8A8A8");
            Add("CER_LoadoutUtility_AvailInProduction", seg.inProduction, "#C7AD7A");
            Add("CER_LoadoutUtility_AvailStillMissing", seg.def != null ? Mathf.Max(0, ColonyAssignedDemand(seg.def) - seg.Total) : seg.missing, "#E65959");
            return string.Join(", ", parts.ToArray());
        }

        public static string BuildAvailabilityTooltip(AvailabilitySegments seg, string extraTip = null,
            string heading = null, string trailer = null)
        {
            string availableHex = seg.pawnContext ? "#E6D94D" : "#66D966";
            // "Total" ist wieder der BESTAND und nur der (2026-08-17 abends, in ZWEI unabhaengigen
            // Tests aufgefallen). Bis dahin stand hier `seg.Total + seg.missing`, also "wie viele
            // werden gebraucht" - das ergab Sinn, solange die Fehlmenge kolonieweit war. Seit die Fehlmenge
            // kolonistenbezogen ist, ergibt es keinen mehr, und die Zahl wurde nachweislich unlesbar:
            //   cell-availability-per-pawn: "die Zeile 'Total' ist nicht koloniweit konstant, sondern
            //   aendert sich mit dem betrachteten Kolonisten (14 / 20 / 12)" - bei drei Kolonisten mit
            //   identischem Bedarf und identischem Bestand.
            //   weapon-avail-bars-agree: dieselbe Waffe steht "zweimal auf 2 und zweimal auf 1 ... unter
            //   derselben Beschriftung stehen zwei verschiedene Zahlen" - je nachdem, ob das Fenster eine
            //   Fehlmengen-Zeile hat oder nicht.
            // Eine Beschriftung, die je nach Fenster und je nach Kolonist etwas anderes bedeutet, ist keine
            // Beschriftung. "Total" heisst ab jetzt ueberall dasselbe: was die Kolonie davon hat. Was
            // gebraucht wird, steht ohnehin in der Fehlmengen-Zeile darunter.
            // Der BALKEN selbst rechnet unveraendert mit seg.Total + seg.missing - er soll die Fehlmenge ja
            // als eigenen Abschnitt zeigen.
            string tip = "CER_LoadoutUtility_AvailTotal".Translate(seg.Total.ToString()).ToString();
            // Equipped is the one status line that is structurally meaningless without a specific pawn in
            // context (it can only ever be nonzero when BuildAvailabilitySegments carved it out of a pawn's
            // own carried/assigned count) - shown only when seg.pawnContext, unlike every other line below,
            // which stays visible even at 0 (2026-08-01, refines the "always show every line" rule to not
            // include a line that literally cannot be anything but 0 in a colony-wide, no-pawn bar).
            if (seg.pawnContext)
                tip += "\n  <color=#66D966>" + "CER_LoadoutUtility_AvailEquipped".Translate(seg.equippedByPawn.ToString()).ToString() + "</color>";
            tip += "\n  <color=" + availableHex + ">" + "CER_LoadoutUtility_AvailAvailable".Translate(seg.available.ToString()).ToString() + "</color>";
            // Boden und "nicht aufsammeln" stehen als UNTERPUNKTE direkt unter Verfuegbar (2026-08-15, laut
            // Skizze Unterpunkte von "available"). Das ist
            // nicht nur Optik: der Balken schneidet beide Abschnitte aus `available` heraus (siehe die
            // Min-Deckelung in DrawAvailabilityBar), sie sind also wirklich Teilmengen davon. Vorher standen
            // sie ganz unten, hinter "fehlt noch" - dort las sich der Boden-Anteil wie ein weiterer Topf
            // NEBEN den anderen, und die Summe ging scheinbar nicht auf.
            // Zwei Leerzeichen mehr Einzug als die Ebene darueber, und weiterhin nur bei > 0: eine dauerhafte
            // Null-Zeile an jedem Balken des Spiels waere Rauschen.
            // GEDECKELT wie im Balken, nicht die Rohzahl (2026-08-16, im Test gefunden: "Lying on
            // the ground: 53" stand eingerueckt unter "Available: 35", war also groesser als der Wert, unter
            // dem es haengt). Der Widerspruch: die Einrueckung vom Vortag
            // behauptet "Teilmenge von Verfuegbar", die Zahl daneben kam aber ungefiltert aus der
            // Bestandszaehlung - und 53 lose Medizin verteilen sich eben ueber MEHRERE Toepfe (verfuegbar,
            // zugewiesen-im-Lager). Der Balken deckelt genau deshalb schon immer; jetzt rechnet die Zeile
            // mit derselben Formel wie ihr eigenes Balkenstueck, in derselben Reihenfolge (gesperrt
            // zuerst, Boden gegen den Rest) - Zeile und Streifen koennen damit nicht mehr auseinandergehen.
            // Was dabei verschwindet, ist die Gesamtzahl am Boden. Das ist Absicht: unter der Ueberschrift
            // "Verfuegbar" ist "wie viel DAVON liegt herum" die Frage, und die Gesamtzahl stand vorher an
            // einer Stelle, an der sie eine falsche Aussage machte.
            int forbiddenShown = Mathf.Min(Mathf.Max(0, seg.forbidden), seg.available);
            int groundShown = Mathf.Min(Mathf.Max(0, seg.ground), seg.available - forbiddenShown);
            if (groundShown > 0)
                tip += "\n    <color=" + GroundHex + ">" + "CER_LoadoutUtility_AvailOnGround".Translate(groundShown.ToString()).ToString() + "</color>";
            if (forbiddenShown > 0)
                tip += "\n    <color=" + ForbiddenHex + ">" + "CER_LoadoutUtility_AvailForbidden".Translate(forbiddenShown.ToString()).ToString() + "</color>";
            tip += "\n  <color=#8CB8F2>" + "CER_LoadoutUtility_AvailAssignedInStore".Translate(seg.assignedInStore.ToString()).ToString() + "</color>";
            tip += "\n  <color=#426BB8>" + "CER_LoadoutUtility_AvailAssignedCarried".Translate(seg.assignedCarried.ToString()).ToString() + "</color>";
            tip += "\n  <color=#66666F>" + "CER_LoadoutUtility_AvailCarriedUnassigned".Translate(seg.carriedUnassigned.ToString()).ToString() + "</color>";
            tip += "\n  <color=#C7AD7A>" + "CER_LoadoutUtility_AvailInProduction".Translate(seg.inProduction.ToString()).ToString() + "</color>";
            // KOLONIWEIT gerechnet, nicht kolonistenbezogen (2026-08-30: unten muesste es dann auch 1 heissen,
            // nicht 2). Diese Aufstellung ueberschreibt sich selbst mit 'Colony-wide', also
            // beantwortet die Zeile jetzt auch die koloniweite Frage: wie viele Stueck sind zugewiesen, die es nicht
            // gibt. Die kolonistenbezogene Fehlmenge steht im Block darueber (Dialog_AssignLoadouts.PawnBlock) und im
            // Streifen - beide rechnen unveraendert weiter mit seg.missing.
            int colonyMissing = seg.def != null ? Mathf.Max(0, ColonyAssignedDemand(seg.def) - seg.Total) : seg.missing;
            if (colonyMissing > 0 || seg.missing > 0 || seg.missingIsPawnSpecific)
            {
                // Always red now - see DrawAvailabilityBar's own Seg() call for the same 2026-08-01 reversal.
                tip += "\n  <color=#E65959>" + "CER_LoadoutUtility_AvailStillMissing".Translate(colonyMissing.ToString()).ToString() + "</color>";
            }
            if (!string.IsNullOrEmpty(extraTip)) tip += "\n\n" + extraTip;
            // Beide bewusst am Ende und beide als reiner string: was ein Aufrufer hereingibt, ist damit
            // laengst aufgeloest, und die Tags oben ueberleben die Rueckgabe.
            if (!string.IsNullOrEmpty(heading)) tip = heading + "\n" + tip;
            if (!string.IsNullOrEmpty(trailer)) tip += "\n\n" + trailer;
            return tip;
        }

        // --- Shared constants & lookups -------------------------------------------------

        // Enum.GetValues allocates a fresh array on EVERY call - and the ability flags are counted per weapon per
        // frame in the list loops, so the hot paths iterate this cached copy instead.
        public static readonly SpecialAbility[] AllAbilities = (SpecialAbility[])Enum.GetValues(typeof(SpecialAbility));

        /// <summary>Prefix CE uses for the per-weapon generic-ammo placeholder defs (e.g. "GenericAmmo-Gun_AssaultRifle").</summary>
        public const string GenericAmmoPrefix = "GenericAmmo-";

        private static Dictionary<ThingDef, RecipeDef> recipeByProduct;

        /// <summary>First recipe producing <paramref name="product"/>, cached. Defs are immutable at runtime, so the cache is valid for the game process.</summary>
        public static RecipeDef GetRecipeFor(ThingDef product)
        {
            if (product == null) return null;
            if (recipeByProduct == null)
            {
                recipeByProduct = new Dictionary<ThingDef, RecipeDef>();
                foreach (RecipeDef r in DefDatabase<RecipeDef>.AllDefs)
                {
                    if (r.products == null) continue;
                    foreach (ThingDefCountClass p in r.products)
                    {
                        if (p.thingDef != null && !recipeByProduct.ContainsKey(p.thingDef))
                            recipeByProduct[p.thingDef] = r;
                    }
                }
            }
            recipeByProduct.TryGetValue(product, out RecipeDef recipe);
            return recipe;
        }

        /// <summary>All colony benches whose def is a recipeUser of this recipe, in stable id order. Mirrors
        /// Dialog_SupplyLoadouts' own private AmmoBenchesFor - shared here so EnsureAmmoMaintenanceBills below
        /// doesn't need a live Dialog_SupplyLoadouts instance to pick a bench.</summary>
        public static List<Building_WorkTable> BenchesForRecipe(RecipeDef recipe)
        {
            if (recipe == null) return new List<Building_WorkTable>();
            return AllColonyBenches().Where(b => BenchCanRun(b, recipe)).OrderBy(b => b.thingIDNumber).ToList();
        }

        // ====== "Kann DIESE Bank DIESES Rezept?" - eine Frage, eine Antwort (2026-09-04) ======
        //
        // Anlass ist ein Absturzkreislauf, im Test aus dem Player.log belegt:
        //   JobDriver threw exception in toil MakeUnfinishedThingIfNeeded's initAction for pawn Mack
        //   driver.job=(DoBill ... A = Thing_AmmoBench ... Giver = ... DoBillsAmmoBench)
        //   System.InvalidCastException: Specified cast is not valid.
        // Im Stapel steht keine Zeile von uns - RimWorld stolpert ueber einen Auftrag, den es an dieser
        // Bank gar nicht geben duerfte. Der Kolonist bricht ab, faengt neu an, das Log laeuft voll.
        //
        // Wie es dazu kam: das Nachschub-Fenster hat die Bankliste einer Zeile aus dem PRODUKT
        // hergeleitet (GetWorkbenchesFor(weapon) sammelt die recipeUsers ALLER Rezepte, die diesen
        // Gegenstand herstellen), den Auftrag dann aber mit e.recipe gesetzt. Solange ein Gegenstand nur
        // ein Rezept hat, faellt das nie auf. Beim Wurfsprengstoff faellt es auf: MakeSatchelCharge haengt
        // in CE an GrenadeRecipeBase mit <recipeUsers><li>TableMachining</li></recipeUsers>, und die
        // Munitionsbank stand trotzdem in der Liste - also landete ein Maschinentisch-Rezept auf der
        // Munitionsbank. Klassische "eine Frage, zwei Rechnungen".
        //
        // AllRecipes ist ausdruecklich die richtige Quelle und nicht recipeUsers: RimWorld fuehrt ein
        // Rezept auf ZWEI Wegen an eine Bank (recipeUsers am Rezept ODER recipes am Bank-Def) und
        // verschmilzt beide genau hier. Wer nur recipeUsers prueft, uebersieht die zweite Haelfte -
        // Dialog_GenerateAmmoBills fragt seit jeher AllRecipes, diese Stelle tat es nicht.
        public static bool BenchCanRun(Building_WorkTable bench, RecipeDef recipe)
            => bench?.def != null && recipe != null && bench.def.AllRecipes != null && bench.def.AllRecipes.Contains(recipe);

        // True for anything that gets USED UP rather than just carried once - medicine (treatment), drugs,
        // food, and thrown explosives (grenades) - as opposed to a durable item (a gun, a shield, a
        // binoculars-style tool) that a colonist just needs ONE of and keeps indefinitely (2026-08-01,
        // correction: medical goods, smoke grenades etc. are consumables too -
        // the standing-maintenance-bill fix below must not be scoped to ammo alone). Checked against the
        // THING's own properties, not the PackCategory bucket it displays under - PackCategory.Utility mixes
        // real consumables (a grenade) with durable tools (binoculars) together, which would either miss
        // grenades or wrongly include binoculars if used instead.
        public static bool IsConsumableThing(ThingDef d) =>
            d != null && (d.IsMedicine || d.IsDrug || d.IsIngestible || IsThrownExplosive(d));

        // Which defs in `demand` need a NEW or RAISED standing maintenance bill, regardless of whether
        // current stock already covers that demand right now (2026-08-01: an ammo type that
        // started out already well-stocked - e.g. 668 rounds of .30-06 Springfield sitting free in storage -
        // never got a bill at all, because the review popup's own "stock+production < demand" filter excluded
        // it from ever being proposed. Consumable items need a bill even when there is stock -
        // extended to medicine/drugs/grenades, not just ammo).
        // Deliberately does NOT touch any bill itself - an earlier version of this function called
        // AddOrUpdateAmmoBill directly, silently, with no review step at all, which the user then correctly
        // called out as breaking this mod's own "nothing happens without going through the popup" convention
        // (every other production decision in this mod is Confirm-gated). This just tells the CALLER which
        // defs to fold into the review popup's own row list (even though raw current stock alone wouldn't
        // otherwise earn them a row) - the existing Confirm-time GenerateAll() then creates the bill exactly
        // like it would for any other row, through the same reviewable path.
        // requireConsumable: false for the ammo dict (everything in it is inherently ammo, no filter needed);
        // true for weapon/pack-item dicts, which mix real consumables (medicine, grenades) in with durable
        // items (guns, shields, tools) that should keep their existing one-shot "make N now" behavior instead.
        // "Already covered" here means an existing TargetCount bill whose own target already meets or beats
        // current demand - once the popup-driven Confirm actually creates/raises that bill once, a later call
        // with the same demand correctly stops re-proposing it (not nagging about something already set up).
        public static Dictionary<ThingDef, int> ConsumablesNeedingMaintenance(Dictionary<ThingDef, int> demand, bool requireConsumable = false)
        {
            var result = new Dictionary<ThingDef, int>();
            if (demand == null) return result;
            foreach (var kv in demand)
            {
                if (kv.Value <= 0) continue;
                if (requireConsumable && !IsConsumableThing(kv.Key)) continue;
                RecipeDef recipe = GetRecipeFor(kv.Key);
                if (recipe == null || !RecipeIsResearched(recipe)) continue;
                if (BenchesForRecipe(recipe).Count == 0) continue;
                // Der BESTAND zaehlt hier genauso wie ein laufender Auftrag (2026-08-13, im Test widerlegt:
                // 30.000 Schuss lagen auf dem Boden, der
                // Verfuegbarkeits-Balken zeigte gruen 29.988 - und in derselben Zeile stand ein Vorschlag,
                // 200 weitere herzustellen).
                // Das war die SECHSTE Fundstelle desselben Musters und die unangenehmste, weil sie das Tor
                // von gestern nicht etwa falsch berechnet, sondern KOMPLETT UMGEHT: die Aufrufer sieben ihre
                // Fehlmengen sauber ueber AmmoOnHand, und danach legt diese Funktion die Zeile ungeprueft
                // wieder oben drauf ("Verbrauchsgut braucht einen Dauerauftrag"). Sie hat Bestand nie
                // angesehen, nur Rezept, Werkbank und vorhandene Auftraege.
                // Der Sinn der Funktion bleibt: ein Verbrauchsgut, das WIRKLICH knapp ist, bekommt weiterhin
                // seine Zeile, auch wenn die rohe Fehlmenge sie sonst nicht verdient haette.
                // OHNE GroundStockCount (2026-08-15, Review-Befund): WeaponStoredCount zaehlt JEDES gespawnte
                // Stueck auf einer Kolonie-Karte, mit Lagerzone oder ohne - loses Zeug steckt dort also
                // schon drin, waehrend GroundStockCount genau dieses lose Zeug NOCHMAL liefert.
                // Fehlszenario: 30 Medizin liegen ungezont herum, Bedarf 40. Gerechnet wurden 30 + 30 = 60,
                // also "gedeckt", und die Zeile fuer den Dauerauftrag verschwand still aus dem
                // Nachschub-Fenster, obwohl 10 fehlen.
                // Die anderen Fundstellen im Mod paaren GroundStockCount mit IngredientStockCount, und DAS
                // ist ueberschneidungsfrei (Zone gegen Nicht-Zone) - nur hier stand die karteweite Summe.
                // CarriedStockCount bleibt: was in Rucksaecken steckt, ist nicht gespawnt.
                int have = IsAmmoDef(kv.Key)
                    ? AmmoOnHand(kv.Key)
                    : WeaponStoredCount(kv.Key) + CarriedStockCount(kv.Key);
                if (have >= kv.Value) continue;
                bool alreadyCovered = AllColonyBenches().Any(b => b.billStack != null && b.billStack.Bills.OfType<Bill_Production>()
                    .Any(bp => bp.recipe == recipe && bp.repeatMode == BillRepeatModeDefOf.TargetCount && bp.targetCount >= kv.Value));
                if (!alreadyCovered) result[kv.Key] = kv.Value;
            }
            return result;
        }

        /// <summary>True if every research prerequisite of <paramref name="recipe"/> is finished.</summary>
        public static bool RecipeIsResearched(RecipeDef recipe)
        {
            if (recipe == null) return false;
            if (recipe.researchPrerequisite != null && !recipe.researchPrerequisite.IsFinished) return false;
            if (recipe.researchPrerequisites != null && recipe.researchPrerequisites.Any(x => !x.IsFinished)) return false;
            return true;
        }

        /// <summary>True if the colony can actually craft <paramref name="w"/> right now (researched recipe + a matching bench present).</summary>
        public static bool IsProducible(ThingDef w)
        {
            RecipeDef r = GetRecipeFor(w);
            if (r == null || !RecipeIsResearched(r)) return false;
            List<ThingDef> benchDefs = GetWorkbenchesFor(w);
            return AllColonyBenches().Any(b => benchDefs.Contains(b.def));
        }

        // --- Generic ammo helpers -------------------------------------------------------

        /// <summary>The CE generic-ammo placeholder def for a weapon, or null if none exists.</summary>
        public static LoadoutGenericDef GetGenericAmmoDef(ThingDef weapon)
        {
            return weapon == null ? null : DefDatabase<LoadoutGenericDef>.GetNamedSilentFail(GenericAmmoPrefix + weapon.defName);
        }

        /// <summary>The weapon a generic-ammo placeholder resolves to, or null if <paramref name="genericDef"/> is not a generic-ammo def.</summary>
        public static ThingDef GetWeaponForGenericAmmo(Def genericDef)
        {
            LoadoutGenericDef gd = genericDef as LoadoutGenericDef;
            if (gd == null || !gd.defName.StartsWith(GenericAmmoPrefix)) return null;
            return DefDatabase<ThingDef>.GetNamedSilentFail(gd.defName.Substring(GenericAmmoPrefix.Length));
        }

        /// <summary>"Ist dieser generische Eintrag ein MUNITIONS-Platzhalter" - nur der Praefix, ohne die
        /// DefDatabase-Aufloesung von GetWeaponForGenericAmmo. Gedacht fuer den heissen Pfad
        /// (Patch_Loadout_MultiParentSlots haengt an Loadout.Slots, das CEs Think-Tree pro Kolonist und
        /// Entscheidung liest) - dort zaehlt nur, OB es Munition ist, nie welche Waffe dahintersteht.
        /// Bewusst auch dann true, wenn die Waffen-Def nicht mehr existiert: ein Platzhalter ohne Waffe ist
        /// immer noch ein Munitions-Platzhalter und darf nicht als Versorgungs-Eintrag durchgehen.</summary>
        public static bool IsGenericAmmoDef(Def genericDef)
        {
            LoadoutGenericDef gd = genericDef as LoadoutGenericDef;
            return gd != null && gd.defName.StartsWith(GenericAmmoPrefix);
        }

        /// <summary>Die EINE Definition von "dieser Slot ist Munition" - konkrete Munition ODER ein
        /// generischer Munitions-Platzhalter ("Any ammo for X").
        /// Warum zentral (2026-08-14, nach einem wiederholten Fehler): dieselbe Frage wurde an vier
        /// Stellen einzeln beantwortet, und zwei davon zaehlten nur konkrete Munition. Folge: hakt man bei
        /// zwei Waffen "Any ammo" an und nimmt einen Haken wieder weg, blieb ein generischer Eigen-Slot
        /// stehen, den Patch_Loadout_MultiParentSlots nicht als Munition erkannte - er liess daraufhin die
        /// Vorgabe-Munition der Vorlage fuer BEIDE Waffen zurueckkommen, samt Haken neben dem noch
        /// gesetzten "Any ammo". Der Fix vom 13.08. hatte nur die Dialog-Seite korrigiert, nicht die
        /// Patch-Seite, die tatsaechlich entscheidet. Vier Stellen, eine Antwort - deshalb hier.</summary>
        /// <summary>Echte Munition - nicht die Wurfwaffen, die CE technisch auch als AmmoThing fuehrt.</summary>
        // 2026-08-31, Testergebnis zu "Optimize ammo bills" (angelegte Auftraege, die garantiert niemand
        // braucht, tauchten nicht als loeschbar auf): die Kandidatenliste filterte mit
        // !IsSelfAmmoWeapon - und das ist fuer JEDE Munition wahr (die Funktion endet auf "|| w is AmmoDef").
        // Damit war die Liste seit dem 14.08. immer leer, und der Knopf "Remove unneeded bills" stand dauerhaft
        // auf (0). Gemeint war "Wurfsprengstoff raus" - und das trennt genau dieselbe Bedingung wie beim
        // Slot-Verbund (Patch_Loadout_MultiParentSlots.IsRealAmmoSlot): eine Wurfwaffe ist ein Weapon-Def,
        // echte Munition nicht.
        public static bool IsRealAmmoDef(ThingDef d) => IsAmmoDef(d) && !d.IsWeapon;

        public static bool IsAmmoSlot(LoadoutSlot s) =>
            s != null && (IsAmmoDef(s.thingDef) || IsGenericAmmoDef(s.genericDef));

        // --- Ammo set helpers -----------------------------------------------------------

        public static AmmoSetDef GetAmmoSet(ThingDef weapon)
        {
            CompProperties_AmmoUser comp = weapon != null ? weapon.GetCompProperties<CompProperties_AmmoUser>() : null;
            return comp != null ? comp.ammoSet : null;
        }

        /// <summary>
        /// True if the weapon uses Combat Extended mechanics (CE ammo, CE verb, CE projectile, or CE melee tools).
        /// A weapon that fails this is a non-CE / uncompatible weapon whose damage/AP we cannot read reliably.
        /// </summary>
        public static bool IsCeCompatible(ThingDef w)
        {
            if (w == null) return false;
            if (w.IsRangedWeapon)
            {
                // If CE bolted its ammo system onto the gun (CompProperties_AmmoUser is a CE class), CE has patched it -
                // this catches beam/charge weapons whose damage lives on a beam verb rather than a CE projectile
                // (e.g. the Biotech beam graser, which CE rewrites to consume charged/spacer ammo).
                if (w.GetCompProperties<CompProperties_AmmoUser>() != null) return true;
                // Otherwise a ranged weapon is CE-readable if its ammo or verb projectiles are ProjectilePropertiesCE, or
                // it uses a CE verb. A ToolCE bash alone does NOT make its RANGED stats readable.
                AmmoSetDef set = GetAmmoSet(w);
                if (set != null && set.ammoTypes != null)
                    foreach (AmmoLink link in set.ammoTypes)
                        if (link.projectile != null && link.projectile.projectile is ProjectilePropertiesCE) return true;
                if (w.Verbs != null)
                    foreach (VerbProperties v in w.Verbs)
                    {
                        if (v is VerbPropertiesCE) return true;
                        if (v.defaultProjectile != null && v.defaultProjectile.projectile is ProjectilePropertiesCE) return true;
                    }
                return false;
            }
            // Melee: CE-readable if it has ToolCE tools (which carry the CE armor-pen values).
            return w.tools != null && w.tools.Any(t => t is ToolCE);
        }

        /// <summary>A weapon usable one-handed (so it can be paired with a CE shield). CE marks these with the
        /// "CE_OneHandedWeapon" weapon tag (a "CE_OnehandedWeapon" typo variant exists in some defs, hence the
        /// case-insensitive "onehand" match).</summary>
        public static bool IsOneHandedWeapon(ThingDef w)
        {
            return w != null && w.IsWeapon && w.weaponTags != null
                && w.weaponTags.Any(t => t != null && t.Replace("-", "").ToLower().Contains("onehand"));
        }

        /// <summary>A CE shield: apparel whose thing class is CombatExtended.Apparel_Shield. Checked by class name so
        /// there is no hard type dependency and modded subclasses still count.</summary>
        // Gemerkt (2026-08-14, Befund 9 der zweiten Performance-Runde): die Antwort haengt allein an
        // unveraenderlichen Def-Daten, die Pruefung selbst laeuft aber die ganze Basistyp-Kette hoch und
        // vergleicht dabei STRINGS - und sie steht in den Kandidatenschleifen des Solvers (PickBest,
        // PickSidearm, PickGrenade prüfen sie pro Kandidat), die beim Ziehen an den Reglern alle 150 ms
        // komplett neu laufen. Session-Cache wie bei den Nachbarn (utilityWeaponCache, weaponBulkCache).
        private static readonly Dictionary<ThingDef, bool> shieldApparelCache = new Dictionary<ThingDef, bool>();

        public static bool IsShieldApparel(ThingDef d)
        {
            if (d == null) return false;
            if (shieldApparelCache.TryGetValue(d, out bool cached)) return cached;
            bool result = false;
            if (d.IsApparel && d.thingClass != null)
                for (System.Type t = d.thingClass; t != null && t != typeof(object); t = t.BaseType)
                    if (t.Name == "Apparel_Shield") { result = true; break; }
            shieldApparelCache[d] = result;
            return result;
        }

        /// <summary>A def that belongs in a WEAPON list. ThingDef.IsWeapon alone is too wide: vanilla gives
        /// resources like wood a melee tool, so they land in listerThings' Weapon group and in every
        /// IsWeapon-only enumeration (2026-08-26: "wood, Available 2000" as a row in Weapon Loadouts).
        /// equipmentType filters those out - real weapons declare Primary, resources stay None - and
        /// !IsStuff is the belt-and-braces for modded stuff with tools. Every def-database weapon
        /// enumeration goes through here; one question, one calculation.</summary>
        public static bool IsRealWeaponDef(ThingDef d)
            => d != null && d.IsWeapon && !d.destroyOnDrop
               && d.equipmentType != EquipmentType.None && !d.IsStuff;

        /// <summary>Thrown explosive / throwable (grenade, bomb, throwing weapon) detected by weapon tag or category -
        /// never by defName, so modded throwables are caught too.</summary>
        public static bool IsThrownExplosive(ThingDef w)
        {
            if (w == null) return false;
            if (w.weaponTags != null && w.weaponTags.Any(t => { string l = t.ToLower(); return l.Contains("grenade") || l.Contains("bomb") || l.Contains("throw"); })) return true;
            if (w.thingCategories != null && w.thingCategories.Any(c => c.defName.ToLower().Contains("grenade"))) return true;
            return false;
        }

        /// <summary>A weapon that IS its own ammo: no external CE ammoSet, but spent on use and crafted in batches like
        /// ammunition. Covers thrown explosives AND CE weapons declared as an <c>AmmoDef</c> (javelins/pila). Detected by
        /// CE type/tags, never by defName. Drives ammo-bill routing; category placement still uses the narrower grenade
        /// check in the dialog.</summary>
        public static bool IsSelfAmmoWeapon(ThingDef w)
        {
            if (w == null) return false;
            if (w.GetCompProperties<CompProperties_AmmoUser>() != null) return false; // uses real external ammo → normal flow
            return IsThrownExplosive(w) || w is AmmoDef;
        }

        /// <summary>The concrete ammo ThingDefs a weapon can fire.</summary>
        public static IEnumerable<ThingDef> GetAmmoTypesFor(ThingDef weapon)
        {
            AmmoSetDef set = GetAmmoSet(weapon);
            if (set == null || set.ammoTypes == null) yield break;
            foreach (AmmoLink link in set.ammoTypes)
                if (link.ammo != null) yield return link.ammo;
        }

        /// <summary>
        /// The ammo picker list for a weapon's loadout row: the generic placeholder first (if any), then every concrete
        /// ammo type. For grenades the weapon itself is its only "ammo".
        /// </summary>
        public static List<Def> GetLoadoutAmmoDefs(ThingDef weapon, bool selfAmmo)
        {
            List<Def> defs = new List<Def>();
            // Also re-checked internally (not just trusting the caller's flag): a couple of call sites gate
            // on Dialog_GenerateLoadouts.IsGrenade(weapon) before reaching here, and that check only catches
            // grenade/bomb/throw weapon tags - it misses CE's OTHER self-ammo shape, a weapon that IS itself
            // an AmmoDef (javelins/pila, no separate CompProperties_AmmoUser ammo type at all). Those callers
            // then passed selfAmmo=false, which fell into the GetAmmoSet(weapon) branch below - null for a
            // self-ammo weapon (no CompProperties_AmmoUser), so it silently returned an EMPTY list instead of
            // "the weapon is its own ammo". That read everywhere downstream as "no ammo available" rather
            // than resolving to the weapon itself, incorrectly implying an unresearched/missing ammo type for
            // a weapon that was actually fine. IsSelfAmmoWeapon is cheap (tag/type checks, no DB scan), so
            // OR-ing it in here is a safe no-op for every already-correctly-guarded caller.
            if (selfAmmo || IsSelfAmmoWeapon(weapon))
            {
                if (weapon != null) defs.Add(weapon);
                return defs;
            }
            if (GetAmmoSet(weapon) != null)
            {
                LoadoutGenericDef generic = GetGenericAmmoDef(weapon);
                if (generic != null) defs.Add(generic);
                defs.AddRange(GetAmmoTypesFor(weapon).Cast<Def>());
            }
            return defs;
        }

        /// <summary>Preferred default ammo for a fresh loadout row: generic placeholder, else the FMJ variant, else the first entry.</summary>
        public static Def PickDefaultAmmo(List<Def> ammoDefs)
        {
            if (ammoDefs == null || ammoDefs.Count == 0) return null;

            DefaultFirearmAmmo pref = CEReloadedMod.settings != null ? CEReloadedMod.settings.defFirearmAmmo : DefaultFirearmAmmo.FMJ;

            if (pref == DefaultFirearmAmmo.Generic)
            {
                Def g = ammoDefs.FirstOrDefault(a => a is LoadoutGenericDef);
                if (g != null) return g;
            }
            else
            {
                // Match CE's "(FMJ)" / "(AP)" / "(HP)" label suffix for the preferred variant.
                string key = "(" + pref.ToString() + ")";
                Def byType = ammoDefs.FirstOrDefault(a => a.label != null && a.label.IndexOf(key, StringComparison.OrdinalIgnoreCase) >= 0);
                if (byType != null) return byType;
            }

            // No variant of the chosen type (e.g. a bow when FMJ is selected): pick the plain, effect-free round rather
            // than a generic placeholder - the cheapest concrete ammo with no special ability (e.g. a stone arrow),
            // falling back to the cheapest of any concrete ammo, then whatever is first.
            List<ThingDef> concrete = ammoDefs.OfType<ThingDef>().ToList();
            if (concrete.Count > 0)
            {
                ThingDef normal = concrete
                    .Where(a => GetAmmoEffects(a) == SpecialAbility.None)
                    .OrderBy(a => a.BaseMarketValue)
                    .FirstOrDefault();
                if (normal != null) return normal;
                ThingDef cheapest = concrete.OrderBy(a => a.BaseMarketValue).FirstOrDefault();
                if (cheapest != null) return cheapest;
            }
            return ammoDefs[0];
        }

        // Representative fixed ingredients of a recipe (one ThingDef + count per ingredient, using the filter's default
        // allowed def). Pass the chosen `stuff` for a made-from-stuff item so its open material ingredient shows (and
        // stock-checks) the material the player actually picked, not an arbitrary allowed one - fixed ingredients
        // (exactly one allowed def, e.g. components) are never substituted.
        public static List<ThingDefCountClass> GetRecipeIngredients(RecipeDef r, ThingDef stuff = null)
        {
            List<ThingDefCountClass> list = new List<ThingDefCountClass>();
            if (r == null || r.ingredients == null) return list;
            foreach (IngredientCount ing in r.ingredients)
            {
                ThingDef def = null;
                if (stuff != null && ing.filter != null && !ing.IsFixedIngredient && ing.filter.Allows(stuff)) def = stuff;
                if (def == null) def = ing.filter != null ? ing.filter.AnyAllowedDef : null;
                if (def == null) continue;
                list.Add(new ThingDefCountClass(def, (int)ing.GetBaseCount()));
            }
            // Stable order so the icons never shuffle between rows/frames.
            list.Sort((a, b) => string.Compare(a.thingDef.defName, b.thingDef.defName, StringComparison.Ordinal));
            return list;
        }

        // How affordable a weapon is to craft RIGHT NOW, from actual current ingredient stock (not abstract market
        // value). 1 = every ingredient is fully in stock for at least one craft; 0 = the scarcest ingredient is
        // completely unavailable. No recipe / no ingredients (e.g. some self-ammo items) -> 1 (nothing to be short
        // of). Bottlenecked on the SCARCEST ingredient, not averaged - plenty of steel doesn't help if components
        // are at zero. Shared signal behind the "intelligent core, slider = tipping point" sliders (Pool/Craft/
        // Budget) - see WORKFLOW_OVERHAUL_PLAN.md sections 6a/6b/6d.
        private static readonly Dictionary<ThingDef, float> affordabilityCache = new Dictionary<ThingDef, float>();
        private static float affordabilityCacheTime = -99f;

        public static float MaterialAffordability(ThingDef weapon)
        {
            // The popup's slider resolvers re-score their candidate pools every frame, and each score consults this
            // (recipe ingredient list + stock counts). Stock barely moves inside a force-paused window - a 2s TTL
            // turns per-frame allocation churn into a handful of lookups.
            if (Time.realtimeSinceStartup - affordabilityCacheTime > 2f)
            {
                affordabilityCache.Clear();
                affordabilityCacheTime = Time.realtimeSinceStartup;
            }
            if (affordabilityCache.TryGetValue(weapon, out float cachedAfford)) return cachedAfford;

            float result = ComputeMaterialAffordability(weapon);
            affordabilityCache[weapon] = result;
            return result;
        }

        private static float ComputeMaterialAffordability(ThingDef weapon)
        {
            RecipeDef recipe = GetRecipeFor(weapon);
            List<ThingDefCountClass> ings = GetRecipeIngredients(recipe);
            if (ings.Count == 0) return 1f;
            float min = 1f;
            foreach (ThingDefCountClass ing in ings)
            {
                float ratio = ing.count > 0 ? Mathf.Clamp01((float)IngredientStockCount(ing.thingDef) / ing.count) : 1f;
                if (ratio < min) min = ratio;
            }
            return min;
        }

        // Was diese Waffe an MATERIAL kostet, in Silberwert der Zutaten. Ausdruecklich das Gegenstueck zu
        // MaterialAffordability darueber, und nicht dasselbe: Affordability fragt "haben wir genug davon",
        // Kosten fragt "wie teuer ist das Stueck". In einer rohstoffreichen Kolonie steht Affordability fuer
        // JEDE Waffe auf 1.0 - vom billigen Revolver bis zum schweren MG -, und weil der Regler
        // "Guenstige Waffen <-> Beste Waffe" bis zum 2026-08-14 ausschliesslich an dieser Zahl haengte,
        // hatte er dort exakt keine Wirkung mehr. Genau das war der Befund: Economy gewaehlt und
        // trotzdem Kord-MG, RPG-7 und Brandwerfer eingeplant, 642 Stahl und 132 Bauteile Baukosten.
        // Nur Materialwert, keine Arbeitszeit: die Arbeit einer Kolonie ist nicht das, was der Spieler beim
        // Wort "guenstig" meint, und workAmount ist zwischen Mods so unterschiedlich skaliert, dass es die
        // Reihenfolge mehr verrauschen als schaerfen wuerde.
        // Dauerhaft gecacht (kein TTL wie oben): Rezepte und Marktwerte sind Defs und aendern sich zur
        // Laufzeit nicht - nur der BESTAND tut das, und der steckt in MaterialAffordability, nicht hier.
        private static readonly Dictionary<ThingDef, float> craftCostCache = new Dictionary<ThingDef, float>();

        public static float WeaponCraftCost(ThingDef weapon)
        {
            if (weapon == null) return 0f;
            if (craftCostCache.TryGetValue(weapon, out float cached)) return cached;
            float total = 0f;
            foreach (ThingDefCountClass ing in GetRecipeIngredients(GetRecipeFor(weapon)))
            {
                if (ing?.thingDef == null || ing.count <= 0) continue;
                total += ing.count * ing.thingDef.BaseMarketValue;
            }
            // Kein Rezept hinterlegt (nur handel- oder erbeutbar): der Marktwert der Waffe selbst ist das
            // ehrlichste Ersatzmass. Betrifft im Planer ohnehin fast nichts - ohne Rezept ist `producible`
            // falsch und die Waffe faellt schon vorher aus den Kandidaten, sobald sie nicht auf Lager ist.
            if (total <= 0f) total = weapon.BaseMarketValue;
            craftCostCache[weapon] = total;
            return total;
        }

        // The name a virtual pick's default loadout will get (same prefix rule as the Generate tab). Used by both
        // halves of Dialog_SquadSetup - the generate-side "does this already exist" check and the assign-side
        // virtual-candidate label.
        public static string VirtualLoadoutLabel(ThingDef w)
        {
            var cfg = CEReloadedMod.settings;
            string prefix = (cfg != null && !cfg.useLoadoutPrefix) ? "" : ((cfg != null ? cfg.loadoutPrefix : "R|") + " ");
            return prefix + w.label;
        }

        // Die Waffenliste der HAUPTWAFFEN-Auswahl (Einzelauswahl-Modus von Dialog_PickPacks, 2026-09-02):
        // alle echten Waffen ausser Granaten und Hilfswaffen, Fernkampf vor Nahkampf, in der Gruppe nach
        // Wertung absteigend. Einmal beim Oeffnen bauen und als fertige Liste durchreichen - nicht pro Bild.
        public static List<ThingDef> MainWeaponChoices(ThingDef alwaysInclude = null)
        {
            List<ThingDef> list = new List<ThingDef>();
            foreach (ThingDef d in DefDatabase<ThingDef>.AllDefsListForReading)
            {
                if (!IsRealWeaponDef(d) || IsUtilityWeapon(d) || Dialog_GenerateLoadouts.IsGrenade(d)) continue;
                GetWeaponScore(d, out float sc, out _);
                if (sc <= 0f && d != alwaysInclude) continue;
                list.Add(d);
            }
            return list.OrderBy(d => d.IsRangedWeapon && !d.IsMeleeWeapon ? 0 : 1)
                .ThenByDescending(d => { GetWeaponScore(d, out float s2, out _); return s2; })
                .ThenBy(d => d.label).ToList();
        }

        // Waffen, zu denen noch KEINE Standard-Vorlage existiert - die Waffen-Sektion der Auswahlfenster
        // des Lazy-Umbaus (2026-09-01). Dedup ueber die Label-Identitaet (VirtualLoadoutLabel), dieselbe
        // Regel wie EnsureDefaultLoadout selbst: existiert die Vorlage schon, steht sie als Loadout-Zeile
        // in der jeweiligen Liste, die Waffe hier doppelt anzubieten waere dieselbe Sache zweimal.
        public static List<ThingDef> WeaponsWithoutTemplate(bool includeGrenades = true, ThingDef exclude = null)
        {
            HashSet<string> labels = new HashSet<string>(LoadoutManager.Loadouts
                .Where(l => l != null && !l.defaultLoadout && l.label != null).Select(l => l.label));
            List<ThingDef> result = new List<ThingDef>();
            foreach (ThingDef d in DefDatabase<ThingDef>.AllDefsListForReading)
            {
                if (!IsRealWeaponDef(d) || IsUtilityWeapon(d) || d == exclude) continue;
                if (!includeGrenades && Dialog_GenerateLoadouts.IsGrenade(d)) continue;
                if (labels.Contains(VirtualLoadoutLabel(d))) continue;
                // Gesperrte Waffen: die Checkliste bietet sie gar nicht erst an -
                // entsperren geht im Hauptwaffen-Fenster, das die Sperr-Schalter traegt.
                if (IsWeaponDisabled(d)) continue;
                result.Add(d);
            }
            result.SortBy(d => d.label);
            return result;
        }

        // What ammo do these pawns' ACTUAL assigned loadouts specify? Closes a real, confirmed gap: nothing in the
        // Assign flow (manual or auto) ever checked ammo before this - a colonist could get a freshly-crafted
        // weapon and never have ammo production queued for it, with no popup ever mentioning it (WORKFLOW_OVERHAUL_
        // PLAN.md section 7). Convenience overload for the common case where the loadout is already live on the pawn.
        public static void ComputeAmmoNeedForAssignedPawns(IEnumerable<Pawn> pawns,
            out Dictionary<LoadoutGenericDef, int> generics, out Dictionary<ThingDef, int> specifics,
            out Dictionary<Def, List<string>> usedBy)
        {
            ComputeAmmoNeedForLoadouts(pawns.Where(p => p != null).Select(p => (p.GetLoadout(), p.LabelShortCap)),
                out generics, out specifics, out usedBy);
        }

        // Scans every colonist's REAL, CURRENT assigned loadout (main weapon + every sidearm) for anything not
        // already carried/owned and with no active bill yet - independent of any specific wizard's in-memory
        // plan, unlike the Squad Setup/Arm-my-Colony flows which build their own request from a preview. Used
        // by the "did I forget to produce something for what I just assigned?" check on closing
        // (Dialog_CEReloadedTabs' own close hook, 2026-07-30) - covers weapons/sidearms; ammo has its own
        // ComputeAmmoNeedForAssignedPawns above, packs are a separate flow (different item categories,
        // routed through Dialog_GenerateEquipmentPack instead) and are NOT covered here yet.
        public static void ScanMissingAssignedWeapons(out Dictionary<ThingDef, int> missingMain,
            out Dictionary<ThingDef, int> missingSidearms, out Dictionary<ThingDef, List<string>> usedBy)
        {
            missingMain = new Dictionary<ThingDef, int>();
            missingSidearms = new Dictionary<ThingDef, int>();
            Dictionary<ThingDef, List<string>> usedByLocal = new Dictionary<ThingDef, List<string>>();

            // Quantity-aware, not the old binary "any bill exists at all" gate (2026-07-31 fix) - that used to
            // suppress EVERY colonist's need for a weapon the instant a SINGLE bill for it existed anywhere,
            // even a bill for just 1 when 5 colonists needed one. Shared across main+sidearm calls for the
            // same def - one Bill_Production's queued pile is fungible regardless of which role wants it.
            // Same "claim what's already queued, one unit per occurrence" idea as Dialog_SupplyLoadouts.
            // BuildWeaponEntry's own remaining = count - QueuedCount(recipe).
            Dictionary<ThingDef, int> queuedRemaining = new Dictionary<ThingDef, int>();
            // Same idea for UNCLAIMED STOCK already sitting in a stockpile - a pawn who was JUST assigned a
            // weapon this exact tick doesn't physically carry it yet (equipping is a real in-game job that
            // takes a moment to run), so PawnAlreadyHasItem alone always reads "missing" for a split second
            // after every bulk auto-assign, even when the colony already owns plenty and nobody needs to
            // craft anything at all - this used to send Arm my Colony's own end-of-run safety check straight
            // into proposing to build MORE of a weapon the Availability bar itself showed as fully covered
            // (2026-07-31: it wanted to build all kinds of things - screenshots showed Knife at 4/4
            // available yet still checked for production).
            Dictionary<ThingDef, int> stockRemaining = new Dictionary<ThingDef, int>();

            // usedBy = Bedarfssemantik, nicht Fehlmengenrest (2026-08-31, dieselbe Korrektur wie in
            // ComputeMissingForCreatedLoadouts.Track): die Raute soll sagen, WEM das Ding zugeteilt ist -
            // auch wenn Lager oder Traeger den Bedarf laengst decken. Vorher wurde der Name erst hinter
            // allen Abzuegen notiert, eine voll versorgte Zeile stand also als "niemandem zugeteilt" da.
            void Note(ThingDef w, Pawn p)
            {
                if (w == null || p == null) return;
                if (!usedByLocal.TryGetValue(w, out List<string> list)) usedByLocal[w] = list = new List<string>();
                if (!list.Contains(p.LabelShortCap)) list.Add(p.LabelShortCap);
            }

            // alreadyCounted: der Aufrufer hat den getragenen Bestand schon abgezogen (Mengen-Pfad unten).
            // Ohne diesen Ausgang wuerde die binaere Frage "traegt er ueberhaupt eines" jede weitere Einheit
            // verschlucken - wer vier Satchel charges braucht und eine traegt, braeuchte laut dieser Zeile
            // gar keine mehr (2026-08-16 beim Gegenlesen des Mengen-Pfads gefunden).
            void Track(Dictionary<ThingDef, int> dict, ThingDef w, Pawn p, bool alreadyCounted = false)
            {
                if (w == null) return;
                Note(w, p);
                if (!alreadyCounted && PawnAlreadyHasItem(p, w)) return;
                // WeaponStoredCount, not IngredientStockCount (2026-08-02 fix: Knife with 4
                // sitting in storage - 2 on a display shelf, 2 on a weapon rack - still got queued for more
                // production) - see WeaponStoredCount's own comment for why IngredientStockCount undercounts
                // weapons specifically.
                if (!stockRemaining.TryGetValue(w, out int stock)) stock = WeaponStoredCount(w);
                if (stock > 0) { stockRemaining[w] = stock - 1; return; }
                stockRemaining[w] = 0;
                if (!queuedRemaining.TryGetValue(w, out int rem)) rem = WeaponQueuedCount(w);
                if (rem > 0) { queuedRemaining[w] = rem - 1; return; }
                queuedRemaining[w] = 0;
                dict[w] = (dict.TryGetValue(w, out int c) ? c : 0) + 1;
            }

            foreach (Pawn p in AllColonyPawns())
            {
                Loadout raw = p.GetLoadout();
                if (raw == null || raw.defaultLoadout) continue;
                Loadout display = AmmoAssignmentUtility.DisplayLoadout(raw);
                if (display == null) continue;
                // Vor dem None-Zweig deklariert, weil TrackAllWeaponSlots (lokale Funktion unten) es einfaengt.
                Dictionary<ThingDef, int> sidearmSink = missingSidearms;
                if (display.defaultLoadout)
                {
                    // Zweitwaffen ueber CEs None-Loadout (2026-08-25): seit die Zweitwaffen-Zelle
                    // auch ohne Hauptwaffe arbeitet, haengt so eine Waffe am Anker ueber "None" - keine
                    // Hauptwaffe zu zaehlen, die Zweitwaffen aber sehr wohl, sonst fehlt die Hellebarde in
                    // jedem Nachschub-Fenster, waehrend die Zelle "fehlt" sagt.
                    foreach (Loadout sideLoadout in SidearmAssignmentUtility.GetPawnSidearms(p) ?? new List<Loadout>())
                        TrackAllWeaponSlots(sideLoadout, null);
                    continue;
                }
                LoadoutMainAndSide(display, out ThingDef mainW, out _);
                Track(missingMain, mainW, p);
                // JEDER Waffenslot, nicht nur die eine "Hauptwaffe" pro Vorlage (2026-08-16: im neuen Fenster
                // fehlten Posten, z.B. die Satchel Charge - der Bedarf wurde nicht richtig berechnet).
                // Die Ursache ist die DRITTE Wiederholung
                // derselben Sache: LoadoutMainAndSide waehlt bewusst NIE eine Granate, einen
                // Wurfsprengstoff, ein Schild oder eine Hilfswaffe als "Hauptwaffe" (siehe dessen
                // Kommentar). Eine Zweitwaffen-Vorlage, die genau so etwas enthaelt - der Normalfall fuer
                // Wurfsprengstoff -, lieferte hier `null`, und damit tauchte der Bedarf im Nachschub-Fenster
                // ueberhaupt nicht auf.
                // Dieselbe Korrektur hat ComputeMissingForCreatedLoadouts am 2026-08-01 bekommen und
                // ComputeWeaponDemand am 2026-08-13 - nur die ZUGEWIESEN-Sicht hier ist beide Male nicht
                // mitgezogen worden. Genau deshalb konnten Bedarfs- und Aufraeumseite verschiedener Meinung
                // sein; dieselbe Menge Waffenslots ist die Voraussetzung dafuer, dass sie sich einig sind.
                // Praedikat woertlich wie in ComputeWeaponDemand.AddAllWeapons: IsWeapon, keine Hilfswaffen.
                // Ueber eine lokale Referenz auf dasselbe Woerterbuch: C# laesst einen out-Parameter in einer
                // lokalen Funktion nicht zu (CS1628), und das Woerterbuch ist ohnehin ein Verweis.
                void TrackAllWeaponSlots(Loadout lo, ThingDef skip)
                {
                    if (lo?.Slots == null) return;
                    foreach (LoadoutSlot s in lo.Slots)
                    {
                        // Praedikat WOERTLICH wie in ComputeMissingForCreatedLoadouts (Waffen und Schilde):
                        // beide Sichten fuellen dasselbe Fenster, und ein Unterschied zwischen ihnen waere
                        // ein eigener Fehler - der Bereichs-Schalter wuerde dann die Liste aus Gruenden
                        // aendern, die mit dem Bereich nichts zu tun haben. Erste Fassung dieser Zeile hatte
                        // stattdessen das Praedikat von ComputeWeaponDemand kopiert (ohne Schilde, dafuer
                        // ohne Hilfswaffen) - beim Gegenlesen aufgefallen.
                        if (s.thingDef == null || (!s.thingDef.IsWeapon && !IsShieldApparel(s.thingDef))) continue;
                        // Die Hauptwaffe ist eine Zeile darueber schon gezaehlt - sonst stuende sie doppelt.
                        if (skip != null && s.thingDef == skip) { skip = null; continue; }
                        // Die MENGE zaehlt mit, auch dafuer ist die Created-Sicht das Vorbild: vier Satchel
                        // charges in einem Slot sind vier Stueck Bedarf, nicht eines. Track beansprucht pro
                        // Aufruf genau eine Einheit aus Lager bzw. laufender Produktion - also so oft rufen,
                        // wie noch fehlt. Getragenes zuerst abziehen (PawnCarriedCount zaehlt Ausruestung,
                        // Inventar und Getragenes zusammen), denn Tracks eigene Traegerpruefung ist binaer
                        // und wuerde bei einem einzigen getragenen Stueck den ganzen Rest verschlucken.
                        int want = Mathf.Max(1, s.count);
                        int carried = PawnCarriedCount(p, s.thingDef);
                        // Note auch bei carried >= want (2026-08-31): traegt er schon alles, laeuft die
                        // Schleife nullmal und Track kaeme nie dran - zugeteilt ist es ihm trotzdem.
                        Note(s.thingDef, p);
                        for (int k = carried; k < want; k++) Track(sidearmSink, s.thingDef, p, alreadyCounted: true);
                    }
                }
                TrackAllWeaponSlots(display, mainW);
                foreach (Loadout sideLoadout in SidearmAssignmentUtility.GetPawnSidearms(p) ?? new List<Loadout>())
                    TrackAllWeaponSlots(sideLoadout, null);
            }
            usedBy = usedByLocal;
        }

        // Seit 2026-09-07 fuellt das hier NUR noch die Herkunftsspalten (templateUsedBy - "welche Vorlage
        // braucht das") - der Bereichs-Schalter "Consider created loadouts", fuer den es 2026-08-01 gebaut
        // wurde, ist entfallen (seit dem Lazy-Umbau dieselbe Menge wie "assigned").
        // "Created" here means: one unit of demand per real, managed template
        // (Weapon Loadout or Equipment Pack, not the default loadout, not a hidden per-pawn anchor) -
        // regardless of whether any colonist is currently using it - so a freshly built but not-yet-assigned
        // template still shows up as needing production.
        // Covers EVERY weapon-or-shield slot a template carries, not just its single "main" pick (2026-08-01,
        // correction to an earlier version of this comment: a sidearm-only candidate loadout -
        // e.g. a grenade-only "W| Frag grenade" or a shield-only "W| Riot shield", exactly what
        // SidearmAssignmentUtility.CandidateLoadouts offers as a real, pickable sidearm - is still a real,
        // producible Loadout template someone might want stocked ahead of time; it just never wins
        // LoadoutMainAndSide's own "which one weapon is THE main weapon" pick, since that deliberately
        // excludes grenades/shields/utility weapons from being anyone's main gun. Anything not chosen as
        // main is routed into missingSidearms instead, matching the "Sidearms & grenades" section the
        // ASSIGNED-scope scan already shows the exact same mix of content under).
        public static void ComputeMissingForCreatedLoadouts(
            out Dictionary<ThingDef, int> missingMain, out Dictionary<ThingDef, int> missingSidearms,
            out Dictionary<ThingDef, List<string>> weaponUsedBy,
            out Dictionary<ThingDef, int> missingPackItems, out Dictionary<ThingDef, List<string>> packUsedBy,
            out Dictionary<LoadoutGenericDef, int> genericAmmo, out Dictionary<ThingDef, int> specificAmmo,
            out Dictionary<Def, List<string>> ammoUsedBy, out Dictionary<ThingDef, Def> packDemandDefs)
        {
            List<Loadout> weaponTemplates = LoadoutManager.Loadouts.Where(l => l != null && !l.defaultLoadout
                && !AmmoAssignmentUtility.IsAmmoLink(l) && !EquipmentPackUtility.IsEquipmentPackLoadout(l)).ToList();
            List<Loadout> packTemplates = LoadoutManager.Loadouts.Where(l => l != null
                && EquipmentPackUtility.IsEquipmentPackLoadout(l)).ToList();

            // Same claim-and-decrement order as ScanMissingAssignedWeapons' own Track closure (stock first,
            // then already-queued production, only THEN a real shortfall) - just without the
            // PawnAlreadyHasItem pre-check, since a template has no pawn to already be holding one.
            missingMain = new Dictionary<ThingDef, int>();
            missingSidearms = new Dictionary<ThingDef, int>();
            Dictionary<ThingDef, List<string>> weaponUsedByLocal = new Dictionary<ThingDef, List<string>>();
            Dictionary<ThingDef, int> stockRemaining = new Dictionary<ThingDef, int>();
            Dictionary<ThingDef, int> queuedRemaining = new Dictionary<ThingDef, int>();
            void Track(Dictionary<ThingDef, int> dict, ThingDef w, int count, Loadout l)
            {
                if (w == null) return;
                // usedBy VOR dem Lager-/Produktionsabzug (2026-08-31, im Test):
                // die Liste beantwortet "welche Vorlage NUTZT das" - eine Bedarfs-, keine Fehlmengenfrage.
                // Vorher wurde der Name erst hinter beiden Abzuegen notiert; eine voll gedeckte Waffe
                // (Satchel charge: 4 gebraucht, 5 im Regal) stand deshalb mit leerer Liste da und der
                // Kreis-Punkt behauptete "No saved loadout uses this ammo", waehrend dieselbe Rolle zwei
                // Zeilen weiter (KS-23-Munition, aus ComputeAmmoNeedForLoadouts, das immer beim BEDARF
                // notiert) korrekt genannt wurde. Alle Abnehmer machen nur TryGetValue pro Zeile - mehr
                // Eintraege im Woerterbuch aendern keine Menge und keine Zeilenauswahl.
                if (!weaponUsedByLocal.TryGetValue(w, out List<string> list)) weaponUsedByLocal[w] = list = new List<string>();
                // Diese Listen werden dem Spieler gezeigt ("wird gebraucht von ..."), also der ANGEZEIGTE
                // Name und nicht das gespeicherte Etikett (2026-08-18) - sonst steht in genau diesen
                // Aufzaehlungen weiter "[Role] Foo", waehrend die Zeile daneben "Foo" sagt.
                string lName = LoadoutDisplayName(l);
                if (!list.Contains(lName)) list.Add(lName);
                // WeaponStoredCount, not IngredientStockCount - see ScanMissingAssignedWeapons' own Track
                // closure and WeaponStoredCount's comment for the full story (2026-08-02 fix).
                if (!stockRemaining.TryGetValue(w, out int stock)) stock = WeaponStoredCount(w);
                int fromStock = Mathf.Min(stock, count);
                stockRemaining[w] = stock - fromStock;
                int stillNeeded = count - fromStock;
                if (stillNeeded <= 0) return;
                if (!queuedRemaining.TryGetValue(w, out int rem)) rem = WeaponQueuedCount(w);
                int fromQueue = Mathf.Min(rem, stillNeeded);
                queuedRemaining[w] = rem - fromQueue;
                stillNeeded -= fromQueue;
                if (stillNeeded <= 0) return;
                dict[w] = (dict.TryGetValue(w, out int c) ? c : 0) + stillNeeded;
            }
            foreach (Loadout l in weaponTemplates)
            {
                LoadoutMainAndSide(l, out ThingDef mainW, out _);
                if (mainW != null) Track(missingMain, mainW, 1, l);
                if (l.Slots == null) continue;
                foreach (LoadoutSlot s in l.Slots)
                {
                    if (s.thingDef == null || s.thingDef == mainW) continue;
                    if (!s.thingDef.IsWeapon && !IsShieldApparel(s.thingDef)) continue;
                    Track(missingSidearms, s.thingDef, Mathf.Max(1, s.count), l);
                }
            }
            weaponUsedBy = weaponUsedByLocal;

            // Pack items: same claim-and-decrement idea as ScanMissingAssignedPackItems, sourced from every
            // created Equipment Pack's own OwnSlots directly instead of a pawn's currently-attached packs
            // (that function's own parameterized overload wants a real Pawn per demand source, which a bare
            // template doesn't have - reimplemented here against a loadout label instead).
            Dictionary<Def, int> packDemand = new Dictionary<Def, int>();
            Dictionary<Def, List<string>> packDemandedBy = new Dictionary<Def, List<string>>();
            foreach (Loadout l in packTemplates)
            {
                if (l.OwnSlots == null) continue;
                foreach (LoadoutSlot s in l.OwnSlots)
                {
                    Def d = (Def)s.thingDef ?? s.genericDef;
                    if (d == null) continue;
                    packDemand[d] = (packDemand.TryGetValue(d, out int c) ? c : 0) + s.count;
                    if (!packDemandedBy.TryGetValue(d, out List<string> names)) packDemandedBy[d] = names = new List<string>();
                    string pName = LoadoutDisplayName(l);
                    if (!names.Contains(pName)) names.Add(pName);
                }
            }
            missingPackItems = new Dictionary<ThingDef, int>();
            Dictionary<ThingDef, List<string>> packUsedByLocal = new Dictionary<ThingDef, List<string>>();
            Dictionary<ThingDef, Def> packDemandDefLocal = new Dictionary<ThingDef, Def>();
            Dictionary<ThingDef, int> remainingConcretePackStock = new Dictionary<ThingDef, int>();
            int RemainingPackStockFor(ThingDef td)
            {
                if (!remainingConcretePackStock.TryGetValue(td, out int r))
                {
                    ComputeItemOwnership(td, out int co, out int ca, out int ais, out int fs, out int ip);
                    r = co + ca + ais + fs + ip;
                }
                return r;
            }
            foreach (var kv in packDemand.OrderByDescending(kv => kv.Key is ThingDef))
            {
                List<ThingDef> family = kv.Key is ThingDef ctd ? new List<ThingDef> { ctd } : GenericFamily(kv.Key as LoadoutGenericDef);
                int need = kv.Value;
                int have = 0;
                foreach (ThingDef member in family)
                {
                    if (have >= need) break;
                    int r = RemainingPackStockFor(member);
                    int take = Mathf.Min(r, need - have);
                    if (take <= 0) continue;
                    remainingConcretePackStock[member] = r - take;
                    have += take;
                }
                ThingDef concrete = kv.Key as ThingDef ?? ResolveGenericPackItem(kv.Key as LoadoutGenericDef);
                if (concrete == null) continue;
                // usedBy auch bei voller Deckung (2026-08-31) - dieselbe Korrektur wie in Track oben:
                // "welches Pack nutzt das" ist eine Bedarfsfrage, kein Fehlmengenrest.
                if (!packUsedByLocal.TryGetValue(concrete, out List<string> list)) packUsedByLocal[concrete] = list = new List<string>();
                foreach (string n in packDemandedBy[kv.Key]) if (!list.Contains(n)) list.Add(n);
                int shortfall = need - have;
                if (shortfall <= 0) continue;
                missingPackItems[concrete] = (missingPackItems.TryGetValue(concrete, out int mc) ? mc : 0) + shortfall;
                RecordPackDemandDef(packDemandDefLocal, concrete, kv.Key);
            }
            packUsedBy = packUsedByLocal;
            packDemandDefs = packDemandDefLocal;

            // Ammo: ComputeAmmoNeedForLoadouts already takes (loadout, name) pairs generically (built for the
            // deferred pre-apply Assign flow) - reused as-is with every weapon template's own label as the name.
            ComputeAmmoNeedForLoadouts(weaponTemplates.Select(l => (l, LoadoutDisplayName(l))), out genericAmmo, out specificAmmo, out ammoUsedBy);
        }

        // "Did I forget to produce something I assigned?" check - scans the CURRENT actual colony state
        // (every colonist's real assigned main weapon, sidearms, and ammo) and opens the shared Supply popup
        // if anything producible is missing. Independent of any specific window's edits, so it also catches
        // older gaps, not just ones made just now. Replaces the old per-dialog "Produce missing ammo..."
        // button entirely (2026-07-30: a manual button is easy to forget, and NOT
        // wanting something you just assigned to actually be produced is the rare exception - Cancel in the
        // popup itself already covers that case). Hooked into BOTH Dialog_CEReloadedTabs' close (the normal
        // "done configuring" moment) AND Dialog_ChangeAmmo's own close (it can be opened standalone from the
        // map gizmo without the tabs window ever being open). Equipment Packs ARE covered too (2026-07-30) -
        // ScanMissingAssignedPackItems below folds their items (medicine/food/drugs/utility) into the same
        // popup request, even though generating a NEW pack template is still its own separate flow
        // (Dialog_GenerateEquipmentPack).
        // Umfang: immer die zugewiesenen Waffen (jedes Kolonisten ECHTE aktuelle Zuweisung). Der Schalter
        // "Consider created loadouts" (2026-08-01 bis 2026-09-07) ist entfallen - siehe Dialog_SupplyLoadouts.Request.
        // "Hat sich seit dem Oeffnen ueberhaupt etwas geaendert, das den Nachschub-Dialog beruehrt?"
        // (2026-08-09). Ein Fenster nimmt beim Oeffnen diese Signatur,
        // vergleicht sie beim Schliessen und ruft CheckAndOpenMissingProductionPopup nur bei Abweichung -
        // sonst geht der Dialog auf, obwohl der Spieler nur hineingesehen hat.
        //
        // Enthalten ist genau das, was der Spieler in diesen Fenstern aendern kann UND was den Inhalt des
        // Dialogs bestimmt: wer welches Loadout traegt und was darin steht. Slots laeuft ueber die ganze
        // Elternkette, Packs, Sidearms und der persoenliche Munitions-Anhang zaehlen also mit.
        // BEWUSST NICHT enthalten: der Lagerbestand und laufende Auftraege. Beide aendern sich von selbst,
        // waehrend das Fenster offen ist, und beide koennen den Dialog nur KLEINER machen - hat sich der
        // Rueckstand von allein erledigt, oeffnet CheckAndOpenMissingProductionPopup ohnehin nichts.
        // Sortiert (Pawn-Id, dann Def-Name), weil Aufzaehlungsreihenfolge kein Inhalt ist.
        // Der Stand, fuer den zuletzt WIRKLICH ein Nachschub-Fenster aufgegangen ist - gesetzt im
        // Konstruktor von Dialog_SupplyLoadouts, gelesen beim Reiterwechsel. Absichtlich nicht gespeichert:
        // nach einem Neustart ist ein Angebot wieder in Ordnung, die Frage ist dann ja neu.
        public static string lastSupplyOfferSignature;

        public static string AssignmentSignature()
        {
            var sb = new System.Text.StringBuilder();
            foreach (Pawn p in AllColonyPawns().OrderBy(x => x.thingIDNumber))
            {
                sb.Append(p.thingIDNumber).Append(':');
                Loadout lo = p.GetLoadout();
                if (lo == null || lo.defaultLoadout) { sb.Append("-;"); continue; }
                sb.Append(lo.UniqueID).Append('=');
                if (lo.Slots != null)
                    foreach (LoadoutSlot s in lo.Slots.OrderBy(x => x.thingDef?.defName ?? x.genericDef?.defName ?? "", StringComparer.Ordinal))
                        sb.Append(s.thingDef?.defName ?? s.genericDef?.defName ?? "?").Append('*').Append(s.count).Append(',');
                sb.Append(';');
            }
            return sb.ToString();
        }

        public static void CheckAndOpenMissingProductionPopup()
        {
            // Don't stack a second copy on top of one already open (2026-07-31 fix) - several dialogs in the
            // chain each call this from their own PostClose (Dialog_ChangeAmmo, Dialog_CEReloadedTabs,
            // Dialog_ArmReport, Dialog_ArmRolesStep...), so closing an inner one can trigger this again while
            // the popup it already opened is still sitting on the WindowStack, unchanged - re-showing an
            // effectively identical popup right after the player already dismissed it once.
            if (Find.WindowStack.IsOpen<Dialog_SupplyLoadouts>()) return;

            Dictionary<ThingDef, int> missingMain, missingSidearms, missingPackItems;
            Dictionary<LoadoutGenericDef, int> generics;
            Dictionary<ThingDef, int> specifics;

            // Both indicator sources are ALWAYS computed now, regardless of scope (2026-08-02) - the "Assig."
            // column used to be a single dict-set whose MEANING silently flipped between "real pawn" and
            // "loadout template" depending on this scope flag (two screenshots of the same
            // colonist showing wildly different numbers). Only the visible DEMAND numbers (missingMain etc.
            // below) still depend on scope; the diamond/circle indicators are independent of it.
            ComputeMissingForCreatedLoadouts(out var createdMain, out var createdSidearms, out var createdWeaponUsedBy,
                out var createdPackItems, out var createdPackUsedBy, out var createdGenerics, out var createdSpecifics, out var createdAmmoUsedBy, out var createdPackDemandDefs);
            List<Pawn> allPawns = AllColonyPawns().ToList();
            ComputeAmmoNeedForAssignedPawns(allPawns, out var assignedGenerics, out var assignedSpecifics, out var assignedAmmoUsedBy);
            ScanMissingAssignedWeapons(out var assignedMain, out var assignedSidearms, out var assignedWeaponUsedBy);
            ScanMissingAssignedPackItems(AllColonyPawns().Select(p => (p, AmmoAssignmentUtility.GetPawnPacks(p))),
                out var assignedPackItems, out var assignedPackUsedBy, out var assignedPackDemandDefs);

            // Die Herkunfts-Defs kommen aus DEMSELBEN Umfang wie die Fehlmengen - sonst traegt eine Zeile
            // aus dem Zugewiesen-Umfang die Herkunft aus dem Vorlagen-Umfang.
            // Nur noch der Zugewiesen-Umfang (2026-09-07): seit dem Lazy-Umbau gibt es je benutzter Waffe
            // eine Vorlage, "erzeugt" und
            // "zugewiesen" waren dieselbe Menge. Die Vorlagen-Rechnung darueber bleibt - sie fuellt die
            // Herkunftsspalten (templateUsedBy), nicht die Fehlmengen.
            Dictionary<ThingDef, Def> packDemandDefs;
            missingMain = assignedMain; missingSidearms = assignedSidearms; missingPackItems = assignedPackItems;
            generics = assignedGenerics; specifics = assignedSpecifics;
            packDemandDefs = assignedPackDemandDefs;

            Dictionary<ThingDef, List<string>> weaponAssignedUsedBy = new Dictionary<ThingDef, List<string>>(assignedWeaponUsedBy);
            foreach (var kv in assignedPackUsedBy) weaponAssignedUsedBy[kv.Key] = kv.Value;
            Dictionary<ThingDef, List<string>> weaponTemplateUsedBy = new Dictionary<ThingDef, List<string>>(createdWeaponUsedBy);
            foreach (var kv in createdPackUsedBy) weaponTemplateUsedBy[kv.Key] = kv.Value;
            // Same "only what Dialog_SupplyLoadouts could actually act on" filter the old, now-removed
            // ammo-only popup used - an unresearched-only gap must never pop this open. Quantity-aware
            // (2026-07-31 fix, mirrors ScanMissingAssignedWeapons' own same-day fix) - the old binary
            // WeaponHasActiveBill(kv.Key) suppressed the WHOLE shortfall the instant ANY bill for that ammo
            // existed, even a 1-unit bill left over from before 5 more colonists got assigned it. Compares the
            // real total demand against the existing bill's own TargetCount instead - kv.Value stays the FULL
            // demand (not a delta) since AddOrUpdateAmmoBill sets a bill's targetCount as an absolute replace,
            // not an additive top-up.
            // ALSO now nets out already-STORED stock, not just queued production (2026-07-31, same finding
            // as ScanMissingAssignedWeapons' own fix above) - this raw `specifics` demand comes straight from
            // ComputeAmmoNeedForLoadouts, which is pure want with no supply-side check at all; without also
            // subtracting IngredientStockCount here, self-ammo "ammo" like satchel charges/stick bombs (and any
            // firearm ammo already fully stockpiled) kept showing up as "missing" and pre-checked for MORE
            // production the instant no bill happened to exist yet, even with a full stockpile sitting right
            // there (screenshots: satchel charge/stick bomb at 4/4 available, still proposed for crafting).
            // Value stays the FULL demand (bill targetCount is an absolute replace, per the comment above) -
            // only the FILTER (does this even belong in the popup at all) accounts for stock, not the amount
            // requested once it does.
            // AmmoOnHand statt IngredientStockCount (2026-08-13): VIERTE Fundstelle desselben Tors, per Grep
            // gefunden - und die unangenehmste, weil dieses Popup beim Schliessen des Mod-Fensters von SELBST
            // aufgeht. Boden- und getragener Bestand fehlten hier genauso, also konnte es sich melden fuer
            // Munition, die daneben als "alles gedeckt" gruen angezeigt wurde.
            var missingSpecificAmmo = specifics.Where(kv => IsAmmoResearched(kv.Key)
                && AmmoOnHand(kv.Key) + WeaponQueuedCount(kv.Key) < kv.Value)
                .ToDictionary(kv => kv.Key, kv => kv.Value);
            var relevantGenericAmmo = generics.Where(kv =>
                GetAmmoTypesFor(GetWeaponForGenericAmmo(kv.Key)).Any(IsAmmoResearched))
                .ToDictionary(kv => kv.Key, kv => kv.Value);

            var producibleMain = missingMain.Where(kv => IsProducible(kv.Key)).ToDictionary(kv => kv.Key, kv => kv.Value);
            var producibleSidearms = missingSidearms.Where(kv => IsProducible(kv.Key)).ToDictionary(kv => kv.Key, kv => kv.Value);
            var produciblePackItems = missingPackItems.Where(kv => IsProducible(kv.Key)).ToDictionary(kv => kv.Key, kv => kv.Value);

            // Fold in consumables that need a standing maintenance bill even though raw current stock alone
            // wouldn't otherwise earn them a row (2026-08-01) - see ConsumablesNeedingMaintenance's own
            // comment. Union, not overwrite - a def already present keeps its (equal or larger) real
            // shortfall value. These still only become a REAL bill once the player hits Confirm on this same
            // popup, same as every other row - no more silent background bill creation.
            foreach (var kv in ConsumablesNeedingMaintenance(specifics))
                if (!missingSpecificAmmo.ContainsKey(kv.Key)) missingSpecificAmmo[kv.Key] = kv.Value;
            foreach (var kv in ConsumablesNeedingMaintenance(missingPackItems, requireConsumable: true))
                if (!produciblePackItems.ContainsKey(kv.Key)) produciblePackItems[kv.Key] = kv.Value;
            foreach (var kv in ConsumablesNeedingMaintenance(missingMain, requireConsumable: true))
                if (!producibleMain.ContainsKey(kv.Key)) producibleMain[kv.Key] = kv.Value;
            foreach (var kv in ConsumablesNeedingMaintenance(missingSidearms, requireConsumable: true))
                if (!producibleSidearms.ContainsKey(kv.Key)) producibleSidearms[kv.Key] = kv.Value;

            // ANZEIGEN: die volle Fehlmenge, auch was sich nicht herstellen laesst. OEFFNEN: weiterhin nur,
            // wenn wirklich etwas Herstellbares dabei ist (siehe die Abbruchbedingung unten, die deshalb auf
            // den producible*-Mengen bleibt).
            // 2026-08-13, im Test: derselbe Fehlbestand (eine SKS ohne
            // Rezept, zweimal zugewiesen, einmal vorhanden) stand im Nachschub-Fenster, wenn es ueber
            // Uebernehmen aufging, und fehlte, wenn es sich beim Schliessen von selbst oeffnete. Ursache war
            // genau dieser Filter: der Schwesterweg (Dialog_AssignLoadouts.OpenProducePopup) filtert nicht.
            // Richtig ist Zeigen - das Fenster hat fuer nicht herstellbare Zeilen eine eigene Darstellung
            // ("can't be crafted"), und die gibt es, weil stilles Weglassen als "nichts fehlt" gelesen wird.
            // Die Oeffnungsbedingung bleibt bewusst unveraendert, damit dieses Popup nicht anfaengt,
            // ungefragt fuer Dinge aufzugehen, gegen die niemand etwas tun kann.
            // 2026-08-13, VIERTE Fundstelle desselben Musters: die
            // Pack-Inhalte hatten den Filter noch. Eine Rolle mit "Any Medicine" meldete im Tooltip
            // "Still missing: 42" und zeigte dazu keine Zeile - weil Kraeutermedizin nicht herstellbar ist
            // (siehe ResolveGenericPackItem). Waffen und Zweitwaffen sind hier seit heute frueh unfiltriert,
            // Pack-Inhalte jetzt genauso.
            var mainForDisplay = new Dictionary<ThingDef, int>(missingMain);
            foreach (var kv in producibleMain) mainForDisplay[kv.Key] = kv.Value;
            var sidearmsForDisplay = new Dictionary<ThingDef, int>(missingSidearms);
            foreach (var kv in producibleSidearms) sidearmsForDisplay[kv.Key] = kv.Value;
            var packItemsForDisplay = new Dictionary<ThingDef, int>(missingPackItems);
            foreach (var kv in produciblePackItems) packItemsForDisplay[kv.Key] = kv.Value;

            var req = new Dialog_SupplyLoadouts.Request
            {
                mainWeapons = mainForDisplay,
                secondaryWeapons = sidearmsForDisplay,
                packItems = packItemsForDisplay,
                packItemDemandDefs = packDemandDefs,
                genericAmmo = relevantGenericAmmo,
                specificAmmo = missingSpecificAmmo,
                assignedUsedBy = assignedAmmoUsedBy,
                templateUsedBy = createdAmmoUsedBy,
                weaponAssignedUsedBy = weaponAssignedUsedBy,
                weaponTemplateUsedBy = weaponTemplateUsedBy,
                // Die Zahl ist schon um laufende Auftraege bereinigt - sie kommt aus
                // ScanMissingAssignedWeapons, das den Abzug seit dem 2026-07-31 selbst macht.
                // Begruendung in voller Laenge am Feld selbst.
                weaponCountsNetOfProduction = true
            };
            // Obsolete-bill cleanup suggestions (2026-08-01) - this is the NORMAL, automatic path (every
            // Dialog_CEReloadedTabs/Dialog_ChangeAmmo close), so it's one of the ones that actually needs to
            // catch "I unchecked 2 of this pawn's 3 ammo types and nobody else used them" - the manual Bill
            // Overview tab button alone never saw that case. Shared with every other caller via
            // AttachObsoleteCleanup - see its own comment for why this isn't hand-rolled here anymore.
            AttachObsoleteCleanup(req);

            // Hier hing bis 2026-08-15 ein Diagnoselog. Ausgebaut, weil
            // der Fall im Test nachgestellt war und das Fenster kam. Eine Zahl daraus ist trotzdem
            // festgehalten: `produciblePackItems` stand auf 0, waehrend die ROHE Fehlmenge 1 war - die
            // Pack-Zeile (Glitterworld medicine) erschien im Fenster ueber einen anderen Weg. Das Tor unten
            // haette sie also allein nicht aufgemacht. Kein Fehler, den man sieht, aber der Grund, warum
            // dieses Tor bei reinen Pack-Aenderungen knapp am Zuhalten war.
            if (producibleMain.Count == 0 && producibleSidearms.Count == 0 && produciblePackItems.Count == 0
                && relevantGenericAmmo.Count == 0 && missingSpecificAmmo.Count == 0
                && req.obsoleteAmmoCandidates.Count == 0 && req.obsoleteWeaponCandidates.Count == 0)
                return;

            Dialog_SupplyLoadouts dlg = new Dialog_SupplyLoadouts(req);
            if (dlg.AnythingToSupply) Find.WindowStack.Add(dlg);
        }

        // Same computation, but keyed directly by (loadout, pawn name) pairs - for contexts where the loadout is
        // about to be assigned and pawn.GetLoadout() doesn't reflect that yet (the deferred pre-apply Assign flow).
        // For each weapon a loadout carries (main or sidearm), reads which of that weapon's ammo defs the loadout
        // specifies with a count &gt; 0 - the same "what ammo belongs to this weapon" resolution GetLoadoutAmmoDefs
        // already provides elsewhere, so this doesn't invent new ammo-matching logic. Output is shaped exactly like
        // the Generate flow's dry-run (generics/specifics dicts), so both routes into the Supply popup look
        // identical; usedBy additionally names which pawns need each ammo type (for the Supply popup's "Used by"
        // column).
        // Reserve-buffer demand curve for a "do until X in stock" ammo maintenance bill (2026-08-02,
        // by design - see ComputeAmmoNeedForLoadouts's own comment for why a flat sum overstates the real
        // need): the first ammoReserveFullWeightPawns pawns sharing a caliber count at full weight (the
        // stockpile should always be able to fully re-equip that many from empty), every pawn beyond that
        // contributes a shrinking marginal share (1/rank harmonic falloff from that point), floored at
        // ammoReserveTrailingFloor so a caliber shared by many pawns never trails off to a token amount.
        // Setting the floor to 1.0 reproduces the old "sum everyone at 100%" behavior exactly. Ranked
        // descending by each pawn's OWN configured count first, so the biggest individual demands claim the
        // full-weight slots (a personalized high-count pawn is guaranteed fully covered before anyone's
        // marginal buffer is computed).
        public static int WeightedAmmoDemand(IEnumerable<int> perPawnCounts)
        {
            CEReloadedSettings st = CEReloadedMod.settings;
            int fullWeightPawns = Mathf.Max(0, st?.ammoReserveFullWeightPawns ?? 2);
            float floor = Mathf.Clamp01(st?.ammoReserveTrailingFloor ?? 0.10f);
            List<int> sorted = perPawnCounts.OrderByDescending(c => c).ToList();
            float total = 0f;
            for (int i = 0; i < sorted.Count; i++)
            {
                int rank = i + 1;
                float weight = rank <= fullWeightPawns ? 1f : Mathf.Max(floor, 1f / (rank - fullWeightPawns + 1));
                total += sorted[i] * weight;
            }
            return Mathf.CeilToInt(total);
        }

        public static void ComputeAmmoNeedForLoadouts(IEnumerable<(Loadout loadout, string pawnName)> pairs,
            out Dictionary<LoadoutGenericDef, int> generics, out Dictionary<ThingDef, int> specifics,
            out Dictionary<Def, List<string>> usedBy)
        {
            // Raw per-pawn counts collected first, weighted (WeightedAmmoDemand) only after every pawn
            // sharing a def is known - NOT reduced to a running total as each pawn is seen, since the
            // weight of any given pawn's contribution depends on their RANK among every OTHER pawn sharing
            // that same ammo def, which isn't known until the whole pass is done.
            Dictionary<Def, List<int>> rawCountsByDef = new Dictionary<Def, List<int>>();
            Dictionary<Def, List<string>> usedByLocal = new Dictionary<Def, List<string>>();

            void TrackRaw(Def ammoDef, int count, string pawnName)
            {
                if (!rawCountsByDef.TryGetValue(ammoDef, out List<int> counts)) rawCountsByDef[ammoDef] = counts = new List<int>();
                counts.Add(count);
                if (!usedByLocal.TryGetValue(ammoDef, out List<string> list)) usedByLocal[ammoDef] = list = new List<string>();
                if (!list.Contains(pawnName)) list.Add(pawnName);
            }

            foreach (var pair in pairs)
            {
                Loadout lo = pair.loadout;
                if (lo == null || lo.defaultLoadout || lo.Slots == null) continue;

                foreach (LoadoutSlot weaponSlot in lo.Slots.Where(s => s.thingDef != null && s.thingDef.IsWeapon))
                {
                    ThingDef w = weaponSlot.thingDef;
                    // Self-ammo weapons (thrown explosives, javelins/pila - IsSelfAmmoWeapon) are the weapon
                    // domain's own concern (ComputeMissingSplit/ScanMissingAssignedWeapons, which net demand
                    // against real owned stock) - tracking them here TOO double-books the exact same
                    // real-world item through two disconnected systems, and this ammo-domain path never
                    // subtracts owned stock at all (correct for a continuously-replenished consumable like
                    // bullets, wrong for a discrete owned item like a grenade). 2026-08-01:
                    // a satchel-charge sidearm demanded by 6 pawns with 2 in stock correctly showed Count 4
                    // under "Sidearms & grenades" (weapon domain) AND a second, wrong Count 6 under
                    // "Ammunition" (this path, ignoring the 2 in stock entirely).
                    if (IsSelfAmmoWeapon(w)) continue;
                    List<Def> ammoFamily = GetLoadoutAmmoDefs(w, false);
                    foreach (Def ammoDef in ammoFamily)
                    {
                        LoadoutSlot ammoSlot = lo.Slots.FirstOrDefault(s =>
                            (ammoDef is ThingDef atd && s.thingDef == atd) || (ammoDef is LoadoutGenericDef agd && s.genericDef == agd));
                        if (ammoSlot != null && ammoSlot.count > 0) TrackRaw(ammoDef, ammoSlot.count, pair.pawnName);
                    }
                }
            }

            Dictionary<LoadoutGenericDef, int> genericsLocal = new Dictionary<LoadoutGenericDef, int>();
            Dictionary<ThingDef, int> specificsLocal = new Dictionary<ThingDef, int>();
            foreach (var kv in rawCountsByDef)
            {
                int weighted = WeightedAmmoDemand(kv.Value);
                if (kv.Key is LoadoutGenericDef gd) genericsLocal[gd] = weighted;
                else if (kv.Key is ThingDef td) specificsLocal[td] = weighted;
            }

            generics = genericsLocal;
            specifics = specificsLocal;
            usedBy = usedByLocal;
        }

        // A colonist who can never be a loadout target: permanently incapable of violence (pacifist backstory or
        // trait) or a baby (always downed, cannot hold a weapon). Children are NOT excluded - the game lets them
        // fight, that stays the player's call. One shared rule so the solver, the readiness scan and the Arm-my-
        // Colony preview can never disagree about who counts.
        public static bool CannotFight(Pawn p)
        {
            if (p == null) return true;
            if (IsExcludedFromArming(p)) return true;
            return CannotFightByNature(p);
        }

        // Die Haelfte von CannotFight, die eine TATSACHE ueber den Pawn ist - ohne das Kennzeichen, das der
        // Spieler selbst gesetzt hat (2026-08-13). Gebraucht, seit das Kennzeichen nicht mehr nur "lass ihn in
        // Ruhe" heisst, sondern "nimm ihm sein Loadout weg" (Variante B):
        // eine Entscheidung ist widerrufbar und darf deshalb im Zeilensymbol weitergeschaltet werden, ein
        // Pazifist oder ein Baby nicht. "CannotFight(p) und nicht IsExcludedFromArming(p)" waere dafuer KEIN
        // Ersatz - bei einem Pazifisten, der zusaetzlich ausgeschlossen ist, kaeme falsch false heraus.
        public static bool CannotFightByNature(Pawn p)
        {
            if (p == null) return true;
            if (p.WorkTagIsDisabled(WorkTags.Violent)) return true;
            if (p.DevelopmentalStage.Baby()) return true;
            return false;
        }

        // The player's own "leave this colonist alone" mark (per save, see CEReloadedGameComponent.
        // excludedPawnIds). Split out from CannotFight so the UI can tell the two apart: a pacifist or a baby
        // is a fact about the pawn and must NOT be clickable, while this one is a decision the player made and
        // has to be revocable in the same place they made it.
        public static bool IsExcludedFromArming(Pawn p)
        {
            if (p == null) return false;
            CEReloadedGameComponent comp = CEReloadedGameComponent.Cached;
            return comp != null && comp.excludedPawnIds.Contains(p.thingIDNumber);
        }

        public static void SetExcludedFromArming(Pawn p, bool excluded)
        {
            if (p == null) return;
            CEReloadedGameComponent comp = CEReloadedGameComponent.Cached;
            if (comp == null) return;
            if (excluded) comp.excludedPawnIds.Add(p.thingIDNumber);
            else comp.excludedPawnIds.Remove(p.thingIDNumber);
        }

        // Stricter than CannotFight: genuinely incapable of carrying ANYTHING at all, not just a weapon - a
        // baby, who is always downed and can't wear/hold gear. A pacifist adult fails CannotFight (no weapon
        // loadout) but not this - they can still be handed an Equipment Pack (e.g. to act as a dedicated
        // doctor carrying medical supplies), just never a Weapon Loadout.
        public static bool CannotCarryAnything(Pawn p)
        {
            if (p == null) return true;
            return p.DevelopmentalStage.Baby();
        }

        // Sticky check: has a hostile mechanoid ever been seen on ANY colony map this save? Cheap (one pass over
        // spawned pawns), called lazily whenever the Mech default is needed rather than on a tick hook. Once true,
        // stays true (the player has genuinely faced the threat, that fact doesn't expire). Checks every home map,
        // not just the one currently in view - a mech assault on an outpost should count too.
        public static void CheckMechanoidEncounter(CERUiState ui)
        {
            if (ui == null || ui.hasFoughtMechanoids) return;
            foreach (Map map in PlayerMaps())
                foreach (Pawn p in map.mapPawns.AllPawnsSpawned)
                {
                    if (p.RaceProps != null && p.RaceProps.IsMechanoid && p.HostileTo(Faction.OfPlayer))
                    {
                        ui.hasFoughtMechanoids = true;
                        return;
                    }
                }
        }

        // Dynamic Anti-flesh<->Anti-mech seed: a confirmed mechanoid encounter wins outright (real, known threat);
        // otherwise a gentle days-played ramp (mech raids become likelier the longer a colony survives). This only
        // SEEDS the slider - once the player drags it themselves, ui.mechManuallySet latches and this is never
        // consulted again until "Use recommended setup" resets the flag. See WORKFLOW_OVERHAUL_PLAN.md 6a.
        public static float DynamicMechDefault(CERUiState ui)
        {
            CheckMechanoidEncounter(ui);
            if (ui.hasFoughtMechanoids) return 0.62f;
            float days = GenDate.DaysPassed;
            return Mathf.Lerp(0.5f, 0.55f, Mathf.Clamp01(days / 60f));
        }

        // Dynamic Pool anchor bonus (Generate-time "available <-> researched" slider): rewards a resource-flush
        // colony with a wider default net, using the same MaterialAffordability signal as Craft/Budget. Bounded so
        // it never runs away without the player's own input. See WORKFLOW_OVERHAUL_PLAN.md 6a.
        public static float DynamicPoolBonus(IEnumerable<ThingDef> researchedCandidates)
        {
            List<ThingDef> list = researchedCandidates?.ToList();
            if (list == null || list.Count == 0) return 0f;
            float avgAfford = list.Average(w => MaterialAffordability(w));
            return Mathf.Clamp01(avgAfford - 0.5f) * 0.3f;
        }

        // Is a workbench that can craft this weapon actually built on any of the player's colony maps?
        public static bool WeaponBenchExists(ThingDef w)
        {
            List<ThingDef> benchDefs = GetWorkbenchesFor(w);
            if (benchDefs == null || benchDefs.Count == 0) return false;
            return AllColonyBenches().Any(b => benchDefs.Contains(b.def));
        }

        // ---- Table headers (one vanilla-style look everywhere) ------------------------------------------------------

        // A column header in RimWorld's own table style: plain light text with a small sort caret, a hover highlight when
        // sortable, and no button box. Returns true when clicked. Use sortable:false for non-sortable columns so they
        // still match. `sortActive`/`descending` drive the caret.
        public static bool ColumnHeader(Rect r, string label, bool sortActive = false, bool descending = false, bool sortable = true)
        {
            if (sortable && Mouse.IsOver(r)) Widgets.DrawHighlight(r);
            string txt = label + (sortActive ? (descending ? " ▼" : " ▲") : "");
            Color old = GUI.color;
            GUI.color = sortActive ? new Color(1f, 1f, 1f, 0.95f) : new Color(1f, 1f, 1f, 0.72f);
            // Erst kleinere Schrift, dann erst kuerzen (2026-08-23, im Test aufgefallen: die
            // Ueberschrift "Loadouts" stand als "Loadou" da - ohne Auslassungspunkte, also nicht als
            // gekuerztes Wort erkennbar, sondern wie ein anderes Wort).
            // RimWorlds Widgets.Label schneidet einen zu langen Text in einem zu engen Rechteck stumpf ab.
            // Fuer eine Spaltenbreite ist das besonders unguenstig: die Ueberschrift ist der einzige Ort,
            // an dem steht, was die Spalte bedeutet, und sie ist in JEDER Sprache anders lang - eine
            // Spalte breit genug fuer Englisch zu machen sagt nichts ueber Deutsch oder Russisch.
            // Deshalb hier statt an einer einzelnen Spalte: dieselbe Staffelung, die BarLabel schon
            // benutzt - passt es in Small, bleibt alles wie bisher; sonst Tiny; und erst wenn auch das
            // nicht reicht, wird sichtbar mit Auslassungspunkten gekuerzt.
            GameFont prevFont = Text.Font;
            if (Text.CalcSize(txt).x > r.width)
            {
                Text.Font = GameFont.Tiny;
                if (Text.CalcSize(txt).x > r.width) txt = txt.Truncate(r.width);
            }
            Widgets.Label(r, txt);
            Text.Font = prevFont;
            GUI.color = old;
            return sortable && Widgets.ButtonInvisible(r);
        }

        // ---- Shared UI kit (one look everywhere: radios, search boxes, mode toggles, bottom bars) -------------------

        // The one status palette: same meaning = same color on every tab and popup.
        public static readonly Color ColGood = new Color(0.55f, 0.85f, 0.55f);   // done / available / new / assigned
        public static readonly Color ColInfo = new Color(0.6f, 0.8f, 0.95f);     // covered: in storage or in production
        public static readonly Color ColWarn = new Color(0.95f, 0.65f, 0.25f);   // actionable gap (fixable now)
        public static readonly Color ColBad = new Color(0.95f, 0.55f, 0.45f);    // missing / impossible
        // Hintergrundflaeche hinter einer zusammengehoerenden Bedienguppe (2026-08-12: ein
        // rechteckiger, hellgrauer Hintergrund je Gruppe). Bewusst eine ALPHA-Flaeche und kein fester
        // Grauton: sie liegt mal auf der Karte, mal auf einer Menue-Sektion, mal auf einer hervorgehobenen
        // Zeile - ein deckendes Grau wuerde je nach Untergrund mal zu hell, mal zu dunkel wirken.
        public static readonly Color ColGroupBg = new Color(1f, 1f, 1f, 0.07f);
        // Die Farbe der Pin-Symbole (2026-08-13 zusammengefuehrt): stand bisher als dasselbe Zahlentripel an
        // fuenf Stellen in drei Dateien - und genau bei so einer Streuung faellt spaeter eine heraus.
        // WEISS seit 2026-08-25 (die Pins waren gelb, obwohl das PNG weiss ist - Faerbung entfernt).
        // Das PNG ist weiss, die Nadel soll so aussehen wie gezeichnet. Die
        // Konstante bleibt, damit alle sechs Stellen (beide Pin-Zeilen des Pack-Assistenten, Zeilenmarke,
        // Kopfschalter und Pfeil im Assign-Fenster) weiter EINE Quelle haben - wer die Nadel je wieder
        // einfaerben will, tut es hier und nirgends sonst.
        public static readonly Color ColPin = Color.white;

        // Der Pfeil der angehefteten Zeile: weiss, der nicht angeheftete gedimmt. Das FUNKTIONIERT erst,
        // seit der Pfeil aus unserem eigenen weissen PNG kommt (TexArrowRight, siehe dort) - mit RimWorlds
        // grauem Sprite war "voll weiss" durch keine Farbe zu erreichen.
        // Kurze Geschichte, damit das nicht ein drittes Mal versucht wird: am 2026-08-27 stand hier
        // (2,2,2,1) in der Annahme, ein Faktor ueber 1 helle im Shader auf. Die Messung im laufenden Spiel
        // hat das widerlegt - Unity klemmt den Farbwert, der Pfeil blieb bei 144. Farbe kann nur abdunkeln.
        public static readonly Color ColPinArrow = Color.white;

        // One shared, prominent "!" badge (filled circle + dark outline + bold glyph) for every "this is short/
        // blocked, look at the tooltip" spot in the mod (2026-08-02: the plain small text "!"
        // several spots drew on their own - DrawIngredientCell's shortage mark, DrawSupplyReqGroup's impossible
        // dot - was "so thin and small you can't see it"). Bigger (18px default, was 12-14px) and bold Small
        // font instead of Tiny, so it actually reads as a warning at a glance instead of disappearing against
        // the dark UI background. r's own size decides the badge size - callers pick the Rect, this only draws.
        // Das Zustandssymbol einer Zeile: klickbar, aber OHNE Farbwechsel beim Ueberfahren (2026-08-13,
        // ein rotes Kreuz wurde beim Ueberfahren dunkelrot - das soll nicht sein).
        //
        // Warum es ueberhaupt passiert, im Dekompilat nachgelesen: Widgets.ButtonImage(rect, tex, baseColor)
        // reicht intern GenUI.MouseoverColor als ABSOLUTE Farbe weiter (0.3/0.7/0.9, ein helles Blau) und
        // wirft baseColor dabei weg. GUI.color multipliziert sich mit den Farben der Textur - ein rotes
        // Kreuz mal Blau ergibt genau das dunkle Rot, das beim Ueberfahren erschien. Bei diesen Symbolen TRAEGT die
        // Farbe die Aussage (rot = wird geloescht, gelb = angepinnt); sie darf nicht ausgerechnet in dem
        // Moment verschwinden, in dem der Zeiger daraufsteht - also kurz vor dem Klick.
        //
        // Zweite Haelfte desselben Fehlers: die Ueberladung OHNE Farbe setzt baseColor auf Weiss und
        // ueberschreibt damit ein vorher gesetztes GUI.color. In den Equipment-Pack-Zeilen stand genau das -
        // die Farbe wurde gesetzt, kam aber nie an, weshalb dort ein WEISSER Muelleimer stand, wo in den
        // Weapon-Loadouts-Zeilen ein roter steht. Ueber diesen Helfer geht die Farbe wieder durch.
        //
        // Der Klick selbst bleibt unveraendert (ButtonInvisible darunter), es geht nur ums Zeichnen.
        public static bool StateIconButton(Rect r, Texture2D tex, Color color)
            => Widgets.ButtonImage(r, tex, color, color);

        public static void DrawWarningBadge(Rect r, Color color)
        {
            // Shrunk once here, centered within whatever Rect a caller passes, so every badge in the mod
            // stays the same size relative to each other without touching every call site by hand.
            // 0.1 -> 0.18 on 2026-08-02 after a live look: the square still read as too heavy next to an
            // 18px item icon.
            float shrinkX = r.width * 0.18f, shrinkY = r.height * 0.18f;
            Rect b = new Rect(r.x + shrinkX, r.y + shrinkY, r.width - 2f * shrinkX, r.height - 2f * shrinkY);
            Widgets.DrawBoxSolid(new Rect(b.x - 1f, b.y - 1f, b.width + 2f, b.height + 2f), new Color(0.08f, 0.08f, 0.08f, 0.95f));
            Widgets.DrawBoxSolid(b, color);
            // Das Ausrufezeichen ist seit dem 2026-08-12 GEZEICHNET, kein Text mehr: es sass mal in der
            // Mitte, mal nicht, mal dick, mal duenn. Das war keine Einbildung und auch kein
            // SVG-gegen-PNG-Thema: es war eine Glyphe, die
            // durch Text.CurFontStyle lief. Damit hing ihr Aussehen an drei Dingen, die von aussen wackeln -
            // an der aktiven Schriftgroesse, am Padding/contentOffset des Fontstyles (auf einem ~12px-Quadrat
            // ein sichtbarer Bruchteil der Breite) und an der UI-Skalierung, die aus dem Faux-Bold-Versatz
            // von 0.6px mal einen ganzen Pixel machte und mal gar nichts. Jede der drei frueheren
            // Korrekturen (gemessene Platzierung, eine Schriftgroesse kleiner, anteiliger Tiefer-Versatz)
            // hat an genau diesen Symptomen gedreht, ohne die Ursache anzufassen.
            // KORREKTUR 2026-08-18, dritter Anlauf: es IST jetzt ein PNG.
            // Der Satz, der hier stand ("ein PNG braucht es dafuer nicht - und es waere sogar schlechter"),
            // war zweimal falsch. Erstens sah die gerechnete Fassung nicht wie ein Ausrufezeichen aus:
            // auf einem 12-Pixel-Quadrat kam ein Balken von 4 und ein Punkt von 2 Pixeln heraus - "man
            // erkennt es kaum als Rufezeichen, weil beide Striche fast gleich lang sind"
            // beschreibt genau diese Zahlen. Zweitens war es weiter uneinheitlich, und zwar aus einem
            // Grund, der uebersehen worden war: die BREITE des Balkens kam aus box.width, seine HOEHE aus
            // box.height. In einem nicht-quadratischen Rechteck ergab dasselbe Zeichen andere Proportionen -
            // und genau solche Rechtecke gibt es an den Aufrufstellen.
            // Ein PNG hat beide Probleme nicht: eine Zeichnung, an einer Stelle festgelegt, seitenverhaeltnis-
            // treu eingepasst. Sein Vorschlag war der richtige.
            DrawExclamationGlyph(b, Color.black);
        }

        // Das Ausrufezeichen als Geometrie - Balken plus Punkt, beides aus der uebergebenen Box abgeleitet.
        // Geteilt von der Warn-Plakette und der Verbots-Markierung auf den Verfuegbarkeitsbalken, weil es
        // "an vielen Stellen vorkommt": beide zeichneten vorher ein Text-"!"
        // und sahen deshalb an verschiedenen Stellen verschieden aus, obwohl beide dasselbe meinen.
        // Deterministisch, weil hier nichts mehr durch Text.CurFontStyle laeuft: keine Schriftgroesse, kein
        // Padding, kein contentOffset, kein Faux-Bold-Versatz, der bei UI-Skalierung mal ein ganzer Pixel
        // ist und mal keiner. Mathf.Round haelt die Kanten auf ganzen Pixeln, damit der Strich nicht je nach
        // Position halb transparent ausfaellt.
        // Die Zeichnung liegt in Textures/UI/Icon_Warning.png - weiss auf transparent, damit der Aufrufer
        // sie einfaerben kann. Einmal geladen und gehalten; faellt sie aus (Datei fehlt, Pfad umbenannt),
        // zeichnet der Rumpf darunter weiter die gerechnete Fassung, damit an keiner Stelle ein Loch bleibt.
        private static Texture2D warnGlyphTex;
        private static bool warnGlyphLoaded;

        private static Texture2D WarnGlyphTex
        {
            get
            {
                if (!warnGlyphLoaded)
                {
                    warnGlyphLoaded = true;
                    warnGlyphTex = ContentFinder<Texture2D>.Get("UI/Icon_Warning", false);
                }
                return warnGlyphTex;
            }
        }

        public static void DrawExclamationGlyph(Rect box, Color ink)
        {
            Texture2D tex = WarnGlyphTex;
            if (tex != null)
            {
                Color prev = GUI.color;
                GUI.color = ink;
                // Fitted, nicht gestreckt: DAS ist der Teil, der die Einheitlichkeit wirklich herstellt -
                // die Aufrufstellen uebergeben unterschiedlich geformte Rechtecke, und ein gestrecktes
                // Ausrufezeichen waere in einem breiten Kasten wieder ein anderes als in einem hohen.
                Widgets.DrawTextureFitted(box, tex, 1f);
                GUI.color = prev;
                return;
            }

            float barW = Mathf.Max(1f, Mathf.Round(box.width * 0.16f));
            float gapY = Mathf.Max(1f, Mathf.Round(box.height * 0.10f));
            float dotH = barW;
            // Gesamthoehe (Balken + Luecke + Punkt) auf der Box zentriert, damit die sichtbare TINTE mittig
            // sitzt - nicht die Line Box wie bei der Textvariante, die genau deshalb immer zu hoch sass und
            // zweimal per Versatz nachkorrigiert werden musste.
            float totalH = box.height * 0.62f;
            float barH = Mathf.Max(1f, totalH - gapY - dotH);
            float x = Mathf.Round(box.center.x - barW / 2f);
            float top = Mathf.Round(box.center.y - totalH / 2f);
            Color pc = GUI.color;
            GUI.color = Color.white;   // DrawBoxSolid multipliziert mit GUI.color - sonst faerbt der Aufrufer mit
            Widgets.DrawBoxSolid(new Rect(x, top, barW, barH), ink);
            Widgets.DrawBoxSolid(new Rect(x, top + barH + gapY, barW, dotH), ink);
            GUI.color = pc;
        }

        // Standard control metrics shared by every tab and popup.
        public const float BottomBtnH = 36f;      // bottom-bar button height
        public const float BottomBarH = 40f;      // strip reserved at the bottom of a window for the buttons
        public const float CloseBtnW = 130f;      // Close/Cancel (always bottom-left)
        public const float PrimaryBtnW = 230f;    // the main action (always bottom-right)
        public const float SecondaryBtnW = 200f;  // further actions, flowing right-to-left from the primary
        public const float SkipBtnW = 110f;       // "Skip" only - narrow, always immediately left of the primary action
        public const float ModeToggleW = 160f;    // the tinted view toggle in the top-right corner of a tab

        // The one radio button style: dot on the left, measured label on the right, the whole strip clickable
        // (RadioButton consumes the click on its dot, hence the OR). Advances x so a group spaces out evenly.
        public static bool Radio(ref float x, float y, string label, bool selected, string tip = null)
        {
            float lw = Text.CalcSize(label).x;
            Rect hit = new Rect(x, y, 26f + lw, 24f);
            bool clicked = Widgets.RadioButton(new Vector2(x, y), selected);
            Widgets.Label(new Rect(x + 26f, y + 2f, lw + 4f, 24f), label);
            if (!string.IsNullOrEmpty(tip)) TooltipHandler.TipRegion(hit, tip);
            x += 26f + lw + 22f;
            return clicked || Widgets.ButtonInvisible(hit);
        }

        // Ein Radio-Fluss links, ein Haken rechts - EINE Rechnung fuer beide Filterzeilen der
        // Auftragsuebersicht (2026-08-27). Vorher stand dieselbe Formel an beiden Aufrufstellen, und sie liess
        // den Fluss bis auf 8 Punkte an den Haken heranlaufen. Zwei Befunde aus demselben Lauf:
        //   - Befund: auf Deutsch stiess das letzte Radio
        //     "Erf. Munition" ohne jeden Abstand an "Doppelte Werkbaenke ausblenden" - kein Buchstabe fehlte,
        //     aber es las sich als ein Wort. Im Waffen-Reiter fiel es nicht auf, weil "Erf. Waffen" kuerzer ist:
        //     dieselbe Zeile, zwei Buchstaben Unterschied, und schon kippt sie.
        //   - Nebenbefund: in einem schmalen Fenster liefen beide DEUTLICH
        //     ineinander. Die Tiny-Stufe war die letzte Sicherung, und darunter gab es keine mehr.
        // Deshalb hier drei Stufen statt einer Schwelle: Normalschrift, Tiny, und wenn auch das nicht reicht,
        // faellt der Haken auf eine EIGENE Zeile, statt sich mit dem letzten Radio zu ueberlagern. Der
        // Mindestabstand ist ein benannter Wert und keine 8 mehr - ein Abstand, den man sehen kann, ist der
        // ganze Zweck der Rechnung.
        public struct RadioRowFit
        {
            public GameFont font;      // in DIESER Schrift Fluss und Haken zeichnen - beide, sonst rechnet einer falsch
            public Rect checkboxRect;
            public float extraHeight;  // > 0: der Haken sitzt auf einer eigenen Zeile, die Reihe braucht mehr Hoehe
        }

        // Breite eines Radio-Flusses in der GERADE gesetzten Schrift - dieselbe Formel, die Radio() beim
        // Zeichnen vorrueckt (26 Knopf + Text + 22 Abstand). Wer sie hier aendert, aendert sie dort mit.
        public static float RadioFlowWidth(IEnumerable<string> labels)
        {
            float w = 0f;
            if (labels != null) foreach (string l in labels) w += 26f + Text.CalcSize(l).x + 22f;
            return w;
        }

        public static RadioRowFit FitRadioRow(float flowStart, float y, float rowWidth,
            IEnumerable<string> labels, string checkboxLabel, float minGap = 24f)
        {
            GameFont prev = Text.Font;
            foreach (GameFont f in new[] { prev, GameFont.Tiny })
            {
                Text.Font = f;
                float cw = Text.CalcSize(checkboxLabel).x + 30f;
                float flowEnd = flowStart + RadioFlowWidth(labels);
                if (flowEnd + minGap <= rowWidth - cw)
                {
                    Text.Font = prev;
                    return new RadioRowFit { font = f, checkboxRect = new Rect(rowWidth - cw, y, cw, 24f), extraHeight = 0f };
                }
            }
            Text.Font = GameFont.Tiny;
            float cwTiny = Text.CalcSize(checkboxLabel).x + 30f;
            Text.Font = prev;
            return new RadioRowFit
            {
                font = GameFont.Tiny,
                // Linksbuendig unter dem Fluss, nicht rechts am Rand: auf einer eigenen Zeile gibt es keinen
                // Nachbarn mehr, an dem er sich ausrichten muesste, und links steht er dort, wo der Blick ohnehin ist.
                checkboxRect = new Rect(flowStart, y + 26f, cwTiny, 24f),
                extraHeight = 26f
            };
        }

        // One-line table cell. Every fixed-height name/label cell in a table row goes through this instead of
        // a bare Widgets.Label + .Truncate() (2026-08-09: the ".30-06 Springfield (FMJ)" row in
        // Supply Loadouts showed a second, half-cut line of text underneath).
        //
        // Truncate() alone is NOT enough, and that is the whole point of this helper: it measures the string
        // against the RECT width, while Widgets.Label draws through Text.CurFontStyle, whose padding is applied
        // INSIDE that rect - so the real content box is a couple of pixels narrower than what Truncate checked.
        // A label that measures as "just fits" therefore still word-wraps, and the second line is then clipped
        // mid-glyph by the 24px-high rect. Turning wrapping off makes the cell structurally one-line: the
        // failure mode for an over-long string becomes a clean horizontal clip instead of a sliced second row,
        // no matter what the padding does. Truncate stays on top of it so the normal case ends in "..." rather
        // than a hard cut.
        // "Lohnt es sich, DIESEN Tooltip-Text ueberhaupt zu bauen?" (2026-08-13)
        //
        // Aus RimWorlds eigenem TooltipHandler dekompiliert, und das ist der ganze Punkt: TipRegion verwirft
        // JEDEN Tipp, der nicht in einem Repaint-Frame unter der Maus liegt - die ersten beiden Zeilen der
        // Funktion sind genau diese Pruefung. Der Text wird vom Aufrufer aber VORHER gebaut, weil er ein
        // ganz normales Argument ist. Ergebnis: eine Zeichenschleife registriert pro Bild Dutzende Tooltips,
        // von denen hoechstens einer je gezeigt wird, und baut fuer alle anderen den vollen String -
        // hier keine Kleinigkeit, sondern koloniweite Bestandsaufstellungen pro Gegenstand pro Zelle.
        // Vanilla macht es selbst so: TipRegionByKey prueft Mouse.IsOver, BEVOR es .Translate() aufruft.
        //
        // Nebenwirkung, bewusst in Kauf genommen: DebugViewSettings.drawTooltipEdges zeichnet fuer bewachte
        // Tooltips keine Rahmen mehr. Das ist ein Entwickler-Overlay, das niemand aus diesem Mod benutzt.
        //
        // Die zwei Zaehler beantworten die Frage "wie viel war es denn": uebersprungen == das, was vorher
        // in JEDEM Bild gebaut wurde. Sie kosten nichts, solange das Dev-Log aus ist.
        public static int tipBuilt, tipSkipped;
        // Zweiter Zweck seit 2026-08-14: SOLANGE EIN AUSWAHLMENUE OFFEN IST, WILL NIEMAND EINEN TOOLTIP.
        // Gemeldet an der Bereitschafts-Anzeige (klicken -> das Menue geht unter dem Zeiger auf, und der
        // Tooltip des Lichts bleibt stehen und wird DARUEBER gezeichnet - genau die Zeilen, die man jetzt
        // anklicken soll, sind unlesbar). RimWorld zeichnet Tooltips nach allen Fenstern, ein Zeichenreihen-
        // folge-Trick hilft also nicht; der Tooltip darf schlicht nicht registriert werden.
        // Hier statt nur an der Meldestelle, weil es dieselbe Bauform ueberall im Mod gibt: ein Knopf mit
        // Tooltip, der beim Klick ein FloatMenu unter dem Zeiger oeffnet - die Waffen-Zelle im Assign-Tab ist
        // die naechste (ihr Tooltip listet den ganzen Loadout-Inhalt und ist entsprechend gross). Diese
        // Funktion ist das gemeinsame Tor, an dem die teuren Tooltips des Mods ohnehin schon haengen.
        // Der Klick selbst bleibt unberuehrt - er laeuft nirgends ueber TipWanted.
        public static bool TipWanted(Rect r)
        {
            bool want = Event.current != null && Event.current.type == EventType.Repaint && Mouse.IsOver(r)
                && Find.WindowStack?.WindowOfType<FloatMenu>() == null;
            if (Prefs.DevMode) { if (want) tipBuilt++; else tipSkipped++; }
            return want;
        }

        // Das Waffensymbol, das fuer "Any ammo for <Waffe>" steht - MIT der kleinen Munitionsmarke oben
        // rechts (2026-08-13: die Waffe traegt rechts oben das Munitionssymbol, wie bei den
        // Waffeneffekten). Ohne sie sagt die Zeile zweimal dasselbe Bild: einmal die Waffe als Waffe,
        // einmal die Waffe als Platzhalter fuer ihre Munition - und nur der Text unterscheidet die beiden.
        // Genau dieselbe 9px-Marke an derselben Ecke, die DrawAmmoEffectIcons schon auf jedes Effekt-Symbol
        // setzt; hier eine Spur groesser, weil das Grundsymbol 20-22px hat statt 16px.
        // EINE Funktion fuer alle Fundstellen, damit die Marke nicht an der einen Stelle sitzt und an der
        // naechsten fehlt - genau so ist dieser Bericht entstanden.
        public static void DrawGenericAmmoIcon(Rect r, ThingDef weapon)
        {
            if (weapon == null) return;
            Widgets.ThingIcon(r, weapon);
            float badge = Mathf.Max(9f, r.width * 0.5f);
            GUI.DrawTexture(new Rect(r.xMax - badge * 0.75f, r.y - badge * 0.3f, badge, badge),
                Dialog_GenerateLoadouts.TexAmmoOverlay);
        }

        public static void LabelCell(Rect r, string text)
        {
            if (text == null) text = "";
            // Das Diagnoselogging von hier ist RAUS (2026-08-13, nach dem Fund): im Test fand sich im
            // Player.log KEIN Treffer der Messzeile,
            // die Zelle bricht also nicht um. Liegenlassen war ausserdem teurer als es aussah - ein
            // Text.CalcHeight PLUS ein Text.CalcSize pro Beschriftung pro Bild, sobald der Entwicklermodus
            // an ist.
            bool prevWrap = Text.WordWrap;
            Text.WordWrap = false;
            // Klammer erhalten (2026-08-23, zweite Fundstelle per Regel-7-Grep): diese Zelle traegt im
            // Nachschub-Fenster die Munitionsnamen, und dort unterscheidet genau die Klammer die Zeilen
            // (".30-06 Springfield (AP)" gegen "(FMJ)" gegen "(Sabot)"). Gemeldet war nur die
            // Auftragsuebersicht - siehe TruncateKeepingSuffix.
            Widgets.Label(r, TruncateKeepingSuffix(text, r.width));
            Text.WordWrap = prevWrap;
        }

        // "Da kommt noch was" - der Hinweis am unteren Rand einer Liste, die laenger ist als ihr Fenster
        // (2026-08-13). Im Test waere deswegen beinahe ein falscher
        // Befund entstanden: die Rollen-Vorschau zeigt drei von fuenf Zeilen, RimWorlds Rollbalken ist ein
        // duenner grauer Strich, und direkt unter der Liste stehen die Summen - das liest sich wie eine
        // vollstaendige Liste, nicht wie ein Ausschnitt. Dieselbe Falle im Nachschub-Fenster ("Obsolete
        // bills", 3 von 7 sichtbar, darunter sofort die Gesamtkosten).
        //
        // NACH EndScrollView aufrufen, mit denselben zwei Rects und derselben Scrollposition. Zeichnet nur,
        // solange wirklich etwas unterhalb der Kante liegt, und verschwindet von selbst, sobald man unten
        // angekommen ist - ein Hinweis, der immer dasteht, wird nicht mehr gelesen.
        // rowH > 0 macht daraus eine Zeilenzahl ("noch 4"), sonst bleibt es beim reinen "mehr unten".
        // Rechts um 18px eingerueckt: genau die Breite von RimWorlds eigenem Rollbalken, damit die Blase
        // ihn nicht verdeckt - abgeleitet, nicht geraten.
        public static void DrawScrollMoreHint(Rect outRect, Rect viewRect, Vector2 scrollPos, float rowH = 0f)
        {
            float hidden = viewRect.height - (scrollPos.y + outRect.height);
            if (hidden <= 1f) return;
            string txt = rowH > 0f
                ? "CER_LoadoutUtility_ScrollMoreRows".Translate(Mathf.Max(1, Mathf.CeilToInt(hidden / rowH)).ToString()).ToString()
                : "CER_LoadoutUtility_ScrollMore".Translate().ToString();
            GameFont prevFont = Text.Font;
            TextAnchor prevAnchor = Text.Anchor;
            Text.Font = GameFont.Tiny;
            float w = Text.CalcSize(txt).x + 14f;
            Rect r = new Rect(outRect.xMax - w - 18f, outRect.yMax - 17f, w, 15f);
            Widgets.DrawBoxSolid(r, new Color(0.08f, 0.08f, 0.08f, 0.88f));
            GUI.color = ColWarn;
            Text.Anchor = TextAnchor.MiddleCenter;
            Widgets.Label(r, txt);
            GUI.color = Color.white;
            Text.Anchor = prevAnchor;
            Text.Font = prevFont;
        }

        // The standard "Search:" box: same label, offset and width on every screen.
        // Every full-text search field in the mod goes through this one helper now (or should - 2026-07-30,
        // sweep), so the red-X clear button lives in exactly one place instead of being
        // re-added by hand at every call site.
        public static string SearchBox(float x, float y, string value)
        {
            Widgets.Label(new Rect(x, y + 2f, 56f, 24f), "CER_LoadoutUtility_SearchLabel".Translate());
            Rect fieldRect = new Rect(x + 60f, y, 200f, 24f);
            string result = Widgets.TextField(fieldRect, value);
            if (ClearSearchButton(fieldRect, y, result)) result = "";
            return result;
        }

        // The red-X clear button any full-text search field sits next to, factored out so a dialog with its
        // own custom-built search field (not going through SearchBox above) still gets the exact same look
        // instead of a hand-rolled "Clear" text button or nothing at all (2026-07-30, same
        // sweep). Draws nothing (and returns false) while the field is already empty - nothing to clear.
        public static bool ClearSearchButton(Rect fieldRect, float y, string currentValue)
        {
            if (string.IsNullOrEmpty(currentValue)) return false;
            Rect clearRect = new Rect(fieldRect.xMax + 4f, y + 2f, 20f, 20f);
            GUI.color = ColBad;
            bool clicked = Widgets.ButtonText(clearRect, "CER_LoadoutUtility_ClearButtonX".Translate());
            GUI.color = Color.white;
            TooltipHandler.TipRegion(clearRect, "CER_LoadoutUtility_ClearSearchTooltip".Translate());
            return clicked;
        }

        // RimWorlds Lernhilfe ("Learning helper") zeichnet oben rechts, aber auf WindowLayer.Super - also
        // UEBER jedem Dialog - und verarbeitet ihre Klicks zuerst. Ein Fenster, dessen rechte Kante unter
        // diesen 200-Punkte-Streifen reicht, verliert dort JEDEN Knopf: bei uns zuerst das Schliessen-X
        // (Widgets.CloseButtonFor sitzt bei width-22), dann der Hilfe-Knopf, auf sehr schmalen Schirmen auch
        // der Arm-my-Colony-Knopf. Zusammen mit absorbInputAroundWindow heisst das: das Fenster laesst sich
        // nur noch mit Escape schliessen, und wer das nicht weiss, sitzt fest.
        // Gemessen 2026-08-19 im Test bei UI-Breite 1440 und 1366; die Rechnung dahinter gilt fuer
        // JEDES unserer Fenster, nicht nur das Hauptfenster: Kollision ab UI-Breite < Fensterbreite + 416.
        // Beim Hauptfenster (1100) also unter 1516, beim Nachschub-Fenster (1192) unter 1608.
        // Zahlen aus LearningReadout.LearningReadoutOnGUI: Rect(screenWidth - 8 - 200, 8, 200, ...).
        private const float LearningReadoutWidth = 200f;
        private const float LearningReadoutMargin = 8f;

        // float.MaxValue heisst "die Lernhilfe ist gar nicht da" - dann bleibt jedes Fenster, wo es war.
        public static float LearningReadoutLeftEdge()
        {
            // Dieselben vier Bedingungen wie LearningReadoutOnGUI. Alles defensiv, weil diese Fenster in
            // Randfaellen (Hauptmenue, Ladebildschirm) ohne Spielzustand geoeffnet werden koennen.
            if (TutorSystem.TutorialMode || !TutorSystem.AdaptiveTrainingEnabled) return float.MaxValue;
            LearningReadout readout = Find.Tutor?.learningReadout;
            if (readout == null) return float.MaxValue;
            PlaySettings play = Find.PlaySettings;
            if ((play == null || !play.showLearningHelper) && readout.ActiveConceptsCount == 0) return float.MaxValue;
            return UI.screenWidth - LearningReadoutMargin - LearningReadoutWidth;
        }

        // Schiebt ein Fenster nach links aus dem Streifen heraus - und NUR das. Es wird nie schmaler.
        //
        // Die erste Fassung von heute Mittag hat als zweiten Schritt die Breite gekappt, wenn das Schieben
        // nicht reichte. Das war falsch, und zwar aus diesem Grund:
        // die Zeilen des Nachschub-Fensters zeichnen an FESTEN Abstaenden vom linken Rand und enden
        // bei 1124; unter 1176 Fensterbreite rutschen Werkbank-Auswahl, Sprung- und Info-Symbol aus dem
        // Fenster, und ein waagrechter Bildlauf existiert nicht - sie sind dann nicht abgeschnitten,
        // sondern UNERREICHBAR. Nachgerechnet haette das Kappen genau im Band von rund 1216 bis 1392
        // Bildbreite einen Fall kaputtgemacht, der vorher in Ordnung war.
        //
        // Also die ehrliche Abwaegung: ein Fenster, dessen X unter der Lernhilfe liegt, laesst sich immer
        // noch mit Escape schliessen. Ein Fenster, dessen Bedienelemente aus der eigenen Kante gefallen
        // sind, hat gar keinen Ausweg. Passt es nicht daneben, bleibt es also lieber ueberdeckt - dann ist
        // schlicht kein Platz fuer beides, und daran kann keine Rechnung etwas aendern.
        // Die beobachteten Faelle deckt das Schieben allein ab: Hauptfenster (1100 breit) bei 1366 Bildbreite
        // -> x = 58 statt 133, rechte Kante genau auf der Streifenkante; bei 1440 -> x = 132. Beide voll frei.
        // Ohne Kollision faellt die Funktion sofort durch - auf breiten Schirmen bleibt alles, wie es war.
        public static Rect KeepClearOfLearningReadout(Rect r)
        {
            float limit = LearningReadoutLeftEdge();
            if (r.xMax <= limit) return r;
            float x = Mathf.Max(LearningReadoutMargin, Mathf.Min(r.x, limit - r.width));
            if (Mathf.Approximately(x, r.x)) return r;
            return new Rect(x, r.y, r.width, r.height).Rounded();
        }

        // ====== Der Rollen-Schalter, EIN Bedienelement fuer beide Fenster (2026-08-19) ======
        // Er hat seit heute drei Werte (RoleAssignMode), und drei Werte gehen nicht auf einen Haken.
        // Ein Teilhaken waere die naheliegende Loesung und die schlechtere: ein halbgefuelltes Kaestchen
        // braucht eine Legende, und dann ist es keine Abkuerzung mehr. Deshalb ein Knopf, der den Wert im
        // Klartext zeigt, mit einem Auswahlmenue - dieselbe Bauform wie jeder andere beschriftete
        // Auswahlknopf im Mod.
        // Hier und nicht in einem der Fenster, weil ihn der Assign-Reiter UND die Zuweisungs-Vorschau
        // zeichnen - zwei Kopien waeren genau die Sorte Doppelung, aus der die Fehlbeschriftung von
        // heute morgen entstanden ist (derselbe Schalter hiess auf zwei Bildschirmen verschieden).
        public static string RoleModeLabel(RoleAssignMode m)
        {
            switch (m)
            {
                case RoleAssignMode.RolesOnly: return "CER_LoadoutUtility_RoleModeRolesOnly".Translate().ToString();
                case RoleAssignMode.NoRoles: return "CER_LoadoutUtility_RoleModeNoRoles".Translate().ToString();
                default: return "CER_LoadoutUtility_RoleModeMixed".Translate().ToString();
            }
        }

        // true, wenn der Wert sich geaendert hat - der Aufrufer muss dann neu rechnen lassen.
        // Wie breit muss der Knopf sein, damit seine Beschriftung in NORMALER Schrift hineinpasst
        // (2026-09-04, Befund im Test: die drei Beschriftungen standen in der normalen
        // Schriftgroesse, der Knopf dazwischen deutlich kleiner)?
        //
        // Ursache war nicht der Knopf, sondern seine feste Breite von 170: ButtonTextFit faellt auf Tiny
        // zurueck, sobald der Text nicht passt - das ist als Notnagel gegen Abschneiden richtig (deshalb
        // steht er da, siehe unten), aber in einer Zeile mit drei normal gesetzten Beschriftungen faellt
        // der eine kleine sofort auf. Statt die 170 auf eine groessere Zahl zu drehen, die beim naechsten
        // laengeren Uebersetzungstext wieder zu klein waere: messen. Der Notnagel bleibt fuer den Fall,
        // dass der Aufrufer wirklich keinen Platz hat.
        public static float RoleModeControlWidth(RoleAssignMode current)
        {
            GameFont prev = Text.Font;
            Text.Font = GameFont.Small;
            float w = Text.CalcSize("CER_LoadoutUtility_RoleModeButtonFmt".Translate(RoleModeLabel(current)).ToString()).x;
            Text.Font = prev;
            return w + 16f;   // dieselbe Luft, mit der ButtonTextFit rechnet (12), plus etwas Rand
        }

        public static void RoleModeControl(Rect r, RoleAssignMode current, Action<RoleAssignMode> onPicked)
        {
            // ButtonTextFit, nicht Widgets.ButtonText (2026-08-27): der Kasten hat eine feste Breite, und
            // "Zuweisen: Rollen und Vorlagen" passt auf Deutsch nicht hinein - der Knopf zeigte beidseitig
            // beschnitten "weisen: Rollen und Vorlag". Eine Stelle genuegt, weil Assign-Reiter UND
            // Zuweisungs-Vorschau denselben Knopf hier zeichnen (siehe Kommentar oben).
            if (ButtonTextFit(r, "CER_LoadoutUtility_RoleModeButtonFmt".Translate(RoleModeLabel(current)).ToString()))
            {
                List<FloatMenuOption> opts = new List<FloatMenuOption>();
                foreach (RoleAssignMode m in new[] { RoleAssignMode.Mixed, RoleAssignMode.RolesOnly, RoleAssignMode.NoRoles })
                {
                    RoleAssignMode captured = m;
                    opts.Add(new FloatMenuOption(RoleModeLabel(captured), () => onPicked(captured)));
                }
                Find.WindowStack.Add(new FloatMenu(opts));
            }
            if (TipWanted(r)) TooltipHandler.TipRegion(r, "CER_LoadoutUtility_RoleModeTip".Translate());
        }

        // A "Label: [current]" dropdown trigger: returns true when the button is clicked so the caller can open
        // its FloatMenu. Keeps every labeled dropdown the same shape.
        public static bool Dropdown(float x, float y, float labelW, string label, float btnW, string current)
        {
            Widgets.Label(new Rect(x, y + 2f, labelW, 24f), label);
            return Widgets.ButtonText(new Rect(x + labelW + 4f, y, btnW, 24f), (current ?? "").Truncate(btnW - 8f));
        }

        // The tinted view toggle that sits in the top-right corner of a tab (Role mode, Weapon bills, ...).
        public static bool ModeToggle(float inRectWidth, string label, string tip = null)
        {
            Rect r = new Rect(inRectWidth - ModeToggleW, 0f, ModeToggleW, 26f);
            Color old = GUI.color;
            GUI.color = new Color(0.55f, 0.85f, 1f);
            bool clicked = ButtonTextFit(r, label);   // ModeToggleW ist fest, der Text uebersetzt (de-buttontext-sweep)
            GUI.color = old;
            if (!string.IsNullOrEmpty(tip)) TooltipHandler.TipRegion(r, tip);
            return clicked;
        }

        // Eager field initializers, not lazy properties (2026-08-01) - see Patch_Pawn_ChangeAmmoGizmo's own
        // identical comment for why [StaticConstructorOnStartup] alone doesn't help a field left null until
        // some later property access.
        public static readonly Texture2D TexModeSimple = ContentFinder<Texture2D>.Get("UI/Icon_ModeSimple", false) ?? BaseContent.WhiteTex;
        public static readonly Texture2D TexModeDetailed = ContentFinder<Texture2D>.Get("UI/Icon_ModeDetailed", false) ?? BaseContent.WhiteTex;
        public const float ModeDetailToggleW = 30f;

        // Per-tab Simple/Detailed toggle icon: sits in the same top-right row as ModeToggle (just left of it, pass
        // reserveRight = ModeToggleW + 8f on tabs that also have one; 0 on tabs that don't). One consistent control,
        // same visual language everywhere - see WORKFLOW_OVERHAUL_PLAN.md section 1. Click flips `detailed` in place.
        public static void ModeDetailToggle(float inRectWidth, ref bool detailed, float reserveRight = 0f)
        {
            ModeDetailToggle(new Rect(inRectWidth - reserveRight - 8f - ModeDetailToggleW, 1f, ModeDetailToggleW, ModeDetailToggleW), ref detailed,
                "CER_LoadoutUtility_ModeDetailedTip".Translate(),
                "CER_LoadoutUtility_ModeSimpleTip".Translate());
        }

        // Same Simple/Detailed icon at a caller-chosen spot with caller wording (the popups' preset row is not a
        // tab header, but the control must read as the SAME control everywhere).
        public static void ModeDetailToggle(Rect r, ref bool detailed, string detailedTip, string simpleTip)
        {
            Texture2D tex = detailed ? TexModeDetailed : TexModeSimple;
            if (Widgets.ButtonImage(r, tex)) detailed = !detailed;
            TooltipHandler.TipRegion(r, detailed ? detailedTip : simpleTip);
        }

        // The one score/skill bar: dark background, red->yellow->green fill, black border, optional centered text
        // ("Ranged: 170" style when textPrefix is set, the bare score otherwise) and optional tooltip.
        // Die Beschriftung einmal je Kolonist vorbauen statt je Durchgang (2026-08-25, perf-cell-row-next) -
        // siehe Dialog_AssignLoadouts.GetRowLabels. prebuiltLabel gewinnt, wenn gesetzt.
        public static string ScoreBarLabel(float score, string textPrefix)
            => string.IsNullOrEmpty(textPrefix)
                ? Mathf.RoundToInt(score).ToString()
                : "CER_LoadoutUtility_ScoreBarLabel".Translate(textPrefix, Mathf.RoundToInt(score).ToString()).ToString();

        public static void ScoreBar(Rect rect, float score, float maxScore, string textPrefix = null, bool drawText = true, string tooltip = null, string prebuiltLabel = null)
        {
            float pct = Mathf.Clamp01(maxScore > 0f ? score / maxScore : 0f);
            GUI.color = new Color(0.2f, 0.2f, 0.2f, 1f);
            GUI.DrawTexture(rect, BaseContent.WhiteTex);
            if (pct > 0f)
            {
                Color darkGreen = new Color(0.1f, 0.55f, 0.1f);
                GUI.color = pct < 0.5f
                    ? Color.Lerp(Color.red, Color.yellow, pct * 2f)
                    : Color.Lerp(Color.yellow, darkGreen, (pct - 0.5f) * 2f);
                GUI.DrawTexture(new Rect(rect.x + 1f, rect.y + 1f, (rect.width - 2f) * pct, rect.height - 2f), BaseContent.WhiteTex);
            }
            GUI.color = Color.black;
            Widgets.DrawBox(rect, 1);
            GUI.color = Color.white;
            if (drawText)
            {
                Text.Font = GameFont.Tiny;
                BarLabel(rect, prebuiltLabel ?? ScoreBarLabel(score, textPrefix));
                Text.Font = GameFont.Small;
            }
            if (!string.IsNullOrEmpty(tooltip)) TooltipHandler.TipRegion(rect, tooltip);
        }

        // A full-width divider with the standard breathing room below; returns the y to continue at.
        public static float Divider(float y, float width)
        {
            Widgets.DrawLineHorizontal(0f, y, width);
            return y + 10f;
        }

        // Bottom-left Close/Cancel. Every bottom-bar button shares one height and baseline.
        public static bool BottomCloseButton(Rect inRect, string label)
        {
            return ButtonTextFit(new Rect(0f, inRect.height - BottomBarH, CloseBtnW, BottomBtnH), label);
        }

        // A bottom-bar action flowing right-to-left: pass xRight = inRect.width for the primary (rightmost)
        // action, then reuse the same xRight for the next one to its left. Disabled buttons draw dimmed.
        // The magic-wand marker every "let the mod do it" button shares (Auto-generate/Auto-assign/Arm my Colony).
        public static readonly Texture2D TexWand = ContentFinder<Texture2D>.Get("UI/Icon_Wand", false) ?? BaseContent.WhiteTex;

        public static readonly Texture2D TexTrash = ContentFinder<Texture2D>.Get("UI/Icon_Trash", false) ?? BaseContent.WhiteTex;

        // "Stays put by default, but pick it up any time" - NOT a padlock, which reads as "nothing here can
        // be touched" (wrong: every field stays editable, editing IS what un-pins a row). Previously two
        // separate ContentFinder.Get("UI/Icon_Pin") fields (Dialog_SquadSetup.cs, Dialog_GenerateEquipmentPack.cs)
        // that already independently converged on the exact same texture/meaning after side-by-side user
        // comparison-testing (2026-07-31) - centralized here (2026-08-01) as the first piece of a real shared
        // generate-preview icon language, not just a coincidentally-matching convention per file.
        public static readonly Texture2D TexPin = ContentFinder<Texture2D>.Get("UI/Icon_Pin", true);

        // Eigener Pfeil statt RimWorlds TexUI.ArrowTexRight (2026-08-27). Grund, im laufenden Spiel
        // nachgemessen: der Sprite von RimWorld IST selbst ein 144er Grau, und GUI.color kann ein Bild nur
        // dunkler machen - Unity klemmt den Farbwert bei 1, ein Faktor darueber tut nachweislich nichts
        // (Messung: angeheftet 144, nicht angeheftet 95 = 0.6 Alpha ueber Grund 21 - die Rechnung geht exakt
        // auf). "Voll weiss gegen gedimmt" war mit dem fremden Sprite also nie erreichbar, egal welche Farbe
        // man setzt. Mit einem weissen PNG ist es ein Alphawert, und der wirkt.
        // Rueckfall auf den RimWorld-Pfeil, falls die Datei fehlt - dann sieht es aus wie vorher, statt zu werfen.
        public static readonly Texture2D TexArrowRight =
            ContentFinder<Texture2D>.Get("UI/Icon_ArrowRight", false) ?? TexUI.ArrowTexRight;

        // "Du hast an dieser Zeile etwas geaendert" (2026-08-12, dritter Zustand der bestehenden
        // Pack-/Loadout-Zeilen).
        // EIGENES PNG seit 2026-08-13: statt eines Stifts erschien ein anderes, seltsames Symbol
        // - die Annahme, Vanillas "UI/Buttons/Rename" SEI ein Stift,
        // war schlicht falsch - es ist ein Umbenennen-Glyph und liest sich an dieser Stelle als Stoerung.
        // BEWUSST PLATZHALTER (Dummy-PNG, ein eigenes Stift-Symbol kann es ersetzen):
        // einfarbig weiss und 64px, genau wie die Kategorie-Symbole unten - zum Austauschen genuegt es,
        // Textures/UI/Icon_EditPencil.png zu ersetzen, hier ist nichts anzupassen.
        // Die zwei Rueckfaelle bleiben (reportFailure:false), damit ein fehlendes PNG keine rote
        // Log-Meldung erzeugt und die Zelle nie leer bleibt.
        public static readonly Texture2D TexEditPencil =
            ContentFinder<Texture2D>.Get("UI/Icon_EditPencil", false)
            ?? ContentFinder<Texture2D>.Get("UI/Buttons/Rename", false) ?? TexPin;

        // Kategorie-Symbole fuer die vier Zellen der Kartenansicht (Munition / Pack / Zweitwaffe / Sonstiges).
        // In der kompakten Ansicht sagt die Spaltenueberschrift, um welche Kategorie es geht - in der
        // Kartenansicht gibt es keine Ueberschrift, dort standen die vier Zellen ohne jede Beschriftung
        // nebeneinander (2026-08-09 nachgeliefert).
        // BEWUSST PLATZHALTER (koennen durch schoenere ersetzt werden): einfarbig weiss,
        // damit RimWorlds GUI.color sie wie jedes andere UI-Symbol einfaerben kann - ein farbiges Grundbild
        // waere an der Stelle nicht mehr steuerbar. 64px, weil die Zellen auf ~16px herunterskalieren.
        // Zum Austauschen genuegt es, die PNGs in Textures/UI zu ersetzen; hier ist nichts anzupassen.
        public static readonly Texture2D TexCatAmmo = ContentFinder<Texture2D>.Get("UI/Icon_CatAmmo", false);
        public static readonly Texture2D TexCatPack = ContentFinder<Texture2D>.Get("UI/Icon_CatPack", false);
        public static readonly Texture2D TexCatSidearm = ContentFinder<Texture2D>.Get("UI/Icon_CatSidearm", false);
        public static readonly Texture2D TexCatOther = ContentFinder<Texture2D>.Get("UI/Icon_CatOther", false);

        // Zeichnet das Kategorie-Symbol links der Zelle und gibt zurueck, wie viel Breite es gekostet hat.
        // Fehlt das PNG (ContentFinder mit false), wird nichts gezeichnet UND nichts reserviert - die Zeile
        // sieht dann aus wie vorher, statt eine leere Luecke zu zeigen.
        public static float DrawCategoryIcon(Rect cell, Texture2D tex, string tip)
        {
            if (tex == null) return 0f;
            Rect r = new Rect(cell.x, cell.y + (cell.height - 16f) / 2f, 16f, 16f);
            Color prev = GUI.color;
            GUI.color = new Color(1f, 1f, 1f, 0.55f);
            GUI.DrawTexture(r, tex);
            GUI.color = prev;
            if (!string.IsNullOrEmpty(tip)) TooltipHandler.TipRegion(r, tip);
            return 18f;
        }

        // Shared "what will happen to this generate-preview row" status - one language for every screen that
        // shows a checkbox-driven preview of things about to be created/updated/left alone/removed (Weapon
        // Loadouts' auto-generate table, Equipment Packs' auto-generate table, and any future one). Pinned
        // stays a separate, existing concept (drawn via TexPin above, not through this enum) - it answers a
        // different question ("will the slider/re-plan touch this row at all") than GenRowStatus answers
        // ("what does the CURRENT checkbox state mean for this row's real-world content").
        // STAND 2026-08-12: dieses Bauteil hat gerade KEINE Aufrufer mehr, und das ist Absicht, kein Rest.
        // Die Entscheidung lautete "ein Symbol pro Zeile, und es zeigt den
        // ZUSTAND" - beide Vorschauzeilen (Weapon Loadouts, Equipment Packs) zeichneten vorher den Haken UND
        // zusaetzlich dieses Statusglyph daneben. Das zweite Zeichen ist dort entfallen; die einzige Aussage,
        // die es exklusiv trug ("wird geloescht"), sitzt jetzt auf dem Hauptsymbol selbst (Muelleimer statt
        // leerem Kaestchen, siehe Dialog_SquadSetup's stTex).
        // NICHT loeschen, weil "unbenutzt": das hier ist die abgestimmte Statussprache samt Farben und
        // Reihenfolge, die eigens dafuer zentralisiert wurde, dass ein dritter Bildschirm nicht wieder seine
        // eigene erfindet. Wer eine neue Vorschauzeile baut, faengt hier an - und wer erwaegt, das zweite
        // Symbol zurueckzubringen, liest zuerst die Entscheidung oben.
        public enum GenRowStatus { New, Update, Kept, Skipped, Delete }

        // `canDelete`: some screens treat unchecking an EXISTING row as "delete it" (Weapon Loadouts, per
        // explicit 2026-07-31 user instruction - "das heisst LÖSCHE IHN"); others are purely additive and
        // unchecking just means "leave it alone, don't touch it" with no destructive consequence (Equipment
        // Packs, which has no per-row delete concept at all) - the caller must say which, this function
        // never assumes.
        public static GenRowStatus ComputeGenRowStatus(bool selected, bool exists, bool edited, bool canDelete)
        {
            if (!selected) return exists && canDelete ? GenRowStatus.Delete : GenRowStatus.Skipped;
            if (!exists) return GenRowStatus.New;
            return edited ? GenRowStatus.Update : GenRowStatus.Kept;
        }

        private static readonly Color ColKept = new Color(1f, 1f, 1f, 0.55f);
        private static readonly Color ColSkipped = new Color(1f, 1f, 1f, 0.45f);

        // Icon-only half of the shared status language, for screens too tight on row width for a text tag
        // too (2026-08-01, Equipment Packs' own row - Weapon Loadouts' wider row uses the tag+text wrapper
        // below instead). Sets GUI.color to the status's own established color and draws its glyph/texture
        // into iconRect directly - caller resets GUI.color afterward. Kept draws nothing (see DrawGenRowStatus's
        // own comment - it's the "nothing happening" default, not a glanceable state).
        public static void DrawGenRowStatusIcon(Rect iconRect, GenRowStatus status)
        {
            switch (status)
            {
                case GenRowStatus.Delete: GUI.color = ColBad; GUI.DrawTexture(iconRect, TexTrash); return;
                case GenRowStatus.Skipped: GUI.color = ColSkipped; break;
                case GenRowStatus.Kept: GUI.color = ColKept; return;
                case GenRowStatus.Update: GUI.color = ColInfo; break;
                case GenRowStatus.New: GUI.color = ColGood; break;
                default: return;
            }
            string glyph = status == GenRowStatus.Skipped ? "x" : status == GenRowStatus.Update ? "~" : "+";
            Text.Anchor = TextAnchor.MiddleCenter;
            Widgets.Label(iconRect, glyph);
            // Reset before returning (2026-08-02 fix, not just when DrawGenRowStatus's own wrapper happens
            // to overwrite it afterward) - this function is also called directly now (Equipment Packs
            // always did, Weapon Loadouts' icon-only row as of today too), and a caller with no wrapper of
            // its own left every following Widgets.Label centered for the rest of the frame ("Alignment was
            // MiddleCenter at end of frame" log spam).
            Text.Anchor = TextAnchor.UpperLeft;
        }

        // Draws the icon-glyph + already-translated tag text for a GenRowStatus, right-aligned to tagRect's
        // own right edge with a 16px icon slot immediately to its left - the exact Rect shape/colors Weapon
        // Loadouts' own row already used before this was centralized, so every existing caller's layout and
        // look stays pixel-identical. Owns color too (not just the glyph/texture) - every caller gets the
        // exact same look, not just a coincidentally-matching one.
        public static void DrawGenRowStatus(Rect tagRect, GenRowStatus status, string tagText)
        {
            DrawGenRowStatusIcon(new Rect(tagRect.x - 16f, tagRect.y, 14f, tagRect.height), status);
            Text.Anchor = TextAnchor.MiddleRight;
            Widgets.Label(tagRect, tagText);
            Text.Anchor = TextAnchor.UpperLeft;
            GUI.color = Color.white;
        }

        // A plain white filled circle (alpha mask), generated procedurally rather than a shipped asset - draw
        // with GUI.color set to the desired tint, same convention DrawBoxSolid's own solid-color fills already
        // use. 2026-08-01: Dialog_SupplyLoadouts' "Assigned" count badge used a small solid
        // square, the SAME shape the availability bars already use colony-wide - a dot reads as visually
        // distinct at a glance instead of looking like a second, unrelated availability indicator.
        private static Texture2D tDot;
        public static Texture2D TexDot
        {
            get
            {
                if (tDot == null)
                {
                    const int size = 32;
                    Texture2D tex = new Texture2D(size, size, TextureFormat.ARGB32, false);
                    tex.filterMode = FilterMode.Bilinear;
                    Vector2 center = new Vector2(size / 2f, size / 2f);
                    float radius = size / 2f - 1f;
                    Color[] pixels = new Color[size * size];
                    for (int py = 0; py < size; py++)
                        for (int px = 0; px < size; px++)
                        {
                            float dist = Vector2.Distance(new Vector2(px + 0.5f, py + 0.5f), center);
                            // 1px soft edge so the circle isn't jagged at small on-screen sizes.
                            float alpha = Mathf.Clamp01(radius - dist + 0.5f);
                            pixels[py * size + px] = new Color(1f, 1f, 1f, alpha);
                        }
                    tex.SetPixels(pixels);
                    tex.Apply();
                    tDot = tex;
                }
                return tDot;
            }
        }

        // Same solid-fill call signature as Widgets.DrawBoxSolid, but a circle - swap-in replacement for
        // callers that want the "colored badge" look without reusing the square shape availability bars
        // already own.
        public static void DrawDotSolid(Rect r, Color color)
        {
            GUI.color = color;
            GUI.DrawTexture(r, TexDot);
            GUI.color = Color.white;
        }

        // Same procedural alpha-mask approach as TexDot, but a diamond (Manhattan/L1 distance instead of
        // Euclidean) - 2026-08-02, Dialog_SupplyLoadouts' "Assigned" column split into two always-visible
        // indicators (a real pawn count used to silently change into a loadout-template count depending on
        // the scope radio - fix: two separate shapes, always both shown). Diamond = assigned to a
        // colonist, circle (TexDot, reused as-is) = used in a saved loadout template.
        private static Texture2D tDiamond;
        public static Texture2D TexDiamond
        {
            get
            {
                if (tDiamond == null)
                {
                    const int size = 32;
                    Texture2D tex = new Texture2D(size, size, TextureFormat.ARGB32, false);
                    tex.filterMode = FilterMode.Bilinear;
                    Vector2 center = new Vector2(size / 2f, size / 2f);
                    float radius = size / 2f - 1f;
                    Color[] pixels = new Color[size * size];
                    for (int py = 0; py < size; py++)
                        for (int px = 0; px < size; px++)
                        {
                            float dist = Mathf.Abs(px + 0.5f - center.x) + Mathf.Abs(py + 0.5f - center.y);
                            float alpha = Mathf.Clamp01(radius - dist + 0.5f);
                            pixels[py * size + px] = new Color(1f, 1f, 1f, alpha);
                        }
                    tex.SetPixels(pixels);
                    tex.Apply();
                    tDiamond = tex;
                }
                return tDiamond;
            }
        }

        // Die kleine Traeger-/Vorlagen-Anzeige (Raute bzw. Punkt + Zahl, Namen im Mouseover) - herausgezogen
        // aus Dialog_SupplyLoadouts.DrawIndicatorCell (2026-09-01: das Rad nicht neu erfinden - Supply
        // Loadouts hatte das schon), damit der Packs-Reiter DIESELBE Anzeige samt
        // denselben Mouseover-Keys benutzt. Weiss, nicht ColGood (2026-08-02): die Marke ist ein neutraler
        // Zaehler, kein gut/schlecht-Status.
        public static void DrawUsedByIndicator(float x, float y, List<string> names, string noneTip, bool diamond)
        {
            Rect cell = new Rect(x, y, 30f, 24f);
            Rect dot = new Rect(x + 2f, y + 8f, 9f, 9f);
            if (names != null && names.Count > 0)
            {
                if (diamond) DrawDiamondSolid(dot, Color.white);
                else DrawDotSolid(dot, Color.white);
                Text.Font = GameFont.Tiny;
                Text.Anchor = TextAnchor.MiddleLeft;
                Widgets.Label(new Rect(x + 13f, y + 1f, 17f, 22f), names.Count.ToString());
                Text.Anchor = TextAnchor.UpperLeft;
                Text.Font = GameFont.Small;
                TooltipHandler.TipRegion(cell, "CER_SupplyLoadouts_UsedByTip".Translate(string.Join("\n", names.ToArray())));
            }
            else
            {
                Color grey = new Color(0.6f, 0.6f, 0.6f, 0.45f);
                if (diamond) DrawDiamondSolid(dot, grey);
                else DrawDotSolid(dot, grey);
                TooltipHandler.TipRegion(cell, noneTip);
            }
        }

        public static void DrawDiamondSolid(Rect r, Color color)
        {
            GUI.color = color;
            GUI.DrawTexture(r, TexDiamond);
            GUI.color = Color.white;
        }

        // Icon + centered label inside an already-drawn button. Label eingepasst wie bei ButtonTextFit -
        // dieselbe Ueberlaufklasse, nur mit 36px weniger Platz wegen des Icons.
        public static void ButtonIconLabel(Rect r, Texture2D icon, string label)
        {
            GUI.DrawTexture(new Rect(r.x + 8f, r.y + (r.height - 22f) / 2f, 22f, 22f), icon);
            Text.Anchor = TextAnchor.MiddleCenter;
            GameFont prevFont = Text.Font;
            bool prevWrap = Text.WordWrap;
            Text.WordWrap = false;
            if (Text.Font != GameFont.Tiny) Text.Font = GameFont.Small;   // siehe ButtonTextFit (2026-08-30)
            float avail = r.width - 36f - 2f;
            string shown = label;
            if (Text.CalcSize(shown).x > avail)
            {
                Text.Font = GameFont.Tiny;
                if (Text.CalcSize(shown).x > avail) shown = shown.Truncate(avail);
            }
            Widgets.Label(new Rect(r.x + 30f, r.y, r.width - 36f, r.height), shown);
            Text.WordWrap = prevWrap;
            Text.Font = prevFont;
            Text.Anchor = TextAnchor.UpperLeft;
        }

        /// <summary>Widgets.ButtonText, dessen Beschriftung in den Knopf EINGEPASST wird: erst Small, passt
        /// das nicht, Tiny, erst danach sichtbar gekuerzt (Auslassungspunkte) - nie der stumpfe Beidseits-
        /// Abschnitt von ButtonText (MiddleCenter laesst Ueberlauf symmetrisch aus dem Rect laufen:
        /// "einstellungen auf Standard zuruecksetz") und nie der Zweizeilen-Quetschfall. WordWrap ist
        /// waehrend des Zeichnens aus, damit ein knapp nicht passender Text nicht doch umbricht (dieselbe
        /// CurFontStyle-Falle wie bei LabelCell). Fuer jeden Knopf, dessen Beschriftung uebersetzt wird und
        /// dessen Breite fest ist.</summary>
        // Schriftstufe fuer eine REIHE gleichrangiger Knoepfe (2026-08-29, de-buttontext-sweep): passt EIN Text
        // seiner Zelle nicht in Small, faellt die ganze Reihe auf Tiny - sonst steht ein kleiner Knopf neben
        // grossen, und die Reihe liest sich kaputt statt uebersetzt. Der Aufrufer setzt Text.Font fuer die
        // ButtonTextFit-Aufrufe der Reihe und stellt es danach zurueck; ButtonTextFit misst weiter selbst und
        // kuerzt nur, was auch in Tiny nicht passt. Dieselbe Idee wie FitRadioRow, fuer Knoepfe statt Radios.
        public static GameFont ButtonRowFont(params (Rect r, string label)[] cells)
        {
            GameFont prev = Text.Font;
            bool prevWrap = Text.WordWrap;
            Text.WordWrap = false;
            Text.Font = GameFont.Small;
            GameFont result = GameFont.Small;
            for (int i = 0; i < cells.Length; i++)
            {
                if (string.IsNullOrEmpty(cells[i].label)) continue;
                if (Text.CalcSize(cells[i].label).x > cells[i].r.width - 12f) { result = GameFont.Tiny; break; }
            }
            Text.WordWrap = prevWrap;
            Text.Font = prev;
            return result;
        }

        // Eine Fussleiste, EINE Schriftstufe (2026-09-03: im Reiter
        // "3. Kolonisten ausruesten" stand "Munition massenhaft ersetzen" deutlich kleiner zwischen seinen
        // beiden Nachbarn - genau der Fall "ein kleiner Knopf neben grossen", den ButtonRowFont fuer die
        // Kopfleiste laengst ausschliesst). BottomActionButton misst je Knopf, weiss also nichts von den
        // Nachbarn, die erst danach gezeichnet werden. Deshalb meldet der Aufrufer die Reihe vorab an -
        // Breite und Beschriftung genuegen, die Rechtecke entstehen ohnehin erst beim Zeichnen.
        // `icon: true` fuer Knoepfe mit Symbol (ButtonIconLabel), denen 36 statt 12 Punkte Rand fehlen.
        public static GameFont BottomBarFont(params (float width, string label, bool icon)[] cells)
        {
            GameFont prev = Text.Font;
            bool prevWrap = Text.WordWrap;
            Text.WordWrap = false;
            Text.Font = GameFont.Small;
            GameFont result = GameFont.Small;
            for (int i = 0; i < cells.Length; i++)
            {
                if (string.IsNullOrEmpty(cells[i].label)) continue;
                float avail = cells[i].width - (cells[i].icon ? 38f : 12f);
                if (Text.CalcSize(cells[i].label).x > avail) { result = GameFont.Tiny; break; }
            }
            Text.WordWrap = prevWrap;
            Text.Font = prev;
            return result;
        }

        // Breite eines Fussleisten-Knopfes, IMMER in Small gemessen (2026-09-04, Befund im Test:
        // auf Deutsch standen in der Fussleiste der Auftragsuebersicht die zwei LINKEN Knoepfe gross und
        // die zwei rechten klein - eine Reihe, zwei Schriftgroessen). Zwei Ursachen steckten darin, und
        // diese ist die leisere: die Breite wurde mit der Schrift gemessen, die zufaellig gerade gesetzt
        // war. Wird sie in Tiny gemessen und in Small gezeichnet, ist der Knopf zu schmal - und
        // ButtonTextFit stuft ihn dann herunter, obwohl daneben nichts heruntergestuft wurde.
        // Die laute Ursache steht an BottomBarFont: die linken Knoepfe waren an der Reihenrechnung gar
        // nicht beteiligt. Eine Fussleiste meldet ihre Knoepfe deshalb ALLE an, links wie rechts.
        public static float BottomBtnW(string label, float min, float max)
        {
            GameFont prev = Text.Font;
            bool prevWrap = Text.WordWrap;
            Text.WordWrap = false;
            Text.Font = GameFont.Small;
            float w = Mathf.Clamp(Text.CalcSize(label).x + 20f, min, max);
            Text.WordWrap = prevWrap;
            Text.Font = prev;
            return w;
        }

        // Ein Haken, dessen Beschriftung wirklich hineinpasst (2026-09-03:
        // auf Deutsch brach "Beliebige 'X'-generische Typen verwenden" im 220 Punkte breiten Feld auf ZWEI
        // Zeilen um und wurde dabei oben UND unten angeschnitten - die Zeilenhoehe reicht nur fuer eine).
        // Reihenfolge wie bei ButtonTextFit, nur mit einem Schritt davor: erst das Feld verbreitern, solange
        // maxWidth es hergibt (neben einem Haken ist meist Platz, er steht selten am Rand), dann Tiny, erst
        // dann sichtbar kuerzen. WordWrap bleibt waehrenddessen aus, damit kein Umbruch mehr entstehen kann.
        // Gibt das benutzte Rechteck zurueck - der Aufrufer haengt seinen Erklaertext daran.
        public static Rect CheckboxLabeledFit(Rect r, string label, ref bool checkOn, float maxWidth)
        {
            GameFont prevFont = Text.Font;
            bool prevWrap = Text.WordWrap;
            Text.WordWrap = false;
            if (Text.Font != GameFont.Tiny) Text.Font = GameFont.Small;
            const float boxAndGap = 34f;   // Widgets.CheckboxLabeled: Haken (24) rechts + Abstand davor
            float needed = Text.CalcSize(label).x + boxAndGap;
            if (needed > maxWidth)
            {
                Text.Font = GameFont.Tiny;
                needed = Text.CalcSize(label).x + boxAndGap;
            }
            string shown = label;
            if (needed > maxWidth) { shown = label.Truncate(maxWidth - boxAndGap); needed = maxWidth; }
            Rect used = new Rect(r.x, r.y, Mathf.Clamp(needed, r.width, maxWidth), r.height);
            Widgets.CheckboxLabeled(used, shown, ref checkOn);
            Text.WordWrap = prevWrap;
            Text.Font = prevFont;
            return used;
        }

        // Ein Zahlenfeld, das nur dann ein ECHTES Eingabefeld ist, wenn jemand es benutzen kann (2026-09-06,
        // perf-packwizard-draw-time - gemessen: "Posten Zaehlfeld+X" 24 ms/s bei 2600 Aufrufen/s, also
        // 9 us je Feld). Widgets.TextFieldNumeric parst und formatiert in jedem Durchgang, auch fuer die
        // acht Felder, in denen niemand tippt. Steht die Maus nicht ueber der Zeile und hat das Feld nicht
        // den Tastaturfokus, wird nur sein Text im Feldstil gezeichnet - pixelgleich, ohne Eingabelogik.
        // `hot` ist die Zeile (nicht nur das Feld), damit das echte Feld schon da ist, bevor der Klick landet.
        public static void NumericFieldLazy(Rect r, string controlName, bool hot, ref int val, ref string buf, float min, float max)
        {
            bool focused = GUI.GetNameOfFocusedControl() == controlName;
            if (hot || focused)
            {
                GUI.SetNextControlName(controlName);
                Widgets.TextFieldNumeric(r, ref val, ref buf, min, max);
                return;
            }
            if (buf == null) buf = val.ToString();
            GUI.Label(r, buf, Text.CurTextFieldStyle);
        }

        public static bool ButtonTextFit(Rect r, string label, bool drawBackground = true, bool active = true)
        {
            GameFont prevFont = Text.Font;
            bool prevWrap = Text.WordWrap;
            Text.WordWrap = false;
            // Ein Knopf erbt nie eine GROESSERE Schrift als Small (2026-08-30: im Kopf des Loadout
            // Planners standen 'Fetch gear now' und 'Arm my Colony' ploetzlich in Tiny, im Zuweisungs-Reiter
            // nicht). Beide passen in Small locker; Tiny kam nur, weil hier mit der Schrift gemessen wurde, die
            // gerade gesetzt war - und die war Medium, uebrig von einer vorherigen Zeichnung. Medium ist nie eine
            // Knopfschrift. Tiny bleibt erhalten, wenn ein Aufrufer sie bewusst fuer eine ganze Reihe gesetzt hat
            // (ButtonRowFont).
            if (Text.Font != GameFont.Tiny) Text.Font = GameFont.Small;
            float avail = r.width - 12f;
            string shown = label;
            if (!string.IsNullOrEmpty(shown) && Text.CalcSize(shown).x > avail)
            {
                Text.Font = GameFont.Tiny;
                if (Text.CalcSize(shown).x > avail) shown = shown.Truncate(avail);
            }
            bool clicked = Widgets.ButtonText(r, shown, drawBackground, true, active);
            Text.WordWrap = prevWrap;
            Text.Font = prevFont;
            return clicked;
        }

        // The "Arm my Colony" button itself - shared by Dialog_CEReloadedTabs' own header and the shortcut
        // this mod adds to RimWorld's native Assign (policy table) tab, so a player never has to open the
        // full tab window just to kick off a run. Draws the button; the caller decides which Dialog_
        // GenerateLoadouts/AmmoBills/AssignLoadouts instances to hand Dialog_ArmColony (a fresh throwaway
        // set for the native-tab shortcut, the tab window's own persistent ones when called from there).
        public static bool DrawArmColonyButton(Rect rect)
        {
            bool arm = Widgets.ButtonText(rect, "");
            ButtonIconLabel(rect, TexWand, "CER_LoadoutUtility_ArmColonyButtonLabel".Translate());
            TooltipHandler.TipRegion(rect, "CER_LoadoutUtility_ArmColonyButtonTooltip".Translate());
            return arm;
        }

        // "Fetch gear now" (2026-08-30) - links neben Arm my Colony, in RimWorlds Zuweisungs-Reiter UND im Kopf
        // unseres Fensters, damit beide Orte dieselben drei Knoepfe zeigen. Mechanik in FetchGearNow.
        public static void DrawFetchGearButton(Rect rect)
        {
            if (ButtonTextFit(rect, "CER_LoadoutUtility_FetchGearButtonLabel".Translate().ToString()))
            {
                int started = FetchGearNow.Trigger(out int withLoadout, out int skipped, out int blockedFull);
                // Volle Kolonisten stehen MIT in der Meldung (2026-09-01): CE holt bei vollem
                // Bulk/Gewicht schlicht nichts, und ohne diese Zahl las sich das wie 'alles erledigt'.
                // Einzahl bei genau EINEM (2026-09-04, Befund im Test: "1 colonists are fetching
                // their gear now. 1 of them can't carry any more" - zweimal Mehrzahl bei der Zahl 1).
                // RimWorlds Translate kennt keine Pluralregeln; die Sprachen loesen das ueber je einen
                // eigenen Schluessel, und die Wahl gehoert deshalb hierher.
                string fullNote = blockedFull > 0
                    ? " " + (blockedFull == 1
                        ? "CER_LoadoutUtility_FetchGearMsgFullSuffixOne".Translate().ToString()
                        : "CER_LoadoutUtility_FetchGearMsgFullSuffix".Translate(blockedFull.ToString()).ToString())
                    : "";
                if (started > 0)
                    Messages.Message((started == 1
                        ? "CER_LoadoutUtility_FetchGearMsgSentOne".Translate().ToString()
                        : "CER_LoadoutUtility_FetchGearMsgSent".Translate(started.ToString()).ToString()) + fullNote,
                        MessageTypeDefOf.TaskCompletion, false);
                else
                    Messages.Message("CER_LoadoutUtility_FetchGearMsgNone".Translate(withLoadout.ToString(), skipped.ToString()).ToString() + fullNote, MessageTypeDefOf.RejectInput, false);
            }
            // Vorschau statt Erklaertext allein (2026-08-30): wer wuerde was abbrechen und was holen - siehe FetchGearNow.PreviewTooltip.
            // FESTE ID, sonst flackert der Kasten (2026-08-30: der Tooltip flackerte dauernd auf und weg).
            // RimWorld erkennt einen Tooltip an der uniqueId seines TipSignals - und ein blosser String
            // leitet die aus dem TEXT ab. Dieser Text nennt die laufende Taetigkeit jedes Kolonisten, aendert sich also
            // im Sekundentakt: jedes Mal galt er als NEUER Tooltip, der alte ging zu, der neue auf. Mit fester Id bleibt
            // EIN Kasten offen und schreibt sich nur neu. Die Bereitschaftsanzeige daneben macht es seit jeher so.
            if (TipWanted(rect)) TooltipHandler.TipRegion(rect, new TipSignal(() => FetchGearNow.PreviewTooltip(), 0x0FE7C4));
        }

        // The consolidated readiness ampel (WORKFLOW_OVERHAUL_PLAN.md section 4) - shared by Dialog_
        // CEReloadedTabs' header and the shortcut this mod adds to RimWorld's native Assign (policy table)
        // tab, so "is my squad ready" and its one-click fixes are reachable without opening the full tab
        // window. Unlike the vanilla Alert_CER_Readiness (informational, standard click-to-select-pawns),
        // this IS the actionable one-click fix: clicking it with any production-related issue present opens
        // Dialog_SupplyLoadouts directly, pre-filled for exactly the affected colonists. Draws right-aligned
        // within `available`. getLoadouts/getAssign are lazy - only ever invoked inside a fix's own click
        // closure, never during the per-frame draw, so a caller with no persistent dialog instance around
        // (the native-tab shortcut) can safely pass a factory that builds one on first actual use instead of
        // eagerly constructing (and re-scanning the map for) one every frame just to draw a status dot.
        // Ab wie vielen gleichartigen Kolonisten-Eintraegen das Klick-Menue EINEN Sammel-Eintrag zeigt (2026-08-30).
        private const int ReadinessGroupFrom = 5;

        private static void OpenBillOverview()
        {
            Dialog_CEReloadedTabs tabs = Find.WindowStack.WindowOfType<Dialog_CEReloadedTabs>();
            bool open = tabs != null;
            if (tabs == null) tabs = new Dialog_CEReloadedTabs();
            tabs.JumpToAmmoBills();
            if (!open) Find.WindowStack.Add(tabs);
        }

        // Nachschub-Fenster fuer genau diese Kolonisten; ist dort nichts herzustellen, ersatzweise die Ausruestung des ersten.
        private static void OpenSupplyForPawns(List<Pawn> pawns)
        {
            if (pawns == null || pawns.Count == 0) return;
            Dialog_SupplyLoadouts.Request req = Dialog_AssignLoadouts.BuildSupplyRequestForPawns(pawns);
            Dialog_SupplyLoadouts dlg = req != null ? new Dialog_SupplyLoadouts(req) : null;
            if (dlg != null && dlg.AnythingToProduce) Find.WindowStack.Add(dlg);
            else Find.WindowStack.Add(new Dialog_PawnGear(pawns[0]));
        }

        public static void DrawReadinessWidget(Rect available, Func<Dialog_GenerateLoadouts> getLoadouts, Func<Dialog_AssignLoadouts> getAssign)
        {
            CEReloadedSettings s = CEReloadedMod.settings;
            if (s == null || !s.showReadinessAlerts || !PlayerMaps().Any()) return;

            // Never been through the workflow at all (no loadout) is the most fundamental gap - and the most
            // actionable one-click fix, since there is nothing to "supply" yet: it needs the combined Generate+
            // Assign flow, not the Supply popup. Checked first so it takes priority over the other buckets.
            List<Pawn> noLoadout = new List<Pawn>();
            if (s.alertNoLoadout) noLoadout.AddRange(CombatReadiness.Culprits(ReadinessIssue.NoLoadout));
            List<Pawn> noLoadoutDistinct = noLoadout.Distinct().ToList();

            List<Pawn> prod = new List<Pawn>();
            if (s.alertWeaponWithoutAmmo) prod.AddRange(CombatReadiness.Culprits(ReadinessIssue.WeaponWithoutAmmo));
            if (s.alertAmmoNotProduced) prod.AddRange(CombatReadiness.Culprits(ReadinessIssue.AmmoNotProduced));
            // Wer davon 'Waffe ohne Munitionsslot' ist - fuer die Dreiteilung unten (2026-08-30).
            HashSet<Pawn> noAmmoSlotC = new HashSet<Pawn>(s.alertWeaponWithoutAmmo ? CombatReadiness.Culprits(ReadinessIssue.WeaponWithoutAmmo) : new List<Pawn>());
            // Kept separate from the other two (2026-08-01, see the click-handler's own
            // comment for the full mechanism) - CombatReadinessAlerts.Recompute already determines
            // WeaponUnavailable via a scarcity-pool CLAIM against colony-wide stock (each pawn decrements a
            // shared weaponStock dict as it's checked, same model Dialog_AssignLoadouts.SingleFulfillStatus
            // uses for its own per-pawn cells) - so a pawn only ends up here when the pool was ALREADY
            // exhausted for them specifically, a strictly more precise verdict than a colony-wide aggregate
            // demand-vs-stock sum could ever give.
            List<Pawn> weaponUnavailC = s.alertWeaponUnavailable ? CombatReadiness.Culprits(ReadinessIssue.WeaponUnavailable) : new List<Pawn>();
            List<Pawn> prodDistinct = prod.Concat(weaponUnavailC).Distinct().Except(noLoadoutDistinct).ToList();

            List<Pawn> lowAmmoC = s.alertLowAmmo ? CombatReadiness.Culprits(ReadinessIssue.LowAmmo) : new List<Pawn>();
            List<Pawn> overloadedC = s.alertOverloaded ? CombatReadiness.Culprits(ReadinessIssue.Overloaded) : new List<Pawn>();
            // CapacityBlocked (2026-08-01): this widget never referenced this category at all,
            // even though it's a full first-class, player-toggleable ReadinessIssue wired into the vanilla
            // RimWorld alert (Alert_CER_Readiness's own Categories array) - a pawn who IS CapacityBlocked
            // showed solid "Ready" here while the vanilla alert bar correctly flagged them, and there was no
            // click-to-fix entry for this category at all. Routes the same place as Overloaded: Dialog_
            // PawnGear's Drop action is the real, direct fix (frees carry space so CE can fetch the rest of
            // the loadout).
            List<Pawn> capacityBlockedC = s.alertCapacityBlocked ? CombatReadiness.Culprits(ReadinessIssue.CapacityBlocked) : new List<Pawn>();
            // AssignedNotCarried (2026-08-09) landet in DIESEM Eimer, nicht bei den Produktions-Punkten:
            // es gibt nichts herzustellen, die Waffe liegt ja da. Der Kolonist hat sie nur nicht geholt -
            // dieselbe Art Problem wie ueberladen oder blockiert, und derselbe Klickweg (Ausruestungs-Tab).
            List<Pawn> notCarriedC = s.alertAssignedNotCarried ? CombatReadiness.Culprits(ReadinessIssue.AssignedNotCarried) : new List<Pawn>();
            List<Pawn> otherDistinct = lowAmmoC.Concat(overloadedC).Concat(capacityBlockedC).Concat(notCarriedC).Distinct().Except(prodDistinct).Except(noLoadoutDistinct).ToList();
            // Which issue-tags apply to one pawn, joined ("overloaded, low ammo") - shared by the click-to-fix
            // menu labels and the tooltip grouping below, so both always agree. Grouping by this exact string
            // (rather than 3 hardcoded overloaded/lowAmmo/both buckets) is what let CapacityBlocked join as a
            // 3rd dimension without needing 4 more combination-specific buckets/keys.
            string OtherIssueTags(Pawn p)
            {
                List<string> parts = new List<string>();
                if (overloadedC.Contains(p)) parts.Add("CER_LoadoutUtility_WhatOverloaded".Translate().ToString());
                if (lowAmmoC.Contains(p)) parts.Add("CER_LoadoutUtility_WhatLowAmmo".Translate().ToString());
                if (capacityBlockedC.Contains(p)) parts.Add("CER_LoadoutUtility_WhatCapacityBlocked".Translate().ToString());
                if (notCarriedC.Contains(p)) parts.Add("CER_LoadoutUtility_WhatAssignedNotCarried".Translate().ToString());
                return string.Join(", ", parts);
            }

            int total = noLoadoutDistinct.Count + prodDistinct.Count + otherDistinct.Count;
            string label = total == 0 ? "CER_LoadoutUtility_ReadinessReady".Translate().ToString()
                : (total == 1 ? "CER_LoadoutUtility_ReadinessIssueSingular".Translate(total.ToString())
                    : "CER_LoadoutUtility_ReadinessIssuePlural".Translate(total.ToString())).ToString();
            Color dotColor = total == 0 ? ColGood
                : (noLoadoutDistinct.Count > 0 || prodDistinct.Count > 0 ? ColWarn : new Color(0.9f, 0.85f, 0.35f));

            // Kleiner statt zweizeilig (2026-08-30, im Test: auf Deutsch brach '9 brauchen Aufmerksamkeit' in zwei
            // Zeilen um und wurde im 26 Punkte hohen Feld abgeschnitten). Erst Small versuchen, sonst Tiny - dieselbe
            // Stufenfolge wie bei den Knoepfen (ButtonTextFit).
            GameFont prevReadFont = Text.Font;
            bool prevReadWrap = Text.WordWrap;
            Text.WordWrap = false;
            Text.Font = GameFont.Small;
            if (Text.CalcSize(label).x + 32f > available.width) Text.Font = GameFont.Tiny;
            float w = Mathf.Min(available.width, Text.CalcSize(label).x + 32f);
            Rect r = new Rect(available.xMax - w, available.y, w, available.height);

            // Anklickbar auch bei "alles gruen", wenn es bessere Munition zu holen gibt (2026-08-13):
            // sonst waere der Hinweis genau dann unerreichbar, wenn sonst nichts ansteht - und das ist der
            // Normalfall nach einer abgeschlossenen Forschung. Die BESCHRIFTUNG und die Punktfarbe bleiben
            // bewusst unberuehrt: eine Gelegenheit ist kein Problem und faerbt die Anzeige nicht ein.
            bool anyAction = total > 0 || TemplatesWithBetterAmmo().Count > 0;
            if (anyAction && Mouse.IsOver(r)) Widgets.DrawHighlight(r);
            GUI.color = dotColor;
            GUI.DrawTexture(new Rect(r.x + 7f, r.y + r.height / 2f - 4f, 8f, 8f), BaseContent.WhiteTex);
            GUI.color = Color.white;
            Text.Anchor = TextAnchor.MiddleLeft;
            // Letzte Sicherung (2026-09-03: auf Deutsch stand da
            // "9 brauchen Aufmerksaml" - mitten im Wort abgeschnitten und OHNE Auslassungspunkte).
            // Reicht auch Tiny nicht in das Feld, das der Aufrufer hergibt, dann wird sichtbar gekuerzt
            // statt stumpf abgeschnitten - dieselbe Zusage, die ButtonTextFit fuer Knoepfe gibt.
            Rect labelRect = new Rect(r.x + 22f, r.y, r.width - 24f, r.height);
            string shownLabel = label;
            if (Text.CalcSize(shownLabel).x > labelRect.width) shownLabel = shownLabel.Truncate(labelRect.width);
            Widgets.Label(labelRect, shownLabel);
            Text.Anchor = TextAnchor.UpperLeft;
            Text.Font = prevReadFont;
            Text.WordWrap = prevReadWrap;

            if (anyAction && Widgets.ButtonInvisible(r))
            {
                // One issue -> jump straight to its fix; several -> a menu, so every problem/pawn is one click away.
                var acts = new List<(string label, Action act)>();
                if (noLoadoutDistinct.Count > 0)
                    acts.Add(("CER_LoadoutUtility_ReadinessNoLoadoutOption".Translate(noLoadoutDistinct.Count.ToString()).ToString(), () =>
                    {
                        // Direkt in die Zuweisungs-Vorschau (Lazy-Umbau 2026-09-01): der fruehere
                        // Erzeugen-Zwischenschritt entfiel - der Loeser waehlt Waffen direkt, Vorlagen
                        // entstehen unsichtbar beim Uebernehmen. keepAssigned bleibt an (Vorgabe), damit
                        // dieser Ein-Klick-Weg nie eine bestehende, von Hand gepflegte Zuweisung verwirft -
                        // dieselbe Schutzabsicht wie das fruehere presetLockExisting.
                        Dialog_AssignLoadouts assignDialog = getAssign();
                        assignDialog.RefreshPawns();
                        Find.WindowStack.Add(new Dialog_SquadSetup(null, assignDialog, presetKeepAssigned: true));
                    }
                    ));
                if (prodDistinct.Count > 0)
                {
                    // Build the request NOW, not on click: the detector only sees "weapon without ammo / no bill",
                    // the request does the real math against stock and running production. If that math says the
                    // shortfall is already covered, a Supply entry would be a dead-end ("nothing to supply"), so
                    // the pawns are offered as gear entries instead - picking the items up IS the fix then.
                    //
                    // Frueher stand hier eine Ausnahme fuer WeaponUnavailable-Kolonisten ("trustedGap"):
                    // die Warnung rechnete reihum, das Nachschub-Fenster kolonieweit, und wo die beiden sich
                    // widersprachen, bekam die Warnung recht - das Fenster ging trotzdem auf. Genau daran
                    // blieb ein Test am 2026-08-17 haengen: "Production gaps (2) - open Supply"
                    // fuehrte in ein Fenster mit "Everything needed is already in stock or in production".
                    // Die Ausnahme ist weg, weil der Widerspruch weg ist: beide Seiten lesen jetzt aus
                    // Dialog_AssignLoadouts.WeaponShortfallsByPawn. Ist hier nichts
                    // zu produzieren, dann fehlt dieser Gruppe wirklich nichts - dann ist der Weg ueber die
                    // Ausruestung der richtige, und nicht ein Fenster, das nichts anzubieten hat.
                    // DREI Sorten statt einer (2026-08-30: bei Production gaps fuer 4 Kolonisten muessten
                    // mehr Posten angezeigt werden). Die Warnung AmmoNotProduced feuert bei
                    // Schwelle 0, sobald fuer eine Sorte KEIN Auftrag laeuft - auch bei vollem Lager; das Fenster zeigt nur
                    // echte Fehlmengen. Drei der vier waren 'kein Auftrag, Bestand reicht', einer eine Fehlmenge - alle
                    // vier hiessen 'Produktionsluecke'. Jetzt je Kolonist getrennt, mit derselben Frage wie das Fenster:
                    //   das Nachschub-Fenster haette fuer IHN eine Zeile -> Production gaps (Posten) - open Supply
                    //   Waffe ohne Munitionsslot im Loadout               -> Munitionsfenster
                    //   sonst (kein Auftrag, Bestand reicht vorerst)      -> Bill Overview
                    List<Pawn> gapPawns = new List<Pawn>(), noBillPawns = new List<Pawn>(), noAmmoSlotPawns = new List<Pawn>();
                    foreach (Pawn p in prodDistinct)
                    {
                        if (ReadinessGapRowsFor(p) > 0) gapPawns.Add(p);
                        else if (noAmmoSlotC.Contains(p)) noAmmoSlotPawns.Add(p);
                        else noBillPawns.Add(p);
                    }
                    Dialog_SupplyLoadouts.Request req = gapPawns.Count > 0 ? Dialog_AssignLoadouts.BuildSupplyRequestForPawns(gapPawns) : null;
                    Dialog_SupplyLoadouts dlg = req != null ? new Dialog_SupplyLoadouts(req) : null;
                    // `dlg != null` ist tragend, keine Zierde (2026-08-02): BuildSupplyRequestForPawns gibt
                    // null zurueck, wenn nichts fehlt, und acts[0] wird OHNE Menue ausgefuehrt, wenn es der
                    // einzige Punkt ist - ohne die Pruefung war das ein Ein-Klick-Absturz an der immer
                    // sichtbaren Bereitschaftsanzeige.
                    // AnythingToProduce statt AnythingToSupply (2026-08-13,
                    // zweite Fundstelle desselben Musters): dieser Weg kommt aus einer WARNUNG ueber einen
                    // MANGEL. Ein Fenster, in dem nur die Aufraeumliste "diese Auftraege braucht niemand
                    // mehr" steht, beantwortet die Frage nicht - es wechselt sie.
                    // Die Zahl im Knopf ist die der POSTEN, die das Fenster dann zeigt - nicht die der Kolonisten
                    // (2026-08-30). Die Kolonisten stehen im Tooltip daneben, mit Namen.
                    if (dlg != null && dlg.AnythingToProduce)
                        acts.Add(("CER_LoadoutUtility_ReadinessProdGapsOption".Translate(dlg.ProduceRowCount.ToString()).ToString(), () => Find.WindowStack.Add(dlg)));
                    else
                        foreach (Pawn p in gapPawns)
                        {
                            Pawn local = p;
                            // 'liegt im Lager, nicht abgeholt' - seit 2026-08-30 der Abhol-Befehl fuer genau diesen
                            // Kolonisten statt seiner Ausruestung (dort gab es nur 'Drop', das half hier nie).
                            acts.Add(("CER_LoadoutUtility_ReadinessSuppliesInStockOption".Translate(local.LabelShortCap).ToString(),
                                () => FetchGearNow.TriggerFor(local)));
                        }
                    if (noBillPawns.Count > 0)
                        acts.Add(("CER_LoadoutUtility_ReadinessNoBillOption".Translate(noBillPawns.Count.ToString()).ToString(), () =>
                        {
                            Dialog_CEReloadedTabs tabs = Find.WindowStack.WindowOfType<Dialog_CEReloadedTabs>();
                            bool open = tabs != null;
                            if (tabs == null) tabs = new Dialog_CEReloadedTabs();
                            tabs.JumpToAmmoBills();
                            if (!open) Find.WindowStack.Add(tabs);
                        }));
                    if (noAmmoSlotPawns.Count > 0)
                    {
                        List<Pawn> localNoAmmo = noAmmoSlotPawns;
                        acts.Add(("CER_LoadoutUtility_ReadinessNoAmmoSlotOption".Translate(localNoAmmo.Count.ToString()).ToString(),
                            () => Find.WindowStack.Add(new Dialog_ChangeAmmo(localNoAmmo))));
                    }
                }
                // LowAmmo-ONLY pawns (not also overloaded/capacity-blocked) get the SAME "try Supply first"
                // treatment as prodDistinct just above, instead of always routing to Dialog_PawnGear
                // (2026-08-01, direction confirmed): PawnGear's only control is Drop,
                // which does nothing for a pawn who simply doesn't have enough ammo - there's nothing of
                // theirs to drop. Overloaded/CapacityBlocked pawns keep going to Dialog_PawnGear unchanged -
                // Drop is the real, direct fix for both of those.
                List<Pawn> lowAmmoOnlyDistinct = otherDistinct.Where(p => lowAmmoC.Contains(p) && !overloadedC.Contains(p) && !capacityBlockedC.Contains(p)).ToList();
                List<Pawn> otherRemaining = otherDistinct.Except(lowAmmoOnlyDistinct).ToList();
                if (lowAmmoOnlyDistinct.Count > 0)
                {
                    // NACH URSACHE GETRENNT (2026-08-13). Vorher lief JEDER
                    // Fall in dieselbe Antwort: Nachschub-Fenster fuer diese Pawns bauen, und wenn darin
                    // irgendetwas stand, wurde es angeboten. "Irgendetwas" war im Test ein Vorschlag,
                    // Sprengsatz-Auftraege abzubrechen - die Munition, wegen der die Warnung ueberhaupt kam,
                    // stand nicht darin. Das Fenster hat die Frage nicht beantwortet, es hat sie gewechselt.
                    // Ursache dafuer war die Toroeffnung: AnythingToSupply ist auch dann wahr, wenn NUR der
                    // Abschnitt "Obsolete bills" gefuellt ist.
                    // Siehe CombatReadiness.LowAmmoState fuer die drei Ursachen und warum sie verschiedene
                    // Antworten brauchen.
                    // Pending traegt seine Einstufung MIT (2026-08-17 abends):
                    // die Schleife unten hat den Grund bisher selbst noch einmal ermittelt, und genau das war
                    // die zweite Rechnung. Siehe dort.
                    var pending = new List<(Pawn pawn, CombatReadiness.LowAmmoInfo info)>();
                    var planTooSmall = new List<Pawn>();
                    var notProduced = new List<Pawn>();
                    // "Es fehlt, und ich kann es hier nicht einmal anbieten" - siehe unten, wo diese Liste
                    // gefuellt wird. Bewusst NICHT mehr `pending`: das haette ihm "Nachschub ist unterwegs"
                    // gesagt, wo nichts unterwegs ist.
                    var noneOffered = new List<Pawn>();
                    var lowAmmoDefs = new HashSet<ThingDef>();
                    foreach (Pawn p in lowAmmoOnlyDistinct)
                    {
                        if (!CombatReadiness.LowAmmoState(p, out CombatReadiness.LowAmmoInfo info)) { pending.Add((p, default(CombatReadiness.LowAmmoInfo))); continue; }
                        switch (info.cause)
                        {
                            case CombatReadiness.LowAmmoCause.PlanTooSmall: planTooSmall.Add(p); break;
                            case CombatReadiness.LowAmmoCause.NotProduced:
                                notProduced.Add(p);
                                foreach (ThingDef a in info.AmmoToCheck) lowAmmoDefs.Add(a);
                                break;
                            default: pending.Add((p, info)); break;
                        }
                    }

                    // (C) Wirklich fehlend: Nachschub-Fenster, aber AUF DIESE MUNITION VERENGT. Die
                    // Bedarfsrechnung kommt weiter aus BuildSupplyRequestForPawns (dieselbe wie ueberall,
                    // damit hier keine zweite Wahrheit entsteht), gezeigt wird davon nur der Munitionsteil -
                    // ohne Waffen, ohne Packs und vor allem ohne die Obsolet-Aufraeumliste, die den ganzen
                    // Fehlgriff ausgeloest hat. Bleibt danach nichts uebrig, wird auch nichts angeboten.
                    if (notProduced.Count > 0)
                    {
                        Dialog_SupplyLoadouts.Request full = Dialog_AssignLoadouts.BuildSupplyRequestForPawns(notProduced);
                        var focused = new Dialog_SupplyLoadouts.Request
                        {
                            mainWeapons = new Dictionary<ThingDef, int>(),
                            specificAmmo = full?.specificAmmo?.Where(kv => lowAmmoDefs.Contains(kv.Key)).ToDictionary(kv => kv.Key, kv => kv.Value)
                                           ?? new Dictionary<ThingDef, int>(),
                            genericAmmo = full?.genericAmmo?.Where(kv => GetAmmoTypesFor(GetWeaponForGenericAmmo(kv.Key)).Any(a => a is ThingDef td && lowAmmoDefs.Contains(td)))
                                              .ToDictionary(kv => kv.Key, kv => kv.Value)
                                           ?? new Dictionary<LoadoutGenericDef, int>(),
                            assignedUsedBy = full?.assignedUsedBy,
                            templateUsedBy = full?.templateUsedBy,
                            // Auch hier durchreichen (2026-08-18), obwohl dieser verengte Aufruf heute keine
                            // Wurfsprengstoff-Zeile enthaelt: die vier anderen Baustellen, die so einen
                            // Request zusammensetzen, tun es jetzt alle. Eine, die es nicht tut, ist genau
                            // die Luecke, durch die derselbe Fehler beim naechsten Umbau zurueckkommt.
                            weaponAssignedUsedBy = full?.weaponAssignedUsedBy,
                            weaponTemplateUsedBy = full?.weaponTemplateUsedBy
                        };
                        if (focused.specificAmmo.Count > 0 || focused.genericAmmo.Count > 0)
                        {
                            Dialog_SupplyLoadouts lowDlg = new Dialog_SupplyLoadouts(focused);
                            acts.Add(("CER_LoadoutUtility_ReadinessLowAmmoSupplyOption".Translate(notProduced.Count.ToString()).ToString(),
                                () => Find.WindowStack.Add(lowDlg)));
                        }
                        else
                        {
                            // Kein herstellbarer Eintrag daraus (nicht erforscht, kein Rezept, nichts uebrig
                            // nach der Verengung) - dann bleibt nur die Ausruestung. Frueher landeten diese
                            // Kolonisten in `pending` und bekamen damit "Nachschub ist unterwegs" zu lesen,
                            // obwohl LowAmmoState fuer sie gerade das Gegenteil festgestellt hatte: nichts
                            // greifbar, kein Auftrag (2026-08-17 - im Test fiel genau
                            // diese Zeile fuer Ltz auf, mit dem widersprechenden Tooltip daneben).
                            // "Warten" war hier nie die ehrlichere Antwort, sondern die falsche.
                            noneOffered.AddRange(notProduced);
                        }
                    }

                    // (B) Das Loadout selbst fordert zu wenig - kein Bestand und kein Auftrag der Welt macht
                    // diese Warnung weg, nur eine groessere Menge im Loadout. Deshalb direkt dorthin, wo man
                    // sie einstellt. Rollen-Traeger bekommen denselben Weg: Dialog_ChangeAmmo zeigt fuer sie
                    // die Rollen-Munition an und sagt, dass sie aus der Rolle stammt.
                    if (planTooSmall.Count > 0)
                    {
                        List<Pawn> local = planTooSmall;
                        acts.Add(("CER_LoadoutUtility_ReadinessLowAmmoPlanOption".Translate(local.Count.ToString()).ToString(),
                            () => Find.WindowStack.Add(new Dialog_ChangeAmmo(local))));
                    }

                    // (A) Unterwegs: es liegt im Lager oder wird gerade gebaut. Bisheriges Verhalten, jetzt
                    // nur noch fuer den Fall, fuer den es gedacht war - zur Werkbank springen, wenn wirklich
                    // etwas laeuft, sonst die Ausruestung zeigen.
                    // DIE ZWEITE RECHNUNG, DIE HIER STAND (2026-08-17 abends).
                    //
                    // Heute frueh ist LowAmmoSuffix - der Text im Tooltip DERSELBEN Anzeige - auf LowAmmoState
                    // umgestellt worden, mit genau dieser Begruendung. Diese Schleife zwei Bildschirmseiten
                    // darueber ist dabei stehengeblieben und hat den Grund weiter selbst ermittelt: sie sah nur
                    // die GELADENE Sorte (CompAmmoUser.CurrentAmmo) und fragte allein, ob dafuer irgendwo ein
                    // Auftrag laeuft. Der Widerspruch, woertlich aus dem Test:
                    //   Tooltip: "1 affected by low ammo (opens their gear) - Sammy (30274 in stock)"
                    //   Menue:   "Sammy (already in production) - jump to bench"
                    // Derselbe Mann, dieselbe Anzeige, ein Klick dazwischen - zwei Ursachen und zwei Ziele.
                    //
                    // LowAmmoState traegt beide Zahlen laengst mit (reachable ueber ALLE nutzbaren Sorten,
                    // anyBill ueber alle davon). Damit entscheidet dieselbe Einstufung wie oben, und der
                    // Unterschied kann nicht wiederkommen:
                    //   greifbar > 0  -> es liegt da, er hat es nur nicht geholt. Die Werkbank hilft ihm nicht,
                    //                    die Ausruestung schon. (Das war der Fall im Test: 30274 Schuss.)
                    //   sonst         -> es wird gebaut. Zur Bank springen, und die suchen wir ueber ALLE
                    //                    nutzbaren Sorten statt nur ueber die geladene - sonst findet sie
                    //                    niemand, wenn gerade eine andere Sorte des Sets hergestellt wird.
                    // FindActiveBillBench bleibt, aber nur noch als NACHSCHLAG (welche Bank ist es), nicht mehr
                    // als Urteil (laeuft ueberhaupt etwas) - das sagt anyBill.
                    // GRUPPIEREN ab ReadinessGroupFrom gleichartigen Eintraegen (2026-08-30, grosse Kolonie mit 29
                    // Kolonisten': ein Eintrag JE KOLONIST machte aus dem Menue eine zweispaltige Vollbildliste, 24-mal derselbe
                    // Text). Bis vier bleibt der Name, ab fuenf steht die Zahl und der Griff gilt fuer alle.
                    // (B) liegt da, nicht geholt -> holen; (B') schon in Produktion -> zur Bank; (B'') unterwegs -> Ausruestung bzw.
                    // gesammelt die Auftragsuebersicht; (C ohne Angebot) nichts da, nichts in Arbeit -> Ausruestung bzw. gesammelt
                    // das Nachschub-Fenster fuer genau diese Kolonisten.
                    var fetchStock = new List<Pawn>();
                    var jumpBench = new List<(Pawn pawn, Building_WorkTable bench)>();
                    var waitGear = new List<Pawn>();
                    foreach (var e in pending)
                    {
                        if (e.info.reachable > 0) { fetchStock.Add(e.pawn); continue; }
                        Building_WorkTable bench = null;
                        foreach (ThingDef a in e.info.AmmoToCheck ?? new List<ThingDef>())
                        {
                            bench = FindActiveBillBench(a);
                            if (bench != null) break;
                        }
                        if (bench != null) jumpBench.Add((e.pawn, bench)); else waitGear.Add(e.pawn);
                    }
                    if (fetchStock.Count >= ReadinessGroupFrom)
                    {
                        List<Pawn> local = fetchStock;
                        acts.Add(("CER_LoadoutUtility_ReadinessSuppliesInStockAllOption".Translate(local.Count.ToString()).ToString(),
                            () => FetchGearNow.TriggerAll(local)));
                    }
                    else
                        foreach (Pawn p in fetchStock)
                        {
                            Pawn local = p;
                            acts.Add(("CER_LoadoutUtility_ReadinessSuppliesInStockOption".Translate(local.LabelShortCap).ToString(),
                                () => FetchGearNow.TriggerFor(local)));
                        }
                    if (jumpBench.Count >= ReadinessGroupFrom)
                    {
                        Building_WorkTable localBench = jumpBench[0].bench;
                        acts.Add(("CER_LoadoutUtility_ReadinessLowAmmoJumpAllOption".Translate(jumpBench.Count.ToString()).ToString(),
                            () => JumpToBench(localBench)));
                    }
                    else
                        foreach (var e in jumpBench)
                        {
                            Pawn local = e.pawn; Building_WorkTable localBench = e.bench;
                            acts.Add(("CER_LoadoutUtility_ReadinessLowAmmoJumpOption".Translate(local.LabelShortCap).ToString(),
                                () => JumpToBench(localBench)));
                        }
                    if (waitGear.Count >= ReadinessGroupFrom)
                        acts.Add(("CER_LoadoutUtility_ReadinessLowAmmoWaitAllOption".Translate(waitGear.Count.ToString()).ToString(), OpenBillOverview));
                    else
                        foreach (Pawn p in waitGear)
                        {
                            Pawn local = p;
                            acts.Add(("CER_LoadoutUtility_ReadinessLowAmmoWaitOption".Translate(local.LabelShortCap).ToString(),
                                () => Find.WindowStack.Add(new Dialog_PawnGear(local))));
                        }
                    if (noneOffered.Count >= ReadinessGroupFrom)
                    {
                        List<Pawn> local = noneOffered;
                        acts.Add(("CER_LoadoutUtility_ReadinessLowAmmoNoneAllOption".Translate(local.Count.ToString()).ToString(),
                            () => OpenSupplyForPawns(local)));
                    }
                    else
                        foreach (Pawn p in noneOffered)
                        {
                            Pawn local = p;
                            acts.Add(("CER_LoadoutUtility_ReadinessLowAmmoNoneOption".Translate(local.LabelShortCap).ToString(),
                                () => Find.WindowStack.Add(new Dialog_PawnGear(local))));
                        }

                    // (C ohne Angebot) Es fehlt wirklich, und dieses Fenster hat dazu nichts anzubieten.
                    // Derselbe Weg wie bei (A) - die Ausruestung -, aber mit der Wahrheit im Text: der
                    // Tooltip derselben Anzeige sagt fuer diese Kolonisten "nichts auf Lager, nichts in
                    // Arbeit", und beide lesen dieselbe Einstufung aus LowAmmoState.
                    foreach (Pawn p in noneOffered)
                    {
                        Pawn local = p;
                        acts.Add(("CER_LoadoutUtility_ReadinessLowAmmoNoneOption".Translate(local.LabelShortCap).ToString(),
                            () => Find.WindowStack.Add(new Dialog_PawnGear(local))));
                    }
                }
                // Bessere Munition fuer angeheftete Vorlagen (2026-08-13).
                // Bewusst HIER und ausdruecklich NICHT als Vanilla-Warnung in der rechten Leiste: das soll
                // nicht dauernd als Problem in der rechten Vanilla-Leiste stehen, sehr wohl aber als Punkt in
                // der internen Fehleruebersicht neben Arm my Colony. Diese Anzeige IST diese Uebersicht.
                // Kein Kolonisten-Problem, deshalb steht es hinter den Kolonistenzeilen und zaehlt auch nicht
                // in deren Zahl mit: es ist eine Gelegenheit, kein Mangel.
                List<Loadout> betterAmmo = TemplatesWithBetterAmmo();
                if (betterAmmo.Count > 0)
                    acts.Add(("CER_LoadoutUtility_ReadinessBetterAmmoOption".Translate(betterAmmo.Count.ToString()).ToString(),
                        () => Find.WindowStack.Add(new Dialog_MassReplaceAmmo())));

                // 'Zugewiesen, traegt nichts': holen lassen nur, wenn wirklich etwas Holbares liegt - sonst ist Nachschub der
                // richtige Weg (Bot: 'Cloud ... fetch now' -> 'Nothing to fetch right now'). Ueberladen/blockiert bleiben bei der
                // Ausruestung (Drop-Knoepfe). Und ab ReadinessGroupFrom gleichartigen Eintraegen EIN Eintrag fuer alle (siehe oben).
                var fetchNow = new List<Pawn>(); var fetchNoStock = new List<Pawn>(); var gearOnly = new List<Pawn>();
                foreach (Pawn p in otherRemaining)
                {
                    if (notCarriedC.Contains(p) && !overloadedC.Contains(p) && !capacityBlockedC.Contains(p) && !lowAmmoC.Contains(p))
                    { if (FetchGearNow.AnythingFetchable(p)) fetchNow.Add(p); else fetchNoStock.Add(p); }
                    else gearOnly.Add(p);
                }
                string whatNotCarried = "CER_LoadoutUtility_WhatAssignedNotCarried".Translate().ToString();
                if (fetchNow.Count >= ReadinessGroupFrom)
                {
                    List<Pawn> local = fetchNow;
                    acts.Add(("CER_LoadoutUtility_ReadinessFetchAllOption".Translate(local.Count.ToString(), whatNotCarried).ToString(),
                        () => FetchGearNow.TriggerAll(local)));
                }
                else
                    foreach (Pawn p in fetchNow)
                    {
                        Pawn local = p;
                        acts.Add(("CER_LoadoutUtility_ReadinessFetchOption".Translate(local.LabelShortCap, OtherIssueTags(local)).ToString(), () => FetchGearNow.TriggerFor(local)));
                    }
                if (fetchNoStock.Count >= ReadinessGroupFrom)
                {
                    List<Pawn> local = fetchNoStock;
                    acts.Add(("CER_LoadoutUtility_ReadinessFetchNoStockAllOption".Translate(local.Count.ToString(), whatNotCarried).ToString(),
                        () => OpenSupplyForPawns(local)));
                }
                else
                    foreach (Pawn p in fetchNoStock)
                    {
                        Pawn local = p;
                        acts.Add(("CER_LoadoutUtility_ReadinessFetchNoStockOption".Translate(local.LabelShortCap, OtherIssueTags(local)).ToString(),
                            () => OpenSupplyForPawns(new List<Pawn> { local })));
                    }
                foreach (Pawn p in gearOnly)
                {
                    Pawn local = p;
                    acts.Add(("CER_LoadoutUtility_ReadinessGearOption".Translate(local.LabelShortCap, OtherIssueTags(local)).ToString(), () => Find.WindowStack.Add(new Dialog_PawnGear(local))));
                }

                // IMMER ein Menue, auch bei einem einzigen Eintrag (2026-08-30: dort sieht man,
                // was passieren wird). Vorher sprang die Anzeige bei genau einem Problem direkt ins Zielfenster.
                if (acts.Count > 0) Find.WindowStack.Add(new FloatMenu(acts.Select(a => new FloatMenuOption(a.label, a.act)).ToList()));
            }

            // Per-pawn "(N in stock)"/"(in production)"/"(none queued)" suffix for the low-ammo names - same
            // ComputeAmmoAvailability/WeaponHasActiveBill this mod's own ammo cells already use everywhere
            // else, just keyed off the pawn's CURRENTLY LOADED ammo type (CompAmmoUser.CurrentAmmo) instead
            // of a loadout slot. Observed: naming the pawn alone didn't say whether the fix is
            // "wait, it's coming" or "go queue a bill" - the same gap the availability-info audit flagged.
            string LowAmmoSuffix(Pawn p)
            {
                // NUR aus LowAmmoState, kein einziger eigener Bestands- oder Auftragsblick mehr (2026-08-17,
                // lowammo-three-answers). Vorher stand hier die zweite Rechnung: sie sah nur die GELADENE
                // Sorte (CompAmmoUser.CurrentAmmo) statt aller nutzbaren, nahm eine andere Bestandsfunktion
                // und zaehlte gesperrten Bestand mit. Der Widerspruch, woertlich aus dem Test:
                // Tooltip "Ltz (none in stock, not queued)" gegen Menue "Ltz (supplies are on the way)" -
                // dieselbe Anzeige, dieselbe Person, zwei Antworten. Die Einstufung entscheidet jetzt auch
                // den Text, deshalb kann er der Antwort darunter nicht mehr widersprechen.
                // Der Fall "das Loadout selbst fordert zu wenig" bleibt vorn: er ist der einzige, bei dem
                // weder Bestand noch Produktion etwas aendern, und genau deshalb der irrefuehrendste, wenn
                // dort "(200 im Lager)" steht - dann liest es sich, als muesste der Kolonist nur hingehen,
                // und die Warnung bleibt trotzdem.
                if (!CombatReadiness.LowAmmoState(p, out CombatReadiness.LowAmmoInfo info) || !info.IsLow) return "";
                switch (info.cause)
                {
                    case CombatReadiness.LowAmmoCause.PlanTooSmall:
                        return "CER_LoadoutUtility_LowAmmoPlanTooSmall".Translate(info.planned.ToString(), info.threshold.ToString()).ToString();
                    case CombatReadiness.LowAmmoCause.NotProduced:
                        return "CER_LoadoutUtility_LowAmmoNoneQueued".Translate().ToString();
                    default:
                        // Pending heisst: greifbarer Bestand deckt die Luecke ODER ein Auftrag laeuft. In
                        // dieser Reihenfolge, weil "liegt im Lager" die konkretere Auskunft ist.
                        return info.reachable > 0
                            ? "CER_LoadoutUtility_LowAmmoInStock".Translate(info.reachable.ToString()).ToString()
                            : "CER_LoadoutUtility_LowAmmoInProduction".Translate().ToString();
                }
            }
            // Split into precise subsets instead of one blanket "carrying too much OR low on ammo" line for
            // everyone (2026-08-01: that combined phrasing forces the reader to go check WHICH
            // one actually applies - a pawn who is purely low on ammo doesn't carry too much, and vice versa).
            // The click-to-fix FloatMenu already made this same precise distinction per-pawn (ov/low above) -
            // this brings the tooltip header up to the same precision instead of staying vaguer than the menu
            // it opens.
            string NamesFor(List<Pawn> ps) => string.Join(", ", ps.Take(8)
                .Select(p => p.LabelShort + (lowAmmoC.Contains(p) && !overloadedC.Contains(p) && !capacityBlockedC.Contains(p) ? LowAmmoSuffix(p) : "")))
                + (ps.Count > 8 ? "CER_LoadoutUtility_AndNOthers".Translate((ps.Count - 8).ToString()) : "");
            // Grouped by the exact same issue-tag combo the click-to-fix menu already computes (OtherIssueTags)
            // instead of 3 hardcoded overloaded/lowAmmo/both buckets - covers every combination, including
            // CapacityBlocked alone or mixed with the other two, with one shared line format.
            // Die Gruppierung selbst ist ins Lambda GEWANDERT (2026-08-13): sie
            // wird ausschliesslich dort gelesen, lief aber hier draussen - also in JEDEM Bild, obwohl diese
            // Anzeige dauerhaft auf dem Schirm ist. OtherIssueTags baut pro Kolonist eine Liste, vier
            // Uebersetzungen und ein string.Join. Genau die Verschwendung, gegen die das Lambda darunter
            // ueberhaupt eingefuehrt wurde - die Arbeit war nur davor gewandert statt zu verschwinden.

            // Lazy (2026-08-01, this widget is ALWAYS on screen, so an eager string rebuilt every single
            // frame regardless of hover is pure waste - same fix as several other spots earlier today) AND,
            // while at it, the "production gaps" wording is now only used when there's a REAL gap: the
            // click-handler above already distinguishes "genuinely needs producing" from "colony already
            // has it, this pawn just hasn't picked it up" via the same AnythingToSupply check, but the
            // tooltip text never made that same distinction - it said "production gaps" for
            // a pawn (Kachnovo) whose click-through correctly said "supplies in stock, not picked up", and
            // whose own Assign Loadouts card showed fully green (that card checks whether the LOADOUT is
            // satisfiable at all, a different, broader question than "is this pawn physically holding it
            // right now").
            // Nicht, solange das eigene Klick-Menue offen ist (2026-08-14) - siehe TipWanted, wo dieselbe
            // Bedingung fuer alle bewachten Tooltips des Mods steht. DIESER Tooltip laeuft nicht durch
            // TipWanted (er ist als Lambda ohnehin schon faul), also braucht er die Pruefung selbst - und er
            // ist die Stelle, an der es auffiel: das Menue geht unter dem Zeiger auf, der Tooltip
            // blieb darueber stehen.
            if (Find.WindowStack?.WindowOfType<FloatMenu>() != null) return;
            TooltipHandler.TipRegion(r, () =>
            {
                // FORMATIERT (2026-08-30: zu unuebersichtlich - jetzt FETT, Farben je nach Schwere, Kopfzeilen
                // getrennt vom Beschreibungstext). Ohne 13 Sprachen anzufassen: jede Zeile
                // wird mit einem Platzhalter statt der Namen uebersetzt und dort geteilt - Ueberschrift fett und farbig
                // (rot = etwas fehlt wirklich, gelb = liegt da / laeuft / kein Auftrag, grau = Erklaerung), die Namen
                // eingerueckt darunter. Siehe ReadinessStyledLine.
                string tip = total == 0 ? "<b>" + "CER_LoadoutUtility_ReadinessAllGood".Translate().ToString() + "</b>"
                    : ReadinessStyledHeader((total == 1 ? "CER_LoadoutUtility_ReadinessHeaderSingular".Translate(total.ToString())
                        : "CER_LoadoutUtility_ReadinessHeaderPlural".Translate(total.ToString())).ToString());
                if (noLoadoutDistinct.Count > 0)
                    tip += ReadinessStyledLine("CER_LoadoutUtility_ReadinessNoLoadoutLine".Translate(noLoadoutDistinct.Count.ToString(), ReadinessNamesSentinel).ToString(),
                        CombatReadiness.NameList(noLoadoutDistinct), ReadinessRedHex);
                if (prodDistinct.Count > 0)
                {
                    // Dieselbe eine Frage wie im Klick-Menue darueber, und seit 2026-08-17 auch dieselbe
                    // Rechnung: die Ausnahme fuer WeaponUnavailable-Kolonisten ist dort weggefallen, hier
                    // ebenso. Stuende sie nur an einer der beiden Stellen, sagte der Tooltip wieder etwas
                    // anderes als der Klick darunter.
                    // Ueber ReadinessRealGap gecacht, nicht direkt: dieses Lambda wertet Verse in JEDEM Frame
                    // neu aus, solange der Tooltip steht. Der teure Teil (BuildSupplyRequestForPawns plus ein
                    // komplettes Dialog_SupplyLoadouts, dessen Konstruktor pro Waffe ueber alle Kolonisten
                    // laeuft) lief damit pro Bild - ausgerechnet waehrend man in Ruhe lesen will. Der Kommentar
                    // ueber diesem TipRegion sagt, der Tooltip sei genau deswegen lazy gemacht worden; die
                    // teure Arbeit war danach nach innen gewandert (2026-08-09).
                    // Dieselbe Dreiteilung wie im Klick-Menue (2026-08-30) - siehe dort. Tooltip und Menue lesen
                    // dieselbe Je-Kolonist-Frage (ReadinessGapRowsFor), koennen sich also nicht widersprechen.
                    List<Pawn> gapPawns = new List<Pawn>(), noBillPawns = new List<Pawn>(), noAmmoSlotPawns = new List<Pawn>();
                    foreach (Pawn p in prodDistinct)
                    {
                        if (ReadinessGapRowsFor(p) > 0) gapPawns.Add(p);
                        else if (noAmmoSlotC.Contains(p)) noAmmoSlotPawns.Add(p);
                        else noBillPawns.Add(p);
                    }
                    if (gapPawns.Count > 0)
                        tip += ReadinessStyledLine("CER_LoadoutUtility_ReadinessProdGapsLine".Translate(gapPawns.Count.ToString(), ReadinessNamesSentinel, ReadinessGapRows(gapPawns).ToString()).ToString(),
                            CombatReadiness.NameList(gapPawns), ReadinessRedHex);
                    if (noBillPawns.Count > 0)
                        tip += ReadinessStyledLine("CER_LoadoutUtility_ReadinessNoBillLine".Translate(noBillPawns.Count.ToString(), ReadinessNamesSentinel).ToString(),
                            CombatReadiness.NameList(noBillPawns), ReadinessYellowHex);
                    if (noAmmoSlotPawns.Count > 0)
                        tip += ReadinessStyledLine("CER_LoadoutUtility_ReadinessNoAmmoSlotLine".Translate(noAmmoSlotPawns.Count.ToString(), ReadinessNamesSentinel).ToString(),
                            CombatReadiness.NameList(noAmmoSlotPawns), ReadinessRedHex);
                }
                foreach (var g in otherDistinct.GroupBy(OtherIssueTags).OrderByDescending(g2 => g2.Count()))
                {
                    // "(oeffnet ihre Ausruestung)" nur, wo es auch stimmt (2026-08-17 abends). Im Test fielen
                    // nicht nur verschiedene URSACHEN auf, sondern auch verschiedene ZIELE: der Tooltip
                    // versprach fuer Sammy die Ausruestung, das Menue sprang zur Werkbank.
                    // Ueberladen, blockiert und "zugewiesen, traegt nichts" gehen wirklich alle in die
                    // Ausruestung - fuer die bleibt der Satz. Kolonisten, bei denen NUR die Munition knapp
                    // ist, verteilt das Menue dagegen auf drei Wege (Lager holen, zur Bank, Menge im Loadout
                    // erhoehen), und welcher es wird, sagt der Zusatz hinter ihrem Namen ohnehin schon. Diese
                    // eine Gruppe bekommt deshalb die Zeile ohne Aktionsversprechen.
                    List<Pawn> members = g.ToList();
                    bool lowAmmoOnlyGroup = members.All(p => lowAmmoC.Contains(p)
                        && !overloadedC.Contains(p) && !capacityBlockedC.Contains(p));
                    // Ueberladen/blockiert/traegt nichts sind echte Maengel (rot), 'nur Munition knapp' ist gelb.
                    bool severe = members.Any(p => overloadedC.Contains(p) || capacityBlockedC.Contains(p) || notCarriedC.Contains(p));
                    tip += ReadinessStyledLine((lowAmmoOnlyGroup
                        ? "CER_LoadoutUtility_ReadinessOtherIssueLineNoAction"
                        : "CER_LoadoutUtility_ReadinessOtherIssueLine").Translate(members.Count.ToString(), g.Key, ReadinessNamesSentinel).ToString(),
                        NamesFor(members), severe ? ReadinessRedHex : ReadinessYellowHex);
                }
                // Siehe den gleichnamigen Eintrag im Klick-Menue: eine Gelegenheit, kein Mangel - deshalb
                // ganz unten und in ruhigerem Ton als die Problemzeilen darueber.
                List<Loadout> betterAmmoTip = TemplatesWithBetterAmmo();
                if (betterAmmoTip.Count > 0)
                    tip += "<color=" + ReadinessGreyHex + ">" + "CER_LoadoutUtility_ReadinessBetterAmmoLine".Translate(betterAmmoTip.Count.ToString(),
                        string.Join(", ", betterAmmoTip.Take(5).Select(l => CellLabel(l)).ToArray())
                        + (betterAmmoTip.Count > 5 ? "CER_LoadoutUtility_AndNOthers".Translate((betterAmmoTip.Count - 5).ToString()).ToString() : "")).ToString() + "</color>";
                return tip.TrimEnd();
            }, 0x8EA1);
        }

        // "Gibt es fuer diese Pawns wirklich eine Produktionsluecke?" - dieselbe Antwort wie im Klick-Pfad,
        // nur bezahlbar oft. Kurze Lebensdauer statt Dauer-Cache, weil sich die Antwort mit Bestand und
        // Auftraegen aendert; der Schluessel haengt am Pawn-Set, damit ein anderer Satz nicht die Antwort des
        // vorigen erbt. Gleiches Muster wie weaponInvCache/ammoResearchedCache nebenan.
        // Seit 2026-08-30 die ZAHL der Posten statt ja/nein - 0 heisst 'keine echte Luecke', wie vorher false.
        private static readonly Dictionary<int, int> readinessGapCache = new Dictionary<int, int>();
        private static float readinessGapCacheTime = -99f;

        // Formatierung des Bereitschafts-Tooltips (2026-08-30). Die Zeilen-Keys tragen die Namen als
        // Platzhalter am Ende (' - {1}'); uebersetzt wird mit einem Sentinel, danach geteilt: davor die Ueberschrift
        // (ohne den ' - '-Rest), fett und in der Farbe der Schwere, danach die Namen eingerueckt und grau.
        private const string ReadinessNamesSentinel = "\u0001";
        private const string ReadinessRedHex = "#E65959", ReadinessYellowHex = "#E6D94D", ReadinessGreyHex = "#A8A8A8";

        private static string ReadinessStyledLine(string translated, string names, string hex)
        {
            string caption = translated, rest = "";
            int i = translated.IndexOf(ReadinessNamesSentinel, StringComparison.Ordinal);
            if (i >= 0) { caption = translated.Substring(0, i); rest = translated.Substring(i + 1); }
            caption = caption.TrimEnd();
            if (caption.EndsWith("-")) caption = caption.Substring(0, caption.Length - 1).TrimEnd();
            if (caption.EndsWith(":")) caption = caption.Substring(0, caption.Length - 1).TrimEnd();
            string tail = rest.Trim(' ');
            return "\n<color=" + hex + "><b>" + caption + "</b></color>\n    <color=" + ReadinessGreyHex + ">" + names + "</color>" + (tail.EndsWith("\n") ? "\n" : "\n");
        }

        // Kopf: der erste Absatz fett, alles danach (die Erklaerung 'je Kolonist gezaehlt') grau.
        private static string ReadinessStyledHeader(string translated)
        {
            string t = translated.TrimEnd();
            int i = t.IndexOf("\n\n", StringComparison.Ordinal);
            if (i < 0) return "<b>" + t + "</b>\n";
            return "<b>" + t.Substring(0, i) + "</b>\n<color=" + ReadinessGreyHex + ">" + t.Substring(i + 2).Trim() + "</color>\n";
        }

        // Dieselbe Frage fuer EINEN Kolonisten: haette das Nachschub-Fenster fuer ihn allein eine Zeile? Gecacht ueber
        // den Pawn-Set-Schluessel von ReadinessGapRows (1 s), eine frische Liste je Aufruf, weil der Request sie
        // moeglicherweise haelt.
        private static int ReadinessGapRowsFor(Pawn p) => p == null ? 0 : ReadinessGapRows(new List<Pawn> { p });

        private static int ReadinessGapRows(List<Pawn> prodDistinct)
        {
            if (prodDistinct == null || prodDistinct.Count == 0) return 0;
            if (Time.realtimeSinceStartup - readinessGapCacheTime > 1f)
            {
                readinessGapCache.Clear();
                readinessGapCacheTime = Time.realtimeSinceStartup;
            }
            int key = 17;
            foreach (Pawn p in prodDistinct) key = key * 31 + (p?.thingIDNumber ?? 0);
            if (readinessGapCache.TryGetValue(key, out int cached)) return cached;

            Dialog_SupplyLoadouts.Request prodReq = Dialog_AssignLoadouts.BuildSupplyRequestForPawns(prodDistinct);
            // AnythingToProduce, wie der Klick-Weg daneben (2026-08-13): der Tooltip sagt "Produktionsluecken",
            // und dieselbe Zeile ist der Knopf, der das Nachschub-Fenster oeffnet. Wuerde hier weiter
            // AnythingToSupply stehen, verspraeche der Text eine Luecke, wo der Klick nur noch eine
            // Aufraeumliste zu bieten haette - genau die Sorte Widerspruch, aus der dieser Punkt entstanden ist.
            int rows = prodReq != null ? new Dialog_SupplyLoadouts(prodReq).ProduceRowCount : 0;
            readinessGapCache[key] = rows;
            return rows;
        }

        public static bool BottomActionButton(Rect inRect, ref float xRight, float width, string label, bool enabled = true, string tip = null, Texture2D icon = null)
        {
            Rect r = new Rect(xRight - width, inRect.height - BottomBarH, width, BottomBtnH);
            xRight -= width + 10f;
            bool clicked;
            if (enabled)
            {
                clicked = ButtonTextFit(r, icon == null ? label : "");
                if (icon != null) ButtonIconLabel(r, icon, label);
            }
            else
            {
                // Dimmed, not "active: false" - the inactive button style strikes the label through.
                Color old = GUI.color;
                GUI.color = new Color(1f, 1f, 1f, 0.4f);
                ButtonTextFit(r, icon == null ? label : "");
                if (icon != null) ButtonIconLabel(r, icon, label);
                GUI.color = old;
                clicked = false;
            }
            if (!string.IsNullOrEmpty(tip)) TooltipHandler.TipRegion(r, tip);
            return clicked;
        }

        // ---- Anti-flesh / anti-mech affinity (detection-based; no hardcoded defNames, works across modlists) --------

        // Where an item sits on the anti-flesh <-> anti-mech spectrum, from its detected abilities. -1 = pure
        // anti-flesh (toxic gas, fire - mechanoids are immune to both; frag/HE shine against soft targets),
        // +1 = pure anti-mech (EMP stuns mechs; AP/Sabot penetrates their high sharp armor). 0 = neutral (FMJ etc.).
        public static float ItemMechAffinity(ThingDef d)
        {
            if (d == null) return 0f;

            // Weapons with an intrinsic effect (grenades, flamethrowers, self-ammo launchers) have no separate ammo
            // projectile to measure, so they stay on the effect-based lean.
            if (d.IsWeapon)
            {
                SpecialAbility wfx = DetectSpecialAbilities(d) | DetectAmmoSpecialAbilities(d);
                DetectArmorPiercing(d, out bool intrinsic, out bool _);
                if (intrinsic) wfx |= SpecialAbility.ArmorPiercing;
                return AffinityFromEffects(wfx);
            }

            // Ammo: measure the round itself, like the weapon damage table does. Chemical / electrical effects the
            // ballistic model can't see override it (mechs are immune to toxin, and EMP is their hard counter);
            // everything else is driven by how well the round's real sharp AP defeats mech-grade armor, so hollow-point
            // and buckshot come out anti-flesh from their low penetration with no label matching, AP/Sabot anti-mech.
            SpecialAbility fx = GetAmmoEffects(d);
            if ((fx & SpecialAbility.EMP) != 0) return 1f;
            if ((fx & SpecialAbility.Tox) != 0) return -1f;

            float ballistic = 0f;
            bool hasKinetic = false;
            ThingDef proj = GetAmmoProjectile(d);
            if (proj != null)
            {
                ExtractDamageAndAP(proj, out float dmg, out float ap, out float apB);
                if (dmg > 0f && (ap > 0f || apB > 0f))
                {
                    // Retention against mech-grade armor (same curve and same two axes as the weapon table):
                    // ~1 = punches through (anti-mech), 0 = stopped (anti-flesh). Map [0..1] onto [-1..+1].
                    // Blunt included 2026-08-02 for the same reason as everywhere else - a 12ga slug is stopped
                    // on the sharp axis and goes through on the blunt one, and reading sharp alone put it on the
                    // anti-flesh side while buckshot, which really does bounce, sat next to it.
                    // AmmoMechRetention, nicht MechRetention (2026-08-19) - siehe dort: fuer eine
                    // MUNITIONSSORTE ist die stumpfe Achse innerhalb desselben Kalibers konstant und
                    // ueberdeckt mit Mathf.Max genau den Unterschied, um den es hier geht.
                    float retention = AmmoMechRetention(ap, apB);
                    ballistic = Mathf.Clamp(2f * retention - 1f, -1f, 1f);
                    hasKinetic = true;
                }
            }

            // Nudges for effects the pure AP model can't see.
            float extra = 0f;
            // ArmorPiercing was detected here and then never used in this branch (only in the no-projectile
            // fallback below) - so a round CE itself labels AP got no anti-mech credit at all beyond its raw
            // penetration. With CE's real armor curve, a 14-AP round against sharp 20 retains exactly nothing,
            // which is correct for a single hit but understates why players do reach for AP: it is the round
            // that actually threatens the armored targets in the first place. Small, explicit nudge.
            if ((fx & SpecialAbility.ArmorPiercing) != 0) extra += 0.3f;
            // Feuer zieht nach ANTI-FLEISCH, nicht nach anti-mech (Vorzeichen korrigiert 2026-08-19).
            // Hier stand +0.25 mit der Begruendung "fire also hurts mechs" - das ist im Spiel nachweislich
            // falsch: RimWorlds Mechanoiden-Basis traegt <Flammability>0</Flammability> (Core/Defs/
            // ThingDefs_Races/Races_Mechanoid.xml), sie brennen ueberhaupt nicht.
            // Es war ausserdem ein Widerspruch INNERHALB dieser Datei: AffinityFromEffects (der Zweig fuer
            // Munition ohne messbares Geschoss, zwanzig Zeilen weiter unten) fuehrt Flame mit -0.25, und
            // EffectMultMech rechnet mit Faktor 0.1 ("mechs are immune to fire"). Dieselbe Frage, drei
            // Stellen, zwei Vorzeichen - je nachdem, ob die Sorte ein auswertbares Geschoss hat.
            // Sichtbare Folge: am rechten Reglerende gewann AP-I (+0.50) vor Sabot (+0.27) und AP (+0.25),
            // obwohl AP-I dieselbe scharfe Durchschlagskraft hat wie AP (11) und Sabot mit 19.25 deutlich
            // darueber liegt - der Brandanteil, der gegen Mechs gar nichts tut, hat den Ausschlag gegeben.
            // Nach der Korrektur gewinnt dort Sabot, dann AP, und AP-I liegt neutral. Das linke Reglerende
            // ist davon unberuehrt (HP gewinnt vorher wie nachher).
            if ((fx & SpecialAbility.Flame) != 0) extra -= 0.25f;
            if ((fx & SpecialAbility.Fragment) != 0) extra -= 0.2f;   // shrapnel pings off armor -> a bit more anti-flesh
            if ((fx & SpecialAbility.Stun) != 0) extra -= 0.2f;       // less-lethal -> anti-flesh
            if ((fx & SpecialAbility.Concussive) != 0) extra -= 0.1f;
            if ((fx & SpecialAbility.Explosive) != 0) extra -= 0.1f;
            if ((fx & SpecialAbility.ExplosiveSecondary) != 0) extra -= 0.05f;

            if (!hasKinetic) return AffinityFromEffects(fx);          // no projectile data (self-ammo etc.): fall back
            return Mathf.Clamp(ballistic + extra, -1f, 1f);
        }

        // ====== Munitions-Vergleich: drei Balken je Sorte, normiert auf das Kaliber (2026-09-06) ======
        //
        // Ersetzt den einen "wogegen taugt das"-Balken vom 04.09. Ziel: die Werte (Schaden usw.) in
        // Balken nebeneinander, damit man sie gut vergleichen kann. Also keine Deutung mehr ("Mostly armour"), sondern die drei Zahlen, aus denen
        // eine Deutung erst entstuende: Schaden je Schuss, scharfer und stumpfer Durchschlag.
        //
        // Bezug ist das KALIBER: jeder Balken ist voll bei der hoechsten Sorte desselben AmmoSets. Genau das
        // war der Fehler der alten Anzeige - der stumpfe Durchschlag ist innerhalb eines Kalibers konstant
        // und hob absolut gerechnet jede Gewehrpatrone auf die Panzerungsseite, HP eingeschlossen (Befund
        // im Test vom 04.09., nachgerechnet). Relativ zum Kaliber sagt der Balken wieder, worum es
        // beim Waehlen geht: HP hat den laengsten Schadensbalken und den kuerzesten scharfen, Sabot
        // umgekehrt, FMJ liegt dazwischen. Ein Balken, der bei allen Sorten gleich lang ist (stumpf beim
        // Gewehr), sagt damit ehrlich "hier gibt es nichts zu waehlen".
        //
        // ItemMechAffinity (der Anti-Fleisch/Anti-Mech-Regler) bleibt unangetastet - das ist eine
        // Entscheidung, das hier eine Anzeige.
        private static readonly Texture2D ammoDmgBarTex = SolidColorMaterials.NewSolidColorTexture(new Color(0.86f, 0.55f, 0.22f));
        private static readonly Texture2D ammoSharpBarTex = SolidColorMaterials.NewSolidColorTexture(new Color(0.33f, 0.60f, 0.90f));
        private static readonly Texture2D ammoBluntBarTex = SolidColorMaterials.NewSolidColorTexture(new Color(0.45f, 0.72f, 0.62f));

        // Breite, mit der die Aufrufer rechnen duerfen - eine Zahl, zwei Zeilenzeichner (die beiden
        // Munitionszeilen in Dialog_ChangeAmmo sind Zwillinge und driften sonst auseinander).
        public const float AmmoStatBarsW = 240f;
        private const float AmmoStatBarGap = 6f;
        public static float AmmoStatBarW => (AmmoStatBarsW - 2f * AmmoStatBarGap) / 3f;

        private struct AmmoFamilyMax { public float dmg, sharp, blunt; }
        private static Dictionary<ThingDef, AmmoFamilyMax> ammoFamilyMaxCache;

        // Das Maximum je Wert ueber alle Sorten desselben AmmoSets. Einmal je Prozess ueber alle Sets
        // gebaut; eine Sorte, die in mehreren Sets steht, bekommt das groessere Maximum - so ist ihr Balken
        // nie laenger als voll.
        private static AmmoFamilyMax FamilyMaxFor(ThingDef ammo)
        {
            if (ammoFamilyMaxCache == null)
            {
                ammoFamilyMaxCache = new Dictionary<ThingDef, AmmoFamilyMax>();
                foreach (AmmoSetDef set in DefDatabase<AmmoSetDef>.AllDefs)
                {
                    if (set.ammoTypes == null) continue;
                    AmmoFamilyMax m = new AmmoFamilyMax();
                    foreach (AmmoLink link in set.ammoTypes)
                    {
                        if (link.projectile == null) continue;
                        ExtractDamageAndAP(link.projectile, out float d, out float a, out float b);
                        m.dmg = Mathf.Max(m.dmg, d); m.sharp = Mathf.Max(m.sharp, a); m.blunt = Mathf.Max(m.blunt, b);
                    }
                    foreach (AmmoLink link in set.ammoTypes)
                    {
                        if (link.ammo == null) continue;
                        if (ammoFamilyMaxCache.TryGetValue(link.ammo, out AmmoFamilyMax had))
                            m = new AmmoFamilyMax { dmg = Mathf.Max(m.dmg, had.dmg), sharp = Mathf.Max(m.sharp, had.sharp), blunt = Mathf.Max(m.blunt, had.blunt) };
                        ammoFamilyMaxCache[link.ammo] = m;
                    }
                }
            }
            ammoFamilyMaxCache.TryGetValue(ammo, out AmmoFamilyMax r);
            return r;
        }

        public static void DrawAmmoStatBars(Rect r, ThingDef ammoDef)
        {
            if (ammoDef == null || r.width < 60f) return;
            ThingDef proj = GetAmmoProjectile(ammoDef);
            if (proj == null) return;   // Selbstmunition (Granaten) hat kein Geschoss - dort steht nichts
            ExtractDamageAndAP(proj, out float dmg, out float ap, out float apB);
            AmmoFamilyMax fam = FamilyMaxFor(ammoDef);

            float w = AmmoStatBarW;
            void Bar(float x, float value, float max, string tagKey, Texture2D tex)
            {
                Rect b = new Rect(x, r.y, w, r.height);
                GUI.color = new Color(0.16f, 0.16f, 0.18f);
                GUI.DrawTexture(b, BaseContent.WhiteTex);
                GUI.color = Color.white;
                float frac = max > 0f ? Mathf.Clamp01(value / max) : 0f;
                if (frac > 0f) GUI.DrawTexture(new Rect(b.x + 1f, b.y + 1f, (b.width - 2f) * frac, b.height - 2f), tex);
                GUI.color = Color.black;
                Widgets.DrawBox(b, 1);
                GUI.color = Color.white;
                BarLabel(b, tagKey.Translate().ToString() + " " + value.ToString("0.#"));
            }
            Bar(r.x, dmg, fam.dmg, "CER_LoadoutUtility_AmmoStatDmg", ammoDmgBarTex);
            Bar(r.x + w + AmmoStatBarGap, ap, fam.sharp, "CER_LoadoutUtility_AmmoStatSharp", ammoSharpBarTex);
            Bar(r.x + 2f * (w + AmmoStatBarGap), apB, fam.blunt, "CER_LoadoutUtility_AmmoStatBlunt", ammoBluntBarTex);

            // TipWanted davor - der Text wuerde sonst je Zeile und Bild gebaut (bis zu einem Dutzend Sorten je Kolonist).
            if (TipWanted(r))
                TooltipHandler.TipRegion(r, "CER_LoadoutUtility_AmmoStatTip".Translate(
                    dmg.ToString("0.#"), fam.dmg.ToString("0.#"), ap.ToString("0.#"), fam.sharp.ToString("0.#"),
                    apB.ToString("0.#"), fam.blunt.ToString("0.#")));
        }

        // Die drei Spaltenkoepfe ueber den Balken - dieselbe Aufteilung wie die Balken selbst.
        public static void DrawAmmoStatHeaders(float x, float y)
        {
            float w = AmmoStatBarW;
            GameFont pf = Text.Font; Text.Font = GameFont.Tiny;
            GUI.color = new Color(1f, 1f, 1f, 0.55f);
            Widgets.Label(new Rect(x, y, w, 22f), "CER_LoadoutUtility_AmmoStatDmg".Translate());
            Widgets.Label(new Rect(x + w + AmmoStatBarGap, y, w, 22f), "CER_LoadoutUtility_AmmoStatSharp".Translate());
            Widgets.Label(new Rect(x + 2f * (w + AmmoStatBarGap), y, w, 22f), "CER_LoadoutUtility_AmmoStatBlunt".Translate());
            GUI.color = Color.white; Text.Font = pf;
        }

        // Effect-only anti-flesh<->anti-mech lean, used for weapons and for ammo with no measurable projectile.
        private static float AffinityFromEffects(SpecialAbility fx)
        {
            float a = 0f;
            bool any = false;
            void Add(SpecialAbility f, float v) { if ((fx & f) != 0) { a += v; any = true; } }
            Add(SpecialAbility.EMP, 1f);
            Add(SpecialAbility.ArmorPiercing, 0.6f);
            Add(SpecialAbility.Flame, -0.25f);     // generalist: potent vs centipedes too, only a mild anti-flesh lean
            Add(SpecialAbility.Tox, -1f);          // toxin does nothing to mechs -> the purest anti-flesh
            Add(SpecialAbility.Fragment, -0.5f);   // shrapnel is low-penetration, stopped by armor -> anti-flesh
            Add(SpecialAbility.Concussive, -0.3f);
            Add(SpecialAbility.Stun, -0.4f);
            // Explosive (real Bomb/BombSuper AoE, e.g. HE shells/grenades) leans anti-flesh like Concussive -
            // blast/shrapnel is far more lethal to soft targets than to armored mechs, but less purely so than
            // Fragment's dedicated shrapnel comp. ExplosiveSecondary (the small Bomb_Secondary "HE kicker" an
            // AP-HE round adds to its own kinetic hit) only matters once the primary AP has already gotten
            // through, so it's a milder nudge on top of whatever the round's own AP already earned.
            Add(SpecialAbility.Explosive, -0.3f);
            Add(SpecialAbility.ExplosiveSecondary, -0.15f);
            return any ? Mathf.Clamp(a, -1f, 1f) : 0f;
        }

        // A whole loadout's spot on the same spectrum, mapped to 0..1 (0 = anti-flesh, 0.5 = neutral, 1 = anti-mech):
        // the average affinity of its special-ability items; loadouts with only plain gear sit at 0.5.
        public static float LoadoutMechAffinity01(Loadout lo)
        {
            if (lo == null || lo.Slots == null) return 0.5f;
            float sum = 0f;
            int n = 0;
            foreach (LoadoutSlot s in lo.Slots)
            {
                float a = ItemMechAffinity(s.thingDef);
                if (a != 0f) { sum += a; n++; }
            }
            if (n == 0) return 0.5f;
            return 0.5f + 0.5f * Mathf.Clamp(sum / n, -1f, 1f);
        }

        // One tooltip that lists what a loadout carries: "<b>name</b>" then one "Nx item" line per slot.
        // Non-breaking spaces keep each entry on its own line without mid-name wraps.
        // slotsSource (2026-08-01): optional override for WHICH object's .Slots to read, while
        // still labeling with `lo`'s own name - lets a caller show a specific PAWN's actual resolved/
        // personalized composition (e.g. a Role-assigned pawn who has a personal ammo override via Dialog_
        // ChangeAmmo, which has no Role gate) under the Role's own name, instead of the Role's raw baked
        // Slots which would silently hide that personalization. Defaults to `lo` itself - every existing
        // caller (Dialog_ArmColony, Dialog_GenerateAmmoBills, Dialog_SquadSetup, Dialog_AssignLoadouts) is
        // unaffected.
        public static string LoadoutContentsTooltip(Loadout lo, Loadout slotsSource = null)
        {
            if (lo == null) return "";
            var sb = new System.Text.StringBuilder();
            // Der ANGEZEIGTE Name, nicht das gespeicherte Etikett (2026-08-17, im Test: die Vorsilbe
            // "W|" lebte im Tooltip weiter). Diese Ueberschrift steht ueber jeder Inhaltsliste,
            // die der Mod irgendwo zeigt - sie war die letzte grosse Stelle mit dem rohen Etikett.
            sb.Append("<b>").Append(LoadoutDisplayName(lo)).Append("</b>");
            List<LoadoutSlot> slots = (slotsSource ?? lo).Slots;
            if (slots == null || slots.Count == 0)
            {
                sb.Append("CER_LoadoutUtility_LoadoutEmpty".Translate());
                return sb.ToString();
            }
            foreach (LoadoutSlot s in slots)
            {
                string label = s.thingDef != null ? s.thingDef.label : (s.genericDef != null ? s.genericDef.label : "?");
                sb.Append("\n").Append(s.count).Append("x ").Append(label.Replace(" ", " "));
            }
            return sb.ToString();
        }

        // ---- Ingredient helpers (single source of truth for the whole UI) --------------------------------------------

        // Is this resource currently sitting on any colony map (in storage / on the ground)?
        public static bool IngredientInStorage(ThingDef d)
        {
            return d != null && PlayerMaps().Any(map => map.listerThings.ThingsOfDef(d).Any(t => !IsHiddenInFog(t)));
        }

        // Reference-identity equality/hashing for Def-keyed dictionaries (2026-08-01,
        // confirmed via live diagnostic logging: "Any Perishable Meal"/"Any Medicine"/"Any Drugs" all showed
        // IDENTICAL availability numbers). Root cause: Verse.Def overrides Equals/GetHashCode itself, based
        // on `defName.GetHashCode()` (a plain string hash) plus runtime Type - and it turns out, on this
        // game's actual Mono/.NET runtime, the specific strings "GenericMeal"/"GenericMedicine"/"GenericDrugs"
        // hash-collide with each other. A Dictionary<LoadoutGenericDef,...> keyed the normal way then silently
        // treats all three as the SAME key: whichever one gets inserted last during the one-time
        // EnsureGenericSupplyDefs population loop wins, and the other two permanently read back that one's
        // family/category/icon instead of their own from then on - confirmed live (Log.Message diagnostic
        // showed "Any Medicine" being served from a cache entry that only ever held "Any Meal"'s value, with
        // cacheSize=1 proving no other entry could possibly have matched it). Every real Def in the
        // DefDatabase is already a singleton per defName, so reference equality loses nothing legitimate -
        // it's strictly more correct for "did I already see this EXACT object" than defName-hash equality,
        // and immune to whatever collision Def's own override has for any particular set of strings.
        // BERICHTIGUNG 2026-08-25 (per Dekompilat von Verse.Def): es war nie eine Kollision dreier Strings.
        // Def.GetHashCode() liefert das Feld `defNameHash`, gesetzt nur durch Def.ResolveDefNameHash() - das
        // RimWorlds Lader fuer XML-Defs aufruft, CE fuer seine im Code erzeugten LoadoutGenericDefs aber nie.
        // ALLE CE-Generics tragen Hash 0 und Equals(Def) vergleicht genau dieses Feld: fuer jedes
        // Standard-Woerterbuch sind "Any Meal", "Any Medicine" und jedes "Any ammo for X" EIN Schluessel.
        // Die Wurzel ist seither in CEReloadedMain behoben (ResolveDefNameHash beim Start); diese beiden
        // Vergleicher bleiben als zweiter Boden fuer die Stellen, die sie schon benutzen. `internal`, damit
        // die Reihum-Rechnung in Dialog_AssignLoadouts denselben Vergleicher nimmt statt einen dritten.
        internal sealed class DefRefComparer : IEqualityComparer<Def>
        {
            public static readonly DefRefComparer Instance = new DefRefComparer();
            public bool Equals(Def a, Def b) => ReferenceEquals(a, b);
            public int GetHashCode(Def d) => System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(d);
        }
        private sealed class GenericDefRefComparer : IEqualityComparer<LoadoutGenericDef>
        {
            public static readonly GenericDefRefComparer Instance = new GenericDefRefComparer();
            public bool Equals(LoadoutGenericDef a, LoadoutGenericDef b) => ReferenceEquals(a, b);
            public int GetHashCode(LoadoutGenericDef d) => System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(d);
        }

        // How much of this resource the colony has in stock - the same number shown in the left resource readout
        // (counts stockpiled/shelved resources via the map resource counter).
        // ---- Loadout supply options (food / medicine), shared by the Generate tab and the mod settings ------------
        // Attribute-driven, no def-name sniffing: generics are classified by what their OWN CE matcher (lambda)
        // accepts, and "travel food" means shelf-stable (rots slowly or never), which is what made Pemmican special
        // in the first place - modded hardtack-style foods qualify the same way.
        private static List<LoadoutGenericDef> foodGenerics, medicineGenerics, drugGenerics;
        private static Dictionary<LoadoutGenericDef, ThingDef> genericIconDefs;
        // Every concrete ThingDef this generic's own matcher accepts - NOT just the single "representative"
        // above (that one's for a display icon only). A stock/feasibility check has to sum across the whole
        // family: checking just the representative's stock reports "missing" even when the colony is stacked
        // on some OTHER concrete match (e.g. plain "Medicine" in storage while the representative happened
        // to resolve to "MedicineHerbal") - this is what GenericStockCount below is for.
        private static Dictionary<LoadoutGenericDef, List<ThingDef>> genericFamily;
        // Single source of truth for "which Equipment Packs section does this generic belong to" - populated once,
        // alongside foodGenerics/medicineGenerics, so a generic can never be offered/shown as a member of two
        // sections at once (EquipmentPackUtility.CategoryOf and the Ingredients tab both read this, never their
        // own independent Contains-checks against the two lists above).
        private static Dictionary<LoadoutGenericDef, PackCategory> genericCategories;

        public static PackCategory? GenericCategoryOf(LoadoutGenericDef gd)
        {
            EnsureGenericSupplyDefs();
            return gd != null && genericCategories.TryGetValue(gd, out PackCategory c) ? c : (PackCategory?)null;
        }

        private static void EnsureGenericSupplyDefs()
        {
            if (foodGenerics != null) return;
            foodGenerics = new List<LoadoutGenericDef>();
            medicineGenerics = new List<LoadoutGenericDef>();
            drugGenerics = new List<LoadoutGenericDef>();
            // Reference-identity keyed (2026-08-01) - see DefRefComparer/GenericDefRefComparer's own comment
            // for why Def's own Equals/GetHashCode can't be trusted here.
            genericIconDefs = new Dictionary<LoadoutGenericDef, ThingDef>(GenericDefRefComparer.Instance);
            genericCategories = new Dictionary<LoadoutGenericDef, PackCategory>(GenericDefRefComparer.Instance);
            genericFamily = new Dictionary<LoadoutGenericDef, List<ThingDef>>(GenericDefRefComparer.Instance);
            foreach (LoadoutGenericDef gd in DefDatabase<LoadoutGenericDef>.AllDefs)
            {
                // Per-weapon ammo placeholders follow CE's documented naming contract - they are neither.
                if (gd.defName != null && gd.defName.StartsWith(GenericAmmoPrefix)) continue;
                if (gd.lambda == null) continue;
                bool med = false, drug = false, food = false;
                ThingDef representative = null;
                List<ThingDef> family = new List<ThingDef>();
                foreach (ThingDef td in DefDatabase<ThingDef>.AllDefsListForReading)
                {
                    bool matches;
                    try { matches = gd.lambda(td); }
                    catch { continue; }   // a modded matcher assuming properties this def lacks must not kill the scan
                    if (!matches) continue;
                    family.Add(td);
                    if (representative == null) representative = td;   // first real thing this generic accepts - just for a display icon
                    if (td.IsMedicine) med = true;
                    else if (td.IsDrug) drug = true;
                    else if (td.IsIngestible) food = true;
                }
                genericFamily[gd] = family;
                if (representative != null) genericIconDefs[gd] = representative;
                // Medicine wins outright. A FOOD generic whose matcher happens to accept beer (a drug AND
                // nutrition) stays food - a single drug match is not enough to pull it into Drugs. Only a
                // generic that NEVER matched a food item, purely drugs, goes to the dedicated Drugs section
                // (which didn't exist yet when "drugs-only -> medicine" was first written).
                if (med) { medicineGenerics.Add(gd); genericCategories[gd] = PackCategory.Medical; }
                else if (food) { foodGenerics.Add(gd); genericCategories[gd] = PackCategory.Food; }
                else if (drug) { drugGenerics.Add(gd); genericCategories[gd] = PackCategory.Drugs; }
            }
        }

        // A real ThingDef that matches this generic's own CE matcher, borrowed purely for a display icon - the
        // generic itself (e.g. "Any Meal") has no ThingDef/graphic of its own.
        public static ThingDef RepresentativeIconDef(LoadoutGenericDef gd)
        {
            EnsureGenericSupplyDefs();
            return gd != null && genericIconDefs.TryGetValue(gd, out ThingDef d) ? d : null;
        }

        /// <summary>Colony stock for a generic ("Any Medicine", "Any Perishable Meal", ...), summed over EVERY
        /// concrete def its own matcher accepts - not just the single representative. A Ready/Missing verdict
        /// checking only the representative would misreport "missing" whenever the colony's stock happens to
        /// be a different qualifying item than the one arbitrarily picked for display.</summary>
        public static int GenericStockCount(LoadoutGenericDef gd)
        {
            EnsureGenericSupplyDefs();
            if (gd == null || !genericFamily.TryGetValue(gd, out List<ThingDef> family)) return 0;
            // Bodenbestand zaehlt mit (2026-08-13: einheitlich wie bei Munition und Waffen).
            // Fuer Munition zaehlen Lager, Boden und Getragenes seit dem 13.08. gemeinsam (AmmoOnHand),
            // fuer Waffen zaehlt der Boden ohnehin - nur Pack-Inhalte hatten den Topf nicht. Fehlszenario:
            // nach einem Ueberfall liegen 30 Medizin lose auf der Karte, und der Mod schlug vor, welche
            // herzustellen, waehrend sie danebenlagen.
            int total = 0;
            foreach (ThingDef td in family) total += StoredStockCount(td) + GroundStockCount(td);
            return total;
        }

        // Every concrete ThingDef a generic's own matcher accepts - the same family GenericStockCount sums
        // over. Exposed so callers that need per-def operations across the whole family (e.g. summing queued
        // production) don't have to re-scan the ThingDef database themselves.
        public static List<ThingDef> GenericFamily(LoadoutGenericDef gd)
        {
            EnsureGenericSupplyDefs();
            return gd != null && genericFamily.TryGetValue(gd, out List<ThingDef> family) ? family : new List<ThingDef>();
        }

        // "Owned" breakdown for a pack item (medicine/food/drug/utility, concrete or generic) - feeds the
        // same DrawAvailabilityBar the Weapon Loadouts tab already draws per weapon, adapted to what a PACK
        // item's "assigned" means: not a loadout slot demanding a specific weapon, but a colonist's attached
        // Equipment Pack(s) demanding this item (AmmoAssignmentUtility.GetPawnPacks). For a generic ("Any
        // Medicine", ...) stock/production are summed across its WHOLE family (the same family
        // GenericStockCount already sums over), but "carried"/"assigned" match the generic itself directly -
        // a pack authored with "Any Medicine" stores the generic in its own slot, and a carried item matches
        // it via the generic's own lambda - so neither needs resolving down to one arbitrary representative
        // concrete def. Shared by Dialog_Ingredients.cs (the pack editor) and Dialog_GenerateEquipmentPack.cs
        // (the auto-generate wizard) - one implementation, not two independently-tuned copies.
        // Cached per Def for a short TTL (2026-08-01) - originally fine as a bare per-call scan when only
        // called from a handful of rows in the Ingredients/Auto-generate-Pack editors, but Dialog_AssignLoadouts'
        // own rich Pack-cell tooltip now calls this UNCONDITIONALLY every frame per pack item per visible pawn
        // row (not just on hover) - uncached, that's the exact "very laggy" empire-rescan-per-row bug class
        // Patch_LoadoutTooltip.ComputeAvailability's own cache was added to fix, just reintroduced via this
        // sibling function. Same 1s real-time TTL convention as GetWeaponInventory/GetAmmoOwnership.
        private struct ItemOwn { public int carriedOnly, carriedAndAssigned, assignedInStore, freeStored, inProduction; }
        // Reference-identity keyed (2026-08-01) - see DefRefComparer's own comment above for why.
        private static readonly Dictionary<Def, ItemOwn> itemOwnCache = new Dictionary<Def, ItemOwn>(DefRefComparer.Instance);
        // Time.frameCount, not a real-time TTL (2026-08-01: Availability bars on the Auto-
        // Generate Equipment Packs screen went briefly BLANK - all-zero - when navigating Weiter then Zurueck
        // within Arm my Colony, then self-corrected about a second later). A 1-real-second window can span
        // several logical frames AND a dialog reconstruction (Back rebuilds several of these dialogs fresh),
        // so a value cached from BEFORE the transition could still be "fresh" by the wall-clock the instant
        // the new dialog's first frame reads it, even though the underlying colony state (or which Defs are
        // even being asked about) has moved on - a real cross-transition staleness window a per-second TTL
        // can't rule out. Keying the clear on Unity's own per-rendered-frame counter instead bounds every
        // cached value to the SINGLE frame it was computed in, which still dedupes the repeated Layout+
        // Repaint-pass calls a single frame makes for the same Def (the actual redundant-work case this
        // caching exists for) without ever risking a value surviving into a frame where anything changed.
        // 0,3 s statt je Bild (2026-08-29, perf-packwizard-draw-cost): ComputeItemOwnershipUncached laeuft pro Def
        // ueber alle Kolonisten, und der Pack-Assistent fragt in jedem Durchgang jede seiner Zeilen - dieselbe
        // Stufung wie die Zellen-Caches des Assign-Reiters; die Fenster pausieren das Spiel, der Bestand steht.
        private static float itemOwnCacheTime = -99f;

        // Koloniweiter Bedarf fuer diesen Gegenstand: was ALLE Kolonisten zusammen ueber ihre Packs und
        // ihren persoenlichen Anker anfordern. Gegenstueck zu ComputeItemOwnership, das den Bestand
        // ebenfalls koloniweit liefert.
        // WARUM (2026-08-12): der Verfuegbarkeits-Tooltip mischte die Bezugsgroessen - Bestand
        // koloniweit, Bedarf aber nur der des einen Kolonisten, in dessen Zeile man gerade steht. Bei zwei
        // Kolonisten mit je 5 Medizin und 3 im Lager stand deshalb bei BEIDEN "Assigned, in storage: 3", und
        // eine Fehlmenge tauchte gar nicht oder zu klein auf. Richtig waere:
        // "3 assigned in storage, 7 missing". Genau das ergibt sich, wenn beide Seiten dieselbe Menge meinen.
        // Die pawn-eigene Aussage geht dabei nicht verloren - das gruene "traegt er selbst"-Segment carvt
        // BuildAvailabilitySegments weiterhin aus dem Bestand heraus.
        // Laeuft nur beim Ueberfahren mit der Maus (die Tooltip-Fabrik ist lazy), nicht pro Frame.
        // Ein Frame, eine Antwort (2026-08-13: der Assign-Loadouts-Reiter lief traege).
        // Diese Funktion ist ein KOLONIEWEITER Scan, und sie wurde aus der Zeichenschleife heraus pro
        // Gegenstand, pro Zelle, pro Kolonistenkarte und pro Frame gerufen: bei 12 Kolonisten mit je vier
        // Zellen und mehreren Inhalten sind das schnell einige hundert vollstaendige Durchlaeufe ueber alle
        // Pawns - pro Bild. Schlimmer noch, der Rumpf allokiert dabei staendig (GetPawnPacks baut jedes Mal
        // eine neue Liste, dazu die beiden `?? new List<...>()`), was den Speicherbereiniger mitlaufen laesst.
        // Das Ergebnis kann sich innerhalb eines Frames nicht aendern - es haengt nur an zugewiesenen
        // Loadouts, und die aendert kein Zeichenaufruf. Deshalb derselbe Frame-Cache wie bei
        // ComputeItemOwnership zwoelf Zeilen weiter oben, inklusive desselben Referenzvergleichers.
        // Bewusst frameCount statt einer Zeitspanne: eine TTL wuerde nach einer Zuweisung fuer bis zu einer
        // Sekunde die alte Zahl zeigen, und genau diese Zelle soll sofort stimmen.
        private static readonly Dictionary<Def, int> assignedNeedCache = new Dictionary<Def, int>(DefRefComparer.Instance);
        private static int assignedNeedCacheFrame = -1;

        public static int ColonyWideAssignedNeed(Def d)
        {
            if (d == null) return 0;
            if (Time.frameCount != assignedNeedCacheFrame)
            {
                assignedNeedCache.Clear();
                assignedNeedCacheFrame = Time.frameCount;
            }
            if (assignedNeedCache.TryGetValue(d, out int hit)) return hit;
            int n = 0;
            foreach (Pawn p in AllColonyPawns())
            {
                // CarriedPackSlots statt GetPawnPacks (2026-08-13): sieht auch das, was ueber eine ROLLE
                // getragen wird - eine Rolle haengt ihre Packs nicht an, sie backt sie ein. Vorher fehlte
                // der Rollen-Traeger in dieser Summe UND in der "Assigned to"-Aufzaehlung daneben.
                foreach (LoadoutSlot s in AmmoAssignmentUtility.CarriedPackSlots(p))
                    if ((Def)s.thingDef == d || (Def)s.genericDef == d) n += Mathf.Max(0, s.count);
                Loadout leaf = AmmoAssignmentUtility.GetAmmoLeaf(p);
                foreach (LoadoutSlot s in leaf?.OwnSlots ?? Enumerable.Empty<LoadoutSlot>())
                    if ((Def)s.thingDef == d || (Def)s.genericDef == d) n += Mathf.Max(0, s.count);
            }
            // DAS Schreiben hat gefehlt (2026-08-13): der Cache wurde angelegt,
            // geleert und gelesen - nur nie gefuellt, also konnte er per Konstruktion nie treffen. Der
            // Kommentar darueber beschreibt seit dem 12.08. eine Wirkung, die es nie gab: dieser Rumpf lief
            // bei JEDEM Aufruf ueber alle Kolonisten, mit CarriedPackSlots und GetAmmoLeaf pro Pawn.
            // Der direkte Nachbar ComputeItemOwnership macht es zwanzig Zeilen weiter richtig.
            assignedNeedCache[d] = n;
            return n;
        }

        public static void ComputeItemOwnership(Def def, out int carriedOnly, out int carriedAndAssigned, out int assignedInStore, out int freeStored, out int inProduction)
        {
            carriedOnly = carriedAndAssigned = assignedInStore = freeStored = inProduction = 0;
            if (def == null) return;
            if (Time.realtimeSinceStartup - itemOwnCacheTime > 0.3f)
            {
                itemOwnCache.Clear();
                itemOwnCacheTime = Time.realtimeSinceStartup;
            }
            if (itemOwnCache.TryGetValue(def, out ItemOwn cached))
            {
                carriedOnly = cached.carriedOnly; carriedAndAssigned = cached.carriedAndAssigned;
                assignedInStore = cached.assignedInStore; freeStored = cached.freeStored; inProduction = cached.inProduction;
                return;
            }
            ComputeItemOwnershipUncached(def, out carriedOnly, out carriedAndAssigned, out assignedInStore, out freeStored, out inProduction);
            itemOwnCache[def] = new ItemOwn { carriedOnly = carriedOnly, carriedAndAssigned = carriedAndAssigned, assignedInStore = assignedInStore, freeStored = freeStored, inProduction = inProduction };
        }

        private static void ComputeItemOwnershipUncached(Def def, out int carriedOnly, out int carriedAndAssigned, out int assignedInStore, out int freeStored, out int inProduction)
        {
            ThingDef td = def as ThingDef;
            LoadoutGenericDef gd = def as LoadoutGenericDef;

            // + GroundStockCount (2026-08-13): lose auf
            // der Karte liegende Medizin/Rationen sind genauso aufhebbar wie eingelagerte, und fuer Munition
            // und Waffen zaehlt der Boden laengst mit. GenericStockCount bringt seinen Boden-Anteil selbst
            // schon mit, deshalb nur im konkreten Zweig addieren.
            int stored = td != null ? StoredStockCount(td) + GroundStockCount(td) : GenericStockCount(gd);
            int production = 0;
            if (td != null) production = WeaponQueuedCount(td);
            else if (gd != null)
                foreach (ThingDef member in GenericFamily(gd)) production += WeaponQueuedCount(member);

            // ============ ZWEI Korrekturen, beide aus dem Buckler-Befund (2026-08-18) ============
            //
            // (1) GETRAGENES zaehlt mit. Bis hierher galt als "getragen" ausschliesslich das INVENTAR.
            // Ein Buckler ist Apparel: angezogen steckt er in WornApparel, nicht im Inventar - und weil er
            // dabei auch nicht gespawnt ist, faellt er ebenso aus dem LAGER-Zaehler. Ergebnis: er war in
            // keinem einzigen Topf. Genau das war der Befund "wir haben 4 Buckler, aber nur 3 werden
            // angezeigt". Dieselbe Blindheit ist am 2026-08-13 schon einmal in ComputeAvailability behoben
            // worden (dort steht der Kommentar "Ein CE-Schild ist Apparel, kein Equipment und kein Inventar -
            // es fiel durch alle drei Pruefungen"); dieser Weg hier ist die vierte Fundstelle und wurde
            // damals nicht mitgezogen. Ausruestung in der Hand zaehlt aus demselben Grund mit.
            //
            // (2) "Zugewiesen UND getragen" wird PRO KOLONIST verrechnet, nicht als Schnittmenge zweier
            // Koloniesummen. Seine Rechnung dazu, und sie ist richtig: vier Kolonisten haben einen Buckler
            // zugewiesen, drei tragen ihn auch, und ein vierter Buckler ist bei jemandem gelandet, der gar
            // keinen zugewiesen hat. Mit den alten Koloniesummen war getragen=4 und Bedarf=4, also
            // min(4,4) = 4 "zugewiesen und getragen" - obwohl einer davon bei einem Kolonisten OHNE
            // Zuweisung liegt und einem Zugewiesenen umgekehrt gar keiner zur Verfuegung steht.
            // Richtig ist die Zuordnung je Person: wer traegt, was ihm zusteht (zugewiesen und getragen),
            // wer traegt, was ihm NICHT zusteht (nur getragen), und wem etwas zusteht, das er nicht hat
            // (offener Bedarf, der aus dem Lager gedeckt oder als fehlend ausgewiesen wird).
            int carried = 0, demand = 0, matched = 0, openDemand = 0;
            foreach (Pawn p in AllColonyPawns())
            {
                int pawnCarried = 0;
                if (p.inventory?.innerContainer != null)
                {
                    if (td != null) pawnCarried += p.inventory.innerContainer.Where(t => t.def == td).Sum(t => t.stackCount);
                    else if (gd?.lambda != null) pawnCarried += p.inventory.innerContainer.Where(t => t.def != null && gd.lambda(t.def)).Sum(t => t.stackCount);
                }
                // Magazin mitzaehlen (2026-08-30, dieselbe Zaehlung wie PawnCarriedCount - siehe MagazineAmmoCount).
                if (td != null) pawnCarried += MagazineAmmoCount(p, d => d == td);
                else if (gd?.lambda != null) pawnCarried += MagazineAmmoCount(p, d => gd.lambda(d));
                if (p.apparel?.WornApparel != null)
                {
                    if (td != null) pawnCarried += p.apparel.WornApparel.Where(t => t.def == td).Sum(t => t.stackCount);
                    else if (gd?.lambda != null) pawnCarried += p.apparel.WornApparel.Where(t => t.def != null && gd.lambda(t.def)).Sum(t => t.stackCount);
                }
                if (p.equipment?.Primary != null)
                {
                    if (td != null && p.equipment.Primary.def == td) pawnCarried += p.equipment.Primary.stackCount;
                    else if (gd?.lambda != null && gd.lambda(p.equipment.Primary.def)) pawnCarried += p.equipment.Primary.stackCount;
                }
                carried += pawnCarried;
                int pawnDemand = 0;
                // CarriedPackSlots statt GetPawnPacks (2026-08-13 abends):
                // GetPawnPacks kennt nur angehaengte Pack-OBJEKTE, eine Rolle ist keins. Dieselbe
                // Blindheit, die heute frueh an vier anderen Stellen behoben wurde - diese hier ist
                // durchgerutscht, obwohl der Aufrufer daneben (ColonyWideAssignedNeed) laengst umgestellt
                // war. Fehlszenario: zwei Kolonisten auf einer Medic-Rolle mit je 5 Medizin, 3 im Lager -
                // der Bedarf hier war 0, also stand im Tooltip "Available: 3", waehrend die Fehlmenge
                // daneben schon 7 sagte. Zwei Zahlen aus einer Zelle, die sich widersprechen.
                foreach (LoadoutSlot s in AmmoAssignmentUtility.CarriedPackSlots(p))
                {
                    if (td != null && s.thingDef == td) pawnDemand += s.count;
                    else if (gd != null && s.genericDef == gd) pawnDemand += s.count;
                }
                // Plus die EINZELPOSTEN am eigenen Anker (2026-09-06, oi-any-medicine-total-off2). Die
                // Bedarfsseite (ColonyWideAssignedNeed) und die "Assigned to"-Aufzaehlung zaehlen sie seit
                // dem 16.08. mit - dieser Bestandszaehler hier nicht. Dieselbe Frage ("was ist diesem
                // Kolonisten zugeteilt"), zwei Antworten: ein ueber "Sonstiges" zugewiesener Posten stand
                // in der Fehlmenge als gefordert, sein getragenes Stueck aber als "ohne Zuweisung" - und die
                // Zeilen der Kurzform gingen um genau diese Stuecke nicht auf. Gleicher Filter wie dort:
                // keine Waffen, keine Munition (die haben eigene Zaehler).
                Loadout ownAnchor = p.GetLoadout();
                if (ownAnchor?.OwnSlots != null)
                    foreach (LoadoutSlot s in ownAnchor.OwnSlots)
                    {
                        if (s.thingDef != null && (s.thingDef.IsWeapon || IsAmmoDef(s.thingDef))) continue;
                        if (td != null && s.thingDef == td) pawnDemand += s.count;
                        else if (gd != null && s.genericDef == gd) pawnDemand += s.count;
                    }
                demand += pawnDemand;
                // Die Zuordnung, um die es geht: nur was DIESER Kolonist traegt UND zugewiesen hat, ist
                // "zugewiesen und getragen".
                matched += Mathf.Min(pawnCarried, pawnDemand);
                openDemand += Mathf.Max(0, pawnDemand - pawnCarried);
            }

            carriedAndAssigned = matched;
            carriedOnly = carried - matched;
            assignedInStore = Mathf.Min(stored, openDemand);
            freeStored = stored - assignedInStore;
            inProduction = production;
        }

        // Caps a name-by-name breakdown list at `cap` entries plus a trailing "...and N more" summary line,
        // so a large colony's per-item location/ownership tooltip can't grow unboundedly (2026-08-01, hoisted
        // out of Dialog_GenerateLoadouts.ComputeWeaponStats' own private closure into a shared helper so
        // every "Carried by:/In storage:/Assigned to:" style breakdown in the mod - weapons, ammo, pack
        // items - gets the same cap instead of each caller needing to remember to add its own).
        public static string JoinCapped(IEnumerable<string> lines, int cap = 10)
        {
            List<string> distinct = lines.Distinct().ToList();
            string joined = string.Join("\n", distinct.Take(cap).Select(s => s.Replace(" ", "\u00A0")).ToArray());
            if (distinct.Count > cap) joined += "\n" + "CER_LoadoutUtility_AndNMoreLine".Translate((distinct.Count - cap).ToString());
            return joined;
        }

        // "Where is it / who has it" breakdown for a weapon - same section-headered format the pack-item
        // version below already shows. GetWeaponInventory only ever returns bare counts, and
        // Dialog_GenerateLoadouts.ComputeWeaponStats' own equivalent scan is a private instance method (needs
        // a live Dialog_GenerateLoadouts, which not every caller has) - this is the shared, dialog-independent
        // version for anyone who just wants the tooltip strings for one weapon (2026-08-01, added for
        // Dialog_SupplyLoadouts' own Availability bars, which previously showed no location detail at all
        // unlike every sibling screen).
        // Hat die Kolonie mehr als eine Karte? Bricht beim zweiten Treffer ab, kostet also nichts.
        public static bool HasMultiplePlayerMaps()
        {
            int n = 0;
            foreach (Map m in PlayerMaps()) { n++; if (n > 1) return true; }
            return false;
        }

        // Ortsangabe MIT Kartennamen, sobald die Kolonie mehr als eine Karte hat.
        // Grund (2026-08-28, Fall "das Spiel sagt, da liegt ein Monoschwert, ich finde keins"):
        // jeder Bestandszaehler des Mods laeuft ueber ALLE Kolonie-Karten (PlayerMaps), die Ortszeile sagte
        // aber nur "Boden" oder den Namen der Lagerzone. Ein Stueck auf der zweiten Siedlung liest sich damit
        // wie eines vor der Haustuer - und RimWorlds eigene Suche findet es garantiert nicht, weil sie
        // ausdruecklich nur die AKTUELLE Karte durchsucht ("Search the current map"). Bei einer einzigen
        // Karte bleibt die Zeile unveraendert, der Name waere dort nur Rauschen.
        public static string LocationLabel(string loc, Map map, bool multiMap)
        {
            if (!multiMap || map == null) return loc;
            string mapName = map.Parent != null ? map.Parent.LabelCap.ToString() : null;
            return string.IsNullOrEmpty(mapName) ? loc : "CER_LoadoutUtility_LocationOnMap".Translate(loc, mapName).ToString();
        }

        public static string BuildWeaponLocationTooltip(ThingDef w)
        {
            if (w == null) return null;
            Dictionary<string, int> storedDict = new Dictionary<string, int>();
            bool multiMap = HasMultiplePlayerMaps();
            foreach (Map map in PlayerMaps())
            {
                foreach (Thing t in map.listerThings.ThingsOfDef(w))
                {
                    if (!t.Position.IsValid || IsHiddenInFog(t)) continue;
                    var slotGroup = t.Position.GetSlotGroup(map);
                    if (slotGroup?.parent == null) continue;   // loose/carried, not actually stored
                    string loc = LocationLabel(slotGroup.parent.SlotYielderLabel(), map, multiMap);
                    storedDict[loc] = (storedDict.TryGetValue(loc, out int sc) ? sc : 0) + t.stackCount;
                }
            }

            List<string> carriedBy = new List<string>();
            List<string> assignedTo = new List<string>();
            foreach (Pawn p in AllColonyPawns())
            {
                int carried = 0;
                if (p.equipment?.Primary != null && p.equipment.Primary.def == w) carried++;
                if (p.inventory?.innerContainer != null)
                    carried += p.inventory.innerContainer.Where(t => t.def == w).Sum(t => t.stackCount);
                if (carried > 0) carriedBy.Add(p.LabelShortCap + " x" + carried);

                // KORREKTUR 2026-08-18: hier stand seit dem
                // 16.08. zusaetzlich eine Schleife ueber GetPawnSidearms, mit der Begruendung "eine Zweitwaffe
                // haengt NICHT in den Slots des Ankers". Diese Begruendung ist FALSCH, und sie hat den Wert
                // verdoppelt: GetPawnSidearms liefert genau die EXTRA-ELTERN des Ankers, und
                // Patch_Loadout_MultiParentSlots haengt deren Slots in `Slots` mit hinein. Dieselbe
                // Zweitwaffe wurde also einmal ueber die Vereinigung und einmal ueber die Schleife gezaehlt.
                // Genau das ist das Fehlerbild "eingefuegte Zweitwaffe kommt mit doppelter Menge an": ein voellig
                // leerer Kolonist, EIN angehaengtes Loadout mit Menge 4, angezeigt als x8.
                // `Slots` ist die vollstaendige Antwort - Vorlage, Rolle, Packs und Zweitwaffen zusammen.
                // Summe ueber ALLE passenden Slots statt FirstOrDefault: liefern zwei verschiedene Quellen
                // dieselbe Waffe (Vorlage UND Zweitwaffe), sind das wirklich zwei Stueck, und die eine Zeile
                // wegzuwerfen war die Gegenrichtung desselben Fehlers.
                int assignedCount = p.GetLoadout()?.Slots?.Where(s => s.thingDef == w).Sum(s => s.count) ?? 0;
                if (assignedCount > 0 && !CannotFight(p)) assignedTo.Add(p.LabelShortCap + " x" + assignedCount);
            }

            string storedTT = storedDict.Count > 0 ? JoinCapped(storedDict.Select(kv => kv.Key + ": " + kv.Value)) : null;
            string carriedTT = carriedBy.Count > 0 ? JoinCapped(carriedBy) : null;
            string assignedTT = assignedTo.Count > 0 ? JoinCapped(assignedTo) : null;
            string result = BuildLocationTooltip(storedTT, carriedTT, assignedTT, BuildStuffBreakdownLines(w));
            return string.IsNullOrEmpty(result) ? null : result;
        }

        // Same idea as BuildWeaponLocationTooltip, for ammo - stored only counts items sitting in an actual
        // SlotGroup (matches IngredientStockCount's own resourceCounter-based definition of "in stock"; loose
        // ground ammo is intentionally excluded here too, same as everywhere else "in stock" is shown).
        public static string BuildAmmoLocationTooltip(ThingDef ammoDef)
        {
            if (ammoDef == null) return null;
            Dictionary<string, int> storedDict = new Dictionary<string, int>();
            bool multiMap = HasMultiplePlayerMaps();
            foreach (Map map in PlayerMaps())
            {
                foreach (Thing t in map.listerThings.ThingsOfDef(ammoDef))
                {
                    if (!t.Position.IsValid || IsHiddenInFog(t)) continue;
                    var slotGroup = t.Position.GetSlotGroup(map);
                    if (slotGroup?.parent == null) continue;
                    string loc = LocationLabel(slotGroup.parent.SlotYielderLabel(), map, multiMap);
                    storedDict[loc] = (storedDict.TryGetValue(loc, out int sc) ? sc : 0) + t.stackCount;
                }
            }

            List<string> carriedBy = new List<string>();
            List<string> assignedTo = new List<string>();
            foreach (Pawn p in AllColonyPawns())
            {
                // Inventar UND Magazin (2026-08-30, MagazineAmmoCount) - wie CE selbst zaehlt.
                int carried = PawnCarriedCount(p, ammoDef);
                if (carried > 0) carriedBy.Add(p.LabelShortCap + " x" + carried);

                int slotCount = p.GetLoadout()?.Slots?.Where(s => s.thingDef == ammoDef).Sum(s => s.count) ?? 0;
                if (slotCount > 0) assignedTo.Add(p.LabelShortCap + " x" + slotCount);
            }

            string storedTT = storedDict.Count > 0 ? JoinCapped(storedDict.Select(kv => kv.Key + ": " + kv.Value)) : null;
            string carriedTT = carriedBy.Count > 0 ? JoinCapped(carriedBy) : null;
            string assignedTT = assignedTo.Count > 0 ? JoinCapped(assignedTo) : null;
            string result = BuildLocationTooltip(storedTT, carriedTT, assignedTT);
            return string.IsNullOrEmpty(result) ? null : result;
        }

        // "Where is it / who has it" breakdown for a pack item (concrete or generic), same section-headered
        // format Weapon Loadouts' own availability bar already shows (Dialog_GenerateLoadouts.GetWeaponStats
        // + BuildLocationTooltip) - the same level of detail here ("which food, where is it")
        // instead of the bare "Total/Available" count ComputeItemOwnership's raw numbers gave on their own.
        // Not cached/memoised like GetWeaponStats - an Equipment Pack popup has a handful of rows, not a
        // whole colony's weapon list, so a fresh scan per tooltip hover is cheap enough not to need it.
        public static string BuildPackItemLocationTooltip(Def def)
        {
            ThingDef td = def as ThingDef;
            LoadoutGenericDef gd = def as LoadoutGenericDef;
            List<ThingDef> concretes = td != null ? new List<ThingDef> { td } : GenericFamily(gd);
            if (concretes.Count == 0) return null;
            bool multiConcrete = concretes.Count > 1;

            Dictionary<string, int> storedDict = new Dictionary<string, int>();
            bool multiMap = HasMultiplePlayerMaps();
            foreach (Map map in PlayerMaps())
            {
                foreach (ThingDef c in concretes)
                {
                    foreach (Thing t in map.listerThings.ThingsOfDef(c))
                    {
                        if (!t.Position.IsValid || IsHiddenInFog(t)) continue;
                        var slotGroup = t.Position.GetSlotGroup(map);
                        if (slotGroup?.parent == null) continue;   // loose/carried, not actually stored
                        string loc = LocationLabel(slotGroup.parent.SlotYielderLabel(), map, multiMap);
                        string key = multiConcrete ? c.LabelCap + " @ " + loc : loc;
                        storedDict[key] = (storedDict.TryGetValue(key, out int sc) ? sc : 0) + t.stackCount;
                    }
                }
            }

            List<string> carriedBy = new List<string>();
            List<string> assignedTo = new List<string>();
            foreach (Pawn p in AllColonyPawns())
            {
                int inv = 0;
                if (p.inventory?.innerContainer != null)
                {
                    inv = td != null
                        ? p.inventory.innerContainer.Where(t => t.def == td).Sum(t => t.stackCount)
                        : p.inventory.innerContainer.Where(t => t.def != null && gd?.lambda != null && gd.lambda(t.def)).Sum(t => t.stackCount);
                }
                // Magazin mitzaehlen (2026-08-30, MagazineAmmoCount).
                if (td != null) inv += MagazineAmmoCount(p, d => d == td);
                else if (gd?.lambda != null) inv += MagazineAmmoCount(p, d => gd.lambda(d));
                if (inv > 0) carriedBy.Add(p.LabelShortCap + " x" + inv);

                // CarriedPackSlots statt GetPawnPacks (2026-08-13 abends) - siehe die gleichlautende
                // Korrektur in ComputeItemOwnershipUncached. Genau dieses Symptom stand schon einmal im
                // Bericht ("in 'Assigned to' fehlte die Rollen-Traegerin ganz"); behoben war damals nur die
                // Pack-Zelle im Assign-Tab, nicht diese Aufzaehlung, die im Pack-Editor, im Pack-Assistenten
                // und im Nachschub-Fenster haengt.
                int demand = 0;
                foreach (LoadoutSlot s in AmmoAssignmentUtility.CarriedPackSlots(p))
                {
                    if (td != null && s.thingDef == td) demand += s.count;
                    else if (gd != null && s.genericDef == gd) demand += s.count;
                }
                // Dazu die EINZELPOSTEN des Kolonisten selbst (2026-08-16, zweite Haelfte desselben
                // Befunds: "2 frisch zugewiesene Component blieben 'Assigned, in storage: 0' ohne
                // 'Assigned to'-Zeile"). Was in der Zelle "Sonstiges" steht, liegt in den EIGENEN Slots
                // seines Ankers und nicht in einem Pack - CarriedPackSlots konnte es also gar nicht sehen.
                // Genau dieselbe Menge, die DrawOtherItemsCell zeichnet: eigene Slots, keine Waffen, keine
                // Munition.
                Loadout ownAnchor = p.GetLoadout();
                if (ownAnchor?.OwnSlots != null)
                    foreach (LoadoutSlot s in ownAnchor.OwnSlots)
                    {
                        if (s.thingDef != null && (s.thingDef.IsWeapon || IsAmmoDef(s.thingDef))) continue;
                        if (td != null && s.thingDef == td) demand += s.count;
                        else if (gd != null && s.genericDef == gd) demand += s.count;
                    }
                if (demand > 0) assignedTo.Add(p.LabelShortCap + " x" + demand);
            }

            string storedTT = storedDict.Count > 0 ? JoinCapped(storedDict.Select(kv => kv.Key + ": " + kv.Value)) : null;
            string carriedTT = carriedBy.Count > 0 ? JoinCapped(carriedBy) : null;
            string assignedTT = assignedTo.Count > 0 ? JoinCapped(assignedTo) : null;
            string result = BuildLocationTooltip(storedTT, carriedTT, assignedTT, BuildStuffBreakdownLines(def));
            return string.IsNullOrEmpty(result) ? null : result;
        }

        // Scans every colonist's attached Equipment Pack(s) for items not sufficiently covered (owned +
        // carried + in production, via ComputeItemOwnership above) - the Pack equivalent of
        // ScanMissingAssignedWeapons/ComputeAmmoNeedForAssignedPawns, used by the same end-of-session
        // production check (2026-07-30). A generic slot ("Any Medicine") is resolved to ONE concrete family
        // member to actually queue a bill for (a bill always targets one specific recipe) - see
        // ResolveGenericPackItem below; the AVAILABILITY check itself never needed that resolution since
        // ComputeItemOwnership already sums a generic's whole family directly.
        public static void ScanMissingAssignedPackItems(out Dictionary<ThingDef, int> missingItems, out Dictionary<ThingDef, List<string>> usedBy)
            => ScanMissingAssignedPackItems(AllColonyPawns().Select(p => (p, AmmoAssignmentUtility.GetPawnPacks(p))), out missingItems, out usedBy, out _);

        public static void ScanMissingAssignedPackItems(IEnumerable<(Pawn pawn, List<Loadout> packs)> assignments,
            out Dictionary<ThingDef, int> missingItems, out Dictionary<ThingDef, List<string>> usedBy)
            => ScanMissingAssignedPackItems(assignments, out missingItems, out usedBy, out _);

        // Same computation, but sourced from an explicit (pawn, packs) set instead of reading each pawn's LIVE
        // anchor via AmmoAssignmentUtility.GetPawnPacks - lets a caller pass a PLANNED assignment that hasn't
        // been applied to pawns yet (Dialog_AssignLoadouts.ApplyPreviewPlan's deferred-apply flow, same reason
        // ComputeAmmoNeedForLoadouts exists alongside ComputeAmmoNeedForAssignedPawns). 2026-07-31: this is how
        // the Arm-my-Colony in-chain Supply popup folds Equipment Pack shortfalls into itself instead of only
        // ever catching them via the separate end-of-chain safety-net popup.
        // demandDefs: zu jedem herausgegebenen KONKRETEN Def der Def, an dem der BEDARF wirklich haengt -
        // bei einem generischen Pack-Eintrag ("Any Medicine") also der generische, nicht das konkrete
        // Herstellungsziel (2026-08-19, supply-medicine-row-numbers).
        // Warum das gebraucht wird: `concrete` unten ist eine Antwort auf "wofuer kann ich einen AUFTRAG
        // anlegen" (ResolveGenericPackItem), nicht auf "worum geht es hier". Wer damit den Bestand befragt,
        // stellt eine andere Frage als der Equipment-Packs-Reiter - und bekommt eine andere Antwort:
        // ComputeItemOwnership zaehlt fuer einen KONKRETEN Def nur konkret benannte Pack-Eintraege als
        // Bedarf, ein generischer faellt durch. Gemessener Fall: 600 gefordert ("Any Medicine", 3 Kolonisten
        // x 200), 75 im Lager - der Packs-Reiter sagte "Assigned, in storage: 75 / Available: 0", das
        // Nachschub-Fenster "Assigned: 0 / Available: 75", und die Zeile las sich als voll gedeckt.
        public static void ScanMissingAssignedPackItems(IEnumerable<(Pawn pawn, List<Loadout> packs)> assignments,
            out Dictionary<ThingDef, int> missingItems, out Dictionary<ThingDef, List<string>> usedBy,
            out Dictionary<ThingDef, Def> demandDefs)
        {
            Dictionary<Def, int> demand = new Dictionary<Def, int>();
            Dictionary<Def, List<string>> demandedBy = new Dictionary<Def, List<string>>();
            foreach (var (p, packs) in assignments)
            {
                // Versorgung aus einer ROLLE zaehlt mit (2026-08-13) - siehe
                // AmmoAssignmentUtility.RoleSupplySlots. `packs` enthaelt nur angehaengte Pack-OBJEKTE, und
                // eine Rolle ist keins: sie backt ihren Inhalt in ein flaches Loadout. Ohne das entstand fuer
                // Rollen-Traeger nie eine Fehlmenge, egal wie leer das Lager war.
                //
                // RoleSupplySlots, NICHT CarriedPackSlots (korrigiert am 2026-08-13 abends):
                // CarriedPackSlots liefert BEIDES - die Rollen-Slots UND die OwnSlots der
                // angehaengten Packs. Die Schleife darunter zaehlt dieselben angehaengten Packs gleich noch
                // einmal. Meine Begruendung von heute frueh ("ein Pawn hat entweder Packs ODER eine Rolle")
                // ist falsch: EnsureAmmoLink leert die Extra-Eltern nur im MOMENT des Rollenwechsels, danach
                // darf man jedem Kolonisten frei Packs anhaengen - die Pack-Auswahl in Assign Loadouts
                // koennen an einem Kolonisten sehr wohl beide Quellen haengen.
                // PRAEZISIERT 2026-08-18 (Frage: wenn eine Rolle zugewiesen ist, sind die Equipment Packs
                // gesperrt - zu Recht?): der Halbsatz, der hier stand
                // ("die Pack-Auswahl in Assign Loadouts sperrt Rollen-Traeger ausdruecklich nicht aus"),
                // stimmt seit dem 2026-08-15 nicht mehr - dort ist die Zelle fuer Rollen-Traeger
                // ausgegraut. Der Doppelzaehler bleibt trotzdem noetig, nur ueber einen
                // anderen Weg: eine ROLLE kann selbst ein Equipment Pack tragen (CommitRole haengt es als
                // Extra-Elternteil an, statt es einzuschmelzen). Ein Kolonist auf so einer Rolle hat also
                // Rollen-Versorgungsslots UND Pack-Eltern - genau die Lage, um die es hier geht.
                // Fehlszenario: Rolle mit 5 Medizin plus angehaengtes Pack mit 3 -> Bedarf 3+5+3 = 11 statt 8.
                // Die beiden Schwesterstellen in Dialog_SquadSetup (BuildPlanWideShortfalls und
                // PackRequirementsFor) machen es seit heute schon richtig - das hier war die dritte.
                Loadout roleLo = AmmoAssignmentUtility.DisplayLoadout(p?.GetLoadout());
                if (roleLo != null)
                    foreach (LoadoutSlot rs in AmmoAssignmentUtility.RoleSupplySlots(roleLo))
                    {
                        Def rd = (Def)rs.thingDef ?? rs.genericDef;
                        if (rd == null) continue;
                        demand[rd] = (demand.TryGetValue(rd, out int rc) ? rc : 0) + rs.count;
                        if (!demandedBy.TryGetValue(rd, out List<string> rnames)) demandedBy[rd] = rnames = new List<string>();
                        if (!rnames.Contains(p.LabelShortCap)) rnames.Add(p.LabelShortCap);
                    }
                if (packs == null) continue;
                foreach (Loadout pack in packs)
                {
                    if (pack?.OwnSlots == null) continue;
                    foreach (LoadoutSlot s in pack.OwnSlots)
                    {
                        Def d = (Def)s.thingDef ?? s.genericDef;
                        if (d == null) continue;
                        demand[d] = (demand.TryGetValue(d, out int c) ? c : 0) + s.count;
                        if (!demandedBy.TryGetValue(d, out List<string> names)) demandedBy[d] = names = new List<string>();
                        if (!names.Contains(p.LabelShortCap)) names.Add(p.LabelShortCap);
                    }
                }
            }

            var missing = new Dictionary<ThingDef, int>();
            var usedByLocal = new Dictionary<ThingDef, List<string>>();
            var demandDefLocal = new Dictionary<ThingDef, Def>();
            // Shared remaining-stock pool keyed by CONCRETE ThingDef, decremented as EACH demand key
            // (concrete or generic) claims from it (2026-08-01 fix) - independently calling ComputeItemOwnership
            // per Def key let a concrete item (e.g. "Penoxycycline") and a generic whose family includes it
            // (e.g. "Any Medicine") BOTH see the SAME real stock as fully theirs, silently under-reporting a
            // genuine shortfall whenever some colonists were pinned to the concrete item and others left on
            // the generic. Mirrors ScanMissingAssignedWeapons' own claim-and-decrement pattern, generalized to
            // walk a generic's whole family instead of a single ThingDef. Local to this one scan call - not a
            // cross-call cache, so no staleness risk, just a shared "what's left" view for THIS pass.
            Dictionary<ThingDef, int> remainingConcreteStock = new Dictionary<ThingDef, int>();
            int RemainingFor(ThingDef td)
            {
                if (!remainingConcreteStock.TryGetValue(td, out int r))
                {
                    ComputeItemOwnership(td, out int co, out int ca, out int ais, out int fs, out int ip);
                    r = co + ca + ais + fs + ip;
                }
                return r;
            }
            // Concrete-keyed demand claims first (a pawn pinned to a specific item is the more specific
            // intent), generics then share whatever's left across their family members.
            foreach (var kv in demand.OrderByDescending(kv => kv.Key is ThingDef))
            {
                List<ThingDef> family = kv.Key is ThingDef ctd ? new List<ThingDef> { ctd } : GenericFamily(kv.Key as LoadoutGenericDef);
                int need = kv.Value;
                int have = 0;
                foreach (ThingDef member in family)
                {
                    if (have >= need) break;
                    int r = RemainingFor(member);
                    int take = Mathf.Min(r, need - have);
                    if (take <= 0) continue;
                    remainingConcreteStock[member] = r - take;
                    have += take;
                }
                ThingDef concrete = kv.Key as ThingDef ?? ResolveGenericPackItem(kv.Key as LoadoutGenericDef);
                if (concrete == null) continue;
                // usedBy auch bei voller Deckung (2026-08-31) - Bedarfssemantik, siehe die Korrektur in
                // ScanMissingAssignedWeapons.Note und ComputeMissingForCreatedLoadouts.Track.
                if (!usedByLocal.TryGetValue(concrete, out List<string> list)) usedByLocal[concrete] = list = new List<string>();
                foreach (string n in demandedBy[kv.Key]) if (!list.Contains(n)) list.Add(n);
                int shortfall = need - have;
                if (shortfall <= 0) continue;
                missing[concrete] = (missing.TryGetValue(concrete, out int mc) ? mc : 0) + shortfall;
                RecordPackDemandDef(demandDefLocal, concrete, kv.Key);
            }
            missingItems = missing;
            usedBy = usedByLocal;
            demandDefs = demandDefLocal;
        }

        // Mehrere Bedarfsschluessel koennen auf DASSELBE konkrete Herstellungsziel zeigen (eine konkrete
        // Zuweisung und ein generischer Eintrag, der sie mit umfasst). Dann gibt es keinen EINEN Def, an dem
        // der Bedarf haengt - und in dem Fall lieber gar keinen eintragen als einen der beiden behaupten:
        // die Aufrufer fallen ohne Eintrag auf das konkrete Def zurueck, also auf das bisherige Verhalten.
        private static void RecordPackDemandDef(Dictionary<ThingDef, Def> map, ThingDef concrete, Def source)
        {
            if (map.TryGetValue(concrete, out Def had))
            {
                if (had != source) map[concrete] = concrete;   // uneindeutig -> zurueck auf das konkrete Def
                return;
            }
            map[concrete] = source;
        }

        // Preferred concrete family member for a generic pack item when queuing production. No slider/cost
        // preference here (unlike the wizard's own PickMedicineDef/PickFoodDef/PickDrugDef) - this only fires
        // for an ALREADY-assigned pack that needs restocking, not for building one from scratch.
        //
        // HERSTELLBARKEIT SCHLAEGT BESTAND (2026-08-13). Vorher gewann
        // "irgendetwas davon liegt im Lager", und genau das hat die Medizin-Zeile im Nachschub-Fenster
        // verschwinden lassen: "Any Medicine" loeste auf Kraeutermedizin auf, sobald auch nur ein Buendel
        // davon dalag - und Kraeutermedizin hat in Vanilla ueberhaupt kein Rezept (sie waechst). Der
        // IsProducible-Filter der Aufrufer warf die Zeile danach lautlos weg, waehrend der Tooltip
        // weiterrechnete und "Still missing: 42" sagte.
        // Der Zweck dieser Funktion ist, einen AUFTRAG anlegen zu koennen; ein Def, fuer das kein Auftrag
        // entstehen kann, erfuellt ihn nicht. Die alte Absicht ("keine Lieferkette wechseln, nur weil
        // nachgefuellt wird") bleibt als erste Wahl erhalten - sie gilt jetzt nur noch unter denen, die
        // sich auch wirklich herstellen lassen.
        private static ThingDef ResolveGenericPackItem(LoadoutGenericDef gd)
        {
            if (gd == null) return null;
            List<ThingDef> family = GenericFamily(gd);
            bool Researched(ThingDef td) { RecipeDef r = GetRecipeFor(td); return r != null && RecipeIsResearched(r); }
            return family.FirstOrDefault(td => IsProducible(td) && IngredientStockCount(td) > 0)   // laeuft schon UND machbar
                ?? family.FirstOrDefault(IsProducible)                                             // machbar, Rezept + Bank stehen
                ?? family.FirstOrDefault(Researched)                                               // Rezept erforscht, Bank fehlt
                ?? family.FirstOrDefault(td => IngredientStockCount(td) > 0)                       // wenigstens das, was da ist
                ?? family.FirstOrDefault();
        }

        // These three (plus AllAmmoDefs below) all scan the full ThingDef database with LINQ - defs never
        // change within a running game, so like the generics above, compute once and cache rather than
        // re-filtering thousands of defs every single UI frame a caller redraws in (Dialog_Ingredients calls
        // all three from DoWindowContents, unconditionally, every frame the tab is open).
        private static List<Def> foodOptionsCache, medicineOptionsCache, drugOptionsCache;
        private static List<ThingDef> allAmmoDefsCache;

        // Loadout-worthy food, ordered: real meals first, then shelf-stable travel food, then the raw-tasty rest,
        // generic categories ("Any Meal", ...) appended.
        public static List<Def> LoadoutFoodOptions()
        {
            if (foodOptionsCache != null) return foodOptionsCache;
            EnsureGenericSupplyDefs();
            int TravelRank(ThingDef d)
            {
                if (d.ingestible.preferability >= FoodPreferability.MealAwful) return 0;
                CompProperties_Rottable rot = d.GetCompProperties<CompProperties_Rottable>();
                if (rot == null || rot.daysToRotStart >= 20f) return 1;   // keeps for weeks or never rots
                return 2;
            }
            List<Def> foods = new List<Def>();
            foods.AddRange(DefDatabase<ThingDef>.AllDefs
                .Where(d => d.IsIngestible && !d.IsDrug && (int)d.ingestible.preferability >= (int)FoodPreferability.RawTasty)
                .OrderBy(TravelRank).ThenBy(f => f.label)
                .Cast<Def>());
            foods.AddRange(foodGenerics);
            foodOptionsCache = foods;
            return foods;
        }

        // Loadout-worthy medicine (plus drug generics), ordered by label.
        public static List<Def> LoadoutMedicineOptions()
        {
            if (medicineOptionsCache != null) return medicineOptionsCache;
            EnsureGenericSupplyDefs();
            List<Def> meds = new List<Def>();
            meds.AddRange(DefDatabase<ThingDef>.AllDefs.Where(d => d.IsMedicine).Cast<Def>());
            meds.AddRange(medicineGenerics);
            medicineOptionsCache = meds.OrderBy(m => m.label).ToList();
            return medicineOptionsCache;
        }

        // Combat drugs/stims (go-juice, wake-up, etc.) - deliberately excludes recreational-only substances
        // covered elsewhere; IsDrug already matches vanilla + CE drug things regardless of category. Plus
        // CE's own "Any Drug"-style generics (see EnsureGenericSupplyDefs) that aren't food or medicine.
        public static List<Def> LoadoutDrugOptions()
        {
            if (drugOptionsCache != null) return drugOptionsCache;
            EnsureGenericSupplyDefs();
            List<Def> drugs = new List<Def>();
            drugs.AddRange(DefDatabase<ThingDef>.AllDefs.Where(d => d.IsDrug).OrderBy(d => d.label).Cast<Def>());
            drugs.AddRange(drugGenerics);
            drugOptionsCache = drugs;
            return drugs;
        }

        // Gecacht (2026-08-09): IsAssignableFrom ist Reflection, das Ergebnis
        // haengt nur an unveraenderlichen Def-Daten, und diese Funktion laeuft pro Slot in
        // Patch_Loadout_MultiParentSlots - also an Loadout.Slots, das CEs Think-Tree pro Kolonist und
        // Entscheidung anfasst. Session-Lebensdauer wie bei den anderen Def-Caches hier.
        private static readonly Dictionary<ThingDef, bool> ammoDefCache = new Dictionary<ThingDef, bool>();

        public static bool IsAmmoDef(ThingDef d)
        {
            if (d == null || d.thingClass == null) return false;
            if (ammoDefCache.TryGetValue(d, out bool cached)) return cached;
            bool result = typeof(AmmoThing).IsAssignableFrom(d.thingClass);
            ammoDefCache[d] = result;
            return result;
        }

        // Every concrete ammo ThingDef in the game (not scoped to one weapon) - used by the Ammo Ingredient
        // category, which is a free-standing "N rounds of X" bundle rather than tied to a specific gun.
        public static List<ThingDef> AllAmmoDefs()
        {
            if (allAmmoDefsCache != null) return allAmmoDefsCache;
            allAmmoDefsCache = DefDatabase<ThingDef>.AllDefs.Where(IsAmmoDef).OrderBy(d => d.label).ToList();
            return allAmmoDefsCache;
        }

        // Total in-stock count across every colony map, not just the one in view - the colony's real stockpile
        // is empire-wide, and a bench on any map can only ever be fed by ITS OWN map's stock anyway, so this is
        // informational-total, not "available to this specific bench" (bill queueing still happens per-bench).
        public static int IngredientStockCount(ThingDef d)
        {
            if (d == null) return 0;
            int total = 0;
            foreach (Map map in PlayerMaps()) total += map.resourceCounter.GetCount(d);
            return total;
        }

        // How much of this resource is lying loose on any colony map RIGHT NOW - never counted toward "in stock"
        // (IngredientStockCount, via CE's own resourceCounter, only ever counts items sitting in an actual
        // SlotGroup - a stockpile zone or storage building - confirmed by reading vanilla's own
        // ResourceCounter.UpdateResourceCounts, which iterates haulDestinationManager's SlotGroups
        // specifically). Shown purely as INFO in ingredient tooltips - user explicitly asked for this after
        // confirming the "storage zone required" behavior itself is fair/intentional, not a bug.
        // Kurzer Zeit-Cache, wie ihn jede andere Bestandsfunktion dieser Familie laengst hat (weaponInvCache
        // 1s, itemOwnCache, affordabilityCache 2s) - GroundStockCount und CarriedStockCount waren die
        // einzigen ohne, und genau sie werden pro Def gerufen: in RecomputeAssign (alle 150 ms, solange ein
        // Regler gezogen wird) und in CombatReadinessAlerts pro knapper Munitionssorte pro Kolonist
        // (2026-08-14, Befund 3 und 8 der zweiten Performance-Runde).
        // 1 Sekunde, aus demselben Grund wie bei den Nachbarn: die Fenster des Mods pausieren das Spiel, der
        // Bestand bewegt sich waehrenddessen praktisch nicht - und die Richtung eines Fehlers waere hier
        // "eine Sekunde lang die Zahl von vorhin", nicht eine falsche Entscheidung.
        private static readonly Dictionary<Def, int> groundStockCache = new Dictionary<Def, int>();
        private static float groundStockCacheTime = -99f;
        private static readonly Dictionary<Def, int> carriedStockCache = new Dictionary<Def, int>();
        private static float carriedStockCacheTime = -99f;

        private static bool StockCacheHit(Dictionary<Def, int> cache, ref float stamp, Def key, out int value)
        {
            value = 0;
            if (key == null) return false;
            if (Time.realtimeSinceStartup - stamp > 1f)
            {
                cache.Clear();
                stamp = Time.realtimeSinceStartup;
                return false;
            }
            return cache.TryGetValue(key, out value);
        }

        // "Wieviel davon liegt in einem echten Lager" - und zwar fuer JEDEN Gegenstand, nicht nur fuer die,
        // die RimWorld selbst als Rohstoff fuehrt (2026-08-18).
        //
        // IngredientStockCount liest map.resourceCounter, und der zaehlt ausschliesslich Defs mit
        // CountAsResource - also genau das, was in RimWorlds Rohstoffleiste oben links auftaucht. Kleidung,
        // Schilde, Waffen und Wurfsachen sind dort ausdruecklich NICHT dabei und liefern deshalb IMMER 0.
        // Zusammen mit GroundStockCount, das alles ueberspringt, was in einem Lager liegt, entsteht ein Loch,
        // durch das ein ganzer Gegenstandstyp faellt: ein Schild, ein Fernglas oder eine Granate aus einem
        // Equipment Pack, die im Regal liegt, zaehlt als GAR NICHT VORHANDEN - und das Nachschub-Fenster
        // schlaegt vor, sie herzustellen, obwohl sie danebenliegt.
        // Gefunden beim Nachgehen des Befunds "unser Bestand zaehlt Ware, die RimWorld noch nicht fuehrt":
        // dieselbe Wurzel, nur die andere Richtung - die beiden Raster (Lager / Boden) teilen die Welt nicht
        // sauber auf, sondern lassen dazwischen etwas liegen.
        //
        // Fuer Rohstoffe bleibt alles wie bisher (ein Woerterbuch-Zugriff auf RimWorlds eigenen Zaehler);
        // die Suche laeuft NUR fuer die Defs, die dort ohnehin 0 liefern, und mit demselben Sekunden-Cache
        // wie die Nachbarn. Waffen sind davon unberuehrt, die haben mit GetWeaponInventory ihre eigene,
        // vollstaendige Zaehlung.
        private static readonly Dictionary<Def, int> storedStockCache = new Dictionary<Def, int>();
        private static float storedStockCacheTime = -99f;

        public static int StoredStockCount(ThingDef d)
        {
            if (d == null) return 0;
            if (d.CountAsResource) return IngredientStockCount(d);
            if (StockCacheHit(storedStockCache, ref storedStockCacheTime, d, out int cached)) return cached;
            int total = 0;
            foreach (Map map in PlayerMaps())
                foreach (Thing t in map.listerThings.ThingsOfDef(d))
                    if (!IsHiddenInFog(t) && t.Position.GetSlotGroup(map) != null) total += t.stackCount;
            storedStockCache[d] = total;
            return total;
        }

        public static int GroundStockCount(ThingDef d)
        {
            if (d == null) return 0;
            if (StockCacheHit(groundStockCache, ref groundStockCacheTime, d, out int cached)) return cached;
            int result = GroundStockCountUncached(d);
            groundStockCache[d] = result;
            return result;
        }

        private static int GroundStockCountUncached(ThingDef d)
        {
            int total = 0;
            foreach (Map map in PlayerMaps())
                foreach (Thing t in map.listerThings.ThingsOfDef(d))
                {
                    if (IsHiddenInFog(t)) continue;
                    // Was in einem Lager liegt, zaehlt der LAGER-Zaehler - und das ist seit 2026-08-18
                    // StoredStockCount, nicht mehr direkt der resourceCounter: fuer alles, was RimWorld nicht
                    // als Rohstoff fuehrt (Kleidung, Schilde, Wurfsachen), zaehlte der naemlich gar nichts,
                    // und diese Zeile hier hat es zusaetzlich uebersprungen.
                    if (t.Position.GetSlotGroup(map) != null) continue;
                    // No "not forbidden" filter any more (2026-08-09) - see IsMarkedDoNotPickUp. Loose loot
                    // marked "do not pick up" is exactly what this function is asked about most often (a raid
                    // just ended), so it counts, and ForbiddenStockCount/DrawForbiddenMark carry the warning.
                    total += t.stackCount;
                }
            return total;
        }

        // ENTFERNT 2026-08-18: die Diagnosezeile zum Geisterbestand. Die Frage ist beantwortet - der Log-Auszug
        // zeigte die Fundstellen als echte Stapel auf der Karte (Halter "Map", kein Lager, gespawnt), unter
        // anderem sechs Stapel a 5000 Schuss Buckshot. Der Zaehler sagt also die Wahrheit; was fehlte, war
        // nur die Ortsangabe in der Anzeige, und die ist am 15.08. bewusst gestrichen worden.
        // Nach dem Fund wieder raus.


        // "Wie viel von dieser Munition hat die Kolonie GERADE" - alle drei Toepfe in EINER Funktion
        // (2026-08-13). Vorher hat jede Stelle ihre eigene Summe gebildet, und genau daran ist der Fehler
        // entstanden, der im Spiel zu sehen war: die Vorschau-Badge und der Verfuegbarkeits-Balken
        // rechneten Lager + Boden (+ Getragenes), das Tor, das ueber die ZEILE im Nachschub-Fenster
        // entscheidet, aber nur Lager + Getragenes. Fuer Munition, die auf dem Boden liegt - nach einem
        // Ueberfall der Normalfall -, sagte die eine Seite "alles gedeckt" (gruen) und die andere ordnete
        // im selben Durchgang eine Produktion an. Der Balken in derselben Zeile zeigte dann 335 verfuegbar
        // neben einem Auftrag ueber 12.
        // Eine Funktion, drei Toepfe, keine zweite Meinung mehr. Wer bewusst eine ENGERE Frage stellt
        // ("was kann eine Werkbank sofort verarbeiten"), nimmt weiterhin IngredientStockCount direkt -
        // dort ist der Bodenbestand richtigerweise draussen.
        public static int AmmoOnHand(ThingDef ammoDef)
            => IngredientStockCount(ammoDef) + GroundStockCount(ammoDef) + CarriedStockCount(ammoDef);

        // Der dritte Topf neben Lager und Boden: was die Kolonisten GERADE AM KOERPER haben (2026-08-12).
        // IngredientStockCount sieht das nicht (CEs resourceCounter zaehlt ausschliesslich SlotGroups, siehe
        // GroundStockCount) und GroundStockCount auch nicht (Inventar liegt nicht herum). Ohne diesen dritten
        // Topf ist jede Rechnung "Bedarf minus Bestand" fuer einen Kolonisten, der seine Sachen laengst
        // TRAEGT, um genau diese Stuecke zu hoch - und genau deshalb musste die Bedarfsseite frueher
        // stattdessen `kept`-Zeilen aussieben, was den ganzen echten Bedarf mit verschluckte
        // (bei den Zweitwaffen). Bestand ergaenzen statt Bedarf wegwerfen ist die richtige Haelfte.
        // Nur das Inventar, kein equipment.Primary und kein carryTracker - identisch zu dem, was
        // GetAmmoOwnership und ComputeItemOwnership als ihr eigenes `carried` zaehlen, damit die drei
        // Funktionen ueber denselben Gegenstand nie verschiedene Zahlen sagen koennen. Fuer Waffen und
        // Schilde ist das ohnehin die falsche Frage: dort rechnet Patch_LoadoutTooltip.ComputeAvailability
        // den Traeger bereits selbst heraus (es zaehlt Getragenes gar nicht erst als Bestand und laesst
        // dafuer dessen Bedarf weg).
        public static int CarriedStockCount(ThingDef d)
        {
            if (d == null) return 0;
            if (StockCacheHit(carriedStockCache, ref carriedStockCacheTime, d, out int cached)) return cached;
            int total = 0;
            foreach (Pawn p in AllColonyPawns())
            {
                if (p.inventory?.innerContainer == null) continue;
                foreach (Thing t in p.inventory.innerContainer)
                    if (t.def == d) total += t.stackCount;
            }
            carriedStockCache[d] = total;
            return total;
        }

        // Dieselbe Frage fuer einen generischen Eintrag ("Any Medicine"): ueber die Lambda des Generics
        // gematcht, nicht ueber einen willkuerlichen Vertreter-Def - exakt wie ComputeItemOwnership es tut.
        public static int CarriedStockCount(LoadoutGenericDef gd)
        {
            if (gd?.lambda == null) return 0;
            if (StockCacheHit(carriedStockCache, ref carriedStockCacheTime, gd, out int cached)) return cached;
            int total = 0;
            foreach (Pawn p in AllColonyPawns())
            {
                if (p.inventory?.innerContainer == null) continue;
                foreach (Thing t in p.inventory.innerContainer)
                    if (t.def != null && gd.lambda(t.def)) total += t.stackCount;
            }
            carriedStockCache[gd] = total;
            return total;
        }

        // The real per-colony "how much of this ammo is actually free right now" - raw stockpiled count minus
        // whatever every OTHER assigned colonist's own loadout already claims (that they don't already carry
        // in their inventory yet). A plain IngredientStockCount only ever answers "how much exists on the
        // map" - if 5 colonists all want 80 rounds of the same caliber and the map has 100 in storage, every
        // one of their rows independently reading "100 in stock, I need 80: ready" is misleading, since there
        // is nowhere near enough for all 5. Mirrors Patch_LoadoutTooltip.ComputeAvailability's exact
        // stored-minus-demanded convention for weapons (unique items, 1 demand per pawn) - here scaled by
        // each pawn's own configured count instead of a flat 1. Deliberately sums EVERY FreeColonist's
        // demand uniformly, including whichever pawn a caller happens to be checking THIS FOR - same
        // "how much slack exists in the system" semantics ComputeAvailability already uses, not "how much is
        // left over for everyone ELSE" (that would double-favor whichever pawn is being asked about).
        // Cached per ammo ThingDef, cleared once per rendered frame (2026-08-01 - see ComputeItemOwnership's
        // own comment for why Time.frameCount replaced a real-time TTL here: bounds every cached value to
        // the single frame it was computed in, so it can never survive stale into a frame where a dialog
        // transition changed what's actually being asked about). Called from SingleFulfillStatus for EVERY
        // ammo Def in every pawn's Ammo cell, every frame, unconditional on hover (same "very laggy" bug
        // class as ComputeItemOwnership's own cache - this one predates today but had no cache at all).
        private struct AmmoAvail { public int avail, stored, demanded; }
        private static readonly Dictionary<ThingDef, AmmoAvail> ammoAvailCache = new Dictionary<ThingDef, AmmoAvail>();
        private static int ammoAvailCacheFrame = -1;

        public static int ComputeAmmoAvailability(ThingDef ammoDef, out int storedCount, out int demandedFromStorage)
        {
            storedCount = 0;
            demandedFromStorage = 0;
            if (ammoDef == null) return 0;
            if (Time.frameCount != ammoAvailCacheFrame)
            {
                ammoAvailCache.Clear();
                ammoAvailCacheFrame = Time.frameCount;
            }
            if (ammoAvailCache.TryGetValue(ammoDef, out AmmoAvail cached))
            {
                storedCount = cached.stored; demandedFromStorage = cached.demanded;
                return cached.avail;
            }
            // + GroundStockCount (2026-08-02: ammo visibly sitting on the map still showed as
            // "missing" in the Assign Loadouts fulfillment badge) - deliberately DIFFERENT from
            // IngredientStockCount's own callers (DrawIngredientCell/DrawTotalCostLine, crafting-bill ingredient
            // cost), which stay zone-only on purpose (a bench can only draw from an actual SlotGroup, and the
            // restriction is correct for THAT context). Whether a colonist is
            // considered readily armed/supplied is a different question - loose ammo on the map is just as real
            // and pickup-able as shelved ammo, so it belongs in "available" here.
            storedCount = IngredientStockCount(ammoDef) + GroundStockCount(ammoDef);
            foreach (Pawn p in AllColonyPawns())
            {
                Loadout lo = p.GetLoadout();
                // Generische Slots zaehlen mit (2026-08-09) - siehe GenericAmmoDemandFor. Vorher fiel ein
                // Kolonist mit "irgendeine 7.62" komplett aus dieser Rechnung heraus.
                int slotCount = (lo?.Slots?.Where(s => s.thingDef == ammoDef).Sum(s => s.count) ?? 0)
                    + GenericAmmoDemandFor(lo, ammoDef);
                if (slotCount <= 0) continue;
                // Inventar UND Magazin (2026-08-30, MagazineAmmoCount) - sonst meldet die Nachschub-Rechnung eine
                // Fehlmenge, die CE nie holen wird.
                int carried = PawnCarriedCount(p, ammoDef);
                demandedFromStorage += Mathf.Max(0, slotCount - carried);
            }
            int available = storedCount - demandedFromStorage;
            if (available < 0) available = 0;
            ammoAvailCache[ammoDef] = new AmmoAvail { avail = available, stored = storedCount, demanded = demandedFromStorage };
            return available;
        }

        // Same 5-number breakdown shape as ComputeItemOwnership, but demand comes from a colonist's LOADOUT
        // ammo slots (main weapon + sidearms, ComputeAmmoAvailability's own domain) rather than Equipment
        // Pack slots (ComputeItemOwnership's domain) - the source Dialog_AssignLoadouts' own Ammo cell
        // actually cares about. Mirrors GetWeaponInventory's own empire-scan-with-1s-TTL-cache shape so a
        // rich per-item tooltip built from this doesn't reintroduce the "full empire rescan every frame per
        // row" lag Patch_LoadoutTooltip.ComputeAvailability's own cache was added to fix.
        private struct AmmoOwn { public int carriedOnly, carriedAndAssigned, assignedInStore, freeStored, inProduction, demand; }
        private static readonly Dictionary<ThingDef, AmmoOwn> ammoOwnCache = new Dictionary<ThingDef, AmmoOwn>();
        // Time.frameCount, not a real-time TTL - see ComputeItemOwnership's own comment (2026-08-01).
        private static int ammoOwnCacheFrame = -1;

        // Explicit invalidation for a caller that mutates ammo state and needs its OWN very next read
        // (same frame) to see it - e.g. Dialog_ChangeAmmo.cs toggling a checkbox and immediately redrawing
        // the Availability bar for the same ammoDef. Without this, Unity firing multiple OnGUI passes
        // (Layout, Repaint, input) within one Time.frameCount means an earlier pass's cached read (from a
        // sibling row sharing the same ammoDef, e.g. a main weapon and a sidearm using the same caliber)
        // can still be served to a later pass in the SAME frame even after the mutation - Time.frameCount
        // alone doesn't change until the next real frame (2026-08-01).
        public static void InvalidateAmmoOwnership(ThingDef ammoDef = null)
        {
            if (ammoDef == null) { ammoAvailCache.Clear(); ammoOwnCache.Clear(); return; }
            ammoAvailCache.Remove(ammoDef);
            ammoOwnCache.Remove(ammoDef);
        }

        public static void GetAmmoOwnership(ThingDef ammoDef, out int carriedOnly, out int carriedAndAssigned, out int assignedInStore, out int freeStored, out int inProduction)
        {
            carriedOnly = carriedAndAssigned = assignedInStore = freeStored = inProduction = 0;
            if (ammoDef == null) return;
            if (Time.frameCount != ammoOwnCacheFrame)
            {
                ammoOwnCache.Clear();
                ammoOwnCacheFrame = Time.frameCount;
            }
            if (ammoOwnCache.TryGetValue(ammoDef, out AmmoOwn cached))
            {
                carriedOnly = cached.carriedOnly; carriedAndAssigned = cached.carriedAndAssigned;
                assignedInStore = cached.assignedInStore; freeStored = cached.freeStored; inProduction = cached.inProduction;
                return;
            }
            // + GroundStockCount - see ComputeAmmoAvailability's identical comment just above.
            int stored = IngredientStockCount(ammoDef) + GroundStockCount(ammoDef);
            inProduction = WeaponQueuedCount(ammoDef);
            // "Zugewiesen UND getragen" PRO KOLONIST verrechnet, nicht als Schnittmenge zweier
            // Koloniesummen (2026-08-31, im Test):
            // Ltz trug 5000 Schuss OHNE Beladung, Sachiko hatte 12 zugewiesen und trug nichts - die alte
            // min(carried, demand)-Rechnung buchte Ltz' Bestand als 'Assigned & carried: 12' und liess
            // 'Assigned, in storage' auf 0, waehrend der Kolonisten-Block derselben Zelle voellig richtig
            // 'still to fetch: 12' sagte. Zwei Bloecke, eine Frage, zwei Antworten.
            // ComputeItemOwnershipUncached hat dieselbe Korrektur seit 2026-08-18 (Buckler-Befund, siehe
            // dessen Kommentar (2)) - diese Schwesterstelle wurde damals nicht mitgezogen.
            int demand = 0, carried = 0, matched = 0, openDemand = 0;
            foreach (Pawn p in AllColonyPawns())
            {
                // Inventar UND Magazin (2026-08-30, MagazineAmmoCount).
                int pawnCarried = PawnCarriedCount(p, ammoDef);
                carried += pawnCarried;
                Loadout lo = p.GetLoadout();
                // Generische Slots zaehlen mit - identische Ergaenzung wie in ComputeAmmoAvailability, damit
                // die beiden Kernfunktionen nicht unterschiedliche Bedarfe melden (2026-08-09).
                int pawnDemand = (lo?.Slots?.Where(s => s.thingDef == ammoDef).Sum(s => s.count) ?? 0)
                    + GenericAmmoDemandFor(lo, ammoDef);
                demand += pawnDemand;
                matched += Mathf.Min(pawnCarried, pawnDemand);
                openDemand += Mathf.Max(0, pawnDemand - pawnCarried);
            }
            carriedAndAssigned = matched;
            carriedOnly = carried - matched;
            assignedInStore = Mathf.Min(stored, openDemand);
            freeStored = stored - assignedInStore;
            ammoOwnCache[ammoDef] = new AmmoOwn { carriedOnly = carriedOnly, carriedAndAssigned = carriedAndAssigned, assignedInStore = assignedInStore, freeStored = freeStored, inProduction = inProduction, demand = demand };
        }

        // EINE Antwort auf 'wie viele Stueck davon hat die Kolonie insgesamt zugewiesen' (2026-08-30) - fuer die
        // koloniweite Zeile 'Missing' im Tooltip. Drei Sorten, drei bestehende Rechnungen, keine vierte:
        // Waffen/Wurfwaffen ueber GetWeaponInventory, Munition ueber GetAmmoOwnership (beide zaehlen Stueck),
        // alles andere ueber ColonyWideAssignedNeed.
        public static int ColonyAssignedDemand(Def d)
        {
            if (d is ThingDef td)
            {
                if (td.IsWeapon || IsShieldApparel(td))
                {
                    GetWeaponInventory(td, out _, out _, out _, out int assigned);
                    return assigned;
                }
                if (IsAmmoDef(td))
                {
                    GetAmmoOwnership(td, out _, out _, out _, out _, out _);
                    return ammoOwnCache.TryGetValue(td, out AmmoOwn own) ? own.demand : 0;
                }
            }
            return ColonyWideAssignedNeed(d);
        }

        // One aggregated cost strip (default label "Total cost:", overridable so callers can stack several labeled
        // lines - e.g. one per component category, then a final "Total:" line). `costs` is the ingredient sum over
        // everything currently selected for production. labelW: how much space the leading label reserves before
        // the icon strip starts - callers with no label at all (label == "", a column header already says it) can
        // shrink this to reclaim that space instead of leaving it as dead padding.
        // A thin wrapper around DrawIngredientCell now (icon + "!" on shortage + the shared IngredientTooltip
        // wording, including the "on the ground" info line) - used to have its OWN separate icon+inline-colored-
        // number rendering with slightly different tooltip wording, which was the exact inconsistency the user
        // flagged ("harmonisiere die Materials-Anzeige überall, nutze denselben Code"). Now every ingredient/
        // material display in the mod is the SAME one function.
        // Default parameters must be compile-time constants, so a real .Translate() call can't sit in the
        // signature - this sentinel stands in for "caller didn't pass a label at all" (distinct from an
        // explicit null, which several callers pass on purpose to mean "no label line").
        private const string DefaultTotalCostLabelSentinel = "CER_DefaultTotalCostLabel";

        // showCounts standardmaessig AN (2026-08-12): die Leiste ist fast ueberall eine breite Summenzeile
        // ueber die volle Fensterbreite, und dort ist die Menge der ganze Punkt. Die beiden Aufrufer, die
        // damit eine schmale TABELLENSPALTE fuellen (Equipment-Pack-Wizard 160px, Pack-Auswahl 220px),
        // schalten sie aus: dort passen mit Zahlen nur noch drei bis vier Chips in die Spalte, der Rest
        // wuerde abgeschnitten - und dort haengt die Zahl ohnehin im Tooltip derselben Zelle.
        public static void DrawTotalCostLine(float x, float y, float width, Dictionary<ThingDef, int> costs, string label = DefaultTotalCostLabelSentinel, float labelW = 80f, bool showCounts = true)
        {
            if (label == DefaultTotalCostLabelSentinel) label = "CER_LoadoutUtility_TotalCostLabel".Translate();
            if (!string.IsNullOrEmpty(label))
            {
                GUI.color = new Color(1f, 1f, 1f, 0.8f);
                Widgets.Label(new Rect(x, y + 3f, 74f, 22f), label);
                GUI.color = Color.white;
            }
            List<ThingDefCountClass> ings = SortedCostList(costs);
            // Mit Mengen (2026-08-12): das hier ist die Gesamtsumme eines ganzen Vorgangs, und ohne Zahlen
            // war sie nicht nachrechenbar - siehe DrawIngredientCell.
            DrawIngredientCell(x + labelW, width - labelW, y, ings, 1, showCounts);
        }

        // Die sortierte Materialliste einer Kostenzeile, je Woerterbuch-OBJEKT gehalten (2026-09-06,
        // perf-packwizard-draw-time): OrderByDescending + Select + ToList lief je Zeile je Durchgang - ein
        // Dutzend Allokationen fuer eine Liste, die sich nur aendert, wenn der Aufrufer sein Woerterbuch
        // neu baut (der Pack-Assistent haelt es in costsCache und ersetzt es bei uiDirty). Gegen einen
        // Aufrufer, der DASSELBE Woerterbuch stillschweigend veraendert, sichert eine billige Pruefsumme
        // ueber Inhalt und Anzahl: stimmt sie nicht, wird neu gebaut. Schwache Schluessel, damit die
        // Woerterbuecher nicht am Cache haengen bleiben.
        private static readonly System.Runtime.CompilerServices.ConditionalWeakTable<Dictionary<ThingDef, int>, CostListEntry> costListCache
            = new System.Runtime.CompilerServices.ConditionalWeakTable<Dictionary<ThingDef, int>, CostListEntry>();
        private class CostListEntry { public long checksum; public List<ThingDefCountClass> list; }
        private static List<ThingDefCountClass> SortedCostList(Dictionary<ThingDef, int> costs)
        {
            if (costs == null || costs.Count == 0) return null;
            long sum = costs.Count;
            foreach (KeyValuePair<ThingDef, int> kv in costs) sum = sum * 31 + (kv.Key != null ? kv.Key.shortHash : 0) * 131 + kv.Value;
            if (costListCache.TryGetValue(costs, out CostListEntry e) && e.checksum == sum) return e.list;
            List<ThingDefCountClass> list = costs.OrderByDescending(kv => kv.Value).Select(kv => new ThingDefCountClass(kv.Key, kv.Value)).ToList();
            costListCache.Remove(costs);
            costListCache.Add(costs, new CostListEntry { checksum = sum, list = list });
            return list;
        }

        // Is this weapon already queued on a workbench (any production bill that produces it)? Used so "you don't own
        // it" warnings don't fire for gear that is already on its way.
        private static readonly Dictionary<ThingDef, bool> activeBillCache = new Dictionary<ThingDef, bool>();
        private static float activeBillCacheTime = -99f;

        public static bool WeaponHasActiveBill(ThingDef weapon)
        {
            if (weapon == null) return false;

            // Full bench scan per call, across every colony map - and the Squad Setup assign list asks per
            // visible row PER FRAME for its "in production" badge. A 1s TTL keeps freshly queued bills showing
            // up promptly (the windows that queue bills force-pause anyway) without the per-frame scan cost.
            if (Time.realtimeSinceStartup - activeBillCacheTime > 1f)
            {
                activeBillCache.Clear();
                activeBillCacheTime = Time.realtimeSinceStartup;
            }
            if (activeBillCache.TryGetValue(weapon, out bool cached)) return cached;

            bool result = false;
            foreach (Building_WorkTable bench in AllColonyBenches())
            {
                if (bench.billStack == null) continue;
                foreach (Bill b in bench.billStack.Bills)
                {
                    if (b is Bill_Production bp && bp.recipe != null && bp.recipe.products != null
                        && bp.recipe.products.Any(pr => pr.thingDef == weapon))
                    { result = true; break; }
                }
                if (result) break;
            }
            activeBillCache[weapon] = result;
            return result;
        }

        // The first workbench with an active bill producing this weapon, or null - lets a click on an
        // "in production" badge jump straight to the relevant bench's Bills tab instead of just stating the fact.
        // Can return a bench on a different map than the one currently in view - callers that open its Bills
        // tab must switch the active map first (see Dialog_ArmColony.JumpToBills).
        public static Building_WorkTable FindActiveBillBench(ThingDef weapon)
        {
            if (weapon == null) return null;
            foreach (Building_WorkTable bench in AllColonyBenches())
            {
                if (bench.billStack == null) continue;
                foreach (Bill b in bench.billStack.Bills)
                    if (b is Bill_Production bp && bp.recipe != null && bp.recipe.products != null
                        && bp.recipe.products.Any(pr => pr.thingDef == weapon))
                        return bench;
            }
            return null;
        }

        // Wie viele Stueck davon NOCH ENTSTEHEN, ueber alle Werkbaenke aller Kolonie-Karten.
        //
        // "Noch entstehen", nicht "steht in der Auftragszahl" - das war der Fehler (2026-08-09, zweimal
        // unabhaengig gefunden). Bei einem "mache X mal"-Auftrag ist repeatCount tatsaechlich die Restmenge,
        // bei einem "stelle her BIS X im Lager"-Auftrag ist targetCount aber das ENDZIEL, und davon ist der
        // vorhandene Bestand schon abgedeckt. Konkret: 400 Schuss im Lager, ein Bill "bis 500", Bedarf 600 ->
        // gerechnet wurde 400 vorhanden + 500 in Produktion = 900, also Fehlmenge 0 und gruener Balken.
        // Tatsaechlich stoppt die Werkbank bei 500 und die Kolonie steht 100 Schuss zu knapp da.
        // Das traf jede Anzeige, in der irgendwo "in Produktion" steht, und immer in dieselbe Richtung: zu
        // optimistisch. Alle unsere Munitions-Bills sind TargetCount, Waffen-Bills sind RepeatCount - bei
        // Waffen stimmte die alte Formel also, bei Munition nie.
        //
        // Der Bestand wird pro Aufruf EINMAL bestimmt und ueber alle Bills desselben Defs geteilt: zwei
        // "bis 500"-Bills nebeneinander bedeuten nicht zweimal 500 minus Bestand, sie zielen beide auf
        // dasselbe Lager. Deshalb Max ueber die Ziele statt Summe der Einzelreste.
            // 1s-Cache wie beim Zwilling activeBillCache direkt darueber (2026-08-15, eine Messung nannte
            // diese Funktion ausdruecklich als ungecacht). Sie laeuft ueber ALLE Werkbaenke
        // ALLER Karten und filtert deren ganze Auftragsstapel - und sie steht in Zeichenpfaden, die pro
        // Zeile pro Bild laufen: die Waffenuebersicht (Dialog_GenerateLoadouts) tut das seit jeher, und
        // seit heute auch der Verfuegbarkeitsbalken im Zweitwaffen-Popup, wo die Vorgaengeranzeige noch
        // ueber einen gecachten Pfad ging. Ohne diesen Cache waere das ein Rueckschritt gewesen.
        // Dieselbe Begruendung wie ueberall hier: die Fenster, die das zeichnen, pausieren das Spiel,
        // eine Sekunde verliert also nichts Sichtbares - und ein frisch angelegter Auftrag erscheint
        // spaetestens im naechsten Takt.
        private static readonly Dictionary<ThingDef, int> weaponQueuedCache = new Dictionary<ThingDef, int>();
        private static float weaponQueuedCacheTime = -99f;

        public static int WeaponQueuedCount(ThingDef weapon)
        {
            if (weapon == null) return 0;
            if (Time.realtimeSinceStartup - weaponQueuedCacheTime > 1f)
            {
                weaponQueuedCache.Clear();
                weaponQueuedCacheTime = Time.realtimeSinceStartup;
            }
            if (weaponQueuedCache.TryGetValue(weapon, out int cachedQueued)) return cachedQueued;
            List<Bill_Production> bills = new List<Bill_Production>();
            foreach (Building_WorkTable bench in AllColonyBenches())
            {
                if (bench.billStack == null) continue;
                foreach (Bill_Production bp in bench.billStack.Bills.OfType<Bill_Production>())
                    if (bp.recipe != null && bp.recipe.products != null && bp.recipe.products.Any(pr => pr.thingDef == weapon))
                        bills.Add(bp);
            }
            int queued = QueuedFromBills(bills, weapon);
            weaponQueuedCache[weapon] = queued;
            return queued;
        }

        // Die EINE Regel, wie viel ein Auftragsstapel noch WIRKLICH herhergibt. Vorher stand sie dreimal im
        // Mod, und zwei der drei Kopien waren die alte, zu optimistische Fassung (2026-08-15, Review-Befund):
        // dort galt schlicht `RepeatCount ? repeatCount : targetCount`. Fehlszenario aus dem Bericht: 45
        // Medizin im Lager und ein "bis du 50 hast"-Auftrag meldeten 50 in Produktion - die Zeile las sich als
        // gedeckt (45+50), waehrend die Werkbank nur 5 herstellt. Die Munitionszeilen im SELBEN Fenster
        // rechneten daneben richtig und sagten 5.
        //
        // - "mache X mal": echte Restmengen, die sich addieren.
        // - "bis X im Lager": nur das HOECHSTE Ziel zaehlt (zwei solche Auftraege ueber 50 machen zusammen
        //   nicht 100), und davon geht ab, was schon dasteht.
        // - "endlos": traegt gar nichts bei. Sein targetCount ist ein toter Vorgabewert (10) und hat mit dem,
        //   was der Auftrag tut, nichts zu tun - er durfte bisher als Ziel mitrechnen und hat die Zahl in
        //   beide Richtungen verbogen. Ob ueberhaupt ein Auftrag laeuft, beantwortet WeaponHasActiveBill.
        public static int QueuedFromBills(IEnumerable<Bill_Production> bills, ThingDef product)
        {
            int repeatTotal = 0;
            int highestTarget = 0;
            bool anyTarget = false;
            if (bills != null)
                foreach (Bill_Production bp in bills)
                {
                    if (bp == null) continue;
                    if (bp.repeatMode == BillRepeatModeDefOf.RepeatCount) { repeatTotal += bp.repeatCount; continue; }
                    if (bp.repeatMode == BillRepeatModeDefOf.Forever) continue;
                    anyTarget = true;
                    if (bp.targetCount > highestTarget) highestTarget = bp.targetCount;
                }
            if (!anyTarget || product == null) return repeatTotal;
            // Derselbe Bestandsbegriff, den RimWorlds "bis X"-Regel selbst benutzt: was gelagert ist. Fuer
            // Waffen zusaetzlich ueber WeaponStoredCount, weil die auch in Staendern und Regalen liegen und
            // IngredientStockCount die dort nicht sieht (siehe dessen eigenen Kommentar).
            // StoredStockCount im Nicht-Waffen-Zweig (2026-08-18): ein Produkt, das RimWorld nicht als
            // Rohstoff fuehrt (Kleidung, Schilde), zaehlte hier als gar nicht vorhanden - und eine
            // "bis X"-Regel haette dann immer wieder nachproduziert.
            int inStock = product.IsWeapon ? WeaponStoredCount(product) : StoredStockCount(product);
            return repeatTotal + Mathf.Max(0, highestTarget - inStock);
        }

        // Das hoechste "stelle her BIS X im Lager"-Ziel, das aktuell irgendwo fuer diesen Def laeuft (0 wenn
        // es keinen solchen Auftrag gibt). Beantwortet die Frage "reicht der vorhandene Auftrag ueberhaupt",
        // die vorher gar nicht gestellt wurde - dort galt ein reines "existiert irgendein Auftrag" (2026-08-09).
        // Bewusst das Ziel und NICHT die Restmenge wie in WeaponQueuedCount: verglichen wird gegen den
        // Gesamtbedarf, und das Ziel ist genau die Zahl, die am Ende im Lager stehen wird.
        // RepeatCount-Auftraege ("mache 10 Stueck") bleiben aussen vor - die halten keinen Lagerstand, aus
        // ihnen laesst sich kein "reicht das dauerhaft" ableiten.
        public static int HighestBillTargetFor(ThingDef def)
        {
            if (def == null) return 0;
            int best = 0;
            foreach (Building_WorkTable bench in AllColonyBenches())
            {
                if (bench.billStack == null) continue;
                foreach (Bill_Production bp in bench.billStack.Bills.OfType<Bill_Production>())
                {
                    if (bp.recipe?.products == null || !bp.recipe.products.Any(pr => pr.thingDef == def)) continue;
                    if (bp.repeatMode == BillRepeatModeDefOf.RepeatCount) continue;
                    if (bp.targetCount > best) best = bp.targetCount;
                }
            }
            return best;
        }

        // Same scan as WeaponQueuedCount, but also returns WHERE - one "<bench label> (xN)" line per bench
        // with an active bill (2026-08-01: the obsolete-bills delete-preview named WHAT was
        // running for nothing but not how much or on which bench, so there was nothing to sanity-check the
        // deletion against). Bench labels are disambiguated with BenchId the same way every other bench list
        // in this mod already does, so "Fabrication bench #2 (x50)" reads unambiguously when several exist.
        public static int BuildProductionDetail(ThingDef def, out List<string> detail)
        {
            detail = new List<string>();
            int total = 0;
            if (def == null) return 0;
            List<Building_WorkTable> benches = AllColonyBenches().ToList();
            foreach (Building_WorkTable bench in benches)
            {
                if (bench.billStack == null) continue;
                foreach (Bill_Production bp in bench.billStack.Bills.OfType<Bill_Production>())
                {
                    if (bp.recipe == null || bp.recipe.products == null || !bp.recipe.products.Any(pr => pr.thingDef == def)) continue;
                    int cnt = bp.repeatMode == BillRepeatModeDefOf.RepeatCount ? bp.repeatCount : bp.targetCount;
                    total += cnt;
                    detail.Add(bench.def.LabelCap + BenchId(bench, benches) + " (x" + cnt + ")");
                }
            }
            return total;
        }

        // Unity's legacy rich text (which every Widgets.Label in this mod goes through) has no <s>/<u> tag
        // support at all - an unrecognized tag renders as literal characters instead of being interpreted or
        // stripped, which is why a translated string containing <s>...</s> showed the raw tag text instead of
        // a strikethrough line (2026-08-01). True strikethrough needs a manually drawn line
        // over the label instead - this is the one shared place that does it, so every delete-preview in the
        // mod draws the same way (strikethrough is reserved exclusively for delete-preview).
        public static void DrawStrikethroughLabel(Rect rect, string label)
        {
            Widgets.Label(rect, label);
            Vector2 size = Text.CalcSize(label);
            float lineY = rect.y + rect.height / 2f;
            Widgets.DrawLineHorizontal(rect.x, lineY, Mathf.Min(size.x, rect.width));
        }

        // "#n" disambiguating id for a bench when several of its def are present in `benches`; "" when unique.
        public static string BenchId(Building_WorkTable bench, List<Building_WorkTable> benches)
        {
            if (bench == null || benches == null) return "";
            if (benches.Count(b => b.def == bench.def) <= 1) return "";
            return "#" + (benches.Where(b => b.def == bench.def).ToList().IndexOf(bench) + 1);
        }

        // One-line-per-ingredient tooltip, e.g. "Chemfuel x10 (not in storage)". Non-breaking spaces keep each entry
        // intact so a line never wraps mid-name.
        public static string IngredientTooltip(List<ThingDefCountClass> ings, int mult = 1)
        {
            if (ings == null || ings.Count == 0) return "";
            return string.Join("\n", ings.Select(g =>
            {
                int need = g.count * mult;
                string qty = (mult > 1 ? "CER_LoadoutUtility_IngredientQtyMulti".Translate(need.ToString(), mult.ToString(), g.count.ToString()) : "CER_LoadoutUtility_IngredientQtySingle".Translate(g.count.ToString())).ToString();
                int have = IngredientStockCount(g.thingDef);
                int ground = GroundStockCount(g.thingDef);
                string groundInfo = ground > 0 ? "CER_LoadoutUtility_GroundInfo".Translate(ground.ToString()).ToString() : "";
                return g.thingDef.LabelCap + qty
                    + (have >= need
                        ? "CER_LoadoutUtility_InStockSuffix".Translate(have.ToString(), groundInfo)
                        : "CER_LoadoutUtility_InStockSuffixShort".Translate(have.ToString(), groundInfo));
            }).ToArray());
        }

        // The canonical ingredient cell: sorted resource icons, a "!" over any resource you don't have enough of, and a single
        // whole-cell tooltip listing every ingredient. Reused by the bill rows and every popup for a uniform look.
        // Returns the x just past the last icon so callers can append (e.g. the "+mat." marker).
        // showCounts (2026-08-12): schreibt die Menge neben jedes Symbol. Aus in
        // den engen Tabellenzellen (dort stehen 150px fuer bis zu sechs Materialien, und die Menge steht
        // ohnehin schon in der Batch-Spalte daneben), AN in der breiten Gesamtkosten-Leiste - dort war die
        // Zeile bis heute eine reine Symbolreihe, und "kostet Stahl und Komponenten" ohne jede Zahl laesst
        // sich mit nichts abgleichen. Genau daran ist ein Testlauf haengengeblieben.
        public static float DrawIngredientCell(float x, float w, float cy, List<ThingDefCountClass> ings, int mult = 1, bool showCounts = false)
        {
            if (ings == null || ings.Count == 0)
            {
                Color c0 = GUI.color;
                GUI.color = new Color(c0.r, c0.g, c0.b, 0.4f);
                Widgets.Label(new Rect(x, cy + 2f, w, 22f), "-");
                GUI.color = c0;
                return x;
            }
            float ix = x;
            float limit = x + w - 22f;
            GameFont prevFont = Text.Font;
            foreach (ThingDefCountClass ing in ings)
            {
                if (ix > limit) break;
                Rect ir = new Rect(ix, cy + 1f, 22f, 22f);
                Widgets.ThingIcon(ir, ing.thingDef);
                if (IngredientStockCount(ing.thingDef) < ing.count * mult)
                    DrawWarningBadge(new Rect(ir.xMax - 11f, ir.y - 6f, 16f, 16f), ColWarn);
                ix += 25f;
                if (showCounts)
                {
                    // Tiny font: die Zahl gehoert zum Symbol, sie soll die Reihe nicht dominieren - und sie
                    // muss schmal bleiben, damit eine Leiste mit sechs Materialien nicht auslaeuft.
                    Text.Font = GameFont.Tiny;
                    string qty = (ing.count * mult).ToString();
                    float qw = Text.CalcSize(qty).x + 4f;
                    Widgets.Label(new Rect(ix, cy + 4f, qw, 20f), qty);
                    Text.Font = prevFont;
                    ix += qw + 4f;
                }
            }
            // Lazy (2026-08-01, the whole Ammo Bills tab felt sluggish): IngredientTooltip
            // calls GroundStockCount per ingredient, which does a REAL per-Thing map scan (not a cached
            // counter lookup) - a plain-string TipRegion call forces that to run every frame for every row's
            // ingredient cell regardless of whether the mouse is anywhere near it. DrawIngredientCell is the
            // single shared ingredient-cell renderer for the whole mod (bill rows, every popup), so this one
            // fix covers all of them at once. The Func<string> overload only invokes the getter when
            // TipRegion's own Mouse.IsOver check already passed.
            // Schluessel aus dem INHALT, nicht aus der Listenreferenz (2026-08-12): Aufrufer wie
            // DrawTotalCostLine bauen ihre Liste in jedem Frame neu, und ein Referenz-Hash aendert sich dann
            // jeden Frame mit. Der Tooltip-Handler fuehrt seine aktiven Tipps per uniqueId - eine ID, die pro
            // Frame wechselt, legt dort in jedem Frame einen neuen Eintrag an, statt denselben Tipp am Leben
            // zu halten. Inhaltsgleiche Zellen teilen sich jetzt bewusst eine ID; sie zeigen ohnehin
            // denselben Text.
            int tipKey = 0x1ED6 ^ mult;
            foreach (ThingDefCountClass ing in ings)
                tipKey = tipKey * 31 + (ing.thingDef != null ? ing.thingDef.shortHash : 0) * 7 + ing.count;
            TooltipHandler.TipRegion(new Rect(x, cy, w, 24f), () => IngredientTooltip(ings, mult), tipKey);
            return ix;
        }

        // Compact workbench button: the bench's own icon (falls back to a wrench-less blank) plus a small "#n" id when
        // several benches of the same def exist. `id` is "" for a unique bench. The full name goes in the tooltip.
        // Returns true on click. Used everywhere a bench used to be a wide text button.
        public static bool BenchIconButton(Rect rect, Building_WorkTable bench, string id, string tooltip)
        {
            bool clicked = Widgets.ButtonText(rect, "");
            if (bench != null && bench.def != null)
            {
                float s = rect.height - 4f;
                Texture2D icon = bench.def.uiIcon;
                // Seitenverhaeltnis erhalten und die volle Knopfbreite nutzen (2026-08-13: der Munitions-Knopf
                // und der Werkbank-Knopf waren nicht breit genug, im Nachschub-Fenster).
                // Der Knopf war nicht das Problem, die Zeichnung war es: das Symbol ging in ein QUADRAT von
                // 20x20, und GUI.DrawTexture staucht ohne Weiteres auf die Zielgroesse. Werkbank-Symbole sind
                // aber breite Gebaeude (Ladebank, Schmiede) - gestaucht sehen sie genau so aus wie
                // beobachtet: an den Seiten beschnitten.
                // Widgets.DrawTextureFitted skaliert unter Wahrung des Verhaeltnisses und zentriert selbst;
                // ohne id-Text bekommt es die ganze Knopfbreite, mit id bleibt links Platz fuer das Symbol
                // und rechts fuer die Nummer.
                float idW = string.IsNullOrEmpty(id) ? 0f : Mathf.Max(16f, Text.CalcSize(id).x + 4f);
                Rect iconArea = new Rect(rect.x + 3f, rect.y + 2f, rect.width - 6f - idW, s);
                if (icon != null) Widgets.DrawTextureFitted(iconArea, icon, 1f);
                if (!string.IsNullOrEmpty(id))
                {
                    Text.Anchor = TextAnchor.MiddleLeft;
                    Widgets.Label(new Rect(iconArea.xMax + 2f, rect.y, idW, rect.height), id);
                    Text.Anchor = TextAnchor.UpperLeft;
                }
            }
            if (!string.IsNullOrEmpty(tooltip)) TooltipHandler.TipRegion(rect, tooltip);
            return clicked;
        }

        // Arm-my-Colony: remove colony bills that produce a real weapon, on every colony map, so the run
        // rebuilds them to match the current requirements. Scope (2026-08-01) - ManagedOnly only deletes
        // bills this mod's own tooling queued (LoadoutUtility.IsManagedBill); All deletes every bill for a
        // recognized weapon type regardless of who set it up (the original, only behavior before scope
        // existed) - including ones the player queued by hand. Returns how many bills were deleted.
        public static int DeleteAllWeaponBills(BillDeleteScope scope = BillDeleteScope.All)
        {
            int n = 0;
            if (scope == BillDeleteScope.None) return n;
            foreach (Building_WorkTable bench in AllColonyBenches())
            {
                if (bench.billStack == null) continue;
                foreach (Bill bill in bench.billStack.Bills.ToList())
                {
                    Bill_Production bp = bill as Bill_Production;
                    if (bp == null || bp.recipe == null || bp.recipe.products == null) continue;
                    if (scope == BillDeleteScope.ManagedOnly && !IsManagedBill(bp)) continue;
                    if (bp.recipe.products.Any(p => p.thingDef != null && p.thingDef.IsWeapon && !IsUtilityWeapon(p.thingDef)))
                    {
                        bench.billStack.Delete(bill);
                        n++;
                    }
                }
                RefreshBillsTab(bench);
            }
            return n;
        }

        // Dry-run counterparts to DeleteAllWeaponBills/Dialog_GenerateAmmoBills.ArmDeleteAllAmmoBills - same
        // scope filter, no deletion. Used by Dialog_ArmColony's table to show a live "(N)" count next to each
        // scope choice before the player commits to Apply (2026-07-31 - picking a scope blind
        // with no idea how many bills it'll actually touch was the problem).
        public static int CountWeaponBillsForScope(BillDeleteScope scope)
        {
            if (scope == BillDeleteScope.None) return 0;
            int n = 0;
            foreach (Building_WorkTable bench in AllColonyBenches())
            {
                if (bench.billStack == null) continue;
                foreach (Bill bill in bench.billStack.Bills)
                {
                    Bill_Production bp = bill as Bill_Production;
                    if (bp == null || bp.recipe == null || bp.recipe.products == null) continue;
                    if (scope == BillDeleteScope.ManagedOnly && !IsManagedBill(bp)) continue;
                    if (bp.recipe.products.Any(p => p.thingDef != null && p.thingDef.IsWeapon && !IsUtilityWeapon(p.thingDef))) n++;
                }
            }
            return n;
        }

        public static int CountAmmoBillsForScope(BillDeleteScope scope)
        {
            if (scope == BillDeleteScope.None) return 0;
            int n = 0;
            foreach (Building_WorkTable bench in AllColonyBenches())
            {
                if (bench.billStack == null) continue;
                foreach (Bill bill in bench.billStack.Bills)
                {
                    Bill_Production bp = bill as Bill_Production;
                    if (bp == null || bp.recipe?.products == null) continue;
                    if (scope == BillDeleteScope.ManagedOnly && !IsManagedBill(bp)) continue;
                    if (bp.recipe.products.Any(p => p.thingDef != null && IsAmmoDef(p.thingDef))) n++;
                }
            }
            return n;
        }

        // Total weapon demand (main + sidearm, one unit per pawn/role that wants it) under one of the two
        // scopes this mod already uses elsewhere (Assigned = real pawns' actual current gear; Created = every
        // managed loadout template that exists at all, regardless of whether anyone's using it right now).
        // Shared by the obsolete-bill detector below AND anything else that wants a scope-aware weapon
        // headcount (2026-08-01).
        public static Dictionary<ThingDef, int> ComputeWeaponDemand(bool includeCreated)
        {
            Dictionary<ThingDef, int> demand = new Dictionary<ThingDef, int>();
            void Add(ThingDef w) { if (w != null) demand[w] = (demand.TryGetValue(w, out int c) ? c : 0) + 1; }
            // JEDER Waffenslot, nicht nur die eine "Hauptwaffe" (2026-08-13, im Test aufgefallen:
            // Arm my Colony stellte Satchel charge x2 her, Mack trug sie wirklich als Zweitwaffe - und
            // eine Minute spaeter stand genau dieser Auftrag unter 'Obsolete bills').
            // Ursache: LoadoutMainAndSide waehlt bewusst NIE eine Granate, ein Schild oder eine
            // Hilfswaffe als "Hauptwaffe" (siehe dessen Kommentar). Eine Zweitwaffen-Vorlage, die genau so
            // eine enthaelt - der Normalfall fuer Wurfsprengstoff -, lieferte hier also `null`, und damit
            // stand ihr Auftrag ohne jeden Bedarf da. Ein Klick auf "Supply selected" haette den Auftrag
            // geloescht, den derselbe Durchgang zwei Schritte vorher angelegt hat.
            // Dieselbe Korrektur hat ComputeMissingForCreatedLoadouts am 2026-08-01 schon bekommen; die
            // Obsolet-Seite ist damals nicht mitgezogen worden - dieselbe Frage, zwei Rechnungen.
            // Praedikat identisch zu UnneededWeaponBillCandidates (IsWeapon, keine Hilfswaffen), damit
            // Bedarfs- und Kandidatenseite dieselbe Menge betrachten.
            void AddAllWeapons(Loadout lo)
            {
                if (lo?.Slots == null) return;
                foreach (LoadoutSlot s in lo.Slots)
                    if (s.thingDef != null && s.thingDef.IsWeapon && !IsUtilityWeapon(s.thingDef)) Add(s.thingDef);
            }

            if (!includeCreated)
            {
                foreach (Pawn p in AllColonyPawns())
                {
                    Loadout raw = p.GetLoadout();
                    if (raw == null || raw.defaultLoadout) continue;
                    Loadout display = AmmoAssignmentUtility.DisplayLoadout(raw);
                    if (display == null) continue;
                    // Ohne Hauptwaffe (None) zaehlen die Zweitwaffen trotzdem (2026-08-25) - siehe
                    // ScanMissingAssignedWeapons, dieselbe Luecke.
                    if (!display.defaultLoadout) AddAllWeapons(display);
                    foreach (Loadout sideLoadout in SidearmAssignmentUtility.GetPawnSidearms(p) ?? new List<Loadout>())
                        AddAllWeapons(sideLoadout);
                }
            }
            else
            {
                foreach (Loadout lo in LoadoutManager.Loadouts)
                {
                    if (lo == null || lo.defaultLoadout || AmmoAssignmentUtility.IsAmmoLink(lo)
                        || EquipmentPackUtility.IsEquipmentPackLoadout(lo)) continue;
                    AddAllWeapons(lo);
                }
            }
            return demand;
        }

        // Weapon/ammo defs with an active production bill that current demand (under the given scope) no
        // longer wants at all - production that kept running after a loadout was edited/deleted or a
        // colonist reassigned. Standalone versions of the same concept Dialog_GenerateAmmoBills' own
        // UnneededAmmoEntries already computes for its own tab, usable from anywhere (2026-08-01, closes the
        // Supply Loadouts popup's own cleanup-suggestion gap for the NORMAL automatic close-of-window flow,
        // not just the one manual Ammo Bills tab button that mechanism was originally wired to alone).
        // Resolves a generic ammo demand dict (LoadoutGenericDef -> count) down to the SAME concrete default
        // pick Dialog_SupplyLoadouts.cs's own generic-ammo rows already use (GetWeaponForGenericAmmo ->
        // GetAmmoTypesFor -> PickDefaultAmmo among researched variants), and merges the result into an
        // existing specifics dict. 2026-08-02 fix: a pawn still on the unresolved generic "Any ammo for X"
        // placeholder used to be invisible to every specifics-only demand check - UnneededAmmoBillCandidates
        // is one of them (with both AP and FMJ bills active and nobody's SPECIFIC slot
        // demanding either anymore, only AP got flagged obsolete because some pawn's still-generic slot for
        // that caliber happens to default-resolve to FMJ, silently keeping it "needed" while looking
        // unrelated to the generic pawn that's actually the reason).
        public static void MergeResolvedGenericAmmoDemand(Dictionary<LoadoutGenericDef, int> generics, Dictionary<ThingDef, int> specifics)
        {
            if (generics == null || specifics == null) return;
            foreach (var kv in generics)
            {
                ThingDef gun = GetWeaponForGenericAmmo(kv.Key);
                List<ThingDef> researchedFamily = GetAmmoTypesFor(gun).Where(IsAmmoResearched).ToList();
                if (researchedFamily.Count == 0) continue;
                if (!(PickDefaultAmmo(researchedFamily.Cast<Def>().ToList()) is ThingDef resolved)) continue;
                specifics[resolved] = (specifics.TryGetValue(resolved, out int c) ? c : 0) + kv.Value;
            }
        }

        // Every ammo variant a generic "Any ammo for X" slot would ACCEPT - as opposed to the single variant
        // MergeResolvedGenericAmmoDemand above resolves it to.
        //
        // The two are deliberately different answers to two different questions, and conflating them was the
        // bug (2026-08-09, with a screenshot: a colonist on a generic .30-06 slot, and the
        // Supply popup offered to delete the .30-06 AP and .30-06 HP bills as "obsolete" while keeping FMJ):
        //   - "how much should I PRODUCE" has to pick exactly one variant, or there is no bill target at all.
        //     That is MergeResolvedGenericAmmoDemand, and PickDefaultAmmo picking FMJ there is correct.
        //   - "is this existing bill still WANTED by anybody" must not: the pawn will happily take AP or HP,
        //     so none of the siblings is obsolete. Answering that one with the single resolved pick declares
        //     every other variant of the caliber dead.
        // Deliberately NOT filtered by IsAmmoResearched, unlike the resolution path: this set only ever
        // PROTECTS a bill from being offered for deletion, and the cost of the two error directions is wildly
        // asymmetric - protecting one bill too many leaves a bill the player can still delete by hand, while
        // protecting one too few is a pre-ticked checkbox away from destroying their production setup.
        // Auf welche EINE Sorte ein generischer Slot heute hinauslaeuft - dieselbe Aufloesung wie
        // MergeResolvedGenericAmmoDemand, nur einzeln abfragbar und gemerkt (2026-08-09). Gebraucht von den
        // beiden gecachten Bestandsfunktionen weiter unten, die pro Frame ueber alle Kolonisten laufen: ohne
        // Memo wuerde dort pro Pawn und Slot die ganze Munitionsfamilie neu aufgeloest.
        // Kurze Lebensdauer, weil abgeschlossene Forschung die Antwort aendert - dieselbe Schranke wie beim
        // benachbarten IsAmmoResearched, dessen Ergebnis hier ohnehin einfliesst.
        private static readonly Dictionary<LoadoutGenericDef, ThingDef> genericAmmoResolveCache = new Dictionary<LoadoutGenericDef, ThingDef>();
        private static float genericAmmoResolveCacheTime = -99f;

        public static ThingDef ResolveGenericAmmo(LoadoutGenericDef gd)
        {
            if (gd == null) return null;
            if (Time.realtimeSinceStartup - genericAmmoResolveCacheTime > 5f)
            {
                genericAmmoResolveCache.Clear();
                genericAmmoResolveCacheTime = Time.realtimeSinceStartup;
            }
            if (genericAmmoResolveCache.TryGetValue(gd, out ThingDef cached)) return cached;
            ThingDef gun = GetWeaponForGenericAmmo(gd);
            List<ThingDef> fam = GetAmmoTypesFor(gun).Where(IsAmmoResearched).ToList();
            ThingDef resolved = fam.Count > 0 ? PickDefaultAmmo(fam.Cast<Def>().ToList()) as ThingDef : null;
            genericAmmoResolveCache[gd] = resolved;
            return resolved;
        }

        // Wie viele Schuss dieser konkreten Sorte ein Loadout ueber GENERISCHE Slots verlangt. Ohne das war
        // ein Pawn, der "irgendeine 7.62" traegt, fuer die zentrale Bestandsrechnung unsichtbar: sein Bedarf
        // tauchte nirgends auf, und der Mod tat so, als brauche niemand die Munition (2026-08-09).
        private static int GenericAmmoDemandFor(Loadout lo, ThingDef ammoDef)
        {
            if (lo?.Slots == null || ammoDef == null) return 0;
            int n = 0;
            foreach (LoadoutSlot s in lo.Slots)
            {
                if (s.genericDef == null || s.genericDef.defName == null) continue;
                if (!s.genericDef.defName.StartsWith(GenericAmmoPrefix)) continue;
                if (ResolveGenericAmmo(s.genericDef) == ammoDef) n += s.count;
            }
            return n;
        }

        public static void CollectGenericAmmoFamilies(IEnumerable<LoadoutGenericDef> generics, HashSet<ThingDef> into)
        {
            if (generics == null || into == null) return;
            foreach (LoadoutGenericDef g in generics)
            {
                if (g == null) continue;
                ThingDef gun = GetWeaponForGenericAmmo(g);
                foreach (ThingDef variant in GetAmmoTypesFor(gun)) if (variant != null) into.Add(variant);
            }
        }

        public static List<ThingDef> UnneededAmmoBillCandidates(bool includeCreated = false)
        {
            Dictionary<ThingDef, int> demand;
            // Bills a generic slot keeps alive without being the resolved pick - see CollectGenericAmmoFamilies
            // for why "obsolete?" and "how much to produce?" must not share one answer here.
            HashSet<ThingDef> genericallyCovered = new HashSet<ThingDef>();
            if (!includeCreated)
            {
                ComputeAmmoNeedForAssignedPawns(AllColonyPawns().ToList(), out var generics, out demand, out _);
                MergeResolvedGenericAmmoDemand(generics, demand);
                CollectGenericAmmoFamilies(generics.Keys, genericallyCovered);
            }
            else
            {
                demand = new Dictionary<ThingDef, int>();
                Dictionary<LoadoutGenericDef, int> genericDemand = new Dictionary<LoadoutGenericDef, int>();
                foreach (Loadout lo in LoadoutManager.Loadouts)
                {
                    if (lo == null || lo.defaultLoadout || AmmoAssignmentUtility.IsAmmoLink(lo)) continue;
                    foreach (LoadoutSlot s in lo.OwnSlots ?? new List<LoadoutSlot>())
                    {
                        if (s.thingDef != null && IsAmmoDef(s.thingDef))
                            demand[s.thingDef] = (demand.TryGetValue(s.thingDef, out int c) ? c : 0) + s.count;
                        else if (s.genericDef != null && s.genericDef.defName != null && s.genericDef.defName.StartsWith(GenericAmmoPrefix))
                            genericDemand[s.genericDef] = (genericDemand.TryGetValue(s.genericDef, out int c) ? c : 0) + s.count;
                    }
                }
                MergeResolvedGenericAmmoDemand(genericDemand, demand);
                CollectGenericAmmoFamilies(genericDemand.Keys, genericallyCovered);
            }
            List<ThingDef> result = new List<ThingDef>();
            foreach (Building_WorkTable bench in AllColonyBenches())
            {
                if (bench.billStack == null) continue;
                foreach (Bill bill in bench.billStack.Bills)
                {
                    Bill_Production bp = bill as Bill_Production;
                    if (bp?.recipe?.products == null) continue;
                    foreach (var prod in bp.recipe.products)
                        // !IsSelfAmmoWeapon (2026-08-14, im Test aufgefallen): Wurfsprengstoff und
                        // Wurfwaffen (Satchel charge, Granaten, Speere) sind ThingClass AmmoThing, IsAmmoDef
                        // sagt also ja - die Bedarfsseite hier daneben schliesst sie aber ausdruecklich aus
                        // (ComputeAmmoNeedForLoadouts, siehe die Begruendung dort: sie gehoeren dem Waffen-
                        // Bereich, der echten Besitz gegenrechnet). Kandidaten- und Bedarfsseite betrachteten
                        // damit verschiedene Mengen, und das Ergebnis war ein Widerspruch im SELBEN Fenster:
                        // "satchel charge" stand als Bedarf mit +5 Batches drin und gleichzeitig angehakt
                        // unter "niemand braucht das mehr" - ein Klick auf Bestaetigen haette genau den
                        // Auftrag geloescht, den dasselbe Fenster zwei Zeilen darueber anlegen wollte.
                        // Der Waffen-Bereich sieht sie weiterhin: UnneededWeaponBillCandidates rechnet gegen
                        // ComputeWeaponDemand, und das zaehlt Zweitwaffen ausdruecklich mit.
                        if (prod.thingDef != null && IsRealAmmoDef(prod.thingDef)
                            && !demand.ContainsKey(prod.thingDef)
                            && !genericallyCovered.Contains(prod.thingDef) && !result.Contains(prod.thingDef))
                            result.Add(prod.thingDef);
                }
            }
            return result;
        }

        public static List<ThingDef> UnneededWeaponBillCandidates(bool includeCreated = false)
        {
            Dictionary<ThingDef, int> demand = ComputeWeaponDemand(includeCreated);
            List<ThingDef> result = new List<ThingDef>();
            foreach (Building_WorkTable bench in AllColonyBenches())
            {
                if (bench.billStack == null) continue;
                foreach (Bill bill in bench.billStack.Bills)
                {
                    Bill_Production bp = bill as Bill_Production;
                    if (bp?.recipe?.products == null) continue;
                    foreach (var prod in bp.recipe.products)
                        if (prod.thingDef != null && prod.thingDef.IsWeapon && !IsUtilityWeapon(prod.thingDef)
                            && !demand.ContainsKey(prod.thingDef) && !result.Contains(prod.thingDef))
                            result.Add(prod.thingDef);
                }
            }
            return result;
        }

        // Deletes every active bill (any bench, any map) producing one of the given defs, regardless of which
        // list (ammo or weapon) they came from - a Bill_Production's own recipe.products is what actually
        // decides, not which caller asked. Returns how many bills were deleted.
        public static int PruneBillsFor(IEnumerable<ThingDef> only)
        {
            HashSet<ThingDef> set = only as HashSet<ThingDef> ?? new HashSet<ThingDef>(only);
            if (set.Count == 0) return 0;
            int n = 0;
            HashSet<Building_WorkTable> touched = new HashSet<Building_WorkTable>();
            foreach (Building_WorkTable bench in AllColonyBenches())
            {
                if (bench.billStack == null) continue;
                foreach (Bill bill in bench.billStack.Bills.ToList())
                {
                    Bill_Production bp = bill as Bill_Production;
                    if (bp?.recipe?.products == null) continue;
                    if (bp.recipe.products.Any(p => p.thingDef != null && set.Contains(p.thingDef)))
                    {
                        bench.billStack.Delete(bill);
                        touched.Add(bench);
                        n++;
                    }
                }
            }
            foreach (Building_WorkTable b in touched) RefreshBillsTab(b);
            return n;
        }

        // ONE shared attachment point for "does this Supply Loadouts popup also offer to prune obsolete
        // bills" - every construction site that wants the feature calls this on its own Request instead of
        // repeating the same 4-line UnneededAmmoBillCandidates/UnneededWeaponBillCandidates/PruneBillsFor
        // wiring inline (2026-08-01: the popup reached via Dialog_AssignLoadouts'
        // OpenProducePopup - by far the most commonly seen instance, fired every single Assign Loadouts
        // apply - never got this at all, only the separate, far rarer close-of-window
        // CheckAndOpenMissingProductionPopup did; a second independent copy of the same wiring would have
        // been exactly the kind of duplication already flagged
        // elsewhere, not a real fix).
        // (Seit 2026-09-07 gibt es den Bereichs-Schalter nicht mehr; includeCreated bleibt als Vorgabe false.)
        // WER EINEN BEREICHS-SCHALTER ZEICHNET, MUSS SEINEN STAND HIER DURCHREICHEN (2026-08-14). Der
        // Vorgaengerkommentar sagte "includeCreated defaults to false, matching every existing caller's own
        // scope everywhere else - Created-scope cleanup stays a Bill Overview tab-only concept": das stimmte
        // am 2026-08-01 und ist seit dem 2026-08-01 abends falsch, weil ZWEI weitere Aufrufer den Schalter
        // seither selbst zeichnen (`showAmmoScopeToggle = true`). Beide nahmen die Vorgabe, ihre
        // Ueberfluessig-Liste rechnete also immer in der ZUWEISUNGS-Sicht, egal was der Schalter darueber
        // sagte. Fehlszenario, im Test am 14.08. belegt: Schalter auf "Consider created loadouts",
        // das erzeugte Loadout "W| Saiga 12K" steht in derselben Liste - und ihr Auftrag stand trotzdem unter
        // "niemand braucht das mehr". Seit die Haken dort vorbelegt sind, ist das ein Klick vom Datenverlust.
        public static void AttachObsoleteCleanup(Dialog_SupplyLoadouts.Request req, bool includeCreated = false)
        {
            req.obsoleteAmmoCandidates = UnneededAmmoBillCandidates(includeCreated);
            req.onPruneObsoleteAmmo = approved => PruneBillsFor(approved);
            req.obsoleteWeaponCandidates = UnneededWeaponBillCandidates(includeCreated);
            req.onPruneObsoleteWeapons = approved => PruneBillsFor(approved);
        }

        // Same "how many would this scope actually touch" dry-run pattern as the two bill counters above,
        // for Dialog_ArmColony's "Delete weapon loadouts"/"Delete equipment packs" rows (2026-08-01 upgrade
        // from a blunt bool to the same None/ManagedOnly/All scope bills already got).
        public static int CountWeaponLoadoutsForScope(BillDeleteScope scope)
        {
            if (scope == BillDeleteScope.None) return 0;
            // Wortgleich mit der Loeschung in Dialog_ArmColony (2026-08-25): die spart ein
            // "[Role] X"-Loadout ohne Rollenkarte aus (IsRoleLabel), der Zaehler tat es nicht - seit die
            // Alles-Spalte "erzeugt+handgebaut" zeigt, stand so ein Waisen-Loadout als "+1 handgebaut" da,
            // das der Lauf dann doch nicht loeschte.
            return LoadoutManager.Loadouts.Count(x => x != null && !x.defaultLoadout
                && !EquipmentPackUtility.IsEquipmentPackLoadout(x) && IsPlainWeaponLoadout(x) && !RoleUtility.IsRoleLabel(x.label)
                && (scope == BillDeleteScope.All || IsGeneratedTemplate(x)));
        }

        public static int CountEquipmentPacksForScope(BillDeleteScope scope)
        {
            if (scope == BillDeleteScope.None) return 0;
            return EquipmentPackUtility.AllEquipmentPacks().Count(x => scope == BillDeleteScope.All || IsGeneratedPack(x));
        }

        // Empire-wide inventory of a weapon: stored (spawned on any colony map), carried (equipped + in colonist
        // inventories, any map or caravan), total = stored + carried, and assigned = how many colonists have it
        // in their loadout. Used by the Weapons Overview.
        private struct WeaponInv { public int total, carried, stored, assigned; }
        private static readonly Dictionary<ThingDef, WeaponInv> weaponInvCache = new Dictionary<ThingDef, WeaponInv>();
        private static float weaponInvCacheTime = -99f;

        public static void GetWeaponInventory(ThingDef w, out int total, out int carried, out int stored, out int assigned)
        {
            total = carried = stored = assigned = 0;
            if (w == null) return;

            // Full empire scan per call - and the Bill Overview asks per weapon PER FRAME under its default
            // "Available weapons" filter. The tab windows force-pause the game, so a 1s TTL loses nothing visible.
            if (Time.realtimeSinceStartup - weaponInvCacheTime > 1f)
            {
                weaponInvCache.Clear();
                weaponInvCacheTime = Time.realtimeSinceStartup;
            }
            if (weaponInvCache.TryGetValue(w, out WeaponInv inv))
            {
                total = inv.total; carried = inv.carried; stored = inv.stored; assigned = inv.assigned;
                return;
            }

            // Thing-instance dedup, same pattern as BuildOwnedWeaponCounts/AvailableWeaponsForPawn (2026-08-01,
            // this function was the one remaining GetWeaponInventory-shaped scan without it -
            // a weapon can show up both in the map lister and in a pawn's equipment/inventory).
            HashSet<Thing> seen = new HashSet<Thing>();
            foreach (Map map in PlayerMaps())
                // Forbidden loot counts (2026-08-09, see IsMarkedDoNotPickUp) - the "!" marker and the
                // tooltip's own section are what tell the player part of this still has to be released.
                foreach (Thing t in map.listerThings.ThingsOfDef(w)) if (!IsHiddenInFog(t) && seen.Add(t)) stored += t.stackCount;
            foreach (Pawn p in AllColonyPawns())
            {
                if (p.equipment != null && p.equipment.Primary != null && p.equipment.Primary.def == w && seen.Add(p.equipment.Primary)) carried++;
                if (p.inventory != null && p.inventory.innerContainer != null)
                    foreach (Thing t in p.inventory.innerContainer) if (t.def == w && seen.Add(t)) carried += t.stackCount;
                // CannotFight excluded (2026-08-01, found while tracing a Supply Loadouts contradiction) -
                // matches BuildWeaponLocationTooltip's own "Assigned to:" list, which already filters the same
                // way. A pawn who can never actually receive/use a weapon (baby, incapacitated, ...) still
                // showing up here inflated "assigned" demand without ever appearing in that list, so the two
                // could silently disagree on how many pawns "really" need this weapon.
                // Zweitwaffen-Granaten stehen seit 2026-08-30 wieder im Slot-Verbund des Ankers (Patch_Loadout_MultiParentSlots.
                // IsRealAmmoSlot) - eine einzige Zaehlung reicht, keine zweite Liste daneben.
                // STUECK, nicht Traeger (2026-08-30: 'warum ist da jetzt eine gelb available,
                // wenn doch 2 schon assigned sind?'). Mack hatte VIER Satchel Charges zugewiesen, diese Zeile zaehlte
                // ihn als 1 - also galt nur 1 Stueck im Lager als ihm zugeteilt und der Rest als frei verfuegbar (gelb),
                // waehrend 'Assigned to: Mack x4' daneben die Stueckzahl nannte. Bei Waffen (ein Stueck je Kolonist)
                // aendert sich dadurch nichts, bei Zweitwaffen mit Menge sehr wohl.
                Loadout lo = p.GetLoadout();
                if (lo != null && !lo.defaultLoadout && lo.Slots != null && !CannotFight(p))
                    assigned += lo.Slots.Where(s => s.thingDef == w).Sum(s => Mathf.Max(1, s.count));
            }
            total = stored + carried;
            weaponInvCache[w] = new WeaponInv { total = total, carried = carried, stored = stored, assigned = assigned };
        }

        // Die Umrechnung von GetWeaponInventorys vier rohen Zahlen in die Abschnitte des
        // Verfuegbarkeitsbalkens. Stand am 2026-08-15 als woertliche Kopie an VIER Stellen - Assign-Tab,
        // Waffenuebersicht im Bill Overview, Squad Setup und (seit demselben Tag, von mir selbst
        // hinzugefuegt) im Zweitwaffen-Popup. Genau die Fehlerklasse, die diesen Mod schon zwei volle
        // Testrunden gekostet hat: eine Kopie wird gefixt, die anderen drei nicht, und "eine von vier
        // Stellen zeigt es richtig" ist das Erkennungszeichen. Deshalb geloescht statt nachgezogen.
        //
        // Die Ueberschneidung ist der springende Punkt und der Grund, warum das nie jemand im Kopf
        // nachrechnen sollte: `carried` und `assigned` zaehlen teils DIESELBEN Stuecke (wer eine Waffe
        // traegt, hat sie meist auch zugewiesen). Ungetrennt addiert waere die Summe zu gross und der
        // Balken laenger als der Bestand.
        //
        // ZWEI Stellen machen das bewusst NICHT so und duerfen nicht hierher gezogen werden:
        // - Dialog_GenerateLoadouts' Owned-Spalte speist andere Eingangszahlen ein: sie zaehlt getragene
        //   STUECKE gegen die zugewiesene Menge (assignedItemCount), waehrend `assigned` hier KOLONISTEN
        //   zaehlt. Bei stapelbaren Sachen - Granaten, Wurfspeeren, Sprengsaetzen - sind das verschiedene
        //   Zahlen; der Unterschied ist am 2026-08-09 als Fehler aufgefallen und dort absichtlich so geloest.
        // - Dialog_SupplyLoadouts' Produktionsspalte nimmt ein anderes `inProduction`: nur die Auftraege
        //   zum REZEPT dieser Zeile (QueuedFromBills ueber e.recipe), nicht jeden Auftrag, der die Waffe
        //   irgendwie herstellt. Bei mehreren Rezepten fuer dieselbe Waffe ist das ein echter Unterschied.
        public static AvailabilitySegments WeaponAvailability(ThingDef w, Pawn pawn = null, int neededCount = 0)
        {
            GetWeaponInventory(w, out int _total, out int carried, out int stored, out int assigned);
            int carriedAndAssigned = Mathf.Min(carried, assigned);
            int assignedInStore = Mathf.Min(stored, Mathf.Max(0, assigned - carried));
            return BuildAvailabilitySegments(carried - carriedAndAssigned, carriedAndAssigned, assignedInStore,
                stored - assignedInStore, WeaponQueuedCount(w), pawn, w, neededCount: neededCount);
        }

        // Free (uncarried) weapon stock, for demand-scanning code that needs to know "is there already
        // enough of this WEAPON sitting around" - NOT IngredientStockCount, which only sees items in a
        // vanilla-recognized SlotGroup (a stockpile zone or ordinary storage building) via CE's own
        // resourceCounter. Weapons routinely sit in CE's own display/rack furniture instead (2026-08-02,
        // Knife showed "4 in storage" in its own tooltip - 2 on a display shelf, 2 on a
        // weapon rack - yet still got queued for MORE production) - GetWeaponInventory's own `stored`
        // field already finds those correctly (map.listerThings.ThingsOfDef, not resourceCounter), this
        // just exposes that one number for callers that only care about the stock side.
        public static int WeaponStoredCount(ThingDef w)
        {
            GetWeaponInventory(w, out _, out _, out int stored, out _);
            return stored;
        }

        /// <summary>Total mass (kg) and bulk of a loadout's contents. Generic "Any …" entries use CE's own
        /// average mass/bulk for that category, so nothing has to be resolved to a concrete item.</summary>
        public static void GetLoadoutMassBulk(Loadout lo, out float mass, out float bulk)
        {
            mass = 0f;
            bulk = 0f;
            // Slots EINMAL holen (2026-08-13): CEs Loadout.Slots ist kein Feld,
            // sondern ein Getter, der bei einem Loadout mit Eltern jedes Mal `_slots.Concat(ParentSlots)
            // .ToList()` baut - und jeder Kolonist mit Zuweisung dieses Mods haengt an einem persoenlichen
            // Anker, hat also immer Eltern. Zweimal lesen hiess: zwei komplette Neuaufbauten der Union pro
            // Aufruf, und diese Funktion laeuft pro Kolonistenzeile pro Bild.
            SlotsMassBulk(lo?.Slots, out mass, out bulk);
        }

        // Dieselbe Rechnung fuer eine lose Slot-Liste. Gebraucht, seit die Ueberlast-Warnung auch eine
        // Vorlage wiegen muss, die es noch gar nicht gibt (2026-08-15, Variante a) - bewusst als
        // geteilte Funktion, damit Warnung und Balken
        // nicht mit zwei verschiedenen Formeln antworten koennen.
        public static void SlotsMassBulk(IEnumerable<LoadoutSlot> slots, out float mass, out float bulk)
        {
            mass = 0f;
            bulk = 0f;
            if (slots == null) return;
            StatDef bulkStat = BulkStat;
            foreach (LoadoutSlot slot in slots)
            {
                if (slot.thingDef != null)
                {
                    mass += slot.thingDef.GetStatValueAbstract(StatDefOf.Mass) * slot.count;
                    if (bulkStat != null) bulk += slot.thingDef.GetStatValueAbstract(bulkStat) * slot.count;
                }
                else if (slot.genericDef != null)
                {
                    mass += slot.genericDef.mass * slot.count;
                    bulk += slot.genericDef.bulk * slot.count;
                }
            }
        }

        // Real production cost (raw materials, per-recipe) for one Loadout's own contents - same aggregation
        // Dialog_GenerateEquipmentPack's own pack rows already use (PackMaterialCosts), generalized to any
        // Loadout's slots instead of a pack-authoring PackItem list, so the Sidearm/Pack picker can show the
        // same "what would this actually cost to make" breakdown a market-value guess never captured.
        public static Dictionary<ThingDef, int> GetLoadoutProductionCosts(Loadout lo)
            => GetSlotsProductionCosts(lo?.OwnSlots);

        // Herausgezogen (2026-09-02: die Produktionskosten waren ueberall leer): dieselbe Rechnung
        // fuer eine Slot-Liste OHNE Loadout-Objekt - die Waffen-Zeilen der Auswahlfenster rechnen ihre
        // Kosten aus der Vorgabe, aus der beim Waehlen die Vorlage entsteht.
        public static Dictionary<ThingDef, int> GetSlotsProductionCosts(IEnumerable<LoadoutSlot> slots)
        {
            var costs = new Dictionary<ThingDef, int>();
            if (slots == null) return costs;
            foreach (LoadoutSlot s in slots)
            {
                if (s.thingDef == null) continue;
                RecipeDef r = GetRecipeFor(s.thingDef);
                if (r == null) continue;
                int yield = r.products != null && r.products.Count > 0 ? Mathf.Max(1, r.products[0].count) : 1;
                int batches = Mathf.CeilToInt((float)s.count / yield);
                foreach (ThingDefCountClass ing in GetRecipeIngredients(r))
                    if (ing?.thingDef != null)
                        costs[ing.thingDef] = (costs.TryGetValue(ing.thingDef, out int c) ? c : 0) + ing.count * batches;
            }
            return costs;
        }

        // AssignedWeaponCounts (Zugewiesen-Spalte) lebte hier nur einen Abend lang - am 2026-09-02
        // gebaut und am selben Abend wieder entfernt (die Spalte wurde nicht gebraucht).

        /// <summary>A single ThingDef's own Bulk stat (0 if CE's "Bulk" StatDef isn't found, or the def has
        /// none). Used to cap what can be picked as a sidearm - a full-size battle rifle shouldn't be
        /// handed out as someone's "backup" weapon just because it scored well.</summary>
        // Gemerkt (2026-08-14, Befund 9 der zweiten Performance-Runde): haengt nur an unveraenderlichen
        // Def-Daten, wird aber im Zweitwaffen-Picker des Solvers PRO KANDIDAT geprueft - und der laeuft
        // beim Ziehen an den Reglern alle 150 ms komplett neu. GetStatValueAbstract geht dabei jedes Mal
        // die ganze Stat-Kette des Def durch.
        private static readonly Dictionary<ThingDef, float> weaponBulkCache = new Dictionary<ThingDef, float>();

        public static float WeaponBulk(ThingDef d)
        {
            if (d == null) return 0f;
            if (weaponBulkCache.TryGetValue(d, out float cached)) return cached;
            StatDef bulkStat = BulkStat;
            float v = bulkStat != null ? d.GetStatValueAbstract(bulkStat) : 0f;
            weaponBulkCache[d] = v;
            return v;
        }

        // Eager field initializer (2026-08-01) - see Patch_Pawn_ChangeAmmoGizmo's own identical comment.
        private static readonly Texture2D massBulkBarTex = SolidColorMaterials.NewSolidColorTexture(new Color(0f, 0.8f, 0.8f));
        // Dieselbe Rotstufe wie Dialog_AssignLoadouts.OverCapBarTex - ein ueberladener Balken sieht ueberall
        // gleich aus, egal in welchem Fenster er steht.
        private static readonly Texture2D overCapBarTex = SolidColorMaterials.NewSolidColorTexture(new Color(0.75f, 0.18f, 0.18f));

        /// <summary>
        /// Beschriftung MITTEN AUF einem Balken, mit eigenem Kontrast (2026-08-13, im Test aufgefallen:
        /// die Zahlen wurden vom Balken verschluckt - bei hohen Melee-Werten ist der Balken hellgelb, bei
        /// Masse/Sperrigkeit tuerkis, und ausgerechnet der IST-Wert steht auf dem gefuellten Teil, waehrend
        /// der Grenzwert dahinter auf Schwarz gut lesbar bleibt).
        /// Schatten statt fester Farbe, weil die Fuellfarbe je nach Wert wandert (rot -> gelb -> gruen bei
        /// den Score-Balken): ein dunkler Versatz unter weisser Schrift traegt auf JEDEM Untergrund, eine
        /// feste Farbe waere nur fuer die eine Haelfte des Farbverlaufs richtig.
        /// Erwartet Text.Font vom Aufrufer; setzt Anchor selbst und stellt ihn zurueck.
        /// </summary>
        // "Welche Waffen-Vorlagen koennten JETZT bessere Munition benutzen?" (2026-08-13).
        // Der Fall: panzerbrechende Munition wird erforscht, und die 29
        // bestehenden Vorlagen stehen im Erzeugen-Schritt alle auf der Nadel - sie behalten also ihre alte
        // Munition, und nichts weist darauf hin. Die Nadel tut damit genau, was sie verspricht; still ist
        // trotzdem falsch.
        //
        // Entschieden war (b) plus Hinweis, und der Knopf dafuer existiert schon ("Mass-replace ammo").
        // Zu bauen war also nur das, was wirklich fehlte: dass ueberhaupt jemand sagt, es GIBT etwas zu
        // ersetzen.
        //
        // Die Frage ist bewusst schlicht gestellt und OHNE die Regler des Erzeugen-Schritts: was wuerde
        // PickDefaultAmmo aus den ERFORSCHTEN Sorten dieser Waffe waehlen, und steht das schon in der Vorlage?
        // Damit haengt der Hinweis nicht an einer Reglerstellung, die der Spieler gerade zufaellig gezogen
        // hat - er beantwortet "es gibt etwas Neues", nicht "so muesste es aussehen".
        // 5-Sekunden-Cache: die Anzeige, die das liest, ist dauerhaft auf dem Bildschirm, und die Antwort
        // aendert sich nur, wenn eine Forschung fertig wird.
        private static List<Loadout> betterAmmoCache;
        private static float betterAmmoCacheTime = -99f;
        public static List<Loadout> TemplatesWithBetterAmmo()
        {
            if (betterAmmoCache != null && Time.realtimeSinceStartup - betterAmmoCacheTime < 5f) return betterAmmoCache;
            var hits = new List<Loadout>();
            foreach (Loadout lo in LoadoutManager.Loadouts.Where(IsPlainWeaponLoadout))
            {
                LoadoutMainAndSide(lo, out ThingDef mainW, out _);
                if (mainW == null || IsSelfAmmoWeapon(mainW)) continue;
                List<Def> researched = GetAmmoTypesFor(mainW).Where(a => a is ThingDef td && IsAmmoResearched(td)).Cast<Def>().ToList();
                if (researched.Count == 0) continue;
                Def best = PickDefaultAmmo(researched);
                if (!(best is ThingDef bestTd)) continue;
                bool alreadyIn = lo.Slots != null && lo.Slots.Any(s => s.thingDef == bestTd);
                if (!alreadyIn) hits.Add(lo);
            }
            betterAmmoCache = hits;
            betterAmmoCacheTime = Time.realtimeSinceStartup;
            return hits;
        }

        // Der Name, wie er in eine ZELLE gehoert (2026-08-13): auch im Vanilla-Menue, ABER nur in der
        // Knopfanzeige - in der Auswahl selbst steht der ganze Name.
        // Das kurze Praefix ("W| ", "R| ", "E| ") sagt genau das, was das Symbol daneben schon zeigt, und
        // kostet in einer Zeile drei bis vier Zeichen - in der kompakten Ansicht war davon "W| R-6 ..." uebrig.
        // Im AUSWAHLFENSTER bleibt der volle, gespeicherte Name stehen: dort steht kein Symbol daneben, und
        // dort sucht man nach dem Namen, den man selbst vergeben hat.
        // Nur ein Praefix am ANFANG und hoechstens zwei Zeichen vor dem Strich - ein Loadout, das
        // "Scharfschuetze | Winter" heisst, behaelt seinen Namen vollstaendig.
        // ================= DER Name eines Loadouts, an EINER Stelle (2026-08-16) =================
        //
        // Ziel: eine zentrale Funktion fuer den Knopfnamen, damit er sich an einer Stelle aendern laesst,
        // am besten gleich auch fuer die Farbe - statt an jeder Stelle zu basteln. Die Farbe hat das seit
        // heute Mittag (LoadoutTintFor), der Name bekommt es
        // hier.
        //
        // Drei Sorten Vorsilbe, die bisher an verschiedenen Stellen einzeln behandelt wurden:
        //   "[Role] "  - INTERNE Kennung einer Rolle. Buchhaltung, nie fuer den Spieler. Faellt immer weg.
        //   "R| "      - einstellbare ANZEIGE-Vorsilbe fuer Rollen (Mod-Einstellungen).
        //   "W| "/"E| "- einstellbare Vorsilben, die bei Waffen-Vorlagen und Packs wirklich IM gespeicherten
        //                Namen stehen. Nicht loeschen: IsGeneratedTemplate benutzt sie als Ersatzerkennung,
        //                falls ein Loadout nicht in der Liste der selbst erzeugten steht (alter Spielstand).
        //
        // Deshalb aendert diese Funktion nur die ANZEIGE und nie die Daten. Seine Frage "brauchen wir die
        // vorsilben ueberhaupt noch" ist damit ein Schalter statt eines Umbaus: `ShowKindPrefix` auf false,
        // und sie sind ueberall weg - ohne dass ein einziges gespeichertes Loadout angefasst wird.
        //
        // markerShownElsewhere sagt, ob an dieser Stelle schon etwas anderes die Art verraet (Farbe, Symbol).
        // In einer Zelle ist das so - dort ist die Vorsilbe reine Wiederholung. In CEs eigenem Auswahlmenue
        // steht dagegen nur Text: faellt die Vorsilbe dort weg, ist eine Rolle von einer Waffen-Vorlage nicht
        // mehr zu unterscheiden. Genau diese Unterscheidung war der Grund des offenen Punktes.
        // Eine im gespeicherten Namen stehende Vorsilbe ("W| Kord", "E| Sanitaetstasche") abschneiden. Nur ein
        // bis zwei Zeichen vor dem Strich, damit ein Loadout namens "Scharfschuetze | Winter" seinen Namen
        // behaelt. Fragt BEWUSST die Einstellung nicht: hier geht es um Altbestand, der die Vorsilbe im
        // Etikett stehen hat - der muss auch dann sauber aussehen, wenn die Vorsilben aus sind.
        public static string StripKindPrefix(string name)
        {
            if (string.IsNullOrEmpty(name)) return name;
            int bar = name.IndexOf('|');
            return bar > 0 && bar <= 2 && bar + 1 < name.Length ? name.Substring(bar + 1).TrimStart() : name;
        }

        // DER Name eines Loadouts, wie ihn der Spieler sieht - die einzige Stelle, die das entscheidet
        // (2026-08-16: eine zentrale Funktion fuer den Knopfnamen statt Basteln an jeder Stelle).
        // Vorher bauten drei
        // Stellen ihren eigenen Namen zusammen - RoleUtility.DisplayLabel, EquipmentPackUtility.StripMarker und
        // die Zellen-Fassung - und liefen bei jeder Aenderung auseinander; genau daher kam "M16A4 +W| M...".
        //
        // Die harten Marker ("[Role]", "[Pack]") sind BUCHHALTUNG und werden nie gezeigt; sie bleiben im
        // gespeicherten Etikett stehen, damit die Erkennung nicht an der Anzeige haengt. Die kurzen Vorsilben
        // (W|, E|) sind reine Anzeige und haengen an den Einstellungen - seit 2026-08-16 aus.
        //
        // markerShownElsewhere sagt, ob an dieser Stelle schon etwas anderes die Art verraet (Farbe, Symbol).
        public static string LoadoutDisplayName(string rawLabel, bool markerShownElsewhere = false)
        {
            if (string.IsNullOrEmpty(rawLabel)) return rawLabel ?? "";
            string raw = rawLabel;
            CEReloadedSettings cfg = CEReloadedMod.settings;

            // ROLLE: der Marker steht IMMER da, auch neben Farbe und Symbol (Rollen tragen ueberall das Praefix
            // [Role]). Grund: bei einer Rolle traegt der Kolonist etwas
            // anderes als bei der gleichnamigen Waffen-Vorlage - das darf nicht nur an einem Blauton haengen.
            if (RoleUtility.IsRoleLabel(raw))
            {
                string roleBare = StripKindPrefix(raw.Substring(RoleUtility.RoleMarker.Length).TrimStart());
                return cfg != null && cfg.useRolePrefix && !string.IsNullOrEmpty(cfg.rolePrefix)
                    ? cfg.rolePrefix.Trim() + " " + roleBare : roleBare;
            }

            if (EquipmentPackUtility.IsEquipmentPackLabel(raw))
            {
                string packBare = StripKindPrefix(EquipmentPackUtility.BareName(raw));
                return !markerShownElsewhere && cfg != null && cfg.usePackPrefix && !string.IsNullOrEmpty(cfg.packPrefix)
                    ? cfg.packPrefix.Trim() + " " + packBare : packBare;
            }

            string bare = StripKindPrefix(raw);
            return !markerShownElsewhere && cfg != null && cfg.useLoadoutPrefix && !string.IsNullOrEmpty(cfg.loadoutPrefix)
                ? cfg.loadoutPrefix.Trim() + " " + bare : bare;
        }

        public static string LoadoutDisplayName(Loadout lo, bool markerShownElsewhere = false)
            => lo == null ? "" : LoadoutDisplayName(lo.label, markerShownElsewhere);

        // Der Name, wie er in eine ZELLE gehoert: dort steht ein Symbol oder eine Farbe daneben, die Vorsilbe
        // waere Wiederholung. Bleibt als eigener Name bestehen, damit die Aufrufstellen lesbar bleiben.
        public static string CellLabel(Loadout lo) => LoadoutDisplayName(lo, markerShownElsewhere: true);

        // Kuerzen, ohne die Klammer am Ende zu opfern (2026-08-23).
        //
        // Der Anlass: in der Auftragsuebersicht standen fuenf Zeilen untereinander, die alle
        // ".30-06 Springfie..." hiessen. Zweimal verbreitert, und danach stand im Test
        // ".30-06 Springfield (AP-...", ".30-06 Springfield (Sab...", "5.56x45mm NATO (F...". Die Spalte
        // war also breiter geworden und das Problem trotzdem dasselbe - weil normales Kuerzen von rechts
        // wegnimmt, und rechts steht bei Munition ausgerechnet die Sorte.
        //
        // Genau die ist die Information: der Grundname wiederholt sich die ganze Spalte hinunter, AP gegen
        // FMJ gegen Sabot unterscheidet die Zeilen. Wer von rechts kuerzt, wirft die unterscheidende Haelfte
        // weg und laesst die redundante stehen. Deshalb bleibt die Klammer ganz und der Grundname wird
        // gekuerzt: ".30-06 Spring... (Sabot)" statt ".30-06 Springfield (Sab...".
        //
        // Kein Platzproblem mehr wegzurechnen: passt beides zusammen, aendert die Funktion nichts. Passt
        // nicht einmal die Klammer allein sinnvoll hinein (weniger als ein Drittel bliebe fuer den Namen),
        // faellt sie auf gewoehnliches Kuerzen zurueck - ein Ergebnis wie "... (Sabot)" ohne jeden Namen
        // waere schlechter als ein gekuerzter Name.
        //
        // Die Klammerpruefung steht VOR dem Messen, nicht dahinter: diese Funktion laeuft pro Zeile pro
        // Bild, und Text.CalcSize ist der teure Teil. Ein Name ohne Klammer geht damit direkt in
        // GenText.Truncate, das seinen eigenen Zwischenspeicher hat - genau wie vorher, ohne Aufschlag.
        public static string TruncateKeepingSuffix(string label, float width)
        {
            if (string.IsNullOrEmpty(label)) return label;
            int open = label.LastIndexOf(" (", StringComparison.Ordinal);
            if (open <= 0 || !label.EndsWith(")", StringComparison.Ordinal)) return label.Truncate(width);
            if (Text.CalcSize(label).x <= width) return label;
            string head = label.Substring(0, open);
            string tail = label.Substring(open);
            float tailW = Text.CalcSize(tail).x;
            if (tailW > width * 0.66f) return label.Truncate(width);
            return head.Truncate(width - tailW) + tail;
        }

        // Die zwei Blautoene, mit denen der Mod ueberall zwischen ROLLE und Waffen-Vorlage unterscheidet -
        // bisher nur in CEs eigener Loadout-Spalte (Patch_LoadoutTooltip), seit 2026-08-13 auch in den
        // eigenen Zellen ("nutze die blau abstufungen um rollen und waffen zu unterscheiden, so wie wir es
        // auch beim vanilla current loadout draussen machen"). Eine Definition, damit die drei Stellen nicht
        // auseinanderlaufen.
        public static readonly Color LoadoutTintRole = new Color(0.55f, 0.75f, 1f);
        public static readonly Color LoadoutTintWeapon = new Color(0.8f, 0.88f, 1f);

        // EINE Antwort auf "gehoert dieses Loadout uns, und welcher Blauton steht ihm zu" (2026-08-16).
        //
        // Anlass: kaum war die Einfaerbung ueberhaupt sichtbar (siehe TintedButton), fiel auf, dass
        // sie am falschen Knopf klebt - ein in CE erstelltes Vanilla-Loadout hatte einen Blauton am Knopf,
        // den es nicht haben soll. Der Fehler war aelter als der Fix; er war nur unsichtbar: die zwei Ansichten
        // faerbten ALLES, was ein Loadout hatte, waehrend CEs eigene Spalte laengst richtig unterscheidet.
        // Der Blauton heisst "das hat dieser Mod gemacht" - ein von Hand in CE gebautes Loadout gehoert
        // nicht dazu, und wenn die Farbe das nicht mehr trennt, sagt sie gar nichts mehr.
        // Deshalb hier, an einer Stelle, mit derselben Regel, die Patch_LoadoutTooltip fuer CEs Spalte
        // benutzt: null = nicht unseres, kein Ton.
        public static Color? LoadoutTintFor(Loadout assigned)
        {
            if (assigned == null || assigned.defaultLoadout) return null;
            // Ein Anker ist unsere eigene Klammer um die Zuweisung - was darunter haengt, ist die Vorlage.
            bool isAnchor = AmmoAssignmentUtility.IsAmmoLink(assigned);
            Loadout real = isAnchor ? AmmoAssignmentUtility.DisplayLoadout(assigned) : assigned;
            if (real == null || real.defaultLoadout) return null;
            if (RoleUtility.IsRoleLoadout(real)) return LoadoutTintRole;
            if (isAnchor || IsGeneratedTemplate(real)) return LoadoutTintWeapon;
            return null;
        }

        // EIN Knopf, der eine Einfaerbung und einen gesperrten Zustand wirklich ZEIGT (2026-08-16).
        //
        // Im Test zwei Befunde, beide mit gemessenen RGB-Werten belegt: die blaue
        // Unterscheidung Rolle/Vorlage fehlt in unseren beiden Ansichten vollstaendig (alle Knoepfe exakt
        // 106,81,46 - der ungefaerbte Standardwert), und die gesperrten Rollen-Zellen sind von normalen
        // paarweise nicht zu unterscheiden. Beide Male lag es NICHT am fehlenden Code, sondern an der
        // Reihenfolge: wir haben brav den eingefaerbten Hintergrund gezeichnet und danach
        // Widgets.ButtonText gerufen - und dessen ButtonTextWorker ruft DrawButtonGraphic(rect), malt die
        // Knopfgrafik also in voller Deckkraft NOCHMAL darueber. Im Dekompilat nachgelesen; dieselbe
        // Reihenfolge steht an drei Stellen im Assign-Tab.
        // Auch GUI.enabled = false kann daran nichts aendern: DrawButtonGraphic zeichnet ueber
        // GUI.DrawTexture, und das kennt GUI.enabled nicht - es sperrt nur den Klick. Genau deshalb war
        // "ausgegraut" bisher unsichtbar, obwohl der Klick tatsaechlich schon gesperrt war.
        // Konsequenz: der Hintergrund gehoert uns, ButtonText zeichnet keinen mehr (drawBackground:false)
        // und liefert nur noch den Klick.
        public static bool TintedButton(Rect rect, Color? tint, bool disabled = false)
        {
            Texture2D atlas = Widgets.ButtonBGAtlas;
            if (!disabled && Mouse.IsOver(rect))
                atlas = Input.GetMouseButton(0) ? Widgets.ButtonBGAtlasClick : BtnMouseoverAtlas;
            Color prev = GUI.color;
            // Gesperrt = spuerbar dunkler und flacher. Ein eigener Grauton statt GUI.enabled, weil der
            // nachweislich nichts zeichnet (siehe oben) - und kein Durchstreichen, das ist der
            // Loesch-Vorschau vorbehalten.
            GUI.color = disabled ? new Color(0.45f, 0.45f, 0.45f) : (tint ?? Color.white);
            Widgets.DrawAtlas(rect, atlas);
            GUI.color = prev;
            if (disabled) return false;
            return Widgets.ButtonText(rect, "", drawBackground: false);
        }

        // RimWorld haelt die Mouseover-Grafik in einem PRIVATEN Feld (Widgets.ButtonBGAtlasMouseover),
        // also laden wir dieselbe Textur selbst - ohne sie fuehlt sich ein eingefaerbter Knopf tot an,
        // weil er beim Ueberfahren nicht mehr reagiert. Faellt auf die normale Grafik zurueck, falls
        // RimWorld den Pfad einmal umbenennt: dann fehlt die Rueckmeldung, aber nichts ist kaputt.
        private static Texture2D btnMouseoverAtlas;
        private static Texture2D BtnMouseoverAtlas
        {
            get
            {
                if (btnMouseoverAtlas == null)
                    btnMouseoverAtlas = ContentFinder<Texture2D>.Get("UI/Widgets/ButtonBGMouseover", false)
                        ?? Widgets.ButtonBGAtlas;
                return btnMouseoverAtlas;
            }
        }

        // reserveRight = Platz am rechten Balkenende, der schon belegt ist (das Warnzeichen bei Ueberlast)
        // und deshalb nicht mehr fuer die zentrierte Zahl zaehlt. 2026-08-28 im Selbsttest gefunden: in der
        // KOMPAKTEN Zuweisungs-Ansicht stand "57.6/40.0 kg" und das "g" lag unter dem Warnzeichen. Die
        // Kartenansicht zeigte denselben Wert vollstaendig - nicht weil sie es richtig machte, sondern weil
        // ihr Balken breiter ist. Struktureller Fix an der gemeinsamen Funktion statt an einer
        // der drei Aufrufstellen: die Zahl wird in dem Platz zentriert, der ihr wirklich gehoert.
        // Die zwei Toene der Tragkraft-Leisten (2026-08-30): was der Koerper selbst traegt,
        // im gewohnten Tuerkis; was erst ein Rucksack, eine Weste oder ein anderer Gegenstand moeglich macht, eine
        // Stufe dunkler. Das Hellere steht immer zuerst, weil es die Grundlage ist.
        private static Texture2D carryBarTex, carryBoostTex, carryOverTex;
        private static Texture2D CarryBarTex => carryBarTex ?? (carryBarTex = SolidColorMaterials.NewSolidColorTexture(new Color(0f, 0.8f, 0.8f)));
        private static Texture2D CarryBoostTex => carryBoostTex ?? (carryBoostTex = SolidColorMaterials.NewSolidColorTexture(new Color(0f, 0.52f, 0.55f)));
        private static Texture2D CarryOverTex => carryOverTex ?? (carryOverTex = SolidColorMaterials.NewSolidColorTexture(new Color(0.75f, 0.18f, 0.18f)));

        /// <summary>Fuellstand einer Tragkraft-Leiste: bis `naturalCap` im Grundton, darueber (nur dank getragener
        /// Gegenstaende moeglich) dunkler; ueberladen ganz rot. `naturalCap` &lt; 0 heisst "nicht bekannt", dann
        /// bleibt es einfarbig.</summary>
        public static void DrawCarryFillBar(Rect bar, float load, float cap, float naturalCap, bool over)
        {
            Widgets.FillableBar(bar, cap > 0f ? Mathf.Clamp01(load / cap) : 0f, over ? CarryOverTex : CarryBarTex, BaseContent.BlackTex, true);
            if (over || cap <= 0f || naturalCap < 0f || load <= naturalCap) return;
            // Genau die Geometrie, die FillableBar selbst benutzt (Rand 3px), sonst sitzt der zweite Ton daneben.
            Rect inner = bar.ContractedBy(3f);
            float x0 = inner.x + inner.width * Mathf.Clamp01(naturalCap / cap);
            float x1 = inner.x + inner.width * Mathf.Clamp01(load / cap);
            if (x1 - x0 > 0.5f) GUI.DrawTexture(new Rect(x0, inner.y, x1 - x0, inner.height), CarryBoostTex);
        }

        /// <summary>Die EINE Material-Zelle des Mods: ein Knopf mit dem Materialbild darin. Liefert true bei Klick.</summary>
        // 2026-08-30: das Material hatte in der Waffen-Bill-Uebersicht einen weissen Rand, in Equipment
        // Packs dagegen sah es aus wie ein Knopf - zwei Codebasen fuer dieselbe Sache, also vereinheitlicht.
        // Der weisse Rand war ein Widgets.DrawBox um einen unsichtbaren Knopf - zwei Nachbauten
        // derselben Zelle, die auseinandergelaufen sind. Jetzt eine Funktion, beide Stellen rufen sie.
        // DIE ZWEI AUFTRAGSARTEN ALS SYMBOL (2026-08-30): Unendlichkeitszeichen und x1 als PNGs, bei
        // Bedarf ersetzbar. Textures/UI/Icon_BillUntil.png
        // (Lemniskate) und Icon_BillOnce.png (x1), weiss auf transparent - austauschbar, ohne dass hier etwas zu
        // aendern waere. Erst beim ersten Zeichnen geladen, danach gemerkt.
        private static Texture2D texBillUntil, texBillOnce;
        public static Texture2D TexBillUntil => texBillUntil ?? (texBillUntil = ContentFinder<Texture2D>.Get("UI/Icon_BillUntil", false));
        public static Texture2D TexBillOnce => texBillOnce ?? (texBillOnce = ContentFinder<Texture2D>.Get("UI/Icon_BillOnce", false));

        /// <summary>Auftrag vom Typ "stelle her bis X" (Unendlichkeit) statt "mache N Stueck" (x1)?</summary>
        public static bool IsUntilBill(Bill_Production b) => b != null && b.repeatMode == BillRepeatModeDefOf.TargetCount;

        public static void DrawBillModeIcon(Rect r, bool until)
        {
            Texture2D tex = until ? TexBillUntil : TexBillOnce;
            if (tex != null) GUI.DrawTexture(r, tex);
        }

        public static bool StuffButton(Rect r, ThingDef stuff)
        {
            bool clicked = Widgets.ButtonText(r, "");
            if (stuff != null) Widgets.ThingIcon(new Rect(r.x + 1f, r.y + 1f, r.width - 2f, r.height - 2f), stuff);
            return clicked;
        }

        public static void BarLabel(Rect rect, string label, float reserveRight = 0f)
        {
            if (reserveRight > 0f)
                rect = new Rect(rect.x, rect.y, Mathf.Max(12f, rect.width - reserveRight), rect.height);
            // Gekuerzt statt geclippt (2026-08-13): in der kompakten Ansicht stand
            // "6.5/10" da, wo "6.5/101.3" gemeint war - RimWorld schneidet einen zu langen Label-Text in
            // einem zu engen Rect einfach ab, und zwar OHNE Auslassungszeichen. Eine abgeschnittene Zahl
            // liest sich als andere Zahl, das ist schlimmer als eine sichtbar gekuerzte.
            // Kleinere Schrift zuerst versuchen, damit der Wert moeglichst ganz dasteht; erst wenn das auch
            // nicht reicht, wirklich kuerzen.
            // IMMER die kleine Schrift (2026-08-30: im Equipment-Packs-Fenster stand Mass/Bulk in grosser
            // Schrift, sonst ueberall in kleiner - jetzt ueberall klein). Vorher erbte jede Leiste die Schrift ihres Aufrufers und wurde nur
            // dann kleiner, wenn der Text sonst nicht passte - also mal so, mal so. Eine Zahl in einer Leiste ist
            // ueberall dieselbe Sorte Information und sieht ab jetzt ueberall gleich aus.
            GameFont prevFont = Text.Font;
            Text.Font = GameFont.Tiny;
            string shown = label.Truncate(rect.width - 2f);
            Text.Anchor = TextAnchor.MiddleCenter;
            Color prev = GUI.color;
            // NUR die weisse Schrift, ohne Unterlage und ohne Schatten (2026-08-30): die grauen Overlays in
            // den Balken sind komplett weg. Die dunkle Pille vom 26.08. kam aus einem Testbefund zum Kontrast
            // auf dem gefuellten Teil; sie ist damit ausdruecklich zurueckgenommen.
            GUI.color = Color.white;
            Widgets.Label(rect, shown);
            GUI.color = prev;
            Text.Anchor = TextAnchor.UpperLeft;
            Text.Font = prevFont;
        }

        /// <summary>Two fillable bars (mass, bulk) against a median pawn's carrying capacity - the same
        /// visual the Weapon Loadouts tab and Assign Loadouts cards use, factored out so the Equipment
        /// Packs tab and its auto-generate popup can show "how much of a pawn's capacity does this alone
        /// cost" too. `rect` should be ~48px tall (two 24px rows).</summary>
        // CEs Gewichtsgrenze, nicht RimWorlds - und das ist der ganze Punkt (2026-08-09).
        // Vanilla StatDefOf.CarryingCapacity hat Basiswert 75, CEs CarryWeight hat 40. Ausgebremst wird ein
        // Pawn aber von CE: in CompInventory (im Dekompilat nachgelesen) steht
        // capacityWeight => GetStatValue(CE_StatDefOf.CarryWeight), und moveSpeedFactor rechnet gegen genau
        // dieses capacityWeight. Der Balken gegen den Vanilla-Wert zeigte also rund die halbe Fuellung: ein
        // Pawn, den CE schon auf Schneckentempo bremst, las sich als locker unter der Haelfte.
        // Zwei Indizien, dass das ein Versehen war und keine Absicht: die Fallback-Konstanten an denselben
        // Stellen (35f / 33.2f) sind CEs Groessenordnung, nicht Vanillas 75, und der Bulk-Balken direkt
        // daneben liest laengst CEs eigenen CarryBulk. Eine Achse stimmte, die andere nicht.
        // Als Helfer statt an fuenf Stellen per Hand, damit die Namenssuche eine Stelle bleibt.
        public static float CarryWeightOf(Pawn p)
        {
            StatDef st = DefDatabase<StatDef>.GetNamed("CarryWeight", false);
            return st != null && p != null ? p.GetStatValue(st) : 0f;
        }

        // Die drei CE-StatDefs EINMAL nachgeschlagen statt ueber ihren STRING pro Zeile pro Bild
        // (2026-08-14, Befund 4 der ersten Performance-Runde). DefDatabase.GetNamed ist ein
        // Wörterbuch-Zugriff mit String-Schluessel - fuer sich billig, aber diese drei standen in
        // Zeichenfunktionen, die pro sichtbarer Zeile laufen (DrawMassBulkBars, die waagerechte
        // Schwester daneben, und beide Kolonisten-Ansichten). Defs sind zur Laufzeit unveraenderlich,
        // eine einmalige Aufloesung kann also nicht veralten; `resolved` statt eines Null-Tests, damit
        // ein in dieser Modliste wirklich fehlender Stat nicht bei jedem Aufruf neu gesucht wird.
        private static StatDef statCarryBulk, statCarryWeight;
        private static bool statDefsResolved;

        private static void EnsureStatDefs()
        {
            if (statDefsResolved) return;
            statDefsResolved = true;
            statCarryBulk = DefDatabase<StatDef>.GetNamed("CarryBulk", false);
            statCarryWeight = DefDatabase<StatDef>.GetNamed("CarryWeight", false);
        }

        public static StatDef CarryBulkStat { get { EnsureStatDefs(); return statCarryBulk; } }
        public static StatDef CarryWeightStat { get { EnsureStatDefs(); return statCarryWeight; } }

        // ===== Tragkraft-Erhoeher an den kolonistenbezogenen Masse/Sperrigkeits-Leisten (2026-08-30) =====
        // CEs Tragkraft kommt aus zwei Stats (CarryWeight fuer Masse, CarryBulk fuer Sperrigkeit), und Kleidung hebt
        // sie ueber equippedStatOffsets (Apparel_Carrying.xml: Rucksack/Weste +CarryBulk, Exoframe +CarryWeight -
        // siehe reference-ce-carry-capacity: normale Kleidung gibt nichts). EINE Funktion fuer alle Leisten, die an
        // einem konkreten Kolonisten haengen: rechts in der Leiste das Symbol des Gegenstands, der diese Grenze
        // hebt, bei mehreren das erste mit '...'; der Tooltip zaehlt alle auf, mit Grundwert und Rest.
        // Die Leisten mit dem Median-Kolonisten (Vorlagen, Packs) bekommen das NICHT - dort gibt es keinen
        // Traeger, dessen Kleidung man zeigen koennte.
        public struct CarryBooster { public Apparel apparel; public float offset; public float factor; }

        public static List<CarryBooster> CarryBoostersOf(Pawn pawn, bool bulk)
        {
            var list = new List<CarryBooster>();
            StatDef stat = bulk ? CarryBulkStat : CarryWeightStat;
            if (stat == null || pawn?.apparel?.WornApparel == null) return list;
            foreach (Apparel a in pawn.apparel.WornApparel)
            {
                if (a?.def == null) continue;
                float off = a.def.equippedStatOffsets != null ? a.def.equippedStatOffsets.GetStatOffsetFromList(stat) : 0f;
                float fac = 1f;   // Apparel kennt nur equippedStatOffsets, keine Faktoren (RimWorld 1.6)
                if (Mathf.Abs(off) > 0.001f || Mathf.Abs(fac - 1f) > 0.001f)
                    list.Add(new CarryBooster { apparel = a, offset = off, factor = fac });
            }
            return list;
        }

        // Zeichnet das Symbol rechts IN der Leiste und gibt zurueck, wie viel Breite es rechts belegt (0, wenn nichts
        // getragen wird) - der Aufrufer reserviert das fuer die Beschriftung. `rightReserve` ist, was rechts schon
        // belegt ist (das Warnzeichen bei Ueberlast), das Symbol rueckt davor.
        public static float DrawCarryBoosterIcon(Rect bar, List<CarryBooster> boosters, float rightReserve)
        {
            if (boosters == null || boosters.Count == 0) return 0f;
            float size = Mathf.Clamp(bar.height - 2f, 12f, 18f);
            Rect icon = new Rect(bar.xMax - rightReserve - size - 2f, bar.y + (bar.height - size) / 2f, size, size);
            Widgets.ThingIcon(icon, boosters[0].apparel);
            if (boosters.Count > 1)
            {
                // Drei Punkte als Kaestchen statt als Text (2026-08-30, im Test: vom Text '...' war
                // nur ein einzelner heller Punkt an der Zellkante zu sehen - RimWorlds Label kappt ihn in dem 14-Punkte-Rect).
                // Unten IM Symbol, mittig, mit dunkler Unterlage, damit sie auf jedem Gegenstandsbild zu sehen sind.
                float d = Mathf.Max(2f, Mathf.Round(size / 7f));
                float totalW = 5f * d;
                float x0 = icon.x + Mathf.Round((icon.width - totalW) / 2f), y0 = icon.yMax - d - 1f;
                Widgets.DrawBoxSolid(new Rect(x0 - 1f, y0 - 1f, totalW + 2f, d + 2f), new Color(0f, 0f, 0f, 0.75f));
                for (int i = 0; i < 3; i++) Widgets.DrawBoxSolid(new Rect(x0 + i * 2f * d, y0, d, d), Color.white);
            }
            return size + 4f;
        }

        // Der Tooltip der Leiste: Grenze mit Herkunft (Grundwert, jeder Gegenstand, Rest) und der aktuelle Stand.
        // Ersetzt die alten Ein-Wort-Tooltips ('Mass' / 'Bulk'), die nichts sagten.
        public static string CarryCapacityTooltip(Pawn pawn, bool bulk, float load, float cap, List<CarryBooster> boosters)
        {
            string F(float v) => bulk ? v.ToString("0.0") : v.ToString("0.0") + " kg";
            StatDef stat = bulk ? CarryBulkStat : CarryWeightStat;
            float baseVal = stat != null && pawn?.def != null ? pawn.def.GetStatValueAbstract(stat) : 0f;
            var sb = new System.Text.StringBuilder();
            sb.Append("<b>").Append((bulk ? "CER_LoadoutUtility_CarryTipHeaderBulk" : "CER_LoadoutUtility_CarryTipHeaderMass").Translate(F(load), F(cap)).ToString()).Append("</b>\n");
            sb.Append("  ").Append("CER_LoadoutUtility_CarryTipBase".Translate(F(baseVal)).ToString());
            float sumOff = 0f;
            if (boosters != null)
                foreach (CarryBooster b in boosters)
                {
                    string amount = Mathf.Abs(b.offset) > 0.001f ? (b.offset >= 0 ? "+" : "") + F(b.offset) : "x" + b.factor.ToString("0.00");
                    sb.Append("\n  <color=#66D966>").Append("CER_LoadoutUtility_CarryTipItem".Translate(b.apparel.LabelCap.ToString(), amount).ToString()).Append("</color>");
                    sumOff += b.offset;
                }
            float other = cap - baseVal - sumOff;
            if (Mathf.Abs(other) >= 0.05f)
                sb.Append("\n  <color=#A8A8A8>").Append("CER_LoadoutUtility_CarryTipOther".Translate((other >= 0 ? "+" : "") + F(other)).ToString()).Append("</color>");
            if (boosters == null || boosters.Count == 0)
                sb.Append("\n\n<color=#A8A8A8>").Append((bulk ? "CER_LoadoutUtility_CarryTipNoneBulk" : "CER_LoadoutUtility_CarryTipNoneMass").Translate().ToString()).Append("</color>");
            return sb.ToString();
        }

        // "Bulk" liegt schon aufgeloest im Block der Handhabungs-Stats weiter unten (ResolveHandlingStats).
        // Bewusst KEIN zweites Feld dafuer: zwei Aufloesungen desselben Stats waeren genau die Art
        // Doppelbuchhaltung, die spaeter auseinanderlaeuft.
        public static StatDef BulkStat { get { ResolveHandlingStats(); return statBulk; } }

        // Die zwei Mittelwerte haengen nur an unveraenderlichen Def-Daten (ThingDefOf.Human) - einmal
        // ausgerechnet gilt fuer den ganzen Spielprozess. Vorher rief jede gezeichnete Balkenreihe zweimal
        // GetStatValueAbstract, das intern die ganze Stat-Kette des Def durchgeht.
        private static float medianMassCache = -1f, medianBulkCache = -1f;

        public static float MedianCarryWeight()
        {
            if (medianMassCache >= 0f) return medianMassCache;
            EnsureStatDefs();
            medianMassCache = statCarryWeight != null ? ThingDefOf.Human.GetStatValueAbstract(statCarryWeight) : 0f;
            return medianMassCache;
        }

        private static float MedianCarryBulk()
        {
            if (medianBulkCache >= 0f) return medianBulkCache;
            EnsureStatDefs();
            medianBulkCache = BulkStat != null && statCarryBulk != null
                ? ThingDefOf.Human.GetStatValueAbstract(statCarryBulk) : 20f;
            return medianBulkCache;
        }

        public static void DrawMassBulkBars(Rect rect, float totalMass, float totalBulk)
        {
            float medianMass = MedianCarryWeight();
            float medianBulk = MedianCarryBulk();
            if (medianMass <= 0f) medianMass = 33.2f;
            if (medianBulk <= 0f) medianBulk = 21.6f;

            // Rot + Warnzeichen + BarLabel, wortgleich zur waagerechten Schwester darunter (2026-08-13,
            // im Test in der Rollen-Vorschau gefunden: Bulk 31.0/20.0, also 155 %, und der Balken
            // blieb tuerkis wie bei "alles gut"). Beide Korrekturen wurden am selben Tag EINE Funktion
            // weiter unten gebaut und hier nicht - genau der Fehlgriff: erste
            // Fundstelle gefixt, zweite desselben Musters daneben stehen gelassen.
            // Diese Fassung zeichnet dieselben Werte fuer die Pack-Zutaten, die Waffen-Vorlage und die
            // Rollen-Vorschau - alle drei sind "das baue ich gerade", also gilt hier dieselbe Aussage:
            // eine Warnung gegen den MEDIAN-Kolonisten, keine Tatsache ueber eine bestimmte Person.
            void Bar(Rect labelR, string caption, Rect barR, float value, float cap, string valueText)
            {
                // LabelCell statt Widgets.Label: haelt die Beschriftung strukturell einzeilig und kuerzt
                // sichtbar, falls die Kappung unten doch greift - vorher wurde stumpf geclippt ("Sperri").
                LabelCell(labelR, caption);
                bool over = cap > 0f && value > cap;
                Widgets.FillableBar(barR, Mathf.Clamp01(value / cap), over ? overCapBarTex : massBulkBarTex, BaseContent.BlackTex, true);
                BarLabel(barR, valueText, over ? 16f : 0f);
                if (over) DrawWarningBadge(new Rect(barR.xMax - 16f, barR.y - 3f, 15f, 15f), ColBad);
            }

            // Beschriftungsbreite gemessen statt 40f fest (2026-08-26: "Sperri" auf Deutsch -
            // "Sperrigkeit" braucht ~62px). Beide Zeilen teilen sich EINE Breite, damit die Balken
            // buendig untereinander stehen; die Kappung bei 40% der Gesamtbreite schuetzt den Balken.
            string massCaption = "CER_LoadoutUtility_MassLabel".Translate();
            string bulkCaption = "CER_LoadoutUtility_BulkLabel".Translate();
            float captionW = Mathf.Min(rect.width * 0.4f,
                Mathf.Max(40f, Text.CalcSize(massCaption).x + 2f, Text.CalcSize(bulkCaption).x + 2f));

            Rect massRect = new Rect(rect.x, rect.y, rect.width, 22f);
            Bar(new Rect(massRect.x, massRect.y, captionW, 22f), massCaption,
                new Rect(massRect.x + captionW + 4f, massRect.y + 1f, massRect.width - captionW - 4f, 18f), totalMass, medianMass,
                "CER_LoadoutUtility_MassValue".Translate(totalMass.ToString("F1"), medianMass.ToString("F1")));

            Rect bulkRect = new Rect(rect.x, rect.y + 24f, rect.width, 22f);
            Bar(new Rect(bulkRect.x, bulkRect.y, captionW, 22f), bulkCaption,
                new Rect(bulkRect.x + captionW + 4f, bulkRect.y + 1f, bulkRect.width - captionW - 4f, 18f), totalBulk, medianBulk,
                "CER_LoadoutUtility_BulkValue".Translate(totalBulk.ToString("F1"), medianBulk.ToString("F1")));
        }

        /// <summary>Same two bars, SIDE BY SIDE instead of stacked - for a spot that has the width to spare
        /// but not much height (a picker row). `rect` should be ~22px tall and at least ~160px wide - each
        /// bar gets the full half-width for its own value text (no separate "Mass"/"Bulk" word label eating
        /// into it; the "kg" unit suffix on Mass's own number is disambiguation enough against Bulk's bare
        /// number, and the column header above already says "Mass / Bulk").</summary>
        // Beschriftungs-Cache fuer die zwei Balken (2026-09-06, perf-packwizard-draw-time - gemessen: der
        // Abschnitt "Zeile Kosten+Masse" war mit 37 ms/s der teuerste des Pack-Assistenten, bei 2000
        // Aufrufen/s). Zwei Translate-Aufrufe plus zwei F1-Formatierungen je Zeile je Durchgang sind reine
        // Zeichenkettenarbeit fuer einen Text, der sich nur aendert, wenn sich die Zahl aendert. Schluessel
        // ist das auf Zehntel gerundete Wertepaar - genau das, was die Beschriftung zeigt. Der Cache wird
        // geleert, wenn er waechst (Sprachwechsel, neue Werte), damit er nie zur Speicherfalle wird.
        private static readonly Dictionary<long, string> massLabelCache = new Dictionary<long, string>();
        private static readonly Dictionary<long, string> bulkLabelCache = new Dictionary<long, string>();
        private static string CapLabel(Dictionary<long, string> cache, string key, float value, float cap)
        {
            long k = ((long)Mathf.RoundToInt(value * 10f) << 32) ^ (uint)Mathf.RoundToInt(cap * 10f);
            if (cache.TryGetValue(k, out string s)) return s;
            if (cache.Count > 4096) cache.Clear();
            s = key.Translate(value.ToString("F1"), cap.ToString("F1")).ToString();
            cache[k] = s;
            return s;
        }

        public static void DrawMassBulkBarsHorizontal(Rect rect, float totalMass, float totalBulk)
        {
            float medianMass = MedianCarryWeight();
            float medianBulk = MedianCarryBulk();
            if (medianMass <= 0f) medianMass = 33.2f;
            if (medianBulk <= 0f) medianBulk = 21.6f;

            // Rot + Warnzeichen, sobald es nicht mehr passt (2026-08-13, im Test aufgefallen: "Mass
            // 55.0/40.0 und Bulk 55.8/20.0, beide Balken komplett gefuellt und tuerkis - 138 % und 279 %
            // ueber der Grenze sehen aus wie 'alles gut'"). Ursache war das Clamp01: ab 100 % sieht jeder
            // Ueberschuss identisch aus. Auf der Kolonisten-Karte macht DrawCapacityBar es laengst richtig;
            // dieser Balken - derselbe optisch, andere Funktion - hatte es nie bekommen.
            // Warum das zusammenhaengt: genau dieses Loadout hat der Auto-assign danach an einen Kolonisten
            // vergeben, der es nicht tragen kann. Rot BEIM BAUEN zeigt das Problem eine Stufe frueher.
            // Bezugsgroesse ist hier bewusst der MEDIAN-Kolonist und nicht ein konkreter Pawn - ein Loadout
            // gehoert noch niemandem. Es ist also eine Warnung ("passt dem Durchschnitt nicht"), keine
            // Tatsache ueber eine Person.
            void Bar(Rect r, float value, float cap, string label)
            {
                bool over = cap > 0f && value > cap;
                Widgets.FillableBar(r, Mathf.Clamp01(value / cap), over ? overCapBarTex : massBulkBarTex, BaseContent.BlackTex, true);
                BarLabel(r, label, over ? 11f : 0f);
                if (over) DrawWarningBadge(new Rect(r.xMax - 11f, r.y - 5f, 16f, 16f), ColBad);
            }

            float half = (rect.width - 8f) / 2f;
            Bar(new Rect(rect.x, rect.y + 1f, half, rect.height - 2f), totalMass, medianMass,
                CapLabel(massLabelCache, "CER_LoadoutUtility_MassValue", totalMass, medianMass));
            Bar(new Rect(rect.x + half + 8f, rect.y + 1f, half, rect.height - 2f), totalBulk, medianBulk,
                CapLabel(bulkLabelCache, "CER_LoadoutUtility_BulkValue", totalBulk, medianBulk));
        }

        /// <summary>A small clipboard glyph (body + clip tab + a couple of text lines), drawn from plain
        /// rects rather than a texture asset - this mod ships no "paste" icon of its own, and a clipboard
        /// reads unambiguously as "paste" wherever a bare "+" would just look like "add". Tinted by the
        /// current GUI.color, same convention as every other icon button in this codebase. Briefly replaced
        /// by a real Icon_Paste.png asset (2026-08-01), reverted the same day after that file turned out to
        /// have never been committed to git (so an accidental local edit to it had nothing to restore FROM) -
        /// back to procedural, which has no such single-point-of-failure. Square, centered on the SHORTER
        /// side of `r` rather than stretched to fill it - DrawPasteButton's own pasteRect is 20 wide but full
        /// CELL height, and naively using the fractions of a non-square `r` directly would stretch into a
        /// vertical bar (the one thing worth keeping from the asset-based version's own fix).</summary>
        // 2026-08-13: zurueck auf das echte PNG (Icon_Paste.png). Der Grund fuer die gezeichnete Fassung
        // war, dass die Datei damals nicht unter Versionskontrolle lag und ein versehentlicher Eingriff
        // nichts zum Zurueckfallen hatte - inzwischen ist sie
        // versioniert, damit ist der Einwand erledigt. Das Quadrat-Zentrieren bleibt: pasteRect ist 20 breit
        // und so hoch wie die ganze Zelle, ein gestrecktes Bild waere ein senkrechter Balken.
        // Faellt das Laden aus (Datei fehlt/kaputt), zeichnet die alte Geometrie weiter - ein leeres
        // Rechteck an einer Schaltflaeche waere schlimmer als ein schlichtes Symbol.
        private static Texture2D pasteTex;
        private static bool pasteTexTried;

        public static void DrawPasteIcon(Rect r)
        {
            float size = Mathf.Min(r.width, r.height);
            Rect sq = new Rect(r.x + (r.width - size) / 2f, r.y + (r.height - size) / 2f, size, size);
            if (!pasteTexTried)
            {
                pasteTexTried = true;
                pasteTex = ContentFinder<Texture2D>.Get("UI/Icon_Paste", false);
            }
            if (pasteTex != null)
            {
                GUI.DrawTexture(sq, pasteTex);
                return;
            }
            float w = sq.width, h = sq.height;
            Rect body = new Rect(sq.x + w * 0.12f, sq.y + h * 0.16f, w * 0.76f, h * 0.8f);
            Widgets.DrawBoxSolid(body, GUI.color);
            Color bg = new Color(0.08f, 0.08f, 0.08f, 0.95f);
            Widgets.DrawBoxSolid(body.ContractedBy(Mathf.Max(1f, w * 0.07f)), bg);
            Rect clip = new Rect(sq.x + w * 0.32f, sq.y, w * 0.36f, h * 0.22f);
            Widgets.DrawBoxSolid(clip, GUI.color);
            float lineH = Mathf.Max(1f, h * 0.09f);
            float lineX = body.x + body.width * 0.22f;
            float lineW = body.width * 0.56f;
            for (int i = 0; i < 3; i++)
                Widgets.DrawBoxSolid(new Rect(lineX, body.y + body.height * (0.32f + i * 0.22f), lineW, lineH), GUI.color);
        }

        /// <summary>A rough per-unit cost for one slot's def - real market value for a concrete ThingDef, or
        /// its generic's borrowed representative's value as an estimate (a generic has no price of its own,
        /// since the actual item fetched at runtime could be any match).</summary>
        // Production cost, not sale price - the raw materials a batch actually needs, market-priced and
        // divided by the recipe's own yield, matching the "crafting costs by component" convention used
        // everywhere else costs are previewed in this mod. Falls back to BaseMarketValue only when there
        // is no known recipe at all (found-only items - nothing better to go on).
        public static float SlotUnitCost(Def d)
        {
            ThingDef td = d as ThingDef ?? (d is LoadoutGenericDef gd ? RepresentativeIconDef(gd) : null);
            if (td == null) return 0f;
            RecipeDef r = GetRecipeFor(td);
            if (r == null) return td.BaseMarketValue;
            int yield = r.products != null && r.products.Count > 0 ? Mathf.Max(1, r.products[0].count) : 1;
            List<ThingDefCountClass> ingredients = GetRecipeIngredients(r);
            if (ingredients.Count == 0) return td.BaseMarketValue;
            float ingredientCost = ingredients.Sum(i => i.thingDef.BaseMarketValue * i.count);
            return ingredientCost / yield;
        }

        // Memoized permanently, NOT on the 5s TTL its neighbour IsAmmoResearched uses - and the difference is
        // the point (2026-08-09). That one has to expire because research finishing mid-game changes its
        // answer. This one only ever reads def data (RecipeDef.products/recipeUsers, ThingDef.recipes), which
        // is loaded once at startup and immutable for the rest of the game process - it deliberately does NOT look
        // at which benches are actually BUILT (that is the caller's own AllColonyBenches check, still live
        // every call), so nothing a player does in game can change what this returns.
        //
        // Why it needed one at all: a full DefDatabase<RecipeDef> scan, plus a full DefDatabase<ThingDef> scan
        // for every recipe without recipeUsers - and the hot callers ask per weapon per row PER FRAME
        // (Dialog_SquadSetup.DrawSupplyReqGroup runs three of them per visible colonist row, and
        // BenchTypeNames is evaluated eagerly as a tooltip argument). Same symptom and same fix as
        // IsAmmoResearched's own cache; this sibling was simply missed at the time.
        //
        // The cached List is handed out by reference - verified at the time of writing that all six call sites
        // only read it (Contains/Count/Select). Anything that needs to mutate must copy first.
        private static readonly Dictionary<ThingDef, List<ThingDef>> workbenchesForCache = new Dictionary<ThingDef, List<ThingDef>>();

        public static List<ThingDef> GetWorkbenchesFor(ThingDef ammoDef)
        {
            // Null guard is load-bearing, not decoration: Dictionary throws on a null key, while the uncached
            // version this replaces simply returned an empty list - and WeaponBenchExists/BenchExistsFor both
            // hand their argument straight through without checking it.
            if (ammoDef == null) return new List<ThingDef>();
            if (workbenchesForCache.TryGetValue(ammoDef, out List<ThingDef> cached)) return cached;
            List<ThingDef> benches = new List<ThingDef>();
            foreach (var recipe in DefDatabase<RecipeDef>.AllDefs)
            {
                if (recipe.products != null && recipe.products.Any(p => p.thingDef == ammoDef))
                {
                    if (recipe.recipeUsers != null)
                    {
                        foreach (var ru in recipe.recipeUsers)
                        {
                            if (!benches.Contains(ru)) benches.Add(ru);
                        }
                    }
                    else
                    {
                        foreach (var thingDef in DefDatabase<ThingDef>.AllDefs)
                        {
                            if (thingDef.recipes != null && thingDef.recipes.Contains(recipe))
                            {
                                if (!benches.Contains(thingDef)) benches.Add(thingDef);
                            }
                        }
                    }
                }
            }
            workbenchesForCache[ammoDef] = benches;
            return benches;
        }

        // Comma-joined display names of every bench TYPE that could produce this def, for the "No bench"
        // warning (2026-08-02: it never said which bench was actually missing, even though
        // GetWorkbenchesFor already has that exact def list on hand).
        public static string BenchTypeNames(ThingDef d)
        {
            List<ThingDef> benchDefs = GetWorkbenchesFor(d);
            return benchDefs.Count > 0 ? string.Join(", ", benchDefs.Select(b => b.LabelCap.ToString())) : "?";
        }

        // Die Forschung(en), die dieser Def noch fehlen - fuer das REZEPT und fuer die noetige WERKBANK.
        // Leerer String, wenn alles erforscht ist.
        //
        // Der Werkbank-Teil ist der eigentliche Punkt (2026-08-09, aus einem Neulings-Durchlauf): geprueft
        // wurde bisher nur, ob das REZEPT erforscht ist, nie ob die Bank dafuer ueberhaupt schon erforschbar
        // ist. In den ersten Spieltagen sah man deshalb einen Bildschirm, auf dem jede Munitionszeile
        // gesperrt war, "Confirm & supply" mit "nichts ausgewaehlt" antwortete - und nirgends stand, dass man
        // erst Machining braucht. Der Name der BANK wurde genannt, das noetige Forschungsprojekt nie; im
        // ganzen Mod kam kein einziges namentlich vor. Das ist der klassische Moment, in dem ein Mod wieder
        // deinstalliert wird.
        public static string MissingResearchFor(ThingDef d)
        {
            if (d == null) return "";
            List<string> missing = new List<string>();
            void Add(ResearchProjectDef p)
            {
                if (p == null || p.IsFinished) return;
                string s = p.LabelCap.ToString();
                if (!missing.Contains(s)) missing.Add(s);
            }
            RecipeDef r = GetRecipeFor(d);
            if (r != null)
            {
                Add(r.researchPrerequisite);
                if (r.researchPrerequisites != null) foreach (ResearchProjectDef p in r.researchPrerequisites) Add(p);
            }
            // Und die Baenke: es genuegt EINE erforschte Bank, also nur melden, wenn KEINE davon schon
            // verfuegbar ist - sonst stuende dort "brauchst Forschung X", obwohl man laengst bauen koennte.
            List<ThingDef> benches = GetWorkbenchesFor(d);
            if (benches.Count > 0 && !benches.Any(b => b.researchPrerequisites == null || b.researchPrerequisites.All(p => p == null || p.IsFinished)))
                foreach (ThingDef b in benches)
                    if (b.researchPrerequisites != null) foreach (ResearchProjectDef p in b.researchPrerequisites) Add(p);
            return missing.Count > 0 ? string.Join(", ", missing.ToArray()) : "";
        }

        private static readonly Dictionary<ThingDef, bool> ammoResearchedCache = new Dictionary<ThingDef, bool>();
        private static float ammoResearchedCacheTime = -99f;

        public static bool IsAmmoResearched(ThingDef ammoDef)
        {
            // This is a full DefDatabase<RecipeDef> scan - and hot UI paths ask it per ammo variant per row PER
            // FRAME, which on modded lists means six-figure iterations every frame and a visible FPS drop with the
            // tab open. Cached with a short TTL so a research finishing mid-game still shows up within seconds.
            if (Time.realtimeSinceStartup - ammoResearchedCacheTime > 5f)
            {
                ammoResearchedCache.Clear();
                ammoResearchedCacheTime = Time.realtimeSinceStartup;
            }
            if (ammoResearchedCache.TryGetValue(ammoDef, out bool cached)) return cached;

            bool researched = false;
            foreach (var recipe in DefDatabase<RecipeDef>.AllDefs)
            {
                if (recipe.products != null && recipe.products.Any(p => p.thingDef == ammoDef) && RecipeIsResearched(recipe))
                {
                    researched = true;
                    break;
                }
            }
            ammoResearchedCache[ammoDef] = researched;
            return researched;
        }

        // ---- "Do not pick up" loot (2026-08-09) -------------------------------------------------------
        // RimWorld's own Forbidden flag. Raid and mech-cluster drops carry it automatically until the colony
        // claims them, and the player can set it by hand on anything.
        //
        // This REPLACES the old IsColonyOwned(t) => !t.IsForbidden gate, which every map-wide stock scan in
        // this mod used to filter by. That gate meant the twenty assault rifles lying in front of the door
        // right after a raid - the exact moment somebody opens this mod - counted nowhere at all, and the
        // availability column simply read empty with no hint that anything had been filtered out.
        // The decision: count them everywhere, UNIFORMLY, for every kind
        // of item; list them separately in the tooltip; and put a "!" on every display that shows stock, so
        // it stays obvious that part of what is being counted still has to be released before anyone can
        // touch it. The old gate's stated worry - a visiting trade caravan's goods, an unclaimed quest-reward
        // container - does not actually reach these scans: PlayerMaps() is IsPlayerHome-only, listerThings
        // returns SPAWNED things only, and trader/caravan goods live inside pawn inventories and containers,
        // which are not spawned. What is left over that carries the flag on a home map is precisely the loot
        // the player wants to see.
        public static bool IsMarkedDoNotPickUp(Thing t) => t != null && t.IsForbidden(Faction.OfPlayer);

        // Im NEBEL liegende Dinge zaehlen NIRGENDS als Bestand (2026-08-28, Geister-Gegenstaende:
        // ein Persona-Monoschwert mit Verfuegbarkeit 1 und "!", das weder auf der Karte noch ueber
        // RimWorlds Suche zu finden war - in einer frischen Kolonie, also bei noch grossem Nebel).
        // listerThings fuehrt auch, was in noch unaufgedeckten Bereichen liegt (Ancient Danger, verschuettete
        // Ruinen, Odyssey-Strukturen), und solche Beute ist obendrein immer "nicht aufsammeln" - daher das "!".
        // RimWorlds eigene Auswahl und Suche blenden genau das aus (ThingSelectionUtility.SelectableByMapClick
        // -> Position.Fogged). Ein Bestand, den der Spieler weder sehen noch holen kann, ist keiner; und die
        // "!"-Anleitung "erst freigeben" waere dort falsch, weil es nichts freizugeben gibt.
        // Gilt fuer JEDEN listerThings-Scan des Mods (alle Stellen am selben Tag umgestellt, grep
        // `listerThings.` findet sie). Getragenes ist nie gespawnt und faellt hier ueber !Spawned durch.
        public static bool IsHiddenInFog(Thing t)
        {
            if (t == null || !t.Spawned) return false;
            Map map = t.Map;
            return map != null && map.fogGrid != null && map.fogGrid.IsFogged(t.Position);
        }

        // How much of `def` sits spawned on a colony map marked "do not pick up". Already INCLUDED in every
        // stock number the mod shows - this is the subset, computed purely so the tooltip can list it on its
        // own line and DrawForbiddenMark can put the "!" on the bar. A generic ("Any Medicine") sums its
        // whole family, same as GenericStockCount.
        // Per-frame cache, same reasoning as ComputeItemOwnership's own (this is read once per availability
        // bar per row per frame, and each miss is a full empire scan).
        private static readonly Dictionary<Def, int> forbiddenStockCache = new Dictionary<Def, int>(DefRefComparer.Instance);
        private static int forbiddenStockCacheFrame = -1;

        public static int ForbiddenStockCount(Def def)
        {
            if (def == null) return 0;
            if (Time.frameCount != forbiddenStockCacheFrame)
            {
                forbiddenStockCache.Clear();
                forbiddenStockCacheFrame = Time.frameCount;
            }
            if (forbiddenStockCache.TryGetValue(def, out int cached)) return cached;

            ThingDef td = def as ThingDef;
            List<ThingDef> concretes = td != null ? new List<ThingDef> { td } : GenericFamily(def as LoadoutGenericDef);
            int n = 0;
            foreach (Map map in PlayerMaps())
                foreach (ThingDef c in concretes)
                {
                    List<Thing> things = map.listerThings.ThingsOfDef(c);
                    if (things == null) continue;
                    foreach (Thing t in things)
                    {
                        // A colonist's own equipped/carried gear is never "loot lying around" - the Thing can
                        // still carry the flag from before it was picked up, and counting that would put a "!"
                        // on a weapon somebody is holding right now.
                        if (IsHiddenInFog(t) || t.ParentHolder is Pawn_EquipmentTracker || t.ParentHolder is Pawn_InventoryTracker || t.ParentHolder is Pawn_CarryTracker)
                            continue;
                        if (IsMarkedDoNotPickUp(t)) n += t.stackCount;
                    }
                }
            forbiddenStockCache[def] = n;
            return n;
        }

        // "12 knives" never said WHICH knives - steel, plasteel or jade (2026-08-09: "which material do
        // I actually have"). Counts the colony's
        // stock of this def per material, spawned map things plus what colonists carry, and returns
        // "Steel: 7\nPlasteel: 3\nJade: 2".
        //
        // Deliberately silent in the two cases where the line would be noise rather than information: a def
        // that isn't MadeFromStuff at all (ammo, medicine, food - most of what these tooltips cover), and a
        // stock that is all one material, where the breakdown would just repeat the total under it.
        //
        // Scoped to the shared location tooltip rather than to one screen (the item proposed starting with
        // Supply Loadouts alone): BuildLocationTooltip is the ONE place every availability bar's "where is
        // it" block comes from, so hanging it there gets every screen at once and cannot drift apart later.
        public static string BuildStuffBreakdownLines(Def def)
        {
            ThingDef td = def as ThingDef;
            List<ThingDef> concretes = td != null ? new List<ThingDef> { td } : GenericFamily(def as LoadoutGenericDef);
            if (concretes.Count == 0 || !concretes.Any(c => c != null && c.MadeFromStuff)) return null;

            Dictionary<string, int> byStuff = new Dictionary<string, int>();
            void Count(Thing t)
            {
                if (t?.Stuff == null) return;
                string k = t.Stuff.LabelCap;
                byStuff[k] = (byStuff.TryGetValue(k, out int n) ? n : 0) + t.stackCount;
            }
            HashSet<Thing> seen = new HashSet<Thing>();
            foreach (Map map in PlayerMaps())
                foreach (ThingDef c in concretes)
                {
                    if (c == null || !c.MadeFromStuff) continue;
                    List<Thing> things = map.listerThings.ThingsOfDef(c);
                    if (things == null) continue;
                    foreach (Thing t in things) if (!IsHiddenInFog(t) && seen.Add(t)) Count(t);
                }
            HashSet<ThingDef> concreteSet = new HashSet<ThingDef>(concretes.Where(c => c != null && c.MadeFromStuff));
            foreach (Pawn p in AllColonyPawns())
            {
                if (p.equipment?.Primary != null && concreteSet.Contains(p.equipment.Primary.def) && seen.Add(p.equipment.Primary))
                    Count(p.equipment.Primary);
                if (p.inventory?.innerContainer != null)
                    foreach (Thing t in p.inventory.innerContainer)
                        if (concreteSet.Contains(t.def) && seen.Add(t)) Count(t);
                if (p.apparel?.WornApparel != null)
                    foreach (Apparel a in p.apparel.WornApparel)
                        if (concreteSet.Contains(a.def) && seen.Add(a)) Count(a);
            }
            if (byStuff.Count < 2) return null;
            return JoinCapped(byStuff.OrderByDescending(kv => kv.Value).Select(kv => kv.Key + ": " + kv.Value));
        }

        // BuildForbiddenLocationLines ist am 2026-08-15 ersatzlos entfallen - zusammen mit dem
        // Tooltip-Abschnitt, den sie als einzige gefuellt hat (siehe BuildLocationTooltip oben: einmal
        // reicht). Ihr letzter Aufrufer war genau dieser Abschnitt; nach dessen Wegfall
        // stand sie ohne einen einzigen Aufrufer im Mod. Mitentfernt statt liegengelassen, weil genau
        // solche Reste spaeter wie ein vergessener Fix aussehen.
        // Die Keyed-Schluessel CER_LoadoutUtility_LocationTooltipForbidden und
        // CER_LoadoutUtility_ForbiddenOnGround bleiben in allen 13 Sprachen liegen: ein Zurueckdrehen
        // waere damit eine Funktion und keine 13 Dateien.

        // The "!" that goes on every stock display carrying flagged units. Drawn as an overlay in the
        // top-right of whatever rect shows the stock (bar, icon, number cell) so it reads the same
        // everywhere - exactly one marker shape, not a per-screen improvisation.
        public static readonly Color ForbiddenMarkColor = new Color(1f, 0.68f, 0.2f);

        // Text form of the same marker, for stock displays that are a LABEL rather than a rect we own
        // (FloatMenu entries, inline "N in stock" strings). Same glyph and same color as DrawForbiddenMark
        // so the two never read as two different warnings.
        public const string ForbiddenMarkRichText = "<color=#FFAD33>!</color>";

        public static string AppendForbiddenMark(string label, int forbidden)
            => forbidden > 0 ? label + " " + ForbiddenMarkRichText : label;

        public static void DrawForbiddenMark(Rect host, int forbidden, bool tooltip = true)
        {
            if (forbidden <= 0) return;
            float s = Mathf.Clamp(Mathf.Min(host.width, host.height), 10f, 14f);
            DrawForbiddenMarkAt(new Rect(host.xMax - s + 1f, host.y - 1f, s, s), forbidden, tooltip);
        }

        // Same glyph at an explicitly given rect - for hosts whose top-right corner is already taken by
        // another badge (e.g. the readiness widget's own "impossible" warning sits exactly there).
        public static void DrawForbiddenMarkAt(Rect r, int forbidden, bool tooltip = true)
        {
            if (forbidden <= 0) return;
            // Kein Text mehr (2026-08-12: das Rufezeichen war unberechenbar und kommt
            // an vielen Stellen vor): die Plakette skaliert mit dem Host (DrawForbiddenMark clampt s auf
            // 10..14), eine Schrift-Glyphe tut das nicht - GameFont.Tiny hat eine feste Zeilenboxhoehe.
            // Der MiddleCenter-Fix vom 2026-08-10 hat das Herausragen beseitigt, aber nicht die Ursache: die
            // Strichstaerke blieb von der Schriftart abhaengig und damit ueber die Groessen hinweg
            // ungleichmaessig. Jetzt dieselbe gezeichnete Geometrie wie in der Warn-Plakette, also bei jeder
            // Plakettengroesse proportional gleich - und beide Ausrufezeichen im Mod sehen endlich gleich aus.
            // Dunkle Platte bleibt: die Markierung liegt auf beliebig gefaerbten Balkensegmenten, und ein
            // blankes oranges Zeichen auf den beigen/gelben Segmenten war unlesbar.
            Widgets.DrawBoxSolid(r, new Color(0.08f, 0.08f, 0.09f, 0.75f));
            DrawExclamationGlyph(r, ForbiddenMarkColor);
            if (tooltip) TooltipHandler.TipRegion(r, "CER_LoadoutUtility_ForbiddenMarkTip".Translate(forbidden.ToString()));
        }

        // Records that WE generated this Weapon Loadout template (Dialog_GenerateLoadouts' own creation
        // site) - the reliable half of IsManagedByUs's check, immune to the player later renaming the
        // loadout away from the configurable "W|" prefix.
        public static void MarkAsManaged(Loadout lo)
        {
            if (lo == null) return;
            CEReloadedGameComponent comp = CEReloadedGameComponent.Cached;
            comp?.managedLoadoutIds.Add(lo.UniqueID);
        }

        // A plain Weapon Loadout TEMPLATE this mod generated - tracked ID (MarkAsManaged) OR, only as a
        // fallback for loadouts generated before that tracking existed, the configurable "W|" prefix
        // convention. Deliberately narrower than IsManagedByUs below: does NOT include Roles or personal
        // anchors - some callers (e.g. hiding raw templates from CE's native quick-pick so a player doesn't
        // accidentally bypass our tooling) must NOT sweep up Roles, which are legitimately pickable there.
        public static bool IsGeneratedTemplate(Loadout lo)
        {
            if (lo == null) return false;
            CEReloadedGameComponent comp = CEReloadedGameComponent.Cached;
            if (comp != null && comp.managedLoadoutIds.Contains(lo.UniqueID)) return true;
            // Notnagel fuer Altbestand: Loadouts, die VOR managedLoadoutIds (2026-08-01) entstanden sind,
            // haben nur ihre Vorsilbe im Etikett. Seit 2026-08-16 ohne die Einstellung zu fragen - die steht
            // jetzt standardmaessig auf "keine Vorsilbe", und dann waere dieser Notnagel genau fuer die
            // Spielstaende tot, fuer die es ihn ueberhaupt gibt (Bedingung: nur, solange intern noch
            // bekannt ist, was es ist).
            // Notnagel ENTFALLEN (2026-08-17): der Altbestand wird beim Laden einmal nachgetragen
            // (CEReloadedGameComponent.AdoptMarkersOnce), danach fragt niemand mehr das Etikett.
            return false;
        }

        // ============ Gesperrte Waffen (2026-09-03) ============
        // Spielstand-Zustand im GameComponent (defNames); die Automatik (Kandidatenaufbau) meidet
        // Gesperrtes, das Auswahlfenster zeigt es ausgegraut am Gruppenende mit Entsperr-Schalter.
        public static bool IsWeaponDisabled(ThingDef d)
        {
            if (d == null) return false;
            CEReloadedGameComponent comp = CEReloadedGameComponent.Cached;
            return comp != null && comp.disabledWeapons.Contains(d.defName);
        }

        public static void SetWeaponDisabled(ThingDef d, bool disabled)
        {
            if (d == null) return;
            CEReloadedGameComponent comp = CEReloadedGameComponent.Cached;
            if (comp == null) return;
            if (disabled) comp.disabledWeapons.Add(d.defName);
            else comp.disabledWeapons.Remove(d.defName);
        }

        // ---- Gesperrte ROLLEN und LOADOUTS (2026-09-04, "auch rollen und loadouts sperren ... mit allen
        // implikationen") ----------------------------------------------------------------------------
        // Gleiche Bauform wie oben, nur ueber die UniqueID statt den defName. Wer hier sperrt, sperrt das
        // Objekt - Umbenennen aendert nichts daran, und ein geloeschtes Loadout nimmt seinen Eintrag mit
        // (LoadoutDeletionUtility.ForgetSideBooks), weil CE seine IDs wiederverwendet.
        public static bool IsLoadoutDisabled(Loadout lo)
        {
            if (lo == null || lo.defaultLoadout) return false;
            CEReloadedGameComponent comp = CEReloadedGameComponent.Cached;
            return comp != null && comp.disabledLoadouts.Count > 0 && comp.disabledLoadouts.Contains(lo.UniqueID);
        }

        public static void SetLoadoutDisabled(Loadout lo, bool disabled)
        {
            if (lo == null || lo.defaultLoadout) return;
            CEReloadedGameComponent comp = CEReloadedGameComponent.Cached;
            if (comp == null) return;
            if (disabled) comp.disabledLoadouts.Add(lo.UniqueID);
            else comp.disabledLoadouts.Remove(lo.UniqueID);
        }

        // Wieviele Kolonisten haengen laut ZUWEISUNG an etwas Gesperrtem? Fuer die Neuzuweisungs-Nachfrage
        // beim Schliessen des Auswahlfensters. Drei Faelle, ein Zaehler:
        //   - eine gesperrte WAFFE irgendwo in der Slots-Vereinigung (Hauptwaffe, Zweitwaffe, Rolleninhalt),
        //   - die zugewiesene Vorlage SELBST ist gesperrt (auch ueber den persoenlichen Anker hinweg -
        //     deshalb DisplayLoadout, sonst sieht man bei jedem personalisierten Kolonisten nur den Anker),
        //   - eine gesperrte Vorlage haengt als Zusatz-Elternteil am Anker (Pack, Zweitwaffe, Rolle).
        // Der Name bleibt, damit die Aufrufstellen nicht alle angefasst werden mussten; er heisst
        // inzwischen "gesperrtes IRGENDWAS".
        public static int DisabledWeaponCarrierCount()
        {
            CEReloadedGameComponent comp = CEReloadedGameComponent.Cached;
            if (comp == null || (comp.disabledWeapons.Count == 0 && comp.disabledLoadouts.Count == 0)) return 0;
            int n = 0;
            foreach (Pawn p in AllColonyPawns())
            {
                Loadout raw = p.GetLoadout();
                if (raw == null || raw.defaultLoadout) continue;
                if (comp.disabledLoadouts.Count > 0)
                {
                    Loadout shown = AmmoAssignmentUtility.DisplayLoadout(raw);
                    if (IsLoadoutDisabled(shown) || IsLoadoutDisabled(raw)) { n++; continue; }
                    bool viaParent = false;
                    foreach (int pid in MultiParentUtility.GetExtraParentIds(raw))
                        if (comp.disabledLoadouts.Contains(pid)) { viaParent = true; break; }
                    if (viaParent) { n++; continue; }
                }
                if (comp.disabledWeapons.Count == 0 || raw.Slots == null) continue;
                foreach (LoadoutSlot s in raw.Slots)
                    if (s.thingDef != null && s.thingDef.IsWeapon && comp.disabledWeapons.Contains(s.thingDef.defName)) { n++; break; }
            }
            return n;
        }

        // An Equipment Pack generated by Dialog_GenerateEquipmentPack's own wizard (MarkAsManaged, called at
        // its creation site) rather than hand-built in the Ingredients tab's manual "New Pack" builder - no
        // prefix fallback here unlike IsGeneratedTemplate above: Packs use their own always-reliable
        // EquipmentPackUtility marker prefix for "is this a Pack AT ALL", which says nothing about which of
        // the two creation paths made it, so a pack that predates this tracking (added 2026-08-01) simply
        // reads as hand-made by default - the safer failure mode for a destructive "delete only generated
        // ones" scope than guessing wrong and deleting something the player actually built themselves.
        public static bool IsGeneratedPack(Loadout lo)
        {
            if (lo == null) return false;
            CEReloadedGameComponent comp = CEReloadedGameComponent.Cached;
            return comp != null && comp.managedLoadoutIds.Contains(lo.UniqueID);
        }

        // "Welches Pack gehoert zu diesem Generator-Plan" - ueber eine feste Kennung statt ueber den
        // Namenstext (2026-08-09: nicht mehr ueber den String identifizieren, sondern ueber eine
        // Kennung).
        // Der Name bleibt als ZWEITE Stufe drin, und das ist Absicht, keine Halbherzigkeit: Packs, die vor
        // dieser Aufzeichnung entstanden sind, haben keinen Eintrag, und die sollen weiterhin gefunden statt
        // ein zweites Mal angelegt werden. Neue Packs bekommen ihren Eintrag bei der Erzeugung, ab da
        // ueberlebt die Zuordnung jedes Umbenennen.
        public static Loadout FindGeneratedPackForPlan(string planName, string fullLabel)
        {
            CEReloadedGameComponent comp = CEReloadedGameComponent.Cached;
            if (comp != null && !string.IsNullOrEmpty(planName) && comp.packPlanIds.TryGetValue(planName, out int id))
            {
                Loadout byId = LoadoutManager.Loadouts.FirstOrDefault(l => l != null && !l.defaultLoadout && l.UniqueID == id);
                // Weg (geloescht) -> Eintrag aufraeumen und auf die Namenssuche zurueckfallen, sonst bleibt
                // eine tote Kennung ewig stehen und blockiert das Neuanlegen.
                if (byId != null) return byId;
                comp.packPlanIds.Remove(planName);
            }
            return LoadoutManager.Loadouts.FirstOrDefault(l => l != null && !l.defaultLoadout && l.label == fullLabel);
        }

        public static void RecordGeneratedPackForPlan(string planName, Loadout pack)
        {
            if (pack == null || string.IsNullOrEmpty(planName)) return;
            CEReloadedGameComponent comp = CEReloadedGameComponent.Cached;
            if (comp == null) return;
            comp.packPlanIds[planName] = pack.UniqueID;
        }

        // The single, canonical "is this loadout something OUR system manages" check - consolidates what
        // used to be 5+ separately hand-rolled versions of the same question scattered across this mod
        // (2026-07-30 harmonization pass). True for: a personal "[Pawn-Loadout]" anchor, a "[Role]" loadout,
        // or a generated Weapon Loadout template (IsGeneratedTemplate). Equipment Packs are NOT included
        // here: they have their own always-reliable marker (EquipmentPackUtility.IsEquipmentPackLoadout)
        // and are a different kind of "ours" (supply, never a main weapon assignment) than what callers of
        // this check actually care about.
        public static bool IsManagedByUs(Loadout lo)
        {
            if (lo == null) return false;
            return AmmoAssignmentUtility.IsAmmoLink(lo) || RoleUtility.IsRoleLoadout(lo) || IsGeneratedTemplate(lo);
        }

        // A real, player-facing Weapon Loadout - not a personal anchor, not an Equipment Pack, not a Role,
        // and not the default (empty) loadout. Consolidates a filter that was independently hand-rolled in
        // 3+ places (Dialog_ArmColony's "Delete weapon loadouts" scope, Dialog_GenerateLoadouts.
        // ExistingLoadouts/the Role Mode source list, Dialog_SquadSetup's grenade-candidate scan) - every
        // one wanted the exact same "just the plain templates" set, just written out fresh each time.
        public static bool IsPlainWeaponLoadout(Loadout lo)
        {
            if (lo == null || lo.defaultLoadout) return false;
            return !AmmoAssignmentUtility.IsAmmoLink(lo) && !EquipmentPackUtility.IsEquipmentPackLoadout(lo) && !RoleUtility.IsRoleLoadout(lo);
        }

        // Every map the player actually colonizes - the correct scope for "does the colony have X" instead of
        // whichever single map the camera happens to be looking at. A multi-base playthrough has more than one
        // of these; a caravan-formed, quest, or hostile map is never included (Map.IsPlayerHome already excludes
        // those). This is the fix for the "Arm my Colony says nothing's available while standing on an outpost
        // with no bench, even though the main base is fully stocked" gap - every scan below now checks the whole
        // empire, not just the current camera position.
        public static IEnumerable<Map> PlayerMaps()
        {
            if (Find.Maps == null) yield break;
            foreach (Map m in Find.Maps)
                if (m.IsPlayerHome) yield return m;
        }

        // Every free colonist in the empire - every home map's FreeColonists plus every player-controlled
        // caravan's own colonists. A pawn's loadout is data, not tied to whichever map they physically stand
        // on right now, so assignment/counting logic should see all of them, not just the viewed map.
        public static IEnumerable<Pawn> AllColonyPawns()
        {
            foreach (Map map in PlayerMaps())
                foreach (Pawn p in map.mapPawns.FreeColonists)
                    yield return p;
            if (Find.WorldObjects != null)
                foreach (RimWorld.Planet.Caravan c in Find.WorldObjects.Caravans)
                {
                    if (!c.IsPlayerControlled) continue;
                    foreach (Pawn p in c.PawnsListForReading)
                        if (p.IsFreeColonist) yield return p;
                }
        }

        // Every colonist-built crafting bench across every home map - production isn't limited to whichever
        // map is currently in view.
        // GECACHT seit 2026-08-13 (in einer Messung als teuerster Einzelposten aufgefallen, danach an RimWorlds
        // ListerBuildings nachgeprueft): `allBuildingsColonist` enthaelt JEDES Spielergebaeude - Waende, Tueren,
        // Leitungen, Moebel. In einer gewachsenen Kolonie sind das vierstellig viele Eintraege, und `OfType`
        // laeuft ueber alle, um eine Handvoll Werkbaenke herauszufischen.
        // Warum das weh tat: die 31 Aufrufstellen haengen fast alle an Herstellbarkeits-Pruefungen
        // (IsProducible, BenchExistsFor, ProducibleNow), und die laufen pro Gegenstand pro Zeile pro Bild -
        // bei zwoelf sichtbaren Kolonisten mit je acht Bedarfsposten waren das mehrere hundert Voll-Scans
        // PRO BILD, jeder mit zwei Iterator-Allokationen.
        // Frame-Cache, kein TTL: eine Werkbank kann innerhalb eines Bildes weder entstehen noch verschwinden,
        // und Time.frameCount ist derselbe Schluessel, den die Nachbar-Caches hier schon benutzen. Als LISTE,
        // nicht als Iterator - genau die Allokation soll ja weg.
        private static List<Building_WorkTable> benchCache;
        private static int benchCacheFrame = -1;
        public static List<Building_WorkTable> AllColonyBenches()
        {
            if (benchCacheFrame == Time.frameCount && benchCache != null) return benchCache;
            List<Building_WorkTable> list = new List<Building_WorkTable>();
            foreach (Map map in PlayerMaps())
                foreach (Building b in map.listerBuildings.allBuildingsColonist)
                    if (b is Building_WorkTable wt) list.Add(wt);
            benchCache = list;
            benchCacheFrame = Time.frameCount;
            return benchCache;
        }

        // Does this specific pawn already personally have this def (equipped, carried in inventory, or worn as
        // apparel) right now? A map-wide stock scan (ComputeAvailability, IngredientStockCount) never counts a
        // colonist's own gear as "stock", so a pawn who already owns exactly what their planned loadout wants can
        // still read as a shortfall unless callers check this first - see Dialog_SupplyLoadouts.ComputeUnfulfilled,
        // the real production step's own version of this same check.
        public static bool PawnAlreadyHasItem(Pawn pawn, ThingDef def)
        {
            if (pawn == null || def == null) return false;
            if (pawn.equipment != null && pawn.equipment.Primary != null && pawn.equipment.Primary.def == def) return true;
            if (pawn.inventory != null && pawn.inventory.innerContainer != null && pawn.inventory.innerContainer.Any(t => t.def == def)) return true;
            if (pawn.apparel != null && pawn.apparel.WornApparel != null && pawn.apparel.WornApparel.Any(a => a.def == def)) return true;
            return false;
        }

        // Same question as PawnAlreadyHasItem, but for a generic slot ("Any Medicine") - any concrete def the
        // generic's own matcher accepts satisfies it, not just one arbitrarily-picked representative.
        public static bool PawnAlreadyHasGeneric(Pawn pawn, LoadoutGenericDef gd)
        {
            if (pawn == null || gd == null || pawn.inventory?.innerContainer == null) return false;
            return pawn.inventory.innerContainer.Any(t => t?.def != null && gd.lambda != null && gd.lambda(t.def));
        }

        // Wie viele davon traegt der Pawn WIRKLICH - die Mengenversion der beiden Ja/Nein-Fragen darueber
        // (2026-08-09). Die reine Ja/Nein-Antwort liess einen Kolonisten mit 1 von 5 geforderten Medizin als
        // vollstaendig ausgeruestet gelten: die Anzeige war gruen, im Kampf war nach dem ersten Verletzten
        // Schluss, und weil es als erfuellt galt, wurde auch kein Nachschub eingeplant - der Fehler versteckte
        // sich also selbst. stackCount, nicht Anzahl der Stapel: fuenf Medizin liegen als EIN Stapel im
        // Inventar, "gibt es einen Eintrag" haette dieselbe falsche Antwort gegeben.
        public static int PawnCarriedCount(Pawn pawn, ThingDef def)
        {
            if (pawn == null || def == null) return 0;
            int n = 0;
            if (pawn.equipment?.Primary != null && pawn.equipment.Primary.def == def) n += 1;
            if (pawn.inventory?.innerContainer != null)
                foreach (Thing t in pawn.inventory.innerContainer) if (t?.def == def) n += t.stackCount;
            if (pawn.apparel?.WornApparel != null)
                foreach (Apparel a in pawn.apparel.WornApparel) if (a?.def == def) n += 1;
            n += MagazineAmmoCount(pawn, d => d == def);
            return n;
        }

        public static int PawnCarriedGenericCount(Pawn pawn, LoadoutGenericDef gd)
        {
            if (pawn == null || gd == null || gd.lambda == null || pawn.inventory?.innerContainer == null) return 0;
            int n = 0;
            foreach (Thing t in pawn.inventory.innerContainer) if (t?.def != null && gd.lambda(t.def)) n += t.stackCount;
            n += MagazineAmmoCount(pawn, d => gd.lambda(d));
            return n;
        }

        // Munition IM MAGAZIN zaehlt als getragen (2026-08-30, im Test: 'Fetch gear now laesst zwei Kolonisten aus,
        // obwohl alles gruen ist': Kachnovo hatte 144 Schuss im Inventar und 6 im Gewehr, das Loadout sagt 150 - CE sah
        // nichts zu holen, unsere Zellen und die Vorschau 'still to fetch: 6'). CEs GetStorageByThingDef zaehlt
        // CurMagCount der Hauptwaffe UND jeder Waffe im Inventar zur Munition dazu; jede Zaehlung hier, die mit CEs
        // Abholen verglichen wird, muss dasselbe tun - sonst zeigt der Mod eine Fehlmenge, die CE nie holen wird.
        public static int MagazineAmmoCount(Pawn pawn, Func<ThingDef, bool> match)
        {
            if (pawn == null || match == null) return 0;
            int n = 0;
            void Add(Thing weapon)
            {
                CompAmmoUser au = weapon?.TryGetComp<CompAmmoUser>();
                if (au != null && au.UseAmmo && au.CurrentAmmo != null && match(au.CurrentAmmo)) n += au.CurMagCount;
            }
            if (pawn.equipment?.Primary != null) Add(pawn.equipment.Primary);
            if (pawn.inventory?.innerContainer != null)
                foreach (Thing t in pawn.inventory.innerContainer) if (t?.def != null && t.def.IsWeapon) Add(t);
            return n;
        }

        public static List<ThingDef> GetAllOwnedWeapons()
        {
            List<ThingDef> foundWeapons = new List<ThingDef>();

            foreach (Map map in PlayerMaps())
                foreach (Thing thing in map.listerThings.ThingsInGroup(ThingRequestGroup.Weapon))
                {
                    // Forbidden loot included (2026-08-09, see IsMarkedDoNotPickUp) - a raid's dropped rifles
                    // are the whole reason someone opens this list right after a fight.
                    // IsRealWeaponDef, not IsWeapon: the lister's Weapon group also holds resources with
                    // melee tools (wood), and "owned" fed them straight into the Available filter.
                    if (!IsHiddenInFog(thing) && IsRealWeaponDef(thing.def) && !foundWeapons.Contains(thing.def))
                    {
                        foundWeapons.Add(thing.def);
                    }
                }

            foreach (Pawn pawn in AllColonyPawns())
            {
                if (pawn.equipment != null && pawn.equipment.Primary != null)
                {
                    ThingDef wDef = pawn.equipment.Primary.def;
                    if (IsRealWeaponDef(wDef) && !foundWeapons.Contains(wDef))
                    {
                        foundWeapons.Add(wDef);
                    }
                }

                if (pawn.inventory != null && pawn.inventory.innerContainer != null)
                {
                    foreach (Thing t in pawn.inventory.innerContainer)
                    {
                        if (IsRealWeaponDef(t.def) && !foundWeapons.Contains(t.def))
                        {
                            foundWeapons.Add(t.def);
                        }
                    }
                }

                if (pawn.carryTracker != null && pawn.carryTracker.CarriedThing != null)
                {
                    if (IsRealWeaponDef(pawn.carryTracker.CarriedThing.def) && !foundWeapons.Contains(pawn.carryTracker.CarriedThing.def))
                    {
                        foundWeapons.Add(pawn.carryTracker.CarriedThing.def);
                    }
                }
            }

            return foundWeapons;
        }

        /// <summary>
        /// Gets loadouts currently assigned to colonists anywhere in the empire (every colony map, every
        /// player-controlled caravan). Safe replacement for the non-existent LoadoutManager.AssignedLoadouts.
        /// </summary>
        public static IEnumerable<Loadout> GetAssignedLoadouts()
        {
            HashSet<int> seen = new HashSet<int>();
            foreach (Pawn pawn in AllColonyPawns())
            {
                Loadout loadout = pawn.GetLoadout();
                if (loadout != null && !loadout.defaultLoadout && seen.Add(loadout.GetHashCode()))
                {
                    yield return loadout;
                }
            }
        }

        public static string GetAmmoRecipeTooltip(ThingDef ammoDef)
        {
            System.Text.StringBuilder sb = new System.Text.StringBuilder();
            sb.Append("<b>" + ammoDef.label + "</b>");

            // Combat stats from the ammo's projectile.
            ThingDef proj = GetAmmoProjectile(ammoDef);
            if (proj != null && proj.projectile != null)
            {
                ExtractDamageAndAP(proj, out float dmg, out float ap);
                float blunt = proj.projectile is ProjectilePropertiesCE ce ? ce.armorPenetrationBlunt : 0f;
                sb.Append("CER_LoadoutUtility_AmmoTooltipDamage".Translate(dmg.ToString("F0")));
                sb.Append("CER_LoadoutUtility_AmmoTooltipSharpAP".Translate(ap.ToString("F1")));
                if (blunt > 0f) sb.Append("CER_LoadoutUtility_AmmoTooltipBluntAP".Translate(blunt.ToString("F1")));
                List<string> effects = AbilityNames(GetAmmoEffects(ammoDef));
                if (effects.Count > 0) sb.Append("CER_LoadoutUtility_AmmoTooltipEffects".Translate(string.Join(", ", effects.ToArray())));
            }

            // Tatsaechlicher Bestand (2026-08-09: der Tooltip zeigt, wie viel Munition es ueberhaupt
            // gibt). Bewusst aufgeteilt statt einer Summe - "300" ist wertlos,
            // wenn 280 davon schon getragen werden. Genau die Aufteilung, die GetAmmoOwnership ohnehin
            // liefert und die jede Verfuegbarkeits-Anzeige im Mod schon benutzt.
            GetAmmoOwnership(ammoDef, out int carriedOnly, out int _ca, out int _ais, out int freeStored, out int inProd);
            sb.Append("CER_LoadoutUtility_AmmoTooltipStock".Translate(
                (freeStored + carriedOnly).ToString(), freeStored.ToString(), carriedOnly.ToString()));
            if (inProd > 0) sb.Append("CER_LoadoutUtility_AmmoTooltipInProduction".Translate(inProd.ToString()));

            // Crafting recipe.
            RecipeDef recipe = GetRecipeFor(ammoDef);
            if (recipe != null)
            {
                float yield = recipe.products.First(p => p.thingDef == ammoDef).count;
                sb.Append("CER_LoadoutUtility_AmmoTooltipCrafts".Translate(yield.ToString()));
                foreach (var ing in recipe.ingredients)
                    sb.Append("CER_LoadoutUtility_AmmoTooltipIngredientLine".Translate(ing.GetBaseCount().ToString("F0"), ing.filter.Summary));
            }
            else
            {
                sb.Append("CER_LoadoutUtility_AmmoTooltipNoRecipe".Translate());
            }

            return sb.ToString().Replace(" ", "\u00A0");
        }

        // Hover details for a shield (or similar gear) in the generate preview: protection, burden, value.
        public static string ShieldDetailsTip(ThingDef d)
        {
            if (d == null) return "";
            System.Text.StringBuilder sb = new System.Text.StringBuilder();
            sb.Append("<b>").Append(d.LabelCap.ToString()).Append("</b>");
            GetShieldScore(d, out float shScore, out _);
            sb.Append("CER_LoadoutUtility_ShieldTooltipScore".Translate(Mathf.RoundToInt(shScore).ToString()));
            float sharp = d.GetStatValueAbstract(StatDefOf.ArmorRating_Sharp);
            float blunt = d.GetStatValueAbstract(StatDefOf.ArmorRating_Blunt);
            if (sharp > 0f || blunt > 0f)
                sb.Append("CER_LoadoutUtility_ShieldTooltipArmor".Translate(sharp.ToString("F1"), blunt.ToString("F1")));
            StatDef bulkStat = DefDatabase<StatDef>.GetNamedSilentFail("Bulk");
            sb.Append("CER_LoadoutUtility_ShieldTooltipMass".Translate(d.GetStatValueAbstract(StatDefOf.Mass).ToString("F1")));
            if (bulkStat != null) sb.Append("CER_LoadoutUtility_ShieldTooltipBulk".Translate(d.GetStatValueAbstract(bulkStat).ToString("F1")));
            sb.Append("CER_LoadoutUtility_ShieldTooltipMarketValue".Translate(d.BaseMarketValue.ToString("F0")));
            if (!d.description.NullOrEmpty()) sb.Append("\n\n").Append(d.description);
            return sb.ToString();
        }

        public static int GetAmmoRecipeYield(ThingDef ammoDef)
        {
            RecipeDef recipe = GetRecipeFor(ammoDef);
            if (recipe == null) return 1;
            return (int)recipe.products.First(p => p.thingDef == ammoDef).count;
        }

        /// <summary>
        /// Updates the inspect pane cache using a 2-frame delay to ensure the UI refreshes properly.
        public static void RefreshBillsTab(Building_WorkTable bench)
        {
            if (bench == null || bench.Destroyed) return;
            // NUR, wenn die Werkbank gerade AUSGEWAEHLT ist (2026-08-30: bei Arm my Colony ging
            // der Electric-Smelter-Auftragsreiter auf, links am Bildschirm, mit DEV-Knopf). Diese Funktion waehlte die
            // Bank bisher selbst aus, um RimWorlds Auftragsliste zum Neuzeichnen zu zwingen - und jedes Aufraeumen von
            // Auftraegen (PruneBillsFor, laeuft im Arm-my-Colony-Reset ueber ALLE Baenke) riss damit den Inspektor mit
            // der zuletzt beruehrten Bank auf, obwohl niemand sie angeklickt hatte. Der 'DEV'-Knopf ist RimWorlds
            // eigener Entwicklermodus-Reiter im Inspektor, kein Teil dieses Mods. Ist die Bank nicht ausgewaehlt, gibt
            // es keine Liste, die veralten koennte - dann passiert hier nichts.
            if (Find.Selector == null || !Find.Selector.IsSelected(bench)) return;
            var inspectPane = MainButtonDefOf.Inspect.TabWindow as MainTabWindow_Inspect;
            if (inspectPane != null && inspectPane.OpenTabType == typeof(RimWorld.ITab_Bills))
            {
                inspectPane.CloseOpenTab();
                inspectPane.OpenTabType = typeof(RimWorld.ITab_Bills);
            }
        }

        // JEDER Sprung zu einer Werkbank oeffnet auch ihre Auftragsliste (2026-08-30: "Jump to this workbench"
        // soll auch die Auftraege oeffnen, nicht nur die Werkbank auswaehlen). Wer aus einem
        // Auftrags-Fenster zu einer Bank springt, will dort den Auftrag sehen - die Bank nur auszuwaehlen kostete
        // jedes Mal einen zweiten Klick auf den Reiter. Alle Sprungknoepfe rufen deshalb diese eine Funktion.
        public static void JumpToBench(Building_WorkTable bench) => OpenBillsTab(bench);

        // JEDER Sprung zu einer Werkbank oeffnet auch ihre Auftragsliste (2026-08-30: "Jump to this workbench"
        // soll auch die Auftraege oeffnen, nicht nur die Werkbank auswaehlen). Wer aus einem
        // Auftrags-Fenster zu einer Bank springt, will dort den Auftrag sehen - die Bank nur auszuwaehlen kostete
        // jedes Mal einen zweiten Klick auf den Reiter. Alle Sprungknoepfe rufen deshalb diese eine Funktion.
        public static void OpenBillsTab(Building_WorkTable bench)
        {
            if (bench == null || bench.Destroyed) return;

            // ====== Erst aus dem Weg gehen, dann springen (2026-09-04) ======
            //
            // Befund im Test: die Kamera sprang richtig zur Wooden loading bench, die Bank wurde
            // ausgewaehlt und ihre Auftragsliste ging links unten auf - ABER der Loadout Planner blieb
            // offen und lag darueber. Weil er Klicks um sich herum schluckt, war die Auftragsliste tot.
            //
            // Der Sprung hat also genau das getan, was er sollte, und es war trotzdem nutzlos. Unsere
            // grossen Fenster setzen absorbInputAroundWindow (sonst klickt man beim Bedienen versehentlich
            // in die Welt) - und dieselbe Eigenschaft macht die eben geoeffnete Auftragsliste unbedienbar.
            //
            // Wer "zu dieser Werkbank springen" waehlt, will dorthin. Also schliessen wir vorher, was im
            // Weg steht - und zwar genau das: unsere eigenen Fenster (Namensraum, nicht Typliste, damit
            // der naechste Dialog nicht vergessen wird - dieselbe Pruefung wie in Patch_VanillaTabHider),
            // und davon nur die, die Klicks tatsaechlich abfangen. Ein Fenster, das nicht blockiert, darf
            // stehen bleiben.
            if (Find.WindowStack != null)
            {
                List<Window> blocking = Find.WindowStack.Windows
                    .Where(w => w != null && w.absorbInputAroundWindow && w.GetType().Namespace == "CEReloaded")
                    .ToList();
                foreach (Window w in blocking) w.Close(false);
            }

            // Jump-and-select rather than a plain Find.Selector.Select - a bench found via a multi-map scan
            // (AllColonyBenches) can live on a different map than the one currently in view; CameraJumper
            // switches Current.Game.CurrentMap first when needed, then selects, so this works either way.
            CameraJumper.TryJumpAndSelect(bench);
            Find.MainTabsRoot.SetCurrentTab(MainButtonDefOf.Inspect, false);

            var inspectPane = MainButtonDefOf.Inspect.TabWindow as MainTabWindow_Inspect;
            if (inspectPane != null)
            {
                inspectPane.OpenTabType = typeof(RimWorld.ITab_Bills);
            }
        }
        // Deckel fuer Selbstmunition/Wurfsprengstoff - siehe den Kommentar an der Aufrufstelle unten.
        // Max(1, ...): auch das sperrigste Wurfding faengt bei einem Stueck an, sonst waere die Zeile leer.
        private static string CappedThrowableDefault(ThingDef w, string settingValue)
        {
            if (w == null || !int.TryParse(settingValue, out int wanted) || wanted <= 0) return settingValue;
            float unitBulk = WeaponBulk(w);
            float unitMass = w.GetStatValueAbstract(StatDefOf.Mass);
            int capBulk = unitBulk > 0.01f ? Mathf.FloorToInt(0.75f * MedianCarryBulk() / unitBulk) : wanted;
            int capMass = unitMass > 0.01f ? Mathf.FloorToInt(0.75f * MedianCarryWeight() / unitMass) : wanted;
            int cap = Mathf.Max(1, Mathf.Min(capBulk, capMass));
            return Mathf.Min(wanted, cap).ToString();
        }

        public static string GetDefaultAmmoCountForWeapon(ThingDef weaponDef)
        {
            if (weaponDef == null) return CEReloadedMod.settings.defAmmoDefault;

            // A self-ammo weapon's "ammo count" IS its own carried copy count (javelins, pila, thrown
            // explosives - the weapon and the round are the same Thing). Falling through to the normal
            // magazine-ammo default here used to hand out "150" of a two-handed thrown spear per colonist -
            // a number that only makes sense for actual cartridges, not for a weapon you carry a handful of
            // and re-arm from storage/crafting. IsSelfAmmoWeapon covers BOTH thrown explosives (grenades)
            // AND true self-ammo weapons (javelins/pila, CE AmmoDef) - split into their own settings since
            // a sensible grenade count ("carry a couple") and a sensible javelin count ("carry a proper
            // handful to actually throw across a fight") are very different numbers.
            // GEDECKELT an der Tragkraft (2026-09-01: war die Vorgabe-Stapelgroesse fuer Javelins, Pila usw.
            // sinnvoll?): CEs Pila wiegen je Stueck 4.25 Bulk / 2.0 kg -
            // der nackte Selbstmunitions-Default 10 ergab 42.5 Bulk bei ~20-35 Kapazitaet, also einen
            // Kolonisten, der schon mit seiner Waffe allein VOLL ist und nichts mehr holen kann (genau der
            // Fall aus '-modtest max bulk'). Die Einstellung bleibt die OBERGRENZE; gedeckelt wird auf 75 %
            // der Median-Tragkraft (Bulk UND Masse, das engere gewinnt) geteilt durch das Stueckgewicht -
            // fuer Pila heisst das 3 statt 10, fuer leichte Wurfsachen aendert sich nichts (Satchel charge:
            // Deckel 6, Einstellung 4 -> bleibt 4). EINE Rechnung hier, weil alle Verbraucher (Wizard,
            // Munitionsfenster, Auto-assign-Budget) durch diese Funktion gehen.
            if (IsThrownExplosive(weaponDef)) return CappedThrowableDefault(weaponDef, CEReloadedMod.settings.defAmmoGrenade);
            if (IsSelfAmmoWeapon(weaponDef)) return CappedThrowableDefault(weaponDef, CEReloadedMod.settings.defAmmoSelfAmmo);

            if (weaponDef.weaponTags != null)
            {
                // Pistolen VOR den anderen Zweigen (2026-08-09): sie hatten gar keinen und fielen auf die
                // allgemeinen 150 Schuss zurueck - dieselbe Menge wie ein Sturmgewehr, obendrauf auf die
                // Munition der Hauptwaffe, weil eine Pistole fast immer Zweitwaffe ist. Genau das Gewicht,
                // das Leute in die Ueberladungs-Warnung treibt.
                //
                // "SimpleGun" ist hier RAUS (2026-08-09 abends, im Test gefunden). Das ist ein
                // VANILLA-Tag fuer einfache Feuerwaffen allgemein, kein Pistolen-Tag: es haengt auch an
                // Repetierbuechsen und Unterhebelrepetierern. Gemessen im Testspielstand traf es acht echte
                // HAUPTwaffen - M1917 Enfield, Winchester 1886, MosqueFAL, Mosin-Nagant, SKS, Henry Side
                // Gate, Henry Big Boy, Winchester 94 - die dadurch 24 statt ~150 Schuss bekamen. Eine
                // Hauptwaffe mit Pistolenmunitionsmenge ist der teurere Fehler von beiden: zu viel Munition
                // kostet Traglast, zu wenig kostet das Gefecht.
                // Pistolen ohne CE-Tag fallen jetzt auf die Namenspruefung weiter unten
                // ("pistol"/"revolver"/"derringer"), und wenn auch die nicht greift, auf den Standardwert -
                // also genau auf den Stand vor dem Pistolen-Fix, statt acht Gewehre kaputtzumachen.
                if (weaponDef.weaponTags.Contains("CE_Pistol") || weaponDef.weaponTags.Contains("CE_Revolver"))
                    return CEReloadedMod.settings.defAmmoPistol;
                if (weaponDef.weaponTags.Contains("CE_MachineGun")) return CEReloadedMod.settings.defAmmoLMG;
                if (weaponDef.weaponTags.Contains("CE_SniperRifle")) return CEReloadedMod.settings.defAmmoSniper;
                if (weaponDef.weaponTags.Contains("CE_Shotgun")) return CEReloadedMod.settings.defAmmoShotgun;
                if (weaponDef.weaponTags.Contains("CE_Bow") || weaponDef.weaponTags.Contains("NeolithicBow")) return CEReloadedMod.settings.defAmmoBow;
                if (weaponDef.weaponTags.Contains("CE_Launcher") || weaponDef.weaponTags.Contains("Gun_Rocket")) return CEReloadedMod.settings.defAmmoLauncher;
                if (weaponDef.weaponTags.Contains("CE_AssaultRifle") || weaponDef.weaponTags.Contains("CE_SMG")) return CEReloadedMod.settings.defAmmoAssault;
            }

            // Fallbacks based on defName or label if tags are missing
            string name = (weaponDef.defName + " " + weaponDef.label).ToLower();
            // Auch hier vor allem anderen - sonst schluckt der "rifle"-Fallback ganz unten jede Modwaffe mit
            // "rifle" im Namen, inklusive Dingen wie "rifle pistol". Bewusst enge Begriffe: die Kosten eines
            // Fehlgriffs sind hier zu wenig Munition fuer eine Hauptwaffe.
            if (name.Contains("pistol") || name.Contains("revolver") || name.Contains("derringer")) return CEReloadedMod.settings.defAmmoPistol;
            if (name.Contains("lmg") || name.Contains("machinegun") || name.Contains("minigun") || name.Contains("machine gun")) return CEReloadedMod.settings.defAmmoLMG;
            if (name.Contains("sniper") || name.Contains("hunting") || name.Contains("svd") || name.Contains("marksman")) return CEReloadedMod.settings.defAmmoSniper;
            if (name.Contains("shotgun") || name.Contains("ks-23") || name.Contains("ks23") || name.Contains("scatter")) return CEReloadedMod.settings.defAmmoShotgun;
            if (name.Contains("bow") || name.Contains("crossbow") || name.Contains("greatbow") || name.Contains("shortbow")) return CEReloadedMod.settings.defAmmoBow;
            if (name.Contains("launcher") || name.Contains("rocket") || name.Contains("grenade") || name.Contains("molotov") || name.Contains("rpg") || name.Contains("law")) return CEReloadedMod.settings.defAmmoLauncher;
            if (name.Contains("assault") || name.Contains("smg") || name.Contains("rifle") || name.Contains("carbine")) return CEReloadedMod.settings.defAmmoAssault;

            return CEReloadedMod.settings.defAmmoDefault;
        }

        private static Dictionary<ThingDef, float> weaponScores = new Dictionary<ThingDef, float>();

        // Der Score haengt ueber die Traglast an den Mod-Einstellungen (Munitionsmengen pro Waffenklasse).
        // Ohne Leeren behielt jede schon bewertete Waffe ihren alten Wert bis zum Spielneustart - man
        // stellt die Sturmgewehr-Menge von 150 auf 400 und die Rangfolge bewegt sich nicht.
        // 2026-08-09 ergaenzt; wird von den Mod-Einstellungen beim Schliessen gerufen.
        // Zaehler statt blossem Leeren (2026-08-14): wer sich einen ABGELEITETEN Wert aus den Scores merkt
        // (z.B. den Hoechstwert fuer die Balkenskala in Dialog_GenerateLoadouts), braucht ein Signal, dass
        // der Wert nicht mehr gilt. Ohne das muesste jeder solche Aufrufer entweder pro Bild neu rechnen
        // oder still veralten - und beides gab es hier schon.
        public static int WeaponScoreVersion { get; private set; }

        public static void ClearWeaponScoreCache()
        {
            weaponScores.Clear();
            // Die Ziel-Bewertungen gehoeren mit geleert (2026-08-15, Review-Befund). Sie haengen ueber
            // CarryMult an denselben Einstellungen (Munitionsmengen je Waffenklasse) wie die Punktzahl
            // darueber, wurden hier aber nie zurueckgesetzt: nach einer Aenderung in den Einstellungen
            // rechnete die eine Haelfte mit den neuen Werten und die andere servierte die alten. Sichtbar
            // wurde es an der Zeile "Forschung bremst diese Waffe" - die vergleicht genau diese beiden
            // Haelften und erschien dann bei Waffen, deren Munition laengst erforscht ist.
            weaponTargetScores.Clear();
            weaponTargetScoresResearched.Clear();
            WeaponScoreVersion++;
        }
        private static Dictionary<ThingDef, string> weaponScoreTooltips = new Dictionary<ThingDef, string>();

        // Shields had no comparative score at all - only raw stats (ShieldDetailsTip) and an internal, never-shown
        // ranking heuristic used just to auto-pick one (ShieldQuality in Dialog_GenerateLoadouts). Protection per
        // unit of burden (mass/bulk), scaled 0-100 RELATIVE TO THE BEST SHIELD ANY ACTIVE MOD PROVIDES - not a
        // hand-picked constant like GetWeaponScore's Normalize() uses, because CE shield armor magnitudes aren't
        // known ahead of time the way vanilla weapon DPS is; this way the scale always self-calibrates instead of
        // silently clustering everything at 0 or 100 if a modded shield's numbers turn out far off a guessed constant.
        private static Dictionary<ThingDef, float> shieldScores = new Dictionary<ThingDef, float>();
        private static Dictionary<ThingDef, string> shieldScoreTooltips = new Dictionary<ThingDef, string>();
        private static float shieldRawMax = -1f;

        private static float ShieldRawPower(ThingDef d, out float sharp, out float blunt, out float burden)
        {
            sharp = d.GetStatValueAbstract(StatDefOf.ArmorRating_Sharp);
            blunt = d.GetStatValueAbstract(StatDefOf.ArmorRating_Blunt);
            float mass = d.GetStatValueAbstract(StatDefOf.Mass);
            StatDef bulkStat = DefDatabase<StatDef>.GetNamedSilentFail("Bulk");
            float bulk = bulkStat != null ? d.GetStatValueAbstract(bulkStat) : 0f;
            // Middle ground between ShieldQuality's own mobile/stationary burden weights - this score is a single
            // context-free number, not a per-situation pick, so it doesn't lean either way.
            burden = 1f + mass * 0.2f + bulk * 0.1f;
            return (sharp + blunt) / burden;
        }

        public static void GetShieldScore(ThingDef d, out float score, out string tooltip)
        {
            score = 0f;
            tooltip = "";
            if (d == null) return;
            if (shieldScores.TryGetValue(d, out float cachedScore))
            {
                score = cachedScore;
                tooltip = shieldScoreTooltips[d];
                return;
            }

            if (shieldRawMax < 0f)
            {
                shieldRawMax = 0.01f;   // floor so a modlist with exactly one weak shield doesn't divide by ~0
                foreach (ThingDef sd in DefDatabase<ThingDef>.AllDefsListForReading)
                {
                    if (!IsShieldApparel(sd)) continue;
                    float r = ShieldRawPower(sd, out _, out _, out _);
                    if (r > shieldRawMax) shieldRawMax = r;
                }
            }

            float raw = ShieldRawPower(d, out float sharp, out float blunt, out float burden);
            score = Mathf.Min(100f, 100f * raw / shieldRawMax);
            tooltip = "CER_LoadoutUtility_ShieldScoreTooltip".Translate(
                Mathf.RoundToInt(score).ToString(), sharp.ToString("F1"), blunt.ToString("F1"), burden.ToString("F2"));
            shieldScores[d] = score;
            shieldScoreTooltips[d] = tooltip;
        }
        private static Dictionary<ThingDef, Vector2> weaponTargetScores = new Dictionary<ThingDef, Vector2>(); // x = anti-flesh, y = anti-mech
        // Same numbers, but computed only from ammo the colony has researched - see GetWeaponTargetScores'
        // researchedOnly parameter. Unlike its sibling above this one is NOT valid for the whole game: finishing a
        // research project changes it while nothing about the weapon def moved. Same 5s TTL as its own input,
        // IsAmmoResearched - so a completed project reaches the assign solver within seconds, and the cache
        // still absorbs the per-weapon-per-frame calls the candidate build makes.
        private static Dictionary<ThingDef, Vector2> weaponTargetScoresResearched = new Dictionary<ThingDef, Vector2>();
        private static float weaponTargetScoresResearchedTime = -99f;
        private static Dictionary<ThingDef, int> specialSortKeys = new Dictionary<ThingDef, int>();

        private static Dictionary<ThingDef, SpecialAbility> intrinsicAbilityCache = new Dictionary<ThingDef, SpecialAbility>();
        private static Dictionary<ThingDef, SpecialAbility> ammoAbilityCache = new Dictionary<ThingDef, SpecialAbility>();
        private static Dictionary<ThingDef, int> armorPiercingCache = new Dictionary<ThingDef, int>();

        public static SpecialAbility DetectSpecialAbilities(ThingDef wDef)
        {
            if (wDef == null) return SpecialAbility.None;
            if (intrinsicAbilityCache.TryGetValue(wDef, out SpecialAbility cached)) return cached;

            SpecialAbility result = SpecialAbility.None;

            if (wDef.tools != null)
            {
                foreach (var tool in wDef.tools)
                {
                    if (tool.extraMeleeDamages != null)
                    {
                        foreach (var extra in tool.extraMeleeDamages)
                        {
                            if (extra.def == null) continue;
                            result |= ClassifyEffect(extra.def.defName.ToLower(), "");
                        }
                    }
                }
            }

            // The verb's default projectile only counts as an INTRINSIC weapon effect for weapons that do NOT
            // use ammo. For CE ammo-users (flare gun, launchers, Carl Gustaf, ...) the projectile is determined
            // by the loaded ammo, so its effect belongs to DetectAmmoSpecialAbilities, not here. Skipping it means
            // such weapons correctly show the ammo-badge icon rather than a plain "on the weapon" icon.
            if (wDef.Verbs != null && GetAmmoSet(wDef) == null)
            {
                foreach (var verb in wDef.Verbs)
                {
                    result |= ClassifyProjectile(verb.defaultProjectile);
                }
            }
            intrinsicAbilityCache[wDef] = result;
            return result;
        }

        public static SpecialAbility DetectAmmoSpecialAbilities(ThingDef wDef)
        {
            if (wDef == null) return SpecialAbility.None;
            if (ammoAbilityCache.TryGetValue(wDef, out SpecialAbility cached)) return cached;

            SpecialAbility result = SpecialAbility.None;
            AmmoSetDef ammoSet = GetAmmoSet(wDef);
            if (ammoSet != null)
            {
                foreach (var link in ammoSet.ammoTypes)
                    result |= ClassifyProjectile(link.projectile);
            }
            ammoAbilityCache[wDef] = result;
            return result;
        }

        private static Dictionary<ThingDef, ThingDef> ammoProjectileCache;

        /// <summary>The effect icons a single concrete ammo type should show: its projectile's status effects plus an
        /// Armor-Piercing marker for AP/sabot rounds. Weapon-independent (an ammo's effects are its own).</summary>
        /// <summary>The projectile ThingDef a concrete ammo type fires (looked up across all ammo sets, cached).</summary>
        public static ThingDef GetAmmoProjectile(ThingDef ammoDef)
        {
            if (ammoDef == null) return null;
            if (ammoProjectileCache == null)
            {
                ammoProjectileCache = new Dictionary<ThingDef, ThingDef>();
                foreach (AmmoSetDef set in DefDatabase<AmmoSetDef>.AllDefs)
                {
                    if (set.ammoTypes == null) continue;
                    foreach (var link in set.ammoTypes)
                        if (link.ammo != null && !ammoProjectileCache.ContainsKey(link.ammo))
                            ammoProjectileCache[link.ammo] = link.projectile;
                }
            }
            ammoProjectileCache.TryGetValue(ammoDef, out ThingDef proj);
            return proj;
        }

        public static SpecialAbility GetAmmoEffects(ThingDef ammoDef)
        {
            if (ammoDef == null) return SpecialAbility.None;
            ThingDef proj = GetAmmoProjectile(ammoDef);
            // Real ammo resolves its effect through the ammoSet's projectile. "Self-ammo" weapons (grenades routed
            // through the ammo-bill UI) have no ammoSet entry, so GetAmmoProjectile is null and their frag/EMP/flame
            // effect lives intrinsically on the weapon itself - fall back to the weapon's own intrinsic detection.
            SpecialAbility r = proj != null ? ClassifyProjectile(proj) : DetectSpecialAbilities(ammoDef);
            // Erkennung ueber den internen defName, NICHT ueber das Label (2026-08-09 gefixt): CE liefert
            // uebersetzte Labels pro Sprache mit, auf Deutsch oder Chinesisch stand hier also nie "(ap"
            // drin - der Panzerbrechend-Bonus und sein Icon fielen in jeder Sprache ausser Englisch
            // stillschweigend aus. Der defName ist sprachunabhaengig.
            // Das Label bleibt als zweite Chance drin: Modmunition, die den Typ nur im Namen fuehrt,
            // wird auf Englisch weiterhin erkannt - schlechter als vorher wird also nichts.
            // ====== ERST CEs eigene Angabe fragen (2026-08-19) ======
            // Der Fix vom 2026-08-09 (defName statt Label) hat die Sprachabhaengigkeit nur zur HAELFTE
            // beseitigt, und das ist nachgezaehlt, nicht geschaetzt: von 370 Sorten, die CE im Namen als
            // panzerbrechend fuehrt, erkennt die defName-Regel 222 - die restlichen 148 heissen
            // "..._Incendiary" (Label "(AP-I)") und "..._HE" (Label "(AP-HE)") und haengen weiterhin
            // ausschliesslich am englischen Label. Auf Deutsch fiel bei ihnen der Bonus samt Symbol aus,
        // also genau der Fehler, der schon einmal als behoben galt.
            // Die Endung einfach mitzunehmen waere falsch: es gibt "..._HE" und "..._Incendiary", die KEINE
            // AP-Munition sind (Artilleriegranate "(Incendiary)", Kanonengranate "(HE)", Werfergranate
            // "(HE)") - eine Namensregel kann die beiden Faelle nicht trennen.
            // CE selbst kann es: jede AmmoDef traegt ein <ammoClass>, und das ist eine DEF-Referenz, also in
            // jeder Sprache dieselbe Zeichenkette. In der installierten CE tragen alle 370 einen der
            // Klassennamen ArmorPiercing / Sabot / IncendiaryAP / ExplosiveAP / CompositeRigidAP /
            // RailgunSabot / ChargedAP, und KEINE der 539 uebrigen Sorten passt auf die Regel unten
            // (gepruefte Stolpersteine: "Grapeshot" und "Catapult_Boulder" enthalten ein kleingeschriebenes
            // "ap", "GrenadeHEDP" endet auf DP - deshalb Gross-/Kleinschreibung beachten und auf die ENDUNG
            // "AP" pruefen statt auf ein Vorkommen).
            // Die alten Regeln bleiben als Rueckfall fuer Munition aus Mods, die kein ammoClass setzen.
            AmmoCategoryDef ammoClass = (ammoDef as AmmoDef)?.ammoClass;
            string cls = ammoClass?.defName ?? "";
            bool ap = cls.EndsWith("AP", StringComparison.Ordinal)
                   || cls.IndexOf("Sabot", StringComparison.Ordinal) >= 0
                   || cls.IndexOf("ArmorPiercing", StringComparison.Ordinal) >= 0;
            if (!ap)
            {
                string dn = ammoDef.defName != null ? ammoDef.defName.ToLower() : "";
                string lbl = ammoDef.label != null ? ammoDef.label.ToLower() : "";
                ap = dn.Contains("_ap") || dn.EndsWith("ap") || dn.Contains("sabot")
                   || dn.Contains("aphe") || dn.Contains("apds")
                   || lbl.Contains("(ap") || lbl.Contains("sabot") || lbl.Contains("aphe") || lbl.Contains("apds");
            }
            if (ap) r |= SpecialAbility.ArmorPiercing;
            return r;
        }

        // Single source of truth mapping a damage def / projectile name to the status effects we surface as icons.
        // dName = damageDef.defName (lowercased); pName = projectile defName (lowercased, may be empty for melee).
        private static SpecialAbility ClassifyEffect(string dName, string pName)
        {
            SpecialAbility r = SpecialAbility.None;
            if (dName.Contains("emp")) r |= SpecialAbility.EMP;
            // "bomb_secondary" removed from this check (2026-07-31: AP-HE showing as
            // "Concussive"). CE's Bomb_Secondary damageDef (ParentName="Bomb") is the small explosive/HE
            // kicker riding on ~90 ordinary AP-HE-style rounds across nearly every caliber in the game (see
            // e.g. Defs/Ammo/Rifle/762x51mmNATO.xml's "Bullet_762x51mmNATO_HE", label "7.62mm NATO bullet
            // (AP-HE)") - real explosive damage, not the blunt-trauma/suppression effect this tag's own
            // tooltip ("Blunt AoE damage, heavy suppression") describes. That description genuinely fits
            // CE's dedicated Thump-cannon demolition shells (damageDef "Thump", 0 armor penetration, pure
            // blunt AoE - correctly caught by the "thump" check below) and the Concussion grenade (caught by
            // pName.Contains("concuss") on its own projectile defName, CE_Proj_GrenadeConcussion) - neither
            // of which Bomb_Secondary has anything to do with. No CE damageDef is ever literally named with
            // "concuss" in it, so dName.Contains("concuss") never actually fires; kept as a harmless defensive
            // catch-all rather than removed.
            if (dName.Contains("concuss") || dName.Contains("thump") || pName.Contains("concuss")) r |= SpecialAbility.Concussive;
            if (dName.Contains("flash") || dName.Contains("dazzle") || pName.Contains("flash")) r |= SpecialAbility.Flash;
            // Non-lethal / electrical incapacitation all reads as "Stun" to the player. "tranquil"/"neural" added
            // (2026-08-01 audit) - CE's own Damages_LocalInjury.xml defines Tranquilizer, Neuralizer and
            // Neuralizer_weak right alongside Beanbag/Taser/Electrical as the same "less-lethal incapacitation"
            // family (sedative dart / neural disruptor, additionalHediffs causing sleep/stun rather than a kill),
            // but none of their defNames contain "stun"/"taser"/"beanbag"/"electric" - Neuralizer's own ParentName
            // IS "Electrical", yet the parent chain isn't checked, only the literal defName substring - so they
            // fell through to SpecialAbility.None (no icon at all) the same way AP-HE/Concussive fell through
            // before that fix.
            if (dName.Contains("stun") || dName.Contains("taser") || dName.Contains("beanbag") || dName.Contains("electric")
                || dName.Contains("tranquil") || dName.Contains("neural")) r |= SpecialAbility.Stun;
            if (dName.Contains("flame") || dName.Contains("burn") || dName.Contains("fire") || dName.Contains("promethium") || dName.Contains("flare") || pName.Contains("flare") || pName.Contains("illum")) r |= SpecialAbility.Flame;
            if (dName.Contains("tox") || dName.Contains("venom") || dName.Contains("gas")) r |= SpecialAbility.Tox;
            if ((dName.Contains("smoke") || dName.Contains("extinguish")) && !dName.Contains("flare") && !pName.Contains("flare") && !pName.Contains("illum")) r |= SpecialAbility.Smoke;
            if (dName.Contains("fragment")) r |= SpecialAbility.Fragment;
            // Explosive/ExplosiveSecondary (2026-07-31, closing the audit gap): "bomb_secondary" checked FIRST
            // and exclusively - it must never also set the plain Explosive flag, since the substring "bomb" is
            // itself contained inside "bomb_secondary". Bomb/BombSuper (real primary explosions, e.g. mortar/
            // cannon HE shells, grenades - almost always paired with a real projectile explosionRadius) get
            // Explosive; Bomb_Secondary (the small HE kicker riding on an AP round, same-target, no radius)
            // gets its own separate ExplosiveSecondary. Thermobaric doesn't contain "bomb" as a substring at
            // all despite being ParentName="Bomb", so it needs its own explicit rule - tagged BOTH Explosive
            // (its own description: "causes a large explosion") AND Flame (its actual workerClass is
            // DamageWorker_Flame, and it "ignites flammable materials" per the same description).
            if (dName.Contains("bomb_secondary")) r |= SpecialAbility.ExplosiveSecondary;
            else if (dName.Contains("bomb")) r |= SpecialAbility.Explosive;
            if (dName.Contains("thermobaric")) r |= SpecialAbility.Explosive | SpecialAbility.Flame;
            return r;
        }

        /// <summary>
        /// Full effect classification of a projectile: its primary damage def AND any CE secondary-damage entries.
        /// AP-I / HE rounds carry their fire/blast as <c>secondaryDamage</c> (Flame_Secondary / Bomb_Secondary), so
        /// scanning only the primary damageDef misses them.
        /// </summary>
        private static SpecialAbility ClassifyProjectile(ThingDef projDef)
        {
            if (projDef == null || projDef.projectile == null) return SpecialAbility.None;
            string pName = projDef.defName.ToLower();
            SpecialAbility r = SpecialAbility.None;
            if (projDef.projectile.damageDef != null)
                r |= ClassifyEffect(projDef.projectile.damageDef.defName.ToLower(), pName);
            if (projDef.projectile is ProjectilePropertiesCE ce && ce.secondaryDamage != null)
            {
                foreach (SecondaryDamage sd in ce.secondaryDamage)
                    if (sd.def != null) r |= ClassifyEffect(sd.def.defName.ToLower(), pName);
            }
            // Shrapnel is delivered by CompFragments, not a damage def, so detect the comp directly.
            if (projDef.comps != null && projDef.comps.Any(c => c is CompProperties_Fragments)) r |= SpecialAbility.Fragment;
            return r;
        }

        /// <summary>
        /// Real per-round numbers for the ability icon tooltips (2026-07-31 - the icons only
        /// ever showed a static generic description before, never what THIS specific projectile actually
        /// does: explosion radius, fragment count, secondary damage amount, ...). <paramref name="projDef"/>
        /// is the resolved projectile ThingDef (ammo callers: GetAmmoProjectile(ammoDef); weapon callers: the
        /// first verb's defaultProjectile) - returns "" if there's nothing concrete to add for this flag,
        /// never invents or estimates a number.
        /// </summary>
        internal static string AbilityDetailSuffix(SpecialAbility flag, ThingDef projDef)
        {
            if (projDef == null || projDef.projectile == null) return "";
            ProjectileProperties p = projDef.projectile;
            ProjectilePropertiesCE ce = p as ProjectilePropertiesCE;

            switch (flag)
            {
                case SpecialAbility.Explosive:
                case SpecialAbility.Concussive:
                    return p.explosionRadius > 0f
                        ? "\n" + "CER_LoadoutUtility_DetailRadius".Translate(p.explosionRadius.ToString("0.#"))
                        : "";

                case SpecialAbility.ExplosiveSecondary:
                    {
                        SecondaryDamage sd = ce?.secondaryDamage?.FirstOrDefault(s => s.def != null && s.def.defName.ToLower().Contains("bomb_secondary"));
                        return sd != null ? "\n" + "CER_LoadoutUtility_DetailBonusDamage".Translate(sd.amount.ToString()) : "";
                    }

                case SpecialAbility.Flame:
                    {
                        SecondaryDamage sd = ce?.secondaryDamage?.FirstOrDefault(s => s.def != null &&
                            (s.def.defName.ToLower().Contains("flame") || s.def.defName.ToLower().Contains("burn")));
                        return sd != null ? "\n" + "CER_LoadoutUtility_DetailBonusDamage".Translate(sd.amount.ToString()) : "";
                    }

                case SpecialAbility.Fragment:
                    {
                        CompProperties_Fragments comp = projDef.comps?.FirstOrDefault(c => c is CompProperties_Fragments) as CompProperties_Fragments;
                        if (comp?.fragments == null || comp.fragments.Count == 0) return "";
                        int total = comp.fragments.Sum(f => f.count);
                        return "\n" + "CER_LoadoutUtility_DetailFragmentCount".Translate(total.ToString());
                    }

                case SpecialAbility.EMP:
                    return ce != null && ce.empShieldBreakChance > 0f
                        ? "\n" + "CER_LoadoutUtility_DetailShieldBreak".Translate(Mathf.RoundToInt(ce.empShieldBreakChance * 100f).ToString())
                        : "";

                default:
                    return "";
            }
        }

        /// <summary>
        /// The projectile whose properties AbilityDetailSuffix should read for a weapon's INTRINSIC effects
        /// (grenades, flamethrowers, self-ammo launchers) - the first verb's defaultProjectile, same one
        /// DetectSpecialAbilities itself scans. Ammo-using weapons resolve their detail projectile per ammo
        /// def instead (GetAmmoProjectile), not through this - see DrawWeaponSpecialIcons.
        /// </summary>
        public static ThingDef IntrinsicDetailProjectile(ThingDef wDef)
        {
            return wDef?.Verbs?.Select(v => v.defaultProjectile).FirstOrDefault(p => p != null);
        }

        /// <summary>Human-readable names of the set ability flags, in a stable order.</summary>
        public static List<string> AbilityNames(SpecialAbility a)
        {
            List<string> n = new List<string>();
            if (a.HasFlag(SpecialAbility.EMP)) n.Add("CER_LoadoutUtility_AbilityEMP".Translate());
            if (a.HasFlag(SpecialAbility.Concussive)) n.Add("CER_LoadoutUtility_AbilityConcussive".Translate());
            if (a.HasFlag(SpecialAbility.Flash)) n.Add("CER_LoadoutUtility_AbilityFlashDazzle".Translate());
            if (a.HasFlag(SpecialAbility.Stun)) n.Add("CER_LoadoutUtility_AbilityStun".Translate());
            if (a.HasFlag(SpecialAbility.Flame)) n.Add("CER_LoadoutUtility_AbilityIncendiary".Translate());
            if (a.HasFlag(SpecialAbility.Tox)) n.Add("CER_LoadoutUtility_AbilityToxic".Translate());
            if (a.HasFlag(SpecialAbility.Smoke)) n.Add("CER_LoadoutUtility_AbilitySmoke".Translate());
            if (a.HasFlag(SpecialAbility.Fragment)) n.Add("CER_LoadoutUtility_AbilityFragmentation".Translate());
            if (a.HasFlag(SpecialAbility.Explosive)) n.Add("CER_LoadoutUtility_AbilityExplosive".Translate());
            if (a.HasFlag(SpecialAbility.ExplosiveSecondary)) n.Add("CER_LoadoutUtility_AbilityExplosiveSecondary".Translate());
            return n;
        }

        private static int CountFlags(SpecialAbility f)
        {
            int c = 0;
            foreach (SpecialAbility v in AllAbilities)
                if (v != SpecialAbility.None && f.HasFlag(v)) c++;
            return c;
        }

        /// <summary>Sort key for a weapon's Special column = the number of icons actually shown there (each visible
        /// intrinsic effect icon counts 1, so weapons sort 3 → 2 → 1 → 0). Only INTRINSIC effects appear in that column
        /// (ammo-derived ones live in the expanded view), so the key ignores ammo. Cached: computing it walks the
        /// weapon's projectiles/tools, which is far too costly to redo every frame while the list re-sorts.</summary>
        public static int GetSpecialSortKey(ThingDef w)
        {
            if (specialSortKeys.TryGetValue(w, out int cached)) return cached;

            SpecialAbility shown = DetectSpecialAbilities(w);
            DetectArmorPiercing(w, out bool apIntrinsic, out _);
            if (apIntrinsic) shown |= SpecialAbility.ArmorPiercing;
            int key = CountFlags(shown);

            specialSortKeys[w] = key;
            return key;
        }

        /// <summary>Sort key for a single ammo type: one point per effect icon it shows.</summary>
        public static int GetAmmoSpecialSortKey(ThingDef ammoDef)
        {
            return CountFlags(GetAmmoEffects(ammoDef));
        }

        // --- Armor penetration -----------------------------------------------------------

        /// <summary>Per-hit base damage and cooldown of a melee weapon's strongest tool.</summary>
        public static void GetMeleeStats(ThingDef wDef, out float baseDamage, out float cooldown)
        {
            baseDamage = 0f;
            cooldown = 0f;
            if (wDef == null || wDef.tools == null) return;
            foreach (Tool tool in wDef.tools)
            {
                if (tool.power > baseDamage) { baseDamage = tool.power; cooldown = tool.cooldownTime; }
            }
        }

        /// <summary>Best sharp/blunt melee armor penetration (mm) across a weapon's ToolCE tools.</summary>
        public static void GetMeleeArmorPen(ThingDef wDef, out float sharpAP, out float bluntAP)
        {
            sharpAP = 0f;
            bluntAP = 0f;
            if (wDef == null || wDef.tools == null) return;
            foreach (Tool tool in wDef.tools)
            {
                if (tool is ToolCE tce)
                {
                    if (tce.armorPenetrationSharp > sharpAP) sharpAP = tce.armorPenetrationSharp;
                    if (tce.armorPenetrationBlunt > bluntAP) bluntAP = tce.armorPenetrationBlunt;
                }
            }
        }

        /// <summary>Highest sharp AP (mm) among a ranged weapon's available ammo/projectiles.</summary>
        public static float GetBestSharpAP(ThingDef weapon)
        {
            float best = 0f;
            AmmoSetDef set = GetAmmoSet(weapon);
            if (set != null)
            {
                foreach (var link in set.ammoTypes)
                    if (link.projectile != null && link.projectile.projectile is ProjectilePropertiesCE p)
                        best = Mathf.Max(best, p.armorPenetrationSharp);
            }
            else if (weapon.Verbs != null)
            {
                var verb = weapon.Verbs.FirstOrDefault(v => v.defaultProjectile != null);
                if (verb != null && verb.defaultProjectile.projectile is ProjectilePropertiesCE p)
                    best = p.armorPenetrationSharp;
            }
            return best;
        }

        /// <summary>
        /// True if the weapon can load a dedicated armour-piercing round: either heavy sharp AP outright, or an ammo
        /// variant whose AP is clearly above its own baseline (so even pistol/SMG calibres with an AP round count).
        /// </summary>
        public static bool HasApAmmo(ThingDef weapon)
        {
            // Consistent with the per-ammo icons: the weapon "has AP ammo" iff at least one of its ammo types is
            // flagged Armor-Piercing (a dedicated AP/sabot/APHE round). This avoids false positives like bows whose
            // arrows merely differ in AP by material without a real AP round.
            AmmoSetDef set = GetAmmoSet(weapon);
            if (set == null) return false;
            foreach (var link in set.ammoTypes)
                if (link.ammo != null && GetAmmoEffects(link.ammo).HasFlag(SpecialAbility.ArmorPiercing)) return true;
            return false;
        }

        /// <summary>
        /// Flags weapons that specifically defeat heavy armour, split by source: <paramref name="intrinsic"/> for
        /// on-weapon AP (melee blunt/sharp mauls &amp; mono/plasma swords, HEAT/explosive launchers) and
        /// <paramref name="ammoBased"/> when it takes AP ammo to get there (heavy-calibre / sabot rifles).
        /// </summary>
        public static void DetectArmorPiercing(ThingDef wDef, out bool intrinsic, out bool ammoBased)
        {
            intrinsic = false;
            ammoBased = false;
            if (wDef == null) return;
            if (armorPiercingCache.TryGetValue(wDef, out int flags))
            {
                intrinsic = (flags & 1) != 0;
                ammoBased = (flags & 2) != 0;
                return;
            }
            if (wDef.IsMeleeWeapon)
            {
                GetMeleeArmorPen(wDef, out float s, out float b);
                intrinsic = Mathf.Max(s, b) >= 15f;
            }
            else if (wDef.IsRangedWeapon)
            {
                ExtractExplosiveStats(wDef, out float d, out float r);
                if (r > 1.5f && d > 0f) intrinsic = true;   // HEAT / explosive: on the weapon
                if (HasApAmmo(wDef)) ammoBased = true;       // a dedicated AP round is available (incl. pistol/SMG AP)
            }
            armorPiercingCache[wDef] = (intrinsic ? 1 : 0) | (ammoBased ? 2 : 0);
        }

        /// <summary>Buckets a weapon into a squad role so auto-assign can keep a balanced mix.</summary>
        public static WeaponGroup ClassifyWeaponGroup(ThingDef w)
        {
            if (w == null) return WeaponGroup.Other;
            if (w.IsMeleeWeapon) return WeaponGroup.Melee;
            if (!w.IsRangedWeapon) return WeaponGroup.Other;

            ExtractExplosiveStats(w, out float expDmg, out float expRad);
            bool isMass = w.weaponTags != null && (w.weaponTags.Contains("CE_Launcher") || w.weaponTags.Contains("CE_Explosive") || w.weaponTags.Contains("CE_Grenade"));
            if ((isMass || expRad > 1.5f) && expDmg > 0f) return WeaponGroup.Launcher;

            float range = 0f;
            int burst = 1;
            if (w.Verbs != null && w.Verbs.Count > 0)
            {
                range = w.Verbs[0].range;
                burst = Mathf.Max(1, w.Verbs[0].burstShotCount);
            }

            // Neolithic bows / thrown weapons (no CE ammo, primitive tags).
            if (GetAmmoSet(w) == null && w.weaponTags != null &&
                w.weaponTags.Any(t => { string tl = t.ToLower(); return tl.Contains("neolithic") || tl.Contains("bow") || tl.Contains("archaic"); }))
                return WeaponGroup.Archaic;

            if (burst >= 6) return WeaponGroup.SupportMG;          // LMG / minigun: suppression
            if (range >= 45f && burst <= 2) return WeaponGroup.Marksman;  // long-range precision
            if (range <= 22f) return WeaponGroup.CQC;              // shotgun / SMG / pistol
            return WeaponGroup.Rifle;                              // versatile main line
        }

        // Session-cached: the answer only depends on immutable def data, and hot paths (list pre-filters,
        // weapon scoring, the assign candidate build) ask it per weapon per frame.
        private static readonly Dictionary<ThingDef, bool> utilityWeaponCache = new Dictionary<ThingDef, bool>();

        public static bool IsUtilityWeapon(ThingDef wDef)
        {
            if (wDef == null) return false;
            if (utilityWeaponCache.TryGetValue(wDef, out bool cached)) return cached;
            bool result = ComputeIsUtilityWeapon(wDef);
            utilityWeaponCache[wDef] = result;
            return result;
        }

        private static bool ComputeIsUtilityWeapon(ThingDef wDef)
        {
            // 1. Explicit utility tags or marker verbs
            if (wDef.weaponTags != null && wDef.weaponTags.Any(t => t.ToLower().Contains("utility")))
                return true;
            if (wDef.Verbs != null && wDef.Verbs.Any(v => v.verbClass != null && v.verbClass.Name.Contains("MarkForArtillery")))
                return true;

            // 2. Grenade mechanics check: a grenade is a real weapon if it hurts things - either through blast damage
            // from a harmful damage type (ExtractExplosiveStats already zeroes harmsHealth=false blasts like EMP), or
            // through a damaging special (fire burns, tox gas poisons, frag shreds, concussion crushes). Only pure
            // tactical grenades (smoke, firefoam, flashbang, EMP-only) are utility. The old rule declared anything
            // without a Flame/Tox/Concussive special utility, which swallowed frag grenades (Fragment wasn't counted)
            // and satchel charges (huge Bomb blast, but only AP/stun specials).
            bool isGrenade = wDef.weaponTags != null && wDef.weaponTags.Any(t => t.ToLower().Contains("grenade"));
            if (isGrenade)
            {
                ExtractExplosiveStats(wDef, out float expDamage, out float expRadius);
                SpecialAbility sa = DetectSpecialAbilities(wDef) | DetectAmmoSpecialAbilities(wDef);
                bool damagingSpecial = sa.HasFlag(SpecialAbility.Flame) || sa.HasFlag(SpecialAbility.Tox)
                    || sa.HasFlag(SpecialAbility.Fragment) || sa.HasFlag(SpecialAbility.Concussive);
                if (expDamage > 0f || damagingSpecial) return false;
                if (expRadius > 0f) return true;   // AoE that harms nothing: smoke / EMP / flash / foam
            }

            return false;
        }

        // --- Weapon scoring ------------------------------------------------------------
        // Calibrated against real CE ammo values (bullet damage ~10-42, sharp AP ~4-49) and the community
        // consensus that armor penetration, range and single-shot alpha define a weapon's worth - LMGs and
        // anti-materiel rifles at the top, launchers strong-but-niche. Raw power is unbounded, so it is
        // square-root-compressed onto a 0..100 scale for the colour bar.
        // Saturating normalisation: score = HEADROOM * raw / (raw + C), clamped to 100. Preserves the low/mid range
        // (C is fitted so the existing anchors stay put) but compresses the top so elite weapons spread out instead of
        // all clamping to 100. HEADROOM is only slightly above 100 so the clip point sits far out and just the very
        // strongest weapons actually reach 100 - the rest of the top tier fans out across ~85-99. C differs by class
        // because ranged and melee raw-power magnitudes differ.
        private const float RangedNormC = 33f;
        private const float MeleeNormC = 10f;
        private const float NormHeadroom = 111f;

        private static float Normalize(float rawPower, float c)
        {
            return rawPower <= 0f ? 0f : Mathf.Min(100f, NormHeadroom * rawPower / (rawPower + c));
        }

        // AP gates damage in CE: at 0 sharp AP a shot mostly glances, high AP defeats armour outright.
        private static float PenMult(float apSharp)
        {
            return Mathf.Clamp(0.35f + apSharp / 22f, 0.35f, 1.8f);
        }

        // ---- Generic handling modifiers (pure CE stats, no per-weapon hardcoding) ----------------------------------
        private static StatDef statShotSpread, statSwayFactor, statSightsEfficiency, statBulk;
        private static bool handlingStatsResolved;

        private static void ResolveHandlingStats()
        {
            if (handlingStatsResolved) return;
            handlingStatsResolved = true;
            statShotSpread = DefDatabase<StatDef>.GetNamedSilentFail("ShotSpread");
            statSwayFactor = DefDatabase<StatDef>.GetNamedSilentFail("SwayFactor");
            statSightsEfficiency = DefDatabase<StatDef>.GetNamedSilentFail("SightsEfficiency");
            statBulk = DefDatabase<StatDef>.GetNamedSilentFail("Bulk");
        }

        // CE decides hits through sights/spread/sway, not vanilla accuracy: one aimed sniper shot incapacitates while
        // a minigun's spray walks off target. precision = sights / ((1+spread) * (1+0.35*sway)), normalised so a
        // typical service rifle (sights 1, spread 0.07, sway 1.35 -> 0.63) sits at x1.0; sqrt compresses the tails.
        // Real anchors: sniper (2.82/0.02/2.05) -> x1.35 (clamped), minigun (1/0.06/3.22) -> x0.84, pistol -> x0.83.
        private static float AccuracyMult(ThingDef wDef)
        {
            ResolveHandlingStats();
            if (statSightsEfficiency == null) return 1f;   // CE stats absent - no effect
            float sights = Mathf.Max(0.3f, wDef.GetStatValueAbstract(statSightsEfficiency));
            float spread = statShotSpread != null ? Mathf.Max(0f, wDef.GetStatValueAbstract(statShotSpread)) : 0.07f;
            float sway = statSwayFactor != null ? Mathf.Max(0f, wDef.GetStatValueAbstract(statSwayFactor)) : 1.35f;
            float precision = sights / ((1f + spread) * (1f + 0.35f * sway));
            return Mathf.Clamp(Mathf.Sqrt(precision / 0.63f), 0.75f, 1.35f);
        }

        // How much a blast's damage TYPE counts toward kill power - by damage-type mechanics, never weapon names.
        // Fire (Flame/Prometheum/Burn/Incendiary damage defs) is area denial + a slow DoT that panics targets, not an
        // immediate kill, and mechs ignore it entirely; non-harming blasts (EMP, smoke, stun) do no health damage at
        // all - their value is tactical, which the effect icons/anti-mech table capture, not the raw power score.
        private static float BlastDamageFactor(DamageDef dd)
        {
            if (dd == null) return 1f;
            if (!dd.harmsHealth) return 0f;   // EMP/smoke/stun blasts deal no health damage at all
            string n = dd.defName.ToLowerInvariant();
            if (dd == DamageDefOf.Flame || n.Contains("flame") || n.Contains("burn") || n.Contains("prometheum") || n.Contains("incendiary"))
                return 0.55f;
            return 1f;
        }

        // Mass and Bulk burden the wielder in CE (move speed, carry capacity left for ammo). A rifle (~3.5kg/10 bulk)
        // is untouched; crew-served monsters (minigun 20kg) pay for having to be lugged around.
        private static float CarryMult(ThingDef wDef)
        {
            ResolveHandlingStats();
            float mass = wDef.GetStatValueAbstract(StatDefOf.Mass);
            float bulk = statBulk != null ? wDef.GetStatValueAbstract(statBulk) : 0f;
            // Logistics: the pawn carries the gun's AMMO LOAD too. A belt-fed 7.62 MG hauls kilograms of ammunition
            // where a charge rifle's cells barely register - which is exactly why charge weapons outperform WW-era
            // MGs in real play while raw DPS ranked them lower. Estimated as the default carried round count times
            // the round's mass; melee and self-ammo weapons contribute nothing. The bulk knee also dropped from 11
            // to 8 - it sat ABOVE a minigun's bulk of 10, so the heaviest weapons in the game paid no bulk price.
            float ammoKg = 0f;
            if (wDef.IsRangedWeapon && !IsSelfAmmoWeapon(wDef))
            {
                ThingDef round = GetLoadoutAmmoDefs(wDef, false).OfType<ThingDef>().FirstOrDefault();
                if (round != null && int.TryParse(GetDefaultAmmoCountForWeapon(wDef), out int cnt) && cnt > 0)
                    ammoKg = round.GetStatValueAbstract(StatDefOf.Mass) * cnt;
            }
            return 1f / (1f + Mathf.Max(0f, mass + ammoKg - 5f) * 0.025f + Mathf.Max(0f, bulk - 8f) * 0.02f);
        }

        // One-use launchers (AT4, M72 LAW, doomsday/triple rocket - detected via their OneUse verb class, vanilla
        // and CE alike) fire once and leave the pawn UNARMED mid-fight: enormous alpha, terrible "weapon you live
        // with". The damp keeps their alpha visible but stops them outranking every sustained-fire weapon - they
        // belong in a backpack as the emergency mech-opener, not in the #1 slot of the ranking.
        private static float OneUseMult(ThingDef w)
        {
            if (w == null) return 1f;
            // A thrown weapon that IS its own ammo (javelins, pila) is exactly as "single-use per copy" as a
            // disposable launcher - throw it, it's gone, the pawn needs a whole new one, not a reload. Missing
            // this case scored them like a sustained-fire gun (full burst/DPS math, no discount), which let a
            // handful of javelins outscore real rifles and get auto-assigned colony-wide.
            if (IsSelfAmmoWeapon(w)) return 0.55f;
            if (w.Verbs == null || w.Verbs.Count == 0 || w.Verbs[0].verbClass == null) return 1f;
            return w.Verbs[0].verbClass.Name.IndexOf("OneUse", StringComparison.OrdinalIgnoreCase) >= 0 ? 0.55f : 1f;
        }

        // Single-shot "alpha" matters (a big hit drops a target now), but with diminishing returns past what
        // already kills a human, so 100-damage rounds don't score as pure overkill.
        private static float AlphaValue(float effShot)
        {
            return effShot <= 60f ? effShot : 60f + 0.25f * (effShot - 60f);
        }

        // Every auto-generated loadout is named after the weapon it was built around ("<prefix> <weapon label>", or
        // just the bare label with the prefix off) - a far more reliable "which slot is the real main" signal than
        // any score/class heuristic, since a loadout's Slots list has no other record of which item was the seed
        // and which was added as a sidearm. A melee-primary loadout that bundles a ranged backup (the default for
        // every melee main - see PickDefaultSidearm) used to have that backup outrank the melee weapon outright
        // under the old "ranged always wins" rule, mislabeling the loadout's own main weapon. Falls back to the
        // score heuristic only when no slot's label matches (Role-mode loadouts, or ones renamed in CE's editor).
        public static bool LoadoutNameMatchesWeapon(string loadoutLabel, ThingDef w)
        {
            if (string.IsNullOrEmpty(loadoutLabel) || w == null || string.IsNullOrEmpty(w.label)) return false;
            // Vorsilbe unabhaengig von der Einstellung abschneiden (2026-08-16): das Etikett eines aelteren
            // Loadouts hat sie, auch wenn niemand mehr Vorsilben vergibt. Sonst faende diese Erkennung die
            // Hauptwaffe genau bei den bestehenden Loadouts nicht mehr und fiele auf die Punkteschaetzung
            // zurueck - bei einem Nahkaempfer mit Schusswaffen-Zweitwaffe ist das die falsche Antwort.
            string label = StripKindPrefix(loadoutLabel.Trim());
            return string.Equals(label.Trim(), w.label.Trim(), StringComparison.OrdinalIgnoreCase);
        }

        // The main weapon (shown first everywhere) and the sidearm of a loadout. Main = the weapon the loadout's own
        // name identifies (see LoadoutNameMatchesWeapon); failing that, the best ranged weapon, or the best melee
        // weapon if it carries no ranged one - so displays lead with the main weapon rather than just whichever
        // scores highest. Thrown explosives (grenades, satchel charges) never compete for main/side at all - CE's
        // heaviest ones can outscore the actual gun on raw blast damage alone, but they are never a loadout's
        // primary weapon.
        public static void LoadoutMainAndSide(Loadout lo, out ThingDef main, out ThingDef side)
        {
            main = null; side = null;
            if (lo == null || lo.Slots == null) return;
            MainAndSideFrom(lo.Slots.Select(s => s.thingDef), lo.label, out main, out side);
        }

        // Dieselbe Kuer aus einer losen Def-Liste statt aus einem fertigen Loadout. Gebraucht, seit die
        // Rollen-Karte die Hauptwaffe kennzeichnet (2026-08-14, Variante a): dort muss die Frage "welche
        // dieser Waffen wird die Hauptwaffe" beantwortet sein, BEVOR das Rollen-Loadout ueberhaupt
        // existiert.
        // Bewusst als geteilte Funktion und nicht als zweite Kopie der Regel - diese Kuer steht im Mod
        // schon an zwei Stellen (hier und Dialog_AssignLoadouts.AnalyzeLoadout) und laeuft genau dann
        // auseinander, wenn eine dritte dazukommt.
        public static void MainAndSideFrom(IEnumerable<ThingDef> defs, string label, out ThingDef main, out ThingDef side)
        {
            main = null; side = null;
            if (defs == null) return;
            List<ThingDef> all = defs.Where(d => d != null).ToList();
            List<ThingDef> weps = all
                .Where(w => w.IsWeapon && !IsUtilityWeapon(w) && !IsThrownExplosive(w)).Distinct().ToList();
            if (weps.Count == 0)
            {
                // Self-ammo-only loadout (a "sidearm candidate" template whose ENTIRE content is a thrown
                // explosive - stick bomb/Molotov/satchel charge - with no other weapon at all). Thrown
                // explosives are excluded from `weps` above so they never steal the main-weapon pick away
                // from a REAL gun sitting alongside them, but when one is the loadout's ONLY weapon, that
                // exclusion left `main` null entirely - no icon, no availability rect on the Assign card
                // (2026-08-02: "W| stick bomb" showed neither). Falls back to it here instead
                // of leaving this loadout with no main weapon at all.
                main = all.FirstOrDefault(w => w.IsWeapon && !IsUtilityWeapon(w) && IsThrownExplosive(w));
                return;
            }
            ThingDef mainW = weps.FirstOrDefault(w => LoadoutNameMatchesWeapon(label, w));
            if (mainW == null)
            {
                List<ThingDef> ranged = weps.Where(w => w.IsRangedWeapon && !w.IsMeleeWeapon).ToList();
                List<ThingDef> pool = ranged.Count > 0 ? ranged : weps;
                mainW = pool.OrderByDescending(w => { GetWeaponScore(w, out float s, out _); return s; }).First();
            }
            main = mainW;
            side = weps.Where(w => w != mainW).OrderByDescending(w => { GetWeaponScore(w, out float s, out _); return s; }).FirstOrDefault();
        }

        // Wie viele der fuenf CE-Datenquellen dieser Waffe fehlen. Oeffentlich, weil das UI dasselbe Ergebnis
        // fuer sein Warnzeichen braucht - Zeichen und Punktabzug muessen aus derselben Zahl kommen, sonst
        // erklaert das eine nicht das andere (2026-08-09). Nur fuer Fernkampfwaffen sinnvoll: eine Nahkampf-
        // waffe hat weder Magazin noch AmmoSet noch Rueckstoss, dort waere "fehlt" kein Mangel, sondern normal.
        private static readonly Dictionary<ThingDef, int> missingCeDataCache = new Dictionary<ThingDef, int>();

        public static int CountMissingCeData(ThingDef w)
        {
            // CEs EIGENE Hilfsmittel sind nicht 'ungepatcht' (2026-08-30): fuer CEs eigene Gegenstaende wie
            // das Fernglas gehoert so eine Meldung nicht.
            // Das Fernglas ist ein Markier-Werkzeug (Verb MarkForArtillery) - kein Magazin, keine
            // Munition, kein Rueckstoss ist dort Absicht, nicht eine fehlende CE-Anpassung. Diese Zaehlung fragt
            // "fehlen CE-Daten", und fuer Hilfsmittel lautet die Antwort nein - damit faellt auch der
            // Punkte-Abschlag weg, der an derselben Zahl haengt (siehe GetWeaponScore).
            if (w == null || !w.IsRangedWeapon || IsUtilityWeapon(w)) return 0;
            if (missingCeDataCache.TryGetValue(w, out int cached)) return cached;
            int missing = 0;
            // Wurfwaffen und Selbstmunitions-Waffen (Granate, Molotow, Speer, Satchel) sind von zwei der
            // fuenf Pruefungen AUSGENOMMEN (2026-08-09 abends, im Test gefunden): sie haben
            // konstruktionsbedingt weder ein Magazin noch ein AmmoSet. Der Abzug existiert gegen Waffen,
            // deren SCHWAECHEN unbekannt sind ("fehlende CE-Daten lesen sich als 'kein Nachteil'") - bei
            // einer Granate fehlt da aber nichts, es gibt schlicht nichts zu wissen. Gemessen: CEs eigene
            // Splittergranate meldete "2 von 5 Quellen fehlen" und bekam Faktor 0.82 aufgedrueckt, also
            // 18% Abzug fuer eine Waffe, die CE selbst patcht. Das ist das Gegenteil dessen, was der Abzug
            // erreichen soll - er soll ungepatchte Modwaffen unter gepatchte schieben, nicht ganze
            // Waffenklassen bestrafen.
            bool noMagByDesign = IsSelfAmmoWeapon(w) || IsThrownExplosive(w);
            // 1) Rueckstoss/Zielzeit - ohne die faellt der volle Burst-Bonus an.
            if (!(w.Verbs != null && w.Verbs.Any(v => v is CombatExtended.VerbPropertiesCE))) missing++;
            // 2) Magazin + Nachladezeit - fehlt beides, entfaellt die Nachladepause in der Zykluszeit.
            if (!noMagByDesign && w.GetCompProperties<CompProperties_AmmoUser>() == null) missing++;
            // 3) CEs Handling-Stats - ohne sie bleibt die Genauigkeit auf einem guenstigen Default.
            ResolveHandlingStats();
            if (statShotSpread != null && !w.statBases.NullOrEmpty()
                && !w.statBases.Any(sb => sb.stat == statShotSpread || sb.stat == statSightsEfficiency || sb.stat == statSwayFactor)) missing++;
            else if (w.statBases.NullOrEmpty()) missing++;
            // 4) Sperrigkeit - ohne sie greift die Bulk-Strafe nie.
            if (statBulk != null && (w.statBases.NullOrEmpty() || !w.statBases.Any(sb => sb.stat == statBulk))) missing++;
            // 5) AmmoSet - ohne das wiegt die mitgeschleppte Munition nichts. Fuer Wurf-/Selbstmunitions-
            //    Waffen ausgenommen: dort IST die Waffe die Munition, ein AmmoSet gibt es per Bauart nicht.
            if (!noMagByDesign && GetAmmoSet(w) == null) missing++;
            missingCeDataCache[w] = missing;
            return missing;
        }

        // Das sichtbare Kennzeichen zum Abzug oben (2026-08-09): sichtbar kennzeichnen, wie das
        // Munitionssymbol bei Statuseffekten, als Zeichen rechts oben. Sitzt genau dort, in der oberen rechten Ecke des Waffensymbols, und benutzt
        // bewusst das schon vorhandene Warn-Quadrat statt eines neuen Bildes - dieselbe Formensprache wie bei
        // "keine Werkbank", und es ist dieselbe Aussage: hier stimmt etwas mit den Daten nicht.
        // Nimmt seine Zahl aus derselben Funktion wie der Punktabzug, damit Zeichen und Tooltip nie
        // auseinanderlaufen koennen.
        // Magazingroesse einer Waffe, oder 0 wenn sie keine hat (Bogen, Wurfwaffe, Nahkampf).
        public static int MagazineSizeOf(ThingDef weapon)
        {
            CompProperties_AmmoUser au = weapon?.GetCompProperties<CompProperties_AmmoUser>();
            return au != null && au.magazineSize > 0 ? au.magazineSize : 0;
        }

        // "= 3 Mag" als Zusatz zu einer Schusszahl (2026-08-09: beides, Schuss und Magazin).
        // Bewusst ZUSATZ statt Ersatz: die genaue Schusszahl ist das, was der Spieler tippt
        // und was in den Auftrag geht, waehrend "reicht das fuer einen Kampf" sich nur in Magazinen
        // beantworten laesst. Leerer String, wenn die Waffe kein Magazin hat oder die Zahl unter einem
        // Magazin liegt - "= 0 Mag" waere keine Information, sondern Rauschen.
        public static string MagazineSuffix(ThingDef weapon, int rounds)
        {
            int mag = MagazineSizeOf(weapon);
            if (mag <= 0 || rounds < mag) return "";
            return "CER_LoadoutUtility_MagazineSuffix".Translate((rounds / mag).ToString()).ToString();
        }

        public static void DrawCeDataBadge(Rect iconRect, ThingDef w)
        {
            int missing = CountMissingCeData(w);
            if (missing <= 0) return;
            Rect badge = new Rect(iconRect.xMax - 9f, iconRect.y - 3f, 13f, 13f);
            DrawWarningBadge(badge, ColWarn);
            TooltipHandler.TipRegion(badge, "CER_LoadoutUtility_CeDataBadgeTip".Translate(missing.ToString()));
        }

        public static void GetWeaponScore(ThingDef wDef, out float score, out string tooltip)
        {
            if (weaponScores.ContainsKey(wDef))
            {
                score = weaponScores[wDef];
                tooltip = weaponScoreTooltips[wDef];
                return;
            }

            score = 0f;
            tooltip = "";

            // Special abilities are informational (each icon has its own tooltip). Only INTRINSIC (on-weapon) effects
            // give the small capped score nudge - ammo-based effects are optional, so they are listed separately and
            // never bonused. Armor Piercing is already reflected in the score via AP, so it is listed but not bonused.
            List<string> intrinsicNames = AbilityNames(DetectSpecialAbilities(wDef));
            List<string> ammoNames = AbilityNames(DetectAmmoSpecialAbilities(wDef));
            // Small proportional nudge only - a flat "+3 for any special" used to tip most elite weapons onto the
            // 100 plateau. Keep it modest so specials break ties without recreating a cluster at the top.
            float specialBonus = Mathf.Min(6f, intrinsicNames.Count * 2f);

            DetectArmorPiercing(wDef, out bool apIntrinsic, out bool apAmmo);
            if (apIntrinsic) intrinsicNames.Add("CER_LoadoutUtility_AbilityArmorPiercing".Translate());
            else if (apAmmo) ammoNames.Add("CER_LoadoutUtility_AbilityArmorPiercing".Translate());

            string specialText = "";
            if (intrinsicNames.Count > 0) specialText += "CER_LoadoutUtility_SpecialLabel".Translate(string.Join(", ", intrinsicNames.ToArray()));
            if (ammoNames.Count > 0) specialText += "CER_LoadoutUtility_SpecialAmmoLabel".Translate(string.Join(", ", ammoNames.ToArray()));

            bool isMassDamage = false;
            if (wDef.weaponTags != null && (wDef.weaponTags.Contains("CE_Grenade") || wDef.weaponTags.Contains("CE_Launcher") || wDef.weaponTags.Contains("CE_Explosive"))) isMassDamage = true;
            if (wDef.thingCategories != null && wDef.thingCategories.Any(c => c.defName.ToLower().Contains("grenades") || c.defName.ToLower().Contains("explosive"))) isMassDamage = true;

            if (wDef.IsMeleeWeapon)
            {
                float dps = wDef.GetStatValueAbstract(StatDefOf.MeleeWeapon_AverageDPS);
                GetMeleeArmorPen(wDef, out float sharpAP, out float bluntAP);
                GetMeleeStats(wDef, out float baseDamage, out float cooldown);
                float bestAP = Mathf.Max(sharpAP, bluntAP);   // a blunt maul defeats armour via blunt AP, a sword via sharp (both mm-scale)
                float weakAP = Mathf.Min(sharpAP, bluntAP);
                float penMult = Mathf.Clamp(0.5f + bestAP / 22f, 0.5f, 1.9f);
                // Hybrid versatility: a weapon that threatens BOTH armour types (real sharp AND blunt AP) adapts to any
                // target - CE auto-swings blunt when sharp would bounce off plate. Small, capped nudge scaled by the
                // WEAKER of the two AP values, so pure swords (blunt≈0) and pure hammers (sharp≈0) are untouched and
                // only genuine hybrids (pollaxe, bec de corbin) get the edge the game actually gives them.
                bool hybrid = weakAP > 1.5f;
                if (hybrid) penMult *= 1f + Mathf.Min(0.12f, 0.04f + weakAP / 120f);
                // Honest AP-weighted DPS: a fast EMP hammer and a slow high-AP maul come out close on their own merits,
                // decided by their real stats + special bonus (no artificial per-hit boost).
                // Fire on a melee weapon is a mixed blessing: burning targets panic-flee the melee block instead of
                // dying in it, so the burn DPS overstates real kill power (mechanics-based, mirrors the flesh table).
                float fireMult = (DetectSpecialAbilities(wDef) & SpecialAbility.Flame) != 0 ? 0.9f : 1f;
                // Approach risk (CE reality check): paper melee DPS is only real AFTER surviving the run through
                // suppression and fire - a cost no stat models. A flat global discount keeps the melee-internal
                // order intact but stops persona blades from crowning the MIXED ranking above every gun; the
                // melee/ranged balance in assignment stays governed by its own slider.
                score = Normalize(dps * penMult * fireMult * CarryMult(wDef), MeleeNormC) * 0.85f;
                tooltip = "CER_LoadoutUtility_ScoreTooltipMelee".Translate(baseDamage.ToString("F0"), dps.ToString("F1"), sharpAP.ToString("F0"), bluntAP.ToString("F0"));
                if (hybrid) tooltip += "CER_LoadoutUtility_ScoreTooltipHybridSuffix".Translate();
                if (cooldown > 0f) tooltip += "CER_LoadoutUtility_ScoreTooltipCooldown".Translate(cooldown.ToString("F1"));
            }
            else if (wDef.IsRangedWeapon)
            {
                float range = 0f, warmup = 0f;
                float cooldown = wDef.GetStatValueAbstract(StatDefOf.RangedWeapon_Cooldown);
                int burst = 1, ticksBetween = 8;
                float recoil = 0f;
                if (wDef.Verbs != null && wDef.Verbs.Count > 0)
                {
                    VerbProperties v = wDef.Verbs[0];
                    range = v.range;
                    burst = Mathf.Max(1, v.burstShotCount);
                    warmup = v.warmupTime;
                    ticksBetween = Mathf.Max(0, v.ticksBetweenBurstShots);
                    if (v is VerbPropertiesCE vce) recoil = vce.recoilAmount;
                }
                float reloadTime = 0f;
                int mag = 1;
                CompProperties_AmmoUser au = wDef.GetCompProperties<CompProperties_AmmoUser>();
                if (au != null) { reloadTime = au.reloadTime; mag = Mathf.Max(1, au.magazineSize); }

                // One full burst cycle in seconds, including amortised reload.
                float burstFire = (burst - 1) * ticksBetween / 60f;
                float reloadPerBurst = reloadTime * burst / mag;
                float cycleTotal = Mathf.Max(0.4f, warmup + cooldown + burstFire + reloadPerBurst);
                float rangeMult = Mathf.Clamp(0.4f + range / 40f, 0.4f, 2.1f);

                ExtractExplosiveStats(wDef, out float expDamage, out float expRadius);
                // Launcher/grenade if it is tagged as one, or throws a genuinely large blast (a gun firing HE ammo
                // has a tiny radius and must stay in the direct-fire branch).
                if ((isMassDamage || expRadius > 1.5f) && expDamage > 0f)
                {
                    // Explosive/launcher: big blast + AoE, but damped by its slow cycle and a niche penalty so it
                    // no longer dominates the list the way the old flat mass-damage bonus did.
                    float effShot = expDamage * 1.2f;
                    float power = effShot / cycleTotal + 0.35f * AlphaValue(effShot) + expRadius * 3f;
                    float carryE = CarryMult(wDef);
                    score = Normalize(power * rangeMult * 0.8f * carryE, RangedNormC);
                    tooltip = "CER_LoadoutUtility_ScoreTooltipLauncher".Translate(expDamage.ToString("F0"), expRadius.ToString("F1"), range.ToString("F0"), cycleTotal.ToString("F1"));
                    if (carryE < 0.98f) tooltip += "CER_LoadoutUtility_ScoreTooltipWeightBulkFactor".Translate(carryE.ToString("F2"));
                }
                else
                {
                    // Best round, not the average (2026-08-09) - see GetProjectileBestStats. researchedOnly
                    // stays FALSE here on purpose: this is the DISPLAYED score, and the user ruled that it
                    // should answer "how good is this weapon in principle", regardless of research state.
                    GetProjectileBestStats(wDef, out float bestDamage, out float bestAP, out int ammoVariants, out ThingDef bestAmmo);
                    float effShot = bestDamage * PenMult(bestAP);
                    // Recoil accumulates over a burst, so a long spray's tail wastes shots. This saturates: past a
                    // point extra burst adds little, which trims machine-guns/miniguns without touching aimed shots.
                    float effectiveBurst = burst / (1f + burst * recoil * 0.02f);
                    float dps = effShot * effectiveBurst / cycleTotal;
                    float power = dps + 0.6f * AlphaValue(effShot);
                    // Handling (generic CE stats): sights/spread/sway decide real hits; mass/bulk burden the wielder.
                    float acc = AccuracyMult(wDef);
                    float carry = CarryMult(wDef);
                    float variety = AmmoVarietyMult(ammoVariants);
                    score = Normalize(power * rangeMult * acc * carry * variety, RangedNormC);
                    tooltip = "CER_LoadoutUtility_ScoreTooltipRanged".Translate(bestDamage.ToString("F0"), bestAP.ToString("F1"), dps.ToString("F1"), range.ToString("F0"), burst.ToString(), reloadTime.ToString("F1"), mag.ToString());
                    // Name the round the number came from (2026-08-09) - without it "best round instead of the
                    // average" is a change nobody can see or check in the running game.
                    if (bestAmmo != null)
                        tooltip += "CER_LoadoutUtility_ScoreTooltipBestAmmo".Translate(bestAmmo.LabelCap.ToString());
                    if (ammoVariants > 1)
                        tooltip += "CER_LoadoutUtility_ScoreTooltipAmmoVariety".Translate(ammoVariants.ToString(), ((variety - 1f) * 100f).ToString("F0"));
                    if (Mathf.Abs(acc - 1f) > 0.02f || carry < 0.98f)
                        tooltip += "CER_LoadoutUtility_ScoreTooltipHandling".Translate(acc.ToString("F2"), carry.ToString("F2"));
                }

                float oneUse = OneUseMult(wDef);
                if (oneUse < 1f)
                {
                    score *= oneUse;
                    tooltip += "CER_LoadoutUtility_ScoreTooltipSingleUse".Translate(oneUse.ToString("F2"));
                }
            }

            score = Mathf.Min(100f, score + specialBonus);
            tooltip += specialText;
            if (specialBonus > 0f) tooltip += "CER_LoadoutUtility_ScoreTooltipSpecialBonus".Translate(specialBonus.ToString("F0"));

            // Fehlende CE-Daten duerfen nicht als Staerke durchgehen (2026-08-09).
            // Von fuenf CE-Datenquellen SCHMEICHELN vier der Waffe, wenn sie fehlen: ohne VerbPropertiesCE
            // gibt es keinen Rueckstoss und damit den vollen Burst-Bonus; ohne CompProperties_AmmoUser faellt
            // die Nachladezeit ganz aus der Zykluszeit; ohne CE-Handling-Stats bleibt die Genauigkeit auf dem
            // guenstigen Default; ohne Bulk gibt es keine Sperrigkeitsstrafe; ohne AmmoSet wiegt die Munition
            // nichts. Nur die fehlende ProjectilePropertiesCE bestraft (AP 0). Unterm Strich sah eine nicht
            // CE-angepasste Modwaffe damit BESSER aus als eine sauber balancierte CE-Waffe - nicht weil sie
            // besser waere, sondern weil ueber ihre Schwaechen nichts bekannt ist.
            // Bewusst EIN erklaerbarer Abschlag pro fehlender Quelle statt erfundener Ersatzwerte: einen
            // plausiblen Rueckstoss zu raten waere eine Zahl, die niemand nachrechnen kann, waehrend "wir
            // wissen ueber vier von fuenf Eigenschaften nichts, also trauen wir dem Ergebnis weniger" im
            // Tooltip steht und stimmt. Die Reihenfolge der Waffen UNTEREINANDER, ueber die dieselbe Luecke
            // klafft, bleibt dadurch erhalten - sie rutschen gemeinsam unter die gepatchten.
            int missingCe = CountMissingCeData(wDef);
            if (missingCe > 0 && score > 0f)
            {
                float confidence = Mathf.Max(0.55f, 1f - 0.09f * missingCe);
                score *= confidence;
                tooltip += "CER_LoadoutUtility_ScoreTooltipCeDataMissing".Translate(missingCe.ToString(), confidence.ToString("F2"));
            }

            // Score transparency (plan item 3): show the flesh/mech split this weapon actually resolves to, so the
            // Anti-flesh<->Anti-mech slider's effect on the ranking is never a black box. GetWeaponTargetScores has
            // its own independent cache and never calls back into GetWeaponScore, so this is safe and cheap.
            if (!IsUtilityWeapon(wDef))
            {
                GetWeaponTargetScores(wDef, out float fleshScore, out float mechScore);
                tooltip += "CER_LoadoutUtility_ScoreTooltipVsFleshMech".Translate(fleshScore.ToString("F0"), mechScore.ToString("F0"));
                // Both numbers, whenever research actually holds one of them back (2026-08-09,
                // as decided). The line above is the THEORETICAL pair, kept on purpose;
                // this one names the pair the assign solver and the anti-flesh/anti-mech slider really use, so
                // the two can never be confused for each other. Silent when they agree - which is the normal
                // case and would otherwise put a redundant second line on every weapon in the game.
                GetWeaponTargetScores(wDef, out float fleshR, out float mechR, researchedOnly: true);
                if (Mathf.Abs(fleshR - fleshScore) > 0.5f || Mathf.Abs(mechR - mechScore) > 0.5f)
                    tooltip += "CER_LoadoutUtility_ScoreTooltipVsFleshMechResearched".Translate(fleshR.ToString("F0"), mechR.ToString("F0"));
            }
            tooltip += "CER_LoadoutUtility_ScoreTooltipFinalScore".Translate(score.ToString("F0"));

            if (IsUtilityWeapon(wDef))
            {
                score = -1f;
                tooltip += "CER_LoadoutUtility_ScoreTooltipUtilityExcluded".Translate();
            }

            weaponScores[wDef] = score;
            weaponScoreTooltips[wDef] = tooltip;
        }

        // ---- Flesh vs mech damage table ---------------------------------------------------------------------------
        // Representative CE armor of the two target archetypes. Sharp is mm RHA-equivalent, blunt is CE's own MPa-ish
        // scale (a separate axis: a zeushammer swings 160 blunt AP while swords carry 2-3 sharp).
        // Mech values corrected 2026-08-02 to CE's ACTUAL centipede statBases, read out of the installed CE at
        // Patches/Core/ThingDefs_Races/Races_Mechanoid.xml: Sharp 20 / Blunt 45. The old 24/30 described a target
        // that exists nowhere in the game - harder in sharp than the toughest mech, softer in blunt than the same
        // one. Centipede (not the Scyther/Lancer walkers at 4/6) is the right archetype for this axis on purpose:
        // the anti-mech slider is dragged BECAUSE of the target that ordinary rounds bounce off, and a walker dies
        // to anything, so calibrating on the walker would make the slider meaningless.
        private const float FleshArmorSharp = 6f;
        private const float FleshArmorBlunt = 1.5f;
        // "Mech" is TWO real archetypes, not one invented number, and the difference is not cosmetic: with CE's
        // honest armor curve (see ArmorMult) a single centipede-only archetype makes the whole anti-mech axis
        // collapse - 7.62 HP/FMJ/AP all retain exactly 0.00 against sharp 20, and even Sabot only reaches 0.20,
        // so every kinetic round would be reported as anti-flesh and the slider would rank nothing. That is
        // arithmetically true and useless as advice, because a mech raid is mostly Scythers and Lancers
        // (BaseMechanoidWalker, sharp 4 / blunt 6), with centipedes (sharp 20 / blunt 45) as the hard minority.
        // Averaging retention over both is what reproduces the answer players actually give: HP anti-flesh,
        // FMJ anti-flesh, AP roughly neutral, Sabot clearly anti-mech. Both value pairs are read out of the
        // installed CE at Patches/Core/ThingDefs_Races/Races_Mechanoid.xml, not estimated.
        private const float MechWalkerSharp = 4f;
        private const float MechWalkerBlunt = 6f;
        private const float MechHeavySharp = 20f;
        private const float MechHeavyBlunt = 45f;

        // Damage retention against the mech mix / against flesh, always on both axes (sharp AND blunt) because
        // CE turns a deflected sharp hit into a blunt one - see ExtractDamageAndAP.
        private static float MechRetention(float apSharp, float apBlunt)
        {
            float walker = Mathf.Max(ArmorMult(apSharp, MechWalkerSharp), ArmorMult(apBlunt, MechWalkerBlunt));
            float heavy = Mathf.Max(ArmorMult(apSharp, MechHeavySharp), ArmorMult(apBlunt, MechHeavyBlunt));
            return 0.5f * (walker + heavy);
        }

        // ====== Dieselbe Rechnung fuer MUNITIONSSORTEN, nur ohne das Mathf.Max (2026-08-19) ======
        //
        // Anlass: die HP-Munition ist staerker gegen Fleisch als FMJ, trotzdem wurde beim Regler ganz links
        // nie HP vorgeschlagen - und das hat eine messbare Ursache, keine
        // Geschmacksfrage. In CE ist `armorPenetrationBlunt` eine Eigenschaft der PATRONE, nicht des
        // Geschosses: 7.62x39mm steht bei FMJ, AP, HP, AP-I, AP-HE und Sabot auf exakt 41.08, waehrend
        // `armorPenetrationSharp` mit 5.5 / 11 / 3 / 11 / 5.5 / 19.25 genau das ist, was die Sorten
        // unterscheidet (nachgelesen in CEs Defs/Ammo/Rifle/762x39mmSoviet.xml).
        // Mit Mathf.Max gewinnt bei jeder Gewehrpatrone der stumpfe Wert - 41.08 gegen die 6 Panzerung
        // eines Laeufers sind 0.85, der scharfe Anteil liegt immer darunter. Ergebnis: FMJ und HP kamen
        // beide auf exakt dieselbe Affinitaet, und "der erste mit dem hoechsten Wert gewinnt" hat die
        // Def-Reihenfolge entscheiden lassen - FMJ steht in CEs XML vor HP. Genau die Beobachtung.
        //
        // Statt Max ein ODER: der Treffer kommt durch, wenn er die scharfe ODER die stumpfe Panzerung
        // schlaegt. 1-(1-a)(1-b) ist in BEIDEN Achsen monoton, der scharfe Unterschied bleibt also
        // sichtbar, auch wenn der stumpfe Wert gross ist.
        // Gegenprobe an dem Fall, fuer den die stumpfe Achse 2026-08-02 ueberhaupt aufgenommen wurde
        // (12 Gauge: Buckshot 4/4.52, Slug 6/85.2): alt Buck -1.00 / Slug +0.40, neu Buck -1.00 /
        // Slug +0.43. Wo eine Achse dominiert, aendert sich praktisch nichts - der Unterschied entsteht
        // nur dort, wo beide etwas beitragen, und das ist genau der Fall, der bisher flach war.
        // Bewusst NUR fuer Munition: die Waffenbewertung (GetWeaponTargetScores) rechnet unveraendert
        // weiter mit MechRetention, damit diese Korrektur keine Waffenrangfolge verschiebt.
        private static float AmmoMechRetention(float apSharp, float apBlunt)
        {
            float walker = PenetratesEither(ArmorMult(apSharp, MechWalkerSharp), ArmorMult(apBlunt, MechWalkerBlunt));
            float heavy = PenetratesEither(ArmorMult(apSharp, MechHeavySharp), ArmorMult(apBlunt, MechHeavyBlunt));
            return 0.5f * (walker + heavy);
        }

        private static float PenetratesEither(float a, float b) => 1f - (1f - a) * (1f - b);

        private static float FleshRetention(float apSharp, float apBlunt)
        {
            return Mathf.Max(ArmorMult(apSharp, FleshArmorSharp), ArmorMult(apBlunt, FleshArmorBlunt));
        }

        // CE's own partial-penetration formula, no longer an approximation (2026-08-02). Verified in the decompile:
        // ArmorUtilityCE.TryPenetrateArmor does `Clamp01((pen - armor) / pen)` and multiplies the damage by it.
        // The previous curve (0.12 + 0.88 * r^2, saturating at parity) was wrong in both directions and in a way
        // that mattered: against a centipede's sharp 20 it gave a 14-AP round 0.55 where CE gives 0.00, and rated
        // a barely-penetrating 25-AP sabot identically to a 60-AP anti-materiel round (both 1.00) where CE gives
        // 0.20 vs 0.67. So the model both promised penetration that never happens and could not tell "just gets
        // through" from "massively over-penetrates".
        private static float ArmorMult(float ap, float armor)
        {
            if (armor <= 0f) return 1f;
            if (ap <= 0f) return 0f;
            return Mathf.Clamp01((ap - armor) / ap);
        }

        // Per-target effect multipliers. AP is mostly handled by the armor model above, so its extra here is small.
        // melee: fire is a liability up close - burning targets panic-flee the melee block instead of dying in it -
        // while incendiary AMMO shines against flesh crowds at range. Mechanics-based, not per-weapon.
        private static float EffectMultFlesh(SpecialAbility fx, bool melee = false)
        {
            float m = 1f;
            if ((fx & SpecialAbility.Flame) != 0) m *= melee ? 0.85f : 1.35f;
            if ((fx & SpecialAbility.Tox) != 0) m *= 1.3f;         // toxic buildup
            if ((fx & SpecialAbility.Fragment) != 0) m *= 1.25f;   // frag shreds unarmored flesh
            if ((fx & SpecialAbility.Concussive) != 0) m *= 1.05f;
            if ((fx & SpecialAbility.EMP) != 0) m *= 0.35f;        // EMP barely hurts flesh
            if ((fx & SpecialAbility.Stun) != 0) m *= 0.7f;
            if ((fx & SpecialAbility.Explosive) != 0) m *= 1.2f;   // real blast/shrapnel - shreds unarmored targets
            if ((fx & SpecialAbility.ExplosiveSecondary) != 0) m *= 1.1f;  // small HE kicker on top of an AP hit
            return m;
        }

        private static float EffectMultMech(SpecialAbility fx)
        {
            float m = 1f;
            if ((fx & SpecialAbility.EMP) != 0) m *= 1.8f;         // EMP stuns/disables mechs - prime anti-mech
            if ((fx & SpecialAbility.Stun) != 0) m *= 1.3f;
            if ((fx & SpecialAbility.Concussive) != 0) m *= 1.1f;
            if ((fx & SpecialAbility.ArmorPiercing) != 0) m *= 1.1f;
            if ((fx & SpecialAbility.Flame) != 0) m *= 0.1f;       // mechs are immune to fire
            if ((fx & SpecialAbility.Tox) != 0) m *= 0.1f;         // immune to toxins
            if ((fx & SpecialAbility.Fragment) != 0) m *= 0.5f;    // frag pings off armor
            if ((fx & SpecialAbility.Explosive) != 0) m *= 0.9f;   // still does something to mechs, just less than pure AP
            if ((fx & SpecialAbility.ExplosiveSecondary) != 0) m *= 0.95f; // rides on an already-penetrating AP hit, barely dents mech effectiveness
            return m;
        }

        // Effectiveness of a weapon against flesh vs a mechanoid, on the same 0..100 curve as the general weapon score
        // (so the three can be blended). Uses the BEST round the weapon's ammo set offers for each target - a colonist
        // loads AP for mechs and HE/incendiary for flesh - plus the special-effect multipliers above. The general
        // weapon score stays the ammo-independent anchor; this only tilts it toward what the anti-flesh/anti-mech
        // slider asks for. Cached per weapon (built on first use).
        // researchedOnly (2026-08-09): when true, only rounds the colony has actually
        // researched may be picked. The split is the whole fix: for the score that is SHOWN, the theoretical
        // value is fine and even correct - we want to see how good weapons are in theory; for concrete
        // suggestions, what the colony can actually produce has to count.
        // So: FALSE everywhere the number is merely SHOWN (the weapon list, the score
        // tooltip), TRUE everywhere the mod SUGGESTS a weapon or hands one out (the assign solver and the
        // anti-flesh/anti-mech slider that sorts by these two numbers). Without it, a rifle whose ammo set
        // contains an unresearched sabot round advertises "vs mech: 78" and the slider reorders the whole
        // squad around a capability nobody has.
        // Two independent caches - the same weapon legitimately has two different answers here.
        public static void GetWeaponTargetScores(ThingDef wDef, out float fleshScore, out float mechScore, bool researchedOnly = false)
        {
            if (researchedOnly && Time.realtimeSinceStartup - weaponTargetScoresResearchedTime > 5f)
            {
                weaponTargetScoresResearched.Clear();
                weaponTargetScoresResearchedTime = Time.realtimeSinceStartup;
            }
            Dictionary<ThingDef, Vector2> cache = researchedOnly ? weaponTargetScoresResearched : weaponTargetScores;
            if (wDef != null && cache.TryGetValue(wDef, out Vector2 v)) { fleshScore = v.x; mechScore = v.y; return; }
            fleshScore = 0f; mechScore = 0f;
            if (wDef == null) return;

            if (wDef.IsMeleeWeapon)
            {
                float dps = wDef.GetStatValueAbstract(StatDefOf.MeleeWeapon_AverageDPS);
                GetMeleeArmorPen(wDef, out float sharpAP, out float bluntAP);
                SpecialAbility fx = DetectSpecialAbilities(wDef);
                float carry = CarryMult(wDef);
                // Sharp and blunt are separate armor axes in CE; the pawn attacks along whichever defeats the target.
                // Against mechs this is what makes a 160-blunt zeushammer crush what a 3-sharp sword bounces off.
                float fleshPen = FleshRetention(sharpAP, bluntAP);
                float mechPen = MechRetention(sharpAP, bluntAP);
                fleshScore = Normalize(dps * fleshPen * EffectMultFlesh(fx, melee: true) * carry, MeleeNormC);
                mechScore = Normalize(dps * mechPen * EffectMultMech(fx) * carry, MeleeNormC);
            }
            else if (wDef.IsRangedWeapon)
            {
                // Cycle/burst/range mirror GetWeaponScore so the DPS is comparable to the general score.
                float range = 0f, warmup = 0f, recoil = 0f;
                float cooldown = wDef.GetStatValueAbstract(StatDefOf.RangedWeapon_Cooldown);
                int burst = 1, ticksBetween = 8;
                if (wDef.Verbs != null && wDef.Verbs.Count > 0)
                {
                    VerbProperties vp = wDef.Verbs[0];
                    range = vp.range; burst = Mathf.Max(1, vp.burstShotCount);
                    warmup = vp.warmupTime; ticksBetween = Mathf.Max(0, vp.ticksBetweenBurstShots);
                    if (vp is VerbPropertiesCE vce) recoil = vce.recoilAmount;
                }
                float reloadTime = 0f; int mag = 1;
                CompProperties_AmmoUser au = wDef.GetCompProperties<CompProperties_AmmoUser>();
                if (au != null) { reloadTime = au.reloadTime; mag = Mathf.Max(1, au.magazineSize); }
                float burstFire = (burst - 1) * ticksBetween / 60f;
                float reloadPerBurst = reloadTime * burst / mag;
                float cycleTotal = Mathf.Max(0.4f, warmup + cooldown + burstFire + reloadPerBurst);
                float rangeMult = Mathf.Clamp(0.4f + range / 40f, 0.4f, 2.1f);
                float effectiveBurst = burst / (1f + burst * recoil * 0.02f);

                ExtractExplosiveStats(wDef, out float expDamage, out float expRadius);
                bool isMassDamage = false;
                if (wDef.weaponTags != null && (wDef.weaponTags.Contains("CE_Grenade") || wDef.weaponTags.Contains("CE_Launcher") || wDef.weaponTags.Contains("CE_Explosive"))) isMassDamage = true;
                if (wDef.thingCategories != null && wDef.thingCategories.Any(c => c.defName.ToLower().Contains("grenades") || c.defName.ToLower().Contains("explosive"))) isMassDamage = true;

                if ((isMassDamage || expRadius > 1.5f) && expDamage > 0f)
                {
                    SpecialAbility fx = DetectSpecialAbilities(wDef);
                    float carryE = CarryMult(wDef);
                    // The blast's armor penetration runs through the same partial-penetration model as bullets do -
                    // without it, raw blast damage ranked a stick bomb ABOVE a satchel charge against mechs, exactly
                    // backwards: the satchel's demolition AP punches through centipede plate, the stick bomb's huge
                    // soft-target blast barely scratches it. Explosives with no CE blast AP data stay at the old
                    // neutral behavior instead of being zeroed out.
                    ExtractExplosiveAP(wDef, out float eApSharp, out float eApBlunt);
                    float fleshPen = 1f, mechPen = 1f;
                    if (eApSharp > 0f || eApBlunt > 0f)
                    {
                        fleshPen = FleshRetention(eApSharp, eApBlunt);
                        mechPen = MechRetention(eApSharp, eApBlunt);
                    }
                    // A demolition charge's fragmentation is a secondary bonus against infantry, not its real
                    // threat - unlike a small frag/HE grenade where the pellets ARE the damage. EffectMultMech's
                    // blanket "frag pings off armor" penalty is calibrated for that latter case; applied to a
                    // satchel charge it ranks the charge BELOW a plain stick bomb purely because it also happens
                    // to carry shrapnel, exactly backwards from its actual anti-armor purpose. Once the primary
                    // blast itself is already well past a typical frag grenade's damage (~150), the charge (not
                    // the shrapnel) is clearly what matters against a mech, so the penalty no longer applies
                    // there - flesh scoring is untouched, frag genuinely helps against infantry either way.
                    SpecialAbility fxMech = expDamage > 150f ? (fx & ~SpecialAbility.Fragment) : fx;
                    float shotF = expDamage * 1.2f * EffectMultFlesh(fx) * fleshPen;
                    float shotM = expDamage * 1.2f * EffectMultMech(fxMech) * mechPen;
                    float powerF = shotF / cycleTotal + 0.35f * AlphaValue(shotF) + expRadius * 3f;   // AoE crushes flesh crowds
                    float powerM = shotM / cycleTotal + 0.35f * AlphaValue(shotM) + expRadius * 1f;   // less so against a lone mech
                    fleshScore = Normalize(powerF * rangeMult * 0.8f * carryE, RangedNormC);
                    mechScore = Normalize(powerM * rangeMult * 0.8f * carryE, RangedNormC);
                }
                else
                {
                    float bestFleshShot = 0f, bestMechShot = 0f;
                    AmmoSetDef ammoSet = GetAmmoSet(wDef);
                    if (ammoSet != null && ammoSet.ammoTypes != null)
                    {
                        // Two passes for the same "the filter emptied the set" fallback GetProjectileBestStats
                        // uses: a weapon whose every round is still unresearched must not silently score 0 here
                        // (that would make it look WORSE than a weapon with no ammo set at all). The assign path
                        // already refuses such a weapon through its own producible/ammo checks.
                        for (int pass = 0; pass < 2; pass++)
                        {
                            bool filter = researchedOnly && pass == 0;
                            bool anyCounted = false;
                            foreach (var link in ammoSet.ammoTypes)
                            {
                                if (link.projectile == null || link.projectile.projectile == null) continue;
                                if (filter && (link.ammo == null || !IsAmmoResearched(link.ammo))) continue;
                                ExtractDamageAndAP(link.projectile, out float dmg, out float ap, out float apB);
                                SpecialAbility fx = link.ammo != null ? GetAmmoEffects(link.ammo) : SpecialAbility.None;
                                if (dmg <= 1f)
                                {
                                    // Utility round (EMP/smoke/flare): no direct damage, but EMP is a top anti-mech option.
                                    if ((fx & SpecialAbility.EMP) != 0) { bestMechShot = Mathf.Max(bestMechShot, 22f); anyCounted = true; }
                                    continue;
                                }
                                anyCounted = true;
                                // Max(sharp, blunt) - the same two-axis form the melee and explosive branches above
                                // already used, which the ranged branch was missing. See ExtractDamageAndAP for why
                                // blunt is not optional: CE routes a deflected sharp hit into a blunt one.
                                float shotF = dmg * FleshRetention(ap, apB) * EffectMultFlesh(fx);
                                float shotM = dmg * MechRetention(ap, apB) * EffectMultMech(fx);
                                if (shotF > bestFleshShot) bestFleshShot = shotF;
                                if (shotM > bestMechShot) bestMechShot = shotM;
                            }
                            if (anyCounted || !filter) break;
                        }
                    }
                    else if (wDef.Verbs != null)
                    {
                        var verb = wDef.Verbs.FirstOrDefault(vv => vv.defaultProjectile != null && vv.defaultProjectile.projectile != null);
                        if (verb != null)
                        {
                            ExtractDamageAndAP(verb.defaultProjectile, out float dmg, out float ap, out float apB);
                            SpecialAbility fx = DetectSpecialAbilities(wDef);
                            DetectArmorPiercing(wDef, out bool apI, out _); if (apI) fx |= SpecialAbility.ArmorPiercing;
                            bestFleshShot = dmg * FleshRetention(ap, apB) * EffectMultFlesh(fx);
                            bestMechShot = dmg * MechRetention(ap, apB) * EffectMultMech(fx);
                        }
                    }
                    float powerF = bestFleshShot * effectiveBurst / cycleTotal + 0.6f * AlphaValue(bestFleshShot);
                    float powerM = bestMechShot * effectiveBurst / cycleTotal + 0.6f * AlphaValue(bestMechShot);
                    float handling = AccuracyMult(wDef) * CarryMult(wDef);   // mirror the general score
                    fleshScore = Normalize(powerF * rangeMult * handling, RangedNormC);
                    mechScore = Normalize(powerM * rangeMult * handling, RangedNormC);
                }

                // Mirror the general score's single-use damp: without it the assign solver at Anti-mech would still
                // crown an AT4 "best main weapon" although its wielder is unarmed after one trigger pull.
                float oneUse = OneUseMult(wDef);
                fleshScore *= oneUse;
                mechScore *= oneUse;
            }

            cache[wDef] = new Vector2(fleshScore, mechScore);
        }

        // Armor penetration of a weapon's blast projectile (sharp + blunt), read from the same projectiles the
        // explosive damage comes from. This is what separates a satchel/demolition charge (huge AP, punches through
        // centipede plate) from a stick bomb (huge blast, superficial scratches on armor) - raw blast damage alone
        // ranks them backwards against mechs.
        private static void ExtractExplosiveAP(ThingDef weapon, out float apSharp, out float apBlunt)
        {
            apSharp = 0f;
            apBlunt = 0f;
            List<ThingDef> defsToCheck = new List<ThingDef> { weapon };
            if (weapon.Verbs != null)
                foreach (var verb in weapon.Verbs)
                    if (verb.defaultProjectile != null) defsToCheck.Add(verb.defaultProjectile);
            AmmoSetDef ammoSet = GetAmmoSet(weapon);
            if (ammoSet != null && ammoSet.ammoTypes != null)
                foreach (var link in ammoSet.ammoTypes)
                    if (link.projectile != null) defsToCheck.Add(link.projectile);
            foreach (var def in defsToCheck)
            {
                if (def == null || def.projectile == null) continue;
                if (def.projectile.explosionRadius <= 0.5f) continue;   // same blast gate as the damage extractor
                if (def.projectile is ProjectilePropertiesCE ppce)
                {
                    apSharp = Mathf.Max(apSharp, ppce.armorPenetrationSharp);
                    apBlunt = Mathf.Max(apBlunt, ppce.armorPenetrationBlunt);
                }
            }
        }

        private static void ExtractExplosiveStats(ThingDef weapon, out float maxDamage, out float maxRadius)
        {
            maxDamage = 0f;
            maxRadius = 0f;

            List<ThingDef> defsToCheck = new List<ThingDef>();
            defsToCheck.Add(weapon);

            if (weapon.Verbs != null)
            {
                foreach (var verb in weapon.Verbs)
                {
                    if (verb.defaultProjectile != null) defsToCheck.Add(verb.defaultProjectile);
                }
            }
            
            AmmoSetDef ammoSet = GetAmmoSet(weapon);
            if (ammoSet != null && ammoSet.ammoTypes != null)
            {
                foreach (var link in ammoSet.ammoTypes)
                {
                    if (link.projectile != null) defsToCheck.Add(link.projectile);
                }
            }

            foreach (var def in defsToCheck)
            {
                if (def == null) continue;

                if (def.projectile != null)
                {
                    float rad = def.projectile.explosionRadius;
                    if (rad > maxRadius) maxRadius = rad;
                    // Blast projectiles like the doomsday rocket (CombatExtended.ProjectileCE_Explosive) carry their
                    // blast damage on the projectile's own damageAmountBase, read via GetDamageAmount - NOT on
                    // damageDef.defaultDamage, which is ~0 for CE and left the doomsday scoring off its actual 250.
                    // Only treat it as explosion damage when the projectile really has a blast radius, so a normal
                    // penetrating bullet's direct damage isn't double-counted here.
                    if (rad > 0.5f)
                    {
                        float dmg = def.projectile.GetDamageAmount((Thing)null);
                        if (dmg >= 99999f) dmg = 0f;
                        if (dmg <= 0f && def.projectile.damageDef != null) dmg = def.projectile.damageDef.defaultDamage; // vanilla fallback
                        dmg *= BlastDamageFactor(def.projectile.damageDef);
                        if (dmg > maxDamage) maxDamage = dmg;
                    }
                }

                if (def.comps != null)
                {
                    foreach (var comp in def.comps)
                    {
                        if (comp == null) continue;

                        // Typed access to CE's (and vanilla's) explosive comp instead of reflection: if CE ever
                        // renames these fields this fails to compile rather than silently reading 0.
                        float dmg = 0f;
                        float rad = 0f;
                        DamageDef dtype = null;
                        if (comp is CompProperties_ExplosiveCE ce)
                        {
                            dmg = ce.damageAmountBase;
                            rad = ce.explosiveRadius;
                            dtype = ce.explosiveDamageType;
                        }
                        else if (comp is CompProperties_Explosive vanilla)
                        {
                            dmg = vanilla.damageAmountBase;
                            rad = vanilla.explosiveRadius;
                            dtype = vanilla.explosiveDamageType;
                        }
                        else continue;

                        if (dmg >= 99999f) dmg = 0f;
                        dmg *= BlastDamageFactor(dtype);
                        maxDamage = Mathf.Max(maxDamage, dmg);
                        maxRadius = Mathf.Max(maxRadius, rad);
                    }
                }
            }
        }

        // --- Bill creation --------------------------------------------------------------

        // Records that WE created this specific bill (called only at fresh-bill construction time, never
        // when merely updating an existing bill's target count - an existing bill might be the player's own
        // hand-placed one, and adjusting its count shouldn't silently reclassify it as ours). Mirrors
        // MarkAsManaged's own Loadout-tracking pattern, keyed by Bill.GetUniqueLoadID() (a bill has no
        // int UniqueID the way a Loadout does, but every Bill already carries a stable, save-surviving
        // string ID via ILoadReferenceable).
        public static void MarkBillAsManaged(Bill bill)
        {
            if (bill == null) return;
            CEReloadedGameComponent comp = CEReloadedGameComponent.Cached;
            comp?.managedBillIds.Add(bill.GetUniqueLoadID());
            // Die zwei Auftrags-Caches sofort verwerfen (2026-08-15, zusammen mit dem neuen Cache in
            // WeaponQueuedCount). Beide Aufrufer von AddBill laufen durch hier, das ist also der eine
            // Engpass fuer "es ist ein Auftrag dazugekommen". Wichtig ist genau diese Richtung: die
            // Pruefung "fehlt noch Produktion?" laeuft teils unmittelbar nach dem Anlegen der Auftraege,
            // und eine Sekunde alte Zahlen wuerden dort behaupten, es sei nichts angelegt worden - der
            // Nachschub-Hinweis ginge nochmal auf. Die Gegenrichtung (Auftrag GELOESCHT, Zahl kurz zu
            // hoch) bleibt bewusst beim Hoechstalter: sie hat acht Loeschstellen und ist harmlos, sie
            // heilt nach einer Sekunde von selbst.
            weaponQueuedCache.Clear();
            activeBillCache.Clear();
        }

        // Vergisst jede gemerkte Auftrags-ID, zu der es keinen Auftrag mehr gibt (2026-08-15, ein
        // Speicherleck). managedBillIds wuchs bisher mit JEDEM je erzeugten Auftrag und wurde nie
        // wieder kleiner - auch nicht, wenn der Auftrag laengst geloescht war. Es steht ausserdem im
        // Spielstand, wuchs also dauerhaft mit: wer den Nachschub-Assistenten ueber eine lange Partie
        // regelmaessig benutzt, sammelt tausende tote Zeichenketten an.
        // Die Richtung ist sicher: eine faelschlich vergessene ID macht aus einem Mod-Auftrag einen
        // "von Hand angelegten", und die werden beim Aufraeumen NICHT angefasst - wir loeschen im
        // Zweifel also weniger, nie mehr.
        // Laeuft am Aufraeum-Tick mit (einmal je Spielstunde), nicht bei jedem Zugriff.
        public static void PruneManagedBillIds()
        {
            CEReloadedGameComponent comp = CEReloadedGameComponent.Cached;
            if (comp?.managedBillIds == null || comp.managedBillIds.Count == 0) return;
            HashSet<string> alive = new HashSet<string>();
            foreach (Building_WorkTable bench in AllColonyBenches())
            {
                if (bench.billStack == null) continue;
                foreach (Bill b in bench.billStack.Bills)
                    if (b != null) alive.Add(b.GetUniqueLoadID());
            }
            comp.managedBillIds.RemoveWhere(id => !alive.Contains(id));
        }

        public static bool IsManagedBill(Bill bill)
        {
            if (bill == null) return false;
            CEReloadedGameComponent comp = CEReloadedGameComponent.Cached;
            return comp != null && comp.managedBillIds.Contains(bill.GetUniqueLoadID());
        }

        /// <summary>Stammt JEDER laufende Auftrag fuer dieses Ding von unserem eigenen Assistenten?
        /// (2026-08-14: alles wird vorgeschlagen, aber MANUELL angelegte Auftraege sind standardmaessig AUS
        /// (opt-in), die vom Mod angelegten dagegen opt-out.)
        /// Bewusst ALLE statt IRGENDEINER: der Loeschlauf (PruneBillsFor) entfernt jeden Auftrag fuer diesen
        /// Def, nicht nur unseren. Gibt es zu einem Ding einen eigenen UND einen handgesetzten Auftrag,
        /// wuerde ein vorbelegter Haken also auch den handgesetzten mitnehmen - genau das, was diese
        /// Regel verhindern soll. In dem Fall zaehlt die Zeile als handgesetzt und ist opt-in.
        /// Ohne laufenden Auftrag: false - dann gibt es nichts vorzubelegen.</summary>
        public static bool AllBillsForDefAreOurs(ThingDef d)
        {
            if (d == null) return false;
            bool any = false;
            foreach (Building_WorkTable bench in AllColonyBenches())
            {
                if (bench.billStack == null) continue;
                foreach (Bill bill in bench.billStack.Bills)
                {
                    Bill_Production bp = bill as Bill_Production;
                    if (bp?.recipe?.products == null) continue;
                    bool makesIt = false;
                    foreach (var prod in bp.recipe.products)
                        if (prod.thingDef == d) { makesIt = true; break; }
                    if (!makesIt) continue;
                    any = true;
                    if (!IsManagedBill(bill)) return false;
                }
            }
            return any;
        }

        // Delete-scope for Arm-my-Colony's bill-reset switches (2026-08-01) - replaces a plain bool with a
        // 3-state choice: leave bills alone, delete only what THIS mod's own tooling queued (MarkBillAsManaged),
        // or delete every bill for a recognized weapon/ammo type regardless of who set it up (the original,
        // only, blunt behavior). Cautious/Normal/Aggressive map to None/ManagedOnly/All respectively.
        public enum BillDeleteScope { None, ManagedOnly, All }

        public enum BillChange { Added, Updated, Unchanged }

        /// <summary>
        /// Adds a TargetCount production bill for <paramref name="recipe"/> to <paramref name="bench"/>, or updates the
        /// existing one. When <paramref name="onlyIncrease"/> is true an existing bill is only ever raised, never lowered.
        /// Does not refresh the bench UI - callers batch that via <see cref="RefreshBillsTab"/>.
        /// </summary>
        // `markManaged` (2026-08-30): von Hand im Bill-Tab hinzugefuegte Einzelauftraege galten als
        // "wizard generated" - das fuehlte sich nicht richtig an.
        // Die Marke bedeutete bisher "durch diesen Mod entstanden" - und das traf auch den Einzelklick auf
        // eine Werkbank, den der Spieler gemacht hat. Fuer den Reset von Arm my Colony ist aber die ABSICHT entscheidend, nicht das
        // Fenster: was der Assistent selbst angelegt hat, darf er wieder aufraeumen; was der Spieler einzeln angeklickt
        // hat, zaehlt wie ein von Hand an der Werkbank gesetzter Auftrag und bleibt stehen.
        public static BillChange AddOrUpdateAmmoBill(Building_WorkTable bench, RecipeDef recipe, int targetCount, bool onlyIncrease, bool markManaged = true)
        {
            // SICHERHEITSNETZ (2026-09-04): niemals einen Auftrag an eine Bank haengen, die das Rezept
            // nicht fuehrt. RimWorlds billStack.AddBill prueft das NICHT - es nimmt alles an, und der
            // Fehler faellt erst dem Kolonisten auf die Fuesse (InvalidCastException in
            // MakeUnfinishedThingIfNeeded, Endlosschleife im Log). Die Aufrufer waehlen die Bank jetzt
            // richtig; dieser Riegel sorgt dafuer, dass ein kuenftiger Aufrufer denselben Fehler nicht
            // noch einmal machen kann. Laut statt still: ein verschluckter Auftrag waere seinerseits ein
            // Raetsel.
            if (!BenchCanRun(bench, recipe))
            {
                Log.Warning("[CE Reloaded] Auftrag abgelehnt: " + (recipe?.defName ?? "?") + " laeuft nicht an "
                    + (bench?.def?.defName ?? "?") + " - Bank uebersprungen statt einen Absturz zu erzeugen.");
                return BillChange.Unchanged;
            }
            Bill_Production existing = bench.billStack.Bills.OfType<Bill_Production>()
                .FirstOrDefault(b => b.recipe == recipe && b.repeatMode == BillRepeatModeDefOf.TargetCount);

            if (existing != null)
            {
                if (onlyIncrease && targetCount <= existing.targetCount) return BillChange.Unchanged;
                if (existing.targetCount == targetCount) return BillChange.Unchanged;
                existing.targetCount = targetCount;
                return BillChange.Updated;
            }

            int minSkill = CEReloadedMod.settings != null ? CEReloadedMod.settings.ammoBillMinSkill : 0;
            int maxSkill = CEReloadedMod.settings != null ? CEReloadedMod.settings.ammoBillMaxSkill : 20;
            Bill_Production newBill = new Bill_Production(recipe)
            {
                targetCount = targetCount,
                repeatMode = BillRepeatModeDefOf.TargetCount,
                allowedSkillRange = new IntRange(minSkill, maxSkill)
            };
            bench.billStack.AddBill(newBill);
            if (markManaged) MarkBillAsManaged(newBill);
            return BillChange.Added;
        }

        /// <summary>Adds a fresh "do X times" (RepeatCount) production bill for a WEAPON - one batch run, not an
        /// open-ended target. Deliberately ALWAYS adds a new bill rather than merging into an existing one: producing
        /// missing weapons is a one-off top-up, so leaving prior bills untouched and appending the new amount is the
        /// least surprising behaviour. Forces the chosen stuff/material for made-from-stuff weapons and stamps the
        /// weapon-production skill range from the mod settings. Returns the created bill.</summary>
        // Einen bestehenden "mache N Stueck"-Auftrag ANHEBEN statt einen zweiten danebenzulegen (2026-08-30):
        // jeder Klick auf "+1 workbench" legte einen neuen Auftrag an - inhaltlich richtig, aber ueberladen.
        // Besser die Menge zum bestehenden addieren, dasselbe Verhalten wie bei "until". Gleiches Rezept,
        // gleiche Bank, und - wo das Rezept ein Material kennt -
        // ein Auftrag, der DIESES Material zulaesst; sonst entstuende aus zwei Klicks ein Auftrag mit dem falschen.
        public static Bill_Production AddOrRaiseOnceBill(Building_WorkTable bench, RecipeDef recipe, int runs, ThingDef stuff, bool markManaged = true)
        {
            // Dritte Anlegestelle, gleicher Riegel (Regel-7-Grep 2026-09-04).
            if (!BenchCanRun(bench, recipe))
            {
                Log.Warning("[CE Reloaded] Auftrag abgelehnt: " + (recipe?.defName ?? "?") + " laeuft nicht an "
                    + (bench?.def?.defName ?? "?") + " - Bank uebersprungen statt einen Absturz zu erzeugen.");
                return null;
            }
            runs = Mathf.Max(1, runs);
            Bill_Production existing = bench?.billStack?.Bills.OfType<Bill_Production>().FirstOrDefault(b =>
                b.recipe == recipe && b.repeatMode == BillRepeatModeDefOf.RepeatCount
                && (stuff == null || b.ingredientFilter == null || b.ingredientFilter.Allows(stuff)));
            if (existing != null)
            {
                existing.repeatCount += runs;
                return existing;
            }
            return AddWeaponBillOnce(bench, recipe, runs, stuff, markManaged);
        }

        public static Bill_Production AddWeaponBillOnce(Building_WorkTable bench, RecipeDef recipe, int repeatCount, ThingDef stuff, bool markManaged = true)
        {
            // Dasselbe Sicherheitsnetz wie bei AddOrUpdateAmmoBill - siehe dort. Genau ueber diesen Weg
            // ist der beobachtete Satchel-charge-Auftrag an der Munitionsbank entstanden.
            if (!BenchCanRun(bench, recipe))
            {
                Log.Warning("[CE Reloaded] Auftrag abgelehnt: " + (recipe?.defName ?? "?") + " laeuft nicht an "
                    + (bench?.def?.defName ?? "?") + " - Bank uebersprungen statt einen Absturz zu erzeugen.");
                return null;
            }
            int minSkill = CEReloadedMod.settings != null ? CEReloadedMod.settings.weaponBillMinSkill : 0;
            int maxSkill = CEReloadedMod.settings != null ? CEReloadedMod.settings.weaponBillMaxSkill : SkillRecord.MaxLevel;
            Bill_Production newBill = new Bill_Production(recipe)
            {
                repeatMode = BillRepeatModeDefOf.RepeatCount,
                repeatCount = Mathf.Max(1, repeatCount),
                allowedSkillRange = new IntRange(minSkill, maxSkill)
            };
            // Force the material: for a made-from-stuff weapon the only ingredient is its stuff, so restricting the
            // bill's ingredient filter to the chosen stuff makes crafters use exactly that material.
            if (stuff != null && recipe.ProducedThingDef != null && recipe.ProducedThingDef.MadeFromStuff)
            {
                newBill.ingredientFilter.SetDisallowAll();
                newBill.ingredientFilter.SetAllow(stuff, true);
            }
            bench.billStack.AddBill(newBill);
            if (markManaged) MarkBillAsManaged(newBill);
            return newBill;
        }

        // The BEST combat round the weapon can fire, not the average over all of them (2026-08-09,
        // d3-score-averages-all-ammo). The old averaging version made a weapon score WORSE the more ammo types
        // existed for it: CE's pump shotgun fires buck, slug, beanbag and electroslug, and the last two - real
        // rounds with ~9 damage and no penetration, so they survive the dmg<=1 utility filter - dragged the
        // same gun from 47.5 down to 29.6. Clearly several ammo types should never lead to a worse
        // rating.
        //
        // "Best" is measured with the same effShot = dmg * PenMult(ap) the caller then scores with, NOT by raw
        // damage - otherwise a high-damage/no-penetration round would beat the AP round that actually performs
        // better downstream, and the pick would disagree with the number it feeds.
        //
        // usableVariants is the count of rounds that passed the utility filter - the caller turns it into the
        // small variety bonus ("but really small"). It is a BONUS on purpose: after this
        // change an extra ammo type can only ever raise a weapon's score or leave it alone, never lower it.
        //
        // researchedOnly: restricts the pick to rounds the colony can actually make. False for the DISPLAYED
        // score (theoretical value is fine and even correct there - we want to see how good weapons are in
        // theory), true wherever
        // the mod SUGGESTS or hands out a weapon. If the filter would leave nothing at all, it is ignored
        // rather than scoring the weapon at zero - a gun with no researched ammo is still the gun it is, and
        // the assign path has its own producible/ammo checks for that case.
        private static void GetProjectileBestStats(ThingDef weapon, out float bestDamage, out float bestAP,
            out int usableVariants, out ThingDef bestAmmo, bool researchedOnly = false)
        {
            bestDamage = 0f;
            bestAP = 0f;
            usableVariants = 0;
            bestAmmo = null;

            AmmoSetDef ammoSet = GetAmmoSet(weapon);
            if (ammoSet != null && ammoSet.ammoTypes != null)
            {
                for (int pass = 0; pass < 2; pass++)
                {
                    // pass 0 honours researchedOnly; pass 1 is the "nothing survived the filter" fallback.
                    bool filter = researchedOnly && pass == 0;
                    float bestEff = 0f;
                    foreach (var link in ammoSet.ammoTypes)
                    {
                        if (link.projectile == null || link.projectile.projectile == null) continue;
                        if (filter && (link.ammo == null || !IsAmmoResearched(link.ammo))) continue;
                        ExtractDamageAndAP(link.projectile, out float dmg, out float ap);
                        if (dmg <= 1f) continue; // utility rounds (EMP, smoke, flare) carry ~0 direct damage
                        usableVariants++;
                        float eff = dmg * PenMult(ap);
                        if (eff > bestEff) { bestEff = eff; bestDamage = dmg; bestAP = ap; bestAmmo = link.ammo; }
                    }
                    if (usableVariants > 0 || !filter) return;
                    // filter emptied the set - fall through to pass 1 and score the weapon unrestricted.
                }
                return;
            }

            if (weapon.Verbs != null)
            {
                var verb = weapon.Verbs.FirstOrDefault(v => v.defaultProjectile != null);
                if (verb != null && verb.defaultProjectile.projectile != null)
                {
                    ExtractDamageAndAP(verb.defaultProjectile, out bestDamage, out bestAP);
                    usableVariants = 1;
                }
            }
        }

        // The small variety bonus on top of the best-round pick: "a small bonus for ammo variety
        // (but really small)". 2% per extra combat round beyond the first, capped at 6%,
        // so a four-variant caliber gains ~6% over a single-variant one - enough to break a tie between two
        // otherwise equal guns in favour of the flexible one, far too little to reorder the list on its own.
        private const float AmmoVarietyBonusPerExtra = 0.02f;
        private const float AmmoVarietyBonusCap = 0.06f;

        private static float AmmoVarietyMult(int usableVariants)
            => 1f + Mathf.Min(AmmoVarietyBonusCap, Mathf.Max(0, usableVariants - 1) * AmmoVarietyBonusPerExtra);

        private static void ExtractDamageAndAP(ThingDef projDef, out float damage, out float ap)
        {
            ExtractDamageAndAP(projDef, out damage, out ap, out _);
        }

        // apBlunt matters as much as apSharp for anything armored (2026-08-02): CE does NOT simply discard a
        // shot that fails to beat sharp armor - ArmorUtilityCE.GetAfterArmorDamage turns the deflection into a
        // real Blunt DamageInfo whose penetration is the projectile's armorPenetrationBlunt (verified in the
        // decompile, ArmorUtilityCE.cs ~line 365). A 12ga slug has sharp 6 / blunt 85 against a centipede's
        // sharp 20 / blunt 45: the sharp hit bounces, the blunt one goes straight through. Reading sharp alone
        // is why the model rated buckshot above slugs as an anti-mech round - the exact opposite of the truth.
        private static void ExtractDamageAndAP(ThingDef projDef, out float damage, out float ap, out float apBlunt)
        {
            damage = 0f;
            ap = 0f;
            apBlunt = 0f;
            if (projDef == null || projDef.projectile == null) return;

            // CE stores the real per-shot damage in ProjectileProperties.damageAmountBase (private), NOT in
            // damageDef.defaultDamage - which is 0 for CE bullets. GetDamageAmount() reads it correctly (and
            // works for vanilla projectiles too). Reading damageDef.defaultDamage was why every CE gun scored 0.
            damage = projDef.projectile.GetDamageAmount((Thing)null);
            if (damage >= 99999f) damage = 0f; // Vanilla/CE trick for massive non-lethal explosions

            // CE stores sharp armor penetration on its own ProjectileProperties subclass. Typed cast, no reflection.
            if (projDef.projectile is ProjectilePropertiesCE ppce)
            {
                ap = ppce.armorPenetrationSharp;
                apBlunt = ppce.armorPenetrationBlunt;
                // Shot shells fire multiple pellets per trigger pull (12ga buckshot: 9 x 8 dmg = 72 per shot).
                // damageAmountBase is PER PELLET, so without this every shotgun read at ~1/9 of its real damage
                // and the whole class sank to the bottom of the ranking.
                if (ppce.pelletCount > 1) damage *= ppce.pelletCount;
            }
        }

        /// <summary>Builds display labels for a set of benches, numbering duplicates of the same def ("Machining table 1", "…2").</summary>
        public static Dictionary<Building_WorkTable, string> BuildBenchLabels(IEnumerable<Building_WorkTable> benches)
        {
            Dictionary<Building_WorkTable, string> labels = new Dictionary<Building_WorkTable, string>();
            foreach (var group in benches.GroupBy(b => b.def.label))
            {
                List<Building_WorkTable> members = group.ToList();
                if (members.Count == 1)
                {
                    labels[members[0]] = members[0].def.label;
                }
                else
                {
                    for (int i = 0; i < members.Count; i++)
                        labels[members[i]] = string.Format("{0} {1}", members[i].def.label, i + 1);
                }
            }
            return labels;
        }

        // ---- Bestaetigungs-Abfragen ---------------------------------------------------------------------

        // RimWorlds Dialog_MessageBox hat eine FESTE Groesse - `InitialSize => new Vector2(640f, 460f)`,
        // aus dem Dekompilat, nicht geraten. Bei zwei Zeilen Text sind das rund zwei Drittel Fensterhoehe
        // fuer zwei Zeilen und eine Knopfreihe, dazwischen Leere (2026-08-14 aufgefallen). Der Inhalt selbst
        // rechnet dagegen alles aus `inRect`: Text von oben bis `inRect.height - 35 - 5`, Knoepfe bei
        // `inRect.height - 35`. Ein kleineres Fenster ergibt also dieselbe Anordnung, nur ohne die Luecke -
        // deshalb genuegt es, die Hoehe zu ueberschreiben, statt den Dialog nachzubauen.
        //
        // EINE Stelle fuer ALLE Bestaetigungen des Mods (es sind zwoelf), damit die naechste nicht wieder
        // in der Standardgroesse aufgeht. Signatur absichtlich Zeichen fuer Zeichen wie
        // Dialog_MessageBox.CreateConfirmation - die Aufrufstellen aendern nur den Namen.
        public static Dialog_MessageBox CompactConfirmation(TaggedString text, Action confirmedAct,
            bool destructive = false, string title = null, Action cancelAct = null)
        {
            return new Dialog_ConfirmCompact(text, confirmedAct, destructive, title, cancelAct);
        }

        public class Dialog_ConfirmCompact : Dialog_MessageBox
        {
            public Dialog_ConfirmCompact(TaggedString text, Action confirmedAct, bool destructive, string title, Action cancelAct = null)
                // Dieselben Argumente, die CreateConfirmation selbst durchreicht (aus dem Dekompilat):
                // Bestaetigen/Zurueck, accept = dieselbe Tat, cancel = leere Aktion (damit Escape schliesst,
                // ohne etwas auszufuehren). cancelAct (2026-09-03): der Zurueck-Knopf darf ein eigenes
                // Ziel haben - die Neuzuweisungs-Nachfrage fuehrt damit zurueck ins Waffen-Auswahlfenster.
                : base(text, "Confirm".Translate(), confirmedAct, "GoBack".Translate(), cancelAct, title,
                       destructive, confirmedAct, cancelAct ?? delegate { })
            {
            }

            public override Vector2 InitialSize
            {
                get
                {
                    // Die Groessenkette exakt wie im Original: Fensterbreite 640, davon 2x18 Rand
                    // (Window.Margin) und 16 fuer die Bildlaufleiste -> das ist die Breite, mit der der
                    // Dialog seinen Text umbricht. Mit derselben Breite messen, sonst rechnet man an der
                    // Umbruchstelle vorbei.
                    const float w = 640f, margin = 18f, scrollbar = 16f, buttons = 35f, gap = 5f, titleH = 42f;
                    GameFont prev = Text.Font;
                    Text.Font = GameFont.Small;
                    float textH = Text.CalcHeight(text, w - 2f * margin - scrollbar);
                    Text.Font = prev;
                    float inner = (string.IsNullOrEmpty(title) ? 0f : titleH) + textH + gap + buttons;
                    // Nach OBEN auf die bisherigen 460 gedeckelt: diese Aenderung darf einen Dialog nur
                    // kleiner machen, nie groesser. Nach unten 180, damit eine Ein-Zeilen-Frage noch wie ein
                    // Dialog aussieht und nicht wie ein Streifen. Ein paar Pixel Luft, und selbst wenn ich
                    // mich verrechnet haette, ist die Richtung harmlos: der Text sitzt in einem Scrollview,
                    // zu wenig Hoehe scrollt, es wird nichts abgeschnitten.
                    return new Vector2(w, Mathf.Clamp(inner + 2f * margin + 8f, 180f, 460f));
                }
            }

            public override void DoWindowContents(Rect inRect)
            {

                // SELBST ABFANGEN (2026-08-15). Ich habe die Kette heute zu Ende gelesen und dabei ein
                // Glied gefunden, das im Kommentar oben noch fehlte: KeyBindingDef.KeyDownEvent liefert
                // FALSE, sobald irgendwo ein QuickSearchWidget den Fokus hat - dann wird
                // Notify_PressedCancel nie gerufen, und keine Einstellung an diesem Kasten kann das
                // heilen. Fuer UNS scheidet genau dieser Verdacht allerdings aus: der Mod benutzt
                // nirgends ein QuickSearchWidget (gegrept, alle Suchfelder sind Widgets.TextField).
                // Damit ist die Kette aus dem Quelltext lueckenlos UND der Kasten muesste schliessen -
                // er tut es laut Beobachtung nicht. Was auch immer dazwischenfunkt: dieser Kasten braucht
                // RimWorlds Umweg nicht. Kommt die Taste hier an, erledigen wir es selbst - dieselben
                // drei Schritte, die Dialog_MessageBox.OnCancelKeyPressed auch machen wuerde.
                // Doppelt schliessen kann es nicht: Use() verbraucht das Ereignis, und wenn RimWorlds
                // eigener Weg im selben Bild schon geschlossen hat, sind wir gar nicht mehr offen.
                // Das Diagnoselog darueber bleibt, bis er es einmal mitgeschickt hat - es sagt weiterhin,
                // WARUM der normale Weg nicht greift, und das will ich wissen, bevor ich es ausbaue.
                if (Event.current != null && Event.current.type == EventType.KeyDown
                    && Event.current.keyCode == KeyCode.Escape && IsOpen)
                {
                    if (cancelAction != null) cancelAction();
                    Close();
                    Event.current.Use();
                    return;
                }
                base.DoWindowContents(inRect);
            }

            public override void OnCancelKeyPressed()
            {
                base.OnCancelKeyPressed();
            }
        }
    }
}
