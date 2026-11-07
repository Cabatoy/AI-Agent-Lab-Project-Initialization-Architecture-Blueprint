# Yeni Checkout ve Local İnceleme

Bu rehber yeni indirilen checkout'ta dosya incelemesinden Copilot handoff'una, oradan da kullanıcı onaylı Podman kontrol/onarım adımlarına kadar olan sırayı tanımlar. `run.me` ve `run.me.ps1` ilk iki adımda yalnızca Chat mesajı gösterir; `approve-podman` onayından sonra salt okunur Podman durum kontrolünü kendisi çalıştırır. Sorun bulunursa ve kullanıcı `approve-setup` ile açıkça onay verirse yalnızca `new-build` onarımını tetikler. Chat'e gönderilecek metinler `tools/run-me/prompts.tr.json` dosyasında tutulur.

## IDE'de Açma

- **VS Code:** Depo kökünü **File > Open Folder** ile açın. `.github/copilot-instructions.md` repo talimatı olarak yüklenir; yeni sohbette ayrıca `docs-md/continuation.md` dosyasını okutun.
- **Visual Studio:** Repo kökündeki `HumanInControl.sln` dosyasını açın. Solution Control Plane API, Agent Worker, Native Host, Agent Bridge, `run.me` ve test projelerini içerir. Copilot talimatlarının otomatik yüklenmesine güvenmeyin; Chat'ten `.github/copilot-instructions.md` ve `docs-md/continuation.md` dosyalarını açıkça okumasını isteyin.

## Önkoşul ve Başlatma

Rehberi çalıştırmak için .NET 10 SDK gerekir. Podman bu ilk iki adım için gerekli değildir.

Windows PowerShell, depo kökünden:

```powershell
./run.me.ps1
```

macOS veya Linux, depo kökünden:

```bash
bash ./run.me
```

## Sıralı Akış

1. **Dosyaları denetle:** Rehberde `repo review` komutunu çalıştırıp Chat mesajını kopyalayın. Bu adımda Podman, build, ağ isteği, kurulum veya dosya değişikliği yapılmaz. Copilot sonucu bildirdiğinde `done` yazın.
2. **Sohbeti kaldığı yerden sürdür:** Rehberde `handoff check` komutunu çalıştırıp Chat mesajını gönderin. Handoff öneridir; onaylanmış karar değildir. Dosya değişikliği veya Podman işlemi yapılmaz. Sonucu gördükten sonra `done` yazın.
3. **Podman incelemesine izin ver:** Rehber `approve-podman` yazmanızı ister. Bu onay yalnızca durum komutlarının gösterilmesine/çalıştırılmasına izin verir; machine başlatma, ayar değiştirme, build, container başlatma, volume silme veya prune izni değildir. İzin vermiyorsanız `quit` veya `Ctrl+C` ile çıkın.
4. **Salt okunur durumu kontrol et:** `approve-podman` onayından sonra rehber salt okunur Podman kontrollerini kendisi çalıştırır ve sonucu raporlar (`podman --version`, `podman machine list`, `podman compose version`, `podman compose ps`). Machine durmuşsa başlatmadan raporlayıp durur.
5. **Sonucu değerlendir ve karar ver:** Sorun yoksa akışı `done` ile tamamlayın. Sorun varsa run.me konsolunda `approve-setup` yazarak yalnızca gerekli setup/onarım komutlarına ayrı açık onay verin.
6. **Onaylı setup/onarım uygula:** `approve-setup` sonrası rehber yalnızca repository scriptleriyle `new-build` onarımını başlatır (`./scripts/local.ps1 new-build` veya `./scripts/local.sh new-build`). Windows'ta user-mode networking değişikliği gerekirse etkiyi ayrıca açıklatın ve ayrı açık onay verin; onaysız ağ ayarı değişikliği yaptırmayın.
7. **Sonuçları konuşun:** Setup/onarım sonucunu Copilot'tan özetletin. `rebuild-all` veya `delete-all` gibi veri silen işlemlere geçmeden önce kapsamı ayrıca onaylayın.

## Rehber Komutları

| Komut | İşlev |
|---|---|
| `help` veya `?` | Tüm komutları gösterir |
| `status` | Adım ilerlemesini gösterir |
| `step <n>` | Listedeki istenen adımı doğrudan çağırır (`step 1` ... `step 6`) |
| `repo review` | Proje dosya inceleme Chat mesajını gösterir |
| `handoff check` | Handoff karşılaştırma Chat mesajını gösterir |
| `podman check` | Salt okunur Podman kontrol Chat mesajını gösterir |
| `setup repair` | Setup/onarım Chat mesajını gösterir |
| `done` | Dosya/sohbet adımını tamamlayıp ilerler |
| `approve-podman` | Yalnızca salt okunur Podman durum kontrolüne izin verir |
| `approve-setup` | Sorun varsa setup/onarım komutlarına ayrı açık onay verir |
| `reset` | Onay alarak yalnızca rehber ilerlemesini sıfırlar |
| `quit` veya `exit` | Kaydedilmiş ilerlemeyle çıkar |
| `Ctrl+C` | Güvenli biçimde çıkar; ilerleme korunur |

