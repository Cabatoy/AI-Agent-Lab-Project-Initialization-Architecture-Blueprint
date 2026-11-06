create table if not exists app_schema_versions (
    version integer primary key,
    applied_at_utc timestamptz not null default now()
);

alter table tasks add column if not exists execution_generation bigint not null default 0
    check (execution_generation >= 0);

create table if not exists task_executions (
    id uuid primary key,
    task_id uuid not null references tasks(id),
    worker_id uuid not null,
    generation bigint not null check (generation > 0),
    status text not null,
    started_at_utc timestamptz not null default now(),
    completed_at_utc timestamptz null,
    unique (task_id, generation),
    check ((status in ('Executing', 'InProgress', 'Review') and completed_at_utc is null)
        or (status not in ('Executing', 'InProgress', 'Review') and completed_at_utc is not null))
);

create unique index if not exists ix_task_executions_one_active
    on task_executions(task_id) where completed_at_utc is null;

alter table task_status_transitions add column if not exists execution_id uuid null
    references task_executions(id);

insert into app_schema_versions(version) values (2) on conflict do nothing;
