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

        // Startreiter ist seit dem Lazy-Umbau (2026-09-01) der Zuweisungs-Reiter: der fruehere
        // Weapon-Loadouts-Reiter (Massenerzeugung + Kuration) hat keine Reiterkarte mehr - Vorlagen
        // entstehen unsichtbar beim Zuweisen. Tab.Loadouts existiert nur noch als Enum-Wert, damit der
        // Rueckbau klein bleibt; erreichbar ist er nicht mehr.
        private Tab currentTab = Tab.AssignLoadouts;

        private Dialog_Ingredients ingredientsDialog;
        private Dialog_GenerateLoadouts loadoutsDialog;
        private Dialog_GenerateAmmoBills billsDialog;
        private Dialog_AssignLoadouts assignDialog;
        // Fuer den Klick auf CEs eigene Loadout-Zelle im Zuweisungs-Reiter (2026-09-04): ist der Planer
        // offen, soll das Waffenfenster SEINEN Zustand benutzen (Rollen-Schalter, Filter) und nicht den
        // einer zweiten, unsichtbaren Instanz. Siehe Dialog_AssignLoadouts.OpenWeaponPickerFor.
        internal Dialog_AssignLoadouts AssignDialog => assignDialog;

        public override Vector2 InitialSize
        {
            // Use more of the screen's height where available (falls back gracefully on small displays).
            get { return new Vector2(Mathf.Min(1100f, UI.screenWidth - 40f), Mathf.Min(1015f, UI.screenHeight - 40f)); }
        }

        // Nicht unter RimWorlds Lernhilfe schieben (2026-08-19) - siehe
        // LoadoutUtility.KeepClearOfLearningReadout. Ohne Kollision aendert das nichts.
        // Hier aufgefallen: unter etwa 1480 UI-Breite lag das Schliessen-X unter der Lernhilfe und war weder
        // sichtbar noch klickbar - und weil dieses Fenster absorbInputAroundWindow setzt, war ausser
        // Escape kein Weg mehr heraus.
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
            // Seit dem Lazy-Umbau (2026-09-01) landet dieser Sprung IMMER im Rollen-Reiter - der
            // Weapon-Loadouts-Reiter hat keine Karte mehr, und jeder verbliebene Aufrufer (die
            // "Save as Role"-Wege) setzt RoleMode unmittelbar vorher selbst.
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

        // Bereitschaftsanzeige -> "kein Munitionsauftrag laeuft" (2026-08-30): der Weg fuehrt in die Auftragsuebersicht.
        public void JumpToAmmoBills()
        {
            currentTab = Tab.AmmoBills;
        }

        // Schnappschuss beim Oeffnen, Vergleich beim Schliessen (2026-08-09) - siehe
        // LoadoutUtility.AssignmentSignature. Vorher rief
        // PostClose den Nachschub-Dialog IMMER, sobald irgendwo ein Rueckstand bestand: einmal
        // hineingesehen und wieder zugemacht, und er ging auf. Dialog_ChangeAmmo hatte diesen Vergleich
        // laengst, dieses Fenster nicht.
        private string openSignature;

        public override void PostOpen()
        {
            base.PostOpen();
            openSignature = LoadoutUtility.AssignmentSignature();
            // Der Startreiter ist seit dem Lazy-Umbau der Zuweisungs-Reiter - derselbe Refresh, den der
            // Reiterwechsel-Klick ausloesen wuerde, sonst zeigt das erste Bild veraltete Kolonisten.
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
            // Nur wenn wirklich etwas anders ist als beim Oeffnen (2026-08-09) - openSignature oben.
            if (openSignature == null || LoadoutUtility.AssignmentSignature() != openSignature)
                LoadoutUtility.CheckAndOpenMissingProductionPopup();
        }

        private List<TabRecord> cachedTabs;
        private object cachedTabsLanguage;

        // Die Reiterleiste, EINMAL gebaut. Jede Auswahl haengt an einem selectedGetter statt an einem
        // eingefrorenen bool - sonst wuesste die zwischengespeicherte Liste nichts vom Reiterwechsel.
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
            // Der Weapon-Loadouts-Reiter entfiel mit dem Lazy-Umbau (2026-09-01) - Waffen waehlt man im
            // Zuweisungs-Reiter bzw. im Wizard direkt, die Vorlage dahinter entsteht unsichtbar.
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

        // Ein einziger Umschaltpfad fuer alle Tabs (2026-08-10). Vorher setzte jeder TabRecord
        // `currentTab` selbst; damit gab es keine Stelle, an der man "der Assign-Tab wird gerade
        // VERLASSEN" haette bemerken koennen - und genau die braucht es, damit das Nachschub-Fenster immer
        // am Ende kommt, beim Verlassen des Reiters (Reiterwechsel oder Schliessen).
        // Strukturell hier statt fuenfmal in den Lambdas: sonst vergisst der naechste neue Tab es.
        private void SwitchTab(Tab next)
        {
            if (currentTab == next) return;
            Tab prev = currentTab;
            currentTab = next;
            if (prev == Tab.AssignLoadouts) CheckSupplyOnLeavingAssign();
            // Beim BETRETEN des Rollen-Reiters darf sich jede Zeile mit genau einer Beladung wieder
            // selbst laden (2026-08-31) - auch die, in der er beim letzten Besuch 'Entladen'
            // geklickt hat. Dieselbe eine Umschaltstelle wie oben, aus demselben Grund.
        }

        // Der gesammelte Nachschub-Hinweis, den Dialog_AssignLoadouts' Einzelzuweisung nicht mehr selbst
        // aufmacht. Derselbe Signaturvergleich wie PostClose - blosses Durchklicken durch die Tabs darf
        // ihn nicht ausloesen, sonst ist er genauso laestig wie das Popup pro Zuweisung, das er ersetzt.
        // openSignature wird dabei NACHGEZOGEN, damit PostClose dieselbe Aenderung nicht ein zweites Mal
        // meldet, wenn man nach dem Tabwechsel das Fenster schliesst.
        private void CheckSupplyOnLeavingAssign()
        {
            string now = LoadoutUtility.AssignmentSignature();
            // Diagnoselogging am 2026-08-15 wieder ausgebaut, nachdem der Fall im Test nachgestellt war:
            // die Signatur bemerkt eine reine Pack-Aenderung sehr wohl ("Signatur ANDERS"), und das
            // Nachschub-Fenster kam. Der beobachtete Fehler war hier nicht reproduzierbar.
            if (openSignature != null && now == openSignature) return;
            openSignature = now;
            // Fuer GENAU DIESEN Stand ist das Fenster schon einmal aufgegangen (2026-08-16, zweimal dasselbe
            // Fenster war zu viel): der Ablauf war Uebernehmen -> Fenster -> Cancel -> Reiter
            // wechseln -> dasselbe Fenster nochmal. Eine Aenderung, zwei Fenster. Der Rueckstand bleibt
            // deswegen nicht unsichtbar - die Bereitschaftsanzeige oben zeigt ihn dauerhaft und fuehrt mit
            // einem Klick genau hierher; das ist der ruhige Ort dafuer, ein Fenster, das sich selbst
            // aufdraengt, ist es nicht.
            if (now == LoadoutUtility.lastSupplyOfferSignature) return;
            LoadoutUtility.CheckAndOpenMissingProductionPopup();
        }

        public override void DoWindowContents(Rect inRect)
        {
            // Header strip for the window-level buttons, then the tab row below it (TabDrawer draws tabs extending ~32px
            // UP from tabRect.y, so the strip must clear that or the tabs cover/eat the buttons).
            // Das "?" ist weg (2026-08-30: das macht sonst auch kein Mod) - der Crashkurs kommt
            // beim ersten Oeffnen von selbst und danach ueber Mod-Einstellungen -> Werkzeuge.

            Rect armRect = new Rect(inRect.xMax - 168f, inRect.y + 4f, 168f, 26f);
            // "Fetch gear now" zwischen Bereitschaft und Arm my Colony (2026-08-30) - wie im Zuweisungs-Reiter.
            Rect fetchRect = new Rect(armRect.x - 8f - 150f, inRect.y + 4f, 150f, 26f);
            // Beide Knoepfe teilen eine Schriftstufe (2026-08-30) - siehe ButtonRowFont.
            GameFont prevHeadFont = Text.Font;
            Text.Font = LoadoutUtility.ButtonRowFont(
                (fetchRect, "CER_LoadoutUtility_FetchGearButtonLabel".Translate().ToString()),
                (armRect, "CER_LoadoutUtility_ArmColonyButtonLabel".Translate().ToString()));
            if (LoadoutUtility.DrawArmColonyButton(armRect)) Find.WindowStack.Add(new Dialog_ArmColony(loadoutsDialog, billsDialog, assignDialog));
            LoadoutUtility.DrawFetchGearButton(fetchRect);
            Text.Font = prevHeadFont;

            LoadoutUtility.DrawReadinessWidget(new Rect(inRect.x, inRect.y + 4f, fetchRect.x - inRect.x - 8f, 26f), () => loadoutsDialog, () => assignDialog);

            Rect tabRect = new Rect(inRect.x, inRect.y + 66f, inRect.width, inRect.height - 66f);

            // EINMAL gebaut statt in jedem Durchgang (2026-08-15). Diese Liste ist
            // pro Bild MEHRFACH entstanden - fuenf TabRecord-Objekte, fuenf Abschlussfunktionen, fuenf
            // Uebersetzungsaufrufe und die Liste selbst -, und zwar in JEDEM Reiter dieses Fensters, also
            // auch dort, wo gar keine Liste gezeichnet wird. Fuer sich genommen ist das wenig; es ist
            // Grundlast, die es nicht geben muss, und sie faellt bei jedem der ~286 IMGUI-Durchgaenge pro
            // Sekunde an.
            // Moeglich wird es durch TabRecord.selectedGetter: die Auswahl wird pro Bild ABGEFRAGT statt
            // beim Bauen eingefroren - sonst haette die zwischengespeicherte Liste den Reiterwechsel nicht
            // mitbekommen. Der Sprachvergleich baut sie neu, falls jemand die Sprache umstellt, waehrend
            // das Fenster offen ist; die Beschriftungen stecken sonst uebersetzt fest.
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
