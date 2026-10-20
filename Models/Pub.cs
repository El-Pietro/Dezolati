namespace Dezolati.Models
{
    /// <summary>
    /// Hospoda z OpenStreetMap (katalog v telefonu, ne ve Firestore).
    /// Id = typ + OSM id, např. "n123456" (node) nebo "w789" (way).
    /// </summary>
    public sealed record PubInfo(
        string Id,
        string Name,
        string Kind,
        double Lat,
        double Lon,
        string? Address,
        string? Brewery,
        string? Hours)
    {
        public string KindText => Kind switch
        {
            "bar" => "bar",
            "biergarten" => "pivní zahrádka",
            "restaurant" => "restaurace",
            _ => "hospoda"
        };
    }

    /// <summary>Profil hospody, který vyplňují uživatelé (Firestore Pubs/{id}).</summary>
    public class PubProfile
    {
        /// <summary>Opravený název (null/prázdné = platí název z OpenStreetMap).</summary>
        public string? DisplayName { get; set; }
        public string? Taps { get; set; }
        public string? Prices { get; set; }
        public string? Hours { get; set; }
        public string? Note { get; set; }
        public DateTime? UpdatedAt { get; set; }
        public string? UpdatedByNick { get; set; }

        public bool IsEmpty =>
            string.IsNullOrWhiteSpace(Taps) && string.IsNullOrWhiteSpace(Prices) &&
            string.IsNullOrWhiteSpace(Hours) && string.IsNullOrWhiteSpace(Note);
    }

    /// <summary>Jedna úprava profilu (Firestore PubEdits/{id}) – historie proti trollům.</summary>
    public class PubEdit
    {
        public string? DisplayName { get; set; }
        public DateTime? At { get; set; }
        public string? ByNick { get; set; }
        public string? Taps { get; set; }
        public string? Prices { get; set; }
        public string? Hours { get; set; }
        public string? Note { get; set; }
    }
}
