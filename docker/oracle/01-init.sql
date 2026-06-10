-- Phase 0 — minimal application schema bootstrap.
-- Runs once, on first DB creation, against the FREEPDB1 pluggable database.
-- Goal: an application user/schema with enough privileges that the Phase 0
-- acceptance `CREATE TABLE` round-trip (see /diagnostics + CLI `doctor`) succeeds.
--
-- Full schema, VECTOR columns and Oracle Text indexes are Phase 4 (see spike-vector.sql).

ALTER SESSION SET CONTAINER = FREEPDB1;

-- Application user. Password is intentionally a placeholder for LOCAL DEV ONLY;
-- it is overridden per-environment via the app connection string (env/.env).
CREATE USER llmwiki IDENTIFIED BY "Wiki_Dev_0"
  DEFAULT TABLESPACE users
  TEMPORARY TABLESPACE temp
  QUOTA UNLIMITED ON users;

GRANT CONNECT, RESOURCE TO llmwiki;
GRANT CREATE SESSION, CREATE TABLE, CREATE VIEW, CREATE SEQUENCE, CREATE PROCEDURE TO llmwiki;

-- Oracle Text + vector work (Phase 4) needs CTXSYS access; grant now so the
-- spike script can run without re-provisioning.
GRANT EXECUTE ON CTXSYS.CTX_DDL TO llmwiki;

COMMIT;

EXIT;
