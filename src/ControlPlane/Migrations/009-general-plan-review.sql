-- Manual, additive migration. Never applied by Control Plane startup.
DO $guard$
BEGIN
  IF NOT EXISTS (SELECT 1 FROM app_schema_versions WHERE version = 8) THEN
    RAISE EXCEPTION 'General plan review requires schema v8';
  END IF;
  IF EXISTS (SELECT 1 FROM app_schema_versions WHERE version = 9) THEN
    RAISE EXCEPTION 'General plan review schema v9 already applied; refusing re-run';
  END IF;
END
$guard$;

CREATE TABLE hic_general_plan_revisions (
  conversation_id uuid NOT NULL REFERENCES hic_general_conversations(conversation_id) ON DELETE RESTRICT,
  revision integer NOT NULL CHECK (revision > 0),
  actor_id uuid NOT NULL,
  tasks_json jsonb NOT NULL CHECK (jsonb_typeof(tasks_json) = 'array'),
  source text NOT NULL CHECK (source IN ('model', 'human')),
  created_at_utc timestamptz NOT NULL,
  PRIMARY KEY (conversation_id, revision)
);
CREATE TABLE hic_general_plan_approvals (
  conversation_id uuid PRIMARY KEY,
  revision integer NOT NULL,
  actor_id uuid NOT NULL,
  approved_at_utc timestamptz NOT NULL,
  FOREIGN KEY (conversation_id, revision)
    REFERENCES hic_general_plan_revisions(conversation_id, revision) ON DELETE RESTRICT
);
CREATE TABLE hic_general_plan_rejections (
  conversation_id uuid NOT NULL,
  revision integer NOT NULL,
  actor_id uuid NOT NULL,
  rejected_at_utc timestamptz NOT NULL,
  PRIMARY KEY (conversation_id, revision),
  FOREIGN KEY (conversation_id, revision)
    REFERENCES hic_general_plan_revisions(conversation_id, revision) ON DELETE RESTRICT
);
CREATE TABLE hic_general_plan_tasks (
  conversation_id uuid NOT NULL REFERENCES hic_general_plan_approvals(conversation_id) ON DELETE RESTRICT,
  ordinal integer NOT NULL CHECK (ordinal >= 0),
  task_id uuid NOT NULL UNIQUE REFERENCES tasks(id) ON DELETE RESTRICT,
  PRIMARY KEY (conversation_id, ordinal)
);
INSERT INTO app_schema_versions(version) VALUES (9);
