# Free Cloud APIs and Local Specialist Model Pool

## Executive Summary

**Status:** Feature discovery proposal, 8 November 2026. No implementation, provider selection, account creation, runtime operation or paid usage is authorized by this document.

Evaluate a pool of free cloud endpoints and small local specialist models for HIC. Extend existing [P6–P8](../../ROADMAP.md) and [token economy](../../TOKEN_ECONOMY.md); retain the existing task engine and approval boundaries. The [Turkish evaluation specification](../tr/HYBRID_MODEL_POOL_EVALUATION.md) contains the full discovery checklist, acceptance matrix and source register.

Proposed initial comparison: Groq, an accessible NVIDIA Nemotron endpoint, one small local coding model and one local embedding model. Gemini and OpenRouter are additional candidates. Measure verified completed tasks per quota and elapsed time, rather than nominal free tokens.

## Technical Appendix

### Candidate snapshot

Documentation checked on 8 November 2026; live API access and account quotas have not been tested. Recheck availability, region, data terms and limits before any pilot.

| Candidate | Qualification |
|---|---|
| NVIDIA Nemotron | Legacy Nano 30B-A3B free endpoint is marked Deprecated; Nano Omni reasoning free endpoint is marked Available. Trial service, without an assumed permanent quota. |
| Groq | Free plan with model/organization request and token limits. |
| Gemini API | Free tier for selected models; not every model qualifies. |
| OpenRouter | Free variants: 20 RPM, initially 50 RPD; at least USD 10 cumulative credit purchase raises the daily cap to 1,000. The higher allowance requires initial payment. |
| Qwen2.5-Coder 7B/14B | Local coding baseline candidates, not a claim of current best quality. |
| EmbeddingGemma or measured alternative | Local retrieval/indexing specialist, not a coding generator. |

Model, API provider and inference server are separate concepts. Ollama and llama.cpp are runtime candidates. OpenAI-compatible HTTP does not guarantee compatible tool calls, structured outputs, streaming or cancellation. Specialization should begin with task instructions, repository retrieval, bounded tools and evaluation; fine-tuning is outside the first scope.

### Integration and control

Reuse current Worker/provider configuration after a fresh code review; do not infer implemented capabilities from historical documents. Proposed endpoint metadata includes model/provider, secret reference, locality, verified capabilities, context/output limits, concurrency, quota source/check date, data policy and local/free-tier/trial/paid classification.

Use deterministic code where possible and authorized adequate local models first. Every cloud route, including free endpoints, requires an explicit data policy. Paid fallback defaults to disabled. Provider changes cannot broaden data permissions.

Handle 429 with bounded backoff and Retry-After; wait visibly through the existing engine or select an already authorized capable alternative. Do not retry configuration/authentication failures indefinitely or repeat uncertain tool side effects through another provider. Preserve ownership, idempotency and approval boundaries.

NVIDIA trial terms allow input/output recording and product/model improvement. Review every provider's terms before sending repository context. Keep credentials, raw prompts and private reasoning out of telemetry.

### Local baseline and discovery

Developer hardware baseline: Ryzen 9 PRO 8945HS, 64 GB RAM, Radeon 780M. Start with one small coding model and one embedding model; memory fit is not evidence of usable speed. Measure quantization, context/KV cache, cold/warm latency, memory and concurrency. Ollama and llama.cpp Vulkan are candidates; acceleration is unverified. Keep current Podman lifecycle rules. Local hardware must not become a mandatory cloud dependency.

1. Review current provider implementation and existing P6–P8 work.
2. Verify candidate access, quotas, data terms, trial expiry and tool/JSON capabilities.
3. Prepare 12 synthetic tasks on a fixed commit: three summary/classification, three retrieval, three small code fixes and three multistep tool tasks.
4. Compare supported tasks with equivalent context/tool budgets; separately record unsupported tasks.
5. Use mock providers to verify 429, timeout, cancellation, malformed JSON, missing usage and disabled paid fallback.
6. Produce a Go/No-go/limited-pilot recommendation and a separate implementation backlog.

### Acceptance and open decisions

Record task success, reviewed patch/build/test results, valid tool calls, schema compliance, false completion, latency, cold/warm behavior, memory, retries and total requests/tokens. Mark usage actual/estimated/unknown. All control tests must pass: no unauthorized cloud or paid requests, no local-only data export, bounded cancellation/retry and no duplicate side effects.

Set numerical quality/speed thresholds before the experiment. No benchmark outcome is claimed here. Decide permitted cloud projects, zero-payment versus speed/quality priorities, whether a credit purchase is acceptable, and wait-versus-authorized-fallback behavior.

Fine-tuning, multiple large local models, quota circumvention, production rollout and changes to the coding lifecycle priority are outside scope. Primary-source links are maintained in the [full source register](../tr/HYBRID_MODEL_POOL_EVALUATION.md#8-birincil-kaynaklar).
