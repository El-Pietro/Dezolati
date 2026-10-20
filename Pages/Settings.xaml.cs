using Dezolati.Controls;
using Dezolati.Models;
using Dezolati.Services;

namespace Dezolati.Pages;

public partial class Settings : ContentPage
{
    private readonly FirestoreService firestoreService = new();

    private string? gender;
    private string? drinkerType;

    public Settings()
    {
        InitializeComponent();
    }

    protected override async void OnAppearing()
    {
        base.OnAppearing();
        VersionLabel.Text = $"Dezoláti {AppUpdater.CurrentVersionText}";
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
            }
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

    private void UpdateChips()
    {
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
                Email = AuthSession.Email
            };
            await firestoreService.UpdateUserData(userId, profile);
            UserSession.SetProfile(profile);

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

    private async void OnLogoutClicked(object sender, EventArgs e)
    {
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
