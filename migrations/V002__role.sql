-- V002 — role (header + version)
-- Source: docs/design-iam-schema.md §1.2.

CREATE TABLE `role` (
  id BIGINT UNSIGNED NOT NULL AUTO_INCREMENT,
  PRIMARY KEY (id)
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_0900_ai_ci;

CREATE TABLE `role_version` (
  id             BIGINT UNSIGNED NOT NULL AUTO_INCREMENT,
  role_id        BIGINT UNSIGNED NOT NULL,       -- FK -> role(id) (IDENTITY)
  role_code      VARCHAR(20)  NOT NULL,          -- natural key (P6)
  role_name      VARCHAR(100) NOT NULL,
  is_admin_role  TINYINT(1) NOT NULL DEFAULT 0,  -- version-level; <=1 admin-flag role active per day (N-14)
  cancelled      TINYINT(1) NOT NULL DEFAULT 0,  -- future-plan cancellation marker (N6)
  operation_kind VARCHAR(10) NULL,               -- Add/Edit/Close/Cancel (Phase 4d history)
  effective_from DATE NOT NULL,
  effective_to   DATE NOT NULL DEFAULT '9999-12-31',
  isactive       TINYINT(1) NOT NULL DEFAULT 1,
  recorded_at    DATETIME NOT NULL DEFAULT CURRENT_TIMESTAMP,
  recorded_by    VARCHAR(100) NOT NULL,
  reason         VARCHAR(255) NULL,              -- optional "Ghi chú"; deliberately nullable (Screen A parity)
  PRIMARY KEY (id),
  KEY idx_rv_res   (role_id, isactive, effective_from, effective_to),
  KEY idx_rv_code  (role_code, isactive, effective_from, effective_to),
  KEY idx_rv_admin (is_admin_role, isactive, effective_from, effective_to),
  CONSTRAINT fk_rv_role     FOREIGN KEY (role_id) REFERENCES `role`(id),
  CONSTRAINT chk_rv_period  CHECK (effective_from <= effective_to)
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_0900_ai_ci;
