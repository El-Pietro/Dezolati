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
public partial class Hospody : ContentPage
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

    private MapControl? mapControl;
    private MemoryLayer? pubLayer;
    private MemoryLayer? meLayer;
    private PubInfo? selected;
    private bool loaded;

    public Hospody()
    {
        InitializeComponent();
    }

    protected override async void OnAppearing()
    {
        base.OnAppearing();
        if (mapControl == null) CreateMap();
        if (!loaded) await LoadPubsAsync();
        else
        {
            // návrat z profilu hospody – mohl se změnit název
            ShowPubs(await PubCatalog.GetAsync());
            if (selected != null && PubCard.IsVisible) CardName.Text = PubService.NameOf(selected);
        }
    }

    private void CreateMap()
    {
        var map = new Mapsui.Map();
        map.Layers.Add(OpenStreetMap.CreateTileLayer("Dezolati/2.2 (cz.elpietro.dezolati)"));

        pubLayer = new MemoryLayer("Hospody") { Style = null, IsMapInfoLayer = true };
        meLayer = new MemoryLayer("Já") { Style = null };
        map.Layers.Add(pubLayer);
        map.Layers.Add(meLayer);

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
        LoadingLabel.Text = PubCatalog.DownloadedAt == null || force
            ? "Sháním seznam hospod v kraji…\n(poprvé to chvíli trvá)"
            : "Načítám hospody…";

        try
        {
            var pubs = await PubCatalog.GetAsync(force);
            await PubService.LoadRenamedAsync();   // opravené názvy (čtení jen za přejmenované)
            ShowPubs(pubs);
            loaded = true;
            LoadingBox.IsVisible = false;

            await CenterOnMeAsync(silent: true);
        }
        catch (Exception ex)
        {
            LoadingSpinner.IsRunning = false;
            LoadingLabel.Text = ex.Message;
            RetryButton.IsVisible = true;
        }
    }

    private void ShowPubs(IReadOnlyList<PubInfo> pubs)
    {
        if (pubLayer == null) return;

        var features = new List<IFeature>(pubs.Count);
        foreach (var p in pubs)
        {
            var (x, y) = SphericalMercator.FromLonLat(p.Lon, p.Lat);
            var f = new PointFeature(x, y);
            f["id"] = p.Id;
            f["name"] = PubService.NameOf(p);
            f.Styles.Add(p.Kind == "restaurant" ? RestaurantSymbol : PubSymbol);
            f.Styles.Add(PubLabel);
            features.Add(f);
        }
        pubLayer.Features = features;
        pubLayer.DataHasChanged();
    }

    private void OnMapInfo(object? sender, MapInfoEventArgs e)
    {
        if (e.MapInfo?.Feature?["id"] is not string id) return;
        var pub = PubCatalog.Find(id);
        if (pub == null) return;
        MainThread.BeginInvokeOnMainThread(() => ShowCard(pub, null));
    }

    private void ShowCard(PubInfo pub, double? distanceMeters)
    {
        selected = pub;
        CardName.Text = PubService.NameOf(pub);
        var info = pub.KindText;
        if (!string.IsNullOrEmpty(pub.Address)) info += " · " + pub.Address;
        if (distanceMeters is double d) info += $" · {Math.Round(d)} m";
        CardInfo.Text = info;
        PubCard.IsVisible = true;
    }

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
            var near = PubCatalog.Nearest(loc.Latitude, loc.Longitude, 60);
            if (near != null)
                ShowCard(near, PubCatalog.DistanceMeters(loc.Latitude, loc.Longitude, near.Lat, near.Lon));
        }
        catch (Exception ex)
        {
            if (!silent) await Listek.AlertAsync(this, "Chyba", $"Poloha: {ex.Message}");
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
        if (PubCard.IsVisible)
        {
            PubCard.IsVisible = false; // nejdřív zavřít kartičku
            selected = null;
            return true;
        }
        Dispatcher.Dispatch(async () => await Shell.Current.GoToAsync("//MainPage"));
        return true;
    }
}
