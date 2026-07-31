namespace ProxyTests;

/// <summary>
/// Cooldown behaviour, driven by an injected clock so a test can cross midnight, a month
/// boundary or a DST change without sleeping.
/// </summary>
public sealed class ProviderHealthServiceTests
{
    /// <summary>A clock the test moves by hand.</summary>
    private sealed class FakeClock(DateTimeOffset start)
    {
        internal DateTimeOffset Now { get; private set; } = start;
        internal void Advance(TimeSpan by) => Now += by;
        internal void Set(DateTimeOffset to) => Now = to;
        internal Func<DateTimeOffset> Func => () => Now;
    }

    private static readonly TimeSpan Utc = TimeSpan.Zero;

    private static (ProviderHealthService Health, FakeClock Clock) Build(DateTimeOffset? start = null)
    {
        var clock = new FakeClock(start ?? new DateTimeOffset(2026, 7, 31, 14, 0, 0, Utc));
        return (new ProviderHealthService(clock.Func), clock);
    }

    private static UpstreamFailure Quota(QuotaPeriod period, TimeSpan? retryAfter = null) =>
        new(UpstreamFailureKind.QuotaExhausted, 429, retryAfter, period, "daily-limit");

    private static UpstreamFailure RateLimit(TimeSpan? retryAfter = null) =>
        new(UpstreamFailureKind.RateLimit, 429, retryAfter, QuotaPeriod.None, null);

    // ── Quota exhaustion waits for the window, not a backoff curve ───────────

    [Fact]
    public void DailyQuotaExhausted_CoolsDownUntilNextMidnight()
    {
        var (health, clock) = Build(new DateTimeOffset(2026, 7, 31, 14, 0, 0, Utc));

        health.RecordFailure("groq", "gpt-oss-120b", Quota(QuotaPeriod.Daily));

        Assert.True(health.IsCoolingDown("groq", "gpt-oss-120b", out CooldownState? state));
        Assert.NotNull(state);
        Assert.Equal(new DateTimeOffset(2026, 8, 1, 0, 0, 0, Utc), state.UntilUtc);
        Assert.Equal(UpstreamFailureKind.QuotaExhausted, state.Kind);

        // Still cooling one minute before midnight, available one minute after.
        clock.Set(new DateTimeOffset(2026, 7, 31, 23, 59, 0, Utc));
        Assert.True(health.IsCoolingDown("groq", "gpt-oss-120b", out _));

        clock.Set(new DateTimeOffset(2026, 8, 1, 0, 1, 0, Utc));
        Assert.False(health.IsCoolingDown("groq", "gpt-oss-120b", out _));
    }

    [Fact]
    public void MonthlyQuotaExhausted_CoolsDownUntilTheFirstOfNextMonth()
    {
        var (health, _) = Build(new DateTimeOffset(2026, 7, 31, 14, 0, 0, Utc));

        health.RecordFailure("mistral", "mistral-large", Quota(QuotaPeriod.Monthly));

        Assert.True(health.IsCoolingDown("mistral", "mistral-large", out CooldownState? state));
        Assert.Equal(new DateTimeOffset(2026, 8, 1, 0, 0, 0, Utc), state!.UntilUtc);
    }

    [Fact]
    public void MonthlyQuota_CrossingAYearBoundary_RollsToJanuary()
    {
        var (health, _) = Build(new DateTimeOffset(2026, 12, 15, 9, 0, 0, Utc));

        health.RecordFailure("mistral", "mistral-large", Quota(QuotaPeriod.Monthly));

        Assert.True(health.IsCoolingDown("mistral", "mistral-large", out CooldownState? state));
        Assert.Equal(new DateTimeOffset(2027, 1, 1, 0, 0, 0, Utc), state!.UntilUtc);
    }

