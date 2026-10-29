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
            FriendBadge.Changed -= OnBadgeChanged;
            FriendBadge.Changed += OnBadgeChanged;
            ShowBadge();
            StartPromileTimer();
            await LoadData();

            // oznámení od kámošů: povolení + token telefonu (jednou za spuštění)
            _ = PushService.InitAsync();

            // čekající žádosti o přátelství (jen při spuštění / návratu do appky / po oznámení)
            _ = FriendBadge.RefreshAsync();

            // po aktualizaci jednou: co je nového v téhle verzi
            await ShowWhatsNewAsync();

            // nová verze na GitHubu? (max. jednou za 6 h, chyby potichu)
            await UpdatePrompt.CheckAndOfferAsync(this);
        }

        protected override void OnDisappearing()
        {
            base.OnDisappearing();
            UserSession.DrinksChanged -= OnDrinksChanged;
            FriendBadge.Changed -= OnBadgeChanged;
            promileTimer?.Stop();
        }

        // ---------------- orientační promile (malým písmem) ----------------

        private static readonly System.Globalization.CultureInfo Cs = new("cs-CZ");
        private IDispatcherTimer? promileTimer;

        private void StartPromileTimer()
        {
            UpdatePromile();
            if (promileTimer == null)
            {
                promileTimer = Dispatcher.CreateTimer();
                promileTimer.Interval = TimeSpan.FromMinutes(1);
                promileTimer.Tick += (_, _) => UpdatePromile();
            }
            promileTimer.Start();
        }

        private void UpdatePromile()
        {
            var est = AuthSession.Uid is string uid ? Promile.Estimate(uid, UserSession.Profile?.Gender) : null;
            if (est is not { } e)
            {
                PromileLabel.IsVisible = false;
                return;
            }
            PromileLabel.Text = $"≈ {e.Promile.ToString("0.0", Cs)} ‰ · {Promile.SoberText(e.SoberAtLocal, UserSession.Profile?.Gender)} · jen odhad, neřiď podle toho";
            PromileLabel.IsVisible = true;
        }

        private void OnBadgeChanged() => MainThread.BeginInvokeOnMainThread(ShowBadge);

        private void ShowBadge()
        {
            var n = FriendBadge.Count;
            FriendBadgeLabel.Text = n > 9 ? "9+" : n.ToString();
            FriendBadgeView.IsVisible = n > 0;
        }

        private static bool _whatsNewChecked;

        private async Task ShowWhatsNewAsync()
        {
            if (_whatsNewChecked) return;
            _whatsNewChecked = true;
            try
            {
                var note = await ReleaseNotes.PendingForCurrentAsync();
                if (note == null) return;

                var text = note.Text.Length > 700 ? note.Text[..700] + "…" : note.Text;
                const string all = "Všechny novinky";
                var choice = await Listek.ShowAsync(this, $"Co je nového ve verzi {AppInfo.Current.VersionString}", text,
                    ("Super", PencilVariant.Primary),
                    (all, PencilVariant.Secondary));
                if (choice == all)
                    await Shell.Current.GoToAsync("//Novinky", new Dictionary<string, object> { ["back"] = "//MainPage" });
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Novinky: {ex.Message}");
            }
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

                // nejdřív zkusit odeslat, co čeká ve frontě (sítové chyby si řeší sama)
                await DrinkQueue.FlushAsync(userId);

                Models.DailyDrinksModel? today;
                bool offline = !Net.IsOnline;
                if (offline)
                {
                    // telefon hlásí, že je bez internetu → na server se ani nezkoušet
                    today = LocalCache.LoadDaily(userId, day);
                }
                else if (LocalCache.IsFresh(userId, day))
                {
                    // lístek v telefonu je čerstvý (vlastní zápisy se do něj přičítají) → nic nečíst
                    today = LocalCache.LoadDaily(userId, day);
                }
                else
                {
                    try
                    {
                        today = await firestoreService.GetDailyDrinks(userId, day);
                        LocalCache.SaveDaily(userId, day, today);
                        LocalCache.MarkFresh(userId, day);
                    }
                    catch (Exception ex) when (Net.IsNetworkError(ex))
                    {
                        today = LocalCache.LoadDaily(userId, day); // poslední známý stav
                        offline = true;
                    }
                }

                RenderWithPending(userId, day, today, offline);
            }
            catch (Exception ex)
            {
                // i při chybě ukázat aspoň poslední známý stav + co čeká ve frontě
                if (TryRenderFromCache(userId)) SouhrnLabel.Text += "\n(nepodařilo se načíst ze serveru)";
                else SouhrnLabel.Text = "Lístek se nepodařilo načíst.";

                if (!Net.IsNetworkError(ex))
                    await Listek.AlertAsync(this, "Chyba", "Chyba při načítání dat: " + ex.Message);
            }
        }

        /// <summary>Server/cache + zápisy, které ještě nedorazily na server.</summary>
        private void RenderWithPending(string userId, DateTime day, Models.DailyDrinksModel? today, bool offline)
        {
            var pending = DrinkQueue.PendingFor(userId, day);
            shownDay = day;
            Render(Math.Max(0, (today?.Pivo ?? 0) + pending.Pivo),
                   Math.Max(0, (today?.Vino ?? 0) + pending.Vino),
                   Math.Max(0, (today?.Panaky ?? 0) + pending.Panaky),
                   pending.Count, offline);
        }

        private bool TryRenderFromCache(string userId)
        {
            try
            {
                var day = UserSession.CurrentPubDay;
                RenderWithPending(userId, day, LocalCache.LoadDaily(userId, day), offline: true);
                return true;
            }
            catch
            {
                return false;
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

            // kde sedím (poslední zápis dneška, z telefonu)
            if (AuthSession.Uid is string uid && PushService.LastPubName(uid, shownDay) is string pub)
                text += $"\n📍 {pub}";

            if (pendingCount > 0)
                text += $"\n({Cz.Plural(pendingCount, "zápis čeká", "zápisy čekají", "zápisů čeká")} na signál)";
            else if (offline)
                text += "\n(bez signálu)";

            SouhrnLabel.Text = text;
            UpdatePromile();

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

        private async void OnGamesClicked(object sender, EventArgs e) =>
            await Shell.Current.GoToAsync("//Hry");

        private async void OnPubsClicked(object sender, EventArgs e)
        {
            await Shell.Current.GoToAsync("//Hospody");
        }

        // Zpět na lístku: zavřít případnou hlášku, jinak normálně z appky ven
        protected override bool OnBackButtonPressed() =>
            Listek.DismissActive() || base.OnBackButtonPressed();
    }
}
