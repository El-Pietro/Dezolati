using Dezolati.Controls;
using Dezolati.Models;
using Dezolati.Services;
using Mapsui;
using Mapsui.Layers;
using Mapsui.Projections;
using Mapsui.Styles;
using Mapsui.Tiling;
using Mapsui.UI.Maui;
using MBrush = Mapsui.Styles.Brush;
using MColor = Mapsui.Styles.Color;
using MFont = Mapsui.Styles.Font;

namespace Dezolati.Pages;

/// <summary>
/// Mapa hospod (Mapsui + dlaždice OpenStreetMap). Hospody jsou z katalogu v telefonu,
/// takže mapa samotná nestojí žádné čtení z Firestore.
/// </summary>
public partial class Hospody : ContentPage, IQueryAttributable
{
    // rozlišení mapy v metrech na pixel (≈ zoom 13 / 16 / 17)
    private const double ResolutionOverview = 19;
    private const double ResolutionStreet = 2.4;
    private const double LabelsBelowResolution = 3.5;

    private static readonly IStyle PubSymbol = new SymbolStyle
    {
        SymbolType = SymbolType.Ellipse,
        SymbolScale = 0.5,
        Fill = new MBrush(new MColor(194, 122, 18)),
        Outline = new Pen(new MColor(59, 35, 20), 2)
    };

    // restaurace tlumeněji, ať hospody vyčnívají
    private static readonly IStyle RestaurantSymbol = new SymbolStyle
    {
        SymbolType = SymbolType.Ellipse,
        SymbolScale = 0.4,
        Fill = new MBrush(new MColor(222, 196, 150)),
        Outline = new Pen(new MColor(107, 90, 58), 2)
    };

    // zavřené podniky šedě a menší
    private static readonly IStyle ClosedSymbol = new SymbolStyle
    {
        SymbolType = SymbolType.Ellipse,
        SymbolScale = 0.35,
        Fill = new MBrush(new MColor(170, 170, 170)),
        Outline = new Pen(new MColor(110, 110, 110), 2)
    };

    // podniky přidané uživateli: hospodská barva, ale hranatě
    private static readonly IStyle CustomSymbol = new SymbolStyle
    {
        SymbolType = SymbolType.Rectangle,
        SymbolScale = 0.45,
        Fill = new MBrush(new MColor(194, 122, 18)),
        Outline = new Pen(new MColor(59, 35, 20), 2)
    };

    private static readonly IStyle PubLabel = new LabelStyle
    {
        LabelColumn = "name",
        MaxVisible = LabelsBelowResolution,           // popisky až při přiblížení
        ForeColor = new MColor(30, 30, 42),
        BackColor = new MBrush(new MColor(245, 245, 220, 220)),
        Font = new MFont { Size = 12 },
        Offset = new Offset(0, -16)
    };

    private static readonly IStyle MeSymbol = new SymbolStyle
    {
        SymbolType = SymbolType.Ellipse,
        SymbolScale = 0.6,
        Fill = new MBrush(new MColor(40, 110, 220)),
        Outline = new Pen(MColor.White, 3)
    };

    // oblíbené: zlaté a větší
    private static readonly IStyle FavoriteSymbol = new SymbolStyle
    {
        SymbolType = SymbolType.Ellipse,
        SymbolScale = 0.65,
        Fill = new MBrush(new MColor(232, 176, 32)),
        Outline = new Pen(new MColor(120, 70, 10), 3)
    };

    // kámoši, kteří tam teď pijí: popisek nad hospodou
    private static readonly IStyle FriendsLabel = new LabelStyle
    {
        LabelColumn = "friends",
        ForeColor = MColor.White,
        BackColor = new MBrush(new MColor(70, 110, 170, 235)),
        Font = new MFont { Size = 13 },
        Offset = new Offset(0, -34)
    };

    private static readonly IStyle FriendSymbol = new SymbolStyle
    {
        SymbolType = SymbolType.Ellipse,
        SymbolScale = 0.45,
        Fill = new MBrush(new MColor(70, 110, 170)),
        Outline = new Pen(MColor.White, 3)
    };

