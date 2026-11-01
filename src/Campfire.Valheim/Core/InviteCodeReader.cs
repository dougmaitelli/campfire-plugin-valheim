using System;
using System.Reflection;

namespace Campfire.Valheim.Core;

// Optional API: never create a matchmaking instance just to read its state.
internal sealed class InviteCodeReader
{
    private const BindingFlags Members = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static | BindingFlags.Instance;
    private readonly FieldInfo? backend;
    private readonly FieldInfo? instance;
    private readonly FieldInfo? state;
    private readonly PropertyInfo? code;

    internal InviteCodeReader(Type net, Type? matchmaking)
    {
        backend = net.GetField("m_onlineBackend", Members);
        instance = matchmaking?.GetField("m_instance", Members);
        state = matchmaking?.GetField("m_state", Members);
        code = matchmaking?.GetProperty("JoinCode", Members);
    }

    internal string? Capture()
    {
        try
        {
            if (backend?.GetValue(null)?.ToString() != "PlayFab") { return null; }
            var lobby = instance?.GetValue(null);
            if (lobby == null || state?.GetValue(lobby)?.ToString() != "Active") { return null; }
            return Validate(code?.GetValue(null) as string);
        }
        catch (Exception)
        {
            // A missing or changed matchmaking API must not interrupt player readings.
            return null;
        }
    }

    internal static string? Validate(string? value)
    {
        if (value == null || value.Length != 6) { return null; }
        foreach (var digit in value)
        {
            if (digit < '0' || digit > '9') { return null; }
        }

        return value;
    }
}
