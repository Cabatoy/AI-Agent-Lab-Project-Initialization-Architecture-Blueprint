-- HIC server-side user sessions — MANUAL, opt-in additive migration.
-- Never applied by Control Plane startup. Requires 010-user-identities.sql.

CREATE TABLE hic_user_sessions (
  id uuid PRIMARY KEY,
  user_id uuid NOT NULL REFERENCES user_identities(id) ON DELETE CASCADE,
  created_at_utc timestamptz NOT NULL DEFAULT now(),
  expires_at_utc timestamptz NOT NULL,
  revoked_at_utc timestamptz NULL,
  CONSTRAINT ck_hic_user_sessions_expiry CHECK (expires_at_utc > created_at_utc)
);

CREATE INDEX ix_hic_user_sessions_user_expiry
  ON hic_user_sessions(user_id, expires_at_utc);
