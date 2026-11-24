using System.Globalization;
using Dezolati.Controls;
using Dezolati.Services;

namespace Dezolati.Pages;

/// <summary>
/// Jen pro správce: hlášení trhů (Schválit = kruh kolem nahlášených míst, Zamítnout, Smazat) a úprava trhů ze seznamu
/// (vygooglené termíny, přejmenování, stav, smazání). Všechno jde přes Cloud Function trhAction.
/// </summary>
public partial class TrhyNavrhy : ContentPage
{
    private static readonly CultureInfo Cs = new("cs-CZ");
    private List<TrhZone> zones = new();
    private string? editStatus;

    public TrhyNavrhy()
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
        ListBox.Clear();
        StatusLabel.IsVisible = false;
        Busy.IsRunning = Busy.IsVisible = true;
        RefreshButton.IsEnabled = false;
        try
        {
            await Events.EnsureAsync();
            FillPicker();
            var reports = await TrhyService.ListAsync();
            if (reports.Count == 0)
            {
                StatusLabel.Text = "Nikdo nic nenahlásil. Trhy asi stojí, kde stály. ";
                StatusLabel.IsVisible = true;
            }
            foreach (var r in reports) ListBox.Add(Row(r));
        }
        catch (Exception ex)
        {
            StatusLabel.Text = Net.IsNetworkError(ex) ? "Bez signálu nic nenačtu. " : $"Nepodařilo se načíst: {ex.Message} ";
            StatusLabel.IsVisible = true;
        }
        finally
        {
            Busy.IsRunning = Busy.IsVisible = false;
            RefreshButton.IsEnabled = true;
        }
    }

    private static string StatusText(string s) => s switch
    {
        "pending" => "čeká", "known" => "už známé", "approved" => "schválené", "rejected" => "zamítnuté", _ => s
    };

    private View Row(TrhReport r)
    {
        var box = new VerticalStackLayout { Spacing = 2 };
        var when = r.At is DateTime at ? at.ToLocalTime().ToString("d. M. H:mm", Cs) : r.Day;
        box.Add(new Label { Text = $"{r.ByNick} · {when} ", Style = AppStyle("Heading") });
        var dates = r.From != null || r.To != null ? $"termín {(r.From != null ? Events.Short(r.From) : "?")} – {(r.To != null ? Events.Short(r.To) : "?")}" : "termín nevyplnil";
        box.Add(new Label { Text = $"{StatusText(r.Status)} · {dates} ", Style = AppStyle("Small") });
        if (!string.IsNullOrWhiteSpace(r.Note)) box.Add(new Label { Text = $"„{r.Note}“ ", Style = AppStyle("Small") });
        var near = Trhy.Nearest(r.Lat, r.Lon, 1).FirstOrDefault();
        if (near.Zone != null)
            box.Add(new Label
            {
                Text = $"nejbližší známé: {near.Zone.Name} ({Trhy.DistanceText(near.Meters)}) ",
                Style = AppStyle("Small"), TextColor = InkSoft()
            });

        var name = new Entry { Placeholder = "Název trhu (jinak poznámka)", Text = r.Note ?? "", MaxLength = 80, IsVisible = r.Status == "pending" };
        box.Add(name);

        var buttons = new Grid
        {
            ColumnDefinitions = new ColumnDefinitionCollection(
                new ColumnDefinition(GridLength.Star), new ColumnDefinition(GridLength.Star), new ColumnDefinition(GridLength.Star)),
            ColumnSpacing = 6,
            Margin = new Thickness(0, 4, 0, 0)
        };
        PencilButton Btn(string text, PencilVariant v) => new()
        {
            Text = text, Variant = v,
            FontSize = (double)(Application.Current?.Resources["FontButtonSmall"] ?? 20.0),
            HeightRequest = (double)(Application.Current?.Resources["ButtonHeightSmall"] ?? 42.0)
        };
        var map = Btn("Mapa", PencilVariant.Secondary);
        map.Clicked += async (_, _) => await Shell.Current.GoToAsync("//Hospody", new Dictionary<string, object>
        {
            ["trhy"] = "1", ["focusLat"] = r.Lat, ["focusLon"] = r.Lon
        });
        buttons.Add(map, 0, 0);
        if (r.Status == "pending")
        {
            var ok = Btn("Schválit", PencilVariant.Primary);
            var no = Btn("Zamítnout", PencilVariant.Danger);
            ok.Clicked += async (_, _) => await Handle(box, () => TrhyService.ApproveAsync(r.Id,
                string.IsNullOrWhiteSpace(name.Text) ? null : name.Text.Trim(), null, null), "Schváleno. Odznaky se rozdaly zpětně.");
            no.Clicked += async (_, _) => await Handle(box, () => TrhyService.RejectAsync(r.Id), "Zamítnuto.");
            buttons.Add(ok, 1, 0);
            buttons.Add(no, 2, 0);
        }
        else
        {
            var del = Btn("Smazat", PencilVariant.Danger);
            del.Clicked += async (_, _) => await Handle(box, () => TrhyService.DeleteReportAsync(r.Id), null);
            buttons.Add(del, 2, 0);
        }
        box.Add(buttons);
        return box;
    }

    private async Task Handle(View row, Func<Task> action, string? done)
    {
        row.IsEnabled = false;
        try
        {
            await action();
            ListBox.Remove(row);
            FillPicker();
            if (done != null) await Listek.AlertAsync(this, "Hotovo", done);
        }
        catch (Exception ex)
        {
            row.IsEnabled = true;
            await Listek.AlertAsync(this, "Nepovedlo se", Net.IsNetworkError(ex) ? "Bez signálu to nepůjde." : ex.Message);
        }
    }

    // ---------------- úprava trhu ----------------

    private void FillPicker()
    {
        zones = Trhy.All.OrderBy(z => z.Confirmed).ThenBy(z => z.Name).ToList();
        TrhPicker.ItemsSource = zones.Select(z => $"{(z.Confirmed ? "" : "⚠ ")}{z.Name} ({z.When})").ToList();
        EditBox.IsVisible = false;
    }

    private TrhZone? Selected => TrhPicker.SelectedIndex >= 0 && TrhPicker.SelectedIndex < zones.Count ? zones[TrhPicker.SelectedIndex] : null;

    private static DateTime Day(string iso) =>
        DateTime.TryParseExact(iso, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var d) ? d : DateTime.Today;

    private void OnTrhPicked(object? sender, EventArgs e)
    {
        if (Selected is not { } z) { EditBox.IsVisible = false; return; }
        EditBox.IsVisible = true;
        EditName.Text = z.Name;
        EditFrom.Date = Day(z.From);
        EditTo.Date = Day(z.To);
        SetStatus(z.Status);
    }

    private void SetStatus(string s)
    {
        editStatus = s;
        foreach (var (b, key) in new[] { (StPotvrzeno, "potvrzeno"), (StOdhad, "odhad"), (StNahlaseno, "nahlaseno") })
        {
            b.Variant = key == s ? PencilVariant.Primary : PencilVariant.Secondary;
            b.Opacity = key == s ? 1 : 0.45;
        }
    }

    private void OnStatusClicked(object sender, EventArgs e)
    {
        if ((sender as PencilButton)?.CommandParameter is string s) SetStatus(s);
    }

    private async void OnEditSaveClicked(object sender, EventArgs e)
    {
        if (Selected is not { } z) return;
        var from = EditFrom.Date; var to = EditTo.Date;
        if (from > to) (from, to) = (to, from);
        await Edit(() => TrhyService.EditAsync(z.Id, from, to, EditName.Text, editStatus), "Uloženo. Kdo tam pil v novém termínu, dostane odznaky zpětně.");
    }

    private async void OnEditRemoveClicked(object sender, EventArgs e)
    {
        if (Selected is not { } z) return;
        if (!await Listek.ConfirmAsync(this, "Smazat trh", $"{z.Name} zmizí ze seznamu i z mapy. Rozdané odznaky zůstanou.", "Smazat", danger: true)) return;
        await Edit(() => TrhyService.EditAsync(z.Id, null, null, null, null, remove: true), "Smazáno.");
    }

    private async Task Edit(Func<Task> action, string done)
    {
        EditBox.IsEnabled = false;
        try
        {
            await action();
            FillPicker();
            await Listek.AlertAsync(this, "Hotovo", done);
        }
        catch (Exception ex)
        {
            await Listek.AlertAsync(this, "Nepovedlo se", Net.IsNetworkError(ex) ? "Bez signálu to nepůjde." : ex.Message);
        }
        finally
        {
            EditBox.IsEnabled = true;
        }
    }

    private static Color InkSoft() =>
        Application.Current?.Resources.TryGetValue("InkSoft", out var c) == true && c is Color color ? color : Colors.Gray;

    private static Style? AppStyle(string key) =>
        Application.Current?.Resources.TryGetValue(key, out var s) == true ? s as Style : null;

    private async void OnRefreshClicked(object sender, EventArgs e) => await Load();

    private void OnZpetClicked(object sender, EventArgs e) => OnBackButtonPressed();

    protected override bool OnBackButtonPressed()
    {
        if (Listek.DismissActive()) return true;
        Dispatcher.Dispatch(async () => await Shell.Current.GoToAsync("//Trhy"));
        return true;
    }
}
