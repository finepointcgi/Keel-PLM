using System.Text.Json.Nodes;
using Keel.Integration;
using Keel.Notifications;

namespace Keel.UnitTests;

/// <summary>Bounce mail parsing, tags, option validation and the mail text of the verification flow (ADR-0055).</summary>
public sealed class DsnParserTests
{
    private const string Secret = "0123456789abcdef0123456789abcdef-secret";

    private static string Dsn(string recipientBlock, string? tag = "TAGHERE", string reportType = "delivery-status", string statusPartType = "message/delivery-status", string original = "headers")
    {
        var returned = tag is null ? "From: keel@corp.example\r\nTo: rita@corp.example\r\n" : $"From: keel@corp.example\r\nTo: rita@corp.example\r\nX-Keel-Bounce-Tag: {tag}\r\n";
        return "From: MAILER-DAEMON@corp.example\r\nTo: keel@corp.example\r\nSubject: Undelivered Mail Returned to Sender\r\n" +
               $"Content-Type: multipart/report; report-type={reportType}; boundary=\"BND\"\r\n\r\n" +
               "--BND\r\nContent-Type: text/plain\r\n\r\nThis is the mail system.\r\n" +
               $"--BND\r\nContent-Type: {statusPartType}\r\n\r\nReporting-MTA: dns; relay.corp.example\r\n\r\n{recipientBlock}\r\n" +
               (original == "headers" ? $"--BND\r\nContent-Type: text/rfc822-headers\r\n\r\n{returned}\r\n" : $"--BND\r\nContent-Type: message/rfc822\r\n\r\n{returned}\r\nbody\r\n") +
               "--BND--\r\n";
    }

    [Fact]
    public void A_failed_delivery_with_a_5xx_status_is_a_hard_bounce_and_carries_the_returned_tag()
    {
        var parsed = Assert.Single(DsnParser.Parse(Dsn("Final-Recipient: rfc822; rita@corp.example\r\nAction: failed\r\nStatus: 5.1.1\r\nDiagnostic-Code: smtp; 550 5.1.1 <rita@corp.example>: user unknown")));
        Assert.Equal("rita@corp.example", parsed.Report.Address);
        Assert.Equal(BounceKinds.Hard, parsed.Report.Kind);
        Assert.Equal("5.1.1", parsed.Report.Status);
        Assert.Equal("TAGHERE", parsed.ReturnedTag);
        Assert.DoesNotContain("@", EmailBounceService.Sanitize(parsed.Report.Diagnostic), StringComparison.Ordinal); // the stored diagnostic never holds an address
    }

    [Theory]
    [InlineData("Action: delayed\r\nStatus: 4.4.1", BounceKinds.Soft)]
    [InlineData("Action: failed\r\nStatus: 4.2.0", BounceKinds.Soft)]
    [InlineData("Action: failed\r\nStatus: 5.2.2", BounceKinds.Soft)] // mailbox full is not a dead address
    [InlineData("Action: failed\r\nStatus: 5.7.1", BounceKinds.Hard)]
    [InlineData("Action: failed", BounceKinds.Hard)]
    public void Delays_and_temporary_failures_are_soft_and_permanent_failures_hard(string fields, string expected)
    {
        var parsed = Assert.Single(DsnParser.Parse(Dsn("Final-Recipient: rfc822; rita@corp.example\r\n" + fields)));
        Assert.Equal(expected, parsed.Report.Kind);
    }

    [Theory]
    [InlineData("Action: delivered\r\nStatus: 2.0.0")]
    [InlineData("Action: relayed\r\nStatus: 2.0.0")]
    [InlineData("Action: expanded\r\nStatus: 2.0.0")]
    public void Successful_delivery_notices_are_not_reports(string fields) =>
        Assert.Empty(DsnParser.Parse(Dsn("Final-Recipient: rfc822; rita@corp.example\r\n" + fields)));

