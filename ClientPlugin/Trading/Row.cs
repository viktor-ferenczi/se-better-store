namespace ClientPlugin.Trading;

public enum Side
{
    /// <summary>The Store's offers: the player buys.</summary>
    Buy,

    /// <summary>The Store's orders: the player sells.</summary>
    Sell,
}

/// <summary>One item of a Store as the game lists it: an offer or an order.</summary>
public sealed class Row
{
    public long Id;

    /// <summary>What is traded. Rows of the same item have the same key.</summary>
    public string Item;

    public Side Side;

    /// <summary>Units of the item moved by one unit of this row: 1, or 1,000 for a row of lots.</summary>
    public int Lot = 1;

    /// <summary>Units of this row the Store has (offer) or wants (order).</summary>
    public int Amount;

    public int Price;

    /// <summary>The faction's bonus on the price: below 1 on offers, above 1 on orders.</summary>
    public float Bonus = 1f;

    /// <summary>The price per unit of this row as the vanilla screen shows it.</summary>
    public int ShownPrice => (int)(Price * Bonus);

    /// <summary>The price per unit of the item.</summary>
    public double PricePerItem => (double)Price * Bonus / Lot;

    /// <summary>What the server charges or pays for count units of this row, by its own arithmetic.</summary>
    public long Cost(int count)
    {
        // ReSharper disable once CompareOfFloatsByEqualityOperator
        if (Bonus == 1f)
            return (long)Price * count;
        return Side == Side.Buy
            ? (long)(Price * (count * Bonus))
            : (long)((float)((long)Price * count) * Bonus);
    }
}
