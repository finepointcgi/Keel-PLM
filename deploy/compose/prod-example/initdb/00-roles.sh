#!/bin/sh
# Creates the Keel database and roles on first start of an empty data directory (docker-entrypoint-initdb.d).
# Same least-privilege layout as deploy/compose/initdb/00-roles.sql, but passwords come from secret files.
set -eu
migrator_pw=$(cat /run/secrets/pg_migrator_password)
app_pw=$(cat /run/secrets/pg_app_password)
psql -v ON_ERROR_STOP=1 --username postgres --dbname postgres \
  -v migrator_pw="$migrator_pw" -v app_pw="$app_pw" <<'SQL'
CREATE ROLE keel_migrator LOGIN PASSWORD :'migrator_pw';
CREATE ROLE keel_app      LOGIN PASSWORD :'app_pw';
CREATE DATABASE keel OWNER keel_migrator;
REVOKE ALL ON DATABASE keel FROM PUBLIC;
GRANT CONNECT ON DATABASE keel TO keel_app;
\connect keel
REVOKE CREATE ON SCHEMA public FROM PUBLIC;
ALTER SCHEMA public OWNER TO keel_migrator;
SQL

# TLS only: remote clients that do not use TLS are rejected by pg_hba.conf (the local socket stays for pg_isready and admin use).
sed -i 's/^host all all all /hostssl all all all /' "$PGDATA/pg_hba.conf"
grep -q '^hostssl all all all' "$PGDATA/pg_hba.conf"
