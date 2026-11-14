using ClientPlugin.Trading;
using Sandbox.Definitions;
using Sandbox.Game.Entities.Blocks;
using Sandbox.Game.Entities.Character.Components;
using Sandbox.Game.Localization;
using VRage;
using VRage.Game;
using VRage.Game.ObjectBuilders.Definitions;

namespace ClientPlugin.Game;

/// <summary>What a Store row trades, as the screen needs it: its name, icon, unit and kind.</summary>
internal sealed class Listing
{
    public string Name;
    public string Icon;
    public Unit Unit;

    /// <summary>The item, or the gas of a gas offer.</summary>
    public MyDefinitionId Id;

    public bool IsGas;
    public bool IsGrid;

    /// <summary>Liters of cargo space a unit of the item takes.</summary>
    public double Volume;

    /// <summary>The key rows of the same item share.</summary>
    public string Key;

    /// <summary>Null for an item the game has no definition of.</summary>
    public static Listing Of(MyStoreItem item)
    {
        switch (item.ItemType)
        {
            case ItemTypes.PhysicalItem:
            {
                if (
                    item.Item == null
                    || !MyDefinitionManager.Static.TryGetDefinition(
                        item.Item.Value,
                        out MyPhysicalItemDefinition definition
                    )
                )
                    return null;
                MyDefinitionId id = item.Item.Value;
                return new Listing
                {
                    Name = definition.DisplayNameText,
                    Icon = FirstIcon(definition.Icons),
                    Unit =
                        id.TypeId == typeof(MyObjectBuilder_Ore)
                        || id.TypeId == typeof(MyObjectBuilder_Ingot)
                            ? Unit.Kg
                            : Unit.Pieces,
                    Id = id,
                    Volume = definition.Volume * 1000.0,
                    Key = id.ToString(),
                };
            }
            case ItemTypes.Oxygen:
                return Gas(
                    MyCharacterOxygenComponent.OxygenId,
                    MyTexts.GetString(MySpaceTexts.DisplayName_Item_Oxygen),
                    "Textures\\GUI\\Icons\\OxygenIcon.dds"
                );
            case ItemTypes.Hydrogen:
                return Gas(
                    MyCharacterOxygenComponent.HydrogenId,
                    MyTexts.GetString(MySpaceTexts.DisplayName_Item_Hydrogen),
                    "Textures\\GUI\\Icons\\HydrogenIcon.dds"
                );
            case ItemTypes.Gas:
            {
                if (
                    item.Item == null
                    || !MyDefinitionManager.Static.TryGetGasPropertyDefinition(
                        item.Item.Value,
                        out var gas
                    )
                )
                    return null;
                return Gas(item.Item.Value, gas.DisplayNameText, FirstIcon(gas.Icons));
            }
            case ItemTypes.Grid:
            {
                var prefab = MyDefinitionManager.Static.GetPrefabDefinition(item.PrefabName);
                string name = prefab?.DisplayNameString;
                if (
                    string.IsNullOrEmpty(name)
                    && prefab?.CubeGrids != null
                    && prefab.CubeGrids.Length != 0
                )
                    name = prefab.CubeGrids[0].DisplayName;
                return new Listing
                {
                    Name = string.IsNullOrEmpty(name) ? item.PrefabName : name,
                    Icon = FirstIcon(prefab?.Icons),
                    Unit = Unit.Pieces,
                    IsGrid = true,
                    // Every ship for sale is its own line
                    Key = "Grid/" + item.Id,
                };
            }
            default:
                return null;
        }
    }

    private static Listing Gas(MyDefinitionId id, string name, string icon) =>
        new Listing
        {
            Name = name,
            Icon = icon,
            Unit = Unit.Gas,
            Id = id,
            IsGas = true,
            Key = "Gas/" + id.SubtypeName,
        };

    private static string FirstIcon(string[] icons) =>
        icons != null && icons.Length != 0 ? icons[0] : null;
}
