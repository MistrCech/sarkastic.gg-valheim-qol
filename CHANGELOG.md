# Changelog

## [0.5.0] - 2026-10-07

### Changed

- Renamed to Sarkastic.gg QoL: the community moved from sarkastic.eu to sarkastic.gg. The DLL is now
  `SarkasticGG_QoL.dll`; delete `SarkasticEU_QoL.dll` from `BepInEx/plugins` when upgrading. The plugin
  GUID, the config file `sarkasticeu.qol.cfg` and the files next to it are unchanged.
- Everything a player might not want is off until switched on. Stations no longer feed
  themselves (`[Feeding] Smelters` false) and doors no longer close by themselves (new
  `[Doors] Default`, false) until a player switches one on; a player sees taming progress and gets
  pins only after `!tame on` / `!pins on` (new `[Tames] ProgressDefault` and `[Pins] Default`,
  false). A piece or player that was switched explicitly keeps that choice. **An existing config
  keeps its old values:** after upgrading set `qol set Feeding.Smelters false`.
- A command for a piece acts on the piece the player looks at, within the game's use distance
  (3.5 m), instead of the nearest one within 5 m: the client writes where its player looks into
  the player's data (for the head of the character on the other clients), and the server follows
  that line from the eyes as the game does from the camera. With two chests side by side the
  command used to hit either.
