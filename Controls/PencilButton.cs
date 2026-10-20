using System.Windows.Input;
using Microsoft.Maui.Controls.Shapes;

namespace Dezolati.Controls
{
    public enum PencilVariant
    {
        /// <summary>hnědá pastelka – hlavní akce</summary>
        Primary,
        /// <summary>šedá tužka – vedlejší akce</summary>
        Secondary,
        /// <summary>červená pastelka – odhlášení, odmítnutí</summary>
        Danger,
        /// <summary>bílé orámování – tlačítka ležící na stole (Zpět, Nastavení…)</summary>
        Outline
    }

    /// <summary>
    /// Tlačítko ve stylu lístku: pozadí vyčmárané pastelkou (PNG textura) + ručně psaný text.
    /// Text se vždy vejde: když je moc dlouhý, písmo se zmenší (FontSize je maximum).
    /// Umí Clicked i Command/CommandParameter jako obyčejný Button.
    /// Pastelkové varianty dávej s HorizontalOptions=Fill nebo s WidthRequest;
    /// Outline se přizpůsobí šířce textu.
    /// </summary>
    public class PencilButton : Grid
    {
        private const double MinFontSize = 13;

        // textura jen zlehka, ať je text čitelný (barva pastelky pořád poznat)
        private readonly Image _background = new() { Aspect = Aspect.Fill, Opacity = 0.6 };
        private readonly Border _outline = new()
        {
            StrokeShape = new RoundRectangle { CornerRadius = 10 },
            StrokeThickness = 1.5,
            Stroke = Colors.WhiteSmoke,
            BackgroundColor = Color.FromRgba(0, 0, 0, 0.18),
            IsVisible = false
        };
        private readonly Label _label = new()
        {
            FontFamily = "CaveatBold",   // tučná Caveat – na čmáranici je regular špatně čitelná
            HorizontalOptions = LayoutOptions.Center,
            VerticalOptions = LayoutOptions.Center,
            HorizontalTextAlignment = TextAlignment.Center,
            VerticalTextAlignment = TextAlignment.Center,
            LineBreakMode = LineBreakMode.NoWrap,
            MaxLines = 1,
            Padding = new Thickness(0),          // přesah písma řeší šířka štítku navíc (viz FitText)
            Margin = new Thickness(12, 0)
        };

        public event EventHandler? Clicked;

        // výchozí velikosti se nastavují jen při přechodu na/z Outline,
        // aby přepnutí Primary/Secondary nepřebilo velikost danou v XAML
        private bool? _outlineSizing;

        public static readonly BindableProperty TextProperty = BindableProperty.Create(
            nameof(Text), typeof(string), typeof(PencilButton), "",
            propertyChanged: (b, _, n) =>
            {
                var pb = (PencilButton)b;
                pb._label.Text = n as string ?? "";
                pb.FitText();
            });

        public static readonly BindableProperty VariantProperty = BindableProperty.Create(
            nameof(Variant), typeof(PencilVariant), typeof(PencilButton), PencilVariant.Primary,
            propertyChanged: (b, _, _) => ((PencilButton)b).ApplyVariant());

        /// <summary>Maximální velikost písma (zmenší se, když se text nevejde).</summary>
        public static readonly BindableProperty FontSizeProperty = BindableProperty.Create(
            nameof(FontSize), typeof(double), typeof(PencilButton), 24d,
            propertyChanged: (b, _, _) => ((PencilButton)b).FitText());

        public static readonly BindableProperty CommandProperty = BindableProperty.Create(
            nameof(Command), typeof(ICommand), typeof(PencilButton), null,
            propertyChanged: (b, o, n) => ((PencilButton)b).OnCommandChanged(o as ICommand, n as ICommand));

        public static readonly BindableProperty CommandParameterProperty = BindableProperty.Create(
            nameof(CommandParameter), typeof(object), typeof(PencilButton), null,
            propertyChanged: (b, _, _) => ((PencilButton)b).RefreshCanExecute());

        public string Text
        {
            get => (string)GetValue(TextProperty);
            set => SetValue(TextProperty, value);
        }

        public PencilVariant Variant
        {
            get => (PencilVariant)GetValue(VariantProperty);
            set => SetValue(VariantProperty, value);
        }

        public double FontSize
        {
            get => (double)GetValue(FontSizeProperty);
            set => SetValue(FontSizeProperty, value);
        }

        public ICommand? Command
        {
            get => (ICommand?)GetValue(CommandProperty);
            set => SetValue(CommandProperty, value);
        }

        public object? CommandParameter
        {
            get => GetValue(CommandParameterProperty);
            set => SetValue(CommandParameterProperty, value);
        }

