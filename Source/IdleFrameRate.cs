using System.Reflection;
using HarmonyLib;
using UnityEngine;
using Verse;

namespace CEReloaded
{
    // Warum es das gibt (2026-08-15): der Luefter drehte hoch, sobald eines der grossen Fenster dieses
    // Mods offen war, und voll hoch im Zuweisungs-Fenster.
    //
    // Die Rechnung, jedes Glied im RimWorld-Dekompilat nachgelesen, nichts davon geraten:
    //   * Verse.Root.CheckGlobalInit setzt Application.targetFrameRate auf die Bildwiederholrate des
    //     Monitors. Auf diesem Rechner sind das 143 Hz - RimWorld zeichnet also 143 Bilder pro Sekunde,
    //     auch wenn das Spiel pausiert ist und sich auf dem Schirm nichts bewegt.
    //   * Verse.Window.WindowOnGUI ruft den Fensterinhalt ueber GUI.Window auf. Unitys IMGUI ruft die
    //     Fensterfunktion pro Bild MINDESTENS zweimal (Layout und Repaint) plus einmal je Eingabe.
    //   * Unser teuerster Fensterinhalt kostet gemessene ~3,4 ms pro Durchgang.
    //     3,4 ms * 2 Durchgaenge * 143 Bilder = rund 1000 ms Rechenzeit pro Sekunde - ein Kern zu 100 %,
    //     dauerhaft, fuer ein Bild, das stillsteht. Genau das hoert man am Luefter.
    //
    // Der Hebel hier ist der eine, der ohne Ruecksicht auf den Inhalt wirkt: solange ein Fenster dieses
    // Mods offen ist UND das Spiel dadurch pausiert, gibt es keinen Grund fuer 143 Bilder pro Sekunde.
    // 60 im Umgang, 30 wenn eine Dreiviertelsekunde lang weder Maus noch Taste etwas getan hat. Das ist
    // dieselbe Arbeit pro Bild wie vorher - nur ein Viertel bis die Haelfte so oft.
    //
    // Bewusst NICHT an einzelnen Fenstern aufgehaengt, sondern an einer Pruefung pro Bild: ein Fenster,
    // das ueber PostClose/Close hinweg vergisst freizugeben (Absturz im Inhalt, Spielstand geladen
    // waehrend es offen ist), liesse das Spiel sonst dauerhaft bei 30 Bildern stehen. Diese Fassung
    // heilt sich selbst - sobald kein Fenster von uns mehr offen ist, steht im naechsten Bild wieder
    // der urspruengliche Wert.
    [HarmonyPatch(typeof(WindowStack), nameof(WindowStack.WindowsUpdate))]
    public static class Patch_IdleFrameRate
    {
        public static void Postfix()
        {
            IdleFrameRate.Update();
        }
    }

    public static class IdleFrameRate
    {
        // Im Umgang: 60 ist fuer eine pausierte, stehende Oberflaeche fluessig - auch beim Ziehen eines
        // Reglers. Im Leerlauf: 30, da bewegt sich ueberhaupt nichts.
        private const int ActiveFps = 60;
        private const int IdleFps = 30;
        private const float IdleAfterSeconds = 0.75f;

        private static readonly Assembly OwnAssembly = typeof(IdleFrameRate).Assembly;

        private static bool held;            // wir halten gerade die Bildrate (und kennen den Originalwert)
        private static int savedTarget;
        private static int appliedFps = -1;  // was zuletzt wirklich gesetzt wurde, damit wir nicht pro Bild schreiben
        private static Vector3 lastMouse;
        private static float lastInputAt;

