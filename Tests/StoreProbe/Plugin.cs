using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using HarmonyLib;
using Sandbox.Game;
using Sandbox.Game.Entities;
using Sandbox.Game.Entities.Blocks;
using Sandbox.Game.Entities.Cube;
using Sandbox.Game.GameSystems.BankingAndCurrency;
using Sandbox.Game.World;
using Sandbox.Graphics.GUI;
using Sandbox.ModAPI;
using VRage;
using VRage.FileSystem;
using VRage.Game;
using VRage.Game.Entity;
using VRage.Game.Entity.UseObject;
using VRage.Game.ModAPI;
using VRage.Game.ObjectBuilders.Definitions;
using VRage.ObjectBuilders;
using VRage.ObjectBuilders.Private;
using VRage.Plugins;
using VRageMath;

namespace BetterStoreProbe;

// Test only, for Tests/rig: sets up the offline test world of the Better Store plugin and reports
// on it. The rig writes a command into <user data>/BetterStoreProbe/cmd-<n>.txt; the probe runs it
// on the game thread and writes its answer lines into out-<n>.txt.
//
//   setup                       docks the ship (trading on, connectors locked), gives the Store to
//                               an NPC faction with money, anyone may use it; OK when done
//   reset                       removes every row of the Store
//   offer|order <item> <amount> <price> [lot]
//                               adds a row; item is Ore/Stone, Component/SteelPlate or Hydrogen.
//                               A lot row moves lot units of the item per unit traded, like on
//                               Octahedron's server. Answers ROW <id>
//   stock <id> <amount>         sets the amount of a row
//   rows                        ROW <id> <offer|order> <item> <amount> <price>
//   clear                       empties the ship's inventories and its hydrogen tanks
//   fill <block> <item> <amount>  adds items to a block of the ship, by its name
//   tank <ratio>                fills the ship's hydrogen tanks to a ratio
//   inv                         INV <block>|<item>=<amount>;...  and  TANK <block> <liters>, then
//                               INV Character|... for the player's suit
//   lots                        sends the plugin the lot rows as a server would (channel 14860)
//   use                         uses the Store as the F key does
//   usespot                     SPOT x y z: the middle of the Store's use area, for aiming at it
//   enabled <true|false>        sets the Better Store plugin's Enabled option
//   faction                     FACTION <balance>
//   control <name>              CONTROL <type> enabled=<bool> visible=<bool>, then ITEM <text>
//                               for each item of a combo box, on the Better Store screen
//   closex                      clicks the X of the Better Store screen
// ReSharper disable once UnusedType.Global
public class Plugin : IPlugin
{
    public const string StationName = "BS Station";
    public const string ShipName = "BS Ship";
    private const string FactionTag = "BST";

    // Rows of lots and their lot size, by row id
    internal static readonly Dictionary<long, int> Lots = new Dictionary<long, int>();

    private int frame;

    public void Init(object gameInstance)
    {
        new Harmony("BetterStoreProbe").PatchAll(Assembly.GetExecutingAssembly());
    }

    public void Dispose() { }

    public void Update()
    {
        if (++frame % 10 != 0 || MySession.Static is not { Ready: true })
            return;
        string folder = Path.Combine(MyFileSystem.UserDataPath, "BetterStoreProbe");
        if (!Directory.Exists(folder))
            return;
        foreach (string file in Directory.GetFiles(folder, "cmd-*.txt").OrderBy(f => f))
        {
            string command = File.ReadAllText(file).Trim();
            File.Delete(file);
            List<string> output;
            try
            {
                output = Run(command.Split(' ', StringSplitOptions.RemoveEmptyEntries));
            }
            catch (Exception e)
            {
                output = new List<string> { "ERROR " + e };
            }
            string result = Path.Combine(folder, "out-" + Path.GetFileName(file).Substring(4));
            File.WriteAllLines(result + ".tmp", output);
            File.Move(result + ".tmp", result, true);
        }
    }

    private static MyCubeGrid Grid(string name) =>
        MyEntities.GetEntities().OfType<MyCubeGrid>().FirstOrDefault(g => g.DisplayName == name);

    private static MyStoreBlock Store => Grid(StationName).GetFatBlocks<MyStoreBlock>().First();

    private static IEnumerable<MyCubeBlock> ShipBlocks =>
        MyCubeGridGroups
            .Static.Mechanical.GetGroup(Grid(ShipName))
            .Nodes.SelectMany(n => n.NodeData.GetFatBlocks());

    private static MyDefinitionId Item(string name) =>
        MyDefinitionId.Parse("MyObjectBuilder_" + name);

    private static MyFixedPoint Amount(string text) =>
        (MyFixedPoint)double.Parse(text, CultureInfo.InvariantCulture);

