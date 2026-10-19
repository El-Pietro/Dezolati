using Dezolati.Models;
using Dezolati.Pages;
using Dezolati.Services;
using Firebase.Auth;
using Firebase.Auth.Providers;
using Firebase.Auth.Repository;
using Microsoft.Extensions.Logging;
#if ANDROID
using Plugin.LocalNotification;
using Plugin.LocalNotification.AndroidOption;
#endif

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
                    fonts.AddFont("Caveat-Bold.ttf", "CaveatBold");   // statická tučná instance z Google Fonts (OFL)
                });

#if DEBUG
            builder.Logging.AddDebug();
#endif

#if ANDROID
            // připomínky: každý druh pití má svou kategorii s tlačítky „+1 …“ a „Dneska končím“
            builder.UseLocalNotification(config =>
            {
                foreach (var (kind, label) in new[]
                         {
                             (Reminders.Kind.Pivo, "+1 pivo"),
                             (Reminders.Kind.Vino, "+1 víno"),
                             (Reminders.Kind.Panak, "+1 panák")
                         })
                {
                    config.AddCategory(new NotificationCategory(Reminders.CategoryFor(kind))
                    {
                        ActionList = new HashSet<NotificationAction>
                        {
                            new(Reminders.ActionPlusOne)
                            {
                                Title = label,
                                Android = { LaunchAppWhenTapped = false }
                            },
                            new(Reminders.ActionDone)
                            {
                                Title = "Dneska končím",
                                Android = { LaunchAppWhenTapped = false }
                            }
                        }
                    });
                }
            });

            // tlačítka v notifikaci (funguje i bez otevřené appky)
            LocalNotificationCenter.Current.NotificationActionTapped += e =>
            {
                if (e.IsDismissed || e.IsTapped) return;   // ťuknutí na samotnou notifikaci jen otevře appku
                _ = Reminders.HandleActionAsync(e.ActionId, e.Request?.ReturningData);
            };
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
