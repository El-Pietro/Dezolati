using Dezolati.Controls;
using Dezolati.Services;

namespace Dezolati.Pages;

public partial class Zapis : ContentPage
{
    private readonly FirestoreService firestoreService = new();

    public Zapis()
    {
        InitializeComponent();
    }

    protected override async void OnAppearing()
    {
        base.OnAppearing();

        // výchozí zápis podle typu píče: pivopíč = 1 pivo, vínopíč = 1 víno (2 dcl)
        SetDefaults(wine: false);

        var userId = AuthSession.Uid;
        if (userId == null) return;

        try
        {
            await UserSession.EnsureReadyAsync(); // z paměti, žádné čtení navíc
            if (UserSession.Profile?.DrinkerType == "Vínopíč/ka")
                SetDefaults(wine: true);
        }
        catch
        {
            // bez profilu prostě zůstane pivo
        }
    }

    private void SetDefaults(bool wine)
    {
        PivaSlider.Value = wine ? 0 : 1;
        VinoSlider.Value = wine ? 1 : 0;
        PanakySlider.Value = 0;
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
    }

    private async void OnZapsatClicked(object sender, EventArgs e)
    {
        int piva = (int)Math.Round(PivaSlider.Value);
        int vino = (int)Math.Round(VinoSlider.Value);
        int panaky = (int)Math.Round(PanakySlider.Value);

        if (piva + vino + panaky == 0)
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
            await firestoreService.RecordDrink(userId, AuthSession.DisplayName, piva, vino, panaky);
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
