using Dezolati.Services;

namespace Dezolati.Controls;

/// <summary>
/// Výzdoba akce na stránce (appka): hlavička (pás + roh) do obsahu lístku, tácek a padající věci
/// přes celou stránku (GraphicsView „Fx“ v XAML). Animace 20 snímků/s jen když je co hýbat.
/// </summary>
public sealed class AkceDecorHost
{
    private readonly GraphicsView fx;
    private readonly EventFxDrawable fxDraw, headDraw;
    private readonly GraphicsView head;
    private IDispatcherTimer? timer;
    private static readonly System.Diagnostics.Stopwatch Clock = System.Diagnostics.Stopwatch.StartNew();
    private AkceScene.Look? look;
    private bool running;

    public event Action<string>? ImagesChanged;

    public AkceDecorHost(Page page, GraphicsView fx)
    {
        AkceImages.Init();
        this.fx = fx;
        fxDraw = new EventFxDrawable { Scene = (r, t) => look == null ? new() : AkceScene.Fall(r.Width, r.Height, look, t) };
        fx.Drawable = fxDraw;
        headDraw = new EventFxDrawable { Scene = (r, t) => look == null ? new() : AkceScene.Header(r.Width, look, t) };
        head = new GraphicsView { HeightRequest = AkceScene.HeaderH, InputTransparent = true, Drawable = headDraw, Margin = new Thickness(0, -6, 0, -4) };
        timer = page.Dispatcher.CreateTimer();
        timer.Interval = TimeSpan.FromMilliseconds(33);   // 30 snímků/s
        timer.Tick += (_, _) => Tick();
        AkceImages.Changed += key => { head.Invalidate(); ImagesChanged?.Invoke(key); };
    }

    public AkceScene.Look? Look
    {
        get => look;
        set
        {
            look = value;
            fx.IsVisible = value is { Fall: not "none" };
            head.Invalidate();
            UpdateTimer();
        }
    }

    /// <summary>Hlavička do lístku; null = pás i roh jsou „nic“.</summary>
    public View? Header => look is { } l && (l.Top != "none" || l.Corner != "none") ? head : null;

    /// <summary>Tácek 56 × 56; null = žádný.</summary>
    public View? Coaster()
    {
        if (look is not { Coaster: not "none" } l) return null;
        return new GraphicsView
        {
            WidthRequest = 56, HeightRequest = 56, InputTransparent = true,
            Drawable = new EventFxDrawable { Scene = (_, _) => new() { new SGroup(0, 0, 0, null, AkceScene.Coaster(l), 56 / 64.0) } }
        };
    }

    public void Start() { running = true; UpdateTimer(); }
    public void Stop() { running = false; timer?.Stop(); }

    private void UpdateTimer()
    {
        if (running && look is { Animated: true }) timer?.Start();
        else timer?.Stop();
    }

    private void Tick()
    {
        var t = Clock.Elapsed.TotalSeconds;
        fxDraw.Time = headDraw.Time = t;
        if (fx.IsVisible) fx.Invalidate();
        if (look is { Top: "preset", Preset: "vanoce" }) head.Invalidate();
    }
}
