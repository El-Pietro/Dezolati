using Dezolati.Controls;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Dezolati.Services;
using Firebase.Auth;

namespace Dezolati.Models
{
    public partial class SignUpModel : ObservableObject
    {
        private readonly FirebaseAuthClient _authClient;

        [ObservableProperty]
        private string _email = "";

        [ObservableProperty]
        private string _username = "";

        [ObservableProperty]
        private string _password = "";

        [ObservableProperty]
        private string _error = "";

        [ObservableProperty]
        private bool _isBusy;

        public SignUpModel(FirebaseAuthClient authClient)
        {
            _authClient = authClient;
        }

        [RelayCommand]
        private async Task SignUp()
        {
            if (IsBusy) return;
            Error = "";

            var email = Email?.Trim() ?? "";
            var nickname = Username?.Trim() ?? "";

            if (string.IsNullOrEmpty(email) || string.IsNullOrEmpty(nickname) || string.IsNullOrEmpty(Password))
            {
                Error = "Vyplň e-mail, přezdívku i heslo.";
                return;
            }
            if (Password.Length < 6)
            {
                Error = "Heslo musí mít aspoň 6 znaků.";
                return;
            }

            try
            {
                IsBusy = true;

                // vytvoří účet a rovnou přihlásí
                await _authClient.CreateUserWithEmailAndPasswordAsync(email, Password, nickname);
                Password = "";
                Preferences.Set("LastEmail", email);

                var fs = new FirestoreService();
                var uid = AuthSession.Uid!;

                // přezdívka musí být jedinečná, jinak by tě kámoši nenašli
                var taken = await fs.FindUserByNickname(nickname);
                var finalNickname = nickname;
                if (taken != null && taken.UserId != uid)
                    finalNickname = $"{nickname}{Random.Shared.Next(100, 999)}";

                await fs.EnsureUserDoc(uid, email, finalNickname);

                if (finalNickname != nickname)
                    await Listek.AlertAsync(Shell.Current?.CurrentPage, "Přezdívka zabraná",
                        $"„{nickname}“ už někdo má, dostal jsi „{finalNickname}“. Změnit si ji můžeš v Nastavení.");

                await Shell.Current.GoToAsync("//MainPage");
            }
            catch (FirebaseAuthException ex)
            {
                Error = ex.Reason switch
                {
                    AuthErrorReason.EmailExists => "Tenhle e-mail už má účet. Přihlas se.",
                    AuthErrorReason.InvalidEmailAddress => "Tohle není platný e-mail.",
                    AuthErrorReason.WeakPassword => "Moc slabé heslo.",
                    _ => "Registrace se nepovedla."
                };
            }
            catch (Exception ex)
            {
                Error = "Registrace se nepovedla: " + ex.Message;
            }
            finally
            {
                IsBusy = false;
            }
        }

        [RelayCommand]
        private async Task NavigateSignIn()
        {
            Error = "";
            await Shell.Current.GoToAsync("//SignIn");
        }
    }
}
