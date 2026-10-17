using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Dezolati.Services;
using Firebase.Auth;

namespace Dezolati.Models
{
    public partial class SignInModel : ObservableObject
    {
        private readonly FirebaseAuthClient _authClient;

        [ObservableProperty]
        private string _email = "";

        [ObservableProperty]
        private string _password = "";

        [ObservableProperty]
        private string _error = "";

        [ObservableProperty]
        private bool _isBusy;

        public SignInModel(FirebaseAuthClient authClient)
        {
            _authClient = authClient;

            Email = Preferences.Get("LastEmail", string.Empty);
            // dřívější verze ukládala heslo v čitelné podobě – uklidit
            Preferences.Remove("LastPassword");
        }

        [RelayCommand]
        private async Task SignIn()
        {
            if (IsBusy) return;
            Error = "";

            if (string.IsNullOrWhiteSpace(Email) || string.IsNullOrEmpty(Password))
            {
                Error = "Vyplň e-mail i heslo.";
                return;
            }

            try
            {
                IsBusy = true;
                await _authClient.SignInWithEmailAndPasswordAsync(Email.Trim(), Password);
                Preferences.Set("LastEmail", Email.Trim());
                Password = "";

                // starší účty nemusí mít profil v Users (pak by je nikdo nenašel)
                try
                {
                    await new FirestoreService().EnsureUserDoc(AuthSession.Uid!, AuthSession.Email, AuthSession.DisplayName);
                }
                catch { /* není kritické, hlavní stránka případně ukáže chybu */ }

                await Shell.Current.GoToAsync("//MainPage");
            }
            catch (FirebaseAuthException ex)
            {
                Error = ex.Reason switch
                {
                    AuthErrorReason.WrongPassword or
                    AuthErrorReason.UnknownEmailAddress or
                    AuthErrorReason.InvalidEmailAddress => "Špatný e-mail nebo heslo.",
                    AuthErrorReason.TooManyAttemptsTryLater => "Moc pokusů. Dej si pauzu (a vodu).",
                    AuthErrorReason.UserDisabled => "Účet je zablokovaný.",
                    _ => "Přihlášení se nepovedlo. Zkontroluj e-mail a heslo."
                };
            }
            catch (Exception ex)
            {
                Error = "Přihlášení se nepovedlo: " + ex.Message;
            }
            finally
            {
                IsBusy = false;
            }
        }

        [RelayCommand]
        private async Task NavigateSignUp()
        {
            Error = "";
            await Shell.Current.GoToAsync("//SignUp");
        }
    }
}
