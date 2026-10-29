using Dezolati.Controls;
using Dezolati.Models;
using Dezolati.Services;

namespace Dezolati.Pages;

public partial class Friends : ContentPage
{
    private readonly FirestoreService firestoreService = new();

    public Friends()
    {
        InitializeComponent();
    }

    protected override async void OnAppearing()
    {
        base.OnAppearing();
        Cheers.Received += OnCheerReceived;
        await LoadFriendsAndRequests();
    }

    protected override void OnDisappearing()
    {
        base.OnDisappearing();
        Cheers.Received -= OnCheerReceived;
    }

    // ---------------- kdo mi připil ----------------

    private readonly List<CheerInfo> myCheers = new();
    private string? mySession;

    /// <summary>Připití z oznámení → hned nahoru (bez čtení).</summary>
    private void OnCheerReceived(CheerInfo cheer, string sessionId) => MainThread.BeginInvokeOnMainThread(() =>
    {
        if (mySession != null && mySession != sessionId) myCheers.Clear();   // nové sezení
        mySession = sessionId;
        myCheers.RemoveAll(c => c.FromUid == cheer.FromUid);
        myCheers.Add(cheer);
        RenderCheers();
    });

    /// <summary>„Připili si s tebou: Bílas 🍺, Mates 🥃“ – ťuk na jméno otevře jeho statistiky.</summary>
    private void RenderCheers()
    {
        CheersBox.Clear();
        CheersBox.IsVisible = myCheers.Count > 0;
        if (myCheers.Count == 0) return;
        CheersBox.Add(new Label { Text = "Připili si s tebou: ", Style = AppStyle("Small") });
        for (var i = 0; i < myCheers.Count; i++)
        {
            var c = myCheers[i];
            var label = new Label
            {
                Text = $"{c.FromNick} {c.Emoji}{(i < myCheers.Count - 1 ? ", " : "")}",
                Style = AppStyle("Small"), TextDecorations = TextDecorations.Underline
            };
            var tap = new TapGestureRecognizer();
            tap.Tapped += async (_, _) =>
            {
                if (friends.All(f => f.UserId != c.FromUid)) return;   // jen kámoši
                await Shell.Current.GoToAsync("//StatsFriend", new Dictionary<string, object>
                {
                    ["userId"] = c.FromUid, ["nick"] = c.FromNick, ["back"] = "//Friends"
                });
            };
            label.GestureRecognizers.Add(tap);
            CheersBox.Add(label);
        }
    }

    private List<UserModel> friends = new();

    private async Task LoadFriendsAndRequests()
    {
        var userId = AuthSession.Uid;
        if (userId == null)
        {
            await Shell.Current.GoToAsync("//SignIn");
            return;
        }

        try
        {
            // jeden dotaz na žádosti i přátele
            var (requests, list) = await firestoreService.GetFriendsOverview(userId);
            PendingRequestsList.ItemsSource = requests;
            PendingHeader.IsVisible = PendingRequestsList.IsVisible = requests.Count > 0;
            friends = list;
            FriendBadge.Set(requests.Count);   // odznak na lístku bez dalšího čtení
            FriendsNow.RememberFriends(friends);
            RenderFriends(new());               // hned seznam, kdo pije se doplní
            // můj řádek v adresáři (přezdívka + kámoši) – zapíše se jen, když se něco změnilo
            _ = NickDirectory.PublishFriendsAsync(UserSession.Profile?.Nickname, friends);
            if (SuggestStack.IsVisible) _ = LoadSuggestionsAsync();
            _ = LoadNowAsync();
        }
        catch (Exception ex)
        {
            await Listek.AlertAsync(this, "Chyba", $"Nepodařilo se načíst přátele: {ex.Message}");
        }
    }

    // ---------------- kdo teď pije + připíjení ----------------

