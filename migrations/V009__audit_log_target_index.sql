-- Additive only: the permission journal filters audit_log by `target` and orders by `occurred_at`
-- (spec 2026-08-14 §5 B3). No column is altered and no row is rewritten -- historical rows keep their own
-- target shapes and are matched by the read model's legacy branch in slice 3.
ALTER TABLE audit_log ADD INDEX idx_audit_target (target, occurred_at);

INSERT INTO schema_version (version, applied_by, description) VALUES
  (9, 'baseline-deploy', 'V009__audit_log_target_index');
