#!/bin/bash
# Creates the role the API connects as.
#
# This is a bootstrap step and NOT a migration, for one reason: creating a login
# role requires a password, and a password does not belong in committed SQL. The
# migration that follows does the grants and ALTER DEFAULT PRIVILEGES, which
# carry no secret and do need to be versioned with the schema.
#
# Postgres only runs /docker-entrypoint-initdb.d against an EMPTY data directory.
# Editing this file therefore has no effect on an existing volume — you need
# `docker compose down -v` (which destroys the local database) for it to re-run.
set -euo pipefail

psql -v ON_ERROR_STOP=1 \
     -v app_password="${POSTGRES_APP_PASSWORD}" \
     -v db_name="${POSTGRES_DB}" \
     --username "${POSTGRES_USER}" \
     --dbname "${POSTGRES_DB}" <<-'EOSQL'

    -- NOBYPASSRLS is the whole point of this role and is stated rather than
    -- relied upon as the default. The owner role (POSTGRES_USER) is a superuser
    -- and bypasses row-level security unconditionally, so if the API connected
    -- as the owner every policy in the next migration would be in place and
    -- enforcing nothing. FORCE ROW LEVEL SECURITY covers the non-superuser
    -- owner case; this covers the superuser one.
    CREATE ROLE pos_app WITH LOGIN NOBYPASSRLS PASSWORD :'app_password';

    GRANT CONNECT ON DATABASE :"db_name" TO pos_app;
    GRANT USAGE ON SCHEMA public TO pos_app;

    -- Deliberately no CREATE on the schema. pos_app runs the application; it
    -- does not run migrations, and it must not be able to create a table that
    -- would then exist with no RLS policy on it.
    REVOKE CREATE ON SCHEMA public FROM pos_app;
EOSQL
