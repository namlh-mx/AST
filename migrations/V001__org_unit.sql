-- V001 — org_unit (header + version)
-- Source: docs/design-iam-schema.md §1.1 (do not invent schema).
-- Header = durable identity; version = effective-period version (rule-effective-period).

CREATE TABLE `org_unit` (
  id BIGINT UNSIGNED NOT NULL AUTO_INCREMENT,
  PRIMARY KEY (id)
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_0900_ai_ci;

CREATE TABLE `org_unit_version` (
  id                       BIGINT UNSIGNED NOT NULL AUTO_INCREMENT,
  org_unit_id              BIGINT UNSIGNED NOT NULL,       -- FK -> org_unit(id) (identity header)
  org_code                 VARCHAR(8)   NOT NULL,          -- business code / natural key (P6); app: 4-8, letters+digits, ALL CAPS
  org_name_full_vn         VARCHAR(100) NOT NULL,          -- full VN name (legal profile)
  org_name_short_vn        VARCHAR(100) NOT NULL,          -- short VN name (internal management)
  parent_id                BIGINT UNSIGNED NULL,           -- FK -> org_unit(id) (parent identity); NULL = root
  -- supplemental (optional, §2.4) --
  org_business_number      VARCHAR(14)  NULL,
  org_addr_line_vn         VARCHAR(255) NULL,
  org_addr_line_en         VARCHAR(255) NULL,
  org_addr_ward_vn         VARCHAR(255) NULL,
  org_addr_ward_en         VARCHAR(255) NULL,
  org_addr_district_vn     VARCHAR(255) NULL,
  org_addr_district_en     VARCHAR(255) NULL,
  org_addr_province_vn     VARCHAR(255) NULL,
  org_addr_province_en     VARCHAR(255) NULL,
  org_admin_division_level TINYINT      NOT NULL DEFAULT 2, -- 2 or 3 (§2.4); excluded from x/y progress
  org_name_full_en         VARCHAR(100) NULL,
  org_name_short_en        VARCHAR(100) NULL,
  org_phone                VARCHAR(15)  NULL,
  org_fax                  VARCHAR(15)  NULL,
  org_email                VARCHAR(255) NULL,
  org_reserve_1            VARCHAR(255) NULL,
  org_reserve_2            VARCHAR(255) NULL,
  org_reserve_3            VARCHAR(255) NULL,
  -- durable "Bị hủy" discriminator (§8 #10 / N6): a future plan closed while pending gets isactive=0 AND cancelled=1 --
  cancelled                TINYINT(1)   NOT NULL DEFAULT 0,
  -- per-row action recorded on write (Add/Edit/Close/Cancel) -- Phase 4d history-grid read; nullable, no backfill
  -- for pre-4d rows; enum persisted verbatim via ToString()/Enum.Parse.
  operation_kind           VARCHAR(10)  NULL,
  effective_from           DATE NOT NULL,
  effective_to             DATE NOT NULL DEFAULT '9999-12-31',
  isactive                 TINYINT(1) NOT NULL DEFAULT 1,
  recorded_at              DATETIME NOT NULL DEFAULT CURRENT_TIMESTAMP,
  recorded_by              VARCHAR(100) NOT NULL,
  reason                   VARCHAR(255) NULL,
  PRIMARY KEY (id),
  KEY idx_ouv_res    (org_unit_id, isactive, effective_from, effective_to),
  KEY idx_ouv_code   (org_code, isactive, effective_from, effective_to),   -- backs the P6 overlapping-code lookup
  KEY idx_ouv_parent (parent_id, isactive, effective_from, effective_to),  -- subtree CTE + temporal-FK parent
  CONSTRAINT fk_ouv_ou     FOREIGN KEY (org_unit_id) REFERENCES `org_unit`(id),
  CONSTRAINT fk_ouv_parent FOREIGN KEY (parent_id)   REFERENCES `org_unit`(id),
  CONSTRAINT chk_ouv_period CHECK (effective_from <= effective_to)
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_0900_ai_ci;
