-- Password reset tokens — MANUAL, opt-in additive migration.
-- Never applied by Control Plane startup. Requires 011-local-credentials.sql.

CREATE TABLE hic_password_reset_tokens (
  verifier bytea PRIMARY KEY CHECK (octet_length(verifier) = 32),
  user_id uuid NOT NULL REFERENCES local_password_credentials(user_id) ON DELETE CASCADE,
  created_at_utc timestamptz NOT NULL DEFAULT now(),
  expires_at_utc timestamptz NOT NULL,
  invalidated_at_utc timestamptz NULL,
  CONSTRAINT ck_hic_password_reset_expiry CHECK (expires_at_utc > created_at_utc)
);

CREATE INDEX ix_hic_password_reset_tokens_user
  ON hic_password_reset_tokens(user_id);
