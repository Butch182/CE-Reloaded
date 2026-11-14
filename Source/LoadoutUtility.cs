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
            // The item this breakdown describes (2026-08-30): the colony-wide 'Missing' line uses it to compute its
            // OWN number (ColonyAssignedDemand - stock) instead of reusing the pawn-specific one.
            public Def def;
            // How much of the counted stock above is marked "do not pick up" (2026-08-09). NOT a segment of
            // its own and deliberately NOT subtracted from anything - those units are already sitting inside
            // `available`/`assignedInStore`/... exactly like any other stock. It only drives the "!" marker
            // and its own tooltip line, which is the whole point: the number stays honest AND the player can
            // see that part of it still has to be released. Total must not include it, or every bar would
            // double-count its own loot.
            public int forbidden;
            // How much of the counted stock lies LOOSE on the map instead of in a storage zone (2026-08-13): shown
            // uniformly, including its own color in the availability bar, like the rest. Just like `forbidden` it
            // is NOT additional stock but a subset of what is already counted - so it is not in Total, but cut out
            // of `available` and drawn in its own color.
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
        // "Do not pick up" stock as its own segment (2026-08-13, slightly lighter, shifted toward yellow).
        // Deliberately a GREEN, not orange: the units exist and are counted - they are just forbidden. A warning
        // color would make them read as missing. It sits between the available green (0.4/0.85/0.4) and the
        // storage yellow, so it reads as "the same, but different" without introducing a new meaning color.
        private static readonly Color AvailForbiddenGreen = new Color(0.68f, 0.9f, 0.42f);
        // "Lying around loose" (2026-08-13). Same idea as the forbidden green: it IS there, just not on a shelf -
        // so a neighbor of the available color, not a warning color. Slightly paler and pulled toward grey-green
        // so the three segments (available / on the ground / forbidden) stay distinguishable in that order even
        // side by side.
        private static readonly Color AvailGroundGreen = new Color(0.52f, 0.78f, 0.55f);
        private static readonly Color AvailAssignedInStoreBlue = new Color(0.55f, 0.72f, 0.95f);
        private static readonly Color AvailAssignedCarriedBlue = new Color(0.26f, 0.42f, 0.72f);
        private static readonly Color AvailCarriedGrey = new Color(0.40f, 0.40f, 0.44f);
        // Public - also the one "in production" color Dialog_SquadSetup's own ReqStatus dot convention uses
        // now (2026-07-30 harmonization), replacing that spot's own ColInfo (blue), which never meant
        // anything in-production-specific elsewhere and just happened to be reused there.
        public static readonly Color AvailProductionBeige = new Color(0.78f, 0.68f, 0.48f);
        private static readonly Color AvailMissingRed = FulfillColor(FulfillStatus.Missing);

        // The two sub-items of the tooltip use EXACTLY the color of their own bar segment, computed from the
        // same constant instead of a second value written next to it (the forbidden line used to be orange while
        // its bar segment had long been green - it belongs in the bar's green). Orange stays reserved for the
        // exclamation mark and the outline: that is the WARNING, the line is only a breakdown.
        // Computed once at load, not per mouseover - one string per call would be cheap here, but pointless.
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

                // THE line behind the "Still missing: 0" bug (2026-08-17, both sides changed). The calculation
                // one line above compares THIS cell's demand with the stock of the WHOLE colony: 50 required,
                // 102 present, so "nothing missing" - even when all colonists' assignments together are far
                // above 102 and in truth nothing is left for this one. For weapons it even counted the weapon
                // another colonist is currently holding.
                // The round-robin allocation knows better, for exactly this colonist. If it has no answer (-1:
                // no template, or the item is not in it), the number above stays - colony-wide bars without a
                // pawn are unaffected, they pass no pawn at all.
                //
                // The comparison is against the ALLOCATION, not the colonist's shortfall: `neededCount` is
                // THIS cell's demand, and it belongs against what the colonist gets at all. Plugging in the
                // shortfall directly made two cells of the same colonist show the same number again - a bug
                // that had already been fixed once before.
                int alloc = Dialog_AssignLoadouts.ClaimedAllocation(pawn, def);
                if (alloc >= 0) seg.missing = Mathf.Max(0, neededCount - alloc);
            }
            return seg;
        }

        // Shared "where is it" breakdown for an availability bar's extraTip - storedTT is WHERE it physically
        // sits (a shelf/rack), carriedTT/assignedTT are WHO has it. One implementation so every caller that
        // bothers to compute these three strings (currently Dialog_GenerateLoadouts, Dialog_SquadSetup) shows
        // byte-for-byte the same section-headered format, instead of each hand-rolling its own join.
        // The separate "marked do not pick up" section was REMOVED again on 2026-08-15 - it appeared twice in
        // the tooltip, once is enough.
        // This does not reverse the 2026-08-09 requirement to list it separately in the tooltip; it fulfills
        // it in the right place: since the bar rework the number has long been at the TOP of the breakdown
        // ('Marked "do not pick up": 4'), together with all other numbers. The section below repeated it and
        // usually named as location exactly what the line above already said ('Lying on the ground: 4' above
        // vs. 'On the ground: 4' below).
        // What goes away and what does not: the NUMBER stays (top), the warning stays (orange outline and "!"
        // on the bar), the exact storage location of a FORBIDDEN stack goes away - it was only listed here.
        // That loss is accepted deliberately; "In storage:" still names where the stock as a whole is.
        // stuffTT: "which material do I actually have" (2026-08-09) - see BuildStuffBreakdownLines.
        // Null for anything not made from stuff, and also when the whole stock is a single material; the line
        // only appears where it says something.
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
                // The forbidden share is CUT OUT of the available segment, not drawn in addition (2026-08-13):
                // those units are already contained in `available`, an extra bar on top would distort the sum.
                // Hence Min against available - if forbidden units sit in other buckets (e.g. already
                // assigned), they stay counted there and the outline plus the exclamation mark carry the
                // warning as before.
                int forbiddenShown = Mathf.Min(Mathf.Max(0, seg.forbidden), seg.available);
                // The ground share is cut out AFTER the forbidden one and capped against the remainder: a
                // forbidden stack lying on the ground is in BOTH numbers, and subtracting it twice would
                // distort the bar.
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

            // TipWanted first (2026-08-13): BuildAvailabilityTooltip assembles an eight-line block, and this bar
            // sits in EVERY row of EVERY list in the mod. RimWorld discarded the text anyway while the mouse was
            // elsewhere - see TipWanted.
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
        // ============ heading/trailer are why the trap above is NOT enough (2026-08-17) ============
        // Observed: the ammo cell rendered this block in color, the weapon cell rendered the same block plain
        // white - a sign of missing central code. The goal is one central path so every caller behaves the
        // same, unless there is a VERY good reason against it.
        //
        // The central function already existed; it was not the cause. The difference was ONE level above:
        // the weapon cell built its own heading and trailer around the result
        //     string tip = mainW.LabelCap + ":\n" + BuildAvailabilityTooltip(seg) + "\n\n" + "...".Translate(x);
        // which made the WHOLE chain a TaggedString - its implicit conversion back to string (right here, on
        // the assignment to `string tip`) calls RawText.StripTags() and deletes every <color> this function
        // just added. The ammo cell only escaped because it goes through string.Join: that calls ToString(),
        // which keeps the tags.
        // A trap you cannot SEE at the call site cannot be caught there either - so heading and trailer can
        // now be passed IN here instead of being built around the result. The final concatenation then always
        // happens inside this function, and a new caller can no longer throw the coloring away by accident.
        // The colony-wide breakdown on ONE line - same line keys and colors as BuildAvailabilityTooltip, but
        // only the items with a value > 0 (Total always), separated by commas. For cell tooltips with many
        // items (2026-08-30: the weapon explanation text was taller than the screen).
        public static string AvailabilityOneLiner(AvailabilitySegments seg)
        {
            var parts = new List<string> { "CER_LoadoutUtility_AvailTotal".Translate(seg.Total.ToString()).ToString() };
            void Add(string key, int v, string hex) { if (v > 0) parts.Add("<color=" + hex + ">" + key.Translate(v.ToString()).ToString() + "</color>"); }
            // "Equipped" belongs in the line whenever a colonist is in context (2026-09-06): Total counts this
            // bucket, the short form left it out - so the items never added up to the number they stand next
            // to. Same rule as in the long form (pawn context only).
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
            // "Total" is the STOCK again, and only that (2026-08-17). Until then it was `seg.Total + seg.missing`,
            // i.e. "how many are needed" - which made sense while the shortfall was colony-wide. Since the
            // shortfall became per-colonist it no longer does, and the number became unreadable: the 'Total'
            // line was not constant colony-wide but changed with the viewed colonist (14 / 20 / 12) for three
            // colonists with identical demand and identical stock, and the same weapon showed two different
            // numbers under the same label depending on whether the window had a shortfall line or not.
            // A label that means something different per window and per colonist is not a label. "Total" now
            // means the same everywhere: what the colony has of it. What is needed is in the shortfall line below.
            // The BAR itself still uses seg.Total + seg.missing unchanged - it is supposed to show the shortfall
            // as its own segment.
            string tip = "CER_LoadoutUtility_AvailTotal".Translate(seg.Total.ToString()).ToString();
            // Equipped is the one status line that is structurally meaningless without a specific pawn in
            // context (it can only ever be nonzero when BuildAvailabilitySegments carved it out of a pawn's
            // own carried/assigned count) - shown only when seg.pawnContext, unlike every other line below,
            // which stays visible even at 0 (2026-08-01, refines the "always show every line" rule to not
            // include a line that literally cannot be anything but 0 in a colony-wide, no-pawn bar).
            if (seg.pawnContext)
                tip += "\n  <color=#66D966>" + "CER_LoadoutUtility_AvailEquipped".Translate(seg.equippedByPawn.ToString()).ToString() + "</color>";
            tip += "\n  <color=" + availableHex + ">" + "CER_LoadoutUtility_AvailAvailable".Translate(seg.available.ToString()).ToString() + "</color>";
            // Ground and "do not pick up" appear as SUB-ITEMS directly under Available (2026-08-15). This is not
            // just cosmetic: the bar cuts both segments out of `available` (see the Min capping in
            // DrawAvailabilityBar), so they really are subsets of it. Previously they sat at the very bottom,
            // after "still missing" - there the ground share read like another bucket NEXT TO the others, and
            // the sum seemed not to add up.
            // Indented two spaces more than the level above, and still only when > 0: a permanent zero line on
            // every bar in the game would be noise.
            // CAPPED like in the bar, not the raw number (2026-08-16: "Lying on the ground: 53" appeared
            // indented under "Available: 35", i.e. larger than the value it hangs under). The contradiction:
            // the indentation claims "subset of Available", but the number next to it came unfiltered from the
            // stock count - and 53 loose medicine are spread across SEVERAL buckets (available,
            // assigned-in-storage). The bar has always capped for exactly that reason; now the line uses the
            // same formula as its own bar segment, in the same order (forbidden first, ground against the
            // rest) - line and strip can no longer diverge.
            // What disappears is the total amount on the ground. That is intentional: under the heading
            // "Available" the question is "how much OF THAT is lying around", and the total previously stood
            // in a place where it made a false statement.
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
            // Computed COLONY-WIDE, not per colonist (2026-08-30: otherwise the line below would also have to
            // read 1, not 2). This breakdown is headed 'Colony-wide', so the line now answers the colony-wide
            // question too: how many units are assigned that do not exist. The per-colonist shortfall is in the
            // block above (Dialog_AssignLoadouts.PawnBlock) and in the strip - both still use seg.missing.
            int colonyMissing = seg.def != null ? Mathf.Max(0, ColonyAssignedDemand(seg.def) - seg.Total) : seg.missing;
            if (colonyMissing > 0 || seg.missing > 0 || seg.missingIsPawnSpecific)
            {
                // Always red now - see DrawAvailabilityBar's own Seg() call for the same 2026-08-01 reversal.
                tip += "\n  <color=#E65959>" + "CER_LoadoutUtility_AvailStillMissing".Translate(colonyMissing.ToString()).ToString() + "</color>";
            }
            if (!string.IsNullOrEmpty(extraTip)) tip += "\n\n" + extraTip;
            // Both deliberately at the end and both as plain strings: whatever a caller passes in is already
            // resolved, so the tags above survive the return.
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

        // ====== "Can THIS bench run THIS recipe?" - one question, one answer (2026-09-04) ======
        //
        // Triggered by a crash loop, visible in Player.log:
        //   JobDriver threw exception in toil MakeUnfinishedThingIfNeeded's initAction for pawn <pawn>
        //   driver.job=(DoBill ... A = Thing_AmmoBench ... Giver = ... DoBillsAmmoBench)
        //   System.InvalidCastException: Specified cast is not valid.
        // None of the stack frames are ours - RimWorld trips over a bill that should not exist on this bench
        // at all. The colonist aborts, starts over, and the log fills up.
        //
        // How it happened: the supply window derived a row's bench list from the PRODUCT
        // (GetWorkbenchesFor(weapon) collects the recipeUsers of ALL recipes that make this item) but then
        // created the bill with e.recipe. As long as an item has only one recipe this never shows. With
        // thrown explosives it does: in CE, MakeSatchelCharge inherits from GrenadeRecipeBase with
        // <recipeUsers><li>TableMachining</li></recipeUsers>, yet the ammo bench was still in the list - so
        // a machining-table recipe ended up on the ammo bench. A classic "one question, two calculations".
        //
        // AllRecipes is explicitly the right source, not recipeUsers: RimWorld attaches a recipe to a bench
        // in TWO ways (recipeUsers on the recipe OR recipes on the bench def) and merges both exactly here.
        // Checking only recipeUsers misses the second half - Dialog_GenerateAmmoBills has always asked
        // AllRecipes, this spot did not.
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
        // AddOrUpdateAmmoBill directly, silently, with no review step at all, which broke this mod's own
        // "nothing happens without going through the popup" convention
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
                // STOCK counts here just like a running bill (2026-08-13: 30,000 rounds lay on the ground, the
                // availability bar showed green 29,988 - and the same row proposed crafting 200 more).
                // That was the SIXTH occurrence of the same pattern and the nastiest, because it does not
                // miscompute the earlier gate but BYPASSES it COMPLETELY: the callers filter their shortfalls
                // cleanly via AmmoOnHand, and afterwards this function puts the row back on top unchecked
                // ("a consumable needs a standing bill"). It never looked at stock, only at recipe, bench and
                // existing bills.
                // The function's purpose stays: a consumable that is REALLY short still gets its row, even if
                // the raw shortfall would not otherwise have earned it one.
                // WITHOUT GroundStockCount (2026-08-15): WeaponStoredCount counts EVERY spawned unit on a
                // colony map, zoned or not - so loose items are already included, while GroundStockCount
                // returns exactly those loose items AGAIN.
                // Failure case: 30 medicine lying around unzoned, demand 40. It computed 30 + 30 = 60, i.e.
                // "covered", and the standing-bill row silently vanished from the supply window although 10
                // are missing.
                // The other call sites in the mod pair GroundStockCount with IngredientStockCount, and THAT is
                // disjoint (zone vs. non-zone) - only here was the map-wide total used.
                // CarriedStockCount stays: what sits in backpacks is not spawned.
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

        /// <summary>"Is this generic entry an AMMO placeholder" - prefix only, without GetWeaponForGenericAmmo's
        /// DefDatabase lookup. Meant for the hot path (Patch_Loadout_MultiParentSlots hooks Loadout.Slots,
        /// which CE's think tree reads per colonist and decision) - there it only matters WHETHER it is ammo,
        /// never which weapon is behind it.
        /// Deliberately true even when the weapon def no longer exists: a placeholder without a weapon is
        /// still an ammo placeholder and must not pass as a supply entry.</summary>
        public static bool IsGenericAmmoDef(Def genericDef)
        {
            LoadoutGenericDef gd = genericDef as LoadoutGenericDef;
            return gd != null && gd.defName.StartsWith(GenericAmmoPrefix);
        }

        /// <summary>The ONE definition of "this slot is ammo" - concrete ammo OR a generic ammo placeholder
        /// ("Any ammo for X").
        /// Why central (2026-08-14, after a repeated bug): the same question was answered separately in four
        /// places, and two of them only counted concrete ammo. Result: tick "Any ammo" on two weapons and
        /// untick one again, and a generic own-slot remained that Patch_Loadout_MultiParentSlots did not
        /// recognize as ammo - it then let the template's default ammo come back for BOTH weapons, ticked
        /// next to the still-set "Any ammo". The earlier fix (2026-08-13) had only corrected the dialog side,
        /// not the patch side that actually decides. Four places, one answer - hence here.</summary>
        /// <summary>Real ammo - not the throwables that CE technically also lists as AmmoThing.</summary>
        // 2026-08-31, "Optimize ammo bills" (bills that are certainly not needed did not show up as
        // removable): the candidate list filtered with !IsSelfAmmoWeapon - and IsSelfAmmoWeapon is true for
        // EVERY ammo (the function ends with "|| w is AmmoDef"). So the list had been empty since 2026-08-14,
        // and the "Remove unneeded bills" button permanently showed (0). The intent was "exclude thrown
        // explosives" - and that is separated by exactly the same condition as in the slot merge
        // (Patch_Loadout_MultiParentSlots.IsRealAmmoSlot): a throwable is a weapon def, real ammo is not.
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
        // Cached (2026-08-14, performance pass): the answer depends only on immutable def data, but the check
        // itself walks the whole base-type chain comparing STRINGS - and it sits in the solver's candidate
        // loops (PickBest, PickSidearm, PickGrenade check it per candidate), which rerun completely every
        // 150 ms while the sliders are dragged. Session cache like its neighbors (utilityWeaponCache,
        // weaponBulkCache).
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
            if (w.GetCompProperties<CompProperties_AmmoUser>() != null) return false; // uses real external ammo -> normal flow
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
        // Budget).
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

        // What this weapon costs in MATERIAL, as the silver value of its ingredients. Explicitly the
        // counterpart to MaterialAffordability above, and not the same thing: affordability asks "do we have
        // enough of it", cost asks "how expensive is one". In a resource-rich colony affordability sits at
        // 1.0 for EVERY weapon - from a cheap revolver to a heavy MG - and since the "Cheap weapons <-> Best
        // weapon" slider depended solely on that number until 2026-08-14, it had no effect at all there.
        // That was the observed failure: Economy selected and still a Kord MG, RPG-7 and incendiary launcher
        // planned, at a build cost of 642 steel and 132 components.
        // Material value only, no work time: a colony's labor is not what the player means by "cheap", and
        // workAmount is scaled so differently between mods that it would blur the ordering more than sharpen
        // it.
        // Cached permanently (no TTL like above): recipes and market values are defs and do not change at
        // runtime - only STOCK does, and that lives in MaterialAffordability, not here.
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
            // No recipe (trade or loot only): the weapon's own market value is the most honest substitute.
            // Hardly matters in the planner anyway - without a recipe `producible` is false and the weapon
            // already drops out of the candidates unless it is in stock.
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

        // The weapon list of the MAIN WEAPON picker (single-select mode of Dialog_PickPacks, 2026-09-02):
        // all real weapons except grenades and utility weapons, ranged before melee, within each group by
        // descending score. Built once on open and passed along as a finished list - not per frame.
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

        // Weapons that have NO default template yet - the weapon section of the picker windows from the lazy
        // rework (2026-09-01). Deduplicated by label identity (VirtualLoadoutLabel), the same rule as
        // EnsureDefaultLoadout itself: if the template already exists it is listed as a loadout row in the
        // respective list, and offering the weapon here as well would be the same thing twice.
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
                // Disabled weapons: the checklist does not offer them at all -
                // re-enabling happens in the main-weapon window, which has the disable toggles.
                if (IsWeaponDisabled(d)) continue;
                result.Add(d);
            }
            result.SortBy(d => d.label);
            return result;
        }

        // What ammo do these pawns' ACTUAL assigned loadouts specify? Closes a real, confirmed gap: nothing in the
        // Assign flow (manual or auto) ever checked ammo before this - a colonist could get a freshly-crafted
        // weapon and never have ammo production queued for it, with no popup ever mentioning it.
        // Convenience overload for the common case where the loadout is already live on the pawn.
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
            // (e.g. a Knife at 4/4 available was still checked for production).
            Dictionary<ThingDef, int> stockRemaining = new Dictionary<ThingDef, int>();

            // usedBy = demand semantics, not leftover shortfall (2026-08-31, same correction as in
            // ComputeMissingForCreatedLoadouts.Track): the diamond should say WHO the item is allocated to -
            // even when storage or carriers already cover the demand. Previously the name was only noted after
            // all deductions, so a fully supplied row showed as "allocated to nobody".
            void Note(ThingDef w, Pawn p)
            {
                if (w == null || p == null) return;
                if (!usedByLocal.TryGetValue(w, out List<string> list)) usedByLocal[w] = list = new List<string>();
                if (!list.Contains(p.LabelShortCap)) list.Add(p.LabelShortCap);
            }

            // alreadyCounted: the caller has already deducted the carried stock (quantity path below).
            // Without this exit the binary question "does the pawn carry one at all" would swallow every
            // further unit - a pawn who needs four satchel charges and carries one would, by this line, need
            // none at all (found 2026-08-16 while reviewing the quantity path).
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
                // Declared before the None branch because TrackAllWeaponSlots (local function below) captures it.
                Dictionary<ThingDef, int> sidearmSink = missingSidearms;
                if (display.defaultLoadout)
                {
                    // Sidearms via CE's None loadout (2026-08-25): since the sidearm cell also works without a
                    // main weapon, such a weapon hangs on the anchor via "None" - no main weapon to count, but
                    // the sidearms do count, otherwise the halberd is missing from every supply window while
                    // the cell says "missing".
                    foreach (Loadout sideLoadout in SidearmAssignmentUtility.GetPawnSidearms(p) ?? new List<Loadout>())
                        TrackAllWeaponSlots(sideLoadout, null);
                    continue;
                }
                LoadoutMainAndSide(display, out ThingDef mainW, out _);
                Track(missingMain, mainW, p);
                // EVERY weapon slot, not just the one "main weapon" per template (2026-08-16: items such as the
                // satchel charge were missing from the new window because demand was not computed correctly).
                // The cause is the THIRD repetition of the same thing: LoadoutMainAndSide deliberately NEVER
                // picks a grenade, a thrown explosive, a shield or a utility weapon as "main weapon" (see its
                // comment). A sidearm template containing exactly such an item - the normal case for thrown
                // explosives - returned `null` here, so the demand never showed up in the supply window at all.
                // ComputeMissingForCreatedLoadouts got the same correction on 2026-08-01 and ComputeWeaponDemand
                // on 2026-08-13 - only this ASSIGNED view was left behind both times. That is exactly why the
                // demand side and the cleanup side could disagree; the same set of weapon slots is the
                // precondition for them to agree.
                // Predicate verbatim as in ComputeWeaponDemand.AddAllWeapons: IsWeapon, no utility weapons.
                // Through a local reference to the same dictionary: C# does not allow an out parameter inside
                // a local function (CS1628), and the dictionary is a reference anyway.
                void TrackAllWeaponSlots(Loadout lo, ThingDef skip)
                {
                    if (lo?.Slots == null) return;
                    foreach (LoadoutSlot s in lo.Slots)
                    {
                        // Predicate VERBATIM as in ComputeMissingForCreatedLoadouts (weapons and shields): both
                        // views fill the same window, and any difference between them would be a bug of its
                        // own - the scope toggle would then change the list for reasons unrelated to scope.
                        // The first version of this line had copied ComputeWeaponDemand's predicate instead
                        // (no shields, but no utility weapons either) - caught on review.
                        if (s.thingDef == null || (!s.thingDef.IsWeapon && !IsShieldApparel(s.thingDef))) continue;
                        // The main weapon was already counted one line above - otherwise it would count twice.
                        if (skip != null && s.thingDef == skip) { skip = null; continue; }
                        // The QUANTITY counts too, again following the Created view: four satchel charges in
                        // one slot are four units of demand, not one. Track claims exactly one unit from
                        // storage or running production per call - so call it as often as still needed.
                        // Deduct carried items first (PawnCarriedCount counts equipment, inventory and apparel
                        // together), because Track's own carrier check is binary and would swallow the whole
                        // rest after a single carried unit.
                        int want = Mathf.Max(1, s.count);
                        int carried = PawnCarriedCount(p, s.thingDef);
                        // Note even when carried >= want (2026-08-31): if the pawn already carries everything,
                        // the loop runs zero times and Track is never reached - it is still allocated to them.
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

        // Since 2026-09-07 this ONLY fills the origin columns (templateUsedBy - "which template needs this") -
        // the "Consider created loadouts" scope toggle it was built for on 2026-08-01 is gone (since the lazy
        // rework it is the same set as "assigned").
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
                // usedBy BEFORE the stock/production deduction (2026-08-31): the list answers "which template
                // USES this" - a demand question, not a shortfall question. Previously the name was only noted
                // after both deductions; a fully covered weapon (satchel charge: 4 needed, 5 on the shelf)
                // therefore had an empty list and the circle dot claimed "No saved loadout uses this ammo",
                // while the same role two rows further down (KS-23 ammo, from ComputeAmmoNeedForLoadouts, which
                // always notes at DEMAND) was named correctly. All consumers only do TryGetValue per row - more
                // entries in the dictionary change neither amounts nor row selection.
                if (!weaponUsedByLocal.TryGetValue(w, out List<string> list)) weaponUsedByLocal[w] = list = new List<string>();
                // These lists are shown to the player ("needed by ..."), so they use the DISPLAYED name, not
                // the stored label (2026-08-18) - otherwise exactly these lists would keep saying "[Role] Foo"
                // while the row next to them says "Foo".
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
                // usedBy even at full coverage (2026-08-31) - same correction as in Track above:
                // "which pack uses this" is a demand question, not a leftover shortfall.
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
        // Scope: always the assigned weapons (every colonist's REAL current assignment). The "Consider created
        // loadouts" toggle (2026-08-01 to 2026-09-07) is gone - see Dialog_SupplyLoadouts.Request.
        // "Has anything that affects the supply dialog changed since opening at all?" (2026-08-09). A window
        // takes this signature on open, compares it on close and only calls CheckAndOpenMissingProductionPopup
        // if it differs - otherwise the dialog opens even though the player only looked in.
        //
        // Included is exactly what the player can change in these windows AND what determines the dialog's
        // content: who wears which loadout and what is in it. Slots walks the whole parent chain, so packs,
        // sidearms and the personal ammo attachment are counted too.
        // Deliberately NOT included: storage stock and running bills. Both change on their own while the
        // window is open, and both can only make the dialog SMALLER - if the backlog resolved itself,
        // CheckAndOpenMissingProductionPopup opens nothing anyway.
        // Sorted (pawn id, then def name), because enumeration order is not content.
        // The state for which a supply window ACTUALLY opened last - set in Dialog_SupplyLoadouts'
        // constructor, read on tab switch. Deliberately not saved: after a restart an offer is fine again,
        // since the question is new then.
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
            // "loadout template" depending on this scope flag (the same colonist could show
            // wildly different numbers). Only the visible DEMAND numbers (missingMain etc.
            // below) still depend on scope; the diamond/circle indicators are independent of it.
            ComputeMissingForCreatedLoadouts(out var createdMain, out var createdSidearms, out var createdWeaponUsedBy,
                out var createdPackItems, out var createdPackUsedBy, out var createdGenerics, out var createdSpecifics, out var createdAmmoUsedBy, out var createdPackDemandDefs);
            List<Pawn> allPawns = AllColonyPawns().ToList();
            ComputeAmmoNeedForAssignedPawns(allPawns, out var assignedGenerics, out var assignedSpecifics, out var assignedAmmoUsedBy);
            ScanMissingAssignedWeapons(out var assignedMain, out var assignedSidearms, out var assignedWeaponUsedBy);
            ScanMissingAssignedPackItems(AllColonyPawns().Select(p => (p, AmmoAssignmentUtility.GetPawnPacks(p))),
                out var assignedPackItems, out var assignedPackUsedBy, out var assignedPackDemandDefs);

            // The origin defs come from the SAME scope as the shortfalls - otherwise a row from the assigned
            // scope would carry its origin from the template scope.
            // Assigned scope only (2026-09-07): since the lazy rework there is one template per used weapon,
            // so "created" and "assigned" were the same set. The template calculation above stays - it fills
            // the origin columns (templateUsedBy), not the shortfalls.
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
            // there (e.g. satchel charge/stick bomb at 4/4 available, still proposed for crafting).
            // Value stays the FULL demand (bill targetCount is an absolute replace, per the comment above) -
            // only the FILTER (does this even belong in the popup at all) accounts for stock, not the amount
            // requested once it does.
            // AmmoOnHand instead of IngredientStockCount (2026-08-13): FOURTH occurrence of the same gate,
            // found by grep - and the nastiest, because this popup opens BY ITSELF when the mod window closes.
            // Ground and carried stock were missing here too, so it could fire for ammo that was shown green
            // as "fully covered" right next to it.
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

            // DISPLAY: the full shortfall, including what cannot be crafted. OPEN: still only when something
            // craftable is really included (see the abort condition below, which therefore stays on the
            // producible* sets).
            // 2026-08-13: the same shortfall (an SKS without a recipe, assigned twice, owned once) appeared in
            // the supply window when it opened via Apply, and was missing when it opened by itself on close.
            // The cause was exactly this filter: the sibling path (Dialog_AssignLoadouts.OpenProducePopup)
            // does not filter. Showing is right - the window has its own display for non-craftable rows
            // ("can't be crafted"), and it exists because silently omitting rows reads as "nothing missing".
            // The open condition stays deliberately unchanged so this popup does not start opening unasked
            // for things nobody can do anything about.
            // 2026-08-13, FOURTH occurrence of the same pattern: the pack contents still had the filter. A
            // role with "Any Medicine" reported "Still missing: 42" in the tooltip with no matching row -
            // because herbal medicine is not craftable (see ResolveGenericPackItem). Weapons and sidearms are
            // unfiltered here now, and pack contents are too.
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
                // The number is already net of running bills - it comes from ScanMissingAssignedWeapons, which
                // has done that deduction itself since 2026-07-31. Full reasoning at the field itself.
                weaponCountsNetOfProduction = true
            };
            // Obsolete-bill cleanup suggestions (2026-08-01) - this is the NORMAL, automatic path (every
            // Dialog_CEReloadedTabs/Dialog_ChangeAmmo close), so it's one of the ones that actually needs to
            // catch "I unchecked 2 of this pawn's 3 ammo types and nobody else used them" - the manual Bill
            // Overview tab button alone never saw that case. Shared with every other caller via
            // AttachObsoleteCleanup - see its own comment for why this isn't hand-rolled here anymore.
            AttachObsoleteCleanup(req);

            // A diagnostic log sat here until 2026-08-15. Removed because the case was reproduced and the
            // window did open. One number from it is still worth recording: `produciblePackItems` was 0 while
            // the RAW shortfall was 1 - the pack row (Glitterworld medicine) appeared in the window via another
            // path. The gate below would not have opened it on its own. Not a visible bug, but the reason this
            // gate nearly stayed shut on pack-only changes.
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

        // The half of CannotFight that is a FACT about the pawn - without the mark the player set themselves.
        // Needed since that mark no longer just means "leave this pawn alone" but "take its loadout away":
        // a decision is revocable and may therefore be toggled in the row icon, a pacifist or a baby may not.
        // "CannotFight(p) && !IsExcludedFromArming(p)" is NOT a substitute - for a pacifist who is also
        // excluded it would wrongly yield false.
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
        // consulted again until "Use recommended setup" resets the flag.
        public static float DynamicMechDefault(CERUiState ui)
        {
            CheckMechanoidEncounter(ui);
            if (ui.hasFoughtMechanoids) return 0.62f;
            float days = GenDate.DaysPassed;
            return Mathf.Lerp(0.5f, 0.55f, Mathf.Clamp01(days / 60f));
        }

        // Dynamic Pool anchor bonus (Generate-time "available <-> researched" slider): rewards a resource-flush
        // colony with a wider default net, using the same MaterialAffordability signal as Craft/Budget. Bounded so
        // it never runs away without the player's own input.
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
            // Shrink the font first, truncate only after that. Previously the "Loadouts" header showed as
            // "Loadou" - no ellipsis, so it didn't read as a shortened word but as a different word.
            // RimWorld's Widgets.Label bluntly clips text that is too long for a narrow rect. For column widths
            // that is especially bad: the header is the only place that says what the column means, and its
            // length differs in EVERY language - making a column wide enough for English says nothing about
            // German or Russian.
            // Hence here rather than per column: the same tiering BarLabel already uses - if it fits in Small,
            // nothing changes; otherwise Tiny; and only if that isn't enough either, truncate visibly with an
            // ellipsis.
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
        // Background fill behind a group of related controls (a rectangular light-grey background per group).
        // Deliberately an ALPHA fill rather than a fixed grey: it sits sometimes on the map, sometimes on a menu
        // section, sometimes on a highlighted row - an opaque grey would look too light or too dark depending on
        // what is underneath.
        public static readonly Color ColGroupBg = new Color(1f, 1f, 1f, 0.07f);
        // Color of the pin icons, consolidated: previously the same RGB triple sat in five places across three
        // files - and exactly that kind of scatter is where one of them drifts later.
        // WHITE: the pins used to be tinted yellow even though the PNG is white; the tint was removed so the pin
        // looks as drawn. The constant stays so all six spots (both pin rows of the Pack wizard, the row marker,
        // the header toggle and the arrow in the Assign window) keep ONE source - anyone who wants to tint the
        // pin again does it here and nowhere else.
        public static readonly Color ColPin = Color.white;

        // Arrow of the pinned row: white; the unpinned one is dimmed. This only WORKS since the arrow comes from
        // our own white PNG (TexArrowRight, see there) - with RimWorld's grey sprite, "full white" was
        // unreachable with any color.
        // Short history so this isn't attempted a third time: this was once (2,2,2,1) on the assumption that a
        // factor above 1 brightens in the shader. Measuring in the running game disproved that - Unity clamps
        // the color value and the arrow stayed at 144. A color can only darken.
        public static readonly Color ColPinArrow = Color.white;

        // One shared, prominent "!" badge (filled circle + dark outline + bold glyph) for every "this is short/
        // blocked, look at the tooltip" spot in the mod (2026-08-02: the plain small text "!"
        // several spots drew on their own - DrawIngredientCell's shortage mark, DrawSupplyReqGroup's impossible
        // dot - was too thin and small to notice). Bigger (18px default, was 12-14px) and bold Small
        // font instead of Tiny, so it actually reads as a warning at a glance instead of disappearing against
        // the dark UI background. r's own size decides the badge size - callers pick the Rect, this only draws.
        // State icon of a row: clickable, but WITHOUT a color change on hover (a red cross used to turn dark red
        // on hover).
        //
        // Why it happens at all, per the decompiled source: Widgets.ButtonImage(rect, tex, baseColor) internally
        // passes GenUI.MouseoverColor on as an ABSOLUTE color (0.3/0.7/0.9, a light blue) and discards baseColor.
        // GUI.color multiplies with the texture colors - a red cross times blue gives exactly the dark red that
        // appeared on hover. For these icons the color CARRIES the meaning (red = will be deleted, yellow =
        // pinned); it must not vanish precisely when the pointer is on it - right before the click.
        //
        // Second half of the same bug: the overload WITHOUT a color sets baseColor to white and thereby
        // overwrites a previously set GUI.color. The Equipment Pack rows did exactly that - the color was set
        // but never arrived, which is why they showed a WHITE trash can where the Weapon Loadouts rows show a
        // red one. Going through this helper lets the color through again.
        //
        // The click itself is unchanged (ButtonInvisible underneath); this is only about drawing.
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
            // The exclamation mark is drawn from an image, not text. As a text glyph it sat sometimes centered,
            // sometimes not, sometimes thick, sometimes thin. That was real and not an SVG-vs-PNG issue: the
            // glyph went through Text.CurFontStyle, so its look depended on three things that shift from
            // outside - the active font size, the font style's padding/contentOffset (a visible fraction of the
            // width on a ~12px square) and the UI scale, which turned the 0.6px faux-bold offset into a whole
            // pixel or into nothing. Each of three earlier fixes (measured placement, one font size smaller,
            // proportional downward offset) tweaked exactly these symptoms without touching the cause.
            // The computed (geometry) version that followed was not good enough either, for two reasons. First,
            // it didn't look like an exclamation mark: on a 12px square it came out as a 4px bar and a 2px dot,
            // so both strokes were nearly the same length and the mark was hard to recognize. Second, it was
            // still inconsistent, for a reason that had been overlooked: the bar's WIDTH came from box.width,
            // its HEIGHT from box.height. In a non-square rect the same mark got different proportions - and
            // call sites do pass such rects.
            // A PNG has neither problem: one drawing, defined in one place, fitted with its aspect ratio kept.
            DrawExclamationGlyph(b, Color.black);
        }

        // The exclamation mark as geometry - bar plus dot, both derived from the given box. Shared by the
        // warning badge and the forbidden marker on the availability bars, because the mark appears in many
        // places: both previously drew a text "!" and therefore looked different in different places, although
        // both mean the same thing.
        // Deterministic, because nothing here goes through Text.CurFontStyle anymore: no font size, no padding,
        // no contentOffset, no faux-bold offset that is a whole pixel at some UI scales and none at others.
        // Mathf.Round keeps the edges on whole pixels so the stroke doesn't come out half transparent depending
        // on position.
        // The drawing lives in Textures/UI/Icon_Warning.png - white on transparent so the caller can tint it.
        // Loaded once and kept; if it fails (file missing, path renamed), the fallback below keeps drawing the
        // computed version so no spot is left empty.
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
                // Fitted, not stretched: THIS is the part that actually makes it uniform - call sites pass
                // differently shaped rects, and a stretched exclamation mark would look different in a wide
                // box than in a tall one.
                Widgets.DrawTextureFitted(box, tex, 1f);
                GUI.color = prev;
                return;
            }

            float barW = Mathf.Max(1f, Mathf.Round(box.width * 0.16f));
            float gapY = Mathf.Max(1f, Mathf.Round(box.height * 0.10f));
            float dotH = barW;
            // Total height (bar + gap + dot) centered in the box so the visible INK is centered - not the line
            // box as in the text version, which always sat too high for exactly that reason and had to be
            // corrected twice with an offset.
            float totalH = box.height * 0.62f;
            float barH = Mathf.Max(1f, totalH - gapY - dotH);
            float x = Mathf.Round(box.center.x - barW / 2f);
            float top = Mathf.Round(box.center.y - totalH / 2f);
            Color pc = GUI.color;
            GUI.color = Color.white;   // DrawBoxSolid multiplies by GUI.color - otherwise the caller's tint applies too
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

        // A radio flow on the left, a checkbox on the right - ONE computation for both filter rows of the bills
        // overview. Previously the same formula sat at both call sites and let the flow run up to within 8
        // points of the checkbox. Two problems came from that:
        //   - In German the last radio ("Req. ammo") ran into "Hide duplicate workbenches" with no gap at all -
        //     no letter was missing, but it read as one word. On the weapons tab it didn't show because
        //     "Req. weapons" is shorter: same row, two letters difference, and it tips over.
        //   - In a narrow window both overlapped CLEARLY. The Tiny step was the last safeguard, with nothing
        //     below it.
        // Hence three steps instead of one threshold: normal font, Tiny, and if even that isn't enough the
        // checkbox drops to its OWN line instead of overlapping the last radio. The minimum gap is a named
        // value rather than 8 - a gap you can actually see is the whole purpose of the computation.
        public struct RadioRowFit
        {
            public GameFont font;      // draw both flow and checkbox in THIS font - both, otherwise one miscalculates
            public Rect checkboxRect;
            public float extraHeight;  // > 0: the checkbox sits on its own line, the row needs more height
        }

        // Width of a radio flow in the CURRENTLY set font - the same formula Radio() advances by when drawing
        // (26 button + text + 22 gap). Changing it here means changing it there too.
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
                // Left-aligned under the flow, not at the right edge: on its own line there is no neighbor to
                // align with, and on the left it sits where the eye already is.
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
        // Is it worth building THIS tooltip text at all?
        //
        // Decompiled from RimWorld's own TooltipHandler, and that is the whole point: TipRegion discards EVERY
        // tip that isn't under the mouse in a Repaint frame - the function's first two lines are exactly that
        // check. But the caller builds the text BEFORE that, since it is an ordinary argument. Result: a draw
        // loop registers dozens of tooltips per frame, at most one of which is ever shown, and builds the full
        // string for all the others - not a trifle here, but colony-wide stock breakdowns per item per cell.
        // Vanilla does the same: TipRegionByKey checks Mouse.IsOver BEFORE calling .Translate().
        //
        // Accepted side effect: DebugViewSettings.drawTooltipEdges no longer draws frames for guarded tooltips.
        // That is a developer overlay nobody uses with this mod.
        //
        // The two counters answer "how much was it": skipped == what was previously built EVERY frame. They
        // cost nothing while the dev log is off.
        public static int tipBuilt, tipSkipped;
        // Second purpose: WHILE A FLOAT MENU IS OPEN, NO TOOLTIP IS WANTED. Seen on the readiness indicator
        // (click -> the menu opens under the pointer, and the light's tooltip stays and is drawn ON TOP of it -
        // exactly the rows that are meant to be clicked become unreadable). RimWorld draws tooltips after all
        // windows, so a draw-order trick doesn't help; the tooltip simply must not be registered.
        // Done here rather than only at that one spot because the same pattern exists throughout the mod: a
        // button with a tooltip that opens a FloatMenu under the pointer on click - the weapon cell in the
        // Assign tab is the next one (its tooltip lists the whole loadout content and is correspondingly big).
        // This function is the common gate the mod's expensive tooltips already go through.
        // The click itself is untouched - it never goes through TipWanted.
        public static bool TipWanted(Rect r)
        {
            bool want = Event.current != null && Event.current.type == EventType.Repaint && Mouse.IsOver(r)
                && Find.WindowStack?.WindowOfType<FloatMenu>() == null;
            if (Prefs.DevMode) { if (want) tipBuilt++; else tipSkipped++; }
            return want;
        }

        // The weapon icon that stands for "Any ammo for <weapon>" - WITH the small ammo mark in the top-right
        // corner, as on the weapon effect icons. Without it the row shows the same picture twice: once the
        // weapon as a weapon, once the weapon as a stand-in for its ammo - and only the text tells them apart.
        // Exactly the same 9px mark in the same corner that DrawAmmoEffectIcons already puts on every effect
        // icon; slightly larger here because the base icon is 20-22px instead of 16px.
        // ONE function for all call sites so the mark doesn't sit in one place and go missing in the next -
        // which is exactly how the gap arose in the first place.
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
            // The diagnostic logging that was here is gone: Player.log showed no hits for the measurement line,
            // so the cell does not wrap. Keeping it was also more expensive than it looked - a Text.CalcHeight
            // PLUS a Text.CalcSize per label per frame whenever dev mode is on.
            bool prevWrap = Text.WordWrap;
            Text.WordWrap = false;
            // Keep the parenthesized suffix: in the Supply window this cell carries ammo names, and there the
            // parentheses are exactly what distinguishes the rows (".30-06 Springfield (AP)" vs "(FMJ)" vs
            // "(Sabot)"). The same issue first showed up in the bills overview - see TruncateKeepingSuffix.
            Widgets.Label(r, TruncateKeepingSuffix(text, r.width));
            Text.WordWrap = prevWrap;
        }

        // "There is more below" - the hint at the bottom edge of a list that is longer than its window. Without
        // it a list is easily misread: the role preview shows three of five rows, RimWorld's scrollbar is a thin
        // grey line, and the totals sit directly under the list - that reads as a complete list, not an
        // excerpt. Same trap in the Supply window ("Obsolete bills", 3 of 7 visible, total cost right below).
        //
        // Call AFTER EndScrollView, with the same two rects and the same scroll position. Draws only while
        // something really lies below the edge, and disappears by itself once the bottom is reached - a hint
        // that is always there stops being read.
        // rowH > 0 turns it into a row count ("4 more"), otherwise it stays a plain "more below".
        // Indented 18px on the right: exactly the width of RimWorld's own scrollbar so the bubble doesn't cover
        // it - derived, not guessed.
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

        // RimWorld's learning helper draws at the top right, but on WindowLayer.Super - i.e. ABOVE every dialog -
        // and handles its clicks first. A window whose right edge reaches under this 200-point strip loses EVERY
        // button there: for us first the close X (Widgets.CloseButtonFor sits at width-22), then the help
        // button, on very narrow screens also the Arm-my-Colony button. Together with absorbInputAroundWindow
        // this means the window can only be closed with Escape, and without knowing that you are stuck.
        // Measured at UI widths 1440 and 1366; the math behind it holds for EVERY one of our windows, not just
        // the main one: collision when UI width < window width + 416.
        // So for the main window (1100) below 1516, for the Supply window (1192) below 1608.
        // Numbers from LearningReadout.LearningReadoutOnGUI: Rect(screenWidth - 8 - 200, 8, 200, ...).
        private const float LearningReadoutWidth = 200f;
        private const float LearningReadoutMargin = 8f;

        // float.MaxValue means "the learning helper isn't shown" - then every window stays where it was.
        public static float LearningReadoutLeftEdge()
        {
            // The same four conditions as LearningReadoutOnGUI. All defensive, because these windows can be
            // opened in edge cases (main menu, loading screen) without a game state.
            if (TutorSystem.TutorialMode || !TutorSystem.AdaptiveTrainingEnabled) return float.MaxValue;
            LearningReadout readout = Find.Tutor?.learningReadout;
            if (readout == null) return float.MaxValue;
            PlaySettings play = Find.PlaySettings;
            if ((play == null || !play.showLearningHelper) && readout.ActiveConceptsCount == 0) return float.MaxValue;
            return UI.screenWidth - LearningReadoutMargin - LearningReadoutWidth;
        }

        // Pushes a window left out of the strip - and ONLY that. It never gets narrower.
        //
        // An earlier version capped the width as a second step when pushing wasn't enough. That was wrong, for
        // this reason: the Supply window's rows draw at FIXED offsets from the left edge and end at 1124; below
        // a window width of 1176 the workbench picker, jump and info icons slide out of the window, and there is
        // no horizontal scrolling - they are then not clipped but UNREACHABLE. By the numbers, the capping would
        // have broken a case that worked before, in a band of roughly 1216 to 1392 screen width.
        //
        // So the honest trade-off: a window whose X lies under the learning helper can still be closed with
        // Escape. A window whose controls have fallen off its own edge has no way out at all. If it doesn't fit
        // beside the strip, it stays covered - there is simply no room for both, and no computation changes
        // that.
        // Pushing alone covers the observed cases: main window (1100 wide) at 1366 screen width -> x = 58
        // instead of 133, right edge exactly on the strip's edge; at 1440 -> x = 132. Both fully clear.
        // Without a collision the function falls through immediately - on wide screens nothing changes.
        public static Rect KeepClearOfLearningReadout(Rect r)
        {
            float limit = LearningReadoutLeftEdge();
            if (r.xMax <= limit) return r;
            float x = Mathf.Max(LearningReadoutMargin, Mathf.Min(r.x, limit - r.width));
            if (Mathf.Approximately(x, r.x)) return r;
            return new Rect(x, r.y, r.width, r.height).Rounded();
        }

        // ====== The role toggle, ONE control for both windows ======
        // It has three values (RoleAssignMode), and three values don't fit on a checkbox.
        // A partial check would be the obvious solution and the worse one: a half-filled box needs a legend,
        // and then it is no longer a shortcut. Hence a button that shows the value in plain text, with a float
        // menu - the same shape as every other labeled picker button in the mod.
        // Here and not in one of the windows because both the Assign tab AND the assignment preview draw it -
        // two copies would be exactly the kind of duplication that previously led to a mislabel (the same
        // toggle was named differently on two screens).
        public static string RoleModeLabel(RoleAssignMode m)
        {
            switch (m)
            {
                case RoleAssignMode.RolesOnly: return "CER_LoadoutUtility_RoleModeRolesOnly".Translate().ToString();
                case RoleAssignMode.NoRoles: return "CER_LoadoutUtility_RoleModeNoRoles".Translate().ToString();
                default: return "CER_LoadoutUtility_RoleModeMixed".Translate().ToString();
            }
        }

        // true if the value changed - the caller must then trigger a recompute.
        // How wide must the button be for its label to fit in the NORMAL font? (The three labels were drawn
        // in the normal font size and the button between them noticeably smaller.)
        //
        // The cause wasn't the button but its fixed width of 170: ButtonTextFit falls back to Tiny as soon as
        // the text doesn't fit - correct as a last resort against clipping (which is why it is there, see
        // below), but in a row with three normally set labels the one small one stands out immediately.
        // Instead of turning 170 into a bigger number that would be too small again for the next longer
        // translation: measure. The last resort stays for when the caller really has no room.
        public static float RoleModeControlWidth(RoleAssignMode current)
        {
            GameFont prev = Text.Font;
            Text.Font = GameFont.Small;
            float w = Text.CalcSize("CER_LoadoutUtility_RoleModeButtonFmt".Translate(RoleModeLabel(current)).ToString()).x;
            Text.Font = prev;
            return w + 16f;   // the same padding ButtonTextFit uses (12), plus a little margin
        }

        public static void RoleModeControl(Rect r, RoleAssignMode current, Action<RoleAssignMode> onPicked)
        {
            // ButtonTextFit, not Widgets.ButtonText: the box has a fixed width and the German label for
            // "Assign: roles and templates" doesn't fit - the button showed it clipped on both sides. One place
            // is enough because the Assign tab AND the assignment preview draw the same button here (see the
            // comment above).
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
            bool clicked = ButtonTextFit(r, label);   // ModeToggleW is fixed, the text is translated
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
        // same visual language everywhere. Click flips `detailed` in place.
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
        // Build the label once per colonist instead of once per pass - see Dialog_AssignLoadouts.GetRowLabels.
        // prebuiltLabel wins when set.
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

        // Our own arrow instead of RimWorld's TexUI.ArrowTexRight. Reason, measured in the running game:
        // RimWorld's sprite is itself a grey of 144, and GUI.color can only make an image darker - Unity clamps
        // the color value at 1, a factor above that demonstrably does nothing (measured: pinned 144, unpinned
        // 95 = 0.6 alpha over a background of 21 - the math works out exactly). "Full white vs. dimmed" was
        // therefore never reachable with the foreign sprite, whatever color was set. With a white PNG it is an
        // alpha value, and that works.
        // Falls back to the RimWorld arrow if the file is missing - then it looks as before instead of throwing.
        public static readonly Texture2D TexArrowRight =
            ContentFinder<Texture2D>.Get("UI/Icon_ArrowRight", false) ?? TexUI.ArrowTexRight;

        // "This row has been edited" (third state of existing pack/loadout rows).
        // OWN PNG: instead of a pencil a different, odd symbol appeared - the assumption that vanilla's
        // "UI/Buttons/Rename" IS a pencil was simply wrong; it is a rename glyph and reads as noise here.
        // DELIBERATELY A PLACEHOLDER (dummy PNG, a proper pencil icon can replace it): single-color white and
        // 64px, just like the category icons below - to swap it, replacing Textures/UI/Icon_EditPencil.png is
        // enough, nothing here needs to change.
        // The two fallbacks stay (reportFailure:false) so a missing PNG produces no red log error and the cell
        // is never empty.
        public static readonly Texture2D TexEditPencil =
            ContentFinder<Texture2D>.Get("UI/Icon_EditPencil", false)
            ?? ContentFinder<Texture2D>.Get("UI/Buttons/Rename", false) ?? TexPin;

        // Category icons for the four cells of the card view (Ammo / Pack / Sidearm / Other).
        // In the compact view the column header says which category it is - the card view has no header, so
        // the four cells used to sit side by side without any label.
        // DELIBERATELY PLACEHOLDERS (can be replaced by nicer ones): single-color white so RimWorld's GUI.color
        // can tint them like any other UI icon - a colored base image could no longer be controlled there.
        // 64px because the cells scale them down to ~16px.
        // To swap them, replacing the PNGs in Textures/UI is enough; nothing here needs to change.
        public static readonly Texture2D TexCatAmmo = ContentFinder<Texture2D>.Get("UI/Icon_CatAmmo", false);
        public static readonly Texture2D TexCatPack = ContentFinder<Texture2D>.Get("UI/Icon_CatPack", false);
        public static readonly Texture2D TexCatSidearm = ContentFinder<Texture2D>.Get("UI/Icon_CatSidearm", false);
        public static readonly Texture2D TexCatOther = ContentFinder<Texture2D>.Get("UI/Icon_CatOther", false);

        // Draws the category icon at the left of the cell and returns how much width it took.
        // If the PNG is missing (ContentFinder with false), nothing is drawn AND nothing reserved - the row
        // then looks as before instead of showing an empty gap.
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
        // STATUS: this component currently has NO callers, and that is intentional, not a leftover.
        // The decision was "one icon per row, and it shows the STATE" - both preview rows (Weapon Loadouts,
        // Equipment Packs) previously drew the checkbox AND this status glyph next to it. The second mark was
        // dropped there; the only information it carried exclusively ("will be deleted") now sits on the main
        // icon itself (trash can instead of an empty box, see Dialog_SquadSetup's stTex).
        // Do NOT delete as "unused": this is the agreed status language including colors and order,
        // centralized precisely so a third screen doesn't invent its own again. Anyone building a new preview
        // row starts here - and anyone considering bringing back the second icon reads the decision above first.
        public enum GenRowStatus { New, Update, Kept, Skipped, Delete }

        // `canDelete`: some screens treat unchecking an EXISTING row as "delete it" (Weapon Loadouts, by
        // design); others are purely additive and unchecking just means "leave it alone, don't touch it" with
        // no destructive consequence (Equipment Packs, which has no per-row delete concept at all) - the caller
        // must say which, this function never assumes.
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

        // The small carrier/template indicator (diamond or dot + number, names on hover) - extracted from
        // Dialog_SupplyLoadouts.DrawIndicatorCell (Supply Loadouts already had it, no need to reinvent it) so
        // the Packs tab uses THE SAME indicator with the same hover keys. White, not ColGood: the mark is a
        // neutral counter, not a good/bad status.
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

        // Icon + centered label inside an already-drawn button. The label is fitted as in ButtonTextFit -
        // the same overflow handling, just with 36px less room because of the icon.
        public static void ButtonIconLabel(Rect r, Texture2D icon, string label)
        {
            GUI.DrawTexture(new Rect(r.x + 8f, r.y + (r.height - 22f) / 2f, 22f, 22f), icon);
            Text.Anchor = TextAnchor.MiddleCenter;
            GameFont prevFont = Text.Font;
            bool prevWrap = Text.WordWrap;
            Text.WordWrap = false;
            if (Text.Font != GameFont.Tiny) Text.Font = GameFont.Small;   // see ButtonTextFit
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

        /// <summary>Widgets.ButtonText whose label is FITTED into the button: Small first, Tiny if that doesn't
        /// fit, and only then visibly shortened (ellipsis) - never ButtonText's blunt clipping on both sides
        /// (MiddleCenter lets overflow run symmetrically out of the rect, cutting off the start and end of the
        /// label) and never the squeezed two-line case. WordWrap is off while drawing so a text that barely
        /// doesn't fit doesn't wrap after all (the same CurFontStyle trap as in LabelCell). For every button
        /// with a translated label and a fixed width.</summary>
        // Font tier for a ROW of equal-ranked buttons: if ONE label doesn't fit its cell in Small, the whole
        // row drops to Tiny - otherwise a small button sits next to big ones and the row reads as broken
        // rather than translated. The caller sets Text.Font for the row's ButtonTextFit calls and restores it
        // afterwards; ButtonTextFit still measures by itself and only shortens what doesn't fit even in Tiny.
        // The same idea as FitRadioRow, for buttons instead of radios.
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

        // One bottom bar, ONE font tier: in the "3. Equip colonists" tab one button was noticeably smaller
        // than its two neighbors - exactly the "small button next to big ones" case ButtonRowFont already rules
        // out for the header bar. BottomActionButton measures per button, so it knows nothing about neighbors
        // drawn after it. Hence the caller registers the row up front - width and label are enough, the rects
        // only come into being when drawing.
        // `icon: true` for buttons with an icon (ButtonIconLabel), which lose 36 instead of 12 points of margin.
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

        // Width of a bottom-bar button, ALWAYS measured in Small. In German the bills overview's bottom bar
        // showed the two LEFT buttons large and the two right ones small - one row, two font sizes. There were
        // two causes, and this is the quieter one: the width was measured with whatever font happened to be
        // set. Measured in Tiny and drawn in Small, the button is too narrow - and ButtonTextFit then steps it
        // down even though nothing next to it was stepped down.
        // The louder cause is documented at BottomBarFont: the left buttons weren't part of the row computation
        // at all. A bottom bar therefore registers ALL its buttons, left and right.
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

        // A checkbox whose label actually fits: in German the "use any 'X' generic types" label wrapped onto
        // TWO lines in the 220-point field and got clipped at the top AND bottom - the row height only fits one.
        // Same order as ButtonTextFit, with one extra step first: widen the field as far as maxWidth allows
        // (there is usually room next to a checkbox, it rarely sits at the edge), then Tiny, and only then
        // visibly shorten. WordWrap stays off meanwhile so no wrap can happen.
        // Returns the rect used - the caller attaches its explanation text to it.
        public static Rect CheckboxLabeledFit(Rect r, string label, ref bool checkOn, float maxWidth)
        {
            GameFont prevFont = Text.Font;
            bool prevWrap = Text.WordWrap;
            Text.WordWrap = false;
            if (Text.Font != GameFont.Tiny) Text.Font = GameFont.Small;
            const float boxAndGap = 34f;   // Widgets.CheckboxLabeled: checkbox (24) on the right + gap before it
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

        // A numeric field that is a REAL input field only when someone can use it (measured: the item count
        // field + X took 24 ms/s at 2600 calls/s, i.e. 9 us per field). Widgets.TextFieldNumeric parses and
        // formats on every pass, including the eight fields nobody is typing in. If the mouse isn't over the
        // row and the field doesn't have keyboard focus, only its text is drawn in field style -
        // pixel-identical, without input logic.
        // `hot` is the row (not just the field) so the real field already exists before the click lands.
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
            // A button never inherits a font LARGER than Small: 'Fetch gear now' and 'Arm my Colony' in the
            // Loadout Planner header suddenly showed in Tiny while the Assign tab's did not. Both fit easily in
            // Small; Tiny only came because this measured with whatever font was set - and that was Medium,
            // left over from a previous draw. Medium is never a button font. Tiny is kept when a caller set it
            // on purpose for a whole row (ButtonRowFont).
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

        // "Fetch gear now" - left of Arm my Colony, in RimWorld's Assign tab AND in our window's header, so
        // both places show the same three buttons. Mechanics in FetchGearNow.
        public static void DrawFetchGearButton(Rect rect)
        {
            if (ButtonTextFit(rect, "CER_LoadoutUtility_FetchGearButtonLabel".Translate().ToString()))
            {
                int started = FetchGearNow.Trigger(out int withLoadout, out int skipped, out int blockedFull);
                // Full colonists are included in the message: CE simply fetches nothing at full bulk/weight,
                // and without this number it read like "all done".
                // Singular for exactly ONE: otherwise the message used the plural twice for the number 1.
                // RimWorld's Translate knows no plural rules; languages solve this with a separate key each,
                // so the choice belongs here.
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
            // A preview instead of explanation text alone: who would abort what and fetch what - see
            // FetchGearNow.PreviewTooltip.
            // FIXED ID, otherwise the box flickers (the tooltip kept flickering on and off).
            // RimWorld identifies a tooltip by its TipSignal's uniqueId - and a plain string derives that from
            // the TEXT. This text names each colonist's current activity, so it changes every second: each time
            // it counted as a NEW tooltip, the old one closed and the new one opened. With a fixed id ONE box
            // stays open and just rewrites itself. The readiness indicator next to it has always done it so.
            if (TipWanted(rect)) TooltipHandler.TipRegion(rect, new TipSignal(() => FetchGearNow.PreviewTooltip(), 0x0FE7C4));
        }

        // The consolidated readiness indicator - shared by Dialog_
        // CEReloadedTabs' header and the shortcut this mod adds to RimWorld's native Assign (policy table)
        // tab, so "is my squad ready" and its one-click fixes are reachable without opening the full tab
        // window. Unlike the vanilla Alert_CER_Readiness (informational, standard click-to-select-pawns),
        // this IS the actionable one-click fix: clicking it with any production-related issue present opens
        // Dialog_SupplyLoadouts directly, pre-filled for exactly the affected colonists. Draws right-aligned
        // within `available`. getLoadouts/getAssign are lazy - only ever invoked inside a fix's own click
        // closure, never during the per-frame draw, so a caller with no persistent dialog instance around
        // (the native-tab shortcut) can safely pass a factory that builds one on first actual use instead of
        // eagerly constructing (and re-scanning the map for) one every frame just to draw a status dot.
        // Number of same-kind colonist entries at which the click menu shows ONE grouped entry instead.
        private const int ReadinessGroupFrom = 5;

        private static void OpenBillOverview()
        {
            Dialog_CEReloadedTabs tabs = Find.WindowStack.WindowOfType<Dialog_CEReloadedTabs>();
            bool open = tabs != null;
            if (tabs == null) tabs = new Dialog_CEReloadedTabs();
            tabs.JumpToAmmoBills();
            if (!open) Find.WindowStack.Add(tabs);
        }

        // Supply window for exactly these colonists; if there is nothing to craft there, the first one's gear instead.
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
            // Which of them have a 'weapon without ammo slot' - for the three-way split below.
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
            // AssignedNotCarried lands in THIS bucket, not with the production items: there is nothing to
            // craft, the weapon is right there. The colonist just hasn't fetched it - the same kind of problem
            // as overloaded or blocked, and the same click path (gear tab).
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

            // Smaller rather than two lines: in German the "9 need attention" label wrapped onto two lines and was
            // clipped in the 26-point-high field. Try Small first, otherwise Tiny - the same tiering as for buttons
            // (ButtonTextFit).
            GameFont prevReadFont = Text.Font;
            bool prevReadWrap = Text.WordWrap;
            Text.WordWrap = false;
            Text.Font = GameFont.Small;
            if (Text.CalcSize(label).x + 32f > available.width) Text.Font = GameFont.Tiny;
            float w = Mathf.Min(available.width, Text.CalcSize(label).x + 32f);
            Rect r = new Rect(available.xMax - w, available.y, w, available.height);

            // Clickable even when "all green" if there is better ammo to fetch: otherwise the hint would be
            // unreachable exactly when nothing else is pending - which is the normal case after finishing a
            // research project. The LABEL and the dot color deliberately stay untouched: an opportunity is not a
            // problem and doesn't tint the indicator.
            bool anyAction = total > 0 || TemplatesWithBetterAmmo().Count > 0;
            if (anyAction && Mouse.IsOver(r)) Widgets.DrawHighlight(r);
            GUI.color = dotColor;
            GUI.DrawTexture(new Rect(r.x + 7f, r.y + r.height / 2f - 4f, 8f, 8f), BaseContent.WhiteTex);
            GUI.color = Color.white;
            Text.Anchor = TextAnchor.MiddleLeft;
            // Last safeguard: in German the label was once cut off mid-word with NO ellipsis.
            // If even Tiny doesn't fit the field the caller provides, shorten visibly instead of clipping
            // bluntly - the same promise ButtonTextFit makes for buttons.
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
                        // Straight into the assignment preview: the former Generate intermediate step is gone -
                        // the solver picks weapons directly and templates are created invisibly on apply.
                        // keepAssigned stays on (default) so this one-click path never discards an existing,
                        // hand-maintained assignment - the same protective intent as the former
                        // presetLockExisting.
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
                    // There used to be an exception here for WeaponUnavailable colonists ("trustedGap"): the
                    // alert computed per pawn in turn, the Supply window colony-wide, and where the two disagreed
                    // the alert won - the window opened anyway. That led to "Production gaps (2) - open Supply"
                    // opening a window that said "Everything needed is already in stock or in production".
                    // The exception is gone because the contradiction is gone: both sides now read from
                    // Dialog_AssignLoadouts.WeaponShortfallsByPawn. If nothing needs producing here, this group
                    // really lacks nothing - then the gear route is the right one, not a window with nothing to
                    // offer.
                    // THREE kinds instead of one. The AmmoNotProduced alert fires at threshold 0 as soon as NO
                    // bill runs for an ammo type - even with full stock; the window only shows real shortfalls.
                    // With four affected colonists, three were "no bill, stock suffices" and one a real
                    // shortfall - yet all four were labeled "production gap". Now split per colonist, asking the
                    // same question as the window:
                    //   the Supply window would have a row for this colonist -> Production gaps (items) - open Supply
                    //   weapon without ammo slot in the loadout             -> ammo window
                    //   otherwise (no bill, stock suffices for now)         -> Bill Overview
                    List<Pawn> gapPawns = new List<Pawn>(), noBillPawns = new List<Pawn>(), noAmmoSlotPawns = new List<Pawn>();
                    foreach (Pawn p in prodDistinct)
                    {
                        if (ReadinessGapRowsFor(p) > 0) gapPawns.Add(p);
                        else if (noAmmoSlotC.Contains(p)) noAmmoSlotPawns.Add(p);
                        else noBillPawns.Add(p);
                    }
                    Dialog_SupplyLoadouts.Request req = gapPawns.Count > 0 ? Dialog_AssignLoadouts.BuildSupplyRequestForPawns(gapPawns) : null;
                    Dialog_SupplyLoadouts dlg = req != null ? new Dialog_SupplyLoadouts(req) : null;
                    // `dlg != null` is load-bearing, not decoration: BuildSupplyRequestForPawns returns null when
                    // nothing is missing, and acts[0] runs WITHOUT a menu when it is the only entry - without the
                    // check that was a one-click crash on the always-visible readiness indicator.
                    // AnythingToProduce instead of AnythingToSupply (second occurrence of the same pattern): this
                    // path comes from a WARNING about a SHORTAGE. A window showing only the cleanup list "nobody
                    // needs these bills anymore" doesn't answer that question - it changes it.
                    // The number on the button is the number of ITEMS the window then shows - not of colonists.
                    // The colonists are listed by name in the tooltip next to it.
                    if (dlg != null && dlg.AnythingToProduce)
                        acts.Add(("CER_LoadoutUtility_ReadinessProdGapsOption".Translate(dlg.ProduceRowCount.ToString()).ToString(), () => Find.WindowStack.Add(dlg)));
                    else
                        foreach (Pawn p in gapPawns)
                        {
                            Pawn local = p;
                            // "In storage, not picked up" - the fetch command for exactly this colonist instead
                            // of their gear dialog (which only offered "Drop", and that never helped here).
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
                    // SPLIT BY CAUSE. Previously EVERY case ended in the same answer: build the supply window
                    // for these pawns and offer it if it contained anything at all. That "anything" could be a
                    // suggestion to cancel explosive bills - while the ammo that triggered the warning in the
                    // first place was not in it. The window did not answer the question, it changed it.
                    // The cause was the gate: AnythingToSupply is also true when ONLY the "Obsolete bills"
                    // section is filled.
                    // See CombatReadiness.LowAmmoState for the three causes and why they need different
                    // answers.
                    // Pending carries its classification WITH it: the loop below used to work out the reason
                    // again on its own, and that was exactly the second calculation. See there.
                    var pending = new List<(Pawn pawn, CombatReadiness.LowAmmoInfo info)>();
                    var planTooSmall = new List<Pawn>();
                    var notProduced = new List<Pawn>();
                    // "It is missing, and I cannot even offer it here" - see below where this list is filled.
                    // Deliberately NOT `pending` any more: that would have said "supplies are on the way"
                    // when nothing is on the way.
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

                    // (C) Genuinely missing: supply window, but NARROWED TO THIS AMMO. The demand calculation
                    // still comes from BuildSupplyRequestForPawns (the same one used everywhere, so no second
                    // truth appears here); only its ammo part is shown - no weapons, no packs and above all no
                    // obsolete-bill cleanup list, which caused the whole misfire. If nothing is left after
                    // that, nothing is offered.
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
                            // Pass this through here too, even though this narrowed call contains no thrown-
                            // explosive row today: the four other places that assemble such a request all do
                            // it now. One that doesn't is exactly the gap through which the same bug comes
                            // back on the next refactor.
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
                            // No producible entry came out of it (not researched, no recipe, nothing left after
                            // narrowing) - so all that remains is the gear. Previously these colonists landed in
                            // `pending` and were told "supplies are on the way", even though LowAmmoState had
                            // just determined the opposite for them: nothing reachable, no bill. The row showed
                            // up with a contradicting tooltip right next to it.
                            // "Wait" was never the more honest answer here, just the wrong one.
                            noneOffered.AddRange(notProduced);
                        }
                    }

                    // (B) The loadout itself asks for too little - no stock and no bill in the world clears
                    // this warning, only a larger amount in the loadout. So go straight to where that is set.
                    // Role holders get the same path: Dialog_ChangeAmmo shows the role ammo for them and says
                    // that it comes from the role.
                    if (planTooSmall.Count > 0)
                    {
                        List<Pawn> local = planTooSmall;
                        acts.Add(("CER_LoadoutUtility_ReadinessLowAmmoPlanOption".Translate(local.Count.ToString()).ToString(),
                            () => Find.WindowStack.Add(new Dialog_ChangeAmmo(local))));
                    }

                    // (A) On the way: it is in storage or being produced. The old behavior, now only for the
                    // case it was meant for - jump to the workbench if something is really running, otherwise
                    // show the gear.
                    // THE SECOND CALCULATION THAT USED TO BE HERE.
                    //
                    // LowAmmoSuffix - the tooltip text of THIS SAME widget - had been switched to LowAmmoState
                    // for exactly this reason. This loop two screens above was left behind and kept working
                    // out the reason on its own: it only looked at the LOADED type (CompAmmoUser.CurrentAmmo)
                    // and only asked whether a bill for it was running anywhere. The resulting contradiction:
                    //   Tooltip: "1 affected by low ammo (opens their gear) - <pawn> (30274 in stock)"
                    //   Menu:    "<pawn> (already in production) - jump to bench"
                    // Same pawn, same widget, one click apart - two causes and two targets.
                    //
                    // LowAmmoState already carries both numbers (reachable over ALL usable types, anyBill
                    // over all of them). So the same classification as above decides, and the discrepancy
                    // cannot come back:
                    //   reachable > 0 -> it is there, the pawn just hasn't fetched it. The workbench does not
                    //                    help, the gear does. (That was the 30274-rounds case.)
                    //   otherwise     -> it is being produced. Jump to the bench, and look for it across ALL
                    //                    usable types instead of only the loaded one - otherwise nobody finds
                    //                    it when a different type of the set is currently being made.
                    // FindActiveBillBench stays, but only as a LOOKUP (which bench is it), no longer as the
                    // verdict (is anything running at all) - anyBill says that.
                    // GROUP from ReadinessGroupFrom identical entries onwards: one entry PER COLONIST turned
                    // the menu into a two-column full-screen list in a large colony (29 colonists, the same
                    // text 24 times). Up to four the name stays; from five on the count is shown and the
                    // action applies to all of them.
                    // (B) lying there, not fetched -> fetch; (B') already in production -> to the bench;
                    // (B'') on the way -> gear, or grouped the bill overview; (C without offer) nothing there,
                    // nothing in progress -> gear, or grouped the supply window for exactly these colonists.
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

                    // (C without offer) Genuinely missing, and this window has nothing to offer for it.
                    // Same path as (A) - the gear - but with the truth in the text: the tooltip of the same
                    // widget says "none in stock, nothing in progress" for these colonists, and both read
                    // the same classification from LowAmmoState.
                    foreach (Pawn p in noneOffered)
                    {
                        Pawn local = p;
                        acts.Add(("CER_LoadoutUtility_ReadinessLowAmmoNoneOption".Translate(local.LabelShortCap).ToString(),
                            () => Find.WindowStack.Add(new Dialog_PawnGear(local))));
                    }
                }
                // Better ammo for pinned templates.
                // Deliberately HERE and explicitly NOT as a vanilla alert in the right-hand bar: it should
                // not sit there permanently as a problem, but it does belong in the mod's internal issue
                // overview next to Arm my Colony. This widget IS that overview.
                // Not a colonist problem, so it comes after the colonist rows and does not count towards
                // their number: it is an opportunity, not a shortage.
                List<Loadout> betterAmmo = TemplatesWithBetterAmmo();
                if (betterAmmo.Count > 0)
                    acts.Add(("CER_LoadoutUtility_ReadinessBetterAmmoOption".Translate(betterAmmo.Count.ToString()).ToString(),
                        () => Find.WindowStack.Add(new Dialog_MassReplaceAmmo())));

                // 'Assigned, carries nothing': only let them fetch if something fetchable really exists - otherwise
                // supply is the right path (a "fetch now" entry would just report "Nothing to fetch right now").
                // Overloaded/blocked stay with the gear (drop buttons). From ReadinessGroupFrom identical entries
                // on, ONE entry for all (see above).
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

                // ALWAYS a menu, even with a single entry (it shows what will happen). Previously the widget
                // jumped straight into the target window when there was exactly one problem.
                if (acts.Count > 0) Find.WindowStack.Add(new FloatMenu(acts.Select(a => new FloatMenuOption(a.label, a.act)).ToList()));
            }

            // Per-pawn "(N in stock)"/"(in production)"/"(none queued)" suffix for the low-ammo names - same
            // ComputeAmmoAvailability/WeaponHasActiveBill this mod's own ammo cells already use everywhere
            // else, just keyed off the pawn's CURRENTLY LOADED ammo type (CompAmmoUser.CurrentAmmo) instead
            // of a loadout slot. Observed: naming the pawn alone didn't say whether the fix is
            // "wait, it's coming" or "go queue a bill" - the same gap the availability-info audit flagged.
            string LowAmmoSuffix(Pawn p)
            {
                // ONLY from LowAmmoState, not a single stock or bill lookup of its own any more. The second
                // calculation used to be here: it only looked at the LOADED type (CompAmmoUser.CurrentAmmo)
                // instead of all usable ones, used a different stock function and counted forbidden stock.
                // The resulting contradiction: tooltip "(none in stock, not queued)" vs. menu "(supplies are
                // on the way)" - same widget, same pawn, two answers. The classification now decides the text
                // too, so it can no longer contradict the answer below it.
                // The "loadout itself asks for too little" case stays first: it is the only one where
                // neither stock nor production changes anything, and therefore the most misleading one if it
                // says "(200 in stock)" - that reads as if the colonist only has to go and get it, and the
                // warning stays anyway.
                if (!CombatReadiness.LowAmmoState(p, out CombatReadiness.LowAmmoInfo info) || !info.IsLow) return "";
                switch (info.cause)
                {
                    case CombatReadiness.LowAmmoCause.PlanTooSmall:
                        return "CER_LoadoutUtility_LowAmmoPlanTooSmall".Translate(info.planned.ToString(), info.threshold.ToString()).ToString();
                    case CombatReadiness.LowAmmoCause.NotProduced:
                        return "CER_LoadoutUtility_LowAmmoNoneQueued".Translate().ToString();
                    default:
                        // Pending means: reachable stock covers the gap OR a bill is running. In this order,
                        // because "it is in storage" is the more concrete information.
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
            // The grouping itself has MOVED into the lambda: it is only read there, but ran out here - i.e.
            // every frame, even though this widget is permanently on screen. OtherIssueTags builds a list
            // per colonist, four translations and a string.Join. Exactly the waste the lambda below was
            // introduced to avoid - the work had just moved in front of it instead of disappearing.

            // Lazy (2026-08-01, this widget is ALWAYS on screen, so an eager string rebuilt every single
            // frame regardless of hover is pure waste - same fix as several other spots earlier today) AND,
            // while at it, the "production gaps" wording is now only used when there's a REAL gap: the
            // click-handler above already distinguishes "genuinely needs producing" from "colony already
            // has it, this pawn just hasn't picked it up" via the same AnythingToSupply check, but the
            // tooltip text never made that same distinction - it said "production gaps" for
            // a pawn whose click-through correctly said "supplies in stock, not picked up", and
            // whose own Assign Loadouts card showed fully green (that card checks whether the LOADOUT is
            // satisfiable at all, a different, broader question than "is this pawn physically holding it
            // right now").
            // Not while this widget's own click menu is open - see TipWanted, which holds the same
            // condition for all of the mod's guarded tooltips. THIS tooltip doesn't go through TipWanted
            // (as a lambda it is already lazy), so it needs the check itself - and it is where the problem
            // showed: the menu opens under the cursor and the tooltip stayed on top of it.
            if (Find.WindowStack?.WindowOfType<FloatMenu>() != null) return;
            TooltipHandler.TipRegion(r, () =>
            {
                // FORMATTED: bold, colors by severity, headers separated from the description text. Without
                // touching 13 languages: each line is translated with a placeholder instead of the names and
                // split there - heading bold and colored (red = something is really missing, yellow = lying
                // there / running / no bill, gray = explanation), the names indented below. See
                // ReadinessStyledLine.
                string tip = total == 0 ? "<b>" + "CER_LoadoutUtility_ReadinessAllGood".Translate().ToString() + "</b>"
                    : ReadinessStyledHeader((total == 1 ? "CER_LoadoutUtility_ReadinessHeaderSingular".Translate(total.ToString())
                        : "CER_LoadoutUtility_ReadinessHeaderPlural".Translate(total.ToString())).ToString());
                if (noLoadoutDistinct.Count > 0)
                    tip += ReadinessStyledLine("CER_LoadoutUtility_ReadinessNoLoadoutLine".Translate(noLoadoutDistinct.Count.ToString(), ReadinessNamesSentinel).ToString(),
                        CombatReadiness.NameList(noLoadoutDistinct), ReadinessRedHex);
                if (prodDistinct.Count > 0)
                {
                    // The same single question as in the click menu above, and also the same calculation:
                    // the exception for WeaponUnavailable colonists was dropped there and here alike. If it
                    // remained in only one of the two places, the tooltip would again say something
                    // different from the click below it.
                    // Cached via ReadinessRealGap, not called directly: Verse re-evaluates this lambda EVERY
                    // frame while the tooltip is shown. The expensive part (BuildSupplyRequestForPawns plus a
                    // full Dialog_SupplyLoadouts, whose constructor walks all colonists per weapon) ran every
                    // frame - precisely while one wants to read in peace. The comment above this TipRegion
                    // says the tooltip was made lazy for exactly that reason; the expensive work had since
                    // moved inside.
                    // Same three-way split as in the click menu - see there. Tooltip and menu read the same
                    // per-colonist question (ReadinessGapRowsFor), so they cannot contradict each other.
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
                    // "(opens their gear)" only where it is actually true. Not only the CAUSES differed, the
                    // TARGETS did too: the tooltip promised the gear for a pawn while the menu jumped to the
                    // workbench.
                    // Overloaded, blocked and "assigned, carries nothing" really do all go to the gear - the
                    // phrase stays for them. Colonists who are ONLY low on ammo, however, are split by the
                    // menu across three paths (fetch from storage, go to bench, raise the amount in the
                    // loadout), and the suffix after their name already says which one. So this one group
                    // gets the line without an action promise.
                    List<Pawn> members = g.ToList();
                    bool lowAmmoOnlyGroup = members.All(p => lowAmmoC.Contains(p)
                        && !overloadedC.Contains(p) && !capacityBlockedC.Contains(p));
                    // Overloaded/blocked/carries nothing are real shortages (red), 'only low on ammo' is yellow.
                    bool severe = members.Any(p => overloadedC.Contains(p) || capacityBlockedC.Contains(p) || notCarriedC.Contains(p));
                    tip += ReadinessStyledLine((lowAmmoOnlyGroup
                        ? "CER_LoadoutUtility_ReadinessOtherIssueLineNoAction"
                        : "CER_LoadoutUtility_ReadinessOtherIssueLine").Translate(members.Count.ToString(), g.Key, ReadinessNamesSentinel).ToString(),
                        NamesFor(members), severe ? ReadinessRedHex : ReadinessYellowHex);
                }
                // See the entry of the same name in the click menu: an opportunity, not a shortage - so at
                // the very bottom and in a calmer tone than the problem lines above.
                List<Loadout> betterAmmoTip = TemplatesWithBetterAmmo();
                if (betterAmmoTip.Count > 0)
                    tip += "<color=" + ReadinessGreyHex + ">" + "CER_LoadoutUtility_ReadinessBetterAmmoLine".Translate(betterAmmoTip.Count.ToString(),
                        string.Join(", ", betterAmmoTip.Take(5).Select(l => CellLabel(l)).ToArray())
                        + (betterAmmoTip.Count > 5 ? "CER_LoadoutUtility_AndNOthers".Translate((betterAmmoTip.Count - 5).ToString()).ToString() : "")).ToString() + "</color>";
                return tip.TrimEnd();
            }, 0x8EA1);
        }

        // "Is there really a production gap for these pawns?" - the same answer as the click path, just
        // affordable to ask often. Short lifetime instead of a permanent cache, because the answer changes
        // with stock and bills; the key depends on the pawn set so a different set doesn't inherit the
        // previous one's answer. Same pattern as weaponInvCache/ammoResearchedCache next to it.
        // Holds the NUMBER of entries instead of yes/no - 0 means 'no real gap', like false before.
        private static readonly Dictionary<int, int> readinessGapCache = new Dictionary<int, int>();
        private static float readinessGapCacheTime = -99f;

        // Formatting of the readiness tooltip. The line keys carry the names as a placeholder at the end
        // (' - {1}'); translation runs with a sentinel and is split afterwards: before it the heading
        // (without the ' - ' remainder), bold and in the severity color, after it the names indented and gray.
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

        // Header: the first paragraph bold, everything after it (the 'counted per colonist' note) gray.
        private static string ReadinessStyledHeader(string translated)
        {
            string t = translated.TrimEnd();
            int i = t.IndexOf("\n\n", StringComparison.Ordinal);
            if (i < 0) return "<b>" + t + "</b>\n";
            return "<b>" + t.Substring(0, i) + "</b>\n<color=" + ReadinessGreyHex + ">" + t.Substring(i + 2).Trim() + "</color>\n";
        }

        // The same question for ONE colonist: would the supply window have a row for them alone? Cached via
        // the pawn-set key of ReadinessGapRows (1 s); a fresh list per call, because the request may hold
        // on to it.
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
            // AnythingToProduce, like the click path next to it: the tooltip says "production gaps", and the
            // same line is the button that opens the supply window. If this still used AnythingToSupply, the
            // text would promise a gap where the click only had a cleanup list to offer - exactly the kind
            // of contradiction this check was introduced to fix.
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
                    // AmmoMechRetention, not MechRetention - see there: for an AMMO TYPE the blunt axis is
                    // constant within one caliber, and with Mathf.Max it would mask exactly the difference
                    // that matters here.
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
            // Fire pulls towards ANTI-FLESH, not anti-mech (sign corrected).
            // This used to be +0.25 with the reasoning "fire also hurts mechs" - which is demonstrably wrong
            // in game: RimWorld's mechanoid base carries <Flammability>0</Flammability> (Core/Defs/
            // ThingDefs_Races/Races_Mechanoid.xml), they don't burn at all.
            // It was also a contradiction WITHIN this file: AffinityFromEffects (the branch for ammo with
            // no measurable projectile, twenty lines further down) has Flame at -0.25, and EffectMultMech
            // uses a factor of 0.1 ("mechs are immune to fire"). Same question, three places, two signs -
            // depending on whether the type has an evaluable projectile.
            // Visible consequence: at the right end of the slider AP-I (+0.50) beat Sabot (+0.27) and AP
            // (+0.25), even though AP-I has the same sharp penetration as AP (11) and Sabot is well above
            // at 19.25 - the incendiary part, which does nothing against mechs, tipped the balance.
            // After the fix Sabot wins there, then AP, and AP-I sits neutral. The left end of the slider
            // is unaffected (HP wins before and after).
            if ((fx & SpecialAbility.Flame) != 0) extra -= 0.25f;
            if ((fx & SpecialAbility.Fragment) != 0) extra -= 0.2f;   // shrapnel pings off armor -> a bit more anti-flesh
            if ((fx & SpecialAbility.Stun) != 0) extra -= 0.2f;       // less-lethal -> anti-flesh
            if ((fx & SpecialAbility.Concussive) != 0) extra -= 0.1f;
            if ((fx & SpecialAbility.Explosive) != 0) extra -= 0.1f;
            if ((fx & SpecialAbility.ExplosiveSecondary) != 0) extra -= 0.05f;

            if (!hasKinetic) return AffinityFromEffects(fx);          // no projectile data (self-ammo etc.): fall back
            return Mathf.Clamp(ballistic + extra, -1f, 1f);
        }

        // ====== Ammo comparison: three bars per type, normalized to the caliber ======
        //
        // Replaces the single "what is it good against" bar. Goal: the values (damage etc.) as bars side
        // by side so they are easy to compare. So no interpretation any more ("Mostly armour"), but the
        // three numbers an interpretation would be derived from: damage per shot, sharp and blunt
        // penetration.
        //
        // The reference is the CALIBER: each bar is full at the highest type of the same AmmoSet. That was
        // exactly the flaw of the old display - blunt penetration is constant within a caliber, and in
        // absolute terms it lifted every rifle round onto the armor side, HP included (verified by
        // recalculation). Relative to the caliber the bar again shows what the choice is about: HP has the
        // longest damage bar and the shortest sharp one, Sabot the reverse, FMJ in between. A bar that is
        // the same length for all types (blunt for rifles) thus honestly says "nothing to choose here".
        //
        // ItemMechAffinity (the anti-flesh/anti-mech slider) is left untouched - that one is a decision,
        // this one a display.
        private static readonly Texture2D ammoDmgBarTex = SolidColorMaterials.NewSolidColorTexture(new Color(0.86f, 0.55f, 0.22f));
        private static readonly Texture2D ammoSharpBarTex = SolidColorMaterials.NewSolidColorTexture(new Color(0.33f, 0.60f, 0.90f));
        private static readonly Texture2D ammoBluntBarTex = SolidColorMaterials.NewSolidColorTexture(new Color(0.45f, 0.72f, 0.62f));

        // Width callers may count on - one number, two row drawers (the two ammo rows in Dialog_ChangeAmmo
        // are twins and would otherwise drift apart).
        public const float AmmoStatBarsW = 240f;
        private const float AmmoStatBarGap = 6f;
        public static float AmmoStatBarW => (AmmoStatBarsW - 2f * AmmoStatBarGap) / 3f;

        private struct AmmoFamilyMax { public float dmg, sharp, blunt; }
        private static Dictionary<ThingDef, AmmoFamilyMax> ammoFamilyMaxCache;

        // The maximum per value over all types of the same AmmoSet. Built once per process over all sets;
        // a type that appears in several sets gets the larger maximum - so its bar is never longer than
        // full.
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
            if (proj == null) return;   // self-ammo (grenades) has no projectile - nothing to show there
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

            // TipWanted first - otherwise the text would be built per row and frame (up to a dozen types per colonist).
            if (TipWanted(r))
                TooltipHandler.TipRegion(r, "CER_LoadoutUtility_AmmoStatTip".Translate(
                    dmg.ToString("0.#"), fam.dmg.ToString("0.#"), ap.ToString("0.#"), fam.sharp.ToString("0.#"),
                    apB.ToString("0.#"), fam.blunt.ToString("0.#")));
        }

        // The three column headers above the bars - the same split as the bars themselves.
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
            // The DISPLAYED name, not the stored label (the "W|" prefix used to survive in the tooltip).
            // This heading sits above every contents list the mod shows anywhere - it was the last major
            // place still using the raw label.
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
        // CORRECTION 2026-08-25 (from the decompiled Verse.Def): it was never a collision of three strings.
        // Def.GetHashCode() returns the field `defNameHash`, set only by Def.ResolveDefNameHash() - which
        // RimWorld's loader calls for XML defs, but CE never calls for its code-generated LoadoutGenericDefs.
        // ALL CE generics carry hash 0, and Equals(Def) compares exactly that field: for any standard
        // dictionary "Any Meal", "Any Medicine" and every "Any ammo for X" are ONE key.
        // The root has since been fixed in CEReloadedMain (ResolveDefNameHash at startup); these two
        // comparers stay as a second safety net for the places that already use them. `internal` so that
        // the round-robin calculation in Dialog_AssignLoadouts uses the same comparer instead of a third.
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
            // Ground stock counts too (consistent with ammo and weapons).
            // For ammo, storage, ground and carried items count together (AmmoOnHand); for weapons the
            // ground counts anyway - only pack contents lacked that pool. Failure case: after a raid 30
            // medicine lie loose on the map, and the mod suggested producing some while it lay right there.
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
        // Generate Equipment Packs screen went briefly BLANK - all-zero - when navigating Next then Back
        // within Arm my Colony, then self-corrected about a second later). A 1-real-second window can span
        // several logical frames AND a dialog reconstruction (Back rebuilds several of these dialogs fresh),
        // so a value cached from BEFORE the transition could still be "fresh" by the wall-clock the instant
        // the new dialog's first frame reads it, even though the underlying colony state (or which Defs are
        // even being asked about) has moved on - a real cross-transition staleness window a per-second TTL
        // can't rule out. Keying the clear on Unity's own per-rendered-frame counter instead bounds every
        // cached value to the SINGLE frame it was computed in, which still dedupes the repeated Layout+
        // Repaint-pass calls a single frame makes for the same Def (the actual redundant-work case this
        // caching exists for) without ever risking a value surviving into a frame where anything changed.
        // Now 0.3 s instead of per frame: ComputeItemOwnershipUncached walks all colonists per Def, and the
        // pack wizard queries every one of its rows on each pass - the same tiering as the Assign tab's cell
        // caches; these windows pause the game, so stock does not change.
        private static float itemOwnCacheTime = -99f;

        // Colony-wide demand for this item: what ALL colonists together request through their packs and
        // their personal anchor. Counterpart to ComputeItemOwnership, which also returns stock colony-wide.
        // WHY: the availability tooltip mixed reference quantities - stock colony-wide, but demand only for
        // the one colonist whose row the cursor is on. With two colonists at 5 medicine each and 3 in
        // storage, BOTH showed "Assigned, in storage: 3", and a shortfall did not appear at all or too
        // small. Correct would be "3 assigned in storage, 7 missing" - which is what you get when both
        // sides mean the same quantity. The pawn's own statement is not lost: BuildAvailabilitySegments
        // still carves the green "carries it himself" segment out of stock.
        // Only runs on mouse hover (the tooltip factory is lazy), not per frame.
        // One frame, one answer (the Assign Loadouts tab was sluggish).
        // This function is a COLONY-WIDE scan, and it was called from the draw loop per item, per cell,
        // per colonist card and per frame: with 12 colonists at four cells each and several contents that
        // quickly adds up to a few hundred full passes over all pawns - per frame. Worse, the body
        // allocates constantly (GetPawnPacks builds a new list every time, plus the two
        // `?? new List<...>()`), which keeps the garbage collector busy.
        // The result cannot change within a frame - it only depends on assigned loadouts, and no draw call
        // changes those. Hence the same frame cache as ComputeItemOwnership twelve lines above, including
        // the same reference comparer.
        // Deliberately frameCount instead of a time span: a TTL would show the old number for up to a
        // second after an assignment, and this cell in particular has to be right immediately.
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
                // CarriedPackSlots instead of GetPawnPacks: it also sees what is carried via a ROLE - a role
                // doesn't attach its packs, it bakes them in. Previously the role holder was missing from this
                // sum AND from the "Assigned to" list next to it.
                foreach (LoadoutSlot s in AmmoAssignmentUtility.CarriedPackSlots(p))
                    if ((Def)s.thingDef == d || (Def)s.genericDef == d) n += Mathf.Max(0, s.count);
                Loadout leaf = AmmoAssignmentUtility.GetAmmoLeaf(p);
                foreach (LoadoutSlot s in leaf?.OwnSlots ?? Enumerable.Empty<LoadoutSlot>())
                    if ((Def)s.thingDef == d || (Def)s.genericDef == d) n += Mathf.Max(0, s.count);
            }
            // THIS write was missing: the cache was created, cleared and read - just never filled, so by
            // construction it could never hit. The comment above described an effect that never existed:
            // this body ran over all colonists on EVERY call, with CarriedPackSlots and GetAmmoLeaf per pawn.
            // Its direct neighbor ComputeItemOwnership does it right twenty lines further on.
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

            // + GroundStockCount: medicine/rations lying loose on the map can be picked up just like stored
            // ones, and for ammo and weapons the ground has long been counted. GenericStockCount already
            // includes its own ground share, so only add it in the concrete branch.
            int stored = td != null ? StoredStockCount(td) + GroundStockCount(td) : GenericStockCount(gd);
            int production = 0;
            if (td != null) production = WeaponQueuedCount(td);
            else if (gd != null)
                foreach (ThingDef member in GenericFamily(gd)) production += WeaponQueuedCount(member);

            // ============ TWO corrections, both from the buckler case ============
            //
            // (1) WORN items count too. Until here only the INVENTORY counted as "carried". A buckler is
            // apparel: when worn it sits in WornApparel, not in the inventory - and since it is not spawned
            // either, it also drops out of the STORAGE count. Result: it was in no pool at all. That was
            // the symptom "we have 4 bucklers, but only 3 are shown". The same blind spot had already been
            // fixed once in ComputeAvailability (the comment there reads "A CE shield is apparel, not
            // equipment and not inventory - it fell through all three checks"); this path is the fourth
            // occurrence and was not updated back then. Equipment in hand counts for the same reason.
            //
            // (2) "Assigned AND carried" is computed PER COLONIST, not as the intersection of two colony
            // totals. Example: four colonists have a buckler assigned, three also carry it, and a fourth
            // buckler ended up with someone who has none assigned. With the old colony totals carried=4
            // and demand=4, so min(4,4) = 4 "assigned and carried" - although one of them sits with a
            // colonist WITHOUT an assignment, and conversely one assignee has none available at all.
            // The correct mapping is per person: who carries what they are entitled to (assigned and
            // carried), who carries what they are NOT entitled to (carried only), and who is entitled to
            // something they don't have (open demand, covered from storage or reported as missing).
            int carried = 0, demand = 0, matched = 0, openDemand = 0;
            foreach (Pawn p in AllColonyPawns())
            {
                int pawnCarried = 0;
                if (p.inventory?.innerContainer != null)
                {
                    if (td != null) pawnCarried += p.inventory.innerContainer.Where(t => t.def == td).Sum(t => t.stackCount);
                    else if (gd?.lambda != null) pawnCarried += p.inventory.innerContainer.Where(t => t.def != null && gd.lambda(t.def)).Sum(t => t.stackCount);
                }
                // Count the magazine too (same counting as PawnCarriedCount - see MagazineAmmoCount).
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
                // CarriedPackSlots instead of GetPawnPacks: GetPawnPacks only knows attached pack OBJECTS,
                // and a role is not one. The same blind spot had been fixed in four other places - this one
                // slipped through, even though the caller next to it (ColonyWideAssignedNeed) had long been
                // switched over. Failure case: two colonists on a Medic role with 5 medicine each, 3 in
                // storage - demand here was 0, so the tooltip said "Available: 3" while the shortfall next to
                // it already said 7. Two contradicting numbers from one cell.
                foreach (LoadoutSlot s in AmmoAssignmentUtility.CarriedPackSlots(p))
                {
                    if (td != null && s.thingDef == td) pawnDemand += s.count;
                    else if (gd != null && s.genericDef == gd) pawnDemand += s.count;
                }
                // Plus the INDIVIDUAL ITEMS on the colonist's own anchor. The demand side
                // (ColonyWideAssignedNeed) and the "Assigned to" list already count them - this stock counter
                // did not. Same question ("what is assigned to this colonist"), two answers: an item assigned
                // via "Other" showed up in the shortfall as demanded, but its carried piece as "unassigned" -
                // and the rows of the summary were off by exactly those pieces. Same filter as there: no
                // weapons, no ammo (they have their own counters).
                Loadout ownAnchor = p.GetLoadout();
                if (ownAnchor?.OwnSlots != null)
                    foreach (LoadoutSlot s in ownAnchor.OwnSlots)
                    {
                        if (s.thingDef != null && (s.thingDef.IsWeapon || IsAmmoDef(s.thingDef))) continue;
                        if (td != null && s.thingDef == td) pawnDemand += s.count;
                        else if (gd != null && s.genericDef == gd) pawnDemand += s.count;
                    }
                demand += pawnDemand;
                // The mapping that matters: only what THIS colonist carries AND has assigned counts as
                // "assigned and carried".
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
        // Does the colony have more than one map? Stops at the second hit, so it costs nothing.
        public static bool HasMultiplePlayerMaps()
        {
            int n = 0;
            foreach (Map m in PlayerMaps()) { n++; if (n > 1) return true; }
            return false;
        }

        // Location WITH the map name as soon as the colony has more than one map.
        // Reason (an item the game reported as present could not be found): every stock counter in the
        // mod runs over ALL colony maps (PlayerMaps), but the location line only said "ground" or the
        // stockpile zone name. An item on the second settlement then reads like one at the front door -
        // and RimWorld's own search is guaranteed not to find it, because it explicitly searches only the
        // CURRENT map ("Search the current map"). With a single map the line stays unchanged; the name
        // would only be noise there.
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

                // CORRECTION: an extra loop over GetPawnSidearms used to sit here, reasoning "a sidearm is NOT
                // in the anchor's slots". That reasoning is WRONG, and it doubled the value: GetPawnSidearms
                // returns exactly the anchor's EXTRA PARENTS, and Patch_Loadout_MultiParentSlots merges their
                // slots into `Slots`. The same sidearm was thus counted once via the union and once via the
                // loop. That is exactly the "inserted sidearm arrives with double the amount" symptom: a
                // completely empty colonist, ONE attached loadout with amount 4, shown as x8.
                // `Slots` is the complete answer - template, role, packs and sidearms together.
                // Sum over ALL matching slots instead of FirstOrDefault: if two different sources provide the
                // same weapon (template AND sidearm), those really are two pieces, and dropping one row was
                // the opposite direction of the same bug.
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
                // Inventory AND magazine (MagazineAmmoCount) - the way CE itself counts.
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
                // Count the magazine too (MagazineAmmoCount).
                if (td != null) inv += MagazineAmmoCount(p, d => d == td);
                else if (gd?.lambda != null) inv += MagazineAmmoCount(p, d => gd.lambda(d));
                if (inv > 0) carriedBy.Add(p.LabelShortCap + " x" + inv);

                // CarriedPackSlots instead of GetPawnPacks - see the matching correction in
                // ComputeItemOwnershipUncached. The same symptom had come up before (the role holder
                // was missing entirely from 'Assigned to'); back then only the pack cell in the Assign tab was
                // fixed, not this list, which is used in the pack editor, the pack wizard and the supply window.
                int demand = 0;
                foreach (LoadoutSlot s in AmmoAssignmentUtility.CarriedPackSlots(p))
                {
                    if (td != null && s.thingDef == td) demand += s.count;
                    else if (gd != null && s.genericDef == gd) demand += s.count;
                }
                // Plus the colonist's own INDIVIDUAL ITEMS (second half of the same issue: 2 freshly assigned
                // components stayed at "Assigned, in storage: 0" with no "Assigned to" line). What sits in the
                // "Other" cell lives in the anchor's OWN slots, not in a pack - so CarriedPackSlots could not
                // see it at all. Exactly the same set DrawOtherItemsCell draws: own slots, no weapons, no ammo.
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
        // demandDefs: for each CONCRETE def handed out, the def the DEMAND really belongs to - for a generic
        // pack entry ("Any Medicine") that is the generic one, not the concrete production target.
        // Why it is needed: `concrete` below answers "what can I create a BILL for"
        // (ResolveGenericPackItem), not "what is this about". Querying stock with it asks a different
        // question than the Equipment Packs tab - and gets a different answer: for a CONCRETE def,
        // ComputeItemOwnership only counts concretely named pack entries as demand, a generic one falls
        // through. Measured case: 600 demanded ("Any Medicine", 3 colonists x 200), 75 in storage - the
        // Packs tab said "Assigned, in storage: 75 / Available: 0", the supply window "Assigned: 0 /
        // Available: 75", and the row read as fully covered.
        public static void ScanMissingAssignedPackItems(IEnumerable<(Pawn pawn, List<Loadout> packs)> assignments,
            out Dictionary<ThingDef, int> missingItems, out Dictionary<ThingDef, List<string>> usedBy,
            out Dictionary<ThingDef, Def> demandDefs)
        {
            Dictionary<Def, int> demand = new Dictionary<Def, int>();
            Dictionary<Def, List<string>> demandedBy = new Dictionary<Def, List<string>>();
            foreach (var (p, packs) in assignments)
            {
                // Supply from a ROLE counts too - see AmmoAssignmentUtility.RoleSupplySlots. `packs` only
                // contains attached pack OBJECTS, and a role is not one: it bakes its contents into a flat
                // loadout. Without this, role holders never produced a shortfall, no matter how empty storage
                // was.
                //
                // RoleSupplySlots, NOT CarriedPackSlots: CarriedPackSlots returns BOTH - the role slots AND the
                // OwnSlots of the attached packs. The loop below counts the same attached packs once more. The
                // earlier assumption ("a pawn has either packs OR a role") is wrong: EnsureAmmoLink only clears
                // the extra parents at the MOMENT of the role change, after that packs can be freely attached
                // to any colonist, so both sources can indeed hang on one colonist.
                // REFINED: the pack selection in Assign Loadouts no longer lets role holders attach packs
                // (that cell is grayed out for them). The double-count guard is still needed, just via a
                // different path: a ROLE can itself carry an Equipment Pack (CommitRole attaches it as an
                // extra parent instead of melting it in). A colonist on such a role therefore has role supply
                // slots AND pack parents - exactly the situation this is about.
                // Failure case: role with 5 medicine plus an attached pack with 3 -> demand 3+5+3 = 11 instead
                // of 8. The two sister places in Dialog_SquadSetup (BuildPlanWideShortfalls and
                // PackRequirementsFor) already do it right - this was the third.
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
                // usedBy even with full coverage - demand semantics, see the correction in
                // ScanMissingAssignedWeapons.Note and ComputeMissingForCreatedLoadouts.Track.
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

        // Several demand keys can point to the SAME concrete production target (a concrete assignment and a
        // generic entry that includes it). Then there is no SINGLE def the demand belongs to - and in that
        // case rather record none than claim one of the two: without an entry the callers fall back to the
        // concrete def, i.e. the previous behavior.
        private static void RecordPackDemandDef(Dictionary<ThingDef, Def> map, ThingDef concrete, Def source)
        {
            if (map.TryGetValue(concrete, out Def had))
            {
                if (had != source) map[concrete] = concrete;   // ambiguous -> fall back to the concrete def
                return;
            }
            map[concrete] = source;
        }

        // Preferred concrete family member for a generic pack item when queuing production. No slider/cost
        // preference here (unlike the wizard's own PickMedicineDef/PickFoodDef/PickDrugDef) - this only fires
        // for an ALREADY-assigned pack that needs restocking, not for building one from scratch.
        //
        // PRODUCIBILITY BEATS STOCK. Previously "some of it is in storage" won, and that made the medicine
        // row disappear from the supply window: "Any Medicine" resolved to herbal medicine as soon as a
        // single bundle of it was lying around - and herbal medicine has no recipe at all in vanilla (it
        // grows). The callers' IsProducible filter then silently dropped the row, while the tooltip kept
        // calculating and said "Still missing: 42".
        // The purpose of this function is to be able to create a BILL; a def for which no bill can exist
        // does not serve it. The old intent ("don't switch supply chains just because of restocking")
        // stays as the first choice - it now only applies among those that can actually be produced.
        private static ThingDef ResolveGenericPackItem(LoadoutGenericDef gd)
        {
            if (gd == null) return null;
            List<ThingDef> family = GenericFamily(gd);
            bool Researched(ThingDef td) { RecipeDef r = GetRecipeFor(td); return r != null && RecipeIsResearched(r); }
            return family.FirstOrDefault(td => IsProducible(td) && IngredientStockCount(td) > 0)   // already in stock AND producible
                ?? family.FirstOrDefault(IsProducible)                                             // producible, recipe + bench exist
                ?? family.FirstOrDefault(Researched)                                               // recipe researched, bench missing
                ?? family.FirstOrDefault(td => IngredientStockCount(td) > 0)                       // at least whatever is there
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

        // Cached: IsAssignableFrom is reflection, the result depends only on immutable def data, and this
        // function runs per slot in Patch_Loadout_MultiParentSlots - i.e. on Loadout.Slots, which CE's think
        // tree touches per colonist and per decision. Session lifetime, like the other def caches here.
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
        // specifically). Shown purely as INFO in ingredient tooltips - the "storage zone required" behavior
        // itself is fair and intentional, not a bug; this line just makes the loose stock visible.
        // Short time cache, like every other stock function in this family already has (weaponInvCache 1s,
        // itemOwnCache, affordabilityCache 2s) - GroundStockCount and CarriedStockCount were the only ones
        // without, and they are exactly the ones called per def: in RecomputeAssign (every 150 ms while a
        // slider is dragged) and in CombatReadinessAlerts per scarce ammo type per colonist.
        // 1 second, for the same reason as the neighbors: the mod's windows pause the game, so the stock
        // practically doesn't move meanwhile - and the failure mode here would be "the number from a second
        // ago", not a wrong decision.
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

        // "How much of this sits in a real storage" - for EVERY item, not only the ones RimWorld itself
        // tracks as a resource.
        //
        // IngredientStockCount reads map.resourceCounter, which counts only defs with CountAsResource - i.e.
        // exactly what shows up in RimWorld's resource readout at the top left. Apparel, shields, weapons
        // and throwables are explicitly NOT in there and therefore ALWAYS return 0.
        // Combined with GroundStockCount, which skips everything lying in storage, this left a hole that a
        // whole item category fell through: a shield, binoculars or a grenade from an Equipment Pack lying on
        // a shelf counted as NOT PRESENT AT ALL - and the restock window suggested crafting it although it
        // was right there.
        // Same root cause as stock counting items RimWorld does not track yet, just the other direction:
        // the two grids (storage / ground) don't split the world cleanly, they leave a gap in between.
        //
        // For resources nothing changes (one dictionary lookup into RimWorld's own counter); the search
        // runs ONLY for defs that return 0 there anyway, with the same one-second cache as the neighbors.
        // Weapons are unaffected - they have their own complete count via GetWeaponInventory.
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
                    // Whatever lies in storage is counted by the STORAGE counter - which is now
                    // StoredStockCount, no longer the resourceCounter directly: for anything RimWorld does not
                    // track as a resource (apparel, shields, throwables) that counted nothing at all, and this
                    // line skipped it on top of that.
                    if (t.Position.GetSlotGroup(map) != null) continue;
                    // No "not forbidden" filter any more (2026-08-09) - see IsMarkedDoNotPickUp. Loose loot
                    // marked "do not pick up" is exactly what this function is asked about most often (a raid
                    // just ended), so it counts, and ForbiddenStockCount/DrawForbiddenMark carry the warning.
                    total += t.stackCount;
                }
            return total;
        }

        // REMOVED: the diagnostic line for the phantom stock. The question is answered - the log showed the
        // hits as real stacks on the map (holder "Map", not in storage, spawned), among them six stacks of
        // 5000 rounds of buckshot each. So the counter tells the truth; what was missing was only the
        // location in the display, which was deliberately dropped earlier.


        // "How much of this ammo does the colony have RIGHT NOW" - all three pools in ONE function.
        // Previously each call site built its own sum, and that caused a visible bug: the preview badge and
        // the availability bar counted storage + ground (+ carried), while the gate deciding the ROW in the
        // restock window counted only storage + carried. For ammo lying on the ground - the normal case
        // after a raid - one side said "all covered" (green) and the other ordered production in the same
        // pass. The bar in the same row then showed 335 available next to a bill for 12.
        // One function, three pools, no second opinion. Callers that deliberately ask a NARROWER question
        // ("what can a workbench process right now") still use IngredientStockCount directly - ground
        // stock is correctly excluded there.
        public static int AmmoOnHand(ThingDef ammoDef)
            => IngredientStockCount(ammoDef) + GroundStockCount(ammoDef) + CarriedStockCount(ammoDef);

        // The third pool next to storage and ground: what colonists are CARRYING right now.
        // IngredientStockCount doesn't see it (CE's resourceCounter counts SlotGroups only, see
        // GroundStockCount), and neither does GroundStockCount (inventory isn't lying around). Without this
        // third pool every "demand minus stock" calculation for a colonist who already CARRIES his gear is
        // too high by exactly those items - which is why the demand side used to filter out `kept` rows
        // instead, swallowing real demand along with it (for sidearms). Adding to stock instead of
        // discarding demand is the correct half.
        // Inventory only, no equipment.Primary and no carryTracker - identical to what GetAmmoOwnership and
        // ComputeItemOwnership count as their own `carried`, so the three functions can never report
        // different numbers for the same item. For weapons and shields this is the wrong question anyway:
        // Patch_LoadoutTooltip.ComputeAvailability already factors out the carrier itself (it doesn't count
        // carried items as stock and drops the carrier's demand instead).
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

        // The same question for a generic entry ("Any Medicine"): matched via the generic's lambda, not via
        // an arbitrary representative def - exactly like ComputeItemOwnership does.
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
                // Generic slots count too - see GenericAmmoDemandFor. Previously a colonist with "any 7.62"
                // dropped out of this calculation entirely.
                int slotCount = (lo?.Slots?.Where(s => s.thingDef == ammoDef).Sum(s => s.count) ?? 0)
                    + GenericAmmoDemandFor(lo, ammoDef);
                if (slotCount <= 0) continue;
                // Inventory AND magazine (MagazineAmmoCount) - otherwise the restock calculation reports a
                // shortfall CE will never fetch.
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
            // "Assigned AND carried" is settled PER COLONIST, not as the intersection of two colony totals.
            // Example: one colonist carried 5000 rounds WITHOUT a loadout, another had 12 assigned and
            // carried nothing - the old min(carried, demand) calculation booked the first one's stock as
            // 'Assigned & carried: 12' and left 'Assigned, in storage' at 0, while the colonist block of the
            // same cell correctly said 'still to fetch: 12'. Two blocks, one question, two answers.
            // ComputeItemOwnershipUncached has had the same fix for a while (see its comment (2)) - this
            // sibling was not updated along with it.
            int demand = 0, carried = 0, matched = 0, openDemand = 0;
            foreach (Pawn p in AllColonyPawns())
            {
                // Inventory AND magazine (MagazineAmmoCount).
                int pawnCarried = PawnCarriedCount(p, ammoDef);
                carried += pawnCarried;
                Loadout lo = p.GetLoadout();
                // Generic slots count too - same addition as in ComputeAmmoAvailability, so the two core
                // functions never report different demands.
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

        // ONE answer to 'how many of these has the colony assigned in total' - for the colony-wide
        // 'Missing' line in the tooltip. Three kinds, three existing calculations, no fourth:
        // weapons/throwables via GetWeaponInventory, ammo via GetAmmoOwnership (both count pieces),
        // everything else via ColonyWideAssignedNeed.
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
        // wording, including the "on the ground" info line) - it used to have its OWN separate icon+inline-
        // colored-number rendering with slightly different tooltip wording, an inconsistency between material
        // displays. Now every ingredient/material display in the mod is the SAME one function.
        // Default parameters must be compile-time constants, so a real .Translate() call can't sit in the
        // signature - this sentinel stands in for "caller didn't pass a label at all" (distinct from an
        // explicit null, which several callers pass on purpose to mean "no label line").
        private const string DefaultTotalCostLabelSentinel = "CER_DefaultTotalCostLabel";

        // showCounts defaults to ON: the strip is almost everywhere a wide total line across the full window
        // width, and there the quantity is the whole point. The two callers that fill a narrow TABLE COLUMN
        // with it (Equipment Pack wizard 160px, pack picker 220px) turn it off: with numbers only three or
        // four chips fit into the column, the rest would be clipped - and there the number is in the same
        // cell's tooltip anyway.
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
            // With quantities: this is the grand total of a whole operation, and without numbers it could
            // not be checked by hand - see DrawIngredientCell.
            DrawIngredientCell(x + labelW, width - labelW, y, ings, 1, showCounts);
        }

        // The sorted material list of a cost line, held per dictionary OBJECT: OrderByDescending + Select +
        // ToList ran per row per pass - a dozen allocations for a list that only changes when the caller
        // rebuilds its dictionary (the pack wizard keeps it in costsCache and replaces it on uiDirty).
        // A cheap checksum over contents and count guards against a caller that silently mutates the SAME
        // dictionary: if it doesn't match, the list is rebuilt. Weak keys, so the dictionaries don't stay
        // alive because of the cache.
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

        // How many of these are STILL BEING MADE, across all workbenches on all colony maps.
        //
        // "Still being made", not "the number in the bill" - that was the bug. For a "do X times" bill
        // repeatCount really is the remaining amount, but for a "do until you have X" bill targetCount is
        // the FINAL target, and the existing stock already covers part of it. Concretely: 400 rounds in
        // storage, a bill "until 500", demand 600 -> it computed 400 present + 500 in production = 900, so
        // shortfall 0 and a green bar. In reality the bench stops at 500 and the colony is 100 rounds short.
        // That hit every display that shows "in production" anywhere, always in the same direction: too
        // optimistic. All our ammo bills are TargetCount, weapon bills are RepeatCount - so the old formula
        // was right for weapons and never for ammo.
        //
        // Stock is determined ONCE per call and shared across all bills of the same def: two "until 500"
        // bills side by side don't mean 500 minus stock twice, they both target the same storage. Hence
        // max over the targets instead of a sum of the individual remainders.
        // 1s cache like its twin activeBillCache right above (this function was measured as uncached). It
        // walks ALL workbenches on ALL maps and filters their whole bill stacks - and it sits in draw paths
        // that run per row per frame: the weapon overview (Dialog_GenerateLoadouts) always did, and now so
        // does the availability bar in the sidearm popup, where the previous display went through a cached
        // path. Without this cache that would have been a regression.
        // Same reasoning as everywhere here: the windows drawing this pause the game, so one second loses
        // nothing visible - and a freshly added bill shows up on the next tick at the latest.
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

        // The ONE rule for how much a bill stack will REALLY still produce. It used to exist three times in
        // the mod, and two of the three copies were the old, too optimistic version: plain
        // `RepeatCount ? repeatCount : targetCount`. Failure case: 45 medicine in storage and a "until you
        // have 50" bill reported 50 in production - the row read as covered (45+50) while the bench only
        // makes 5. The ammo rows in the SAME window computed it correctly next to it and said 5.
        //
        // - "do X times": real remaining amounts that add up.
        // - "until X in storage": only the HIGHEST target counts (two such bills for 50 don't make 100
        //   together), minus what is already there.
        // - "forever": contributes nothing. Its targetCount is a dead default (10) unrelated to what the bill
        //   does - it used to count as a target and skewed the number in both directions. Whether a bill is
        //   running at all is answered by WeaponHasActiveBill.
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
            // The same stock notion RimWorld's own "until X" rule uses: what is stored. For weapons also via
            // WeaponStoredCount, because they also sit in racks and shelves and IngredientStockCount doesn't
            // see them there (see its own comment).
            // StoredStockCount in the non-weapon branch: a product RimWorld does not track as a resource
            // (apparel, shields) counted as not present here - and an "until X" rule would then keep
            // producing it over and over.
            int inStock = product.IsWeapon ? WeaponStoredCount(product) : StoredStockCount(product);
            return repeatTotal + Mathf.Max(0, highestTarget - inStock);
        }

        // The highest "do until X in storage" target currently running anywhere for this def (0 if there is
        // no such bill). Answers "is the existing bill even enough", a question that wasn't asked before -
        // previously a plain "does any bill exist" applied.
        // Deliberately the target and NOT the remainder as in WeaponQueuedCount: it is compared against the
        // total demand, and the target is exactly the number that will end up in storage.
        // RepeatCount bills ("do 10") are left out - they don't maintain a stock level, so no "is this
        // enough permanently" can be derived from them.
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
        // showCounts: writes the quantity next to each icon. Off in the narrow table cells (150px there for up
        // to six materials, and the quantity is already in the batch column next to it), ON in the wide total
        // cost strip - that line used to be a bare row of icons, and "costs steel and components" without any
        // number can't be checked against anything.
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
                    // Tiny font: the number belongs to the icon and should not dominate the row - and it has to
                    // stay narrow so a strip with six materials doesn't overflow.
                    Text.Font = GameFont.Tiny;
                    string qty = (ing.count * mult).ToString();
                    float qw = Text.CalcSize(qty).x + 4f;
                    Widgets.Label(new Rect(ix, cy + 4f, qw, 20f), qty);
                    Text.Font = prevFont;
                    ix += qw + 4f;
                }
            }
            // Lazy (the whole Ammo Bills tab felt sluggish): IngredientTooltip
            // calls GroundStockCount per ingredient, which does a REAL per-Thing map scan (not a cached
            // counter lookup) - a plain-string TipRegion call forces that to run every frame for every row's
            // ingredient cell regardless of whether the mouse is anywhere near it. DrawIngredientCell is the
            // single shared ingredient-cell renderer for the whole mod (bill rows, every popup), so this one
            // fix covers all of them at once. The Func<string> overload only invokes the getter when
            // TipRegion's own Mouse.IsOver check already passed.
            // Key derived from the CONTENT, not the list reference: callers like DrawTotalCostLine rebuild
            // their list every frame, so a reference hash would change every frame too. The tooltip handler
            // tracks its active tips by uniqueId - an ID that changes per frame creates a new entry there
            // every frame instead of keeping the same tip alive. Cells with identical content now share an
            // ID on purpose; they show the same text anyway.
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
                // Keep the aspect ratio and use the full button width (the ammo button and the workbench
                // button in the restock window were not wide enough).
                // The button wasn't the problem, the drawing was: the icon went into a 20x20 SQUARE, and
                // GUI.DrawTexture simply squashes to the target size. Workbench icons are wide buildings
                // (loading bench, smithy) though - squashed, they look clipped at the sides.
                // Widgets.DrawTextureFitted scales while preserving the ratio and centers by itself; without id
                // text it gets the whole button width, with an id there is room for the icon on the left and
                // the number on the right.
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
            // EVERY weapon slot, not just the one "main weapon". Previously Arm my Colony queued satchel
            // charge x2 for a colonist who really carried them as a sidearm - and a minute later exactly that
            // bill showed up under 'Obsolete bills'.
            // Cause: LoadoutMainAndSide deliberately NEVER picks a grenade, shield or utility weapon as the
            // "main weapon" (see its comment). A sidearm template containing one of those - the normal case
            // for thrown explosives - therefore returned `null` here, leaving its bill without any demand. A
            // click on "Supply selected" would have deleted the bill the same pass had created two steps
            // earlier.
            // ComputeMissingForCreatedLoadouts already had the same fix; the obsolete side was not updated
            // with it - same question, two calculations.
            // Predicate identical to UnneededWeaponBillCandidates (IsWeapon, no utility weapons), so the demand
            // and candidate sides look at the same set.
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
                    // Without a main weapon (None) the sidearms still count - see
                    // ScanMissingAssignedWeapons, same gap.
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
        // bug (a colonist on a generic .30-06 slot, and the
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
        // Which ONE variant a generic slot currently resolves to - the same resolution as
        // MergeResolvedGenericAmmoDemand, just queryable individually and memoized. Used by the two cached
        // stock functions further down, which run over all colonists per frame: without the memo the whole
        // ammo family would be re-resolved there per pawn and slot.
        // Short lifetime, because completed research changes the answer - the same bound as the neighboring
        // IsAmmoResearched, whose result feeds into this anyway.
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

        // How many rounds of this concrete variant a loadout demands via GENERIC slots. Without this a pawn
        // carrying "any 7.62" was invisible to the central stock calculation: his demand showed up nowhere,
        // and the mod acted as if nobody needed the ammo.
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
                        // !IsSelfAmmoWeapon: thrown explosives and throwing weapons (satchel charge, grenades,
                        // spears) are ThingClass AmmoThing, so IsAmmoDef says yes - but the demand side right
                        // next to this explicitly excludes them (ComputeAmmoNeedForLoadouts, see the reasoning
                        // there: they belong to the weapons area, which offsets real ownership). The candidate
                        // and demand sides thus looked at different sets, producing a contradiction in the
                        // SAME window: "satchel charge" listed as demand with +5 batches and at the same time
                        // ticked under "nobody needs this anymore" - one click on Confirm would have deleted
                        // exactly the bill the same window wanted to create two rows above.
                        // The weapons area still sees them: UnneededWeaponBillCandidates checks against
                        // ComputeWeaponDemand, which explicitly counts sidearms.
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
        // wiring inline (the popup reached via Dialog_AssignLoadouts'
        // OpenProducePopup - by far the most commonly seen instance, fired every single Assign Loadouts
        // apply - never got this at all, only the separate, far rarer close-of-window
        // CheckAndOpenMissingProductionPopup did; a second independent copy of the same wiring would have
        // been exactly the kind of duplication already flagged
        // elsewhere, not a real fix).
        // (The scope toggle no longer exists; includeCreated stays false by default.)
        // ANY CALLER THAT DRAWS A SCOPE TOGGLE MUST PASS ITS STATE THROUGH HERE. The previous comment said
        // "includeCreated defaults to false, matching every existing caller's own scope everywhere else -
        // Created-scope cleanup stays a Bill Overview tab-only concept": that was true at first and became
        // wrong once TWO more callers started drawing the toggle themselves (`showAmmoScopeToggle = true`).
        // Both took the default, so their obsolete list always computed in the ASSIGNED view, regardless of
        // what the toggle above it said. Failure case: toggle on "Consider created loadouts", the created
        // loadout "W| Saiga 12K" is in the same list - and its bill still showed up under "nobody needs this
        // anymore". Since those checkboxes are pre-ticked, that is one click away from data loss.
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
            // Worded identically to the deletion in Dialog_ArmColony: that one spares a "[Role] X" loadout
            // without a role card (IsRoleLabel), the counter didn't - since the All column shows
            // "created+hand-built", such an orphan loadout appeared as "+1 hand-built" that the run then
            // didn't delete after all.
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
                // CannotFight excluded (found while tracing a Supply Loadouts contradiction) -
                // matches BuildWeaponLocationTooltip's own "Assigned to:" list, which already filters the same
                // way. A pawn who can never actually receive/use a weapon (baby, incapacitated, ...) still
                // showing up here inflated "assigned" demand without ever appearing in that list, so the two
                // could silently disagree on how many pawns "really" need this weapon.
                // Sidearm grenades are back in the anchor's slot group (Patch_Loadout_MultiParentSlots.
                // IsRealAmmoSlot) - a single count is enough, no second list next to it.
                // PIECES, not carriers: a colonist with FOUR satchel charges assigned was counted as 1 here -
                // so only 1 piece in storage counted as assigned to him and the rest as freely available
                // (yellow), while 'Assigned to: <name> x4' next to it gave the piece count. For weapons (one per
                // colonist) nothing changes, for sidearms with a quantity it does.
                Loadout lo = p.GetLoadout();
                if (lo != null && !lo.defaultLoadout && lo.Slots != null && !CannotFight(p))
                    assigned += lo.Slots.Where(s => s.thingDef == w).Sum(s => Mathf.Max(1, s.count));
            }
            total = stored + carried;
            weaponInvCache[w] = new WeaponInv { total = total, carried = carried, stored = stored, assigned = assigned };
        }

        // Converts GetWeaponInventory's four raw numbers into the sections of the availability bar. This used
        // to be a verbatim copy in FOUR places - Assign tab, weapon overview in the Bill Overview, Squad Setup
        // and the sidearm popup. Exactly the bug class that is costly here: one copy gets fixed, the other
        // three don't, and "one of four places shows it correctly" is the telltale sign. Hence deleted
        // instead of kept in sync.
        //
        // The overlap is the crux, and the reason nobody should redo this math in their head: `carried` and
        // `assigned` partly count the SAME pieces (whoever carries a weapon usually also has it assigned).
        // Added without separating them, the sum would be too large and the bar longer than the stock.
        //
        // TWO places deliberately do NOT do it this way and must not be moved here:
        // - Dialog_GenerateLoadouts' Owned column feeds different inputs: it counts carried PIECES against
        //   the assigned quantity (assignedItemCount), while `assigned` here counts COLONISTS. For stackable
        //   items - grenades, throwing spears, explosive charges - those are different numbers; the
        //   difference surfaced as a bug and was resolved there on purpose.
        // - Dialog_SupplyLoadouts' production column uses a different `inProduction`: only the bills for
        //   this row's RECIPE (QueuedFromBills over e.recipe), not every bill that somehow produces the
        //   weapon. With several recipes for the same weapon that is a real difference.
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

        /// <summary>Total mass (kg) and bulk of a loadout's contents. Generic "Any ..." entries use CE's own
        /// average mass/bulk for that category, so nothing has to be resolved to a concrete item.</summary>
        public static void GetLoadoutMassBulk(Loadout lo, out float mass, out float bulk)
        {
            mass = 0f;
            bulk = 0f;
            // Fetch Slots ONCE: CE's Loadout.Slots is not a field but a getter that, for a loadout with
            // parents, builds `_slots.Concat(ParentSlots).ToList()` every time - and every colonist assigned
            // by this mod hangs off a personal anchor, so always has parents. Reading it twice meant two full
            // rebuilds of the union per call, and this function runs per colonist row per frame.
            SlotsMassBulk(lo?.Slots, out mass, out bulk);
        }

        // The same calculation for a loose slot list. Needed since the overload warning also has to weigh a
        // template that doesn't exist yet - deliberately a shared function, so the warning and the bar can't
        // answer with two different formulas.
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

        // Extracted (production costs were empty everywhere): the same calculation for a slot list WITHOUT
        // a Loadout object - the weapon rows of the picker windows compute their costs from the spec the
        // template is created from on selection.
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

        // AssignedWeaponCounts (Assigned column) briefly lived here - added and removed again the same day
        // (the column turned out not to be needed).

        /// <summary>A single ThingDef's own Bulk stat (0 if CE's "Bulk" StatDef isn't found, or the def has
        /// none). Used to cap what can be picked as a sidearm - a full-size battle rifle shouldn't be
        /// handed out as someone's "backup" weapon just because it scored well.</summary>
        // Memoized: depends only on immutable def data, but the solver's sidearm picker checks it PER
        // CANDIDATE - and that reruns completely every 150 ms while sliders are dragged.
        // GetStatValueAbstract walks the def's whole stat chain each time.
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
        // Same red as Dialog_AssignLoadouts.OverCapBarTex - an overloaded bar looks the same everywhere,
        // whichever window it is in.
        private static readonly Texture2D overCapBarTex = SolidColorMaterials.NewSolidColorTexture(new Color(0.75f, 0.18f, 0.18f));

        /// <summary>
        /// Label drawn ON TOP of a bar, with its own contrast. The numbers used to get swallowed by the bar -
        /// for high melee values the bar is light yellow, for mass/bulk turquoise, and the current value of
        /// all things sits on the filled part, while the limit behind it stays readable on black.
        /// Shadow instead of a fixed color, because the fill color shifts with the value (red -> yellow ->
        /// green on the score bars): a dark offset under white text works on EVERY background, a fixed color
        /// would only be right for one half of the gradient.
        /// Expects Text.Font from the caller; sets Anchor itself and restores it.
        /// </summary>
        // "Which weapon templates could use better ammo NOW?"
        // The case: armor-piercing ammo gets researched, and all the existing templates are pinned in the
        // generate step - so they keep their old ammo, and nothing points it out. The pin does exactly what
        // it promises; staying silent is still wrong.
        //
        // The decision was to keep the pin and add a hint, and the button for replacing already exists
        // ("Mass-replace ammo"). So the only thing missing was something saying that there IS something to
        // replace.
        //
        // The question is deliberately simple and WITHOUT the generate step's sliders: what would
        // PickDefaultAmmo choose from this weapon's RESEARCHED variants, and is that already in the template?
        // That way the hint doesn't depend on whatever slider position the player happens to have - it
        // answers "there is something new", not "this is how it should look".
        // 5-second cache: the display reading this is permanently on screen, and the answer only changes
        // when a research project finishes.
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

        // The name as it belongs in a CELL: also in the vanilla menu, BUT only in the button display - the
        // selection list itself shows the full name.
        // The short prefix ("W| ", "R| ", "E| ") says exactly what the icon next to it already shows, and
        // costs three to four characters in a row - in the compact view only "W| R-6 ..." was left.
        // In the SELECTION WINDOW the full, saved name stays: there is no icon next to it, and that is where
        // people search for the name they gave it themselves.
        // Only a prefix at the START and at most two characters before the bar - a loadout called
        // "Sharpshooter | Winter" keeps its full name.
        // ================= THE name of a loadout, in ONE place =================
        //
        // Goal: one central function for the button name, so it can be changed in one place, ideally the
        // color too - instead of patching it at every call site. The color already has that
        // (LoadoutTintFor), the name gets it here.
        //
        // Three kinds of prefix that used to be handled separately in different places:
        //   "[Role] "  - INTERNAL marker of a role. Bookkeeping, never for the player. Always dropped.
        //   "R| "      - configurable DISPLAY prefix for roles (mod settings).
        //   "W| "/"E| "- configurable prefixes that really are IN the saved name of weapon templates and packs.
        //                Don't delete: IsGeneratedTemplate uses them as a fallback check in case a loadout
        //                is not in the list of self-generated ones (old save).
        //
        // That is why this function only changes the DISPLAY and never the data. Whether the prefixes are
        // still needed at all thus becomes a switch instead of a rework: set `ShowKindPrefix` to false and
        // they are gone everywhere - without touching a single saved loadout.
        //
        // markerShownElsewhere says whether something else at this spot already reveals the kind (color,
        // icon). In a cell that is the case - there the prefix is pure repetition. CE's own selection menu,
        // on the other hand, shows only text: drop the prefix there and a role can no longer be told apart
        // from a weapon template. That distinction is the whole reason for this parameter.
        // Strip a prefix stored in the saved name ("W| Kord", "E| Medic bag"). Only one to two characters
        // before the bar, so a loadout named "Sharpshooter | Winter" keeps its name. Deliberately does NOT
        // check the setting: this is about legacy data that has the prefix in its label - that has to look
        // clean even when the prefixes are turned off.
        public static string StripKindPrefix(string name)
        {
            if (string.IsNullOrEmpty(name)) return name;
            int bar = name.IndexOf('|');
            return bar > 0 && bar <= 2 && bar + 1 < name.Length ? name.Substring(bar + 1).TrimStart() : name;
        }

        // THE name of a loadout as the player sees it - the only place that decides it (one central function
        // for the button name instead of patching it at every call site).
        // Previously three places assembled their own name - RoleUtility.DisplayLabel,
        // EquipmentPackUtility.StripMarker and the cell version - and drifted apart with every change;
        // that is where "M16A4 +W| M..." came from.
        //
        // The hard markers ("[Role]", "[Pack]") are BOOKKEEPING and never shown; they stay in the saved
        // label so detection doesn't depend on the display. The short prefixes (W|, E|) are display only
        // and depend on the settings - off by default.
        //
        // markerShownElsewhere says whether something else at this spot already reveals the kind (color, icon).
        public static string LoadoutDisplayName(string rawLabel, bool markerShownElsewhere = false)
        {
            if (string.IsNullOrEmpty(rawLabel)) return rawLabel ?? "";
            string raw = rawLabel;
            CEReloadedSettings cfg = CEReloadedMod.settings;

            // ROLE: the marker is ALWAYS shown, even next to color and icon (roles carry the [Role] prefix
            // everywhere). Reason: with a role the colonist carries something different than with the
            // weapon template of the same name - that must not hinge on a shade of blue alone.
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

        // The name as it belongs in a CELL: an icon or a color sits next to it, so the prefix would be
        // repetition. Kept as its own name so the call sites stay readable.
        public static string CellLabel(Loadout lo) => LoadoutDisplayName(lo, markerShownElsewhere: true);

        // Truncate without sacrificing the parenthesis at the end.
        //
        // The trigger: the bill overview showed five rows stacked on top of each other, all reading
        // ".30-06 Springfie...". Widened twice, and afterwards it read ".30-06 Springfield (AP-...",
        // ".30-06 Springfield (Sab...", "5.56x45mm NATO (F...". So the column had become wider and the
        // problem was still the same - because normal truncation cuts from the right, and for ammo the
        // right side holds exactly the variant.
        //
        // That is the actual information: the base name repeats all the way down the column, AP vs FMJ vs
        // Sabot is what tells the rows apart. Truncating from the right throws away the distinguishing half
        // and keeps the redundant one. So the parenthesis stays whole and the base name gets truncated:
        // ".30-06 Spring... (Sabot)" instead of ".30-06 Springfield (Sab...".
        //
        // No space problem to compute away: if both fit together, the function changes nothing. If not even
        // the parenthesis alone fits sensibly (less than a third would remain for the name), it falls back
        // to ordinary truncation - a result like "... (Sabot)" without any name would be worse than a
        // truncated name.
        //
        // The parenthesis check comes BEFORE measuring, not after: this function runs per row per frame,
        // and Text.CalcSize is the expensive part. A name without a parenthesis thus goes straight into
        // GenText.Truncate, which has its own cache - exactly as before, with no overhead.
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

        // The two shades of blue the mod uses everywhere to tell a ROLE from a weapon template - first only in
        // CE's own loadout column (Patch_LoadoutTooltip), now also in our own cells, matching the vanilla
        // "current loadout" column outside. One definition so the three places cannot drift apart.
        public static readonly Color LoadoutTintRole = new Color(0.55f, 0.75f, 1f);
        public static readonly Color LoadoutTintWeapon = new Color(0.8f, 0.88f, 1f);

        // ONE answer to "is this loadout ours, and which shade of blue does it get".
        //
        // Background: as soon as the tint actually became visible (see TintedButton), it turned out to sit on
        // the wrong button - a vanilla loadout created in CE had a blue tint it should not have. The bug was
        // older than the fix; it was just invisible: the two views tinted EVERYTHING that had a loadout, while
        // CE's own column had long distinguished correctly.
        // The blue means "this mod made it" - a loadout built by hand in CE does not count, and if the color
        // no longer separates that, it says nothing at all.
        // Hence one place, with the same rule Patch_LoadoutTooltip uses for CE's column: null = not ours, no tint.
        public static Color? LoadoutTintFor(Loadout assigned)
        {
            if (assigned == null || assigned.defaultLoadout) return null;
            // An anchor is our own wrapper around the assignment - what hangs below it is the template.
            bool isAnchor = AmmoAssignmentUtility.IsAmmoLink(assigned);
            Loadout real = isAnchor ? AmmoAssignmentUtility.DisplayLoadout(assigned) : assigned;
            if (real == null || real.defaultLoadout) return null;
            if (RoleUtility.IsRoleLoadout(real)) return LoadoutTintRole;
            if (isAnchor || IsGeneratedTemplate(real)) return LoadoutTintWeapon;
            return null;
        }

        // ONE button that actually SHOWS a tint and a disabled state.
        //
        // Two problems, both confirmed with measured RGB values: the blue role/template distinction was
        // completely missing in both of our views (every button exactly 106,81,46 - the untinted default),
        // and disabled role cells could not be told apart from normal ones. Neither was missing code; it was
        // the order: we dutifully drew the tinted background and then called Widgets.ButtonText - whose
        // ButtonTextWorker calls DrawButtonGraphic(rect) and so paints the button graphic AGAIN on top at full
        // opacity. Verified in the decompiled source; the same order existed in three places in the Assign tab.
        // GUI.enabled = false cannot help either: DrawButtonGraphic draws via GUI.DrawTexture, which ignores
        // GUI.enabled - it only blocks the click. That is why "greyed out" was invisible even though the
        // click was already blocked.
        // Consequence: we own the background, ButtonText no longer draws one (drawBackground:false) and only
        // provides the click.
        public static bool TintedButton(Rect rect, Color? tint, bool disabled = false)
        {
            Texture2D atlas = Widgets.ButtonBGAtlas;
            if (!disabled && Mouse.IsOver(rect))
                atlas = Input.GetMouseButton(0) ? Widgets.ButtonBGAtlasClick : BtnMouseoverAtlas;
            Color prev = GUI.color;
            // Disabled = noticeably darker and flatter. Our own grey instead of GUI.enabled, because that
            // demonstrably draws nothing (see above) - and no strikethrough, which is reserved for the
            // delete preview.
            GUI.color = disabled ? new Color(0.45f, 0.45f, 0.45f) : (tint ?? Color.white);
            Widgets.DrawAtlas(rect, atlas);
            GUI.color = prev;
            if (disabled) return false;
            return Widgets.ButtonText(rect, "", drawBackground: false);
        }

        // RimWorld keeps the mouseover graphic in a PRIVATE field (Widgets.ButtonBGAtlasMouseover), so we load
        // the same texture ourselves - without it a tinted button feels dead because it no longer reacts on
        // hover. Falls back to the normal graphic if RimWorld ever renames the path: the hover feedback is
        // lost, but nothing breaks.
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

        // reserveRight = space at the right end of the bar that is already taken (the overload warning sign)
        // and therefore no longer counts toward the centered number. Previously the COMPACT assign view showed
        // "57.6/40.0 kg" with the "g" under the warning sign. The card view showed the same value in full - not
        // because it did it right, but because its bar is wider. Structural fix in the shared function instead
        // of in one of the three call sites: the number is centered in the space that actually belongs to it.
        // The two shades of the carry-capacity bars: what the body carries by itself in the usual turquoise;
        // what only a backpack, vest or other item makes possible one step darker. The lighter part always
        // comes first because it is the base.
        private static Texture2D carryBarTex, carryBoostTex, carryOverTex;
        private static Texture2D CarryBarTex => carryBarTex ?? (carryBarTex = SolidColorMaterials.NewSolidColorTexture(new Color(0f, 0.8f, 0.8f)));
        private static Texture2D CarryBoostTex => carryBoostTex ?? (carryBoostTex = SolidColorMaterials.NewSolidColorTexture(new Color(0f, 0.52f, 0.55f)));
        private static Texture2D CarryOverTex => carryOverTex ?? (carryOverTex = SolidColorMaterials.NewSolidColorTexture(new Color(0.75f, 0.18f, 0.18f)));

        /// <summary>Fill level of a carry-capacity bar: up to `naturalCap` in the base color, beyond that (only
        /// possible thanks to worn items) darker; overloaded all red. `naturalCap` &lt; 0 means "unknown", then
        /// the bar stays single-colored.</summary>
        public static void DrawCarryFillBar(Rect bar, float load, float cap, float naturalCap, bool over)
        {
            Widgets.FillableBar(bar, cap > 0f ? Mathf.Clamp01(load / cap) : 0f, over ? CarryOverTex : CarryBarTex, BaseContent.BlackTex, true);
            if (over || cap <= 0f || naturalCap < 0f || load <= naturalCap) return;
            // Exactly the geometry FillableBar itself uses (3px border), otherwise the second shade is misaligned.
            Rect inner = bar.ContractedBy(3f);
            float x0 = inner.x + inner.width * Mathf.Clamp01(naturalCap / cap);
            float x1 = inner.x + inner.width * Mathf.Clamp01(load / cap);
            if (x1 - x0 > 0.5f) GUI.DrawTexture(new Rect(x0, inner.y, x1 - x0, inner.height), CarryBoostTex);
        }

        /// <summary>The ONE material cell of the mod: a button with the material image inside. Returns true on click.</summary>
        // The material had a white border in the weapon bill overview but looked like a button in Equipment
        // Packs - two code paths for the same thing, so they were unified. The white border was a
        // Widgets.DrawBox around an invisible button - two re-implementations of the same cell that had
        // drifted apart. Now one function, both places call it.
        // THE TWO BILL TYPES AS ICONS: infinity sign and x1 as PNGs, replaceable when needed.
        // Textures/UI/Icon_BillUntil.png (lemniscate) and Icon_BillOnce.png (x1), white on transparent -
        // swappable without changing anything here. Loaded on first draw, then cached.
        private static Texture2D texBillUntil, texBillOnce;
        public static Texture2D TexBillUntil => texBillUntil ?? (texBillUntil = ContentFinder<Texture2D>.Get("UI/Icon_BillUntil", false));
        public static Texture2D TexBillOnce => texBillOnce ?? (texBillOnce = ContentFinder<Texture2D>.Get("UI/Icon_BillOnce", false));

        /// <summary>Is this a "make until X" bill (infinity) rather than a "make N" bill (x1)?</summary>
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
            // Shortened instead of clipped: the compact view showed "6.5/10" where "6.5/101.3" was meant -
            // RimWorld simply cuts off label text that is too long for its rect, WITHOUT an ellipsis. A clipped
            // number reads as a different number, which is worse than a visibly shortened one.
            // Try a smaller font first so the value shows in full if possible; only truncate if that is not
            // enough either.
            // ALWAYS the small font (the Equipment Packs window used to show mass/bulk in the large font,
            // everywhere else in the small one). Previously each bar inherited its caller's font and only went
            // smaller when the text would not fit otherwise - so it varied. A number in a bar is the same kind
            // of information everywhere and now looks the same everywhere.
            GameFont prevFont = Text.Font;
            Text.Font = GameFont.Tiny;
            string shown = label.Truncate(rect.width - 2f);
            Text.Anchor = TextAnchor.MiddleCenter;
            Color prev = GUI.color;
            // ONLY the white text, without backing and without shadow: the grey overlays in the bars are gone
            // completely. The dark pill that was added earlier for contrast on the filled part is explicitly
            // reverted with this.
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
        // CE's weight limit, not RimWorld's - and that is the whole point.
        // Vanilla StatDefOf.CarryingCapacity has a base value of 75, CE's CarryWeight has 40. But a pawn is
        // slowed down by CE: CompInventory (checked in the decompiled source) has
        // capacityWeight => GetStatValue(CE_StatDefOf.CarryWeight), and moveSpeedFactor is computed against
        // exactly that capacityWeight. The bar against the vanilla value therefore showed about half the fill:
        // a pawn CE already slows to a crawl read as comfortably under half.
        // Two signs that this was an oversight, not intent: the fallback constants in the same places
        // (35f / 33.2f) are CE's order of magnitude, not vanilla's 75, and the bulk bar right next to it has
        // long read CE's own CarryBulk. One axis was right, the other was not.
        // A helper instead of five hand-written copies, so the stat lookup stays in one place.
        public static float CarryWeightOf(Pawn p)
        {
            StatDef st = DefDatabase<StatDef>.GetNamed("CarryWeight", false);
            return st != null && p != null ? p.GetStatValue(st) : 0f;
        }

        // The three CE StatDefs are looked up ONCE instead of by their STRING per row per frame (found in a
        // performance pass). DefDatabase.GetNamed is a dictionary lookup with a string key - cheap on its own,
        // but these three sat in draw functions that run per visible row (DrawMassBulkBars, its horizontal
        // sibling next to it, and both colonist views). Defs are immutable at runtime, so a one-time
        // resolution cannot go stale; `resolved` instead of a null test so a stat that is genuinely missing
        // from this mod list is not searched for again on every call.
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

        // ===== Carry-capacity boosters on the colonist-specific mass/bulk bars =====
        // CE's carry capacity comes from two stats (CarryWeight for mass, CarryBulk for bulk), and apparel raises
        // them via equippedStatOffsets (Apparel_Carrying.xml: backpack/vest +CarryBulk, exoframe +CarryWeight -
        // normal clothing gives nothing). ONE function for all bars attached to a concrete colonist: inside the
        // bar on the right, the icon of the item that raises this limit, with '...' for the first of several;
        // the tooltip lists them all, with base value and remainder.
        // Bars against the median colonist (templates, packs) do NOT get this - there is no wearer whose
        // apparel could be shown.
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
                float fac = 1f;   // Apparel only has equippedStatOffsets, no factors (RimWorld 1.6)
                if (Mathf.Abs(off) > 0.001f || Mathf.Abs(fac - 1f) > 0.001f)
                    list.Add(new CarryBooster { apparel = a, offset = off, factor = fac });
            }
            return list;
        }

        // Draws the icon on the right INSIDE the bar and returns how much width it occupies on the right (0 if
        // nothing is worn) - the caller reserves that for the label. `rightReserve` is what is already taken
        // on the right (the overload warning sign); the icon moves in front of it.
        public static float DrawCarryBoosterIcon(Rect bar, List<CarryBooster> boosters, float rightReserve)
        {
            if (boosters == null || boosters.Count == 0) return 0f;
            float size = Mathf.Clamp(bar.height - 2f, 12f, 18f);
            Rect icon = new Rect(bar.xMax - rightReserve - size - 2f, bar.y + (bar.height - size) / 2f, size, size);
            Widgets.ThingIcon(icon, boosters[0].apparel);
            if (boosters.Count > 1)
            {
                // Three dots as small boxes instead of text: of the text '...' only a single bright dot at the
                // cell edge was visible - RimWorld's Label clips it in the 14-point rect.
                // At the bottom INSIDE the icon, centered, on a dark backing so they show on any item image.
                float d = Mathf.Max(2f, Mathf.Round(size / 7f));
                float totalW = 5f * d;
                float x0 = icon.x + Mathf.Round((icon.width - totalW) / 2f), y0 = icon.yMax - d - 1f;
                Widgets.DrawBoxSolid(new Rect(x0 - 1f, y0 - 1f, totalW + 2f, d + 2f), new Color(0f, 0f, 0f, 0.75f));
                for (int i = 0; i < 3; i++) Widgets.DrawBoxSolid(new Rect(x0 + i * 2f * d, y0, d, d), Color.white);
            }
            return size + 4f;
        }

        // The bar's tooltip: the limit with its sources (base value, each item, remainder) and the current load.
        // Replaces the old one-word tooltips ('Mass' / 'Bulk') that said nothing.
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

        // "Bulk" is already resolved in the handling-stats block further down (ResolveHandlingStats).
        // Deliberately NO second field for it: two resolutions of the same stat are exactly the kind of
        // double bookkeeping that drifts apart later.
        public static StatDef BulkStat { get { ResolveHandlingStats(); return statBulk; } }

        // The two medians depend only on immutable def data (ThingDefOf.Human) - computed once, they hold for
        // the whole game process. Previously every drawn bar row called GetStatValueAbstract twice, which
        // walks the def's whole stat chain internally.
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

            // Red + warning sign + BarLabel, identical to the horizontal sibling below (the role preview showed
            // bulk 31.0/20.0, i.e. 155 %, and the bar stayed turquoise as if all were fine). Both corrections had
            // been made ONE function further down and not here - the classic mistake: first occurrence fixed,
            // the second occurrence of the same pattern right next to it left as is.
            // This version draws the same values for pack ingredients, the weapon template and the role
            // preview - all three are "what I am building right now", so the same statement applies: a
            // warning against the MEDIAN colonist, not a fact about a specific person.
            void Bar(Rect labelR, string caption, Rect barR, float value, float cap, string valueText)
            {
                // LabelCell instead of Widgets.Label: keeps the caption structurally single-line and shortens it
                // visibly if the cap below does kick in - previously it was simply clipped (German "Sperri").
                LabelCell(labelR, caption);
                bool over = cap > 0f && value > cap;
                Widgets.FillableBar(barR, Mathf.Clamp01(value / cap), over ? overCapBarTex : massBulkBarTex, BaseContent.BlackTex, true);
                BarLabel(barR, valueText, over ? 16f : 0f);
                if (over) DrawWarningBadge(new Rect(barR.xMax - 16f, barR.y - 3f, 15f, 15f), ColBad);
            }

            // Caption width is measured instead of a fixed 40f (German "Sperrigkeit" needs ~62px and was
            // clipped to "Sperri"). Both rows share ONE width so the bars line up under each other; the cap
            // at 40% of the total width protects the bar.
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
        // Label cache for the two bars (measured: the "row cost+mass" section was the most expensive in the
        // pack wizard at 37 ms/s, with 2000 calls/s). Two Translate calls plus two F1 formats per row per pass
        // are pure string work for a text that only changes when the number changes. The key is the value
        // pair rounded to tenths - exactly what the label shows. The cache is cleared when it grows
        // (language change, new values) so it never becomes a memory trap.
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

            // Red + warning sign as soon as it no longer fits (mass 55.0/40.0 and bulk 55.8/20.0 showed both bars
            // completely full and turquoise - 138 % and 279 % over the limit looked like "all fine"). The cause
            // was the Clamp01: above 100 % every excess looks identical. On the colonist card DrawCapacityBar has
            // long done it right; this bar - visually the same, different function - never got it.
            // Why it matters: exactly such a loadout was later handed out by auto-assign to a colonist who
            // cannot carry it. Red WHILE BUILDING shows the problem one step earlier.
            // The reference here is deliberately the MEDIAN colonist and not a concrete pawn - a loadout does
            // not belong to anyone yet. So it is a warning ("does not fit the average"), not a fact about a
            // person.
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
        // Back to the real PNG (Icon_Paste.png). The reason for the drawn version was that the file was not
        // under version control at the time, so an accidental edit had nothing to fall back to - it is now
        // versioned, so that objection is gone. The square centering stays: pasteRect is 20 wide and as tall
        // as the whole cell, a stretched image would be a vertical bar.
        // If loading fails (file missing/broken), the old geometry is still drawn - an empty rectangle on a
        // button would be worse than a plain symbol.
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

        // The research project(s) this def still lacks - for the RECIPE and for the required WORKBENCH.
        // Empty string if everything is researched.
        //
        // The workbench part is the actual point (found in a new-player playthrough): previously only the
        // RECIPE's research was checked, never whether the bench for it could be built yet. In the first days
        // of a game this produced a screen where every ammo row was locked and "Confirm & supply" answered
        // "nothing selected" - and nowhere did it say that Machining was needed first. The BENCH was named,
        // the required research project never; not a single one was named anywhere in the mod. That is the
        // classic moment a mod gets uninstalled again.
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
            // And the benches: ONE researched bench is enough, so only report when NONE of them is available
            // yet - otherwise it would say "needs research X" although building is long possible.
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

        // Things lying in FOG count as stock NOWHERE (ghost items: a persona monosword with availability 1
        // and "!" that could be found neither on the map nor via RimWorld's search - in a fresh colony, so
        // with lots of fog still left).
        // listerThings also lists what lies in still unrevealed areas (Ancient Danger, buried ruins, Odyssey
        // structures), and such loot is always "do not pick up" on top - hence the "!".
        // RimWorld's own selection and search hide exactly that (ThingSelectionUtility.SelectableByMapClick
        // -> Position.Fogged). Stock the player can neither see nor fetch is no stock; and the "!" hint
        // "release it first" would be wrong there, because there is nothing to release.
        // Applies to EVERY listerThings scan in the mod (all converted together; grep `listerThings.` finds
        // them). Carried items are never spawned and drop out here via !Spawned.
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

        // BuildForbiddenLocationLines was removed without replacement, together with the tooltip section it
        // alone filled (see BuildLocationTooltip above: once is enough). Its last caller was exactly that
        // section; after it was removed, the function had no caller left in the mod. Removed rather than left
        // behind, because leftovers like that later look like a forgotten fix.
        // The keyed strings CER_LoadoutUtility_LocationTooltipForbidden and CER_LoadoutUtility_ForbiddenOnGround
        // stay in all 13 languages: reverting this is then one function, not 13 files.

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
            // No text any more (the exclamation glyph rendered unpredictably and appears in many places): the
            // badge scales with the host (DrawForbiddenMark clamps s to 10..14), a font glyph does not -
            // GameFont.Tiny has a fixed line box height. An earlier MiddleCenter fix removed the overhang but
            // not the cause: the stroke width still depended on the font and so was uneven across sizes. Now
            // the same drawn geometry as in the warning badge, so it is proportionally identical at every
            // badge size - and both exclamation marks in the mod finally look the same.
            // The dark plate stays: the marker sits on bar segments of any color, and a bare orange glyph on
            // the beige/yellow segments was unreadable.
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
            // Fallback for old saves: loadouts created BEFORE managedLoadoutIds (2026-08-01) only have their
            // prefix in the label. This later stopped consulting the prefix setting - it now defaults to "no
            // prefix", and then the fallback would be dead for exactly the saves it exists for.
            // Fallback REMOVED: old data is backfilled once on load (CEReloadedGameComponent.AdoptMarkersOnce),
            // after which nothing asks the label any more.
            return false;
        }

        // ============ Disabled weapons ============
        // Save-game state in the GameComponent (defNames); the automation (candidate building) avoids disabled
        // weapons, the picker shows them greyed out at the end of the group with an enable toggle.
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

        // ---- Disabled ROLES and LOADOUTS (with all implications) ------------------------------------------
        // Same design as above, just keyed by UniqueID instead of defName. Disabling here disables the
        // object - renaming changes nothing, and a deleted loadout takes its entry with it
        // (LoadoutDeletionUtility.ForgetSideBooks), because CE reuses its IDs.
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

        // How many colonists are, by ASSIGNMENT, attached to something disabled? Used for the reassign prompt
        // when the picker closes. Three cases, one counter:
        //   - a disabled WEAPON anywhere in the slot union (primary, sidearm, role contents),
        //   - the assigned template ITSELF is disabled (also through the personal anchor - hence
        //     DisplayLoadout, otherwise every personalized colonist would only show the anchor),
        //   - a disabled template hangs as an extra parent on the anchor (pack, sidearm, role).
        // The name stays so the call sites did not all have to be touched; it now means "disabled ANYTHING".
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

        // "Which pack belongs to this generator plan" - via a fixed ID instead of the name text, so the
        // match does not depend on a string.
        // The name stays as a SECOND tier, and that is intentional, not half-hearted: packs created before
        // this tracking have no entry, and they should still be found instead of being created a second
        // time. New packs get their entry on creation; from then on the mapping survives any rename.
        public static Loadout FindGeneratedPackForPlan(string planName, string fullLabel)
        {
            CEReloadedGameComponent comp = CEReloadedGameComponent.Cached;
            if (comp != null && !string.IsNullOrEmpty(planName) && comp.packPlanIds.TryGetValue(planName, out int id))
            {
                Loadout byId = LoadoutManager.Loadouts.FirstOrDefault(l => l != null && !l.defaultLoadout && l.UniqueID == id);
                // Gone (deleted) -> clean up the entry and fall back to the name search, otherwise a dead ID
                // stays forever and blocks re-creation.
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
        // CACHED (it showed up as the single most expensive item in a measurement, then confirmed against
        // RimWorld's ListerBuildings): `allBuildingsColonist` contains EVERY player building - walls, doors,
        // conduits, furniture. In a grown colony that is thousands of entries, and `OfType` walks all of
        // them to fish out a handful of workbenches.
        // Why it hurt: the 31 call sites almost all hang off producibility checks (IsProducible,
        // BenchExistsFor, ProducibleNow), and those run per item per row per frame - with twelve visible
        // colonists with eight requirements each that was several hundred full scans PER FRAME, each with
        // two iterator allocations.
        // Frame cache, no TTL: a workbench can neither appear nor vanish within a frame, and Time.frameCount
        // is the same key the neighbouring caches here already use. As a LIST, not an iterator - that
        // allocation is exactly what should go away.
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

        // How many of them the pawn ACTUALLY carries - the quantity version of the two yes/no questions above.
        // The plain yes/no answer let a colonist with 1 of 5 required medicine count as fully equipped: the
        // display was green, in combat it ran out after the first injury, and because it counted as fulfilled
        // no resupply was planned either - so the bug hid itself. stackCount, not the number of stacks: five
        // medicine sit in the inventory as ONE stack, "is there an entry" would have given the same wrong answer.
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

        // Ammo IN THE MAGAZINE counts as carried ("Fetch gear now" skipped two colonists although everything
        // was green: one had 144 rounds in the inventory and 6 in the rifle, the loadout says 150 - CE saw
        // nothing to fetch, our cells and the preview said "still to fetch: 6"). CE's GetStorageByThingDef adds
        // CurMagCount of the primary AND of every weapon in the inventory to the ammo; every count here that is
        // compared against CE's fetching must do the same - otherwise the mod shows a shortfall CE will never fetch.
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

            // Actual stock (the tooltip shows how much ammo exists at all). Deliberately split instead of one
            // sum - "300" is worthless if 280 of it is already being carried. Exactly the split GetAmmoOwnership
            // already provides and every availability display in the mod already uses.
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
            // ONLY when the workbench is currently SELECTED (Arm my Colony used to pop open the Electric
            // Smelter's bills tab on the left of the screen, with the DEV button). This function used to select
            // the bench itself to force RimWorld's bill list to redraw - and every bill cleanup (PruneBillsFor,
            // which runs over ALL benches in the Arm my Colony reset) thereby opened the inspector on the last
            // touched bench although nobody had clicked it. The 'DEV' button is RimWorld's own dev-mode tab in
            // the inspector, not part of this mod. If the bench is not selected there is no list that could go
            // stale - then nothing happens here.
            if (Find.Selector == null || !Find.Selector.IsSelected(bench)) return;
            var inspectPane = MainButtonDefOf.Inspect.TabWindow as MainTabWindow_Inspect;
            if (inspectPane != null && inspectPane.OpenTabType == typeof(RimWorld.ITab_Bills))
            {
                inspectPane.CloseOpenTab();
                inspectPane.OpenTabType = typeof(RimWorld.ITab_Bills);
            }
        }

        // EVERY jump to a workbench also opens its bill list ("Jump to this workbench" should open the bills,
        // not just select the bench). Jumping to a bench from a bills window means wanting to see the bill
        // there - only selecting the bench cost a second click on the tab every time. All jump buttons
        // therefore call this one function.
        public static void JumpToBench(Building_WorkTable bench) => OpenBillsTab(bench);

        // EVERY jump to a workbench also opens its bill list ("Jump to this workbench" should open the bills,
        // not just select the bench). Jumping to a bench from a bills window means wanting to see the bill
        // there - only selecting the bench cost a second click on the tab every time. All jump buttons
        // therefore call this one function.
        public static void OpenBillsTab(Building_WorkTable bench)
        {
            if (bench == null || bench.Destroyed) return;

            // ====== Get out of the way first, then jump ======
            //
            // Previously the camera jumped correctly to the wooden loading bench, the bench was selected and
            // its bill list opened bottom left - BUT the Loadout Planner stayed open on top. Since it swallows
            // clicks around itself, the bill list was dead.
            //
            // So the jump did exactly what it should and was still useless. Our large windows set
            // absorbInputAroundWindow (otherwise operating them would accidentally click into the world) - and
            // the same property makes the just-opened bill list unusable.
            //
            // Choosing "jump to this workbench" means wanting to go there. So we first close what is in the
            // way - and exactly that: our own windows (by namespace, not a type list, so the next dialog is
            // not forgotten - the same check as in Patch_VanillaTabHider), and of those only the ones that
            // actually intercept clicks. A window that does not block may stay open.
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
        // Cap for self-contained ammo/throwable explosives - see the comment at the call site below.
        // Max(1, ...): even the bulkiest throwable starts at one, otherwise the row would be empty.
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
            // CAPPED by carry capacity: CE's pila weigh 4.25 bulk / 2.0 kg each - the bare self-ammo default
            // of 10 came to 42.5 bulk against ~20-35 capacity, i.e. a colonist who is already FULL with the
            // weapon alone and cannot pick up anything else. The setting stays the UPPER LIMIT; the cap is 75%
            // of median carry capacity (bulk AND mass, the tighter one wins) divided by the per-item weight -
            // for pila that means 3 instead of 10, for light throwables nothing changes (satchel charge:
            // cap 6, setting 4 -> stays 4). ONE calculation here, because every consumer (wizard, ammo
            // window, auto-assign budget) goes through this function.
            if (IsThrownExplosive(weaponDef)) return CappedThrowableDefault(weaponDef, CEReloadedMod.settings.defAmmoGrenade);
            if (IsSelfAmmoWeapon(weaponDef)) return CappedThrowableDefault(weaponDef, CEReloadedMod.settings.defAmmoSelfAmmo);

            if (weaponDef.weaponTags != null)
            {
                // Pistols BEFORE the other branches: they had no branch at all and fell back to the generic
                // 150 rounds - the same amount as an assault rifle, on top of the primary weapon's ammo,
                // since a pistol is almost always a sidearm. Exactly the weight that pushes loadouts into
                // the overload warning.
                //
                // "SimpleGun" is deliberately NOT used here. It is a VANILLA tag for simple firearms in
                // general, not a pistol tag: it is also on bolt-action and lever-action rifles. Measured on
                // a test save it hit eight real PRIMARY weapons - M1917 Enfield, Winchester 1886, MosqueFAL,
                // Mosin-Nagant, SKS, Henry Side Gate, Henry Big Boy, Winchester 94 - which then got 24
                // instead of ~150 rounds. A primary weapon with a pistol's ammo count is the costlier of the
                // two mistakes: too much ammo costs carry weight, too little costs the fight.
                // Pistols without a CE tag now fall through to the name check further down
                // ("pistol"/"revolver"/"derringer"), and if that doesn't match either, to the default -
                // i.e. exactly the behavior before the pistol fix, instead of breaking eight rifles.
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
            // Also ahead of everything else - otherwise the "rifle" fallback at the very bottom swallows every
            // modded weapon with "rifle" in its name, including things like "rifle pistol". Deliberately narrow
            // terms: the cost of a false match here is too little ammo for a primary weapon.
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

        // The score depends on the mod settings via carry weight (ammo amounts per weapon class). Without
        // clearing, every weapon already scored kept its old value until the game restarted - raising the
        // assault rifle amount from 150 to 400 would not move the ranking at all.
        // Called by the mod settings when they close.
        // A counter rather than plain clearing: anyone caching a DERIVED value from the scores (e.g. the
        // maximum for the bar scale in Dialog_GenerateLoadouts) needs a signal that the value is stale.
        // Without it every such caller would either have to recompute every frame or silently go stale -
        // and both had already happened here.
        public static int WeaponScoreVersion { get; private set; }

        public static void ClearWeaponScoreCache()
        {
            weaponScores.Clear();
            // The target scores must be cleared too. Via CarryMult they depend on the same settings
            // (ammo amounts per weapon class) as the score above, but were never reset here: after a
            // settings change one half computed with the new values while the other served the old ones.
            // It showed up in the "research is holding this weapon back" line - which compares exactly
            // these two halves and then appeared for weapons whose ammo had long been researched.
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
            // Detection goes by the internal defName, NOT the label: CE ships translated labels per
            // language, so in German or Chinese "(ap" never appeared here - the armor-piercing bonus and
            // its icon silently dropped out in every language except English. The defName is
            // language-independent.
            // The label stays as a second chance: modded ammo that only carries the type in its name is
            // still recognized in English - so nothing gets worse than before.
            // ====== Ask CE's own data FIRST ======
            // Switching from label to defName only removed HALF of the language dependency, and that is
            // counted, not estimated: of 370 types CE names as armor-piercing, the defName rule catches
            // 222 - the remaining 148 are named "..._Incendiary" (label "(AP-I)") and "..._HE" (label
            // "(AP-HE)") and still depended solely on the English label. In German their bonus and icon
            // dropped out - exactly the bug that was already considered fixed once.
            // Simply including those suffixes would be wrong: there are "..._HE" and "..._Incendiary" types
            // that are NOT AP ammo (artillery shell "(Incendiary)", cannon shell "(HE)", launcher grenade
            // "(HE)") - a name rule cannot tell the two cases apart.
            // CE itself can: every AmmoDef carries an <ammoClass>, and that is a DEF reference, so the same
            // string in every language. In the installed CE all 370 carry one of the class names
            // ArmorPiercing / Sabot / IncendiaryAP / ExplosiveAP / CompositeRigidAP / RailgunSabot /
            // ChargedAP, and NONE of the other 539 types matches the rule below (pitfalls checked:
            // "Grapeshot" and "Catapult_Boulder" contain a lowercase "ap", "GrenadeHEDP" ends in DP -
            // hence case-sensitive, and checking for the SUFFIX "AP" rather than any occurrence).
            // The old rules stay as a fallback for ammo from mods that don't set ammoClass.
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
        /// intrinsic effect icon counts 1, so weapons sort 3 -> 2 -> 1 -> 0). Only INTRINSIC effects appear in that column
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
            // Strip the kind prefix regardless of the setting: an older loadout's label has it even if
            // prefixes are no longer assigned. Otherwise this detection would miss the primary weapon in
            // exactly the existing loadouts and fall back to the score estimate - which is the wrong
            // answer for a melee fighter with a firearm sidearm.
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

        // The same pick from a loose def list instead of a finished loadout. Needed since the role card
        // marks the primary weapon: there the question "which of these weapons becomes the primary" has to
        // be answered BEFORE the role loadout even exists.
        // Deliberately a shared function rather than a second copy of the rule - this pick already lives
        // in two places in the mod (here and Dialog_AssignLoadouts.AnalyzeLoadout) and they drift apart
        // exactly when a third one is added.
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

        // How many of the five CE data sources this weapon is missing. Public because the UI needs the same
        // result for its warning badge - badge and score penalty must come from the same number, otherwise
        // one does not explain the other. Only meaningful for ranged weapons: a melee weapon has no
        // magazine, AmmoSet or recoil, so "missing" there is not a flaw but normal.
        private static readonly Dictionary<ThingDef, int> missingCeDataCache = new Dictionary<ThingDef, int>();

        public static int CountMissingCeData(ThingDef w)
        {
            // CE's OWN utility items are not "unpatched": such a warning does not belong on CE's own items
            // like the binoculars.
            // The binoculars are a marking tool (verb MarkForArtillery) - no magazine, no ammo and no recoil
            // are intentional there, not a missing CE patch. This count asks "is CE data missing", and for
            // utility items the answer is no - which also drops the score penalty that hangs off the same
            // number (see GetWeaponScore).
            if (w == null || !w.IsRangedWeapon || IsUtilityWeapon(w)) return 0;
            if (missingCeDataCache.TryGetValue(w, out int cached)) return cached;
            int missing = 0;
            // Thrown and self-ammo weapons (grenade, molotov, spear, satchel) are EXEMPT from two of the
            // five checks: by design they have neither a magazine nor an AmmoSet. The penalty exists for
            // weapons whose WEAKNESSES are unknown ("missing CE data reads as 'no drawback'") - but for a
            // grenade nothing is missing, there is simply nothing to know. Measured: CE's own frag grenade
            // reported "2 of 5 sources missing" and got a 0.82 factor, i.e. an 18% penalty for a weapon CE
            // patches itself. That is the opposite of what the penalty is for - it should push unpatched
            // modded weapons below patched ones, not punish whole weapon classes.
            bool noMagByDesign = IsSelfAmmoWeapon(w) || IsThrownExplosive(w);
            // 1) Recoil/aiming time - without it the full burst bonus applies.
            if (!(w.Verbs != null && w.Verbs.Any(v => v is CombatExtended.VerbPropertiesCE))) missing++;
            // 2) Magazine + reload time - if both are missing, the reload pause drops out of the cycle time.
            if (!noMagByDesign && w.GetCompProperties<CompProperties_AmmoUser>() == null) missing++;
            // 3) CE's handling stats - without them accuracy stays at a favorable default.
            ResolveHandlingStats();
            if (statShotSpread != null && !w.statBases.NullOrEmpty()
                && !w.statBases.Any(sb => sb.stat == statShotSpread || sb.stat == statSightsEfficiency || sb.stat == statSwayFactor)) missing++;
            else if (w.statBases.NullOrEmpty()) missing++;
            // 4) Bulk - without it the bulk penalty never applies.
            if (statBulk != null && (w.statBases.NullOrEmpty() || !w.statBases.Any(sb => sb.stat == statBulk))) missing++;
            // 5) AmmoSet - without it the carried ammo weighs nothing. Exempt for thrown/self-ammo
            //    weapons: there the weapon IS the ammo, so by design there is no AmmoSet.
            if (!noMagByDesign && GetAmmoSet(w) == null) missing++;
            missingCeDataCache[w] = missing;
            return missing;
        }

        // The visible marker for the penalty above: shown like the ammo icon for status effects, as a
        // badge in the top-right corner of the weapon icon. It deliberately reuses the existing warning
        // square instead of a new image - the same visual language as "no workbench", and it says the
        // same thing: something is wrong with the data here.
        // It takes its number from the same function as the score penalty, so badge and tooltip can never
        // drift apart.
        // Magazine size of a weapon, or 0 if it has none (bow, thrown weapon, melee).
        public static int MagazineSizeOf(ThingDef weapon)
        {
            CompProperties_AmmoUser au = weapon?.GetCompProperties<CompProperties_AmmoUser>();
            return au != null && au.magazineSize > 0 ? au.magazineSize : 0;
        }

        // "= 3 Mag" as an addition to a round count (both rounds and magazines).
        // Deliberately an ADDITION rather than a replacement: the exact round count is what gets typed
        // in and goes into the bill, while "is this enough for a fight" can only be answered in
        // magazines. Empty string if the weapon has no magazine or the count is below one magazine -
        // "= 0 Mag" would be noise, not information.
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
                // WEAKER of the two AP values, so pure swords (blunt~0) and pure hammers (sharp~0) are untouched and
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
                    // stays FALSE here on purpose: this is the DISPLAYED score, and it is meant to
                    // answer "how good is this weapon in principle", regardless of research state.
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

            // Missing CE data must not pass as a strength.
            // Of the five CE data sources, four FLATTER the weapon when missing: without VerbPropertiesCE
            // there is no recoil and thus the full burst bonus; without CompProperties_AmmoUser the reload
            // time drops out of the cycle time entirely; without CE handling stats accuracy stays at the
            // favorable default; without bulk there is no bulk penalty; without an AmmoSet the ammo weighs
            // nothing. Only a missing ProjectilePropertiesCE penalizes (AP 0). On balance, a modded weapon
            // without CE patches looked BETTER than a properly balanced CE weapon - not because it is
            // better, but because nothing is known about its weaknesses.
            // Deliberately ONE explainable penalty per missing source instead of invented substitute
            // values: guessing a plausible recoil would produce a number nobody can verify, while "we know
            // nothing about four of five properties, so we trust the result less" is stated in the tooltip
            // and true. The ordering AMONG weapons that share the same gap is preserved - they slide below
            // the patched ones together.
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

        // ====== The same calculation for AMMO TYPES, just without the Mathf.Max ======
        //
        // Reason: HP ammo is stronger against flesh than FMJ, yet with the slider all the way left HP was
        // never suggested - and that has a measurable cause, not a matter of taste. In CE
        // `armorPenetrationBlunt` is a property of the CARTRIDGE, not the projectile: 7.62x39mm has
        // exactly 41.08 for FMJ, AP, HP, AP-I, AP-HE and Sabot, while `armorPenetrationSharp` at
        // 5.5 / 11 / 3 / 11 / 5.5 / 19.25 is exactly what distinguishes the types (checked in CE's
        // Defs/Ammo/Rifle/762x39mmSoviet.xml).
        // With Mathf.Max the blunt value wins for every rifle cartridge - 41.08 against a Scyther's armor
        // of 6 gives 0.85, and the sharp share is always below that. Result: FMJ and HP got exactly the
        // same affinity, and "first with the highest value wins" let def order decide - FMJ comes before
        // HP in CE's XML. That is exactly the observed behavior.
        //
        // Instead of Max, an OR: the hit gets through if it beats the sharp OR the blunt armor.
        // 1-(1-a)(1-b) is monotonic in BOTH axes, so the sharp difference stays visible even when the
        // blunt value is large.
        // Cross-check against the case the blunt axis was originally added for (12 gauge: buckshot
        // 4/4.52, slug 6/85.2): old buck -1.00 / slug +0.40, new buck -1.00 / slug +0.43. Where one axis
        // dominates, practically nothing changes - the difference only arises where both contribute, and
        // that is exactly the case that used to be flat.
        // Deliberately ONLY for ammo: weapon scoring (GetWeaponTargetScores) keeps using MechRetention
        // unchanged, so this correction does not shift any weapon ranking.
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
            // Drop both bill caches immediately (together with the cache in WeaponQueuedCount). Both
            // callers of AddBill go through here, so this is the single choke point for "a bill was
            // added". This direction is what matters: the "is production still missing?" check sometimes
            // runs right after the bills are created, and one-second-old numbers there would claim nothing
            // was created - the restock hint would pop up again. The opposite direction (bill DELETED,
            // count briefly too high) deliberately stays with the max age: it has eight deletion sites, is
            // harmless, and heals itself after a second.
            weaponQueuedCache.Clear();
            activeBillCache.Clear();
        }

        // Forgets every remembered bill ID that no longer has a bill (a memory leak). managedBillIds used to
        // grow with EVERY bill ever created and never shrank - not even when the bill had long been
        // deleted. It is also saved in the game file, so it grew permanently: using the restock wizard
        // regularly over a long game accumulated thousands of dead strings.
        // The direction is safe: a wrongly forgotten ID turns a mod bill into a "manually created" one,
        // and those are NOT touched during cleanup - so when in doubt we delete less, never more.
        // Runs on the cleanup tick (once per in-game hour), not on every access.
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

        /// <summary>Does EVERY active bill for this thing come from our own wizard?
        /// (Everything is suggested, but MANUALLY created bills are unchecked by default (opt-in), while
        /// bills created by the mod are opt-out.)
        /// Deliberately ALL rather than ANY: the deletion pass (PruneBillsFor) removes every bill for this
        /// def, not just ours. If a thing has both one of our bills AND a hand-placed one, a pre-checked box
        /// would take the hand-placed bill with it - exactly what this rule is meant to prevent. In that
        /// case the row counts as hand-placed and is opt-in.
        /// No active bill: false - then there is nothing to pre-check.</summary>
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
        // `markManaged`: single bills added by hand from the bill tab used to count as "wizard generated".
        // The mark meant "created by this mod" - which also covered a deliberate single click on a
        // workbench. For the Arm my Colony reset, though, the INTENT matters, not the window: what the
        // wizard created itself it may clean up again; what was clicked individually counts like a bill
        // set by hand at the workbench and stays.
        public static BillChange AddOrUpdateAmmoBill(Building_WorkTable bench, RecipeDef recipe, int targetCount, bool onlyIncrease, bool markManaged = true)
        {
            // SAFETY NET: never attach a bill to a bench that doesn't offer the recipe. RimWorld's
            // billStack.AddBill does NOT check this - it accepts anything, and the error only hits the
            // colonist later (InvalidCastException in MakeUnfinishedThingIfNeeded, endless loop in the
            // log). The callers now pick the right bench; this guard ensures a future caller cannot make
            // the same mistake again. Loud rather than silent: a swallowed bill would be a mystery of its
            // own.
            if (!BenchCanRun(bench, recipe))
            {
                Log.Warning("[CE Reloaded] Bill skipped: " + (recipe?.defName ?? "?") + " cannot run at "
                    + (bench?.def?.defName ?? "?") + " - workbench skipped instead of throwing.");
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
        // RAISE an existing "do N times" bill instead of adding a second one next to it: every click on
        // "+1 workbench" used to add a new bill - correct in substance, but cluttered. Adding the amount to
        // the existing bill is better, the same behavior as with "until". Same recipe, same bench, and -
        // where the recipe takes a material - a bill that allows THAT material; otherwise two clicks
        // would produce a bill with the wrong one.
        public static Bill_Production AddOrRaiseOnceBill(Building_WorkTable bench, RecipeDef recipe, int runs, ThingDef stuff, bool markManaged = true)
        {
            // Dritte Anlegestelle, gleicher Riegel (Regel-7-Grep 2026-09-04).
            if (!BenchCanRun(bench, recipe))
            {
                Log.Warning("[CE Reloaded] Bill skipped: " + (recipe?.defName ?? "?") + " cannot run at "
                    + (bench?.def?.defName ?? "?") + " - workbench skipped instead of throwing.");
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
            // Same safety net as in AddOrUpdateAmmoBill - see there. This is exactly the path through which
            // a satchel charge bill ended up on the ammo bench.
            if (!BenchCanRun(bench, recipe))
            {
                Log.Warning("[CE Reloaded] Bill skipped: " + (recipe?.defName ?? "?") + " cannot run at "
                    + (bench?.def?.defName ?? "?") + " - workbench skipped instead of throwing.");
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

        /// <summary>Builds display labels for a set of benches, numbering duplicates of the same def ("Machining table 1", "...2").</summary>
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

        // ---- Confirmation dialogs -----------------------------------------------------------------------

        // RimWorld's Dialog_MessageBox has a FIXED size - `InitialSize => new Vector2(640f, 460f)`, taken
        // from the decompiled source, not guessed. For two lines of text that is about two thirds of the
        // window height for two lines and a button row, with empty space in between. The content itself,
        // however, computes everything from `inRect`: text from the top to `inRect.height - 35 - 5`,
        // buttons at `inRect.height - 35`. A smaller window therefore gives the same layout, just without
        // the gap - so overriding the height is enough, no need to rebuild the dialog.
        //
        // ONE place for ALL of the mod's confirmations (there are twelve), so the next one doesn't open at
        // the default size again. The signature deliberately matches Dialog_MessageBox.CreateConfirmation
        // character for character - call sites only change the name.
        public static Dialog_MessageBox CompactConfirmation(TaggedString text, Action confirmedAct,
            bool destructive = false, string title = null, Action cancelAct = null)
        {
            return new Dialog_ConfirmCompact(text, confirmedAct, destructive, title, cancelAct);
        }

        public class Dialog_ConfirmCompact : Dialog_MessageBox
        {
            public Dialog_ConfirmCompact(TaggedString text, Action confirmedAct, bool destructive, string title, Action cancelAct = null)
                // The same arguments CreateConfirmation itself passes (from the decompiled source):
                // Confirm/Go back, accept = the same action, cancel = empty action (so Escape closes
                // without doing anything). cancelAct: the Go back button may have its own target - the
                // reassignment prompt uses it to return to the weapon selection window.
                : base(text, "Confirm".Translate(), confirmedAct, "GoBack".Translate(), cancelAct, title,
                       destructive, confirmedAct, cancelAct ?? delegate { })
            {
            }

            public override Vector2 InitialSize
            {
                get
                {
                    // The size chain exactly as in the original: window width 640, minus 2x18 margin
                    // (Window.Margin) and 16 for the scrollbar -> that is the width the dialog wraps its
                    // text at. Measure with the same width, otherwise the calculation misses the wrap point.
                    const float w = 640f, margin = 18f, scrollbar = 16f, buttons = 35f, gap = 5f, titleH = 42f;
                    GameFont prev = Text.Font;
                    Text.Font = GameFont.Small;
                    float textH = Text.CalcHeight(text, w - 2f * margin - scrollbar);
                    Text.Font = prev;
                    float inner = (string.IsNullOrEmpty(title) ? 0f : titleH) + textH + gap + buttons;
                    // Capped at the previous 460 at the TOP: this change may only make a dialog smaller,
                    // never larger. Minimum 180, so a one-line question still looks like a dialog and not
                    // like a strip. A few pixels of slack, and even if the calculation were off, the
                    // direction is harmless: the text sits in a scroll view, too little height scrolls,
                    // nothing gets cut off.
                    return new Vector2(w, Mathf.Clamp(inner + 2f * margin + 8f, 180f, 460f));
                }
            }

            public override void DoWindowContents(Rect inRect)
            {

                // HANDLE IT OURSELVES. Reading the chain through to the end turned up a link that was
                // missing from the comment above: KeyBindingDef.KeyDownEvent returns FALSE as soon as any
                // QuickSearchWidget has focus - then Notify_PressedCancel is never called, and no setting
                // on this box can fix that. That particular suspect is ruled out for this mod, though: it
                // uses no QuickSearchWidget anywhere (grepped, all search fields are Widgets.TextField).
                // So the chain in the source is complete AND the box should close - yet in practice it
                // does not. Whatever interferes: this box doesn't need RimWorld's detour. If the key
                // arrives here, we do it ourselves - the same three steps Dialog_MessageBox.OnCancelKeyPressed
                // would perform.
                // It cannot close twice: Use() consumes the event, and if RimWorld's own path already
                // closed the box in the same frame, we are no longer open.
                // The diagnostic log above stays until it has been captured once - it still reports WHY
                // the normal path doesn't fire, and that needs to be known before removing it.
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
