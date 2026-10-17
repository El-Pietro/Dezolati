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

        public string Display => string.IsNullOrWhiteSpace(Nickname) ? "(bez přezdívky)" : Nickname!;
    }
}
