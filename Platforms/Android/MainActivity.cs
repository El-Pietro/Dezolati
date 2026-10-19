using Android.App;
using Android.Content.PM;
using Android.OS;
using Android.Views;

namespace Dezolati
{
    [Activity(Theme = "@style/Maui.SplashTheme", MainLauncher = true, LaunchMode = LaunchMode.SingleTop, ConfigurationChanges = ConfigChanges.ScreenSize | ConfigChanges.Orientation | ConfigChanges.UiMode | ConfigChanges.ScreenLayout | ConfigChanges.SmallestScreenSize | ConfigChanges.Density)]
    public class MainActivity : MauiAppCompatActivity
    {
        protected override void OnCreate(Bundle? savedInstanceState)
        {
            base.OnCreate(savedInstanceState);

            // dřevo až pod hodiny a baterku: průhledná stavová lišta, obsah se kreslí pod ni
            // (spodní navigační lišta zůstává, jak je)
            if (Window is { } window)
            {
                window.SetStatusBarColor(Android.Graphics.Color.Transparent);
#pragma warning disable CA1422 // starší API, ale funguje a řeší jen stavovou lištu
                window.DecorView.SystemUiVisibility =
                    (StatusBarVisibility)(SystemUiFlags.LayoutStable | SystemUiFlags.LayoutFullscreen);
#pragma warning restore CA1422
            }

            // horní tlačítka posunout pod stavovou lištu (její výška se liší podle telefonu)
            var res = Resources;
            var id = res?.GetIdentifier("status_bar_height", "dimen", "android") ?? 0;
            if (res != null && id > 0 && Microsoft.Maui.Controls.Application.Current is { } app)
            {
                var density = res.DisplayMetrics?.Density ?? 1f;
                var statusBarDp = res.GetDimensionPixelSize(id) / density;
                app.Resources["TopBarMargin"] = new Thickness(12, 10 + statusBarDp, 12, 10);
                app.Resources["TopInset"] = new Thickness(0, statusBarDp, 0, 0);
            }
        }
    }
}
