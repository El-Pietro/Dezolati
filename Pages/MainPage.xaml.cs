using Dezolati.Services;

namespace Dezolati.Pages
{
    public partial class MainPage : ContentPage
    {
        private readonly FirestoreService firestoreService = new();

        private static readonly string[] CarkyImages =
        {
            "carka1.png", "carka2.png", "carka3.png", "carka4.png", "carka5.png",
            "carka6.png", "carka7.png", "carka8.png", "carka9.png", "carka10.png"
        };

        public MainPage()
        {
            InitializeComponent();
        }

        // Shell stránku drží v paměti, takže se data načítají při každém zobrazení
        // (třeba po návratu ze Zápisu), ne jen jednou v konstruktoru.
        protected override async void OnAppearing()
        {
            base.OnAppearing();
            await LoadData();
        }

        private async Task LoadData()
        {
            var userId = AuthSession.Uid;
            if (userId == null)
            {
                await Shell.Current.GoToAsync("//SignIn");
                return;
            }

            try
            {
                var day = await firestoreService.GetCurrentPubDay(userId);
                var today = await firestoreService.GetDailyDrinks(userId, day);
                Render(today?.Pivo ?? 0, today?.Vino ?? 0, today?.Panaky ?? 0);
            }
            catch (Exception ex)
            {
                SouhrnLabel.Text = "Lístek se nepodařilo načíst.";
                await DisplayAlert("Chyba", "Chyba při načítání dat: " + ex.Message, "OK");
            }
        }

        private void Render(int pivo, int vino, int panaky)
        {
            SouhrnLabel.Text = pivo + vino + panaky == 0
                ? "Zatím čistý lístek. Podezřelé."
                : "Dnes: " + Cz.Souhrn(pivo, vino, panaky);

            // pevný seed = čárky vypadají pokaždé stejně, neskáčou při každém načtení
            DrawCarky(Pivo, pivo, new Random(1));
            DrawCarky(Vino, vino, new Random(2));
            DrawCarky(Panaky, panaky, new Random(3));
        }

        private static void DrawCarky(FlexLayout flexLayout, int count, Random random)
        {
            flexLayout.Children.Clear();

            for (int i = 0; i < count; i++)
            {
                var source = CarkyImages[random.Next(CarkyImages.Length)];

                if (i % 5 == 4)
                {
                    // pátá čárka přeškrtne předchozí čtyři
                    flexLayout.Children.Add(new Image
                    {
                        Source = source,
                        WidthRequest = 40,
                        Rotation = -80,
                        Margin = new Thickness(-17, -10, 0, 0)
                    });
                }
                else
                {
                    flexLayout.Children.Add(new Image
                    {
                        Source = source,
                        WidthRequest = 40,
                        HeightRequest = 50,
                        Margin = new Thickness(0, 0, -12, 0)
                    });
                }
            }
        }

        private async void OnPridatClicked(object sender, EventArgs e)
        {
            await Shell.Current.GoToAsync("//Zapis");
        }

        private async void OnStatsClicked(object sender, EventArgs e)
        {
            await Shell.Current.GoToAsync("//StatsFriend", new Dictionary<string, object>
            {
                ["userId"] = AuthSession.Uid ?? "",
                ["nick"] = "Moje",
                ["back"] = "//MainPage"
            });
        }

        private async void OnFriendsClicked(object sender, EventArgs e)
        {
            await Shell.Current.GoToAsync("//Friends");
        }

        private async void OnSettingsClicked(object sender, EventArgs e)
        {
            await Shell.Current.GoToAsync("//Settings");
        }
    }
}
