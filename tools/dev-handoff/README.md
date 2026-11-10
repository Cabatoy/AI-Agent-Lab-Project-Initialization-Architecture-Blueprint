# Developer handoff — Issue #83 D0–D1

## Yönetici özeti

Bu araç yalnız geliştiricinin **ChatGPT ↔ yerel Fusion** iş akışı içindir.
HIC ürünü, Dashboard, runtime, dağıtım veya release bileşeni **değildir**.
Varsayılan kapalıdır: argümansız yalnız kullanım metni döner. Servis,
scheduler, executor, credential okuyucu veya otomatik oturum başlatıcı yoktur.
Yeni paket/kurulum gerekmez; Node.js 24 ve Git yeterlidir.

Kurulu VS Code **1.145.2**, commit
`c4453f8ea430795f941c1b2ff6c9321135a74a02`, birlikte gelen
`GitHub.copilot-chat` **0.73.0** salt okunur incelendi.
Resmî `vscode://agents/new?prompt=…&workspace=…` sözleşmesi ve kurulu URL
ayrıştırıcısı doğrulandı: **prompt gönderilmez**, yeni oturum composer'ına
taslak hazırlanır. Mevcut composer taslağı korunabilir; kullanıcı metni,
workspace'i, model/host ve approval ayarlarını kontrol etmelidir.
Kurulu dosya fingerprint'leri [arayüz kaydındadır](installed-interface.json).
Editör güncellenirse araç yeniden inceleme yapılana kadar reddeder.

**Teslim:** dev-only CLI, gözden geçirilmiş #75 sıra eşlemesinden ilk uygun
Draft task için branch/SHA bağlı taslak ve tek bağlantılı yerel inbox üretir.
İnsan bağlantıyı açıp taslağı inceler; gönderim ayrıca insan eylemidir.
Klavye/fare otomasyonu, `code chat`, agent dispatch veya completion callback
çağrılmaz. **Canlı Fusion tıklama/gönderim kabulü yapılmadı.**

İngilizce karşılık: [EN](en/design.md).
Keşif, tehdit modeli ve önerilen ADR: [TR](tr/design.md).

## Teknik ek

### Çalıştırma ve kapalı varsayılan

Repo kökünden:

```powershell
node .\tools\dev-handoff\cli.mjs
node --test .\tools\dev-handoff\handoff.test.mjs
```

Yalnız açık manuel kullanım için; aşağıdaki yollar operatörün kendi
**gözden geçirdiği**, secret içermeyen dosyalara işaret etmelidir:

```powershell
node .\tools\dev-handoff\cli.mjs prepare --enable-manual `
  --input <reviewed.json> --approved-sha256 <64-hex-digest> `
  --installation <versioned-VS-Code-resources-app-directory> `
  --out <existing-directory-outside-the-target-worktree>
