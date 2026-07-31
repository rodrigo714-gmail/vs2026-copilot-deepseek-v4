using System.Globalization;
using System.Text.RegularExpressions;

/// <summary>
/// What kind of failure an upstream response represents. Public because it is serialised into
/// the cooldown and failover payloads the dashboard reads.
/// </summary>
public enum UpstreamFailureKind
{
    /// <summary>The response succeeded.</summary>
    None,

    /// <summary>Service-level hiccup (408, 5xx). Worth trying another provider and retrying later.</summary>
    Transient,

    /// <summary>Short-window throttling — "too many requests this minute". Recovers in seconds.</summary>
    RateLimit,

    /// <summary>A long-period cap was hit (daily/monthly budget, credits gone). Recovers in hours or days.</summary>
    QuotaExhausted,

    /// <summary>Bad or unentitled credentials for this provider (401/403).</summary>
    Auth,

    /// <summary>The request itself is malformed. Sending the same body elsewhere will fail the same way.</summary>
    BadRequest,

    /// <summary>This provider does not serve this model (404/410), but another one might.</summary>
    ModelUnavailable,

    /// <summary>
    /// The provider never answered: a connection failure, or nothing back within the model's
    /// configured <c>timeout_seconds</c>. Distinct from <see cref="Transient"/> because a
    /// provider that hangs costs a full timeout every time it is tried, so it earns a stand-down
    /// on the first occurrence rather than after a burst.
    /// </summary>
    Unreachable
}

/// <summary>Which budget window <see cref="UpstreamFailureKind.QuotaExhausted"/> refers to.</summary>
public enum QuotaPeriod
{
    None,
    Daily,
    Monthly,
    /// <summary>A prepaid credit balance ran out — refills when the account is topped up, not on a clock.</summary>
    Credit
}

/// <summary>A classified upstream failure.</summary>
/// <param name="Kind">What went wrong.</param>
/// <param name="StatusCode">The upstream HTTP status.</param>
/// <param name="RetryAfter">An upstream-supplied wait hint, if one was present.</param>
/// <param name="Period">For <see cref="UpstreamFailureKind.QuotaExhausted"/>, which window was exhausted.</param>
/// <param name="MatchedPattern">The body pattern that drove the classification, for logging.</param>
internal readonly record struct UpstreamFailure(
    UpstreamFailureKind Kind,
    int StatusCode,
    TimeSpan? RetryAfter,
    QuotaPeriod Period,
    string? MatchedPattern)
{
    internal static readonly UpstreamFailure Success = new(UpstreamFailureKind.None, 200, null, QuotaPeriod.None, null);

    /// <summary>The provider host could not be reached at all.</summary>
    internal static readonly UpstreamFailure Unreachable =
        new(UpstreamFailureKind.Unreachable, StatusCodes.Status502BadGateway, null, QuotaPeriod.None, "transport-failure");

    /// <summary>The provider accepted the connection but did not answer in time.</summary>
    internal static readonly UpstreamFailure TimedOut =
        new(UpstreamFailureKind.Unreachable, StatusCodes.Status504GatewayTimeout, null, QuotaPeriod.None, "timeout");

    internal bool IsFailure => Kind != UpstreamFailureKind.None;
}

