-- Bootstraps database roles. Runs once as the PostgreSQL superuser when the data volume is created.
-- Dev-only passwords; production passwords come from secrets (docs/setup.md).
CREATE ROLE keel_migrator LOGIN PASSWORD 'keel_migrator_dev';
CREATE ROLE keel_app      LOGIN PASSWORD 'keel_app_dev';
CREATE DATABASE keel OWNER keel_migrator;
REVOKE ALL ON DATABASE keel FROM PUBLIC;
GRANT CONNECT ON DATABASE keel TO keel_app;
\connect keel
REVOKE CREATE ON SCHEMA public FROM PUBLIC;
ALTER SCHEMA public OWNER TO keel_migrator;
