using System.Security.Claims;
using System.Text.Json.Nodes;
using Keel.Core;
using Keel.Security;
using Keel.Workflow;

namespace Keel.UnitTests;

/// <summary>Signature hashing, step-up freshness and the auth_time claim (A9, ADR-0007).</summary>
public sealed class SignatureTests
{
    private static readonly Markings Itar = Markings.Create(ExportJurisdiction.Itar, DistributionStatement.D, "ENG", usmlCategory: "XI(d)",
        cuiCategories: ["EXPT"], controllingOffice: "ACME", distributionReason: "Critical technology", program: "ORCA").Value;

    private static readonly DateTimeOffset At = new(2026, 10, 27, 14, 5, 9, 123, TimeSpan.Zero);

    private static SignedContent Content(string description = "Bracket", string attributes = """{"mass":0.42,"material":"6061-T6"}""", Markings? markings = null) =>
        new("part", "P-000001", "A", Guid.Parse("018f0000-0000-7000-8000-000000000001"), "A.2", description, attributes, markings ?? Itar);

    private static SignedStatement Statement(SignedContent? c = null, string meaning = "Approved", string signer = "sub-alice", string? comment = null, DateTimeOffset? at = null) =>
        new(c ?? Content(), meaning, "approve", signer, comment, at ?? At);

    [Fact]
    public void Canonical_json_sorts_keys_and_is_stable()
    {
        var a = JsonNode.Parse("""{ "b": 1, "a": { "z": [3, 1], "y": null } }""");
        var b = JsonNode.Parse("""{"a":{"y":null,"z":[3,1]},"b":1}""");
        Assert.Equal("""{"a":{"y":null,"z":[3,1]},"b":1}""", CanonicalJson.Serialize(a));
        Assert.Equal(CanonicalJson.Serialize(a), CanonicalJson.Serialize(b));
    }

    [Fact]
    public void Attribute_key_order_does_not_change_the_hash_but_content_does()
    {
        var h = SignatureHasher.SignatureHash(Statement());
        Assert.Equal(32, h.Length);
        Assert.Equal(h, SignatureHasher.SignatureHash(Statement(Content(attributes: """{"material":"6061-T6","mass":0.42}"""))));
        Assert.NotEqual(h, SignatureHasher.SignatureHash(Statement(Content(description: "Bracket 2"))));
        Assert.NotEqual(h, SignatureHasher.SignatureHash(Statement(Content(attributes: """{"mass":0.43,"material":"6061-T6"}"""))));
    }

    [Fact]
    public void Every_signed_field_is_covered_by_the_hash()
    {
        var baseline = SignatureHasher.SignatureHash(Statement());
        var variants = new[]
        {
            Statement(meaning: "Rejected"),
            Statement(signer: "sub-bob"),
            Statement(comment: "looks fine"),
            Statement(at: At.AddMilliseconds(1)),
            Statement(Content(markings: Markings.Create(ExportJurisdiction.None, DistributionStatement.A, "ENG").Value)),
            Statement(Content() with { Revision = "B" }),
            Statement(Content() with { IterationId = Guid.CreateVersion7() }),
            Statement(Content() with { ObjectKey = "P-000002" }),
        };
        Assert.All(variants, v => Assert.NotEqual(baseline, SignatureHasher.SignatureHash(v)));
    }

    [Fact]
    public void Timestamps_hash_at_microsecond_precision_like_the_database()
    {
        var withNanos = At.AddTicks(7);
        Assert.Equal(SignatureHasher.Timestamp(At), SignatureHasher.Timestamp(withNanos));
        Assert.Equal("2026-10-27T14:05:09.123000Z", SignatureHasher.Timestamp(At));
        Assert.Equal(0, SignatureHasher.Truncate(withNanos).Ticks % 10);
    }

    [Fact]
    public void Markings_round_trip_through_their_signed_form()
    {
        var parsed = SignatureHasher.ParseMarkings(SignatureHasher.MarkingsJson(Itar));
        Assert.Equal(CanonicalJson.Serialize(SignatureHasher.MarkingsJson(Itar)), CanonicalJson.Serialize(SignatureHasher.MarkingsJson(parsed)));
        Assert.Equal(Itar.BannerText(), parsed.BannerText());
    }

    [Fact]
    public void Manifestation_names_signer_time_and_meaning()
    {
        Assert.Equal("Signed by Alice Example (alice) on 2026-10-27 14:05:09 UTC — Approved with comments",
            SignatureHasher.Manifestation("Alice Example", "alice", At, "Approved with comments"));
    }

    // ---------------------------------------------------------------- step-up

    private static readonly DateTimeOffset Now = new(2026, 10, 27, 15, 0, 0, TimeSpan.Zero);
    private static Subject Who(DateTimeOffset? authTime) => new() { Id = "u", DisplayName = "U", AuthTime = authTime };

