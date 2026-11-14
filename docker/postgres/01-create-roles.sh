#!/bin/sh
set -eu

# Executed by the official image on an empty data volume only. Use psql variables
# and quoted SQL literals so credentials never become executable SQL.
psql --username "$POSTGRES_USER" --dbname "$POSTGRES_DB" --set=ON_ERROR_STOP=1 \
    --set=database="$POSTGRES_DB" \
    --set=migration_password="$MIGRATION_PASSWORD" \
    --set=app_password="$APP_PASSWORD" <<'SQL'
CREATE ROLE exactledger_migrations LOGIN NOSUPERUSER NOCREATEDB NOCREATEROLE PASSWORD :'migration_password';
CREATE ROLE exactledger_runtime NOLOGIN;
CREATE ROLE exactledger_app LOGIN NOSUPERUSER NOCREATEDB NOCREATEROLE PASSWORD :'app_password' IN ROLE exactledger_runtime;

REVOKE CONNECT ON DATABASE :"database" FROM PUBLIC;
GRANT CONNECT, CREATE ON DATABASE :"database" TO exactledger_migrations;
GRANT CONNECT ON DATABASE :"database" TO exactledger_app;
REVOKE CREATE ON SCHEMA public FROM PUBLIC;
SQL
