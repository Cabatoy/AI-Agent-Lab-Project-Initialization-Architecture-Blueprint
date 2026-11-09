-- Manual, additive migration. Never applied by Control Plane startup.
DO $guard$
BEGIN
  IF NOT EXISTS (SELECT 1 FROM app_schema_versions WHERE version = 7) THEN
    RAISE EXCEPTION 'General conversations require GitHub publisher schema v7';
  END IF;
  IF EXISTS (SELECT 1 FROM app_schema_versions WHERE version = 8) THEN
    RAISE EXCEPTION 'General conversation schema v8 already applied; refusing re-run';
  END IF;
END
$guard$;

CREATE TABLE hic_general_conversations (
  conversation_id uuid PRIMARY KEY,
  actor_id uuid NOT NULL CHECK (actor_id <> '00000000-0000-0000-0000-000000000000'),
  title text NOT NULL CHECK (length(title) BETWEEN 1 AND 120),
  created_at_utc timestamptz NOT NULL,
  updated_at_utc timestamptz NOT NULL
);
CREATE INDEX ix_hic_general_conversations_actor_updated
  ON hic_general_conversations(actor_id, updated_at_utc DESC);

CREATE TABLE hic_general_messages (
  sequence bigint GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
  message_id uuid NOT NULL UNIQUE,
  conversation_id uuid NOT NULL REFERENCES hic_general_conversations(conversation_id) ON DELETE RESTRICT,
  role text NOT NULL CHECK (role IN ('user', 'assistant')),
  content text NOT NULL CHECK (length(content) BETWEEN 1 AND 12000),
  provider text NULL,
  model_name text NULL,
  status text NOT NULL CHECK (status IN ('completed', 'unavailable', 'invalid-response')),
  client_message_id uuid NULL,
  reply_to_message_id uuid NULL REFERENCES hic_general_messages(message_id) ON DELETE RESTRICT,
  created_at_utc timestamptz NOT NULL,
  CHECK ((role = 'user' AND client_message_id IS NOT NULL AND reply_to_message_id IS NULL
    AND provider IS NULL AND model_name IS NULL AND status = 'completed') OR
    (role = 'assistant' AND client_message_id IS NULL AND reply_to_message_id IS NOT NULL))
);
CREATE UNIQUE INDEX ux_hic_general_client_turn
  ON hic_general_messages(conversation_id, client_message_id) WHERE client_message_id IS NOT NULL;
CREATE UNIQUE INDEX ux_hic_general_single_reply
  ON hic_general_messages(reply_to_message_id) WHERE reply_to_message_id IS NOT NULL;
CREATE INDEX ix_hic_general_messages_conversation ON hic_general_messages(conversation_id, sequence);

INSERT INTO app_schema_versions(version) VALUES (8);
