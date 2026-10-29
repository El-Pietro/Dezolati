using System.Globalization;
using Dezolati.Controls;
using Dezolati.Services;

namespace Dezolati.Pages;

/// <summary>
/// Kostky zjednodušené: jen kalkulačka. Pravidla a hody si hlídá stůl, appka sčítá body tahu
/// (Další hod), zapisuje je hráči (Zapsat) nebo tah vynuluje.
/// </summary>
public partial class Kostky2 : ContentPage
{
    private static readonly CultureInfo Cs = new("cs-CZ");
    private SimpleDiceGame? game;
    private string entry = "";      // právě psané body z hodu

    public Kostky2()
    {
        InitializeComponent();
        // klasická klávesnice: 1 2 3 / 4 5 6 / 7 8 9 / Smaž 0 00
        string[] keys = { "1", "2", "3", "4", "5", "6", "7", "8", "9", "Smaž", "0", "00" };
        for (var i = 0; i < keys.Length; i++)
        {
            var key = keys[i];
            var b = new PencilButton
            {
                Text = key, Variant = PencilVariant.Secondary,
                HeightRequest = 42, FontSize = key == "Smaž" ? 17 : 22
            };
            b.Clicked += (_, _) => OnKey(key);
            KeyGrid.Add(b, i % 3, i / 3);
        }
    }

    protected override void OnAppearing()
    {
        base.OnAppearing();
        game = SimpleDiceGame.LoadSaved(SimpleDiceGame.Key);
        entry = "";
        Render();
    }

    private static string N(int v) => v.ToString("#,0", Cs);

    private int EntryValue => int.TryParse(entry, out var v) ? v : 0;

    private void OnKey(string key)
    {
        if (key == "Smaž")
            entry = entry.Length > 0 ? entry[..^1] : "";
        else if (entry.Length < 6 && !(entry.Length == 0 && key.StartsWith('0')))
            entry += key;
        RenderPoints();
    }

    private void Render()
    {
        Board.Clear();
        if (game == null || game.Players.Count == 0)
        {
            TitleLabel.Text = "Kostky";
            TurnLabel.Text = "Žádná hra. Založ ji v Hrách.";
            PointsLabel.Text = EventLabel.Text = "";
            PlayBox.IsVisible = false;
            UndoButton.IsEnabled = false;
            return;
        }

        TitleLabel.Text = $"Kostky do {N(game.Target)}";
        var winners = game.Finished ? game.Winners() : new List<int>();
        for (var i = 0; i < game.Players.Count; i++)
        {
            var current = !game.Finished && i == game.Current;
            var win = winners.Contains(i);
            var row = new Grid
            {
                ColumnDefinitions = new ColumnDefinitionCollection(new ColumnDefinition(GridLength.Star), new ColumnDefinition(GridLength.Auto)),
                ColumnSpacing = 8
            };
            row.Add(new Label
            {
                Text = game.Players[i] + (win ? " – vítěz!" : current ? "  (hází)" : ""),
                Style = AppStyle(current || win ? "Heading" : "Body"),
                VerticalOptions = LayoutOptions.Center, LineBreakMode = LineBreakMode.TailTruncation, MaxLines = 1
            }, 0, 0);
            row.Add(new Label
            {
                Text = N(game.Totals[i]) + " ", Style = AppStyle("Heading"),   // mezera: Caveat přetahuje a konec by se uřízl
                VerticalOptions = LayoutOptions.Center, HorizontalTextAlignment = TextAlignment.End
            }, 1, 0);
            Board.Add(row);
        }

        EventLabel.Text = game.LastEvent ?? "";
        FinalLabel.IsVisible = game.FinalBy != null && !game.Finished;
        if (game.FinalBy is int f)
            FinalLabel.Text = $"Poslední kolo! {game.Players[f]} přehodil(a) {N(game.Target)}.";
        UndoButton.IsEnabled = game.CanUndo;
        PlayBox.IsVisible = !game.Finished;

        if (game.Finished)
        {
            TurnLabel.Text = winners.Count == 1
                ? $"Vyhrál(a) {game.Players[winners[0]]}! 🎲"
                : "Remíza: " + string.Join(", ", winners.Select(w => game.Players[w]));
            PointsLabel.Text = "";
            return;
        }
        TurnLabel.Text = $"Hází {game.Players[game.Current]}";
        RenderPoints();
    }

    /// <summary>„450 + 150“: body z tahu + právě psaný hod.</summary>
    private void RenderPoints()
    {
        if (game == null || game.Finished) return;
        PointsLabel.Text = $"{N(game.TurnPoints)} + {N(EntryValue)}";
    }

    private void OnNextClicked(object sender, EventArgs e)
    {
        if (game == null || EntryValue <= 0) return;
        game.NextRoll(EntryValue);
        entry = "";
        Render();
    }

    private void OnBankClicked(object sender, EventArgs e)
    {
        if (game == null) return;
        game.Bank(EntryValue);
        entry = "";
        Render();
    }

    private async void OnResetClicked(object sender, EventArgs e)
    {
        if (game == null) return;
        if (game.TurnPoints + EntryValue > 0 &&
            !await Listek.ConfirmAsync(this, "Vynulovat?", $"{game.Players[game.Current]} přijde o {N(game.TurnPoints + EntryValue)} a hraje další.", "Vynulovat", danger: true))
            return;
        game.Reset();
        entry = "";
        Render();
    }

    private void OnUndoClicked(object sender, EventArgs e)
    {
        if (game?.Undo() is { } prev)
        {
            game = prev;
            entry = "";
            Render();
        }
    }

    private async void OnHelpTapped(object? sender, TappedEventArgs e) =>
        await Listek.AlertAsync(this, "Návod",
            "Hází se skutečnými kostkami, pravidla si hlídáte sami – appka jen počítá.\n\n" +
            "– Napiš body z hodu na klávesnici (nahoře uvidíš „body z tahu + hod“).\n" +
            "– Další hod – přičte hod k tahu, hází dál stejný hráč.\n" +
            "– Zapsat – tah i s posledním hodem jde hráči do sloupce a hraje další.\n" +
            "– Vynulovat – nic nepadlo (nebo nenahrál 300), tah propadá a hraje další.\n" +
            "– Zpět o krok – oprava překlepu.\n\n" +
            "Kdo přehodí cíl, kolo se dohraje. Vyhrává nejvíc bodů.");

    private async void OnEndClicked(object sender, EventArgs e)
    {
        if (game != null && !game.Finished &&
            !await Listek.ConfirmAsync(this, "Konec hry?", "Rozehraná hra se zahodí.", "Konec", danger: true))
            return;
        game?.Clear();
        await Shell.Current.GoToAsync("//Hry");
    }

    private static Style? AppStyle(string key) =>
        Application.Current?.Resources.TryGetValue(key, out var s) == true ? s as Style : null;

    private void OnZpetClicked(object sender, EventArgs e) => OnBackButtonPressed();

    protected override bool OnBackButtonPressed()
    {
        if (Listek.DismissActive()) return true;
        Dispatcher.Dispatch(async () => await Shell.Current.GoToAsync("//Hry"));
        return true;
    }
}
