namespace ProxyTests;

/// <summary>
/// Pure classification tests — no fixture, no environment, no HTTP.
///
/// The bodies below are the shapes these providers actually return; the whole point of the
/// classifier is that HTTP 429 alone cannot distinguish "slow down for 20 seconds" from
/// "your daily free tokens are gone until midnight".
/// </summary>
public sealed class UpstreamFailureClassifierTests
{
    private static UpstreamFailure Classify(int status, string? body = null, params (string Key, string Value)[] headers)
    {
        var dict = headers.ToDictionary(h => h.Key, h => (string?)h.Value, StringComparer.OrdinalIgnoreCase);
        return UpstreamFailureClassifier.Classify(status, dict, body);
    }

    // ── Success ──────────────────────────────────────────────────────────────

    [Theory]
    [InlineData(200)]
    [InlineData(201)]
    [InlineData(299)]
    public void SuccessStatus_IsNotAFailure(int status)
    {
        UpstreamFailure f = Classify(status);
        Assert.Equal(UpstreamFailureKind.None, f.Kind);
        Assert.False(f.IsFailure);
        Assert.False(UpstreamFailureClassifier.ShouldFailover(f));
    }

    // ── 429: the load-bearing distinction ────────────────────────────────────

    [Fact]
    public void Bare429_IsRateLimit_NotQuotaExhausted()
    {
        // Guessing "quota exhausted" here would stand a healthy provider down for hours
        // because of a one-minute blip.
        UpstreamFailure f = Classify(429, """{"error":{"message":"Rate limit reached. Please try again later.","type":"rate_limit_exceeded"}}""");

        Assert.Equal(UpstreamFailureKind.RateLimit, f.Kind);
        Assert.Equal(QuotaPeriod.None, f.Period);
        Assert.Null(f.MatchedPattern);
    }

    [Fact]
    public void Empty429Body_IsRateLimit()
    {
        Assert.Equal(UpstreamFailureKind.RateLimit, Classify(429).Kind);
        Assert.Equal(UpstreamFailureKind.RateLimit, Classify(429, "").Kind);
    }

    [Theory]
    // Groq: per-day token budget.
    [InlineData("""{"error":{"message":"Rate limit reached for model `llama-3.3-70b` : Limit 100000, Used 100000. Please try again in 4m32s. Need more? Your daily limit will reset at 00:00 UTC.","type":"tokens"}}""", QuotaPeriod.Daily)]
    // Google Gemini free tier.
    [InlineData("""{"error":{"code":429,"message":"Resource has been exhausted (e.g. check quota). Quota will reset after 1 day.","status":"RESOURCE_EXHAUSTED"}}""", QuotaPeriod.Daily)]
    // Cloudflare Workers AI neurons.
    [InlineData("""{"errors":[{"message":"you have used up your daily free allocation of 10,000 neurons, please upgrade to Cloudflare's Workers Paid plan"}]}""", QuotaPeriod.Daily)]
    // OpenAI style.
    [InlineData("""{"error":{"message":"You exceeded your current quota, please check your plan and billing details.","type":"insufficient_quota"}}""", QuotaPeriod.Daily)]
    // Monthly cap.
    [InlineData("""{"error":"You have reached your monthly quota for this model."}""", QuotaPeriod.Monthly)]
    // Credits gone.
    [InlineData("""{"error":{"message":"Your account is out of credits. Add credits to continue."}}""", QuotaPeriod.Credit)]
    public void QuotaBody_On429_IsQuotaExhausted(string body, QuotaPeriod expectedPeriod)
    {
        UpstreamFailure f = Classify(429, body);

        Assert.Equal(UpstreamFailureKind.QuotaExhausted, f.Kind);
        Assert.Equal(expectedPeriod, f.Period);
        Assert.NotNull(f.MatchedPattern);
    }

    [Fact]
    public void TransientQuotaWording_IsNotMistakenForDailyExhaustion()
    {
        // "retry in 60s" is a per-minute limit. Treating it as a day-long lockout would take
        // a perfectly healthy provider out of rotation until midnight.
        UpstreamFailure f = Classify(429, """{"error":"Request quota reached, retry in 60s"}""");
        Assert.Equal(UpstreamFailureKind.RateLimit, f.Kind);
    }

    // ── Other statuses ───────────────────────────────────────────────────────

    [Fact]
    public void PaymentRequired_IsQuotaExhausted_OnCredit()
    {
        UpstreamFailure f = Classify(402, """{"error":"Payment required"}""");
        Assert.Equal(UpstreamFailureKind.QuotaExhausted, f.Kind);
        Assert.Equal(QuotaPeriod.Credit, f.Period);
    }

