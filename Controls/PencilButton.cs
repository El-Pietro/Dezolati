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

        private readonly Image _background = new() { Aspect = Aspect.Fill };
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
            FontFamily = "Caveat",
            HorizontalOptions = LayoutOptions.Center,
            VerticalOptions = LayoutOptions.Center,
            HorizontalTextAlignment = TextAlignment.Center,
            VerticalTextAlignment = TextAlignment.Center,
            LineBreakMode = LineBreakMode.NoWrap,
            MaxLines = 1,
            Padding = new Thickness(3, 0, 8, 0),   // Caveat přesahuje svůj box – ať se konec neuřízne
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
                pb._label.Text = n as string;
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
                PencilVariant.Secondary => ("skrabanec.png", "#1E1E1E"),
                PencilVariant.Danger => ("skrabanec_cerveny.png", "#2A0808"),
                PencilVariant.Outline => (null, "#F5F5F5"),
                _ => ("skrabanec_hnedy.png", "#2B1608")
            };
            if (texture != null) _background.Source = texture;
            _label.TextColor = Color.FromArgb(text);
            FitText();
        }

        /// <summary>Zmenší písmo tak, aby se text vešel do šířky tlačítka.</summary>
        private void FitText()
        {
            var max = FontSize > 0 ? FontSize : 24;
            _label.FontSize = max;

            // Outline se přizpůsobuje textu, šířku nemá danou
            if (Variant == PencilVariant.Outline || Width <= 0 || string.IsNullOrEmpty(_label.Text))
                return;

            var available = Width - _label.Margin.HorizontalThickness - 4;
            if (available <= 0) return;

            var size = max;
            while (size > MinFontSize)
            {
                _label.FontSize = size;
                var desired = ((IView)_label).Measure(double.PositiveInfinity, double.PositiveInfinity);
                if (desired.Width <= available) break;
                size -= 1;
            }
            _label.FontSize = size;
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
