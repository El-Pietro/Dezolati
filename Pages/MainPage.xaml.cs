using Dezolati.Controls;
using Dezolati.Services;

namespace Dezolati.Pages
{
    public partial class MainPage : ContentPage
    {
        private readonly FirestoreService firestoreService = new();

        // co je právě na lístku (kvůli škrtání)
        private DateTime shownDay;
        private int shownPivo, shownVino, shownPanaky, shownMichacky;

        private readonly WetRingsDrawable rings = new();

        public MainPage()
        {
            InitializeComponent();
            RingsView.Drawable = rings;
            Pivo.SizeChanged += OnCarkySizeChanged;   // stačí jeden řádek – všechny mají stejnou šířku
            SetupEventFx();
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

            // event (Vánoce …): Events/aktualni max. 1× za 12 h; výzdoba hned podle uložené verze
            Events.Changed -= OnEventChanged;
            Events.Changed += OnEventChanged;
            ApplyEvent();
            _ = Events.EnsureAsync();
            _ = ShowNewBadgesAsync();

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
            Events.Changed -= OnEventChanged;
            fxTimer?.Stop();   // na pozadí nic nepadá (baterie)
        }

        // ---------------- event: výzdoba lístku (Services/EventScene.cs) ----------------

        private string? theme;
        private readonly EventFxDrawable backFx = new(), deco = new(), coasterFx = new(), snow = new();
        private readonly EventFxDrawable tagIcon = new(), tagCandles = new();
        private IDispatcherTimer? fxTimer;
        private readonly System.Diagnostics.Stopwatch fxClock = System.Diagnostics.Stopwatch.StartNew();
        private DateTime tagUpdatedAt;
        private Thickness? listekPadding, topBarMargin;

        private void SetupEventFx()
        {
            BackFxView.Drawable = backFx;
            DecoView.Drawable = deco;
            CoasterFxView.Drawable = coasterFx;
            SnowView.Drawable = snow;
            EventTagIcon.Drawable = tagIcon;
            EventTagCandles.Drawable = tagCandles;

            // světýlka nahoře + na Silvestra ohňostroj
            backFx.Scene = (r, t) =>
            {
                var n = EventScene.Lights(r.Width, theme, t);
                if (theme == "silvestr") n.AddRange(EventScene.Fireworks(r.Width, t));
                return n;
            };
            // čepice přes horní okraj lístku, roh: jmelí / sekt (+ serpentýna)
            deco.Scene = (r, t) =>
            {
                var lx = ListekBorder.X - DecoView.X;
                var ly = ListekBorder.Y - DecoView.Y;
                var lw = ListekBorder.Width;
                var n = new List<SceneNode>();
                if (lw <= 0) return n;
                if (theme == "silvestr")
                    n.Add(new SGroup(lx - 8, ly + 6, 0, null, EventScene.Streamer()));
                n.Add(new SGroup(lx, ly - EventScene.CapOverhang, 0, null, EventScene.Cap(lw)));
                if (theme == "vanoce")
                    n.Add(new SGroup(lx + lw + 6 - 78, ly - 4, 0, null, EventScene.Mistletoe()));
                else if (theme == "silvestr")
                    n.Add(new SGroup(lx + lw + 10 - EventScene.CornerW, ly - 20, 0, null, EventScene.Sekt(t)));
                return n;
            };
            // tácek (88 px uprostřed 108 px plochy)
            coasterFx.Scene = (r, t) => new()
            {
                theme == "silvestr"
                    ? new SGroup((r.Width - 88) / 2 - 10, (r.Height - 88) / 2 - 10, 0, null, EventScene.CoasterStreamer())
                    : new SGroup((r.Width - 88) / 2, (r.Height - 88) / 2 - 9, 0, null, EventScene.CoasterCap())
            };
            snow.Scene = (r, t) => Events.Snow ? EventScene.Particles(r.Width, r.Height, theme, t) : new();
            tagIcon.Scene = (r, t) => theme == null ? new() : EventScene.TagIcon(theme);
            tagCandles.Scene = (r, t) => EventScene.Tag(theme, DateTime.Now) is { Candles: >= 0 } tag ? EventScene.Candles(tag.Candles) : new();

            ListekBorder.SizeChanged += (_, _) => DecoView.Invalidate();
        }

