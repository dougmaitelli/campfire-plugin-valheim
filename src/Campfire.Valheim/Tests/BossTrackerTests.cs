using System;
using System.Collections.Generic;
using System.Text.Json;
using Campfire.Valheim.Core;
using Xunit;

namespace Campfire.Valheim.Tests;

public sealed class BossTrackerTests
{
    private static readonly DateTimeOffset Now = DateTimeOffset.Parse("2026-10-30T12:00:00Z");

    [Fact]
    public void ExistingDefeatsAreBaselineAndNewDefeatsProduceAnEvent()
    {
        var tracker = new BossTracker();
        var defeated = new Dictionary<string, string> { ["defeated_eikthyr"] = "Eikthyr" };
        Assert.Empty(tracker.Observe("world", defeated, Now));
        defeated.Add("defeated_gdking", "The Elder");
        var gameEvent = Assert.Single(tracker.Observe("world", defeated, Now.AddSeconds(2)));
        Assert.Equal("valheim.boss.defeated", gameEvent.Type);
        Assert.Equal("The Elder was defeated", gameEvent.Title);
        Assert.Equal(Now.AddSeconds(2), DateTimeOffset.Parse(gameEvent.OccurredAt));
        using var encoded = JsonDocument.Parse(new Publication { Event = gameEvent }.Encode());
        var data = encoded.RootElement.GetProperty("event").GetProperty("data");
        Assert.Equal("defeated_gdking", data.GetProperty("progression_key").GetString());
        Assert.Equal("world_progression", data.GetProperty("source").GetString());
        Assert.False(data.TryGetProperty("player", out _));
        Assert.Empty(tracker.Observe("world", defeated, Now));
    }

    [Fact]
    public void RemovalAndReadditionDoNotRepeatNotificationsAndSessionChangesRebaseline()
    {
        var tracker = new BossTracker();
        var empty = new Dictionary<string, string>();
        var defeated = new Dictionary<string, string> { ["defeated_eikthyr"] = "Eikthyr" };
        tracker.Observe("one", empty, Now);
        Assert.Single(tracker.Observe("one", defeated, Now));
        Assert.Empty(tracker.Observe("one", empty, Now));
        Assert.Empty(tracker.Observe("one", defeated, Now));
        Assert.Empty(tracker.Observe("two", defeated, Now));
        tracker.Reset();
        Assert.Empty(tracker.Observe("two", defeated, Now));
    }

    [Fact]
    public void MultipleBossesAndPreviouslyUnknownBossKeysAreSupported()
    {
        var tracker = new BossTracker();
        tracker.Observe("world", new Dictionary<string, string>(), Now);
        var defeated = new Dictionary<string, string>
        {
            ["defeated_eikthyr"] = "Eikthyr",
            ["modded_boss_key"] = "A new boss"
        };
        Assert.Equal(2, tracker.Observe("world", defeated, Now).Count);
    }
}
