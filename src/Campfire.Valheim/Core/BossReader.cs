using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
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
    private readonly FieldInfo? displayName;
    private readonly PropertyInfo? localizationInstance;
    private readonly MethodInfo? localize;
    private readonly MethodInfo getGlobalKey;
    private object? cachedScene;
    private Dictionary<string, string>? bosses;

    public BossReader(Assembly game) : this(
        game.GetType("ZoneSystem") ?? throw new TypeLoadException("ZoneSystem"),
        game.GetType("ZNetScene") ?? throw new TypeLoadException("ZNetScene"),
        game.GetType("Character") ?? throw new TypeLoadException("Character"),
        FindLocalization(game))
    { }

    private static Type? FindLocalization(Assembly game)
    {
        var type = game.GetType("Localization");
        if (type != null) { return type; }
        foreach (var assembly in AppDomain.CurrentDomain.GetAssemblies())
        {
            type = assembly.GetType("Localization");
            if (type != null) { return type; }
        }

        return null;
    }

    internal BossReader(Type zone, Type scene, Type character, Type? localization = null)
    {
        this.character = character;
        zoneInstance = zone.GetProperty("instance", Members) ?? throw new MissingMemberException("ZoneSystem.instance");
        sceneInstance = scene.GetProperty("instance", Members) ?? throw new MissingMemberException("ZNetScene.instance");
        prefabs = scene.GetField("m_prefabs", Members) ?? throw new MissingFieldException("ZNetScene.m_prefabs");
        isBoss = character.GetMethod("IsBoss", Members, null, Type.EmptyTypes, null) ?? throw new MissingMethodException("Character.IsBoss");
        defeatKey = character.GetField("m_defeatSetGlobalKey", Members) ?? throw new MissingFieldException("Character.m_defeatSetGlobalKey");
        displayName = character.GetField("m_name", Members);
        localizationInstance = localization?.GetProperty("instance", Members);
        localize = localization?.GetMethod("Localize", Members, null, new[] { typeof(string) }, null);
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
                var prefabName = type.GetProperty("name")?.GetValue(prefab) as string;
                var key = defeatKey.GetValue(component) as string;
                if (string.IsNullOrWhiteSpace(key)) { continue; }
                discovered[key!] = ResolveName(key!, component, prefabName);
            }

            // An empty list can mean the scene has not finished initializing yet.
            if (discovered.Count == 0) { return null; }
            bosses = SelectFinalPhases(discovered);
            cachedScene = scene;
        }

        var defeated = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var boss in bosses!)
        {
            if (getGlobalKey.Invoke(zone, new object[] { boss.Key }) is true) { defeated.Add(boss.Key, boss.Value); }
        }

        return defeated;
    }

    private static Dictionary<string, string> SelectFinalPhases(Dictionary<string, string> discovered)
    {
        var phases = new Dictionary<string, (string Key, int Number)>(StringComparer.OrdinalIgnoreCase);
        foreach (var boss in discovered)
        {
            var root = boss.Key;
            var number = 0;
            var suffix = root.LastIndexOf("_p", StringComparison.OrdinalIgnoreCase);
            if (suffix > 0 && int.TryParse(root.Substring(suffix + 2), NumberStyles.None, CultureInfo.InvariantCulture, out var parsed) && parsed > 0)
            {
                root = root.Substring(0, suffix);
                number = parsed;
            }

            if (!phases.TryGetValue(root, out var current) || number > current.Number)
            {
                phases[root] = (boss.Key, number);
            }
        }

        var selected = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var phase in phases.Values)
        {
            selected.Add(phase.Key, discovered[phase.Key]);
        }

        return selected;
    }

    private string ResolveName(string key, object component, string? prefabName)
    {
        if (Names.TryGetValue(key, out var known)) { return known; }
        var name = displayName?.GetValue(component) as string;
        if (!string.IsNullOrWhiteSpace(name))
        {
            var localization = localizationInstance?.GetValue(null);
            var localized = localization != null ? localize?.Invoke(localization, new object[] { name! }) as string : null;
            if (!string.IsNullOrWhiteSpace(localized) && localized!.IndexOf('$') < 0 && localized.IndexOf('[') < 0)
            {
                return localized;
            }

            // Plain-text mod names do not need a localization entry.
            if (name!.IndexOf('$') < 0) { return name; }
        }

        return string.IsNullOrWhiteSpace(prefabName) ? key : prefabName!;
    }
}
