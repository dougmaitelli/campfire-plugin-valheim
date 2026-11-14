using System;
using BepInEx;
using BepInEx.Configuration;
using Campfire.Valheim.Core;
using UnityEngine;

namespace Campfire.Valheim;

[BepInPlugin(Guid, MyPluginInfo.PLUGIN_NAME, Version)]
public sealed class Plugin : BaseUnityPlugin
{
    public const string Guid = MyPluginInfo.PLUGIN_GUID;
    public const string Version = MyPluginInfo.PLUGIN_VERSION;
    private static Plugin? instance;
    private Publisher? publisher;
    private ValheimReader? reader;
    private DeathHooks? deathHooks;
    private BossReader? bossReader;
    private readonly BossTracker bosses = new();
    private PlayerTracker? players;
    private ConfigEntry<float> sampleInterval = null!;
    private ConfigEntry<int> capacity = null!;
    private ConfigEntry<bool> playerNames = null!;
    private ConfigEntry<bool> deaths = null!;
    private ConfigEntry<bool> bossDefeats = null!;
    private float nextSample;
    private float nextWarning;
    private string? session;
    private volatile bool active;

    private void Awake()
    {
        var enabledSetting = Config.Bind("General", "Enabled", true, "Enable publishing. Restart after changing settings.");
        var socket = Config.Bind("General", "SocketPath",
            Environment.GetEnvironmentVariable("CAMPFIRE_GAME_SOCKET") ?? "/run/campfire-game/publish.sock",
            "Spark's per-server mounted Unix socket. No API token is needed.");
        sampleInterval = Config.Bind("General", "SampleIntervalSeconds", 2f,
            new ConfigDescription("Observe players on Unity's main thread; state heartbeats run every 15 seconds.", new AcceptableValueRange<float>(1, 10)));
        capacity = Config.Bind("State", "CapacityOverride", 0,
            new ConfigDescription("0 reads the game's player-limit field when available; set explicitly if another mod changes its semantics.", new AcceptableValueRange<int>(0, 1_000_000)));
        playerNames = Config.Bind("Events", "IncludePlayerNames", true, "Include display names in player events and the current player list. Requires list-capable Spark and Campfire for state publishing. Never publishes platform IDs or IP addresses. Restart after changing settings.");
        deaths = Config.Bind("Events", "ReportDeaths", true, "Report player death broadcasts.");
        bossDefeats = Config.Bind("Events", "ReportBossDefeats", true, "Report newly observed first boss defeats from world progression. Restart after changing settings.");
        if (!enabledSetting.Value || !Application.isBatchMode)
        {
            Logger.LogInfo("Campfire publishing inactive: enable it on a headless dedicated server.");
            enabled = false;
            return;
        }

        try
        {
            publisher = new Publisher(new SparkTransport(socket.Value), message => Logger.LogWarning(message), includePlayerNames: playerNames.Value);
            players = new PlayerTracker(playerNames.Value);
            publisher.Start();
            instance = this;
            Logger.LogInfo("Campfire Valheim " + Version + " started; publishing through " + socket.Value);
        }
        catch (Exception error)
        {
            Logger.LogError("Campfire publisher could not start: " + error.Message);
            enabled = false;
        }
    }

    private void Update()
    {
        if (publisher == null || Time.realtimeSinceStartup < nextSample) { return; }
        nextSample = Time.realtimeSinceStartup + sampleInterval.Value;
        try
        {
            if (reader == null)
            {
                Type? znet = null;
                foreach (var assembly in AppDomain.CurrentDomain.GetAssemblies())
                {
                    znet = assembly.GetType("ZNet", throwOnError: false);
                    if (znet != null) { break; }
                }

                if (znet == null) { throw new MissingMemberException("Valheim ZNet API is not loaded."); }
                reader = new ValheimReader(znet);
                if (bossDefeats.Value)
                {
                    try { bossReader = new BossReader(znet.Assembly); }
                    catch (Exception error) { Logger.LogWarning("Boss reporting unavailable for this Valheim API: " + error.Message); }
                }
                if (deaths.Value)
                {
                    deathHooks = DeathHooks.TryInstall(znet.Assembly, reader, Enqueue, Warn, playerNames.Value);
                }
            }

            var snapshot = reader.Capture(DateTimeOffset.UtcNow, capacity.Value);
            publisher.SetState(snapshot);
            active = snapshot != null;
            if (snapshot == null)
            {
                players!.Reset();
                session = null;
                deathHooks?.Reset();
                bosses.Reset();
                return;
            }

            if (session != snapshot.Session)
            {
                deathHooks?.Reset();
                session = snapshot.Session;
            }

            foreach (var gameEvent in players!.Observe(snapshot)) { Enqueue(gameEvent); }
            if (bossReader != null)
            {
                try
                {
                    var defeated = bossReader.Capture();
                    if (defeated != null)
                    {
                        foreach (var gameEvent in bosses.Observe(snapshot.Session, defeated, snapshot.CapturedAt)) { Enqueue(gameEvent); }
                    }
                }
                catch (Exception error) { Warn("Could not observe boss progression: " + error.Message); }
            }
        }
        catch (Exception error)
        {
            active = false;
            publisher.SetState(null);
            Warn("Valheim observation unavailable: " + (error.InnerException ?? error).Message);
        }
    }

    // Other server mods can publish messages without knowing the socket or credentials.
    public static bool Announce(string message)
    {
        var current = instance;
        if (current is null || !current.active || current.publisher == null || string.IsNullOrWhiteSpace(message)) { return false; }
        return current.publisher.Enqueue(new GameEvent
        {
            Title = TextBounds.Utf8(message, 256),
            Data = new() { ["message"] = TextBounds.Utf8(message, 1024) }
        }, DateTimeOffset.UtcNow);
    }

    private void Enqueue(GameEvent gameEvent) => publisher?.Enqueue(gameEvent, DateTimeOffset.UtcNow);

    private void Warn(string message)
    {
        if (Time.realtimeSinceStartup < nextWarning) { return; }
        nextWarning = Time.realtimeSinceStartup + 30;
        Logger.LogWarning(message);
    }

    private void OnDestroy()
    {
        active = false;
        if (instance == this) { instance = null; }
        deathHooks?.Dispose();
        publisher?.Dispose();
    }
}
