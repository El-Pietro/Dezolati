using System.Diagnostics;
using Dezolati.Controls;
using Dezolati.Services;

namespace Dezolati.Pages;

/// <summary>
/// Vaření piva – hra pro jednoho (dekokce, chmelovar, kvašení). Logika i obrázek sdílené s webem
/// (<see cref="BrewGame"/>, <see cref="BrewScene"/>); uvařená piva jsou jen v telefonu (<see cref="BrewCellar"/>).
/// </summary>
public partial class Varna : ContentPage
{
    private const string BestKey = "hry_varna_best";

    private BrewGame game = new();
    private readonly SceneDrawable drawable = new();
    private readonly Stopwatch clock = Stopwatch.StartNew();
    private double last;
    private bool ticking, saved;
    private string shownKey = "";

    public Varna()
    {
        InitializeComponent();
        drawable.Scene = () => BrewScene.Build(game);
        Canvas.Drawable = drawable;
        PourButton.Pressed += (_, _) => game.Pouring = true;
        PourButton.Released += (_, _) => game.Pouring = false;
        CoolButton.Pressed += (_, _) => game.Cool = true;
        CoolButton.Released += (_, _) => game.Cool = false;
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
        game = new BrewGame();
        saved = false;
        shownKey = "";
        NameEntry.Text = "";
        Render();
        Canvas.Invalidate();
    }

    private void Render()
    {
        var key = $"{game.State}|{game.Sub}|{game.FoamEvent}|{game.CanConfirm}|{game.Hops.Count}";
        if (key == shownKey) return;   // přepínat tlačítka jen při změně (ne 60× za vteřinu)
        shownKey = key;

        var s = game.State;
        var mash = s == BrewGame.Phase.Mash;
        StartButton.IsVisible = s == BrewGame.Phase.Ready;
        TakeButton.IsVisible = mash && game.Sub == BrewGame.MashStep.Take;
        PourButton.IsVisible = RestButton.IsVisible = mash && game.Sub == BrewGame.MashStep.Return;
        RestButton.IsEnabled = game.CanConfirm;
        HopButton.IsVisible = s == BrewGame.Phase.Boil;
        HopButton.IsEnabled = game.Hops.Count < 3;
        CalmButton.IsVisible = s == BrewGame.Phase.Boil && game.FoamEvent;
        CoolButton.IsVisible = s == BrewGame.Phase.Ferment;
        SaveBox.IsVisible = ResultTitle.IsVisible = ResultText.IsVisible = s == BrewGame.Phase.Done;

        HintLabel.Text = s switch
        {
            BrewGame.Phase.Ready => "Vaříme ležák po česku: trojrmutová dekokce, chmelovar, kvašení. Pak ho ochutnají štamgasti.",
            BrewGame.Phase.Mash => game.Sub switch
            {
                BrewGame.MashStep.Take => $"Rmutování {game.Decoction + 1}/3: cíl {game.Target:0} °C ({BrewGame.RestNames[game.Decoction]}). Odeber díl rmutu a povař ho.",
                BrewGame.MashStep.Boiling => "Díl rmutu se povařuje…",
                BrewGame.MashStep.Return => $"Drž a vracej povařený rmut, dokud teploměr neukáže {game.Target:0} °C. Teploměr se opožďuje – pusť o chlup dřív. Pak Prodleva.",
                _ => "Prodleva – rmut odpočívá."
            },
            BrewGame.Phase.Boil => "Chmelovar: přihoď chmel třikrát – v zelených oknech (na hořkost, na chuť, na vůni). Když to kypí, ztlum oheň!",
            BrewGame.Phase.Ferment => "Hlavní kvašení: drž teplotu 8–10 °C. Když se sklep ohřívá, chlaď.",
            _ => "Hotovo. Pojmenuj ho a ulož do sklepa."
        };

        if (s == BrewGame.Phase.Done && game.Result is { } r)
        {
            ResultTitle.Text = $"{r.Title} – {r.Score} bodů";
            ResultText.Text = $"{r.Summary}\n{r.Comment}\nRmut {r.MashScore} · chmel {r.BoilScore} · kvašení {r.FermentScore}";
            if (string.IsNullOrWhiteSpace(NameEntry.Text))
                NameEntry.Text = BrewCellar.Names[Random.Shared.Next(BrewCellar.Names.Length)] + $" {r.Degree:0}°";
            SaveButton.IsEnabled = !saved;
        }
        CellarLabel.Text = s is BrewGame.Phase.Ready or BrewGame.Phase.Done ? "Tvůj sklep:\n" + BrewCellar.Text(BrewCellar.Load()) : "";
    }

    private void OnStartClicked(object sender, EventArgs e)
    {
        game.Start();
        StartTicking();
        Render();
    }

    private void OnTakeClicked(object sender, EventArgs e) { game.TakePortion(); Render(); }
    private void OnRestClicked(object sender, EventArgs e) { game.ConfirmRest(); Render(); }
    private void OnHopClicked(object sender, EventArgs e) { game.AddHop(); Render(); }
    private void OnCalmClicked(object sender, EventArgs e) { game.CalmFire(); Render(); }

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
        game.Step(now - last);
        last = now;
        Canvas.Invalidate();
        Render();
        if (game.State != BrewGame.Phase.Done) return true;

        ticking = false;
        if (game.Result is { } r)
        {
            var best = Preferences.Get(BestKey, -1);
            if (r.Score > best) Preferences.Set(BestKey, r.Score);
        }
        try { HapticFeedback.Default.Perform(HapticFeedbackType.Click); } catch { }
        return false;
    }

    private void OnSaveClicked(object sender, EventArgs e)
    {
        if (saved || game.Result is not { } r) return;
        var name = string.IsNullOrWhiteSpace(NameEntry.Text) ? "Bezejmenné" : NameEntry.Text.Trim();
        BrewCellar.Add(new BrewCellar.Beer(name, r.Degree, r.Abv, r.Ibu, r.Score, DateTime.Now));
        saved = true;
        SaveButton.IsEnabled = false;
        CellarLabel.Text = "Tvůj sklep:\n" + BrewCellar.Text(BrewCellar.Load());
    }

    private void OnAgainClicked(object sender, EventArgs e) => NewGame();

    private void OnZpetClicked(object sender, EventArgs e) => OnBackButtonPressed();

    protected override bool OnBackButtonPressed()
    {
        if (Listek.DismissActive()) return true;
        Dispatcher.Dispatch(async () => await Shell.Current.GoToAsync("//Hry"));
        return true;
    }
}
