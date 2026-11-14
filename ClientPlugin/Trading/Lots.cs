using System.Collections.Generic;
using System.Linq;

namespace ClientPlugin.Trading;

/// <summary>Tells a Store's rows of lots from its rows of single units.</summary>
public static class Lots
{
    public const int GuessedLot = 1000;

    // A lot costs the unit price times the lot size, give or take taxes and rounding to whole SC
    private const double MinRatio = 0.8 * GuessedLot;
    private const double MaxRatio = 1.25 * GuessedLot;

    /// <summary>Sets the lot size of the rows the server named, by row id. The other rows are single units.</summary>
    public static void Apply(IEnumerable<Row> rows, IReadOnlyDictionary<long, int> lotsByRowId)
    {
        foreach (var row in rows)
            row.Lot = lotsByRowId.TryGetValue(row.Id, out int lot) && lot > 1 ? lot : 1;
    }

    /// <summary>
    /// Guesses the rows of lots from the prices, for servers that don't say: of two rows of an
    /// item on the same side, one priced about 1,000 times the other is the row of lots. A lone
    /// row on the other side priced like that row of lots is one too.
    /// </summary>
    public static void Guess(IReadOnlyCollection<Row> rows)
    {
        foreach (var row in rows)
            row.Lot = 1;
        foreach (var item in rows.Where(r => r.Price > 0).GroupBy(r => r.Item))
        {
            Row unit = null;
            Row lot = null;
            foreach (var side in item.GroupBy(r => r.Side))
            {
                var pair = side.OrderBy(r => r.Price).ToList();
                if (pair.Count != 2)
                    continue;
                double ratio = (double)pair[1].Price / pair[0].Price;
                if (ratio < MinRatio || ratio > MaxRatio)
                    continue;
                unit = pair[0];
                lot = pair[1];
                lot.Lot = GuessedLot;
            }
            if (lot == null)
                continue;
            foreach (var side in item.GroupBy(r => r.Side))
            {
                var rowsOfSide = side.ToList();
                if (rowsOfSide.Count != 1 || rowsOfSide[0].Side == lot.Side)
                    continue;
                var lone = rowsOfSide[0];
                if (lone.Price >= lot.Price / 2 && lone.Price <= lot.Price * 3L / 2 && lone.Price > unit.Price * 100L)
                    lone.Lot = GuessedLot;
            }
        }
    }
}
