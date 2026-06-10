-- ============================================================================
-- SPIKE (R-02) — NOT run automatically. Validate Oracle 23ai VECTOR DML +
-- Oracle Text BEFORE Phase 4 builds the real persistence/vector adapter.
--
-- Run manually as the application user once the container is healthy:
--   docker compose exec oracle bash -c \
--     "sqlplus llmwiki/Wiki_Dev_0@localhost:1521/FREEPDB1 @/opt/oracle/scripts/startup/spike-vector.sql"
--
-- The same DML must also be exercised through ODP.NET (Oracle.ManagedDataAccess.Core)
-- to confirm the .NET driver round-trips the VECTOR type — see the Phase 4 plan.
-- ============================================================================

SET SERVEROUTPUT ON;

-- ---- 1. VECTOR datatype: create, insert, similarity query --------------------
CREATE TABLE spike_vec (
  id   NUMBER GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
  body VARCHAR2(400),
  emb  VECTOR(768, FLOAT32)          -- matches nomic-embed-text dimensionality
);

INSERT INTO spike_vec (body, emb)
  VALUES ('hello world', TO_VECTOR('[' || RPAD('0.1,', 768*4, '0.1,') || '0.1]'));

-- Nearest-neighbour by cosine distance (the Phase 4 retrieval primitive).
SELECT id, body,
       VECTOR_DISTANCE(emb, TO_VECTOR('[' || RPAD('0.1,', 768*4, '0.1,') || '0.1]'), COSINE) AS dist
  FROM spike_vec
 ORDER BY dist
 FETCH FIRST 1 ROWS ONLY;

-- ---- 2. Oracle Text: full-text index + CONTAINS query -----------------------
CREATE TABLE spike_text (
  id   NUMBER GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
  body CLOB
);
INSERT INTO spike_text (body) VALUES ('the quick brown fox jumps over the lazy dog');

CREATE INDEX spike_text_idx ON spike_text (body) INDEXTYPE IS CTXSYS.CONTEXT;

SELECT id, SCORE(1) AS rank
  FROM spike_text
 WHERE CONTAINS(body, 'fox', 1) > 0;

-- ---- cleanup ----------------------------------------------------------------
DROP INDEX spike_text_idx;
DROP TABLE spike_text PURGE;
DROP TABLE spike_vec PURGE;

PROMPT *** spike-vector.sql completed: VECTOR DML + Oracle Text both validated. ***
EXIT;
