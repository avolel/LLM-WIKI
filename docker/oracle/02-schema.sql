-- ============================================================================
-- Phase 4 — application schema: hybrid retrieval (VECTOR + Oracle Text).
--
-- Canonical DDL for the `wiki_page` table the OracleVectorStore reads/writes. Init
-- scripts run only on FIRST container creation and NOT on an already-provisioned
-- container, so the adapter also ensures this schema idempotently at runtime
-- (OracleVectorStore.EnsureSchemaAsync). This file is the reproducible source of
-- truth (NFR-04) and can be applied by hand:
--
--   docker compose exec oracle bash -c \
--     "sqlplus llmwiki/Wiki_Dev_0@localhost:1521/FREEPDB1 @/opt/oracle/scripts/startup/02-schema.sql"
--
-- Requires the CTXSYS grant already given in 01-init.sql.
-- ============================================================================

ALTER SESSION SET CONTAINER = FREEPDB1;

-- One row per page, partitioned logically by wiki_name (NFR-10: searches never cross wikis).
CREATE TABLE wiki_page (
  wiki_name  VARCHAR2(128)      NOT NULL,
  path       VARCHAR2(400)      NOT NULL,
  title      VARCHAR2(400),
  type       VARCHAR2(20),
  tags       VARCHAR2(2000),
  snippet    VARCHAR2(2000),
  content    CLOB,
  emb        VECTOR(768, FLOAT32),   -- matches nomic-embed-text (EMBEDDING_DIM)
  updated_at TIMESTAMP,
  CONSTRAINT wiki_page_pk PRIMARY KEY (wiki_name, path)
);

-- Lexical arm: Oracle Text full-text index over the body (CONTAINS / SCORE).
CREATE INDEX wiki_page_txt ON wiki_page (content) INDEXTYPE IS CTXSYS.CONTEXT;

-- Semantic arm uses an exact cosine scan (VECTOR_DISTANCE), correct and fast at the
-- 50–100-doc / hundreds-of-pages target (NFR-08). At larger scale (R-08) add an index:
--
-- CREATE VECTOR INDEX wiki_page_vec ON wiki_page (emb) ORGANIZATION INMEMORY NEIGHBOR GRAPH
--   DISTANCE COSINE WITH TARGET ACCURACY 95;

COMMIT;
EXIT;