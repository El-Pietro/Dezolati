using Dezolati.Controls;
using Dezolati.Models;
using Dezolati.Services;

namespace Dezolati.Pages;

public partial class Settings : ContentPage
{
    private readonly FirestoreService firestoreService = new();

    private string? gender;
    private string? drinkerType;
    private bool notifyFriends = true;
    private int shareLevel = 2;   // 0 nehlásit, 1 že piju, 2 i kde
    private bool detectPub = true;

    public Settings()
    {
        InitializeComponent();
    }

    protected override async void OnAppearing()
    {
        base.OnAppearing();
        VersionLabel.Text = $"Dezoláti {AppUpdater.CurrentVersionText}";
        UsageButton.IsVisible = OsmButton.IsVisible = UsageService.IsAdmin;
        LoadLocalSettings();
        await LoadUserData();
    }

    private async Task LoadUserData()
    {
        var userId = AuthSession.Uid;
        if (userId == null)
        {
            await Shell.Current.GoToAsync("//SignIn");
            return;
        }

        try
        {
            await UserSession.EnsureReadyAsync(); // z paměti
            var user = UserSession.Profile;
            if (user != null)
            {
                NameEntry.Text = user.Name;
                NicknameEntry.Text = user.Nickname;
                gender = user.Gender;
                drinkerType = user.DrinkerType;
                notifyFriends = user.NotifyFriends;
                shareLevel = user.ShareLevel;
                PushService.ShareLevel = shareLevel;
            }
            detectPub = PushService.DetectPub || shareLevel == 2;
        }
        catch (Exception ex)
        {
            await Listek.AlertAsync(this, "Chyba", $"Nepodařilo se načíst profil: {ex.Message}");
        }
        finally
        {
            UpdateChips();
        }
    }

    private void OnGenderClicked(object sender, EventArgs e)
    {
        gender = ((PencilButton)sender).CommandParameter as string;
        UpdateChips();
    }

    private void OnTypeClicked(object sender, EventArgs e)
    {
        drinkerType = ((PencilButton)sender).CommandParameter as string;
        UpdateChips();
    }

    private void OnReminderClicked(object sender, EventArgs e)
    {
        if (((PencilButton)sender).CommandParameter is string s && int.TryParse(s, out var minutes))
            Reminders.Minutes = minutes;
        UpdateChips();
    }

    // ---------------- jen v telefonu: domov, ceny, promile ----------------

    private bool loadingLocal;

    private void LoadLocalSettings()
    {
        loadingLocal = true;
        HomeClearButton.IsVisible = HomePlace.IsSet;
        HomeSetButton.Text = HomePlace.IsSet ? "Přestěhovat" : "Nastavit domov";

        static string P(int v) => v > 0 ? v.ToString(System.Globalization.CultureInfo.InvariantCulture) : "";
        PricePivoEntry.Text = P(Prices.Pivo);
        PriceVinoEntry.Text = P(Prices.Vino);
        PricePanakEntry.Text = P(Prices.Panak);
        WeightEntry.Text = P(Promile.WeightKg);
        loadingLocal = false;
    }

    private async void OnHomeSetClicked(object sender, EventArgs e) =>
        await Shell.Current.GoToAsync("//Hospody", new Dictionary<string, object> { ["mode"] = "home" });

    private void OnHomeClearClicked(object sender, EventArgs e)
    {
        HomePlace.Clear();
        LoadLocalSettings();
    }

    private static int ParseNum(string? text) =>
        int.TryParse((text ?? "").Trim(), out var n) && n > 0 ? n : 0;

    private void OnPriceChanged(object? sender, TextChangedEventArgs e)
    {
        if (loadingLocal) return;
        Prices.Pivo = Math.Min(ParseNum(PricePivoEntry.Text), PubService.MaxPrice);
        Prices.Vino = Math.Min(ParseNum(PriceVinoEntry.Text), PubService.MaxPrice);
        Prices.Panak = Math.Min(ParseNum(PricePanakEntry.Text), PubService.MaxPrice);
    }

    private void OnWeightChanged(object? sender, TextChangedEventArgs e)
    {
        if (loadingLocal) return;
        Promile.WeightKg = ParseNum(WeightEntry.Text);
    }

    private void OnPromileClicked(object sender, EventArgs e)
    {
        Promile.Enabled = ((PencilButton)sender).CommandParameter as string == "1";
        UpdateChips();
    }

    private void OnDegreesClicked(object sender, EventArgs e)
    {
        if (((PencilButton)sender).CommandParameter is string s && int.TryParse(s, out var deg))
            Promile.BeerDegrees = deg;
        UpdateChips();
    }

