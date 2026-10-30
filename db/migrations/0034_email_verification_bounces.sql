-- Increment 9.6: e-mail address verification and bounce handling for the notification mail channel (ADR-0055, docs/increments/9.6-email-verification-broker.md).
-- Forward-only, runs as keel_migrator. The message-broker sink (same increment) needs no schema change: it reuses integration.sink / integration.delivery.
--   * ntf.preference gains the address the user has proven to own (email_verified_address) and when. Mail goes only to an address that equals the verified one and is
--     not suppressed. Existing opted-in users therefore stop receiving mail until they confirm their address once (documented in the release notes).
--   * ntf.email_verification: one row per verification link sent. Only the SHA-256 of the one-time token is stored, never the token. A row is usable once, until it
--     expires, for the user and the address it was made for. Rows also drive the rate limits, so they are kept for a few days (the dispatcher purges them).
--   * ntf.email_address_health: per address (lower case), the count of hard bounces and whether the address is suppressed. Suppression stops all mail to it.
--   * ntf.email_bounce_event: a bounded log of the reports that matched an address of this system, for the administrator's page. Diagnostics never hold an address.
-- The addresses are personal data: only the administrator's page reads them, and the audit log never holds one (a short hash identifies it there).
ALTER TABLE ntf.preference
    ADD COLUMN email_verified_address text,
    ADD COLUMN email_verified_at      timestamptz;
ALTER TABLE ntf.preference ADD CONSTRAINT preference_verified_pair CHECK ((email_verified_address IS NULL) = (email_verified_at IS NULL));
ALTER TABLE ntf.preference ADD CONSTRAINT preference_verified_format CHECK (email_verified_address IS NULL OR length(email_verified_address) BETWEEN 6 AND 254);

CREATE TABLE ntf.email_verification (
    id             uuid        PRIMARY KEY,
    user_sub       text        NOT NULL CHECK (length(user_sub) BETWEEN 1 AND 200),
    address        text        NOT NULL CHECK (length(address) BETWEEN 6 AND 254),
    token_hash     text        NOT NULL UNIQUE CHECK (token_hash ~ '^[0-9a-f]{64}$'),
    created_at     timestamptz NOT NULL,
    expires_at     timestamptz NOT NULL,
    consumed_at    timestamptz,
    invalidated_at timestamptz,
    CONSTRAINT email_verification_expiry CHECK (expires_at > created_at)
);
CREATE INDEX email_verification_user ON ntf.email_verification (user_sub, created_at DESC);
CREATE INDEX email_verification_address ON ntf.email_verification (lower(address), created_at DESC);
GRANT SELECT, INSERT, UPDATE, DELETE ON ntf.email_verification TO keel_app;

CREATE TABLE ntf.email_address_health (
    address          text        PRIMARY KEY CHECK (address = lower(address) AND length(address) BETWEEN 6 AND 254),
    hard_bounces     int         NOT NULL DEFAULT 0 CHECK (hard_bounces >= 0),
    last_event_at    timestamptz NOT NULL,
    suppressed_at    timestamptz,
    suppress_reason  text        CHECK (suppress_reason IS NULL OR suppress_reason IN ('hard_bounce', 'complaint')),
    CONSTRAINT email_health_reason CHECK ((suppressed_at IS NULL) = (suppress_reason IS NULL))
);
CREATE INDEX email_health_suppressed ON ntf.email_address_health (suppressed_at DESC) WHERE suppressed_at IS NOT NULL;
GRANT SELECT, INSERT, UPDATE, DELETE ON ntf.email_address_health TO keel_app;

CREATE TABLE ntf.email_bounce_event (
    id          uuid        PRIMARY KEY,
    address     text        NOT NULL CHECK (address = lower(address) AND length(address) BETWEEN 6 AND 254),
    kind        text        NOT NULL CHECK (kind IN ('hard', 'soft', 'complaint')),
    source      text        NOT NULL CHECK (source IN ('api', 'mailbox')),
    status_code text        CHECK (status_code IS NULL OR status_code ~ '^[245]\.[0-9]{1,3}\.[0-9]{1,3}$'),
    diagnostic  text        CHECK (diagnostic IS NULL OR (length(diagnostic) <= 200 AND diagnostic !~ '@')),
    received_at timestamptz NOT NULL
);
CREATE INDEX email_bounce_event_recent ON ntf.email_bounce_event (received_at DESC);
CREATE INDEX email_bounce_event_address ON ntf.email_bounce_event (address, received_at DESC);
GRANT SELECT, INSERT, UPDATE, DELETE ON ntf.email_bounce_event TO keel_app;
