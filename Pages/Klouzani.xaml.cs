using System.Diagnostics;
using Dezolati.Controls;
using Dezolati.Services;

namespace Dezolati.Pages;

/// <summary>
/// Klouzání krýglu po pultu – hra pro jednoho. Logika i obrázek jsou sdílené s webem
/// (<see cref="SlideGame"/>, <see cref="SlideScene"/>), tady se jen převádí prst a hlídá čas.
/// </summary>
public partial class Klouzani : ContentPage
{
    private const string BestKey = "hry_klouzani_best";

    private SlideGame game = new();
    private readonly SceneDrawable drawable = new();
    private readonly Stopwatch clock = Stopwatch.StartNew();
    private double last;
    private bool ticking;

    public Klouzani()
    {
        InitializeComponent();
        drawable.Scene = () => SlideScene.Build(game.Current);
        Canvas.Drawable = drawable;
        Canvas.StartInteraction += (_, e) => { if (e.Touches.Length > 0) { game.Current.Down(SceneX(e.Touches[0].X), Now); StartTicking(); } };
        Canvas.DragInteraction += (_, e) => { if (e.Touches.Length > 0) game.Current.Move(SceneX(e.Touches[0].X), Now); };
        Canvas.EndInteraction += (_, _) => game.Current.Up(Now);
        Canvas.CancelInteraction += (_, _) => game.Current.Up(Now);
    }

    private double Now => clock.Elapsed.TotalSeconds;

    /// <summary>Souřadnice prstu → souřadnice scény (360 × 400, vycentrovaná a zvětšená).</summary>
    private double SceneX(float viewX)
    {
        var w = Canvas.Width; var h = Canvas.Height;
        if (w <= 0 || h <= 0) return viewX;
        var scale = Math.Min(w / 360, h / 400);
        return (viewX - (w - 360 * scale) / 2) / scale;
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
    }

    private void NewGame()
    {
        game = new SlideGame();
        ShowRoundStart();
        var best = Preferences.Get(BestKey, -1);
        BestLabel.Text = best < 0 ? "" : $"Rekord: {best} bodů";
    }

    private void ShowRoundStart()
    {
        HintLabel.Text = $"Krýgl {game.Round} z {SlideGame.Rounds}. {game.Current.Surface} Švihni krýglem doprava ke štamgastovi na tácek.";
        ResultTitle.IsVisible = NextButton.IsVisible = false;
        ScoreLabel.Text = game.Total > 0 ? $"Zatím {game.Total} bodů" : "";
        Canvas.Invalidate();
    }

    private void StartTicking()
    {
        if (ticking) return;
        ticking = true;
        last = Now;
        Dispatcher.StartTimer(TimeSpan.FromMilliseconds(16), Tick);
    }

    private bool Tick()
    {
        if (!ticking) return false;
        var now = Now;
        game.Current.Step(now - last);
        last = now;
        Canvas.Invalidate();

        var round = game.Current;
        if (round.State == SlideRound.Phase.Ready) { ticking = false; return false; }   // jen posunul, nepostrčil
        if (round.State != SlideRound.Phase.Done || round.Result is not { } r) return true;

        ticking = false;
        game.Commit();
        ResultTitle.Text = $"{r.Title} – {r.Score} bodů";
        ResultTitle.IsVisible = true;
        if (game.Finished)
        {
            ScoreLabel.Text = $"Celkem {game.Total} z {SlideGame.Rounds * 100} bodů. " + SlideGame.Verdict(game.Total);
            var best = Preferences.Get(BestKey, -1);
            if (game.Total > best)
            {
                Preferences.Set(BestKey, game.Total);
                BestLabel.Text = best < 0 ? $"Rekord: {game.Total} bodů" : $"Nový rekord: {game.Total} bodů!";
            }
            NextButton.Text = "Znovu";
        }
        else
        {
            ScoreLabel.Text = $"Zatím {game.Total} bodů";
            NextButton.Text = "Další krýgl";
        }
        NextButton.IsVisible = true;
        try { HapticFeedback.Default.Perform(r.Broken ? HapticFeedbackType.LongPress : HapticFeedbackType.Click); } catch { }
        return false;
    }

    private void OnNextClicked(object sender, EventArgs e)
    {
        if (game.Finished) NewGame();
        else if (game.Next()) ShowRoundStart();
    }

    private void OnZpetClicked(object sender, EventArgs e) => OnBackButtonPressed();

    protected override bool OnBackButtonPressed()
    {
        if (Listek.DismissActive()) return true;
        Dispatcher.Dispatch(async () => await Shell.Current.GoToAsync("//Hry"));
        return true;
    }
}
