# HIC Web UI — Yerel kontrol paneli

## Yönetici özeti

Bu yerel HIC ekranı, mevcut Control Plane'in task ve plan bilgilerini gösterir.
**Command Center** ekranından doğal dilde iş tanımı yazılarak yalnızca **Draft**
durumunda task oluşturulur. **Local Runner**, yapılandırılmış yerel operatör
oturumu ve manuel schema v4 kurulumu mevcutsa, Draft task için yalnızca sabit
`system.health` eylemini gösterir, açık onay alır ve güvenli sonucu aynı ekranda
gösterir. Task Worker kuyruğuna girmez; Worker, model, shell veya repository
değişikliği kullanılmaz. Control Plane tek task ve run otoritesidir; bu UI
ayrı bir task deposu ya da scheduler eklemez. Bu dar akış tam P9 approval
workflow veya production readiness değildir. **Planning** ekranı Native Host'un tek yapılandırılmış
workspace'ini salt okunur kontrol eder ve yalnız tarayıcı belleğinde sürümlü plan
önizlemesi üretir; bu plan Control Plane'e yazılmaz ve çalıştırılamaz. Genel
plan için kimliği doğrulanmış onay ve kalıcı audit uygulanmamıştır; yalnız
sabit `system.health` için dar local runner otoritesi vardır. R3/R4 recovery ve
tam P9 approval workflow hâlâ açıktır.

## Teknik ek

### Önkoşullar ve çalıştırma

- Node.js 20.19+ veya 22.12+ ve npm.
- Ayrı olarak, önceden çalıştırılmış ve yalnız yerel erişime uygun biçimde
  yapılandırılmış Control Plane. UI mevcut stack'i başlatmaz veya değiştirmez.
- Control Plane erişilemiyorsa UI yine açılır ve hataları gösterir.

Bu klasörde Windows PowerShell, macOS veya Linux terminalinden:

```text
npm ci
npm run dev
```

Tarayıcı: **http://127.0.0.1:5173**. Port kullanımda ise sunucu otomatik başka
porta geçmek yerine hata verir. Durdurmak için terminalde Ctrl+C kullanın.
Geliştirme sunucusu yalnız `127.0.0.1` adresine bağlanır.

### API yapılandırması

Varsayılan API adresi `http://127.0.0.1:8080`'dir. Başka bir yerel port için
sunucu başlamadan önce `HIC_API_URL` ayarlayın:

```powershell
$env:HIC_API_URL = 'http://127.0.0.1:18080'
npm run dev
```

```bash
HIC_API_URL=http://127.0.0.1:18080 npm run dev
```

Yalnız HTTP, tam IPv4 loopback adresi `127.0.0.1` ve port kabul edilir;
credential, path, query, hostname ve uzak adresler reddedilir. Bu ayar yalnız
Vite geliştirme sürecinde kullanılır, browser bundle'ına konmaz. Podman
machine IP'si veya uzak bir API bu sürümde desteklenmez; güvenli bir loopback
erişimi ayrıca mevcut değilse altyapıyı değiştirmeden API erişimi kullanılamaz.

Geliştirme proxy'si yalnız şu GET isteklerini iletir: `/health`, `/tasks`,
`/tasks/{guid}` ve `/plans`. `POST /api/tasks` yalnız Draft oluşturur. Local
Runner için yalnız session açma, capability/intent/run okuma, exact intent
onayı ve allowlist edilmiş run başlatma yolları eklenmiştir. Diğer yöntemler,
yollar (ör. Worker `/approve` ve `/retry`) ve query string'ler reddedilir;
cookie ve istenmeyen istemci başlıkları upstream'e aktarılmaz. Tüm `/api`
isteklerinde `Host` başlığı `127.0.0.1:<port>` olmalıdır (DNS rebinding
koruması); başka bir `Origin` ile gelen GET'ler reddedilir.

`POST /api/tasks` yalnız şu koşullarda iletilir:

- `Origin` tam olarak `http://127.0.0.1:<port>`, `Sec-Fetch-Site` yok veya
  `same-origin` ve `X-HIC-Request: command-center` başlığı mevcut (CSRF
  koruması).
