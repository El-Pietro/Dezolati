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
            Services.UserSession.RaiseDrinksChanged();
            _ = Controls.UpdatePrompt.ResumeIfPendingAsync(); // po povolení instalace v nastavení
        }
    }
}
