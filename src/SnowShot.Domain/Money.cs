namespace SnowShot.Domain;

public readonly record struct NanoYuan : IComparable<NanoYuan>
{
    public static readonly NanoYuan Zero = new(0);
    public static readonly NanoYuan ThreeYuan = new(3_000_000_000);

    public NanoYuan(long value)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(value);
        Value = value;
    }

    public long Value { get; }

    public int CompareTo(NanoYuan other) => Value.CompareTo(other.Value);
    public static NanoYuan operator +(NanoYuan left, NanoYuan right) => new(checked(left.Value + right.Value));
    public static NanoYuan operator *(NanoYuan price, long units)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(units);
        return new(checked(price.Value * units));
    }

    public static bool operator <(NanoYuan left, NanoYuan right) => left.Value < right.Value;
    public static bool operator >(NanoYuan left, NanoYuan right) => left.Value > right.Value;
    public static bool operator <=(NanoYuan left, NanoYuan right) => left.Value <= right.Value;
    public static bool operator >=(NanoYuan left, NanoYuan right) => left.Value >= right.Value;
    public static NanoYuan Min(NanoYuan left, NanoYuan right) => left <= right ? left : right;
}

public readonly record struct UnitPrice(NanoYuan Input, NanoYuan Output)
{
    public NanoYuan Calculate(long inputUnits, long outputUnits) =>
        (Input * inputUnits) + (Output * outputUnits);
}
