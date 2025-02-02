using Dezolati.Models;
using Dezolati.Services;
using Firebase.Auth;
using Firebase.Auth.Repository;
using Microsoft.Maui.Layouts;

namespace Dezolati.Pages
{
    public partial class MainPage : ContentPage
    {
        private FirestoreService firestoreService;
        private bool dailyDrinksExists;
        public static MainPage Instance { get; private set; }
        public int pocetPiv;
        public int pocetVin;
        public int pocetPanaku;


        public MainPage()
        {
            InitializeComponent();

            firestoreService = new FirestoreService();
            Instance = this;
            LoadData();
        }
        private async void LoadData()
        {
            var userID = SignInModel.ID;

            if (userID != null)
            {
                try
                {
                    var currentDate = DateTime.UtcNow.Date;
                    var DailyDrinks = await firestoreService.GetDailyDrinks(userID, currentDate);

                    if (DailyDrinks != null)
                    {
                        // Načtení dat do UI
                        dailyDrinksExists = true;
                        UpdateUI(DailyDrinks.Pivo, DailyDrinks.Vino, DailyDrinks.Panaky);
                        pocetPiv = DailyDrinks.Pivo; pocetVin = DailyDrinks.Vino; pocetPanaku = DailyDrinks.Panaky;
                    }
                    else
                    {
                        // Záznam neexistuje
                        dailyDrinksExists = false;
                        SouhrnLabel.Text = "Dnešní spotřeba: žádná data.";
                    }
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

        private void UpdateUI(int pivo, int vino, int panaky)
        {
            // Aktualizace UI podle dat
            SouhrnLabel.Text = $"Dnešní spotřeba: {pivo} piv, {vino} vín, {panaky} panáků";
            AddDrink("Pivo", pivo);
            AddDrink("Víno", vino);
            AddDrink("Panáky", panaky);
        }

        private async void OnPridatClicked(object sender, EventArgs e)
        {
            await Shell.Current.GoToAsync("//Zapis");
        }

        public async void RecordDrink(int pivo, int vino, int panaky)
        {
            var userID = SignInModel.ID;
            var username = SignInModel.Username;

            if (userID != null)
            {
                try
                {
                    // Zápis do Firestore
                    await firestoreService.RecordDrink(userID, username, pivo, vino, panaky);

                    // Pokud denní záznam neexistoval, vytvoří se nový a načte se data
                    if (!dailyDrinksExists)
                    {
                        dailyDrinksExists = true;
                        LoadData(); // Aktualizuje UI
                    }
                    else
                    {
                        UpdateLocalData(pivo, vino, panaky); // Lokální aktualizace UI
                    }
                }
                catch (Exception ex)
                {
                    await DisplayAlert("Chyba", "Chyba při přidávání drinku: " + ex.Message, "OK");
                }
            }
        }

        public void UpdateLocalData(int addedPiva, int addedVino, int addedPanaky)
        {
            pocetPiv += addedPiva;
            pocetVin += addedVino;
            pocetPanaku += addedPanaky;

            SouhrnLabel.Text = $"Dnešní spotřeba: {pocetPiv} piv, {pocetVin} vín, {pocetPanaku} panáků";
            
            if (addedPiva > 0) {AddDrink("Pivo", addedPiva); }
            if (addedVino > 0) { AddDrink("Víno", addedVino); }
            if (addedPanaky > 0) { AddDrink("Panáky", addedPanaky); }

        }

        private Random random = new Random();
        private List<string> carkyImages = new List<string>
        {
            "carka1.png", "carka2.png", "carka3.png", "carka4.png",
            "carka5.png", "carka6.png", "carka7.png", "carka8.png",
            "carka9.png", "carka10.png"
        };

        private void AddCarky(FlexLayout flexLayout, int countToAdd)
        {
            for (int i = 0; i < countToAdd; i++)
            {
                int itemsInCurrentRow = flexLayout.Children.Count % 5;

                // Pokud je to pátá čárka, přidej přeškrtnutí
                if (itemsInCurrentRow == 4)
                {
                    var overlayImage = new Image
                    {
                        Source = carkyImages[random.Next(carkyImages.Count)],
                        WidthRequest = 40,
                        //HeightRequest = 50,
                        Rotation = -80,
                        Margin = new Thickness(-17,-10,0,0)
                    };

                    flexLayout.Children.Add(overlayImage);
                }
                else
                {
                    // Přidání jednotlivé čárky
                    var image = new Image
                    {
                        Source = carkyImages[random.Next(carkyImages.Count)],
                        WidthRequest = 40,
                        HeightRequest = 50,
                        Margin = new Thickness(0, 0, -12, 0)
                    };

                    flexLayout.Children.Add(image);
                }

            }
        }


        // Přidání drinků podle typu (a množství)
        private void AddDrink(string type, int count)
        {
            switch (type)
            {
                case "Pivo":
                    AddCarky(Pivo, count);
                    break;
                case "Víno":
                    AddCarky(Vino, count);
                    break;
                case "Panáky":
                    AddCarky(Panaky, count);
                    break;
            }
        }

        private async void OnResetClicked(object sender, EventArgs e)
        {
            //LoadData();
            Pivo.Clear();
            Vino.Clear();
            Panaky.Clear();
            AddDrink("Pivo", pocetPiv);
            AddDrink("Víno", pocetVin);
            AddDrink("Panáky", pocetPanaku);
        }

        private async void OnSettingsClicked(object sender, EventArgs e)
        {
            await Shell.Current.GoToAsync("//Settings");
        }
    }
}
