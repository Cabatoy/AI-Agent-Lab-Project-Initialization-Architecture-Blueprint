# Devam Noktası

Bu belge yeni bir checkout veya Copilot sohbeti için kısa ve kalıcı handoff kaynağıdır. Sohbet transcript'i değildir; doğrulanmış proje durumunu ve henüz çözülmemiş işleri taşır. Öneriler onaylanmış mimari karar değildir.

**Güncel R2 devam bağlantısı (6 Kasım 2026):**
[Ownership, staged harness ve A2.2 runtime checkpoint](../docs/human-in-control/R2_EXECUTION_OWNERSHIP.md#a22-izole-runtime-checkpoint--6-kasım-2026).
Aşağıdaki R1 durum/eksik/öneri kayıtları tarihsel checkpoint'tir; güncel R2
kanıtı ve sınırları için bu bağlantıyı esas alın. Bu not yeni runtime izni değildir.

## Depoyu İndirme

1. Depoyu klonlayın. VS Code'da repo kökünü **File > Open Folder** ile açın; Visual Studio'da repo kökündeki `AI-Agent-Lab.sln` dosyasını açın.
2. `AI-Agent-Lab.sln`, Control Plane API, Agent Worker, `run.me` console ve iki test projesi olmak üzere beş .NET projesini içerir. Solution Explorer'dan çalışacağınız projeyi seçin; `dotnet build AI-Agent-Lab.sln` tüm .NET projelerini derler.
3. Önce `.github/copilot-instructions.md`, bu handoff dosyası, `README.md`, karar/açık-soru/yol-haritası belgeleri ve kaynak proje yapısını salt okunur kontrol edin. `bin/` ve `obj/` çıktıları kaynak incelemesine dahil değildir.
4. Copilot talimatı VS Code'da repo ayarı olarak yüklenir. Visual Studio'da otomatik yükleme sürüme göre değişebileceği için Copilot Chat'ten talimat dosyasını açıkça okumasını isteyin.
5. Podman Desktop/Compose veya .NET 10 SDK kurulumu gerekiyorsa önce gereksinimleri konuşun. Podman kontrolü ve başlatma adımları birbirinden ayrıdır; [local onboarding](local-onboarding.md) içindeki onay kapılarını izleyin.

## Yeni Sohbetten Devam

Copilot Chat'e şu isteği gönderin:

> Bu repo için önce dosyaları salt okunur kontrol et; `.github/copilot-instructions.md` ve `docs-md/continuation.md` dosyalarını oku. Handoff'taki durumu mevcut repo ile karşılaştır, doğrulanmış gerçekleri ve önerileri ayır, sıradaki işi kısaca bildir. Kullanıcı ayrıca onay vermeden Podman komutu, build, ağ isteği veya dosya değişikliği yapma.

Sohbet geçmişi Git'e veya `run.me` durum dosyasına yazılmaz. Büyük bir işi bitirdikten sonra bu belge güncellenirse sonraki sohbet, eski konuşmayı aramadan projedeki devam noktasını bulabilir.

## Doğrulanmış Durum

- Repo, mimari belgeler ve Podman Compose ile çalışan local prototip içeriyor; AWS provisioning veya production deployment yapılmadı.
- Repo kökündeki [AI-Agent-Lab.sln](../AI-Agent-Lab.sln) beş .NET projesini bir araya getiriyor: `ControlPlane.Api`, `AgentWorker`, `RunMe`, `ControlPlane.Api.Tests`, `AgentWorker.Tests`.
- .NET 10 migration tamamlandı ve `feature/dotnet10-migration` dalında `699bd116bf49cbef0e8ac97d791f1b02b4c51305` (`build: migrate AI Agent Lab to .NET 10 LTS`) commit'i ile kaydedildi. Beş proje `net10.0` hedefliyor; API/Worker Dockerfile SDK/runtime image'ları 10.0 kullanıyor. Migration doğrulamasında restore, build, 23/23 test ve API/Worker/RunMe Release publish geçti; belge güncellemesinde yeniden çalıştırılmadı.
- Local stack Control Plane API, Agent Worker, PostgreSQL ve Redis servislerinden oluşuyor.
- `run.me` ve `run.me.ps1` önce dosya denetimini, sonra handoff'tan sohbeti sürdürmeyi yönlendiriyor; bu iki adımda Chat mesajı göstermekten öte bir şey yapmıyor. `approve-podman` onayından sonra salt okunur Podman durum kontrolünü (`podman --version`, `podman machine list`, `podman compose version`, `podman compose ps`) kendisi çalıştırıyor; sorun bulunursa ve kullanıcı `approve-setup` ile açıkça onaylarsa `scripts/local.ps1`/`scripts/local.sh new-build` komutunu kendisi başlatıyor. Chat mesajlarını `tools/run-me/prompts.tr.json` dosyasında `repo review`, `handoff check`, `podman check`, `setup repair` komutlarıyla veya doğrudan `step <n>` ile de gösterebilir.
- `run.me` ilerlemesi `.run-me-state.json` dosyasında yalnızca o checkout'ta tutulur ve Git tarafından yok sayılır. Başka makineye veya yeni clone'a taşınmaz; bu dosya sohbet bağlamı değildir.
- Worker task detayını Control Plane'den alır, prompt üretir ve opsiyonel model provider üzerinden çağrı yapar. Podman AI Lab için önce `chat/completions`, sonra `completions` fallback'i dener; geçici hatalarda retry uygular.
- `docker-compose.yml` içinde Worker için `MODEL_PROVIDER`, `MODEL_ENDPOINT`, `MODEL_NAME`, `MODEL_API_KEY`, `MODEL_TIMEOUT_SECONDS` ortam değişkenleri bulunur.
- `scripts/local.ps1` ve `scripts/local.sh` `bootstrap-dev` komutunu destekler. Bu komut stack kurulumunu, AI Lab alias bağlamasını (`ai-lab`), `.env` üretimini ve smoke test'i otomatikler. `-Reset`/`--reset` ile veri silerek temiz başlangıç yapılabilir. Etkileşimli akışta provider parametresi verilmezse servisler başladıktan sonra seçim açılır; `podman-ai-lab` seçildiğinde veya birden fazla servis varken `auto` kullanıldığında çalışan AI Lab servisleri arasından seçim yaptırılır.
- Önceki local prototip doğrulamasında `bootstrap-dev -LlmProvider podman-ai-lab` uçtan uca çalıştırıldı; stack ayağa kalktı ve smoke test (`task -> approve -> completed`) başarıyla geçti. Bu tarihsel sonuç, aşağıdaki .NET 10 stub doğrulamasından ayrıdır; gerçek model bağlantısı yeni checkpoint'te yeniden test edilmedi.
- Control Plane içinde Faz 2 planlama katmanı iskeleti eklendi (`/plans`, `/plans/generate`, `/plans/{id}/approve`, `execution_plans` tablosu).
- Transaction endpoint’lerinde bağlantı açık başlatma düzeltmesi (`await connection.OpenAsync()`) uygulanarak bootstrap sırasında görülen `/tasks/{id}/approve` 500 hatası giderildi.

## .NET 10 İzole Runtime Checkpoint — 6 Kasım 2026

- [İzole harness](../deploy/hic-runtime-test/README.md) yedi kaynak dosyasından oluşur: `.gitignore`, `README.md`, `compose.json`, `empty.env`, `runtime-test.ps1`, `stub/Dockerfile`, `stub/server.js`. Migration commit'i harness'i içermez; harness `01d4b88d1622bce734de0744eae7646ded47bb39` commit'i ile ayrıca kaydedildi. `.state/`, üretilen config ve test verileri Git'e dahil değildir.
- Proje: `hic-net10-76c713f9b481`; API: `127.0.0.1:18080`. Ayrı PostgreSQL/Redis volume'ları ve internal ağ kullanıldı; mevcut production kaynakları kullanılmadı. Model yalnızca deterministik stub idi; gerçek local/cloud LLM çağrısı yapılmadı.
- [Harness](../deploy/hic-runtime-test/runtime-test.ps1) `validate`, `preflight`, `readiness`, `persistence`, `worker` aksiyonları ayrı ayrı bir kez çalıştırıldı ve tümü `exit=0` ile geçti. PostgreSQL `SELECT 1`, Redis `PING`, stub/API health, task persistence ve API read-back doğrulandı.
- Tek sentetik task: `fb9f10c0-d530-4b5a-a153-59aa5f12cf5c`. İlk durum `Draft`, son durum `Completed`; beklenen ve gerçek sonuç `HIC_RUNTIME_TEST_OK`, `attemptCount=1`.
- PostgreSQL audit zinciri: `Draft → Approved → Executing → Completed`. Redis `agentlab:task-queue` ve `agentlab:task-queue:dead-letter` uzunlukları son kontrolde `0` idi.
- Worker önceden hazırlanmış `docker.io/library/hic-net10-76c713f9b481-agent-worker:latest` image'ından oluşturuldu; image runtime metadata'sı `10.0.14` olarak doğrulandı. Worker aksiyonu `up -d --no-build --no-recreate --no-deps agent-worker` kullanıyor; E2E sırasında rebuild/recreate yapılmadı.
- Aşağıdaki durumlar E2E sonundaki son gözlemdir; anlık durum garantisi değildir. Bu belge güncellemesinde Podman veya API çağrısı yapılmadı.

| Test servisi | Container ID (kısa) | Son gözlenen durum |
|---|---|---|
| PostgreSQL | `d3e6a801f091` | Running |
| Redis | `3e486570e754` | Running |
| Model stub | `b96c348354b0` | Running |
| Control Plane API | `a60a3ad408da` | Running |
| Agent Worker | `66f4a194bfd1` | Running |

İlk dört container'ın ID'leri korundu; yalnızca test Worker container'ı oluşturulup başlatıldı. Öncesindeki readiness kontrollerinde PostgreSQL, Redis ve stub healthy idi. Shutdown/cleanup yapılmadı; volume, ağ, test verisi ve saved state korundu. Saved state artık task ID'sini içeriyor; üretilmiş Compose config hash'i değişmedi. Aynı task için persistence/worker aksiyonlarını tekrar çalıştırmak güvenli veya desteklenen bir yeniden doğrulama olarak kabul edilmemelidir.

## R1 PostgreSQL Integration Checkpoint — 6 Kasım 2026

- R1 uygulama/test checkpoint'i: `6448ae1ad126e037f7309906f905679af7859d13` (`fix(reliability): enforce atomic transitions and safe worker execution`).
- R1, approve/retry/status mutasyonlarını ortak [TaskTransitionStore](../agent-lab/control-plane/TaskTransitionStore.cs) üzerinden aynı PostgreSQL transaction'ında satır kilidi, mevcut duruma koşullu update ve audit insert ile uygular. Opsiyonel `expectedStatus` uyuşmazlığı veya aynı-status tekrarı `409 Conflict` döndürür; geçersiz geçiş `400`, bulunamayan task `404` olarak kalır.
- Worker yalnızca HTTP `200` ve eşleşen task ID/status cevabıyla `Executing` kabulünü doğrularsa model çağırır. Conflict, timeout ve eksik/bozuk cevap execution'ı engeller; belirsiz sonuç otomatik `Failed` yapılmaz. Bu protokol durable ownership veya exactly-once sağlamaz.
- [PostgreSQL fixture](../tests/ControlPlane.Api.Tests/TaskTransitionPostgresTests.cs), dedicated database/owner/marker doğrulaması ve GUID schema/token/OID kontrolü kullanır; cleanup yalnızca kendi iki tablosu ve schema'sında transaction/lock/`RESTRICT` ile yapılır.
- Mevcut runtime projesinden ayrı kaynaklar oluşturuldu: container `hic-r1-pg-10a8cae2ef06` (`312a9bef6457`), volume `hic-r1-pg-10a8cae2ef06-data`, ağ `hic-r1-pg-10a8cae2ef06-net`; yalnızca `127.0.0.1:18432` portu açıldı. Image `postgres:16-alpine`; database `hic_r1_tests`, owner `hic_r1_test_runner`, database comment `HIC:R1:dedicated-integration:v1`. Rol superuser/CREATEROLE/CREATEDB yetkilerine sahip değil. Credential bu belgede veya repoda tutulmaz.
- Gerçek PostgreSQL üzerinde dört integration testi geçti: başarılı state/audit commit, geçersiz geçişte değişmezlik, concurrent claim-benzeri state geçişlerinden yalnızca birinin kabulü ve audit insert hatasında state rollback. Bu, lease/claim protokolü testi değildir.
- Restore ve Release build geçti: `0 warning/error`. Release tam suite: **77 passed, 0 failed, 0 skipped** (API 48, Worker 29; dört integration testi dahil). Önceki 23/23 baseline tarihsel olarak korunur.
- Fixture schema'larının cleanup sonrası sayısı `0`; dedicated container/volume/ağ silinmedi veya durdurulmadı. Mevcut runtime container ID/start-time/mount bilgileri ve repo dosya hash'leri test boyunca değişmedi. Uygulama verilerine test yazımı yapılmadı; runtime verilerinin kapsamlı içerik karşılaştırması yapılmadı.
- Çalışan API/Worker container'ları R1 koduyla rebuild/recreate edilmedi. Önceki stub E2E sonucu R1 sonrası container E2E kanıtı değildir. Harness README'sindeki “henüz çalıştırılmadı” ifadesi tarihsel hazırlık aşamasına aittir; doğrulanmış sonuçlar bu checkpoint'tedir. Yeni projede Worker aksiyonu önceden build edilmiş proje image'ı gerektirir; `start` yalnızca API/stub image'larını build eder.

## Açık Güvenilirlik ve Production Hazırlığı Eksikleri

- Bu checkpoint yalnızca izole, deterministik happy-path'i doğrular; production hazırlığı, gerçek model davranışı veya dayanıklılık kanıtı değildir.
- Worker Redis mesajını destructive pop ile alıyor; in-flight durum process-local. Durable claim/ack, lease ve crash sonrası recovery henüz yok.
- PostgreSQL state commit'i ile Redis enqueue atomik değil; outbox/reconciliation ve idempotent recovery tasarımı gerekiyor.
- R1 status-update kabul kapısı ve cancellation aktarımı deterministic testlerle doğrulandı; model retry sınıflandırması/attempt raporlaması hâlâ ayrıca incelenmeli. Retry, cancellation, failure injection ve crash recovery container runtime'da doğrulanmadı.
- Önceki test container'larının toplu kapanmasını başlatan aktör bilinmiyor; PostgreSQL normal fast-shutdown ile kapanmıştı. Sonraki scoped recovery bu nedeni açıklamaz.
- Planlama API'si iskelet düzeyinde; dependency graph, topolojik scheduling ve rejection/replanning tamamlanmış kabul edilmemeli.
- HIC governance/approval sınırları ve provider-neutral routing hedefleri, bu runtime başarısıyla uygulanmış sayılmaz. AWS provisioning ve production deployment yapılmadı.

## Önerilen Devam Sırası

Yeni checkout'ta önce dosya denetimini ve handoff karşılaştırmasını tamamlayın; yerel runtime state veya dedicated test credential'ının taşındığını varsaymayın. R1 DB integration kapısı geçti; önerilen tek sonraki mühendislik işi **R1 sonrası izole container E2E doğrulaması** için mevcut verileri koruyan kapsamı hazırlayıp açık runtime onayı almaktır. R2 lease/heartbeat/recovery uygulanmadı; bunlar için ayrıca uygulama onayı gerekir. Bu öneri yeni runtime işlemi veya feature geliştirme izni değildir.

Kullanıcı açıkça izin verirse yalnızca salt okunur Podman durum kontrollerini yapın; machine durmuşsa başlatmadan raporlayıp durun. Bu checkpoint bootstrap, restart, yeni task, retry, shutdown veya cleanup izni vermez. Mevcut test verisini koruyun; runtime değişikliği için ayrıca açık onay alın.