        private void OnEventChanged(object? sender, EventArgs e) => Dispatcher.Dispatch(ApplyEvent);

        /// <summary>Zapne / vypne výzdobu podle aktuálního motivu (Events.Theme).</summary>
        private void ApplyEvent()
        {
            theme = Events.Theme;
            var on = theme != null;
            BackFxView.IsVisible = DecoView.IsVisible = CoasterFxView.IsVisible = on;
            SnowView.IsVisible = on && Events.Snow;

            // místo pro světýlka nad tlačítky a pro čepici nad nadpisem
            listekPadding ??= ListekBorder.Padding;
            topBarMargin ??= TopBar.Margin;
            ListekBorder.Padding = on ? new Thickness(listekPadding.Value.Left, listekPadding.Value.Top + 10, listekPadding.Value.Right, listekPadding.Value.Bottom) : listekPadding.Value;
            TopBar.Margin = on ? new Thickness(topBarMargin.Value.Left, Math.Max(topBarMargin.Value.Top, 30), topBarMargin.Value.Right, topBarMargin.Value.Bottom) : topBarMargin.Value;

            if (rings.ColorOverride != EventScene.RingColor(theme))
            {
                rings.ColorOverride = EventScene.RingColor(theme);
                RingsView.Invalidate();
            }
            UpdateEventTag();

            if (!on)
            {
                fxTimer?.Stop();
                return;
            }
            if (fxTimer == null)
            {
                fxTimer = Dispatcher.CreateTimer();
                fxTimer.Interval = TimeSpan.FromMilliseconds(50);   // 20 snímků za sekundu stačí
                fxTimer.Tick += OnFxTick;
            }
            fxTimer.Start();
            OnFxTick(null, EventArgs.Empty);
            CoasterFxView.Invalidate();
            DecoView.Invalidate();
        }

        private void OnFxTick(object? sender, EventArgs e)
        {
            var t = fxClock.Elapsed.TotalSeconds;
            backFx.Time = deco.Time = snow.Time = t;
            BackFxView.Invalidate();
            if (theme == "silvestr") DecoView.Invalidate();   // sekt bouchá
            if (SnowView.IsVisible) SnowView.Invalidate();
            if (DateTime.Now - tagUpdatedAt > TimeSpan.FromSeconds(20)) UpdateEventTag();   // odpočet do půlnoci
        }

        private void UpdateEventTag()
        {
            tagUpdatedAt = DateTime.Now;
            var tag = EventScene.Tag(theme, DateTime.Now);
            EventTag.IsVisible = tag != null;
            if (tag == null) return;
            EventTag.BackgroundColor = Color.FromArgb(tag.Dark ? "#1E1E2A" : "#FFFDF4");
            EventTag.Stroke = Color.FromArgb(tag.Dark ? "#D4A62A" : theme == "zima" ? "#5B87B5" : "#B3262E");
            EventTagTitle.Text = tag.Title + " ";   // mezera: Caveat na Androidu jinak ořízne konec nápisu
            EventTagTitle.TextColor = Color.FromArgb(tag.Dark ? "#F2C94C" : theme == "zima" ? "#3C5F86" : "#8E1B2A");
            EventTagDetail.Text = tag.Detail + " ";
            EventTagDetail.IsVisible = tag.Detail.Length > 0;
            EventTagDetail.TextColor = Color.FromArgb(tag.Dark ? "#F5F5DC" : "#6B5A3A");
            EventTagCandles.IsVisible = tag.Candles >= 0;
            EventTagIcon.Invalidate();
            EventTagCandles.Invalidate();
        }

