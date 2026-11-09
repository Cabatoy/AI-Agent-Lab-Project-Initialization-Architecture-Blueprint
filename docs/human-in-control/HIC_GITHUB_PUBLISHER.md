# HIC GitHub Publisher — Issue #52

> **Yerel önizleme güvenlik durumu:** Bu kaynak sürümünde Control Plane,
> doğrulanmış OS izolasyonu bulunana kadar repository çalıştırma isteklerini
> claim etmeden reddeder. Zaten çalışan eski bir API bu korumayı içermeyebilir;
> yalnız arayüzdeki düğmenin kaldırılması eski API'yi güvenli hale getirmez.
> Önceden üretilmiş host worktree içeriğinin yayınlanması da aynı dosya sistemi
> riskini taşır; izolasyon ve artifact doğrulaması tamamlanana kadar yeni GitHub
> yayınları yapmayın. Geçmiş sandbox sonucu yalnız tarihsel kanıttır.

## Yönetici Özeti

Mevcut model önerisi → ayrı kod onayı → izole worktree → sınırlı C# derlemesi
akışına **ikinci, GitHub yazma kapsamına özel onay** eklenir. HIC Control Plane
GitHub App ile tek `hic/task-<task-id>` ref'i, parent'ı onaylı base SHA olan
commit ve **draft** PR oluşturur. Ref yaratılması Git Data API ile yapılır;
Git'e token verilmez, credential helper/hook veya authenticated Git URL kullanılmaz.

Geliştirme PR'si bu özelliğin gerçek HIC execution kanıtı değildir. App'ın
kurulmuş olması local credentials'ın yapılandırıldığı anlamına gelmez.
Credentials eksikse yayıncı kapalı kalır; PAT veya başka sağlayıcı fallback'i yoktur.
Mevcut PostgreSQL/Redis/Podman servislerine migration, restart, merge veya deploy yapılmaz.

## Teknik Ek

### Yerel model erişilebilirliği

Konuşma ekranındaki model durumu, yapılandırılmış yerel sağlayıcının
`/v1/models` yanıtını denetler. `available`, ilgili modelin envanterde
listelendiğini gösterir; sonraki üretim isteğinin başarılı olacağını garanti
etmez. Erişim hatası veya geçersiz yanıt `unavailable`, listede bulunmayan
model `model-not-listed` olarak gösterilir. Durum oturum açılırken kontrol
edilir; **Refresh model status** ile yeniden sorgulanabilir. Kontrol prompt
veya sır göndermez. **Models & Providers** ekranı aynı doğrulanmış operator
oturumunu ve aynı Control Plane tanısını kullanır. Sağlayıcı yönetimi, model
seçimi ve maliyet görünürlüğü henüz uygulanmamıştır.

### Genel konuşmalar

Genel konuşmalar repository, dosya veya task oluşturulmasını gerektirmez.
Mesajlar operator kimliğiyle sınırlandırılır; model yalnızca son konuşma
bağlamını alır ve araç, dosya, yürütme veya yayın yetkisi verilmez. Kesilmiş
bir model yanıtında kaydedilmiş kullanıcı mesajı korunur; geçmiş gözden
geçirilmeden aynı istek yeniden çalıştırılmaz.

Bu işlev `008-general-conversations.sql`, plan incelemesi de
`009-general-plan-review.sql` şemasını gerektirir; GitHub Publisher v7 bu
şemalardan önce uygulanmalıdır. Migration
otomatik çalıştırılmaz; yalnızca hedef veritabanı ve yedekleme açıkça
onaylandıktan sonra uygulanabilir. Mevcut preview snapshot veritabanına bu
değişiklik uygulanmamıştır. Modelin yapılandırılmış önerisi güvenilmezdir;
operatör kaydedilmiş sürümü düzenleyip ayrıca gözden geçirerek yalnızca Draft
task oluşturulmasını onaylar veya sürümü reddeder. Reddedilen sürüm onaylanamaz;
yeniden düzenleme ya da yeni model önerisi ayrı sürüm oluşturur. Bu onay Worker dispatch, kod yürütme, araç,
GitHub veya deployment yetkisi vermez. Her genel konuşmada en fazla bir plan
sürümü için Draft task oluşturulur; yeniden deneme aynı task kimliklerini döndürür.

### 1. GitHub App ve eksik local ayarlar

