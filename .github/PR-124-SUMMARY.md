# PR #124: HIC Relay Agent Mode Opt-in for Development Tasks

## Amaç (Issue #124)
PR #121 tarafında kurulup, **Agent modu seçeneğini** Windows watch setup'ında doğrulanmış local opt-in olarak ekle:
- **Ask'ı varsayılan tut** (breaking change yok)
- **Agent modu için doğrulanmış opt-in** (`agentModeOptIn` boolean)
- **PowerShell launcher** üzerinden `-ChatMode ask|agent` parametresi
- **CLI** üzerinden `--agent-mode` flag'i
- **Test coverage**: Ask default + Agent opt-in senaryoları

## Tamamlanan İş

### 1. watch.mjs Değişiklikleri
- **CHAT_MODE regex eklenmiş** (`/^(ask|agent)$/`)
- **checkWatchConfig()** güncellendi: `agentModeOptIn` boolean değeri destek
  - Falsy değer varsayılan olarak `false`
  - JSON'da eksikse `false`
- **watchOnce()** güncellendi: `chatMode` şu kurala göre belirleniyor:
  ```javascript
  const chatMode = c.agentModeOptIn ? 'agent' : 'ask';
  ```
- **forwardWatchIssueOnce()** güncellendi: config'den chatMode dinamik olarak belirleniyor

### 2. watch-cli.mjs Değişiklikleri
- **Usage mesajı güncellendi** (`--agent-mode` flag'i eklendi)
- **--poll-once komutu** `args.includes('--agent-mode')` kontrol ediyor
  - Varsa config'e `agentModeOptIn: true` spreads
- **--poll-loop komutu** aynı kontrol ve override yapıyor

### 3. Run-DevRelay.ps1 Güncellemeler
- **ChatForwardWatchSetup modu**:
  - `-ChatMode` parametresi destekleniyor (default: `ask`)
  - Config yazarken `agentModeOptIn = ($ChatMode -eq 'agent')`
  - Çıkış mesajı chatMode bilgisi veriyor
  
- **ChatForwardWatchOnce modu**:
  - `-ChatMode` ile CLI'a `--agent-mode` flag'i geçiliyor
  - Flag'i geçmekten kaçınılıyor (çok fazla parametre olmamak için)
  
- **ChatForwardWatchLoop modu**:
  - `-ChatMode` ile loop sırasında mode belirtiliyor

### 4. Test Kapsamı (watch.test.mjs)
- **testAskModeDefault()**: 
  - `agentModeOptIn: false` ile config oluştur
  - `chatMode='ask'` aldığını assert et
  - ✅ PASS
  
- **testAgentModeOptIn()**:
  - `agentModeOptIn: true` ile config oluştur
  - `chatMode='agent'` aldığını assert et
  - ✅ PASS

Toplam test sayısı: **34/34** (32 existing + 2 new agent-mode tests)

### 5. Dokümantasyon (CHAT_FORWARD.md)
- **ChatForwardSetup**: `-ChatMode ask` parametresi açıklandı (Ask modunu seçmek için)
- **ChatForwardWatchSetup**: `-ChatMode agent` parametresi açıklandı
- **Kullanım örnekleri**:
  - Ask modu (varsayılan) örneği
  - Agent modu opt-in örneği

## Branch & Commits

- **Branch**: `feature/hic-relay-agent-mode` (base: `feature/hic-relay-watch-polling` #121)
- **Commits**:
  1. `feat(dev): add Agent mode opt-in to watch config and CLI (Refs #124)`
  2. `test(dev): add Ask default and Agent opt-in test cases (Refs #124)`
  3. `docs(dev): document Agent mode setup and -ChatMode parameter (Refs #124)`

## Test Sonuçları

```
✅ All watch tests passed!
✔ test\watch.test.mjs (118ms)
  - ✅ chatMode defaults to ask
  - ✅ chatMode switches to agent when opted in
  - ✅ codeCommand precedence maintained
  - ... (31 other tests passing)

ℹ tests 34
ℹ pass 34
ℹ fail 0
```

## Mevcut Korumalar

- ✅ Ask varsayılan değer (breaking change yok)
- ✅ trusted-author, repository, label, deduplication checks korundu
- ✅ Existing VS Code permission prompts ve approval settings'i koruma (kontrol chat-forward.mjs üzerinde)
- ✅ Per-issue persistent DB re-send blokaçı korundu
- ✅ CLI exit code 0/non-zero semantiği korundu

## Sınırlar & Sonraki Aşama

- CLI parametresi `--agent-mode` binary flag olarak kalır (config'e yazılmaz)
- Agent mode'un gerçek VS Code Chat davranışı live trial'da doğrulanacak (issue #125'te)
- Local setup confirmation (Ctrl+C ile interactivity) henüz eklenmedi

## PR Başlık Önerileri

```
feat(dev): add Agent mode opt-in to watch polling setup (Refs #124)

- Add agentModeOptIn boolean to watch config with Ask as default
- Implement -ChatMode parameter in PowerShell launcher and --agent-mode CLI flag
- Add test cases for Ask default and Agent mode opt-in scenarios
- Update documentation with setup examples for both modes
- Preserve all existing security checks and permission prompts
```

---

**Canlı deneme**: Issue #125'te iki test issue üzerinde Ask ve Agent modlarının davranışını doğrula.
