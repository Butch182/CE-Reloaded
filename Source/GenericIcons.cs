using System;
using UnityEngine;
using Verse;
using CombatExtended;

namespace CEReloaded
{
    // CE's "Any Medicine"/"Any Perishable Meal"/"Any Raw Food"/"Any Drugs" generics have no ThingDef/graphic
    // of their own - everywhere they show up as a picker or a list row, this mod used to borrow a random
    // matching item's icon (e.g. a single specific pill standing in for "Any Drugs"), which reads as
    // arbitrary rather than as "this is a placeholder". Small hand-drawn badges instead - one per generic,
    // not just per pack category, so "Any Perishable Meal" and "Any Raw Food" (both PackCategory.Food) still
    // read as two different things - baked once into a tiny Texture2D (transparent background, flat-colored
    // silhouette) and cached, cheap to reuse everywhere a generic needs an icon (list rows, FloatMenu options).
    // Loads its own static Texture2D fields - see Dialog_AssignLoadouts' own identical comment for why this
    // attribute is required (2026-08-01).
    [StaticConstructorOnStartup]
    public static class GenericIcons
    {
        private const int Size = 32;

        private static Texture2D medical, drugs, meal, rawFood, foodOther;

        // The primary entry point - looks at the SPECIFIC generic first (only Food currently has more than
        // one shape), falling back to a per-category badge for anything else (Medical/Drugs, or a modded
        // Food generic that isn't CE's own "GenericMeal"/"GenericRawFood").
        public static Texture2D For(LoadoutGenericDef gd)
        {
            PackCategory cat = LoadoutUtility.GenericCategoryOf(gd) ?? PackCategory.Other;
            if (cat == PackCategory.Food)
            {
                if (gd?.defName == "GenericRawFood") return rawFood ?? (rawFood = FromPngOr("Icon_AnyRawFood", PaintCarrot, new Color(0.80f, 0.45f, 0.15f)));
                if (gd?.defName == "GenericMeal") return meal ?? (meal = FromPngOr("Icon_AnyMeal", PaintMeal, new Color(0.85f, 0.72f, 0.30f)));
                return foodOther ?? (foodOther = FromPngOr("Icon_AnyFood", PaintApple, new Color(0.85f, 0.72f, 0.30f)));
            }
            return For(cat);
        }

        // Grundbild aus einem austauschbaren PNG, das "A" weiterhin aus Code darueber (2026-08-09): das Bild
        // dahinter laesst sich durch Austauschen der PNG-Datei ersetzen, das "A" rechts unten wird weiter erzeugt.
        //
        // Der Umweg ueber eine RenderTexture ist der Grund, warum das nicht in fuenf Minuten ging: eine ueber
        // ContentFinder geladene Textur ist NICHT lesbar, GetPixels/SetPixels wirft darauf. Man kann das "A"
        // also nicht einfach hineinstempeln. Blit erzeugt eine lesbare Kopie, in die es geht.
        // Laeuft genau einmal pro Symbol (die Felder darueber cachen), also beim ersten Zeichnen, nicht pro
        // Bild - und bewusst NICHT beim Laden des Mods: eine RenderTexture zu diesem Zeitpunkt ist heikel.
        //
        // Fehlt das PNG, wird weiter gemalt wie bisher. Ein geloeschtes Bild darf den Mod nicht kaputtmachen.
        private static Texture2D FromPngOr(string pngName, Action<Color[], Color> fallbackPaint, Color accent)
        {
            Texture2D src = ContentFinder<Texture2D>.Get("UI/" + pngName, false);
            if (src == null) return Build(fallbackPaint, accent);
            RenderTexture rt = RenderTexture.GetTemporary(Size, Size, 0, RenderTextureFormat.ARGB32);
            RenderTexture prevActive = RenderTexture.active;
            try
            {
                Graphics.Blit(src, rt);
                RenderTexture.active = rt;
                Texture2D copy = new Texture2D(Size, Size, TextureFormat.RGBA32, false) { filterMode = FilterMode.Bilinear };
                copy.ReadPixels(new Rect(0, 0, Size, Size), 0, 0);
                Color[] px = copy.GetPixels();
                PaintAnyBadge(px, Size * 0.78f, Size * 0.22f, Size * 0.24f);
                copy.SetPixels(px);
                copy.Apply();
                return copy;
            }
            catch (Exception e)
            {
                // Lieber das gemalte Symbol als gar keins - ein Fehler hier ist ein Schoenheitsproblem,
                // kein Grund, die Zeile leer zu lassen.
                Log.Warning("[CE Reloaded] " + pngName + " konnte nicht uebernommen werden, benutze das "
                    + "gezeichnete Symbol.\n" + e);
                return Build(fallbackPaint, accent);
            }
            finally
            {
                RenderTexture.active = prevActive;
                RenderTexture.ReleaseTemporary(rt);
            }
        }

