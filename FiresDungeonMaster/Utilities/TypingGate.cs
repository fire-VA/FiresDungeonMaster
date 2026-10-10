using System;
using System.Reflection;

namespace FiresDungeonMaster
{
    /// <summary>
    /// 0.1.129 (Fire, team-wide: typing in any input field blocks every mod hotkey). Core 0.2.376's
    /// FiresCore.Input.FiresInputBlock.IsTyping() is the one shared check; it is read by reflection so FDM still loads on an
    /// older Core, where FiresInputBlock.IsCapturing is used instead.
    /// </summary>
    internal static class TypingGate
    {
        static bool _resolved;
        static MethodInfo _isTyping;

        internal static bool Typing
        {
            get
            {
                if (!_resolved)
                {
                    _resolved = true;
                    _isTyping = typeof(FiresCore.Input.FiresInputBlock).GetMethod("IsTyping", BindingFlags.Public | BindingFlags.Static, null, Type.EmptyTypes, null);
                    FiresLogger.LogVerbose($"typing gate: {(_isTyping != null ? "Core FiresInputBlock.IsTyping()" : "FiresInputBlock.IsCapturing (older Core)")}");
                }
                if (_isTyping != null)
                {
                    try { return (bool)_isTyping.Invoke(null, null); } catch { }
                }
                return FiresCore.Input.FiresInputBlock.IsCapturing;
            }
        }
    }
}
