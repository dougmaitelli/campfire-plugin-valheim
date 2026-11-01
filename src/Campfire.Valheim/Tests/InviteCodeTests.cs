using System;
using System.Text.Json;
using Campfire.Valheim.Core;
using Xunit;

namespace Campfire.Valheim.Tests;

public sealed class InviteCodeTests
{
    private enum Backend { Steamworks, PlayFab }
    private enum LobbyState { Uninitialized, Creating, RegenerateJoinCode, Active }

    private sealed class Net
    {
        public static Backend m_onlineBackend = Backend.PlayFab;
        public static Net instance = new();
        public bool IsServer() => true;
        public object[] GetPeers() => Array.Empty<object>();
    }

    private sealed class Matchmaking
    {
        public static Matchmaking? m_instance;
        public LobbyState m_state = LobbyState.Active;
        public static string? JoinCode { get; set; }
    }

    [Fact]
    public void CodeIsReadLiveOnlyForAnActiveCrossplayLobby()
    {
        var reader = new InviteCodeReader(typeof(Net), typeof(Matchmaking));
        Net.m_onlineBackend = Backend.PlayFab;
        Matchmaking.m_instance = null;
        Matchmaking.JoinCode = "001234";
        Assert.Null(reader.Capture());
        Matchmaking.m_instance = new Matchmaking();
        Assert.Equal("001234", reader.Capture());
        var stateReader = new ValheimReader(typeof(Net), typeof(Matchmaking));
        var snapshot = stateReader.Capture(DateTimeOffset.UtcNow)!;
        Assert.Equal("001234", snapshot.StatePublication().State!.Fields["invite_code"]);
        Matchmaking.JoinCode = "567890";
        Assert.Equal("567890", reader.Capture());
        foreach (var state in new[] { LobbyState.Uninitialized, LobbyState.Creating, LobbyState.RegenerateJoinCode })
        {
            Matchmaking.m_instance.m_state = state;
            Assert.Null(reader.Capture());
        }

        Matchmaking.m_instance.m_state = LobbyState.Active;
        Net.m_onlineBackend = Backend.Steamworks;
        Assert.Null(reader.Capture());
        Assert.False(stateReader.Capture(DateTimeOffset.UtcNow)!.StatePublication().State!.Fields.ContainsKey("invite_code"));
        Assert.Null(new InviteCodeReader(typeof(object), typeof(Matchmaking)).Capture());
        Assert.Null(new InviteCodeReader(typeof(Net), null).Capture());
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("12345")]
    [InlineData("1234567")]
    [InlineData("12a456")]
    [InlineData("１２３４５６")]
    public void InvalidCodesAreOmitted(string? value)
    {
        var snapshot = Snapshot(value);
        Assert.Null(snapshot.InviteCode);
        Assert.False(snapshot.StatePublication().State!.Fields.ContainsKey("invite_code"));
    }

    [Fact]
    public void PublicationPreservesLeadingZeroesAndDropsCodesFromLaterSnapshots()
    {
        using var encoded = JsonDocument.Parse(Snapshot("001234").StatePublication().Encode());
        Assert.Equal("001234", encoded.RootElement.GetProperty("state").GetProperty("fields").GetProperty("invite_code").GetString());
        using var later = JsonDocument.Parse(Snapshot(null).StatePublication().Encode());
        Assert.False(later.RootElement.GetProperty("state").GetProperty("fields").TryGetProperty("invite_code", out _));
    }

    private static ServerSnapshot Snapshot(string? code) => new("world", "Meadows", 10, Array.Empty<PlayerSnapshot>(), DateTimeOffset.UtcNow, code);
}
