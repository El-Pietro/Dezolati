using Dezolati.Models;
using Dezolati.Services;
using Firebase.Auth;
using Firebase.Auth.Repository;

namespace Dezolati.Pages
{
    public partial class MainPage : ContentPage
    {
        public static MainPage Instance { get; private set; }
        public int pocetPiv;
        public int pocetVin;
        public int pocetPanaku;


        public MainPage()
        {
            InitializeComponent();

            Instance = this;
            LoadData();
        }
        private async void LoadData()
        {
            var firestoreService = new FirestoreService();
            var userID = SignInModel.ID;

            if (userID != null)
            {
                try
                {
                    pocetPiv = await firestoreService.GetTotalDrinksByUser(userID, 1);
                    pocetVin = await firestoreService.GetTotalDrinksByUser(userID, 2);
                    pocetPanaku = await firestoreService.GetTotalDrinksByUser(userID, 3);

                    SouhrnLabel.Text = $"Dnešní spotřeba: {pocetPiv} piv, {pocetVin} vín, {pocetPanaku} panáků";
                    UpdateListekUI(pocetPiv);
                }
                catch (Exception ex)
                {
                    await DisplayAlert("Chyba", "Chyba při načítání dat: " + ex.Message, "OK");
                }
            }
            else
            {
                await DisplayAlert("Chyba", "Uživatel není přihlášen.", "OK");
            }
        }


        private async void OnPridatClicked(object sender, EventArgs e)
        {
            await Shell.Current.GoToAsync("//Zapis");
            //var firestoreService = new FirestoreService();
            //var button = (Button)sender;
            //button.IsEnabled = false;

            //try
            //{
            //    pocetPiv++;
            //    SouhrnLabel.Text = $"Dnešní souhrn: {pocetPiv} piv";

            //    var sample = new DrinkModel
            //    {
            //        UserId = SignInModel.ID,
            //        Name = SignInModel.Username,
            //        DrinkType = 1,
            //        Quantity = 1,
            //        DrinkAt = DateTime.Now
            //    };

            //    await firestoreService.InsertDrinkModel(sample);

            //    UpdateListekUI(pocetPiv);
            //}
            //catch (Exception ex)
            //{
            //    await DisplayAlert("Chyba", "Nepodařilo se přidat drink: " + ex.Message, "OK");
            //}
            //finally
            //{
            //    button.IsEnabled = true;
            //}
        }

        public void UpdateLocalData(int addedPiva, int addedVino, int addedPanaky)
        {
            pocetPiv += addedPiva;
            pocetVin += addedVino;
            pocetPanaku += addedPanaky;

            SouhrnLabel.Text = $"Dnešní spotřeba: {pocetPiv} piv, {pocetVin} vín, {pocetPanaku} panáků";
            UpdateListekUI(pocetPiv);
        }

        private void UpdateListekUI(int pocetPiv)
        {
            // Nejprve vyčistíme layout
            ListekLayout.Children.Clear();

            // Přidáme skupiny po 5 piv (ikony pet.png)
            int petek = pocetPiv / 5;
            for (int i = 0; i < petek; i++)
            {
                var image5 = new Image
                {
                    Source = "pet.png",
                    WidthRequest = 50,
                    HeightRequest = 50,
                    Margin = new Thickness(5)
                };
                ListekLayout.Children.Add(image5);
            }

            // Přidáme zbylé jednotlivé čárky (ikony carka.png)
            int carky = pocetPiv % 5;
            for (int i = 0; i < carky; i++)
            {
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
            
        //    // Přidání nové čárky
        //    if (pocetPiv % 5 == 0)
        //    {
        //        // Pokud je pět čárek, nahradíme je obrázkem pro 5 čárek
        //        for (int i = 0; i < 4; i++)
        //        {
        //            ListekLayout.Children.RemoveAt(ListekLayout.Children.Count - 1);
        //        }

        //        var image5 = new Image
        //        {
        //            Source = "pet.png",
        //            WidthRequest = 50,
        //            HeightRequest = 50,
        //            Margin = new Thickness(5)
        //        };

        //        ListekLayout.Children.Add(image5);
        //    }
        //    else
        //    {
        //        // Přidání jednotlivé čárky
        //        var image1 = new Image
        //        {
        //            Source = "carka.png",
        //            WidthRequest = 20,
        //            HeightRequest = 50,
        //            Margin = new Thickness(5)
        //        };

        //        ListekLayout.Children.Add(image1);
        //    }
        //}
    }

}
