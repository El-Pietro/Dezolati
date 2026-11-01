using Android.App;
using Android.Content;
using Android.Content.PM;
using Android.Widget;

namespace Dezolati.Droid
{
    /// <summary>
    /// Instalace aktualizace přes PackageInstaller (session). Proti „otevřít soubor“ odpadá výběr aplikace
    /// a obrazovka „Aplikace nainstalována – Otevřít / Hotovo“: zůstane jen jedno systémové „Aktualizovat?“.
    /// Na Androidu 12+ může systém další aktualizace od stejného instalátoru pustit i bez ptaní.
    /// </summary>
    public static class ApkInstaller
    {
        public static void Install(string apkPath)
        {
            var ctx = Android.App.Application.Context;
            var installer = ctx.PackageManager!.PackageInstaller;
            var parameters = new PackageInstaller.SessionParams(PackageInstallMode.FullInstall);
            parameters.SetAppPackageName(ctx.PackageName);

            var sessionId = installer.CreateSession(parameters);
            using var session = installer.OpenSession(sessionId);
            try
            {
                var length = new FileInfo(apkPath).Length;
                using (var output = session.OpenWrite("dezolati.apk", 0, length))
                using (var input = File.OpenRead(apkPath))
                {
                    input.CopyTo(output);
                    session.Fsync(output);
                }

                var intent = new Intent(ctx, typeof(InstallResultReceiver));
                var flags = PendingIntentFlags.UpdateCurrent;
                // systém do intentu doplňuje stav → na Androidu 12+ musí být „mutable“
                if (OperatingSystem.IsAndroidVersionAtLeast(31)) flags |= PendingIntentFlags.Mutable;
                var pending = PendingIntent.GetBroadcast(ctx, sessionId, intent, flags)!;
                session.Commit(pending.IntentSender);
            }
            catch
            {
                session.Abandon();
                throw;
            }
        }
    }

    /// <summary>Výsledek instalace: potvrzení od uživatele → ukázat systémový dialog, chyba → krátká hláška.</summary>
    [BroadcastReceiver(Exported = false)]
    public class InstallResultReceiver : BroadcastReceiver
    {
        public override void OnReceive(Context? context, Intent? intent)
        {
            if (context == null || intent == null) return;
            var status = intent.GetIntExtra(PackageInstaller.ExtraStatus, int.MinValue);

            if (status == (int)PackageInstallStatus.PendingUserAction)
            {
#pragma warning disable CA1422, CS0618 // GetParcelableExtra(string) je od API 33 zastaralé, ale funguje všude
                if (intent.GetParcelableExtra(Intent.ExtraIntent) is Intent confirm)
#pragma warning restore CA1422, CS0618
                {
                    confirm.AddFlags(ActivityFlags.NewTask);
                    context.StartActivity(confirm);
                }
                return;
            }

            if (status == (int)PackageInstallStatus.Success) return;   // appka se právě nahradila novou verzí

            var why = intent.GetStringExtra(PackageInstaller.ExtraStatusMessage);
            var text = status == (int)PackageInstallStatus.FailureAborted
                ? "Aktualizace zrušena."
                : $"Aktualizace se nepovedla{(string.IsNullOrEmpty(why) ? "" : $": {why}")}";
            Toast.MakeText(context, text, ToastLength.Long)?.Show();
        }
    }
}