    [Theory]
    [InlineData(401)]
    [InlineData(403)]
    public void AuthStatuses_AreAuth(int status)
    {
        UpstreamFailure f = Classify(status, """{"error":{"message":"Invalid API key provided"}}""");
        Assert.Equal(UpstreamFailureKind.Auth, f.Kind);
        Assert.True(UpstreamFailureClassifier.ShouldFailover(f));
    }

    [Fact]
    public void Forbidden_WithQuotaBody_IsQuotaExhausted_NotAuth()
    {
        // Some providers report a spent free tier as 403. Cooling the key down as "bad
        // credentials" would be the wrong diagnosis and the wrong recovery window.
        UpstreamFailure f = Classify(403, """{"error":"Your free daily quota has been exceeded."}""");
        Assert.Equal(UpstreamFailureKind.QuotaExhausted, f.Kind);
    }

    [Theory]
    [InlineData(404)]
    [InlineData(410)]
    public void NotFoundAndGone_AreModelUnavailable(int status)
    {
        UpstreamFailure f = Classify(status, """{"error":{"message":"The model `kimi-k3` does not exist","code":"model_not_found"}}""");
        Assert.Equal(UpstreamFailureKind.ModelUnavailable, f.Kind);
        Assert.True(UpstreamFailureClassifier.ShouldFailover(f));
    }

    [Theory]
    [InlineData(500)]
    [InlineData(502)]
    [InlineData(503)]
    [InlineData(504)]
    [InlineData(408)]
    public void ServerErrors_AreTransient(int status)
    {
        UpstreamFailure f = Classify(status, "upstream exploded");
        Assert.Equal(UpstreamFailureKind.Transient, f.Kind);
        Assert.True(UpstreamFailureClassifier.ShouldFailover(f));
    }

    // ── The 400 rule: do not burn every candidate on a malformed request ──────

    [Fact]
    public void BadRequest_DoesNotFailOver()
    {
        UpstreamFailure f = Classify(400, """{"error":{"message":"Unsupported parameter: 'max_tokens' is not supported with this model.","type":"invalid_request_error"}}""");

        Assert.Equal(UpstreamFailureKind.BadRequest, f.Kind);
        Assert.False(UpstreamFailureClassifier.ShouldFailover(f));
    }

    [Fact]
    public void BadRequest_ThatIsReallyAnUnknownModel_DoesFailOver()
    {
        // Some providers answer an unknown model with 400 instead of 404. Another provider
        // may well serve it, so this one is worth passing on.
        UpstreamFailure f = Classify(400, """{"error":{"message":"model `qwen3-coder` not found"}}""");

        Assert.Equal(UpstreamFailureKind.ModelUnavailable, f.Kind);
        Assert.True(UpstreamFailureClassifier.ShouldFailover(f));
    }

    [Theory]
    [InlineData(413)]
    [InlineData(422)]
    public void PayloadAndUnprocessable_DoNotFailOver(int status)
    {
        UpstreamFailure f = Classify(status, "request entity too large");
        Assert.Equal(UpstreamFailureKind.BadRequest, f.Kind);
        Assert.False(UpstreamFailureClassifier.ShouldFailover(f));
    }

    /// <summary>
    /// Caught by running the proxy against the real Groq free tier: an over-TPM request comes
    /// back as 413, not 429. Read literally that is "your request is malformed", so the router
    /// gave up with ten other providers idle — when it was only a throttle.
    /// </summary>
    [Fact]
    public void Groq_413_ThatIsReallyATpmLimit_IsRateLimit_AndFailsOver()
    {
        const string groqBody = """
            {"error":{"message":"Request too large for model `openai/gpt-oss-120b` in organization `org_x` service tier `on_demand` on tokens per minute (TPM): Limit 8000, Requested 8268, please reduce your message size and try again.","type":"tokens","code":"rate_limit_exceeded"}}
            """;

        UpstreamFailure f = Classify(413, groqBody);

        Assert.Equal(UpstreamFailureKind.RateLimit, f.Kind);
        Assert.Equal("rate-limit-in-body", f.MatchedPattern);
        Assert.True(UpstreamFailureClassifier.ShouldFailover(f));
    }

    [Fact]
    public void A400_CarryingRateLimitSemantics_AlsoFailsOver()
    {
        UpstreamFailure f = Classify(400, """{"error":"Too many requests, slow down"}""");
        Assert.Equal(UpstreamFailureKind.RateLimit, f.Kind);
        Assert.True(UpstreamFailureClassifier.ShouldFailover(f));
    }

