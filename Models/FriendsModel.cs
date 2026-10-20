namespace Dezolati.Models
{
    /// <summary>
    /// Žádost o přátelství / přátelství.
    /// V DB: UserId = žadatel, FriendId = adresát, Members = [UserId, FriendId], Nicks = {uid: přezdívka}.
    /// </summary>
    public class FriendsModel
    {
        /// <summary>ID dokumentu ve Firestore – potřeba pro přijetí/odmítnutí.</summary>
        public string RequestId { get; set; } = "";
        /// <summary>Ten druhý (u příchozí žádosti ten, kdo ji poslal).</summary>
        public string UserId { get; set; } = "";
        public string FriendId { get; set; } = "";
        public string Status { get; set; } = "";
        public DateTime? CreatedAt { get; set; }

        /// <summary>Přezdívka toho druhého.</summary>
        public string? FromNickname { get; set; }

        public string Display => string.IsNullOrWhiteSpace(FromNickname) ? "(neznámý dezolát)" : FromNickname!;
    }
}
