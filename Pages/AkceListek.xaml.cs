using Dezolati.Controls;
using Dezolati.Models;
using Dezolati.Services;

namespace Dezolati.Pages;

/// <summary>
/// Akční lístek: jen pivo. Ťuk na řádek = vždy nabídka (zapsat pivo / škrtnout / detail), u řádku echo
/// (kdy naposledy a kdo zapsal). Nahoře bečka, kolik zbývá a „Přeraženo!“. Stav: otevření = 1 čtení,
/// vlastní zápis = stav z odpovědi, ostatní = tichý push (Changed), ↻ ručně.
/// Parametry: id, invite=1 (hned po založení nabídnout pozvání).
/// </summary>
public partial class AkceListek : ContentPage, IQueryAttributable
{
    private string? id;
    private bool offerInvite;
    private AkceInfo? akce;
    private bool busy;
    private readonly AkceDecorHost decor;

    public AkceListek()
    {
        InitializeComponent();
        decor = new AkceDecorHost(this, Fx);
        decor.ImagesChanged += key => { if (id != null && key.StartsWith(id + "/")) Render(); };
    }

    private bool visible;

    public void ApplyQueryAttributes(IDictionary<string, object> query)
    {
        var old = id;
        if (query.TryGetValue("id", out var v) && v is string s) id = s;
        offerInvite = query.TryGetValue("invite", out var i) && i as string == "1";
        query.Clear();
        // Shell občas pošle parametry až PO OnAppearing → stránka by zůstala prázdná / se starou akcí
        if (visible && id != old) Dispatcher.Dispatch(async () => await Show());
    }

    protected override async void OnAppearing()
    {
        base.OnAppearing();
        visible = true;
        AkceService.Changed -= OnChanged;
        AkceService.Changed += OnChanged;
        decor.Start();
        await Show();
    }

    private async Task Show()
    {
        if (id == null) return;
        Body.Clear();
        akce = AkceService.Cached(id);
        if (akce != null) Render();
        else Body.Add(Ui.Small("Načítám…", soft: true));
        await Load();
        if (offerInvite && akce != null)
        {
            offerInvite = false;
            await Invite();
        }
    }

    protected override void OnDisappearing()
    {
        base.OnDisappearing();
        visible = false;
        AkceService.Changed -= OnChanged;
        decor.Stop();
    }

    private void OnChanged(string changedId)
    {
        if (changedId != id) return;
        MainThread.BeginInvokeOnMainThread(() =>
        {
            akce = AkceService.Cached(changedId);
            if (akce == null) { _ = Shell.Current.GoToAsync("//Akce"); return; }
            Render();
        });
    }

    private async Task Load()
    {
        if (id == null) return;
        try
        {
            akce = await AkceService.GetAsync(id);
            if (akce == null)
            {
                await Listek.AlertAsync(this, "Akce", "Tahle akce už není (nebo tě z ní vyškrtli).");
                await Shell.Current.GoToAsync("//Akce");
                return;
            }
            Render();
            _ = AkceService.LoadMediaAsync(akce);
        }
        catch (Exception ex)
        {
            if (akce == null) { Body.Clear(); Body.Add(Ui.Title("Akce")); }
            Body.Add(Ui.Small(Net.IsNetworkError(ex) ? "Bez signálu ukazuju, co znám z minula." : $"Nepodařilo se načíst: {ex.Message}", soft: true));
        }
        finally
        {
            Refresh.IsRefreshing = false;
        }
    }

