using Dezolati.Controls;
using System.Globalization;
using Dezolati.Models;
using Dezolati.Services;

namespace Dezolati.Pages;

/// <summary>
/// Statistiky libovolného uživatele (kámoše i sebe).
/// Parametry navigace: userId, nick, back (kam vede tlačítko Zpět).
/// </summary>
public partial class StatsFriend : ContentPage, IQueryAttributable
{
    private static readonly CultureInfo Cs = new("cs-CZ");
    private readonly FirestoreService firestoreService = new();

    private string? userId;
    private string? nick;
    private string back = "//Friends";

    public StatsFriend()
    {
        InitializeComponent();
    }

    public void ApplyQueryAttributes(IDictionary<string, object> query)
    {
        userId = query.TryGetValue("userId", out var u) ? u as string : null;
        nick = query.TryGetValue("nick", out var n) ? n as string : null;
        back = query.TryGetValue("back", out var b) && b is string s ? s : "//Friends";

        TitleLabel.Text = nick == "Moje" ? "Moje statistiky" : $"Statistiky: {nick}";
        _ = Load();
    }

    private async Task Load()
    {
        SummaryGrid.Clear();
        SummaryGrid.RowDefinitions.Clear();
        HistoryList.Clear();
        NowLabel.IsVisible = false;
        var isMe = userId == AuthSession.Uid;
        RemoveFriendButton.IsVisible = !string.IsNullOrEmpty(userId) && !isMe;

        if (string.IsNullOrEmpty(userId))
            return;

        try
        {
            Busy.IsRunning = Busy.IsVisible = true;

            // ~33 čtení bez ohledu na to, jak dlouho už dotyčný chlastá
            var stats = await firestoreService.GetStats(userId);
            var history = stats.Last30;
            var today = stats.Today;

            DailyDrinksModel Sum(IEnumerable<DailyDrinksModel> days) => new()
            {
                Pivo = days.Sum(d => d.Pivo),
                Vino = days.Sum(d => d.Vino),
                Panaky = days.Sum(d => d.Panaky)
            };

            ShowNow(stats, isMe);

            var dnes = Sum(history.Where(h => h.Day == today));
            var tyden = Sum(history.Where(h => h.Day > today.AddDays(-7)));
            var mesic = Sum(history.Where(h => h.Day > today.AddDays(-30)));
            var celkem = stats.Totals;
            var drinkingDays = stats.Days;
            var record = stats.Record;

            string fav = celkem.Total == 0 ? "—"
                : celkem.Pivo >= celkem.Vino && celkem.Pivo >= celkem.Panaky ? "pivo"
                : celkem.Vino >= celkem.Panaky ? "víno"
                : "tvrdej";

            AddRow("Dnes", dnes.Total == 0 ? "čistý lístek" : Cz.Souhrn(dnes.Pivo, dnes.Vino, dnes.Panaky));
            AddRow("7 dní", Cz.Souhrn(tyden.Pivo, tyden.Vino, tyden.Panaky));
            AddRow("30 dní", Cz.Souhrn(mesic.Pivo, mesic.Vino, mesic.Panaky));
            AddRow("Celkem", Cz.Souhrn(celkem.Pivo, celkem.Vino, celkem.Panaky));
            AddRow("Dní v akci", drinkingDays.ToString());
            AddRow("Rekord", record == null ? "—"
                : $"{record.Day.ToString("d. M. yyyy", Cs)} ({Cz.Plural(record.Total, "kousek", "kousky", "kousků")})");
            AddRow("Srdcovka", fav);
            if (drinkingDays > 0)
                AddRow("Průměr / tah", (celkem.Total / (double)drinkingDays).ToString("0.0", Cs));

            DrawCharts(history, today, celkem);

            var last = history.Where(h => h.Total > 0).Take(14).ToList();
            if (last.Count == 0)
            {
                HistoryList.Add(new Label { Text = "Zatím nic. Abstinent, nebo lhář?", Style = AppStyle("Body") });
            }
            foreach (var day in last)
            {
                HistoryList.Add(new Label
                {
                    Text = $"{day.Day.ToString("ddd d. M.", Cs)} – {Cz.Souhrn(day.Pivo, day.Vino, day.Panaky)}",
                    Style = AppStyle("Body")
                });
            }
        }
        catch (Exception ex)
        {
            await Listek.AlertAsync(this, "Chyba", $"Statistiky se nepodařilo načíst: {ex.Message}");
        }
        finally
        {
            Busy.IsRunning = Busy.IsVisible = false;
        }
    }

