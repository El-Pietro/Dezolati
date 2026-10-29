using System.Globalization;
using Dezolati.Controls;
using Dezolati.Services;

namespace Dezolati.Pages;

/// <summary>
/// Jen pro správce: návrhy poznámek do OpenStreetMap od uživatelů (zavřeno / zase otevřeno / nový podnik).
/// Odeslat = anonymní OSM poznámka (bez účtu), Zahodit = smazat návrh.
/// </summary>
public partial class OsmNavrhy : ContentPage
{
    private static readonly CultureInfo Cs = new("cs-CZ");

    public OsmNavrhy()
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
            var reports = await OsmReports.ListAsync();
            if (reports.Count == 0)
            {
                StatusLabel.Text = "Nic nečeká. Hospody stojí, kde stály.";
                StatusLabel.IsVisible = true;
            }
            foreach (var r in reports) ListBox.Add(Row(r));
        }
        catch (Exception ex)
        {
            StatusLabel.Text = Net.IsNetworkError(ex) ? "Bez signálu nic nenačtu." : $"Nepodařilo se načíst: {ex.Message}";
            StatusLabel.IsVisible = true;
        }
        finally
        {
            Busy.IsRunning = Busy.IsVisible = false;
            RefreshButton.IsEnabled = true;
        }
    }

    private View Row(OsmReport r)
    {
        var box = new VerticalStackLayout { Spacing = 2 };
        box.Add(new Label { Text = $"{r.Name} – {r.WhatText}", Style = AppStyle("Heading") });
        var when = r.At is DateTime at ? at.ToLocalTime().ToString("d. M. H:mm", Cs) : "";
        box.Add(new Label { Text = $"{r.ByNick ?? "?"} · {when}", Style = AppStyle("Small") });
        box.Add(new Label { Text = OsmReports.NoteText(r), Style = AppStyle("Small"), TextColor = InkSoft() });

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
        var send = Btn("Odeslat", PencilVariant.Primary);
        var drop = Btn("Zahodit", PencilVariant.Danger);
        map.Clicked += async (_, _) => await Shell.Current.GoToAsync("//Hospoda", new Dictionary<string, object>
        {
            ["pubId"] = r.PubId, ["lat"] = r.Lat, ["lon"] = r.Lon, ["name"] = r.Name, ["kind"] = r.Kind, ["back"] = "//OsmNavrhy"
        });
        send.Clicked += async (_, _) => await Handle(r, box, send: true);
        drop.Clicked += async (_, _) => await Handle(r, box, send: false);
        buttons.Add(map, 0, 0);
        buttons.Add(send, 1, 0);
        buttons.Add(drop, 2, 0);
        box.Add(buttons);
        return box;
    }

    private async Task Handle(OsmReport r, View row, bool send)
    {
        row.IsEnabled = false;
        try
        {
            if (send) await OsmReports.SendAsync(r);
            else await OsmReports.DiscardAsync(r);
            ListBox.Remove(row);
            if (ListBox.Count == 0)
            {
                StatusLabel.Text = send ? "Odesláno. Mapovači se činí." : "Nic nečeká.";
                StatusLabel.IsVisible = true;
            }
        }
        catch (Exception ex)
        {
            row.IsEnabled = true;
            await Listek.AlertAsync(this, "Nepovedlo se", Net.IsNetworkError(ex) ? "Bez signálu to nepůjde." : ex.Message);
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
        Dispatcher.Dispatch(async () => await Shell.Current.GoToAsync("//Settings"));
        return true;
    }
}