        /// <summary>Ruční oprava místa: appka se spletla (doma vs. hospoda vedle) → vybrat, kde opravdu sedím.</summary>
        private async void OnPlaceTapped(object? sender, TappedEventArgs e)
        {
            if (shownDay.Date != UserSession.CurrentPubDay.Date) return;   // jen dnešek
            if (!PushService.DetectPub)
            {
                await Listek.AlertAsync(this, "Kde sedím", "Poznávání místa máš vypnuté v Nastavení. ");
                return;
            }
            var busy = Listek.ShowBusy(this, "Kde sedím", "Zjišťuju, kde jsi…");
            var loaded = await PlaceChoices.LoadAsync(shownDay);
            busy?.Dispose();
            if (loaded is not { } l)
            {
                await Listek.AlertAsync(this, "Kde sedím", "Bez polohy nevím, co je okolo. Povol appce polohu. ");
                return;
            }
            var pick = await Listek.ShowAsync(this, "Kde opravdu sedíš? ",
                "Platí pro další zápisy, dokud neodejdeš dál než 50 m. Pak se místo zase pozná samo. ",
                l.Choices.Select((c, i) => (c.Label, i == 0 ? PencilVariant.Primary : PencilVariant.Secondary)).ToArray());
            if (l.Choices.FirstOrDefault(c => c.Label == pick) is not { } choice) return;
            PlaceChoices.Choose(choice, l.Me, shownDay);
            await LoadData();
        }

        /// <summary>Okno „Nový odznak“ (po pushi hned, jinak nejvýš 1× za 10 min; jen když běží event).</summary>
        private async Task ShowNewBadgesAsync()
        {
            foreach (var b in await Badges.CheckNewAsync())
            {
                var r = await Listek.ShowAsync(this, $"Nový odznak: {b.Name}! ", b.Desc + " ",
                    ("Na zdraví!", PencilVariant.Primary), ("Ukázat odznaky", PencilVariant.Secondary));
                if (r == "Ukázat odznaky")
                {
                    OnStatsClicked(this, EventArgs.Empty);
                    return;
                }
            }
        }

        private async void OnEventTagTapped(object? sender, TappedEventArgs e) =>
            await Shell.Current.GoToAsync("//Trhy");   // detail eventu: trhy poblíž, „Tady jsou trhy!“, odznaky, mapa

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
                   pending.Count, offline,
                   Math.Max(0, (today?.Michacky ?? 0) + pending.Michacky));
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

        private void Render(int pivo, int vino, int panaky, int pendingCount = 0, bool offline = false, int michacky = 0)
        {
            shownPivo = pivo;
            shownVino = vino;
            shownPanaky = panaky;
            shownMichacky = michacky;
            lastPending = pendingCount;
            lastOffline = offline;

            var text = pivo + vino + panaky + michacky == 0
                ? "Zatím čistý lístek."
                : "Dnes: " + Cz.Souhrn(pivo, vino, panaky, michacky);

            // kde sedím (poslední zápis dneška, z telefonu)
            if (AuthSession.Uid is string uid && PushService.LastPubName(uid, shownDay) is string pub)
                text += $"\n📍 {(pub == "doma" ? "Doma" : pub)}";

            if (pendingCount > 0)
                text += $"\n({Cz.Plural(pendingCount, "zápis čeká", "zápisy čekají", "zápisů čeká")} na signál)";
            else if (offline)
                text += "\n(bez signálu)";

            SouhrnLabel.Text = text;
            UpdatePromile();

            // pevný seed = čárky vypadají pokaždé stejně, neskáčou při každém načtení;
            // „šejdr“ (natočení, posun) a mokrá kolečka podle dne – celý den stejné, zítra jiné
            DrawCarky(Pivo, pivo, shownDay, 0);
            DrawCarky(Vino, vino, shownDay, 1);
            DrawCarky(Panaky, panaky, shownDay, 2);
            DrawCarky(Michacky, michacky, shownDay, 3);
            Dispatcher.Dispatch(FitRows);   // až se řádky přeměří
            rings.Rings = Krouzky.For(shownDay, pivo, vino);
            RingsView.Invalidate();
        }

        // Čárky kreslí propiska (Services/Carky.cs) přes CarkyDrawable – stejně jako na webu.
        private void DrawCarky(GraphicsView view, int count, DateTime day, int kind)
        {
            var width = view.Width > 0 ? view.Width
                : DeviceDisplay.Current.MainDisplayInfo.Width / DeviceDisplay.Current.MainDisplayInfo.Density - 90;
            view.Drawable = new CarkyDrawable { Count = count, Day = day, Kind = kind };
            view.HeightRequest = CarkyDrawable.HeightFor(count, width);
            view.Invalidate();
        }