App yalnız **Selected repositories → Cabatoy/hic-agent-sandbox** üzerinde kurulu
olmalı. Repository permissions tam olarak:

| Permission | Access |
|---|---|
| Metadata | Read |
| Contents | Read and write |
| Pull requests | Read and write |

Organization/account izinleri, Actions, Workflows, Administration vb. eklemeyin.
Broker App ve installation izinlerini de denetler; daha geniş veya eksik izin reddedilir.

1. GitHub **Settings → Developer settings → GitHub Apps → ilgili App** sayfasından
   **App ID** değerini alın. Client ID değildir.
2. Kurulu App'ın **Configure** bağlantısındaki `/settings/installations/<id>`
   segmentinden **Installation ID** değerini alın.
3. **Private keys → Generate a private key** ile indirilen PEM'i repo dışında,
   yalnız Control Plane servis hesabının okuyabileceği bir klasöre koyun.
   Anahtarın içeriğini Chat'e, `.env` içine, loga veya Git'e yazmayın.
   Windows'ta dosyanın Security/ACL ayarından diğer hesapların erişimini kaldırın;
   macOS/Linux'ta servis hesabı sahipliği ve `chmod 600` kullanın.
4. Private sandbox'ın GitHub sayfa kaynağındaki
   `octolytics-dimension-repository_id` meta alanından sayısal repository ID'yi
   alın. Sayfa kaynağının tamamını Chat'e göndermeyin. Zaten kurulu/yetkili GitHub
   CLI varsa yalnız metadata okumak için `gh api repos/Cabatoy/hic-agent-sandbox --jq .id`
   kullanılabilir; bu operatör okuması HIC publication kimliği değildir.
5. **Yeni ve izole Control Plane process'inin** ortamını ayarlayın:

```powershell
$env:HIC_GITHUB_APP_ID = Read-Host 'GitHub App ID'
$env:HIC_GITHUB_INSTALLATION_ID = Read-Host 'Installation ID'
$env:HIC_GITHUB_PRIVATE_KEY_PATH = Read-Host 'Repo disindaki PEM dosyasinin tam yolu'
$env:HIC_GITHUB_REPOSITORY_ID = Read-Host 'hic-agent-sandbox sayisal repository ID'
$env:HIC_GITHUB_REPOSITORY = 'Cabatoy/hic-agent-sandbox'
$env:HIC_GITHUB_BASE_BRANCH = 'SaasMain'
```

`SaasMain` yalnız başlangıçta bildirilen isimdir. `/local-runner/github/repository`
gerçek ref'i kontrol eder; metadata'daki `default_branch` bir commit kanıtı değildir.
Gerçek base branch farklıysa açık onaylı hedefi yapılandırın. Ortamın veya
anahtarın içeriğini dump etmeyin. Anahtar path'i public UI'ye gönderilmez.
Container kullanımı ayrıca onaylı read-only secret mount ve bu ayarların yalnız
Control Plane'e iletilmesini gerektirir; mevcut Compose stack'i değiştirmeyin.

Broker RS256 App JWT'yi bellekte üretir (en fazla 9 dakikalık ileri expiry);
tek sayısal repository ID ve üç minimal permission ile kısa ömürlü installation
token ister. Önce repository ID ile daraltılmamış, aynı minimal izinli token'ın
installation envanteri okunur; tam installation yalnız hedef repoyu içermelidir.
Ardından yayın için ayrı, tek repository ID'ye daraltılmış token alınır.
Daraltılmış token'ın tek repo göstermesi tek başına installation izolasyonu
kanıtı sayılmaz. Installation/repository kimliği ve URL yeniden doğrulanır.
Tokenlar yalnız HTTP Authorization header'ında, backend belleğinde bulunur.
Redirect, proxy, HTTP, özel API host'u ve geniş token fallback'i kullanılmaz.

### 2. İzole veritabanı ve runtime

Bu komutlar yeni test kaynakları içindir; mevcut `agentlab-*` kaynaklarına
uygulamayın. Önceden onaylı Podman üzerinde, benzersiz isimlerle:

```powershell
podman run --detach --name hic52-local-postgres --pull=never `
  --publish 127.0.0.1::5432 --tmpfs /var/lib/postgresql/data:rw `
  --env POSTGRES_HOST_AUTH_METHOD=trust docker.io/library/postgres:16-alpine
podman run --detach --name hic52-local-redis --pull=never `
  --publish 127.0.0.1::6379 docker.io/library/redis:7-alpine
