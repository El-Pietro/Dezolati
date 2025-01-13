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
            var tasks = new List<Task>();
            if (piva > 0)
            {
                tasks.Add(firestoreService.InsertDrinkModel(new DrinkModel
                {
                    UserId = userId,
                    Name = username,
                    DrinkType = 1,
                    Quantity = piva,
                    DrinkAt = DateTime.UtcNow
                }));
            }

            if (vino > 0)
            {
                tasks.Add(firestoreService.InsertDrinkModel(new DrinkModel
                {
                    UserId = userId,
                    Name = username,
                    DrinkType = 2,
                    Quantity = vino,
                    DrinkAt = DateTime.UtcNow
                }));
            }

            if (panaky > 0)
            {
                tasks.Add(firestoreService.InsertDrinkModel(new DrinkModel
                {
                    UserId = userId,
                    Name = username,
                    DrinkType = 3,
                    Quantity = panaky,
                    DrinkAt = DateTime.UtcNow
                }));
            }

            await Task.WhenAll(tasks);

            MainPage.Instance?.UpdateLocalData(piva, vino, panaky);
            

            await Shell.Current.GoToAsync("//MainPage");
        }
        else
        {
            await DisplayAlert("Chyba", "Uživatel není pøihlášen.", "OK");
        }
    }
}