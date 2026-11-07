-- Fresh HIC-managed installations ONLY. This script must NEVER migrate historic R2 databases.
-- Run exclusively through the profiled schema-init container on a just-created, empty
-- named PostgreSQL volume. The full script is transactional (psql --single-transaction).
DO $$
BEGIN
    IF EXISTS (
        SELECT 1 FROM pg_catalog.pg_class c
        JOIN pg_catalog.pg_namespace n ON n.oid = c.relnamespace
        WHERE n.nspname = 'public' AND c.relkind IN ('r','p','v','m','S')
    ) THEN
        RAISE EXCEPTION 'HIC managed bootstrap refused: public schema is not empty.';
    END IF;
END
$$;

CREATE TABLE tasks (
    id uuid primary key,
    title text not null,
    description text not null,
    status text not null,
    attempt_count integer not null default 0,
    result text null,
    error_message text null,
    created_at_utc timestamptz not null,
    updated_at_utc timestamptz not null
);

CREATE TABLE task_status_transitions (
    id uuid primary key,
    task_id uuid not null references tasks(id) on delete cascade,
    from_status text not null,
    to_status text not null,
    source text not null,
    changed_at_utc timestamptz not null,
    note text null
);

CREATE INDEX ix_task_status_transitions_task_id_changed_at
    ON task_status_transitions(task_id, changed_at_utc desc);

CREATE TABLE execution_plans (
    id uuid primary key,
    project_key text null,
    request_title text not null,
    request_description text null,
    status text not null,
    plan_summary text not null,
    created_at_utc timestamptz not null,
    updated_at_utc timestamptz not null
);

CREATE INDEX ix_execution_plans_created_at
    ON execution_plans(created_at_utc desc);

-- Reuse the original R2-owned migration resource exactly; do not maintain a copy.
\i /control-plane-migrations/002-execution-ownership.sql
