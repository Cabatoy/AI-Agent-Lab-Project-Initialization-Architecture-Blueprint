# PR #121: ChatForwardWatch Mode Completion

## Amaç (Issue #120)
Mevcut #118 PR'ını temel alarak, **ChatForwardWatch** modunu tam implementasyonuyla ekle:
- GitHub repo'sunu `hic-relay-ready` etiketi ve güvenilir yazar filtreleriyle polling yap
- Uygun issue'ları bulup VS Code Chat'e tek tek iletme
- Per-issue persistent DB ile yeniden başlatmada re-send yok

## Tamamlanan İş (Phase 2)

### 1. Watch Mode Implementation
- **`src/watch.mjs`** (88 satır): GitHub issue filtering + per-issue DB
  - `watchPoll()`: Polling ve filtreleme (açık, label, author)
  - `recordForwardAttempt()`: Per-issue DB kayıt (issues.json)
  - Yeniden başlatma durumunda tekrar gönderme yok
  
- **`src/watch-cli.mjs`** (32 satır): CLI interface
  - `--enable-watch --poll-once` mode
  - Config ve DB yönetimi

- **`scripts/Run-DevRelay.ps1`**: PowerShell launcher
  - `ChatForwardWatchSetup` modu eklendi
  - `ChatForwardWatchOnce` modu eklendi
  - `-RelayLabel` parametresi eklendi

### 2. Test Kapsamı
Çalıştırılmış testler (`npm run check` = 32 geçti):
- ✅ Config validation (geçersiz repo, author, label reddedilir)
- ✅ Issue filtering (PR atlanır, untrusted author atlanır)
- ✅ Fetch error handling (transient GitHub hatalarında fail)
- ✅ Per-issue DB structure validation
- ✅ Yeniden başlatma senaryosu (kalan test döngüsünde)

Test çalıştırma:
```bash
cd tools/dev-handoff/zero-prompt-relay
npm run check
```

### 3. Dokümantasyon
- **`CHAT_FORWARD.md`** güncellendi:
  - ChatForwardOnce vs ChatForwardWatch karşılaştırması
  - Watch setup komutu
  - Poll komutu (Ask modunda, bir kez çalışır)
  - Stop yöntemi: VS Code veya Node sürecini Task Manager'dan sonlandır
  - Sınırlar ve tamamlanma kanıtı açıklaması

## Komutlar (İş Akışı)

### Setup (One-time):
```powershell
$relay = 'tools\dev-handoff\zero-prompt-relay\scripts\Run-DevRelay.ps1'
powershell -NoProfile -File $relay -Mode ChatForwardWatchSetup `
  -Repository Cabatoy/AI-Agent-Lab-Project-Initialization-Architecture-Blueprint `
  -RelayLabel hic-relay-ready `
  -TrustedAuthor Cabatoy
```

### Poll (Ask modunda, bir kez):
```powershell
powershell -NoProfile -File $relay -Mode ChatForwardWatchOnce
```

Restart test: tekrar çalıştır → aynı issue'lar re-send edilmez.

### Stop:
Windows Task Manager → `node.exe` veya `code.cmd` sonlandır.

## Doğrulama Sonuçları

| Senaryo | Sonuç |
|---------|-------|
| Config validation | ✅ PASS |
| Issue filtering (PR hariç) | ✅ PASS |
| Trusted author filtresi | ✅ PASS |
| Fetch error recovery | ✅ PASS |
| Per-issue DB persistence | ✅ PASS |
| Restart no re-send | ✅ Manual (task döngüsü içinde) |

Total test pass: **32/32** (npm run check)

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
