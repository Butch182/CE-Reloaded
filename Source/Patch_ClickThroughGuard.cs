using System.Reflection;
using HarmonyLib;
using Verse;

namespace CEReloaded
{
    // ====== Der Klick-Durchschlag beim Schliessen unserer Fenster (2026-08-19) ======
    //
    // Mehrfach im Test aufgetreten, immer gleich: ein Klick auf "Cancel" im
    // Nachschub-Fenster oder in "Arm my Colony" schliesst das Fenster UND oeffnet gleichzeitig das
    // Auswahlmenue, das an derselben Bildschirmstelle in RimWorlds Zuweisungs-Reiter darunterliegt -
    // beide Male die Spalte "Food policy" mit Lavish/Fine/Simple/...
    //
    // Die Ursache steht in RimWorlds eigenem Code und ist keine Nachlaessigkeit von uns:
    // RimWorlds Richtlinien-Spalten zeichnen ihre Zelle mit Widgets.Dropdown, und das laeuft ueber
    // Widgets.ButtonInvisibleDraggable. Diese Funktion fragt NICHT das IMGUI-Ereignis ab, sondern
    // Unitys Polling:
    //
    //     if (Input.GetMouseButtonDown(0) && Mouse.IsOver(butRect))
    //         buttonInvisibleDraggable_activeControl = controlID;
    //     ...
    //     if (buttonInvisibleDraggable_activeControl == controlID && Input.GetMouseButtonUp(0)
    //         && Mouse.IsOver(butRect)) return DraggableResult.Pressed;
    //
    // Input.GetMouseButtonDown ist im ganzen BILD wahr - fuer jeden Zeichendurchgang, unabhaengig
    // davon, ob ein Ereignis schon verbraucht wurde und welches Fenster obenauf liegt. Genau deshalb
    // hilft absorbInputAroundWindow hier nicht: das arbeitet auf der Ereignis-Seite, und diese Stelle
    // liest die andere. Der Zeigerdruck bewaffnet also die Zelle darunter, waehrend unser Fenster noch
    // offen ist; beim Loslassen ist unser Fenster weg, die Zelle liegt frei, und sie feuert.
    // buttonInvisibleDraggable_activeControl ist dabei EIN globales Feld - es merkt sich genau einen
    // bewaffneten Knopf, und der ueberlebt das Schliessen unseres Fensters.
    //
    // Deshalb der Fix genau dort: schliesst eines UNSERER Fenster, wird die Bewaffnung geloescht.
    // Das kann nichts Erwuenschtes abbrechen - wer gerade unser modales Fenster bedient hat, hatte
    // keinen laufenden Zieh-Vorgang in RimWorlds Tabelle darunter, die war ja gesperrt.
    // Absichtlich auf unsere eigenen Fenster begrenzt (Namensraum-Pruefung): fuer RimWorlds eigene
    // Fenster ist dieses Verhalten deren Sache, und ein globaler Eingriff waere eine Aenderung an
    // fremdem Verhalten ohne Befund.
    [HarmonyPatch(typeof(WindowStack), nameof(WindowStack.TryRemove), new[] { typeof(Window), typeof(bool) })]
    public static class Patch_ClickThroughGuard
    {
        // Privates Feld, deshalb ueber AccessTools. Null-tolerant: benennt RimWorld es einmal um,
        // faellt nur dieser Schutz aus - nichts anderes haengt daran.
        private static readonly FieldInfo ActiveControl =
            AccessTools.Field(typeof(Widgets), "buttonInvisibleDraggable_activeControl");

        public static void Postfix(Window window, bool __result)
        {
            if (!__result || window == null || ActiveControl == null) return;
            if (window.GetType().Namespace != "CEReloaded") return;
            ActiveControl.SetValue(null, 0);
        }
    }
}
