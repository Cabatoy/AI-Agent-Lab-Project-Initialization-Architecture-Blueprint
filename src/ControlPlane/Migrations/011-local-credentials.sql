-- Local password credentials — MANUAL, opt-in additive migration.
-- Never applied by Control Plane startup. Requires 010-user-identities.sql.

CREATE TABLE local_password_credentials (
  user_id uuid PRIMARY KEY REFERENCES user_identities(id) ON DELETE CASCADE,
  password_hash text NOT NULL,
  created_at_utc timestamptz NOT NULL DEFAULT now(),
  updated_at_utc timestamptz NOT NULL DEFAULT now()
);
