using System.Collections.Generic;
using System.Linq;

namespace ClientPlugin.Trading;

/// <summary>A line of the Store screen: one row, or an item's row of single units merged with its row of lots.</summary>
public sealed class Entry
{
    public Row Unit;
    public Row LotRow;

    public string Item => (Unit ?? LotRow).Item;
    public Side Side => (Unit ?? LotRow).Side;

    /// <summary>Amounts are multiples of this: the lot size where the item trades in lots only.</summary>
    public int Step => Unit == null ? LotRow.Lot : 1;

    /// <summary>Units of the item the Store has or wants.</summary>
    public long Available => (Unit?.Amount ?? 0) + (long)(LotRow?.Amount ?? 0) * (LotRow?.Lot ?? 0);

    /// <summary>
    /// The lines of one side of the Store, in the Store's order. An item with exactly one row of
    /// single units and one of lots becomes one line if merge is set.
    /// </summary>
    public static List<Entry> Build(IEnumerable<Row> rows, Side side, bool merge)
    {
        var entries = new List<Entry>();
        var mine = rows.Where(r => r.Side == side && r.Amount > 0 && r.Price > 0).ToList();
        foreach (var group in mine.GroupBy(r => r.Item))
        {
            var units = group.Where(r => r.Lot == 1).ToList();
            var lots = group.Where(r => r.Lot > 1).ToList();
            if (merge && units.Count == 1 && lots.Count == 1)
            {
                entries.Add(new Entry { Unit = units[0], LotRow = lots[0] });
                continue;
            }
            foreach (var row in group)
                entries.Add(row.Lot == 1 ? new Entry { Unit = row } : new Entry { LotRow = row });
        }
        return entries;
    }
}
