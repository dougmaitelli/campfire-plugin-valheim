using System;
using System.Text;
using System.Text.Json;
using Campfire.Valheim.Core;
using Xunit;

namespace Campfire.Valheim.Tests;

public sealed class GameRegistrationTests
{
    [Fact]
    public void ManifestRegistersAllValheimFieldLabelsWithoutRuntimeValues()
    {
        var bytes = ValheimRegistration.CreatePublication().Encode();
        Assert.True(bytes.Length < 16 * 1024);
        using var document = JsonDocument.Parse(bytes);
        var root = document.RootElement;
        Assert.Equal(1, root.GetProperty("version").GetInt32());
        Assert.False(root.TryGetProperty("state", out _));
        Assert.False(root.TryGetProperty("event", out _));
        Assert.False(root.TryGetProperty("server_id", out _));
        var registration = root.GetProperty("registration");
        Assert.Equal(2, ValheimRegistration.Revision);
        Assert.Equal(ValheimRegistration.Revision, registration.GetProperty("revision").GetInt64());
        var state = registration.GetProperty("state_fields");
        Assert.Equal("World name", state.GetProperty("world").GetString());
        Assert.Equal("Invite code", state.GetProperty("invite_code").GetString());
        Assert.Equal("Online players", state.GetProperty("player_names").GetString());
        var events = registration.GetProperty("event_fields");
        foreach (var type in new[] { "player.joined", "player.left", "player.died" })
        {
            Assert.Equal("Player name", events.GetProperty(type).GetProperty("player").GetString());
        }

        Assert.Equal("Boss", events.GetProperty("valheim.boss.defeated").GetProperty("boss").GetString());
        Assert.Equal("Progression key", events.GetProperty("valheim.boss.defeated").GetProperty("progression_key").GetString());
        Assert.Equal("Source", events.GetProperty("valheim.boss.defeated").GetProperty("source").GetString());
        Assert.Equal("Message", events.GetProperty("game.announcement").GetProperty("message").GetString());
        Assert.Empty(events.GetProperty("valheim.integration.started").EnumerateObject());
        Assert.DoesNotContain("occurred_at", Encoding.UTF8.GetString(bytes));
    }

    [Fact]
    public void ManifestIsStableAndDoesNotShareMutableDefinitions()
    {
        var first = ValheimRegistration.CreatePublication();
        var original = first.Encode();
        first.Registration!.StateFields["world"] = "Must not change defaults";
        first.Registration.EventFields["player.joined"]["player"] = "Must not change defaults";
        Assert.Equal(original, ValheimRegistration.CreatePublication().Encode());
    }

    [Theory]
    [InlineData(0L)]
    [InlineData(-1L)]
    [InlineData(9_007_199_254_740_992L)]
    public void RejectsUnsafeRegistrationRevisions(long revision)
    {
        var publication = ValheimRegistration.CreatePublication();
        publication.Registration!.Revision = revision;
        Assert.Throws<InvalidOperationException>(() => publication.Encode());
    }

    [Fact]
    public void RegistrationCannotBeMixedWithStateOrEvents()
    {
        var publication = ValheimRegistration.CreatePublication();
        publication.State = new GameState();
        Assert.Throws<InvalidOperationException>(() => publication.Encode());
        publication.State = null;
        publication.Event = new GameEvent { Title = "Hello" };
        Assert.Throws<InvalidOperationException>(() => publication.Encode());
    }
}
