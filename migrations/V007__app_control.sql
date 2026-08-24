-- V007 — app_control (hạ tầng, KHÔNG temporal — §2 ⑧.2)
-- Lệnh điều khiển app (hẹn giờ đóng để cập nhật bản mới); app poll qua IPollingService (phase sau).
-- LƯU Ý: is_active (cờ lệnh) KHÁC isactive (cờ kỳ hiệu lực trên bảng version) — 2 ngữ nghĩa khác nhau.

CREATE TABLE `app_control` (
  id             BIGINT UNSIGNED NOT NULL AUTO_INCREMENT,
  command        VARCHAR(50)  NOT NULL,     -- vd 'shutdown'
  target_version VARCHAR(50)  NULL,         -- bản deploy mới cần chuyển sang
  deadline_at    DATETIME     NULL,         -- hạn app phải tự đóng
  message        VARCHAR(500) NULL,         -- thông báo hiển thị cho user
  is_active      TINYINT(1) NOT NULL DEFAULT 1,
  created_at     DATETIME NOT NULL DEFAULT CURRENT_TIMESTAMP,
  created_by     VARCHAR(100) NOT NULL,
  PRIMARY KEY (id),
  KEY idx_appctl_active (is_active, created_at)
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_0900_ai_ci;

INSERT INTO schema_version (version, applied_by, description) VALUES
  (7, 'baseline-deploy', 'V007__app_control');
