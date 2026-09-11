using System.Security.Cryptography;
using System.Text;
using Konscious.Security.Cryptography;
using Wms.Modules.Identity.Contracts;

namespace Wms.Modules.Identity.Infrastructure;

/// <summary>
/// Argon2id hashing for passwords, PINs and badge secrets (§8.1).
/// </summary>
/// <remarks>
/// <para>
/// <c>Konscious.Security.Cryptography.Argon2</c>, MIT — licence confirmed from
/// NuGet's registration metadata before adoption, per the standing rule that
/// several popular .NET libraries have moved to commercial terms.
/// </para>
/// <para>
/// The encoded form carries its own parameters
/// (<c>argon2id$v=19$m=..,t=..,p=..$salt$hash</c>) so the cost can be raised
/// later without invalidating existing credentials: a verify reads the
/// parameters the hash was created with, and only new hashes use the new
/// cost. A scheme that hard-codes its parameters cannot be strengthened
/// without forcing every user to re-enrol.
/// </para>
/// </remarks>
public sealed class Argon2PasswordHasher : IPasswordHasher
{
    /// <summary>
    /// OWASP's second recommended Argon2id configuration: 19 MiB, two passes,
    /// one lane.
    /// </summary>
    /// <remarks>
    /// Chosen over the 46 MiB variant because operator sign-on happens at a
    /// handheld on a shift change, where several hundred milliseconds of
    /// hashing is felt directly. Memory cost is what makes Argon2id worth
    /// having; two passes at 19 MiB keeps that property while staying
    /// responsive on the API's reserved pool.
    /// </remarks>
    private const int MemoryKib = 19 * 1024;

    private const int Iterations = 2;
    private const int Parallelism = 1;
    private const int SaltBytes = 16;
    private const int HashBytes = 32;

    public string Hash(string secret)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(secret);

        byte[] salt = RandomNumberGenerator.GetBytes(SaltBytes);
        byte[] hash = Derive(secret, salt, MemoryKib, Iterations, Parallelism);

        return string.Join(
            '$',
            "argon2id",
            "v=19",
            $"m={MemoryKib},t={Iterations},p={Parallelism}",
            Convert.ToBase64String(salt),
            Convert.ToBase64String(hash));
    }

    public bool Verify(string secret, string encoded)
    {
        if (string.IsNullOrWhiteSpace(secret) || string.IsNullOrWhiteSpace(encoded))
        {
            return false;
        }

        string[] parts = encoded.Split('$');
        if (parts.Length != 5 || parts[0] != "argon2id")
        {
            return false;
        }

        try
        {
            (int memory, int iterations, int parallelism) = ParseParameters(parts[2]);
            byte[] salt = Convert.FromBase64String(parts[3]);
            byte[] expected = Convert.FromBase64String(parts[4]);

            byte[] actual = Derive(secret, salt, memory, iterations, parallelism, expected.Length);

            // Fixed-time comparison: a byte-by-byte early exit leaks how much
            // of the hash matched, which is enough to reconstruct it.
            return CryptographicOperations.FixedTimeEquals(actual, expected);
        }
        catch (FormatException)
        {
            // A malformed stored hash is a corrupt row, not a valid password.
            return false;
        }
    }

    private static (int Memory, int Iterations, int Parallelism) ParseParameters(string segment)
    {
        Dictionary<string, int> values = segment
            .Split(',')
            .Select(pair => pair.Split('='))
            .ToDictionary(pair => pair[0], pair => int.Parse(pair[1], provider: null));

        return (values["m"], values["t"], values["p"]);
    }

    private static byte[] Derive(
        string secret, byte[] salt, int memoryKib, int iterations, int parallelism,
        int length = HashBytes)
    {
        using Argon2id argon = new(Encoding.UTF8.GetBytes(secret))
        {
            Salt = salt,
            MemorySize = memoryKib,
            Iterations = iterations,
            DegreeOfParallelism = parallelism,
        };

        return argon.GetBytes(length);
    }
}
