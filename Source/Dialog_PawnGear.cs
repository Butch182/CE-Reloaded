using System;
using System.Collections.Generic;
using System.Linq;
using RimWorld;
using UnityEngine;
using Verse;

namespace CEReloaded
{
    // Compact gear viewer/editor for one pawn, opened by clicking the pawn card in the Assign tab. The vanilla Gear
    // inspect tab would open BEHIND the fullscreen tabs window (selecting the pawn puts it in the background), so
    // this brings the same information - equipment, apparel, inventory - to the front, with the edit that matters
    // in this workflow: drop an item back into storage. Everything else (loadout contents) is edited via loadouts.
    public class Dialog_PawnGear : Window
    {
        private readonly Pawn pawn;
        private Vector2 scroll;

        public override Vector2 InitialSize => new Vector2(500f, 640f);
        // Nicht unter RimWorlds Lernhilfe schieben (2026-08-19) - siehe
        // LoadoutUtility.KeepClearOfLearningReadout. Ohne Kollision aendert das nichts.
        protected override void SetInitialSizeAndPosition()
        {
            base.SetInitialSizeAndPosition();
            windowRect = LoadoutUtility.KeepClearOfLearningReadout(windowRect);
        }

        public Dialog_PawnGear(Pawn pawn) : base(new ReadoutAwareWindowDrawing())
        {
            this.pawn = pawn;
            doCloseX = true;
            forcePause = true;
            absorbInputAroundWindow = true;
            closeOnClickedOutside = true;
        }

        public override void DoWindowContents(Rect inRect)
        {
            if (pawn == null || pawn.Destroyed) { Close(); return; }

            Text.Font = GameFont.Medium;
            Widgets.Label(new Rect(0, 0, inRect.width, 30f), "CER_PawnGear_Title".Translate(pawn.LabelShortCap.ToString()));
            Text.Font = GameFont.Small;

            bool canDrop = pawn.Spawned && pawn.MapHeld != null;
            GUI.color = new Color(1f, 1f, 1f, 0.6f);
            Widgets.Label(new Rect(0, 30f, inRect.width, 20f), canDrop
                ? "CER_PawnGear_CanDropHint".Translate()
                : "CER_PawnGear_CannotDropHint".Translate());
            GUI.color = Color.white;

            // Snapshot the three containers up front: a Drop mid-draw must not modify the list being iterated.
            List<ThingWithComps> equipment = pawn.equipment != null ? pawn.equipment.AllEquipmentListForReading.ToList() : new List<ThingWithComps>();
            List<Apparel> apparel = pawn.apparel != null ? pawn.apparel.WornApparel.ToList() : new List<Apparel>();
            List<Thing> inventory = pawn.inventory != null && pawn.inventory.innerContainer != null ? pawn.inventory.innerContainer.ToList() : new List<Thing>();

            const float rowH = 28f, headerH = 24f;
            int rows = Mathf.Max(1, equipment.Count) + Mathf.Max(1, apparel.Count) + Mathf.Max(1, inventory.Count);
            float viewH = 3 * headerH + rows * rowH + 12f;

            Rect outRect = new Rect(0, 54f, inRect.width, inRect.height - 54f - 46f);
            Rect viewRect = new Rect(0, 0, outRect.width - 16f, viewH);
            Widgets.BeginScrollView(outRect, ref scroll, viewRect);
            float y = 0f;

            void Header(string label)
            {
                GUI.color = new Color(0.75f, 0.85f, 1f);
                Widgets.Label(new Rect(0, y + 3f, viewRect.width, 20f), label);
                GUI.color = Color.white;
                Widgets.DrawLineHorizontal(0, y + headerH - 2f, viewRect.width);
                y += headerH;
            }

            void Row(Thing t, Action drop)
            {
                Rect row = new Rect(0, y, viewRect.width, rowH - 2f);
                if (Mouse.IsOver(row)) Widgets.DrawHighlight(row);
                Widgets.ThingIcon(new Rect(2f, y + 2f, 22f, 22f), t);
                Widgets.Label(new Rect(30f, y + 4f, viewRect.width - 30f - 70f, 22f), t.LabelCap.Truncate(viewRect.width - 104f));
                TooltipHandler.TipRegion(row, t.DescriptionDetailed);
                if (canDrop && drop != null)
                {
                    if (LoadoutUtility.ButtonTextFit(new Rect(viewRect.width - 62f, y + 1f, 60f, 24f), "CER_PawnGear_DropButton".Translate().ToString())) drop();
                }
                y += rowH;
            }

            void EmptyRow()
            {
                GUI.color = new Color(1f, 1f, 1f, 0.4f);
                Widgets.Label(new Rect(8f, y + 4f, viewRect.width, 22f), "CER_PawnGear_EmptyRow".Translate());
                GUI.color = Color.white;
                y += rowH;
            }

            Header("CER_PawnGear_HeaderEquipment".Translate());
            if (equipment.Count == 0) EmptyRow();
            foreach (ThingWithComps eq in equipment)
            {
                ThingWithComps local = eq;
                Row(local, () => { pawn.equipment.TryDropEquipment(local, out _, pawn.PositionHeld, false); CombatReadiness.Invalidate(); });
            }

            Header("CER_PawnGear_HeaderApparel".Translate());
            if (apparel.Count == 0) EmptyRow();
            foreach (Apparel ap in apparel)
            {
                Apparel local = ap;
                Row(local, () => { pawn.apparel.TryDrop(local); CombatReadiness.Invalidate(); });
            }

            Header("CER_PawnGear_HeaderInventory".Translate());
            if (inventory.Count == 0) EmptyRow();
            foreach (Thing t in inventory)
            {
                Thing local = t;
                Row(local, () => { pawn.inventory.innerContainer.TryDrop(local, pawn.PositionHeld, pawn.MapHeld, ThingPlaceMode.Near, out _); CombatReadiness.Invalidate(); });
            }

            Widgets.EndScrollView();

            if (LoadoutUtility.ButtonTextFit(new Rect(inRect.width - LoadoutUtility.CloseBtnW, inRect.height - LoadoutUtility.BottomBarH, LoadoutUtility.CloseBtnW, LoadoutUtility.BottomBtnH), "CER_PawnGear_CloseButton".Translate().ToString())) Close();
        }
    }
}