/// <summary>
/// Classifies an upstream response so the proxy can decide whether trying the next provider is
/// worth doing, and — later — how long to stand the failing one down.
///
/// The load-bearing distinction is inside HTTP 429. Providers use it for two very different
/// things: short transient throttling ("slow down, retry in 20s") and a long-period cap
/// ("your daily free tokens are gone, come back tomorrow"). The status code alone cannot tell
/// them apart, so the body is scanned for an explicit quota keyword. A bare 429 with no such
/// keyword is always treated as the milder <see cref="UpstreamFailureKind.RateLimit"/> — guessing
/// "quota exhausted" would stand a healthy provider down for hours over a one-minute blip.
/// </summary>
internal static partial class UpstreamFailureClassifier
{
    // Patterns are deliberately specific. A bare /quota reached/ would also swallow transient
    // per-minute limits like "request quota reached, retry in 60s" and mark them as day-long.
    private static readonly (Regex Pattern, QuotaPeriod Period, string Name)[] QuotaPatterns =
    [
        (DailyLimit(), QuotaPeriod.Daily, "daily-limit"),
        (MonthlyLimit(), QuotaPeriod.Monthly, "monthly-limit"),
        (QuotaExceeded(), QuotaPeriod.Daily, "quota-exceeded"),
        (InsufficientQuota(), QuotaPeriod.Credit, "insufficient-quota"),
        (CreditsExhausted(), QuotaPeriod.Credit, "credits-exhausted"),
        (BillingOrPlanCap(), QuotaPeriod.Monthly, "billing-cap"),
        // Cloudflare Workers AI: "you have used up your daily free allocation of 10,000 neurons".
        // Nothing above matches it, so without this the 429 looks transient and the router
        // hammers a budget that only resets at UTC midnight.
        (CloudflareDailyAllocation(), QuotaPeriod.Daily, "cloudflare-daily-allocation"),
        // Google returns a generic RESOURCE_EXHAUSTED when a billing-period quota is consumed.
        // The "reset after" qualifier keeps transient Google throttling out of this bucket.
        (GoogleResourceExhausted(), QuotaPeriod.Daily, "google-resource-exhausted"),
    ];

    [GeneratedRegex(@"daily.*(limit|quota)|per.?day.*limit", RegexOptions.IgnoreCase)]
    private static partial Regex DailyLimit();

    [GeneratedRegex(@"monthly.*(limit|quota)|per.?month.*limit", RegexOptions.IgnoreCase)]
    private static partial Regex MonthlyLimit();

    [GeneratedRegex(@"quota.*exceed|exceed.*quota", RegexOptions.IgnoreCase)]
    private static partial Regex QuotaExceeded();

    [GeneratedRegex(@"insufficient.*quota|insufficient.*(balance|credit)", RegexOptions.IgnoreCase)]
    private static partial Regex InsufficientQuota();

    [GeneratedRegex(@"credit.*exhaust|out of credits|no.*credits.*remaining", RegexOptions.IgnoreCase)]
    private static partial Regex CreditsExhausted();

    [GeneratedRegex(@"billing.*cap|hard.?limit|plan.*limit", RegexOptions.IgnoreCase)]
    private static partial Regex BillingOrPlanCap();

    [GeneratedRegex(@"daily free allocation", RegexOptions.IgnoreCase)]
    private static partial Regex CloudflareDailyAllocation();

    [GeneratedRegex(@"resource has been exhausted.*reset after", RegexOptions.IgnoreCase | RegexOptions.Singleline)]
    private static partial Regex GoogleResourceExhausted();

    // A 400 normally means the request body is wrong, and re-sending it to another provider
    // wastes a candidate. The exception is a provider that reports an unknown model as 400
    // rather than 404 — that IS worth trying elsewhere.
    [GeneratedRegex(@"model.*(not.*(found|exist|support)|unavailable|unknown)|unknown.*model|no such model", RegexOptions.IgnoreCase)]
    private static partial Regex ModelNotFoundInBody();

    /// <summary>
    /// Rate limiting dressed up as a client error. Groq answers an over-TPM request with
    /// <c>413 Payload Too Large</c> and a body reading "Request too large ... on tokens per
    /// minute (TPM): Limit 8000, Requested 8268 ... code: rate_limit_exceeded". Taken at face
    /// value that is a malformed request and the router gives up, when in truth it is a throttle
    /// and the next provider would have answered immediately.
    ///
    /// Only an explicit rate-limit phrase promotes a 4xx this way — a genuinely oversized body
    /// must still fail fast instead of being retried against every provider in the list.
    /// </summary>
    [GeneratedRegex(@"rate.?limit|tokens per minute|requests per minute|\(TPM\)|\(RPM\)|too many requests|reduce your message size", RegexOptions.IgnoreCase)]
    private static partial Regex RateLimitInBody();

