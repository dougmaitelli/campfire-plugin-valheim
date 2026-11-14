using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Runtime.Serialization;
using System.Runtime.Serialization.Json;
using System.Text;

namespace Campfire.Valheim.Core;

[DataContract]
public sealed class Publication
{
    [DataMember(Name = "version", Order = 0)]
    public int Version { get; set; } = 1;

    [DataMember(Name = "state", EmitDefaultValue = false, Order = 1)]
    public GameState? State { get; set; }

    [DataMember(Name = "event", EmitDefaultValue = false, Order = 2)]
    public GameEvent? Event { get; set; }

    [DataMember(Name = "registration", EmitDefaultValue = false, Order = 3)]
    public GameRegistration? Registration { get; set; }

    public byte[] Encode()
    {
        var payloads = (State != null ? 1 : 0) + (Event != null ? 1 : 0) + (Registration != null ? 1 : 0);
        if (Version != 1 || payloads != 1)
        {
            throw new InvalidOperationException("Version 1 requires exactly one registration, state or event.");
        }

        if (Registration != null && (Registration.Revision < 1 || Registration.Revision > 9_007_199_254_740_991))
        {
            throw new InvalidOperationException("Registration requires a positive, JSON-safe revision.");
        }

        var serializer = new DataContractJsonSerializer(typeof(Publication),
            new DataContractJsonSerializerSettings { UseSimpleDictionaryFormat = true, KnownTypes = new[] { typeof(string[]) } });
        using var output = new MemoryStream();
        serializer.WriteObject(output, this);
        if (output.Length > 16 * 1024)
        {
            throw new InvalidOperationException("Publication exceeds Spark's 16 KiB limit.");
        }

        return output.ToArray();
    }
}

[DataContract]
public sealed class GameState
{
    [DataMember(Name = "players")]
    public int Players { get; set; }

    [DataMember(Name = "capacity", EmitDefaultValue = false)]
    public int? Capacity { get; set; }

    [DataMember(Name = "fields")]
    public Dictionary<string, object> Fields { get; set; } = new();
}

[DataContract]
public sealed class GameEvent
{
    [DataMember(Name = "id")]
    public string ID { get; set; } = Guid.NewGuid().ToString("N");

    [DataMember(Name = "type")]
    public string Type { get; set; } = "game.announcement";

    [DataMember(Name = "occurred_at")]
    public string OccurredAt { get; set; } = DateTimeOffset.UtcNow.ToString("O", CultureInfo.InvariantCulture);

    [DataMember(Name = "title")]
    public string Title { get; set; } = "";

    [DataMember(Name = "data")]
    public Dictionary<string, string> Data { get; set; } = new();
}

public static class TextBounds
{
    public static string Utf8(string value, int maximumBytes)
    {
        var bytes = new byte[maximumBytes];
        Encoding.UTF8.GetEncoder().Convert(value.ToCharArray(), 0, value.Length,
            bytes, 0, bytes.Length, true, out _, out var written, out _);
        return Encoding.UTF8.GetString(bytes, 0, written);
    }
}
