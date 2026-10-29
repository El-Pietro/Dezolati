using System.Globalization;
using Dezolati.Controls;
using Dezolati.Services;

namespace Dezolati.Pages;

/// <summary>
/// Kostky (deset tisíc podle hospodských pravidel). Hází se skutečnými kostkami a do appky se ťukají
/// odložené kostky – nebo hodí appka. Appka počítá body, hlídá 300 do 3 hodů, horké kostky a dohrání kola.
/// </summary>
public partial class Kostky : ContentPage
{
    private static readonly CultureInfo Cs = new("cs-CZ");
    private DiceGame? game;
    private readonly List<int> selection = new();
    private List<int>? roll;                     // hod appkou
    private readonly HashSet<int> usedFromRoll = new();   // indexy kostek z hodu, které už jsou odložené

    public Kostky()
    {
        InitializeComponent();
        for (var face = 1; face <= 6; face++)
        {
            var f = face;
            var b = new PencilButton { Text = f.ToString(), Variant = PencilVariant.Secondary, HeightRequest = 44, FontSize = 20 };
            b.Clicked += (_, _) => AddDie(f);
            PadGrid.Add(b, face - 1, 0);
        }
        var clear = new PencilButton { Text = "Smazat", Variant = PencilVariant.Secondary, HeightRequest = 44, FontSize = 16, WidthRequest = 78 };
        clear.Clicked += (_, _) => ClearSelection();
        PadGrid.Add(clear, 6, 0);
    }

    protected override void OnAppearing()
    {
        base.OnAppearing();
        game = DiceGame.LoadSaved(DiceGame.Key);
        ResetTurnUi();
        Render();
    }

    private static string N(int v) => v.ToString("#,0", Cs);

    // ---------------- vykreslení ----------------

    private void Render()
    {
        Board.Clear();
        if (game == null || game.Players.Count == 0)
        {
            TitleLabel.Text = "Kostky";
            TurnLabel.Text = "Žádná hra. Založ ji v Hrách.";
            TurnInfo.Text = HintLabel.Text = EventLabel.Text = "";
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
                Text = N(game.Totals[i]), Style = AppStyle("Heading"),
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
            TurnInfo.Text = "Poražení platí rundu.";
            HintLabel.Text = "";
            return;
        }

        TurnLabel.Text = $"Hází {game.Players[game.Current]}";
        TurnInfo.Text = game.TurnPoints == 0 && game.Rolls == 0
            ? $"Nový tah: do {DiceGame.RollsForMin} hodů aspoň {DiceGame.MinTurn}"
            : $"V tahu {N(game.TurnPoints)} · {game.Rolls}. hod za sebou · zbývá kostek {game.DiceLeft}";
        HintLabel.Text = game.HotDice
            ? "Všech 6 bodovalo – musíš házet znovu všemi šesti!"
            : game.NeedsMin && game.Rolls > 0
                ? $"Do {DiceGame.MinTurn} chybí {DiceGame.MinTurn - game.TurnPoints}, zbývá {game.RollsLeftForMin} {Cz.Plural(game.RollsLeftForMin, "hod", "hody", "hodů")}."
                : !game.NeedsMin ? "Můžeš zapsat, nebo riskovat dál." : "";
        RenderSelection();
        RenderRoll();
    }

    private void RenderSelection()
    {
        if (game == null) return;
        if (selection.Count == 0)
        {
            SelectionLabel.Text = "Ťukni, které kostky odkládáš:";
            return;
        }
        var score = DiceGame.Score(selection, game.Pairs);
        var dice = string.Join(" ", selection.OrderBy(d => d));
        SelectionLabel.Text = score is int s ? $"Odkládám {dice} = {N(s)}" : $"Odkládám {dice} – tohle se nepočítá";
    }

    private void RenderRoll()
    {
        RollStack.Clear();
        RollButton.IsVisible = roll == null;
        if (roll == null || game == null) return;
        for (var i = 0; i < roll.Count; i++)
        {
            var index = i;
            var used = usedFromRoll.Contains(i);
            var b = new PencilButton
            {
                Text = roll[i].ToString(), Variant = used ? PencilVariant.Primary : PencilVariant.Secondary,
                WidthRequest = 46, HeightRequest = 46, FontSize = 22, Opacity = used ? 0.5 : 1
            };
            b.Clicked += (_, _) => ToggleRollDie(index);
            RollStack.Add(b);
        }
        if (!DiceGame.AnyScore(roll, game.Pairs))
            HintLabel.Text = "Nic nepadlo. Ťukni na „Nic nepadlo“.";
    }

    // ---------------- odkládání ----------------

    private void AddDie(int face)
    {
        if (game == null || selection.Count >= game.DiceLeft) return;
        selection.Add(face);
        RenderSelection();
    }

    private void ToggleRollDie(int index)
    {
        if (roll == null) return;
        if (usedFromRoll.Remove(index)) selection.Remove(roll[index]);
        else
        {
            usedFromRoll.Add(index);
            selection.Add(roll[index]);
        }
        RenderSelection();
        RenderRoll();
    }

    private void ClearSelection()
    {
        selection.Clear();
        usedFromRoll.Clear();
        RenderSelection();
        RenderRoll();
    }

    private void ResetTurnUi()
    {
        selection.Clear();
        usedFromRoll.Clear();
        roll = null;
    }

    private void OnRollClicked(object sender, EventArgs e)
    {
        if (game == null) return;
        roll = Enumerable.Range(0, game.DiceLeft).Select(_ => Random.Shared.Next(1, 7)).OrderBy(d => d).ToList();
        selection.Clear();
        usedFromRoll.Clear();
        Render();
    }

    // ---------------- akce ----------------

    private void OnKeepClicked(object sender, EventArgs e) => Act(game => game.Keep(selection.ToList(), bank: false));

    private void OnBankClicked(object sender, EventArgs e) =>
        Act(game => selection.Count > 0 ? game.Keep(selection.ToList(), bank: true) : game.Bank());

    private void OnNothingClicked(object sender, EventArgs e) => Act(game => game.Nothing());

    private void Act(Func<DiceGame, DiceResult> action)
    {
        if (game == null || game.Finished) return;
        if (action == null) return;
        var result = action(game);
        switch (result)
        {
            case DiceResult.Invalid:
                HintLabel.Text = selection.Count == 0 ? "Nejdřív ťukni, které kostky odkládáš." : "Tyhle kostky se nepočítají – odkládají se jen bodované.";
                return;
            case DiceResult.TooMany:
                HintLabel.Text = $"Házíš jen {game.DiceLeft} {Cz.Plural(game.DiceLeft, "kostkou", "kostkami", "kostkami")}.";
                return;
            case DiceResult.NotYet:
                HintLabel.Text = $"Zapsat jde až od {DiceGame.MinTurn}.";
                return;
            case DiceResult.MustRoll:
                HintLabel.Text = "Všech 6 bodovalo – musíš házet znovu všemi šesti!";
                if (selection.Count > 0) game.Keep(selection.ToList(), bank: false);   // odložit a pokračovat
                break;
        }
        ResetTurnUi();
        Render();
    }

    private void OnUndoClicked(object sender, EventArgs e)
    {
        if (game?.Undo() is { } prev)
        {
            game = prev;
            ResetTurnUi();
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

    // Zpět jen odejde do Her – hra zůstává uložená
    protected override bool OnBackButtonPressed()
    {
        if (Listek.DismissActive()) return true;
        Dispatcher.Dispatch(async () => await Shell.Current.GoToAsync("//Hry"));
        return true;
    }
}
