using Dezolati.Controls;
using Dezolati.Services;

namespace Dezolati.Pages;

/// <summary>
/// Detail eventu: odkazy na odznaky a mapu trhů, nahlášení trhů („Tady jsou trhy!“) a nejbližší trhy.
/// Otevírá se ťuknutím na cedulku eventu na lístku.
/// </summary>
public partial class TrhyPage : ContentPage
{
    private bool datesKnown;

    public TrhyPage()
    {
        InitializeComponent();
    }

    protected override async void OnAppearing()
    {
        base.OnAppearing();
        AdminButton.IsVisible = Events.IsAdmin;
        FromPicker.Date = ToPicker.Date = DateTime.Today;
        await Events.EnsureAsync();
        if (Events.Current is { } ev)
        {
            TitleLabel.Text = ev.Name + " ";
            DatesLabel.Text = $"{Events.Short(ev.From)} – {Events.Short(ev.To)} · {Trhy_.Count} trhů v seznamu ";
        }
        await ShowNearAsync();
    }

    private static IReadOnlyList<TrhZone> Trhy_ => Trhy.All;

    private async Task ShowNearAsync()
    {
        NearList.Clear();
        var me = LastLocation.Get(TimeSpan.FromMinutes(30));
        if (me == null)
        {
            NearHint.Text = "Zjišťuju polohu… ";
            if (await PushService.LocateAsync() is { } l) me = (l.Lat, l.Lon);
        }
        NearHint.Text = me == null ? "Bez polohy nevím, co je blízko – tady jsou všechny. " : "";
        var list = me is { } p
            ? Trhy.Nearest(p.Lat, p.Lon, 10)
            : Trhy_.Select(z => (z, -1.0)).Take(30).ToList();
        foreach (var (z, m) in list) NearList.Add(Row(z, m));
    }

    private View Row(TrhZone z, double meters)
    {
        var box = new VerticalStackLayout { Spacing = 0 };
        box.Add(new Label { Text = z.Name + " ", Style = AppStyle("Body"), FontFamily = "CaveatBold" });
        var running = z.RunsOn(Events.Today) ? "běží · " : "";
        var dist = meters >= 0 ? $" · {Trhy.DistanceText(meters)}" : "";
        box.Add(new Label
        {
            Text = $"{running}{z.When}{(z.Confirmed ? "" : " (odhad)")}{dist} ",
            Style = AppStyle("Small"),
            TextColor = z.Confirmed ? InkSoft() : Color.FromArgb("#C46A12")
        });
        var tap = new TapGestureRecognizer();
        tap.Tapped += async (_, _) => await OpenMap(z);
        box.GestureRecognizers.Add(tap);
        return box;
    }

    private static async Task OpenMap(TrhZone? z)
    {
        var args = new Dictionary<string, object> { ["trhy"] = "1" };
        if (z != null)
        {
            var c = Trhy.CenterOf(z);
            args["focusLat"] = c.Lat;
            args["focusLon"] = c.Lon;
            args["focusTrh"] = z.Id;
        }
        await Shell.Current.GoToAsync("//Hospody", args);
    }

    private void OnDatesKnownClicked(object sender, EventArgs e)
    {
        datesKnown = (sender as PencilButton)?.CommandParameter as string == "1";
        DatesBox.IsVisible = datesKnown;
        DatesKnown.Variant = datesKnown ? PencilVariant.Primary : PencilVariant.Secondary;
        DatesUnknown.Variant = datesKnown ? PencilVariant.Secondary : PencilVariant.Primary;
    }

    private async void OnReportClicked(object sender, EventArgs e)
    {
        if (!Events.Active)
        {
            await Listek.AlertAsync(this, "Trhy", "Event teď neběží, takže trhy hlásit nejde.");
            return;
        }
        ReportButton.IsEnabled = false;
        var busy = Listek.ShowBusy(this, "Tady jsou trhy!", "Zjišťuju, kde přesně jsi…");
        try
        {
            if (await PushService.LocateAsync() is not { } loc)
            {
                busy?.Dispose();
                await Listek.AlertAsync(this, "Bez polohy to nejde",
                    "Trhy se hlásí podle GPS přímo na místě. Povol appce polohu a zkus to znovu.");
                return;
            }
            if (Trhy.Match(loc.Lat, loc.Lon, Events.Today) is { } known)
            {
                busy?.Dispose();
                await Listek.AlertAsync(this, "Tyhle trhy známe",
                    $"{known.Name}\n{known.When}\n\nZápisy odsud se počítají do odznaků samy.");
                return;
            }
            DateTime? from = datesKnown ? FromPicker.Date : null;
            DateTime? to = datesKnown ? ToPicker.Date : null;
            if (from > to) (from, to) = (to, from);
            await TrhyService.SendAsync(loc.Lat, loc.Lon, loc.Accuracy, from, to, NoteEntry.Text);
            busy?.Dispose();
            NoteEntry.Text = "";
            await Listek.AlertAsync(this, "Díky!",
                "Trhy jsou nahlášené. Když je tady dnes nahlásí ještě další, schválí se samy; jinak je schválí správce. Odznaky za ně se dorovnají zpětně.");
        }
        catch (Exception ex)
        {
            busy?.Dispose();
            await Listek.AlertAsync(this, "Nepovedlo se", Net.IsNetworkError(ex) ? "Bez signálu to nepůjde." : ex.Message);
        }
        finally
        {
            ReportButton.IsEnabled = true;
        }
    }

    private async void OnBadgesClicked(object sender, EventArgs e) =>
        await Shell.Current.GoToAsync("//StatsFriend", new Dictionary<string, object>
        {
            ["userId"] = AuthSession.Uid ?? "", ["nick"] = "Moje", ["back"] = "//Trhy"
        });

    private async void OnMapClicked(object sender, EventArgs e) => await OpenMap(null);

    private async void OnAdminClicked(object sender, EventArgs e) => await Shell.Current.GoToAsync("//TrhyNavrhy");

    private static Color InkSoft() =>
        Application.Current?.Resources.TryGetValue("InkSoft", out var c) == true && c is Color color ? color : Colors.Gray;

    private static Style? AppStyle(string key) =>
        Application.Current?.Resources.TryGetValue(key, out var s) == true ? s as Style : null;

    private void OnZpetClicked(object sender, EventArgs e) => OnBackButtonPressed();

    protected override bool OnBackButtonPressed()
    {
        if (Listek.DismissActive()) return true;
        Dispatcher.Dispatch(async () => await Shell.Current.GoToAsync("//MainPage"));
        return true;
    }
}
