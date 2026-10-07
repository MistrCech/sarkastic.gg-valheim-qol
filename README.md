# Sarkastic.eu QoL

A server-side [BepInEx](https://github.com/BepInEx/BepInEx) plugin for Valheim 1.0 dedicated servers:
small conveniences for the players, none of them needing anything on the client. Nothing a player
might not want is on until they switch it on, for themselves or for a piece they look at; what
applies to the whole server (sleep vote, container sizes, message of the day) is the admin's, set
from the server console.

- **Sleep by vote:** the night is skipped once half the players (configurable) are in bed, with a
  warning a few seconds before, and a note to everyone whenever the count changes.
- **Doors close by themselves** once nobody is near (player-built, unlocked doors a player
  switched on).
- **Ballistas** stop shooting at players and tamed creatures, which the game has them do (each
  ballista can be set on its own).
- **Taming, hatching and growing progress** floats above the creature or egg.
- **Container sizes** per kind of container, from the config, changeable while the server runs.
- **Field overrides** for any player-built piece from a text file: the range of a crafting
  station, a fireplace that needs no fuel, whatever the game keeps in a public field.
- **Stations feed themselves** from the chests next to them: ore and fuel for smelters, kilns,
  windmills, spinning wheels and blast furnaces, fuel for shield generators, and for fireplaces if
  a player switches a fire on.
- **Chest labels**: a chest named after what is inside ("Coal 156"), where the game shows its
  name, in each player's language.
- **Clocks**: a sign that shows the in-game day and time.
- **Tidy chests**: a chest that merges its stacks and sorts itself whenever it changed.
- **Map pins** on a player's own map for what they come near: clusters of berries and mushrooms
  ("BB x7"), ore deposits, dungeon entrances, portals. Given the way a runestone gives a pin, so
  they are the player's own to keep or delete, and on nobody else's map.
- **Message of the day** after logging in.
- **A guard** for a hole in the game: persistent world events, which any player could start or stop with `/pevents` in 1.0.12, are admin-only.

Players need nothing: vanilla clients work. The plugin depends on BepInEx (with its Harmony) and
nothing else.

## How players use it

Players type commands into the normal chat, starting with `!`. The reply comes back as a chat line
from **Server** (the name is configurable).

| Command | |
|---|---|
| `!help` | Lists the commands. |
| `!sleep` (or `/sleep`) | Vote to skip the night; again or `!sleep off` withdraws, `!sleep ?` shows the vote. |
| `!ballista players on\|off`, `!ballista tames on\|off` | The ballista you look at. Without on/off: shows its setting. |
| `!door auto on\|off` | The door you look at closes by itself once nobody is near. |
| `!feed on\|off` | The smelter, kiln, windmill, spinning wheel, blast furnace or shield generator you look at feeds itself from chests within 4 m. |
| `!fire feed on\|off` | The fireplace, hearth or torch you look at feeds itself from chests within 4 m. |
| `!label on\|off` | The chest you look at is named after its contents ("Coal 156", "Wood 240, Stone 120, Copper ore 30 +4") when a player looks at it and as the title of the opened chest. It blinks for a moment when the name changes; never while it is open, at most every 10 s. An empty chest, or off, has its own name again. |
| `!clock on\|off` | The sign you look at shows the day and time ("Day 44 - 19:10"). |
| `!sort on\|off` | The chest you look at keeps itself sorted: stacks merged, items by name, from the top left. |
| `!tame on\|off` | Whether you see taming, hatching and growing progress. |
| `!pins on\|off\|reset` | Pins on your own map for what you come near (within 30 m). They are your own pins: delete one and it does not come back; `reset` gives the ones near you once more. |
| `!deaths [n]` | What killed the tamed creatures: the latest five (up to ten), with the creature or player behind the last blow, or burning, smoke, a fall, drowning ..., how long ago and where. |

Everything is off until switched on: a piece until somebody switches it on, `!tame` and `!pins`
until the player does (the admin can change these defaults). A command for a piece acts on the
piece under the player's crosshair, as close as the game's own use key reaches (3.5 m) -- the
client tells the server where its player looks, so that the others see the head turn. Nearby is not
enough: look at it and type. Anyone may switch a piece where no ward stands; under an active ward only its owner and
the players they added, as the game has it for opening chests there. Asking how a piece is set
(the command without on/off) works for anyone.

How that works: a Valheim client sends its chat only to the players in the player list it got
from the server, one copy each, never to the server itself. So the plugin lists the server as a
player (`[Chat] ServerPresence`), which makes every message reach the server too -- alone or not
-- and lets the server answer as a chat line under that name (a client shows a chat line only from
a listed player). A `!` message is not passed on to the other players; ordinary chat is, and is
also written to the server log (`[Chat] Log`). The server's entry shows in the players list, has
no map pin and, with `ServerPresence` off, commands fall back to the game's admin-only `sleep`
console command (`/sleep`, always forwarded, taken over for everyone) and to `!` messages that
only reach the server while another player is online.

## How the admin uses it

On the server's own console -- with [Dedicated Simulation](https://github.com/MistrCech/valheim-serverside)
that is the panel's console, e.g. AMP -- the command `qol`:

| Command | |
|---|---|
| `qol status` | What is on. |
| `qol set <Section.Key> <value>` | Change a setting now, e.g. `qol set Sleep.RequiredPercent 60`, `qol set Containers.piece_chest_wood 6x2`, `qol set Motd.Text Welcome!`. Saved to the config file. |
| `qol reload` | Re-read the config file and the field override file after editing them. |
| `qol containers` | The container sizes with the game's defaults. |
| `qol deaths [n]` | The latest deaths of tamed creatures and what killed them. |
| `qol pins` | How many pins of each kind, tables, pins players removed, names in the config the game does not have. `qol pins list [pickables\|ores\|dungeons\|portals]` lists them, `qol pins forget` lets removed pins come back, `qol pins clear` takes every pin off the tables. |

Without Dedicated Simulation the game's console exists on a dedicated server but nothing feeds
it; edit the config file and restart instead.

## Configuration

`BepInEx/config/sarkasticeu.qol.cfg`; everything is read live.

| Setting | Default | |
|---|---|---|
| `[General] ChatPrefix` | `!` | What a chat message starts with to be a command. |
| `[General] ScanSeconds` | 2 | How often the objects around each player are looked at. |
| `[Chat] ServerPresence`, `ServerName` | true, `Server` | List the server as a player under this name, so chat reaches the server and replies come as chat lines. |
| `[Chat] ReplyInChat` | true | Answer commands in the chat; off: at the top left of the screen. |
| `[Chat] Log` | true | Write the players' chat to the server log. |
| `[Motd] Text`, `DelaySeconds` | empty, 6 | Shown in the middle of the screen after the player's character appears; `\|` breaks a line. |
| `[Sleep] RequiredPercent`, `MinInBed`, `WarnSeconds`, `ShowProgress` | 50, 1, 10, true | The share of online players that must be in bed, the least number, the warning before the skip, and whether everyone is told when the count changes. |
| `[Doors] Default` | false | Whether every door closes by itself unless switched off with `!door auto off`. |
| `[Doors] CloseAfterSeconds`, `PlayerDistance` | 3, 4 | A door closes this long after the last player left this distance. |
| `[Ballistas] TargetPlayers`, `TargetTames` | false, false | Defaults for ballistas nobody set with `!ballista`. |
| `[Tames] Progress`, `ProgressDefault`, `ProgressStepPercent`, `ProgressRange` | true, false, 5, 30 | Progress text; whether a player who never typed `!tame` sees it, how often it repeats, who sees it. |
| `[Tames] LogDeaths`, `DeathsKept` | true, 50 | Record what kills tamed creatures (server log and `!deaths`), and how many of the latest to keep. |
| `[Containers] <prefab>` | the game's size | One entry per buildable container appears once the world is loaded, `WIDTHxHEIGHT`, at most 8 wide. A container is only shrunk when its items fit. |
| `[Feeding] Smelters`, `Fireplaces` | false, false | Defaults for stations nobody set with `!feed` / `!fire feed`. |
| `[Feeding] Range`, `PlayerDistance`, `LeaveAtLeast`, `ShowText` | 4, 4, 1, true | Chests within Range are used, only when it is below half, never while a player is within PlayerDistance of the station or has the chest open; LeaveAtLeast of each item stays; "+N item" floats above the station. |
| `[Labels] Default`, `MaxItems`, `RefreshSeconds` | false, 3, 10 | Whether every chest is named after its contents, how many kinds the name lists, how often at most a chest is renamed (each rename creates it afresh: a blink). |
| `[Signs] ClockStepMinutes`, `ClockFormat` | 10, `Day {0} - {1:00}:{2:00}` | The clock's resolution (a sign update for everyone nearby per step) and text. |
| `[Sorting] Default` | false | Whether every chest sorts itself. |
| `[Guards] PersistentEventsAdminOnly` | true | Only admins and the game may start or stop persistent world events (`/pevents`, open to everyone in 1.0.12). |
| `[Prefabs] File` | `sarkasticeu.qol.prefabs.txt` | One override per line: `<prefab> <Component>.<field> <value>`, e.g. `piece_workbench CraftingStation.m_rangeBuild 20`. |
| `[Pins] Default` | false | Whether a player who never typed `!pins` gets pins. |
| `[Pins] SharedTables` | false | Also write every pin into every cartography table, so whoever reads a table gets them all (as another player's pins; one deleted and written back stays gone). Off: the tables are left to the players and any of our pins in them are taken out. |
| `[Pins] Pickables`, `Ores`, `Dungeons`, `Portals` | true | Which kinds of pin are made. |
| `[Pins] DiscoverRange` | 30 | A thing is found once a player has been within this many metres of it. |
| `[Pins] ClusterRadius`, `ClusterMin` | 24, 3 | Pickables of one kind this close to each other form one cluster, pinned at its centre, if there are at least this many. |
| `[Pins] FarmDistance` | 20 | No pickable pins this close to a player-built piece (a farm, a base). |
| `[Pins] WriteSeconds`, `MaxPins` | 60, 1500 | With SharedTables: a table is rewritten at most this often and only when its pins changed. No more pins than this in all. |
| `[Pins] PickableNames`, `OreNames`, `DungeonNames`, `PortalName` | BB, CB, LB, Rasp, Mush, YMush, BMush, Magecap, Jotun, Thistle, Dand, Fiddle, Smoke, Sn, Obs, Tar, Barley, Flax; Cu, Ag, Flametal; Crypt, Sunken crypt (Fe), Troll cave, Frost cave, Mine, Hildir ..., Haldor; `Portal` | What is pinned and what the pin says, `<prefab>=<name>` lists (short, so the map stays readable). A change renames the existing pins too; `qol pins` names any entry the game does not know. |
| `[Pins] PickablesIcon`, `OresIcon`, `DungeonsIcon`, `PortalsIcon` | dot, hammer, house, portal | The map icon per kind (fire, house, hammer, dot, portal). |
| `[Pins] MapTextureSize` | 2048 | Only for a table nobody has written yet, when the game does not say: the map's size in pixels along a side. A wrong size makes clients ignore the table. |

Every feature has its own `Enabled`. The pins, what each player has had, and the ones players
removed from tables are kept in `BepInEx/config/sarkasticeu.qol.pins.<world>.txt`.

## How it works

Everything goes through what a vanilla client already understands:

- **The objects' data.** A door's open/closed state, a ballista's per-piece choice, a container's
  items, a smelter's fuel and ore queue, a sign's text: the server changes the world object's data
  and every client shows the result.
- **Per-object field overrides.** The game lets a world object override the public fields of its
  own components (`ZNetView.LoadFields`: `HasFields`, `HasFields<Component>`,
  `<Component>.<field>`), which every client applies when it creates the object. Container sizes,
  chest labels (`Container.m_name`, translated by the client since it holds the game's name keys)
  and the field override file use this; an object that is already loaded is created afresh under a
  new id so the change shows at once.
- **The game's own messages.** `ShowMessage` for the top-left and centre texts, `RPC_DamageText`
  for text floating in the world.
- **A runestone's pin.** The server gives a player a pin with the routed RPC a runestone's
  response uses (`Game.RPC_DiscoverLocationResponse`): the client adds it as the player's own saved
  pin, silently while a map is shown. There is no RPC to take one away, so a pin stays until the
  player deletes it, and the server remembers per player what it gave so nothing comes twice.
- **The tables' data** (with `SharedTables`). A cartography table holds what a client wrote to it
  (`MapTable`, `Minimap.GetSharedMapData`): the explored map and the pins, each with an owner id.
  The server rewrites a table with its own pins under an owner id no player has, keeping everything
  the players wrote as it is (and checking nobody changed the table meanwhile; the decompressing
  and compressing of the map happens off the main thread). A client treats such pins as another
  player's: shows them, lets a player delete one, and drops them again when a later read no longer
  lists them. A pin missing from a table a player wrote is one they deleted, and is not made again.
- **Five patched methods:** the sleep vote (`Game.EverybodyIsTryingToSleep`), the server's entry
  in the player list (`ZNet.SendPlayerList`), chat addressed to the server and commands caught
  before they are passed on (`ZRoutedRpc.HandleRoutedRPC`, `RouteRPC`), `/sleep` taken over from
  the game's admin-only console command (`ZNet.RPC_RemoteCommand`), and a player's character
  appearing (`ZNet.RPC_CharacterID`).

Every `ScanSeconds` the objects in the zones around one player per frame are handed to the
features; a few thousand at most, and nothing is changed unless something differs.

## Installation

1. Install [BepInExPack_Valheim](https://thunderstore.io/c/valheim/p/denikson/BepInExPack_Valheim/) 5.4.2350 or newer on the dedicated server.
2. Copy `SarkasticEU_QoL.dll` from the latest release into `BepInEx/plugins/`.
3. Restart the server. `BepInEx/LogOutput.log` shows `Sarkastic.eu QoL running`.

Made for the Sarkastic.eu server together with
[Dedicated Simulation](https://github.com/MistrCech/valheim-serverside) (the server simulates the
world around players, admin console) and
[Resource Regrowth](https://github.com/MistrCech/valheim-resource-regrowth) (one-time world content
comes back). Each works on its own.

## Building

`dotnet build src/SarkasticEU_QoL.csproj -c Release` with `VALHEIM_DEDI_INSTALL` pointing at a
dedicated server install that has BepInEx. The game assemblies are publicized at build time.
