# HIC GitHub Publisher — Issue #52

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
token ister. Installation/repository kimliği ve URL yeniden doğrulanır.
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
Control Plane'in v2 temel şemasını bu yeni DB'de oluşturmasından sonra process'i
durdurun; **aynı yeni DB'ye** 004, 005, 006 ve
[007](../../src/ControlPlane/Migrations/007-github-publisher.sql) migration'larını
tek transaction içinde uygulayın. v3 uygulanmaz. Örnek, repo kökünde:

```powershell
$migrations = @(
  '004-local-health-runner.sql', '005-isolated-git-runner.sql',
  '006-project-conversation-code-edit.sql', '007-github-publisher.sql'
)
$sql = "BEGIN;`n" + (($migrations | ForEach-Object {
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
`HIC_API_URL=http://127.0.0.1:19552` kullanıp `npm run dev -- --port 19553`
çalıştırın. Mevcut UI/API portlarına bağlanmayın.

### 3. Boş repo veya mevcut repo

**Boş repo:** Conversation workspace'inin GitHub App bölümünde bootstrap preview
hazırlayın. Tam repository ID/URL, base branch, `src/Value.cs` içeriği, digest ve
expiry'yi inceleyip **Approve exact bootstrap write**, ardından execute seçin.
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

Auth tüm publisher read/write endpoint'lerinde mevcut 15 dakikalık memory-only
operator session'dır. Model/prompt içinde publisher credential'ı veya permission
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
