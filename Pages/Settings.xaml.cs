using Dezolati.Models;
using Dezolati.Services;

namespace Dezolati.Pages;

public partial class Settings : ContentPage
{
    private readonly FirestoreService firestoreService = new();

    public Settings()
    {
        InitializeComponent();
    }

    protected override async void OnAppearing()
    {
        base.OnAppearing();
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
            var user = await firestoreService.GetUserData(userId);
            if (user != null)
            {
                NameEntry.Text = user.Name;
                NicknameEntry.Text = user.Nickname;
                GenderPicker.SelectedItem = user.Gender;
                DrinkerTypePicker.SelectedItem = user.DrinkerType;
            }
        }
        catch (Exception ex)
        {
            await DisplayAlert("Chyba", $"Nepodařilo se načíst profil: {ex.Message}", "OK");
        }
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
            await DisplayAlert("Chyba", "Přezdívka je povinná, podle ní tě najdou kámoši.", "OK");
            return;
        }

        try
        {
            var taken = await firestoreService.FindUserByNickname(nickname);
            if (taken != null && taken.UserId != userId)
            {
                await DisplayAlert("Zabráno", $"Přezdívku „{nickname}“ už má někdo jiný.", "OK");
                return;
            }

            await firestoreService.UpdateUserData(userId, new UserModel
            {
                UserId = userId,
                Name = NameEntry.Text?.Trim(),
                Nickname = nickname,
                Gender = GenderPicker.SelectedItem as string,
                DrinkerType = DrinkerTypePicker.SelectedItem as string,
                Email = AuthSession.Email
            });

            await DisplayAlert("Hotovo", "Změny byly uloženy.", "OK");
            await Shell.Current.GoToAsync("//MainPage");
        }
        catch (Exception ex)
        {
            await DisplayAlert("Chyba", $"Uložení se nepovedlo: {ex.Message}", "OK");
        }
    }

    private async void OnLogoutClicked(object sender, EventArgs e)
    {
        AuthSession.SignOut();
        Preferences.Remove("LastPassword");
        await Shell.Current.GoToAsync("//SignIn");
    }

    private async void OnZpetClicked(object sender, EventArgs e)
    {
        await Shell.Current.GoToAsync("//MainPage");
    }
}
