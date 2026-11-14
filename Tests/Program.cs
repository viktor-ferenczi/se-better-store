using System;
using System.Collections.Generic;
using System.Linq;
using ClientPlugin.Trading;

// Tests of the trade planning. Prints one line per check and exits with the number of failures.

int failed = 0;
int passed = 0;

void Check(string name, bool ok, string detail = "")
{
    if (ok)
        passed++;
    else
        failed++;
    Console.WriteLine($"{(ok ? "PASS" : "FAIL")} {name}{(detail == "" ? "" : ": " + detail)}");
}

void Equal<T>(string name, T actual, T expected) =>
    Check(
        name,
        EqualityComparer<T>.Default.Equals(actual, expected),
        $"{actual} (expected {expected})"
    );

Row Offer(string item, int price, int amount, int lot = 1, long id = 0) =>
    new Row
    {
        Id = id,
        Item = item,
        Side = Side.Buy,
        Price = price,
        Amount = amount,
        Lot = lot,
    };

Row Order(string item, int price, int amount, int lot = 1, long id = 0) =>
    new Row
    {
        Id = id,
        Item = item,
        Side = Side.Sell,
        Price = price,
        Amount = amount,
        Lot = lot,
    };

string Show(Quote q) =>
    string.Join(" + ", q.Parts.Select(p => $"{p.Count}x{p.Row.Lot}@{p.Row.Price}"));

string Placed(List<Request> requests) =>
    string.Join(" ", requests.Select(r => $"{r.Slot}:{r.Count}x{r.Row.Lot}"));

// --- prices of Octahedron's Stores (SE-1471) ---
var stone = new Entry { Unit = Offer("Stone", 1, 50000), LotRow = Offer("Stone", 1020, 50, 1000) };
var iron = new Entry { Unit = Offer("Iron", 14, 50000), LotRow = Offer("Iron", 14484, 50, 1000) };
var nickel = new Entry
{
    Unit = Offer("Nickel", 26, 20000),
    LotRow = Offer("Nickel", 25500, 20, 1000),
};
var ice = new Entry { LotRow = Offer("Ice", 10, 50, 1000) };
var ironSell = new Entry
{
    Unit = Order("Iron", 13, 150000),
    LotRow = Order("Iron", 13916, 150, 1000),
};
var stoneSell = new Entry { LotRow = Order("Stone", 980, 150, 1000) };

// --- Split ---
var q = Planner.Split(stone, 2500);
Equal("split: stone by the kg, cheaper than its lots", Show(q), "2500x1@1");
Equal("split: stone cost", q.Cost, 2500L);

q = Planner.Split(nickel, 2500);
Equal("split: nickel in lots, the rest by the kg", Show(q), "2x1000@25500 + 500x1@26");
Equal("split: nickel cost", q.Cost, 2 * 25500L + 500 * 26);
Equal("split: nickel nothing left", q.Left, 0L);

q = Planner.Split(stone, 60000);
Equal("split: stone over the unit stock goes on in lots", Show(q), "50000x1@1 + 10x1000@1020");

q = Planner.Split(stone, 100500);
Equal("split: over the whole stock", q.Left, 500L);
Equal("split: over the whole stock, amount", q.Amount, 100000L);

q = Planner.Split(ice, 2500);
Equal("split: lots only trade whole lots", Show(q), "2x1000@10");
Equal("split: lots only leave the rest", q.Left, 500L);

q = Planner.Split(ironSell, 2500);
Equal("split: selling iron in lots where they pay more", Show(q), "2x1000@13916 + 500x1@13");

q = Planner.Split(stoneSell, 999);
Equal("split: less than a lot can't be sold to a lots only order", q.Amount, 0L);

Equal("split: nothing", Planner.Split(stone, 0).Parts.Count, 0);
Equal("split: negative", Planner.Split(stone, -5).Amount, 0L);

// --- Cost with a faction bonus, as the server computes it ---
var bonus = new Row
{
    Side = Side.Buy,
    Price = 112,
    Amount = 100,
    Bonus = 0.95f,
};
Equal("cost: offer with a bonus", bonus.Cost(10), (long)(112f * (10 * 0.95f)));
Equal("cost: shown price with a bonus", bonus.ShownPrice, 106);
var big = Offer("Big", 2_000_000_000, 5);
Equal("cost: no overflow without a bonus", big.Cost(5), 10_000_000_000L);

