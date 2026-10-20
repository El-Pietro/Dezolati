using Android.App;
using Android.Content;
using Android.OS;
using AndroidX.Core.App;
using Firebase.Messaging;

namespace Dezolati.Droid
{
    /// <summary>
    /// Přijímá oznámení z Firebase Cloud Messaging.
    /// Když je appka v pozadí, oznámení zobrazí systém sám (kanál a ikona z AndroidManifestu),
    /// když je v popředí, zobrazíme ho tady.
    /// </summary>
    [Service(Exported = false)]
    [IntentFilter(new[] { "com.google.firebase.MESSAGING_EVENT" })]
    public class DezolatiMessagingService : FirebaseMessagingService
    {
        public override void OnNewToken(string token)
        {
            base.OnNewToken(token);
            _ = Services.PushService.OnNewTokenAsync(token);
        }

        public override void OnMessageReceived(RemoteMessage message)
        {
            base.OnMessageReceived(message);
            var n = message.GetNotification();
            message.Data.TryGetValue("title", out var dataTitle);
            message.Data.TryGetValue("body", out var dataBody);
            PushAndroid.Show(this, n?.Title ?? dataTitle, n?.Body ?? dataBody);

            // žádost o přátelství → odznak na tlačítku Přátelé
            if (message.Data.TryGetValue("type", out var type) && type == "friend")
            {
                Services.FriendBadge.Invalidate();
                _ = Services.FriendBadge.RefreshAsync();
            }
        }
    }

    public static class PushAndroid
    {
        public const string ChannelId = "kamosi";

        private static Context Ctx => global::Android.App.Application.Context;

        /// <summary>Kanál „Kámoši“ (Android 8+). Jde vypnout v nastavení telefonu zvlášť od připomínek.</summary>
        public static void EnsureChannel()
        {
            if (Build.VERSION.SdkInt < BuildVersionCodes.O) return;
            var nm = (NotificationManager?)Ctx.GetSystemService(Context.NotificationService);
            if (nm?.GetNotificationChannel(ChannelId) != null) return;
            nm?.CreateNotificationChannel(new NotificationChannel(ChannelId, "Kámoši", NotificationImportance.High)
            {
                Description = "Žádosti o přátelství a kdo z kámošů sedí v hospodě"
            });
        }

        public static void Show(Context context, string? title, string? body)
        {
            if (string.IsNullOrEmpty(title) && string.IsNullOrEmpty(body)) return;
            try
            {
                EnsureChannel();
                var intent = context.PackageManager?.GetLaunchIntentForPackage(context.PackageName ?? "");
                intent?.AddFlags(ActivityFlags.ClearTop | ActivityFlags.SingleTop);
                var pending = intent == null ? null : PendingIntent.GetActivity(context, 0, intent,
                    PendingIntentFlags.Immutable | PendingIntentFlags.UpdateCurrent);

                var builder = new NotificationCompat.Builder(context, ChannelId)
                    .SetSmallIcon(Resource.Drawable.ic_stat_beer)
                    .SetColor(unchecked((int)0xFF6B3E1E))
                    .SetContentTitle(title)
                    .SetContentText(body)
                    .SetStyle(new NotificationCompat.BigTextStyle().BigText(body))
                    .SetPriority(NotificationCompat.PriorityHigh)
                    .SetAutoCancel(true);
                if (pending != null) builder.SetContentIntent(pending);

                NotificationManagerCompat.From(context).Notify(Random.Shared.Next(10_000, 1_000_000), builder.Build());
            }
            catch (Exception ex)
            {
                // bez povolení oznámení (Android 13+) se prostě nic nezobrazí
                System.Diagnostics.Debug.WriteLine($"Push show: {ex.Message}");
            }
        }

        /// <summary>Token tohoto telefonu pro FCM.</summary>
        public static Task<string?> GetTokenAsync()
        {
            var tcs = new TaskCompletionSource<string?>();
            FirebaseMessaging.Instance.GetToken().AddOnCompleteListener(new TokenListener(tcs));
            return tcs.Task;
        }

        private sealed class TokenListener : Java.Lang.Object, global::Android.Gms.Tasks.IOnCompleteListener
        {
            private readonly TaskCompletionSource<string?> _tcs;
            public TokenListener(TaskCompletionSource<string?> tcs) => _tcs = tcs;

            public void OnComplete(global::Android.Gms.Tasks.Task task)
            {
                if (task.IsSuccessful) _tcs.TrySetResult(task.Result?.ToString());
                else _tcs.TrySetException(task.Exception ?? new Exception("FCM token se nepodařilo získat."));
            }
        }
    }
}
