namespace Dezolati.Models
{
    public class UserModel
    {
        public string Id { get; set; } = "";
        public string UserId { get; set; } = "";
        public string? Name { get; set; }
        public string? Nickname { get; set; }
        public string? Gender { get; set; }
        public string? DrinkerType { get; set; }
        public string? Email { get; set; }
        /// <summary>Chci oznámení od kámošů (žádosti, kdo sedí v hospodě). Výchozí ano.</summary>
        public bool NotifyFriends { get; set; } = true;
        /// <summary>Hlásit kámošům, ve které hospodě sedím. Výchozí ne (poloha).</summary>
        public bool SharePub { get; set; }

        public string Display => string.IsNullOrWhiteSpace(Nickname) ? "(bez přezdívky)" : Nickname!;
    }
}
