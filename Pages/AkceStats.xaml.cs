using Dezolati.Controls;
using Dezolati.Services;

namespace Dezolati.Pages;

/// <summary>
/// Statistiky akce: živě během akce, po konci „Výsledky“. Pivo z akčního lístku, víno a panáky z osobních lístků
/// (spočítá server – functions/akce.js, akce „stats“). Tituly: Král piva, Král večera, Rychlík, Vytrvalec, Padl první.
/// </summary>
public partial class AkceStats : ContentPage, IQueryAttributable
{
    private string? id;
    private readonly AkceDecorHost decor;

    public AkceStats()
    {
        InitializeComponent();
        decor = new AkceDecorHost(this, Fx);
    }

    private bool visible;

    public void ApplyQueryAttributes(IDictionary<string, object> query)
    {
        var old = id;
        if (query.TryGetValue("id", out var v) && v is string s) id = s;
        query.Clear();
        if (visible && id != old) Dispatcher.Dispatch(async () => await Load());   // parametry dorazily až po OnAppearing
    }

    protected override async void OnAppearing()
    {
        base.OnAppearing();
        visible = true;
        decor.Start();
        await Load();
    }

    protected override void OnDisappearing()
    {
        base.OnDisappearing();
        visible = false;
        decor.Stop();
    }

    private async Task Load()
    {
        if (id == null) return;
        var a = AkceService.Cached(id);
        Body.Clear();
        if (a != null)
        {
            // po konci slavnostně i s padajícími věcmi
            var look = AkceScene.Of(a);
            decor.Look = a.Over ? look : look with { Fall = "none" };
            if (decor.Header is { } head) Body.Add(head);
            _ = AkceService.LoadMediaAsync(a);
        }
        Body.Add(Ui.Title(a?.Over == true ? "Výsledky" : "Statistiky"));
        if (a != null) Body.Add(Ui.Small($"{a.Name} · {a.When}", soft: true));
        var loading = Ui.Small("Počítám…", soft: true);
        Body.Add(loading);
        try
        {
            var s = await AkceService.StatsAsync(id);
            Body.Remove(loading);
            Render(a, s);
        }
        catch (Exception ex)
        {
            loading.Text = (Net.IsNetworkError(ex) ? "Bez signálu nic nespočítám." : $"Nepovedlo se: {ex.Message}") + " ";
        }
        finally
        {
            Refresh.IsRefreshing = false;
        }
    }

    private void Render(AkceInfo? a, AkceService.AkceStats s)
    {
        Body.Add(Ui.H($"{s.Pivo} {Piv(s.Pivo)} · {s.Liters:0.#} l"));
        if (s.People.Count > 0)
        {
            var other = s.People.Sum(p => p.Vino + p.Panaky + p.Michacky);
            if (other > 0) Body.Add(Ui.Small($"a k tomu {other}× víno / panák / míchaný z osobních lístků", soft: true));
        }

        var titles = new (string Key, string Label)[]
        {
            ("kralPiva", "👑 Král piva"), ("kralVecera", "🏆 Král večera"), ("rychlik", "⚡ Rychlík"),
            ("vytrvalec", "🦉 Vytrvalec"), ("padlPrvni", "💤 Padl první")
        };
        var shown = titles.Where(t => s.Titles.TryGetValue(t.Key, out var n) && !string.IsNullOrEmpty(n)).ToList();
        if (shown.Count > 0)
        {
            Body.Add(Section("Tituly"));
            foreach (var (key, label) in shown) Body.Add(Line(label, s.Titles[key]!));
        }

        if (s.People.Count > 0)
        {
            Body.Add(Section("Kdo kolik"));
            var place = 0;
            foreach (var p in s.People.OrderByDescending(p => p.Pivo).ThenByDescending(p => p.Total))
            {
                place++;
                var extra = new List<string>();
                if (p.Vino > 0) extra.Add($"víno {p.Vino}");
                if (p.Panaky > 0) extra.Add($"panáky {p.Panaky}");
                if (p.Michacky > 0) extra.Add($"míchané {p.Michacky}");
                Body.Add(Line($"{place}. {p.Name}", $"{p.Pivo} {Piv(p.Pivo)}"));
                var sub = new List<string>();
                if (extra.Count > 0) sub.Add(string.Join(", ", extra));
                if (p.First is DateTime f && p.Last is DateTime l) sub.Add($"{f:H:mm}–{l:H:mm}");
                if (p.Tempo is int t) sub.Add($"pivo za {t} min");
                if (sub.Count > 0) Body.Add(Ui.Small(string.Join(" · ", sub), soft: true));
            }
        }

        if (s.Days.Count > 1)
        {
            Body.Add(Section("Po dnech"));
            var cs = new System.Globalization.CultureInfo("cs-CZ");
            foreach (var (day, beers) in s.Days)
                Body.Add(Line(DateTime.TryParse(day, out var d) ? $"{d.ToString("dddd", cs)} {d.Day}. {d.Month}." : day, $"{beers} {Piv(beers)}"));
        }

        if (s.Kegs.Count > 0)
        {
            Body.Add(Section("Bečky"));
            foreach (var k in s.Kegs)
            {
                var when = k.Tapped is DateTime t
                    ? k.Empty is DateTime e ? $"{t:H:mm} → {e:H:mm} ({(int)(e - t).TotalHours} h {(e - t).Minutes} min)" : $"naražená {t:H:mm}"
                    : "";
                Body.Add(Line($"{k.Name} {k.Liters:0.#} l", $"{k.Beers} {Piv(k.Beers)}"));
                if (when.Length > 0) Body.Add(Ui.Small(when, soft: true));
            }
        }

        if (s.Pivo == 0) Body.Add(Ui.Small("Zatím se nic nevypilo. To je ostuda.", soft: true));
        if (a != null && !a.Over)
            Body.Add(Ui.Small("Počítá se průběžně, výsledky přijdou pushem po konci akce.", soft: true));
    }

    private static Label Section(string t)
    {
        var h = Ui.H(t);
        h.Margin = new Thickness(0, 12, 0, 0);
        return h;
    }

    private static Grid Line(string left, string right)
    {
        var g = new Grid { ColumnDefinitions = new ColumnDefinitionCollection(new ColumnDefinition(GridLength.Star), new ColumnDefinition(GridLength.Auto)) };
        g.Add(Ui.Body(left), 0, 0);
        var r = Ui.Body(right);
        r.HorizontalOptions = LayoutOptions.End;
        g.Add(r, 1, 0);
        return g;
    }

    private static string Piv(int n) => n == 1 ? "pivo" : n is >= 2 and <= 4 ? "piva" : "piv";

    private async void OnRefreshing(object? sender, EventArgs e) => await Load();

    private void OnZpetClicked(object sender, EventArgs e) => OnBackButtonPressed();

    protected override bool OnBackButtonPressed()
    {
        if (Listek.DismissActive()) return true;
        Dispatcher.Dispatch(async () => await Shell.Current.GoToAsync("//AkceListek",
            id != null ? new Dictionary<string, object> { ["id"] = id } : new Dictionary<string, object>()));
        return true;
    }
}
