# B1.1 — Agent Bridge Snapshot Foundation

## Yönetici Özeti

**Humans are not message queues.**

B1.1, bir agent'ın onaylanmış yapılandırılmış metadata'dan kompakt JSON
handoff üretmesini ve başka bir yerel agent'ın `latest` üzerinden okumasını
sağlar. LLM çağırmaz, task çalıştırmaz veya değiştirmez. Control Plane tek
orchestration otoritesidir; snapshot canlı task veritabanı değildir.

Bu milestone **yalnız yerel artifact üretimi/okumasıdır**. GitHub yayını,
uzak agent/ChatGPT connector'ı, authentication servisi ve otomatik Copilot
kontrolü uygulanmadı. İnternet üzerinden insan kopyalaması olmadan teslim
B1.2'nin ayrıca onaylanacak yayıncı/okuyucu entegrasyonuna bağlıdır.

Kaynak dal `feature/hic-agent-bridge`, ayrı worktree
`C:\Enterprise-AgentBridge`; temel commit
`0f36c20dc4d01750f634aaad17ea2d1b6bd6e6e5`.
Reliability worktree'si ve uygulama dosyaları değiştirilmez.
[B0 tasarımı](./AGENT_BRIDGE.md) tarihsel kapsamıyla korunur.

## Teknik Ek

### Uygulama ve sahiplik

- [CLI projesi](../../tools/agent-bridge/AgentBridge.csproj): .NET 10 console;
  ek runtime paketi yok. Ayrı solution, engine, scheduler, queue veya HTTP
  servisi yok.
- [Contract](../../tools/agent-bridge/Contracts.cs),
  [üretici](../../tools/agent-bridge/SnapshotProducer.cs),
  [canonical JSON](../../tools/agent-bridge/CanonicalJson.cs),
  [alan/path güvenliği](../../tools/agent-bridge/Safety.cs),
  [salt okunur Git adapter](../../tools/agent-bridge/LocalGit.cs),
  [yerel artifact store/reader](../../tools/agent-bridge/ArtifactStore.cs),
  [CLI entry point](../../tools/agent-bridge/Program.cs).
- [Offline test projesi](../../tests/AgentBridge.Tests/AgentBridge.Tests.csproj)
  mevcut xUnit/Test SDK sürümlerini kullanır.
- Ortak solution/project dosyalarına sahiplik çatışması yaratmamak için iki yeni
  proje mevcut solution'a eklenmedi; aşağıdaki ayrı test komutu zorunludur.
  Mevcut solution build/test'i Bridge testlerini kendiliğinden çalıştırmaz.
- Mevcut task durum adları yalnız projection olarak taşınır. `AgentTask.Result`
  ve Worker `ModelResult.Content` serbest metindir; otomatik içeri alınmaz.
  R2 ExecutionId verilirse aynen taşınır; task ID execution ID'ye dönüştürülmez.
  DB/API okuması olmadığı için task/execution/project bağı yetkili kaynakta doğrulanmış sayılmaz.

### Snapshot sözleşmesi

`contractVersion = hic.bridge/1.0`. Input/output alanları explicit typed
allowlist'tir; bilinmeyen alan, duplicate JSON key, eksik zorunlu alan,
beklenmeyen null veya desteklenmeyen sürüm reddedilir. Null olabilen alanlar
da input'ta açıkça bulunmalıdır.

| Alan | Kaynak / anlam |
|---|---|
| `snapshotId` | ID alanı boşaltılmış canonical payload'un SHA-256 değeri; 64 lowercase hex |
| `projectId` | Açıkça seçilen, en fazla 48 karakterli lowercase slug; registry/yetki kanıtı değildir |
| `taskId`, `executionId`, `correlationId` | Onaylı metadata UUID'leri veya null; uydurulmaz |
| `source` | Sabit izinli repo kimliği, yerel Git branch/40-hex HEAD ve `workingTreeDirty` |
| `observedAtUtc` | Çağıranın metadata gözlem zamanı; UTC, gelecekte olamaz |
| `publishedAtUtc` | B1.1'de daima null; yerel yazma yayın değildir |
| `executionStatus` | Mevcut task status adı veya `Unknown`; mutation veya yeni state machine değildir |
| `executionStatusConfidence` | Verilen durum `Inferred`, bilinmeyen durum `Unknown` |
| `summary` | Kapalı kod kataloğundan kısa sabit metin |
| `changedFiles` | En çok 20 göreli path + `added/modified/deleted`; çağıran beyanıdır, Git diff kanıtı değildir |
| `verificationEvidence` | Açık kind/status/result/sourceRef/sourceCommit/UTC; aşağıdaki güven ayrımı |
| `knownRisks` | Kapalı risk kataloğu; unverified caller/usage/unpublished sınırları otomatik eklenir |
| `recommendedNextAction` | Kapalı katalogdan tek öneri; execution yetkisi vermez |
| `usage` | `measurement: unknown`; input/output token, cost, currency, pricingVersion null |
| `artifactReferences` | En çok 8 onaylı göreli path; kaynak commit, gerçek Git blob SHA-256/size ve accessScope |

