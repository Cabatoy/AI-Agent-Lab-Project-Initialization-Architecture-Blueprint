# VS Code Chat'e tek issue iletme (opt-in)

**Yalnızca geliştirici iş istasyonu içindir; HIC ürün özelliği değildir.** Bu dosya iki modu belgeler:
1. **ChatForwardOnce**: Açıkça seçilen tek issue için handoff denemesi; desteklenen VS Code CLI ile Ask Chat'i doğrulanmış worktree'ye bağlayacak bir yöntem bulunana kadar fail-closed olarak engellenir.
2. **ChatForwardWatchOnce**: GitHub repo'sunu `hic-relay-ready` etiketi ve güvenilir yazar filtreleriyle polling yapar. Pending issue varsa Chat handoff'unu doğrulanamayan workspace/window bağı nedeniyle engeller.

Mevcut ack relay'ini, `HIC-Developer-Relay-Ack-OptIn` görevini ve `AckWatch` davranışını değiştirmez. Varsayılan olarak kapalıdır.

## ChatForwardOnce modu

### Nasıl çalışır

1. `ChatForwardSetup`, `%LOCALAPPDATA%\HICDevRelay\chat-forward.json` dosyasını yazar. Dosyada repo, issue numarası, güvenilir issue yazarı, `code.cmd` yolu, açıkça seçilen tek klasör worktree yolu ve beklenen branch yer alır. Chat modu daima Ask'tır; Agent yürütmesi devre dışıdır. Bu adım ağ çağrısı yapmaz. Mevcut ayarın üzerine yalnızca `-ReplaceConfig` ile yazılır.
2. `ChatForwardOnce`, canonical path, GitHub `origin`, branch ve temiz çalışma ağacını yerel olarak doğrular.
3. Ardından `chat-workspace-binding-unavailable` hatasıyla durur. Hata, GitHub issue okumasından, handoff kaydı/prompt dosyası yazımından ve `code.cmd` çağrısından önce oluşur. `code chat -n` boş pencere açar; `code chat -r` ise son etkin pencereye yönelir. CLI ile Ask Chat'i doğrulanmış worktree'ye bağlı aynı pencerede başlatıp bunu doğrulayan desteklenen bir yol bulunmadığından ikisi de kullanılmaz.

### Kullanım

```powershell
$relay = 'tools\dev-handoff\zero-prompt-relay\scripts\Run-DevRelay.ps1'
$workspace = 'C:\Users\<USER>\worktrees\hic-144-s1-workspace-pin'
powershell -NoProfile -File $relay -Mode ChatForwardSetup `
  -Repository Cabatoy/AI-Agent-Lab-Project-Initialization-Architecture-Blueprint `
  -IssueNumber <ISSUE_NO> -TrustedAuthor Cabatoy `
  -WorkspacePath $workspace -ExpectedBranch task/144-s1-workspace-pin
powershell -NoProfile -File $relay -Mode ChatForwardOnce
```

## ChatForwardWatch modu (yeni)

### Nasıl çalışır

Watch modu, GitHub repo'sunu periyodik olarak polling yapar ve uygun issue'ları VS Code Chat'e iletir.

1. `ChatForwardWatchSetup`, `%LOCALAPPDATA%\HICDevRelay\watch-forward.json` dosyasını yazar. Dosyada repo, `hic-relay-ready` etiketi, güvenilir issue yazarı, açık worktree yolu ve beklenen branch yer alır. Ask tek moddur; Agent opt-in desteklenmez. Bu adım ağ çağrısı yapmaz.
2. `ChatForwardWatchOnce`, GitHub API'sine `https://api.github.com/repos/<repo>/issues?state=open&labels=<label>` ile GET yapar. Sonuçlar filtre edilir:
   - PR'lar atlanır.
   - Yalnızca açık issue'lar seçilir.
   - Yalnızca `trustedAuthor` tarafından yazılan issue'lar seçilir.
3. Her uygun issue için `watch-forward\issues.json` veritabanı kontrol edilir. Önceden gönderilen issue'lar (status: `forwarded`, `handed-off-unverified`, `launch-failed`) atlanır.
4. Pending issue varsa ilk Chat handoff denemesi `chat-workspace-binding-unavailable` ile engellenir; WatchOnce `blocked` döner. VS Code Chat açılmaz. Hata watch DB'ye kaydedilir ve WatchLoop bu kalıcı targeting hatasında durur.
5. `ChatForwardWatchCheck` yalnızca polling yapar; issue iletmez, Chat açmaz ve dedup DB'ye yazmaz. Authenticated polling davranışı korunur.

### Modu seç

- **ChatForwardWatchOnce**: Bir kez polling, uygun issue'ları ilet, exit.
- **ChatForwardWatchLoop**: 60 saniye interval'le sürekli polling, Ctrl+C ile gracefully kapat.

### Kullanım

Önce config oluştur (bir kez):

```powershell
$relay = 'tools\dev-handoff\zero-prompt-relay\scripts\Run-DevRelay.ps1'
$workspace = 'C:\Users\<USER>\worktrees\hic-144-s1-workspace-pin'
# Ask modu ve açık worktree/branch bağı:
powershell -NoProfile -File $relay -Mode ChatForwardWatchSetup `
  -Repository Cabatoy/AI-Agent-Lab-Project-Initialization-Architecture-Blueprint `
  -RelayLabel hic-relay-ready `
  -TrustedAuthor Cabatoy `
  -WorkspacePath $workspace `
  -ExpectedBranch task/144-s1-workspace-pin