        // šířka lístku se změní (první měření, otočení) → výšky řádků přepočítat
        private void OnCarkySizeChanged(object? sender, EventArgs e)
        {
            if (sender is GraphicsView v && v.Width > 0 && Math.Abs(v.Width - lastCarkyWidth) > 1)
            {
                lastCarkyWidth = v.Width;
                Dispatcher.Dispatch(() => Render(shownPivo, shownVino, shownPanaky, lastPending, lastOffline, shownMichacky));
            }
        }

        // ---------------- mezery mezi řádky podle místa ----------------
        // Když je na lístku místo, řádky se rozestoupí (max. MaxRowGap), když ne, jsou těsně u sebe
        // a lístek se začne posouvat.
        private const double MaxRowGap = 40;
        private double rowGap;

        private void OnRowsSizeChanged(object? sender, EventArgs e) => FitRows();

        private void FitRows()
        {
            var avail = RowsScroll.Height;
            if (avail <= 0 || RowsScroll.Width <= 0) return;
            var measured = ((IView)RowsStack).Measure(RowsScroll.Width, double.PositiveInfinity).Height;
            var natural = measured - 4 * rowGap;                    // výška bez současných mezer
            var gap = Math.Clamp((avail - natural) / 4, 0, MaxRowGap);
            if (Math.Abs(gap - rowGap) < 1) return;
            rowGap = gap;
            VinoHeader.Margin = PanakyHeader.Margin = MichackyHeader.Margin = new Thickness(0, gap, 0, -3);
            RowsHint.Margin = new Thickness(0, 12 + gap, 0, 0);
        }

        private double lastCarkyWidth = -1;
        private int lastPending;
        private bool lastOffline;

        private async void OnPlusTapped(object sender, TappedEventArgs e)
        {
            // tácek se při ťuknutí trochu zmáčkne
            await PlusButton.ScaleTo(0.88, 70, Easing.CubicOut);
            await PlusButton.ScaleTo(1.0, 90, Easing.CubicIn);
            await Shell.Current.GoToAsync("//Zapis");
        }

        /// <summary>Ťuknutí na řádek lístku → připsat, nebo škrtnout jednu čárku (každý druh zvlášť).</summary>
        private async void OnRowTapped(object sender, TappedEventArgs e)
        {
            var userId = AuthSession.Uid;
            if (userId == null || e.Parameter is not string kind) return;

            var (count, name, one) = kind switch
            {
                "Pivo" => (shownPivo, "Pívo", "jedno pivo"),
                "Vino" => (shownVino, "Víno", "jedno víno"),
                "Michacky" => (shownMichacky, "Míchačky", "jednu míchačku"),
                _ => (shownPanaky, "Panáky", "jednoho panáka")
            };

            var add = $"Zapsat {one}";
            var strike = $"Odmazat {one}";
            var choice = count > 0
                ? await Listek.ShowAsync(this, $"{name}: {count}", "Co s tím?",
                    (add, PencilVariant.Primary), (strike, PencilVariant.Danger), ("Nic", PencilVariant.Secondary))
                : await Listek.ShowAsync(this, $"{name}: zatím nic", "Připsat čárku?",
                    (add, PencilVariant.Primary), ("Nic", PencilVariant.Secondary));

            int p = kind == "Pivo" ? 1 : 0, v = kind == "Vino" ? 1 : 0, s = kind == "Panaky" ? 1 : 0, m = kind == "Michacky" ? 1 : 0;
            try
            {
                if (choice == add)
                    await firestoreService.RecordDrink(userId, AuthSession.DisplayName, p, v, s, michacky: m);
                else if (choice == strike)
                    await firestoreService.RecordCorrection(userId, shownDay, p, v, s, m);
                else
                    return;
            }
            catch (Exception ex)
            {
                await Listek.AlertAsync(this, "Chyba", (choice == add ? "Zápis" : "Škrtnutí") + " se nepovedl: " + ex.Message);
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