    [Fact]
    public void CreditExhausted_RetriesInSixHours_BecauseCreditsRefillOnTopUp()
    {
        var (health, _) = Build();

        health.RecordFailure("openrouter", "some-model", Quota(QuotaPeriod.Credit));

        Assert.True(health.IsCoolingDown("openrouter", "some-model", out CooldownState? state));
        Assert.Equal(new DateTimeOffset(2026, 7, 31, 20, 0, 0, Utc), state!.UntilUtc);
    }

    [Fact]
    public void NonUtcOffset_UsesLocalMidnight()
    {
        // Daily free tiers reset on the provider's calendar day; local midnight is what a user
        // reading the dashboard expects to see.
        var madrid = new TimeSpan(2, 0, 0);
        var (health, _) = Build(new DateTimeOffset(2026, 7, 31, 23, 0, 0, madrid));

        health.RecordFailure("groq", "m", Quota(QuotaPeriod.Daily));

        Assert.True(health.IsCoolingDown("groq", "m", out CooldownState? state));
        Assert.Equal(new DateTimeOffset(2026, 8, 1, 0, 0, 0, madrid), state!.UntilUtc);
    }

    // ── Upstream hints win over anything computed locally ────────────────────

    [Fact]
    public void RetryAfter_OverridesTheComputedCooldown()
    {
        var (health, _) = Build();

        health.RecordFailure("groq", "m", Quota(QuotaPeriod.Daily, retryAfter: TimeSpan.FromMinutes(3)));

        Assert.True(health.IsCoolingDown("groq", "m", out CooldownState? state));
        Assert.Equal(new DateTimeOffset(2026, 7, 31, 14, 3, 0, Utc), state!.UntilUtc);
    }

    // ── Rate limit: short and exponential ────────────────────────────────────

    [Fact]
    public void RateLimit_BacksOffExponentially_AndIsCapped()
    {
        var (health, clock) = Build();

        health.RecordFailure("groq", "m", RateLimit());
        Assert.True(health.IsCoolingDown("groq", "m", out CooldownState? first));
        Assert.Equal(5, (first!.UntilUtc - clock.Now).TotalSeconds, precision: 1);

        // Second failure inside the escalation window doubles it.
        clock.Advance(TimeSpan.FromSeconds(10));
        health.RecordFailure("groq", "m", RateLimit());
        Assert.True(health.IsCoolingDown("groq", "m", out CooldownState? second));
        Assert.Equal(10, (second!.UntilUtc - clock.Now).TotalSeconds, precision: 1);

        // It never escalates past five minutes.
        for (int i = 0; i < 20; i++)
        {
            clock.Advance(TimeSpan.FromSeconds(1));
            health.RecordFailure("groq", "m", RateLimit());
        }
        Assert.True(health.IsCoolingDown("groq", "m", out CooldownState? capped));
        Assert.True((capped!.UntilUtc - clock.Now) <= TimeSpan.FromMinutes(5));
    }

    [Fact]
    public void EscalationLadder_ResetsAfterAQuietPeriod()
    {
        var (health, clock) = Build();

        health.RecordFailure("groq", "m", RateLimit());
        clock.Advance(TimeSpan.FromHours(2)); // Long quiet spell — not "continuously failing".
        health.RecordFailure("groq", "m", RateLimit());

        Assert.True(health.IsCoolingDown("groq", "m", out CooldownState? state));
        Assert.Equal(1, state!.FailureCount);
        Assert.Equal(5, (state.UntilUtc - clock.Now).TotalSeconds, precision: 1);
    }

    // ── Scope: a missing model must not disable the whole provider ───────────

    [Fact]
    public void ModelUnavailable_IsScopedToThatModelOnly()
    {
        var (health, _) = Build();

        health.RecordFailure("nvidia", "kimi-k2.6", new UpstreamFailure(UpstreamFailureKind.ModelUnavailable, 404, null, QuotaPeriod.None, null));

        Assert.True(health.IsCoolingDown("nvidia", "kimi-k2.6", out _));
        // Every other model on NVIDIA keeps working.
        Assert.False(health.IsCoolingDown("nvidia", "nemotron-3", out _));
    }

