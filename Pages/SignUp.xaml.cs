using Dezolati.Models;

namespace Dezolati.Pages;

public partial class SignUp : ContentPage
{
	public SignUp(SignUpModel viewModel)
	{
		InitializeComponent();

		BindingContext = viewModel;
	}
}