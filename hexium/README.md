# FiresDungeonMaster

Dungeon doors, keys and a lock-picking skill for Valheim, plus a section-based dungeon builder for admins.

Grand doors in the world lead to whole dungeons, either vanilla crypt themes or ones your admins built. Some
doors are locked: bring the right key, or pick the lock yourself in a short minigame that gets easier as your
Lockpicking skill grows.

## Requires

- BepInExPack for Valheim
- FiresUnifiedCore 0.2.389 or newer

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
- **Entry doors.** Four door pieces (Iron-Banded, Arched Oak, Planked, Grand Arch). Configure a placed door to
  pick which dungeon it generates (a vanilla theme such as Forest, Sunken or Meadows Crypt, Frost Cave,
  Infested Mine, Troll Cave, Goblin Camp, Ashlands ruins, or your own type), the interior environment, its
  display name and its lock mode (Open, Key or Quest), then generate.
- **Section browser.** Shift+Y (or the Dungeon Blueprint Desk piece) opens the saved-section browser, where
  you can pick a saved section and place it.
- **Dungeon presets.** With the hammer out, Shift+U opens the dungeon presets window.
- **Environment Box.** A piece that forces an interior sky and biome inside its volume, with presets on the
  hammer.
- **Vanilla rooms on the hammer.** Every vanilla dungeon room as a build piece, one tab per dungeon type,
  placed as plain geometry (no chests, spawners or doors).
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
- `[Blueprints] AllPiecesScalable` - default on. Makes every piece keep its scale when saved and reloaded, so
  scaled blueprint pieces don't snap back to normal size. Steps aside on its own when the Structure Tweaks mod
  is installed.
- `[Presets] Dungeon presets key` - opens the dungeon presets window with the hammer out. Default Shift+U.
- `[Vanilla Rooms] Rooms on the hammer` - default on. Shows the vanilla dungeon rooms on the hammer (admin).
  Each room tab also has its own on/off entry in this section. Changes apply at the next login.
- `[Sections] CenterpointFallbackPrefab` - the vanilla object written at a saved section's origin so the file
  works without this mod. Default `Pickable_Mushroom_yellow`.
- `[General] Verbose Logging` - default off. Extra detail in the log for troubleshooting.

## Issues / questions

https://discord.gg/zQRgHbqms4

## Licence

MIT. See the LICENSE file. Part of the Fires mod family.
