# HIC Agent Bridge — B0

## Yönetici Özeti

**Motto: Humans are not message queues.** Bridge, insanın sürekli sonuç kopyalamasını azaltmayı hedefler; insan onayını kaldırmaz. Mevcut AI Agent Lab Control Plane tek task orchestration otoritesidir. Bridge ince, provider-neutral erişim/governance adapter'ıdır; yeni task engine, scheduler, model router veya queue değildir.

Track A güvenilirlik ile Track B interaction paraleldir. İlk önerilen kanal, kişisel GitHub reposunda **read-only, asenkron, kompakt snapshot/handoff** paylaşımıdır. Bu bir canlı ChatGPT entegrasyonu veya onaylanmış ürün seçimi değildir. B0 yalnız dokümantasyondur; B1–B4 ayrıca onay gerektirir.

**Sonraki B1.1 uygulama checkpoint'i:**
[yerel snapshot foundation](./B1_SNAPSHOT_FOUNDATION.md) ayrı Track B worktree'sinde
uygulandı. Aşağıdaki B0 durum/öneri kayıtları tarihsel tasarım bağlamıdır;
B1.1 GitHub yayını veya canlı API/agent entegrasyonu sağlamaz.

[B1.2 publisher checkpoint'i](./B1_GITHUB_PUBLISHER.md), exact digest onayı ve
yerel fake-remote doğrulamasını ekler; gerçek GitHub publication/ChatGPT erişimi
bu checkpoint'te de çalıştırılmamıştır.

## Teknik Ek

### B1.3 ürün yönü

[Ürün vizyonu](./PRODUCT_VISION.md): HIC, kendi self-hosted Web UI'siyle
işletilecek provider-neutral AI Software Factory hedefidir. ChatGPT/Copilot
opsiyonel kanallardır. B1.1/B1.2 uygulanmış, offline doğrulanmıştır; gerçek
GitHub snapshot yayını ve ChatGPT okuması doğrulanmamıştır. Aşağıdaki B0
operation/API örnekleri tasarımdır; Web UI, routing ve otomatik delegation
planlanmıştır. Snapshot Control Plane otoritesi veya execution ownership değildir.

### 1. Doğrulanan temel ve tekrar kullanılacak yüzeyler

6 Kasım 2026 kaynak incelemesi ve [önceki checkpoint](../../docs-md/continuation.md):

| Mevcut yüzey | Yeniden kullanım / sınır |
|---|---|
| [Control Plane](../../src/ControlPlane/Program.cs): `GET /tasks`, `GET /tasks/{id}` | Task durumu/result/attempt/timestamp projection'ı; project scope, pagination ve durable execution kimliği yok |
| `POST /tasks`, `/tasks/{id}/approve`, `/retry`, `/status` | Mevcut tek task engine; B1 bu mutasyonları sunmaz |
| [TaskTransitionStore](../../src/ControlPlane/TaskTransitionStore.cs) | R1 row lock + transaction/audit, expected-state ve conflict; kalıcı sahiplik değil |
| `/plans`, `/plans/generate`, `/plans/{id}/approve` | Metadata tabanlı plan iskeleti; graph/versioned human approval/replanning yok |
| [Worker](../../src/AgentWorker/Program.cs) | Yalnız doğrulanmış Executing cevabıyla model; mevcut adapter isimleri aynı OpenAI-compatible client'a gider |
| PostgreSQL task transition audit | Kaynak DB'de mevcut; public history endpoint'i yok. B1 DB erişim yetkisi otomatik kazanmaz |
| Model output | Task `result` alanında metin; artifact registry, doğrulama kanıtı deposu ve usage telemetry yok |
| GitHub/MCP | Tracked Actions workflow, MCP server/SDK veya bridge kodu bulunmadı; `run.me` Chat yönlendirmesi orchestration entegrasyonu değildir |

.NET 10 beş proje, önceki Release build ve 77/77 test (4/4 gerçek PostgreSQL integration) checkpoint'te doğrulandı. Eski stub E2E R1 kodundan öncedir; A1 bekliyor. Bu tur build/test/runtime veya dış API çağrısı yapılmadı.

### 2. Amaç, sınırlar ve veri akışı

```text
Yetkili insan / dış agent client
  -> kanal adapter (ilk öneri: GitHub snapshot)
  -> Bridge contract validation + principal/project scope + redaction
  -> Control Plane read API veya gelecekte onaylı proposal/dispatch API
  -> mevcut task state/Redis/Worker
  -> compact result + yetkili artifact/evidence referansları
```

- PostgreSQL/Control Plane task gerçeğini taşır; Redis bildirim/dispatch'tir. Bridge GitHub issue durumunu task durumu gibi yorumlamaz.
- B1 sonucu okur; proje task allowlist'i ve kanal delivery metadata'sı yeni task tablosu/state machine değildir.
- Snapshot `observedAtUtc`, source revision ve freshness taşır. Gecikmiş snapshot canlı durum veya yeni execution yetkisi sayılmaz.
- GitHub snapshot private repoya da yazılsa dış veri aktarımıdır; yalnız açıkça onaylanan repo, alan ve visibility kullanılır.
- Local-first prototip bir doğrulama seçeneğidir; uzun vadede yayıncı/gateway kişisel PC/ev ağının zorunlu bağımlılığı olamaz. Cloud hosting/provisioning bu fazda seçilmez.

### 3. Sürümlü JSON contract taslağı

Alanlar camelCase; sürüm `hic.bridge/1.0`. Bunlar taslak JSON örnekleridir; mevcut API request/response değildir. Bilinmeyen major veya desteklenmeyen operation fail-closed reddedilir. Minor genişlemeler ancak explicit capability ile; bilinmeyen approval/security alanı sessizce yok sayılmaz.

#### Ortak request zarfı

```json
{
  "contractVersion": "hic.bridge/1.0",
  "operation": "GetExecutionResult",
  "projectId": "project-demo",
  "taskId": "11111111-1111-4111-8111-111111111111",
  "correlationId": "22222222-2222-4222-8222-222222222222",
  "executionId": null,
  "agent": {
    "agentId": "architect-agent",
    "provider": "independent",
    "sessionRef": null
  },
  "idempotencyKey": null,
  "parameters": {
    "include": ["summary", "verification"]
  }
}
```

`agent` audit beyanıdır; yetki kimliği değildir. Gateway bunu doğrulanmış principal/client ile bağlar; token veya sohbet transcript'i envelope'a girmez. `correlationId` izleme içindir, dedupe anahtarı değildir. `executionId` durable execution kaydı yokken `null` kalır; task ID execution ID gibi yeniden adlandırılmaz.

#### Operation parametreleri ve sonuçları

Ortak zarftaki `operation` ve `parameters` şu taslaklarla değişir:

```json
{
  "GetProjectStatus": {
    "request": {"cursor": null, "limit": 20, "ifRevision": null},
    "result": {
      "revision": "snapshot-ref",
      "taskCounts": {"Completed": 1},
      "nextCursor": null,
      "source": "control-plane-projection"
    }
  },
  "GetExecutionResult": {
    "request": {"include": ["summary", "verification"]},
    "result": {"executionStatus": "Completed", "resultRef": "artifact:result-1"}
  },
  "SubmitTaskProposal": {
    "request": {
      "title": "Bounded task",
      "descriptionRef": "artifact:brief-1",
      "expectedProjectRevision": "snapshot-ref",
      "requestedCapabilities": ["repository.read"]
    },
    "result": {"proposalId": "proposal-1", "approvalStatus": "pending", "taskId": null}
  },
  "RequestExecution": {
    "request": {
      "proposalId": "proposal-1",
      "approvalRef": "approval-1",
      "expectedTaskStatus": "Draft",
      "scopeDigest": "sha256:example",
      "budgetRef": "budget-1"
    },
    "result": {"requestId": "request-1", "disposition": "pendingApproval", "executionId": null}
  }
}
```

Bu gösterimde operation başlıkları şema kataloğudur; wire format dört çağrı değil tek ortak envelope'dur. Sayısal limit örnektir, ölçülmüş token hedefi veya onaylanmış varsayılan değildir.

| Operation | Kimlik / izin | Davranış |
|---|---|---|
| GetProjectStatus | `projectId`; `taskId`/`executionId` null; `project.status.read` | Yalnız yetkili task projection; mevcut project registry yokken server-side onaylı task allowlist'i. Tüm task listesini project gibi sunmaz |
| GetExecutionResult | `projectId` + `taskId`; execution ID varsa doğrulanır; `execution.result.read` | Null execution ID ile yalnız task'ın güncel sonucu; per-attempt sonuç iddiası yok. Caller belirli execution ID isterse destek yokken `CAPABILITY_UNAVAILABLE` |
| SubmitTaskProposal | `projectId`, mutasyon idempotency key; `proposal.write` | Proposal kaydı; tek başına task yaratmaz/onaylamaz. Kalıcı governance kaydı B3 işi |
| RequestExecution | Onaylı proposal/task bağlamı, mutasyon key; `execution.request` | Sunucu approval kaydını, scope/revision/budget'ı doğrular; ancak sonra mevcut engine'e devreder. Agent bu çağrıyla insan olamaz |

#### Ortak result zarfı

```json
{
  "contractVersion": "hic.bridge/1.0",
  "operation": "GetExecutionResult",
  "projectId": "project-demo",
  "taskId": "11111111-1111-4111-8111-111111111111",
  "correlationId": "22222222-2222-4222-8222-222222222222",
  "executionId": null,
  "agent": {"agentId": "architect-agent", "provider": "independent", "sessionRef": null},
  "requestStatus": "ok",
  "executionStatus": "Completed",
  "observedAtUtc": "2026-11-06T10:00:00Z",
  "sourceRevision": "snapshot-ref",
  "capabilities": {"durableExecutionIdentity": false, "cancellation": false},
  "summary": "Task result available",
  "artifactReferences": [],
  "verificationEvidence": [],
  "usage": {
    "measurement": "unknown",
    "inputTokens": null,
    "outputTokens": null,
    "cost": null,
    "currency": null,
    "pricingVersion": null
  },
  "approval": {"required": false, "status": "notRequired", "reference": null},
  "error": null
}
```

- `requestStatus` bridge operation sonucudur; task state machine değildir. Değerler: `ok`, `pendingApproval`, `rejected`, `error`. `executionStatus` mevcut task status'ünü aynen taşır veya task yoksa `null` olur.
- Artifact: `{ "artifactId", "uri", "sha256", "mediaType", "sizeBytes", "accessScope" }`. URI credential/signed token içermez; repo/commit/path gibi izinli kalıcı referanstır. Content ayrıca yetkilendirilir; SSRF/redirect ve path traversal denetlenir.
- Evidence: `{ "kind", "status", "sourceRef", "sourceCommit", "observedAtUtc", "scope" }`. Test için `PASS/FAIL/NOT_RUN`, çalıştırılan sürüm ve komut kapsamı bulunur. Completed ile test PASS eşit değildir; kanıt yoksa boş dizi/NOT_RUN.
- Usage provider bildiriyorsa `actual`, hesaplanan tahminse `estimated` ve yöntemi, bilinmiyorsa `unknown/null`. Ücretsiz/local çalışma sıfır maliyet diye uydurulmaz; retry toplamı ayrıca scope'lanır.

### 4. Idempotency, authorization, audit, hata ve cancellation

- B1: scoped read token, varsayılan deny; proje+task eşleşmesi ve artifact izinleri her çağrıda kontrol edilir. ProjectId client beyanına güvenilmez. Bugünkü API'de authentication middleware doğrulanmadı; doğrudan public expose edilmez.
- B3+: mutasyon key kapsamı `(principal, projectId, operation, idempotencyKey)`; canonical body digest ile kalıcı kaydedilir. Aynı key/aynı içerik aynı operation sonucunu döndürür; farklı içerik `IDEMPOTENCY_CONFLICT`. Teslim kaydı task engine değildir.
- Dispatch belirsizse başarılı/başarısız uydurulmaz; request ID ile read/reconciliation gerekir. R1 `409` tek başına “benim isteğim commit oldu” kanıtı değildir. Dual-write boşluğu A4'ten önce otomatik retry ile gizlenmez.
- İnsan onayı server-side principal, scope digest, task/plan sürümü, expiry, budget ve eyleme bağlanır. Propose, approve, execute, merge ve production deploy ayrı yetkilerdir. Read-only token approve/retry/status endpoint'lerine erişemez.
- Audit: correlation/request ID, doğrulanmış principal ve agent beyanı, operation/project/task/execution, source revision, karar/policy/approvalRef, sonuç/hata kodu, UTC zaman ve redacted digest. Sır/prompt/ham log ve chain-of-thought tutulmaz.
- Hata şekli: `{ "code": "STATE_CONFLICT", "retryable": false, "outcome": "rejected", "message": "Safe summary" }`. Kodlar: `INVALID_REQUEST`, `UNSUPPORTED_VERSION`, `UNAUTHORIZED`, `FORBIDDEN`, `NOT_FOUND`, `STATE_CONFLICT`, `IDEMPOTENCY_CONFLICT`, `APPROVAL_REQUIRED`, `CAPABILITY_UNAVAILABLE`, `RATE_LIMITED`, `DEPENDENCY_UNAVAILABLE`, `OUTCOME_UNKNOWN`.
- HTTP adapter önerisi: sırasıyla validation 400, auth 401/403, hidden/missing resource 404, conflict 409, capability 501, rate limit 429, dependency 503. Approval bekleyen kabul edilmiş request 202'dir; execution kabulü değildir. Başka transport aynı hata kodlarını korur.
- `OUTCOME_UNKNOWN` mutasyonu otomatik tekrar ettirmez; `retryable=false` ve status lookup gerekir. Read retry bounded/backoff; timeout ve rate limits explicit policy'dir.
- B1 cancellation yalnız read isteğini keser; task iptal etmez. Task cancellation ayrı onaylı command ve Control Plane transition/Worker acknowledgement gerektirir; şimdilik capability false. Timeout uzak modelin durduğu anlamına gelmez.

### 5. Entegrasyon seçenekleri ve fizibilite

**Doğrulama sınırı:** Kullanıcı bu tur dış API çağrısını yasakladı. Aşağıdaki değerlendirme mimari karşılaştırmadır; 6 Kasım 2026 için hesap uygunluğu, SDK sürümü/kararlılığı, fiyat veya connector desteği doğrulanmadı. Resmi bağlantılar sonraki B2 incelemesi içindir; bu tur ziyaret edilmedi.

| Yol | Kişisel hesap/auth | Maliyet ve exposure | Güvenlik/onay | Efor/vendor/token |
|---|---|---|---|---|
| GitHub asenkron handoff | Hedef kişisel private repo erişimi; öneri GitHub App veya ince kapsamlı token; org/SSO şartları ayrıca kontrol | API/Actions kota ve maliyetleri doğrulanmalı; snapshot publisher için outbound HTTPS, inbound HIC endpoint gerekmez | İzinli repo/branch/artifact; approval belirli digest'e bağlanır, comment otomatik onay değildir | Düşük başlangıç; repo/manifest taşınabilir; yalnız diff/summary gönderilir |
| Secured HIC MCP server | MCP client desteği ve kişisel ChatGPT ürün/planı ayrıca doğrulanır; remote auth/OAuth tasarımı gerekir | Hosting/egress/plan şartları; uzak client için erişilebilir TLS endpoint/gateway gerekebilir, local-only seçenek client'a bağlı | Read/write tool ayrımı, project scope, prompt injection ve tool approval; MCP tek başına auth değildir | Orta-yüksek; ortak protocol, client policy bağımlılığı; filtreli tool sonucu verimli |
| OpenAI API Architect Agent | Ayrı API projesi/key/service identity ve veri politikası gerekir | API tüketimi için ayrı bütçe/onay; ChatGPT aboneliğinin API kullanımını karşıladığı varsayılmaz; outbound yeterli olabilir | Model önerir, deterministik kod authorize eder; tool invocation insan onayını geçemez | Orta; provider adapter ile kilitlenme azaltılır; context/cache/usage ancak doğrulanmış destekle |
| Copilot SDK/CLI | Resmi desteklenen API/CLI, kişisel lisans ve automation koşulları doğrulanır | Plan/kota/model ve unattended kullanım şartları belirsiz; yerel runner/cloud alternatifleri ayrı değerlendirilir | Araç allowlist ve sandbox; şirket lisansı dış programatik kullanım izni sayılmaz | Sürüm/lisans riski; model ve ortam bağımlılığı; otomatik tam repo bağlamından kaçınılır |

Bir OpenAI API key mevcut ChatGPT sohbetini okuyamaz varsayımıyla tasarlanır: böyle bir sohbet erişimi garanti edilmez. API agent'ı kendi açık, erişim kontrollü bağlamına sahiptir. GitHub snapshot'ın oluşması ChatGPT'nin onu kendiliğinden okuduğu anlamına gelmez; connector, yetkili client veya başlangıçta insanın referans sunması gerekir.

**İlk yol önerisi:** GitHub-mediated **read-only** handoff. B1 önce fixture/mock ile compact snapshot üretir; canlı repo yayını ayrı onayla yapılır. Otomatik Actions workflow, bot veya ChatGPT connector bu fazda kurulmaz. Böylece public local endpoint gerektirmeden veri sözleşmesi denenir; sonrasında B2 gerçek hesapta uygun okuma mekanizmasını doğrular.

Sonraki resmi doğrulama kaynakları:
- [GitHub authentication](https://docs.github.com/en/authentication) ve [Actions billing](https://docs.github.com/en/billing/concepts/product-billing/github-actions)
- [MCP specification](https://modelcontextprotocol.io/specification/latest)
- [OpenAI API documentation](https://platform.openai.com/docs) ve [ChatGPT yardım merkezi](https://help.openai.com/)
- [GitHub Copilot SDK](https://github.com/github/copilot-sdk) ve [Copilot belgeleri](https://docs.github.com/en/copilot)

### 6. Güvenlik ve token ekonomisi

- Model çıktısı, repo metni, issue/comment ve artifact güvenilmez input'tur; içindeki araç talimatları yetki sayılmaz. Shell çalıştırma veya URL fetch implicit değildir.
- Yetkiler minimum kapsamlı, kısa ömürlü ve server-side doğrulanır; credential JSON/log/issue içine yazılmaz. GitHub yayınında secret scan ve visibility gate gerekir.
- Cevaplar bounded summary, cursor ve changed-since/revision ile verilir. Tam repo, transcript, terminal logu veya ham model yanıtı varsayılan yük değildir.
- Hassas sonuç redaction sonrası da sınıflandırılır; private repo her veri türü için güvenli hedef kabul edilmez. Saklama, revocation ve artifact erişimi ayrıca tasarlanır.
- Read/proposal doğrulaması deterministiktir; LLM gerektirmez. Model route'u mevcut adapter sınırında kalır; Bridge paid fallback açmaz.
- Payload byte limit, pagination, allowed fields ve rate limit B1 contract testlerinde ölçülür; token tasarrufu yüzdesi uydurulmaz. Önerilen ilk test sınırı response başına 16 KiB, sayfa başına en fazla 20 task'tır; ürün kararı değildir. Aşımda içerik sessiz kesilmez; ref/cursor veya açık hata döner.

### 7. Rollout, bağımlılıklar ve kabul ölçütleri

| Faz | Önkoşul / kabul | Gerekli test |
|---|---|---|
| B0 | Taslak bu belge; dört operation ve açık kapsam; implementasyon yok | JSON parse, yerel link/scope/secret/Markdown kontrolleri |
| B1 | Onaylı project/task eşlemesi; mevcut task read projection; kimlik doğrulama; yalnız read | Fake Control Plane; denied project/cross-task, redaction, freshness, max byte/page limit, unknown execution ID, no mutating HTTP/model calls |
| B2 | Kişisel hesabın resmi destek/izin/ücret veri politikası doğrulanmış | Sentetik non-sensitive snapshot; auth revoke/expiry; erişim yokken açık hata; otomatik sohbet erişimi varsayımı yok |
| B3 | Kalıcı proposal+approval kaydı; scope/version; P5 ve P6 gereken dilimleri | Replay/idempotency, ret, stale approval, agent-as-human reddi, no-dispatch-before-approval |
| B4 | A2 ownership/fencing, A3 lease/heartbeat ve A4/A5 gereken safety kapıları; B2/B3 | Duplicate handoff, crash/unknown dispatch, stale owner, cancellation ve budget; dış yan etkide exactly-once iddiası yok |

B1 için A2 lease beklemek gerekmez; executionId null ve capability sınırı açıklanır. A1 önceliği korunur. B3 proposal toplamak ile B4 otomatik execution aynı kapı değildir; gerçek delegation reliability tamamlanmadan açılmaz.

**Açıkça ertelenenler:** Agent Bridge/MCP kodu, yeni endpoint/şema, GitHub workflow/bot/App kurulumu, yeni dashboard, paid API, mevcut sohbet belleğine erişim, otomatik approval/merge/deploy, genel artifact store ve provider router. Bu belge R2 veya cloud provisioning izni değildir.

### 8. Açık kararlar ve çalışma düzeni

Project registry/allowlist sahibi, human identity, approval saklama/retention, artifact store, read API pagination ve audit projection'ı açık. Execution ID Track A ile tek canonical kimlik olarak tasarlanmalı; [mevcut `runId` taslağıyla](./INTEGRATION_CONTRACTS.md) iki kimlik motoru oluşturulmamalı.

[Roadmap](./ROADMAP.md) iki ayrı branch/worktree önerir; hiçbir branch/worktree bu tur oluşturulmadı. Ortak dosyalarda tek entegrasyon sahibi ve contract review gerekir. B1 canlı sonuçta erişilemeyen veriyi DB bypass ile tamamlamaz; unsupported capability açıkça döner.

**Tek önerilen sonraki uygulama görevi:** A1 — R1 sonrası izole container E2E doğrulaması; image build, izole test kaynakları ve sentetik task yazımları için ayrı açık onay. B1 contract/mock hazırlığı ancak ayrı onaylanan paralel kapsamla ilerler.
