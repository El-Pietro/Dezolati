using Dezolati.Services;

namespace Dezolati.Pages;

public partial class Zapis : ContentPage
{
    private readonly FirestoreService firestoreService = new();

    public Zapis()
    {
        InitializeComponent();
    }

    protected override void OnAppearing()
    {
        base.OnAppearing();
        // každý zápis začíná od "jedno pivo", ne od posledních hodnot
        PivaSlider.Value = 1;
        VinoSlider.Value = 0;
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
            await DisplayAlert("Nula?", "Zapsat nic? To si napiš do deníku, ne na lístek.", "OK");
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
            await firestoreService.RecordDrink(userId, AuthSession.DisplayName, piva, vino, panaky);
            await Shell.Current.GoToAsync("//MainPage"); // MainPage si data načte sama
        }
        catch (Exception ex)
        {
            await DisplayAlert("Chyba", $"Chyba při zápisu: {ex.Message}", "OK");
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

    private async void OnZpetClicked(object sender, EventArgs e)
    {
        await Shell.Current.GoToAsync("//MainPage");
    }
}
