# Relay Approval Smoke Test #2

## Purpose

This document describes the second development-only smoke test for the GitHub issue → Relay watcher → VS Code Copilot Agent → commit → push → Draft PR workflow. The test objective is to determine whether an automated relay-launched session can complete the entire workflow to Draft PR publication **without requiring manual user approval** at any step.

This builds on smoke test #1 (#139 / Draft PR #140) by explicitly verifying that permission inheritance and approval gates work as expected in a relay-launched context.

## Permission Model: Interactive Chat vs. Relay Sessions

### Interactive Chat Permissions
When a user manually opens VS Code Chat and interacts with Copilot:
- **User decides**: VS Code displays permission prompts (e.g., "Allow Copilot to access your workspace files?")
- **User clicks**: The user explicitly grants or denies each permission
- **Session inherits**: Once granted, permissions persist for that interactive session
- **Observable**: Prompts are visible and user actions are logged by VS Code

### Relay-Launched Session Permissions
When Relay watches a GitHub issue and launches a VS Code Copilot Agent to work on it:
- **Programmatic launch**: No interactive user prompt appears in VS Code
- **Inheritance question**: Does the relay-launched session inherit the user's previously-granted interactive Chat permissions?
- **Configuration unclear**: User may have configured interactive Chat permissions, but it is **not established** that relay sessions automatically inherit them
- **Evidence requirement**: Only GitHub-observable commit/PR creation and explicit local permission logs can confirm zero-manual-approval execution

## Evidence Needed

To credibly claim **zero-manual-approval execution**, we need both:

1. **GitHub-Observable Evidence** (authoritative):
   - Issue → Relay watcher activation logged
   - Relay launches Copilot Agent session
   - Agent completes task logic and creates a commit
   - Commit is pushed to the task branch
   - Draft PR is opened against `develop` with issue reference
   - All steps recorded in GitHub Actions logs or Relay event log

2. **Local Permission Prompts** (supplementary, not independently observable):
   - Any permission prompt that appears during relay session execution
   - Explicit documentation of whether user manually approved or dismissed prompts
   - **Qualifier**: "local permission prompts are not independently observable from GitHub and cannot be used alone to claim zero-manual-approval"

## Test Constraints

This smoke test is **bounded and development-only**:
- Tests **only** the workflow flow, not security controls
- Creates **only** this markdown file; no other tracked files change
- Does **not** modify VS Code settings, permission policies, Relay implementation, workflows, or runtime environment
- Does **not** attempt approval bypass or automatic clicking
- Respects existing organizational policies and VS Code permission gates

## Expected Outcome

If the workflow completes to Draft PR without user intervention:
- Evidence is recorded in GitHub (commit hash, PR link)
- This smoke test confirms the relay → agent → draft PR flow can operate unattended
- Remaining question: whether interactive Chat permission inheritance is the mechanism, or whether relay uses a different permission model

If a manual approval prompt appears:
- The session stops and waits for human decision
- No automated workaround is attempted
- The blocker is reported in the issue

## Related Issues
- #139: Relay smoke test #1 (initial workflow validation)
- #140: Draft PR from smoke test #1
