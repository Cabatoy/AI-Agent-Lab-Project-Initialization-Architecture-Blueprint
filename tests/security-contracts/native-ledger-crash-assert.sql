-- Post-crash durable state of an ephemeral PostgreSQL CI instance.
DO $check$
DECLARE n_claims integer; n_audit integer; drafts integer;
BEGIN
 SELECT count(*) INTO n_claims FROM native_run_claims;
 SELECT count(*) INTO n_audit FROM native_run_audit WHERE event_code='claimed';
 SELECT count(*) INTO drafts FROM tasks WHERE status='Draft';

 IF n_claims <> 1 OR n_audit <> 1 OR drafts <> 2 THEN
   RAISE EXCEPTION 'Crash recovery violated claim/audit/task invariants';
 END IF;
 IF NOT EXISTS (
   SELECT 1 FROM native_run_claims
   WHERE run_id='30000000-0000-4000-8000-000000000301'::uuid
 ) THEN
   RAISE EXCEPTION 'Committed run claim disappeared after crash';
 END IF;
 IF EXISTS (
   SELECT 1 FROM native_run_claims
   WHERE run_id='30000000-0000-4000-8000-000000000302'::uuid
 ) OR EXISTS (
   SELECT 1 FROM native_run_audit
   WHERE run_id='30000000-0000-4000-8000-000000000302'::uuid
 ) THEN
   RAISE EXCEPTION 'Uncommitted run claim/audit was not rolled back';
 END IF;
 IF NOT EXISTS (
   SELECT 1 FROM native_run_approvals
   WHERE id='30000000-0000-4000-8000-000000000202'::uuid AND state='approved'
 ) THEN
   RAISE EXCEPTION 'Synthetic approval was improperly changed by rollback';
 END IF;
END
$check$;