    /// <summary>„Teď sedí: U Peťky (před 40 min)“ – jen když tam zapisoval v posledních 3 hodinách.</summary>
    private void ShowNow(UserStats stats, bool isMe)
    {
        if (string.IsNullOrWhiteSpace(stats.CurrentPubName) || stats.CurrentPubAt is not DateTime at) return;
        var ago = DateTime.UtcNow - at.ToUniversalTime();
        if (ago > TimeSpan.FromHours(3) || ago < TimeSpan.FromMinutes(-5)) return;

        var when = ago.TotalMinutes < 2 ? "právě teď"
            : ago.TotalMinutes < 60 ? $"před {(int)ago.TotalMinutes} min"
            : (int)ago.TotalHours == 1 ? "před hodinou"
            : $"před {(int)ago.TotalHours} hodinami";
        NowLabel.Text = $"🍺 {(isMe ? "Teď sedíš" : "Teď sedí")}: {stats.CurrentPubName} ({when})";
        NowLabel.IsVisible = true;
    }

    private async void OnRemoveFriendClicked(object sender, EventArgs e)
    {
        if (AuthSession.Uid is not string me || string.IsNullOrEmpty(userId) || userId == me) return;

        var who = string.IsNullOrWhiteSpace(nick) ? "tohohle kámoše" : nick;
        var ok = await Listek.ConfirmAsync(this, "Odebrat z kámošů?",
            $"{who} ti zmizí ze seznamu a ty jemu. Nic mu nepřijde, jen si toho časem všimne.",
            "Odebrat", "Nechat", danger: true);
        if (!ok) return;

        try
        {
            RemoveFriendButton.IsEnabled = false;
            await firestoreService.RemoveFriend(me, userId);
            await Shell.Current.GoToAsync("//Friends");
        }
        catch (Exception ex)
        {
            await Listek.AlertAsync(this, "Chyba", $"Odebrání se nepovedlo: {ex.Message}");
        }
        finally
        {
            RemoveFriendButton.IsEnabled = true;
        }
    }

    // ---------------- grafy ----------------

    private static readonly string[] WeekShort = { "Po", "Út", "St", "Čt", "Pá", "So", "Ne" };
    private static readonly string[] WeekLong = { "pondělí", "úterý", "středa", "čtvrtek", "pátek", "sobota", "neděle" };

