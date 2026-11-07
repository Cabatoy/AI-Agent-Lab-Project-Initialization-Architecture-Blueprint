-- Synthetic, isolated CI ONLY. The real HIC runtime never invokes this script.
-- Prerequisites: disposable PostgreSQL, fresh schema v2, manually-applied v3.
INSERT INTO tasks(id,title,description,status,attempt_count,created_at_utc,updated_at_utc)
VALUES ('00000000-0000-4000-8000-000000000101','synthetic CI task','not user data',
        'Draft',0,now(),now());

INSERT INTO native_run_approvals(
 id,task_id,run_id,task_revision,actor_id,action,scope,intent_digest,
 receipt_digest,key_id,approved_at_utc,expires_at_utc,state)
VALUES (
 '00000000-0000-4000-8000-000000000201',
 '00000000-0000-4000-8000-000000000101',
 '00000000-0000-4000-8000-000000000301',1,
 '00000000-0000-4000-8000-000000000401',
 'workspace.inspect','workspace:configured:read',repeat('a',64),
 repeat('b',64),'synthetic-ci-issuer',now()-interval '5 seconds',
 now()+interval '2 minutes','approved');

DO $verify$
DECLARE v_first uuid; v_replay uuid; v_count integer; v_revision bigint;
BEGIN
  v_first := hic_try_claim_native_readonly_run(
    '00000000-0000-4000-8000-000000000201',
    '00000000-0000-4000-8000-000000000501',repeat('a',64));
  IF v_first IS DISTINCT FROM '00000000-0000-4000-8000-000000000301'::uuid THEN
    RAISE EXCEPTION 'First valid claim was not recorded';
  END IF;

  v_replay := hic_try_claim_native_readonly_run(
    '00000000-0000-4000-8000-000000000201',
    '00000000-0000-4000-8000-000000000502',repeat('a',64));
  IF v_replay IS NOT NULL THEN RAISE EXCEPTION 'A replay unexpectedly claimed the same run'; END IF;

  SELECT count(*) INTO v_count FROM native_run_claims;
  IF v_count <> 1 THEN RAISE EXCEPTION 'Exactly one durable claim was expected'; END IF;
  SELECT count(*) INTO v_count FROM native_run_audit WHERE event_code='claimed';
  IF v_count <> 1 THEN RAISE EXCEPTION 'Exactly one claim audit was expected'; END IF;

  SELECT native_run_revision INTO v_revision FROM tasks
    WHERE id='00000000-0000-4000-8000-000000000101';
  IF v_revision <> 1 THEN RAISE EXCEPTION 'New task native revision should begin at 1'; END IF;
END
$verify$;

-- Construct untrusted grants exclusively for negative testing. No authenticated
-- approval issuer exists yet; these inserts do not grant runtime permissions.
INSERT INTO native_run_approvals(
 id,task_id,run_id,task_revision,actor_id,action,scope,intent_digest,
 receipt_digest,key_id,approved_at_utc,expires_at_utc,state,revoked_at_utc)
VALUES
 ('00000000-0000-4000-8000-000000000202',
  '00000000-0000-4000-8000-000000000101',
  '00000000-0000-4000-8000-000000000302',1,
  '00000000-0000-4000-8000-000000000401',
  'workspace.inspect','workspace:configured:read',repeat('a',64),
  repeat('b',64),'ci',now()-interval '5 seconds',now()+interval '2 minutes','approved',null),
 ('00000000-0000-4000-8000-000000000203',
  '00000000-0000-4000-8000-000000000101',
  '00000000-0000-4000-8000-000000000303',1,
  '00000000-0000-4000-8000-000000000401',
  'workspace.inspect','workspace:configured:read',repeat('a',64),
  repeat('b',64),'ci',now()-interval '3 minutes',now()-interval '1 minute','approved',null),
 ('00000000-0000-4000-8000-000000000204',
  '00000000-0000-4000-8000-000000000101',
  '00000000-0000-4000-8000-000000000304',1,
  '00000000-0000-4000-8000-000000000401',
  'workspace.inspect','workspace:configured:read',repeat('a',64),
  repeat('b',64),'ci',now()-interval '1 minute',now()+interval '2 minutes','revoked',now());

-- Any task mutation must invalidate older approval revision.
UPDATE tasks SET title='changed synthetic task'
WHERE id='00000000-0000-4000-8000-000000000101';

DO $verify$
DECLARE v_revision bigint; v_locked boolean := false; v_count integer;
BEGIN
  SELECT native_run_revision INTO v_revision FROM tasks
   WHERE id='00000000-0000-4000-8000-000000000101';
  IF v_revision <> 2 THEN RAISE EXCEPTION 'Task update must bump approval revision'; END IF;

  IF hic_try_claim_native_readonly_run(
       '00000000-0000-4000-8000-000000000202',
       '00000000-0000-4000-8000-000000000501',repeat('a',64)) IS NOT NULL
  THEN RAISE EXCEPTION 'Stale task revision was accepted'; END IF;

  IF hic_try_claim_native_readonly_run(
       '00000000-0000-4000-8000-000000000203',
       '00000000-0000-4000-8000-000000000501',repeat('a',64)) IS NOT NULL
  THEN RAISE EXCEPTION 'Expired approval was accepted'; END IF;

  IF hic_try_claim_native_readonly_run(
       '00000000-0000-4000-8000-000000000204',
       '00000000-0000-4000-8000-000000000501',repeat('a',64)) IS NOT NULL
  THEN RAISE EXCEPTION 'Revoked approval was accepted'; END IF;

  BEGIN
    DELETE FROM native_run_audit WHERE
      run_id='00000000-0000-4000-8000-000000000301';
  EXCEPTION WHEN raise_exception THEN v_locked := true;
  END;
  IF NOT v_locked THEN RAISE EXCEPTION 'Append-only audit accepted a deletion'; END IF;

  SELECT count(*) INTO v_count FROM native_run_claims;
  IF v_count <> 1 THEN RAISE EXCEPTION 'Denied claims unexpectedly mutated persistent state'; END IF;
  SELECT count(*) INTO v_count FROM tasks WHERE status='Draft';
  IF v_count <> 1 THEN RAISE EXCEPTION 'Native read-only claim changed task/Worker state'; END IF;
END
$verify$;

-- Test wrong digest on a current revision independently from stale grants.
INSERT INTO native_run_approvals(
 id,task_id,run_id,task_revision,actor_id,action,scope,intent_digest,
 receipt_digest,key_id,approved_at_utc,expires_at_utc,state)
VALUES (
 '00000000-0000-4000-8000-000000000205',
 '00000000-0000-4000-8000-000000000101',
 '00000000-0000-4000-8000-000000000305',2,
 '00000000-0000-4000-8000-000000000401',
 'workspace.inspect','workspace:configured:read',repeat('a',64),
 repeat('b',64),'ci',now()-interval '5 seconds',
 now()+interval '2 minutes','approved');

DO $verify$
BEGIN
 IF hic_try_claim_native_readonly_run(
      '00000000-0000-4000-8000-000000000205',
      '00000000-0000-4000-8000-000000000501',repeat('c',64)) IS NOT NULL
 THEN RAISE EXCEPTION 'Mismatched intent digest accepted'; END IF;
 IF hic_try_claim_native_readonly_run(
      '00000000-0000-4000-8000-000000000205',
      '00000000-0000-0000-0000-000000000000',repeat('a',64)) IS NOT NULL
 THEN RAISE EXCEPTION 'Invalid runner identity accepted'; END IF;
END
$verify$;
