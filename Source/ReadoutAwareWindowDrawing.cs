using UnityEngine;
using Verse;

namespace CEReloaded
{
    // ====== Das Schliessen-X ausweichen lassen, wenn das FENSTER es nicht mehr kann (2026-08-19) ======
    //
    // Vorgeschichte in zwei Schritten, beide im Test gemessen:
    //   1. RimWorlds Lernhilfe liegt auf WindowLayer.Super und frisst jeden Klick im 200 Punkte breiten
    //      Streifen oben rechts - siehe LoadoutUtility.LearningReadoutLeftEdge. Das X unserer Fenster war
    //      dort weder sichtbar noch klickbar.
    //   2. Der Fix dafuer schiebt das Fenster nach links und macht es ausdruecklich NICHT schmaler, weil
    //      ein gekapptes Nachschub-Fenster seine rechten Bedienelemente ganz verliert (fixe Spalten, kein
    //      waagrechter Bildlauf).
    // Damit bleibt genau ein Fall offen, und der fiel auch prompt auf: ein Fenster, das BREITER ist
    // als der Platz neben dem Streifen. Das Nachschub-Fenster ist 1192 breit, bei 1366 Bildbreite stehen
    // neben der Lernhilfe nur 1158 zur Verfuegung - es kann gar nicht frei kommen, und sein X blieb
    // verdeckt ("ein Klick an die Stelle bewirkt nichts"; Cancel unten ging weiter, es war keine Sackgasse).
    //
    // Also weicht hier nicht mehr das Fenster aus, sondern nur noch das X: es rueckt so weit nach links,
    // dass es GERADE NOCH links vom Streifen sitzt. Alles andere zeichnet unveraendert RimWorld selbst.
    //
    // Warum ueber IWindowDrawing und nicht ueber doCloseX + eigenem Knopf im Fensterinhalt: Window zeichnet
    // das X in WindowOnGUI, ausserhalb von DoWindowContents, und zwar ueber genau diese Schnittstelle
    // (Window.cs: `if (doCloseX && windowDrawing.DoClostButtonSmall(rect))`). Der Konstruktor nimmt eine
    // eigene Fassung entgegen (`Window(IWindowDrawing customWindowDrawing = null)`), also reicht EINE Klasse
    // fuer alle Fenster statt eines Handgriffs in jedem DoWindowContents.
    public class ReadoutAwareWindowDrawing : IWindowDrawing
    {
        private readonly DefaultWindowDrawing inner = new DefaultWindowDrawing();

        public GUIStyle EmptyStyle => inner.EmptyStyle;
        public void DoWindowBackground(Rect rect) => inner.DoWindowBackground(rect);
        public bool DoCloseButton(Rect rect, string text) => inner.DoCloseButton(rect, text);
        public void BeginGroup(Rect rect) => inner.BeginGroup(rect);
        public void EndGroup() => inner.EndGroup();
        public void DoGrayOut(Rect rect) => inner.DoGrayOut(rect);

        public bool DoClostButtonSmall(Rect rect)
        {
            // rect ist fensterlokal (windowRect.AtZero()), der Streifen liegt in Bildschirmkoordinaten -
            // ohne die Fensterposition laesst sich das nicht vergleichen. Window setzt currentlyDrawnWindow
            // unmittelbar vor diesem Aufruf (InnerWindowOnGUI), es ist hier also verlaesslich gesetzt.
            Window w = Find.WindowStack != null ? Find.WindowStack.currentlyDrawnWindow : null;
            float limit = LoadoutUtility.LearningReadoutLeftEdge();
            float shift = 0f;
            if (w != null && limit < float.MaxValue)
            {
                // Die 6 Punkte Luft gehoeren HINTER die Pruefung, nicht hinein (2026-08-22). Vorher standen
                // sie im Vergleich selbst, und damit galt schon ein
                // Fenster als ueberdeckt, dessen rechte Kante GENAU auf der Streifenkante endet - was nach
                // dem Verschieben des Fensters der Normalfall ist. Folge: das Hauptfenster rueckte sein X
                // um 6 Punkte und zeigte den Erklaertext, obwohl es laengst frei stand.
                float overlap = w.windowRect.x + rect.xMax - limit;
                // Nie so weit, dass das X aus dem Fenster faellt oder in dessen linke Haelfte wandert -
                // dort steht bei uns ueberall Inhalt (Titel, Reiterzeile).
                if (overlap > 0f) shift = Mathf.Min(overlap + 6f, rect.width * 0.4f);
            }
            if (shift <= 0f) return inner.DoClostButtonSmall(rect);

            // Dieselbe Geometrie wie Widgets.CloseButtonFor, nur um `shift` nach links.
            Rect r = new Rect(rect.x + rect.width - 18f - 4f - shift, rect.y + 4f, 18f, 18f);
            // Dunkler Grund darunter: an der verschobenen Stelle steht je nach Fenster Inhalt, und ein
            // weisses X auf hellem Text waere zwar klickbar, aber nicht auffindbar.
            Widgets.DrawBoxSolid(r.ExpandedBy(3f), new Color(0.12f, 0.12f, 0.13f, 0.92f));
            bool clicked = Widgets.ButtonImage(r, TexButton.CloseXSmall);
            TooltipHandler.TipRegion(r, "CER_ReadoutAwareWindowDrawing_CloseMovedTip".Translate());
            return clicked;
        }
    }
}