    // záloha bez katalogu ČR: od jakého přiblížení (m/px) se dotahují čtverce z Overpassu
    private const double DownloadBelowResolution = 60;
    private const int MaxCellsAtOnce = 24;

    // víc bodů ve výřezu → shluky s počtem (tisíce bodů mapu sekají); velikost shluku v px
    private const int ClusterAbove = 250;
    private const double ClusterCellPx = 72;

    private static readonly IStyle ClusterSymbol = new SymbolStyle
    {
        SymbolType = SymbolType.Ellipse,
        SymbolScale = 1.0,
        Fill = new MBrush(new MColor(194, 122, 18, 230)),
        Outline = new Pen(new MColor(59, 35, 20), 2)
    };

    private static readonly IStyle ClusterLabel = new LabelStyle
    {
        LabelColumn = "cluster",
        ForeColor = MColor.White,
        BackColor = new MBrush(MColor.Transparent),
        Halo = null,
        Font = new MFont { Size = 13, Bold = true },
        Offset = new Offset(0, 0)
    };

    // domov (jen v telefonu): modrý trojúhelník
    private static readonly IStyle HomeSymbol = new SymbolStyle
    {
        SymbolType = SymbolType.Triangle,
        SymbolScale = 0.6,
        Fill = new MBrush(new MColor(70, 110, 170)),
        Outline = new Pen(MColor.White, 2)
    };

    private MapControl? mapControl;
    private MemoryLayer? homeLayer;
    private MemoryLayer? friendsLayer;
    private CancellationTokenSource? viewportCts;

    // filtr mapy (pamatuje si volbu)
    private bool onlyFavorites = Preferences.Get("map_only_fav", false);
    private bool showClosed = Preferences.Get("map_show_closed", true);
    private HashSet<string> kinds = new((Preferences.Get("map_kinds", "pub,bar,restaurant,biergarten"))
        .Split(',', StringSplitOptions.RemoveEmptyEntries));
    private MemoryLayer? pubLayer;
    private MemoryLayer? meLayer;
    private PubInfo? selected;
    private bool loaded;
    private string addKind = "pub";

    private bool homeRequested;   // přišlo se z Nastavení → „Nastavit domov“

    public Hospody()
    {
        InitializeComponent();
    }

    public void ApplyQueryAttributes(IDictionary<string, object> query)
    {
        homeRequested = query.TryGetValue("mode", out var m) && m as string == "home";
    }

    protected override async void OnAppearing()
    {
        base.OnAppearing();
        if (mapControl == null) CreateMap();
        if (homeRequested)
        {
            homeRequested = false;
            OpenHomeMode();
        }
        if (!loaded) await LoadPubsAsync();
        else
        {
            // návrat z profilu hospody – mohl se změnit název, zavřít nebo smazat
            ShowPubs(await PubCatalog.GetAsync());
            _ = ShowFriendsAsync();
            if (selected != null && PubCard.IsVisible)
            {
                if (PubService.Find(selected.Id) == null) HideCard();
                else ShowCard(selected, null);
            }
        }
    }

