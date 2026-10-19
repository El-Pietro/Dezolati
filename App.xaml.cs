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
    }
}
