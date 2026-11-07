# B1.2 — Onay digest'ine bağlı GitHub snapshot yayıncısı

## Yönetici Özeti

**Humans are not message queues.**

B1.2, [B1.1 yerel snapshot foundation](./B1_SNAPSHOT_FOUNDATION.md) üzerine
deterministik publication candidate, preview, digest onayı, izole Git staging,
publication commit ve remote doğrulama ekler. Control Plane tek orchestration
otoritesidir. Task state, approval veya execution mutation yapılmaz.

**Bu milestone'da gerçek GitHub yayını yapılmadı.** Yalnız geçici yerel
Git/bare-remote testleri yetkilidir. Aşağıdaki GitHub komutları kullanım
belgesidir; bunların varlığı insan export/ağ/push onayı değildir. ChatGPT'nin
private repoyu okuyabildiği veya snapshot'ı kendiliğinden tükettiği doğrulanmadı.

## Teknik Ek

### Dosya sahipliği ve B1.1'in korunması

- [SnapshotPublisher](../../src/AgentBridge/SnapshotPublisher.cs):
  candidate/preview, exact approval, staging/commit/push/verification akışı.
- [PublicationContracts](../../src/AgentBridge/PublicationContracts.cs):
  manifest, journal, preview/receipt ve transport sınırı; snapshot contract'ının
  yerine geçen ikinci contract/store değildir.
- [PublicationGit](../../src/AgentBridge/PublicationGit.cs),
  [PublicationTransport](../../src/AgentBridge/PublicationTransport.cs),
  [PublicationWorkspace](../../src/AgentBridge/PublicationWorkspace.cs),
  [PublicationCli](../../src/AgentBridge/PublicationCli.cs).
- [PublicationTests](../../tests/AgentBridge.Tests/PublicationTests.cs):
  gerçek ama sentetik temp Git/bare-remote testleri.
- B1.1 canonical serializer, snapshot validation/identity, immutable store,
  usage/evidence semantiği kullanılır. Mevcut store'un bundle doğrulaması
  remote pointer/snapshot için ortak public metoda çıkarılmıştır.
- Kaynak repo allowlist'i `LocalGit` constructor/CLI'da açık beklenen repo
  kimliğiyle yapılandırılır; eski varsayılan korunur. Snapshot'taki repo adı
  yalnız biçim doğrulamasıdır; yetki kontrolü explicit kaynak allowlist'inde
  ve publication destination eşleşmesindedir.
- Track A, uygulama kaynakları, production Compose ve ortak solution değişmez.

### Yayın akışı

1. **Prepare:** Kaynak index'te hiçbir staged dosya bulunamaz. Unstaged
   geliştirme kodu hiçbir zaman staging/push kapsamına alınmaz. B1.1 reader,
   snapshot/pointer canonical byte'larını, kaynak HEAD/branch, freshness ve
   committed artifact referanslarının digest'lerini doğrular.
2. **Hedef doğrulama:** Owner/repo ve yalnız `handoffs/<slug>` destination
   branch açıkça seçilir. `main`, geliştirme branch'i, arbitrary refspec,
   URL ve shell/Git argument injection reddedilir. Destination branch önceden
   mevcut olmalıdır; branch yaratma ve reconciliation otomatik yapılmaz.
3. **İzole alan:** Kaynak ağacın tamamen dışında yeni, benzersiz publication
   workspace oluşturulur. Mevcut alan benimsenmez/silinmez. Yalnız hedef branch
   fetch edilir; iki head gözlemi ve fetched commit eşleşmelidir. Snapshot'ın
   kaynak geçmişi publication repo'suna kopyalanmaz.
4. **Manifest/preview:** `hic.publication/1.0`, source repo/branch/revision,
   destination repo/branch, transport fingerprint, expected remote commit,
   project/snapshot ID, prepare UTC ve iki dosyanın path/size/SHA-256/önceki blob
   kimliğini içerir. Manifest en fazla 16 KiB; canonical bytes SHA-256 onay
   digest'idir. Preview dosya kapsamını ve confidence sınırını gösterir.
5. **Explicit approval:** `--approval-digest <exact SHA-256>` ve
   `--approve-publication` birlikte gerekir. Onay bir saat geçerlidir.
   Candidate, latest, destination, source revision/branch, artifact veya
   remote base değişirse eski onay kullanılamaz; yeni prepare/insan kararı gerekir.
