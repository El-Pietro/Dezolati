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
    private MemoryLayer? pubLayer;
    private MemoryLayer? meLayer;
    private PubInfo? selected;
    private bool loaded;
    private string addKind = "pub";

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
            // návrat z profilu hospody – mohl se změnit název, zavřít nebo smazat
            ShowPubs(await PubCatalog.GetAsync());
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
        map.Layers.Add(homeLayer);
        map.Layers.Add(pubLayer);
        map.Layers.Add(meLayer);
        ShowHome();

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
            await PubService.LoadOverlayAsync();   // opravené názvy, zavřené a přidané podniky
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
        foreach (var p in PubService.AllPubs(pubs))
        {
            var (x, y) = SphericalMercator.FromLonLat(p.Lon, p.Lat);
            var f = new PointFeature(x, y);
            f["id"] = p.Id;
            var closed = PubService.IsClosed(p);
            f["name"] = closed ? PubService.NameOf(p) + " (zavřeno)" : PubService.NameOf(p);
            f.Styles.Add(closed ? ClosedSymbol
                : PubService.IsCustom(p) ? CustomSymbol
                : p.Kind == "restaurant" ? RestaurantSymbol
                : PubSymbol);
            f.Styles.Add(PubLabel);
            features.Add(f);
        }
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
        CloseAdd();
        ShowHome();
    }

    private void OnMapInfo(object? sender, MapInfoEventArgs e)
    {
        if (e.MapInfo?.Feature?["id"] is not string id) return;
        if (AddCard.IsVisible) return;   // při přidávání se ťuká jen na mapu
        var pub = PubService.Find(id);
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
            var near = AddCard.IsVisible ? null : PubService.Nearest(loc.Latitude, loc.Longitude, 60);
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
        if (AddCard.IsVisible)
        {
            CloseAdd(); // Zpět při přidávání = zrušit
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
