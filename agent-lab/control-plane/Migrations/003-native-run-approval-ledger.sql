-- HIC Native Runner approval ledger v3 — MANUAL, opt-in migration ONLY.
-- NEVER applied by the current Control Plane startup, managed schema v2 bootstrap,
-- legacy R2 recovery, the Worker or the native preview.
-- Run only after future authenticated-approval implementation, snapshot/backup
-- verification and explicit local database migration authorization.
-- Caller MUST run with psql -v ON_ERROR_STOP=1 --single-transaction.

DO $guard$
BEGIN
  IF NOT EXISTS (SELECT 1 FROM app_schema_versions WHERE version = 2) THEN
    RAISE EXCEPTION 'Native approval ledger v3 requires verified schema v2';
  END IF;
  IF EXISTS (SELECT 1 FROM app_schema_versions WHERE version = 3) THEN
    RAISE EXCEPTION 'Native approval ledger v3 already applied; refusing re-run';
  END IF;
END
$guard$;

-- Every existing task update invalidates any approval bound to an older revision.
-- Existing rows start at revision 1. This is not a replacement for human auth.
ALTER TABLE tasks ADD COLUMN native_run_revision bigint NOT NULL DEFAULT 1
  CHECK (native_run_revision >= 1);

CREATE FUNCTION hic_bump_native_run_revision() RETURNS trigger
LANGUAGE plpgsql AS $fn$
BEGIN
  NEW.native_run_revision := OLD.native_run_revision + 1;
  RETURN NEW;
END
$fn$;

CREATE TRIGGER hic_tasks_revision_update
BEFORE UPDATE ON tasks FOR EACH ROW
EXECUTE FUNCTION hic_bump_native_run_revision();

CREATE TABLE native_run_approvals (
  id uuid PRIMARY KEY,
  task_id uuid NOT NULL REFERENCES tasks(id) ON DELETE RESTRICT,
  run_id uuid NOT NULL UNIQUE,
  task_revision bigint NOT NULL CHECK (task_revision > 0),
  actor_id uuid NOT NULL CHECK (actor_id <> '00000000-0000-0000-0000-000000000000'),
  action text NOT NULL CHECK (action = 'workspace.inspect'),
  scope text NOT NULL CHECK (scope = 'workspace:configured:read'),
  intent_digest text NOT NULL CHECK (intent_digest ~ '^[0-9a-f]{64}$'),
  receipt_digest text NOT NULL CHECK (receipt_digest ~ '^[0-9a-f]{64}$'),
  key_id text NOT NULL CHECK (length(key_id) BETWEEN 1 AND 128),
  approved_at_utc timestamptz NOT NULL,
  expires_at_utc timestamptz NOT NULL,
  state text NOT NULL CHECK (state IN ('approved', 'revoked')),
  revoked_at_utc timestamptz NULL,
  UNIQUE (id, task_id, run_id),
  CHECK (expires_at_utc > approved_at_utc),
  CHECK (expires_at_utc <= approved_at_utc + interval '5 minutes'),
  CHECK ((state = 'approved' AND revoked_at_utc IS NULL) OR
         (state = 'revoked' AND revoked_at_utc IS NOT NULL))
);
CREATE INDEX ix_native_run_approvals_task ON native_run_approvals(task_id);

CREATE TABLE native_run_claims (
  id uuid PRIMARY KEY,
  approval_id uuid NOT NULL UNIQUE,
  task_id uuid NOT NULL,
  run_id uuid NOT NULL UNIQUE,
  runner_id uuid NOT NULL CHECK (runner_id <> '00000000-0000-0000-0000-000000000000'),
  claimed_at_utc timestamptz NOT NULL DEFAULT now(),
  status text NOT NULL DEFAULT 'claimed'
    CHECK (status IN ('claimed', 'completed', 'failed', 'cancelled')),
  completed_at_utc timestamptz NULL,
  FOREIGN KEY (approval_id, task_id, run_id)
    REFERENCES native_run_approvals(id, task_id, run_id) ON DELETE RESTRICT,
  CHECK ((status = 'claimed' AND completed_at_utc IS NULL) OR
         (status <> 'claimed' AND completed_at_utc IS NOT NULL))
);
CREATE INDEX ix_native_run_claims_task ON native_run_claims(task_id);

