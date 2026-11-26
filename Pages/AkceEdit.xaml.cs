using System.Globalization;
using Dezolati.Controls;
using Dezolati.Services;

namespace Dezolati.Pages;

/// <summary>
/// Založení / úprava akce: název, od–do (i víc dní), místo (špendlík = kde teď jsem + text), bečky v pořadí naražení,
/// výzdoba (hotový motiv). Upravovat smí každý, kdo jde (server si pamatuje kdo a co bylo předtím), smazat jen hostitel.
/// Parametr navigace: id (žádný = nová akce).
/// </summary>
public partial class AkceEdit : ContentPage, IQueryAttributable
{
    private string? id;
    private AkceInfo? akce;
    private readonly Entry name = new() { Placeholder = "Název (třeba Mikuláš u Pepy)", MaxLength = 60 };
    // datum a čas: vlastní políčka s výběrem na lístku (DatePicker měl v tmavém režimu bílé písmo a systémový kalendář)
    private DateTime from, to;
    private readonly PickField fromDate = new(), fromTime = new(), toDate = new(), toTime = new();
    private readonly Entry place = new() { Placeholder = "Místo (u Pepy, Komenského 12)", MaxLength = 120 };
    private readonly Label pinLabel = Ui.Small("", soft: true);
    private double? lat, lon;
    private readonly List<AkceKeg> kegs = new();
    private readonly VerticalStackLayout kegList = new() { Spacing = 4 };
    private readonly Entry kegName = new() { Placeholder = "Pivo (Plzeň, Kozel…)", MaxLength = 40 };
    private double kegLiters = 30;
    private readonly List<(PencilButton B, double L)> literChips = new();
    private string preset = "mikulas";
    private readonly List<(PencilButton B, string Key)> presetChips = new();
    private Dictionary<string, string> slots = AkceTheme.SlotsOf(null);
    private readonly HashSet<string> pending = new();   // obrázky vybrané u nové akce, nahrají se po založení
    private readonly VerticalStackLayout slotBox = new() { Spacing = 4 };
    private readonly GraphicsView preview = new() { HeightRequest = AkceScene.HeaderH, InputTransparent = true };
    private readonly GraphicsView previewCoaster = new() { WidthRequest = 56, HeightRequest = 56, InputTransparent = true, HorizontalOptions = LayoutOptions.Start };
    private bool built;

    public AkceEdit()
    {
        InitializeComponent();
        fromDate.Tapped += async () => { if (await PencilPickers.PickDateAsync(this, "Začátek", from) is { } d) { from = d.Date + from.TimeOfDay; ShowTimes(); } };
        fromTime.Tapped += async () => { if (await PencilPickers.PickTimeAsync(this, "Začátek", from.TimeOfDay) is { } t) { from = from.Date + t; ShowTimes(); } };
        toDate.Tapped += async () => { if (await PencilPickers.PickDateAsync(this, "Konec", to) is { } d) { to = d.Date + to.TimeOfDay; ShowTimes(); } };
        toTime.Tapped += async () => { if (await PencilPickers.PickTimeAsync(this, "Konec", to.TimeOfDay) is { } t) { to = to.Date + t; ShowTimes(); } };
        // pozor: RefreshView.IsEnabled = false vypne i všechno uvnitř (nešlo nic zmáčknout) → jen hned zastavit
        AkceImages.Init();
        preview.Drawable = new EventFxDrawable { Scene = (r, _) => AkceScene.Header(r.Width, PreviewLook, 0) };
        previewCoaster.Drawable = new EventFxDrawable { Scene = (_, _) => new() { new SGroup(0, 0, 0, null, AkceScene.Coaster(PreviewLook), 56 / 64.0) } };
        AkceImages.Changed += _ => { preview.Invalidate(); previewCoaster.Invalidate(); };
    }

    private string ImgKey(string slot) => $"{akce?.Id ?? "nova"}/{slot}";

    private AkceScene.Look PreviewLook =>
        AkceScene.Of(AkceTheme.Build(preset, slots, akce?.Theme), akce?.Id ?? "nova",
            AkceTheme.Slots.Where(s => AkceService.MediaOf(ImgKey(s)) != null).ToHashSet());

    private bool visible;

    public void ApplyQueryAttributes(IDictionary<string, object> query)
    {
        // návrat z mapy: jen převzít místo, rozepsanou akci nechat
        if (query.TryGetValue("picked", out var pk))
        {
            query.Clear();
            if (pk as string == "1" && MapPick.Take() is { } r) { (lat, lon) = (r.Lat, r.Lon); PinText(); }
            return;
        }
        id = query.TryGetValue("id", out var v) ? v as string : null;
        query.Clear();
        built = false;
        if (visible) Dispatcher.Dispatch(Rebuild);   // parametry dorazily až po OnAppearing
    }