        // Category-only fallback, for callers that only have a PackCategory in hand (not the generic itself).
        public static Texture2D For(PackCategory cat)
        {
            switch (cat)
            {
                case PackCategory.Medical: return medical ?? (medical = FromPngOr("Icon_AnyMedical", PaintCross, new Color(0.85f, 0.35f, 0.35f)));
                case PackCategory.Food: return foodOther ?? (foodOther = FromPngOr("Icon_AnyFood", PaintApple, new Color(0.85f, 0.72f, 0.30f)));
                case PackCategory.Drugs: return drugs ?? (drugs = FromPngOr("Icon_AnyDrugs", PaintCapsule, new Color(0.68f, 0.48f, 0.88f)));
                default: return null;
            }
        }

        private static Texture2D Build(Action<Color[], Color> paint, Color accent)
        {
            Texture2D tex = new Texture2D(Size, Size, TextureFormat.RGBA32, false) { filterMode = FilterMode.Bilinear };
            Color[] px = new Color[Size * Size];
            paint(px, accent);
            // Every generic badge gets the same small "A" (for "Any") corner marker, so a placeholder
            // reads as a placeholder at a glance instead of looking like a real, specific item.
            PaintAnyBadge(px, Size * 0.78f, Size * 0.22f, Size * 0.24f);
            tex.SetPixels(px);
            tex.Apply();
            return tex;
        }

        private static void Set(Color[] px, int x, int y, Color c)
        {
            if (x < 0 || y < 0 || x >= Size || y >= Size) return;
            px[y * Size + x] = c;
        }

        // Distance from point (px,py) to the segment (x1,y1)-(x2,y2) - used to rasterize simple letterforms.
        private static float DistToSegment(float px, float py, float x1, float y1, float x2, float y2)
        {
            float dx = x2 - x1, dy = y2 - y1;
            float lenSq = dx * dx + dy * dy;
            float t = lenSq > 0f ? Mathf.Clamp01(((px - x1) * dx + (py - y1) * dy) / lenSq) : 0f;
            float projX = x1 + t * dx, projY = y1 + t * dy;
            float ddx = px - projX, ddy = py - projY;
            return Mathf.Sqrt(ddx * ddx + ddy * ddy);
        }

        // A small dark circular badge with a bold white "A" - stamped onto every generic's badge (corner
        // accent, drawn last so it always sits on top of the main shape underneath).
        private static void PaintAnyBadge(Color[] px, float bcx, float bcy, float br)
        {
            Color bg = new Color(0.10f, 0.10f, 0.12f, 0.95f);
            Color fg = Color.white;
            float apexX = bcx, apexY = bcy + br * 0.62f;
            float baseY = bcy - br * 0.62f;
            float baseLX = bcx - br * 0.55f, baseRX = bcx + br * 0.55f;
            float barY = bcy - br * 0.05f;
            float tBar = (apexY - barY) / (apexY - baseY);
            float barLX = Mathf.Lerp(apexX, baseLX, tBar);
            float barRX = Mathf.Lerp(apexX, baseRX, tBar);
            int x0 = Mathf.Max(0, Mathf.FloorToInt(bcx - br - 1f)), x1 = Mathf.Min(Size - 1, Mathf.CeilToInt(bcx + br + 1f));
            int y0 = Mathf.Max(0, Mathf.FloorToInt(bcy - br - 1f)), y1 = Mathf.Min(Size - 1, Mathf.CeilToInt(bcy + br + 1f));
            for (int y = y0; y <= y1; y++)
                for (int x = x0; x <= x1; x++)
                {
                    float dxp = x - bcx, dyp = y - bcy;
                    if (Mathf.Sqrt(dxp * dxp + dyp * dyp) > br) continue;
                    bool onLetter = DistToSegment(x, y, apexX, apexY, baseLX, baseY) <= 1.3f
                        || DistToSegment(x, y, apexX, apexY, baseRX, baseY) <= 1.3f
                        || DistToSegment(x, y, barLX, barY, barRX, barY) <= 1.1f;
                    Set(px, x, y, onLetter ? fg : bg);
                }
        }

        // A plain colored plus sign, no background disc - the universal "medical" glyph. Sized to match the
        // other badges' silhouette-only footprint (was previously a tinted background circle behind a white
        // cross, which read visibly larger/bolder than Food/Drugs at the same icon size).
        private static void PaintCross(Color[] px, Color accent)
        {
            float c = (Size - 1) / 2f;
            float armHalf = Size * 0.10f, armLen = Size * 0.32f;
            for (int y = 0; y < Size; y++)
                for (int x = 0; x < Size; x++)
                {
                    float dx = x - c, dy = y - c;
                    bool inH = Mathf.Abs(dy) <= armHalf && Mathf.Abs(dx) <= armLen;
                    bool inV = Mathf.Abs(dx) <= armHalf && Mathf.Abs(dy) <= armLen;
                    if (inH || inV) Set(px, x, y, accent);
                }
        }

