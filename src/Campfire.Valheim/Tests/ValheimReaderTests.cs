using System;
using System.Collections.Generic;
using Campfire.Valheim.Core;
using Xunit;

namespace Campfire.Valheim.Tests;

[Collection("Valheim API")]
public sealed class ValheimReaderTests
{
    public sealed class Peer
    {
        public long m_uid = 1;
        public string m_playerName = "Alex";
        public string m_characterID = "character";
        public bool Ready = true;
        public bool IsReady() => Ready;
    }

    public sealed class Net
    {
        public static Net? instance;
        public int m_serverPlayerLimit = 20;
        public bool Server = true;
        public string? World = "Meadows";
        public List<Peer> Peers = new();
        private bool IsServer() => Server;
        private List<Peer> GetPeers() => Peers;
        private string? GetWorldName() => World;
        private long GetWorldUID() => 1;
    }

    public sealed class MinimalNet
    {
        public static MinimalNet instance = new();
        private bool IsServer() => true;
        private List<Peer> GetPeers() => new();
    }

    public sealed class PropertyNet
    {
        public static PropertyNet? instance { get; set; }
        public List<Peer> Peers = new();
        private bool IsServer() => true;
        private List<Peer> GetPeers() => Peers;
    }

    [Fact]
    public void SingletonPropertyIsReadLiveForSnapshotsAndDeathLookup()
    {
        PropertyNet.instance = null;
        var reader = new ValheimReader(typeof(PropertyNet));
        Assert.Null(reader.Capture(DateTimeOffset.UtcNow));
        Assert.Null(reader.FindPlayer("character", 1));

        PropertyNet.instance = new PropertyNet { Peers = new() { new Peer() } };
        Assert.Single(reader.Capture(DateTimeOffset.UtcNow)!.Players);
        Assert.NotNull(reader.FindPlayer("character", 1));

        PropertyNet.instance = new PropertyNet();
        Assert.Empty(reader.Capture(DateTimeOffset.UtcNow)!.Players);
        Assert.Null(reader.FindPlayer("character", 1));

        PropertyNet.instance = null;
        Assert.Null(reader.Capture(DateTimeOffset.UtcNow));
        Assert.Null(reader.FindPlayer("character", 1));
    }

    [Fact]
    public void CaptureCountsReadyPeersAndCopiesGameState()
    {
        Net.instance = new Net { Peers = new() { new Peer(), new Peer { m_uid = 2, Ready = false } } };
        var reader = new ValheimReader(typeof(Net));
        var snapshot = reader.Capture(DateTimeOffset.UtcNow);
        Assert.NotNull(snapshot);
        Assert.Single(snapshot.Players);
        Assert.Equal(20, snapshot.Capacity);
        Net.instance.Peers[0].m_playerName = "Changed";
        Assert.Equal("Alex", snapshot.Players[0].Name);
        Assert.Null(reader.FindPlayer("character", 999));
        Assert.NotNull(reader.FindPlayer("character", 1));
        Assert.Null(reader.FindPlayer("another-character", 1));
        Assert.Equal(30, reader.Capture(DateTimeOffset.UtcNow, 30)!.Capacity);
        Net.instance.Server = false;
        Assert.Null(reader.Capture(DateTimeOffset.UtcNow));
        Assert.Null(reader.FindPlayer("character", 1));
        Net.instance = null;
        Assert.Null(reader.Capture(DateTimeOffset.UtcNow));
    }

    [Fact]
    public void UnloadedWorldDoesNotProduceFalseEmptyState()
    {
        Net.instance = new Net { World = null };
        Assert.Null(new ValheimReader(typeof(Net)).Capture(DateTimeOffset.UtcNow));
    }

    [Fact]
    public void MissingOptionalAPIsOmitFieldsButMissingRequiredAPIsFailClearly()
    {
        var snapshot = new ValheimReader(typeof(MinimalNet)).Capture(DateTimeOffset.UtcNow);
        Assert.NotNull(snapshot);
        Assert.Null(snapshot.World);
        Assert.Null(snapshot.Capacity);
        Assert.Throws<MissingFieldException>(() => new ValheimReader(typeof(object)));
    }
}