    private void Render()
    {
        if (akce is not { } a) return;
        Body.Clear();
        decor.Look = AkceScene.Of(a);
        if (decor.Header is { } head) Body.Add(head);
        Body.Add(Ui.Title(a.Name));
        Body.Add(Ui.Small(a.Place.Length > 0 ? $"{a.When} · {a.Place}" : a.When, soft: true));

        if (!a.Going)
        {
            Body.Add(Ui.Body(a.MyStatus == "no" ? "Napsal jsi, že nejdeš. Rozmyslel sis to?" : "Jdeš?"));
            Body.Add(Ui.Cols(
                Ui.Btn("Jdu", PencilVariant.Primary, true, async (_, _) => await Respond("yes")),
                Ui.Btn("Možná", PencilVariant.Secondary, true, async (_, _) => await Respond("maybe")),
                Ui.Btn("Nejdu", PencilVariant.Secondary, true, async (_, _) => await Respond("no"))));
        }

        Body.Add(KegBlock(a));

        var total = a.Rows.Sum(r => r.N);
        var h = Ui.H(total > 0 ? $"Piva: {total} ({total * 0.5:0.#} l)" : "Piva");
        var pivoRow = new Grid
        {
            ColumnDefinitions = new ColumnDefinitionCollection(new ColumnDefinition(GridLength.Star), new ColumnDefinition(GridLength.Auto)),
            Margin = new Thickness(0, 10, 0, 0)
        };
        h.VerticalOptions = LayoutOptions.Center;
        pivoRow.Add(h, 0, 0);
        if (decor.Coaster() is { } coaster) pivoRow.Add(coaster, 1, 0);
        Body.Add(pivoRow);
        if (a.Running || a.Over) { }
        else Body.Add(Ui.Small("Začíná " + a.When + ". Zapisovat jde už teď, ale nespěchej.", soft: true));

        var rows = AkceService.RowsFor(a);
        if (rows.Count == 0) Body.Add(Ui.Small("Zatím nikdo. Pozvi lidi.", soft: true));
        foreach (var r in rows) Body.Add(Row(a, r));

        var acts = new VerticalStackLayout { Spacing = 6, Margin = new Thickness(0, 14, 0, 0) };
        acts.Add(Ui.Cols(
            Ui.Btn("Přidat člověka", PencilVariant.Secondary, true, async (_, _) => await AddName()),
            Ui.Btn("Pozvat kámoše", PencilVariant.Secondary, true, async (_, _) => await Invite())));
        acts.Add(Ui.Cols(
            Ui.Btn("Sdílet odkaz", PencilVariant.Secondary, true, async (_, _) => await ShareLink()),
            Ui.Btn("Statistiky", PencilVariant.Secondary, true, async (_, _) =>
                await Shell.Current.GoToAsync("//AkceStats", new Dictionary<string, object> { ["id"] = a.Id }))));
        if (a.Going)
            acts.Add(Ui.Cols(
                Ui.Btn("Upravit", PencilVariant.Secondary, true, async (_, _) =>
                    await Shell.Current.GoToAsync("//AkceEdit", new Dictionary<string, object> { ["id"] = a.Id })),
                Ui.Btn("Historie změn", PencilVariant.Secondary, true, async (_, _) => await History())));
        if (a.Going && a.MyStatus != "no")
            acts.Add(Ui.Btn("Nakonec nejdu", PencilVariant.Outline, true, async (_, _) => await Respond("no")));
        Body.Add(acts);
    }

    // ---------------- bečka ----------------

    private View KegBlock(AkceInfo a)
    {
        var box = new Grid
        {
            ColumnDefinitions = new ColumnDefinitionCollection(new ColumnDefinition(new GridLength(70)), new ColumnDefinition(GridLength.Star)),
            ColumnSpacing = 10,
            Margin = new Thickness(0, 8, 0, 0)
        };
        var k = a.CurrentKeg;
        var fill = k == null ? 0 : (double)k.Left / Math.Max(1, k.Full);
        box.Add(new GraphicsView
        {
            WidthRequest = 64, HeightRequest = 80, InputTransparent = true,
            Drawable = new EventFxDrawable { Scene = (_, _) => EventScene.Keg(fill) }
        }, 0, 0);

        var info = new VerticalStackLayout { Spacing = 2, VerticalOptions = LayoutOptions.Center };
        if (k == null)
        {
            info.Add(Ui.Body(a.Kegs.Count == 0 ? "Bečka žádná" : "Bečka ještě nenaražená"));
            info.Add(Ui.Small(a.Kegs.Count == 0 ? "Pijete lahváče? Až narazíte, dej Přeraženo!" : $"Na řadě: {a.Kegs[0].Label}", soft: true));
        }
        else
        {
            info.Add(Ui.Body($"Bečka {a.Keg + 1}/{Math.Max(a.Kegs.Count, a.Keg + 1)} · {k.Label}"));
            info.Add(Ui.Small($"zbývá ~{k.Left} {Piv(k.Left)} z {k.Full}", soft: true));
            if (a.KegEmptyEstimate() is DateTime est && k.Left > 0) info.Add(Ui.Small($"padne kolem {est:H:mm}", soft: true));
            if (k.Left == 0) info.Add(Ui.Small("Podle čárek je prázdná. Přerazte!", soft: true));
        }
        var buttons = new HorizontalStackLayout { Spacing = 6, Margin = new Thickness(0, 4, 0, 0) };
        if (a.Going)
        {
            var tap = Ui.Btn("Přeraženo!", PencilVariant.Primary, true, async (_, _) => await Tap());
            tap.WidthRequest = 140;
            buttons.Add(tap);
        }
        var refresh = Ui.Btn("↻", PencilVariant.Secondary, true, async (_, _) => { await Load(); });
        refresh.WidthRequest = 54;
        buttons.Add(refresh);
        info.Add(buttons);
        box.Add(info, 1, 0);
        return box;
    }