    /// <summary>
    /// Classifies an upstream response. <paramref name="headers"/> may be null; lookups are
    /// case-insensitive.
    /// </summary>
    internal static UpstreamFailure Classify(int statusCode, IReadOnlyDictionary<string, string?>? headers, string? body)
    {
        if (statusCode is >= 200 and < 300)
            return UpstreamFailure.Success;

        TimeSpan? retryAfter = ParseRetryAfter(headers);

        switch (statusCode)
        {
            case 429:
            {
                (QuotaPeriod period, string? matched) = MatchQuotaPattern(body);
                return matched is not null
                    ? new UpstreamFailure(UpstreamFailureKind.QuotaExhausted, statusCode, retryAfter, period, matched)
                    : new UpstreamFailure(UpstreamFailureKind.RateLimit, statusCode, retryAfter, QuotaPeriod.None, null);
            }

            // Payment required — the account is out of money, not merely throttled.
            case 402:
                return new UpstreamFailure(UpstreamFailureKind.QuotaExhausted, statusCode, retryAfter, QuotaPeriod.Credit, "http-402");

            case 401 or 403:
            {
                // Some providers report an exhausted free tier as 403 with a quota body.
                (QuotaPeriod period, string? matched) = MatchQuotaPattern(body);
                return matched is not null
                    ? new UpstreamFailure(UpstreamFailureKind.QuotaExhausted, statusCode, retryAfter, period, matched)
                    : new UpstreamFailure(UpstreamFailureKind.Auth, statusCode, retryAfter, QuotaPeriod.None, null);
            }

            case 404 or 410:
                return new UpstreamFailure(UpstreamFailureKind.ModelUnavailable, statusCode, retryAfter, QuotaPeriod.None, null);

            case 400 or 413 or 422:
            {
                string text = body ?? string.Empty;

                if (ModelNotFoundInBody().IsMatch(text))
                    return new UpstreamFailure(UpstreamFailureKind.ModelUnavailable, statusCode, retryAfter, QuotaPeriod.None, "model-not-found-in-body");

                // A quota message outranks a rate-limit one: "daily limit reached" delivered as
                // a 403/400 is still an exhausted budget, not a per-minute throttle.
                (QuotaPeriod period, string? quotaMatch) = MatchQuotaPattern(text);
                if (quotaMatch is not null)
                    return new UpstreamFailure(UpstreamFailureKind.QuotaExhausted, statusCode, retryAfter, period, quotaMatch);

                if (RateLimitInBody().IsMatch(text))
                    return new UpstreamFailure(UpstreamFailureKind.RateLimit, statusCode, retryAfter, QuotaPeriod.None, "rate-limit-in-body");

                return new UpstreamFailure(UpstreamFailureKind.BadRequest, statusCode, retryAfter, QuotaPeriod.None, null);
            }

            default:
                return new UpstreamFailure(UpstreamFailureKind.Transient, statusCode, retryAfter, QuotaPeriod.None, null);
        }
    }

    /// <summary>
    /// Whether the next candidate provider is worth trying.
    ///
    /// Everything is worth retrying elsewhere except a genuinely malformed request: the body is
    /// rebuilt per candidate but stays semantically the same, so a 400 would burn every provider
    /// in the list and return the same error many seconds later.
    /// </summary>
    internal static bool ShouldFailover(in UpstreamFailure failure) => failure.Kind switch
    {
        UpstreamFailureKind.None => false,
        UpstreamFailureKind.BadRequest => false,
        _ => true
    };