    [Fact]
    public void QuotaExhaustion_IsScopedToTheWholeProvider()
    {
        var (health, _) = Build();

        health.RecordFailure("groq", "gpt-oss-120b", Quota(QuotaPeriod.Daily));

        // A spent account budget applies to every model that account serves.
        Assert.True(health.IsCoolingDown("groq", "some-other-model", out _));
    }

    [Fact]
    public void BadRequest_IsNotRecorded_BecauseItSaysNothingAboutTheProvider()
    {
        var (health, _) = Build();

        health.RecordFailure("groq", "m", new UpstreamFailure(UpstreamFailureKind.BadRequest, 400, null, QuotaPeriod.None, null));

        Assert.False(health.IsCoolingDown("groq", "m", out _));
        Assert.Empty(health.Snapshot());
    }

    [Fact]
    public void SingleTransientFailure_DoesNotCoolDown()
    {
        var (health, _) = Build();

        health.RecordFailure("groq", "m", new UpstreamFailure(UpstreamFailureKind.Transient, 503, null, QuotaPeriod.None, null));
        Assert.False(health.IsCoolingDown("groq", "m", out _));

        // Only a repeated pattern opens the breaker.
        health.RecordFailure("groq", "m", new UpstreamFailure(UpstreamFailureKind.Transient, 503, null, QuotaPeriod.None, null));
        health.RecordFailure("groq", "m", new UpstreamFailure(UpstreamFailureKind.Transient, 503, null, QuotaPeriod.None, null));
        Assert.True(health.IsCoolingDown("groq", "m", out _));
    }

    // ── Success decay ────────────────────────────────────────────────────────

    [Fact]
    public void Success_HalvesTheFailureCount_AndClearsAtZero()
    {
        var (health, clock) = Build();

        health.RecordFailure("groq", "m", RateLimit());
        health.RecordFailure("groq", "m", RateLimit());
        Assert.True(health.IsCoolingDown("groq", "m", out CooldownState? before));
        Assert.Equal(2, before!.FailureCount);

        health.RecordSuccess("groq", "m");           // 2 -> 1, available again immediately
        clock.Advance(TimeSpan.FromMilliseconds(1));
        Assert.False(health.IsCoolingDown("groq", "m", out _));

        health.RecordSuccess("groq", "m");           // 1 -> 0, entry removed
        Assert.Empty(health.Snapshot());
    }

    [Fact]
    public void Success_DoesNotResurrectAnUnrelatedProvider()
    {
        var (health, _) = Build();

        health.RecordFailure("groq", "m", Quota(QuotaPeriod.Daily));
        health.RecordSuccess("nvidia", "m");

        Assert.True(health.IsCoolingDown("groq", "m", out _));
    }

    // ── Ordering: degrade, never exclude ─────────────────────────────────────

    private static (ProviderInfo Provider, string UpstreamModel) Candidate(string name)
    {
        ProviderCapabilities caps = ProviderCapabilitiesRegistry.Get(name);
        return (new ProviderInfo(name, "k", caps.DefaultBaseUrl, new HttpClient(), caps), "shared-model");
    }

    [Fact]
    public void Order_MovesCoolingProvidersToTheBack_PreservingHealthyOrder()
    {
        var (health, _) = Build();
        var candidates = new[] { Candidate("groq"), Candidate("nvidia"), Candidate("openrouter") };

        health.RecordFailure("groq", "shared-model", Quota(QuotaPeriod.Daily));

        var ordered = health.Order(candidates, "shared-model");

        Assert.Equal(3, ordered.Count);
        Assert.Equal("nvidia", ordered[0].Provider.Name);
        Assert.Equal("openrouter", ordered[1].Provider.Name);
        Assert.Equal("groq", ordered[2].Provider.Name);
    }

