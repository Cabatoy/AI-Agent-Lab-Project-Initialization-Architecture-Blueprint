-- Manual, separately approved migration only; never run automatically.
DO $guard$
BEGIN
  IF NOT EXISTS (SELECT 1 FROM app_schema_versions WHERE version = 10) THEN
    RAISE EXCEPTION 'Issue import requires schema v10';
  END IF;
  IF EXISTS (SELECT 1 FROM app_schema_versions WHERE version = 11) THEN
    RAISE EXCEPTION 'Issue import v11 already applied';
  END IF;
END
$guard$;

CREATE TABLE hic_issue_import_previews (
  preview_id uuid PRIMARY KEY,
  actor_id uuid NOT NULL CHECK (actor_id <> '00000000-0000-0000-0000-000000000000'),
  repository_id bigint NOT NULL CHECK (repository_id > 0),
  issue_id bigint NOT NULL CHECK (issue_id > 0),
  issue_number integer NOT NULL CHECK (issue_number > 0),
  reference text NOT NULL,
  revision_sha256 text NOT NULL CHECK (revision_sha256 ~ '^[0-9a-f]{64}$'),
  title text NOT NULL,
  description text NOT NULL,
  created_at_utc timestamptz NOT NULL DEFAULT clock_timestamp(),
  expires_at_utc timestamptz NOT NULL,
  UNIQUE (preview_id, actor_id, repository_id, issue_id, issue_number, revision_sha256)
);
CREATE TABLE hic_issue_imports (
  repository_id bigint NOT NULL,
  issue_number integer NOT NULL,
  revision_sha256 text NOT NULL,
  issue_id bigint NOT NULL,
  actor_id uuid NOT NULL,
  preview_id uuid NOT NULL UNIQUE,
  task_id uuid NOT NULL UNIQUE REFERENCES tasks(id) ON DELETE RESTRICT,
  imported_at_utc timestamptz NOT NULL DEFAULT clock_timestamp(),
  PRIMARY KEY (repository_id, issue_number, revision_sha256),
  FOREIGN KEY (preview_id, actor_id, repository_id, issue_id, issue_number, revision_sha256)
    REFERENCES hic_issue_import_previews(preview_id, actor_id, repository_id, issue_id, issue_number, revision_sha256)
    ON DELETE RESTRICT
);
CREATE FUNCTION hic_reject_issue_import_mutation() RETURNS trigger LANGUAGE plpgsql AS $fn$
BEGIN
  RAISE EXCEPTION 'Issue import evidence is immutable';
END
$fn$;
CREATE TRIGGER hic_issue_imports_no_change BEFORE UPDATE OR DELETE ON hic_issue_imports
FOR EACH ROW EXECUTE FUNCTION hic_reject_issue_import_mutation();
CREATE TRIGGER hic_issue_import_previews_no_change BEFORE UPDATE OR DELETE ON hic_issue_import_previews
FOR EACH ROW EXECUTE FUNCTION hic_reject_issue_import_mutation();
CREATE TRIGGER hic_issue_imports_no_truncate BEFORE TRUNCATE ON hic_issue_imports
FOR EACH STATEMENT EXECUTE FUNCTION hic_reject_issue_import_mutation();
CREATE TRIGGER hic_issue_import_previews_no_truncate BEFORE TRUNCATE ON hic_issue_import_previews
FOR EACH STATEMENT EXECUTE FUNCTION hic_reject_issue_import_mutation();
INSERT INTO app_schema_versions(version) VALUES (11);
