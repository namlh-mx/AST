-- V008 — audit_log (hạ tầng, KHÔNG temporal — §2 ⑧.4)
-- Audit nghiệp vụ/bảo mật, append-only (ast_app KHÔNG có DELETE => tự chống xóa).
-- Log KỸ THUẬT không vào đây (đi Serilog file cục bộ) — bảng này chỉ audit nghiệp vụ/bảo mật.

CREATE TABLE `audit_log` (
  id          BIGINT UNSIGNED NOT NULL AUTO_INCREMENT,
  occurred_at DATETIME NOT NULL DEFAULT CURRENT_TIMESTAMP,
  username    VARCHAR(100) NULL,            -- ai gây sự kiện (nếu có)
  event_type  VARCHAR(50)  NOT NULL,        -- login | break-glass | signature-fail | permission-change | ...
  target      VARCHAR(150) NULL,            -- đối tượng bị tác động (bảng/căn cước/id)
  detail      JSON NULL,                    -- payload chi tiết (before/after tối thiểu)
  PRIMARY KEY (id),
  KEY idx_audit_time (occurred_at),
  KEY idx_audit_type (event_type, occurred_at)
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_0900_ai_ci;

INSERT INTO schema_version (version, applied_by, description) VALUES
  (8, 'baseline-deploy', 'V008__audit_log');