    private static List<string> Run(string[] args)
    {
        var output = new List<string>();
        var store = args[0] is "use" or "enabled" or "faction" or "inv" ? null : Store;
        switch (args[0])
        {
            case "setup":
                output.Add(Setup(store));
                break;
            case "reset":
                foreach (var item in store.PlayerItems.ToList())
                    store.RemoveStoreItem(item);
                Lots.Clear();
                output.Add("OK");
                break;
            case "offer":
            case "order":
            {
                var type = args[0] == "offer" ? StoreItemTypes.Offer : StoreItemTypes.Order;
                int amount = int.Parse(args[2], CultureInfo.InvariantCulture);
                int price = int.Parse(args[3], CultureInfo.InvariantCulture);
                long id = MyEntityIdentifier.AllocateId(
                    MyEntityIdentifier.ID_OBJECT_TYPE.STORE_ITEM
                );
                // Rows made through the mod API: the Store makes and takes the items itself
                var row =
                    args[1] == "Hydrogen"
                        ? new MyStoreItem(id, amount, price, type, ItemTypes.Hydrogen)
                        : new MyStoreItem(
                            id,
                            Item(args[1]),
                            amount,
                            price,
                            ItemTypes.PhysicalItem,
                            type
                        );
                row.IsCustomStoreItem = true;
                store.InsertStoreItem(row);
                if (args.Length > 4)
                    Lots[id] = int.Parse(args[4], CultureInfo.InvariantCulture);
                output.Add("ROW " + id);
                break;
            }
            case "stock":
            {
                long id = long.Parse(args[1], CultureInfo.InvariantCulture);
                store.PlayerItems.First(i => i.Id == id).Amount = int.Parse(
                    args[2],
                    CultureInfo.InvariantCulture
                );
                output.Add("OK");
                break;
            }
            case "rows":
                foreach (var item in store.PlayerItems)
                    output.Add(
                        $"ROW {item.Id} {item.StoreItemType} {(item.Item?.ToString() ?? item.ItemType.ToString())} {item.Amount} {item.PricePerUnit}"
                    );
                break;
            case "clear":
                foreach (var block in ShipBlocks)
                {
                    if (block is MyGasTank tank)
                        ((Sandbox.ModAPI.IMyGasTank)tank).ChangeFilledRatio(0, true);
                    for (int i = 0; i < block.InventoryCount; i++)
                        block.GetInventory(i).Clear();
                }
                output.Add("OK");
                break;
            case "fill":
            {
                var block = ShipBlocks.First(b => b.DisplayNameText == args[1].Replace('_', ' '));
                var id = Item(args[2]);
                bool added = block
                    .GetInventory()
                    .AddItems(Amount(args[3]), MyObjectBuilderSerializerKeen.CreateNewObject(id));
                output.Add(added ? "OK" : "FULL");
                break;
            }
            case "tank":
                foreach (var tank in ShipBlocks.OfType<MyGasTank>())
                    ((Sandbox.ModAPI.IMyGasTank)tank).ChangeFilledRatio(
                        double.Parse(args[1], CultureInfo.InvariantCulture),
                        true
                    );
                output.Add("OK");
                break;
            case "inv":
                foreach (var block in ShipBlocks.OrderBy(b => b.DisplayNameText))
                {
                    if (block is MyGasTank tank)
                        output.Add(
                            FormattableString.Invariant(
                                $"TANK {block.DisplayNameText} {tank.FilledRatio * tank.Capacity:0}"
                            )
                        );
                    if (!block.HasInventory)
                        continue;
                    output.Add(
                        $"INV {block.DisplayNameText}|{string.Join(";", Items(block.GetInventory()))}"
                    );
                }
                output.Add(
                    $"INV Character|{string.Join(";", Items(MySession.Static.LocalCharacter.GetInventory()))}"
                );
                break;
            case "lots":
            {
                string text =
                    "BetterStoreLots 1\n" + string.Concat(Lots.Select(l => $"{l.Key} {l.Value}\n"));
                MyAPIGateway.Multiplayer.SendMessageTo(
                    14860,
                    Encoding.UTF8.GetBytes(text),
                    MyAPIGateway.Multiplayer.MyId
                );
                output.Add("OK");
                break;
            }
            case "use":
            {
                var character = MySession.Static.LocalCharacter;
                var objects = new List<IMyUseObject>();
                Store.UseObjectsComponent.GetInteractiveObjects(objects);
                var use = objects.First(o => o.GetType().Name == "MyUseObjectStoreBlock");
                use.Use(UseActionEnum.Manipulate, character);
                output.Add("OK");
                break;
            }
            case "usespot":
            {
                var objects = new List<IMyUseObject>();
                Store.UseObjectsComponent.GetInteractiveObjects(objects);
                var spot = objects
                    .First(o => o.GetType().Name == "MyUseObjectStoreBlock")
                    .ActivationMatrix.Translation;
                output.Add(FormattableString.Invariant($"SPOT {spot.X} {spot.Y} {spot.Z}"));
                break;
            }
            case "enabled":
            {
                // The plugin's Config.Current.Enabled, found by its Store screen's type
                var assembly = AppDomain
                    .CurrentDomain.GetAssemblies()
                    .First(a => a.GetType("ClientPlugin.Gui.StoreScreen") != null);
                var config = assembly.GetType("ClientPlugin.Config");
                var current = config.GetField("Current").GetValue(null);
                config.GetProperty("Enabled").SetValue(current, bool.Parse(args[1]));
                output.Add("OK");
                break;
            }
            case "control":
            {
                var control = Find(BetterStoreScreen().Controls, args[1]);
                output.Add(
                    $"CONTROL {control.GetType().Name} enabled={control.Enabled} visible={control.Visible}"
                );
                if (control is MyGuiControlCombobox combo)
                    for (int i = 0; i < combo.GetItemsCount(); i++)
                        output.Add("ITEM " + combo.GetItemByIndex(i).Value);
                break;
            }
            case "closex":
            {
                var close = (MyGuiControlButton)
                    AccessTools
                        .Field(typeof(MyGuiScreenBase), "m_closeButton")
                        .GetValue(BetterStoreScreen());
                close.PressButton();
                output.Add("OK");
                break;
            }
            case "shipowner":
            {
                // me: the player owns the ship; npc: the Store's faction owns all of it but the
                // connector, which undocks when its owner changes
                long owner =
                    args[1] == "me"
                        ? MySession.Static.LocalPlayerId
                        : MySession.Static.Factions.TryGetFactionByTag(FactionTag).FounderId;
                foreach (var block in ShipBlocks.Where(b => !(b is MyShipConnector)))
                    block.ChangeOwner(owner, MyOwnershipShareModeEnum.None);
                output.Add("OK");
                break;
            }
            case "mate":
            {
                // on: the player joins the Store's faction, whose founder owns the ship and
                // shares it with the faction. The server then lists the ship for the player.
                var faction = MySession.Static.Factions.TryGetFactionByTag(FactionTag);
                long player = MySession.Static.LocalPlayerId;
                bool on = args[1] == "on";
                if (on)
                    MySession.Static.Factions.AddPlayerToFaction(player, faction.FactionId);
                else
                    ((IMyFactionCollection)MySession.Static.Factions).KickPlayerFromFaction(player);
                foreach (var block in ShipBlocks.Where(b => !(b is MyShipConnector)))
                    block.ChangeOwner(
                        on ? faction.FounderId : player,
                        on ? MyOwnershipShareModeEnum.Faction : MyOwnershipShareModeEnum.None
                    );
                output.Add("OK");
                break;
            }
            case "listed":
            {
                // The inventories the server lists for the player at the station's connector
                var connector = Grid(StationName).GetFatBlocks<MyShipConnector>().First();
                var listed = (List<long>)
                    AccessTools
                        .Method(typeof(MyShipConnector), "GetInventoryEntities")
                        .Invoke(connector, new object[] { MySession.Static.LocalPlayerId });
                output.Add("LISTED " + listed.Count);
                break;
            }
            case "ship":
            {
                var connector = ShipBlocks.OfType<MyShipConnector>().First();
                output.Add(
                    $"SHIP connected={connector.Connected} me={MySession.Static.LocalPlayerId} owners={string.Join(",", Grid(ShipName).BigOwners)} blockOwner={connector.OwnerId}"
                );
                break;
            }
            case "trading":
            {
                foreach (var connector in Grid(StationName).GetFatBlocks<MyShipConnector>())
                    connector.TradingEnabled.Value = args[1] == "on";
                output.Add("OK");
                break;
            }
            case "faction":
            {
                var faction = MySession.Static.Factions.TryGetFactionByTag(FactionTag);
                output.Add(
                    "FACTION "
                        + (faction == null ? -1 : MyBankingSystem.GetBalance(faction.FactionId))
                );
                break;
            }
            default:
                output.Add("UNKNOWN " + args[0]);
                break;
        }
        return output;
    }