// --- Place ---
long left;
var requests = Planner.Place(Planner.Split(nickel, 2500), new long[] { 1200, 5000 }, out left);
Equal(
    "place: a lot goes whole into one inventory",
    Placed(requests),
    "0:1x1000 1:1x1000 0:200x1 1:300x1"
);
Equal("place: all placed", left, 0L);

requests = Planner.Place(Planner.Split(ice, 3000), new long[] { 900, 900, 2100 }, out left);
Equal("place: lots skip inventories too small for one", Placed(requests), "2:2x1000");
Equal("place: the lot that fits nowhere is left", left, 1000L);

requests = Planner.Place(Planner.Split(stone, 700), new long[] { 0, 300, 1000 }, out left);
Equal("place: single units fill the inventories in order", Placed(requests), "1:300x1 2:400x1");

requests = Planner.Place(Planner.Split(stone, 700), new long[0], out left);
Equal("place: no inventory", left, 700L);

// --- Max ---
Equal("max: the room limits", Planner.Max(stone, new long[] { 1200, 300 }, long.MaxValue), 1500L);
Equal("max: the money limits", Planner.Max(iron, new long[] { 1_000_000 }, 1400), 100L);
Equal(
    "max: the stock limits",
    Planner.Max(stone, new long[] { 10_000_000 }, long.MaxValue),
    100000L
);
Equal(
    "max: lots only, whole lots that fit",
    Planner.Max(ice, new long[] { 2500, 1999 }, long.MaxValue),
    3000L
);
Equal("max: lots only, the money limits", Planner.Max(ice, new long[] { 100000 }, 25), 2000L);
Equal("max: selling what is held", Planner.Max(ironSell, new long[] { 2500, 40 }, 0), 2540L);
Equal("max: selling to lots only", Planner.Max(stoneSell, new long[] { 2500, 1700 }, 0), 3000L);
Equal("max: nothing held", Planner.Max(ironSell, new long[] { 0, 0 }, 0), 0L);
Equal("max: no money", Planner.Max(iron, new long[] { 1000 }, 0), 0L);
Check(
    "possible: the max is",
    Planner.Possible(ice, 3000, new long[] { 2500, 1999 }, long.MaxValue)
);
Check(
    "possible: more than the max is not",
    !Planner.Possible(ice, 4000, new long[] { 2500, 1999 }, long.MaxValue)
);

// --- Usage: lines of one item share the inventories (Sell all cargo with lots shown apart) ---
var ironUnits = new Entry { Unit = Order("Iron", 13, 150000) };
var ironLots = new Entry { LotRow = Order("Iron", 13916, 150, 1000) };
var usage = new Usage();
var held = new long[] { 3000, 1500 };
long most = Planner.Max(ironUnits, usage.Left(ironUnits, held), 0);
Equal("usage: the first line sells all", most, 4500L);
usage.Add(
    ironUnits,
    Planner.Place(Planner.Split(ironUnits, most), usage.Left(ironUnits, held), out left)
);
Equal(
    "usage: nothing left for the second line",
    Planner.Max(ironLots, usage.Left(ironLots, held), 0),
    0L
);
Equal(
    "usage: other items keep their room",
    string.Join(",", usage.Left(stoneSell, held)),
    "3000,1500"
);

// --- Entry.Build ---
var rows = new List<Row>
{
    Offer("Stone", 1, 50000, 1, 1),
    Offer("Stone", 1020, 50, 1000, 2),
    Order("Stone", 980, 150, 1000, 3),
    Offer("Ice", 10, 50, 1000, 4),
    Offer("Motor", 195, 500, 1, 5),
    Offer("Motor", 190, 20, 1, 6),
    Offer("Gone", 5, 0, 1, 7),
};
var entries = Entry.Build(rows, Side.Buy, merge: true);
Equal("build: merged lines", entries.Count, 4);
Check(
    "build: stone is one line of two rows",
    entries[0].Unit?.Id == 1 && entries[0].LotRow?.Id == 2
);
Check("build: ice is a line of lots", entries[1].Unit == null && entries[1].Step == 1000);
Check(
    "build: two offers of an item at different prices stay apart",
    entries[2].Unit.Id == 5 && entries[3].Unit.Id == 6
);
Equal("build: separate lines", Entry.Build(rows, Side.Buy, merge: false).Count, 5);
Equal("build: the other side", Entry.Build(rows, Side.Sell, merge: true).Count, 1);
Equal("build: available", entries[0].Available, 100000L);

