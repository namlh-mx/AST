-- V010 -- one durable lifecycle column per version row, replacing the `cancelled` boolean.
--
-- WHY one column: a `cancelled` boolean sitting beside a status enum that also has a `cancelled` value
-- admits rows that are both and rows that are neither, with no rule saying which wins (design spec
-- §14.3, AI Agent 141/F-80).
--
-- WHY `replaced` is permitted on org_unit_version only: replacement is scoped to org units in v1
-- (design spec §14.1, requester-ruled twice). A role_version row can never legally be `replaced`, so the
-- constraint says so rather than leaving it representable-but-unwritten.
--
-- ORDERING -- read this before editing anything below (AI Agent AST-CONSULT-144/F-01).
-- MySQL implicitly COMMITs each DDL statement; a .sql file is NOT one transaction. So the order of
-- statements IS the recovery story, and this file is arranged in four phases so that an abort in any of
-- the first three leaves `cancelled` intact on ALL THREE tables and nothing lost:
--   phase 1  legacy-domain gate  -- a CHECK on the OLD column; aborts if any value is outside {0,1}
--   phase 2  add the new columns -- additive only, nothing destroyed
--   phase 3  backfill + the real CHECKs -- MySQL validates existing rows when a CHECK is added, so
--            THIS is the abort gate for the invariant itself
--   phase 4  destroy -- and only here. Every DROP is after every gate has passed.
-- ⚠ An earlier draft interleaved the DROPs per table. If the third table's CHECK had failed, the first
-- two would already have dropped `cancelled` -- and this file's own header claimed "nothing is lost",
-- which was true only for the first table. Do not re-interleave them.
-- ⚠ Re-running after an abort is NOT automatic: phase 2's columns and phase 1/3's constraints survive.
-- Recovery is manual and deterministic -- drop what phase 1-3 added, fix the offending rows, re-run.
-- That is deliberate: guessing which half ran is worse than an explicit repair. Idempotent re-runs
-- are a separate, unscheduled piece of work.
--
-- WHY NOT procedural SQL: a validate-then-abort routine needs DELIMITER, which the client sends
-- verbatim and the server does not understand. Adding a CHECK is the same abort mechanism with none of
-- that -- which is why phase 1 uses a throwaway constraint rather than a SELECT the DBA might skip.
--
-- ⚠ DESTRUCTIVE (phase 4 only): drops `cancelled` from three tables. Every workstation must be shut
-- down before this runs (requester, 2026-08-23, design spec §15.6). `App.ExpectedSchemaVersion` is
-- bumped to 10 in the same commit, so a stale binary that starts anyway is blocked at startup rather
-- than reading a column that is gone.

-- ================================================================ phase 1: legacy-domain gate
-- `cancelled` is TINYINT(1), which in MySQL is TINYINT with a display width -- it holds -128..127 and
-- never had a domain CHECK. The backfill below keys on `= 1`, so a stray 2 would silently land as
-- 'normal' and then lose its meaning forever when the column is dropped (AI Agent AST-CONSULT-144/F-03).
-- These constraints abort the migration before anything is added or destroyed. They are dropped in
-- phase 4 -- MySQL refuses to drop a column a CHECK still references.
ALTER TABLE `org_unit_version`
  ADD CONSTRAINT `chk_ouv_legacy_cancelled_domain` CHECK (`cancelled` IN (0, 1));
ALTER TABLE `role_version`
  ADD CONSTRAINT `chk_rv_legacy_cancelled_domain` CHECK (`cancelled` IN (0, 1));
ALTER TABLE `role_permission_version`
  ADD CONSTRAINT `chk_rpv_legacy_cancelled_domain` CHECK (`cancelled` IN (0, 1));

