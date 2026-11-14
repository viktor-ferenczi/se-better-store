using System;
using System.Collections.Generic;
using System.Linq;

namespace ClientPlugin.Trading;

/// <summary>Units of a row to trade.</summary>
public struct Part
{
    public Row Row;
    public int Count;
}

/// <summary>How an amount of an item is traded on a line's rows.</summary>
public sealed class Quote
{
    public readonly List<Part> Parts = new List<Part>();

    /// <summary>Units of the item the parts move.</summary>
    public long Amount;

    /// <summary>Units asked for that no row can take: over the stock, or less than a lot.</summary>
    public long Left;

    public long Cost => Parts.Sum(p => p.Row.Cost(p.Count));
}

/// <summary>One request to the server: units of a row, into or out of one inventory.</summary>
public struct Request
{
    public Row Row;
    public int Slot;
    public int Count;
}

public static class Planner
{
    /// <summary>Splits an amount of the item over the line's rows, the better price per unit first.</summary>
    public static Quote Split(Entry entry, long amount)
    {
        var quote = new Quote();
        long left = Math.Max(0, amount);
        var unit = entry.Unit;
        var lot = entry.LotRow;
        bool lotFirst =
            lot != null
            && (
                unit == null
                || (
                    entry.Side == Side.Buy
                        ? lot.PricePerItem <= unit.PricePerItem
                        : lot.PricePerItem >= unit.PricePerItem
                )
            );

        void Take(Row row)
        {
            if (row == null)
                return;
            int count = (int)Math.Min(left / row.Lot, row.Amount);
            if (count <= 0)
                return;
            quote.Parts.Add(new Part { Row = row, Count = count });
            left -= (long)count * row.Lot;
        }

        Take(lotFirst ? lot : unit);
        Take(lotFirst ? unit : lot);
        quote.Amount = Math.Max(0, amount) - left;
        quote.Left = left;
        return quote;
    }

    /// <summary>
    /// Spreads a quote over the inventories. room[i] is the units of the item that fit into
    /// inventory i (buying) or that it holds (selling). A lot goes whole into one inventory.
    /// </summary>
    public static List<Request> Place(Quote quote, IReadOnlyList<long> room, out long left)
    {
        var requests = new List<Request>();
        var free = room.ToArray();
        left = 0;
        // Lots first: they need the room in one piece
        foreach (var part in quote.Parts.OrderByDescending(p => p.Row.Lot))
        {
            int count = part.Count;
            for (int i = 0; i < free.Length && count > 0; i++)
            {
                int here = (int)Math.Min(count, free[i] / part.Row.Lot);
                if (here <= 0)
                    continue;
                requests.Add(
                    new Request
                    {
                        Row = part.Row,
                        Slot = i,
                        Count = here,
                    }
                );
                free[i] -= (long)here * part.Row.Lot;
                count -= here;
            }
            left += (long)count * part.Row.Lot;
        }
        return requests;
    }

    /// <summary>True if the whole amount can be traded: the rows take it, it is paid for and it is placed.</summary>
    public static bool Possible(Entry entry, long amount, IReadOnlyList<long> room, long balance)
    {
        if (amount <= 0)
            return false;
        var quote = Split(entry, amount);
        if (quote.Left > 0)
            return false;
        if (entry.Side == Side.Buy && quote.Cost > balance)
            return false;
        Place(quote, room, out long left);
        return left == 0;
    }

    /// <summary>
    /// The most of the item that can be traded: buying, what the stock, the money and the room
    /// allow; selling, what the inventories hold and the Store wants.
    /// </summary>
    public static long Max(Entry entry, IReadOnlyList<long> room, long balance)
    {
        // What the rows take of an amount grows with it, so the largest amount whose tradable
        // part is paid for and placed gives the most. That part is the result: over the stock
        // of single units only whole lots are left, and not every amount is a whole lot.
        bool Fits(long amount)
        {
            var quote = Split(entry, amount);
            if (entry.Side == Side.Buy && quote.Cost > balance)
                return false;
            Place(quote, room, out long left);
            return left == 0;
        }

        long high = Math.Min(entry.Available, room.Sum());
        long low = 0;
        while (low < high)
        {
            long middle = low + (high - low + 1) / 2;
            if (Fits(middle))
                low = middle;
            else
                high = middle - 1;
        }
        return Split(entry, low).Amount;
    }
}

/// <summary>
/// What the earlier lines of one trade take from each inventory. Two lines of one item, like a
/// row of lots shown on its own next to its row of single units, share what the inventories hold:
/// what one line sells isn't there for the next.
/// </summary>
public sealed class Usage
{
    private readonly Dictionary<(string, int), long> used = new Dictionary<(string, int), long>();

    /// <summary>The room for the line's item left of room[i], by inventory.</summary>
    public List<long> Left(Entry entry, IReadOnlyList<long> room) =>
        room.Select(
                (r, i) => Math.Max(0, r - (used.TryGetValue((entry.Item, i), out long u) ? u : 0))
            )
            .ToList();

    public void Add(Entry entry, IEnumerable<Request> requests)
    {
        foreach (var request in requests)
        {
            var key = (entry.Item, request.Slot);
            used[key] =
                (used.TryGetValue(key, out long u) ? u : 0) + (long)request.Count * request.Row.Lot;
        }
    }
}
