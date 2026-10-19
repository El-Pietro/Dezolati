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
    private string back = "//Friends";

    public StatsFriend()
    {
        InitializeComponent();
    }

    public void ApplyQueryAttributes(IDictionary<string, object> query)
    {
        userId = query.TryGetValue("userId", out var u) ? u as string : null;
        var nick = query.TryGetValue("nick", out var n) ? n as string : null;
        back = query.TryGetValue("back", out var b) && b is string s ? s : "//Friends";

        TitleLabel.Text = nick == "Moje" ? "Moje statistiky" : $"Statistiky: {nick}";
        _ = Load();
    }

    private async Task Load()
    {
        SummaryGrid.Clear();
        SummaryGrid.RowDefinitions.Clear();
        HistoryList.Clear();

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

    private void AddRow(string label, string value)
    {
        var row = SummaryGrid.RowDefinitions.Count;
        SummaryGrid.RowDefinitions.Add(new RowDefinition(GridLength.Auto));
        SummaryGrid.Add(new Label { Text = label, Style = AppStyle("FieldLabel"), Margin = 0 }, 0, row);
        SummaryGrid.Add(new Label { Text = value, Style = AppStyle("Body") }, 1, row);
    }

    private static Style? AppStyle(string key) =>
        Application.Current?.Resources.TryGetValue(key, out var s) == true ? s as Style : null;

    // systémové tlačítko Zpět: nejdřív zavřít případný lístek s hláškou, pak o stránku zpátky
    protected override bool OnBackButtonPressed()
    {
        if (Listek.DismissActive()) return true;
        Dispatcher.Dispatch(async () => await Shell.Current.GoToAsync(back));
        return true;
    }
}
