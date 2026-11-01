using System.Diagnostics;
using Dezolati.Controls;
using Dezolati.Services;

namespace Dezolati.Pages;

/// <summary>
/// Hospodský kvíz – kolem stolu na jednom telefonu. Otázky a logika jsou sdílené s webem (<see cref="QuizGame"/>, <see cref="QuizBank"/>).
/// Hráči jsou stejní jako v Hrách (kostky).
/// </summary>
public partial class Kviz : ContentPage
{
    private QuizGame game = new();
    private List<string> players = new();
    private int rounds = Preferences.Get("hry_kviz_kola", 5);
    private readonly Stopwatch clock = Stopwatch.StartNew();
    private double last;
    private bool ticking;
    private PencilButton[] opts = Array.Empty<PencilButton>();

    public Kviz()
    {
        InitializeComponent();
        opts = new[] { Opt0, Opt1, Opt2, Opt3 };
    }

    protected override void OnAppearing()
    {
        base.OnAppearing();
        players = QuizGame.LoadPlayers();
        ShowSetup();
    }

    protected override void OnDisappearing()
    {
        base.OnDisappearing();
        ticking = false;
    }

    // ---------------- nastavení ----------------

    private void ShowSetup()
    {
        SetupBox.IsVisible = true;
        QuestionBox.IsVisible = DoneBox.IsVisible = false;
        RenderPlayers();
        RenderRounds();
        InfoLabel.Text = $"V balíku je {QuizBank.All.Count} otázek o pivu, pivovarech a hospodách. Dokud neprojdete všechny, žádná se nezopakuje.";
    }

    private void RenderPlayers()
    {
        PlayersStack.Clear();
        if (players.Count == 0)
        {
            PlayersStack.Add(new Label { Text = "Nikdo – hraješ sám.", Style = (Style)Application.Current!.Resources["Small"] });
            return;
        }
        for (var i = 0; i < players.Count; i++)
        {
            var idx = i;
            var row = new Grid { ColumnDefinitions = new ColumnDefinitionCollection(new ColumnDefinition(GridLength.Star), new ColumnDefinition(GridLength.Auto)) };
            row.Add(new Label { Text = $"{i + 1}. {players[i]}", Style = (Style)Application.Current!.Resources["Body"], VerticalOptions = LayoutOptions.Center });
            var del = new PencilButton { Text = "✖", Variant = PencilVariant.Danger, WidthRequest = 48 };
            del.SetDynamicResource(HeightRequestProperty, "ButtonHeightSmall");
            del.Clicked += (_, _) => { players.RemoveAt(idx); QuizGame.SavePlayers(players); RenderPlayers(); };
            row.Add(del, 1);
            PlayersStack.Add(row);
        }
    }

    private void RenderRounds()
    {
        Rounds3.Variant = rounds == 3 ? PencilVariant.Primary : PencilVariant.Secondary;
        Rounds5.Variant = rounds == 5 ? PencilVariant.Primary : PencilVariant.Secondary;
        Rounds10.Variant = rounds == 10 ? PencilVariant.Primary : PencilVariant.Secondary;
    }

    private void OnAddPlayerClicked(object? sender, EventArgs e)
    {
        var name = NewPlayerEntry.Text?.Trim();
        if (string.IsNullOrEmpty(name) || players.Count >= 12) return;
        players.Add(name);
        QuizGame.SavePlayers(players);
        NewPlayerEntry.Text = "";
        RenderPlayers();
    }

    private void OnRoundsClicked(object sender, EventArgs e)
    {
        if (sender is PencilButton { CommandParameter: string s } && int.TryParse(s, out var n))
        {
            rounds = n;
            Preferences.Set("hry_kviz_kola", n);
            RenderRounds();
        }
    }

    private void OnStartClicked(object sender, EventArgs e)
    {
        game = new QuizGame { RoundsPerPlayer = rounds };
        game.Start(players);
        SetupBox.IsVisible = false;
        QuestionBox.IsVisible = true;
        ShowQuestion();
        StartTicking();
    }

    // ---------------- otázka ----------------

