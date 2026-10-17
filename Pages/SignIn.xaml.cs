using Dezolati.Models;
using Dezolati.Services;

namespace Dezolati.Pages;

public partial class SignIn : ContentPage
{
    public SignIn(SignInModel viewModel)
    {
        InitializeComponent();
        BindingContext = viewModel;
    }

    protected override void OnAppearing()
    {
        base.OnAppearing();

        // přihlášení si pamatuje FirebaseAuthClient – rovnou na lístek
        if (AuthSession.IsSignedIn)
            Dispatcher.Dispatch(async () => await Shell.Current.GoToAsync("//MainPage"));
    }
}