İlerleme depo kökündeki `.run-me-state.json` dosyasına kaydedilir ve Git tarafından yok sayılır. Başka makineye veya yeni clone'a taşınmaz; sohbet hafızası değildir. `reset`, Podman container'larına, volume'lere veya task kayıtlarına dokunmaz.

## Konsol Çıktısı: Adım ve Cevap Örneği

Aşağıdaki kısa örnek, `run.me` içindeki seçim adımlarını ve Chat'e gönderilen komutlara alınan yanıt türünü gösterir:

1. Konsolda `repo review` yazılır.
   - **Chat'e gönderilen komut:** `repo review`
   - **Beklenen cevap özeti:** Repo yapısı, solution/proje listesi ve handoff ile tutarlılık kontrolü.
2. Konsolda `done` yazılır.
3. Konsolda `handoff check` yazılır.
   - **Chat'e gönderilen komut:** `handoff check`
   - **Beklenen cevap özeti:** `docs-md/continuation.md` ile mevcut dosyaların karşılaştırması, doğrulanan ve doğrulanmayan noktalar.
4. Konsolda `approve-podman` yazılır.
5. Konsolda `podman check` yazılır.
   - **Chat'e gönderilen komut:** `podman check`
   - **Beklenen cevap özeti:** `podman --version`, `podman machine list`, `podman compose version`, `podman compose ps` çıktılarının raporu.
6. Sorun varsa konsolda `approve-setup`, ardından `setup repair` yazılır.
   - **Chat'e gönderilen komut:** `setup repair`
   - **Beklenen cevap özeti:** Yalnızca `scripts/local.ps1 new-build` / `scripts/local.sh new-build` sonucu.

## Konsol Çalıştırmadan Doğrudan Chat Kullanımı

`run.me` açmadan da aynı komutları doğrudan Chat mesajı olarak yazabilirsiniz:

- `repo review`
- `handoff check`
- `podman check`
- `setup repair`

Bu kullanımda da aynı onay kapıları geçerlidir:

- `podman check` öncesi açık Podman onayı,
- `setup repair` öncesi ayrı setup/onarım onayı,
- veri silen komutlar için (`rebuild-all`, `delete-all`) ayrıca açık onay.

## Local Task Smoke Test

Podman stack'ini başlatma ve task API akışını deneme bu ilk inceleme rehberinin dışındadır. Kullanıcı bu adıma ayrıca karar verirse [README'deki local Podman](../README.md#local-podman-kurulumu) ve task API adımlarını izleyin. Veri koruyan başlangıç için `./scripts/local.ps1 bootstrap-dev` (macOS/Linux: `./scripts/local.sh bootstrap-dev`) komutunu kullanın; temiz başlangıç için sırasıyla `-Reset`/`--reset` eklenebilir. Etkileşimli terminalde `-LlmProvider`/`--llm-provider` verilmezse servisler başladıktan sonra provider seçimi sorulur. `podman-ai-lab` seçildiğinde çalışan AI Lab model service container'ları arasından seçim yapılır; birden fazla servis varken `auto` seçimi de servis seçimini açar. Worker kuyruk/durum akışını sürdürür; opsiyonel model provider ayarları (`MODEL_PROVIDER`, `MODEL_ENDPOINT`, `MODEL_NAME`) verilirse task açıklamasından prompt üretip model sonucunu `result` alanına yazar. `new-build`/`rebuild-all`/`bootstrap-dev` seçilen Podman AI Lab container'ını compose ağına `ai-lab` alias'ıyla bağlar; `.env` içinde `MODEL_ENDPOINT=http://ai-lab:8000` kullanılır. Smoke test sonunda script, `.env` ve `agentlab-worker` runtime model ayarlarını (provider/endpoint/model) da çıktıda özetler.

## Örnek run.me Oturumu

Aşağıdaki örnek yalnızca rehber komut akışını gösterir:

1. `repo review` → Chat mesajını kopyala ve gönder
2. Copilot yanıtını oku, konsola dönüp `done` yaz
3. `handoff check` → ikinci mesajı gönder
4. Copilot yanıtı sonrası `done`
5. Podman kontrolü istenirse `approve-podman` (salt okunur kontroller otomatik çalışır)
6. Sorun yoksa `done`; sorun varsa `approve-setup` (onarım otomatik başlatılır)

Not: `done`, `approve-podman` ve `approve-setup` komutlarını Chat'e değil, `run.me` konsoluna yazın.