- `Authorization`/`Proxy-Authorization` yok; `Content-Type: application/json`,
  sıkıştırılmamış gövde, en fazla 64 KiB, geçerli UTF-8 JSON.
- Gövde yalnız `title` (zorunlu, en fazla 200 karakter) ve `description`
  (zorunlu, en fazla 8.000 karakter) alanlarını içerir; proxy bu iki alanı
  yeniden serialize ederek iletir.
- Ret yanıtları `text/plain` ve `X-HIC-Proxy-Error: rejected` başlığıyla döner;
  kullanıcı girdisi yanıtta tekrarlanmaz.

Upstream ve gövde okuma zaman aşımı 8 saniyedir; bağlantı kurulamazsa 502
(`unreachable`), bağlantı yarıda kesilirse 502 (`interrupted`), zaman aşımında
504 döner. Bu proxy genel bir authentication veya authorization mekanizması
değildir; Control Plane'i doğrudan erişime karşı korumaz. Genel Control Plane
API'si kimlik doğrulamasızdır; API ve UI loopback dışına açılmamalıdır. Local
Runner'a özel API ayrıca `HIC_LOCAL_OPERATOR_TOKEN` ile açılan 15 dakikalık
operator session'ı ister ve actor kimliğini `HIC_LOCAL_OPERATOR_ID` üzerinden
bağlar. Bootstrap token yalnız yerel proxy üzerinden gönderilir, browser
storage'a veya task/audit kayıtlarına yazılmaz. Session token yalnız sayfa
belleğinde tutulur.

Local Runner için Control Plane ortamında en az 32 kriptografik rastgele
byte'ın Base64 değeri olan `HIC_LOCAL_OPERATOR_TOKEN` ve sabit, boş olmayan
GUID `HIC_LOCAL_OPERATOR_ID` ayrıca yapılandırılmalıdır. Bu token'ı repoya,
terminal kaydına veya issue'lara yazmayın. Control Plane schema v4'ü otomatik
uygulamaz; [004-local-health-runner.sql](../../src/ControlPlane/Migrations/004-local-health-runner.sql)
yalnız açıkça onaylanmış disposable test veritabanında ve
`psql --single-transaction --set ON_ERROR_STOP=1 --file ...` ile atomik olarak
uygulanmalıdır. v4 yoksa
session ve runner endpoint'leri fail-closed şekilde kullanılamaz. Çalışan
Control Plane'i yeniden başlatmak veya herhangi bir mevcut veritabanına
migration uygulamak için ayrı açık onay gerekir.

Run onayı task ID, `updated_at_utc` sürümü, `system.health` action'ı,
`control-plane:health:read` scope'u ve SHA-256 intent hash'ine bağlanır. DB'de
tek task için tek run, queued -> running claim'i ve append-only audit event'leri
vardır; tekrar onayı aynı run'ı döndürür ve tekrar çalıştırmaz. Audit, yalnız
actor/run ID, sabit event kodu ve zaman tutar; tamamlanmış veya başka nedenle
çalıştırılamayan run'ı yeniden çalıştırma girişimi `rejected` event'i ekler.
Sonuçta yalnız Control Plane,
PostgreSQL, Redis ve Worker için güvenli durum kategorileri saklanır; ham
exception, connection string, prompt veya credential tutulmaz. Run sırasında
Worker kuyruğu kullanılmaz.

### Isolated Git — Issue #42

**Yönetici özeti:** `Isolated Git` ekranı yalnız önceden açıkça yetkilendirilmiş,
atılabilir test reposunda `message.txt` dosyasını deterministik olarak değiştirir.
Draft task → repo/base commit seçimi → SHA-256 bağlı plan önizlemesi → kimliği
doğrulanmış açık onay → ayrı Git worktree → gerçek diff ve exact-content/scope
test sonucu aynı ekrandadır. Model, shell, Worker ve otomatik task commit/push/
PR/merge/deploy yoktur. Kaynak checkout dosyaları ve HEAD değişmez; Git worktree
kaydı ve task branch'i Git metadata'sına eklenir.