6. **Commit:** Source index ve ayrı publication index yeniden denetlenir.
   Yalnız immutable snapshot ve latest pointer blob'ları `hash-object`/
   `update-index` ile stage edilir. Tam diff path allowlist'i tekrar doğrulanır.
   `write-tree`/`commit-tree` ile parent'ı tam onaylanmış remote base olan tek
   publication commit oluşturulur; kaynak geliştirme worktree'sinde commit yoktur.
7. **Push:** Hedef HEAD tekrar aynı olmak zorundadır. Journal `push-started`
   diske yazıldıktan sonra tek tam refspec ile **normal, force olmayan push**
   yapılır. Tag/submodule veya başka branch taşınmaz.
8. **Verify:** Remote branch SHA publication commit ile eşleşir; o commit'in
   snapshot/pointer blob digest/boyutları ve ortak B1.1 bundle doğrulaması
   geçer. İkinci remote HEAD gözlemi değişikliği denetler. Git commit/object
   içeriği immutable olduğu için aynı remote SHA ve doğrulanmış yerel object'ler
   aynı tree byte'larını tanımlar; remote worktree veya canlı task okunmaz.

Preview hazırlamak insan kimliğini doğrulamaz. Flag/digest, CLI'yi kullanan
yetkili kişinin exact export kapsamına verdiği onayın makine kapısıdır;
server-side approval registry, dijital imza veya kişisel authentication değildir.

### Dört farklı kimlik — circular hash yok

| Kimlik | Anlam |
|---|---|
| Source commit | Snapshot'ın gözlemlediği uygulama/kaynak revision |
| Snapshot ID / dosya digest'i | B1.1 içerik adresi / ID dolu snapshot bytes SHA-256 |
| Publication commit | Snapshot ve pointer'ı remote base üzerine ekleyen yeni Git commit |
| Remote branch revision | Yayından sonra remote ref üzerinde doğrulanan commit |

Manifest source commit ile snapshot digest'ini içerir; publication commit
henüz yoktur. Publication commit message manifest digest'ini taşır. Receipt
publication commit ve remote SHA'yı sonradan kaydeder; bunlar snapshot source
revision'ı diye yeniden adlandırılmaz. Snapshot/pointer'ın `publishedAtUtc`
alanları **null kalır**: immutable payload'u değiştirmek yerine publication
kanıtı yerel journal/receipt ve Git commit'tedir. B1.1'in `unpublished` riski
üretim anındaki bilgidir; sonradan yayın durumu için receipt dikkate alınır.

### Latest/history ve concurrency

- Hedefte aynı snapshot path farklı bytes içeriyorsa açık immutable conflict.
- Var olan remote latest/pointer hedefi canonical, bounded, geçerli ve digest'i
  eşleşen snapshot olmak zorundadır. Bozuk latest veya pointer'sız history
  otomatik düzeltilmez.
- Yeni snapshot daha eski/eşit observed time ile farklı latest'i geçemez;
  source repo/branch değişikliği ayrıca reconciliation gerektirir.
- Snapshot ve latest zaten aynı byte'larsa yeni commit/push yapılmaz;
  `already-published` receipt üretilir. Verified journal ile tekrar çağrı yalnız
  remote doğrulama yapar; ikinci push yoktur.
- Onaydan sonra remote advancement commit'ten önce görülürse durulur.
  Commit sonrası/push sırasında advancement normal fast-forward kuralıyla
  reddedilir; yeni insan onayı olmadan merge/rebase/force yoktur.
- Normal Git push ve iki read gözlemi tek bir server-side compare-and-swap
  değildir. Başka yetkili aktör remote'u zorla eski bir ancestor'a döndürebilir
  veya ref'i silip yeniden oluşturabilirse gözlemler arasındaki bu hareketin
  tamamı garantiyle saptanamaz. Canlı kullanım için branch'te force push/deletion
  yasakları ve sınırlı writer yetkisi ayrıca doğrulanmalıdır. Bu uygulama onları
  API üzerinden ayarlamaz veya var olduklarını iddia etmez.

### Belirsiz sonuç ve kalıcı state

