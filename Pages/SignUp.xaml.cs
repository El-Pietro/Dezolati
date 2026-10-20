using Dezolati.Models;

namespace Dezolati.Pages;

public partial class SignUp : ContentPage
{
	public SignUp(SignUpModel viewModel)
	{
		InitializeComponent();

		BindingContext = viewModel;
	}

	// Zpět z registrace = na přihlášení
	protected override bool OnBackButtonPressed()
	{
		Dispatcher.Dispatch(async () => await Shell.Current.GoToAsync("//SignIn"));
		return true;
	}
}