- Who may switch a piece: anyone where no ward stands; under an active ward only its owner and the
  players they added (the game's own rule for opening chests there). Before, anyone could switch
  anyone's piece, including a ballista to shoot at players.
- The per-player file (`sarkasticeu.qol.players.txt`) keeps ons as well as offs (`+pins -tame`);
  a line from 0.4 reads as before.

### Fixed

- `!deaths` listed a young animal growing up (a chick becoming a hen, a piglet a boar) as a tamed
  creature "taken out of the world alive": the game replaces it by destroying it. Seen on the live
  server.

## [0.4.1] - 2026-09-13

### Fixed

- With `SharedTables` off, a table that still held pins from the table mode was rewritten with
  them again every minute instead of once without them. Seen on the live server after 0.4.0.
- A portal counts by its object for what a player has had, not by its name: players retag
  portals to travel, and every new tag put another pin on the map. The default pin name is now
  plain `Portal` (`[Pins] PortalName`; on a server that had 0.3.x set it with
  `qol set Pins.PortalName Portal`). A change of icons no longer puts a second pin on a map either.

## [0.4.0] - 2026-09-13

### Changed

- Pins go on each player's own map instead of the cartography tables: a player who comes within
  30 m of something gets its pin the way a runestone gives one (`Game.RPC_DiscoverLocationResponse`),
  as their own saved pin -- theirs to delete, on nobody else's map unless they write a table
  themselves. What each player has had is remembered per world, so a pin they deleted is not
  given again; `!pins reset` gives the ones near them once more, `!pins off` stops them. The tables
  are left to the players; `[Pins] SharedTables` (off) brings the old behaviour back, and with it
  off any pins of ours still in a table are taken out, so a player who reads a table loses the
  ones they got from it before. A vanilla client with a map shown adds such a pin silently (only a
  world without a map turns the player to face it).

## [0.3.2] - 2026-09-13

### Added

- `!deaths [n]`: what killed the tamed creatures, the latest five (up to ten) as chat lines --
  "12 min ago: Boar 'Pepa' (lvl 2): attacked, 131 damage, by Wolf (lvl 3), at (-2451, 2522)".
  Every death of a tamed creature is recorded with the creature or player behind the last blow,
  or the kind of harm (burned, smoke, fell, drowned, froze, poisoned ...), and one taken out of the
  world alive is recorded too; the latest `[Tames] DeathsKept` (50) are kept per world, and each
  goes to the server log. `qol deaths [n]` for the admin. Needs the server to own the creatures,
  as it does with Dedicated Simulation.

### Changed

- Pins carry short names by default so the map stays readable: BB, CB, LB, Rasp, Mush, YMush,
  BMush, Magecap, Jotun, Thistle, Dand, Fiddle, Smoke, Sn, Obs, Tar, Barley, Flax; Cu, Ag,
  Flametal; Crypt, Sunken crypt (Fe), Troll cave, Frost cave, Mine, Hildir crypt/cave/fortress,
  Haldor. A change of the name lists or the icons now renames the existing pins as well (`qol set`
  or `qol reload`); a pin whose thing is no longer listed is dropped. A dungeon pin now stands for
  the location itself, so existing dungeon pins are made once more.

## [0.3.1] - 2026-09-13

### Changed

- Chest labels no longer place a sign; the chest itself is named after its contents ("Coal 156",
  "Wood 240, Stone 120 +2"), shown when a player looks at it and as the title of the opened chest,
  in each player's language. The name is a per-object field override (`Container.m_name`), which
  the game reads when it creates the chest, so a chest whose contents changed is created afresh
  (a blink): never while it is open, at most every `[Labels] RefreshSeconds` (10). An empty chest
  has its own name. The 0.2.0 label signs still in the world are removed. Settings moved from
  `[Signs] Labels*` to `[Labels]`; `LabelsEmptyText` is gone.

## [0.3.0] - 2026-09-13

### Added

- Map pins on the cartography tables for what players have found within 30 m: clusters of
  berries, mushrooms, thistle, dandelions, tin, obsidian, tar and the like (three or more of a kind
  within 24 m, "Raspberries x7" at the centre; nothing within 20 m of a player-built piece), copper,
  silver and flametal deposits, burial chambers, crypts, troll and frost caves, infested mines,
  Hildir's places and Haldor, and player-built portals by their tag. Every table in the world gets
  them; a client reads them off a table as usual. A pin a player deletes and writes back to a
  table is taken off every table and not made again; a deposit that is mined out or a portal that
  is gone loses its pin. `!pins on|off` per player (whether what they find is put on the tables),
  `qol pins` for the admin (`list`, `forget`, `clear`), `[Pins]` in the config with the name lists,
  icons and distances.

## [0.2.0] - 2026-09-13

### Added

- Feeding: smelters, kilns, windmills, spinning wheels, blast furnaces and shield generators take
  ore and fuel from player-built chests within 4 m once below half (`!feed on|off` per station,
  on by default); fireplaces too when a player switches one on (`!fire feed on`). Never while a
  player is within 4 m of the station or has the chest open; one of each item stays behind; "+N
  item" floats above the station.
- Chest labels (`!label on|off`): a sign in front of the chest lists what is inside, kept up to
  date with the chest, following it if it settles, removed with the chest.
- Clocks (`!clock on|off`): a sign shows the in-game day and time in ten-minute steps.
- Tidy chests (`!sort on|off`): stacks merged, items ordered by name and quality, laid out from
  the top left, whenever the chest changed and nobody has it open.
- The scan skips an object a feature has just created afresh under a new id, so nothing acts on
  the old copy in the same pass.

Verified on a local 1.0.12 server with a spawned scene: a smelter took 19 coal and 9 copper ore
and left one of each, a fire took 10 wood, a chest with four stacks became two, a label read
"Wood 40 | Stone 12 | Copper ore 3 | +1" and followed the chest, a clock read "Day 44 - 19:10",
`!label off` removed the sign.

## [0.1.3] - 2026-09-13

### Added

- `[Guards] PersistentEventsAdminOnly` (on): only admins and the game itself may start or stop
  persistent world events. In Valheim 1.0.12 the client console command `pevents start|stop
  <name>` has no cheat or admin flag, so any player can type it into the chat, and the server's
  handlers check nobody. Others are told "Only admins can start or stop world events" and logged.

## [0.1.2] - 2026-09-13

### Added

- The server appears in the player list under `[Chat] ServerName` (`Server`). A Valheim client
  sends its chat only to the listed players, so this makes every chat message reach the server --
  a player alone can use `!` commands -- and lets the server answer as a chat line under that name
  (`[Chat] ReplyInChat`), instead of a top-left message that fades. Ordinary chat is written to the
  server log (`[Chat] Log`). Verified on a local 1.0.12 server with fake peers: the list carries the
  entry with the server's peer id as its character, a chat message addressed to the server is
  handled, the reply arrives as a `ChatMessage` from that entry.

## [0.1.1] - 2026-09-13

### Fixed

- Chat commands never reached the server from a player who was alone: a Valheim client sends chat
  only to the other players, never to the server. `!` commands now work while another player is
  online (the first copy is handled, the rest swallowed), and the sleep vote moved to `/sleep`,
  which rides on the game's own `sleep` console command that a client always forwards to the
  server; taken over here for every player, not just admins. `/sleep` votes, `/sleep off`
  withdraws, `/sleep ?` shows the state; a vote counts like being in bed.

## [0.1.0] - 2026-09-13

First release. Chat commands (`!help`, `!ballista`, `!door`, `!tame`, `!sleep`), the `qol`
console command (`status`, `set`, `reload`, `containers`), and the features: sleep by vote with
a warning before the skip, doors that close by themselves, ballistas that leave players and tames
alone, taming/hatching/growing progress text, container sizes, field overrides from a text file,
message of the day.

Verified on a local Valheim 1.0.12 server with fake peers: commands and replies, the vote logic,
the message of the day, chests resized (grown, and shrunk only when their items fit) and created
afresh while loaded, a field override applied to workbenches, the console commands through
Dedicated Simulation's console.
