-- Localization foundation (ADR 0057): the per-user interface language. Display only, like theme and density: it changes which translation of the screen text
-- the person reads, never what they may see, never a marking, a banner or an audit entry. No object data is stored here.
--  * ws.preference.language: a lowercase BCP 47 style tag ("en", "es", "qps-ploc"). The CHECK bounds the shape of the value only. Which tags are shipped is decided
--    by the application (an unknown or retired tag falls back to English at render time), so adding a language never needs a migration.
-- Forward-only. Existing rows and new rows default to English, which is the language the product shipped in.
ALTER TABLE ws.preference
    ADD COLUMN language text NOT NULL DEFAULT 'en' CHECK (language ~ '^[a-z]{2,3}(-[a-z0-9]{2,8})?$');