    [Fact]
    public void Every_recipient_block_is_reported_and_folded_headers_and_an_embedded_message_are_read()
    {
        var blocks = "Final-Recipient: rfc822;\r\n  rita@corp.example\r\nAction: failed\r\nStatus: 5.1.1\r\n\r\nFinal-Recipient: rfc822; bob@corp.example\r\nAction: failed\r\nStatus: 4.2.1";
        var parsed = DsnParser.Parse(Dsn(blocks, original: "message"));
        Assert.Equal(["rita@corp.example", "bob@corp.example"], parsed.Select(p => p.Report.Address));
        Assert.Equal([BounceKinds.Hard, BounceKinds.Soft], parsed.Select(p => p.Report.Kind));
        Assert.All(parsed, p => Assert.Equal("TAGHERE", p.ReturnedTag));
    }

    [Fact]
    public void An_abuse_report_is_a_complaint_and_other_feedback_types_are_ignored()
    {
        var abuse = Dsn("Feedback-Type: abuse\r\nUser-Agent: test/1\r\nOriginal-Rcpt-To: <rita@corp.example>", reportType: "feedback-report", statusPartType: "message/feedback-report");
        var parsed = Assert.Single(DsnParser.Parse(abuse));
        Assert.Equal(BounceKinds.Complaint, parsed.Report.Kind);
        Assert.Equal("rita@corp.example", parsed.Report.Address);
        Assert.Empty(DsnParser.Parse(Dsn("Feedback-Type: not-spam\r\nOriginal-Rcpt-To: <rita@corp.example>", reportType: "feedback-report", statusPartType: "message/feedback-report")));
    }

    [Fact]
    public void Ordinary_mail_oversize_mail_and_garbage_yield_nothing()
    {
        Assert.Empty(DsnParser.Parse("From: a@b.example\r\nSubject: hi\r\n\r\nFinal-Recipient: rfc822; rita@corp.example\r\nAction: failed\r\n"));
        Assert.Empty(DsnParser.Parse(Dsn("Final-Recipient: rfc822; rita@corp.example\r\nAction: failed") + new string('x', DsnParser.MaxBytes)));
        Assert.Empty(DsnParser.Parse(""));
        Assert.Empty(DsnParser.Parse("Content-Type: multipart/report; report-type=delivery-status\r\n\r\nno boundary"));
    }

    [Fact]
    public void A_report_without_a_tag_is_parsed_but_carries_none_so_the_reader_rejects_it()
    {
        var parsed = Assert.Single(DsnParser.Parse(Dsn("Final-Recipient: rfc822; rita@corp.example\r\nAction: failed\r\nStatus: 5.1.1", tag: null)));
        Assert.Null(parsed.ReturnedTag);
        Assert.False(BounceTags.Matches(Secret, "rita@corp.example", parsed.ReturnedTag));
    }

    [Fact]
    public void The_tag_binds_a_bounce_to_the_recipient_and_to_the_secret()
    {
        var tag = BounceTags.Compute(Secret, "Rita@Corp.Example");
        Assert.Equal(32, tag.Length);
        Assert.True(BounceTags.Matches(Secret, "rita@corp.example", tag));
        Assert.True(BounceTags.Matches(Secret, "rita@corp.example", tag.ToUpperInvariant()));
        Assert.False(BounceTags.Matches(Secret, "bob@corp.example", tag)); // a tag seen on one mail cannot suppress another address
        Assert.False(BounceTags.Matches(Secret + "x", "rita@corp.example", tag));
        Assert.False(BounceTags.Matches(Secret, "rita@corp.example", null));
        Assert.False(BounceTags.Matches(Secret, "rita@corp.example", "short"));
    }
}

public sealed class EmailVerificationUnitTests
{
    [Fact]
    public void Sanitize_removes_addresses_control_characters_and_long_text()
    {
        Assert.Null(EmailBounceService.Sanitize(null));
        Assert.Null(EmailBounceService.Sanitize("   "));
        Assert.Equal("smtp; 550 5.1.1 [address] user unknown", EmailBounceService.Sanitize("smtp; 550 5.1.1 <rita@corp.example>: user unknown"));
        Assert.DoesNotContain("\n", EmailBounceService.Sanitize("a\r\nb")!, StringComparison.Ordinal);
        Assert.Equal(200, EmailBounceService.Sanitize(new string('x', 500))!.Length);
    }

