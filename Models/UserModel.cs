namespace Dezolati.Models
{
    /// <summary>Oblíbená hospoda uložená v profilu.</summary>
    public sealed record FavoritePub(string Name, string? Kind, double Lat, double Lon);

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

        /// <summary>Kámošům: 0 nehlásit, 1 hlásit že piju, 2 i kde (hospoda). SharePub = úroveň 2.</summary>
        public int ShareLevel { get; set; } = 1;
        /// <summary>Oblíbené hospody (id → název a poloha, ať jsou vidět i mimo stažené čtverce mapy).</summary>
        public Dictionary<string, FavoritePub> FavoritePubs { get; set; } = new();

        public string Display => string.IsNullOrWhiteSpace(Nickname) ? "(bez přezdívky)" : Nickname!;
    }
}