    private static IEnumerable<string> Items(MyInventory inventory) =>
        inventory
            .GetItems()
            .Select(i =>
                $"{i.Content.GetId().ToString().Replace("MyObjectBuilder_", "")}={i.Amount}"
            );

    private static MyGuiScreenBase BetterStoreScreen() =>
        MyScreenManager.Screens.First(s => s.GetFriendlyName() == "BetterStoreScreen");

    private static MyGuiControlBase Find(MyGuiControls controls, string name)
    {
        foreach (var control in controls)
        {
            if (control.Name == name)
                return control;
            if (control is MyGuiControlParent parent && Find(parent.Controls, name) is { } found)
                return found;
        }
        return null;
    }

    // Repeat until it answers OK: connecting and creating the faction take a few frames
    private static string Setup(MyStoreBlock store)
    {
        foreach (
            var battery in Grid(StationName)
                .GetFatBlocks<MyBatteryBlock>()
                .Concat(ShipBlocks.OfType<MyBatteryBlock>())
        )
            battery.CurrentStoredPower = battery.MaxStoredPower;
        var connectors = Grid(StationName).GetFatBlocks<MyShipConnector>().ToList();
        foreach (var connector in connectors)
            connector.TradingEnabled.Value = true;
        var shipConnector = ShipBlocks.OfType<MyShipConnector>().First();
        if (!shipConnector.Connected)
        {
            // The ship's connector goes right in front of the station's. Their collision pushes
            // the ship back a little, which still leaves it in reach.
            var station = connectors[0].WorldMatrix;
            var offset =
                station.Translation + station.Forward * 2.5 - shipConnector.WorldMatrix.Translation;
            var ship = shipConnector.CubeGrid;
            if (offset.Length() > 0.5)
            {
                var matrix = ship.WorldMatrix;
                matrix.Translation += offset;
                ship.Teleport(matrix);
                ship.Physics.LinearVelocity = Vector3.Zero;
                ship.Physics.AngularVelocity = Vector3.Zero;
                return "WAIT dock";
            }
            shipConnector.TryConnect();
            return "WAIT connector";
        }
        var faction = MySession.Static.Factions.TryGetFactionByTag(FactionTag);
        if (faction == null)
        {
            MySession.Static.Factions.CreateNPCFaction(FactionTag, "Better Store Test", "", "");
            return "WAIT faction";
        }
        if (store.OwnerId != faction.FounderId)
        {
            store.ChangeOwner(faction.FounderId, MyOwnershipShareModeEnum.Faction);
            store.AnyoneCanUse = true;
            MyBankingSystem.ChangeBalance(faction.FactionId, 1_000_000_000);
            return "WAIT owner";
        }
        return "OK";
    }
}

