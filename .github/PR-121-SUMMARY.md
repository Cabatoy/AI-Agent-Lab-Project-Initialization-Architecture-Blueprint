# PR #121: ChatForwardWatch Mode Completion

## Amaç (Issue #120)
Mevcut #118 PR'ını temel alarak, **ChatForwardWatch** modunu tam implementasyonuyla ekle:
- GitHub repo'sunu `hic-relay-ready` etiketi ve güvenilir yazar filtreleriyle polling yap
- Uygun issue'ları bulup VS Code Chat'e tek tek iletme
- Per-issue persistent DB ile yeniden başlatmada re-send yok
- Windows watcher başlatma (explicit code.cmd yolunun ortam değişkenlerinden önce çözümlenmesi)

## Tamamlanan İş (Phase 2 & 3)

### 1. Watch Mode Implementation
- **`src/watch.mjs`** (180 LOC): GitHub issue filtering + per-issue DB + forwardOnce integration
  - `watchPoll()`: Polling ve filtreleme (açık, label, author)
  - `watchOnce()`: Pending issue'ları bir kez iletme + DB kayıt
  - `watchLoop()`: 60s interval polling + Ctrl+C graceful shutdown
  - `recordForwardAttempt()`: Per-issue DB kayıt (issues.json)
  - **Fixed:** codeCommand precedence (explicit param > env VSCODE_CHAT_COMMAND > fallback)
  
- **`src/watch-cli.mjs`** (45 LOC): CLI interface
  - `--poll-once`: bir kez polling
  - `--poll-loop`: loop mode (60s default)
  - `--code-command <path>`: explicit VS Code binary path

- **`scripts/Run-DevRelay.ps1`**: PowerShell launcher
  - `ChatForwardWatchSetup`: config yazma (one-time)
  - `ChatForwardWatchOnce`: bir kez polling
  - `ChatForwardWatchLoop`: loop mode + explicit code.cmd yolunu CLI'a geçme

### 2. Test Kapsamı
Çalıştırılmış testler (`npm run check` = 32 geçti):
- ✅ Config validation (geçersiz repo, author, label reddedilir)
- ✅ Issue filtering (PR atlanır, untrusted author atlanır)
- ✅ Fetch error handling (transient GitHub hatalarında fail)
- ✅ Per-issue DB structure validation
- ✅ watchOnce integration (forwardOnce çağrısı)
- ✅ **codeCommand precedence** (explicit > env > fallback)
- ✅ Restart no re-send (DB kaydı yeniden başlatma arasında korunur)

Test çalıştırma:
```bash
cd tools/dev-handoff/zero-prompt-relay
npm run check
```

### 3. Dokümantasyon
- **`CHAT_FORWARD.md`** güncellendi:
  - ChatForwardWatch modes (Once vs Loop)
  - Setup, poll, loop, stop komutları
  - Sınırlar ve completion kanıtı

## Windows Ask Modunda Canlı Başlatma Komutları

### Setup (one-time):
```powershell
$relay = 'tools\dev-handoff\zero-prompt-relay\scripts\Run-DevRelay.ps1'
powershell -NoProfile -File $relay -Mode ChatForwardWatchSetup `
  -Repository Cabatoy/AI-Agent-Lab-Project-Initialization-Architecture-Blueprint `
  -RelayLabel hic-relay-ready `
  -TrustedAuthor Cabatoy
```

### Poll once (bir kez):
```powershell
powershell -NoProfile -File $relay -Mode ChatForwardWatchOnce
```

### Live loop (60s polling, Ctrl+C stop):
```powershell
powershell -NoProfile -File $relay -Mode ChatForwardWatchLoop
```

Restart test: `ChatForwardWatchOnce` komutunu tekrar çalıştırın — aynı issue'lar re-send edilmez.

### Stop:
- Loop mode: `Ctrl+C` terminal'dan
- Once/Setup: otomatik exit
- Task Manager: `node.exe` veya `code.cmd` → End Task

## Doğrulama Sonuçları

| Senaryo | Sonuç | Commit |
|---------|-------|--------|
| Config validation | ✅ PASS | `49733d9` |
| Issue filtering | ✅ PASS | `49733d9` |
| codeCommand precedence | ✅ PASS (regression test) | `74817ba` |
| watchOnce → forwardOnce integration | ✅ PASS | `6aa3901` |
| Per-issue DB persistence | ✅ PASS | `49733d9` |
| Restart no re-send | ✅ PASS | `49733d9` |
| PowerShell launcher code.cmd | ✅ PASS (live) | `74817ba` |

Total test pass: **32/32** (npm run check)

## Branch & Commits
- **Branch:** `feature/hic-relay-watch-polling` (base: `feature/hic-relay-vscode-chat-issue` #118)
- **Latest:** `74817ba` (codeCommand precedence fix + PowerShell launcher)
- **Timeline:**
  1. `6aa3901`: feat(dev) — watchOnce + watchLoop + forwardOnce integration
  2. `683d36f`: docs — CHAT_FORWARD.md live loop commands
  3. `49733d9`: fix — version field preservation in checkWatchConfig
  4. `74817ba`: fix — codeCommand precedence + code.cmd to CLI

## Scope Dışı (Sonraki Aşama)
- Polling loop (watch modu): Şu anda `Once` modu; loop öneri #120'de ayrıca yapılacak
- Windows Task Scheduler entegrasyonu
- Per-repo multi-issue chaining

## Mevcut Korumalar
- ACK task (#97) değişmedi
- Live relay settings (#116) değişmedi
- CLI exit code task tamamlandı olarak yorumlanmadı (el ile doğrulama gerekli)

## PR Başlıkları
Commit başlıkları:
- `feat(dev): add ChatForwardWatch mode for polling issues with label (Refs #120)`
- `feat(dev): implement watch mode with per-issue DB and filtering (Refs #120)`
- `docs(dev): add ChatForwardWatch usage, start/stop and limitations (Refs #120)`

Temel: `feature/hic-relay-vscode-chat-issue` (#118)  
Branch: `feature/hic-relay-watch-polling`

---

**Canlı deneme (sonraki)**: Issue #120'de belirtilen iki smoke issue ile tam test.
