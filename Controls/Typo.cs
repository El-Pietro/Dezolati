namespace Dezolati.Controls
{
    /// <summary>
    /// Jednotná typografie. Všechny velikosti písma a tlačítek jsou DynamicResource
    /// a škálují se podle šířky displeje (základ = běžný telefon ~390 dp).
    /// Použití v XAML: FontSize="{DynamicResource FontBody}" nebo styly Title/Heading/FieldLabel/Body/Small z App.xaml.
    /// </summary>
    public static class Typo
    {
        private const double BaseWidth = 390;

        public static double Scale { get; private set; } = 1;

        public static void Apply(ResourceDictionary resources)
        {
            double width = BaseWidth;
            try
            {
                var info = DeviceDisplay.Current.MainDisplayInfo;
                if (info.Density > 0)
                    width = Math.Min(info.Width, info.Height) / info.Density; // kratší strana = na výšku
            }
            catch { /* neznámý displej → základ */ }

            // na počítači je okno, ne celý monitor – drž se rozumné velikosti
            if (DeviceInfo.Current.Idiom == DeviceIdiom.Desktop)
                width = 430;

            Scale = Math.Clamp(width / BaseWidth, 0.82, 1.3);

            void Set(string key, double value) => resources[key] = Math.Round(value * Scale);

            Set("FontTitle", 40);          // nadpis lístku
            Set("FontHeading", 30);        // podnadpis / nadpis sekce
            Set("FontLabel", 22);          // popisky polí
            Set("FontBody", 22);           // běžný text, vstupy
            Set("FontSmall", 18);          // drobný text, chyby
            Set("FontButton", 24);         // pastelková tlačítka
            Set("FontButtonSmall", 20);    // vedlejší / malá tlačítka
            Set("ButtonHeight", 50);
            Set("ButtonHeightSmall", 42);
        }
    }
}