    private static (QuotaPeriod Period, string? Name) MatchQuotaPattern(string? body)
    {
        if (string.IsNullOrEmpty(body))
            return (QuotaPeriod.None, null);

        foreach ((Regex pattern, QuotaPeriod period, string name) in QuotaPatterns)
        {
            if (pattern.IsMatch(body))
                return (period, name);
        }
        return (QuotaPeriod.None, null);
    }

    /// <summary>
    /// Reads a wait hint from the response. Prefers <c>Retry-After</c> (RFC 9110: either
    /// delta-seconds or an HTTP date), then falls back to the rate-limit reset headers these
    /// providers commonly send. Values are clamped to a day — a nonsensical hint should not
    /// stand a provider down indefinitely.
    /// </summary>
    internal static TimeSpan? ParseRetryAfter(IReadOnlyDictionary<string, string?>? headers)
    {
        if (headers is null || headers.Count == 0)
            return null;

        string?[] candidates =
        [
            GetHeader(headers, "retry-after"),
            GetHeader(headers, "x-ratelimit-reset-requests"),
            GetHeader(headers, "x-ratelimit-reset-tokens"),
            GetHeader(headers, "x-ratelimit-reset"),
        ];

        foreach (string? raw in candidates)
        {
            TimeSpan? parsed = ParseWaitHint(raw);
            if (parsed is not null)
                return parsed;
        }
        return null;
    }

    private static TimeSpan? ParseWaitHint(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
            return null;

        string value = raw.Trim();

        // Plain number: delta-seconds, unless it is large enough to only make sense as a
        // unix timestamp. The threshold is one year in seconds — the same heuristic
        // UsageTrackerService already uses for x-ratelimit-reset-requests.
        if (long.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out long number))
        {
            TimeSpan span = number < 31_536_000
                ? TimeSpan.FromSeconds(number)
                : DateTimeOffset.FromUnixTimeSeconds(number) - DateTimeOffset.UtcNow;
            return Clamp(span);
        }

        // Groq and friends send durations like "2m59.56s", "1h30m", "500ms".
        TimeSpan? duration = ParseCompoundDuration(value);
        if (duration is not null)
            return Clamp(duration.Value);

        // RFC 9110 HTTP-date form.
        if (DateTimeOffset.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal, out DateTimeOffset when))
            return Clamp(when - DateTimeOffset.UtcNow);

        return null;
    }

    [GeneratedRegex(@"(\d+(?:\.\d+)?)\s*(ms|s|m|h|d)", RegexOptions.IgnoreCase)]
    private static partial Regex DurationComponent();

    private static TimeSpan? ParseCompoundDuration(string value)
    {
        MatchCollection matches = DurationComponent().Matches(value);
        if (matches.Count == 0)
            return null;

        // Reject strings that are not made up purely of duration components, so an ISO date
        // or an arbitrary sentence containing a number never parses as a duration.
        int covered = matches.Sum(m => m.Length);
        if (covered < value.Replace(" ", "").Length)
            return null;

        double totalSeconds = 0;
        foreach (Match m in matches)
        {
            if (!double.TryParse(m.Groups[1].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out double n))
                return null;

            totalSeconds += m.Groups[2].Value.ToLowerInvariant() switch
            {
                "ms" => n / 1000.0,
                "s" => n,
                "m" => n * 60,
                "h" => n * 3600,
                "d" => n * 86400,
                _ => 0
            };
        }
        return TimeSpan.FromSeconds(totalSeconds);
    }

    private static TimeSpan? Clamp(TimeSpan span)
    {
        if (span <= TimeSpan.Zero) return null;
        return span > TimeSpan.FromDays(1) ? TimeSpan.FromDays(1) : span;
    }

    private static string? GetHeader(IReadOnlyDictionary<string, string?> headers, string name)
    {
        if (headers.TryGetValue(name, out string? direct))
            return direct;

        foreach (KeyValuePair<string, string?> kv in headers)
        {
            if (string.Equals(kv.Key, name, StringComparison.OrdinalIgnoreCase))
                return kv.Value;
        }
        return null;
    }
}
