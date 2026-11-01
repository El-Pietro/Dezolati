using System.Diagnostics;
using Dezolati.Controls;
using Dezolati.Services;

namespace Dezolati.Pages;

/// <summary>
/// Foukání pivního skla – hra pro jednoho. Logika i obrázek sdílené s webem (<see cref="GlassGame"/>, <see cref="GlassScene"/>).
/// Foukat jde tlačítkem (drž) nebo do mikrofonu (jen hlasitost, nic se nenahrává).
/// </summary>
public partial class Sklo : ContentPage
{
    private const string BestKey = "hry_sklo_best";

    private GlassGame game = new();
    private readonly SceneDrawable drawable = new();
    private readonly Stopwatch clock = Stopwatch.StartNew();
    private double last, holdBlow, lastX;
    private bool ticking, micOn;

    public Sklo()
    {
        InitializeComponent();
        drawable.Scene = () => GlassScene.Build(game);
        Canvas.Drawable = drawable;

        Canvas.StartInteraction += (_, e) => { if (e.Touches.Length > 0) Touch(e.Touches[0], start: true); };
        Canvas.DragInteraction += (_, e) => { if (e.Touches.Length > 0) Touch(e.Touches[0], start: false); };
        Canvas.EndInteraction += (_, _) => { game.HandleEnd(); Render(); };

        GatherButton.Pressed += (_, _) => { game.InFurnace = true; StartTicking(); };
        GatherButton.Released += (_, _) => { game.EndGather(); Render(); };
        BlowButton.Released += (_, _) => holdBlow = 0;
        FurnaceButton.Pressed += (_, _) => game.InFurnace = true;
        FurnaceButton.Released += (_, _) => game.InFurnace = false;
    }

    private (double X, double Y) Scene(PointF p)
    {
        var w = Canvas.Width; var h = Canvas.Height;
        if (w <= 0 || h <= 0) return (p.X, p.Y);
        var s = Math.Min(w / 360, h / 400);
        return ((p.X - (w - 360 * s) / 2) / s, (p.Y - (h - 400 * s) / 2) / s);
    }

    private void Touch(PointF p, bool start)
    {
        var (x, y) = Scene(p);
        if (game.State == GlassGame.Phase.Handle)
        {
            if (start) game.HandleStart(x, y); else game.HandleMove(x, y);
            Canvas.Invalidate();
            return;
        }
        if (!start) game.AddRotation(x - lastX);
        lastX = x;
    }

    protected override void OnAppearing()
    {
        base.OnAppearing();
        NewGame();
    }

    protected override void OnDisappearing()
    {
        base.OnDisappearing();
        ticking = false;
        StopMic();
    }

    private void NewGame()
    {
        game = new GlassGame();
        var best = Preferences.Get(BestKey, -1);
        BestLabel.Text = best < 0 ? "" : $"Rekord: {best} bodů";
        Render();
        Canvas.Invalidate();
    }

    /// <summary>Viditelnost tlačítek a nápověda podle fáze.</summary>
    private void Render()
    {
        var s = game.State;
        PickKrygl.IsVisible = PickPokal.IsVisible = PickStamprle.IsVisible = s == GlassGame.Phase.Ready;
        GatherButton.IsVisible = s == GlassGame.Phase.Gather;
        BlowButton.IsVisible = s is GlassGame.Phase.Shape or GlassGame.Phase.Mold;
        FurnaceButton.IsVisible = MoldButton.IsVisible = s == GlassGame.Phase.Shape;
        OpenButton.IsVisible = s == GlassGame.Phase.Mold;
        NoHandleButton.IsVisible = s == GlassGame.Phase.Handle;
        AgainButton.IsVisible = s == GlassGame.Phase.Done;
        MicButton.IsVisible = s != GlassGame.Phase.Done;
        MicButton.Text = micOn ? "Mikrofon: zapnutý" : "Foukat do mikrofonu";
        ResultTitle.IsVisible = ResultText.IsVisible = s == GlassGame.Phase.Done;

        HintLabel.Text = s switch
        {
            GlassGame.Phase.Ready => "Co budeš foukat?",
            GlassGame.Phase.Gather => "Drž a nabírej sklovinu z pece. Pusť v zelené – na jeden zátah.",
            GlassGame.Phase.Shape => (micOn ? "Foukej do mikrofonu" : "Drž Foukat") +
                                     " a pořád toč píšťalou – táhni prstem po obrázku sem a tam. Baňka má dorůst do kroužku. Vychladlé sklo do pece.",
            GlassGame.Phase.Mold => "Dofoukni, ať sklo vyplní celou formu. Moc ne – praskne. Pak Otevřít.",
            GlassGame.Phase.Handle => "Nakresli prstem ucho – od horní tečky k dolní.",
            _ => "Hotovo."
        };
        if (s == GlassGame.Phase.Done && game.Result is { } r)
        {
            ResultTitle.Text = $"{r.Title} – {r.Score} bodů";
            ResultText.Text = r.Comment;
        }
    }