    private async Task Tap()
    {
        if (akce is not { } a || busy) return;
        var untapped = a.Kegs.Select((k, i) => (k, i)).Where(x => x.k.TappedAt == null).ToList();
        var options = untapped.Select(x => ($"{x.i + 1}. {x.k.Label}", PencilVariant.Secondary)).ToList();
        options.Add(("Jiná…", PencilVariant.Secondary));
        var pick = await Listek.ShowAsync(this, "Přeraženo!", "Kterou bečku narážíte? Počítá se vždycky plná.", options.ToArray());
        if (pick == null) return;
        int? keg = null; string? custom = null; double liters = 30;
        if (pick == "Jiná…")
        {
            custom = await Listek.PromptAsync(this, "Jiná bečka", "Co to je za pivo?", "Dál", "Plzeň", 40);
            if (string.IsNullOrWhiteSpace(custom)) return;
            var l = await Listek.ShowAsync(this, custom.Trim(), "Kolik litrů?",
                ("10 l", PencilVariant.Secondary), ("15 l", PencilVariant.Secondary), ("20 l", PencilVariant.Secondary),
                ("30 l", PencilVariant.Primary), ("50 l", PencilVariant.Secondary));
            if (l == null) return;
            liters = double.Parse(l.Split(' ')[0]);
            custom = custom.Trim();
        }
        else keg = untapped[options.FindIndex(o => o.Item1 == pick)].i;
        await Run(() => AkceService.TapAsync(a.Id, keg, custom, liters));
    }

    // ---------------- řádky ----------------

    private View Row(AkceInfo a, AkceRow r)
    {
        var row = new Grid
        {
            ColumnDefinitions = new ColumnDefinitionCollection(new ColumnDefinition(GridLength.Star), new ColumnDefinition(GridLength.Auto)),
            RowDefinitions = new RowDefinitionCollection(new RowDefinition(GridLength.Auto), new RowDefinition(GridLength.Auto), new RowDefinition(GridLength.Auto)),
            Padding = new Thickness(0, 6),
            BackgroundColor = Colors.Transparent
        };
        var who = Ui.Body(r.Key == AuthSession.Uid ? $"{r.Name} (já)" : a.Guests.Contains(r.Key) ? $"{r.Name} (z odkazu)" : r.Name);
        row.Add(who, 0, 0);
        var n = Ui.H(r.N.ToString());
        n.HorizontalOptions = LayoutOptions.End;
        row.Add(n, 1, 0);
        if (r.N > 0)
        {
            const double w = 220;
            var carky = new GraphicsView
            {
                WidthRequest = w, HeightRequest = CarkyDrawable.HeightFor(r.N, w), HorizontalOptions = LayoutOptions.Start,
                InputTransparent = true, Drawable = new CarkyDrawable { Count = r.N, Day = a.From.Date }
            };
            row.Add(carky, 0, 1);
            Grid.SetColumnSpan(carky, 2);
        }
        var echo = Ui.Small(AkceService.Echo(r), soft: true);
        row.Add(echo, 0, 2);
        Grid.SetColumnSpan(echo, 2);
        row.Tap(() => _ = RowMenu(a, r));
        return row;
    }