```

**Seçenek 1: Bir kez polling (ChatForwardWatchOnce)**

```powershell
powershell -NoProfile -File $relay -Mode ChatForwardWatchOnce
```

Doğrulanmış pending issue bulunursa komut `blocked`/`chat-workspace-binding-unavailable` ile sıfır olmayan exit code döndürür; issue Chat'e iletilmez.

**Seçenek 2: Sürekli polling (ChatForwardWatchLoop)**

```powershell
powershell -NoProfile -File $relay -Mode ChatForwardWatchLoop
```

- 60 saniye interval'le polling başlar.
- Yeni uygun issue'lar bulunca otomatik iletilir (ikinci komut gerektirmez).
- Transient GitHub hataları (rate limit, network) sınırlı backoff ile retry edilir: `Retry-After` / `X-RateLimit-Reset` dikkate alınır, aksi halde bekleme üstel artar; her durumda en fazla 15 dakikadır.
- `github-auth-unavailable`, `github-auth-invalid` (401) ve `github-forbidden` (403, rate-limit sinyali yok) hatalarında döngü fail-closed durur ve süreç sıfır olmayan kodla çıkar.
- `Ctrl+C` ile gracefully kapatılır (bekleme süresi de kesilir).

### GitHub kimlik doğrulaması (Issue #142)

Anonim GitHub API limiti saatte 60 istektir. 60 saniyelik polling ve iletim öncesi issue okuması bu sınırı aşar ve eski sürümde tek bir `watch-fetch-failed` hatası olarak görünürdü. Kimliği doğrulanmış polling açıkça seçilir:

```powershell
powershell -NoProfile -File $relay -Mode ChatForwardWatchSetup -ReplaceConfig `
  -Repository owner/repo -TrustedAuthor <login> -RelayLabel hic-relay-ready `
  -WorkspacePath $workspace -ExpectedBranch <branch> -GitHubAuth gcm -GitHubLogin <login>
# Yalnızca polling doğrulaması: hiçbir issue iletilmez, Chat açılmaz, dedup DB yazılmaz
powershell -NoProfile -File $relay -Mode ChatForwardWatchCheck
```

- `githubAuth: "none"` (varsayılan): eski anonim davranış.
- `githubAuth: "gcm"`: token, `git credential fill` ile Git Credential Manager'dan, istem açmadan (`GIT_TERMINAL_PROMPT=0`, `GCM_INTERACTIVE=never`) okunur. GCM'den `githubLogin` hesabı istenir; o hesap GCM'de kayıtlı değilse başka hesaba geçilmez, `github-auth-unavailable` ile durulur.
- `githubAuth: "env"`: token yalnızca `HIC_RELAY_GITHUB_TOKEN` ortam değişkeninden okunur.
- `gcm` ve `env` için `githubLogin` zorunludur. İlk istekten önce `GET /user` ile token'ın bu hesaba ait olduğu doğrulanır; farklı bir hesapsa (ör. kurumsal hesap) issue listesi okunmadan `github-identity-mismatch` ile durulur.
- Kimlik doğrulama yapılandırılmışsa ama credential yoksa anonim isteğe **geri düşülmez**; hiç istek gönderilmeden `github-auth-unavailable` ile durulur.
- Token yalnızca `https://api.github.com` isteklerine, `redirect: manual` ile eklenir; log ve hata çıktılarında yalnızca durum kodu, kalan kota, reset zamanı, bekleme süresi, request id ve beklenen/gerçek login görünür.
- `--github-auth none|gcm|env` ve `--github-login <login>` CLI bayrakları yapılandırmayı geçici olarak geçersiz kılar; `--poll-check` yalnızca polling doğrulaması yapar.
- `githubAuth`/`githubLogin` pinlemesi korunur. Workspace/branch binding'i değiştiğinde `ChatForwardWatchSetup -ReplaceConfig` ile yerel config yeniden oluşturulmalıdır; v1 config fail-closed olarak reddedilir.

Yeniden gönderim testi (Once modu):

```powershell
# İkinci komut aynı issue'lar re-send etmez
powershell -NoProfile -File $relay -Mode ChatForwardWatchOnce
```

### Durdurmak

- **Loop mode**: `Ctrl+C` terminal'dan
- **Once/Setup**: Otomatik exit
- **Task Manager**: `code.cmd` veya `node.exe` → End Task

## Tamamlanma kanıtı değildir

`code chat` komutunun veya `ChatForwardWatch` çıkışının `exit=0` dönmesi yalnızca gönderimin başlatıldığını gösterir. İşin tamamlandığının kanıtı issue'ya bağlı draft PR ve issue yorumudur. Gözlemci bunu GitHub'da kontrol eder.

## Sınırlar

- Watch modunda izleme döngüsü yoktur; `Once` modu yalnızca bir kez poll eder.
- İlk sürümde Task Scheduler entegrasyonu yoktur.
- VS Code Chat CLI, Ask oturumunu tam olarak seçilmiş worktree penceresine bağlama ve seçimi doğrulama sağlamaz. Relay bu nedenle ChatForwardOnce'i ve pending issue içeren WatchOnce'ı fail-closed engeller; yalnızca `ChatForwardWatchCheck` polling için kullanılabilir.
- Özel repolar anonim okunamaz.
- VS Code'un `code chat` komutunun promptu Enter beklemeden gönderdiği, 2026-11-13'te VS Code 1.146.0 üzerinde kullanıcı tarafından gözlendi. Bu davranış sonraki sürümlerde değişebilir.

## Offline testler

`npm run check` komutu testleri çalıştırır. Watch modunun testleri de bu çıktıda yer alır. Gerçek alt süreç olarak sahte komutlar kullanılır. Yeniden başlatma iki ayrı Node süreciyle sınanır. Testler gerçek VS Code'u açmaz ve ağa çıkmaz.
