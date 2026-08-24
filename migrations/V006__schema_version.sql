-- V006 — schema_version (hạ tầng, KHÔNG temporal — §2 ⑧.1)
-- App kiểm lúc khởi động: version schema DB khớp version app cần; lệch -> CHẶN khởi động.
-- Quy ước: PK = version (số thứ tự migration); mỗi script migration (từ đây trở đi) tự INSERT
-- dòng của chính nó ở cuối script. V001-V005 chạy TRƯỚC KHI bảng này tồn tại nên được ghi nhận
-- bù (retroactive) ngay trong script này, cùng với chính V006.

CREATE TABLE `schema_version` (
  version     INT NOT NULL,                 -- số thứ tự migration (V001 -> 1)
  applied_at  DATETIME NOT NULL DEFAULT CURRENT_TIMESTAMP,
  applied_by  VARCHAR(100) NOT NULL,        -- DBA chạy script
  description VARCHAR(255) NULL,
  PRIMARY KEY (version)
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_0900_ai_ci;

INSERT INTO schema_version (version, applied_by, description) VALUES
  (1, 'baseline-deploy', 'V001__org_unit'),
  (2, 'baseline-deploy', 'V002__role'),
  (3, 'baseline-deploy', 'V003__function'),
  (4, 'baseline-deploy', 'V004__user'),
  (5, 'baseline-deploy', 'V005__role_permission'),
  (6, 'baseline-deploy', 'V006__schema_version');