    private async Task RowMenu(AkceInfo a, AkceRow r)
    {
        if (busy) return;
        if (!a.Going)
        {
            await Listek.AlertAsync(this, "Akce", "Zapisovat můžou jen ti, co jdou. Nejdřív dej Jdu.");
            return;
        }
        var buttons = new List<(string, PencilVariant)> { ("+1 pivo", PencilVariant.Primary) };
        if (r.N > 0) buttons.Add(("Škrtnout", PencilVariant.Danger));
        buttons.Add(("Detail", PencilVariant.Secondary));
        var pick = await Listek.ShowAsync(this, r.Name, $"{r.N} {Piv(r.N)} · {AkceService.Echo(r)}", buttons.ToArray());
        switch (pick)
        {
            case "+1 pivo":
                if (r.Last is DateTime last && DateTime.Now - last < TimeSpan.FromMinutes(3) &&
                    !await Listek.ConfirmAsync(this, "Zase?", $"{r.Name} má poslední pivo zapsané před {(int)(DateTime.Now - last).TotalMinutes} min{(r.By.Length > 0 ? $" (zapsal {r.By})" : "")}. Fakt další?", "Jo, další"))
                    return;
                await Run(() => AkceService.BeerAsync(a.Id, r.Key, +1));
                break;
            case "Škrtnout":
                if (await Listek.ConfirmAsync(this, "Škrtnout", $"Škrtnout {r.Name} jedno pivo?", "Škrtnout", danger: true))
                    await Run(() => AkceService.BeerAsync(a.Id, r.Key, -1));
                break;
            case "Detail":
                await Detail(a, r);
                break;
        }
    }

    private async Task Detail(AkceInfo a, AkceRow r)
    {
        var note = Listek.ShowBusy(this, r.Name, "Načítám zápisy…");
        try
        {
            var items = await AkceService.LogAsync(a.Id, r.Key);
            note?.Dispose();
            var lines = items.Count == 0 ? "Žádné zápisy."
                : string.Join("\n", items.Take(40).Select(i =>
                    $"{i.At:ddd H:mm}  {(i.Delta > 0 ? "+" : "")}{i.Delta}  · {i.By}{(i.Src == "lístek" ? " (z lístku)" : "")}"));
            await Listek.AlertAsync(this, $"{r.Name}: {r.N} {Piv(r.N)}", lines);
        }
        catch (Exception ex)
        {
            note?.Dispose();
            await Listek.AlertAsync(this, "Nepovedlo se", Net.IsNetworkError(ex) ? "Bez signálu to nepůjde." : ex.Message);
        }
    }

    // ---------------- lidi ----------------

    private async Task Respond(string answer) { if (akce is { } a) await Run(() => AkceService.RespondAsync(a.Id, answer)); }

    private async Task AddName()
    {
        if (akce is not { } a) return;
        var name = await Listek.PromptAsync(this, "Přidat člověka", "Nemá appku ani odkaz – čárky mu píšete vy.", "Přidat", "Jméno", 30);
        if (string.IsNullOrWhiteSpace(name)) return;
        await Run(() => AkceService.AddNameAsync(a.Id, name.Trim()));
    }

    private async Task Invite()
    {
        if (akce is not { } a || AuthSession.Uid is not string me) return;
        List<UserModel> friends;
        try { friends = (await new FirestoreService().GetFriendsOverview(me)).Friends; }
        catch (Exception ex) { await Listek.AlertAsync(this, "Nepovedlo se", Net.IsNetworkError(ex) ? "Bez signálu to nepůjde." : ex.Message); return; }
        var free = friends.Where(f => !a.Status.ContainsKey(f.UserId)).ToList();
        if (free.Count == 0)
        {
            if (await Listek.ConfirmAsync(this, "Pozvat", "Všichni kámoši už jsou pozvaní. Pošli odkaz těm bez appky.", "Sdílet odkaz"))
                await ShareLink();
            return;
        }
        var picked = new HashSet<string>();
        var chips = new FlexLayout { Wrap = Microsoft.Maui.Layouts.FlexWrap.Wrap };
        foreach (var f in free.OrderBy(f => f.Nickname ?? f.Name))
        {
            var b = Ui.Btn(f.Nickname ?? f.Name ?? "?", PencilVariant.Secondary);
            b.Margin = new Thickness(0, 0, 6, 6);
            b.WidthRequest = 130;
            Ui.Chip(b, false);
            var uid = f.UserId;
            b.Clicked += (_, _) => { if (!picked.Remove(uid)) picked.Add(uid); Ui.Chip(b, picked.Contains(uid)); };
            chips.Add(b);
        }
        // výběr přímo na lístku: čipy + Poslat
        var panel = new VerticalStackLayout { Spacing = 6 };
        panel.Add(Ui.H("Koho pozvat?"));
        panel.Add(Ui.Small("Přijde jim push s Jdu / Možná / Nejdu.", soft: true));
        panel.Add(chips);
        var send = Ui.Btn("Poslat pozvánky", PencilVariant.Primary, true);
        var cancel = Ui.Btn("Nic", PencilVariant.Secondary, true);
        panel.Add(Ui.Cols(send, cancel));
        Body.Insert(2, panel);
        var tcs = new TaskCompletionSource<bool>();
        send.Clicked += (_, _) => tcs.TrySetResult(true);
        cancel.Clicked += (_, _) => tcs.TrySetResult(false);
        await Task.Yield();
        var ok = await tcs.Task;
        Body.Remove(panel);
        if (!ok || picked.Count == 0) return;
        await Run(async () =>
        {
            await AkceService.InviteAsync(a.Id, picked);
            await AkceService.GetAsync(a.Id);
        });
        if (await Listek.ConfirmAsync(this, "Pozváno", $"Poslal jsem {picked.Count}× pozvánku. Lidi bez appky pozveš odkazem.", "Sdílet odkaz", "Stačí"))
            await ShareLink();
    }

