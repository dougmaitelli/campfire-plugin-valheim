using System;
using System.Collections.Generic;
using System.Globalization;

namespace Campfire.Valheim.Core;

// World progression marks first defeats, not individual kills or achievements.
public sealed class BossTracker
{
    private string? session;
    private readonly HashSet<string> observed = new(StringComparer.OrdinalIgnoreCase);

    public void Reset()
    {
        session = null;
        observed.Clear();
    }

    public IReadOnlyList<GameEvent> Observe(string worldSession, IReadOnlyDictionary<string, string> defeated, DateTimeOffset now)
    {
        var events = new List<GameEvent>();
        var baseline = session != worldSession;
        if (baseline)
        {
            observed.Clear();
            session = worldSession;
        }

        foreach (var boss in defeated)
        {
            if (!observed.Add(boss.Key) || baseline) { continue; }
            var name = TextBounds.Utf8(boss.Value, 128);
            events.Add(new GameEvent
            {
                Type = "valheim.boss.defeated",
                Title = name + " was defeated",
                OccurredAt = now.ToString("O", CultureInfo.InvariantCulture),
                Data = new()
                {
                    ["boss"] = name,
                    ["progression_key"] = TextBounds.Utf8(boss.Key, 128),
                    ["source"] = "world_progression"
                }
            });
        }

        return events;
    }
}
