using Dezolati.Services;
using Microsoft.Maui.Graphics;

namespace Dezolati.Controls
{
    /// <summary>Mokrá kolečka od piv a vín na lístku (pod čárkami). Rozložení z <see cref="Krouzky"/>.</summary>
    public sealed class WetRingsDrawable : IDrawable
    {
        private static readonly Color Beer = Color.FromArgb("#A0661C");
        private static readonly Color Wine = Color.FromArgb("#7A1C2C");

        public List<WetRing> Rings { get; set; } = new();

        /// <summary>Event: všechna kolečka jednou barvou (svařák / sekt), null = piva a vína zvlášť.</summary>
        public string? ColorOverride { get; set; }

        public void Draw(ICanvas canvas, RectF rect)
        {
            var size = Math.Min(rect.Width, rect.Height * 0.8f);
            foreach (var r in Rings)
            {
                var cx = rect.X + (float)r.X * rect.Width;
                var cy = rect.Y + (float)r.Y * rect.Height;
                var rad = (float)r.R * size;
                var color = (ColorOverride != null ? Color.FromArgb(ColorOverride) : r.Wine ? Wine : Beer).WithAlpha((float)r.Opacity);
                var width = Math.Max(1.5f, (float)r.Width * size);

                // hlavní kruh s mezerou (sklenice se zvedla dřív, než oschla)
                canvas.StrokeColor = color;
                canvas.StrokeSize = width;
                canvas.StrokeLineCap = LineCap.Round;
                var gap = (float)r.Gap;
                canvas.DrawArc(cx - rad, cy - rad, rad * 2, rad * 2, gap + 25, gap + 335, false, false);

                // druhý, slabší otisk kousek vedle – postavená dvakrát
                canvas.StrokeColor = color.WithAlpha((float)r.Opacity * 0.55f);
                canvas.StrokeSize = width * 0.6f;
                var off = rad * 0.08f;
                canvas.DrawArc(cx - rad + off, cy - rad - off, rad * 2, rad * 2, gap + 140, gap + 300, false, false);

                // mokrá skvrna uvnitř
                canvas.FillColor = color.WithAlpha((float)r.Opacity * 0.18f);
                canvas.FillCircle(cx, cy, rad * 0.92f);
            }
        }
    }
}
