-- These assertions run as owner of a throwaway PostgreSQL database only.
DO $check$
DECLARE claims int; events int; drafts int; run_id_seen uuid;
BEGIN
 SELECT count(*) INTO claims FROM native_run_claims;
 SELECT count(*) INTO events FROM native_run_audit WHERE event_code = 'claimed';
 SELECT count(*) INTO drafts FROM tasks
  WHERE id = '10000000-0000-4000-8000-000000000101'
    AND status = 'Draft' AND native_run_revision = 1;
 SELECT run_id INTO run_id_seen FROM native_run_claims LIMIT 1;
 IF claims <> 1 OR events <> 1 OR drafts <> 1 OR
    run_id_seen IS DISTINCT FROM '10000000-0000-4000-8000-000000000301'::uuid THEN
   RAISE EXCEPTION 'Atomic claim/audit/task invariant failed';
 END IF;
 IF NOT EXISTS (SELECT 1 FROM app_schema_versions WHERE version = 3) THEN
   RAISE EXCEPTION 'Native ledger v3 version missing';
 END IF;
END
$check$;
CREATE ROLE hic_untrusted NOLOGIN;
DO $check$
BEGIN
 IF has_function_privilege('hic_untrusted',
  'hic_try_claim_native_readonly_run(uuid,uuid,text)', 'EXECUTE') THEN
   RAISE EXCEPTION 'Untrusted role inherits native claim privilege';
 END IF;
END
$check$;
DROP ROLE hic_untrusted;
