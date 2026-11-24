using Dezolati.Services;

namespace Dezolati.Controls;

/// <summary>
/// Kreslí výzdobu eventu (Services/EventScene.cs) 1 : 1 v souřadnicích GraphicsView.
/// Scene dostane plochu a čas v sekundách (pohyb si scénář spočítá sám).
/// </summary>
public sealed class EventFxDrawable : IDrawable
{
    public Func<RectF, double, List<SceneNode>>? Scene { get; set; }
    public double Time { get; set; }

    public void Draw(ICanvas canvas, RectF rect)
    {
        if (Scene == null || rect.Width < 1 || rect.Height < 1) return;
        canvas.SaveState();
        foreach (var node in Scene(rect, Time)) SceneDrawable.Render(canvas, node);
        canvas.RestoreState();
    }
}
