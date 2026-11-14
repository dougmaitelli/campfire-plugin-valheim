using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Runtime.Serialization.Json;

namespace Campfire.Valheim.Core;

public sealed class PlayerSnapshot
{
    public long ID { get; }
    public string Name { get; }

    public PlayerSnapshot(long id, string name)
    {
        ID = id;
        Name = TextBounds.Utf8(string.IsNullOrWhiteSpace(name) ? "Player" : name, 128);
    }
}

// This copy contains no Unity objects and is safe to pass to the publisher worker.
public sealed class ServerSnapshot
{
    public string Session { get; }
    public string? World { get; }
    public int? Capacity { get; }
    public IReadOnlyList<PlayerSnapshot> Players { get; }
    public DateTimeOffset CapturedAt { get; }
    public string? InviteCode { get; }

    public ServerSnapshot(string session, string? world, int? capacity,
        IEnumerable<PlayerSnapshot> players, DateTimeOffset capturedAt, string? inviteCode = null)
    {
        Session = session;
        World = string.IsNullOrWhiteSpace(world) ? null : TextBounds.Utf8(world!, 1024);
        Capacity = capacity >= 0 && capacity <= 1_000_000 ? capacity : null;
        Players = players.GroupBy(player => player.ID).Select(group => group.Last()).ToArray();
        CapturedAt = capturedAt;
        InviteCode = InviteCodeReader.Validate(inviteCode);
    }

    public Publication StatePublication(bool includeNames = true)
    {
        var fields = new Dictionary<string, object>();
        if (World != null)
        {
            fields["world"] = World;
        }

        if (InviteCode != null)
        {
            fields["invite_code"] = InviteCode;
        }

        if (includeNames)
        {
            fields["player_names"] = PlayerNames();
        }

        return new Publication { State = new GameState { Players = Players.Count, Capacity = Capacity, Fields = fields } };
    }

    private string[] PlayerNames()
    {
        // Leave room in the 16 KiB envelope for the world name, invite code and
        // metadata, even when JSON escaping expands control characters.
        const int maximumEncodedBytes = 8 * 1024;
        var names = new List<string>();
        var serializer = new DataContractJsonSerializer(typeof(string));
        var encodedBytes = 2; // Array brackets.
        foreach (var player in Players.OrderBy(player => player.Name, StringComparer.Ordinal).ThenBy(player => player.ID).Take(64))
        {
            using var encoded = new MemoryStream();
            serializer.WriteObject(encoded, player.Name);
            var size = (int)encoded.Length + (names.Count == 0 ? 0 : 1);
            if (encodedBytes + size > maximumEncodedBytes) { break; }
            names.Add(player.Name);
            encodedBytes += size;
        }

        return names.ToArray();
    }
}

// Polling differences are observations, not a complete gameplay event stream.
public sealed class PlayerTracker
{
    private ServerSnapshot? previous;
    private readonly bool includeNames;

    public PlayerTracker(bool includeNames = true) => this.includeNames = includeNames;

    public void Reset() => previous = null;

    public IReadOnlyList<GameEvent> Observe(ServerSnapshot snapshot)
    {
        var events = new List<GameEvent>();
        if (previous == null || previous.Session != snapshot.Session)
        {
            events.Add(new GameEvent
            {
                Type = "valheim.integration.started",
                Title = "Valheim integration connected",
                OccurredAt = snapshot.CapturedAt.ToString("O", CultureInfo.InvariantCulture)
            });
        }
        else
        {
            var oldIDs = new HashSet<long>(previous.Players.Select(player => player.ID));
            var newIDs = new HashSet<long>(snapshot.Players.Select(player => player.ID));
            foreach (var player in snapshot.Players.Where(player => !oldIDs.Contains(player.ID)))
            {
                events.Add(PlayerEvent(player, "player.joined", "joined", snapshot.CapturedAt));
            }

            foreach (var player in previous.Players.Where(player => !newIDs.Contains(player.ID)))
            {
                events.Add(PlayerEvent(player, "player.left", "left", snapshot.CapturedAt));
            }
        }

        previous = snapshot;
        return events;
    }

    private GameEvent PlayerEvent(PlayerSnapshot player, string type, string verb, DateTimeOffset at)
    {
        var name = includeNames ? player.Name : "A player";
        return new GameEvent
        {
            Type = type,
            Title = name + " " + verb + " the server",
            OccurredAt = at.ToString("O", CultureInfo.InvariantCulture),
            Data = includeNames ? new Dictionary<string, string> { ["player"] = name } : new()
        };
    }
}
