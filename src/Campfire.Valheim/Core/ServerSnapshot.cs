using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

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

    public Publication StatePublication()
    {
        var fields = new Dictionary<string, string>();
        if (World != null)
        {
            fields["world"] = World;
        }

        if (InviteCode != null)
        {
            fields["invite_code"] = InviteCode;
        }

        return new Publication { State = new GameState { Players = Players.Count, Capacity = Capacity, Fields = fields } };
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
