-- HIC local system.health runner v4 — MANUAL, opt-in additive migration.
-- Never applied by Control Plane startup. The migration requires verified v2
-- storage and is independent of the experimental workspace.inspect v3 ledger.
-- Apply only to a separately approved disposable database during development.

DO $guard$
BEGIN
  IF NOT EXISTS (SELECT 1 FROM app_schema_versions WHERE version = 2) THEN
    RAISE EXCEPTION 'Local health runner v4 requires verified schema v2';
  END IF;
  IF EXISTS (SELECT 1 FROM app_schema_versions WHERE version = 4) THEN
    RAISE EXCEPTION 'Local health runner v4 already applied; refusing re-run';
  END IF;
END
$guard$;

CREATE TABLE native_health_runs (
  run_id uuid PRIMARY KEY,
  task_id uuid NOT NULL UNIQUE REFERENCES tasks(id) ON DELETE RESTRICT,
  actor_id uuid NOT NULL CHECK (actor_id <> '00000000-0000-0000-0000-000000000000'),
  action text NOT NULL CHECK (action = 'system.health'),
  scope text NOT NULL CHECK (scope = 'control-plane:health:read'),
  intent_digest_sha256 text NOT NULL CHECK (intent_digest_sha256 ~ '^[0-9a-f]{64}$'),
  task_updated_at_utc timestamptz NOT NULL,
  status text NOT NULL CHECK (status IN ('queued', 'running', 'completed', 'expired', 'invalidated')),
  created_at_utc timestamptz NOT NULL,
  approved_at_utc timestamptz NOT NULL,
  expires_at_utc timestamptz NOT NULL,
  started_at_utc timestamptz NULL,
  completed_at_utc timestamptz NULL,
  result_json jsonb NULL,
  CHECK (expires_at_utc > approved_at_utc),
  CHECK (expires_at_utc <= approved_at_utc + interval '5 minutes'),
  CHECK (
    (status = 'queued' AND started_at_utc IS NULL AND completed_at_utc IS NULL AND result_json IS NULL) OR
    (status = 'running' AND started_at_utc IS NOT NULL AND completed_at_utc IS NULL AND result_json IS NULL) OR
    (status = 'completed' AND started_at_utc IS NOT NULL AND completed_at_utc IS NOT NULL AND result_json IS NOT NULL) OR
    (status IN ('expired', 'invalidated') AND started_at_utc IS NULL AND completed_at_utc IS NOT NULL AND result_json IS NULL)
  )
);
CREATE INDEX ix_native_health_runs_status_expiry
  ON native_health_runs(status, expires_at_utc);

-- Audit contains only fixed event codes, actor/run identifiers and timestamps.
-- Task text, credentials, exception details, prompts and raw responses are excluded.
CREATE TABLE native_health_run_audit (
  id uuid PRIMARY KEY,
  run_id uuid NOT NULL REFERENCES native_health_runs(run_id) ON DELETE RESTRICT,
  actor_id uuid NOT NULL CHECK (actor_id <> '00000000-0000-0000-0000-000000000000'),
  event_code text NOT NULL CHECK (
    event_code IN ('approved', 'queued', 'claimed', 'completed', 'expired', 'invalidated', 'rejected')
  ),
  recorded_at_utc timestamptz NOT NULL
);
CREATE INDEX ix_native_health_run_audit_run_time
  ON native_health_run_audit(run_id, recorded_at_utc);

CREATE FUNCTION hic_reject_native_health_audit_mutation() RETURNS trigger
LANGUAGE plpgsql AS $fn$
BEGIN
  RAISE EXCEPTION 'Native health run audit is append-only';
END
$fn$;
CREATE TRIGGER hic_native_health_run_audit_no_change
BEFORE UPDATE OR DELETE ON native_health_run_audit FOR EACH ROW
EXECUTE FUNCTION hic_reject_native_health_audit_mutation();

INSERT INTO app_schema_versions(version) VALUES (4);
