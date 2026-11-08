-- Manual, additive migration for disposable HIC conversation/code-edit testing.
-- Never applied by Control Plane startup.
DO $guard$
BEGIN
  IF NOT EXISTS (SELECT 1 FROM app_schema_versions WHERE version = 5) THEN
    RAISE EXCEPTION 'Project conversation code editing requires schema v5';
  END IF;
  IF EXISTS (SELECT 1 FROM app_schema_versions WHERE version = 6) THEN
    RAISE EXCEPTION 'Project conversation schema v6 already applied; refusing re-run';
  END IF;
END
$guard$;

CREATE TABLE hic_project_conversations (
  conversation_id uuid PRIMARY KEY,
  actor_id uuid NOT NULL CHECK (actor_id <> '00000000-0000-0000-0000-000000000000'),
  task_id uuid NOT NULL UNIQUE REFERENCES tasks(id) ON DELETE RESTRICT,
  title text NOT NULL CHECK (length(title) BETWEEN 1 AND 120),
  repository_id text NOT NULL,
  repository_path text NOT NULL,
  base_commit text NOT NULL CHECK (base_commit ~ '^[0-9a-f]{40}$'),
  file_path text NOT NULL CHECK (length(file_path) BETWEEN 1 AND 120),
  created_at_utc timestamptz NOT NULL,
  updated_at_utc timestamptz NOT NULL
);
CREATE INDEX ix_hic_project_conversations_actor_updated
  ON hic_project_conversations(actor_id, updated_at_utc DESC);

CREATE TABLE hic_project_messages (
  message_id uuid PRIMARY KEY,
  conversation_id uuid NOT NULL REFERENCES hic_project_conversations(conversation_id) ON DELETE RESTRICT,
  role text NOT NULL CHECK (role IN ('user', 'assistant')),
  content text NOT NULL CHECK (length(content) BETWEEN 1 AND 12000),
  provider text NULL,
  model_name text NULL,
  status text NOT NULL CHECK (status IN ('completed', 'unavailable', 'invalid-response', 'invalid-proposal')),
  client_message_id uuid NULL,
  reply_to_message_id uuid NULL REFERENCES hic_project_messages(message_id) ON DELETE RESTRICT,
  proposal_json jsonb NULL CHECK (proposal_json IS NULL OR jsonb_typeof(proposal_json) = 'object'),
  created_at_utc timestamptz NOT NULL,
  CHECK (
    (role = 'user' AND client_message_id IS NOT NULL AND reply_to_message_id IS NULL
      AND provider IS NULL AND model_name IS NULL AND proposal_json IS NULL AND status = 'completed') OR
    (role = 'assistant' AND client_message_id IS NULL AND reply_to_message_id IS NOT NULL)
  )
);
CREATE UNIQUE INDEX ux_hic_project_messages_client_turn
  ON hic_project_messages(conversation_id, client_message_id)
  WHERE client_message_id IS NOT NULL;
CREATE UNIQUE INDEX ux_hic_project_messages_single_reply
  ON hic_project_messages(reply_to_message_id)
  WHERE reply_to_message_id IS NOT NULL;
CREATE INDEX ix_hic_project_messages_conversation_time
  ON hic_project_messages(conversation_id, created_at_utc, message_id);

ALTER TABLE native_health_runs ADD COLUMN conversation_id uuid NULL
  REFERENCES hic_project_conversations(conversation_id) ON DELETE RESTRICT;
ALTER TABLE native_health_runs DROP CONSTRAINT native_local_action_scope;
ALTER TABLE native_health_runs ADD CONSTRAINT native_local_action_scope CHECK (
  (action = 'system.health' AND scope = 'control-plane:health:read' AND plan_json IS NULL
    AND conversation_id IS NULL) OR
  (action = 'repository.test-edit' AND scope = 'disposable-worktree:message.txt:write'
    AND plan_json IS NOT NULL AND jsonb_typeof(plan_json) = 'object' AND conversation_id IS NULL) OR
  (action = 'repository.model-code-edit'
    AND scope = 'disposable-worktree:one-allowlisted-csharp-file:write'
    AND plan_json IS NOT NULL AND jsonb_typeof(plan_json) = 'object'
    AND conversation_id IS NOT NULL)
);
CREATE INDEX ix_native_health_runs_conversation
  ON native_health_runs(conversation_id, created_at_utc DESC)
  WHERE conversation_id IS NOT NULL;

INSERT INTO app_schema_versions(version) VALUES (6);
