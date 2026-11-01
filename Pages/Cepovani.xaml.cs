using System.Diagnostics;
using Dezolati.Controls;
using Dezolati.Services;

namespace Dezolati.Pages;

/// <summary>
/// Čepování hladinky – hra pro jednoho. Prst na obrázku = pípa otevřená, puštění = zavřená.
/// Logika je ve sdíleném <see cref="PourGame"/> (stejná jako na webu), tady se jen kreslí a hlídá čas.
/// </summary>
public partial class Cepovani : ContentPage
{
    private const string BestKey = "hry_cepovani_best";

    private PourGame game = new();
    private readonly SceneDrawable drawable = new();
    private readonly Stopwatch clock = new();
    private double last;
    private bool ticking;

    public Cepovani()
    {
        InitializeComponent();
        Canvas.Drawable = drawable;
        Canvas.StartInteraction += (_, _) => OnDown();
        Canvas.EndInteraction += (_, _) => game.Release();
        Canvas.CancelInteraction += (_, _) => game.Release();
    }

    protected override void OnAppearing()
    {
        base.OnAppearing();
        NewRound();
    }

    protected override void OnDisappearing()
    {
        base.OnDisappearing();
        game.Release();
        ticking = false;
    }

    private void NewRound()
    {
        game = new PourGame();
        var g = game;
        drawable.Scene = () => PourScene.Build(g);
        ResultTitle.IsVisible = ResultText.IsVisible = AgainButton.IsVisible = false;
        HintLabel.Text = "Drž prst na pípě. Pusť, až je pěna u okraje. Na jeden zátah.";
        LiveLabel.Text = "";
        var best = Preferences.Get(BestKey, -1);
        BestLabel.Text = best < 0 ? "" : $"Rekord: {best} bodů";
        Canvas.Invalidate();
    }

    private void OnDown()
    {
        if (game.Press())
        {
            HintLabel.Text = "Teče… pusť, až je pěna u okraje.";
            StartTicking();
        }
        else if (game.State == PourGame.Phase.Settling)
        {
            HintLabel.Text = "Na jeden zátah! Dočepovávat se nesmí.";
        }
    }

    private void StartTicking()
    {
        if (ticking) return;
        ticking = true;
        clock.Restart();
        last = 0;
        Dispatcher.StartTimer(TimeSpan.FromMilliseconds(16), Tick);
    }

    private bool Tick()
    {
        if (!ticking) return false;
        var now = clock.Elapsed.TotalSeconds;
        game.Step(now - last);
        last = now;
        Canvas.Invalidate();

        LiveLabel.Text = game.State == PourGame.Phase.Settling
            ? "Pěna sedá, pivo dochází…"
            : $"Pivo {game.Beer / 1000:0.00} l · pěna {game.FoamFingers:0.0} prstu";

        if (game.State == PourGame.Phase.Done && game.Result is { } r)
        {
            ticking = false;
            ShowResult(r);
            return false;
        }
        return true;
    }

    private void ShowResult(PourResult r)
    {
        HintLabel.Text = "Tak co, výčepní?";
        LiveLabel.Text = $"Pivo {r.BeerMl / 1000:0.00} l · pěna {r.FoamFingers:0.0} prstu"
                         + (r.SpilledMl >= 1 ? $" · na tácku {r.SpilledMl:0} ml" : "");
        ResultTitle.Text = $"{r.Title} – {r.Score} bodů";
        ResultText.Text = r.Comment;
        ResultTitle.IsVisible = ResultText.IsVisible = AgainButton.IsVisible = true;

        var best = Preferences.Get(BestKey, -1);
        if (r.Score > best)
        {
            Preferences.Set(BestKey, r.Score);
            BestLabel.Text = best < 0 ? $"Rekord: {r.Score} bodů" : $"Nový rekord: {r.Score} bodů!";
        }
        try { HapticFeedback.Default.Perform(HapticFeedbackType.Click); } catch { }
    }

    private void OnAgainClicked(object sender, EventArgs e) => NewRound();

    private void OnZpetClicked(object sender, EventArgs e) => OnBackButtonPressed();

    protected override bool OnBackButtonPressed()
    {
        if (Listek.DismissActive()) return true;
        Dispatcher.Dispatch(async () => await Shell.Current.GoToAsync("//Hry"));
        return true;
    }
}