Summary input kodları: `snapshot-foundation`, `verification-complete`,
`work-pending`, `blocked`, `unknown`.

Risk kodları: `caller-attestation`, `unpublished`, `runtime-not-run`,
`usage-not-measured`, `recovery-not-verified`, `source-dirty`.

Next action kodları: `review-before-publication`, `collect-evidence`,
`resolve-blocker`. Serbest prose, raw prompt, diff, log, stack trace,
transcript veya rastgele metadata alanı kabul edilmez. Katalog bilinçli olarak
dardır; yeni ifade ancak kod/test incelemesiyle eklenir. Bu seçim faydalı
özetleme esnekliğini sınırlar, ancak serbest metinle redaction bypass'ını önler.

#### Kanıtın güven düzeyi

- **Verified:** Üreticinin yerel Git HEAD eşleşmesi ve yalnız seçilen committed
  regular-file artifact'larının digest/boyut gözlemi. `result: MATCH`.
  Bu kanıtların zamanı gerçek üretici capture zamanıdır; çağıranın daha eski
  event zamanı geçmişte Git doğrulanmış gibi yeniden kullanılmaz.
- **Inferred:** Input'taki `tests/build/runtime/review/usage` kind'ları için
  `PASS/FAIL` ancak açık repo-relative `sourceRef` ile taşınır.
  Modelin veya insanın PASS metni bağımsız test doğrulaması sayılmaz.
  Input'tan `Verified` gönderilemez. TRX/log içeriği parse edilmez.
- **NOT_RUN**, **NOT_MEASURED**, **Unknown:** Status ve result aynı değerle
  açıkça taşınır; eksik kanıt PASS'e, bilinmeyen maliyet sıfıra çevrilmez.
- Input'ta 1–12 açık evidence zorunludur. Üretici bunun üzerine bir Git ve
  artifact başına bir bütünlük kanıtı ekler; output en çok 21 evidence içerir.
  Test sonucu sourceCommit'i input commit'iyle aynı olmalıdır.

### Canonical serialization ve freshness

UTF-8 BOM'suz, compact JSON; nesne key'leri ordinal sırada, null'lar korunur.
Path/evidence/risk dizileri belirli alanlarla sıralanır; duplicate dosya
referansları reddedilir. Dosyada newline yoktur; CLI `read` stdout'una bir
satır sonu ekler. ID kendi alanı boş bırakılarak hesaplanır; latest digest'i
ID dolu gerçek dosya byte'larının SHA-256 değeridir. Aynı mantıksal veriler ve
**aynı Git capture zamanı** aynı byte'ları verir; ayrı CLI çağrıları yeni capture
zamanı taşıdığından dry-run ID'si sonraki write ID'si olmak zorunda değildir.

Reader expected commit'i hem mevcut HEAD hem snapshot ile karşılaştırır,
branch eşleşmesini ve artifact digest'lerini yeniden doğrular. Varsayılan
yaş sınırı 1.440 dakika, izinli aralık 1–10.080 dakikadır. Metadata
`observedAtUtc` üzerinden konservatif yaş kontrolü yapılır; stale snapshot
sessizce gösterilmez. Canlı task freshness'i veya source worktree'nin
eşzamanlı değişmediği garantisi yoktur.

### Kullanım

Repo/worktree kökünden, mevcut .NET 10 SDK ile:

