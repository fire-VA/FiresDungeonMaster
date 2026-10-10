using UnityEngine;

namespace FiresDungeonMaster.UI
{
    // FiresDungeonMaster's sections for the shared FiresCore help panel (registration only).
    internal static class DungeonHelpContent
    {
        private const string ModId = "FiresDungeonMaster";
        private static bool _registered;

        public static void Register()
        {
            if (_registered || Application.isBatchMode) return;
            _registered = true;

            FiresCore.Help.HelpRegistry.RegisterSection(ModId, "", "Dungeon Doors, Keys & Lockpicking", 0, BuildPlayer);
            FiresCore.Help.HelpRegistry.RegisterSection(ModId, "", "Authoring Dungeons", 1, BuildAuthoring, adminOnly: true);
            FiresCore.Help.HelpRegistry.RegisterSection(ModId, "", "Commands & Config", 2, BuildCommands, adminOnly: true);
        }

        private static void BuildPlayer(FiresCore.Help.HelpContentWriter w)
        {
            w.Header("Dungeon Doors, Keys & Lockpicking");
            w.Paragraph("Grand doors in the world lead to entire dungeons — vanilla crypt themes or " +
                "custom-built ones. Press E on a door to enter.");
            w.Divider();

            w.SubHeader("Locked Doors");
            w.Bullet("Some doors need a KEY: Old Dungeon Key, Copper Dungeon Key, Skeleton Key, or the " +
                "Ornate Skeleton Key. Holding the right key, E turns the lock — 'The key turns — the " +
                "lock clicks open.'");
            w.Bullet("No key? A Lockpick opens the lock-picking minigame instead.");
            w.Bullet("With neither, the door tells you: 'This door requires a key.'");
            w.Divider();

            w.SubHeader("Picking a Lock");
            w.Bullet("Hold left-click and drag the pick under each pin (up to 5).");
            w.Bullet("Drag upward to lift the pin, and TAP E in its sweet spot to set it.");
            w.Bullet("Set every pin before the timer runs out to open the lock.");
            w.Bullet("Slip and the lock SNAPS — one lockpick breaks, and seated pins can pop back out.");
            w.Divider();

            w.SubHeader("The Lockpicking Skill");
            w.Paragraph("Lockpicking is a real skill: every set pin grants XP. Higher levels widen the " +
                "sweet spots, extend the timer, sharply cut snap chance, and help seated pins hold.");
            w.Divider();

            w.Paragraph("Inside, the dungeon has its own environment and an exit arch that brings you " +
                "back out through the door you entered.");
        }

        private static void BuildAuthoring(FiresCore.Help.HelpContentWriter w)
        {
            w.AdminHeader("Authoring Dungeons");
            w.Paragraph("Build dungeon SECTIONS in the world, save them, and the generator assembles " +
                "them into dungeons. Authoring pieces live on the 'Dungeon Sections' hammer tab " +
                "(admins only — regular players never see them).");
            w.AdminDivider();

            w.SubHeader("The Pipeline");
            w.Bullet("1. Place a Dungeon Section Footprint — a scalable wireframe room shell.");
            w.Bullet("2. Tag its faces: Shift+Scroll on an edge quad cycles Wall / Doorway / Open / " +
                "Window / Back / Left / Right. Doorway and Open faces become room connections.");
            w.Bullet("3. Tag the center sphere's ROLE (Center / Edge / Doorway / Corridor / Entry / " +
                "Exit) and give it a dungeon-type name — Shift+Alt+Right-click opens the settings menu.");
            w.Bullet("4. Build and decorate the room's interior freely.");
            w.Bullet("5. firesdungeon_save [name] — capture EVERYTHING in the footprint to a .blueprint.");
            w.Bullet("6. firesdungeon_generate <type> — assemble all sections of that type into a live " +
                "dungeon; firesdungeon_worldgen <type> opts it into world generation.");
            w.AdminDivider();

            w.SubHeader("Entry Doors");
            w.Paragraph("Place one of the four door pieces (Iron-Banded, Arched Oak, Planked, Grand " +
                "Arch), then Shift+Alt+Right-click it to configure: which dungeon it generates (a " +
                "vanilla theme — Forest/Sunken/Meadows Crypt, Frost Cave, Infested Mine, Troll Cave, " +
                "Goblin Camp, Ashlands ruins — or your custom type), the interior environment, its " +
                "display name, and the lock mode (Open / Key / Quest). Then Generate.");
            w.AdminDivider();

            w.SubHeader("Tools");
            w.Bullet((DungeonMasterConfig.SectionArchiveKey != null ? DungeonMasterConfig.SectionArchiveKey.Value.ToString() : "Shift+Y") + " — toggle the blueprint bench / saved-section browser (config: Blueprints / Section Archive key)");
            w.Bullet("A Dungeon Blueprint Desk piece opens the same browser on E");
            w.Bullet("Environment Box (EnvBox_vad) — force interior sky/biome anywhere");
            w.Bullet("Saved sections are PlanBuild-compatible .blueprints — they stay placeable by " +
                "Expand World / InfinityHammer even without this mod");
        }

        private static void BuildCommands(FiresCore.Help.HelpContentWriter w)
        {
            w.AdminHeader("Commands & Config");
            w.SubHeader("Commands");
            w.Bullet("firesdungeon_save [name] — capture the aimed footprint (admin)");
            w.Bullet("firesdungeon_list [type] — list saved sections");
            w.Bullet("firesdungeon_place <idOrName> [metres] — place a saved section (admin, host)");
            w.Bullet("firesdungeon_browser — toggle the section browser");
            w.Bullet("firesdungeon_types / firesdungeon_sections [type] — library listings");
            w.Bullet("firesdungeon_generate <type> [metres] — spawn the assembled dungeon (host)");
            w.Bullet("firesdungeon_worldgen <type> — opt a type into world generation");
            w.Bullet("firesdungeon_lockpick [Easy|Intermediate|Hard|Very Hard] — test the minigame free");
            w.AdminDivider();

            w.SubHeader("Config");
            w.Bullet("Blueprints.AllPiecesScalable — force every prefab scalable so blueprint scale " +
                "persists (auto-stands-down if Structure Tweaks is installed)");
            w.Bullet("Sections.CenterpointFallbackPrefab — the vanilla marker written at a section's " +
                "origin so blueprints stay portable");
            w.Bullet("General.Verbose Logging — authoring diagnostics");
        }
    }
}
