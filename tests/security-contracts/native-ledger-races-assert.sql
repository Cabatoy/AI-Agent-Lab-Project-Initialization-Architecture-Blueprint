DO $check$
DECLARE n_claims integer; n_audit integer; n_drafts integer;
        task_version bigint; approval_state text;
BEGIN
 SELECT count(*) INTO n_claims FROM native_run_claims;
 SELECT count(*) INTO n_audit FROM native_run_audit WHERE event_code='claimed';
 SELECT count(*) INTO n_drafts FROM tasks WHERE status='Draft';
 SELECT native_run_revision INTO task_version FROM tasks
   WHERE id='20000000-0000-4000-8000-000000000102';
 SELECT state INTO approval_state FROM native_run_approvals
   WHERE id='20000000-0000-4000-8000-000000000201';

 IF n_claims <> 1 OR n_audit <> 1 OR n_drafts <> 3 OR
    task_version <> 2 OR approval_state <> 'revoked' THEN
   RAISE EXCEPTION 'Native run race invariant failed';
 END IF;
 IF EXISTS (SELECT 1 FROM native_run_claims
   WHERE run_id IN ('20000000-0000-4000-8000-000000000301'::uuid,
                    '20000000-0000-4000-8000-000000000302'::uuid)) THEN
   RAISE EXCEPTION 'Revoked or stale approval obtained a claim';
 END IF;
 IF NOT EXISTS (SELECT 1 FROM native_run_claims
   WHERE run_id='20000000-0000-4000-8000-000000000303'::uuid) THEN
   RAISE EXCEPTION 'Positive-control run claim was not recorded';
 END IF;
END
$check$;