        // A two-tone capsule (stadium shape, cream/accent halves) - the universal "medicine/drug" glyph.
        private static void PaintCapsule(Color[] px, Color accent)
        {
            float cy = (Size - 1) / 2f;
            float r = Size * 0.20f;
            float x0 = Size * 0.20f + r, x1 = Size * 0.80f - r;
            float cx = (x0 + x1) / 2f;
            Color capA = new Color(0.96f, 0.94f, 0.90f);
            for (int y = 0; y < Size; y++)
                for (int x = 0; x < Size; x++)
                {
                    float dy = y - cy;
                    bool inBody = x >= x0 && x <= x1 && Mathf.Abs(dy) <= r;
                    bool inLeftCap = x < x0 && Mathf.Sqrt((x - x0) * (x - x0) + dy * dy) <= r;
                    bool inRightCap = x > x1 && Mathf.Sqrt((x - x1) * (x - x1) + dy * dy) <= r;
                    if (!inBody && !inLeftCap && !inRightCap) continue;
                    Set(px, x, y, x < cx ? capA : accent);
                }
        }

        // A small round fruit with a stem and a leaf - fallback "food, unspecified" glyph (any Food generic
        // that isn't specifically CE's "Any Perishable Meal"/"Any Raw Food").
        private static void PaintApple(Color[] px, Color accent)
        {
            float cx = (Size - 1) / 2f, cy = Size * 0.44f;
            float r = Size * 0.34f;
            Color stem = new Color(0.45f, 0.30f, 0.18f);
            Color leaf = new Color(0.40f, 0.70f, 0.30f);
            for (int y = 0; y < Size; y++)
                for (int x = 0; x < Size; x++)
                {
                    float dx = x - cx, dy = y - cy;
                    if (Mathf.Sqrt(dx * dx + dy * dy * 1.05f) <= r) { Set(px, x, y, accent); continue; }
                    // Stem: short vertical sliver above the body (larger y = higher up in this texture -
                    // Unity flips the row order between SetPixels and GUI.DrawTexture).
                    if (Mathf.Abs(dx - r * 0.05f) <= 1.2f && dy > r * 0.7f && dy < r * 1.25f) { Set(px, x, y, stem); continue; }
                    // Leaf: small blob off the top of the stem.
                    float lx = dx - r * 0.55f, ly = dy - r * 1.05f;
                    if (Mathf.Sqrt(lx * lx * 1.6f + ly * ly) <= r * 0.34f) Set(px, x, y, leaf);
                }
        }

        // A shallow plate with a food mound and two steam wisps - "Any Perishable Meal" (a cooked, hot dish -
        // the one that spoils, unlike raw ingredients).
        private static void PaintMeal(Color[] px, Color accent)
        {
            float cx = (Size - 1) / 2f, cy = Size * 0.38f;
            float rx = Size * 0.36f, ry = Size * 0.14f;
            Color plate = new Color(0.85f, 0.85f, 0.85f);
            Color steam = new Color(0.8f, 0.8f, 0.8f, 0.85f);
            // Steam rises from just above the plate's rim toward the top of the icon (larger y = higher up
            // in this texture - Unity flips the row order between SetPixels and GUI.DrawTexture; the
            // earlier version used smaller y for "up", which put the steam as a disconnected smudge at the
            // opposite edge instead of trailing off the plate).
            float steamBase = cy + ry, steamTop = steamBase + Size * 0.26f;
            for (int y = 0; y < Size; y++)
                for (int x = 0; x < Size; x++)
                {
                    float dx = (x - cx) / rx, dy = (y - cy) / ry;
                    float d2 = dx * dx + dy * dy;
                    if (d2 <= 1f) { Set(px, x, y, d2 <= 0.5f ? accent : plate); continue; }
                    if (y < steamBase || y > steamTop) continue;
                    float t = (y - steamBase) / (steamTop - steamBase);
                    float s1x = (cx - rx * 0.32f) + t * (Size * 0.10f);
                    float s2x = (cx + rx * 0.32f) - t * (Size * 0.10f);
                    if (Mathf.Abs(x - s1x) <= 1f || Mathf.Abs(x - s2x) <= 1f) Set(px, x, y, steam);
                }
        }

        // A tapering root with a small leaf tuft - "Any Raw Food" (unprepared, shelf-stable-ish ingredients,
        // as opposed to a cooked meal). Wide, leafy end near the visual top (larger y), tapering to a point
        // near the visual bottom (smaller y - Unity flips the row order between SetPixels and
        // GUI.DrawTexture, so "top" and "bottom" are the opposite of what the row index suggests).
        private static void PaintCarrot(Color[] px, Color accent)
        {
            float topX = Size * 0.36f, topY = Size * 0.80f;
            float tipX = Size * 0.70f, tipY = Size * 0.18f;
            float halfW = Size * 0.13f;
            Color leaf = new Color(0.40f, 0.70f, 0.30f);
            for (int y = 0; y < Size; y++)
                for (int x = 0; x < Size; x++)
                {
                    float t = (topY - y) / (topY - tipY);
                    if (t >= 0f && t <= 1f)
                    {
                        float axisX = Mathf.Lerp(topX, tipX, t);
                        float w = halfW * (1f - t);
                        if (Mathf.Abs(x - axisX) <= w) { Set(px, x, y, accent); continue; }
                    }
                    float lx = x - topX, ly = y - (topY + Size * 0.06f);
                    if (Mathf.Sqrt(lx * lx * 1.3f + ly * ly * 2.2f) <= Size * 0.12f) Set(px, x, y, leaf);
                }
        }
    }
}
