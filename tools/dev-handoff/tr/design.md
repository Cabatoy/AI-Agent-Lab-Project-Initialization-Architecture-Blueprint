# #83 — D0 keşif ve D1 developer adaptörü

## Yönetici özeti

**Kesinleşmiş kapsam:** yalnız yerel geliştirici ChatGPT ↔ Fusion handoff'u.
HIC son kullanıcı özelliği değildir; ürün UI/runtime/release'e bağlanmaz.
`manual` varsayılandır; CLI ayrıca `--enable-manual` olmadan inert kalır.
Ürün execution durumu veya mevcut koruma kapıları değiştirilmez.

**Öneri / ADR-083-D0 (onay bekliyor):** belgelenmiş Agents draft URL'sini
manuel adaptör olarak kullan; başlatma veya completion API'si varsayma.
Cloud ürün runtime'ı yerel VS Code/Fusion'a bağımlı olmayacaktır.
Bu araç yalnız geliştiricinin isteğe bağlı local yardımcısıdır.

## Teknik ek

### 10 Kasım 2026 kurulu arayüz gerçeklik tablosu

| Yüzey | Kanıt / sözleşme | Kullanım / sınır |
|---|---|---|
| VS Code | `package.json` 1.145.2; `product.json` commit `c4453f8ea430795f941c1b2ff6c9321135a74a02`, stable | Salt okunur keşif; kurulum/ayar değişmedi |
| Birlikte gelen Copilot | `extensions/copilot/package.json`: `GitHub.copilot-chat` 0.73.0 | Ayrı kurulu `Fusion` adlı CLI/extension doğrulanmadı; kullanıcı adı ayrı API kanıtı değildir |
| `code chat --help` | `chat [options] [prompt]`; `--mode ask/edit/agent` (default agent), `--add-file`, `--new-window`, `--reuse-window` | Help okundu, prompt gönderilmedi. Doğrudan chat çalıştırır; güvenli draft-only handoff için **kullanılmaz**. Exit code completion değildir |
| Standalone `copilot` shim | `--help`: CLI bulunamadı, exit 127 | Otomatik kurulum yapılmadı; shim'in bulunması CLI/runtime'ın hazır olduğu anlamına gelmez |
| `vscode://agents/new` | Resmî URL docs + kurulu `out/mainImpl.js` ayrıştırıcısında `agents` authority, `/new`, `prompt`, `workspace`; sonuç draft/inputText | **Desteklenen draft-only yüzey**. Prompt URL-encoded; workspace file URI. Göndermez; mevcut taslak korunabilir |
| Extension commands / `onUri` | Manifest command katkıları ve URI activation var | İsim/activation varlığı dış otomasyon parametre sözleşmesi değildir. Özel command/URI tahmin edilmez |
| Extension exports / public Fusion SDK | Bu incelemede dış start/completion sözleşmesi doğrulanmadı | Bundled private API, WebSocket, endpoint, port veya token kullanılmaz |
| Bu sohbetin session/automation araçları | Provider aracılı chat/schedule araçları görünür | Developer CLI'den çağrılabilir dış Fusion API kanıtı değildir; çağrılmadı, schedule/session oluşturulmadı |
| Completion / webhook / MCP listener | Kurulu Fusion için dış completion callback sözleşmesi doğrulanmadı | Listener, poller veya webhook sunucusu yok |

