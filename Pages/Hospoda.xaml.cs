using System.Globalization;
using Dezolati.Controls;
using Dezolati.Models;
using Dezolati.Services;

namespace Dezolati.Pages;

/// <summary>
/// Profil jedné hospody. Základ (jméno, adresa) je z OpenStreetMap, zbytek vyplňují uživatelé.
/// Parametry navigace: pubId.
/// </summary>
public partial class Hospoda : ContentPage, IQueryAttributable
{
    private static readonly CultureInfo Cs = new("cs-CZ");

    private PubInfo? pub;
    private PubProfile profile = new();

    public Hospoda()
    {
        InitializeComponent();
    }

    public void ApplyQueryAttributes(IDictionary<string, object> query)
    {
        var id = query.TryGetValue("pubId", out var v) ? v as string : null;
        _ = LoadAsync(id);
    }

    private async Task LoadAsync(string? id)
    {
        SetEditing(false);
        HistoryList.Clear();
        HistoryButton.IsVisible = true;
        profile = new PubProfile();

        if (string.IsNullOrEmpty(id)) return;

        try
        {
            Busy.IsRunning = Busy.IsVisible = true;

            await PubCatalog.GetAsync(); // z telefonu, nic se nestahuje
            pub = PubCatalog.Find(id);
            if (pub == null)
            {
                NameLabel.Text = "Hospoda zmizela";
                InfoLabel.Text = "V seznamu z OpenStreetMap už není.";
                return;
            }

            await PubService.LoadRenamedAsync();
            NameLabel.Text = PubService.NameOf(pub);
            InfoLabel.Text = string.IsNullOrEmpty(pub.Address) ? pub.KindText : $"{pub.KindText} · {pub.Address}";

            var osm = new List<string>();
            if (PubService.NameOf(pub) != pub.Name) osm.Add($"název: {pub.Name}");
            if (!string.IsNullOrEmpty(pub.Brewery)) osm.Add($"pivovar: {pub.Brewery}");
            if (!string.IsNullOrEmpty(pub.Hours)) osm.Add($"otvíračka: {pub.Hours}");
            OsmLabel.Text = "Podle OpenStreetMap – " + string.Join(", ", osm);
            OsmLabel.IsVisible = osm.Count > 0;

            RenderProfile(); // prázdné, než dorazí Firestore
            profile = await PubService.GetProfileAsync(pub.Id) ?? new PubProfile();
            NameLabel.Text = PubService.NameOf(pub);
            RenderProfile();
        }
        catch (Exception ex)
        {
            await Listek.AlertAsync(this, "Chyba", $"Hospodu se nepodařilo načíst: {ex.Message}");
        }
        finally
        {
            Busy.IsRunning = Busy.IsVisible = false;
        }
    }

    private void RenderProfile()
    {
        static string Or(string? s) => string.IsNullOrWhiteSpace(s) ? "—" : s;

        TapsLabel.Text = Or(profile.Taps);
        PricesLabel.Text = Or(profile.Prices);
        HoursLabel.Text = Or(profile.Hours);
        NoteLabel.Text = Or(profile.Note);

        UpdatedLabel.Text = profile.UpdatedAt is DateTime at
            ? $"Naposledy upravil(a) {profile.UpdatedByNick ?? "?"}, {at.ToString("d. M. yyyy", Cs)}"
            : "Zatím to nikdo nevyplnil. Buď první.";
    }

    private void SetEditing(bool editing)
    {
        ViewBox.IsVisible = !editing;
        EditBox.IsVisible = editing;
    }

    private void OnEditClicked(object sender, EventArgs e)
    {
        if (pub == null) return;
        NameEntry.Text = PubService.NameOf(pub);
        TapsEntry.Text = profile.Taps;
        PricesEntry.Text = profile.Prices;
        // když nikdo nic nezadal, nabídnout otvíračku z OpenStreetMap
        HoursEntry.Text = string.IsNullOrWhiteSpace(profile.Hours) ? pub.Hours : profile.Hours;
        NoteEntry.Text = profile.Note;
        SetEditing(true);
    }

    private void OnCancelEditClicked(object sender, EventArgs e) => SetEditing(false);

    private async void OnSaveClicked(object sender, EventArgs e)
    {
        if (pub == null) return;

        var updated = new PubProfile
        {
            DisplayName = NameEntry.Text,
            Taps = TapsEntry.Text,
            Prices = PricesEntry.Text,
            Hours = HoursEntry.Text,
            Note = NoteEntry.Text
        };

        try
        {
            SaveButton.IsEnabled = false;
            await PubService.SaveAsync(pub, updated);
            profile = await PubService.GetProfileAsync(pub.Id) ?? updated;
            NameLabel.Text = PubService.NameOf(pub);
            RenderProfile();
            HistoryList.Clear();
            HistoryButton.IsVisible = true;
            SetEditing(false);
        }
        catch (Exception ex)
        {
            await Listek.AlertAsync(this, "Chyba", $"Uložit se nepovedlo: {ex.Message}");
        }
        finally
        {
            SaveButton.IsEnabled = true;
        }
    }

    private async void OnHistoryClicked(object sender, EventArgs e)
    {
        if (pub == null) return;
        try
        {
            HistoryButton.IsEnabled = false;
            var edits = await PubService.GetHistoryAsync(pub.Id);
            HistoryList.Clear();
            HistoryButton.IsVisible = false;

            if (edits.Count == 0)
            {
                HistoryList.Add(new Label { Text = "Žádné úpravy.", Style = AppStyle("Small") });
                return;
            }

            foreach (var ed in edits)
            {
                var lines = new List<string>
                {
                    $"{ed.At?.ToString("d. M. yyyy H:mm", Cs) ?? "?"} – {ed.ByNick ?? "?"}"
                };
                if (!string.IsNullOrWhiteSpace(ed.DisplayName)) lines.Add($"název: {ed.DisplayName}");
                if (!string.IsNullOrWhiteSpace(ed.Taps)) lines.Add($"točí: {ed.Taps}");
                if (!string.IsNullOrWhiteSpace(ed.Prices)) lines.Add($"ceny: {ed.Prices}");
                if (!string.IsNullOrWhiteSpace(ed.Hours)) lines.Add($"otvíračka: {ed.Hours}");
                if (!string.IsNullOrWhiteSpace(ed.Note)) lines.Add($"poznámka: {ed.Note}");
                HistoryList.Add(new Label { Text = string.Join("\n", lines), Style = AppStyle("Small") });
            }
        }
        catch (Exception ex)
        {
            await Listek.AlertAsync(this, "Chyba", $"Historie: {ex.Message}");
        }
        finally
        {
            HistoryButton.IsEnabled = true;
        }
    }

    private static Style? AppStyle(string key) =>
        Application.Current?.Resources.TryGetValue(key, out var s) == true ? s as Style : null;

    private void OnZpetClicked(object sender, EventArgs e) => OnBackButtonPressed();

    protected override bool OnBackButtonPressed()
    {
        if (Listek.DismissActive()) return true;
        if (EditBox.IsVisible)
        {
            SetEditing(false); // Zpět při úpravě = zrušit úpravu
            return true;
        }
        Dispatcher.Dispatch(async () => await Shell.Current.GoToAsync("//Hospody"));
        return true;
    }
}
