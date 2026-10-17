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

            var history = await firestoreService.GetDailyHistory(userId);
            var today = await firestoreService.GetCurrentPubDay(userId);

            DailyDrinksModel Sum(IEnumerable<DailyDrinksModel> days) => new()
            {
                Pivo = days.Sum(d => d.Pivo),
                Vino = days.Sum(d => d.Vino),
                Panaky = days.Sum(d => d.Panaky)
            };

            var dnes = Sum(history.Where(h => h.Day == today));
            var tyden = Sum(history.Where(h => h.Day > today.AddDays(-7)));
            var mesic = Sum(history.Where(h => h.Day > today.AddDays(-30)));
            var celkem = Sum(history);
            var drinkingDays = history.Count(h => h.Total > 0);
            var record = history.Where(h => h.Total > 0).MaxBy(h => h.Total);

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
                HistoryList.Add(new Label { Text = "Zatím nic. Abstinent, nebo lhář?", TextColor = Colors.Black });
            }
            foreach (var day in last)
            {
                HistoryList.Add(new Label
                {
                    Text = $"{day.Day.ToString("ddd d. M.", Cs)} – {Cz.Souhrn(day.Pivo, day.Vino, day.Panaky)}",
                    TextColor = Colors.Black,
                    FontSize = 15
                });
            }
        }
        catch (Exception ex)
        {
            await DisplayAlert("Chyba", $"Statistiky se nepodařilo načíst: {ex.Message}", "OK");
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
        SummaryGrid.Add(new Label { Text = label, FontAttributes = FontAttributes.Bold, TextColor = Colors.Black }, 0, row);
        SummaryGrid.Add(new Label { Text = value, TextColor = Colors.Black }, 1, row);
    }

    private async void OnZpetClicked(object sender, EventArgs e)
    {
        await Shell.Current.GoToAsync(back);
    }
}
