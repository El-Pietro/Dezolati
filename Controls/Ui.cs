namespace Dezolati.Controls;

/// <summary>Stavebnice pro stránky skládané v kódu (akce): styly z App.xaml, malá tlačítka, řádky.</summary>
public static class Ui
{
    public static Style? S(string key) =>
        Application.Current?.Resources.TryGetValue(key, out var s) == true ? s as Style : null;

    public static Color InkSoft =>
        Application.Current?.Resources.TryGetValue("InkSoft", out var c) == true && c is Color color ? color : Colors.Gray;

    // mezera na konci: Caveat na Androidu jinak ořízne poslední písmeno
    public static Label Title(string t) => new() { Text = t + " ", Style = S("Title") };
    public static Label H(string t) => new() { Text = t + " ", Style = S("Heading") };
    public static Label Body(string t) => new() { Text = t + " ", Style = S("Body") };
    public static Label Small(string t, bool soft = false)
    {
        var l = new Label { Text = t + " ", Style = S("Small") };
        if (soft) l.TextColor = InkSoft;
        return l;
    }
    public static Label Field(string t) => new() { Text = t + " ", Style = S("FieldLabel") };

    public static PencilButton Btn(string text, PencilVariant v = PencilVariant.Primary, bool small = true, EventHandler? click = null)
    {
        var b = new PencilButton { Text = text, Variant = v };
        if (small)
        {
            b.FontSize = (double)(Application.Current?.Resources["FontButtonSmall"] ?? 20.0);
            b.HeightRequest = (double)(Application.Current?.Resources["ButtonHeightSmall"] ?? 42.0);
        }
        if (click != null) b.Clicked += click;
        return b;
    }

    /// <summary>Vedle sebe stejně široké.</summary>
    public static Grid Cols(params View[] views)
    {
        var g = new Grid { ColumnSpacing = 8 };
        for (var i = 0; i < views.Length; i++)
        {
            g.ColumnDefinitions.Add(new ColumnDefinition(GridLength.Star));
            g.Add(views[i], i, 0);
        }
        return g;
    }

    public static void Chip(PencilButton b, bool on)
    {
        b.Variant = on ? PencilVariant.Primary : PencilVariant.Secondary;
        b.Opacity = on ? 1.0 : 0.45;
    }

    public static T Tap<T>(this T view, Action action) where T : View
    {
        var t = new TapGestureRecognizer();
        t.Tapped += (_, _) => action();
        view.GestureRecognizers.Add(t);
        return view;
    }
}