Workspace'te `manifest.json`, `journal.json`, `.git` ve işlem sırasında
exclusive `publication.lock` bulunur. Marker, beklenen Git config, link/index/
object-source kapıları denetlenir. Journal sırası:

```text
prepared -> committing -> commit-created -> push-started -> verified
                                                     \-> outcome-unknown
```

In-progress aşaması mutation'dan önce flush + atomic rename ile yazılır.
`committing`, `commit-created`, `push-started`, `outcome-unknown` yeniden
commit/push izni vermez. Push timeout/hata veya verify başarısızlığı uzak
başarısızlık diye uydurulmaz; **OUTCOME UNKNOWN, NO RETRY**.
Verified tekrar remote değişmişse başarı dönmez.

Kaynakları ve journal'ı koruyun. Ayrı onayla read-only remote inceleme yapıp
publication commit'in kabul edilip edilmediğini belirleyin; journal'ı elle
`prepared` yapmayın. Otomatik reconciliation/cleanup implement edilmedi.
Yeni candidate/workspace eski sonucu araştırmanın yerine geçmez.

### Owner/repo ve credential yönetimi

Source repository ve publication destination **ayrı** CLI parametreleridir.
Transfer sonrası canonical repository
`Cabatoy/AI-Agent-Lab-Project-Initialization-Architecture-Blueprint` olmuştur.
Bu repository kullanılırken source/destination parametreleri açıkça yeni
kimliği taşımalıdır; eski kimliğe otomatik fallback veya historical snapshot
yeniden yazımı yoktur. Generator default'u da yeni kimliği kullanır.
Owner/account auth kimliği hardcode değildir; başka hesap/repo'ya göç açık
config + yeni digest/onay gerektirir.

GitHub transport yalnız oluşturulmuş exact
`https://github.com/<owner>/<repo>.git` endpoint'ini kullanır. Git global/system
config ve `GIT_*` override'ları temizlenir; URL rewrites, redirects, fsmonitor,
submodule recursion ve hook çalıştırılması publication repo'sunda kapalıdır.
Yalnız `manager`, `manager-core`, `osxkeychain`, `libsecret` credential helper
adları kabul edilir. Mevcut OS credential helper/GitHub credential kullanılır;
token yazdırılmaz, manifest/journal/config'e kaydedilmez. `useHttpPath=true`;
helper ve repo için uygun mevcut credential yoksa açık hata gerekir.
Interaktif credential edinme, login veya key oluşturma bu tool'un işi değildir.

Publication commit author metadata'sı `HIC Snapshot Publisher
<hic-snapshot@localhost>` servis etiketi, ayrıca Copilot co-author trailer'ıdır;
GitHub auth identity veya insan onayı değildir. Plumbing commit imzasızdır;
signed-commit zorunlu branch policy'si varsa destek eklenmeden yayın yapılamaz.
Hook tabanlı bir secret scanner'a güvenilmez; publication candidate validator'ı
zorunludur. İşletim sistemi, Git binary/credential helper ve yerel ACL güven
sınırıdır; düşman aynı-kullanıcı filesystem yarışlarına karşı sandbox değildir.

### Veri minimizasyonu ve güvenlik

- Sadece **iki** onaylı JSON dosyası export edilir. Artifact referanslarının
  içerikleri, kaynak kod/diff/log/prompt, dev worktree veya kaynak Git history
  publication commit'ine alınmaz.
- B1.1 allowlist, kapalı prose kataloğu, evidence confidence ve unknown/null
  usage aynen korunur. Source/ref metadata'sı da dış aktarım öncesi insan
  sınıflandırmasına tabidir; private repo otomatik export izni değildir.
- Snapshot ve manifest 16 KiB; JSON unknown/duplicate field/string/secret/path
  kontrolleri uygulanır. Stdout bounded; Git stderr basılmaz, credential
  output'u alınmaz. Journal/manifest URL veya token içermez.
- Yalnız exact owner/repo URL'sine erişim, authenticated account'ın hangisi
  olduğunu veya repo visibility'sini API ile doğrulamak değildir. Gerçek
  publication öncesi hesap yetkisi/visibility/protected branch kapıları insan
  tarafından doğrulanmalıdır.
- Offline mode yalnız açıkça verilen, mevcut bare yerel repo ve sentetik
  `hic.bridge.repository` identity marker'ını kabul eder; GitHub'a fallback yok.

### Kullanım

Önce B1.1 generator ile incelenmiş snapshot üretin. Publisher ayrı state alanı
ister; state'i source repo'ya veya credential içeren dizine koymayın.

```powershell
# Yerel fake-remote doğrulaması (gerçek GitHub değildir):
# Bare remote önceden sentetik hedef branch/identity ile test fixture tarafından hazırlanır.
dotnet .\src\AgentBridge\bin\Release\net10.0\AgentBridge.dll prepare-publication `
  --repo C:\SyntheticSource --source-repository synthetic-owner/source `
  --workspace C:\SyntheticPublication --destination synthetic-owner/destination `
  --branch handoffs/synthetic --project synthetic-project --offline-remote C:\SyntheticRemote.git

# Ayrı insan incelemesinden SONRA preview'daki exact digest:
dotnet .\src\AgentBridge\bin\Release\net10.0\AgentBridge.dll publish-publication `
  --repo C:\SyntheticSource --source-repository synthetic-owner/source `
  --workspace C:\SyntheticPublication --destination synthetic-owner/destination `
  --branch handoffs/synthetic --offline-remote C:\SyntheticRemote.git `
  --approval-digest <preview-digest> --approve-publication
```

