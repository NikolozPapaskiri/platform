-- LOCAL DEVELOPMENT AND TESTS ONLY. Runs once, when the database is first created (the docker
-- compose volume, or each Testcontainers database). The passwords are throwaway local values.
--
-- Two roles, per ADR 0003:
--   platform_owner  the POSTGRES_USER. Owns the schema and tables; migrations run as this role.
--   platform_app    what the application connects as. Owns nothing, is not a superuser, and cannot
--                   bypass row-level security. It can only read and write rows it is allowed to.

CREATE ROLE platform_app LOGIN PASSWORD 'platform_app'
    NOSUPERUSER NOCREATEDB NOCREATEROLE NOREPLICATION NOBYPASSRLS;

DO $$
BEGIN
    EXECUTE format('GRANT CONNECT ON DATABASE %I TO platform_app', current_database());
END
$$;

GRANT USAGE ON SCHEMA public TO platform_app;

-- Tables and sequences created later by the owner (that is, by migrations) are usable by the
-- application role automatically; no migration has to remember a GRANT for a new tenant-owned table.
-- The exceptions (the tenant catalog and the migrations history, read-only for the application) are
-- revoked explicitly in the migration that creates them.
ALTER DEFAULT PRIVILEGES FOR ROLE platform_owner IN SCHEMA public
    GRANT SELECT, INSERT, UPDATE, DELETE ON TABLES TO platform_app;
ALTER DEFAULT PRIVILEGES FOR ROLE platform_owner IN SCHEMA public
    GRANT USAGE, SELECT ON SEQUENCES TO platform_app;

-- Nobody but the owner needs temporary tables.
DO $$
BEGIN
    EXECUTE format('REVOKE TEMPORARY ON DATABASE %I FROM PUBLIC', current_database());
END
$$;
