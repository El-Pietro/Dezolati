using Dezolati.Services;

namespace Dezolati.Controls;

/// <summary>
/// Jeden řádek lístku: čárky propiskou (<see cref="TallyScene"/>) v šířce GraphicsView, bez měřítka.
/// Stejná kresba jako na webu. Dřív obrázky ve FlexLayoutu – ten se v posouvacím lístku na Androidu
/// občas slil do hromádky a pátou čárku otáčel kolem rohu.
/// </summary>
public sealed class CarkyDrawable : IDrawable
{
    public int Count { get; set; }
    public DateTime Day { get; set; }
    public int Kind { get; set; }

    public static double HeightFor(int count, double width) => TallyScene.HeightFor(count, width);

    public void Draw(ICanvas canvas, RectF rect)
    {
        if (Count <= 0 || rect.Width <= 0) return;
        canvas.SaveState();
        canvas.Translate(rect.X, rect.Y);
        foreach (var node in TallyScene.Build(Count, rect.Width, Day, Kind)) SceneDrawable.Render(canvas, node);
        canvas.RestoreState();
    }
}
