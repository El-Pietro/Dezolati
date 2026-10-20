using Microsoft.Maui.Controls.Shapes;

namespace Dezolati.Controls
{
    /// <summary>
    /// Dialogy ve stylu lístku místo nativních DisplayAlert / DisplayActionSheet.
    /// Lístek se vykreslí přes aktuální stránku (ztmavené pozadí, lehce pootočený papír, pastelková tlačítka).
    /// </summary>
    public static class Listek
    {
        // právě otevřený lístek (systémové tlačítko Zpět ho zavře)
        private static TaskCompletionSource<string?>? _active;

        /// <summary>Zavře otevřený lístek jako ťuknutí mimo. true = nějaký byl otevřený.</summary>
        public static bool DismissActive() => _active?.TrySetResult(null) == true;

        private static readonly Color Paper = Color.FromArgb("#F5F5DC");   // stejná "Beige" jako lístky
        private static readonly Color Ink = Color.FromArgb("#6B3E1E");

        /// <summary>Lístek „pracuju…“ bez tlačítek (stahování apod.). Zavře se přes Dispose.</summary>
        public sealed class BusyNote : IDisposable
        {
            private readonly Label _text;
            private readonly Action _close;
            private bool _closed;

            internal BusyNote(Label text, Action close)
            {
                _text = text;
                _close = close;
            }

            public void SetText(string text) =>
                MainThread.BeginInvokeOnMainThread(() => _text.Text = text);

            public void Dispose()
            {
                if (_closed) return;
                _closed = true;
                MainThread.BeginInvokeOnMainThread(_close);
            }
        }

        public static BusyNote? ShowBusy(Page? page, string title, string text)
        {
            var host = page as ContentPage ?? Shell.Current?.CurrentPage as ContentPage;
            if (host?.Content is not View original) return null;

            var titleLabel = new Label { Text = title, HorizontalOptions = LayoutOptions.Fill, HorizontalTextAlignment = TextAlignment.Center };
            titleLabel.SetDynamicResource(Label.FontSizeProperty, "FontHeading");
            var textLabel = new Label { Text = text, HorizontalOptions = LayoutOptions.Fill, HorizontalTextAlignment = TextAlignment.Center };
            textLabel.SetDynamicResource(Label.FontSizeProperty, "FontBody");

            var card = new Border
            {
                BackgroundColor = Paper,
                Stroke = Ink.WithAlpha(0.35f),
                StrokeThickness = 1,
                StrokeShape = new RoundRectangle { CornerRadius = 2 },
                Padding = new Thickness(22, 18),
                Margin = new Thickness(32),
                MaximumWidthRequest = 420,
                HorizontalOptions = LayoutOptions.Center,
                VerticalOptions = LayoutOptions.Center,
                Rotation = -1.5,
                Shadow = new Shadow { Brush = Colors.Black, Offset = new Point(4, 6), Radius = 12, Opacity = 0.6f },
                Content = new VerticalStackLayout
                {
                    Spacing = 10,
                    Children = { titleLabel, new ActivityIndicator { IsRunning = true, Color = Ink }, textLabel }
                }
            };

            // ztmavení zároveň blokuje ťukání do stránky pod ním
            var dim = new BoxView { Color = Colors.Black, Opacity = 0.55 };
            dim.GestureRecognizers.Add(new TapGestureRecognizer());

            var root = new Grid();
            host.Content = null;
            root.Add(original);
            root.Add(dim);
            root.Add(card);
            host.Content = root;

            return new BusyNote(textLabel, () =>
            {
                host.Content = null;
                root.Clear();
                host.Content = original;
            });
        }

        /// <summary>Hláška s jedním tlačítkem.</summary>
        public static Task AlertAsync(Page? page, string title, string? message, string ok = "OK") =>
            ShowAsync(page, title, message, (ok, PencilVariant.Primary));

        /// <summary>Ano/ne. danger = potvrzovací tlačítko červenou pastelkou.</summary>
        public static async Task<bool> ConfirmAsync(Page? page, string title, string? message,
            string yes, string no = "Nic", bool danger = false) =>
            await ShowAsync(page, title, message,
                (yes, danger ? PencilVariant.Danger : PencilVariant.Primary),
                (no, PencilVariant.Secondary)) == yes;