    [Fact]
    public void Order_NeverReturnsAnEmptyList_EvenWhenEveryProviderIsCooling()
    {
        var (health, _) = Build();
        var candidates = new[] { Candidate("groq"), Candidate("nvidia") };

        health.RecordFailure("groq", "shared-model", Quota(QuotaPeriod.Daily));
        health.RecordFailure("nvidia", "shared-model", Quota(QuotaPeriod.Daily));

        var ordered = health.Order(candidates, "shared-model");

        // A last-ditch attempt beats a hard error the user cannot act on.
        Assert.Equal(2, ordered.Count);
    }

    [Fact]
    public void Order_PutsTheSoonestToRecoverFirstAmongCoolingProviders()
    {
        var (health, clock) = Build();
        var candidates = new[] { Candidate("groq"), Candidate("nvidia") };

        health.RecordFailure("groq", "shared-model", Quota(QuotaPeriod.Daily));      // until midnight
        clock.Advance(TimeSpan.FromSeconds(1));
        health.RecordFailure("nvidia", "shared-model", RateLimit());                  // 5 seconds

        var ordered = health.Order(candidates, "shared-model");

        Assert.Equal("nvidia", ordered[0].Provider.Name);
        Assert.Equal("groq", ordered[1].Provider.Name);
    }

    [Fact]
    public void Order_IsAPassThrough_WhenNothingIsCooling()
    {
        var (health, _) = Build();
        var candidates = new[] { Candidate("groq"), Candidate("nvidia") };

        var ordered = health.Order(candidates, "shared-model");

        Assert.Equal("groq", ordered[0].Provider.Name);
        Assert.Equal("nvidia", ordered[1].Provider.Name);
    }

    // ── Existing stand-downs are never shortened ─────────────────────────────

    [Fact]
    public void ATransientBlip_DoesNotShortenAnExistingDailyQuotaCooldown()
    {
        var (health, _) = Build();

        health.RecordFailure("groq", "m", Quota(QuotaPeriod.Daily));
        health.RecordFailure("groq", "m", RateLimit());   // would only be 10s on its own

        Assert.True(health.IsCoolingDown("groq", "m", out CooldownState? state));
        Assert.Equal(new DateTimeOffset(2026, 8, 1, 0, 0, 0, Utc), state!.UntilUtc);
    }

    // ── Reporting and manual reset ───────────────────────────────────────────

    [Fact]
    public void Snapshot_ListsActiveCooldowns_SoonestFirst_AndDropsExpiredOnes()
    {
        var (health, clock) = Build();

        health.RecordFailure("groq", "m", Quota(QuotaPeriod.Daily));
        health.RecordFailure("nvidia", "m", RateLimit());

        var snapshot = health.Snapshot();
        Assert.Equal(2, snapshot.Count);
        Assert.Equal("nvidia", snapshot[0].Provider);

        clock.Advance(TimeSpan.FromMinutes(1));  // the nvidia 5s window has passed
        Assert.Single(health.Snapshot());
        Assert.Equal("groq", health.Snapshot()[0].Provider);
    }

    [Fact]
    public void Clear_ReEnablesAProviderImmediately()
    {
        var (health, _) = Build();
        health.RecordFailure("groq", "m", Quota(QuotaPeriod.Daily));

        Assert.True(health.Clear("groq"));
        Assert.False(health.IsCoolingDown("groq", "m", out _));
        Assert.False(health.Clear("groq"));   // already gone
    }

    [Fact]
    public void RecentFailovers_AreReportedNewestFirst_AndBounded()
    {
        var (health, _) = Build();

        for (int i = 0; i < 150; i++)
            health.RecordFailover("groq", "nvidia", $"model-{i}", 429, UpstreamFailureKind.QuotaExhausted, 12);

        var events = health.RecentFailovers();
        Assert.Equal(100, events.Count);
        Assert.Equal("model-149", events[0].Model);
    }

    [Fact]
    public void SecondsRemaining_NeverGoesNegative()
    {
        var (health, clock) = Build();
        health.RecordFailure("groq", "m", RateLimit());
        health.IsCoolingDown("groq", "m", out CooldownState? state);

        clock.Advance(TimeSpan.FromHours(1));
        Assert.Equal(0, state!.SecondsRemaining(clock.Now));
    }
}
