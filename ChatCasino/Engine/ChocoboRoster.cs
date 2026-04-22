using System.Collections.Generic;
using System.Text.Json.Serialization;

namespace ChatCasino.Engine;

public sealed class ChocoboRacerProfile
{
    public string Name { get; set; } = string.Empty;
    public int Speed { get; set; }
    public int Endurance { get; set; }
}

public sealed class ChocoboRoster
{
    [JsonPropertyName("racers")]
    public List<ChocoboRacerProfile> Racers { get; set; } = new();
}
