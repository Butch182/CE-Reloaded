using System.Collections.Generic;
using UnityEngine;
using Verse;

namespace CEReloaded
{
    // The "red thread": a short overview that names the workflow and how roles + alerts fit in.
    // Opens once automatically the first time a player opens CE Reloaded (Loadout Planner or Arm my Colony); after
    // that only from Mod Settings -> Tools ("Show crash course" / "Reset first-start guide"). The "?" that used to
    // sit in the tab window's header is gone (2026-08-30: no other mod does that).
    //
    // ONE PAGE: the crash course is a single page without scrolling, "Good to know" was dropped and
    // the window grew. Layout: THREE ROWS with an image on the left and text on the right:
    //   1. Fastest path    - image: the Arm my Colony start window (Cautious / Normal / Aggressive)
    //   2. Tabs            - image: the Loadout Planner with its tab bar and the colonist cards
    //   3. Key features    - image: the readiness display with its click menu open
    // The images are textures `UI/Guide/FastestPath|Tabs|KeyFeatures` (Textures/UI/Guide/*.png, 16:9). If one is
    // missing, the text takes the full width - the layout does not depend on the images. They were captured
    // in-game. No scrolling: if a language's text does not fit the height
    // (German, French), the whole text body drops one font size. "Good to know" is gone;
    // the hint where to find the course again sits small in the footer.
    // Animations would be image sequences (one texture per frame, switched over time) - possible, but not
    // built for now since there is no material for them.
    public class Dialog_CERWorkflowGuide : Window
    {
        // ONE place for "show once on the very first run" - previously the same block existed twice (tabs window
        // and the Arm my Colony button in the Assign tab). seenGuide is written immediately so a crash
        // in between doesn't repeat the course on every start.
        public static void OpenIfFirstTime()
        {
            if (CEReloadedMod.settings == null || CEReloadedMod.settings.seenGuide) return;
            CEReloadedMod.settings.seenGuide = true;
            CEReloadedMod.settings.Write();
            Find.WindowStack.Add(new Dialog_CERWorkflowGuide());
        }

        public override Vector2 InitialSize => new Vector2(Mathf.Min(1060f, UI.screenWidth - 40f), Mathf.Min(700f, UI.screenHeight - 60f));
        // Keep clear of RimWorld's learning readout - see LoadoutUtility.KeepClearOfLearningReadout.
        // Without an overlap this changes nothing.
        protected override void SetInitialSizeAndPosition()
        {
            base.SetInitialSizeAndPosition();
            windowRect = LoadoutUtility.KeepClearOfLearningReadout(windowRect);
        }

        public Dialog_CERWorkflowGuide() : base(new ReadoutAwareWindowDrawing())
        {
            doCloseX = true;
            forcePause = true;
            absorbInputAroundWindow = true;
            closeOnClickedOutside = true;
        }

        // Resolved once per process, not per pass: ContentFinder would otherwise hit the disk 3x per frame.
        private static Texture2D[] images;
        private static readonly string[] ImageNames = { "UI/Guide/FastestPath", "UI/Guide/Tabs", "UI/Guide/KeyFeatures" };
        private static Texture2D ImageFor(int i)
        {
            if (images == null)
            {
                images = new Texture2D[ImageNames.Length];
                for (int k = 0; k < ImageNames.Length; k++) images[k] = ContentFinder<Texture2D>.Get(ImageNames[k], false);
            }
            return i >= 0 && i < images.Length ? images[i] : null;
        }

        private struct Section { public string head; public List<string> lines; public int imageIndex; }

        private static Section Make(string headKey, int imageIndex, params string[] lineKeys)
        {
            var s = new Section { head = headKey.Translate(), lines = new List<string>(), imageIndex = imageIndex };
            foreach (string k in lineKeys) s.lines.Add(k.Translate());
            return s;
        }

        private const float ImageMaxW = 330f, RowGap = 16f, LineGap = 4f, HeadH = 24f, ImageTextGap = 18f;
        // Steps for the image width when even Tiny is not enough. 1.0 is included as the first step so
        // the "Tiny alone is enough" case doesn't need to run as a separate special case.
        private static readonly float[] ImageScaleSteps = { 1f, 0.85f, 0.7f, 0.55f, 0.4f };