    private void UpdateChips()
    {
        SetChip(PromileOn, Promile.Enabled);
        SetChip(PromileOff, !Promile.Enabled);
        PromileBox.IsVisible = Promile.Enabled;
        SetChip(Deg10, Promile.BeerDegrees == 10);
        SetChip(Deg11, Promile.BeerDegrees == 11);
        SetChip(Deg12, Promile.BeerDegrees == 12);

        SetChip(NotifyOn, notifyFriends);
        SetChip(NotifyOff, !notifyFriends);
        SetChip(DetectOn, detectPub);
        SetChip(DetectOff, !detectPub);
        SetChip(ShareWhere, shareLevel == 2);
        SetChip(ShareYes, shareLevel == 1);
        SetChip(ShareNo, shareLevel == 0);

        var rem = Reminders.Minutes;
        SetChip(Rem0, rem == 0);
        SetChip(Rem20, rem == 20);
        SetChip(Rem30, rem == 30);
        SetChip(Rem60, rem == 60);

        SetChip(GenderPic, gender == "Píč");
        SetChip(GenderPicka, gender == "Píčka");
        SetChip(TypePivo, drinkerType == "Pivopíč/ka");
        SetChip(TypeVino, drinkerType == "Vínopíč/ka");
    }

    /// <summary>Vysvětlivky schované pod „(info)“, ať nastavení není samý text.</summary>
    private async void OnInfoTapped(object? sender, TappedEventArgs e)
    {
        var (title, text) = (e.Parameter as string) switch
        {
            "reminder" => ("Připomínka",
                "Když po zápisu tolik minut nic nezapíšeš, přijde připomínka s tlačítky +1 a Dneska končím. Po 6. ráno už nic. Jen v tomhle telefonu."),
            "notify" => ("Oznámení od kámošů",
                "Žádosti o přátelství, kámoš si dal první drink, připil si s tebou nebo tě zve do hospody."),
            "detect" => ("Poznávat hospodu",
                "Při zápisu se podívá, jestli jsi do 60 m od hospody (nebo doma). Uvidíš to na lístku a ve statistikách „kde nejčastěji piješ“ – ty vidí i kámoši. Poloha se nikam neukládá."),
            "share" => ("Hlásit kámošům",
                "Při prvním drinku po 3h pauze přijde kámošům oznámení a uvidí tě v Kámoších.\n\n" +
                "I kde – „X si právě dal pivo – U Peťky“ / „… doma“ / „… venku“ a uvidí tě i na mapě (i doma).\n" +
                "Že piju – „X si právě dal pivo“, bez hospody.\n" +
                "Nehlásit – kámošům nic nepřijde a mezi pijícími nebudeš."),
            "home" => ("Domov",
                (HomePlace.IsSet ? "Nastavený. " : "Nenastavený – pití doma se počítá jako „jinde“. ") +
                "Pití do 80 m od domova se počítá jako „doma“. Poloha domova zůstává jen v tomhle telefonu."),
            "prices" => ("Ceny",
                "Z nich se počítá útrata ve statistikách. Když má hospoda v profilu vyplněné ceny, vezmou se ty. Tvoje ceny vidíš jen ty."),
            "promile" => ("Promile",
                "Odhad ze vzorce (Widmark) podle váhy, pohlaví a časů zápisů. Bez vyplněné váhy se nepočítá. Skutečnost se může lišit klidně o půl promile – podle toho NIKDY neřiď. Váha zůstává jen v telefonu."),
            _ => ("", "")
        };
        if (text.Length > 0) await Listek.AlertAsync(this, title, text);
    }

    /// <summary>Vybraná volba = vyčmáraná hnědou pastelkou, nevybraná = bledá tužka.</summary>
    private static void SetChip(PencilButton chip, bool selected)
    {
        chip.Variant = selected ? PencilVariant.Primary : PencilVariant.Secondary;
        chip.Opacity = selected ? 1.0 : 0.45;
    }

    private async void OnSaveClicked(object sender, EventArgs e)
    {
        var userId = AuthSession.Uid;
        if (userId == null)
        {
            await Shell.Current.GoToAsync("//SignIn");
            return;
        }

        var nickname = NicknameEntry.Text?.Trim();
        if (string.IsNullOrEmpty(nickname))
        {
            await Listek.AlertAsync(this, "Chyba", "Přezdívka je povinná, podle ní tě najdou kámoši.");
            return;
        }

        try
        {
            var oldNickname = UserSession.Profile?.Nickname;
            var nicknameChanged = !string.Equals(oldNickname, nickname, StringComparison.Ordinal);

            if (nicknameChanged)
            {
                var taken = await firestoreService.FindUserByNickname(nickname);
                if (taken != null && taken.UserId != userId)
                {
                    await Listek.AlertAsync(this, "Zabráno", $"Přezdívku „{nickname}“ už má někdo jiný.");
                    return;
                }
            }

            var profile = new UserModel
            {
                Id = userId,
                UserId = userId,
                Name = NameEntry.Text?.Trim(),
                Nickname = nickname,
                Gender = gender,
                DrinkerType = drinkerType,
                Email = AuthSession.Email,
                NotifyFriends = notifyFriends,
                SharePub = shareLevel == 2,
                ShareLevel = shareLevel,
                FavoritePubs = UserSession.Profile?.FavoritePubs ?? new(),
                MaskFor = UserSession.Profile?.MaskFor ?? new()
            };
            await firestoreService.UpdateUserData(userId, profile);
            UserSession.SetProfile(profile);
            _ = NickDirectory.PublishNickAsync(nickname);   // adresář pro hledání kámošů

            // přezdívka je uložená i v přátelstvích, ať se kámošům nečtou profily
            if (nicknameChanged)
                await firestoreService.PropagateNickname(userId, nickname);

            await Listek.AlertAsync(this, "Hotovo", "Změny byly uloženy.");
            await Shell.Current.GoToAsync("//MainPage");
        }
        catch (Exception ex)
        {
            await Listek.AlertAsync(this, "Chyba", $"Uložení se nepovedlo: {ex.Message}");
        }
    }