        /// <summary>
        /// Obecný dialog. Vrací text zmáčknutého tlačítka, null = ťuknutí mimo lístek.
        /// </summary>
        public static async Task<string?> ShowAsync(Page? page, string title, string? message,
            params (string Text, PencilVariant Variant)[] buttons)
        {
            var host = page as ContentPage ?? Shell.Current?.CurrentPage as ContentPage;
            if (host?.Content is not View original)
            {
                // nouzovka: nativní dialog
                var p = page ?? Application.Current?.MainPage;
                if (p == null) return null;
                if (buttons.Length <= 1)
                {
                    await p.DisplayAlert(title, message ?? "", buttons.FirstOrDefault().Text ?? "OK");
                    return buttons.FirstOrDefault().Text;
                }
                return await p.DisplayActionSheet(title, null, null, buttons.Select(b => b.Text).ToArray());
            }

            var tcs = new TaskCompletionSource<string?>();
            _active = tcs;

            // ztmavení stolu
            var dim = new BoxView { Color = Colors.Black, Opacity = 0 };
            var dimTap = new TapGestureRecognizer();
            dimTap.Tapped += (_, _) => tcs.TrySetResult(null);
            dim.GestureRecognizers.Add(dimTap);

            // obsah lístku
            var stack = new VerticalStackLayout { Spacing = 10 };
            var titleLabel = new Label
            {
                Text = title,
                HorizontalOptions = LayoutOptions.Fill,
                HorizontalTextAlignment = TextAlignment.Center
            };
            titleLabel.SetDynamicResource(Label.FontSizeProperty, "FontHeading");
            stack.Add(titleLabel);

            if (!string.IsNullOrWhiteSpace(message))
            {
                var messageLabel = new Label
                {
                    Text = message,
                    HorizontalOptions = LayoutOptions.Fill,
                    HorizontalTextAlignment = TextAlignment.Center,
                    Margin = new Thickness(0, 0, 0, 6)
                };
                messageLabel.SetDynamicResource(Label.FontSizeProperty, "FontBody");
                stack.Add(messageLabel);
            }
            foreach (var (text, variant) in buttons)
            {
                var b = new PencilButton { Text = text, Variant = variant };
                b.Clicked += (_, _) => tcs.TrySetResult(text);
                stack.Add(b);
            }

            // papír: lehce pootočený, se stínem jako na stole
            var card = new Border
            {
                BackgroundColor = Paper,
                Stroke = Ink.WithAlpha(0.35f),
                StrokeThickness = 1,
                StrokeShape = new RoundRectangle { CornerRadius = 2 },
                Padding = new Thickness(22, 18),
                Margin = new Thickness(32),
                MaximumWidthRequest = 420,
                HorizontalOptions = LayoutOptions.Center,
                VerticalOptions = LayoutOptions.Center,
                Rotation = -1.5,
                Opacity = 0,
                Scale = 0.9,
                Shadow = new Shadow { Brush = Colors.Black, Offset = new Point(4, 6), Radius = 12, Opacity = 0.6f },
                Content = stack
            };
            // ťuknutí na lístek samotný ho nezavře
            card.GestureRecognizers.Add(new TapGestureRecognizer());

            var overlay = new Grid();
            overlay.Add(dim);
            overlay.Add(card);

            var root = new Grid();
            host.Content = null;          // odpojit původní obsah…
            root.Add(original);           // …a dát ho pod lístek
            root.Add(overlay);
            host.Content = root;

            await Task.WhenAll(
                dim.FadeTo(0.55, 140),
                card.FadeTo(1, 140),
                card.ScaleTo(1, 160, Easing.CubicOut));

            var result = await tcs.Task;
            if (_active == tcs) _active = null;

            await Task.WhenAll(
                dim.FadeTo(0, 110),
                card.FadeTo(0, 110),
                card.ScaleTo(0.94, 110));

            // vrátit stránku do původního stavu
            host.Content = null;
            root.Clear();
            host.Content = original;

            return result;
        }
    }
}