        public PencilButton()
        {
            MinimumWidthRequest = 48;

            Children.Add(_outline);
            Children.Add(_background);
            Children.Add(_label);

            var tap = new TapGestureRecognizer();
            tap.Tapped += OnTapped;
            GestureRecognizers.Add(tap);

            SizeChanged += (_, _) => FitText();
            _label.HandlerChanged += (_, _) => { _fitKey = null; FitText(); };
            ApplyVariant();
        }

        private void ApplyVariant()
        {
            var outline = Variant == PencilVariant.Outline;
            _outline.IsVisible = outline;
            _background.IsVisible = !outline;

            // tlačítka na stole jsou menší (atributy uvedené v XAML za Variant to můžou přepsat)
            if (_outlineSizing != outline)
            {
                _outlineSizing = outline;
                this.SetDynamicResource(FontSizeProperty, outline ? "FontButtonSmall" : "FontButton");
                this.SetDynamicResource(HeightRequestProperty, outline ? "ButtonHeightSmall" : "ButtonHeight");
            }

            (string? texture, string text) = Variant switch
            {
                PencilVariant.Secondary => ("skrabanec.png", "#151515"),
                PencilVariant.Danger => ("skrabanec_cerveny.png", "#220404"),
                PencilVariant.Outline => (null, "#FAFAFA"),
                _ => ("skrabanec_hnedy.png", "#1F0F04")
            };
            if (texture != null) _background.Source = texture;
            _label.TextColor = Color.FromArgb(text);
            FitText();
        }

        // Caveat kreslí poslední písmeno (čárky, háčky) až o 0,2 em za šířku, kterou o sobě hlásí,
        // a Android TextView ořízne text přesně na tu šířku (padding ani mezery na konci nepomáhají).
        // Proto má štítek pevnou šířku = změřený text + kus navíc a text je v něm na středu.
        private const double OverhangEm = 0.5;   // 0,25 em na každou stranu

        private string? _fitKey;

        /// <summary>Nastaví písmo (zmenší, když se nevejde) a šířku štítku i s rezervou na přesah.</summary>
        private void FitText()
        {
            var max = FontSize > 0 ? FontSize : 24;
            var text = _label.Text ?? "";
            if (text.Length == 0 || _label.Handler == null)
            {
                _label.FontSize = max;
                return;
            }

            var outline = Variant == PencilVariant.Outline;
            // Outline se přizpůsobuje textu; ostatní mají šířku danou zvenku
            var available = outline ? double.PositiveInfinity : Width - _label.Margin.HorizontalThickness;
            if (!outline && Width <= 0) return;

            var key = $"{text}|{max}|{(outline ? -1 : Math.Round(available))}";
            if (key == _fitKey) return;   // nic se nezměnilo → neměřit znovu (SizeChanged chodí často)

            _label.WidthRequest = -1;
            var size = max;
            double width;
            while (true)
            {
                _label.FontSize = size;
                var desired = ((IView)_label).Measure(double.PositiveInfinity, double.PositiveInfinity);
                if (desired.Width <= 0) return;   // ještě nejde měřit, zkusí se při dalším SizeChanged
                width = desired.Width + size * OverhangEm;
                if (width <= available || size <= MinFontSize) break;
                size -= 1;
            }

            _label.WidthRequest = Math.Min(width, available);
            _fitKey = key;
        }

        private async void OnTapped(object? sender, TappedEventArgs e)
        {
            if (!IsEnabled) return;

            // pastelka se při ťuknutí trochu zmáčkne
            await this.ScaleTo(0.94, 60, Easing.CubicOut);
            await this.ScaleTo(1.0, 80, Easing.CubicIn);

            Clicked?.Invoke(this, EventArgs.Empty);
            if (Command?.CanExecute(CommandParameter) == true)
                Command.Execute(CommandParameter);
        }

        private void OnCommandChanged(ICommand? oldCommand, ICommand? newCommand)
        {
            if (oldCommand != null) oldCommand.CanExecuteChanged -= OnCanExecuteChanged;
            if (newCommand != null) newCommand.CanExecuteChanged += OnCanExecuteChanged;
            RefreshCanExecute();
        }

        private void OnCanExecuteChanged(object? sender, EventArgs e) => RefreshCanExecute();

        private void RefreshCanExecute()
        {
            if (Command != null)
                IsEnabled = Command.CanExecute(CommandParameter);
        }

        protected override void OnPropertyChanged(string? propertyName = null)
        {
            base.OnPropertyChanged(propertyName);
            if (propertyName == nameof(IsEnabled))
                Opacity = IsEnabled ? 1.0 : 0.45;
        }
    }
}
