using Dezolati.Models;
using Dezolati.Pages;
using Dezolati.Services;
using Firebase.Auth;

namespace Dezolati
{
    public partial class App : Application
    {
        public App()
        {
            InitializeComponent();

            MainPage = new AppShell();
        }
    }
}
