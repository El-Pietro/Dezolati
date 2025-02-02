using Dezolati.Models;
using Dezolati.Services;

namespace Dezolati.Pages;

public partial class Zapis : ContentPage
{
	private readonly FirestoreService _firestoreService;
	public Zapis()
	{
		InitializeComponent();
	}

    private void OnSliderValueChanged(object sender, ValueChangedEventArgs e)
    {
        if (sender == PivaSlider)
        {
            PivaLabel.Text = ((int)PivaSlider.Value).ToString();
        }
        else if (sender == VinoSlider)
        {
            VinoLabel.Text = ((int)VinoSlider.Value).ToString();
        }
        else if (sender == PanakySlider)
        {
            PanakyLabel.Text = ((int)PanakySlider.Value).ToString();
        }
    }

    private async void OnZapsatClicked(object sender, EventArgs e)
    {
        var firestoreService = new FirestoreService();
        // Získání hodnot posuvníkù
        int piva = (int)PivaSlider.Value;
        int vino = (int)VinoSlider.Value;
        int panaky = (int)PanakySlider.Value;

        var userId = SignInModel.ID;
        var username = SignInModel.Username;

        if (userId != null)
        {
            try
            {
                // Zápis dat pomocí nové metody RecordDrink
                await firestoreService.RecordDrink(userId, username, piva, vino, panaky);

                // Aktualizace hlavní stránky (UI) a návrat na ni
                MainPage.Instance?.UpdateLocalData(piva, vino, panaky);
                await Shell.Current.GoToAsync("//MainPage");
            }
            catch (Exception ex)
            {
                await DisplayAlert("Chyba", $"Chyba pøi zápisu drinku: {ex.Message}", "OK");
            }
        }
        else
        {
            await DisplayAlert("Chyba", "Uživatel není pøihlášen.", "OK");
        }
    }
}