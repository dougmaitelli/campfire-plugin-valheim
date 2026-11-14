using System;
using System.Linq;
using System.Text;
using System.Text.Json;
using Campfire.Valheim.Core;
using Xunit;

namespace Campfire.Valheim.Tests;

public sealed class PlayerListTests
{
    private static readonly DateTimeOffset Now = DateTimeOffset.Parse("2026-10-24T00:00:00Z");

    [Fact]
    public void StatePublishesAnOrderedArrayOfDisplayNamesWithoutIdentities()
    {
        var snapshot = new ServerSnapshot("private-session", "Meadows", 10, new[]
        {
            new PlayerSnapshot(987654321, "Sam <guest>"),
            new PlayerSnapshot(123456789, "Alex"),
            new PlayerSnapshot(2, "Alex"),
            new PlayerSnapshot(987654321, "Sam <guest>")
        }, Now);
        var bytes = snapshot.StatePublication().Encode();
        using var document = JsonDocument.Parse(bytes);
        var state = document.RootElement.GetProperty("state");
        Assert.Equal(3, state.GetProperty("players").GetInt32());
        Assert.Equal(new[] { "Alex", "Alex", "Sam <guest>" },
            state.GetProperty("fields").GetProperty("player_names").EnumerateArray().Select(item => item.GetString()));
        Assert.Equal("Meadows", state.GetProperty("fields").GetProperty("world").GetString());
        var json = Encoding.UTF8.GetString(bytes);
        Assert.DoesNotContain("987654321", json);
        Assert.DoesNotContain("123456789", json);
        Assert.DoesNotContain("private-session", json);
        Assert.DoesNotContain("__type", json);
        Assert.DoesNotContain("field_labels", json);
    }

    [Fact]
    public void RosterOrderDoesNotChangeWhenPeerEnumerationOrderChanges()
    {
        var players = new[] { new PlayerSnapshot(2, "Sam"), new PlayerSnapshot(1, "Alex") };
        var first = new ServerSnapshot("one", "World", 10, players, Now);
        var second = new ServerSnapshot("one", "World", 10, players.Reverse(), Now);
        Assert.Equal(first.StatePublication().Encode(), second.StatePublication().Encode());
    }

    [Fact]
    public void EmptyServerPublishesAnEmptyArrayAndPrivacyOmitsTheField()
    {
        var snapshot = new ServerSnapshot("one", null, null, Array.Empty<PlayerSnapshot>(), Now);
        using var empty = JsonDocument.Parse(snapshot.StatePublication().Encode());
        Assert.Equal(0, empty.RootElement.GetProperty("state").GetProperty("players").GetInt32());
        Assert.Empty(empty.RootElement.GetProperty("state").GetProperty("fields").GetProperty("player_names").EnumerateArray());
        snapshot = new ServerSnapshot("one", "World", 10, new[] { new PlayerSnapshot(1, "Private name") }, Now);
        using var hidden = JsonDocument.Parse(snapshot.StatePublication(includeNames: false).Encode());
        Assert.Equal(1, hidden.RootElement.GetProperty("state").GetProperty("players").GetInt32());
        Assert.False(hidden.RootElement.GetProperty("state").GetProperty("fields").TryGetProperty("player_names", out _));
        Assert.DoesNotContain("Private name", hidden.RootElement.GetRawText());
    }

    [Fact]
    public void RosterLimitDoesNotReduceTheTotalPlayerCount()
    {
        var players = Enumerable.Range(1, 100).Select(id => new PlayerSnapshot(id, "Player " + id.ToString("D3")));
        var snapshot = new ServerSnapshot("one", null, 100, players, Now);
        using var document = JsonDocument.Parse(snapshot.StatePublication().Encode());
        var state = document.RootElement.GetProperty("state");
        Assert.Equal(100, state.GetProperty("players").GetInt32());
        Assert.Equal(64, state.GetProperty("fields").GetProperty("player_names").GetArrayLength());
    }

    [Theory]
    [InlineData("\u0001")]
    [InlineData("\"\\/")]
    [InlineData("🪓")]
    [InlineData("\u2028\u2029")]
    public void EscapedAndUnicodeNamesStayWithinTheEntirePublicationLimit(string text)
    {
        var longText = string.Concat(Enumerable.Repeat(text, 1024));
        var players = Enumerable.Range(1, 100).Select(id => new PlayerSnapshot(id, longText));
        var snapshot = new ServerSnapshot("one", longText, 100, players, Now, "001234");
        var bytes = snapshot.StatePublication().Encode();
        Assert.True(bytes.Length <= 16 * 1024);
        using var document = JsonDocument.Parse(bytes);
        var state = document.RootElement.GetProperty("state");
        Assert.Equal(100, state.GetProperty("players").GetInt32());
        var names = state.GetProperty("fields").GetProperty("player_names");
        Assert.InRange(names.GetArrayLength(), 1, 64);
        Assert.All(names.EnumerateArray(), item =>
        {
            Assert.True(Encoding.UTF8.GetByteCount(item.GetString()!) <= 128);
            Assert.DoesNotContain("�", item.GetString()!);
        });
    }

    [Fact]
    public void PublicationsDoNotShareTheirRosterWithTheSnapshot()
    {
        var snapshot = new ServerSnapshot("one", null, null, new[] { new PlayerSnapshot(1, "Alex") }, Now);
        var publication = snapshot.StatePublication();
        Assert.IsType<string[]>(publication.State!.Fields["player_names"])[0] = "Mutation";
        using var document = JsonDocument.Parse(snapshot.StatePublication().Encode());
        Assert.Equal("Alex", document.RootElement.GetProperty("state").GetProperty("fields").GetProperty("player_names")[0].GetString());
    }
}
