-- Durable delivery for approved tasks. Apply only after verifying schema v2
-- and the target database; this migration is additive and is never auto-applied.
DO $guard$
BEGIN
  IF NOT EXISTS (SELECT 1 FROM app_schema_versions WHERE version = 2) THEN
    RAISE EXCEPTION 'Task enqueue outbox v5 requires verified schema v2';
  END IF;
  IF EXISTS (SELECT 1 FROM app_schema_versions WHERE version = 5) THEN
    RAISE EXCEPTION 'Task enqueue outbox v5 already applied; refusing re-run';
  END IF;
END
$guard$;

CREATE TABLE task_enqueue_outbox (
  task_id uuid PRIMARY KEY REFERENCES tasks(id) ON DELETE RESTRICT,
  created_at_utc timestamptz NOT NULL DEFAULT now(),
  attempt_count integer NOT NULL DEFAULT 0 CHECK (attempt_count >= 0),
  last_attempt_at_utc timestamptz NULL,
  last_enqueued_at_utc timestamptz NULL,
  claimed_at_utc timestamptz NULL
);

CREATE INDEX ix_task_enqueue_outbox_pending
  ON task_enqueue_outbox(created_at_utc)
  WHERE claimed_at_utc IS NULL;

-- Preserve delivery for approvals committed before this migration.
INSERT INTO task_enqueue_outbox(task_id)
SELECT id FROM tasks WHERE status = 'Approved'
ON CONFLICT (task_id) DO NOTHING;

INSERT INTO app_schema_versions(version) VALUES (5);
