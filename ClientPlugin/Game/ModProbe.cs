using Sandbox.ModAPI;
using VRage.Input;

namespace ClientPlugin.Game;

/// <summary>
/// Tells a mod with its own Store screen, such as the Octahedron world's mod, that Better Store
/// opens the Store this time, so the mod leaves F to it. A mod can't see plugins, so it sends
/// <see cref="Probe"/> as a local mod message just before it would open its screen, and Better
/// Store answers <see cref="Answer"/> at once while its screen is on and would open.
/// </summary>
internal static class ModProbe
{
    // "BSTOREPR" and "BSTOREON"
    public const long Probe = 0x4253544F52455052;
    public const long Answer = 0x4253544F52454F4E;

    // The game keeps mod message handlers for the whole process, across sessions
    public static void Listen() => MyAPIUtilities.Static.RegisterMessageHandler(Probe, OnProbe);

    private static void OnProbe(object payload)
    {
        // The same conditions StoreUsePatch opens the screen on
        if (Config.Current.Enabled && !MyInput.Static.IsJoystickLastUsed)
            MyAPIUtilities.Static.SendModMessage(Answer, true);
    }
}
