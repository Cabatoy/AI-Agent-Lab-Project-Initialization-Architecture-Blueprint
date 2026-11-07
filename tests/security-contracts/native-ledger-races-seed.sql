-- Synthetic, disposable CI fixture. Not a real operator approval.
INSERT INTO tasks(id,title,description,status,attempt_count,created_at_utc,updated_at_utc)
VALUES
 ('20000000-0000-4000-8000-000000000101','Revoke race','synthetic','Draft',0,now(),now()),
 ('20000000-0000-4000-8000-000000000102','Revision race','synthetic','Draft',0,now(),now()),
 ('20000000-0000-4000-8000-000000000103','Positive control','synthetic','Draft',0,now(),now());

INSERT INTO native_run_approvals (
 id,task_id,run_id,task_revision,actor_id,action,scope,intent_digest,
 receipt_digest,key_id,approved_at_utc,expires_at_utc,state)
VALUES
 ('20000000-0000-4000-8000-000000000201',
  '20000000-0000-4000-8000-000000000101',
  '20000000-0000-4000-8000-000000000301',1,
  '20000000-0000-4000-8000-000000000401',
  'workspace.inspect','workspace:configured:read',repeat('a',64),
  repeat('b',64),'ci-only',now()-interval '5 seconds',
  now()+interval '4 minutes','approved'),
 ('20000000-0000-4000-8000-000000000202',
  '20000000-0000-4000-8000-000000000102',
  '20000000-0000-4000-8000-000000000302',1,
  '20000000-0000-4000-8000-000000000401',
  'workspace.inspect','workspace:configured:read',repeat('a',64),
  repeat('b',64),'ci-only',now()-interval '5 seconds',
  now()+interval '4 minutes','approved'),
 ('20000000-0000-4000-8000-000000000203',
  '20000000-0000-4000-8000-000000000103',
  '20000000-0000-4000-8000-000000000303',1,
  '20000000-0000-4000-8000-000000000401',
  'workspace.inspect','workspace:configured:read',repeat('a',64),
  repeat('b',64),'ci-only',now()-interval '5 seconds',
  now()+interval '4 minutes','approved');
