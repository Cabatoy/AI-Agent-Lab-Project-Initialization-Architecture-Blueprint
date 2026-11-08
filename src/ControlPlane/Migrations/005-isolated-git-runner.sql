-- Manual opt-in migration: disposable databases only; never applied at startup.
DO $guard$
BEGIN
  IF NOT EXISTS (SELECT 1 FROM app_schema_versions WHERE version = 4) THEN
    RAISE EXCEPTION 'Isolated Git runner requires schema v4';
  END IF;
  IF EXISTS (SELECT 1 FROM app_schema_versions WHERE version = 5) THEN
    RAISE EXCEPTION 'Isolated Git runner v5 already applied';
  END IF;
END
$guard$;

ALTER TABLE native_health_runs ADD COLUMN plan_json jsonb NULL;
ALTER TABLE native_health_runs DROP CONSTRAINT native_health_runs_action_check;
ALTER TABLE native_health_runs DROP CONSTRAINT native_health_runs_scope_check;
ALTER TABLE native_health_runs ADD CONSTRAINT native_local_action_scope CHECK (
  (action = 'system.health' AND scope = 'control-plane:health:read' AND plan_json IS NULL) OR
  (action = 'repository.test-edit' AND scope = 'disposable-worktree:message.txt:write'
   AND plan_json IS NOT NULL AND jsonb_typeof(plan_json) = 'object')
);
ALTER TABLE native_health_runs DROP CONSTRAINT native_health_runs_status_check;
DO $state$
DECLARE state_constraint text;
BEGIN
  SELECT conname INTO STRICT state_constraint FROM pg_constraint
  WHERE conrelid = 'native_health_runs'::regclass AND contype = 'c'
    AND pg_get_constraintdef(oid) LIKE '%started_at_utc%';
  EXECUTE format('ALTER TABLE native_health_runs DROP CONSTRAINT %I', state_constraint);
END
$state$;
ALTER TABLE native_health_runs ADD CONSTRAINT native_local_run_status CHECK (
  status IN ('queued', 'running', 'completed', 'failed', 'expired', 'invalidated')
);
ALTER TABLE native_health_runs ADD CONSTRAINT native_local_run_state CHECK (
  (status = 'queued' AND started_at_utc IS NULL AND completed_at_utc IS NULL AND result_json IS NULL) OR
  (status = 'running' AND started_at_utc IS NOT NULL AND completed_at_utc IS NULL AND result_json IS NULL) OR
  (status IN ('completed', 'failed') AND started_at_utc IS NOT NULL AND completed_at_utc IS NOT NULL AND result_json IS NOT NULL) OR
  (status IN ('expired', 'invalidated') AND started_at_utc IS NULL AND completed_at_utc IS NOT NULL AND result_json IS NULL)
);
ALTER TABLE native_health_run_audit DROP CONSTRAINT native_health_run_audit_event_code_check;
ALTER TABLE native_health_run_audit ADD CONSTRAINT native_local_audit_event CHECK (
  event_code IN ('approved', 'queued', 'claimed', 'completed', 'failed', 'expired', 'invalidated', 'rejected')
);
-- Keep the existing task reservation, single claim and append-only audit.
INSERT INTO app_schema_versions(version) VALUES (5);
