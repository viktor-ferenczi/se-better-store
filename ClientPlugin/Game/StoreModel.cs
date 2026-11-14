using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using ClientPlugin.Trading;
using HarmonyLib;
using Sandbox.Game;
using Sandbox.Game.Entities;
using Sandbox.Game.Entities.Blocks;
using Sandbox.Game.Entities.Character;
using Sandbox.Game.Entities.Character.Components;
using Sandbox.Game.GameSystems.BankingAndCurrency;
using Sandbox.Game.World;
using VRage;
using VRage.Game.Entity;
using VRage.Game.ObjectBuilders.Definitions;
using VRage.Utils;

namespace ClientPlugin.Game;

/// <summary>A docked ship the Store trades with, or the player's character.</summary>
internal sealed class Ship
{
    public string Name;
    public bool IsCharacter;

    /// <summary>The blocks with an inventory, tanks among them; or the character.</summary>
    public readonly List<MyEntity> Inventories = new List<MyEntity>();
}

/// <summary>An inventory or tank of a ship, with the units of an item that fit into it or that it holds.</summary>
internal struct Slot
{
    public long EntityId;
    public string Name;
    public long Room;
}

/// <summary>A Store as the screen sees it: its rows, the ships docked to it, and trades split over a ship.</summary>
internal sealed class StoreModel
{
    private const double AnswerTimeoutSeconds = 10;

    // The buy request is internal to the game, unlike the sell request
    private static readonly Action<MyStoreBlock, long, int, long, double, Action<MyStoreBuyItemResult>> CreateBuyRequest =
        AccessTools.MethodDelegate<Action<MyStoreBlock, long, int, long, double, Action<MyStoreBuyItemResult>>>(
            AccessTools.Method(typeof(MyStoreBlock), "CreateBuyRequest")
        );

    private sealed class Step
    {
        public Row Row;
        public Listing Listing;
        public long EntityId;
        public int Count;
    }

    public readonly MyStoreBlock Store;
    public readonly List<Row> Rows = new List<Row>();
    public readonly List<Ship> Ships = new List<Ship>();
    private readonly Dictionary<Row, Listing> listings = new Dictionary<Row, Listing>();

    private readonly Queue<Step> steps = new Queue<Step>();
    private readonly Stopwatch waiting = new Stopwatch();
    private int requestNumber;
    private long tradedUnits;
    private long tradedMoney;
    private int tradedItems;
    private Listing tradedListing;
    private Side tradedSide;
    private double economyTick;
    private bool closed;

    /// <summary>True while the Store's items are being loaded.</summary>
    public bool Loading { get; private set; }

    /// <summary>True while the requests of a trade are being sent.</summary>
    public bool Trading => steps.Count != 0 || waiting.IsRunning;

    /// <summary>The result of the last trade, for the status line.</summary>
    public string Status { get; private set; } = "";

    public bool StatusIsError { get; private set; }

    /// <summary>Raised when the rows, the ships or the status change.</summary>
    public event Action Changed;

    /// <summary>Raised with the reason when the server refuses a trade.</summary>
    public event Action<string> Refused;

    public StoreModel(MyStoreBlock store)
    {
        Store = store;
    }

    public void Open()
    {
        LotInfo.Listen();
        LotInfo.Changed += OnLots;
        Store.CreateGetConnectedGridInventoriesRequest(OnInventories);
        Refresh();
    }

    public void Close()
    {
        closed = true;
        LotInfo.Changed -= OnLots;
        steps.Clear();
        waiting.Reset();
    }

    public void Refresh()
    {
        Loading = true;
        Store.CreateGetStoreItemsRequest(OnItems, isPlayerRequest: true);
    }

    public long Balance =>
        MyBankingSystem.Static.TryGetAccountInfo(MySession.Static.LocalPlayerId, out var account)
            ? account.Balance
            : 0;

    public Listing ListingOf(Entry entry) => listings[entry.Unit ?? entry.LotRow];

    /// <summary>The lines of the Buy or the Sell tab. The Store can't take gas or ships, so those orders are left out.</summary>
    public List<Entry> Entries(Side side, bool merge) =>
        Entry.Build(
            Rows.Where(r => side == Side.Buy || !(listings[r].IsGas || listings[r].IsGrid)),
            side,
            merge
        );

    // --- loading ---

    private void OnItems(
        List<MyStoreItem> items,
        double lastEconomyTick,
        float offersBonus,
        float ordersBonus,
        bool isNpc
    )
    {
        if (closed)
            return;
        Loading = false;
        economyTick = lastEconomyTick;
        Rows.Clear();
        listings.Clear();
        foreach (var item in items)
        {
            var listing = Listing.Of(item);
            if (listing == null || item.Amount <= 0)
                continue;
            bool offer = item.StoreItemType == StoreItemTypes.Offer;
            var row = new Row
            {
                Id = item.Id,
                Item = listing.Key,
                Side = offer ? Side.Buy : Side.Sell,
                Amount = item.Amount,
                Price = item.PricePerUnit,
                Bonus = offer ? offersBonus : ordersBonus,
            };
            Rows.Add(row);
            listings[row] = listing;
        }
        ApplyLots();
        Changed?.Invoke();
    }

