using Dezolati.Controls;
using Dezolati.Models;
using Dezolati.Services;

namespace Dezolati.Pages;

/// <summary>
/// Pozvánky do hospody: nová pozvánka (z mapy / profilu hospody přes <see cref="Pozvani.DraftPub"/>) – nejdřív
/// nabídne „Přidat se“ ke kámošově pozvánce do stejné hospody, pak výběr kámošů. Jinak seznam mých pozvánek:
/// kdo jde / nejde / čeká, Jdu / Nejdu, Pozvat další, po skončení „Kdo lil nejvíc?“. Logika sdílená s webem.
/// </summary>
public partial class Pozvanky : ContentPage
{
    private PubInfo? draft;
    private List<JoinableInvite> joinable = new();
    private List<UserModel> friends = new();
    private readonly HashSet<string> picked = new();
    private List<InviteInfo> invites = new();
    private readonly Dictionary<string, List<(string Nick, int Total, string Text)>> summaries = new();
    private string? addingTo;
    private bool busy;

    public Pozvanky()
    {
        InitializeComponent();
    }

    protected override async void OnAppearing()
    {
        base.OnAppearing();
        if (AuthSession.Uid is not string me)
        {
            await Shell.Current.GoToAsync("//SignIn");
            return;
        }
        draft = Pozvani.DraftPub;
        Pozvani.DraftPub = null;
        picked.Clear();
        addingTo = null;
        Box.Clear();
        SetBusy(true);
        try
        {
            var (_, list) = await new FirestoreService().GetFriendsOverview(me);
            friends = list;
            if (draft != null) joinable = await Pozvani.AtPubAsync(draft.Id);
            else await LoadInvitesAsync();
        }
        catch (Exception ex)
        {
            await Listek.AlertAsync(this, "Chyba", Net.IsNetworkError(ex) ? "Bez signálu se nic nenačte." : ex.Message);
        }
        finally
        {
            SetBusy(false);
        }
        Render();
    }

    private async Task LoadInvitesAsync()
    {
        invites = await Pozvani.MineAsync();
        FriendBadge.SetInvites(Pozvani.WaitingCount);
    }

    private void SetBusy(bool on) => Busy.IsRunning = Busy.IsVisible = on;

    // ---------------- kreslení ----------------

    private void Render()
    {
        Box.Clear();
        if (draft != null) RenderDraft();
        else RenderList();
    }

    private void RenderDraft()
    {
        Box.Add(Text($"Do hospody {PubService.NameOf(draft!)}", "Heading", center: true));

        if (joinable.Count > 0)
        {
            Box.Add(Text("Tam už se chystají tvoji kámoši:", "FieldLabel"));
            foreach (var j in joinable)
            {
                var more = j.Going > j.Friends.Count ? $" a {j.Going - j.Friends.Count} dalších" : "";
                var row = Row(Text($"{j.FromNick} zve · jde {string.Join(", ", j.Friends)}{more}", "Body"));
                var join = SmallButton("Přidat se", PencilVariant.Primary, 120);
                var inv = j;
                join.Clicked += async (_, _) => await RunAsync(async () =>
                {
                    await Pozvani.JoinAsync(inv.Id);
                    draft = null;
                    await LoadInvitesAsync();
                });
                row.Add(join, 1, 0);
                Box.Add(row);
            }
            Box.Add(Text("Nebo udělej vlastní pozvánku:", "FieldLabel"));
        }
        else
        {
            Box.Add(Text("Koho zveš?", "FieldLabel"));
        }

        if (friends.Count == 0)
            Box.Add(Text("Nemáš koho pozvat. Nejdřív si přidej kámoše.", "Small"));

        var send = new PencilButton { Text = "Pozvat", Variant = PencilVariant.Primary, IsEnabled = false };
        void UpdateSend()
        {
            send.Text = picked.Count > 0 ? $"Pozvat ({picked.Count})" : "Pozvat";
            send.IsEnabled = picked.Count > 0;
        }
        var chips = Picker(friends, UpdateSend);
        Box.Add(chips);

        var all = SmallButton("Všichni", PencilVariant.Secondary);
        all.Clicked += (_, _) =>
        {
            if (picked.Count == friends.Count) picked.Clear();
            else foreach (var f in friends) picked.Add(f.UserId);
            Render();
        };
        var buttons = new Grid { ColumnDefinitions = new ColumnDefinitionCollection(new ColumnDefinition(GridLength.Auto), new ColumnDefinition(GridLength.Star)), ColumnSpacing = 8 };
        buttons.Add(all, 0, 0);
        buttons.Add(send, 1, 0);
        Box.Add(buttons);
        UpdateSend();
        send.Clicked += async (_, _) => await RunAsync(async () =>
        {
            await Pozvani.CreateAsync(draft!, picked);
            draft = null;
            picked.Clear();
            await LoadInvitesAsync();
        }, "Pozvánka letí. Teď už jen čekat, kdo dorazí.");

        Box.Add(Text("Pozvaní odpoví Jdu / Nejdu a můžou přizvat svoje kámoše. Pozvánka platí do 6 ráno.", "Small", soft: true));
        var cancel = SmallButton("Zrušit", PencilVariant.Secondary);
        cancel.Clicked += async (_, _) => await RunAsync(async () =>
        {
            draft = null;
            await LoadInvitesAsync();
        });
        Box.Add(cancel);
    }