    private void OnPickClicked(object sender, EventArgs e)
    {
        if (sender is PencilButton { CommandParameter: string idx } && int.TryParse(idx, out var i))
        {
            game.Start(GlassShape.All[i]);
            Render();
            StartTicking();
        }
    }

    private void StartTicking()
    {
        if (ticking) return;
        ticking = true;
        last = clock.Elapsed.TotalSeconds;
        Dispatcher.StartTimer(TimeSpan.FromMilliseconds(16), Tick);
    }

    private bool Tick()
    {
        if (!ticking) return false;
        var now = clock.Elapsed.TotalSeconds;
        var dt = now - last;
        last = now;

        // foukání: tlačítko náběh do plné síly za ~0,7 s, mikrofon podle hlasitosti
        holdBlow = BlowButton.IsHeld ? Math.Min(1, holdBlow + dt * 1.4) : 0;
        var mic = 0.0;
#if ANDROID
        if (micOn) mic = Math.Clamp((MicMeter.Level - 0.08) / 0.35, 0, 1);
#endif
        game.Blow = Math.Max(holdBlow, mic);

        var before = game.State;
        game.Step(dt);
        Canvas.Invalidate();
        if (game.State != before) Render();

        if (game.State == GlassGame.Phase.Done)
        {
            ticking = false;
            Finish();
            return false;
        }
        return true;
    }

    private void Finish()
    {
        Render();
        if (game.Result is not { } r) return;
        var best = Preferences.Get(BestKey, -1);
        if (r.Score > best)
        {
            Preferences.Set(BestKey, r.Score);
            BestLabel.Text = best < 0 ? $"Rekord: {r.Score} bodů" : $"Nový rekord: {r.Score} bodů!";
        }
        try { HapticFeedback.Default.Perform(HapticFeedbackType.Click); } catch { }
    }

    private void OnMoldClicked(object sender, EventArgs e) { game.IntoMold(); Render(); }
    private void OnOpenClicked(object sender, EventArgs e)
    {
        game.OpenMold();
        Render();
        if (game.State == GlassGame.Phase.Done) { ticking = false; Finish(); }
        Canvas.Invalidate();
    }
    private void OnNoHandleClicked(object sender, EventArgs e) { game.SkipHandle(); Finish(); Canvas.Invalidate(); }
    private void OnAgainClicked(object sender, EventArgs e) => NewGame();

    private async void OnMicClicked(object sender, EventArgs e)
    {
        if (micOn) { StopMic(); Render(); return; }
#if ANDROID
        var status = await Permissions.RequestAsync<Permissions.Microphone>();
        if (status != PermissionStatus.Granted)
        {
            await Listek.AlertAsync(this, "Bez mikrofonu", "Nevadí – foukej tlačítkem. Mikrofon jde povolit v nastavení telefonu.");
            return;
        }
        micOn = MicMeter.Start();
        if (!micOn) await Listek.AlertAsync(this, "Mikrofon nejde", "Telefon nepustil mikrofon. Foukej tlačítkem.");
        else await Listek.AlertAsync(this, "Foukej do mikrofonu", "Stačí foukat na spodek telefonu. Nic se nenahrává ani neposílá – appka jen měří, jak silně foukáš.");
#else
        await Task.CompletedTask;
#endif
        Render();
    }

    private void StopMic()
    {
#if ANDROID
        MicMeter.Stop();
#endif
        micOn = false;
    }

    private void OnZpetClicked(object sender, EventArgs e) => OnBackButtonPressed();

    protected override bool OnBackButtonPressed()
    {
        if (Listek.DismissActive()) return true;
        Dispatcher.Dispatch(async () => await Shell.Current.GoToAsync("//Hry"));
        return true;
    }
}