```powershell
dotnet restore .\tests\AgentBridge.Tests\AgentBridge.Tests.csproj
dotnet build .\tests\AgentBridge.Tests\AgentBridge.Tests.csproj -c Release --no-restore
dotnet test .\tests\AgentBridge.Tests\AgentBridge.Tests.csproj -c Release --no-build --no-restore

$repo = 'C:\Enterprise-AgentBridge'
$dll = '.\tools\agent-bridge\bin\Release\net10.0\AgentBridge.dll'
$input = 'tools/agent-bridge/sample-input.json'
$approval = (Get-FileHash .\tools\agent-bridge\sample-input.json -Algorithm SHA256).Hash.ToLowerInvariant()

# Varsayılan dry-run: doğrular, ID/boyut basar; hiçbir artifact yazmaz.
dotnet $dll generate --repo $repo --input $input --approval-sha256 $approval

# Yalnız incelenmiş sentetik/izinli metadata için açık yerel yazma:
dotnet $dll generate --repo $repo --input $input --approval-sha256 $approval --write-local

# Başka bir yerel agent aynı dosyayı bu komutla okuyabilir:
dotnet $dll read --repo $repo --project bridge-synthetic --expected-source 0f36c20dc4d01750f634aaad17ea2d1b6bd6e6e5 --max-age-minutes 1440
```

Komut başına exit code kontrol edilmelidir; nonzero sonuçtan sonra akışı
sürdürmeyin. Input SHA-256 raw byte'lara bağlıdır; whitespace değişikliği bile
yeni inceleme/digest gerektirir. Hash/flag kriptografik insan kimliği veya
server-side approval değildir: CLI kullanıcısının önceden onaylanmış input'u
seçtiğini beyan eder. Örnekteki digest hesaplama tek başına insan onayı sağlamaz.

[Sentetik input](../../tools/agent-bridge/sample-input.json) yalnız fixture'dır;
runtime/test başarısı iddia etmez. Yeni HEAD/zaman için onaylı input
hazırlanmalıdır; CLI kaynak revision'ını sessizce değiştirmez.

B1.1 doğrulamasında restore yalnız mevcut yerel NuGet cache'i kullanarak yapıldı:

```powershell
dotnet restore .\tests\AgentBridge.Tests\AgentBridge.Tests.csproj --source "$env:USERPROFILE\.nuget\packages" -p:NuGetAudit=false
```

Cache eksikse offline restore açık hata verir; generator hiçbir paket veya ağ
erişimi başlatmaz. Normal restore komutu feed erişimi yapabilir; bu yerel
generator'ın ağsız davranışıyla karıştırılmamalıdır.

### Artifact layout ve failure handling

```text
handoffs/<project-id>/snapshots/<snapshot-id>.json
handoffs/<project-id>/latest.json
```

Latest pointer contractVersion/project/snapshot ID, kesin göreli snapshot path,
dosya SHA-256, source commit, observed time ve null publication time taşır.
Snapshot `CreateNew` ile oluşturulur; aynı ID tekrarında açık hata verilir,
historical dosya veya pointer overwrite edilmez. Farklı snapshot'ta eski
dosya korunur; latest aynı dizindeki temporary file'dan atomic rename ile
değişir. Daha eski observed time latest'i geriye alamaz.

Proje bazlı exclusive `.write.lock` eşzamanlı yazarı reddeder. Normal çıkışta
lock kaldırılır; stale lock otomatik zorla silinmez. Snapshot ve pointer tek
filesystem transaction değildir: kesintide orphan snapshot veya geçerli eski
pointer kalabilir. Pointer'sız dolu history otomatik sahiplenilmez; kaynakları
koruyup inceleyin. Otomatik migration, retry, history cleanup/reset yoktur.

Input/output path'leri repo içine sınırlandırılır; reparse point/symlink
reddedilir. Bunlar düşman bir yerel kullanıcının eşzamanlı filesystem
değişimini sandbox'lamaz; güvenilir çalışma alanı ve OS ACL zorunludur.

### Authentication ve veri sınırı

- Network client/listener, LLM SDK, GitHub token veya Control Plane credential
  yok. Yerel kullanıcı/OS dosya izinleri güven sınırıdır; project/task kimlikleri
  erişim yetkisi değildir. B1.1 varsayılanı kişisel repo allowlist'idir;
  B1.2 kaynak identity'sini açık `--source-repository` allowlist'iyle
  yapılandırmayı ekler; publication destination ayrı kalır.
