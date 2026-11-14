using System;
using System.Globalization;

namespace ClientPlugin.Trading;

public enum Unit
{
    /// <summary>Ores and ingots: one unit is a kg.</summary>
    Kg,
    Pieces,

    /// <summary>One unit of a gas offer is 1,000 L.</summary>
    Gas,
}

/// <summary>Amounts of an item as the screen shows and reads them.</summary>
public static class Amounts
{
    private static readonly CultureInfo Culture = CultureInfo.InvariantCulture;

    public static string Number(long value) => value.ToString("#,0", Culture);

    public static string Format(long amount, Unit unit)
    {
        switch (unit)
        {
            case Unit.Kg when amount >= 1000:
                return (amount / 1000m).ToString("#,0.###", Culture) + " t";
            case Unit.Kg:
                return Number(amount) + " kg";
            case Unit.Gas:
                return Number(amount) + " m³";
            default:
                return Number(amount) + " pcs";
        }
    }

    /// <summary>The short name of the unit, next to the amount box.</summary>
    public static string Name(Unit unit) =>
        unit == Unit.Kg ? "kg"
        : unit == Unit.Gas ? "m³"
        : "pcs";

    /// <summary>Reads a typed amount: "2500", "2,500", "2.5t", "2.5k". False if it isn't one.</summary>
    public static bool TryParse(string text, out long amount)
    {
        amount = 0;
        text = (text ?? "").Trim().ToLowerInvariant().Replace(",", "").Replace(" ", "");
        decimal scale = 1;
        if (text.EndsWith("t") || text.EndsWith("k"))
        {
            scale = 1000;
            text = text.Substring(0, text.Length - 1);
        }
        if (!decimal.TryParse(text, NumberStyles.AllowDecimalPoint, Culture, out decimal value))
            return false;
        value *= scale;
        if (value < 0 || value > int.MaxValue)
            return false;
        amount = (long)Math.Round(value);
        return true;
    }

    /// <summary>The step of the + and - buttons at an amount: its leading digit's place, 100 at least for kg.</summary>
    public static long StepAt(long amount, Unit unit, long lotStep)
    {
        if (lotStep > 1)
            return lotStep;
        long step = unit == Unit.Kg ? 100 : 1;
        while (step * 10 <= amount)
            step *= 10;
        return step;
    }

    /// <summary>The amount one step down: from 1,000 it is 900, not 0.</summary>
    public static long StepDown(long amount, Unit unit, long lotStep) =>
        Math.Max(0, amount - StepAt(Math.Max(0, amount - 1), unit, lotStep));
}
