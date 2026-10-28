using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Keel.Audit;
using Keel.Siem;

namespace Keel.UnitTests;

/// <summary>Syslog formatting (RFC 5424 / 5425), chain verification, configuration rules and the file sink, without a database or a network.</summary>
public sealed partial class SiemTests
{
    private static List<AuditRecord> Chain(int n, Func<int, string>? payload = null)
    {
        var list = new List<AuditRecord>();
        var prev = AuditChain.GenesisHash;
        for (var i = 1; i <= n; i++)
        {
            var p = payload?.Invoke(i) ?? $"{{\"i\":{i}}}";
            var hash = AuditChain.ComputeHash(prev, i, p);
            list.Add(new AuditRecord(i, Guid.NewGuid(), new DateTimeOffset(2026, 10, 28, 9, 0, i, TimeSpan.Zero), "u-alice", "part.read", "part", $"P-{i:000000}",
                AuditOutcome.Success, $"corr-{i}", p, prev, hash));
            prev = hash;
        }
        return list;
    }

    private static SyslogFormatter Formatter(SiemOptions? o = null) => new(o ?? new SiemOptions { Enabled = true }, "keel-host.example", 4242);

    // ---------------------------------------------------------------- RFC 5424

    [GeneratedRegex(@"^<(?<pri>\d+)>1 (?<ts>\S+) (?<host>\S+) (?<app>\S+) (?<proc>\S+) (?<msgid>\S+) \[(?<sdid>keel@\d+)(?<params>(?: [A-Za-z]+=""(?:[^""\\\]]|\\[""\\\]])*"")*)\] (?<msg>.*)$", RegexOptions.Singleline)]
    private static partial Regex SyslogLine();

    [GeneratedRegex(@" (?<name>[A-Za-z]+)=""(?<value>(?:[^""\\\]]|\\[""\\\]])*)""")]
    private static partial Regex SdParam();

    private static Dictionary<string, string> Params(Match m) =>
        SdParam().Matches(m.Groups["params"].Value).ToDictionary(p => p.Groups["name"].Value, p => Regex.Replace(p.Groups["value"].Value, @"\\([""\\\]])", "$1"));

    [Fact]
    public void An_event_becomes_an_rfc5424_message_with_the_chain_in_the_structured_data()
    {
        var r = Chain(3)[2];
        var message = Formatter().Format(r);
        var m = SyslogLine().Match(message.Syslog);

        Assert.True(m.Success, message.Syslog);
        Assert.Equal((13 * 8 + 6).ToString(), m.Groups["pri"].Value); // facility 13 (log audit), severity info
        Assert.Equal("2026-10-28T09:00:03.000000Z", m.Groups["ts"].Value);
        Assert.Equal(("keel-host.example", "keel-plm", "4242", "part.read"), (m.Groups["host"].Value, m.Groups["app"].Value, m.Groups["proc"].Value, m.Groups["msgid"].Value));
        Assert.Equal("keel@32473", m.Groups["sdid"].Value); // the documented placeholder enterprise number
        var p = Params(m);
        Assert.Equal(("3", Convert.ToHexStringLower(r.Hash), Convert.ToHexStringLower(r.PrevHash), r.Id.ToString("D")), (p["seq"], p["hash"], p["prevHash"], p["id"]));
        Assert.Equal(("u-alice", "part.read", "Success", "part:P-000003", "corr-3"), (p["actor"], p["action"], p["outcome"], p["object"], p["corr"]));
        Assert.Equal("part.read Success object=part:P-000003 actor=u-alice", m.Groups["msg"].Value);
    }

    [Fact]
    public void Structured_data_values_escape_quote_backslash_and_closing_bracket()
    {
        Assert.Equal(@"a\""b\\c\]d", SyslogFormatter.EscapeParamValue(@"a""b\c]d"));
        Assert.Equal("[ is fine unescaped", SyslogFormatter.EscapeParamValue("[ is fine unescaped"));