// A lot row moves lot units of its item for each one traded, as on Octahedron's server

[HarmonyPatch(typeof(MyStoreBlock), "BuyPhysicalItemInternal")]
// ReSharper disable once UnusedType.Global
internal static class BuyLotPatch
{
    private static void Prefix(MyStoreItem storeItem, out int __state) =>
        __state = storeItem.Amount;

    private static void Postfix(
        int amount,
        MyInventory inventory,
        MyStoreItem storeItem,
        int __state
    )
    {
        if (
            !Plugin.Lots.TryGetValue(storeItem.Id, out int lot)
            || storeItem.Amount != __state - amount
        )
            return;
        inventory.AddItems(
            (MyFixedPoint)(double)((long)amount * (lot - 1)),
            MyObjectBuilderSerializerKeen.CreateNewObject(storeItem.Item.Value)
        );
    }
}

[HarmonyPatch(typeof(MyStoreBlock), "SellToStation")]
// ReSharper disable once UnusedType.Global
internal static class SellLotPatch
{
    private static readonly MethodInfo Refuse = AccessTools.Method(
        typeof(MyStoreBlock),
        "SendSellItemResult",
        new[] { typeof(long), typeof(int), typeof(MyStoreSellItemResults) }
    );

    private static bool Prefix(
        MyStoreBlock __instance,
        long id,
        int amount,
        long sourceEntityId,
        out int __state
    )
    {
        __state = -1;
        var item = __instance.PlayerItems.FirstOrDefault(i => i.Id == id);
        if (item == null || !Plugin.Lots.TryGetValue(id, out int lot))
            return true;
        var inventory = MyEntities.GetEntityById(sourceEntityId)?.GetInventory();
        if (
            inventory == null
            || (double)inventory.GetItemAmount(item.Item.Value) < (double)amount * lot
        )
        {
            Refuse.Invoke(
                __instance,
                new object[] { id, amount, MyStoreSellItemResults.NotEnoughAmount }
            );
            return false;
        }
        __state = item.Amount;
        return true;
    }

    private static void Postfix(
        MyStoreBlock __instance,
        long id,
        int amount,
        long sourceEntityId,
        int __state
    )
    {
        var item = __instance.PlayerItems.FirstOrDefault(i => i.Id == id);
        if (__state < 0 || item == null || item.Amount != __state - amount)
            return;
        MyEntities
            .GetEntityById(sourceEntityId)
            .GetInventory()
            .RemoveItemsOfType(
                (MyFixedPoint)(double)((long)amount * (Plugin.Lots[id] - 1)),
                item.Item.Value
            );
    }
}
