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
    private string[] editStart = Array.Empty<string>();   // hodnoty při otevření úpravy (kvůli „nic se nezměnilo“)

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
            await PubService.LoadOverlayAsync();
            pub = PubService.Find(id);
            if (pub == null)
            {
                NameLabel.Text = "Hospoda zmizela";
                InfoLabel.Text = "V seznamu z OpenStreetMap už není.";
                return;
            }

            NameLabel.Text = PubService.NameOf(pub);
            InfoLabel.Text = string.IsNullOrEmpty(pub.Address) ? pub.KindText : $"{pub.KindText} · {pub.Address}";

            RenderOsmName();

            WebButton.IsVisible = !string.IsNullOrEmpty(pub.Website);
            CallButton.IsVisible = !string.IsNullOrEmpty(pub.Phone);
            ContactBox.IsVisible = WebButton.IsVisible || CallButton.IsVisible;

            RenderProfile(); // prázdné, než dorazí Firestore
            RenderState();
            profile = await PubService.GetProfileAsync(pub.Id) ?? new PubProfile();
            NameLabel.Text = PubService.NameOf(pub);
            RenderOsmName();
            RenderProfile();
            RenderState();
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

    private void RenderOsmName()
    {
        if (pub == null) return;
        OsmLabel.Text = PubService.IsCustom(pub) ? $"původní název: {pub.Name}" : $"v OpenStreetMap: {pub.Name}";
        OsmLabel.IsVisible = PubService.NameOf(pub) != pub.Name;
    }

    /// <summary>Zavřeno / přidané uživatelem / tlačítka podle toho.</summary>
    private void RenderState()
    {
        if (pub == null) return;
        var custom = PubService.IsCustom(pub);
        ClosedLabel.IsVisible = profile.Closed;
        ClosedButton.Text = profile.Closed ? "Zase otevřeno" : "Natrvalo zavřeno";
        ClosedButton.Variant = profile.Closed ? PencilVariant.Primary : PencilVariant.Danger;
        DeleteButton.IsVisible = custom && PubService.IsMine(pub);

        var info = pub.KindText;
        if (!string.IsNullOrEmpty(pub.Address)) info += $" · {pub.Address}";
        if (custom) info += $" · přidal(a) {profile.CreatedByNick ?? "?"}";
        InfoLabel.Text = info;
    }

    // co se ukáže / předvyplní, když uživatelé nic nezadali: údaje z OpenStreetMap
    private string? OsmTaps => pub?.Brewery;
    private string? OsmHoursCz => OsmHours.ToCzech(pub?.Hours);

    private void RenderProfile()
    {
        static string Or(string? s) => string.IsNullOrWhiteSpace(s) ? "—" : s;

        var tapsFromOsm = string.IsNullOrWhiteSpace(profile.Taps) && !string.IsNullOrWhiteSpace(OsmTaps);
        TapsLabel.Text = Or(tapsFromOsm ? OsmTaps : profile.Taps);
        TapsSourceLabel.IsVisible = tapsFromOsm;

        var hoursFromOsm = string.IsNullOrWhiteSpace(profile.Hours) && !string.IsNullOrWhiteSpace(OsmHoursCz);
        HoursLabel.Text = Or(hoursFromOsm ? OsmHoursCz : profile.Hours);
        HoursSourceLabel.IsVisible = hoursFromOsm;

        PricesLabel.Text = Or(profile.Prices);
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
        // když nikdo nic nezadal, předvyplnit z OpenStreetMap (jde přepsat)
        TapsEntry.Text = string.IsNullOrWhiteSpace(profile.Taps) ? OsmTaps : profile.Taps;
        PricesEntry.Text = profile.Prices;
        HoursEntry.Text = string.IsNullOrWhiteSpace(profile.Hours) ? OsmHoursCz : profile.Hours;
        NoteEntry.Text = profile.Note;
        editStart = CurrentEditValues();
        SetEditing(true);
    }

    private void OnCancelEditClicked(object sender, EventArgs e) => SetEditing(false);

    private static string Norm(string? s) => (s ?? "").Trim();

    private string[] CurrentEditValues() => new[]
    {
        Norm(NameEntry.Text), Norm(TapsEntry.Text), Norm(PricesEntry.Text), Norm(HoursEntry.Text), Norm(NoteEntry.Text)
    };

    private async void OnSaveClicked(object sender, EventArgs e)
    {
        if (pub == null) return;

        // nic se nezměnilo → neukládat (historie by se plnila prázdnými záznamy)
        if (CurrentEditValues().SequenceEqual(editStart))
        {
            SetEditing(false);
            await Listek.AlertAsync(this, "Nic nového", "Nic jsi nezměnil, takže není co ukládat.");
            return;
        }

        var updated = new PubProfile
        {
            DisplayName = NameEntry.Text,
            Taps = TapsEntry.Text,
            Prices = PricesEntry.Text,
            Hours = HoursEntry.Text,
            Note = NoteEntry.Text,
            Closed = profile.Closed
        };

        try
        {
            SaveButton.IsEnabled = false;
            await PubService.SaveAsync(pub, updated);
            profile = await PubService.GetProfileAsync(pub.Id) ?? updated;
            NameLabel.Text = PubService.NameOf(pub);
            RenderOsmName();
            RenderProfile();
            RenderState();
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

            // každá úprava jen s tím, co se oproti předchozí změnilo
            for (var i = 0; i < edits.Count; i++)
            {
                var ed = edits[i];
                // nejstarší načtená: když jich je víc, než se načetlo, předchozí neznáme → ukázat celé
                PubEdit? prev = i + 1 < edits.Count ? edits[i + 1]
                    : edits.Count >= PubService.HistoryLimit ? null
                    : new PubEdit();

                var lines = new List<string>
                {
                    $"{ed.At?.ToString("d. M. yyyy H:mm", Cs) ?? "?"} – {ed.ByNick ?? "?"}"
                };
                if (ed.Action == "created")
                {
                    lines.Add($"přidal(a) podnik „{ed.DisplayName}“");
                    HistoryList.Add(new Label { Text = string.Join("\n", lines), Style = AppStyle("Small") });
                    continue;
                }
                if (prev != null ? ed.Closed != prev.Closed : ed.Closed)
                    lines.Add(ed.Closed ? "označil(a) jako natrvalo zavřeno" : "znovu otevřeno");
                void Diff(string label, string? now, string? before)
                {
                    if (prev == null ? Norm(now).Length == 0 : Norm(now) == Norm(before)) return;
                    lines.Add(string.IsNullOrWhiteSpace(now) ? $"{label}: (smazáno)" : $"{label}: {now}");
                }
                Diff("název", ed.DisplayName, prev?.DisplayName);
                Diff("točí", ed.Taps, prev?.Taps);
                Diff("ceny", ed.Prices, prev?.Prices);
                Diff("otvíračka", ed.Hours, prev?.Hours);
                Diff("poznámka", ed.Note, prev?.Note);
                if (lines.Count == 1) lines.Add("(beze změny)");

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

    private async void OnClosedClicked(object sender, EventArgs e)
    {
        if (pub == null) return;
        var closing = !profile.Closed;
        var ok = closing
            ? await Listek.ConfirmAsync(this, "Zavřeno natrvalo?",
                $"„{PubService.NameOf(pub)}“ na mapě zešedne. Kdyby to nebyla pravda, kdokoli to vrátí.", "Zavřeno", danger: true)
            : await Listek.ConfirmAsync(this, "Zase otevřeno?", $"„{PubService.NameOf(pub)}“ zase jede?", "Otevřeno");
        if (!ok) return;

        var updated = new PubProfile
        {
            DisplayName = PubService.NameOf(pub),
            Taps = profile.Taps,
            Prices = profile.Prices,
            Hours = profile.Hours,
            Note = profile.Note,
            Closed = closing
        };
        try
        {
            ClosedButton.IsEnabled = false;
            await PubService.SaveAsync(pub, updated);
            profile = await PubService.GetProfileAsync(pub.Id) ?? updated;
            RenderProfile();
            RenderState();
            HistoryList.Clear();
            HistoryButton.IsVisible = true;
        }
        catch (Exception ex)
        {
            await Listek.AlertAsync(this, "Chyba", $"Uložit se nepovedlo: {ex.Message}");
        }
        finally
        {
            ClosedButton.IsEnabled = true;
        }
    }

    private async void OnDeleteClicked(object sender, EventArgs e)
    {
        if (pub == null) return;
        if (!await Listek.ConfirmAsync(this, "Smazat podnik?",
                $"„{PubService.NameOf(pub)}“ zmizí z mapy všem. Jestli jen zavřeli, dej radši „Natrvalo zavřeno“.",
                "Smazat", danger: true))
            return;
        try
        {
            await PubService.DeleteAsync(pub);
            pub = null;
            await Shell.Current.GoToAsync("//Hospody");
        }
        catch (Exception ex)
        {
            await Listek.AlertAsync(this, "Chyba", $"Smazat se nepovedlo: {ex.Message}");
        }
    }

    private async void OnWebClicked(object sender, EventArgs e)
    {
        if (string.IsNullOrEmpty(pub?.Website)) return;
        var url = pub.Website.StartsWith("http", StringComparison.OrdinalIgnoreCase) ? pub.Website : "https://" + pub.Website;
        try { await Launcher.Default.OpenAsync(new Uri(url)); }
        catch (Exception ex) { await Listek.AlertAsync(this, "Chyba", $"Web se nepodařilo otevřít: {ex.Message}"); }
    }

    private async void OnCallClicked(object sender, EventArgs e)
    {
        if (string.IsNullOrEmpty(pub?.Phone)) return;
        var number = new string(pub.Phone.Where(c => char.IsDigit(c) || c == '+').ToArray());
        if (await Listek.ConfirmAsync(this, "Zavolat?", pub.Phone, "Zavolat") == false) return;
        try { await Launcher.Default.OpenAsync(new Uri("tel:" + number)); }   // jen otevře vytáčení, nevolá samo
        catch (Exception ex) { await Listek.AlertAsync(this, "Chyba", $"Vytáčení se nepodařilo otevřít: {ex.Message}"); }
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
