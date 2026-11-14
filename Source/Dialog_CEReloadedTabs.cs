using System;
using System.Collections.Generic;
using System.Linq;
using RimWorld;
using Verse;
using UnityEngine;

namespace CEReloaded
{
    public class Dialog_CEReloadedTabs : Window
    {
        private enum Tab
        {
            Ingredients,
            Loadouts,
            AssignLoadouts,
            Roles,
            AmmoBills
        }

        // Since the lazy-template rework the start tab is the Assign tab: the former Weapon Loadouts tab
        // (bulk generation + curation) no longer has a tab card - templates are created invisibly on
        // assignment. Tab.Loadouts only remains as an enum value to keep the rework small; it is no
        // longer reachable.
        private Tab currentTab = Tab.AssignLoadouts;

        private Dialog_Ingredients ingredientsDialog;
        private Dialog_GenerateLoadouts loadoutsDialog;
        private Dialog_GenerateAmmoBills billsDialog;
        private Dialog_AssignLoadouts assignDialog;
        // For clicks on CE's own loadout cell in the Assign tab: if the planner is open, the weapon window
        // should use ITS state (role toggle, filters) and not that of a second, invisible instance.
        // See Dialog_AssignLoadouts.OpenWeaponPickerFor.
        internal Dialog_AssignLoadouts AssignDialog => assignDialog;

        public override Vector2 InitialSize
        {
            // Use more of the screen's height where available (falls back gracefully on small displays).
            get { return new Vector2(Mathf.Min(1100f, UI.screenWidth - 40f), Mathf.Min(1015f, UI.screenHeight - 40f)); }
        }

        // Keep clear of RimWorld's learning readout - see LoadoutUtility.KeepClearOfLearningReadout.
        // Without an overlap this changes nothing.
        // Found here: below roughly 1480 UI width the close X sat under the learning readout and was
        // neither visible nor clickable - and since this window sets absorbInputAroundWindow, Escape was
        // the only way out.
        protected override void SetInitialSizeAndPosition()
        {
            base.SetInitialSizeAndPosition();
            windowRect = LoadoutUtility.KeepClearOfLearningReadout(windowRect);
        }

        public Dialog_CEReloadedTabs() : base(new ReadoutAwareWindowDrawing())
        {
            this.doCloseX = true;
            this.doCloseButton = false;
            this.forcePause = true;
            this.absorbInputAroundWindow = true;
            // Enter in a text field (e.g. naming a role) must not close the whole window.
            this.closeOnAccept = false;

            ingredientsDialog = new Dialog_Ingredients();
            ingredientsDialog.parentWindow = this;

            loadoutsDialog = new Dialog_GenerateLoadouts();
            loadoutsDialog.parentWindow = this;

            billsDialog = new Dialog_GenerateAmmoBills();
            billsDialog.parentWindow = this;

            assignDialog = new Dialog_AssignLoadouts();
            assignDialog.parentWindow = this;
        }

        // Access for child dialogs (auto-assign creates default loadouts for virtual weapon picks on apply).
        public Dialog_GenerateLoadouts LoadoutsDialog => loadoutsDialog;

        // Let a child popup (the auto-assign preview) send the player to the Generate Loadouts tab to create
        // loadouts, optionally filtering straight to a specific weapon. Every "Save as Role" entry point
        // calls StartRoleDraftFromPawn (which sets loadoutsDialog.RoleMode = true) right before this, so
        // routing off that flag lands on the right top-level tab (Roles now has its own, separate from
        // Weapon Loadouts) without every one of those call sites needing to say so explicitly.
        public void JumpToLoadouts()
        {
            // Since the lazy-template rework this jump ALWAYS lands in the Roles tab - the Weapon Loadouts
            // tab no longer has a card, and every remaining caller (the "Save as Role" paths) sets
            // RoleMode itself right before.
            currentTab = Tab.Roles;
            loadoutsDialog.RoleMode = true;
        }

        // Open straight on the Assign tab (used by the gear-tab loadout panel's jump button).
        public void JumpToAssign(string pawnFilter = null)
        {
            currentTab = Tab.AssignLoadouts;
            if (pawnFilter != null) assignDialog.SetPawnSearch(pawnFilter);
            assignDialog.RefreshPawns();
        }

        // Readiness display -> "no ammo bill running": the path leads to the bills overview.
        public void JumpToAmmoBills()
        {
            currentTab = Tab.AmmoBills;
        }

        // Snapshot on open, comparison on close - see LoadoutUtility.AssignmentSignature. Previously
        // PostClose ALWAYS opened the supply dialog as soon as there was a backlog anywhere: look in
        // once, close again, and it popped up. Dialog_ChangeAmmo already had this comparison, this
        // window did not.
        private string openSignature;

