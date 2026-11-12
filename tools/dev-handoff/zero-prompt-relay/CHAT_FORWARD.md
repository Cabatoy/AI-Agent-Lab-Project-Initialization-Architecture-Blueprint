# VS Code Chat'e tek issue iletme (opt-in)

**Yalnızca geliştirici iş istasyonu içindir; HIC ürün özelliği değildir.** Bu mod, yerel ayarda açıkça seçilen **tek bir GitHub issue'sunu** okur ve VS Code'un `code chat` girişiyle Chat'e iletir. Mevcut ack relay'ini, `HIC-Developer-Relay-Ack-OptIn` görevini ve `AckWatch` davranışını değiştirmez. Varsayılan olarak kapalıdır.

## Nasıl çalışır

1. `ChatForwardSetup`, `%LOCALAPPDATA%\HICDevRelay\chat-forward.json` dosyasını yazar. Dosyada repo, issue numarası, güvenilir issue yazarı, `code.cmd` yolu ve Chat modu yer alır. Varsayılan mod `agent`dır. Bu adım ağ çağrısı yapmaz. Mevcut ayarın üzerine yalnızca `-ReplaceConfig` ile yazılır.
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

## Tamamlanma kanıtı değildir

`code chat` komutunun `exit=0` dönmesi yalnızca VS Code'a teslim edildiğini gösterir. İşin tamamlandığının kanıtı issue'ya bağlı draft PR ve issue yorumudur. Gözlemci bunu GitHub'da kontrol eder. Hatalı veya zaman aşımına uğramış gönderimler de otomatik olarak tekrar denenmez. Yeniden denemek için operatör ilgili kaydı bilerek siler.

## Kullanım

HIC checkout'undaki bu klasörden, yeni bir issue için:

```powershell
$relay = 'tools\dev-handoff\zero-prompt-relay\scripts\Run-DevRelay.ps1'
powershell -NoProfile -File $relay -Mode ChatForwardSetup -Repository Cabatoy/AI-Agent-Lab-Project-Initialization-Architecture-Blueprint -IssueNumber <YENI_ISSUE> -TrustedAuthor Cabatoy
powershell -NoProfile -File $relay -Mode ChatForwardOnce
```

Başka bir issue'ya geçmek için `ChatForwardSetup` adımını `-ReplaceConfig` ile tekrar çalıştır. Eski issue'nun kaydı korunur; aynı issue yeniden seçilse bile tekrar gönderilmez.

## Sınırlar

- İlk sürümde izleme döngüsü ve Task Scheduler entegrasyonu yoktur; yalnızca `Once` modu vardır.
- `-r`, son etkin VS Code penceresini kullanır. Chat o pencerenin workspace'inde çalışır. Hedef repoyu açık tut.
- Özel repolar anonim okunamaz. Bu durumda mod `github-read-failed` ile durur.
- VS Code'un `code chat` komutunun promptu Enter beklemeden gönderdiği, 2026-11-13'te VS Code 1.146.0 üzerinde kullanıcı tarafından gözlendi. Bu davranış sonraki sürümlerde değişebilir.

## Offline testler

`npm run check` komutu `test/chat-forward.test.mjs` testlerini de çalıştırır. Gerçek alt süreç olarak sahte bir `code` komutu kullanılır: Windows'ta `.cmd`, Linux'ta shell script. Yeniden başlatma iki ayrı Node süreciyle sınanır. Testler gerçek VS Code'u açmaz ve ağa çıkmaz.
