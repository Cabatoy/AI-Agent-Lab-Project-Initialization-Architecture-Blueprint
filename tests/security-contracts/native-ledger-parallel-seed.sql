-- Synthetic CI-only fixture, not a genuine authenticated approval.
INSERT INTO tasks(id,title,description,status,attempt_count,created_at_utc,updated_at_utc)
VALUES ('10000000-0000-4000-8000-000000000101','Parallel CI Draft','No user data',
        'Draft',0,now(),now());
INSERT INTO native_run_approvals (
 id,task_id,run_id,task_revision,actor_id,action,scope,intent_digest,
 receipt_digest,key_id,approved_at_utc,expires_at_utc,state)
VALUES (
 '10000000-0000-4000-8000-000000000201',
 '10000000-0000-4000-8000-000000000101',
 '10000000-0000-4000-8000-000000000301',1,
 '10000000-0000-4000-8000-000000000401',
 'workspace.inspect','workspace:configured:read',repeat('a',64),
 repeat('b',64),'ci-synthetic-key',now()-interval '5 seconds',
 now()+interval '4 minutes','approved');
