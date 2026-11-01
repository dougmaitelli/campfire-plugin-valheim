using System;
using System.Collections.Generic;
using System.Globalization;

namespace Campfire.Valheim.Core;

public sealed class DeathTracker
{
    private readonly HashSet<string> seen = new();
    private readonly Queue<string> order = new();
    private readonly bool includeNames;

    public DeathTracker(bool includeNames = true) => this.includeNames = includeNames;

    public GameEvent? ObserveDeath(string messageID, string playerName, DateTimeOffset now)
    {
        if (!seen.Add(messageID)) { return null; }
        order.Enqueue(messageID);
        if (order.Count > 512) { seen.Remove(order.Dequeue()); }

        var name = includeNames ? TextBounds.Utf8(playerName, 128) : "A player";
        var data = new Dictionary<string, string>();
        if (includeNames) { data["player"] = name; }

        return new GameEvent { Type = "player.died", Title = name + " died", OccurredAt = now.ToString("O", CultureInfo.InvariantCulture), Data = data };
    }
}
