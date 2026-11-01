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
        public bool Boss;
        public bool IsBoss() => Boss;
    }

    private sealed class Prefab
    {
        public string name { get; set; } = "Custom Boss";
        public Character? Character;
        public object? GetComponent(Type type) => type == typeof(Character) ? Character : null;
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