    private void DrawCharts(List<DailyDrinksModel> history, DateTime today, DailyDrinksModel celkem)
    {
        ChartsBox.IsVisible = celkem.Total > 0 || history.Any(h => h.Total > 0);
        if (!ChartsBox.IsVisible) return;

        static int[] Parts(DailyDrinksModel? d) => d == null ? new[] { 0, 0, 0 } : new[] { d.Pivo, d.Vino, d.Panaky };

        // legenda
        Legend.Clear();
        foreach (var (name, color) in new[] { ("pivo", Pastelky.Pivo), ("víno", Pastelky.Vino), ("panáky", Pastelky.Panak) })
        {
            Legend.Add(new HorizontalStackLayout
            {
                Spacing = 6,
                Children =
                {
                    new BoxView { Color = color, WidthRequest = 14, HeightRequest = 14, CornerRadius = 3, VerticalOptions = LayoutOptions.Center },
                    new Label { Text = name, Style = AppStyle("Small"), VerticalOptions = LayoutOptions.Center }
                }
            });
        }

        // 30 dní: index 0 = před 29 dny, 29 = dnes (jeden hospodský den = jeden dokument, ale radši sečíst)
        var byDay = history.GroupBy(h => h.Day.Date).ToDictionary(g => g.Key, g => new[]
        {
            g.Sum(d => d.Pivo), g.Sum(d => d.Vino), g.Sum(d => d.Panaky)
        });
        var days = Enumerable.Range(0, 30)
            .Select(i => byDay.TryGetValue(today.Date.AddDays(i - 29), out var v) ? v : new[] { 0, 0, 0 })
            .ToList();
        var max = days.Max(d => d.Sum());
        DaysMaxLabel.Text = max > 0 ? $"nejvíc {Cz.Plural(max, "kousek", "kousky", "kousků")} za den" : "čistý měsíc";
        DaysFromLabel.Text = today.AddDays(-29).ToString("d. M.", Cs);
        DaysChart.Drawable = new SketchChart { Values = days, Seed = 3 };
        DaysChart.Invalidate();

        // dny v týdnu (z posledních 30 dní; hospodský den, takže sobota 1:00 je pořád pátek)
        var week = Enumerable.Range(0, 7).Select(_ => new[] { 0, 0, 0 }).ToList();
        foreach (var d in history)
        {
            var i = ((int)d.Day.DayOfWeek + 6) % 7; // pondělí = 0
            week[i][0] += d.Pivo;
            week[i][1] += d.Vino;
            week[i][2] += d.Panaky;
        }
        WeekChart.Drawable = new SketchChart { Values = week, Seed = 11 };
        WeekChart.Invalidate();

        WeekLabels.Clear();
        for (var i = 0; i < 7; i++)
            WeekLabels.Add(new Label { Text = WeekShort[i], Style = AppStyle("Small"), HorizontalOptions = LayoutOptions.Center }, i, 0);

        var best = week.Select((v, i) => (Sum: v.Sum(), i)).MaxBy(x => x.Sum);
        WeekCaption.Text = best.Sum > 0
            ? $"Nejvíc to jede: {WeekLong[best.i]} ({Cz.Plural(best.Sum, "kousek", "kousky", "kousků")})"
            : "Za posledních 30 dní nic.";

        // poměr za celou historii
        var parts = Parts(celkem);
        RatioChart.Drawable = new SketchChart { Mode = SketchMode.Ratio, Values = new[] { parts }, Seed = 5 };
        RatioChart.Invalidate();

        RatioLabels.Clear();
        var total = parts.Sum();
        var names = new[] { "pivo", "víno", "panáky" };
        var aligns = new[] { LayoutOptions.Start, LayoutOptions.Center, LayoutOptions.End };
        for (var i = 0; i < 3; i++)
        {
            var pct = total > 0 ? (int)Math.Round(100.0 * parts[i] / total) : 0;
            RatioLabels.Add(new Label
            {
                Text = $"{names[i]} {pct} %",
                Style = AppStyle("Small"),
                TextColor = Pastelky.Druhy[i],
                HorizontalOptions = aligns[i]
            }, i, 0);
        }
    }

    private void AddRow(string label, string value)
    {
        var row = SummaryGrid.RowDefinitions.Count;
        SummaryGrid.RowDefinitions.Add(new RowDefinition(GridLength.Auto));
        SummaryGrid.Add(new Label { Text = label, Style = AppStyle("FieldLabel"), Margin = 0 }, 0, row);
        SummaryGrid.Add(new Label { Text = value, Style = AppStyle("Body") }, 1, row);
    }

    private static Style? AppStyle(string key) =>
        Application.Current?.Resources.TryGetValue(key, out var s) == true ? s as Style : null;

    private void OnZpetClicked(object sender, EventArgs e) => OnBackButtonPressed();

    // systémové tlačítko Zpět: nejdřív zavřít případný lístek s hláškou, pak o stránku zpátky
    protected override bool OnBackButtonPressed()
    {
        if (Listek.DismissActive()) return true;
        Dispatcher.Dispatch(async () => await Shell.Current.GoToAsync(back));
        return true;
    }
}
