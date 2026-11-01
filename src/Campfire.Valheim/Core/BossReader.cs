using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;

namespace Campfire.Valheim.Core;

// Read on the main thread. Discover boss keys from game prefabs, including modded bosses.
public sealed class BossReader
{
    private const BindingFlags Members = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static;
    private static readonly IReadOnlyDictionary<string, string> Names = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
    {
        ["defeated_eikthyr"] = "Eikthyr",
        ["defeated_gdking"] = "The Elder",
        ["defeated_bonemass"] = "Bonemass",
        ["defeated_dragon"] = "Moder",
        ["defeated_goblinking"] = "Yagluth",
        ["defeated_queen"] = "The Queen",
        ["defeated_fader"] = "Fader"
    };
    private readonly PropertyInfo zoneInstance;
    private readonly PropertyInfo sceneInstance;
    private readonly FieldInfo prefabs;
    private readonly Type character;
    private readonly MethodInfo isBoss;
    private readonly FieldInfo defeatKey;
    private readonly MethodInfo getGlobalKey;
    private object? cachedScene;
    private Dictionary<string, string>? bosses;

    public BossReader(Assembly game) : this(
        game.GetType("ZoneSystem") ?? throw new TypeLoadException("ZoneSystem"),
        game.GetType("ZNetScene") ?? throw new TypeLoadException("ZNetScene"),
        game.GetType("Character") ?? throw new TypeLoadException("Character"))
    { }

    internal BossReader(Type zone, Type scene, Type character)
    {
        this.character = character;
        zoneInstance = zone.GetProperty("instance", Members) ?? throw new MissingMemberException("ZoneSystem.instance");
        sceneInstance = scene.GetProperty("instance", Members) ?? throw new MissingMemberException("ZNetScene.instance");
        prefabs = scene.GetField("m_prefabs", Members) ?? throw new MissingFieldException("ZNetScene.m_prefabs");
        isBoss = character.GetMethod("IsBoss", Members, null, Type.EmptyTypes, null) ?? throw new MissingMethodException("Character.IsBoss");
        defeatKey = character.GetField("m_defeatSetGlobalKey", Members) ?? throw new MissingFieldException("Character.m_defeatSetGlobalKey");
        getGlobalKey = zone.GetMethod("GetGlobalKey", Members, null, new[] { typeof(string) }, null) ?? throw new MissingMethodException("ZoneSystem.GetGlobalKey");
    }

    public IReadOnlyDictionary<string, string>? Capture()
    {
        var zone = zoneInstance.GetValue(null);
        var scene = sceneInstance.GetValue(null);
        if (zone == null || scene == null) { return null; }
        if (!ReferenceEquals(cachedScene, scene))
        {
            var discovered = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            var objects = prefabs.GetValue(scene) as IEnumerable ?? throw new InvalidOperationException("ZNetScene.m_prefabs is not a collection.");
            foreach (var prefab in objects)
            {
                if (prefab == null) { continue; }
                var type = prefab.GetType();
                var getComponent = type.GetMethod("GetComponent", new[] { typeof(Type) }) ?? throw new MissingMethodException("GameObject.GetComponent(Type)");
                var component = getComponent.Invoke(prefab, new object[] { character });
                if (component == null || isBoss.Invoke(component, null) is not true) { continue; }
                var key = defeatKey.GetValue(component) as string;
                if (string.IsNullOrWhiteSpace(key)) { continue; }
                var name = Names.TryGetValue(key!, out var known) ? known : type.GetProperty("name")?.GetValue(prefab) as string;
                discovered[key!] = string.IsNullOrWhiteSpace(name) ? key! : name!;
            }

            // An empty list can mean the scene has not finished initializing yet.
            if (discovered.Count == 0) { return null; }
            bosses = discovered;
            cachedScene = scene;
        }

        var defeated = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var boss in bosses!)
        {
            if (getGlobalKey.Invoke(zone, new object[] { boss.Key }) is true) { defeated.Add(boss.Key, boss.Value); }
        }

        return defeated;
    }
}
