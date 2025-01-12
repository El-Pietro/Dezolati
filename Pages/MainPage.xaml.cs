using Dezolati.Models;
using Dezolati.Services;

namespace Dezolati.Pages
{
    public partial class MainPage : ContentPage
    {   
        private int pocetPiv = 0;
        

        public MainPage()
        {
            InitializeComponent();
        }

        private async void OnPridatClicked(object sender, EventArgs e)
        {
            pocetPiv++;
            SouhrnLabel.Text = $"Dnešní souhrn: {pocetPiv} piv";

            var firestoreService = new FirestoreService();
            var sample = new DrinkModel
            {
                UserId = SignInModel.ID,
                Name = SignInModel.Username,
                DrinkType = 1,
                Quantity = 1,
                DrinkAt = DateTime.Now
            };

            await firestoreService.InsertDrinkModel(sample);

            // Přidání nové čárky
            if (pocetPiv % 5 == 0)
            {
                // Pokud je pět čárek, nahradíme je obrázkem pro 5 čárek
                for (int i = 0; i < 4; i++)
                {
                    ListekLayout.Children.RemoveAt(ListekLayout.Children.Count - 1);
                }

                var image5 = new Image
                {
                    Source = "pet.png",
                    WidthRequest = 50,
                    HeightRequest = 50,
                    Margin = new Thickness(5)
                };

                ListekLayout.Children.Add(image5);
            }
            else
            {
                // Přidání jednotlivé čárky
                var image1 = new Image
                {
                    Source = "carka.png",
                    WidthRequest = 20,
                    HeightRequest = 50,
                    Margin = new Thickness(5)
                };

                ListekLayout.Children.Add(image1);
            }
        }
    }

}
