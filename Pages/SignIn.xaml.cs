using Dezolati.Models;

namespace Dezolati.Pages;

public partial class SignIn : ContentPage
{
	public SignIn(SignInModel viewModel)
	{
		InitializeComponent();

		BindingContext = viewModel;
	}
}