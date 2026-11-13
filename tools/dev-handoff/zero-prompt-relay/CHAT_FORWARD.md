# VS Code Chat'e tek issue iletme (opt-in)

**Yalnızca geliştirici iş istasyonu içindir; HIC ürün özelliği değildir.** Bu dosya iki modu belgeler:
1. **ChatForwardOnce**: Yerel ayarda açıkça seçilen **tek bir GitHub issue'sunu** okur ve VS Code Chat'e iletir.
2. **ChatForwardWatchOnce**: GitHub repo'sunu `hic-relay-ready` etiketi ve güvenilir yazar filtreleriyle polling yapar, uygun issue'ları bulur ve VS Code Chat'e iletir.

Mevcut ack relay'ini, `HIC-Developer-Relay-Ack-OptIn` görevini ve `AckWatch` davranışını değiştirmez. Varsayılan olarak kapalıdır.

## ChatForwardOnce modu

### Nasıl çalışır

1. `ChatForwardSetup`, `%LOCALAPPDATA%\HICDevRelay\chat-forward.json` dosyasını yazar. Dosyada repo, issue numarası, güvenilir issue yazarı, `code.cmd` yolu ve Chat modu yer alır. `-ChatMode ask` ile Ask modunu seçebilirsin; varsayılan `agent`dır. Bu adım ağ çağrısı yapmaz. Mevcut ayarın üzerine yalnızca `-ReplaceConfig` ile yazılır.
2. `ChatForwardOnce`, önce yerel gönderim kaydına bakar. Kayıt varsa GitHub'a bile gitmeden `already-forwarded` döner.
3. Kayıt yoksa `GET https://api.github.com/repos/<repo>/issues/<n>` ile issue anonim olarak okunur. Yönlendirme takip edilmez. Yalnızca şu koşullar sağlanırsa devam edilir:
   - Issue açık olmalı ve PR olmamalı.
   - Numara ile URL ayarla birebir eşleşmeli.
   - Yazar, ayardaki `trustedAuthor` olmalı.
   - Başlık 256 ve açıklama 20.000 karakteri aşmamalı.
4. Gönderimden **önce** `chat-forward\records\<owner>__<repo>__<n>.json` kaydı `wx` ile atomik olarak oluşturulur. Gönderim en fazla bir kez yapılır: süreç bu noktada çökse bile issue yeniden gönderilmez.
5. Görev metni `chat-forward\prompts\...md` dosyasına yazılır. Metin issue URL'sini, başlığını, açıklamasını ve çalışma kurallarını içerir: ayrı branch, ilgili testler, issue'ya bağlı draft PR, merge/deploy yok. Ardından şu komut çalıştırılır:
   `code chat -m <mod> -r -a <prompt-dosyası> "Follow the HIC relay task in the attached file"`.
   Issue metni komut satırına girmez; Chat'e yalnızca ek dosya olarak ulaşır. Windows'ta `code.cmd`, `cmd.exe /d /s /c` ile çağrılır. Çağrıdaki her argüman güvenli karakter allowlist'iyle denetlenir.
6. Kayıt `handed-off-unverified`, `launch-failed`, `launch-timeout` veya `spawn-failed` olarak güncellenir. `completionVerified` her zaman `false` kalır.

### Kullanım

```powershell
$relay = 'tools\dev-handoff\zero-prompt-relay\scripts\Run-DevRelay.ps1'
powershell -NoProfile -File $relay -Mode ChatForwardSetup -Repository Cabatoy/AI-Agent-Lab-Project-Initialization-Architecture-Blueprint -IssueNumber <ISSUE_NO> -TrustedAuthor Cabatoy
powershell -NoProfile -File $relay -Mode ChatForwardOnce
```

## ChatForwardWatch modu (yeni)

### Nasıl çalışır

Watch modu, GitHub repo'sunu periyodik olarak polling yapar ve uygun issue'ları VS Code Chat'e iletir.

