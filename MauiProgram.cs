using Dezolati.Models;
using Dezolati.Pages;
using Dezolati.Services;
using Firebase.Auth;
using Firebase.Auth.Providers;
using Firebase.Auth.Repository;
using Microsoft.Extensions.Logging;

namespace Dezolati
{
    public static class MauiProgram
    {
        public static MauiApp CreateMauiApp()
        {
            var builder = MauiApp.CreateBuilder();
            builder
                .UseMauiApp<App>()
                .ConfigureFonts(fonts =>
                {
                    fonts.AddFont("OpenSans-Regular.ttf", "OpenSansRegular");
                    fonts.AddFont("OpenSans-Semibold.ttf", "OpenSansSemibold");
                    fonts.AddFont("Caveat-Regular.ttf", "Caveat");
                });

#if DEBUG
            builder.Logging.AddDebug();
#endif

            // Firebase Auth – API key je veřejný identifikátor projektu, ne tajemství.
            // Data chrání přihlášení + security rules ve Firestore.
            var authClient = new FirebaseAuthClient(new FirebaseAuthConfig()
            {
                ApiKey = "AIzaSyBmfjw3PRo85_PDq8Z763rmh49pags1PjA",
                AuthDomain = "dezolati.firebaseapp.com",
                Providers = new FirebaseAuthProvider[]
                {
                    new EmailProvider()
                },
                // pamatuje si přihlášení i po restartu appky (místo ukládání hesla)
                UserRepository = new FileUserRepository("Dezolati")
            });
            AuthSession.Client = authClient;

            builder.Services.AddSingleton(authClient);
            builder.Services.AddSingleton<SignIn>();
            builder.Services.AddSingleton<SignInModel>();
            builder.Services.AddSingleton<SignUp>();
            builder.Services.AddSingleton<SignUpModel>();

            return builder.Build();
        }
    }
}