    protected override void OnDisappearing()
    {
        base.OnDisappearing();
        visible = false;
    }

    protected override void OnAppearing()
    {
        base.OnAppearing();
        visible = true;
        Rebuild();
    }

    private void Rebuild()
    {
        if (built) return;
        built = true;
        akce = id != null ? AkceService.Cached(id) : null;
        Fill();
        Build();
    }

    private void Fill()
    {
        kegs.Clear();
        if (akce != null)
        {
            name.Text = akce.Name;
            (from, to) = (akce.From, akce.To);
            place.Text = akce.Place;
            (lat, lon) = (akce.Lat, akce.Lon);
            kegs.AddRange(akce.Kegs.Where(k => k.TappedAt == null).Select(k => new AkceKeg { Name = k.Name, Liters = k.Liters }));
            preset = akce.Theme.TryGetValue("preset", out var p) ? p : "mikulas";
            slots = AkceTheme.SlotsOf(akce.Theme);
            _ = AkceService.LoadMediaAsync(akce);
        }
        else
        {
            name.Text = "";
            (from, to) = (DateTime.Today.AddHours(18), DateTime.Today.AddDays(1).AddHours(6));
            place.Text = "";
            (lat, lon) = (null, null);
            preset = DateTime.Today.Month == 12 && DateTime.Today.Day <= 6 ? "mikulas" : DateTime.Today.Month == 12 ? "vanoce" : "party";
            slots = AkceTheme.SlotsOf(new() { ["preset"] = preset });
            pending.Clear();
            if (LastLocation.Get(TimeSpan.FromMinutes(30)) is { } me) (lat, lon) = (me.Lat, me.Lon);
        }
        PinText();
        ShowTimes();
    }

    private void ShowTimes()
    {
        fromDate.Text = PencilPickers.DateText(from);
        fromTime.Text = PencilPickers.TimeText(from.TimeOfDay);
        toDate.Text = PencilPickers.DateText(to);
        toTime.Text = PencilPickers.TimeText(to.TimeOfDay);
    }

    private void PinText() =>
        pinLabel.Text = lat is double la && lon is double lo
            ? $"📍 špendlík nastavený ({la.ToString("0.0000", CultureInfo.InvariantCulture)}, {lo.ToString("0.0000", CultureInfo.InvariantCulture)}) – kdo zapíše do 100 m, pije na akci "
            : "📍 špendlík zatím není – bez něj se piva z běžného lístku na akci nepočítají ";

