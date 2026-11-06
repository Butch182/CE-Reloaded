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
    // EIN-SEITER (2026-08-30): der Crash-Kurs ist eine Seite ohne Scrollen, "Good to know" ist entfallen,
    // das Fenster wurde groesser. Aufbau: DREI ZEILEN mit Bild links und Text rechts:
    //   1. Schnellster Weg   - Bild: das Arm-my-Colony-Startfenster (Cautious / Normal / Aggressive)
    //   2. Reiter            - Bild: der Loadout Planner mit seiner Reiterleiste und den Kolonistenkarten
    //   3. Wichtige Funktionen - Bild: die Bereitschaftsanzeige mit geoeffnetem Klick-Menue
    // Die Bilder sind Texturen `UI/Guide/FastestPath|Tabs|KeyFeatures` (Textures/UI/Guide/*.png, 16:9). Liegt eine
    // nicht da, nimmt der Text die ganze Breite - das Layout haengt nicht an den Bildern. Sie kommen aus dem
    // Screenshot-Lauf im Spiel. Kein Scrollen: passt der Text einer Sprache nicht
    // in die Hoehe (Deutsch, Franzoesisch), faellt der ganze Textkoerper eine Schriftstufe kleiner. "Gut zu wissen"
    // ist weg; der Hinweis, wo der Kurs wieder zu finden ist, steht klein in der Fusszeile.
    // Animationen waeren Bildfolgen (eine Textur je Frame, nach Zeit gewechselt) - moeglich, aber ohne Material
    // erst einmal nicht gebaut.
    public class Dialog_CERWorkflowGuide : Window
    {
        // EINE Stelle fuer "beim allerersten Mal einmal zeigen" - vorher stand derselbe Block zweimal (Tabs-Fenster
        // und Arm-my-Colony-Knopf im Zuweisungs-Reiter). seenGuide wird sofort geschrieben, damit ein Absturz
        // dazwischen den Kurs nicht bei jedem Start wiederholt.
        public static void OpenIfFirstTime()
        {
            if (CEReloadedMod.settings == null || CEReloadedMod.settings.seenGuide) return;
            CEReloadedMod.settings.seenGuide = true;
            CEReloadedMod.settings.Write();
            Find.WindowStack.Add(new Dialog_CERWorkflowGuide());
        }

        public override Vector2 InitialSize => new Vector2(Mathf.Min(1060f, UI.screenWidth - 40f), Mathf.Min(700f, UI.screenHeight - 60f));
        // Nicht unter RimWorlds Lernhilfe schieben (2026-08-19) - siehe
        // LoadoutUtility.KeepClearOfLearningReadout. Ohne Kollision aendert das nichts.
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

        // Einmal je Prozess aufgeloest, nicht je Durchgang: ContentFinder sucht sonst 3x je Bild auf der Platte.
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
        // Stufen fuer die Bildbreite, wenn auch Tiny nicht reicht. 1.0 steht als erste Stufe mit drin, damit
        // der Fall "Tiny allein genuegt" nicht als Sonderfall daneben laufen muss.
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
                // Rollen VOR "Beladungen zuweisen" (2026-08-14): im Spiel ist Roles Reiter 3 und Assign Loadouts Reiter 4.
                // TabsLine2 (Weapon Loadouts) entfiel mit dem Lazy-Umbau (2026-09-01) - der Reiter existiert
                // nicht mehr; die Nummern in den Schluesseltexten der Folgezeilen sind mitgezogen.
                Make("CER_WorkflowGuide_HeadTabs", 1, "CER_WorkflowGuide_TabsLine1", "CER_WorkflowGuide_TabsLineRoles",
                    "CER_WorkflowGuide_TabsLine3", "CER_WorkflowGuide_TabsLineBills"),
                Make("CER_WorkflowGuide_HeadKeyFeatures", 2, "CER_WorkflowGuide_KeyFeatureOpen", "CER_WorkflowGuide_KeyFeatureReadiness",
                    "CER_WorkflowGuide_KeyFeatureGearTab", "CER_WorkflowGuide_KeyFeatureAmmoGizmo", "CER_WorkflowGuide_KeyFeatureSaveAsRole",
                    "CER_WorkflowGuide_KeyFeatureFetchGear", "CER_WorkflowGuide_KeyFeatureModSettings"),
            };

            // Schriftstufe UND Bildgroesse, mit denen alles in die Hoehe passt. Gemessen, nicht geraten -
            // dieselbe Lehre wie beim alten Scroll-Layout (2026-08-14: eine feste Zeilenhoehe schnitt Saetze ab).
            //
            // Zweite Stufe seit 2026-09-04 (Befund im Test): "Tiny oder gar nichts" war eine
            // Sackgasse. Passte der Text auch in Tiny nicht, wurde er trotzdem gezeichnet und lief unten aus
            // dem Textkoerper heraus - die letzte Zeile des Abschnitts "Key features" verschwand hinter dem
            // deckenden "Got it"-Knopf, und das dritte Bild ragte in die Fussleiste, wo die graue Zeile
            // "Reopen it any time..." quer darueber lag und am Fensterrand halb abgeschnitten wurde. Beides
            // ist dieselbe Ursache: nichts hat den Koerper auf seine Hoehe verpflichtet.
            //
            // Scrollen scheidet aus (der Crash-Kurs ist bewusst eine Seite ohne Scrollen), also muss der
            // Inhalt kleiner werden. Der groesste Posten je Zeile ist das
            // Bild: es bestimmt die Zeilenhoehe UND nimmt dem Text die Breite. Schrumpft es, sinkt die
            // Zeilenhoehe und der Text braucht weniger Zeilen - zwei Wirkungen aus einem Regler. Deshalb
            // erst Small/Tiny, dann das Bild in Stufen bis 40 %, und der zuletzt geprueste Wert wird auch
            // gezeichnet: gemessen wird mit genau der Kombination, mit der danach gezeichnet wird.
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

            // Und als letzte Sicherung eine Gruppe: sie SCHNEIDET am Textkoerper ab. Reicht auch die
            // kleinste Stufe einer sehr langen Uebersetzung nicht, wird der Rest abgeschnitten statt ueber
            // die Fussleiste gezeichnet - ein abgeschnittener Satz ist sichtbar zu wenig Platz, ein Satz
            // hinter dem "Got it"-Knopf sieht aus wie ein fehlender Satz.
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

            // Fusszeile: wo der Kurs wiederkommt (klein, grau), rechts der Knopf.
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

        // Bildgroesse fuer eine Zeile: links, feste Breite, Hoehe nach Seitenverhaeltnis der Textur (16:9 erwartet).
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

        // Zeichnet eine Zeile (Bild links, Ueberschrift + Zeilen rechts) und gibt ihre Hoehe zurueck.
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
