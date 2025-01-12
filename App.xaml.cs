using Dezolati.Models;
using Dezolati.Pages;
using Firebase.Auth;

namespace Dezolati
{
    public partial class App : Application
    {
        private readonly FirebaseAuthClient _authClient;
        public App(FirebaseAuthClient authClient)
        {
            InitializeComponent();
            _authClient = authClient;
            MainPage = new AppShell();

            Device.BeginInvokeOnMainThread(async () =>
            {
                var user = _authClient.User;
                if (user != null && user.Credential != null)
                {
                    var expirationTime = DateTime.UtcNow.AddSeconds(user.Credential.ExpiresIn);
                    if (expirationTime > DateTime.UtcNow)
                    {
                        await Shell.Current.GoToAsync("//MainPage");
                    }
                    else
                    {
                        await Shell.Current.GoToAsync("//SignIn");
                    }
                }
                else
                {
                    await Shell.Current.GoToAsync("//SignIn");
                }
            });
        }
    }
}
