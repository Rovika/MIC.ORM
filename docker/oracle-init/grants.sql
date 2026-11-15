-- Run once by the container at database creation, as SYSDBA in the CDB root (gvenzl's /container-entrypoint-initdb.d).
--
-- ORACLE TEXT FOR THE SUITE. The suite connects as SYSTEM, which the DBA role does NOT let into Oracle Text:
-- `CTXAPP` is what runs `CTX_DDL` (the datastore preference a multi-column full-text index needs), and the loader
-- reads a datastore's columns back from `CTXSYS.CTX_INDEX_VALUES`, which no role grants. A deployment makes the same
-- two grants to its application user — see OracleSqlDialect, the full-text section.
ALTER SESSION SET CONTAINER = FREEPDB1;
GRANT CTXAPP TO SYSTEM;
GRANT SELECT ON CTXSYS.CTX_INDEX_VALUES TO SYSTEM;
