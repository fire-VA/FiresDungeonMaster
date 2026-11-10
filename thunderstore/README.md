# FiresDungeonMaster

Dungeon doors, keys and a lock-picking skill for Valheim, plus a section-based dungeon builder for admins.

Grand doors in the world lead to whole dungeons, either vanilla crypt themes or ones your admins built. Some
doors are locked: bring the right key, or pick the lock yourself in a short minigame that gets easier as your
Lockpicking skill grows.

## What's new in 0.1.202

This update is mostly for admins who build and place dungeons. Players will notice dungeons that keep working after
rooms are rebuilt, and no more sky floor stuck inside a picked dungeon.

**Pick a dungeon for any entrance.** Admins can now choose which dungeon an entrance leads to straight from the
entrance's menu, using **Pick dungeon...**. This works on placed dungeon entrances and on this mod's Entry Doors
alike, and the menu also has an **Empty** option. If the entrance already has a fully generated dungeon, it is reset
to empty first, then the new one is built. The choices are the vanilla dungeons you reach through a door (Forest
Crypt, Sunken Crypt, Troll Cave, Infested Mine, Dvergr Boss Hall, Hildir's Crypt and Cave), now with **Morkhalla**
and **The Hole** added. Outdoor camps and villages are no longer offered here, since they have no entrance.

**Changed**
- A dungeon room placed on the ground no longer creates an Entry Door or an interior box by itself. The setting
  "Entrance makes its box" is removed because it is no longer needed.
- Clearing a zone is refused if the zone holds any other location, so nothing else gets wiped by mistake.
- Joining a world is lighter: stock dungeon setups no longer load at login. A dungeon type picks up its settings when
  it is generated.
- A quieter log.

**Fixed**
- Rooms that are rebuilt later stay part of their dungeon.
- A picked dungeon no longer has the outdoor sky floor stuck inside it.

## Requires

- BepInExPack for Valheim
- FiresUnifiedCore 0.2.1223 or newer

## Installation

- Install with a mod manager, or copy the `FiresDungeonMaster` folder into `BepInEx/plugins`.
- Install it on every client **and** on the dedicated server. Doors, keys and dungeons are shared world
  objects, so everyone on the server needs the mod.
- Update it together with FiresUnifiedCore.

## For players

- **Dungeon doors.** Press E on a door to enter its dungeon. Inside, the dungeon has its own environment, and
  an exit arch brings you back out through the door you came in by.
- **Keys.** Some doors need a key: Old Dungeon Key, Copper Dungeon Key, Skeleton Key or Ornate Skeleton Key.
  Holding the right key, E turns the lock. With no key and no lockpick, the door tells you it needs a key.
- **Lockpicking.** No key? A Lockpick opens the lock-picking minigame instead:
  - hold left-click and drag the pick under each pin (up to 5),
  - drag upward to lift the pin and tap E in its sweet spot to set it,
  - set every pin before the timer runs out.
  Slip and the lock snaps: one lockpick breaks, and pins you already set can pop back out.
- **The Lockpicking skill.** A real skill on your skills list. Every pin you set gives XP. Higher levels widen
  the sweet spots, extend the timer, cut the snap chance and help set pins stay put.

## For admins and builders

Authoring pieces live on the "Dungeon Sections" hammer tab and are admin-only; regular players never see them.

- **Sections.** Place a Dungeon Section Footprint (a scalable wireframe room shell), tag each face as wall,
  doorway, open or window, give the section a role (center, edge, doorway, corridor, entry or exit) and a
  dungeon-type name, build the room inside, then save it. The generator assembles every section of a type into
  a dungeon.
- **Entry doors.** Four door pieces (Iron-Banded, Arched Oak, Planked, Grand Arch). Use the door's menu to
  **Pick dungeon...**, make it **Empty**, or open **Door settings...** for its interior environment, display name
  and lock mode (Open, Key or Quest).
- **Picking a dungeon for an entrance.** Placed dungeon entrances get the same **Pick dungeon...** menu row. The
  choices are the vanilla door dungeons, including Morkhalla and The Hole.
- **Section browser.** Shift+Y (or the Dungeon Blueprint Desk piece) opens the saved-section browser, where
  you can pick a saved section and place it.
- **Dungeon presets.** With the hammer out, Shift+U opens the dungeon presets window.
- **Environment Box.** A piece that forces an interior sky and biome inside its volume, with presets on the
  hammer.
- **Vanilla rooms on the hammer.** Every vanilla dungeon room as a build piece, one tab per dungeon type. By
  default a placed room comes with working chests, pickables and spawners; turn off `Live room contents` to place
  walls only. Placing a room no longer adds an Entry Door or box.
- **Portable blueprints.** Saved sections are PlanBuild-style `.blueprint` files. A small vanilla marker is
  written at each section's origin so the file stays placeable by Expand World, Infinity Hammer or PlanBuild
  even without this mod.

The in-game Fires help panel (the "? Help" button on the pause menu) has the full walkthrough; its authoring
and command pages show for admins.

## Console commands

| command | what it does |
|---|---|
| `firesdungeon_save [name]` | save everything inside the footprint you are aiming at as a section (admin) |
| `firesdungeon_list [type]` | list saved sections |
| `firesdungeon_place <idOrName> [metres]` | place a saved section in front of you (admin, host) |
| `firesdungeon_browser` | open or close the saved-section browser |
| `firesdungeon_types` | list the dungeon types in the section library |
| `firesdungeon_sections [type]` | list saved sections, optionally by type |
| `firesdungeon_generate <type> [metres]` | build a dungeon from every section of a type, ahead of you (host) |
| `firesdungeon_worldgen <type>` | opt a dungeon type into world generation (applies on the next world load) |
| `firesdungeon_lockpick [Easy\|Intermediate\|Hard\|Very Hard]` | try the lock-picking minigame; nothing is used up |

## Configuration

`BepInEx/config/com.Fire.FiresDungeonMaster.cfg`, or the Fires config window (F1) in game.

- `[Blueprints] Section Archive key` - opens the saved-section browser. Default Shift+Y. Admin only.
- `[Blueprints] AllPiecesScalable` - default on. Scaled pieces keep their size after a reload and for other
  players, so scaled blueprint pieces don't snap back to normal size. Steps aside on its own when the Structure
  Tweaks mod is installed.
- `[Presets] Dungeon presets key` - opens the dungeon presets window with the hammer out. Default Shift+U.
- `[Vanilla Rooms] Rooms on the hammer` - default on. Shows the vanilla dungeon rooms on the hammer (admin).
  Each room tab also has its own on/off entry in this section. Changes apply at the next login.
- `[Vanilla Rooms] Live room contents` - default on. Placed rooms come with working chests, pickables and
  spawners. Off: walls only.
- `[Vanilla Rooms] Clear zone on last entrance` - default off. Server side. When the last entrance in a zone is
  removed, everything in that zone's dungeon space is deleted, whoever built it. A zone that holds any other
  location is never cleared.
- `[Sections] CenterpointFallbackPrefab` - the vanilla object written at a saved section's origin so the file
  works without this mod. Empty (the default) uses a yellow mushroom.
- `[General] Verbose Logging` - default off. Extra detail in the log for troubleshooting.

The "Entrance makes its box" setting has been removed.

## Issues / questions

https://discord.gg/zQRgHbqms4

## Licence

MIT. See the LICENSE file. Part of the Fires mod family.