    private void OnLots()
    {
        ApplyLots();
        Changed?.Invoke();
    }

    private void ApplyLots()
    {
        // Gas and ships are never sold in lots
        var items = Rows.Where(r => !listings[r].IsGas && !listings[r].IsGrid).ToList();
        if (LotInfo.Known)
            Lots.Apply(items, LotInfo.ByRowId);
        else
            Lots.Guess(items);
    }

    private void OnInventories(List<long> entityIds)
    {
        if (closed)
            return;
        Ships.Clear();
        var blocks = entityIds
            .Select(id => MyEntities.TryGetEntityById(id, out MyCubeBlock block) ? block : null)
            .Where(block => block != null);
        // A ship is all the grids mechanically joined to the docked one
        foreach (var group in blocks.GroupBy(b => (object)MyCubeGridGroups.Static.Mechanical.GetGroup(b.CubeGrid) ?? b.CubeGrid))
        {
            var ship = new Ship
            {
                Name = group.Select(b => b.CubeGrid).Distinct().OrderByDescending(g => g.BlocksCount).First().DisplayName,
            };
            ship.Inventories.AddRange(group.OrderBy(b => b.DisplayNameText, StringComparer.InvariantCultureIgnoreCase));
            Ships.Add(ship);
        }
        Ships.Sort((a, b) => string.Compare(a.Name, b.Name, StringComparison.InvariantCultureIgnoreCase));
        var character = MySession.Static.LocalCharacter;
        if (character != null)
        {
            var suit = new Ship { Name = "Character", IsCharacter = true };
            suit.Inventories.Add(character);
            Ships.Add(suit);
        }
        Changed?.Invoke();
    }

    // --- inventories ---

    /// <summary>The inventories of the ship a trade of the line can use, each with its room for the item.</summary>
    public List<Slot> Slots(Entry entry, Ship ship)
    {
        var listing = ListingOf(entry);
        var slots = new List<Slot>();
        if (listing.IsGrid)
        {
            // A bought ship appears next to the Store; the request only needs the buyer
            var character = MySession.Static.LocalCharacter;
            if (character != null)
                slots.Add(new Slot { EntityId = character.EntityId, Name = "Next to the Store", Room = entry.Available });
            return slots;
        }
        foreach (var entity in ship.Inventories)
        {
            long room;
            if (listing.IsGas)
            {
                if (entity is MyCharacter character)
                    // The suit takes any amount as long as it isn't full
                    room = character.OxygenComponent?.GetGasFillLevel(listing.Id) < 1f ? 1 : 0;
                else if (entity is MyGasTank tank && tank.BlockDefinition.StoredGasId == listing.Id)
                    room = (long)((1.0 - tank.FilledRatio) * tank.Capacity / 1000.0);
                else
                    continue;
            }
            else
            {
                if (!entity.TryGetInventory(out MyInventory inventory))
                    continue;
                if (entry.Side == Side.Buy)
                    room = inventory.CheckConstraint(listing.Id)
                        ? (long)(double)inventory.ComputeAmountThatFits(listing.Id)
                        : 0;
                else
                    room = (long)(double)inventory.GetItemAmount(listing.Id);
            }
            slots.Add(
                new Slot
                {
                    EntityId = entity.EntityId,
                    Name = entity is MyCubeBlock block ? block.DisplayNameText : "Character",
                    Room = Math.Max(0, room),
                }
            );
        }
        return slots;
    }

    /// <summary>Units of the line's item in the ship's inventories. Zero for gas and ships.</summary>
    public long Held(Ship ship, Entry entry)
    {
        var listing = ListingOf(entry);
        if (listing.IsGas || listing.IsGrid)
            return 0;
        long total = 0;
        foreach (var entity in ship.Inventories)
            if (entity.TryGetInventory(out MyInventory inventory))
                total += (long)(double)inventory.GetItemAmount(listing.Id);
        return total;
    }

    /// <summary>The used and the whole volume of the ship's inventories, in liters.</summary>
    public static void Cargo(Ship ship, out double used, out double capacity)
    {
        used = 0;
        capacity = 0;
        foreach (var entity in ship.Inventories)
        {
            if (!entity.TryGetInventory(out MyInventory inventory))
                continue;
            used += (double)inventory.CurrentVolume * 1000;
            capacity += (double)inventory.MaxVolume * 1000;
        }
    }

