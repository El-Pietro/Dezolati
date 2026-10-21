namespace Dezolati.Controls
{
    /// <summary>
    /// Vodorovný „teploměr“ vyčmáraný pastelkou: kolik z limitu je vyčerpáno.
    /// Do 60 % hnědá tuha, do 85 % pivní oranžová, nad to vínově červená.
    /// </summary>
    public sealed class SketchMeter : IDrawable
    {
        /// <summary>0 = nic, 1 = limit (víc se ořízne na plný pruh).</summary>
        public double Fraction { get; set; }

        public int Seed { get; set; } = 13;

        public void Draw(ICanvas canvas, RectF rect)
        {
            canvas.StrokeLineCap = LineCap.Round;
            canvas.StrokeLineJoin = LineJoin.Round;

            var bar = new RectF(rect.Left + 1, rect.Top + 3, rect.Width - 2, rect.Height - 6);
            if (bar.Width <= 0 || bar.Height <= 0) return;
            var rnd = new Random(Seed);

            var f = Math.Clamp(Fraction, 0, 1);
            if (f > 0)
            {
                var color = Fraction >= 0.85 ? Pastelky.Vino : Fraction >= 0.6 ? Pastelky.Pivo : Pastelky.Tuha;
                // aspoň kousek, ať je vidět, že něco běží
                var w = Math.Max(3f, (float)(bar.Width * f));
                SketchChart.Scribble(canvas, new RectF(bar.Left, bar.Top, w, bar.Height), color, rnd);
            }

            // obrys celého limitu tužkou
            canvas.StrokeColor = Pastelky.Tuha.WithAlpha(0.55f);
            canvas.StrokeSize = 1.1f;
            canvas.DrawPath(SketchChart.WobblyRect(bar, rnd));
        }
    }
}
