-- Apply only to an explicitly selected isolated database; startup does not run this migration.
do $guard$
begin
    if not exists(select 1 from app_schema_versions where version=6) then
        raise exception 'GitHub publisher requires schema v6';
    end if;
    if exists(select 1 from app_schema_versions where version=7) then
        raise exception 'GitHub publisher v7 already applied';
    end if;
end;
$guard$;
create table hic_github_publications (
    publication_id uuid primary key,
    actor_id uuid not null,
    run_id uuid references native_health_runs(run_id),
    conversation_id uuid references hic_project_conversations(conversation_id),
    repository_id bigint not null,
    kind text not null check (kind in ('task','bootstrap')),
    plan_json jsonb not null,
    digest text not null check (digest ~ '^[0-9a-f]{64}$'),
    state text not null check (state in ('preview','pending','blob-intent','tree-intent','commit-intent',
        'commit-created','ref-intent','pushed','pr-intent','succeeded','unknown','blocked')),
    blob_sha text,
    tree_sha text,
    commit_sha text,
    remote_sha text,
    pr_number integer,
    pr_url text,
    error_code text,
    created_at_utc timestamptz not null,
    approved_at_utc timestamptz,
    updated_at_utc timestamptz not null
);
create unique index hic_github_one_task_publication on hic_github_publications(run_id)
    where approved_at_utc is not null and run_id is not null;
create unique index hic_github_one_bootstrap on hic_github_publications(repository_id)
    where approved_at_utc is not null and kind='bootstrap';
create table hic_github_publication_events (
    event_id bigint generated always as identity primary key,
    publication_id uuid not null references hic_github_publications(publication_id),
    event_code text not null,
    recorded_at_utc timestamptz not null default now()
);
create function hic_github_event_immutable() returns trigger language plpgsql as $$
begin
    raise exception 'GitHub publication events are append-only';
end;
$$;
create trigger hic_github_event_immutable before update or delete on hic_github_publication_events
    for each row execute function hic_github_event_immutable();
insert into app_schema_versions(version) values(7);