    private async void OnCheckUpdateClicked(object sender, EventArgs e)
    {
        try
        {
            UpdateButton.IsEnabled = false;
            var release = await AppUpdater.CheckAsync(manual: true);
            if (release == null)
                await Listek.AlertAsync(this, "Nic nového", $"Verze {AppUpdater.CurrentVersionText} je nejnovější.");
            else
                await UpdatePrompt.OfferAsync(this, release);
        }
        catch (Exception ex)
        {
            await Listek.AlertAsync(this, "Chyba", $"Aktualizaci se nepodařilo zjistit: {ex.Message}");
        }
        finally
        {
            UpdateButton.IsEnabled = true;
        }
    }

    private async void OnNotifyClicked(object sender, EventArgs e)
    {
        var on = ((PencilButton)sender).CommandParameter as string == "1";
        if (on == notifyFriends) return;
        notifyFriends = on;
        UpdateChips();
        await SavePrefsAsync();
    }

    /// <summary>Poznávání hospody – jen v telefonu. Vypnutí vypne i hlášení kámošům.</summary>
    private async void OnDetectClicked(object sender, EventArgs e)
    {
        var on = ((PencilButton)sender).CommandParameter as string == "1";
        if (on == detectPub) return;
        if (on && !await EnsureLocationAsync()) return;

        detectPub = on;
        PushService.DetectPub = on;
        if (!on && shareLevel == 2)
        {
            shareLevel = 1;   // bez poznávání hospody jde hlásit jen „že piju“
            PushService.ShareLevel = 1;
            UpdateChips();
            await SavePrefsAsync();
            return;
        }
        UpdateChips();
    }

    /// <summary>Hlášení kámošům: „i kde“ potřebuje poznávání hospody, takže ho zapne taky.</summary>
    private async void OnShareClicked(object sender, EventArgs e)
    {
        if (((PencilButton)sender).CommandParameter is not string p || !int.TryParse(p, out var level)) return;
        if (level == shareLevel) return;
        if (level == 2 && !await EnsureLocationAsync()) return;

        shareLevel = level;
        PushService.ShareLevel = level;
        if (level == 2)
        {
            detectPub = true;
            PushService.DetectPub = true;
        }
        UpdateChips();
        await SavePrefsAsync();
    }

    /// <summary>Bez polohy nejde poznat hospodu.</summary>
    private async Task<bool> EnsureLocationAsync()
    {
        var status = await Permissions.CheckStatusAsync<Permissions.LocationWhenInUse>();
        if (status != PermissionStatus.Granted)
            status = await Permissions.RequestAsync<Permissions.LocationWhenInUse>();
        if (status == PermissionStatus.Granted) return true;

        await Listek.AlertAsync(this, "Bez polohy to nejde",
            "Povol appce polohu (jen při používání), jinak nepoznám, ve které hospodě sedíš.");
        return false;
    }

    private async void OnUsageClicked(object sender, EventArgs e) =>
        await Shell.Current.GoToAsync("//Spotreba");

    private async void OnOsmClicked(object sender, EventArgs e) =>
        await Shell.Current.GoToAsync("//OsmNavrhy");

    private async void OnNewsClicked(object sender, EventArgs e) =>
        await Shell.Current.GoToAsync("//Novinky", new Dictionary<string, object> { ["back"] = "//Settings" });

    private async Task SavePrefsAsync()
    {
        if (AuthSession.Uid is not string uid) return;
        try
        {
            await firestoreService.UpdateNotificationPrefs(uid, notifyFriends, shareLevel);
            if (UserSession.Profile is { } p)
            {
                p.NotifyFriends = notifyFriends;
                p.ShareLevel = shareLevel;
                p.SharePub = shareLevel == 2;
            }
        }
        catch (Exception ex)
        {
            await Listek.AlertAsync(this, "Chyba", $"Nastavení se nepodařilo uložit: {ex.Message}");
        }
    }

    private async void OnLogoutClicked(object sender, EventArgs e)
    {
        await PushService.UnregisterAsync();   // tenhle telefon už nemá chodit oznámení pro tenhle účet
        FriendBadge.Reset();
        AuthSession.SignOut();
        Preferences.Remove("LastPassword");
        await Shell.Current.GoToAsync("//SignIn");
    }

    private void OnZpetClicked(object sender, EventArgs e) => OnBackButtonPressed();

    // systémové tlačítko Zpět: nejdřív zavřít případný lístek s hláškou, pak o stránku zpátky
    protected override bool OnBackButtonPressed()
    {
        if (Listek.DismissActive()) return true;
        Dispatcher.Dispatch(async () => await Shell.Current.GoToAsync("//MainPage"));
        return true;
    }
}
