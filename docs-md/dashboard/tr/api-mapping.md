# HIC #79 — Otoriter API ve yetki matrisi

## Yönetici Özeti

Bu envanter **mevcut kod kanıtı / birleşmemiş sözleşme / veri boşluğu** ayrımıdır;
yeni endpoint veya yetki tanımlamaz. [IA ve responsive önerisi](dashboard-design.md)
ile [test fixture'ı](../dashboard-contract.json) birlikte okunur.
Browser `/api` önekini kullanır; aşağıdaki yollar Control Plane yollarıdır.
Baseline yerel, loopback prototiptir; `/tasks`, `/plans`, `/health` ve
`/diagnostics/local` operator/tenant authorization uygulamaz. Bu, cloud veya
multi-user yayın izni değildir. `X-HIC-Request` tek başına kimlik doğrulama değildir.

## Teknik Ek

### İncelenen sabit kanıtlar

- **D:** [develop App](https://github.com/Cabatoy/AI-Agent-Lab-Project-Initialization-Architecture-Blueprint/blob/bcfa97679e235f75483135d19a64654228fecc27/src/WebUI/src/App.tsx),
  [Program/API](https://github.com/Cabatoy/AI-Agent-Lab-Project-Initialization-Architecture-Blueprint/blob/bcfa97679e235f75483135d19a64654228fecc27/src/ControlPlane/Program.cs#L53)
  ve [client](https://github.com/Cabatoy/AI-Agent-Lab-Project-Initialization-Architecture-Blueprint/blob/bcfa97679e235f75483135d19a64654228fecc27/src/WebUI/src/api.ts#L355).
- **W / #59:** [App](https://github.com/Cabatoy/AI-Agent-Lab-Project-Initialization-Architecture-Blueprint/blob/0601242279d3a55bd39a2fc278ea5c0df429cf0e/src/WebUI/src/App.tsx),
  [GeneralPlans](https://github.com/Cabatoy/AI-Agent-Lab-Project-Initialization-Architecture-Blueprint/blob/0601242279d3a55bd39a2fc278ea5c0df429cf0e/src/ControlPlane/GeneralPlans.cs#L45)
  ve [LocalModelStatus](https://github.com/Cabatoy/AI-Agent-Lab-Project-Initialization-Architecture-Blueprint/blob/0601242279d3a55bd39a2fc278ea5c0df429cf0e/src/WebUI/src/LocalModelStatus.tsx).
- **B / #61:** [Draft detay → bounded Git workflow](https://github.com/Cabatoy/AI-Agent-Lab-Project-Initialization-Architecture-Blueprint/blob/c8df5725beb981bb3e9438d44a366dcae7899ccb/src/WebUI/src/GitRunnerWorkflow.tsx);
  UI bağlantısıdır; genel executor veya yeni global approval API değildir.
- **G / #66:** [GitEditEndpoints](https://github.com/Cabatoy/AI-Agent-Lab-Project-Initialization-Architecture-Blueprint/blob/ff4337578df67b1905e52abd664b5548efcbc9de/src/ControlPlane/GitEditRunner.cs#L289)
  ve [durable decision store](https://github.com/Cabatoy/AI-Agent-Lab-Project-Initialization-Architecture-Blueprint/blob/ff4337578df67b1905e52abd664b5548efcbc9de/src/ControlPlane/GitPlanDecisions.cs#L46).
- **I / #73:** [Issue preview/import](https://github.com/Cabatoy/AI-Agent-Lab-Project-Initialization-Architecture-Blueprint/blob/07a49f9d511ad7dfd55b9f258f759568061e7046/src/ControlPlane/GitHubIssuePreview.cs#L25),
  [publisher reads](https://github.com/Cabatoy/AI-Agent-Lab-Project-Initialization-Architecture-Blueprint/blob/07a49f9d511ad7dfd55b9f258f759568061e7046/src/ControlPlane/GitHubPublisherEndpoints.cs#L32),
  [session](https://github.com/Cabatoy/AI-Agent-Lab-Project-Initialization-Architecture-Blueprint/blob/07a49f9d511ad7dfd55b9f258f759568061e7046/src/ControlPlane/LocalHealthRunner.cs#L557)
  ve [proxy](https://github.com/Cabatoy/AI-Agent-Lab-Project-Initialization-Architecture-Blueprint/blob/07a49f9d511ad7dfd55b9f258f759568061e7046/src/WebUI/vite.proxy.ts#L38).
- **R / #76/#78:** [salt okunur resource/Phase 2 politikası](https://github.com/Cabatoy/AI-Agent-Lab-Project-Initialization-Architecture-Blueprint/blob/fa8dd6e1f78fd1d6dee3d241d9c336d576e41e74/scripts/hic-phase2-policy.ps1);
  web endpoint'i değildir.

### Mevcut read yüzeyi

Contract ID'leri fixture ile aynıdır. **O** = geçerli operator oturumu;
**L** = yalnız yerel erişim sınırı, operator/tenant izolasyonu yok.
“Stacked” yalnız ilgili PR kabulü ve deployed schema/capability doğrulaması
sonrası kullanılabilecek demektir. Kaynak snapshot'ı deployment kanıtı değildir.

| ID | Method / Control Plane yolu | Yetki / kaynak | Otoriter alanlar ve sınırlama |
|---|---|---|---|
| `health` | GET `/health` | L / D | `status`, `service`; DB/Worker probe değil |
| `tasks` | GET `/tasks` | L / D | `id,title,status,attemptCount,result,errorMessage,createdAtUtc,updatedAtUtc`; tam liste, pagination yok |
| `task` | GET `/tasks/{id:guid}` | L / D | Aynı task ayrıntısı; 404 silinmiş/bulunamadı |
| `plans` | GET `/plans` | L / D | `id,projectKey,requestTitle,status,planSummary` ve zamanlar; execution plans, general plans değil |
| `diagnostics` | GET `/diagnostics/local` | L / D | `checkedAtUtc,controlPlane,postgres,redis,worker=not-checked`; bellek ölçmez |
| `session` | GET `/local-runner/session` | O / I stacked | `actorId,expiresAtUtc,expiresInSeconds`; read restore, login değil |
| `general-conversations` | GET `/local-runner/general-conversations` | O / W stacked | Actor'a bağlı konuşma özeti; pending karar feed'i değil |
| `general-plan` | GET `/local-runner/general-conversations/{id:guid}/plan` | O / W stacked | `revision,source,createdAtUtc,tasks,approved,rejected,taskIds`; boş plan 404 |
| `git-decisions` | GET `/local-runner/tasks/{taskId:guid}/git-decisions` | O / G stacked | `decisionId,actorId,action,scope,intentDigestSha256,decision,decidedAtUtc,runId,plan`; en çok 50, `hasMore` ve `limit=50` |
| `run` | GET `/local-runner/runs/{runId:guid}` | O / I stacked | Actor'a bağlı run, plan/result/events; task state'ini değiştirmez; bilinmeyen ID ile keşif yok |
| `model` | GET `/local-runner/model` | O / W stacked | `configured,provider,model,timeoutSeconds,readiness`; envanter isteği prompt göndermez; listelenmek generation garantisi değil |
| `github-status` | GET `/local-runner/github/status` | O / I stacked | `configured,repository,missing` ve opsiyonel revision bilgisi; bağlantı/CI sağlığı kanıtı değil |
| `publication` | GET `/local-runner/github/publications/{id:guid}` | O / I stacked | Bilinen actor publication'ında `state,plan,commitSha,remoteSha,prNumber,prUrl,errorCode,updatedAtUtc,events`; GitHub canlı PR/CI durumu değil |
| `issue-preview` | POST `/local-runner/github/issues/preview` | O / I stacked | `{reference}` → repo/issue sayısal ID, title/body, `updatedAtUtc,revisionSha256`; provider GET, task yazmaz |

`git-decisions` yalnız geçmiş **verilmiş** kararları listeler; `hasMore=true`
sonuçları tam liste sayılmaz. Plan içindeki `revision=1`, protokol revision'ıdır;
task revizyonu/digest ve exact plan ayrı doğrulanır. Pending global inbox,
actor'lar arası karar taraması veya total approval count endpoint'i yoktur.
General plan GET'i approval actor/timestamp audit feed'i döndürmez. Birleşik
provenance gösterimi için ayrıca gözden geçirilmiş read contract gerekir.

Model readiness: `not-configured`, `not-checked`, `available`, `unavailable`,
`model-not-listed`; UI bilinmeyen değeri başarılıya çeviremez. Kontrol
konfigüre yerel provider `/v1/models` envanteridir; remote LLM, token,
gerçek maliyet ve latency istatistiği değildir.

### Mevcut ama Dashboard refresh'inde yasak olan insan adımları

| ID | Method / Control Plane yolu | Payload ve gerçek yan etki |
|---|---|---|
| `general-plan-approve` | POST `/local-runner/general-conversations/{id:guid}/plan/approve` | `{expectedRevision}`; W/O, exact revision ile yalnız Draft task'lar oluşturur |
| `general-plan-reject` | POST `/local-runner/general-conversations/{id:guid}/plan/reject` | `{expectedRevision}`; W/O, ret kaydı, run değil |
| `git-preview` | POST `/local-runner/tasks/{taskId:guid}/git-preview` | `{repositoryId,baseCommit,file,content}`; G/O, actor/task-bound sunucu preview kaydı oluşturur; salt read değildir |
| `git-approve` | POST `/local-runner/tasks/{taskId:guid}/git-approve` | `{plan,intentDigestSha256}`; G/O, durable karar + run rezervasyonu; 202 execution veya publish değildir |
| `git-reject` | POST `/local-runner/tasks/{taskId:guid}/git-reject` | Aynı exact preview; G/O, durable ret; `runId=null` |
| `issue-import-preview` | POST `/local-runner/github/issues/import-preview` | `{reference,revisionSha256,title,description}`; I/O, beş dakikalık actor-bound DB preview, task değil |
| `issue-import-confirm` | POST `/local-runner/github/issues/import-previews/{id:guid}/confirm` | Boş gövde; I/O, kaynak revision tekrar doğrulaması; atomik Draft/dedup/audit; `{taskId,replayed}` |

#73 yalnız kurulu/yetkili **public** repo issue'sunu destekler; private repo
issue isteğinden önce reddedilir. Issue metni güvenilmez içeriktir; otomatik
prompt veya talimat yapılmaz. Kaynak yeniden okunur fakat GitHub okuması ile
yerel commit atomik değildir. Mevcut broker publisher kapsamlı credential
kullanır; yalnız GET göndermek onu ayrı read-only credential yapmaz.
Dashboard'a bu credential verilmez; daraltılmış broker ayrı kapıdır.
Publication GET yerel kayıttır; branch/PR canlı ilerleme ve CI head SHA
kontrolü için yeni, yetkili read-only feed gerekir.

`POST /tasks/{id}/approve`, retry/status/claim, plan approve, local
run execute, model message/generate ve GitHub approve/execute/reconcile/
complete-draft bu tasarımın otomatik veya yeni Dashboard action yüzeyine
**dahil değildir**. “Preview” adının read-only olduğu varsayılmaz:
publisher preview DB'ye yazar ve provider çağrısı yapabilir.

### Veri boşlukları ve izin kapıları

| Widget | Bugünkü kanıt | Önerilen sınır |
|---|---|---|
| `resources` | CLI `resource status` / `phase2-plan`; HTTP yok | Shell çalıştırma yok; ayrı auth'lu read-only bridge tasarımı bekle |
| `usage` | Token/fiyat/harcama ledger'ı yok | Unknown; para birimi, fiyat tarihi ve provider ölçümü olmadan tutar gösterme |
| `agents` | Kalıcı agent registry yok | “Henüz bağlanmadı”; task sayısından agent türetme |
| `decisions` | Seçili task geçmişi / ayrı general plan | Global inbox/provenance eksik; boş inbox/0 pending iddiası yok |
| `github` | Issue preview + yerel publication | Canlı issue listesi/branch status/PR lifecycle/CI yok; unknown |

Memory gelecekte `source,scope,measuredAt,value,unit,availability` taşımalıdır;
host/VM/container/process ayrı, tahmin etiketi açık; toplamları alınmaz.
Her yeni adapter için auth, redaction, iptal/timeout ve private repo testleri
zorunlu öneridir. Hiçbir boşluk bu PR'da implementation veya migration ile
kapatılmaz. [English counterpart](../en/api-mapping.md).