-- ================================================================ phase 2: add, destroy nothing
-- COLLATE utf8mb4_0900_as_cs is load-bearing, not tidiness (AI Agent AST-CONSULT-144/F-02). The tables
-- default to utf8mb4_0900_ai_ci -- accent-INsensitive, case-INsensitive -- under which `status =
-- 'cancelled'` also matches 'Cancelled' and 'cancelléd'. Those satisfy the CHECK and are then not
-- VersionLifecycleStatus names, so they pass the database and fail at materialization. `_as_cs` makes
-- the comparison exact, so the CHECK enforces the domain it claims to enforce.
ALTER TABLE `org_unit_version`
  ADD COLUMN `status` VARCHAR(10) COLLATE utf8mb4_0900_as_cs NOT NULL DEFAULT 'normal' AFTER `cancelled`,
  ADD COLUMN `replaced_by_org_unit_id` BIGINT UNSIGNED NULL AFTER `status`;

ALTER TABLE `role_version`
  ADD COLUMN `status` VARCHAR(10) COLLATE utf8mb4_0900_as_cs NOT NULL DEFAULT 'normal' AFTER `cancelled`;

ALTER TABLE `role_permission_version`
  ADD COLUMN `status` VARCHAR(10) COLLATE utf8mb4_0900_as_cs NOT NULL DEFAULT 'normal' AFTER `cancelled`;

-- BIGINT UNSIGNED, matching org_unit.id (V001__org_unit.sql:6). A plain BIGINT here made MySQL refuse
-- the FK outright -- four design-review rounds passed over it and only running found it.
ALTER TABLE `org_unit_version`
  ADD CONSTRAINT `fk_ouv_replaced_by`
    FOREIGN KEY (`replaced_by_org_unit_id`) REFERENCES `org_unit`(`id`);

-- ================================================================ phase 3: backfill, then the gate
UPDATE `org_unit_version`        SET `status` = 'cancelled' WHERE `cancelled` = 1;
UPDATE `role_version`            SET `status` = 'cancelled' WHERE `cancelled` = 1;
UPDATE `role_permission_version` SET `status` = 'cancelled' WHERE `cancelled` = 1;

-- The whole lifecycle invariant, in one constraint per table: the status domain,
-- `cancelled|replaced => isactive=0`, and successor-link coherence in BOTH directions (a replaced row
-- has a link; nothing else does). Adding it validates every existing row, so this is the abort gate --
-- and it is the reason the invariant is the DATABASE's guarantee rather than a promise a write path
-- makes (design spec §15.2, AI Agent 142/F-85).
ALTER TABLE `org_unit_version`
  ADD CONSTRAINT `chk_ouv_status` CHECK (
       (`status` = 'normal'    AND `replaced_by_org_unit_id` IS NULL)
    OR (`status` = 'cancelled' AND `isactive` = 0 AND `replaced_by_org_unit_id` IS NULL)
    OR (`status` = 'replaced'  AND `isactive` = 0 AND `replaced_by_org_unit_id` IS NOT NULL)
  );

ALTER TABLE `role_version`
  ADD CONSTRAINT `chk_rv_status` CHECK (
       `status` = 'normal'
    OR (`status` = 'cancelled' AND `isactive` = 0)
  );

ALTER TABLE `role_permission_version`
  ADD CONSTRAINT `chk_rpv_status` CHECK (
       `status` = 'normal'
    OR (`status` = 'cancelled' AND `isactive` = 0)
  );

-- ================================================================ phase 4: destroy, gates all passed
ALTER TABLE `org_unit_version`        DROP CONSTRAINT `chk_ouv_legacy_cancelled_domain`;
ALTER TABLE `role_version`            DROP CONSTRAINT `chk_rv_legacy_cancelled_domain`;
ALTER TABLE `role_permission_version` DROP CONSTRAINT `chk_rpv_legacy_cancelled_domain`;

ALTER TABLE `org_unit_version`        DROP COLUMN `cancelled`;
ALTER TABLE `role_version`            DROP COLUMN `cancelled`;
ALTER TABLE `role_permission_version` DROP COLUMN `cancelled`;

INSERT INTO schema_version (version, applied_by, description) VALUES
  (10, 'baseline-deploy', 'V010__version_lifecycle_status');
