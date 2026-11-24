using Dezolati.Services;

namespace Dezolati.Controls;

/// <summary>
/// Výzdoba eventu na všech stránkách kromě lístku (ten má vlastní s jmelím, sektem a cedulkou – MainPage):
/// světýlka nahoře, sněhová čepice přes každý lístek (Border se stylem Listek) a padající sníh / konfety.
/// Připojí se samo při zobrazení stránky (App.PageAppearing) – stránky o ní nemusí vědět.
/// Předpoklad: kořen stránky je Grid a jeho první dítě je dřevo stolu (tak jsou postavené všechny stránky).
/// </summary>
public static class EventDecor
{
    private const string BackId = "ev-decor-back", FrontId = "ev-decor-front";
    private static readonly System.Runtime.CompilerServices.ConditionalWeakTable<Border, object> OriginalPadding = new();
    private static readonly System.Diagnostics.Stopwatch Clock = System.Diagnostics.Stopwatch.StartNew();
    private static IDispatcherTimer? _timer;
    private static WeakReference<ContentPage>? _current;
    private static List<Border> _listky = new();   // lístky aktuální stránky (hledají se jednou při zobrazení, ne každý snímek)

    public static void Attach(Page page)
    {
        if (page is not ContentPage cp || page is Pages.MainPage || cp.Content is not Grid root) return;
        _current = new(cp);

        var theme = Events.Theme;
        var back = Find(root, BackId);
        var front = Find(root, FrontId);
        if (theme == null && back == null) return;   // event neběží a stránka ještě nic nemá

        if (back == null)
        {
            back = Overlay(BackId);
            root.Children.Insert(Math.Min(1, root.Children.Count), back);   // hned nad dřevo, pod tlačítka a lístky
            front = Overlay(FrontId);
            root.Children.Add(front);
            var backFx = new EventFxDrawable { Scene = (r, t) => EventScene.Lights(r.Width, Events.Theme, t) };
            back.Drawable = backFx;
            front!.Drawable = new EventFxDrawable { Scene = (r, t) => FrontScene(root, r, t) };
        }
        back.IsVisible = front!.IsVisible = theme != null;
        Span(root, back);
        Span(root, front);

        // místo pro čepici nad obsahem lístků
        _listky = Listky(root).ToList();
        foreach (var b in _listky)
        {
            if (!OriginalPadding.TryGetValue(b, out var orig)) OriginalPadding.Add(b, orig = b.Padding);
            var p = (Thickness)orig;
            b.Padding = theme != null ? new Thickness(p.Left, p.Top + 10, p.Right, p.Bottom) : p;
        }

        if (theme == null) { _timer?.Stop(); return; }
        if (_timer == null)
        {
            _timer = cp.Dispatcher.CreateTimer();
            _timer.Interval = TimeSpan.FromMilliseconds(50);
            _timer.Tick += (_, _) => Tick();
        }
        _timer.Start();
        Tick();
    }

    public static void Detach(Page page)
    {
        if (_current != null && _current.TryGetTarget(out var cur) && cur == page) _timer?.Stop();
    }

    private static void Tick()
    {
        if (_current == null || !_current.TryGetTarget(out var page) || page.Content is not Grid root) { _timer?.Stop(); return; }
        var t = Clock.Elapsed.TotalSeconds;
        foreach (var id in new[] { BackId, FrontId })
            if (Find(root, id) is { IsVisible: true } v && v.Drawable is EventFxDrawable d)
            {
                d.Time = t;
                v.Invalidate();
            }
    }

    private static GraphicsView? Find(Grid root, string id) =>
        root.Children.OfType<GraphicsView>().FirstOrDefault(v => v.ClassId == id);

    private static GraphicsView Overlay(string id) => new() { ClassId = id, InputTransparent = true, BackgroundColor = Colors.Transparent };

    private static void Span(Grid root, View v)
    {
        Grid.SetRow(v, 0);
        Grid.SetColumn(v, 0);
        Grid.SetRowSpan(v, Math.Max(1, root.RowDefinitions.Count));
        Grid.SetColumnSpan(v, Math.Max(1, root.ColumnDefinitions.Count));
    }

    /// <summary>Čepice přes viditelné lístky (poloha přepočtená do souřadnic kořene, i v posouvaném seznamu) + sníh.</summary>
    private static List<SceneNode> FrontScene(Grid root, RectF r, double t)
    {
        var n = new List<SceneNode>();
        foreach (var b in _listky)
        {
            if (PositionIn(root, b) is not { } pos || b.Width <= 0) continue;
            n.Add(new SGroup(pos.X, pos.Y - EventScene.CapOverhang, 0, null, EventScene.Cap(b.Width)));
        }
        if (Events.Snow) n.AddRange(EventScene.Particles(r.Width, r.Height, Events.Theme, t));
        return n;
    }

    private static IEnumerable<Border> Listky(Element root)
    {
        if (Application.Current?.Resources.TryGetValue("Listek", out var s) != true || s is not Style listek) yield break;
        foreach (var e in Descendants(root))
            if (e is Border b && b.Style == listek) yield return b;
    }

    private static IEnumerable<Element> Descendants(Element e)
    {
        foreach (var c in ((IVisualTreeElement)e).GetVisualChildren().OfType<Element>())
        {
            yield return c;
            foreach (var d in Descendants(c)) yield return d;
        }
    }

    /// <summary>Levý horní roh prvku v souřadnicích kořene; null = schovaný.</summary>
    private static Point? PositionIn(Element root, VisualElement v)
    {
        double x = 0, y = 0;
        Element? e = v;
        while (e != null && e != root)
        {
            if (e is VisualElement ve)
            {
                if (!ve.IsVisible) return null;
                x += ve.X + ve.TranslationX;
                y += ve.Y + ve.TranslationY;
            }
            if (e.Parent is ScrollView sv) { x -= sv.ScrollX; y -= sv.ScrollY; }
            e = e.Parent;
        }
        return e == root ? new Point(x, y) : null;
    }
}
