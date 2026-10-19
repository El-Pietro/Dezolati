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
            var (requests, friends) = await firestoreService.GetFriendsOverview(userId);
            PendingRequestsList.ItemsSource = requests;
            PendingHeader.IsVisible = PendingRequestsList.IsVisible = requests.Count > 0;
            FriendsList.ItemsSource = friends;
        }
        catch (Exception ex)
        {
            await Listek.AlertAsync(this, "Chyba", $"Nepodařilo se načíst přátele: {ex.Message}");
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

    private async void OnViewStatsClicked(object sender, EventArgs e)
    {
        if (((PencilButton)sender).CommandParameter is not UserModel friend)
            return;

        await Shell.Current.GoToAsync("//StatsFriend", new Dictionary<string, object>
        {
            ["userId"] = friend.UserId,
            ["nick"] = friend.Display,
            ["back"] = "//Friends"
        });
    }

    // systémové tlačítko Zpět: nejdřív zavřít případný lístek s hláškou, pak o stránku zpátky
    protected override bool OnBackButtonPressed()
    {
        if (Listek.DismissActive()) return true;
        Dispatcher.Dispatch(async () => await Shell.Current.GoToAsync("//MainPage"));
        return true;
    }
}
