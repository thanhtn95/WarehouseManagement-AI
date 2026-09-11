using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace Wms.SharedKernel;

/// <summary>
/// Canonical text forms for the request hash that guards an idempotency key
/// against reuse with a different payload (invariant 7, H2).
/// </summary>
/// <remarks>
/// The hash must be a property of the <em>values</em> a fact carries, never
/// of how they happen to be formatted. A queued fact is written to IndexedDB
/// on a handheld and replayed later, possibly by a different app version;
/// if a value round-trips into a different textual form, a byte-equivalent
/// retry hashes differently, comes back as a <c>409</c>, and the device
/// quarantines a fact the server already accepted (§6.4 case B). That is a
/// stuck receipt caused by formatting.
///
/// Every fact type hashes through here, so the rule is stated once rather
/// than re-derived — slightly differently — in each handler.
/// </remarks>
public static class FactHash
{
    /// <summary>
    /// The scale of <c>numeric(18,4)</c>, which is what every quantity and
    /// money column in the schema is declared as.
    /// </summary>
    private const int StorageScale = 4;

    /// <summary>
    /// Renders a quantity in the one form the database would store it in.
    /// </summary>
    /// <remarks>
    /// <c>decimal</c> preserves trailing zeros and <c>System.Text.Json</c>
    /// binds the scale written in the JSON, so <c>10</c>, <c>10.0</c> and
    /// <c>10.0000</c> are numerically equal but format differently. Rounding
    /// to the storage scale and forcing that scale collapses all three.
    /// </remarks>
    public static string Quantity(decimal value) =>
        decimal.Round(value, StorageScale).ToString("F4", CultureInfo.InvariantCulture);

    /// <summary>
    /// Hashes the already-canonical field values of a fact.
    /// </summary>
    /// <remarks>
    /// Hashing named fields rather than a serialized object graph keeps the
    /// hash stable across serializer settings and property reordering — a
    /// hash that changes when the serializer is upgraded would reject every
    /// in-flight retry in the fleet at once.
    /// </remarks>
    public static string Of(params ReadOnlySpan<string?> fields)
    {
        StringBuilder canonical = new();
        foreach (string? field in fields)
        {
            // The separator is emitted for empty and null fields too, so
            // ("a", null) and ("a|", null) cannot collide.
            canonical.Append(field ?? string.Empty).Append('|');
        }

        return Convert.ToHexStringLower(
            SHA256.HashData(Encoding.UTF8.GetBytes(canonical.ToString())));
    }
}