    [Fact]
    public void A400_CarryingQuotaSemantics_OutranksRateLimit()
    {
        // "daily limit reached" delivered as a 400 is still an exhausted budget, and must get
        // the long cooldown rather than a few seconds of backoff.
        UpstreamFailure f = Classify(400, """{"error":"You have hit your daily limit. Rate limit resets tomorrow."}""");
        Assert.Equal(UpstreamFailureKind.QuotaExhausted, f.Kind);
        Assert.Equal(QuotaPeriod.Daily, f.Period);
    }

    [Fact]
    public void A413_ThatIsGenuinelyTooLarge_StillFailsFast()
    {
        // No rate-limit wording: the body really is too big, and every other provider would
        // reject it the same way.
        UpstreamFailure f = Classify(413, """{"error":{"message":"Request entity too large: maximum body size is 10MB"}}""");
        Assert.Equal(UpstreamFailureKind.BadRequest, f.Kind);
        Assert.False(UpstreamFailureClassifier.ShouldFailover(f));
    }

    // ── Retry-After parsing ──────────────────────────────────────────────────

    [Fact]
    public void RetryAfter_DeltaSeconds_IsParsed()
    {
        UpstreamFailure f = Classify(429, null, ("Retry-After", "30"));
        Assert.Equal(TimeSpan.FromSeconds(30), f.RetryAfter);
    }

    [Fact]
    public void RetryAfter_IsCaseInsensitive()
    {
        Assert.Equal(TimeSpan.FromSeconds(30), Classify(429, null, ("retry-after", "30")).RetryAfter);
        Assert.Equal(TimeSpan.FromSeconds(30), Classify(429, null, ("RETRY-AFTER", "30")).RetryAfter);
    }

    [Fact]
    public void RetryAfter_HttpDate_IsParsed()
    {
        string httpDate = DateTimeOffset.UtcNow.AddMinutes(5).ToString("r");
        TimeSpan? retry = Classify(429, null, ("Retry-After", httpDate)).RetryAfter;

        Assert.NotNull(retry);
        Assert.InRange(retry.Value, TimeSpan.FromMinutes(4), TimeSpan.FromMinutes(6));
    }

    [Theory]
    [InlineData("2m59.56s", 179.56)]
    [InlineData("1h30m", 5400)]
    [InlineData("500ms", 0.5)]
    [InlineData("45s", 45)]
    public void RetryAfter_CompoundDuration_IsParsed(string header, double expectedSeconds)
    {
        // Groq sends "Please try again in 2m59.56s" style values in its reset headers.
        TimeSpan? retry = Classify(429, null, ("x-ratelimit-reset-requests", header)).RetryAfter;

        Assert.NotNull(retry);
        Assert.Equal(expectedSeconds, retry.Value.TotalSeconds, precision: 2);
    }

    [Fact]
    public void RetryAfter_UnixTimestamp_IsConvertedToADelta()
    {
        long epoch = DateTimeOffset.UtcNow.AddMinutes(10).ToUnixTimeSeconds();
        TimeSpan? retry = Classify(429, null, ("x-ratelimit-reset-requests", epoch.ToString())).RetryAfter;

        Assert.NotNull(retry);
        Assert.InRange(retry.Value, TimeSpan.FromMinutes(9), TimeSpan.FromMinutes(11));
    }

    [Fact]
    public void RetryAfter_IsClampedToOneDay()
    {
        TimeSpan? retry = Classify(429, null, ("Retry-After", "999999")).RetryAfter;
        Assert.Equal(TimeSpan.FromDays(1), retry);
    }

    [Fact]
    public void RetryAfter_InThePast_IsIgnored()
    {
        string past = DateTimeOffset.UtcNow.AddHours(-1).ToString("r");
        Assert.Null(Classify(429, null, ("Retry-After", past)).RetryAfter);
        Assert.Null(Classify(429, null, ("Retry-After", "0")).RetryAfter);
    }

    [Fact]
    public void RetryAfter_Garbage_IsIgnored()
    {
        Assert.Null(Classify(429, null, ("Retry-After", "soon-ish")).RetryAfter);
        Assert.Null(Classify(429, null, ("Retry-After", "")).RetryAfter);
    }

    [Fact]
    public void NoHeaders_IsHandled()
    {
        Assert.Null(UpstreamFailureClassifier.Classify(429, null, null).RetryAfter);
    }
}