    private void CreateMap()
    {
        var map = new Mapsui.Map();
        map.Layers.Add(OpenStreetMap.CreateTileLayer("Dezolati/2.2 (cz.elpietro.dezolati)"));

        pubLayer = new MemoryLayer("Hospody") { Style = null, IsMapInfoLayer = true };
        meLayer = new MemoryLayer("Já") { Style = null };
        homeLayer = new MemoryLayer("Domov") { Style = null };
        friendsLayer = new MemoryLayer("Kámoši") { Style = null };
        map.Layers.Add(homeLayer);
        map.Layers.Add(pubLayer);
        map.Layers.Add(friendsLayer);
        map.Layers.Add(meLayer);
        ShowHome();

        // posun / přiblížení mapy → dotáhnout hospody ve výřezu (s prodlevou, ať se nestahuje při každém pohybu)
        map.Navigator.ViewportChanged += (_, _) => ScheduleViewportCheck();
        PubCatalog.Changed += () => MainThread.BeginInvokeOnMainThread(async () => await EnsureViewportAsync(CancellationToken.None, showErrors: false));
        Favorites.Changed += () => MainThread.BeginInvokeOnMainThread(async () => ShowPubs(await PubCatalog.GetAsync()));

        // výchozí pohled: Brno
        var (x, y) = SphericalMercator.FromLonLat(16.6068, 49.1951);
        map.Home = n => n.CenterOnAndZoomTo(new MPoint(x, y), ResolutionOverview);

        mapControl = new MapControl
        {
            Map = map,
            // sever drží nahoře: otáčet se začne až po 35° kroucení, pod 12° se vrátí zpátky
            UnSnapRotationDegrees = 35,
            ReSnapRotationDegrees = 12
        };
        mapControl.Info += OnMapInfo;
        MapHost.Content = mapControl;
    }

    private async Task LoadPubsAsync(bool force = false)
    {
        LoadingBox.IsVisible = true;
        LoadingSpinner.IsRunning = true;
        RetryButton.IsVisible = false;
        LoadingLabel.Text = "Načítám hospody…";

        try
        {
            var pubs = await PubCatalog.GetAsync();   // co už je v telefonu
            await PubService.LoadOverlayAsync();       // opravené názvy, zavřené a přidané podniky
            ShowPubs(pubs);
            loaded = true;
            LoadingBox.IsVisible = false;

            await CenterOnMeAsync(silent: true);
            await EnsureViewportAsync(CancellationToken.None, showErrors: !PubCatalog.HasAny);
            _ = ShowFriendsAsync();
        }
        catch (Exception ex)
        {
            LoadingBox.IsVisible = true;
            LoadingSpinner.IsRunning = false;
            LoadingLabel.Text = ex.Message;
            RetryButton.IsVisible = true;
        }
    }

    // ---------------- dotahování hospod podle výřezu ----------------

    private void ScheduleViewportCheck()
    {
        if (!loaded) return;
        viewportCts?.Cancel();
        var cts = viewportCts = new CancellationTokenSource();
        _ = Task.Run(async () =>
        {
            try
            {
                await Task.Delay(400, cts.Token);
                await MainThread.InvokeOnMainThreadAsync(() => EnsureViewportAsync(cts.Token, showErrors: false));
            }
            catch (OperationCanceledException) { }
        });
    }

    /// <summary>Výřez mapy v zeměpisných souřadnicích (jih, západ, sever, východ).</summary>
    private (double S, double W, double N, double E, double Resolution)? ViewportBox()
    {
        if (mapControl == null) return null;
        var vp = mapControl.Map.Navigator.Viewport;
        if (vp.Width <= 0 || vp.Height <= 0) return null;
        var halfW = vp.Width * vp.Resolution / 2;
        var halfH = vp.Height * vp.Resolution / 2;
        var (w, s) = SphericalMercator.ToLonLat(vp.CenterX - halfW, vp.CenterY - halfH);
        var (e, n) = SphericalMercator.ToLonLat(vp.CenterX + halfW, vp.CenterY + halfH);
        return (s, w, n, e, vp.Resolution);
    }

    private bool downloading;