- Git adapter yalnız sabit yerel read komutlarını çalıştırır; fetch/push/remote
  API, shell, hook, diff veya code execution yok. Git environment override'ları,
  global/system config ve fsmonitor etkileri kapatılır; stderr içeriği basılmaz.
- Input/output 16 KiB, string 512, göreli path 240 karakter. Summary/risk/action
  metinleri kapalı katalogla daha dar sınırlıdır. Aşım truncate edilmez.
  Artifact blob'u en fazla 1 MiB; içerik export edilmez, yalnız hash/boyut taşınır.
- URL'ler (GitHub dahil), mutlak path, `..`, backslash, dot/hidden segment,
  alternate stream, device name, beklenmeyen extension ve hassas path/string
  örüntüleri reddedilir. Yalnız selected committed regular Git blob'ları
  okunur; symlink/submodule ve untracked runtime dosyaları artifact olamaz.
- Yaygın secret/connection/private-key/prompt/transcript/stack-trace örüntüleri
  **redact yerine reject** edilir; hata input içeriğini echo etmez.
  IDs/path/branch metadata'sı da dış aktarımdan önce ayrıca incelenmelidir.
  Pattern taraması genel DLP veya şirket verisi sınıflandırması değildir.
- Dosya digest'i authenticity/signature sağlamaz. Düşman local writer pointer
  ve payload'u yeniden oluşturabilir; B1.2'de güvenilen yayıncı/principal,
  repo/visibility ve digest'e bağlı export onayı ayrıca gerekir.

### B1.2 devam checkpoint'i

[Onay digest'ine bağlı publisher](./B1_GITHUB_PUBLISHER.md) ayrı modül olarak
eklendi; gerçek GitHub yayını henüz yapılmadı. Aşağıdaki adımlar B1.1 sırasında
planlanan akıştır; B1.2 uygulama ve doğrulama sınırları devam belgesindedir.

1. İzinli repo/branch/visibility, metadata sınıflandırması ve exact artifact
   digest'ine bağlı insan export onayı doğrulanır.
2. Ayrı, minimum izinli yayıncı immutable snapshot ve latest pointer'ı scoped
   commit/API write ile taşır. Concurrent/latest update ve stale writer kapısı
   sunucu tarafında korunur; GitHub task state otoritesi olmaz.
3. Yetkili okuyucu latest/digest/source/freshness'i doğrular; issue status veya
   snapshot içeriği task çalıştırma/onay yetkisine dönüşmez.
4. Gerçek publication zamanı/receipt modeli ayrıca tasarlanır; B1.1'in null
   `publishedAtUtc` alanı yerel write zamanıyla doldurulamaz ve immutable
   historical payload yayın sonrasında sessizce değiştirilemez.

### Doğrulama ve kabul kapıları

- Bridge deterministic offline suite: **100 PASS**, 0 fail/skip.
- Mevcut solution restore/Release build: **PASS**, 0 warning/error;
  PostgreSQL dışı API **71/71**, Worker **36/36**: toplam **107 PASS**.
  12 gerçek PostgreSQL testi açık filtreyle dışarıda bırakıldı; DB/runtime
  işlemleri ve önceki 119-test integration koşusu yeniden çalıştırılmadı.
- Gerçek Git adapter'ı ile dry-run → açık local write → latest read **PASS**.
  Dry-run hiç dosya üretmedi. Korunan sentetik snapshot **2.159 byte**;
  pointer SHA-256 ve null usage/publication alanları doğrulandı.
- Testler zorunlu alan/version/evidence/unknown usage/SHA, traversal/secret/URL,
  16 KiB expanded output, sayım/string sınırları, immutable duplicate/history,
  pointer digest/tamper/freshness, orphan/lock, canonical sıralama ve ağsız
  üretimi kapsar. Hiçbir test gerçek repo commit'i veya ağ çağrısı yapmaz.

Kabul: ayrı worktree; mevcut engine'e mutation yok; bounded canonical JSON;
açık yerel yazma; immutable artifact + doğrulanan latest; kaynak/kanıt/usage
ayrımları; offline tests/build/scope kontrolleri. Gerçek remote handoff,
server auth, bağımsız test-result parser, usage telemetry, retention ve
multi-user filesystem transaction garantisi bu milestone'da yoktur.
