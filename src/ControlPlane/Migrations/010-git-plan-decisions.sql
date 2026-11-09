-- Elle ve açık onayla uygulanır; startup veya canlı veritabanında çalıştırılmaz.
DO $guard$
BEGIN
  IF NOT EXISTS (SELECT 1 FROM app_schema_versions WHERE version = 5) THEN
    RAISE EXCEPTION 'Git plan decisions require schema v5';
  END IF;
  IF EXISTS (SELECT 1 FROM app_schema_versions WHERE version = 10) THEN
    RAISE EXCEPTION 'Git plan decisions v10 already applied';
  END IF;
END
$guard$;

CREATE TABLE hic_git_plan_previews (
  intent_digest_sha256 text PRIMARY KEY CHECK (intent_digest_sha256 ~ '^[0-9a-f]{64}$'),
  task_id uuid NOT NULL REFERENCES tasks(id) ON DELETE RESTRICT,
  actor_id uuid NOT NULL CHECK (actor_id <> '00000000-0000-0000-0000-000000000000'),
  task_digest_sha256 text NOT NULL CHECK (task_digest_sha256 ~ '^[0-9a-f]{64}$'),
  action text NOT NULL CHECK (action = 'repository.test-edit'),
  scope text NOT NULL CHECK (scope = 'disposable-worktree:message.txt:write'),
  plan_json jsonb NOT NULL CHECK (jsonb_typeof(plan_json) = 'object'),
  created_at_utc timestamptz NOT NULL DEFAULT clock_timestamp(),
  expires_at_utc timestamptz NOT NULL,
  CHECK (expires_at_utc > created_at_utc AND expires_at_utc <= created_at_utc + interval '5 minutes'),
  UNIQUE (intent_digest_sha256, task_id, actor_id)
);

CREATE UNIQUE INDEX ux_native_health_run_git_decision
  ON native_health_runs(run_id, task_id, actor_id, intent_digest_sha256);

CREATE TABLE hic_git_plan_decisions (
  decision_id uuid PRIMARY KEY,
  intent_digest_sha256 text NOT NULL UNIQUE,
  task_id uuid NOT NULL,
  actor_id uuid NOT NULL,
  decision text NOT NULL CHECK (decision IN ('approved', 'rejected')),
  run_id uuid NULL,
  decided_at_utc timestamptz NOT NULL DEFAULT clock_timestamp(),
  FOREIGN KEY (intent_digest_sha256, task_id, actor_id)
    REFERENCES hic_git_plan_previews(intent_digest_sha256, task_id, actor_id) ON DELETE RESTRICT,
  FOREIGN KEY (run_id, task_id, actor_id, intent_digest_sha256)
    REFERENCES native_health_runs(run_id, task_id, actor_id, intent_digest_sha256) ON DELETE RESTRICT,
  CHECK ((decision = 'approved' AND run_id IS NOT NULL) OR (decision = 'rejected' AND run_id IS NULL))
);
CREATE INDEX ix_hic_git_plan_decisions_task
  ON hic_git_plan_decisions(task_id, actor_id, decided_at_utc DESC, decision_id DESC);

CREATE FUNCTION hic_reject_git_plan_mutation() RETURNS trigger
LANGUAGE plpgsql AS $fn$
BEGIN
  RAISE EXCEPTION 'Git plan previews and decisions are immutable';
END
$fn$;
CREATE TRIGGER hic_git_plan_previews_no_change
BEFORE UPDATE OR DELETE ON hic_git_plan_previews FOR EACH ROW
EXECUTE FUNCTION hic_reject_git_plan_mutation();
CREATE TRIGGER hic_git_plan_decisions_no_change
BEFORE UPDATE OR DELETE ON hic_git_plan_decisions FOR EACH ROW
EXECUTE FUNCTION hic_reject_git_plan_mutation();
CREATE TRIGGER hic_git_plan_previews_no_truncate
BEFORE TRUNCATE ON hic_git_plan_previews FOR EACH STATEMENT
EXECUTE FUNCTION hic_reject_git_plan_mutation();
CREATE TRIGGER hic_git_plan_decisions_no_truncate
BEFORE TRUNCATE ON hic_git_plan_decisions FOR EACH STATEMENT
EXECUTE FUNCTION hic_reject_git_plan_mutation();

INSERT INTO app_schema_versions(version) VALUES (10);