    private async Task EnsureViewportAsync(CancellationToken ct, bool showErrors)
    {
        if (ViewportBox() is not { } box) return;
        ShowPubs(await PubCatalog.GetAsync());   // body / shluky pro aktuální výřez

        // katalog celé ČR v telefonu → nic se nestahuje
        if (PubCatalog.HasNational)
        {
            CellStatusLabel.IsVisible = false;
            return;
        }
        if (PubCatalog.NationalLoading || downloading)
        {
            CellStatusLabel.Text = "Stahuji hospody…";
            CellStatusLabel.IsVisible = true;
            return;
        }

        // záloha: čtverce z Overpassu podle výřezu
        if (box.Resolution > DownloadBelowResolution)
        {
            CellStatusLabel.Text = "Přibliž mapu, ať načtu hospody";
            CellStatusLabel.IsVisible = true;
            return;
        }
        var cells = PubCatalog.CellsIn(box.S, box.W, box.N, box.E);
        var missing = cells.Where(c => !PubCatalog.HasCell(c)).ToList();
        if (missing.Count == 0)
        {
            CellStatusLabel.IsVisible = false;
            return;
        }
        if (cells.Count > MaxCellsAtOnce)
        {
            CellStatusLabel.Text = "Přibliž mapu, ať načtu hospody";
            CellStatusLabel.IsVisible = true;
            return;
        }

        CellStatusLabel.Text = "Stahuji hospody v okolí…";
        CellStatusLabel.IsVisible = true;
        downloading = true;
        try
        {
            // posun mapy rozběhnuté stahování neruší (jinak by se odpověď serveru zahodila a čekalo znovu)
            await PubCatalog.EnsureCellsAsync(missing, CancellationToken.None);
            CellStatusLabel.IsVisible = !PubCatalog.HasNational && missing.Any(c => !PubCatalog.HasCell(c));
            if (CellStatusLabel.IsVisible)
                CellStatusLabel.Text = Net.IsOnline ? "Některé hospody se nepodařilo načíst" : "Bez signálu – hospody načtu později";
        }
        catch (Exception ex)
        {
            CellStatusLabel.Text = Net.IsOnline ? "Hospody se nepodařilo načíst" : "Bez signálu – hospody načtu později";
            if (showErrors) await Listek.AlertAsync(this, "Hospody", ex.Message);
        }
        finally
        {
            downloading = false;
        }
        // mezitím se mapa posunula → dotáhnout i nový výřez
        if (ct.IsCancellationRequested) ScheduleViewportCheck();
    }

    // ---------------- kámoši, kteří teď pijí ----------------

