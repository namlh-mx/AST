-- V004 — user (header + version); sid ở HEADER (write-once, Q2 đã chốt)
-- Nguồn: docs/thiet-ke-iam-schema-chi-tiet.md §1.3. `user` là reserved word MySQL -> bắt buộc backtick.
-- Phụ thuộc: org_unit (V001), role (V002) — phải chạy sau 2 script đó.

CREATE TABLE `user` (
  id  BIGINT UNSIGNED NOT NULL AUTO_INCREMENT,
  sid VARCHAR(100) NULL,                    -- [Q2] metadata bền, chụp 1 lần khi đăng nhập lần đầu, immutable sau đó
  PRIMARY KEY (id),
  UNIQUE KEY uq_user_sid (sid)              -- SID (nếu có) duy nhất; NULL không tính vào UNIQUE trong MySQL
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_0900_ai_ci;

CREATE TABLE `user_version` (
  id             BIGINT UNSIGNED NOT NULL AUTO_INCREMENT,
  user_id        BIGINT UNSIGNED NOT NULL,   -- FK -> user(id) (CĂN CƯỚC)
  username       VARCHAR(100) NOT NULL,      -- samAccountName, ĐÃ bỏ tiền tố domain; case-insensitive qua collation
  display_name   VARCHAR(255) NOT NULL,
  org_unit_id    BIGINT UNSIGNED NOT NULL,   -- FK -> org_unit(id) (CĂN CƯỚC) — temporal-FK cha
  role_id        BIGINT UNSIGNED NOT NULL,   -- FK -> role(id) (CĂN CƯỚC)    — temporal-FK cha
  effective_from DATE NOT NULL,
  effective_to   DATE NOT NULL DEFAULT '9999-12-31',
  isactive       TINYINT(1) NOT NULL DEFAULT 1,
  recorded_at    DATETIME NOT NULL DEFAULT CURRENT_TIMESTAMP,
  recorded_by    VARCHAR(100) NOT NULL,
  reason         VARCHAR(255) NULL,
  PRIMARY KEY (id),
  KEY idx_uv_res      (user_id, isactive, effective_from, effective_to),
  KEY idx_uv_username (username, isactive, effective_from, effective_to),  -- phân giải login theo hôm nay
  KEY idx_uv_org      (org_unit_id, isactive, effective_from, effective_to),
  KEY idx_uv_role     (role_id, isactive, effective_from, effective_to),
  CONSTRAINT fk_uv_user FOREIGN KEY (user_id)     REFERENCES `user`(id),
  CONSTRAINT fk_uv_org  FOREIGN KEY (org_unit_id) REFERENCES `org_unit`(id),
  CONSTRAINT fk_uv_role FOREIGN KEY (role_id)     REFERENCES `role`(id),
  CONSTRAINT chk_uv_period CHECK (effective_from <= effective_to)
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_0900_ai_ci;
