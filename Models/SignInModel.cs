using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Firebase.Auth;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using static Google.Apis.Auth.OAuth2.Web.AuthorizationCodeWebApp;

namespace Dezolati.Models
{
    public partial class SignInModel : ObservableObject
    {
        private readonly FirebaseAuthClient _authClient;

        [ObservableProperty]
        private string _email;

        [ObservableProperty]
        private string _password;
        public static string ID { get; private set; }
        public static string Username { get; private set; }
        //public static FirebaseAuthClient AuthClient { get; private set; }


        public SignInModel(FirebaseAuthClient authClient)
        {
            _authClient = authClient;

            Email = Preferences.Get("LastEmail", string.Empty);
            Password = Preferences.Get("LastPassword", string.Empty);
        }

        [RelayCommand]
        private async Task SignIn()
        {
            await _authClient.SignInWithEmailAndPasswordAsync(Email, Password);

            ID = _authClient.User.Info.Uid;
            Username = _authClient.User.Info.DisplayName;
            //AuthClient = _authClient;

            Preferences.Set("LastEmail", Email);
            Preferences.Set("LastPassword", Password);


            await Shell.Current.GoToAsync("//MainPage");
        }

        [RelayCommand]
        private async Task NavigateSignUp()
        {
            await Shell.Current.GoToAsync("//SignUp");
        }
    }
}