    private void Build()
    {
        Body.Clear();
        // pole se při dalším otevření používají znovu – musí se odpojit od starých řádků, jinak Android spadne
        // („The specified child already has a parent“)
        foreach (var v in new View[] { fromDate, fromTime, toDate, toTime, preview, previewCoaster })
            if (v.Parent is Layout l) l.Remove(v);
        Body.Add(Ui.Title(akce == null ? "Nová akce" : "Upravit akci"));
        Body.Add(name);

        Body.Add(Ui.Field("Od:"));
        Body.Add(Ui.Cols(fromDate, fromTime));
        Body.Add(Ui.Field("Do:"));
        Body.Add(Ui.Cols(toDate, toTime));
        Body.Add(Ui.Small("Může trvat i víc dní (nejvýš týden). Výsledky přijdou po konci.", soft: true));

        Body.Add(Ui.Field("Kde:"));
        Body.Add(place);
        Body.Add(pinLabel);
        Body.Add(Ui.Btn(lat == null ? "Vybrat místo na mapě" : "Posunout špendlík na mapě", PencilVariant.Secondary, true, async (_, _) =>
        {
            var start = lat is double la && lon is double lo ? (la, lo) : LastLocation.Get(TimeSpan.FromHours(2)) ?? HomePlace.Location;
            MapPick.Begin(start?.Item1, start?.Item2, "Posuň mapu, ať je křížek tam, kde bude akce", "Tady to bude", "//AkceEdit");
            await Shell.Current.GoToAsync("//Hospody", new Dictionary<string, object> { ["mode"] = "pick" });
        }));

        Body.Add(Ui.Field("Bečky (v pořadí, jak se budou narážet):"));
        Body.Add(kegList);
        RenderKegs();
        Body.Add(kegName);
        var chips = new FlexLayout { Wrap = Microsoft.Maui.Layouts.FlexWrap.Wrap };
        literChips.Clear();
        foreach (var l in new[] { 10.0, 15, 20, 30, 50 })
        {
            var b = Ui.Btn($"{l:0} l", PencilVariant.Secondary);
            b.Margin = new Thickness(0, 0, 6, 6);
            b.WidthRequest = 72;
            var liters = l;
            b.Clicked += (_, _) => { kegLiters = liters; PaintLiters(); };
            literChips.Add((b, l));
            chips.Add(b);
        }
        PaintLiters();
        Body.Add(chips);
        Body.Add(Ui.Btn("Přidat bečku", PencilVariant.Secondary, true, (_, _) =>
        {
            kegs.Add(new AkceKeg { Name = string.IsNullOrWhiteSpace(kegName.Text) ? "Bečka" : kegName.Text.Trim(), Liters = kegLiters });
            RenderKegs();
        }));

        Body.Add(Ui.Field("Výzdoba:"));
        var presets = new FlexLayout { Wrap = Microsoft.Maui.Layouts.FlexWrap.Wrap };
        presetChips.Clear();
        foreach (var (key, label) in AkceTheme.Presets)
        {
            var b = Ui.Btn(label, PencilVariant.Secondary);
            b.Margin = new Thickness(0, 0, 6, 6);
            b.WidthRequest = 140;
            var k = key;
            b.Clicked += (_, _) =>
            {
                preset = k;
                foreach (var s in AkceTheme.Slots)
                    if (k == "none") slots[s] = "none";
                    else if (slots[s] == "none") slots[s] = "preset";
                PaintPresets();
                RenderSlots();
            };
            presetChips.Add((b, key));
            presets.Add(b);
        }
        PaintPresets();
        Body.Add(presets);
        Body.Add(slotBox);
        RenderSlots();

        var save = Ui.Btn(akce == null ? "Založit akci" : "Uložit změny", PencilVariant.Primary, false, async (_, _) => await Save());
        save.Margin = new Thickness(0, 14, 0, 0);
        Body.Add(save);
        if (akce?.IsHost == true)
            Body.Add(Ui.Btn("Smazat akci", PencilVariant.Danger, true, async (_, _) => await Delete()));
    }

    /// <summary>Místa výzdoby: motiv / obrázek / nic, u obrázku výběr z telefonu; náhled.</summary>
    private void RenderSlots()
    {
        slotBox.Clear();
        preview.Invalidate();
        previewCoaster.Invalidate();
        if (preset == "none") return;
        slotBox.Add(Ui.Small("Každé místo může mít motiv, vlastní obrázek (třeba hlavu kámoše), nebo nic.", soft: true));
        foreach (var (slot, label) in AkceTheme.SlotLabels)
        {
            var sl = slot;
            var row = new Grid { ColumnSpacing = 6 };
            row.ColumnDefinitions.Add(new ColumnDefinition(new GridLength(70)));
            row.Add(Ui.Body(label), 0, 0);
            var col = 1;
            foreach (var (mode, text) in new[] { ("preset", "Motiv"), ("img", "Obrázek"), ("none", "Nic") })
            {
                var m = mode;
                row.ColumnDefinitions.Add(new ColumnDefinition(GridLength.Star));
                var b = Ui.Btn(text, PencilVariant.Secondary, true, (_, _) => { slots[sl] = m; RenderSlots(); });
                Ui.Chip(b, slots[sl] == m);
                row.Add(b, col++, 0);
            }
            slotBox.Add(row);
            if (slots[sl] == "img")
            {
                var has = AkceService.MediaOf(ImgKey(sl)) != null;
                var pick = Ui.Btn(has ? "Jiný obrázek" : "Vybrat obrázek", PencilVariant.Secondary, true, async (_, _) => await Pick(sl));
                pick.Margin = new Thickness(76, 0, 0, 4);
                slotBox.Add(pick);
            }
        }
        slotBox.Add(Ui.Small("Náhled:", soft: true));
        slotBox.Add(preview);
        slotBox.Add(previewCoaster);
    }

