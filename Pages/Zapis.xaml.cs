using Dezolati.Controls;
using Dezolati.Services;

namespace Dezolati.Pages;

public partial class Zapis : ContentPage
{
    private readonly FirestoreService firestoreService = new();
    private string druh = PanakDruhy.Ostatni;

    public Zapis()
    {
        InitializeComponent();
    }

    protected override async void OnAppearing()
    {
        base.OnAppearing();

        // výchozí zápis podle typu píče: pivopíč = 1 pivo, vínopíč = 1 víno (2 dcl), tvrdopíč = 1 panák
        SetDefaults(UserSession.Profile?.DrinkerType);

        var userId = AuthSession.Uid;
        if (userId == null) return;

        try
        {
            await UserSession.EnsureReadyAsync(); // z paměti, žádné čtení navíc
            SetDefaults(UserSession.Profile?.DrinkerType);
        }
        catch
        {
            // bez profilu prostě zůstane pivo
        }
    }

    private void SetDefaults(string? drinkerType)
    {
        PivaSlider.Value = drinkerType is "Vínopíč/ka" or "Tvrdopíč/ka" ? 0 : 1;
        VinoSlider.Value = drinkerType == "Vínopíč/ka" ? 1 : 0;
        PanakySlider.Value = drinkerType == "Tvrdopíč/ka" ? 1 : 0;
        MichackySlider.Value = 0;
        SetDruh(PanakDruhy.Ostatni);
    }

    private void SetDruh(string key)
    {
        var d = PanakDruhy.Find(key);
        druh = d.Key;
        DruhLabel.Text = $"{d.Name.ToLowerInvariant()} ▾";
    }

    /// <summary>Šipečka u panáků: slivovice, likér, rum, vodka, whisky, ostatní.</summary>
    private async void OnDruhTapped(object? sender, TappedEventArgs e)
    {
        var buttons = PanakDruhy.All
            .Select(d => (d.Name, d.Key == druh ? PencilVariant.Primary : PencilVariant.Secondary))
            .ToArray();
        var choice = await Listek.ShowAsync(this, "Jakej panák?", "Pořád je to panák, jen se líp spočítají promile.", buttons);
        if (PanakDruhy.All.FirstOrDefault(d => d.Name == choice) is { } picked) SetDruh(picked.Key);
    }

    private void OnSliderValueChanged(object sender, ValueChangedEventArgs e)
    {
        var slider = (Slider)sender;
        var rounded = Math.Round(e.NewValue);
        if (slider.Value != rounded)
        {
            slider.Value = rounded; // přicvaknout na celá čísla
            return;
        }

        var text = ((int)rounded).ToString();
        if (slider == PivaSlider) PivaLabel.Text = text;
        else if (slider == VinoSlider) VinoLabel.Text = text;
        else if (slider == PanakySlider) PanakyLabel.Text = text;
        else if (slider == MichackySlider) MichackyLabel.Text = text;
    }

    private async void OnZapsatClicked(object sender, EventArgs e)
    {
        int piva = (int)Math.Round(PivaSlider.Value);
        int vino = (int)Math.Round(VinoSlider.Value);
        int panaky = (int)Math.Round(PanakySlider.Value);
        int michacky = (int)Math.Round(MichackySlider.Value);

        if (piva + vino + panaky + michacky == 0)
        {
            await Listek.AlertAsync(this, "Nula?", "Zapsat nic? To si napiš do deníku, ne na lístek.");
            return;
        }

        var userId = AuthSession.Uid;
        if (userId == null)
        {
            await Shell.Current.GoToAsync("//SignIn");
            return;
        }

        try
        {
            SetBusy(true);
            await Reminders.EnsurePermissionAsync();   // jednou se zeptá na povolení notifikací
            await firestoreService.RecordDrink(userId, AuthSession.DisplayName, piva, vino, panaky,
                michacky: michacky, panakDruh: druh);
            await Shell.Current.GoToAsync("//MainPage"); // MainPage si data načte sama
        }
        catch (Exception ex)
        {
            await Listek.AlertAsync(this, "Chyba", $"Chyba při zápisu: {ex.Message}");
        }
        finally
        {
            SetBusy(false);
        }
    }

    private void SetBusy(bool busy)
    {
        ZapsatButton.IsEnabled = !busy;
        Busy.IsRunning = busy;
        Busy.IsVisible = busy;
    }

    private void OnZpetClicked(object sender, EventArgs e) => OnBackButtonPressed();

    // systémové tlačítko Zpět: nejdřív zavřít případný lístek s hláškou, pak o stránku zpátky
    protected override bool OnBackButtonPressed()
    {
        if (Listek.DismissActive()) return true;
        Dispatcher.Dispatch(async () => await Shell.Current.GoToAsync("//MainPage"));
        return true;
    }
}