    private async Task LoadNowAsync()
    {
        try
        {
            var all = await FriendsNow.GetAsync(includeMe: true, force: true);
            var me = all.FirstOrDefault(f => f.Uid == AuthSession.Uid);
            RenderFriends(all.Where(f => f.Uid != AuthSession.Uid).ToList());

            // kdo mi připil na moje sezení (sezení končí 3h pauzou → pak už tu nic není)
            myCheers.Clear();
            mySession = me?.SessionId;
            if (me?.SessionId is string session)
                myCheers.AddRange(await Cheers.ForMeAsync(session));
            RenderCheers();
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"Teď pijí: {ex.Message}");
        }
    }

    /// <summary>Jeden seznam: kdo pije nahoře („🍺 Jméno (hospoda, 20 min)“ + reakce), pak ostatní.</summary>
    private void RenderFriends(List<DrinkingFriend> drinking)
    {
        // smazat staré řádky, prázdný popisek nechat
        foreach (var child in FriendsStack.Children.Where(c => c != EmptyLabel).ToList())
            FriendsStack.Remove(child);
        EmptyLabel.IsVisible = friends.Count == 0;

        var byUid = drinking.GroupBy(f => f.Uid).ToDictionary(g => g.Key, g => g.First());
        var ordered = friends
            .OrderBy(f => byUid.ContainsKey(f.UserId) ? 0 : 1)
            .ThenByDescending(f => byUid.TryGetValue(f.UserId, out var d) ? d.LastDrinkAt : DateTime.MinValue)
            .ThenBy(f => f.Display, StringComparer.CurrentCultureIgnoreCase);

        foreach (var friend in ordered)
        {
            byUid.TryGetValue(friend.UserId, out var now);
            var row = new Grid
            {
                ColumnDefinitions = new ColumnDefinitionCollection(new ColumnDefinition(GridLength.Star), new ColumnDefinition(GridLength.Auto)),
                ColumnSpacing = 6,
                MinimumHeightRequest = 44
            };

            var name = new Label
            {
                Style = AppStyle("Heading"), VerticalOptions = LayoutOptions.Center,
                HorizontalOptions = LayoutOptions.Fill, HorizontalTextAlignment = TextAlignment.Start,
                LineBreakMode = LineBreakMode.TailTruncation, MaxLines = 1
            };
            // Span na Androidu nedědí písmo ani barvu ze stylu labelu → nastavit ručně
            var text = new FormattedString();
            text.Spans.Add(InkSpan(now != null ? $"{now.Emoji} {friend.Display}" : friend.Display, "FontHeading", "Ink"));
            if (now != null)
            {
                var where = now.PubName ?? "pije";
                text.Spans.Add(InkSpan($"  ({where}, {FriendsNow.Ago(now.LastDrinkAt)})", "FontSmall", "InkSoft"));
            }
            name.FormattedText = text;

            var tap = new TapGestureRecognizer();
            var f = friend;
            tap.Tapped += async (_, _) => await Shell.Current.GoToAsync("//StatsFriend", new Dictionary<string, object>
            {
                ["userId"] = f.UserId, ["nick"] = f.Display, ["back"] = "//Friends"
            });
            name.GestureRecognizers.Add(tap);
            row.Add(name, 0, 0);

            // reakce: jen na běžící sezení (nová verze serveru + appky) a jen jednou
            if (now?.SessionId is string session)
            {
                if (!Cheers.Reacted(session))
                {
                    var buttons = new HorizontalStackLayout { Spacing = 3, VerticalOptions = LayoutOptions.Center };
                    foreach (var emoji in Cheers.Emojis)
                    {
                        var b = new PencilButton
                        {
                            Text = emoji, Variant = PencilVariant.Secondary, WidthRequest = 44,
                            HeightRequest = 38, FontSize = 17
                        };
                        var e = emoji;
                        b.Clicked += async (_, _) => await CheerAsync(now, session, e, buttons);
                        buttons.Add(b);
                    }
                    row.Add(buttons, 1, 0);
                }
                else
                {
                    row.Add(new Label { Text = "připito ✔", Style = AppStyle("Small"), VerticalOptions = LayoutOptions.Center }, 1, 0);
                }
            }

            FriendsStack.Add(row);
        }
    }

    private async Task CheerAsync(DrinkingFriend friend, string session, string emoji, View buttons)
    {
        buttons.IsEnabled = false;
        try
        {
            var sent = await Cheers.SendAsync(friend.Uid, session, emoji);
            if (buttons.Parent is Grid g)
            {
                g.Remove(buttons);
                g.Add(new Label { Text = sent ? $"připito {emoji}" : "připito ✔", Style = AppStyle("Small"),
                                  VerticalOptions = LayoutOptions.Center }, 1, 0);
            }
        }
        catch (Exception ex)
        {
            buttons.IsEnabled = true;
            await Listek.AlertAsync(this, "Nepovedlo se", Net.IsNetworkError(ex) ? "Bez signálu se připít nedá." : ex.Message);
        }
    }

    private static Span InkSpan(string text, string sizeKey, string colorKey)
    {
        var span = new Span { Text = text, FontFamily = "Caveat" };
        span.SetDynamicResource(Span.FontSizeProperty, sizeKey);
        if (Application.Current?.Resources.TryGetValue(colorKey, out var c) == true && c is Color color)
            span.TextColor = color;
        return span;
    }

    private static Style? AppStyle(string key) =>
        Application.Current?.Resources.TryGetValue(key, out var s) == true ? s as Style : null;

    // ---------------- Možná znáš ----------------

    private void OnSuggestTapped(object? sender, TappedEventArgs e)
    {
        SuggestStack.IsVisible = !SuggestStack.IsVisible;
        SuggestToggle.Text = SuggestStack.IsVisible ? "(schovat)" : "(ukázat)";
        if (SuggestStack.IsVisible) _ = LoadSuggestionsAsync();
    }

    private async Task LoadSuggestionsAsync()
    {
        SuggestStack.Clear();
        SuggestStack.Add(new Label { Text = "Hledám…", Style = AppStyle("Small") });
        var dir = await NickDirectory.GetAsync();
        var list = NickDirectory.Suggest(dir, friends.Select(f => f.UserId));
        SuggestStack.Clear();
        if (list.Count == 0)
        {
            SuggestStack.Add(new Label
            {
                Text = dir.Count == 0 && !Net.IsOnline ? "Bez signálu nic nevymyslím." : "Všechny už máš. Nebo tu kromě vás nikdo nepije.",
                Style = AppStyle("Small")
            });
            return;
        }
        foreach (var s in list)
        {
            var row = new Grid
            {
                ColumnDefinitions = new ColumnDefinitionCollection(new ColumnDefinition(GridLength.Star), new ColumnDefinition(GridLength.Auto)),
                ColumnSpacing = 6
            };
            var text = new FormattedString();
            text.Spans.Add(InkSpan(s.Nick, "FontLabel", "Ink"));
            if (s.Mutual > 0)
                text.Spans.Add(InkSpan($"  ({s.Mutual} {Cz.Plural(s.Mutual, "společný", "společní", "společných")})", "FontSmall", "InkSoft"));
            row.Add(new Label { FormattedText = text, VerticalOptions = LayoutOptions.Center, LineBreakMode = LineBreakMode.TailTruncation, MaxLines = 1 }, 0, 0);
            var add = new PencilButton
            {
                Text = "Přidat", Variant = PencilVariant.Secondary, WidthRequest = 96, HeightRequest = 38, FontSize = 17
            };
            var suggestion = s;
            add.Clicked += async (_, _) =>
            {
                add.IsEnabled = false;
                if (await RequestAsync(new UserModel { Id = suggestion.Uid, UserId = suggestion.Uid, Nickname = suggestion.Nick }))
                    add.Text = "Posláno";
                else
                    add.IsEnabled = true;
            };
            row.Add(add, 1, 0);
            SuggestStack.Add(row);
        }
    }

    // ---------------- hledání přezdívky (v telefonu) ----------------

    private Dictionary<string, DirectoryEntry>? directory;

    private async void OnNickFocused(object? sender, FocusEventArgs e)
    {
        directory = await NickDirectory.GetAsync();   // 1 čtení max. 3× denně, jinak z telefonu
        RenderSearch();
    }

    private void OnNickTextChanged(object? sender, TextChangedEventArgs e) => RenderSearch();

    private void RenderSearch()
    {
        SearchList.Clear();
        var text = NicknameEntry.Text ?? "";
        var found = directory == null ? new List<DirectoryEntry>() : NickDirectory.Search(directory, text, 4);
        var friendIds = friends.Select(f => f.UserId).ToHashSet();
        found = found.Where(f => !friendIds.Contains(f.Uid)
                                 && !string.Equals(f.Nick, text.Trim(), StringComparison.Ordinal)).ToList();
        SearchList.IsVisible = found.Count > 0;
        foreach (var f in found)
        {
            var label = new Label { Text = f.Nick, Style = AppStyle("Body"), Padding = new Thickness(6, 4) };
            var tap = new TapGestureRecognizer();
            var nick = f.Nick;
            tap.Tapped += (_, _) =>
            {
                NicknameEntry.Text = nick;   // pak „Poslat žádost“
                SearchList.Clear();
                SearchList.IsVisible = false;
            };
            label.GestureRecognizers.Add(tap);
            SearchList.Add(label);
        }
    }

    /// <summary>Poslat žádost; true = odesláno.</summary>
    private async Task<bool> RequestAsync(UserModel target)
    {
        if (AuthSession.Uid is not string userId) return false;
        try
        {
            var problem = await firestoreService.SendFriendRequest(userId, target);
            if (problem != null)
            {
                await Listek.AlertAsync(this, "Hmm", problem);
                return false;
            }
            return true;
        }
        catch (Exception ex)
        {
            await Listek.AlertAsync(this, "Chyba", Net.IsNetworkError(ex) ? "Bez signálu to nepůjde." : ex.Message);
            return false;
        }
    }

    private async void OnAddFriendClicked(object sender, EventArgs e)
    {
        var nickname = NicknameEntry.Text?.Trim();
        var userId = AuthSession.Uid;

        if (string.IsNullOrEmpty(nickname) || userId == null)
        {
            await Listek.AlertAsync(this, "Chyba", "Zadej přezdívku.");
            return;
        }

        try
        {
            AddButton.IsEnabled = false;

            // z adresáře v telefonu (bez čtení), jinak přesná shoda na serveru (starší verze appky v adresáři nejsou)
            var hit = directory?.Values.FirstOrDefault(d => string.Equals(d.Nick, nickname, StringComparison.CurrentCultureIgnoreCase));
            var friend = hit != null
                ? new UserModel { Id = hit.Uid, UserId = hit.Uid, Nickname = hit.Nick }
                : await firestoreService.FindUserByNickname(nickname);
            if (friend == null)
            {
                await Listek.AlertAsync(this, "Nic", $"Žádný dezolát „{nickname}“ tu není.");
                return;
            }

            var problem = await firestoreService.SendFriendRequest(userId, friend);
            if (problem != null)
            {
                await Listek.AlertAsync(this, "Hmm", problem);
                return;
            }

            NicknameEntry.Text = "";
            SearchList.Clear();
            SearchList.IsVisible = false;
            await Listek.AlertAsync(this, "Hotovo", "Žádost odeslána.");
        }
        catch (Exception ex)
        {
            await Listek.AlertAsync(this, "Chyba", ex.Message);
        }
        finally
        {
            AddButton.IsEnabled = true;
        }
    }

    private async void OnAcceptRequestClicked(object sender, EventArgs e) =>
        await AnswerRequest(sender, "Accepted");

    private async void OnRejectRequestClicked(object sender, EventArgs e) =>
        await AnswerRequest(sender, "Rejected");

    private async Task AnswerRequest(object sender, string status)
    {
        if (((PencilButton)sender).CommandParameter is not string requestId || requestId.Length == 0)
            return;

        try
        {
            await firestoreService.UpdateFriendRequest(requestId, status);
            await LoadFriendsAndRequests();
        }
        catch (Exception ex)
        {
            await Listek.AlertAsync(this, "Chyba", ex.Message);
        }
    }

    private void OnZpetClicked(object sender, EventArgs e) => OnBackButtonPressed();

    // systémové tlačítko Zpět: nejdřív zavřít případný lístek s hláškou, pak o stránku zpátky
    protected override bool OnBackButtonPressed()
    {
        if (Listek.DismissActive()) return true;
        Dispatcher.Dispatch(async () => await Shell.Current.GoToAsync("//MainPage"));
        return true;
    }
}
