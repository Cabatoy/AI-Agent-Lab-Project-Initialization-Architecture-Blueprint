-- HIC #159: additive, MANUAL opt-in observe-only handoff ledger.
-- Never run automatically during startup; apply only to approved disposable test DB.
-- No execution queue, AWS credentials, shell commands or worker permissions.
DO $guard$
BEGIN
  IF to_regclass('tasks') IS NULL THEN
    RAISE EXCEPTION 'tasks table required before handoff ledger';
  END IF;
  IF to_regclass('hic_relay_handoffs') IS NOT NULL THEN
    RAISE EXCEPTION 'handoff ledger already exists; refusing repeat';
  END IF;
END
$guard$;

CREATE TABLE hic_relay_handoffs (
  handoff_id uuid PRIMARY KEY,
  task_id uuid NOT NULL UNIQUE REFERENCES tasks(id) ON DELETE RESTRICT,
  actor_id uuid NOT NULL CHECK (actor_id <> '00000000-0000-0000-0000-000000000000'),
  idempotency_key text NOT NULL CHECK (length(idempotency_key) BETWEEN 32 AND 128 AND
    idempotency_key ~ '^[A-Za-z0-9_-]+$'),
  intent_digest_sha256 text NOT NULL CHECK (intent_digest_sha256 ~ '^[0-9a-f]{64}$'),
  repository text NOT NULL,
  expected_base_ref text NOT NULL,
  task_updated_at_utc timestamptz NOT NULL,
  scope text NOT NULL DEFAULT 'staging:observe-only'
    CHECK (scope = 'staging:observe-only'),
  status text NOT NULL DEFAULT 'recorded-untrusted'
    CHECK (status = 'recorded-untrusted'),
  execution_allowed boolean NOT NULL DEFAULT false CHECK (execution_allowed = false),
  created_at_utc timestamptz NOT NULL DEFAULT now(),
  UNIQUE (actor_id, idempotency_key)
);
CREATE INDEX ix_hic_relay_handoffs_actor_created
  ON hic_relay_handoffs(actor_id, created_at_utc DESC);
-- This ledger is immutable. Future transitions require a separately reviewed migration.
CREATE FUNCTION hic_reject_relay_handoff_mutation() RETURNS trigger
LANGUAGE plpgsql AS $fn$
BEGIN
  RAISE EXCEPTION 'Relay handoff ledger is append-only';
END
$fn$;
CREATE TRIGGER hic_relay_handoffs_no_change
BEFORE UPDATE OR DELETE ON hic_relay_handoffs
FOR EACH ROW EXECUTE FUNCTION hic_reject_relay_handoff_mutation();