Canlı GitHub için `--offline-remote` yerine prepare'da
`--credential-helper manager --approve-github-read`, publish'de
`--credential-helper manager --approve-github-push` gerekir; exact destination,
source repo, workspace, branch ve publish digest/approval parametreleri aynen
korunur. Bu görev bu bayrakların canlı kullanımını **yetkilendirmez**.
Önceden var olan, ayrı onaylanmış `handoffs/<slug>` branch gerekir.

```powershell
dotnet test .\tests\AgentBridge.Tests\AgentBridge.Tests.csproj -c Release --no-restore
dotnet test .\HumanInControl.sln -c Release --no-build --no-restore --filter "Category!=PostgreSQLIntegration"
```

### Doğrulama kapsamı ve açık sınırlar

6 Kasım 2026 B1.2 offline checkpoint'i:

- **141/141 Bridge testi PASS**, 0 fail/skip: korunan 100 B1.1 senaryosu +
  41 publisher/CLI senaryosu. Exact digest preview → explicit approval →
  fake-remote publication commit → remote SHA/bundle verification geçti.
- Mevcut solution Release build **PASS**, 0 warning/error. Güvenli offline
  regresyonlar **107/107 PASS** (API 71, Worker 36); PostgreSQL integration
  testleri explicit filtreyle çalıştırılmadı.
- Tracked ve yeni dosyalarda whitespace, Markdown link/fence ve scope
  kontrolleri geçti. Önceki B1.1 sentetik snapshot/pointer değiştirilmedi.
- Gerçek geliştirme HEAD'i `0f36c20dc4d01750f634aaad17ea2d1b6bd6e6e5`;
  staging boş, commit/push yok. Reliability worktree temiz ve aynı HEAD'de.
- İlk test koşusunda Windows Git object/pack readonly dosyaları fixture
  cleanup'ını engelledi. Yalnız task-owned sentetik temp dosyaların
  özniteliklerini düzelten fixture cleanup'ı sonrası final suite geçti;
  ilk koşudan kalan 40 açıkça belirlenen temp root temizlendi.
- Gerçek GitHub read/push/API write, Control Plane, DB, Podman, LLM,
  merge/PR ve geliştirme worktree commit'i **NOT RUN**.

Deterministik testler geçici source Git + ayrı local bare remote kullanır:
candidate, exact digest/changed candidate, source/destination/branch/index
retleri, secret/immutable/latest, local commit/ref verification, concurrent
remote advancement/push race, accepted/not-accepted unknown push sonucu,
idempotency, invalid remote content, config/state ve explicit offline
transport kapıları. B1.1 testleri de aynı suite içinde korunur.

Gerçek GitHub credential/permission/visibility/branch policy, proxy/TLS/ağ
kesintisi, GitHub connector/ChatGPT read ve kriptografik insan approval
registry'si doğrulanmadı. Publication Git blob doğrulaması bağımsız task/test
sonucu doğrulaması değildir. CI/Actions ve remote receipt artifact'ı bu
milestone'da kurulmadı.
