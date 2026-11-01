using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.Reflection;
using System.Runtime.CompilerServices;

namespace Campfire.Valheim.Core;

// Resolve game members once. No game assemblies are bundled or patched.
// Required API changes fail explicitly; optional information is omitted.
public sealed class ValheimReader
{
    private const BindingFlags Members = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static;
    private readonly Func<object?> getInstance;
    private readonly MethodInfo isServer;
    private readonly MethodInfo getPeers;
    private readonly MethodInfo? getWorldName;
    private readonly MethodInfo? getWorldUID;
    private readonly FieldInfo? playerLimit;
    private readonly InviteCodeReader inviteCode;
    private Type? peerType;
    private MethodInfo? isReady;
    private FieldInfo? peerID;
    private FieldInfo? peerName;
    private FieldInfo? peerCharacter;

    public ValheimReader(Type znet) : this(znet, znet.Assembly.GetType("ZPlayFabMatchmaking"))
    { }

    internal ValheimReader(Type znet, Type? matchmaking)
    {
        inviteCode = new InviteCodeReader(znet, matchmaking);
        const BindingFlags singletonMembers = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static;
        var instanceProperty = znet.GetProperty("instance", singletonMembers);
        var instanceGetter = instanceProperty?.GetGetMethod(nonPublic: true);
        if (instanceGetter != null && instanceProperty!.GetIndexParameters().Length == 0)
        {
            getInstance = () => instanceGetter.Invoke(null, null);
        }
        else
        {
            var instanceField = znet.GetField("instance", singletonMembers)
                ?? throw new MissingFieldException(znet.FullName, "instance");
            getInstance = () => instanceField.GetValue(null);
        }
        isServer = RequiredMethod(znet, "IsServer");
        getPeers = RequiredMethod(znet, "GetPeers");
        getWorldName = znet.GetMethod("GetWorldName", Members, null, Type.EmptyTypes, null);
        getWorldUID = znet.GetMethod("GetWorldUID", Members, null, Type.EmptyTypes, null);
        playerLimit = znet.GetField("m_serverPlayerLimit", Members);
    }

    // Call only from Unity's main thread. Never retain peers or the ZNet instance.
    public ServerSnapshot? Capture(DateTimeOffset now, int capacityOverride = 0)
    {
        var net = getInstance();
        if (net == null || !(bool)isServer.Invoke(net, null)!)
        {
            return null;
        }

        var world = getWorldName?.Invoke(net, null) as string;
        if (getWorldName != null && string.IsNullOrWhiteSpace(world))
        {
            return null; // The server has not loaded a world yet.
        }

        var players = new List<PlayerSnapshot>();
        var peers = getPeers.Invoke(net, null) as IEnumerable
            ?? throw new InvalidOperationException("ZNet.GetPeers no longer returns a collection.");
        foreach (var peer in peers)
        {
            if (peer == null)
            {
                continue;
            }

            ResolvePeer(peer.GetType());
            if ((bool)isReady!.Invoke(peer, null)!)
            {
                players.Add(new PlayerSnapshot(Convert.ToInt64(peerID!.GetValue(peer), CultureInfo.InvariantCulture),
                    peerName!.GetValue(peer) as string ?? "Player"));
            }
        }

        var capacity = capacityOverride > 0 ? capacityOverride : playerLimit?.GetValue(playerLimit.IsStatic ? null : net) as int?;
        var worldID = getWorldUID?.Invoke(net, null);
        var session = RuntimeHelpers.GetHashCode(net).ToString(CultureInfo.InvariantCulture) + ":" + worldID + ":" + world;
        return new ServerSnapshot(session, world, capacity, players, now, inviteCode.Capture());
    }

    // A death broadcast must refer to its reported sender's current character.
    // Read the live peers here: a player may spawn and die between polling samples.
    public PlayerSnapshot? FindPlayer(object character, long? sender = null)
    {
        var net = getInstance();
        if (net == null || !(bool)isServer.Invoke(net, null)!) { return null; }
        var peers = getPeers.Invoke(net, null) as IEnumerable;
        if (peers == null) { return null; }
        foreach (var peer in peers)
        {
            if (peer == null) { continue; }
            ResolvePeer(peer.GetType());
            if (peerCharacter == null) { throw new MissingFieldException(peer.GetType().FullName, "m_characterID"); }
            if ((bool)isReady!.Invoke(peer, null)!
                && (sender == null || Convert.ToInt64(peerID!.GetValue(peer), CultureInfo.InvariantCulture) == sender.Value)
                && Equals(peerCharacter?.GetValue(peer), character))
            {
                return new PlayerSnapshot(Convert.ToInt64(peerID!.GetValue(peer), CultureInfo.InvariantCulture), peerName!.GetValue(peer) as string ?? "Player");
            }
        }

        return null;
    }

    private void ResolvePeer(Type type)
    {
        if (peerType == type)
        {
            return;
        }

        isReady = RequiredMethod(type, "IsReady");
        peerID = type.GetField("m_uid", Members) ?? throw new MissingFieldException(type.FullName, "m_uid");
        peerName = type.GetField("m_playerName", Members) ?? throw new MissingFieldException(type.FullName, "m_playerName");
        peerCharacter = type.GetField("m_characterID", Members);
        peerType = type;
    }

    private static MethodInfo RequiredMethod(Type type, string name) =>
        type.GetMethod(name, Members, null, Type.EmptyTypes, null) ?? throw new MissingMethodException(type.FullName, name);
}