```

`--installation`, `package.json`, `product.json`, `extensions\copilot` ve
`out\mainImpl.js` içeren dizindir; kullanıcı profilini/settings/secrets'i
okutmayın. CLI bu kurulu dosyaları yalnız okur ve hash karşılaştırır.

Onay digest'i **input dosyasının tam byte'larının SHA-256** değeridir.
Bu bir imza, kimlik doğrulama veya uzaktaki içerik için izin değildir:
dosyayı/hash'i oluşturmak tek başına güven kararı sayılmaz. Operatör önce
alanları, #75 sırasını ve HIC Task eşlemesini gözden geçirir.
Dosya bu araç tarafından GitHub Issue gövdesinden otomatik türetilmez.

CLI yalnız açık `prepare --enable-manual` sırasında:

1. En fazla 64 KiB input ve beş dakikalık inceleme penceresini doğrular.
2. İncelenmiş kurulu arayüz fingerprint'ini kontrol eder.
3. Ayrı Git worktree, temiz baseline, sabit repository, branch ve SHA okur.
   `origin` credential içeren URL ise reddeder; URL'yi hata çıktısına yazmaz.
4. **Credential kullanmadan**, yalnız sabit public GitHub repository'sinden
   #75 ve allowlist'teki en çok sekiz Issue'nun number/state/updated_at/URL
   alanları ve branch ref SHA'larını uzlaştırır. Her GET sekiz saniye,
   redirect/retry yoktur; Issue body/title/comment prompt'a girmez.
   Private repo/auth ihtiyacında durur; publisher broker kullanılmaz.
5. Baseline'ı yeniden okur ve süreyi yeniden doğrular. İlk tamamlanmamış
   task Draft değilse, Issue açık değilse veya insan onayı gerekiyorsa durur.
   Sıradaki uygun öğeyi aramak için engellenmiş task'ı atlamaz.
6. Harici output dizininde content-addressed, exclusive oluşturulan
   `handoff.json` + `inbox.html` yazar. Inbox'taki **Prepare next task draft**
   bağlantısı yalnız belgelenmiş Agents taslak URL'sidir. Açmak/göndermek
   CLI tarafından yapılmaz.

Aynı artifact veya yarım kalmış yazım tekrarında açık hata verilir; üzerine
yazma, silme veya otomatik recovery yapılmaz. Aynı input'tan yeniden hesaplanan
taslak aynıdır; farklı yeni output dizininde yeniden üretilebilir.
Bu rezervasyon **execution claim/lease veya tek aktif coordinator garantisi
değildir**. Birden çok kopya açılabilir; hiçbir kopya task yürütmez.

### Input sözleşmesi

Ek/bilinmeyen alanlar reddedilir. Serbest prompt, secret, command veya
completion payload alanı yoktur.

| Alan | Anlam |
|---|---|
| `version`, `mode` | `hic.dev-handoff/1`, yalnız `manual` |
| `repository`, `queueIssue` | Sabit repo, `75` |
| `queueUpdatedAt` | İnsan incelemesindeki GitHub #75 `updated_at` |
| `observedAt`, `expiresAt` | UTC, saniye hassasiyeti; en çok beş dakika |
| `provenance` | CLI yalnız `operator-export`; `synthetic` yalnız offline test |
| `worktree` | Tam, ayrı ve temiz worktree yolu |
| `items` | İnsan tarafından #75 sırasına eşlenmiş 1–8 öğe |
| `tasks` | Mevcut HIC Task export'undan yalnız `{id,status}` |

Her item tam olarak `issue`, `issueUpdatedAt`, `taskId`, `branch`, `head`,
`scope`, `predecessor`, `requiresApproval` içerir. İlk predecessor `null`,
diğerleri hemen önceki taskId'dir. Scope yalnız bu developer aracının
`tools/dev-handoff/` altındaki açık `.md/.mjs/.json` dosyalarıdır.
Ürün yolları, traversal ve çalıştırılacak komutlar kabul edilmez.
Ürün geliştirme kuyrukları bu D1 allowlist'ine dahil değildir.

Task export'u **bağımsız doğrulanmış canlı ledger okuması değildir**.
Completed + kapalı Issue yalnız manuel taslak sıralaması için ipucudur;
CI veya gerçek completion doğrulanmış sayılmaz. `completionVerified=false`
her raporda açıkça taşınır. HIC Task eşlemesi yoksa UUID uydurmayın:
canlı kullanım için durun. Bu nedenle repo için hazır/aktif bir production
queue dosyası veya kendi task durum deposu eklenmedi.

### Kanıt ve sınırlar

- Offline testler: iki ardışık **sentetik, yürütülmeyen** handoff, aynı
  ledger export'undan restart, üçüncü approval gate; geçersiz/replay/expired
  input, branch/SHA/repo/ownership, auth/network, kötü niyetli Issue metni,
  değişmiş kurulum, duplicate/yarım artifact ve sandbox Git worktree kontrolleri.
  Windows drive case ve 8.3 kısa yol alias'ları native canonical path ile
  karşılaştırılır; alias, ayrı worktree veya output sınırını aşamaz.
- Synthetic rapor clickable dispatch bağlantısı içermez. Sandbox'ta kod
  çalıştıran bir ajan, Worker, DB, model veya servis başlatılmaz.
- Gerçek kurulum fingerprint kontrolü geçti; Agents composer'ın gerçek
  operatör tıklaması/gönderimi test edilmedi. URL, mevcut taslağı değiştirmeyebilir.
- Statik link tıklama anında expiry/SHA doğrulayamaz; süre veya baseline
  değişince yeniden hazırlayın. URL bir approval/izin token'ı değildir.
- PR base, test sonucu ve exact-head CI kontrolü bu araçta **completion
  verifier olarak uygulanmadı**. GitHub green CI de yürütme izni değildir.
- D2'nin durable claim/lease, doğrulanmış completion, restart ownership ve
  auto-resume kabulü **teslim edilmedi**. Şu an ikinci state machine yok;
  ürün run ledger'ı değiştirilmedi. Bu D0–D1 manuel taslak sınırıdır.

CI yalnız bu klasördeki offline Node testlerini Windows/Linux'ta çalıştırır;
artifact publish, editor launch, runtime veya ürün build/deploy işi yoktur.