**Teknik ek:** Control Plane için `HIC_DISPOSABLE_GIT_REPOSITORIES`, en fazla dört
`{ "id": "...", "path": "...", "baseCommit": "40-haneli-kucuk-harf-SHA" }`
kaydından oluşan JSON array'dir. `path` mutlak ve kanonik olmalı; ağ deposu,
normal müşteri reposu veya kaynak geliştirme checkout'u burada yetkilendirilmez.
`HIC_GIT_RUN_ROOT` önceden oluşturulmuş, kaynak repo dışında ayrı bir yerel
dizindir. Bu alanlar varsayılan olarak boş/kapalıdır; yanlış yapılandırma
fail-closed olur. Bu ayarlar runtime izni değildir.

Test reposunun commit ağacı yalnız iki plain `100644` dosya içerir:
`.hic-disposable` (tam içerik: `HIC disposable test repository v1` ve newline)
ve `message.txt`. Checkout temiz olmalı, HEAD yapılandırılmış SHA ile eşleşmelidir.
LF ve CRLF kabul edilir. İçerik en fazla 512 karakter, printable ASCII ve newline
olabilir; sır yazmayın. Başka dosya, path traversal, symlink/junction, attributes,
submodule, alternates veya yürütülebilir Git config reddedilir. Git global/system
config, hooks, external diff/textconv ve protokoller devre dışıdır; subprocess
zamanı ve çıktısı sınırlandırılır. Bu doğrulama hostile yerel kullanıcıya karşı
OS sandbox yerine geçmez.

Manuel [005-isolated-git-runner.sql](../ControlPlane/Migrations/005-isolated-git-runner.sql)
v4 run/audit otoritesini genişletir; yeni bir task state machine yaratmaz.
v2 → v4 → v5 yalnız ayrı, açıkça yetkilendirilmiş disposable veritabanında
`psql --single-transaction --set ON_ERROR_STOP=1` ile denenir. Startup migration
uygulamaz. Mevcut HIC veritabanına migration veya canlı runtime'a deployment
için ayrı insan onayı gerekir.

Plan hash'i domain/action/scope, revision, task sürümü, actor, kanonik repo ID/path,
immutable SHA, tek dosya, tam payload ve en fazla beş dakikalık expiry'yi kapsar.
Önizleme değişince UI onayı siler; onaylandıktan sonra plan immutable'dır.
Bir task yalnız bir local-run kararına ayrılır; başka plan yeni Draft task ister.
Task `Draft` kalır; run `completed`/`failed` sonucu ayrı gösterilir. Denied
execution başarı gibi raporlanmaz. Audit yalnız sabit event kodları, ID ve zaman
tutar; plan/diff/test artefaktı ayrı run kaydındadır. Ham exception veya token
saklanmaz.

Run ID ile restart sonrası kimlik doğrulayıp durum/diff/audit okunabilir.
Commit edilmiş claim'den sonra process ölürse run `running`/unknown kalır;
otomatik retry, recovery, yeniden execution veya cleanup yapılmaz. Tamamlanmış
ve in-flight run replay'i reddedilir. Worktree'ler manuel inceleme için korunur.
Genel regression/chaos backfill, hostile eşzamanlı filesystem saldırıları,
platform matrisi ve production izolasyonu bu ilk dilimde tamamlanmış sayılmaz;
execution runtime'ını merge/deploy etmeden önce ikinci iterasyonda incelenmelidir.

**Güvenlik sınırı:** Bu yerel geliştirme aracı production için değildir.
Kullanılan Control Plane portunun ayrıca yalnız loopback'e bağlı ve yetkisiz
ağ erişiminden korunmuş olması gerekir. Depodaki genel Compose port mapping'i
bu güvenceyi tek başına sağlamayabilir; bu UI onu değiştirmez. Geliştirme
sunucusunu `--host 0.0.0.0` ile açmayın, ters proxy ile internete sunmayın ve
tarayıcıya secret koymayın. Güvenilmeyen yerel kullanıcılar ve tarayıcı
eklentileri de yerel verilere erişebilir. Production için kimlik doğrulama,
yetkilendirme, TLS, izolasyon ve audit ayrıca tasarlanmalıdır.

### Kontroller

```text
npm run build
npm test
```

Build TypeScript kontrolünü ve Vite üretim bundle'ını oluşturur. Bundle tek
başına API proxy'si içermez; desteklenen yerel kullanım `npm run dev`'dir.
Testler sentetik yanıtlarla ve sahte upstream sunucusuyla çalışır; gerçek task\nveya altyapı oluşturmaz.

