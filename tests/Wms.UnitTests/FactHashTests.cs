using Wms.SharedKernel;
using Xunit;

namespace Wms.UnitTests;

/// <summary>
/// The request hash decides whether a retry is honoured or rejected as a
/// client bug, so it must depend on what a fact means and not on how it was
/// written down.
/// </summary>
public sealed class FactHashTests
{
    /// <summary>
    /// The failure this guards: a handheld queues a fact with quantity 10,
    /// the value round-trips through IndexedDB or a newer app version as
    /// 10.0, and the retry hashes differently. The server answers 409, the
    /// device treats it as a poisoned fact and quarantines it (§6.4 case B) —
    /// a stuck receipt caused entirely by decimal formatting.
    /// </summary>
    [Theory]
    [InlineData("10", "10.0")]
    [InlineData("10", "10.0000")]
    [InlineData("0", "0.00")]
    [InlineData("2.5", "2.5000")]
    public void Quantity_IsTheSameForEveryScaleOfTheSameNumber(string left, string right)
    {
        Assert.Equal(
            FactHash.Quantity(decimal.Parse(left, System.Globalization.CultureInfo.InvariantCulture)),
            FactHash.Quantity(decimal.Parse(right, System.Globalization.CultureInfo.InvariantCulture)));
    }

    [Fact]
    public void Quantity_StillDistinguishesGenuinelyDifferentAmounts()
    {
        Assert.NotEqual(FactHash.Quantity(10m), FactHash.Quantity(10.0001m));
        Assert.NotEqual(FactHash.Quantity(10m), FactHash.Quantity(-10m));
    }

    [Fact]
    public void Of_SeparatesFields_SoAdjacentValuesCannotBeReArranged()
    {
        // Without a separator emitted for every field, ("ab", "c") and
        // ("a", "bc") would hash identically and a reused key with a shifted
        // payload would be accepted as a replay.
        Assert.NotEqual(FactHash.Of("ab", "c"), FactHash.Of("a", "bc"));
    }

    [Fact]
    public void Of_TreatsNullAndEmptyConsistently_AndIsStable()
    {
        Assert.Equal(FactHash.Of("a", null, "b"), FactHash.Of("a", string.Empty, "b"));
        Assert.Equal(FactHash.Of("a", "b"), FactHash.Of("a", "b"));
        Assert.NotEqual(FactHash.Of("a", "b"), FactHash.Of("b", "a"));
    }
}