    private void ShowQuestion()
    {
        if (game.Current is not { } q) return;
        TurnLabel.Text = game.Players.Count > 1
            ? $"Na řadě: {game.CurrentPlayerName}"
            : $"Otázka {game.Index + 1} z {game.Deck.Count}";
        CategoryLabel.Text = game.Players.Count > 1 ? $"{q.Category} · otázka {game.Index + 1} z {game.Deck.Count}" : q.Category;
        QuestionLabel.Text = q.Text;
        for (var i = 0; i < 4; i++)
        {
            opts[i].Text = game.Options[i];
            opts[i].Variant = PencilVariant.Secondary;
            opts[i].Opacity = 1;
        }
        VerdictLabel.IsVisible = NoteLabel.IsVisible = NextButton.IsVisible = false;
        TimeBar.Progress = 1;
        ScoreLabel.Text = string.Join(" · ", game.Players.Select((p, i) => $"{p} {game.Scores[i]}"));
    }

    private void OnOptionClicked(object sender, EventArgs e)
    {
        if (sender is PencilButton { CommandParameter: string s } && int.TryParse(s, out var i))
        {
            game.Answer(i);
            ShowAnswer();
        }
    }

    private void ShowAnswer()
    {
        if (game.Current is not { } q) return;
        var correct = game.CorrectIndex;
        for (var i = 0; i < 4; i++)
        {
            // tlačítka nechat „živá“ (vypnutá by zbledla) – další ťuk už QuizGame ignoruje
            opts[i].Variant = i == correct ? PencilVariant.Primary : i == game.Picked ? PencilVariant.Danger : PencilVariant.Secondary;
            opts[i].Text = i == correct ? "✔ " + game.Options[i] : i == game.Picked ? "✖ " + game.Options[i] : game.Options[i];
            opts[i].Opacity = i == correct || i == game.Picked ? 1 : 0.5;
        }
        VerdictLabel.Text = game.Picked == -1 ? "Čas vypršel!" : game.LastCorrect ? "Správně!" : "Vedle.";
        NoteLabel.Text = q.Note;
        VerdictLabel.IsVisible = NoteLabel.IsVisible = NextButton.IsVisible = true;
        NextButton.Text = game.Index + 1 >= game.Deck.Count ? "Výsledky" : "Další";
        ScoreLabel.Text = string.Join(" · ", game.Players.Select((p, i) => $"{p} {game.Scores[i]}"));
        try { HapticFeedback.Default.Perform(HapticFeedbackType.Click); } catch { }
    }

    private void OnNextClicked(object sender, EventArgs e)
    {
        game.Next();
        if (game.State == QuizGame.Phase.Done)
        {
            ticking = false;
            QuestionBox.IsVisible = false;
            DoneBox.IsVisible = true;
            RankingLabel.Text = game.Ranking();
            return;
        }
        ShowQuestion();
        StartTicking();
    }

    private void OnAgainClicked(object sender, EventArgs e) => ShowSetup();

    // ---------------- čas ----------------

    private void StartTicking()
    {
        if (ticking) return;
        ticking = true;
        last = clock.Elapsed.TotalSeconds;
        Dispatcher.StartTimer(TimeSpan.FromMilliseconds(100), Tick);
    }

    private bool Tick()
    {
        if (!ticking) return false;
        var now = clock.Elapsed.TotalSeconds;
        game.Step(now - last);
        last = now;
        if (game.State == QuizGame.Phase.Question)
        {
            TimeBar.Progress = game.TimeLeft / QuizGame.TimeLimit;
            return true;
        }
        ticking = false;
        if (game.State == QuizGame.Phase.Answered && !NextButton.IsVisible) ShowAnswer();   // vypršel čas
        return false;
    }

    private void OnZpetClicked(object sender, EventArgs e) => OnBackButtonPressed();

    protected override bool OnBackButtonPressed()
    {
        if (Listek.DismissActive()) return true;
        Dispatcher.Dispatch(async () => await Shell.Current.GoToAsync("//Hry"));
        return true;
    }
}
