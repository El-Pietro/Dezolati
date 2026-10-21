using Dezolati.Controls;

namespace Dezolati
{
    public partial class App : Application
    {
        public App()
        {
            InitializeComponent();

            // velikosti písma a tlačítek podle displeje
            Typo.Apply(Resources);

            MainPage = new AppShell();
        }

        // návrat do appky (třeba po zápisu z notifikace nebo ráno po 6:00) → obnovit lístek
        protected override void OnResume()
        {
            base.OnResume();
            Services.LocalCache.MarkStale();               // mezitím mohl zapisovat jiný telefon → lístek přečíst ze serveru
            Services.UserSession.RaiseDrinksChanged();
            Services.FriendBadge.Invalidate();             // mezitím mohla přijít žádost o přátelství
            _ = Services.FriendBadge.RefreshAsync();
            _ = Controls.UpdatePrompt.ResumeIfPendingAsync(); // po povolení instalace v nastavení
        }
    }
}
