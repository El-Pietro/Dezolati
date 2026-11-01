using System.Diagnostics;
using Dezolati.Controls;
using Dezolati.Services;

namespace Dezolati.Pages;

/// <summary>
/// Číšník s tácem – hra pro jednoho. Ovládá se nakláněním telefonu (akcelerometr) a/nebo prstem:
/// držet levou půlku obrázku = naklonit doleva, pravou = doprava. Logika a obrázek jsou sdílené s webem.
/// </summary>
public partial class Cisnik : ContentPage
{
    private const string BestKey = "hry_cisnik_best";
    private const string InvertKey = "hry_cisnik_obratit";

    private WaiterGame game = new();
    private readonly SceneDrawable drawable = new();
    private readonly Stopwatch clock = Stopwatch.StartNew();
    private double last, sensorTilt, touchTarget, touchPart;
    private bool ticking, sensorOn;
    private bool inverted = Preferences.Get(InvertKey, false);

    public Cisnik()
    {
        InitializeComponent();
        drawable.Scene = () => WaiterScene.Build(game);
        Canvas.Drawable = drawable;
        Canvas.StartInteraction += (_, e) => Touch(e.Touches);
        Canvas.DragInteraction += (_, e) => Touch(e.Touches);
        Canvas.EndInteraction += (_, _) => touchTarget = 0;
        Canvas.CancelInteraction += (_, _) => touchTarget = 0;
    }

    private void Touch(PointF[] touches)
    {
        if (touches.Length == 0 || Canvas.Width <= 0) return;
        touchTarget = touches[0].X < Canvas.Width / 2 ? -1 : 1;   // zvednout stranu, na kterou ťukáš
    }

    protected override void OnAppearing()
    {
        base.OnAppearing();
        StartSensor();
        NewGame();
    }

    protected override void OnDisappearing()
    {
        base.OnDisappearing();
        ticking = false;
        StopSensor();
    }

    private void StartSensor()
    {
        try
        {
            if (!Accelerometer.Default.IsSupported) return;
            Accelerometer.Default.ReadingChanged += OnReading;
            if (!Accelerometer.Default.IsMonitoring) Accelerometer.Default.Start(SensorSpeed.Game);
            sensorOn = true;
        }
        catch { sensorOn = false; }
        InvertButton.IsVisible = sensorOn;
    }

    private void StopSensor()
    {
        try
        {
            Accelerometer.Default.ReadingChanged -= OnReading;
            if (Accelerometer.Default.IsMonitoring) Accelerometer.Default.Stop();
        }
        catch { }
        sensorOn = false;
    }

    // telefon na výšku: pravý okraj dolů = X záporné → náklon doprava kladný (°)
    private void OnReading(object? sender, AccelerometerChangedEventArgs e)
    {
        var x = Math.Clamp(e.Reading.Acceleration.X, -1f, 1f);
        sensorTilt = -Math.Asin(x) * 180 / Math.PI * (inverted ? -1 : 1);
    }

    private void NewGame()
    {
        game = new WaiterGame();
        ResultTitle.IsVisible = ResultText.IsVisible = false;
        StartButton.Text = "Vyrazit";
        StartButton.IsVisible = true;
        HintLabel.Text = sensorOn
            ? "Tác se sám naklání. Která strana klesá, na tu půlku obrázku drž prst – zvedneš ji. Nebo nakláněj telefon proti pádu. Moc dlouho ne, přepadne to na druhou stranu."
            : "Tác se sám naklání. Která strana klesá, na tu půlku obrázku drž prst – zvedneš ji. Moc dlouho ne, přepadne to na druhou stranu.";
        var best = Preferences.Get(BestKey, -1);
        BestLabel.Text = best < 0 ? "" : $"Rekord: {best} bodů";
        Canvas.Invalidate();
    }

    private void OnStartClicked(object sender, EventArgs e)
    {
        if (game.State == WaiterGame.Phase.Done) { NewGame(); return; }
        if (game.State != WaiterGame.Phase.Ready) return;
        // „nula“ = jak teď držíš telefon (nikdo ho nedrží úplně rovně)
        zero = sensorTilt;
        game.Start();
        StartButton.IsVisible = false;
        ticking = true;
        last = clock.Elapsed.TotalSeconds;
        Dispatcher.StartTimer(TimeSpan.FromMilliseconds(16), Tick);
    }

    private double zero;

    private bool Tick()
    {
        if (!ticking) return false;
        var now = clock.Elapsed.TotalSeconds;
        var dt = now - last;
        last = now;
        // prst plynule (ruka se nenaklopí skokem)
        touchPart += (touchTarget - touchPart) * Math.Min(1, dt * 8);
        // telefon: náklon doleva (proti pádu doprava) zvedá pravou stranu; plná síla od 15°
        var phone = sensorOn ? -(sensorTilt - zero) / 15 : 0;
        game.Control = Math.Clamp(phone + touchPart, -1, 1);
        game.Step(dt);
        Canvas.Invalidate();

        if (game.State != WaiterGame.Phase.Done || game.Result is not { } r) return true;
        ticking = false;
        ResultTitle.Text = $"{r.Title} – {r.Score} bodů";
        ResultText.Text = r.Comment;
        ResultTitle.IsVisible = ResultText.IsVisible = true;
        var best = Preferences.Get(BestKey, -1);
        if (r.Score > best)
        {
            Preferences.Set(BestKey, r.Score);
            BestLabel.Text = best < 0 ? $"Rekord: {r.Score} bodů" : $"Nový rekord: {r.Score} bodů!";
        }
        StartButton.Text = "Znovu";
        StartButton.IsVisible = true;
        try { HapticFeedback.Default.Perform(HapticFeedbackType.Click); } catch { }
        return false;
    }

    private void OnInvertClicked(object sender, EventArgs e)
    {
        inverted = !inverted;
        Preferences.Set(InvertKey, inverted);
        sensorTilt = -sensorTilt;
        zero = -zero;
        _ = Listek.AlertAsync(this, "Náklon obrácený", inverted ? "Teď se tác naklání opačně než telefon." : "Tác se zase naklání stejně jako telefon.");
    }

    private void OnZpetClicked(object sender, EventArgs e) => OnBackButtonPressed();

    protected override bool OnBackButtonPressed()
    {
        if (Listek.DismissActive()) return true;
        Dispatcher.Dispatch(async () => await Shell.Current.GoToAsync("//Hry"));
        return true;
    }
}
