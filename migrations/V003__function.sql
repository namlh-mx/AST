-- V003 — function (header + version); epoch effective_from DEFAULT '2000-01-01' (C2)
-- Nguồn: docs/thiet-ke-iam-schema-chi-tiet.md §1.4. `function` là reserved word MySQL -> bắt buộc backtick.

CREATE TABLE `function` (
  id BIGINT UNSIGNED NOT NULL AUTO_INCREMENT,
  PRIMARY KEY (id)
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_0900_ai_ci;

CREATE TABLE `function_version` (
  id             BIGINT UNSIGNED NOT NULL AUTO_INCREMENT,
  function_id    BIGINT UNSIGNED NOT NULL,   -- FK -> function(id) (CĂN CƯỚC)
  function_key   VARCHAR(150) NOT NULL,      -- Module.Entity.Action — khóa kỹ thuật bền từ code
  business_code  VARCHAR(30)  NOT NULL,      -- vd FX002 (hiển thị)
  display_name   VARCHAR(255) NOT NULL,
  menu_group     VARCHAR(100) NOT NULL,      -- trỏ hằng MenuGroupCodes (SharedKernel)
  nav_target     VARCHAR(150) NOT NULL,      -- đích điều hướng (mở đúng màn hình)
  effective_from DATE NOT NULL DEFAULT '2000-01-01',  -- C2: epoch cố định
  effective_to   DATE NOT NULL DEFAULT '9999-12-31',
  isactive       TINYINT(1) NOT NULL DEFAULT 1,
  recorded_at    DATETIME NOT NULL DEFAULT CURRENT_TIMESTAMP,
  recorded_by    VARCHAR(100) NOT NULL,      -- 'system-sync' khi đồng bộ từ code
  reason         VARCHAR(255) NULL,
  PRIMARY KEY (id),
  KEY idx_fv_res (function_id, isactive, effective_from, effective_to),
  KEY idx_fv_key (function_key, isactive, effective_from, effective_to),  -- match khi đồng bộ
  CONSTRAINT fk_fv_function FOREIGN KEY (function_id) REFERENCES `function`(id),
  CONSTRAINT chk_fv_period  CHECK (effective_from <= effective_to)
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_0900_ai_ci;
