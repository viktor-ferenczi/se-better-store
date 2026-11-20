using ClientPlugin.Gui;
using HarmonyLib;
using Sandbox.Game.Entities.Blocks;
using Sandbox.Game.Entities.Character;
using Sandbox.Game.Gui;
using Sandbox.Game.World;
using Sandbox.Graphics.GUI;
using SpaceEngineers.Game.Entities.UseObjects;
using VRage.Game;
using VRage.Game.Entity.UseObject;
using VRage.Input;
using VRage.ModAPI;

namespace ClientPlugin.Patches;

/// <summary>Opens the Better Store screen where the game would open its own Store screen.</summary>
// ReSharper disable once UnusedType.Global
[HarmonyPatch(typeof(MyUseObjectStoreBlock), nameof(MyUseObjectStoreBlock.Use))]
public static class StoreUsePatch
{
    // ReSharper disable once UnusedMember.Local
    private static bool Prefix(
        MyUseObjectStoreBlock __instance,
        UseActionEnum actionEnum,
        IMyEntity userEntity
    )
    {
        // The screen has no gamepad control: with a gamepad the game's own screen opens
        if (
            !Config.Current.Enabled
            || actionEnum != UseActionEnum.Manipulate
            || MyInput.Static.IsJoystickLastUsed
        )
            return true;
        // A server mod that opened its own Store screen on the F press takes precedence
        if (!(MyScreenManager.GetScreenWithFocus() is MyGuiScreenGamePlay))
            return true;
        if (
            !(userEntity is MyCharacter character)
            || !(__instance.Owner is MyStoreBlock store)
            || !store.IsWorking
        )
            return true;
        // The game's own access check. Where it fails, the game says so
        long identityId = character.ControllerInfo?.Controller?.Player?.Identity?.IdentityId ?? 0;
        if (
            identityId == 0
            || (
                !store.GetUserRelationToOwner(identityId).IsFriendly()
                && !store.AnyoneCanUse
                && !MySession.Static.AdminSettings.HasFlag(AdminSettingsEnum.UseTerminals)
            )
        )
            return true;
        StoreScreen.Open(store);
        return false;
    }
}
