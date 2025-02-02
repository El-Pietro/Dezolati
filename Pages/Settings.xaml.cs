using Dezolati.Models;
using Dezolati.Services;
using Firebase.Auth;
using Firebase.Auth.Repository;
using Microsoft.Maui.Layouts;

namespace Dezolati.Pages;

public partial class Settings : ContentPage
{
    private FirestoreService firestoreService;
    private readonly FirebaseAuthClient _authClient;
    public Settings()
	{
		InitializeComponent();
        firestoreService = new FirestoreService();
        LoadUserData();
    }
    private async void LoadUserData()
    {
        var userId = SignInModel.ID;

        if (userId == null)
        {
            await DisplayAlert("Chyba", "Uživatel není pøihlášen.", "OK");
            return;
        }

        var user = await firestoreService.GetUserData(userId);

        if (user != null)
        {
            NameEntry.Text = user.Name;
            NicknameEntry.Text = user.Nickname;
            GenderPicker.SelectedItem = user.Gender;
            DrinkerTypePicker.SelectedItem = user.DrinkerType;
        }
    }

    private async void OnSaveClicked(object sender, EventArgs e)
    {
        var userId = SignInModel.ID;

        if (userId == null)
        {
            await DisplayAlert("Chyba", "Uživatel není pøihlášen.", "OK");
            return;
        }

        var user = new UserModel
        {
            Name = NameEntry.Text,
            Nickname = NicknameEntry.Text,
            Gender = (string)GenderPicker.SelectedItem,
            DrinkerType = (string)DrinkerTypePicker.SelectedItem
        };

        await firestoreService.UpdateUserData(userId, user);

        await DisplayAlert("Hotovo", "Zmìny byly uloženy.", "OK");
        await Shell.Current.GoToAsync("//MainPage");

    }

    private async void OnLogoutClicked(object sender, EventArgs e)
    {
        await Shell.Current.GoToAsync("//SignIn");
    }
}