// Read-only assessment of an already cryptographically verified dev proposal.
// HIC's current GET /tasks/{id} lacks native_run_revision and authenticated
// issuer binding. Even a matching Draft row is NOT permission to claim or run.
const UUID = /^[0-9a-f]{8}-[0-9a-f]{4}-[1-8][0-9a-f]{3}-[89ab][0-9a-f]{3}-[0-9a-f]{12}$/;

export async function inspectExistingTask(review, {readTask}={}) {
  if (review?.status !== 'accepted-for-review' || review?.executionAllowed !== false || review?.ledgerVerified !== false || !UUID.test(review?.taskId || '')) throw new Error('invalid-reviewed-order');
  if (typeof readTask !== 'function') return {status:'blocked', reason:'ledger-unavailable', executionAllowed:false};
  let task;
  try {task = await readTask(review.taskId)} catch {return {status:'blocked',reason:'ledger-read-failed',executionAllowed:false};}
  if (!task || task.id !== review.taskId) return {status:'blocked',reason:'task-not-found-or-mismatch',executionAllowed:false};
  if (task.status !== 'Draft') return {status:'blocked',reason:'task-not-draft',executionAllowed:false};
  if (!Number.isSafeInteger(task.nativeRunRevision) || task.nativeRunRevision < 0) return {status:'blocked',reason:'authoritative-task-revision-unavailable',executionAllowed:false};
  // A task revision by itself is insufficient: authorization, actor identity,
  // signed grant, claim and native v3 migration are not presently wired.
  return {status:'observed-draft-unverified',reason:'native-claim-and-issuer-not-wired',taskId:review.taskId,executionAllowed:false};
}
