using System.Globalization;

namespace Dezolati.Controls;

/// <summary>
/// Políčko „jako Entry“ (Caveat, inkoust, linka), které po ťuknutí otevře výběr na lístku –
/// místo DatePickeru/TimePickeru (bílé písmo v tmavém režimu a systémový kalendář mimo styl appky).
/// </summary>
public sealed class PickField : VerticalStackLayout
{
    private readonly Label label = new() { FontFamily = "Caveat", TextColor = Color.FromArgb("#1E1E2A"), Margin = new Thickness(4, 6, 0, 2) };

    public PickField()
    {
        Spacing = 0;
        label.SetDynamicResource(Label.FontSizeProperty, "FontBody");
        Add(label);
        Add(new BoxView { HeightRequest = 1, Color = Color.FromArgb("#C9B98F") });
        var tap = new TapGestureRecognizer();
        tap.Tapped += (_, _) => Tapped?.Invoke();
        GestureRecognizers.Add(tap);
    }

    public string Text { set => label.Text = value + " "; }
    public event Action? Tapped;
}

public static class PencilPickers
{
    private static readonly CultureInfo Cs = new("cs-CZ");
    private static readonly Color InkC = Color.FromArgb("#1E1E2A"), Pencil = Color.FromArgb("#6B3E1E"), Faint = Color.FromArgb("#9A8F78");

    public static string DateText(DateTime d) => $"{d.ToString("ddd", Cs)} {d.Day}. {d.Month}. {d.Year}";
    public static string TimeText(TimeSpan t) => $"{(int)t.TotalHours}:{t.Minutes:00}";

    private static Border Cell(string text, bool selected, bool dim, Action tap, double size = 36)
    {
        var l = new Label
        {
            Text = text, FontFamily = selected ? "CaveatBold" : "Caveat", FontSize = 20,
            TextColor = selected ? Color.FromArgb("#F5F5DC") : dim ? Faint : InkC,
            HorizontalTextAlignment = TextAlignment.Center, VerticalTextAlignment = TextAlignment.Center
        };
        var b = new Border
        {
            Content = l, HeightRequest = size, StrokeThickness = 0, Padding = 0,
            BackgroundColor = selected ? Pencil : Colors.Transparent,
            StrokeShape = new Microsoft.Maui.Controls.Shapes.RoundRectangle { CornerRadius = size / 2 }
        };
        var g = new TapGestureRecognizer();
        g.Tapped += (_, _) => tap();
        b.GestureRecognizers.Add(g);
        return b;
    }

    /// <summary>Kalendář na lístku; null = nic.</summary>
    public static async Task<DateTime?> PickDateAsync(Page page, string title, DateTime initial)
    {
        var selected = initial.Date;
        var month = new DateTime(selected.Year, selected.Month, 1);
        var box = new VerticalStackLayout { Spacing = 4 };

        void Render()
        {
            box.Clear();
            var head = new Grid { ColumnDefinitions = new ColumnDefinitionCollection(new ColumnDefinition(new GridLength(44)), new ColumnDefinition(GridLength.Star), new ColumnDefinition(new GridLength(44))) };
            head.Add(Cell("‹", false, false, () => { month = month.AddMonths(-1); Render(); }), 0, 0);
            var name = Cs.DateTimeFormat.GetMonthName(month.Month);
            head.Add(new Label { Text = $"{char.ToUpper(name[0])}{name[1..]} {month.Year} ", FontFamily = "CaveatBold", FontSize = 22, TextColor = InkC, HorizontalTextAlignment = TextAlignment.Center, VerticalTextAlignment = TextAlignment.Center }, 1, 0);
            head.Add(Cell("›", false, false, () => { month = month.AddMonths(1); Render(); }), 2, 0);
            box.Add(head);

            var grid = new Grid { ColumnSpacing = 2, RowSpacing = 2 };
            for (var c = 0; c < 7; c++) grid.ColumnDefinitions.Add(new ColumnDefinition(GridLength.Star));
            string[] days = { "Po", "Út", "St", "Čt", "Pá", "So", "Ne" };
            grid.RowDefinitions.Add(new RowDefinition(GridLength.Auto));
            for (var c = 0; c < 7; c++)
                grid.Add(new Label { Text = days[c] + " ", FontFamily = "Caveat", FontSize = 16, TextColor = Faint, HorizontalTextAlignment = TextAlignment.Center }, c, 0);
            var offset = ((int)month.DayOfWeek + 6) % 7;
            var count = DateTime.DaysInMonth(month.Year, month.Month);
            for (var d = 1; d <= count; d++)
            {
                var pos = offset + d - 1;
                var row = pos / 7 + 1;
                while (grid.RowDefinitions.Count <= row) grid.RowDefinitions.Add(new RowDefinition(GridLength.Auto));
                var date = new DateTime(month.Year, month.Month, d);
                grid.Add(Cell(d.ToString(), date == selected, date < DateTime.Today, () => { selected = date; Render(); }), pos % 7, row);
            }
            box.Add(grid);
            box.Add(new Label { Text = DateText(selected) + " ", FontFamily = "CaveatBold", FontSize = 20, TextColor = Pencil, HorizontalTextAlignment = TextAlignment.Center });
        }
        Render();
        var r = await Listek.ShowCustomAsync(page, title, null, box, ("Vybrat", PencilVariant.Primary), ("Nic", PencilVariant.Secondary));
        return r == "Vybrat" ? selected : null;
    }

    /// <summary>Hodina a minuty po pěti na lístku; null = nic.</summary>
    public static async Task<TimeSpan?> PickTimeAsync(Page page, string title, TimeSpan initial)
    {
        int hour = initial.Hours, minute = initial.Minutes / 5 * 5;
        var box = new VerticalStackLayout { Spacing = 6 };

        void Render()
        {
            box.Clear();
            box.Add(new Label { Text = "Hodina ", FontFamily = "Caveat", FontSize = 17, TextColor = Faint });
            var hg = new Grid { ColumnSpacing = 2, RowSpacing = 2 };
            for (var c = 0; c < 6; c++) hg.ColumnDefinitions.Add(new ColumnDefinition(GridLength.Star));
            for (var h = 0; h < 24; h++)
            {
                if (h % 6 == 0) hg.RowDefinitions.Add(new RowDefinition(GridLength.Auto));
                var hh = h;
                hg.Add(Cell(h.ToString(), h == hour, false, () => { hour = hh; Render(); }), h % 6, h / 6);
            }
            box.Add(hg);
            box.Add(new Label { Text = "Minuty ", FontFamily = "Caveat", FontSize = 17, TextColor = Faint });
            var mg = new Grid { ColumnSpacing = 2, RowSpacing = 2 };
            for (var c = 0; c < 6; c++) mg.ColumnDefinitions.Add(new ColumnDefinition(GridLength.Star));
            for (var i = 0; i < 12; i++)
            {
                if (i % 6 == 0) mg.RowDefinitions.Add(new RowDefinition(GridLength.Auto));
                var mm = i * 5;
                mg.Add(Cell($"{mm:00}", mm == minute, false, () => { minute = mm; Render(); }), i % 6, i / 6);
            }
            box.Add(mg);
            box.Add(new Label { Text = $"{hour}:{minute:00} ", FontFamily = "CaveatBold", FontSize = 22, TextColor = Pencil, HorizontalTextAlignment = TextAlignment.Center });
        }
        Render();
        var r = await Listek.ShowCustomAsync(page, title, null, box, ("Vybrat", PencilVariant.Primary), ("Nic", PencilVariant.Secondary));
        return r == "Vybrat" ? new TimeSpan(hour, minute, 0) : null;
    }
}