Belge kaynakları:
[CLI/chat ve Agents draft URL](https://code.visualstudio.com/docs/configure/command-line#_prepare-a-new-agent-session-draft),
[Agents window](https://code.visualstudio.com/docs/agents/run/agents-window).
Belgeler 10 Kasım 2026'da okundu. Kurulu main implementation ve extension
manifest hash'leri [sabit keşif kaydında](../installed-interface.json).
Üçüncü taraf extension'lar veya kimlik bilgileri incelenmedi.
Canlı composer kabulü **not-run**; statik sözleşme desteği ile kullanıcı
tarafından yapılmış uçtan uca kabulü birbirine karıştırmayın.

### Tek otorite ve mevcut HIC eşlemesi

| Bilgi | Otorite / mevcut kaynak | D0–D1 davranışı |
|---|---|---|
| İş sırası | GitHub #75, insan incelemeli ordered allowlist | Gövdeyi instruction olarak çalıştırmaz; exact `updated_at` ve issue metadata uzlaştırır |
| Task kimliği/durumu | `src/ControlPlane/Program.cs`: GET `/tasks`, `/tasks/{id}`; durable `tasks` | Yalnız operatör export'undan id/status kabul eder; yeni task deposu/cursor yok |
| Claim / owner | `ExecutionOwnershipStore`: executionId, workerId, generation; mevcut transaction/audit | **Çağırmaz**: claim endpoint task'ı Executing yapar. Developer adaptörü Worker kimliği uyduramaz |
| Geçiş/audit | `TaskTransitionStore`, `task_status_transitions`, `task_executions` | POST status/approve/retry veya DB yazımı yok |
| İlişkili sonuç kanıtı | `AgentBridge/Contracts.cs`, `SnapshotProducer.cs`: taskId/executionId/correlationId, source SHA; caller attestation ayrı | Bu snapshot'ı imzalı completion gibi değerlendirmez; publisher/credential kullanmaz |
| Branch/worktree/SHA | Salt okunur Git baseline + GitHub branch ref | Exact eşleşme ve temiz ayrı worktree; sonradan değişince tekrar hazırlama |
| PR/test/CI | GitHub exact-head verifier gerektirir | D1 completion verifier yok; yeşil CI veya kapanmış Issue yetki/başarı sayılmaz |

Mevcut API'de #75 Issue ↔ HIC taskId için doğrulanmış otomatik eşleme ve
developer-specific read/export endpoint'i yoktur. Bu yüzden operatör
incelemeli eşleme gerekir; boşluk yeni production Task/Attempt oluşturularak
veya canlı migration ile kapatılmaz. Task id'si olmayan gerçek iş bloke kalır.

**Gelecekteki adaptör sözleşmesi (uygulanmadı):** taskId, executionId ve
generation mevcut ownership otoritesinden gelmeli; Issue/queue revision,
authorized scope, predecessor taskId, branch/worktree/expected base SHA,
completion manifest digest, PR/head/test/CI kanıtları aynı mevcut attempt/run
ilişkisine bağlanmalı. İlgili şema/yetki kararı ayrı onay gerektirir.
Yerel inbox bu ilişkinin durable yürütme kaydı değildir.
İmzalanmamış/forged/replayed completion alınırsa state ilerletilmemeli.
Bir sonraki iş için claim yalnız önceki immutable verification/audit kabulünden
sonra, tek ownership otoritesi tarafından verilmelidir.

### Tehdit modeli ve durma kapıları

| Tehdit | D1 önlemi / eksik sınır |
|---|---|
| Issue prompt injection, shell/secret talebi | Body/title/comment taşınmaz; sabit template, strict alanlar ve developer-only path allowlist |
| Yanlış repo/branch/worktree/eski SHA | Git + remote ref uzlaştırma; yeni temiz ayrı worktree zorunlu |
| Çalınmış/geniş publisher credential | Credential yolu yok; anonymous public GET; 401/403 açık stop |
| Eski onay / uyku / ağ kesintisi | Beş dakika input süresi, timeout, retry yok; başarısızlıkta artifact yok |
| Replay/duplicate/yarım yazım | Digest + exclusive artifact; üzerine yazılmaz. Bu bir execution lease değildir |
| Sahte local completion / CI no-run | Completion alanları reddedilir; `completionVerified=false`; otomatik ilerleme yok |
| Birden çok coordinator/yeniden başlatma | Çalışan coordinator yok. Aynı export aynı taslak; D2 ownership/recovery teslim edilmedi |
| Kurulu sürüm drift / private API | Pinlenmiş fingerprint, değişimde stop; private dispatch API kullanılmaz |
| Browser/script injection | Script/form/resource içermeyen escaped HTML/CSP; URL yalnız draft scheme |
| Expired statik link / mevcut composer taslağı | Link izin değildir; operatör yeniden doğrular. Tıklama anı atomik preflight garantisi yok |
| Model/host approvals override | URL bunları seçmez/kilitlemez; gönderim öncesi insan kontrolü. Prompt metni security boundary değildir |
| Riskli üçüncü iş | Sırayı atlamadan `HUMAN_APPROVAL_REQUIRED`; auto-safe desteklenmez |

### ADR seçenekleri ve operasyon/maliyet

1. **Agents draft URI (önerilen, D1 uygulandı):** model/API çağrısı yapmadan
   local artifact ve manuel composer taslağı; ek servis yok, yalnız açık
   hazırlamada bounded public metadata GET. İnsanın mesajı servisler arasında
   kopyalaması gerekmez; insan inceleme/gönderim kapısı korunur.
2. `code chat` / session dispatch: prompt'u çalıştırabilir; model/approval ve
   completion otoritesi bu scope'ta yeterli değil. Seçilmedi.
3. Keyboard/UI scripting veya private endpoint: kırılgan ve güvensiz,
   seçilmedi.
4. Native HIC executor: #63 ve ayrı izolasyon/onay kapılarından önce kapsam dışı.

Harcama yaratan model çalışması otomatik yapılmaz. Kurulu URL handler için
manuel operator kabulü, canlı read-only ledger eşleme tasarımı ve D2 durable
ownership/verifier sonraki **açık işlerdir**, bu teslimde onaylanmış karar veya
çalışan auto-resume değildir. Test kanıtı [README](../README.md)'dedir.
