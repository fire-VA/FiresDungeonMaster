# FiresDungeonMaster

Dungeon doors, keys, lockpicking and a dungeon builder for Valheim.

## For players

- **Dungeon doors.** Grand doors in the world lead to whole dungeons, vanilla crypt themes or custom-built ones. Press
  E on a door to enter; an exit arch inside brings you back out through the same door.
- **Keys.** Some doors need a key (Old Dungeon Key, Copper Dungeon Key, Skeleton Key, Ornate Skeleton Key).
- **Lockpicking.** No key? A Lockpick opens a lock-picking minigame: lift each pin and set it in its sweet spot before
  the timer runs out. Slip, and a pick breaks. Lockpicking is a skill: higher levels widen the sweet spots, extend
  the timer and cut the snap chance.

## For admins and builders

- Build dungeon **sections** in the world inside a Dungeon Section Footprint, tag its faces and role, then save it.
  The generator assembles all sections of a type into a dungeon.
- Place a dungeon door, pick which dungeon it generates (a vanilla theme or your custom type), the interior
  environment, its name and lock mode, then generate.
- An Environment Box forces an interior sky and biome anywhere, with presets on the hammer.
- Saved sections are PlanBuild-compatible `.blueprint` files.
- Console: `firesdungeon_save`, `firesdungeon_list`, `firesdungeon_place`, `firesdungeon_browser`,
  `firesdungeon_generate`, `firesdungeon_worldgen`. Authoring pieces are admin-only.

The in-game help page has the full walkthrough.

Requires FiresUnifiedCore. Part of the Fires mod family.
