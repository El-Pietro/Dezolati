namespace Dezolati.Controls
{
    /// <summary>Barvy pastelek pro druhy pití (pořadí: pivo, víno, panáky).</summary>
    public static class Pastelky
    {
        public static readonly Color Pivo = Color.FromArgb("#C27A12");
        public static readonly Color Vino = Color.FromArgb("#8E1B2A");
        public static readonly Color Panak = Color.FromArgb("#4A4A55");
        public static readonly Color Tuha = Color.FromArgb("#6B3E1E");

        public static readonly Color[] Druhy = { Pivo, Vino, Panak };
    }

    public enum SketchMode
    {
        /// <summary>Sloupce vedle sebe, každý složený z druhů.</summary>
        Columns,
        /// <summary>Jeden vodorovný pruh rozdělený podle poměru.</summary>
        Ratio
    }

    /// <summary>
    /// Graf „vybarvený pastelkou“ pro GraphicsView: sloupce/pruhy se vyčmárají cikcakem
    /// a obtáhnou trochu roztřeseným obrysem. Žádný text (popisky jsou obyčejné Labely kolem),
    /// takže se nemusí řešit písmo v canvasu. Bez externí knihovny = žádné další AndroidX peklo.
    /// </summary>
    public sealed class SketchChart : IDrawable
    {
        /// <summary>Hodnoty: pro každý sloupec pole [pivo, víno, panáky].</summary>
        public IReadOnlyList<int[]> Values { get; set; } = Array.Empty<int[]>();

        public SketchMode Mode { get; set; } = SketchMode.Columns;

        /// <summary>Semínko náhody – stejný graf se při překreslení netřese.</summary>
        public int Seed { get; set; } = 7;

        /// <summary>Volitelná čára limitu (např. denní limit databáze) – přerušovaná červená.</summary>
        public int? Limit { get; set; }

        public void Draw(ICanvas canvas, RectF rect)
        {
            canvas.StrokeLineCap = LineCap.Round;
            canvas.StrokeLineJoin = LineJoin.Round;

            if (Mode == SketchMode.Ratio) DrawRatio(canvas, rect);
            else DrawColumns(canvas, rect);
        }

        private void DrawColumns(ICanvas c, RectF r)
        {
            var n = Values.Count;
            if (n == 0 || r.Width <= 0 || r.Height <= 0) return;

            var baseY = r.Bottom - 3;
            var top = r.Top + 4;
            var max = Math.Max(1, Math.Max(Values.Max(v => v.Sum()), Limit ?? 0));
            var slot = r.Width / n;
            var barW = Math.Max(2f, slot * 0.66f);

            for (var i = 0; i < n; i++)
            {
                var rnd = new Random(Seed * 997 + i);
                var x = r.Left + i * slot + (slot - barW) / 2;
                var vals = Values[i];

                if (vals.Sum() == 0)
                {
                    // prázdný den = jen čárka tužkou u země
                    c.StrokeColor = Pastelky.Tuha.WithAlpha(0.3f);
                    c.StrokeSize = 1.2f;
                    c.DrawLine(x + barW * 0.25f, baseY - 2, x + barW * 0.75f, baseY - 2);
                    continue;
                }

                var y = baseY;
                for (var s = 0; s < vals.Length && s < Pastelky.Druhy.Length; s++)
                {
                    if (vals[s] <= 0) continue;
                    var h = (baseY - top) * vals[s] / max;
                    Scribble(c, new RectF(x, y - h, barW, h), Pastelky.Druhy[s], rnd);
                    y -= h;
                }
            }

            WobblyLine(c, r.Left, baseY + 1, r.Right, baseY + 1, new Random(Seed));

            if (Limit is int limit && limit > 0)
            {
                var ly = baseY - (baseY - top) * limit / max;
                c.StrokeColor = Pastelky.Vino.WithAlpha(0.8f);
                c.StrokeSize = 1.3f;
                c.StrokeDashPattern = new float[] { 5, 4 };
                c.DrawLine(r.Left, ly, r.Right, ly);
                c.StrokeDashPattern = null;
            }
        }

        private void DrawRatio(ICanvas c, RectF r)
        {
            var vals = Values.Count > 0 ? Values[0] : Array.Empty<int>();
            var total = vals.Sum();
            var rnd = new Random(Seed);
            var bar = new RectF(r.Left + 1, r.Top + 3, r.Width - 2, r.Height - 6);

            if (total <= 0)
            {
                c.StrokeColor = Pastelky.Tuha.WithAlpha(0.4f);
                c.StrokeSize = 1.1f;
                c.DrawPath(WobblyRect(bar, rnd));
                return;
            }

            var x = bar.Left;
            for (var s = 0; s < vals.Length && s < Pastelky.Druhy.Length; s++)
            {
                if (vals[s] <= 0) continue;
                var w = bar.Width * vals[s] / total;
                Scribble(c, new RectF(x, bar.Top, w, bar.Height), Pastelky.Druhy[s], rnd);
                x += w;
            }
        }

        // ---------------- tužka ----------------

        private static float J(Random rnd, float amount) => (float)(rnd.NextDouble() * 2 - 1) * amount;

        /// <summary>Vyčmárá obdélník cikcakem a obtáhne ho.</summary>
        internal static void Scribble(ICanvas c, RectF rect, Color color, Random rnd)
        {
            if (rect.Width < 0.5f || rect.Height < 0.5f) return;

            var path = new PathF();
            const float step = 2.6f;

            if (rect.Height >= rect.Width)
            {
                // úzký sloupec: tahy zleva doprava, postupně nahoru
                var y = rect.Bottom;
                var right = true;
                path.MoveTo(rect.Left + J(rnd, 0.6f), y);
                while (y > rect.Top)
                {
                    y = Math.Max(rect.Top, y - step * (0.7f + (float)rnd.NextDouble() * 0.6f));
                    path.LineTo((right ? rect.Right : rect.Left) + J(rnd, 0.8f), y);
                    right = !right;
                }
            }
            else
            {
                // široký pruh: tahy shora dolů, postupně doprava
                var x = rect.Left;
                var down = true;
                path.MoveTo(x, rect.Top + J(rnd, 0.6f));
                while (x < rect.Right)
                {
                    x = Math.Min(rect.Right, x + step * (0.7f + (float)rnd.NextDouble() * 0.6f));
                    path.LineTo(x, (down ? rect.Bottom : rect.Top) + J(rnd, 0.8f));
                    down = !down;
                }
            }

            c.StrokeColor = color.WithAlpha(0.75f);
            c.StrokeSize = 1.3f;
            c.DrawPath(path);

            c.StrokeColor = color.WithAlpha(0.95f);
            c.StrokeSize = 1.1f;
            c.DrawPath(WobblyRect(rect, rnd));
        }

        internal static PathF WobblyRect(RectF r, Random rnd)
        {
            const float j = 0.7f;
            var p = new PathF();
            p.MoveTo(r.Left + J(rnd, j), r.Bottom + J(rnd, j));
            p.LineTo(r.Left + J(rnd, j), r.Top + J(rnd, j));
            p.LineTo(r.Right + J(rnd, j), r.Top + J(rnd, j));
            p.LineTo(r.Right + J(rnd, j), r.Bottom + J(rnd, j));
            p.Close();
            return p;
        }

        private static void WobblyLine(ICanvas c, float x1, float y1, float x2, float y2, Random rnd)
        {
            var p = new PathF();
            p.MoveTo(x1, y1 + J(rnd, 0.6f));
            const int parts = 6;
            for (var i = 1; i <= parts; i++)
                p.LineTo(x1 + (x2 - x1) * i / parts, y1 + (y2 - y1) * i / parts + J(rnd, 0.9f));
            c.StrokeColor = Pastelky.Tuha.WithAlpha(0.7f);
            c.StrokeSize = 1.4f;
            c.DrawPath(p);
        }
    }
}