        public override void DoWindowContents(Rect inRect)
        {
            Text.Font = GameFont.Medium;
            Widgets.Label(new Rect(0, 0, inRect.width, 34f), "CER_WorkflowGuide_Title".Translate());
            Text.Font = GameFont.Small;

            const float top = 44f;
            float footerH = LoadoutUtility.BottomBarH;
            Rect body = new Rect(0, top, inRect.width, inRect.height - top - footerH - 6f);

            var sections = new List<Section>
            {
                Make("CER_WorkflowGuide_HeadFastestPath", 0, "CER_WorkflowGuide_FastestPathLine"),
                // Roles BEFORE "Assign Loadouts": in the game Roles is tab 3 and Assign Loadouts tab 4.
                // TabsLine2 (Weapon Loadouts) was dropped with the lazy-template rework - the tab no longer
                // exists; the numbers in the key texts of the following lines were updated accordingly.
                Make("CER_WorkflowGuide_HeadTabs", 1, "CER_WorkflowGuide_TabsLine1", "CER_WorkflowGuide_TabsLineRoles",
                    "CER_WorkflowGuide_TabsLine3", "CER_WorkflowGuide_TabsLineBills"),
                Make("CER_WorkflowGuide_HeadKeyFeatures", 2, "CER_WorkflowGuide_KeyFeatureOpen", "CER_WorkflowGuide_KeyFeatureReadiness",
                    "CER_WorkflowGuide_KeyFeatureGearTab", "CER_WorkflowGuide_KeyFeatureAmmoGizmo", "CER_WorkflowGuide_KeyFeatureSaveAsRole",
                    "CER_WorkflowGuide_KeyFeatureFetchGear", "CER_WorkflowGuide_KeyFeatureModSettings"),
            };

            // Font size AND image size at which everything fits the height. Measured, not guessed -
            // same lesson as with the old scroll layout (a fixed row height cut sentences off).
            //
            // Second step: "Tiny or nothing" was a dead end. If the text did not fit
            // even in Tiny, it was drawn anyway and ran out of the bottom of the text body - the last
            // line of the "Key features" section disappeared behind the opaque "Got it" button, and the
            // third image reached into the footer, where the grey "Reopen it any time..." line lay
            // across it and was half cut off at the window edge. Both have the same cause: nothing
            // constrained the body to its height.
            //
            // Scrolling is out (the crash course is deliberately one page without scrolling), so the
            // content has to shrink. The biggest item per row is the
            // image: it determines the row height AND takes width away from the text. Shrinking it lowers
            // the row height and the text needs fewer lines - two effects from one knob. So first
            // Small/Tiny, then the image in steps down to 40 %, and the last value checked is also the one
            // drawn: measuring uses exactly the combination that is drawn afterwards.
            GameFont font = GameFont.Small;
            float imgScale = 1f;
            if (MeasureAll(sections, body.width, font, imgScale) > body.height)
            {
                font = GameFont.Tiny;
                foreach (float trial in ImageScaleSteps)
                {
                    imgScale = trial;
                    if (MeasureAll(sections, body.width, font, imgScale) <= body.height) break;
                }
            }

            // And as a last safeguard a group: it CLIPS at the text body. If even the smallest step is not
            // enough for a very long translation, the rest is cut off instead of drawn over the footer -
            // a clipped sentence visibly shows too little space, a sentence behind the "Got it" button
            // looks like a missing sentence.
            Text.Font = font;
            Widgets.BeginGroup(body);
            float y = 0f;
            foreach (Section s in sections)
            {
                y += DrawSection(new Rect(0f, y, body.width, body.height - y), s, font, imgScale);
                y += RowGap;
            }
            Widgets.EndGroup();
            Text.Font = GameFont.Small;

            // Footer: where to find the course again (small, grey), button on the right.
            Text.Font = GameFont.Tiny;
            GUI.color = new Color(1f, 1f, 1f, 0.55f);
            Text.Anchor = TextAnchor.MiddleLeft;
            Widgets.Label(new Rect(0, inRect.height - footerH, inRect.width - LoadoutUtility.CloseBtnW - 12f, LoadoutUtility.BottomBtnH), "CER_WorkflowGuide_GoodToKnowReopen".Translate());
            Text.Anchor = TextAnchor.UpperLeft;
            GUI.color = Color.white;
            Text.Font = GameFont.Small;
            if (LoadoutUtility.ButtonTextFit(new Rect(inRect.width - LoadoutUtility.CloseBtnW, inRect.height - footerH, LoadoutUtility.CloseBtnW, LoadoutUtility.BottomBtnH), "CER_Common_GotIt".Translate().ToString()))
                Close();
        }

        // Image size for a row: on the left, fixed width, height from the texture's aspect ratio (16:9 expected).
        private static Vector2 ImageSize(Texture2D tex, float bodyW, float scale)
        {
            if (tex == null) return Vector2.zero;
            float w = Mathf.Min(ImageMaxW, bodyW * 0.34f) * scale;
            float h = w * tex.height / Mathf.Max(1, tex.width);
            return new Vector2(w, h);
        }

        private static float TextHeight(Section s, float textW)
        {
            float h = HeadH;
            foreach (string l in s.lines) h += Mathf.Max(18f, Text.CalcHeight(l, textW)) + LineGap;
            return h;
        }

        private static float MeasureAll(List<Section> sections, float bodyW, GameFont font, float imgScale)
        {
            GameFont prev = Text.Font;
            Text.Font = font;
            float total = 0f;
            foreach (Section s in sections)
            {
                Vector2 img = ImageSize(ImageFor(s.imageIndex), bodyW, imgScale);
                float textW = img.x > 0f ? bodyW - img.x - ImageTextGap : bodyW;
                total += Mathf.Max(img.y, TextHeight(s, textW)) + RowGap;
            }
            Text.Font = prev;
            return total;
        }

        // Draws one row (image left, heading + lines right) and returns its height.
        private static float DrawSection(Rect area, Section s, GameFont font, float imgScale)
        {
            Texture2D tex = ImageFor(s.imageIndex);
            Vector2 img = ImageSize(tex, area.width, imgScale);
            float textX = area.x, textW = area.width;
            if (tex != null)
            {
                GUI.DrawTexture(new Rect(area.x, area.y, img.x, img.y), tex, ScaleMode.ScaleToFit);
                GUI.color = new Color(1f, 1f, 1f, 0.25f);
                Widgets.DrawBox(new Rect(area.x, area.y, img.x, img.y), 1);
                GUI.color = Color.white;
                textX = area.x + img.x + ImageTextGap;
                textW = area.width - img.x - ImageTextGap;
            }
            Text.Font = font;
            float y = area.y;
            GUI.color = new Color(0.6f, 0.85f, 1f);
            Widgets.Label(new Rect(textX, y, textW, HeadH), s.head);
            GUI.color = Color.white;
            y += HeadH;
            foreach (string l in s.lines)
            {
                float h = Mathf.Max(18f, Text.CalcHeight(l, textW));
                Widgets.Label(new Rect(textX, y, textW, h), l);
                y += h + LineGap;
            }
            return Mathf.Max(img.y, y - area.y);
        }
    }
}