        var hostile = Chain(1)[0] with { Actor = "u-\"evil\\]\"", ObjectId = "GET /api/v1/x?a=\"b\"]" };
        var m = SyslogLine().Match(Formatter().Format(hostile).Syslog);
        Assert.True(m.Success);
        var p = Params(m);
        Assert.Equal("u-\"evil\\]\"", p["actor"]); // survives escaping and unescaping exactly
        Assert.Equal("part:GET /api/v1/x?a=\"b\"]", p["object"]);
    }

    [Fact]
    public void Denied_and_failed_events_are_louder_than_normal_ones()
    {
        Assert.Equal(6, SyslogFormatter.Severity(AuditOutcome.Success));
        Assert.Equal(4, SyslogFormatter.Severity(AuditOutcome.Denied));
        Assert.Equal(3, SyslogFormatter.Severity(AuditOutcome.Failure));
        var options = new SiemOptions { Enabled = true, Facility = 4 };
        var denied = Chain(1)[0] with { Outcome = AuditOutcome.Denied };
        Assert.StartsWith("<36>1 ", Formatter(options).Format(denied).Syslog, StringComparison.Ordinal); // 4 * 8 + 4
    }

    [Fact]
    public void Octet_counting_frames_state_the_length_in_bytes_not_characters()
    {
        var text = "<14>1 2026-10-28T09:00:00.000000Z h a 1 id - café – \U0001F512";
        var frame = SyslogFormatter.Frame(text);
        var space = Array.IndexOf(frame, (byte)' ');
        var declared = int.Parse(Encoding.ASCII.GetString(frame, 0, space), System.Globalization.CultureInfo.InvariantCulture);
        Assert.Equal(Encoding.UTF8.GetByteCount(text), declared);
        Assert.NotEqual(text.Length, declared);
        Assert.Equal(frame.Length - space - 1, declared);
        Assert.Equal(text, Encoding.UTF8.GetString(frame, space + 1, declared));
    }

    [Fact]
    public void Msgid_is_printable_ascii_without_spaces_and_at_most_32_characters()
    {
        Assert.Equal("document.download", SyslogFormatter.MsgId("document.download"));
        Assert.Equal("ab", SyslogFormatter.MsgId("a bé"));
        Assert.Equal(32, SyslogFormatter.MsgId(new string('x', 80)).Length);
        Assert.Equal("audit", SyslogFormatter.MsgId("éé"));
    }

    [Fact]
    public void Messages_carry_identifiers_only_never_the_payload_with_names_and_markings()
    {
        var secret = Chain(1, _ => "{\"details\":{\"name\":\"Secret Aurora bracket\",\"description\":\"ITAR USML XI(d) CUI//EXPT\"},\"actor\":{\"name\":\"Alice Analyst\"}}")[0];
        var message = Formatter().Format(secret);
        foreach (var forbidden in new[] { "Aurora", "bracket", "USML", "CUI", "Alice", "details", "description" })
        {
            Assert.DoesNotContain(forbidden, message.Syslog, StringComparison.Ordinal);
            Assert.DoesNotContain(forbidden, message.Json, StringComparison.Ordinal);
        }
        using var json = JsonDocument.Parse(message.Json);
        Assert.Equal(secret.Seq, json.RootElement.GetProperty("seq").GetInt64());
        Assert.Equal(Convert.ToHexStringLower(secret.PrevHash), json.RootElement.GetProperty("prevHash").GetString());
    }

    [Fact]
    public void Checkpoint_and_alert_messages_use_their_own_msgids()
    {
        var f = Formatter();
        var cp = f.FormatCheckpoint(7, 120, new byte[32], new DateTimeOffset(2026, 10, 28, 10, 0, 0, TimeSpan.Zero), "ab12cd34ef56ab12");
        var m = SyslogLine().Match(cp.Syslog);
        Assert.True(m.Success);
        Assert.Equal("audit.checkpoint", m.Groups["msgid"].Value);
        Assert.Equal(("7", "120", "ab12cd34ef56ab12"), (Params(m)["checkpoint"], Params(m)["seq"], Params(m)["keyId"]));

        var alert = f.FormatAlert("gap", 55, "expected seq 54 after 53, read 55", DateTimeOffset.UnixEpoch);
        var am = SyslogLine().Match(alert.Syslog);
        Assert.True(am.Success);
        Assert.Equal(("siem.chain_break", (13 * 8 + 2).ToString()), (am.Groups["msgid"].Value, am.Groups["pri"].Value)); // critical
        Assert.True(alert.IsAlert);
    }

    // ---------------------------------------------------------------- chain verification

    [Fact]
    public void A_contiguous_chain_verifies_and_resumes_from_a_cursor()
    {
        var chain = Chain(6);
        var tracker = new ChainTracker(0, AuditChain.GenesisHash);
        Assert.All(chain, r => Assert.Empty(tracker.Accept(r)));
        Assert.Equal(6, tracker.LastSeq);

        var resumed = new ChainTracker(3, chain[2].Hash);
        Assert.All(chain.Skip(3), r => Assert.Empty(resumed.Accept(r)));
    }

    [Fact]
    public void A_deleted_event_is_a_gap_and_the_link_no_longer_matches()
    {
        var chain = Chain(5);
        var tracker = new ChainTracker(0, AuditChain.GenesisHash);
        tracker.Accept(chain[0]);
        tracker.Accept(chain[1]);
        var problems = tracker.Accept(chain[3]); // seq 3 is missing
        Assert.Contains(problems, p => p is { Kind: ChainBreakKind.Gap, Seq: 4 });
        Assert.Contains(problems, p => p.Kind == ChainBreakKind.LinkMismatch);
        Assert.Empty(tracker.Accept(chain[4])); // reported once; the chain after it is consistent again
    }

    [Fact]
    public void A_rewritten_row_fails_its_hash()
    {
        var chain = Chain(3);
        var tampered = chain[1] with { Payload = "{\"i\":2,\"changed\":true}" };
        var tracker = new ChainTracker(0, AuditChain.GenesisHash);
        tracker.Accept(chain[0]);
        Assert.Contains(tracker.Accept(tampered), p => p.Kind == ChainBreakKind.HashMismatch);
    }

    [Fact]
    public void A_changed_prev_hash_is_a_link_mismatch()
    {
        var chain = Chain(3);
        var broken = chain[2] with { PrevHash = new byte[32] };
        var tracker = new ChainTracker(0, AuditChain.GenesisHash);
        tracker.Accept(chain[0]);
        tracker.Accept(chain[1]);
        var problems = tracker.Accept(broken);
        Assert.Contains(problems, p => p.Kind == ChainBreakKind.LinkMismatch);
        Assert.Contains(problems, p => p.Kind == ChainBreakKind.HashMismatch);
    }

    // ---------------------------------------------------------------- configuration

    private static SiemOptions Valid(Action<SiemDestinationOptions>? change = null)
    {
        var d = new SiemDestinationOptions { Name = "primary", Host = "siem.example.mil", Port = 6514 };
        change?.Invoke(d);
        return new SiemOptions { Enabled = true, Destinations = [d] };
    }

    [Fact]
    public void Disabled_forwarding_needs_no_configuration()
    {
        Assert.Empty(new SiemOptions().Validate(requireFips: true));
        Assert.Empty(Valid().Validate(requireFips: true));
    }

    [Fact]
    public void UDP_syslog_is_not_supported_and_the_error_says_why()
    {
        var e = Assert.Throws<NotSupportedException>(() => Valid(d => d.Transport = SyslogTransport.Udp).Validate(false));
        Assert.Contains("no delivery guarantee", e.Message, StringComparison.Ordinal);
        Assert.Contains("TLS", e.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Plain_tcp_needs_the_development_acknowledgement_and_is_never_allowed_in_production()
    {
        Assert.Single(Valid(d => d.Transport = SyslogTransport.Tcp).Validate(false));
        var ack = Valid(d => { d.Transport = SyslogTransport.Tcp; d.AllowInsecureTcp = true; });
        Assert.Empty(ack.Validate(requireFips: false));
        Assert.Contains(ack.Validate(requireFips: true), e => e.Contains("RequireFips", StringComparison.Ordinal));
        Assert.Contains(Valid(d => d.AllowInsecureTcp = true).Validate(false), e => e.Contains("Transport=Tcp", StringComparison.Ordinal));
    }

    [Fact]
    public void Destination_rules_cover_names_host_mtls_and_files()
    {
        Assert.NotEmpty(Valid(d => d.Name = "Bad Name").Validate(false));
        Assert.NotEmpty(Valid(d => d.Host = "").Validate(false));
        Assert.NotEmpty(Valid(d => d.ClientCertificatePath = "client.crt").Validate(false)); // key missing
        Assert.Empty(Valid(d => { d.ClientCertificatePath = "client.crt"; d.ClientKeyPath = "client.key"; }).Validate(true));
        Assert.NotEmpty(new SiemOptions { Enabled = true }.Validate(false)); // no destinations
        var twice = Valid();
        twice.Destinations.Add(new SiemDestinationOptions { Name = "primary", Host = "other" });
        Assert.Contains(twice.Validate(false), e => e.Contains("used twice", StringComparison.Ordinal));
        Assert.Empty(new SiemOptions { Enabled = true, Destinations = [new SiemDestinationOptions { Name = "files", Type = SiemDestinationType.File, FilePath = "/var/log/keel/audit.ndjson" }] }.Validate(true));
        Assert.NotEmpty(new SiemOptions { Enabled = true, Destinations = [new SiemDestinationOptions { Name = "files", Type = SiemDestinationType.File }] }.Validate(false));
    }

    [Fact]
    public void Backoff_grows_exponentially_is_capped_and_is_jittered()
    {
        var min = TimeSpan.FromSeconds(1);
        var max = TimeSpan.FromSeconds(60);
        Assert.Equal(1.0, Backoff.Next(0, min, max, 0.999999).TotalSeconds, 3);
        Assert.Equal(0.5, Backoff.Next(0, min, max, 0).TotalSeconds, 3);
        Assert.Equal(8.0, Backoff.Next(3, min, max, 0.999999).TotalSeconds, 3);
        Assert.Equal(60.0, Backoff.Next(30, min, max, 0.999999).TotalSeconds, 3);
        Assert.InRange(Backoff.Next(10, min, max, 0.5).TotalSeconds, 30, 60);
    }

    // ---------------------------------------------------------------- file sink

    [Fact]
    public async Task File_sink_appends_ndjson_and_survives_rotation()
    {
        var dir = Directory.CreateTempSubdirectory("keel-siem-").FullName;
        try
        {
            var path = Path.Combine(dir, "logs", "audit.ndjson");
            var sink = new FileSink(new SiemDestinationOptions { Name = "files", Type = SiemDestinationType.File, FilePath = path }, null);
            var f = Formatter();
            var chain = Chain(4);

            await sink.SendAsync([f.Format(chain[0]), f.Format(chain[1])], CancellationToken.None);
            File.Move(path, path + ".1"); // logrotate renames the file
            await sink.SendAsync([f.Format(chain[2]), f.Format(chain[3])], CancellationToken.None);

            var rotated = File.ReadAllLines(path + ".1");
            var fresh = File.ReadAllLines(path);
            Assert.Equal(2, rotated.Length);
            Assert.Equal(2, fresh.Length);
            Assert.Equal([1L, 2L, 3L, 4L], rotated.Concat(fresh).Select(l => JsonDocument.Parse(l).RootElement.GetProperty("seq").GetInt64()));
            Assert.All(rotated.Concat(fresh), l => Assert.DoesNotContain('\n', l));
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public async Task File_sink_resolves_relative_paths_against_the_secrets_directory_and_reports_failures()
    {
        var dir = Directory.CreateTempSubdirectory("keel-siem-").FullName;
        try
        {
            var sink = new FileSink(new SiemDestinationOptions { Name = "files", Type = SiemDestinationType.File, FilePath = "relative.ndjson" }, dir);
            await sink.SendAsync([Formatter().Format(Chain(1)[0])], CancellationToken.None);
            Assert.True(File.Exists(Path.Combine(dir, "relative.ndjson")));

            var blocked = new FileSink(new SiemDestinationOptions { Name = "files", Type = SiemDestinationType.File, FilePath = dir }, null); // a directory, not a file
            await Assert.ThrowsAsync<SiemSendException>(() => blocked.SendAsync([Formatter().Format(Chain(1)[0])], CancellationToken.None));
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }
}
