using System.Globalization;
using Dezolati.Controls;
using Dezolati.Services;

namespace Dezolati.Pages;

/// <summary>Šipky 501 / 301: zapisuje se součet tří šipek, appka hlídá přehození a double out.</summary>
public partial class Sipky : ContentPage
{
    private static readonly CultureInfo Cs = new("cs-CZ");
    private static readonly int[] Quick = { 0, 26, 41, 45, 60, 81, 100, 140 };
    private DartsGame? game;

    public Sipky()
    {
        InitializeComponent();
        for (var i = 0; i < Quick.Length; i++)
        {
            var value = Quick[i];
            var b = new PencilButton
            {
                Text = value.ToString(), Variant = PencilVariant.Secondary,
                HeightRequest = 40, FontSize = 18
            };
            b.Clicked += async (_, _) => await SubmitAsync(value);
            QuickGrid.Add(b, i % 4, i / 4);
        }
    }

    protected override void OnAppearing()
    {
        base.OnAppearing();
        game = DartsGame.LoadSaved(DartsGame.Key);
        InfoLabel.Text = "";
        Render();
    }

    private void Render()
    {
        Board.Clear();
        if (game == null || game.Players.Count == 0)
        {
            TitleLabel.Text = "Šipky";
            TurnLabel.Text = "Žádná hra. Založ ji v Hrách.";
            CheckoutLabel.Text = "";
            InputBox.IsVisible = false;
            UndoButton.IsEnabled = false;
            return;
        }

        TitleLabel.Text = $"Šipky {game.Start}" + (game.DoubleOut ? " · double out" : "");
        for (var i = 0; i < game.Players.Count; i++)
        {
            var current = !game.Finished && i == game.Current;
            var row = new Grid
            {
                ColumnDefinitions = new ColumnDefinitionCollection(
                    new ColumnDefinition(GridLength.Star), new ColumnDefinition(GridLength.Auto), new ColumnDefinition(new GridLength(90))),
                ColumnSpacing = 8
            };
            var name = game.Winner == i ? $"{game.Players[i]} – vítěz!" : game.Players[i] + (current ? "  (hází)" : "");
            row.Add(new Label
            {
                Text = name, Style = AppStyle(current || game.Winner == i ? "Heading" : "Body"),
                VerticalOptions = LayoutOptions.Center, LineBreakMode = LineBreakMode.TailTruncation, MaxLines = 1
            }, 0, 0);
            row.Add(new Label
            {
                Text = game.Remaining(i).ToString(), Style = AppStyle("Heading"),
                VerticalOptions = LayoutOptions.Center, HorizontalTextAlignment = TextAlignment.End
            }, 1, 0);
            row.Add(new Label
            {
                Text = game.VisitsOf(i) > 0 ? $"prům. {game.Average(i).ToString("0", Cs)}" : "",
                Style = AppStyle("Small"), VerticalOptions = LayoutOptions.Center, HorizontalTextAlignment = TextAlignment.End
            }, 2, 0);
            Board.Add(row);
        }

        UndoButton.IsEnabled = game.CanUndo;
        InputBox.IsVisible = !game.Finished;
        if (game.Finished)
        {
            TurnLabel.Text = game.Winner is int w ? $"Vyhrál(a) {game.Players[w]}! 🍻" : "Konec hry";
            CheckoutLabel.Text = "Poražení platí rundu.";
            return;
        }
        var left = game.Remaining(game.Current);
        TurnLabel.Text = $"Hází {game.Players[game.Current]} – zbývá {left}";
        var route = game.DoubleOut ? DartsGame.Checkout(left) : null;
        CheckoutLabel.Text = route != null ? $"Zavření: {route}" : "";
    }

    private async void OnSubmitClicked(object? sender, EventArgs e)
    {
        if (!int.TryParse(PointsEntry.Text?.Trim(), out var points))
        {
            InfoLabel.Text = "Napiš, kolik hodil třemi šipkami.";
            return;
        }
        await SubmitAsync(points);
    }

    private async Task SubmitAsync(int points)
    {
        if (game == null || game.Finished) return;
        var who = game.Players[game.Current];
        var result = game.Submit(points);
        if (result == DartsResult.NeedsDouble)
        {
            var closed = await Listek.ConfirmAsync(this, "Zavřel na double?",
                $"{who} je na nule. Poslední šipka šla do dvojitého pole (nebo bullseye)?", "Ano", "Ne");
            result = game.Submit(points, closed);
        }
        InfoLabel.Text = result switch
        {
            DartsResult.Invalid => $"{points} třemi šipkami hodit nejde.",
            DartsResult.Bust => $"{who} přehodil(a) – kolo neplatí.",
            DartsResult.Win => "",
            _ => $"{who}: {points}"
        };
        if (result != DartsResult.Invalid) PointsEntry.Text = "";
        Render();
    }

    private void OnUndoClicked(object sender, EventArgs e)
    {
        if (game?.Undo() is { } prev)
        {
            game = prev;
            InfoLabel.Text = "Vráceno.";
            Render();
        }
    }

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

    // Zpět jen odejde do Her – hra zůstává uložená a jde v ní pokračovat
    protected override bool OnBackButtonPressed()
    {
        if (Listek.DismissActive()) return true;
        Dispatcher.Dispatch(async () => await Shell.Current.GoToAsync("//Hry"));
        return true;
    }
}
