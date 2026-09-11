using System.Security.Claims;
using System.Text;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;
using Wms.Modules.Identity.Contracts;

namespace Wms.Modules.Identity.Infrastructure;

/// <summary>
/// Issues and validates the ~15-minute access token (§8.1).
/// </summary>
/// <remarks>
/// <para>
/// The token carries identity, not authority. It holds the user, the session,
/// the device and the <c>security_stamp</c> — and deliberately <strong>not</strong>
/// the permission set. Permissions in the token would be a 15-minute cache of
/// an authorization decision, so a revoked role would keep working until the
/// token expired; §8.1 requires termination "in seconds rather than at token
/// expiry". Permissions are therefore resolved per request, and the stamp is
/// what makes that resolution cheap to invalidate.
/// </para>
/// <para>
/// <c>Microsoft.IdentityModel.JsonWebTokens</c>, MIT — licence confirmed from
/// NuGet's registration metadata before adoption.
/// </para>
/// </remarks>
public sealed class TokenIssuer : ITokenIssuer
{
    public const string SecurityStampClaim = "wms:stamp";
    public const string SessionClaim = "wms:session";
    public const string DeviceClaim = "wms:device";

    private const int AccessTokenSeconds = 900;

    private readonly SigningCredentials _credentials;
    private readonly TokenValidationParameters _validation;
    private readonly JsonWebTokenHandler _handler = new();

    public TokenIssuer(string signingKey, string issuer)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(signingKey);

        if (Encoding.UTF8.GetByteCount(signingKey) < 32)
        {
            // HS256 with a key shorter than its output is a forgeable token.
            // Refusing at startup is the only place this can be caught before
            // it becomes a production signing key.
            throw new InvalidOperationException(
                "The JWT signing key must be at least 32 bytes.");
        }

        SymmetricSecurityKey key = new(Encoding.UTF8.GetBytes(signingKey));
        _credentials = new SigningCredentials(key, SecurityAlgorithms.HmacSha256);

        _validation = new TokenValidationParameters
        {
            ValidateIssuer = true,
            ValidIssuer = issuer,
            ValidateAudience = false,
            ValidateIssuerSigningKey = true,
            IssuerSigningKey = key,
            ValidateLifetime = true,

            // The default five minutes of clock skew would let a token that
            // has expired keep working for five more, which undercuts the
            // short lifetime the design chose it for.
            ClockSkew = TimeSpan.FromSeconds(30),
        };

        Issuer = issuer;
    }

    private string Issuer { get; }

    public string IssueAccessToken(
        Guid userId, Guid securityStamp, Guid? sessionId, Guid? deviceId, out int expiresInSeconds)
    {
        expiresInSeconds = AccessTokenSeconds;

        List<Claim> claims =
        [
            new(ClaimTypes.NameIdentifier, userId.ToString()),
            new(SecurityStampClaim, securityStamp.ToString()),
        ];

        if (sessionId is Guid session)
        {
            claims.Add(new Claim(SessionClaim, session.ToString()));
        }

        if (deviceId is Guid device)
        {
            claims.Add(new Claim(DeviceClaim, device.ToString()));
        }

        return _handler.CreateToken(new SecurityTokenDescriptor
        {
            Issuer = Issuer,
            Subject = new ClaimsIdentity(claims),
            Expires = DateTime.UtcNow.AddSeconds(AccessTokenSeconds),
            SigningCredentials = _credentials,
        });
    }

    public async Task<TokenClaims?> ValidateAsync(string token)
    {
        if (string.IsNullOrWhiteSpace(token))
        {
            return null;
        }

        TokenValidationResult result = await _handler.ValidateTokenAsync(token, _validation);
        if (!result.IsValid)
        {
            return null;
        }

        Guid? Read(string type) =>
            result.Claims.TryGetValue(type, out object? value)
                && Guid.TryParse(value?.ToString(), out Guid parsed)
                ? parsed
                : null;

        Guid? userId = Read(ClaimTypes.NameIdentifier);
        Guid? stamp = Read(SecurityStampClaim);

        return userId is Guid user && stamp is Guid securityStamp
            ? new TokenClaims(user, securityStamp, Read(SessionClaim), Read(DeviceClaim))
            : null;
    }
}