        public override void PostOpen()
        {
            base.PostOpen();
            openSignature = LoadoutUtility.AssignmentSignature();
            // Since the lazy-template rework the start tab is the Assign tab - the same refresh a tab-switch
            // click would trigger, otherwise the first frame shows stale colonists.
            if (currentTab == Tab.AssignLoadouts) assignDialog.RefreshPawns();
            // First-ever open: show the workflow overview once so the three tabs read as a sequence.
            Dialog_CERWorkflowGuide.OpenIfFirstTime();
        }

        public override void PostClose()
        {
            base.PostClose();
            // See LoadoutUtility.CheckAndOpenMissingProductionPopup - also hooked into Dialog_ChangeAmmo's own
            // close, since that one can be opened standalone from the map gizmo without this window ever
            // being open at all.
            // Only if something really differs from when the window opened - openSignature above.
            if (openSignature == null || LoadoutUtility.AssignmentSignature() != openSignature)
                LoadoutUtility.CheckAndOpenMissingProductionPopup();
        }

        private List<TabRecord> cachedTabs;
        private object cachedTabsLanguage;

        // The tab bar, built ONCE. Each selection hangs off a selectedGetter instead of a frozen bool -
        // otherwise the cached list would know nothing about tab switches.
        // Tabs read as the workflow order: prepare ingredients -> decide -> distribute -> supply.
        private List<TabRecord> BuildTabs()
        {
            List<TabRecord> tabs = new List<TabRecord>();
            TabRecord T(string label, Action onClick, Func<bool> isOn)
            {
                TabRecord t = new TabRecord(label, onClick, false);
                t.selectedGetter = isOn;
                return t;
            }
            tabs.Add(T("CER_Tabs_TabEquipmentPacks".Translate(), () => { SwitchTab(Tab.Ingredients); },
                () => currentTab == Tab.Ingredients));
            // The Weapon Loadouts tab was dropped with the lazy-template rework - weapons are picked directly
            // in the Assign tab or the wizard, and the template behind them is created invisibly.
            tabs.Add(T("CER_Tabs_TabRoles".Translate(), () => {
                SwitchTab(Tab.Roles);
                loadoutsDialog.RoleMode = true;
            }, () => currentTab == Tab.Roles));
            tabs.Add(T("CER_Tabs_TabAssignLoadouts".Translate(), () => {
                if (currentTab != Tab.AssignLoadouts)
                {
                    SwitchTab(Tab.AssignLoadouts);
                    assignDialog.RefreshPawns();
                }
            }, () => currentTab == Tab.AssignLoadouts));
            tabs.Add(T("CER_Tabs_TabBillOverview".Translate(), () => {
                if (currentTab != Tab.AmmoBills)
                {
                    SwitchTab(Tab.AmmoBills);
                    billsDialog.RefreshBenchesAndAmmo();
                }
            }, () => currentTab == Tab.AmmoBills));
            return tabs;
        }

        // A single switch path for all tabs. Previously every TabRecord set `currentTab` itself; so there
        // was no place where "the Assign tab is being LEFT" could be noticed - and exactly that is needed
        // so the supply window always comes at the end, when leaving the tab (tab switch or close).
        // Structurally here instead of five times in the lambdas: otherwise the next new tab forgets it.
        private void SwitchTab(Tab next)
        {
            if (currentTab == next) return;
            Tab prev = currentTab;
            currentTab = next;
            if (prev == Tab.AssignLoadouts) CheckSupplyOnLeavingAssign();
            // When ENTERING the Roles tab, every row with exactly one loadout may load itself again - including
            // the one where 'Unload' was clicked on the last visit. Same single switch point as above, for
            // the same reason.
        }

        // The collected supply hint that Dialog_AssignLoadouts' single assignment no longer opens itself.
        // Same signature comparison as PostClose - merely clicking through the tabs must not trigger it,
        // otherwise it is just as annoying as the per-assignment popup it replaces.
        // openSignature is UPDATED in the process, so PostClose does not report the same change a second
        // time when the window is closed after the tab switch.
        private void CheckSupplyOnLeavingAssign()
        {
            string now = LoadoutUtility.AssignmentSignature();
            // Diagnostic logging removed again after the case was reproduced: the signature does notice a
            // pure pack change ("signature DIFFERENT"), and the supply window came up. The observed bug was
            // not reproducible here.
            if (openSignature != null && now == openSignature) return;
            openSignature = now;
            // The window has already opened once for EXACTLY THIS state (the same window twice was too
            // much): the sequence was Apply -> window -> Cancel -> switch tab -> the same window again. One
            // change, two windows. The backlog does not become invisible because of this - the readiness
            // display at the top shows it permanently and leads exactly here with one click; that is the
            // quiet place for it, a window that pushes itself forward is not.
            if (now == LoadoutUtility.lastSupplyOfferSignature) return;
            LoadoutUtility.CheckAndOpenMissingProductionPopup();
        }

