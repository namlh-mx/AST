-- V005 — role_permission (header + version): role x function x scope_level
-- Source: docs/design-iam-schema.md §1.5 (Model 2: one write identity per grant; revoke closes that identity).
-- Depends on: role (V002), function (V003) — must run after those scripts.

CREATE TABLE `role_permission` (
  id BIGINT UNSIGNED NOT NULL AUTO_INCREMENT,
  PRIMARY KEY (id)
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_0900_ai_ci;

CREATE TABLE `role_permission_version` (
  id                 BIGINT UNSIGNED NOT NULL AUTO_INCREMENT,
  role_permission_id BIGINT UNSIGNED NOT NULL,   -- FK -> role_permission(id) (IDENTITY)
  role_id            BIGINT UNSIGNED NOT NULL,    -- FK -> role(id) (IDENTITY)     — temporal-FK parent
  function_id        BIGINT UNSIGNED NOT NULL,    -- FK -> function(id) (IDENTITY) — temporal-FK parent
  scope_level        TINYINT UNSIGNED NOT NULL,   -- 1..4 = ScopeLevel
  cancelled          TINYINT(1) NOT NULL DEFAULT 0,
  operation_kind     VARCHAR(10) NULL,
  effective_from     DATE NOT NULL,
  effective_to       DATE NOT NULL DEFAULT '9999-12-31',
  isactive           TINYINT(1) NOT NULL DEFAULT 1,
  recorded_at        DATETIME NOT NULL DEFAULT CURRENT_TIMESTAMP,
  recorded_by        VARCHAR(100) NOT NULL,
  reason             VARCHAR(255) NULL,
  PRIMARY KEY (id),
  KEY idx_rpv_res  (role_permission_id, isactive, effective_from, effective_to),
  KEY idx_rpv_rf   (role_id, function_id, isactive, effective_from, effective_to),  -- lookup (role,function)
  KEY idx_rpv_role (role_id, isactive, effective_from, effective_to),
  KEY idx_rpv_func (function_id, isactive, effective_from, effective_to),
  CONSTRAINT fk_rpv_rp   FOREIGN KEY (role_permission_id) REFERENCES `role_permission`(id),
  CONSTRAINT fk_rpv_role FOREIGN KEY (role_id)     REFERENCES `role`(id),
  CONSTRAINT fk_rpv_func FOREIGN KEY (function_id) REFERENCES `function`(id),
  CONSTRAINT chk_rpv_period CHECK (effective_from <= effective_to),
  CONSTRAINT chk_rpv_scope  CHECK (scope_level BETWEEN 1 AND 4)
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_0900_ai_ci;