    /// <summary>The gas in the ship's hydrogen tanks and their capacity, in liters.</summary>
    public static void Hydrogen(Ship ship, out double filled, out double capacity)
    {
        filled = 0;
        capacity = 0;
        foreach (var tank in ship.Inventories.OfType<MyGasTank>())
        {
            if (tank.BlockDefinition.StoredGasId != MyCharacterOxygenComponent.HydrogenId)
                continue;
            filled += tank.FilledRatio * tank.Capacity;
            capacity += tank.Capacity;
        }
    }

    // --- trading ---

    /// <summary>
    /// Trades an amount of each line with the ship, one request per row and inventory, one after the
    /// other. False, with nothing sent, if a trade is running or an amount can't be traded whole.
    /// </summary>
    public bool Trade(IReadOnlyList<KeyValuePair<Entry, long>> trades, Ship ship)
    {
        if (Trading || trades.Count == 0)
            return false;
        long balance = Balance;
        var planned = new List<Step>();
        foreach (var trade in trades)
        {
            var entry = trade.Key;
            var slots = Slots(entry, ship);
            var room = slots.Select(s => s.Room).ToList();
            if (!Planner.Possible(entry, trade.Value, room, balance))
                return false;
            var quote = Planner.Split(entry, trade.Value);
            if (entry.Side == Side.Buy)
                balance -= quote.Cost;
            foreach (var request in Planner.Place(quote, room, out _))
                planned.Add(
                    new Step
                    {
                        Row = request.Row,
                        Listing = ListingOf(entry),
                        EntityId = slots[request.Slot].EntityId,
                        Count = request.Count,
                    }
                );
        }
        foreach (var step in planned)
            steps.Enqueue(step);
        tradedUnits = 0;
        tradedMoney = 0;
        tradedItems = 0;
        tradedListing = null;
        tradedSide = trades[0].Key.Side;
        SetStatus("", false);
        SendNext();
        return true;
    }

    private void SendNext()
    {
        waiting.Reset();
        if (steps.Count == 0)
        {
            Finish();
            return;
        }
        var step = steps.Peek();
        int number = ++requestNumber;
        waiting.Start();
        if (step.Row.Side == Side.Buy)
            CreateBuyRequest(
                Store,
                step.Row.Id,
                step.Count,
                step.EntityId,
                economyTick,
                result => OnAnswer(number, result.Result == MyStoreBuyItemResults.Success, "Buy", result.Result.ToString())
            );
        else
            Store.CreateSellItemRequest(
                step.Row.Id,
                step.Count,
                step.EntityId,
                economyTick,
                result => OnAnswer(number, result.Result == MyStoreSellItemResults.Success, "Sell", result.Result.ToString())
            );
    }

    private void OnAnswer(int number, bool success, string kind, string result)
    {
        // An answer that comes after its request timed out, or after the screen closed
        if (closed || number != requestNumber || steps.Count == 0)
            return;
        if (!success)
        {
            Fail(Reason(kind, result));
            return;
        }
        var step = steps.Dequeue();
        tradedUnits += (long)step.Count * step.Row.Lot;
        tradedMoney += step.Row.Cost(step.Count);
        if (tradedListing != step.Listing)
            tradedItems++;
        tradedListing = step.Listing;
        SendNext();
    }

    /// <summary>The server drops some bad requests without an answer. Call this every frame.</summary>
    public void Update()
    {
        if (waiting.IsRunning && waiting.Elapsed.TotalSeconds > AnswerTimeoutSeconds)
            Fail("The server did not answer the request.");
    }

    private void Fail(string reason)
    {
        steps.Clear();
        waiting.Reset();
        requestNumber++;
        SetStatus(tradedUnits == 0 ? "Nothing was traded." : "Stopped. " + Summary(), true);
        Refused?.Invoke(reason);
        Refresh();
    }

    private void Finish()
    {
        SetStatus(Summary(), false);
        Refresh();
    }

    private string Summary()
    {
        string verb = tradedSide == Side.Buy ? "Bought" : "Sold";
        string money = Amounts.Number(tradedMoney) + " SC";
        return tradedItems == 1
            ? $"{verb} {Amounts.Format(tradedUnits, tradedListing.Unit)} of {tradedListing.Name} for {money}."
            : $"{verb} {tradedItems} items for {money}.";
    }

    private void SetStatus(string text, bool isError)
    {
        Status = text;
        StatusIsError = isError;
        Changed?.Invoke();
    }

    // The game's own text for a refusal, like "StoreBuy_Error_Text_NotEnoughMoney"
    private static string Reason(string kind, string result)
    {
        foreach (
            string name in new[]
            {
                $"Store{kind}_Error_Text_{result}",
                $"StoreBuy_Error_Text_{result}",
                $"Store{kind}_Error_{result}",
                $"StoreBuy_Error_{result}",
            }
        )
        {
            var id = MyStringId.GetOrCompute(name);
            if (MyTexts.Exists(id))
                return MyTexts.GetString(id);
        }
        return result;
    }
}
