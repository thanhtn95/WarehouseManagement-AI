namespace Wms.Api;

/// <summary>
/// The machine-readable <c>type</c> values from the error catalogue (§7.1).
/// </summary>
/// <remarks>
/// §7.1 specifies the suffixes and leaves the prefix open. A URN is used
/// rather than an <c>https://</c> URL because RFC 7807 does not require the
/// URI to resolve, and a URL that looks resolvable but 404s in every
/// customer deployment is worse than one that never claimed to be
/// dereferenceable. Clients branch on this string; it is part of the API
/// contract and changing one is a breaking change.
/// </remarks>
public static class ProblemTypes
{
    private const string Prefix = "urn:wms:problem:";

    public const string MalformedRequest = Prefix + "malformed-request";
    public const string Unauthenticated = Prefix + "unauthenticated";
    public const string InsufficientPermission = Prefix + "insufficient-permission";
    public const string NotFound = Prefix + "not-found";
    public const string IdempotencyConflict = Prefix + "idempotency-conflict";
    public const string ClientTooOld = Prefix + "client-too-old";

    /// <summary>A state change the aggregate's lifecycle does not allow (§5.4).</summary>
    public const string InvalidTransition = Prefix + "invalid-transition";

    /// <summary>
    /// Completion attempted while lines still have no count. Carries
    /// <c>unconfirmedLineNos</c>, because a supervisor needs to know which
    /// lines to go and count, not merely that some exist.
    /// </summary>
    public const string LinesUnconfirmed = Prefix + "lines-unconfirmed";

    /// <summary>
    /// H13: an administrator tried to grant a role carrying permissions they
    /// do not hold. Named in the review, and distinct from
    /// <see cref="InsufficientPermission"/> — the caller <em>may</em> manage
    /// roles; what they may not do is grant upward.
    /// </summary>
    public const string CannotGrantPermissionYouLack =
        Prefix + "cannot-grant-permission-you-lack";

    public const string AccountLocked = Prefix + "account-locked";

    /// <summary>
    /// Outside the validity window — named separately from bad credentials
    /// because the remedy differs: the operator is who they say they are and
    /// needs an administrator to extend their access (§5.1).
    /// </summary>
    public const string AccountExpired = Prefix + "account-expired";

    public const string DeviceClaimed = Prefix + "device-claimed";

    public const string TokenReused = Prefix + "token-reused";
}