    [Fact]
    public void The_verification_mail_is_a_link_and_constant_text_only()
    {
        var token = "tok-" + new string('a', 40);
        var body = VerificationMailContent.Body(new Uri("https://keel.corp.example/"), token);
        Assert.Contains("https://keel.corp.example/notifications/verify-email/" + token, body, StringComparison.Ordinal);
        Assert.All(body.Split("\r\n"), line => Assert.True(line.All(c => c < 128)));
        Assert.StartsWith("Confirm", VerificationMailContent.Subject, StringComparison.Ordinal);
        foreach (var secret in new[] { "ITAR", "CUI", "part", "document", "review" }) Assert.DoesNotContain(secret, body, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void The_audit_hash_identifies_an_address_without_being_one()
    {
        var hash = EmailVerificationService.AddressHash("rita@corp.example");
        Assert.Equal(12, hash.Length);
        Assert.DoesNotContain("rita", hash, StringComparison.Ordinal);
        Assert.Equal(hash, EmailVerificationService.AddressHash("rita@corp.example"));
        Assert.NotEqual(hash, EmailVerificationService.AddressHash("bob@corp.example"));
    }

    [Fact]
    public void Compose_adds_the_bounce_tag_header_only_when_there_is_one()
    {
        var plain = System.Text.Encoding.ASCII.GetString(SmtpSender.Compose(new OutgoingMail("keel@corp.example", "rita@corp.example", "s", "b")));
        Assert.DoesNotContain("X-Keel-Bounce-Tag", plain, StringComparison.Ordinal);
        var tagged = System.Text.Encoding.ASCII.GetString(SmtpSender.Compose(new OutgoingMail("keel@corp.example", "rita@corp.example", "s", "b", "abc123")));
        Assert.Contains("\r\nX-Keel-Bounce-Tag: abc123\r\n", tagged, StringComparison.Ordinal);
    }

    [Fact]
    public void Verification_and_bounce_options_are_validated_and_a_mailbox_needs_the_tag_secret()
    {
        Assert.Empty(new EmailVerificationOptions().Validate());
        Assert.NotEmpty(new EmailVerificationOptions { LinkMinutes = 1 }.Validate());
        Assert.NotEmpty(new EmailVerificationOptions { MaxPerAddressPerDay = 0 }.Validate());
        Assert.Empty(new EmailBounceOptions().Validate());
        Assert.NotEmpty(new EmailBounceOptions { HardBounceThreshold = 0 }.Validate());
        Assert.NotEmpty(new EmailBounceOptions { TagSecret = "short" }.Validate());
        var dir = Directory.CreateTempSubdirectory("keel-bounce-").FullName;
        try
        {
            Assert.Contains(new EmailBounceOptions { MailboxDirectory = dir }.Validate(), e => e.Contains("TagSecret", StringComparison.Ordinal));
            Assert.Empty(new EmailBounceOptions { MailboxDirectory = dir, TagSecret = new string('s', 40) }.Validate());
            Assert.Contains(new EmailBounceOptions { MailboxDirectory = dir + "-missing", TagSecret = new string('s', 40) }.Validate(), e => e.Contains("does not exist", StringComparison.Ordinal));
        }
        finally { Directory.Delete(dir); }
    }
}

public sealed class BrokerSinkConfigTests
{
    private static BrokerSink Sink(Action<IntegrationOptions>? configure = null)
    {
        var o = new IntegrationOptions { AllowedHosts = ["bus.site.example"], Secrets = { ["bus-user"] = "u", ["bus-pass"] = "p", ["bus-sign"] = "s" } };
        configure?.Invoke(o);
        return new BrokerSink(o, new SecretResolver(o), new SsrfGuard(o), TimeProvider.System);
    }

    private static JsonObject Cfg(string json) => JsonNode.Parse(json)!.AsObject();

    private const string Good = """{"host":"bus.site.example","exchange":"keel","usernameSecretName":"bus-user","passwordSecretName":"bus-pass"}""";

    [Fact]
    public void A_complete_configuration_is_accepted()
    {
        Assert.Empty(Sink().ValidateConfig(Cfg(Good), enabled: true));
        Assert.Empty(Sink().ValidateConfig(Cfg("""{"protocol":"amqp091","host":"bus.site.example","port":5671,"virtualHost":"/keel","exchange":"keel","routingKey":"erp.events","declareExchange":true,"usernameSecretName":"bus-user","passwordSecretName":"bus-pass","signingSecretName":"bus-sign","timeoutSeconds":15}"""), enabled: true));
    }

    [Fact]
    public void Other_protocols_are_named_as_not_available_and_unknown_settings_are_refused()
    {
        Assert.Contains(Sink().ValidateConfig(Cfg(Good.Replace("{", "{\"protocol\":\"kafka\",", StringComparison.Ordinal)), true), e => e.Contains("not available", StringComparison.Ordinal));
        Assert.Contains(Sink().ValidateConfig(Cfg(Good.Replace("{", "{\"protocol\":\"amqp10\",", StringComparison.Ordinal)), true), e => e.Contains("not available", StringComparison.Ordinal));
        Assert.Contains(Sink().ValidateConfig(Cfg(Good.Replace("{", "{\"protocol\":\"mqtt\",", StringComparison.Ordinal)), true), e => e.Contains("Unknown protocol", StringComparison.Ordinal));
        Assert.Contains(Sink().ValidateConfig(Cfg(Good.Replace("{", "{\"password\":\"x\",", StringComparison.Ordinal)), true), e => e.Contains("Unknown setting 'password'", StringComparison.Ordinal));
    }

    [Fact]
    public void The_host_must_pass_the_allow_list_like_a_webhook_host()
    {
        Assert.Contains(Sink().ValidateConfig(Cfg(Good.Replace("bus.site.example", "evil.example", StringComparison.Ordinal)), true), e => e.Contains("evil.example", StringComparison.Ordinal));
        Assert.Contains(Sink().ValidateConfig(Cfg(Good.Replace("bus.site.example", "127.0.0.1", StringComparison.Ordinal)), true), e => e.Contains("loopback", StringComparison.Ordinal));
        Assert.Contains(Sink().ValidateConfig(Cfg(Good.Replace("bus.site.example", "169.254.169.254", StringComparison.Ordinal)), true), e => e.Contains("metadata", StringComparison.Ordinal));
        Assert.DoesNotContain(Sink(o => o.AllowDangerousAddresses = true).ValidateConfig(Cfg(Good.Replace("bus.site.example", "127.0.0.1", StringComparison.Ordinal)), true), e => e.Contains("loopback", StringComparison.Ordinal));
        Assert.NotEmpty(Sink().ValidateConfig(Cfg(Good.Replace("bus.site.example", "bus site", StringComparison.Ordinal)), true));
    }

    [Fact]
    public void Plain_amqp_needs_an_explicit_acknowledgement_and_authentication_is_never_defaulted()
    {
        var plain = Good.Replace("{", "{\"tls\":\"none\",", StringComparison.Ordinal);
        Assert.Contains(Sink().ValidateConfig(Cfg(plain), true), e => e.Contains("clear text", StringComparison.Ordinal));
        Assert.Empty(Sink().ValidateConfig(Cfg(plain.Replace("{", "{\"acknowledgeInsecureTransport\":true,", StringComparison.Ordinal)), true));
        Assert.Contains(Sink().ValidateConfig(Cfg(Good.Replace("{", "{\"tls\":\"maybe\",", StringComparison.Ordinal)), true), e => e.Contains("tls must be", StringComparison.Ordinal));
        Assert.Contains(Sink().ValidateConfig(Cfg("""{"host":"bus.site.example","exchange":"keel"}"""), true), e => e.Contains("Authentication is required", StringComparison.Ordinal));
        Assert.Contains(Sink().ValidateConfig(Cfg("""{"host":"bus.site.example","exchange":"keel","usernameSecretName":"bus-user"}"""), true), e => e.Contains("passwordSecretName", StringComparison.Ordinal));
    }

    [Fact]
    public void Secrets_are_referenced_by_name_and_must_exist_only_when_the_sink_is_enabled()
    {
        var missing = Good.Replace("bus-pass", "nope", StringComparison.Ordinal);
        Assert.Contains(Sink().ValidateConfig(Cfg(missing), true), e => e.Contains("'nope' is not provisioned", StringComparison.Ordinal));
        Assert.Empty(Sink().ValidateConfig(Cfg(missing), false));
        Assert.Contains(Sink().ValidateConfig(Cfg(Good.Replace("bus-pass", "9bad name", StringComparison.Ordinal)), false), e => e.Contains("not a valid secret name", StringComparison.Ordinal));
        Assert.Contains(Sink().ValidateConfig(Cfg(Good.Replace("{", "{\"caBundleName\":\"ca\",", StringComparison.Ordinal)), true), e => e.Contains("CA bundle", StringComparison.Ordinal));
        Assert.Contains(Sink().ValidateConfig(Cfg("""{"host":"bus.site.example","exchange":"keel","clientCertName":"cert"}"""), true), e => e.Contains("Client certificate", StringComparison.Ordinal));
    }

    [Fact]
    public void Exchange_routing_key_port_and_timeout_are_checked()
    {
        Assert.Contains(Sink().ValidateConfig(Cfg("""{"host":"bus.site.example","usernameSecretName":"bus-user","passwordSecretName":"bus-pass"}"""), true), e => e.Contains("routingKey", StringComparison.Ordinal));
        Assert.Empty(Sink().ValidateConfig(Cfg("""{"host":"bus.site.example","routingKey":"keel-events","usernameSecretName":"bus-user","passwordSecretName":"bus-pass"}"""), true)); // default exchange, a queue name
        Assert.NotEmpty(Sink().ValidateConfig(Cfg(Good.Replace("\"keel\"", "\"bad exchange!\"", StringComparison.Ordinal)), true));
        Assert.NotEmpty(Sink().ValidateConfig(Cfg(Good.Replace("{", "{\"routingKey\":\"a.*\",", StringComparison.Ordinal)), true));
        Assert.NotEmpty(Sink().ValidateConfig(Cfg(Good.Replace("{", "{\"port\":70000,", StringComparison.Ordinal)), true));
        Assert.NotEmpty(Sink().ValidateConfig(Cfg(Good.Replace("{", "{\"timeoutSeconds\":0,", StringComparison.Ordinal)), true));
        Assert.Contains(Sink().ValidateConfig(Cfg("""{"host":"bus.site.example","routingKey":"q","declareExchange":true,"usernameSecretName":"bus-user","passwordSecretName":"bus-pass"}"""), true), e => e.Contains("declareExchange", StringComparison.Ordinal));
    }

    [Fact]
    public async Task A_sink_without_a_host_is_a_permanent_failure_and_a_missing_secret_is_retried()
    {
        var sink = Sink();
        var message = new EnvelopeMessage(Guid.NewGuid(), "part.released", 1, DateTimeOffset.UtcNow, [1, 2, 3], 1);
        SinkRecord Record(JsonObject c) => new(Guid.NewGuid(), "bus", "Bus", "broker", true, Guid.NewGuid(), "erp", c, [], false, 1);
        Assert.Equal(DeliveryResult.Permanent, (await sink.DeliverAsync(Record(new JsonObject()), message, CancellationToken.None)).Result);
        var noSecret = await sink.DeliverAsync(Record(Cfg(Good.Replace("bus-pass", "gone", StringComparison.Ordinal))), message, CancellationToken.None);
        Assert.Equal(DeliveryResult.Retry, noSecret.Result);
        Assert.Contains("'gone' is not provisioned", noSecret.Error, StringComparison.Ordinal);
    }

    [Fact]
    public void The_registry_serves_the_broker_kind_and_it_is_a_real_sink()
    {
        var registry = new SinkRegistry([Sink()]);
        Assert.IsType<BrokerSink>(registry.For(SinkKinds.Broker));
    }
}
