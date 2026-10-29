using System.Globalization;
using Dezolati.Controls;
using Dezolati.Services;

namespace Dezolati.Pages;

/// <summary>Přehled spotřeby Firebase proti bezplatným limitům (jen pro správce).</summary>
public partial class Spotreba : ContentPage
{
    private static readonly CultureInfo Cs = new("cs-CZ");

    public Spotreba()
    {
        InitializeComponent();
    }

    protected override async void OnAppearing()
    {
        base.OnAppearing();
        await Load();
    }

    private async Task Load()
    {
        ErrorLabel.IsVisible = false;
        Busy.IsRunning = Busy.IsVisible = true;
        RefreshButton.IsEnabled = false;
        try
        {
            var r = await UsageService.GetAsync();
            Render(r);
            UsageBox.IsVisible = true;
        }
        catch (Exception ex)
        {
            ErrorLabel.Text = Net.IsNetworkError(ex)
                ? "Bez signálu spotřebu nezjistím."
                : $"Spotřebu se nepodařilo zjistit: {ex.Message}";
            ErrorLabel.IsVisible = true;
        }
        finally
        {
            Busy.IsRunning = Busy.IsVisible = false;
            RefreshButton.IsEnabled = true;
        }
    }

    private void Render(UsageReport r)
    {
        UpdatedLabel.Text = $"stav {r.UpdatedAt.ToLocalTime().ToString("d. M. H:mm", Cs)}";

        TodayHeading.Text = $"Dnes (od {r.Today.Since.ToLocalTime():H:mm})";
        TodayBox.Clear();
        AddMeter(TodayBox, "Čtení", r.Today.Reads, r.Limits.Reads, 1);
        AddMeter(TodayBox, "Zápisy", r.Today.Writes, r.Limits.Writes, 2);
        AddMeter(TodayBox, "Mazání", r.Today.Deletes, r.Limits.Deletes, 3);

        MonthBox.Clear();
        AddMeter(MonthBox, "Volání funkcí", r.Month.Invocations, r.Limits.Invocations, 4);
        MonthBox.Add(new Label
        {
            Text = $"při tomhle tempu za celý měsíc {N(r.Month.Projected)} ({Pct(r.Month.Projected, r.Limits.Invocations)} z limitu)",
            Style = AppStyle("Small")
        });

        // 30 dní: čtení a zápisy zvlášť (každé má jiný limit)
        var reads = r.Days.Select(d => new[] { (int)Math.Min(int.MaxValue, d.Reads) }).ToList();
        var writes = r.Days.Select(d => new[] { 0, (int)Math.Min(int.MaxValue, d.Writes) }).ToList();
        ReadsChart.Drawable = new SketchChart { Values = reads, Limit = (int)r.Limits.Reads, Seed = 21 };
        WritesChart.Drawable = new SketchChart { Values = writes, Limit = (int)r.Limits.Writes, Seed = 22 };
        ReadsChart.Invalidate();
        WritesChart.Invalidate();

        var maxReads = r.Days.MaxBy(d => d.Reads);
        var maxWrites = r.Days.MaxBy(d => d.Writes);
        ReadsCaption.Text = maxReads == null || maxReads.Reads == 0 ? "nic"
            : $"nejvíc {N(maxReads.Reads)} ({DayText(maxReads.Day)}), průměr {N((long)r.Days.Average(d => d.Reads))} za den";
        WritesCaption.Text = maxWrites == null || maxWrites.Writes == 0 ? "nic"
            : $"nejvíc {N(maxWrites.Writes)} ({DayText(maxWrites.Day)}), průměr {N((long)r.Days.Average(d => d.Writes))} za den";
    }

    private void AddMeter(Layout box, string name, long value, long limit, int seed)
    {
        var row = new Grid { ColumnDefinitions = new ColumnDefinitionCollection(new ColumnDefinition(GridLength.Star), new ColumnDefinition(GridLength.Auto)) };
        row.Add(new Label { Text = name, Style = AppStyle("FieldLabel"), Margin = 0 }, 0, 0);
        row.Add(new Label
        {
            Text = $"{N(value)} / {N(limit)} ({Pct(value, limit)})",
            Style = AppStyle("Small"),
            VerticalOptions = LayoutOptions.Center
        }, 1, 0);
        box.Add(row);

        box.Add(new GraphicsView
        {
            HeightRequest = 22,
            Drawable = new SketchMeter { Fraction = limit > 0 ? (double)value / limit : 0, Seed = seed }
        });
    }