    private async Task ShareLink()
    {
        if (akce is not { } a) return;
        await Share.Default.RequestAsync(new ShareTextRequest
        {
            Title = a.Name,
            Text = $"{a.Name} – {a.When}{(a.Place.Length > 0 ? $", {a.Place}" : "")}. Dej vědět, jestli jdeš: {AkceService.ShareLink(a)}"
        });
    }

    private async Task History()
    {
        if (akce is not { } a) return;
        if (a.History.Count == 0) { await Listek.AlertAsync(this, "Historie změn", "Zatím nikdo nic neměnil."); return; }
        var items = a.History.Select((h, i) => (h, i)).Reverse().Take(8).ToList();
        var revertable = items.Where(x => x.h.Before != null && Revertable.Contains(x.h.What)).ToList();
        var buttons = revertable
            .Select(x => ($"Vrátit: {Short(x.h.What)}", PencilVariant.Secondary)).ToList();
        buttons.Add(("Zavřít", PencilVariant.Primary));
        var text = string.Join("\n", items.Select(x =>
            $"{(x.h.At is DateTime t ? t.ToString("d. M. H:mm") : "")} {x.h.By}: {x.h.What}{(x.h.Before != null ? $" (předtím {Before(x.h)})" : "")}"));
        var pick = await Listek.ShowAsync(this, "Historie změn", text, buttons.ToArray());
        if (pick == null || pick == "Zavřít") return;
        var chosen = revertable[buttons.FindIndex(b => b.Item1 == pick)];
        if (!await Listek.ConfirmAsync(this, "Vrátit", $"Vrátit „{chosen.h.What}“ zpátky na {Before(chosen.h)}?", "Vrátit")) return;
        await Run(async () => { await AkceService.RevertAsync(a.Id, chosen.i); await AkceService.GetAsync(a.Id); });
    }

    private static readonly HashSet<string> Revertable = new() { "přejmenoval akci", "změnil začátek", "změnil konec", "změnil místo" };

    /// <summary>Začátek/konec jsou v historii v ms.</summary>
    private static string Before(AkceChange h) =>
        h.What is "změnil začátek" or "změnil konec" && long.TryParse(h.Before, out var ms)
            ? DateTimeOffset.FromUnixTimeMilliseconds(ms).LocalDateTime.ToString("d. M. H:mm")
            : string.IsNullOrEmpty(h.Before) ? "(nic)" : h.Before!;

    private static string Short(string s) => s.Length > 26 ? s[..25] + "…" : s;

    private static string Piv(int n) => n == 1 ? "pivo" : n is >= 2 and <= 4 ? "piva" : "piv";

    private async Task Run(Func<Task> action)
    {
        if (busy) return;
        busy = true;
        try
        {
            await action();
            if (id != null) akce = AkceService.Cached(id);
            Render();
        }
        catch (Exception ex)
        {
            await Listek.AlertAsync(this, "Nepovedlo se", Net.IsNetworkError(ex) ? "Bez signálu to nepůjde." : ex.Message);
        }
        finally
        {
            busy = false;
        }
    }

    private async void OnRefreshing(object? sender, EventArgs e) => await Load();

    private void OnZpetClicked(object sender, EventArgs e) => OnBackButtonPressed();

    protected override bool OnBackButtonPressed()
    {
        if (Listek.DismissActive()) return true;
        Dispatcher.Dispatch(async () => await Shell.Current.GoToAsync("//Akce"));
        return true;
    }
}