### Mevcut ekranlar ve sınırlar

- **Planning:** Native Host'tan yalnızca yapılandırılmış workspace için başarılı
  read-only Git checkout kontrolü alındıktan sonra bu tek repository seçilebilir.
  Plan, yerel deterministik bir şablondur; içerik, sürüm ve SHA-256 hash içerir.
  Provider/Worker durumları için probe yapılmaz; provider'lar doğrulanana kadar
  hazır sayılmaz. Plan önizleme olayları sadece sayfa belleğinde tutulur; bu
  güvenilir veya kalıcı audit değildir. Genel plan için authenticated actor ve
  hash'e bağlı approval receipt olmadığından Approve devre dışıdır. Local
  Runner onayı bu Planning önizlemesini yetkilendirmez.
- **Command Center:** Request (zorunlu) ve Title (isteğe bağlı) alanları.
  Title boşsa isteğin ilk dolu satırından en fazla 80 karakterlik başlık
  türetilir. Enter yeni satır ekler; Ctrl+Enter (macOS'ta ⌘+Enter) veya düğme
  gönderir. Gönderim tek seferliktir (çift tıklama korumalı), otomatik retry
  yoktur. Başarıda "Created as Draft — not executed" mesajı, task ID'si ve
  task detayına geçiş düğmesi gösterilir. Kesin hatalarda girilen metin
  korunur. Sonucu belirsiz durumlarda (zaman aşımı, bağlantı kesilmesi,
  Control Plane 5xx, beklenmeyen yanıt) task oluşmuş olabilir: yeni gönderim,
  Tasks listesi başarıyla yenilenene kadar kilitlenir.
- **Local Runner:** önce yerel operator bootstrap token ile kısa ömürlü session
  açılır, sonra Draft task seçilip server'ın sunduğu sabit action, scope,
  task-version ve hash incelenir. Açık `Approve and run system.health`
  onayından sonra bir defalık run kaydı oluşturulur; aynı ekran queued/running
  durumunu, kalıcı audit event'lerini ve kategorize edilmiş sonucu gösterir.
  Task status Draft kalır; Agent Worker, provider, model, shell ve arbitrary
  action çağrısı yoktur. Runner schema v4 yoksa endpoint'ler çalışmaz.
- **Overview:** `/health`, `/tasks`, `/plans`; durum sayıları, son task'lar ve
  üç isteğin tümünün son başarılı refresh zamanı. Health yalnız API'nin health
  yanıtıdır, PostgreSQL/Redis/Worker sağlık garantisi değildir.
- **Tasks:** `/tasks` ve `/tasks/{id}`; mevcut durum, deneme sayısı, sonuç ve
  hata alanları. Audit/execution geçmişi bu read API'de mevcut değildir.
- **Plans:** `/plans`; gerçek plan alanları. Plan–task ilişkileri ve dependency
  graph API tarafından sunulmaz.
- **Agents**, **Models & Providers**, **Approvals:** yalnız gelecek yetenekleri
  açıklayan placeholder ekranlar; onay/ret düğmesi veya model çağrısı yoktur.
- Local Runner'daki sabit, insan-onaylı `system.health` run'ı dışında task
  mutasyonu (Worker approve, retry, dispatch), LLM çağrısı, GitHub yazma
  işlemi, secret/token/maliyet ölçümü, repository editing, genel komut
  execution veya deployment yapılmaz.


## Offline unsent input

Command Center keeps **one unsent text buffer** in this browser profile's `localStorage` while typing, even if Control Plane/WSL is offline. It is not a persisted Control Plane task, not encrypted, and must not contain secrets. It does not sync or submit automatically; only the explicit `Create Draft task` click sends a request. On a confirmed 201 Draft creation the local text is cleared. When a POST outcome cannot be confirmed, an `uncertain` lock is saved across page reloads to prevent blind retry until the real task list is checked. Browser/profile clearing, private browsing, storage denial and simultaneous tabs may affect availability; the UI warns when local saving fails. To access this feature, a new built UI must actually be running; a pre-existing 5180 dev server does not automatically update until it serves the updated checkout.
