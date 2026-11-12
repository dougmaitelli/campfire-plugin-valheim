using System.Collections.Generic;
using System.Runtime.Serialization;

namespace Campfire.Valheim.Core;

[DataContract]
public sealed class GameRegistration
{
    [DataMember(Name = "revision", Order = 0)]
    public long Revision { get; set; }

    [DataMember(Name = "state_fields", Order = 1)]
    public Dictionary<string, string> StateFields { get; set; } = new();

    [DataMember(Name = "event_fields", Order = 2)]
    public Dictionary<string, Dictionary<string, string>> EventFields { get; set; } = new();
}

public static class ValheimRegistration
{
    // This is a persistent schema revision, not the plugin release version.
    // Increase it whenever changing the label definitions below.
    public const long Revision = 1;

    public static Publication CreatePublication() => new()
    {
        Registration = new GameRegistration
        {
            Revision = Revision,
            StateFields = new()
            {
                ["world"] = "World name",
                ["invite_code"] = "Invite code"
            },
            EventFields = new()
            {
                ["valheim.integration.started"] = new(),
                ["player.joined"] = new() { ["player"] = "Player name" },
                ["player.left"] = new() { ["player"] = "Player name" },
                ["player.died"] = new() { ["player"] = "Player name" },
                ["valheim.boss.defeated"] = new()
                {
                    ["boss"] = "Boss",
                    ["progression_key"] = "Progression key",
                    ["source"] = "Source"
                },
                ["game.announcement"] = new() { ["message"] = "Message" }
            }
        }
    };
}
