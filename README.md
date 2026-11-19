# Campfire Valheim

Connect your Valheim dedicated server to
[Campfire](https://github.com/dougmaitelli/Campfire), a self-hosted game server
management panel. See who's online and follow server activity from Campfire.
Install the mod on the server only; players do not need to install anything.

[Download the latest release](https://github.com/dougmaitelli/campfire-plugin-valheim/releases/latest/download/Campfire.Valheim.zip)

## Features

- Connected player count and online player list, refreshed every 15 seconds.
- Server capacity and world name when available.
- Crossplay invite code while the lobby is active.
- Player join, leave, and death notifications. Death notifications include the
  player name, but not the cause of death or killer.
- First boss defeat notifications. Previously defeated bosses are not announced
  when the server starts, and repeat kills do not generate notifications. Modded
  bosses use their in-game names when available.
- Integration connection notifications and announcements from compatible server mods.

## Requirements

- A Linux Valheim dedicated server managed through Campfire.
- [BepInExPack for Valheim](https://thunderstore.io/c/valheim/p/denikson/BepInExPack_Valheim/)
  installed on the server.
- Up-to-date versions of both Campfire and Spark, with game integrations enabled.
  The online player list requires support in both. If you cannot update yet,
  disable player names in the mod's settings before using it.

## Installation

1. In Campfire, edit this server and select **Enable game integration**.
2. Install BepInExPack for Valheim, following the pack's server instructions.
   Start Valheim through its BepInEx launcher and keep the `-batchmode -nographics`
   arguments.
3. Extract the downloaded `Campfire.Valheim.zip` into the Valheim server directory.
   The plugin should be at
   `BepInEx/plugins/CampfireValheim/Campfire.Valheim.dll`.
4. Start or restart the server through Campfire.
5. Check `BepInEx/LogOutput.log` for `Campfire Valheim <version> started`, then check
   the player readings and **Activity** tab in Campfire.

## Settings

BepInEx creates `BepInEx/config/io.campfire.valheim.cfg` when the mod first loads.
Edit this file to change the settings below, then restart the server.

| Section | Setting | What it does |
| --- | --- | --- |
| `General` | `Enabled` | Enable or disable the Campfire integration. Enabled by default. |
| `Events` | `IncludePlayerNames` | Show display names in player notifications and the online player list. Enabled by default. |
| `Events` | `ReportDeaths` | Enable or disable player death notifications. Enabled by default. |
| `Events` | `ReportBossDefeats` | Enable or disable first boss defeat notifications. Enabled by default. |
| `State` | `CapacityOverride` | Set the server capacity shown in Campfire. Leave at `0` to use the game's player limit when available. |

## Online player list and privacy

Select **Online players** in Campfire's server Discord card settings to show the
current roster. Names appear as comma-separated text, or **None** when nobody is
online. When server information becomes unavailable, the list is marked
unavailable instead of continuing to show old names.

Very large rosters or unusually long names may be shortened in the displayed
list. The player count still includes all connected players.

To hide player names, set `IncludePlayerNames` to `false` in the `[Events]` section
of the configuration file and restart the server. This disables the online player
list and removes names from player notifications. Player counts, world name, and
invite code are still shared with Campfire.

The mod does not share player platform IDs or IP addresses. Adding the roster to
a Discord card shares player display names with everyone who can see that message.

## Troubleshooting

- **The mod does not start:** Confirm BepInEx is installed and the server is using
  its launcher with `-batchmode -nographics`. Check `BepInEx/LogOutput.log` for
  warnings or errors.
- **No information appears in Campfire:** Confirm **Enable game integration** is
  selected and restart the server through Campfire. If you use an existing server
  container, check that it has Campfire's game integration connection configured;
  Campfire provides this for newly created containers when integrations are enabled.
- **The online player list does not work:** Update both Campfire and Spark. If
  you cannot update yet, set `IncludePlayerNames` to `false` and restart the server
  to use the integration without player names.
- **A boss defeat is missing:** Only first defeats observed while the mod is
  running are announced. Bosses already defeated when the server starts and
  repeat kills are not announced. Also check that `ReportBossDefeats` is enabled.

## License

MIT licensed. See [LICENSE](LICENSE).
