using System.Globalization;
using Dezolati.Controls;
using Dezolati.Services;

namespace Dezolati.Pages;

/// <summary>Novinky ze všech vydaných verzí (popisy vydání z GitHubu). Parametr navigace: back.</summary>
public partial class Novinky : ContentPage, IQueryAttributable
{
    private static readonly CultureInfo Cs = new("cs-CZ");
    private string back = "//Settings";

    public Novinky()
    {
        InitializeComponent();
    }

    public void ApplyQueryAttributes(IDictionary<string, object> query)
    {
        back = query.TryGetValue("back", out var b) && b is string s ? s : "//Settings";
    }

    protected override async void OnAppearing()
    {
        base.OnAppearing();
        VersionLabel.Text = $"Máš verzi {AppUpdater.CurrentVersionText}";
        await Load();
    }

    private async Task Load()
    {
        NotesList.Clear();
        StatusLabel.IsVisible = false;
        Busy.IsRunning = Busy.IsVisible = true;
        try
        {
            var (notes, fromCache) = await ReleaseNotes.GetAsync();
            ReleaseNotes.MarkSeen();

            if (fromCache)
                ShowStatus("Bez signálu – ukazuju poslední stažené novinky.");
            if (notes.Count == 0)
                ShowStatus("Zatím žádné vydané verze.");

            var current = AppUpdater.CurrentVersion;
            var first = true;
            foreach (var n in notes)
            {
                var title = n.Version != null ? $"Verze {n.Version.Major}.{n.Version.Minor}" : n.Tag;
                if (n.Version == current) title += " (tvoje)";
                else if (n.Version != null && n.Version > current) title += " (novější!)";

                NotesList.Add(new Label
                {
                    Text = title,
                    Style = AppStyle("Heading"),
                    HorizontalOptions = LayoutOptions.Start,
                    HorizontalTextAlignment = TextAlignment.Start,
                    Margin = new Thickness(0, first ? 6 : 18, 0, 0)
                });
                if (n.PublishedAt is DateTime at)
                    NotesList.Add(new Label { Text = at.ToString("d. M. yyyy", Cs), Style = AppStyle("Small") });
                NotesList.Add(new Label
                {
                    Text = n.Text.Length > 0 ? n.Text : "(bez popisu – autor byl líný)",
                    Style = AppStyle("Body")
                });
                first = false;
            }
        }
        catch (Exception ex)
        {
            ShowStatus($"Novinky se nepodařilo stáhnout: {ex.Message}");
        }
        finally
        {
            Busy.IsRunning = Busy.IsVisible = false;
        }
    }

    private void ShowStatus(string text)
    {
        StatusLabel.Text = text;
        StatusLabel.IsVisible = true;
    }

    private static Style? AppStyle(string key) =>
        Application.Current?.Resources.TryGetValue(key, out var s) == true ? s as Style : null;

    private void OnZpetClicked(object sender, EventArgs e) => OnBackButtonPressed();

    protected override bool OnBackButtonPressed()
    {
        if (Listek.DismissActive()) return true;
        Dispatcher.Dispatch(async () => await Shell.Current.GoToAsync(back));
        return true;
    }
}
