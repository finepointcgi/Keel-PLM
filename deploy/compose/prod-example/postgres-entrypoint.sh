#!/bin/sh
# PostgreSQL requires the TLS key to be owned by the server user (or root) with mode 0600/0640. Secrets arrive as read-only
# bind mounts, so copy the key to a private directory first, then hand over to the stock entrypoint with TLS enforced.
set -eu
install -d -m 0700 -o postgres -g postgres /var/lib/postgresql/tls
install -m 0600 -o postgres -g postgres /run/certs/postgres.key /var/lib/postgresql/tls/server.key
install -m 0644 -o postgres -g postgres /run/certs/postgres.crt /var/lib/postgresql/tls/server.crt
exec docker-entrypoint.sh postgres \
  -c ssl=on \
  -c ssl_cert_file=/var/lib/postgresql/tls/server.crt \
  -c ssl_key_file=/var/lib/postgresql/tls/server.key \
  -c ssl_min_protocol_version=TLSv1.2 \
  -c password_encryption=scram-sha-256 \
  -c max_connections=200 \
  -c log_connections=on -c log_disconnections=on \
  -c log_statement=ddl
