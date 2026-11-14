using UnityEngine;
using Verse;

namespace CEReloaded
{
    // ====== Let the close X move aside when the WINDOW can no longer do so ======
    //
    // Background in two steps, both measured in testing:
    //   1. RimWorld's learning readout sits on WindowLayer.Super and eats every click in the 200-point
    //      strip at the top right - see LoadoutUtility.LearningReadoutLeftEdge. The X of our windows was
    //      neither visible nor clickable there.
    //   2. The fix for that moves the window left and deliberately does NOT make it narrower, because a
    //      clipped supply window loses its right-hand controls entirely (fixed columns, no horizontal
    //      scrolling).
    // That leaves exactly one case open, and it showed up promptly: a window WIDER than the space next
    // to the strip. The supply window is 1192 wide; at a screen width of 1366 only 1158 are available
    // next to the learning readout - it can never get clear, and its X stayed covered (clicking there
    // did nothing; Cancel at the bottom still worked, so it was not a dead end).
    //
    // So now it is not the window that moves aside but only the X: it shifts left just far enough to
    // sit JUST left of the strip. Everything else is drawn by RimWorld itself, unchanged.
    //
    // Why via IWindowDrawing and not via doCloseX + our own button in the window content: Window draws
    // the X in WindowOnGUI, outside DoWindowContents, through exactly this interface
    // (Window.cs: `if (doCloseX && windowDrawing.DoClostButtonSmall(rect))`). The constructor accepts a
    // custom implementation (`Window(IWindowDrawing customWindowDrawing = null)`), so ONE class covers
    // all windows instead of a tweak in every DoWindowContents.
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
            // rect is window-local (windowRect.AtZero()), the strip is in screen coordinates - they
            // cannot be compared without the window position. Window sets currentlyDrawnWindow right
            // before this call (InnerWindowOnGUI), so it is reliably set here.
            Window w = Find.WindowStack != null ? Find.WindowStack.currentlyDrawnWindow : null;
            float limit = LoadoutUtility.LearningReadoutLeftEdge();
            float shift = 0f;
            if (w != null && limit < float.MaxValue)
            {
                // The 6 points of margin belong AFTER the check, not inside it. Previously they were part
                // of the comparison itself, so a window whose right edge ended EXACTLY at the strip edge
                // already counted as covered - which is the normal case after the window has been moved.
                // As a result the main window shifted its X by 6 points and showed the explanation text
                // although it was long clear.
                float overlap = w.windowRect.x + rect.xMax - limit;
                // Never so far that the X falls out of the window or wanders into its left half -
                // all our windows have content there (title, tab row).
                if (overlap > 0f) shift = Mathf.Min(overlap + 6f, rect.width * 0.4f);
            }
            if (shift <= 0f) return inner.DoClostButtonSmall(rect);

            // Same geometry as Widgets.CloseButtonFor, just shifted left by `shift`.
            Rect r = new Rect(rect.x + rect.width - 18f - 4f - shift, rect.y + 4f, 18f, 18f);
            // Dark background underneath: depending on the window there is content at the shifted spot,
            // and a white X on light text would be clickable but hard to find.
            Widgets.DrawBoxSolid(r.ExpandedBy(3f), new Color(0.12f, 0.12f, 0.13f, 0.92f));
            bool clicked = Widgets.ButtonImage(r, TexButton.CloseXSmall);
            TooltipHandler.TipRegion(r, "CER_ReadoutAwareWindowDrawing_CloseMovedTip".Translate());
            return clicked;
        }
    }
}
