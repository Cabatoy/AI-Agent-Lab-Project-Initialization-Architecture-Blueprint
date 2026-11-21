-- HIC normalized user identity foundation — MANUAL, opt-in additive migration.
-- Never applied by Control Plane startup. Apply only to a disposable database
-- until a separately reviewed authentication rollout explicitly adopts it.

CREATE TABLE user_identities (
  id uuid PRIMARY KEY,
  normalized_email text NOT NULL,
  normalized_username text NOT NULL,
  created_at_utc timestamptz NOT NULL DEFAULT now(),
  CONSTRAINT uq_user_identities_normalized_email UNIQUE (normalized_email),
  CONSTRAINT uq_user_identities_normalized_username UNIQUE (normalized_username),
  CONSTRAINT ck_user_identities_normalized_email CHECK (
    normalized_email = lower(btrim(normalized_email)) AND
    normalized_email !~ '[[:space:]]' AND
    length(normalized_email) BETWEEN 3 AND 320
  ),
  CONSTRAINT ck_user_identities_normalized_username CHECK (
    normalized_username = lower(btrim(normalized_username)) AND
    length(normalized_username) BETWEEN 1 AND 64
  )
);