1. `ChatForwardWatchSetup`, `%LOCALAPPDATA%\HICDevRelay\watch-forward.json` dosyasını yazar. Dosyada repo, `hic-relay-ready` etiketi, güvenilir issue yazarı ve (isteğe bağlı) `agentModeOptIn` boolean bayrağı yer alır. `-ChatMode agent` ile Agent modunu opt-in edebilirsin; varsayılan Ask modudur. Bu adım ağ çağrısı yapmaz.
2. `ChatForwardWatchOnce`, GitHub API'sine `https://api.github.com/repos/<repo>/issues?state=open&labels=<label>` ile GET yapar. Sonuçlar filtre edilir:
   - PR'lar atlanır.
   - Yalnızca açık issue'lar seçilir.
   - Yalnızca `trustedAuthor` tarafından yazılan issue'lar seçilir.
3. Her uygun issue için `watch-forward\issues.json` veritabanı kontrol edilir. Önceden gönderilen issue'lar (status: `forwarded`, `handed-off-unverified`, `launch-failed`) atlanır.
4. Kalan her issue için `ChatForwardOnce` gibi kayıt oluşturulur ve VS Code Chat'e iletilir. Gönderimler sıra ile yapılır (paralel değil).
5. Yeniden başlatmada watch modu tekrar gönderimden kaçınır. Veritabanı kalıcıdır ve yeniden başlatma arasında korunur.

### Modu seç

- **ChatForwardWatchOnce**: Bir kez polling, uygun issue'ları ilet, exit.
- **ChatForwardWatchLoop**: 60 saniye interval'le sürekli polling, Ctrl+C ile gracefully kapat.

### Kullanım

Önce config oluştur (bir kez):

```powershell
$relay = 'tools\dev-handoff\zero-prompt-relay\scripts\Run-DevRelay.ps1'
# Ask modu (varsayılan):
powershell -NoProfile -File $relay -Mode ChatForwardWatchSetup `
  -Repository Cabatoy/AI-Agent-Lab-Project-Initialization-Architecture-Blueprint `
  -RelayLabel hic-relay-ready `
  -TrustedAuthor Cabatoy

# Veya Agent modunu opt-in et:
powershell -NoProfile -File $relay -Mode ChatForwardWatchSetup `
  -Repository Cabatoy/AI-Agent-Lab-Project-Initialization-Architecture-Blueprint `
  -RelayLabel hic-relay-ready `
  -TrustedAuthor Cabatoy `
  -ChatMode agent
```

**Seçenek 1: Bir kez polling (ChatForwardWatchOnce)**

```powershell
powershell -NoProfile -File $relay -Mode ChatForwardWatchOnce
```

`exit=0` döner, uygun issue'lar iletilir, sonra exit eder.

**Seçenek 2: Sürekli polling (ChatForwardWatchLoop)**

```powershell
powershell -NoProfile -File $relay -Mode ChatForwardWatchLoop
```

- 60 saniye interval'le polling başlar.
- Yeni uygun issue'lar bulunca otomatik iletilir (ikinci komut gerektirmez).
- Transient GitHub hataları (rate limit, network) otomatik retry edilir.
- `Ctrl+C` ile gracefully kapatılır.

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
- `-r`, son etkin VS Code penceresini kullanır. Chat o pencerenin workspace'inde çalışır. Hedef repoyu açık tut.
- Özel repolar anonim okunamaz.
- VS Code'un `code chat` komutunun promptu Enter beklemeden gönderdiği, 2026-11-13'te VS Code 1.146.0 üzerinde kullanıcı tarafından gözlendi. Bu davranış sonraki sürümlerde değişebilir.

## Offline testler

`npm run check` komutu testleri çalıştırır. Watch modunun testleri de bu çıktıda yer alır. Gerçek alt süreç olarak sahte komutlar kullanılır. Yeniden başlatma iki ayrı Node süreciyle sınanır. Testler gerçek VS Code'u açmaz ve ağa çıkmaz.
