using Dezolati.Services;

namespace Dezolati.Controls;

/// <summary>
/// Kreslí sdílený scénář her (<see cref="SceneNode"/> – čepování, klouzání…); web kreslí stejný scénář do SVG.
/// Plátno 360 × 400 se zvětší na velikost GraphicsView a vycentruje.
/// </summary>
public sealed class SceneDrawable : IDrawable
{
    public Func<List<SceneNode>>? Scene { get; set; }
    public double Width { get; set; } = 360;
    public double Height { get; set; } = 400;

    private static readonly Dictionary<string, PathF> PathCache = new();
    private static readonly Dictionary<string, Color> ColorCache = new();

    public void Draw(ICanvas canvas, RectF rect)
    {
        if (Scene == null || rect.Width < 50 || rect.Height < 50) return;
        var scale = (float)Math.Min(rect.Width / Width, rect.Height / Height);
        canvas.SaveState();
        canvas.Translate(rect.X + (rect.Width - (float)Width * scale) / 2, rect.Y + (rect.Height - (float)Height * scale) / 2);
        canvas.Scale(scale, scale);
        foreach (var node in Scene()) Render(canvas, node);
        canvas.RestoreState();
    }

    private static PathF PathOf(string d)
    {
        if (PathCache.TryGetValue(d, out var p)) return p;
        p = PathBuilder.Build(d);
        if (PathCache.Count > 400) PathCache.Clear();   // hladiny se mění každý snímek
        PathCache[d] = p;
        return p;
    }

    private static Color ColorOf(string hex)
    {
        if (!ColorCache.TryGetValue(hex, out var c)) ColorCache[hex] = c = Color.FromArgb(hex);
        return c;
    }

    /// <summary>Obrázky pro <see cref="SImage"/> (výzdoba akce) podle klíče „{akce}/{slot}“.</summary>
    public static readonly Dictionary<string, Microsoft.Maui.Graphics.IImage> Images = new();

    internal static void Render(ICanvas canvas, SceneNode node)
    {
        switch (node)
        {
            case SGroup g:
                canvas.SaveState();
                if (g.Tx != 0 || g.Ty != 0) canvas.Translate((float)g.Tx, (float)g.Ty);
                if (g.Rot != 0) canvas.Rotate((float)g.Rot);
                if (g.Scale != 1) canvas.Scale((float)g.Scale, (float)g.Scale);
                if (g.Clip != null) canvas.ClipPath(PathOf(g.Clip));
                foreach (var c in g.Children) Render(canvas, c);
                canvas.RestoreState();
                break;

            case SPath p:
                var path = PathOf(p.D);
                if (p.Gradient is { } gd)
                {
                    // přechod v absolutních souřadnicích → obdélník + relativní body
                    var minX = Math.Min(gd.X1, gd.X2); var minY = Math.Min(gd.Y1, gd.Y2);
                    var w = Math.Max(1, Math.Abs(gd.X2 - gd.X1)); var h = Math.Max(1, Math.Abs(gd.Y2 - gd.Y1));
                    var paint = new LinearGradientPaint(
                        gd.Stops.Select(s => new PaintGradientStop((float)s.Offset, ColorOf(s.Color))).ToArray(),
                        new Point((gd.X1 - minX) / w, (gd.Y1 - minY) / h),
                        new Point((gd.X2 - minX) / w, (gd.Y2 - minY) / h));
                    // Android: přechod (shader) zůstane v plátně viset a další FillColor ho nepřebije –
                    // pak by se všechno další (pěna, okna, postavičky) vybarvilo tímhle přechodem.
                    // Save/Restore vrátí plátno do stavu bez přechodu.
                    canvas.SaveState();
                    canvas.Alpha = (float)p.FillOpacity;
                    canvas.SetFillPaint(paint, new RectF((float)minX, (float)minY, (float)w, (float)h));
                    canvas.FillPath(path);
                    canvas.RestoreState();
                }
                else if (p.Fill != null)
                {
                    canvas.FillColor = ColorOf(p.Fill).WithAlpha((float)p.FillOpacity);
                    canvas.FillPath(path);
                }
                if (p.Stroke != null)
                {
                    canvas.StrokeColor = ColorOf(p.Stroke).WithAlpha((float)p.StrokeOpacity);
                    canvas.StrokeSize = (float)p.StrokeWidth;
                    canvas.StrokeLineCap = p.Round ? LineCap.Round : LineCap.Butt;
                    canvas.StrokeLineJoin = LineJoin.Round;
                    canvas.DrawPath(path);
                }
                break;

            case SText t:
                canvas.FontColor = ColorOf(t.Color);
                canvas.FontSize = (float)t.Size;
                canvas.DrawString(t.Text, (float)t.X, (float)(t.Y - t.Size), 400, (float)t.Size * 1.3f,
                    HorizontalAlignment.Left, VerticalAlignment.Center);
                break;

            case SImage im when Images.TryGetValue(im.Key, out var img):
                canvas.SaveState();
                if (im.Circle) canvas.ClipPath(PathOf(SceneKit.Ellipse(im.X + im.W / 2, im.Y + im.H / 2, im.W / 2, im.H / 2)));
                // výřez na střed (jako CSS cover)
                var scale = Math.Max(im.W / img.Width, im.H / img.Height);
                float dw = (float)(img.Width * scale), dh = (float)(img.Height * scale);
                canvas.DrawImage(img, (float)(im.X + (im.W - dw) / 2), (float)(im.Y + (im.H - dh) / 2), dw, dh);
                canvas.RestoreState();
                break;
        }
    }
}
