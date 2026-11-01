using System;
using System.Text.Json;
using Campfire.Valheim.Core;
using Xunit;

namespace Campfire.Valheim.Tests;

public sealed class DeathTrackerTests
{
    private static readonly DateTimeOffset Now = DateTimeOffset.Parse("2026-10-24T00:00:00Z");

    [Fact]
    public void DeathPublishesOnlyPlayerIdentityAndDeduplicatesBroadcasts()
    {
        var tracker = new DeathTracker();
        var death = tracker.ObserveDeath("rpc-1", "Alex", Now);
        Assert.NotNull(death);
        Assert.Equal("player.died", death.Type);
        Assert.Equal("Alex died", death.Title);
        Assert.Equal(Now, DateTimeOffset.Parse(death.OccurredAt));
        using var encoded = JsonDocument.Parse(new Publication { Event = death }.Encode());
        var data = encoded.RootElement.GetProperty("event").GetProperty("data");
        Assert.Equal("Alex", data.GetProperty("player").GetString());
        Assert.Equal(1, data.GetPropertyCount());
        Assert.Null(tracker.ObserveDeath("rpc-1", "Alex", Now.AddSeconds(2)));
        Assert.NotNull(tracker.ObserveDeath("rpc-2", "Alex", Now.AddSeconds(3)));
    }

    [Fact]
    public void DeduplicationHistoryIsBounded()
    {
        var tracker = new DeathTracker();
        for (var index = 0; index <= 512; index++)
        {
            Assert.NotNull(tracker.ObserveDeath("rpc-" + index, "Alex", Now));
        }

        Assert.Null(tracker.ObserveDeath("rpc-512", "Alex", Now));
        Assert.NotNull(tracker.ObserveDeath("rpc-0", "Alex", Now));
    }

    [Fact]
    public void PrivacySettingOmitsAllPlayerData()
    {
        var tracker = new DeathTracker(includeNames: false);
        var death = tracker.ObserveDeath("rpc", "Alex", Now);
        Assert.Equal("A player died", death!.Title);
        using var encoded = JsonDocument.Parse(new Publication { Event = death }.Encode());
        Assert.Equal(0, encoded.RootElement.GetProperty("event").GetProperty("data").GetPropertyCount());
    }
}
