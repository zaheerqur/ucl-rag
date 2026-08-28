using System.ComponentModel;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Api.Tools;

public class RosterService
{
    private readonly IReadOnlyDictionary<string, Club> _clubs;

    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        WriteIndented = false,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    public RosterService(string rostersJsonPath)
    {
        string json = File.ReadAllText(rostersJsonPath);
        var root = JsonSerializer.Deserialize<RosterRoot>(json, SerializerOptions)
            ?? throw new InvalidOperationException("Failed to deserialize rosters.json");

        _clubs = root.Clubs.ToDictionary(
            c => c.Name,
            c => c,
            StringComparer.OrdinalIgnoreCase);
    }

    [Description("Returns the squad list for a club, including each player's position and training status (club-trained, association-trained, or neither). Call this only when the question requires actual squad data for a specific club.")]
    public string GetSquad(
        [Description("The club name, e.g. 'Liverpool' or 'Manchester United'")] string clubName)
    {
        if (!_clubs.TryGetValue(clubName, out var club))
            return JsonSerializer.Serialize(new
            {
                error = $"Club '{clubName}' not found.",
                availableClubs = _clubs.Keys.ToList(),
            });

        var summary = new
        {
            club = club.Name,
            totalPlayers = club.Players.Count,
            clubTrained = club.Players.Count(p => p.Training == "club"),
            associationTrained = club.Players.Count(p => p.Training == "association"),
            neitherTrained = club.Players.Count(p => p.Training == "neither"),
            players = club.Players.Select(p => new { p.Name, p.Position, p.Training }),
        };

        return JsonSerializer.Serialize(summary, SerializerOptions);
    }

    private record RosterRoot(List<Club> Clubs);
    private record Club(string Name, List<Player> Players);
    private record Player(string Name, string Position, string Training);
}
