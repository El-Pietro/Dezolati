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
        await LoadFriendsAndRequests();
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
            CheersLabel.IsVisible = false;
            if (me?.SessionId is string session)
            {
                var cheers = await Cheers.ForMeAsync(session);
                if (cheers.Count > 0)
                {
                    CheersLabel.Text = "Připili si s tebou: " + string.Join(", ", cheers.Select(c => $"{c.FromNick} {c.Emoji}"));
                    CheersLabel.IsVisible = true;
                }
            }
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

            var friend = await firestoreService.FindUserByNickname(nickname);
            if (friend == null)
            {
                await Listek.AlertAsync(this, "Nic", $"Žádný dezolát „{nickname}“ tu není. Pozor na velká/malá písmena.");
                return;
            }

            var problem = await firestoreService.SendFriendRequest(userId, friend);
            if (problem != null)
            {
                await Listek.AlertAsync(this, "Hmm", problem);
                return;
            }

            NicknameEntry.Text = "";
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