podman exec hic52-local-postgres pg_isready -U postgres
podman exec hic52-local-postgres psql -U postgres -v ON_ERROR_STOP=1 `
  -c 'CREATE DATABASE hic52_local;'
$pgPort = (podman port hic52-local-postgres 5432).Split(':')[-1]
$redisPort = (podman port hic52-local-redis 6379).Split(':')[-1]
$env:ConnectionStrings__Postgres = "Host=127.0.0.1;Port=$pgPort;Database=hic52_local;Username=postgres"
$env:ConnectionStrings__Redis = "127.0.0.1:$redisPort"
```

Trust auth yalnız loopback'e bağlanan, sır içermeyen geçici test DB'si içindir;
paylaşılan/canlı DB'de kullanmayın. Image eksikse onaysız kurulum/pull yapmayın.
Control Plane startup sırasında schema v2'yi **InitializeSchemaAsync'ten önce**
doğrular. Bu nedenle ilk API startından önce temel tablo SQL'ini ve 002, 004, 005, 006 ve
[007](../../src/ControlPlane/Migrations/007-github-publisher.sql) migration'larını
**yalnız aynı yeni DB'ye**, tek transaction içinde uygulayın. v3 uygulanmaz.
Örnek, repo kökünde; SQL bulunamazsa işlem açıkça durur:

```powershell
$program = Get-Content 'src\ControlPlane\Program.cs' -Raw
$baseSchema = [regex]::Match($program,
  '(?s)static async Task InitializeSchemaAsync.*?connection.ExecuteAsync\(@"(.*?)"\);')
if (!$baseSchema.Success) { throw 'Base schema SQL not found; do not start API.' }
$migrations = @(
  '002-execution-ownership.sql',
  '004-local-health-runner.sql', '005-isolated-git-runner.sql',
  '006-project-conversation-code-edit.sql', '007-github-publisher.sql'
)
$sql = "BEGIN;`n" + $baseSchema.Groups[1].Value + "`n" + (($migrations | ForEach-Object {
  Get-Content (Join-Path 'src\ControlPlane\Migrations' $_) -Raw
}) -join "`n") + "`nCOMMIT;"
$sql | podman exec --interactive hic52-local-postgres psql -U postgres `
  -d hic52_local -v ON_ERROR_STOP=1
```

App/installation ayarlarından önce de API çalışabilir; yayıncı kapalıdır.
Control Plane'i **aynı yeni process ortamında** boş bir local portta başlatın:

```powershell
$env:HIC_LOCAL_OPERATOR_ID = [guid]::NewGuid().ToString()
$bytes = New-Object byte[] 32
$rng = [System.Security.Cryptography.RandomNumberGenerator]::Create()
$rng.GetBytes($bytes)
$rng.Dispose()
$env:HIC_LOCAL_OPERATOR_TOKEN = [Convert]::ToBase64String($bytes)
Set-Clipboard -Value $env:HIC_LOCAL_OPERATOR_TOKEN
dotnet run --project src\ControlPlane\ControlPlane.Api.csproj --no-launch-profile `
  --urls http://127.0.0.1:19552
```

Bu yalnız yerel HIC bootstrap session token'ıdır, GitHub token'ı değildir.
UI'de operator giriş alanına yapıştırıp clipboard'ı temizleyin. Model ayarları
mevcut local `MODEL_PROVIDER`, `MODEL_ENDPOINT`, `MODEL_NAME` ve timeout
sözleşmesidir. Worker başlatılmaz. Ayrı WebUI process'inde
`HIC_API_URL=http://127.0.0.1:19552` ve `HIC_WEB_PORT=19553` kullanıp `npm run dev`
çalıştırın. Mevcut UI/API portlarına bağlanmayın.

### 3. Boş repo veya mevcut repo