    private void RenderList()
    {
        if (invites.Count == 0)
        {
            Box.Add(Text("Žádná pozvánka. Pozvat kámoše jde z mapy – ťukni na hospodu.", "Body", center: true));
            return;
        }

        foreach (var inv in invites)
        {
            var card = new VerticalStackLayout { Spacing = 6, Padding = new Thickness(0, 10, 0, 6), Opacity = inv.Active ? 1 : 0.85 };
            card.Add(new BoxView { HeightRequest = 1, Color = Soft.WithAlpha(0.5f) });
            card.Add(Text(inv.PubName, "Heading"));
            var who = inv.From == AuthSession.Uid ? "ty" : inv.FromNick;
            card.Add(Text(inv.Active ? $"zve {who} · do 6:00 · jde {inv.Going} z {inv.Members.Count}" : $"zve {who} · skončila v 6:00", "Small", soft: true));

            var people = new FlexLayout { Wrap = Microsoft.Maui.Layouts.FlexWrap.Wrap };
            foreach (var m in inv.Members.OrderBy(m => m.Status == "yes" ? 0 : m.Status == "wait" ? 1 : 2))
            {
                var l = Text($"{m.Mark} {(m.Uid == AuthSession.Uid ? "ty" : m.Nick)}", "Body", soft: m.Status != "yes");
                l.Margin = new Thickness(0, 0, 12, 0);
                if (m.Status == "no") l.TextDecorations = TextDecorations.Strikethrough;
                people.Add(l);
            }
            card.Add(people);

            if (inv.Active)
            {
                var mine = inv.MyStatus;
                var row = new Grid
                {
                    ColumnDefinitions = new ColumnDefinitionCollection(new ColumnDefinition(GridLength.Star), new ColumnDefinition(GridLength.Star), new ColumnDefinition(GridLength.Star)),
                    ColumnSpacing = 6
                };
                var yes = SmallButton("Jdu 🍺", mine == "yes" ? PencilVariant.Primary : PencilVariant.Secondary);
                var no = SmallButton("Nejdu", mine == "no" ? PencilVariant.Danger : PencilVariant.Secondary);
                var i1 = inv;
                yes.Clicked += async (_, _) => await RunAsync(async () => { await Pozvani.RespondAsync(i1.Id, true); await LoadInvitesAsync(); });
                no.Clicked += async (_, _) => await RunAsync(async () => { await Pozvani.RespondAsync(i1.Id, false); await LoadInvitesAsync(); });
                row.Add(yes, 0, 0);
                row.Add(no, 1, 0);
                if (mine != "no")
                {
                    var more = SmallButton("Pozvat další", PencilVariant.Secondary);
                    more.Clicked += (_, _) =>
                    {
                        picked.Clear();
                        addingTo = addingTo == i1.Id ? null : i1.Id;
                        Render();
                    };
                    row.Add(more, 2, 0);
                }
                card.Add(row);

                if (addingTo == inv.Id)
                {
                    var others = friends.Where(f => inv.Members.All(m => m.Uid != f.UserId)).ToList();
                    if (others.Count == 0)
                    {
                        card.Add(Text("Všechny svoje kámoše už tu máš.", "Small"));
                    }
                    else
                    {
                        var send = SmallButton("Pozvat", PencilVariant.Primary);
                        send.IsEnabled = false;
                        card.Add(Picker(others, () =>
                        {
                            send.Text = picked.Count > 0 ? $"Pozvat ({picked.Count})" : "Pozvat";
                            send.IsEnabled = picked.Count > 0;
                        }));
                        send.Clicked += async (_, _) => await RunAsync(async () =>
                        {
                            await Pozvani.AddAsync(i1.Id, picked);
                            picked.Clear();
                            addingTo = null;
                            await LoadInvitesAsync();
                        });
                        card.Add(send);
                    }
                }
            }
            else if (summaries.TryGetValue(inv.Id, out var sum))
            {
                if (sum.Count == 0) card.Add(Text("Nikdo nic nezapsal. Buď se nepilo, nebo se zapomnělo.", "Small"));
                for (var i = 0; i < sum.Count; i++)
                    card.Add(Text($"{(i == 0 && sum[i].Total > 0 ? "🏆" : $"{i + 1}.")} {sum[i].Nick} – {sum[i].Text}", "Small"));
            }
            else
            {
                var show = SmallButton("Kdo lil nejvíc?", PencilVariant.Secondary);
                var i2 = inv;
                show.Clicked += async (_, _) => await RunAsync(async () => summaries[i2.Id] = await Pozvani.SummaryAsync(i2));
                card.Add(show);
            }

            Box.Add(card);
        }
    }