    private async Task ShowFriendsAsync()
    {
        if (friendsLayer == null) return;
        try
        {
            var drinking = await FriendsNow.GetAsync();
            var features = new List<IFeature>();
            foreach (var g in drinking.Where(f => f.HasPubLocation)
                         .GroupBy(f => (Math.Round(f.PubLat!.Value, 4), Math.Round(f.PubLon!.Value, 4))))
            {
                var (x, y) = SphericalMercator.FromLonLat(g.Key.Item2, g.Key.Item1);
                var f = new PointFeature(x, y);
                f["friends"] = string.Join(", ", g.Select(d => $"{d.Emoji} {d.Nick}"));
                f.Styles.Add(FriendSymbol);
                f.Styles.Add(FriendsLabel);
                features.Add(f);
            }
            friendsLayer.Features = features;
            friendsLayer.DataHasChanged();
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"Kámoši na mapě: {ex.Message}");
        }
    }

    private bool PassesFilter(PubInfo p, bool favorite)
    {
        if (onlyFavorites && !favorite) return false;
        if (!showClosed && PubService.IsClosed(p)) return false;
        return favorite || kinds.Contains(PubService.Kinds.Contains(p.Kind) ? p.Kind : "pub");
    }

    private IFeature PubFeature(PubInfo p, bool favorite)
    {
        var (x, y) = SphericalMercator.FromLonLat(p.Lon, p.Lat);
        var f = new PointFeature(x, y);
        f["id"] = p.Id;
        var closed = PubService.IsClosed(p);
        f["name"] = (favorite ? "★ " : "") + (closed ? PubService.NameOf(p) + " (zavřeno)" : PubService.NameOf(p));
        f.Styles.Add(closed ? ClosedSymbol
            : favorite ? FavoriteSymbol
            : PubService.IsCustom(p) ? CustomSymbol
            : p.Kind == "restaurant" ? RestaurantSymbol
            : PubSymbol);
        f.Styles.Add(PubLabel);
        return f;
    }

    /// <summary>
    /// Hospody ve výřezu (+ půl výřezu okolo na posun). Když je jich moc, sloučí se do shluků s počtem;
    /// oblíbené jsou vždy samostatně (i mimo stažené).
    /// </summary>
    private void ShowPubs(IReadOnlyList<PubInfo> pubs)
    {
        if (pubLayer == null) return;

        var shown = new HashSet<string>();
        var favorites = new List<IFeature>();
        foreach (var (id, fav) in Favorites.All)
        {
            var p = PubService.Find(id) ?? Favorites.ToPub(id, fav);
            if (shown.Add(p.Id) && PassesFilter(p, true)) favorites.Add(PubFeature(p, true));
        }

        var features = new List<IFeature>();
        if (ViewportBox() is { } box)
        {
            var mLat = (box.N - box.S) / 2;
            var mLon = (box.E - box.W) / 2;
            var visible = new List<PubInfo>();
            foreach (var p in PubService.AllPubs(pubs))
            {
                if (p.Lat < box.S - mLat || p.Lat > box.N + mLat || p.Lon < box.W - mLon || p.Lon > box.E + mLon) continue;
                if (!shown.Add(p.Id) || !PassesFilter(p, false)) continue;
                visible.Add(p);
            }

            if (visible.Count <= ClusterAbove)
            {
                foreach (var p in visible) features.Add(PubFeature(p, false));
            }
            else
            {
                var size = ClusterCellPx * box.Resolution;
                var buckets = new Dictionary<(long, long), List<(PubInfo P, double X, double Y)>>();
                foreach (var p in visible)
                {
                    var (x, y) = SphericalMercator.FromLonLat(p.Lon, p.Lat);
                    var key = ((long)Math.Floor(x / size), (long)Math.Floor(y / size));
                    if (!buckets.TryGetValue(key, out var list)) buckets[key] = list = new();
                    list.Add((p, x, y));
                }
                foreach (var b in buckets.Values)
                {
                    if (b.Count == 1)
                    {
                        features.Add(PubFeature(b[0].P, false));
                        continue;
                    }
                    var f = new PointFeature(b.Average(i => i.X), b.Average(i => i.Y));
                    f["cluster"] = b.Count.ToString();
                    f.Styles.Add(ClusterSymbol);
                    f.Styles.Add(ClusterLabel);
                    features.Add(f);
                }
            }
        }
        features.AddRange(favorites);   // oblíbené navrch

        pubLayer.Features = features;
        pubLayer.DataHasChanged();
    }

    private void ShowHome()
    {
        if (homeLayer == null) return;
        var features = new List<IFeature>();
        if (HomePlace.Location is { } h)
        {
            var (x, y) = SphericalMercator.FromLonLat(h.Lon, h.Lat);
            var f = new PointFeature(x, y);
            f.Styles.Add(HomeSymbol);
            features.Add(f);
        }
        homeLayer.Features = features;
        homeLayer.DataHasChanged();
    }

    private void OpenHomeMode()
    {
        HideCard();
        FilterCard.IsVisible = false;
        if (AddCard.IsVisible) CloseAdd();
        Crosshair.IsVisible = true;
        HomeCard.IsVisible = true;
        AddButton.IsVisible = false;

        // začít na stávajícím domově, ať se dá jen doladit
        if (HomePlace.Location is { } h && mapControl != null)
        {
            var (x, y) = SphericalMercator.FromLonLat(h.Lon, h.Lat);
            mapControl.Map.Navigator.CenterOnAndZoomTo(new MPoint(x, y), ResolutionStreet);
        }
    }

    private void CloseHomeMode()
    {
        HomeCard.IsVisible = false;
        Crosshair.IsVisible = false;
        AddButton.IsVisible = true;
    }

    private async void OnHomeCancelClicked(object sender, EventArgs e)
    {
        CloseHomeMode();
        await Shell.Current.GoToAsync("//Settings");
    }

    private async void OnHomeHereClicked(object sender, EventArgs e)
    {
        if (mapControl == null) return;
        var vp = mapControl.Map.Navigator.Viewport;
        var (lon, lat) = SphericalMercator.ToLonLat(vp.CenterX, vp.CenterY);

        var replace = HomePlace.IsSet;
        if (!await Listek.ConfirmAsync(this, replace ? "Přestěhoval ses?" : "Tady bydlíš?",
                "Pití do 80 m odsud se bude počítat jako „doma“. Poloha domova zůstane jen v tomhle telefonu, " +
                "kámoši uvidí nanejvýš to, že jsi pil doma.",
                replace ? "Nový domov" : "Tady bydlím"))
            return;

        HomePlace.Set(Math.Round(lat, 6), Math.Round(lon, 6));
        CloseHomeMode();
        ShowHome();
        await Shell.Current.GoToAsync("//Settings");
    }

    private void OnMapInfo(object? sender, MapInfoEventArgs e)
    {
        // shluk → přiblížit na něj
        if (e.MapInfo?.Feature is PointFeature { } cluster && cluster["cluster"] != null && mapControl != null)
        {
            var nav = mapControl.Map.Navigator;
            MainThread.BeginInvokeOnMainThread(() =>
                nav.CenterOnAndZoomTo(cluster.Point, nav.Viewport.Resolution / 3, 400));
            return;
        }
        if (e.MapInfo?.Feature?["id"] is not string id) return;
        if (AddCard.IsVisible || HomeCard.IsVisible) return;   // při přidávání / domově se ťuká jen na mapu
        var pub = PubService.Find(id)
                  ?? (Favorites.All.TryGetValue(id, out var fav) ? Favorites.ToPub(id, fav) : null);
        if (pub == null) return;
        MainThread.BeginInvokeOnMainThread(() => ShowCard(pub, null));
    }

    private void ShowCard(PubInfo pub, double? distanceMeters)
    {
        selected = pub;
        CardName.Text = PubService.NameOf(pub);
        var info = PubService.IsClosed(pub) ? "zavřeno · " + pub.KindText : pub.KindText;
        if (!string.IsNullOrEmpty(pub.Address)) info += " · " + pub.Address;
        if (distanceMeters is double d) info += $" · {Math.Round(d)} m";
        CardInfo.Text = info;
        CardStar.Text = Favorites.Is(pub.Id) ? "★" : "☆";
        FilterCard.IsVisible = false;
        PubCard.IsVisible = true;
    }

    private async void OnStarTapped(object? sender, TappedEventArgs e)
    {
        if (selected == null) return;
        try
        {
            var on = await Favorites.ToggleAsync(selected);
            CardStar.Text = on ? "★" : "☆";
        }
        catch (Exception ex)
        {
            await Listek.AlertAsync(this, "Chyba", Net.IsNetworkError(ex) ? "Bez signálu to nejde uložit." : ex.Message);
        }
    }

    private async void OnInviteClicked(object sender, EventArgs e)
    {
        if (selected == null) return;
        var name = PubService.NameOf(selected);
        if (!await Listek.ConfirmAsync(this, "Pozvat kámoše?",
                $"Kámošům do {Invites.RadiusKm:0} km přijde „zve tě na drink do {name}“.", "Pozvat"))
            return;
        try
        {
            InviteButton.IsEnabled = false;
            await Invites.SendAsync(selected);
            await Listek.AlertAsync(this, "Posláno", "Pozvánka letí. Teď už jen čekat, kdo dorazí.");
        }
        catch (Exception ex)
        {
            await Listek.AlertAsync(this, "Nepovedlo se", Net.IsNetworkError(ex) ? "Bez signálu pozvánka neodejde." : ex.Message);
        }
        finally
        {
            InviteButton.IsEnabled = true;
        }
    }

    // ---------------- filtr ----------------

    private void OnFilterClicked(object sender, EventArgs e)
    {
        if (FilterCard.IsVisible) { FilterCard.IsVisible = false; return; }
        HideCard();
        if (AddCard.IsVisible) CloseAdd();
        UpdateFilterChips();
        FilterCard.IsVisible = true;
    }

    private void UpdateFilterChips()
    {
        void Chip(PencilButton b, bool on)
        {
            b.Variant = on ? PencilVariant.Primary : PencilVariant.Secondary;
            b.Opacity = on ? 1.0 : 0.45;
        }
        Chip(ShowAll, !onlyFavorites);
        Chip(ShowFav, onlyFavorites);
        Chip(FPub, kinds.Contains("pub"));
        Chip(FBar, kinds.Contains("bar"));
        Chip(FRestaurant, kinds.Contains("restaurant"));
        Chip(FGarden, kinds.Contains("biergarten"));
        Chip(FClosed, showClosed);
        FilterButton.Text = onlyFavorites || !showClosed || kinds.Count < 4 ? "Filtr •" : "Filtr";
    }

    private async void ApplyFilter()
    {
        Preferences.Set("map_only_fav", onlyFavorites);
        Preferences.Set("map_show_closed", showClosed);
        Preferences.Set("map_kinds", string.Join(",", kinds));
        UpdateFilterChips();
        ShowPubs(await PubCatalog.GetAsync());
    }

    private void OnShowModeClicked(object sender, EventArgs e)
    {
        onlyFavorites = ((PencilButton)sender).CommandParameter as string == "fav";
        ApplyFilter();
    }

    private void OnFilterKindClicked(object sender, EventArgs e)
    {
        if (((PencilButton)sender).CommandParameter is not string kind) return;
        if (!kinds.Remove(kind)) kinds.Add(kind);
        ApplyFilter();
    }

    private void OnFilterClosedClicked(object sender, EventArgs e)
    {
        showClosed = !showClosed;
        ApplyFilter();
    }

    private void OnFilterDoneClicked(object sender, EventArgs e) => FilterCard.IsVisible = false;

    /// <summary>Najde polohu, vycentruje mapu a nabídne nejbližší hospodu.</summary>
    private async Task CenterOnMeAsync(bool silent)
    {
        try
        {
            var status = await Permissions.CheckStatusAsync<Permissions.LocationWhenInUse>();
            if (status != PermissionStatus.Granted)
            {
                if (silent && status == PermissionStatus.Denied && DeviceInfo.Platform == DevicePlatform.iOS) return;
                status = await Permissions.RequestAsync<Permissions.LocationWhenInUse>();
            }
            if (status != PermissionStatus.Granted)
            {
                if (!silent)
                    await Listek.AlertAsync(this, "Bez polohy", "Bez povolení polohy nevím, ve které hospodě sedíš.");
                return;
            }

            var loc = await Geolocation.GetLastKnownLocationAsync();
            if (loc == null || DateTimeOffset.Now - loc.Timestamp > TimeSpan.FromMinutes(5))
                loc = await Geolocation.GetLocationAsync(
                    new GeolocationRequest(GeolocationAccuracy.Medium, TimeSpan.FromSeconds(10))) ?? loc;
            if (loc == null)
            {
                if (!silent) await Listek.AlertAsync(this, "Nevím", "Polohu se nepodařilo zjistit.");
                return;
            }

            LastLocation.Remember(loc.Latitude, loc.Longitude);
            var (x, y) = SphericalMercator.FromLonLat(loc.Longitude, loc.Latitude);
            if (meLayer != null)
            {
                var me = new PointFeature(x, y);
                me.Styles.Add(MeSymbol);
                meLayer.Features = new List<IFeature> { me };
                meLayer.DataHasChanged();
            }
            mapControl?.Map.Navigator.CenterOnAndZoomTo(new MPoint(x, y), ResolutionStreet);

            // sedíš v hospodě? (do 60 m)
            var near = AddCard.IsVisible || HomeCard.IsVisible ? null : PubService.Nearest(loc.Latitude, loc.Longitude, 60);
            if (near != null)
                ShowCard(near, PubCatalog.DistanceMeters(loc.Latitude, loc.Longitude, near.Lat, near.Lon));
        }
        catch (Exception ex)
        {
            if (!silent) await Listek.AlertAsync(this, "Chyba", $"Poloha: {ex.Message}");
        }
    }

    private void HideCard()
    {
        PubCard.IsVisible = false;
        selected = null;
    }

    // ---------------- přidání podniku ----------------

    private void OnAddClicked(object sender, EventArgs e)
    {
        if (!loaded) return;
        HideCard();
        FilterCard.IsVisible = false;
        AddNameEntry.Text = "";
        SetKind("pub");
        Crosshair.IsVisible = true;
        AddCard.IsVisible = true;
        AddButton.IsVisible = false;
    }

    private void CloseAdd()
    {
        AddCard.IsVisible = false;
        Crosshair.IsVisible = false;
        AddButton.IsVisible = true;
        AddNameEntry.Unfocus();
    }

    private void OnAddCancelClicked(object sender, EventArgs e) => CloseAdd();

    private void OnKindClicked(object sender, EventArgs e) =>
        SetKind(((PencilButton)sender).CommandParameter as string ?? "pub");

    private void SetKind(string kind)
    {
        addKind = kind;
        foreach (var chip in new[] { KindPub, KindBar, KindRestaurant, KindGarden })
        {
            var on = chip.CommandParameter as string == kind;
            chip.Variant = on ? PencilVariant.Primary : PencilVariant.Secondary;
            chip.Opacity = on ? 1.0 : 0.45;
        }
    }

    private async void OnAddSaveClicked(object sender, EventArgs e)
    {
        if (mapControl == null) return;
        var name = AddNameEntry.Text?.Trim();
        if (string.IsNullOrEmpty(name))
        {
            await Listek.AlertAsync(this, "Jak se jmenuje?", "Napiš název podniku.");
            return;
        }

        var vp = mapControl.Map.Navigator.Viewport;
        var (lon, lat) = SphericalMercator.ToLonLat(vp.CenterX, vp.CenterY);

        // nepřidávat podruhé, co už v mapě je
        var near = PubService.Nearest(lat, lon, 25);
        if (near != null && !await Listek.ConfirmAsync(this, "Už tu něco je",
                $"Do 25 m je „{PubService.NameOf(near)}“. Opravdu přidat nový podnik?", "Přidat"))
            return;

        try
        {
            AddSaveButton.IsEnabled = false;
            var pub = await PubService.CreateAsync(name, addKind, lat, lon);
            CloseAdd();
            ShowPubs(await PubCatalog.GetAsync());
            ShowCard(pub, null);
        }
        catch (Exception ex)
        {
            await Listek.AlertAsync(this, "Chyba", $"Podnik se nepodařilo přidat: {ex.Message}");
        }
        finally
        {
            AddSaveButton.IsEnabled = true;
        }
    }

    private async void OnWhereAmIClicked(object sender, EventArgs e) => await CenterOnMeAsync(silent: false);

    private async void OnRetryClicked(object sender, EventArgs e) => await LoadPubsAsync(force: true);

    private async void OnOpenClicked(object sender, EventArgs e)
    {
        if (selected == null) return;
        await Shell.Current.GoToAsync("//Hospoda", new Dictionary<string, object>
        {
            ["pubId"] = selected.Id
        });
    }

    private void OnZpetClicked(object sender, EventArgs e) => OnBackButtonPressed();

    protected override bool OnBackButtonPressed()
    {
        if (Listek.DismissActive()) return true;
        if (FilterCard.IsVisible)
        {
            FilterCard.IsVisible = false;
            return true;
        }
        if (AddCard.IsVisible)
        {
            CloseAdd(); // Zpět při přidávání = zrušit
            return true;
        }
        if (HomeCard.IsVisible)
        {
            CloseHomeMode(); // Zpět při nastavování domova = zpátky do Nastavení
            Dispatcher.Dispatch(async () => await Shell.Current.GoToAsync("//Settings"));
            return true;
        }
        if (PubCard.IsVisible)
        {
            HideCard(); // nejdřív zavřít kartičku
            return true;
        }
        Dispatcher.Dispatch(async () => await Shell.Current.GoToAsync("//MainPage"));
        return true;
    }
}