    [Fact]
    public void Authentication_must_be_recent_to_sign()
    {
        var window = TimeSpan.FromSeconds(300);
        Assert.True(StepUp.IsFresh(Who(Now.AddSeconds(-10)), Now, window));
        Assert.True(StepUp.IsFresh(Who(Now.AddSeconds(-300)), Now, window));
        Assert.False(StepUp.IsFresh(Who(Now.AddSeconds(-301)), Now, window));
        Assert.False(StepUp.IsFresh(Who(null), Now, window));
        Assert.True(StepUp.IsFresh(Who(Now.AddSeconds(30)), Now, window));      // small clock skew is tolerated
        Assert.False(StepUp.IsFresh(Who(Now.AddMinutes(10)), Now, window));     // a token from the future is not
    }

    private static ClaimsPrincipal Principal(string? sub, bool authTime = true)
    {
        var claims = new List<Claim>();
        if (sub is not null) claims.Add(new Claim("sub", sub));
        if (authTime) claims.Add(new Claim("auth_time", "1790000000"));
        return new ClaimsPrincipal(new ClaimsIdentity(claims, "test"));
    }

    [Fact]
    public void Step_up_must_be_the_same_person_with_an_auth_time()
    {
        Assert.Equal(StepUp.Verdict.Accepted, StepUp.Check(Principal("alice"), Principal("alice")));
        Assert.Equal(StepUp.Verdict.SubjectMismatch, StepUp.Check(Principal("alice"), Principal("mallory")));
        Assert.Equal(StepUp.Verdict.SubjectMismatch, StepUp.Check(Principal("alice"), Principal(null)));
        Assert.Equal(StepUp.Verdict.MissingAuthTime, StepUp.Check(Principal("alice"), Principal("alice", authTime: false)));
        Assert.Equal(StepUp.Verdict.NoPriorSession, StepUp.Check(null, Principal("alice")));
        Assert.Equal(StepUp.Verdict.NoPriorSession, StepUp.Check(new ClaimsPrincipal(new ClaimsIdentity()), Principal("alice")));
    }

    [Fact]
    public void Subject_carries_auth_time_from_the_claim()
    {
        static Subject? From(params Claim[] extra) =>
            SubjectClaims.FromPrincipal(new ClaimsPrincipal(new ClaimsIdentity([new Claim("sub", "u"), .. extra], "test")));
        Assert.Equal(DateTimeOffset.FromUnixTimeSeconds(1790000000), From(new Claim("auth_time", "1790000000"))!.AuthTime);
        Assert.Null(From()!.AuthTime);
        Assert.Null(From(new Claim("auth_time", "yesterday"))!.AuthTime);
        Assert.Null(From(new Claim("auth_time", "-5"))!.AuthTime);
    }

    [Fact]
    public void Sign_drafts_are_one_time_and_expire()
    {
        var clock = new MutableClock(Now);
        var store = new SignDraftStore(clock);
        var task = Guid.NewGuid();
        store.Put(task, "alice", new SignDraft("Rejected", "too heavy"));
        Assert.Null(store.Take(task, "bob"));                       // keyed by user
        Assert.Equal("too heavy", store.Take(task, "alice")!.Comment);
        Assert.Null(store.Take(task, "alice"));                     // one time
        store.Put(task, "alice", new SignDraft(null, "later"));
        clock.Advance(SignDraftStore.Lifetime + TimeSpan.FromSeconds(1));
        Assert.Null(store.Take(task, "alice"));                     // expired
    }

    [Theory]
    [InlineData("/tasks/abc", "/tasks/abc")]
    [InlineData("/", "/")]
    [InlineData(null, "/")]
    [InlineData("", "/")]
    [InlineData("https://evil.example/x", "/")]
    [InlineData("//evil.example/x", "/")]
    [InlineData("/\\evil.example", "/")]
    [InlineData("tasks/abc", "/")]
    public void Step_up_returns_only_to_local_paths(string? returnUrl, string expected) =>
        Assert.Equal(expected, Keel.Web.Services.KeelUi.LocalReturnUrl(returnUrl));

    [Fact]
    public void Workflow_options_default_to_the_a9_window()
    {
        Assert.Equal(TimeSpan.FromSeconds(300), new WorkflowOptions().StepUpMaxAge);
    }
}

/// <summary>A clock tests can move.</summary>
public sealed class MutableClock(DateTimeOffset start) : TimeProvider
{
    private DateTimeOffset _now = start;
    public override DateTimeOffset GetUtcNow() => _now;
    public void Advance(TimeSpan by) => _now += by;
    public void Set(DateTimeOffset to) => _now = to;
}