    // ---------------- pomocné ----------------

    private async Task RunAsync(Func<Task> action, string? done = null)
    {
        if (busy) return;
        busy = true;
        SetBusy(true);
        try
        {
            await action();
            if (done != null) await Listek.AlertAsync(this, "Hotovo", done);
        }
        catch (Exception ex)
        {
            await Listek.AlertAsync(this, "Nepovedlo se", Net.IsNetworkError(ex) ? "Bez signálu to nepůjde." : ex.Message);
        }
        finally
        {
            busy = false;
            SetBusy(false);
        }
        Render();
    }

    /// <summary>Kámoši jako čipy: ťuk = vybrat / zrušit (vybraný = plná pastelka, nevybraný = bledý).</summary>
    private FlexLayout Picker(List<UserModel> list, Action changed)
    {
        var flex = new FlexLayout { Wrap = Microsoft.Maui.Layouts.FlexWrap.Wrap };
        foreach (var f in list)
        {
            var chip = SmallButton(f.Display, PencilVariant.Secondary);
            chip.Margin = new Thickness(0, 0, 6, 6);
            chip.Padding = new Thickness(14, 0);
            var uid = f.UserId;
            void Paint()
            {
                var on = picked.Contains(uid);
                chip.Variant = on ? PencilVariant.Primary : PencilVariant.Secondary;
                chip.Opacity = on ? 1.0 : 0.5;
            }
            chip.Clicked += (_, _) =>
            {
                if (!picked.Remove(uid)) picked.Add(uid);
                Paint();
                changed();
            };
            Paint();
            flex.Add(chip);
        }
        return flex;
    }

    private static Color Soft =>
        Application.Current?.Resources.TryGetValue("InkSoft", out var c) == true && c is Color color ? color : Colors.Gray;

    private static Label Text(string text, string style, bool center = false, bool soft = false)
    {
        var l = new Label { Text = text, HorizontalTextAlignment = center ? TextAlignment.Center : TextAlignment.Start };
        if (Application.Current?.Resources.TryGetValue(style, out var s) == true && s is Style st) l.Style = st;
        if (soft) l.TextColor = Soft;
        return l;
    }

    private static Grid Row(View left)
    {
        var g = new Grid
        {
            ColumnDefinitions = new ColumnDefinitionCollection(new ColumnDefinition(GridLength.Star), new ColumnDefinition(GridLength.Auto)),
            ColumnSpacing = 6,
            MinimumHeightRequest = 44
        };
        left.VerticalOptions = LayoutOptions.Center;
        g.Add(left, 0, 0);
        return g;
    }

    private static PencilButton SmallButton(string text, PencilVariant variant, double width = -1)
    {
        var b = new PencilButton { Text = text, Variant = variant };
        if (width > 0) b.WidthRequest = width;
        b.SetDynamicResource(PencilButton.FontSizeProperty, "FontButtonSmall");
        b.SetDynamicResource(HeightRequestProperty, "ButtonHeightSmall");
        return b;
    }

    private void OnZpetClicked(object sender, EventArgs e) => OnBackButtonPressed();

    protected override bool OnBackButtonPressed()
    {
        if (Listek.DismissActive()) return true;
        Dispatcher.Dispatch(async () => await Shell.Current.GoToAsync("//Friends"));
        return true;
    }
}