    private async Task Pick(string slot)
    {
        try
        {
            var data = await AkceImages.PickAsync();
            if (data == null) return;
            if (data.Length > 250_000) { await Listek.AlertAsync(this, "Obrázek", "I po zmenšení je moc velký. Zkus jiný."); return; }
            if (akce != null)
            {
                var busy = Listek.ShowBusy(this, "Obrázek", "Nahrávám…");
                try { await AkceService.UploadMediaAsync(akce.Id, slot, data); }
                finally { busy?.Dispose(); }
                akce = AkceService.Cached(akce.Id) ?? akce;
            }
            else
            {
                AkceService.SetLocalMedia(ImgKey(slot), data);
                pending.Add(slot);
            }
            RenderSlots();
        }
        catch (Exception ex)
        {
            await Listek.AlertAsync(this, "Obrázek se nepovedl", Net.IsNetworkError(ex) ? "Bez signálu to nepůjde." : ex.Message);
        }
    }

    private void PaintLiters() { foreach (var (b, l) in literChips) Ui.Chip(b, l == kegLiters); }
    private void PaintPresets() { foreach (var (b, k) in presetChips) Ui.Chip(b, k == preset); }

    private void RenderKegs()
    {
        kegList.Clear();
        if (kegs.Count == 0) kegList.Add(Ui.Small("Zatím žádná. Bez bečky se jen nepočítá, kolik zbývá.", soft: true));
        for (var i = 0; i < kegs.Count; i++)
        {
            var k = kegs[i];
            var idx = i;
            var row = new Grid { ColumnDefinitions = new ColumnDefinitionCollection(new ColumnDefinition(GridLength.Star), new ColumnDefinition(GridLength.Auto)) };
            row.Add(Ui.Body($"{i + 1}. {k.Label} ({k.Full} piv)"), 0, 0);
            var del = Ui.Btn("✖", PencilVariant.Secondary, true, (_, _) => { kegs.RemoveAt(idx); RenderKegs(); });
            del.WidthRequest = 54;
            row.Add(del, 1, 0);
            kegList.Add(row);
        }
    }

    private DateTime From => from;
    private DateTime To => to;

    private async Task Save()
    {
        var n = name.Text?.Trim() ?? "";
        if (n.Length == 0) { await Listek.AlertAsync(this, "Akce", "Dej akci název."); return; }
        if (To <= From || (To - From).TotalDays > 7) { await Listek.AlertAsync(this, "Akce", "Konec musí být po začátku a nejvýš za týden."); return; }
        var busy = Listek.ShowBusy(this, "Akce", "Ukládám…");
        try
        {
            var theme = AkceTheme.Build(preset, slots, akce?.Theme);
            if (akce == null)
            {
                var (newId, _) = await AkceService.CreateAsync(n, From, To, lat, lon, place.Text?.Trim() ?? "", kegs, theme);
                foreach (var slot in pending)
                    if (AkceService.MediaOf($"nova/{slot}") is { } m)
                        try { await AkceService.UploadMediaAsync(newId, slot, m.Data, m.Mime); } catch { /* zůstane motiv */ }
                pending.Clear();
                busy?.Dispose();
                await Shell.Current.GoToAsync("//AkceListek", new Dictionary<string, object> { ["id"] = newId, ["invite"] = "1" });
            }
            else
            {
                await AkceService.UpdateAsync(akce.Id, n, From, To, place.Text?.Trim() ?? "", lat, lon, kegs, theme);
                busy?.Dispose();
                await Shell.Current.GoToAsync("//AkceListek", new Dictionary<string, object> { ["id"] = akce.Id });
            }
        }
        catch (Exception ex)
        {
            busy?.Dispose();
            await Listek.AlertAsync(this, "Nepovedlo se", Net.IsNetworkError(ex) ? "Bez signálu to nepůjde." : ex.Message);
        }
    }

    private async Task Delete()
    {
        if (akce == null) return;
        if (!await Listek.ConfirmAsync(this, "Smazat akci", $"{akce.Name} zmizí všem pozvaným. Zápisy na lístcích zůstanou.", "Smazat", danger: true)) return;
        try
        {
            await AkceService.DeleteAsync(akce.Id);
            await Shell.Current.GoToAsync("//Akce");
        }
        catch (Exception ex)
        {
            await Listek.AlertAsync(this, "Nepovedlo se", ex.Message);
        }
    }

    private void OnRefreshing(object? sender, EventArgs e) => Refresh.IsRefreshing = false;

    private void OnZpetClicked(object sender, EventArgs e) => OnBackButtonPressed();

    protected override bool OnBackButtonPressed()
    {
        if (Listek.DismissActive()) return true;
        Dispatcher.Dispatch(async () => await Shell.Current.GoToAsync(akce != null ? "//AkceListek" : "//Akce",
            akce != null ? new Dictionary<string, object> { ["id"] = akce.Id } : new Dictionary<string, object>()));
        return true;
    }
}
