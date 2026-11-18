using System;
using System.Collections.Generic;
using Campfire.Valheim.Core;
using Xunit;

namespace Campfire.Valheim.Tests;

public sealed class BossReaderTests
{
    // Per-test types keep the reflection adapter independent of the Unity runtime.
    private sealed class Zone
    {
        public static Zone? instance { get; set; }
        public readonly HashSet<string> Keys = new();
        public bool GetGlobalKey(string key) => Keys.Contains(key);
    }

    private sealed class Scene
    {
        public static Scene? instance { get; set; }
        public readonly List<Prefab> m_prefabs = new();
    }

    private sealed class Character
    {
        public string m_defeatSetGlobalKey = "";
        public string m_name = "";
        public bool Boss;
        public bool IsBoss() => Boss;
    }

    private sealed class Localization
    {
        public static Localization instance { get; } = new();
        public string Localize(string name) => name == "$frozen_king" ? "The Frozen King" : "[" + name.TrimStart('$') + "]";
    }

    private sealed class Prefab
    {
        public string name { get; set; } = "Custom Boss";
        public Character? Character;
        public object? GetComponent(Type type) => type == typeof(Character) ? Character : null;
    }

    [Fact]
    public void OnlyTheFinalFrozenKingPhaseProducesAnEventWithItsLocalizedName()
    {
        Zone.instance = new Zone();
        Scene.instance = new Scene();
        foreach (var phase in new[] { "FrozenKing", "FrozenKing_p3" })
        {
            Scene.instance.m_prefabs.Add(new Prefab
            {
                name = phase,
                Character = new Character { Boss = true, m_defeatSetGlobalKey = "defeated_" + phase, m_name = "$frozen_king" }
            });
        }

        var reader = new BossReader(typeof(Zone), typeof(Scene), typeof(Character), typeof(Localization));
        var tracker = new BossTracker();
        var now = DateTimeOffset.UtcNow;
        Assert.Empty(tracker.Observe("world", reader.Capture()!, now));
        Zone.instance.Keys.Add("defeated_FrozenKing");
        Assert.Empty(tracker.Observe("world", reader.Capture()!, now));

        Zone.instance.Keys.Add("defeated_FrozenKing_p3");
        var gameEvent = Assert.Single(tracker.Observe("world", reader.Capture()!, now));
        Assert.Equal("The Frozen King was defeated", gameEvent.Title);
        Assert.Equal("The Frozen King", gameEvent.Data["boss"]);
        Assert.Empty(tracker.Observe("world", reader.Capture()!, now));
        Zone.instance = null;
        Scene.instance = null;
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void HighestNumericProgressionPhaseIsSelectedRegardlessOfDiscoveryOrder(bool reverse)
    {
        Zone.instance = new Zone();
        Scene.instance = new Scene();
        var keys = new List<string>
        {
            "defeated_custom_p3", "defeated_custom", "defeated_custom_P10",
            "defeated_other", "defeated_other_p4", "defeated_standalone_p5",
            "defeated_custom_preview", "defeated_custom_p0", "defeated_custom_p-1",
            "defeated_custom_p", "defeated_custom_p999999999999999999999"
        };
        if (reverse) { keys.Reverse(); }
        foreach (var key in keys)
        {
            Scene.instance.m_prefabs.Add(new Prefab
            {
                // Group by progression keys, not prefab names.
                name = "Unrelated prefab " + key,
                Character = new Character { Boss = true, m_defeatSetGlobalKey = key, m_name = "Display " + key }
            });
        }

        var reader = new BossReader(typeof(Zone), typeof(Scene), typeof(Character));
        Zone.instance.Keys.UnionWith(new[] { "defeated_custom", "defeated_custom_p3", "defeated_other" });
        Assert.Empty(reader.Capture()!); // Only final phases are monitored, not the highest defeated phase.
        Zone.instance.Keys.UnionWith(keys);
        var bosses = reader.Capture()!;
        Assert.Equal(8, bosses.Count);
        Assert.Equal("Display defeated_custom_P10", bosses["defeated_custom_p10"]);
        Assert.Contains("defeated_other_p4", bosses.Keys);
        Assert.Contains("defeated_standalone_p5", bosses.Keys);
        foreach (var key in new[] { "defeated_custom_preview", "defeated_custom_p0", "defeated_custom_p-1", "defeated_custom_p", "defeated_custom_p999999999999999999999" })
        {
            Assert.Contains(key, bosses.Keys);
        }

        Zone.instance = null;
        Scene.instance = null;
    }

    [Theory]
    [InlineData("A modded boss", true, "A modded boss")]
    [InlineData("$frozen_king", true, "The Frozen King")]
    [InlineData("$missing_translation", true, "Custom_p3")]
    [InlineData("$frozen_king", false, "Custom_p3")]
    [InlineData("", true, "Custom_p3")]
    public void ModdedBossNamesUseCharacterNamesWithSafeFallbacks(string name, bool localize, string expected)
    {
        Zone.instance = new Zone();
        Scene.instance = new Scene();
        Scene.instance.m_prefabs.Add(new Prefab
        {
            name = "Custom_p3",
            Character = new Character { Boss = true, m_defeatSetGlobalKey = "custom_key", m_name = name }
        });
        Zone.instance.Keys.Add("custom_key");
        var reader = new BossReader(typeof(Zone), typeof(Scene), typeof(Character), localize ? typeof(Localization) : null);
        Assert.Equal(expected, Assert.Single(reader.Capture()!).Value);
        Zone.instance = null;
        Scene.instance = null;
    }

    [Fact]
    public void ReadsOnlyBossProgressionAndRefreshesWhenTheSceneChanges()
    {
        Zone.instance = new Zone();
        Scene.instance = new Scene();
        var reader = new BossReader(typeof(Zone), typeof(Scene), typeof(Character));
        Assert.Null(reader.Capture()); // Scene still loading: do not establish a baseline.
        Scene.instance.m_prefabs.Add(new Prefab { Character = new Character { Boss = true, m_defeatSetGlobalKey = "defeated_gdking" } });
        Scene.instance.m_prefabs.Add(new Prefab { Character = new Character { m_defeatSetGlobalKey = "unrelated" } });
        Scene.instance.m_prefabs.Add(new Prefab());
        Assert.Empty(reader.Capture()!);
        Zone.instance.Keys.UnionWith(new[] { "defeated_gdking", "unrelated" });
        Assert.Equal("The Elder", Assert.Single(reader.Capture()!).Value);

        Scene.instance = new Scene();
        Scene.instance.m_prefabs.Add(new Prefab { Character = new Character { Boss = true, m_defeatSetGlobalKey = "custom_key" } });
        Zone.instance.Keys.Add("custom_key");
        var boss = Assert.Single(reader.Capture()!);
        Assert.Equal("custom_key", boss.Key);
        Assert.Equal("Custom Boss", boss.Value);
        Zone.instance = null;
        Assert.Null(reader.Capture());
        Scene.instance = null;
    }
}
