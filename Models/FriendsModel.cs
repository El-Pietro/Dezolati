namespace Dezolati.Models
{
    /// <summary>Žádost o přátelství / přátelství. UserId = kdo žádal, FriendId = koho žádal.</summary>
    public class FriendsModel
    {
        /// <summary>ID dokumentu ve Firestore – potřeba pro přijetí/odmítnutí.</summary>
        public string RequestId { get; set; } = "";
        public string UserId { get; set; } = "";
        public string FriendId { get; set; } = "";
        public string Status { get; set; } = "";
        public DateTime? CreatedAt { get; set; }

        /// <summary>Přezdívka toho, kdo žádost poslal.</summary>
        public string? FromNickname { get; set; }

        public string Display => string.IsNullOrWhiteSpace(FromNickname) ? "(neznámý dezolát)" : FromNickname!;
    }
}
