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

    private UserStats? stats;      // naposledy načtené (úprava dne se promítne bez dalšího čtení)
    private bool isMe;

    private async Task Load()
    {
        SummaryGrid.Clear();
        SummaryGrid.RowDefinitions.Clear();
        HistoryList.Clear();
        NowLabel.IsVisible = false;
        SpendBox.IsVisible = PlacesBox.IsVisible = PubsBox.IsVisible = false;
        isMe = userId == AuthSession.Uid;
        RemoveFriendButton.IsVisible = !string.IsNullOrEmpty(userId) && !isMe;
        EditHint.IsVisible = OtherDayButton.IsVisible = isMe;
        stats = null;

        if (string.IsNullOrEmpty(userId))
            return;

        try
        {
            Busy.IsRunning = Busy.IsVisible = true;

            // ~35 čtení bez ohledu na to, jak dlouho už dotyčný chlastá
            stats = await firestoreService.GetStats(userId);
            Render(stats);
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

    private static DailyDrinksModel Sum(IEnumerable<DailyDrinksModel> days) => new()
    {
        Pivo = days.Sum(d => d.Pivo),
        Vino = days.Sum(d => d.Vino),
        Panaky = days.Sum(d => d.Panaky)
    };

    private void Render(UserStats stats)
    {
        SummaryGrid.Clear();
        SummaryGrid.RowDefinitions.Clear();
        HistoryList.Clear();

        var history = stats.Last30;
        var today = stats.Today;

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

        AddRow(SummaryGrid, "Dnes", dnes.Total == 0 ? "čistý lístek" : Cz.Souhrn(dnes.Pivo, dnes.Vino, dnes.Panaky));
        AddRow(SummaryGrid, "7 dní", Cz.Souhrn(tyden.Pivo, tyden.Vino, tyden.Panaky));
        AddRow(SummaryGrid, "30 dní", Cz.Souhrn(mesic.Pivo, mesic.Vino, mesic.Panaky));
        AddRow(SummaryGrid, "Celkem", Cz.Souhrn(celkem.Pivo, celkem.Vino, celkem.Panaky));
        AddRow(SummaryGrid, "Dní v akci", drinkingDays.ToString());
        AddRow(SummaryGrid, "Rekord", record == null ? "—"
            : $"{record.Day.ToString("d. M. yyyy", Cs)} ({Cz.Plural(record.Total, "kousek", "kousky", "kousků")})");
        AddRow(SummaryGrid, "Srdcovka", fav);
        if (drinkingDays > 0)
            AddRow(SummaryGrid, "Průměr / tah", (celkem.Total / (double)drinkingDays).ToString("0.0", Cs));

        RenderSpend(stats);
        RenderPlaces(stats);
        RenderPubs(stats);
        DrawCharts(history, today, celkem);

        var last = history.Where(h => h.Total > 0).Take(14).ToList();
        if (last.Count == 0)
        {
            HistoryList.Add(new Label { Text = "Zatím nic. Abstinent, nebo lhář?", Style = AppStyle("Body") });
        }
        foreach (var day in last)
        {
            var label = new Label
            {
                Text = $"{day.Day.ToString("ddd d. M.", Cs)} – {Cz.Souhrn(day.Pivo, day.Vino, day.Panaky)}",
                Style = AppStyle("Body")
            };
            if (isMe && day.Day.Date >= today.Date.AddDays(-30))
            {
                var tap = new TapGestureRecognizer();
                var d = day.Day;
                tap.Tapped += async (_, _) => await EditDayAsync(d);
                label.GestureRecognizers.Add(tap);
            }
            HistoryList.Add(label);
        }
    }

    // ---------------- útrata ----------------

    private static string Kc(long kc) => $"{kc.ToString("#,0", Cs)} Kč";

    private void RenderSpend(UserStats stats)
    {
        SpendGrid.Clear();
        SpendGrid.RowDefinitions.Clear();
        if (!isMe) return;   // útrata je jen moje věc

        var h = stats.Last30;
        var today = stats.Today;
        long SumKc(IEnumerable<DailyDrinksModel> days) => days.Sum(d => (long)(d.Kc ?? 0));

        var anyKc = stats.TotalKc != 0 || h.Any(d => d.Kc != null);
        SpendBox.IsVisible = true;
        if (anyKc)
        {
            AddRow(SpendGrid, "Dnes", Kc(SumKc(h.Where(d => d.Day == today))));
            AddRow(SpendGrid, "7 dní", Kc(SumKc(h.Where(d => d.Day > today.AddDays(-7)))));
            AddRow(SpendGrid, "30 dní", Kc(SumKc(h.Where(d => d.Day > today.AddDays(-30)))));
            AddRow(SpendGrid, "Celkem", Kc(stats.TotalKc));
        }

        var estimated = Preferences.Get($"kc_estimated_{userId}", false);
        if (!Prices.AnyDefault)
        {
            SpendNote.Text = "Vyplň si v Nastavení, kolik u vás stojí pivo, víno a panák – útrata se pak počítá sama. V hospodě s vyplněnými cenami se vezmou ty.";
            SpendNote.IsVisible = true;
        }
        else
        {
            SpendNote.Text = "Počítá se od verze 3.7 cenami hospody (jsou-li vyplněné), jinak tvými výchozími.";
            SpendNote.IsVisible = !estimated;
        }
        EstimateButton.IsVisible = Prices.AnyDefault && !estimated && stats.Totals.Total > 0;
    }

    private async void OnEstimateClicked(object sender, EventArgs e)
    {
        if (userId == null || !isMe) return;
        var ok = await Listek.ConfirmAsync(this, "Útrata zpětně",
            $"Dny bez útraty dostanou odhad podle tvých výchozích cen (pivo {Prices.Pivo} Kč, víno {Prices.Vino} Kč, panák {Prices.Panak} Kč). " +
            "Kolik jsi kde skutečně utratil, už nikdo nezjistí – je to odhad. Stojí to jedno čtení a jeden zápis za každý den s pitím.",
            "Dopočítat");
        if (!ok) return;
        try
        {
            EstimateButton.IsEnabled = false;
            var (days, kc) = await firestoreService.EstimatePastSpend(userId);
            Preferences.Set($"kc_estimated_{userId}", true);
            await Listek.AlertAsync(this, "Hotovo", days == 0
                ? "Nebylo co dopočítat."
                : $"Dopočítáno {Cz.Plural(days, "den", "dny", "dní")}, odhadem {Kc(kc)}. Na játra se radši nedívej.");
            await Load();
        }
        catch (Exception ex)
        {
            await Listek.AlertAsync(this, "Chyba", ex.Message);
        }
        finally
        {
            EstimateButton.IsEnabled = true;
        }
    }

    // ---------------- místa a hospody ----------------

    private void RenderPlaces(UserStats stats)
    {
        PlacesGrid.Clear();
        PlacesGrid.RowDefinitions.Clear();

        string Split(long pub, long home, long other)
        {
            var known = pub + home + other;
            if (known == 0) return "—";
            int P(long v) => (int)Math.Round(100.0 * v / known);
            var parts = new List<string>();
            if (pub > 0) parts.Add($"hospoda {P(pub)} %");
            if (home > 0) parts.Add($"doma {P(home)} %");
            if (other > 0) parts.Add($"jinde {P(other)} %");
            return string.Join(" · ", parts);
        }

        var h = stats.Last30;
        long p30 = h.Sum(d => (long)d.PlacePub), h30 = h.Sum(d => (long)d.PlaceHome), o30 = h.Sum(d => (long)d.PlaceOther);
        var anyTotal = stats.TotalPlacePub + stats.TotalPlaceHome + stats.TotalPlaceOther > 0;
        PlacesBox.IsVisible = anyTotal;
        if (!anyTotal) return;

        AddRow(PlacesGrid, "30 dní", Split(p30, h30, o30));
        AddRow(PlacesGrid, "Celkem", Split(stats.TotalPlacePub, stats.TotalPlaceHome, stats.TotalPlaceOther));
    }

    private void RenderPubs(UserStats stats)
    {
        PubsList.Clear();
        var top = stats.Pubs.Take(5).ToList();
        PubsBox.IsVisible = top.Count > 0;
        var rank = 1;
        foreach (var p in top)
        {
            var info = $"{Cz.Plural(p.Count, "kousek", "kousky", "kousků")}";
            if (p.Visits > 0) info += $" · {Cz.Plural(p.Visits, "návštěva", "návštěvy", "návštěv")}";
            if (p.LastAt is DateTime at) info += $" · naposledy {at.ToLocalTime().ToString("d. M.", Cs)}";
            if (isMe && p.Kc > 0) info += $" · {Kc(p.Kc)}";

            var box = new VerticalStackLayout { Spacing = 0 };
            box.Add(new Label { Text = $"{rank++}. {p.Name ?? "?"}", Style = AppStyle("Body") });
            box.Add(new Label { Text = info, Style = AppStyle("Small") });

            var tap = new TapGestureRecognizer();
            var id = p.PubId;
            tap.Tapped += async (_, _) => await Shell.Current.GoToAsync("//Hospoda", new Dictionary<string, object> { ["pubId"] = id });
            box.GestureRecognizers.Add(tap);
            PubsList.Add(box);
        }
    }

    // ---------------- úprava starších dní (jen u sebe) ----------------

    private async Task EditDayAsync(DateTime day)
    {
        if (!isMe || userId == null || stats == null) return;
        if (day.Date < stats.Today.Date.AddDays(-30) || day.Date > stats.Today.Date) return;

        var current = stats.Last30.FirstOrDefault(d => d.Day.Date == day.Date) ?? new DailyDrinksModel { Day = day.Date };
        var title = day.Date == stats.Today.Date ? "Dneska" : day.ToString("dddd d. M.", Cs);
        var summary = current.Total == 0 ? "Čistý lístek." : Cz.Souhrn(current.Pivo, current.Vino, current.Panaky);

        const string add = "Dopsat", strike = "Škrtnout";
        var buttons = new List<(string, PencilVariant)> { (add, PencilVariant.Primary) };
        if (current.Total > 0) buttons.Add((strike, PencilVariant.Danger));
        buttons.Add(("Nic", PencilVariant.Secondary));
        var action = await Listek.ShowAsync(this, title, summary, buttons.ToArray());
        if (action != add && action != strike) return;

        var sign = action == add ? 1 : -1;
        var kinds = new List<(string, PencilVariant)>();
        if (sign > 0 || current.Pivo > 0) kinds.Add(("Pivo", PencilVariant.Primary));
        if (sign > 0 || current.Vino > 0) kinds.Add(("Víno", PencilVariant.Primary));
        if (sign > 0 || current.Panaky > 0) kinds.Add(("Panák", PencilVariant.Primary));
        kinds.Add(("Nic", PencilVariant.Secondary));
        var kind = await Listek.ShowAsync(this, action == add ? "Co dopsat?" : "Co škrtnout?",
            "Po jednom kousku – klidně víckrát za sebou.", kinds.ToArray());
        if (kind is not ("Pivo" or "Víno" or "Panák")) return;

        int p = kind == "Pivo" ? sign : 0, v = kind == "Víno" ? sign : 0, pa = kind == "Panák" ? sign : 0;
        try
        {
            await firestoreService.RecordPastEdit(userId, day.Date, p, v, pa);

            // promítnout bez dalšího čtení
            if (!stats.Last30.Contains(current))
            {
                stats.Last30.Add(current);
                stats.Last30 = stats.Last30.OrderByDescending(d => d.Day).ToList();
            }
            current.Pivo += p; current.Vino += v; current.Panaky += pa;
            current.Kc = (current.Kc ?? 0) + Prices.Default(p, v, pa);
            stats.Totals.Pivo += p; stats.Totals.Vino += v; stats.Totals.Panaky += pa;
            stats.TotalKc += Prices.Default(p, v, pa);
            Render(stats);
            if (day.Date == stats.Today.Date) UserSession.RaiseDrinksChanged();
        }
        catch (Exception ex)
        {
            await Listek.AlertAsync(this, "Chyba", $"Úprava se nepovedla: {ex.Message}");
        }
    }

    private void OnOtherDayClicked(object sender, EventArgs e)
    {
        if (stats == null) return;
        OtherDayPicker.MaximumDate = stats.Today.Date;
        OtherDayPicker.MinimumDate = stats.Today.Date.AddDays(-30);
        OtherDayPicker.Date = stats.Today.Date;   // vybrat dnešek = nic (ten je na lístku)
        OtherDayPicker.Focus();
    }

    private async void OnOtherDaySelected(object? sender, DateChangedEventArgs e)
    {
        if (stats == null || e.NewDate.Date == stats.Today.Date) return;
        await EditDayAsync(e.NewDate.Date);
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

    private static void AddRow(Grid grid, string label, string value)
    {
        var row = grid.RowDefinitions.Count;
        grid.RowDefinitions.Add(new RowDefinition(GridLength.Auto));
        grid.Add(new Label { Text = label, Style = AppStyle("FieldLabel"), Margin = 0 }, 0, row);
        grid.Add(new Label { Text = value, Style = AppStyle("Body") }, 1, row);
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
