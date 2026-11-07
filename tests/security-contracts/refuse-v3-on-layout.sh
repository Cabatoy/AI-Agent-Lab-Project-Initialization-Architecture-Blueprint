#!/usr/bin/env bash
set -euo pipefail
shopt -s nocasematch

branch='refactor/enterprise-workspace-layout-20261107-223711'
case "${GITHUB_EVENT_NAME:-}" in
  pull_request)
    if [[ -z "${GITHUB_HEAD_REF:-}" || -z "${GITHUB_REPOSITORY:-}" ]]; then
      echo '::error::Cannot identify the pull request branch and repository.' >&2
      exit 1
    fi
    if [[ "$GITHUB_HEAD_REF" == "$branch" ]]; then
      if [[ -z "${HIC_HEAD_REPOSITORY:-}" ]]; then
        echo '::error::Cannot verify the owner of the protected refactor branch.' >&2
        exit 1
      fi
      if [[ "$HIC_HEAD_REPOSITORY" == "$GITHUB_REPOSITORY" ]]; then
        echo '::error::Ledger v3 execution is prohibited for this workspace refactor.' >&2
        exit 1
      fi
    fi
    ;;
  workflow_dispatch)
    if [[ -z "${GITHUB_REF:-}" ]]; then
      echo '::error::Cannot identify the manually selected branch.' >&2
      exit 1
    fi
    if [[ "$GITHUB_REF" == "refs/heads/$branch" ]]; then
      echo '::error::Ledger v3 execution is prohibited for this workspace refactor.' >&2
      exit 1
    fi
    ;;
  *)
    echo '::error::Unexpected event; refusing ledger v3 execution.' >&2
    exit 1
    ;;
esac
echo 'PASS: event is outside the protected workspace-refactor exclusion.'