**Boş repo:** Conversation workspace'inin GitHub App bölümünde bootstrap preview
hazırlayın. Tam repository ID/URL, base branch, `src/Value.cs` içeriği, digest ve
expiry'yi inceleyip **Approve exact bootstrap write**, ardından execute seçin.
Ekran immutable repository ID, public App/installation kimliği, runtime revision
ve tam yeni-dosya diff'ini gösterir. **Reject exact GitHub write** yalnız kalıcı
ret kaydı oluşturur; onay veya uzak yazma yapmaz. Süresi dolan preview aynı
publication ID üzerinde, scope ve remote base tekrar doğrulanarak yenilenir;
`preview-renewed` audit kaydı oluşur, yeni task/publication/onay kaydı oluşmaz.
Digest değiştiğinde yeni exact insan kararı gerekir.
Bu tek dosya için Contents API ile ilk commit oluşturulur. İlk commit'in parent
listesi boş ve tree'si yalnız onaylı source blob olmalıdır. Bootstrap ayrı
kalıcı operation'dır; task push/PR izni değildir. Sayfayı yeniden açınca kayıt yüklenir.

GitHub boş repoda Git ref yaratılmasına izin vermediği için ilk commit Contents
API üzerinden başlatılır. Bu endpoint repository-empty koşulu için atomik
compare-and-swap sağlamaz. Bootstrap sırasında başka writer kullanmayın;
ön kontrol ile write arasında başka aktör repo başlatırsa sonuç unknown olabilir
ve manuel inceleme gerekir. Uygulama bunu merge/force/delete ile düzeltmez.

**Mevcut repo:** Bootstrap reddedilir. Onaylanan gerçek base branch SHA'sı
server-authorized temiz local checkout'un HEAD'i ile birebir aynı olmalıdır.
Genel import/clone ajanı bu issue'nun kapsamında değildir. Operatör yalnız yeni
disposable checkout'ta normal GitHub authentication ile repo'yu indirip
onaylı immutable SHA'ya getirir; HIC App token'ı Git'e verilmez.
Yeni checkout'taki `origin` kaydını kaldırın; publisher kaynak Git config'i
remote, include, filter, hooksPath, alternates vb. kabul etmez.
Orijinal checkout'u değiştirmeyin.

```powershell
$source = Read-Host 'Yeni izole ve temiz checkout tam yolu'
$base = (git -C $source rev-parse HEAD).Trim()
$env:HIC_GITHUB_SOURCE_REPOSITORY = 'hic-agent-sandbox'
$env:HIC_GITHUB_LOCAL_REPOSITORIES = ConvertTo-Json -Compress -InputObject @(
  @{ id='hic-agent-sandbox'; path=[IO.Path]::GetFullPath($source);
     baseCommit=$base; allowedFiles=@('src/Value.cs') }
)
$env:HIC_GIT_RUN_ROOT = Read-Host 'Checkout disindaki mevcut bos run klasoru'
```

Bu config yeni API process başlamadan ayarlanmalıdır. Repo en fazla 200 regular
`100644` dosya, 8 açık izinli UTF-8 dosya ve küçük, inert Git metadata/config ile
sınırlıdır. Executable blob, symlink/submodule, junction, UNC/network-drive,
unsafe config, dirty source veya stale base reddedilir. Mevcut disposable
`.hic-disposable` akışı değişmez. Bu sınır genel repo import desteği veya OS sandbox
iddiası değildir; her iki repo durumu da mevcut tek-C#-dosya workflow'unu kullanır.

### 4. Aynı conversation'da publication

1. Local model ile konuşun, üretilen C# replacement'ı inceleyip kod planını onaylayın.
2. **Execute approved isolated edit** ile gerçek diff/test'i elde edin.
3. Yalnız completed, tests-passed ve source-unchanged run'da
   **Preview GitHub task publication** görünür.
4. Actor, task/run/conversation, repository ID/URL, base SHA/tree, file/content
   digest, App/installation scope digest, `hic/task-<task-id>`, target branch ve 5 dakikalık expiry'ye bağlı
   **ayrı** SHA-256 onayını verin. Onay pending durumudur, başarı değildir.
5. Execute blob/tree/commit oluşturur, yalnız tek yeni task ref'i yaratır,
   remote SHA'yı doğrular ve draft PR açar. Default branch değişmez.
6. Conversation aynı DB'den publication state, actual commit/ref SHA, PR number/link
   ve append-only audit'i reload sonrasında gösterir.

Auth tüm publisher read/write endpoint'lerinde aynı `LocalOperatorSessions`
operator kimliğidir. Doğrudan bearer client'ları 15 dakikalık oturum kullanır;
WebUI açık bootstrap girişinden sonra 8 saatlik mutlak süreli HttpOnly cookie kullanır.
Model/prompt içinde publisher credential'ı veya permission
kararı bulunmaz. Task sürümü, code plan, tested content, source config veya remote
base değişirse publication reddedilir. İzinli run başına tek onaylı publication vardır.

