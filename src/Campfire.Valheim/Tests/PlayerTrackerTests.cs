using System;
using System.Linq;
using System.Text.Json;
using Campfire.Valheim.Core;
using Xunit;

namespace Campfire.Valheim.Tests;

public sealed class PlayerTrackerTests
{
    private static readonly DateTimeOffset Now = DateTimeOffset.Parse("2026-10-24T00:00:00Z");
    private static ServerSnapshot Snapshot(string session, params PlayerSnapshot[] players) => new(session, "Meadows", 10, players, Now);

    [Fact]
    public void InitialPlayersAreBaselineAndChangesUseSessionIDs()
    {
        var tracker = new PlayerTracker();
        var alex = new PlayerSnapshot(1, "Alex");
        Assert.Equal("valheim.integration.started", Assert.Single(tracker.Observe(Snapshot("one", alex))).Type);
        Assert.Empty(tracker.Observe(Snapshot("one", new PlayerSnapshot(1, "Renamed"))));
        var joined = Assert.Single(tracker.Observe(Snapshot("one", alex, new PlayerSnapshot(2, "Alex"))));
        Assert.Equal("player.joined", joined.Type);
        var left = Assert.Single(tracker.Observe(Snapshot("one", alex)));
        Assert.Equal("player.left", left.Type);
        Assert.NotEqual(joined.ID, left.ID);
        Assert.Single(tracker.Observe(Snapshot("two", alex)));
        tracker.Reset();
        Assert.Equal("valheim.integration.started", Assert.Single(tracker.Observe(Snapshot("two", alex))).Type);
    }

    [Fact]
    public void StateUsesOptionalFieldsAndOmitsUnknownCapacity()
    {
        var snapshot = new ServerSnapshot("one", null, null, new[] { new PlayerSnapshot(1, "Alex"), new PlayerSnapshot(1, "Alex") }, Now);
        using var publication = JsonDocument.Parse(snapshot.StatePublication().Encode());
        var state = publication.RootElement.GetProperty("state");
        Assert.Equal(1, state.GetProperty("players").GetInt32());
        Assert.False(state.TryGetProperty("capacity", out _));
        Assert.False(state.GetProperty("fields").TryGetProperty("world", out _));
        Assert.False(state.GetProperty("fields").TryGetProperty("map", out _));
        Assert.False(publication.RootElement.TryGetProperty("event", out _));
    }

    [Fact]
    public void UnicodeNamesStayWithinProtocolLimitsAndPrivacyHidesNames()
    {
        var name = string.Concat(Enumerable.Repeat("🪓", 100));
        var player = new PlayerSnapshot(1, name);
        Assert.Equal(128, System.Text.Encoding.UTF8.GetByteCount(player.Name));
        Assert.DoesNotContain("�", player.Name);
        var tracker = new PlayerTracker(includeNames: false);
        tracker.Observe(Snapshot("one"));
        var joined = Assert.Single(tracker.Observe(Snapshot("one", player)));
        Assert.Equal("A player joined the server", joined.Title);
        Assert.Empty(joined.Data);
    }
}
