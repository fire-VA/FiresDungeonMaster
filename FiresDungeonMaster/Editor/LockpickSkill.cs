using FiresCore.SkillSystem;
using UnityEngine;

namespace FiresDungeonMaster.Editor
{
    /// <summary>
    /// The "Lockpicking" skill for FiresDungeonMaster, registered through the shared FUC skill system
    /// (<see cref="FiresSkillRegistry"/>). Higher Lockpicking widens each pin's sweet-spot window, grants more time on
    /// the lock timer, and sharply lowers the chance a pick snaps. XP is earned per pin set during the minigame.
    ///
    /// Registered once at plugin Setup (before any character loads, so vanilla ZPackage persistence keeps it); the icon
    /// is refreshed to the lockpick's inventory icon once ObjectDB.Awake has built it. Registration is idempotent.
    /// </summary>
    internal static class LockpickSkill
    {
        public const string SkillName = "Lockpicking";
        private const string SkillDesc =
            "Your mastery of tumblers and tension wrenches. Each level widens every pin's sweet spot, buys you more " +
            "time on the lock, makes your picks far less likely to snap, and helps seated pins hold their set when " +
            "a pick does break.";

        // XP granted per pin successfully set. Vanilla skill gain slows as the level rises, so early Lockpicking climbs
        // quickly then tapers — the standard vanilla feel.
        public const float XpPerPin = 0.5f;

        private static bool _registered;
        public static Skills.SkillType SkillType { get; private set; }

        /// <summary>Register the skill, or refresh its icon. Safe before the icon exists (pass null) and to re-call later.</summary>
        public static void Register(Sprite icon = null)
        {
            var s = FiresSkillRegistry.Register(SkillName, SkillDesc, icon);
            SkillType = s.SkillType;
            _registered = true;
        }

        /// <summary>Local player's Lockpicking factor (0..1). 0 when unregistered or off a live player (e.g. main menu).</summary>
        public static float LocalFactor()
        {
            var p = Player.m_localPlayer;
            return (_registered && p != null) ? p.GetFiresSkillFactor(SkillType) : 0f;
        }

        /// <summary>Local player's Lockpicking level (0..100) — for the minigame's skill readout.</summary>
        public static float LocalLevel()
        {
            var p = Player.m_localPlayer;
            return (_registered && p != null) ? p.GetFiresSkillLevel(SkillType) : 0f;
        }

        /// <summary>Grant Lockpicking XP to the local player. No-op off a live player.</summary>
        public static void GrantXp(float amount)
        {
            var p = Player.m_localPlayer;
            if (_registered && p != null) p.RaiseFiresSkill(SkillType, amount);
        }
    }
}