### 5. Recovery ve kanıt sınırı

Her external write'tan önce intent DB'ye commit edilir. Publication başına
PostgreSQL advisory lock eşzamanlı execute/reconcile'i dışlar.

```text
preview -> approved/pending -> blob-intent -> tree-intent -> commit-intent
        -> commit-created -> ref-intent -> pushed -> pr-intent -> succeeded
                                            \-> unknown
```

Timeout/crash başarı sayılmaz. **Read-only remote reconciliation** hiçbir write
yapmadan exact ref/commit/tree/PR'ı karşılaştırır. Ref yazılmış ama PR yoksa
`pushed` gösterilir; hâlâ geçerli exact onayla **Reconcile and complete approved
draft PR** önce PR varlığını tekrar kontrol edip devam eder. PR timeout'undan
sonra bulunan mevcut draft benimsenir; ikinci PR yaratılmaz.
Commit/tree yanıtı kaybolmuş ve commit kimliği kaydedilememişse unknown korunur;
unreachable object'ler otomatik tekrar yaratılmaz. Süresi geçmiş onay yeni
write izni değildir; manuel inceleme/yeni task gerekir. Branch advancement,
closed/non-draft/mismatched PR veya revoked permission başarıya çevrilmez.

### 6. Testler ve dış E2E

```powershell
# HIC_R1_TEST_POSTGRES unset/empty: DB testleri atlanır, canlı DB'ye erişilmez.
dotnet test tests\ControlPlane.Api.Tests\ControlPlane.Api.Tests.csproj `
  --filter 'FullyQualifiedName~GitHubPublisherTests|FullyQualifiedName~ModelCodeEditTests'
Set-Location src\WebUI
npm test
npm run build
```

PostgreSQL testlerini yalnız mevcut dedicated-fixture guard'larına uygun yeni,
işaretli tmpfs database/role ile etkinleştirin. Fake GitHub HTTP testleri broker
JWT/permission/repo, intent-before-write, base mismatch, revoked auth, replay,
bootstrap, tree/path/mode ve ambiguous ref/PR senaryolarını kapsar.
Gerçek HIC browser → Mistral → sandbox commit/ref/draft PR acceptance **credentials
olmadan doğrulanmış değildir**. Geliştirme draft PR'si bu kabulün yerine geçmez.

### Standart launcher ile development UI farkı

Windows'ta operator token taşımadan erişim için
[`hic-open-operator.ps1`](../../scripts/hic-open-operator.ps1) açıkça çağrılan
tek seferlik bir desktop giriş yardımcısıdır. Ajan launcher'ı kullanıcının görünür
browser penceresinde çalıştırır; insan terminal veya token taşıma işlemi yapmaz.
Yardımcı exact browser PID, address bar URL, Control Plane revision ve sandbox
kimliğini doğrular. DPAPI korumalı **local operator** token'ını yalnız mevcut
parola alanına Windows UI Automation ile yazar ve mevcut login düğmesine basar.
Clipboard, URL, log, browser storage veya model prompt'una token yazılmaz.
GitHub App PEM/token'ına erişmez; kısa süreli runtime doğrulama bearer oturumunu
işlem sonunda revoke eder. Giriş UI'nin desteklenen bootstrap akışından geçer;
tüm insan onayları korunur. Kalıcı login servisi, browser policy değişikliği veya auth
fallback'i oluşturmaz. Süre dolduğunda yardımcı açıkça yeniden
çalıştırılabilir; hiçbir Approve/Reject/Execute kontrolünü kullanmaz.

### Kalıcı operatör oturumu — local HTTP WebUI

- Bootstrap token yalnız mevcut parola girişinde bir kez kullanılır. Vite proxy
  backend'e `X-HIC-Session-Mode: browser` ile aynı session issuance isteğini iletir.
  Backend varsayılan doğrudan bearer sözleşmesi **15 dakika** kalır; açık browser
  modu aynı actor için **8 saat mutlak** geçerlilik verir. GET/aktivite süreyi
  uzatmaz, refresh token veya otomatik bootstrap yoktur.
- Proxy başarılı issuance yanıtındaki session token'ını JavaScript'e göndermez.
  Yanıttaki `cookie-session` yalnız gizli olmayan transport işaretidir.
  Asıl token `HttpOnly; SameSite=Strict; Max-Age=<kalan saniye>` cookie'dedir;
  bootstrap token hiçbir cookie, localStorage, sessionStorage, URL veya loga yazılmaz.
  Cookie `Domain` içermez; adı frontend portu ve API origin hash'i ile ayrılır,
  yolu `/api/local-runner` olur. Aynı host'taki diğer runtime cookie'leri
  kullanılmaz veya backend'e aktarılmaz. Cookie'ler port güvenlik sınırı değildir:
  bu izolasyon yanlış runtime karışmasını önler, kötü niyetli local process'e
  karşı işletim sistemi izolasyonunun yerini tutmaz.
- Mevcut exact `http://127.0.0.1:<UI-port>` üzerinde `Secure` kullanılmaz;
  bu yalnız onaylı loopback development akışıdır, uzak/production HTTP için
  güvenlik tasarımı değildir. Proxy exact Host, Origin, `Sec-Fetch-Site` ve
  `X-HIC-Request: local-runner` denetimleriyle cookie-authenticated read/write
  ve bootstrap/revoke isteklerini CSRF'den korur. Backend cookie kabul etmez;
  proxy yalnız seçilen runtime cookie'sini Authorization bearer'a çevirir.