-- Deliberately no descriptions, stdout, prompts, credentials or raw receipts.
CREATE TABLE native_run_audit (
  id uuid PRIMARY KEY,
  approval_id uuid NOT NULL REFERENCES native_run_approvals(id) ON DELETE RESTRICT,
  task_id uuid NOT NULL REFERENCES tasks(id) ON DELETE RESTRICT,
  run_id uuid NOT NULL,
  event_code text NOT NULL
    CHECK (event_code IN ('claimed', 'completed', 'failed', 'cancelled', 'revoked')),
  recorded_at_utc timestamptz NOT NULL DEFAULT now(),
  detail_digest text NULL CHECK (detail_digest IS NULL OR detail_digest ~ '^[0-9a-f]{64}$')
);
CREATE INDEX ix_native_run_audit_task_time ON native_run_audit(task_id, recorded_at_utc);

CREATE FUNCTION hic_reject_native_run_audit_mutation() RETURNS trigger
LANGUAGE plpgsql AS $fn$
BEGIN
  RAISE EXCEPTION 'Native run audit is append-only';
END
$fn$;
CREATE TRIGGER hic_native_run_audit_no_change
BEFORE UPDATE OR DELETE ON native_run_audit FOR EACH ROW
EXECUTE FUNCTION hic_reject_native_run_audit_mutation();

-- ONLY a trustworthy, authenticated Control Plane component may call this
-- function AFTER validating human identity, signed receipt, original intent
-- digest, task status, scope and authority. Never expose this function as a
-- generic user/browser RPC. The SQL function itself does NOT authenticate.
-- Returns run_id exactly once; a second call returns NULL.
CREATE FUNCTION hic_try_claim_native_readonly_run(
  p_approval_id uuid,
  p_runner_id uuid,
  p_intent_digest text)
RETURNS uuid
LANGUAGE plpgsql AS $fn$
DECLARE
  v_task_id uuid;
  v_status text;
  v_revision bigint;
  v_approval native_run_approvals%ROWTYPE;
  v_claimed uuid;
  v_now timestamptz;
BEGIN
  IF p_runner_id IS NULL OR p_runner_id = '00000000-0000-0000-0000-000000000000' OR
     p_intent_digest IS NULL THEN
    RETURN NULL;
  END IF;

  -- Consistent lock order: task row, then approval row. Recheck after locking.
  SELECT task_id INTO v_task_id FROM native_run_approvals WHERE id = p_approval_id;
  IF NOT FOUND THEN RETURN NULL; END IF;
  SELECT status, native_run_revision INTO v_status, v_revision
  FROM tasks WHERE id = v_task_id FOR UPDATE;
  IF NOT FOUND THEN RETURN NULL; END IF;
  SELECT * INTO v_approval FROM native_run_approvals
  WHERE id = p_approval_id FOR UPDATE;
  IF NOT FOUND THEN RETURN NULL; END IF;
  v_now := clock_timestamp();

  -- Existing Worker approvals are deliberately NOT accepted; native read-only
  -- grant keeps task in Draft without enqueuing the Worker.
  IF v_status <> 'Draft' OR
     v_approval.task_id <> v_task_id OR
     v_approval.task_revision <> v_revision OR
     v_approval.state <> 'approved' OR
     v_approval.revoked_at_utc IS NOT NULL OR
     v_approval.approved_at_utc > v_now OR
     v_approval.expires_at_utc <= v_now OR
     v_approval.intent_digest <> p_intent_digest OR
     v_approval.action <> 'workspace.inspect' OR
     v_approval.scope <> 'workspace:configured:read' THEN
    RETURN NULL;
  END IF;

  INSERT INTO native_run_claims(id, approval_id, task_id, run_id, runner_id)
  VALUES (gen_random_uuid(), v_approval.id, v_task_id, v_approval.run_id, p_runner_id)
  ON CONFLICT DO NOTHING
  RETURNING run_id INTO v_claimed;

  IF v_claimed IS NULL THEN RETURN NULL; END IF;

  INSERT INTO native_run_audit(id, approval_id, task_id, run_id, event_code)
  VALUES (gen_random_uuid(), v_approval.id, v_task_id, v_claimed, 'claimed');
  RETURN v_claimed;
END
$fn$;

INSERT INTO app_schema_versions(version) VALUES (3);