// --- Lots ---
foreach (var row in rows)
    row.Lot = 1;
Lots.Guess(rows);
Equal("guess: the stone row 1,020 times the other is the lot", rows[1].Lot, 1000);
Equal("guess: the unit row stays", rows[0].Lot, 1);
Equal("guess: the lone order priced like the lot is one", rows[2].Lot, 1000);
Equal("guess: lots only can't be guessed", rows[3].Lot, 1);
Equal("guess: two offers 3 % apart are no lots", rows[5].Lot, 1);

var ironRows = new List<Row>
{
    Offer("Iron", 14, 1),
    Offer("Iron", 14484, 1),
    Order("Iron", 13, 1),
    Order("Iron", 13916, 1),
};
Lots.Guess(ironRows);
Equal("guess: both sides of iron", string.Join(",", ironRows.Select(r => r.Lot)), "1,1000,1,1000");

var far = new List<Row> { Offer("X", 10, 1), Offer("X", 200, 1), Order("X", 100000, 1) };
Lots.Guess(far);
Equal(
    "guess: prices far from 1,000 times apart",
    string.Join(",", far.Select(r => r.Lot)),
    "1,1,1"
);

Lots.Apply(rows, new Dictionary<long, int> { [4] = 1000, [2] = 1000 });
Equal("apply: the server's lots", string.Join(",", rows.Select(r => r.Lot)), "1,1000,1,1000,1,1,1");

// --- Amounts ---
Equal("format: kg", Amounts.Format(850, Unit.Kg), "850 kg");
Equal("format: tonnes", Amounts.Format(2500, Unit.Kg), "2.5 t");
Equal("format: big tonnes", Amounts.Format(323_500, Unit.Kg), "323.5 t");
Equal("format: whole tonnes", Amounts.Format(1_250_000, Unit.Kg), "1,250 t");
Equal("format: pieces", Amounts.Format(3794, Unit.Pieces), "3,794 pcs");
Equal("format: gas", Amounts.Format(11250, Unit.Gas), "11,250 m³");

long amount;
Check("parse: plain", Amounts.TryParse("2500", out amount) && amount == 2500);
Check("parse: separators", Amounts.TryParse(" 12,500 ", out amount) && amount == 12500);
Check("parse: tonnes", Amounts.TryParse("2.5t", out amount) && amount == 2500);
Check("parse: thousands", Amounts.TryParse("40K", out amount) && amount == 40000);
Check("parse: text is refused", !Amounts.TryParse("lots", out amount));
Check("parse: empty is refused", !Amounts.TryParse("", out amount));
Check("parse: negative is refused", !Amounts.TryParse("-5", out amount));
Check("parse: too big is refused", !Amounts.TryParse("99999999999", out amount));

Equal("step: kg below a tonne", Amounts.StepAt(500, Unit.Kg, 1), 100L);
Equal("step: kg at 3 t", Amounts.StepAt(3000, Unit.Kg, 1), 1000L);
Equal("step: pieces at 7", Amounts.StepAt(7, Unit.Pieces, 1), 1L);
Equal("step: pieces at 250", Amounts.StepAt(250, Unit.Pieces, 1), 100L);
Equal("step: lots only", Amounts.StepAt(5000, Unit.Kg, 1000), 1000L);
Equal("step down: from 1 t to 900 kg", Amounts.StepDown(1000, Unit.Kg, 1), 900L);
Equal("step down: from 100 kg to nothing", Amounts.StepDown(100, Unit.Kg, 1), 0L);
Equal("step down: from 10 pieces to 9", Amounts.StepDown(10, Unit.Pieces, 1), 9L);

Console.WriteLine($"{passed}/{passed + failed} trade planning checks passed");
return failed;
