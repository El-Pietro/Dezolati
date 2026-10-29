using Dezolati.Controls;
using Dezolati.Services;

namespace Dezolati.Pages;

/// <summary>Hospodské hry: hráči u stolu, šipky a kostky. Všechno jen v telefonu.</summary>
public partial class Hry : ContentPage
{
    private List<string> players = new();
    private int dartsStart = Preferences.Get("hry_sipky_start", 501);
    private bool doubleOut = Preferences.Get("hry_sipky_double", true);
    private bool pairs = Preferences.Get("hry_kostky_dvojicky", true);

    public Hry()
    {
        InitializeComponent();
    }

    protected override async void OnAppearing()
    {
        base.OnAppearing();
        players = GamePlayers.Load();
        if (players.Count == 0)
        {
            await UserSession.EnsureReadyAsync();
            if (UserSession.Profile?.Nickname is string me && me.Length > 0) players.Add(me);
        }
        TargetEntry.Text = Preferences.Get("hry_kostky_cil", 10000).ToString();
        ContinueDarts.IsVisible = DartsGame.LoadSaved(DartsGame.Key) is { Finished: false };
        ContinueDice.IsVisible = DiceGame.LoadSaved(DiceGame.Key) is { Finished: false };
        ContinueDice2.IsVisible = SimpleDiceGame.LoadSaved(SimpleDiceGame.Key) is { Finished: false };
        RenderPlayers();
        UpdateChips();
    }

    // ---------------- hráči ----------------

    private void RenderPlayers()
    {
        PlayersStack.Clear();
        for (var i = 0; i < players.Count; i++)
        {
            var index = i;
            var row = new Grid
            {
                ColumnDefinitions = new ColumnDefinitionCollection(new ColumnDefinition(GridLength.Star), new ColumnDefinition(GridLength.Auto)),
                ColumnSpacing = 6
            };
            row.Add(new Label { Text = $"{i + 1}. {players[i]}", Style = AppStyle("Body"), VerticalOptions = LayoutOptions.Center }, 0, 0);
            var remove = new PencilButton { Text = "Pryč", Variant = PencilVariant.Secondary, WidthRequest = 84, HeightRequest = 38, FontSize = 16 };
            remove.Clicked += (_, _) =>
            {
                players.RemoveAt(index);
                SavePlayers();
            };
            row.Add(remove, 1, 0);
            PlayersStack.Add(row);
        }
        if (players.Count == 0)
            PlayersStack.Add(new Label { Text = "Zatím nikdo. Hrát sám se sebou je smutný.", Style = AppStyle("Small") });
    }

    private void SavePlayers()
    {
        GamePlayers.Save(players);
        RenderPlayers();
    }

    private void OnAddPlayerClicked(object? sender, EventArgs e)
    {
        var name = NewPlayerEntry.Text?.Trim();
        if (string.IsNullOrEmpty(name)) return;
        if (name.Length > 20) name = name[..20];
        players.Add(name);
        NewPlayerEntry.Text = "";
        SavePlayers();
    }

    private void OnShuffleClicked(object sender, EventArgs e)
    {
        players = players.OrderBy(_ => Random.Shared.Next()).ToList();
        SavePlayers();
    }

    // ---------------- volby ----------------

    private void OnStartScoreClicked(object sender, EventArgs e)
    {
        dartsStart = ((PencilButton)sender).CommandParameter as string == "301" ? 301 : 501;
        Preferences.Set("hry_sipky_start", dartsStart);
        UpdateChips();
    }

    private void OnDoubleClicked(object sender, EventArgs e)
    {
        doubleOut = ((PencilButton)sender).CommandParameter as string == "1";
        Preferences.Set("hry_sipky_double", doubleOut);
        UpdateChips();
    }

    private void OnPairsClicked(object sender, EventArgs e)
    {
        pairs = ((PencilButton)sender).CommandParameter as string == "1";
        Preferences.Set("hry_kostky_dvojicky", pairs);
        UpdateChips();
    }

    private void UpdateChips()
    {
        SetChip(Start501, dartsStart == 501);
        SetChip(Start301, dartsStart == 301);
        SetChip(DoubleOn, doubleOut);
        SetChip(DoubleOff, !doubleOut);
        SetChip(PairsOn, pairs);
        SetChip(PairsOff, !pairs);
    }

    private static void SetChip(PencilButton chip, bool selected)
    {
        chip.Variant = selected ? PencilVariant.Primary : PencilVariant.Secondary;
        chip.Opacity = selected ? 1.0 : 0.45;
    }

    // ---------------- start ----------------

    private async Task<bool> ReadyAsync(bool running)
    {
        if (players.Count < 1)
        {
            await Listek.AlertAsync(this, "Kdo hraje?", "Přidej aspoň jednoho hráče.");
            return false;
        }
        return !running || await Listek.ConfirmAsync(this, "Nová hra?", "Rozehraná hra se zahodí.", "Nová hra", danger: true);
    }

    private async void OnDartsClicked(object sender, EventArgs e)
    {
        if (!await ReadyAsync(ContinueDarts.IsVisible)) return;
        var game = new DartsGame { Players = players.ToList(), Start = dartsStart, DoubleOut = doubleOut };
        game.Save();
        await Shell.Current.GoToAsync("//Sipky");
    }

    private async void OnDiceClicked(object sender, EventArgs e)
    {
        var target = int.TryParse(TargetEntry.Text?.Trim(), out var t) && t >= 300 ? t : 10000;
        Preferences.Set("hry_kostky_cil", target);
        if (!await ReadyAsync(ContinueDice.IsVisible)) return;
        var game = new DiceGame
        {
            Players = players.ToList(),
            Totals = players.Select(_ => 0).ToList(),
            Target = target,
            Pairs = pairs
        };
        game.Save();
        await Shell.Current.GoToAsync("//Kostky");
    }

    private async void OnDice2Clicked(object sender, EventArgs e)
    {
        var target = int.TryParse(TargetEntry.Text?.Trim(), out var t) && t >= 300 ? t : 10000;
        Preferences.Set("hry_kostky_cil", target);
        if (!await ReadyAsync(ContinueDice2.IsVisible)) return;
        var game = new SimpleDiceGame
        {
            Players = players.ToList(),
            Totals = players.Select(_ => 0).ToList(),
            Target = target
        };
        game.Save();
        await Shell.Current.GoToAsync("//Kostky2");
    }

    private async void OnContinueDice2Clicked(object sender, EventArgs e) => await Shell.Current.GoToAsync("//Kostky2");

    private async void OnContinueDartsClicked(object sender, EventArgs e) => await Shell.Current.GoToAsync("//Sipky");

    private async void OnContinueDiceClicked(object sender, EventArgs e) => await Shell.Current.GoToAsync("//Kostky");

    private static Style? AppStyle(string key) =>
        Application.Current?.Resources.TryGetValue(key, out var s) == true ? s as Style : null;

    private void OnZpetClicked(object sender, EventArgs e) => OnBackButtonPressed();

    protected override bool OnBackButtonPressed()
    {
        if (Listek.DismissActive()) return true;
        Dispatcher.Dispatch(async () => await Shell.Current.GoToAsync("//MainPage"));
        return true;
    }
}
