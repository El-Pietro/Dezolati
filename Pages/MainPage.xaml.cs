using Dezolati.Controls;
using Dezolati.Services;

namespace Dezolati.Pages
{
    public partial class MainPage : ContentPage
    {
        private readonly FirestoreService firestoreService = new();

        // co je právě na lístku (kvůli škrtání)
        private DateTime shownDay;
        private int shownPivo, shownVino, shownPanaky;

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
            UserSession.DrinksChanged -= OnDrinksChanged;
            UserSession.DrinksChanged += OnDrinksChanged;
            await LoadData();
        }

        protected override void OnDisappearing()
        {
            base.OnDisappearing();
            UserSession.DrinksChanged -= OnDrinksChanged;
        }

        // zápis z notifikace / návrat do appky – může přijít z jiného vlákna
        private void OnDrinksChanged() =>
            MainThread.BeginInvokeOnMainThread(async () => await LoadData());

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
                await UserSession.EnsureReadyAsync(); // profil je v paměti, čte se jen poprvé
                var day = UserSession.CurrentPubDay;

                // nejdřív zkusit odeslat, co čeká ve frontě
                await DrinkQueue.FlushAsync(userId);

                Models.DailyDrinksModel? today;
                bool offline = false;
                try
                {
                    today = await firestoreService.GetDailyDrinks(userId, day);
                    LocalCache.SaveDaily(userId, day, today);
                }
                catch (Exception ex) when (Net.IsNetworkError(ex))
                {
                    today = LocalCache.LoadDaily(userId, day); // poslední známý stav
                    offline = true;
                }

                // + zápisy, které ještě nedorazily na server
                var pending = DrinkQueue.PendingFor(userId, day);

                shownDay = day;
                Render(Math.Max(0, (today?.Pivo ?? 0) + pending.Pivo),
                       Math.Max(0, (today?.Vino ?? 0) + pending.Vino),
                       Math.Max(0, (today?.Panaky ?? 0) + pending.Panaky),
                       pending.Count, offline);
            }
            catch (Exception ex)
            {
                SouhrnLabel.Text = "Lístek se nepodařilo načíst.";
                await Listek.AlertAsync(this, "Chyba", "Chyba při načítání dat: " + ex.Message);
            }
        }

        private void Render(int pivo, int vino, int panaky, int pendingCount = 0, bool offline = false)
        {
            shownPivo = pivo;
            shownVino = vino;
            shownPanaky = panaky;

            var text = pivo + vino + panaky == 0
                ? "Zatím čistý lístek."
                : "Dnes: " + Cz.Souhrn(pivo, vino, panaky);

            if (pendingCount > 0)
                text += $"\n({Cz.Plural(pendingCount, "zápis čeká", "zápisy čekají", "zápisů čeká")} na signál)";
            else if (offline)
                text += "\n(bez signálu)";

            SouhrnLabel.Text = text;

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

        private async void OnPlusTapped(object sender, TappedEventArgs e)
        {
            // tácek se při ťuknutí trochu zmáčkne
            await PlusButton.ScaleTo(0.88, 70, Easing.CubicOut);
            await PlusButton.ScaleTo(1.0, 90, Easing.CubicIn);
            await Shell.Current.GoToAsync("//Zapis");
        }

        /// <summary>Ťuknutí na řádek lístku → škrtnout jednu čárku.</summary>
        private async void OnRowTapped(object sender, TappedEventArgs e)
        {
            var userId = AuthSession.Uid;
            if (userId == null || e.Parameter is not string kind) return;

            var (count, name, one) = kind switch
            {
                "Pivo" => (shownPivo, "Pívo", "jedno pivo"),
                "Vino" => (shownVino, "Víno", "jedno víno"),
                _ => (shownPanaky, "Panáky", "jednoho panáka")
            };
            if (count <= 0) return;

            var strike = await Listek.ConfirmAsync(this, $"{name}: {count}", "Škrtnout čárku z lístku?",
                $"Škrtnout {one}", "Nic", danger: true);
            if (!strike) return;

            try
            {
                await firestoreService.RecordCorrection(userId, shownDay,
                    kind == "Pivo" ? 1 : 0, kind == "Vino" ? 1 : 0, kind == "Panaky" ? 1 : 0);
            }
            catch (Exception ex)
            {
                await Listek.AlertAsync(this, "Chyba", "Škrtnutí se nepovedlo: " + ex.Message);
            }
            await LoadData();
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

        private async void OnPubsClicked(object sender, EventArgs e)
        {
            await Shell.Current.GoToAsync("//Hospody");
        }

        // Zpět na lístku: zavřít případnou hlášku, jinak normálně z appky ven
        protected override bool OnBackButtonPressed() =>
            Listek.DismissActive() || base.OnBackButtonPressed();
    }
}
