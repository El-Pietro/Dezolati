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
        NowLabel.IsVisible = PromileLabel.IsVisible = false;
        AvgBox.Clear();
        MonthsBox.IsVisible = false;
        MonthsGrid.Clear();
        MonthsGrid.RowDefinitions.Clear();
        MonthsGrid.IsVisible = monthsOpen = false;
        MonthsToggle.Text = "(ukázat)";
        SpendBox.IsVisible = PlacesBox.IsVisible = PubsBox.IsVisible = false;
        isMe = userId == AuthSession.Uid;
        RemoveFriendButton.IsVisible = MaskButton.IsVisible = !string.IsNullOrEmpty(userId) && !isMe;
        RenderMaskButton();
        EditHint.IsVisible = isMe;
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
        var record = stats.Record;

        string fav = celkem.Total == 0 ? "—"
            : celkem.Pivo >= celkem.Vino && celkem.Pivo >= celkem.Panaky ? "pivo"
            : celkem.Vino >= celkem.Panaky ? "víno"
            : "tvrdej";

        AddRow(SummaryGrid, "Dnes", dnes.Total == 0 ? "čistý lístek" : Cz.Souhrn(dnes.Pivo, dnes.Vino, dnes.Panaky));
        AddRow(SummaryGrid, "7 dní", Cz.Souhrn(tyden.Pivo, tyden.Vino, tyden.Panaky));
        AddRow(SummaryGrid, "30 dní", Cz.Souhrn(mesic.Pivo, mesic.Vino, mesic.Panaky));
        AddRow(SummaryGrid, "Celkem", Cz.Souhrn(celkem.Pivo, celkem.Vino, celkem.Panaky));
        AddRow(SummaryGrid, "Rekord", record == null ? "—"
            : $"{record.Day.ToString("d. M. yyyy", Cs)} ({Cz.Plural(record.Total, "kousek", "kousky", "kousků")})");
        AddRow(SummaryGrid, "Srdcovka", fav);
        // průměry na den (i dny bez pití): posledních 30 dní a celkem od prvního zápisu
        AvgBox.Clear();
        if (stats.FirstDay is DateTime first && celkem.Total > 0)
        {
            var daysAll = Math.Max(1, (today.Date - first.Date).Days + 1);
            var days30 = Math.Min(30, daysAll);
            string A(int n, int d) => (n / (double)d).ToString("0.0", Cs);
            void Avg(string title, int n30, int nAll)
            {
                AvgBox.Add(new Label { Text = title, Style = AppStyle("FieldLabel") });
                AvgBox.Add(new Label
                {
                    Text = $"30 dní: {A(n30, days30)} / den · celkem: {A(nAll, daysAll)} / den",
                    Style = AppStyle("Body"), Margin = new Thickness(16, 0, 0, 4)
                });
            }
            Avg("Průměrná spotřeba", mesic.Total, celkem.Total);
            Avg("Průměrná spotřeba piva", mesic.Pivo, celkem.Pivo);
            Avg("Průměrná spotřeba vína", mesic.Vino, celkem.Vino);
            Avg("Průměrná spotřeba panáků", mesic.Panaky, celkem.Panaky);
        }
        MonthsBox.IsVisible = celkem.Total > 0;

        RenderSpend(stats);
        RenderPlaces(stats);
        RenderPubs(stats);
        RenderFavorites(stats);
        DrawCharts(history, today, celkem);

        // celý měsíc den po dni, i prázdné dny (ťuknutím se dá dopsat zapomenutý večer)
        if (!history.Any(h => h.Total > 0))
            HistoryList.Add(new Label { Text = "Zatím nic. Abstinent, nebo lhář?", Style = AppStyle("Body") });
        var byDay = history.GroupBy(h => h.Day.Date).ToDictionary(g => g.Key, g => g.First());
        for (var i = 0; i < 30; i++)
        {
            var date = today.Date.AddDays(-i);
            var text = date.ToString("ddd d. M.", Cs);
            if (byDay.TryGetValue(date, out var day) && day.Total > 0)
                text += $" – {Cz.Souhrn(day.Pivo, day.Vino, day.Panaky)}";
            var label = new Label { Text = text, Style = AppStyle("Body") };
            if (isMe)
            {
                var tap = new TapGestureRecognizer();
                tap.Tapped += async (_, _) => await EditDayAsync(date);
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

        SpendNote.Text = "Vyplň si v Nastavení obvyklé ceny.";
        SpendNote.IsVisible = !Prices.AnyDefault;
        SpendDetail.IsVisible = spendOpen;
        SpendToggle.Text = spendOpen ? "(schovat)" : "(ukázat)";

        // dny z doby před útratou se dopočítají samy (jednou) podle obvyklých cen
        if (Prices.AnyDefault && stats.Totals.Total > 0 && !Preferences.Get($"kc_estimated_{userId}", false))
            _ = EstimateSpendAsync();
    }

    private bool spendOpen;
    private bool estimating;

    private void OnSpendTapped(object? sender, TappedEventArgs e)
    {
        spendOpen = !spendOpen;
        SpendDetail.IsVisible = spendOpen;
        SpendToggle.Text = spendOpen ? "(schovat)" : "(ukázat)";
    }

    private async Task EstimateSpendAsync()
    {
        if (estimating || userId == null || !isMe) return;
        estimating = true;
        try
        {
            var (days, _) = await firestoreService.EstimatePastSpend(userId);
            Preferences.Set($"kc_estimated_{userId}", true);
            if (days > 0) await Load();
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"Útrata zpětně: {ex.Message}");   // zkusí se příště
        }
        finally
        {
            estimating = false;
        }
    }

    // ---------------- místa a hospody ----------------

    private void RenderPlaces(UserStats stats)
    {
        PlacesGrid.Clear();
        PlacesGrid.RowDefinitions.Clear();

        string Split(long pub, long home, long other)
        {
            // starší škrtnutí místa neubírala → pro jistotu nic pod nulu
            pub = Math.Max(0, pub); home = Math.Max(0, home); other = Math.Max(0, other);
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

    private void RenderFavorites(UserStats stats)
    {
        FavList.Clear();
        FavBox.IsVisible = stats.FavoritePubs.Count > 0;
        foreach (var (id, fav) in stats.FavoritePubs.OrderBy(f => f.Value.Name, StringComparer.CurrentCultureIgnoreCase))
        {
            var label = new Label { Text = "★ " + fav.Name, Style = AppStyle("Body") };
            var tap = new TapGestureRecognizer();
            var pubId = id;
            var f = fav;
            tap.Tapped += async (_, _) => await Shell.Current.GoToAsync("//Hospoda", new Dictionary<string, object>
            {
                ["pubId"] = pubId, ["lat"] = f.Lat, ["lon"] = f.Lon, ["name"] = f.Name, ["kind"] = f.Kind ?? "pub"
            });
            label.GestureRecognizers.Add(tap);
            FavList.Add(label);
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

    /// <summary>„Teď sedí: U Peťky (před 40 min)“ – jen když tam zapisoval v posledních 3 hodinách.</summary>
    private void ShowNow(UserStats stats, bool isMe)
    {
        // promile kámoše (u sebe je na lístku)
        if (!isMe && Promile.FromShared(stats.PromileValue, stats.PromileAt) is { } p)
        {
            PromileLabel.Text = $"≈ {p.Promile.ToString("0.0", Cs)} ‰ · {Promile.SoberText(p.SoberAtLocal, stats.Gender)}";
            PromileLabel.IsVisible = true;
        }

        if (string.IsNullOrWhiteSpace(stats.CurrentPubName) || stats.CurrentPubAt is not DateTime at) return;
        var ago = DateTime.UtcNow - at.ToUniversalTime();
        if (ago > TimeSpan.FromHours(3) || ago < TimeSpan.FromMinutes(-5)) return;

        var when = ago.TotalMinutes < 2 ? "právě teď"
            : ago.TotalMinutes < 60 ? $"před {(int)ago.TotalMinutes} min"
            : (int)ago.TotalHours == 1 ? "před hodinou"
            : $"před {(int)ago.TotalHours} hodinami";
        NowLabel.Text = stats.CurrentPubName is "doma" or "venku"
            ? $"🍺 {(isMe ? "Teď piješ" : "Teď pije")} {stats.CurrentPubName} ({when})"
            : $"🍺 {(isMe ? "Teď sedíš" : "Teď sedí")}: {stats.CurrentPubName} ({when})";
        NowLabel.IsVisible = true;
    }

    // ---------------- po měsících ----------------

    private bool monthsOpen;
    private bool monthsLoading;

    private async void OnMonthsTapped(object? sender, TappedEventArgs e)
    {
        monthsOpen = !monthsOpen;
        MonthsGrid.IsVisible = monthsOpen;
        MonthsToggle.Text = monthsOpen ? "(schovat)" : "(ukázat)";
        if (monthsOpen && MonthsGrid.Children.Count == 0) await LoadMonthsAsync();
    }

    private record MonthCache(int Pivo, int Vino, int Panaky);

    /// <summary>
    /// Posledních 12 měsíců (ne dřív než první zápis). Uzavřené měsíce (starší než 31 dní, už nejdou upravit)
    /// se drží v telefonu → čtou se jen jednou; aktuální se čtou při každém rozbalení (1 čtení za den s pitím).
    /// </summary>
    private async Task LoadMonthsAsync()
    {
        if (monthsLoading || stats == null || string.IsNullOrEmpty(userId)) return;
        monthsLoading = true;
        var s = stats;
        var uid = userId;
        try
        {
            MonthsGrid.Clear();
            MonthsGrid.RowDefinitions.Clear();
            var first = s.FirstDay ?? s.Today;
            var firstMonth = new DateTime(first.Year, first.Month, 1);
            var month = new DateTime(s.Today.Year, s.Today.Month, 1);
            for (var i = 0; i < 12 && month >= firstMonth; i++, month = month.AddMonths(-1))
            {
                var closed = month.AddMonths(1) < DateTime.Today.AddDays(-31);
                var key = $"month_{uid}_{month:yyyy-MM}_{(s.Masked ? "m" : "r")}";
                DailyDrinksModel total;
                MonthCache? cached = null;
                if (closed)
                    try { cached = System.Text.Json.JsonSerializer.Deserialize<MonthCache>(Preferences.Get(key, "")); } catch { }
                if (cached != null)
                    total = new DailyDrinksModel { Pivo = cached.Pivo, Vino = cached.Vino, Panaky = cached.Panaky };
                else
                {
                    total = await firestoreService.GetMonthTotal(uid, month.Year, month.Month, s.Masked);
                    if (closed)
                        Preferences.Set(key, System.Text.Json.JsonSerializer.Serialize(new MonthCache(total.Pivo, total.Vino, total.Panaky)));
                }
                if (stats != s) return;   // mezitím se přepnulo jinam
                AddRow(MonthsGrid, month.ToString("MMMM yyyy", Cs),
                    total.Total == 0 ? "čistý měsíc" : Cz.Souhrn(total.Pivo, total.Vino, total.Panaky));
            }
        }
        catch (Exception ex)
        {
            MonthsGrid.Clear();
            MonthsGrid.RowDefinitions.Clear();
            AddRow(MonthsGrid, "", Net.IsNetworkError(ex) ? "Bez signálu nic nenačtu." : $"Nepovedlo se: {ex.Message}");
        }
        finally
        {
            monthsLoading = false;
        }
    }

    // ---------------- tajný režim ----------------

    private bool MaskOn => userId != null && UserSession.Profile?.MaskFor.Contains(userId) == true;

    private void RenderMaskButton() =>
        MaskButton.Text = MaskOn ? "Tajný režim: zapnutý" : "Tajný režim: vypnutý";

    private async void OnMaskClicked(object sender, EventArgs e)
    {
        if (AuthSession.Uid is not string me || string.IsNullOrEmpty(userId) || userId == me) return;
        await UserSession.EnsureReadyAsync();
        if (UserSession.Profile is not { } profile) return;
        var who = string.IsNullOrWhiteSpace(nick) ? "Tenhle kámoš" : nick;
        var on = !MaskOn;
        var ok = await Listek.ConfirmAsync(this, on ? "Zapnout tajný režim?" : "Vypnout tajný režim?",
            on ? $"{who} uvidí ve tvých statistikách jen asi třetinu, nedozví se, že piješ teď ani kde, " +
                 "nepřijdou mu oznámení o tvém pití a neuvidí tvoje promile. Nic nepozná."
               : $"{who} zase uvidí skutečná čísla, kdo teď pije, kde sedíš i oznámení.",
            on ? "Zapnout" : "Vypnout");
        if (!ok) return;

        var list = profile.MaskFor.Where(id => id != userId).ToList();
        if (on) list.Add(userId);
        try
        {
            MaskButton.IsEnabled = false;
            await FirestoreRest.PatchAsync($"Users/{me}", new() { ["MaskFor"] = list });
            profile.MaskFor = list;
            UserSession.SetProfile(profile);
            RenderMaskButton();
        }
        catch (Exception ex)
        {
            await Listek.AlertAsync(this, "Nepovedlo se", Net.IsNetworkError(ex) ? "Bez signálu to nepůjde." : ex.Message);
        }
        finally
        {
            MaskButton.IsEnabled = true;
        }
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
