# Campfire Valheim

A BepInEx plugin that sends Valheim server information and activity to
[Campfire](https://github.com/dougmaitelli/Campfire), a self-hosted game server
management panel. Install it on the dedicated server; players can use an unmodified
Valheim client.

[Download the latest release](https://github.com/dougmaitelli/campfire-plugin-valheim/releases/latest/download/Campfire.Valheim.zip)

## Features

- Connected player count, refreshed every 15 seconds.
- Server capacity and world name when available.
- Crossplay invite code while the lobby is active.
- Player join, leave, and death notifications. Deaths include the player name,
  without a cause or killer.
- First boss defeat notifications based on world progression. Previously defeated
  bosses are not announced on startup, and repeat kills do not generate notifications.
- Integration connection events and announcements submitted by other server mods.
- Human-readable state and event field labels, registered once before publishing.

## Installation

1. In Campfire, edit this server and select **Enable game integration**.
2. Install [BepInExPack for Valheim](https://thunderstore.io/c/valheim/p/denikson/BepInExPack_Valheim/)
   on your Linux dedicated server, following the pack's server instructions. Start
   Valheim through its BepInEx launcher and keep the `-batchmode -nographics` arguments.
3. Extract the downloaded `Campfire.Valheim.zip` into the Valheim server directory.
   The plugin should be at
   `BepInEx/plugins/CampfireValheim/Campfire.Valheim.dll`.
4. Start or restart the server through Campfire. The server container needs Spark's
   integration socket mounted at `/run/campfire-game/publish.sock`; Campfire provides
   this for newly created containers when game integrations are enabled.
5. Check `BepInEx/LogOutput.log` for `Campfire Valheim <version> started`, then check
   the player readings and **Activity** tab in Campfire.

BepInEx creates `BepInEx/config/io.campfire.valheim.cfg` on first load. You can use
it to disable death or boss notifications, hide player names, or override server
capacity. Restart the server after editing it.

## Registration and delivery

At startup, the background publisher sends a version 1 label manifest to
`PUT /v1/registration` over the same private Unix socket used for publications.
It waits for Campfire's `204 No Content` acknowledgement before sending any
state snapshots or events. The ordinary `valheim.integration.started` activity
still appears, but is not used as a registration handshake.

The manifest declares labels for `world` and `invite_code`, player names on
join/leave/death events, boss progression fields, and announcement messages.
Definitions are independent of current values: registering a `player` label does
not send player names when `IncludePlayerNames` is disabled. Snapshots and events
continue sending stable machine keys and values without repeating labels.

Registration is idempotent and persists across panel restarts and Spark reconnects.
The plugin re-registers after socket errors before retrying publications, but not
on every heartbeat. Temporary failures respect `Retry-After` and use bounded
exponential backoff. Until registration succeeds, only the latest state and a
bounded in-memory event queue are retained; events expire after 24 hours.

A permanent registration rejection (such as `400`, `403`, `404`, or `409`) pauses
publishing and logs a warning rather than silently falling back to unregistered
data. Check the server's game integration configuration and resolve any
registration revision conflict before restarting the server.

The manifest's revision starts at `1` and is separate from the plugin version.
When modifying `src/Campfire.Valheim/Core/GameRegistration.cs`, increase
`ValheimRegistration.Revision`. Campfire accepts an identical retry but rejects
changed definitions at the same revision or any older revision.

## Building

Install the .NET SDK specified in `global.json`, then run:

```sh
make restore
make check
make package
```

The installable ZIP is written to `artifacts/`. Automated tests cover publishing,
socket transport, and event processing without running Valheim.

To publish a release, update `Version` in `Directory.Build.props` and push the
matching tag (for example, `v0.1.0`). The release workflow checks the build and
publishes the ZIP to GitHub Releases.

MIT licensed. See [LICENSE](LICENSE).
