using Dezolati.Controls;
using Dezolati.Services;

namespace Dezolati.Pages;

/// <summary>Moje akce u kámošů: nadcházející a běžící (Jdu / Možná / Nejdu), proběhlé, nová akce.</summary>
public partial class AkceList : ContentPage
{
    public AkceList()
    {
        InitializeComponent();
    }

    protected override async void OnAppearing()
    {
        base.OnAppearing();
        Render(AkceService.Mine);   // hned z telefonu, pak ze serveru
        await Load(force: true);
    }

    private async Task Load(bool force)
    {
        try
        {
            Render(await AkceService.LoadMineAsync(force));
        }
        catch (Exception ex)
        {
            Body.Add(Ui.Small(Net.IsNetworkError(ex) ? "Bez signálu jen to, co znám z minula." : $"Nepodařilo se načíst: {ex.Message}", soft: true));
        }
        finally
        {
            Refresh.IsRefreshing = false;
        }
    }

    private void Render(List<AkceInfo> list)
    {
        Body.Clear();
        Body.Add(Ui.Title("Akce (beta)"));
        Body.Add(Ui.Small("Večírky u kámošů: akční lístek na pivo, bečky a výsledky.", soft: true));
        Body.Add(Ui.Small("Novinka ve zkušebním provozu – když něco nesedí, napiš Petrovi. Zápisy na lístcích se neztratí.", soft: true));
        Body.Add(Ui.Btn("Nová akce", PencilVariant.Primary, false, async (_, _) => await Shell.Current.GoToAsync("//AkceEdit")));
        if (Events.IsAdmin)
            Body.Add(Ui.Btn("Zkušební akce (správce)", PencilVariant.Secondary, true, async (_, _) => await CreateTest()));

        var upcoming = list.Where(a => !a.Over).OrderBy(a => a.From).ToList();
        var past = list.Where(a => a.Over).OrderByDescending(a => a.From).ToList();
        if (upcoming.Count == 0) Body.Add(Ui.Small("Nic naplánovaného. Ty to tak necháš?", soft: true));
        foreach (var a in upcoming) Body.Add(Card(a));
        if (past.Count > 0)
        {
            var h = Ui.H("Proběhlé");
            h.Margin = new Thickness(0, 14, 0, 0);
            Body.Add(h);
            foreach (var a in past) Body.Add(Card(a));
        }
    }

    /// <summary>Správce: akce teď a tady na vyzkoušení (lístek, bečka, výzdoba, odkaz). Nikoho nezve.</summary>
    private async Task CreateTest()
    {
        var busy = Listek.ShowBusy(this, "Zkušební akce", "Hledám polohu a zakládám…");
        try
        {
            var loc = await PushService.LocateAsync();
            var (id, _) = await AkceService.CreateTestAsync(loc?.Lat, loc?.Lon);
            busy?.Dispose();
            await Shell.Current.GoToAsync("//AkceListek", new Dictionary<string, object> { ["id"] = id });
        }
        catch (Exception ex)
        {
            busy?.Dispose();
            await Listek.AlertAsync(this, "Nepovedlo se", Net.IsNetworkError(ex) ? "Bez signálu to nepůjde." : ex.Message);
        }
    }

    private View Card(AkceInfo a)
    {
        var box = new VerticalStackLayout { Spacing = 0, Margin = new Thickness(0, 8, 0, 0) };
        box.Add(Ui.H(a.Name));
        box.Add(Ui.Small($"{a.When}{(a.Place.Length > 0 ? " · " + a.Place : "")}"));
        var going = a.Status.Count(s => s.Value == "yes");
        var state = a.MyStatus switch { "yes" => "jdeš", "maybe" => "možná", "no" => "nejdeš", _ => "čeká na tvou odpověď" };
        box.Add(Ui.Small($"{(a.Running ? "běží · " : "")}{state} · jde {going}{(a.IsHost ? " · pořádáš" : "")}", soft: true));
        if (a.MyStatus is null or "wait")
        {
            var row = Ui.Cols(
                Ui.Btn("Jdu", PencilVariant.Primary, true, async (_, _) => await Respond(a, "yes")),
                Ui.Btn("Možná", PencilVariant.Secondary, true, async (_, _) => await Respond(a, "maybe")),
                Ui.Btn("Nejdu", PencilVariant.Secondary, true, async (_, _) => await Respond(a, "no")));
            row.Margin = new Thickness(0, 4, 0, 0);
            box.Add(row);
        }
        box.Tap(async () => await Shell.Current.GoToAsync("//AkceListek", new Dictionary<string, object> { ["id"] = a.Id }));
        return box;
    }

    private async Task Respond(AkceInfo a, string answer)
    {
        try
        {
            await AkceService.RespondAsync(a.Id, answer);
            Render(AkceService.Mine);
        }
        catch (Exception ex)
        {
            await Listek.AlertAsync(this, "Nepovedlo se", Net.IsNetworkError(ex) ? "Bez signálu to nepůjde." : ex.Message);
        }
    }

    private async void OnRefreshing(object? sender, EventArgs e) => await Load(force: true);

    private void OnZpetClicked(object sender, EventArgs e) => OnBackButtonPressed();

    protected override bool OnBackButtonPressed()
    {
        if (Listek.DismissActive()) return true;
        Dispatcher.Dispatch(async () => await Shell.Current.GoToAsync("//Friends"));
        return true;
    }
}
