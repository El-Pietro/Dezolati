using Dezolati.Models;
using Dezolati.Pages;
using Firebase.Auth;
using Firebase.Auth.Providers;
using Firebase.Auth.Repository;
using Google.Cloud.Firestore.V1;
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

            builder.Services.AddSingleton(new FirebaseAuthClient(new FirebaseAuthConfig()
            {
                ApiKey = "AIzaSyBmfjw3PRo85_PDq8Z763rmh49pags1PjA",
                AuthDomain = "dezolati.firebaseapp.com",
                Providers = new FirebaseAuthProvider[]
                {
                    new EmailProvider()
                },
                UserRepository = new FileUserRepository("Dezolati")
            }));

            builder.Services.AddSingleton<SignIn>();
            builder.Services.AddSingleton<SignInModel>();
            builder.Services.AddSingleton<SignUp>();
            builder.Services.AddSingleton<SignUpModel>();

#endif

            return builder.Build();
        }
    }
}