        public static void Update()
        {
            // Bei eingeschalteter Bildsynchronisation gar nicht erst anfassen (2026-08-25: Flackern bei
            // laufendem Luefter, Messzeile darin: "140 Bilder/s (Ziel 30, vSync 1)").
            // Unity ignoriert targetFrameRate, sobald vSyncCount > 0 - die 140 Bilder bei "Ziel 30" sind der
            // Beweis. Das Umschalten 30/60 je nach Eingabe hat also nie gedrosselt und war zugleich der
            // EINZIGE Wert, den dieser Mod an der Bildausgabe noch veraendert hat. Ein Hebel ohne Wirkung,
            // der als Flacker-Kandidat uebrig bleibt, kommt weg: unter vSync tut dieser Patch nichts mehr.
            // Ohne vSync (vom Spieler abgeschaltet) drosselt er weiter wie bisher - dort wirkt der Wert.
            // Release() statt return, damit ein Wert, den wir vor einem vSync-Wechsel gesetzt hatten, nicht
            // stehen bleibt.
            if (QualitySettings.vSyncCount > 0 || !ShouldHold())
            {
                Release();
                return;
            }

            if (!held)
            {
                savedTarget = Application.targetFrameRate;
                held = true;
                appliedFps = -1;
                lastMouse = Input.mousePosition;
                lastInputAt = Time.realtimeSinceStartup;
            }

            // Maus oder Taste = "er arbeitet gerade damit". Das Zeitfenster danach faengt die Pausen
            // zwischen zwei Klicks ab, damit die Bildrate nicht bei jedem Absetzen der Hand springt.
            Vector3 mouse = Input.mousePosition;
            if (mouse != lastMouse || Input.anyKey || Input.mouseScrollDelta.y != 0f)
            {
                lastMouse = mouse;
                lastInputAt = Time.realtimeSinceStartup;
            }
            bool idle = Time.realtimeSinceStartup - lastInputAt > IdleAfterSeconds;

            // Nie ueber den Wert hinaus, den das Spiel selbst gesetzt hat - sonst wuerde dieser Patch auf
            // einem 60-Hz-Schirm die Bildrate ERHOEHEN wollen.
            int want = idle ? IdleFps : (savedTarget > 0 ? Mathf.Min(savedTarget, ActiveFps) : ActiveFps);
            // Auch dann neu setzen, wenn jemand anders den Wert unter uns geaendert hat (RimWorld setzt ihn
            // beim Start und beim Aufloesungswechsel, ein anderer Mod kann es jederzeit tun). Sonst haetten
            // wir eine Sperre, die still verloren geht und trotzdem als gesetzt gilt.
            if (want == appliedFps && Application.targetFrameRate == appliedFps) return;
            appliedFps = want;
            // ============ vSync wird NICHT MEHR angefasst (2026-08-18) ============
            // Hier stand: "bei eingeschalteter Synchronisation ignoriert Unity targetFrameRate, also
            // schalten wir sie ab, solange wir halten". Das stimmt in der ersten Haelfte und war in der
            // zweiten ein Fehler mit sichtbarer Folge.
            //
            // Beobachtet zweimal an einem Abend: der Bildschirm flackerte - und tags zuvor dasselbe beim
            // Tippen in einem anderen Programm. Dazu die Messzeile
            // aus dem laufenden Spiel: "76 Bilder/s (Ziel 144, vSync 1)". vSync stand also auf 1, WAEHREND
            // wir halten - jemand hat unsere Null ueberschrieben. Der Test eine Zeile darueber prueft nur
            // targetFrameRate, nicht vSync: wir haben die Null also in jedem Bild neu hingeschrieben, in
            // dem der Wert wieder auf 1 stand. Ein An-und-Aus der Synchronisation pro Bild ist genau das,
            // was ein Bildschirm als Flackern zeigt - Unity baut dabei jedes Mal den Bildtakt neu auf.
            //
            // Die Zeile hat also nie erreicht, wofuer sie da war (die 76 Bilder/s beweisen es: das ist die
            // halbe Bildwiederholrate, also vSync), und hat dabei geflackert. Ein Hebel, der nicht wirkt
            // und stattdessen schadet, kommt weg.
            //
            // Was das kostet, ehrlich: bei eingeschalteter Synchronisation ist die Drosselung wirkungslos -
            // Unity ignoriert targetFrameRate dann, und daran kann kein Mod etwas aendern, das nicht genau
            // diesen Schaden anrichtet. Der Luefter bleibt in dem Fall also oben, und der richtige Hebel
            // ist der andere: weniger Arbeit pro Bild statt weniger Bilder.
            Application.targetFrameRate = want;
            // Die Messzeile fuer die Bildrate ist am 2026-08-17 entfallen: eine Luefter-Messung hat gezeigt,
            // dass das Entwickler-Logfenster selbst 530 von 537 ms/s aller
            // Fensterkosten traegt. Die Drosselung darunter bleibt unveraendert - nur ihr Protokoll ist weg.
        }

        private static void Release()
        {
            if (!held) return;
            held = false;
            appliedFps = -1;
            Application.targetFrameRate = savedTarget;
            // KEIN Zuruecksetzen von vSync mehr (2026-08-18): wir fassen den Wert nicht mehr an, also
            // gehoert er uns auch nicht. Ihn hier zu schreiben hiesse, eine Einstellung zurueckzustellen,
            // die der Spieler oder der Treiber inzwischen selbst geaendert haben koennte - und beim
            // Oeffnen/Schliessen eines Fensters war genau das der zweite Flacker-Moment.
        }

        // Zwei Bedingungen, und die zweite ist die wichtige: NUR wenn das Spiel wegen eines Fensters
        // ohnehin steht. Ein Fenster von uns, das den Betrieb nicht pausiert, darf die Bildrate der
        // laufenden Karte nicht anfassen - da bewegt sich etwas, das fluessig bleiben muss.
        private static bool ShouldHold()
        {
            WindowStack stack = Find.WindowStack;
            if (stack == null || stack.Count == 0) return false;
            if (!stack.WindowsForcePause) return false;
            for (int i = 0; i < stack.Count; i++)
            {
                Window w = stack[i];
                // Ueber die Assembly statt ueber eine Liste von Fenstertypen: eine Aufzaehlung waere beim
                // naechsten neuen Dialog still unvollstaendig (dieselbe Begruendung wie in
                // CombatReadiness.AnyModWindowOpen). stack.Windows waere hier falsch - das ist ein
                // AsReadOnly() und belegt bei JEDEM Aufruf Speicher; Count + Indexer tun es nicht.
                if (w != null && w.GetType().Assembly == OwnAssembly) return true;
            }
            return false;
        }
    }
}