    private static string N(long n) => n.ToString("#,0", Cs);

    private static string Pct(long value, long limit)
    {
        if (limit <= 0) return "–";
        var p = 100.0 * value / limit;
        return p < 1 && value > 0 ? "pod 1 %" : $"{Math.Round(p)} %";
    }

    private static string DayText(string iso) =>
        DateTime.TryParseExact(iso, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var d)
            ? d.ToString("d. M.", Cs) : iso;

    private static Style? AppStyle(string key) =>
        Application.Current?.Resources.TryGetValue(key, out var s) == true ? s as Style : null;

    private async void OnRefreshClicked(object sender, EventArgs e) => await Load();

    /// <summary>Kdo má jakou verzi (appka ji zapisuje do adresáře po každé aktualizaci; starší než 3.10 nic).</summary>
    private async void OnVersionsTapped(object? sender, TappedEventArgs e)
    {
        VersionsList.IsVisible = !VersionsList.IsVisible;
        VersionsToggle.Text = VersionsList.IsVisible ? "(schovat)" : "(ukázat)";
        if (!VersionsList.IsVisible) return;

        VersionsList.Clear();
        VersionsList.Add(new Label { Text = "Načítám…", Style = AppStyle("Small") });
        var dir = await NickDirectory.GetAsync(force: true);   // 1 čtení
        VersionsList.Clear();
        var rows = dir.Values
            .OrderByDescending(u => AppUpdater.ParseVersion(u.Version) ?? new Version(0, 0))
            .ThenBy(u => u.Nick, StringComparer.CurrentCultureIgnoreCase)
            .ToList();
        var groups = rows.GroupBy(u => u.Version ?? "starší").Select(g => $"{g.Key}: {g.Count()}");
        VersionsList.Add(new Label { Text = string.Join(" · ", groups), Style = AppStyle("Small") });
        foreach (var u in rows)
        {
            var row = new Grid
            {
                ColumnDefinitions = new ColumnDefinitionCollection(new ColumnDefinition(GridLength.Star), new ColumnDefinition(GridLength.Auto)),
                ColumnSpacing = 8
            };
            row.Add(new Label { Text = u.Nick, Style = AppStyle("Body"), LineBreakMode = LineBreakMode.TailTruncation, MaxLines = 1 }, 0, 0);
            row.Add(new Label { Text = (u.Version ?? "starší") + " ", Style = AppStyle("Body") }, 1, 0);
            VersionsList.Add(row);
        }
        if (rows.Count == 0)
            VersionsList.Add(new Label { Text = Net.IsOnline ? "V adresáři zatím nikdo není." : "Bez signálu nic nenačtu.", Style = AppStyle("Small") });
    }

    /// <summary>Jednorázově: smazat moje staré testovací dny (statistiky, průměry). Ve 3.11 tlačítko zmizí.</summary>
    private async void OnCleanupClicked(object sender, EventArgs e)
    {
        var before = new DateTime(2026, 10, 1);
        if (!await Listek.ConfirmAsync(this, "Smazat staré zápisy?",
                "Smažou se tvoje denní součty před 1. 10. 2026 – zmizí ze statistik, součtů, rekordu i průměrů. " +
                "Vrátit to nejde. Stojí to 1 čtení a 1 smazání za každý takový den.", "Smazat", danger: true))
            return;
        try
        {
            CleanupButton.IsEnabled = false;
            var count = await new FirestoreService().DeleteMyDaysBefore(before);
            await Listek.AlertAsync(this, "Hotovo", count == 0 ? "Nic starého tam nebylo." : $"Smazáno {Cz.Plural(count, "den", "dny", "dní")}.");
        }
        catch (Exception ex)
        {
            await Listek.AlertAsync(this, "Nepovedlo se", Net.IsNetworkError(ex) ? "Bez signálu to nepůjde." : ex.Message);
        }
        finally
        {
            CleanupButton.IsEnabled = true;
        }
    }

    private void OnZpetClicked(object sender, EventArgs e) => OnBackButtonPressed();

    protected override bool OnBackButtonPressed()
    {
        if (Listek.DismissActive()) return true;
        Dispatcher.Dispatch(async () => await Shell.Current.GoToAsync("//Settings"));
        return true;
    }
}