        public override void DoWindowContents(Rect inRect)
        {
            // Header strip for the window-level buttons, then the tab row below it (TabDrawer draws tabs extending ~32px
            // UP from tabRect.y, so the strip must clear that or the tabs cover/eat the buttons).
            // The "?" is gone (no other mod does that) - the crash course appears by itself
            // on first open and afterwards via Mod Settings -> Tools.

            Rect armRect = new Rect(inRect.xMax - 168f, inRect.y + 4f, 168f, 26f);
            // "Fetch gear now" between readiness and Arm my Colony - as in the Assign tab.
            Rect fetchRect = new Rect(armRect.x - 8f - 150f, inRect.y + 4f, 150f, 26f);
            // Both buttons share one font size - see ButtonRowFont.
            GameFont prevHeadFont = Text.Font;
            Text.Font = LoadoutUtility.ButtonRowFont(
                (fetchRect, "CER_LoadoutUtility_FetchGearButtonLabel".Translate().ToString()),
                (armRect, "CER_LoadoutUtility_ArmColonyButtonLabel".Translate().ToString()));
            if (LoadoutUtility.DrawArmColonyButton(armRect)) Find.WindowStack.Add(new Dialog_ArmColony(loadoutsDialog, billsDialog, assignDialog));
            LoadoutUtility.DrawFetchGearButton(fetchRect);
            Text.Font = prevHeadFont;

            LoadoutUtility.DrawReadinessWidget(new Rect(inRect.x, inRect.y + 4f, fetchRect.x - inRect.x - 8f, 26f), () => loadoutsDialog, () => assignDialog);

            Rect tabRect = new Rect(inRect.x, inRect.y + 66f, inRect.width, inRect.height - 66f);

            // Built ONCE instead of on every pass. This list used to be created SEVERAL times
            // per frame - five TabRecord objects, five closures, five translation calls and the list
            // itself - in EVERY tab of this window, including where no list is drawn at all. On its
            // own that is little; it is baseline load that need not exist, and it occurs on every one
            // of the ~286 IMGUI passes per second.
            // Made possible by TabRecord.selectedGetter: the selection is QUERIED per frame instead of
            // frozen at build time - otherwise the cached list would not have noticed tab switches. The
            // language check rebuilds it if the language is changed while the window is open; otherwise
            // the labels stay stuck in the old translation.
            if (cachedTabs == null || !ReferenceEquals(cachedTabsLanguage, LanguageDatabase.activeLanguage))
            {
                cachedTabsLanguage = LanguageDatabase.activeLanguage;
                cachedTabs = BuildTabs();
            }
            List<TabRecord> tabs = cachedTabs;

            // The 3rd argument is a MAX cap, not a divisor - TabDrawer.DrawTabs computes each tab's own
            // natural width internally as (baseRect.width + (count-1)*10) / count (10px accounts for the
            // overlap every tab needs with its neighbor to hide the seam between them). Passing
            // tabRect.width/5f here as if it were the exact width undercut that natural (larger) value,
            // clamping every tab too narrow to fully overlap its neighbor - the gap that left behind is
            // exactly the thin black seams seen (2026-07-30). tabRect.width itself is always >= the
            // natural per-tab width for any reasonable tab count, so it never clamps anything down.
            TabDrawer.DrawTabs(tabRect, tabs, tabRect.width);

            // Draw background frame for the tab content
            Widgets.DrawMenuSection(tabRect);

            // Deflate the rect slightly for padding
            tabRect = tabRect.ContractedBy(16f);

            GUI.BeginGroup(tabRect);
            Rect localRect = new Rect(0, 0, tabRect.width, tabRect.height);

            if (currentTab == Tab.Ingredients)
            {
                ingredientsDialog.DoWindowContents(localRect);
            }
            else if (currentTab == Tab.Loadouts)
            {
                loadoutsDialog.DoWindowContents(localRect);
            }
            else if (currentTab == Tab.Roles)
            {
                // Calls DoRoleMode directly rather than DoWindowContents - skips that method's own embedded
                // mode-toggle button entirely, since the tab bar itself is now that toggle.
                loadoutsDialog.DoRoleMode(localRect);
            }
            else if (currentTab == Tab.AmmoBills)
            {
                billsDialog.DoWindowContents(localRect);
            }
            else if (currentTab == Tab.AssignLoadouts)
            {
                assignDialog.DoWindowContents(localRect);
            }
            GUI.EndGroup();
        }
    }
}
