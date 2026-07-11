-- ============================================================================
-- Phase 6 — application schema: the project registry.
--
-- Canonical DDL for the `wiki_project` table the OracleProjectRepository reads/writes.
-- Init scripts run only on FIRST container creation, so the adapter also ensures this
-- schema idempotently at runtime (OracleProjectRepository.EnsureSchemaAsync). This file
-- is the reproducible source of truth (NFR-04) and can be applied by hand:
--
--   docker compose exec oracle bash -c \
--     "sqlplus llmwiki/Wiki_Dev_0@localhost:1521/FREEPDB1 @/opt/oracle/scripts/startup/03-schema.sql"
-- ============================================================================

ALTER SESSION SET CONTAINER = FREEPDB1;

-- One row per project (== wiki). Metadata only; page rows live in wiki_page (BR-052).
CREATE TABLE wiki_project (
  name           VARCHAR2(128) NOT NULL,
  created_at     TIMESTAMP,
  last_ingest_at TIMESTAMP,
  page_count     NUMBER DEFAULT 0,
  source_count   NUMBER DEFAULT 0,
  CONSTRAINT wiki_project_pk PRIMARY KEY (name)
);

COMMIT;
EXIT;