- Conversations, Local Runner ve Isolated Git mount sırasında
  `GET /api/local-runner/session` ile aynı oturumun actor/absolute expiry
  metadata'sını okur ve mevcut actor'a ait kayıtları yeniden yükler.
  Sayfa yenileme, sidebar navigation ve aynı browser profilinde aynı UI adresini
  yeniden açma geçerli cookie'yi korur; bunlar yeni bootstrap veya write değildir.
  Private browser, cookie silinmesi, başka port/API veya backend restart
  yeniden açık giriş gerektirir: session kayıtları backend belleğindedir.
- `End session` yalnız mevcut oturumu
  `POST /api/local-runner/session/revoke` ile server-side iptal eder ve cookie'yi
  temizler. Revoke edilmiş token tekrar kullanılamaz. Eksik/expired/revoked
  session 401 üretir; proxy cookie'yi temizler, UI stale workspace ve hatayı
  kaldırıp açık yeniden giriş açıklamasını gösterir. Idle UI mutlak expiry'de
  de giriş görünümüne döner. Başka açık sekme sonraki protected isteğinde
  revoke'u görür. Ağ hatası sign-out başarısı sayılmaz.
- Süre dolduğunda kullanıcı aynı desteklenen bootstrap login'i kullanabilir.
  Tokenı elle taşımadan giriş için ajan, açıkça seçilmiş görünür browser'ı
  exact UI adresinde `#operator-access` konumuna getirip mevcut
  `scripts/hic-open-operator.ps1` yardımcısını açıkça bir kez çalıştırır.
  Yardımcı mevcut DPAPI token dosyası, exact UI/API origin, browser PID ve
  beklenen revision ister; token dosyası yoksa yeni kimlik uydurmaz.
  Bu etkileşim her reload için değil, expiry/logout/restart sonrasında gerekir.
  Perpetual/arka plan otomatik login değildir.
- Oturum restore/revoke/yeniden giriş onay vermez, publication yenilemez ve
  interrupted write'ı tekrar çalıştırmaz. Conversation/run/publication kayıtları
  ve mevcut onay DB'si korunur; yeni giriş aynı configured actor'ı doğrular.

Launcher kendi checkout'undaki UI'yi başlatır; readiness yalnız page title/health
ile ölçülür. Bu kontroller source revision, conversation/publisher capability veya
v6/v7 schema uyumunu kanıtlamaz. Böylece eski sağlıklı UI yeniden kullanılabilir.
Review edilmemiş PR #54'ün kodunu stable launcher kendiliğinden seçmemelidir.

Küçük sonraki adım önerisi: onaylanmış runtime manifestinde revision, UI/API
capability ve schema sürümünü pinlemek; `hic status` içinde farkı açık göstermek
ve tek launcher girişinden desteklenen UI'yi açmak. Uyumsuzlukta mevcut runtime'ı
değiştirmek yerine ayrı izole profile yönlendirmek. Bu görev geniş launcher
refactor'ı, branch merge'i veya canlı DB cutover'ı yapmaz.
