using System;
using System.Globalization;
using System.Reflection;
using Campfire.Valheim.Core;
using HarmonyLib;

namespace Campfire.Valheim;

// Observe existing RPCs only. No custom RPCs, client installs, or gameplay changes.
internal sealed class DeathHooks : IDisposable
{
    private const BindingFlags Members = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static;
    private static readonly int OnDeathHash = StableHash("OnDeath");
    private static DeathHooks? current;
    private readonly Harmony harmony = new(Plugin.Guid + ".deaths");
    private readonly ValheimReader reader;
    private readonly Action<GameEvent> emit;
    private readonly Action<string> warn;
    private readonly bool includeNames;
    private readonly FieldInfo methodHash;
    private readonly FieldInfo target;
    private readonly MethodInfo isNone;
    private readonly FieldInfo sender;
    private readonly FieldInfo messageID;
    private DeathTracker tracker;

    private DeathHooks(Assembly game, ValheimReader reader, Action<GameEvent> emit, Action<string> warn, bool includeNames)
    {
        this.reader = reader;
        this.emit = emit;
        this.warn = warn;
        this.includeNames = includeNames;
        tracker = new DeathTracker(includeNames);
        var rpc = RequiredType(game, "ZRoutedRpc");
        var data = rpc.GetNestedType("RoutedRPCData", Members) ?? throw new MissingMemberException("ZRoutedRpc.RoutedRPCData");
        methodHash = RequiredField(data, "m_methodHash");
        target = RequiredField(data, "m_targetZDO");
        isNone = RequiredMethod(target.FieldType, "IsNone");
        sender = RequiredField(data, "m_senderPeerID");
        messageID = RequiredField(data, "m_msgID");
        var handle = rpc.GetMethod("HandleRoutedRPC", Members, null, new[] { data }, null)
            ?? throw new MissingMethodException("ZRoutedRpc.HandleRoutedRPC");
        current = this;
        try
        {
            harmony.Patch(handle, postfix: new HarmonyMethod(typeof(DeathHooks), nameof(AfterRPC)));
        }
        catch
        {
            Dispose();
            throw;
        }
    }

    public static DeathHooks? TryInstall(Assembly game, ValheimReader reader, Action<GameEvent> emit, Action<string> warn, bool includeNames)
    {
        try { return new DeathHooks(game, reader, emit, warn, includeNames); }
        catch (Exception error)
        {
            warn("Death reporting unavailable for this Valheim API; state and joins/leaves continue: " + error.Message);
            return null;
        }
    }

    public void Reset() => tracker = new DeathTracker(includeNames);

    private static void AfterRPC(object __0)
    {
        var hooks = current;
        if (hooks == null || __0 == null) { return; }
        try
        {
            if (Convert.ToInt32(hooks.methodHash.GetValue(__0), CultureInfo.InvariantCulture) != OnDeathHash) { return; }
            var targetID = hooks.target.GetValue(__0);
            var senderID = Convert.ToInt64(hooks.sender.GetValue(__0), CultureInfo.InvariantCulture);
            if (targetID == null || (bool)hooks.isNone.Invoke(targetID, null)) { return; }
            var player = hooks.reader.FindPlayer(targetID, senderID);
            if (player == null) { return; }
            var key = senderID.ToString(CultureInfo.InvariantCulture) + ":" + hooks.messageID.GetValue(__0);
            var gameEvent = hooks.tracker.ObserveDeath(key, player.Name, DateTimeOffset.UtcNow);
            if (gameEvent != null) { hooks.emit(gameEvent); }
        }
        catch (Exception error) { hooks.warn("Could not observe a death broadcast: " + error.Message); }
    }

    private static int StableHash(string text)
    {
        unchecked
        {
            var first = 5381;
            var second = first;
            for (var index = 0; index < text.Length; index += 2)
            {
                first = (first * 33) ^ text[index];
                if (index == text.Length - 1) { break; }
                second = (second * 33) ^ text[index + 1];
            }

            return first + second * 1566083941;
        }
    }

    private static Type RequiredType(Assembly assembly, string name) => assembly.GetType(name) ?? throw new MissingMemberException(name);
    private static FieldInfo RequiredField(Type type, string name) => type.GetField(name, Members) ?? throw new MissingFieldException(type.FullName, name);
    private static MethodInfo RequiredMethod(Type type, string name) => type.GetMethod(name, Members, null, Type.EmptyTypes, null) ?? throw new MissingMethodException(type.FullName, name);

    public void Dispose()
    {
        if (current == this) { current = null; }
        harmony.UnpatchSelf();
    }
}
