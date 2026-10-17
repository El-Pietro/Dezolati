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
            var requests = await firestoreService.GetPendingFriendRequests(userId);
            PendingRequestsList.ItemsSource = requests;
            PendingHeader.IsVisible = PendingRequestsList.IsVisible = requests.Count > 0;

            FriendsList.ItemsSource = await firestoreService.GetFriendsList(userId);
        }
        catch (Exception ex)
        {
            await DisplayAlert("Chyba", $"Nepodařilo se načíst přátele: {ex.Message}", "OK");
        }
    }

    private async void OnAddFriendClicked(object sender, EventArgs e)
    {
        var nickname = NicknameEntry.Text?.Trim();
        var userId = AuthSession.Uid;

        if (string.IsNullOrEmpty(nickname) || userId == null)
        {
            await DisplayAlert("Chyba", "Zadej přezdívku.", "OK");
            return;
        }

        try
        {
            AddButton.IsEnabled = false;

            var friend = await firestoreService.FindUserByNickname(nickname);
            if (friend == null)
            {
                await DisplayAlert("Nic", $"Žádný dezolát „{nickname}“ tu není. Pozor na velká/malá písmena.", "OK");
                return;
            }

            var problem = await firestoreService.SendFriendRequest(userId, friend.UserId);
            if (problem != null)
            {
                await DisplayAlert("Hmm", problem, "OK");
                return;
            }

            NicknameEntry.Text = "";
            await DisplayAlert("Hotovo", "Žádost odeslána.", "OK");
        }
        catch (Exception ex)
        {
            await DisplayAlert("Chyba", ex.Message, "OK");
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
        if (((Button)sender).CommandParameter is not string requestId || requestId.Length == 0)
            return;

        try
        {
            await firestoreService.UpdateFriendRequest(requestId, status);
            await LoadFriendsAndRequests();
        }
        catch (Exception ex)
        {
            await DisplayAlert("Chyba", ex.Message, "OK");
        }
    }

    private async void OnViewStatsClicked(object sender, EventArgs e)
    {
        if (((Button)sender).CommandParameter is not UserModel friend)
            return;

        await Shell.Current.GoToAsync("//StatsFriend", new Dictionary<string, object>
        {
            ["userId"] = friend.UserId,
            ["nick"] = friend.Display,
            ["back"] = "//Friends"
        });
    }

    private async void OnZpetClicked(object sender, EventArgs e)
    {
        await Shell.Current.GoToAsync("//MainPage");
    }
}
